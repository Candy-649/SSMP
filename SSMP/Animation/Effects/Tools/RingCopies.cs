using System.IO;
using System.Reflection;
using UnityEngine;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// The ring, which flies straight and bounces off walls and enemies in a random direction, towards the next enemy in
/// sight if there is one, until it has bounced enough or goes too long without a bounce and breaks; now and then it
/// hits a wall edge on and rolls off as a cog instead. Every bounce is random and depends on the enemies around it, so
/// the copy bounces off nothing by itself: the thrower's game sends where the ring is and where it goes after each
/// bounce, and the copy only flies on until then, stopped by a wall if it gets there first.
/// </summary>
internal class RingState : IToolState {
    /// <summary>
    /// The single instance.
    /// </summary>
    public static readonly RingState Instance = new();

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly FieldInfo? InertCogField = typeof(ToolRing).GetField("inertCog", Flags);
    private static readonly FieldInfo? EnemyRangeField = typeof(ToolRing).GetField("enemyRange", Flags);
    private static readonly MethodInfo? StopMethod = typeof(ToolRing).GetMethod("Stop", Flags);
    private static readonly MethodInfo? BreakMethod = typeof(ToolRing).GetMethod("Break", Flags);

    /// <inheritdoc/>
    public void PrepareCopyPrefab(GameObject copyPrefab) {
        // Switched off, the ring neither starts nor counts down by itself, and uses up none of the local player's
        // taunt as it starts; it still sets itself up and still gets told of what it runs into, which the hooks of
        // ToolCopies turn away for a copy
        if (!copyPrefab.TryGetComponent<ToolRing>(out var ring)) {
            return;
        }

        ring.enabled = false;
        if (EnemyRangeField?.GetValue(ring) is Component enemyRange) {
            foreach (var range in enemyRange.GetComponents<Collider2D>()) {
                range.enabled = false;
            }
        }
    }

    /// <inheritdoc/>
    public void PrepareCopy(GameObject copy, GameObject character, bool poisoned) {
    }

    /// <inheritdoc/>
    public bool IsSettled(GameObject thing) {
        return true;
    }

    /// <inheritdoc/>
    public byte[] Write(GameObject thing) {
        var cog = GetCog(thing);
        var rolling = cog != null && cog.gameObject.activeSelf;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(rolling);
        if (rolling) {
            var cogTransform = cog!.transform;
            var cogBody = cog.GetComponent<Rigidbody2D>();
            var velocity = cogBody != null ? cogBody.linearVelocity : Vector2.zero;
            writer.Write(cogTransform.position.x);
            writer.Write(cogTransform.position.y);
            writer.Write(cogTransform.eulerAngles.z);
            writer.Write(velocity.x);
            writer.Write(velocity.y);
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <inheritdoc/>
    public void Apply(GameObject copy, byte[] state) {
        using var reader = new BinaryReader(new MemoryStream(state));
        if (!reader.ReadBoolean()) {
            return;
        }

        var position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        var rotation = reader.ReadSingle();
        var velocity = new Vector2(reader.ReadSingle(), reader.ReadSingle());

        var cog = GetCog(copy);
        if (cog == null || cog.gameObject.activeSelf || !copy.TryGetComponent<ToolRing>(out var ring)) {
            return;
        }

        // What the ring does as it rolls off as a cog, with the cog going the way the thrower's went
        StopMethod?.Invoke(ring, null);
        var cogObject = cog.gameObject;
        cogObject.SetActive(true);
        cogObject.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, 0f, rotation));
        if (cogObject.TryGetComponent<Rigidbody2D>(out var cogBody)) {
            cogBody.linearVelocity = velocity;
        }
    }

    /// <inheritdoc/>
    public void Break(GameObject copy, byte how) {
        var cog = GetCog(copy);
        if (copy.TryGetComponent<ToolRing>(out var ring) && (cog == null || !cog.gameObject.activeSelf)) {
            BreakMethod?.Invoke(ring, null);
        }
    }

    /// <summary>
    /// The cog that a ring rolls off as.
    /// </summary>
    private static Component? GetCog(GameObject thing) {
        return thing.TryGetComponent<ToolRing>(out var ring) ? InertCogField?.GetValue(ring) as Component : null;
    }
}
