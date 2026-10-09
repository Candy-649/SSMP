using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity.Action;
using SSMP.Ui;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// A talk of the local player with the copy of a creature whose talk is one of the creature's own FSMs - the FSM that
/// also runs the creature, which only the scene host's game runs: a guardian that offers a fight, a sparring partner,
/// a knight met before its fight. The copy keeps that FSM switched off, and the game switches on whatever FSM a talk
/// goes to before it tells it anything (PlayMakerNPC.SendEvent, checked in IL). So pressing to talk to such a copy
/// started the creature's whole FSM in this game as well, from its first state, and from then on this game ran a
/// creature of its own beside the one it is shown. Only the player who came into the room first could really talk to
/// it (USER 10-10: "凭什么只能由先进房的角色触发啊").
///
/// Each player talks to it in their own game, as to any character. The copy's FSM runs here for the talk, the way it
/// runs here for an input of the local player (<see cref="StartRunningHere"/>): from the state the scene host's
/// creature is in, or - while that one is busy, talking with the scene host's player - from the one state that takes a
/// talk; what the talk tells anything both games share is left to the scene host (<see cref="IsLeftToSceneHost"/>),
/// but what it writes into the save and tells the room is this player's, as for the part of a boss each game plays.
/// The talk is over once the FSM leaves the state that ended its dialogue. Where it would go from there is what the
/// talk did to the creature - a fight it starts, a guardian that rises - and the scene host's creature goes there
/// (<see cref="TakeTalkEnd"/>), unless it has gone somewhere else meanwhile. A talk that does not give the player
/// back hands them over to the creature, which gives them back as it does in its own game (Entity.HandOver).
///
/// What the scene host's creature does meanwhile is held and given the copy once the talk is over. The scene host's
/// game does not see the creature talk to this player; it sees what the talk led to.
/// </summary>
internal partial class Entity {
    /// <summary>
    /// How long a talk that runs here can go without a line on screen, a prompt or anything else to wait for, in
    /// seconds, before it counts as stuck and the copy follows the scene host again.
    /// </summary>
    private const float TalkStallTime = 5f;

    /// <summary>
    /// How long a talk that runs here lasts at the most, in seconds.
    /// </summary>
    private const float TalkLongest = 900f;

    /// <summary>
    /// How many states that each go on to the next by themselves are looked through for more of a talk, after a state
    /// that ended its dialogue (see <see cref="MoreTalkFollows"/>).
    /// </summary>
    private const int TalkLookAhead = 8;

    /// <summary>
    /// The FSM that a talking character tells about its talk when nothing else is set to hear it.
    /// </summary>
    private static readonly FieldInfo? NpcDialogueFsmField = typeof(PlayMakerNPC).GetField(
        "dialogueFsm", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
    );

    /// <summary>
    /// The other FSMs that a talking character tells about its talk.
    /// </summary>
    private static readonly FieldInfo? NpcSecondaryFsmsField = typeof(PlayMakerNPC).GetField(
        "secondaryFsms", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
    );

    /// <summary>
    /// The event that a talking character tells its FSM as a talk starts.
    /// </summary>
    private static readonly FieldInfo? NpcInteractEventField = typeof(PlayMakerNPC).GetField(
        "interactEvent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
    );

    /// <summary>
    /// The game's dialogue box, of which it keeps one.
    /// </summary>
    private static readonly FieldInfo? TalkBoxField = typeof(DialogueBox).GetField(
        "_instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
    );

    /// <summary>
    /// Whether the dialogue box shows dialogue.
    /// </summary>
    private static readonly FieldInfo? TalkBoxRunningField = typeof(DialogueBox).GetField(
        "isDialogueRunning", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
    );

    /// <summary>
    /// The FSMs of copies that run here for a talk of the local player, with their entities. They go from state to
    /// state as the talk takes them, until it is over (see <see cref="OnSwitchState"/>).
    /// </summary>
    private static readonly Dictionary<HutongGames.PlayMaker.Fsm, Entity> TalkFsms = new();

    /// <summary>
    /// The hook on how a talking character tells its FSM about a talk.
    /// </summary>
    private static Hook? _talkHook;

    /// <summary>
    /// When the local player was last told that a creature can't be talked to now, so that one press says it once.
    /// </summary>
    private static float _talkRefusedAt = float.MinValue;

    /// <summary>
    /// Raised on a scene client when a talk of the local player with the copy of an entity is over: the entity, the
    /// index of the FSM, the state the talk started in and the state the FSM would go to from it, where the scene
    /// host's creature goes too (see <see cref="TakeTalkEnd"/>).
    /// </summary>
    public static event Action<Entity, byte, string, string>? TalkLedTo;

    /// <summary>
    /// Whether <see cref="_runHere"/> runs for a talk of the local player.
    /// </summary>
    private bool _runHereTalk;

    /// <summary>
    /// The character the local player talks through in the talk that runs here.
    /// </summary>
    private PlayMakerNPC? _talkNpc;

    /// <summary>
    /// The state that the talk that runs here started in.
    /// </summary>
    private FsmState? _talkFrom;

    /// <summary>
    /// The state that the FSM of the talk that runs here would go to once the talk was over, or null while it is not.
    /// </summary>
    private FsmState? _talkEnd;

    /// <summary>
    /// When the talk that runs here started, in unscaled seconds.
    /// </summary>
    private float _talkStarted;

    /// <summary>
    /// When the talk that runs here last had something to wait for, in unscaled seconds.
    /// </summary>
    private float _talkLastBusy;

    /// <summary>
    /// Whether the game ended the talk that runs here by force (see <see cref="OnNpcSendEvent"/>).
    /// </summary>
    private bool _talkForcedEnd;

    /// <summary>
    /// Puts the hook on how a talking character tells its FSM about a talk in place, once.
    /// </summary>
    private static void HookTalks() {
        if (_talkHook != null) {
            return;
        }

        var send = typeof(PlayMakerNPC).GetMethod(
            "SendEvent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [typeof(string)], null
        );
        if (send == null || send.ReturnType != typeof(bool)) {
            Logger.Warn(
                "Could not find how a talking character tells its FSM about a talk, so a talk with a copy may start " +
                "the copy's own FSM"
            );
            return;
        }

        _talkHook = new Hook(send, OnNpcSendEvent);
    }

    /// <summary>
    /// Hook for a talking character telling its FSM about a talk: a talk with a copy whose FSM only the scene host's
    /// game runs runs that FSM here for the talk, rather than switching it on for good.
    /// </summary>
    private static bool OnNpcSendEvent(Func<PlayMakerNPC, string, bool> orig, PlayMakerNPC self, string eventName) {
        PlayMakerFSM target;
        Entity entity;
        try {
            var told = self.CustomEventTarget != null
                ? self.CustomEventTarget
                : NpcDialogueFsmField?.GetValue(self) as PlayMakerFSM;
            if (told == null || told.enabled || FindByCopyPart(told.gameObject) is not { } found ||
                !found._fsms.Client.Contains(told) || !found.IsRunBySceneHostAlone(told)) {
                return orig(self, eventName);
            }

            target = told;
            entity = found;
        } catch (Exception e) {
            Logger.Warn($"Could not check a talk with a copy: {e.Message}");
            return orig(self, eventName);
        }

        // The game ends a talk by force when the player is interrupted (InteractManager.OnHeroInterrupted, the only
        // caller of PlayMakerNPC.ForceEndDialogue): it tells the FSM, then closes the box and gives the player back
        // itself. The FSM may stay where it was, doing something every frame, which kept the talk looking busy for as
        // long as a talk can last: it is over here on that word
        if (entity._runHere == target && entity._runHereTalk && eventName == "CONVO_END_FORCED") {
            entity._talkForcedEnd = true;
        }

        // Told while the copy's FSM runs here - the talk it is in, the end of a boss that each game plays - or as a
        // talk with it starts, it is told without being switched on, which would also run it a second time each frame
        if (entity._runHere == target || entity.StartTalkHere(self, target, eventName)) {
            return TellHere(self, target, eventName);
        }

        // Nothing here hears it, which makes the game end the talk and give the player back
        // (PlayMakerNPC.OnStartDialogue)
        return false;
    }

    /// <summary>
    /// Tells the FSMs of a talking character an event as the game does, without switching any of them on.
    /// </summary>
    private static bool TellHere(PlayMakerNPC npc, PlayMakerFSM target, string eventName) {
        if (NpcSecondaryFsmsField?.GetValue(npc) is PlayMakerFSM[] secondaries) {
            foreach (var secondary in secondaries) {
                secondary.SendEventRecursive(eventName);
            }
        }

        return target.SendEventRecursive(eventName);
    }

    /// <summary>
    /// Starts the FSM of the copy that a talk of the local player goes to running here for the talk, if the talk can
    /// start.
    /// </summary>
    /// <param name="npc">The character the player talks through.</param>
    /// <param name="copyFsm">The FSM of the copy.</param>
    /// <param name="eventName">The event that the character tells the FSM.</param>
    /// <returns>Whether it runs here now.</returns>
    private bool StartTalkHere(PlayMakerNPC npc, PlayMakerFSM copyFsm, string eventName) {
        var index = _fsms.Client.IndexOf(copyFsm);
        if (!_isControlled || Object.Client == null || index < 0 || SwitchToStateField == null ||
            NpcInteractEventField?.GetValue(npc) as string != eventName) {
            return false;
        }

        if (_runHere != null) {
            // A catch, a combo or the end of a boss is under way here, and the talk waits for it
            if (_runHereTalk || _runHereForGood || _runHereLed || PlaysACombo) {
                SayCannotTalkNow();
                return false;
            }

            FollowSceneHost();
        }

        var fsm = copyFsm.Fsm;
        var from = StateOfCopy(copyFsm) is { } state && EntityFsmActions.FindTransition(fsm, state, eventName) != null
            ? state
            : TheOneStateThatTakes(fsm, eventName);
        if (from == null) {
            Logger.Info(
                $"The local player could not talk to the copy of entity {Id} ({Type}): its '{copyFsm.FsmName}' takes " +
                $"'{eventName}' neither in '{StateOfCopy(copyFsm)?.Name}', where the scene host's is, nor in one " +
                "state of its own"
            );
            SayCannotTalkNow();
            return false;
        }

        _runHereTalk = true;
        _runHereIndex = (byte) index;
        _talkNpc = npc;
        _talkFrom = from;
        _talkEnd = null;
        _talkForcedEnd = false;
        _talkStarted = _talkLastBusy = Time.unscaledTime;
        foreach (var fsmState in fsm.States) {
            ForgetEndsEntered(fsmState);
        }

        StartRunningHere(copyFsm, from);
        StartSteppingRunHere(copyFsm);

        Logger.Info(
            $"The local player talks to the copy of entity {Id} ({Type}): its '{copyFsm.FsmName}' runs here for the " +
            $"talk from '{from.Name}'"
        );
        return true;
    }

    /// <summary>
    /// The one state of an FSM that goes on from the given event, or null if there are none or several.
    /// </summary>
    private static FsmState? TheOneStateThatTakes(HutongGames.PlayMaker.Fsm fsm, string eventName) {
        FsmState? found = null;
        foreach (var state in fsm.States) {
            if (Array.Exists(state.Transitions, transition => transition.EventName == eventName)) {
                if (found != null) {
                    return null;
                }

                found = state;
            }
        }

        return found;
    }

    /// <summary>
    /// Tells the local player that the creature can't be talked to right now.
    /// </summary>
    private static void SayCannotTalkNow() {
        if (Time.unscaledTime - _talkRefusedAt < 1f) {
            return;
        }

        _talkRefusedAt = Time.unscaledTime;
        UiManager.InternalChatBox.AddMessage(Lang.Pick(
            "This character can't be talked to right now. Try again in a moment.",
            "现在还不能和这个角色对话，等一下再试。"
        ));
    }

    /// <summary>
    /// Takes it that the FSM of a talk that runs here is about to go to a state: once the talk is over - the FSM leaves
    /// the state that ended its dialogue - it goes there no further here; the scene host's creature goes there instead.
    /// </summary>
    /// <returns>Whether the FSM is kept where it is.</returns>
    private bool HoldsTalkAt(HutongGames.PlayMaker.Fsm fsm, FsmState toState) {
        if (_talkEnd != null) {
            return true;
        }

        if (fsm.ActiveState is not { } leaving) {
            return false;
        }

        // Whether this visit ended the dialogue, which is cleared for the next one
        var ended = Array.Exists(leaving.Actions, action => action is EndDialogue { Entered: true });
        ForgetEndsEntered(leaving);

        // A talk with a pause in it goes on: a creature ends its call, wakes, bows and then speaks again, and all of it
        // is the talk of the player who started it. Cut at the pause, the rest was played in the scene host's game, to
        // its player, who had not talked at all, and the player who had never heard it.
        if (!ended || MoreTalkFollows(fsm, toState)) {
            return false;
        }

        _talkEnd = toState;
        return true;
    }

    /// <summary>
    /// Clears that the actions of a state that end dialogue were entered. The game sets it the first time one is
    /// entered and never clears it (FsmState.ActivateActions, checked in IL): from a copy's second talk on, every
    /// state that ends dialogue read as having ended it, whether it got that far that time or not.
    /// </summary>
    private static void ForgetEndsEntered(FsmState state) {
        foreach (var action in state.Actions) {
            if (action is EndDialogue end) {
                end.Entered = false;
            }
        }
    }

    /// <summary>
    /// Whether a talk goes on from a state by itself: a line or a prompt comes within a few states that each go on to
    /// the next once they are done (see <see cref="TalkLookAhead"/>).
    /// </summary>
    private static bool MoreTalkFollows(HutongGames.PlayMaker.Fsm fsm, FsmState from) {
        var state = from;
        for (var i = 0; i < TalkLookAhead && state != null; i++) {
            if (Array.Exists(state.Actions, action => action.Enabled && action is RunDialogueBase or YesNoAction)) {
                return true;
            }

            state = Array.Find(state.Transitions, transition => transition.EventName == "FINISHED") is { } finished
                ? finished.ToFsmState ?? fsm.GetState(finished.ToState)
                : null;
        }

        return false;
    }

    /// <summary>
    /// Goes on with the talk that runs here, and ends it once it is over or stuck.
    /// </summary>
    /// <returns>Whether it ended.</returns>
    private bool UpdateTalkHere() {
        if (_talkEnd is { } to) {
            EndTalkHere(to);
            return true;
        }

        if (_talkForcedEnd) {
            Logger.Info($"The game ended the talk with the copy of entity {Id}, so the copy follows the scene host again");
            EndTalkHere(null);
            return true;
        }

        var now = Time.unscaledTime;
        if (IsTalkBusy()) {
            _talkLastBusy = now;
        }

        if (_talkNpc == null || now - _talkLastBusy > TalkStallTime || now - _talkStarted > TalkLongest) {
            Logger.Info($"The talk with the copy of entity {Id} stopped going anywhere, so the copy follows the scene " +
                        "host again");
            EndTalkHere(null);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the talk that runs here waits for something: a line on screen, a prompt, or anything its state is still
    /// doing.
    /// </summary>
    private bool IsTalkBusy() {
        if (TalkBoxField?.GetValue(null) is DialogueBox box && box != null &&
            TalkBoxRunningField?.GetValue(box) is true) {
            return true;
        }

        return _runHereFsm?.ActiveState is { } state && StateFinishedField?.GetValue(state) is false;
    }

    /// <summary>
    /// Ends the talk that runs here: the copy follows the scene host again, the scene host's creature is told where
    /// the talk took it, and a player the talk did not give back is the creature's until it does.
    /// </summary>
    /// <param name="to">The state the FSM would go to from the talk, or null for a talk that stopped short.</param>
    private void EndTalkHere(FsmState? to) {
        if (!_runHereTalk) {
            return;
        }

        var from = _talkFrom;
        var fsmIndex = _runHereIndex;
        var copyFsm = _runHere;
        var npc = _talkNpc;
        var hero = HeroController.instance;
        var heroHeld = hero != null && hero.controlReqlinquished;
        FollowSceneHost();

        if (to != null && from != null) {
            // The scene host's creature goes where the talk led only from where the talk began (TakeTalkEnd). One that
            // went on from there meanwhile - its own player talked to it too - stays where it is, and nothing there
            // will give back a player the talk did not: this game does
            if (StateOfCopy(copyFsm) is { } hostState && hostState != from) {
                Logger.Info(
                    $"The talk with the copy of entity {Id} is over, but the scene host's went from '{from.Name}' to " +
                    $"'{hostState.Name}' meanwhile, so the talk leads nowhere there"
                );
                CloseTalk(npc, heroHeld);
                return;
            }

            TalkLedTo?.Invoke(this, fsmIndex, from.Name, to.Name);
            Logger.Info($"The talk with the copy of entity {Id} is over and leads from '{from.Name}' to '{to.Name}'");
            if (heroHeld) {
                HandOverTheLocalPlayer(npc);
            }

            return;
        }

        // A talk that stopped short leaves no box up, no character still talking and no player held
        CloseTalk(npc, heroHeld);
    }

    /// <summary>
    /// Ends a talk of the local player the way the game ends one that gives them back: the box goes, and the character
    /// is done talking - it can be talked to again, and what waited for the talk to end, a save among it, goes on
    /// (PlayMakerNPC.CloseDialogueBox, checked in IL). A talk that ended without giving the player back left the
    /// character talking for good, since what gives them back is the creature, which only the scene host's game runs.
    /// </summary>
    /// <param name="npc">The character the player talked through, or null.</param>
    /// <param name="giveBack">Whether to give the player back too.</param>
    private static void CloseTalk(PlayMakerNPC? npc, bool giveBack) {
        try {
            if (npc != null && npc.IsRunningDialogue) {
                npc.CloseDialogueBox(true, true, null);
            } else if (TalkBoxField?.GetValue(null) is DialogueBox box && box != null &&
                       TalkBoxRunningField?.GetValue(box) is true) {
                DialogueBox.EndConversation();
            }
        } catch (Exception e) {
            Logger.Warn($"Could not end a talk of the local player: {e.Message}");
        }

        if (!giveBack || HeroController.instance is not { } hero) {
            return;
        }

        try {
            hero.RegainControl();
            hero.StartAnimationControl();
        } catch (Exception e) {
            Logger.Warn($"Could not give the local player back after a talk: {e.Message}");
        }
    }

    /// <summary>
    /// Forgets the talk that runs here, as its FSM stops running here.
    /// </summary>
    private void ForgetTalkHere(HutongGames.PlayMaker.Fsm fsm) {
        TalkFsms.Remove(fsm);
        _runHereTalk = false;
        _talkForcedEnd = false;
        _talkNpc = null;
        _talkFrom = null;
        _talkEnd = null;
    }

    /// <summary>
    /// Takes a talk of the partner with their copy of the entity, on the scene host: the FSM goes from the state the
    /// talk started in to where the talk took the copy's, unless it has gone somewhere else meanwhile. What the state
    /// does to this game's player - who did not talk - is kept off them (see <see cref="PlayForPartner"/>).
    /// </summary>
    /// <param name="fsmIndex">The index of the FSM.</param>
    /// <param name="fromState">The state the talk started in.</param>
    /// <param name="toState">The state the talk led to.</param>
    public void TakeTalkEnd(byte fsmIndex, string fromState, string toState) {
        if (_isControlled || fsmIndex >= _fsms.Host.Count || _fsms.Host[fsmIndex] is not { } hostFsm ||
            hostFsm == null) {
            return;
        }

        if (hostFsm.ActiveStateName != fromState) {
            Logger.Info(
                $"The partner's talk with entity {Id} led to '{toState}', but its '{hostFsm.FsmName}' went from " +
                $"'{fromState}' to '{hostFsm.ActiveStateName}' meanwhile, so it stays there"
            );
            return;
        }

        if (hostFsm.Fsm.GetState(toState) is not { } to) {
            return;
        }

        // Only the state the talk leads to is kept off this game's player, who did not talk: what it puts at the player
        // goes to the partner's figure. Beyond it - a fight the talk started - the creature is after both players again,
        // and keeping it off this one for the whole fight would leave them never aimed at.
        PlayForPartner(hostFsm, true, () => hostFsm.Fsm.SetState(toState), [to]);
        Logger.Info(
            $"The partner's talk with entity {Id} took its '{hostFsm.FsmName}' from '{fromState}' to '{toState}'"
        );
    }
}
