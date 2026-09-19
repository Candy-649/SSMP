using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Lifts in a checked two-player save, which are in the same place in both games. While both players are in a room, the
/// game of the player with the larger save key decides about its lifts: it plays the rides that its player starts and
/// serves the calls that the other game sends instead of starting a ride itself, and the other game plays the same
/// rides. A started ride goes at once, and a call from the other stop waits until the lift arrived. A lift waits at a
/// stop for a short time while the other player is on it or at that stop, then serves a call if the caller still waits.
/// A player who enters a room takes the lifts from the game of a partner who has been in it longer. Nobody is moved: the
/// avatar of a partner who rides a lift moves with the lift. The kinds of lifts are in CoopSave.CageLift and
/// CoopSave.FsmLift.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How long a lift waits at a stop after it arrived, in seconds, while the other player is on it or at that stop.
    /// </summary>
    private const float LiftWaitTime = 2.5f;

    /// <summary>
    /// How long a call of a lift that waits for its turn lives, in seconds.
    /// </summary>
    private const float LiftCallLifetime = 30f;

    /// <summary>
    /// How often a game that doesn't decide about a lift sends the same call again, in seconds.
    /// </summary>
    private const float LiftCallResendTime = 1f;

    /// <summary>
    /// How long after entering a room the game asks for the state of its lifts, in seconds, so the lifts started.
    /// </summary>
    private const float LiftStateRequestDelay = 0.5f;

    /// <summary>
    /// How long is left between saying that lifts could not be synced, in seconds.
    /// </summary>
    private const float LiftErrorLogTime = 10f;

    /// <summary>
    /// How long a lift is asked again to take a ride of the partner that it could not take at once, in seconds.
    /// </summary>
    private const float LiftMoveRetryTime = 2f;

    /// <summary>
    /// How long a game that asked for the state of the lifts of its room doesn't decide about them, in seconds, unless
    /// the state arrives first.
    /// </summary>
    private const float LiftStateWaitTime = 2f;

    /// <summary>
    /// The height difference below which a lift counts as in the same place in both games.
    /// </summary>
    private const float LiftSameHeight = 0.5f;

    /// <summary>
    /// The difference in the time that both players have been in a room, in seconds, below which the save key decides
    /// whose lifts both games take.
    /// </summary>
    private const float LiftRoomTimeTie = 0.25f;

    /// <summary>
    /// How far a caller may be from a stop of a lift, sideways and up or down, and still wait there.
    /// </summary>
    private static readonly Vector2 LiftCallReach = new(8f, 4f);

    /// <summary>
    /// The hooks of lifts.
    /// </summary>
    private readonly List<Hook> _liftHooks = [];

    /// <summary>
    /// The lifts of the current room that the sync looked at, by the component or FSM that runs them.
    /// </summary>
    private readonly Dictionary<object, SyncedLift> _lifts = new();

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
    /// When the last error of lifts was said.
    /// </summary>
    private float _liftFailedAt;

    /// <summary>
    /// A lift that the sync keeps in the same place in both games. Its stops count from 0.
    /// </summary>
    private abstract class SyncedLift {
        /// <summary>
        /// The path of the object of the lift in its scene.
        /// </summary>
        public required string Path { get; init; }

        /// <summary>
        /// The component that runs the lift.
        /// </summary>
        public abstract MonoBehaviour Owner { get; }

        /// <summary>
        /// The name of the FSM that runs the lift, or empty for a lift that a component runs.
        /// </summary>
        public abstract string FsmName { get; }

        /// <summary>
        /// The object that moves.
        /// </summary>
        public virtual Transform Transform => Owner.transform;

        /// <summary>
        /// Whether the lift is on a ride.
        /// </summary>
        public abstract bool IsMoving { get; }

        /// <summary>
        /// Whether players can use the lift.
        /// </summary>
        public virtual bool IsUnlocked => true;

        /// <summary>
        /// The stop that the lift stands at, or goes to while it moves.
        /// </summary>
        public abstract int Stop { get; }

        /// <summary>
        /// Whether the lift has a stop.
        /// </summary>
        public abstract bool HasStop(int stop);

        /// <summary>
        /// Whether a position is in or on the lift where it is now.
        /// </summary>
        public abstract bool Contains(Vector3 position);

        /// <summary>
        /// Whether a position is where a player waits for the lift at a stop.
        /// </summary>
        public abstract bool IsAtStop(int stop, Vector3 position);

        /// <summary>
        /// Starts a ride to a stop, or turns a ride around, returning whether the lift now goes there.
        /// </summary>
        /// <param name="stop">The stop.</param>
        /// <param name="camera">Whether the ride may move the camera, for when the local hero rides it.</param>
        /// <param name="skippedDelay">How much of the wait before the lift moves to skip, in seconds.</param>
        public abstract bool Move(int stop, bool camera, float skippedDelay);

        /// <summary>
        /// Puts the lift standing at a stop at once.
        /// </summary>
        /// <param name="stop">The stop.</param>
        /// <param name="value">Where the lift stands in the other game (see <see cref="StateValue"/>).</param>
        public abstract void PlaceAt(int stop, float value);

        /// <summary>
        /// Continues a ride to a stop at once from where the lift is in the other game, for a ride that is under way
        /// there.
        /// </summary>
        public abstract void JoinRide(int stop, float value);

        /// <summary>
        /// Where the lift is, for the state that the game sends: its height, unless its kind says otherwise.
        /// </summary>
        public virtual float StateValue => Transform.position.y;

        /// <summary>
        /// The difference of <see cref="StateValue"/> below which the lift counts as in the same place in both games.
        /// </summary>
        public virtual float StateTolerance => LiftSameHeight;

        /// <summary>
        /// Whether a player can call the lift to a stop.
        /// </summary>
        public virtual bool CanCall => true;

        /// <summary>
        /// Whether the lift moves sideways rather than up and down.
        /// </summary>
        public virtual bool MovesSideways => false;

        /// <summary>
        /// Whether the local hero is in or on the lift.
        /// </summary>
        public bool ContainsHero(HeroController hero) {
            return hero.transform.IsChildOf(Transform) || Contains(hero.transform.position);
        }

        /// <summary>
        /// Unlocks the lift for a ride of the partner, whose game unlocked it first.
        /// </summary>
        public virtual void Unlock() {
        }

        /// <summary>
        /// Called every frame before the calls that wait are served.
        /// </summary>
        public virtual void Update() {
        }

        /// <summary>
        /// Called when a call of the local player doesn't start a ride at once, to undo what the call already did.
        /// </summary>
        public virtual void OnCallHeld() {
        }

        /// <summary>
        /// Called before a ride of the local player starts at once.
        /// </summary>
        /// <param name="partnerInside">Whether the avatar of the partner is in or on the lift.</param>
        public virtual void BeforeLocalRide(bool partnerInside) {
        }

        /// <summary>
        /// Puts the lift at a height where it stands.
        /// </summary>
        public void SetHeight(float height) {
            var position = Transform.position;
            if (Mathf.Abs(position.y - height) > LiftSameHeight) {
                position.y = height;
                Transform.position = position;
            }
        }

        /// <summary>
        /// Whether the lift moved in the last frame.
        /// </summary>
        public bool WasMoving { get; set; }

        /// <summary>
        /// When the lift last arrived at a stop.
        /// </summary>
        public float ArrivedAt { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// The count of the newest ride of the partner that the lift played.
        /// </summary>
        public ulong PartnerRide { get; set; }

        /// <summary>
        /// The calls that wait for the lift, oldest first.
        /// </summary>
        public List<LiftCall> Calls { get; } = [];

        /// <summary>
        /// The stop of the last call that the local game sent to the game that decides, or -1.
        /// </summary>
        public int SentCallStop { get; set; } = -1;

        /// <summary>
        /// When the local game last sent a call to the game that decides.
        /// </summary>
        public float SentCallTime { get; set; } = float.NegativeInfinity;

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

        /// <summary>
        /// Where the lift was when the sync last looked at it for the avatar.
        /// </summary>
        public Vector3 LastPosition { get; set; }

        /// <summary>
        /// When the lift last changed its position.
        /// </summary>
        public float MovedAt { get; set; } = float.NegativeInfinity;
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
        /// Whether the caller is in or on the lift and rides it, rather than waiting at the stop.
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
        RegisterCageLiftHooks();
        RegisterFsmLiftHooks();
        RegisterCarriageHooks();
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
    /// Logs an error of lifts, at most every <see cref="LiftErrorLogTime"/> seconds.
    ///
    /// Said again rather than once ever. One throw in here stops every lift in the room from being updated at all,
    /// every frame, for as long as it keeps throwing - and saying it once left that looking like a single stray
    /// line in the log rather than the thing that had stopped everything.
    /// </summary>
    private void LogLiftError(Exception e) {
        if (_liftFailed && Time.unscaledTime - _liftFailedAt < LiftErrorLogTime) {
            return;
        }

        _liftFailed = true;
        _liftFailedAt = Time.unscaledTime;
        Logger.Error($"Could not sync a lift of the two-player save:\n{e}");
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

        return !IsWaitingForLiftState() && !PartnerKeyWins();
    }

    /// <summary>
    /// Whether the local player entered the current room a moment ago and the game still waits for the state of its lifts
    /// from a partner in it, during which neither game may decide about them yet.
    /// </summary>
    private bool IsWaitingForLiftState() {
        return !_liftStateReceived && Time.unscaledTime - _liftRoomStart < LiftStateRequestDelay + LiftStateWaitTime;
    }

    /// <summary>
    /// Whether a room of an update is loaded in the local game.
    /// </summary>
    private static bool IsLiftRoom(string scene) => scene.Length > 0 && SceneManager.GetSceneByName(scene).isLoaded;

    /// <summary>
    /// Finds the lifts of the current room.
    /// </summary>
    private List<SyncedLift> FindRoomLifts() {
        var lifts = new List<SyncedLift>();
        AddRoomCageLifts(lifts);
        AddRoomFsmLifts(lifts);
        AddRoomCarriages(lifts);
        return lifts;
    }

    /// <summary>
    /// Finds the lift of an update in the local game.
    /// </summary>
    private SyncedLift? FindLift(CoopSaveUpdate update) {
        if (ScenePath.Find(update.ObjectPath, update.Scene) is not { } target) {
            return null;
        }

        if (update.FsmName.Length == 0) {
            return target.TryGetComponent<LiftControl>(out var control) ? GetCageLift(control) : null;
        }

        if (update.FsmName == CarriageKind) {
            return target.TryGetComponent<ManualLift>(out var carriage) ? GetCarriage(carriage) : null;
        }

        foreach (var component in target.GetComponents<PlayMakerFSM>()) {
            if (component.FsmName == update.FsmName) {
                return GetFsmLift(component.Fsm);
            }
        }

        return null;
    }

    /// <summary>
    /// Forgets the lifts of the room that the local player left.
    /// </summary>
    private void OnLiftSceneChanged() {
        ResetLifts();
        _liftRoomStart = Time.unscaledTime;
    }

    /// <summary>
    /// Forgets the lifts, for when the room or the session ends.
    /// </summary>
    private void ResetLifts() {
        EndAvatarRides();
        _lifts.Clear();
        ResetFsmLifts();
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
                if (FindRoomLifts().Count > 0) {
                    Send(new CoopSaveUpdate {
                        TargetId = partner.Id,
                        Kind = CoopSaveUpdateKind.LiftStateRequest,
                        Scene = SceneManager.GetActiveScene().name
                    });
                } else {
                    _liftStateReceived = true;
                }
            }

            var liftPartner = partner.IsInLocalScene ? partner : null;
            var decides = DecidesLifts(liftPartner);
            List<object>? gone = null;
            foreach (var entry in _lifts) {
                if (entry.Value.Owner == null) {
                    (gone ??= []).Add(entry.Key);
                    continue;
                }

                UpdateLift(entry.Value, liftPartner, decides);
            }

            if (gone != null) {
                foreach (var key in gone) {
                    // A lift that is gone can't end the ride of the avatar on it anymore
                    EndAvatarRide(_lifts[key]);
                    _lifts.Remove(key);
                }
            }

            UpdateStoryLifts(partner);
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Notices when a lift arrives and serves its calls that wait when the local game decides about it.
    /// </summary>
    private void UpdateLift(SyncedLift lift, ClientPlayerData? partner, bool decides) {
        lift.Update();
        if (lift is FsmLift fsmLift) {
            UpdateFsmLiftRide(fsmLift, partner);
        }

        var moving = lift.IsMoving;
        if (lift.WasMoving && !moving) {
            lift.ArrivedAt = Time.unscaledTime;
        }

        lift.WasMoving = moving;
        if (!decides) {
            // Calls that came while both games waited for the state of the room wait for the game that decides
            if (!IsWaitingForLiftState()) {
                lift.Calls.Clear();
            }

            return;
        }

        if (moving || lift.Calls.Count == 0) {
            return;
        }

        var currentStop = lift.Stop;
        lift.Calls.RemoveAll(call =>
            call.Stop == currentStop || Time.unscaledTime - call.Time > LiftCallLifetime ||
            !IsCallerWaiting(lift, call, partner)
        );
        if (lift.Calls.Count == 0 || IsLiftHeldForOther(lift, lift.Calls[0].ByPartner, partner)) {
            return;
        }

        var hero = HeroController.instance;
        StartLiftRide(lift, lift.Calls[0].Stop, hero != null && lift.ContainsHero(hero), partner);
    }

    /// <summary>
    /// Handles a ride or a call that the local player started: the game that decides starts it at once or lets it wait
    /// for its turn, and the other game sends it to the game that decides.
    /// </summary>
    /// <param name="lift">The lift.</param>
    /// <param name="stop">The stop that the player wants the lift to go to.</param>
    /// <param name="inside">Whether the local hero is in or on the lift.</param>
    /// <param name="camera">Whether the ride may move the camera.</param>
    /// <param name="partner">The partner in the room.</param>
    private void RequestLiftRide(SyncedLift lift, int stop, bool inside, bool camera, ClientPlayerData partner) {
        if (!DecidesLifts(partner)) {
            lift.OnCallHeld();
            if (lift.SentCallStop != stop || Time.unscaledTime - lift.SentCallTime >= LiftCallResendTime) {
                lift.SentCallStop = stop;
                lift.SentCallTime = Time.unscaledTime;
                Send(new CoopSaveUpdate {
                    TargetId = partner.Id,
                    Kind = CoopSaveUpdateKind.LiftCall,
                    Scene = lift.Owner.gameObject.scene.name,
                    ObjectPath = lift.Path,
                    FsmName = lift.FsmName,
                    Part = (ushort) stop,
                    PartCount = (ushort) (inside ? 1 : 0)
                });
            }

            // If this game turns out to decide once the state of the room arrived, it serves the call itself
            if (IsWaitingForLiftState()) {
                QueueLiftCall(lift, stop, false, inside);
            }

            return;
        }

        if (lift.IsMoving || IsLiftHeldForOther(lift, false, partner)) {
            lift.OnCallHeld();
            QueueLiftCall(lift, stop, false, inside);
            return;
        }

        lift.BeforeLocalRide(IsAvatarOn(lift, partner));
        if (!StartLiftRide(lift, stop, camera, partner)) {
            QueueLiftCall(lift, stop, false, inside);
        }
    }

    /// <summary>
    /// Lets a call wait for the lift, replacing an earlier call of the same player to the same stop.
    /// </summary>
    private static void QueueLiftCall(SyncedLift lift, int stop, bool byPartner, bool inside) {
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
    /// <returns>Whether the ride started.</returns>
    private bool StartLiftRide(SyncedLift lift, int stop, bool camera, ClientPlayerData? partner) {
        var fromY = lift.Transform.position.y;
        bool started;
        _liftReplaying = true;
        try {
            started = lift.Move(stop, camera, 0f);
        } finally {
            _liftReplaying = false;
        }

        if (!started) {
            return false;
        }

        lift.WasMoving = true;
        lift.Calls.RemoveAll(call => call.Stop == stop);
        if (partner != null) {
            SendLiftMove(lift, stop, fromY, partner.Id);
        }

        return true;
    }

    /// <summary>
    /// Sends a ride that started in the local game, or turned around, to the partner.
    /// </summary>
    private void SendLiftMove(SyncedLift lift, int stop, float fromY, ushort partnerId) {
        Send(new CoopSaveUpdate {
            TargetId = partnerId,
            Kind = CoopSaveUpdateKind.LiftMove,
            Scene = lift.Owner.gameObject.scene.name,
            ObjectPath = lift.Path,
            FsmName = lift.FsmName,
            Part = (ushort) stop,
            Key = ++_liftRideCount,
            Values = [fromY]
        });
    }

    /// <summary>
    /// Whether a lift that arrived a moment ago still waits for the player other than the one who wants it, who is on it
    /// or at its stop.
    /// </summary>
    private static bool IsLiftHeldForOther(SyncedLift lift, bool byPartner, ClientPlayerData? partner) {
        if (Time.unscaledTime - lift.ArrivedAt >= LiftWaitTime) {
            return false;
        }

        if (byPartner) {
            var hero = HeroController.instance;
            return hero != null && (lift.ContainsHero(hero) || lift.IsAtStop(lift.Stop, hero.transform.position));
        }

        return partner?.PlayerContainer is { } container &&
               (IsAvatarOn(lift, partner) || lift.IsAtStop(lift.Stop, container.transform.position));
    }

    /// <summary>
    /// Whether the player of a call still waits for the lift: on it for a ride, or at the stop for a call.
    /// </summary>
    private static bool IsCallerWaiting(SyncedLift lift, LiftCall call, ClientPlayerData? partner) {
        Vector3 position;
        bool inside;
        if (call.ByPartner) {
            if (partner?.PlayerContainer is not { } container) {
                return false;
            }

            position = container.transform.position;
            inside = IsAvatarOn(lift, partner);
        } else {
            if (HeroController.instance is not { } hero) {
                return false;
            }

            position = hero.transform.position;
            inside = lift.ContainsHero(hero);
        }

        // A partner standing on their own lift is taken at their word. Their game looked at their own hero and their
        // own lift and said so; asking whether their body is inside the lift over here asks about a different lift,
        // and the one moment that matters is the moment the two lifts are not in the same place. That is exactly
        // when this was answering no: they rode up, this lift stayed down, and from then on every call they sent
        // was thrown away on the grounds that they were not standing on a lift they were nowhere near - forever,
        // because nothing ever asks twice. Stepping onto a lift is one event, sent once, and never sent again.
        if (call.Inside) {
            return !call.ByPartner || inside || partner is { IsInLocalScene: true };
        }

        return !inside && lift.IsAtStop(call.Stop, position);
    }

    /// <summary>
    /// Whether the avatar of the partner is in or on a lift, or rides it.
    /// </summary>
    private static bool IsAvatarOn(SyncedLift lift, ClientPlayerData? partner) {
        return lift.AvatarRiding ||
               partner?.PlayerContainer is { } container && lift.Contains(container.transform.position);
    }

    /// <summary>
    /// Whether a position is within the reach of a caller around a point.
    /// </summary>
    private static bool IsWithinCallReach(Vector3 point, Vector3 position) {
        return Mathf.Abs(point.x - position.x) <= LiftCallReach.x && Mathf.Abs(point.y - position.y) <= LiftCallReach.y;
    }
}
