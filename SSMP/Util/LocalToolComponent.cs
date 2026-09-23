using UnityEngine;

namespace SSMP.Util;

/// <summary>
/// Marks a thing that the local player threw or set with a tool, like a barbed shard, as theirs. In the game only the
/// player who threw one can set it off, so the copies of the partner's attacks that this game shows must not set this
/// one off either, the same way as the local player's attacks must not set off the partner's.
/// </summary>
internal class LocalToolComponent : MonoBehaviour {
    /// <summary>
    /// Whether the given GameObject is part of something that the local player threw or set with a tool. Also checks
    /// the parents of the object, since a tool has child objects that do their own collision handling.
    /// </summary>
    /// <param name="gameObject">The GameObject to check.</param>
    /// <returns>true if the GameObject or any of its parents is a tool of the local player; otherwise false.</returns>
    public static bool IsLocalTool(GameObject? gameObject) {
        return gameObject != null && gameObject.GetComponentInParent<LocalToolComponent>(true) != null;
    }
}
