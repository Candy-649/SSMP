using System.Collections;
using System.Collections.Generic;
using GlobalSettings;
using SSMP.Internals;
using SSMP.Util;
using UnityEngine;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// Class for the tool effect of Flea Brew (attack buff).
/// </summary>
internal class FleaBrew : BaseAttackTool {
    private const string ParticlesName = "Flea Brew Particles";

    /// <summary>
    /// Cached modified versions of the poisoned flea brew trail that hurt the local player, one for each character of
    /// another player, which wears what that player wears (see HeroCopies).
    /// </summary>
    private static readonly Dictionary<GameObject, GameObject> HurtingPoisonTrails = new();

    /// <summary>
    /// Cached modified versions of the poisoned flea brew trail that do not hurt the local player, one for each
    /// character of another player. Each brew spawns its trail from one of the two for as long as it lasts, so setting
    /// up a single one for every brew changed the trails of brews still running: of another remote player, or from
    /// before PvP was turned off.
    /// </summary>
    private static readonly Dictionary<GameObject, GameObject> HarmlessPoisonTrails = new();

    /// <summary>
    /// Cached values of sprite flashes for Flea Brews.
    /// </summary>
    private static readonly Dictionary<int, SpriteFlash.FlashHandle> BrewFlashes = [];

    /// <summary>
    /// Instance of the effect class.
    /// </summary>
    public static readonly FleaBrew Instance = new();

    /// <inheritdoc/>
    public override byte[] GetEffectInfo() {
        return [
            (byte) (HasPoison() ? 1 : 0)
        ];
    }

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        var hc = HeroController.instance;
        var isPoison = EffectIsPoisoned(effectInfo);

        // Play audio
        var fsm = hc.toolsFSM;
        if (fsm != null) {
            var audio = fsm.GetFirstAction<PlayAudioEvent>("Flea Brew Burst");
            AudioUtil.PlayAudio(audio, playerObject);
        }

        // Start particles
        var duration = hc.QUICKENING_DURATION;
        var localPrefab = isPoison ? hc.quickeningPoisonEffectPrefab : hc.quickeningEffectPrefab;

        var particles = EffectUtils.SpawnGlobalPoolObject(
            localPrefab.gameObject, 
            playerObject.transform, 
            duration,
            true
        );
        if (particles == null) return;

        particles.name = ParticlesName;

        // Set up poison clouds
        if (isPoison) {
            SetPoisonTrail(playerObject, particles, ShouldDoDamage && ServerSettings.IsPvpEnabled);
        }

        // Play sprite flash
        if (!playerObject.TryGetComponent<SpriteFlash>(out var flash)) {
            return;
        }

        // See if previous effect is playing
        var id = playerObject.GetInstanceID();
        if (BrewFlashes.TryGetValue(id, out var prevHandle)) {
            if (flash.IsFlashing(true, prevHandle)) {
                flash.CancelRepeatingFlash(prevHandle);
            }
        }

        // Start new flash
        var color = isPoison ? Gameplay.PoisonPouchHeroTintColour : new Color(1f, 0.85f, 0.47f, 1f);
        var flashHandle = flash.Flash(
            color, 
            0.7f, 
            0.2f,
            0.01f,
            0.22f,
            0f,
            repeating: true,
            0,
            1,
            requireExplicitCancel: true
        );
        BrewFlashes[id] = flashHandle;

        MonoBehaviourUtil.Instance.StartCoroutine(StopBrewFlashAfterDelay(playerObject, flashHandle));

    }

    /// <summary>
    /// Stops the Flea Brew flashing and particles.
    /// </summary>
    /// <param name="playerObject">The player object with the Flea Brew animation.</param>
    public static void StopBrew(GameObject playerObject) {
        var id = playerObject.GetInstanceID();
        if (!BrewFlashes.TryGetValue(id, out var handle)) {
            return;
        }

        StopBrew(playerObject, handle);
    }

    /// <summary>
    /// Stops the Flea Brew flashing and particles.
    /// </summary>
    /// <param name="playerObject">The player object with the Flea Brew animation.</param>
    /// <param name="handle">The current sprite flash handle.</param>
    private static void StopBrew(GameObject playerObject, SpriteFlash.FlashHandle handle) {
        // Stop sprite flash
        if (playerObject.TryGetComponent<SpriteFlash>(out var flash)) {
            flash.CancelRepeatingFlash(handle);
        }

        // Stop particles
        var particles = playerObject.FindGameObjectInChildren(ParticlesName);
        if (particles) {
            Object.Destroy(particles);
        }
    }

    /// <summary>
    /// Stops the Flea Brew sprite flash after a delay.
    /// </summary>
    /// <param name="playerObject">The player that used the tool.</param>
    /// <param name="handle">The flash's handle.</param>
    private static IEnumerator StopBrewFlashAfterDelay(GameObject playerObject, SpriteFlash.FlashHandle handle) {
        // Wait for effect to end
        yield return new WaitForSeconds(HeroController.instance.QUICKENING_DURATION);

        // Cancel flash
        StopBrew(playerObject, handle);
    }

    /// <summary>
    /// Has the poisoned Flea Brew particles of a remote player spawn a copy of the poison trail: marked as that
    /// player's attack, without the poison that the trail leaves on creatures, and hurting the local player only with
    /// PvP on. Without PvP the particles spawned the trail of the game, which poisoned the creatures of this game while
    /// the trail in the game of the player who drank the brew poisoned theirs, and the game that runs a creature took
    /// the poison of both. The particles are made for this player alone, so the trail they spawn is theirs to change.
    /// </summary>
    /// <param name="playerObject">The character of the player who drank the brew.</param>
    /// <param name="particles">The poisoned Flea Brew particle spawner.</param>
    /// <param name="hurtsPlayers">Whether the trail hurts the local player.</param>
    private void SetPoisonTrail(GameObject playerObject, GameObject particles, bool hurtsPlayers) {
        // Find the prefab spawner
        var spawnerObj = particles.FindGameObjectInChildren("Trail Spawner");
        if (!spawnerObj) return;

        if (!spawnerObj.TryGetComponent<SpawnRepeatingSmart>(out var spawner)) {
            return;
        }

        // Set up a modified version of the prefab
        var trails = hurtsPlayers ? HurtingPoisonTrails : HarmlessPoisonTrails;
        if (!trails.TryGetValue(playerObject, out var trail) || !trail) {
            var prefab = spawner.prefab;
            if (!prefab) return;

            trail = MakePoisonTrail(prefab, particles.transform, hurtsPlayers);
            if (!trail) return;

            trails[playerObject] = trail;
        }

        // Set the damage every time the brew is used, as the damage amount can change
        if (hurtsPlayers) {
            var damager = trail.FindGameObjectInChildren("damager");
            if (damager) {
                SetDamageHeroState(damager, true, ServerSettings.PoisonBrewDamage);
            }
        }

        // Set the spawner's prefab
        spawner.prefab = trail;
    }

    /// <summary>
    /// Makes a modified version of the poisoned flea brew trail for remote players (see <see cref="SetPoisonTrail"/>).
    /// </summary>
    /// <param name="prefab">The trail of the game.</param>
    /// <param name="parent">Where to make it.</param>
    /// <param name="hurtsPlayers">Whether it hurts the local player.</param>
    /// <returns>The modified trail, or null if it could not be made.</returns>
    private GameObject? MakePoisonTrail(GameObject prefab, Transform parent, bool hurtsPlayers) {
        var trail = EffectUtils.SpawnGlobalPoolObject(prefab, parent, 0);
        if (!trail) return null;

        trail.SetActive(false);
        trail.name = hurtsPlayers ? "Hornet Poison Trail Hurting" : "Hornet Poison Trail Harmless";
        trail.AddComponentIfNotPresent<RemoteAttackComponent>();

        // The poison on creatures is counted in the game of the player who drank the brew, whose own trail leaves
        // it on them there
        foreach (var tagDamager in trail.GetComponentsInChildren<TagDamager>(true)) {
            Object.DestroyImmediate(tagDamager);
        }

        // Re-add recycler so that it de-spawns
        // Since this is a new object, it won't override the other pool
        var recycler = trail.AddComponent<AutoRecycleSelf>();
        recycler.afterEvent = GlobalEnums.AfterEvent.TIME;
        recycler.timeToWait = 1.1f;

        var damager = trail.FindGameObjectInChildren("damager");
        if (damager) {
            SetDamageHeroState(damager, hurtsPlayers, ServerSettings.PoisonBrewDamage);
            if (hurtsPlayers) {
                damager.layer = 17;
            }
        }

        return trail;
    }
}
