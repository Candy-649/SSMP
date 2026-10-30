using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// The look of a needle that has taken on an element from a tool: for as long as it lasts the hero flashes in the
/// colour of the element with embers or bubbles around them, and each slash takes the tint of the element with a burst
/// of it on the slash. The thrower's game sends when the element comes and goes; the slashes carry it themselves. Only
/// the look goes: the loop of sound around the hero is left out, and the partner's slashes do no more to the enemies
/// here than they did before. A charged slash goes by the element of the needle of the player whose it is, as it shows
/// here (<see cref="OnEffectEnable"/>, <see cref="OnExtraSlashEnable"/>).
/// </summary>
internal static class ImbuedNail {
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// The looks of the elements, on the hero's own handling of them, by the element.
    /// </summary>
    private static readonly FieldInfo? ConfigsField = typeof(HeroNailImbuement).GetField("nailConfigs", Flags);

    /// <summary>
    /// The element that a part of a charged slash is for, what it switches on for it, and whether it has started,
    /// before which it switches nothing (HeroNailImbuementEffect).
    /// </summary>
    private static readonly FieldInfo? EffectElementField =
        typeof(HeroNailImbuementEffect).GetField("nailElement", Flags);

    /// <inheritdoc cref="EffectElementField" />
    private static readonly FieldInfo? EffectObjectsField =
        typeof(HeroNailImbuementEffect).GetField("activateObjects", Flags);

    /// <inheritdoc cref="EffectElementField" />
    private static readonly FieldInfo? EffectStartedField =
        typeof(HeroNailImbuementEffect).GetField("hasStarted", Flags);

    /// <summary>
    /// The sprites of a charged slash that take the tint of the element of the needle, and its damagers, which take the
    /// element itself (HeroExtraNailSlash).
    /// </summary>
    private static readonly FieldInfo? TintSpritesField = typeof(HeroExtraNailSlash).GetField("tintSprites", Flags);

    /// <inheritdoc cref="TintSpritesField" />
    private static readonly FieldInfo? TintTk2dSpritesField =
        typeof(HeroExtraNailSlash).GetField("tintTk2dSprites", Flags);

    /// <inheritdoc cref="TintSpritesField" />
    private static readonly FieldInfo? DamagersField = typeof(HeroExtraNailSlash).GetField("damagers", Flags);

    /// <summary>
    /// Whether the embers or bubbles around a hero follow the local hero rather than their target.
    /// </summary>
    private static readonly FieldInfo? UseHeroField = typeof(FollowTransform).GetField("useHero", Flags);

    /// <summary>
    /// The element, the flash and the embers or bubbles of the players whose needle has an element, by their character.
    /// </summary>
    private static readonly
        Dictionary<int, (NailElements Element, SpriteFlash.FlashHandle Flash, GameObject? Particles)> Imbued = new();

    /// <summary>
    /// Writes the message that the local hero's needle took on an element, or lost it.
    /// </summary>
    public static byte[] Write(NailElements element) {
        return [(byte) ToolMessageKind.Imbue, 0, (byte) element];
    }

    /// <summary>
    /// Shows on the character of another player that their needle took on an element, or lost it.
    /// </summary>
    /// <param name="playerObject">The character of the player.</param>
    /// <param name="reader">The message, from after its kind and number.</param>
    public static void Play(GameObject playerObject, BinaryReader reader) {
        Show(playerObject, (NailElements) reader.ReadByte());
    }

    /// <summary>
    /// Shows the element that the needle of another player has, if it is not what shows already: for a message about
    /// it that got lost, or a character that came into the room later.
    /// </summary>
    /// <param name="playerObject">The character of the player.</param>
    /// <param name="element">The element of the needle, or none.</param>
    public static void Keep(GameObject playerObject, NailElements element) {
        var shown = Imbued.TryGetValue(playerObject.GetInstanceID(), out var imbued)
            ? imbued.Element
            : NailElements.None;
        if (shown != element) {
            Show(playerObject, element);
        }
    }

    /// <summary>
    /// Shows on the character of another player the element that their needle has, from its start, or none.
    /// </summary>
    private static void Show(GameObject playerObject, NailElements element) {
        Stop(playerObject);

        var config = GetConfig(element);
        if (config == null) {
            return;
        }

        // The flash that the element starts with, and then again and again while it lasts
        var handle = default(SpriteFlash.FlashHandle);
        if (playerObject.TryGetComponent<SpriteFlash>(out var flash)) {
            flash.flashFocusHeal();
            var look = config.HeroFlashing;
            handle = flash.Flash(
                look.Colour,
                look.Amount,
                look.TimeUp,
                look.StayTime,
                look.TimeDown,
                0f,
                repeating: true,
                0,
                1,
                requireExplicitCancel: false
            );
        }

        GameObject? particles = null;
        if (config.HeroParticles != null) {
            particles = HeroCopies.Make(
                config.HeroParticles.gameObject,
                playerObject,
                playerObject.transform.position,
                Quaternion.identity
            );

            // With the character of the player rather than the local hero, and without the loop of sound
            if (particles.TryGetComponent<FollowTransform>(out var follow)) {
                UseHeroField?.SetValue(follow, false);
                follow.Target = playerObject.transform;
            }

            foreach (var audio in particles.GetComponentsInChildren<AudioSource>(true)) {
                audio.enabled = false;
            }

            if (particles.TryGetComponent<PlayParticleEffects>(out var effects)) {
                effects.PlayParticleSystems();
            }
        }

        Imbued[playerObject.GetInstanceID()] = (element, handle, particles);
    }

    /// <summary>
    /// Stops the look of an element on the character of another player: for a needle that lost it, or a character
    /// that leaves the room.
    /// </summary>
    /// <param name="playerObject">The character of the player.</param>
    public static void Stop(GameObject playerObject) {
        if (!Imbued.Remove(playerObject.GetInstanceID(), out var imbued)) {
            return;
        }

        if (playerObject.TryGetComponent<SpriteFlash>(out var flash)) {
            flash.CancelRepeatingFlash(imbued.Flash);
        }

        // They die down and go by themselves, as the hero's own do
        if (imbued.Particles != null && imbued.Particles.TryGetComponent<PlayParticleEffects>(out var effects)) {
            effects.StopParticleSystems();
        }
    }

    /// <summary>
    /// Gives a slash of another player the look of their needle's element, as the game gives it to the hero's own:
    /// the tint of the element, and a burst of it on the slash, turned for a slash up or down.
    /// </summary>
    /// <param name="slash">The slash.</param>
    /// <param name="element">The element of the needle, or none.</param>
    /// <param name="direction">The direction of the slash, in degrees.</param>
    public static void PlaySlash(GameObject slash, NailElements element, float direction) {
        var config = GetConfig(element);
        if (slash.TryGetComponent<tk2dSprite>(out var sprite)) {
            sprite.color = config != null ? config.NailTintColor : Color.white;
        }

        if (config == null) {
            return;
        }

        if (config.SlashEffect != null) {
            // Its own copy, which goes with the slash
            var burst = HeroCopies.Make(config.SlashEffect, slash.transform).transform;
            burst.localPosition = Vector3.zero;
            burst.localRotation = Quaternion.identity;
            burst.localScale = Vector3.one;
            switch (DirectionUtils.GetCardinalDirection(direction)) {
                case DirectionUtils.Up:
                    burst.localEulerAngles = new Vector3(0f, 0f, -90f);
                    break;
                case DirectionUtils.Down:
                    burst.localEulerAngles = new Vector3(0f, 0f, 45f);
                    break;
            }
        }

        config.ExtraSlashAudio.SpawnAndPlayOneShot(slash.transform.position);
    }

    /// <summary>
    /// The element of the needle of the player whose character this is, as it shows here, or none.
    /// </summary>
    /// <param name="playerObject">The character of the player.</param>
    public static NailElements ElementOf(GameObject playerObject) {
        return Imbued.TryGetValue(playerObject.GetInstanceID(), out var imbued) ? imbued.Element : NailElements.None;
    }

    /// <summary>
    /// A part of a charged slash that switches on what goes with one element of the needle as the slash starts
    /// (HeroNailImbuementEffect): the embers or the poison on it, and the sound of them. It goes by the needle of the
    /// local hero, so on the copy of the local hero's charged slash that shows another player's, it showed and sounded
    /// the element of the local player's needle and never that of the player whose slash it was (USER 10-30: "我擦火刀
    /// 好像听到我朋友挥刀也有火刀的声音了"). On such a copy it goes by the element of that player's needle instead.
    /// </summary>
    public static void OnEffectEnable(Action<HeroNailImbuementEffect> orig, HeroNailImbuementEffect self) {
        var character = HeroCopies.CharacterOf?.Invoke(self.transform);
        if (character == null || EffectElementField == null || EffectObjectsField == null ||
            EffectStartedField == null) {
            orig(self);
            return;
        }

        // As the game's own: nothing until it has started, as it switches its parts once more then
        if (EffectStartedField.GetValue(self) is not true) {
            return;
        }

        var element = ElementOf(character);
        var on = EffectElementField.GetValue(self) is NailElements partElement && partElement == element;
        if (EffectObjectsField.GetValue(self) is not GameObject[] parts) {
            return;
        }

        foreach (var part in parts) {
            if (part != null) {
                part.SetActive(on);
            }
        }
    }

    /// <summary>
    /// A charged slash taking on the element of the needle as it starts (HeroExtraNailSlash): the tint of the element on
    /// its sprites, and the element on its blows. It goes by the needle of the local hero, so the copy that shows another
    /// player's charged slash took the tint of the local player's element (see <see cref="OnEffectEnable"/>). On such a
    /// copy it takes the tint of that player's element instead, or none, and its blows take no element, as the blows of
    /// the partner's slashes do no more here than they did before.
    /// </summary>
    public static void OnExtraSlashEnable(Action<HeroExtraNailSlash> orig, HeroExtraNailSlash self) {
        var character = HeroCopies.CharacterOf?.Invoke(self.transform);
        if (character == null) {
            orig(self);
            return;
        }

        var element = ElementOf(character);
        var config = GetConfig(element);
        var tint = config != null ? config.NailTintColor : Color.white;
        if (TintSpritesField?.GetValue(self) is SpriteRenderer[] sprites) {
            foreach (var sprite in sprites) {
                if (sprite != null) {
                    sprite.color = tint;
                }
            }
        }

        if (TintTk2dSpritesField?.GetValue(self) is tk2dBaseSprite[] tk2dSprites) {
            foreach (var sprite in tk2dSprites) {
                if (sprite != null) {
                    sprite.color = tint;
                }
            }
        }

        if (DamagersField?.GetValue(self) is DamageEnemies[] damagers) {
            foreach (var damager in damagers) {
                if (damager != null) {
                    damager.NailElement = NailElements.None;
                    damager.NailImbuement = null;
                }
            }
        }

        var hero = HeroController.SilentInstance;
        var ownElement = hero != null ? hero.NailImbuement.CurrentElement : NailElements.None;
        Logger.Info(
            $"[Imbued] Charged slash '{self.name}' of '{character.name}' shows that player's needle with " +
            $"{element}; this player's own needle has {ownElement}"
        );
    }

    /// <summary>
    /// The look of an element, from the local hero's own handling of them, or null for none.
    /// </summary>
    private static NailImbuementConfig? GetConfig(NailElements element) {
        var hero = HeroController.instance;
        if (element == NailElements.None || hero == null ||
            ConfigsField?.GetValue(hero.NailImbuement) is not NailImbuementConfig[] configs ||
            (int) element >= configs.Length) {
            return null;
        }

        return configs[(int) element];
    }
}
