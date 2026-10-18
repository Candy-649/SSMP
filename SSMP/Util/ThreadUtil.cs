using System;
using System.Collections.Generic;
using UnityEngine;

namespace SSMP.Util;

/// <summary>
/// Class for utilities regarding threading.
/// </summary>
internal class ThreadUtil : MonoBehaviour {
    /// <summary>
    /// Object to lock asynchronous access.
    /// </summary>
    private static readonly object Lock = new object();

    /// <summary>
    /// List of actions that need to be run on the Unity main thread.
    /// </summary>
    private static List<Action> _actionsToRun = new List<Action>();

    /// <summary>
    /// The actions that run in this frame: a list that takes turns with <see cref="_actionsToRun"/>, rather than one
    /// made anew each frame.
    /// </summary>
    private static List<Action> _actionsRunning = new List<Action>();

    /// <summary>
    /// Instantiate this static class.
    /// </summary>
    public static void Instantiate() {
        var threadUtilObject = new GameObject();
        threadUtilObject.AddComponent<ThreadUtil>();
        DontDestroyOnLoad(threadUtilObject);
    }

    /// <summary>
    /// Runs the given action on the main thread of Unity.
    /// </summary>
    /// <param name="action">The action to run.</param>
    public static void RunActionOnMainThread(Action action) {
        lock (Lock) {
            _actionsToRun.Add(action);
        }
    }

    public void Update() {
        lock (Lock) {
            (_actionsToRun, _actionsRunning) = (_actionsRunning, _actionsToRun);
        }

        // What hasn't run when one throws is dropped, as it was with a copy of the list made each frame
        try {
            foreach (var action in _actionsRunning) {
                action.Invoke();
            }
        } finally {
            _actionsRunning.Clear();
        }
    }
}
