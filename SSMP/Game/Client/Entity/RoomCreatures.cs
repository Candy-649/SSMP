using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Client;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// Creatures that the room makes, rather than a creature: a nest that lets out its flyers, the controller of a room
/// that places the creatures haunting it, and the shell a creature leaves behind that lets out what lived inside it.
/// What makes them is a part of the room and not an entity, so it runs in both games alike, and each game made
/// creatures of its own that the other player never saw - each player fought their own. They are now made in the scene
/// host's game only, where they are made entities and sent to the other game as spawned by
/// <see cref="EntityType.Room"/>, and the scene client makes none of its own.
/// </summary>
internal class RoomCreatures {
    /// <summary>
    /// The net client, to send what the room made.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The entity manager, which knows which game runs the room.
    /// </summary>
    private readonly EntityManager _entityManager;

    /// <summary>
    /// Per kind of creature, how the room makes it. Null for a kind that the room makes from two different prefabs: a
    /// spawn message names only the kind, so the other game could not tell which one to make, and such creatures are
    /// left to each game as before.
    /// </summary>
    private readonly Dictionary<EntityType, Maker?> _makers = new();

    /// <summary>
    /// What the room made before the server said which game runs it, and whether each was switched on. The room makes
    /// its creatures as it starts and the answer takes a round trip to the server, so this is nearly always all of
    /// them. They are held switched off until then, as the room's own creatures are (see <see cref="Entity"/>), and
    /// only the scene host's are switched on and made entities; the others are taken away (<see cref="Settle"/>).
    /// </summary>
    private readonly List<(GameObject Creature, bool WasActive)> _madeBeforeTheRoomWasSettled = [];

    /// <summary>
    /// Detour hook for the FSM action that makes an object from a prefab.
    /// </summary>
    private Hook? _createObjectHook;

    /// <summary>
    /// Detour hook for the FSM action that lets out what an object made ahead of time.
    /// </summary>
    private Hook? _letOutHook;

    public RoomCreatures(NetClient netClient, EntityManager entityManager) {
        _netClient = netClient;
        _entityManager = entityManager;
    }

    /// <summary>
    /// Register the hooks of the actions that make the creatures.
    /// </summary>
    public void RegisterHooks() {
        _createObjectHook = new Hook(
            typeof(CreateObject).GetMethod(
                nameof(CreateObject.OnEnter),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            ),
            OnCreateObject
        );

        _letOutHook = new Hook(
            typeof(GetPreInstantiatedGameObject).GetMethod(
                nameof(GetPreInstantiatedGameObject.OnEnter),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            ),
            OnLetOut
        );
    }

    /// <summary>
    /// Deregister the hooks of the actions that make the creatures.
    /// </summary>
    public void DeregisterHooks() {
        _createObjectHook?.Dispose();
        _createObjectHook = null;

        _letOutHook?.Dispose();
        _letOutHook = null;
    }

    /// <summary>
    /// Forgets the room: what it makes, and what it made before it was settled which game runs it.
    /// </summary>
    public void Clear() {
        _makers.Clear();
        _madeBeforeTheRoomWasSettled.Clear();
    }

    /// <summary>
    /// Finds what the room makes in the given scene: every creature that an FSM of the room, rather than of a
    /// creature, makes from a prefab the action names itself, and every creature that a shell makes ahead of time to
    /// let out when it breaks. Both games load the same room, so both find the same.
    /// </summary>
    /// <param name="scene">The scene of the room, or a part of it that was loaded later.</param>
    public void FindMakers(Scene scene) {
        var fsms = Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var fsm in fsms) {
            // An FSM on something that was never switched on is not set up yet, and PlayMaker complains about every
            // state it is asked for
            if (fsm.gameObject.scene != scene || fsm.Fsm is not { Initialized: true }) {
                continue;
            }

            foreach (var state in fsm.FsmStates) {
                foreach (var action in state.Actions) {
                    if (action is CreateObject { gameObject: { UseVariable: false } prefabField } create) {
                        Find(prefabField.Value, fsm, create);
                    }
                }
            }
        }

        // The shell that a creature leaves behind when it dies, which lets out what lived inside it. A creature's
        // death is played out in both games, and so is its shell: each game's shell let out a creature of its own.
        // Each shell is made along with its creature, as the room starts, and makes what it lets out there and then
        var shells = Object.FindObjectsByType<PreInstantiateGameObject>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None
        );
        foreach (var shell in shells) {
            if (shell.gameObject.scene == scene) {
                Find(shell.prefab, shell, null);
            }
        }

        void Find(GameObject? prefab, UnityEngine.Component maker, CreateObject? create) {
            // A creature's own FSMs run in one game only, and what they spawn is sent already
            if (prefab == null ||
                !EntityRegistry.TryGetEntry(prefab, out var entry) ||
                !EntitySpawner.IsSpawnedAsEntity(prefab, entry.Type) ||
                EntityProcessor.IsRegistered(maker.gameObject)) {
                return;
            }

            if (!_makers.TryGetValue(entry.Type, out var known)) {
                Logger.Info($"'{maker.gameObject.name}' makes '{prefab.name}' ({entry.Type}) in this room");
                _makers[entry.Type] = new Maker(prefab, maker, create);
            } else if (known != null && known.Prefab != prefab) {
                Logger.Info(
                    $"The room makes {entry.Type} from both '{known.Prefab.name}' and '{prefab.name}', so " +
                    "each game makes its own"
                );
                _makers[entry.Type] = null;
            }
        }
    }

    /// <summary>
    /// Settles what the room made before the server said which game runs it: in the scene host's game it is switched
    /// back on and made entities, and in the other game it is taken away, since the scene host's come in its place.
    /// </summary>
    /// <param name="share">Whether this game runs the room.</param>
    public void Settle(bool share) {
        foreach (var (creature, wasActive) in _madeBeforeTheRoomWasSettled) {
            if (creature == null) {
                continue;
            }

            if (share) {
                creature.SetActive(wasActive);
                Share(creature);
            } else {
                Logger.Info($"Taking away '{creature.name}', which the room made here: the scene host's game sends it");
                Object.Destroy(creature);
            }
        }

        _madeBeforeTheRoomWasSettled.Clear();
    }

    /// <summary>
    /// Makes a creature of the given kind the way the room makes it, for one that the scene host's game says the room
    /// made there. It stands where the room makes such creatures until its first position arrives.
    /// </summary>
    /// <param name="type">The kind of creature.</param>
    /// <returns>The creature, or null if the room here makes no creature of that kind, or not from one prefab.
    /// </returns>
    public GameObject? Make(EntityType type) {
        if (!_makers.TryGetValue(type, out var maker) || maker == null) {
            return null;
        }

        var spawnPoint = maker.Action?.spawnPoint.Value;
        var position = spawnPoint != null
            ? spawnPoint.transform.position
            : maker.MadeBy != null
                ? maker.MadeBy.transform.position
                : Vector3.zero;

        return Object.Instantiate(maker.Prefab, position, Quaternion.identity);
    }

    /// <summary>
    /// Makes an object from a prefab, and for a creature that the room makes, does it in the scene host's game only.
    /// </summary>
    private void OnCreateObject(Action<CreateObject> orig, CreateObject self) {
        var prefab = self.gameObject is { UseVariable: false } prefabField ? prefabField.Value : null;
        if (prefab == null || !IsMadeByTheRoom(prefab) || EntityProcessor.IsRegistered(self.Fsm.GameObject)) {
            orig(self);
            return;
        }

        MakeInTheSceneHostsGame(self, prefab, self.storeObject, () => orig(self));
    }

    /// <summary>
    /// Lets out what a shell made ahead of time, and for a creature that the room makes, does it in the scene host's
    /// game only. The other game's shell lets out nothing, and what it made stays switched off where it was made.
    /// </summary>
    private void OnLetOut(Action<GetPreInstantiatedGameObject> orig, GetPreInstantiatedGameObject self) {
        var shell = self.Fsm.GetOwnerDefaultTarget(self.target);
        var prefab = shell != null && shell.TryGetComponent<PreInstantiateGameObject>(out var maker)
            ? maker.prefab
            : null;
        if (prefab == null || !IsMadeByTheRoom(prefab) || EntityProcessor.IsRegistered(self.Fsm.GameObject)) {
            orig(self);
            return;
        }

        MakeInTheSceneHostsGame(self, prefab, self.storeGameObject, () => orig(self));
    }

    /// <summary>
    /// Makes a creature that the room makes in the scene host's game only, where it is made an entity and sent.
    /// </summary>
    /// <param name="action">The action that makes it.</param>
    /// <param name="prefab">What it is made from.</param>
    /// <param name="store">Where the action keeps what it made.</param>
    /// <param name="make">Makes it the way the game does.</param>
    private void MakeInTheSceneHostsGame(
        FsmStateAction action,
        GameObject prefab,
        FsmGameObject store,
        System.Action make
    ) {
        // The scene host's game makes it and sends it. Nothing is stored either, so that what the FSM goes on to do
        // to the new creature - move it, put it under something, tell it where it belongs - is done to nothing rather
        // than to whatever the variable held before.
        if (_entityManager.IsSceneRoleDetermined && !_entityManager.IsSceneHost) {
            Logger.Info(
                $"Not making '{prefab.name}' for '{action.Fsm.GameObject.name}': the scene host's game makes it"
            );
            store.Value = null;
            action.Finish();
            return;
        }

        make();

        var creature = store.Value;
        if (creature == null) {
            return;
        }

        // Held switched off until it is settled which game runs the room. A creature that ran here in the meantime
        // could get hold of the player - one hides the player's character and holds on until its own FSM lets go - and
        // then be taken away, or started over when it is made an entity, with the player never let go of
        if (!_entityManager.IsSceneRoleDetermined) {
            _madeBeforeTheRoomWasSettled.Add((creature, creature.activeSelf));
            creature.SetActive(false);
            return;
        }

        Share(creature);
    }

    /// <summary>
    /// Whether the room makes creatures from the given prefab.
    /// </summary>
    private bool IsMadeByTheRoom(GameObject prefab) {
        foreach (var maker in _makers.Values) {
            if (maker != null && maker.Prefab == prefab) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Makes a creature that the room made in this game, which runs the room, an entity, and tells the other game.
    /// </summary>
    private void Share(GameObject creature) {
        var processor = new EntityProcessor {
            GameObject = creature,
            IsSceneHost = true,
            IsSceneHostDetermined = true,
            KeepsRunning = true,
            LateLoad = true
        }.Process();

        if (!processor.Success) {
            Logger.Warn($"Could not make '{creature.name}', which the room made, an entity: it is in this game only");
            return;
        }

        var entity = processor.Entities[0];
        Logger.Info($"Notifying server of the room making entity ({creature.name}, {entity.Type}) with ID {entity.Id}");
        _netClient.UpdateManager.SetEntitySpawn(entity.Id, EntityType.Room, entity.Type);
    }

    /// <summary>
    /// How the room makes one kind of creature.
    /// </summary>
    private sealed class Maker {
        /// <summary>
        /// What the creature is made from.
        /// </summary>
        public GameObject Prefab { get; }

        /// <summary>
        /// One of the things that make it: an FSM of the room, or a shell that makes it ahead of time.
        /// </summary>
        public UnityEngine.Component MadeBy { get; }

        /// <summary>
        /// The action of that FSM that makes it, or null for a shell.
        /// </summary>
        public CreateObject? Action { get; }

        public Maker(GameObject prefab, UnityEngine.Component madeBy, CreateObject? action) {
            Prefab = prefab;
            MadeBy = madeBy;
            Action = action;
        }
    }
}
