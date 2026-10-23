using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Has a creature that this game runs answer a wound at the player who dealt it, whichever player it goes after (USER
/// 10-22: "问题是主机那边也应该朝打它的那个人那里飞啊，凭啥改客机的反应啊"). What a wound takes a creature to - a turn to the
/// one who struck it, a flight off to a distance from them, a dodge away - aims at the player it goes after, and one
/// that went after one player answered the other one's blows at the first: in the game of the player who struck it,
/// its copy flew off from them at once (Entity.PlayHere), while the creature here flew off from the other player, and
/// the copy was put back where the creature was as soon as it went on.
///
/// Now the creature goes after the player who dealt a wound from the moment the wound plays. One that the wound took
/// nowhere goes back to whom it went after at once. One that fought no player yet goes on after them, as one does in a
/// game of one, and so does one that the wound took into a new move, which it started against them. One that fights
/// another player goes back to them as it starts its next move, where it rolls whom it turns to as at any other
/// (TurnAtMoveStart): what the wound takes it through until then - a flinch and the dodge after it, a flight off and
/// the hovering that follows - goes by the player who dealt it.
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// A wound that a creature answers at the player who dealt it, until it starts its next move.
    /// </summary>
    private sealed class WoundAnswer {
        /// <summary>
        /// Whom the creature went after before the wound, to give back (see <see cref="TargetHoldOf"/>).
        /// </summary>
        public readonly TargetHold Hold;

        /// <summary>
        /// The player who dealt the wound, the last one if more than one did.
        /// </summary>
        public GameObject Striker;

        /// <summary>
        /// Makes the answer to a wound.
        /// </summary>
        /// <param name="hold">Whom the creature went after before the wound.</param>
        /// <param name="striker">The player who dealt the wound.</param>
        public WoundAnswer(TargetHold hold, GameObject striker) {
            Hold = hold;
            Striker = striker;
        }
    }

    /// <summary>
    /// The wounds that creatures answer, by the instance ID of the creature (see <see cref="GetEnemyTargetOwner"/>).
    /// </summary>
    private static readonly Dictionary<int, WoundAnswer> WoundAnswers = new();

    /// <summary>
    /// The creature that a wound is played on now (see <see cref="AnswerWound"/>), or null.
    /// </summary>
    private static GameObject? _woundedNow;

    /// <summary>
    /// For each FSM of the creature that a wound is played on now, the state it was in before the wound and the last
    /// state the wound took it to.
    /// </summary>
    private static readonly Dictionary<HutongGames.PlayMaker.Fsm, (FsmState? From, FsmState To)> WoundPaths = new();

    /// <summary>
    /// Whether the wound that is played now took an FSM of the creature into a state where it picks a move at random.
    /// </summary>
    private static bool _woundStartedAMove;

    /// <summary>
    /// Plays a wound of a player on a creature that this game runs, which the creature answers at that player (see
    /// above). Nothing changes for one that goes after them already.
    /// </summary>
    /// <param name="creature">The creature, or one of its parts.</param>
    /// <param name="striker">The player who dealt the wound: this game's player, or the figure of another one.</param>
    /// <param name="play">What plays the wound.</param>
    internal static void AnswerWound(GameObject creature, GameObject striker, Action play) {
        var owner = GetEnemyTargetOwner(creature);
        if (owner == null || striker == null || _woundedNow != null) {
            play();
            return;
        }

        var id = owner.GetInstanceID();
        WoundAnswers.TryGetValue(id, out var answer);
        if (answer == null && (GetFsmActionTarget(owner) ?? LocalPlayerObject()) == striker) {
            play();
            return;
        }

        var fights = FightsAPlayer(owner);
        var isNew = answer == null;
        answer ??= new WoundAnswer(TargetHoldOf(owner)!, striker);
        answer.Striker = striker;
        WoundAnswers[id] = answer;
        EnemyApprovedTargets[id] = striker;

        _woundedNow = owner;
        WoundPaths.Clear();
        _woundStartedAMove = false;
        var played = false;
        try {
            play();
            played = true;
        } finally {
            _woundedNow = null;

            // A wound that went wrong as it played leaves the creature going after whom it went after
            if (!played) {
                WoundPaths.Clear();
                _woundStartedAMove = false;
                if (isNew) {
                    EndWoundAnswer(owner, id, null);
                }
            }
        }

        // Where it went, unless back to where it was before the wound, like a count of the blows that went up by one
        // and back to waiting for the next
        var moved = false;
        foreach (var path in WoundPaths.Values) {
            if (path.To != path.From) {
                moved = true;
                break;
            }
        }

        var startedAMove = _woundStartedAMove;
        WoundPaths.Clear();
        _woundStartedAMove = false;

        // Gone with the creature, or with the player it went after, as the wound played
        if (!WoundAnswers.TryGetValue(id, out var still) || still != answer) {
            return;
        }

        if (!moved) {
            if (isNew) {
                EndWoundAnswer(owner, id, null);
            }

            return;
        }

        // Set off by the player who struck it, or into a move against them: it goes after them from here, as if it had
        // turned to them as it started the move
        if (!fights || startedAMove) {
            WoundAnswers.Remove(id);
            PutBackWhomItWentAfter(id, answer.Hold);
            Logger.Info(
                $"{owner.name} goes after {DescribePlayer(striker)}, whose wound " +
                (startedAMove ? "took it into a new move" : "set it off while it fought no player")
            );
            ApproveEnemyTarget(owner, striker);
            return;
        }

        if (isNew) {
            Logger.Info(
                $"{owner.name} answers the wound of {DescribePlayer(striker)} at them, and goes after " +
                $"{DescribeHeld(answer.Hold)} again as it starts its next move"
            );
        }
    }

    /// <summary>
    /// Notes a state that an FSM of the creature that a wound is played on goes into as it plays (see
    /// <see cref="AnswerWound"/>): where the wound takes the FSM, and whether it starts a move.
    /// </summary>
    /// <param name="state">The state.</param>
    private static void NoteWoundState(FsmState state) {
        if (_woundedNow == null || state.Fsm is not { } fsm || fsm.FsmComponent is not { } machine || machine == null ||
            !machine.transform.IsChildOf(_woundedNow.transform)) {
            return;
        }

        var from = WoundPaths.TryGetValue(fsm, out var path) ? path.From : fsm.PreviousActiveState;
        WoundPaths[fsm] = (from, state);
        _woundStartedAMove |= PicksAMove(state) && AimsAtPlayers(machine);
    }

    /// <summary>
    /// Gives a creature that answers a wound back whom it went after before (see <see cref="AnswerWound"/>), if it
    /// answers one.
    /// </summary>
    /// <param name="owner">The creature.</param>
    /// <param name="id">Its instance ID.</param>
    /// <param name="why">What it does that ends the answer, for the log, or null to say nothing.</param>
    private static void EndWoundAnswer(GameObject owner, int id, string? why) {
        if (!WoundAnswers.Remove(id, out var answer)) {
            return;
        }

        GiveBackTarget(owner, answer.Hold);
        if (why != null) {
            Logger.Info(
                $"{owner.name} {why} and goes after {DescribeHeld(answer.Hold)} again after the wound of " +
                (answer.Striker != null ? DescribePlayer(answer.Striker) : "a player who has gone")
            );
        }
    }

    /// <summary>
    /// Forgets the wounds that creatures answer after which they would go back to a player who has gone down: each goes
    /// on after the player who dealt its wound, who still stands.
    /// </summary>
    /// <param name="player">The player who went down.</param>
    private static void ForgetWoundAnswersBackTo(GameObject player) {
        List<int>? ids = null;
        foreach (var pair in WoundAnswers) {
            if (pair.Value.Hold.Target == player) {
                (ids ??= []).Add(pair.Key);
            }
        }

        foreach (var id in ids ?? []) {
            WoundAnswers.Remove(id);
        }
    }

    /// <summary>
    /// Puts back whom a creature went after and how it had turned, as a hold says, without
    /// pointing what it aims at anywhere: for a creature that is turned to a player at once after.
    /// </summary>
    /// <param name="id">The instance ID of the creature.</param>
    /// <param name="hold">Whom it went after (see <see cref="TargetHoldOf"/>).</param>
    private static void PutBackWhomItWentAfter(int id, TargetHold hold) {
        if (hold.Turns is { } before && Turns.TryGetValue(id, out var turns)) {
            turns.PutBack(before);
        }

        if (hold.Target is { } target && target != null) {
            EnemyApprovedTargets[id] = target;
        } else {
            EnemyApprovedTargets.Remove(id);
        }
    }

    /// <summary>
    /// Says whom a creature went after before a wound, for the log.
    /// </summary>
    /// <param name="hold">Whom it went after (see <see cref="TargetHoldOf"/>).</param>
    private static string DescribeHeld(TargetHold hold) {
        return hold.Target is { } target && target != null ? DescribePlayer(target) : "no one in particular";
    }
}
