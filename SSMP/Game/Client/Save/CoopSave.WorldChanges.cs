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
    /// The keys of the saved objects of the world that both saves are known to have set.
    /// </summary>
    private readonly HashSet<string> _knownWorldItems = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether scenes loaded or unloaded since the saved objects of the world in the loaded scenes were found.
    /// </summary>
    private bool _loadedWorldItemsDirty = true;

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
    /// Forgets the saved objects of the world that were found and known to both saves, for a new check or session.
    /// </summary>
    private void ResetWorldChanges() {
        _loadedWorldItems.Clear();
        _knownWorldItems.Clear();
        _loadedWorldItemsDirty = true;
        _nextWorldChangeTime = 0f;
    }

    /// <summary>
    /// Makes the saved objects of the world be found again after a scene loaded.
    /// </summary>
    private void OnWorldSceneLoaded(Scene scene, LoadSceneMode mode) => _loadedWorldItemsDirty = true;

    /// <summary>
    /// Makes the saved objects of the world be found again after a scene unloaded.
    /// </summary>
    private void OnWorldSceneUnloaded(Scene scene) => _loadedWorldItemsDirty = true;

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

        _nextWorldChangeTime = 0f;
        UpdateWorldChanges(partner);
    }

    /// <summary>
    /// Sends the saved objects of the world that got set in the loaded scenes to the partner.
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
                    _loadedWorldItemsDirty = true;
                    continue;
                }

                if (_knownWorldItems.Contains(key) || !item.GetCurrentValue()) {
                    continue;
                }

                _knownWorldItems.Add(key);
                update ??= new CoopSaveUpdate { TargetId = partner.Id, Kind = CoopSaveUpdateKind.WorldChange };
                update.ItemScenes.Add(scene);
                update.ItemIds.Add(id);
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
    /// Finds the saved objects of the world in the loaded scenes. Those that the save has set already count as known,
    /// because the check added them to both saves.
    /// </summary>
    private void FindLoadedWorldItems() {
        _loadedWorldItemsDirty = false;
        _loadedWorldItems.Clear();

        var worldBools = GetWorldBools();
        var sceneData = SceneData.instance;
        if (worldBools.Count == 0 || sceneData == null) {
            return;
        }

        foreach (var item in UnityEngine.Object.FindObjectsByType<PersistentBoolItem>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None
                 )) {
            GetItemSceneAndId(item, out var scene, out var id);
            var key = GetItemKey(scene, id);
            if (!worldBools.Contains(key) || item.GetIsSemiPersistent()) {
                continue;
            }

            _loadedWorldItems.Add((item, scene, id, key));
            if (sceneData.PersistentBools.TryGetValue(scene, id, out var saved) && saved.Value) {
                _knownWorldItems.Add(key);
            }
        }
    }

    /// <summary>
    /// Adds saved objects of the world that got set in the game of the partner to the local save.
    /// </summary>
    private void OnWorldChange(ClientPlayerData player, CoopSaveUpdate update) {
        if (_checkedWith != player.Id || PlayerData.instance == null || SceneData.instance == null) {
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

        if (items > 0) {
            Logger.Info($"Added {items} changes of the world from {player.Username}, with {flags} player data flags");
        }
    }
}
