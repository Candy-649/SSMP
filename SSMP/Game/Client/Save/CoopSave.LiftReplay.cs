using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// What the games of the members send about the lifts of a room that they are in (see CoopSave.Lifts): rides that they
/// started, calls for the game that decides, and the state of the lifts for a player who entered the room. Also moves
/// the avatar of each member with a lift that it rides.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How long a lift has to stand still, in seconds, before the positions that a member sends give the height of
    /// their avatar on it again.
    /// </summary>
    private const float LiftAvatarSettleTime = 0.3f;

    /// <summary>
    /// Plays a ride that the game of a member started on the same lift.
    /// </summary>
    private void OnLiftMove(ClientPlayerData player, CoopSaveUpdate update) {
        if (!_checkedMembers.Contains(player.Id) || !IsLiftRoom(update.Scene)) {
            return;
        }

        try {
            // Each game counts its own rides, so the newest is told apart for each member on their own
            if (FindLift(update) is not { } lift ||
                lift.MemberRides.TryGetValue(player.Id, out var newest) && update.Key <= newest) {
                return;
            }

            lift.MemberRides[player.Id] = update.Key;
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
            bool moved;
            _liftReplaying = true;
            try {
                lift.Unlock();
                if (!lift.IsMoving && !inside && update.Values.Count > 0) {
                    lift.SetHeight(update.Values[0]);
                }

                // The ride of the member started a moment ago, which the wait before the lift moves makes up for
                moved = lift.Move(stop, inside, (float) _netClient.UpdateManager.AverageRtt / 2000f);
                lift.WasMoving = lift.IsMoving;
                if (moved) {
                    // Their ride, which their game sent to every member in the room, so nobody is told about it
                    // again. Only once it was really taken: a ride that was refused would otherwise silence a later
                    // ride of ours that happens to go the same way.
                    lift.ToldStop = stop;
                }
            } finally {
                _liftReplaying = false;
            }

            // A ride that could not be played is the lifts parting company, and nothing here ever asked again: the
            // member sends a ride once, and from then on their lift is at one end and this one at the other, with no
            // way back but leaving the room. A lift refuses only while it is locked, starting up or already part way
            // through something, so it is worth asking again for a moment.
            if (!moved) {
                Logger.Info($"The lift '{update.ObjectPath}' could not take the ride of {player.Username} yet");
                MonoBehaviourUtil.Instance.StartCoroutine(RetryLiftMove(lift, stop, player.Username));
            }
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Serves a call of a lift that the game of a member sent because the local game decides about the lift.
    /// </summary>
    private void OnLiftCall(ClientPlayerData player, CoopSaveUpdate update) {
        if (!_checkedMembers.Contains(player.Id) || !IsLiftRoom(update.Scene)) {
            return;
        }

        try {
            int stop = update.Part;
            if (FindLift(update) is not { } lift || !lift.HasStop(stop) || !lift.IsUnlocked) {
                return;
            }

            var members = GetLiftMembers();
            var inside = update.PartCount == 1;
            if (!DecidesLifts(members)) {
                // Only the game that decides serves calls and corrects the other games. While the games wait for the
                // state of the room, the call waits for whichever of them decides
                if (IsWaitingForLiftState()) {
                    QueueLiftCall(lift, stop, player.Id, inside);
                }

                return;
            }

            if (lift.Stop == stop) {
                // The lift goes to that stop or stands there already, which the game of the member may not show
                if (!lift.IsMoving) {
                    SendLiftState(lift, player.Id, true);
                }

                return;
            }

            var hero = HeroController.instance;
            if (lift.IsMoving || IsLiftHeldForOther(lift, player.Id, members) ||
                !StartLiftRide(lift, stop, hero != null && lift.ContainsHero(hero), player)) {
                QueueLiftCall(lift, stop, player.Id, inside);
            }
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Unlocks a lift that the game of a member unlocked, mostly with a plate that their player stood on. The plate
    /// here goes down for them as well, but tells the lift nothing (see <see cref="OnLiftPlateActivate"/>).
    /// </summary>
    private void OnLiftUnlock(ClientPlayerData player, CoopSaveUpdate update) {
        if (!_checkedMembers.Contains(player.Id) || !IsLiftRoom(update.Scene)) {
            return;
        }

        try {
            if (FindLift(update) is not { IsUnlocked: false } lift) {
                return;
            }

            _liftReplaying = true;
            try {
                lift.Unlock();
            } finally {
                _liftReplaying = false;
            }

            Logger.Info($"{player.Username} unlocked the lift '{update.ObjectPath}'");
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Asks a lift again to take a ride of a member that it could not take at once, for a moment.
    /// </summary>
    /// <param name="lift">The lift.</param>
    /// <param name="stop">The stop the member rode to.</param>
    /// <param name="username">The name of the member, for the log.</param>
    private IEnumerator RetryLiftMove(SyncedLift lift, int stop, string username) {
        var until = Time.unscaledTime + LiftMoveRetryTime;

        while (Time.unscaledTime < until) {
            yield return null;

            if (lift.Stop == stop) {
                yield break;
            }

            var hero = HeroController.instance;
            var inside = hero != null && lift.ContainsHero(hero);
            bool moved;
            _liftReplaying = true;
            try {
                lift.Unlock();
                moved = lift.Move(stop, inside, 0f);
                lift.WasMoving = lift.IsMoving;
                if (moved) {
                    lift.ToldStop = stop;
                }
            } catch (Exception e) {
                LogLiftError(e);

                yield break;
            } finally {
                _liftReplaying = false;
            }

            if (moved) {
                Logger.Info($"The lift took the ride of {username} after all");

                yield break;
            }
        }

        Logger.Warn(
            $"A lift never took the ride of {username}, so the games have it at different stops until the room is left"
        );
    }

    /// <summary>
    /// Sends the state of the lifts of the current room to a member who entered it.
    /// </summary>
    private void OnLiftStateRequest(ClientPlayerData player, CoopSaveUpdate update) {
        if (!_checkedMembers.Contains(player.Id) || update.Scene != SceneManager.GetActiveScene().name) {
            return;
        }

        try {
            // The member entered the room, maybe after their game started again, so their counts start over
            foreach (var known in _lifts.Values) {
                known.MemberRides.Remove(player.Id);
                if (known is Carriage carriage) {
                    ForgetCarriageDriveOf(carriage, player.Id);
                }
            }

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
    /// <param name="targetId">The ID of the member.</param>
    /// <param name="correction">Whether the state corrects the lift of the member, which they take even when they have
    /// been in the room longer.</param>
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
            Values = [lift.StateValue]
        });
    }

    /// <summary>
    /// Takes the state of a lift from a member who has been in the room longer, unless the local hero is on the lift,
    /// in which case the lift of the member comes to the hero instead.
    /// </summary>
    private void OnLiftState(ClientPlayerData player, CoopSaveUpdate update) {
        if (!_checkedMembers.Contains(player.Id) || !IsLiftRoom(update.Scene)) {
            return;
        }

        try {
            _liftStateReceived = true;
            var localTime = Time.unscaledTime - _liftRoomStart;
            if (update.PlayTime < localTime - LiftRoomTimeTie ||
                (update.PlayTime <= localTime + LiftRoomTimeTie && !MemberKeyWins(player))) {
                return;
            }

            int stop = update.Part;
            if (FindLift(update) is not { } lift || !lift.HasStop(stop)) {
                return;
            }

            lift.MemberRides[player.Id] = System.Math.Max(
                lift.MemberRides.TryGetValue(player.Id, out var newest) ? newest : 0, update.Key
            );
            lift.Calls.Clear();
            var moving = update.PartCount == 1;
            var hero = HeroController.instance;
            if (hero != null && lift.ContainsHero(hero)) {
                if (lift.CanCall && (moving || stop != lift.Stop) && !lift.IsMoving) {
                    Send(new CoopSaveUpdate {
                        TargetId = player.Id,
                        Kind = CoopSaveUpdateKind.LiftCall,
                        Scene = update.Scene,
                        ObjectPath = lift.Path,
                        FsmName = lift.FsmName,
                        Part = (ushort) lift.Stop,
                        PartCount = 1
                    });
                }

                return;
            }

            _liftReplaying = true;
            try {
                var value = update.Values.Count > 0 ? update.Values[0] : lift.StateValue;
                if (moving) {
                    // A lift that stands where the ride of the member ends already, like at the end of the ride,
                    // doesn't ride there again
                    if (lift.IsMoving
                            ? lift.Stop != stop
                            : lift.Stop != stop || Mathf.Abs(lift.StateValue - value) > lift.StateTolerance) {
                        lift.JoinRide(stop, value);
                    }

                    // Their lift is on its way there, so that much they know
                    lift.ToldStop = stop;
                } else {
                    if (lift.IsMoving || lift.Stop != stop ||
                        Mathf.Abs(lift.StateValue - value) > lift.StateTolerance) {
                        lift.PlaceAt(stop, value);
                    }

                    // Their lift stands still, so they know of no ride at all - and putting this one where theirs
                    // stands can set it off, because that runs the lift's own state machine as far as it will go.
                    // A ride that begins that way is this game's, and they have to be told.
                    lift.ToldStop = -1;
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
    /// Moves the avatars of the members with the lifts they ride right before the frame is drawn, after the positions
    /// that the members sent, which lag behind a moving lift, moved them.
    /// </summary>
    private void OnLiftBeforeRender() {
        if (_lifts.Count == 0) {
            return;
        }

        try {
            var members = GetLiftMembers();
            foreach (var lift in _lifts.Values) {
                if (lift.Owner != null) {
                    UpdateAvatarRides(lift, members);
                }
            }
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Starts, keeps or ends the rides of the avatars of the members on a lift.
    /// </summary>
    private static void UpdateAvatarRides(SyncedLift lift, List<ClientPlayerData> members) {
        var liftPosition = lift.Transform.position;
        if ((liftPosition - lift.LastPosition).sqrMagnitude > 0.000001f) {
            lift.LastPosition = liftPosition;
            lift.MovedAt = Time.unscaledTime;
        }

        // A member who left the room or the save no longer rides it
        if (lift.Avatars.Count > 0) {
            foreach (var id in lift.Avatars.Keys.ToList()) {
                if (!members.Exists(member => member.Id == id)) {
                    EndAvatarRide(lift, id);
                }
            }
        }

        foreach (var member in members) {
            UpdateAvatarRide(lift, member, liftPosition);
        }
    }

    /// <summary>
    /// Starts, keeps or ends the ride of the avatar of a member on a lift.
    /// </summary>
    private static void UpdateAvatarRide(SyncedLift lift, ClientPlayerData member, Vector3 liftPosition) {
        lift.Avatars.TryGetValue(member.Id, out var ride);
        var container = member.PlayerContainer;
        var stopped = !lift.IsMoving && (ride == null || Time.unscaledTime - lift.MovedAt > LiftAvatarSettleTime);
        if (container == null || !container.activeInHierarchy || stopped || ride != null && ride.Container != container) {
            EndAvatarRide(lift, member.Id);
            return;
        }

        var position = container.transform.position;
        if (ride == null) {
            if (!lift.Contains(position)) {
                return;
            }

            ride = new AvatarRide { Container = container, Offset = position - liftPosition };
            lift.Avatars[member.Id] = ride;
            if (container.TryGetComponent<SSMP.Fsm.PredictiveInterpolation>(out var interpolation)) {
                interpolation.SetPredictionEnabled(false);
            }
        } else if ((position - ride.Placed).sqrMagnitude > 0.0001f) {
            // A position from the member moved the avatar, which lags behind a moving lift along its way.
            //
            // Stepping off is looked for across the lift and not along it, for that same reason: along the way it
            // travels, a position that has merely fallen behind looks exactly like one that has left, and reading
            // the first as the second would throw a passenger off every time the lift got going. Across it there is
            // no lag to confuse, so a member whose position has moved off the side of the lift really has.
            //
            // Nothing looked at all before this, and once the avatar was holding on it never let go: a member who
            // stepped off a rising lift went on rising beside it, because the one axis their own position was still
            // believed on was the one they had walked along.
            var across = lift.MovesSideways
                ? new Vector3(ride.Placed.x, position.y, position.z)
                : new Vector3(position.x, ride.Placed.y, position.z);
            if (!lift.Contains(across)) {
                EndAvatarRide(lift, member.Id);

                return;
            }

            var offset = position - liftPosition;
            ride.Offset = Time.unscaledTime - lift.MovedAt > LiftAvatarSettleTime ? offset :
                lift.MovesSideways ? new Vector3(ride.Offset.x, offset.y, offset.z) :
                new Vector3(offset.x, ride.Offset.y, offset.z);
        }

        var placed = liftPosition + ride.Offset;
        container.transform.position = placed;
        ride.Placed = placed;
    }

    /// <summary>
    /// Ends the ride of the avatar of a member on a lift, which gives the avatar back to the positions that the member
    /// sends.
    /// </summary>
    private static void EndAvatarRide(SyncedLift lift, ushort playerId) {
        if (!lift.Avatars.TryGetValue(playerId, out var ride)) {
            return;
        }

        lift.Avatars.Remove(playerId);
        if (ride.Container != null &&
            ride.Container.TryGetComponent<SSMP.Fsm.PredictiveInterpolation>(out var interpolation)) {
            interpolation.SetPredictionEnabled(true);
        }
    }

    /// <summary>
    /// Ends the rides of the avatars of every member on a lift.
    /// </summary>
    private static void EndAvatarRides(SyncedLift lift) {
        foreach (var playerId in lift.Avatars.Keys.ToList()) {
            EndAvatarRide(lift, playerId);
        }
    }

    /// <summary>
    /// Ends the rides of the avatars of the members on all lifts.
    /// </summary>
    private void EndAvatarRides() {
        foreach (var lift in _lifts.Values) {
            EndAvatarRides(lift);
        }
    }
}
