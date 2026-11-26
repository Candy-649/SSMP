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
/// follows the scene host's tells that game of it, whose creature is told the same where it is, as if its own part of
/// the room told it now, and which every other game takes as a step of the scene host's (USER 11-25 "就本地先演，本地的
/// 怪本来就是活的"). Before, a game that follows the scene host's held such a step for the scene host's, which never came
/// where only its own player had come into the part of the room: no part of a room feels the figure of a partner.
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
    /// </summary>
    private static bool ToldFromOutside(Fsm fsm, FsmState toState, GameObject creature) {
        return Fsm.EventData?.SentByFsm is { } teller && teller != fsm && fsm.LastTransition is { } way &&
               way.ToState == toState.Name &&
               (teller.GameObject is not { } tellerObject ||
                !OwnCreatures.AreKin(OwnCreatures.Owning(tellerObject), OwnCreatures.Owning(creature)) ||
                teller.FsmComponent is { } heard && OwnCreatures.RunsInEachGame(heard));
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
        SharedDice.RollByName(creature, OwnCreatures.RollName(creature));
        var word = OwnCreatureUpdate(
            machine,
            new OwnStep(
                fsm.ActiveStateName, SharedDice.RollCounts(creature), creature.transform.position,
                creature.transform.localScale.x
            )
        );
        word.PartCount = OwnTellPart;
        word.Key = ToldByEachGame() ? 1UL : 0UL;
        var teller = Fsm.EventData?.SentByFsm;
        word.Names = [fsm.LastTransition!.EventName, toState.Name];
        Logger.Info(
            $"[Own creatures] '{word.ObjectPath}' is told by '{teller?.Name}' of " +
            $"'{(teller?.GameObject != null ? teller.GameObject.name : "?")}' to go from '{word.StateName}' to " +
            $"'{toState.Name}' of '{word.FsmName}' by '{word.Names[0]}', and goes at once, as the scene host's game " +
            "is told"
        );
        SendToRoomMembers(word);
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
    /// itself told it to go on there (see <see cref="TellOwnTell"/>): in the scene host's game, the creature is told
    /// the same where it is, as if what told it there told it here now, and goes on as the game has it, which every
    /// other game takes as a step of the scene host's. Not where it took the same tell a moment before, but for one of
    /// a state machine that each game runs by itself, which is of that game's player.
    /// </summary>
    private void TakeOwnTell(ClientPlayerData player, GameObject creature, CoopSaveUpdate update) {
        var place = update.ObjectPath;
        var (by, into) = (update.Names[0], update.Names[1]);
        var told = $"'{place}' was told in {player.Username}'s game to go from '{update.StateName}' to '{into}' of " +
                   $"'{update.FsmName}' by '{by}'";
        if (IsSceneHost?.Invoke() != true) {
            Logger.Info($"[Own creatures] {told}, which comes here with the scene host's step");
            return;
        }

        var fsm = PlayMakerFSM.FindFsmOnGameObject(creature, update.FsmName).Fsm;
        if (OwnCreatures.RunsInEachGame(fsm.FsmComponent)) {
            Logger.Info($"[Own creatures] {told}, which each game runs by itself for its own player here");
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
            return;
        }

        Logger.Info(
            $"[Own creatures] {told}, and is told the same here, from '{fsm.ActiveStateName}' into '{way.ToState}'"
        );
        fsm.Event(way.FsmEvent);
    }
}
