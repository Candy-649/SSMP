using System.Collections.Generic;
using System.Linq;
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
/// the chewing that goes on until their health is low, and a move that a state machine that each game runs by itself
/// for its own player sets off and that goes by what that player has, like the swallow of a bomb that they threw,
/// which goes by whether they have the pouch of poison on (see CoopSave.OwnTells). No other game can play such a move
/// by itself.
///
/// The game of that player plays it by itself, as the game does, and in a game that follows the scene host's, that is
/// a move of its own to its end, whose every step, the one out of it too, it tells the scene host's game, with the
/// number that it gave the move and the number of the step in it (see <see cref="TellLedStep"/>). The scene host's
/// game, playing that player's move, takes these steps in their order and no step of its own creature's (see
/// <see cref="TakeLedStep"/>), and every other game takes the steps of the scene host's as they come: nothing of a
/// game's own takes the creature on in a move that another game leads, like a hurt of its own player, a look at its
/// own player's health or the end of a wait, until the word of the game that leads it does, or its death (see
/// <see cref="HoldsInLedMove"/>; USER 11-25 "套现有规则": the caught player's game leads). The scene host's game takes
/// up one move at a time: one of another player's that comes while the creature is in a move already goes nowhere, and
/// its steps neither (see <see cref="MeetLedMove"/>). A move whose player left the room ends in every game, and so
/// does one that a game followed as it followed the scene host's, once it is the scene host (see
/// <see cref="EndLedMove"/>). With three or four players, each move is the game of its own player's, and every other
/// game shows it.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What an update of a creature that every game runs by itself is when it tells a step of a move that the game of
    /// the player it acts on leads (see <see cref="TellLedStep"/>): the state that it went from, the one that it went
    /// to, the number of the move in <see cref="CoopSaveUpdate.Sequence"/> and the number of the step in the move, from
    /// 1, in <see cref="CoopSaveUpdate.Key"/>.
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
    private readonly Dictionary<Fsm, List<(ushort Leader, ulong Number, ulong Step, string Into)>> _earlyLedSteps =
        new();

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
    /// <see cref="TellLedStep"/>): the number of their blow that set it off, for an extraction, and of the grab, for a
    /// grab (see CoopSave.OwnCatches).</param>
    /// <param name="byHost">Whether this game took it up as the scene host's, which takes its steps from the game that
    /// leads it (see <see cref="TakeLedStep"/>), rather than as a game that follows the scene host's, which takes the
    /// scene host's.</param>
    private sealed class LedMove(
        HashSet<FsmState> states,
        FsmGameObject? hero,
        ushort leader,
        ulong number,
        bool byHost
    ) {
        public readonly HashSet<FsmState> States = states;
        public readonly FsmGameObject? Hero = hero;
        public readonly ushort Leader = leader;
        public readonly ulong Number = number;
        public readonly bool ByHost = byHost;

        /// <summary>
        /// For the scene host's game, the number of the step of the move that it takes next (see
        /// <see cref="TakeLedSteps"/>).
        /// </summary>
        public ulong Next = 1;

        /// <summary>
        /// For the scene host's game, the steps of the move that came before one that comes before them, by their
        /// number: the state that each went to.
        /// </summary>
        public readonly Dictionary<ulong, string> Pending = new();

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
        _ledMoves[fsm] = new LedMove(states, hero, leader, number, IsSceneHost?.Invoke() == true);
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
    /// a switch of its own: only a step that the scene host's game takes from the game that leads (see
    /// <see cref="TakeLedStep"/>), a step of the scene host's in every other game, or the death of the creature takes
    /// it on in the move or out of it, its own here or another game's that it is told, as the game tells its state
    /// machines of it (HealthManager.Die): a death by a blow of the scene host's player as another player extracted
    /// from it would go nowhere there. A game that follows the scene host's went on through the move by itself where
    /// its states took their way by what they do themselves, which ran it ahead of the scene host's, as into the air
    /// with a player whose grab the game that leads it had broken off, and held its way out of the move until the
    /// scene host's step out of it, kept as one that it would get to by itself, was taken a second later (review of
    /// 4aae450): a step of the scene host's in the move is taken as it comes (see OnOwnCreature). Not by a way that
    /// something of this game takes it, like a blow of its own player that extracts from it, which would have this game
    /// lead a move of its own in it. One out of the move ends it here, and the hero of the state machine is this game's
    /// player again.
    /// </summary>
    private bool HoldsInLedMove(Fsm fsm, FsmState toState) {
        if (!_ledMoves.TryGetValue(fsm, out var move) || fsm.ActiveState is not { } at || !move.States.Contains(at)) {
            return false;
        }

        var creature = OwnCreatures.Owning(fsm.GameObject) ?? fsm.GameObject;
        var place = OwnCreatures.PlaceOf(fsm.GameObject);
        var dies = creature.TryGetComponent<HealthManager>(out var health) && (health.isDead || health.hp <= 0);
        if (OwnCreatures.PlaysSceneHostsStep(fsm) || _takingLedStep || dies) {
            if (!move.States.Contains(toState)) {
                StopFollowing(fsm, move);
                var why = _takingLedStep ? "as the game that leads it tells"
                    : dies ? "as it dies"
                    : "as the scene host's game tells";
                Logger.Info(
                    $"[Own creatures] '{place}' goes out of move {move.Number} of the player {move.Leader}'s from " +
                    $"'{at.Name}' to '{toState.Name}' of '{fsm.Name}', {why}"
                );
            }

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
    /// Has a state machine no longer follow a move of another player's (see <see cref="FollowLedMove"/>): its hero is
    /// this game's player again.
    /// </summary>
    private void StopFollowing(Fsm fsm, LedMove move) {
        _ledMoves.Remove(fsm);
        if (move.Hero != null && HeroController.SilentInstance is { } hero) {
            move.Hero.Value = hero.gameObject;
        }
    }

    /// <summary>
    /// Tells the members in the room a step of a move that this game leads, in a game that follows the scene host's
    /// (see <see cref="LeadsMove"/>), which the scene host's game takes (see <see cref="TakeLedStep"/>), with the
    /// number that this game gave the move and the number of the step in it.
    /// </summary>
    private void TellLedStep(PlayMakerFSM machine, FsmState toState, ulong number, ulong step) {
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
        word.Key = step;
        Logger.Info(
            $"[Own creatures] '{word.ObjectPath}' goes from '{word.StateName}' to '{toState.Name}' of " +
            $"'{word.FsmName}', step {step} of move {number}, which this game leads, as the scene host's game is told"
        );
        SendToRoomMembers(word);
    }

    /// <summary>
    /// Has the creature of the scene host's game take a step of a move of another player's that the game of that
    /// player leads (see <see cref="TellLedStep"/>), into the state that it went to there, as the state machine runs,
    /// after every step of the move before it (see <see cref="TakeLedSteps"/>): one held up on the network comes after
    /// those after it, which wait for it (review of 4aae450: the muck roach, set past the state where it lets go of
    /// the player it threw, held on to them in its state machine, and its death later hurt this game's player). One of
    /// a move that the scene host's game has not met yet waits for it, and one of a move that it met and does not
    /// follow, as it did not take it up or as it is over, goes nowhere (see <see cref="MeetLedMove"/>).
    /// </summary>
    private void TakeLedStep(ClientPlayerData player, GameObject creature, CoopSaveUpdate update) {
        var place = update.ObjectPath;
        var into = update.Names[0];
        var (number, step) = (update.Sequence, update.Key);
        var told = $"'{place}' went from '{update.StateName}' to '{into}' of '{update.FsmName}', step {step} of " +
                   $"move {number} that {player.Username}'s game leads";
        if (IsSceneHost?.Invoke() != true) {
            Logger.Info($"[Own creatures] {told}, which comes here with the scene host's step");
            return;
        }

        var fsm = PlayMakerFSM.FindFsmOnGameObject(creature, update.FsmName).Fsm;
        if (_ledMoves.TryGetValue(fsm, out var move) && move.Leader == player.Id && move.Number == number) {
            move.Pending[step] = into;
            if (step != move.Next) {
                Logger.Info($"[Own creatures] {told}, which waits here for step {move.Next} before it");
                return;
            }

            TakeLedSteps(fsm, move);
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

        // A step of a move that a scene host before numbered, which this game never followed, is no step of a look
        // of this game's own, which goes after it (see CoopSave.OwnSilkLooks)
        NoteSilkLook(number);
        early.Add((player.Id, number, step, into));
        Logger.Info($"[Own creatures] {told}, which comes before the move does here, and waits for it");
    }

    /// <summary>
    /// Has a state machine of the scene host's creature take the steps of a move that another game leads that came,
    /// one after another from the next one on, up to one that has not come yet or to the end of the move (see
    /// <see cref="TakeLedStep"/>).
    /// </summary>
    private void TakeLedSteps(Fsm fsm, LedMove move) {
        var place = OwnCreatures.PlaceOf(fsm.GameObject);
        while (move.Pending.Remove(move.Next, out var into)) {
            Logger.Info(
                $"[Own creatures] '{place}' goes from '{fsm.ActiveStateName}' to '{into}' of '{fsm.Name}' here, " +
                $"step {move.Next} of move {move.Number} that the player {move.Leader}'s game leads"
            );
            move.Next++;
            TakeLedStepHere(fsm, into);
            if (!_ledMoves.TryGetValue(fsm, out var still) || still != move) {
                return;
            }
        }
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
    /// came before are taken now, in their order, where it follows it, and let go of where it does not, and so are
    /// those of moves of that player's before it, whose steps never come to anything here.
    /// </summary>
    private void MeetLedMove(Fsm fsm, ushort leader, ulong number) {
        _ledMovesMet[(fsm, leader)] = number;
        if (!_earlyLedSteps.Remove(fsm, out var early)) {
            return;
        }

        var place = OwnCreatures.PlaceOf(fsm.GameObject);
        var follows = _ledMoves.TryGetValue(fsm, out var move) && move.Leader == leader && move.Number == number;
        foreach (var step in early) {
            if (step.Leader != leader || step.Number > number) {
                if (!_earlyLedSteps.TryGetValue(fsm, out var still)) {
                    still = [];
                    _earlyLedSteps[fsm] = still;
                }

                still.Add(step);
                continue;
            }

            var told = $"'{place}' went to '{step.Into}' of '{fsm.Name}', step {step.Step} of move {step.Number} of " +
                       $"the player {leader}'s, before the move came here";
            if (follows && step.Number == number) {
                Logger.Info($"[Own creatures] {told}, and goes there in its turn");
                move!.Pending[step.Step] = step.Into;
            } else {
                Logger.Info($"[Own creatures] {told}, which does not go on here");
            }
        }

        if (follows) {
            TakeLedSteps(fsm, move!);
        }
    }

    /// <summary>
    /// Ends a move of another player's that a state machine of a creature that every game runs by itself follows here,
    /// whose steps no longer come to this game: as their player left the room, or as this game, which followed it as
    /// one that follows the scene host's, is the scene host now (review of 4aae450: the scene host's creature, held at
    /// every switch of its own in the move, waited for good). In the scene host's game, which every other game takes
    /// the steps of, a creature in an extraction is let go of as the game lets go of one when the hero who extracts is
    /// hurt, or else extracted (see CoopSave.ExtractEndOf), and any other move it goes on with by itself, like the
    /// flight and the throw of a grab.
    /// </summary>
    private void EndLedMove(Fsm fsm, LedMove move, string why) {
        StopFollowing(fsm, move);
        var at = fsm.ActiveState;
        var end = IsSceneHost?.Invoke() == true && at != null && move.States.Contains(at) &&
                  ExtractStates(fsm) is { } states && states.Contains(at)
            ? ExtractEndOf(states)
            : null;
        Logger.Info(
            $"[Own creatures] '{OwnCreatures.PlaceOf(fsm.GameObject)}' goes out of move {move.Number} of the player " +
            $"{move.Leader}'s in '{fsm.ActiveStateName}' of '{fsm.Name}', {why}" +
            (end != null ? $", into '{end}', an end of the extraction" : "")
        );
        if (end != null) {
            fsm.SetStateAsItRuns(end);
        }
    }

    /// <summary>
    /// Ends each move of another player's that a state machine here followed as one that follows the scene host's, once
    /// this game is the scene host (see <see cref="EndLedMove"/>): it took the scene host's steps of it, and none of
    /// the game that leads it, whose steps of it come to the new scene host as to one that has not met it.
    /// </summary>
    private void UpdateLedMoves() {
        if (_ledMoves.Count == 0 || IsSceneHost?.Invoke() != true) {
            return;
        }

        foreach (var pair in _ledMoves.ToList()) {
            if (!pair.Value.ByHost) {
                EndLedMove(pair.Key, pair.Value, "as this game is the scene host now");
            }
        }
    }

    /// <summary>
    /// Ends the moves that a player's game leads that state machines here follow, as they left the room or the game
    /// (see <see cref="EndLedMove"/>), and forgets the moves of theirs met here and the steps that wait for one: their
    /// game, started again, numbers its moves anew, whose steps would go nowhere here.
    /// </summary>
    private void ForgetLedMovesOf(ushort id) {
        foreach (var pair in _ledMoves.ToList()) {
            if (pair.Value.Leader == id) {
                EndLedMove(pair.Key, pair.Value, "as their player left the room");
            }
        }

        foreach (var met in _ledMovesMet.Keys.Where(key => key.Leader == id).ToList()) {
            _ledMovesMet.Remove(met);
        }

        foreach (var pair in _earlyLedSteps.ToList()) {
            pair.Value.RemoveAll(step => step.Leader == id);
            if (pair.Value.Count == 0) {
                _earlyLedSteps.Remove(pair.Key);
            }
        }
    }
}
