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
    /// of their prefab: what those tools shoot, set down or leave behind, and the effects they make around the hero.
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
        "Flint Effects Poison"
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
        }
    };

    /// <summary>
    /// The numbers that a state machine of a thing had when it changed state for a reason that only the thrower's game
    /// knows of, which the copy decides by afterwards, by the name of the prefab of the thing and the name of the
    /// state machine. The rod turns the way the thrower's attack struck it.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string[]>> CarriedFloats = new() {
        ["Tool Lightning Rod"] = new() {
            ["Control"] = ["Hit Direction"]
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
    public static HashSet<string>? GetThrowerEvents(string prefabName, string fsmName) {
        return ThrowerEvents.TryGetValue(prefabName, out var fsms) && fsms.TryGetValue(fsmName, out var events)
            ? events
            : null;
    }

    /// <summary>
    /// The names of the numbers of a state machine of a copied thing that go with its changes of state from the
    /// thrower's game, in the order they are sent.
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
