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
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        NeedolinCoop.SetRemoteMusicianCharm(playerObject, effectInfo is { Length: > 0 } && effectInfo[0] == 1);
    }
}
