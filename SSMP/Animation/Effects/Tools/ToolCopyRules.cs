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
        "Lightning Rod"
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
        }
    };

    /// <summary>
    /// The things that are moved by their own code rather than by a state machine, by the name of their prefab, with
    /// the state that goes from the thrower's game whenever something that only it knows of changed them.
    /// </summary>
    private static readonly Dictionary<string, IToolState> States = new() {
        ["Curve Claw"] = ClawState.Instance,
        ["Curve Claw Upgraded"] = ClawState.Instance
    };

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
}
