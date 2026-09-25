using UnityEngine;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// Tells the entity whose room object it is on of anything solid that object bumps into (see OwnMotionComponent).
/// </summary>
internal class BumpListener : MonoBehaviour {
    /// <summary>
    /// Called when the object starts touching something solid.
    /// </summary>
    public event System.Action? Bumped;

    private void OnCollisionEnter2D(Collision2D collision) {
        Bumped?.Invoke();
    }
}
