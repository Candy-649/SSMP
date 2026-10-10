using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity.Action;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// The local player waking a creature that the other game runs. A creature that lies in wait - under a cloth, in the
/// ground, asleep, ready to spring - wakes as a player comes into one of its ranges. The game that runs it asks that of
/// both players, but it sees the partner only as the figure it is told of, a moment late and after a gap a step at a
/// time, so a player who walked up to it in their own game woke it late, or walked past it without waking it at all
/// (USER 10-10, "那个从布底下钻出来的爬行小怪真的同步了吗，我怎么感觉我这里只有我能叫醒"). So the game of the player who
/// comes near wakes its copy at once, as a strike is played (PlayHere), and the game that runs the creature wakes it the
/// same way, going after that player (CoopHits.ApplyEntityInput).
/// </summary>
internal partial class Entity {
    /// <summary>
    /// How long a wake that the local player set off is not set off again from the same state, while the game that runs
    /// the creature has not said it went anywhere.
    /// </summary>
    private const float WakeAgainTime = 1.5f;

    /// <summary>
    /// The start of the name of the object that a fight with a boss sits under.
    /// </summary>
    private const string BossSceneNamePrefix = "Boss Scene";

    /// <summary>
    /// What a player coming near wakes a creature with, by the state of the copy's FSM that waits for it, found the first
    /// time the copy rests in that state.
    /// </summary>
    private static readonly ConditionalWeakTable<FsmState, List<WakeCheck>> WakeChecksByState = new();

    /// <summary>
    /// Raised when the local player came into a range that wakes the copy of a creature, with the index of the FSM and
    /// the event.
    /// </summary>
    public static event Action<Entity, byte, string>? CopyWokenByLocalPlayer;

    /// <summary>
    /// The FSM and the state of it that the local player last woke this copy from, and when, so that a wake that did not
    /// take is not set off again every frame.
    /// </summary>
    private (byte FsmIndex, string State, float At)? _wokenFrom;

    /// <summary>
    /// When the local player was first seen in each range of this copy that wakes it only after a while, by the action
    /// that asks (CheckAlertRange.InRangeDelay).
    /// </summary>
    private readonly Dictionary<FsmStateAction, float> _seenInRangeSince = new();

    /// <summary>
    /// The wake checks of the state each FSM of the copy was last found waiting in, by the index of the FSM, so that the
    /// state is not looked up by its name every frame.
    /// </summary>
    private (string State, List<WakeCheck> Checks)?[]? _wakeChecksOf;

    /// <summary>
    /// Whether the creature belongs to a fight of its own - a fight in a closed arena or with a boss - whose start goes
    /// by its own rules (ArenaCoop, BossRoomCoop), found the first time it is asked.
    /// </summary>
    private bool? _wakesInAFight;

    /// <summary>
    /// A range of the copy's FSM that wakes the creature when a player comes into it: the action that asks, the event
    /// it sends, the range, and how long a player has to stand in it first.
    /// </summary>
    private sealed class WakeCheck(FsmStateAction action, string eventName, string? rangeName, float delay) {
        public FsmStateAction Action { get; } = action;
        public string EventName { get; } = eventName;
        public string? RangeName { get; } = rangeName;
        public float Delay { get; } = delay;
        public AlertRange? Range { get; set; }
    }

    /// <summary>
    /// Wakes the copy at once when the local player comes into a range of it that wakes it, while the game that runs the
    /// creature says it waits in that state: the copy's own FSM takes the wake here and the scene host is sent it (see
    /// <see cref="CopyWokenByLocalPlayer"/>). Only a wait for the player to come near counts - a range that wakes or
    /// ambushes - and not one that starts an attack of a creature that is up already, which the game that runs it decides
    /// from where everybody is as it moves.
    /// </summary>
    private void CheckWokenByLocalPlayer() {
        if (CopyWokenByLocalPlayer == null || _runHere != null || _runHereTalk || Object.Client == null ||
            !Object.Client.activeInHierarchy || WakesInAFight()) {
            return;
        }

        for (var index = 0; index < _fsms.Client.Count && index < byte.MaxValue; index++) {
            var copyFsm = _fsms.Client[index];
            if (copyFsm == null || copyFsm.Fsm == null || index >= _fsms.Host.Count || _fsms.Host[index] == null ||
                IsRunByEachGame(_fsms.Host[index])) {
                continue;
            }

            var fsm = copyFsm.Fsm;
            if (EntityFsmActions.HostStateOf(fsm) is not { } stateName) {
                continue;
            }

            _wakeChecksOf ??= new (string, List<WakeCheck>)?[_fsms.Client.Count];
            List<WakeCheck> checks;
            if (_wakeChecksOf[index] is { } known && known.State == stateName) {
                checks = known.Checks;
            } else {
                if (fsm.GetState(stateName) is not { } state) {
                    continue;
                }

                if (!WakeChecksByState.TryGetValue(state, out checks)) {
                    checks = FindWakeChecks(fsm, state);
                    WakeChecksByState.Add(state, checks);
                }

                // A wait in a range starts over once the FSM has been in another state: left as it was, coming back
                // would count the time away as time seen
                if (_wakeChecksOf[index] is { } left) {
                    foreach (var check in left.Checks) {
                        _seenInRangeSince.Remove(check.Action);
                    }
                }

                _wakeChecksOf[index] = (stateName, checks);
            }

            if (checks.Count == 0 ||
                _wokenFrom is { } woken && woken.FsmIndex == index && woken.State == stateName &&
                Time.unscaledTime - woken.At < WakeAgainTime) {
                continue;
            }

            foreach (var check in checks) {
                if (!SeesLocalPlayer(fsm, check)) {
                    _seenInRangeSince.Remove(check.Action);
                    continue;
                }

                if (check.Delay > 0f) {
                    if (!_seenInRangeSince.TryGetValue(check.Action, out var since)) {
                        _seenInRangeSince[check.Action] = Time.time;
                        continue;
                    }

                    if (Time.time - since < check.Delay) {
                        continue;
                    }
                }

                _seenInRangeSince.Clear();
                _wokenFrom = ((byte) index, stateName, Time.unscaledTime);

                // Woken by this game's player, so it is this game's player that the copy goes after
                if (HeroController.instance != null) {
                    GamePatcher.SetTargetOf(Object.Client, HeroController.instance.gameObject);
                }

                Logger.Info(
                    $"The local player came into '{check.RangeName ?? "the range"}' of the copy of entity {Id} " +
                    $"({Type}) in '{stateName}', which wakes it with '{check.EventName}'"
                );
                CopyWokenByLocalPlayer(this, (byte) index, check.EventName);
                return;
            }
        }
    }

    /// <summary>
    /// Whether the range of a wake check of the copy sees the local player, as the check itself asks (an alert range
    /// that is switched off sees nobody).
    /// </summary>
    private bool SeesLocalPlayer(HutongGames.PlayMaker.Fsm fsm, WakeCheck check) {
        var range = check.Range;
        if (range == null) {
            range = check.Action switch {
                CheckAlertRangeByName => AlertRange.Find(fsm.GameObject, check.RangeName),
                CheckAlertRange byObject => byObject.alertRange?.Value as AlertRange,
                _ => null
            };

            // Only a range of the copy itself: one that the FSM was handed from the room's own creature asks about
            // what stands near that one
            if (range == null || Object.Client == null || !range.transform.IsChildOf(Object.Client.transform)) {
                return false;
            }

            check.Range = range;
        }

        return range.gameObject.activeSelf && GamePatcher.SeesLocalPlayer(range);
    }

    /// <summary>
    /// Finds the ranges in a state of the copy's FSM that wake the creature when a player comes into them: every-frame
    /// checks of an alert range that send the FSM itself an event that leads somewhere from the state, where the event
    /// or the range is the game's own word for waking - a wake or an ambush.
    /// </summary>
    private static List<WakeCheck> FindWakeChecks(HutongGames.PlayMaker.Fsm fsm, FsmState state) {
        var checks = new List<WakeCheck>();
        foreach (var action in state.Actions ?? []) {
            string? eventName;
            string? rangeName;
            var delay = 0f;
            switch (action) {
                case CheckAlertRangeByName byName when byName.everyFrame &&
                                                       (byName.eventTarget == null ||
                                                        byName.eventTarget.target == FsmEventTarget.EventTarget.Self):
                    eventName = byName.sendEvent == null || byName.sendEvent.IsNone ? null : byName.sendEvent.Value;
                    rangeName = byName.alertRangeName;
                    break;
                case CheckAlertRange byObject when byObject.everyFrame:
                    eventName = byObject.InRangeEvent?.Name;
                    rangeName = (byObject.alertRange?.Value as AlertRange)?.name;
                    delay = byObject.InRangeDelay == null || byObject.InRangeDelay.IsNone
                        ? 0f
                        : byObject.InRangeDelay.Value;
                    break;
                default:
                    continue;
            }

            if (string.IsNullOrEmpty(eventName) || !IsWakeWord(eventName!) && !IsWakeWord(rangeName) ||
                EntityFsmActions.FindTransition(fsm, state, eventName!) == null) {
                continue;
            }

            checks.Add(new WakeCheck(action, eventName!, rangeName, delay));
        }

        return checks;
    }

    /// <summary>
    /// Whether an event or the name of a range is the game's word for waking up or springing an ambush.
    /// </summary>
    private static bool IsWakeWord(string? name) {
        return name != null && (name.IndexOf("wake", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("ambush", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// Whether the creature belongs to a fight in a closed arena or with a boss, found once.
    /// </summary>
    private bool WakesInAFight() {
        if (_wakesInAFight is { } known) {
            return known;
        }

        var inAFight = false;
        if (Object.Host != null) {
            inAFight = Object.Host.GetComponentInParent<BattleScene>(true) != null;
            for (var current = Object.Host.transform; current != null && !inAFight; current = current.parent) {
                inAFight = current.name.StartsWith(BossSceneNamePrefix, StringComparison.Ordinal);
            }
        }

        _wakesInAFight = inAFight;
        return inAFight;
    }
}
