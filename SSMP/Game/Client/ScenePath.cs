using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SSMP.Game.Client;

/// <summary>
/// Paths of objects in the loaded scenes, which find the same object in the games of all players. An object with the
/// same name as earlier siblings gets their number, like "Battle Gate [1]", so that paths stay unique.
/// </summary>
internal static class ScenePath {
    /// <summary>
    /// Gets the path of an object in its scene.
    /// </summary>
    public static string Get(Transform transform) {
        var names = new List<string>();
        for (var current = transform; current != null; current = current.parent) {
            names.Add(GetIndexedName(current));
        }

        names.Reverse();
        return string.Join("/", names);
    }

    /// <summary>
    /// Finds the object with the given path in the loaded scenes, including inactive objects.
    /// </summary>
    /// <param name="path">The path of the object in its scene.</param>
    /// <param name="sceneName">The name of the scene to look in, or null to look in all loaded scenes.</param>
    /// <returns>The object, or null if no loaded scene has it.</returns>
    public static GameObject? Find(string path, string? sceneName = null) {
        var segments = path.Split('/');

        for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++) {
            var scene = SceneManager.GetSceneAt(sceneIndex);
            if (!scene.isLoaded || sceneName != null && scene.name != sceneName) {
                continue;
            }

            ParseSegment(segments[0], out var rootName, out var rootIndex);
            Transform? current = null;
            foreach (var root in scene.GetRootGameObjects()) {
                if (root.name == rootName && rootIndex-- == 0) {
                    current = root.transform;
                    break;
                }
            }

            for (var i = 1; current != null && i < segments.Length; i++) {
                ParseSegment(segments[i], out var childName, out var childIndex);

                Transform? next = null;
                for (var j = 0; j < current.childCount; j++) {
                    var child = current.GetChild(j);
                    if (child.name == childName && childIndex-- == 0) {
                        next = child;
                        break;
                    }
                }

                current = next;
            }

            if (current != null) {
                return current.gameObject;
            }
        }

        return null;
    }

    /// <summary>
    /// The objects found by <see cref="FindByUniqueName"/>, by scene and name. What wasn't found is looked for again
    /// each time, since an object can be in a place left out of the search, or share its name, for a moment only.
    /// </summary>
    private static readonly Dictionary<string, Transform> UniqueNames = new();

    /// <summary>
    /// Finds the one object of a loaded scene that has the name a path ends in, wherever it is in the scene: for an
    /// object that this game has somewhere else than the game that named it. Null for a path that names one of several
    /// siblings of the same name, and when the scene has no object of that name or more than one.
    /// </summary>
    /// <param name="path">The path of the object in its scene.</param>
    /// <param name="sceneName">The name of the scene to look in.</param>
    /// <param name="leftOut">Whether an object, given with its name, is not to be looked at, nor anything under it.
    /// </param>
    /// <returns>The object, or null.</returns>
    public static GameObject? FindByUniqueName(string path, string sceneName, Func<Transform, string, bool> leftOut) {
        ParseSegment(path.Substring(path.LastIndexOf('/') + 1), out var name, out var index);
        if (index != 0) {
            return null;
        }

        // Looked for again once it is gone or called something else
        var key = sceneName + "\n" + name;
        if (UniqueNames.TryGetValue(key, out var known) && known != null && known.name == name) {
            return known.gameObject;
        }

        Transform? found = null;
        var count = 0;
        for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount && count < 2; sceneIndex++) {
            var scene = SceneManager.GetSceneAt(sceneIndex);
            if (!scene.isLoaded || scene.name != sceneName) {
                continue;
            }

            foreach (var root in scene.GetRootGameObjects()) {
                Look(root.transform);
            }
        }

        if (count != 1 || found == null) {
            UniqueNames.Remove(key);
            return null;
        }

        UniqueNames[key] = found;
        return found.gameObject;

        void Look(Transform transform) {
            // The name is made anew on each ask, so it is asked once
            var ownName = transform.name;
            if (count > 1 || leftOut(transform, ownName)) {
                return;
            }

            if (ownName == name) {
                found = transform;
                count++;
            }

            for (var i = 0; i < transform.childCount; i++) {
                Look(transform.GetChild(i));
            }
        }
    }

    /// <summary>
    /// Forgets what <see cref="FindByUniqueName"/> found, as a room is left.
    /// </summary>
    public static void ForgetUniqueNames() {
        UniqueNames.Clear();
    }

    /// <summary>
    /// Gets the name of an object, followed by the number of earlier siblings with the same name if there are any.
    /// </summary>
    private static string GetIndexedName(Transform transform) {
        var index = 0;
        var parent = transform.parent;
        if (parent != null) {
            for (var i = 0; i < transform.GetSiblingIndex(); i++) {
                if (parent.GetChild(i).name == transform.name) {
                    index++;
                }
            }
        } else {
            foreach (var root in transform.gameObject.scene.GetRootGameObjects()) {
                if (root.transform == transform) {
                    break;
                }

                if (root.name == transform.name) {
                    index++;
                }
            }
        }

        return index == 0 ? transform.name : $"{transform.name} [{index}]";
    }

    /// <summary>
    /// Splits a path segment into the object name and the number of earlier siblings with that name.
    /// </summary>
    private static void ParseSegment(string segment, out string name, out int index) {
        name = segment;
        index = 0;

        if (!segment.EndsWith("]", StringComparison.Ordinal)) {
            return;
        }

        var open = segment.LastIndexOf(" [", StringComparison.Ordinal);
        if (open < 0 || !int.TryParse(segment.Substring(open + 2, segment.Length - open - 3), out var parsed)) {
            return;
        }

        name = segment.Substring(0, open);
        index = parsed;
    }
}
