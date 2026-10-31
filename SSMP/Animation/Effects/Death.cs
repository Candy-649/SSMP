using System.Collections;
using HutongGames.PlayMaker.Actions;
using SSMP.Internals;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation.Effects;

/// <summary>
/// Animation effect for the death of the player. The character plays the death clip that comes with it, and a death by
/// frost also shows the frost growing over them and their frozen figure, as their own game does.
///
/// Nothing else is drawn on the character. The rest of what the game plays over a dying hero closes the screen of the
/// player who died, and the cocoon that a death leaves is shown by the two-player save where the player lies, with the
/// spiders that fall from it as it appears. This effect used to switch on the hero's 'Low Health Leak' around the
/// character for four seconds, a part that the game itself never plays, and to let the spiders of a cocoon fall where
/// the character stood four seconds later. A partner pulled up within those seconds fought on with both around them
/// (USER 10-31: "像碎片从她身上飞出来", "可是她后来打小水母的时候身上还有那个特效").
/// </summary>
internal class Death : AnimationEffect {
    /// <inheritdoc/>
    public override byte[] GetEffectInfo() {
        var frosted = HeroController.instance.cState.isFrostDeath;

        byte[] info = [
            (byte)(frosted ? 1 : 0)
        ];

        return info;
    }

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        // Read one at a time rather than matched as a whole, so that the two answers stay separate and a message
        // from a game that only had the first of them to send still means what it meant
        var byFrost = effectInfo is { Length: > 0 } && effectInfo[0] == 1;
        var nonLethal = effectInfo is { Length: > 1 } && effectInfo[1] == 1;

        // Said out loud, because this is the only place a death of the other player leaves any mark at all on this
        // game. Their own game can miss it entirely - a death that is not lethal, and a death the room itself deals,
        // both go through without a word - so when one player says they watched the other die over and over, this
        // is the line that can agree with them.
        Logger.Info(
            $"The death of the other player is being played out here, at {playerObject.transform.position}" +
            (byFrost ? ", by frost" : "") +
            (nonLethal ? ", and it was not a lethal one" : "")
        );

        if (byFrost) {
            MonoBehaviourUtil.Instance.StartCoroutine(PlayFrostDeath(playerObject));
        }
    }

    /// <summary>
    /// Plays the frost death animation.
    /// </summary>
    /// <param name="playerObject">The GameObject representing the player.</param>
    private static IEnumerator PlayFrostDeath(GameObject playerObject) {
        // Find frost death prefab
        var prefab = HeroController.instance.heroDeathFrostPrefab;
        if (prefab == null) {
            yield break;
        }

        // Play freeze audio
        var fsm = prefab.LocateMyFSM("Hero Death Anim");
        var audio = fsm.GetFirstAction<AudioPlayerOneShotSingle>("Start");
        AudioUtil.PlayAudio(audio, playerObject);

        // Spawn the two crystal growth particle systems
        var localCrystalSlow = prefab.FindGameObjectInChildren("Particle_Crystal Slow");
        var localCrystalFinal = prefab.FindGameObjectInChildren("Particle_Crystal Final");

        const float growthTime = 3f;
        var crystalSlow = EffectUtils.SpawnGlobalPoolObject(
            localCrystalSlow, 
            playerObject.transform, 
            growthTime, 
            true
        );
        var crystalFinal = EffectUtils.SpawnGlobalPoolObject(
            localCrystalFinal, 
            playerObject.transform, 
            growthTime, 
            true
        );

        if (crystalSlow == null || crystalFinal == null) {
            yield break;
        }

        // Reset their positions
        crystalSlow.transform.localPosition = new Vector3(-0.32f, 0, -2.22f);
        crystalFinal.transform.localPosition = new Vector3(-0.22f, -0.04f, -2.2f);

        yield return new WaitForSeconds(growthTime);

        // Transition from growing the crystals to exploding them
        var localDestroyEffects = prefab.FindGameObjectInChildren("Destroy Effects");
        var destroyEffects = EffectUtils.SpawnGlobalPoolObject(
            localDestroyEffects, 
            playerObject.transform, 
            5
        );

        destroyEffects?.DestroyComponent<CameraShakeOnEnable>();

        // Hide the player. They're shown as a separate frosted object in the next step
        HidePlayer(playerObject);

        // Spawn the frosted hornet object
        const int frostedDuration = 3;
        var localFrostDeath = prefab.FindGameObjectInChildren("Hornet_Frosted");
        var frostDeath = EffectUtils.SpawnGlobalPoolObject(
            localFrostDeath, 
            playerObject.transform, 
            frostedDuration + 1.1f
        );
        if (frostDeath == null) {
            yield break;
        }

        frostDeath.transform.localPosition = playerObject.transform.position + new Vector3(0.11f, 0, -0.18f);

        // Fade out
        yield return new WaitForSeconds(frostedDuration);
        frostDeath.AddComponent<SimpleFadeOut>();
    }
}
