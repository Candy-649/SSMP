using System;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The deaths of creatures that the game brings back after a rest, in a checked two-player save. The game that runs a
/// room keeps the death of each of its creatures in its save as it dies, and leaves the creature out of the room while
/// it is kept, until a rest or a death of either player clears them all (BenchCoop). A creature that one player killed
/// while the partner was in another room was dead in one save only, and back in both games whenever the partner's game
/// ran the room next. So each such death goes into the partner's save too, wherever the partner is.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// Hook for the items of the world saving a yes or no, which is how the death of a creature goes into the save.
    /// </summary>
    private Hook? _boolItemSaveHook;

    /// <summary>
    /// Whether telling the partner of a death threw, which is only logged once.
    /// </summary>
    private bool _creatureDeathFailed;

    /// <summary>
    /// Registers the hook on the items of the world saving a yes or no.
    /// </summary>
    private void RegisterCreatureDeathHooks() {
        _boolItemSaveHook = CreateHook(
            typeof(PersistentBoolItem).GetMethod(
                "SaveValue", InstanceFlags, null, [typeof(PersistentItemData<bool>)], null
            ),
            new Action<Action<PersistentBoolItem, PersistentItemData<bool>>, PersistentBoolItem,
                PersistentItemData<bool>>(OnBoolItemSaveValue)
        );
    }

    /// <summary>
    /// Tells the partner of a creature that the game brings back after a rest and that died just now in this game: its
    /// item saves it as dead, and the save did not have it so yet. Only the game that runs the creature saves its item
    /// (Entity.KeepBoolSavesOfTheCopy), and only a creature that both games show as one is told of: one that each game
    /// runs by itself is the partner's own to kill.
    /// </summary>
    private void OnBoolItemSaveValue(
        Action<PersistentBoolItem, PersistentItemData<bool>> orig,
        PersistentBoolItem self,
        PersistentItemData<bool> data
    ) {
        var died = false;
        try {
            died = data is { Value: true, IsSemiPersistent: true } && _checkedWith != null &&
                   SceneData.instance is { } sceneData &&
                   !(sceneData.PersistentBools.TryGetValue(data.SceneName, data.ID, out var saved) && saved.Value) &&
                   self.TryGetComponent<HealthManager>(out _) &&
                   SSMP.Game.Client.Entity.Entity.IsRoomObjectOfAnEntity(self.gameObject);
        } catch (Exception e) {
            if (!_creatureDeathFailed) {
                _creatureDeathFailed = true;
                Logger.Error($"Could not tell whether '{self.name}' died:\n{e}");
            }
        }

        orig(self, data);

        if (died && _checkedWith is { } partnerId && _playerData.TryGetValue(partnerId, out var partner)) {
            var update = new CoopSaveUpdate { TargetId = partner.Id, Kind = CoopSaveUpdateKind.CreatureDeath };
            update.ItemScenes.Add(data.SceneName);
            update.ItemIds.Add(data.ID);
            Send(update);
        }
    }

    /// <summary>
    /// Keeps a creature that died in the partner's game dead in this save, until the next rest of either player. In a
    /// room this game has loaded, the copy dies here with the creature, and the room's own creature here does not save
    /// over it (Entity.KeepBoolSavesOfTheCopy).
    /// </summary>
    private void OnCreatureDeath(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker) ||
            SceneData.instance is not { } sceneData) {
            return;
        }

        for (var i = 0; i < update.ItemIds.Count && i < update.ItemScenes.Count; i++) {
            var scene = update.ItemScenes[i];
            var id = update.ItemIds[i];
            if (sceneData.PersistentBools.TryGetValue(scene, id, out var saved) && saved.Value) {
                continue;
            }

            sceneData.PersistentBools.SetValue(new PersistentItemData<bool> {
                ID = id,
                SceneName = scene,
                Value = true,
                IsSemiPersistent = true
            });
            Logger.Info($"'{id}' in {scene} died in the game of {player.Username}, so it is dead in this save too");
        }
    }
}
