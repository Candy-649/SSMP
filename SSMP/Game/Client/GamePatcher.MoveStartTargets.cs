using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity.Action;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Has a creature that picks its moves at random now and then turn from the player it goes after to another one, as it
/// starts a move (USER 10-15: "怪物的锁敌其实可以带一点随机性？不然实战基本上就是怼着一个人打，两个人都站在怪面前才会调整
/// 锁敌。").
///
/// Before this a creature turned only to a player who stood in one of its ranges while the one it went after did
/// not, or who stood a fifth nearer (see ShouldSwitchApprovedTarget), and a creature with no ranges went after the
/// scene host's player alone: it fought whoever kept closest to it, and the other player could stand back for the
/// whole fight.
/// Now the game that runs a creature rolls, each time the creature starts a move, whether it turns to another player
/// within its reach - the way the games that share one monster between players do it, on a growing chance that is
/// reset by a turn - and between two moves nothing turns it to the nearer one.
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// How far a player may be from a creature, in units, to be one that it may turn to as it starts a move: half the
    /// width of what the camera shows (<see cref="CameraHalfWidth"/>), so that it turns only to someone it can reach
    /// without crossing the room. The further this reaches, the more a creature runs from one player to the other
    /// between its moves; the shorter, the more it keeps to whoever is near it.
    /// </summary>
    private const float TargetReach = CameraHalfWidth;

    /// <summary>
    /// How long a creature goes after a player it has turned to, in seconds, before it may roll to turn again: long
    /// enough to see a move or two through.
    /// </summary>
    private const float TurnHoldTime = 4f;

    /// <summary>
    /// How many moves a creature starts after it has turned to a player before it may roll to turn again, together
    /// with <see cref="TurnHoldTime"/>: a creature whose moves are long would otherwise turn after every one.
    /// </summary>
    private const int TurnHoldMoves = 2;

    /// <summary>
    /// How long a creature leaves between two rolls, in seconds: one that goes through the state where it picks a move
    /// many times a second would otherwise roll its chance up to the most within a few frames.
    /// </summary>
    private const float TurnRollSpacing = 1f;

    /// <summary>
    /// The chance that a creature turns, as it starts the first move on which it may, and how much more it is for
    /// every move it started since without turning, up to <see cref="TurnChanceMost"/>. A turn resets it.
    ///
    /// The way of the games that share one monster between several players: one that rolls the same chance every
    /// time can hold to one player for a long time by chance alone, and one whose chance grows cannot.
    /// </summary>
    private const float TurnChanceFirst = 0.15f, TurnChanceStep = 0.15f, TurnChanceMost = 0.7f;

    /// <summary>
    /// What the player a creature goes after counts for when it rolls whom it turns to, against another player as
    /// near who has gone as long unwatched: a roll that would turn it may still keep it where it is.
    /// </summary>
    private const float HeldTargetWeight = 0.5f;

    /// <summary>
    /// How long a player has gone without a creature going after them, in seconds, by which they count twice as much
    /// when it rolls whom it turns to: the one it has left alone the longest is the likeliest.
    /// </summary>
    private const float UnwatchedTime = 10f;

    /// <summary>
    /// How lately a creature must have asked a range with which it notices players, in seconds, for the range to be one
    /// it watches with now (see <see cref="IsNoticedBy"/>): one it woke by and never asks again is left out.
    /// </summary>
    private const float AwarenessAskedTime = 2f;

    /// <summary>
    /// How long after it last started a move a creature still counts as one that picks its moves, in seconds, and so
    /// turns only as it starts one (see <see cref="HoldsTargetBetweenMoves"/>). One that picks a move only now and
    /// then goes back to turning as it did before.
    /// </summary>
    private const float MovePickerMemory = 10f;

    /// <summary>
    /// The actions with which a creature picks its next move at random, by name: a state that holds one is where a
    /// move starts.
    /// </summary>
    private static readonly HashSet<string> MovePickActionNames = [
        "SendRandomEvent", "SendRandomEventV2", "SendRandomEventV3", "SendRandomEventV3ActiveBool",
        "SendRandomEventV4", "SendRandomEventFair"
    ];

    /// <summary>
    /// The parts of the names of the ranges with which a creature notices a player, rather than chooses what to do to
    /// one it goes after: someone who comes into one is noticed whomever it goes after (see
    /// <see cref="IsAwarenessRange"/>). A name that starts with "Start" counts as well.
    /// </summary>
    private static readonly string[] AwarenessRangeNameParts = [
        "Wake", "Alert", "Ambush", "Sight", "Seen", "Look", "Needolin", "Aggro"
    ];

    /// <summary>
    /// Whether each state that has been gone into starts a move, written down the first time.
    /// </summary>
    private static readonly Dictionary<FsmState, bool> MoveStartStates = new();

    /// <summary>
    /// Whether each FSM looks at a player anywhere, written down the first time: one that only picks a voice at random
    /// starts no move.
    /// </summary>
    private static readonly ConditionalWeakTable<PlayMakerFSM, BoxedBool> PlayerAimingFsms = [];

    /// <summary>
    /// Whether each alert range is one with which a creature notices a player (see <see cref="IsAwarenessRange"/>).
    /// </summary>
    private static readonly ConditionalWeakTable<AlertRange, BoxedBool> AwarenessRangeNotes = [];

    /// <summary>
    /// The ranges under each creature with which it notices a player, by the instance ID of the creature.
    /// </summary>
    private static readonly Dictionary<int, AlertRange[]> AwarenessRanges = new();

    /// <summary>
    /// How each creature that picks its moves has turned, by its instance ID (see <see cref="TargetTurns"/>).
    /// </summary>
    private static readonly Dictionary<int, TargetTurns> Turns = new();

    /// <summary>
    /// The creatures and things whose target a rule of their own sets, by instance ID, which roll for nothing: the one
    /// that throws at the players of a festival game is given whom it throws at each time (FleaGameCoop).
    /// </summary>
    private static readonly HashSet<int> KeptTargets = [];

    /// <summary>
    /// The dice of the turns: not the game's own, which the dice of what is set off here are taken from and handed to
    /// the partner (SharedDice), so that a roll here does not change what the partner's game rolls after it.
    /// </summary>
    private static readonly System.Random TurnDice = new();

    /// <summary>
    /// The players within a creature's reach as it starts a move, and what each counts for, kept to be filled again.
    /// </summary>
    private static readonly List<GameObject> ReachablePlayers = [];

    /// <inheritdoc cref="ReachablePlayers"/>
    private static readonly List<float> ReachableWeights = [];

    /// <summary>
    /// Hook for a state being gone into, before its actions start.
    /// </summary>
    private Hook? _stateEnterHook;

    /// <summary>
    /// How a creature that picks its moves has turned from one player to another.
    /// </summary>
    private sealed class TargetTurns {
        /// <summary>
        /// When it last turned, or was first seen to start a move.
        /// </summary>
        public float TurnedAt;

        /// <summary>
        /// How many moves it started since.
        /// </summary>
        public int MovesSince;

        /// <summary>
        /// How many times it rolled since without turning.
        /// </summary>
        public int Misses;

        /// <summary>
        /// When it last rolled.
        /// </summary>
        public float RolledAt = float.NegativeInfinity;

        /// <summary>
        /// The frame in which it last started a move, and when.
        /// </summary>
        public int MoveFrame;

        /// <inheritdoc cref="MoveFrame"/>
        public float MoveAt;

        /// <summary>
        /// When it last stopped going after each player.
        /// </summary>
        public readonly Dictionary<GameObject, float> LeftAt = new();

        /// <summary>
        /// Whether the player it went after was within its reach, as last worked out, for whom and in which frame (see
        /// <see cref="HoldsTargetBetweenMoves"/>), since that is asked many times in one frame.
        /// </summary>
        public bool TargetInReach;

        /// <inheritdoc cref="TargetInReach"/>
        public GameObject? ReachTarget;

        /// <inheritdoc cref="TargetInReach"/>
        public int ReachFrame = -1;
    }

    /// <summary>
    /// Registers the hook that has creatures turn as they start a move.
    /// </summary>
    private void RegisterMoveStartHook() {
        _stateEnterHook = new Hook(
            typeof(FsmState).GetMethod(nameof(FsmState.OnEnter), InstancePublicFlags)!,
            new Action<Action<FsmState>, FsmState>(OnStateEnter)
        );
    }

    /// <summary>
    /// Disposes the hook that has creatures turn as they start a move.
    /// </summary>
    private void DisposeMoveStartHook() {
        _stateEnterHook?.Dispose();
        _stateEnterHook = null;
    }

    /// <summary>
    /// Forgets how the creatures of the room turned, with the room.
    /// </summary>
    private static void ClearMoveStartTargets() {
        MoveStartStates.Clear();
        AwarenessRanges.Clear();
        Turns.Clear();
        KeptTargets.Clear();
    }

    /// <summary>
    /// Forgets how a creature that is going away turned.
    /// </summary>
    /// <param name="instanceId">The instance ID of the creature.</param>
    private static void ForgetMoveStartTargets(int instanceId) {
        AwarenessRanges.Remove(instanceId);
        Turns.Remove(instanceId);
        KeptTargets.Remove(instanceId);
    }

    /// <summary>
    /// Has a creature that is going into a state where it picks its next move roll whether it turns to another player
    /// first (see <see cref="TurnAtMoveStart"/>): before the actions of the state, which measure the player it goes
    /// after to choose the move, so that the move is chosen for the one it goes after now. In 76 of the 171 FSMs of
    /// creatures that pick a move at random, the state that picks it measures a player too (reports/fsm-usage.tsv).
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The state.</param>
    private static void OnStateEnter(Action<FsmState> orig, FsmState self) {
        if (StartsAMove(self) && self.Fsm?.FsmComponent is { } machine && machine != null) {
            try {
                TurnAtMoveStart(machine, self);
            } catch (Exception e) {
                Logger.Warn(
                    $"Could not have '{machine.name}' roll whom it goes after: {e.GetType().Name}: {e.Message}"
                );
            }
        }

        orig(self);
    }

    /// <summary>
    /// Whether a state is one where a creature picks its next move at random.
    /// </summary>
    /// <param name="state">The state.</param>
    private static bool StartsAMove(FsmState state) {
        if (MoveStartStates.TryGetValue(state, out var starts)) {
            return starts;
        }

        starts = false;
        if (state.Actions != null) {
            foreach (var action in state.Actions) {
                if (action != null && MovePickActionNames.Contains(action.GetType().Name)) {
                    starts = true;
                    break;
                }
            }
        }

        MoveStartStates[state] = starts;
        return starts;
    }

    /// <summary>
    /// As a creature that this game runs for everyone starts a move, rolls whether it turns to another player within
    /// its reach, and turns it if so.
    ///
    /// A creature turns only after it has gone after the player it turned to for a few seconds and a couple of moves,
    /// and then each move it starts may turn it: first on a small chance that grows with every move it does not, and
    /// reset by a turn. Who it turns to is rolled among the players within its reach, the nearer one and the one it
    /// has left alone the longer counting more, and the one it goes after counting half (see
    /// <see cref="PickTarget"/>). A creature whose player has gone out of its reach - too far, behind a wall, down -
    /// turns at once to one who is within it.
    ///
    /// Only the game that runs the creature rolls, and the other games see it turn as they see it do anything. What
    /// another game set off with its dice plays here as it played there (SharedDice), so nothing rolls while it does,
    /// and nothing rolls while the local player gives a sign, which the creature hears of them alone (Entity.Signs).
    /// </summary>
    /// <param name="machine">The FSM of the creature.</param>
    /// <param name="state">The state where it picks its next move.</param>
    private static void TurnAtMoveStart(PlayMakerFSM machine, FsmState state) {
        if (Save.SharedDice.Throwing || Entity.Entity.OwnSign != null || !Entity.Entity.RunsHere(machine) ||
            !AimsAtPlayers(machine)) {
            return;
        }

        var owner = GetEnemyTargetOwner(machine.gameObject);
        if (owner == null || owner.GetComponent<HealthManager>() == null) {
            return;
        }

        // One that sings to a player of a song turns to them by the song (GetFsmActionTarget)
        var id = owner.GetInstanceID();
        if (KeptTargets.Contains(id) || TryGetSongTarget(id, out _)) {
            return;
        }

        var now = Time.unscaledTime;
        if (!Turns.TryGetValue(id, out var turns)) {
            // The first move it is seen to start counts as a turn to whom it goes after
            Turns[id] = new TargetTurns { TurnedAt = now, MoveFrame = Time.frameCount, MoveAt = now };
            return;
        }

        // Two of its FSMs that start a move in the same frame start one move
        if (turns.MoveFrame == Time.frameCount) {
            return;
        }

        turns.MoveFrame = Time.frameCount;
        turns.MoveAt = now;
        turns.MovesSince++;

        // A creature that has noticed nobody yet turns to nobody; one that notices no one by itself - a boss that only
        // ever looks at the player - goes after this game's player while it goes after no one in particular
        var held = GetApprovedEnemyTarget(owner) ?? (HasAwarenessRanges(owner) ? null : LocalPlayerObject());
        if (held == null) {
            return;
        }

        var candidates = FindReachablePlayers(owner);
        if (candidates.Count == 0) {
            return;
        }

        var forced = !candidates.Contains(held);
        var chance = 1f;
        if (!forced) {
            if (candidates.Count < 2 || turns.MovesSince < TurnHoldMoves || now - turns.TurnedAt < TurnHoldTime ||
                now - turns.RolledAt < TurnRollSpacing) {
                return;
            }

            turns.RolledAt = now;
            chance = Mathf.Min(TurnChanceFirst + TurnChanceStep * turns.Misses, TurnChanceMost);
            if (TurnDice.NextDouble() >= chance) {
                turns.Misses++;
                return;
            }
        }

        var pick = PickTarget(owner, turns, candidates, forced ? null : held);
        if (pick == held) {
            turns.Misses++;
            return;
        }

        var why = forced
            ? $"{DescribePlayer(held)}, whom it went after, was out of its reach"
            : $"it rolled under {chance:P0} after {turns.Misses} roll(s) that kept it where it was";
        Logger.Info(
            $"{owner.name} turned to {DescribePlayer(pick)} as it started a move in '{state.Name}' of " +
            $"'{machine.FsmName}': {why}; weights {DescribeWeights(candidates)}"
        );

        turns.LeftAt[held] = now;
        ApproveEnemyTarget(owner, pick);
    }

    /// <summary>
    /// Notes that a creature turned from one player to another, whatever turned it - its own roll, a range, a sign - so
    /// that it holds to the one it turned to before it rolls again.
    /// </summary>
    /// <param name="instanceId">The instance ID of the creature.</param>
    /// <param name="from">The player it went after before, or null for none.</param>
    private static void NoteTurn(int instanceId, GameObject? from) {
        if (!Turns.TryGetValue(instanceId, out var turns)) {
            return;
        }

        turns.TurnedAt = Time.unscaledTime;
        turns.MovesSince = 0;
        turns.Misses = 0;
        if (from != null) {
            turns.LeftAt[from] = Time.unscaledTime;
        }
    }

    /// <summary>
    /// Whether a creature that picks its moves holds to the player it goes after until it starts its next move (see
    /// AnswerWhetherAPlayerIsInAlertRange): it does while it started one lately and the player is still within its
    /// reach. Worked out once a frame, since its ranges ask many times.
    /// </summary>
    /// <param name="owner">The creature.</param>
    /// <param name="target">The player it goes after.</param>
    private static bool HoldsTargetBetweenMoves(GameObject owner, GameObject target) {
        if (!Turns.TryGetValue(owner.GetInstanceID(), out var turns) ||
            Time.unscaledTime - turns.MoveAt > MovePickerMemory) {
            return false;
        }

        if (turns.ReachFrame != Time.frameCount || turns.ReachTarget != target) {
            turns.ReachFrame = Time.frameCount;
            turns.ReachTarget = target;
            turns.TargetInReach = PlayerTargetRegistry.GetTrackedPlayers().Contains(target) &&
                                  IsWithinReach(owner.transform.position, target);
        }

        return turns.TargetInReach;
    }

    /// <summary>
    /// The players a creature may turn to as it starts a move: those standing within its reach and in its sight, and,
    /// for a creature that watches for players with a range of its own, inside one of those ranges. It would otherwise
    /// turn to a player its own ranges do not see, and the next time it asked one it would turn straight back.
    /// </summary>
    /// <param name="owner">The creature.</param>
    /// <returns>The players, in a list that is filled again next time.</returns>
    private static List<GameObject> FindReachablePlayers(GameObject owner) {
        ReachablePlayers.Clear();

        var origin = owner.transform.position;
        foreach (var player in PlayerTargetRegistry.GetTrackedPlayers()) {
            if (player != null && player.activeInHierarchy && IsWithinReach(origin, player) &&
                IsNoticedBy(owner, player)) {
                ReachablePlayers.Add(player);
            }
        }

        return ReachablePlayers;
    }

    /// <summary>
    /// Whether a player stands within <see cref="TargetReach"/> of a place, with no wall in between.
    /// </summary>
    /// <param name="origin">Where the creature is.</param>
    /// <param name="player">The player.</param>
    private static bool IsWithinReach(Vector2 origin, GameObject player) {
        var aim = (Vector2) GetTargetAimPosition(player);
        return (aim - origin).sqrMagnitude <= TargetReach * TargetReach &&
               !Helper.LineCast2DHit(origin, aim, TerrainLayerMask, out _);
    }

    /// <summary>
    /// Whether a creature notices a player by its own ranges: the player is inside one of the ranges it watches for
    /// players with now and can be seen from it, or it watches with none. A range it watches with now is one with which
    /// it notices players that is on and that it asked lately (<see cref="AwarenessAskedTime"/>): the range a boss woke
    /// by is often still there in the fight, and asked by nothing.
    /// </summary>
    /// <param name="owner">The creature.</param>
    /// <param name="player">The player.</param>
    private static bool IsNoticedBy(GameObject owner, GameObject player) {
        var watching = false;
        foreach (var range in AwarenessRangesOf(owner)) {
            if (range == null || !range.isActiveAndEnabled || !EnemyAlertRanges.TryGetValue(range, out var note) ||
                Time.unscaledTime - note.AskedAt > AwarenessAskedTime) {
                continue;
            }

            watching = true;
            if (IsPlayerInsideAlertRange(range, player) && HasLineOfSightToAlertRangeTarget(range, player)) {
                return true;
            }
        }

        return !watching;
    }

    /// <summary>
    /// Whether a creature has ranges of its own with which it notices players, switched on or not.
    /// </summary>
    /// <param name="owner">The creature.</param>
    private static bool HasAwarenessRanges(GameObject owner) => AwarenessRangesOf(owner).Length > 0;

    /// <summary>
    /// The ranges under a creature with which it notices players, found the first time. One that the creature handed
    /// to something else as it started up is not under it any more and is not among them.
    /// </summary>
    /// <param name="owner">The creature.</param>
    private static AlertRange[] AwarenessRangesOf(GameObject owner) {
        var id = owner.GetInstanceID();
        if (AwarenessRanges.TryGetValue(id, out var ranges)) {
            return ranges;
        }

        var found = new List<AlertRange>();
        foreach (var range in owner.GetComponentsInChildren<AlertRange>(true)) {
            if (range != null && IsAwarenessRange(range) && !IsNonAcquiringAlertRangeName(range.name)) {
                found.Add(range);
            }
        }

        AwarenessRanges[id] = ranges = found.ToArray();
        return ranges;
    }

    /// <summary>
    /// Whether a range is one with which a creature notices a player - it wakes, comes out of hiding or keeps watch by
    /// it - by the game's own word for it in its name, rather than one with which it chooses what to do to the player
    /// it goes after. A player who comes into one is noticed whomever the creature goes after, as before; one who
    /// comes into another does not turn it until it starts a move (see AnswerWhetherAPlayerIsInAlertRange).
    /// </summary>
    /// <param name="range">The range.</param>
    private static bool IsAwarenessRange(AlertRange range) {
        if (AwarenessRangeNotes.TryGetValue(range, out var note)) {
            return note.Value;
        }

        var name = range.name;
        var awareness = name.StartsWith("Start", StringComparison.OrdinalIgnoreCase);
        foreach (var part in AwarenessRangeNameParts) {
            awareness |= name.Contains(part, StringComparison.OrdinalIgnoreCase);
        }

        AwarenessRangeNotes.Add(range, new BoxedBool { Value = awareness });
        return awareness;
    }

    /// <summary>
    /// Whether an FSM looks at a player anywhere - asks where one is, faces one, aims at one or asks one of its ranges
    /// about one - written down the first time. One that does not starts no move that a player could be the target of.
    /// </summary>
    /// <param name="machine">The FSM.</param>
    private static bool AimsAtPlayers(PlayMakerFSM machine) {
        if (PlayerAimingFsms.TryGetValue(machine, out var note)) {
            return note.Value;
        }

        var aims = false;
        foreach (var state in machine.FsmStates ?? []) {
            foreach (var action in state?.Actions ?? []) {
                if (action is GetHero or CheckAlertRange or CheckAlertRangeByName ||
                    action != null && ActionRegistry.TargetedFsmActionTypes.Contains(action.GetType().Name)) {
                    aims = true;
                    break;
                }
            }

            if (aims) {
                break;
            }
        }

        PlayerAimingFsms.Add(machine, new BoxedBool { Value = aims });
        return aims;
    }

    /// <summary>
    /// Rolls whom a creature turns to among the players within its reach. Each counts for how near they are - from
    /// once right by it to half as much at the edge of its reach - times how long it has left them alone, from once
    /// to twice as much after <see cref="UnwatchedTime"/>, and the one it goes after counts
    /// <see cref="HeldTargetWeight"/> of that.
    /// </summary>
    /// <param name="owner">The creature.</param>
    /// <param name="turns">How it has turned.</param>
    /// <param name="candidates">The players within its reach.</param>
    /// <param name="held">The player it goes after, or null when it has to turn.</param>
    /// <returns>The player it turns to, which may be the one it goes after.</returns>
    private static GameObject PickTarget(
        GameObject owner,
        TargetTurns turns,
        List<GameObject> candidates,
        GameObject? held
    ) {
        ReachableWeights.Clear();

        var origin = (Vector2) owner.transform.position;
        var now = Time.unscaledTime;
        var total = 0f;
        foreach (var player in candidates) {
            var distance = ((Vector2) player.transform.position - origin).magnitude;
            var nearness = 1f - 0.5f * Mathf.Clamp01(distance / TargetReach);
            var unwatched = player == held ? 0f
                : turns.LeftAt.TryGetValue(player, out var leftAt) ? now - leftAt
                : UnwatchedTime;
            var weight = nearness * (1f + Mathf.Clamp01(unwatched / UnwatchedTime)) *
                         (player == held ? HeldTargetWeight : 1f);

            ReachableWeights.Add(weight);
            total += weight;
        }

        var roll = (float) TurnDice.NextDouble() * total;
        for (var i = 0; i < candidates.Count; i++) {
            roll -= ReachableWeights[i];
            if (roll < 0f) {
                return candidates[i];
            }
        }

        return candidates[^1];
    }

    /// <summary>
    /// Says what each player within a creature's reach counted for in its last roll, for the log.
    /// </summary>
    /// <param name="candidates">The players, in the order they were weighed.</param>
    private static string DescribeWeights(List<GameObject> candidates) {
        var said = new StringBuilder();
        for (var i = 0; i < candidates.Count && i < ReachableWeights.Count; i++) {
            if (i > 0) {
                said.Append(", ");
            }

            said.Append($"{DescribePlayer(candidates[i])} {ReachableWeights[i]:0.00}");
        }

        return said.ToString();
    }

    /// <summary>
    /// Names a player for the log as the local player or as the partner.
    /// </summary>
    /// <param name="player">The player.</param>
    private static string DescribePlayer(GameObject player) {
        return player == LocalPlayerObject() ? "the local player" : $"the partner ({player.name})";
    }

    /// <summary>
    /// The object of this game's player, or null while there is none.
    /// </summary>
    private static GameObject? LocalPlayerObject() {
        var hero = HeroController.SilentInstance;
        return hero != null ? hero.gameObject : null;
    }
}
