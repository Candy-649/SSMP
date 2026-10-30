using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace SSMP.Game.Client.Save;

/// <summary>
/// What the state machines of the things of the room that take the players' hits do with them: in which states they wait
/// for a hit, whether they count the hits, and where they go by themselves after one. The walls, doors and vines that give
/// way after a number of strikes are of these. Only for a state machine that has started: the actions of one that has not
/// are not loaded yet, and loading them early breaks it.
/// </summary>
internal static class HitMachines {
    /// <summary>
    /// The tag of the player's attacks, which the things that break listen for when they are touched.
    /// </summary>
    public const string NailAttackTag = "Nail Attack";

    /// <summary>
    /// The event by which a state machine goes on by itself once a state is done.
    /// </summary>
    public const string FinishedEventName = "FINISHED";

    /// <summary>
    /// Whether a state waits for the player's hits: by the damage that they deal, or by their touch.
    /// </summary>
    public static bool ListensForHits(FsmState state) {
        foreach (var action in state.Actions) {
            if (action.Enabled && action is ReceivedDamageBase or Trigger2dEvent { collideTag.Value: NailAttackTag }) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an action counts something once each time its state is entered, the way a thing that breaks after a
    /// number of hits counts them.
    /// </summary>
    public static bool IsCount(FsmStateAction action) {
        return action.Enabled && action is IntAdd { everyFrame: false } or IntOperator { everyFrame: false };
    }

    /// <summary>
    /// Whether a state machine counts the player's hits: it waits for them somewhere and counts something. A lever, a
    /// platform that drops or a reminder that a strike sets off counts nothing: the one hit sets it.
    /// </summary>
    public static bool CountsHits(PlayMakerFSM fsm) {
        var listens = false;
        var counts = false;
        foreach (var state in fsm.FsmStates) {
            listens |= ListensForHits(state);
            foreach (var action in state.Actions) {
                counts |= IsCount(action);
            }

            if (listens && counts) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The state that an event takes a state machine to from a state, by a transition of that state, or null.
    /// </summary>
    public static FsmState? FindTarget(PlayMakerFSM fsm, FsmState state, string eventName) {
        var transition = Array.Find(state.Transitions, t => t.EventName == eventName);
        return transition == null ? null : transition.ToFsmState ?? fsm.Fsm.GetState(transition.ToState);
    }

    /// <summary>
    /// The first state that a state machine in a state comes to by itself, once each state on the way is done, of those
    /// for which a test holds. Null if it comes to none: it stops on the way, or goes round.
    /// </summary>
    public static FsmState? FindOnWay(PlayMakerFSM fsm, FsmState from, Func<FsmState, bool> stopsAt) {
        var seen = new HashSet<FsmState> { from };
        var state = from;
        while (FindTarget(fsm, state, FinishedEventName) is { } next && seen.Add(next)) {
            if (stopsAt(next)) {
                return next;
            }

            state = next;
        }

        return null;
    }
}
