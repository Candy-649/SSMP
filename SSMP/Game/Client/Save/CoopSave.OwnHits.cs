using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// What a creature that every game runs by itself does as it hurts a player, like the map seller in a spar: she stops
/// where her charge hurt someone, punches there and leaves, with none of the ring that she throws back after a charge
/// that hurt nobody. Only the game of the player hurt has the hurt, as in another game they are a figure that nothing
/// hurts. That game plays it at once, as a game of one does, and tells the scene host's game, which plays it too where
/// its own creature is and tells every other game, which plays it so as well: that the creature hurt a player is
/// shared, not where (USER 11-22 "可以同步撞到了的状态，但是不同步撞到哪了，因为本地先演有可能多个人被撞倒，而在本地几乎都
/// 是自己先被撞到", "只要收到撞到了，就在当前位置停下出拳，然后消失，等主机下一招"), as a catch is played whenever a player is
/// caught (USER 11-22 "这个机制其实在丝母的丝线抓人里用过差不多的"; see CoopSave.OwnCatches). Before, no other game heard
/// of it, and one whose player the scene host's creature charged past was put where that one punched, up to 12 units
/// off, to throw its ring back there (the user's log of 11-21, 21:30:21.510).
///
/// A creature that is still in the state that the hurt took theirs from, at the same rolls, takes the same way where it
/// is, and one still on its way there, a moment behind, as it gets there (see <see cref="TakeOwnHit"/>,
/// <see cref="TakeEarlyHit"/>). One that went on from there otherwise, as the word came a moment late, is given
/// the marks that the way sets for later, by which the map seller leaves without her ring, while it has yet to look at
/// them; one past that plays what it plays (USER 11-22 "仍然可能客机说撞到了，结果主机已经扔环了（，让主机扔吧"). In a game
/// that follows the scene host's, the rest of the move is that game's own, to its end, as a leap at its player is (see
/// <see cref="StartOwnHit"/>): the steps of the scene host's of it are let go of, as its creature may have gone on
/// otherwise there, and the creature goes on from the first one out of the move, the next move or what cut the move
/// short there. A throw that the scene host's creature plays as the word came too late is not followed in such a game,
/// which would take its creature off to where that one was, in sight (USER 11-22 "如果要用平滑移动那就算了").
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What an update of a creature that every game runs by itself is when a game that follows the scene host's tells
    /// that the creature hurt its player, with the way that that set the creature off on (see
    /// <see cref="TellOwnHit"/>): the scene host's game takes it, and tells every other game in its turn.
    /// </summary>
    private const ushort OwnHitPart = 12;

    /// <summary>
    /// What an update of a creature that every game runs by itself is when the scene host's game tells that the
    /// creature hurt a player, its own or another member's as their game told, with the way that that set it off on:
    /// every other game takes it (see <see cref="TakeOwnHit"/>).
    /// </summary>
    private const ushort OwnHostHitPart = 13;

    /// <summary>
    /// The creature that every game runs by itself of which a part hurts this game's player now, as the game tells the
    /// part of the hurt (see OnHurtSourceTold), or null.
    /// </summary>
    private static GameObject? _hurtingOwn;

    /// <summary>
    /// The state machine that takes the way of a hurt of a player of another game now, at the word of that game (see
    /// <see cref="TakeOwnHit"/>), or null.
    /// </summary>
    private Fsm? _takingHit;

    /// <summary>
    /// The words of hurts of players of other games that came to a game that follows the scene host's before its state
    /// machine got to the state that the hurt came from there, each to be played as it gets there (see
    /// <see cref="TakeEarlyHit"/>).
    /// </summary>
    private readonly Dictionary<Fsm, (ClientPlayerData Player, CoopSaveUpdate Word)> _earlyHits = new();

    /// <summary>
    /// Whether a state machine of a creature that every game runs by itself goes to a state on a way that a hurt of a
    /// player takes it: of this game's player, whom a part of the creature hurts now, or of another game's, whose hurt
    /// this game takes at the word of that game. Only the way that the hurt itself sends it on, from outside it, not
    /// the ways on that it then takes by itself at once.
    /// </summary>
    private bool IsHitWay(Fsm fsm, FsmState toState, GameObject creature) {
        return (_takingHit == fsm || _hurtingOwn != null && OwnCreatures.Owning(creature) == _hurtingOwn) &&
               fsm.LastTransition is { } way && way.ToState == toState.Name && Fsm.EventData?.SentByFsm != fsm;
    }

    /// <summary>
    /// Has a state machine of a creature that every game runs by itself, in a game that follows the scene host's, play
    /// the rest of a move by itself to its end, as a hurt of a player took it out of a state of the move on a way of
    /// its own, or as it was given the marks of that way (see <see cref="TakeOwnHit"/>): as it plays a leap at its
    /// player (see CoopSave.OwnCatches), and as a way of its own, among whose steps those of the scene host's are found
    /// by their states (see TakeOwnStep). The steps of the scene host's into the states of the move go, into the state
    /// of the hurt too, whose step may come after the hurt here (review: it ended the move, and the creature waited
    /// for the scene host's turn to throw its ring back, to be put where that one was), and so do those that it kept to
    /// get to by itself, as those of a blow of its player do (see OnOwnCreatureStep).
    /// </summary>
    private void StartOwnHit(Fsm fsm, FsmState from) {
        var move = MoveOf(fsm, from);
        var hit = new OwnLeap(OwnCreatures.PlaceOf(fsm.GameObject), false, "move after the hurt") {
            Catch = move,
            Hit = true
        };
        _ownLeaps[fsm] = hit;
        _ownWays.Add(fsm);
        _keptOwnSteps.Remove(fsm);
        Logger.Info(
            $"[Own creatures] '{hit.Place}' plays the rest of its move from '{from.Name}' of '{fsm.Name}' here by " +
            $"itself, to its end: '{string.Join("', '", move.Select(state => state.Name))}'"
        );
    }

    /// <summary>
    /// The states of the rest of a move of a state machine after a hurt of a player took it out of a state on a way of
    /// its own: the states of the move from that state (see CoopSave.MoveOf), which its way and
    /// its other ways go on into, but that state itself.
    /// </summary>
    private static HashSet<FsmState> HitMove(Fsm fsm, FsmState from) {
        var move = MoveOf(fsm, from);
        move.Remove(from);
        return move;
    }

    /// <summary>
    /// Tells the others that a creature that every game runs by itself hurt the player of this game, or, from the scene
    /// host's game, a player of another game as that game told: the state that the hurt took it from, with the rolls up
    /// to then, and the way, by its event and the state that it leads to (see <see cref="TakeOwnHit"/>).
    /// </summary>
    private void TellOwnHit(PlayMakerFSM machine, FsmState toState, ushort part) {
        var creature = machine.gameObject;
        var fsm = machine.Fsm;
        SharedDice.RollByName(creature, OwnCreatures.RollName(creature));
        var word = OwnCreatureUpdate(
            machine,
            new OwnStep(
                fsm.ActiveStateName, SharedDice.RollCounts(creature), creature.transform.position,
                creature.transform.localScale.x
            )
        );
        word.PartCount = part;
        word.Names = [fsm.LastTransition!.EventName, toState.Name];
        Logger.Info(
            $"[Own creatures] '{word.ObjectPath}' hurt " +
            (_takingHit == fsm ? "a player of another game" : "the local player") +
            $" and goes from '{word.StateName}' to '{toState.Name}' of '{word.FsmName}' by '{word.Names[0]}', as " +
            (part == OwnHitPart ? "the scene host's game is told" : "the members in the room are told")
        );
        SendToRoomMembers(word);
    }

    /// <summary>
    /// Takes the word of another game that a creature that every game runs by itself hurt a player there (see
    /// <see cref="TellOwnHit"/>): in the scene host's game the word of a game that follows it, which it tells every
    /// other game in its turn as it takes the way, and in every other game the scene host's word (USER 11-22 "只要收到撞到
    /// 了，就在当前位置停下出拳"). A creature that is in the state that the hurt took theirs from, at the same rolls, takes
    /// the same way where it is, and one still on its way there, a moment behind, as it gets there. One that went on
    /// from there otherwise is given the marks that the way sets for later, while it has yet to look at them, and one
    /// past that, or in another move, plays what it plays. One that plays the rest of a move after a hurt here, of its
    /// own player or at an earlier word, goes on with that.
    /// </summary>
    private void TakeOwnHit(ClientPlayerData player, GameObject creature, CoopSaveUpdate update) {
        var place = update.ObjectPath;
        var sceneHost = IsSceneHost?.Invoke() == true;
        if (!sceneHost && update.PartCount == OwnHitPart) {
            Logger.Info(
                $"[Own creatures] '{place}' hurt {player.Username} in their game, which comes here with the scene " +
                "host's word of it"
            );
            return;
        }

        var fsm = PlayMakerFSM.FindFsmOnGameObject(creature, update.FsmName).Fsm;
        var (by, into) = (update.Names[0], update.Names[1]);
        var told = $"'{place}' hurt a player in {player.Username}'s game, from '{update.StateName}' into '{into}'";

        // The scene host's game plays no move after a hurt by itself, and one left from before it was the scene host's
        // keeps it from no word (review)
        if (!sceneHost && _ownLeaps.TryGetValue(fsm, out var leap) && leap is { Hit: true, Through: false }) {
            Logger.Info($"[Own creatures] {told}, and plays the rest of a move after a hurt here already");
            return;
        }

        var from = fsm.GetState(update.StateName);
        SharedDice.RollByName(creature, OwnCreatures.RollName(creature));
        var atRolls = SharedDice.RollCounts(creature).SequenceEqual(update.Amounts);
        var way = fsm.ActiveState.Transitions.FirstOrDefault(each => each.EventName == by && each.ToState == into);
        if (fsm.ActiveState == from && way != null && atRolls) {
            var away = Vector2.Distance(creature.transform.position, new Vector2(update.Values[0], update.Values[1]));
            Logger.Info($"[Own creatures] {told}, and goes there by '{by}' here too, where it is, {away:F1} away");

            // The move is this game's own before the way is taken: where the word is played as the state machine gets
            // to the state, it takes the way only after this, as a step of its own (see TakeEarlyHit)
            if (!sceneHost) {
                StartOwnHit(fsm, from);
            }

            _takingHit = fsm;
            try {
                fsm.Event(way.FsmEvent);
            } finally {
                _takingHit = null;
            }

            return;
        }

        // One still on its way into that state, a moment behind, plays it as it gets there (see TakeEarlyHit)
        if (!sceneHost && fsm.ActiveState != from && GoesOnInto(fsm.ActiveState, state => state == from)) {
            _earlyHits[fsm] = (player, update);
            Logger.Info(
                $"[Own creatures] {told}, and is in '{fsm.ActiveStateName}' of '{fsm.Name}' here on its way there, " +
                "where it plays the hurt"
            );
            return;
        }

        var move = HitMove(fsm, from);
        var marks = HitMarks(fsm, fsm.GetState(into));
        if (move.Contains(fsm.ActiveState) && LeftAtRollsOf(fsm, move, update) &&
            MarksReadLater(fsm.ActiveState, move, marks)) {
            foreach (var mark in marks) {
                mark.boolVariable.Value = mark.boolValue.Value;
            }

            Logger.Info(
                $"[Own creatures] {told}, and went on otherwise here, to '{fsm.ActiveStateName}' of '{fsm.Name}', " +
                "and is given what that way marks: " +
                string.Join(", ", marks.Select(mark => $"'{mark.boolVariable.Name}' {mark.boolValue.Value}"))
            );
            if (!sceneHost) {
                StartOwnHit(fsm, from);
            }

            return;
        }

        Logger.Info(
            $"[Own creatures] {told}, and is in '{fsm.ActiveStateName}' of '{fsm.Name}' here" +
            (fsm.ActiveState == from && !atRolls ? " at other rolls" : ", past what that way marks") +
            ", where it plays what it plays"
        );
    }

    /// <summary>
    /// Whether a state machine here took a step into the rest of a move after a state at the rolls that the word of a
    /// hurt of a player from that state came with (see <see cref="TakeOwnHit"/>): it went on from that state otherwise
    /// in the move that the hurt came in, and not in a later one: among its steps since it last came into the move,
    /// and not those of an earlier time through it (review).
    /// </summary>
    private bool LeftAtRollsOf(Fsm fsm, HashSet<FsmState> move, CoopSaveUpdate word) {
        if (!_ownSteps.TryGetValue(fsm, out var steps)) {
            return false;
        }

        var outside = steps.FindLastIndex(step => !move.Contains(fsm.GetState(step.State)));
        return steps.Skip(outside + 1).Any(step => step.Counts.SequenceEqual(word.Amounts));
    }

    /// <summary>
    /// Plays in a game that follows the scene host's the hurt of a player of another game whose word came before its
    /// state machine got to the state that the hurt came from there, as it gets there, and as the word finds it where
    /// it gets first otherwise (see <see cref="TakeOwnHit"/>). Before, one still in the wind-up of the map seller's
    /// charge as the word came that the scene host's player was hurt by it charged all the way by itself, and was then
    /// put back where the scene host's punched (review; a game is as far behind after a stall of the network: the
    /// scene host's steps up to her turn to throw her ring back came to the user's game within 10 ms while its creature
    /// was still in the teleport before the wind-up, at 21:41:31.090 of 11-21).
    /// </summary>
    private void TakeEarlyHit(Fsm fsm, FsmState toState) {
        if (_earlyHits.Count == 0 || !_earlyHits.TryGetValue(fsm, out var early) || fsm.ActiveState != toState) {
            return;
        }

        var from = early.Word.StateName;
        if (toState.Name != from && GoesOnInto(toState, state => state.Name == from)) {
            return;
        }

        _earlyHits.Remove(fsm);
        TakeOwnHit(early.Player, fsm.GameObject, early.Word);
    }

    /// <summary>
    /// What the way of a hurt of a player marks a state machine with for later, which one that went on otherwise from
    /// the state of the hurt is given (see <see cref="TakeOwnHit"/>): the switches that the states that only the way
    /// leads into set, like the map seller's mark that she hit someone, by which she leaves without the ring that she
    /// throws back otherwise.
    /// </summary>
    private static List<SetBoolValue> HitMarks(Fsm fsm, FsmState into) {
        var marks = new List<SetBoolValue>();
        foreach (var state in MoveOf(fsm, into)) {
            marks.AddRange(state.Actions.OfType<SetBoolValue>().Where(mark => mark.Enabled));
        }

        return marks;
    }

    /// <summary>
    /// Whether a state machine in a state of the rest of a move still comes, as each state finishes, to a state of it
    /// that looks at one of the marks of the way of a hurt (see <see cref="HitMarks"/>), like the map seller's turn to
    /// throw her ring back, which looks at whether she hit someone: one in that state looked at them already.
    /// </summary>
    private static bool MarksReadLater(FsmState at, HashSet<FsmState> move, List<SetBoolValue> marks) {
        var names = marks.Select(mark => mark.boolVariable.Name).ToHashSet();
        return GoesOnInto(
            at,
            state => move.Contains(state) && state.Actions.Any(
                action => action.Enabled && action is not SetBoolValue &&
                          Entity.Action.EntityFsmActions.ValueVariablesOf(action).Any(names.Contains)
            )
        );
    }
}
