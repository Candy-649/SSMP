using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity;
using SSMP.Hooks;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Ui;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Co-op rules for rooms whose gates are closed by PlayMaker FSMs and for boss scenes, which includes most boss rooms.
/// Arenas with a <see cref="BattleScene"/> are left to <see cref="ArenaCoop"/>.
/// A room only starts once every connected player has reached it: the event of the trigger that starts the room is held
/// back until all players have been inside one of its triggers, and the players hear that someone waits for them.
/// Gates only close for the players in the room, so a player who comes back into a running fight can still walk in.
/// When a gate of the scene host opens after it had closed, it opens for the other players too, and when a room starts
/// for another player but not for the scene host, its gates open again for that player.
/// Defeat and encounter records that a room writes for one player are written for the other players in the scene too.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// Binding flags for the private members of the game.
    /// </summary>
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Prefix of the names of objects that control boss rooms, including rooms without gates.
    /// </summary>
    private const string BossSceneNamePrefix = "Boss Scene";

    /// <summary>
    /// How many transitions may lead from a trigger to the state that closes the gates.
    /// </summary>
    private const int MaxCloseSteps = 4;

    /// <summary>
    /// How long, in seconds, the scene host gives its room to start after the room started for another player.
    /// </summary>
    private const float StartCheckTime = 15f;

    /// <summary>
    /// How often, in seconds, the scene is searched for FSMs that start rooms.
    /// </summary>
    private const float ScanInterval = 5f;

    /// <summary>
    /// How often, in seconds, the triggers of a room are searched again while none were found.
    /// </summary>
    private const float RegionResolveInterval = 1f;

    /// <summary>
    /// The distance between the points that are checked along the path of a player during a frame.
    /// </summary>
    private const float SampleSpacing = 0.25f;

    /// <summary>
    /// The distance that a player can move in a frame before it counts as a teleport.
    /// </summary>
    private const float MaxSampledDistance = 10f;

    /// <summary>
    /// How long, in seconds, a message about waiting players isn't repeated.
    /// </summary>
    private const float NoticeInterval = 30f;

    /// <summary>
    /// The event that opens a gate for a player whose room can't start.
    /// </summary>
    private const string OpenGateEventName = "BG OPEN";

    /// <summary>
    /// The message for a player who waits in a room for the other players.
    /// </summary>
    private const string WaitingMessage = "Waiting for your teammate to catch up...";

    /// <summary>
    /// The message for a player whose teammate waits in a room.
    /// </summary>
    private const string TeammateWaitingMessage = "Your teammate is waiting for you.";

    /// <summary>
    /// Events that close gates.
    /// </summary>
    private static readonly HashSet<string> CloseEventNames = ["BG CLOSE", "BG QUICK CLOSE"];

    /// <summary>
    /// Events that open gates or remove them.
    /// </summary>
    private static readonly HashSet<string> OpenEventNames = ["BG OPEN", "BG QUICK OPEN", "BG DESTROY"];

    /// <summary>
    /// The kind of trigger of each action type.
    /// </summary>
    private static readonly Dictionary<Type, TriggerKind> TriggerKinds = new();

    /// <summary>
    /// Reflected fields of actions, by action type and field name.
    /// </summary>
    private static readonly Dictionary<(Type, string), FieldInfo?> ActionFields = new();

    /// <summary>
    /// The FSM whose event SSMP sends for the scene host, or null.
    /// </summary>
    private static Fsm? _networkSender;

    /// <summary>
    /// The net client for sending room updates to other players.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The data of the other connected players, by player ID.
    /// </summary>
    private readonly Dictionary<ushort, ClientPlayerData> _playerData;

    /// <summary>
    /// The entity manager, which knows whether the local player is the scene host.
    /// </summary>
    private readonly EntityManager _entityManager;

    /// <summary>
    /// Whether the server synchronises entities. Without it, every player fights their own bosses.
    /// </summary>
    private readonly Func<bool> _isFullSynchronisation;

    /// <summary>
    /// What this class found out about the FSMs in the current scene.
    /// </summary>
    private readonly Dictionary<Fsm, FsmInfo> _fsmInfos = new();

    /// <summary>
    /// The FSMs of the current scene that were searched for triggers that start rooms.
    /// </summary>
    private readonly HashSet<Fsm> _scannedFsms = [];

    /// <summary>
    /// The FSMs of the current scene with triggers that start rooms.
    /// </summary>
    private readonly HashSet<Fsm> _startFsms = [];

    /// <summary>
    /// The players who reached each room of the current scene, by the FSM that starts the room.
    /// </summary>
    private readonly Dictionary<Fsm, RoomArrivals> _arrivals = new();

    /// <summary>
    /// The starts of rooms that are held back until all players reached them, by the FSM that starts the room.
    /// </summary>
    private readonly Dictionary<Fsm, HeldStart> _heldStarts = new();

    /// <summary>
    /// The gates that are closed for the local player, with the FSM that closed each of them if it is known.
    /// </summary>
    private readonly Dictionary<Fsm, Fsm?> _closedGates = new();

    /// <summary>
    /// The gates that a room closed while the local player was outside, which close once they walk in.
    /// </summary>
    private readonly List<PendingClose> _pendingCloses = [];

    /// <summary>
    /// Rooms that started for another player, which the scene host checks started for it too.
    /// </summary>
    private readonly List<StartCheck> _startChecks = [];

    /// <summary>
    /// The positions of the other players in the local scene in the previous frame, by player ID.
    /// </summary>
    private readonly Dictionary<ushort, Vector2> _remotePositions = new();

    /// <summary>
    /// Hook for holding back starts of rooms and keeping gates open for players outside their room.
    /// </summary>
    private Hook? _processEventHook;

    /// <summary>
    /// Hook for telling the scene host when a room started for the local player.
    /// </summary>
    private Hook? _switchStateHook;

    /// <summary>
    /// Hook for sharing records that are written as booleans of the player data.
    /// </summary>
    private Hook? _setBoolHook;

    /// <summary>
    /// Hook for sharing records that FSMs write as variables of the player data.
    /// </summary>
    private Hook? _setVariableHook;

    /// <summary>
    /// Whether this class sends an event to a gate itself, which the hooks let through.
    /// </summary>
    private bool _bypassGates;

    /// <summary>
    /// Whether this class writes a record that another player shared, so that it isn't shared back.
    /// </summary>
    private bool _applyingRecord;

    /// <summary>
    /// The position of the local player in the previous frame, or null.
    /// </summary>
    private Vector2? _lastHeroPosition;

    /// <summary>
    /// When the scene is searched for FSMs that start rooms next, in unscaled seconds.
    /// </summary>
    private float _nextScanTime;

    /// <summary>
    /// When the local player can hear again that a teammate waits for them, in unscaled seconds.
    /// </summary>
    private float _nextTeammateNoticeTime;

    public BossRoomCoop(
        NetClient netClient,
        Dictionary<ushort, ClientPlayerData> playerData,
        EntityManager entityManager,
        Func<bool> isFullSynchronisation
    ) {
        _netClient = netClient;
        _playerData = playerData;
        _entityManager = entityManager;
        _isFullSynchronisation = isFullSynchronisation;
    }

    /// <summary>
    /// Registers the hooks for boss room co-op.
    /// </summary>
    public void RegisterHooks() {
        _processEventHook = CreateHook(
            typeof(Fsm).GetMethod("ProcessEvent", InstanceFlags, null, [typeof(FsmEvent), typeof(FsmEventData)], null),
            new Action<Action<Fsm, FsmEvent, FsmEventData>, Fsm, FsmEvent, FsmEventData>(OnProcessEvent)
        );
        _switchStateHook = CreateHook(
            typeof(Fsm).GetMethod("SwitchState", InstanceFlags, null, [typeof(FsmState)], null),
            new Action<Action<Fsm, FsmState>, Fsm, FsmState>(OnSwitchState)
        );
        _setBoolHook = CreateHook(
            typeof(PlayerData).GetMethod("SetBool", InstanceFlags, null, [typeof(string), typeof(bool)], null),
            new Action<Action<PlayerData, string, bool>, PlayerData, string, bool>(OnSetPlayerDataBool)
        );
        _setVariableHook = CreateHook(
            typeof(SetPlayerDataVariable).GetMethod("OnEnter", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null),
            new Action<Action<SetPlayerDataVariable>, SetPlayerDataVariable>(OnSetPlayerDataVariableEnter)
        );
        RegisterDialogueHooks();
        RegisterEventHooks();

        EventHooks.HeroControllerUpdate += OnHeroControllerUpdate;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    /// <summary>
    /// Disposes the hooks for boss room co-op.
    /// </summary>
    public void DeregisterHooks() {
        _processEventHook?.Dispose();
        _processEventHook = null;

        _switchStateHook?.Dispose();
        _switchStateHook = null;

        _setBoolHook?.Dispose();
        _setBoolHook = null;

        _setVariableHook?.Dispose();
        _setVariableHook = null;

        DeregisterDialogueHooks();
        DeregisterEventHooks();

        EventHooks.HeroControllerUpdate -= OnHeroControllerUpdate;
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        ClearScene();
    }

    /// <summary>
    /// Creates a hook, logging an error instead of throwing if the method does not exist or can't be hooked.
    /// </summary>
    private static Hook? CreateHook(MethodInfo? method, Delegate detour) {
        if (method == null) {
            Logger.Error($"Could not find the method for {detour.Method.Name}; hook was not registered");
            return null;
        }

        try {
            return new Hook(method, detour);
        } catch (Exception e) {
            Logger.Error($"Could not hook the method for {detour.Method.Name}:\n{e}");
            return null;
        }
    }

    /// <summary>
    /// Sends an event of an FSM that the scene host's game sent, so that gates know which FSM closes them.
    /// </summary>
    /// <param name="fsm">The FSM that sends the event.</param>
    /// <param name="eventTarget">The target of the event.</param>
    /// <param name="eventName">The name of the event.</param>
    public static void SendNetworkEvent(Fsm fsm, FsmEventTarget eventTarget, string eventName) {
        var previousSender = _networkSender;
        _networkSender = fsm;
        try {
            fsm.Event(eventTarget, eventName);
        } finally {
            _networkSender = previousSender;
        }
    }

    /// <summary>
    /// Callback method for when another player sends something that happened in a room.
    /// </summary>
    /// <param name="update">The BossRoomUpdate packet data.</param>
    public void OnBossRoomUpdate(BossRoomUpdate update) {
        if (!_isFullSynchronisation()) {
            return;
        }

        // Players hear that a teammate waits for them wherever they are
        if (update.Kind == BossRoomUpdateKind.Waiting) {
            OnTeammateWaiting();
            return;
        }

        if (SceneManager.GetActiveScene().name != update.SceneName) {
            return;
        }

        switch (update.Kind) {
            case BossRoomUpdateKind.Arrived:
                OnArrived(update);
                break;
            case BossRoomUpdateKind.Started:
                _startChecks.Add(new StartCheck(update, Time.unscaledTime));
                break;
            case BossRoomUpdateKind.RoomDone:
                OnRoomDone(update);
                break;
            case BossRoomUpdateKind.GateOpened:
                OnGateOpened(update);
                break;
            case BossRoomUpdateKind.RecordSet:
                OnRecordSet(update);
                break;
            case BossRoomUpdateKind.DialogueStarted:
                OnDialogueStarted(update);
                break;
            case BossRoomUpdateKind.DialogueDone:
                OnDialogueDone(update);
                break;
            case BossRoomUpdateKind.Ready:
                OnReady(update);
                break;
            case BossRoomUpdateKind.RoomEvent:
                OnRoomEvent(update);
                break;
        }
    }

    /// <summary>
    /// Tells a player who entered the local scene which rooms the local player already reached, and shares the dialogue
    /// that is still being read.
    /// </summary>
    public void OnPlayerEnterScene() {
        foreach (var pair in _arrivals) {
            if (pair.Value.Local && pair.Key.GameObject != null) {
                Send(BossRoomUpdateKind.Arrived, pair.Key, "", "", "");
            }
        }

        ShareDialoguesWithNewPlayers();
    }

    /// <summary>
    /// Holds back the start of a room until all players reached it, keeps a gate open while the local player is outside
    /// the room that closes it, and tells other players when a gate of the scene host opens.
    /// </summary>
    private void OnProcessEvent(
        Action<Fsm, FsmEvent, FsmEventData> orig,
        Fsm self,
        FsmEvent fsmEvent,
        FsmEventData eventData
    ) {
        if (_bypassGates || fsmEvent?.Name is not { } eventName) {
            orig(self, fsmEvent!, eventData);
            return;
        }

        if (IsHoldActive() && self.ActiveState is { } activeState &&
            (TryHoldDialogueEnd(self, activeState, eventName) || TryHoldFightGate(self, activeState, eventName) ||
             TryHoldStart(self, activeState, eventName) || TryHoldEventStart(self, activeState, eventName))) {
            return;
        }

        var isClose = CloseEventNames.Contains(eventName);
        if ((!isClose && !OpenEventNames.Contains(eventName)) || !GetInfo(self).IsGate) {
            orig(self, fsmEvent, eventData);
            return;
        }

        if (!isClose) {
            OnGateOpening(self, eventName);
            orig(self, fsmEvent, eventData);
            return;
        }

        // No FSM is on the execution stack while SSMP sends the events of the scene host's entities
        var sender = _networkSender ?? FsmExecutionStack.ExecutingFsm;
        if (!IsCoopActive() || sender == null || sender == self || IsLocalHeroInRoom(sender)) {
            _closedGates[self] = sender;
            RemovePendingClose(self);
            MarkRoomStarted(self);
            orig(self, fsmEvent, eventData);
            return;
        }

        if (AddPendingClose(self, sender, eventName)) {
            Logger.Info($"Gate '{GetPath(self)}' stays open until the local player walks into the room of '{sender.Name}'");
        }
    }

    /// <summary>
    /// Holds back the event of a trigger that starts a room until all players have reached the room.
    /// </summary>
    /// <returns>Whether the event is held back.</returns>
    private bool TryHoldStart(Fsm fsm, FsmState state, string eventName) {
        var trigger = GetStartTrigger(fsm, state, eventName);
        if (trigger == null) {
            return false;
        }

        // Without triggers to check, the room starts like it does alone
        var regions = GetRegions(fsm);
        if (regions.Count == 0) {
            return false;
        }

        // Only the local player sets off collider triggers, while alert ranges also see the other players
        var arrivals = GetArrivals(fsm);
        var heroController = HeroController.instance;
        if (trigger.Kind == TriggerKind.Collider ||
            heroController != null && IsInRegions(regions, heroController.transform.position)) {
            MarkLocalArrival(fsm, arrivals);
        }

        if (HaveAllArrived(fsm, arrivals)) {
            _heldStarts.Remove(fsm);
            return false;
        }

        if (!_heldStarts.TryGetValue(fsm, out var held) || held.StateName != state.Name || held.EventName != eventName) {
            _heldStarts[fsm] = new HeldStart(state.Name, eventName);
            Logger.Info($"Holding back the start of '{GetPath(fsm)}' until all players reached it");
            NotifyWaiting(fsm);
        }

        return true;
    }

    /// <summary>
    /// Remembers that the local player reached the room of an FSM, and tells the other players.
    /// </summary>
    private void MarkLocalArrival(Fsm fsm, RoomArrivals arrivals) {
        if (arrivals.Local) {
            return;
        }

        arrivals.Local = true;
        Send(BossRoomUpdateKind.Arrived, fsm, "", "", "");
        NotifyWaiting(fsm);
    }

    /// <summary>
    /// Tells the local player and the other players that the local player waits in a room, if they do.
    /// </summary>
    private void NotifyWaiting(Fsm fsm) {
        if (_heldStarts.ContainsKey(fsm)) {
            NotifyWaiting(GetArrivals(fsm));
        }
    }

    /// <summary>
    /// Tells the local player and the other players that the local player waits somewhere, if the local player got
    /// there and they weren't told recently.
    /// </summary>
    private void NotifyWaiting(RoomArrivals progress) {
        if (!progress.Local || Time.unscaledTime < progress.NextNoticeTime) {
            return;
        }

        progress.NextNoticeTime = Time.unscaledTime + NoticeInterval;
        UiManager.InternalChatBox.AddMessage(WaitingMessage);
        Send(BossRoomUpdateKind.Waiting, "", "", "", "", "");
    }

    /// <summary>
    /// Whether the local player and every other connected player have reached the room of an FSM. Players in other
    /// scenes haven't.
    /// </summary>
    private bool HaveAllArrived(Fsm fsm, RoomArrivals arrivals) {
        if (!arrivals.Local) {
            return false;
        }

        if (!IsHoldActive()) {
            return true;
        }

        foreach (var playerData in _playerData.Values) {
            if (!playerData.IsInLocalScene) {
                return false;
            }

            if (arrivals.Remote.Contains(playerData.Id)) {
                continue;
            }

            var container = playerData.PlayerContainer;
            if (container == null || !IsInRegions(GetRegions(fsm), container.transform.position)) {
                return false;
            }

            arrivals.Remote.Add(playerData.Id);
        }

        return true;
    }

    /// <summary>
    /// Forgets that a gate is closed or waits to close, and tells the other players if the scene host's gate opened.
    /// </summary>
    private void OnGateOpening(Fsm gate, string eventName) {
        var wasClosed = _closedGates.Remove(gate);
        var wasPending = RemovePendingClose(gate);
        if ((!wasClosed && !wasPending) || !_entityManager.IsSceneHost || !IsCoopActive()) {
            return;
        }

        Logger.Info($"Gate '{GetPath(gate)}' opened, opening it for the other players");
        Send(BossRoomUpdateKind.GateOpened, gate, "", "", eventName);
    }

    /// <summary>
    /// Tells the scene host when a room started for the local player, if another player is the scene host.
    /// </summary>
    private void OnSwitchState(Action<Fsm, FsmState> orig, Fsm self, FsmState toState) {
        var fromState = self.ActiveState;
        orig(self, toState);

        // A boss fight that began doesn't wait for players anymore
        if (toState != null && _netClient.IsConnected) {
            MarkFightBegan(self, toState.Name);
        }

        if (fromState == null || toState == null || fromState == toState || !IsFollower()) {
            return;
        }

        var transition = self.LastTransition;
        if (transition == null || transition.ToState != toState.Name ||
            GetStartTrigger(self, fromState, transition.EventName) == null || !GetInfo(self).IsController) {
            return;
        }

        Send(BossRoomUpdateKind.Started, self, fromState.Name, toState.Name, transition.EventName);
    }

    /// <summary>
    /// Shares a defeat or encounter record that a room writes as a boolean with the other players in the scene.
    /// </summary>
    private void OnSetPlayerDataBool(Action<PlayerData, string, bool> orig, PlayerData self, string boolName, bool value) {
        orig(self, boolName, value);

        if (value && !_applyingRecord) {
            ShareRecord(boolName, FsmExecutionStack.ExecutingFsm);
        }
    }

    /// <summary>
    /// Shares a defeat or encounter record that a room writes as a variable with the other players in the scene.
    /// </summary>
    private void OnSetPlayerDataVariableEnter(Action<SetPlayerDataVariable> orig, SetPlayerDataVariable self) {
        orig(self);

        if (_applyingRecord || GetActionField(self, "VariableName") is not FsmString { Value: { } variableName } ||
            GetActionField(self, "SetValue") is not FsmVar setValue || setValue.RealType != typeof(bool) ||
            setValue.GetValue() is not true) {
            return;
        }

        ShareRecord(variableName, self.Fsm);
    }

    /// <summary>
    /// Shares a record that an FSM of a room or a boss wrote with the other players in the scene, and any other progress
    /// that a boss writes, like an ability it gives. Bosses only run for the scene host, so the other players would never
    /// get that progress otherwise.
    /// </summary>
    private void ShareRecord(string name, Fsm? fsm) {
        if (fsm == null || !IsCoopActive()) {
            return;
        }

        var info = GetInfo(fsm);
        var isRecord = IsSharedRecordName(name) &&
                       (info.IsEntity || info.IsInBossScene || info.IsController || info.StartTriggers.Count > 0);
        if (!isRecord && (!info.IsEntity || IsHeroStateName(name) || !IsBoss(fsm))) {
            return;
        }

        Logger.Info($"Sharing record '{name}' with the other players in the scene");
        Send(BossRoomUpdateKind.RecordSet, "", "", "", "", "", name);
    }

    /// <summary>
    /// Writes a record or other progress that another player in the scene shared.
    /// </summary>
    private void OnRecordSet(BossRoomUpdate update) {
        var playerData = PlayerData.instance;
        if (playerData == null || IsHeroStateName(update.VariableName)) {
            return;
        }

        Logger.Info($"Another player shared record '{update.VariableName}'");
        _applyingRecord = true;
        try {
            playerData.SetBool(update.VariableName, true);
        } finally {
            _applyingRecord = false;
        }
    }

    /// <summary>
    /// Whether a record of the player data is a defeat or encounter record, which all players in a room share.
    /// </summary>
    private static bool IsSharedRecordName(string name) {
        return name.StartsWith("defeated", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("encountered", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("Defeated", StringComparison.Ordinal) ||
               name.EndsWith("Encountered", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a boolean of the player data holds the state of the hero while a boss plays out, like whether they can
    /// pause or take damage, instead of progress. That state belongs to the player whose game set it.
    /// </summary>
    private static bool IsHeroStateName(string name) {
        return name.StartsWith("disable", StringComparison.Ordinal) ||
               name.StartsWith("respawn", StringComparison.Ordinal) ||
               name.StartsWith("hazard", StringComparison.Ordinal) ||
               name.EndsWith("Cooldown", StringComparison.Ordinal) ||
               name is "isInvincible" or "atBench";
    }

    /// <summary>
    /// Whether the object of an FSM is a boss: it is part of a boss scene, or one of its FSMs shows the title of a boss.
    /// </summary>
    private static bool IsBoss(Fsm fsm) {
        var gameObject = fsm.GameObject;
        if (gameObject == null) {
            return false;
        }

        if (IsInBossScene(gameObject.transform)) {
            return true;
        }

        foreach (var playMakerFsm in gameObject.GetComponents<PlayMakerFSM>()) {
            foreach (var state in playMakerFsm.FsmStates ?? []) {
                if ((state.Actions ?? []).Any(action => action?.GetType().Name == BossTitleActionName)) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Remembers that another player reached a room.
    /// </summary>
    private void OnArrived(BossRoomUpdate update) {
        var fsm = FindFsm(update.Path, update.FsmName);
        if (fsm == null) {
            return;
        }

        GetArrivals(fsm).Remote.Add(update.PlayerId);
        if (GetInfo(fsm).StartTriggers.Count > 0) {
            _startFsms.Add(fsm);
        }
    }

    /// <summary>
    /// Tells the local player that a teammate waits for them in a room.
    /// </summary>
    private void OnTeammateWaiting() {
        if (Time.unscaledTime < _nextTeammateNoticeTime) {
            return;
        }

        _nextTeammateNoticeTime = Time.unscaledTime + NoticeInterval;
        UiManager.InternalChatBox.AddMessage(TeammateWaitingMessage);
    }

    /// <summary>
    /// Opens the gates of a room that started for the local player but not for the scene host.
    /// </summary>
    private void OnRoomDone(BossRoomUpdate update) {
        if (_entityManager.IsSceneRoleDetermined && _entityManager.IsSceneHost) {
            return;
        }

        var controller = FindFsm(update.Path, update.FsmName);
        if (controller == null) {
            return;
        }

        _pendingCloses.RemoveAll(pendingClose => pendingClose.Sender == controller);

        var gates = _closedGates.Where(pair => pair.Value == controller).Select(pair => pair.Key).ToList();
        foreach (var gate in gates) {
            Logger.Info($"'{update.Path}' didn't start for the scene host, opening gate '{GetPath(gate)}'");
            gate.Event(OpenGateEventName);
        }
    }

    /// <summary>
    /// Opens a gate that the scene host opened, if it is closed for the local player or waits to close.
    /// </summary>
    private void OnGateOpened(BossRoomUpdate update) {
        if (_entityManager.IsSceneRoleDetermined && _entityManager.IsSceneHost) {
            return;
        }

        var gate = FindFsm(update.Path, update.FsmName);
        if (gate == null || (!_closedGates.ContainsKey(gate) && !HasPendingClose(gate))) {
            return;
        }

        Logger.Info($"The scene host opened gate '{update.Path}'");
        gate.Event(update.EventName);
    }

    /// <summary>
    /// Keeps track of which players reached rooms, starts rooms that all players reached, checks rooms that started for
    /// other players, and closes the gates of rooms that the local player walked into.
    /// </summary>
    private void OnHeroControllerUpdate(HeroController heroController) {
        var position = (Vector2) heroController.transform.position;
        var previousPosition = _lastHeroPosition ?? position;
        _lastHeroPosition = position;

        if (IsHoldActive()) {
            ScanScene();
            UpdateArrivals(previousPosition, position);
        }

        ReleaseHeldStarts();
        UpdateDialogues();
        UpdateEventStarts();
        CheckStartedRooms();
        ClosePendingGates(previousPosition, position);
    }

    /// <summary>
    /// Searches the scene for FSMs with triggers that start rooms, every so often.
    /// </summary>
    private void ScanScene() {
        if (Time.unscaledTime < _nextScanTime) {
            return;
        }

        _nextScanTime = Time.unscaledTime + ScanInterval;
        foreach (var playMakerFsm in Object.FindObjectsByType<PlayMakerFSM>(
                     FindObjectsInactive.Include,
                     FindObjectsSortMode.None
                 )) {
            // FSMs that never woke up haven't loaded their actions yet
            var fsm = playMakerFsm.Fsm;
            if (fsm == null || fsm.Owner == null || !_scannedFsms.Add(fsm)) {
                continue;
            }

            if (GetInfo(fsm).StartTriggers.Count > 0) {
                _startFsms.Add(fsm);
            }

            // Reads boss rooms before their fights start, instead of when the first event arrives
            GetBossRoom(fsm);
        }
    }

    /// <summary>
    /// Remembers which players went through the triggers of rooms since the previous frame.
    /// </summary>
    private void UpdateArrivals(Vector2 previousPosition, Vector2 position) {
        foreach (var fsm in _startFsms) {
            if (fsm.GameObject == null) {
                continue;
            }

            var regions = GetRegions(fsm);
            if (regions.Count == 0) {
                continue;
            }

            var arrivals = GetArrivals(fsm);
            if (!arrivals.Local && IsSegmentInRegions(regions, previousPosition, position)) {
                MarkLocalArrival(fsm, arrivals);
            }

            foreach (var playerData in _playerData.Values) {
                if (!playerData.IsInLocalScene) {
                    arrivals.Remote.Remove(playerData.Id);
                    continue;
                }

                var container = playerData.PlayerContainer;
                if (container == null || arrivals.Remote.Contains(playerData.Id)) {
                    continue;
                }

                var current = (Vector2) container.transform.position;
                var previous = _remotePositions.TryGetValue(playerData.Id, out var last) ? last : current;
                if (IsSegmentInRegions(regions, previous, current)) {
                    arrivals.Remote.Add(playerData.Id);
                }
            }
        }

        foreach (var playerData in _playerData.Values) {
            var container = playerData.PlayerContainer;
            if (playerData.IsInLocalScene && container != null) {
                _remotePositions[playerData.Id] = container.transform.position;
            } else {
                _remotePositions.Remove(playerData.Id);
            }
        }
    }

    /// <summary>
    /// Starts the rooms that were held back once all players reached them.
    /// </summary>
    private void ReleaseHeldStarts() {
        if (_heldStarts.Count == 0) {
            return;
        }

        foreach (var pair in _heldStarts.ToList()) {
            var fsm = pair.Key;
            var held = pair.Value;
            if (fsm.GameObject == null || fsm.ActiveState?.Name != held.StateName) {
                _heldStarts.Remove(fsm);
                continue;
            }

            if (!HaveAllArrived(fsm, GetArrivals(fsm))) {
                continue;
            }

            _heldStarts.Remove(fsm);
            Logger.Info($"All players reached '{GetPath(fsm)}', starting it");
            fsm.Event(held.EventName);
        }
    }

    /// <summary>
    /// Opens the gates of other players whose room started while it didn't start for the scene host, for example because
    /// the scene host already won its fight.
    /// </summary>
    private void CheckStartedRooms() {
        for (var i = _startChecks.Count - 1; i >= 0; i--) {
            var check = _startChecks[i];
            if (Time.unscaledTime - check.ReceivedTime < StartCheckTime) {
                continue;
            }

            _startChecks.RemoveAt(i);
            if (!_entityManager.IsSceneRoleDetermined || !_entityManager.IsSceneHost) {
                continue;
            }

            var update = check.Update;
            var fsm = FindFsm(update.Path, update.FsmName);
            if (fsm != null && fsm.Owner is Behaviour { isActiveAndEnabled: true } &&
                fsm.ActiveState?.Name != update.FromState && (!GetInfo(fsm).ClosesGates || IsFightRunning(fsm))) {
                continue;
            }

            Logger.Info($"'{update.Path}' started for another player, but not for the scene host");
            Send(BossRoomUpdateKind.RoomDone, update.Path, update.FsmName, "", "", "");
        }
    }

    /// <summary>
    /// Closes the gates that wait for the local player once they walk into the room that closed them.
    /// </summary>
    private void ClosePendingGates(Vector2 previousPosition, Vector2 position) {
        foreach (var pendingClose in _pendingCloses.ToList()) {
            if (pendingClose.Gate.GameObject == null) {
                _pendingCloses.Remove(pendingClose);
                continue;
            }

            if (!IsSegmentInRegions(GetRegions(pendingClose.Sender), previousPosition, position)) {
                continue;
            }

            _pendingCloses.Remove(pendingClose);
            _closedGates[pendingClose.Gate] = pendingClose.Sender;
            MarkRoomStarted(pendingClose.Gate);

            Logger.Info(
                $"The local player walked into the room of '{pendingClose.Sender.Name}', closing gate '{GetPath(pendingClose.Gate)}'"
            );
            _bypassGates = true;
            try {
                pendingClose.Gate.Event(pendingClose.EventName);
            } finally {
                _bypassGates = false;
            }
        }
    }

    /// <summary>
    /// Whether the local player is in the room of an FSM that closes gates. Without triggers to check, the gates close
    /// like they do alone.
    /// </summary>
    private bool IsLocalHeroInRoom(Fsm sender) {
        var regions = GetRegions(sender);
        var heroController = HeroController.instance;
        return regions.Count == 0 || heroController == null || IsInRegions(regions, heroController.transform.position);
    }

    /// <summary>
    /// Whether a point is in one of the given triggers.
    /// </summary>
    private static bool IsInRegions(List<Collider2D> regions, Vector2 point) {
        foreach (var region in regions) {
            if (region != null && ContainsPoint(region, point)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a player who moved between two points during a frame went through one of the given triggers. A move that
    /// is too far to walk counts as a teleport, of which only the end counts.
    /// </summary>
    private static bool IsSegmentInRegions(List<Collider2D> regions, Vector2 from, Vector2 to) {
        if (regions.Count == 0) {
            return false;
        }

        var distance = Vector2.Distance(from, to);
        if (distance > MaxSampledDistance) {
            return IsInRegions(regions, to);
        }

        var steps = Mathf.Max(1, Mathf.CeilToInt(distance / SampleSpacing));
        for (var i = 0; i <= steps; i++) {
            if (IsInRegions(regions, Vector2.Lerp(from, to, (float) i / steps))) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the colliders of the triggers that start the room of an FSM, searching them again every so often while
    /// none were found, since some FSMs only find their alert ranges once they run.
    /// </summary>
    private List<Collider2D> GetRegions(Fsm fsm) {
        var info = GetInfo(fsm);
        if (info.StartTriggers.Count == 0 || info.Regions.Count > 0 || Time.unscaledTime < info.NextRegionResolveTime) {
            return info.Regions;
        }

        info.NextRegionResolveTime = Time.unscaledTime + RegionResolveInterval;
        foreach (var trigger in info.StartTriggers) {
            AddRegionColliders(fsm, trigger.Action, trigger.Kind, info.Regions);
        }

        return info.Regions;
    }

    /// <summary>
    /// Gets which players reached the room of an FSM.
    /// </summary>
    private RoomArrivals GetArrivals(Fsm fsm) {
        if (!_arrivals.TryGetValue(fsm, out var arrivals)) {
            arrivals = new RoomArrivals();
            _arrivals[fsm] = arrivals;
        }

        return arrivals;
    }

    /// <summary>
    /// Whether the fight of a room runs for the scene host, because a gate that its FSM closed is still closed or waits
    /// to close.
    /// </summary>
    private bool IsFightRunning(Fsm controller) {
        return _closedGates.ContainsValue(controller) ||
               _pendingCloses.Any(pendingClose => pendingClose.Sender == controller);
    }

    /// <summary>
    /// Remembers that a gate closes once the local player walks into a room.
    /// </summary>
    /// <returns>Whether the gate didn't wait to close yet.</returns>
    private bool AddPendingClose(Fsm gate, Fsm sender, string eventName) {
        var isNew = !RemovePendingClose(gate);
        _pendingCloses.Add(new PendingClose(gate, sender, eventName));
        return isNew;
    }

    /// <summary>
    /// Forgets that a gate waits to close.
    /// </summary>
    /// <returns>Whether the gate waited to close.</returns>
    private bool RemovePendingClose(Fsm gate) {
        return _pendingCloses.RemoveAll(pendingClose => pendingClose.Gate == gate) > 0;
    }

    /// <summary>
    /// Whether a gate waits to close.
    /// </summary>
    private bool HasPendingClose(Fsm gate) {
        return _pendingCloses.Any(pendingClose => pendingClose.Gate == gate);
    }

    /// <summary>
    /// Sends something that happened to an FSM of a room to the other players in the scene.
    /// </summary>
    private void Send(BossRoomUpdateKind kind, Fsm fsm, string fromState, string toState, string eventName) {
        if (fsm.GameObject != null) {
            Send(kind, ScenePath.Get(fsm.GameObject.transform), fsm.Name, fromState, toState, eventName);
        }
    }

    /// <summary>
    /// Sends something that happened in a room to the other players.
    /// </summary>
    private void Send(
        BossRoomUpdateKind kind,
        string path,
        string fsmName,
        string fromState,
        string toState,
        string eventName,
        string variableName = ""
    ) {
        if (!_netClient.IsConnected) {
            return;
        }

        _netClient.UpdateManager.SetBossRoomUpdate(
            new BossRoomUpdate {
                SceneName = SceneManager.GetActiveScene().name,
                Kind = kind,
                Path = path,
                FsmName = fsmName,
                FromState = fromState,
                ToState = toState,
                EventName = eventName,
                VariableName = variableName
            }
        );
    }

    /// <summary>
    /// Whether rooms wait for other players, because other players are connected and the server synchronises
    /// entities.
    /// </summary>
    private bool IsHoldActive() {
        return _playerData.Count > 0 && _netClient.IsConnected && _isFullSynchronisation();
    }

    /// <summary>
    /// Whether another player shares the local scene while the server synchronises entities.
    /// </summary>
    private bool IsCoopActive() {
        return _netClient.IsConnected && _isFullSynchronisation() && IsOtherPlayerInScene();
    }

    /// <summary>
    /// Whether the local player follows the scene host, because another player in the scene controls the bosses.
    /// </summary>
    private bool IsFollower() {
        return IsCoopActive() && _entityManager.IsSceneRoleDetermined && !_entityManager.IsSceneHost;
    }

    /// <summary>
    /// Whether another player is in the local scene.
    /// </summary>
    private bool IsOtherPlayerInScene() {
        foreach (var playerData in _playerData.Values) {
            if (playerData.IsInLocalScene) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets what this class knows about an FSM, finding it out the first time.
    /// </summary>
    private FsmInfo GetInfo(Fsm fsm) {
        if (_fsmInfos.TryGetValue(fsm, out var info)) {
            return info;
        }

        try {
            info = BuildInfo(fsm);
        } catch (Exception e) {
            Logger.Warn($"Could not read FSM '{fsm.Name}' for boss room co-op:\n{e}");
            info = new FsmInfo();
        }

        _fsmInfos[fsm] = info;
        return info;
    }

    /// <summary>
    /// Finds out whether an FSM is a gate or controls a room, and which of its triggers start the room.
    /// </summary>
    private static FsmInfo BuildInfo(Fsm fsm) {
        var info = new FsmInfo();
        var gameObject = fsm.GameObject;
        if (gameObject == null) {
            return info;
        }

        var states = fsm.States ?? [];
        var globalTransitions = fsm.GlobalTransitions ?? [];

        var hasCloseTransition = false;
        var hasOpenTransition = false;
        foreach (var transition in globalTransitions.Concat(states.SelectMany(state => state.Transitions ?? []))) {
            hasCloseTransition |= CloseEventNames.Contains(transition.EventName ?? "");
            hasOpenTransition |= OpenEventNames.Contains(transition.EventName ?? "");
        }

        var statesByName = new Dictionary<string, FsmState>();
        var closingStates = new HashSet<string>();
        foreach (var state in states) {
            statesByName[state.Name] = state;
            foreach (var action in state.Actions ?? []) {
                var sentEvent = GetSentEventName(action);
                if (sentEvent != null && CloseEventNames.Contains(sentEvent)) {
                    closingStates.Add(state.Name);
                }
            }
        }

        var isInArena = gameObject.GetComponentInParent<BattleScene>(true) != null;
        var isBossScene = gameObject.name.StartsWith(BossSceneNamePrefix, StringComparison.Ordinal);
        info.IsEntity = EntityRegistry.TryGetEntry(gameObject, out _);
        info.IsInBossScene = IsInBossScene(gameObject.transform);
        info.ClosesGates = closingStates.Count > 0;
        info.IsGate = !isInArena && hasCloseTransition && hasOpenTransition;
        info.IsController = !isInArena && !info.IsEntity && (info.ClosesGates || isBossScene);
        if (isInArena) {
            return info;
        }

        // Boss scenes without gates start their room with the first trigger that a player walks into
        var preFightStates = isBossScene && !info.IsEntity ? GetPreFightStates(fsm, statesByName) : [];
        foreach (var state in states) {
            foreach (var action in state.Actions ?? []) {
                var kind = GetTriggerKind(action);
                if (kind == TriggerKind.None || GetTriggerEventName(action, kind) is not { } eventName) {
                    continue;
                }

                if (kind == TriggerKind.Collider && preFightStates.Contains(state.Name) ||
                    info.ClosesGates &&
                    LeadsToStates(state, eventName, globalTransitions, statesByName, closingStates)) {
                    info.StartTriggers.Add(new StartTrigger(state.Name, eventName, action, kind));
                }
            }
        }

        return info;
    }

    /// <summary>
    /// Whether an object or one of its parents is a boss scene.
    /// </summary>
    private static bool IsInBossScene(Transform transform) {
        for (var current = transform; current != null; current = current.parent) {
            if (current.name.StartsWith(BossSceneNamePrefix, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the states that an FSM reaches from its start state before a player walks into one of its triggers.
    /// </summary>
    private static HashSet<string> GetPreFightStates(Fsm fsm, Dictionary<string, FsmState> statesByName) {
        var preFightStates = new HashSet<string>();
        if (fsm.StartState is not { Length: > 0 } startState || !statesByName.ContainsKey(startState)) {
            return preFightStates;
        }

        var queue = new Queue<string>();
        preFightStates.Add(startState);
        queue.Enqueue(startState);
        while (queue.Count > 0) {
            var state = statesByName[queue.Dequeue()];
            var triggerEvents = new HashSet<string>();
            foreach (var action in state.Actions ?? []) {
                var kind = GetTriggerKind(action);
                if (kind != TriggerKind.None && GetTriggerEventName(action, kind) is { } eventName) {
                    triggerEvents.Add(eventName);
                }
            }

            foreach (var transition in state.Transitions ?? []) {
                if (transition.ToState is not { } toState || triggerEvents.Contains(transition.EventName ?? "") ||
                    !statesByName.ContainsKey(toState) || !preFightStates.Add(toState)) {
                    continue;
                }

                queue.Enqueue(toState);
            }
        }

        return preFightStates;
    }

    /// <summary>
    /// Whether an event in a state leads to one of the given states within a few transitions.
    /// </summary>
    private static bool LeadsToStates(
        FsmState state,
        string eventName,
        FsmTransition[] globalTransitions,
        Dictionary<string, FsmState> statesByName,
        HashSet<string> targetStates
    ) {
        var seen = new HashSet<string>();
        var queue = new Queue<(string name, int steps)>();
        foreach (var transition in (state.Transitions ?? []).Concat(globalTransitions)) {
            if (transition.EventName == eventName && transition.ToState != null && seen.Add(transition.ToState)) {
                queue.Enqueue((transition.ToState, 1));
            }
        }

        while (queue.Count > 0) {
            var (name, steps) = queue.Dequeue();
            if (targetStates.Contains(name)) {
                return true;
            }

            if (steps >= MaxCloseSteps || !statesByName.TryGetValue(name, out var nextState)) {
                continue;
            }

            foreach (var transition in nextState.Transitions ?? []) {
                if (transition.ToState != null && seen.Add(transition.ToState)) {
                    queue.Enqueue((transition.ToState, steps + 1));
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the trigger of an FSM that starts its room when it sends the given event in the given state, or null.
    /// </summary>
    private StartTrigger? GetStartTrigger(Fsm fsm, FsmState state, string? eventName) {
        // Most events of most FSMs don't come from triggers, which is cheaper to check than the whole FSM
        if (eventName is not { Length: > 0 } || !HasTriggerEvent(state, eventName)) {
            return null;
        }

        foreach (var trigger in GetInfo(fsm).StartTriggers) {
            if (trigger.StateName == state.Name && trigger.EventName == eventName) {
                return trigger;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a state has an action that sends the given event when a player reaches its trigger.
    /// </summary>
    private static bool HasTriggerEvent(FsmState state, string eventName) {
        foreach (var action in state.Actions ?? []) {
            var kind = GetTriggerKind(action);
            if (kind != TriggerKind.None && GetTriggerEventName(action, kind) == eventName) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Adds the colliders of the trigger of an action to the given regions.
    /// </summary>
    private static void AddRegionColliders(Fsm fsm, FsmStateAction action, TriggerKind kind, List<Collider2D> regions) {
        var regionObject = kind switch {
            TriggerKind.Collider => GetActionField(action, "gameObject") is FsmOwnerDefault owner
                ? fsm.GetOwnerDefaultTarget(owner)
                : fsm.GameObject,
            TriggerKind.AlertRangeByName => FindAlertRange(
                fsm.GameObject,
                GetActionField(action, "alertRangeName") as string
            )?.gameObject,
            TriggerKind.AlertRange => ((GetActionField(action, "alertRange") as FsmObject)?.Value as AlertRange)
                ?.gameObject,
            _ => null
        };
        if (regionObject == null) {
            return;
        }

        var colliders = regionObject.GetComponents<Collider2D>();
        var hasTrigger = colliders.Any(collider => collider.isTrigger);
        foreach (var collider in colliders) {
            if ((collider.isTrigger || !hasTrigger) && !regions.Contains(collider)) {
                regions.Add(collider);
            }
        }
    }

    /// <summary>
    /// Finds an alert range among the children of an object, like <c>AlertRange.Find</c>.
    /// </summary>
    private static AlertRange? FindAlertRange(GameObject? parent, string? name) {
        if (parent == null) {
            return null;
        }

        foreach (Transform child in parent.transform) {
            var alertRange = child.GetComponent<AlertRange>();
            if (alertRange != null && (string.IsNullOrEmpty(name) || child.name == name)) {
                return alertRange;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the event that a trigger action sends when a player reaches its trigger.
    /// </summary>
    private static string? GetTriggerEventName(FsmStateAction action, TriggerKind kind) {
        return GetEventName(GetActionField(action, kind == TriggerKind.AlertRange ? "InRangeEvent" : "sendEvent"));
    }

    /// <summary>
    /// Gets the name of the event that an action sends to other FSMs, or null if it doesn't send one.
    /// </summary>
    private static string? GetSentEventName(FsmStateAction? action) {
        return action switch {
            null => null,
            SendEventByName sendEventByName => sendEventByName.sendEvent?.Value,
            SendEventByNameV2 sendEventByNameV2 => sendEventByNameV2.sendEvent?.Value,
            _ => action.GetType().Name.StartsWith("SendEventToRegister", StringComparison.Ordinal)
                ? GetEventName(GetActionField(action, "eventName"))
                : null
        };
    }

    /// <summary>
    /// Gets the kind of trigger that an action checks.
    /// </summary>
    private static TriggerKind GetTriggerKind(FsmStateAction? action) {
        if (action == null) {
            return TriggerKind.None;
        }

        var type = action.GetType();
        if (!TriggerKinds.TryGetValue(type, out var kind)) {
            var name = type.Name;
            kind = name.StartsWith("Trigger2dEvent", StringComparison.Ordinal) ||
                   name.StartsWith("SendTrigger2DEvent", StringComparison.Ordinal)
                ? TriggerKind.Collider
                : name switch {
                    "CheckAlertRangeByName" => TriggerKind.AlertRangeByName,
                    "CheckAlertRange" => TriggerKind.AlertRange,
                    _ => TriggerKind.None
                };
            TriggerKinds[type] = kind;
        }

        return kind;
    }

    /// <summary>
    /// Gets the value of a field of an action by name, or null if the action has no such field.
    /// </summary>
    private static object? GetActionField(FsmStateAction action, string fieldName) {
        var key = (action.GetType(), fieldName);
        if (!ActionFields.TryGetValue(key, out var field)) {
            field = action.GetType().GetField(fieldName, InstanceFlags);
            ActionFields[key] = field;
        }

        return field?.GetValue(action);
    }

    /// <summary>
    /// Gets the name of an event from a field that holds an event or its name.
    /// </summary>
    private static string? GetEventName(object? value) {
        return value switch {
            FsmEvent fsmEvent => fsmEvent.Name,
            FsmString fsmString => fsmString.Value,
            string name => name,
            _ => null
        };
    }

    /// <summary>
    /// Whether a collider contains a point. Inactive colliders are checked by their shape, since physics ignores them.
    /// </summary>
    private static bool ContainsPoint(Collider2D collider, Vector2 point) {
        if (collider.enabled && collider.gameObject.activeInHierarchy) {
            return collider.OverlapPoint(point);
        }

        var local = (Vector2) collider.transform.InverseTransformPoint(point) - collider.offset;
        switch (collider) {
            case BoxCollider2D box:
                return Mathf.Abs(local.x) <= box.size.x / 2f && Mathf.Abs(local.y) <= box.size.y / 2f;
            case CapsuleCollider2D capsule:
                return Mathf.Abs(local.x) <= capsule.size.x / 2f && Mathf.Abs(local.y) <= capsule.size.y / 2f;
            case CircleCollider2D circle:
                return local.sqrMagnitude <= circle.radius * circle.radius;
            case PolygonCollider2D polygon:
                for (var i = 0; i < polygon.pathCount; i++) {
                    if (IsInPolygon(polygon.GetPath(i), local)) {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Whether a point is inside a polygon, by counting how many of its edges a ray from the point crosses.
    /// </summary>
    private static bool IsInPolygon(Vector2[] points, Vector2 point) {
        var inside = false;
        for (int i = 0, j = points.Length - 1; i < points.Length; j = i++) {
            if (points[i].y > point.y != points[j].y > point.y &&
                point.x < (points[j].x - points[i].x) * (point.y - points[i].y) / (points[j].y - points[i].y) +
                points[i].x) {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// Finds the FSM with the given name on the object with the given path.
    /// </summary>
    private static Fsm? FindFsm(string path, string fsmName) {
        var gameObject = ScenePath.Find(path);
        if (gameObject == null) {
            return null;
        }

        foreach (var playMakerFsm in gameObject.GetComponents<PlayMakerFSM>()) {
            if (playMakerFsm.FsmName == fsmName) {
                return playMakerFsm.Fsm;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the path of the object of an FSM, for logging.
    /// </summary>
    private static string GetPath(Fsm fsm) {
        return fsm.GameObject == null ? fsm.Name : ScenePath.Get(fsm.GameObject.transform);
    }

    /// <summary>
    /// Forgets the rooms of the previous scene.
    /// </summary>
    private void OnActiveSceneChanged(Scene oldScene, Scene newScene) {
        ClearScene();
    }

    /// <summary>
    /// Forgets everything about the rooms of the current scene.
    /// </summary>
    private void ClearScene() {
        _fsmInfos.Clear();
        _scannedFsms.Clear();
        _startFsms.Clear();
        _arrivals.Clear();
        _heldStarts.Clear();
        _closedGates.Clear();
        _pendingCloses.Clear();
        _startChecks.Clear();
        _remotePositions.Clear();
        ClearDialogues();
        ClearEventStarts();
        _lastHeroPosition = null;
        _nextScanTime = 0f;
    }

    /// <summary>
    /// The kinds of triggers that start rooms.
    /// </summary>
    private enum TriggerKind {
        None,
        Collider,
        AlertRangeByName,
        AlertRange
    }

    /// <summary>
    /// A trigger action of an FSM that starts its room.
    /// </summary>
    private class StartTrigger {
        /// <summary>
        /// The name of the state that the action is in.
        /// </summary>
        public readonly string StateName;

        /// <summary>
        /// The event that the action sends when a player reaches its trigger.
        /// </summary>
        public readonly string EventName;

        /// <summary>
        /// The trigger action.
        /// </summary>
        public readonly FsmStateAction Action;

        /// <summary>
        /// The kind of trigger that the action checks.
        /// </summary>
        public readonly TriggerKind Kind;

        public StartTrigger(string stateName, string eventName, FsmStateAction action, TriggerKind kind) {
            StateName = stateName;
            EventName = eventName;
            Action = action;
            Kind = kind;
        }
    }

    /// <summary>
    /// What this class knows about an FSM.
    /// </summary>
    private class FsmInfo {
        /// <summary>
        /// Whether the FSM is a gate that closes and opens with events, outside arenas.
        /// </summary>
        public bool IsGate;

        /// <summary>
        /// Whether the FSM belongs to an entity, which only runs for the scene host.
        /// </summary>
        public bool IsEntity;

        /// <summary>
        /// Whether the object of the FSM is a boss scene or part of one.
        /// </summary>
        public bool IsInBossScene;

        /// <summary>
        /// Whether the FSM sends events that close gates.
        /// </summary>
        public bool ClosesGates;

        /// <summary>
        /// Whether the FSM controls a room outside arenas: it closes gates or runs a boss scene, and isn't an entity.
        /// </summary>
        public bool IsController;

        /// <summary>
        /// The trigger actions of the FSM that start its room.
        /// </summary>
        public readonly List<StartTrigger> StartTriggers = [];

        /// <summary>
        /// The colliders of the triggers that start the room of the FSM, once they are found.
        /// </summary>
        public readonly List<Collider2D> Regions = [];

        /// <summary>
        /// When the colliders of the triggers are searched again if none were found, in unscaled seconds.
        /// </summary>
        public float NextRegionResolveTime;
    }

    /// <summary>
    /// The players who reached a room.
    /// </summary>
    private class RoomArrivals {
        /// <summary>
        /// Whether the local player reached the room.
        /// </summary>
        public bool Local;

        /// <summary>
        /// The IDs of the other players who reached the room.
        /// </summary>
        public readonly HashSet<ushort> Remote = [];

        /// <summary>
        /// When the players can hear again that the local player waits in the room, in unscaled seconds.
        /// </summary>
        public float NextNoticeTime;
    }

    /// <summary>
    /// The start of a room that is held back until all players reached it.
    /// </summary>
    private class HeldStart {
        /// <summary>
        /// The state of the FSM that waits for the event.
        /// </summary>
        public readonly string StateName;

        /// <summary>
        /// The event that starts the room.
        /// </summary>
        public readonly string EventName;

        public HeldStart(string stateName, string eventName) {
            StateName = stateName;
            EventName = eventName;
        }
    }

    /// <summary>
    /// A gate that closes once the local player walks into a room.
    /// </summary>
    private class PendingClose {
        /// <summary>
        /// The FSM of the gate.
        /// </summary>
        public readonly Fsm Gate;

        /// <summary>
        /// The FSM that closed the gate, whose triggers the local player has to walk into.
        /// </summary>
        public readonly Fsm Sender;

        /// <summary>
        /// The event that closes the gate.
        /// </summary>
        public readonly string EventName;

        public PendingClose(Fsm gate, Fsm sender, string eventName) {
            Gate = gate;
            Sender = sender;
            EventName = eventName;
        }
    }

    /// <summary>
    /// A room that started for another player, which the scene host checks started for it too.
    /// </summary>
    private class StartCheck {
        /// <summary>
        /// The update about the room that started.
        /// </summary>
        public readonly BossRoomUpdate Update;

        /// <summary>
        /// When the update arrived, in unscaled seconds.
        /// </summary>
        public readonly float ReceivedTime;

        public StartCheck(BossRoomUpdate update, float receivedTime) {
            Update = update;
            ReceivedTime = receivedTime;
        }
    }
}
