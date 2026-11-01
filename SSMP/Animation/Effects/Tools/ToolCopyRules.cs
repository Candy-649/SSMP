using System.Collections.Generic;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// Which things of the tools are copied for the partner, and which changes of their state machines only the thrower's
/// game can know of. A copy runs by itself whatever goes the same way on both screens - flying, bouncing off the walls
/// of the room, timers - and takes from the thrower's game what depends on anything else: an enemy it touched, an
/// attack that hit it, a limit of the thrower's that broke it. Those changes go from the thrower's game as they happen,
/// and a copy never makes them itself.
/// </summary>
internal static class ToolCopyRules {
    /// <summary>
    /// The tools whose thrown things are copied, by the name of the tool.
    /// </summary>
    public static readonly HashSet<string> ThrownTools = [
        "Sting Shard",
        "Curve Claws",
        "Curve Claws Upgraded",
        "Conch Drill",
        "Cogwork Saw",
        "Pimpilo",
        "Dustpilo",
        "Lightning Rod",
        "Shakra Ring",
        "Cogwork Flier"
    ];

    /// <summary>
    /// The things that the hero's own handling of their tools spawns, rather than throws, that are copied, by the name
    /// of their prefab: what those tools shoot, set down or leave behind, and the effects they make around the hero;
    /// and what the hero spawns with a tool on as they are hurt, the crystal that forms where they were hurt.
    /// </summary>
    public static readonly HashSet<string> HeroToolSpawns = [
        "WebShot Bullet",
        "WebShot Bullet A",
        "WebShot Casing F",
        "WebShot Casing A",
        "WebShot Casing W",
        "White Flash R",
        "Geo Small Projectile",
        "Tool Lightning Rod",
        "Silk Snare",
        "Silk Snare Poison",
        "Silk Snare Cartridge",
        "Silk Snare Set Effect",
        "Silk Snare Set Effect Poison Variant",
        "Weaver_snare_dive_effect",
        "Weaver_snare_dive_effect_poison",
        "Tool SyringeEmpty",
        "Drill Down Black Rock Hit",
        "Blue_Health_Overblue_burst",
        "Soft Land Effect",
        "Jump Effects",
        "Flint Effects",
        "Flint Effects Poison",
        "Revenge Crystal Hornet"
    ];

    /// <summary>
    /// The things that stay with the hero while they last, by the name of their prefab, which the copy does with the
    /// character of the thrower.
    /// </summary>
    public static readonly HashSet<string> FollowsHero = [
        "Silk Snare Set Effect",
        "Silk Snare Set Effect Poison Variant",
        "Weaver_snare_dive_effect",
        "Weaver_snare_dive_effect_poison",
        "Blue_Health_Overblue_burst"
    ];

    /// <summary>
    /// The events of the bombs that come from anything but the room: an enemy, the attacks that knock them about, or a
    /// boss or a hazard that blows them up or away.
    /// </summary>
    private static readonly HashSet<string> BombEvents = [
        "ENEMY",
        "INSTA EXPLODE",
        "TINK DOWN L",
        "TINK DOWN R",
        "TINK LEFT",
        "TINK RIGHT",
        "TINK UP",
        "TORNADO",
        "SWALLOW",
        "DISABLE"
    ];

    /// <summary>
    /// The events that only the thrower's game can know of, by the name of the prefab of the thing and the name of its
    /// state machine.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, HashSet<string>>> ThrowerEvents = new() {
        ["Tool Barb"] = new() {
            ["Control"] = [
                "SPRING",
                "TINK",
                "TINK DOWN",
                "BREAK",
                "PROJECTILE",
                "TORNADO",
                "LAVA",
                "BREAK HERO PROJECTILE"
            ]
        },
        ["Hero Conch Projectile"] = new() {
            ["Control"] = [
                "BREAK",
                "TORNADO",
                "BOUNCE TINKED DOWN",
                "BOUNCE TINKED UP",
                "BOUNCE TINKED LEFT",
                "BOUNCE TINKED RIGHT",
                "TINK UP",
                "TINK DOWN",
                "TINK LEFT",
                "TINK RIGHT"
            ]
        },
        ["Tool Wheel"] = new() {
            ["Control"] = ["BREAK", "TORNADO", "LAVA", "SPIKES"],
            ["Damage Slowdown"] = ["HIT LANDED"],
            ["Tink React"] = ["TINK DOWN", "TINK LEFT", "TINK RIGHT", "TINK UP"]
        },
        ["Tool Bomb"] = new() {
            ["Control"] = BombEvents
        },
        ["Tool Dust Bomb New"] = new() {
            ["Control"] = BombEvents
        },
        ["Tool Lightning Bola"] = new() {
            ["Control"] = ["DAMAGED ENEMY", "BREAK", "SWALLOW", "TORNADO"]
        },
        ["Tool Lightning Rod"] = new() {
            ["Control"] = ["NAIL STRIKE", "TORNADO", "LAVA"]
        },
        ["WebShot Bullet"] = new() {
            ["Break On Tink"] = ["DAMAGER TINKED"]
        },
        ["WebShot Bullet A"] = new() {
            ["Break On Tink"] = ["DAMAGER TINKED"]
        },
        // An enemy that came near, the thrower's own attack, one crystal too many of the thrower's, and the thrower's
        // game clearing the effects away
        ["Revenge Crystal Hornet"] = new() {
            ["FSM"] = ["ATTACK", "NAIL HIT", "SPAWN DEQUEUED", "CLEAR EFFECTS"]
        }
    };

    /// <summary>
    /// The changes of state that a copy waits for from the thrower's game, in one state only, as the state it changes
    /// to rolls what the copy must show the same, or as the thing stops at a place that only the thrower's game knows:
    /// by the name of the prefab of the thing, the name of its state machine, the state and the event. The crystal rolls
    /// its tilt as it starts to form, stays where the hurt hero was a moment later, and rolls its look as it shows.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, HashSet<(string State, string Event)>>>
        ThrowerStateEvents = new() {
            ["Revenge Crystal Hornet"] = new() {
                ["FSM"] = [("Init Wait", "FINISHED"), ("Appear Antic 1", "FINISHED"), ("Appear Begin", "FINISHED")]
            }
        };

    /// <summary>
    /// The events of each state machine that only the thrower's game can know of, as put together once.
    /// </summary>
    private static readonly Dictionary<(string Prefab, string Fsm), ThrowerEventSet?> EventSets = new();

    /// <summary>
    /// The state machines of things that take their place and facing from the hero who made them, by the global
    /// variable of the game's hero, by the name of the prefab of the thing: on a copy they take them from the character
    /// of the thrower. Elsewhere "the hero" can be whoever struck the thing, which on a copy is this player, like the
    /// spark of the rod that faces the attack that struck it, so only these do. The crystal forms on the hero who was
    /// hurt and faces their way.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> TakesPlaceFromThrower = new() {
        ["Revenge Crystal Hornet"] = ["FSM"]
    };

    /// <summary>
    /// Whether a state machine of a copied thing takes its place and facing from the character of the thrower where
    /// the thing takes them from the hero (see <see cref="TakesPlaceFromThrower"/>).
    /// </summary>
    /// <param name="prefabName">The name of the prefab of the thing.</param>
    /// <param name="fsmName">The name of the state machine.</param>
    public static bool TakesPlaceFromTheThrower(string prefabName, string fsmName) {
        return TakesPlaceFromThrower.TryGetValue(prefabName, out var fsms) && fsms.Contains(fsmName);
    }

    /// <summary>
    /// The numbers, whole or not, that a state machine of a thing had when it changed state for a reason that only the
    /// thrower's game knows of, which the copy decides by afterwards, by the name of the prefab of the thing and the name
    /// of the state machine. A copy never rolls these itself. The rod turns the way the thrower's attack struck it; the
    /// crystal has the tilt and the look that the thrower's rolled.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string[]>> CarriedFloats = new() {
        ["Tool Lightning Rod"] = new() {
            ["Control"] = ["Hit Direction"]
        },
        ["Revenge Crystal Hornet"] = new() {
            ["FSM"] = ["Rotation", "Num"]
        }
    };

    /// <summary>
    /// The things that are moved by their own code rather than by a state machine, by the name of their prefab, with
    /// the state that goes from the thrower's game whenever something that only it knows of changed them.
    /// </summary>
    private static readonly Dictionary<string, IToolState> States = new() {
        ["Curve Claw"] = ClawState.Instance,
        ["Curve Claw Upgraded"] = ClawState.Instance,
        ["Hero Shakra Ring"] = RingState.Instance,
        ["Clockwork Hatchling"] = FlierState.Instance,
        ["Geo Small Projectile"] = PelletState.Instance,
        ["Silk Snare"] = SnareState.Instance,
        ["Silk Snare Poison"] = SnareState.Instance,
        ["Flint Effects"] = FlintState.Instance,
        ["Flint Effects Poison"] = FlintState.Instance
    };

    /// <summary>
    /// The things whose state the thrower's game sends all the time rather than when it changes, by the name of their
    /// prefab, with the seconds between two sends.
    /// </summary>
    private static readonly Dictionary<string, float> StreamIntervals = new() {
        ["Clockwork Hatchling"] = 0.1f
    };

    /// <summary>
    /// The seconds between two sends of the state of a thing whose state goes all the time, or 0 for a thing whose
    /// state goes when it changes.
    /// </summary>
    public static float GetStreamInterval(string prefabName) {
        return StreamIntervals.TryGetValue(prefabName, out var interval) ? interval : 0f;
    }

    /// <summary>
    /// The state of a copied thing that is moved by its own code, or null for a thing that is not.
    /// </summary>
    public static IToolState? GetState(string prefabName) {
        return States.TryGetValue(prefabName, out var state) ? state : null;
    }

    /// <summary>
    /// The events of a state machine of a copied thing that only the thrower's game can know of.
    /// </summary>
    /// <param name="prefabName">The name of the prefab of the thing.</param>
    /// <param name="fsmName">The name of the state machine.</param>
    /// <returns>The events, or null if there are none.</returns>
    public static ThrowerEventSet? GetThrowerEvents(string prefabName, string fsmName) {
        if (EventSets.TryGetValue((prefabName, fsmName), out var set)) {
            return set;
        }

        var inAnyState = ThrowerEvents.TryGetValue(prefabName, out var fsms) &&
                         fsms.TryGetValue(fsmName, out var events)
            ? events
            : null;
        var inOneState = ThrowerStateEvents.TryGetValue(prefabName, out var stateFsms) &&
                         stateFsms.TryGetValue(fsmName, out var stateEvents)
            ? stateEvents
            : null;
        set = inAnyState == null && inOneState == null ? null : new ThrowerEventSet(inAnyState, inOneState);
        EventSets[(prefabName, fsmName)] = set;
        return set;
    }

    /// <summary>
    /// The names of the numbers, whole or not, of a state machine of a copied thing that go with its changes of state
    /// from the thrower's game, in the order they are sent.
    /// </summary>
    /// <param name="prefabName">The name of the prefab of the thing.</param>
    /// <param name="fsmName">The name of the state machine.</param>
    /// <returns>The names, or none.</returns>
    public static string[] GetCarriedFloats(string prefabName, string fsmName) {
        return CarriedFloats.TryGetValue(prefabName, out var fsms) && fsms.TryGetValue(fsmName, out var names)
            ? names
            : [];
    }
}

/// <summary>
/// The events of a state machine of a copied thing that only the thrower's game can know of: some in whatever state the
/// state machine is in, and some in one state only.
/// </summary>
internal sealed class ThrowerEventSet {
    /// <summary>
    /// The events in whatever state.
    /// </summary>
    private readonly HashSet<string> _inAnyState;

    /// <summary>
    /// The events in one state only, with that state.
    /// </summary>
    private readonly HashSet<(string State, string Event)> _inOneState;

    public ThrowerEventSet(HashSet<string>? inAnyState, HashSet<(string State, string Event)>? inOneState) {
        _inAnyState = inAnyState ?? [];
        _inOneState = inOneState ?? [];
    }

    /// <summary>
    /// Whether an event that comes in the given state is one that only the thrower's game can know of.
    /// </summary>
    /// <param name="state">The name of the state the state machine is in.</param>
    /// <param name="eventName">The name of the event.</param>
    public bool Has(string? state, string eventName) {
        return _inAnyState.Contains(eventName) || IsInOneStateOnly(state, eventName);
    }

    /// <summary>
    /// Whether an event that comes in the given state is one that only the thrower's game can know of in that state
    /// only: the thrower's game sends the change it makes there each time, rather than only when something happens.
    /// </summary>
    /// <param name="state">The name of the state the state machine is in.</param>
    /// <param name="eventName">The name of the event.</param>
    public bool IsInOneStateOnly(string? state, string eventName) {
        return _inOneState.Count > 0 && state != null && !_inAnyState.Contains(eventName) &&
               _inOneState.Contains((state, eventName));
    }
}
