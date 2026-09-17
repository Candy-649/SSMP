using System;
using System.Collections.Generic;
using System.Linq;
using MonoMod.RuntimeDetour;
using SSMP.Ui;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// What a boss room does while its boss waits for every player. The room's own objects play their part of the intro
/// for each player as they come in, and only the boss waits. Meanwhile, a player in the room gets control back when the
/// intro keeps it, can't leave through the gates of the room, and can't hurt the boss. A player who gives up waiting
/// opens the gates for themselves.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// How long, in seconds, the intro of a waiting room may keep control from the local player before it is given back.
    /// Short breaks, like picking something up, are left alone.
    /// </summary>
    private const float FreeHeroDelay = 2f;

    /// <summary>
    /// How long, in seconds, a gate that kept the local player in a waiting room stays closed after the room started,
    /// before it opens again because the fight didn't close it.
    /// </summary>
    private const float UnclaimedGateTime = 8f;

    /// <summary>
    /// The name of the boolean of the player data that stops the player from pausing.
    /// </summary>
    private const string DisablePauseName = "disablePause";

    /// <summary>
    /// The message that tells a player in a waiting room how to leave it.
    /// </summary>
    private static string GiveUpHintMessage => Lang.Pick(
        "Type /giveup to open the doors if you don't want to wait.",
        "不想等的话，输入 /giveup 就能把门打开。"
    );

    /// <summary>
    /// The message for a player who opened the doors of the room they waited in.
    /// </summary>
    private static string GaveUpMessage => Lang.Pick(
        "The doors are open. The boss still waits until your teammate is in the room.",
        "门开了。不过在队友进屋之前，这里仍然不会开打。"
    );

    /// <summary>
    /// The message for a player who gives up waiting while they don't wait in a room.
    /// </summary>
    private static string NothingToGiveUpMessage => Lang.Pick(
        "You aren't waiting in a boss room.",
        "你现在并没有在等待什么。"
    );

    /// <summary>
    /// The events that close a gate, in the order they are tried. Gates only react to them while they are open.
    /// </summary>
    private static readonly string[] GateCloseEvents = ["BG CLOSE", "BG QUICK CLOSE"];

    /// <summary>
    /// The event that opens a gate. Gates only react to it while they are closed, unlike the quick open event, which
    /// they react to at any time.
    /// </summary>
    private static readonly string[] GateOpenEvents = [OpenGateEventName];

    /// <summary>
    /// Whether this class changes the control of the local player itself, which the hooks ignore.
    /// </summary>
    private static bool _changingHero;

    /// <summary>
    /// Gates that kept the local player in a room that started, which open again unless the fight closes them in time.
    /// </summary>
    private readonly List<UnclaimedGate> _unclaimedGates = [];

    /// <summary>
    /// Hook for noticing when the game gives control back to the local player while a room waits.
    /// </summary>
    private Hook? _regainControlHook;

    /// <summary>
    /// Registers the hooks for boss rooms that wait for every player.
    /// </summary>
    private void RegisterWaitHooks() {
        // Giving control back without arguments goes through the overload with one
        var regainControl = typeof(HeroController).GetMethods(InstanceFlags)
            .FirstOrDefault(method => method.Name == "RegainControl" && method.GetParameters().Length == 1);
        _regainControlHook = regainControl?.GetParameters()[0].ParameterType == typeof(bool)
            ? CreateHook(regainControl, new Action<Action<HeroController, bool>, HeroController, bool>(OnRegainControl))
            : CreateHook(
                typeof(HeroController).GetMethod("RegainControl", InstanceFlags, null, Type.EmptyTypes, null),
                new Action<Action<HeroController>, HeroController>(OnRegainControl)
            );
    }

    /// <summary>
    /// Disposes the hooks for boss rooms that wait for every player.
    /// </summary>
    private void DeregisterWaitHooks() {
        _regainControlHook?.Dispose();
        _regainControlHook = null;
    }

    /// <summary>
    /// Notices when the game gives control back to the local player through the overload with an argument.
    /// </summary>
    private void OnRegainControl(Action<HeroController, bool> orig, HeroController self, bool allowInput) {
        orig(self, allowInput);
        ForgetTakenControl();
    }

    /// <summary>
    /// Notices when the game gives control back to the local player through the overload without arguments.
    /// </summary>
    private void OnRegainControl(Action<HeroController> orig, HeroController self) {
        orig(self);
        ForgetTakenControl();
    }

    /// <summary>
    /// Forgets that the intros of waiting rooms took control from the local player once the game gives it back itself,
    /// so that it isn't taken again when the rooms start.
    /// </summary>
    private void ForgetTakenControl() {
        if (_changingHero) {
            return;
        }

        foreach (var room in _bossRooms.Values) {
            if (room.Wait is { } wait) {
                wait.RegainedControl = false;
                wait.StartedAnimation = false;
                wait.EnabledPause = false;
            }
        }
    }

    /// <summary>
    /// Forgets that the intros of waiting rooms stopped the local player from pausing once the game allows it again
    /// itself.
    /// </summary>
    private void ForgetDisabledPause() {
        foreach (var room in _bossRooms.Values) {
            if (room.Wait is { } wait) {
                wait.EnabledPause = false;
            }
        }
    }

    /// <summary>
    /// Starts waiting in a boss room for the other players. The local player gets control back right away if an
    /// interaction of theirs in the room took it, and a boss of the scene host can't be hurt while it waits.
    /// </summary>
    /// <param name="room">The boss room.</param>
    /// <param name="shape">The part of the room that players must be in, or null to find it from the FSM that waits.
    /// </param>
    /// <param name="fsm">The FSM that waits, or null for an event that was passed on to the scene host.</param>
    /// <param name="freeHero">Whether to give control back to the local player right away, instead of after the intro
    /// kept it for a while.</param>
    private void BeginRoomWait(BossRoom room, RoomShape? shape, Fsm? fsm, bool freeHero) {
        if (room.Wait is not { } wait) {
            if (shape == null && fsm == null) {
                return;
            }

            wait = new RoomWait(shape ?? GetRoomShape(room, GetLocalAnchor(room, fsm!)));
            room.Wait = wait;
            Logger.Info($"Boss room '{GetRoomPath(room)}' waits for the other players");
        }

        var heroController = HeroController.instance;
        if (freeHero && heroController != null && wait.Shape.Contains(heroController.transform.position)) {
            FreeHero(wait);
        }

        // The other players hit the boss through the scene host
        if (fsm?.GameObject == null || !GetInfo(fsm).IsEntity || !_entityManager.IsSceneHost) {
            return;
        }

        var healthManager = fsm.GameObject.GetComponentInParent<HealthManager>(true) ??
                            fsm.GameObject.GetComponentInChildren<HealthManager>(true);
        if (healthManager != null && !wait.Invincible.ContainsKey(healthManager)) {
            wait.Invincible[healthManager] = healthManager.IsInvincible;
            healthManager.IsInvincible = true;
        }
    }

    /// <summary>
    /// Stops waiting in a boss room, because it starts or doesn't wait anymore. The intro takes control from the local
    /// player again like it did before, the boss can be hurt again, and the gates that kept the player in open again
    /// unless the fight closes them itself.
    /// </summary>
    /// <param name="room">The boss room.</param>
    /// <param name="restoreHero">Whether what waited goes on now, so that its intro takes control again. A wait that
    /// ended some other way, like when the object that waited moved on, leaves the local player's control alone.</param>
    private void EndRoomWait(BossRoom room, bool restoreHero) {
        if (room.Wait is not { } wait) {
            return;
        }

        room.Wait = null;

        // A player who left the room, like after giving up, doesn't lose control where they are now
        var heroController = HeroController.instance;
        if (restoreHero && heroController != null && wait.Shape.Contains(heroController.transform.position)) {
            RestoreHero(wait);
        }

        foreach (var pair in wait.Invincible) {
            if (pair.Key != null) {
                pair.Key.IsInvincible = pair.Value;
            }
        }

        foreach (var gate in wait.LockedGates) {
            if (gate.GameObject != null && !_closedGates.ContainsKey(gate)) {
                _unclaimedGates.Add(new UnclaimedGate(gate, Time.unscaledTime + UnclaimedGateTime));
            }
        }

        Logger.Info($"Boss room '{GetRoomPath(room)}' doesn't wait anymore");
    }

    /// <summary>
    /// Stops waiting in the boss room of an FSM, right before what waited in it goes on.
    /// </summary>
    private void EndRoomWaitFor(Fsm fsm) {
        if (GetBossRoom(fsm) is { } room) {
            EndRoomWait(room, true);
        }
    }

    /// <summary>
    /// Whether something in a boss room waits for the other players: an event or a trigger that starts its fight, the
    /// fight after its dialogue, or an event that the local player passed on to the scene host.
    /// </summary>
    private bool IsRoomHeld(BossRoom room) {
        return room.ForwardPending ||
               _heldEventStarts.Keys.Any(fsm => GetBossRoom(fsm) == room) ||
               _heldStarts.Keys.Any(fsm => GetBossRoom(fsm) == room) ||
               _heldFights.Keys.Any(fsm => GetBossRoom(fsm) == room);
    }

    /// <summary>
    /// Whether the local player is in a boss room that waits for the other players.
    /// </summary>
    public bool IsLocalHeroInWaitingRoom() {
        var heroController = HeroController.instance;
        return heroController != null && _bossRooms.Values.Any(room =>
            room.Wait is { } wait && wait.Shape.Contains(heroController.transform.position)
        );
    }

    /// <summary>
    /// Keeps the local player free to move and inside the boss room they wait in, and stops waiting in rooms that don't
    /// wait anymore.
    /// </summary>
    private void UpdateRoomWaits() {
        OpenUnclaimedGates();
        if (!_bossRooms.Values.Any(room => room.Wait != null)) {
            return;
        }

        // Finding the rooms of held FSMs may read new rooms
        foreach (var room in _bossRooms.Values.ToList()) {
            if (room.Wait is not { } wait) {
                continue;
            }

            // An event that the local player passed on waits at the scene host until everyone is in the room
            var isForwardDone = room.ForwardPending && (room.FightBegan || AreAllInRoom(wait.Shape));
            if (isForwardDone) {
                room.ForwardPending = false;
                room.Started = true;
                OnBossFightStarting(room);
            }

            if (!IsRoomHeld(room)) {
                EndRoomWait(room, isForwardDone);
                continue;
            }

            var heroController = HeroController.instance;
            if (heroController == null || !wait.Shape.Contains(heroController.transform.position)) {
                wait.RestrictedSince = null;
                continue;
            }

            if (!wait.HintShown) {
                wait.HintShown = true;
                UiManager.InternalChatBox.AddMessage(GiveUpHintMessage);
            }

            if (!wait.GaveUp) {
                LockGates(room, wait);
            }

            KeepHeroFree(heroController, wait);
        }
    }

    /// <summary>
    /// Gives control back to the local player once the intro of their waiting room kept it for a while, since the fight
    /// that the intro leads to waits for the other players.
    /// </summary>
    private static void KeepHeroFree(HeroController heroController, RoomWait wait) {
        var isRestricted = heroController.controlReqlinquished ||
                           (heroController.AnimCtrl != null && !heroController.AnimCtrl.controlEnabled) ||
                           PlayerData.instance is { disablePause: true };

        // Dialogue, dying and changing scenes need the control that they take
        if (!isRestricted || IsDialogueRunning() == true || heroController.cState.dead ||
            global::GameManager.instance is { IsInSceneTransition: true }) {
            wait.RestrictedSince = null;
            return;
        }

        if (wait.RestrictedSince is not { } restrictedSince) {
            wait.RestrictedSince = Time.unscaledTime;
            return;
        }

        if (Time.unscaledTime - restrictedSince < FreeHeroDelay) {
            return;
        }

        wait.RestrictedSince = null;
        Logger.Info("The intro of the waiting room kept control from the local player, giving it back");
        FreeHero(wait);
    }

    /// <summary>
    /// Closes the open gates of a waiting room for the local player inside it, so that they can't leave before the
    /// fight. The other players' games keep their own gates open.
    /// </summary>
    private void LockGates(BossRoom room, RoomWait wait) {
        foreach (var gate in room.Gates) {
            if (gate.GameObject == null || !gate.GameObject.activeInHierarchy || wait.LockedGates.Contains(gate) ||
                GetGateEventName(gate, GateCloseEvents) is not { } closeEvent) {
                continue;
            }

            wait.LockedGates.Add(gate);
            Logger.Info($"Closing gate '{GetPath(gate)}' to keep the local player in the waiting room");
            SendGateEvent(gate, closeEvent);
        }
    }

    /// <summary>
    /// Opens the gates that kept the local player in a room that started, once the fight had time to close them itself
    /// and didn't.
    /// </summary>
    private void OpenUnclaimedGates() {
        for (var i = _unclaimedGates.Count - 1; i >= 0; i--) {
            var unclaimed = _unclaimedGates[i];
            if (unclaimed.Gate.GameObject == null || _closedGates.ContainsKey(unclaimed.Gate)) {
                _unclaimedGates.RemoveAt(i);
                continue;
            }

            if (Time.unscaledTime < unclaimed.OpenTime) {
                continue;
            }

            _unclaimedGates.RemoveAt(i);
            if (GetGateEventName(unclaimed.Gate, GateOpenEvents) is { } openEvent) {
                Logger.Info($"The fight didn't close gate '{GetPath(unclaimed.Gate)}', opening it again");
                SendGateEvent(unclaimed.Gate, openEvent);
            }
        }
    }

    /// <summary>
    /// Opens the gates of the boss room that the local player waits in, so that they can leave. The boss keeps waiting
    /// until every player is in the room.
    /// </summary>
    public void GiveUpWaiting() {
        var heroController = HeroController.instance;
        var gaveUp = false;
        foreach (var room in _bossRooms.Values) {
            if (room.Wait is not { } wait || heroController == null ||
                !wait.Shape.Contains(heroController.transform.position)) {
                continue;
            }

            gaveUp = true;
            wait.GaveUp = true;
            wait.RestrictedSince = null;
            FreeHero(wait);

            foreach (var gate in room.Gates) {
                if (gate.GameObject == null || GetGateEventName(gate, GateOpenEvents) is not { } openEvent) {
                    continue;
                }

                _closedGates.Remove(gate);
                RemovePendingClose(gate);
                Logger.Info($"Opening gate '{GetPath(gate)}', since the local player gave up waiting");
                SendGateEvent(gate, openEvent);
            }
        }

        UiManager.InternalChatBox.AddMessage(gaveUp ? GaveUpMessage : NothingToGiveUpMessage);
    }

    /// <summary>
    /// Stops waiting in the boss rooms of the current scene, like after disconnecting, and opens the gates that kept
    /// the local player in if they are still there.
    /// </summary>
    private void ClearRoomWaits() {
        foreach (var room in _bossRooms.Values) {
            // The scene may be changing, so the local player's control stays as it is
            if (room.Root != null) {
                EndRoomWait(room, false);
            } else {
                room.Wait = null;
            }
        }

        foreach (var unclaimed in _unclaimedGates) {
            if (unclaimed.Gate.GameObject != null &&
                GetGateEventName(unclaimed.Gate, GateOpenEvents) is { } openEvent) {
                SendGateEvent(unclaimed.Gate, openEvent);
            }
        }

        _unclaimedGates.Clear();
    }

    /// <summary>
    /// Remembers that the boss room of a gate started because the gate closed. A room whose boss waits for every player
    /// doesn't start that way, since its gates close in each player's own intro before the boss starts.
    /// </summary>
    private void MarkRoomStartedByGate(Fsm gate) {
        if (GetBossRoom(gate) is { HasBossStarts: false } room) {
            room.Started = true;
            OnBossFightStarting(room);
        }
    }

    /// <summary>
    /// Whether the local player is in the room of an FSM: inside the camera locks of its boss room, or else inside the
    /// triggers of the FSM. Without either to check, they count as inside.
    /// </summary>
    private bool IsLocalHeroInBossRoom(Fsm fsm) {
        var heroController = HeroController.instance;
        if (heroController == null) {
            return false;
        }

        var position = (Vector2) heroController.transform.position;
        if (GetBossRoom(fsm) is { LockAreas.Count: > 0 } room) {
            return IsInRegions(room.LockAreas, position);
        }

        var regions = GetRegions(fsm);
        return regions.Count == 0 || IsInRegions(regions, position);
    }

    /// <summary>
    /// Gets the first of the given events that a gate reacts to in its current state, or null if it reacts to none of
    /// them, like a closed gate to an event that closes it.
    /// </summary>
    private static string? GetGateEventName(Fsm gate, string[] eventNames) {
        var transitions = gate.ActiveState?.Transitions;
        if (transitions == null) {
            return null;
        }

        foreach (var eventName in eventNames) {
            if (transitions.Any(transition => transition.EventName == eventName)) {
                return eventName;
            }
        }

        return null;
    }

    /// <summary>
    /// Sends an event to a gate past the rules for gates, since this class decides about the gate itself.
    /// </summary>
    private void SendGateEvent(Fsm gate, string eventName) {
        _bypassGates = true;
        try {
            gate.Event(eventName);
        } finally {
            _bypassGates = false;
        }
    }

    /// <summary>
    /// Gets the path of the root object of a boss room, for logging.
    /// </summary>
    private static string GetRoomPath(BossRoom room) {
        return room.Root != null ? ScenePath.Get(room.Root) : "(unloaded)";
    }

    /// <summary>
    /// A boss room that waits for the other players, and what it changed for the local player meanwhile.
    /// </summary>
    private sealed class RoomWait : FreedHero {
        /// <summary>
        /// The part of the room that players must be in.
        /// </summary>
        public readonly RoomShape Shape;

        /// <summary>
        /// The gates that were closed to keep the local player in the room.
        /// </summary>
        public readonly List<Fsm> LockedGates = [];

        /// <summary>
        /// The bosses that can't be hurt while the room waits, with whether each of them was invincible before.
        /// </summary>
        public readonly Dictionary<HealthManager, bool> Invincible = new();

        /// <summary>
        /// Whether the local player gave up waiting, after which the gates stay open for them.
        /// </summary>
        public bool GaveUp;

        /// <summary>
        /// Whether the local player was told how to give up waiting.
        /// </summary>
        public bool HintShown;

        /// <summary>
        /// Since when, in unscaled seconds, the local player has been without control, or null while they have it.
        /// </summary>
        public float? RestrictedSince;

        public RoomWait(RoomShape shape) {
            Shape = shape;
        }
    }

    /// <summary>
    /// A gate that kept the local player in a room that started, which opens again unless the fight closes it.
    /// </summary>
    private sealed class UnclaimedGate {
        /// <summary>
        /// The FSM of the gate.
        /// </summary>
        public readonly Fsm Gate;

        /// <summary>
        /// When the gate opens again, in unscaled seconds.
        /// </summary>
        public readonly float OpenTime;

        public UnclaimedGate(Fsm gate, float openTime) {
            Gate = gate;
            OpenTime = openTime;
        }
    }
}
