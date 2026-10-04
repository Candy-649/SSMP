using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// A room whose own objects play their part of the intro for each player as they come in can fall behind its boss in
/// the game of a player who did not set that part off: the boss starts for the other player and tells the room that
/// the fight began while the room still waits for someone to walk in. The room then sat in the start of its intro for
/// the fight, its gate open and the camera loose, until a player wandered into the spot that starts it. Then it shut
/// its gate and held the camera where its intro holds it, with nothing left to tell it that the fight was on: the
/// camera jumped to the middle of one room in the middle of its fight and stayed there. A room that falls behind
/// catches up instead. Its own part plays once the local player is in the room, and what the boss told it before it
/// was ready is told again.
///
/// A player who comes into the scene after the boss began never hears it from the boss at all, since the boss says it
/// once a fight. The games of the players who were there remember what the boss told the room in this visit and tell
/// it to the player who comes in, whose room then catches up the same way.
///
/// Such a player comes in through the door of the room, and the part of the intro that the other player's figure sets
/// off shuts a gate right there. The room waits while the local player is still in the doorway.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// How long, in seconds, an object of a room that fell behind its boss is told again what the boss told it before.
    /// </summary>
    private const float CatchUpTime = 5f;

    /// <summary>
    /// What separates the names of the events that a boss told its room, when a player who comes in is told them.
    /// </summary>
    private const char BossEventSeparator = '\n';

    /// <summary>
    /// Events that the bosses of rooms sent to the room's own objects while those couldn't take them yet, by the FSM
    /// that missed them.
    /// </summary>
    private readonly Dictionary<Fsm, HashSet<string>> _missedBossEvents = new();

    /// <summary>
    /// Objects of rooms that are catching up with their boss, with when they stop being told what they missed, in
    /// unscaled seconds.
    /// </summary>
    private readonly Dictionary<Fsm, float> _catchUpEnds = new();

    /// <summary>
    /// Objects of rooms that still wait for a player to walk in while their boss already told them that the fight
    /// began, with the state they wait in and the event of a player walking in.
    /// </summary>
    private readonly Dictionary<Fsm, (string StateName, string EventName)> _pendingCatchUps = new();

    /// <summary>
    /// Objects of rooms that were moved on as if a player walked in, which happens once for a visit of the room.
    /// </summary>
    private readonly HashSet<Fsm> _pushedCatchUps = [];

    /// <summary>
    /// What the bosses of rooms told the room's own objects in this visit of the scene, in the order that they took
    /// it, by the FSM that took it.
    /// </summary>
    private readonly Dictionary<Fsm, List<string>> _bossEventsTaken = new();

    /// <summary>
    /// Objects of rooms that were told what their boss said before the local player came into the scene, and that
    /// didn't come to a state yet from which they catch up.
    /// </summary>
    private readonly HashSet<Fsm> _lateCatchUps = [];

    /// <summary>
    /// The steps of objects of rooms that a player walking in sets off, by the object, the state it waits in and the
    /// event of the step, or null for an event that no trigger of that state sends.
    /// </summary>
    private readonly Dictionary<(Fsm Fsm, string StateName, string EventName), WalkIn?> _walkIns = new();

    /// <summary>
    /// The gates that the states of objects of rooms shut, by the object and the state.
    /// </summary>
    private readonly Dictionary<(Fsm Fsm, string StateName), List<Collider2D>> _shutGates = new();

    /// <summary>
    /// Notes an event between a boss and its room: one that the boss sends to an object of the room, which is
    /// remembered for players who come in later, and which arrives out of step if that object can't take it yet; and
    /// one that starts the boss which an object of the room sends after the boss already began.
    /// </summary>
    private void NoteOutOfStepEvent(Fsm self, string eventName) {
        if (_bossRooms.Count == 0 || self.ActiveState is not { } state || !_netClient.IsConnected) {
            return;
        }

        var sender = _networkSender ?? FsmExecutionStack.ExecutingFsm;
        if (sender == null || sender == self) {
            return;
        }

        // Only the FSM that runs the room catches up: what else in the room hears from the boss isn't waiting for it
        var isBoss = GetInfo(self).IsEntity;
        if (isBoss == GetInfo(sender).IsEntity || !GetInfo(isBoss ? sender : self).IsController ||
            GetBossRoom(self) is not { HasBossStarts: true } room || !IsInRoomOrCopy(sender, room)) {
            return;
        }

        if (CanTakeEvent(self, state, eventName)) {
            // What moved the room on from where it was, not what any state of it takes. FINISHED would move on any
            // state of the room of a player who comes in later.
            if (!isBoss && eventName != FinishedEventName && GetTransition(state, eventName) != null) {
                NoteBossEventTaken(self, eventName);
            }

            return;
        }

        if (!IsCoopActive()) {
            return;
        }

        if (isBoss) {
            if (room.StartEventNames.Contains(eventName)) {
                BeginCatchUp(sender);
            }

            return;
        }

        var missed = GetMissedBossEvents(self);
        missed.Add(eventName);
        if (!_pushedCatchUps.Contains(self) && !_catchUpEnds.ContainsKey(self)) {
            PlanCatchUpPush(self, state, missed);
        }
    }

    /// <summary>
    /// Remembers that an object of a room took an event from its boss in this visit, for players who come in later.
    /// </summary>
    private void NoteBossEventTaken(Fsm fsm, string eventName) {
        if (!_bossEventsTaken.TryGetValue(fsm, out var taken)) {
            taken = [];
            _bossEventsTaken[fsm] = taken;
        }

        if (!taken.Contains(eventName)) {
            taken.Add(eventName);
        }
    }

    /// <summary>
    /// Gets the events that the boss of a room sent to an object of the room while it couldn't take them yet.
    /// </summary>
    private HashSet<string> GetMissedBossEvents(Fsm fsm) {
        if (!_missedBossEvents.TryGetValue(fsm, out var missed)) {
            missed = [];
            _missedBossEvents[fsm] = missed;
        }

        return missed;
    }

    /// <summary>
    /// Has an object of a room that waits for a player to walk in moved on as if one did, once the local player is in
    /// the room, if walking in takes it to a state that takes what its boss said.
    /// </summary>
    /// <returns>Whether it waits for a player to walk in like that.</returns>
    private bool PlanCatchUpPush(Fsm fsm, FsmState state, HashSet<string> missed) {
        foreach (var transition in state.Transitions ?? []) {
            if (transition.EventName is not { Length: > 0 } walkIn ||
                (transition.ToFsmState ?? fsm.GetState(transition.ToState)) is not { } next ||
                GetTakenTransition(next, missed) == null || !IsDetectionEvent(state, walkIn)) {
                continue;
            }

            _pendingCatchUps[fsm] = (state.Name, walkIn);
            _pushedCatchUps.Add(fsm);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether an FSM is in a boss room, or belongs to the copy of a creature of it that runs in a game that isn't the
    /// scene host's. Such a copy is made outside of the room, but it tells the room what the boss in the scene host's
    /// game tells it there.
    /// </summary>
    private bool IsInRoomOrCopy(Fsm fsm, BossRoom room) {
        return GetBossRoom(fsm) is not { } fsmRoom ? GetInfo(fsm).IsEntity : fsmRoom == room;
    }

    /// <summary>
    /// Lets an object of a room that fell behind its boss be told again what the boss told it before, for a while.
    /// </summary>
    private void BeginCatchUp(Fsm fsm) {
        if (!_catchUpEnds.ContainsKey(fsm)) {
            Logger.Info($"'{GetPath(fsm)}' fell behind its boss, which began the fight for another player");
        }

        _catchUpEnds[fsm] = Time.unscaledTime + CatchUpTime;
    }

    /// <summary>
    /// Plays the part of the intro of a room that fell behind its boss as if a player walked in, since the fight that it
    /// leads to began, once the local player is in the room. Done a frame after the boss said so, outside of what the
    /// boss was doing. Objects of rooms that were told late what their boss said catch up as well.
    /// </summary>
    private void UpdateCatchUps() {
        UpdateLateCatchUps();
        if (_pendingCatchUps.Count == 0) {
            return;
        }

        foreach (var pair in _pendingCatchUps.ToList()) {
            var fsm = pair.Key;
            var (stateName, walkIn) = pair.Value;
            if (fsm.GameObject == null || fsm.ActiveState is not { } state || state.Name != stateName) {
                _pendingCatchUps.Remove(fsm);
                continue;
            }

            // That part of the intro closes the room around the player, so it waits for them to be in the room
            if (!IsLocalHeroInBossRoom(fsm) || IsLocalHeroInDoorway(fsm, state, walkIn)) {
                continue;
            }

            _pendingCatchUps.Remove(fsm);
            BeginCatchUp(fsm);
            Logger.Info($"'{GetPath(fsm)}' still waited in '{stateName}' for a player to walk in, sending '{walkIn}'");
            fsm.Event(walkIn);
        }
    }

    /// <summary>
    /// Lets the objects of rooms that were told late what their boss said catch up from the state that they are in:
    /// at once if the state takes it, like after the local player walked in, once the local player is in the room;
    /// else once the local player walks in, if the state waits for that. Otherwise they try again later.
    /// </summary>
    private void UpdateLateCatchUps() {
        if (_lateCatchUps.Count == 0) {
            return;
        }

        foreach (var fsm in _lateCatchUps.ToList()) {
            if (fsm.GameObject == null || !_missedBossEvents.TryGetValue(fsm, out var missed) || missed.Count == 0) {
                _lateCatchUps.Remove(fsm);
                continue;
            }

            if (fsm.ActiveState is not { } state || fsm.IsSwitchingState) {
                continue;
            }

            if (GetTakenTransition(state, missed) is { } transition) {
                var next = transition.ToFsmState ?? fsm.GetState(transition.ToState);
                if (!IsLocalHeroInBossRoom(fsm) ||
                    (next != null && IsLocalHeroOutsideGates(GetShutGates(fsm, next), GetRoomInside(fsm)))) {
                    continue;
                }

                _lateCatchUps.Remove(fsm);
                _pendingCatchUps.Remove(fsm);
                BeginCatchUp(fsm);
                CatchUpWithBoss(fsm, state);
                continue;
            }

            if (_pushedCatchUps.Contains(fsm) || PlanCatchUpPush(fsm, state, missed)) {
                _lateCatchUps.Remove(fsm);
            }
        }
    }

    /// <summary>
    /// Whether an event is an object of a room noticing a player walk in while the local player is still in the
    /// doorway of the room, in or behind a gate that the step shuts. A trigger that sees the other player's figure sets
    /// it off then, which would shut the local player out of the room. The trigger notices again every frame, so the
    /// step is taken once the local player is through.
    /// </summary>
    private bool IsWalkInAtDoorway(Fsm fsm, string eventName) {
        if (_bossRooms.Count == 0 || FsmExecutionStack.ExecutingFsm != fsm || fsm.ActiveState is not { } state ||
            !GetInfo(fsm).IsController || GetWalkIn(fsm, state, eventName) is not { NoticesEveryFrame: true } walkIn ||
            GetBossRoom(fsm) is not { HasBossStarts: true } || !IsCoopActive() ||
            !IsLocalHeroOutsideGates(walkIn.Gates, walkIn.Inside)) {
            return false;
        }

        if (!walkIn.HeldInDoorway) {
            walkIn.HeldInDoorway = true;
            Logger.Info(
                $"'{GetPath(fsm)}' noticed a player walk in while the local player is still in the doorway, waiting " +
                "until they are through the gate"
            );
        }

        return true;
    }

    /// <summary>
    /// Whether the local player is still in the doorway of a room, in or behind a gate that a step that a player walking
    /// in sets off shuts.
    /// </summary>
    private bool IsLocalHeroInDoorway(Fsm fsm, FsmState state, string eventName) {
        return GetWalkIn(fsm, state, eventName) is { } walkIn && IsLocalHeroOutsideGates(walkIn.Gates, walkIn.Inside);
    }

    /// <summary>
    /// Gets the step of an object of a room that a player walking in sets off from a state with an event: where the
    /// triggers of the state notice the player and which gates the step shuts.
    /// </summary>
    /// <returns>The step, or null if no trigger of the state sends the event.</returns>
    private WalkIn? GetWalkIn(Fsm fsm, FsmState state, string eventName) {
        var key = (fsm, state.Name, eventName);
        if (_walkIns.TryGetValue(key, out var walkIn)) {
            return walkIn;
        }

        var transition = GetTransition(state, eventName);
        var next = transition == null ? null : transition.ToFsmState ?? fsm.GetState(transition.ToState);
        if (next != null) {
            var regions = new List<Collider2D>();
            var noticesEveryFrame = true;
            foreach (var action in state.Actions ?? []) {
                var kind = GetTriggerKind(action);
                if (kind == TriggerKind.None || !action.Enabled || GetTriggerEventName(action, kind) != eventName) {
                    continue;
                }

                AddRegionColliders(fsm, action, kind, regions);
                noticesEveryFrame &= kind != TriggerKind.Collider &&
                                     GetActionField(action, "everyFrame") is true or FsmBool { Value: true };
            }

            if (GetRegionsCenter(regions) is { } inside) {
                walkIn = new WalkIn(inside, GetShutGates(fsm, next), noticesEveryFrame);
            }
        }

        _walkIns[key] = walkIn;
        return walkIn;
    }

    /// <summary>
    /// Gets the gates that a state of an object of a room shuts: the solid colliders standing upright in the objects
    /// that it switches on, like the gate that the intro of a room shuts behind the player.
    /// </summary>
    private List<Collider2D> GetShutGates(Fsm fsm, FsmState state) {
        var key = (fsm, state.Name);
        if (_shutGates.TryGetValue(key, out var gates)) {
            return gates;
        }

        gates = [];
        foreach (var action in state.Actions ?? []) {
            // The object of the FSM itself holds the whole room rather than a gate of it
            if (action is not ActivateGameObject { Enabled: true } activate || activate.activate?.Value != true ||
                fsm.GetOwnerDefaultTarget(activate.gameObject) is not { } target || fsm.GameObject == null ||
                fsm.GameObject.transform.IsChildOf(target.transform)) {
                continue;
            }

            foreach (var collider in target.GetComponentsInChildren<Collider2D>(true)) {
                if (!collider.isTrigger && GetWorldBounds(collider) is { } bounds && bounds.height >= bounds.width &&
                    !gates.Contains(collider)) {
                    gates.Add(collider);
                }
            }
        }

        _shutGates[key] = gates;
        return gates;
    }

    /// <summary>
    /// Gets a point inside of the room of an object of a room: the middle of its triggers that notice players walking
    /// in, or of the camera locks of the room.
    /// </summary>
    private Vector2? GetRoomInside(Fsm fsm) {
        return GetRegionsCenter(GetRegions(fsm)) ??
               (GetBossRoom(fsm) is { } room ? GetRegionsCenter(room.LockAreas) : null);
    }

    /// <summary>
    /// Gets the middle of the space that the given colliders take together, or null if there are none.
    /// </summary>
    private static Vector2? GetRegionsCenter(List<Collider2D> regions) {
        Rect? all = null;
        foreach (var region in regions) {
            if (region == null || GetWorldBounds(region) is not { } bounds) {
                continue;
            }

            all = all is { } joined ? Rect.MinMaxRect(
                Mathf.Min(joined.xMin, bounds.xMin),
                Mathf.Min(joined.yMin, bounds.yMin),
                Mathf.Max(joined.xMax, bounds.xMax),
                Mathf.Max(joined.yMax, bounds.yMax)
            ) : bounds;
        }

        return all?.center;
    }

    /// <summary>
    /// Whether the local player stands in one of the given gates, or on its side away from a point inside of the room.
    /// A gate only blocks the way at its own height, so one above or below the player doesn't count.
    /// </summary>
    private static bool IsLocalHeroOutsideGates(List<Collider2D> gates, Vector2? inside) {
        var heroController = HeroController.instance;
        if (gates.Count == 0 || inside is not { } point || heroController == null) {
            return false;
        }

        var hero = heroController.col2d != null
            ? Rect.MinMaxRect(
                heroController.col2d.bounds.min.x,
                heroController.col2d.bounds.min.y,
                heroController.col2d.bounds.max.x,
                heroController.col2d.bounds.max.y
            )
            : new Rect((Vector2) heroController.transform.position, Vector2.zero);
        foreach (var gate in gates) {
            if (gate == null || GetWorldBounds(gate) is not { } bounds || hero.yMax < bounds.yMin ||
                hero.yMin > bounds.yMax) {
                continue;
            }

            if ((point.x > bounds.xMax && hero.xMin <= bounds.xMax) ||
                (point.x < bounds.xMin && hero.xMax >= bounds.xMin)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tells an object of a room that is catching up with its boss what the boss told it before it was ready, once it
    /// comes to a state that takes it. That state has done what it does on entering by then, so the room's own part of
    /// the intro plays in full.
    /// </summary>
    private void CatchUpWithBoss(Fsm self, FsmState toState) {
        // One that went on by itself before it was moved on, like when the other player walked in, catches up all the
        // same: it comes to the state that takes what it missed, or one on the way there
        if (_pendingCatchUps.Count > 0 && _pendingCatchUps.TryGetValue(self, out var pending) &&
            pending.StateName != toState.Name) {
            _pendingCatchUps.Remove(self);
            _lateCatchUps.Remove(self);
            BeginCatchUp(self);
        }

        if (_catchUpEnds.Count == 0 || !_catchUpEnds.TryGetValue(self, out var ends)) {
            return;
        }

        // What it missed long before the catching up is let go of with it
        if (Time.unscaledTime > ends || !_missedBossEvents.TryGetValue(self, out var missed) || missed.Count == 0) {
            _catchUpEnds.Remove(self);
            _missedBossEvents.Remove(self);
            return;
        }

        // A state that its own actions already sent on would drop the event, so it is kept for the state it goes to
        if (self.ActiveState != toState || self.IsSwitchingState) {
            return;
        }

        foreach (var transition in toState.Transitions ?? []) {
            if (transition.EventName is not { Length: > 0 } eventName || !missed.Remove(eventName)) {
                continue;
            }

            Logger.Info($"'{GetPath(self)}' came to '{toState.Name}', telling it '{eventName}' that its boss sent before");
            NoteBossEventTaken(self, eventName);
            _catchUpDelivery = (self, eventName);
            try {
                self.Event(eventName);
            } finally {
                _catchUpDelivery = null;
            }

            return;
        }
    }

    /// <summary>
    /// Tells a player who came into the scene what the bosses of its rooms told the rooms in this visit, since the
    /// bosses won't say it again.
    /// </summary>
    private void TellBossEventsToNewPlayer() {
        foreach (var pair in _bossEventsTaken) {
            if (pair.Key.GameObject == null || pair.Value.Count == 0) {
                continue;
            }

            Logger.Info(
                $"Telling the player who came in what the boss of '{GetPath(pair.Key)}' said in this fight: " +
                string.Join(", ", pair.Value)
            );
            Send(BossRoomUpdateKind.BossEvents, pair.Key, "", "", string.Join(BossEventSeparator.ToString(), pair.Value));
        }
    }

    /// <summary>
    /// Takes what the boss of a room told the room in the game of another player before the local player came into
    /// the scene, so that the room catches up with its boss once the local player is in the room.
    /// </summary>
    private void OnBossEvents(BossRoomUpdate update) {
        if (FindFsm(update.Path, update.FsmName) is not { } fsm || !GetInfo(fsm).IsController ||
            GetBossRoom(fsm) is not { HasBossStarts: true }) {
            return;
        }

        _bossEventsTaken.TryGetValue(fsm, out var taken);
        var missed = GetMissedBossEvents(fsm);
        var isNew = false;
        foreach (var eventName in update.EventName.Split(BossEventSeparator)) {
            if (eventName.Length > 0 && eventName != FinishedEventName && taken?.Contains(eventName) != true &&
                missed.Add(eventName)) {
                isNew = true;
            }
        }

        if (!isNew) {
            return;
        }

        Logger.Info(
            $"'{GetPath(fsm)}' is told what its boss said in this fight before the local player came in: " +
            string.Join(", ", missed)
        );
        _lateCatchUps.Add(fsm);
    }

    /// <summary>
    /// What an object of a room that is catching up is being told again, which goes through without being held: it is
    /// the boss's word, sent before, and no start of anything.
    /// </summary>
    private (Fsm Fsm, string EventName)? _catchUpDelivery;

    /// <summary>
    /// Whether an event is one that an object of a room that is catching up is being told again.
    /// </summary>
    private bool IsCatchUpDelivery(Fsm fsm, string eventName) {
        return _catchUpDelivery is { } delivery && delivery.Fsm == fsm && delivery.EventName == eventName;
    }

    /// <summary>
    /// Whether an FSM in a state goes somewhere on an event, from that state or from anywhere.
    /// </summary>
    private static bool CanTakeEvent(Fsm fsm, FsmState state, string eventName) {
        if (GetTransition(state, eventName) != null) {
            return true;
        }

        foreach (var transition in fsm.GlobalTransitions ?? []) {
            if (transition.EventName == eventName) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the transition of a state's own that it takes on an event, or null if it has none.
    /// </summary>
    private static FsmTransition? GetTransition(FsmState state, string eventName) {
        foreach (var transition in state.Transitions ?? []) {
            if (transition.EventName == eventName) {
                return transition;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the transition of a state's own that it takes on one of the given events, or null if it has none.
    /// </summary>
    private static FsmTransition? GetTakenTransition(FsmState state, HashSet<string> eventNames) {
        foreach (var transition in state.Transitions ?? []) {
            if (transition.EventName is { Length: > 0 } eventName && eventNames.Contains(eventName)) {
                return transition;
            }
        }

        return null;
    }

    /// <summary>
    /// Forgets the objects of the rooms of the previous scene that were behind their bosses, what the bosses told them,
    /// and the steps and gates of the rooms.
    /// </summary>
    private void ClearCatchUps() {
        _missedBossEvents.Clear();
        _catchUpEnds.Clear();
        _pendingCatchUps.Clear();
        _pushedCatchUps.Clear();
        _bossEventsTaken.Clear();
        _lateCatchUps.Clear();
        _walkIns.Clear();
        _shutGates.Clear();
    }

    /// <summary>
    /// A step of an object of a room that a player walking in sets off.
    /// </summary>
    private sealed class WalkIn(Vector2 inside, List<Collider2D> gates, bool noticesEveryFrame) {
        /// <summary>
        /// The middle of the triggers that notice a player walking in, which is inside of the room.
        /// </summary>
        public readonly Vector2 Inside = inside;

        /// <summary>
        /// The gates that the step shuts.
        /// </summary>
        public readonly List<Collider2D> Gates = gates;

        /// <summary>
        /// Whether every trigger that sends the step notices players again every frame, so that the step is taken
        /// later when it is held back once.
        /// </summary>
        public readonly bool NoticesEveryFrame = noticesEveryFrame;

        /// <summary>
        /// Whether the step was held back while the local player was in the doorway, which is logged once.
        /// </summary>
        public bool HeldInDoorway;
    }
}
