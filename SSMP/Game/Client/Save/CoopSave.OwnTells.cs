using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// What a creature that every game runs by itself does as something outside it tells it to: a part of the room that the
/// player of this game came into, like the ground under the bats that wakes them, the player's own harpoon, another
/// creature or an alarm of the room. The game that is told plays it at once, as a game of one does, and a game that
/// follows the scene host's tells that game of it, whose creature is put where it was in the game that was told, with
/// what told it set on it there, and is told the same, as if its own part of the room told it now, and which every
/// other game takes as a step of the scene host's (USER 11-25 "就本地先演，本地的怪本来就是活的"). Before, a game that
/// follows the scene host's held such a step for the scene host's, which never came where only its own player had come
/// into the part of the room: no part of a room feels the figure of a partner. What tells it may have set it somewhere
/// and set the way it goes first: a part of a room that a player of the ward sets off by taking what lies there puts
/// the husk that lies in wait at the hole it leaps out of, and sets the way of its leap, before it tells it to leap,
/// which only the game of that player has.
///
/// What tells it in every game, like another creature that every game runs by itself, comes to the scene host's game
/// both by itself and at the word of each other game, and is played there once: whichever comes first (see
/// <see cref="TellTakenAlready"/>). Not what a state machine of its own that each game runs by itself tells it, like
/// the one that sees the harpoon of that game's player coming, which each game tells it of its own player: one of two
/// players a moment after another's went nowhere (review of 4aae450; see <see cref="ToldByEachGame"/>). What its own
/// state machines and parts tell one another stays as before, the scene host's step: what one of them tells another
/// there may go by where the players are.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What an update of a creature that every game runs by itself is when a game that follows the scene host's tells
    /// that game that something outside the creature told a state machine of it to go on there, with the event (see
    /// <see cref="TellOwnTell"/>): the scene host's game takes it (see <see cref="TakeOwnTell"/>).
    /// <see cref="CoopSaveUpdate.Key"/> is 1 for a tell of a state machine that each game runs by itself (see
    /// <see cref="ToldByEachGame"/>), and 0 for any other.
    /// </summary>
    private const ushort OwnTellPart = 16;

    /// <summary>
    /// How long, in seconds, the scene host's game takes the same tell of a state machine as played already, by itself
    /// or at the word of a game that follows it (see <see cref="TellTakenAlready"/>).
    /// </summary>
    private const float OwnTellWindow = 2f;

    /// <summary>
    /// The latest tell from outside a creature that every game runs by itself that each of its state machines took in
    /// the scene host's game: the event, when, by Time.unscaledTime, and whether at the word of another game.
    /// </summary>
    private readonly Dictionary<Fsm, (string Event, float At, bool Worded)> _tellsTaken = new();

    /// <summary>
    /// Whether a state machine of a creature that every game runs by itself goes to a state as something outside the
    /// creature tells it: another state machine than its own and those of its parts, whose event takes it there, or
    /// one of them that each game runs by itself for its own player (see OwnCreatures.RunsInEachGame), which hears
    /// what only this game's player does, like the throw of their harpoon. Not the creature that made it, or one that
    /// it made, its summon (see OwnCreatures.AreKin), whose orders go by what each game has under it: a conductor
    /// picks the fly that it sends at the player among those under it, whose order each game has otherwise as they
    /// leave and come back, and an order taken at once in every game would send another fly in each.
    ///
    /// Nor a machine of the room that runs by the scene host's word, like the controller of a fight (see
    /// CoopSave.PacedMachines): the scene host's tells the scene host's creature, whose step every other game takes. A
    /// game that follows has the start of a fight by itself, as its player walks in, and its controller told the great
    /// centipede to strike at the end of it, which went at once, and went to the scene host's game, whose centipede was
    /// in the fight long since and was sent into a strike out of turn (review of e499a84).
    /// </summary>
    private static bool ToldFromOutside(Fsm fsm, FsmState toState, GameObject creature) {
        return Fsm.EventData?.SentByFsm is { } teller && teller != fsm && fsm.LastTransition is { } way &&
               way.ToState == toState.Name &&
               (teller.GameObject is not { } tellerObject ||
                FindMachineKind(tellerObject, teller.Name) == null &&
                (!OwnCreatures.AreKin(OwnCreatures.Owning(tellerObject), OwnCreatures.Owning(creature)) ||
                 teller.FsmComponent is { } heard && OwnCreatures.RunsInEachGame(heard)));
    }

    /// <summary>
    /// Whether what tells a state machine to go on now is a state machine that each game runs by itself for its own
    /// player (see OwnCreatures.RunsInEachGame), whose tell is of that player and comes from no other game.
    /// </summary>
    private static bool ToldByEachGame() {
        return Fsm.EventData?.SentByFsm?.FsmComponent is { } teller && OwnCreatures.RunsInEachGame(teller);
    }

    /// <summary>
    /// Tells the scene host's game that something outside a creature that every game runs by itself told a state
    /// machine of it here to go on, from the state it was in, by the event (see <see cref="TakeOwnTell"/>).
    /// </summary>
    private void TellOwnTell(PlayMakerFSM machine, FsmState toState) {
        var creature = machine.gameObject;
        var fsm = machine.Fsm;
        var teller = Fsm.EventData?.SentByFsm;
        SharedDice.RollByName(creature, OwnCreatures.RollName(creature));
        var word = OwnCreatureUpdate(
            machine,
            new OwnStep(
                fsm.ActiveStateName, SharedDice.RollCounts(creature), creature.transform.position,
                creature.transform.localScale.x
            ),
            teller != null ? ToldValues.SetBy(teller, creature, machine) : null
        );
        word.PartCount = OwnTellPart;
        word.Key = ToldByEachGame() ? 1UL : 0UL;
        word.Names = [fsm.LastTransition!.EventName, toState.Name];
        Logger.Info(
            $"[Own creatures] '{word.ObjectPath}' is told by '{teller?.Name}' of " +
            $"'{(teller?.GameObject != null ? teller.GameObject.name : "?")}' to go from '{word.StateName}' to " +
            $"'{toState.Name}' of '{word.FsmName}' by '{word.Names[0]}', and goes at once, as the scene host's game " +
            "is told"
        );
        if (word.Key == 1) {
            // The move of this game's player's thing, which this game leads where it goes by what they have, and
            // whose checks of what they have on find in every game what they find here (see CoopSave.OwnToolChecks)
            word.Sequence = LeadsMoveByWhatPlayerHas(machine, toState);
            word.Names.AddRange(ToolChecksOf(fsm, toState));
        }

        SendToRoomMembers(word);
    }

    /// <summary>
    /// Has a state machine of a creature that every game runs by itself, in a game that follows the scene host's, lead
    /// the move that a state machine that each game runs by itself for its own player told it into, where the move
    /// goes by what that player has (see ChoosesByWhatPlayerHas), telling every step of it (see CoopSave.OwnLedMoves):
    /// the shaman of the swamp swallows a bomb into the burst of poison where the player who threw it has the pouch
    /// on, and the scene host's game, told of the swallow, looked at whether its own player had it (USER 11-26 "肯定得
    /// 改吧！"). The number of the move, which the word of the tell carries, or 0 for a move that goes by nothing of
    /// the player's, whose way on stays the scene host's.
    /// </summary>
    private ulong LeadsMoveByWhatPlayerHas(PlayMakerFSM machine, FsmState toState) {
        var fsm = machine.Fsm;
        if (_ownLeaps.ContainsKey(fsm)) {
            return 0;
        }

        var states = MoveOf(fsm, toState);
        if (!states.Any(ChoosesByWhatPlayerHas)) {
            return 0;
        }

        var leap = new OwnLeap(OwnCreatures.PlaceOf(machine.gameObject), false, "move by what the local player has") {
            Catch = states,
            Led = true,
            Move = ++_grabsLed
        };
        _ownLeaps[fsm] = leap;
        Logger.Info(
            $"[Own creatures] '{leap.Place}' goes into '{toState.Name}' of '{fsm.Name}', a move that goes by what " +
            $"the local player has, which it plays here by itself to its end, leading it as move {leap.Move}"
        );
        return leap.Move;
    }

    /// <summary>
    /// Whether the scene host's game took a tell from outside a creature that every game runs by itself already, of the
    /// same event to the same state machine a moment before: where it comes at the word of a game that follows it, by
    /// itself or at the word of another game, and where it comes by itself, at such a word. A tell that every game has
    /// comes both ways, and from each game. A tell taken now is noted.
    /// </summary>
    private bool TellTakenAlready(Fsm fsm, string by, bool worded) {
        var now = Time.unscaledTime;
        if (_tellsTaken.TryGetValue(fsm, out var taken) && taken.Event == by && (worded || taken.Worded) &&
            now - taken.At < OwnTellWindow) {
            return true;
        }

        _tellsTaken[fsm] = (by, now, worded);
        return false;
    }

    /// <summary>
    /// Takes the word of a game that follows the scene host's that something outside a creature that every game runs by
    /// itself told it to go on there (see <see cref="TellOwnTell"/>): in the scene host's game, the creature is put
    /// where it was there as it was told, turned and going as it was, with what told it set on it there (see
    /// PutAtSceneHostsStep), and is told the same, as if what told it there told it here now, and goes on as the game
    /// has it, which every other game takes as a step of the scene host's. Not where it took the same tell a moment
    /// before, but for one of a state machine that each game runs by itself, which is of that game's player, nor where
    /// it holds a player or plays a move that another player's game leads, which goes on to its end.
    /// </summary>
    private void TakeOwnTell(ClientPlayerData player, GameObject creature, CoopSaveUpdate update) {
        var place = update.ObjectPath;
        var (by, into) = (update.Names[0], update.Names[1]);
        var told = $"'{place}' was told in {player.Username}'s game to go from '{update.StateName}' to '{into}' of " +
                   $"'{update.FsmName}' by '{by}'";

        // What the checks of what that player has on in the move find, which they find in every game through it (see
        // CoopSave.OwnToolChecks), where it is the scene host's step that takes it there too
        NoteToolChecks(creature, update, into, 2);
        if (IsSceneHost?.Invoke() != true) {
            Logger.Info($"[Own creatures] {told}, which comes here with the scene host's step");
            return;
        }

        var fsm = PlayMakerFSM.FindFsmOnGameObject(creature, update.FsmName).Fsm;
        if (OwnCreatures.RunsInEachGame(fsm.FsmComponent)) {
            Logger.Info($"[Own creatures] {told}, which each game runs by itself for its own player here");
            return;
        }

        // The move that it goes into, which goes by what that player has, their game leads (see
        // LeadsMoveByWhatPlayerHas)
        var move = update.Key == 1 ? update.Sequence : 0UL;

        // One that comes as the creature here holds a player, or plays a move that another player's game leads, goes
        // nowhere, as a catch over a catch that it plays already does (see TakeOwnCatch): the move goes on here to its
        // end. The shaman of the swamp, holding the scene host's player, would have gone to swallow the bomb of another
        // player, whose game had it swallow that as it still sucked there, and never come to spit the player out; and
        // a move of a third player's, set off there as it was in the other's here, would have taken the place of that
        // one (review of d9dee93)
        if (_ledMoves.ContainsKey(fsm) || fsm.ActiveState is { } at && CatchStateOf(fsm, at) != null) {
            Logger.Info($"[Own creatures] {told}, and is in '{fsm.ActiveStateName}' here, in a move that goes on");
            if (move > 0) {
                MeetLedMove(fsm, player.Id, move);
            }

            return;
        }

        if (update.Key == 0 && TellTakenAlready(fsm, by, true)) {
            Logger.Info($"[Own creatures] {told}, which it was told here too a moment before");
            return;
        }

        var way = fsm.ActiveState?.Transitions.FirstOrDefault(each => each.EventName == by) ??
                  fsm.GlobalTransitions.FirstOrDefault(each => each.EventName == by);
        if (way == null) {
            Logger.Info($"[Own creatures] {told}, and is in '{fsm.ActiveStateName}' here, which that takes nowhere");
            if (move > 0) {
                MeetLedMove(fsm, player.Id, move);
            }

            return;
        }

        var away = Vector2.Distance(creature.transform.position, new Vector2(update.Values[0], update.Values[1]));
        Logger.Info(
            $"[Own creatures] {told}, and is told the same here, from '{fsm.ActiveStateName}' into '{way.ToState}', " +
            $"where it was there, {away:F1} away" +
            (move > 0 ? $", move {move}, which goes on as their game leads" : "")
        );
        PutAtSceneHostsStep(fsm, update);
        if (move > 0) {
            FollowLedMove(
                fsm, MoveOf(fsm, way.ToFsmState ?? fsm.GetState(way.ToState)), null,
                player.Id, move
            );
        }

        fsm.Event(way.FsmEvent);
        if (move > 0) {
            MeetLedMove(fsm, player.Id, move);
        }
    }
}
