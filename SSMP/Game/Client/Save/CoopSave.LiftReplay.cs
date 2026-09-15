using System;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// What the game of the partner sends about the lifts of a room that both players are in (see
/// <see cref="CoopSave"/>.Lifts): rides that it started, calls for the game that decides, and the state of the lifts
/// for a player who entered the room. Also moves the avatar of the partner with a lift that it rides.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The difference in the time that both players have been in a room, in seconds, below which the save key decides
    /// whose lifts both games take.
    /// </summary>
    private const float LiftRoomTimeTie = 0.25f;

    /// <summary>
    /// Whether a room of the update is loaded in the local game.
    /// </summary>
    private static bool IsLiftRoom(string scene) => scene.Length > 0 && SceneManager.GetSceneByName(scene).isLoaded;

    /// <summary>
    /// Whether the current room has lifts that the sync keeps the same in both games.
    /// </summary>
    private static bool RoomHasLifts() {
        return Object.FindObjectsByType<LiftControl>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length > 0;
    }

    /// <summary>
    /// Finds the cage lift of an update in the local game.
    /// </summary>
    private CageLift? FindCage(CoopSaveUpdate update) {
        return ScenePath.Find(update.ObjectPath, update.Scene) is { } target &&
               target.TryGetComponent<LiftControl>(out var control)
            ? GetCage(control)
            : null;
    }

    /// <summary>
    /// Plays a ride that the game of the partner started on the same lift.
    /// </summary>
    private void OnLiftMove(ClientPlayerData player, CoopSaveUpdate update) {
        if (_checkedWith != player.Id || !IsLiftRoom(update.Scene)) {
            return;
        }

        try {
            if (FindCage(update) is not { } lift || update.Key <= lift.PartnerRide) {
                return;
            }

            lift.PartnerRide = update.Key;
            var control = lift.Control;
            int stop = update.Part;
            if (GetStopHeight(control, stop) == null) {
                return;
            }

            lift.Calls.RemoveAll(call => call.Stop == stop);
            _liftReplaying = true;
            try {
                if (!IsLiftUnlocked(control)) {
                    control.SetUnlocked(true);
                }

                var moving = IsLiftMoving(control);
                if (GetLiftStop(control) == stop && (moving || !lift.WasMoving)) {
                    // It already goes there, or stands there
                    if (!moving) {
                        control.MoveToStop(stop, false);
                    }

                    return;
                }

                var inside = IsLocalHeroInside(lift);
                if (!moving && !inside && update.Values.Count > 0) {
                    SetLiftHeight(control, update.Values[0]);
                }

                // The ride of the partner started a moment ago, which the wait before the lift moves makes up for
                PlayLiftMove(lift, stop, inside, (float) _netClient.UpdateManager.AverageRtt / 2000f);
            } finally {
                _liftReplaying = false;
            }
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Serves a call of a lift that the game of the partner sent because the local game decides about the lift.
    /// </summary>
    private void OnLiftCall(ClientPlayerData player, CoopSaveUpdate update) {
        if (_checkedWith != player.Id || !IsLiftRoom(update.Scene)) {
            return;
        }

        try {
            if (FindCage(update) is not { } lift) {
                return;
            }

            var control = lift.Control;
            int stop = update.Part;
            if (GetStopHeight(control, stop) == null || !IsLiftUnlocked(control)) {
                return;
            }

            var partner = player.IsInLocalScene ? player : null;
            if (GetLiftStop(control) == stop) {
                // The lift goes to that stop or stands there already, which the game of the partner may not show yet
                if (!IsLiftMoving(control)) {
                    SendLiftState(lift, player.Id);
                }

                return;
            }

            var inside = update.PartCount == 1;
            if (IsLiftMoving(control) || IsLiftHeldForOther(lift, true, partner)) {
                QueueLiftCall(lift, stop, true, inside);
                return;
            }

            StartLiftRide(lift, stop, IsLocalHeroInside(lift), player);
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Sends the state of the lifts of the current room to a partner who entered it.
    /// </summary>
    private void OnLiftStateRequest(ClientPlayerData player, CoopSaveUpdate update) {
        if (_checkedWith != player.Id || update.Scene != SceneManager.GetActiveScene().name) {
            return;
        }

        try {
            var controls = Object.FindObjectsByType<LiftControl>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var control in controls) {
                SendLiftState(GetCage(control), player.Id);
            }
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Sends where a cage lift is and whether it moves.
    /// </summary>
    private void SendLiftState(CageLift lift, ushort targetId) {
        var control = lift.Control;
        Send(new CoopSaveUpdate {
            TargetId = targetId,
            Kind = CoopSaveUpdateKind.LiftState,
            Scene = control.gameObject.scene.name,
            ObjectPath = lift.Path,
            Part = (ushort) Mathf.Max(0, GetLiftStop(control)),
            PartCount = (ushort) (IsLiftMoving(control) ? 1 : 0),
            Key = _liftRideCount,
            PlayTime = Time.unscaledTime - _liftRoomStart,
            Values = [control.transform.position.y]
        });
    }

    /// <summary>
    /// Takes the state of a lift from a partner who has been in the room longer, unless the local hero is inside it,
    /// in which case the lift of the partner comes to the hero instead.
    /// </summary>
    private void OnLiftState(ClientPlayerData player, CoopSaveUpdate update) {
        if (_checkedWith != player.Id || !IsLiftRoom(update.Scene)) {
            return;
        }

        try {
            _liftStateReceived = true;
            var localTime = Time.unscaledTime - _liftRoomStart;
            if (update.PlayTime < localTime - LiftRoomTimeTie ||
                (update.PlayTime <= localTime + LiftRoomTimeTie && !PartnerKeyWins())) {
                return;
            }

            if (FindCage(update) is not { } lift) {
                return;
            }

            lift.PartnerRide = System.Math.Max(lift.PartnerRide, update.Key);
            lift.Calls.Clear();
            var control = lift.Control;
            int stop = update.Part;
            if (GetStopHeight(control, stop) is not { } stopHeight) {
                return;
            }

            var moving = update.PartCount == 1;
            if (IsLocalHeroInside(lift)) {
                if ((moving || stop != GetLiftStop(control)) && !IsLiftMoving(control)) {
                    Send(new CoopSaveUpdate {
                        TargetId = player.Id,
                        Kind = CoopSaveUpdateKind.LiftCall,
                        Scene = update.Scene,
                        ObjectPath = lift.Path,
                        Part = (ushort) GetLiftStop(control)
                    });
                }

                return;
            }

            var height = update.Values.Count > 0 ? update.Values[0] : stopHeight;
            _liftReplaying = true;
            try {
                if (moving) {
                    if (IsLiftMoving(control) && GetLiftStop(control) == stop) {
                        return;
                    }

                    if (IsLiftMoving(control)) {
                        control.StopMoving();
                    }

                    SetLiftHeight(control, height);
                    if (GetLiftStop(control) == stop) {
                        LiftCurrentStopField?.SetValue(control, stop == 0 ? 1 : 0);
                    }

                    PlayLiftMove(lift, stop, false, float.MaxValue);
                    return;
                }

                if (!IsLiftMoving(control) && GetLiftStop(control) == stop &&
                    Mathf.Abs(control.transform.position.y - stopHeight) < LiftSameHeight) {
                    return;
                }

                if (IsLiftMoving(control)) {
                    control.StopMoving();
                }

                LiftCurrentStopField?.SetValue(control, stop);
                LiftSetInitialPosMethod?.Invoke(control, null);
                lift.WasMoving = false;
                lift.StandingY = control.transform.position.y;
            } finally {
                _liftReplaying = false;
            }
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Moves a lift to a stop with a shorter wait before it moves, for a ride that started earlier in the other game.
    /// </summary>
    private static void PlayLiftMove(CageLift lift, int stop, bool camera, float skippedDelay) {
        var control = lift.Control;
        var delay = LiftMoveDelayField?.GetValue(control) is float value ? value : 0f;
        LiftMoveDelayField?.SetValue(control, Mathf.Max(0f, delay - skippedDelay));
        try {
            control.MoveToStop(stop, camera);
        } finally {
            LiftMoveDelayField?.SetValue(control, delay);
        }

        lift.WasMoving = IsLiftMoving(control);
    }

    /// <summary>
    /// Gets the height of a stop of a lift, or null if the lift has no such stop.
    /// </summary>
    private static float? GetStopHeight(LiftControl control, int stop) {
        return LiftStopsField?.GetValue(control) is Array stops && stop >= 0 && stop < stops.Length &&
               LiftStopPosField?.GetValue(stops.GetValue(stop)) is float height
            ? height
            : null;
    }

    /// <summary>
    /// Puts a lift at a height.
    /// </summary>
    private static void SetLiftHeight(LiftControl control, float height) {
        var transform = control.transform;
        var position = transform.position;
        if (Mathf.Abs(position.y - height) > LiftSameHeight) {
            position.y = height;
            transform.position = position;
        }
    }

    /// <summary>
    /// Moves the avatar of the partner with the lifts it rides right before the frame is drawn, after the positions that
    /// the partner sent, which lag behind the lift, moved it.
    /// </summary>
    private void OnLiftBeforeRender() {
        if (_cages.Count == 0) {
            return;
        }

        try {
            var partner = GetLiftPartner();
            foreach (var lift in _cages.Values) {
                if (lift.Control != null) {
                    UpdateAvatarRide(lift, partner);
                }
            }
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Starts, keeps or ends the ride of the avatar of the partner on a moving cage lift.
    /// </summary>
    private static void UpdateAvatarRide(CageLift lift, ClientPlayerData? partner) {
        var container = partner?.PlayerContainer;
        if (container == null || !container.activeInHierarchy || !IsLiftMoving(lift.Control) ||
            lift.AvatarRiding && lift.AvatarContainer != container) {
            EndAvatarRide(lift);
            return;
        }

        var liftPosition = lift.Control.transform.position;
        var position = container.transform.position;
        if (!lift.AvatarRiding) {
            if (!IsInsideCage(lift, position)) {
                return;
            }

            lift.AvatarRiding = true;
            lift.AvatarContainer = container;
            lift.AvatarOffset = position - liftPosition;
            if (container.TryGetComponent<SSMP.Fsm.PredictiveInterpolation>(out var interpolation)) {
                interpolation.SetPredictionEnabled(false);
            }
        } else if ((position - lift.AvatarPlaced).sqrMagnitude > 0.0001f) {
            // A position from the partner moved the avatar, which can walk in the lift but lags behind its height
            var offset = position - liftPosition;
            lift.AvatarOffset = new Vector3(offset.x, lift.AvatarOffset.y, offset.z);
        }

        var placed = liftPosition + lift.AvatarOffset;
        container.transform.position = placed;
        lift.AvatarPlaced = placed;
    }

    /// <summary>
    /// Ends the ride of the avatar of the partner on a lift, which gives the avatar back to the positions that the
    /// partner sends.
    /// </summary>
    private static void EndAvatarRide(CageLift lift) {
        if (!lift.AvatarRiding) {
            return;
        }

        lift.AvatarRiding = false;
        if (lift.AvatarContainer != null &&
            lift.AvatarContainer.TryGetComponent<SSMP.Fsm.PredictiveInterpolation>(out var interpolation)) {
            interpolation.SetPredictionEnabled(true);
        }

        lift.AvatarContainer = null;
    }

    /// <summary>
    /// Ends the rides of the avatar of the partner on all lifts.
    /// </summary>
    private void EndAvatarRides() {
        foreach (var lift in _cages.Values) {
            EndAvatarRide(lift);
        }
    }
}
