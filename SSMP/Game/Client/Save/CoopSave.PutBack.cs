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
/// on and spitters come back to life, and spiked flyers go back to where they started. Only the game that runs the
/// room runs its creatures, so only its own player's put-backs reached them: a player of another game who fell found
/// the creatures they had used up gone, and a fall of the player whose game runs the room sent the spiked flyers back
/// under the others.
///
/// The user decided (10-11) to go about them the way the grinders went (see CoopSave.PacedMachines):
/// - A creature that is dead or out comes back, and one that lost health gets it back, whichever player of the room is
///   put back: the game that runs the room hears of the others' and does what the game does for its own player.
/// - A living creature is not sent back to where it started while another player is in the room, for anyone's
///   put-back, just as being caught no longer sends the grinders back.
/// Both games see the same, as the creatures of the other games follow those of the game that runs the room.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The creatures that the game sets right as the player is put back, each named by what its objects are called.
    /// </summary>
    private static readonly PutBackKind[] PutBackKinds = [
        // Flies that the player bounces on: one that died waits for it to come back, one that lost health gets it back
        new(["Bone Goomba Bounce Fly"], "Control", "HAZARD RELOAD", ["Die", "Wait For Hazard", "Idle"], null),

        // Spiked flyers: one that died comes back where it started, and the event sends a living one back there too
        new(["Spike Lazy Flyer"], "Control", "HAZARD RESPAWNED", ["Die", "Check Distance"], "Hazard Reset"),

        // Spitters: one that fell into the lava comes back
        new(["Bone Spitter"], "Control", "HAZARD RESPAWNED", ["Lava Death"], null)
    ];

    /// <summary>
    /// The local player was put back at the last safe place. If another game runs the room, it is told, to bring back
    /// what the game brings back for its own player (see <see cref="OnPutBack"/>).
    /// </summary>
    private void TellRoomOfPutBack() {
        if (_checkedMembers.Count == 0 || IsSceneHost?.Invoke() != false ||
            !GetCheckedMembers().Exists(member => member.IsInLocalScene)) {
            return;
        }

        // To each member in the room, of whom the one whose game runs it takes it
        SendToRoomMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.PutBack,
            Scene = SceneUtil.GetCurrentSceneName()
        });
    }

    /// <summary>
    /// A member in the room was put back at the last safe place in their game. This game runs the room, so its
    /// creatures that are dead or out come back, and those that lost health get it back, as for its own player. A
    /// living one that the game would send back to where it started stays where it is (see
    /// <see cref="KeepsCreatureInPlace"/>).
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update.</param>
    private void OnPutBack(ClientPlayerData player, CoopSaveUpdate update) {
        if (!_checkedMembers.Contains(player.Id) || update.Scene != SceneUtil.GetCurrentSceneName() ||
            IsSceneHost?.Invoke() != true) {
            return;
        }

        // Found first and told after, as what they do as they come back can switch objects on and off, which changes
        // the list of FSMs
        var told = new List<(Fsm Fsm, string EventName)>();
        foreach (var playMakerFsm in PlayMakerFSM.FsmList) {
            if (playMakerFsm == null || !playMakerFsm.isActiveAndEnabled ||
                playMakerFsm.gameObject.scene.name != update.Scene ||
                FindPutBackKind(playMakerFsm.gameObject.name, playMakerFsm.FsmName) is not { } kind ||
                playMakerFsm.Fsm is not { Started: true } fsm ||
                Array.IndexOf(kind.ComeBackStates, fsm.ActiveStateName) < 0) {
                continue;
            }

            told.Add((fsm, kind.EventName));
        }

        foreach (var (fsm, eventName) in told) {
            fsm.Event(FsmEvent.GetFsmEvent(eventName));
        }

        if (told.Count > 0) {
            Logger.Info(
                $"{player.Username} was put back at the last safe place, so {told.Count} creatures of the room that " +
                "were dead or hurt came back"
            );
        }
    }

    /// <summary>
    /// Whether an FSM switch would send a living creature back to where it started as a player is put back, while
    /// another player is in the room: it stays where it is (see <see cref="PutBackKinds"/>).
    /// </summary>
    /// <param name="fsm">The FSM about to switch.</param>
    /// <param name="toState">The state it would switch to.</param>
    private bool KeepsCreatureInPlace(Fsm fsm, FsmState toState) {
        // Every switch of every FSM comes through here, so the name of the state goes first, before the name of the
        // object, which is made anew each time it is asked for
        if (_checkedMembers.Count == 0 || !IsSendBackState(toState.Name) || fsm.GameObject == null ||
            FindPutBackKind(fsm.GameObject.name, fsm.Name) is not { SendBackState: { } sendBackState } kind ||
            toState.Name != sendBackState || Array.IndexOf(kind.ComeBackStates, fsm.ActiveStateName) >= 0) {
            return false;
        }

        if (!GetCheckedMembers().Exists(member => member.IsInLocalScene)) {
            return false;
        }

        Logger.Info($"'{fsm.GameObject.name}' stays where it is as a player is put back, since another is in the room");
        return true;
    }

    /// <summary>
    /// Whether a state is one into which a put-back sends a living creature of some kind back to where it started.
    /// </summary>
    private static bool IsSendBackState(string stateName) {
        foreach (var kind in PutBackKinds) {
            if (kind.SendBackState == stateName) {
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
    /// A kind of creature that the game sets right as the player is put back at the last safe place.
    /// </summary>
    /// <param name="Names">What its objects are called, by the start of their names.</param>
    /// <param name="FsmName">The FSM that hears of the player being put back.</param>
    /// <param name="EventName">The event it hears it by.</param>
    /// <param name="ComeBackStates">The states in which the event brings it back to life or gives it its health back:
    /// those of a creature that is dead or out, and of a living one whose health it fills.</param>
    /// <param name="SendBackState">The state into which the event sends a living one back to where it started, or
    /// null if it sends none there.</param>
    private sealed record PutBackKind(
        string[] Names,
        string FsmName,
        string EventName,
        string[] ComeBackStates,
        string? SendBackState
    );
}
