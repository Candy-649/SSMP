using System;
using System.Collections.Generic;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Live changes of the world in a checked two-player save. When a saved object of the world gets set in the game of one
/// player, like a wall that breaks or a lever that opens, the save of the partner gets it at once, even when the partner
/// is in another room. <see cref="CoopHits"/> replays hits for a partner in the same room; this makes sure that the save
/// of the partner has the change either way.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How often the saved objects of the world in the loaded scenes are checked for changes, in seconds.
    /// </summary>
    private const float WorldChangeInterval = 0.5f;

    /// <summary>
    /// The saved objects of the world in the loaded scenes, with their scenes, IDs and keys.
    /// </summary>
    private readonly List<(PersistentBoolItem Item, string Scene, string Id, string Key)> _loadedWorldItems = [];

    /// <summary>
    /// The keys of the saved objects of the world that both saves are known to have set: those that the last check
    /// exchanged, and those that were sent or received since.
    /// </summary>
    private readonly HashSet<string> _knownWorldItems = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether scenes loaded or unloaded since the saved objects of the world in the loaded scenes were found.
    /// </summary>
    private bool _loadedWorldItemsDirty = true;

    /// <summary>
    /// Whether the save may have saved objects of the world set that the loaded objects don't show, like objects that
    /// save their change at once and objects of scenes that unloaded, so the save is compared with the known ones.
    /// </summary>
    private bool _savedWorldItemsDirty = true;

    /// <summary>
    /// The time at which the saved objects of the world are checked for changes next.
    /// </summary>
    private float _nextWorldChangeTime;

    /// <summary>
    /// The game manager whose saving of the objects of a level is watched.
    /// </summary>
    private global::GameManager? _watchedGameManager;

    /// <summary>
    /// Whether checking the world for changes threw, which is only logged once.
    /// </summary>
    private bool _worldChangeFailed;

    /// <summary>
    /// How many changes of the world, changes of the wish log and interactions the local game sent, for their sequences.
    /// </summary>
    private uint _changeCounter;

    /// <summary>
    /// The counts in the sequences of the last changes of flags of the player data from the partner that were applied,
    /// by name.
    /// </summary>
    private readonly Dictionary<string, uint> _flagSequences = new(StringComparer.Ordinal);

    /// <summary>
    /// The counts in the sequences of the last changes of wishes and rumours from the partner that were applied, by name.
    /// </summary>
    private readonly Dictionary<string, uint> _wishSequences = new(StringComparer.Ordinal);

    /// <summary>
    /// Forgets the saved objects of the world that were found and known to both saves, for a new check or session.
    /// </summary>
    private void ResetWorldChanges() {
        _loadedWorldItems.Clear();
        _knownWorldItems.Clear();
        _loadedWorldItemsDirty = true;
        _savedWorldItemsDirty = true;
        _nextWorldChangeTime = 0f;
    }

    /// <summary>
    /// The sequence of the next change of the world, change of the wish log or interaction that the local game sends:
    /// the current check in the upper half and a growing count in the lower half.
    /// </summary>
    private ulong NextChangeSequence() => ((_checkKey >> 16) << 32) | ++_changeCounter;

    /// <summary>
    /// Whether the change of a flag or wish in an update of the partner is newer than the last one that was applied for
    /// it, and if so remembers it as the last one. A change from another check, or one that the network delivered after
    /// a newer one, is old. Updates that weren't sent, like the changes that a check applies, have no sequence and
    /// count as new.
    /// </summary>
    private bool IsNewerChange(Dictionary<string, uint> sequences, CoopSaveUpdate update, string name) {
        if (update.Sequence == 0) {
            return true;
        }

        if (update.Sequence >> 32 != _checkKey >> 16) {
            return false;
        }

        var count = (uint) update.Sequence;
        if (sequences.TryGetValue(name, out var last) && last >= count) {
            return false;
        }

        sequences[name] = count;
        return true;
    }

    /// <summary>
    /// Remembers the saved objects of the world that both saves have after a check: those that the local save sent and
    /// those that the save of the partner sent. Everything else that is set gets sent as a change.
    /// </summary>
    private void AddKnownWorldItems() {
        foreach (var (scene, id) in _sentWorldItems) {
            _knownWorldItems.Add(GetItemKey(scene, id));
        }

        foreach (var part in _stateParts.Values) {
            for (var i = 0; i < part.ItemIds.Count && i < part.ItemScenes.Count; i++) {
                _knownWorldItems.Add(GetItemKey(part.ItemScenes[i], part.ItemIds[i]));
            }
        }
    }

    /// <summary>
    /// Makes the saved objects of the world be found again after a scene loaded.
    /// </summary>
    private void OnWorldSceneLoaded(Scene scene, LoadSceneMode mode) {
        _loadedWorldItemsDirty = true;
        _savedWorldItemsDirty = true;
    }

    /// <summary>
    /// Makes the saved objects of the world be found again after a scene unloaded.
    /// </summary>
    private void OnWorldSceneUnloaded(Scene scene) {
        _loadedWorldItemsDirty = true;
        _savedWorldItemsDirty = true;
    }

    /// <summary>
    /// Checks the world for changes right before the game saves the objects of the level it leaves, since those
    /// objects are gone once the next check would run.
    /// </summary>
    private void WatchLevelSaves(global::GameManager gameManager) {
        if (_watchedGameManager == gameManager) {
            return;
        }

        if (_watchedGameManager != null) {
            _watchedGameManager.SavePersistentObjects -= OnSavePersistentObjects;
        }

        _watchedGameManager = gameManager;
        gameManager.SavePersistentObjects += OnSavePersistentObjects;
    }

    /// <summary>
    /// Sends the changes of the world before the game saves the objects of the level.
    /// </summary>
    private void OnSavePersistentObjects() {
        if (_checkedWith is not { } partnerId || !_playerData.TryGetValue(partnerId, out var partner)) {
            return;
        }

        _savedWorldItemsDirty = true;
        _nextWorldChangeTime = 0f;
        UpdateWorldChanges(partner);
    }

    /// <summary>
    /// Sends the saved objects of the world that got set to the partner: those that the loaded objects show, and those
    /// that only the save shows.
    /// </summary>
    private void UpdateWorldChanges(ClientPlayerData partner) {
        if (Time.unscaledTime < _nextWorldChangeTime) {
            return;
        }

        _nextWorldChangeTime = Time.unscaledTime + WorldChangeInterval;

        try {
            if (_loadedWorldItemsDirty) {
                FindLoadedWorldItems();
            }

            CoopSaveUpdate? update = null;
            foreach (var (item, scene, id, key) in _loadedWorldItems) {
                if (item == null) {
                    // An object that is gone may have saved its change right before, which the save shows
                    _loadedWorldItemsDirty = true;
                    _savedWorldItemsDirty = true;
                    continue;
                }

                if (!_knownWorldItems.Contains(key) && item.GetCurrentValue()) {
                    AddWorldChange(ref update, partner, scene, id, key);
                }
            }

            if (_savedWorldItemsDirty) {
                _savedWorldItemsDirty = false;
                foreach (var (scene, id) in GetWorldItems()) {
                    var key = GetItemKey(scene, id);
                    if (!_knownWorldItems.Contains(key)) {
                        AddWorldChange(ref update, partner, scene, id, key);
                    }
                }
            }

            if (update != null) {
                Send(update);
                Logger.Info($"Sent {update.ItemIds.Count} changes of the world to {partner.Username}");
            }
        } catch (Exception e) {
            if (!_worldChangeFailed) {
                _worldChangeFailed = true;
                Logger.Error($"Could not send the changes of the world of the two-player save:\n{e}");
            }
        }
    }

    /// <summary>
    /// Adds a saved object of the world to the update with the changes for the partner and remembers it as known.
    /// </summary>
    private void AddWorldChange(ref CoopSaveUpdate? update, ClientPlayerData partner, string scene, string id, string key) {
        _knownWorldItems.Add(key);
        update ??= new CoopSaveUpdate { TargetId = partner.Id, Kind = CoopSaveUpdateKind.WorldChange };
        update.ItemScenes.Add(scene);
        update.ItemIds.Add(id);
    }

    /// <summary>
    /// Finds the saved objects of the world in the loaded scenes.
    /// </summary>
    private void FindLoadedWorldItems() {
        _loadedWorldItemsDirty = false;
        _loadedWorldItems.Clear();

        var worldBools = GetWorldBools();
        if (worldBools.Count == 0) {
            return;
        }

        foreach (var item in UnityEngine.Object.FindObjectsByType<PersistentBoolItem>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None
                 )) {
            GetItemSceneAndId(item, out var scene, out var id);
            var key = GetItemKey(scene, id);
            if (worldBools.Contains(key) && !item.GetIsSemiPersistent()) {
                _loadedWorldItems.Add((item, scene, id, key));
            }
        }
    }

    /// <summary>
    /// Adds saved objects of the world and flags of the player data that got set in the game of the partner to the local
    /// save. They are added as soon as the loaded save is paired with the partner, also while its check is still running,
    /// because the game of the partner may have finished its check already and doesn't send them again.
    /// </summary>
    private void OnWorldChange(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker) || PlayerData.instance == null ||
            SceneData.instance == null) {
            return;
        }

        var loadedScenes = GetLoadedSceneNames();
        var loadedItems = new HashSet<string>(StringComparer.Ordinal);
        var items = 0;
        var flags = 0;

        for (var i = 0; i < update.ItemIds.Count && i < update.ItemScenes.Count; i++) {
            var scene = update.ItemScenes[i];
            var id = update.ItemIds[i];

            // The partner has it now, so the local game doesn't send it back
            _knownWorldItems.Add(GetItemKey(scene, id));
            if (AddWorldItem(scene, id, loadedScenes, loadedItems, ref flags)) {
                items++;
            }
        }

        OverrideLoadedItems(loadedItems);
        flags += ApplyInteractionFlags(update);

        if (items > 0 || flags > 0) {
            Logger.Info($"Added {items} changes of the world from {player.Username}, with {flags} player data flags");
        }
    }
}
