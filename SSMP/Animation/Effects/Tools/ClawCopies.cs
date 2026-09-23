using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// The state of a thing that is moved by its own code rather than by a state machine, which the thrower's game sends
/// whenever something that only it knows of changed it.
/// </summary>
internal interface IToolState {
    /// <summary>
    /// Changes what the copies of the thing are made from, for what a copy of it must not do by itself.
    /// </summary>
    void PrepareCopyPrefab(GameObject copyPrefab);

    /// <summary>
    /// Sets up a new copy once it is switched on, for what the thing does by itself as it starts that a copy must not
    /// do, or does the way of the local player rather than the thrower.
    /// </summary>
    /// <param name="copy">The copy.</param>
    /// <param name="poisoned">Whether the thrower has the pouch that poisons their tools.</param>
    void PrepareCopy(GameObject copy, bool poisoned);

    /// <summary>
    /// Whether the thing has taken the change in full, so that its state can be sent. A thing may only settle on its
    /// next physics step.
    /// </summary>
    bool IsSettled(GameObject thing);

    /// <summary>
    /// Writes the state of a thing of the local player.
    /// </summary>
    byte[] Write(GameObject thing);

    /// <summary>
    /// Puts a copy in the state that the thrower's thing was in.
    /// </summary>
    void Apply(GameObject copy, byte[] state);

    /// <summary>
    /// Breaks a copy as the thrower's thing broke.
    /// </summary>
    /// <param name="copy">The copy.</param>
    /// <param name="how">How the thing broke, for a thing that can break in more than one way.</param>
    void Break(GameObject copy, byte how);
}

/// <summary>
/// The curved claws, which fly out along a curve and back, go off in a straight line when the thrower hits them, stick
/// in walls and break. Their flight is worked out by their own code from where they started and a random tilt, so the
/// copy takes the flight of the thrower's claws over rather than rolling its own tilt, and again whenever the thrower's
/// claws were hit or knocked back off a blocking enemy, which only the thrower's game can know of.
/// </summary>
internal class ClawState : IToolState {
    /// <summary>
    /// The single instance.
    /// </summary>
    public static readonly ClawState Instance = new();

    /// <summary>
    /// The state that the claws are broken in.
    /// </summary>
    private const int BrokenState = 3;

    /// <summary>
    /// The state in which the claws fly under their own speed rather than along their curve.
    /// </summary>
    private const int BodyState = 1;

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly FieldInfo? DoSetupField = typeof(ToolBoomerang).GetField("doSetup", Flags);
    private static readonly FieldInfo? StateField = typeof(ToolBoomerang).GetField("currentState", Flags);
    private static readonly FieldInfo? InitialPositionField = typeof(ToolBoomerang).GetField("initialPosition", Flags);
    private static readonly FieldInfo? TargetPositionField = typeof(ToolBoomerang).GetField("targetPosition", Flags);
    private static readonly FieldInfo? PreviousPositionField =
        typeof(ToolBoomerang).GetField("previousPosition", Flags);
    private static readonly FieldInfo? ElapsedTimeField = typeof(ToolBoomerang).GetField("elapsedTime", Flags);
    private static readonly FieldInfo? BodyVelocityField = typeof(ToolBoomerang).GetField("bodyVelocity", Flags);
    private static readonly FieldInfo? FlattenField = typeof(ToolBoomerang).GetField("doFlattenVelocity", Flags);
    private static readonly FieldInfo? DamagerField = typeof(ToolBoomerang).GetField("damager", Flags);
    private static readonly FieldInfo? AnimatorField = typeof(ToolBoomerang).GetField("animator", Flags);
    private static readonly FieldInfo? FlyAnimField = typeof(ToolBoomerang).GetField("flyAnim", Flags);
    private static readonly FieldInfo? LerpTimeField = typeof(ToolBoomerang).GetField("damageVelocityLerpTime", Flags);
    private static readonly FieldInfo? CurrentLerpTimeField =
        typeof(ToolBoomerang).GetField("currentDamageVelocityLerpTime", Flags);

    /// <inheritdoc/>
    public void PrepareCopyPrefab(GameObject copyPrefab) {
    }

    /// <inheritdoc/>
    public void PrepareCopy(GameObject copy, bool poisoned) {
    }

    /// <inheritdoc/>
    public bool IsSettled(GameObject thing) {
        return thing.TryGetComponent<ToolBoomerang>(out var claws) && DoSetupField?.GetValue(claws) is false;
    }

    /// <inheritdoc/>
    public byte[] Write(GameObject thing) {
        var claws = thing.GetComponent<ToolBoomerang>();
        var damager = DamagerField?.GetValue(claws) as DamageEnemies;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte) Convert.ToInt32(StateField?.GetValue(claws) ?? 0));
        WriteVector(writer, (Vector2) (InitialPositionField?.GetValue(claws) ?? Vector2.zero));
        WriteVector(writer, (Vector2) (TargetPositionField?.GetValue(claws) ?? Vector2.zero));
        writer.Write((float) (ElapsedTimeField?.GetValue(claws) ?? 0f));
        WriteVector(writer, (Vector2) (BodyVelocityField?.GetValue(claws) ?? Vector2.zero));
        writer.Write((bool) (FlattenField?.GetValue(claws) ?? false));
        writer.Write(damager != null ? damager.direction : 0f);
        writer.Write(thing.transform.localScale.x);
        writer.Flush();
        return stream.ToArray();
    }

    /// <inheritdoc/>
    public void Apply(GameObject copy, byte[] state) {
        if (!copy.TryGetComponent<ToolBoomerang>(out var claws)) {
            return;
        }

        using var reader = new BinaryReader(new MemoryStream(state));
        var currentState = (int) reader.ReadByte();
        var initialPosition = ReadVector(reader);
        var targetPosition = ReadVector(reader);
        var elapsedTime = reader.ReadSingle();
        var bodyVelocity = ReadVector(reader);
        var flatten = reader.ReadBoolean();
        var direction = reader.ReadSingle();
        var scaleX = reader.ReadSingle();

        var transform = copy.transform;
        var scale = transform.localScale;
        transform.localScale = new Vector3(scaleX, scale.y, scale.z);

        // What the claws set up on their first physics step, taken from the thrower's claws instead
        DoSetupField?.SetValue(claws, false);
        if (StateField != null) {
            StateField.SetValue(claws, Enum.ToObject(StateField.FieldType, currentState));
        }

        InitialPositionField?.SetValue(claws, initialPosition);
        TargetPositionField?.SetValue(claws, targetPosition);
        PreviousPositionField?.SetValue(claws, (Vector2) transform.position);
        ElapsedTimeField?.SetValue(claws, elapsedTime);
        BodyVelocityField?.SetValue(claws, bodyVelocity);
        FlattenField?.SetValue(claws, flatten);
        if (CurrentLerpTimeField != null && LerpTimeField != null) {
            CurrentLerpTimeField.SetValue(claws, LerpTimeField.GetValue(claws));
        }

        if (DamagerField?.GetValue(claws) is DamageEnemies damager) {
            damager.direction = direction;
            damager.enabled = true;
        }

        if (copy.TryGetComponent<Rigidbody2D>(out var body)) {
            body.bodyType = currentState != BodyState ? RigidbodyType2D.Kinematic : RigidbodyType2D.Dynamic;
        }

        if (AnimatorField?.GetValue(claws) is tk2dSpriteAnimator animator &&
            FlyAnimField?.GetValue(claws) is string fly &&
            (animator.CurrentClip == null || animator.CurrentClip.name != fly)) {
            animator.Play(fly);
        }
    }

    /// <inheritdoc/>
    public void Break(GameObject copy, byte how) {
        if (copy.TryGetComponent<ToolBoomerang>(out var claws) &&
            Convert.ToInt32(StateField?.GetValue(claws) ?? BrokenState) != BrokenState) {
            claws.Break();
        }
    }

    private static void WriteVector(BinaryWriter writer, Vector2 vector) {
        writer.Write(vector.x);
        writer.Write(vector.y);
    }

    private static Vector2 ReadVector(BinaryReader reader) {
        return new Vector2(reader.ReadSingle(), reader.ReadSingle());
    }
}
