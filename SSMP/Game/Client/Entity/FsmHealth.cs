using System.Collections.Generic;
using UnityEngine;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// The health of a boss that its FSMs keep instead of a HealthManager, for its bar (Ui.EnemyHealthBars): the one whose
/// fight is first against the burning parts of its body, each with health of its own, and then against its core, which
/// opens once they are all broken (census of every boss the user named, 11-10; USER 11-10 "你的血条要能覆盖这些boss",
/// "一阶段是打几个着火的地方，二阶段才是boss本体好像"). Each breaks when its health is gone or after a number of blows,
/// whichever comes first, and its bar shows what is left by the nearer of the two (see <see cref="LeftOf"/>). Read from
/// those FSMs by the game that runs the boss's copy, which tells the other games (see Component.FsmHealthComponent),
/// and by every game for a boss that every game runs by itself, whose parts take the blows of every player there (see
/// OwnCreatures.FsmHealths).
/// </summary>
internal class FsmHealth {
    /// <summary>
    /// The FSM of each burning part, with its health in <see cref="HealthVariable"/>.
    /// </summary>
    public const string PartFsmName = "wisp_brazier_arm";

    /// <summary>
    /// The FSM of the core, on the boss's own object, with its health in <see cref="HealthVariable"/>: it waits in
    /// <see cref="ClosedState"/> until the core opens, and ends in <see cref="BrokenState"/>, which can come before its
    /// health is gone, after a number of blows.
    /// </summary>
    private const string CoreFsmName = "Take Damage";

    /// <summary>
    /// The variable of the health of a burning part and of the core.
    /// </summary>
    private const string HealthVariable = "HP";

    /// <summary>
    /// The variable of the blows that a burning part and the core counted.
    /// </summary>
    private const string HitsVariable = "Total Times Hit";

    /// <summary>
    /// The blows that break a burning part whatever health it has left: its FSM breaks it once it counted more than
    /// one fewer, in its state 'Hit'. It counts each blow it takes at rest, not those as it swings back.
    /// </summary>
    private const int PartHits = 12;

    /// <summary>
    /// The blows that break the core whatever health it has left, as <see cref="PartHits"/>: it counts one a half
    /// second at most.
    /// </summary>
    private const int CoreHits = 30;

    /// <summary>
    /// The state of the core's FSM while the core is closed.
    /// </summary>
    private const string ClosedState = "Init";

    /// <summary>
    /// The state of the core's FSM in which the core breaks, and the boss with it.
    /// </summary>
    private const string BrokenState = "Break";

    /// <summary>
    /// The FSMs of the burning parts, with the health each has at the start.
    /// </summary>
    private readonly List<(PlayMakerFSM Fsm, int Full)> _parts = [];

    /// <summary>
    /// The FSM of the core.
    /// </summary>
    private readonly PlayMakerFSM? _core;

    /// <summary>
    /// Reads the FSMs of the given object of the boss.
    /// </summary>
    public FsmHealth(GameObject creature) {
        Creature = creature;
        foreach (var fsm in creature.GetComponentsInChildren<PlayMakerFSM>(true)) {
            if (fsm.FsmName == PartFsmName) {
                var full = fsm.FsmVariables.GetFsmInt(HealthVariable).Value;
                _parts.Add((fsm, full));
                PartsFull += full;
            } else if (fsm.FsmName == CoreFsmName && fsm.gameObject == creature) {
                _core = fsm;
                CoreFull = fsm.FsmVariables.GetFsmInt(HealthVariable).Value;
            }
        }
    }

    /// <summary>
    /// The object of the boss whose FSMs it reads.
    /// </summary>
    public GameObject Creature { get; }

    /// <summary>
    /// The health that the burning parts have together at the start, as the room was made: the full health of the
    /// first part of the fight.
    /// </summary>
    public int PartsFull { get; }

    /// <summary>
    /// The health of the core as the room was made: the full health of the second part of the fight.
    /// </summary>
    public int CoreFull { get; }

    /// <summary>
    /// The full health of the part of the fight that is on.
    /// </summary>
    public int Full => CoreOpen ? CoreFull : PartsFull;

    /// <summary>
    /// What is left: of the burning parts together, or of the core once it is open (see <see cref="LeftOf"/>); -1
    /// before it was read or told.
    /// </summary>
    public int Health { get; set; } = -1;

    /// <summary>
    /// Whether the core is open, the second part of the fight.
    /// </summary>
    public bool CoreOpen { get; set; }

    /// <summary>
    /// Whether the boss is beaten: its core open and broken.
    /// </summary>
    public bool IsBeaten => CoreOpen && Health == 0;

    /// <summary>
    /// Whether a blow landed on the boss in its fight, as far as its health says.
    /// </summary>
    public bool WasHit => CoreOpen || Health >= 0 && Health < PartsFull;

    /// <summary>
    /// Whether the object has the FSMs of such a boss: burning parts and a core.
    /// </summary>
    public bool KeepsHealth => _core != null && _parts.Count > 0;

    /// <summary>
    /// Reads the health and whether the core is open from the FSMs, returning whether either changed.
    /// </summary>
    public bool Read() {
        var coreState = _core != null ? _core.ActiveStateName : null;
        var coreOpen = !string.IsNullOrEmpty(coreState) && coreState != ClosedState;
        var health = 0;
        if (!coreOpen) {
            foreach (var (part, full) in _parts) {
                health += LeftOf(part, full, PartHits);
            }
        } else if (coreState != BrokenState) {
            health = LeftOf(_core!, CoreFull, CoreHits);
        }

        if (health == Health && coreOpen == CoreOpen) {
            return false;
        }

        Health = health;
        CoreOpen = coreOpen;
        return true;
    }

    /// <summary>
    /// What is left of a burning part or the core: its health, or less when the blows it counted are nearer to
    /// breaking it, as the same share of its full health, so that the bar is empty as it breaks. A needle of 5
    /// damage breaks a burning part at its 12th blow with near half its health left (USER 11-10 "是次数盾吗").
    /// </summary>
    /// <param name="fsm">The FSM of the burning part or of the core.</param>
    /// <param name="full">Its health at the start.</param>
    /// <param name="hits">The blows that break it.</param>
    private static int LeftOf(PlayMakerFSM fsm, int full, int hits) {
        var variables = fsm.FsmVariables;
        var health = Mathf.Max(variables.GetFsmInt(HealthVariable).Value, 0);
        var hitsLeft = Mathf.Max(hits - variables.GetFsmInt(HitsVariable).Value, 0);
        return Mathf.Min(health, full * hitsLeft / hits);
    }
}
