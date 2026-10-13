using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;
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
/// A controller whose only work is to put up parts, like the two of the fight on the stage, isn't followed in its
/// states: the other game shows its parts the way they go up in the host's game (see CoopSave.ShownParts).
///
/// The other game's controller now follows the host's in every state. The host's tells the other game each state it
/// goes into, with the dice the state rolled, which pick the charges, and the order that the lists of charges were
/// shuffled into (see SharedDice); the other game's goes into
/// the same state as the word comes, with those dice, and holds back every step it would take by itself, for as long
/// as it takes: every state of the host's is told, so a word that doesn't come means the host's is still where it
/// was. One of a game that runs the room now goes on by itself and tells the partner, as the other machines do (see
/// CoopSave.PacedMachines).
///
/// Where a part goes up hangs on more than the dice of the state, though: on where an earlier state left it, or on
/// what the state puts it next to, which each game came to by itself. So the word also says where the parts right under
/// the controller stand that the state switched on or moved, and the other game puts its own there, rather than work
/// it out again, as the user asked for the parts of the fight on the stage (USER 10-13: 烟花的位置不能直接拿到吗).
///
/// The start of the fight before the first beat each game still has by itself, since what it does there is for its
/// own player: the gates, the lights and the beat setting off. A word that comes while the controller here is still in
/// that start is kept, and the controller takes it where its start ends. Anything else that would send it into the
/// fight before that, like a dancer's word of a fight that is further on in the host's game, is turned away, so that
/// the start is played to the end.
///
/// The parts that a state of a controller switches on start their own state machines, which may roll as they start.
/// One switched on again starts again at once, in the middle of the state, so its rolls go with the dice of the state;
/// but the game starts one that was never on a moment later, after the state, so the first time, each game rolled its
/// own. Both games now start the parts that a state switched on as the state is gone into.
///
/// The controller goes into a state of the host's word the way the game goes into a state in an update of its own:
/// the steps that the state's actions set going wait until it is in, rather than running in the middle of it. The
/// event that led into the state is from no one, as in a replay, so the state that notes who sent it, the one that
/// notes which dancer was struck down, notes no one rather than whatever sent an event last anywhere in the game. The
/// debris of a dancer that dies is then put nowhere in particular by the state, but goes where the host's was put as
/// the word says, before it flies.
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
    /// The list that the state machines under a controller of a fight are gathered in to be started, kept to be used
    /// again for each state.
    /// </summary>
    private static readonly List<PlayMakerFSM> StartingParts = [];

    /// <summary>
    /// Whether <see cref="StartingParts"/> is being gone through.
    /// </summary>
    private static bool _startingParts;

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
        if (ownStateNames.Contains(toState.Name) || !IsMemberInRoom()) {
            return MachineStep.None;
        }

        // A controller whose parts the other game shows goes on by itself here, and only its parts are told
        if (IsSceneHost?.Invoke() == true) {
            return director.ShowsParts ? MachineStep.None : MachineStep.Tell;
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
    /// <param name="word">The scene host's word of the state, with where the host's parts stand after it, or null.
    /// </param>
    private void EnterDirectedState(
        Fsm fsm,
        FsmState? letThrough,
        IReadOnlyList<int>? dice,
        Action enter,
        CoopSaveUpdate? word = null
    ) {
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
                SharedDice.Throw(dice, () => {
                    enter();
                    StartSwitchedOnParts(fsm);
                });
            } finally {
                (_startingMachineRound, _startingMachineState) = lastStarting;
            }

            if (word != null) {
                try {
                    PutPartsWhereTheHostsAre(fsm, word);
                } catch (Exception e) {
                    LogInteractionError(e);
                }
            }

            fsm.UpdateStateChanges();
        } finally {
            FsmExecutionStack.PopFsm();
            Fsm.EventData = eventData;
        }
    }

    /// <summary>
    /// Starts the state machines of the parts of a controller of a fight here that are on but haven't started yet,
    /// which are those that the state it went into switched on for the first time: the game would start them a moment
    /// later, before they first update. One that was switched on before starts again as it is switched on, in the
    /// middle of the state, so what it rolls as it starts goes with the dice of the state; started a moment later, it
    /// rolled by itself in each game. Started at once, it is the same in both games from the first time on. A part that
    /// throws as it starts is written in the log and left, as the game leaves a part that throws as it starts.
    /// </summary>
    private void StartSwitchedOnParts(Fsm fsm) {
        if (fsm.GameObject is { } gameObject && gameObject != null) {
            StartMachinesUnder(gameObject.transform);
        }
    }

    /// <summary>
    /// Starts the state machines on an object and under it that are on but haven't started yet, as the game would a
    /// moment later, before they first update. One that throws as it starts is written in the log and left, as the
    /// game leaves one that throws as it starts.
    /// </summary>
    private void StartMachinesUnder(Transform top) {
        // A part that starts can set off a state of another controller, which starts its own parts with a list of its
        // own
        var parts = _startingParts ? [] : StartingParts;
        var wasStarting = _startingParts;
        _startingParts = true;
        try {
            top.GetComponentsInChildren(false, parts);
            foreach (var part in parts) {
                if (part == null || !part.isActiveAndEnabled || part.Fsm is not { Started: false } partFsm) {
                    continue;
                }

                try {
                    partFsm.Start();
                } catch (Exception e) {
                    LogInteractionError(e);
                }
            }
        } finally {
            parts.Clear();
            _startingParts = wasStarting;
        }
    }

    /// <summary>
    /// Takes the parts right under a controller of a fight of the scene host's, with whether each is on and where it
    /// is, before a state of it, to tell after the state which of them it switched on or moved.
    /// </summary>
    private static List<(Transform Part, bool WasOn, Vector3 Was)> TakeParts(Fsm fsm) {
        var parts = new List<(Transform, bool, Vector3)>();
        if (fsm.GameObject is { } gameObject && gameObject != null) {
            foreach (Transform part in gameObject.transform) {
                parts.Add((part, part.gameObject.activeSelf, part.position));
            }
        }

        return parts;
    }

    /// <summary>
    /// Writes in the scene host's word of a state of a controller of a fight where the parts right under it stand that
    /// the state switched on or moved: their names in <see cref="CoopSaveUpdate.Names"/>, and the x and y of each in
    /// <see cref="CoopSaveUpdate.Values"/>. A creature among them is left out, since its own sync puts it where it is.
    /// </summary>
    private static void TellPartsMoved(CoopSaveUpdate word, List<(Transform Part, bool WasOn, Vector3 Was)> before) {
        foreach (var (part, wasOn, was) in before) {
            if (part == null || !part.gameObject.activeSelf || (wasOn && part.position == was) ||
                EntityProcessor.IsRegistered(part.gameObject)) {
                continue;
            }

            var position = part.position;
            word.Names.Add(part.name);
            word.Values.Add(position.x);
            word.Values.Add(position.y);
        }
    }

    /// <summary>
    /// Puts the parts of a controller of a fight here that the scene host's word names where the host's stand after
    /// the state, which the state here also put somewhere, from where it went there: a part that an earlier state left
    /// somewhere, or that a state that this game did not go through would have put somewhere, goes up where the host's
    /// does all the same (USER 10-13).
    /// </summary>
    private static void PutPartsWhereTheHostsAre(Fsm fsm, CoopSaveUpdate word) {
        if (word.Names.Count == 0 || word.Values.Count < 2 * word.Names.Count ||
            fsm.GameObject is not { } gameObject || gameObject == null) {
            return;
        }

        var controller = gameObject.transform;
        for (var i = 0; i < word.Names.Count; i++) {
            if (controller.Find(word.Names[i]) is not { } part || EntityProcessor.IsRegistered(part.gameObject)) {
                continue;
            }

            part.position = new Vector3(word.Values[2 * i], word.Values[2 * i + 1], part.position.z);
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
