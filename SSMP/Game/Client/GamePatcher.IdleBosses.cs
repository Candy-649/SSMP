using System;
using System.Collections.Generic;
using System.Text;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Ui;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Says in the log when a boss that this game runs has started no move for a while with a player standing, and what it
/// does meanwhile: the state each of its FSMs is in, the ranges those states ask and who stands in them, whom it goes
/// after, and where everyone is. Players saw a boss go without attacking for a long time now and then, and neither log
/// said what it was doing: no line of it had a time, and none told the states of the boss (USER 2026-10-21:
/// "有时候会有很长一段时间不出技能").
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// How long a boss goes without starting a move before the log says what it does, in seconds of the game's time: a
    /// stun or a roar with what comes after it takes less.
    /// </summary>
    private const float IdleSayTime = 8f;

    /// <summary>
    /// How often the bosses are looked at, in seconds.
    /// </summary>
    private const float IdleLookSpacing = 1f;

    /// <summary>
    /// When a creature that this game runs last started a move, and whether the log has said that it has not since.
    /// </summary>
    private sealed class IdleWatch {
        /// <summary>
        /// The creature.
        /// </summary>
        public required GameObject Owner;

        /// <summary>
        /// Its health, by which it is seen to be dead.
        /// </summary>
        public required HealthManager Health;

        /// <summary>
        /// Whether it is a boss, the only ones looked at: other creatures wait for players by the way they are made.
        /// </summary>
        public bool IsBoss;

        /// <summary>
        /// When it last started a move, in the game's time.
        /// </summary>
        public float MoveAt;

        /// <summary>
        /// The state and the FSM in which it started it.
        /// </summary>
        public string MoveState = "";

        /// <inheritdoc cref="MoveState"/>
        public string MoveFsm = "";

        /// <summary>
        /// Whether the log said that it has started none since.
        /// </summary>
        public bool Said;
    }

    /// <summary>
    /// The creatures that this game runs that have started a move in this room, by instance ID.
    /// </summary>
    private static readonly Dictionary<int, IdleWatch> IdleWatches = new();

    /// <summary>
    /// The creatures gone since the last look, kept to be filled again.
    /// </summary>
    private static readonly List<int> IdleWatchesGone = [];

    /// <summary>
    /// When the bosses are next looked at, in unscaled time.
    /// </summary>
    private static float _nextIdleLook;

    /// <summary>
    /// Notes that a creature that this game runs starts a move, whoever set it off (see TurnAtMoveStart), and says so
    /// when the log said it had started none for a while.
    /// </summary>
    /// <param name="owner">The creature.</param>
    /// <param name="health">Its health.</param>
    /// <param name="machine">The FSM in which the move starts.</param>
    /// <param name="state">The state in which it starts.</param>
    private static void NoteMoveForIdle(GameObject owner, HealthManager health, PlayMakerFSM machine, FsmState state) {
        var now = Time.time;
        var id = owner.GetInstanceID();
        if (!IdleWatches.TryGetValue(id, out var watch)) {
            watch = new IdleWatch { Owner = owner, Health = health, IsBoss = EnemyHealthBars.IsBoss(health) };
            IdleWatches[id] = watch;
        } else if (watch.Said) {
            Logger.Info(
                $"[Idle] '{owner.name}' started a move again in '{state.Name}' of '{machine.FsmName}', " +
                $"{now - watch.MoveAt:0.0} s after the one before"
            );
        }

        watch.MoveAt = now;
        watch.MoveState = state.Name;
        watch.MoveFsm = machine.FsmName;
        watch.Said = false;
    }

    /// <summary>
    /// Looks at the bosses this game runs once a second, and says in the log what each does that has started no move
    /// for <see cref="IdleSayTime"/> with a player standing. Nobody standing, a boss waits for someone as it should.
    /// </summary>
    private static void WatchIdleBosses() {
        if (IdleWatches.Count == 0 || Time.unscaledTime < _nextIdleLook) {
            return;
        }

        _nextIdleLook = Time.unscaledTime + IdleLookSpacing;

        var now = Time.time;
        foreach (var pair in IdleWatches) {
            var watch = pair.Value;
            if (watch.Owner == null || watch.Health == null || watch.Health.GetIsDead()) {
                IdleWatchesGone.Add(pair.Key);
                continue;
            }

            if (!watch.IsBoss || watch.Said || now - watch.MoveAt < IdleSayTime || !watch.Owner.activeInHierarchy ||
                PlayerTargetRegistry.GetTrackedPlayers().Count == 0) {
                continue;
            }

            watch.Said = true;

            // Only a look, so it never stands in the way of the game
            try {
                Logger.Info(DescribeIdle(watch, now));
            } catch (Exception e) {
                Logger.Warn($"Could not say what '{watch.Owner.name}' does: {e.GetType().Name}: {e.Message}");
            }
        }

        foreach (var id in IdleWatchesGone) {
            IdleWatches.Remove(id);
        }

        IdleWatchesGone.Clear();
    }

    /// <summary>
    /// Says for the log what a boss that has started no move for a while does.
    /// </summary>
    /// <param name="watch">What is known of when it last started one.</param>
    /// <param name="now">The game's time now.</param>
    private static string DescribeIdle(IdleWatch watch, float now) {
        var owner = watch.Owner;
        var at = (Vector2) owner.transform.position;
        var players = new List<GameObject>(PlayerTargetRegistry.GetTrackedPlayers());

        var said = new StringBuilder(
            $"[Idle] '{owner.name}' has started no move for {now - watch.MoveAt:0.0} s in this game, which runs it, " +
            $"the last one in '{watch.MoveState}' of '{watch.MoveFsm}'; it is at ({at.x:0.0}, {at.y:0.0})"
        );

        foreach (var fsm in owner.GetComponents<PlayMakerFSM>()) {
            var state = fsm.Fsm?.ActiveState;
            if (state == null) {
                continue;
            }

            said.Append($"; '{fsm.FsmName}' in '{state.Name}'");
            var asked = DescribeRangesAsked(fsm, state, players);
            if (asked.Length > 0) {
                said.Append($", which asks {asked}");
            }
        }

        var target = GetApprovedEnemyTarget(owner);
        if (target == null) {
            said.Append("; it goes after nobody in particular");
        } else {
            said.Append($"; it goes after {DescribePlayer(target)}");
            if (!players.Contains(target)) {
                said.Append(PlayerTargetRegistry.IsPlayerDown(target) ? ", who is down" : ", who is not standing here");
            }

            if (TurnsOf(owner) is { } turns) {
                var time = Time.unscaledTime;
                said.Append(
                    $", turned to {time - turns.TurnedAt:0.0} s ago, last found in one of its ranges " +
                    (float.IsNegativeInfinity(turns.FoundAt) ? "never" : $"{time - turns.FoundAt:0.0} s ago")
                );
            }
        }

        foreach (var player in players) {
            var place = (Vector2) player.transform.position;
            said.Append(
                $"; {DescribePlayer(player)} at ({place.x:0.0}, {place.y:0.0}), {Vector2.Distance(place, at):0.0} away"
            );
        }

        return said.ToString();
    }

    /// <summary>
    /// Says for the log which ranges the actions of a state ask about a player, and which of the standing players are
    /// inside each.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="state">Its state.</param>
    /// <param name="players">The players who stand.</param>
    private static string DescribeRangesAsked(PlayMakerFSM fsm, FsmState state, List<GameObject> players) {
        var said = new StringBuilder();
        foreach (var action in state.Actions ?? []) {
            if (action == null || !action.Enabled) {
                continue;
            }

            var range = action switch {
                CheckAlertRangeByName byName => AlertRange.Find(fsm.gameObject, byName.alertRangeName),
                CheckAlertRange byObject => byObject.alertRange?.Value as AlertRange,
                _ => null
            };
            if (range == null) {
                continue;
            }

            var inside = new List<string>();
            foreach (var player in players) {
                if (IsPlayerInsideAlertRange(range, player)) {
                    inside.Add(DescribePlayer(player));
                }
            }

            said.Append(said.Length > 0 ? " and " : "");
            said.Append(
                $"'{range.name}' ({(range.isActiveAndEnabled ? "" : "switched off, ")}" +
                $"{(inside.Count == 0 ? "nobody" : string.Join(" and ", inside))} inside)"
            );
        }

        return said.ToString();
    }
}
