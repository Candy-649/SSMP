using System.Collections.Generic;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// The creatures that every game runs by itself as the room's own, with no copy made of them, of the kinds that the
/// registry says so of (<see cref="EntityRegistryEntry.OwnInEachGame"/>). The game runs each one in every game as it
/// runs it for a player alone, and only what decides it is shared: what it rolls, by its place in the room (see
/// SharedDice), the hits it takes, as the room's own objects take them (see CoopHits.IsRoomObject), and the state that
/// the scene host's game has it in (see CoopSave.OwnCreatures).
///
/// A trial of keeping to the game's own creature and sending what is listed, rather than showing a copy of it whose
/// every state machine the mod has to teach (USER 11-14, "用游戏本身那只，更像是一种白名单",
/// "先微微试一下").
/// </summary>
internal static class OwnCreatures {
    /// <summary>
    /// The creatures of the rooms loaded now, with their scenes and their places in them as the rooms loaded them.
    /// </summary>
    private static readonly Dictionary<GameObject, (string Scene, string Place)> Creatures = new();

    /// <summary>
    /// How many there are.
    /// </summary>
    public static int Count => Creatures.Count;

    /// <summary>
    /// The creatures of the rooms loaded now, with their scenes and places as the rooms loaded them, including those
    /// that died since.
    /// </summary>
    public static IEnumerable<KeyValuePair<GameObject, (string Scene, string Place)>> All => Creatures;

    /// <summary>
    /// Takes a creature of the room as one that every game runs by itself.
    /// </summary>
    /// <param name="creature">The creature.</param>
    /// <param name="type">Its kind.</param>
    public static void Take(GameObject creature, EntityType type) {
        if (Creatures.ContainsKey(creature)) {
            return;
        }

        var place = ScenePath.Get(creature.transform);
        Creatures[creature] = (creature.scene.name, place);
        Logger.Info($"[Own creatures] '{place}' ({type}) runs in each game as the room's own, with no copy");
    }

    /// <summary>
    /// Whether an object is a creature that every game runs by itself.
    /// </summary>
    public static bool Has(GameObject gameObject) {
        return Creatures.Count > 0 && Creatures.ContainsKey(gameObject);
    }

    /// <summary>
    /// The creature at a place of a scene as the room loaded it, if it is one that every game runs by itself.
    /// </summary>
    public static GameObject? Find(string scene, string place) {
        foreach (var pair in Creatures) {
            if (pair.Key != null && pair.Value.Scene == scene && pair.Value.Place == place) {
                return pair.Key;
            }
        }

        return null;
    }

    /// <summary>
    /// The place of a creature as the room loaded it.
    /// </summary>
    public static string PlaceOf(GameObject creature) {
        return Creatures[creature].Place;
    }

    /// <summary>
    /// The name that a creature rolls under, the same in every game (see SharedDice.RollByName).
    /// </summary>
    public static string RollName(GameObject creature) {
        return "Own " + Creatures[creature].Place;
    }

    /// <summary>
    /// Forgets the creatures, as the room is left.
    /// </summary>
    public static void Clear() {
        Creatures.Clear();
    }
}
