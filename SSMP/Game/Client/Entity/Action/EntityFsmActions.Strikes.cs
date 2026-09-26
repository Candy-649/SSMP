using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity.Action;

internal static partial class EntityFsmActions {
    /// <summary>
    /// The most states a strike may take an FSM through before it is back where it was struck.
    /// </summary>
    private const int MaxStrikeStates = 8;

    /// <summary>
    /// The most states that playing a strike here goes through before it gives up, in case its states lead round in a
    /// circle that the copy never leaves: the game cuts such a circle off by itself, and nothing here would.
    /// </summary>
    private const int MaxStrikeSteps = MaxStrikeStates * 4;

    /// <summary>
    /// The kinds of action that a strike on a copy may run here (see <see cref="PlayStrikeHere"/>): those that only
    /// work out numbers in the FSM's own variables, and read or set how the entity itself moves. A sound, an effect or
    /// anything done to another object is left to the scene host's game, which sends it as usual.
    /// </summary>
    private static readonly HashSet<Type> StrikeActionTypes = [
        typeof(SetFloatValue), typeof(SetIntValue), typeof(SetBoolValue), typeof(FloatAdd), typeof(FloatMultiply),
        typeof(FloatOperator), typeof(FloatClamp), typeof(FloatCompare), typeof(IntCompare), typeof(BoolTest),
        typeof(GetVelocity2d), typeof(SetVelocity2d), typeof(SetAngularVelocity2d)
    ];

    /// <summary>
    /// The kinds of action that the state an FSM is struck in may have: those that only watch for something to happen
    /// or wait. The game enters that state again at the end of the strike and starts them over, which changes nothing
    /// about how the entity moves; anything that would is not left out here.
    /// </summary>
    private static readonly HashSet<Type> StruckStateActionTypes = [
        typeof(GetPosition), typeof(GetVelocity2d), typeof(FloatCompare), typeof(IntCompare), typeof(BoolTest),
        typeof(Wait)
    ];

    /// <summary>
    /// The field that says whether an action of a kind runs every frame, or null for a kind without one, looked up
    /// once for each kind.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo?> EveryFrameFields = new();

    /// <summary>
    /// The hook that catches the events an FSM of a copy sends itself while a strike is played on it, put in place the
    /// first time a strike is played.
    /// </summary>
    private static Hook? _strikeEventHook;

    /// <summary>
    /// Whether putting <see cref="_strikeEventHook"/> in place failed, after which no strike is played here.
    /// </summary>
    private static bool _strikeEventHookFailed;

    /// <summary>
    /// The FSM of the copy that a strike is being played on, whose events are caught rather than handled.
    /// </summary>
    private static HutongGames.PlayMaker.Fsm? _strikeFsm;

    /// <summary>
    /// The state of <see cref="_strikeFsm"/> whose actions are being run.
    /// </summary>
    private static FsmState? _strikeState;

    /// <summary>
    /// The first event that <see cref="_strikeFsm"/> sent itself in the action being run and that leads anywhere from
    /// <see cref="_strikeState"/>, if any.
    /// </summary>
    private static FsmEvent? _strikeEvent;

    /// <summary>
    /// Plays here at once what the local player's strike makes an FSM of the copy of an entity do, if that is only a
    /// change in how the entity moves: the event leads from the state the scene host says the FSM is in, through states
    /// that do nothing but work out numbers and set the entity's own movement, back to that state. A bell that a player
    /// strikes sideways takes a new speed that way and falls on. The copy runs none of its FSMs, and the scene host's
    /// game hears of the strike only after the player has seen it land, so the copy fell on towards the player for a
    /// whole round trip before it was knocked away. Played here, the actions of the copy's own FSM work out the new
    /// speed from how the copy moves, the way the scene host's game then works it out for the entity. Anything more is
    /// left to the scene host.
    /// </summary>
    /// <param name="copyFsm">The FSM of the copy.</param>
    /// <param name="eventName">The event that the strike tells it.</param>
    /// <returns>
    /// Whether the strike was played here. If it was not, some of it may have been played before that turned out, and
    /// what it did to the copy's body is for the caller to take back.
    /// </returns>
    public static bool PlayStrikeHere(PlayMakerFSM copyFsm, string eventName) {
        var fsm = copyFsm.Fsm;
        try {
            if (HostStateOf(fsm) is not { } fromName || fsm.GetState(fromName) is not { } from ||
                FindTransition(fsm, from, eventName) is not { } start || start == from ||
                !IsStruckState(from) || !TryGetStrikeStates(fsm, from, start, out var states) ||
                !HookStrikeEvents()) {
                return false;
            }

            _strikeFsm = fsm;
            var state = start;
            for (var steps = 0; state != from; steps++) {
                state = steps < MaxStrikeSteps ? RunStrikeState(fsm, state) : null;

                // Gone somewhere that was not looked at first, or round and round: the scene host's game says how the
                // entity moves once it has taken the strike too
                if (state == null || state != from && !states.Contains(state)) {
                    return false;
                }
            }

            return true;
        } catch (Exception e) {
            Logger.Warn($"Could not play '{eventName}' here on '{copyFsm.gameObject.name}': {e.Message}");
            return false;
        } finally {
            _strikeFsm = null;
            _strikeState = null;
            _strikeEvent = null;
        }
    }

    /// <summary>
    /// Finds the state an event takes an FSM to from the given state, looking at the transitions of the whole FSM
    /// first, as the game does.
    /// </summary>
    internal static FsmState? FindTransition(HutongGames.PlayMaker.Fsm fsm, FsmState state, string eventName) {
        foreach (var transition in fsm.GlobalTransitions) {
            if (transition.EventName == eventName) {
                return transition.ToFsmState;
            }
        }

        foreach (var transition in state.Transitions) {
            if (transition.EventName == eventName) {
                return transition.ToFsmState;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an FSM may be struck in a state: one whose actions only watch or wait (see
    /// <see cref="StruckStateActionTypes"/>), since the game enters it again when the strike is done.
    /// </summary>
    private static bool IsStruckState(FsmState state) {
        foreach (var action in state.Actions) {
            if (action == null || action.Enabled && !StruckStateActionTypes.Contains(action.GetType())) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Looks at every state that a strike could take an FSM through before it is back in the state it was struck in:
    /// each must do nothing but what a strike may do here (see <see cref="StrikeActionTypes"/>), and each way on from
    /// it - by an event its actions may send, or by FINISHED once they are done - must lead to another such state or
    /// back, and one of them back.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="from">The state the FSM was struck in.</param>
    /// <param name="start">The state the strike takes it to first.</param>
    /// <param name="states">The states the strike could take it through, other than the one it was struck in.</param>
    /// <returns>Whether the strike may be played here.</returns>
    private static bool TryGetStrikeStates(
        HutongGames.PlayMaker.Fsm fsm,
        FsmState from,
        FsmState start,
        out HashSet<FsmState> states
    ) {
        states = [];
        var comesBack = false;
        var open = new Stack<FsmState>();
        open.Push(start);
        while (open.Count > 0) {
            var state = open.Pop();
            if (state == from) {
                comesBack = true;
                continue;
            }

            if (!states.Add(state)) {
                continue;
            }

            if (states.Count > MaxStrikeStates) {
                return false;
            }

            foreach (var action in state.Actions) {
                if (action == null || action.Enabled && !IsStrikeAction(action)) {
                    return false;
                }
            }

            // Once every action is done the state goes on by FINISHED, and one with nowhere to go then stays put
            if (FindTransition(fsm, state, FsmEvent.Finished.Name) is not { } finished) {
                return false;
            }

            open.Push(finished);

            // An event that leads nowhere from the state is let go by the game, and the state goes on
            foreach (var action in state.Actions) {
                if (!action.Enabled) {
                    continue;
                }

                foreach (var sent in EventsSentBy(action)) {
                    if (sent != null && !FsmEvent.IsNullOrEmpty(sent) &&
                        FindTransition(fsm, state, sent.Name) is { } next) {
                        open.Push(next);
                    }
                }
            }
        }

        return comesBack;
    }

    /// <summary>
    /// The events that an action of <see cref="StrikeActionTypes"/> may send its FSM.
    /// </summary>
    private static FsmEvent?[] EventsSentBy(FsmStateAction action) => action switch {
        FloatCompare compare => [compare.equal, compare.lessThan, compare.greaterThan],
        IntCompare compare => [compare.equal, compare.lessThan, compare.greaterThan],
        BoolTest test => [test.isTrue, test.isFalse],
        _ => []
    };

    /// <summary>
    /// Whether an action may be run here for a strike: one of <see cref="StrikeActionTypes"/>, done once rather than
    /// every frame, keeping what it works out in the FSM's own variables rather than in ones the whole game shares, and
    /// moving only the entity itself.
    /// </summary>
    private static bool IsStrikeAction(FsmStateAction action) {
        var type = action.GetType();
        if (!StrikeActionTypes.Contains(type) || action.Fsm is not { } fsm) {
            return false;
        }

        if (!EveryFrameFields.TryGetValue(type, out var everyFrame)) {
            everyFrame = type.GetField("everyFrame", BindingFlags.Instance | BindingFlags.Public);
            if (everyFrame?.FieldType != typeof(bool)) {
                everyFrame = null;
            }

            EveryFrameFields[type] = everyFrame;
        }

        if (everyFrame?.GetValue(action) is true) {
            return false;
        }

        return action switch {
            SetFloatValue set => IsOwnVariable(fsm, set.floatVariable),
            SetIntValue set => IsOwnVariable(fsm, set.intVariable),
            SetBoolValue set => IsOwnVariable(fsm, set.boolVariable),
            FloatAdd add => IsOwnVariable(fsm, add.floatVariable),
            FloatMultiply multiply => IsOwnVariable(fsm, multiply.floatVariable),
            FloatOperator operation => IsOwnVariable(fsm, operation.storeResult),
            FloatClamp clamp => IsOwnVariable(fsm, clamp.floatVariable),
            GetVelocity2d get => IsOwner(fsm, get.gameObject) && IsOwnVariable(fsm, get.vector) &&
                                 IsOwnVariable(fsm, get.x) && IsOwnVariable(fsm, get.y),
            SetVelocity2d set => IsOwner(fsm, set.gameObject),
            SetAngularVelocity2d spin => IsOwner(fsm, spin.gameObject),
            _ => true
        };
    }

    /// <summary>
    /// Whether an action's target is the object its FSM runs on, which for the copy is the copy itself.
    /// </summary>
    private static bool IsOwner(HutongGames.PlayMaker.Fsm fsm, FsmOwnerDefault target) {
        return fsm.GetOwnerDefaultTarget(target) == fsm.GameObject;
    }

    /// <summary>
    /// Whether what an action writes to is the FSM's own: one of its variables, a value that the action holds itself
    /// rather than a variable, or nothing at all. A variable that the whole game shares would be changed for everything
    /// else in the scene client's game that reads it.
    /// </summary>
    private static bool IsOwnVariable(HutongGames.PlayMaker.Fsm fsm, NamedVariable? variable) {
        return variable == null || variable.IsNone || !variable.UseVariable || fsm.Variables.Contains(variable);
    }

    /// <summary>
    /// Runs the actions of a state of the copy for a strike the way the game enters a state: each in turn, until one
    /// sends the FSM an event that leads somewhere, and when all of them are done, the state goes on by FINISHED.
    /// </summary>
    /// <param name="fsm">The FSM of the copy.</param>
    /// <param name="state">The state.</param>
    /// <returns>The state it goes on to, or null if it goes nowhere.</returns>
    private static FsmState? RunStrikeState(HutongGames.PlayMaker.Fsm fsm, FsmState state) {
        _strikeState = state;
        foreach (var action in state.Actions) {
            if (!action.Enabled) {
                continue;
            }

            _strikeEvent = null;
            action.Init(state);
            action.Finished = false;
            action.Entered = true;
            action.OnEnter();

            if (_strikeEvent is { } sent) {
                return FindTransition(fsm, state, sent.Name);
            }
        }

        return FindTransition(fsm, state, FsmEvent.Finished.Name);
    }

    /// <summary>
    /// Puts the hook in place that catches what an FSM of a copy sends itself while a strike is played on it.
    /// </summary>
    /// <returns>Whether the hook is in place.</returns>
    private static bool HookStrikeEvents() {
        if (_strikeEventHook != null) {
            return true;
        }

        if (_strikeEventHookFailed) {
            return false;
        }

        try {
            _strikeEventHook = new Hook(
                typeof(HutongGames.PlayMaker.Fsm).GetMethod(
                    "ProcessEvent",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    [typeof(FsmEvent), typeof(FsmEventData)],
                    null
                )!,
                new Action<Action<HutongGames.PlayMaker.Fsm, FsmEvent, FsmEventData>, HutongGames.PlayMaker.Fsm,
                    FsmEvent, FsmEventData>(OnStrikeProcessEvent)
            );
            return true;
        } catch (Exception e) {
            _strikeEventHookFailed = true;
            Logger.Warn($"Could not hook the events of FSMs, so strikes on copies wait for the scene host:\n{e}");
            return false;
        }
    }

    /// <summary>
    /// Hook for the processing of an event by an FSM, which catches the first event that the FSM of a copy sends
    /// itself while a strike is played on it (see <see cref="RunStrikeState"/>). The copy's FSM is switched off, and
    /// taken in there the event would go nowhere or run a state of its own that nothing ever leaves.
    /// </summary>
    private static void OnStrikeProcessEvent(
        Action<HutongGames.PlayMaker.Fsm, FsmEvent, FsmEventData> orig,
        HutongGames.PlayMaker.Fsm self,
        FsmEvent fsmEvent,
        FsmEventData eventData
    ) {
        if (_strikeFsm == null || !ReferenceEquals(self, _strikeFsm)) {
            orig(self, fsmEvent, eventData);
            return;
        }

        // An event that leads nowhere is let go by the game, and the state goes on with its next action
        if (_strikeEvent == null && _strikeState != null && !FsmEvent.IsNullOrEmpty(fsmEvent) &&
            FindTransition(self, _strikeState, fsmEvent.Name) != null) {
            _strikeEvent = fsmEvent;
        }
    }
}
