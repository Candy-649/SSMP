using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SSMP.Util;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity.Action;
using SSMP.Game.Client.Entity.Component;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using MonoMod.RuntimeDetour;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

#pragma warning disable CS8604 // Possible null reference argument.
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
#pragma warning disable CS0618 // Type or member is obsolete

namespace SSMP.Game.Client.Entity;

/// <summary>
/// Manager class that handles entity creation, updating, networking and destruction.
/// </summary>
internal class EntityManager {
    /// <summary>
    /// The net client for networking.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// Dictionary mapping entity IDs to their respective entity instances.
    /// </summary>
    private readonly Dictionary<ushort, Entity> _entities;

    /// <summary>
    /// Queue of buffered entity updates waiting on entity registration or role assignment.
    /// </summary>
    private readonly Queue<BaseEntityUpdate> _pendingUpdates;

    /// <summary>
    /// Detour hook for intercepting FSM queries targeting inactive game objects.
    /// </summary>
    private Hook? _findGameObjectHook;

    /// <summary>
    /// Detour hook for the screen starting to shake, to end the shakes that a copy of a creature starts here.
    /// </summary>
    private Hook? _cameraShakeHook;

    /// <summary>
    /// The creatures that the room makes rather than a creature, which only the scene host's game makes.
    /// </summary>
    private readonly RoomCreatures _roomCreatures;

    // Both flags are set together in InitializeSceneHost / InitializeSceneClient.
    public bool IsSceneHost { get; private set; }

    /// <summary>
    /// Whether the client's role (host vs client) has been determined for the current scene.
    /// </summary>
    private bool _sceneRoleDetermined;

    /// <summary>
    /// Whether the server told the client if it is the scene host of the current scene.
    /// </summary>
    public bool IsSceneRoleDetermined => _sceneRoleDetermined;

    /// <summary>
    /// Gets all currently registered active entities.
    /// </summary>
    public Dictionary<ushort, Entity>.ValueCollection ActiveEntities => _entities.Values;

    public EntityManager(NetClient netClient) {
        _netClient = netClient;
        _entities = new Dictionary<ushort, Entity>();
        _pendingUpdates = new Queue<BaseEntityUpdate>();
        _roomCreatures = new RoomCreatures(netClient, this);
    }

    /// <summary>
    /// Initialize the entity manager by initializing the processor and action hooks.
    /// </summary>
    public void Initialize() {
        EntityProcessor.Initialize(_entities, _netClient);
    }

    /// <summary>
    /// Register the hooks for entity-related operations.
    /// </summary>
    public void RegisterHooks() {
        FsmActionHooks.RegisterHooks();
        MusicComponent.RegisterHooks();

        EntityFsmActions.EntitySpawnEvent += OnGameObjectSpawned;
        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.activeSceneChanged += OnSceneChanged;

        _findGameObjectHook = new Hook(
            typeof(FindGameObject).GetMethod(
                nameof(FindGameObject.OnEnter),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            ),
            OnFindGameObject
        );

        _cameraShakeHook = new Hook(
            typeof(CameraManagerReference).GetMethod(nameof(CameraManagerReference.DoShake)),
            OnCameraShake
        );

        _roomCreatures.RegisterHooks();
    }

    /// <summary>
    /// Deregister the hooks for entity-related operations.
    /// </summary>
    public void DeregisterHooks() {
        FsmActionHooks.DeregisterHooks();
        MusicComponent.DeregisterHooks();

        EntityFsmActions.EntitySpawnEvent -= OnGameObjectSpawned;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.activeSceneChanged -= OnSceneChanged;

        _findGameObjectHook?.Dispose();
        _findGameObjectHook = null;

        _cameraShakeHook?.Dispose();
        _cameraShakeHook = null;

        _roomCreatures.DeregisterHooks();

        ClearEntities();
    }

    /// <summary>
    /// Initializes the entity manager if we are the scene host.
    /// </summary>
    public void InitializeSceneHost(uint sceneHostEpoch = 0) {
        Logger.Info($"We are scene host, releasing control of all registered entities (epoch {sceneHostEpoch})");
        IsSceneHost = true;
        foreach (var entity in _entities.Values) entity.InitializeHost(sceneHostEpoch);
        _sceneRoleDetermined = true;
        _roomCreatures.OnSceneHost();
        DrainPendingUpdates();
    }

    /// <summary>
    /// Initializes the entity manager if we are a scene client.
    /// </summary>
    public void InitializeSceneClient(uint sceneHostEpoch = 0) {
        Logger.Info($"We are scene client, taking control of all registered entities (epoch {sceneHostEpoch})");
        IsSceneHost = false;
        foreach (var entity in _entities.Values) entity.InitializeClient(sceneHostEpoch);
        _sceneRoleDetermined = true;
        _roomCreatures.OnSceneClient();
        DrainPendingUpdates();
    }

    /// <summary>
    /// Updates the entity manager if we become the scene host.
    /// </summary>
    public void BecomeSceneHost(uint sceneHostEpoch = 0) {
        Logger.Info($"Becoming scene host (epoch {sceneHostEpoch})");
        IsSceneHost = true;
        foreach (var entity in _entities.Values) entity.MakeHost(sceneHostEpoch);

        // Immediately refresh targeting fields for all active enemies
        GamePatcher.ForceImmediateRetarget();
    }

    /// <summary>
    /// Attempts to spawn a networked entity. No-ops if the ID is already registered (assumed spawned by action).
    /// </summary>
    public void SpawnEntity(ushort id, EntityType spawningType, EntityType spawnedType) {
        Logger.Info($"Trying to spawn entity with ID {id} with types: {spawningType}, {spawnedType}");

        if (_entities.ContainsKey(id)) {
            Logger.Info($"  Entity with ID {id} already exists, assuming it has been spawned by action");
            return;
        }

        // What the room made is made the way the room here makes it. Otherwise any entity of the spawning type that
        // spawns something of the spawned type works as a template: the thing is made from what its FSMs spawn
        GameObject? spawnedObject = null;
        if (spawningType == EntityType.Room) {
            spawnedObject = _roomCreatures.Make(spawnedType);
        } else {
            foreach (var templateEntity in _entities.Values.Where(e => e.Type == spawningType)) {
                spawnedObject = EntitySpawner.SpawnEntityGameObject(
                    spawningType,
                    spawnedType,
                    templateEntity.Object.Client,
                    templateEntity.GetClientFsms()
                );
                if (spawnedObject != null) {
                    break;
                }
            }
        }

        if (spawnedObject == null) {
            Logger.Warn($"Nothing of type {spawningType} here spawns a {spawnedType}, so entity {id} is not made");
            return;
        }

        var processor = new EntityProcessor {
            GameObject = spawnedObject,
            IsSceneHost = IsSceneHost,
            IsSceneHostDetermined = _sceneRoleDetermined,
            LateLoad = true,
            SpawnedId = id
        }.Process();

        if (!processor.Success) {
            Logger.Warn($"Could not process game object of spawned entity: {spawnedObject.name}");
        }
    }

    /// <summary>
    /// Applies an unreliable entity update (position, scale, animation).
    /// Returns false and buffers the update if the entity isn't ready yet.
    /// </summary>
    public bool HandleEntityUpdate(EntityUpdate update, bool alreadyInSceneUpdate = false) {
        // Scene host owns entity state; updates from peers are ignored.
        if (IsSceneHost) return true;

        if (!_entities.TryGetValue(update.Id, out var entity) || !_sceneRoleDetermined) {
            _pendingUpdates.Enqueue(update);
            return false;
        }

        // The stamp travels beside the position it describes rather than on its own, so it is read out here and
        // handed over with it. An update that carries none says nothing either way, which is not the same as saying
        // the scene host has taken nothing in.
        var anticipation = update.UpdateTypes.Contains(EntityUpdateType.Anticipation)
            ? update.Anticipation
            : (byte?) null;

        if (update.UpdateTypes.Contains(EntityUpdateType.Position))
            entity.UpdatePosition(update.Position, update.ReceivedSequence, anticipation);

        if (update.UpdateTypes.Contains(EntityUpdateType.Scale))
            entity.UpdateScale(update.Scale);

        if (update.UpdateTypes.Contains(EntityUpdateType.Animation))
            entity.UpdateAnimation(
                update.AnimationId,
                (tk2dSpriteAnimationClip.WrapMode) update.AnimationWrapMode,
                alreadyInSceneUpdate
            );

        return true;
    }

    /// <summary>
    /// Applies a reliable entity update (active state, host FSM data, generic data).
    /// Returns false and buffers the update if the entity isn't ready yet.
    /// </summary>
    public bool HandleReliableEntityUpdate(ReliableEntityUpdate update, bool alreadyInSceneUpdate = false) {
        if (!_entities.TryGetValue(update.Id, out var entity) || !_sceneRoleDetermined) {
            _pendingUpdates.Enqueue(update);
            return false;
        }

        // What one player did to something that belongs to each of them alone is not carried to the other on the way
        // into a room.
        //
        // The server keeps whether each thing in a room is still there and how much health it has, and hands all of
        // it to whoever walks in afterwards. For nearly everything that is what should happen: an enemy the other
        // player killed is dead, and walking in should not raise it. But a creature carrying something that belongs
        // to one player is not shared - this mod gives each of them their own copy of it - and handing over "that
        // one is gone" took it away from a player who had never had it. One of them went through the room first, and
        // the other could not get theirs at all: not by walking in, not by being made the one who runs the room,
        // which reads the state of the copy it was just told to switch off.
        //
        // Only what was stored is turned away. While both of them are standing there it still behaves as one
        // creature, so neither of them sees the other fighting nothing.
        var isPersonal = alreadyInSceneUpdate && entity.Type == EntityType.Aknid;

        // Active state and host FSM are driven by the scene host; clients are consumers only.
        if (!IsSceneHost) {
            if (update.UpdateTypes.Contains(EntityUpdateType.Active) && !isPersonal)
                entity.UpdateIsActive(update.IsActive);

            if (update.UpdateTypes.Contains(EntityUpdateType.HostFsm))
                entity.UpdateHostFsmData(update.HostFsmData);
        }

        if (update.UpdateTypes.Contains(EntityUpdateType.Data) && !isPersonal)
            entity.UpdateData(update.GenericData, alreadyInSceneUpdate);

        return true;
    }

    /// <summary>
    /// Callback method for when the scene changes. Will clear existing entities and start checking for
    /// new entities.
    /// </summary>
    /// <param name="oldScene">The old scene.</param>
    /// <param name="newScene">The new scene.</param>
    private void OnSceneChanged(Scene oldScene, Scene newScene) {
        Logger.Info("Scene changed, clearing registered entities");
        ClearEntities();

        if (!_netClient.IsConnected) return;

        _sceneRoleDetermined = false;
        FindEntitiesInScene(newScene, lateLoad: false);
        _roomCreatures.FindMakers(newScene);
        DrainPendingUpdates();
    }

    /// <summary>
    /// Callback method for when a scene is loaded.
    /// </summary>
    /// <param name="scene">The scene that is loaded.</param>
    /// <param name="mode">The load scene mode.</param>
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
        var activeScene = SceneManager.GetActiveScene().name;

        // Boss/boss-defeated scenes share a name prefix with the base scene; skip unrelated additively-loaded scenes.
        if (!scene.name.StartsWith(activeScene) || scene.name.Equals(activeScene)) return;

        // The room the player is walking into is loaded beside this one before it takes over, and many rooms are
        // named after the room next to them with a letter added. Taken for a part of this room, its creatures were
        // registered here first, and a scene client registers creatures by putting them to sleep. When the room
        // took over, they were registered again and read as asleep from the start, so whoever came in first ran
        // the room with its creatures switched off, for both players. They are found when the room takes over.
        var gameManager = global::GameManager.instance;
        if (gameManager != null && scene.name == gameManager.nextSceneName) {
            Logger.Info($"Scene loaded ({scene.name}) is the room being entered, not a part of this one");
            return;
        }

        Logger.Info($"Additional scene loaded ({scene.name}), looking for entities");
        FindEntitiesInScene(scene, lateLoad: true);
        _roomCreatures.FindMakers(scene);
        DrainPendingUpdates();
    }

    /// <summary>
    /// Find entities to register in the given scene.
    /// </summary>
    /// <param name="scene">The scene to find entities in.</param>
    /// <param name="lateLoad">Whether this scene was loaded late.</param>
    private void FindEntitiesInScene(Scene scene, bool lateLoad) {
        var objects = CollectEntityCandidates(scene);

        foreach (var obj in objects) {
            new EntityProcessor {
                GameObject = obj,
                IsSceneHost = IsSceneHost,
                IsSceneHostDetermined = _sceneRoleDetermined,
                LateLoad = lateLoad,
                UseStableSceneId = true
            }.Process();
        }
    }

    /// <summary>
    /// Gathers all GameObjects in the scene that are candidates for entity registration.
    /// Handles EnemyDeathEffects owners and several component-driven object types
    /// (Climber, Walker, BigCentipede, CameraLockArea, DreamPlatform).
    /// </summary>
    private static IEnumerable<GameObject> CollectEntityCandidates(Scene scene) {
        var fromDeathEffects = Object.FindObjectsOfType<EnemyDeathEffects>()
                                     .Where(e => e.gameObject.scene == scene)
                                     .SelectMany(ExpandDeathEffects);

        var fromFsms = Object.FindObjectsOfType<PlayMakerFSM>(true)
                             .Where(fsm => fsm.gameObject.scene == scene)
                             .Select(fsm => fsm.gameObject);

        var fromComponents = new[] {
            Object.FindObjectsOfType<Climber>(true).Select(c => c.gameObject),
            Object.FindObjectsOfType<Walker>(true).Select(c => c.gameObject),
            Object.FindObjectsOfType<BigCentipede>(true).Select(c => c.gameObject),
            Object.FindObjectsOfType<CameraLockArea>(true).Select(c => c.gameObject),
            Object.FindObjectsOfType<DreamPlatform>(true).Select(c => c.gameObject),
        }.SelectMany(x => x);

        return fromDeathEffects
               // Expand each object to itself and all children
               .Concat(fromFsms)
               .SelectMany(obj => obj == null ? [] : obj.GetChildren().Prepend(obj))
               .Concat(fromComponents)
               .Where(obj => obj.scene == scene && !IsCorpseObject(obj))
               .Distinct();
    }

    private static bool IsCorpseObject(GameObject gameObject) {
        return gameObject.name.StartsWith("corpse", StringComparison.OrdinalIgnoreCase) ||
               gameObject.GetComponent<Corpse>()               != null ||
               gameObject.GetComponent<ActiveCorpse>()         != null ||
               gameObject.GetComponent<CorpseItems>()          != null ||
               gameObject.GetComponentInParent<Corpse>()       != null ||
               gameObject.GetComponentInParent<ActiveCorpse>() != null ||
               gameObject.GetComponentInParent<CorpseItems>()  != null;
    }

    /// <summary>
    /// Pre-instantiates death effects and returns only their owning game object as an entity candidate.
    /// </summary>
    private static IEnumerable<GameObject> ExpandDeathEffects(EnemyDeathEffects deathEffects) {
        try {
            deathEffects.PreInstantiate();
        } catch (Exception) {
            // PersonalObjectPool objects cannot be pre-instantiated this early.
        }

        return [deathEffects.gameObject];
    }

    /// <summary>
    /// Callback method for when a game object is spawned from an existing entity.
    /// </summary>
    /// <param name="details">The entity spawn details containing how the entity was spawned.</param>
    /// <returns>Whether the spawn is kept from being done again on the other game by doing the spawning action
    /// there: true for what is made an entity, and for a creature that stays in the game it was spawned in.</returns>
    private bool OnGameObjectSpawned(EntitySpawnDetails details) {
        if (_entities.Values.Any(e => e.Object.Host == details.GameObject)) {
            Logger.Debug("Spawned object was already a registered entity");
            return true;
        }

        if (!EntityRegistry.TryGetEntry(details.GameObject, out var spawnedEntry)) {
            return false;
        }

        // What a creature throws is often named after it - "Spine Floater Spine" - so the registry takes it for the
        // creature, but it is thrown on the other game like anything else, by doing the spawning action there
        if (!EntitySpawner.IsSpawnedAsEntity(details.GameObject, spawnedEntry.Type)) {
            return false;
        }

        // A creature is made an entity only in the game that runs the creature that spawned it, and only when the
        // other game can make the same one from a spawn message; otherwise it stays in the game it was spawned in.
        // One the other game could not make threw over there, and took everything else it was being told about the
        // room down with it: whoever walked into a room after a floater had grown its spines never saw the creatures,
        // the partner or anything else in it. In a game that does not run the room it would be a copy that is
        // switched off and never moved by anything.
        if (!IsSceneHost ||
            details.Type != EntitySpawnType.FsmAction ||
            !EntityRegistry.TryGetEntry(details.Action.Fsm.GameObject, out var entry) ||
            !EntitySpawner.CanSpawn(details.Action, spawnedEntry.Type)) {
            return true;
        }

        var processor = new EntityProcessor {
            GameObject = details.GameObject,
            IsSceneHost = IsSceneHost,
            IsSceneHostDetermined = _sceneRoleDetermined,
            LateLoad = true
        }.Process();

        if (!processor.Success) return false;

        var topLevel = processor.Entities[0];
        Logger.Info(
            $"Notifying server of entity ({details.Action.Fsm.GameObject.name}, {entry.Type}) spawning entity ({details.GameObject.name}, {topLevel.Type}) with ID {topLevel.Id}"
        );
        _netClient.UpdateManager.SetEntitySpawn(topLevel.Id, entry.Type, topLevel.Type);

        return true;
    }

    /// <summary>
    /// Replays buffered updates for entities that are now registered and whose scene role is known.
    /// Updates that still can't be applied are left in the queue.
    /// </summary>
    private void DrainPendingUpdates() {
        // Iterate a snapshot count; newly buffered updates (HandleEntityUpdate returning false) stay in the queue.
        var count = _pendingUpdates.Count;
        for (var i = 0; i < count; i++) {
            var update = _pendingUpdates.Dequeue();

            var applied = update switch {
                EntityUpdate eu => HandleEntityUpdate(eu),
                ReliableEntityUpdate r => HandleReliableEntityUpdate(r),
                _ => true
            };

            if (!applied) {
                // Still not applicable; it was re-enqueued by Handle*..nothing to do.
                continue;
            }

            ReleaseUpdate(update);
        }
    }

    /// <summary>
    /// Clears all the registered entities, and resets static components.
    /// </summary>
    private void ClearEntities() {
        foreach (var entity in _entities.Values) entity.Destroy();
        _entities.Clear();

        foreach (var pendingUpdate in _pendingUpdates) {
            ReleaseUpdate(pendingUpdate);
        }

        _pendingUpdates.Clear();
        MusicComponent.ClearInstance();
        _roomCreatures.Clear();
    }

    // Once an update is buffered, the queue owns its lifetime until it is applied or discarded.
    /// <summary>
    /// Discards and returns the buffered entity update packet to the object pool.
    /// </summary>
    /// <param name="update">The update packet to release.</param>
    private static void ReleaseUpdate(BaseEntityUpdate update) {
        switch (update) {
            case EntityUpdate entityUpdate:
                ObjectPool<EntityUpdate>.Return(entityUpdate);
                break;
            case ReliableEntityUpdate reliableEntityUpdate:
                ObjectPool<ReliableEntityUpdate>.Return(reliableEntityUpdate);
                break;
        }
    }

    // Patch: intercept FindGameObject.OnEnter so that entities the system has made inactive are still findable.
    /// <summary>
    /// Detour hook for <c>FindGameObject.OnEnter</c>. Resolves inactive entities by matching their name against registered host objects.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The action instance.</param>
    private void OnFindGameObject(Action<FindGameObject> orig, FindGameObject self) {
        orig(self);

        if (self.store.Value != null) return;

        // This particular FSM state finds a Roller via tag; letting our code handle it breaks Blocker Control logic.
        if (self.State.Name == "Can Roller?" && self.Fsm.Name == "Blocker Control") return;

        Logger.Debug($"OnFindGameObject, find failed: looking for '{self.objectName.Value}'");

        // Tag-based finds won't match our entities by name.
        if (self.withTag.Value != "Untagged") return;

        foreach (var entity in _entities.Values) {
            var host = entity.Object.Host;
            if (host != null && host.name == self.objectName.Value) {
                self.store.Value = host;
                Logger.Debug($"  Name matches host object of entity: ({entity.Id}, {entity.Type})");
                return;
            }
        }

        Logger.Debug("  Name did not match any entity");
    }

    /// <summary>
    /// Ends a shake of the screen that does not end by itself once the part of a creature's copy that started it
    /// goes.
    ///
    /// Some shakes run until something tells them to stop. On a scene client the parts of a creature's copy run by
    /// themselves, and a part that catches this player starts such a shake here. What stops it is the creature's
    /// own FSM, which runs in the scene host's game and not here, so the screen shook until the room changed. The
    /// scene host switches that part off when the move is over, which is carried over, so it ends here then too.
    /// </summary>
    private void OnCameraShake(
        Action<CameraManagerReference, ICameraShake, Object, bool, bool, bool> orig,
        CameraManagerReference self,
        ICameraShake shake,
        Object source,
        bool doFreeze,
        bool vibrate,
        bool sendWorldForce
    ) {
        orig(self, shake, source, doFreeze, vibrate, sendWorldForce);

        if (shake == null || shake.CanFinish) return;

        var part = source switch {
            GameObject gameObject => gameObject,
            UnityEngine.Component component => component.gameObject,
            _ => null
        };
        if (part == null || !IsPartOfACopy(part.transform)) return;

        part.AddComponent<EndShakeWhenGone>().Watch(self, shake);
    }

    /// <summary>
    /// Whether the object is the copy of an entity, or a part of one. Copies only run while the scene host runs the
    /// entity.
    /// </summary>
    /// <param name="part">The object.</param>
    private bool IsPartOfACopy(Transform part) {
        for (var current = part; current != null; current = current.parent) {
            foreach (var entity in _entities.Values) {
                if (entity.Object.Client == current.gameObject) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Ends a shake of the screen when the object it is on is switched off, or goes with its room.
    /// </summary>
    private sealed class EndShakeWhenGone : MonoBehaviour {
        private CameraManagerReference? _camera;
        private ICameraShake? _shake;

        /// <summary>
        /// Starts watching the object, to end the given shake when it goes.
        /// </summary>
        public void Watch(CameraManagerReference camera, ICameraShake shake) {
            _camera = camera;
            _shake = shake;
        }

        private void OnDisable() {
            if (_camera != null && _shake != null) {
                _camera.CancelShake(_shake);
            }

            Destroy(this);
        }
    }
}
