using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using SSMP.Ui;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Rules for boss rooms whose fight starts from an event instead of a trigger, like talking, challenging the boss, or a
/// signal from another object: the event waits until every player is in the room. The room is where the camera locks
/// inside the boss scene, bounded by the gates of the room. Events that other players' objects send to bosses are
/// passed on to the scene host, since bosses only move for the scene host.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// How many steps that happen by themselves may lie between an event and the start of a fight.
    /// </summary>
    private const int MaxEventStartSteps = 8;

    /// <summary>
    /// How far, in units, other players may be from where a fight starts in a room without camera locks or gates.
    /// </summary>
    private const float FallbackRoomRadius = 15f;

    /// <summary>
    /// How far, in units, other players may be from where a fight starts in a room with gates but without camera locks.
    /// </summary>
    private const float GateRoomRadius = 60f;

    /// <summary>
    /// How often, in seconds, a boss room is read again when objects in it woke up.
    /// </summary>
    private const float RoomAnalysisInterval = 1f;

    /// <summary>
    /// How often, in seconds, the same event of a boss room may be passed on to the scene host.
    /// </summary>
    private const float ForwardInterval = 0.5f;

    /// <summary>
    /// The event that states send to themselves when their actions finish.
    /// </summary>
    private const string FinishedEventName = "FINISHED";

    /// <summary>
    /// The name of the action that shows the title of a boss when its fight starts.
    /// </summary>
    private const string BossTitleActionName = "DisplayBossTitle";

    /// <summary>
    /// The prefix of the names of actions that run another FSM inside a state, whose events come back to the state.
    /// </summary>
    private const string SubFsmActionPrefix = "RunFSM";

    /// <summary>
    /// Prefixes of the names of actions that notice where players are, whose events players cause.
    /// </summary>
    private static readonly string[] DetectionActionPrefixes = [
        "CheckAlertRange", "CheckHeroPerformanceRegion", "Trigger2dEvent", "CheckXPosition", "CheckYPosition",
        "TriggerEnterEventSubscribe", "CheckTrackTriggerCount", "CheckCanSeeHero"
    ];

    /// <summary>
    /// The fields of each action type that hold events.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> EventFields = new();

    /// <summary>
    /// The boss rooms of the current scene, by their root object.
    /// </summary>
    private readonly Dictionary<Transform, BossRoom> _bossRooms = new();

    /// <summary>
    /// The boss room of each FSM, or null for FSMs outside boss rooms.
    /// </summary>
    private readonly Dictionary<Fsm, BossRoom?> _fsmBossRooms = new();

    /// <summary>
    /// Events that start fights, held back until every player is in the room, by the FSM that waits for them.
    /// </summary>
    private readonly Dictionary<Fsm, HeldEventStart> _heldEventStarts = new();

    /// <summary>
    /// Hook for passing on events that start bosses.
    /// </summary>
    private Hook? _eventHook;

    /// <summary>
    /// Where the player who passed on the event that is being sent is, while the scene host sends it.
    /// </summary>
    private Vector2? _forwardedAnchor;

    /// <summary>
    /// When each event of a boss room may be passed on to the scene host again, in unscaled seconds, by the path of the
    /// room and the name of the event.
    /// </summary>
    private readonly Dictionary<string, float> _nextForwardTimes = new();

    /// <summary>
    /// When the local player may be told again that an event they passed on waits for other players, in unscaled
    /// seconds.
    /// </summary>
    private float _nextForwardNoticeTime;

    /// <summary>
    /// Registers the hooks for boss rooms that start from events.
    /// </summary>
    private void RegisterEventHooks() {
        _eventHook = CreateHook(
            typeof(Fsm).GetMethod("Event", InstanceFlags, null, [typeof(FsmEventTarget), typeof(FsmEvent)], null),
            new Action<Action<Fsm, FsmEventTarget, FsmEvent>, Fsm, FsmEventTarget, FsmEvent>(OnFsmEvent)
        );
    }

    /// <summary>
    /// Disposes the hooks for boss rooms that start from events.
    /// </summary>
    private void DeregisterEventHooks() {
        _eventHook?.Dispose();
        _eventHook = null;
    }

    /// <summary>
    /// Passes events that start a fight on to the scene host, when an object of the local player sends them to other
    /// objects before the fight began, since bosses only move for the scene host.
    /// </summary>
    private void OnFsmEvent(
        Action<Fsm, FsmEventTarget, FsmEvent> orig,
        Fsm self,
        FsmEventTarget eventTarget,
        FsmEvent fsmEvent
    ) {
        orig(self, eventTarget, fsmEvent);

        if (_networkSender != null || fsmEvent?.Name is not { } eventName || eventTarget == null ||
            eventTarget.target == FsmEventTarget.EventTarget.Self || !IsFollower()) {
            return;
        }

        var room = GetBossRoom(self);
        if (room == null || room.FightBegan || !room.FightEvents.Contains(eventName) || GetInfo(self).IsEntity) {
            return;
        }

        // Objects may send an event every frame
        var path = ScenePath.Get(room.Root);
        var key = path + "\n" + eventName;
        if (_nextForwardTimes.TryGetValue(key, out var nextForwardTime) && Time.unscaledTime < nextForwardTime) {
            return;
        }

        _nextForwardTimes[key] = Time.unscaledTime + ForwardInterval;
        Logger.Info($"Passing event '{eventName}' of '{GetPath(self)}' on to the scene host");
        Send(BossRoomUpdateKind.RoomEvent, path, "", "", "", eventName);

        var shape = room.Wait?.Shape ?? GetRoomShape(room, GetLocalAnchor(room, self));
        if (room.Started || AreAllInRoom(shape)) {
            return;
        }

        // The scene host holds the event for its bosses, so the local player waits in the room until everyone is in,
        // while the intro of the room plays on for a while first
        room.ForwardPending = true;
        BeginRoomWait(room, shape, null, false);

        // The local player wouldn't know why nothing happens, unless an object of the room already waits here
        if (Time.unscaledTime >= _nextForwardNoticeTime &&
            !_heldEventStarts.Keys.Any(fsm => GetBossRoom(fsm) == room)) {
            _nextForwardNoticeTime = Time.unscaledTime + NoticeInterval;
            UiManager.InternalChatBox.AddMessage(WaitingMessage);
        }
    }

    /// <summary>
    /// Sends an event that another player's object sent to the bosses of the scene host that wait for it.
    /// </summary>
    private void OnRoomEvent(BossRoomUpdate update) {
        if (!_entityManager.IsSceneRoleDetermined || !_entityManager.IsSceneHost) {
            return;
        }

        var rootObject = ScenePath.Find(update.Path);
        if (rootObject == null) {
            return;
        }

        var room = GetBossRoom(rootObject.transform);
        if (room.FightBegan) {
            return;
        }

        Vector2? anchor = _playerData.TryGetValue(update.PlayerId, out var playerData) &&
                          playerData.PlayerContainer != null
            ? playerData.PlayerContainer.transform.position
            : null;
        foreach (var playMakerFsm in rootObject.GetComponentsInChildren<PlayMakerFSM>()) {
            var fsm = playMakerFsm.Fsm;
            if (!playMakerFsm.enabled || fsm?.ActiveState is not { } state || !GetInfo(fsm).IsEntity ||
                !IsEventStart(room, fsm, state, update.EventName)) {
                continue;
            }

            Logger.Info($"Another player sent '{update.EventName}' to '{GetPath(fsm)}'");
            _forwardedAnchor = anchor;
            try {
                fsm.Event(update.EventName);
            } finally {
                _forwardedAnchor = null;
            }
        }
    }

    /// <summary>
    /// Holds back an event that starts the fight of a boss room until every player is in the room.
    /// </summary>
    /// <returns>Whether the event is held back.</returns>
    private bool TryHoldEventStart(Fsm fsm, FsmState state, string eventName) {
        // The scene host already waited for the events of its bosses that other players receive
        if (_networkSender != null) {
            return false;
        }

        var room = GetBossRoom(fsm);
        if (room == null || room.Started || !room.StartEventNames.Contains(eventName)) {
            return false;
        }

        // Bosses only move for the scene host, and triggers wait in their own way
        if ((GetInfo(fsm).IsEntity && !_entityManager.IsSceneHost) || !IsEventStart(room, fsm, state, eventName) ||
            GetStartTrigger(fsm, state, eventName) != null) {
            return false;
        }

        // When the boss of the room waits for every player, the room's own objects play their part of the intro for
        // each player as they come in
        if (room.HasBossStarts && !GetInfo(fsm).IsEntity) {
            return false;
        }

        // Of the events that an object sends itself, only those of noticing players are caused by players
        if (FsmExecutionStack.ExecutingFsm == fsm && !IsDetectionEvent(state, eventName)) {
            return false;
        }

        _heldEventStarts.TryGetValue(fsm, out var held);
        if (held != null && (held.StateName != state.Name || held.EventName != eventName)) {
            _heldEventStarts.Remove(fsm);
            held = null;
        }

        var shape = held?.Shape ?? GetRoomShape(room, _forwardedAnchor ?? GetLocalAnchor(room, fsm));
        if (AreAllInRoom(shape)) {
            if (held != null) {
                _heldEventStarts.Remove(fsm);
                RestoreHero(held);
                EndRoomWaitFor(fsm);
            }

            room.Started = true;
            OnBossFightStarting(room);
            return false;
        }

        if (held == null) {
            held = new HeldEventStart(state.Name, eventName, shape);
            _heldEventStarts[fsm] = held;
            Logger.Info($"Holding back '{eventName}' of '{GetPath(fsm)}' until all players are in the room");

            // An interaction that took control from the local player, like talking, gives it back right away, while an
            // intro that sent the event plays on for a while first. An event that another player passed on didn't take
            // control from the local player, who may be busy elsewhere, like sitting on a bench
            var sender = FsmExecutionStack.ExecutingFsm;
            BeginRoomWait(room, shape, fsm, _forwardedAnchor == null && (sender == null || sender == fsm));
        }

        NotifyWaitingInRoom(held);
        return true;
    }

    /// <summary>
    /// Continues the events that start fights once every player is in the room.
    /// </summary>
    private void UpdateEventStarts() {
        if (_heldEventStarts.Count == 0) {
            return;
        }

        foreach (var pair in _heldEventStarts.ToList()) {
            var fsm = pair.Key;
            var held = pair.Value;
            if (fsm.GameObject == null || fsm.ActiveState?.Name != held.StateName) {
                _heldEventStarts.Remove(fsm);
                continue;
            }

            if (!AreAllInRoom(held.Shape)) {
                NotifyWaitingInRoom(held);
                continue;
            }

            _heldEventStarts.Remove(fsm);
            MarkRoomStarted(fsm);
            OnBossFightStarting(fsm);
            RestoreHero(held);
            EndRoomWaitFor(fsm);
            Logger.Info($"All players are in the room of '{GetPath(fsm)}', continuing with '{held.EventName}'");
            fsm.Event(held.EventName);
        }
    }

    /// <summary>
    /// Remembers that the fight of the boss room of an FSM began, if the FSM went into a state that closes gates or
    /// shows the title of a boss. Events don't wait for players anymore after that, and aren't passed on either.
    /// </summary>
    private void MarkFightBegan(Fsm fsm, string stateName) {
        if (GetBossRoom(fsm) is not { FightBegan: false } room ||
            !room.BaseFightStates.TryGetValue(fsm, out var baseFightStates) || !baseFightStates.Contains(stateName)) {
            return;
        }

        // The room's own objects close gates in each player's intro, before the boss that waits for every player starts
        if (room.HasBossStarts && !GetInfo(fsm).IsEntity) {
            return;
        }

        room.FightBegan = true;
        room.Started = true;
        OnBossFightStarting(room);
    }

    /// <summary>
    /// Remembers that the boss room of an FSM started, like when its gates close, after which events don't wait for
    /// players anymore.
    /// </summary>
    private void MarkRoomStarted(Fsm fsm) {
        if (GetBossRoom(fsm) is { } room) {
            room.Started = true;
        }
    }

    /// <summary>
    /// Tells the local player that they or their teammate wait in a room, if they weren't told recently.
    /// </summary>
    private void NotifyWaitingInRoom(HeldEventStart held) {
        if (Time.unscaledTime < held.NextNoticeTime) {
            return;
        }

        held.NextNoticeTime = Time.unscaledTime + NoticeInterval;
        var heroController = HeroController.instance;
        if (heroController != null && held.Shape.Contains(heroController.transform.position)) {
            UiManager.InternalChatBox.AddMessage(WaitingMessage);
            Send(BossRoomUpdateKind.Waiting, "", "", "", "", "");
        } else {
            UiManager.InternalChatBox.AddMessage(TeammateWaitingMessage);
        }
    }

    /// <summary>
    /// Whether the local player and every other connected player are in a room. Players in other scenes aren't.
    /// </summary>
    private bool AreAllInRoom(RoomShape shape) {
        if (!IsHoldActive()) {
            return true;
        }

        // The partner of a two-player save counts as not there while they aren't on the server
        if (_isPartnerMissing()) {
            return false;
        }

        var heroController = HeroController.instance;
        if (heroController == null || !shape.Contains(heroController.transform.position)) {
            return false;
        }

        foreach (var playerData in _playerData.Values) {
            var container = playerData.PlayerContainer;
            if (!playerData.IsInLocalScene || container == null || !shape.Contains(container.transform.position)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets a point in a room to find its shape from: the local player if they are where the camera locks, else the
    /// object that waits for the event.
    /// </summary>
    private static Vector2 GetLocalAnchor(BossRoom room, Fsm fsm) {
        var heroController = HeroController.instance;
        var heroPosition = heroController != null ? (Vector2) heroController.transform.position : (Vector2?) null;
        if (heroPosition is { } position && (room.LockAreas.Count == 0 || IsInRegions(room.LockAreas, position))) {
            return position;
        }

        var ownerPosition = fsm.GameObject != null
            ? (Vector2) fsm.GameObject.transform.position
            : (Vector2) room.Root.position;
        return IsInRegions(room.LockAreas, ownerPosition) ? ownerPosition : heroPosition ?? ownerPosition;
    }

    /// <summary>
    /// Gets the shape of the part of a room that contains a point: the camera locks of the room around it, on its side
    /// of each gate.
    /// </summary>
    private static RoomShape GetRoomShape(BossRoom room, Vector2 anchor) {
        var shape = new RoomShape(anchor);
        foreach (var lockArea in room.LockAreas) {
            if (lockArea != null && ContainsPoint(lockArea, anchor)) {
                shape.LockAreas.Add(lockArea);
            }
        }

        foreach (var gate in room.GateColliders) {
            if (gate == null || GetWorldBounds(gate) is not { } bounds) {
                continue;
            }

            // A gate blocks along its thin side; a point within its span can't tell which side is inside
            var isVertical = bounds.height >= bounds.width;
            var coordinate = isVertical ? anchor.x : anchor.y;
            var min = isVertical ? bounds.xMin : bounds.yMin;
            var max = isVertical ? bounds.xMax : bounds.yMax;
            if (coordinate < min) {
                shape.Limits.Add(new RoomLimit(isVertical, min, true));
            } else if (coordinate > max) {
                shape.Limits.Add(new RoomLimit(isVertical, max, false));
            }
        }

        return shape;
    }

    /// <summary>
    /// Gets the bounds of a collider in the world, also for colliders that are inactive.
    /// </summary>
    private static Rect? GetWorldBounds(Collider2D collider) {
        var offset = collider.offset;
        var points = new List<Vector2>();
        switch (collider) {
            case BoxCollider2D box:
                AddCorners(points, offset, box.size / 2f);
                break;
            case CapsuleCollider2D capsule:
                AddCorners(points, offset, capsule.size / 2f);
                break;
            case CircleCollider2D circle:
                AddCorners(points, offset, Vector2.one * circle.radius);
                break;
            case PolygonCollider2D polygon:
                for (var i = 0; i < polygon.pathCount; i++) {
                    points.AddRange(polygon.GetPath(i).Select(point => point + offset));
                }

                break;
            case EdgeCollider2D edge:
                points.AddRange(edge.points.Select(point => point + offset));
                break;
        }

        if (points.Count == 0) {
            return null;
        }

        var transform = collider.transform;
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        foreach (var point in points) {
            var world = (Vector2) transform.TransformPoint(point);
            min = Vector2.Min(min, world);
            max = Vector2.Max(max, world);
        }

        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    /// <summary>
    /// Adds the corners of a box around a center to a list of points.
    /// </summary>
    private static void AddCorners(List<Vector2> points, Vector2 center, Vector2 halfSize) {
        points.Add(center - halfSize);
        points.Add(center + halfSize);
        points.Add(new Vector2(center.x - halfSize.x, center.y + halfSize.y));
        points.Add(new Vector2(center.x + halfSize.x, center.y - halfSize.y));
    }

    /// <summary>
    /// Whether an event in a state of an FSM of a boss room starts its fight, while the FSM waits for its fight.
    /// </summary>
    private static bool IsEventStart(BossRoom room, Fsm fsm, FsmState state, string eventName) {
        if (!room.EventStarts.TryGetValue(fsm, out var starts) || starts.Count == 0) {
            return false;
        }

        return starts.Contains(GetTransitionKey(state.Name, eventName)) ||
               (starts.Contains(GetTransitionKey("", eventName)) &&
                room.PreFightStates.TryGetValue(fsm, out var preFightStates) && preFightStates.Contains(state.Name));
    }

    /// <summary>
    /// Whether an event is a reaction to fighting, like taking damage or being stunned, which never waits: a player who
    /// attacks a boss already started its fight.
    /// </summary>
    private static bool IsCombatEvent(string eventName) {
        return eventName.Contains("DAMAGE") || eventName.Contains("STUN") || eventName.Contains("PARRY") ||
               eventName == "BLOCKED HIT";
    }

    /// <summary>
    /// Whether an action in a state that notices where players are sends the given event.
    /// </summary>
    private static bool IsDetectionEvent(FsmState state, string eventName) {
        foreach (var action in state.Actions ?? []) {
            if (action == null || !action.Enabled || !IsDetectionAction(action)) {
                continue;
            }

            var names = new HashSet<string>();
            AddEventFieldNames(action, names);
            if (names.Contains(eventName)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an action notices where players are.
    /// </summary>
    private static bool IsDetectionAction(FsmStateAction? action) {
        if (action == null) {
            return false;
        }

        var name = action.GetType().Name;
        if (!DetectionActionPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))) {
            return false;
        }

        // Position checks of the object itself, like whether it landed, don't notice players
        return (!name.StartsWith("CheckXPosition", StringComparison.Ordinal) &&
                !name.StartsWith("CheckYPosition", StringComparison.Ordinal)) ||
               GetActionField(action, "gameObject") is not FsmOwnerDefault {
                   OwnerOption: OwnerDefaultOption.UseOwner
               };
    }

    /// <summary>
    /// Adds the names of the events that an action holds to a set.
    /// </summary>
    private static void AddEventFieldNames(FsmStateAction action, HashSet<string> names) {
        var type = action.GetType();
        if (!EventFields.TryGetValue(type, out var fields)) {
            fields = type.GetFields(InstanceFlags)
                .Where(field => field.FieldType == typeof(FsmEvent) || field.FieldType == typeof(FsmEvent[]))
                .ToArray();
            EventFields[type] = fields;
        }

        foreach (var field in fields) {
            switch (field.GetValue(action)) {
                case FsmEvent { Name: { Length: > 0 } name }:
                    names.Add(name);
                    break;
                case FsmEvent[] events:
                    foreach (var fsmEvent in events) {
                        if (fsmEvent?.Name is { Length: > 0 } eventName) {
                            names.Add(eventName);
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Gets the boss room that an FSM is in, or null if it isn't in one. FSMs of arenas aren't in boss rooms.
    /// </summary>
    private BossRoom? GetBossRoom(Fsm fsm) {
        if (!_fsmBossRooms.TryGetValue(fsm, out var room)) {
            var gameObject = fsm.GameObject;
            var root = gameObject == null || gameObject.GetComponentInParent<BattleScene>(true) != null
                ? null
                : FindBossRoot(gameObject.transform);
            room = root == null ? null : GetBossRoom(root);
            _fsmBossRooms[fsm] = room;
        }

        // Objects that woke up after the room was read may start the fight too
        if (room != null && fsm.Owner != null && !room.AwakeFsms.Contains(fsm) &&
            Time.unscaledTime >= room.NextAnalysisTime) {
            AnalyzeRoom(room);
        }

        return room;
    }

    /// <summary>
    /// Gets the boss room with the given root object.
    /// </summary>
    private BossRoom GetBossRoom(Transform root) {
        if (!_bossRooms.TryGetValue(root, out var room)) {
            room = new BossRoom(root);
            _bossRooms[root] = room;
            AnalyzeRoom(room);
        }

        return room;
    }

    /// <summary>
    /// Finds the outermost boss scene that an object is in.
    /// </summary>
    private static Transform? FindBossRoot(Transform transform) {
        Transform? root = null;
        for (var current = transform; current != null; current = current.parent) {
            if (current.name.StartsWith(BossSceneNamePrefix, StringComparison.Ordinal)) {
                root = current;
            }
        }

        return root;
    }

    /// <summary>
    /// Reads where a boss room is and which events start its fight.
    /// </summary>
    private void AnalyzeRoom(BossRoom room) {
        room.NextAnalysisTime = Time.unscaledTime + RoomAnalysisInterval;
        room.LockAreas.Clear();
        room.GateColliders.Clear();
        room.Gates.Clear();
        room.AwakeFsms.Clear();
        if (room.Root == null) {
            return;
        }

        foreach (var lockArea in room.Root.GetComponentsInChildren<CameraLockArea>(true)) {
            room.LockAreas.AddRange(lockArea.GetComponents<Collider2D>());
        }

        var graphs = new List<FightGraph>();
        var seen = new HashSet<Fsm>();
        foreach (var playMakerFsm in room.Root.GetComponentsInChildren<PlayMakerFSM>(true)) {
            var fsm = playMakerFsm.Fsm;
            if (fsm?.GameObject == null || !seen.Add(fsm)) {
                continue;
            }

            if (fsm.Owner != null) {
                room.AwakeFsms.Add(fsm);
            }

            if (fsm.GameObject.GetComponentInParent<BattleScene>(true) != null) {
                continue;
            }

            var info = GetInfo(fsm);
            if (info.IsGate) {
                room.Gates.Add(fsm);
                foreach (var collider in fsm.GameObject.GetComponentsInChildren<Collider2D>(true)) {
                    if (!collider.isTrigger && !room.GateColliders.Contains(collider)) {
                        room.GateColliders.Add(collider);
                    }
                }

                continue;
            }

            try {
                graphs.Add(new FightGraph(fsm, info.IsEntity));
            } catch (Exception e) {
                Logger.Warn($"Could not read FSM '{fsm.Name}' of boss room '{room.Root.name}':\n{e}");
            }
        }

        room.FightEvents.Clear();
        room.StartEventNames.Clear();
        room.EventStarts.Clear();
        room.PreFightStates.Clear();
        room.BaseFightStates.Clear();

        // Fights start in states that close gates or show the title of a boss, and in states that send events that
        // start fights in other objects of the room
        for (var round = 0; round < 8; round++) {
            var changed = false;
            foreach (var graph in graphs) {
                changed |= graph.AddSendersOf(room.FightEvents);
                graph.UpdateStates();
                foreach (var eventName in graph.GetFightEvents()) {
                    changed |= room.FightEvents.Add(eventName);
                }
            }

            if (!changed) {
                break;
            }
        }

        foreach (var graph in graphs) {
            graph.AddSendersOf(room.FightEvents);
            graph.UpdateStates();
            room.PreFightStates[graph.Fsm] = graph.PreFightStates;
            room.BaseFightStates[graph.Fsm] = graph.BaseFightStates;

            var starts = graph.GetEventStarts();
            room.EventStarts[graph.Fsm] = starts;
            foreach (var key in starts) {
                room.StartEventNames.Add(key[(key.IndexOf('\n') + 1)..]);
            }
        }

        // A boss that starts from its own events or triggers can wait for every player by itself
        room.HasBossStarts = graphs.Any(graph =>
            graph.IsEntity && (room.EventStarts[graph.Fsm].Count > 0 || GetInfo(graph.Fsm).StartTriggers.Count > 0)
        );

        Logger.Info(
            $"Boss room '{ScenePath.Get(room.Root)}': {room.LockAreas.Count} camera locks, " +
            $"{room.GateColliders.Count} gate colliders, boss waits: {room.HasBossStarts}, " +
            $"fights start with: {string.Join(", ", room.StartEventNames)}"
        );
    }

    /// <summary>
    /// Releases the events that wait for other players, if their objects are still there, like after disconnecting,
    /// and forgets the boss rooms of the current scene.
    /// </summary>
    private void ClearEventStarts() {
        var heldEventStarts = _heldEventStarts.ToList();
        _heldEventStarts.Clear();
        _bossRooms.Clear();
        _fsmBossRooms.Clear();
        _nextForwardTimes.Clear();
        if (heldEventStarts.Count == 0) {
            return;
        }

        _bypassGates = true;
        try {
            foreach (var pair in heldEventStarts) {
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
    /// A boss scene: where its camera locks and gates are, and which events start its fight.
    /// </summary>
    private sealed class BossRoom {
        /// <summary>
        /// The root object of the boss scene.
        /// </summary>
        public readonly Transform Root;

        /// <summary>
        /// The colliders of the camera locks of the boss scene.
        /// </summary>
        public readonly List<Collider2D> LockAreas = [];

        /// <summary>
        /// The solid colliders of the gates of the boss scene.
        /// </summary>
        public readonly List<Collider2D> GateColliders = [];

        /// <summary>
        /// The names of the events that start the fight when an object of the room receives them while it waits, which
        /// makes the objects that send them start the fight too.
        /// </summary>
        public readonly HashSet<string> FightEvents = [];

        /// <summary>
        /// The names of all events that start the fight in one of the objects of the room.
        /// </summary>
        public readonly HashSet<string> StartEventNames = [];

        /// <summary>
        /// The transitions of each FSM of the room that start its fight.
        /// </summary>
        public readonly Dictionary<Fsm, HashSet<string>> EventStarts = new();

        /// <summary>
        /// The states of each FSM of the room in which it waits for its fight.
        /// </summary>
        public readonly Dictionary<Fsm, HashSet<string>> PreFightStates = new();

        /// <summary>
        /// The states of each FSM of the room in which its fight began: they close gates or show the title of a boss.
        /// </summary>
        public readonly Dictionary<Fsm, HashSet<string>> BaseFightStates = new();

        /// <summary>
        /// The FSMs of the room that were awake when it was read.
        /// </summary>
        public readonly HashSet<Fsm> AwakeFsms = [];

        /// <summary>
        /// When the room may be read again, in unscaled seconds.
        /// </summary>
        public float NextAnalysisTime;

        /// <summary>
        /// Whether the fight of the room started, with every player in it or because its gates closed, after which its
        /// events don't wait anymore.
        /// </summary>
        public bool Started;

        /// <summary>
        /// Whether an object of the room went into a state where the fight began, after which events aren't passed on
        /// to the scene host anymore.
        /// </summary>
        public bool FightBegan;

        /// <summary>
        /// The FSMs of the gates of the room.
        /// </summary>
        public readonly List<Fsm> Gates = [];

        /// <summary>
        /// Whether a boss of the room starts from its own events or triggers, which wait for every player. The room's
        /// own objects then play their part of the intro for each player as they come in.
        /// </summary>
        public bool HasBossStarts;

        /// <summary>
        /// Whether the local player passed on an event that waits at the scene host until everyone is in the room.
        /// </summary>
        public bool ForwardPending;

        /// <summary>
        /// The wait of the room for the other players, or null while it doesn't wait.
        /// </summary>
        public RoomWait? Wait;

        /// <summary>
        /// Whether the start of the fight of the room with every player there was reported to the two-player save.
        /// </summary>
        public bool FightStartReported;

        public BossRoom(Transform root) {
            Root = root;
        }
    }

    /// <summary>
    /// The part of a room that players must be in: inside its camera locks, on the inner side of its gates.
    /// </summary>
    private sealed class RoomShape {
        /// <summary>
        /// The point that the shape was found from.
        /// </summary>
        public readonly Vector2 Anchor;

        /// <summary>
        /// The camera locks that contain the anchor.
        /// </summary>
        public readonly List<Collider2D> LockAreas = [];

        /// <summary>
        /// The sides of gates that the room is on.
        /// </summary>
        public readonly List<RoomLimit> Limits = [];

        public RoomShape(Vector2 anchor) {
            Anchor = anchor;
        }

        /// <summary>
        /// Whether a point is in the room.
        /// </summary>
        public bool Contains(Vector2 point) {
            if (LockAreas.Count > 0 && !IsInRegions(LockAreas, point)) {
                return false;
            }

            foreach (var limit in Limits) {
                var coordinate = limit.IsVertical ? point.x : point.y;
                if (limit.IsBelow ? coordinate >= limit.Value : coordinate <= limit.Value) {
                    return false;
                }
            }

            return LockAreas.Count > 0 ||
                   Vector2.Distance(point, Anchor) <= (Limits.Count > 0 ? GateRoomRadius : FallbackRoomRadius);
        }
    }

    /// <summary>
    /// The side of a gate that a room is on: below or above a coordinate, horizontally for gates that stand upright.
    /// </summary>
    private readonly struct RoomLimit {
        public readonly bool IsVertical;
        public readonly float Value;
        public readonly bool IsBelow;

        public RoomLimit(bool isVertical, float value, bool isBelow) {
            IsVertical = isVertical;
            Value = value;
            IsBelow = isBelow;
        }
    }

    /// <summary>
    /// An event that starts the fight of a room, held back until every player is in the room.
    /// </summary>
    private sealed class HeldEventStart : HeldFight {
        /// <summary>
        /// The part of the room that players must be in.
        /// </summary>
        public readonly RoomShape Shape;

        /// <summary>
        /// When the players may be told about the wait again, in unscaled seconds.
        /// </summary>
        public float NextNoticeTime;

        public HeldEventStart(string stateName, string eventName, RoomShape shape) : base(stateName, eventName) {
            Shape = shape;
        }
    }

    /// <summary>
    /// The states and transitions of one FSM of a boss room, for finding out which events start the fight.
    /// </summary>
    private sealed class FightGraph {
        /// <summary>
        /// The FSM.
        /// </summary>
        public readonly Fsm Fsm;

        /// <summary>
        /// Whether the FSM belongs to an entity, which only runs for the scene host.
        /// </summary>
        public readonly bool IsEntity;

        /// <summary>
        /// The states in which the fight began: they close gates or show the title of a boss.
        /// </summary>
        public readonly HashSet<string> BaseFightStates = [];

        /// <summary>
        /// The states in which the FSM waits for its fight, as of the last update.
        /// </summary>
        public HashSet<string> PreFightStates { get; private set; } = [];

        private readonly FsmState[] _states;
        private readonly Dictionary<string, FsmState> _statesByName = new();
        private readonly Dictionary<string, HashSet<string>> _ownEvents = new();
        private readonly HashSet<string> _subFsmStates = [];
        private readonly Dictionary<string, List<string>> _sentEvents = new();
        private readonly HashSet<string> _fightStates = [];
        private HashSet<string> _leadingStates = [];
        private HashSet<string> _inevitableStates = [];
        private HashSet<string> _fightReachableStates = [];

        public FightGraph(Fsm fsm, bool isEntity) {
            Fsm = fsm;
            IsEntity = isEntity;
            _states = fsm.States ?? [];
            foreach (var state in _states) {
                _statesByName[state.Name] = state;
                var ownEvents = new HashSet<string> { FinishedEventName };
                var sentEvents = new List<string>();
                foreach (var action in state.Actions ?? []) {
                    // Disabled actions never run
                    if (action == null || !action.Enabled) {
                        continue;
                    }

                    var actionName = action.GetType().Name;
                    if (GetSentEventName(action) is { } sentEvent) {
                        sentEvents.Add(sentEvent);
                        if (CloseEventNames.Contains(sentEvent)) {
                            BaseFightStates.Add(state.Name);
                        }
                    }

                    if (isEntity && actionName == BossTitleActionName) {
                        BaseFightStates.Add(state.Name);
                    }

                    // Whatever another FSM run by the state sends back happens by itself too
                    if (actionName.StartsWith(SubFsmActionPrefix, StringComparison.Ordinal)) {
                        _subFsmStates.Add(state.Name);
                    }

                    // Events of actions that notice players are caused by players, not by the state itself
                    if (!IsDetectionAction(action)) {
                        AddEventFieldNames(action, ownEvents);
                    }
                }

                _ownEvents[state.Name] = ownEvents;
                _sentEvents[state.Name] = sentEvents;
            }

            _fightStates.UnionWith(BaseFightStates);
        }

        /// <summary>
        /// Adds the states that send one of the given events to the states where the fight starts.
        /// </summary>
        /// <returns>Whether states were added.</returns>
        public bool AddSendersOf(HashSet<string> fightEvents) {
            var changed = false;
            foreach (var pair in _sentEvents) {
                if (pair.Value.Any(fightEvents.Contains)) {
                    changed |= _fightStates.Add(pair.Key);
                }
            }

            return changed;
        }

        /// <summary>
        /// Finds the states from which the fight starts by itself, and the states in which the FSM waits for its fight.
        /// </summary>
        public void UpdateStates() {
            UpdateLeadingStates();
            UpdateInevitableStates();
            UpdateFightReachableStates();
            PreFightStates = FindPreFightStates();
        }

        /// <summary>
        /// Finds the states from which the fight starts by steps that happen by themselves.
        /// </summary>
        private void UpdateLeadingStates() {
            var sources = new Dictionary<string, List<string>>();
            foreach (var state in _states) {
                foreach (var transition in state.Transitions ?? []) {
                    if (transition.ToState == null || !IsOwnEvent(state.Name, transition.EventName)) {
                        continue;
                    }

                    if (!sources.TryGetValue(transition.ToState, out var list)) {
                        list = [];
                        sources[transition.ToState] = list;
                    }

                    list.Add(state.Name);
                }
            }

            var steps = new Dictionary<string, int>();
            var queue = new Queue<string>();
            foreach (var fightState in _fightStates) {
                steps[fightState] = 0;
                queue.Enqueue(fightState);
            }

            while (queue.Count > 0) {
                var stateName = queue.Dequeue();
                if (steps[stateName] >= MaxEventStartSteps || !sources.TryGetValue(stateName, out var list)) {
                    continue;
                }

                foreach (var source in list) {
                    if (!steps.ContainsKey(source)) {
                        steps[source] = steps[stateName] + 1;
                        queue.Enqueue(source);
                    }
                }
            }

            _leadingStates = new HashSet<string>(steps.Keys);
        }

        /// <summary>
        /// Finds the states from which the fight starts no matter what: every step that happens by itself goes there. A
        /// state that only might start the fight, like one that tests whether it was won before, can still be one in
        /// which the FSM waits for its fight.
        /// </summary>
        private void UpdateInevitableStates() {
            var inevitableStates = new HashSet<string>(_fightStates);
            for (var round = 0; round < MaxEventStartSteps; round++) {
                var added = false;
                foreach (var state in _states) {
                    if (inevitableStates.Contains(state.Name) || !_leadingStates.Contains(state.Name)) {
                        continue;
                    }

                    var hasOwnTransition = false;
                    var allLeadToFight = true;
                    foreach (var transition in state.Transitions ?? []) {
                        if (!IsOwnEvent(state.Name, transition.EventName)) {
                            continue;
                        }

                        hasOwnTransition = true;
                        if (transition.ToState == null || !inevitableStates.Contains(transition.ToState)) {
                            allLeadToFight = false;
                            break;
                        }
                    }

                    if (hasOwnTransition && allLeadToFight) {
                        added |= inevitableStates.Add(state.Name);
                    }
                }

                if (!added) {
                    break;
                }
            }

            _inevitableStates = inevitableStates;
        }

        /// <summary>
        /// Finds the states that the FSM can go to once its fight began.
        /// </summary>
        private void UpdateFightReachableStates() {
            var reachableStates = new HashSet<string>(_fightStates);
            var queue = new Queue<string>(reachableStates);
            while (queue.Count > 0) {
                if (!_statesByName.TryGetValue(queue.Dequeue(), out var state)) {
                    continue;
                }

                foreach (var transition in state.Transitions ?? []) {
                    if (transition.ToState is { } toState && _statesByName.ContainsKey(toState) &&
                        reachableStates.Add(toState)) {
                        queue.Enqueue(toState);
                    }
                }
            }

            _fightReachableStates = reachableStates;
        }

        /// <summary>
        /// Finds the states in which the FSM waits for its fight: reached from its start state through states in which
        /// it can wait. States of the fight itself lie beyond those.
        /// </summary>
        private HashSet<string> FindPreFightStates() {
            var preFightStates = new HashSet<string>();
            if (Fsm.StartState is not { Length: > 0 } startState || !_statesByName.ContainsKey(startState) ||
                !CanWaitIn(startState)) {
                return preFightStates;
            }

            var queue = new Queue<string>();
            preFightStates.Add(startState);
            queue.Enqueue(startState);
            while (queue.Count > 0) {
                foreach (var transition in _statesByName[queue.Dequeue()].Transitions ?? []) {
                    if (transition.ToState is { } toState && _statesByName.ContainsKey(toState) &&
                        CanWaitIn(toState) && preFightStates.Add(toState)) {
                        queue.Enqueue(toState);
                    }
                }
            }

            return preFightStates;
        }

        /// <summary>
        /// Whether the FSM can wait for its fight in a state: the fight doesn't start from it no matter what, and if it
        /// might start from it, the fight doesn't come back to it, like to the state that chooses the next attack.
        /// </summary>
        private bool CanWaitIn(string stateName) {
            return !_inevitableStates.Contains(stateName) &&
                   (!_leadingStates.Contains(stateName) || !_fightReachableStates.Contains(stateName));
        }

        /// <summary>
        /// Gets the events that start the fight when this FSM receives them while it waits for its fight.
        /// </summary>
        public IEnumerable<string> GetFightEvents() {
            foreach (var state in _states) {
                if (!PreFightStates.Contains(state.Name)) {
                    continue;
                }

                foreach (var transition in state.Transitions ?? []) {
                    if (IsStartTransition(state.Name, transition)) {
                        yield return transition.EventName;
                    }
                }
            }
        }

        /// <summary>
        /// Gets the transitions that start the fight from a state in which the FSM waits for it, with an empty state name
        /// for global transitions.
        /// </summary>
        public HashSet<string> GetEventStarts() {
            var starts = new HashSet<string>();
            foreach (var state in _states) {
                if (!PreFightStates.Contains(state.Name)) {
                    continue;
                }

                foreach (var transition in state.Transitions ?? []) {
                    if (IsStartTransition(state.Name, transition)) {
                        starts.Add(GetTransitionKey(state.Name, transition.EventName));
                    }
                }
            }

            foreach (var transition in Fsm.GlobalTransitions ?? []) {
                if (IsStartTransition(null, transition)) {
                    starts.Add(GetTransitionKey("", transition.EventName));
                }
            }

            return starts;
        }

        /// <summary>
        /// Whether a transition is caused from outside the state and leads to the fight by steps that happen by
        /// themselves.
        /// </summary>
        private bool IsStartTransition(string? stateName, FsmTransition transition) {
            return transition.EventName is { Length: > 0 } eventName && eventName != FinishedEventName &&
                   !IsCombatEvent(eventName) && transition.ToState != null &&
                   _leadingStates.Contains(transition.ToState) &&
                   (stateName == null || !IsOwnEvent(stateName, eventName));
        }

        /// <summary>
        /// Whether a state sends an event to itself, like when its actions finish or test something.
        /// </summary>
        private bool IsOwnEvent(string stateName, string? eventName) {
            return eventName != null && (_subFsmStates.Contains(stateName) ||
                                         (_ownEvents.TryGetValue(stateName, out var events) &&
                                          events.Contains(eventName)));
        }
    }
}
