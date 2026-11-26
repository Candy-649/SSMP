using SSMP.Game.Client.Entity.Component;
using UnityEngine;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// What the room's own object of an entity keeps in the save, like whether a creature is dead (see
/// <see cref="KeepBoolSavesOfTheCopy"/>). The copy of a scene client is a clone of the room's own object, so it has the
/// same items: the copy's never saves, and the room's own saves only in the game that runs the object.
/// </summary>
internal partial class Entity {
    /// <summary>
    /// The item of the room's own object that keeps a yes or no in the save - for a creature, whether it is dead - or
    /// null for an object without one (see <see cref="KeepBoolSavesOfTheCopy"/>).
    /// </summary>
    private PersistentBoolItem? _hostBoolSave;

    /// <summary>
    /// Whether that item was left out of the saves before this mod had any say.
    /// </summary>
    private bool _hostBoolSaveWasOff;

    /// <summary>
    /// Whether this entity last switched the saving of that item off (see <see cref="SetHostBoolSaveOff"/>).
    /// </summary>
    private bool _hostBoolSaveSetOff;

    /// <summary>
    /// Readies the items that keep a yes or no of the object in the save as the copy is made: for a creature, whether it
    /// is dead. The game saves the creature's item as it dies (EnemyDeathEffects), and leaves the creature out of its
    /// room while that is kept, until a rest clears it. The copy's item goes by the name of the clone, which no save
    /// ever reads, so it saves nothing. The room's own saves only in the game that runs the object (SaveWhereItRuns):
    /// in a scene client's game it is switched off before it ever reads the save, and as the player left the room it
    /// saved the "alive" it was built with. A creature that died there was then alive in that save, and back in both
    /// games whenever that game ran the room next (USER 10-10: "只要我们两个人都出房间了再回来就会刷新").
    /// </summary>
    private void KeepBoolSavesOfTheCopy() {
        var copySave = Object.Client.GetComponent<PersistentBoolItem>();
        if (copySave != null) {
            copySave.dontSave = true;

            // Nor does it read the save, as it first starts: under the name of the clone, a death that older builds
            // kept for a copy set every later copy of the creature dead as it started (HealthManager takes what its
            // item reads), while the room's own creature lived on
            if (copySave.itemData != null) {
                copySave.itemData.ID = Object.Host.name + " (copy, not saved)";
            }
        }

        _hostBoolSave = Object.Host.GetComponent<PersistentBoolItem>();
        if (_hostBoolSave != null) {
            _hostBoolSaveWasOff = _hostBoolSave.dontSave;

            // Until it is known which game runs the object, it saves only if it runs here already, having read the save
            SetHostBoolSaveOff(!_keepsRunning);
        }
    }

    /// <summary>
    /// Switches the saving of the room's own item off or on, as far as this entity has a say: off as well when it was
    /// off before this mod, and kept off when something else switched it off since this entity last switched it on -
    /// a rest shared while the room is loaded leaves what is set in it out of the save (BenchCoop).
    /// </summary>
    private void SetHostBoolSaveOff(bool off) {
        if (_hostBoolSave == null) {
            return;
        }

        var offElsewhere = _hostBoolSave.dontSave && !_hostBoolSaveSetOff;
        _hostBoolSaveSetOff = off || _hostBoolSaveWasOff;
        _hostBoolSave.dontSave = _hostBoolSaveSetOff || offElsewhere;
    }

    /// <summary>
    /// Keeps whether the copy in this game is dead in this game's save, under the room's own creature, the way the game
    /// that runs it keeps it there (see <see cref="KeepBoolSavesOfTheCopy"/>); neither item saves it here. Not for a
    /// creature that never comes back, which the partner killed alone before this player walked in: that stays the
    /// partner's to have killed, as it did before.
    /// </summary>
    /// <param name="dead">Whether the copy is dead now.</param>
    /// <param name="witnessed">Whether its death was seen in this game rather than found as the player walked in.</param>
    private void RecordCopyDeath(bool dead, bool witnessed) {
        var item = _hostBoolSave;
        var sceneData = SceneData.instance;
        if (item == null || _hostBoolSaveWasOff || sceneData == null ||
            (!witnessed && !item.GetIsSemiPersistent()) ||
            (Object.Host != null && Object.Host.TryGetComponent<HealthManager>(out var health) &&
             health.ignorePersistence) ||
            item.saveCondition is { IsDefined: true, IsFulfilled: false }) {
            return;
        }

        var id = item.GetId();
        var sceneName = item.GetSceneName();
        sceneData.PersistentBools.SetValue(new PersistentItemData<bool> {
            ID = string.IsNullOrEmpty(id) ? item.name : id,
            SceneName = string.IsNullOrEmpty(sceneName)
                ? global::GameManager.GetBaseSceneName(item.gameObject.scene.name)
                : sceneName,
            IsSemiPersistent = item.GetIsSemiPersistent(),
            Value = dead
        });
    }

    /// <summary>
    /// Whether an object is the room's own object of an entity, which both games show as one.
    /// </summary>
    internal static bool IsRoomObjectOfAnEntity(GameObject gameObject) {
        return EntitiesByRoomObject.ContainsKey(gameObject);
    }

    /// <summary>
    /// Whether the room's own object of the creature sleeps in this game, as another game runs it, so that what the
    /// room sends it never reaches it here.
    /// </summary>
    private bool SleepsHere => _isControlled && !_keepsRunning;

    /// <summary>
    /// The state that an FSM of a creature that sleeps in this game is in (see <see cref="SleepsHere"/>), as the
    /// room's own FSM of it: the one the scene host says, or that of its copy while that runs here. Null if the
    /// creature does not sleep here.
    /// </summary>
    /// <param name="roomFsm">The room's own FSM of the creature.</param>
    internal static HutongGames.PlayMaker.FsmState? StateOfSleeper(PlayMakerFSM roomFsm) {
        if (!EntitiesByRoomObject.TryGetValue(roomFsm.gameObject, out var entity) || !entity.SleepsHere) {
            return null;
        }

        var index = entity._fsms.Host.IndexOf(roomFsm);
        return index >= 0 && index < entity._fsms.Client.Count &&
               entity.StateOfCopy(entity._fsms.Client[index]) is { } state
            ? roomFsm.Fsm.GetState(state.Name)
            : null;
    }

    /// <summary>
    /// Whether the copy died here or is no more, or the creature was told dead. The room's own object, taking the
    /// creature over, never ran in this game and does not know it. A death that was only told is not this game's to
    /// keep either: the partner's game sends one of a creature that comes back (CoopSave.CreatureDeaths), and one that
    /// never does stays theirs (see <see cref="RecordCopyDeath"/>).
    /// </summary>
    private bool CopyIsGone() {
        return Object.Client == null ||
               (Object.Client.TryGetComponent<HealthManager>(out var health) && health.GetIsDead()) ||
               (_components.TryGetValue(EntityComponentType.Health, out var component) &&
                component is HealthManagerComponent { ToldDead: true });
    }

    /// <summary>
    /// Lets the item of the room's own object save the yes or no of the object only in the game that runs it, and not
    /// for a creature whose copy died here: it would save the "alive" it was built with (RecordCopyDeath kept the
    /// death).
    /// </summary>
    /// <param name="copyRuns">Whether the copy runs in this game, as it does in a scene client's.</param>
    private void SaveWhereItRuns(bool copyRuns) {
        SetHostBoolSaveOff(copyRuns || CopyIsGone());
    }
}
