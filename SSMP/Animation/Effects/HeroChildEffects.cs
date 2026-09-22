using System;
using SSMP.Internals;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Animation.Effects;

/// <summary>
/// The parts of the hero that only show while it does something - the thread of a taunt, the burst of a dash, the puff
/// of a wall jump, the glow of a charge - which the game switches on and off by themselves, beside the hero's own
/// animation. Only that animation reached the other players, so they saw the hero taunt without its thread, dash
/// without its burst and so on. Each of these parts is watched on the local hero, and a copy of it on the character of
/// the player it belongs to is switched on and off with it.
/// </summary>
internal class HeroChildEffects : AnimationEffect {
    /// <summary>
    /// The single instance, which <see cref="AnimationManager"/> plays for every player.
    /// </summary>
    public static readonly HeroChildEffects Instance = new();

    /// <summary>
    /// The watched parts, by their path under the hero. Their place in this list is what they are known by over the
    /// network, so new ones go at the end. Left out are the parts that another effect already plays, the parts that
    /// could hurt, touch or be noticed by anything in the room (damagers, colliders, state machines, noise makers),
    /// the parts that show or hide by what the local player has equipped, and the parts of the local player's own view
    /// (the light around the hero and the dark of a room).
    /// </summary>
    private static readonly string[] Paths = [
        "Effects/Taunt Thread",
        "Effects/Taunt Rings Flash",
        "Effects/Dash Burst",
        "Effects/Water Dash Burst",
        "Effects/air_sprint_effect",
        "Effects/Walldash Kickoff",
        "Effects/Wall Puff",
        "Effects/Backflip Puff",
        "Effects/Downspike Burst",
        "Effects/Can Bind Effect",
        "Effects/NA Charge",
        "Effects/NA Charged",
        "Effects/Super Jump Antic Effect L",
        "Effects/Super Jump Antic Effect R",
        "Effects/Super Jump Thread Loop",
        "Effects/Super Jump Thread",
        "Effects/Super Jump Charged",
        "Effects/Super Jump Extra Throw Effect",
        "Effects/Super Jump Extra Ground Effect",
        "Effects/Super Jump Catch Effect",
        "Effects/Hornet_harpoon_grab_effect",
        "Effects/Hornet_harpoon_throw_effect",
        "Effects/Hornet_harpoon_dash",
        "Effects/Harpoon Thread Ring/Thread",
        "Effects/Hunter SprintAttackBurst",
        "Effects/Wanderer DashCombo Burst",
        "Effects/wall_launch_puff",
        "Effects/clamber_impact_effects",
        "Effects/Hornet_Wall_Scramble_Burst_Effect",
        "Effects/umbrella_updraftJump_fx",
        "Effects/Tool_wall_cling_effect",
        "Effects/Needolin Snap Effect",
        "Effects/Needolin Thread",
        "Effects/Swimp Bonk Effect",
        "Effects/Wall Bump/NeedleThrow Effect",
        "Effects/Wall Bump/Particle System",
        "Effects/Damage Effect/low health hit effect",
        "Effects/Damage Effect/Frost Dmg Pt",
        "Special Attacks/Sphere Flash",
        "Special Attacks/Silk Charge WallBonk/Slam Effect",
        "Special Attacks/Super Jump Needle Stick",
        "Special Attacks/Parry DashBurst",
        "Attacks/Shaman/Shaman_blade_cast_effect DashSlash",
        "Bind Effects/shaman_focus_halo",
        "Bind Effects/hornet_quick_bind_effect",
        "Tool Effects/Screw Activation Flash",
        "Tool Effects/Syringe Flash poison",
        "Tool Effects/Syringe Flash",
        "Tool Effects/Craft Flash",
        "Tool Effects/QuickCraft Silk",
        "Tool Effects/Pt QuickCraft",
        "Tool Effects/Screw Attack Burst",
        "Tool Effects/Screw Burst",
        "Tool Effects/Extractor ChargedEffect",
        "Tool Effects/Scuttle Burst",
        "Tool Effects/Syringe Glow",
        "Tool Effects/Syringe Glow poison",
        "Tool Effects/Screw Attack Impact",
        "Tool Effects/Pt ScrewAttack Poison Trail",
        "Tool Effects/Pt ScrewAttack Poison Impact",
        "Tool Effects/Amplify_Effect",
        "Tool Effects/Rosary Cannon Point/Shoot Effect"
    ];

    /// <summary>
    /// The number of bytes that one bit for each watched part takes.
    /// </summary>
    private static readonly int MaskLength = (Paths.Length + 7) / 8;

    /// <summary>
    /// Which watched parts of the local hero are switched on. It goes along in full with every change, so that a
    /// change that got lost on the way is put right by the next one.
    /// </summary>
    private static readonly byte[] LocalMask = new byte[MaskLength];

    /// <summary>
    /// Starts watching the parts of the local hero, which may be a new hero or the same one again.
    /// </summary>
    /// <param name="hero">The local hero.</param>
    /// <param name="send">Sends the effect info of a change to the other players.</param>
    public static void Watch(HeroController hero, Action<byte[]> send) {
        Array.Clear(LocalMask, 0, MaskLength);

        for (var i = 0; i < Paths.Length; i++) {
            var part = hero.transform.Find(Paths[i]);
            if (part == null) {
                Logger.Warn($"The hero has no part '{Paths[i]}' to show to other players");
                continue;
            }

            if (!part.TryGetComponent<HeroChildEffectWatcher>(out var watcher)) {
                watcher = part.gameObject.AddComponent<HeroChildEffectWatcher>();
            }

            watcher.Index = i;
            watcher.Toggled = (index, on) => OnToggled(index, on, send);
            SetBit(LocalMask, i, part.gameObject.activeInHierarchy);
        }
    }

    /// <summary>
    /// Called when a watched part of the local hero is switched on or off.
    /// </summary>
    private static void OnToggled(int index, bool on, Action<byte[]> send) {
        SetBit(LocalMask, index, on);

        var effectInfo = new byte[2 + MaskLength];
        effectInfo[0] = (byte) index;
        effectInfo[1] = (byte) (on ? 1 : 0);
        Array.Copy(LocalMask, 0, effectInfo, 2, MaskLength);
        send(effectInfo);
    }

    /// <inheritdoc/>
    public override byte[]? GetEffectInfo() {
        return null;
    }

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        if (effectInfo == null || effectInfo.Length != 2 + MaskLength || effectInfo[0] >= Paths.Length) {
            return;
        }

        var changed = effectInfo[0];
        var on = effectInfo[1] == 1;
        var part = GetCopy(playerObject, changed, on);
        if (part != null) {
            if (on) {
                Restart(part);
            } else {
                part.SetActive(false);
            }
        }

        // What the changes that got lost on the way would have done. A part that is off there is off here. A part
        // that stays on until it is switched off is on here too, but a part that ends by itself is only ever started by
        // its own change: it may already have ended here while it is still showing there.
        for (var i = 0; i < Paths.Length; i++) {
            if (i == changed) {
                continue;
            }

            if ((effectInfo[2 + i / 8] & (1 << (i % 8))) == 0) {
                var copy = GetCopy(playerObject, i, false);
                if (copy != null && copy.activeSelf) {
                    copy.SetActive(false);
                }

                continue;
            }

            var local = HeroController.instance.transform.Find(Paths[i]);
            if (local == null || EndsByItself(local.gameObject)) {
                continue;
            }

            var other = GetCopy(playerObject, i, true);
            if (other != null && !other.activeSelf) {
                Restart(other);
            }
        }
    }

    /// <summary>
    /// Switches a copied part on from its start, the way the game switches its own part on.
    /// </summary>
    private static void Restart(GameObject part) {
        part.SetActive(false);
        part.SetActive(true);

        // The game plays the animation of a part that does not end by itself right after switching it on; a part that
        // does end by itself plays it from the start as it is switched on, which this repeats harmlessly
        if (part.TryGetComponent<tk2dSpriteAnimator>(out var animator)) {
            animator.PlayFromFrame(0);
        }
    }

    /// <summary>
    /// Whether a part switches itself off when it is done, rather than being switched off by the game.
    /// </summary>
    private static bool EndsByItself(GameObject part) {
        return part.GetComponent<DeactivateAfter2dtkAnimation>() ||
               part.GetComponent<DeactivateAfterDelay>() ||
               part.GetComponent<DisableAfterTime>() ||
               part.GetComponent<ParticleSystemAutoDisable>() ||
               part.GetComponent<ParticleSystemAutoDeactivate>();
    }

    /// <summary>
    /// The copy of a watched part on the character of another player, at the same place under it as the part is under
    /// the local hero, made from the local hero's own part when it is not there yet.
    /// </summary>
    /// <param name="playerObject">The character of the other player.</param>
    /// <param name="index">The index of the part.</param>
    /// <param name="create">Whether to make the copy when there is none yet.</param>
    /// <returns>The copy, or null when there is none and none could or should be made.</returns>
    private static GameObject? GetCopy(GameObject playerObject, int index, bool create) {
        var names = Paths[index].Split('/');
        var hero = HeroController.instance;
        var original = hero != null ? hero.transform : null;
        var current = playerObject.transform;

        for (var i = 0; i < names.Length; i++) {
            original = original != null ? original.Find(names[i]) : null;

            var next = current.Find(names[i]);
            if (next == null) {
                if (!create || original == null) {
                    return null;
                }

                next = i < names.Length - 1
                    ? CreateStep(current, original)
                    : CreateCopy(current, original, playerObject.transform);
            }

            current = next;
        }

        return current.gameObject;
    }

    /// <summary>
    /// Makes an empty object on the way to a copied part, placed like the object on the way to the local part, so that
    /// only the part itself is copied and never the objects around it.
    /// </summary>
    private static Transform CreateStep(Transform parent, Transform original) {
        var step = new GameObject(original.name).transform;
        step.SetParent(parent, false);
        step.localPosition = original.localPosition;
        step.localRotation = original.localRotation;
        step.localScale = original.localScale;
        return step;
    }

    /// <summary>
    /// Copies a part of the local hero, switched off, without whatever on it would shake the camera or the controller
    /// of the local player, and turned towards the character it is on rather than towards the local hero.
    /// </summary>
    /// <param name="parent">Where the copy goes.</param>
    /// <param name="original">The part of the local hero.</param>
    /// <param name="character">The character of the other player, which the copy is on.</param>
    private static Transform CreateCopy(Transform parent, Transform original, Transform character) {
        // Made inside a holder that is switched off, so that nothing on the copy starts before it is switched on the
        // way the other player's part was, even when the local part is showing right now
        var holder = new GameObject("Hero Child Effect Holder");
        holder.SetActive(false);

        var copy = Object.Instantiate(original.gameObject, holder.transform, false);
        copy.name = original.name;
        copy.SetActive(false);

        // Switched off rather than removed: switched off they do nothing, and the animation events that call some of
        // them still find them
        foreach (var shaker in copy.GetComponentsInChildren<CameraControlAnimationEvents>(true)) {
            shaker.enabled = false;
        }

        foreach (var vibration in copy.GetComponentsInChildren<VibrationPlayer>(true)) {
            vibration.enabled = false;
        }

        foreach (var shaker in copy.GetComponentsInChildren<CameraShakeOnEnable>(true)) {
            shaker.enabled = false;
        }

        // Some parts go by the way the hero faces, and name the local hero for it, which the copy would go by too
        foreach (var flip in copy.GetComponentsInChildren<DeactivateOnParentScaleFlip>(true)) {
            if (flip.parent != null && !flip.parent.IsChildOf(copy.transform)) {
                flip.parent = character;
            }
        }

        foreach (var match in copy.GetComponentsInChildren<MatchXScaleSignOnEnable>(true)) {
            if (match.matchObject != null && !match.matchObject.IsChildOf(copy.transform)) {
                match.matchObject = character;
            }
        }

        copy.transform.SetParent(parent, false);
        Object.Destroy(holder);
        return copy.transform;
    }

    /// <summary>
    /// Sets whether the part with an index is on in a mask.
    /// </summary>
    private static void SetBit(byte[] mask, int index, bool value) {
        if (value) {
            mask[index / 8] |= (byte) (1 << (index % 8));
        } else {
            mask[index / 8] &= (byte) ~(1 << (index % 8));
        }
    }
}

/// <summary>
/// Watches one part of the local hero for being switched on and off, for <see cref="HeroChildEffects"/>.
/// </summary>
internal class HeroChildEffectWatcher : MonoBehaviour {
    /// <summary>
    /// The index of the part in the watched parts.
    /// </summary>
    [NonSerialized]
    public int Index;

    /// <summary>
    /// Called with the index of the part and whether it is on. Not serialized, so a copy of the part made for another
    /// player's character, which has this component too, watches nothing.
    /// </summary>
    [NonSerialized]
    public Action<int, bool>? Toggled;

    private void OnEnable() {
        Toggled?.Invoke(Index, true);
    }

    private void OnDisable() {
        Toggled?.Invoke(Index, false);
    }
}
