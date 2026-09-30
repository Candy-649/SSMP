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
    /// Readies the items that save the number of an FSM that each game runs by itself, as the copy is made. Only a
    /// number that such an FSM holds is looked after: the number of an FSM that the scene host runs for both games is
    /// that game's to keep.
    /// </summary>
    private void KeepSavesOfTheCopy() {
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

        // Until it is known which game runs the creature, the room's own item saves as it always did
        SaveWhereItRuns(copyRuns: false);
    }

    /// <summary>
    /// Lets only the item of the creature that runs in this game save: the copy's, or the room's own.
    /// </summary>
    /// <param name="copyRuns">Whether the copy runs in this game, as it does in a scene client's.</param>
    private void SaveWhereItRuns(bool copyRuns) {
        if (_hostSave != null) {
            _hostSave.dontSave = copyRuns || _hostSaveWasOff;
        }

        if (_copySave != null) {
            _copySave.dontSave = !copyRuns;
        }
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

        var hostValue = _hostSaveFsm.FsmVariables.FindFsmInt(SavedIntName);
        var copyValue = _copySaveFsm.FsmVariables.FindFsmInt(SavedIntName);
        if (hostValue == null || copyValue == null) {
            return;
        }

        hostValue.Value = copyValue.Value;

        // Saved now as well, while the copy's item still saves: a room's own item that never started yet reads the save
        // as it starts, and would put the number from before back into the FSM
        _copySave.SaveState();
        Logger.Info(
            $"Handed the saved '{SavedIntName}' {copyValue.Value} of '{_copySaveFsm.FsmName}' from the copy of entity " +
            $"{Id} to the room's own"
        );
    }
}
