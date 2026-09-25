using System;
using UnityEngine;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// Tells the entity whose copy it is on of anything that starts touching the copy (see
/// <see cref="Entity.ListenForTouches"/>).
/// </summary>
internal class TouchListener : MonoBehaviour {
    /// <summary>
    /// Called with the collider that started touching the copy.
    /// </summary>
    public event Action<Collider2D>? Touched;

    private void OnTriggerEnter2D(Collider2D other) {
        Touched?.Invoke(other);
    }
}
