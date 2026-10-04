using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
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
/// catches up instead. Its own part plays at once, and what the boss told it before it was ready is told again.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// How long, in seconds, an object of a room that fell behind its boss is told again what the boss told it before.
    /// </summary>
    private const float CatchUpTime = 5f;

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
    /// Notes an event between a boss and its room that arrives out of step: one that the boss sends to an object of the
    /// room which can't take it yet, and one that starts the boss which an object of the room sends after the boss
    /// already began.
    /// </summary>
    private void NoteOutOfStepEvent(Fsm self, string eventName) {
        if (_bossRooms.Count == 0 || self.ActiveState is not { } state) {
            return;
        }

        var sender = _networkSender ?? FsmExecutionStack.ExecutingFsm;
        if (sender == null || sender == self || CanTakeEvent(self, state, eventName) || !IsCoopActive() ||
            GetBossRoom(self) is not { HasBossStarts: true } room || !IsInRoomOrCopy(sender, room)) {
            return;
        }

        // Only the FSM that runs the room catches up: what else in the room hears from the boss isn't waiting for it
        var isBoss = GetInfo(self).IsEntity;
        if (isBoss == GetInfo(sender).IsEntity || !GetInfo(isBoss ? sender : self).IsController) {
            return;
        }

        if (isBoss) {
            if (room.StartEventNames.Contains(eventName)) {
                BeginCatchUp(sender);
            }

            return;
        }

        if (!_missedBossEvents.TryGetValue(self, out var missed)) {
            missed = [];
            _missedBossEvents[self] = missed;
        }

        missed.Add(eventName);
        if (_pushedCatchUps.Contains(self) || _catchUpEnds.ContainsKey(self)) {
            return;
        }

        // Still waiting for a player to walk in, one step away from where what the boss said is taken
        foreach (var transition in state.Transitions ?? []) {
            if (transition.EventName is not { Length: > 0 } walkIn ||
                (transition.ToFsmState ?? self.GetState(transition.ToState)) is not { } next ||
                !CanTakeEvent(self, next, eventName) || !IsDetectionEvent(state, walkIn)) {
                continue;
            }

            _pendingCatchUps[self] = (state.Name, walkIn);
            _pushedCatchUps.Add(self);
            return;
        }
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
    /// leads to began. Done a frame after the boss said so, outside of what the boss was doing.
    /// </summary>
    private void UpdateCatchUps() {
        if (_pendingCatchUps.Count == 0) {
            return;
        }

        foreach (var pair in _pendingCatchUps.ToList()) {
            var fsm = pair.Key;
            var (stateName, walkIn) = pair.Value;
            if (fsm.GameObject == null || fsm.ActiveState?.Name != stateName) {
                _pendingCatchUps.Remove(fsm);
                continue;
            }

            // That part of the intro closes the room around the player, so it waits for them to be in the room
            if (!IsLocalHeroInBossRoom(fsm)) {
                continue;
            }

            _pendingCatchUps.Remove(fsm);
            BeginCatchUp(fsm);
            Logger.Info($"'{GetPath(fsm)}' still waited in '{stateName}' for a player to walk in, sending '{walkIn}'");
            fsm.Event(walkIn);
        }
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
        foreach (var transition in state.Transitions ?? []) {
            if (transition.EventName == eventName) {
                return true;
            }
        }

        foreach (var transition in fsm.GlobalTransitions ?? []) {
            if (transition.EventName == eventName) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Forgets the objects of the rooms of the previous scene that were behind their bosses.
    /// </summary>
    private void ClearCatchUps() {
        _missedBossEvents.Clear();
        _catchUpEnds.Clear();
        _pendingCatchUps.Clear();
        _pushedCatchUps.Clear();
    }
}
