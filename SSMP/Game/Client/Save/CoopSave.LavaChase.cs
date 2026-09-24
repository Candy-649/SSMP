using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The lava that rises behind the players and has to be outrun, played the way a game made for two plays a chase: one
/// lava for both of them, which keeps coming whatever happens to either. The game's own lava only knows its own
/// player, so each game had one of its own that chased only its own player, and the two of them never raced the same
/// thing. Now it chases whichever of the two is lower, the game that runs the room says where it is, and the other
/// game follows. A player it catches is not set down below it again: a burn they live through puts them back beside
/// the other player, and a death has them stand up beside the other player a few seconds later, without a cocoon. Only
/// when the other player cannot take them - down themselves, or with nowhere safe to stand - does a burn put them back
/// the game's own way, with the lava pulled down below them for both, as the game does it for one player.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The name of the FSM that raises the lava. The game uses it for every lava that chases the player.
    /// </summary>
    private const string RiseControlFsmName = "Rise Control";

    /// <summary>
    /// The variable through which the actions of that FSM ask where the player is. It is one of the game's global
    /// variables, shared by every FSM, which is why the actions of this FSM are pointed elsewhere one by one instead.
    /// </summary>
    private const string ChasedVariableName = "Hero";

    /// <summary>
    /// The states of that FSM in which the lava is not after anyone: before it starts, and after it has reached the
    /// top. Every other state is part of the chase.
    /// </summary>
    private static readonly HashSet<string> LavaRestingStates = [
        "Init", "Check Entry", "Hidden", "Idle", "Completed", "Already Completed", "End", "End Effects",
        "Set Death Respawn", "Reset Camlocks", "Reset Lava Box"
    ];

    /// <summary>
    /// The states in which the lava has stopped for a burn and is being set down below the player the game put back,
    /// which is how the game gives one player another go.
    /// </summary>
    private static readonly HashSet<string> LavaResetStates = [
        "Damage Stop", "Set Restart Time", "Wait", "Hazard Respawn"
    ];

    /// <summary>
    /// The state in which the lava has been set down for a burn and waits for the player to be back on their feet.
    /// </summary>
    private const string LavaSetDownStateName = "Wait";

    /// <summary>
    /// The variable holding how long the lava pauses once the player it was set down for is back on their feet.
    /// </summary>
    private const string RestartPauseVariableName = "Restart Pause";

    /// <summary>
    /// What the death effects of a burn tell the lava so that it stops: the one of lava says the first, and those of
    /// spikes, steam, coal and lightning the second.
    /// </summary>
    private static readonly string[] LavaStopEvents = ["LAVA DEATH", "KNIGHT SPIKE HIT"];

    /// <summary>
    /// What a listener of the lava for one of those is switched to while the lava is not to stop. Nothing ever says it.
    /// </summary>
    private const string MutedLavaStopPrefix = "SSMP MUTED ";

    /// <summary>
    /// How often each game tells the other where its lava is and where its player last stood safely, in seconds.
    /// </summary>
    private const float LavaSampleInterval = 0.2f;

    /// <summary>
    /// How long what the other game said about its lava is followed, in seconds. Past that the lava of this game
    /// goes its own way again, which is what it does when the other player has left.
    /// </summary>
    private const float LavaSampleLifetime = 1f;

    /// <summary>
    /// How long a place a player last stood safely on stays good for putting someone there, in seconds.
    /// </summary>
    private const float LavaSafeSpotLifetime = 3f;

    /// <summary>
    /// How far above the top of the lava a place must be to put a player there, measured from the middle of a player
    /// standing there. The lava does not stop for them: near the players it rises about 2.5 a second, a player put back
    /// after a burn cannot move for about 1.3 s, and they need half a second more to get going, which with the height
    /// of the player comes to about this.
    /// </summary>
    private const float LavaClearance = 6f;

    /// <summary>
    /// The layers on which the game looks for the ground below the place it puts a player back after a burn.
    /// </summary>
    private const int PutBackGroundLayerMask = 8448;

    /// <summary>
    /// How much further down than the feet of a player that ground may be below them, which covers slopes and small
    /// steps.
    /// </summary>
    private const float PutBackGroundSlack = 0.5f;

    /// <summary>
    /// How long a player who died in the chase lies down before they stand up beside the other player, in seconds.
    /// </summary>
    private const float ChaseStandUpDelay = 3f;

    /// <summary>
    /// Roughly how long a burn takes to put its player back on their feet after the lava was set down for it, in
    /// seconds, which the game that runs the room holds its lava for on top of the pause the lava makes itself.
    /// </summary>
    private const float LavaSetDownHoldExtra = 1f;

    /// <summary>
    /// How quickly the speed of the other player, which is worked out from where their body is drawn, follows it.
    /// </summary>
    private const float AvatarVelocitySmoothing = 0.1f;

    /// <summary>
    /// Whether this game runs the room the local player is in. Given by the entity manager.
    /// </summary>
    public Func<bool>? IsSceneHost { get; set; }

    /// <summary>
    /// Whether another game runs the room the local player is in. Given by the entity manager.
    /// </summary>
    public Func<bool>? IsSceneClient { get; set; }

    /// <summary>
    /// The lava that chases in the room the local player is in, or null if there is none.
    /// </summary>
    private LavaChase? _lavaChase;

    /// <summary>
    /// The FSM of a lava of this room that was found before it was set up, and is set up once it is.
    /// </summary>
    private PlayMakerFSM? _pendingLavaFsm;

    /// <summary>
    /// Counts what this game said about its lava, so that the other game can tell the newest from one that arrived
    /// late.
    /// </summary>
    private ulong _lavaChaseSequence;

    /// <summary>
    /// When this game next says where its lava is, in unscaled seconds.
    /// </summary>
    private float _nextLavaSampleTime;

    /// <summary>
    /// Where the game puts the local player back after the burn they are in, instead of its own mark, or null.
    /// </summary>
    private Vector2? _burnRedirect;

    /// <summary>
    /// When <see cref="_burnRedirect"/> was set, in seconds of game time.
    /// </summary>
    private float _burnRedirectTime;

    /// <summary>
    /// A partner who died in the chase and is to stand up beside the local player, or null.
    /// </summary>
    private ChaseStandUp? _chaseStandUp;

    /// <summary>
    /// The partner whose body was taken off the screen for lying down in the chase, or null.
    /// </summary>
    private ushort? _chaseHiddenPartner;

    /// <summary>
    /// Detour hook for the burn of the local player.
    /// </summary>
    private Hook? _burnHook;

    /// <summary>
    /// Detour hook for the game telling the room that the local player has been put back after a burn.
    /// </summary>
    private Hook? _respawnResetHook;

    /// <summary>
    /// Whether a failure of this has been logged already.
    /// </summary>
    private bool _lavaChaseFailed;

    /// <summary>
    /// A lava that chases, in the room the local player is in.
    /// </summary>
    private sealed class LavaChase {
        public LavaChase(PlayMakerFSM fsm, Rigidbody2D body, GameObject target, Rigidbody2D targetBody, string scene) {
            Fsm = fsm;
            Body = body;
            Target = target;
            TargetBody = targetBody;
            Scene = scene;
        }

        /// <summary>
        /// The FSM that raises it.
        /// </summary>
        public PlayMakerFSM Fsm { get; }

        /// <summary>
        /// The room it is in.
        /// </summary>
        public string Scene { get; }

        /// <summary>
        /// The body it moves by.
        /// </summary>
        public Rigidbody2D Body { get; }

        /// <summary>
        /// What its FSM is told the player is: an object that stands where the player it chases is.
        /// </summary>
        public GameObject Target { get; }

        /// <summary>
        /// The body of <see cref="Target"/>, which carries the speed of that player for the actions that ask for it.
        /// </summary>
        public Rigidbody2D TargetBody { get; }

        /// <summary>
        /// The part of it that burns, or null if it has none.
        /// </summary>
        public Collider2D? Band { get; set; }

        /// <summary>
        /// How far above its body the top of the part that burns is, once that part has been seen switched on.
        /// </summary>
        public float? BandTopOffset { get; set; }

        /// <summary>
        /// How high the top of it was when last seen, for once it is gone with the room it was in.
        /// </summary>
        public float? LastTop { get; set; }

        /// <summary>
        /// The listeners through which a burn stops it.
        /// </summary>
        public List<EventRegister> Stops { get; } = [];

        /// <summary>
        /// When those listeners were switched off for a burn that does not stop it, or a negative number while they
        /// are on.
        /// </summary>
        public float MutedAt { get; set; } = -1f;

        /// <summary>
        /// The newest of what the other game said that was used.
        /// </summary>
        public ulong PartnerSequence { get; set; }

        /// <summary>
        /// How high the other game had its lava, how fast it was rising, whether it was after anyone, and when that
        /// arrived, in seconds of game time.
        /// </summary>
        public float SampleY { get; set; }

        public float SampleVelocity { get; set; }

        public bool SampleChasing { get; set; }

        public float SampleTime { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// Where the other player last stood safely, and when that arrived.
        /// </summary>
        public Vector2 PartnerSafeSpot { get; set; }

        public float PartnerSafeTime { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// Where the local player last stood safely, and when.
        /// </summary>
        public Vector2 SafeSpot { get; set; }

        public float SafeTime { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// Where a burn in the other game set the lava down, and until when it is held there.
        /// </summary>
        public float HoldY { get; set; }

        public float HoldUntil { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// Whether the setting down of the lava for the current burn has been told to the game that runs the room.
        /// </summary>
        public bool SetDownReported { get; set; }

        /// <summary>
        /// Where the body of the other player was drawn last frame, and how fast it moved.
        /// </summary>
        public Vector2 AvatarPosition { get; set; }

        public float AvatarTime { get; set; } = float.NegativeInfinity;

        public Vector2 AvatarVelocity { get; set; }
    }

    /// <summary>
    /// A partner who died in the chase, to be stood up beside the local player.
    /// </summary>
    private sealed class ChaseStandUp {
        public ChaseStandUp(ushort playerId, ulong key, string scene, float at) {
            PlayerId = playerId;
            Key = key;
            Scene = scene;
            At = at;
        }

        /// <summary>
        /// The player who died.
        /// </summary>
        public ushort PlayerId { get; }

        /// <summary>
        /// What they called this death.
        /// </summary>
        public ulong Key { get; }

        /// <summary>
        /// The room they died in.
        /// </summary>
        public string Scene { get; }

        /// <summary>
        /// When they stand up, in unscaled seconds.
        /// </summary>
        public float At { get; }
    }

    /// <summary>
    /// Stands where the player the lava chases is and moves as they move, for the actions of the lava that ask where
    /// the player is. It goes before the lava's FSM in every frame, so that what they read is this frame.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    private sealed class LavaChaseTarget : MonoBehaviour {
        public Action<bool>? Follow { get; set; }

        private void Update() => Follow?.Invoke(true);

        private void FixedUpdate() => Follow?.Invoke(false);
    }

    /// <summary>
    /// Puts the lava of this game where the game that runs the room has it, after the lava's FSM has moved it.
    /// </summary>
    [DefaultExecutionOrder(100)]
    private sealed class LavaChaseFollower : MonoBehaviour {
        public Action? Correct { get; set; }

        private void FixedUpdate() => Correct?.Invoke();
    }

    /// <summary>
    /// Forgets the lava of the room that was left and looks for one in the room that was entered.
    /// </summary>
    /// <param name="scene">The room that was entered.</param>
    private void OnLavaChaseSceneChanged(Scene scene) {
        try {
            ForgetLavaChase();

            // Nothing is changed for a player who is not in a two-player save: there is nobody to chase but them
            if (GetCurrentMarker() == null) {
                return;
            }

            foreach (var fsm in Object.FindObjectsByType<PlayMakerFSM>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None
                     )) {
                if (fsm != null && fsm.FsmName == RiseControlFsmName && fsm.gameObject.scene == scene) {
                    _pendingLavaFsm = fsm;
                    break;
                }
            }

            TrySetUpLavaChase();
        } catch (Exception e) {
            LogLavaChaseError(e);
        }
    }

    /// <summary>
    /// Lets go of the lava of the room that was left.
    /// </summary>
    private void ForgetLavaChase() {
        if (_lavaChase is { } chase) {
            StandUpOnLeaving(chase);

            if (chase.Fsm != null) {
                UnmuteLavaStops(chase);
            }

            if (chase.Target != null) {
                Object.Destroy(chase.Target);
            }
        }

        _lavaChase = null;
        _pendingLavaFsm = null;
        _burnRedirect = null;

        if (_chaseHiddenPartner is { } hidden) {
            SetPartnerBodyHidden(hidden, false);
            _chaseHiddenPartner = null;
        }
    }

    /// <summary>
    /// Sets up the lava that was found in this room, once its FSM has been set up by the game. What its actions ask
    /// about the player is pointed at an object of ours, which then stands where the lower of the two players is.
    /// </summary>
    private void TrySetUpLavaChase() {
        if (_pendingLavaFsm is not { } fsm) {
            return;
        }

        if (fsm == null) {
            _pendingLavaFsm = null;
            return;
        }

        if (fsm.Fsm is not { Initialized: true }) {
            return;
        }

        _pendingLavaFsm = null;

        var body = fsm.GetComponent<Rigidbody2D>();
        if (body == null) {
            Logger.Warn($"The rising lava '{fsm.gameObject.name}' has no body to move by, so each game keeps its own");
            return;
        }

        // In the room, so that it goes when the room does
        var target = new GameObject("SSMP Lava Chase Target");
        SceneManager.MoveGameObjectToScene(target, fsm.gameObject.scene);
        var targetBody = target.AddComponent<Rigidbody2D>();
        targetBody.bodyType = RigidbodyType2D.Kinematic;
        targetBody.gravityScale = 0f;

        var chase = new LavaChase(fsm, body, target, targetBody, SceneUtil.GetCurrentSceneName()) {
            Band = FindLavaBand(fsm)
        };
        foreach (var register in fsm.GetComponents<EventRegister>()) {
            if (register != null && Array.IndexOf(LavaStopEvents, register.SubscribedEvent) >= 0) {
                chase.Stops.Add(register);
            }
        }

        target.AddComponent<LavaChaseTarget>().Follow = frame => MoveChaseTarget(chase, frame);
        fsm.gameObject.AddComponent<LavaChaseFollower>().Correct = () => FollowTheRoomsLava(chase);
        var pointed = PointLavaAtChaseTarget(fsm, target);
        _lavaChase = chase;

        Logger.Info(
            $"The rising lava '{fsm.gameObject.name}' now chases whichever player is lower: {pointed} of its actions " +
            $"ask about them, {chase.Stops.Count} listener(s) stop it for a burn"
        );
    }

    /// <summary>
    /// The part of a lava that burns: the collider of its lava hazard.
    /// </summary>
    private static Collider2D? FindLavaBand(PlayMakerFSM fsm) {
        foreach (var damage in fsm.GetComponentsInChildren<DamageHero>(true)) {
            if (damage.hazardType == GlobalEnums.HazardType.LAVA && damage.TryGetComponent<Collider2D>(out var band)) {
                return band;
            }
        }

        return null;
    }

    /// <summary>
    /// Points every action of the lava's FSM that asks about the game's own player at the given object instead.
    /// </summary>
    /// <returns>How many were pointed.</returns>
    private static int PointLavaAtChaseTarget(PlayMakerFSM fsm, GameObject target) {
        var pointed = 0;
        foreach (var state in fsm.FsmStates) {
            foreach (var action in state.Actions) {
                if (action == null) {
                    continue;
                }

                var fields = action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                        BindingFlags.NonPublic);
                foreach (var field in fields) {
                    if (field.FieldType == typeof(FsmGameObject) &&
                        field.GetValue(action) is FsmGameObject { Name: ChasedVariableName }) {
                        field.SetValue(action, new FsmGameObject { Value = target });
                        pointed++;
                    } else if (field.FieldType == typeof(FsmOwnerDefault) &&
                               field.GetValue(action) is FsmOwnerDefault {
                                   OwnerOption: OwnerDefaultOption.SpecifyGameObject,
                                   GameObject.Name: ChasedVariableName
                               } owner) {
                        owner.GameObject = new FsmGameObject { Value = target };
                        pointed++;
                    }
                }
            }
        }

        return pointed;
    }

    /// <summary>
    /// Moves the object the lava chases to the lower of the two players who are on their feet: the local player, and
    /// the partner if they are in the room. A player lying down is not chased.
    /// </summary>
    /// <param name="chase">The lava.</param>
    /// <param name="frame">Whether this is the frame update, which is when the speed of the partner is worked out.
    /// </param>
    private void MoveChaseTarget(LavaChase chase, bool frame) {
        try {
            var hero = HeroController.instance;
            Vector2? at = null;
            var velocity = Vector2.zero;
            if (hero != null && !hero.cState.dead) {
                at = hero.transform.position;
                velocity = hero.rb2d.linearVelocity;
            }

            if (GetChasablePartnerBody() is { } partnerBody) {
                var position = (Vector2) partnerBody.transform.position;
                if (frame) {
                    TrackPartnerBody(chase, position);
                }

                if (at is not { } heroAt || position.y < heroAt.y) {
                    at = position;
                    velocity = chase.AvatarVelocity;
                }
            } else {
                chase.AvatarTime = float.NegativeInfinity;
            }

            // Nobody on their feet: the lava keeps its eye on the local player's body, which is what it does alone
            if (at == null && hero != null) {
                at = hero.transform.position;
            }

            if (at is not { } where) {
                return;
            }

            var transform = chase.Target.transform;
            transform.position = new Vector3(where.x, where.y, transform.position.z);
            chase.TargetBody.position = where;
            chase.TargetBody.linearVelocity = velocity;
        } catch (Exception e) {
            LogLavaChaseError(e);
        }
    }

    /// <summary>
    /// The body of the partner if the lava can chase them: they are in the room and not lying down. A partner who died
    /// in the chase counts as lying down until their game says they are up, since their body stays where they fell
    /// until then.
    /// </summary>
    private GameObject? GetChasablePartnerBody() {
        if (GetCheckedPartner() is not { IsInLocalScene: true, PlayerObject: { } body } partner ||
            _partnerWaitingRescue == partner.Id || _chaseHiddenPartner == partner.Id || !body.activeInHierarchy) {
            return null;
        }

        return body;
    }

    /// <summary>
    /// Works out how fast the body of the partner moves. It carries no speed of its own, since it is drawn where their
    /// game says they are rather than moved, and one part of the chase waits for the player to rise fast enough.
    /// </summary>
    private static void TrackPartnerBody(LavaChase chase, Vector2 position) {
        var now = Time.time;
        var elapsed = now - chase.AvatarTime;
        if (elapsed >= 1f) {
            chase.AvatarVelocity = Vector2.zero;
        } else if (elapsed > 0f) {
            var raw = (position - chase.AvatarPosition) / elapsed;
            var weight = elapsed / (elapsed + AvatarVelocitySmoothing);
            chase.AvatarVelocity = Vector2.Lerp(chase.AvatarVelocity, raw, weight);
        }

        chase.AvatarPosition = position;
        chase.AvatarTime = now;
    }

    /// <summary>
    /// Puts the lava of this game where the game that runs the room has it, or holds it where a burn in the other
    /// game set it down. Left alone while this game sets its own lava down for a burn of its own, which it then tells
    /// the other game about.
    /// </summary>
    private void FollowTheRoomsLava(LavaChase chase) {
        try {
            if (chase.Body == null) {
                return;
            }

            var now = Time.time;
            if (now < chase.HoldUntil) {
                SetLavaY(chase, chase.HoldY, 0f);
                return;
            }

            if (IsSceneClient?.Invoke() != true || !chase.SampleChasing ||
                now - chase.SampleTime > LavaSampleLifetime || !IsLavaRising(chase) ||
                GetCheckedPartner() is not { IsInLocalScene: true }) {
                return;
            }

            // Where it has got to since, going by how fast it was rising and how long the news took to arrive
            var age = now - chase.SampleTime + (float) _netClient.UpdateManager.AverageRtt / 2000f;
            SetLavaY(chase, chase.SampleY + chase.SampleVelocity * age, chase.SampleVelocity);
        } catch (Exception e) {
            LogLavaChaseError(e);
        }
    }

    /// <summary>
    /// Puts the lava at a height and has it rise at a speed. Where it is drawn is set as well as its body: the actions
    /// of its FSM read and set the one, and move it by the other.
    /// </summary>
    private static void SetLavaY(LavaChase chase, float y, float velocity) {
        var body = chase.Body;
        var transform = body.transform;
        transform.position = new Vector3(transform.position.x, y, transform.position.z);
        body.position = new Vector2(body.position.x, y);
        body.linearVelocity = new Vector2(body.linearVelocity.x, velocity);
    }

    /// <summary>
    /// Whether the lava is after anyone at all.
    /// </summary>
    private static bool IsLavaChasing(LavaChase chase) {
        var fsm = chase.Fsm;
        if (fsm == null || !fsm.enabled || !fsm.gameObject.activeInHierarchy) {
            return false;
        }

        var state = fsm.ActiveStateName;
        return !string.IsNullOrEmpty(state) && !LavaRestingStates.Contains(state);
    }

    /// <summary>
    /// Whether the lava is after someone and rising, rather than set down for a burn.
    /// </summary>
    private static bool IsLavaRising(LavaChase chase) {
        return IsLavaChasing(chase) && !LavaResetStates.Contains(chase.Fsm.ActiveStateName);
    }

    /// <summary>
    /// Whether a lava is after the players in the room the local player is in, with the partner in it too.
    /// </summary>
    private bool IsChaseWithPartner(ClientPlayerData partner) {
        return _lavaChase is { } chase && IsLavaChasing(chase) && partner.IsInLocalScene &&
               partner.PlayerObject != null;
    }

    /// <summary>
    /// How high the lava is. Read from where it is drawn rather than from its body: the actions of its FSM that put it
    /// somewhere set that, and the body only catches up at the next step of the physics.
    /// </summary>
    private static float GetLavaY(LavaChase chase) => chase.Body.transform.position.y;

    /// <summary>
    /// How high the top of the lava is, or null before the part that burns has been seen. Once the lava is gone with
    /// the room it was in, how high it was when last seen.
    /// </summary>
    private static float? GetLavaTop(LavaChase chase) {
        if (chase.BandTopOffset is not { } offset) {
            return null;
        }

        return chase.Body != null ? GetLavaY(chase) + offset : chase.LastTop;
    }

    /// <summary>
    /// Whether a place is clear of the lava.
    /// </summary>
    private static bool IsAboveLava(LavaChase chase, Vector2 position) {
        return GetLavaTop(chase) is not { } top || position.y > top + LavaClearance;
    }

    /// <summary>
    /// Keeps the lava of this room in step each frame: where the local player last stood safely, what the two games
    /// tell each other, and the listeners a burn switched off.
    /// </summary>
    /// <param name="hero">The hero controller.</param>
    /// <param name="partner">The partner whose save is checked with this one, or null.</param>
    private void UpdateLavaChase(HeroController hero, ClientPlayerData? partner) {
        try {
            TrySetUpLavaChase();
            if (_lavaChase is not { } chase) {
                return;
            }

            // Gone without the room going, which is let go of the same way
            if (chase.Fsm == null || chase.Body == null) {
                ForgetLavaChase();
                return;
            }

            if (chase.Band is { enabled: true } band && band.gameObject.activeInHierarchy) {
                chase.BandTopOffset = band.bounds.max.y - GetLavaY(chase);
            }

            chase.LastTop = GetLavaTop(chase);

            NoteSafeSpot(hero, chase);

            // The listeners go back on once the burn they were switched off for is over. The lava hears about a burn
            // while the player is still being put back, so this is soon enough for the next one.
            if (chase.MutedAt >= 0f && !hero.cState.hazardDeath && !hero.cState.hazardRespawning &&
                Time.time - chase.MutedAt > 1f) {
                UnmuteLavaStops(chase);
            }

            if (partner is not { IsInLocalScene: true } || !IsLavaChasing(chase)) {
                chase.SetDownReported = false;
                return;
            }

            SendLavaSample(partner, chase);
            ReportLavaSetDown(partner, chase);
        } catch (Exception e) {
            LogLavaChaseError(e);
        }
    }

    /// <summary>
    /// Notes where the local player stands if it is a safe place to put someone: on the ground, clear of the lava, and
    /// over nothing that hurts. The ground has to be right below the middle of the player, which is where the game
    /// looks for it when it puts a player back: standing on the very edge of a ledge already counts as being on the
    /// ground, and from there the game would find the ground at the bottom of the drop instead, under the lava.
    /// </summary>
    private static void NoteSafeSpot(HeroController hero, LavaChase chase) {
        if (hero.cState.dead || hero.cState.hazardDeath || hero.cState.hazardRespawning ||
            hero.cState.transitioning || !hero.cState.onGround) {
            return;
        }

        var position = (Vector2) hero.transform.position;
        if (!IsAboveLava(chase, position) || IsInOrOverTheRoomsHarm(position) || !HasGroundRightBelow(hero, position)) {
            return;
        }

        chase.SafeSpot = position;
        chase.SafeTime = Time.time;
    }

    /// <summary>
    /// Whether the ground the game puts a player back on is straight below the middle of the local player, no further
    /// down than their feet and a little more.
    /// </summary>
    private static bool HasGroundRightBelow(HeroController hero, Vector2 position) {
        var collider = hero.GetComponent<Collider2D>();
        if (collider == null) {
            return false;
        }

        var reach = position.y - collider.bounds.min.y + PutBackGroundSlack;
        return Helper.IsRayHittingNoTriggers(position, Vector2.down, reach, PutBackGroundLayerMask);
    }

    /// <summary>
    /// Tells the partner where the lava of this game is, and where the local player last stood safely.
    /// </summary>
    private void SendLavaSample(ClientPlayerData partner, LavaChase chase) {
        var now = Time.unscaledTime;
        if (now < _nextLavaSampleTime) {
            return;
        }

        _nextLavaSampleTime = now + LavaSampleInterval;
        var safeAge = Time.time - chase.SafeTime;
        var hasSafeSpot = safeAge <= LavaSafeSpotLifetime;
        Send(new CoopSaveUpdate {
            TargetId = partner.Id,
            Kind = CoopSaveUpdateKind.LavaChase,
            Scene = SceneUtil.GetCurrentSceneName(),
            Sequence = ++_lavaChaseSequence,
            Values = [
                GetLavaY(chase),
                chase.Body.linearVelocity.y,
                IsLavaChasing(chase) ? 1f : 0f,
                hasSafeSpot ? 1f : 0f,
                chase.SafeSpot.x,
                chase.SafeSpot.y,
                hasSafeSpot ? safeAge : 0f
            ]
        });
    }

    /// <summary>
    /// Tells the game that runs the room that the lava of this game was set down for a burn, so that it sets its own
    /// down the same way. Otherwise the lava of this game would be pulled straight back up to where the other one is,
    /// over the player it was just set down below.
    /// </summary>
    private void ReportLavaSetDown(ClientPlayerData partner, LavaChase chase) {
        if (IsSceneClient?.Invoke() != true) {
            return;
        }

        var state = chase.Fsm.ActiveStateName;
        if (!LavaResetStates.Contains(state)) {
            chase.SetDownReported = false;
            return;
        }

        if (chase.SetDownReported || state != LavaSetDownStateName) {
            return;
        }

        chase.SetDownReported = true;
        var pause = chase.Fsm.FsmVariables.FindFsmFloat(RestartPauseVariableName)?.Value ?? 0f;
        Send(new CoopSaveUpdate {
            TargetId = partner.Id,
            Kind = CoopSaveUpdateKind.LavaChaseSetDown,
            Scene = SceneUtil.GetCurrentSceneName(),
            Values = [GetLavaY(chase), pause + LavaSetDownHoldExtra]
        });
        Logger.Info(
            $"The lava was set down to {GetLavaY(chase)} for a burn of this player, telling the game that runs the room"
        );
    }

    /// <summary>
    /// What the game of the partner says about its lava and where the partner last stood safely.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update.</param>
    private void OnLavaChase(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCheckedPartner()?.Id != player.Id || _lavaChase is not { } chase || update.Values.Count < 7 ||
            update.Scene != SceneUtil.GetCurrentSceneName()) {
            return;
        }

        // One that was overtaken on the way is dropped, but only while newer ones keep arriving: a partner whose game
        // was started again counts from the beginning again
        if (update.Sequence <= chase.PartnerSequence && Time.time - chase.SampleTime <= LavaSampleLifetime) {
            return;
        }

        chase.PartnerSequence = update.Sequence;
        chase.SampleY = update.Values[0];
        chase.SampleVelocity = update.Values[1];
        chase.SampleChasing = update.Values[2] > 0f;
        chase.SampleTime = Time.time;

        if (update.Values[3] > 0f) {
            chase.PartnerSafeSpot = new Vector2(update.Values[4], update.Values[5]);
            // As old as it already was when it was sent
            chase.PartnerSafeTime = Time.time - update.Values[6];
        }
    }

    /// <summary>
    /// The game of the partner set its lava down for a burn of the partner, so the lava of this game, which runs the
    /// room, is set down and held there the same way.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update.</param>
    private void OnLavaChaseSetDown(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCheckedPartner()?.Id != player.Id || _lavaChase is not { } chase || update.Values.Count < 2 ||
            update.Scene != SceneUtil.GetCurrentSceneName() || IsSceneHost?.Invoke() != true) {
            return;
        }

        // Only a lava that is after the players, and only downwards: one at rest before the chase or at the top after
        // it stays where the game put it
        if (!IsLavaChasing(chase) || update.Values[0] >= GetLavaY(chase)) {
            return;
        }

        chase.HoldY = update.Values[0];
        chase.HoldUntil = Time.time + update.Values[1];
        Logger.Info(
            $"{player.Username} was put back below the lava, so it is set down to {chase.HoldY} for " +
            $"{update.Values[1]}s"
        );
    }

    /// <summary>
    /// Watches for the burns of the local player.
    /// </summary>
    private Hook? WatchForBurns() {
        try {
            var method = typeof(HeroController).GetMethod(
                "DieFromHazard",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );

            if (method == null) {
                Logger.Warn("Could not find the burn of the player, so a burn in the chase puts them back alone");

                return null;
            }

            return new Hook(
                method,
                (Func<Func<HeroController, GlobalEnums.HazardType, float, IEnumerator>, HeroController,
                    GlobalEnums.HazardType, float, IEnumerator>) OnBurn
            );
        } catch (Exception e) {
            Logger.Error($"Could not watch for the burns of the player:\n{e}");

            return null;
        }
    }

    /// <summary>
    /// A burn of the local player that they live through: in the chase, with the partner on their feet and somewhere
    /// safe, they are put back beside the partner instead of at the game's own mark, and the lava does not stop for
    /// it. The game's mark is below the lava by then, and a lava that stopped and came down for one player would come
    /// down for both.
    /// </summary>
    private IEnumerator OnBurn(
        Func<HeroController, GlobalEnums.HazardType, float, IEnumerator> orig,
        HeroController self,
        GlobalEnums.HazardType type,
        float angle
    ) {
        try {
            _burnRedirect = null;
            if (_lavaChase is { } chase && GetCheckedPartner() is { } partner && IsChaseWithPartner(partner)) {
                if (TryGetPartnerSafeSpot(chase, partner, out var spot)) {
                    _burnRedirect = spot;
                    _burnRedirectTime = Time.time;
                    MuteLavaStops(chase);
                    Logger.Info($"Burnt in the chase, so going back beside {partner.Username} at {spot}");
                } else {
                    Logger.Info(
                        $"Burnt in the chase with nowhere safe beside {partner.Username}, so going back the game's " +
                        "own way, with the lava set down for both"
                    );
                }
            }
        } catch (Exception e) {
            LogLavaChaseError(e);
        }

        return orig(self, type, angle);
    }

    /// <summary>
    /// Where the partner last stood safely, if they are on their feet in the room and that place is still clear of
    /// the lava. It is a place on the ground, which is what the game needs to put a player back on.
    /// </summary>
    private bool TryGetPartnerSafeSpot(LavaChase chase, ClientPlayerData partner, out Vector2 spot) {
        spot = chase.PartnerSafeSpot;
        return partner.IsInLocalScene && partner.PlayerObject != null && _partnerWaitingRescue != partner.Id &&
               Time.time - chase.PartnerSafeTime <= LavaSafeSpotLifetime && IsAboveLava(chase, spot);
    }

    /// <summary>
    /// Takes where the game is about to put the local player back after a burn, if that burn is one that puts them
    /// beside the partner instead. The partner has usually moved on since the burn, and the lava, which did not stop
    /// for it, has gone on rising: where they stand now is used if they stand somewhere safe, the place taken at the
    /// burn if the lava has not reached it yet, and otherwise the burn goes the game's own way after all.
    /// </summary>
    private void TakeBurnRedirect(PlayerData? playerData) {
        if (_burnRedirect is not { } spot) {
            return;
        }

        _burnRedirect = null;
        if (playerData == null || Time.time - _burnRedirectTime > 10f || _lavaChase is not { } chase) {
            return;
        }

        if (GetCheckedPartner() is { } partner && TryGetPartnerSafeSpot(chase, partner, out var newer)) {
            spot = newer;
        } else if (!IsAboveLava(chase, spot)) {
            // The game's own mark, with the lava stopped and set down below it, which is what the burn would have
            // told the lava had it not been kept from stopping
            UnmuteLavaStops(chase);
            chase.Fsm.SendEvent(LavaStopEvents[0]);
            Logger.Info("Nowhere beside the partner is clear of the lava any more, so going back the game's own way");

            return;
        }

        playerData.hazardRespawnLocation = new Vector3(spot.x, spot.y, playerData.hazardRespawnLocation.z);
    }

    /// <summary>
    /// The game has put the local player back after a burn and is about to tell the room so. That is when the lava
    /// takes where the player it chases is, to set itself down below them, and the object it asks about was last
    /// moved before the player was, so it is moved to where they are now first.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The hero controller.</param>
    private void OnRespawnReset(Action<HeroController> orig, HeroController self) {
        if (_lavaChase is { } chase) {
            MoveChaseTarget(chase, false);
        }

        orig(self);
    }

    /// <summary>
    /// Switches off the listeners through which a burn stops the lava.
    /// </summary>
    private static void MuteLavaStops(LavaChase chase) {
        if (chase.MutedAt < 0f) {
            foreach (var register in chase.Stops) {
                if (register != null && !register.SubscribedEvent.StartsWith(MutedLavaStopPrefix)) {
                    register.SwitchEvent(MutedLavaStopPrefix + register.SubscribedEvent);
                }
            }
        }

        chase.MutedAt = Time.time;
    }

    /// <summary>
    /// Switches those listeners back on.
    /// </summary>
    private static void UnmuteLavaStops(LavaChase chase) {
        if (chase.MutedAt < 0f) {
            return;
        }

        foreach (var register in chase.Stops) {
            if (register != null && register.SubscribedEvent.StartsWith(MutedLavaStopPrefix)) {
                register.SwitchEvent(register.SubscribedEvent.Substring(MutedLavaStopPrefix.Length));
            }
        }

        chase.MutedAt = -1f;
    }

    /// <summary>
    /// Stands up a partner who died in the chase, beside the local player, once they have lain long enough and the
    /// local player is on their feet somewhere clear of the lava. It is this game that says when, and only while the
    /// local player is standing: a player who goes down first stops anyone being stood up by them, which makes it two
    /// players down, and that is decided the same way in both games.
    /// </summary>
    /// <param name="hero">The hero controller.</param>
    /// <param name="partner">The partner whose save is checked with this one, or null.</param>
    private void UpdateChaseStandUp(HeroController hero, ClientPlayerData? partner) {
        try {
            if (_chaseStandUp is not { } standUp || Time.unscaledTime < standUp.At) {
                return;
            }

            // No longer this game's to decide: the partner left, stood up or went to their bench, or this player went
            // down after them and told them so
            if (partner == null || partner.Id != standUp.PlayerId || _partnerWaitingRescue != standUp.PlayerId) {
                _chaseStandUp = null;
                return;
            }

            Vector2? spot = null;
            if (_lavaChase is { } chase && chase.Scene == standUp.Scene) {
                // Standing them up while this player is in mid-air, being put back after a burn, or somewhere the lava
                // is about to reach would drop them straight into it, so it waits for a place that is clear
                if (hero.cState.dead || hero.cState.hazardDeath || hero.cState.hazardRespawning ||
                    !TryGetOwnSafeSpot(chase, out var beside)) {
                    return;
                }

                spot = beside;
            }

            // Otherwise this player is somewhere without the lava of that room, and they stand up where they fell
            _chaseStandUp = null;
            StandUpChasePartner(partner, standUp, spot);
        } catch (Exception e) {
            LogLavaChaseError(e);
        }
    }

    /// <summary>
    /// Where the local player last stood safely, if that was a moment ago and the lava has not reached it since.
    /// </summary>
    private static bool TryGetOwnSafeSpot(LavaChase chase, out Vector2 spot) {
        spot = chase.SafeSpot;
        return Time.time - chase.SafeTime <= LavaSafeSpotLifetime && IsAboveLava(chase, spot);
    }

    /// <summary>
    /// Stands up a partner who is still lying down in the chase of the room the local player is leaving, at once: with
    /// the local player gone there is nobody left in that room to stand beside, and waiting for them would leave the
    /// partner lying there until their wait ran out.
    /// </summary>
    private void StandUpOnLeaving(LavaChase chase) {
        if (_chaseStandUp is not { } standUp || standUp.Scene != chase.Scene) {
            return;
        }

        _chaseStandUp = null;
        if (GetCheckedPartner() is not { } partner || partner.Id != standUp.PlayerId ||
            _partnerWaitingRescue != partner.Id) {
            return;
        }

        // The last place the local player stood safely in that room, however long ago, as long as the lava has not
        // reached it: it is no longer about standing beside them, only about the way out
        var hasSpot = chase.SafeTime > float.NegativeInfinity && IsAboveLava(chase, chase.SafeSpot);
        StandUpChasePartner(partner, standUp, hasSpot ? chase.SafeSpot : null);
    }

    /// <summary>
    /// Stands up a partner who died in the chase: at the given place, or without one where they fell, the way a
    /// partner pulled out of a cocoon stands up.
    /// </summary>
    private void StandUpChasePartner(ClientPlayerData partner, ChaseStandUp standUp, Vector2? spot) {
        // Counted as standing from here on, so that a death of this player now is one of one player, which waits to be
        // stood up beside them - the same answer their game gives once this arrives
        _partnerWaitingRescue = null;

        var update = new CoopSaveUpdate {
            TargetId = partner.Id,
            Kind = CoopSaveUpdateKind.RescueHit,
            Part = RescueHits,
            PartCount = RescueHits,
            Key = standUp.Key
        };
        if (spot is { } at) {
            update.Values = [at.x, at.y];
        }

        Send(update);
        Logger.Info(
            spot is { } place
                ? $"Standing {partner.Username} up at {place}"
                : $"Standing {partner.Username} up where they fell"
        );
    }

    /// <summary>
    /// The partner died in the chase: nothing is shown where they fell, and they stand up beside the local player a
    /// few seconds from now.
    /// </summary>
    private void OnChaseDeath(ClientPlayerData player, CoopSaveUpdate update) {
        // Both down: this player is dying as well, on their way to their bench, so there is nobody to stand them up
        // beside. A player lying down waiting was answered before this, and one being stood back up is not dying.
        if (HeroController.instance is { cState.dead: true } && _rescue == null) {
            TellPartnerNobodyIsComing();
            return;
        }

        _chaseStandUp = new ChaseStandUp(player.Id, update.Key, update.Scene, Time.unscaledTime + ChaseStandUpDelay);

        SetPartnerBodyHidden(player.Id, true);
        _chaseHiddenPartner = player.Id;

        Chat(Lang.Pick(
            $"{player.Username} went down. They will stand up beside you in a moment.",
            $"{player.Username} 倒下了，马上会在你身边站起来。"
        ));
    }

    /// <summary>
    /// Forgets a partner who was lying down in the chase, and puts their body back on the screen.
    /// </summary>
    private void ForgetChaseDeath(ushort playerId) {
        if (_chaseStandUp is { } standUp && standUp.PlayerId == playerId) {
            _chaseStandUp = null;
        }

        if (_chaseHiddenPartner == playerId) {
            SetPartnerBodyHidden(playerId, false);
            _chaseHiddenPartner = null;
        }
    }

    /// <summary>
    /// Logs a failure of the chase once, so that a room does not fill the log with the same line.
    /// </summary>
    private void LogLavaChaseError(Exception e) {
        if (_lavaChaseFailed) {
            return;
        }

        _lavaChaseFailed = true;
        Logger.Error($"Could not keep the rising lava in step between the two games:\n{e}");
    }
}
