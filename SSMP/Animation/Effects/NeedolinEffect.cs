using SSMP.Game.Client;
using SSMP.Internals;
using UnityEngine;

namespace SSMP.Animation.Effects;

/// <summary>
/// Effect for the needolin play clips that tells other players whether the Musician Charm (Spider Strings) is
/// equipped. The charm enlarges the needolin ranges and makes enemies sing longer.
/// </summary>
internal class NeedolinEffect : AnimationEffect {
    /// <summary>
    /// The instance shared by all needolin play clips.
    /// </summary>
    public static readonly NeedolinEffect Instance = new();

    /// <inheritdoc/>
    public override byte[] GetEffectInfo() {
        return [(byte) (NeedolinCoop.IsLocalMusicianCharmEquipped() ? 1 : 0)];
    }

    /// <inheritdoc/>
    /// <remarks>Whether the charm is worn holds for the whole song. When the clip change that started a song was lost,
    /// the clip was put right without it, and the performer had the smaller ranges until their next needolin clip.
    /// </remarks>
    public override bool HoldsWhileClipPlays => true;

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        NeedolinCoop.SetRemoteMusicianCharm(playerObject, effectInfo is { Length: > 0 } && effectInfo[0] == 1);
    }
}
