using System;
using SSMP.Internals;
using UnityEngine;

namespace SSMP.Animation.Effects;

/// <summary>
/// What the round of a game that each player plays on their own shows, sent by the player who plays it for the players
/// who watch it (see SSMP.Game.Client.MinigameCoop). It goes the way the animations of a player go, which only reaches
/// the players in the same room and may be lost on the way, which suits a picture that is sent again many times a
/// second.
/// </summary>
internal class MinigameView : AnimationEffect {
    /// <summary>
    /// The single instance, which <see cref="AnimationManager"/> plays for every player.
    /// </summary>
    public static readonly MinigameView Instance = new();

    /// <summary>
    /// Takes a piece of what the round of another player shows, with the character of that player.
    /// </summary>
    public static Action<GameObject, byte[]>? Received { get; set; }

    /// <inheritdoc/>
    public override byte[]? GetEffectInfo() {
        return null;
    }

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        if (effectInfo is { Length: > 0 }) {
            Received?.Invoke(playerObject, effectInfo);
        }
    }
}
