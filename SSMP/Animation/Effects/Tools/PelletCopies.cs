using System.IO;
using System.Reflection;
using UnityEngine;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// The pellets that the hero fires, which fly straight until they hit an enemy or a wall, and then, by chance, are gone
/// at once, shatter where they are, or bounce off broken, to shatter where they land or drop through the floor. What
/// a pellet hit and which way its chances went are only known to the thrower's game, so a copy never takes a hit by
/// itself - which would also drop into this game what a pellet can leave behind - and goes the way of the thrower's
/// pellet once that comes.
/// </summary>
internal class PelletState : IToolState {
    /// <summary>
    /// The single instance.
    /// </summary>
    public static readonly PelletState Instance = new();

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly FieldInfo? HasHitField = typeof(SimpleProjectile).GetField("hasHit", Flags);
    private static readonly FieldInfo? IsPoisonField = typeof(SimpleProjectile).GetField("isPoison", Flags);
    private static readonly FieldInfo? MeshRendererField = typeof(SimpleProjectile).GetField("meshRenderer", Flags);
    private static readonly FieldInfo? BreakOnLandField = typeof(SimpleProjectile).GetField("breakOnLand", Flags);
    private static readonly FieldInfo? AnimatorField = typeof(SimpleProjectile).GetField("animator", Flags);
    private static readonly FieldInfo? SpawnChanceField = typeof(SimpleProjectile).GetField("spawnChance", Flags);
    private static readonly FieldInfo? ShatterChanceField = typeof(SimpleProjectile).GetField("shatterChance", Flags);
    private static readonly FieldInfo? SolidChanceField =
        typeof(SimpleProjectile).GetField("brokenSolidChance", Flags);
    private static readonly MethodInfo? DidHitMethod = typeof(SimpleProjectile).GetMethod("DidHit", Flags);

    /// <summary>
    /// Whether a copy is being sent the way of the thrower's pellet, which is the only way it takes a hit.
    /// </summary>
    public static bool IsApplying { get; private set; }

    /// <inheritdoc/>
    public void PrepareCopyPrefab(GameObject copyPrefab) {
    }

    /// <inheritdoc/>
    public void PrepareCopy(GameObject copy, GameObject character, bool poisoned) {
        // A pellet reads the pouch of the local player as it starts, for the look it shatters with
        foreach (var pellet in copy.GetComponentsInChildren<SimpleProjectile>(true)) {
            IsPoisonField?.SetValue(pellet, poisoned);
        }
    }

    /// <inheritdoc/>
    public bool IsSettled(GameObject thing) {
        return true;
    }

    /// <inheritdoc/>
    public byte[] Write(GameObject thing) {
        var pellet = thing.GetComponent<SimpleProjectile>();
        var hasHit = HasHitField?.GetValue(pellet) is true;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(hasHit);
        if (hasHit) {
            var renderer = MeshRendererField?.GetValue(pellet) as Renderer;
            var animator = AnimatorField?.GetValue(pellet) as tk2dSpriteAnimator;
            writer.Write(renderer != null && !renderer.enabled);
            writer.Write(BreakOnLandField?.GetValue(pellet) is true);
            writer.Write(animator != null && animator.CurrentClip != null ? animator.CurrentClip.name : "");
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

        var shattered = reader.ReadBoolean();
        var breakOnLand = reader.ReadBoolean();
        var clip = reader.ReadString();

        if (!copy.TryGetComponent<SimpleProjectile>(out var pellet) || HasHitField?.GetValue(pellet) is not false ||
            DidHitMethod == null) {
            return;
        }

        // The pellet's own hit, with each of its chances set to go the way it went for the thrower. It is never gone
        // at once here, as that is the way that leaves something behind, and the thrower's pellet that went that way
        // ends its copy anyway.
        var spawnChance = SpawnChanceField?.GetValue(pellet);
        var shatterChance = ShatterChanceField?.GetValue(pellet);
        var solidChance = SolidChanceField?.GetValue(pellet);
        SpawnChanceField?.SetValue(pellet, -1f);
        ShatterChanceField?.SetValue(pellet, shattered ? 2f : -1f);
        SolidChanceField?.SetValue(pellet, breakOnLand ? 2f : -1f);

        // It bounces off the way the thrower's pellet did, which is in the motion that came with this
        var body = copy.GetComponent<Rigidbody2D>();
        var velocity = body != null ? body.linearVelocity : Vector2.zero;
        IsApplying = true;
        try {
            DidHitMethod.Invoke(pellet, new object?[] { null });
        } finally {
            IsApplying = false;
            SpawnChanceField?.SetValue(pellet, spawnChance);
            ShatterChanceField?.SetValue(pellet, shatterChance);
            SolidChanceField?.SetValue(pellet, solidChance);
        }

        if (shattered) {
            return;
        }

        if (body != null) {
            body.linearVelocity = velocity;
        }

        if (AnimatorField?.GetValue(pellet) is tk2dSpriteAnimator animator && clip.Length > 0 &&
            animator.GetClipByName(clip) != null) {
            animator.Play(clip);
        }
    }

    /// <inheritdoc/>
    public void Break(GameObject copy, byte how) {
    }
}
