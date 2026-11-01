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
    /// How much where the local hero is on a lift that moves, along its way, has to change to be told before
    /// <see cref="LiftRideRest"/> has passed, in units.
    /// </summary>
    private const float LiftRideStep = 0.05f;

    /// <summary>
    /// The shortest time between two tellings of where the local hero is on a lift that moves, in seconds: about
    /// twenty a second through a jump.
    /// </summary>
    private const float LiftRideInterval = 0.05f;

    /// <summary>
    /// How long after the last telling any change at all of where the local hero is on a lift is told, in seconds,
    /// so that where a jump ends is told however little it moved last.
    /// </summary>
    private const float LiftRideRest = 0.15f;

    /// <summary>
    /// A change of where the local hero is on a lift, in units, below which nothing changed.
    /// </summary>
    private const float LiftRideSame = 0.001f;

    /// <summary>
    /// How far the local hero has to go, standing on a lift that moves, for that to be told, in units: a walk along a
    /// lift that moves sideways. Less is how physics can shake a hero on a lift that moves, which went out twenty
    /// times a second through a whole ride.
    /// </summary>
    private const float LiftRideWalk = 0.3f;

    /// <summary>
    /// How long before the local lift set off a member's word of where they are on it may have come and still be of
    /// this trip, in seconds: their game tells it as its own lift sets off, and the local lift sets off about then too.
    /// The trip before ended at least the wait of a lift at its stop before.
    /// </summary>
    private const float LiftRideEarly = 1f;

    /// <summary>
    /// How long, after a member said they stepped off a lift that moves, positions of theirs that still put them on it
    /// don't take their avatar on again, in seconds: those lag behind and were sent before they stepped off.
    /// </summary>
    private const float LiftRideLeftHold = 2f;

    /// <summary>
    /// How long the avatar takes to reach where a member's game says they are on a lift, in seconds, so that the
    /// twenty words a second of a jump make one movement.
    /// </summary>
    private const float LiftRideSmoothTime = 0.06f;

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
    /// Takes in where a member's hero is on a lift that moves, along its way, which their avatar on the local lift
    /// is shown at (UpdateAvatarRide).
    /// </summary>
    private void OnLiftRide(ClientPlayerData player, CoopSaveUpdate update) {
        if (!_checkedMembers.Contains(player.Id) || !IsLiftRoom(update.Scene) || update.Values.Count < 1) {
            return;
        }

        try {
            if (FindLift(update) is not { } lift ||
                lift.ToldRides.TryGetValue(player.Id, out var told) && update.Key <= told.Key) {
                return;
            }

            lift.ToldRides[player.Id] = new ToldLiftRide {
                Key = update.Key,
                Along = update.Values[0],
                Riding = update.PartCount == 1,
                At = Time.unscaledTime
            };
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Tells the members in the room where the local hero is on a lift that moves, along the way it moves, as that
    /// changes: their games show the local player's figure on their lift by it, since the positions they get lag behind
    /// a moving lift. Standing on it, that is told once, as the ride begins, and again only for a walk along a lift
    /// that moves sideways; a jump is told as it goes, about twenty times a second, and where it lands once. Stepping
    /// off a lift that still moves is told too.
    /// </summary>
    private void UpdateLocalLiftRide(SyncedLift lift, List<ClientPlayerData> members) {
        var hero = HeroController.instance;
        var ride = lift.LocalRide;
        if (hero == null || !lift.IsMoving || !lift.ContainsHero(hero)) {
            if (ride == null) {
                return;
            }

            lift.LocalRide = null;
            if (lift.IsMoving) {
                // Off a lift that still goes on: their figure of the local player lets go of it
                SendLiftRide(lift, members, ride.SentAlong, false);
                Logger.Info(
                    $"Stepped off the lift '{lift.Path}' while it moves, which was told to the members in the room " +
                    $"(where on it the hero was, {ride.Sent + 1} time(s) in the ride)"
                );
            } else {
                Logger.Info(
                    $"Rode the lift '{lift.Path}' to its stop, telling the members in the room where on it the hero " +
                    $"was {ride.Sent} time(s)"
                );
            }

            return;
        }

        var along = AlongLift(lift, hero.transform.position - lift.Transform.position);
        var grounded = hero.cState.onGround;
        var now = Time.unscaledTime;
        if (ride == null) {
            lift.LocalRide = new LocalLiftRide { SentAlong = along, SentAt = now, Sent = 1, SentGrounded = grounded };
            SendLiftRide(lift, members, along, true);
            return;
        }

        var change = Mathf.Abs(along - ride.SentAlong);
        var since = now - ride.SentAt;
        var tell = grounded
            ? ride.SentGrounded ? change > LiftRideWalk && since >= LiftRideRest : since >= LiftRideInterval
            : change > LiftRideStep && since >= LiftRideInterval || change > LiftRideSame && since >= LiftRideRest;
        if (tell) {
            ride.SentAlong = along;
            ride.SentAt = now;
            ride.SentGrounded = grounded;
            ride.Sent++;
            SendLiftRide(lift, members, along, true);
        }
    }

    /// <summary>
    /// Sends where the local hero is on a lift that moves, along its way, to the members in the room.
    /// </summary>
    /// <param name="lift">The lift.</param>
    /// <param name="members">The members in the room.</param>
    /// <param name="along">How far from the lift along its way the hero is.</param>
    /// <param name="riding">Whether the hero rides it, or stepped off it.</param>
    private void SendLiftRide(SyncedLift lift, List<ClientPlayerData> members, float along, bool riding) {
        if (members.Count == 0) {
            return;
        }

        var update = new CoopSaveUpdate {
            TargetId = members[0].Id,
            Kind = CoopSaveUpdateKind.LiftRide,
            Scene = lift.Owner.gameObject.scene.name,
            ObjectPath = lift.Path,
            FsmName = lift.FsmName,
            PartCount = (ushort) (riding ? 1 : 0),
            Key = ++_liftRiderCount,
            Values = [along]
        };
        Send(update);
        for (var i = 1; i < members.Count; i++) {
            SendCopy(update, members[i].Id);
        }
    }

    /// <summary>
    /// How far a point is from a lift along the way the lift moves, given how far it is from the lift.
    /// </summary>
    private static float AlongLift(SyncedLift lift, Vector3 fromLift) => lift.MovesSideways ? fromLift.x : fromLift.y;

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
            // A lift that stood still sets off on a trip, which what the members tell of their place on it belongs to
            if (Time.unscaledTime - lift.MovedAt > LiftAvatarSettleTime) {
                lift.SetOffAt = Time.unscaledTime;
            }

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

        // Nor is what they told kept: their count of it starts again with their game, which a restart in the room
        // would have kept older than anything they tell (looked over first, so that nothing is made each frame)
        if (lift.ToldRides.Count > 0) {
            var gone = false;
            foreach (var id in lift.ToldRides.Keys) {
                gone |= !IsMember(members, id);
            }

            if (gone) {
                foreach (var id in lift.ToldRides.Keys.ToList()) {
                    if (!IsMember(members, id)) {
                        lift.ToldRides.Remove(id);
                    }
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

        // What the member's own game told last of where they are on this lift, while it moves
        lift.ToldRides.TryGetValue(member.Id, out var told);

        var position = container.transform.position;
        if (ride == null) {
            if (!lift.Contains(position)) {
                return;
            }

            // A member who stepped off it a moment ago is not taken on again by positions sent before they did
            if (told is { Riding: false } && Time.unscaledTime - told.At < LiftRideLeftHold) {
                return;
            }

            var start = position - liftPosition;
            ride = new AvatarRide {
                Container = container, Offset = start, StartedAt = Time.unscaledTime,
                StartAlong = AlongLift(lift, start), Along = AlongLift(lift, start)
            };
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
            //
            // Along the way it is looked for at how far from the lift the ride began, which is on it, rather than where
            // the avatar is shown: a jump on the lift may show it higher than the lift reaches.
            var across = lift.MovesSideways
                ? new Vector3(liftPosition.x + ride.StartAlong, position.y, position.z)
                : new Vector3(position.x, liftPosition.y + ride.StartAlong, position.z);
            if (!lift.Contains(across)) {
                EndAvatarRide(lift, member.Id);

                return;
            }

            var offset = position - liftPosition;
            ride.Offset = Time.unscaledTime - lift.MovedAt > LiftAvatarSettleTime ? offset :
                lift.MovesSideways ? new Vector3(ride.Offset.x, offset.y, offset.z) :
                new Vector3(offset.x, ride.Offset.y, offset.z);
        }

        // Along its way, while it moves, where the member's own game says they are on it, once it said so in this trip.
        // Their positions lag behind the lift, so how far from it they were as the ride began was kept, which held them
        // there through every jump, and up in the air through the whole ride after one they made as it set off (USER
        // 11-01 "电梯启动的那一刻跳了起来就会被固定在跳起来的时候相对电梯的高度上"). A word of the trip counts for a
        // ride of the avatar that began later in it too: one standing still told it once, as the lift set off
        if (told != null && told.At >= lift.SetOffAt - LiftRideEarly &&
            Time.unscaledTime - lift.MovedAt <= LiftAvatarSettleTime) {
            if (!told.Riding) {
                Logger.Info(
                    $"{member.Username} stepped off the lift '{lift.Path}' while it moves, so their figure lets go of it"
                );
                EndAvatarRide(lift, member.Id);
                return;
            }

            if (!ride.Told) {
                ride.Told = true;
                Logger.Info(
                    $"The figure of {member.Username} on the lift '{lift.Path}' goes by where their game says they " +
                    $"are on it ({told.Along:0.00} along its way, {AlongLift(lift, ride.Offset):0.00} before)"
                );
            }

            ride.Along = Mathf.SmoothDamp(
                ride.Along, told.Along, ref ride.AlongSpeed, LiftRideSmoothTime, Mathf.Infinity, Time.unscaledDeltaTime
            );
            ride.Offset = lift.MovesSideways
                ? new Vector3(ride.Along, ride.Offset.y, ride.Offset.z)
                : new Vector3(ride.Offset.x, ride.Along, ride.Offset.z);
        } else {
            ride.Along = AlongLift(lift, ride.Offset);
            ride.AlongSpeed = 0f;
        }

        var placed = liftPosition + ride.Offset;
        container.transform.position = placed;
        ride.Placed = placed;
    }

    /// <summary>
    /// Whether a player is one of the given members.
    /// </summary>
    private static bool IsMember(List<ClientPlayerData> members, ushort id) {
        foreach (var member in members) {
            if (member.Id == id) {
                return true;
            }
        }

        return false;
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
