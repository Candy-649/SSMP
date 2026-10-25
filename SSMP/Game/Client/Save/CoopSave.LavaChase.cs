using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
/// The lava that rises behind the players and has to be outrun, played the way a game made for more than one plays a
/// chase: one lava for all of them, which keeps coming whatever happens to any of them. The game's own lava only knows
/// its own player, so each game had one of its own that chased only its own player, and the players never raced the
/// same thing. Now it chases whichever of them is lowest, the game that runs the room says where it is, and the other
/// games follow. A burn that a player lives through puts them back beside another player, and a death has them stand
/// up beside a player on their feet a few seconds later, without a cocoon. Where the lava is nearer to that place than
/// the game sets it below a player it puts back, it goes down that far below the place, for everyone, and either way
/// it stands still, neither rising nor going back, until a second after they can move (USER 10-23: "所以正确逻辑是
/// 复活在我身边并且把岩浆降下去，而不是非要我比岩浆高6格"). Only when no other player can take them - down
/// themselves, or not on any ground since the lava started - does a burn put them back the game's own way.
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
    /// How often each game tells the others where its lava is and where its player last stood safely, in seconds.
    /// </summary>
    private const float LavaSampleInterval = 0.2f;

    /// <summary>
    /// How long what the game that runs the room said about its lava is followed, in seconds. Past that the lava of
    /// this game goes its own way again, which is what it does when the other players have left.
    /// </summary>
    private const float LavaSampleLifetime = 1f;

    /// <summary>
    /// How long a place a player last stood safely on counts as where they are now, in seconds. Such a place comes
    /// first for putting someone beside them; an older one still does when there is none newer.
    /// </summary>
    private const float LavaSafeSpotLifetime = 3f;

    /// <summary>
    /// The state of the lava's FSM in which it is set down below the player the game put back after a burn, and the
    /// variable it works that height out in: the height of the player, plus an amount below zero, kept no lower than
    /// where the lava starts.
    /// </summary>
    private const string SetDownStateName = "Set Restart Time", SetDownVariableName = "Self Y";

    /// <summary>
    /// How far below the middle of a player the game sets the lava down when it puts them back after a burn, where
    /// the lava's FSM says nothing of it: what it says in the race of the arena, which puts the top of the lava about 6
    /// below the feet. In the other race it is 4, about 4 below the feet.
    /// </summary>
    private const float DefaultSetDownBelow = 6f;

    /// <summary>
    /// How long a member who died in the chase waits past their time to stand up for the local player to stand on
    /// something, in seconds, before they stand up where the local player last stood, however long ago that was: a
    /// player who climbs walls, or by platforms that crumble, can be off firm ground for a while, and the last place
    /// they stood on falls behind them.
    /// </summary>
    private const float StandUpFreshWait = 2f;

    /// <summary>
    /// How long the lava stands still once a player who was put back beside another, or stood up beside one, can move
    /// again, in seconds: the pause the game gives a single player after a burn (its Restart Pause). From the moment
    /// they are put there until then it neither rises nor goes back (USER 10-21 chose this over putting them back only
    /// far above it). In the race it follows the lowest player at 4 a second within 6 of them, and a player put back
    /// cannot move for about 1.3 s, so it came over their feet before they could (USER 10-21: 岩浆离我朋友的安全点
    /// 非常近，她根本没有时间跳起来).
    /// </summary>
    private const float PutBackHoldAfterMoving = 1f;

    /// <summary>
    /// How far above where it is held the lava is shown the player it chases at most, while it is held. Its FSM pulls
    /// it up at once to a set distance below a player further above it than that (Catchup, 14.5 or 16.5 below), which
    /// it would do at every step, to be put back at every step; this is no further than it goes slowly from.
    /// </summary>
    private const float HeldChaseReach = 6f;

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
    /// What the look for the ground below the local player hit, kept from one frame to the next.
    /// </summary>
    private static readonly List<RaycastHit2D> PutBackGroundHits = [];

    /// <summary>
    /// How long a player who died in the chase lies down before they stand up beside a player on their feet, in
    /// seconds.
    /// </summary>
    private const float ChaseStandUpDelay = 3f;

    /// <summary>
    /// Roughly how long a burn takes to put its player back on their feet after the lava was set down for it, in
    /// seconds, which the game that runs the room holds its lava for on top of the pause the lava makes itself.
    /// </summary>
    private const float LavaSetDownHoldExtra = 1f;

    /// <summary>
    /// How quickly the speed of another player, which is worked out from where their body is drawn, follows it.
    /// </summary>
    private const float AvatarVelocitySmoothing = 0.1f;

    /// <summary>
    /// What a game that runs the room puts in <see cref="CoopSaveUpdate.Part"/> of what it says about its lava, which
    /// is the lava the other games follow.
    /// </summary>
    private const ushort LavaFromSceneHost = 1;

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
    /// Counts what this game said about its lava, so that the other games can tell the newest from one that arrived
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
    /// The members who died in the chase and are to stand up beside a member on their feet, by member.
    /// </summary>
    private readonly Dictionary<ushort, ChaseStandUp> _chaseStandUps = new();

    /// <summary>
    /// The members whose body was taken off the screen for lying down in the chase.
    /// </summary>
    private readonly HashSet<ushort> _chaseHiddenMembers = [];

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
        /// The parts of it that burn, switched on or not: a place to put a player is looked at through them, as the
        /// lava is set down below the place if it has risen over it.
        /// </summary>
        public List<Collider2D> Bands { get; } = [];

        /// <summary>
        /// How far below the middle of a player the game sets it down when it puts them back after a burn, as its FSM
        /// says.
        /// </summary>
        public float SetDownBelow { get; set; } = DefaultSetDownBelow;

        /// <summary>
        /// How low the game sets it down at most, which is where it starts, as its FSM says.
        /// </summary>
        public float SetDownFloor { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// When it was last seen after nobody, in seconds of game time: places stood on before then, as in a fight
        /// before the lava starts, are not taken for putting anyone.
        /// </summary>
        public float RestedAt { get; set; } = float.NegativeInfinity;

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
        /// How high the game that runs the room had its lava, how fast it was rising, whether it was after anyone, and
        /// when that arrived, in seconds of game time.
        /// </summary>
        public float SampleY { get; set; }

        public float SampleVelocity { get; set; }

        public bool SampleChasing { get; set; }

        public float SampleTime { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// When the lava of the game that runs the room last arrived, in seconds of game time.
        /// </summary>
        public float HostSampleTime { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// What is known of each other member, by member.
        /// </summary>
        public Dictionary<ushort, ChaseMember> Members { get; } = new();

        /// <summary>
        /// Where the local player last stood safely, and when.
        /// </summary>
        public Vector2 SafeSpot { get; set; }

        public float SafeTime { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// Where the lava is held, and until when: set down there for a burn in another game, or standing still for a
        /// player put back beside another (see <see cref="HoldLava"/>).
        /// </summary>
        public float HoldY { get; set; }

        public float HoldUntil { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// Whether the lava stands still for the local player, who was put back beside a member after a burn or stood
        /// up beside one after a death, and until when it does so: a second after they can move again, moved on while
        /// they cannot (see <see cref="UpdateOwnPutBackHold"/>).
        /// </summary>
        public bool OwnHolding { get; set; }

        public float OwnHoldUntil { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// Whether the setting down of the lava for the current burn has been told to the game that runs the room.
        /// </summary>
        public bool SetDownReported { get; set; }

        /// <summary>
        /// What is known of a member, made the first time it is asked for.
        /// </summary>
        public ChaseMember GetMember(ushort playerId) {
            if (!Members.TryGetValue(playerId, out var member)) {
                member = new ChaseMember();
                Members[playerId] = member;
            }

            return member;
        }
    }

    /// <summary>
    /// What a lava that chases knows of one other member.
    /// </summary>
    private sealed class ChaseMember {
        /// <summary>
        /// The newest of what their game said that was used, and when that arrived, in seconds of game time.
        /// </summary>
        public ulong Sequence { get; set; }

        public float SampleTime { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// Where they last stood safely, and when that arrived.
        /// </summary>
        public Vector2 SafeSpot { get; set; }

        public float SafeTime { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// Where their body was drawn last frame, and how fast it moved.
        /// </summary>
        public Vector2 AvatarPosition { get; set; }

        public float AvatarTime { get; set; } = float.NegativeInfinity;

        public Vector2 AvatarVelocity { get; set; }
    }

    /// <summary>
    /// A member who died in the chase, to be stood up beside a member on their feet.
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

        foreach (var hidden in _chaseHiddenMembers) {
            SetPartnerBodyHidden(hidden, false);
        }

        _chaseHiddenMembers.Clear();
    }

    /// <summary>
    /// Sets up the lava that was found in this room, once its FSM has been set up by the game. What its actions ask
    /// about the player is pointed at an object of ours, which then stands where the lowest of the players is.
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

        var chase = new LavaChase(fsm, body, target, targetBody, SceneUtil.GetCurrentSceneName());
        chase.Bands.AddRange(FindLavaBands(fsm));
        ReadSetDown(chase);
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
            $"The rising lava '{fsm.gameObject.name}' now chases whichever player is lowest: {pointed} of its actions " +
            $"ask about them, {chase.Stops.Count} listener(s) stop it for a burn, and it is set down " +
            $"{chase.SetDownBelow} below a player put back, no lower than {chase.SetDownFloor}"
        );
    }

    /// <summary>
    /// Reads from the lava's FSM how far below a player it sets the lava down when it puts them back after a burn, and
    /// how low at most, which is how far it is set down below a player put back beside another or stood up beside one
    /// (see <see cref="SetLavaDownBelow"/>).
    /// </summary>
    private static void ReadSetDown(LavaChase chase) {
        foreach (var state in chase.Fsm.FsmStates) {
            if (state.Name != SetDownStateName) {
                continue;
            }

            foreach (var action in state.Actions) {
                switch (action) {
                    case HutongGames.PlayMaker.Actions.FloatAdd {
                        floatVariable.Name: SetDownVariableName, add: { } amount
                    }:
                        chase.SetDownBelow = -amount.Value;
                        break;
                    case HutongGames.PlayMaker.Actions.FloatClamp {
                        floatVariable.Name: SetDownVariableName, minValue: { } lowest
                    }:
                        chase.SetDownFloor = lowest.Value;
                        break;
                }
            }
        }
    }

    /// <summary>
    /// The parts of a lava that burn: the colliders of its lava hazards, switched on or not, since which of them burns
    /// is only known by which is on. The lava of the race of the arena carries a spare that stays switched off ahead
    /// of the one that burns.
    /// </summary>
    private static IEnumerable<Collider2D> FindLavaBands(PlayMakerFSM fsm) {
        foreach (var damage in fsm.GetComponentsInChildren<DamageHero>(true)) {
            if (damage.hazardType == GlobalEnums.HazardType.LAVA && damage.TryGetComponent<Collider2D>(out var band)) {
                yield return band;
            }
        }
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
    /// Moves the object the lava chases to the lowest of the players who are on their feet: the local player, and the
    /// members in the room. A player lying down is not chased.
    /// </summary>
    /// <param name="chase">The lava.</param>
    /// <param name="frame">Whether this is the frame update, which is when the speed of the members is worked out.
    /// </param>
    private void MoveChaseTarget(LavaChase chase, bool frame) {
        try {
            var hero = HeroController.instance;
            Vector2? at = null;
            var velocity = Vector2.zero;
            if (hero != null && !hero.cState.dead && !PlayerTargetRegistry.IsPlayerDown(hero.gameObject)) {
                at = hero.transform.position;
                velocity = hero.rb2d.linearVelocity;
            }

            foreach (var id in _checkedMembers) {
                if (!_playerData.TryGetValue(id, out var member)) {
                    continue;
                }

                var known = chase.GetMember(id);
                if (GetChasableBody(member) is not { } body) {
                    known.AvatarTime = float.NegativeInfinity;
                    continue;
                }

                var position = (Vector2) body.transform.position;
                if (frame) {
                    TrackMemberBody(known, position);
                }

                if (at is not { } lowest || position.y < lowest.y) {
                    at = position;
                    velocity = known.AvatarVelocity;
                }
            }

            // Nobody on their feet: the lava keeps its eye on the local player's body, which is what it does alone
            if (at == null && hero != null) {
                at = hero.transform.position;
            }

            if (at is not { } where) {
                return;
            }

            // While the lava is held it is shown nobody further above it than it goes slowly from, which its FSM would
            // otherwise pull it up to at once, and be put back at every step (HeldChaseReach)
            if (Time.time < chase.HoldUntil) {
                where.y = Mathf.Min(where.y, chase.HoldY + HeldChaseReach);
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
    /// The body of a member if the lava can chase them: they are in the room and not lying down. A member who died in
    /// the chase counts as lying down until their game says they are up, since their body stays where they fell until
    /// then.
    /// </summary>
    private GameObject? GetChasableBody(ClientPlayerData member) {
        if (member is not { IsInLocalScene: true, PlayerObject: { } body } || _waitingMembers.ContainsKey(member.Id) ||
            _chaseHiddenMembers.Contains(member.Id) || !body.activeInHierarchy) {
            return null;
        }

        return body;
    }

    /// <summary>
    /// Works out how fast the body of a member moves. It carries no speed of its own, since it is drawn where their
    /// game says they are rather than moved, and one part of the chase waits for the player to rise fast enough.
    /// </summary>
    private static void TrackMemberBody(ChaseMember member, Vector2 position) {
        var now = Time.time;
        var elapsed = now - member.AvatarTime;
        if (elapsed >= 1f) {
            member.AvatarVelocity = Vector2.zero;
        } else if (elapsed > 0f) {
            var raw = (position - member.AvatarPosition) / elapsed;
            var weight = elapsed / (elapsed + AvatarVelocitySmoothing);
            member.AvatarVelocity = Vector2.Lerp(member.AvatarVelocity, raw, weight);
        }

        member.AvatarPosition = position;
        member.AvatarTime = now;
    }

    /// <summary>
    /// Puts the lava of this game where the game that runs the room has it, or holds it where a burn in another game
    /// set it down. Left alone while this game sets its own lava down for a burn of its own, which it then tells the
    /// game that runs the room about.
    /// </summary>
    private void FollowTheRoomsLava(LavaChase chase) {
        try {
            if (chase.Body == null) {
                return;
            }

            var now = Time.time;
            if (now < chase.HoldUntil) {
                // A hold keeps the lava from rising, never from going down: set down by this game for a burn of its
                // own player, the game's own way, it stays down and is held there. Pulled back up to where it was
                // held, it came up over that player as they were put back
                chase.HoldY = Mathf.Min(chase.HoldY, GetLavaY(chase));
                SetLavaY(chase, chase.HoldY, 0f);
                return;
            }

            if (IsSceneClient?.Invoke() != true || !chase.SampleChasing ||
                now - chase.SampleTime > LavaSampleLifetime || !IsLavaRising(chase) || !IsMemberInRoom()) {
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
    /// Whether a lava is after the players in the room the local player is in, with a member on their feet in it too.
    /// </summary>
    private bool IsChaseWithMembers() {
        return _lavaChase is { } chase && IsLavaChasing(chase) &&
               GetRoomMemberBodies().Exists(member => !_waitingMembers.ContainsKey(member.Id));
    }

    /// <summary>
    /// The members the saves were checked with whose body is in the room of the local player.
    /// </summary>
    private List<ClientPlayerData> GetRoomMemberBodies() {
        return GetCheckedMembers().FindAll(member => member.IsInLocalScene && member.PlayerObject != null);
    }

    /// <summary>
    /// How high the lava is. Read from where it is drawn rather than from its body: the actions of its FSM that put it
    /// somewhere set that, and the body only catches up at the next step of the physics.
    /// </summary>
    private static float GetLavaY(LavaChase chase) => chase.Body.transform.position.y;

    /// <summary>
    /// Keeps the lava of this room in step each frame: where the local player last stood safely, what the games tell
    /// each other, and the listeners a burn switched off.
    /// </summary>
    /// <param name="hero">The hero controller.</param>
    private void UpdateLavaChase(HeroController hero) {
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

            if (!IsLavaChasing(chase)) {
                chase.RestedAt = Time.time;
            }

            ForgetSpotsOfMembersWhoLeft(chase);
            NoteSafeSpot(hero, chase);
            UpdateOwnPutBackHold(hero, chase);

            // The listeners go back on once the burn they were switched off for is over. The lava hears about a burn
            // while the player is still being put back, so this is soon enough for the next one.
            if (chase.MutedAt >= 0f && !hero.cState.hazardDeath && !hero.cState.hazardRespawning &&
                Time.time - chase.MutedAt > 1f) {
                UnmuteLavaStops(chase);
            }

            if (!IsMemberInRoom() || !IsLavaChasing(chase)) {
                chase.SetDownReported = false;
                return;
            }

            StandUpBeforeTheLava(chase);
            SendLavaSample(chase);
            ReportLavaSetDown(chase);
        } catch (Exception e) {
            LogLavaChaseError(e);
        }
    }

    /// <summary>
    /// Notes where the local player stands if it is a safe place to put someone: on the ground and over nothing that
    /// hurts. How near the lava is to it does not matter, as the lava is set down below a place a player is put on
    /// (see <see cref="SetLavaDownBelow"/>). The ground has to be right below the middle of the player, which is where
    /// the game looks for it when it puts a player back: standing on the very edge of a ledge already counts as being
    /// on the ground, and from there the game would find the ground at the bottom of the drop instead, under the lava.
    /// And it has to be ground that stays: a floor that crumbles under a player crumbles in each game on its own (see
    /// PersonalPlaces), so one that holds the local player here crumbles under a player put there, in their own game,
    /// before they can move.
    /// </summary>
    private static void NoteSafeSpot(HeroController hero, LavaChase chase) {
        if (hero.cState.dead || PlayerTargetRegistry.IsPlayerDown(hero.gameObject) || hero.cState.hazardDeath ||
            hero.cState.hazardRespawning || hero.cState.transitioning || !hero.cState.onGround) {
            return;
        }

        var position = (Vector2) hero.transform.position;
        if (IsInOrOverTheRoomsHarm(position) || GetGroundRightBelow(hero, position) is not { } ground ||
            PersonalPlaces.Contains(ground.gameObject)) {
            return;
        }

        chase.SafeSpot = position;
        chase.SafeTime = Time.time;
    }

    /// <summary>
    /// The ground the game puts a player back on, if it is straight below a place for the middle of a player, no
    /// further down than the feet of the local player are below their middle and a little more: the nearest solid
    /// thing there on the layers the game looks on.
    /// </summary>
    private static Collider2D? GetGroundRightBelow(HeroController hero, Vector2 position) {
        var collider = hero.GetComponent<Collider2D>();
        if (collider == null) {
            return null;
        }

        var reach = hero.transform.position.y - collider.bounds.min.y + PutBackGroundSlack;
        var filter = new ContactFilter2D { useTriggers = false };
        filter.SetLayerMask(PutBackGroundLayerMask);
        Physics2D.Raycast(position, Vector2.down, filter, PutBackGroundHits, reach);

        Collider2D? ground = null;
        var nearest = float.PositiveInfinity;
        foreach (var hit in PutBackGroundHits) {
            if (hit.collider != null && !hit.collider.isTrigger && hit.distance < nearest) {
                ground = hit.collider;
                nearest = hit.distance;
            }
        }

        PutBackGroundHits.Clear();
        return ground;
    }

    /// <summary>
    /// Tells the members in the room where the lava of this game is, whether this game runs the room, and where the
    /// local player last stood safely.
    /// </summary>
    private void SendLavaSample(LavaChase chase) {
        var now = Time.unscaledTime;
        if (now < _nextLavaSampleTime) {
            return;
        }

        _nextLavaSampleTime = now + LavaSampleInterval;
        var safeAge = Time.time - chase.SafeTime;
        var hasSafeSpot = safeAge <= LavaSafeSpotLifetime;
        SendToRoomMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.LavaChase,
            Scene = SceneUtil.GetCurrentSceneName(),
            Sequence = ++_lavaChaseSequence,
            Part = IsSceneHost?.Invoke() == true ? LavaFromSceneHost : (ushort) 0,
            Values = [
                GetLavaY(chase),
                chase.Body.linearVelocity.y,
                IsLavaChasing(chase) ? 1f : 0f,
                hasSafeSpot ? 1f : 0f,
                chase.SafeSpot.x,
                chase.SafeSpot.y,
                hasSafeSpot ? safeAge : 0f,
                chase.OwnHolding ? Mathf.Max(0f, chase.OwnHoldUntil - Time.time) : 0f
            ]
        });
    }

    /// <summary>
    /// Has the lava stand still for the local player, who is being put back at the given place beside a member after a
    /// burn or stood up there beside one after a death, from now until a second after they can move (see
    /// <see cref="UpdateOwnPutBackHold"/>), set down below the place first if it is nearer to it than the game sets it
    /// below a player it puts back (see <see cref="SetLavaDownBelow"/>). The other games hear of the hold with what
    /// this game next says of its lava, which goes at once, and hold theirs too.
    /// </summary>
    /// <param name="place">Where the local player is put.</param>
    private void HoldLavaForOwnPutBack(Vector2 place) {
        if (_lavaChase is not { } chase || chase.Body == null || !IsLavaChasing(chase)) {
            return;
        }

        chase.OwnHolding = true;
        chase.OwnHoldUntil = Time.time + PutBackHoldAfterMoving;
        HoldLava(chase, chase.OwnHoldUntil);
        SetLavaDownBelow(chase, place);
        _nextLavaSampleTime = 0f;
        Logger.Info(
            $"Put back beside a member in the chase, so the lava stands still at {chase.HoldY} until a second after " +
            "this player can move"
        );
    }

    /// <summary>
    /// Sets the lava, which is held, down below a place where the local player is put, if it is higher than the game
    /// sets it below a player it puts back after a burn, to that height, and tells the other games in the room, which
    /// set theirs down the same way. That is how the game gives one player another go, and how one of several players
    /// gets theirs beside the others: a player on their feet in the chase is seldom far above the lava, which rises the
    /// faster the further below them it is (USER 10-23: "问题是那就应该岩浆降下去啊，我怎么可能一直比岩浆高6格，岩浆
    /// 本来就是离得越远越快的").
    /// </summary>
    private void SetLavaDownBelow(LavaChase chase, Vector2 place) {
        var from = GetLavaY(chase);
        var to = Mathf.Max(place.y - chase.SetDownBelow, chase.SetDownFloor);
        if (from > to) {
            chase.HoldY = Mathf.Min(chase.HoldY, to);
            SetLavaY(chase, chase.HoldY, 0f);
        }

        // Told even when the lava of this game is that low already, as it can be below the one of the game that runs
        // the room, which it goes back to following once it is no longer held; a game whose lava is lower leaves it
        SendToRoomMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.LavaChaseSetDown,
            Scene = SceneUtil.GetCurrentSceneName(),
            Values = [to, Mathf.Max(0f, chase.HoldUntil - Time.time) + LavaSetDownHoldExtra]
        });
        Logger.Info(
            $"The lava goes no higher than {to}, {chase.SetDownBelow} below where this player is put at {place}, as " +
            $"the game does for one player's burn: from {from} here to {GetLavaY(chase)}, telling the other games"
        );
    }

    /// <summary>
    /// Keeps the lava standing still for the local player while they cannot move after being put back, and a second
    /// after. Not for a player who goes down, as the lava stops for nobody who is down: what is held for them already
    /// runs out within that second.
    /// </summary>
    private static void UpdateOwnPutBackHold(HeroController hero, LavaChase chase) {
        if (!chase.OwnHolding) {
            return;
        }

        if (hero.cState.dead || PlayerTargetRegistry.IsPlayerDown(hero.gameObject)) {
            chase.OwnHolding = false;
            return;
        }

        var now = Time.time;
        if (hero.cState.hazardDeath || hero.cState.hazardRespawning) {
            chase.OwnHoldUntil = now + PutBackHoldAfterMoving;
        } else if (now >= chase.OwnHoldUntil) {
            chase.OwnHolding = false;
            return;
        }

        HoldLava(chase, chase.OwnHoldUntil);
    }

    /// <summary>
    /// Holds the lava where it is until the given time, or for as long as it is held already if that is longer, from
    /// where it is if nothing holds it yet, so that it does not go back.
    /// </summary>
    /// <returns>Whether that began a hold.</returns>
    private static bool HoldLava(LavaChase chase, float until) {
        var began = Time.time >= chase.HoldUntil;
        if (began) {
            chase.HoldY = GetLavaY(chase);
        }

        chase.HoldUntil = Mathf.Max(chase.HoldUntil, until);
        return began;
    }

    /// <summary>
    /// Tells the game that runs the room that the lava of this game was set down for a burn, so that it sets its own
    /// down the same way. Otherwise the lava of this game would be pulled straight back up to where the other one is,
    /// over the player it was just set down below.
    /// </summary>
    private void ReportLavaSetDown(LavaChase chase) {
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
        // To each member in the room, of whom the one whose game runs it takes it
        SendToRoomMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.LavaChaseSetDown,
            Scene = SceneUtil.GetCurrentSceneName(),
            Values = [GetLavaY(chase), pause + LavaSetDownHoldExtra]
        });
        Logger.Info(
            $"The lava was set down to {GetLavaY(chase)} for a burn of this player, telling the game that runs the room"
        );
    }

    /// <summary>
    /// What the game of a member says about its lava and where the member last stood safely.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update.</param>
    private void OnLavaChase(ClientPlayerData player, CoopSaveUpdate update) {
        if (!_checkedMembers.Contains(player.Id) || _lavaChase is not { } chase || update.Values.Count < 7 ||
            update.Scene != SceneUtil.GetCurrentSceneName()) {
            return;
        }

        // One that was overtaken on the way is dropped, but only while newer ones keep arriving: a member whose game
        // was started again counts from the beginning again
        var member = chase.GetMember(player.Id);
        if (update.Sequence <= member.Sequence && Time.time - member.SampleTime <= LavaSampleLifetime) {
            return;
        }

        member.Sequence = update.Sequence;
        member.SampleTime = Time.time;

        // The lava of a member chases in this room, which stands up whoever lies in a cocoon here as this game's own
        // does as it starts (StandUpBeforeTheLava). A game whose own lava rests never starts it, as after coming in
        // by the top door, where the race is over, while the member's game counts the one lying as standing already.
        if (update.Values[2] > 0f) {
            StandUpCocoonsIn(update.Scene, $"The lava of {player.Username} in '{update.Scene}' chases", LavaRisingNote);
        }

        // The lava as the game that runs the room has it, which is what a game that doesn't run it follows, and only
        // that one: following a game that follows it as well only follows it later, and two such games would follow
        // each other. A member who is the only other one in the room runs it whenever this game doesn't. With no game
        // of a member running the room, as while its scene host is a player outside the save, the member in the room
        // with the largest key leads instead, the way lifts are decided.
        if (update.Part == LavaFromSceneHost) {
            chase.HostSampleTime = Time.time;
        }

        var roomMembers = GetCheckedMembers().FindAll(other => other.IsInLocalScene);
        if (update.Part == LavaFromSceneHost || roomMembers.Count <= 1 ||
            Time.time - chase.HostSampleTime > LavaSampleLifetime && GetLiftDecider(roomMembers) == player) {
            chase.SampleY = update.Values[0];
            chase.SampleVelocity = update.Values[1];
            chase.SampleChasing = update.Values[2] > 0f;
            chase.SampleTime = Time.time;
        }

        if (update.Values[3] > 0f) {
            member.SafeSpot = new Vector2(update.Values[4], update.Values[5]);
            // As old as it already was when it was sent
            member.SafeTime = Time.time - update.Values[6];
        }

        // The member was put back beside another, or stood up beside one, and cannot move yet or only just can: the
        // lava stands still in every game for as long as theirs says, the one that runs the room and the others that
        // follow it, which would otherwise go on rising, or pull theirs up to the member (HeldChaseReach)
        if (update.Values.Count >= 8 && update.Values[7] > 0f && chase.Body != null &&
            HoldLava(chase, Time.time + update.Values[7])) {
            Logger.Info($"{player.Username} was put back beside a member, so the lava stands still at {chase.HoldY}");
        }
    }

    /// <summary>
    /// The game of a member set its lava down, for a burn of that member the game's own way, or below where it puts
    /// its player beside another or stands them up beside one (see <see cref="SetLavaDownBelow"/>). The lava of this
    /// game is set down and held there the same way: the game that runs the room, which the others follow, and the
    /// others as well, which hold theirs meanwhile, and held it where it was.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update.</param>
    private void OnLavaChaseSetDown(ClientPlayerData player, CoopSaveUpdate update) {
        if (!_checkedMembers.Contains(player.Id) || _lavaChase is not { } chase || update.Values.Count < 2 ||
            update.Scene != SceneUtil.GetCurrentSceneName() || chase.Body == null) {
            return;
        }

        // Only a lava that is after the players, and only downwards: one at rest before the chase or at the top after
        // it stays where the game put it
        if (!IsLavaChasing(chase) || update.Values[0] >= GetLavaY(chase)) {
            return;
        }

        chase.HoldY = Time.time < chase.HoldUntil ? Mathf.Min(chase.HoldY, update.Values[0]) : update.Values[0];
        chase.HoldUntil = Mathf.Max(chase.HoldUntil, Time.time + update.Values[1]);
        Logger.Info(
            $"The game of {player.Username} set its lava down for a player it put back, so it is set down to " +
            $"{chase.HoldY} here too, for {update.Values[1]}s"
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
    /// A burn of the local player that they live through: in the chase, with a member on their feet who has stood
    /// somewhere since the lava started, they are put back beside that member instead of at the game's own mark, which
    /// is far below by then. The lava does not stop for it the game's own way, which would set it down below that mark:
    /// it is set down below the place beside the member instead, if it is nearer to it than the game sets it below a
    /// player it puts back (see <see cref="SetLavaDownBelow"/>).
    /// </summary>
    private IEnumerator OnBurn(
        Func<HeroController, GlobalEnums.HazardType, float, IEnumerator> orig,
        HeroController self,
        GlobalEnums.HazardType type,
        float angle
    ) {
        try {
            _burnRedirect = null;
            if (_lavaChase is { } chase && IsLavaChasing(chase) && GetRoomMemberBodies() is { Count: > 0 } members) {
                if (TryGetMemberSafeSpot(chase, out var spot, out var name, out var age)) {
                    _burnRedirect = spot;
                    _burnRedirectTime = Time.time;
                    MuteLavaStops(chase);
                    Logger.Info(
                        $"Burnt in the chase, so going back beside {name} at {spot}, where they stood {age:0.0} s ago"
                    );
                } else {
                    var names = JoinNames(members.Select(member => member.Username));
                    Logger.Info(
                        $"Burnt in the chase with nowhere beside {names} to stand on, so going back the game's own " +
                        "way, with the lava set down for everyone"
                    );
                }
            }
        } catch (Exception e) {
            LogLavaChaseError(e);
        }

        return orig(self, type, angle);
    }

    /// <summary>
    /// The safest place beside a member who is on their feet in the room: of the places such members last stood
    /// safely that can still be stood on, the highest of those stood on a moment ago, or else the newest. A
    /// player who climbs is in the air for longer than a moment as often as not, and a burn while the only member on
    /// their feet was went the game's own way, back down to a mark of the room, with the lava set down for everyone
    /// (USER 10-23: "岩浆赛跑开始的第一次掉进岩浆之后紧接着就是掉进岩浆第二次"). It is a place on the ground, which is
    /// what the game needs to put a player back on.
    /// </summary>
    private bool TryGetMemberSafeSpot(LavaChase chase, out Vector2 spot, out string name, out float age) {
        spot = default;
        name = "";
        age = 0f;
        var found = false;
        var foundFresh = false;
        var foundTime = float.NegativeInfinity;
        foreach (var member in GetRoomMemberBodies()) {
            if (_waitingMembers.ContainsKey(member.Id) || !chase.Members.TryGetValue(member.Id, out var known) ||
                !IsStillSafe(chase, known.SafeSpot, known.SafeTime)) {
                continue;
            }

            var fresh = Time.time - known.SafeTime <= LavaSafeSpotLifetime;
            var better = !found || fresh && !foundFresh ||
                         fresh == foundFresh && (fresh ? known.SafeSpot.y > spot.y : known.SafeTime > foundTime);
            if (!better) {
                continue;
            }

            spot = known.SafeSpot;
            name = member.Username;
            age = Time.time - known.SafeTime;
            found = true;
            foundFresh = fresh;
            foundTime = known.SafeTime;
        }

        return found;
    }

    /// <summary>
    /// Whether a place a player last stood on safely is still one to put the local player on, as far as this game can
    /// tell: stood on since the lava started after the players, over ground that stays and over nothing that hurts
    /// but the lava itself, which is set down below the place (see <see cref="SetLavaDownBelow"/>). Their game looked
    /// when they stood there, which can be a while ago now: a floor may have broken since, and the game puts a player
    /// back on the nearest ground below the place.
    /// </summary>
    /// <param name="chase">The lava.</param>
    /// <param name="spot">The place.</param>
    /// <param name="time">When it was stood on, in seconds of game time.</param>
    private static bool IsStillSafe(LavaChase chase, Vector2 spot, float time) {
        // The lava is set down below a place only while it chases, so a lava at rest is something that hurts like any
        var lava = chase.Body != null && IsLavaChasing(chase) ? chase.Bands : null;
        if (float.IsNegativeInfinity(time) || time < chase.RestedAt || IsInOrOverTheRoomsHarm(spot, lava)) {
            return false;
        }

        var hero = HeroController.instance;
        return hero == null ||
               GetGroundRightBelow(hero, spot) is { } ground && !PersonalPlaces.Contains(ground.gameObject);
    }

    /// <summary>
    /// Forgets where the members who are not in the room any more last stood safely in it: one who comes back in by
    /// another way is nowhere near there.
    /// </summary>
    private void ForgetSpotsOfMembersWhoLeft(LavaChase chase) {
        foreach (var pair in chase.Members) {
            if (!_playerData.TryGetValue(pair.Key, out var member) || !member.IsInLocalScene) {
                pair.Value.SafeTime = float.NegativeInfinity;
            }
        }
    }

    /// <summary>
    /// Takes where the game is about to put the local player back after a burn, if that burn is one that puts them
    /// beside a member instead. The members have usually moved on since the burn: where they last stood is used if
    /// it can still be stood on, or else the place taken at the burn if that still can, with the lava set down below
    /// it if it has come near, and otherwise the burn goes the game's own way after all.
    /// </summary>
    private void TakeBurnRedirect(PlayerData? playerData) {
        if (_burnRedirect is not { } spot) {
            return;
        }

        _burnRedirect = null;
        if (playerData == null || Time.time - _burnRedirectTime > 10f || _lavaChase is not { } chase) {
            return;
        }

        if (TryGetMemberSafeSpot(chase, out var newer, out _, out _)) {
            spot = newer;
        } else if (!IsStillSafe(chase, spot, _burnRedirectTime)) {
            // The game's own mark, with the lava stopped and set down below it, which is what the burn would have
            // told the lava had it not been kept from stopping
            UnmuteLavaStops(chase);
            chase.Fsm.SendEvent(LavaStopEvents[0]);
            Logger.Info("Nowhere beside the members can be stood on any more, so going back the game's own way");

            return;
        }

        playerData.hazardRespawnLocation = new Vector3(spot.x, spot.y, playerData.hazardRespawnLocation.z);
        HoldLavaForOwnPutBack(spot);
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

        // And the game that runs the room, if it is another's, brings back what the game brings back for the player
        // it put back (see CoopSave.PutBack), and the passages of dropping boxes near there turn to the player, in
        // every game of the room (see GamePatcher.BoxPassages)
        var position = self.transform.position;
        try {
            TellRoomOfPutBack(position, false);
        } catch (Exception e) {
            LogInteractionError(e);
        }

        try {
            GamePatcher.NoteBoxPutBack(SceneUtil.GetCurrentSceneName(), position.x, "the local player");
        } catch (Exception e) {
            LogInteractionError(e);
        }
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
    /// Stands up the members who died in the chase, beside the local player, once they have lain long enough and the
    /// local player is on their feet and has stood somewhere since the lava started, below which the game of the
    /// member sets the lava down if it is near (see <see cref="SetLavaDownBelow"/>). In the room of the chase it is a
    /// game whose player is standing that says when: a player who goes down there stops anyone being stood up beside
    /// them, and with everyone down that is decided the same way in every game. With several members on their feet in
    /// the room, whichever of their games says so first stands them up. From another room, with nobody on their feet
    /// left in the room of the chase, they stand up where they fell.
    /// </summary>
    /// <param name="hero">The hero controller.</param>
    private void UpdateChaseStandUp(HeroController hero) {
        // Without anyone to stand up, nothing to copy every frame
        if (_chaseStandUps.Count == 0) {
            return;
        }

        try {
            foreach (var standUp in _chaseStandUps.Values.ToList()) {
                if (Time.unscaledTime < standUp.At) {
                    continue;
                }

                // No longer this game's to decide: the member left, stood up or went to their bench, or everyone went
                // down and was told so
                if (!_waitingMembers.ContainsKey(standUp.PlayerId) || !_checkedMembers.Contains(standUp.PlayerId) ||
                    !_playerData.TryGetValue(standUp.PlayerId, out var member)) {
                    _chaseStandUps.Remove(standUp.PlayerId);
                    continue;
                }

                Vector2? spot = null;
                if (_lavaChase is { } chase && chase.Scene == standUp.Scene) {
                    // Not beside a player who is down or being put back after a burn, and only on a place the
                    // local player stood on since the lava started, which can still be stood on
                    if (hero.cState.dead || PlayerTargetRegistry.IsPlayerDown(hero.gameObject) ||
                        hero.cState.hazardDeath || hero.cState.hazardRespawning ||
                        !TryGetOwnSafeSpot(chase, Time.unscaledTime - standUp.At >= StandUpFreshWait, out var beside)) {
                        continue;
                    }

                    spot = beside;
                } else if (IsStandingMemberIn(standUp.Scene)) {
                    // A member on their feet is in that room, whose game stands them up beside them
                    continue;
                }

                // Otherwise this player is somewhere without the lava of that room, with nobody on their feet left in
                // it, and they stand up where they fell
                _chaseStandUps.Remove(standUp.PlayerId);
                StandUpChaseMember(member, standUp, spot);
            }
        } catch (Exception e) {
            LogLavaChaseError(e);
        }
    }

    /// <summary>
    /// Whether a member on their feet is in the given room, as far as this game has heard.
    /// </summary>
    private bool IsStandingMemberIn(string scene) {
        return GetStandingMembers().Exists(member => member.Room == scene);
    }

    /// <summary>
    /// Where the local player last stood safely since the lava started, if it can still be stood on (see
    /// <see cref="IsStillSafe"/>): a moment ago, or however long ago when that may do. How near the lava is to it
    /// does not matter, as the lava is set down below it (USER 10-23: "所以正确逻辑是复活在我身边并且把岩浆降下去，
    /// 而不是非要我比岩浆高6格"): waiting for a place well above the lava, and for one stood on a moment ago, kept a
    /// member who died lying there until the player on their feet went down as well (the friend, 10-23: "根本没刷起来").
    /// </summary>
    /// <param name="chase">The lava.</param>
    /// <param name="old">Whether a place stood on longer ago than a moment does.</param>
    /// <param name="spot">The place.</param>
    private static bool TryGetOwnSafeSpot(LavaChase chase, bool old, out Vector2 spot) {
        spot = chase.SafeSpot;
        return (old || Time.time - chase.SafeTime <= LavaSafeSpotLifetime) && IsStillSafe(chase, spot, chase.SafeTime);
    }

    /// <summary>
    /// Whether the lava of the local player's room chases the players now, so that one put somewhere has it set down
    /// below them if it is near (see <see cref="SetLavaDownBelow"/>).
    /// </summary>
    private bool IsLavaChasingHere() => _lavaChase is { } chase && chase.Body != null && IsLavaChasing(chase);

    /// <summary>
    /// Stands up the members who are still lying down in the chase of the room the local player is leaving, at once,
    /// unless a member on their feet stays in it: with nobody left in that room to stand beside, waiting for them would
    /// leave the members lying there until their wait ran out.
    /// </summary>
    private void StandUpOnLeaving(LavaChase chase) {
        foreach (var standUp in _chaseStandUps.Values.ToList()) {
            if (standUp.Scene != chase.Scene || IsStandingMemberIn(standUp.Scene)) {
                continue;
            }

            _chaseStandUps.Remove(standUp.PlayerId);
            if (!_checkedMembers.Contains(standUp.PlayerId) || !_playerData.TryGetValue(standUp.PlayerId, out var member) ||
                !_waitingMembers.ContainsKey(standUp.PlayerId)) {
                continue;
            }

            // The last place the local player stood safely in that room since the lava started, however long ago,
            // below which the game of the member sets the lava down if it is near: it is no longer about standing
            // beside them, only about the way out
            var hasSpot = !float.IsNegativeInfinity(chase.SafeTime) && chase.SafeTime >= chase.RestedAt;
            StandUpChaseMember(member, standUp, hasSpot ? chase.SafeSpot : null);
        }
    }

    /// <summary>
    /// Stands up a member who died in the chase: at the given place, or without one where they fell, the way a member
    /// pulled out of a cocoon stands up.
    /// </summary>
    private void StandUpChaseMember(ClientPlayerData member, ChaseStandUp standUp, Vector2? spot) {
        // Counted as standing from here on, so that a death of this player now waits to be stood up beside them - the
        // same answer their game gives once this arrives
        _waitingMembers.Remove(member.Id);

        var update = new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.RescueHit,
            Part = RescueHits,
            PartCount = RescueHits,
            Key = standUp.Key
        };
        if (spot is { } at) {
            update.Values = [at.x, at.y];
        }

        // To every member, so that the others count them as standing from here on as well
        SendToMembers(update);
        Logger.Info(
            spot is { } place
                ? $"Standing {member.Username} up at {place}"
                : $"Standing {member.Username} up where they fell"
        );
    }

    /// <summary>
    /// A member died in the chase: nothing is shown where they fell, and they stand up beside a member on their feet a
    /// few seconds from now.
    /// </summary>
    /// <param name="player">The member.</param>
    /// <param name="update">The news of their death.</param>
    /// <param name="tell">Whether to tell the local player, who is on their feet.</param>
    private void OnChaseDeath(ClientPlayerData player, CoopSaveUpdate update, bool tell) {
        _chaseStandUps[player.Id] = new ChaseStandUp(
            player.Id, update.Key, update.Scene, Time.unscaledTime + ChaseStandUpDelay
        );

        SetPartnerBodyHidden(player.Id, true);
        _chaseHiddenMembers.Add(player.Id);

        if (!tell) {
            return;
        }

        // Beside the local player when nobody else on their feet is in that room to stand them up
        var besideYou = !IsStandingMemberIn(update.Scene);
        Chat(
            besideYou
                ? Lang.Pick(
                    $"{player.Username} went down. They will stand up beside you in a moment.",
                    $"{player.Username} 倒下了，马上会在你身边站起来。"
                )
                : Lang.Pick(
                    $"{player.Username} went down. They will stand up beside a teammate on their feet in a moment.",
                    $"{player.Username} 倒下了，马上会在一个站着的队友身边站起来。"
                )
        );
    }

    /// <summary>
    /// Stands the local player up where they lie, at once, when the fight of an arena in the room of a lava that chases
    /// is won while they lie in their cocoon there, as a cocoon that is broken does (USER 10-22: "第2秒看起来已经晚了（，
    /// 可不可以这一次立刻把人复活，不要等3秒了，就像茧被打一样立刻给人家放地上"). The win is what starts the lava of such a
    /// room: two seconds later it shakes, and two more and it rises over where the fight was, where nobody can come
    /// back for a cocoon. Each game stands up its own player, as for a boss that fell (EndFightOfFallenBoss), and
    /// counts the members who lie in theirs there as standing (see <see cref="StandUpCocoonsIn"/>).
    /// </summary>
    /// <param name="scene">The room of the arena.</param>
    public void OnArenaWon(string scene) {
        StandUpCocoonsIn(scene, $"The fight in '{scene}', whose win starts the lava there, was won", FightOverNote);
    }

    /// <summary>
    /// Stands the local player up where they lie, at once, when the lava of their room starts while they lie in their
    /// cocoon there, as a cocoon that is broken does: nobody can come back for a cocoon that the lava is over a moment
    /// later (USER 10-22, as for <see cref="OnArenaWon"/>, and "还有另一个岩浆赛跑也是？那个岩浆赛跑应该是打掉石头上两个
    /// 爆炸点，石头掉下去岩浆就开始涨上来"). A lava that a falling rock starts starts as the rock lands, which is the first
    /// that the lava hears of it. One that the win of a fight starts stood them up at the win.
    /// </summary>
    /// <param name="chase">The lava, which chases.</param>
    private void StandUpBeforeTheLava(LavaChase chase) {
        StandUpCocoonsIn(chase.Scene, $"The lava in '{chase.Scene}' started", LavaRisingNote);
    }

    /// <summary>
    /// What a player who lay waiting is told as the start of a lava stands them up.
    /// </summary>
    private static string LavaRisingNote => Lang.Pick(
        "The lava is rising. You are back on your feet.", "岩浆涨起来了，你站起来了。"
    );

    /// <summary>
    /// Stands the local player up where they lie, at once, if they lie in their cocoon in the given room, which has a
    /// lava that chases, and counts the members who lie in theirs there as standing, whom their own games stand up for
    /// the same (see <see cref="CountCocoonsUp"/>). Asked when the fight whose win starts the lava is won, when the
    /// lava of this game starts, and when a member's game says that theirs chases or that they died in its chase.
    /// </summary>
    /// <param name="scene">The room.</param>
    /// <param name="what">What happened there, for the log.</param>
    /// <param name="note">What the local player is told as they stand up.</param>
    private void StandUpCocoonsIn(string scene, string what, string note) {
        if (!HasLavaChase(scene)) {
            return;
        }

        CountCocoonsUp(scene, what);
        if (_rescue is not { Outcome: RescueOutcome.Waiting, Chase: false } rescue || rescue.Scene != scene) {
            return;
        }

        Logger.Info($"{what} while the local player lay in their cocoon there, so they stand up where they lie");
        rescue.StandUpNote = note;
        rescue.Outcome = RescueOutcome.Rescued;
    }

    /// <summary>
    /// Counts the members who lie in their cocoons in the given room as standing from here on, as their own games stand
    /// them up for what happened there (see <see cref="StandUpCocoonsIn"/>), the way a member whose cocoon was broken
    /// counts as standing at once (see <see cref="MarkPulledUp"/>). Each game hears of the other's at its own time,
    /// and in the race of the falling rock the blast as it lands hurts right after the lava starts: a member it killed
    /// was taken for the last of everyone down while the one in the cocoon stood up, and sent to their bench.
    /// </summary>
    /// <param name="scene">The room.</param>
    /// <param name="what">What happened there, for the log.</param>
    private void CountCocoonsUp(string scene, string what) {
        List<ushort>? lying = null;
        foreach (var pair in _waitingMembers) {
            if (pair.Value.Scene == scene && _playerData.TryGetValue(pair.Key, out var member) &&
                member.IsInLocalScene) {
                (lying ??= []).Add(pair.Key);
            }
        }

        foreach (var playerId in lying ?? []) {
            Logger.Info($"{what} while player {playerId} lay in their cocoon there, so they count as standing");
            MarkPulledUp(playerId);
        }
    }

    /// <summary>
    /// Counts the members who lie in their cocoons under a lava of this room that chases as standing (see
    /// <see cref="CountCocoonsUp"/>), before a death of the local player asks whether anyone stands: the lava can have
    /// started since this game last looked, in the same frame, as the blast of the falling rock hurts right after it.
    /// </summary>
    private void CountCocoonsUpUnderTheLava() {
        try {
            if (_lavaChase is { } chase && IsLavaChasing(chase)) {
                CountCocoonsUp(chase.Scene, $"The lava in '{chase.Scene}' started");
            }
        } catch (Exception e) {
            LogLavaChaseError(e);
        }
    }

    /// <summary>
    /// Whether the room of the local player is the given one and has a lava that chases.
    /// </summary>
    /// <param name="scene">The room.</param>
    private bool HasLavaChase(string scene) {
        if (_lavaChase is { } chase) {
            return chase.Scene == scene;
        }

        return _pendingLavaFsm != null && _pendingLavaFsm.gameObject.scene.name == scene;
    }

    /// <summary>
    /// Forgets a member who was lying down in the chase, and puts their body back on the screen.
    /// </summary>
    private void ForgetChaseDeath(ushort playerId) {
        _chaseStandUps.Remove(playerId);

        if (_chaseHiddenMembers.Remove(playerId)) {
            SetPartnerBodyHidden(playerId, false);
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
        Logger.Error($"Could not keep the rising lava in step between the games:\n{e}");
    }
}
