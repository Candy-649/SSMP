using HutongGames.PlayMaker;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The cage that drops down on whoever takes the bait lying under it, in a checked two-player save: it only goes off
/// for the two of them together. Taking the bait alone does nothing at all - the player kneels, gets up again, and the
/// bait lies there as before. Taking it while the partner stands in the cage springs it on both, and each game drops
/// its own player in its own cage, so they go down at once. The user chose this on 2026-09-24, and wanted it to make
/// no sense to whoever tries it alone.
///
/// The bait tells its cage to go off from one state of its FSM, which only the bait of a cage ever goes into: the bait
/// at the end of an ordinary trail starts the next trail instead. That state is where this steps in, through the hook
/// on changes of FSM state that <see cref="RegisterInteractionHooks"/> puts in place. Alone, the bait goes where an
/// ordinary bait goes once it was taken - the player gets up and gets their controls back - and from there straight
/// back to waiting rather than on to following a trail. The bait of a cage has no trail, and following it would only
/// leave the bait lying there for four seconds in which nobody can take it.
///
/// A cage that went off for the partner makes the bait here go off by itself, at once, without the kneeling that
/// taking it starts with: the game of the partner saw the local player in the cage when it went off there. From then
/// on the bait here goes off whoever takes it, for as long as the room is loaded, since the partner is already on the
/// way down and what waits down there waits for both of them.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The name of the FSM of a bait.
    /// </summary>
    private const string CageBaitFsmName = "Control";

    /// <summary>
    /// The variable of a bait with the event that it sends its cage, which is empty for the bait of a trail.
    /// </summary>
    private const string CageBaitEventVariableName = "Trap Event";

    /// <summary>
    /// The state of a bait that waits to be taken.
    /// </summary>
    private const string CageBaitIdleState = "Idle";

    /// <summary>
    /// The state of a bait that takes the controls from the player who takes it, and goes on to have them kneel.
    /// </summary>
    private const string CageBaitTakeControlState = "Take Control";

    /// <summary>
    /// The state of a bait in which the player kneels to take it.
    /// </summary>
    private const string CageBaitKneelState = "Kneel";

    /// <summary>
    /// The state of a bait that tells its cage to go off.
    /// </summary>
    private const string CageBaitSpringState = "Send Trap Event";

    /// <summary>
    /// The state of a bait in which the player gets up again, which goes on to give them back their controls.
    /// </summary>
    private const string CageBaitStandState = "Stand";

    /// <summary>
    /// The state of a bait that follows the trail it started.
    /// </summary>
    private const string CageBaitTrailState = "Tracking";

    /// <summary>
    /// The state of a bait that puts it back to waiting.
    /// </summary>
    private const string CageBaitRewaitState = "Cancel";

    /// <summary>
    /// Where the part of a cage that says who is in it lies, from the object that the bait lies on.
    /// </summary>
    private const string CageInsidePath = "Cage/Capture Range";

    /// <summary>
    /// The bait whose cage went off for the partner in the current room, which goes off here whoever takes it.
    /// </summary>
    private Fsm? _partnerSprangCage;

    /// <summary>
    /// The bait that the cage of the partner started here, which goes straight from taking control to going off.
    /// </summary>
    private Fsm? _cageSprungByPartner;

    /// <summary>
    /// Where a bait goes instead of the state it is about to go into, or that state itself for anything else.
    /// </summary>
    /// <param name="fsm">The FSM that is changing state.</param>
    /// <param name="toState">The state it is changing into.</param>
    private FsmState RedirectCageBait(Fsm fsm, FsmState toState) {
        if (_checkedMembers.Count == 0 || fsm.Name != CageBaitFsmName) {
            return toState;
        }

        switch (toState.Name) {
            case CageBaitKneelState when _cageSprungByPartner == fsm:
                _cageSprungByPartner = null;
                return fsm.GetState(CageBaitSpringState) ?? toState;
            case CageBaitSpringState when IsCageBait(fsm):
                if (_partnerSprangCage == fsm) {
                    Logger.Info("Took the bait of a cage that went off for the partner already, so it goes off here");
                    return toState;
                }

                if (IsPartnerInCage(fsm)) {
                    SendCageSprung(fsm);
                    return toState;
                }

                Logger.Info("Took the bait of a cage without every other member in it, so it does nothing");
                return fsm.GetState(CageBaitStandState) ?? toState;
            case CageBaitTrailState when IsCageBait(fsm):
                return fsm.GetState(CageBaitRewaitState) ?? toState;
            default:
                return toState;
        }
    }

    /// <summary>
    /// Whether an FSM is the bait of a cage rather than the bait of a trail.
    /// </summary>
    private static bool IsCageBait(Fsm fsm) {
        return fsm.Variables.FindFsmString(CageBaitEventVariableName) is { Value: { Length: > 0 } };
    }

    /// <summary>
    /// Whether every other member of the save stands in the cage of a bait, where it catches them all, so that nobody
    /// is left behind above the fight it starts.
    /// </summary>
    private bool IsPartnerInCage(Fsm bait) {
        if (GetCurrentMarker() is not { } marker) {
            return false;
        }

        var members = GetCheckedMembers();
        if (members.Count < marker.Members.Count) {
            return false;
        }

        var inside = bait.GameObject?.transform.parent?.Find(CageInsidePath);
        var area = inside != null ? inside.GetComponent<Collider2D>() : null;
        if (area == null) {
            return false;
        }

        foreach (var member in members) {
            if (member is not { IsInLocalScene: true, PlayerObject: { } body } || !body.activeInHierarchy ||
                !area.OverlapPoint(body.transform.position)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Tells the partner that the cage of a bait went off with both players in it.
    /// </summary>
    private void SendCageSprung(Fsm bait) {
        if (_checkedMembers.Count == 0 || bait.GameObject is not { } gameObject) {
            return;
        }

        SendToMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.CageSprung,
            Scene = gameObject.scene.name,
            ObjectPath = ScenePath.Get(gameObject.transform)
        });

        Logger.Info("Took the bait of a cage with the partner in it, so it goes off in both games");
    }

    /// <summary>
    /// The partner took the bait of a cage with the local player in it, so the cage here goes off as well.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update, which names the bait by its path in its scene.</param>
    private void OnCageSprung(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker) ||
            !_checkedMembers.Contains(player.Id)) {
            return;
        }

        // Nothing is kept for a room that isn't loaded here: its cage is back waiting the next time it loads
        var bait = ScenePath.Find(update.ObjectPath, update.Scene);
        if (bait == null || !bait.activeInHierarchy) {
            return;
        }

        foreach (var playMakerFsm in bait.GetComponents<PlayMakerFSM>()) {
            if (playMakerFsm.FsmName != CageBaitFsmName || playMakerFsm.Fsm is not { } fsm || !IsCageBait(fsm)) {
                continue;
            }

            _partnerSprangCage = fsm;

            // A local player who is kneeling at the bait takes it in a moment, and it goes off then
            var state = playMakerFsm.ActiveStateName;
            if (state != CageBaitIdleState && state != CageBaitStandState) {
                return;
            }

            Logger.Info($"{player.Username} sprang the cage with both players in it, so it goes off here as well");
            _cageSprungByPartner = fsm;
            playMakerFsm.SetState(CageBaitTakeControlState);
            return;
        }
    }

    /// <summary>
    /// Forgets the cages of the room that was left.
    /// </summary>
    private void OnCageSceneChanged() {
        _partnerSprangCage = null;
        _cageSprungByPartner = null;
    }
}
