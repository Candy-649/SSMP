using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Cage lifts (<see cref="LiftControl"/>) in a checked two-player save (see CoopSave.Lifts). Every ride and call of one
/// goes through <see cref="LiftControl.MoveToStop"/>: the trigger and the button inside move it to the next stop, and a
/// call plate moves it to the stop of the plate.
/// </summary>
internal partial class CoopSave {
    private static readonly FieldInfo? LiftMoveRoutineField = typeof(LiftControl).GetField("moveRoutine", InstanceFlags);
    private static readonly FieldInfo? LiftCurrentStopField = typeof(LiftControl).GetField("currentStop", InstanceFlags);
    private static readonly FieldInfo? LiftUnlockedField = typeof(LiftControl).GetField("isUnlocked", InstanceFlags);
    private static readonly FieldInfo? LiftStopsField = typeof(LiftControl).GetField("stops", InstanceFlags);
    private static readonly FieldInfo? LiftMoveDelayField = typeof(LiftControl).GetField("moveDelay", InstanceFlags);

    private static readonly FieldInfo? LiftDoorTriggerField =
        typeof(LiftControl).GetField("doorCloseTrigger", InstanceFlags);

    private static readonly FieldInfo? LiftDoorButtonField = typeof(LiftControl).GetField("doorCloseButton", InstanceFlags);
    private static readonly FieldInfo? LiftBobPlatformField = typeof(LiftControl).GetField("bobPlat", InstanceFlags);

    /// <summary>
    /// How far a cage lift may move in one frame, above which a call plate moved it closer to its stop at once.
    /// </summary>
    private const float LiftTeleportHeight = 1f;

    private static readonly MethodInfo? LiftSetInitialPosMethod =
        typeof(LiftControl).GetMethod("SetInitialPos", InstanceFlags, null, Type.EmptyTypes, null);

    private static readonly Type? LiftStopType = typeof(LiftControl).GetNestedType("LiftStop", BindingFlags.NonPublic);
    private static readonly FieldInfo? LiftStopPosField = LiftStopType?.GetField("PosY", InstanceFlags);
    private static readonly FieldInfo? LiftStopPlatesField = LiftStopType?.GetField("CallPlates", InstanceFlags);

    private static readonly FieldInfo? StickInsideTrackerField =
        typeof(HeroPlatformStick).GetField("insideTracker", InstanceFlags);

    /// <summary>
    /// A cage lift that moves between stops.
    /// </summary>
    private class CageLift : SyncedLift {
        /// <summary>
        /// The lift.
        /// </summary>
        public required LiftControl Control { get; init; }

        /// <summary>
        /// The space inside the cage, relative to the position of the lift.
        /// </summary>
        public required Rect Inside { get; init; }

        /// <summary>
        /// The height of the lift in the last frame, from which a call plate moves it closer to its stop.
        /// </summary>
        public float LastFrameY { get; set; }

        /// <inheritdoc />
        public override MonoBehaviour Owner => Control;

        /// <inheritdoc />
        public override string FsmName => "";

        /// <inheritdoc />
        public override bool IsMoving => LiftMoveRoutineField?.GetValue(Control) != null;

        /// <inheritdoc />
        public override bool IsUnlocked => LiftUnlockedField?.GetValue(Control) is true;

        /// <inheritdoc />
        public override int Stop => LiftCurrentStopField?.GetValue(Control) is int stop ? stop : -1;

        /// <inheritdoc />
        public override bool HasStop(int stop) => GetStopData(stop) != null;

        /// <inheritdoc />
        public override bool Contains(Vector3 position) {
            var origin = Control.transform.position;
            return Inside.Contains(new Vector2(position.x - origin.x, position.y - origin.y));
        }

        /// <inheritdoc />
        public override bool IsAtStop(int stop, Vector3 position) {
            if (GetStopData(stop) is not { } stopData) {
                return false;
            }

            if (LiftStopPlatesField?.GetValue(stopData) is IList { Count: > 0 } plates) {
                foreach (var plate in plates) {
                    if (plate is Component component && component != null &&
                        IsWithinCallReach(component.transform.position, position)) {
                        return true;
                    }
                }

                return false;
            }

            var stopY = LiftStopPosField?.GetValue(stopData) is float y ? y : Control.transform.position.y;
            return IsWithinCallReach(new Vector3(Control.transform.position.x, stopY), position);
        }

        /// <inheritdoc />
        public override bool Move(int stop, bool camera, float skippedDelay) {
            if (Stop == stop) {
                return IsMoving;
            }

            var delay = LiftMoveDelayField?.GetValue(Control) is float value ? value : 0f;
            LiftMoveDelayField?.SetValue(Control, Mathf.Max(0f, delay - skippedDelay));
            try {
                Control.MoveToStop(stop, camera);
            } finally {
                LiftMoveDelayField?.SetValue(Control, delay);
            }

            return IsMoving && Stop == stop;
        }

        /// <inheritdoc />
        public override void PlaceAt(int stop, float value) {
            if (IsMoving) {
                Control.StopMoving();

                // A stopped ride leaves off what it turned off, which the end of the ride turns on again. This only
                // runs while the local hero isn't in the lift, so the button can't start a ride
                if (LiftBobPlatformField?.GetValue(Control) is Behaviour bobPlatform && bobPlatform != null) {
                    bobPlatform.enabled = true;
                }

                if (LiftDoorButtonField?.GetValue(Control) is SimpleButton button && button != null) {
                    button.SetLocked(false);
                }
            }

            LiftCurrentStopField?.SetValue(Control, stop);
            LiftSetInitialPosMethod?.Invoke(Control, null);
            LastFrameY = Control.transform.position.y;
        }

        /// <inheritdoc />
        public override void JoinRide(int stop, float value) {
            if (IsMoving) {
                Control.StopMoving();
            }

            SetHeight(value);
            if (Stop == stop) {
                // Moving to the stop that the lift is at only opens its doors
                LiftCurrentStopField?.SetValue(Control, stop == 0 ? 1 : 0);
            }

            Move(stop, false, float.MaxValue);
        }

        /// <inheritdoc />
        public override void Unlock() {
            if (!IsUnlocked) {
                Control.SetUnlocked(true);
            }
        }

        /// <inheritdoc />
        public override void Update() {
            LastFrameY = Control.transform.position.y;
        }

        /// <inheritdoc />
        public override void OnCallHeld() => RestoreStandingHeight();

        /// <inheritdoc />
        public override void BeforeLocalRide(bool partnerInside) {
            // A call plate that moved the lift closer can't move it away from under the partner
            if (partnerInside) {
                RestoreStandingHeight();
            }
        }

        /// <summary>
        /// Puts the lift back to its height from before a call plate moved it closer to its stop, also while it waits at
        /// a stop before or after a ride. During the ride itself the ride sets the height every frame anyway.
        /// </summary>
        private void RestoreStandingHeight() {
            var transform = Control.transform;
            var position = transform.position;
            if (Mathf.Abs(position.y - LastFrameY) > LiftTeleportHeight) {
                position.y = LastFrameY;
                transform.position = position;
            }
        }

        /// <summary>
        /// Gets the data of a stop, or null if the lift has no such stop.
        /// </summary>
        private object? GetStopData(int stop) {
            return LiftStopsField?.GetValue(Control) is Array stops && stop >= 0 && stop < stops.Length
                ? stops.GetValue(stop)
                : null;
        }
    }

    /// <summary>
    /// Registers the hooks of cage lifts.
    /// </summary>
    private void RegisterCageLiftHooks() {
        AddLiftHook(
            typeof(LiftControl).GetMethod("MoveToStop", InstanceFlags, null, [typeof(int), typeof(bool)], null),
            new Action<Action<LiftControl, int, bool>, LiftControl, int, bool>(OnLiftMoveToStop)
        );
    }

    /// <summary>
    /// Hook for <see cref="LiftControl.MoveToStop"/>, which every ride and call of a cage lift goes through.
    /// </summary>
    private void OnLiftMoveToStop(Action<LiftControl, int, bool> orig, LiftControl self, int stopIndex, bool camera) {
        ClientPlayerData? partner;
        if (_liftReplaying || (partner = GetLiftPartner()) == null) {
            orig(self, stopIndex, camera);
            return;
        }

        CageLift lift;
        try {
            lift = GetCageLift(self);
            if (!lift.IsUnlocked || stopIndex == lift.Stop || !lift.HasStop(stopIndex)) {
                orig(self, stopIndex, camera);
                return;
            }
        } catch (Exception e) {
            LogLiftError(e);
            orig(self, stopIndex, camera);
            return;
        }

        try {
            var hero = HeroController.instance;
            RequestLiftRide(lift, stopIndex, hero != null && lift.ContainsHero(hero), camera, partner);
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Gets the sync of a cage lift, looking at it the first time.
    /// </summary>
    private CageLift GetCageLift(LiftControl control) {
        if (_lifts.TryGetValue(control, out var existing) && existing is CageLift cage) {
            return cage;
        }

        cage = new CageLift {
            Path = ScenePath.Get(control.transform),
            Control = control,
            Inside = GetCageSpace(control),
            LastFrameY = control.transform.position.y
        };
        cage.WasMoving = cage.IsMoving;
        _lifts[control] = cage;
        return cage;
    }

    /// <summary>
    /// Adds the cage lifts of the current room.
    /// </summary>
    private void AddRoomCageLifts(List<SyncedLift> lifts) {
        foreach (var control in Object.FindObjectsByType<LiftControl>(FindObjectsInactive.Exclude,
                     FindObjectsSortMode.None)) {
            lifts.Add(GetCageLift(control));
        }
    }

    /// <summary>
    /// Finds the space inside a cage lift, relative to its position: the trigger that tracks the hero on its floor, or
    /// the trigger that closes its doors, or all its colliders.
    /// </summary>
    private static Rect GetCageSpace(LiftControl control) {
        Bounds? bounds = null;
        foreach (var stick in control.GetComponentsInChildren<HeroPlatformStick>()) {
            if (StickInsideTrackerField?.GetValue(stick) is Component tracker && tracker != null &&
                tracker.TryGetComponent<Collider2D>(out var collider)) {
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
}
