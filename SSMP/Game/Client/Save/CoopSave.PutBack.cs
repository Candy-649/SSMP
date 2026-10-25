using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Creatures that the game sets right as the player is put back at the last safe place after a hazard: flies to bounce
/// on and spitters come back to life, spiked flyers go back to where they started with their health filled, and
/// spitters that the player is put back above switch themselves off. Only the game that runs the room runs its
/// creatures, so only its own player's put-backs reached them: a player of another game who fell found the creatures
/// they had used up gone, and a fall of the player whose game runs the room sent the spiked flyers back and switched
/// the spitters off under the others. What each player has to themselves (see PersonalPlaces), like the flies to
/// bounce on of the lava race since 10-23, is left out: each game brings back its own for its own player, as the game
/// does.
///
/// The user decided (10-11) to go about them the way the grinders went (see CoopSave.PacedMachines):
/// - A creature that is dead or out comes back, and one that lost health gets it back, whichever player of the room is
///   put back: the game that runs the room hears of the others' and does what the game does for its own player.
/// - A living creature is not sent back to where it started or switched off while another player is in the room, for
///   anyone's put-back, just as being caught no longer sends the grinders back.
/// Both games see the same, as the creatures of the other games follow those of the game that runs the room.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The creatures that the game sets right as the player is put back, each named by what its objects are called.
    /// </summary>
    private static readonly PutBackKind[] PutBackKinds = [
        // Flies that the player bounces on: one that died waits for it to come back, one that lost health gets it back.
        // Those of the lava race are each player's own (see PersonalPlaces and BringBackOwnThings)
        new(["Bone Goomba Bounce Fly"], "Control", "HAZARD RELOAD", ["Die", "Wait For Hazard", "Idle"], null, false),

        // Spiked flyers: one that died comes back where it started, and the event sends a living one back there too,
        // where it gets its health back (Activate)
        new(["Spike Lazy Flyer"], "Control", "HAZARD RESPAWNED", ["Die", "Check Distance"], "Hazard Reset", true),

        // Spitters: one that fell into the lava comes back, and the event switches off a living one that the player
        // is above (Check Pause, End Check, End)
        new(["Bone Spitter"], "Control", "HAZARD RESPAWNED", ["Lava Death"], "Check Pause", false)
    ];

    /// <summary>
    /// The local player was put back at the last safe place, or stood up there after a rescue: the other games of the
    /// room are told where. The one that runs the room brings back what the game brings back for its own player as it
    /// puts them back (see <see cref="OnPutBack"/>), which a stand-up isn't, and each turns the passages of dropping
    /// boxes near there to the player (see GamePatcher.BoxPassages).
    /// </summary>
    /// <param name="position">Where the player was put.</param>
    /// <param name="stoodUp">Whether they stood up there after a rescue, rather than being put back by the game.</param>
    private void TellRoomOfPutBack(UnityEngine.Vector3 position, bool stoodUp) {
        if (_checkedMembers.Count == 0 || !GetCheckedMembers().Exists(member => member.IsInLocalScene)) {
            return;
        }

        SendToRoomMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.PutBack,
            Scene = SceneUtil.GetCurrentSceneName(),
            Values = stoodUp ? [position.x, position.y, 1f] : [position.x, position.y]
        });
    }

    /// <summary>
    /// The local player stood back up at the safe place of the room after a rescue, where the game would have put them
    /// back had the hit not taken the last of their health: the passages of dropping boxes near there turn to them,
    /// here and in the other games of the room, as for a put-back (USER 10-25, "只要有人掉血了回到安全点就把机关调到
    /// 她的位置"). Nothing else that a put-back brings back comes back for it.
    /// </summary>
    /// <param name="place">Where they stood up.</param>
    private void NoteStandUpAtSafePlace(UnityEngine.Vector3 place) {
        try {
            TellRoomOfPutBack(place, true);
        } catch (Exception e) {
            LogInteractionError(e);
        }

        try {
            GamePatcher.NoteBoxPutBack(SceneUtil.GetCurrentSceneName(), place.x, "the local player");
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// A member in the room was put back at the last safe place in their game. The passages of dropping boxes near
    /// there turn to them. If this game runs the room, its creatures that are dead or out come back, and those that
    /// lost health get it back, as for its own player. A living one that the game would send back to where it started
    /// or switch off stays as it is (see <see cref="KeepsCreatureInPlace"/>), and gets its health back if the game
    /// gives it that on the way.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update.</param>
    private void OnPutBack(ClientPlayerData player, CoopSaveUpdate update) {
        if (!_checkedMembers.Contains(player.Id) || update.Scene != SceneUtil.GetCurrentSceneName()) {
            return;
        }

        if (update.Values.Count > 0) {
            GamePatcher.NoteBoxPutBack(update.Scene, update.Values[0], player.Username);
        }

        // A stand-up after a rescue brings nothing else back
        if (IsSceneHost?.Invoke() != true || update.Values.Count > 2 && update.Values[2] != 0f) {
            return;
        }

        // Found first and told after, as what they do as they come back can switch objects on and off, which changes
        // the list of FSMs
        var told = new List<(Fsm Fsm, string EventName)>();
        var healed = 0;
        foreach (var playMakerFsm in PlayMakerFSM.FsmList) {
            if (playMakerFsm == null || !playMakerFsm.isActiveAndEnabled ||
                FindPutBackKind(playMakerFsm.gameObject.name, playMakerFsm.FsmName) is not { } kind ||
                playMakerFsm.Fsm is not { Started: true } fsm ||
                !IsPartOfRoom(playMakerFsm.gameObject.scene.name, update.Scene) ||
                PersonalPlaces.Contains(playMakerFsm.gameObject)) {
                continue;
            }

            if (Array.IndexOf(kind.ComeBackStates, fsm.ActiveStateName) >= 0) {
                told.Add((fsm, kind.EventName));
            } else if (kind.HealsHeld && HealHeld(playMakerFsm.gameObject)) {
                healed++;
            }
        }

        foreach (var (fsm, eventName) in told) {
            fsm.Event(FsmEvent.GetFsmEvent(eventName));
        }

        if (told.Count > 0 || healed > 0) {
            Logger.Info(
                $"{player.Username} was put back at the last safe place, so {told.Count} creatures of the room that " +
                $"were dead or hurt came back, and {healed} living ones got their health back"
            );
        }
    }

    /// <summary>
    /// Whether an FSM switch would send a living creature back to where it started or switch it off as a player is put
    /// back, while another player is in the room: it stays as it is, and in the game that runs the room gets its health
    /// back if the game gives it that on the way (see <see cref="PutBackKinds"/>).
    /// </summary>
    /// <param name="fsm">The FSM about to switch.</param>
    /// <param name="toState">The state it would switch to.</param>
    private bool KeepsCreatureInPlace(Fsm fsm, FsmState toState) {
        // Every switch of every FSM comes through here, so the name of the state goes first, before the name of the
        // object, which is made anew each time it is asked for. Only the switch that the put-back's event makes: one
        // into the same state for another reason, or one that SSMP sets, goes on
        if (_checkedMembers.Count == 0 || !IsHeldState(toState.Name) || fsm.GameObject == null ||
            fsm.LastTransition is not { } transition || transition.ToState != toState.Name ||
            FindPutBackKind(fsm.GameObject.name, fsm.Name) is not { HeldState: { } heldState } kind ||
            toState.Name != heldState || transition.EventName != kind.EventName ||
            Array.IndexOf(kind.ComeBackStates, fsm.ActiveStateName) >= 0) {
            return false;
        }

        if (!GetCheckedMembers().Exists(member => member.IsInLocalScene)) {
            return false;
        }

        // Health is settled in the game that runs the room alone, which tells the others
        var healed = kind.HealsHeld && IsSceneHost?.Invoke() == true && HealHeld(fsm.GameObject);
        Logger.Info(
            $"'{fsm.GameObject.name}' stays as it is as a player is put back, since another is in the room" +
            (healed ? ", and gets its health back" : "")
        );
        return true;
    }

    /// <summary>
    /// Gives a living creature that stays as it is as a player is put back the health that the game gives it on the
    /// way it does not go (HealthManager.HealToMax, which the AddHP of the way runs).
    /// </summary>
    /// <param name="creature">The creature.</param>
    /// <returns>Whether its health changed.</returns>
    private static bool HealHeld(UnityEngine.GameObject creature) {
        var healthManager = creature.GetComponent<HealthManager>();
        if (healthManager == null || healthManager.GetIsDead()) {
            return false;
        }

        var hp = healthManager.hp;
        healthManager.HealToMax();
        return healthManager.hp != hp;
    }

    /// <summary>
    /// Whether an object's scene is the room or a part of it loaded beside it, such as the scene of a fight, the way
    /// the room's creatures are found (EntityManager.OnSceneLoaded): one named after the room, but not the room the
    /// player is walking into, many of which are named after the room next to them with a letter added.
    /// </summary>
    /// <param name="sceneName">The name of the object's scene.</param>
    /// <param name="room">The name of the room.</param>
    private static bool IsPartOfRoom(string sceneName, string room) {
        if (sceneName == room) {
            return true;
        }

        if (!sceneName.StartsWith(room, StringComparison.Ordinal)) {
            return false;
        }

        var gameManager = global::GameManager.instance;
        return gameManager == null || sceneName != gameManager.nextSceneName;
    }

    /// <summary>
    /// Whether a state is one into which a put-back takes a living creature of some kind that is held (see
    /// <see cref="PutBackKind.HeldState"/>).
    /// </summary>
    private static bool IsHeldState(string stateName) {
        foreach (var kind in PutBackKinds) {
            if (kind.HeldState == stateName) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The kind of creature that an object is, by its name and the FSM, if the game sets it right as the player is put
    /// back.
    /// </summary>
    private static PutBackKind? FindPutBackKind(string objectName, string fsmName) {
        foreach (var kind in PutBackKinds) {
            if (kind.FsmName != fsmName) {
                continue;
            }

            foreach (var name in kind.Names) {
                if (objectName.StartsWith(name, StringComparison.Ordinal)) {
                    return kind;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Brings back what the local player has to themselves in the room (see PersonalPlaces) and the game brings back as
    /// the player is put back at a safe place after a hazard, like the flies to bounce off that they used up, as the
    /// player stands back up from a death. The game would have brought it back with the room, on the way back from the
    /// bench that a stand-up stands in for. A player stood up beside the partner below flies that they had used found
    /// them gone, where they come back only as the player is put back, and the partner's put-backs no longer bring
    /// them back, as they are each player's own (review of 10-23).
    /// </summary>
    private static void BringBackOwnThings() {
        const string OwnThingsComeBackEvent = "HAZARD RELOAD";

        var told = new List<PlayMakerFSM>();
        foreach (var playMakerFsm in PlayMakerFSM.FsmList) {
            if (playMakerFsm == null || playMakerFsm.Fsm?.ActiveState is not { } state ||
                !Array.Exists(state.Transitions, transition => transition.EventName == OwnThingsComeBackEvent) ||
                !PersonalPlaces.Contains(playMakerFsm.gameObject)) {
                continue;
            }

            told.Add(playMakerFsm);
        }

        foreach (var playMakerFsm in told) {
            if (playMakerFsm != null) {
                playMakerFsm.SendEvent(OwnThingsComeBackEvent);
            }
        }

        if (told.Count > 0) {
            Logger.Info($"Brought back {told.Count} thing(s) of the local player's own with the stand-up");
        }
    }

    /// <summary>
    /// A kind of creature that the game sets right as the player is put back at the last safe place.
    /// </summary>
    /// <param name="Names">What its objects are called, by the start of their names.</param>
    /// <param name="FsmName">The FSM that hears of the player being put back.</param>
    /// <param name="EventName">The event it hears it by.</param>
    /// <param name="ComeBackStates">The states in which the event brings it back to life or gives it its health back:
    /// those of a creature that is dead or out, and of a living one whose health it fills.</param>
    /// <param name="HeldState">The state into which the event takes a living one to send it back to where it started
    /// or to switch it off, which is held while another player is in the room; null if it takes none anywhere.</param>
    /// <param name="HealsHeld">Whether the way from <paramref name="HeldState"/> fills the health of the creature,
    /// which one that is held gets all the same.</param>
    private sealed record PutBackKind(
        string[] Names,
        string FsmName,
        string EventName,
        string[] ComeBackStates,
        string? HeldState,
        bool HealsHeld
    );
}
