using System.Collections;
using System.Reflection;
using UnityEngine;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// The snare, which lies where it was set until an enemy walks into it and it bursts, or it fades when the thrower
/// rests at a bench or sets another snare right next to it. The enemy, the bench and the other snares are only known
/// to the thrower's game, so a copy never bursts or fades by itself, only when the thrower's snare does. Nor does it,
/// as it appears, end the snares next to it, which in this game are this player's own.
/// </summary>
internal class SnareState : IToolState {
    /// <summary>
    /// The single instance.
    /// </summary>
    public static readonly SnareState Instance = new();

    /// <summary>
    /// The way a snare breaks when it fades.
    /// </summary>
    public const byte Fade = 0;

    /// <summary>
    /// The way a snare breaks when it bursts.
    /// </summary>
    public const byte Burst = 1;

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly FieldInfo? GroundTriggerField = typeof(SilkSnare).GetField("groundTrigger", Flags);
    private static readonly FieldInfo? AppearRoutineField = typeof(SilkSnare).GetField("appearRoutine", Flags);
    private static readonly FieldInfo? EndRoutineField = typeof(SilkSnare).GetField("endRoutine", Flags);
    private static readonly FieldInfo? ActiveSnaresField = typeof(SilkSnare).GetField(
        "_activeSnares", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
    );
    private static readonly MethodInfo? BlastMethod = typeof(SilkSnare).GetMethod("Blast", Flags);
    private static readonly MethodInfo? EndMethod = typeof(SilkSnare).GetMethod("End", Flags);

    /// <summary>
    /// Whether a copy is being faded the way of the thrower's snare, which is the only way it fades.
    /// </summary>
    public static bool IsApplying { get; private set; }

    /// <inheritdoc/>
    public void PrepareCopyPrefab(GameObject copyPrefab) {
        // Without the trigger that an enemy walks into, the copy never bursts by itself
        foreach (var snare in copyPrefab.GetComponentsInChildren<SilkSnare>(true)) {
            if (GroundTriggerField?.GetValue(snare) is not Component trigger) {
                continue;
            }

            foreach (var collider in trigger.GetComponents<Collider2D>()) {
                collider.enabled = false;
            }
        }
    }

    /// <inheritdoc/>
    public void PrepareCopy(GameObject copy, GameObject character, bool poisoned) {
        foreach (var snare in copy.GetComponentsInChildren<SilkSnare>(true)) {
            // What a snare does a frame after it appears is end the snares right next to it, which here are this
            // player's own; it has played the look of appearing by then already
            if (AppearRoutineField?.GetValue(snare) is Coroutine appear) {
                snare.StopCoroutine(appear);
                AppearRoutineField.SetValue(snare, null);
            }

            // And this player's own snares, as they appear, leave it alone
            (ActiveSnaresField?.GetValue(null) as IList)?.Remove(snare);
        }
    }

    /// <inheritdoc/>
    public bool IsSettled(GameObject thing) {
        return true;
    }

    /// <inheritdoc/>
    public byte[] Write(GameObject thing) {
        return [];
    }

    /// <inheritdoc/>
    public void Apply(GameObject copy, byte[] state) {
    }

    /// <inheritdoc/>
    public void Break(GameObject copy, byte how) {
        if (!copy.TryGetComponent<SilkSnare>(out var snare)) {
            return;
        }

        if (how == Burst) {
            // What the snare does when an enemy walks into it
            if (EndRoutineField == null || EndRoutineField.GetValue(snare) != null ||
                BlastMethod?.Invoke(snare, null) is not IEnumerator blast) {
                return;
            }

            EndRoutineField.SetValue(snare, snare.StartCoroutine(blast));
            return;
        }

        IsApplying = true;
        try {
            EndMethod?.Invoke(snare, null);
        } finally {
            IsApplying = false;
        }
    }
}
