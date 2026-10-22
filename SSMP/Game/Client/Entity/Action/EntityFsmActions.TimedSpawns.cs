using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Mono.Cecil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Save;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Random = UnityEngine.Random;

// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedParameter.Local

namespace SSMP.Game.Client.Entity.Action;

// The actions that throw or spawn things on a clock for as long as their state lasts: the spit a boss sprays at the
// floor and the roof, the bubbles a brute breathes out, the shots a creature fires from three points, the explosions a
// beetle leaves along its roll, a trail of tar. The copy of the creature in the other game threw what one of these
// kinds throws with its own dice, so it flew other ways than what the creature threw, and two of the kinds it did not
// throw at all. The game that runs the creature now rolls a seed for each run of such an action as its state begins,
// and sends it with the run. Both games draw everything the action rolls from that seed, so the two throw the same
// things the same way, named alike (ThrownThings), in the same rhythm.
internal static partial class EntityFsmActions {
    /// <summary>
    /// The kinds of actions that throw or spawn things on a clock while their state lasts.
    /// </summary>
    private static readonly Type[] TimedTypes = [
        typeof(FlingObjectsFromGlobalPoolTime),
        typeof(FlingObjectsFromGlobalPoolVelTime),
        typeof(SpawnObjectFromGlobalPoolOverTime),
        typeof(SpawnObjectFromGlobalPoolOverTimeV2)
    ];

    /// <summary>
    /// The hooks on the updates of those actions, kept for as long as the game runs.
    /// </summary>
    private static readonly List<IDisposable> TimedHooks = [];

    /// <summary>
    /// The runs of those actions that roll from a seed of the game that runs the creature, by action.
    /// </summary>
    private static readonly ConditionalWeakTable<FsmStateAction, TimedRun> TimedRuns = new();

    /// <summary>
    /// When the state of each action without an OnEnter of its own was entered, as its last update found it (see
    /// <see cref="TellIfEntered"/>).
    /// </summary>
    private static readonly ConditionalWeakTable<FsmStateAction, StrongBox<float>> TimedVisits = new();

    /// <summary>
    /// The action whose update runs now, whose rolls and spawns belong to its run.
    /// </summary>
    private static FsmStateAction? _timedAction;

    /// <summary>
    /// The creatures and things already named for throwing on a clock, in each game, so that each is said once.
    /// </summary>
    private static readonly HashSet<string> SaidTimedRuns = [];

    /// <summary>
    /// Whether telling that an action without an OnEnter of its own was entered threw, which is only said once.
    /// </summary>
    private static bool _timedEnterFailed;

    /// <summary>
    /// Hooks the updates of the actions that throw on a clock: what they roll comes from the seed of their run, and
    /// what they throw is named as it comes out of the pool.
    /// </summary>
    private static void HookTimedActions() {
        foreach (var type in TimedTypes) {
            var update = type.GetMethod(
                nameof(FsmStateAction.OnUpdate),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );
            if (update == null || update.DeclaringType != type) {
                Logger.Error($"Could not find {type.Name}#OnUpdate, so what it throws is not the same in both games");
                continue;
            }

            try {
                TimedHooks.Add(new ILHook(update, ShareTimedRolls));
                TimedHooks.Add(new Hook(update, new Action<Action<FsmStateAction>, FsmStateAction>(OnTimedUpdate)));
            } catch (Exception e) {
                Logger.Error($"Could not hook {type.Name}#OnUpdate:\n{e}");
            }
        }
    }

    /// <summary>
    /// IL hook for the update of an action that throws on a clock: its rolls come from the dice of its run (see
    /// <see cref="RollTimed(float, float)"/>), and what it throws comes out of the pool through
    /// <see cref="SpawnTimed"/>.
    /// </summary>
    private static void ShareTimedRolls(ILContext il) {
        try {
            var c = new ILCursor(il);
            while (c.TryGotoNext(MoveType.Before, i => i.MatchCall(typeof(Random), nameof(Random.Range)))) {
                var range = (MethodReference) c.Next!.Operand;
                var ofWholeNumbers = range.Parameters.Count == 2 &&
                                     range.Parameters[0].ParameterType.MetadataType == MetadataType.Int32;
                c.Remove();
                if (ofWholeNumbers) {
                    c.EmitDelegate<Func<int, int, int>>(RollTimed);
                } else {
                    c.EmitDelegate<Func<float, float, float>>(RollTimed);
                }
            }

            c.Index = 0;
            var spawns = 0;
            while (c.TryGotoNext(
                       MoveType.Before,
                       i => i.MatchCall(typeof(ObjectPoolExtensions), nameof(ObjectPoolExtensions.Spawn))
                   )) {
                var spawn = (MethodReference) c.Next!.Operand;
                if (spawn.Parameters.Count != 3) {
                    continue;
                }

                c.Remove();
                c.EmitDelegate<Func<GameObject, Vector3, Quaternion, GameObject>>(SpawnTimed);
                spawns++;
            }

            if (spawns == 0) {
                Logger.Error(
                    $"Could not find where {il.Method.DeclaringType.Name} spawns what it throws, so it is not named"
                );
            }
        } catch (Exception e) {
            Logger.Error($"Could not change {il.Method.DeclaringType.Name}#{il.Method.Name} IL:\n{e}");
        }
    }

    /// <summary>
    /// Hook for the update of an action that throws on a clock, which has its rolls and spawns go to its run, and tells
    /// that an action without an OnEnter of its own was entered, at its first update of the visit.
    /// </summary>
    private static void OnTimedUpdate(Action<FsmStateAction> orig, FsmStateAction self) {
        if (self is SpawnObjectFromGlobalPoolOverTimeV2) {
            TellIfEntered(self);
        }

        var last = _timedAction;
        _timedAction = self;
        try {
            orig(self);
        } finally {
            _timedAction = last;
        }
    }

    /// <summary>
    /// Tells the creature an action of an FSM that this game runs belongs to that the action was entered, at its first
    /// update in a visit of its state, for an action that has no OnEnter of its own to hook (see
    /// FsmActionHooks.EnteredAtFirstUpdate). Its run is sent from there as any other action's data is.
    /// </summary>
    private static void TellIfEntered(FsmStateAction action) {
        var state = action.State;
        if (state == null || TimedRuns.TryGetValue(action, out var run) && run.IsCopy) {
            return;
        }

        var visit = TimedVisits.GetValue(action, _ => new StrongBox<float>(float.NaN));
        if (visit.Value.Equals(state.RealStartTime)) {
            return;
        }

        visit.Value = state.RealStartTime;
        try {
            FsmActionHooks.TellEntered(action);
        } catch (Exception e) {
            if (!_timedEnterFailed) {
                _timedEnterFailed = true;
                Logger.Error($"Could not tell that {action.GetType().Name} was entered:\n{e}");
            }
        }
    }

    /// <summary>
    /// A roll of the action whose update runs now, with the dice of its run, or with the game's own dice if it has
    /// none.
    /// </summary>
    private static float RollTimed(float min, float max) {
        if (_timedAction == null || !TimedRuns.TryGetValue(_timedAction, out var run)) {
            return Random.Range(min, max);
        }

        var own = Random.state;
        Random.state = run.Dice;
        try {
            return Random.Range(min, max);
        } finally {
            run.Dice = Random.state;
            Random.state = own;
        }
    }

    /// <summary>
    /// A roll of a whole number of the action whose update runs now, with the dice of its run, or with the game's own
    /// dice if it has none.
    /// </summary>
    private static int RollTimed(int min, int max) {
        if (_timedAction == null || !TimedRuns.TryGetValue(_timedAction, out var run)) {
            return Random.Range(min, max);
        }

        var own = Random.state;
        Random.state = run.Dice;
        try {
            return Random.Range(min, max);
        } finally {
            run.Dice = Random.state;
            Random.state = own;
        }
    }

    /// <summary>
    /// Takes what the action whose update runs now throws out of the pool, under the next name of its run if it has
    /// one, and has it looked at like anything else a creature spawns, in case it is a creature (EntityManager).
    /// </summary>
    private static GameObject SpawnTimed(GameObject prefab, Vector3 position, Quaternion rotation) {
        var action = _timedAction;
        GameObject spawned;
        if (action != null && TimedRuns.TryGetValue(action, out var run) && run.Names is { } names) {
            using (ThrownThings.ExpectSeries(names, prefab)) {
                spawned = prefab.Spawn(position, rotation);
            }
        } else {
            spawned = prefab.Spawn(position, rotation);
        }

        if (action != null) {
            EntitySpawnEvent?.Invoke(
                new EntitySpawnDetails {
                    Type = EntitySpawnType.FsmAction,
                    Action = action,
                    GameObject = spawned
                }
            );
        }

        return spawned;
    }

    /// <summary>
    /// Begins a run of an action of a creature that this game runs that throws on a clock, as its state begins, and
    /// writes it for the other game: the seed it rolls from, how far along its clock and its wait before the first
    /// throw are, and the names of what it throws, if it is something that is named (see ThrownThings).
    /// </summary>
    /// <returns>
    /// Whether the run goes to the other game. A creature it spawns goes there as one (EntityManager), and what it
    /// spawns at this game's player, like the rosaries a thief knocks out of them, stays in this game: the copy there
    /// would spawn it at its own player, and keeps such an action off them (see <see cref="ActsOnTheLocalPlayer"/>).
    /// </returns>
    private static bool WriteTimedRun(
        EntityNetworkData data,
        FsmStateAction action,
        GameObject? prefab,
        float timer,
        float delay
    ) {
        if (prefab == null || IsObjectInRegistry(prefab) || WorksOnThePlayerCharacter(action, false)) {
            TimedRuns.Remove(action);
            return false;
        }

        ThrownThings.Series? names = null;
        if (ThrownThings.CanBeThrown(prefab) && Entity.TryGetThrower(action, out var thrower)) {
            var hero = HeroController.SilentInstance;
            names = ThrownThings.BeginSeries(GamePatcher.TargetOf(thrower) ?? (hero != null ? hero.gameObject : null));
        }

        var seed = Random.Range(int.MinValue, int.MaxValue);
        SetTimedRun(action, new TimedRun(seed, names, false));

        data.Packet.Write(seed);
        data.Packet.Write(timer);
        data.Packet.Write(delay);
        ThrownThings.WriteSeries(data, names);

        SayTimedRun(action, prefab, true);
        return true;
    }

    /// <summary>
    /// Reads the run of an action that throws on a clock that <see cref="WriteTimedRun"/> wrote, or null for the replay
    /// that sets up a creature's first states: a player who walks in while it throws misses the rest of that run.
    /// </summary>
    private static TimedRun? ReadTimedRun(EntityNetworkData? data) {
        if (data == null) {
            return null;
        }

        var seed = data.Packet.ReadInt();
        var timer = data.Packet.ReadFloat();
        var delay = data.Packet.ReadFloat();
        var names = ThrownThings.ReadSeries(data);
        return new TimedRun(seed, names, true) {
            Timer = timer,
            Delay = delay
        };
    }

    /// <summary>
    /// Runs a replayed action that throws on a clock on the copy, the way its state runs it in the game (see
    /// <see cref="RunInState"/>), with its run: OnEnter at once, its clock set to where the game that runs the creature
    /// had it, and OnUpdate every frame until the scene host's FSM leaves the state.
    /// </summary>
    private static void RunTimed(FsmStateAction action, GameObject? prefab, TimedRun run) {
        if (prefab == null) {
            return;
        }

        // Entering the state it is still in leaves the state first, as the game does
        for (var i = ActionsInState.Count - 1; i >= 0; i--) {
            if (ActionsInState[i].Action == action) {
                ExitAt(i);
            }
        }

        action.Finished = false;
        action.OnEnter();
        SetTimedClock(action, run.Timer, run.Delay);
        SetTimedRun(action, run);
        SayTimedRun(action, prefab, false);

        new ActionInState {
            Fsm = action.Fsm,
            StateName = action.State.Name,
            Action = action,
            Coroutine = MonoBehaviourUtil.Instance.StartCoroutine(UpdateWhileInState()),
            ExitAction = () => ForgetTimedRun(action, run)
        }.Register();
        return;

        // An FSM stops running its state when its object is switched off or gone, and so does this
        IEnumerator UpdateWhileInState() {
            while (true) {
                yield return null;

                var owner = action.Fsm.GameObject;
                if (owner == null || !owner.activeInHierarchy) {
                    yield break;
                }

                action.OnUpdate();
            }
        }
    }

    /// <summary>
    /// Sets the clock of an action that throws on a clock, and its wait before the first throw where it has one.
    /// </summary>
    private static void SetTimedClock(FsmStateAction action, float timer, float delay) {
        switch (action) {
            case FlingObjectsFromGlobalPoolTime fling:
                fling.timer = timer;
                fling.delayTimer = delay;
                break;
            case FlingObjectsFromGlobalPoolVelTime flingVel:
                flingVel.timer = timer;
                flingVel.delayTimer = delay;
                break;
            case SpawnObjectFromGlobalPoolOverTime spawn:
                spawn.timer = timer;
                break;
            case SpawnObjectFromGlobalPoolOverTimeV2 spawnV2:
                spawnV2.timer = timer;
                break;
        }
    }

    /// <summary>
    /// Gives an action its run, in place of one it had.
    /// </summary>
    private static void SetTimedRun(FsmStateAction action, TimedRun run) {
        TimedRuns.Remove(action);
        TimedRuns.Add(action, run);
    }

    /// <summary>
    /// Takes its run off an action once its state is left, unless it was given another one meanwhile.
    /// </summary>
    private static void ForgetTimedRun(FsmStateAction action, TimedRun run) {
        if (TimedRuns.TryGetValue(action, out var current) && current == run) {
            TimedRuns.Remove(action);
        }
    }

    /// <summary>
    /// Says in the log, once for each creature and thing in each game, that a creature throws something on a clock with
    /// dice that both games share.
    /// </summary>
    private static void SayTimedRun(FsmStateAction action, GameObject prefab, bool here) {
        var creature = action.Fsm?.GameObject;
        var name = creature == null ? "?" : creature.name;
        if (!SaidTimedRuns.Add($"{name}|{prefab.name}|{here}")) {
            return;
        }

        var stateName = action.State?.Name ?? "?";
        Logger.Info(
            here
                ? $"[Thrown] '{name}' throws '{prefab.name}' on a clock in '{stateName}' with dice it sends to the " +
                  "other game (said once)"
                : $"[Thrown] The copy of '{name}' throws '{prefab.name}' on a clock in '{stateName}' with the dice " +
                  "of the game that runs it (said once)"
        );
    }

    #region FlingObjectsFromGlobalPoolTime

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, FlingObjectsFromGlobalPoolTime action) {
        return WriteTimedRun(data, action, action.gameObject?.Value, action.timer, action.delayTimer);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, FlingObjectsFromGlobalPoolTime action) {
        if (ReadTimedRun(data) is { } run) {
            RunTimed(action, action.gameObject?.Value, run);
        }
    }

    #endregion

    #region FlingObjectsFromGlobalPoolVelTime

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, FlingObjectsFromGlobalPoolVelTime action) {
        return WriteTimedRun(data, action, action.gameObject?.Value, action.timer, action.delayTimer);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, FlingObjectsFromGlobalPoolVelTime action) {
        if (ReadTimedRun(data) is { } run) {
            RunTimed(action, action.gameObject?.Value, run);
        }
    }

    #endregion

    #region SpawnObjectFromGlobalPoolOverTime

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SpawnObjectFromGlobalPoolOverTime action) {
        return WriteTimedRun(data, action, action.gameObject?.Value, action.timer, 0f);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, SpawnObjectFromGlobalPoolOverTime action) {
        if (ReadTimedRun(data) is { } run) {
            RunTimed(action, action.gameObject?.Value, run);
        }
    }

    #endregion

    #region SpawnObjectFromGlobalPoolOverTimeV2

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SpawnObjectFromGlobalPoolOverTimeV2 action) {
        return WriteTimedRun(data, action, action.gameObject?.Value, action.timer, 0f);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(
        EntityNetworkData? data,
        SpawnObjectFromGlobalPoolOverTimeV2 action
    ) {
        if (ReadTimedRun(data) is { } run) {
            RunTimed(action, action.gameObject?.Value, run);
        }
    }

    #endregion

    /// <summary>
    /// A run of an action that throws on a clock, from its state's beginning to its end: the dice it rolls with, from
    /// the seed of the game that runs the creature, and the names of what it throws.
    /// </summary>
    private sealed class TimedRun(int seed, ThrownThings.Series? names, bool isCopy) {
        /// <summary>
        /// The dice of the run, as they are after its rolls so far.
        /// </summary>
        public Random.State Dice = DiceOf(seed);

        /// <summary>
        /// The names of what the run throws, or null if it throws nothing that is named.
        /// </summary>
        public readonly ThrownThings.Series? Names = names;

        /// <summary>
        /// Whether the run is the copy's, replayed from the game that runs the creature.
        /// </summary>
        public readonly bool IsCopy = isCopy;

        /// <summary>
        /// How far along its clock the action was as its state began in the game that runs the creature.
        /// </summary>
        public float Timer { get; init; }

        /// <summary>
        /// How long the action waited before its first throw there.
        /// </summary>
        public float Delay { get; init; }

        /// <summary>
        /// The dice that a seed gives, without touching the game's own.
        /// </summary>
        private static Random.State DiceOf(int seed) {
            var own = Random.state;
            Random.InitState(seed);
            var dice = Random.state;
            Random.state = own;
            return dice;
        }
    }
}
