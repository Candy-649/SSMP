using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// The number that an FSM which each game runs by itself keeps in the save, like how many shards a creature has left
/// to fling at the players who strike it (see <see cref="EntityRegistryEntry.HitterGameFsms"/>). The game keeps such a
/// number in a PersistentIntItem on the creature: the item puts the saved number into the FSM's "Value" as the room
/// starts, and takes it back out of there whenever the game saves. The copy of a scene client is a clone of the room's
/// own creature, so it has an item too, and both items were saving in that game:
/// - the copy's under the name of the clone, which no save had ever heard of, so the copy started from nothing every
///   time it was made, and what it counted was saved where the room's own creature would never look;
/// - the room's own, which this mod switches off before it ever reads the save, put the FSM's number from before the
///   save back each time the game saved - and a creature that the player had killed came back to life.
/// Now the copy's item goes by the name of the room's own, and only the item of the creature that runs in this game
/// saves: the copy's in a scene client's game, the room's own in the scene host's.
/// </summary>
internal partial class Entity {
    /// <summary>
    /// The name of the FSM variable that a PersistentIntItem keeps in the save, as FSMUtility.FindFSMWithPersistentInt
    /// looks for it.
    /// </summary>
    private const string SavedIntName = "Value";

    /// <summary>
    /// The item of the room's own creature that saves the number of an FSM that each game runs by itself, or null for a
    /// creature without one.
    /// </summary>
    private PersistentIntItem? _hostSave;

    /// <summary>
    /// The item of the copy that saves the same number, or null.
    /// </summary>
    private PersistentIntItem? _copySave;

    /// <summary>
    /// The FSM of the room's own creature whose number its item saves.
    /// </summary>
    private PlayMakerFSM? _hostSaveFsm;

    /// <summary>
    /// The same FSM of the copy.
    /// </summary>
    private PlayMakerFSM? _copySaveFsm;

    /// <summary>
    /// Whether the item of the room's own creature was left out of the saves before this mod had any say.
    /// </summary>
    private bool _hostSaveWasOff;

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
    /// Readies the items of the room's own object and of its copy that keep something in the save, as the copy is made:
    /// the numbers first, whose own setup has the room's own items save as they always did until it is known which game
    /// runs the object.
    /// </summary>
    private void KeepSavesOfTheCopy() {
        KeepIntSavesOfTheCopy();
        KeepBoolSavesOfTheCopy();
    }

    /// <summary>
    /// Readies the items that save the number of an FSM that each game runs by itself, as the copy is made. Only a
    /// number that such an FSM holds is looked after: the number of an FSM that the scene host runs for both games is
    /// that game's to keep.
    /// </summary>
    private void KeepIntSavesOfTheCopy() {
        var hostSave = Object.Host.GetComponent<PersistentIntItem>();
        var copySave = Object.Client.GetComponent<PersistentIntItem>();
        if (hostSave == null || copySave == null) {
            return;
        }

        // The FSM that the game's item goes by: the first one with the variable
        PlayMakerFSM? hostFsm = null;
        foreach (var fsm in Object.Host.GetComponents<PlayMakerFSM>()) {
            if (fsm.FsmVariables.FindFsmInt(SavedIntName) != null) {
                hostFsm = fsm;
                break;
            }
        }

        if (hostFsm == null || !IsRunByEachGame(hostFsm)) {
            return;
        }

        PlayMakerFSM? copyFsm = null;
        foreach (var fsm in Object.Client.GetComponents<PlayMakerFSM>()) {
            if (fsm.FsmName == hostFsm.FsmName) {
                copyFsm = fsm;
                break;
            }
        }

        if (copyFsm == null || copyFsm.FsmVariables.FindFsmInt(SavedIntName) == null) {
            return;
        }

        // The copy's item has not set itself up yet, which it does as it first starts: the name and the room that it
        // saves under are the ones the room's own item works out for itself, from its object and its scene
        var hostData = hostSave.itemData;
        var copyData = copySave.itemData;
        if (hostData == null || copyData == null) {
            return;
        }

        copyData.ID = string.IsNullOrEmpty(hostData.ID) ? hostSave.name : hostData.ID;
        copyData.SceneName = string.IsNullOrEmpty(hostData.SceneName)
            ? global::GameManager.GetBaseSceneName(hostSave.gameObject.scene.name)
            : hostData.SceneName;

        _hostSave = hostSave;
        _copySave = copySave;
        _hostSaveFsm = hostFsm;
        _copySaveFsm = copyFsm;
        _hostSaveWasOff = hostSave.dontSave;

        // The room's own item reads the save now, which it does as its object first starts - and in a scene client's
        // game that object is switched off before it ever starts. Whatever it saves from here on is then the save's
        // number or one counted from it, and never the number it was built with: saved over the real one, that
        // brought a creature back to life with nothing left to fling
        hostSave.LoadIfNeverStarted();

        // Until it is known which game runs the creature, the room's own item saves as it always did
        SaveWhereItRuns(copyRuns: false);
    }

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
    /// Whether the copy died here or is no more. The room's own object, taking the creature over, never ran in this
    /// game and does not know it.
    /// </summary>
    private bool CopyIsGone() {
        return Object.Client == null ||
               (Object.Client.TryGetComponent<HealthManager>(out var health) && health.GetIsDead());
    }

    /// <summary>
    /// Lets only the item of the creature that runs in this game save: the copy's, or the room's own.
    /// </summary>
    /// <param name="copyRuns">Whether the copy runs in this game, as it does in a scene client's.</param>
    private void SaveWhereItRuns(bool copyRuns) {
        if (_hostSave != null) {
            // What the room's own creature counted until now goes into the save first, which the copy reads as it
            // starts: a game that expected to run the room ran its own creature for a moment before it knew otherwise
            if (copyRuns) {
                _hostSave.SaveState();
            }

            _hostSave.dontSave = copyRuns || _hostSaveWasOff;
        }

        if (_copySave != null) {
            _copySave.dontSave = !copyRuns;
        }

        // The yes or no of the room's own object is saved in the game that runs it, and not for a creature whose copy
        // died here: it would save the "alive" it was built with (RecordCopyDeath kept the death)
        SetHostBoolSaveOff(copyRuns || CopyIsGone());
    }

    /// <summary>
    /// Hands the number that the copy kept to the room's own creature, as this game takes the creature over. The copy's
    /// FSM counted on in this game since the room's own item last read the save, and the room's own FSM starts over
    /// from its number when its object is switched on.
    /// </summary>
    private void HandSaveToTheRoom() {
        if (_copySave == null || _hostSaveFsm == null || _copySaveFsm == null) {
            return;
        }

        // A copy whose item never started never read the save, and counted nothing: it stayed hidden all along. The
        // room's own FSM keeps the number it read from the save then
        var hostValue = _hostSaveFsm.FsmVariables.FindFsmInt(SavedIntName);
        var copyValue = _copySaveFsm.FsmVariables.FindFsmInt(SavedIntName);
        if (hostValue == null || copyValue == null || !_copySave.started) {
            return;
        }

        hostValue.Value = copyValue.Value;

        // Saved now as well, while the copy's item still saves: the room's own item reads the save again as its object
        // first starts, and would put the number from before back into the FSM
        _copySave.SaveState();
        Logger.Info(
            $"Handed the saved '{SavedIntName}' {copyValue.Value} of '{_copySaveFsm.FsmName}' from the copy of entity " +
            $"{Id} to the room's own"
        );
    }
}
