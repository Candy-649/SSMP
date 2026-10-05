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

//using Logger = SSMP.Logging.Logger;

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
    /// MonoMod hook for tk2dSpriteAnimator.Play.
    /// </summary>
    private Hook? _spriteAnimatorPlayHook;

    /// <summary>
    /// MonoMod hook for ObjectPool.Recycle.
    /// </summary>
    private Hook? _objectPoolRecycleHook;

    /// <summary>
    /// MonoMod hook for ActivateGameObject.OnEnter.
    /// </summary>
    private Hook? _activateGameObjectHook;

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
    /// player did to this entity still waiting to come back from the scene host, or the copy moving by itself
    /// (<see cref="OwnMotionComponent"/>) - which says when that has just stopped and the interpolation is about to
    /// take the entity over again.
    /// </summary>
    private bool _wasMovedHere;

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
    /// The number of the last thing a scene client did to this entity that the scene host has been told about but
    /// which nothing it has sent since can show yet, or zero when there is none.
    /// </summary>
    private byte _pendingAnticipation;

    /// <summary>
    /// The step of physics the game was on when <see cref="_pendingAnticipation"/> was taken in. Nothing set in
    /// motion has moved until a step after that one.
    /// </summary>
    private uint _pendingSinceStep;

    /// <summary>
    /// When what <see cref="_pendingAnticipation"/> stands for has finished happening here, so that what is sent
    /// from then on has the whole of it in it rather than the first moment of it.
    /// </summary>
    private float _pendingSettledAt;

    /// <summary>
    /// The number of the last thing a scene client did to this entity which what the scene host sends now has in it.
    /// </summary>
    private byte _incorporatedAnticipation;

    /// <summary>
    /// How many more positions are sent for this entity whether it has moved or not, to carry what the scene host
    /// has taken in to a player who is waiting on it.
    /// </summary>
    private int _anticipationSendsLeft;

    /// <summary>
    /// Until when what the scene host has taken in is sent beside the positions of this entity.
    /// </summary>
    private float _anticipationStampUntil;

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
    /// Dictionary mapping data types to entity components.
    /// </summary>
    private readonly Dictionary<EntityComponentType, EntityComponent> _components;

    /// <summary>
    /// Unique list of components that require periodic update calls on the host.
    /// </summary>
    private readonly List<EntityComponent> _updatableComponents;

    /// <summary>
    /// Dictionary mapping FSM actions to their entity action data instances.
    /// </summary>
    private readonly Dictionary<FsmStateAction, HookedEntityAction> _hookedActions;

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

        Object.Client.transform.localScale = _lastScale = _hasParent
            ? Object.Host.transform.localScale
            : Object.Host.transform.lossyScale;

        // Store whether the host object was active, and unless it keeps running, set it not active until we know if we
        // are scene host
        _originalIsActive = Object.Host.activeSelf;

        _lastIsActive = _hasParent ? Object.Host.activeSelf : Object.Host.activeInHierarchy;

        //Logger.Info(
        //    $"Entity '{Object.Host.name}' was original active: {_originalIsActive}, last active: {_lastIsActive}"
        //);

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
                    //Logger.Error($"Too many animation clips to fit in a byte for entity: {Object.Client.name}");
                    break;
                }
            }

            _spriteAnimatorPlayHook = new Hook(
                typeof(tk2dSpriteAnimator).GetMethod(
                    nameof(tk2dSpriteAnimator.Play),
                    [typeof(tk2dSpriteAnimationClip), typeof(float), typeof(float)]
                ),
                OnAnimationPlayed
            );
        }

        // Always disallow the client object from being recycled, because it will simply be destroyed
        _objectPoolRecycleHook = new Hook(
            typeof(ObjectPool).GetMethod(
                nameof(ObjectPool.Recycle),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
                null,
                [typeof(GameObject)],
                null
            ),
            ObjectPoolOnRecycleGameObject
        );

        // Register a hook for the ActivateGameObject action to update the active state of the host game object
        // before scene host is determined
        _activateGameObjectHook = new Hook(
            typeof(ActivateGameObject).GetMethod(
                nameof(ActivateGameObject.OnEnter),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            ),
            OnDoActivateGameObject
        );

        _fsms = new HostClientPair<List<PlayMakerFSM>> {
            Host = Object.Host.GetComponents<PlayMakerFSM>().ToList(),
            Client = Object.Client.GetComponents<PlayMakerFSM>().ToList()
        };

        EntitiesByCopy[Object.Client] = this;
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

        _components = new Dictionary<EntityComponentType, EntityComponent>();
        HandleComponents(types);

        HandleEnemyDeathEffects();

        _updatableComponents = new List<EntityComponent>();
        foreach (var component in _components.Values) {
            if (component != null &&
                component is not HealthManagerComponent &&
                !_updatableComponents.Contains(component)) {
                _updatableComponents.Add(component);
            }
        }

        if (!_keepsRunning) {
            RememberWhereTheHostFsmsAre();
            Object.Host.SetActive(false);
        }

        Object.Client.SetActive(false);

        // // Debug code that logs each action's OnEnter method call
        // foreach (var fsm in _fsms.Host) {
        //     foreach (var state in fsm.FsmStates) {
        //         foreach (var action in state.Actions) {
        //             FsmActionHooks.RegisterFsmStateActionType(action.GetType(), stateAction => {
        //                 if (stateAction != action) {
        //                     return;
        //                 }
        //
        //                 Logger.Debug($"Entity ({Id}, {Type}) has host FSM enter action: {state.Name}, {action.GetType()}, {state.Actions.ToList().IndexOf(action)}");
        //             });
        //         }
        //     }
        // }
    }

    /// <summary>
    /// Destroy the children of the given game object that are registered entities in the system themselves.
    /// Recursively go through the non-registered children as well.
    /// </summary>
    /// <param name="root">The root game object to start searching for children.</param>
    private void DestroyManagedChildren(GameObject root) {
        foreach (var child in root.GetChildren()) {
            if (EntityRegistry.TryGetEntry(child, out _ /*var entry*/)) {
                //Logger.Debug($"Found managed child: {child.name}, {entry.Type}, destroying it");
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
        //Logger.Info($"Processing host FSM: {fsm.Fsm.Name}");

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
            //var stateName = state.Name;

            for (var j = 0; j < state.Actions.Length; j++) {
                var action = state.Actions[j];
                if (!action.Enabled) {
                    continue;
                }

                if (!EntityFsmActions.SupportedActionTypes.Contains(action.GetType())) {
                    continue;
                }

                if (action.Fsm == null) {
                    //Logger.Error(
                    //    $"FSM in action for state ({i}, {state.Name}), action ({j}, {action.GetType()}) is null"
                    //);
                    continue;
                }

                _hookedActions[action] = new HookedEntityAction {
                    Action = action,
                    FsmIndex = _fsms.Host.IndexOf(fsm),
                    StateIndex = i,
                    ActionIndex = j
                };
                //Logger.Info(
                //    $"Created hooked action: {action.GetType()}, {_fsms.Host.IndexOf(fsm)}, {stateName}, {j}"
                //);

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
        //Logger.Info($"Processing client FSM: {fsm.Fsm.Name}");
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
        //var addedComponentsString = $"Adding components to entity ({Object.Host.name}, {Id}):";

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
            );
            _components[EntityComponentType.Death] = hmComponent;
            _components[EntityComponentType.Health] = hmComponent;
            _components[EntityComponentType.Invincibility] = hmComponent;

            // Check if the object from the health manager is in any of the colosseum trial scenes and remove the
            // geo drops from them if so
            var goScene = hostHealthManager.gameObject.scene.name;
            if (goScene is "Room_Colosseum_Bronze" or "Room_Colosseum_Silver" or "Room_Colosseum_Gold") {
                clientHealthManager.SetGeoSmall(0);
                clientHealthManager.SetGeoMedium(0);
                clientHealthManager.SetGeoLarge(0);
            }

            //addedComponentsString += " Death Health Invincibility";
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

            //addedComponentsString += " Climber Rotation";
        }

        var hostCollider = Object.Host.GetComponent<Collider2D>();
        var clientCollider = Object.Client.GetComponent<Collider2D>();
        if (hostCollider != null && clientCollider != null) {
            //Logger.Info($"Adding collider component to entity: {Object.Host.name}");

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

            //addedComponentsString += " Collider";
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
            //Logger.Info($"Adding DamageHero component to entity: {Object.Host.name}");

            _components[EntityComponentType.DamageHero] = new DamageHeroComponent(
                _netClient,
                Id,
                Object,
                hostDamageHeroes,
                clientDamageHeroes
            );

            //addedComponentsString += " DamageHero";
        }

        var hostMeshRenderer = Object.Host.GetComponent<MeshRenderer>();
        var clientMeshRenderer = Object.Client.GetComponent<MeshRenderer>();
        if (hostMeshRenderer != null && clientMeshRenderer != null) {
            //Logger.Info($"Adding MeshRenderer component to entity: {Object.Host.name}");

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

            //addedComponentsString += " MeshRenderer";
        }

        EntityInitializer.RemoveClientTypes(Object.Client, Type);

        // Instantiate all types defined in the entity registry, which are passed to the constructor
        foreach (var type in types) {
            var component = ComponentFactory.InstantiateByType(type, _netClient, Id, Object);
            if (component == null) {
                //Logger.Debug($"Could not instantiate component for type: {type}");
            } else {
                _components[type] = component;
            }

            //addedComponentsString += $" {type}";
        }

        //Logger.Debug(addedComponentsString);
    }

    /// <summary>
    /// Handle specifics for a set of enemies that rely on EnemyDeathEffects for additional enemies.
    /// </summary>
    private void HandleEnemyDeathEffects() {
        // Hollow Knight specific death effects. Commented out since Silksong has different entities.
        /*
        string corpseName;
        switch (Type) {
            case EntityType.Ooma:
                corpseName = "Corpse Jellyfish(Clone)";
                break;
            case EntityType.Flukemon:
                corpseName = "Corpse Flukeman(Clone)";
                break;
            case EntityType.HuskHornhead:
                corpseName = "Zombie Spider 2(Clone)";
                break;
            case EntityType.WanderingHusk:
                corpseName = "Zombie Spider 1(Clone)";
                break;
            case EntityType.DungDefender:
                corpseName = "Corpse Dung Defender(Clone)";
                break;
            case EntityType.BrokenVessel:
                corpseName = "Corpse Infected Knight(Clone)";
                break;
            case EntityType.LostKin:
                corpseName = "Corpse Infected Knight Dream(Clone)";
                break;
            default:
                return;
        }

        Logger.Debug($"Entity ({Id}, {Type}) has corpse that is also enemy, deleting death effects and corpse from client entity");

        var enemyDeathEffects = Object.Client.GetComponent<EnemyDeathEffects>();
        if (enemyDeathEffects == null) {
            Logger.Debug("  EnemyDeathEffects is null, cannot remove");
        }
        UnityEngine.Object.Destroy(enemyDeathEffects);

        var corpse = Object.Client.FindGameObjectInChildren(corpseName);
        if (corpse != null) {
            Logger.Debug($"  Destroying corpse of client object: {corpse.name}");
            UnityEngine.Object.Destroy(corpse);
        } else {
            Logger.Debug("  Could not find corpse of client object");
        }
        */
    }

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
        //
        //Logger.Info(
        //    $"Entity ({Id}, {Type}) hooked action: {self.Fsm.Name}, {self.State.Name}, {self.GetType()} ({hookedEntityAction.FsmIndex}, {hookedEntityAction.StateIndex}, {hookedEntityAction.ActionIndex})"
        //);

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
        if (EntityFsmActions.GetNetworkDataFromAction(networkData, self)) {
            _netClient.UpdateManager.AddEntityData(Id, networkData);
        }
    }

    /// <summary>
    /// Sends the state that an FSM of the host is in, if it is not the one sent last.
    /// </summary>
    /// <param name="fsmIndex">The index of the FSM.</param>
    private void SendStateChange(int fsmIndex) {
        var fsm = _fsms.Host[fsmIndex];
        var snapshot = _fsmSnapshots[fsmIndex];
        if (fsm.ActiveStateName == snapshot.CurrentState) {
            return;
        }

        // The boss fell into the part of its FSM that each game runs by itself, in this game that runs it
        if (EntityRegistry.TryGetEachGameFrom(Type, fsm.FsmName, out var eachGameFrom) &&
            eachGameFrom == fsm.ActiveStateName) {
            EachGamePartBegan?.Invoke();
        }

        var data = ObjectPool<EntityHostFsmData>.Get();
        snapshot.CurrentState = fsm.ActiveStateName;
        data.Types.Add(EntityHostFsmData.Type.State);
        data.CurrentState = (byte) Array.IndexOf(fsm.FsmStates, fsm.Fsm.ActiveState);

        _netClient.UpdateManager.AddEntityHostFsmData(Id, (byte) fsmIndex, data);
        ObjectPool<EntityHostFsmData>.Return(data);
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
        if (_isControlled) {
            HideTheRoomsOwnCopy();
        }
    }

    /// <summary>
    /// Callback method for handling updates.
    /// </summary>
    [SuppressMessage("ReSharper", "CompareOfFloatsByEqualityOperator")]
    private void OnUpdate() {
        if (_putFsmsBackWhereTheyWere) {
            _putFsmsBackWhereTheyWere = false;
            PutHostFsmsBackWhereTheyWere();
        }

        if (Object.Host == null) {
            if (_lastIsActive) {
                // If the host object was active, but now it null (or destroyed in Unity), we can send
                // to the server that the entity can be regarded as inactive
                //Logger.Info(
                //    Object.Client == null
                //        ? $"Entity ({Id}, {Type}) host and client object is null (or destroyed) and was active"
                //        : $"Entity '{Object.Client.name}' host object is null (or destroyed) and was active"
                //);

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

            if (Object.Client != null &&
                Object.Client.TryGetComponent<PredictiveInterpolation>(out var interpolation)) {
                interpolation.AdaptToRTT(_netClient.UpdateManager.AverageRtt);

                // While something the local player did to this entity is still on its way to the scene host and
                // back, what the local game did is what moves it, and the interpolation stays out of the way. So it
                // does while the copy moves by itself from how the entity set off (OwnMotionComponent), or while its
                // own FSM runs here for something the local player did (PlayHere).
                var movedHere = IsAnticipating() || MovesByItself() || _runHere != null;
                if (_wasMovedHere && !movedHere) {
                    // Nothing wrote this object while that was going on, so the interpolation carried on predicting
                    // from where the entity stood before any of it. Picking that up again would put it back there in
                    // a single frame, which is the whole of what the wait was for, so where it is standing now is
                    // handed over as what it should go on looking like.
                    interpolation.KeepVisualPosition();
                }

                _wasMovedHere = movedHere;
                if (!movedHere) {
                    interpolation.ManualUpdate(Time.deltaTime);
                }
            }

            return;
        }

        UpdateLeads();
        LetThePlayerBackIn(false);

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
        if (_pendingAnticipation != 0 && MonoBehaviourUtil.FixedStep > _pendingSinceStep &&
            Time.unscaledTime >= _pendingSettledAt) {
            _incorporatedAnticipation = _pendingAnticipation;
            _pendingAnticipation = 0;
            _anticipationSendsLeft = AnticipationForcedSends;
            _anticipationStampUntil = Time.unscaledTime + AnticipationStampTime;
        }

        // Sent more than once, because a position travels by the way that drops what it cannot deliver and an enemy
        // that is standing still gives no second chance of its own: one forced position, lost, and the player
        // waiting on it waits out their whole timeout instead. Several in a row cost a few dozen bytes.
        var anticipationTaken = _anticipationSendsLeft > 0;

        var transform = Object.Host.transform;

        // A body that stops moving by itself does not always move its transform as it stops: one that the physics puts
        // to sleep where it lies is left alone, and the place it came to rest was never sent
        var movesByItself = HostMovesByItself();
        var stoppedMovingByItself = _hostMovedByItself && !movesByItself;
        _hostMovedByItself = movesByItself;

        // Unity already tracks transform mutations; avoid re-reading and comparing position/scale on quiet frames. A
        // part of another entity sends neither: the copy of its parent carries it and moves it the way it moves here.
        if (!_hasParent && (transform.hasChanged || anticipationTaken || stoppedMovingByItself)) {
            var newPosition = transform.position;

            // A position is sent even when the entity has not moved, for as long as there are forced ones left,
            // because the player waiting on it has nothing else to wait for. It is also the whole of the answer
            // when the scene host did nothing with what they sent - the knockback went nowhere, and this is where
            // the enemy really is and always was.
            //
            // None is sent while the entity moves by itself from how it set off: the other game moves the copy the
            // same way, and was told where it set off from (OwnMotionComponent).
            if ((newPosition != _lastPosition || anticipationTaken) && !movesByItself) {
                if (_anticipationSendsLeft > 0) {
                    _anticipationSendsLeft--;
                }

                _lastPosition = newPosition;

                _netClient.UpdateManager.UpdateEntityPosition(
                    Id,
                    new Math_Vector3(newPosition.x, newPosition.y, newPosition.z)
                );

                // Beside every position for a while rather than only the once, for the same reason as above, and
                // then not at all: nobody can still be waiting on it by then, since waiting gives up long before.
                if (_incorporatedAnticipation != 0 && Time.unscaledTime < _anticipationStampUntil) {
                    _netClient.UpdateManager.UpdateEntityAnticipation(Id, _incorporatedAnticipation);
                }
            }

            const float epsilon = 0.0001f;

            var newScale = transform.lossyScale;
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
                }

                _netClient.UpdateManager.UpdateEntityScale(Id, scaleData);

                _lastScale = newScale;
            }

            transform.hasChanged = false;
        }

        var newActive = _hasParent ? Object.Host.activeSelf : Object.Host.activeInHierarchy;
        if (newActive != _lastIsActive) {
            _lastIsActive = newActive;

            //Logger.Info($"Entity '{Object.Host.name}' changed active: {newActive}");

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
                    snapshot.CurrentState = fsm.ActiveStateName;
                    data.Types.Add(EntityHostFsmData.Type.State);
                    data.CurrentState = (byte) Array.IndexOf(fsm.FsmStates, fsm.Fsm.ActiveState);
                }

                if (hasFloatsChange) {
                    data.Types.Add(EntityHostFsmData.Type.Floats);
                    for (byte i = 0; i < fsm.FsmVariables.FloatVariables.Length; i++) {
                        var val = fsm.FsmVariables.FloatVariables[i].Value;
                        if (val != snapshot.Floats[i]) {
                            snapshot.Floats[i] = val;
                            data.Floats[i] = val;
                        }
                    }
                }

                if (hasIntsChange) {
                    data.Types.Add(EntityHostFsmData.Type.Ints);
                    for (byte i = 0; i < fsm.FsmVariables.IntVariables.Length; i++) {
                        var val = fsm.FsmVariables.IntVariables[i].Value;
                        if (val != snapshot.Ints[i]) {
                            snapshot.Ints[i] = val;
                            data.Ints[i] = val;
                        }
                    }
                }

                if (hasBoolsChange) {
                    data.Types.Add(EntityHostFsmData.Type.Bools);
                    for (byte i = 0; i < fsm.FsmVariables.BoolVariables.Length; i++) {
                        var val = fsm.FsmVariables.BoolVariables[i].Value;
                        if (val != snapshot.Bools[i]) {
                            snapshot.Bools[i] = val;
                            data.Bools[i] = val;
                        }
                    }
                }

                if (hasStringsChange) {
                    data.Types.Add(EntityHostFsmData.Type.Strings);
                    for (byte i = 0; i < fsm.FsmVariables.StringVariables.Length; i++) {
                        var val = fsm.FsmVariables.StringVariables[i].Value;
                        if (snapshot.Strings[i] != null && val != snapshot.Strings[i]) {
                            snapshot.Strings[i] = val;
                            data.Strings[i] = val;
                        }
                    }
                }

                if (hasVector2SChange) {
                    data.Types.Add(EntityHostFsmData.Type.Vector2s);
                    for (byte i = 0; i < fsm.FsmVariables.Vector2Variables.Length; i++) {
                        var val = fsm.FsmVariables.Vector2Variables[i].Value;
                        if (val != snapshot.Vector2s[i]) {
                            snapshot.Vector2s[i] = val;
                            data.Vec2s[i] = (Math_Vector2) val;
                        }
                    }
                }

                if (hasVector3SChange) {
                    data.Types.Add(EntityHostFsmData.Type.Vector3s);
                    for (byte i = 0; i < fsm.FsmVariables.Vector3Variables.Length; i++) {
                        var val = fsm.FsmVariables.Vector3Variables[i].Value;
                        if (val != snapshot.Vector3s[i]) {
                            snapshot.Vector3s[i] = val;
                            data.Vec3s[i] = (SSMP.Math.Vector3) val;
                        }
                    }
                }

                _netClient.UpdateManager.AddEntityHostFsmData(Id, fsmIndex, data);
                ObjectPool<EntityHostFsmData>.Return(data);
            }
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
            if (!_allowClientAnimation && _runHere == null) {
                //Logger.Info($"Entity '{Object.Client.name}' client animator tried playing animation");
            } else {
                // Logger.Info($"Entity '{_object.Client.name}' client animator was allowed to play animation");

                orig(self, clip, clipStartTime, overrideFps);

                _allowClientAnimation = false;
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

        if (!_animationClipNameIds.TryGetValue(clip.name, out var animationId)) {
            //Logger.Warn($"Entity '{Object.Client.name}' played unknown animation: {clip.name}");
            return;
        }

        //Logger.Info($"Entity '{Object.Host.name}' sends animation: {clip.name}, {animationId}, {clip.wrapMode}");
        _netClient.UpdateManager.UpdateEntityAnimation(
            Id,
            animationId,
            (byte) clip.wrapMode
        );
    }

    /// <summary>
    /// Callback method for when a game object is recycled. Used to prevent client objects from being recycled, which
    /// shouldn't happen because they are instantiated manually instead of from a pool.
    /// </summary>
    private void ObjectPoolOnRecycleGameObject(Action<GameObject> orig, GameObject obj) {
        if (obj == Object.Client) {
            //Logger.Debug($"Client object of entity: {Id}, {Type} tried to be recycled");
            return;
        }

        orig(obj);
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

        //Logger.Debug(
        //    $"Entity '{Object.Host.name}' tried changing active of host object, while host is not determined yet, updating original active to: {self.activate.Value}"
        //);

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

        //Logger.Info(
        //    $"Initializing entity '{Object.Host.name}' with active: {_originalIsActive}, sending active: {_lastIsActive}"
        //);

        _netClient.UpdateManager.UpdateEntityIsActive(Id, _lastIsActive);

        _isControlled = false;
        _isSceneHostDetermined = true;
        SaveWhereItRuns(copyRuns: false);

        foreach (var component in _components.Values) {
            component.IsControlled = false;
            component.InitializeHost(sceneHostEpoch);
        }

        // Deregister the hook for updating the active value of the host object
        _activateGameObjectHook?.Dispose();
        _activateGameObjectHook = null;
    }

    /// <summary>
    /// Initializes the entity when the client user is a scene client. Only sets a variable to indicate the scene
    /// host has been determined.
    /// </summary>
    public void InitializeClient(uint sceneHostEpoch = 0) {
        ResetAnticipation();

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

            var oldActions = state.Actions;
            state.Actions = oldActions.Where(a =>
                ActionRegistry.IsActionContinuous(a) || ActionRegistry.IsActionTransferSafeSetup(a)
            ).ToArray();
            fsm.SetState(wanted);
            state.Actions = oldActions;
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
    /// scene host's colour actions all along, and by the colour it had when this game walked in (TintComponent), and the
    /// FSM goes on from where the copy was, without the state that tinted it last. A crow that roosts in front of the
    /// room is drawn black until it flies at the player: the room's own one had been made black by its own first
    /// states, before it was put to sleep for the other game to run, and went on fighting black once this game took it
    /// over.
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

        ResetAnticipation();

        //Logger.Info($"Making entity ({Id}, {Type}) a host entity");

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

            //Logger.Debug("  Client object null, enabling host object and returning");
            return;
        }

        // Make sure that the sprite animator doesn't play the default clip after enabling the object
        if (_animator.Host != null) {
            _animator.Host.playAutomatically = false;
        }

        //.Debug("  Restoring FSM variables from snapshots");

        for (var fsmIndex = 0; fsmIndex < _fsms.Host.Count; fsmIndex++) {
            var fsm = _fsms.Host[fsmIndex];

            // The other game never said anything about it, and what it does is about this game: it starts over,
            // looking its room up afresh, when the object is switched on
            if (IsRunByEachGame(fsm)) {
                continue;
            }

            //Logger.Debug($"    Restoring variables for FSM: {fsm.Fsm.Name}");

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
        }

        GiveTakenOverFurmItsCorpse();

        // Only now to where the copy stands. The setting up above is done where the room put the creature, as in a
        // game where it started with the room: a creature that lets go of the markers it hides at, so that they stay
        // where the room has them, let them go wherever the copy had run to, and hid beside itself after
        if (_hasParent) {
            //Logger.Debug("  Entity has parent, only setting local transform");

            Object.Host.transform.localPosition = _lastPosition = Object.Client.transform.localPosition;
            Object.Host.transform.localScale = _lastScale = Object.Client.transform.localScale;
        } else {
            //Logger.Debug("  Entity has no parent, calculating transform");

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

        //Logger.Debug("  Restoring FSM states from snapshots");

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
                //.Debug("Not setting FSM state, because current state is empty");
                continue;
            }

            var state = fsm.GetStateOrNull(snapshot.CurrentState);
            if (state == null) {
                //Logger.Debug($"  Not setting FSM state, because state '{snapshot.CurrentState}' was not found");
                continue;
            }

            //Logger.Debug($"  Setting FSM state: {snapshot.CurrentState}");

            var oldActions = state.Actions;
            var newActions = oldActions.Where(a =>
                ActionRegistry.IsActionContinuous(a) || ActionRegistry.IsActionTransferSafeSetup(a)
            ).ToArray();

            //Logger.Debug($"  Only using actions: {string.Join(", ", newActions.Select(a => a.GetType().ToString()))}");

            // Replace the actions, set the state and reset the actions again
            state.Actions = newActions;
            fsm.SetState(snapshot.CurrentState);
            state.Actions = oldActions;
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

                //Logger.Debug($"  Animator and current clip present, updating animation: {clientAnimation}, {wrapMode}");

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

        // What the copy kept in the save goes to the room's own creature, which saves it from now on
        HandSaveToTheRoom();
        SaveWhereItRuns(copyRuns: false);

        var clientActive = Object.Client.activeSelf;
        _sceneHostHasItOn = false;
        Object.Client.SetActive(false);
        SwitchOnWithoutStartingOver(clientActive, resumed);
        SayWhatWasTakenOver();

        //Logger.Debug($"  Set Active of host object to: {clientActive}, disabling client object");

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

        // Or from an FSM of the copy itself that runs here, which only one that each game runs by itself does
        // (EntityRegistryEntry.EachGameFsms): what watches the grab box of a creature that pounces on the player whole
        // and tells the FSM that carries them off, by its name. It grabs nobody itself - that FSM does, once it takes
        // the grab - so a grab that no FSM of the copy takes leaves nobody to let go.
        if (part == target) {
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
    private static bool TouchesTheLocalPlayer(GameObject part) {
        var hero = HeroController.SilentInstance;
        if (hero == null) {
            return false;
        }

        if (_heroBox is not { } known || known.Hero != hero) {
            var box = hero.GetComponentInChildren<HeroBox>(true);
            _heroBox = known = (hero, box != null ? box.GetComponent<Collider2D>() : null);
        }

        var body = hero.GetComponent<Collider2D>();
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

        return _outstandingAnticipation;
    }

    /// <summary>
    /// Stops waiting on the last number taken, for something that turned out not to be done after all.
    /// </summary>
    public void EndAnticipation() {
        _outstandingAnticipation = 0;
    }

    /// <summary>
    /// Notes that the scene host has been told of something a scene client did to this entity, which what it sends
    /// will have the whole of in it once a step of physics and the time it takes to happen have gone by.
    /// </summary>
    /// <param name="anticipation">The number it was sent under.</param>
    /// <param name="settleTime">
    /// How much longer what was done goes on moving the entity here, in seconds. The player waiting on this has
    /// already watched their own copy do the whole of it, so anything said before then only sends them back into
    /// the middle of it. Nothing that takes no time waits at all, and nothing waits longer than
    /// <see cref="AnticipationSettleCap"/> however long it says it takes.
    /// </param>
    public void NoteAnticipation(byte anticipation, float settleTime = 0f) {
        if (anticipation == 0) {
            return;
        }

        _pendingAnticipation = anticipation;
        _pendingSinceStep = MonoBehaviourUtil.FixedStep;
        _pendingSettledAt = Time.unscaledTime + Mathf.Min(settleTime, AnticipationSettleCap);
    }

    /// <summary>
    /// Forgets both sides of this, for an entity that is changing hands or starting again. A number from before the
    /// change means nothing after it: the game that would have answered it is not the one answering now.
    /// </summary>
    private void ResetAnticipation() {
        _outstandingAnticipation = 0;
        _pendingAnticipation = 0;
        _incorporatedAnticipation = 0;
        _anticipationSendsLeft = 0;
        _anticipationStampUntil = 0f;
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
        if (_outstandingAnticipation != 0 && Time.unscaledTime > _outstandingExpiry) {
            _outstandingAnticipation = 0;
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
    /// <param name="anticipation">What the scene host said it had taken in, or null if it said nothing.</param>
    private bool AcceptsWhileAnticipating(byte? anticipation) {
        if (!IsAnticipating()) {
            return true;
        }

        // Compared by the sign of the difference in the size the numbers are kept in, so that the step from the
        // largest back round to one reads as one forward rather than as the whole way back
        if (anticipation is { } stamp && stamp != 0 && (sbyte) (stamp - _outstandingAnticipation) >= 0) {
            _outstandingAnticipation = 0;

            return true;
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
    public void UpdatePosition(Math_Vector3 position, ushort sequence, byte? anticipation) {
        // An older position arriving after a newer one is thrown away: nothing below this transport orders what it
        // carries, so one that had to be sent again lands after ones sent later, and applying it puts the entity
        // back where it was that much earlier.
        if (!_positionSequence.Accepts(sequence, out var packetsCovered)) {
            return;
        }

        if (Object.Client == null || Object.Host == null) {
            //.Warn($"Cannot update position for entity ({Id}, {Type}), client or host object is null");
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
        if (!AcceptsWhileAnticipating(anticipation)) {
            _positionsHeldBack += packetsCovered;

            return;
        }

        positionInterpolation.SetNewPosition(unityPos, _positionsHeldBack + packetsCovered);
        _positionsHeldBack = 0;
    }

    /// <summary>
    /// Updates the scale of the client entity.
    /// </summary>
    /// <param name="scale">The new scale data.</param>
    public void UpdateScale(EntityUpdate.ScaleData scale) {
        // The part of an FSM that each game runs by itself turns the copy to the local player (RunEachGamePart)
        if (Object.Client == null || _runHereForGood) {
            //Logger.Warn($"Cannot update scale for entity ({Id}, {Type}), client object is null");
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

        transform.localScale = localScale;
    }

    /// <summary>
    /// Updates the animation of the client entity.
    /// </summary>
    /// <param name="animationId">The ID of the animation.</param>
    /// <param name="wrapMode">The wrap mode of the animation clip.</param>
    /// <param name="alreadyInSceneUpdate">Whether this update is when entering a new scene.</param>
    public void UpdateAnimation(
        byte animationId,
        tk2dSpriteAnimationClip.WrapMode wrapMode,
        bool alreadyInSceneUpdate
    ) {
        if (_animator.Client == null) {
            //Logger.Warn($"Entity '{Object.Client.name}' received animation while client animator does not exist");
            return;
        }

        // Played by the copy's own FSM until the scene host has answered its input, and through the combo of a catch,
        // and held till then (PlayHere)
        if (WaitsForEcho || _runHereCombo != null) {
            _heldAnimation = (animationId, wrapMode);
            return;
        }

        if (!_animationClipNameIds.TryGetValue(animationId, out var clipName)) {
            //Logger.Warn($"Entity '{Object.Client.name}' received unknown animation ID: {animationId}");
            return;
        }

        //Logger.Info($"Entity '{Object.Client.name}' received animation: {animationId}, {clipName}, {wrapMode}");

        // All paths lead to calling the Play method of the sprite animator that is hooked, so we allow the call
        // through the hook
        _allowClientAnimation = true;

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

        //Logger.Debug($"Entity ({Id}, {Type}) LateUpdateAnimation: {clip.name}, {wrapMode}");

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

                // Logger.Info(
                // $"  Played animation: {clipName}, {clipLength - 1} on {_animator.Client.name}, {_animator.Client.GetHashCode()}");
                break;
            }
        }
    }

    /// <summary>
    /// Updates whether the game object for the client entity is active.
    /// </summary>
    /// <param name="active">The new value for active.</param>
    public void UpdateIsActive(bool active) {
        _sceneHostHasItOn = active;
        _timesToldIfOn++;
        _lastToldIfOn = Time.unscaledTime;

        // Each word from the scene host starts the wait of SayWhyTheCopyIsOff over, so a copy that stays off anyway
        // is said again
        _copyOffSince = -1f;
        _saidTheCopyIsOff = false;

        if (Object.Client != null) {
            //Logger.Info($"Entity '{Object.Client.name}' received active: {active}");
            if (Object.Client.TryGetComponent<SaysWhoSwitchesTheCopyOff>(out var says)) {
                says.Caller = null;
            } else if (active) {
                Object.Client.AddComponent<SaysWhoSwitchesTheCopyOff>().Entity = this;
            }

            Object.Client.SetActive(active);
        } else {
            //Logger.Warn($"Entity ({Id}, {Type}) could not update active, because client object is null");
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

                // Played here by the copy's own FSM until the scene host has answered its input, and held till then:
                // all but what the copy leaves to the scene host, which only comes from there (PlayHere). The combo of
                // a catch is played here all through, and not again from what the scene host sends of it, and so is
                // the part of an FSM that each game runs by itself (RunEachGamePart). What comes after what the copy
                // plays by itself out of a catch that this game led is held until it has played that
                // (HoldsForTheAftermath).
                HearFromSceneHost(fsm, state);
                if (fsm == _runHere && !_mutedHere.Contains(action)) {
                    if (WaitsForEcho || HoldsForTheAftermath(state)) {
                        HoldForEcho(data);
                        continue;
                    }

                    if (_runHereForGood || _runHereCombo?.Contains(state) == true) {
                        continue;
                    }
                }

                // Nor the death that a boss dies in that part while it waits for the local player to be stood up or
                // to come into the room: the copy dies it there itself once the part starts, from its first state
                if (_eachGamePartWaiting is { } waiting && waiting.Fsm == fsm && waiting.Part.Contains(state) &&
                    EntityFsmActions.IsSimulatedDeath(action)) {
                    continue;
                }

                //Logger.Info(
                //    $"Received entity network data for FSM: {fsm.Fsm.Name}, {state.Name}, {actionIndex} ({action.GetType()})"
                //);

                EntityFsmActions.ReadSubject(data, action);
                EntityFsmActions.ApplyNetworkDataFromAction(data, action);

                continue;
            }

            // How the entity moves by itself is its own FSM's to say while that runs here, and held until the scene
            // host has answered the input (PlayHere)
            if (data.Type == EntityComponentType.OwnMotion && WaitsForEcho) {
                HoldForEcho(data);
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
            if (_fsms.Host.Count <= fsmIndex) {
                //Logger.Warn($"Tried to update host FSM data for unknown FSM index: {fsmIndex}");
                continue;
            }

            if (_fsms.Client == null || _fsms.Client.Count <= fsmIndex || _fsms.Client[fsmIndex] == null) {
                continue;
            }

            var hostFsm = _fsms.Host[fsmIndex];
            var snapshot = _fsmSnapshots[fsmIndex];

            if (data.Types.Contains(EntityHostFsmData.Type.State)) {
                var states = hostFsm.FsmStates;
                if (states.Length <= data.CurrentState) {
                    //Logger.Warn($"Tried to update host FSM state for unknown state index: {data.CurrentState}");
                } else {
                    var stateName = states[data.CurrentState].Name;

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
            // game leads, whose count of struggles is (_runHereLed).
            var clientFsm = _fsms.Client[fsmIndex];
            var writesCopy = clientFsm != _runHere || !WaitsForEcho && !_runHereForGood && !_runHereLed;

            if (data.Types.Contains(EntityHostFsmData.Type.Floats)) {
                foreach (var (index, val) in data.Floats) {
                    if (index < hostFsm.FsmVariables.FloatVariables.Length) {
                        hostFsm.FsmVariables.FloatVariables[index].Value = val;
                        snapshot.Floats[index] = val;
                    }

                    if (writesCopy && index < clientFsm.FsmVariables.FloatVariables.Length) {
                        clientFsm.FsmVariables.FloatVariables[index].Value = val;
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

        // What the entity was doing to the local player is undone first. Its hooks and parts go whatever happens there:
        // left behind, they would go on acting for a room that is gone
        try {
            LetGoOfRunHere();
            EndAllLeads();
            LetThePlayerBackIn(true);
        } finally {
            EntitiesByCopy.Remove(Object.Client);

            // An FSM that was still to be made is no longer this entity's to hook
            foreach (var fsm in _fsms.Host) {
                if (FsmsMadeLater.TryGetValue(fsm, out var waiting) && waiting == this) {
                    FsmsMadeLater.Remove(fsm);
                }
            }

            _spriteAnimatorPlayHook?.Dispose();
            _spriteAnimatorPlayHook = null;

            _objectPoolRecycleHook?.Dispose();
            _objectPoolRecycleHook = null;

            _activateGameObjectHook?.Dispose();
            _activateGameObjectHook = null;

            foreach (var component in _components.Values.Distinct()) {
                component.Destroy();
            }

            GamePatcher.OnEntityDestroyed(Object.Host);
            GamePatcher.OnEntityDestroyed(Object.Client);
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
