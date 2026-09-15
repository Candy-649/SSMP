using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SSMP.Networking.Packet.Data;
using SSMP.Ui;
using SSMP.Util;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Checking two-player saves: once both players have loaded their paired saves, each backs up its save file and sends
/// the world progress of its save, and each adds the progress of the other that it lacks.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The embedded list of saved objects of the world.
    /// </summary>
    private const string WorldItemsFilePath = "SSMP.Resource.coop-world-items.json";

    /// <summary>
    /// The folder in the config folder that backups of two-player saves go to.
    /// </summary>
    private const string BackupFolderName = "coop-backups";

    /// <summary>
    /// How many backups each two-player save keeps.
    /// </summary>
    private const int MaxBackups = 10;

    /// <summary>
    /// How many records and saved objects go in one part of the world progress.
    /// </summary>
    private const int EntriesPerPart = 64;

    /// <summary>
    /// The value of a hello that starts a check, rather than one that answers the hello of the other player.
    /// </summary>
    private const ushort HelloStart = 0;

    /// <summary>
    /// The value of a hello that answers the hello of the other player.
    /// </summary>
    private const ushort HelloAnswer = 1;

    /// <summary>
    /// The boolean fields of the player data that can be defeat or encounter records.
    /// </summary>
    private static FieldInfo[]? _recordFields;

    /// <summary>
    /// The field of a collection of saved objects that holds them by scene.
    /// </summary>
    private static FieldInfo? _persistentScenesField;

    /// <summary>
    /// The saved booleans of the world, as keys from <see cref="GetItemKey"/>.
    /// </summary>
    private HashSet<string>? _worldBools;

    /// <summary>
    /// The player that the current check is with, or null.
    /// </summary>
    private ushort? _checkPartnerId;

    /// <summary>
    /// Whether the local player said hello to the partner in this check.
    /// </summary>
    private bool _helloSent;

    /// <summary>
    /// Whether the partner said hello, so they have loaded the save that is paired with the local one.
    /// </summary>
    private bool _partnerHello;

    /// <summary>
    /// Whether the local save was backed up and its world progress sent.
    /// </summary>
    private bool _stateSent;

    /// <summary>
    /// Whether the world progress of the partner arrived and was added.
    /// </summary>
    private bool _stateReceived;

    /// <summary>
    /// The parts of the world progress of the partner that arrived, by part.
    /// </summary>
    private readonly Dictionary<ushort, CoopSaveUpdate> _stateParts = new();

    /// <summary>
    /// How many records and saved objects the last check added.
    /// </summary>
    private int _addedChanges;

    /// <summary>
    /// Forgets the progress of the current check.
    /// </summary>
    private void ResetCheck() {
        _checkPartnerId = null;
        _helloSent = false;
        _partnerHello = false;
        _stateSent = false;
        _stateReceived = false;
        _stateParts.Clear();
        _addedChanges = 0;
    }

    /// <summary>
    /// Moves the check with a connected partner along: say hello, then back up and send the world progress once the
    /// partner said hello, and finish once their progress was added.
    /// </summary>
    private void UpdateCheck(CoopSaveMarker marker, ClientPlayerData partner) {
        if (_checkPartnerId != partner.Id) {
            ResetCheck();
            _checkPartnerId = partner.Id;
        }

        if (!_helloSent) {
            SendHello(partner, marker, HelloStart);
        }

        if (_partnerHello && !_stateSent) {
            _stateSent = true;
            BackUpSave(_sessionSlot);
            SendWorldState(partner);
        }

        if (_stateSent && _stateReceived) {
            FinishCheck(marker, partner);
        }
    }

    private void SendHello(ClientPlayerData partner, CoopSaveMarker marker, ushort kind) {
        _helloSent = true;
        Send(new CoopSaveUpdate {
            TargetId = partner.Id,
            Kind = CoopSaveUpdateKind.Hello,
            PartnerKey = marker.PartnerKey,
            PartCount = kind
        });
    }

    /// <summary>
    /// The partner loaded a save. A hello that starts a check starts the check over, because the partner loaded their
    /// save again, and gets an answer.
    /// </summary>
    private void OnHello(ClientPlayerData player, CoopSaveUpdate update) {
        var marker = GetCurrentMarker();
        if (marker == null || player.SaveKey.Length == 0 || player.SaveKey != marker.PartnerKey) {
            return;
        }

        if (update.PartnerKey != LocalKey) {
            if (update.PartCount == HelloStart) {
                UiManager.InternalChatBox.AddMessage(
                    $"{player.Username} loaded a save that isn't paired with yours. Your two-player save waits " +
                    "for them to load the paired save, or to pair again with /coopsave."
                );
            }

            return;
        }

        if (update.PartCount == HelloStart) {
            if (_checkedWith == player.Id) {
                _checkedWith = null;
            }

            ResetCheck();
            _checkPartnerId = player.Id;
            _partnerHello = true;
            SendHello(player, marker, HelloAnswer);
            return;
        }

        if (_checkPartnerId == player.Id || _checkPartnerId == null) {
            _checkPartnerId = player.Id;
            _partnerHello = true;
        }
    }

    /// <summary>
    /// A part of the world progress of the partner arrived. Once all parts are in, the progress is added.
    /// </summary>
    private void OnWorldState(ClientPlayerData player, CoopSaveUpdate update) {
        if (_checkPartnerId != player.Id || _stateReceived || GetCurrentMarker() is not { } marker ||
            player.SaveKey != marker.PartnerKey) {
            return;
        }

        _stateParts[update.Part] = update;
        if (_stateParts.Count < update.PartCount) {
            return;
        }

        _stateReceived = true;
        AddWorldState(player);
    }

    /// <summary>
    /// Finishes a check: remembers it and lets the local player move.
    /// </summary>
    private void FinishCheck(CoopSaveMarker marker, ClientPlayerData partner) {
        _checkedWith = partner.Id;
        _everChecked = true;

        marker.PartnerName = partner.Username;
        marker.LastCheckUtc = DateTime.UtcNow;
        SaveMarkers();

        Logger.Info($"Checked two-player save with {partner.Username}, {_addedChanges} changes added");
        UiManager.InternalChatBox.AddMessage(
            _addedChanges == 0
                ? $"Two-player save with {partner.Username}: backed up, and your worlds match."
                : $"Two-player save with {partner.Username}: backed up, and {_addedChanges} changes from their " +
                  "world were added to yours. Changes in the room you are in show once you enter it again."
        );
    }

    #region World progress

    /// <summary>
    /// Sends the defeat and encounter records and the saved objects of the world that are set in the local save.
    /// </summary>
    private void SendWorldState(ClientPlayerData partner) {
        var records = GetRecords();
        var items = GetWorldItems();
        var partCount = System.Math.Max(1, (records.Count + items.Count + EntriesPerPart - 1) / EntriesPerPart);

        var recordIndex = 0;
        var itemIndex = 0;
        for (var part = 0; part < partCount; part++) {
            var update = new CoopSaveUpdate {
                TargetId = partner.Id,
                Kind = CoopSaveUpdateKind.WorldState,
                Part = (ushort) part,
                PartCount = (ushort) partCount
            };

            for (var entries = 0; entries < EntriesPerPart; entries++) {
                if (recordIndex < records.Count) {
                    update.Records.Add(records[recordIndex++]);
                } else if (itemIndex < items.Count) {
                    update.ItemScenes.Add(items[itemIndex].Scene);
                    update.ItemIds.Add(items[itemIndex].Id);
                    itemIndex++;
                } else {
                    break;
                }
            }

            Send(update);
        }

        Logger.Info(
            $"Sent world progress to {partner.Username}: {records.Count} records and {items.Count} saved objects " +
            $"in {partCount} parts"
        );
    }

    /// <summary>
    /// Adds the records and saved objects of the world from the partner that the local save lacks. Only what the local
    /// player also counts as world progress is added.
    /// </summary>
    private void AddWorldState(ClientPlayerData partner) {
        var playerData = PlayerData.instance;
        var sceneData = SceneData.instance;
        if (playerData == null || sceneData == null) {
            return;
        }

        var recordNames = new HashSet<string>(GetRecordFields().Select(field => field.Name));
        var worldBools = GetWorldBools();
        var loadedScenes = GetLoadedSceneNames();
        var loadedItems = new HashSet<string>(StringComparer.Ordinal);
        var records = 0;
        var items = 0;

        foreach (var part in _stateParts.OrderBy(pair => pair.Key).Select(pair => pair.Value)) {
            foreach (var name in part.Records) {
                if (!recordNames.Contains(name) || playerData.GetBool(name)) {
                    continue;
                }

                playerData.SetBool(name, true);
                records++;
            }

            for (var i = 0; i < part.ItemIds.Count && i < part.ItemScenes.Count; i++) {
                var scene = part.ItemScenes[i];
                var id = part.ItemIds[i];
                var key = GetItemKey(scene, id);
                if (!worldBools.Contains(key) ||
                    (sceneData.PersistentBools.TryGetValue(scene, id, out var existing) && existing.Value)) {
                    continue;
                }

                sceneData.PersistentBools.SetValue(new PersistentItemData<bool> {
                    ID = id,
                    SceneName = scene,
                    Value = true,
                    IsSemiPersistent = false
                });
                items++;

                if (loadedScenes.Contains(scene.ToLowerInvariant())) {
                    loadedItems.Add(key);
                }
            }
        }

        OverrideLoadedItems(loadedItems);

        _addedChanges = records + items;
        Logger.Info($"Added world progress of {partner.Username}: {records} records and {items} saved objects");
    }

    /// <summary>
    /// The names of the loaded scenes in lower case.
    /// </summary>
    private static HashSet<string> GetLoadedSceneNames() {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++) {
            names.Add(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).name.ToLowerInvariant());
        }

        return names;
    }

    /// <summary>
    /// Makes the saved objects of the loaded scenes that the partner changed keep that change. They still look the old
    /// way until the player enters the scene again, and without this they would save the old way when the player leaves.
    /// </summary>
    private static void OverrideLoadedItems(HashSet<string> keys) {
        if (keys.Count == 0) {
            return;
        }

        foreach (var item in UnityEngine.Object.FindObjectsByType<PersistentBoolItem>(
                     UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None
                 )) {
            var data = item.ItemData;
            var id = string.IsNullOrEmpty(data?.ID) ? item.gameObject.name : data!.ID;
            var scene = string.IsNullOrEmpty(data?.SceneName) ? item.gameObject.scene.name : data!.SceneName;
            if (keys.Contains(GetItemKey(scene, id))) {
                item.SetValueOverride(true);
            }
        }
    }

    /// <summary>
    /// The defeat and encounter records that are set in the local save.
    /// </summary>
    private static List<string> GetRecords() {
        var playerData = PlayerData.instance;
        return playerData == null
            ? []
            : GetRecordFields()
                .Where(field => field.GetValue(playerData) is true)
                .Select(field => field.Name)
                .ToList();
    }

    /// <summary>
    /// The boolean fields of the player data that are defeat or encounter records, which boss rooms share too.
    /// </summary>
    private static FieldInfo[] GetRecordFields() {
        return _recordFields ??= typeof(PlayerData)
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .Where(field => field.FieldType == typeof(bool) && BossRoomCoop.IsSharedRecordName(field.Name) &&
                            !BossRoomCoop.IsHeroStateName(field.Name))
            .ToArray();
    }

    /// <summary>
    /// The saved objects of the world that are set in the local save.
    /// </summary>
    private List<(string Scene, string Id)> GetWorldItems() {
        var result = new List<(string Scene, string Id)>();
        var worldBools = GetWorldBools();
        var collection = SceneData.instance?.PersistentBools;
        if (collection == null || worldBools.Count == 0) {
            return result;
        }

        _persistentScenesField ??= collection.GetType().GetField("scenes", InstanceFlags);
        if (_persistentScenesField?.GetValue(collection) is not Dictionary<string, Dictionary<string, PersistentItemData<bool>>> scenes) {
            Logger.Warn("Could not read the saved objects of the save");
            return result;
        }

        foreach (var scene in scenes) {
            foreach (var item in scene.Value.Values) {
                if (item.Value && !item.IsSemiPersistent && worldBools.Contains(GetItemKey(scene.Key, item.ID))) {
                    result.Add((scene.Key, item.ID));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The saved booleans of the world, loaded from the embedded list when first needed.
    /// </summary>
    private HashSet<string> GetWorldBools() {
        if (_worldBools != null) {
            return _worldBools;
        }

        _worldBools = new HashSet<string>(StringComparer.Ordinal);
        var worldItems = FileUtil.LoadObjectFromEmbeddedJson<CoopWorldItems>(WorldItemsFilePath);
        if (worldItems == null) {
            Logger.Warn("Could not load the saved objects of the world for two-player saves");
            return _worldBools;
        }

        foreach (var scene in worldItems.Bools) {
            foreach (var id in scene.Value) {
                _worldBools.Add(GetItemKey(scene.Key, id));
            }
        }

        return _worldBools;
    }

    /// <summary>
    /// The key of a saved object: the scene in lower case, because the list comes from bundle names, and the ID.
    /// </summary>
    private static string GetItemKey(string scene, string id) => scene.ToLowerInvariant() + "\n" + id;

    #endregion

    #region Backups

    /// <summary>
    /// Copies the save file of a slot to the backups of two-player saves, keeping the newest backups.
    /// </summary>
    private static void BackUpSave(int slot) {
        try {
            var path = GetSaveFilePath(slot);
            if (path == null || !File.Exists(path)) {
                Logger.Warn($"Could not find the save file of slot {slot} to back up");
                return;
            }

            var folder = Path.Combine(FileUtil.GetConfigPath(), BackupFolderName, GetSaveFolderName(), $"slot{slot}");
            Directory.CreateDirectory(folder);
            var backup = Path.Combine(
                folder,
                $"{Path.GetFileNameWithoutExtension(path)}_{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(path)}"
            );
            File.Copy(path, backup, true);
            Logger.Info($"Backed up two-player save to {backup}");

            foreach (var old in new DirectoryInfo(folder).GetFiles()
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Skip(MaxBackups)) {
                old.Delete();
            }
        } catch (Exception e) {
            Logger.Error($"Could not back up the save file of slot {slot}:\n{e}");
        }
    }

    /// <summary>
    /// The path of the save file of a slot, or null if the platform doesn't say.
    /// </summary>
    private static string? GetSaveFilePath(int slot) {
        var platform = Platform.Current;
        var method = platform?.GetType().GetMethod("GetSaveSlotPath", InstanceFlags);
        var parameters = method?.GetParameters();
        if (platform == null || method == null || parameters is not { Length: 2 } ||
            parameters[0].ParameterType != typeof(int)) {
            return null;
        }

        // The first file name usage is the save file itself rather than one of its backups
        var usage = parameters[1].ParameterType.IsEnum ? Enum.ToObject(parameters[1].ParameterType, 0) : null;
        return method.Invoke(platform, [slot, usage]) as string;
    }

    #endregion
}
