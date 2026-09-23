using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// The look of a needle that has taken on an element from a tool: for as long as it lasts the hero flashes in the
/// colour of the element with embers or bubbles around them, and each slash takes the tint of the element with a burst
/// of it on the slash. The thrower's game sends when the element comes and goes; the slashes carry it themselves. Only
/// the look goes: the loop of sound around the hero is left out, and the partner's slashes do no more to the enemies
/// here than they did before.
/// </summary>
internal static class ImbuedNail {
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// The looks of the elements, on the hero's own handling of them, by the element.
    /// </summary>
    private static readonly FieldInfo? ConfigsField = typeof(HeroNailImbuement).GetField("nailConfigs", Flags);

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
            particles = Object.Instantiate(
                config.HeroParticles.gameObject,
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
            var burst = Object.Instantiate(config.SlashEffect, slash.transform).transform;
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
