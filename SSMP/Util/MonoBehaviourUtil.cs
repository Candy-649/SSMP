using System;
using System.Collections.Generic;
using UnityEngine;

namespace SSMP.Util;

/// <summary>
/// MonoBehaviour that offers static utilities methods.
/// </summary>
internal class MonoBehaviourUtil : MonoBehaviour {
    /// <summary>
    /// The instance of this class.
    /// </summary>
    public static MonoBehaviourUtil Instance = null!;

    /// <summary>
    /// Event that is execute each Unity update tick.
    /// </summary>
    public event Action? OnUpdateEvent;

    public void Awake() {
        if (Instance != null) {
            Destroy(this);
            return;
        }

        Instance = this;
    }

    /// <summary>
    /// How many steps of physics the game has taken. What it is for is telling whether something set in motion has
    /// had a chance to move yet: a great deal of the game only moves in a step of physics, so the frame that starts
    /// a movement still has everything standing exactly where it was.
    /// </summary>
    public static uint FixedStep { get; private set; }

    public void Update() {
        OnUpdateEvent?.Invoke();
    }

    public void FixedUpdate() {
        FixedStep++;
    }

    /// <summary>
    /// Destroys all children of a given GameObject.
    /// </summary>
    /// <param name="gameObject">The GameObject to destroy all children of.</param>
    public static void DestroyAllChildren(GameObject gameObject) {
        DestroyAllChildren(gameObject, new List<string>());
    }

    /// <summary>
    /// Destroys all children of the given GameObject, excluding game objects that match the names in the
    /// given list.
    /// </summary>
    /// <param name="gameObject">The GameObject to destroy all children of.</param>
    /// <param name="exclude">The list of names to exclude.</param>
    public static void DestroyAllChildren(
        GameObject gameObject,
        List<string> exclude
    ) {
        for (var i = 0; i < gameObject.transform.childCount; i++) {
            var child = gameObject.transform.GetChild(i);

            if (exclude.Contains(child.name)) {
                continue;
            }

            Destroy(child.gameObject);
        }
    }
}
