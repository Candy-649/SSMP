using System;
using System.Collections.Generic;
using HutongGames.PlayMaker.Actions;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The things that count hits. Each count is kept in one game and takes the hits of both players: the game of the
/// player who struck notices the hit, and the game that keeps the count adds it. The cocoon of a player waiting to be
/// pulled back up has always been counted like this (RescueHit in <see cref="CoopSave"/>), and so is the rest:
/// - A cocoon: the game of the player inside keeps the count, and only the partner hits it.
/// - A creature that both games show: the scene host's game runs it and keeps its counts. The thorns a boss grows
///   break after three hits, a boss that was knocked out of its stance comes to again the sooner the more it is hit,
///   and the stance breaks after enough of a beating in a short while. The partner's hit reached the scene host as
///   health and a picture of the hit, and none of these counted it: a thorn stood however often the partner struck
///   it, until the scene host's own player struck it three times. <see cref="Count"/> adds it.
/// - Something of the room: each game keeps its own count and takes the partner's hits through the replay of them
///   (<see cref="CoopHits"/>): the gates of spikes that break after three strikes, the cages, the bells.
/// </summary>
internal static class HitCounters {
    /// <summary>
    /// Counts a hit of the partner on a creature that the local game runs, the way the game counts a hit of its own
    /// player (HealthManager.TakeDamage): the state machines that count the events of being hit are told them, and the
    /// one that decides whether the creature loses its stance is given the beating. What else a hit makes a creature
    /// do - turn, flinch, strike back - it is not told of: here the partner's hit is only seen.
    /// A blow that takes the last of its health is told of once more, before the creature dies
    /// (HealthManagerComponent). That usually comes in the same packet as this, and a thorn that counted it then waits
    /// a moment in which it hears nothing.
    /// </summary>
    /// <param name="healthManager">The creature.</param>
    /// <param name="hit">The hit of the partner.</param>
    public static void Count(HealthManager healthManager, HitInstance hit) {
        foreach (var eventName in EventsOf(hit)) {
            foreach (var fsm in healthManager.GetComponents<PlayMakerFSM>()) {
                if (CountsOn(fsm, eventName)) {
                    fsm.SendEvent(eventName);
                }
            }
        }

        healthManager.ApplyStunDamage(hit.StunDamage);
    }

    /// <summary>
    /// The events that the game tells a creature of when a hit lands on it, in the order it tells them.
    /// </summary>
    public static IEnumerable<string> EventsOf(HitInstance hit) {
        if (hit.AttackType == AttackTypes.Heavy) {
            yield return "TOOK HEAVY DAMAGE";
        }

        yield return "HIT";
        yield return "TOOK DAMAGE";

        if (hit.AttackType == AttackTypes.Spell) {
            yield return "TOOK SPELL DAMAGE";
        }

        if (hit.AttackType == AttackTypes.Explosion) {
            yield return "TOOK EXPLOSION DAMAGE";
        }
    }

    /// <summary>
    /// Whether an event takes a state machine, from where it is, into a state that counts: one that adds to a number
    /// once as it starts. A number added to every frame is a timer, a number worked out of others is an aim or a
    /// speed, and a state that only turns, flinches or strikes back is what the hit made the creature do.
    /// </summary>
    private static bool CountsOn(PlayMakerFSM fsm, string eventName) {
        if (fsm.Fsm.ActiveState is not { } state) {
            return false;
        }

        // The global transitions come first, as they do when the game tells a state machine of an event
        var transition = Array.Find(fsm.FsmGlobalTransitions, t => t.EventName == eventName) ??
                         Array.Find(state.Transitions, t => t.EventName == eventName);
        if (transition?.ToFsmState is not { } counting) {
            return false;
        }

        foreach (var action in counting.Actions) {
            if (action.Enabled &&
                action is IntAdd { everyFrame: false } or FloatAdd { everyFrame: false, perSecond: false }) {
                return true;
            }
        }

        return false;
    }
}
