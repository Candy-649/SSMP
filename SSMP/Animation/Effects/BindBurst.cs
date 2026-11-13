using HutongGames.PlayMaker.Actions;
using SSMP.Internals;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation.Effects;

/// <summary>
/// Class for the animation effect of a bind (healing) finishing.
/// </summary>
internal class BindBurst : Bind {
    // Effect object names
    private const string BeastCrestRageObjectName = "crest rage_burst_effect(Clone)";
    private const string WitchBindCleanseObjectName = "Witch Bind Maggot Cleanse";
    private const string WitchBindObjectName = "Witch Bind";
    private const string HealAnimObjectName = "Heal Anim";
    private const string HealParticleObjectName = "Pt Heal";

    /// <summary>
    /// Static instance for access by multiple animation clips in <see cref="AnimationManager"/>.
    /// </summary>
    public static readonly BindBurst Instance = new();

    /// <inheritdoc/>
    /// <remarks>
    /// The burst is a moment, but one that the player stands still for, and what it shows is on them: played a few
    /// frames late, it shows where they are. Its clip change lost on the way, the end of the bind never showed in the
    /// other game (USER 11-03, 刚刚我朋友敷丝我没有看到敷丝结束那一小段动画).
    /// </remarks>
    public override bool HoldsWhileClipPlays => true;

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        var flags = new Flags(effectInfo);
        if (!CreateObjects(playerObject, out var bindEffects)) {
            return;
        }

        // Play flag-specific effects
        if (flags.BaseMirror || flags.UpgradedMirror) PlayMirror(playerObject, flags.UpgradedMirror);
        if (flags.Maggoted) PlayMaggotCleanse(bindEffects, playerObject);

        // Stop regardless of if it's on or not
        StopBindBell(bindEffects);

        // Play crest-specific animations
        switch (crestType) {
            case CrestType.Beast:
                if (!flags.Maggoted) {
                    PlayBeastRage(bindEffects);
                }
                break;
            case CrestType.Witch:
                if (!flags.Maggoted) {
                    PlayWitchEnd(bindEffects, flags);
                } else {
                    PlayWitchMaggoted(bindEffects);
                }
                return;
            case CrestType.Shaman:
                PlayShamanEnd(bindEffects);
                break;
        }

        PlayNormalEnd(playerObject, bindEffects, flags);
    }

    /// <summary>
    /// Plays the maggot cleanse animation
    /// </summary>
    private void PlayMaggotCleanse(GameObject bindEffects, GameObject playerObject) {
        // Grab assets from Maggoted state
        var stateName = "Maggoted?";
        var maggotBurst = GetOrFindBindFsm().GetAction<SpawnObjectFromGlobalPool>(stateName, 2);
        var maggotFlash = GetOrFindBindFsm().GetAction<SpawnObjectFromGlobalPool>(stateName, 4);
        var maggotAudio = GetOrFindBindFsm().GetFirstAction<PlayAudioEvent>(stateName);

        // Spawn copies of those assets
        EffectUtils.SpawnGlobalPoolObject(maggotBurst, bindEffects.transform, 5f);
        EffectUtils.SpawnGlobalPoolObject(maggotFlash, bindEffects.transform, 5f);

        // Play audio
        AudioUtil.PlayAudio(maggotAudio, playerObject);

        var at = playerObject.transform.position;
        Logger.Info(
            $"[Maggots] The character at ({at.x:0.0}, {at.y:0.0}) shakes its maggots off with the end of its bind, as " +
            "its player did in their game"
        );
    }

    /// <summary>
    /// Creates the appropriate Claw Mirror object.
    /// The object will be destroyed after it finishes.
    /// </summary>
    private static GameObject? PrepareMirror(GameObject playerObject, SetGameObject mirrorSource) {
        // Spawn mirror
        var mirror = EffectUtils.SpawnGlobalPoolObject(mirrorSource.gameObject.Value, playerObject.transform, 3f);

        if (mirror == null) {
            return null;
        }

        // Remove camera and haze components
        mirror.DestroyComponentsInChildren<CameraControlAnimationEvents>();
        mirror.DestroyGameObjectInChildren("haze2");

        return mirror;
    }

    /// <summary>
    /// Plays the appropriate Claw Mirror animation.
    /// Adds a damage component if appropriate.
    /// </summary>
    private void PlayMirror(GameObject playerObject, bool upgraded) {
        // Get claw prefabs
        Logger.Debug("Playing Claw Mirror Animation");
        var regularClaw = GetOrFindBindFsm().GetAction<SetGameObject>("Dazzle?", 3)!;
        var upgradedClaw = GetOrFindBindFsm().GetAction<SetGameObject>("Dazzle?", 4)!;

        // Create the claw
        GameObject? claw;
        if (upgraded) {
            claw = PrepareMirror(playerObject, upgradedClaw);
        } else {
            claw = PrepareMirror(playerObject, regularClaw);
        }

        if (claw == null) {
            return;
        }

        claw.SetActive(true);
        
        // Add hitbox if appropriate
        if (ServerSettings.IsPvpEnabled && ShouldDoDamage) {
            var damagerParent = claw.FindGameObjectInChildren("Trobbio_dazzle_flash");
            var damager = damagerParent?.FindGameObjectInChildren("hero_dazzle_flash_damager");
            if (damager == null) {
                Logger.Warn("Couldn't find claw mirror damager");
                return;
            }

            damager.layer = AttackLayer;

            var damage = upgraded ? ServerSettings.ClawMirrorUpgradedDamage : ServerSettings.ClawMirrorDamage;
            var damageComponent = AddDamageHeroComponent(damager, damage);
            damageComponent.hazardType = GlobalEnums.HazardType.EXPLOSION;

            FixRemoteAttack(damager);
        }
    }

    /// <summary>
    /// Stops the bind bell animation
    /// </summary>
    public static void StopBindBell(GameObject bindEffects) {
        // Only bother turning it off if it's on
        var bindBell = bindEffects.FindGameObjectInChildren(BindBellObjectName);
        bindBell?.SetActive(false);
    }

    /// <summary>
    /// Plays the Beast Crest specific rage animation
    /// </summary>
    private void PlayBeastRage(GameObject bindEffects) {
        // Create and reactivate the rage effect
        var beastRage = CreateEffectIfNotExists(bindEffects, BeastCrestRageObjectName);

        if (beastRage != null) {
            beastRage.SetActive(false);
            beastRage.SetActive(true);
        }
    }

    /// <summary>
    /// Plays the witch maggot cleanse animation
    /// </summary>
    private void PlayWitchMaggoted(GameObject bindEffects) {
        // Spawn in the special tentacles
        var maggotCleanse = CreateEffectIfNotExists(bindEffects, WitchBindCleanseObjectName);

        if (maggotCleanse != null) {
            maggotCleanse.SetActive(false);
            maggotCleanse.SetActive(true);
        }
    }

    /// <summary>
    /// Plays the Witch Crest tentancles animation, in the version that the game's "Witch Tentancles!" picks by the
    /// multibinder and the injector band. A partner with either showed the plain one (USER 11-13 "这些动画同步了吗").
    /// Yes it's called Tentancles internally. Thanks TC.
    /// </summary>
    private void PlayWitchEnd(GameObject bindEffects, Flags flags) {
        // Get bind effect
        var effectWasCreated = CreateEffectIfNotExists(bindEffects, WitchBindObjectName, out var witchBind);
        if (witchBind == null) {
            return;
        }

        // Remove camera controls if object was created, and what the tendrils do to the hero (WitchBindEffects): on the
        // copy it healed this game's player for each creature it touched, flashed them, and stopped and shook this
        // game, while it is the binding player's, whose own game does it
        if (effectWasCreated) {
            witchBind.DestroyComponent<CameraControlAnimationEvents>();
            witchBind.DestroyComponent<WitchBindEffects>();
        }

        // Toggle damage depending on if PVP is on or not
        SetWitchDamagers(witchBind);

        // Play tentacles audio
        var audio = GetOrFindBindFsm().GetFirstAction<PlayAudioEvent>("Witch Tentancles!");
        AudioUtil.PlayAudio(audio, bindEffects.transform.parent.gameObject);

        witchBind.SetActive(false);
        witchBind.SetActive(true);
        witchBind.GetComponent<Animator>().Play(
            flags.MultiBind
                ? flags.QuickBind ? "Witch Bind Multi Quickened" : "Witch Bind Multi"
                : flags.QuickBind ? "Witch Bind Quick" : "Witch Bind",
            0,
            0f
        );
    }

    /// <summary>
    /// Adds or removes hero damage components from Witch Crest bind
    /// </summary>
    private void SetWitchDamagers(GameObject witchBind) {
        // Loop through all children, looking for all the ones called "Damager"
        for (var i = 0; i < witchBind.transform.childCount; i++) {
            var child = witchBind.transform.GetChild(i);
            if (!child.name.StartsWith("Damager")) {
                continue;
            }

            // Add or remove damage component from Damager object
            SetDamageHeroState(child.gameObject);
            FixRemoteAttack(child.gameObject);
        }
    }

    /// <summary>
    /// Stops the Shaman Crest specific silk animation
    /// </summary>
    private static void PlayShamanEnd(GameObject bindEffects) {
        var shamanAntic = bindEffects.FindGameObjectInChildren(ShamanFallAnticObjectName);
        if (shamanAntic == null) {
            return;
        }
        shamanAntic.SetActive(false);
    }

    /// <summary>
    /// Plays the end of a bind of any crest but the Witch crest's, as the game's "Heal" and "Bind Burst" play it on the
    /// hero: the player flashes white and lets out the sparks of a heal, and unless the bind shook their maggots off
    /// instead of healing them ("Skip Heal"), a white flash and the burst of the heal come out on them. The flashes
    /// never showed on the character (USER 11-13, with the heals of the Witch crest's tendrils, see
    /// <see cref="WitchHeal"/>).
    /// </summary>
    private void PlayNormalEnd(GameObject playerObject, GameObject bindEffects, Flags flags) {
        playerObject.GetComponent<SpriteFlash>().flashFocusHeal();

        // Play particle effect
        CreateEffectIfNotExists(bindEffects, HealParticleObjectName, out var healParticle);
        if (healParticle != null) {
            healParticle.GetComponent<ParticleSystem>().Play();
        }

        if (!flags.Maggoted) {
            SpawnHealFlash(
                GetOrFindBindFsm().GetFirstAction<SpawnObjectFromGlobalPool>("Heal").gameObject.Value,
                playerObject,
                playerObject.transform.position
            );

            // Play silk animation
            var healAnim = CreateEffectIfNotExists(bindEffects, HealAnimObjectName);
            if (healAnim != null) {
                healAnim.SetActive(true);
            }
        }

        // Disable mesh renderer because the game does that. Is this needed?
        var bindSilkObj = bindEffects.FindGameObjectInChildren(BindSilkObjectName);
        if (bindSilkObj != null) {
            var bindSilkMeshRenderer = bindSilkObj.GetComponent<MeshRenderer>();
            bindSilkMeshRenderer.enabled = false;
        }

        // Play audio
        var audio = GetOrFindBindFsm().GetFirstAction<AudioPlayerOneShotSingle>("Bind Burst");
        AudioUtil.PlayAudio(audio, bindEffects.transform.parent.gameObject);
    }
}
