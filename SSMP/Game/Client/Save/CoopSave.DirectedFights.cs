using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Controllers of fights, in a checked two-player save: the part of the room that tells the creatures of a fight each
/// move, picks by the dice which moves come and when, and shows what goes with them. In the fight with the two dancers
/// it tells them where to go on every beat, and itself puts up the ring and the glow in the middle, with their sound,
/// that come before the two join in one move, and sets off the charges that cross the room while the two are away.
///
/// The creatures run in the scene host's game and the other game shows copies of them, but the controller is no
/// creature, so each game ran its own. The copies did what the host's controller told the host's creatures, while the
/// ring, the glow and the charges came when the other game's own controller rolled them: the player whose game doesn't
/// run the room saw the ring with no joined move after it, and later the joined move with no ring before it (USER
/// 10-07), and charges crossed the room for them at other times than for the host.
///
/// The other game's controller now follows the host's in every state. The host's tells the other game each state it
/// goes into, with the dice the state rolled, which pick the charges and their order too; the other game's goes into
/// the same state as the word comes, with those dice, and holds back every step it would take by itself, for as long
/// as it takes: every state of the host's is told, so a word that doesn't come means the host's is still where it
/// was. One of a game that runs the room now goes on by itself and tells the partner, as the other machines do (see
/// CoopSave.PacedMachines).
///
/// The start of the fight before the first beat each game still has by itself, since what it does there is for its
/// own player: the gates, the lights and the beat setting off. A word that comes while the controller here is still in
/// that start is kept, and the controller takes it where its start ends. Anything else that would send it into the
/// fight before that, like a dancer's word of a fight that is further on in the host's game, is turned away, so that
/// the start is played to the end.
///
/// The controller goes into a state of the host's word the way the game goes into a state in an update of its own:
/// the steps that the state's actions set going wait until it is in, rather than running in the middle of it. The
/// event that led into the state is from no one, as in a replay, so the state that notes who sent it, the one that
/// notes which dancer was struck down, notes no one rather than whatever sent an event last anywhere in the game. The
/// debris of a dancer that dies is then put nowhere in particular and flies from where it lies in the room, which is
/// left as it is.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What a controller of a fight here did with a state, for the line in the log as the room is left.
    /// </summary>
    private enum DirectedStep {
        /// <summary>
        /// The scene host's went into it and told the partner.
        /// </summary>
        Told,

        /// <summary>
        /// The one of the other game went into it on the scene host's word.
        /// </summary>
        Followed,

        /// <summary>
        /// The one of the other game, still in the start of the fight, didn't go into it, since it was sent there by
        /// something else than the end of its start.
        /// </summary>
        StayedInStart
    }

    /// <summary>
    /// The FSMs of <see cref="DirectorFsmNames"/> that changed state in the room, with the kind of controller of a
    /// fight each is, or null for none, so that it is found out once for each.
    /// </summary>
    private readonly Dictionary<Fsm, MachineKind?> _directors = new();

    /// <summary>
    /// The controllers of fights here that went into states in the room, by name, with how many in each way of
    /// <see cref="DirectedStep"/>.
    /// </summary>
    private readonly Dictionary<Fsm, (string Name, int[] Counts)> _directedStates = new();

    /// <summary>
    /// The kind of controller of a fight that an FSM is, if it is one.
    /// </summary>
    private MachineKind? FindDirector(Fsm fsm) {
        if (!DirectorFsmNames.Contains(fsm.Name)) {
            return null;
        }

        if (_directors.TryGetValue(fsm, out var known)) {
            return known;
        }

        MachineKind? kind = null;
        if (fsm.GameObject is { } gameObject && gameObject != null &&
            FindMachineKind(gameObject, fsm.Name) is { IsDirector: true } found) {
            kind = found;
        }

        _directors[fsm] = kind;
        return kind;
    }

    /// <summary>
    /// Decides what a change of state of the controller of a fight does. One into the start of the fight goes on as
    /// usual, as does any while the partner isn't in the room. Any other is told to the partner by the scene host's,
    /// and held back by the other game's until the host's word comes, or traded for a word that came already. The
    /// other game's, still in its start, goes into the fight only by the way out of its start.
    /// </summary>
    private MachineStep DecideDirectorSwitch(Fsm fsm, FsmState toState, MachineKind director) {
        // The state of the scene host's word that it is going into here
        if (fsm == _startingMachineRound && toState == _startingMachineState) {
            return MachineStep.None;
        }

        var ownStateNames = director.OwnStateNames!;
        if (ownStateNames.Contains(toState.Name) || GetCheckedPartner() is not { IsInLocalScene: true }) {
            return MachineStep.None;
        }

        if (IsSceneHost?.Invoke() == true) {
            return MachineStep.Tell;
        }

        if (fsm.ActiveState is { } from && ownStateNames.Contains(from.Name) && !GoesStraightTo(from, toState.Name)) {
            return MachineStep.StayInStart;
        }

        return _pendingMachineRounds.ContainsKey(fsm) ? MachineStep.TakePending : MachineStep.Hold;
    }

    /// <summary>
    /// Whether the controller of a fight here is in the fight, past its start, where it goes into the scene host's
    /// states from wherever it is.
    /// </summary>
    private static bool IsInDirectedPart(MachineKind kind, Fsm fsm) {
        return kind.OwnStateNames is { } ownStateNames && fsm.ActiveState is { } state &&
               !ownStateNames.Contains(state.Name);
    }

    /// <summary>
    /// Goes into a state on the controller of a fight here the way the game goes into a state in an update of its own:
    /// with the FSM as the one that runs, so that the steps that the state's actions take wait until it is in. The step
    /// it would then take by itself goes through the hook at once, to be held like any other.
    /// </summary>
    /// <param name="fsm">The FSM of the controller.</param>
    /// <param name="letThrough">The state that is gone into, which the hook lets through, or null for a change that
    /// goes through the hook as any other.</param>
    /// <param name="dice">The dice that the state rolled in the scene host's game, or null for none.</param>
    /// <param name="enter">What goes into the state.</param>
    private void EnterDirectedState(Fsm fsm, FsmState? letThrough, IReadOnlyList<int>? dice, Action enter) {
        // The event that led there is from no one, rather than from whatever sent one last
        var eventData = Fsm.EventData;
        Fsm.EventData = new FsmEventData();
        FsmExecutionStack.PushFsm(fsm);
        try {
            var lastStarting = (_startingMachineRound, _startingMachineState);
            if (letThrough != null) {
                (_startingMachineRound, _startingMachineState) = (fsm, letThrough);
            }

            try {
                SharedDice.Throw(dice, enter);
            } finally {
                (_startingMachineRound, _startingMachineState) = lastStarting;
            }

            fsm.UpdateStateChanges();
        } finally {
            FsmExecutionStack.PopFsm();
            Fsm.EventData = eventData;
        }
    }

    /// <summary>
    /// Counts what a controller of a fight here did with a state.
    /// </summary>
    private void CountDirectedState(Fsm fsm, DirectedStep step) {
        if (!_directedStates.TryGetValue(fsm, out var states)) {
            var name = fsm.GameObject is { } gameObject && gameObject != null ? gameObject.name : fsm.Name;
            states = (name, new int[Enum.GetValues(typeof(DirectedStep)).Length]);
            _directedStates[fsm] = states;
        }

        states.Counts[(int) step]++;
    }

    /// <summary>
    /// Writes in the log what the controllers of fights did in the room that is left, and forgets it. Throws nothing,
    /// since it goes with forgetting the machines as a scene changes, which nothing may keep from running.
    /// </summary>
    private void LogDirectedStates() {
        try {
            foreach (var (name, counts) in _directedStates.Values) {
                Logger.Info(
                    $"[Directed] The fight controller '{name}' told the partner {counts[(int) DirectedStep.Told]} " +
                    $"states, went into {counts[(int) DirectedStep.Followed]} on the scene host's word and stayed " +
                    $"in its start {counts[(int) DirectedStep.StayedInStart]} times when sent into the fight early"
                );
            }
        } catch (Exception) {
            // Writing in the log is all this does, so a log that fails leaves nothing else undone
        } finally {
            _directedStates.Clear();
        }
    }
}
