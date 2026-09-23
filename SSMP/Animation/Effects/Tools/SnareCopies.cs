using System.Collections;
using System.Reflection;
using TeamCherry.NestedFadeGroup;
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
    private static readonly FieldInfo? FoundHeroField = typeof(SilkSnare).GetField("foundHero", Flags);
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

            // Its glow goes by how near the thrower is, not this player's hero, which it found as it appeared
            FoundHeroField?.SetValue(snare, false);
            snare.gameObject.AddComponent<SnareGlow>().Follow(snare, character.transform);
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

/// <summary>
/// Fades the glow of a copy of the partner's snare by how near the thrower's character is, the way the thrower's own
/// snare fades by how near the thrower is: in full close by, not at all from further off, and in between on the way.
/// </summary>
internal class SnareGlow : MonoBehaviour {
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly FieldInfo? GroundField = typeof(SilkSnare).GetField("groundFadeGroup", Flags);
    private static readonly FieldInfo? DistantField = typeof(SilkSnare).GetField("distantGroundFadeGroup", Flags);
    private static readonly FieldInfo? FullRadiusField = typeof(SilkSnare).GetField("fullEffectRadiusSqr", Flags);
    private static readonly FieldInfo? FalloffRadiusField = typeof(SilkSnare).GetField("falloffRadiusSqr", Flags);

    private Transform? _character;
    private NestedFadeGroupBase? _ground;
    private NestedFadeGroupBase? _distant;
    private float _fullRadiusSqr;
    private float _falloffRadiusSqr;

    /// <summary>
    /// Starts fading the glow of the snare by how near the character is.
    /// </summary>
    public void Follow(SilkSnare snare, Transform character) {
        _character = character;
        _ground = GroundField?.GetValue(snare) as NestedFadeGroupBase;
        _distant = DistantField?.GetValue(snare) as NestedFadeGroupBase;
        _fullRadiusSqr = FullRadiusField?.GetValue(snare) is float full ? full : 0f;
        _falloffRadiusSqr = FalloffRadiusField?.GetValue(snare) is float falloff ? falloff : 0f;
        LateUpdate();
    }

    private void LateUpdate() {
        if (_character == null || _ground == null || _distant == null) {
            return;
        }

        var distanceSqr = ((Vector2) _character.position - (Vector2) transform.position).sqrMagnitude;
        float near;
        if (distanceSqr >= _falloffRadiusSqr) {
            near = 0f;
        } else if (distanceSqr <= _fullRadiusSqr) {
            near = 1f;
        } else {
            near = 1f - (distanceSqr - _fullRadiusSqr) / (_falloffRadiusSqr - _fullRadiusSqr);
        }

        _ground.AlphaSelf = near;
        _distant.AlphaSelf = 1f - near;
    }
}
