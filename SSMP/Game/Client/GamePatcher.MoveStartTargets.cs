using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity.Action;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Has a creature that picks its moves at random now and then turn from the player it goes after to another one, as it
/// starts a move (USER 10-15: "怪物的锁敌其实可以带一点随机性？不然实战基本上就是怼着一个人打，两个人都站在怪面前才会调整
/// 锁敌。").
///
/// Before this a creature turned only to a player who stood in one of its ranges while the one it went after did
/// not, or who stood a fifth nearer, and a creature with no ranges went after the scene host's player alone: it fought
/// whoever kept closest to it, and the other player could stand back for the whole fight.
/// Now the game that runs a creature rolls, each time the creature starts a move, whether it turns to another player
/// within its reach - the way the games that share one monster between players do it, on a growing chance that is
/// reset by a turn - and while it fights the player it goes after, nothing between two moves turns it to the nearer
/// one.
///
/// Since 10-24 this is the one rule for every creature (USER: "我不知道你再说哪个，那个遭遇战所有的虫子都会飞，你就不能统一
/// 一下吗，这换目标都改了多少版了，就不能所有怪都统一成一版吗"): it applied to creatures that pick their moves at random,
/// while their ranges, the sight of a flyer and the walking of a walker each kept a rule of their own, so a flyer turned
/// back and forth between two players several times a second. A creature goes after the nearest player it notices,
/// keeps going after them for <see cref="TurnHoldTime"/> whatever else, and after that while they are within its
/// reach and it notices them (see KeepsTarget), rolling whether to turn to another as it starts a move, or, for one
/// that picks no moves, as it looks for players (see RollBetweenTimes).
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// How far a player may be from a creature, in units, to be one that it keeps going after once its hold is over (see
    /// KeepsTarget), may turn to as it rolls, or takes as it first looks for one (see AcquireNearestNoticed): half the
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
    /// The chance that a creature turns, as it rolls for the first time after it turned, and how much more it is for
    /// every roll since that kept it where it was, up to <see cref="TurnChanceMost"/>. A turn resets it.
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
    /// How long a creature that asks its ranges about the player it goes after still counts as fighting them after
    /// one of its ranges last found them inside, in seconds (see <see cref="IsFighting"/>). Long enough to last from
    /// one choice of a move to the next, which a creature may make a few seconds apart and only then ask its ranges.
    /// </summary>
    private const float FightMemory = 4f;

    /// <summary>
    /// How long after it last started a move a creature still counts as one that picks its moves, in seconds, and so
    /// rolls whether to turn only as it starts one (see <see cref="PicksMovesLately"/>). One that picks a move only now
    /// and then rolls as it looks for players between times as well.
    /// </summary>
    private const float MovePickerMemory = 10f;

    /// <summary>
    /// How often a creature that goes after no one may look for the nearest player it notices, in seconds (see
    /// <see cref="AcquireNearestNoticed"/>), since what looks for one for it, like its walking, may ask every frame.
    /// </summary>
    private const float LookSpacing = 0.25f;

    /// <summary>
    /// How long whether a creature keeps going after a player is taken as it was worked out, in seconds (see
    /// <see cref="KeepsTarget"/>): its ranges, its sight, its walking and its chase ask every frame, and each time it
    /// is worked out it measures how far the player is and looks for walls in between.
    /// </summary>
    private const float KeepSpacing = 0.1f;

    /// <summary>
    /// How often what aims at a player for a creature has it keep going after one or look for one, in seconds (see
    /// <see cref="LookAsItAims"/>): a creature that chases a player aims at them every frame.
    /// </summary>
    private const float AimLookSpacing = 0.1f;

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
    /// one it goes after: it keeps going after a player while it notices them by one, and may turn to another player
    /// it notices by one (see <see cref="IsAwarenessRange"/>, IsNoticedBy). A name that starts with "Start" counts as
    /// well.
    /// </summary>
    private static readonly string[] AwarenessRangeNameParts = [
        "Wake", "Alert", "Ambush", "Sight", "Seen", "Look", "Needolin", "Aggro"
    ];

    /// <summary>
    /// What each state that has been gone into has to do with the start of a move (see <see cref="NoteOfState"/>),
    /// written down the first time.
    /// </summary>
    private static readonly ConditionalWeakTable<FsmState, MoveStartNote> MoveStartStates = [];

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
    /// How each creature that went after a player or looked for one has turned, by its instance ID (see
    /// <see cref="TargetTurns"/>).
    /// </summary>
    private static readonly Dictionary<int, TargetTurns> Turns = new();

    /// <summary>
    /// When what aimed at a player for each creature last had it keep going after one or look for one, by the instance
    /// ID of the creature (see <see cref="LookAsItAims"/>).
    /// </summary>
    private static readonly Dictionary<int, float> AimLooks = new();

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
    /// What a state has to do with the start of a move.
    /// </summary>
    private sealed class MoveStartNote {
        /// <summary>
        /// Whether a move starts as the state is gone into.
        /// </summary>
        public bool Starts;

        /// <summary>
        /// Whether one of the ways out of it leads straight into a state where a move is picked at random.
        /// </summary>
        public bool Leads;
    }

    /// <summary>
    /// How a creature has turned from one player to another.
    /// </summary>
    internal sealed class TargetTurns {
        /// <summary>
        /// When it last turned, or was first seen to start a move.
        /// </summary>
        public float TurnedAt;

        /// <summary>
        /// How many moves it started since: -1 while it has not started the first one with the player it turned to
        /// between two moves.
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
        /// When it last asked one of its ranges about a player, and when one of them last found the player it goes
        /// after inside (see <see cref="IsFighting"/>).
        /// </summary>
        public float AskedAt = float.NegativeInfinity;

        /// <inheritdoc cref="AskedAt"/>
        public float FoundAt = float.NegativeInfinity;

        /// <summary>
        /// When it last stopped going after each player.
        /// </summary>
        public readonly Dictionary<GameObject, float> LeftAt = new();

        /// <summary>
        /// Whether it was seen to pick a move at random (see TurnAtMoveStart): one that does rolls whether to turn as it
        /// starts a move, one that does not as it looks for players (see <see cref="PicksMovesLately"/>).
        /// </summary>
        public bool PicksMoves;

        /// <summary>
        /// Whether it keeps going after the player it goes after, as last worked out, for whom and when (see
        /// <see cref="KeepsTarget"/>), since that is asked many times a frame.
        /// </summary>
        public bool Keeps;

        /// <inheritdoc cref="Keeps"/>
        public GameObject? KeepTarget;

        /// <inheritdoc cref="Keeps"/>
        public float KeptAt = float.NegativeInfinity;

        /// <summary>
        /// When it last looked for the nearest player it notices while it went after no one (see
        /// <see cref="AcquireNearestNoticed"/>).
        /// </summary>
        public float LookedAt = float.NegativeInfinity;

        /// <summary>
        /// A copy of how it has turned, to put back with <see cref="PutBack"/>.
        /// </summary>
        public TargetTurns Copy() {
            var copy = new TargetTurns {
                TurnedAt = TurnedAt, MovesSince = MovesSince, Misses = Misses, RolledAt = RolledAt
            };
            foreach (var pair in LeftAt) {
                copy.LeftAt[pair.Key] = pair.Value;
            }

            return copy;
        }

        /// <summary>
        /// Puts back how it had turned, from a copy (<see cref="Copy"/>): a turn that came to nothing is forgotten.
        /// </summary>
        /// <param name="copy">The copy.</param>
        public void PutBack(TargetTurns copy) {
            (TurnedAt, MovesSince, Misses, RolledAt) = (copy.TurnedAt, copy.MovesSince, copy.Misses, copy.RolledAt);
            LeftAt.Clear();
            foreach (var pair in copy.LeftAt) {
                LeftAt[pair.Key] = pair.Value;
            }
        }
    }

    /// <summary>
    /// Registers the hook that has creatures turn as they start a move.
    /// </summary>
    private void RegisterMoveStartHook() {
        _stateEnterHook = new Hook(
            typeof(FsmState).GetMethod(nameof(FsmState.OnEnter), InstancePublicFlags)!,
            new Action<Action<FsmState>, FsmState>(OnStateEnter)
        );
        MonoBehaviourUtil.Instance.OnUpdateEvent += WatchIdleBosses;
    }

    /// <summary>
    /// Disposes the hook that has creatures turn as they start a move.
    /// </summary>
    private void DisposeMoveStartHook() {
        _stateEnterHook?.Dispose();
        _stateEnterHook = null;
        MonoBehaviourUtil.Instance.OnUpdateEvent -= WatchIdleBosses;
        IdleWatches.Clear();
    }

    /// <summary>
    /// Forgets how the creatures of the room turned, with the room.
    /// </summary>
    private static void ClearMoveStartTargets() {
        AwarenessRanges.Clear();
        Turns.Clear();
        AimLooks.Clear();
        IdleWatches.Clear();
        WoundAnswers.Clear();
    }

    /// <summary>
    /// Forgets how a creature that is going away turned.
    /// </summary>
    /// <param name="instanceId">The instance ID of the creature.</param>
    private static void ForgetMoveStartTargets(int instanceId) {
        AwarenessRanges.Remove(instanceId);
        Turns.Remove(instanceId);
        AimLooks.Remove(instanceId);
        IdleWatches.Remove(instanceId);
        WoundAnswers.Remove(instanceId);
    }

    /// <summary>
    /// Has a creature that is going into a state where it starts a move roll whether it turns to another player first
    /// (see <see cref="TurnAtMoveStart"/>): before the actions of the state, so that what they measure of the player it
    /// goes after, and the move picked from that, is of the one it goes after now. A move starts where one is picked
    /// at random, or, for a creature that first measures the player - asks its ranges whether they are near or far -
    /// and goes from there straight into the pick, where it measures them (see <see cref="NoteOfState"/>).
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The state.</param>
    private static void OnStateEnter(Action<FsmState> orig, FsmState self) {
        // Where a wound that is played takes an FSM of the creature (see AnswerWound)
        if (_woundedNow != null) {
            try {
                NoteWoundState(self);
            } catch (Exception e) {
                Logger.Warn($"Could not note where a wound took '{self.Fsm?.Name}': {e.GetType().Name}: {e.Message}");
            }
        }

        if (self.Fsm?.FsmComponent is { } machine && machine != null && NoteOfState(self).Starts) {
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
    /// What a state has to do with the start of a move, worked out the first time it is gone into: a move starts in a
    /// state where one is picked at random, and in one that measures a player and has a way straight into such a state.
    /// </summary>
    /// <param name="state">The state.</param>
    private static MoveStartNote NoteOfState(FsmState state) {
        if (MoveStartStates.TryGetValue(state, out var note)) {
            return note;
        }

        var leads = false;
        foreach (var transition in state.Transitions ?? []) {
            if (transition != null && state.Fsm?.GetState(transition.ToState) is { } to && PicksAMove(to)) {
                leads = true;
                break;
            }
        }

        note = new MoveStartNote { Starts = PicksAMove(state) || leads && MeasuresAPlayer(state), Leads = leads };
        MoveStartStates.Add(state, note);
        return note;
    }

    /// <summary>
    /// Whether a state picks a creature's next move at random.
    /// </summary>
    /// <param name="state">The state.</param>
    private static bool PicksAMove(FsmState state) {
        foreach (var action in state.Actions ?? []) {
            if (action != null && MovePickActionNames.Contains(action.GetType().Name)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a state measures a player: asks where one is, faces one, aims at one or asks a range about one.
    /// </summary>
    /// <param name="state">The state.</param>
    private static bool MeasuresAPlayer(FsmState state) {
        foreach (var action in state.Actions ?? []) {
            if (action is GetHero or CheckAlertRange or CheckAlertRangeByName ||
                action != null && ActionRegistry.TargetedFsmActionTypes.Contains(action.GetType().Name)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// As a creature that this game runs for everyone starts a move, rolls whether it turns to another player within
    /// its reach, and turns it if so.
    ///
    /// A creature turns only while it fights (see <see cref="IsFighting"/>), and only after it has gone after the
    /// player it turned to for a few seconds and a couple of moves; then each move it starts may turn it, at most once
    /// a second: first on a small chance that grows with every roll that keeps it where it is, and reset by a turn.
    /// Who it turns to is rolled among the players within its reach, the nearer one and the one it has left alone the
    /// longer counting more, and the one it goes after counting half (see <see cref="PickTarget"/>). A creature whose
    /// player went down or left turns at once to one within its reach, and one whose player went out of its reach -
    /// too far, behind a wall - as soon as it may turn again: a sign from afar has it go after the one who gave it for
    /// that long.
    ///
    /// Only the game that runs the creature rolls, and the other games see it turn as they see it do anything. What
    /// another player set off plays here as it played in their game (SharedDice.PlaysAnotherGame), so nothing rolls
    /// while it does, and nothing rolls while the local player gives a sign, which the creature hears of them alone
    /// (Entity.Signs), nor while a wound of a player plays, after which a move it started turns it to them. A move
    /// that a creature which answers a wound starts gives it back whom it went after first (see
    /// <see cref="AnswerWound"/>).
    /// </summary>
    /// <param name="machine">The FSM of the creature.</param>
    /// <param name="state">The state where the move starts.</param>
    private static void TurnAtMoveStart(PlayMakerFSM machine, FsmState state) {
        if (!AimsAtPlayers(machine) || !Entity.Entity.RunsHere(machine)) {
            return;
        }

        var owner = GetEnemyTargetOwner(machine.gameObject);
        var health = owner == null ? null : owner.GetComponent<HealthManager>();
        if (owner == null || health == null) {
            return;
        }

        // Every move it starts, whoever set it off (see WatchIdleBosses)
        NoteMoveForIdle(owner, health, machine, state);

        if (Save.SharedDice.PlaysAnotherGame || Entity.Entity.OwnSign != null) {
            return;
        }

        var id = owner.GetInstanceID();
        if (_woundedNow == owner) {
            return;
        }

        EndWoundAnswer(owner, id, $"starts a move in '{state.Name}' of '{machine.FsmName}'");

        // One that sings to a player of a song turns to them by the song (GetFsmActionTarget)
        if (TryGetSongTarget(id, out _)) {
            return;
        }

        var now = Time.unscaledTime;
        if (!Turns.TryGetValue(id, out var turns)) {
            // The first move it is seen to start counts as a turn to whom it goes after: one that goes after no one yet
            // and notices players by no range of its own goes after the nearest player it can reach
            Turns[id] = new TargetTurns { TurnedAt = now, MoveFrame = Time.frameCount, MoveAt = now, PicksMoves = true };
            if (GetApprovedEnemyTarget(owner) == null && !HasAwarenessRanges(owner)) {
                AcquireNearestNoticed(owner, $"as it started its first move in '{state.Name}' of '{machine.FsmName}'");
            }

            return;
        }

        turns.PicksMoves = true;

        // Two of its FSMs that start a move in the same frame start one move, and a pick that the state where the move
        // started leads straight into is a part of that move (the game notes the state it left as it leaves it)
        if (turns.MoveFrame == Time.frameCount || state.Fsm?.PreviousActiveState is { } previous &&
            previous != state && NoteOfState(previous) is { Starts: true, Leads: true } && LeadsInto(previous, state)) {
            return;
        }

        turns.MoveFrame = Time.frameCount;
        turns.MoveAt = now;
        turns.MovesSince++;

        if (!IsFighting(turns, now)) {
            return;
        }

        // A creature that has noticed nobody yet turns to nobody; one that notices no one by itself - a boss that only
        // ever looks at the player - goes after the nearest player within its reach while it goes after no one, where
        // it went after this game's player before, whoever stood nearer
        var held = GetApprovedEnemyTarget(owner);
        if (held == null) {
            if (!HasAwarenessRanges(owner)) {
                AcquireNearestNoticed(owner, $"as it started a move in '{state.Name}' of '{machine.FsmName}'");
            }

            return;
        }

        var candidates = FindReachablePlayers(owner);
        if (candidates.Count == 0) {
            return;
        }

        var forced = !candidates.Contains(held);
        var chance = 1f;
        if (forced) {
            // Out of its reach or unnoticed, it lets them go once its hold of TurnHoldTime is over, as everything else
            // that looks for a player for it does (see KeepsTarget); down or gone, at once
            if (IsHeldByTime(turns, now) && PlayerTargetRegistry.GetTrackedPlayers().Contains(held)) {
                return;
            }
        } else {
            if (candidates.Count < 2 || IsHolding(turns, now) || now - turns.RolledAt < TurnRollSpacing) {
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
            ? $"{DescribePlayer(held)}, whom it went after, was down or out of its reach"
            : $"it rolled under {chance:P0} after {turns.Misses} roll(s) that kept it where it was";
        Logger.Info(
            $"{owner.name} turned to {DescribePlayer(pick)} as it started a move in '{state.Name}' of " +
            $"'{machine.FsmName}': {why}; weights {DescribeWeights(candidates)}"
        );

        turns.LeftAt[held] = now;
        ApproveEnemyTarget(owner, pick);

        // Turned as the move starts, this move is the first with the one it turned to
        turns.MovesSince = 0;
    }

    /// <summary>
    /// Whether one of the ways out of a state leads straight into another.
    /// </summary>
    /// <param name="from">The state the way leads out of.</param>
    /// <param name="to">The state it would lead into.</param>
    private static bool LeadsInto(FsmState from, FsmState to) {
        foreach (var transition in from.Transitions ?? []) {
            if (transition != null && transition.ToState == to.Name) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Notes that a creature turned from one player to another, whatever turned it - its own roll, a range, a sign - so
    /// that it holds to the one it turned to before it rolls again: for the move it starts next and the one after.
    /// </summary>
    /// <param name="instanceId">The instance ID of the creature.</param>
    /// <param name="from">The player it went after before, or null for none.</param>
    private static void NoteTurn(int instanceId, GameObject? from) {
        if (!Turns.TryGetValue(instanceId, out var turns)) {
            Turns[instanceId] = turns = new TargetTurns();
        }

        turns.TurnedAt = Time.unscaledTime;
        turns.MovesSince = -1;
        turns.Misses = 0;
        if (from != null) {
            turns.LeftAt[from] = Time.unscaledTime;
        }
    }

    /// <summary>
    /// How a creature has turned, or null for one that never went after a player nor looked for one.
    /// </summary>
    /// <param name="owner">The creature, or null.</param>
    private static TargetTurns? TurnsOf(GameObject? owner) {
        return owner != null && Turns.TryGetValue(owner.GetInstanceID(), out var turns) ? turns : null;
    }

    /// <summary>
    /// How a creature has turned, written down the first time it is asked for, as a turn now.
    /// </summary>
    /// <param name="owner">The creature.</param>
    private static TargetTurns TurnsFor(GameObject owner) {
        var id = owner.GetInstanceID();
        if (!Turns.TryGetValue(id, out var turns)) {
            Turns[id] = turns = new TargetTurns { TurnedAt = Time.unscaledTime, MovesSince = -1 };
        }

        return turns;
    }

    /// <summary>
    /// Whether a creature fights a player already (see <see cref="IsFighting"/>) and picks its moves: whom it goes after
    /// is then up to its moves when another player wounds it, after it has answered the wound (see
    /// <see cref="AnswerWound"/>). One that picks no moves goes after the player who wounded it from there.
    /// </summary>
    /// <param name="creature">The creature.</param>
    internal static bool FightsAPlayer(GameObject creature) {
        var now = Time.unscaledTime;
        return TurnsOf(GetEnemyTargetOwner(creature)) is { } turns && PicksMovesLately(turns, now) &&
               IsFighting(turns, now);
    }

    /// <summary>
    /// Whether a creature picks its moves at random and started one within <see cref="MovePickerMemory"/>, so that it
    /// rolls whether to turn as it starts one rather than between times.
    /// </summary>
    /// <param name="turns">How it has turned.</param>
    /// <param name="now">The time now.</param>
    private static bool PicksMovesLately(TargetTurns turns, float now) {
        return turns.PicksMoves && now - turns.MoveAt <= MovePickerMemory;
    }

    /// <summary>
    /// Notes that a creature asked one of its ranges about a player, and whether the range found the player it goes
    /// after inside (see <see cref="IsFighting"/>).
    /// </summary>
    /// <param name="turns">How the creature has turned, or null for one that never went after a player.</param>
    /// <param name="foundTarget">Whether the range found the player it goes after inside.</param>
    private static void NoteRangeAnswer(TargetTurns? turns, bool foundTarget) {
        if (turns == null) {
            return;
        }

        turns.AskedAt = Time.unscaledTime;
        if (foundTarget) {
            turns.FoundAt = Time.unscaledTime;
        }
    }

    /// <summary>
    /// Whether a creature still holds to the player it last turned to before it may roll to turn again: for
    /// <see cref="TurnHoldTime"/>, and for one that picks its moves, the next two moves it starts as well.
    /// </summary>
    /// <param name="turns">How it has turned.</param>
    /// <param name="now">The time now.</param>
    private static bool IsHolding(TargetTurns turns, float now) {
        return PicksMovesLately(turns, now) && turns.MovesSince < TurnHoldMoves || IsHeldByTime(turns, now);
    }

    /// <summary>
    /// Whether a creature still keeps the player it last turned to whatever else: for <see cref="TurnHoldTime"/>. The
    /// moves it starts count only for when it may roll (see <see cref="IsHolding"/>): one that starts no move while the
    /// player it turned to is out of its reach - its ranges ask about them alone and never find them - would hold to
    /// them for as long as it counts as one that picks its moves.
    /// </summary>
    /// <param name="turns">How it has turned.</param>
    /// <param name="now">The time now.</param>
    private static bool IsHeldByTime(TargetTurns turns, float now) {
        return now - turns.TurnedAt < TurnHoldTime;
    }

    /// <summary>
    /// Whether a creature fights the player it goes after: it turned to them lately (<see cref="IsHolding"/>), one of
    /// its ranges found them inside within <see cref="FightMemory"/>, or it asks none of its ranges at all, like a boss
    /// that only ever looks at the player. A creature that does none of it - back in its corner after the player it
    /// went after has gone, watching for whoever comes - neither rolls nor holds to them.
    /// </summary>
    /// <param name="turns">How it has turned.</param>
    /// <param name="now">The time now.</param>
    private static bool IsFighting(TargetTurns turns, float now) {
        return IsHolding(turns, now) || now - turns.FoundAt <= FightMemory || now - turns.AskedAt > FightMemory;
    }

    /// <summary>
    /// Whom a creature goes after and keeps going after (see <see cref="KeepsTarget"/>), after it rolled whether to turn
    /// to another player if it picks no moves (see <see cref="RollBetweenTimes"/>); null when it keeps going after no
    /// one, and what looks for a player for it takes the nearest it finds. The one rule of whom a creature goes after
    /// for everything that looks for a player for it - its ranges, its sight, its walking - beside the turns it rolls
    /// as it starts a move (TurnAtMoveStart) and the wounds it answers (AnswerWound).
    /// </summary>
    /// <param name="owner">The creature.</param>
    private static GameObject? KeptTarget(GameObject owner) {
        var held = GetApprovedEnemyTarget(owner);
        if (held == null) {
            return null;
        }

        // One that answers a wound goes after the player who dealt it until it starts its next move, from afar too (see
        // AnswerWound)
        if (WoundAnswers.ContainsKey(owner.GetInstanceID())) {
            return held;
        }

        var turns = TurnsFor(owner);
        if (!KeepsTarget(owner, turns, held)) {
            return null;
        }

        if (!PicksMovesLately(turns, Time.unscaledTime)) {
            RollBetweenTimes(owner, turns, held);
            held = GetApprovedEnemyTarget(owner) ?? held;
        }

        return held;
    }

    /// <summary>
    /// Whether a creature keeps going after the player it goes after: while they are in the room and up, for
    /// <see cref="TurnHoldTime"/> after it turned to them (see <see cref="IsHeldByTime"/>) whatever else, and after that
    /// while they are within its reach and it notices them (see <see cref="IsNoticedBy"/>). Worked out no more often than
    /// <see cref="KeepSpacing"/>, since what looks for a player for it asks many times a frame.
    /// </summary>
    /// <param name="owner">The creature.</param>
    /// <param name="turns">How it has turned.</param>
    /// <param name="target">The player it goes after.</param>
    private static bool KeepsTarget(GameObject owner, TargetTurns turns, GameObject target) {
        var now = Time.unscaledTime;
        if (now - turns.KeptAt < KeepSpacing && turns.KeepTarget == target) {
            return turns.Keeps;
        }

        turns.KeptAt = now;
        turns.KeepTarget = target;
        turns.Keeps = PlayerTargetRegistry.GetTrackedPlayers().Contains(target) &&
                      (IsHeldByTime(turns, now) ||
                       IsWithinReach(owner.transform.position, target) && IsNoticedBy(owner, target));
        return turns.Keeps;
    }

    /// <summary>
    /// Rolls whether a creature that picks no moves turns from the player it keeps going after to another one, as one
    /// that picks its moves does as it starts one (see TurnAtMoveStart): once its hold is over, no more often than
    /// <see cref="TurnRollSpacing"/>, on the same chance that grows with every roll that kept it where it was, among the
    /// players within its reach that it notices, weighed the same way (see <see cref="PickTarget"/>). Only the game
    /// that runs the creature rolls.
    /// </summary>
    /// <param name="owner">The creature.</param>
    /// <param name="turns">How it has turned.</param>
    /// <param name="held">The player it keeps going after.</param>
    private static void RollBetweenTimes(GameObject owner, TargetTurns turns, GameObject held) {
        var now = Time.unscaledTime;
        if (IsHolding(turns, now) || now - turns.RolledAt < TurnRollSpacing) {
            return;
        }

        // Spaced also where nothing rolls, so that the game that does not run the creature works out where it runs once
        // a second, not each time it is asked
        turns.RolledAt = now;
        if (_woundedNow == owner || Save.SharedDice.PlaysAnotherGame || Entity.Entity.OwnSign != null ||
            !Entity.Entity.RunsCreatureHere(owner) || TryGetSongTarget(owner.GetInstanceID(), out _)) {
            return;
        }

        var candidates = FindReachablePlayers(owner);
        if (candidates.Count < 2 || !candidates.Contains(held)) {
            return;
        }

        var chance = Mathf.Min(TurnChanceFirst + TurnChanceStep * turns.Misses, TurnChanceMost);
        if (TurnDice.NextDouble() >= chance) {
            turns.Misses++;
            return;
        }

        var pick = PickTarget(owner, turns, candidates, held);
        if (pick == held) {
            turns.Misses++;
            return;
        }

        Logger.Info(
            $"{owner.name} turned to {DescribePlayer(pick)} between moves: it rolled under {chance:P0} after " +
            $"{turns.Misses} roll(s) that kept it where it was; weights {DescribeWeights(candidates)}"
        );
        turns.LeftAt[held] = now;
        ApproveEnemyTarget(owner, pick);
    }

    /// <summary>
    /// Has a creature that goes after no one go after the nearest player within its reach that it notices (see
    /// <see cref="FindReachablePlayers"/>), in the game that runs it, no more often than <see cref="LookSpacing"/>:
    /// what looks for a player for a creature that notices players by no range of its own - its moves, its aim, its
    /// walking - took the player of the game that runs it before, whoever stood nearer.
    /// </summary>
    /// <param name="owner">The creature.</param>
    /// <param name="why">What it does as it looks, for the log.</param>
    /// <returns>The player, or null for none.</returns>
    private static GameObject? AcquireNearestNoticed(GameObject owner, string why) {
        var turns = TurnsFor(owner);
        var now = Time.unscaledTime;
        if (now - turns.LookedAt < LookSpacing) {
            return null;
        }

        turns.LookedAt = now;
        if (!Entity.Entity.RunsCreatureHere(owner)) {
            return null;
        }

        var origin = (Vector2) owner.transform.position;
        GameObject? nearest = null;
        var nearestDistance = float.PositiveInfinity;
        foreach (var player in FindReachablePlayers(owner)) {
            var distance = ((Vector2) player.transform.position - origin).sqrMagnitude;
            if (distance < nearestDistance) {
                nearestDistance = distance;
                nearest = player;
            }
        }

        if (nearest != null && nearest != GetApprovedEnemyTarget(owner)) {
            Logger.Info($"{owner.name} goes after {DescribePlayer(nearest)}, the nearest player it notices, {why}");
            ApproveEnemyTarget(owner, nearest);
        }

        return nearest;
    }

    /// <summary>
    /// Whom a creature that moves by a component of its own - a walker - goes after, by the one rule for every creature:
    /// whom it keeps going after (see <see cref="KeptTarget"/>), or, for one that can be fought, the nearest player it
    /// notices as it first looks for one (see <see cref="AcquireNearestNoticed"/>); otherwise whom it was set on, or
    /// null for no one.
    /// </summary>
    /// <param name="part">The component's object.</param>
    /// <param name="why">What it does as it looks, for the log.</param>
    private static GameObject? MoverTarget(GameObject part, string why) {
        var owner = GetEnemyTargetOwner(part);
        if (owner == null) {
            return null;
        }

        return KeptTarget(owner) ??
               (owner.GetComponent<HealthManager>() != null ? AcquireNearestNoticed(owner, why) : null) ??
               GetApprovedEnemyTarget(owner);
    }

    /// <summary>
    /// Has a creature that this game runs keep going after a player, or look for one, by the one rule for every
    /// creature (see <see cref="KeptTarget"/>), as one of its FSMs aims at a player or asks where one is, no more often
    /// than <see cref="AimLookSpacing"/>: one that picks no moves rolls there whether to turn; one that no longer keeps
    /// the player it went after goes after the nearest player within its reach that it notices (see
    /// <see cref="AcquireNearestNoticed"/>), and so does one that notices players by no range of its own and goes after
    /// no one yet. Such a creature - no range, no sight, no move it picks - aimed at the player of the game that runs
    /// it before, whoever stood nearer, and never turned; one that fights by its aim alone after a range woke it went
    /// on after the player it woke for wherever they went.
    /// </summary>
    /// <param name="machine">The FSM, or null.</param>
    private static void LookAsItAims(PlayMakerFSM? machine) {
        if (machine == null) {
            return;
        }

        var owner = GetEnemyTargetOwner(machine.gameObject);
        if (owner == null) {
            return;
        }

        var id = owner.GetInstanceID();
        var now = Time.unscaledTime;
        if (AimLooks.TryGetValue(id, out var lookedAt) && now - lookedAt < AimLookSpacing) {
            return;
        }

        AimLooks[id] = now;
        if (!Entity.Entity.RunsHere(machine) || owner.GetComponent<HealthManager>() == null) {
            return;
        }

        if (KeptTarget(owner) == null && (GetApprovedEnemyTarget(owner) != null || !HasAwarenessRanges(owner))) {
            AcquireNearestNoticed(owner, "as it aimed at a player");
        }
    }

    /// <summary>
    /// Whether a state holds an action that aims at a player, which is pointed at the player its creature goes after
    /// (see ActionRegistry.IsRetargeted).
    /// </summary>
    /// <param name="state">The state.</param>
    private static bool AimsAtAPlayerNow(FsmState state) {
        foreach (var action in state.Actions ?? []) {
            if (action != null && action.Enabled && ActionRegistry.IsRetargeted(action.GetType().Name)) {
                return true;
            }
        }

        return false;
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
    /// Whether a player stands within <see cref="TargetReach"/> of a place, with no wall in between. A creature whose
    /// own place is inside a wall, like one that burrows, has no one within its reach, and turns as it did before.
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
    /// The ranges under a creature with which it notices players, and those its sight looks through, found the first
    /// time. One that the creature handed to something else as it started up is not under it any more and is not among
    /// them, unless its sight looks through it.
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

        // The ranges its sight looks through are ones it notices players with, whatever they are called (see
        // NoteSightAsked)
        foreach (var detector in owner.GetComponentsInChildren<LineOfSightDetector>(true)) {
            if (detector == null || LineOfSightDetectorAlertRangesField?.GetValue(detector) is not AlertRange[] sight) {
                continue;
            }

            foreach (var range in sight) {
                if (range != null && !found.Contains(range) && !IsNonAcquiringAlertRangeName(range.name)) {
                    found.Add(range);
                }
            }
        }

        AwarenessRanges[id] = ranges = found.ToArray();
        return ranges;
    }

    /// <summary>
    /// Whether a range is one with which a creature notices a player - it wakes, comes out of hiding or keeps watch by
    /// it - by the game's own word for it in its name, rather than one with which it chooses what to do to the player
    /// it goes after. A creature keeps going after a player it notices by one, and may turn to another player it
    /// notices by one (see <see cref="IsNoticedBy"/>, <see cref="KeepsTarget"/>).
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
            if (state != null && MeasuresAPlayer(state)) {
                aims = true;
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
