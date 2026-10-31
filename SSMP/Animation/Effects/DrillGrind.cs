using SSMP.Internals;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation.Effects;

/// <summary>
/// The grind of the down attack of the Architect crest into what it landed on. In the game of the player whose it is,
/// the hero plays 'Drill Grind' or 'Drill Grind Charged' as the drill lands, and the drill plays its grind with it,
/// 'Drill Grind Effect' or 'Drill Grind Effect Charged' (the hero's 'Crest Attacks' FSM, in 'Drill Grind Uncharged'
/// and 'Drill Grind Charged'), until the hero's clip ends and the drill is called off. Neither clip was known here,
/// so the other players saw the character go on with the plain drill (the friend's log of 10-31: "played unknown
/// clip, name: Drill Grind", 16 times in an hour). The clip of the hero now comes with this, and the grind is played
/// on the copy of the drill that the down attack made on the character (<see cref="DownSpike"/>). Both clips play
/// once and are shorter than a third of a second, and the copy goes when its clip ends, as it did before.
/// </summary>
internal class DrillGrind : AnimationEffect {
    /// <summary>
    /// Whether this is the grind of the fully charged down attack.
    /// </summary>
    private readonly bool _charged;

    /// <summary>
    /// Whether a grind found no drill to be played on, which is said once.
    /// </summary>
    private static bool _saidNoDrill;

    /// <param name="charged">Whether this is the grind of the fully charged down attack.</param>
    public DrillGrind(bool charged) {
        _charged = charged;
    }

    /// <inheritdoc/>
    public override byte[]? GetEffectInfo() {
        return null;
    }

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        var drillName = _charged ? DownSpike.ChargedDrillName : DownSpike.DrillName;
        var clipName = _charged ? "Drill Grind Effect Charged" : "Drill Grind Effect";

        var drill = FindDrill(playerObject, drillName);
        var animator = drill != null ? drill.GetComponent<tk2dSpriteAnimator>() : null;
        var clip = animator != null ? animator.GetClipByName(clipName) : null;
        if (animator == null || clip == null) {
            // The copy of the drill goes as its own clip ends, which a grind that came late can find it gone after
            if (!_saidNoDrill) {
                _saidNoDrill = true;
                Logger.Info(
                    $"The grind of '{playerObject.name}' found no {(drill == null ? "copy of the drill" : $"'{clipName}' on the drill")} " +
                    $"'{drillName}' to be played on (said once)"
                );
            }

            return;
        }

        // The end of this clip takes the copy away, as the end of the drill's own clip did (SlashBase)
        animator.Play(clip);
    }

    /// <summary>
    /// The copy of the drill of the last down attack on the character, or null when there is none.
    /// </summary>
    /// <param name="playerObject">The character.</param>
    /// <param name="drillName">The name of the drill on the hero.</param>
    private static GameObject? FindDrill(GameObject playerObject, string drillName) {
        var attacks = playerObject.FindGameObjectInChildren("Attacks");
        if (attacks == null) {
            return null;
        }

        // The last first: each attack puts its own parent at the end
        var copyName = drillName + "(Clone)";
        for (var i = attacks.transform.childCount - 1; i >= 0; i--) {
            var parent = attacks.transform.GetChild(i);
            if (!parent.gameObject.activeSelf) {
                continue;
            }

            foreach (Transform child in parent) {
                if (child.name == copyName || child.name == drillName) {
                    return child.gameObject;
                }
            }
        }

        return null;
    }
}
