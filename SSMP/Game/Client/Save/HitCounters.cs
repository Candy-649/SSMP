using System.Collections.Generic;
using UnityEngine;

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
///   it, until the scene host's own player struck it three times. <see cref="Count"/> tells the creature of it as of a
///   hit of the scene host's own player, which it counts, and answers, the same.
/// - Something of the room: each game keeps its own count and takes the partner's hits through the replay of them
///   (<see cref="CoopHits"/>): the gates of spikes that break after three strikes, the cages, the bells.
/// </summary>
internal static class HitCounters {
    /// <summary>
    /// Tells a creature that the local game runs of a hit of the partner, the way the game tells it of a hit of its own
    /// player (HealthManager.TakeDamage): its state machines hear the events of being hit, and the one that decides
    /// whether it loses its stance is given the beating. So it counts the hit, and it answers it as it answers one of
    /// this game's player: it shakes, sparks and sounds, wakes, turns and gives chase, and stops a song. Told of the
    /// partner's hits only where they were counted, a creature answered none of them in either game: a nest that
    /// sparked and sounded at every blow of the scene host's player stayed silent under the partner's, which felt to
    /// them like striking a wall. What it answers with goes to the partner like anything else the creature does, a
    /// round trip after their blow.
    /// A blow that takes the last of its health is told of once more, before the creature dies
    /// (HealthManagerComponent). That usually comes in the same packet as this, and a thorn that counted it then waits
    /// a moment in which it hears nothing.
    /// </summary>
    /// <param name="healthManager">The creature.</param>
    /// <param name="hit">The hit of the partner.</param>
    public static void Count(HealthManager healthManager, HitInstance hit) {
        foreach (var (told, eventName) in EventsOf(healthManager, hit)) {
            FSMUtility.SendEventToGameObject(told, eventName, false);
        }

        healthManager.ApplyStunDamage(hit.StunDamage);
    }

    /// <summary>
    /// The events that the game tells of a hit that lands on a creature, in the order it tells them, each with what it
    /// tells: the creature, and what the creature passes its hits on to.
    /// </summary>
    private static IEnumerable<(GameObject Told, string EventName)> EventsOf(
        HealthManager healthManager,
        HitInstance hit
    ) {
        var creature = healthManager.gameObject;
        if (hit.AttackType == AttackTypes.Heavy) {
            yield return (creature, "TOOK HEAVY DAMAGE");
        }

        yield return (creature, "HIT");
        yield return (creature, "TOOK DAMAGE");

        if (!healthManager.hasBlackThreadState || !healthManager.blackThreadState.IsInForcedSing) {
            yield return (creature, "SING DURATION END");
        }

        if (healthManager.sendHitTo != null) {
            yield return (healthManager.sendHitTo, "HIT");
        }

        if (hit.AttackType == AttackTypes.Spell) {
            yield return (creature, "TOOK SPELL DAMAGE");
        }

        if (hit.AttackType == AttackTypes.Explosion) {
            yield return (creature, "TOOK EXPLOSION DAMAGE");
        }
    }
}
