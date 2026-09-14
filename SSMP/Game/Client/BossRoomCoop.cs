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
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Co-op rules for rooms whose gates are closed by PlayMaker FSMs, which includes most boss rooms. Gates of arenas with
/// a <see cref="BattleScene"/> are left to <see cref="ArenaCoop"/>.
/// Gates only close for the players in the room. When an FSM closes gates while the local player is outside the
/// triggers that lead to it, for example because another player walked in, the gates stay open for the local player
/// and close once they walk into one of those triggers.
/// The scene host controls the bosses, so when another player walks into the trigger of a room, the scene host moves
/// its FSM of the room the same way. If the fight can't start for the scene host, for example because it already won
/// it, the gates that the room closed for the other players open again. When a gate of the scene host opens after it
/// had closed, it opens for the other players too.
/// </summary>
internal class BossRoomCoop {
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
    /// How long, in seconds, the scene host waits for its FSM to reach the state that another player's FSM left.
    /// </summary>
    private const float TransitionWaitTime = 5f;

    /// <summary>
    /// How long, in seconds, a room may take to close its gates after the scene host followed another player into it.
    /// </summary>
    private const float FightStartTime = 10f;

    /// <summary>
    /// The event that opens a gate for a player whose room can't start.
    /// </summary>
    private const string OpenGateEventName = "BG OPEN";

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
    /// The gates that are closed for the local player, with the FSM that closed each of them if it is known.
    /// </summary>
    private readonly Dictionary<Fsm, Fsm?> _closedGates = new();

    /// <summary>
    /// The gates that another player's room closed, which close for the local player once they walk in.
    /// </summary>
    private readonly List<PendingClose> _pendingCloses = [];

    /// <summary>
    /// Transitions of other players that the scene host waits to follow.
    /// </summary>
    private readonly List<QueuedTransition> _queuedTransitions = [];

    /// <summary>
    /// Rooms that the scene host followed another player into, to check whether their fight started.
    /// </summary>
    private readonly List<FollowCheck> _followChecks = [];

    /// <summary>
    /// Hook for keeping gates open for players outside their room.
    /// </summary>
    private Hook? _processEventHook;

    /// <summary>
    /// Hook for telling the scene host when the local player walks into a room.
    /// </summary>
    private Hook? _switchStateHook;

    /// <summary>
    /// Whether this class sends an event to a gate itself, which the hooks let through.
    /// </summary>
    private bool _bypassGates;

    /// <summary>
    /// The FSM that this class moves for another player, or null.
    /// </summary>
    private Fsm? _followingFsm;

    /// <summary>
    /// The position of the local player in the previous frame, or null.
    /// </summary>
    private Vector2? _lastHeroPosition;

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
    /// Callback method for when another player in the scene sends something that happened in a room.
    /// </summary>
    /// <param name="update">The BossRoomUpdate packet data.</param>
    public void OnBossRoomUpdate(BossRoomUpdate update) {
        if (!_isFullSynchronisation() || SceneManager.GetActiveScene().name != update.SceneName) {
            return;
        }

        switch (update.Kind) {
            case BossRoomUpdateKind.Transition:
                if (!TryFollowTransition(update, false)) {
                    _queuedTransitions.Add(new QueuedTransition(update, Time.unscaledTime));
                }

                break;
            case BossRoomUpdateKind.RoomDone:
                OnRoomDone(update);
                break;
            case BossRoomUpdateKind.GateOpened:
                OnGateOpened(update);
                break;
        }
    }

    /// <summary>
    /// Keeps a gate open while the local player is outside the room that closes it, and tells other players when a gate
    /// of the scene host opens.
    /// </summary>
    private void OnProcessEvent(
        Action<Fsm, FsmEvent, FsmEventData> orig,
        Fsm self,
        FsmEvent fsmEvent,
        FsmEventData eventData
    ) {
        var eventName = fsmEvent?.Name;
        if (_bypassGates || eventName == null) {
            orig(self, fsmEvent!, eventData);
            return;
        }

        var isClose = CloseEventNames.Contains(eventName);
        if ((!isClose && !OpenEventNames.Contains(eventName)) || !GetInfo(self).IsGate) {
            orig(self, fsmEvent!, eventData);
            return;
        }

        if (!isClose) {
            OnGateOpening(self, eventName);
            orig(self, fsmEvent!, eventData);
            return;
        }

        // No FSM is on the execution stack while SSMP sends the events of the scene host's entities, or while this class
        // moves an FSM for another player
        var sender = _networkSender ?? FsmExecutionStack.ExecutingFsm ?? _followingFsm;
        if (!IsCoopActive() || sender == null || sender == self || IsLocalHeroInRoom(sender)) {
            _closedGates[self] = sender;
            RemovePendingClose(self);
            orig(self, fsmEvent!, eventData);
            return;
        }

        if (AddPendingClose(self, sender, eventName)) {
            Logger.Info($"Gate '{GetPath(self)}' stays open until the local player walks into the room of '{sender.Name}'");
        }
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
    /// Tells the scene host when the local player walks into the trigger of a room, if another player is the scene
    /// host.
    /// </summary>
    private void OnSwitchState(Action<Fsm, FsmState> orig, Fsm self, FsmState toState) {
        var fromState = self.ActiveState;
        orig(self, toState);

        if (fromState == null || toState == null || fromState == toState || self == _followingFsm || !IsFollower()) {
            return;
        }

        // Only a transition on the event of a trigger means that the local player walked into it. Triggers that check
        // alert ranges are left alone, because alert ranges of the scene host already see the other players
        var transition = self.LastTransition;
        if (transition == null || transition.ToState != toState.Name ||
            !IsColliderTriggerEvent(fromState, transition.EventName) || !GetInfo(self).IsController) {
            return;
        }

        Logger.Info(
            $"Asking the scene host to move '{self.Name}' of '{GetPath(self)}' from '{fromState.Name}' to '{toState.Name}'"
        );
        Send(BossRoomUpdateKind.Transition, self, fromState.Name, toState.Name, transition.EventName);
    }

    /// <summary>
    /// Moves the scene host's FSM of a room the way another player's FSM moved when they walked in.
    /// </summary>
    /// <param name="update">The transition of the other player's FSM.</param>
    /// <param name="giveUp">Whether the FSM had time to reach the state, so that waiting longer won't help.</param>
    /// <returns>Whether the transition is done with, or false to try again later.</returns>
    private bool TryFollowTransition(BossRoomUpdate update, bool giveUp) {
        if (SceneManager.GetActiveScene().name != update.SceneName) {
            return true;
        }

        // Keep the transition until it is known whether the local player is the scene host
        if (!_entityManager.IsSceneRoleDetermined) {
            return giveUp;
        }

        if (!_entityManager.IsSceneHost) {
            return true;
        }

        var fsm = FindFsm(update.Path, update.FsmName);
        if (fsm != null && (fsm.Owner is not Behaviour { isActiveAndEnabled: true } || !GetInfo(fsm).IsController)) {
            fsm = null;
        }

        var activeState = fsm?.ActiveState;
        if (fsm != null && activeState != null && activeState.Name == update.FromState) {
            Logger.Info($"Another player walked into '{update.Path}', moving '{fsm.Name}' to '{update.ToState}'");
            FollowTransition(fsm, update, followed => followed.SetState(update.ToState));
            return true;
        }

        if (!giveUp) {
            return false;
        }

        // The FSM may wait for the same trigger in another state, for example because the save of the scene host took
        // another branch
        if (fsm != null && activeState != null && IsColliderTriggerEvent(activeState, update.EventName)) {
            Logger.Info($"Another player walked into '{update.Path}', sending '{update.EventName}' to '{fsm.Name}'");
            FollowTransition(fsm, update, followed => followed.Event(update.EventName));
            return true;
        }

        if (fsm == null || !IsFightRunning(fsm)) {
            Logger.Info($"Another player walked into '{update.Path}', but its fight can't start for the scene host");
            Send(BossRoomUpdateKind.RoomDone, update.Path, update.FsmName, "", "", "");
        }

        return true;
    }

    /// <summary>
    /// Moves an FSM for another player, and remembers to check whether its fight started.
    /// </summary>
    private void FollowTransition(Fsm fsm, BossRoomUpdate update, Action<Fsm> move) {
        _followingFsm = fsm;
        try {
            move(fsm);
        } catch (Exception e) {
            Logger.Error($"Could not move '{fsm.Name}' for another player:\n{e}");
        } finally {
            _followingFsm = null;
        }

        _followChecks.Add(new FollowCheck(fsm, update, Time.unscaledTime));
    }

    /// <summary>
    /// Opens the gates of a room that the scene host can't start.
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
            Logger.Info($"The fight of '{update.Path}' can't start for the scene host, opening gate '{GetPath(gate)}'");
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
    /// Follows queued transitions, checks rooms that the scene host followed, and closes the gates of rooms that the
    /// local player walked into.
    /// </summary>
    private void OnHeroControllerUpdate(HeroController heroController) {
        var position = (Vector2) heroController.transform.position;
        var previousPosition = _lastHeroPosition ?? position;
        _lastHeroPosition = position;

        for (var i = _queuedTransitions.Count - 1; i >= 0; i--) {
            var queued = _queuedTransitions[i];
            var giveUp = Time.unscaledTime - queued.ReceivedTime > TransitionWaitTime;
            if (TryFollowTransition(queued.Update, giveUp)) {
                _queuedTransitions.RemoveAt(i);
            }
        }

        for (var i = _followChecks.Count - 1; i >= 0; i--) {
            var check = _followChecks[i];
            if (Time.unscaledTime - check.StartTime < FightStartTime) {
                continue;
            }

            _followChecks.RemoveAt(i);
            if (check.Fsm.GameObject != null && (!GetInfo(check.Fsm).ClosesGates || IsFightRunning(check.Fsm))) {
                continue;
            }

            Logger.Info($"The fight of '{check.Update.Path}' did not start for the scene host");
            Send(BossRoomUpdateKind.RoomDone, check.Update.Path, check.Update.FsmName, "", "", "");
        }

        foreach (var pendingClose in _pendingCloses.ToList()) {
            if (pendingClose.Gate.GameObject == null) {
                _pendingCloses.Remove(pendingClose);
                continue;
            }

            // Check where the player was during the frame too, so that dashing through a thin trigger still counts
            var middle = (previousPosition + position) / 2f;
            if (!IsInRegions(pendingClose.Sender, position) && !IsInRegions(pendingClose.Sender, middle) &&
                !IsInRegions(pendingClose.Sender, previousPosition)) {
                continue;
            }

            _pendingCloses.Remove(pendingClose);
            _closedGates[pendingClose.Gate] = pendingClose.Sender;

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
        var heroController = HeroController.instance;
        return GetInfo(sender).Regions.Count == 0 || heroController == null ||
               IsInRegions(sender, heroController.transform.position);
    }

    /// <summary>
    /// Whether a point is in one of the triggers that make an FSM close gates.
    /// </summary>
    private bool IsInRegions(Fsm fsm, Vector2 point) {
        foreach (var region in GetInfo(fsm).Regions) {
            if (region != null && ContainsPoint(region, point)) {
                return true;
            }
        }

        return false;
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
    /// Sends something that happened in a room to the other players in the scene.
    /// </summary>
    private void Send(
        BossRoomUpdateKind kind,
        string path,
        string fsmName,
        string fromState,
        string toState,
        string eventName
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
                EventName = eventName
            }
        );
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
        if (!_fsmInfos.TryGetValue(fsm, out var info)) {
            info = BuildInfo(fsm);
            _fsmInfos[fsm] = info;
        }

        return info;
    }

    /// <summary>
    /// Finds out whether an FSM is a gate or controls a room, and which triggers make it close gates.
    /// </summary>
    private static FsmInfo BuildInfo(Fsm fsm) {
        var info = new FsmInfo();
        var gameObject = fsm.GameObject;
        var states = fsm.States ?? [];
        var globalTransitions = fsm.GlobalTransitions ?? [];

        var hasCloseTransition = false;
        var hasOpenTransition = false;
        foreach (var transition in globalTransitions.Concat(states.SelectMany(state => state.Transitions ?? []))) {
            hasCloseTransition |= CloseEventNames.Contains(transition.EventName ?? "");
            hasOpenTransition |= OpenEventNames.Contains(transition.EventName ?? "");
        }

        var closingStates = new HashSet<string>();
        var statesByName = new Dictionary<string, FsmState>();
        foreach (var state in states) {
            statesByName[state.Name] = state;
            foreach (var action in state.Actions ?? []) {
                var sentEvent = GetSentEventName(action);
                if (sentEvent != null && CloseEventNames.Contains(sentEvent)) {
                    closingStates.Add(state.Name);
                }
            }
        }

        info.ClosesGates = closingStates.Count > 0;
        if (info.ClosesGates) {
            foreach (var state in states) {
                foreach (var action in state.Actions ?? []) {
                    var kind = GetTriggerKind(action);
                    if (kind == TriggerKind.None) {
                        continue;
                    }

                    var eventName = GetEventName(
                        GetActionField(action, kind == TriggerKind.AlertRange ? "InRangeEvent" : "sendEvent")
                    );
                    if (eventName != null &&
                        LeadsToStates(state, eventName, globalTransitions, statesByName, closingStates)) {
                        AddRegionColliders(fsm, action, kind, info.Regions);
                    }
                }
            }
        }

        var isInArena = gameObject != null && gameObject.GetComponentInParent<BattleScene>(true) != null;
        info.IsGate = gameObject != null && !isInArena && hasCloseTransition && hasOpenTransition;
        info.IsController = gameObject != null && !isInArena &&
                            (info.ClosesGates ||
                             gameObject.name.StartsWith(BossSceneNamePrefix, StringComparison.Ordinal)) &&
                            !EntityRegistry.TryGetEntry(gameObject, out _);
        return info;
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
    /// Adds the colliders of the trigger of an action to the regions of an FSM.
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
    /// Whether a state has an action that sends the given event when a player walks into its collider.
    /// </summary>
    private static bool IsColliderTriggerEvent(FsmState state, string? eventName) {
        if (string.IsNullOrEmpty(eventName)) {
            return false;
        }

        foreach (var action in state.Actions ?? []) {
            if (GetTriggerKind(action) == TriggerKind.Collider &&
                GetEventName(GetActionField(action, "sendEvent")) == eventName) {
                return true;
            }
        }

        return false;
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
        _closedGates.Clear();
        _pendingCloses.Clear();
        _queuedTransitions.Clear();
        _followChecks.Clear();
        _lastHeroPosition = null;
    }

    /// <summary>
    /// The kinds of triggers that make FSMs close gates.
    /// </summary>
    private enum TriggerKind {
        None,
        Collider,
        AlertRangeByName,
        AlertRange
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
        /// Whether the FSM sends events that close gates.
        /// </summary>
        public bool ClosesGates;

        /// <summary>
        /// Whether the FSM controls a room outside arenas: it closes gates or runs a boss scene, and isn't an entity.
        /// </summary>
        public bool IsController;

        /// <summary>
        /// The colliders of the triggers that make the FSM close gates.
        /// </summary>
        public readonly List<Collider2D> Regions = [];
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
    /// A transition of another player's FSM that the scene host waits to follow.
    /// </summary>
    private class QueuedTransition {
        /// <summary>
        /// The transition.
        /// </summary>
        public readonly BossRoomUpdate Update;

        /// <summary>
        /// When the transition arrived, in unscaled seconds.
        /// </summary>
        public readonly float ReceivedTime;

        public QueuedTransition(BossRoomUpdate update, float receivedTime) {
            Update = update;
            ReceivedTime = receivedTime;
        }
    }

    /// <summary>
    /// A room that the scene host followed another player into.
    /// </summary>
    private class FollowCheck {
        /// <summary>
        /// The FSM of the room that the scene host moved.
        /// </summary>
        public readonly Fsm Fsm;

        /// <summary>
        /// The transition that the scene host followed.
        /// </summary>
        public readonly BossRoomUpdate Update;

        /// <summary>
        /// When the scene host followed the transition, in unscaled seconds.
        /// </summary>
        public readonly float StartTime;

        public FollowCheck(Fsm fsm, BossRoomUpdate update, float startTime) {
            Fsm = fsm;
            Update = update;
            StartTime = startTime;
        }
    }
}
