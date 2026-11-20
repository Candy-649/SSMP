using System.Collections.Generic;
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
/// The talks of the creatures that every game runs by itself (see OwnCreatures), like the one that a knight holds with
/// the player who wakes it before its fight. Each player reads the talk in their own game, at their own pace, and the
/// creature goes on from the steps of the scene host's once it is over (USER 11-16 "把这个boss本地化了吧",
/// "改成本地状态机就不一定要烦恼这些了").
///
/// A talk that the player of a game that follows the scene host's starts takes the creature there on at once, as a
/// talk does in a game of one, and the scene host's game is told, whose creature goes on by the same way if it is
/// still where that one was (see <see cref="TellOwnTalk"/>, <see cref="TakeOwnTalk"/>): every other game takes it from
/// there as a step of the scene host's, and the game puts its player into the talk as it does for a talk that a
/// character starts by itself. A word that comes while this game's player is on the way into the same talk is left
/// to their own word as they get there (see <see cref="OnTheWayIntoTalk"/>). A creature that goes into a talk, by a
/// step of its own or of the scene host's, plays it
/// here to its end, each line as this game's player reads it, while the steps of the scene host's that come meanwhile
/// wait, as for a leap at this game's player (see CoopSave.OwnCatches). It is over as it leaves the state that ended
/// its dialogue with no more of the talk to come, as a talk with a copy is (see Entity.EndsTalk). That step waits in
/// every game until every player is through the talk, as the fight after the talk of a room does
/// (BossRoomCoop.TryHoldFightGate; USER 11-17 "为啥房主读完就开打，不应该都读完才开打吗"); then the creature here
/// waits for the scene host's step out of the talk, or goes on from it if it came first (see
/// <see cref="WaitsAfterOwnTalk"/>). Each game shows its own lines alone: the boss room shares none of them
/// (BossRoomCoop.OnStartDialogue), and a way into a step of the scene host's through a talk runs none (see
/// OnOwnCreatureStep). The copy of the knight that the room had before played the talk of the player who woke it in
/// their game, and the talk that the scene host's game started a moment later took the box from it (USER 11-16
/// "为啥我这里没有对话框").
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
    /// of a talk that it played by itself: one into or before the talk, which the creature here went through already,
    /// is let go of, and the first beyond the talk ends the wait and is taken as any step. Whether it is let go of.
    /// </summary>
    private bool WaitsForSceneHostsTalk(ClientPlayerData player, Fsm fsm, OwnLeap talk, CoopSaveUpdate update) {
        if (LiesBeforeTalkEnd(fsm, talk, update.StateName)) {
            Logger.Info(
                $"[Own creatures] '{talk.Place}' went to '{update.StateName}' of '{fsm.Name}' in {player.Username}'s " +
                "game, in or before the talk that is over here"
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
    /// the scene host's that came meanwhile: one beyond the talk, which comes before the word that lets the talk end
    /// here when the scene host's player is the last through it (see BossRoomCoop.TryHoldFightGate).
    /// </summary>
    private static bool TalkGoesOn(Fsm fsm, OwnLeap talk) {
        return talk.Latest is { } latest && !LiesBeforeTalkEnd(fsm, talk, latest.StateName);
    }

    /// <summary>
    /// Whether a state of a state machine lies in or before a talk that its creature played here by itself: it is a
    /// state of the talk, or the ways out of the states lead from it into the talk still, like the bow of a creature
    /// that a noise woke in the scene host's game, whose player has yet to talk to it there (review: a step into that
    /// bow, taken for one beyond the talk, set the creature here back to it, and its player heard the talk again).
    /// </summary>
    private static bool LiesBeforeTalkEnd(Fsm fsm, OwnLeap talk, string stateName) {
        if (talk.Passed.Contains(stateName)) {
            return true;
        }

        var seen = new HashSet<string> { stateName };
        var queue = new Queue<string>(seen);
        while (queue.Count > 0) {
            if (fsm.GetState(queue.Dequeue()) is not { } state) {
                continue;
            }

            // A way out that leads nowhere, which the game's state machines have, leads to no state
            foreach (var transition in state.Transitions) {
                if (transition.ToState is not { } to || !seen.Add(to)) {
                    continue;
                }

                if (talk.Passed.Contains(to)) {
                    return true;
                }

                queue.Enqueue(to);
            }
        }

        return false;
    }

    /// <summary>
    /// Switches off the lines of a talk and the prompts of a state, for a way through it into a step of the scene
    /// host's (see OnOwnCreatureStep), returning them to be switched on again once it is entered.
    /// </summary>
    private static List<FsmStateAction> MuteTalk(FsmState state) {
        var muted = new List<FsmStateAction>();
        foreach (var action in state.Actions) {
            if (action.Enabled && action is RunDialogueBase or YesNoAction) {
                action.Enabled = false;
                muted.Add(action);
            }
        }

        return muted;
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
    /// talk with it as well, keeps to its own way, and so does one that this game's player is on the way into the talk
    /// with (see <see cref="OnTheWayIntoTalk"/>).
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

        if (OnTheWayIntoTalk(fsm) is { } npc) {
            Logger.Info(
                $"[Own creatures] {player.Username} started a talk with '{place}' in their game, while this game's " +
                $"player is on the way into it through '{npc.name}' here, which takes it on as they get there"
            );
            return;
        }

        Logger.Info(
            $"[Own creatures] {player.Username} started a talk with '{place}' in their game, and it goes on from " +
            $"'{update.StateName}' of '{fsm.Name}' by '{eventName}' here too"
        );
        fsm.Event(eventName);
    }

    /// <summary>
    /// The character through which this game's player is on the way into a talk with a creature that every game runs
    /// by itself, if they are: the game walks them to their place, holding them, and the character tells the creature
    /// the talk once they get there (NPCControlBase.MovePlayer, PlayMakerNPC.OnStartDialogue). A word of another
    /// game's talk that took the creature into it meanwhile left the character nothing to tell, which the game takes
    /// for a talk that nothing heard: it gave the player back while the talk ran, and the button that turns its lines
    /// made them jump (USER 11-20 "对话开始时有概率不被游戏锁住": both players pressed to talk to the knight within a
    /// moment, and the one whose walk ended later read the talk free, in each of the four times that it happened).
    /// </summary>
    /// <param name="fsm">The state machine of the creature.</param>
    private static PlayMakerNPC? OnTheWayIntoTalk(Fsm fsm) {
        if (InteractManager.BlockingInteractable is not PlayMakerNPC { IsRunningDialogue: false } npc) {
            return null;
        }

        var told = npc.CustomEventTarget != null
            ? npc.CustomEventTarget
            : DialogueFsmField?.GetValue(npc) as PlayMakerFSM;
        return told != null && told.Fsm == fsm ? npc : null;
    }
}
