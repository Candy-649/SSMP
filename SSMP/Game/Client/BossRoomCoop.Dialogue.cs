using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using SSMP.Ui;
using TeamCherry.Localization;
using TMProOld;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Dialogue rules for boss rooms: a fight that comes after dialogue only starts once every player finished it.
/// Dialogue of a boss only runs for the scene host, so the other players in the scene are shown the same dialogue and
/// the boss waits at its end until they read it. Dialogue of a room runs for every player, so the room waits before its
/// fight until every player got there.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// The event that dialogue sends to its FSM when the conversation ends.
    /// </summary>
    private const string ConversationEndEventName = "CONVO_END";

    /// <summary>
    /// How long, in seconds, a boss waits for other players to read its dialogue before it continues anyway.
    /// </summary>
    private const float DialogueTimeout = 180f;

    /// <summary>
    /// How long, in seconds, shared dialogue may take to open before it counts as ended.
    /// </summary>
    private const float DialogueOpenTime = 1f;

    /// <summary>
    /// How often, in seconds, the local player checks whether the rooms that other players wait at can still get to
    /// their fight.
    /// </summary>
    private const float FightReachCheckInterval = 1f;

    /// <summary>
    /// The message for a player whose boss waits for other players to read its dialogue.
    /// </summary>
    private const string ReadingMessage = "Waiting for your teammate to finish the dialogue...";

    /// <summary>
    /// The field with the dialogue box of the game.
    /// </summary>
    private static readonly FieldInfo? DialogueBoxField = typeof(DialogueBox).GetField(
        "_instance",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
    );

    /// <summary>
    /// The field with whether the dialogue box shows dialogue.
    /// </summary>
    private static readonly FieldInfo? DialogueRunningField = typeof(DialogueBox).GetField(
        "isDialogueRunning",
        InstanceFlags
    );

    /// <summary>
    /// Hooks for sharing dialogue of bosses with the other players.
    /// </summary>
    private readonly List<Hook> _startDialogueHooks = [];

    /// <summary>
    /// The dialogue of bosses that other players read, by the FSM of the boss.
    /// </summary>
    private readonly Dictionary<Fsm, SharedDialogue> _sharedDialogues = new();

    /// <summary>
    /// Dialogue that the scene host shared, which is shown to the local player in order.
    /// </summary>
    private readonly Queue<BossRoomUpdate> _dialogueQueue = new();

    /// <summary>
    /// The players who got to the fight of each room with dialogue, by the FSM of the room.
    /// </summary>
    private readonly Dictionary<Fsm, RoomArrivals> _fightReadiness = new();

    /// <summary>
    /// Fights of rooms with dialogue that are held back until every player got to them, by the FSM of the room.
    /// </summary>
    private readonly Dictionary<Fsm, HeldFight> _heldFights = new();

    /// <summary>
    /// The FSMs of rooms with dialogue whose fight started, which aren't held back again.
    /// </summary>
    private readonly HashSet<Fsm> _startedFights = [];

    /// <summary>
    /// The transitions into the fight of each room with dialogue, by the FSM of the room.
    /// </summary>
    private readonly Dictionary<Fsm, HashSet<string>> _fightTransitions = new();

    /// <summary>
    /// The FSMs that started dialogue for the local player in the current scene.
    /// </summary>
    private readonly HashSet<Fsm> _dialogueFsms = [];

    /// <summary>
    /// How many starts of dialogue are running, as newer dialogue actions may start their dialogue through older ones.
    /// </summary>
    private int _startDialogueDepth;

    /// <summary>
    /// The shared dialogue that is shown to the local player, or null.
    /// </summary>
    private BossRoomUpdate? _shownDialogue;

    /// <summary>
    /// Counts the shared dialogue that was shown, so that callbacks of an earlier conversation are ignored.
    /// </summary>
    private int _shownDialogueId;

    /// <summary>
    /// When the shared dialogue that is shown opened, in unscaled seconds.
    /// </summary>
    private float _shownDialogueTime;

    /// <summary>
    /// Whether control was taken from the local player to read shared dialogue.
    /// </summary>
    private bool _tookHeroControl;

    /// <summary>
    /// When the local player next checks whether the rooms that other players wait at can still get to their fight.
    /// </summary>
    private float _nextFightReachCheckTime;

    /// <summary>
    /// Registers the hooks for dialogue in boss rooms.
    /// </summary>
    private void RegisterDialogueHooks() {
        // Newer dialogue actions start their dialogue in their own way, so each of them is hooked
        AddStartDialogueHook<RunDialogueBase>();
        AddStartDialogueHook<RunDialogueV4>();
        AddStartDialogueHook<RunDialogueV5>();
    }

    /// <summary>
    /// Hooks how a type of dialogue action starts its dialogue.
    /// </summary>
    private void AddStartDialogueHook<TAction>() where TAction : RunDialogueBase {
        var method = typeof(TAction).GetMethod(
            "StartDialogue",
            InstanceFlags | BindingFlags.DeclaredOnly,
            null,
            [typeof(PlayMakerNPC)],
            null
        );
        if (method == null) {
            Logger.Warn($"Could not find how '{typeof(TAction).Name}' starts dialogue, its dialogue is not shared");
            return;
        }

        var hook = CreateHook(
            method,
            new Action<Action<TAction, PlayMakerNPC>, TAction, PlayMakerNPC>(
                (orig, self, npc) => OnStartDialogue(() => orig(self, npc), self)
            )
        );
        if (hook != null) {
            _startDialogueHooks.Add(hook);
        }
    }

    /// <summary>
    /// Disposes the hooks for dialogue in boss rooms.
    /// </summary>
    private void DeregisterDialogueHooks() {
        foreach (var hook in _startDialogueHooks) {
            hook.Dispose();
        }

        _startDialogueHooks.Clear();
    }

    /// <summary>
    /// Shares dialogue that a boss of the scene host starts with the other players in the scene.
    /// </summary>
    private void OnStartDialogue(Action startDialogue, RunDialogueBase action) {
        _startDialogueDepth++;
        try {
            startDialogue();
        } finally {
            _startDialogueDepth--;
        }

        var fsm = action.Fsm;
        var state = action.State;
        if (fsm != null) {
            _dialogueFsms.Add(fsm);
        }

        if (_startDialogueDepth > 0 || fsm == null || state == null || !IsCoopActive() ||
            !_entityManager.IsSceneHost) {
            return;
        }

        // Entities that aren't bosses, like characters to talk to, keep their dialogue to the player who talks to them
        var info = GetInfo(fsm);
        if (!info.IsEntity || (!info.ClosesGates && !info.IsInBossScene)) {
            return;
        }

        BossRoomUpdate update;
        try {
            update = CreateDialogueUpdate(fsm, state.Name, action);
        } catch (Exception e) {
            Logger.Error($"Could not read the dialogue of '{GetPath(fsm)}' to share it:\n{e}");
            return;
        }

        Logger.Info($"Sharing the dialogue of '{GetPath(fsm)}' with the other players in the scene");
        var dialogue = new SharedDialogue(state.Name, update, Time.unscaledTime);
        _sharedDialogues[fsm] = dialogue;
        ShareDialogue(dialogue);
    }

    /// <summary>
    /// Creates the update that shows the dialogue of an action to other players.
    /// </summary>
    private static BossRoomUpdate CreateDialogueUpdate(Fsm fsm, string stateName, RunDialogueBase action) {
        var actionType = action.GetType();
        var usesCustomText = actionType.GetMethods(InstanceFlags)
            .FirstOrDefault(method => method.Name == "UsesCustomText" && method.GetParameters().Length == 0)
            ?.Invoke(action, []) is true;
        var sheet = usesCustomText ? "" : GetStringValue(GetActionField(action, "Sheet"));
        var key = usesCustomText ? "" : GetStringValue(GetActionField(action, "Key"));

        // Without a sheet and a key, the text of the scene host is all there is to show
        var text = sheet.Length > 0 && key.Length > 0
            ? ""
            : actionType.GetProperties(InstanceFlags)
                .FirstOrDefault(property => property.Name == "DialogueText")
                ?.GetValue(action) as string ?? "";

        // The same options as the dialogue action shows its dialogue with
        var options = new DialogueBox.DisplayOptions {
            ShowDecorators = GetActionField(action, "HideDecorators") is not FsmBool { Value: true },
            Alignment = GetActionField(action, "TextAlignment") is FsmEnum { Value: { } alignment }
                ? (TextAlignmentOptions) Convert.ToInt32(alignment)
                : DialogueBox.DisplayOptions.Default.Alignment,
            OffsetY = GetActionField(action, "OffsetY") is FsmFloat offsetY ? offsetY.Value : 0f,
            TextColor = Color.white
        };
        if (actionType.GetMethods(InstanceFlags)
                .FirstOrDefault(method => method.Name == "GetDisplayOptions" && method.GetParameters().Length == 1)
                ?.Invoke(action, [options]) is DialogueBox.DisplayOptions actionOptions) {
            options = actionOptions;
        }

        return new BossRoomUpdate {
            Kind = BossRoomUpdateKind.DialogueStarted,
            Path = GetPath(fsm),
            FsmName = fsm.Name,
            FromState = stateName,
            DialogueSheet = sheet,
            DialogueKey = key,
            DialogueText = text,
            HideDecorators = !options.ShowDecorators,
            OverrideContinue = GetActionField(action, "OverrideContinue") is FsmBool { IsNone: false, Value: true },
            TextAlignment = (int) options.Alignment,
            OffsetY = options.OffsetY
        };
    }

    /// <summary>
    /// Gets the value of a field that holds a string, or an empty string.
    /// </summary>
    private static string GetStringValue(object? value) {
        return value switch {
            FsmString fsmString => fsmString.Value ?? "",
            string text => text,
            _ => ""
        };
    }

    /// <summary>
    /// Sends shared dialogue to the players in the scene who didn't get it yet.
    /// </summary>
    private void ShareDialogue(SharedDialogue dialogue) {
        var hasNewReaders = false;
        foreach (var playerData in _playerData.Values) {
            if (playerData.IsInLocalScene && !dialogue.Done.Contains(playerData.Id)) {
                hasNewReaders |= dialogue.Waiting.Add(playerData.Id);
            }
        }

        if (hasNewReaders && _netClient.IsConnected) {
            dialogue.Update.SceneName = SceneManager.GetActiveScene().name;
            _netClient.UpdateManager.SetBossRoomUpdate(dialogue.Update);
        }
    }

    /// <summary>
    /// Shares dialogue that is still being read, and the fights that the local player got to, with a player who entered
    /// the local scene.
    /// </summary>
    private void ShareDialoguesWithNewPlayers() {
        foreach (var dialogue in _sharedDialogues.Values) {
            ShareDialogue(dialogue);
        }

        foreach (var pair in _fightReadiness) {
            if (pair.Value.Local && pair.Key.GameObject != null) {
                Send(BossRoomUpdateKind.Ready, pair.Key, "", "", "");
            }
        }
    }

    /// <summary>
    /// Holds back the end of shared dialogue of a boss until the other players in the scene read it.
    /// </summary>
    /// <returns>Whether the end of the dialogue is held back.</returns>
    private bool TryHoldDialogueEnd(Fsm fsm, FsmState state, string eventName) {
        if (eventName != ConversationEndEventName || !_sharedDialogues.TryGetValue(fsm, out var dialogue)) {
            return false;
        }

        RemoveGoneReaders(dialogue);
        if (dialogue.StateName != state.Name || dialogue.Waiting.Count == 0 ||
            Time.unscaledTime - dialogue.StartTime > DialogueTimeout) {
            _sharedDialogues.Remove(fsm);
            return false;
        }

        if (!dialogue.EndHeld) {
            dialogue.EndHeld = true;
            Logger.Info($"Holding back the end of the dialogue of '{GetPath(fsm)}' until the other players read it");
            UiManager.InternalChatBox.AddMessage(ReadingMessage);
        }

        return true;
    }

    /// <summary>
    /// Forgets the readers of shared dialogue who left the scene.
    /// </summary>
    private void RemoveGoneReaders(SharedDialogue dialogue) {
        dialogue.Waiting.RemoveWhere(id =>
            !_playerData.TryGetValue(id, out var playerData) || !playerData.IsInLocalScene
        );
    }

    /// <summary>
    /// Remembers that another player read shared dialogue.
    /// </summary>
    private void OnDialogueDone(BossRoomUpdate update) {
        var fsm = FindFsm(update.Path, update.FsmName);
        if (fsm == null || !_sharedDialogues.TryGetValue(fsm, out var dialogue) ||
            dialogue.StateName != update.FromState) {
            return;
        }

        dialogue.Waiting.Remove(update.PlayerId);
        dialogue.Done.Add(update.PlayerId);
    }

    /// <summary>
    /// Shows dialogue that a boss of the scene host started to the local player.
    /// </summary>
    private void OnDialogueStarted(BossRoomUpdate update) {
        if (_entityManager.IsSceneRoleDetermined && _entityManager.IsSceneHost) {
            return;
        }

        var key = GetDialogueKey(update);
        if ((_shownDialogue != null && GetDialogueKey(_shownDialogue) == key) ||
            _dialogueQueue.Any(queued => GetDialogueKey(queued) == key)) {
            return;
        }

        _dialogueQueue.Enqueue(update);
        ShowNextDialogue();
    }

    /// <summary>
    /// Gets what identifies shared dialogue: the state of the boss that started it.
    /// </summary>
    private static string GetDialogueKey(BossRoomUpdate update) {
        return update.Path + "\n" + update.FsmName + "\n" + update.FromState;
    }

    /// <summary>
    /// Shows the next shared dialogue to the local player, once no dialogue is shown.
    /// </summary>
    private void ShowNextDialogue() {
        // Dialogue that the local player is in already, like a talk with a character, is finished first
        while (_shownDialogue == null && _dialogueQueue.Count > 0 && IsDialogueRunning() != true) {
            var update = _dialogueQueue.Dequeue();
            var text = GetDialogueText(update);
            if (string.IsNullOrEmpty(text) || SceneManager.GetActiveScene().name != update.SceneName) {
                SendDialogueDone(update);
                continue;
            }

            _shownDialogue = update;
            _shownDialogueTime = Time.unscaledTime;
            var dialogueId = ++_shownDialogueId;
            TakeHeroControl();

            var options = new DialogueBox.DisplayOptions {
                ShowDecorators = !update.HideDecorators,
                Alignment = update.TextAlignment >= 0
                    ? (TextAlignmentOptions) update.TextAlignment
                    : DialogueBox.DisplayOptions.Default.Alignment,
                OffsetY = update.OffsetY,
                TextColor = Color.white
            };

            try {
                DialogueBox.StartConversation(
                    text,
                    null,
                    update.OverrideContinue,
                    options,
                    () => OnSharedDialogueEnded(dialogueId),
                    () => OnSharedDialogueEnded(dialogueId)
                );
            } catch (Exception e) {
                Logger.Error($"Could not show the dialogue that the scene host shared:\n{e}");
                OnSharedDialogueEnded(dialogueId);
            }
        }
    }

    /// <summary>
    /// Tells the scene host that the local player read shared dialogue, and shows the next one.
    /// </summary>
    private void OnSharedDialogueEnded(int dialogueId) {
        if (dialogueId != _shownDialogueId || _shownDialogue == null) {
            return;
        }

        var update = _shownDialogue;
        _shownDialogue = null;
        GiveHeroControlBack();
        SendDialogueDone(update);
        ShowNextDialogue();
    }

    /// <summary>
    /// Tells the scene host that the local player read shared dialogue.
    /// </summary>
    private void SendDialogueDone(BossRoomUpdate update) {
        Send(BossRoomUpdateKind.DialogueDone, update.Path, update.FsmName, update.FromState, "", "");
    }

    /// <summary>
    /// Gets the text of shared dialogue, in the language of the local player if it is localised.
    /// </summary>
    private static string GetDialogueText(BossRoomUpdate update) {
        return update.DialogueSheet.Length > 0 && update.DialogueKey.Length > 0
            ? new LocalisedString(update.DialogueSheet, update.DialogueKey).ToString()
            : update.DialogueText;
    }

    /// <summary>
    /// Whether the dialogue box of the game shows dialogue, or null if that can't be read.
    /// </summary>
    private static bool? IsDialogueRunning() {
        if (DialogueBoxField == null || DialogueRunningField == null) {
            return null;
        }

        var dialogueBox = DialogueBoxField.GetValue(null) as DialogueBox;
        return dialogueBox != null && DialogueRunningField.GetValue(dialogueBox) is true;
    }

    /// <summary>
    /// Takes control from the local player while they read shared dialogue, so that continuing the dialogue doesn't
    /// also make them jump or attack. Control that something else took already is left alone.
    /// </summary>
    private void TakeHeroControl() {
        var heroController = HeroController.instance;
        if (heroController == null || heroController.controlReqlinquished) {
            return;
        }

        heroController.RelinquishControl();
        _tookHeroControl = true;
    }

    /// <summary>
    /// Gives control back to the local player after they read shared dialogue.
    /// </summary>
    private void GiveHeroControlBack() {
        if (!_tookHeroControl) {
            return;
        }

        _tookHeroControl = false;
        var heroController = HeroController.instance;
        if (heroController != null) {
            heroController.RegainControl();
        }
    }

    /// <summary>
    /// Holds back the transition of a room with dialogue into its fight until every player got there, so that nobody
    /// is attacked while still reading.
    /// </summary>
    /// <returns>Whether the transition is held back.</returns>
    private bool TryHoldFightGate(Fsm fsm, FsmState state, string eventName) {
        // The end of dialogue and the steps of the room itself happen for one player at a time, while events from
        // elsewhere, like a lever, start the fight for every player at once. A fight that started isn't held back
        // again, so that a player who died doesn't stop it
        if ((eventName != ConversationEndEventName && FsmExecutionStack.ExecutingFsm != fsm) ||
            _startedFights.Contains(fsm) ||
            !IsFightTransition(GetFightTransitions(fsm), state.Name, eventName)) {
            return false;
        }

        var readiness = GetFightReadiness(fsm);
        if (!readiness.Local) {
            readiness.Local = true;
            Send(BossRoomUpdateKind.Ready, fsm, "", "", "");
        }

        // Only a room whose dialogue the local player went through waits. Without dialogue, like in a room that was won
        // before, the room goes on like it does alone, and only tells the other players that it got there
        _heldFights.TryGetValue(fsm, out var held);
        if (!_dialogueFsms.Contains(fsm) || HaveAllReached(readiness)) {
            if (held != null) {
                _heldFights.Remove(fsm);
                RestoreHero(held);
            }

            _startedFights.Add(fsm);
            return false;
        }

        if (held == null || held.StateName != state.Name || held.EventName != eventName) {
            held = new HeldFight(state.Name, eventName);
            _heldFights[fsm] = held;
            FreeHero(held);
            Logger.Info($"Holding back the fight of '{GetPath(fsm)}' until every player finished its dialogue");
        }

        NotifyWaiting(readiness);
        return true;
    }

    /// <summary>
    /// Gives control back to the local player while their room waits for the other players, so that they can move and
    /// pause instead of standing still for as long as that takes. What was changed is remembered to restore it.
    /// </summary>
    private static void FreeHero(HeldFight held) {
        var heroController = HeroController.instance;
        if (heroController != null) {
            if (heroController.controlReqlinquished) {
                heroController.RegainControl();
                held.RegainedControl = true;
            }

            if (heroController.AnimCtrl != null && !heroController.AnimCtrl.controlEnabled) {
                heroController.StartAnimationControl();
                held.StartedAnimation = true;
            }
        }

        var playerData = PlayerData.instance;
        if (playerData != null && playerData.disablePause) {
            playerData.disablePause = false;
            held.EnabledPause = true;
        }
    }

    /// <summary>
    /// Takes control from the local player again as their room took it, before the room continues to its fight.
    /// </summary>
    private static void RestoreHero(HeldFight held) {
        var heroController = HeroController.instance;
        if (heroController != null) {
            if (held.RegainedControl) {
                heroController.RelinquishControl();
            }

            if (held.StartedAnimation) {
                heroController.StopAnimationControl();
            }
        }

        if (held.EnabledPause && PlayerData.instance != null) {
            PlayerData.instance.disablePause = true;
        }
    }

    /// <summary>
    /// Gets the transitions of an FSM with dialogue that go into a state that closes gates, which is where its fight
    /// starts. FSMs of bosses and arenas, and FSMs without dialogue, have none.
    /// </summary>
    private HashSet<string> GetFightTransitions(Fsm fsm) {
        if (_fightTransitions.TryGetValue(fsm, out var transitions)) {
            return transitions;
        }

        transitions = [];
        _fightTransitions[fsm] = transitions;

        var states = fsm.States ?? [];
        if (!states.Any(state => (state.Actions ?? []).Any(action => action is RunDialogueBase))) {
            return transitions;
        }

        var info = GetInfo(fsm);
        var gameObject = fsm.GameObject;
        if (info.IsEntity || !info.ClosesGates || gameObject == null ||
            gameObject.GetComponentInParent<BattleScene>(true) != null) {
            return transitions;
        }

        var closingStates = new HashSet<string>(
            states.Where(state => (state.Actions ?? []).Any(action =>
                    GetSentEventName(action) is { } sentEvent && CloseEventNames.Contains(sentEvent)
                ))
                .Select(state => state.Name)
        );
        foreach (var state in states) {
            foreach (var transition in state.Transitions ?? []) {
                if (transition.ToState != null && closingStates.Contains(transition.ToState)) {
                    transitions.Add(GetTransitionKey(state.Name, transition.EventName));
                }
            }
        }

        foreach (var transition in fsm.GlobalTransitions ?? []) {
            if (transition.ToState != null && closingStates.Contains(transition.ToState)) {
                transitions.Add(GetTransitionKey("", transition.EventName));
            }
        }

        return transitions;
    }

    /// <summary>
    /// Whether an event in a state is one of the transitions into the fight of a room.
    /// </summary>
    private static bool IsFightTransition(HashSet<string> transitions, string stateName, string eventName) {
        return transitions.Count > 0 && (transitions.Contains(GetTransitionKey(stateName, eventName)) ||
                                         transitions.Contains(GetTransitionKey("", eventName)));
    }

    /// <summary>
    /// Gets what identifies a transition of an FSM: its state, or an empty string for a global transition, and its
    /// event.
    /// </summary>
    private static string GetTransitionKey(string stateName, string? eventName) {
        return stateName + "\n" + eventName;
    }

    /// <summary>
    /// Gets which players got to the fight of a room with dialogue.
    /// </summary>
    private RoomArrivals GetFightReadiness(Fsm fsm) {
        if (!_fightReadiness.TryGetValue(fsm, out var readiness)) {
            readiness = new RoomArrivals();
            _fightReadiness[fsm] = readiness;
        }

        return readiness;
    }

    /// <summary>
    /// Whether the local player and every other connected player got somewhere. Players in other scenes haven't.
    /// </summary>
    private bool HaveAllReached(RoomArrivals progress) {
        if (!progress.Local) {
            return false;
        }

        if (!IsHoldActive()) {
            return true;
        }

        foreach (var playerData in _playerData.Values) {
            if (!playerData.IsInLocalScene || !progress.Remote.Contains(playerData.Id)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Remembers that another player got to the fight of a room with dialogue.
    /// </summary>
    private void OnReady(BossRoomUpdate update) {
        var fsm = FindFsm(update.Path, update.FsmName);
        if (fsm == null) {
            return;
        }

        var readiness = GetFightReadiness(fsm);
        readiness.Remote.Add(update.PlayerId);
        PassUnreachableFight(fsm, readiness);
    }

    /// <summary>
    /// Tells the other players that the local player won't get to the fight of a room, because the room of the local
    /// player can't get there anymore, for example because the local player won that fight before. Rooms that aren't
    /// there or didn't start for the local player may still get there, so they keep the other players waiting.
    /// </summary>
    private void PassUnreachableFight(Fsm fsm, RoomArrivals readiness) {
        var gameObject = fsm.GameObject;
        if (readiness.Local || _startedFights.Contains(fsm) || gameObject == null ||
            !gameObject.activeInHierarchy || fsm.ActiveState == null) {
            return;
        }

        var transitions = GetFightTransitions(fsm);
        if (transitions.Count > 0 && CanReachFight(fsm, transitions)) {
            return;
        }

        readiness.Local = true;
        Logger.Info($"The room '{GetPath(fsm)}' can't get to its fight for the local player, so nobody waits for it");
        Send(BossRoomUpdateKind.Ready, fsm, "", "", "");
    }

    /// <summary>
    /// Whether an FSM can still take one of the transitions into its fight from its active state.
    /// </summary>
    private static bool CanReachFight(Fsm fsm, HashSet<string> transitions) {
        var activeState = fsm.ActiveState;
        if (activeState == null) {
            return false;
        }

        // Global transitions can be taken from any state
        if (transitions.Any(key => key.StartsWith("\n", StringComparison.Ordinal))) {
            return true;
        }

        var statesByName = new Dictionary<string, FsmState>();
        foreach (var state in fsm.States ?? []) {
            statesByName[state.Name] = state;
        }

        var queue = new Queue<FsmState>();
        queue.Enqueue(activeState);
        foreach (var transition in fsm.GlobalTransitions ?? []) {
            if (transition.ToState != null && statesByName.TryGetValue(transition.ToState, out var target)) {
                queue.Enqueue(target);
            }
        }

        var visited = new HashSet<string>();
        while (queue.Count > 0) {
            var state = queue.Dequeue();
            if (!visited.Add(state.Name)) {
                continue;
            }

            foreach (var transition in state.Transitions ?? []) {
                if (transitions.Contains(GetTransitionKey(state.Name, transition.EventName))) {
                    return true;
                }

                if (transition.ToState != null && statesByName.TryGetValue(transition.ToState, out var next)) {
                    queue.Enqueue(next);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Continues dialogue of bosses once the other players read it, shows shared dialogue to the local player, and
    /// starts fights of rooms with dialogue once every player got to them.
    /// </summary>
    private void UpdateDialogues() {
        if (_sharedDialogues.Count > 0) {
            foreach (var pair in _sharedDialogues.ToList()) {
                var fsm = pair.Key;
                var dialogue = pair.Value;
                if (fsm.GameObject == null || fsm.ActiveState?.Name != dialogue.StateName) {
                    _sharedDialogues.Remove(fsm);
                    continue;
                }

                if (!dialogue.EndHeld) {
                    continue;
                }

                RemoveGoneReaders(dialogue);
                if (dialogue.Waiting.Count > 0 && Time.unscaledTime - dialogue.StartTime <= DialogueTimeout) {
                    continue;
                }

                _sharedDialogues.Remove(fsm);
                Logger.Info($"The other players finished the dialogue of '{GetPath(fsm)}', continuing it");
                fsm.Event(ConversationEndEventName);
            }
        }

        // Dialogue that couldn't open, or that closed without calling back, doesn't keep the scene host waiting
        if (_shownDialogue != null && Time.unscaledTime - _shownDialogueTime > DialogueOpenTime &&
            IsDialogueRunning() == false) {
            Logger.Info("The dialogue that the scene host shared closed without ending");
            OnSharedDialogueEnded(_shownDialogueId);
        }

        ShowNextDialogue();

        if (_fightReadiness.Count == 0) {
            return;
        }

        foreach (var readiness in _fightReadiness.Values) {
            readiness.Remote.RemoveWhere(id =>
                !_playerData.TryGetValue(id, out var playerData) || !playerData.IsInLocalScene
            );
        }

        foreach (var pair in _heldFights.ToList()) {
            var fsm = pair.Key;
            var held = pair.Value;
            if (fsm.GameObject == null || fsm.ActiveState?.Name != held.StateName) {
                _heldFights.Remove(fsm);
                continue;
            }

            if (!HaveAllReached(GetFightReadiness(fsm))) {
                continue;
            }

            _heldFights.Remove(fsm);
            _startedFights.Add(fsm);
            RestoreHero(held);
            Logger.Info($"Every player finished the dialogue of '{GetPath(fsm)}', starting its fight");
            fsm.Event(held.EventName);
        }

        if (Time.unscaledTime < _nextFightReachCheckTime) {
            return;
        }

        _nextFightReachCheckTime = Time.unscaledTime + FightReachCheckInterval;
        foreach (var pair in _fightReadiness.ToList()) {
            if (pair.Value.Remote.Count > 0) {
                PassUnreachableFight(pair.Key, pair.Value);
            }
        }
    }

    /// <summary>
    /// Forgets the dialogue and fights of the rooms of the current scene. Bosses and rooms that still wait for other
    /// players continue without them if they are still there, like after disconnecting. Shown dialogue ends by itself.
    /// </summary>
    private void ClearDialogues() {
        var heldDialogues = _sharedDialogues
            .Where(pair => pair.Value.EndHeld)
            .Select(pair => (Fsm: pair.Key, pair.Value.StateName))
            .ToList();
        var heldFights = _heldFights.ToList();

        _sharedDialogues.Clear();
        _dialogueQueue.Clear();
        _fightReadiness.Clear();
        _heldFights.Clear();
        _startedFights.Clear();
        _fightTransitions.Clear();
        _dialogueFsms.Clear();

        if (heldDialogues.Count == 0 && heldFights.Count == 0) {
            return;
        }

        _bypassGates = true;
        try {
            foreach (var (fsm, stateName) in heldDialogues) {
                if (fsm.GameObject != null && fsm.ActiveState?.Name == stateName) {
                    fsm.Event(ConversationEndEventName);
                }
            }

            foreach (var pair in heldFights) {
                if (pair.Key.GameObject != null && pair.Key.ActiveState?.Name == pair.Value.StateName) {
                    RestoreHero(pair.Value);
                    pair.Key.Event(pair.Value.EventName);
                }
            }
        } finally {
            _bypassGates = false;
        }
    }

    /// <summary>
    /// Dialogue of a boss of the scene host that other players read.
    /// </summary>
    private class SharedDialogue {
        /// <summary>
        /// The state of the boss that runs the dialogue.
        /// </summary>
        public readonly string StateName;

        /// <summary>
        /// The update that shows the dialogue to other players.
        /// </summary>
        public readonly BossRoomUpdate Update;

        /// <summary>
        /// When the dialogue started, in unscaled seconds.
        /// </summary>
        public readonly float StartTime;

        /// <summary>
        /// The IDs of the players who still read the dialogue.
        /// </summary>
        public readonly HashSet<ushort> Waiting = [];

        /// <summary>
        /// The IDs of the players who read the dialogue.
        /// </summary>
        public readonly HashSet<ushort> Done = [];

        /// <summary>
        /// Whether the end of the dialogue is held back for the players who still read it.
        /// </summary>
        public bool EndHeld;

        public SharedDialogue(string stateName, BossRoomUpdate update, float startTime) {
            StateName = stateName;
            Update = update;
            StartTime = startTime;
        }
    }

    /// <summary>
    /// The fight of a room with dialogue that is held back until every player got to it.
    /// </summary>
    private class HeldFight {
        /// <summary>
        /// The state of the room that waits for the other players.
        /// </summary>
        public readonly string StateName;

        /// <summary>
        /// The event that continues the room to its fight.
        /// </summary>
        public readonly string EventName;

        /// <summary>
        /// Whether control was given back to the local player while waiting.
        /// </summary>
        public bool RegainedControl;

        /// <summary>
        /// Whether the animations of the local player were started again while waiting.
        /// </summary>
        public bool StartedAnimation;

        /// <summary>
        /// Whether pausing was allowed again while waiting.
        /// </summary>
        public bool EnabledPause;

        public HeldFight(string stateName, string eventName) {
            StateName = stateName;
            EventName = eventName;
        }
    }
}
