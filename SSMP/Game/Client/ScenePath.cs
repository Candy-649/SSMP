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
    /// <returns>The object, or null if no loaded scene has it.</returns>
    public static GameObject? Find(string path) {
        var segments = path.Split('/');

        for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++) {
            var scene = SceneManager.GetSceneAt(sceneIndex);
            if (!scene.isLoaded) {
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
