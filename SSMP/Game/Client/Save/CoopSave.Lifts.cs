using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Lifts in a checked two-player save, which are in the same place in every game. While members are in a room together,
/// the game of the one of them with the largest save key decides about its lifts: it plays the rides that its player
/// starts and serves the calls that the other games send instead of starting a ride themselves, and the other games play
/// the same rides. A started ride goes at once, and a call from the other stop waits until the lift arrived. A lift waits
/// at a stop for a short time while another player is on it or at that stop, then serves a call if the caller still
/// waits. A player who enters a room takes the lifts from the game of a member who has been in it longer. Nobody is
/// moved: the avatar of a member who rides a lift moves with the lift. The kinds of lifts are in CoopSave.CageLift and
/// CoopSave.FsmLift.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How long a lift waits at a stop after it arrived, in seconds, while another player is on it or at that stop.
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
    /// How long a lift is asked again to take a ride of a member that it could not take at once, in seconds.
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
    /// The difference in the time that two players have been in a room, in seconds, below which the save key decides
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
    /// Whether a member sent the state of the lifts of the current room.
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
    /// A lift that the sync keeps in the same place in every game. Its stops count from 0.
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
        /// Unlocks the lift because the game of a member unlocked it, when it did or before a ride of theirs.
        /// </summary>
        public virtual void Unlock() {
        }

        /// <summary>
        /// Whether a plate calls or unlocks the lift.
        /// </summary>
        public virtual bool HasPlate(TempPressurePlate plate) => false;

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
        /// <param name="memberInside">Whether the avatar of a member is in or on the lift.</param>
        public virtual void BeforeLocalRide(bool memberInside) {
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
        /// The stop the members in the room have been told this lift is on its way to, or -1 when they have not been
        /// told of a ride at all.
        ///
        /// This is what decides whether a ride is news. It replaced asking which state the lift was in a frame ago,
        /// which only recognised a ride that began from one of a handful of named states and so missed every other
        /// way one can begin - being unlocked, turning around through the state that picks a direction, and worst,
        /// being put where the partner says the lift stands, which runs the lift's own state machine far enough
        /// inside that one call to set it off again before anything looks.
        /// </summary>
        public int ToldStop { get; set; } = -1;

        /// <summary>
        /// When the lift last arrived at a stop.
        /// </summary>
        public float ArrivedAt { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// The count of the newest ride of each member that the lift played, by member: each game counts its own.
        /// </summary>
        public Dictionary<ushort, ulong> MemberRides { get; } = new();

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
        /// The avatars of members that ride the lift, by member.
        /// </summary>
        public Dictionary<ushort, AvatarRide> Avatars { get; } = new();

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
    /// The avatar of a member riding a lift.
    /// </summary>
    private sealed class AvatarRide {
        /// <summary>
        /// The container of the avatar, whose prediction the ride turned off.
        /// </summary>
        public required GameObject Container { get; init; }

        /// <summary>
        /// Where the avatar is relative to the lift while it rides.
        /// </summary>
        public Vector3 Offset { get; set; }

        /// <summary>
        /// Where the sync last put the avatar.
        /// </summary>
        public Vector3 Placed { get; set; }
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
        /// The member who called, or null for the local player.
        /// </summary>
        public required ushort? By { get; init; }

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
        AddLiftHook(
            typeof(TempPressurePlate).GetMethod("Activate", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<TempPressurePlate>, TempPressurePlate>(OnLiftPlateActivate)
        );
        Application.onBeforeRender += OnLiftBeforeRender;
    }

    /// <summary>
    /// Hook for <see cref="TempPressurePlate.Activate"/>, which tells what a plate opens or calls that it went down. A
    /// plate that stands in for a member (see OnWorldTrigger) goes down here like theirs, with the plates beside it,
    /// but a lift that it calls or unlocks is not told: the lift takes its ride and its unlock from the updates that
    /// the game of that member sends about the lift, where their plate told it. Told here as well, the lift took the
    /// call for one of the local player's and made it a second time, and jumped closer to the stop first.
    /// </summary>
    private void OnLiftPlateActivate(Action<TempPressurePlate> orig, TempPressurePlate self) {
        if (self.player != self.gameObject || GetLiftMembers().Count == 0 ||
            !FindRoomLifts().Exists(lift => lift.HasPlate(self))) {
            orig(self);
            return;
        }

        // Down all the same, so the lift raises it again when it leaves that stop
        self.isActivated = true;
    }

    /// <summary>
    /// Lets a lift unlock in the local game and, if it was locked, sends that to the members in the room, whose games
    /// unlock the same lift (see <see cref="OnLiftUnlock"/>). Called from the hooks of the ways that lifts unlock.
    /// </summary>
    /// <param name="lift">The lift.</param>
    /// <param name="unlock">Unlocks it.</param>
    private void UnlockLift(SyncedLift lift, Action unlock) {
        var locked = !lift.IsUnlocked;
        unlock();
        if (!locked || _liftReplaying || GetLiftMembers().Count == 0) {
            return;
        }

        SendToRoomMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.LiftUnlock,
            Scene = lift.Owner.gameObject.scene.name,
            ObjectPath = lift.Path,
            FsmName = lift.FsmName
        });
        Logger.Info($"Sent unlocking the lift '{lift.Path}' to the members in the room");
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
    /// The members with whom the lifts of the current room are synced: checked, and in the room.
    /// </summary>
    private List<ClientPlayerData> GetLiftMembers() {
        return GetCheckedMembers().FindAll(member => member.IsInLocalScene);
    }

    /// <summary>
    /// The member whose game decides about the lifts of the current room, the one of the members in it with the largest
    /// save key, or null when that is the local game.
    /// </summary>
    private ClientPlayerData? GetLiftDecider(List<ClientPlayerData> members) {
        ClientPlayerData? decider = null;
        foreach (var member in members) {
            if (decider == null ? MemberKeyWins(member) : MemberKeyWins(member, decider)) {
                decider = member;
            }
        }

        return decider;
    }

    /// <summary>
    /// Whether the local game decides about the lifts of the current room.
    /// </summary>
    private bool DecidesLifts(List<ClientPlayerData> members) {
        if (members.Count == 0) {
            return true;
        }

        return !IsWaitingForLiftState() && GetLiftDecider(members) == null;
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
    /// Asks the members for the state of the lifts after entering a room, notices when lifts arrive, and serves the
    /// calls that wait.
    /// </summary>
    private void UpdateLifts() {
        try {
            if (!_liftStateRequested && Time.unscaledTime - _liftRoomStart >= LiftStateRequestDelay) {
                _liftStateRequested = true;
                if (FindRoomLifts().Count > 0) {
                    // To every member, of whom those in the room answer
                    SendToMembers(new CoopSaveUpdate {
                        Kind = CoopSaveUpdateKind.LiftStateRequest,
                        Scene = SceneManager.GetActiveScene().name
                    });
                } else {
                    _liftStateReceived = true;
                }
            }

            var members = GetLiftMembers();
            var decides = DecidesLifts(members);
            List<object>? gone = null;
            foreach (var entry in _lifts) {
                if (entry.Value.Owner == null) {
                    (gone ??= []).Add(entry.Key);
                    continue;
                }

                UpdateLift(entry.Value, members, decides);
            }

            if (gone != null) {
                foreach (var key in gone) {
                    // A lift that is gone can't end the rides of the avatars on it anymore
                    EndAvatarRides(_lifts[key]);
                    _lifts.Remove(key);
                }
            }

            UpdateStoryLifts();
        } catch (Exception e) {
            LogLiftError(e);
        }
    }

    /// <summary>
    /// Notices when a lift arrives and serves its calls that wait when the local game decides about it.
    /// </summary>
    private void UpdateLift(SyncedLift lift, List<ClientPlayerData> members, bool decides) {
        lift.Update();
        if (lift is FsmLift fsmLift) {
            UpdateFsmLiftRide(fsmLift, members);
        }

        var moving = lift.IsMoving;
        if (lift.WasMoving && !moving) {
            lift.ArrivedAt = Time.unscaledTime;

            // Nothing is on its way any more, so whatever the members were told about is over and the next ride is
            // news again whichever stop it goes to
            lift.ToldStop = -1;
        }

        lift.WasMoving = moving;
        if (!decides) {
            // Calls that came while the games waited for the state of the room wait for the game that decides
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
            !IsCallerWaiting(lift, call, members)
        );
        if (lift.Calls.Count == 0 || IsLiftHeldForOther(lift, lift.Calls[0].By, members)) {
            return;
        }

        var hero = HeroController.instance;
        StartLiftRide(lift, lift.Calls[0].Stop, hero != null && lift.ContainsHero(hero));
    }

    /// <summary>
    /// Handles a ride or a call that the local player started: the game that decides starts it at once or lets it wait
    /// for its turn, and the other games send it to the game that decides.
    /// </summary>
    /// <param name="lift">The lift.</param>
    /// <param name="stop">The stop that the player wants the lift to go to.</param>
    /// <param name="inside">Whether the local hero is in or on the lift.</param>
    /// <param name="camera">Whether the ride may move the camera.</param>
    /// <param name="members">The members in the room.</param>
    private void RequestLiftRide(SyncedLift lift, int stop, bool inside, bool camera, List<ClientPlayerData> members) {
        if (!DecidesLifts(members)) {
            lift.OnCallHeld();

            // To the game that decides, which is the one with the largest save key in the room. While the games wait
            // for the state of the room, that is the one that decides once it has arrived, and with only one other
            // member in the room the call goes to them either way.
            if ((GetLiftDecider(members) ?? (members.Count == 1 ? members[0] : null)) is { } decider &&
                (lift.SentCallStop != stop || Time.unscaledTime - lift.SentCallTime >= LiftCallResendTime)) {
                lift.SentCallStop = stop;
                lift.SentCallTime = Time.unscaledTime;
                Send(new CoopSaveUpdate {
                    TargetId = decider.Id,
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
                QueueLiftCall(lift, stop, null, inside);
            }

            return;
        }

        if (lift.IsMoving || IsLiftHeldForOther(lift, null, members)) {
            lift.OnCallHeld();
            QueueLiftCall(lift, stop, null, inside);
            return;
        }

        lift.BeforeLocalRide(members.Exists(member => IsAvatarOn(lift, member)));
        if (!StartLiftRide(lift, stop, camera)) {
            QueueLiftCall(lift, stop, null, inside);
        }
    }

    /// <summary>
    /// Lets a call wait for the lift, replacing an earlier call of the same player to the same stop.
    /// </summary>
    /// <param name="lift">The lift.</param>
    /// <param name="stop">The stop.</param>
    /// <param name="by">The member who called, or null for the local player.</param>
    /// <param name="inside">Whether the caller is in or on the lift.</param>
    private static void QueueLiftCall(SyncedLift lift, int stop, ushort? by, bool inside) {
        var call = lift.Calls.Find(other => other.Stop == stop && other.By == by);
        if (call == null) {
            call = new LiftCall { Stop = stop, By = by };
            lift.Calls.Add(call);
        }

        call.Inside = inside;
        call.Time = Time.unscaledTime;
    }

    /// <summary>
    /// Starts a ride of a lift in the local game and sends it to the members in the room.
    /// </summary>
    /// <param name="lift">The lift.</param>
    /// <param name="stop">The stop.</param>
    /// <param name="camera">Whether the ride may move the camera.</param>
    /// <param name="caller">A member whose call the ride serves, who is sent it even when this game doesn't see them
    /// in the room yet, or null.</param>
    /// <returns>Whether the ride started.</returns>
    private bool StartLiftRide(SyncedLift lift, int stop, bool camera, ClientPlayerData? caller = null) {
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
        SendLiftMove(lift, stop, fromY, caller);
        return true;
    }

    /// <summary>
    /// Sends a ride that started in the local game, or turned around, to the members in the room.
    /// </summary>
    /// <param name="lift">The lift.</param>
    /// <param name="stop">The stop it goes to.</param>
    /// <param name="fromY">Where it started from.</param>
    /// <param name="caller">A member whose call the ride serves, who is sent it even when this game doesn't see them
    /// in the room yet, or null.</param>
    private void SendLiftMove(SyncedLift lift, int stop, float fromY, ClientPlayerData? caller = null) {
        var members = GetLiftMembers();
        if (caller != null && !members.Exists(member => member.Id == caller.Id)) {
            members.Add(caller);
        }

        if (members.Count == 0) {
            return;
        }

        lift.ToldStop = stop;
        var update = new CoopSaveUpdate {
            TargetId = members[0].Id,
            Kind = CoopSaveUpdateKind.LiftMove,
            Scene = lift.Owner.gameObject.scene.name,
            ObjectPath = lift.Path,
            FsmName = lift.FsmName,
            Part = (ushort) stop,
            Key = ++_liftRideCount,
            Values = [fromY]
        };
        Send(update);
        for (var i = 1; i < members.Count; i++) {
            SendCopy(update, members[i].Id);
        }
    }

    /// <summary>
    /// Whether a lift that arrived a moment ago still waits for the players other than the one who wants it, who are
    /// on it or at its stop.
    /// </summary>
    /// <param name="lift">The lift.</param>
    /// <param name="by">The member who wants it, or null for the local player.</param>
    /// <param name="members">The members in the room.</param>
    private static bool IsLiftHeldForOther(SyncedLift lift, ushort? by, List<ClientPlayerData> members) {
        if (Time.unscaledTime - lift.ArrivedAt >= LiftWaitTime) {
            return false;
        }

        if (by != null) {
            var hero = HeroController.instance;
            if (hero != null && (lift.ContainsHero(hero) || lift.IsAtStop(lift.Stop, hero.transform.position))) {
                return true;
            }
        }

        return members.Exists(member =>
            member.Id != by && member.PlayerContainer is { } container &&
            (IsAvatarOn(lift, member) || lift.IsAtStop(lift.Stop, container.transform.position))
        );
    }

    /// <summary>
    /// Whether the player of a call still waits for the lift: on it for a ride, or at the stop for a call.
    /// </summary>
    private static bool IsCallerWaiting(SyncedLift lift, LiftCall call, List<ClientPlayerData> members) {
        Vector3 position;
        bool inside;
        ClientPlayerData? caller = null;
        if (call.By is { } by) {
            caller = members.Find(member => member.Id == by);
            if (caller?.PlayerContainer is not { } container) {
                return false;
            }

            position = container.transform.position;
            inside = IsAvatarOn(lift, caller);
        } else {
            if (HeroController.instance is not { } hero) {
                return false;
            }

            position = hero.transform.position;
            inside = lift.ContainsHero(hero);
        }

        // A member standing on their own lift is taken at their word. Their game looked at their own hero and their
        // own lift and said so; asking whether their body is inside the lift over here asks about a different lift,
        // and the one moment that matters is the moment the two lifts are not in the same place. That is exactly
        // when this was answering no: they rode up, this lift stayed down, and from then on every call they sent
        // was thrown away on the grounds that they were not standing on a lift they were nowhere near - forever,
        // because nothing ever asks twice. Stepping onto a lift is one event, sent once, and never sent again.
        if (call.Inside) {
            return call.By == null || inside || caller is { IsInLocalScene: true };
        }

        return !inside && lift.IsAtStop(call.Stop, position);
    }

    /// <summary>
    /// Whether the avatar of a member is in or on a lift, or rides it.
    /// </summary>
    private static bool IsAvatarOn(SyncedLift lift, ClientPlayerData member) {
        return lift.Avatars.ContainsKey(member.Id) ||
               member.PlayerContainer is { } container && lift.Contains(container.transform.position);
    }

    /// <summary>
    /// Whether a position is within the reach of a caller around a point.
    /// </summary>
    private static bool IsWithinCallReach(Vector3 point, Vector3 position) {
        return Mathf.Abs(point.x - position.x) <= LiftCallReach.x && Mathf.Abs(point.y - position.y) <= LiftCallReach.y;
    }
}
