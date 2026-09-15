using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Checking two-player saves: once both players have loaded their paired saves, each backs up its save file and sends
/// the bosses it has beaten, the saved objects of the world that it changed and its wish log. Each save gets the changed
/// objects and accepted wishes that it lacks, and hears whether the beaten bosses and completed wishes differ, which are
/// never copied because they come with rewards.
///
/// Every check has a key. A player who starts a check picks a key larger than every key they have seen from the
/// partner, so a newer check always has a larger key, and the hellos, world progress and leaves of older checks can be
/// told apart and dropped even when they arrive late. When both players start a check at once, the larger key goes
/// ahead: the player with the smaller key takes the other key, and a player who gets a start with a smaller key than
/// their unfinished check sends their start again.
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
    /// How many beaten bosses and saved objects go in one part of the world progress.
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
    /// The boolean fields of the player data that record a beaten boss.
    /// </summary>
    private static FieldInfo[]? _defeatFields;

    /// <summary>
    /// The names of the boolean fields of the player data.
    /// </summary>
    private static HashSet<string>? _playerDataBoolNames;

    /// <summary>
    /// The field of a collection of saved objects that holds them by scene.
    /// </summary>
    private static FieldInfo? _persistentScenesField;

    /// <summary>
    /// The saved booleans of the world, as keys from <see cref="GetItemKey"/>.
    /// </summary>
    private HashSet<string>? _worldBools;

    /// <summary>
    /// The booleans of the player data that saved objects of the world set together with their own state, by the key
    /// of the object.
    /// </summary>
    private Dictionary<string, List<string>>? _worldPlayerData;

    /// <summary>
    /// The player that the current check is with, or null.
    /// </summary>
    private ushort? _checkPartnerId;

    /// <summary>
    /// The key of the current check, or 0 if no check started.
    /// </summary>
    private ulong _checkKey;

    /// <summary>
    /// The largest check key that the local player picked or got from the partner since the partner connected.
    /// </summary>
    private ulong _highestCheckKey;

    /// <summary>
    /// Whether the partner said hello for the current check, so they have loaded the save that is paired with the local
    /// one.
    /// </summary>
    private bool _partnerHello;

    /// <summary>
    /// Whether the local save was backed up and its world progress sent.
    /// </summary>
    private bool _stateSent;

    /// <summary>
    /// Whether all parts of the world progress of the partner arrived.
    /// </summary>
    private bool _stateReceived;

    /// <summary>
    /// Whether the world progress of the partner was added, which waits until the local save sent its own, so that both
    /// games compare what both saves sent.
    /// </summary>
    private bool _stateAdded;

    /// <summary>
    /// The parts of the world progress of the partner that arrived for the current check, by part.
    /// </summary>
    private readonly Dictionary<ushort, CoopSaveUpdate> _stateParts = new();

    /// <summary>
    /// The saved objects of the world that the local save sent for the current check.
    /// </summary>
    private List<(string Scene, string Id)> _sentWorldItems = [];

    /// <summary>
    /// How many saved objects the last check added.
    /// </summary>
    private int _addedChanges;

    /// <summary>
    /// How many bosses only the partner's save has beaten, and how many only the local save has.
    /// </summary>
    private int _onlyPartnerDefeats;

    private int _onlyLocalDefeats;

    /// <summary>
    /// Forgets the current check. The largest key stays, so the next check gets a larger one.
    /// </summary>
    private void ResetCheck() {
        _checkPartnerId = null;
        _checkKey = 0;
        _partnerHello = false;
        _stateSent = false;
        _stateReceived = false;
        _stateAdded = false;
        _stateParts.Clear();
        _sentWorldItems = [];
        _sentWishEntries = [];
        _mergedWishKeys.Clear();
        _differentWishNames.Clear();
        _agreedStoryValues = null;
        ResetWishProgress();
        _addedChanges = 0;
        _onlyPartnerDefeats = 0;
        _onlyLocalDefeats = 0;
        _differentWishes = 0;
        _differentStoryFlags = 0;
        _storyFlagsFromPartner = false;
        _checkPlayTime = null;
        _flagSequences.Clear();
        _wishSequences.Clear();
    }

    /// <summary>
    /// Moves the check with a connected partner along: start a check, then back up and send the world progress once
    /// the partner said hello for it, and finish once their progress was added.
    /// </summary>
    private void UpdateCheck(CoopSaveMarker marker, ClientPlayerData partner) {
        if (_checkPartnerId != partner.Id) {
            ResetCheck();
            _checkPartnerId = partner.Id;
        }

        if (_checkKey == 0) {
            _checkKey = NewCheckKey();
            SendHello(partner, marker, HelloStart);
        }

        if (_partnerHello && !_stateSent) {
            _stateSent = true;
            BackUpSave(_sessionSlot);
            SendWorldState(partner);
        }

        if (_stateSent && _stateReceived) {
            if (!_stateAdded) {
                _stateAdded = true;
                AddWorldState(partner);
            }

            FinishCheck(marker, partner);
        }
    }

    /// <summary>
    /// A key for a new check that is larger than every key seen so far. The lower bits are random, so two checks that
    /// start at once almost never get the same key; if they do, both players take them as the same check, which works
    /// too.
    /// </summary>
    private ulong NewCheckKey() {
        var key = (((_highestCheckKey >> 16) + 1) << 16) | (ulong) UnityEngine.Random.Range(1, 0x10000);
        _highestCheckKey = key;
        return key;
    }

    private void SendHello(ClientPlayerData partner, CoopSaveMarker marker, ushort kind) {
        Send(new CoopSaveUpdate {
            TargetId = partner.Id,
            Kind = CoopSaveUpdateKind.Hello,
            Key = _checkKey,
            PartnerKey = marker.PartnerKey,
            PartCount = kind
        });
    }

    /// <summary>
    /// The partner said hello for a check of the loaded save.
    /// </summary>
    private void OnHello(ClientPlayerData player, CoopSaveUpdate update) {
        var marker = GetCurrentMarker();
        if (marker == null || !IsPartner(player, marker)) {
            return;
        }

        if (update.PartnerKey != LocalKey) {
            if (update.PartCount == HelloStart) {
                PartnerLeft(player.Id, null);
                Chat(
                    $"{player.Username} loaded a save that isn't paired with yours. Your two-player save waits for " +
                    "them to load the paired save, or to pair again with /coopsave."
                );
            }

            return;
        }

        _highestCheckKey = System.Math.Max(_highestCheckKey, update.Key);

        if (update.PartCount == HelloAnswer) {
            if (update.Key == _checkKey && _checkPartnerId == player.Id) {
                _partnerHello = true;
            }

            return;
        }

        if (update.Key == _checkKey && _checkPartnerId == player.Id) {
            // The same check, like when both players started one with the same key
            _partnerHello = true;
            SendHello(player, marker, HelloAnswer);
            return;
        }

        if (update.Key < _checkKey && _checkPartnerId == player.Id) {
            // An older check. If the check of the local player hasn't finished, the partner may not know it yet.
            if (_checkedWith != player.Id) {
                SendHello(player, marker, HelloStart);
            }

            return;
        }

        // A newer check of the partner, like after they loaded their save again
        if (_checkedWith == player.Id) {
            _checkedWith = null;
        }

        ResetCheck();
        _checkPartnerId = player.Id;
        _checkKey = update.Key;
        _partnerHello = true;
        SendHello(player, marker, HelloAnswer);
    }

    /// <summary>
    /// A part of the world progress of the partner arrived. Once all parts of the current check are in, the progress is
    /// added after the local save sent its own.
    /// </summary>
    private void OnWorldState(ClientPlayerData player, CoopSaveUpdate update) {
        if (_checkKey == 0 || update.Key != _checkKey || _checkPartnerId != player.Id || _stateReceived ||
            GetCurrentMarker() is not { } marker || !IsPartner(player, marker)) {
            return;
        }

        _stateParts[update.Part] = update;
        if (_stateParts.Count < update.PartCount) {
            return;
        }

        _stateReceived = true;
    }

    /// <summary>
    /// The partner left the two-player save, unless the leave is older than the current check.
    /// </summary>
    private void OnLeft(ClientPlayerData player, CoopSaveUpdate update) {
        if (_checkPartnerId == player.Id && _checkKey > update.Key) {
            return;
        }

        PartnerLeft(player.Id, "left your two-player save");
    }

    /// <summary>
    /// Finishes a check: remembers it and lets the local player move.
    /// </summary>
    private void FinishCheck(CoopSaveMarker marker, ClientPlayerData partner) {
        _checkedWith = partner.Id;
        _everChecked = true;
        ResetWorldChanges();
        AddKnownWorldItems();
        RememberWishes();
        RememberStoryFlags();
        SendPendingWishTurnIns(partner);

        marker.PartnerName = partner.Username;
        if (partner.SaveKey.Length > 0) {
            marker.PartnerKey = partner.SaveKey;
        }

        marker.LastCheckUtc = DateTime.UtcNow;
        marker.CheckedPlayTime = PlayerData.instance != null ? PlayerData.instance.playTime : marker.CheckedPlayTime;
        _nextCheckedPlayTimeSave = UnityEngine.Time.unscaledTime + CheckedPlayTimeSaveInterval;
        SaveMarkers();

        Logger.Info($"Checked two-player save with {partner.Username}, {_addedChanges} changes added");
        var message = _addedChanges == 0
            ? $"Two-player save with {partner.Username}: backed up, and your worlds match."
            : $"Two-player save with {partner.Username}: backed up, and {_addedChanges} changes from their world " +
              "were added to yours. Changes in the room you are in show once you enter it again.";
        if (_onlyPartnerDefeats > 0 || _onlyLocalDefeats > 0) {
            message += $" Your saves have beaten different bosses ({partner.Username} beat {_onlyPartnerDefeats} " +
                       $"that you haven't, you beat {_onlyLocalDefeats} that they haven't). Beaten bosses aren't " +
                       "copied, so nobody misses a reward.";
        }

        if (_differentWishes > 0) {
            var wishes = _differentWishes == 1 ? "1 wish is" : $"{_differentWishes} wishes are";
            message += $" {wishes} completed in only one of your saves. Completed wishes aren't copied, so nobody " +
                       "misses a reward.";
        }

        if (_differentStoryFlags > 0) {
            var longer = _checkPlayTimeSinceTogether
                ? "played longer since you last played together"
                : "played for longer";
            message += _storyFlagsFromPartner
                ? $" {_differentStoryFlags} story changes came from the save of {partner.Username}, which was {longer}."
                : $" {_differentStoryFlags} story changes went from your save to {partner.Username}, because yours was " +
                  $"{longer}.";
        }

        Chat(message);
    }

    #region World progress

    /// <summary>
    /// Sends the bosses that the local save has beaten, the saved objects of the world that are set in it, its wish log
    /// and its story flags.
    /// </summary>
    private void SendWorldState(ClientPlayerData partner) {
        var defeats = GetDefeatRecords();
        var items = GetWorldItems();
        var wishes = GetWishEntries();
        var storyFlags = GetStoryEntries();
        _sentWorldItems = items;
        _sentWishEntries = wishes;
        _agreedStoryValues = storyFlags.Select(entry => entry.Value).ToArray();
        var partCount = System.Math.Max(
            1, (defeats.Count + items.Count + wishes.Count + storyFlags.Count + EntriesPerPart - 1) / EntriesPerPart
        );

        var defeatIndex = 0;
        var itemIndex = 0;
        var wishIndex = 0;
        var storyIndex = 0;
        for (var part = 0; part < partCount; part++) {
            var update = new CoopSaveUpdate {
                TargetId = partner.Id,
                Kind = CoopSaveUpdateKind.WorldState,
                Key = _checkKey,
                Part = (ushort) part,
                PartCount = (ushort) partCount,
                PlayTime = GetCheckPlayTime()
            };

            for (var entries = 0; entries < EntriesPerPart; entries++) {
                if (defeatIndex < defeats.Count) {
                    update.Records.Add(defeats[defeatIndex++]);
                } else if (itemIndex < items.Count) {
                    update.ItemScenes.Add(items[itemIndex].Scene);
                    update.ItemIds.Add(items[itemIndex].Id);
                    itemIndex++;
                } else if (wishIndex < wishes.Count) {
                    update.WishNames.Add(wishes[wishIndex].Name);
                    update.WishValues.Add(wishes[wishIndex].Value);
                    wishIndex++;
                } else if (storyIndex < storyFlags.Count) {
                    update.FlagNames.Add(storyFlags[storyIndex].Name);
                    update.FlagValues.Add(storyFlags[storyIndex].Value);
                    storyIndex++;
                } else {
                    break;
                }
            }

            Send(update);
        }

        Logger.Info(
            $"Sent world progress to {partner.Username}: {defeats.Count} beaten bosses, {items.Count} saved objects, " +
            $"{wishes.Count} entries of the wish log and {storyFlags.Count} story flags in {partCount} parts"
        );
    }

    /// <summary>
    /// Adds the saved objects of the world from the partner that the local save lacks, with the player data that they
    /// set, and the wishes that the partner accepted, takes the story flags that differ from the save that was played for
    /// longer, and compares the beaten bosses and completed wishes. Only what the local player also counts as the world
    /// is added.
    /// </summary>
    private void AddWorldState(ClientPlayerData partner) {
        var playerData = PlayerData.instance;
        var sceneData = SceneData.instance;
        if (playerData == null || sceneData == null) {
            return;
        }

        var parts = _stateParts.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList();

        var partnerDefeats = new HashSet<string>(parts.SelectMany(part => part.Records));
        var localDefeats = new HashSet<string>(GetDefeatRecords());
        var onlyPartner = partnerDefeats.Where(name => !localDefeats.Contains(name)).ToList();
        var onlyLocal = localDefeats.Where(name => !partnerDefeats.Contains(name)).ToList();
        _onlyPartnerDefeats = onlyPartner.Count;
        _onlyLocalDefeats = onlyLocal.Count;
        if (onlyPartner.Count > 0 || onlyLocal.Count > 0) {
            Logger.Info(
                $"Beaten bosses differ from {partner.Username}. Only theirs: {string.Join(", ", onlyPartner)}; " +
                $"only local: {string.Join(", ", onlyLocal)}"
            );
        }

        var loadedScenes = GetLoadedSceneNames();
        var loadedItems = new HashSet<string>(StringComparer.Ordinal);
        var items = 0;
        var flags = 0;

        foreach (var part in parts) {
            for (var i = 0; i < part.ItemIds.Count && i < part.ItemScenes.Count; i++) {
                if (AddWorldItem(part.ItemScenes[i], part.ItemIds[i], loadedScenes, loadedItems, ref flags)) {
                    items++;
                }
            }
        }

        OverrideLoadedItems(loadedItems);
        var wishes = AddWishes(
            parts.SelectMany(part => part.WishNames.Zip(part.WishValues, (name, value) => (name, value)))
        );
        var storyFlags = AddStoryFlags(partner, parts);

        _addedChanges = items + wishes + storyFlags;
        Logger.Info(
            $"Added world progress of {partner.Username}: {items} saved objects, {flags} player data flags, " +
            $"{wishes} entries of the wish log and {storyFlags} story flags, with {_differentWishes} wishes completed " +
            $"in only one save and {_differentStoryFlags} story flags that differed"
        );
    }

    /// <summary>
    /// Adds a saved object of the world that the partner set to the local save, if the local save lacks it and also
    /// counts it as the world, with the player data that the object sets together with its own state.
    /// </summary>
    /// <param name="scene">The scene of the object.</param>
    /// <param name="id">The ID of the object.</param>
    /// <param name="loadedScenes">The names of the loaded scenes in lower case.</param>
    /// <param name="loadedItems">The keys of the added objects in loaded scenes, which this adds to.</param>
    /// <param name="flags">The number of player data flags that were set, which this adds to.</param>
    /// <returns>Whether the object was added.</returns>
    private bool AddWorldItem(
        string scene,
        string id,
        HashSet<string> loadedScenes,
        HashSet<string> loadedItems,
        ref int flags
    ) {
        var playerData = PlayerData.instance;
        var sceneData = SceneData.instance;
        var key = GetItemKey(scene, id);
        if (!GetWorldBools().Contains(key) ||
            (sceneData.PersistentBools.TryGetValue(scene, id, out var existing) && existing.Value)) {
            return false;
        }

        sceneData.PersistentBools.SetValue(new PersistentItemData<bool> {
            ID = id,
            SceneName = scene,
            Value = true,
            IsSemiPersistent = false
        });

        if (loadedScenes.Contains(scene.ToLowerInvariant())) {
            loadedItems.Add(key);
        }

        if (_worldPlayerData!.TryGetValue(key, out var names)) {
            var boolNames = GetPlayerDataBoolNames();
            foreach (var name in names) {
                if (boolNames.Contains(name) && !BossRoomCoop.IsHeroStateName(name) && !playerData.GetBool(name)) {
                    playerData.SetBool(name, true);
                    flags++;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a boolean of the player data records a beaten boss.
    /// </summary>
    private static bool IsDefeatRecord(string name) {
        return name.StartsWith("defeated", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("Defeated", StringComparison.Ordinal);
    }

    /// <summary>
    /// The bosses that the local save has beaten, by the names of their records.
    /// </summary>
    private static List<string> GetDefeatRecords() {
        var playerData = PlayerData.instance;
        if (playerData == null) {
            return [];
        }

        _defeatFields ??= typeof(PlayerData)
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .Where(field => field.FieldType == typeof(bool) && IsDefeatRecord(field.Name))
            .ToArray();
        return _defeatFields
            .Where(field => field.GetValue(playerData) is true)
            .Select(field => field.Name)
            .ToList();
    }

    private static HashSet<string> GetPlayerDataBoolNames() {
        return _playerDataBoolNames ??= new HashSet<string>(
            typeof(PlayerData)
                .GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Where(field => field.FieldType == typeof(bool))
                .Select(field => field.Name)
        );
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
        _worldPlayerData = new Dictionary<string, List<string>>(StringComparer.Ordinal);
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

        foreach (var scene in worldItems.PlayerData) {
            foreach (var item in scene.Value) {
                _worldPlayerData[GetItemKey(scene.Key, item.Key)] = item.Value;
            }
        }

        return _worldBools;
    }

    /// <summary>
    /// The key of a saved object: the scene in lower case, because the list comes from bundle names, and the ID.
    /// </summary>
    private static string GetItemKey(string scene, string id) => scene.ToLowerInvariant() + "\n" + id;

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
            GetItemSceneAndId(item, out var scene, out var id);
            if (keys.Contains(GetItemKey(scene, id))) {
                item.SetValueOverride(true);
            }
        }
    }

    /// <summary>
    /// Gets the scene and the ID that a saved object is saved under.
    /// </summary>
    private static void GetItemSceneAndId(PersistentBoolItem item, out string scene, out string id) {
        var data = item.ItemData;
        id = string.IsNullOrEmpty(data?.ID) ? item.gameObject.name : data!.ID;
        scene = string.IsNullOrEmpty(data?.SceneName) ? item.gameObject.scene.name : data!.SceneName;
    }

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
