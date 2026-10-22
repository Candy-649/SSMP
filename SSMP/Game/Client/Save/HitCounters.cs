using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity;
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
///   it, until the scene host's own player struck it three times. <see cref="Count"/> adds it, and with it what else
///   a hit changes in the creature for good (see <see cref="OutcomeEvents"/>).
/// - Something of the room: each game keeps its own count and takes the partner's hits through the replay of them
///   (<see cref="CoopHits"/>): the gates of spikes that break after three strikes, the cages, the bells.
/// </summary>
internal static class HitCounters {
    /// <summary>
    /// The events of a hit that a state machine of the creature hears of from the partner wherever it goes on them,
    /// not only into a state that counts. Where a heavy blow, a silk skill or an explosion takes a creature is for
    /// good - out of its daze at once, into shattering, into breaking apart - a hit ends a creature's song, for which
    /// another one has to be played to it, a hit that extracts from a creature has it give way to that, and at a
    /// blocked hit a creature blocks, bounces off or strikes back. Census (10-11, FsmScan --events): heavy blows take
    /// 41 bosses and elites out of their daze, break 25 crystal creatures and shatter the boss thorns, which silk
    /// skills and explosions shatter too; about 2300 state machines end a song on a hit, 150 give way to extracting,
    /// and about 870 answer a blocked hit. The partner's hits did none of it in the game that runs the creature: one
    /// that the partner had struck kept singing, stayed dazed and stood unbroken, and one that blocked the partner
    /// never struck back.
    /// </summary>
    private static readonly HashSet<string> OutcomeEvents = [
        "TOOK HEAVY DAMAGE", "SING DURATION END", "TOOK SPELL DAMAGE", "TOOK EXPLOSION DAMAGE", "EXTRACT",
        "BLOCKED HIT", "BLOCKED HIT R", "BLOCKED HIT L", "BLOCKED HIT U", "BLOCKED DOWN"
    ];

    /// <summary>
    /// The field behind the event by which the creature's own components hear of a blow that it lives through
    /// (HealthManager.TookDamage): one that is alert stays so, a crawler at rest starts crawling, and the like. An
    /// event, which only HealthManager raises itself.
    /// </summary>
    private static readonly FieldInfo? TookDamageField = typeof(HealthManager).GetField(
        nameof(HealthManager.TookDamage), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
    );

    /// <summary>
    /// Counts a hit of the partner on a creature that the local game runs, the way the game counts a hit of its own
    /// player (HealthManager.TakeDamage): the state machines that count the events of being hit are told them, those
    /// that a hit changes for good are told those wherever they go on them (see <see cref="OutcomeEvents"/>), its
    /// components that heed a blow hear of it if it lives, and the one that decides whether it loses its stance is
    /// given the beating. What else a hit makes a creature do - turn, flinch, strike back - it is not told of: here the
    /// partner's hit is only seen; the partner's game plays it, and has the creature here do the same
    /// (Entity.FindWoundReactions).
    /// A blow that takes the last of its health is told of once more, before the creature dies
    /// (HealthManagerComponent). That usually comes in the same packet as this, and a thorn that counted it then waits
    /// a moment in which it hears nothing.
    /// A state machine that each game runs by itself is left out: one that shows the hits hears of the partner's
    /// elsewhere, and one that pays the player who struck, like the shards a creature flings, never hears of them, or
    /// the scene host's player was paid for the partner's blows.
    /// A hit that the creature is immune to, or one that extracts from it, never comes here (see
    /// <see cref="CoopHits"/>).
    /// </summary>
    /// <param name="healthManager">The creature.</param>
    /// <param name="hit">The hit of the partner.</param>
    /// <param name="type">The type of the creature's entity.</param>
    public static void Count(HealthManager healthManager, HitInstance hit, EntityType type) {
        foreach (var eventName in EventsOf(healthManager, hit)) {
            Tell(healthManager, type, eventName);
        }

        if (healthManager.hp > 0 && !healthManager.GetIsDead() &&
            TookDamageField?.GetValue(healthManager) is Action tookDamage) {
            tookDamage();
        }

        healthManager.ApplyStunDamage(hit.StunDamage);
    }

    /// <summary>
    /// Tells a creature that the local game runs of a hit of the partner that extracts from it, as the game tells it
    /// of one of its own player (HealthManager.TakeDamage): with that alone, as such a hit does nothing else. The
    /// extracting itself is the business of the partner's game.
    /// </summary>
    /// <param name="healthManager">The creature.</param>
    /// <param name="type">The type of the creature's entity.</param>
    public static void Extract(HealthManager healthManager, EntityType type) {
        Tell(healthManager, type, "EXTRACT");
    }

    /// <summary>
    /// Whether a hit extracts from a creature rather than hurting it (HealthManager.TakeDamage).
    /// </summary>
    public static bool Extracts(HealthManager healthManager, HitInstance hit) {
        return hit.AttackType == AttackTypes.ExtractMoss && healthManager.isBluebloodExtractable;
    }

    /// <summary>
    /// Tells a creature that the local game runs of a hit of the partner that it blocked, the way the game tells it of
    /// one of its own player (HealthManager.Invincible): it keeps the way the hit came from and its kind, its state
    /// machines that a blocked hit takes somewhere are told so - on top of that, by the side it came from, if it is
    /// not shown blocking - and those that count hits are told of a hit, unless it clinks no more for now; and it
    /// evades nothing more. The spark and the clink are played for every game (<see cref="CoopHits"/>), and the
    /// knockback of the player who struck and the shake of their screen belong to their game.
    /// </summary>
    /// <param name="healthManager">The creature.</param>
    /// <param name="hit">The blocked hit of the partner.</param>
    /// <param name="type">The type of the creature's entity.</param>
    /// <param name="direction">The way the hit came from, as one of four.</param>
    /// <param name="clinks">Whether the creature clinks at it, which it does once a while at most.</param>
    public static void Block(
        HealthManager healthManager,
        HitInstance hit,
        EntityType type,
        int direction,
        bool clinks
    ) {
        healthManager.directionOfLastAttack = direction;
        healthManager.lastAttackType = hit.AttackType;
        Tell(healthManager, type, "BLOCKED HIT");

        if (clinks) {
            Tell(healthManager, type, "HIT");

            var box = healthManager.boxCollider != null
                ? healthManager.boxCollider
                : healthManager.GetComponent<BoxCollider2D>();
            if (!healthManager.PreventInvincibleEffect && box != null) {
                Tell(
                    healthManager, type, direction switch {
                        0 => "BLOCKED HIT R",
                        1 => "BLOCKED HIT U",
                        2 => "BLOCKED HIT L",
                        _ => "BLOCKED DOWN"
                    }
                );
            }
        }

        healthManager.evasionByHitRemaining = 0f;
    }

    /// <summary>
    /// Tells the state machines of a creature that the local game runs one event of a hit of the partner: each that
    /// goes somewhere on it if it is one that a hit changes for good, and otherwise each that counts on it. A state
    /// machine that each game runs by itself is left out (see <see cref="Count"/>).
    /// </summary>
    private static void Tell(HealthManager healthManager, EntityType type, string eventName) {
        var outcome = OutcomeEvents.Contains(eventName);
        foreach (var fsm in healthManager.GetComponents<PlayMakerFSM>()) {
            if (!EntityRegistry.IsRunByEachGame(type, fsm.FsmName) &&
                (outcome ? GoesOn(fsm, eventName) is not null : CountsOn(fsm, eventName))) {
                fsm.SendEvent(eventName);
            }
        }
    }

    /// <summary>
    /// The events that the game tells a creature of when a hit lands on it, in the order it tells them.
    /// </summary>
    /// <param name="healthManager">The creature, or null for the events of any.</param>
    /// <param name="hit">The hit.</param>
    public static IEnumerable<string> EventsOf(HealthManager? healthManager, HitInstance hit) {
        if (hit.AttackType == AttackTypes.Heavy) {
            yield return "TOOK HEAVY DAMAGE";
        }

        yield return "HIT";
        yield return "TOOK DAMAGE";

        // But for a creature that the black thread holds in its song
        if (healthManager == null || !healthManager.hasBlackThreadState ||
            !healthManager.blackThreadState.IsInForcedSing) {
            yield return "SING DURATION END";
        }

        if (hit.AttackType == AttackTypes.Spell) {
            yield return "TOOK SPELL DAMAGE";
        }

        if (hit.AttackType == AttackTypes.Explosion) {
            yield return "TOOK EXPLOSION DAMAGE";
        }
    }

    /// <summary>
    /// The state that an event takes a state machine into from where it is, if any: by a global transition first, as
    /// the game goes when it tells a state machine of an event, and otherwise by one of the state it is in.
    /// </summary>
    private static HutongGames.PlayMaker.FsmState? GoesOn(PlayMakerFSM fsm, string eventName) {
        if (fsm.Fsm.ActiveState is not { } state) {
            return null;
        }

        var transition = Array.Find(fsm.FsmGlobalTransitions, t => t.EventName == eventName) ??
                         Array.Find(state.Transitions, t => t.EventName == eventName);
        return transition?.ToFsmState;
    }

    /// <summary>
    /// Whether an event takes a state machine, from where it is, into a state that counts: one that adds to a number
    /// once as it starts. A number added to every frame is a timer, a number worked out of others is an aim or a
    /// speed, and a state that only turns, flinches or strikes back is what the hit made the creature do.
    /// </summary>
    private static bool CountsOn(PlayMakerFSM fsm, string eventName) {
        return GoesOn(fsm, eventName) is { } counting && Counts(counting);
    }

    /// <summary>
    /// Whether a state counts: it adds to a number once as it starts (see <see cref="CountsOn"/>). The game that runs
    /// the creature tells it of the partner's hits, so the partner's game leaves it be (Entity.FindWoundReactions).
    /// </summary>
    internal static bool Counts(HutongGames.PlayMaker.FsmState state) {
        foreach (var action in state.Actions) {
            if (action.Enabled &&
                action is IntAdd { everyFrame: false } or FloatAdd { everyFrame: false, perSecond: false }) {
                return true;
            }
        }

        return false;
    }
}
