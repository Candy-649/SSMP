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
        ["Caravan_States", "Flea Festival", "Flea Game - Bouncing"]
    ];

    /// <summary>
    /// The loaded rooms that were looked through already, by their handle.
    /// </summary>
    private static readonly HashSet<int> LookedThrough = [];

    /// <summary>
    /// What was in a place when its room was looked through, by instance ID.
    /// </summary>
    private static readonly HashSet<int> InPlace = [];

    static PersonalPlaces() {
        // A room is looked through as it loads, before its things have started. One that was loaded before anything
        // was asked is looked through when one of its things is first asked about
        SceneManager.sceneLoaded += (scene, _) => LookThrough(scene);
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
    /// Takes down what is in the places of a room, once per loaded room.
    /// </summary>
    private static void LookThrough(Scene scene) {
        if (!scene.IsValid() || !scene.isLoaded || !LookedThrough.Add(scene.handle)) {
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

                if (place == null) {
                    continue;
                }

                foreach (var part in place.GetComponentsInChildren<Transform>(true)) {
                    InPlace.Add(part.gameObject.GetInstanceID());
                }
            }
        }
    }
}
