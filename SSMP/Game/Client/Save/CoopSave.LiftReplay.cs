using System;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SSMP.Game.Client.Save;

/// <summary>
/// What the game of the partner sends about the lifts of a room that both players are in (see CoopSave.Lifts): rides
/// that it started, calls for the game that decides, and the state of the lifts for a player who entered the room. Also
/// moves the avatar of the partner with a lift that it rides.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How long a lift has to stand still, in seconds, before the positions that the partner sends give the height of
    /// their avatar on it again.
    /// </summary>
    private const float LiftAvatarSettleTime = 0.3f;

    /// <summary>
    /// Plays a ride that the game of the partner started on the same lift.
    /// </summary>
    private void OnLiftMove(ClientPlayerData player, CoopSaveUpdate update) {
        if (_checkedWith != player.Id || !IsLiftRoom(update.Scene)) {
            return;
        }

        try {
            if (FindLift(update) is not { } lift || update.Key <= lift.PartnerRide) {
                return;
            }

            lift.PartnerRide = update.Key;
            int stop = update.Part;
            if (!lift.HasStop(stop)) {
                return;
            }

            lift.Calls.RemoveAll(call => call.Stop == stop);
            if (lift.Stop == stop) {
                return;
            }

            var hero = HeroController.instance;
            var inside = hero != null && lift.ContainsHero(hero);
            _liftReplaying = true;
            try {
                lift.Unlock();
                if (!lift.IsMoving && !inside && update.Values.Count > 0) {
                    lift.SetHeight(update.Values[0]);
                }

                // The ride of the partner started a moment ago, which the wait before the lift moves makes up for
                lift.Move(stop, inside, (float) _netClient.UpdateManager.AverageRtt / 2000f);
                lift.WasMoving = lift.IsMoving;
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
            int stop = update.Part;
            if (FindLift(update) is not { } lift || !lift.HasStop(stop) || !lift.IsUnlocked) {
                return;
            }

            if (lift.Stop == stop) {
                // The lift goes to that stop or stands there already, which the game of the partner may not show
                if (!lift.IsMoving) {
                    SendLiftState(lift, player.Id, true);
                }

                return;
            }

            var partner = player.IsInLocalScene ? player : null;
            var inside = update.PartCount == 1;
            var hero = HeroController.instance;
            if (lift.IsMoving || IsLiftHeldForOther(lift, true, partner) ||
                !StartLiftRide(lift, stop, hero != null && lift.ContainsHero(hero), player)) {
                QueueLiftCall(lift, stop, true, inside);
            }
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
            foreach (var lift in FindRoomLifts()) {
                SendLiftState(lift, player.Id, false);
            }
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Sends where a lift is and whether it moves.
    /// </summary>
    /// <param name="lift">The lift.</param>
    /// <param name="targetId">The ID of the partner.</param>
    /// <param name="correction">Whether the state corrects the lift of the partner, which it takes even when the
    /// partner has been in the room longer.</param>
    private void SendLiftState(SyncedLift lift, ushort targetId, bool correction) {
        Send(new CoopSaveUpdate {
            TargetId = targetId,
            Kind = CoopSaveUpdateKind.LiftState,
            Scene = lift.Owner.gameObject.scene.name,
            ObjectPath = lift.Path,
            FsmName = lift.FsmName,
            Part = (ushort) Mathf.Max(0, lift.Stop),
            PartCount = (ushort) (lift.IsMoving ? 1 : 0),
            Key = _liftRideCount,
            PlayTime = correction ? float.MaxValue : Time.unscaledTime - _liftRoomStart,
            Values = [lift.Transform.position.y]
        });
    }

    /// <summary>
    /// Takes the state of a lift from a partner who has been in the room longer, unless the local hero is on the lift,
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

            int stop = update.Part;
            if (FindLift(update) is not { } lift || !lift.HasStop(stop)) {
                return;
            }

            lift.PartnerRide = System.Math.Max(lift.PartnerRide, update.Key);
            lift.Calls.Clear();
            var moving = update.PartCount == 1;
            var hero = HeroController.instance;
            if (hero != null && lift.ContainsHero(hero)) {
                if ((moving || stop != lift.Stop) && !lift.IsMoving) {
                    Send(new CoopSaveUpdate {
                        TargetId = player.Id,
                        Kind = CoopSaveUpdateKind.LiftCall,
                        Scene = update.Scene,
                        ObjectPath = lift.Path,
                        FsmName = lift.FsmName,
                        Part = (ushort) lift.Stop
                    });
                }

                return;
            }

            _liftReplaying = true;
            try {
                if (moving) {
                    if (!lift.IsMoving || lift.Stop != stop) {
                        lift.JoinRide(stop, update.Values.Count > 0 ? update.Values[0] : lift.Transform.position.y);
                    }
                } else if (lift.IsMoving || lift.Stop != stop ||
                           update.Values.Count > 0 &&
                           Mathf.Abs(lift.Transform.position.y - update.Values[0]) > LiftSameHeight) {
                    lift.PlaceAt(stop);
                }

                lift.WasMoving = lift.IsMoving;
            } finally {
                _liftReplaying = false;
            }
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Moves the avatar of the partner with the lifts it rides right before the frame is drawn, after the positions that
    /// the partner sent, which lag behind a moving lift, moved it.
    /// </summary>
    private void OnLiftBeforeRender() {
        if (_lifts.Count == 0) {
            return;
        }

        try {
            var partner = GetLiftPartner();
            foreach (var lift in _lifts.Values) {
                if (lift.Owner != null) {
                    UpdateAvatarRide(lift, partner);
                }
            }
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Starts, keeps or ends the ride of the avatar of the partner on a lift.
    /// </summary>
    private static void UpdateAvatarRide(SyncedLift lift, ClientPlayerData? partner) {
        var liftPosition = lift.Transform.position;
        if ((liftPosition - lift.LastPosition).sqrMagnitude > 0.000001f) {
            lift.LastPosition = liftPosition;
            lift.MovedAt = Time.unscaledTime;
        }

        var container = partner?.PlayerContainer;
        if (container == null || !container.activeInHierarchy || !lift.IsMoving ||
            lift.AvatarRiding && lift.AvatarContainer != container) {
            EndAvatarRide(lift);
            return;
        }

        var position = container.transform.position;
        if (!lift.AvatarRiding) {
            if (!lift.Contains(position)) {
                return;
            }

            lift.AvatarRiding = true;
            lift.AvatarContainer = container;
            lift.AvatarOffset = position - liftPosition;
            if (container.TryGetComponent<SSMP.Fsm.PredictiveInterpolation>(out var interpolation)) {
                interpolation.SetPredictionEnabled(false);
            }
        } else if ((position - lift.AvatarPlaced).sqrMagnitude > 0.0001f) {
            // A position from the partner moved the avatar, which lags behind the height of a moving lift
            var offset = position - liftPosition;
            lift.AvatarOffset = Time.unscaledTime - lift.MovedAt > LiftAvatarSettleTime
                ? offset
                : new Vector3(offset.x, lift.AvatarOffset.y, offset.z);
        }

        var placed = liftPosition + lift.AvatarOffset;
        container.transform.position = placed;
        lift.AvatarPlaced = placed;
    }

    /// <summary>
    /// Ends the ride of the avatar of the partner on a lift, which gives the avatar back to the positions that the
    /// partner sends.
    /// </summary>
    private static void EndAvatarRide(SyncedLift lift) {
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
        foreach (var lift in _lifts.Values) {
            EndAvatarRide(lift);
        }
    }
}
