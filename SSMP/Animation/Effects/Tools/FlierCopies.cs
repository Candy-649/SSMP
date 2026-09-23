using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// The flier, which follows the player who threw it wherever they go, even into other rooms, picks out enemies for
/// itself and dives at them, gets knocked about and bursts when it has taken enough. All of that is worked out around
/// the thrower, from what only their game has, so a copy of it does nothing by itself: the thrower's game sends where
/// the flier is and how it moves, what it plays and which of its parts show, several times a second, and the copy
/// follows. Since the flier goes into other rooms with the thrower, whatever is sent also makes the copy when there is
/// none yet, for a flier that came into the room with them.
/// </summary>
internal class FlierState : IToolState {
    /// <summary>
    /// The single instance.
    /// </summary>
    public static readonly FlierState Instance = new();

    /// <summary>
    /// The most parts of a flier that are sent, one bit each.
    /// </summary>
    private const int MaxParts = 16;

    /// <inheritdoc/>
    public void PrepareCopyPrefab(GameObject copyPrefab) {
        // The copy does nothing by itself: no mind of its own, nothing that tells it of what happens in this game,
        // nothing that touches anything, and moved only by what the thrower's game sends
        foreach (var flier in copyPrefab.GetComponentsInChildren<ClockworkHatchling>(true)) {
            flier.enabled = false;
        }

        foreach (var register in copyPrefab.GetComponentsInChildren<EventRegister>(true)) {
            Object.DestroyImmediate(register);
        }

        foreach (var responder in copyPrefab.GetComponentsInChildren<EventResponder>(true)) {
            Object.DestroyImmediate(responder);
        }

        foreach (var fsm in copyPrefab.GetComponentsInChildren<PlayMakerFSM>(true)) {
            fsm.enabled = false;
        }

        foreach (var collider in copyPrefab.GetComponentsInChildren<Collider2D>(true)) {
            collider.enabled = false;
        }

        if (copyPrefab.TryGetComponent<Rigidbody2D>(out var body)) {
            body.bodyType = RigidbodyType2D.Kinematic;
        }
    }

    /// <inheritdoc/>
    public bool IsSettled(GameObject thing) {
        return true;
    }

    /// <inheritdoc/>
    public byte[] Write(GameObject thing) {
        var animator = thing.GetComponent<tk2dSpriteAnimator>();
        var body = thing.GetComponent<Renderer>();
        var parts = GetParts(thing);

        ushort shown = 0;
        ushort emitting = 0;
        for (var i = 0; i < parts.Count; i++) {
            if (parts[i].activeSelf) {
                shown |= (ushort) (1 << i);
            }

            if (parts[i].TryGetComponent<ParticleSystem>(out var particles) && particles.isEmitting) {
                emitting |= (ushort) (1 << i);
            }
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(animator != null && animator.CurrentClip != null ? animator.CurrentClip.name : "");
        writer.Write(body == null || body.enabled);
        writer.Write(shown);
        writer.Write(emitting);
        writer.Flush();
        return stream.ToArray();
    }

    /// <inheritdoc/>
    public void Apply(GameObject copy, byte[] state) {
        using var reader = new BinaryReader(new MemoryStream(state));
        var clip = reader.ReadString();
        var bodyShown = reader.ReadBoolean();
        var shown = reader.ReadUInt16();
        var emitting = reader.ReadUInt16();

        if (copy.TryGetComponent<tk2dSpriteAnimator>(out var animator) && clip.Length > 0 &&
            (animator.CurrentClip == null || animator.CurrentClip.name != clip) &&
            animator.GetClipByName(clip) != null) {
            animator.Play(clip);
        }

        if (copy.TryGetComponent<Renderer>(out var body)) {
            body.enabled = bodyShown;
        }

        var parts = GetParts(copy);
        for (var i = 0; i < parts.Count; i++) {
            var part = parts[i];
            var on = (shown & (1 << i)) != 0;
            if (part.activeSelf != on) {
                part.SetActive(on);
            }

            if (!on || !part.TryGetComponent<ParticleSystem>(out var particles)) {
                continue;
            }

            var emits = (emitting & (1 << i)) != 0;
            if (emits && !particles.isEmitting) {
                particles.Play(false);
            } else if (!emits && particles.isEmitting) {
                particles.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            }
        }
    }

    /// <inheritdoc/>
    public void Break(GameObject copy) {
    }

    /// <summary>
    /// The parts of a flier that show something, in the same order on the thrower's flier and on a copy of it.
    /// </summary>
    private static List<GameObject> GetParts(GameObject thing) {
        var parts = new List<GameObject>();
        foreach (var transform in thing.GetComponentsInChildren<Transform>(true)) {
            if (transform == thing.transform || parts.Count >= MaxParts) {
                continue;
            }

            if (transform.TryGetComponent<Renderer>(out _) || transform.TryGetComponent<ParticleSystem>(out _)) {
                parts.Add(transform.gameObject);
            }
        }

        return parts;
    }
}
