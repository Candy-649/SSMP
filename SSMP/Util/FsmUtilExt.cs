using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;

namespace SSMP.Util;

/// <summary>
/// Class for FSM extensions.
/// </summary>
public static class FsmUtilExt {
    /// <summary>
    /// Get a FSM action by state name and index.
    /// </summary>
    /// <param name="fsm">The FSM instance.</param>
    /// <param name="stateName">The name of the state.</param>
    /// <param name="index">The index of the action within that state.</param>
    /// <returns>The FsmStateAction from the FSM or null if the action could not be found.</returns>
    private static FsmStateAction? GetAction(this PlayMakerFSM fsm, string stateName, int index) {
        foreach (var t in fsm.FsmStates) {
            if (t.Name != stateName) {
                continue;
            }

            var actions = t.Actions;

            Array.Resize(ref actions, actions.Length + 1);

            return actions[index];
        }

        return null;
    }

    /// <summary>
    /// Get a FSM action by state name and index.
    /// </summary>
    /// <param name="fsm">The FSM instance.</param>
    /// <param name="stateName">The name of the state.</param>
    /// <param name="index">The index of the action within that state.</param>
    /// <typeparam name="T">The type of the action that extends FsmStateAction.</typeparam>
    /// <returns>The action from the FSM or null if the action could not be found.</returns>
    public static T? GetAction<T>(this PlayMakerFSM fsm, string stateName, int index) where T : FsmStateAction {
        return GetAction(fsm, stateName, index) as T;
    }

    /// <summary>
    /// Get the first FSM action by state name and type.
    /// </summary>
    /// <param name="fsm">The FSM instance.</param>
    /// <param name="stateName">The name of the state.</param>
    /// <typeparam name="T">The type of the action that extends FsmStateAction.</typeparam>
    /// <returns>The action from the FSM or null if the action could not be found.</returns>
    public static T GetFirstAction<T>(this PlayMakerFSM fsm, string stateName) where T : FsmStateAction {
        return fsm.GetState(stateName).Actions.OfType<T>().FirstOrDefault() ??
               throw new ArgumentException($"FSM state \"{stateName}\" does not have action of type \"{typeof(T)}\"", nameof(stateName));
    }

    /// <summary>
    /// Get an FSM state by its name.
    /// </summary>
    /// <param name="fsm">The FSM instance.</param>
    /// <param name="stateName">The name of the state.</param>
    /// <returns>The state from the FSM.</returns>
    /// <exception cref="ArgumentException">Thrown when the state is not found.</exception>
    public static FsmState GetState(this PlayMakerFSM fsm, string stateName) {
        return fsm.FsmStates.FirstOrDefault(t => t.Name == stateName) ??
               throw new ArgumentException($"FSM does not have state with name \"{stateName}\"", nameof(stateName));
    }

    /// <summary>
    /// Safely gets an FSM state by its name without throwing an exception if not found.
    /// </summary>
    /// <param name="fsm">The FSM instance.</param>
    /// <param name="stateName">The name of the state.</param>
    /// <returns>The state from the FSM or null if not found.</returns>
    public static FsmState? GetStateOrNull(this PlayMakerFSM fsm, string stateName) {
        return fsm.FsmStates.FirstOrDefault(t => t.Name == stateName);
    }

    /// <summary>
    /// Insert a FSM action in a state at a specific index.
    /// </summary>
    /// <param name="fsm">The FSM instance.</param>
    /// <param name="stateName">The name of the state.</param>
    /// <param name="action">The FSM action to insert.</param>
    /// <param name="index">The index at which to insert the action.</param>
    public static void InsertAction(this PlayMakerFSM fsm, string stateName, FsmStateAction action, int index) {
        foreach (FsmState t in fsm.FsmStates) {
            if (t.Name != stateName) continue;
            List<FsmStateAction> actions = t.Actions.ToList();

            actions.Insert(index, action);

            t.Actions = actions.ToArray();
            action.Init(t);
        }
    }

    /// <summary>
    /// Insert a method in a state at a specific index.
    /// </summary>
    /// <param name="fsm">The FSM instance.</param>
    /// <param name="stateName">The name of the state.</param>
    /// <param name="index">The index at which to insert the method.</param>
    /// <param name="method">The method to insert.</param>
    public static void InsertMethod(this PlayMakerFSM fsm, string stateName, int index, Action method) {
        InsertAction(fsm, stateName, new InvokeMethod(method), index);
    }

    /// <summary>
    /// Removes the first action in the given state of the given type from the FSM.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="stateName">The name of the state with the action to remove.</param>
    /// <typeparam name="T">The type of the action to remove.</typeparam>
    public static void RemoveFirstAction<T>(this PlayMakerFSM fsm, string stateName) {
        var state = fsm.GetState(stateName);
        if (state == null) {
            throw new ArgumentException("FSM does not have a state with the given name", nameof(stateName));
        }

        var skipped = false;
        state.Actions = state.Actions.Where(a => {
            if (skipped) {
                return true;
            }

            if (a.GetType() == typeof(T)) {
                skipped = true;
                return false;
            }

            return true;
        }).ToArray();
    }

    /// <summary>
    /// Sets a state machine into a state the way its own switch sets it, as it runs (see PlayMaker's Fsm.DoTransition):
    /// a way on that the state sends itself as it starts is taken once it has started, and what the state would have
    /// done after that is left, as in the game; the event that led there is from no one, rather than from whatever sent
    /// one last. Set with nothing of it running, a state that sent itself on as it started switched at once, in the
    /// middle of its own start, which then went on over what the next state had set: the knight's choice of a slash of
    /// wind went into its choice of one after a jump, then named the slash from the ground its next move again, slashed
    /// from the ground where the scene host's jumped and was set into the jump from there (Luminary's log of 11-19,
    /// 20:39:34.291 and 20:39:57.491; USER "它的boss在跳起来的时候经常定在空中").
    /// </summary>
    /// <param name="fsm">The state machine.</param>
    /// <param name="stateName">The name of the state.</param>
    public static void SetStateAsItRuns(this HutongGames.PlayMaker.Fsm fsm, string stateName) {
        fsm.DoAsItRuns(() => fsm.SetState(stateName));
    }

    /// <summary>
    /// Sets the state machine of a component into a state the way its own switch sets it, as it runs (see
    /// <see cref="SetStateAsItRuns(HutongGames.PlayMaker.Fsm, string)"/>).
    /// </summary>
    /// <param name="fsm">The component.</param>
    /// <param name="stateName">The name of the state.</param>
    public static void SetStateAsItRuns(this PlayMakerFSM fsm, string stateName) {
        fsm.Fsm.SetStateAsItRuns(stateName);
    }

    /// <summary>
    /// Does something to a state machine as it runs, and takes the way on that the state machine sent itself meanwhile
    /// once that is done (see <see cref="SetStateAsItRuns(HutongGames.PlayMaker.Fsm, string)"/>).
    /// </summary>
    /// <param name="fsm">The state machine.</param>
    /// <param name="change">What is done to it.</param>
    public static void DoAsItRuns(this HutongGames.PlayMaker.Fsm fsm, Action change) {
        var eventData = HutongGames.PlayMaker.Fsm.EventData;
        HutongGames.PlayMaker.Fsm.EventData = new FsmEventData();
        FsmExecutionStack.PushFsm(fsm);
        try {
            change();
            fsm.UpdateStateChanges();
        } finally {
            FsmExecutionStack.PopFsm();
            HutongGames.PlayMaker.Fsm.EventData = eventData;
        }
    }
}

/// <summary>
/// FSM action that simply invokes a method.
/// </summary>
internal class InvokeMethod : FsmStateAction {
    /// <summary>
    /// The action to execute.
    /// </summary>
    private readonly Action _action;

    /// <summary>
    /// Construct the FSM action with the given action.
    /// </summary>
    /// <param name="a"></param>
    public InvokeMethod(Action a) {
        _action = a;
    }

    /// <inheritdoc />
    public override void OnEnter() {
        _action.Invoke();
        Finish();
    }
}
