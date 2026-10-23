using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Collection;
using SSMP.Fsm;
using SSMP.Game.Client.Entity.Action;
using SSMP.Game.Client.Entity.Component;
using SSMP.Game.Client.Save;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Math_Vector2 = SSMP.Math.Vector2;
using Math_Vector3 = SSMP.Math.Vector3;

// ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
#pragma warning disable CS8604 // Possible null reference argument.
#pragma warning disable CS8602 // Dereference of a possibly null reference.
#pragma warning disable CS0618 // Type or member is obsolete
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
#pragma warning disable CS0414 // Field is assigned but its value is never used

namespace SSMP.Game.Client.Entity;

/// <summary>
/// A networked entity that is either sending behaviour updates to the server or is entirely controlled by
/// updates from the server.
/// </summary>
internal partial class Entity {
    /// <summary>
    /// The net client for networking.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// MonoMod hook for tk2dSpriteAnimator.Play, one for all entities (see <see cref="OnAnyAnimationPlayed"/>).
    /// </summary>
    private static Hook? _spriteAnimatorPlayHook;

    /// <summary>
    /// MonoMod hook for ObjectPool.Recycle, one for all entities (see <see cref="OnAnyObjectRecycled"/>).
    /// </summary>
    private static Hook? _objectPoolRecycleHook;

    /// <summary>
    /// MonoMod hook for ActivateGameObject.OnEnter, one for all entities (see <see cref="OnAnyActivateGameObject"/>).
    /// </summary>
    private static Hook? _activateGameObjectHook;

    /// <summary>
    /// The entity of the animator of each creature and of its copy, for the hook on tk2dSpriteAnimator.Play that all
    /// entities share. Each entity used to put hooks of its own on these three methods of the game and take them off as
    /// it went. MonoMod builds and compiles a hook anew each time, and goes over every hook already on the method as
    /// one is put on or taken off, so the time that the entities of a room took to make grew faster than their number;
    /// and every clip that any animator played went through one hook of each creature.
    /// </summary>
    private static readonly Dictionary<tk2dSpriteAnimator, Entity> EntitiesByAnimator = new();

    /// <summary>
    /// Whether the room's switching of the room object on and off is still kept from it (see
    /// <see cref="OnDoActivateGameObject"/>), as it is until the entity is initialized as the scene host's.
    /// </summary>
    private bool _watchesActivation = true;

    /// <summary>
    /// Whether the entity has a parent entity.
    /// </summary>
    private readonly bool _hasParent;

    /// <summary>
    /// How many packets' worth of the scene host's positions have been put aside while a knockback of the local
    /// player moves this entity, so that the first position taken afterwards is known to cover more than one tick of
    /// theirs.
    ///
    /// Counted in packets rather than in positions, because the positions lost on the way were never here to be
    /// counted and they cover ground just the same.
    /// </summary>
    private int _positionsHeldBack;

    /// <summary>
    /// The shortest time between two lines about the room's own copy of this entity switching itself back on, in
    /// seconds, so that one that does it every frame cannot fill the log.
    /// </summary>
    private const float HostActiveLogInterval = 2f;

    /// <summary>
    /// When this entity may next say that the room's own copy of it switched itself back on.
    /// </summary>
    private float _nextHostActiveLogTime;

    /// <summary>
    /// Whether the scene host last said that this entity is switched on, as the copy then is too.
    /// </summary>
    private bool _sceneHostHasItOn;

    /// <summary>
    /// How many times the scene host has said whether this entity is on.
    /// </summary>
    private int _timesToldIfOn;

    /// <summary>
    /// When the scene host last said whether this entity is on, in unscaled time.
    /// </summary>
    private float _lastToldIfOn;

    /// <summary>
    /// How long, in seconds, a copy has to stay off while the scene host has it on before that is said: longer than a
    /// copy that something here switches off stays off before the scene host's own word that it is off arrives.
    /// </summary>
    private const float CopyOffGrace = 2f;

    /// <summary>
    /// Since when, in unscaled time, the copy has been off here although the scene host has it on, or a negative number
    /// while it is not (see <see cref="SayWhyTheCopyIsOff"/>).
    /// </summary>
    private float _copyOffSince = -1f;

    /// <summary>
    /// Whether this entity has said that its copy is off although the scene host has it on (see
    /// <see cref="SayWhyTheCopyIsOff"/>), which is said once for each time the scene host says it is on.
    /// </summary>
    private bool _saidTheCopyIsOff;

    /// <summary>
    /// Whether something other than the interpolation was moving the copy on the last frame - something the local
    /// player did to this entity still waiting to come back from the scene host, the copy moving by itself
    /// (<see cref="OwnMotionComponent"/>) or a pull of the local player's own moving it - which says when that has
    /// just stopped and the interpolation is about to take the entity over again.
    /// </summary>
    private bool _wasMovedHere;

    /// <summary>
    /// Until when a pull of the local player's own moves the copy, in game time (see <see cref="NotePulledHere"/>).
    /// </summary>
    private float _pulledHereUntil;

    /// <summary>
    /// The number the next thing the local player does to this entity before telling the scene host goes under.
    /// Zero is kept for "nothing", so it is skipped when the count comes round.
    /// </summary>
    private byte _nextAnticipation;

    /// <summary>
    /// The number of the last thing the local player did to this entity that the scene host has not said it has
    /// taken in yet, or zero when there is nothing to wait for.
    /// </summary>
    private byte _outstandingAnticipation;

    /// <summary>
    /// When the wait for <see cref="_outstandingAnticipation"/> is given up on, whatever the scene host has said.
    /// </summary>
    private float _outstandingExpiry;

    /// <summary>
    /// How the copy goes on while the wait for <see cref="_outstandingAnticipation"/> is for a knockback that the local
    /// game played first, or null while it is for nothing like that (see <see cref="BeginKnockbackAnticipation"/>).
    /// </summary>
    private KnockbackWait? _knockbackWait;

    /// <summary>
    /// How the last wait on a knockback that the local game played first ended, for the [Knock] lines (CoopHits): when,
    /// in game time, how far the scene host said it pushed the creature meanwhile, how far the copy went on by what the
    /// creature did by itself, and what ended the wait.
    /// </summary>
    public (float At, Vector2 Told, Vector2 OwnMoved, string How)? LastKnockbackWait { get; private set; }

    /// <summary>
    /// When the copy last went back to following the scene host's positions after the local game had moved it, in game
    /// time, and how far it then had to slide to get to them, for the [Knock] lines (CoopHits).
    /// </summary>
    public (float At, Vector2 Slide)? LastHandOver { get; private set; }

    /// <summary>
    /// The state that the FSM of the copy runs in here, or null while none runs here (see <see cref="PlayHere"/>).
    /// </summary>
    public string? RunsHereIn => _runHere != null ? _runHere.Fsm.ActiveStateName : null;

    /// <summary>
    /// What the scene host has been told of the things scene clients did to this entity, one for each game that told
    /// it, by the tag that game puts on what it asks (see <see cref="RequestTag"/>). Every position goes to everyone in
    /// the room, and each game takes only what is under its own tag: with more than one other player there, one game
    /// took the scene host's answer to another as its own, and let go of its wait before its own hit was in.
    /// </summary>
    private readonly List<AnticipationStamp> _stamps = [];

    /// <summary>
    /// The tag this game puts on what it asks the scene host to do to an entity, which comes back on the scene host's
    /// answers (see <see cref="BeginAnticipation"/>). Picked at random once, since a game is not told its own number on
    /// the server; two games picking the same one is one chance in tens of thousands.
    /// </summary>
    internal static readonly ushort RequestTag = PickRequestTag();

    /// <summary>
    /// Gets the figure of a player in the room that creatures go after, by their ID, or null if they have none here,
    /// like one who left or is down.
    /// </summary>
    internal static Func<ushort, GameObject?>? FigureOf { get; set; }

    /// <summary>
    /// Which positions of this entity are newer than the one it is standing at.
    /// </summary>
    private readonly PositionSequence _positionSequence;

    /// <summary>
    /// How long the positions of the scene host are held back while something the local player did to this entity is
    /// still on its way there and back, at the very most.
    ///
    /// Only reached when the scene host never says anything about it at all, which takes it leaving or the room
    /// changing under it: everything that can really happen comes back inside a round trip and however long it takes
    /// to happen over there, answered or refused. It is this long because a bound tight enough to cut that short
    /// would throw the wait away on exactly the connections it was written for, and because nothing worse than an
    /// entity standing still for a moment is on the other side of it. It has to be longer than a round trip plus
    /// <see cref="AnticipationSettleCap"/>, which is the longest anything answered can take.
    /// </summary>
    private const float AnticipationHoldTime = 1.5f;

    /// <summary>
    /// How long, in seconds of game time, a copy goes on being moved by a pull of this game after the last step of
    /// physics that pulled it: a few steps, so that the pull holds it from one step to the next and lets it go soon
    /// after it ends.
    /// </summary>
    private const float PulledHereHoldTime = 0.1f;

    /// <summary>
    /// The longest the scene host waits for something a scene client did to finish happening before saying it has
    /// it, in seconds.
    ///
    /// Long enough for the longest knockback in the game, which is what this waits out in practice. It is a cap and
    /// not a wait: what is really waited for is the thing itself ending, and this only keeps one that never ends -
    /// an enemy held in place, a component that went away underneath it - from keeping the other player waiting on
    /// an answer that is never coming.
    /// </summary>
    private const float AnticipationSettleCap = 0.5f;

    /// <summary>
    /// How many positions of an entity are sent whether it has moved or not once the scene host has taken in
    /// something a scene client did to it.
    ///
    /// One would do if nothing were ever lost. Positions travel by the way that drops what it cannot deliver, and
    /// an enemy that the scene host held still - against a wall, or one that is knocked back by standing still -
    /// sends nothing else of its own afterwards, so a single lost one costs the other player the whole of their
    /// wait. A handful of them is a few dozen bytes.
    /// </summary>
    private const int AnticipationForcedSends = 5;

    /// <summary>
    /// How long what the scene host has taken in is sent beside the positions of an entity, in seconds.
    ///
    /// Longer than <see cref="AnticipationHoldTime"/>, so that nobody can still be waiting on it when it stops, and
    /// not forever, so that a fight does not leave every enemy in the room carrying a byte that says something
    /// nobody is listening for any more.
    /// </summary>
    private const float AnticipationStampTime = 2.5f;

    /// <summary>
    /// When, in seconds after it last changed, the scene host sends again which way an entity faces and the place it
    /// came to stand in.
    ///
    /// Each of these goes once, when it changes, by the way that drops what it cannot deliver, and nothing comes after
    /// it until the next change. A creature that turned to charge and then flew straight sent its turn once, and a copy
    /// that missed it flew the whole charge rear first (USER 10-10, "飞天怪用屁股撞"). The player's own facing had been
    /// lost the same way and was walked backwards (ClientManager). Sent again a few times at growing gaps, a short break
    /// in the line is covered for a few bytes each; a copy that already has it changes nothing. Not the clip it starts,
    /// which goes the reliable way (ReliableEntityUpdate.AnimationId): sent again, it started over on a copy that had
    /// played it to its end.
    /// </summary>
    private static readonly float[] SentAgainAfter = [0.1f, 0.3f, 0.7f];

    /// <summary>
    /// The ID of the entity.
    /// </summary>
    public ushort Id { get; }

    /// <summary>
    /// The type of the entity.
    /// </summary>
    public EntityType Type { get; }

    /// <summary>
    /// Host-client pair for the game objects.
    /// </summary>
    public HostClientPair<GameObject> Object { get; }

    /// <summary>
    /// Gets the list of PlayMaker FSMs on the host game object.
    /// </summary>
    public List<PlayMakerFSM> HostFsms => _fsms.Host;

    /// <summary>
    /// Gets the list of PlayMaker FSMs on the client game object.
    /// </summary>
    public List<PlayMakerFSM> ClientFsms => _fsms.Client;

    /// <summary>
    /// Host-client pair for the sprite animators.
    /// </summary>
    private readonly HostClientPair<tk2dSpriteAnimator> _animator;

    /// <summary>
    /// Bi-directional lookup for animation clip names to IDs.
    /// </summary>
    private readonly BiLookup<string, byte> _animationClipNameIds;

    /// <summary>
    /// Host-client pair for the lists of FSMs on the entity.
    /// </summary>
    private readonly HostClientPair<List<PlayMakerFSM>> _fsms;

    /// <summary>
    /// The actions of the FSMs that the copy runs by itself that read how the copy moves, or null for none (see
    /// <see cref="ReadyToRunOnTheCopy"/>).
    /// </summary>
    private HashSet<FsmStateAction>? _motionReadsOfTheCopy;

    /// <summary>
    /// Dictionary mapping data types to entity components. Made with the entity, as what its constructor does before
    /// the components are handled asks about them: the saves of the copy ask whether it was told dead (CopyIsGone),
    /// which threw for a creature with a number in the save whose copy does not run here, every time the game that
    /// expected to run its room came in (a creature in a fossil, 10-23 logs), and left the rest of the room's
    /// creatures unmade.
    /// </summary>
    private readonly Dictionary<EntityComponentType, EntityComponent> _components = new();

    /// <summary>
    /// Unique list of components that require periodic update calls on the host.
    /// </summary>
    private readonly List<EntityComponent> _updatableComponents = [];

    /// <summary>
    /// Whether the constructor got to its end. The updates of each frame are hooked half way through it, and one that
    /// threw after that left them running every frame for an entity that was never finished, failing every time.
    /// </summary>
    private readonly bool _made;

    /// <summary>
    /// Dictionary mapping FSM actions to their entity action data instances.
    /// </summary>
    private readonly Dictionary<FsmStateAction, HookedEntityAction> _hookedActions;

    /// <summary>
    /// The entity that each action of the room's own creatures that throws or tells something is sent for, whose throws
    /// are named for the other game to know them by (see ThrownThings), and so are the parts of the room it tells (see
    /// ToldParts).
    /// </summary>
    private static readonly ConditionalWeakTable<FsmStateAction, Entity> ThrowingActions = new();

    /// <summary>
    /// Whether an action throws or tells something for a creature that this game runs, and sends it to the other game.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <param name="thrower">The room's own creature that throws or tells.</param>
    public static bool TryGetThrower(FsmStateAction action, out GameObject thrower) {
        thrower = null!;
        if (!ThrowingActions.TryGetValue(action, out var entity) || entity._isControlled ||
            entity.Object.Host == null) {
            return false;
        }

        thrower = entity.Object.Host;
        return true;
    }

    /// <summary>
    /// Set of FSM action types that have been hooked to prevent duplicate hooks.
    /// </summary>
    private readonly HashSet<Type> _hookedTypes;

    /// <summary>
    /// Whether the unity game object for the host entity was originally active.
    /// </summary>
    private bool _originalIsActive;

    /// <summary>
    /// Whether the entity is controlled, i.e. in control by updates from the server.
    /// </summary>
    private bool _isControlled;

    /// <summary>
    /// Whether the scene host is determined, or alternatively whether the entity has been determined to be a host or client entity.
    /// </summary>
    private bool _isSceneHostDetermined;

    /// <summary>
    /// Whether the host object was left running when the entity was made, because the local client runs the scene or
    /// expects to, and the scene host has not been determined since. It is not deactivated until then like it would
    /// otherwise be, because activating it again makes PlayMaker start its FSMs over.
    /// </summary>
    private bool _keepsRunning;

    /// <summary>
    /// The last position of the entity.
    /// </summary>
    private Vector3 _lastPosition;

    /// <summary>
    /// Whether the entity was moving by itself in the scene host's game at the last update (see
    /// <see cref="HostMovesByItself"/>).
    /// </summary>
    private bool _hostMovedByItself;

    /// <summary>
    /// The last scale of the entity.
    /// </summary>
    private Vector3 _lastScale;

    /// <summary>
    /// When the place and the scale of this game's creature were last changed and sent, in unscaled seconds, and how
    /// many times each has been sent again since (see <see cref="SentAgainAfter"/>). Counted as all sent until the
    /// first change.
    /// </summary>
    private float _positionChangedAt, _scaleChangedAt;

    /// <inheritdoc cref="_positionChangedAt"/>
    private int _positionSentAgain = SentAgainAfter.Length, _scaleSentAgain = SentAgainAfter.Length;

    /// <summary>
    /// The axes of the scale that changed since it was last sent other than again, which are what is sent again.
    /// </summary>
    private bool _scaleAgainX, _scaleAgainY, _scaleAgainZ;

    /// <summary>
    /// The packet order of the scales the copy took, so that an older one arriving after a newer one is thrown away, as
    /// for positions (see <see cref="UpdatePosition"/>).
    /// </summary>
    private readonly PositionSequence _scaleSequence;

    /// <summary>
    /// Which way the game that runs this creature last said its copy faces (the sign of its x scale), and when, for the
    /// log of what hurt the player (CoopSave.Hurts); 0 until it has said.
    /// </summary>
    private float _toldFacing, _toldFacingAt;

    /// <summary>
    /// Whether the game object for the entity was last active.
    /// </summary>
    private bool _lastIsActive;

    /// <summary>
    /// Whether to allow the client entity to animate itself.
    /// </summary>
    private bool _allowClientAnimation;

    /// <summary>
    /// List of snapshots for each FSM of a host entity that contain latest values for state and FSM variables.
    /// Used to check whether state/variables change and to update the server accordingly.
    /// </summary>
    private readonly List<FsmSnapshot> _fsmSnapshots;

    /// <summary>
    /// Whether the host FSMs still have to be put back where they were before this game took the room in.
    /// </summary>
    private bool _putFsmsBackWhereTheyWere;

    /// <summary>
    /// Where each host FSM was standing the last time this mod switched the object off, or null for one that had not
    /// started yet. It is kept apart from the FSM snapshots, which on a scene client hold what the other game says its
    /// own copy is doing and must not be written over with what this room's sleeping copy was doing.
    /// </summary>
    private string?[]? _statesWhenWeSwitchedOff;

    public Entity(
        NetClient netClient,
        ushort id,
        EntityType type,
        GameObject hostObject,
        bool keepsRunning,
        GameObject clientObject = null,
        params EntityComponentType[] types
    ) {
        _netClient = netClient;
        Id = id;

        Type = type;

        _positionSequence = new PositionSequence($"entity {id} ({type})");
        _scaleSequence = new PositionSequence($"the facing of entity {id} ({type})");

        _isControlled = true;
        _keepsRunning = keepsRunning;

        if (clientObject == null) {
            Object = new HostClientPair<GameObject> {
                Host = hostObject,
                Client = UnityEngine.Object.Instantiate(
                    hostObject,
                    hostObject.transform.position,
                    hostObject.transform.rotation
                )
            };

            DestroyManagedChildren(Object.Client);

            _hasParent = false;
        } else {
            Object = new HostClientPair<GameObject> {
                Host = hostObject,
                Client = clientObject
            };

            _hasParent = true;
        }

        _madeWithCopy = Object.Client.GetComponentsInChildren<Transform>(true);

        Object.Client.transform.localScale = _lastScale = _hasParent
            ? Object.Host.transform.localScale
            : Object.Host.transform.lossyScale;

        // Store whether the host object was active, and unless it keeps running, set it not active until we know if we
        // are scene host
        _originalIsActive = Object.Host.activeSelf;

        _lastIsActive = _hasParent ? Object.Host.activeSelf : Object.Host.activeInHierarchy;

        // Add a position interpolation component to the enemy so we can smooth out position updates. A part of another
        // entity goes without: the parent's copy carries it (UpdatePosition)
        if (!_hasParent) {
            Object.Client.AddComponent<PredictiveInterpolation>();
        }

        // Register an update event to send position updates and check for certain value changes
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
        MonoBehaviourUtil.Instance.OnLateUpdateEvent += OnLateUpdate;

        _animator = new HostClientPair<tk2dSpriteAnimator> {
            Host = Object.Host.GetComponent<tk2dSpriteAnimator>(),
            Client = Object.Client.GetComponent<tk2dSpriteAnimator>()
        };
        if (_animator.Host != null) {
            _animationClipNameIds = new BiLookup<string, byte>();

            var index = 0;
            foreach (var animationClip in _animator.Host.Library.clips) {
                if (_animationClipNameIds.ContainsFirst(animationClip.name)) {
                    continue;
                }

                _animationClipNameIds.Add(animationClip.name, (byte) index++);

                if (index > byte.MaxValue) {
                    break;
                }
            }

            EntitiesByAnimator[_animator.Host] = this;
            if (_animator.Client != null) {
                EntitiesByAnimator[_animator.Client] = this;
            }

            _spriteAnimatorPlayHook ??= new Hook(
                typeof(tk2dSpriteAnimator).GetMethod(
                    nameof(tk2dSpriteAnimator.Play),
                    [typeof(tk2dSpriteAnimationClip), typeof(float), typeof(float)]
                ),
                OnAnyAnimationPlayed
            );
        }

        // Always disallow the client object from being recycled, because it will simply be destroyed
        _objectPoolRecycleHook ??= new Hook(
            typeof(ObjectPool).GetMethod(
                nameof(ObjectPool.Recycle),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
                null,
                [typeof(GameObject)],
                null
            ),
            OnAnyObjectRecycled
        );

        // Register a hook for the ActivateGameObject action to update the active state of the host game object
        // before scene host is determined
        _activateGameObjectHook ??= new Hook(
            typeof(ActivateGameObject).GetMethod(
                nameof(ActivateGameObject.OnEnter),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            ),
            OnAnyActivateGameObject
        );

        _fsms = new HostClientPair<List<PlayMakerFSM>> {
            Host = Object.Host.GetComponents<PlayMakerFSM>().ToList(),
            Client = Object.Client.GetComponents<PlayMakerFSM>().ToList()
        };

        EntitiesByCopy[Object.Client] = this;
        EntitiesByRoomObject[Object.Host] = this;

        // Bounds that hold the creature in the space of what it sits under in the room hold the copy, which sits under
        // nothing, through that same object (OnConstrainLateUpdate)
        // (The game's own LateUpdate is private, which the referenced library does not show.)
        if (!_hasParent && !_constrainHookTried && Object.Host.transform.parent != null &&
            Object.Client.GetComponents<ConstrainPosition>().Any(constrain => constrain.localSpace)) {
            _constrainHookTried = true;
            var lateUpdate = typeof(ConstrainPosition).GetMethod(
                "LateUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                System.Type.EmptyTypes, null
            );
            if (lateUpdate != null) {
                _constrainHook = new Hook(lateUpdate, OnConstrainLateUpdate);
            } else {
                SSMP.Logging.Logger.Warn(
                    "Could not find how the game holds a creature within its bounds, so copies held in the space of " +
                    "what they sit under are held in the world instead"
                );
            }
        }

        // A wave of an arena takes the wish items off the corpses of the creatures in it as the room loads
        // (BattleWave.UpdateChildInfo), but only off the room's own objects. The copy, made from one of them and sitting
        // under no wave, kept them, so the game that follows the room dropped wish items that the game that runs it
        // never did.
        if (Object.Host.transform.parent is { } roomParent && roomParent.GetComponent<BattleWave>() != null) {
            foreach (var variant in Object.Client.GetComponentsInChildren<SpecialQuestItemVariant>(true)) {
                variant.SetInactive();
            }

            var deathEffects = Object.Client.GetComponent<EnemyDeathEffects>();
            if (deathEffects != null) {
                deathEffects.DisableSpecialQuestDrops();
            }
        }

        _broadcastHook ??= new Hook(
            typeof(HutongGames.PlayMaker.Fsm).GetMethod(
                nameof(HutongGames.PlayMaker.Fsm.BroadcastEventToGameObject),
                [typeof(GameObject), typeof(FsmEvent), typeof(FsmEventData), typeof(bool), typeof(bool)]
            ),
            OnBroadcastToObject
        );
        if (!_sendToFsmHookTried) {
            _sendToFsmHookTried = true;
            var sendToFsm = typeof(HutongGames.PlayMaker.Fsm).GetMethod(
                "SendEventToFsmOnGameObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                [typeof(GameObject), typeof(string), typeof(FsmEvent)], null
            );
            if (sendToFsm != null) {
                _sendToFsmHook = new Hook(sendToFsm, OnSendToFsmOnObject);
            } else {
                SSMP.Logging.Logger.Warn("Could not find how an FSM tells another FSM by name, so catches of the " +
                                         "local player's things by the parts of copies go unheard");
            }

            _triggerEnterHook = new Hook(
                typeof(HutongGames.PlayMaker.Fsm).GetMethod(
                    nameof(HutongGames.PlayMaker.Fsm.OnTriggerEnter2D), [typeof(Collider2D)]
                )!,
                OnFsmTriggerEnter2D
            );
            HookTalks();
        }

        _hookedActions = new Dictionary<FsmStateAction, HookedEntityAction>();
        _hookedTypes = [];
        _fsmSnapshots = [];
        foreach (var fsm in _fsms.Host) {
            ProcessHostFsm(fsm);
        }

        // Remove all components that (re-)activate FSMs
        foreach (var fsmActivator in Object.Client.GetComponents<FSMActivator>()) {
            fsmActivator.StopAllCoroutines();
            UnityEngine.Object.Destroy(fsmActivator);
        }

        foreach (var fsm in _fsms.Client) {
            ProcessClientFsm(fsm);
        }

        KeepSavesOfTheCopy();

        HandleComponents(types);

        HandleEnemyDeathEffects();

        foreach (var component in _components.Values) {
            if (component == null) {
                continue;
            }

            // What the parts send goes through the entity, which holds it back with its replays (SendEntityData)
            component.SendThrough = SendEntityData;
            if (component is not HealthManagerComponent && !_updatableComponents.Contains(component)) {
                _updatableComponents.Add(component);
            }
        }

        if (!_keepsRunning) {
            RememberWhereTheHostFsmsAre();
            Object.Host.SetActive(false);
        }

        Object.Client.SetActive(false);
        _made = true;
    }

    /// <summary>
    /// Destroy the children of the given game object that are registered entities in the system themselves.
    /// Recursively go through the non-registered children as well.
    /// </summary>
    /// <param name="root">The root game object to start searching for children.</param>
    private void DestroyManagedChildren(GameObject root) {
        foreach (var child in root.GetChildren()) {
            if (EntityRegistry.TryGetEntry(child, out _)) {
                UnityEngine.Object.Destroy(child);
            } else {
                DestroyManagedChildren(child);
            }
        }
    }

    /// <summary>
    /// Processes the given FSM for the host entity by hooking supported FSM actions.
    /// </summary>
    /// <param name="fsm">The Playmaker FSM to process.</param>
    private void ProcessHostFsm(PlayMakerFSM fsm) {
        // One made from a template is made only when its object is first switched on (in its Awake), and what this
        // hooks until then is a stand-in that never runs (see HookSummoningMadeLater). Whether it has been made yet
        // can't be told from the FSM: the room loading it already marks the stand-in set up. An object switched off
        // after its Awake ran only leaves an entry here that never comes due, cleared with the entity (Destroy).
        if (fsm.FsmTemplate != null && !fsm.gameObject.activeInHierarchy) {
            WaitForTheFsmToBeMade(fsm);
        }

        EntityInitializer.CheckPreProcessFsm(fsm);

        // Nothing of what an FSM that each game runs by itself does is sent (see IsRunByEachGame)
        if (!IsRunByEachGame(fsm)) {
            HookActions(fsm);
        }

        _fsmSnapshots.Add(TakeSnapshot(fsm));
    }

    /// <summary>
    /// Hooks the supported actions of an FSM of the room's own creature, so that what they do is sent to the other game.
    /// </summary>
    /// <param name="fsm">The Playmaker FSM to hook the actions of.</param>
    private void HookActions(PlayMakerFSM fsm) {
        for (var i = 0; i < fsm.FsmStates.Length; i++) {
            var state = fsm.FsmStates[i];

            for (var j = 0; j < state.Actions.Length; j++) {
                var action = state.Actions[j];
                if (!action.Enabled) {
                    continue;
                }

                if (!EntityFsmActions.SupportedActionTypes.Contains(action.GetType())) {
                    continue;
                }

                if (action.Fsm == null) {
                    continue;
                }

                _hookedActions[action] = new HookedEntityAction {
                    Action = action,
                    FsmIndex = _fsms.Host.IndexOf(fsm),
                    StateIndex = i,
                    ActionIndex = j
                };

                if (action is SpawnObjectFromGlobalPool or FlingObjectsFromGlobalPool
                    or FlingObjectsFromGlobalPoolVel or SpawnRandomObjectsRadialV2 or FlingObjectsFromGlobalPoolTime
                    or FlingObjectsFromGlobalPoolVelTime or SpawnObjectFromGlobalPoolOverTime
                    or SpawnObjectFromGlobalPoolOverTimeV2 or SendEventByName or SendEventByNameV2
                    or SendEventByNameOnExit or SendEventToRegister or SendEventToRegisterV2) {
                    ThrowingActions.AddOrUpdate(action, this);
                }

                if (_hookedTypes.Add(action.GetType())) {
                    FsmActionHooks.RegisterFsmStateActionType(action.GetType(), OnActionEntered);
                }
            }
        }
    }

    /// <summary>
    /// What an FSM of the room's own creature is in now, which what it does from here on is told against.
    /// </summary>
    /// <param name="fsm">The Playmaker FSM.</param>
    private static FsmSnapshot TakeSnapshot(PlayMakerFSM fsm) {
        return new FsmSnapshot {
            CurrentState = fsm.ActiveStateName,
            Floats = fsm.FsmVariables.FloatVariables.Select(f => f.Value).ToArray(),
            Ints = fsm.FsmVariables.IntVariables.Select(i => i.Value).ToArray(),
            Bools = fsm.FsmVariables.BoolVariables.Select(b => b.Value).ToArray(),
            Strings = fsm.FsmVariables.StringVariables.Select(s => s.Value).ToArray(),
            Vector2s = fsm.FsmVariables.Vector2Variables.Select(v => v.Value).ToArray(),
            Vector3s = fsm.FsmVariables.Vector3Variables.Select(v => v.Value).ToArray()
        };
    }

    /// <summary>
    /// The FSMs of the room's own creatures that are made from a template and are not made yet, each with the entity it
    /// belongs to (see <see cref="HookSummoningMadeLater"/>).
    /// </summary>
    private static readonly Dictionary<PlayMakerFSM, Entity> FsmsMadeLater = new();

    /// <summary>
    /// The hook that hears an FSM being made, put in place with the first FSM that is waited for.
    /// </summary>
    private static Hook? _fsmInitHook;

    /// <summary>
    /// Whether <see cref="_fsmInitHook"/> was looked for, so that a game without the method says so only once.
    /// </summary>
    private static bool _fsmInitHookTried;

    /// <summary>
    /// Waits for an FSM of the room's own creature to be made from its template, which happens the first time its
    /// object is switched on (PlayMakerFSM.Init).
    /// </summary>
    /// <param name="fsm">The Playmaker FSM.</param>
    private void WaitForTheFsmToBeMade(PlayMakerFSM fsm) {
        FsmsMadeLater[fsm] = this;

        if (_fsmInitHookTried) {
            return;
        }

        _fsmInitHookTried = true;

        var init = typeof(PlayMakerFSM).GetMethod(
            "Init",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            System.Type.EmptyTypes,
            null
        );
        if (init == null) {
            SSMP.Logging.Logger.Warn(
                "Could not find how an FSM is made from its template, so what a creature that is switched on late " +
                "summons stays in the game of the scene host"
            );
            return;
        }

        _fsmInitHook = new Hook(init, OnFsmInit);
    }

    /// <summary>
    /// Callback for an FSM having been made, which hooks it if it is one that was waited for.
    /// </summary>
    private static void OnFsmInit(Action<PlayMakerFSM> orig, PlayMakerFSM self) {
        orig(self);

        if (FsmsMadeLater.Count == 0 || !FsmsMadeLater.Remove(self, out var entity)) {
            return;
        }

        entity.HookSummoningMadeLater(self);
    }

    /// <summary>
    /// Hooks an FSM of the room's own creature that the game has just made from its template, if it summons creatures.
    ///
    /// The game makes such an FSM afresh the first time its object is switched on, with actions of its own, so for a
    /// creature that is switched off when its room loads it was the actions of a stand-in that got hooked, and those
    /// never run. What the creature summoned was then made in the game of the scene host alone and the other player
    /// never saw it: the creatures that march into one room only once it has loaded call others in this way. Only an
    /// FSM that summons is hooked here. Many creatures that are switched on late have FSMs made from templates, and what
    /// the rest of those do has never been sent - which nobody has reported as missing.
    /// </summary>
    /// <param name="fsm">The Playmaker FSM, just made.</param>
    private void HookSummoningMadeLater(PlayMakerFSM fsm) {
        var fsmIndex = _fsms.Host.IndexOf(fsm);
        if (fsmIndex < 0 || fsmIndex >= _fsmSnapshots.Count || IsRunByEachGame(fsm) ||
            !EntitySpawner.SpawnsCreatures(fsm)) {
            return;
        }

        HookActions(fsm);
        _fsmSnapshots[fsmIndex] = TakeSnapshot(fsm);

        SSMP.Logging.Logger.Info(
            $"'{Object.Host.name}' ({Id}, {Type}) made its '{fsm.FsmName}' only on being switched on, so what it " +
            "summons is now kept in step"
        );
    }

    /// <summary>
    /// Processes the given FSM for the client entity by disabling it, unless it is one that each game runs by itself
    /// (see <see cref="IsRunByEachGame"/>), which the copy runs.
    /// </summary>
    /// <param name="fsm">The Playmaker FSM to process.</param>
    private void ProcessClientFsm(PlayMakerFSM fsm) {
        if (IsRunByEachGame(fsm)) {
            ReadyToRunOnTheCopy(fsm);
            return;
        }

        EntityInitializer.InitializeFsm(fsm);
        fsm.enabled = false;
    }

    /// <summary>
    /// Whether an FSM is one that each game runs by itself, as the registry entry of the entity says (see
    /// <see cref="EntityRegistryEntry.EachGameFsms"/> and <see cref="EntityRegistryEntry.HitterGameFsms"/>).
    /// </summary>
    private bool IsRunByEachGame(PlayMakerFSM fsm) {
        return EntityRegistry.IsRunByEachGame(Type, fsm.FsmName);
    }

    /// <summary>
    /// Readies an FSM that the copy runs by itself to find out about the copy what the room's own creature finds out
    /// about itself. The copy stands on its own at the top of the scene and is carried to wherever the scene host says
    /// the creature is. Asked for its parent, it found none, so a flea read how tired its game had made the player -
    /// which makes each knock harder - from nothing at all. Asked how it moved, its own body said it stood still, so a
    /// flea knocked the player to either side at random rather than the way it flew. Its parent and grandparent are
    /// now those of the room's own creature, and it moves as it is seen to move.
    /// </summary>
    /// <param name="fsm">The FSM of the copy.</param>
    private void ReadyToRunOnTheCopy(PlayMakerFSM fsm) {
        EntityInitializer.CheckPreProcessFsm(fsm);

        var parent = Object.Host.transform.parent;
        var grandparent = parent == null ? null : parent.parent;
        foreach (var state in fsm.FsmStates) {
            foreach (var action in state.Actions) {
                switch (action) {
                    case GetParent { gameObject.OwnerOption: OwnerDefaultOption.UseOwner } getParent:
                        getParent.Enabled = false;
                        getParent.storeResult.Value = parent == null ? null : parent.gameObject;
                        break;
                    case GetGrandparent { gameObject.OwnerOption: OwnerDefaultOption.UseOwner } getGrandparent:
                        getGrandparent.Enabled = false;
                        getGrandparent.storeResult.Value = grandparent == null ? null : grandparent.gameObject;
                        break;
                    case GetVelocity2d { gameObject.OwnerOption: OwnerDefaultOption.UseOwner } getVelocity:
                        if (_motionReadsOfTheCopy == null) {
                            _motionReadsOfTheCopy = [];
                            FsmActionHooks.RegisterFsmStateActionType(typeof(GetVelocity2d), OnMotionRead);
                        }

                        _motionReadsOfTheCopy.Add(getVelocity);
                        break;
                    // Whether the copy is there at all is the scene host's to say. A creature that the save of this
                    // game calls dead switches itself off as it starts, while the save of the scene host's player may
                    // call it alive: the copy was gone for this player while the other went on fighting it
                    case ActivateGameObject { gameObject.OwnerOption: OwnerDefaultOption.UseOwner } switchOff
                        when !switchOff.activate.UsesVariable && !switchOff.activate.Value:
                        switchOff.Enabled = false;
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Callback for an action having read how its object moves, which tells one of the FSMs that the copy runs by
    /// itself how the copy is seen to move (see <see cref="ReadyToRunOnTheCopy"/>).
    /// </summary>
    /// <param name="action">The action.</param>
    private void OnMotionRead(FsmStateAction action) {
        if (action is not GetVelocity2d read || _motionReadsOfTheCopy?.Contains(read) != true ||
            !Object.Client.TryGetComponent<PredictiveInterpolation>(out var interpolation)) {
            return;
        }

        Vector2 velocity = interpolation.Velocity;
        if (read.space == Space.Self) {
            velocity = Object.Client.transform.InverseTransformDirection(velocity);
        }

        read.vector.Value = velocity;
        read.x.Value = velocity.x;
        read.y.Value = velocity.y;
    }

    /// <summary>
    /// Check the host and client objects for components that are supported for networking.
    /// </summary>
    private void HandleComponents(EntityComponentType[] types) {
        var hostHealthManager = Object.Host.GetComponent<HealthManager>();
        var clientHealthManager = Object.Client.GetComponent<HealthManager>();
        if (hostHealthManager != null && clientHealthManager != null) {
            var healthManager = new HostClientPair<HealthManager> {
                Host = hostHealthManager,
                Client = clientHealthManager
            };

            var hmComponent = new HealthManagerComponent(
                _netClient,
                Id,
                Object,
                healthManager,
                Type
            ) {
                CarriedByParent = _hasParent
            };
            _components[EntityComponentType.Death] = hmComponent;
            _components[EntityComponentType.Health] = hmComponent;
            _components[EntityComponentType.Invincibility] = hmComponent;
            hmComponent.CopyDeathChanged += RecordCopyDeath;

            // Check if the object from the health manager is in any of the colosseum trial scenes and remove the
            // geo drops from them if so
            var goScene = hostHealthManager.gameObject.scene.name;
            if (goScene is "Room_Colosseum_Bronze" or "Room_Colosseum_Silver" or "Room_Colosseum_Gold") {
                clientHealthManager.SetGeoSmall(0);
                clientHealthManager.SetGeoMedium(0);
                clientHealthManager.SetGeoLarge(0);
            }
        }

        var climber = Object.Client.GetComponent<Climber>();
        if (climber != null) {
            _components[EntityComponentType.Climber] = new ClimberComponent(
                _netClient,
                Id,
                Object,
                climber
            );
            _components[EntityComponentType.Rotation] = new RotationComponent(
                _netClient,
                Id,
                Object
            );
        }

        var hostCollider = Object.Host.GetComponent<Collider2D>();
        var clientCollider = Object.Client.GetComponent<Collider2D>();
        if (hostCollider != null && clientCollider != null) {
            var collider = new HostClientPair<Collider2D> {
                Host = hostCollider,
                Client = clientCollider
            };

            _components[EntityComponentType.Collider] = new ColliderComponent(
                _netClient,
                Id,
                Object,
                collider
            );

            // What the collider can be hit by goes with the layer it is on
            _components[EntityComponentType.Layer] = new LayerComponent(_netClient, Id, Object);
        }

        var hostBody = Object.Host.GetComponent<Rigidbody2D>();
        if (hostBody != null) {
            _components[EntityComponentType.BodyType] = new BodyTypeComponent(
                _netClient,
                Id,
                Object,
                hostBody
            );
        }

        var hostDamageHeroes = Object.Host.GetComponentsInChildren<DamageHero>(true);
        var clientDamageHeroes = Object.Client.GetComponentsInChildren<DamageHero>(true);
        if (hostDamageHeroes.Length > 0 && clientDamageHeroes.Length > 0) {
            _components[EntityComponentType.DamageHero] = new DamageHeroComponent(
                _netClient,
                Id,
                Object,
                hostDamageHeroes,
                clientDamageHeroes
            );
        }

        var hostMeshRenderer = Object.Host.GetComponent<MeshRenderer>();
        var clientMeshRenderer = Object.Client.GetComponent<MeshRenderer>();
        if (hostMeshRenderer != null && clientMeshRenderer != null) {
            var meshRenderer = new HostClientPair<MeshRenderer> {
                Host = hostMeshRenderer,
                Client = clientMeshRenderer
            };

            _components[EntityComponentType.MeshRenderer] = new MeshRendererComponent(
                _netClient,
                Id,
                Object,
                meshRenderer
            );
        }

        EntityInitializer.RemoveClientTypes(Object.Client);

        // Instantiate all types defined in the entity registry, which are passed to the constructor
        foreach (var type in types) {
            var component = ComponentFactory.InstantiateByType(type, _netClient, Id, Object);
            if (component != null) {
                _components[type] = component;
            }
        }

        // The parts that the creature's own FSMs dress it in, which a player walking in later and a game taking the
        // creature over would otherwise not know about. An FSM that each game runs by itself dresses each game's
        // creature by itself.
        var partPaths = PartsComponent.FindParts(Object.Host, _fsms.Host.Where(fsm => !IsRunByEachGame(fsm)));
        if (partPaths.Count > 0) {
            _components[EntityComponentType.Parts] = new PartsComponent(_netClient, Id, Object, partPaths);
            SSMP.Logging.Logger.Info(
                $"'{Object.Host.name}' ({Id}, {Type}) has parts that its FSMs switch: {string.Join(", ", partPaths)}"
            );
        }

        // The sprites that the FSMs on the creature colour, where the copy may not have followed them
        var spritePaths = TintComponent.FindSprites(Object.Host, _fsms.Host.Where(fsm => !IsRunByEachGame(fsm)));
        if (spritePaths.Count > 0) {
            _components[EntityComponentType.Tint] = new TintComponent(_netClient, Id, Object, spritePaths);
        }
    }

    /// <summary>
    /// Handle specifics for a set of enemies that rely on EnemyDeathEffects for additional enemies.
    /// </summary>
    private void HandleEnemyDeathEffects() {
    }

    /// <summary>
    /// The replays of the creature that the scene host holds back until the end of the frame, in the order they came,
    /// from one that switches on the drawing of the creature (see <see cref="DrawsItselfOn"/>); null while none are.
    /// They go out after where the creature is by then (see <see cref="SendHeldOncePlaced"/>). Sent at once, the copy
    /// was drawn where the creature had been: where the creature's FSM put it, before or after it showed it, only went
    /// out with the next look at the entity, often in the packet after. A wave of creatures that rested out of sight
    /// until called showed up at its resting place, and only then came flying in from where its wave starts it (USER
    /// 10-21: 出怪的时候我这边怪会先闪一下，然后再出来).
    /// </summary>
    private List<EntityNetworkData>? _heldUntilPlaced;

    /// <summary>
    /// The frame in which <see cref="_heldUntilPlaced"/> began.
    /// </summary>
    private int _heldSince;

    /// <summary>
    /// Callback method for entering a hooked FSM action.
    /// </summary>
    /// <param name="self">The FSM action instance that was entered.</param>
    private void OnActionEntered(FsmStateAction self) {
        if (_isControlled) {
            // Left running while it is still being settled who runs the room, the creature sends nothing yet. What it
            // spawns meanwhile is still looked at where the data of the spawning action is made
            // (EntityManager.OnGameObjectSpawned), so the data is made and goes nowhere. A creature is made an entity
            // as it is spawned or never, and the server hears of it once this game turns out to run the room. Missed,
            // it stayed in this game alone: a nest makes its first creatures in the first frames of its room, and the
            // other player watched the player of this game swing at nothing.
            if (_keepsRunning && _hookedActions.ContainsKey(self)) {
                EntityFsmActions.GetNetworkDataFromAction(new EntityNetworkData(), self);
            }

            return;
        }

        if (!_hookedActions.TryGetValue(self, out var hookedEntityAction)) {
            return;
        }

        // Whatever the FSM does may change how the entity moves, which the other game is then told
        if (_components.TryGetValue(EntityComponentType.OwnMotion, out var component) &&
            component is OwnMotionComponent ownMotion) {
            ownMotion.MarkChanged();
        }

        // The state goes out with the actions that entering it runs, in the same update. Left to the next look at the
        // FSMs it could go in the update after them, and the other game, which takes the states of an update before
        // its actions, could not tell the action of a state it is yet to hear of from one of a state that was over
        // before the update was even sent.
        SendStateChange(hookedEntityAction.FsmIndex);

        var networkData = new EntityNetworkData {
            Type = EntityComponentType.Fsm
        };

        if (_fsms.Host.Count > 1) {
            networkData.Packet.Write((byte) hookedEntityAction.FsmIndex);
        }

        networkData.Packet.Write((byte) hookedEntityAction.StateIndex);
        networkData.Packet.Write((byte) hookedEntityAction.ActionIndex);

        // Which of its parts the action worked on, when a variable holds it (read back before the action's own data)
        EntityFsmActions.WriteSubject(networkData, self);

        // Only if the GetNetworkDataFromAction method returns true do we add the entity data
        // for sending
        var sent = EntityFsmActions.GetNetworkDataFromAction(networkData, self);
        if (sent) {
            if (_heldUntilPlaced == null && DrawsItselfOn(self)) {
                _heldUntilPlaced = [];
                _heldSince = Time.frameCount;
            }

            SendEntityData(networkData);
        }

        if (IsDazeRingSpawn(self)) {
            NoteDazeRingSpawn(sent ? "went to the other game" : "did not go to the other game");
        }
    }

    /// <summary>
    /// The states of a creature's stun control from the blow that stuns it to the end of the stun. The first two go on
    /// at once, and the other game, which hears only the latest state of an FSM in each update, hears of the stun as
    /// "Stunned".
    /// </summary>
    private static readonly HashSet<string> StunnedStates = ["Stun", "Dazed Effect", "Stunned", "Quick End"];

    /// <summary>
    /// Says in the log that the creature was stunned - its stun control went into the stun from outside it - in this
    /// game, which runs it, or in the scene host's, which the copy here follows: of a boss that the players saw never
    /// stunned in a whole fight it could not be told whether it was stunned in the scene host's game only, or nowhere
    /// (USER 10-19). In this game, with how much of a beating it had taken, where that is read in "Stun", before that
    /// state clears it.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="fromState">The state it was in.</param>
    /// <param name="toState">The state it went to.</param>
    /// <param name="here">Whether this game runs the creature.</param>
    private void SayStunned(PlayMakerFSM fsm, string fromState, string toState, bool here) {
        if (fsm.FsmName != "Stun Control" || !StunnedStates.Contains(toState) || StunnedStates.Contains(fromState)) {
            return;
        }

        SayWhereTheDazeRingIsSoon(here ? fsm : CopyOf(fsm), here);

        if (!here) {
            SSMP.Logging.Logger.Info(
                $"[Stun] '{Object.Host.name}' ({Id}) is stunned in the scene host's game, which its copy here follows"
            );
            return;
        }

        if (toState != "Stun") {
            SSMP.Logging.Logger.Info(
                $"[Stun] '{Object.Host.name}' ({Id}) is stunned in this game, which runs it, seen first in " +
                $"'{toState}', after the stun had cleared how much of a beating it had taken"
            );
            return;
        }

        var hits = fsm.FsmVariables.FindFsmFloat("Hits Total");
        var combo = fsm.FsmVariables.FindFsmFloat("Combo Counter");
        SSMP.Logging.Logger.Info(
            $"[Stun] '{Object.Host.name}' ({Id}) is stunned in this game, which runs it, after " +
            $"{(hits == null ? "?" : hits.Value.ToString("0.#"))} of the beating that stuns it, " +
            $"{(combo == null ? "?" : combo.Value.ToString("0.#"))} of it in a row"
        );
    }

    /// <summary>
    /// How long into a stun <see cref="SayWhereTheDazeRingIsSoon"/> looks at the ring over the creature's head.
    /// </summary>
    private const float DazeRingLookTime = 0.5f;

    /// <summary>
    /// What became of the last spawn of the ring that a stun puts over the creature's head, in this game, and when (see
    /// <see cref="NoteDazeRingSpawn"/>).
    /// </summary>
    private string _dazeRingSpawnNote = "";

    /// <inheritdoc cref="_dazeRingSpawnNote"/>
    private float _dazeRingSpawnAt = float.NegativeInfinity;

    /// <summary>
    /// Whether an action spawns the ring that a stun puts over a creature's head: the stun control's spawn that keeps
    /// what it spawns in "DazedEffect".
    /// </summary>
    /// <param name="action">The action.</param>
    private static bool IsDazeRingSpawn(FsmStateAction action) {
        return action is SpawnObjectFromGlobalPool { storeObject.Name: "DazedEffect" };
    }

    /// <summary>
    /// Notes what became of a spawn of the ring that a stun puts over the creature's head, for the line of
    /// <see cref="SayWhereTheDazeRingIsSoon"/>: in the game that runs the creature whether it went to the other game,
    /// in the other one whether it came and what it made. The copy of a boss had no ring 0.5 s into each of its stuns,
    /// and nothing said whether the spawn of it ever came (USER 2026-10-21: "晕倒光圈没有在日志里有什么线索吗").
    /// </summary>
    /// <param name="note">What became of it.</param>
    private void NoteDazeRingSpawn(string note) {
        _dazeRingSpawnNote = note;
        _dazeRingSpawnAt = Time.unscaledTime;
    }

    /// <summary>
    /// Notes what the spawn of the ring that the scene host sent made here, and whether it was kept in the variable
    /// that the stun control reads it from.
    /// </summary>
    /// <param name="spawn">The copy's own spawn action, which the scene host's was played on.</param>
    private void NoteDazeRingSpawnMade(SpawnObjectFromGlobalPool spawn) {
        var made = spawn.storeObject?.Value;
        if (made == null) {
            NoteDazeRingSpawn("came from the scene host and made nothing here");
            return;
        }

        var read = spawn.Fsm?.Variables?.FindFsmGameObject("DazedEffect");
        NoteDazeRingSpawn(
            $"came from the scene host and made '{made.name}' here, kept where the stun control reads it: " +
            $"{ReferenceEquals(read, spawn.storeObject)}"
        );
    }

    /// <summary>
    /// Says for the log what became of the last spawn of the ring in this game (see <see cref="NoteDazeRingSpawn"/>).
    /// </summary>
    private string DescribeDazeRingSpawn() {
        return float.IsNegativeInfinity(_dazeRingSpawnAt)
            ? "no spawn of the ring was seen in this game yet"
            : $"the last spawn of the ring {_dazeRingSpawnNote}, {Time.unscaledTime - _dazeRingSpawnAt:0.00} s before";
    }

    /// <summary>
    /// Says in the log, a moment into a stun, how the ring that the stun puts over the creature's head stands in this
    /// game: whether there is one, whether it is on and drawn, what it hangs on and where, and how far its own FSM has
    /// got, which shows it only once it has settled - at a depth near nought, or under something that does not fall.
    /// A player saw no ring over a boss that the other game ran, stunned nine times, and nothing in either log could
    /// tell where it went missing (USER 2026-10-20: "我看不到…眩晕的那个头上的特效").
    /// </summary>
    /// <param name="stunControl">The stun control of the creature that this game shows, or null.</param>
    /// <param name="here">Whether this game runs the creature.</param>
    private void SayWhereTheDazeRingIsSoon(PlayMakerFSM? stunControl, bool here) {
        if (stunControl == null || stunControl.FsmVariables.FindFsmGameObject("DazedEffect") is not { } ringVariable) {
            return;
        }

        var creature = Object.Host.name;
        var where = here ? "in this game, which runs it" : "on its copy here";
        MonoBehaviourUtil.Instance.StartCoroutine(LookLater());

        System.Collections.IEnumerator LookLater() {
            yield return new WaitForSeconds(DazeRingLookTime);

            // Gone with its room
            if (stunControl == null) {
                yield break;
            }

            var stunState = here ? stunControl.ActiveStateName : EntityFsmActions.HostStateOf(stunControl.Fsm);
            var ring = ringVariable.Value;
            if (ring == null) {
                SSMP.Logging.Logger.Info(
                    $"[Stun] '{creature}' ({Id}) has no ring over its head {where} {DazeRingLookTime} s into the " +
                    $"stun, with its stun control in '{stunState}'; {DescribeDazeRingSpawn()}"
                );
                yield break;
            }

            var ringFsm = ring.GetComponent<PlayMakerFSM>();
            var parent = ring.transform.parent;
            var offset = ring.transform.position - stunControl.transform.position;
            var renderer = ring.GetComponent<Renderer>();
            var sparkling = ring.GetComponentsInChildren<ParticleSystem>().Count(particles => particles.isPlaying);
            var ringFsmName = ringFsm == null ? "-" : ringFsm.FsmName;
            var ringState = ringFsm == null ? "-" : ringFsm.ActiveStateName;
            var readParent = ringFsm == null ? null : ringFsm.FsmVariables.FindFsmGameObject("Parent")?.Value;
            var readGravity = ringFsm == null ? null : ringFsm.FsmVariables.FindFsmFloat("Parent Gravity");
            SSMP.Logging.Logger.Info(
                $"[Stun] '{creature}' ({Id})'s ring over its head {where}, {DazeRingLookTime} s into the stun with " +
                $"its stun control in '{stunState}': '{ring.name}', on: {ring.activeInHierarchy}, drawn: " +
                $"{(renderer == null ? "-" : renderer.enabled.ToString())}, {sparkling} part(s) sparkling, under " +
                $"'{(parent == null ? "nothing" : parent.name)}', {offset.x:0.00}, {offset.y:0.00} from the creature " +
                $"at depth {ring.transform.position.z:0.0000}; its own FSM '{ringFsmName}' in '{ringState}', " +
                $"having read what it hangs on as '{(readParent == null ? "nothing" : readParent.name)}', whose " +
                $"gravity it took as {(readGravity == null ? "-" : readGravity.Value.ToString("0.##"))}; " +
                DescribeDazeRingSpawn()
            );
        }
    }

    /// <summary>
    /// The FSM of the copy that stands for an FSM of the room's own creature, or null.
    /// </summary>
    /// <param name="fsm">The FSM of the room's own creature.</param>
    private PlayMakerFSM? CopyOf(PlayMakerFSM fsm) {
        var index = _fsms.Host.IndexOf(fsm);
        return _fsms.Client != null && index >= 0 && index < _fsms.Client.Count ? _fsms.Client[index] : null;
    }

    /// <summary>
    /// Sends the state that an FSM of the host is in, if it is not the one sent last.
    /// </summary>
    /// <param name="fsmIndex">The index of the FSM.</param>
    private void SendStateChange(int fsmIndex) {
        SendHeldFromAnEarlierFrame();

        var fsm = _fsms.Host[fsmIndex];
        var snapshot = _fsmSnapshots[fsmIndex];
        // An action that works as its state is left (FsmActionHooks) runs while the FSM is in no state at all
        if (fsm.ActiveStateName == snapshot.CurrentState || fsm.Fsm.ActiveState == null) {
            return;
        }

        // The boss fell into the part of its FSM that each game runs by itself, in this game that runs it
        if (EntityRegistry.TryGetEachGameFrom(Type, fsm.FsmName, out var eachGameFrom) &&
            eachGameFrom == fsm.ActiveStateName) {
            EachGamePartBegan?.Invoke();
        }

        var data = ObjectPool<EntityHostFsmData>.Get();
        SayStunned(fsm, snapshot.CurrentState, fsm.ActiveStateName, true);
        snapshot.CurrentState = fsm.ActiveStateName;
        data.Types.Add(EntityHostFsmData.Type.State);
        data.CurrentState = (byte) Array.IndexOf(fsm.FsmStates, fsm.Fsm.ActiveState);

        // A move that the copy in the other game plays by itself starts there from what the FSM holds and where the
        // creature is as it goes into it (RunMoveHere), which the states before it worked out, so both go with the
        // state. Sent at once here, the state went ahead of them, which went with the next look at the entity, in the
        // update after: a boss that the lava had moved to the floor it would come up through showed it, in the other
        // game, by the floor where it had gone in (USER 2026-10-20: "怪物从岩浆里面出来的先行特效和真实出来的位置不一致").
        if (IsInAMoveTheCopyPlays(fsm)) {
            AddVariableChanges(fsm, snapshot, data);
            SendPositionWithTheMove();
        }

        _netClient.UpdateManager.AddEntityHostFsmData(Id, (byte) fsmIndex, data);
        ObjectPool<EntityHostFsmData>.Return(data);
    }

    /// <summary>
    /// Whether the state an FSM of the host is in now belongs to a move that the copy in the other game plays by
    /// itself (see <see cref="EntityRegistryEntry.RunsHere"/>).
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    private bool IsInAMoveTheCopyPlays(PlayMakerFSM fsm) {
        if (!EntityRegistry.TryGetRunsHere(Type, fsm.FsmName, out var moves)) {
            return false;
        }

        var stateName = fsm.ActiveStateName;
        foreach (var move in moves.Values) {
            if (move.Contains(stateName)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sends where the creature is and the way it faces, as a state of its FSM goes that a move the copy plays by
    /// itself is in (see <see cref="SendStateChange"/>), or before what showed the creature again (see
    /// <see cref="SendHeldOncePlaced"/>), the same as the next look at the entity would (see <see cref="OnUpdate"/>),
    /// which then finds nothing new to send. Not for a part of another entity, while the
    /// entity moves by itself or while what the other player did to it is being answered: then the next look sends it
    /// with all else that it takes.
    /// </summary>
    private void SendPositionWithTheMove() {
        if (_hasParent || _stamps.Count > 0 || HostMovesByItself() || Object.Host == null) {
            return;
        }

        var transform = Object.Host.transform;
        var newPosition = transform.position;
        if (newPosition == _lastPosition) {
            return;
        }

        _positionChangedAt = Time.unscaledTime;
        _positionSentAgain = 0;
        _lastPosition = newPosition;
        _netClient.UpdateManager.UpdateEntityPosition(
            Id,
            new Math_Vector3(newPosition.x, newPosition.y, newPosition.z)
        );

        var newScale = transform.lossyScale;
        _netClient.UpdateManager.UpdateEntityScale(
            Id,
            new EntityUpdate.ScaleData {
                origin = true,
                x = true,
                y = true,
                xFlipped = true,
                yFlipped = true,
                xScale = newScale.x,
                yScale = newScale.y
            }
        );
    }

    /// <summary>
    /// Adds to what goes of an FSM of the host the values of its variables that changed since they last went, which
    /// it notes as gone.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="snapshot">What last went of it.</param>
    /// <param name="data">What goes of it now.</param>
    private static void AddVariableChanges(PlayMakerFSM fsm, FsmSnapshot snapshot, EntityHostFsmData data) {
        var variables = fsm.FsmVariables;

        var changed = false;
        for (byte i = 0; i < variables.FloatVariables.Length; i++) {
            var val = variables.FloatVariables[i].Value;
            if (val != snapshot.Floats[i]) {
                changed = true;
                snapshot.Floats[i] = val;
                data.Floats[i] = val;
            }
        }

        if (changed) {
            data.Types.Add(EntityHostFsmData.Type.Floats);
        }

        changed = false;
        for (byte i = 0; i < variables.IntVariables.Length; i++) {
            var val = variables.IntVariables[i].Value;
            if (val != snapshot.Ints[i]) {
                changed = true;
                snapshot.Ints[i] = val;
                data.Ints[i] = val;
            }
        }

        if (changed) {
            data.Types.Add(EntityHostFsmData.Type.Ints);
        }

        changed = false;
        for (byte i = 0; i < variables.BoolVariables.Length; i++) {
            var val = variables.BoolVariables[i].Value;
            if (val != snapshot.Bools[i]) {
                changed = true;
                snapshot.Bools[i] = val;
                data.Bools[i] = val;
            }
        }

        if (changed) {
            data.Types.Add(EntityHostFsmData.Type.Bools);
        }

        changed = false;
        for (byte i = 0; i < variables.StringVariables.Length; i++) {
            var val = variables.StringVariables[i].Value;
            if (snapshot.Strings[i] != null && val != snapshot.Strings[i]) {
                changed = true;
                snapshot.Strings[i] = val;
                data.Strings[i] = val;
            }
        }

        if (changed) {
            data.Types.Add(EntityHostFsmData.Type.Strings);
        }

        changed = false;
        for (byte i = 0; i < variables.Vector2Variables.Length; i++) {
            var val = variables.Vector2Variables[i].Value;
            if (val != snapshot.Vector2s[i]) {
                changed = true;
                snapshot.Vector2s[i] = val;
                data.Vec2s[i] = (Math_Vector2) val;
            }
        }

        if (changed) {
            data.Types.Add(EntityHostFsmData.Type.Vector2s);
        }

        changed = false;
        for (byte i = 0; i < variables.Vector3Variables.Length; i++) {
            var val = variables.Vector3Variables[i].Value;
            if (val != snapshot.Vector3s[i]) {
                changed = true;
                snapshot.Vector3s[i] = val;
                data.Vec3s[i] = (SSMP.Math.Vector3) val;
            }
        }

        if (changed) {
            data.Types.Add(EntityHostFsmData.Type.Vector3s);
        }
    }

    /// <summary>
    /// Puts the room's own copy of this entity back to sleep if something has switched it on while the other game is
    /// the one running it.
    ///
    /// That copy never moves - nothing runs it - so it stands exactly where the room left it, which is where the
    /// creature was when the room loaded. Switched on for even one frame, what a player sees is the creature
    /// appearing at the place it started from and vanishing again. Two players have reported that on a long list of
    /// creatures with nothing in common, which is what it looks like: it has nothing to do with the creature.
    ///
    /// Called both in the ordinary update and in the late one. The ordinary one is not enough on its own: whatever
    /// switches it on can run after that and still have the whole rest of the frame to be drawn in. Nothing runs
    /// after the late one.
    /// </summary>
    private void HideTheRoomsOwnCopy() {
        if (_keepsRunning || Object.Host == null || !Object.Host.activeSelf) {
            return;
        }

        if (!_isSceneHostDetermined) {
            _originalIsActive = true;
        }

        // Said out loud, throttled, because nothing else can tell afterwards whether a creature that appeared where
        // it started was this or something else entirely
        if (Time.unscaledTime >= _nextHostActiveLogTime) {
            _nextHostActiveLogTime = Time.unscaledTime + HostActiveLogInterval;

            SSMP.Logging.Logger.Info(
                $"The room's own '{Object.Host.name}' switched itself back on while the other game is running " +
                $"it{(_isSceneHostDetermined ? "" : ", before it was settled who runs the room")}, putting it back " +
                "to sleep"
            );
        }

        RememberWhereTheHostFsmsAre();
        AskTheRoomsOwnCopyToSayWhoWakesIt();
        Object.Host.SetActive(false);
    }

    /// <summary>
    /// Puts a listener on the room's own copy that says, once, what switched it back on.
    ///
    /// The action PlayMaker uses to switch an object on is already turned away while the other game runs a creature,
    /// so whatever is doing this reaches the object another way, and by the time this mod notices it the frame is
    /// over and there is nothing left to look at. A listener on the object itself runs inside the call that wakes it,
    /// so the one place that can name the caller is there.
    /// </summary>
    private void AskTheRoomsOwnCopyToSayWhoWakesIt() {
        try {
            if (Object.Host.GetComponent<SaysWhoWakesIt>() != null) {
                return;
            }

            // Adding it wakes it up in the sense Unity means, so it is armed only afterwards: the one line it is
            // allowed should be about whoever wakes the creature, not about this
            Object.Host.AddComponent<SaysWhoWakesIt>().Armed = true;
        } catch (Exception e) {
            SSMP.Logging.Logger.Warn($"Could not ask '{Object.Host.name}' to say who wakes it: {e.Message}");
        }
    }

    /// <summary>
    /// Says once, from inside the call that does it, what switched this object back on.
    /// </summary>
    private sealed class SaysWhoWakesIt : MonoBehaviour {
        /// <summary>
        /// Whether this has anything to say yet. False until the object it was put on has finished being added to.
        /// </summary>
        public bool Armed;

        private void OnEnable() {
            if (!Armed) {
                return;
            }

            Armed = false;

            SSMP.Logging.Logger.Info(
                $"The room's own '{name}' was switched back on by:\n{new System.Diagnostics.StackTrace(1, true)}"
            );
        }
    }

    /// <summary>
    /// Says that the copy is off here although the scene host has the creature on, and what switched it off.
    ///
    /// Only the scene host's word switches a copy on or off, so nothing ever switches one back on that was switched
    /// off here some other way while the creature went on in the other game: its player saw it and this one did not,
    /// for the rest of the visit. Nothing else can tell afterwards what did it; a creature that dies here is left out,
    /// since its death is played out here and the scene host's word follows. Said once for each time the scene host
    /// says the creature is on, and only for a copy that stays off longer than <see cref="CopyOffGrace"/>.
    /// </summary>
    private void SayWhyTheCopyIsOff() {
        if (!_sceneHostHasItOn || _saidTheCopyIsOff || Object.Client == null) {
            return;
        }

        // A part of another entity is on when it is switched on itself, as the scene host tells it (OnUpdate)
        if (_hasParent ? Object.Client.activeSelf : Object.Client.activeInHierarchy) {
            _copyOffSince = -1f;
            return;
        }

        var healthManager = Object.Client.GetComponent<HealthManager>();
        if (healthManager != null && healthManager.GetIsDead()) {
            _copyOffSince = -1f;
            return;
        }

        var now = Time.unscaledTime;
        if (_copyOffSince < 0f) {
            _copyOffSince = now;
            return;
        }

        if (now - _copyOffSince < CopyOffGrace) {
            return;
        }

        _saidTheCopyIsOff = true;

        var parent = Object.Client.transform.parent;
        var caller = Object.Client.TryGetComponent<SaysWhoSwitchesTheCopyOff>(out var says) ? says.Caller : null;
        SSMP.Logging.Logger.Info(
            $"The copy of '{Object.Client.name}' is off here although the other game has it on" + (
                Object.Client.activeSelf && parent != null ? $": '{parent.name}', which it was put under, is off"
                : caller != null ? $", switched off by:\n{caller}"
                : ""
            )
        );
    }

    /// <summary>
    /// Says what the scene host last told this game about whether the creature is on, and what last switched the copy
    /// off here while the scene host had it on, for a state check that finds the copy off here and the creature on in
    /// the other game. The two tell apart a word that never came, or came in the wrong order, from something here.
    /// </summary>
    public string SayWhatItWasToldAboutBeingOn() {
        var ago = (Time.unscaledTime - _lastToldIfOn).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        var told = _timesToldIfOn == 0
            ? "the other game never said here whether it is on"
            : $"the other game last said it is {(_sceneHostHasItOn ? "on" : "off")}, {ago} s ago ({_timesToldIfOn} in all)";
        var caller = Object.Client != null && Object.Client.TryGetComponent<SaysWhoSwitchesTheCopyOff>(out var says)
            ? says.Caller
            : null;
        return caller == null ? told : $"{told}; last switched off here while it was on by:\n{caller}";
    }

    /// <summary>
    /// Remembers what switched a copy off while the scene host had it on (see <see cref="SayWhyTheCopyIsOff"/>), from
    /// inside the call that does it, which is the one place that can name the caller.
    /// </summary>
    private sealed class SaysWhoSwitchesTheCopyOff : MonoBehaviour {
        /// <summary>
        /// The entity whose copy this is on.
        /// </summary>
        public Entity? Entity;

        /// <summary>
        /// The calls that switched the copy off, the last time it happened while the scene host had it on, since the
        /// scene host last said whether it is on.
        /// </summary>
        public string? Caller;

        private void OnDisable() {
            if (Entity is { _sceneHostHasItOn: true, _isControlled: true, _saidTheCopyIsOff: false }) {
                Caller = new System.Diagnostics.StackTrace(1, false).ToString();
            }
        }
    }

    /// <summary>
    /// Writes down where each host FSM is standing, just before this mod switches the object off.
    /// PlayMaker starts an FSM over from the beginning when its object comes back on, and an FSM's variables survive
    /// that while its place does not. A creature that decided once and for all what it was - hiding itself and putting
    /// the answer in a variable so that it would not decide twice - reads that answer again after being started over,
    /// takes the other road this time, and walks off with the body it had already put away and that nothing else will
    /// ever give back.
    /// </summary>
    private void RememberWhereTheHostFsmsAre() {
        if (_fsms.Host.Count == 0) {
            return;
        }

        _statesWhenWeSwitchedOff ??= new string?[_fsms.Host.Count];

        for (var fsmIndex = 0; fsmIndex < _fsms.Host.Count && fsmIndex < _statesWhenWeSwitchedOff.Length; fsmIndex++) {
            var fsm = _fsms.Host[fsmIndex];
            if (fsm == null || !fsm.Fsm.Started || string.IsNullOrEmpty(fsm.ActiveStateName)) {
                continue;
            }

            _statesWhenWeSwitchedOff[fsmIndex] = fsm.ActiveStateName;
        }
    }

    /// <summary>
    /// Callback method for the last thing that happens before a frame is drawn.
    /// </summary>
    private void OnLateUpdate() {
        if (!_made) {
            return;
        }

        if (_isControlled) {
            HideTheRoomsOwnCopy();

            // Nothing of the scene host's goes out from a game that no longer runs the room
            _heldUntilPlaced = null;
            return;
        }

        SendHeldOncePlaced();
    }

    /// <summary>
    /// Whether an action of the creature's FSM switches on the drawing of the creature, or of a part of it that goes
    /// where it goes. Not for a part of another entity, whose place goes nowhere: the copy of that carries it.
    /// </summary>
    private bool DrawsItselfOn(FsmStateAction action) {
        return !_hasParent && Object.Host != null && action is SetMeshRenderer { active.Value: true } draw &&
               draw.Fsm.GetOwnerDefaultTarget(draw.gameObject) is var drawn && drawn != null &&
               drawn.transform.IsChildOf(Object.Host.transform);
    }

    /// <summary>
    /// Sends data of the entity in this update: a replay, an echo, or what one of its parts sends. While replays are
    /// held back (see <see cref="_heldUntilPlaced"/>) it goes behind them, in the order it came: an echo that went
    /// ahead of the replays of the input it answers had the other game play its reaction a second time, and a death
    /// ahead of the replay of the death that its FSM plays left the body where it fell.
    /// </summary>
    private void SendEntityData(EntityNetworkData data) {
        if (_heldUntilPlaced != null) {
            _heldUntilPlaced.Add(data);
            return;
        }

        _netClient.UpdateManager.AddEntityData(Id, data);
    }

    /// <summary>
    /// Sends what was held back in an earlier frame (see <see cref="_heldUntilPlaced"/>) before anything of this one.
    /// A state entered after the entity's look at the end of a frame, as some are when an animation ends, holds what
    /// it shows into the next frame, where a state the FSM went on to would otherwise go out ahead of it, and the
    /// other game end what it began at once.
    /// </summary>
    private void SendHeldFromAnEarlierFrame() {
        if (_heldUntilPlaced != null && _heldSince != Time.frameCount) {
            SendHeldOncePlaced();
        }
    }

    /// <summary>
    /// Sends the replays of the creature held back this frame (see <see cref="_heldUntilPlaced"/>), after where the
    /// creature is now. The other game takes the place in a packet before what came with it, and the place goes no
    /// later than them, so the copy is there before it is drawn.
    /// </summary>
    private void SendHeldOncePlaced() {
        if (_heldUntilPlaced is not { } held) {
            return;
        }

        _heldUntilPlaced = null;
        SendPositionWithTheMove();
        foreach (var data in held) {
            _netClient.UpdateManager.AddEntityData(Id, data);
        }
    }

    /// <summary>
    /// Callback method for handling updates.
    /// </summary>
    [SuppressMessage("ReSharper", "CompareOfFloatsByEqualityOperator")]
    private void OnUpdate() {
        if (!_made) {
            return;
        }

        if (_putFsmsBackWhereTheyWere) {
            _putFsmsBackWhereTheyWere = false;
            PutHostFsmsBackWhereTheyWere();
        }

        if (Object.Host == null) {
            if (_lastIsActive) {
                // If the host object was active, but now it null (or destroyed in Unity), we can send
                // to the server that the entity can be regarded as inactive

                _lastIsActive = false;

                _netClient.UpdateManager.UpdateEntityIsActive(
                    Id,
                    false
                );
            }

            return;
        }

        if (_isControlled) {
            HideTheRoomsOwnCopy();
            SayWhyTheCopyIsOff();
            UpdateRunHere();
            CheckWokenByLocalPlayer();
            CheckCaughtByCopy();

            if (Object.Client != null &&
                Object.Client.TryGetComponent<PredictiveInterpolation>(out var interpolation)) {
                interpolation.AdaptToRTT(_netClient.UpdateManager.AverageRtt);

                // While something the local player did to this entity is still on its way to the scene host and
                // back, what the local game did is what moves it, and the interpolation stays out of the way. So it
                // does while the copy moves by itself from how the entity set off (OwnMotionComponent), while its
                // own FSM runs here for something the local player did (PlayHere), or while a pull of the local
                // player's own moves it (NotePulledHere).
                var movedHere = IsAnticipating() || MovesByItself() || _runHere != null ||
                                Time.time < _pulledHereUntil;
                if (_wasMovedHere && !movedHere) {
                    // The interpolation did not write this object while that was going on, so it carried on
                    // predicting from where the entity stood before any of it. Picking that up again would put it back
                    // there in a single frame, which is the whole of what the wait was for, so where it is standing now
                    // is handed over as what it should go on looking like.
                    interpolation.KeepVisualPosition();
                    LastHandOver = (Time.time, -(Vector2) interpolation.VisualOffset);
                }

                _wasMovedHere = movedHere;
                if (!movedHere) {
                    interpolation.ManualUpdate(Time.deltaTime);
                }
            }

            return;
        }

        SendHeldFromAnEarlierFrame();
        UpdateLeads();
        LetThePlayerBackIn(false);
        SayIfTheCopyIsOnHere();

        foreach (var entityComponent in _updatableComponents) {
            entityComponent.OnUpdate();
        }

        // Something a scene client did to this entity has now finished happening here, so from here on what is sent
        // has the whole of it in it and may say so.
        //
        // Both halves of that wait are needed, and the second one is the one that was missing. A step of physics is
        // the least that anything set in motion needs, since almost nothing in this game moves outside one and the
        // frame that starts a knockback leaves the enemy standing exactly where it was hit. But a knockback is not a
        // shove either: the game sweeps the enemy along at a steady speed over tens of steps, and one step in it has
        // gone a twentieth of the way. Saying "I have this" there hands the other player a position from the very
        // start of a knockback that their own game finished a round trip ago, so their enemy slides most of a
        // knockback back towards them, and then out again as the rest of the sweep arrives behind it. That is the
        // darting about, and it is why this waits for the end of what was done rather than the beginning of it.
        var anticipationTaken = false;
        var stampTime = Time.unscaledTime;
        for (var i = _stamps.Count - 1; i >= 0; i--) {
            var stamp = _stamps[i];
            if (stamp.Pending != 0 && MonoBehaviourUtil.FixedStep > stamp.PendingSinceStep &&
                stampTime >= stamp.PendingSettledAt) {
                stamp.Incorporated = stamp.Pending;
                stamp.Pending = 0;
                stamp.SendsLeft = AnticipationForcedSends;
                stamp.StampUntil = stampTime + AnticipationStampTime;
            }

            // Nobody can be waiting on it any more
            if (stamp.Pending == 0 && stampTime >= stamp.StampUntil) {
                _stamps.RemoveAt(i);
                continue;
            }

            // Sent more than once, because a position travels by the way that drops what it cannot deliver and an
            // enemy that is standing still gives no second chance of its own: one forced position, lost, and the
            // player waiting on it waits out their whole timeout instead. Several in a row cost a few dozen bytes.
            anticipationTaken |= stamp.SendsLeft > 0;
        }

        var transform = Object.Host.transform;

        // A body that stops moving by itself does not always move its transform as it stops: one that the physics puts
        // to sleep where it lies is left alone, and the place it came to rest was never sent
        var movesByItself = HostMovesByItself();
        var stoppedMovingByItself = _hostMovedByItself && !movesByItself;
        _hostMovedByItself = movesByItself;

        // What changed a moment ago goes again in case it was lost (SentAgainAfter); the place not while the entity
        // moves by itself, when none is sent at all
        var positionAgain = !_hasParent && !movesByItself && IsDueAgain(_positionChangedAt, ref _positionSentAgain);
        var scaleAgain = !_hasParent && IsDueAgain(_scaleChangedAt, ref _scaleSentAgain);

        // Unity already tracks transform mutations; avoid re-reading and comparing position/scale on quiet frames. A
        // part of another entity sends neither: the copy of its parent carries it and moves it the way it moves here.
        if (!_hasParent && (transform.hasChanged || anticipationTaken || stoppedMovingByItself || positionAgain ||
                            scaleAgain)) {
            var newPosition = transform.position;
            var newScale = transform.lossyScale;

            // A position is sent even when the entity has not moved, for as long as there are forced ones left,
            // because the player waiting on it has nothing else to wait for. It is also the whole of the answer
            // when the scene host did nothing with what they sent - the knockback went nowhere, and this is where
            // the enemy really is and always was.
            //
            // None is sent while the entity moves by itself from how it set off: the other game moves the copy the
            // same way, and was told where it set off from (OwnMotionComponent).
            if ((newPosition != _lastPosition || anticipationTaken || positionAgain) && !movesByItself) {
                foreach (var stamp in _stamps) {
                    if (stamp.SendsLeft > 0) {
                        stamp.SendsLeft--;
                    }
                }

                if (newPosition != _lastPosition) {
                    _positionChangedAt = Time.unscaledTime;
                    _positionSentAgain = 0;
                }

                _lastPosition = newPosition;

                _netClient.UpdateManager.UpdateEntityPosition(
                    Id,
                    new Math_Vector3(newPosition.x, newPosition.y, newPosition.z)
                );

                // Which way the entity faces rides along with every position, as the player's own does
                // (ClientManager). Sent on its own only on the frame it changed, a lost turn left the copy facing the
                // old way for as long as the entity kept going. Only the sign of each axis, a byte, and added to a
                // change of size the packet may already carry rather than put in its place (ScaleData.TakeNewer).
                _netClient.UpdateManager.UpdateEntityScale(
                    Id,
                    new EntityUpdate.ScaleData {
                        origin = true,
                        x = true,
                        y = true,
                        xFlipped = true,
                        yFlipped = true,
                        xScale = newScale.x,
                        yScale = newScale.y
                    }
                );

                // Beside every position for a while rather than only the once, for the same reason as above, and
                // then not at all: nobody can still be waiting on it by then, since waiting gives up long before.
                // Also while this game is still at it, without a number, for how far it has pushed the entity so far:
                // the player waiting on it takes that out of the position, which leaves what the entity did by itself
                if (_stamps.Count > 0) {
                    var reports = new List<AnticipationReport>(_stamps.Count);
                    foreach (var stamp in _stamps) {
                        var number = Time.unscaledTime < stamp.StampUntil ? stamp.Incorporated : (byte) 0;
                        var pushed = stamp.Pushed;
                        reports.Add(new AnticipationReport(stamp.Tag, number, stamp.Since, pushed.x, pushed.y));
                    }

                    _netClient.UpdateManager.UpdateEntityAnticipation(Id, reports);
                }
            }

            const float epsilon = 0.0001f;

            if (newScale != _lastScale) {
                var scaleData = new EntityUpdate.ScaleData {
                    origin = true
                };

                if (newScale.x != _lastScale.x) {
                    scaleData.x = true;
                    scaleData.xScale = newScale.x;

                    if (System.Math.Abs(newScale.x - _lastScale.x * -1) < epsilon) {
                        scaleData.xFlipped = true;
                    }
                }

                if (newScale.y != _lastScale.y) {
                    scaleData.y = true;
                    scaleData.yScale = newScale.y;

                    if (System.Math.Abs(newScale.y - _lastScale.y * -1) < epsilon) {
                        scaleData.yFlipped = true;
                    }
                }

                if (newScale.z != _lastScale.z) {
                    scaleData.z = true;
                    scaleData.zScale = newScale.z;

                    if (System.Math.Abs(newScale.z - _lastScale.z * -1) < epsilon) {
                        scaleData.zFlipped = true;
                    }

                    // The way a scale is written has no way to say z without x and y (ScaleData.WriteData)
                    if (!scaleData.x) {
                        scaleData.x = true;
                        scaleData.xScale = newScale.x;
                    }

                    if (!scaleData.y) {
                        scaleData.y = true;
                        scaleData.yScale = newScale.y;
                    }
                }

                _netClient.UpdateManager.UpdateEntityScale(Id, scaleData);

                // Sent again a few times in case it was lost (SentAgainAfter), with every axis that changed since it
                // was last sent other than again: a turn and a change of size a moment apart are both in it
                if (_scaleSentAgain >= SentAgainAfter.Length) {
                    _scaleAgainX = _scaleAgainY = _scaleAgainZ = false;
                }

                _scaleAgainX |= scaleData.x;
                _scaleAgainY |= scaleData.y;
                _scaleAgainZ |= scaleData.z;
                _scaleChangedAt = Time.unscaledTime;
                _scaleSentAgain = 0;

                _lastScale = newScale;
            } else if (scaleAgain) {
                SendScaleAgain(newScale);
            }

            transform.hasChanged = false;
        }

        var newActive = _hasParent ? Object.Host.activeSelf : Object.Host.activeInHierarchy;
        if (newActive != _lastIsActive) {
            _lastIsActive = newActive;

            _netClient.UpdateManager.UpdateEntityIsActive(
                Id,
                newActive
            );
        }

        // Sync Host FSM states and variables.
        // To avoid garbage collection pressure, we first check for changes using simple, direct loops
        // (avoiding generic methods with delegates/closures) and only retrieve/populate EntityHostFsmData from the pool if a change is detected.
        for (byte fsmIndex = 0; fsmIndex < _fsms.Host.Count; fsmIndex++) {
            var fsm = _fsms.Host[fsmIndex];
            if (IsRunByEachGame(fsm)) {
                continue;
            }

            var snapshot = _fsmSnapshots[fsmIndex];

            var lastStateName = snapshot.CurrentState;
            var hasStateChange = fsm.ActiveStateName != lastStateName;

            var hasFloatsChange = false;
            for (byte i = 0; i < fsm.FsmVariables.FloatVariables.Length; i++) {
                if (fsm.FsmVariables.FloatVariables[i].Value != snapshot.Floats[i]) {
                    hasFloatsChange = true;
                    break;
                }
            }

            var hasIntsChange = false;
            for (byte i = 0; i < fsm.FsmVariables.IntVariables.Length; i++) {
                if (fsm.FsmVariables.IntVariables[i].Value != snapshot.Ints[i]) {
                    hasIntsChange = true;
                    break;
                }
            }

            var hasBoolsChange = false;
            for (byte i = 0; i < fsm.FsmVariables.BoolVariables.Length; i++) {
                if (fsm.FsmVariables.BoolVariables[i].Value != snapshot.Bools[i]) {
                    hasBoolsChange = true;
                    break;
                }
            }

            var hasStringsChange = false;
            for (byte i = 0; i < fsm.FsmVariables.StringVariables.Length; i++) {
                if (snapshot.Strings[i] != null && fsm.FsmVariables.StringVariables[i].Value != snapshot.Strings[i]) {
                    hasStringsChange = true;
                    break;
                }
            }

            var hasVector2SChange = false;
            for (byte i = 0; i < fsm.FsmVariables.Vector2Variables.Length; i++) {
                if (fsm.FsmVariables.Vector2Variables[i].Value != snapshot.Vector2s[i]) {
                    hasVector2SChange = true;
                    break;
                }
            }

            var hasVector3SChange = false;
            for (byte i = 0; i < fsm.FsmVariables.Vector3Variables.Length; i++) {
                if (fsm.FsmVariables.Vector3Variables[i].Value != snapshot.Vector3s[i]) {
                    hasVector3SChange = true;
                    break;
                }
            }

            if (hasStateChange || hasFloatsChange || hasIntsChange || hasBoolsChange || hasStringsChange ||
                hasVector2SChange || hasVector3SChange) {
                var data = ObjectPool<EntityHostFsmData>.Get();

                if (hasStateChange) {
                    SayStunned(fsm, snapshot.CurrentState, fsm.ActiveStateName, true);
                    snapshot.CurrentState = fsm.ActiveStateName;
                    data.Types.Add(EntityHostFsmData.Type.State);
                    data.CurrentState = (byte) Array.IndexOf(fsm.FsmStates, fsm.Fsm.ActiveState);
                }

                AddVariableChanges(fsm, snapshot, data);

                _netClient.UpdateManager.AddEntityHostFsmData(Id, fsmIndex, data);
                ObjectPool<EntityHostFsmData>.Return(data);
            }
        }
    }

    /// <summary>
    /// The hook on tk2dSpriteAnimator.Play that all entities share: a clip that the animator of a creature or of its
    /// copy plays goes to its entity (see <see cref="OnAnimationPlayed"/>), and any other plays as it would.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The sprite animator instance.</param>
    /// <param name="clip">The animation clip that was played.</param>
    /// <param name="clipStartTime">The start time of the animation clip.</param>
    /// <param name="overrideFps">The FPS override for the clip.</param>
    private static void OnAnyAnimationPlayed(
        Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip, float, float> orig,
        tk2dSpriteAnimator self,
        tk2dSpriteAnimationClip clip,
        float clipStartTime,
        float overrideFps
    ) {
        if (EntitiesByAnimator.TryGetValue(self, out var entity)) {
            entity.OnAnimationPlayed(orig, self, clip, clipStartTime, overrideFps);
        } else {
            orig(self, clip, clipStartTime, overrideFps);
        }
    }

    /// <summary>
    /// Callback method for when the sprite animator plays an animation.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The sprite animator instance.</param>
    /// <param name="clip">The animation clip that was played.</param>
    /// <param name="clipStartTime">The start time of the animation clip.</param>
    /// <param name="overrideFps">The FPS override for the clip.</param>
    private void OnAnimationPlayed(
        Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip, float, float> orig,
        tk2dSpriteAnimator self,
        tk2dSpriteAnimationClip clip,
        float clipStartTime,
        float overrideFps
    ) {
        if (self == _animator.Client) {
            // The copy's own FSM plays its animations while it runs here (PlayHere)
            if (_allowClientAnimation || _runHere != null) {
                orig(self, clip, clipStartTime, overrideFps);
                NotePose(clip, _copyPosePacket);

                _allowClientAnimation = false;
                _copyPosePacket = 0;
            }

            return;
        }

        orig(self, clip, clipStartTime, overrideFps);

        if (self != _animator.Host) {
            return;
        }

        if (_isControlled) {
            return;
        }

        NotePose(clip, 0);

        if (!_animationClipNameIds.TryGetValue(clip.name, out var animationId)) {
            return;
        }

        // Once, as it starts, the way that delivers everything once and in order (ReliableEntityUpdate.AnimationId)
        _netClient.UpdateManager.UpdateEntityAnimation(
            Id,
            animationId,
            (byte) clip.wrapMode
        );
    }

    /// <summary>
    /// Whether something that was sent once when it changed is due to be sent again (see <see cref="SentAgainAfter"/>),
    /// counting it if so. Past more than one of the times at once, after a long frame, it goes only the once.
    /// </summary>
    /// <param name="changedAt">When it changed and was sent, in unscaled seconds.</param>
    /// <param name="sentAgain">How many times it was sent again since.</param>
    private static bool IsDueAgain(float changedAt, ref int sentAgain) {
        if (sentAgain >= SentAgainAfter.Length || Time.unscaledTime - changedAt < SentAgainAfter[sentAgain]) {
            return false;
        }

        while (sentAgain < SentAgainAfter.Length && Time.unscaledTime - changedAt >= SentAgainAfter[sentAgain]) {
            sentAgain++;
        }

        return true;
    }

    /// <summary>
    /// Sends again the axes of this game's creature's scale that changed a moment ago (see
    /// <see cref="SentAgainAfter"/>), whole, so a copy that missed a turn or a change of size has it.
    /// </summary>
    /// <param name="scale">The scale of this game's creature now.</param>
    private void SendScaleAgain(Vector3 scale) {
        // The way a scale is written has no way to say z without x and y (ScaleData.WriteData)
        var all = _scaleAgainZ;
        if (!_scaleAgainX && !_scaleAgainY && !all) {
            return;
        }

        _netClient.UpdateManager.UpdateEntityScale(
            Id,
            new EntityUpdate.ScaleData {
                origin = true,
                x = _scaleAgainX || all,
                y = _scaleAgainY || all,
                z = all,
                xScale = scale.x,
                yScale = scale.y,
                zScale = scale.z
            }
        );
    }

    /// <summary>
    /// The hook on ObjectPool.Recycle that all entities share. Used to prevent client objects from being recycled,
    /// which shouldn't happen because they are instantiated manually instead of from a pool.
    /// </summary>
    private static void OnAnyObjectRecycled(Action<GameObject> orig, GameObject obj) {
        if (obj is not null && EntitiesByCopy.ContainsKey(obj)) {
            return;
        }

        orig(obj);
    }

    /// <summary>
    /// The hook on ActivateGameObject.OnEnter that all entities share: an action that switches the room object of an
    /// entity on or off goes to it while it keeps that from it (see <see cref="OnDoActivateGameObject"/>), and any
    /// other does what it does.
    /// </summary>
    private static void OnAnyActivateGameObject(Action<ActivateGameObject> orig, ActivateGameObject self) {
        var target = self.Fsm.GetOwnerDefaultTarget(self.gameObject);
        if (target is not null && EntitiesByRoomObject.TryGetValue(target, out var entity) &&
            entity._watchesActivation) {
            entity.OnDoActivateGameObject(orig, self);
        } else {
            orig(self);
        }
    }

    /// <summary>
    /// Callback method for when the 'active' of the host game object is changed. Used to update whether the host
    /// game object should return to what active state after the scene host is determined.
    /// </summary>
    private void OnDoActivateGameObject(
        Action<ActivateGameObject> orig,
        ActivateGameObject self
    ) {
        // If the game object in the action is not our host game object, we skip it
        if (self.Fsm.GetOwnerDefaultTarget(self.gameObject) != Object.Host || Object.Host == null) {
            orig(self);
            return;
        }

        // In the game that runs this creature, or expects to, the room switches it on and off as it always does.
        //
        // While the other game is running this creature, the room's own copy of it is held asleep every frame, and
        // anything that switches it back on only makes it flash on screen for the frame in between. It cannot be its
        // own doing - a sleeping object runs nothing - so it is something else in the room reaching over, and that is
        // turned away here rather than fought frame by frame.
        if (_keepsRunning || _isSceneHostDetermined && !_isControlled) {
            orig(self);
            return;
        }

        // Update the original active value to whatever this action will set
        // Also, we do not let this action execute any further since we do not want it to modify our host object
        // before the scene host is determined
        _originalIsActive = self.activate.Value;

        // For its FSM the action is still over, as it is once it has done what it does. Left unfinished, the state it
        // is in never ends: a room that calls a helper into its fight and then waits for the player stood calling
        // them for good, and the fight never came.
        if (!self.everyFrame) {
            self.Finish();
        }
    }

    /// <summary>
    /// Initializes the entity when the client user is the scene host.
    /// </summary>
    public void InitializeHost(uint sceneHostEpoch = 0) {
        // Nothing said under the numbering of a game that is no longer the one answering means anything here
        ResetAnticipation();

        // A room's own object that was left running needs none of this: it is where it is, doing what it does - and it
        // may even be gone by now, since some take themselves away the moment they start.
        if (_keepsRunning) {
            _keepsRunning = false;
        } else {
            // Switching the object back on makes PlayMaker start its FSMs over there and then, so anything they
            // had already done is done again. Where that moves the creature is undone at once, since in a game
            // where nobody switched it off it never moved: one that moves itself out of the room to wait for its
            // wave had moved out as far again, so its wave brought it back only half the way and the battle
            // waited on it for good.
            var position = Object.Host.transform.localPosition;
            Object.Host.SetActive(_originalIsActive);
            Object.Host.transform.localPosition = position;

            // Where the FSMs had got to is put back on the next turn (PutHostFsmsBackWhereTheyWere)
            _putFsmsBackWhereTheyWere = true;
        }

        // Also update the last active variable to account for this potential change
        // Otherwise we might trigger the update sending of activity twice
        _lastIsActive = Object.Host != null && (_hasParent ? Object.Host.activeSelf : Object.Host.activeInHierarchy);

        _netClient.UpdateManager.UpdateEntityIsActive(Id, _lastIsActive);

        _isControlled = false;
        _isSceneHostDetermined = true;
        SaveWhereItRuns(copyRuns: false);

        foreach (var component in _components.Values) {
            component.IsControlled = false;
            component.InitializeHost(sceneHostEpoch);
        }

        // The room switches the host object on and off from now on (OnAnyActivateGameObject)
        _watchesActivation = false;
    }

    /// <summary>
    /// Puts what this game's creature is now into the state of the room this game runs, which the server asked for
    /// after a stretch in which nothing of this game's reached it (ClientManager.OnRoomResendRequest): whether it is on,
    /// where it stands and which way it faces, the clip it shows, its health, and the state and variables of its FSMs.
    /// A copy here of a creature that the other game runs is not this game's to say.
    /// </summary>
    /// <param name="snapshot">The state of the room.</param>
    internal void AddToRoomSnapshot(ClientPlayerAlreadyInScene snapshot) {
        if (_isControlled) {
            return;
        }

        var update = new EntityUpdate { Id = Id };
        var reliable = new ReliableEntityUpdate { Id = Id };

        var host = Object.Host;
        reliable.UpdateTypes.Add(EntityUpdateType.Active);
        reliable.IsActive = host != null && (_hasParent ? host.activeSelf : host.activeInHierarchy);

        if (host != null) {
            // A part of another entity is carried by the copy of its parent, as when it moves (OnUpdate); and a body
            // that moves by itself is moved the same way by its copy, which was told where it set off from
            // (OwnMotionComponent), so where it is now is not sent either
            if (!_hasParent) {
                if (!HostMovesByItself()) {
                    var position = host.transform.position;
                    update.UpdateTypes.Add(EntityUpdateType.Position);
                    update.Position = new Math_Vector3(position.x, position.y, position.z);
                }

                var scale = host.transform.lossyScale;
                update.UpdateTypes.Add(EntityUpdateType.Scale);
                update.Scale = new EntityUpdate.ScaleData {
                    origin = true,
                    x = true,
                    y = true,
                    xScale = scale.x,
                    yScale = scale.y
                };
            }

            if (_animator.Host != null && _animator.Host.CurrentClip is { } clip &&
                _animationClipNameIds.TryGetValue(clip.name, out var animationId)) {
                update.UpdateTypes.Add(EntityUpdateType.Animation);
                update.AnimationId = animationId;
                update.AnimationWrapMode = (byte) clip.wrapMode;
            }
        }

        foreach (var component in _components.Values.Distinct()) {
            component.AddToRoomSnapshot(reliable.GenericData);
        }

        for (var fsmIndex = 0; fsmIndex < _fsms.Host.Count && fsmIndex < byte.MaxValue; fsmIndex++) {
            var fsm = _fsms.Host[fsmIndex];
            if (fsm != null && !IsRunByEachGame(fsm)) {
                reliable.HostFsmData[(byte) fsmIndex] = WholeFsmData(fsm);
            }
        }

        if (reliable.GenericData.Count > 0) {
            reliable.UpdateTypes.Add(EntityUpdateType.Data);
        }

        if (reliable.HostFsmData.Count > 0) {
            reliable.UpdateTypes.Add(EntityUpdateType.HostFsm);
        }

        if (update.UpdateTypes.Count > 0) {
            snapshot.EntityUpdateList.Add(update);
        }

        snapshot.ReliableEntityUpdateList.Add(reliable);
    }

    /// <summary>
    /// The whole of what an FSM of this game's creature holds now: its state, and every variable of the kinds that are
    /// sent as they change (see <see cref="OnUpdate"/>).
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    private static EntityHostFsmData WholeFsmData(PlayMakerFSM fsm) {
        var data = new EntityHostFsmData();
        data.Types.Add(EntityHostFsmData.Type.State);
        data.CurrentState = (byte) Array.IndexOf(fsm.FsmStates, fsm.Fsm.ActiveState);

        var variables = fsm.FsmVariables;
        for (var i = 0; i < variables.FloatVariables.Length && i < byte.MaxValue; i++) {
            data.Floats[(byte) i] = variables.FloatVariables[i].Value;
        }

        for (var i = 0; i < variables.IntVariables.Length && i < byte.MaxValue; i++) {
            data.Ints[(byte) i] = variables.IntVariables[i].Value;
        }

        for (var i = 0; i < variables.BoolVariables.Length && i < byte.MaxValue; i++) {
            data.Bools[(byte) i] = variables.BoolVariables[i].Value;
        }

        for (var i = 0; i < variables.StringVariables.Length && i < byte.MaxValue; i++) {
            if (variables.StringVariables[i].Value is { } text) {
                data.Strings[(byte) i] = text;
            }
        }

        for (var i = 0; i < variables.Vector2Variables.Length && i < byte.MaxValue; i++) {
            data.Vec2s[(byte) i] = (Math_Vector2) variables.Vector2Variables[i].Value;
        }

        for (var i = 0; i < variables.Vector3Variables.Length && i < byte.MaxValue; i++) {
            data.Vec3s[(byte) i] = (Math_Vector3) variables.Vector3Variables[i].Value;
        }

        if (data.Floats.Count > 0) {
            data.Types.Add(EntityHostFsmData.Type.Floats);
        }

        if (data.Ints.Count > 0) {
            data.Types.Add(EntityHostFsmData.Type.Ints);
        }

        if (data.Bools.Count > 0) {
            data.Types.Add(EntityHostFsmData.Type.Bools);
        }

        if (data.Strings.Count > 0) {
            data.Types.Add(EntityHostFsmData.Type.Strings);
        }

        if (data.Vec2s.Count > 0) {
            data.Types.Add(EntityHostFsmData.Type.Vector2s);
        }

        if (data.Vec3s.Count > 0) {
            data.Types.Add(EntityHostFsmData.Type.Vector3s);
        }

        return data;
    }

    /// <summary>
    /// Has the components of this game's creature say again what they are now, as if it had all just changed, and how
    /// it moves by itself: what they said while nothing of this game's reached the server may never have come.
    /// </summary>
    internal void SendStateAgain() {
        if (_isControlled) {
            return;
        }

        foreach (var component in _components.Values.Distinct()) {
            component.SendAgain();
        }

        SendOwnMotionAgain();
    }

    /// <summary>
    /// Sends the clip that the room's own creature shows now, for the other game's copy and for whoever walks in later.
    /// A clip played before this game was settled to run the room was never sent: the sprite animator only says what it
    /// plays at the moment it plays it, and nothing goes out before then. One that lies in wait plays its waiting clip
    /// once as the room loads and holds it until it is woken, so the other game's copy showed the picture the room was
    /// saved with instead: a creature that should have been hidden in the ground stood out in the open in the middle
    /// of an attack (USER 10-09). It is sent once when the room is settled to be run here, like whether the creature is
    /// on - not for one that is taken in later, whose clips all come after, and which may be an old one from a pool
    /// still showing the clip it ended its last life with.
    /// </summary>
    internal void SendShownClip() {
        if (_animator.Host == null || _animator.Host.CurrentClip is not { } clip ||
            !_animationClipNameIds.TryGetValue(clip.name, out var animationId)) {
            return;
        }

        _netClient.UpdateManager.UpdateEntityAnimation(Id, animationId, (byte) clip.wrapMode);
    }

    /// <summary>
    /// Tells a copy that came after the room's other creatures the count of who runs the room, which it was not set up
    /// with (EntityProcessor): its health is told in it (HealthManagerComponent).
    /// </summary>
    internal void SetSceneHostEpoch(uint sceneHostEpoch) {
        if (_components.TryGetValue(EntityComponentType.Health, out var component) &&
            component is HealthManagerComponent health) {
            health.SetSceneHostEpoch(sceneHostEpoch);
        }
    }

    /// <summary>
    /// Initializes the entity when the client user is a scene client. Only sets a variable to indicate the scene
    /// host has been determined.
    /// </summary>
    public void InitializeClient(uint sceneHostEpoch = 0) {
        ResetAnticipation();
        ForgetPosePackets();

        // This game expected to run the room and left the room's own object running, but the other game runs it
        // after all: it is put to sleep now, as it would have been from the start
        if (_keepsRunning) {
            _keepsRunning = false;
            if (Object.Host != null) {
                RememberWhereTheHostFsmsAre();
                Object.Host.SetActive(false);
            }
        }

        _isSceneHostDetermined = true;
        SaveWhereItRuns(copyRuns: true);

        // The hook stays while the other game runs this creature: it is what keeps the room's own copy from being
        // switched on behind our back and flashing on screen. It goes when this game takes the creature over.
        foreach (var component in _components.Values) {
            component.InitializeClient(sceneHostEpoch);
        }
    }

    /// <summary>
    /// Puts the host FSMs back where they were when this game took the entity in, for the ones that had already
    /// moved on by then.
    /// This mod switches every entity off while a room settles which game runs it, and switching a GameObject back
    /// on makes PlayMaker start its FSMs over from the beginning. For an FSM that had not done anything yet that
    /// costs nothing. For one that had, it costs everything: a creature that lies in wait puts its own look, its
    /// own collider and its own weight away first, and the only state that gives them back is the one it reaches by
    /// coming out. Started over, it never passes that state again, and spends the rest of the room walking around
    /// as something no player can see, touch or kill - while still being counted.
    /// Only the states that were left behind are put back, and as in a handover, only the actions that are meant to
    /// run again are run.
    /// </summary>
    private void PutHostFsmsBackWhereTheyWere() {
        if (Object.Host == null || !Object.Host.activeInHierarchy) {
            return;
        }

        // Only a creature whose body was put away and left that way is put back. One that can still be seen and hit
        // is none of this mod's business, however far its FSM was started over: it can go on living its own life.
        var seen = Object.Host.GetComponent<Renderer>();
        var touched = Object.Host.GetComponent<Collider2D>();
        if ((seen == null || seen.enabled) && (touched == null || touched.enabled)) {
            return;
        }

        for (var fsmIndex = 0; fsmIndex < _fsms.Host.Count && fsmIndex < _fsmSnapshots.Count; fsmIndex++) {
            var fsm = _fsms.Host[fsmIndex];
            if (fsm == null) {
                continue;
            }

            // Where it stood when we switched it off comes first: the snapshot is taken as the room is taken in,
            // which for a creature whose FSMs had not started yet says nothing at all
            var wanted = _statesWhenWeSwitchedOff != null && fsmIndex < _statesWhenWeSwitchedOff.Length
                ? _statesWhenWeSwitchedOff[fsmIndex] ?? _fsmSnapshots[fsmIndex].CurrentState
                : _fsmSnapshots[fsmIndex].CurrentState;

            if (string.IsNullOrEmpty(wanted) || wanted == fsm.ActiveStateName || wanted == fsm.Fsm.StartState) {
                continue;
            }

            var state = fsm.GetStateOrNull(wanted);
            if (state == null) {
                continue;
            }

            SSMP.Logging.Logger.Info(
                $"'{Object.Host.name}' had its FSM '{fsm.FsmName}' at '{wanted}' before this game took the room in, " +
                $"and starting it over left it at '{fsm.ActiveStateName}'; putting it back"
            );

            ResumeState(fsm, state);
        }
    }

    /// <summary>
    /// The index in the actions of a state that runs them one after another of the one that runs now, which PlayMaker
    /// keeps to itself.
    /// </summary>
    private static readonly FieldInfo? ActiveActionIndexField =
        typeof(FsmState).GetField("activeActionIndex", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// Puts an FSM of the room's own creature in a state that the creature was in, as the other game's creature goes on
    /// in it here, entering only the actions of the state that keep running once it is entered or that set up what the
    /// others work on (ActionRegistry). The state's own actions are put back after, also when one of them throws as it
    /// starts, which is logged and leaves the FSM as it is: thrown on, the entity was left half taken over, with its
    /// copy still on and the rest of its FSMs never given their states.
    /// A state that runs its actions one after another goes on with the first of these and ends after it. Which of its
    /// actions the other game had got to is not known, and PlayMaker counted on in the whole list from where it was in
    /// the shorter one, which did again or left out what came between, such as a hurt of the player.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="state">The state.</param>
    private static void ResumeState(PlayMakerFSM fsm, FsmState state) {
        var oldActions = state.Actions;
        state.Actions = oldActions.Where(a =>
            ActionRegistry.IsActionContinuous(a) || ActionRegistry.IsActionTransferSafeSetup(a)
        ).ToArray();
        try {
            fsm.SetState(state.Name);
        } catch (Exception e) {
            SSMP.Logging.Logger.Warn(
                $"Could not go on in '{state.Name}' of the FSM '{fsm.FsmName}' of '{fsm.gameObject.name}': {e}"
            );
            return;
        } finally {
            state.Actions = oldActions;
        }

        if (state.IsSequence && fsm.Fsm.ActiveState == state) {
            ActiveActionIndexField?.SetValue(state, oldActions.Length - 1);
        }
    }

    /// <summary>
    /// Switches the room's own copy on when this game takes a creature over, without PlayMaker starting over the FSMs
    /// that were just given the state the other game's copy was in.
    ///
    /// PlayMaker starts an FSM over from its first state whenever its object is switched on (RestartOnEnable, which
    /// is on for nearly every FSM in the game), and the object is switched on only after the states are set. So every
    /// handover threw them away and the creature took its very first road again from wherever the copy had got to -
    /// seen as a flyer that had been chasing a player vanishing the moment the other player left. PlayMaker keeps the
    /// active state when the setting is off (Fsm.OnEnable, checked in IL), so for the moment of switching on it is
    /// turned off for those FSMs and put back straight after. One that never started is started by PlayMaker on the
    /// next frame in the state it was given, rather than in its first one.
    /// </summary>
    /// <param name="active">Whether the copy is to be switched on at all.</param>
    /// <param name="resumed">Per host FSM, whether it was given the other game's state.</param>
    private void SwitchOnWithoutStartingOver(bool active, bool[] resumed) {
        var restarts = new bool[_fsms.Host.Count];
        for (var fsmIndex = 0; fsmIndex < _fsms.Host.Count; fsmIndex++) {
            var fsm = _fsms.Host[fsmIndex].Fsm;
            restarts[fsmIndex] = fsm.RestartOnEnable;
            if (fsmIndex < resumed.Length && resumed[fsmIndex]) {
                fsm.RestartOnEnable = false;
            }
        }

        try {
            Object.Host.SetActive(active);
        } finally {
            for (var fsmIndex = 0; fsmIndex < _fsms.Host.Count; fsmIndex++) {
                _fsms.Host[fsmIndex].Fsm.RestartOnEnable = restarts[fsmIndex];
            }
        }
    }

    /// <summary>
    /// Says, once per creature and handover, what this game took over: where it stands, whether it can be seen and
    /// hit, and which state each FSM goes on from. A creature that vanishes when a room changes hands leaves nothing
    /// else behind to tell what happened to it.
    /// </summary>
    private void SayWhatWasTakenOver() {
        try {
            var seen = Object.Host.GetComponent<Renderer>();
            var touched = Object.Host.GetComponent<Collider2D>();
            var body = Object.Host.GetComponent<Rigidbody2D>();
            var states = string.Join(", ", _fsms.Host.Select(fsm => $"{fsm.FsmName}: {fsm.ActiveStateName}"));

            SSMP.Logging.Logger.Info(
                $"Took over '{Object.Host.name}' at {Object.Host.transform.position}, " +
                $"on: {Object.Host.activeInHierarchy}, " +
                $"drawn: {(seen == null ? "-" : seen.enabled.ToString())}, " +
                $"touchable: {(touched == null ? "-" : touched.enabled.ToString())}, " +
                $"body: {(body == null ? "-" : body.bodyType.ToString())}, going on in {states}"
            );
        } catch (Exception e) {
            SSMP.Logging.Logger.Warn($"Could not say what was taken over: {e.Message}");
        }
    }

    /// <summary>
    /// Gives the room's own creature the tint of the copy as this game takes it over. The copy has been tinted by the
    /// scene host's colour actions all along, and by the colours its sprites ended up with there (TintComponent), and
    /// the FSM goes on from where the copy was, without the state that tinted it last. A crow that roosts in front of
    /// the room is drawn black until it flies at the player: the room's own one had been made black by its own first
    /// states, before it was put to sleep for the other game to run, and went on fighting black once this game took it
    /// over. The sprites under the creature that the FSMs on it colour go the same way.
    /// </summary>
    private void TakeTintFromCopy() {
        if (Object.Client.TryGetComponent<tk2dBaseSprite>(out var copySprite) &&
            Object.Host.TryGetComponent<tk2dBaseSprite>(out var roomSprite)) {
            roomSprite.color = copySprite.color;
        }

        if (Object.Client.TryGetComponent<SpriteRenderer>(out var copyRenderer) &&
            Object.Host.TryGetComponent<SpriteRenderer>(out var roomRenderer)) {
            roomRenderer.color = copyRenderer.color;
        }

        if (_components.TryGetValue(EntityComponentType.Tint, out var component) && component is TintComponent tint) {
            tint.TakeFromCopy();
        }
    }

    /// <summary>
    /// Gives a bell creature that burrows, as this game takes it over, the corpse and the animations that its state
    /// deciding whether it is a silver one sets as properties ("Normal" or "Silver" of "Control"). The room has neither
    /// on it: the creature gets them from that state, which it goes through before anything can kill it. The room's own
    /// creature never went through it here while the other game ran the room, so once taken over it had no corpse, the
    /// first thing it does as it dies threw, and the rest of dying - going under the ground out of reach - never
    /// happened. On spikes it was killed again every step, without end.
    /// </summary>
    private void GiveTakenOverFurmItsCorpse() {
        if (Type != EntityType.Furm) {
            return;
        }

        foreach (var fsm in _fsms.Host) {
            if (fsm == null || fsm.FsmName != "Control") {
                continue;
            }

            // Which of the two it is was said by the other game, whose variables were just put on it
            var state = fsm.GetStateOrNull(fsm.FsmVariables.FindFsmBool("Is Silver") is { Value: true }
                ? "Silver"
                : "Normal");
            if (state == null) {
                continue;
            }

            foreach (var action in state.Actions) {
                if (action is SetPropertyV2 { Enabled: true } property) {
                    property.TargetProperty.SetValue();
                }
            }
        }
    }

    /// <summary>
    /// Everything that was under the copy when it was made, by which what its FSMs let go of into the room is told from
    /// anything else (see <see cref="EntityFsmActions.SwitchOffWhatTheCopyLetGo"/>).
    /// </summary>
    private readonly Transform[] _madeWithCopy;

    /// <summary>
    /// Lets the parts of the room's own creature that hold other creatures, and that its FSMs let go of into the room,
    /// go into the room switched on, as this game is about to take the room over: before any creature is taken over
    /// (EntityManager.BecomeSceneHost), since one whose part is switched on only after it would start over. A knot of
    /// thread lets the group of creatures that wait in it out into the room as it starts, in the game that runs the
    /// room. In this game it never ran, so the group was still under it and switched off: taken over, the creatures
    /// of the group went on switched off, gone for both players, and breaking the knot would have switched them off
    /// with it. Each of those creatures is switched on or off by its own takeover, as the other game had it (see also
    /// EntityInitializer.IsWantedWhenTakingOver).
    /// </summary>
    internal void LetGoOfTheCreaturesItHolds() {
        if (!_isControlled || Object.Host == null) {
            return;
        }

        foreach (var part in EntityFsmActions.PartsLetGoBy(Object.Host)) {
            if (!part.transform.IsChildOf(Object.Host.transform) || !HoldsOtherCreatures(part, Object.Host)) {
                continue;
            }

            part.transform.SetParent(null, true);
            part.SetActive(true);
            SSMP.Logging.Logger.Info(
                $"Let '{part.name}' of '{Object.Host.name}' ({Id}, {Type}) go with the creatures in it, as the " +
                "game that ran the room did, before taking them over"
            );
        }
    }

    /// <summary>
    /// Whether an object is, or holds, the room's own object of an entity other than the given one.
    /// </summary>
    /// <param name="gameObject">The object.</param>
    /// <param name="self">The room's own object of the entity it belongs to, or null.</param>
    internal static bool HoldsOtherCreatures(GameObject gameObject, GameObject? self) {
        foreach (var transform in gameObject.GetComponentsInChildren<Transform>(true)) {
            if (transform.gameObject != self && EntitiesByRoomObject.ContainsKey(transform.gameObject)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Points the variables of an FSM of a creature taken over that are named for the player it goes after, and are
    /// empty, at this game's player, as the creature's own states point them in a room of its own (MakeHost). What the
    /// FSM keeps of objects is not told with the rest of it, an object meaning nothing in the other game: a variable
    /// that the creature's states fill as it goes after someone, which the room's own creature here never went
    /// through, was empty, and all that read it went after no one - a flyer taken over in the air flew off for good
    /// rather than come down on a player. What aims at the player is turned to whom the creature goes after
    /// (GamePatcher.EnemyTargeting), from there as from anywhere.
    ///
    /// Only a variable that the FSM itself puts the player in somewhere (GetHero, or SetGameObject from Hero), and
    /// never asks whether it holds anything: some keep in a variable named like that the creature an ally fights or the
    /// place a worker flies to, found as they go, and go one way or another by its being empty.
    /// </summary>
    /// <param name="fsm">The FSM of the room's own creature.</param>
    private void FillEmptyPlayerVariables(PlayMakerFSM fsm) {
        var hero = HeroController.SilentInstance;
        if (hero == null) {
            return;
        }

        HashSet<string>? empty = null;
        foreach (var variable in fsm.FsmVariables.GameObjectVariables) {
            if (variable.Value == null && GamePatcher.IsTargetVariableName(variable.Name)) {
                (empty ??= []).Add(variable.Name);
            }
        }

        if (empty == null) {
            return;
        }

        var fromPlayer = new HashSet<string>();
        var asked = new HashSet<string>();
        foreach (var state in fsm.FsmStates) {
            foreach (var action in state.Actions) {
                switch (action) {
                    case GetHero { storeResult: { } stored } when empty.Contains(stored.Name):
                        fromPlayer.Add(stored.Name);
                        break;
                    case SetGameObject { variable: { } set, gameObject: { UseVariable: true, Name: "Hero" } }
                        when empty.Contains(set.Name):
                        fromPlayer.Add(set.Name);
                        break;
                    default:
                        NoteAskedAbout(action, empty, asked);
                        break;
                }
            }
        }

        List<string>? filled = null;
        foreach (var variable in fsm.FsmVariables.GameObjectVariables) {
            if (variable.Value == null && fromPlayer.Contains(variable.Name) && !asked.Contains(variable.Name)) {
                variable.Value = hero.gameObject;
                (filled ??= []).Add(variable.Name);
            }
        }

        if (filled != null) {
            SSMP.Logging.Logger.Info(
                $"'{fsm.FsmName}' of entity {Id}, taken over, kept no player in {string.Join(", ", filled)}, and " +
                "now keeps this game's player there"
            );
        }
    }

    /// <summary>
    /// Notes which of the given variables an action asks about: whether one holds anything, or the same as another
    /// (GameObjectIsNull, GameObjectCompare, SetGameObjectIfNull and their kind), by the variables of its fields.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <param name="names">The names of the variables.</param>
    /// <param name="asked">Where the names it asks about go.</param>
    private static void NoteAskedAbout(FsmStateAction? action, HashSet<string> names, HashSet<string> asked) {
        var type = action?.GetType();
        if (type == null || !type.Name.Contains("IsNull") && !type.Name.Contains("Compare") &&
            !type.Name.Contains("IfNull")) {
            return;
        }

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public)) {
            var aim = field.GetValue(action) switch {
                FsmGameObject value => value,
                FsmOwnerDefault { OwnerOption: OwnerDefaultOption.SpecifyGameObject } pair => pair.GameObject,
                _ => null
            };
            if (aim is { UseVariable: true } && names.Contains(aim.Name)) {
                asked.Add(aim.Name);
            }
        }
    }

    /// <summary>
    /// Makes the entity a host entity if the client user became the scene host.
    /// </summary>
    public void MakeHost(uint sceneHostEpoch) {
        // The copy plays the end of the boss for the local player by itself, which takes them out of the room, and the
        // scene host has gone on to the end of its own. Taken over, the room's own boss would go on from where the
        // scene host's was instead: into the memory at once, from the middle of the binding (RunEachGamePart).
        if (_runHereForGood) {
            SSMP.Logging.Logger.Info($"Not taking over entity {Id}, whose copy plays its end for the local player");
            return;
        }

        // A talk of the local player with the copy ends here: the room's own creature goes on from where the scene
        // host's was, and the copy that the talk ran on is put away
        EndTalkHere(null);

        ResetAnticipation();
        ForgetPosePackets();

        // If the client object is null, we don't have to care about doing anything for the host object anymore
        if (Object.Client == null) {
            if (Object.Host != null) {
                Object.Host.SetActive(false);
            }

            _isControlled = false;
            SaveWhereItRuns(copyRuns: false);

            foreach (var component in _components.Values) {
                component.IsControlled = false;
                component.InitializeHost(sceneHostEpoch);
            }

            return;
        }

        // What the scene host sent of a move that the copy plays by itself and that still waits for the copy goes to
        // it now, while it takes such things in: the room's own FSM goes on from where the scene host's was
        // (ResumeState) without doing again what that did on its way there (TakeWhatWaitsInTheMove)
        TakeWhatWaitsInTheMove();

        // Make sure that the sprite animator doesn't play the default clip after enabling the object
        if (_animator.Host != null) {
            _animator.Host.playAutomatically = false;
        }

        for (var fsmIndex = 0; fsmIndex < _fsms.Host.Count; fsmIndex++) {
            var fsm = _fsms.Host[fsmIndex];

            // The other game never said anything about it, and what it does is about this game: it starts over,
            // looking its room up afresh, when the object is switched on
            if (IsRunByEachGame(fsm)) {
                continue;
            }

            var snapshot = _fsmSnapshots[fsmIndex];

            // Force initialize the host FSM, since it might have been disabled before initializing. Only the setting
            // up it never did: the other game's copy has been hiding, showing and moving this creature all along, and
            // doing a first state's hiding again made it invisible for both players while it went on fighting
            EntityInitializer.InitializeFsm(fsm, takingOver: true, currentState: snapshot.CurrentState);

            for (var i = 0; i < snapshot.Floats.Length; i++) {
                fsm.FsmVariables.FloatVariables[i].Value = snapshot.Floats[i];
            }

            for (var i = 0; i < snapshot.Ints.Length; i++) {
                fsm.FsmVariables.IntVariables[i].Value = snapshot.Ints[i];
            }

            for (var i = 0; i < snapshot.Bools.Length; i++) {
                fsm.FsmVariables.BoolVariables[i].Value = snapshot.Bools[i];
            }

            for (var i = 0; i < snapshot.Strings.Length; i++) {
                fsm.FsmVariables.StringVariables[i].Value = snapshot.Strings[i];
            }

            for (var i = 0; i < snapshot.Vector2s.Length; i++) {
                fsm.FsmVariables.Vector2Variables[i].Value = snapshot.Vector2s[i];
            }

            for (var i = 0; i < snapshot.Vector3s.Length; i++) {
                fsm.FsmVariables.Vector3Variables[i].Value = snapshot.Vector3s[i];
            }

            FillEmptyPlayerVariables(fsm);
        }

        GiveTakenOverFurmItsCorpse();

        // Only now to where the copy stands. The setting up above is done where the room put the creature, as in a
        // game where it started with the room: a creature that lets go of the markers it hides at, so that they stay
        // where the room has them, let them go wherever the copy had run to, and hid beside itself after
        if (_hasParent) {
            Object.Host.transform.localPosition = _lastPosition = Object.Client.transform.localPosition;
            Object.Host.transform.localScale = _lastScale = Object.Client.transform.localScale;
        } else {
            // Where the copy stands is where the room's own creature must stand. Worked out by hand from the position
            // of its parent alone, this left out the parent's scale: a crawler on a platform scaled a little narrower
            // was put down almost half a unit from the ledge it had been crawling on, and circled in the air after.
            Object.Host.transform.position = Object.Client.transform.position;
            _lastPosition = Object.Host.transform.position;

            // Since the scale of the client object is the entire scale we have and the host object scale can be in a
            // hierarchy, we need to calculate what the new local scale of the host will be to match the client scale
            var clientScale = Object.Client.transform.localScale;
            var hostLocalScale = Object.Host.transform.localScale;
            var hostLossyScale = Object.Host.transform.lossyScale;

            var newScaleX = hostLocalScale.x == 0 || hostLossyScale.x == 0
                ? 0f
                : clientScale.x / (hostLossyScale.x / hostLocalScale.x);
            var newScaleY = hostLocalScale.y == 0 || hostLossyScale.y == 0
                ? 0f
                : clientScale.y / (hostLossyScale.y / hostLocalScale.y);
            var newScaleZ = hostLocalScale.z == 0 || hostLossyScale.z == 0
                ? 0f
                : clientScale.z / (hostLossyScale.z / hostLocalScale.z);

            Object.Host.transform.localScale = _lastScale = new Vector3(newScaleX, newScaleY, newScaleZ);
        }

        // Which FSMs were given the state the other game's copy was in, so that switching the object on below does
        // not start just those over
        var resumed = new bool[_fsms.Host.Count];

        for (var fsmIndex = 0; fsmIndex < _fsms.Host.Count; fsmIndex++) {
            var fsm = _fsms.Host[fsmIndex];
            var snapshot = _fsmSnapshots[fsmIndex];
            if (IsRunByEachGame(fsm)) {
                continue;
            }

            // Before setting the state, we replace the actions of the to-be state to only include the ones that
            // should be executed again (including actions with "everyFrame" on true or that continuously check
            // collisions for example).
            if (string.IsNullOrEmpty(snapshot.CurrentState)) {
                continue;
            }

            var state = fsm.GetStateOrNull(snapshot.CurrentState);
            if (state == null) {
                continue;
            }

            ResumeState(fsm, state);
            resumed[fsmIndex] = true;
        }

        // The body is left the kind it is, which is the kind the other game last said the creature left it as
        // (BodyTypeComponent). Making every body move by physics here pushed a crawler that is built to hug the
        // walls it crawls on away from them.

        _isControlled = false;

        foreach (var component in _components.Values) {
            component.IsControlled = false;
        }

        if (_animator.Client != null) {
            var currentClip = _animator.Client.CurrentClip;
            if (currentClip != null) {
                var clientAnimation = currentClip.name;
                var wrapMode = currentClip.wrapMode;

                LateUpdateAnimation(_animator.Host, clientAnimation, wrapMode);
            }
        }

        TakeTintFromCopy();

        // How the copy was moving by itself is taken before its replays are stopped and it is switched off, for the
        // room's own object to carry on with (OwnMotionComponent)
        if (_components.TryGetValue(EntityComponentType.OwnMotion, out var ownMotion)) {
            ((OwnMotionComponent) ownMotion).TakeMotionFromCopy();
        }

        // A player that the copy holds stays held by the room's own creature, which goes on from there
        StopRunningHere(letGoOfThePlayer: false);
        EntityFsmActions.LeaveStatesOf(_fsms.Client);

        // Not from a copy that is off already, like one that died: what it dropped as it went stays, as it does in the
        // room's own creature's game, which never let go of anything of its own in its place
        if (Object.Client.activeSelf) {
            EntityFsmActions.SwitchOffWhatTheCopyLetGo(Object.Client, Object.Host, _madeWithCopy);
        }

        // What the copy kept in the save goes to the room's own creature, which saves it from now on
        HandSaveToTheRoom();
        SaveWhereItRuns(copyRuns: false);

        var clientActive = Object.Client.activeSelf;
        _sceneHostHasItOn = false;
        Object.Client.SetActive(false);
        SwitchOnWithoutStartingOver(clientActive, resumed);
        SayWhatWasTakenOver();

        _lastIsActive = _hasParent ? Object.Host.activeSelf : Object.Host.activeInHierarchy;

        foreach (var component in _components.Values) {
            component.InitializeHost(sceneHostEpoch);
        }
    }

    /// <summary>
    /// Events that the FSMs of this entity send themselves when the player touches it, or null for none.
    /// </summary>
    private HashSet<string>? _touchEvents;

    /// <summary>
    /// What a cast of the body of an entity carried on after an input found on the way (see <see cref="CarryOn"/>).
    /// </summary>
    private static readonly RaycastHit2D[] StrikeCastHits = new RaycastHit2D[8];

    /// <summary>
    /// How far short of the room an entity carried on after an input stops, so that it is not left touching it.
    /// </summary>
    private const float StrikeCastSkin = 0.02f;

    /// <summary>
    /// Raised on a scene client when its player touched the copy of an entity, or one of the copy's parts caught them
    /// or a thing of theirs: the entity, the index of the FSM, the event that the FSM is sent for it, for a catch what
    /// the part set on the FSM along with it (null for a touch), and whether the part that told it grabbed the player
    /// themselves (see <see cref="HearCopyTold"/>). What the event leads to is played on the copy at once where the
    /// entity or the catch says so (see <see cref="PlayHere"/>) and sent to the scene host.
    /// </summary>
    public static event Action<Entity, byte, string, ToldValues?, bool>? CopyTouchedLocalPlayer;

    /// <summary>
    /// The events with which the game's catching parts - a blade, a claw, a coil that holds the player for a string of
    /// blows - tell the creature they belong to that they caught the player, and which start its combo.
    /// </summary>
    public static readonly HashSet<string> CatchEvents = ["MULTI HIT CONNECT"];

    /// <summary>
    /// The entities, by the object of their copy (see <see cref="OnBroadcastToObject"/>).
    /// </summary>
    private static readonly Dictionary<GameObject, Entity> EntitiesByCopy = new();

    /// <summary>
    /// Whether what is being taken in is the state of a room this game is already in, asked for again after a gap in
    /// what reached it (ClientManager.OnRoomState), rather than the state of a room being walked into. What that
    /// already is is not done again, and what plays out once is not played again.
    /// </summary>
    internal static bool FromRoomState;

    /// <summary>
    /// When this game last took in each kind of thing the scene host said of this entity as it happened, in unscaled
    /// seconds: whether it is on, the clip it shows, each kind of data, the variables of each FSM.
    /// </summary>
    private readonly Dictionary<int, float> _heardAt = new();

    /// <summary>
    /// How recently a kind of thing must have been heard as it happened for the state of the room to leave it be, in
    /// seconds (see <see cref="YieldsToNewer"/>).
    /// </summary>
    private const float RoomStateYieldTime = 1f;

    /// <summary>
    /// The kinds of things heard of an entity besides its data, which go by their component type (see
    /// <see cref="_heardAt"/>): whether it is on, its clip, and the variables of each FSM from this one on.
    /// </summary>
    private const int HeardActive = -1, HeardClip = -2, HeardScale = -3, HeardPosition = -4, HeardFsmVariables = 1000;

    /// <summary>
    /// Notes that a kind of thing was heard as it happened, or says whether the state of the room is to leave it be.
    /// The server puts the state of the room together when asked, and what reaches it after that goes out in the same
    /// packet - and is taken in first, since a packet's contents are taken in in the order of their kind
    /// (BasePacket.ReadPacketData), the state of the room last. So what was heard as it happened just before is the
    /// newer of the two: a creature that had just been switched on was switched off again.
    /// </summary>
    /// <param name="kind">The kind of thing.</param>
    /// <returns>Whether the state of the room leaves it be.</returns>
    private bool YieldsToNewer(int kind) {
        if (!FromRoomState) {
            _heardAt[kind] = Time.unscaledTime;
            return false;
        }

        return _heardAt.TryGetValue(kind, out var at) && Time.unscaledTime - at < RoomStateYieldTime;
    }

    /// <summary>
    /// The entities, by the room's own object of each.
    /// </summary>
    private static readonly Dictionary<GameObject, Entity> EntitiesByRoomObject = new();

    /// <summary>
    /// The entity whose copy an object is, or is a part of, or null for anything else.
    /// </summary>
    /// <param name="gameObject">The object.</param>
    internal static Entity? FindByCopyPart(GameObject? gameObject) {
        for (var current = gameObject == null ? null : gameObject.transform; current != null; current = current.parent) {
            if (EntitiesByCopy.TryGetValue(current.gameObject, out var entity)) {
                return entity;
            }
        }

        return null;
    }

    /// <summary>
    /// What the game that runs this creature last said of which way it faces (the sign of its x scale), and how long ago,
    /// for the log of what hurt the player (CoopSave.Hurts).
    /// </summary>
    internal string DescribeToldFacing() => _toldFacingAt <= 0f
        ? "never told which way it faces"
        : $"told x scale {(_toldFacing > 0f ? "+" : "-")} {Time.unscaledTime - _toldFacingAt:0.0} s ago";

    /// <summary>
    /// How fast this game moves the copy between the positions it is told, for the same log, or null for a copy that
    /// is not moved that way.
    /// </summary>
    internal Vector3? CopyVelocity => Object.Client != null &&
                                      Object.Client.TryGetComponent<PredictiveInterpolation>(out var interpolation)
        ? interpolation.Velocity
        : null;

    /// <summary>
    /// The entity that an object is, or is a part of - the room's own object or the copy - or null for anything else.
    /// </summary>
    /// <param name="gameObject">The object.</param>
    internal static Entity? FindByPart(GameObject? gameObject) {
        for (var current = gameObject == null ? null : gameObject.transform; current != null; current = current.parent) {
            if (EntitiesByCopy.TryGetValue(current.gameObject, out var entity) ||
                EntitiesByRoomObject.TryGetValue(current.gameObject, out entity)) {
                return entity;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an FSM is one of a creature's own that this game runs for every game: an FSM of the room's own object of
    /// an entity whose scene host this game is, that is not run by each game (see <see cref="IsRunByEachGame"/>). What
    /// such an FSM decides, the other games see the creature do.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    internal static bool RunsHere(PlayMakerFSM? fsm) {
        return fsm != null &&
               FindByPart(fsm.gameObject) is { _isSceneHostDetermined: true, _isControlled: false } entity &&
               entity._fsms.Host.Contains(fsm) && !entity.IsRunByEachGame(fsm);
    }

    /// <summary>
    /// Whether an FSM is one of those of this entity that only the game of the scene host runs: an FSM of the creature
    /// itself, on the room's own object or on the copy, that is not run by each game (see
    /// <see cref="IsRunByEachGame"/>). What such an FSM opens - a line, a prompt - opens in that game alone.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    internal bool IsRunBySceneHostAlone(PlayMakerFSM? fsm) {
        return fsm != null && (_fsms.Host.Contains(fsm) || _fsms.Client.Contains(fsm)) && !IsRunByEachGame(fsm);
    }

    /// <summary>
    /// The hook that holds copies within their bounds in the space the bounds are kept in (see
    /// <see cref="OnConstrainLateUpdate"/>), put in place with the first copy that needs it.
    /// </summary>
    private static Hook? _constrainHook;

    /// <summary>
    /// Whether <see cref="_constrainHook"/> was looked for, so that a game without the method says so only once.
    /// </summary>
    private static bool _constrainHookTried;

    /// <summary>
    /// Holds the copy of a creature within the bounds its game holds the room's own creature in, in the space they are
    /// kept in. Some creatures are held between bounds kept in the space of what they sit under in the room - the
    /// fight they belong to - and a copy sits under nothing, so the same bounds held it in the world instead: a
    /// creature whose fight is far from the corner of the room was put back at the corner every frame, out of sight,
    /// while what it threw still came from where it really was (USER 10-10, "刚刚有小怪我这里看不到，但是投的针能投到我",
    /// "竞技场的怪我能看到一部分"). Done as the game does it (ConstrainPosition.LateUpdate), only through what the room's
    /// own creature sits under; everything else is held by the game itself.
    /// </summary>
    private static void OnConstrainLateUpdate(Action<ConstrainPosition> orig, ConstrainPosition self) {
        if (!self.localSpace || !EntitiesByCopy.TryGetValue(self.gameObject, out var entity) || entity._hasParent ||
            entity.Object.Host == null || entity.Object.Host.transform.parent is not { } space) {
            orig(self);
            return;
        }

        var transform = self.transform;
        var position = space.InverseTransformPoint(transform.position);
        var constrained = false;
        if (self.constrainX) {
            if (position.x < self.xMin) {
                position.x = self.xMin;
                constrained = true;
            } else if (position.x > self.xMax) {
                position.x = self.xMax;
                constrained = true;
            }
        }

        if (self.constrainY) {
            if (position.y < self.yMin) {
                position.y = self.yMin;
                constrained = true;
            } else if (position.y > self.yMax) {
                position.y = self.yMax;
                constrained = true;
            }
        }

        if (!constrained || Time.timeScale <= Mathf.Epsilon) {
            return;
        }

        transform.position = space.TransformPoint(position);
        if (self.cutVelocityOnConstrain && self.rb != null) {
            self.rb.linearVelocity = Vector2.zero;
        }

        self.OnConstrained?.Invoke();
    }

    /// <summary>
    /// The hook that hears events sent to the objects of copies, put in place with the first entity.
    /// </summary>
    private static Hook? _broadcastHook;

    /// <summary>
    /// The hook that hears events sent by name to one FSM of the objects of copies, put in place with the first entity.
    /// </summary>
    private static Hook? _sendToFsmHook;

    /// <summary>
    /// Whether <see cref="_sendToFsmHook"/> was looked for, so that a game without the method says so only once.
    /// </summary>
    private static bool _sendToFsmHookTried;

    /// <summary>
    /// The hook that notes which FSM a collider is entering the trigger of (see <see cref="_triggeredFsm"/>), put in
    /// place with the first entity.
    /// </summary>
    private static Hook? _triggerEnterHook;

    /// <summary>
    /// The FSM that a collider is entering the trigger of right now, while the FSM does what that sets off, or null.
    /// The collider it keeps as the one that set it off stays there after that, until the next one enters.
    /// </summary>
    private static HutongGames.PlayMaker.Fsm? _triggeredFsm;

    /// <summary>
    /// The events with which the game grabs the player: holds them, stuns them and hides them, until the creature that
    /// grabbed them lets go.
    /// </summary>
    private static readonly HashSet<string> GrabEvents = ["HERO GRAB", "HERO GRAB VULNERABLE"];

    /// <summary>
    /// Whether each state of the FSMs of the parts of copies grabs the player, found the first time a part tells its
    /// copy anything from there (see <see cref="GrabsThePlayer"/>).
    /// </summary>
    private static readonly ConditionalWeakTable<FsmState, StrongBox<bool>> GrabbingStates = new();

    /// <summary>
    /// The fields of each kind of action that hold the name of an event or an event, by type.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> EventNameFields = new();

    /// <summary>
    /// The local player's character and the collider of their hit box, found once for each character.
    /// </summary>
    private static (HeroController Hero, Collider2D? Box)? _heroBox;

    /// <summary>
    /// Makes the copy of this entity answer the local player touching it, the way the entity itself answers its own
    /// player. The copy runs none of its FSMs, and in the game that does, the player of this game is only a figure
    /// that nothing can touch: a rock that hit the player of a scene client fell on through them. With this, the
    /// game of the player who was touched plays what the event leads to at once (see <see cref="PlayHere"/>) and the
    /// scene host plays it too - the way a hit is played where it lands and sent on.
    /// </summary>
    /// <param name="events">The events that the FSMs send themselves when the player touches the entity.</param>
    public void ListenForTouches(IEnumerable<string> events) {
        _touchEvents = new HashSet<string>(events);

        if (Object.Client == null) {
            return;
        }

        var listener = Object.Client.GetComponent<TouchListener>() ?? Object.Client.AddComponent<TouchListener>();
        listener.Touched += OnCopyTouched;
    }

    /// <summary>
    /// Says that the local player touched the copy, if the state its FSM is in - its own while it runs here, the scene
    /// host's otherwise - leads anywhere on one of the touch events.
    /// </summary>
    /// <param name="other">The collider that started touching the copy.</param>
    private void OnCopyTouched(Collider2D other) {
        if (!_isControlled || _touchEvents == null || !IsLocalPlayer(other)) {
            return;
        }

        for (var fsmIndex = 0; fsmIndex < _fsms.Client.Count; fsmIndex++) {
            if (StateOfCopy(_fsms.Client[fsmIndex]) is not { } state) {
                continue;
            }

            foreach (var transition in state.Transitions) {
                if (_touchEvents.Contains(transition.EventName) && transition.ToFsmState != null) {
                    CopyTouchedLocalPlayer?.Invoke(this, (byte) fsmIndex, transition.EventName, null, false);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// The state that an FSM of the copy is in: its own while it runs here, the one the scene host says otherwise.
    /// </summary>
    private FsmState? StateOfCopy(PlayMakerFSM? fsm) {
        return fsm != null &&
               (fsm == _runHere ? fsm.ActiveStateName : EntityFsmActions.HostStateOf(fsm.Fsm)) is { } stateName
            ? fsm.Fsm.GetState(stateName)
            : null;
    }

    /// <summary>
    /// Hook for an FSM sending an event to the FSMs of an object, which hears one of the parts of a copy catching the
    /// local player or a thing of theirs (see <see cref="HearCopyTold"/>).
    /// </summary>
    private static void OnBroadcastToObject(
        Action<HutongGames.PlayMaker.Fsm, GameObject, FsmEvent, FsmEventData, bool, bool> orig,
        HutongGames.PlayMaker.Fsm self,
        GameObject go,
        FsmEvent fsmEvent,
        FsmEventData eventData,
        bool sendToChildren,
        bool excludeSelf
    ) {
        orig(self, go, fsmEvent, eventData, sendToChildren, excludeSelf);

        if (go != null && fsmEvent != null) {
            HearCopyTold(self, go, null, fsmEvent.Name);
        }
    }

    /// <summary>
    /// Hook for an FSM sending an event to the FSM of an object that has a given name, which hears one of the parts of a
    /// copy catching the local player or a thing of theirs (see <see cref="HearCopyTold"/>). A tendril tells the
    /// creature it grows from by the name of its FSM.
    /// </summary>
    private static void OnSendToFsmOnObject(
        Action<HutongGames.PlayMaker.Fsm, GameObject, string, FsmEvent> orig,
        HutongGames.PlayMaker.Fsm self,
        GameObject go,
        string fsmName,
        FsmEvent fsmEvent
    ) {
        orig(self, go, fsmName, fsmEvent);

        if (go != null && fsmEvent != null) {
            HearCopyTold(self, go, fsmName, fsmEvent.Name);
        }
    }

    /// <summary>
    /// Hears an FSM that runs in this game telling the copy of an entity an event, as a part of the creature does once
    /// it caught something: the local player, which the game's catching parts say with <see cref="CatchEvents"/>, or
    /// which a part that grabs the player tells from where it is touching them (see <see cref="GrabsThePlayer"/>) - a
    /// tendril that reels them in, a charge that seizes them to drain their silk, the grab box of a creature that
    /// pounces on them whole and carries them off - or a thing of theirs that the game
    /// marks for catching (see <see cref="IsCatchableThingOfTheLocalPlayer"/>). The part runs by itself in this game,
    /// and did to what it caught all that it does to it, but the copy's own FSMs are switched off and did not hear it:
    /// the player's flier that a tendril took simply was gone, and the creature never ate it in either game; a player
    /// that a charge seized was held, hidden, for good. An FSM that is switched off only sends what the scene host's
    /// game did, replayed on it.
    /// </summary>
    /// <param name="sender">The FSM that sends the event.</param>
    /// <param name="target">The object that it sends the event to.</param>
    /// <param name="fsmName">The name of the FSM of the object that it sends the event to, or null for all of them.
    /// </param>
    /// <param name="eventName">The event.</param>
    private static void HearCopyTold(
        HutongGames.PlayMaker.Fsm sender,
        GameObject target,
        string? fsmName,
        string eventName
    ) {
        if (sender.Owner is not PlayMakerFSM { enabled: true } || !EntitiesByCopy.TryGetValue(target, out var entity)) {
            return;
        }

        if (CatchEvents.Contains(eventName)) {
            entity.OnCopyCaught(sender, fsmName, eventName, false);
            return;
        }

        // Anything else counts only from a part of the copy itself, which caught it here: whatever else tells the
        // creature something about the player or their things does so in the scene host's game as well
        if (sender.GameObject is not { } part || !part.transform.IsChildOf(target.transform)) {
            return;
        }

        // Or from a part through which the local player talks to the creature, whose talk ends by telling the creature
        // to go on from it, like the knight asked to spar (see Entity.HandOver)
        if (part != target && IsInTalkPart(part, target)) {
            entity.OnCopyToldByTalk(fsmName, eventName);
            return;
        }

        // Or from an FSM of the copy itself that runs here, which only one that each game runs by itself does
        // (EntityRegistryEntry.EachGameFsms): what heard a sign of the local player and tells the creature what to make
        // of it, or what watches the grab box of a creature that pounces on the player whole and tells the FSM that
        // carries them off, by its name. That one grabs nobody itself - that FSM does, once it takes the grab - so a
        // grab that no FSM of the copy takes leaves nobody to let go.
        if (part == target) {
            if (OwnSign is { } sign && sender.Owner is PlayMakerFSM listener && entity.IsRunByEachGame(listener) &&
                ListensFor(sender, sign)) {
                entity.OnCopyHeardOwnSign(fsmName, eventName, sign);
                return;
            }

            if (!string.IsNullOrEmpty(fsmName) && fsmName != sender.Name && !entity.PlaysACombo &&
                GrabsThePlayer(sender) && TouchesWhatItWatches(sender)) {
                entity.OnCopyCaught(sender, fsmName, eventName, true);
            }

            return;
        }

        // A thing counts only while it is entering the part, the collider that the part keeps as the one that set it
        // off being that thing's; the one that set it off last stays there after. What the parts say while the copy
        // plays the combo of a catch of the local player is part of that combo.
        if (sender == _triggeredFsm && IsCatchableThingOfTheLocalPlayer(sender.TriggerCollider2D)) {
            entity.OnCopyCaught(sender, fsmName, eventName, false);
        } else if (!entity.PlaysACombo && GrabsThePlayer(sender) && TouchesTheLocalPlayer(part) &&
                   !entity.OnCopyCaught(sender, fsmName, eventName, true) && !AnyLeadsACatch()) {
            // A part may grab the player itself before it tells the creature, and nothing goes on with a catch that
            // no FSM of the copy can take from where it is: the player is let go at once
            EntityFsmActions.LetGoOfTheHeldLocalPlayer($"no FSM of the copy of entity {entity.Id} takes '{eventName}'");
        }
    }

    /// <summary>
    /// Takes it that a part of the copy caught the local player or a thing of theirs and told the copy so. In the game
    /// that runs the creature, its FSM goes from there into what it does with the catch - the combo that holds the
    /// player and strikes them, the maw that a tendril reels a thing into; here the part held the player while the copy
    /// flew on, or took the thing and the copy never moved. Each FSM of the copy that goes anywhere on the event from
    /// the state it is in is played as a catch: at once here, where the catch is theirs, and on the scene host with
    /// what the part set on that FSM along with the event (see <see cref="PlayHere"/>).
    /// </summary>
    /// <param name="sender">The FSM of the part.</param>
    /// <param name="fsmName">The name of the FSM that the part told, or null for all of them.</param>
    /// <param name="eventName">The event with which the part told the copy.</param>
    /// <param name="grabbedThePlayer">Whether the part grabbed the player themselves, rather than being one that names
    /// its catch or having caught a thing of theirs.</param>
    /// <returns>Whether any FSM of the copy took the event.</returns>
    private bool OnCopyCaught(HutongGames.PlayMaker.Fsm sender, string? fsmName, string eventName, bool grabbedThePlayer) {
        if (!_isControlled || Object.Client == null) {
            return false;
        }

        var taken = false;

        for (var fsmIndex = 0; fsmIndex < _fsms.Client.Count; fsmIndex++) {
            var fsm = _fsms.Client[fsmIndex];

            // One that runs by itself on the copy heard the event
            if (fsm == null || fsm.enabled || !string.IsNullOrEmpty(fsmName) && fsm.FsmName != fsmName ||
                StateOfCopy(fsm) is not { } state || EntityFsmActions.FindTransition(fsm.Fsm, state, eventName) == null) {
                continue;
            }

            taken = true;
            CopyTouchedLocalPlayer?.Invoke(
                this, (byte) fsmIndex, eventName, ToldValues.From(sender, Object.Client, fsm), grabbedThePlayer
            );
        }

        return taken;
    }

    /// <summary>
    /// Hook for a collider entering the trigger of an FSM, which notes the FSM while it does what that sets off (see
    /// <see cref="_triggeredFsm"/>).
    /// </summary>
    private static void OnFsmTriggerEnter2D(
        Action<HutongGames.PlayMaker.Fsm, Collider2D> orig,
        HutongGames.PlayMaker.Fsm self,
        Collider2D other
    ) {
        var outer = _triggeredFsm;
        _triggeredFsm = self;
        try {
            orig(self, other);
        } finally {
            _triggeredFsm = outer;
        }
    }

    /// <summary>
    /// Whether what set a part off is a thing of the local player that the game marks for catching. The bombs, the
    /// flier, the bola and the like carry a <see cref="CustomTag"/>, by which the parts that catch things - a tendril
    /// that reels them in, a maw that sucks them up - tell what they caught, and which kind of catch it is. Only this
    /// game's player's own things count: those of the partner here are copies, and the partner's game catches the real
    /// ones and says so.
    /// </summary>
    /// <param name="collider">The collider that set the part's FSM off.</param>
    private static bool IsCatchableThingOfTheLocalPlayer(Collider2D? collider) {
        return collider != null && collider.GetComponent<CustomTag>() != null &&
               LocalToolComponent.IsLocalTool(collider.gameObject);
    }

    /// <summary>
    /// Whether an FSM of a part grabs the player with what it tells its creature now: the state it tells it from, or
    /// the one it came there from, asks whether they can be grabbed or names the game's grab (see
    /// <see cref="GrabEvents"/>), itself or in a template it runs. What else the same part tells - a thing it took
    /// from someone - is no grab, nor is anything a part tells that only strikes the player - a blade, a coil - like a
    /// parry: the ones that catch the player for a combo say so by name (<see cref="CatchEvents"/>). The partner's
    /// figure in this game touches nothing (it is on the default layer), so the player such a part grabs here is the
    /// local one.
    /// </summary>
    private static bool GrabsThePlayer(HutongGames.PlayMaker.Fsm fsm) {
        return fsm.ActiveState is { } state && Grabs(state) || fsm.PreviousActiveState is { } previous && Grabs(previous);

        static bool Grabs(FsmState state) {
            if (GrabbingStates.TryGetValue(state, out var known)) {
                return known.Value;
            }

            var grabs = Array.Exists(
                state.Actions,
                action => AsksOrNamesTheGrab(action) ||
                          EntityFsmActions.SubFsmOf(action) is { States: { } states } &&
                          Array.Exists(states, sub => Array.Exists(sub.Actions, AsksOrNamesTheGrab))
            );
            GrabbingStates.Add(state, new StrongBox<bool>(grabs));
            return grabs;
        }

        static bool AsksOrNamesTheGrab(FsmStateAction action) {
            return action.GetType().Name.StartsWith("CanHeroBeGrabbed", StringComparison.Ordinal) ||
                   NamesAGrabEvent(action);
        }
    }

    /// <summary>
    /// Whether an action is given one of the <see cref="GrabEvents"/>, as an event or by its name.
    /// </summary>
    private static bool NamesAGrabEvent(FsmStateAction action) {
        var type = action.GetType();
        if (!EventNameFields.TryGetValue(type, out var fields)) {
            fields = Array.FindAll(
                type.GetFields(BindingFlags.Instance | BindingFlags.Public),
                field => field.FieldType == typeof(FsmString) || field.FieldType == typeof(FsmString[]) ||
                         field.FieldType == typeof(FsmEvent)
            );
            EventNameFields[type] = fields;
        }

        foreach (var field in fields) {
            switch (field.GetValue(action)) {
                case FsmString { Value: { } name } when GrabEvents.Contains(name):
                case FsmEvent { Name: { } eventName } when GrabEvents.Contains(eventName):
                case FsmString[] names when Array.Exists(names, name => name?.Value is { } value && GrabEvents.Contains(value)):
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a collider of a part is touching the body or the hit box of the local player right now.
    /// </summary>
    /// <param name="part">The part.</param>
    /// <param name="hitBoxOnly">Whether only the hit box counts, for a part that waits for the hit box by its tag, the
    /// way the game's own trigger of it does: the body of the player does not set it off.</param>
    private static bool TouchesTheLocalPlayer(GameObject part, bool hitBoxOnly = false) {
        var hero = HeroController.SilentInstance;
        if (hero == null) {
            return false;
        }

        if (_heroBox is not { } known || known.Hero != hero) {
            var box = hero.GetComponentInChildren<HeroBox>(true);
            _heroBox = known = (hero, box != null ? box.GetComponent<Collider2D>() : null);
        }

        var body = hitBoxOnly ? null : hero.GetComponent<Collider2D>();
        foreach (var collider in part.GetComponents<Collider2D>()) {
            if (collider.isActiveAndEnabled && (Touches(collider, body) || Touches(collider, known.Box))) {
                return true;
            }
        }

        return false;

        static bool Touches(Collider2D collider, Collider2D? other) {
            return other != null && other.isActiveAndEnabled &&
                   (collider.IsTouching(other) || collider.Distance(other).isOverlapped);
        }
    }

    /// <summary>
    /// Whether the local player is touching an object whose trigger an FSM watches right now, for an FSM on the
    /// creature itself (see <see cref="HearCopyTold"/>): its own colliders are its body, and what it grabs the player
    /// with is a box of its own that it watches.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    private static bool TouchesWhatItWatches(HutongGames.PlayMaker.Fsm fsm) {
        foreach (var state in fsm.States) {
            foreach (var action in state.Actions) {
                var type = action.GetType();
                if (type.Name.StartsWith("Trigger2dEvent", StringComparison.Ordinal) &&
                    type.GetField("gameObject", BindingFlags.Instance | BindingFlags.Public)?.GetValue(action) is
                        FsmOwnerDefault watched &&
                    fsm.GetOwnerDefaultTarget(watched) is { } watchedObject && TouchesTheLocalPlayer(watchedObject)) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a collider is the body or the hit box of the local player, rather than one of their attacks.
    /// </summary>
    private static bool IsLocalPlayer(Collider2D other) {
        var hero = HeroController.SilentInstance;
        return hero != null && other != null &&
               (other.gameObject == hero.gameObject || other.GetComponent<HeroBox>() != null);
    }

    /// <summary>
    /// Carries the body of the room's own object of the entity on for a while the way the physics would: along at its
    /// speed, falling as its gravity says and turning at its spin. It stops short at the room on the way, which the
    /// partner's copy bumped into rather than went through.
    /// </summary>
    /// <param name="body">The body.</param>
    /// <param name="elapsed">For how long, in seconds.</param>
    private void CarryOn(Rigidbody2D body, float elapsed) {
        var velocity = body.linearVelocity;
        var gravity = body.bodyType == RigidbodyType2D.Dynamic ? Physics2D.gravity * body.gravityScale : Vector2.zero;
        var way = velocity * elapsed + 0.5f * elapsed * elapsed * gravity;

        var distance = way.magnitude;
        if (distance > 0f) {
            var direction = way / distance;
            var hitCount = body.Cast(direction, StrikeCastHits, distance);
            for (var i = 0; i < hitCount; i++) {
                var collider = StrikeCastHits[i].collider;
                if (collider != null && !collider.isTrigger &&
                    collider.gameObject.layer == (int) GlobalEnums.PhysLayers.TERRAIN) {
                    distance = Mathf.Min(distance, Mathf.Max(0f, StrikeCastHits[i].distance - StrikeCastSkin));
                }
            }

            way = direction * distance;
        }

        PlaceBody(body, body.position + way, body.rotation + body.angularVelocity * elapsed);
        body.linearVelocity = velocity + gravity * elapsed;
    }

    /// <summary>
    /// Puts the body of the room's own object of the entity somewhere, the object with it, so that what reads where
    /// the object is before the next step of physics finds it there.
    /// </summary>
    private void PlaceBody(Rigidbody2D body, Vector2 position, float angle) {
        var transform = Object.Host.transform;
        transform.position = new Vector3(position.x, position.y, transform.position.z);

        var eulerAngles = transform.eulerAngles;
        transform.eulerAngles = new Vector3(eulerAngles.x, eulerAngles.y, angle);

        body.position = position;
        body.rotation = angle;
    }

    /// <summary>
    /// Takes the number for something the local player is about to do to this entity before the scene host has heard
    /// of it, and starts waiting for the scene host to say it has taken it in.
    /// </summary>
    /// <returns>The number to send with it.</returns>
    public byte BeginAnticipation() {
        // Zero is kept for "nothing", so the count goes round to one rather than to it
        _nextAnticipation = (byte) (_nextAnticipation == byte.MaxValue ? 1 : _nextAnticipation + 1);
        _outstandingAnticipation = _nextAnticipation;
        _outstandingExpiry = Time.unscaledTime + AnticipationHoldTime;

        // What is waited for now is this, which moves the copy some other way if it moves it at all
        _knockbackWait = null;

        return _outstandingAnticipation;
    }

    /// <summary>
    /// Takes the number for a knockback of the local player's that the local game has just played on the copy, as
    /// <see cref="BeginAnticipation"/> does, and has the copy go on moving by itself while it waits for the scene host
    /// to say it has it. The creature does not stop walking while it is knocked back: the scene host's positions show
    /// it walking on, and from some moment on knocked back by the scene host's own game as well, which says how far
    /// it has pushed it. What is left once that push is taken away is what the copy goes on doing
    /// (<see cref="GoOnWhileKnockedBack"/>); standing still instead, it slid the whole way at the end of the wait, and
    /// a creature that had walked on away from the player looked knocked back much further than the game ever does.
    /// </summary>
    /// <returns>The number to send with it.</returns>
    public byte BeginKnockbackAnticipation() {
        // A knockback in the middle of the wait on another goes on from where that one has got to
        var wait = IsAnticipating() ? _knockbackWait : null;
        var anticipation = BeginAnticipation();
        _knockbackWait = wait ?? new KnockbackWait();

        return anticipation;
    }

    /// <summary>
    /// Stops waiting on the last number taken, for something that turned out not to be done after all.
    /// </summary>
    public void EndAnticipation() {
        if (_knockbackWait != null) {
            NoteKnockbackWaitEnd(_knockbackWait, "the knockback was not sent");
        }

        _outstandingAnticipation = 0;
        _knockbackWait = null;
    }

    /// <summary>
    /// Notes how a wait on a knockback that the local game played first ended (see <see cref="LastKnockbackWait"/>).
    /// </summary>
    /// <param name="wait">The wait.</param>
    /// <param name="how">What ended it.</param>
    private void NoteKnockbackWaitEnd(KnockbackWait wait, string how) {
        LastKnockbackWait = (Time.time, wait.Pushed, wait.OwnMoved, how);
    }

    /// <summary>
    /// Notes that a pull of the local player's own, like the storm of a silk skill or a snare they set, moved the copy
    /// on this step of physics, which shows here at once. Until shortly after the last such step the copy is left where
    /// the pull puts it rather than following the scene host's positions, which only show the pull a round trip later;
    /// then it goes on from where it stands to the scene host's positions. The scene host's game moves the entity
    /// itself with its copy of the same skill or thing, so the two mostly differ by what the entity did by itself
    /// meanwhile.
    /// </summary>
    public void NotePulledHere() {
        _pulledHereUntil = Time.time + PulledHereHoldTime;
    }

    /// <summary>
    /// Notes that the scene host has been told of something a scene client did to this entity, which what it sends
    /// will have the whole of in it once a step of physics and the time it takes to happen have gone by.
    /// </summary>
    /// <param name="tag">The tag of the game that sent it (see <see cref="RequestTag"/>).</param>
    /// <param name="anticipation">The number it was sent under.</param>
    /// <param name="settleTime">
    /// How much longer what was done goes on moving the entity here, in seconds. The player waiting on this has
    /// already watched their own copy do the whole of it, so anything said before then only sends them back into
    /// the middle of it. Nothing that takes no time waits at all, and nothing waits longer than
    /// <see cref="AnticipationSettleCap"/> however long it says it takes.
    /// </param>
    public void NoteAnticipation(ushort tag, byte anticipation, float settleTime = 0f) {
        if (anticipation == 0) {
            return;
        }

        var stamp = _stamps.Find(other => other.Tag == tag);
        if (stamp == null) {
            stamp = new AnticipationStamp(tag, anticipation);
            _stamps.Add(stamp);
        }

        stamp.Pending = anticipation;
        stamp.PendingSinceStep = MonoBehaviourUtil.FixedStep;
        stamp.PendingSettledAt = Time.unscaledTime + Mathf.Min(settleTime, AnticipationSettleCap);
    }

    /// <summary>
    /// Notes that a knockback of a scene client's, which this game plays for it as the scene host, pushed the entity
    /// this far on a step of physics, for the player of that client to tell the push apart from what the entity did by
    /// itself (see <see cref="AnticipationReport"/>).
    /// </summary>
    /// <param name="tag">The tag of that client's game.</param>
    /// <param name="push">How far it pushed the entity.</param>
    /// <returns>
    /// Whether it was noted: not once this game no longer runs the entity, or has nothing of that client's left to
    /// answer for.
    /// </returns>
    public bool NotePushed(ushort tag, Vector2 push) {
        if (_isControlled) {
            return false;
        }

        foreach (var stamp in _stamps) {
            if (stamp.Tag == tag) {
                stamp.Pushed += push;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Picks the tag of this game (see <see cref="RequestTag"/>). Zero is kept for none.
    /// </summary>
    private static ushort PickRequestTag() {
        var tag = BitConverter.ToUInt16(Guid.NewGuid().ToByteArray(), 0);
        return tag == 0 ? (ushort) 1 : tag;
    }

    /// <summary>
    /// How far the scene host has got through the things one scene client did to this entity (see
    /// <see cref="NoteAnticipation"/>).
    /// </summary>
    /// <param name="tag">The tag of that client's game.</param>
    /// <param name="since">The number of the first thing of that client's it was told of.</param>
    private sealed class AnticipationStamp(ushort tag, byte since) {
        public ushort Tag { get; } = tag;

        /// <summary>
        /// The number of the first thing of that client's this was told of, which <see cref="Pushed"/> counts from.
        /// </summary>
        public byte Since { get; } = since;

        /// <summary>
        /// How far the knockbacks of that client, which this game played for it, have pushed the entity since this was
        /// started (see <see cref="NotePushed"/>).
        /// </summary>
        public Vector2 Pushed { get; set; }

        /// <summary>
        /// The number of the last thing it did that the scene host has been told about but which nothing it has sent
        /// since can show yet, or zero when there is none.
        /// </summary>
        public byte Pending { get; set; }

        /// <summary>
        /// The step of physics the game was on when <see cref="Pending"/> was taken in. Nothing set in motion has
        /// moved until a step after that one.
        /// </summary>
        public uint PendingSinceStep { get; set; }

        /// <summary>
        /// When what <see cref="Pending"/> stands for has finished happening here, so that what is sent from then on
        /// has the whole of it in it rather than the first moment of it.
        /// </summary>
        public float PendingSettledAt { get; set; }

        /// <summary>
        /// The number of the last thing it did which what the scene host sends now has in it.
        /// </summary>
        public byte Incorporated { get; set; }

        /// <summary>
        /// How many more positions are sent for this entity whether it has moved or not, to carry what the scene host
        /// has taken in to the player who is waiting on it.
        /// </summary>
        public int SendsLeft { get; set; }

        /// <summary>
        /// Until when what the scene host has taken in is sent beside the positions of this entity.
        /// </summary>
        public float StampUntil { get; set; }
    }

    /// <summary>
    /// How the copy goes on while the local game waits on a knockback that it played first (see
    /// <see cref="BeginKnockbackAnticipation"/>).
    /// </summary>
    private sealed class KnockbackWait {
        /// <summary>
        /// Whether a position of the scene host has been heard since the wait began.
        /// </summary>
        public bool Heard { get; set; }

        /// <summary>
        /// How far the knockbacks of the local player have pushed the creature in the scene host's game since the wait
        /// began, by the positions heard.
        /// </summary>
        public Vector2 Pushed { get; set; }

        /// <summary>
        /// The number the scene host's count of the push that came with the last position heard is counted from, or
        /// zero when that position came with none (see <see cref="AnticipationReport.Since"/>).
        /// </summary>
        public byte PushedSince { get; set; }

        /// <summary>
        /// What that count said.
        /// </summary>
        public Vector2 PushedCount { get; set; }

        /// <summary>
        /// Where the creature was in the scene host's game without the local player's knockbacks, by the last position
        /// heard, or null when the copy has nothing to go on from: no position heard yet, or something else moved it.
        /// </summary>
        public Vector2? OwnFrom { get; set; }

        /// <summary>
        /// How fast the creature went by itself, by those positions.
        /// </summary>
        public Vector3 OwnVelocity { get; set; }

        /// <summary>
        /// How far the copy went on by itself meanwhile, all of it, for the [Knock] lines (CoopHits).
        /// </summary>
        public Vector2 OwnMoved { get; set; }
    }

    /// <summary>
    /// Forgets both sides of this, for an entity that is changing hands or starting again. A number from before the
    /// change means nothing after it: the game that would have answered it is not the one answering now.
    /// </summary>
    private void ResetAnticipation() {
        if (_knockbackWait != null) {
            NoteKnockbackWaitEnd(_knockbackWait, "the entity changed hands or started again");
        }

        _outstandingAnticipation = 0;
        _knockbackWait = null;
        _stamps.Clear();
        _wasMovedHere = false;
    }

    /// <summary>
    /// Whether the copy is moving by itself from how the entity set off (<see cref="OwnMotionComponent"/>).
    /// </summary>
    private bool MovesByItself() {
        return _components.TryGetValue(EntityComponentType.OwnMotion, out var component) &&
               component is OwnMotionComponent { IsMoving: true };
    }

    /// <summary>
    /// Whether the entity moves by itself here, in the scene host's game, from how it set off
    /// (<see cref="OwnMotionComponent"/>).
    /// </summary>
    private bool HostMovesByItself() {
        return _components.TryGetValue(EntityComponentType.OwnMotion, out var component) &&
               component is OwnMotionComponent { IsHostMoving: true };
    }

    /// <summary>
    /// Has how the entity moves by itself sent again, for a player who has just walked into the room: what was kept
    /// for them is where it last set off or stopped, and without this its copy waited there, not moving, until the
    /// next one (<see cref="OwnMotionComponent"/>).
    /// </summary>
    public void SendOwnMotionAgain() {
        if (!_isControlled && _components.TryGetValue(EntityComponentType.OwnMotion, out var component)) {
            ((OwnMotionComponent) component).MarkChanged();
        }
    }

    /// <summary>
    /// Whether something the local player did to this entity is still waiting to come back from the scene host,
    /// giving up on it once it has waited longer than anything can take.
    /// </summary>
    private bool IsAnticipating() {
        // The copy goes back to following the scene host the usual way, however much later the next position comes
        if (_outstandingAnticipation != 0 && Time.unscaledTime > _outstandingExpiry) {
            if (_knockbackWait != null) {
                NoteKnockbackWaitEnd(_knockbackWait, "it waited too long");
            }

            _outstandingAnticipation = 0;
            _knockbackWait = null;
        }

        return _outstandingAnticipation != 0;
    }

    /// <summary>
    /// Whether a position of the scene host may be taken while the local game is waiting for it to take in something
    /// the local player did to this entity.
    ///
    /// A position that says nothing is refused while there is something to wait for, because the whole point is
    /// that the ones sent before the scene host heard say nothing. So the only way out that does not depend on the
    /// scene host answering is the wait giving up, and that is why it gives up at all: a position that is a little
    /// old costs a little smoothing, while positions refused forever stop the entity dead, and a room of enemies
    /// once stood still for nine minutes on the wrong side of a gate like this one.
    /// </summary>
    /// <param name="stamps">What the scene host said it had taken in of what each game did, by the tag of that game,
    /// or null if it said nothing.</param>
    private bool AcceptsWhileAnticipating(IReadOnlyList<AnticipationReport>? stamps) {
        if (!IsAnticipating()) {
            return true;
        }

        if (stamps == null) {
            return false;
        }

        // Only what is under this game's own tag says anything of what this game did. Compared by the sign of the
        // difference in the size the numbers are kept in, so that the step from the largest back round to one reads
        // as one forward rather than as the whole way back
        foreach (var stamp in stamps) {
            if (stamp.Tag == RequestTag && stamp.Number != 0 &&
                (sbyte) (stamp.Number - _outstandingAnticipation) >= 0) {
                _outstandingAnticipation = 0;

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Updates the position of the client entity.
    /// </summary>
    /// <param name="position">The new position.</param>
    /// <param name="sequence">The sequence number of the packet it arrived in.</param>
    /// <param name="anticipation">
    /// How far the scene host had got through what the local player did to this entity when it sent this, or null if
    /// it didn't say.
    /// </param>
    public void UpdatePosition(
        Math_Vector3 position,
        ushort sequence,
        IReadOnlyList<AnticipationReport>? anticipation
    ) {
        // Newer when heard just before the state of the room (see YieldsToNewer)
        if (YieldsToNewer(HeardPosition)) {
            return;
        }

        // An older position arriving after a newer one is thrown away: nothing below this transport orders what it
        // carries, so one that had to be sent again lands after ones sent later, and applying it puts the entity
        // back where it was that much earlier.
        if (!_positionSequence.Accepts(sequence, out var packetsCovered)) {
            return;
        }

        if (Object.Client == null || Object.Host == null) {
            return;
        }

        // The depth comes from the scene host like the rest. It used to be this game's own room copy's, which is
        // wherever that copy stood when it was switched off - part way through a boss rising out of the scenery, say,
        // so that the boss fought on from deep in the background, small and blurred, for the whole fight
        var unityPos = new Vector3(position.X, position.Y, position.Z);

        // A part of another entity, like a head on the torso of a body, is carried by the copy of that body and moved
        // by its animation there, the way the body moves it in the scene host's game. Made a copy of its own instead,
        // it was put into the room by positions that came a few times a second and were guessed in between, beside a
        // body that the animation moved smoothly every frame: it shook.
        if (_hasParent) {
            return;
        }

        var positionInterpolation = Object.Client.GetComponent<PredictiveInterpolation>();
        if (positionInterpolation == null) {
            return;
        }

        // While something the local player did to this entity is still on its way to the scene host and back, the
        // positions it sends were measured before it had even heard. Letting those through was the whole of the
        // problem: the interpolation went on being told the enemy was standing where it had been, and pulled it
        // straight back there - in front of a player who had just hit it, in a game where walking into an enemy
        // hurts. They are counted and dropped until the scene host says it has taken that in, and the count goes
        // with the first position taken afterwards so that the speed read out of it covers the right stretch of
        // time rather than a single tick.
        //
        // For a knockback that the local game played first, they still say what the creature did by itself meanwhile,
        // which the copy goes on doing (GoOnWhileKnockedBack), and the first position taken is taken from where the
        // copy stands, going on at the speed the creature went by itself: the speed read out of the stretch of the
        // wait would have the whole of the knockback in it, and slid the copy on past where it stops.
        var knockbackWait = _knockbackWait;
        if (!AcceptsWhileAnticipating(anticipation)) {
            _positionsHeldBack += packetsCovered;
            if (knockbackWait != null) {
                GoOnWhileKnockedBack(knockbackWait, unityPos, anticipation, packetsCovered, positionInterpolation);
            }

            return;
        }

        if (knockbackWait != null) {
            _knockbackWait = null;
            GoOnWhileKnockedBack(knockbackWait, unityPos, anticipation, packetsCovered, positionInterpolation);
            NoteKnockbackWaitEnd(knockbackWait, "the scene host had it");
            positionInterpolation.Rebase(unityPos, knockbackWait.OwnVelocity);
            _positionsHeldBack = 0;

            return;
        }

        positionInterpolation.SetNewPosition(unityPos, _positionsHeldBack + packetsCovered);
        _positionsHeldBack = 0;
        StartWhereTheSceneHostDidAfterAll();
    }

    /// <summary>
    /// Moves the copy on by what the creature did by itself, as a position of the scene host's shows it that arrived
    /// while the local game waits on a knockback it played first (see <see cref="BeginKnockbackAnticipation"/>).
    /// </summary>
    /// <param name="wait">The wait.</param>
    /// <param name="position">The position.</param>
    /// <param name="reports">What the scene host said beside it of what each game did, or null if nothing.</param>
    /// <param name="packetsCovered">How many packets of the scene host's the step since the last position took.</param>
    /// <param name="interpolation">What moves the copy otherwise, which reads how fast it goes.</param>
    private void GoOnWhileKnockedBack(
        KnockbackWait wait,
        Vector3 position,
        IReadOnlyList<AnticipationReport>? reports,
        int packetsCovered,
        PredictiveInterpolation interpolation
    ) {
        AnticipationReport? report = null;
        if (reports != null) {
            foreach (var other in reports) {
                if (other.Tag == RequestTag) {
                    report = other;
                    break;
                }
            }
        }

        // How far the local player's knockbacks have pushed the creature over there since the wait began. A count
        // that is new since the last position started from nothing, and a position with none was sent while nothing of
        // the local player's was going on there, so that nothing of theirs moved it since the last one
        if (report is { } told) {
            var count = new Vector2(told.PushedX, told.PushedY);
            if (told.Since == wait.PushedSince) {
                wait.Pushed += count - wait.PushedCount;
            } else if (wait.Heard) {
                wait.Pushed += count;
            }

            wait.PushedSince = told.Since;
            wait.PushedCount = count;
        } else {
            wait.PushedSince = 0;
        }

        wait.Heard = true;

        // What is left is what the creature did by itself, which the copy does too, unless something else moves it
        // here: its own FSM run for the local player, how it set off, or a pull of the local player's
        var own = new Vector2(position.x, position.y) - wait.Pushed;
        if (_runHere != null || MovesByItself() || Time.time < _pulledHereUntil) {
            wait.OwnFrom = null;
            return;
        }

        if (wait.OwnFrom is { } from) {
            var step = own - from;
            MoveCopyBy(step);
            wait.OwnMoved += step;
            wait.OwnVelocity = interpolation.ReadVelocity(wait.OwnVelocity, step, packetsCovered);
        }

        wait.OwnFrom = own;
    }

    /// <summary>
    /// Moves the copy by a step the way its knockback moves it (Recoil): its body if it has one in the physics, which
    /// carries the object along on the next step of physics, and the object itself if not.
    /// </summary>
    private void MoveCopyBy(Vector2 step) {
        var copy = Object.Client;
        if (copy.activeInHierarchy && copy.TryGetComponent<Rigidbody2D>(out var body) && body.simulated) {
            body.position += step;
            return;
        }

        copy.transform.position += new Vector3(step.x, step.y, 0f);
    }

    /// <summary>
    /// Updates the scale of the client entity.
    /// </summary>
    /// <param name="scale">The new scale data.</param>
    /// <param name="sequence">The number of the packet it came in, or 0 for none.</param>
    public void UpdateScale(EntityUpdate.ScaleData scale, ushort sequence = 0) {
        // Newer when heard just before the state of the room (see YieldsToNewer). A turn alone, which rides along with
        // every position, says nothing of the size and does not count as one heard: while the creature moved, the state
        // of the room would never again put right a change of size that was lost
        if ((scale.HasSize || FromRoomState) && YieldsToNewer(HeardScale)) {
            return;
        }

        // An older scale arriving after a newer one is thrown away, as an older position is (UpdatePosition): with the
        // facing riding along with every position, one held back on the way would turn the copy back for a moment, and
        // a turn held back past the last of them would for good. What the state of a room says is not numbered by the
        // packet that carries it, and YieldsToNewer sees to it.
        if (!FromRoomState && !_scaleSequence.Accepts(sequence, out _)) {
            return;
        }

        if (scale.x) {
            _toldFacing = scale.xFlipped ? (scale.xPos ? 1f : -1f) : Mathf.Sign(scale.xScale);
            _toldFacingAt = Time.unscaledTime;
        }

        // The part of an FSM that each game runs by itself turns the copy to the local player (RunEachGamePart), and so
        // does a talk with them (TalkHere)
        if (Object.Client == null || _runHereForGood || _runHereTalk) {
            return;
        }

        // Held for as long as the copy's FSM runs here (PlayHere), as its positions are not taken meanwhile either: what
        // the scene host sends is from before the input, or of its own creature, which may face elsewhere, and now that
        // the facing rides along with every position it would turn the copy back from what its own FSM turned it to,
        // frame after frame. It is taken once the copy follows the scene host again.
        if (_runHere != null) {
            _heldScale ??= new EntityUpdate.ScaleData();
            _heldScale.TakeNewer(scale);
            return;
        }

        var transform = Object.Client.transform;
        var localScale = transform.localScale;

        if (scale.x) {
            if (scale.xFlipped) {
                var currentScaleX = localScale.x;

                if (currentScaleX > 0 != scale.xPos) {
                    currentScaleX *= -1;

                    localScale.x = currentScaleX;
                }
            } else {
                localScale.x = scale.xScale;
            }
        }

        if (scale.y) {
            if (scale.yFlipped) {
                var currentScaleY = localScale.y;

                if (currentScaleY > 0 != scale.yPos) {
                    currentScaleY *= -1;

                    localScale.y = currentScaleY;
                }
            } else {
                localScale.y = scale.yScale;
            }
        }

        if (scale.z) {
            if (scale.zFlipped) {
                var currentScaleZ = localScale.z;

                if (currentScaleZ > 0 != scale.zPos) {
                    currentScaleZ *= -1;

                    localScale.z = currentScaleZ;
                }
            } else {
                localScale.z = scale.zScale;
            }
        }

        // Only when it changes: the facing that rides along with every position mostly says what the copy has already
        if (transform.localScale != localScale) {
            transform.localScale = localScale;
        }
    }

    /// <summary>
    /// Updates the animation of the client entity.
    /// </summary>
    /// <param name="animationId">The ID of the animation.</param>
    /// <param name="wrapMode">The wrap mode of the animation clip.</param>
    /// <param name="alreadyInSceneUpdate">Whether this update is when entering a new scene.</param>
    /// <param name="packet">The packet that brought it, or 0 for one that came in none.</param>
    public void UpdateAnimation(
        byte animationId,
        tk2dSpriteAnimationClip.WrapMode wrapMode,
        bool alreadyInSceneUpdate,
        ushort packet = 0
    ) {
        if (_animator.Client == null) {
            return;
        }

        if (!_animationClipNameIds.TryGetValue(animationId, out var clipName)) {
            return;
        }

        // From before the clip shown, which came in a newer packet (Entity.Poses)
        if (IsLatePose(packet, clipName)) {
            return;
        }

        // The state of a room this game is in already plays only a clip the copy is not playing: played again, the
        // clip it is in the middle of would start over or jump to its end
        if (FromRoomState && _animator.Client != null && _animator.Client.CurrentClip?.name == clipName ||
            YieldsToNewer(HeardClip)) {
            return;
        }

        // Played by the copy's own FSM until the scene host has answered its input, through the combo of a catch, and
        // through a talk of the local player, and held till then (PlayHere, TalkHere)
        if (WaitsForEcho || _runHereCombo != null || _runHereTalk) {
            _heldAnimation = (animationId, wrapMode);
            return;
        }

        // All paths lead to calling the Play method of the sprite animator that is hooked, so we allow the call
        // through the hook
        _allowClientAnimation = true;
        _copyPosePacket = packet;

        if (alreadyInSceneUpdate) {
            // Since this is an animation update from an entity that was already present in a scene,
            // we need to determine where to start playing this specific animation
            LateUpdateAnimation(_animator.Client, clipName, wrapMode);
        }

        // Otherwise, default to just playing the clip
        _animator.Client.Play(clipName);
    }

    /// <summary>
    /// Update the animation for the given animator with the given clip name and wrap mode. This assumes that we need
    /// to replicate animation behaviour for a late update.
    /// </summary>
    /// <param name="animator">The sprite animator to update.</param>
    /// <param name="clipName">The name of the animation clip.</param>
    /// <param name="wrapMode">The wrap mode for the animation.</param>
    private void LateUpdateAnimation(
        tk2dSpriteAnimator animator,
        string? clipName,
        tk2dSpriteAnimationClip.WrapMode wrapMode
    ) {
        if (wrapMode == tk2dSpriteAnimationClip.WrapMode.Loop) {
            animator.Play(clipName);
            return;
        }

        var clip = animator.GetClipByName(clipName);

        switch (wrapMode) {
            case tk2dSpriteAnimationClip.WrapMode.LoopSection:
                // The clip loops in a specific section in the frames, so we start playing
                // it from the start of that section
                animator.PlayFromFrame(clipName, clip.loopStart);
                return;
            case tk2dSpriteAnimationClip.WrapMode.Once or tk2dSpriteAnimationClip.WrapMode.Single: {
                // Since the clip was played once, it stops on the last frame,
                // so we emulate that by only "playing" the last frame of the clip
                var clipLength = clip.frames.Length;
                animator.PlayFromFrame(clipName, clipLength - 1);

                break;
            }
        }
    }

    /// <summary>
    /// Updates whether the game object for the client entity is active.
    /// </summary>
    /// <param name="active">The new value for active.</param>
    public void UpdateIsActive(bool active) {
        if (YieldsToNewer(HeardActive)) {
            return;
        }

        _sceneHostHasItOn = active;
        _timesToldIfOn++;
        _lastToldIfOn = Time.unscaledTime;

        // Each word from the scene host starts the wait of SayWhyTheCopyIsOff over, so a copy that stays off anyway
        // is said again
        _copyOffSince = -1f;
        _saidTheCopyIsOff = false;

        if (Object.Client != null) {
            if (Object.Client.TryGetComponent<SaysWhoSwitchesTheCopyOff>(out var says)) {
                says.Caller = null;
            } else if (active) {
                Object.Client.AddComponent<SaysWhoSwitchesTheCopyOff>().Entity = this;
            }

            Object.Client.SetActive(active);
        }
    }

    /// <summary>
    /// Updates generic data for the client entity.
    /// </summary>
    /// <param name="entityNetworkData">A list of data to update the client entity with.</param>
    /// <param name="alreadyInSceneUpdate">Whether this data is from an already in scene update.</param>
    public void UpdateData(List<EntityNetworkData> entityNetworkData, bool alreadyInSceneUpdate) {
        foreach (var data in entityNetworkData) {
            if (data.Type == EntityComponentType.Echo) {
                HearEcho(data);
                continue;
            }

            // What plays something out once - a prompt to a challenge, a spawner moving into place, a jar breaking, a
            // platform showing - is played on walking into the room, and not again for a room this game is in
            if (FromRoomState && data.Type is EntityComponentType.ChallengePrompt or EntityComponentType.EnemySpawner
                    or EntityComponentType.SpawnJar or EntityComponentType.DreamPlatform) {
                continue;
            }

            // Newer when heard just before the state of the room (see YieldsToNewer); a death is a death either way
            if (data.Type != EntityComponentType.Health && YieldsToNewer((int) data.Type)) {
                continue;
            }

            if (data.Type == EntityComponentType.Fsm) {
                PlayMakerFSM fsm;
                var fsmIndex = 0;

                if (_fsms.Client == null) {
                    continue;
                }

                if (_fsms.Client.Count > 1) {
                    // Do a check on the length of the data
                    if (data.Packet.Length < 3) {
                        continue;
                    }

                    fsmIndex = data.Packet.ReadByte();
                    if (fsmIndex >= _fsms.Client.Count) {
                        continue;
                    }

                    fsm = _fsms.Client[fsmIndex];
                } else {
                    // Do a check on the length of the data
                    if (data.Packet.Length < 2) {
                        continue;
                    }

                    if (_fsms.Client.Count == 0) {
                        continue;
                    }

                    fsm = _fsms.Client[0];
                }

                if (fsm == null || fsm.FsmStates == null) {
                    continue;
                }

                var stateIndex = data.Packet.ReadByte();
                var actionIndex = data.Packet.ReadByte();

                if (stateIndex >= fsm.FsmStates.Length) {
                    continue;
                }

                var state = fsm.FsmStates[stateIndex];
                if (state?.Actions == null || actionIndex >= state.Actions.Length) {
                    continue;
                }

                var action = state.Actions[actionIndex];

                // What became of the ring that a stun puts over the creature's head (SayWhereTheDazeRingIsSoon)
                var ringSpawn = IsDazeRingSpawn(action);
                if (ringSpawn) {
                    NoteDazeRingSpawn("came from the scene host and was not played here");
                }

                // Of a move that the copy is about to play by itself, from what came along (StartMoveHeard)
                if (HoldsForTheMoveHeard(fsm, state, data)) {
                    continue;
                }

                // Played here by the copy's own FSM until the scene host has answered its input, and held till then:
                // all but what the copy leaves to the scene host, which only comes from there (PlayHere) - unless that
                // works on a thing that something held makes, and waits for it (HoldsWithWhatMadeIt). The combo of
                // a catch is played here all through, and not again from what the scene host sends of it, and so is
                // the part of an FSM that each game runs by itself (RunEachGamePart). What comes after what the copy
                // plays by itself out of a catch that this game led is held until it has played that
                // (HoldsForTheAftermath), and so is what comes after a move that the copy plays on to its own end of
                // once the scene host's FSM came out of it (HoldsForTheMoveEnd).
                HearFromSceneHost(fsm, state);
                if (fsm == _runHere) {
                    var muted = _mutedHere.Contains(action);

                    // What only the scene host does in a state of a move that the copy plays by itself is done here as
                    // the copy gets to that state (WaitsForTheCopy)
                    if (muted && WaitsForTheCopy(state)) {
                        HoldForTheCopy(state, data);
                        continue;
                    }

                    // And while it runs here for a talk of the local player, until the talk is over (TalkHere)
                    if ((!muted || HoldsWithWhatMadeIt(action)) &&
                        (WaitsForEcho || HoldsForTheAftermath(state) || HoldsForTheMoveEnd(state) || _runHereTalk)) {
                        HoldForEcho(data, action);
                        continue;
                    }

                    if (!muted && (_runHereForGood || _runHereCombo?.Contains(state) == true)) {
                        continue;
                    }
                }

                // Nor the death that a boss dies in that part while it waits for the local player to be stood up or
                // to come into the room: the copy dies it there itself once the part starts, from its first state
                if (_eachGamePartWaiting is { } waiting && waiting.Fsm == fsm && waiting.Part.Contains(state) &&
                    EntityFsmActions.IsSimulatedDeath(action)) {
                    continue;
                }

                EntityFsmActions.ReadSubject(data, action);
                EntityFsmActions.ApplyNetworkDataFromAction(data, action);

                if (ringSpawn) {
                    NoteDazeRingSpawnMade((SpawnObjectFromGlobalPool) action);
                }

                continue;
            }

            // How the scene host switches its creature in a move that the copy plays by itself - drawn, turned, on
            // which layer, whether it can be hit or hurts, how it moves by itself - waits for the copy to get to the
            // state of the move that the scene host's FSM is at (WaitsForTheCopy), and once that came out of the move
            // first, for the copy to be through it, which it comes after (see _moveFinishBy). Not its health or death,
            // which are the scene host's to settle as they come, nor the music of the room. A boss that rose out of the
            // lava turned back as it came out, which came here before the copy's own rise turned it: it rose here and
            // stayed on its side
            if (!_takingWhatWaited && data.Type is not (EntityComponentType.Health or EntityComponentType.Death or
                    EntityComponentType.Music)) {
                // So does what came along with the start of the move, where the scene host's FSM was heard to be
                // further into it than its first state, for the move to start (HoldsSwitchForTheMoveHeard)
                if (HoldsSwitchForTheMoveHeard(data)) {
                    continue;
                }

                if (_runHereMove && _moveFinishBy != null) {
                    HoldForEcho(data);
                    continue;
                }

                if (_runHereMove && _moveHostAt is { } hostAt && WaitsForTheCopy(hostAt)) {
                    HoldForTheCopy(hostAt, data);
                    continue;
                }
            }

            // How the entity moves by itself, the colours its FSMs leave it in, and whether the damage of its parts is
            // on, are its own FSM's to say while that runs here, and held until the scene host has answered the input
            // (PlayHere)
            if ((data.Type is EntityComponentType.OwnMotion or EntityComponentType.Tint or
                    EntityComponentType.DamageHero) && WaitsForEcho) {
                HoldForEcho(data);
                continue;
            }

            // The colours are the copy's own while it runs the part of an FSM that each game runs by itself, or waits
            // to: the scene host's run of that part colours the scene host's creature (RunEachGamePart)
            if (data.Type == EntityComponentType.Tint && (_runHereForGood || _eachGamePartWaiting != null)) {
                continue;
            }

            if (_components.TryGetValue(data.Type, out var component)) {
                component.Update(data, alreadyInSceneUpdate);
            }
        }
    }

    /// <summary>
    /// Update the FSMs of the host entity to prepare for host transfer or disconnects.
    /// </summary>
    /// <param name="hostFsmData">Dictionary mapping FSM index to data.</param>
    public void UpdateHostFsmData(Dictionary<byte, EntityHostFsmData> hostFsmData) {
        foreach (var (fsmIndex, data) in hostFsmData) {
            // Newer when heard just before the state of the room (see YieldsToNewer)
            if (YieldsToNewer(HeardFsmVariables + fsmIndex)) {
                continue;
            }

            if (_fsms.Host.Count <= fsmIndex) {
                continue;
            }

            if (_fsms.Client == null || _fsms.Client.Count <= fsmIndex || _fsms.Client[fsmIndex] == null) {
                continue;
            }

            var hostFsm = _fsms.Host[fsmIndex];
            var snapshot = _fsmSnapshots[fsmIndex];

            if (data.Types.Contains(EntityHostFsmData.Type.State)) {
                var states = hostFsm.FsmStates;
                if (data.CurrentState == byte.MaxValue) {
                    // No state at all, which is how the scene host sends a state it cannot find: its FSM stopped, as it
                    // does when its creature is switched off or dies. The game's FSM leaves the state it was in as it
                    // stops, which ends what that state was doing, and so does the copy. A creature that died sucking
                    // silk out of a player went on drawing the silk stream where it died, and what the stream did
                    // every frame was never taken off. Only that number means it: the FSM here can be a stand-in with
                    // fewer states than the scene host's (Entity.WaitForTheFsmToBeMade)
                    snapshot.CurrentState = "";
                    EntityFsmActions.RegisterStateChange(_fsms.Client[fsmIndex].Fsm, "");
                } else if (states.Length > data.CurrentState) {
                    var stateName = states[data.CurrentState].Name;

                    SayStunned(hostFsm, snapshot.CurrentState, stateName, false);
                    snapshot.CurrentState = stateName;

                    // Also propagate this state change to the EntityFsmActions class with the client FSM for the
                    // same index
                    EntityFsmActions.RegisterStateChange(_fsms.Client[fsmIndex].Fsm, stateName);
                    HearStateFromSceneHost(_fsms.Client[fsmIndex], stateName);
                }
            }

            // What the scene host's FSM held before it took the local player's input is not written into the copy's
            // FSM that runs here for it meanwhile, which has gone on from there: the copy is given it only if the
            // scene host went another way (PlayHere). Nor is anything written into the part that each game runs by
            // itself, whose count of binds left is the local player's (RunEachGamePart), nor into a catch that this
            // game leads, whose count of struggles is (_runHereLed), nor into a move that the copy plays by itself,
            // whose clock and place are its own (RunMoveHere).
            var clientFsm = _fsms.Client[fsmIndex];
            var writesCopy = clientFsm != _runHere ||
                             !WaitsForEcho && !_runHereForGood && !_runHereLed && !_runHereTalk && !_runHereMove;

            if (data.Types.Contains(EntityHostFsmData.Type.Floats)) {
                foreach (var (index, val) in data.Floats) {
                    if (index < hostFsm.FsmVariables.FloatVariables.Length) {
                        hostFsm.FsmVariables.FloatVariables[index].Value = val;
                        snapshot.Floats[index] = val;
                    }

                    if (writesCopy && index < clientFsm.FsmVariables.FloatVariables.Length) {
                        clientFsm.FsmVariables.FloatVariables[index].Value = val;
                        EntityFsmActions.NoteHostWrote(clientFsm.FsmVariables.FloatVariables[index]);
                    }
                }
            }

            if (data.Types.Contains(EntityHostFsmData.Type.Ints)) {
                foreach (var (index, val) in data.Ints) {
                    if (index < hostFsm.FsmVariables.IntVariables.Length) {
                        hostFsm.FsmVariables.IntVariables[index].Value = val;
                        snapshot.Ints[index] = val;
                    }

                    if (writesCopy && index < clientFsm.FsmVariables.IntVariables.Length) {
                        clientFsm.FsmVariables.IntVariables[index].Value = val;
                        EntityFsmActions.NoteHostWrote(clientFsm.FsmVariables.IntVariables[index]);
                    }
                }
            }

            if (data.Types.Contains(EntityHostFsmData.Type.Bools)) {
                foreach (var (index, val) in data.Bools) {
                    if (index < hostFsm.FsmVariables.BoolVariables.Length) {
                        hostFsm.FsmVariables.BoolVariables[index].Value = val;
                        snapshot.Bools[index] = val;
                    }

                    if (writesCopy && index < clientFsm.FsmVariables.BoolVariables.Length) {
                        clientFsm.FsmVariables.BoolVariables[index].Value = val;
                        EntityFsmActions.NoteHostWrote(clientFsm.FsmVariables.BoolVariables[index]);
                    }
                }
            }

            if (data.Types.Contains(EntityHostFsmData.Type.Strings)) {
                foreach (var (index, val) in data.Strings) {
                    if (index < hostFsm.FsmVariables.StringVariables.Length) {
                        hostFsm.FsmVariables.StringVariables[index].Value = val;
                        snapshot.Strings[index] = val;
                    }

                    if (writesCopy && index < clientFsm.FsmVariables.StringVariables.Length) {
                        clientFsm.FsmVariables.StringVariables[index].Value = val;
                        EntityFsmActions.NoteHostWrote(clientFsm.FsmVariables.StringVariables[index]);
                    }
                }
            }

            if (data.Types.Contains(EntityHostFsmData.Type.Vector2s)) {
                foreach (var (index, val) in data.Vec2s) {
                    if (index < hostFsm.FsmVariables.Vector2Variables.Length) {
                        hostFsm.FsmVariables.Vector2Variables[index].Value = (Vector2) val;
                        snapshot.Vector2s[index] = (Vector2) val;
                    }

                    if (writesCopy && index < clientFsm.FsmVariables.Vector2Variables.Length) {
                        clientFsm.FsmVariables.Vector2Variables[index].Value = (Vector2) val;
                        EntityFsmActions.NoteHostWrote(clientFsm.FsmVariables.Vector2Variables[index]);
                    }
                }
            }

            if (data.Types.Contains(EntityHostFsmData.Type.Vector3s)) {
                foreach (var (index, val) in data.Vec3s) {
                    if (index < hostFsm.FsmVariables.Vector3Variables.Length) {
                        hostFsm.FsmVariables.Vector3Variables[index].Value = (Vector3) val;
                        snapshot.Vector3s[index] = (Vector3) val;
                    }

                    if (writesCopy && index < clientFsm.FsmVariables.Vector3Variables.Length) {
                        clientFsm.FsmVariables.Vector3Variables[index].Value = (Vector3) val;
                        EntityFsmActions.NoteHostWrote(clientFsm.FsmVariables.Vector3Variables[index]);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Destroys the entity.
    /// </summary>
    public void Destroy() {
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
        MonoBehaviourUtil.Instance.OnLateUpdateEvent -= OnLateUpdate;
        _heldUntilPlaced = null;

        // What the entity was doing to the local player is undone first. Its hooks and parts go whatever happens there:
        // left behind, they would go on acting for a room that is gone
        try {
            LetGoOfRunHere();
            EndAllLeads();
            LetThePlayerBackIn(true);
        } finally {
            ForgetIn(EntitiesByCopy, Object.Client);
            ForgetIn(EntitiesByRoomObject, Object.Host);

            // An FSM that was still to be made is no longer this entity's to hook
            foreach (var fsm in _fsms.Host) {
                if (FsmsMadeLater.TryGetValue(fsm, out var waiting) && waiting == this) {
                    FsmsMadeLater.Remove(fsm);
                }
            }

            ForgetIn(EntitiesByAnimator, _animator.Host);
            ForgetIn(EntitiesByAnimator, _animator.Client);

            foreach (var component in _components.Values.Distinct()) {
                component.Destroy();
            }

            GamePatcher.OnEntityDestroyed(Object.Host);
            GamePatcher.OnEntityDestroyed(Object.Client);
        }
    }

    /// <summary>
    /// Takes the entry of an object out of one of the lookups of entities if it is this entity's: an entity made for
    /// the same object since keeps its own.
    /// </summary>
    /// <param name="lookup">The lookup.</param>
    /// <param name="key">The object, or null.</param>
    private void ForgetIn<TKey>(Dictionary<TKey, Entity> lookup, TKey? key) where TKey : UnityEngine.Object {
        if (key is not null && lookup.TryGetValue(key, out var entity) && entity == this) {
            lookup.Remove(key);
        }
    }

    /// <summary>
    /// Get the list of client FSMs.
    /// </summary>
    /// <returns>A list containing the client FSM instances.</returns>
    public List<PlayMakerFSM> GetClientFsms() {
        return _fsms.Client;
    }
}
