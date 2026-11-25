using System.Collections.Generic;
using HutongGames.PlayMaker;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The moves of a creature that every game runs by itself that act on one player and go by what that player does and
/// what befalls them, which the game of that player leads: an extraction by them (see CoopSave.OwnExtracts) and a grab
/// of them (see CoopSave.OwnCatches), like the struggle of the player held in a maw, which that player presses, and
/// the chewing that goes on until their health is low. No other game can play such a move by itself.
///
/// The game of that player plays it by itself, as the game does, and in a game that follows the scene host's, that is
/// a move of its own to its end, whose every step, the one out of it too, it tells the scene host's game, with the
/// number that it gave the move (see <see cref="TellLedStep"/>). The scene host's game, playing that player's move,
/// takes these steps as they come and no step of its own creature's (see <see cref="TakeLedStep"/>), and every other
/// game takes the steps of the scene host's: nothing of a game's own takes the creature on in a move that another game
/// leads, like a hurt of its own player or a look at its own player's health, until the word of the game that leads it
/// does, or its death (see <see cref="HoldsInLedMove"/>; USER 11-25 "套现有规则": the caught player's game leads).
/// The scene host's game takes up one move at a time: one of another player's that comes while the creature is in a
/// move already goes nowhere, and its steps neither (see <see cref="MeetLedMove"/>). With three or four players, each
/// move is the game of its own player's, and every other game shows it.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What an update of a creature that every game runs by itself is when it tells a step of a move that the game of
    /// the player it acts on leads (see <see cref="TellLedStep"/>): the state that it went from, the one that it went
    /// to, and the number of the move in <see cref="CoopSaveUpdate.Sequence"/>.
    /// </summary>
    private const ushort OwnLedStepPart = 17;

    /// <summary>
    /// The state machines of creatures that every game runs by itself here in a move that another player's game leads
    /// (see <see cref="FollowLedMove"/>).
    /// </summary>
    private readonly Dictionary<Fsm, LedMove> _ledMoves = new();

    /// <summary>
    /// The latest move of each player's that the scene host's game met on each state machine of a creature that every
    /// game runs by itself, by its number, whether it took it up or not (see <see cref="MeetLedMove"/>): a step of it,
    /// or of one before it, that comes after goes nowhere.
    /// </summary>
    private readonly Dictionary<(Fsm Fsm, ushort Leader), ulong> _ledMovesMet = new();

    /// <summary>
    /// The steps of moves of other players' that came to the scene host's game before it met the move, in the order
    /// that they came, each taken as the move starts here or let go of where it never does (see
    /// <see cref="MeetLedMove"/>). The steps that the state machine of a creature takes at once as a blow extracts from
    /// it are told before the blow, and come before it, and the blow may be held besides (review of 92d7aab: the
    /// scene host's game let them go, and the fat one stood where it gave way to the blow all through the extraction).
    /// </summary>
    private readonly Dictionary<Fsm, List<(ushort Leader, ulong Number, string Into)>> _earlyLedSteps = new();

    /// <summary>
    /// Whether the scene host's game takes a step of a move that another game leads now (see
    /// <see cref="TakeLedStep"/>).
    /// </summary>
    private bool _takingLedStep;

    /// <summary>
    /// A move of another player's that a state machine of a creature that every game runs by itself here follows,
    /// which the game of that player leads.
    /// </summary>
    /// <param name="states">The states of the move.</param>
    /// <param name="hero">The variable of the hero of the state machine, which holds that player's character until the
    /// move is over, if it has one.</param>
    /// <param name="leader">The player.</param>
    /// <param name="number">The number that their game gave the move, which its steps carry (see
    /// <see cref="TellLedStep"/>): the number of their blow that set it off, for an extraction.</param>
    private sealed class LedMove(HashSet<FsmState> states, FsmGameObject? hero, ushort leader, ulong number) {
        public readonly HashSet<FsmState> States = states;
        public readonly FsmGameObject? Hero = hero;
        public readonly ushort Leader = leader;
        public readonly ulong Number = number;

        /// <summary>
        /// The switch of its own that it was last held at, which the log tells once.
        /// </summary>
        public string? Held;
    }

    /// <summary>
    /// Has a state machine of a creature that every game runs by itself here follow a move of another player's, which
    /// the game of that player leads (see <see cref="HoldsInLedMove"/>).
    /// </summary>
    private void FollowLedMove(Fsm fsm, HashSet<FsmState> states, FsmGameObject? hero, ushort leader, ulong number) {
        _ledMoves[fsm] = new LedMove(states, hero, leader, number);
    }

    /// <summary>
    /// The player whose move a state machine of a creature that every game runs by itself is in here, as a throw names
    /// them (see ThrownThings.CodeOf): of another player's, which their game leads, or of this game's player, like an
    /// extraction by them, or no one's.
    /// </summary>
    private ushort LeaderCodeOf(Fsm fsm) {
        return _ledMoves.TryGetValue(fsm, out var move) ? move.Leader
            : ExtractStates(fsm) is { } states && fsm.ActiveState is { } at && states.Contains(at)
                ? ThrownThings.ThrowersPlayer
                : ThrownThings.NoPlayer;
    }

    /// <summary>
    /// Whether a state machine of a creature that every game runs by itself in this game leads a move that acts on this
    /// game's player, through which it tells the scene host's game every step (see <see cref="TellLedStep"/>).
    /// </summary>
    private bool LeadsMove(Fsm fsm) {
        return _ownLeaps.TryGetValue(fsm, out var leap) && leap is { Led: true, Through: false };
    }

    /// <summary>
    /// Whether a state machine in a move that another player's game leads (see <see cref="FollowLedMove"/>) is held at
    /// a switch of its own: only a step of the scene host's, a step that the scene host's game takes from the game that
    /// leads (see <see cref="TakeLedStep"/>) or the death of the creature takes it out of the move, its own here or
    /// another game's that it is told, as the game tells its state machines of it (HealthManager.Die): a death by a
    /// blow of the scene host's player as another player extracted from it would go nowhere there. In the scene host's
    /// game, which takes every step of the move from the game that leads it, so does none of its own within the move
    /// either, but a game that follows the scene host's goes through the move by the ways that its states take by what
    /// they do themselves (see TakenByItself), as it does through any move of such a creature, among whose steps those
    /// of the scene host's are found (see TakeOwnStep): a step of the scene host's that it gets to so waits for it to
    /// get there (see KeepOwnStep), which it never would, held. Not by a way that something of this game takes it, like
    /// a blow of its own player that extracts from it, which would have this game lead a move of its own in it. One out
    /// of the move ends it here, and the hero of the state machine is this game's player again.
    /// </summary>
    private bool HoldsInLedMove(Fsm fsm, FsmState toState) {
        if (!_ledMoves.TryGetValue(fsm, out var move) || fsm.ActiveState is not { } at || !move.States.Contains(at)) {
            return false;
        }

        var creature = OwnCreatures.Owning(fsm.GameObject) ?? fsm.GameObject;
        var place = OwnCreatures.PlaceOf(fsm.GameObject);
        if (OwnCreatures.PlaysSceneHostsStep(fsm) || _takingLedStep ||
            creature.TryGetComponent<HealthManager>(out var health) && (health.isDead || health.hp <= 0)) {
            if (!move.States.Contains(toState)) {
                _ledMoves.Remove(fsm);
                if (move.Hero != null && HeroController.SilentInstance is { } hero) {
                    move.Hero.Value = hero.gameObject;
                }

                var why = _takingLedStep || OwnCreatures.PlaysSceneHostsStep(fsm)
                    ? "as the game that leads it tells"
                    : "as it dies";
                Logger.Info(
                    $"[Own creatures] '{place}' goes out of a move of the player {move.Leader}'s from '{at.Name}' to " +
                    $"'{toState.Name}' of '{fsm.Name}', {why}"
                );
            }

            return false;
        }

        if (move.States.Contains(toState) && IsSceneHost?.Invoke() != true && fsm.LastTransition is { } way &&
            TakenByItself(at, way)) {
            return false;
        }

        var held = $"{at.Name}>{toState.Name}";
        if (move.Held != held) {
            move.Held = held;
            Logger.Info(
                $"[Own creatures] '{place}' would go from '{at.Name}' to '{toState.Name}' of '{fsm.Name}' here, in a " +
                $"move of the player {move.Leader}'s, which goes on as the game that leads it tells"
            );
        }

        return true;
    }

    /// <summary>
    /// Tells the members in the room a step of a move that this game leads, in a game that follows the scene host's
    /// (see <see cref="LeadsMove"/>), which the scene host's game takes (see <see cref="TakeLedStep"/>), with the
    /// number that this game gave the move.
    /// </summary>
    private void TellLedStep(PlayMakerFSM machine, FsmState toState, ulong number) {
        var creature = machine.gameObject;
        SharedDice.RollByName(creature, OwnCreatures.RollName(creature));
        var word = OwnCreatureUpdate(
            machine,
            new OwnStep(
                machine.Fsm.ActiveStateName, SharedDice.RollCounts(creature), creature.transform.position,
                creature.transform.localScale.x
            )
        );
        word.PartCount = OwnLedStepPart;
        word.Names = [toState.Name];
        word.Sequence = number;
        Logger.Info(
            $"[Own creatures] '{word.ObjectPath}' goes from '{word.StateName}' to '{toState.Name}' of " +
            $"'{word.FsmName}' in move {number}, which this game leads, as the scene host's game is told"
        );
        SendToRoomMembers(word);
    }

    /// <summary>
    /// Has the creature of the scene host's game take a step of a move of another player's that the game of that
    /// player leads (see <see cref="TellLedStep"/>), into the state that it went to there, as the state machine runs.
    /// One of a move that the scene host's game has not met yet waits for it, and one of a move that it met and does
    /// not follow, as it did not take it up or as it is over, goes nowhere (see <see cref="MeetLedMove"/>).
    /// </summary>
    private void TakeLedStep(ClientPlayerData player, GameObject creature, CoopSaveUpdate update) {
        var place = update.ObjectPath;
        var into = update.Names[0];
        var number = update.Sequence;
        var told = $"'{place}' went from '{update.StateName}' to '{into}' of '{update.FsmName}' in move {number} " +
                   $"that {player.Username}'s game leads";
        if (IsSceneHost?.Invoke() != true) {
            Logger.Info($"[Own creatures] {told}, which comes here with the scene host's step");
            return;
        }

        var fsm = PlayMakerFSM.FindFsmOnGameObject(creature, update.FsmName).Fsm;
        if (_ledMoves.TryGetValue(fsm, out var move) && move.Leader == player.Id && move.Number == number) {
            Logger.Info($"[Own creatures] {told}, and goes from '{fsm.ActiveStateName}' to '{into}' here too");
            TakeLedStepHere(fsm, into);
            return;
        }

        if (_ledMovesMet.TryGetValue((fsm, player.Id), out var met) && met >= number) {
            Logger.Info($"[Own creatures] {told}, a move that does not go on here, in '{fsm.ActiveStateName}'");
            return;
        }

        if (!_earlyLedSteps.TryGetValue(fsm, out var early)) {
            early = [];
            _earlyLedSteps[fsm] = early;
        }

        early.Add((player.Id, number, into));
        Logger.Info($"[Own creatures] {told}, which comes before the move does here, and waits for it");
    }

    /// <summary>
    /// Has a state machine of the scene host's creature go into the state of a step of a move that another game leads,
    /// as it runs (see <see cref="TakeLedStep"/>).
    /// </summary>
    private void TakeLedStepHere(Fsm fsm, string into) {
        _takingLedStep = true;
        try {
            fsm.SetStateAsItRuns(into);
        } finally {
            _takingLedStep = false;
        }
    }

    /// <summary>
    /// Has the scene host's game note that a state machine of a creature that every game runs by itself met a move of
    /// another player's, which it follows from now on or not (see <see cref="FollowLedMove"/>): the steps of it that
    /// came before are taken now, in the order that they came, where it follows it, and let go of where it does not,
    /// and so are those of moves of that player's before it, whose steps never come to anything here.
    /// </summary>
    private void MeetLedMove(Fsm fsm, ushort leader, ulong number) {
        _ledMovesMet[(fsm, leader)] = number;
        if (!_earlyLedSteps.Remove(fsm, out var early)) {
            return;
        }

        var place = OwnCreatures.PlaceOf(fsm.GameObject);
        foreach (var step in early) {
            if (step.Leader != leader || step.Number > number) {
                if (!_earlyLedSteps.TryGetValue(fsm, out var still)) {
                    still = [];
                    _earlyLedSteps[fsm] = still;
                }

                still.Add(step);
                continue;
            }

            var told = $"'{place}' went to '{step.Into}' of '{fsm.Name}' in move {step.Number} of the player " +
                       $"{leader}'s";
            if (step.Number == number && _ledMoves.TryGetValue(fsm, out var move) && move.Leader == leader &&
                move.Number == number) {
                Logger.Info($"[Own creatures] {told} before the move came here, and goes there now");
                TakeLedStepHere(fsm, step.Into);
            } else {
                Logger.Info($"[Own creatures] {told} before the move came here, which does not go on here");
            }
        }
    }
}
