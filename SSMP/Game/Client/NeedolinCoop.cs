using System.Collections.Generic;
using System.Reflection;
using GlobalSettings;
using UnityEngine;
using AnimationClip = SSMP.Animation.AnimationClip;

namespace SSMP.Game.Client;

/// <summary>
/// Tracks the players in the local scene that are playing the needolin and works out how strongly a target is
/// affected by them, so that needolin range checks count every performing Hornet instead of only the local one.
/// </summary>
internal static class NeedolinCoop {
    /// <summary>
    /// Binding flags for the serialized fields of <see cref="HeroPerformanceRegion"/>.
    /// </summary>
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Animation clips that a player shows while the game considers them to be performing. The hero's needolin FSM
    /// templates (needolin_play_sub and needolin_play_prompted) turn performing on right before the play loop, keep it
    /// on while turning or playing the high and low tunes, and turn it off in the states that play the end clips.
    /// </summary>
    private static readonly HashSet<AnimationClip> PerformingClips = [
        AnimationClip.NeedolinPlay,
        AnimationClip.NeedolinSitPlay,
        AnimationClip.NeedolinTurn,
        AnimationClip.NeedolinSitTurn,
        AnimationClip.NeedolinPlayHigh,
        AnimationClip.NeedolinPlayHighTransition,
        AnimationClip.NeedolinPlayLow,
        AnimationClip.NeedolinPlayLowTransition
    ];

    /// <summary>
    /// Size of the inner needolin range on the Hero_Hornet prefab, used until the local range can be read.
    /// </summary>
    private static readonly Vector2 DefaultInnerSize = new(17f, 12f);

    /// <summary>
    /// Size of the outer needolin range on the Hero_Hornet prefab, used until the local range can be read.
    /// </summary>
    private static readonly Vector2 DefaultOuterSize = new(26f, 19f);

    /// <summary>
    /// Reflected private static field holding the local hero's <see cref="HeroPerformanceRegion"/>.
    /// </summary>
    private static readonly FieldInfo? RegionInstanceField = typeof(HeroPerformanceRegion).GetField(
        "_instance",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
    );

    /// <summary>
    /// Reflected field with the size of the inner needolin range.
    /// </summary>
    private static readonly FieldInfo? RegionInnerSizeField =
        typeof(HeroPerformanceRegion).GetField("innerSize", InstanceFlags);

    /// <summary>
    /// Reflected field with the size of the outer needolin range.
    /// </summary>
    private static readonly FieldInfo? RegionOuterSizeField =
        typeof(HeroPerformanceRegion).GetField("outerSize", InstanceFlags);

    /// <summary>
    /// Reflected field with the offset of the needolin ranges from the hero.
    /// </summary>
    private static readonly FieldInfo? RegionCentreOffsetField =
        typeof(HeroPerformanceRegion).GetField("centreOffset", InstanceFlags);

    /// <summary>
    /// Remote players that are currently performing, by player object.
    /// </summary>
    private static readonly Dictionary<GameObject, NeedolinPerformer> RemotePerformers = new();

    /// <summary>
    /// The local player as a performer.
    /// </summary>
    private static readonly NeedolinPerformer LocalPerformer = new() { IsLocal = true };

    /// <summary>
    /// Whether the local player was performing in the previous frame, to detect when they start.
    /// </summary>
    private static bool _localWasPerforming;

    /// <summary>
    /// The frame in which the range sizes were last read from the local hero.
    /// </summary>
    private static int _rangeShapeFrame = -1;

    /// <summary>
    /// The size of the inner needolin range without the Musician Charm.
    /// </summary>
    private static Vector2 _innerSize = DefaultInnerSize;

    /// <summary>
    /// The size of the outer needolin range without the Musician Charm.
    /// </summary>
    private static Vector2 _outerSize = DefaultOuterSize;

    /// <summary>
    /// The offset of the needolin ranges from the performer.
    /// </summary>
    private static Vector2 _centreOffset;

    /// <summary>
    /// Whether any remote player in the local scene is performing.
    /// </summary>
    public static bool HasRemotePerformers => RemotePerformers.Count > 0;

    /// <summary>
    /// Whether the local player has the Musician Charm (Spider Strings) equipped, which enlarges the needolin ranges and
    /// makes enemies sing longer.
    /// </summary>
    /// <returns>true if the Musician Charm is equipped; otherwise false.</returns>
    public static bool IsLocalMusicianCharmEquipped() {
        var tool = Gameplay.MusicianCharmTool;
        return tool && tool.IsEquipped;
    }

    /// <summary>
    /// Stores the time at which the local player starts performing. Called every frame.
    /// </summary>
    public static void UpdateLocalPerformer() {
        var isPerforming = HeroPerformanceRegion.IsPerforming;
        if (isPerforming && !_localWasPerforming) {
            LocalPerformer.StartTime = Time.time;
        }

        _localWasPerforming = isPerforming;
    }

    /// <summary>
    /// Updates whether a remote player is performing from an animation clip they started playing.
    /// </summary>
    /// <param name="playerObject">The player object of the remote player.</param>
    /// <param name="clip">The animation clip that the remote player plays.</param>
    public static void OnRemoteAnimation(GameObject playerObject, AnimationClip clip) {
        if (!PerformingClips.Contains(clip)) {
            RemotePerformers.Remove(playerObject);
            return;
        }

        if (!RemotePerformers.ContainsKey(playerObject)) {
            RemotePerformers[playerObject] = new NeedolinPerformer {
                Object = playerObject,
                StartTime = Time.time
            };
        }
    }

    /// <summary>
    /// Sets whether a performing remote player has the Musician Charm equipped, as sent with their play clips.
    /// </summary>
    /// <param name="playerObject">The player object of the remote player.</param>
    /// <param name="isEquipped">Whether the Musician Charm is equipped.</param>
    public static void SetRemoteMusicianCharm(GameObject playerObject, bool isEquipped) {
        if (RemotePerformers.TryGetValue(playerObject, out var performer)) {
            performer.HasMusicianCharm = isEquipped;
        }
    }

    /// <summary>
    /// Stops counting a remote player as a performer, for example because they left the scene.
    /// </summary>
    /// <param name="playerObject">The player object of the remote player.</param>
    public static void RemoveRemotePerformer(GameObject? playerObject) {
        if (playerObject is not null) {
            RemotePerformers.Remove(playerObject);
        }
    }

    /// <summary>
    /// Stops counting all remote players as performers.
    /// </summary>
    public static void ClearRemotePerformers() {
        RemotePerformers.Clear();
    }

    /// <summary>
    /// Works out how strongly a target is affected by the performing players and by which of them. A target in an
    /// inner range is affected more than one in an outer range, and between performers with the same effect the one
    /// who started playing last wins.
    /// </summary>
    /// <param name="target">The transform whose position is checked.</param>
    /// <param name="ignoreRange">Whether the check ignores range, so that any performer affects the target fully.</param>
    /// <param name="radius">The radius around the target for checks that use one; otherwise 0.</param>
    /// <param name="includeLocal">Whether to count the local player.</param>
    /// <param name="includeRemote">Whether to count remote players.</param>
    /// <param name="performer">The performer with the strongest effect, or null if nobody affects the target.</param>
    /// <returns>The strongest affected state.</returns>
    public static HeroPerformanceRegion.AffectedState Evaluate(
        Transform target,
        bool ignoreRange,
        float radius,
        bool includeLocal,
        bool includeRemote,
        out NeedolinPerformer? performer
    ) {
        var state = HeroPerformanceRegion.AffectedState.None;
        performer = null;

        var hero = HeroController.instance;
        if (includeLocal && hero != null && HeroPerformanceRegion.IsPerforming) {
            LocalPerformer.Object = hero.gameObject;
            LocalPerformer.HasMusicianCharm = IsLocalMusicianCharmEquipped();
            Consider(LocalPerformer, target.position, ignoreRange, radius, ref state, ref performer);
        }

        if (!includeRemote) {
            return state;
        }

        foreach (var remotePerformer in RemotePerformers.Values) {
            if (remotePerformer.Object != null && remotePerformer.Object.activeInHierarchy) {
                Consider(remotePerformer, target.position, ignoreRange, radius, ref state, ref performer);
            }
        }

        return state;
    }

    /// <summary>
    /// Checks whether any remote performer is within a circular range of a position, like
    /// <see cref="HeroPerformanceRegion.IsPlayingInRange"/> does for the local player.
    /// </summary>
    /// <param name="position">The position to check.</param>
    /// <param name="range">The distance from the performer at which the position counts as in range.</param>
    /// <returns>true if a remote performer is in range; otherwise false.</returns>
    public static bool IsRemotePerformerInRange(Vector2 position, float range) {
        if (RemotePerformers.Count == 0) {
            return false;
        }

        RefreshRangeShape();

        foreach (var remotePerformer in RemotePerformers.Values) {
            if (remotePerformer.Object == null || !remotePerformer.Object.activeInHierarchy) {
                continue;
            }

            var centre = (Vector2) remotePerformer.Object.transform.position + _centreOffset;
            if (Vector2.Distance(centre, position) <= range) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the stronger of two affected states: inner range over outer range over not affected.
    /// </summary>
    /// <param name="first">The first affected state.</param>
    /// <param name="second">The second affected state.</param>
    /// <returns>The stronger state, or the first one if they are equally strong.</returns>
    public static HeroPerformanceRegion.AffectedState GetStronger(
        HeroPerformanceRegion.AffectedState first,
        HeroPerformanceRegion.AffectedState second
    ) {
        return GetStrength(second) > GetStrength(first) ? second : first;
    }

    /// <summary>
    /// Replaces the current strongest performer with the given candidate if it affects the target more strongly, or
    /// equally strongly while having started playing later.
    /// </summary>
    private static void Consider(
        NeedolinPerformer candidate,
        Vector3 targetPosition,
        bool ignoreRange,
        float radius,
        ref HeroPerformanceRegion.AffectedState state,
        ref NeedolinPerformer? performer
    ) {
        var candidateState = GetAffectedState(candidate, targetPosition, ignoreRange, radius);
        var candidateStrength = GetStrength(candidateState);
        if (candidateStrength == 0) {
            return;
        }

        var strength = GetStrength(state);
        if (candidateStrength > strength ||
            candidateStrength == strength && performer != null && candidate.StartTime > performer.StartTime) {
            state = candidateState;
            performer = candidate;
        }
    }

    /// <summary>
    /// Gets how strongly a target position is affected by one performer, mirroring
    /// HeroPerformanceRegion.InternalGetAffectedRange and InternalGetAffectedRangeWithRadius with the Musician Charm
    /// of that performer.
    /// </summary>
    private static HeroPerformanceRegion.AffectedState GetAffectedState(
        NeedolinPerformer performer,
        Vector3 targetPosition,
        bool ignoreRange,
        float radius
    ) {
        if (ignoreRange) {
            return HeroPerformanceRegion.AffectedState.ActiveInner;
        }

        RefreshRangeShape();

        var centre = (Vector2) performer.Object.transform.position + _centreOffset;
        var position = (Vector2) targetPosition;
        if (radius > Mathf.Epsilon) {
            position = GetPositionInRadius(centre, position, radius);
        }

        var multiplier = performer.HasMusicianCharm ? Gameplay.MusicianCharmNeedolinRangeMult : 1f;
        if (IsInBox(position, centre, _innerSize * multiplier)) {
            return HeroPerformanceRegion.AffectedState.ActiveInner;
        }

        return IsInBox(position, centre, _outerSize * multiplier)
            ? HeroPerformanceRegion.AffectedState.ActiveOuter
            : HeroPerformanceRegion.AffectedState.None;
    }

    /// <summary>
    /// Gets the strength of an affected state for comparing performers.
    /// </summary>
    private static int GetStrength(HeroPerformanceRegion.AffectedState state) {
        return state switch {
            HeroPerformanceRegion.AffectedState.ActiveInner => 2,
            HeroPerformanceRegion.AffectedState.ActiveOuter => 1,
            _ => 0
        };
    }

    /// <summary>
    /// Reads the range sizes and offset from the local hero's region, at most once per frame. They are the same for
    /// every Hornet, so they also apply to remote performers.
    /// </summary>
    private static void RefreshRangeShape() {
        if (_rangeShapeFrame == Time.frameCount) {
            return;
        }

        _rangeShapeFrame = Time.frameCount;

        if (RegionInstanceField?.GetValue(null) is not HeroPerformanceRegion region || region == null) {
            return;
        }

        if (RegionInnerSizeField?.GetValue(region) is Vector2 innerSize) {
            _innerSize = innerSize;
        }

        if (RegionOuterSizeField?.GetValue(region) is Vector2 outerSize) {
            _outerSize = outerSize;
        }

        if (RegionCentreOffsetField?.GetValue(region) is Vector2 centreOffset) {
            _centreOffset = centreOffset;
        }
    }

    /// <summary>
    /// Moves a target position towards the range centre by at most the target's radius, like
    /// HeroPerformanceRegion.GetPosInRadius.
    /// </summary>
    private static Vector2 GetPositionInRadius(Vector2 centre, Vector2 position, float radius) {
        var offset = centre - position;
        return position + Mathf.Clamp(offset.magnitude, 0f, radius) * offset.normalized;
    }

    /// <summary>
    /// Checks whether a position is inside a box around a centre, like HeroPerformanceRegion.IsInRange.
    /// </summary>
    private static bool IsInBox(Vector2 position, Vector2 centre, Vector2 size) {
        var halfSize = size * 0.5f;
        return position.x >= centre.x - halfSize.x && position.x <= centre.x + halfSize.x &&
               position.y >= centre.y - halfSize.y && position.y <= centre.y + halfSize.y;
    }
}

/// <summary>
/// A player that is playing the needolin.
/// </summary>
internal sealed class NeedolinPerformer {
    /// <summary>
    /// The player object of the performer: the local hero or a remote player object.
    /// </summary>
    public GameObject Object = null!;

    /// <summary>
    /// Whether this is the local player.
    /// </summary>
    public bool IsLocal;

    /// <summary>
    /// Whether the performer has the Musician Charm equipped.
    /// </summary>
    public bool HasMusicianCharm;

    /// <summary>
    /// The time at which the performer started playing.
    /// </summary>
    public float StartTime;
}
