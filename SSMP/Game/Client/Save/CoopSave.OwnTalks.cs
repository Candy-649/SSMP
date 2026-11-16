using HutongGames.PlayMaker;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The talks of the creatures that every game runs by itself (see OwnCreatures), like the one that a knight holds with
/// the player who wakes it before its fight. Each player reads the talk in their own game, at their own pace, and the
/// creature goes on from the steps of the scene host's once it is over (USER 11-16 "把这个boss本地化了吧",
/// "改成本地状态机就不一定要烦恼这些了").
///
/// A talk that the player of a game that follows the scene host's starts takes the creature there on at once, as a
/// talk does in a game of one, and the scene host's game is told, whose creature goes on by the same way if it is
/// still where that one was (see <see cref="TellOwnTalk"/>, <see cref="TakeOwnTalk"/>): every other game takes it from
/// there as a step of the scene host's, and the game puts its player into the talk as it does for a talk that a
/// character starts by itself. A creature that goes into a talk, by a step of its own or of the scene host's, plays it
/// here to its end, each line as this game's player reads it, while the steps of the scene host's that come meanwhile
/// wait, as for a leap at this game's player (see CoopSave.OwnCatches). It is over as it leaves the state that ended
/// its dialogue with no more of the talk to come, as a talk with a copy is (see Entity.EndsTalk): it waits there for
/// the scene host's to come as far, or goes on from where that one is already (see <see cref="WaitsAfterOwnTalk"/>).
/// The copy of the knight that the room had before played the talk of the player who woke it in their game, and the
/// talk that the scene host's game started a moment later took the box from it (USER 11-16 "为啥我这里没有对话框").
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What an update of a creature that every game runs by itself is when it tells that the player of the game that
    /// sent it started a talk with it (see <see cref="TellOwnTalk"/>).
    /// </summary>
    private const ushort OwnTalkPart = 5;

    /// <summary>
    /// Has a state machine of a creature that every game runs by itself, in a game that follows the scene host's, play
    /// a talk that this game's player started with it here by itself, as it goes to the first state of it, and tells
    /// the scene host's game (see <see cref="TellOwnTalk"/>).
    /// </summary>
    private void StartsLocalTalk(PlayMakerFSM machine, FsmState toState, string eventName) {
        TellOwnTalk(machine, eventName);
        var talk = new OwnLeap(OwnCreatures.PlaceOf(machine.gameObject), true);
        talk.Passed.Add(toState.Name);
        _ownLeaps[machine.Fsm] = talk;
        Logger.Info(
            $"[Own creatures] The local player started a talk with '{talk.Place}', which goes to '{toState.Name}' of " +
            $"'{machine.FsmName}' and plays the talk here by itself"
        );
    }

    /// <summary>
    /// Has a state machine of a creature that every game runs by itself, in a game that follows the scene host's, play
    /// a talk here by itself, as it goes to a state that runs a line of it or a prompt, by itself or as a step of the
    /// scene host's. Whether it does.
    /// </summary>
    private bool StartsOwnTalk(PlayMakerFSM machine, FsmState toState) {
        if (!Entity.Entity.RunsTalk(toState) || _ownLeaps.ContainsKey(machine.Fsm)) {
            return false;
        }

        var talk = new OwnLeap(OwnCreatures.PlaceOf(machine.gameObject), true);
        talk.Passed.Add(toState.Name);
        _ownLeaps[machine.Fsm] = talk;
        Logger.Info(
            $"[Own creatures] '{talk.Place}' goes into a talk in '{toState.Name}' of '{machine.FsmName}', which it " +
            "plays here by itself as the local player reads it"
        );
        return true;
    }

    /// <summary>
    /// Whether a state machine of a creature that every game runs by itself, in a game that follows the scene host's,
    /// waits at the end of a talk that it played here by itself for the scene host's to come as far: every step of its
    /// own waits meanwhile, as one of the scene host's takes it on (see <see cref="WaitsForSceneHostsTalk"/>).
    /// </summary>
    private bool WaitsAfterOwnTalk(Fsm fsm) {
        return _ownLeaps.TryGetValue(fsm, out var talk) && talk is { Talk: true, Through: true };
    }

    /// <summary>
    /// Takes a step of the scene host's creature, in a game that follows it, while its creature here waits at the end
    /// of a talk that it played by itself: one into a state of the talk, which the creature here went through already,
    /// is let go of, and the first beyond the talk ends the wait and is taken as any step. Whether it is let go of.
    /// </summary>
    private bool WaitsForSceneHostsTalk(ClientPlayerData player, Fsm fsm, OwnLeap talk, CoopSaveUpdate update) {
        if (talk.Passed.Contains(update.StateName)) {
            Logger.Info(
                $"[Own creatures] '{talk.Place}' went to '{update.StateName}' of '{fsm.Name}' in {player.Username}'s " +
                "game, still in the talk that is over here"
            );
            return true;
        }

        _ownLeaps.Remove(fsm);
        Logger.Info(
            $"[Own creatures] '{talk.Place}' went to '{update.StateName}' of '{fsm.Name}' in {player.Username}'s " +
            "game, through the talk there too, and goes on from there here"
        );
        return false;
    }

    /// <summary>
    /// Whether a creature that every game runs by itself that is through a talk here goes on from the latest step of
    /// the scene host's that came meanwhile: one beyond the talk, as the scene host's player was through it first.
    /// </summary>
    private static bool TalkGoesOn(OwnLeap talk) {
        return talk.Latest is { } latest && !talk.Passed.Contains(latest.StateName);
    }

    /// <summary>
    /// Tells the scene host's game that the player of this game, which follows it, started a talk with a creature that
    /// every game runs by itself: the state that it went from and the event that took it on (see
    /// <see cref="TakeOwnTalk"/>).
    /// </summary>
    private void TellOwnTalk(PlayMakerFSM machine, string eventName) {
        var word = new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.OwnCreature,
            Scene = machine.gameObject.scene.name,
            ObjectPath = OwnCreatures.PlaceOf(machine.gameObject),
            FsmName = machine.FsmName,
            StateName = machine.Fsm.ActiveStateName,
            PartCount = OwnTalkPart,
            Names = [eventName]
        };
        Logger.Info(
            $"[Own creatures] The local player started a talk with '{word.ObjectPath}' from '{word.StateName}' of " +
            $"'{word.FsmName}' by '{eventName}', as the scene host's game is told"
        );
        SendToRoomMembers(word);
    }

    /// <summary>
    /// Takes in the scene host's game a talk that the player of another member's game started with a creature that
    /// every game runs by itself (see <see cref="TellOwnTalk"/>): its creature here goes on by the same event if it is
    /// still in the state that that one went from, which puts this game's player into the talk too, and every other
    /// game takes it from here as a step of the scene host's. One that went on since, as this game's player started a
    /// talk with it as well, keeps to its own way.
    /// </summary>
    private void TakeOwnTalk(ClientPlayerData player, GameObject creature, CoopSaveUpdate update) {
        var place = update.ObjectPath;
        if (IsSceneHost?.Invoke() != true) {
            Logger.Info(
                $"[Own creatures] {player.Username} started a talk with '{place}' in their game, which the scene " +
                "host's game takes"
            );
            return;
        }

        var fsm = PlayMakerFSM.FindFsmOnGameObject(creature, update.FsmName).Fsm;
        var eventName = update.Names[0];
        if (fsm.ActiveStateName != update.StateName) {
            Logger.Info(
                $"[Own creatures] {player.Username} started a talk with '{place}' from '{update.StateName}' in their " +
                $"game, but it is in '{fsm.ActiveStateName}' of '{fsm.Name}' here and keeps to it"
            );
            return;
        }

        Logger.Info(
            $"[Own creatures] {player.Username} started a talk with '{place}' in their game, and it goes on from " +
            $"'{update.StateName}' of '{fsm.Name}' by '{eventName}' here too"
        );
        fsm.Event(eventName);
    }
}
