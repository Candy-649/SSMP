using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SSMP.Game.Client;

/// <summary>
/// Parts of rooms that each player has to themselves. Nothing in them is shared between the two games: what moves there
/// runs in each game on its own rather than as an entity that the scene host runs for both, a hit or touch of the local
/// player there stays in this game, and the copies of the partner's attacks leave it alone. The partner is still seen
/// there, playing with things that this game does not show.
///
/// What is in a place is taken down once per loaded room, before anything in it has moved, since the things of a place
/// can leave it while they are in use: the festival's fleas fly about without a parent and go back when they are done.
/// Whatever is under something that was taken down counts too, like what those fleas make for themselves.
/// </summary>
internal static class PersonalPlaces {
    /// <summary>
    /// The places, each as the path of the object that everything in it is under, from an object at the top of a room.
    /// </summary>
    private static readonly string[][] Places = [
        // The festival's game of landing on fleas from above. Both players went for the same fleas, and one of them was
        // soon left with nothing to land on
        ["Caravan_States", "Flea Festival", "Flea Game - Bouncing"],

        // The contest of throwing pins at small targets, which each player enters, pays for and wins on their own. A hit
        // of one player on a target was sent to the other game, where it scored for whoever was playing there
        ["Black Thread States", "Normal World", "Pin Gallery States", "Here"]
    ];

    /// <summary>
    /// The things that each player has to themselves wherever a room has them, by how their names start, each with
    /// what is under it: the floors that crumble under a player, or under their attack, and come back a moment later.
    /// Both games used to break them together, so a floor that the partner stood on was gone under this player as
    /// well, who then had to wait for it to come back - or, where lava rises behind them, fell in. The partner's body
    /// sets none of them off here, so a floor stays whole here under a partner who is seen to fall through it. Floors
    /// that stay broken are still shared, since the save keeps them (see CoopSave.WorldTriggerKinds).
    /// </summary>
    private static readonly string[] Things = [
        "moss_crumble_plat",
        "lava_crumble_plat",
        "bone_plat_01_crumble",
        "bone_plat_02_crumble",
        "bone_plat_crumble",
        "crumble_plat_peak_",
        "memory_ground_plat"
    ];

    /// <summary>
    /// The loaded rooms that were looked through already, by their handle, each with the instance IDs of what was taken
    /// down in it.
    /// </summary>
    private static readonly Dictionary<int, List<int>> LookedThrough = [];

    /// <summary>
    /// What was in a place or a thing when its room was looked through, by instance ID.
    /// </summary>
    private static readonly HashSet<int> InPlace = [];

    /// <summary>
    /// The objects of the room being looked through.
    /// </summary>
    private static readonly List<Transform> Parts = [];

    static PersonalPlaces() {
        // A room is looked through as it loads, before its things have started. One that was loaded before anything
        // was asked is looked through when one of its things is first asked about. What was in a room is forgotten
        // when the room is gone
        SceneManager.sceneLoaded += (scene, _) => LookThrough(scene);
        SceneManager.sceneUnloaded += Forget;
    }

    /// <summary>
    /// Whether an object is in a place that each player has to themselves.
    /// </summary>
    /// <param name="gameObject">The object.</param>
    public static bool Contains(GameObject gameObject) {
        LookThrough(gameObject.scene);
        if (InPlace.Count == 0) {
            return false;
        }

        for (var current = gameObject.transform; current != null; current = current.parent) {
            if (InPlace.Contains(current.gameObject.GetInstanceID())) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Takes down what is in the places and things of a room, once per loaded room.
    /// </summary>
    private static void LookThrough(Scene scene) {
        if (!scene.IsValid() || !scene.isLoaded || LookedThrough.ContainsKey(scene.handle)) {
            return;
        }

        var takenDown = new List<int>();
        LookedThrough[scene.handle] = takenDown;

        // What is kept across rooms is in none of them
        if (scene.name == "DontDestroyOnLoad") {
            return;
        }

        foreach (var top in scene.GetRootGameObjects()) {
            foreach (var path in Places) {
                if (top.name != path[0]) {
                    continue;
                }

                var place = top.transform;
                for (var index = 1; index < path.Length && place != null; index++) {
                    place = place.Find(path[index]);
                }

                if (place != null) {
                    TakeDown(place, takenDown);
                }
            }

            top.GetComponentsInChildren(true, Parts);
            foreach (var part in Parts) {
                if (IsThing(part.name)) {
                    TakeDown(part, takenDown);
                }
            }
        }

        Parts.Clear();
    }

    /// <summary>
    /// Takes down an object and everything under it.
    /// </summary>
    private static void TakeDown(Transform root, List<int> takenDown) {
        foreach (var part in root.GetComponentsInChildren<Transform>(true)) {
            var id = part.gameObject.GetInstanceID();
            if (InPlace.Add(id)) {
                takenDown.Add(id);
            }
        }
    }

    /// <summary>
    /// Whether an object of this name is one of <see cref="Things"/>.
    /// </summary>
    private static bool IsThing(string name) {
        foreach (var start in Things) {
            if (name.StartsWith(start, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Forgets what was taken down in a room that is gone.
    /// </summary>
    private static void Forget(Scene scene) {
        if (!LookedThrough.TryGetValue(scene.handle, out var takenDown)) {
            return;
        }

        LookedThrough.Remove(scene.handle);
        foreach (var id in takenDown) {
            InPlace.Remove(id);
        }
    }
}
