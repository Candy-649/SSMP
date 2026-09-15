using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Lifts in a checked two-player save, which are in the same place in both games. While both players are in a room, the
/// game of the player with the larger save key decides about its lifts: it plays the rides that its player starts and
/// serves the calls that the other game sends instead of starting a ride itself, and the other game plays the same
/// rides. A started ride goes at once, and a call from the other stop waits until the lift arrived. A lift waits at a
/// stop for a short time while the other player is in it or at that stop, then serves a call if the caller still waits.
/// A player who enters a room takes the lifts from the game of a partner who is in it. Nobody is moved: the avatar of a
/// partner who rides a lift moves with the lift.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How long a lift waits at a stop after it arrived, in seconds, while the other player is in it or at that stop.
    /// </summary>
    private const float LiftWaitTime = 2.5f;

    /// <summary>
    /// How far a caller may be from a call plate of a stop, sideways and up or down, and still wait there.
    /// </summary>
    private static readonly Vector2 LiftCallReach = new(8f, 4f);

    /// <summary>
    /// How long a call of a lift that waits for its turn lives, in seconds.
    /// </summary>
    private const float LiftCallLifetime = 30f;

    /// <summary>
    /// How long after entering a room the game asks for the state of its lifts, in seconds, so the lifts started.
    /// </summary>
    private const float LiftStateRequestDelay = 0.5f;

    /// <summary>
    /// How long a game that asked for the state of the lifts of its room doesn't decide about them, in seconds, unless
    /// the state arrives first.
    /// </summary>
    private const float LiftStateWaitTime = 2f;

    /// <summary>
    /// The height difference below which a lift counts as in the same place in both games.
    /// </summary>
    private const float LiftSameHeight = 0.5f;

    private static readonly FieldInfo? LiftMoveRoutineField = typeof(LiftControl).GetField("moveRoutine", InstanceFlags);
    private static readonly FieldInfo? LiftCurrentStopField = typeof(LiftControl).GetField("currentStop", InstanceFlags);
    private static readonly FieldInfo? LiftUnlockedField = typeof(LiftControl).GetField("isUnlocked", InstanceFlags);
    private static readonly FieldInfo? LiftStopsField = typeof(LiftControl).GetField("stops", InstanceFlags);
    private static readonly FieldInfo? LiftMoveDelayField = typeof(LiftControl).GetField("moveDelay", InstanceFlags);

    private static readonly FieldInfo? LiftDoorTriggerField =
        typeof(LiftControl).GetField("doorCloseTrigger", InstanceFlags);

    private static readonly MethodInfo? LiftSetInitialPosMethod =
        typeof(LiftControl).GetMethod("SetInitialPos", InstanceFlags, null, Type.EmptyTypes, null);

    private static readonly Type? LiftStopType = typeof(LiftControl).GetNestedType("LiftStop", BindingFlags.NonPublic);
    private static readonly FieldInfo? LiftStopPosField = LiftStopType?.GetField("PosY", InstanceFlags);
    private static readonly FieldInfo? LiftStopPlatesField = LiftStopType?.GetField("CallPlates", InstanceFlags);

    private static readonly FieldInfo? StickInsideTrackerField =
        typeof(HeroPlatformStick).GetField("insideTracker", InstanceFlags);

    /// <summary>
    /// The hooks of lifts.
    /// </summary>
    private readonly List<Hook> _liftHooks = [];

    /// <summary>
    /// The cage lifts of the current room that the sync looked at.
    /// </summary>
    private readonly Dictionary<LiftControl, CageLift> _cages = new();

    /// <summary>
    /// Whether the sync itself moves a lift, which its hooks let through.
    /// </summary>
    private bool _liftReplaying;

    /// <summary>
    /// The count of the rides that the local game started, which tells newer rides from older ones.
    /// </summary>
    private ulong _liftRideCount;

    /// <summary>
    /// When the local player entered the current room.
    /// </summary>
    private float _liftRoomStart;

    /// <summary>
    /// Whether the state of the lifts of the current room was asked for.
    /// </summary>
    private bool _liftStateRequested;

    /// <summary>
    /// Whether a partner sent the state of the lifts of the current room.
    /// </summary>
    private bool _liftStateReceived;

    /// <summary>
    /// Whether an error of lifts was logged, so that it isn't logged every frame.
    /// </summary>
    private bool _liftFailed;

    /// <summary>
    /// A cage lift that moves between stops.
    /// </summary>
    private class CageLift {
        /// <summary>
        /// The lift.
        /// </summary>
        public required LiftControl Control { get; init; }

        /// <summary>
        /// The path of the lift in its scene.
        /// </summary>
        public required string Path { get; init; }

        /// <summary>
        /// The space inside the cage, relative to the position of the lift.
        /// </summary>
        public Rect Inside { get; init; }

        /// <summary>
        /// Whether the lift moved in the last frame.
        /// </summary>
        public bool WasMoving { get; set; }

        /// <summary>
        /// When the lift last arrived at a stop.
        /// </summary>
        public float ArrivedAt { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// The height of the lift in the last frame in which it stood, from which a call plate moved it.
        /// </summary>
        public float StandingY { get; set; }

        /// <summary>
        /// The count of the newest ride of the partner that the lift played.
        /// </summary>
        public ulong PartnerRide { get; set; }

        /// <summary>
        /// The calls that wait for the lift, oldest first.
        /// </summary>
        public List<LiftCall> Calls { get; } = [];

        /// <summary>
        /// Whether the avatar of the partner rides the lift.
        /// </summary>
        public bool AvatarRiding { get; set; }

        /// <summary>
        /// Where the avatar of the partner is relative to the lift while it rides.
        /// </summary>
        public Vector3 AvatarOffset { get; set; }

        /// <summary>
        /// Where the sync last put the avatar of the partner.
        /// </summary>
        public Vector3 AvatarPlaced { get; set; }

        /// <summary>
        /// The container of the avatar that rides the lift, whose prediction the ride turned off.
        /// </summary>
        public GameObject? AvatarContainer { get; set; }
    }

    /// <summary>
    /// A call of a lift that waits for its turn.
    /// </summary>
    private class LiftCall {
        /// <summary>
        /// The stop that the lift goes to.
        /// </summary>
        public required int Stop { get; init; }

        /// <summary>
        /// Whether the partner called, rather than the local player.
        /// </summary>
        public required bool ByPartner { get; init; }

        /// <summary>
        /// Whether the caller is inside the lift and rides it, rather than waiting at the stop.
        /// </summary>
        public bool Inside { get; set; }

        /// <summary>
        /// When the call came.
        /// </summary>
        public float Time { get; set; }
    }

    /// <summary>
    /// Registers the hooks of lifts.
    /// </summary>
    private void RegisterLiftHooks() {
        AddLiftHook(
            typeof(LiftControl).GetMethod("MoveToStop", InstanceFlags, null, [typeof(int), typeof(bool)], null),
            new Action<Action<LiftControl, int, bool>, LiftControl, int, bool>(OnLiftMoveToStop)
        );

        Application.onBeforeRender += OnLiftBeforeRender;
    }

    /// <summary>
    /// Creates a hook of lifts, logging instead of throwing when the method is missing.
    /// </summary>
    private void AddLiftHook(MethodInfo? method, Delegate detour) {
        if (CreateHook(method, detour) is { } hook) {
            _liftHooks.Add(hook);
        }
    }

    /// <summary>
    /// Logs the first error of lifts.
    /// </summary>
    private void LogLiftError(Exception e) {
        if (!_liftFailed) {
            _liftFailed = true;
            Logger.Error($"Could not sync a lift of the two-player save:\n{e}");
        }
    }

    /// <summary>
    /// Gets the partner with whom the lifts of the current room are synced, who is checked and in the room.
    /// </summary>
    private ClientPlayerData? GetLiftPartner() {
        return _checkedWith is { } id && _playerData.TryGetValue(id, out var partner) && partner.IsInLocalScene
            ? partner
            : null;
    }

    /// <summary>
    /// Whether the local game decides about the lifts of the current room.
    /// </summary>
    private bool DecidesLifts(ClientPlayerData? partner) {
        if (partner == null) {
            return true;
        }

        if (_liftStateRequested && !_liftStateReceived &&
            Time.unscaledTime - _liftRoomStart < LiftStateRequestDelay + LiftStateWaitTime) {
            return false;
        }

        return !PartnerKeyWins();
    }

    /// <summary>
    /// Forgets the lifts of the room that the local player left.
    /// </summary>
    private void OnLiftSceneChanged() {
        EndAvatarRides();
        _cages.Clear();
        _liftRoomStart = Time.unscaledTime;
        _liftStateRequested = false;
        _liftStateReceived = false;
    }

    /// <summary>
    /// Forgets the lifts, for when the session ends.
    /// </summary>
    private void ResetLifts() {
        EndAvatarRides();
        _cages.Clear();
        _liftStateRequested = false;
        _liftStateReceived = false;
    }

    /// <summary>
    /// Asks the partner for the state of the lifts after entering a room, notices when lifts arrive, and serves the calls
    /// that wait.
    /// </summary>
    private void UpdateLifts(ClientPlayerData partner) {
        try {
            if (!_liftStateRequested && Time.unscaledTime - _liftRoomStart >= LiftStateRequestDelay) {
                _liftStateRequested = true;
                if (RoomHasLifts()) {
                    Send(new CoopSaveUpdate {
                        TargetId = partner.Id,
                        Kind = CoopSaveUpdateKind.LiftStateRequest,
                        Scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
                    });
                } else {
                    _liftStateReceived = true;
                }
            }

            var liftPartner = partner.IsInLocalScene ? partner : null;
            var decides = DecidesLifts(liftPartner);
            List<LiftControl>? gone = null;
            foreach (var lift in _cages.Values) {
                if (lift.Control == null) {
                    (gone ??= []).Add(lift.Control!);
                    continue;
                }

                UpdateCage(lift, liftPartner, decides);
            }

            if (gone != null) {
                foreach (var control in gone) {
                    _cages.Remove(control);
                }
            }
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Notices when a cage lift arrives and serves its calls that wait when the local game decides about it.
    /// </summary>
    private void UpdateCage(CageLift lift, ClientPlayerData? partner, bool decides) {
        var control = lift.Control;
        var moving = IsLiftMoving(control);
        if (lift.WasMoving && !moving) {
            lift.ArrivedAt = Time.unscaledTime;
        }

        lift.WasMoving = moving;
        if (!moving) {
            lift.StandingY = control.transform.position.y;
        }

        if (!decides) {
            lift.Calls.Clear();
            return;
        }

        if (moving || lift.Calls.Count == 0) {
            return;
        }

        var currentStop = GetLiftStop(control);
        lift.Calls.RemoveAll(call =>
            call.Stop == currentStop || Time.unscaledTime - call.Time > LiftCallLifetime ||
            !IsCallerWaiting(lift, call, partner)
        );
        if (lift.Calls.Count == 0) {
            return;
        }

        var next = lift.Calls[0];
        if (IsLiftHeldForOther(lift, next.ByPartner, partner)) {
            return;
        }

        StartLiftRide(lift, next.Stop, IsLocalHeroInside(lift), partner);
    }

    /// <summary>
    /// Hook for <see cref="LiftControl.MoveToStop"/>, which every ride and call of a cage lift goes through. When the
    /// local game decides, it starts the ride at once or lets it wait for its turn; otherwise it sends the call to the
    /// game that decides.
    /// </summary>
    private void OnLiftMoveToStop(Action<LiftControl, int, bool> orig, LiftControl self, int stopIndex, bool camera) {
        ClientPlayerData? partner;
        if (_liftReplaying || (partner = GetLiftPartner()) == null) {
            orig(self, stopIndex, camera);
            return;
        }

        try {
            var lift = GetCage(self);
            if (!IsLiftUnlocked(self) || stopIndex == GetLiftStop(self)) {
                orig(self, stopIndex, camera);
                return;
            }

            var inside = IsLocalHeroInside(lift);
            var moving = IsLiftMoving(self);
            if (!DecidesLifts(partner)) {
                RestoreStandingHeight(lift, moving);
                Send(new CoopSaveUpdate {
                    TargetId = partner.Id,
                    Kind = CoopSaveUpdateKind.LiftCall,
                    Scene = self.gameObject.scene.name,
                    ObjectPath = lift.Path,
                    Part = (ushort) stopIndex,
                    PartCount = (ushort) (inside ? 1 : 0)
                });
                return;
            }

            if (moving || IsLiftHeldForOther(lift, false, partner)) {
                RestoreStandingHeight(lift, moving);
                QueueLiftCall(lift, stopIndex, false, inside);
                return;
            }

            // A call plate that moved the lift closer can't move it away from under the partner
            if (IsAvatarInside(lift, partner)) {
                RestoreStandingHeight(lift, false);
            }

            StartLiftRide(lift, stopIndex, camera, partner);
        } catch (Exception e) {
            LogLiftError(e);
            orig(self, stopIndex, camera);
        }
    }

    /// <summary>
    /// Puts a standing lift back to its height from before a call plate moved it closer to its stop.
    /// </summary>
    private static void RestoreStandingHeight(CageLift lift, bool moving) {
        var transform = lift.Control.transform;
        var position = transform.position;
        if (!moving && Mathf.Abs(position.y - lift.StandingY) > 0.01f) {
            position.y = lift.StandingY;
            transform.position = position;
        }
    }

    /// <summary>
    /// Lets a call wait for the lift, replacing an earlier call of the same player to the same stop.
    /// </summary>
    private static void QueueLiftCall(CageLift lift, int stop, bool byPartner, bool inside) {
        var call = lift.Calls.Find(other => other.Stop == stop && other.ByPartner == byPartner);
        if (call == null) {
            call = new LiftCall { Stop = stop, ByPartner = byPartner };
            lift.Calls.Add(call);
        }

        call.Inside = inside;
        call.Time = Time.unscaledTime;
    }

    /// <summary>
    /// Starts a ride of a lift in the local game and sends it to the partner.
    /// </summary>
    private void StartLiftRide(CageLift lift, int stop, bool camera, ClientPlayerData? partner) {
        var control = lift.Control;
        var fromY = control.transform.position.y;
        _liftReplaying = true;
        try {
            control.MoveToStop(stop, camera);
        } finally {
            _liftReplaying = false;
        }

        if (!IsLiftMoving(control) || GetLiftStop(control) != stop) {
            return;
        }

        lift.WasMoving = true;
        lift.Calls.RemoveAll(call => call.Stop == stop);
        if (partner == null) {
            return;
        }

        Send(new CoopSaveUpdate {
            TargetId = partner.Id,
            Kind = CoopSaveUpdateKind.LiftMove,
            Scene = control.gameObject.scene.name,
            ObjectPath = lift.Path,
            Part = (ushort) stop,
            Key = ++_liftRideCount,
            Values = [fromY]
        });
    }

    /// <summary>
    /// Whether a lift that arrived a moment ago still waits for the player other than the one who wants it, who is in it
    /// or at its stop.
    /// </summary>
    private bool IsLiftHeldForOther(CageLift lift, bool byPartner, ClientPlayerData? partner) {
        if (Time.unscaledTime - lift.ArrivedAt >= LiftWaitTime) {
            return false;
        }

        if (byPartner) {
            return IsLocalHeroInside(lift) ||
                   HeroController.instance is { } hero && IsNearLiftStop(lift, GetLiftStop(lift.Control),
                       hero.transform.position);
        }

        return partner != null && (IsAvatarInside(lift, partner) ||
                                   partner.PlayerContainer is { } container &&
                                   IsNearLiftStop(lift, GetLiftStop(lift.Control), container.transform.position));
    }

    /// <summary>
    /// Whether the player of a call still waits for the lift: inside it for a ride, or at the stop for a call.
    /// </summary>
    private bool IsCallerWaiting(CageLift lift, LiftCall call, ClientPlayerData? partner) {
        Vector3 position;
        bool inside;
        if (call.ByPartner) {
            if (partner?.PlayerContainer is not { } container) {
                return false;
            }

            position = container.transform.position;
            inside = IsAvatarInside(lift, partner);
        } else {
            if (HeroController.instance is not { } hero) {
                return false;
            }

            position = hero.transform.position;
            inside = IsLocalHeroInside(lift);
        }

        return call.Inside ? inside : !inside && IsNearLiftStop(lift, call.Stop, position);
    }

    /// <summary>
    /// Whether a position is close to a call plate of a stop of a lift, or to the stop itself if it has no plates.
    /// </summary>
    private static bool IsNearLiftStop(CageLift lift, int stop, Vector3 position) {
        if (LiftStopsField?.GetValue(lift.Control) is not Array stops || stop < 0 || stop >= stops.Length) {
            return false;
        }

        var stopData = stops.GetValue(stop);
        if (LiftStopPlatesField?.GetValue(stopData) is IList plates && plates.Count > 0) {
            foreach (var plate in plates) {
                if (plate is Component component && component != null &&
                    IsWithinReach(component.transform.position, position)) {
                    return true;
                }
            }

            return false;
        }

        var stopY = LiftStopPosField?.GetValue(stopData) is float y ? y : lift.Control.transform.position.y;
        return IsWithinReach(new Vector3(lift.Control.transform.position.x, stopY), position);
    }

    /// <summary>
    /// Whether a position is within the reach of a caller around a point.
    /// </summary>
    private static bool IsWithinReach(Vector3 point, Vector3 position) {
        return Mathf.Abs(point.x - position.x) <= LiftCallReach.x && Mathf.Abs(point.y - position.y) <= LiftCallReach.y;
    }

    /// <summary>
    /// Whether the local hero is inside a cage lift.
    /// </summary>
    private static bool IsLocalHeroInside(CageLift lift) {
        var hero = HeroController.instance;
        if (hero == null) {
            return false;
        }

        return hero.transform.IsChildOf(lift.Control.transform) || IsInsideCage(lift, hero.transform.position);
    }

    /// <summary>
    /// Whether the avatar of the partner is inside a cage lift, or rides it.
    /// </summary>
    private static bool IsAvatarInside(CageLift lift, ClientPlayerData partner) {
        return lift.AvatarRiding ||
               partner.PlayerContainer is { } container && IsInsideCage(lift, container.transform.position);
    }

    /// <summary>
    /// Whether a position is inside the space of a cage lift where it is now.
    /// </summary>
    private static bool IsInsideCage(CageLift lift, Vector3 position) {
        var origin = lift.Control.transform.position;
        return lift.Inside.Contains(new Vector2(position.x - origin.x, position.y - origin.y));
    }

    /// <summary>
    /// Gets the sync of a cage lift, looking at it the first time.
    /// </summary>
    private CageLift GetCage(LiftControl control) {
        if (_cages.TryGetValue(control, out var lift)) {
            return lift;
        }

        lift = new CageLift {
            Control = control,
            Path = ScenePath.Get(control.transform),
            Inside = GetCageSpace(control),
            WasMoving = IsLiftMoving(control),
            StandingY = control.transform.position.y
        };
        _cages[control] = lift;
        return lift;
    }

    /// <summary>
    /// Finds the space inside a cage lift, relative to its position: the trigger that tracks the hero on its floor, or
    /// the trigger that closes its doors, or all its colliders.
    /// </summary>
    private static Rect GetCageSpace(LiftControl control) {
        Bounds? bounds = null;
        foreach (var stick in control.GetComponentsInChildren<HeroPlatformStick>()) {
            if (StickInsideTrackerField?.GetValue(stick) is Component tracker && tracker != null &&
                tracker.GetComponent<Collider2D>() is { } collider) {
                bounds = Encapsulate(bounds, collider.bounds);
            }
        }

        if (bounds == null && LiftDoorTriggerField?.GetValue(control) is Component trigger && trigger != null) {
            foreach (var collider in trigger.GetComponents<Collider2D>()) {
                bounds = Encapsulate(bounds, collider.bounds);
            }
        }

        if (bounds == null) {
            foreach (var collider in control.GetComponentsInChildren<Collider2D>()) {
                bounds = Encapsulate(bounds, collider.bounds);
            }
        }

        var origin = control.transform.position;
        if (bounds is not { } found || found.size.x < 0.5f || found.size.y < 0.5f) {
            Logger.Warn($"Could not find the inside of the lift {control.name}, using a guess");
            return new Rect(-2.5f, -1f, 5f, 6f);
        }

        // The hero stands on the floor, so its position can be a bit above the trigger
        return new Rect(
            found.min.x - origin.x - 0.5f, found.min.y - origin.y - 0.5f, found.size.x + 1f, found.size.y + 2f
        );
    }

    /// <summary>
    /// Grows bounds that may not exist yet to include other bounds.
    /// </summary>
    private static Bounds? Encapsulate(Bounds? bounds, Bounds other) {
        if (other.size == Vector3.zero) {
            return bounds;
        }

        if (bounds is not { } existing) {
            return other;
        }

        existing.Encapsulate(other);
        return existing;
    }

    private static bool IsLiftMoving(LiftControl control) => LiftMoveRoutineField?.GetValue(control) != null;

    private static int GetLiftStop(LiftControl control) =>
        LiftCurrentStopField?.GetValue(control) is int stop ? stop : -1;

    private static bool IsLiftUnlocked(LiftControl control) =>
        LiftUnlockedField?.GetValue(control) is true;
}
