using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
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
internal class Entity {
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
    /// Whether something the local player did to this entity was still waiting to come back from the scene host on
    /// the last frame, which says when one has just stopped and the interpolation is about to take the entity over
    /// again.
    /// </summary>
    private bool _wasAnticipating;

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
    /// The last position of the entity.
    /// </summary>
    private Vector3 _lastPosition;

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

    public Entity(
        NetClient netClient,
        ushort id,
        EntityType type,
        GameObject hostObject,
        GameObject clientObject = null,
        params EntityComponentType[] types
    ) {
        _netClient = netClient;
        Id = id;

        Type = type;

        _positionSequence = new PositionSequence($"entity {id} ({type})");

        _isControlled = true;

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

        // Store whether the host object was active and set it not active until we know if we are scene host
        _originalIsActive = Object.Host.activeSelf;

        _lastIsActive = _hasParent ? Object.Host.activeSelf : Object.Host.activeInHierarchy;

        //Logger.Info(
        //    $"Entity '{Object.Host.name}' was original active: {_originalIsActive}, last active: {_lastIsActive}"
        //);

        // Add a position interpolation component to the enemy so we can smooth out position updates
        Object.Client.AddComponent<PredictiveInterpolation>();

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

        Object.Host.SetActive(false);
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

        EntityInitializer.CheckPreProcessFsm(fsm);

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

        var snapshot = new FsmSnapshot {
            CurrentState = fsm.ActiveStateName,
            Floats = fsm.FsmVariables.FloatVariables.Select(f => f.Value).ToArray(),
            Ints = fsm.FsmVariables.IntVariables.Select(i => i.Value).ToArray(),
            Bools = fsm.FsmVariables.BoolVariables.Select(b => b.Value).ToArray(),
            Strings = fsm.FsmVariables.StringVariables.Select(s => s.Value).ToArray(),
            Vector2s = fsm.FsmVariables.Vector2Variables.Select(v => v.Value).ToArray(),
            Vector3s = fsm.FsmVariables.Vector3Variables.Select(v => v.Value).ToArray()
        };

        _fsmSnapshots.Add(snapshot);
    }

    /// <summary>
    /// Processes the given FSM for the client entity by disabling it.
    /// </summary>
    /// <param name="fsm">The Playmaker FSM to process.</param>
    private void ProcessClientFsm(PlayMakerFSM fsm) {
        //Logger.Info($"Processing client FSM: {fsm.Fsm.Name}");
        EntityInitializer.InitializeFsm(fsm);
        fsm.enabled = false;
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
                healthManager
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

            //addedComponentsString += " Collider";
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
            return;
        }

        if (!_hookedActions.TryGetValue(self, out var hookedEntityAction)) {
            return;
        }
        //
        //Logger.Info(
        //    $"Entity ({Id}, {Type}) hooked action: {self.Fsm.Name}, {self.State.Name}, {self.GetType()} ({hookedEntityAction.FsmIndex}, {hookedEntityAction.StateIndex}, {hookedEntityAction.ActionIndex})"
        //);

        var networkData = new EntityNetworkData {
            Type = EntityComponentType.Fsm
        };

        if (_fsms.Host.Count > 1) {
            networkData.Packet.Write((byte) hookedEntityAction.FsmIndex);
        }

        networkData.Packet.Write((byte) hookedEntityAction.StateIndex);
        networkData.Packet.Write((byte) hookedEntityAction.ActionIndex);

        // Only if the GetNetworkDataFromAction method returns true do we add the entity data
        // for sending
        if (EntityFsmActions.GetNetworkDataFromAction(networkData, self)) {
            _netClient.UpdateManager.AddEntityData(Id, networkData);
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
        if (Object.Host == null || !Object.Host.activeSelf) {
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

        Object.Host.SetActive(false);
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

            if (Object.Client != null &&
                Object.Client.TryGetComponent<PredictiveInterpolation>(out var interpolation)) {
                interpolation.AdaptToRTT(_netClient.UpdateManager.AverageRtt);

                // While something the local player did to this entity is still on its way to the scene host and
                // back, what the local game did is what moves it, and the interpolation stays out of the way
                var anticipating = IsAnticipating();
                if (_wasAnticipating && !anticipating) {
                    // Nothing wrote this object while that was going on, so the interpolation carried on predicting
                    // from where the entity stood before any of it. Picking that up again would put it back there in
                    // a single frame, which is the whole of what the wait was for, so where it is standing now is
                    // handed over as what it should go on looking like.
                    interpolation.KeepVisualPosition();
                }

                _wasAnticipating = anticipating;
                if (!anticipating) {
                    interpolation.ManualUpdate(Time.deltaTime);
                }
            }

            return;
        }

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

        // Unity already tracks transform mutations; avoid re-reading and comparing position/scale on quiet frames.
        if (transform.hasChanged || anticipationTaken) {
            var newPosition = _hasParent ? transform.localPosition : transform.position;

            // A position is sent even when the entity has not moved, for as long as there are forced ones left,
            // because the player waiting on it has nothing else to wait for. It is also the whole of the answer
            // when the scene host did nothing with what they sent - the knockback went nowhere, and this is where
            // the enemy really is and always was.
            if (newPosition != _lastPosition || anticipationTaken) {
                if (_anticipationSendsLeft > 0) {
                    _anticipationSendsLeft--;
                }

                _lastPosition = newPosition;

                _netClient.UpdateManager.UpdateEntityPosition(
                    Id,
                    new Math_Vector2(newPosition.x, newPosition.y)
                );

                // Beside every position for a while rather than only the once, for the same reason as above, and
                // then not at all: nobody can still be waiting on it by then, since waiting gives up long before.
                if (_incorporatedAnticipation != 0 && Time.unscaledTime < _anticipationStampUntil) {
                    _netClient.UpdateManager.UpdateEntityAnticipation(Id, _incorporatedAnticipation);
                }
            }

            const float epsilon = 0.0001f;

            var newScale = _hasParent ? transform.localScale : transform.lossyScale;
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
            if (!_allowClientAnimation) {
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

        // If the host client is determined already we skip (although this hook should have been deregistered
        if (_isSceneHostDetermined) {
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
    }

    /// <summary>
    /// Initializes the entity when the client user is the scene host.
    /// </summary>
    public void InitializeHost(uint sceneHostEpoch = 0) {
        // Nothing said under the numbering of a game that is no longer the one answering means anything here
        ResetAnticipation();

        Object.Host.SetActive(_originalIsActive);

        // Also update the last active variable to account for this potential change
        // Otherwise we might trigger the update sending of activity twice
        _lastIsActive = _hasParent ? Object.Host.activeSelf : Object.Host.activeInHierarchy;

        //Logger.Info(
        //    $"Initializing entity '{Object.Host.name}' with active: {_originalIsActive}, sending active: {_lastIsActive}"
        //);

        _netClient.UpdateManager.UpdateEntityIsActive(Id, _lastIsActive);

        _isControlled = false;
        _isSceneHostDetermined = true;

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

        _isSceneHostDetermined = true;

        // Deregister the hook for updating the active value of the host object
        _activateGameObjectHook?.Dispose();
        _activateGameObjectHook = null;

        foreach (var component in _components.Values) {
            component.InitializeClient(sceneHostEpoch);
        }
    }

    /// <summary>
    /// Makes the entity a host entity if the client user became the scene host.
    /// </summary>
    public void MakeHost(uint sceneHostEpoch) {
        ResetAnticipation();

        //Logger.Info($"Making entity ({Id}, {Type}) a host entity");

        // If the client object is null, we don't have to care about doing anything for the host object anymore
        if (Object.Client == null) {
            if (Object.Host != null) {
                Object.Host.SetActive(false);
            }

            _isControlled = false;

            foreach (var component in _components.Values) {
                component.IsControlled = false;
                component.InitializeHost(sceneHostEpoch);
            }

            //Logger.Debug("  Client object null, enabling host object and returning");
            return;
        }

        if (_hasParent) {
            //Logger.Debug("  Entity has parent, only setting local transform");

            Object.Host.transform.localPosition = _lastPosition = Object.Client.transform.localPosition;
            Object.Host.transform.localScale = _lastScale = Object.Client.transform.localScale;
        } else {
            //Logger.Debug("  Entity has no parent, calculating transform");

            var clientPos = Object.Client.transform.localPosition;
            var parentPos = Vector3.zero;
            if (Object.Host.transform.parent != null) {
                parentPos = Object.Host.transform.parent.position;
            }

            var newPosX = clientPos.x - parentPos.x;
            var newPosY = clientPos.y - parentPos.y;
            var newPosZ = clientPos.z - parentPos.z;

            Object.Host.transform.localPosition = _lastPosition = new Vector3(newPosX, newPosY, newPosZ);

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

        // Make sure that the sprite animator doesn't play the default clip after enabling the object
        if (_animator.Host != null) {
            _animator.Host.playAutomatically = false;
        }

        //.Debug("  Restoring FSM variables from snapshots");

        for (var fsmIndex = 0; fsmIndex < _fsms.Host.Count; fsmIndex++) {
            var fsm = _fsms.Host[fsmIndex];

            //Logger.Debug($"    Restoring variables for FSM: {fsm.Fsm.Name}");

            // Force initialize the host FSM, since it might have been disabled before initializing
            EntityInitializer.InitializeFsm(fsm);

            var snapshot = _fsmSnapshots[fsmIndex];

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

        //Logger.Debug("  Restoring FSM states from snapshots");

        for (var fsmIndex = 0; fsmIndex < _fsms.Host.Count; fsmIndex++) {
            var fsm = _fsms.Host[fsmIndex];
            var snapshot = _fsmSnapshots[fsmIndex];

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
        }

        // We need to set the isKinematic property of rigid bodies to ensure physics work again after enabling
        // the host object. In Hornet 1 this is necessary because another state sets this property normally in the
        // fight. See the "Wake" or "Refight Ready" state of the "Control" FSM on Hornet 1.
        // For the Mantis Lord and City Elevator entity, this should never be disabled, since they are always kinematic.
        var rigidBody = Object.Host.GetComponent<Rigidbody2D>();
        if (rigidBody != null) {
            //Logger.Debug("  Resetting isKinematic of Rigidbody to ensure physics work for host object");
            rigidBody.isKinematic = false;
        }

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

        var clientActive = Object.Client.activeSelf;
        Object.Client.SetActive(false);
        Object.Host.SetActive(clientActive);

        //Logger.Debug($"  Set Active of host object to: {clientActive}, disabling client object");

        _lastIsActive = _hasParent ? Object.Host.activeSelf : Object.Host.activeInHierarchy;

        foreach (var component in _components.Values) {
            component.InitializeHost(sceneHostEpoch);
        }
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
        _wasAnticipating = false;
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
    public void UpdatePosition(Math_Vector2 position, ushort sequence, byte? anticipation) {
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

        var unityPos = new Vector3(
            position.X,
            position.Y,
            _hasParent ? Object.Host.transform.localPosition.z : Object.Host.transform.position.z
        );

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
        if (Object.Client == null) {
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
        if (Object.Client != null) {
            //Logger.Info($"Entity '{Object.Client.name}' received active: {active}");
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
            if (data.Type == EntityComponentType.Fsm) {
                PlayMakerFSM fsm;

                if (_fsms.Client == null) {
                    continue;
                }

                if (_fsms.Client.Count > 1) {
                    // Do a check on the length of the data
                    if (data.Packet.Length < 3) {
                        continue;
                    }

                    var fsmIndex = data.Packet.ReadByte();
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

                //Logger.Info(
                //    $"Received entity network data for FSM: {fsm.Fsm.Name}, {state.Name}, {actionIndex} ({action.GetType()})"
                //);

                EntityFsmActions.ApplyNetworkDataFromAction(data, action);

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
                }
            }

            var clientFsm = _fsms.Client[fsmIndex];

            if (data.Types.Contains(EntityHostFsmData.Type.Floats)) {
                foreach (var (index, val) in data.Floats) {
                    if (index < hostFsm.FsmVariables.FloatVariables.Length) {
                        hostFsm.FsmVariables.FloatVariables[index].Value = val;
                        snapshot.Floats[index] = val;
                    }

                    if (index < clientFsm.FsmVariables.FloatVariables.Length) {
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

                    if (index < clientFsm.FsmVariables.IntVariables.Length) {
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

                    if (index < clientFsm.FsmVariables.BoolVariables.Length) {
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

                    if (index < clientFsm.FsmVariables.StringVariables.Length) {
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

                    if (index < clientFsm.FsmVariables.Vector2Variables.Length) {
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

                    if (index < clientFsm.FsmVariables.Vector3Variables.Length) {
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

        _spriteAnimatorPlayHook?.Dispose();
        _spriteAnimatorPlayHook = null;

        _objectPoolRecycleHook?.Dispose();
        _objectPoolRecycleHook = null;

        foreach (var component in _components.Values.Distinct()) {
            component.Destroy();
        }

        GamePatcher.OnEntityDestroyed(Object.Host);
        GamePatcher.OnEntityDestroyed(Object.Client);
    }

    /// <summary>
    /// Get the list of client FSMs.
    /// </summary>
    /// <returns>A list containing the client FSM instances.</returns>
    public List<PlayMakerFSM> GetClientFsms() {
        return _fsms.Client;
    }
}
