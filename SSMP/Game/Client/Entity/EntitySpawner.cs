using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace SSMP.Game.Client.Entity; 

/// <summary>
/// Static class that has implementations for spawning entities that are usually spawned by other entities in-game.
/// </summary>
internal static class EntitySpawner {
    /// <summary>
    /// Whether the other game can make what the given action just spawned from a spawn message alone, which says no
    /// more than what kind of creature spawned what kind of entity. The other game looks for it among what its own
    /// copy of a creature of the spawning kind spawns (<see cref="FindPrefab"/>), so it makes the same thing only when
    /// that finds the very thing this action spawned.
    /// </summary>
    /// <param name="action">The action that spawned it.</param>
    /// <param name="spawnedType">The type the registry takes the spawned object for.</param>
    public static bool CanSpawn(FsmStateAction action, EntityType spawnedType) {
        var spawned = PrefabOf(action, out _)?.Value;
        return spawned != null &&
               FindPrefab(action.Fsm.GameObject.GetComponents<PlayMakerFSM>(), spawnedType, out _) == spawned;
    }

    /// <summary>
    /// The first thing that one of the given FSMs spawns and that is made an entity of the given type when spawned.
    /// Only a thing an action names itself counts, not one it takes from a variable: the creature can change the
    /// variable while it runs, and the other game's copy of it, which does not run, would find something else there.
    /// </summary>
    /// <param name="fsms">The FSMs of the spawning creature.</param>
    /// <param name="spawnedType">The type of the spawned entity.</param>
    /// <param name="pooled">Whether it is taken from the object pool rather than made anew.</param>
    /// <returns>The prefab, or null if none of the FSMs spawns one.</returns>
    public static GameObject? FindPrefab(IEnumerable<PlayMakerFSM> fsms, EntityType spawnedType, out bool pooled) {
        foreach (var fsm in fsms) {
            foreach (var state in fsm.FsmStates) {
                foreach (var action in state.Actions) {
                    var field = PrefabOf(action, out pooled);
                    var prefab = field is { UseVariable: false } ? field.Value : null;
                    if (prefab != null &&
                        EntityRegistry.TryGetEntry(prefab, out var entry) &&
                        entry.Type == spawnedType &&
                        IsSpawnedAsEntity(prefab)) {
                        return prefab;
                    }
                }
            }
        }

        pooled = false;
        return null;
    }

    /// <summary>
    /// Whether the given FSM spawns anything that is made an entity when spawned, named by the action itself the way
    /// <see cref="FindPrefab"/> looks for it.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    public static bool SpawnsCreatures(PlayMakerFSM fsm) {
        foreach (var state in fsm.FsmStates) {
            foreach (var action in state.Actions) {
                var field = PrefabOf(action, out _);
                var prefab = field is { UseVariable: false } ? field.Value : null;
                if (prefab != null &&
                    EntityRegistry.TryGetEntry(prefab, out _) &&
                    IsSpawnedAsEntity(prefab)) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an action spawns a creature: what it spawns has health. The copy of a creature that first does what its
    /// first states do must not do these: the creature that a copy's first state makes is a real one of the room, apart
    /// from every entity, which the game that runs the creature makes once again and shows the other game as a spawn.
    /// </summary>
    /// <param name="action">The action.</param>
    public static bool SpawnsCreature(FsmStateAction action) {
        var prefab = PrefabOf(action, out _)?.Value;
        return prefab != null && IsSpawnedAsEntity(prefab);
    }

    /// <summary>
    /// The parameter of a spawning action that holds what it spawns, or null for an action that spawns nothing.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <param name="pooled">Whether what it spawns is taken from the object pool rather than made anew.</param>
    private static FsmGameObject? PrefabOf(FsmStateAction action, out bool pooled) {
        pooled = action is not CreateObject;
        return action switch {
            CreateObject create => create.gameObject,
            SpawnObjectFromGlobalPool spawn => spawn.gameObject,
            FlingObjectsFromGlobalPool fling => fling.gameObject,
            FlingObjectsFromGlobalPoolVel fling => fling.gameObject,
            FlingObjectsFromGlobalPoolTime fling => fling.gameObject,
            _ => null
        };
    }

    /// <summary>
    /// Whether something that was just spawned, and that the registry takes for an entity, is kept out of the ordinary
    /// way of showing a spawn on the other game, which is doing the spawning action over there: a creature, which has
    /// health. What the other game can make from a spawn message is made there as an entity. A creature it cannot make
    /// stays in the game it was spawned in, since a copy made the ordinary way would go its own way unseen by this one.
    /// What a creature throws is not kept out: its sickles, javelins and bombs are often named after it, so the
    /// registry takes them for the creature itself, but they are thrown like anything else.
    /// </summary>
    /// <param name="gameObject">The spawned object, or the prefab it is spawned from.</param>
    public static bool IsSpawnedAsEntity(GameObject gameObject) {
        return gameObject.GetComponent<HealthManager>() != null;
    }

    /// <summary>
    /// Spawn the game object for an entity with the given type that is spawned from the other given type.
    /// </summary>
    /// <param name="spawningType">The type of the entity that spawns the new entity.</param>
    /// <param name="spawnedType">The type of the spawned entity.</param>
    /// <param name="clientObject">The client game object from the spawning entity.</param>
    /// <param name="clientFsms">The list of client FSMs from the spawning entity.</param>
    /// <returns>The game object for the spawned entity, or null if the spawning entity spawns nothing of that type.
    /// </returns>
    public static GameObject? SpawnEntityGameObject(
        EntityType spawningType, 
        EntityType spawnedType,
        GameObject clientObject,
        List<PlayMakerFSM> clientFsms
    ) {
        // Where a thrown thing is put until the first position of it arrives from the game that threw it.
        //
        // This was the origin of the world, which is a real place in the room and usually one both players can see.
        // Every throw therefore put the thing at that corner for as long as it took a position to cross the border,
        // and the thing then jumped from there to where it really was. Standing it next to a creature of the kind
        // that throws it is not exactly right - there may be several of them in a room and this is whichever one was
        // found first - but it is wrong by the width of a room at the very worst instead of by wherever the corner
        // happens to be, and the first position puts it right either way.
        var startPosition = clientObject != null ? clientObject.transform.position : Vector3.zero;

        // It is looked for among what the spawning creature's own FSMs spawn, which is where the game that
        // spawned it made sure it would be found (CanSpawn)
        var creature = FindPrefab(clientFsms, spawnedType, out var pooled);
        if (creature != null) {
            return pooled
                ? creature.Spawn(startPosition, Quaternion.identity)
                : UnityEngine.Object.Instantiate(creature, startPosition, Quaternion.identity);
        }

        return null;
    }
}
