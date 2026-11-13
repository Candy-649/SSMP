using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HutongGames.PlayMaker.Actions;
using SSMP.Animation.Effects.Tools;
using SSMP.Internals;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Animation.Effects;

/// <summary>
/// Class for the animation effect of bind (healing).
/// </summary>
internal class Bind : DamageAnimationEffect {
    // Effect object names
    protected const string BindBellObjectName = "bind_bell_appear_instance";
    protected const string BindSilkObjectName = "Bind Silk";
    protected const string ShamanFallAnticObjectName = "Shaman_Bind_antic_silk";
    private const string BeastCrestAnticObjectName = "Warrior_Bind_antic_silk";
    private const string CursedBindFailObjectName = "Cursed Bind Hornet";

    // The clips of the curse, played one after the other
    private const string CursedStartClip = "Bind Cursed Start";
    private const string CursedMidClip = "Bind Cursed Mid";
    private const string CursedEndClip = "Bind Cursed End";
    private const string WitchBindObjectName = "Whip_Bind_silk_antic";

    /// <summary>
    /// Keeps track of special bind scenarios, mostly to do with Shaman Crest.
    /// </summary>
    public State BindState = State.Normal;

    /// <inheritdoc/>
    /// <remarks>
    /// The silk of a bind stays on the player for as long as they bind, as a storm does (see ThreadStorm).
    /// </remarks>
    public override bool HoldsWhileClipPlays => true;

    /// <summary>
    /// Cached FSM for Hornet's bind ability.
    /// </summary>
    private static PlayMakerFSM? _bindFsm;

    /// <summary>
    /// Cached effects object for Hornet's bind ability.
    /// </summary>
    private static GameObject? _localBindEffects;

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        // Every bind in the air goes on to this clip after "BindCharge Ground" began it with its effect, and a bind
        // under the curse plays it alone: the Bind FSM goes from "Do Bind" straight to "Cursed Bind Start" (USER 11-12
        // "那个我朋友在畸芽状态下尝试敷丝的时候会有一个特效，那个特效也没同步", "只要敷丝的时候得到畸芽状态就可以了")
        if (BindState == State.Cursed) {
            if (crestType == CrestType.Cursed && CreateObjects(playerObject, out var cursedEffects)) {
                PlayCursedFail(playerObject, cursedEffects);
            }

            return;
        }

        var flags = new Flags(effectInfo);

        var randomClipAction = GetOrFindBindFsm().GetFirstAction<GetRandomAudioClipFromTable>("Bind Start");
        var playAudioAction = GetOrFindBindFsm().GetFirstAction<PlayAudioEvent>("Bind Start");

        if (BindState == State.Normal) {
            AudioUtil.PlayAudio(playAudioAction, randomClipAction, playerObject);

            var oneShotSingleAction = GetOrFindBindFsm().GetFirstAction<AudioPlayerOneShotSingle>("Check Grounded");
            AudioUtil.PlayAudio(oneShotSingleAction, playerObject);
        }


        if (!CreateObjects(playerObject, out var bindEffects)) {
            return;
        }

        switch (crestType) {
            case CrestType.Beast:
                PlayBeastBindStart(bindEffects);
                break;
            case CrestType.Witch:
                PlayWitchAnimationAntic(bindEffects, flags);
                break;
            case CrestType.Shaman:
                var shouldContinue = PickShamanAnimation(playerObject, bindEffects, flags);
                if (!shouldContinue) {
                    return;
                }
                break;
            default:
                PlayNormalStart(bindEffects, flags);
                break;
        }

        // If bind bell, do effects in state "Bind Bell?" and "Bind Bell Disappear?"
        if (flags.BindBell) {
            StartBindBell(bindEffects);
        }

        // TODO: If using reserve bind, use reserve bind animation?

        // TODO: Quick Craft animations
    }

    /// <summary>
    /// Creates the bind bell
    /// </summary>
    private void StartBindBell(GameObject bindEffects) {
        Logger.Debug("Starting warding bell");
        var bindBell = bindEffects.FindGameObjectInChildren(BindBellObjectName);
        
        if (bindBell == null) {
            var allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
            var localBell = allObjects.FirstOrDefault(o => o.name == "bind_bell_appear");
            
            if (localBell == null) {
                Logger.Warn("Couldn't find warding bell object");
                return;
            }

            bindBell = HeroCopies.Make(localBell, bindEffects.transform);
            bindBell.name = BindBellObjectName;
            
            var follower = bindBell.GetComponent<FollowTransform>();
            follower.target = bindEffects.transform;
            follower.useHero = false;

            var delay = bindBell.AddComponentIfNotPresent<DeactivateAfterDelay>();
            delay.time = 5f;
        }

        bindBell.SetActive(false);
        bindBell.SetActive(true);
    }

    /// <summary>
    /// Plays the normal silk animation
    /// </summary>
    private void PlayNormalStart(GameObject bindEffects, Flags flags) {
        Logger.Debug("Playing normal bind start animation");
        var bindSilkObj = CreateEffectIfNotExists(bindEffects, BindSilkObjectName);
        if (bindSilkObj == null) {
            return;
        }

        var bindSilkMeshRenderer = bindSilkObj.GetComponent<MeshRenderer>();
        bindSilkMeshRenderer.enabled = true;

        var bindSilkAnimator = bindSilkObj.GetComponent<tk2dSpriteAnimator>();

        if (flags.QuickBind) {
            bindSilkAnimator.Play(bindSilkAnimator.GetClipByName("Bind Silk Quick"));
        } else {
            bindSilkAnimator.Play(bindSilkAnimator.GetClipByName("Bind Silk"));
        }
        bindSilkObj.SetActive(false);
        bindSilkObj.SetActive(true);
    }

    /// <summary>
    /// Plays the Beast Crest specific silk animation
    /// </summary>
    private void PlayBeastBindStart(GameObject bindEffects) {
        Logger.Debug("Playing Beast Crest start antic");
        var beastAntic = CreateEffectIfNotExists(bindEffects, BeastCrestAnticObjectName);
        if (beastAntic == null) {
            return;
        }

        beastAntic.SetActive(false);
        beastAntic.SetActive(true);
    }

    /// <summary>
    /// Starts the Cursed Crest bind animation, which shows in place of the body of the character until it ends (see
    /// <see cref="ShowsCurse"/>).
    /// </summary>
    private static void PlayCursedFail(GameObject playerObject, GameObject bindEffects) {
        Logger.Info("Playing the curse of a bind of another player");
        var failAntic = bindEffects.FindGameObjectInChildren(CursedBindFailObjectName);

        tk2dSpriteAnimator animator;
        if (failAntic == null) {
            var effects = HeroController.instance.gameObject.FindGameObjectInChildren("Effects");
            if (effects == null) {
                Logger.Warn("Unable to find local effects object");
                return;
            }

            var localFailAntic = effects.FindGameObjectInChildren(CursedBindFailObjectName);
            if (localFailAntic == null) {
                Logger.Warn("Unable to find local cursed bind object");
                return;
            }

            failAntic = HeroCopies.Make(localFailAntic, bindEffects.transform);
            failAntic.name = CursedBindFailObjectName;
            failAntic.transform.SetLocalPositionZ(failAntic.transform.localPosition.z - 0.25f);

            animator = failAntic.GetComponent<tk2dSpriteAnimator>();
            animator.AnimationCompletedEvent += (completed, clip) => PlayNextCursedPart(playerObject, completed, clip);
        } else {
            animator = failAntic.GetComponent<tk2dSpriteAnimator>();
        }

        failAntic.SetActive(false);
        failAntic.SetActive(true);

        animator.Play(CursedStartClip);
        HeroChildEffects.ShowBodyUnlessHidden(playerObject);
    }

    /// <summary>
    /// Starts the next part of the Cursed Crest animation. Its end shows the body of the character again, as the game
    /// shows the hero's own sprite again as it plays the end ("Cursed Bind End pt2"); the end switches the copy off.
    /// </summary>
    private static void PlayNextCursedPart(
        GameObject playerObject,
        tk2dSpriteAnimator animator,
        tk2dSpriteAnimationClip clip
    ) {
        if (clip.name == CursedStartClip) {
            animator.Play(CursedMidClip);
        } else if (clip.name == CursedMidClip) {
            animator.Play(CursedEndClip);
            HeroChildEffects.ShowBodyUnlessHidden(playerObject);
        }
    }

    /// <summary>
    /// Whether the curse of a bind shows on the character of another player in place of its body. The game hides the
    /// hero's own sprite while the curse, a figure of the hero itself, plays its start and middle (Bind FSM "Cursed
    /// Bind Start pt2" to "Cursed Bind End pt2").
    /// </summary>
    internal static bool ShowsCurse(GameObject playerObject) {
        var bindEffects = playerObject.FindGameObjectInChildren("Bind Effects");
        var curse = bindEffects == null ? null : bindEffects.FindGameObjectInChildren(CursedBindFailObjectName);
        return curse != null && curse.activeInHierarchy &&
               curse.TryGetComponent<tk2dSpriteAnimator>(out var animator) &&
               (animator.IsPlaying(CursedStartClip) || animator.IsPlaying(CursedMidClip));
    }

    /// <summary>
    /// Plays the Witch Crest silk bind animation. The game's "Witch" state plays the long silk for the multibinder
    /// alone and the quick silk for the injector band alone ("Witch Multibind", "Witch Quickbind"); with neither or
    /// both, the silk plays as it starts. A partner with either showed the plain silk (USER 11-13 "这些动画同步了吗").
    /// </summary>
    private void PlayWitchAnimationAntic(GameObject bindEffects, Flags flags) {
        Logger.Debug("Playing Witch Crest bind antic");
        var silkAntic = CreateEffectIfNotExists(bindEffects, WitchBindObjectName);
        if (silkAntic == null) {
            return;
        }

        silkAntic.SetActive(false);
        silkAntic.SetActive(true);
        if (flags.MultiBind != flags.QuickBind) {
            silkAntic.GetComponent<Animator>().Play(
                flags.MultiBind ? "Witch Bind Antic Silk Long" : "Witch Bind Antic Silk Quick"
            );
        }
    }

    /// <summary>
    /// Picks and plays a Shaman bind animation
    /// </summary>
    private bool PickShamanAnimation(GameObject playerObject, GameObject bindEffects, Flags flags) {
        if (BindState == State.Normal) {
            PlayShamanFall(bindEffects);
            return true;
        } else if (BindState == State.ShamanCancel) {
            PlayShamanCancel(playerObject, bindEffects);
            return false;
        }

        PlayShamanFallEnd(bindEffects);
        PlayNormalStart(bindEffects, flags);
        return false;
    }
    
    /// <summary>
    /// Plays the Shaman Crest falling silk animation
    /// </summary>
    private void PlayShamanFall(GameObject bindEffects) {
        Logger.Debug("Playing Shaman Crest bind fall animation");
        var shamanAntic = CreateEffectIfNotExists(bindEffects, ShamanFallAnticObjectName);
        if (shamanAntic == null) {
            return;
        }

        var delay = shamanAntic.AddComponentIfNotPresent<DeactivateAfterDelay>();
        delay.time = 5;

        shamanAntic.SetActive(false);
        shamanAntic.SetActive(true);
    }

    /// <summary>
    /// Plays the Shaman Crest 'failed to bind' animation
    /// </summary>
    private void PlayShamanCancel(GameObject playerObject, GameObject bindEffects) {
        Logger.Debug("Playing Shaman Crest bind cancel/fail animation");
        var shamanAntic = bindEffects.FindGameObjectInChildren(ShamanFallAnticObjectName);
        if (shamanAntic != null) {
            shamanAntic.SetActive(false);
        }

        var cancelStateName = "Shaman Air Cancel";
        var silkPuffSpawner = GetOrFindBindFsm().GetFirstAction<SpawnObjectFromGlobalPool>(cancelStateName);

        var audio = GetOrFindBindFsm().GetFirstAction<PlayAudioEvent>(cancelStateName);
        AudioUtil.PlayAudio(audio, playerObject);
        
        EffectUtils.SpawnGlobalPoolObject(silkPuffSpawner, playerObject.transform, 5f);

        BindBurst.StopBindBell(bindEffects);
    }

    /// <summary>
    /// Transitions from the falling silk animation to the normal silk animation
    /// </summary>
    private void PlayShamanFallEnd(GameObject bindEffects) {
        Logger.Debug("Playing Shaman Crest bind fall finished transition animation");
        var shamanAntic = bindEffects.FindGameObjectInChildren(ShamanFallAnticObjectName);
        if (shamanAntic == null) {
            return;
        }
        var animator = shamanAntic.GetComponent<Animator>();
        animator.Play("End");
    }



    /// <inheritdoc/>
    public override byte[] GetEffectInfo() {
        byte[] effectInfo = [
            (byte) ((ToolItemManager.IsToolEquipped("Bell Bind") && !ToolItemManager.GetToolByName("Bell Bind").IsEmpty) ? 1 : 0),
            (byte) (ToolItemManager.IsToolEquipped("Dazzle Bind") ? 1 : 0),
            (byte) (ToolItemManager.IsToolEquipped("Dazzle Bind Upgraded") ? 1 : 0),
            (byte) (ToolItemManager.IsToolEquipped("Quickbind") ? 1 : 0),
            (byte) ((ToolItemManager.IsToolEquipped("Reserve Bind") && !ToolItemManager.GetToolByName("Reserve Bind").IsEmpty) ? 1 : 0),
            (byte) (ShookOffMaggots() ? 1 : 0),
            (byte) (ToolItemManager.IsToolEquipped("Multibind") ? 1 : 0)
        ];
        return effectInfo;
    }

    /// <summary>
    /// Whether the local player's bind shook the maggots off them instead of healing them, which only its end can say
    /// (see <see cref="BindBurst"/>): the game's bind reads whether the player is maggoted just before it would heal,
    /// keeps it as its "Skip Heal" (states "Maggoted?" and "Witch Tentancles!") and takes the maggots off then, and
    /// sets it back to false as the next bind begins. Read off the player as the bind began, as it was, it reached the
    /// other game only with a bind on the ground, whose start alone told the end, so the maggots of a player who bound
    /// in the air or with the beast crest's rage went without a sign (USER 11-07 "我好像看不到我朋友把蛆甩掉的特效").
    /// </summary>
    private bool ShookOffMaggots() {
        return GetOrFindBindFsm().FsmVariables.FindFsmBool("Skip Heal") is { Value: true };
    }

    /// <summary>
    /// Get or find the Bind FSM on the hero object. Will be cached to <see cref="_bindFsm"/>.
    /// </summary>
    /// <returns>The FSM for Bind.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the FSM cannot be found, which shouldn't happen.
    /// </exception>
    protected PlayMakerFSM GetOrFindBindFsm() {
        if (_bindFsm != null) {
            return _bindFsm;
        }

        var heroFsms = HeroController.instance.GetComponents<PlayMakerFSM>();
        foreach (var heroFsm in heroFsms) {
            if (heroFsm.FsmName == "Bind") {
                _bindFsm = heroFsm;
                return _bindFsm;
            }
        }

        throw new InvalidOperationException("Could not find Bind FSM on hero");
    }

    /// <summary>
    /// Attempts to locate and bind the 'Bind Effects' GameObject to the specified player object.
    /// </summary>
    /// <param name="playerObject">The player's object.</param>
    /// <param name="bindEffects">The player's 'Bind Effects' object, or null if not found.</param>
    /// <returns>true if the 'Bind Effects' GameObject is successfully found and bound; otherwise, false.</returns>
    protected bool CreateObjects(GameObject playerObject, [MaybeNullWhen(false)] out GameObject bindEffects) {
        // The hero of an earlier load is destroyed, which ??= takes for one that is still there, as it only checks
        // for no object at all
        if (_localBindEffects == null && HeroController.UnsafeInstance is var hero && hero != null) {
            _localBindEffects = hero.gameObject.FindGameObjectInChildren("Bind Effects");
        }

        if (_localBindEffects == null) {
            Logger.Warn("Could not find local Bind Effects object in hero object");
            bindEffects = null;
            return false;
        }

        bindEffects = playerObject.FindGameObjectInChildren("Bind Effects");
        if (bindEffects == null) {
            Logger.Warn("Player object does not have Bind Effects child, cannot play bind");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Finds and returns a bind effect with the specified name, creating it if it doesn't already exist.
    /// </summary>
    /// <param name="bindEffects">The player's Bind Effects object.</param>
    /// <param name="objectName">The name of the effect to find or create.</param>
    /// <param name="effect">The effect that was found or created</param>
    /// <returns>Returns true if the effect was created, false if it was found.</returns>
    protected bool CreateEffectIfNotExists(GameObject bindEffects, string objectName, out GameObject? effect) {
        effect = bindEffects.FindGameObjectInChildren(objectName);
        if (effect != null) {
            return false;
        }

        var localObj = _localBindEffects!.FindGameObjectInChildren(objectName);
        if (localObj == null) {
            Logger.Warn($"Could not find local {objectName} object, cannot play bind effect");
            return false;
        }

        effect = HeroCopies.Make(localObj, bindEffects.transform);
        effect.name = objectName;
        return true;
    }

    /// <summary>
    /// Finds and returns a bind effect with the specified name, creating it if it doesn't already exist.
    /// </summary>
    /// <param name="bindEffects">The player's Bind Effects object.</param>
    /// <param name="objectName">The name of the effect to find or create.</param>
    /// <returns>The effect that was found or created</returns>
    protected GameObject? CreateEffectIfNotExists(GameObject bindEffects, string objectName) {
        CreateEffectIfNotExists(bindEffects, objectName, out var effect);
        return effect;
    }

    /// <summary>
    /// Lets out the white flash of a heal ("White Flash R") at a place on the character of another player, as the game
    /// lets it out on the hero: a copy that plays none of the shaking of the controller that the game's own plays as it
    /// comes out, which is for the player who heals. It fades and goes by itself, as the game's own does.
    /// </summary>
    /// <param name="prefab">The flash, as the game lets it out.</param>
    /// <param name="playerObject">The character.</param>
    /// <param name="position">Where it comes out.</param>
    protected static void SpawnHealFlash(GameObject prefab, GameObject playerObject, Vector3 position) {
        HeroCopies.Make(ToolCopies.GetCopyPrefab(prefab), playerObject, position, Quaternion.identity).SetActive(true);
    }

    /// <summary>
    /// Turns off all bind effects
    /// </summary>
    public static void ForceStopAllEffects(GameObject bindEffects) {
        BindBurst.StopBindBell(bindEffects);

        var shamanAntic = bindEffects.FindGameObjectInChildren(ShamanFallAnticObjectName);
        if (shamanAntic != null) {
            shamanAntic.SetActive(false);
        }

        var silkAntic = bindEffects.FindGameObjectInChildren(WitchBindObjectName);
        if (silkAntic != null) {
            silkAntic.SetActive(false);
        }

        var cursedFailAntic = bindEffects.FindGameObjectInChildren(CursedBindFailObjectName);
        if (cursedFailAntic != null) {
            cursedFailAntic.SetActive(false);
        }

        var beastAntic = bindEffects.FindGameObjectInChildren(BeastCrestAnticObjectName);
        if (beastAntic != null) {
            beastAntic.SetActive(false);
        }

        var bindSilkObj = bindEffects.FindGameObjectInChildren(BindSilkObjectName);
        if (bindSilkObj != null) {
            bindSilkObj.SetActive(false);
        }
    }

    /// <summary>
    /// Shaman Crest has special animations depending on which animation is
    /// sent, but they're all related to the bind starting
    /// </summary>
    public enum State {
        Normal,
        ShamanCancel,
        ShamanDoneFalling,

        /// <summary>
        /// "BindCharge Air", which is the curse when the player binds under it (see <see cref="Play"/>).
        /// </summary>
        Cursed
    }

    /// <summary>
    /// Effect flags sent by the other player. Mostly items they have equipped.
    /// </summary>
    protected class Flags {
        public readonly bool BindBell;
        public readonly bool BaseMirror;
        public readonly bool UpgradedMirror;
        public readonly bool QuickBind;
        public readonly bool ReserveBind;

        /// <summary>
        /// Whether the bind shook the maggots off the player, which only the end of a bind says (see
        /// <see cref="ShookOffMaggots"/>).
        /// </summary>
        public readonly bool Maggoted;

        /// <summary>
        /// Whether the multibinder is on, which the silk and the tendrils of the Witch crest's bind play by.
        /// </summary>
        public readonly bool MultiBind;

        public Flags(byte[]? info) {
            if (info == null) return;
            BindBell = info[0] == 1;
            BaseMirror = info[1] == 1;
            UpgradedMirror = info[2] == 1;
            QuickBind = info[3] == 1;
            ReserveBind = info[4] == 1;
            Maggoted = info[5] == 1;
            MultiBind = info[6] == 1;
        }
    }
}
