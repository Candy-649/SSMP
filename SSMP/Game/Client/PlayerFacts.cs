using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using AnimationClip = SSMP.Animation.AnimationClip;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// What the game reads of its own player, of each player in the room: whether they swim, and whether they carry the
/// white flower. This game's player as the game has them; another player by what their game sends: their swimming by
/// the clips they play, as their needolin is (see NeedolinCoop), and their flower with the list of their things that
/// their game sends every second (see Animation.Effects.Tools.ToolCopies). A creature that reads these of the player
/// reads them of the player near it (see GamePatcher.ReadPlayerOf).
/// </summary>
internal static class PlayerFacts {
    /// <summary>
    /// The clips that a player plays while they swim (see HeroWaterController): coming into the water, keeping to its
    /// surface, turning and dashing in it, and the current tumbling them about in it.
    /// </summary>
    private static readonly HashSet<AnimationClip> SwimClips = [
        AnimationClip.SurfaceIn,
        AnimationClip.SurfaceInToIdle,
        AnimationClip.SurfaceIdle,
        AnimationClip.SurfaceIdleToSwim,
        AnimationClip.SurfaceTurnToSwim,
        AnimationClip.SurfaceCurrentInRecover,
        AnimationClip.SurfaceCurrentInTumble,
        AnimationClip.SwimDash,
        AnimationClip.SwimDashTurn,
        AnimationClip.SwimDashBonk,
        AnimationClip.SpaSurfaceIn,
        AnimationClip.SpaSurfaceInToIdle,
        AnimationClip.SpaSurfaceIdle,
        AnimationClip.SpaSurfaceIdleToSwim,
        AnimationClip.SpaSurfaceTurnToSwim
    ];

    /// <summary>
    /// What is known of each player of another game, by their figure.
    /// </summary>
    private static readonly ConditionalWeakTable<GameObject, Facts> Known = new();

    /// <summary>
    /// Whether a player swims.
    /// </summary>
    public static bool IsSwimming(GameObject player) {
        return player.GetComponent<HeroController>() is { } hero
            ? hero.cState.swimming
            : Known.TryGetValue(player, out var facts) && facts.Swimming;
    }

    /// <summary>
    /// Whether a player carries the white flower.
    /// </summary>
    public static bool HasWhiteFlower(GameObject player) {
        return player.GetComponent<HeroController>() != null
            ? PlayerData.instance.HasWhiteFlower
            : Known.TryGetValue(player, out var facts) && facts.WhiteFlower;
    }

    /// <summary>
    /// Whether a player plays the needolin.
    /// </summary>
    public static bool Performs(GameObject player) {
        return player.GetComponent<HeroController>() != null
            ? HeroPerformanceRegion.IsPerforming
            : NeedolinCoop.IsRemotePerforming(player);
    }

    /// <summary>
    /// Notes whether a player of another game swims by a clip that they started playing.
    /// </summary>
    public static void OnRemoteAnimation(GameObject player, AnimationClip clip) {
        var facts = Known.GetOrCreateValue(player);
        var swimming = SwimClips.Contains(clip);
        if (facts.Swimming != swimming) {
            facts.Swimming = swimming;
            Logger.Info($"[Player facts] '{player.name}' {(swimming ? "swims" : "stops swimming")}, playing {clip}");
        }
    }

    /// <summary>
    /// Notes whether a player of another game carries the white flower, as their game says every second.
    /// </summary>
    public static void SetRemoteWhiteFlower(GameObject player, bool carries) {
        var facts = Known.GetOrCreateValue(player);
        if (facts.WhiteFlower != carries) {
            facts.WhiteFlower = carries;
            Logger.Info($"[Player facts] '{player.name}' carries {(carries ? "the" : "no")} white flower");
        }
    }

    /// <summary>
    /// What is known of a player of another game.
    /// </summary>
    private sealed class Facts {
        /// <summary>
        /// Whether they swim.
        /// </summary>
        public bool Swimming;

        /// <summary>
        /// Whether they carry the white flower.
        /// </summary>
        public bool WhiteFlower;
    }
}
