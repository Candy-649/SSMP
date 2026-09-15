using System;
using System.Collections.Generic;
using System.Reflection;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Carriages (<see cref="ManualLift"/>) in a checked two-player save (see CoopSave.Lifts), which move sideways while a
/// player holds one of their buttons, or come to a call plate. The player who holds a button or steps on a plate first
/// drives the carriage: their game sends where it is several times a second, and the other game plays the same
/// movement and ignores the buttons of its player until a short time after the driver let go. A driver who holds the
/// carriage too long can be taken over by the other player.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The name that updates about carriages carry in place of the name of an FSM.
    /// </summary>
    private const string CarriageKind = "ManualLift";

    /// <summary>
    /// How long the buttons of a carriage stay ignored after the partner let go of it, in seconds.
    /// </summary>
    private const float CarriageReleaseTime = 0.5f;

    /// <summary>
    /// How long the partner can drive a carriage, in seconds, before the local player can take it over.
    /// </summary>
    private const float CarriageMaxHold = 10f;

    /// <summary>
    /// How often the driver sends where the carriage is, in seconds.
    /// </summary>
    private const float CarriageSendInterval = 1f / 15f;

    /// <summary>
    /// How long the game follows the last position that the partner sent, in seconds.
    /// </summary>
    private const float CarriageFollowTime = 1f;

    /// <summary>
    /// How far ahead the game guesses where the carriage of the partner is from its speed, in seconds at most.
    /// </summary>
    private const float CarriagePredictTime = 0.2f;

    /// <summary>
    /// The difference of the part of the way above which the carriage jumps to where the partner has it.
    /// </summary>
    private const float CarriageSnapPart = 0.1f;

    /// <summary>
    /// How fast the carriage catches up with where the partner has it, per second.
    /// </summary>
    private const float CarriageCatchUp = 5f;

    private static readonly FieldInfo? CarriageLeftPressedField = typeof(ManualLift).GetField("isLeftPressed", InstanceFlags);
    private static readonly FieldInfo? CarriageRightPressedField = typeof(ManualLift).GetField("isRightPressed", InstanceFlags);
    private static readonly FieldInfo? CarriageCalledField = typeof(ManualLift).GetField("calledDirection", InstanceFlags);
    private static readonly FieldInfo? CarriageDelayField = typeof(ManualLift).GetField("moveDelayLeft", InstanceFlags);
    private static readonly FieldInfo? CarriageVelocityField = typeof(ManualLift).GetField("currentVelocity", InstanceFlags);
    private static readonly FieldInfo? CarriagePosField = typeof(ManualLift).GetField("currentPosT", InstanceFlags);
    private static readonly FieldInfo? CarriageDirectionField = typeof(ManualLift).GetField("targetTDirection", InstanceFlags);
    private static readonly FieldInfo? CarriageSpeedFactorField = typeof(ManualLift).GetField("speedFactor", InstanceFlags);
    private static readonly FieldInfo? CarriageUnlockedField = typeof(ManualLift).GetField("isUnlocked", InstanceFlags);
    private static readonly FieldInfo? CarriageTransformField = typeof(ManualLift).GetField("moveTransform", InstanceFlags);
    private static readonly FieldInfo? CarriageLeftField = typeof(ManualLift).GetField("leftTargetPos", InstanceFlags);
    private static readonly FieldInfo? CarriageRightField = typeof(ManualLift).GetField("rightTargetPos", InstanceFlags);

    private static readonly MethodInfo? CarriageUpdateDirectionMethod =
        typeof(ManualLift).GetMethod("UpdateDirection", InstanceFlags, null, [typeof(bool)], null);

    private static readonly MethodInfo? CarriageUpdatePositionMethod =
        typeof(ManualLift).GetMethod("UpdatePosition", InstanceFlags, null, Type.EmptyTypes, null);

    private static readonly MethodInfo? CarriageUpdatePlatesMethod =
        typeof(ManualLift).GetMethod("UpdatePlates", InstanceFlags, null, Type.EmptyTypes, null);

    /// <summary>
    /// A carriage that moves sideways between its two ends, at stop 0 on the left and stop 1 on the right.
    /// </summary>
    private class Carriage : SyncedLift {
        /// <summary>
        /// The carriage.
        /// </summary>
        public required ManualLift Lift { get; init; }

        /// <summary>
        /// The object that moves.
        /// </summary>
        public required Transform Body { get; init; }

        /// <summary>
        /// The space in and on the carriage, relative to the position of the object that moves.
        /// </summary>
        public required Rect Inside { get; init; }

        /// <summary>
        /// Whether the local player drives the carriage.
        /// </summary>
        public bool LocalDriving { get; set; }

        /// <summary>
        /// The number of the drive of the local player, which is newer than every drive that it saw.
        /// </summary>
        public int LocalClaim { get; set; }

        /// <summary>
        /// The highest number of a drive that the game saw.
        /// </summary>
        public int SeenClaim { get; set; }

        /// <summary>
        /// Whether the partner drives the carriage.
        /// </summary>
        public bool PartnerDriving { get; set; }

        /// <summary>
        /// When the partner started to drive the carriage.
        /// </summary>
        public float PartnerDriveStart { get; set; }

        /// <summary>
        /// When the partner let go of the carriage.
        /// </summary>
        public float PartnerReleasedAt { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// Whether the buttons of the local player were ignored in the last frame.
        /// </summary>
        public bool WasFollowing { get; set; }

        /// <summary>
        /// The count of the updates of the drives of the local game.
        /// </summary>
        public ulong DriveCount { get; set; }

        /// <summary>
        /// The count of the newest update of a drive of the partner.
        /// </summary>
        public ulong PartnerDriveKey { get; set; }

        /// <summary>
        /// When the game last sent where the carriage is.
        /// </summary>
        public float SentAt { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// Whether the game sent that the carriage stopped.
        /// </summary>
        public bool SentStopped { get; set; } = true;

        /// <summary>
        /// When the last position from the partner came, or negative infinity before the first.
        /// </summary>
        public float PartnerTime { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// The part of the way, the speed and the direction of the carriage in the last update of the partner.
        /// </summary>
        public float PartnerPart { get; set; }

        /// <inheritdoc cref="PartnerPart" />
        public float PartnerVelocity { get; set; }

        /// <inheritdoc cref="PartnerPart" />
        public float PartnerDirection { get; set; }

        /// <summary>
        /// The part of the way from the left end to the right end where the carriage is.
        /// </summary>
        public float Part {
            get => CarriagePosField?.GetValue(Lift) is float value ? value : 0f;
            set => CarriagePosField?.SetValue(Lift, value);
        }

        /// <summary>
        /// The speed of the carriage, negative to the left.
        /// </summary>
        public float Velocity {
            get => CarriageVelocityField?.GetValue(Lift) is float value ? value : 0f;
            set => CarriageVelocityField?.SetValue(Lift, value);
        }

        /// <summary>
        /// The direction that the carriage speeds up to: -1, 0 or 1.
        /// </summary>
        public float Direction {
            get => CarriageDirectionField?.GetValue(Lift) is float value ? value : 0f;
            set => CarriageDirectionField?.SetValue(Lift, value);
        }

        /// <summary>
        /// How much of the way the carriage moves per unit of speed and second.
        /// </summary>
        public float SpeedFactor => CarriageSpeedFactorField?.GetValue(Lift) is float value ? value : 0f;

        /// <summary>
        /// Whether the local player holds a button of the carriage or called it with a plate.
        /// </summary>
        public bool IsPressed => CarriageLeftPressedField?.GetValue(Lift) is true ||
                                 CarriageRightPressedField?.GetValue(Lift) is true ||
                                 CarriageCalledField?.GetValue(Lift) is int and not 0;

        /// <summary>
        /// Whether the buttons of the local player are ignored because the partner drives, or let go a moment ago.
        /// </summary>
        public bool IsFollowing => PartnerDriving || Time.unscaledTime - PartnerReleasedAt < CarriageReleaseTime;

        /// <inheritdoc />
        public override MonoBehaviour Owner => Lift;

        /// <inheritdoc />
        public override string FsmName => CarriageKind;

        /// <inheritdoc />
        public override Transform Transform => Body;

        /// <inheritdoc />
        public override bool IsMoving => Mathf.Abs(Velocity) > 0.001f;

        /// <inheritdoc />
        public override bool IsUnlocked => CarriageUnlockedField?.GetValue(Lift) is true;

        /// <inheritdoc />
        public override int Stop => Part >= 0.5f ? 1 : 0;

        /// <inheritdoc />
        public override float StateValue => Part;

        /// <inheritdoc />
        public override float StateTolerance => 0.01f;

        /// <inheritdoc />
        public override bool CanCall => false;

        /// <inheritdoc />
        public override bool MovesSideways => true;

        /// <inheritdoc />
        public override bool HasStop(int stop) => stop is 0 or 1;

        /// <inheritdoc />
        public override bool Contains(Vector3 position) {
            var origin = Body.position;
            return Inside.Contains(new Vector2(position.x - origin.x, position.y - origin.y));
        }

        /// <inheritdoc />
        public override bool IsAtStop(int stop, Vector3 position) => false;

        /// <inheritdoc />
        public override bool Move(int stop, bool camera, float skippedDelay) => false;

        /// <inheritdoc />
        public override void PlaceAt(int stop, float value) {
            Velocity = 0f;
            Direction = 0f;
            Part = Mathf.Clamp01(value);
            UpdatePosition();
            CarriageUpdatePlatesMethod?.Invoke(Lift, null);
        }

        /// <inheritdoc />
        public override void JoinRide(int stop, float value) {
            Part = Mathf.Clamp01(value);
            UpdatePosition();
        }

        /// <summary>
        /// Moves the carriage to its part of the way.
        /// </summary>
        public void UpdatePosition() => CarriageUpdatePositionMethod?.Invoke(Lift, null);
    }

    /// <summary>
    /// Registers the hooks of carriages.
    /// </summary>
    private void RegisterCarriageHooks() {
        AddLiftHook(
            CarriageUpdateDirectionMethod,
            new Action<Action<ManualLift, bool>, ManualLift, bool>(OnCarriageUpdateDirection)
        );
        AddLiftHook(
            typeof(ManualLift).GetMethod("Update", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<ManualLift>, ManualLift>(OnCarriageUpdate)
        );
    }

    /// <summary>
    /// Hook for the method of <see cref="ManualLift"/> that the buttons and call plates of a carriage call, which ignores
    /// them while the partner drives, and otherwise makes the local player the driver.
    /// </summary>
    private void OnCarriageUpdateDirection(Action<ManualLift, bool> orig, ManualLift self, bool overrideCall) {
        ClientPlayerData? partner;
        if (_liftReplaying || (partner = GetLiftPartner()) == null) {
            orig(self, overrideCall);
            return;
        }

        Carriage carriage;
        try {
            carriage = GetCarriage(self);
            if (carriage.IsFollowing) {
                return;
            }
        } catch (Exception e) {
            LogLiftError(e);
            orig(self, overrideCall);
            return;
        }

        orig(self, overrideCall);
        try {
            var pressed = carriage.IsPressed;
            if (pressed && !carriage.LocalDriving) {
                carriage.LocalDriving = true;
                carriage.LocalClaim = ++carriage.SeenClaim;
            } else if (!pressed) {
                carriage.LocalDriving = false;
            }

            SendCarriageDrive(carriage, partner, true);
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Hook for the update of <see cref="ManualLift"/>, which plays the drive of the partner, lets the local player take
    /// over a carriage that the partner held too long, and sends the drive of the local player.
    /// </summary>
    private void OnCarriageUpdate(Action<ManualLift> orig, ManualLift self) {
        ClientPlayerData? partner;
        if ((partner = GetLiftPartner()) == null || !_lifts.TryGetValue(self, out var found) ||
            found is not Carriage carriage) {
            orig(self);
            return;
        }

        var follow = false;
        try {
            var following = carriage.IsFollowing;
            if (following && carriage.PartnerDriving && carriage.IsPressed &&
                Time.unscaledTime - carriage.PartnerDriveStart >= CarriageMaxHold) {
                // The partner held the carriage too long, so the held button of the local player takes it over
                carriage.PartnerDriving = false;
                carriage.PartnerReleasedAt = float.NegativeInfinity;
                following = false;
            }

            if (carriage.WasFollowing && !following) {
                // The buttons that the local player still holds count again
                CarriageUpdateDirectionMethod?.Invoke(self, [true]);
            }

            carriage.WasFollowing = following;
            follow = !carriage.LocalDriving && Time.unscaledTime - carriage.PartnerTime < CarriageFollowTime;
            if (follow) {
                carriage.Direction = carriage.PartnerDirection;
                CarriageDelayField?.SetValue(self, 0f);
                CarriageCalledField?.SetValue(self, 0);
                carriage.Velocity = Mathf.Lerp(
                    carriage.Velocity, carriage.PartnerVelocity, Mathf.Min(1f, Time.deltaTime * CarriageCatchUp)
                );
            }
        } catch (Exception e) {
            LogLiftError(e);
        }

        orig(self);

        try {
            if (follow) {
                var ahead = Mathf.Min(
                    Time.unscaledTime - carriage.PartnerTime + (float) _netClient.UpdateManager.AverageRtt / 2000f,
                    CarriagePredictTime
                );
                var target = Mathf.Clamp01(carriage.PartnerPart + carriage.SpeedFactor * carriage.PartnerVelocity * ahead);
                var difference = target - carriage.Part;
                carriage.Part = Mathf.Abs(difference) > CarriageSnapPart
                    ? target
                    : carriage.Part + difference * Mathf.Min(1f, Time.deltaTime * CarriageCatchUp);
                carriage.UpdatePosition();
            } else if (carriage.LocalDriving || !carriage.SentStopped) {
                SendCarriageDrive(carriage, partner, false);
            }
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Sends where the carriage is while the local player drives it, until it stopped after they let go.
    /// </summary>
    /// <param name="carriage">The carriage.</param>
    /// <param name="partner">The partner in the room.</param>
    /// <param name="force">Whether to send at once, for a change of the buttons.</param>
    private void SendCarriageDrive(Carriage carriage, ClientPlayerData partner, bool force) {
        if (!force && Time.unscaledTime - carriage.SentAt < CarriageSendInterval) {
            return;
        }

        carriage.SentAt = Time.unscaledTime;
        carriage.SentStopped = !carriage.LocalDriving && !carriage.IsMoving;
        Send(new CoopSaveUpdate {
            TargetId = partner.Id,
            Kind = CoopSaveUpdateKind.LiftDrive,
            Scene = carriage.Lift.gameObject.scene.name,
            ObjectPath = carriage.Path,
            FsmName = CarriageKind,
            PartCount = (ushort) (carriage.LocalDriving ? 1 : 0),
            Key = ++carriage.DriveCount,
            Amounts = [carriage.LocalClaim],
            Values = [carriage.Part, carriage.Velocity, carriage.Direction]
        });
    }

    /// <summary>
    /// Takes a drive of a carriage from the partner. A drive with a higher number wins over the drive of the local
    /// player, and the save key decides between drives with the same number, which both players started at once.
    /// </summary>
    private void OnLiftDrive(ClientPlayerData player, CoopSaveUpdate update) {
        if (_checkedWith != player.Id || !IsLiftRoom(update.Scene) || update.Values.Count < 3) {
            return;
        }

        try {
            if (FindLift(update) is not Carriage carriage || update.Key <= carriage.PartnerDriveKey) {
                return;
            }

            carriage.PartnerDriveKey = update.Key;
            var claim = update.Amounts.Count > 0 ? update.Amounts[0] : 0;
            carriage.SeenClaim = System.Math.Max(carriage.SeenClaim, claim);
            if (update.PartCount == 1) {
                if (carriage.LocalDriving) {
                    if (claim < carriage.LocalClaim || claim == carriage.LocalClaim && !PartnerKeyWins()) {
                        return;
                    }

                    carriage.LocalDriving = false;
                    carriage.SentStopped = true;
                }

                if (!carriage.PartnerDriving) {
                    carriage.PartnerDriveStart = Time.unscaledTime;
                }

                carriage.PartnerDriving = true;
            } else {
                if (carriage.LocalDriving) {
                    return;
                }

                if (carriage.PartnerDriving) {
                    carriage.PartnerDriving = false;
                    carriage.PartnerReleasedAt = Time.unscaledTime;
                }
            }

            carriage.PartnerTime = Time.unscaledTime;
            carriage.PartnerPart = update.Values[0];
            carriage.PartnerVelocity = update.Values[1];
            carriage.PartnerDirection = update.Values[2];
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Gets the sync of a carriage, looking at it the first time.
    /// </summary>
    private Carriage GetCarriage(ManualLift lift) {
        if (_lifts.TryGetValue(lift, out var existing) && existing is Carriage known) {
            return known;
        }

        var body = CarriageTransformField?.GetValue(lift) as Transform;
        if (body == null) {
            body = lift.transform;
        }

        var carriage = new Carriage {
            Path = ScenePath.Get(lift.transform),
            Lift = lift,
            Body = body,
            Inside = GetCarriageSpace(body)
        };

        // The speed factor is only set once a button was used, which the follower never does
        if (carriage.SpeedFactor <= 0f && CarriageLeftField?.GetValue(lift) is Vector2 left &&
            CarriageRightField?.GetValue(lift) is Vector2 right && Vector2.Distance(left, right) > 0f) {
            CarriageSpeedFactorField?.SetValue(lift, 1f / Vector2.Distance(left, right));
        }

        carriage.WasMoving = carriage.IsMoving;
        _lifts[lift] = carriage;
        return carriage;
    }

    /// <summary>
    /// Adds the carriages of the current room.
    /// </summary>
    private void AddRoomCarriages(List<SyncedLift> lifts) {
        foreach (var lift in Object.FindObjectsByType<ManualLift>(FindObjectsInactive.Exclude,
                     FindObjectsSortMode.None)) {
            lifts.Add(GetCarriage(lift));
        }
    }

    /// <summary>
    /// Finds the space in and on a carriage from its colliders, relative to the position of the object that moves.
    /// </summary>
    private static Rect GetCarriageSpace(Transform body) {
        Bounds? bounds = null;
        foreach (var collider in body.GetComponentsInChildren<Collider2D>()) {
            if (!collider.isTrigger) {
                bounds = Encapsulate(bounds, collider.bounds);
            }
        }

        if (bounds is not { } found) {
            return new Rect(-3f, -1f, 6f, 5f);
        }

        var origin = body.position;
        return new Rect(
            found.min.x - origin.x - 0.5f, found.min.y - origin.y - 0.5f, found.size.x + 1f, found.size.y + 3f
        );
    }
}
