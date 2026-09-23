using System;
using SSMP.Internals;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation.Effects;

/// <summary>
/// The particles on the hero that the game starts and stops without switching anything on or off, like the smoke of a
/// shot or the spatter of a syringe, for the tools that show them. Each is watched on the local hero, and a copy of it
/// on the character of the player it belongs to starts and stops with it.
/// </summary>
internal class HeroChildParticles : AnimationEffect {
    /// <summary>
    /// The single instance, which <see cref="AnimationManager"/> plays for every player.
    /// </summary>
    public static readonly HeroChildParticles Instance = new();

    /// <summary>
    /// The watched particles, by their path under the hero. Their place in this list is what they are known by over the
    /// network, so new ones go at the end.
    /// </summary>
    private static readonly string[] Paths = [
        "Tool Effects/WebShot Effects/Smoke S",
        "Tool Effects/WebShot Effects/Smoke W",
        "Tool Effects/WebShot Effects/Smoke Fail",
        "Tool Effects/Syringe Spatter",
        "Tool Effects/Syringe Spatter poison",
        "Tool Effects/RosaryCannon TurnShootEffect",
        "Tool Effects/Pt Extract M",
        "Tool Effects/Pt Extract B",
        "Tool Effects/Pt Extract Swamp"
    ];

    /// <summary>
    /// Starts watching the particles of the local hero, which may be a new hero or the same one again. This runs while
    /// the hero may still be being made, like <see cref="HeroChildEffects.Watch"/>.
    /// </summary>
    /// <param name="hero">The local hero.</param>
    /// <param name="send">Sends the effect info of a change to the other players.</param>
    public static void Watch(HeroController hero, Action<byte[]> send) {
        var heroTransform = hero.gameObject.transform;
        for (var i = 0; i < Paths.Length; i++) {
            var part = heroTransform.Find(Paths[i]);
            if (part == null || !part.TryGetComponent<ParticleSystem>(out var particles)) {
                Logger.Warn($"The hero has no particles '{Paths[i]}' to show to other players");
                continue;
            }

            if (!part.TryGetComponent<HeroParticleWatcher>(out var watcher)) {
                watcher = part.gameObject.AddComponent<HeroParticleWatcher>();
            }

            watcher.Index = i;
            watcher.Particles = particles;
            watcher.Changed = (index, emitting) => send([(byte) index, (byte) (emitting ? 1 : 0)]);
        }
    }

    /// <inheritdoc/>
    public override byte[]? GetEffectInfo() {
        return null;
    }

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        if (effectInfo == null || effectInfo.Length != 2 || effectInfo[0] >= Paths.Length) {
            return;
        }

        var emitting = effectInfo[1] == 1;
        var copy = HeroChildEffects.GetCopy(playerObject, Paths[effectInfo[0]], emitting);
        if (copy == null || !copy.TryGetComponent<ParticleSystem>(out var particles)) {
            return;
        }

        // The particles of the hero are always on and only play when started, which the copy has to be as well
        if (!copy.activeSelf) {
            copy.SetActive(true);
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        if (emitting) {
            particles.Play(true);
        } else {
            particles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }
    }

    /// <summary>
    /// Stops every copied particle on the character of another player, for a character that leaves the room.
    /// </summary>
    /// <param name="playerObject">The character of the other player.</param>
    public static void ResetCopies(GameObject playerObject) {
        foreach (var path in Paths) {
            var copy = HeroChildEffects.GetCopy(playerObject, path, false);
            if (copy != null && copy.TryGetComponent<ParticleSystem>(out var particles)) {
                particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }
    }
}

/// <summary>
/// Watches the particles of one part of the local hero for starting and stopping, for <see cref="HeroChildParticles"/>.
/// </summary>
internal class HeroParticleWatcher : MonoBehaviour {
    /// <summary>
    /// The index of the particles in the watched ones.
    /// </summary>
    [NonSerialized]
    public int Index;

    /// <summary>
    /// The particles.
    /// </summary>
    [NonSerialized]
    public ParticleSystem? Particles;

    /// <summary>
    /// Called with the index of the particles and whether they emit. Not serialized, so a copy of the part made for
    /// another player's character, which has this component too, watches nothing.
    /// </summary>
    [NonSerialized]
    public Action<int, bool>? Changed;

    /// <summary>
    /// Whether the particles emitted when last looked at.
    /// </summary>
    private bool _emitting;

    private void LateUpdate() {
        if (Changed == null || Particles == null) {
            return;
        }

        var emitting = Particles.isEmitting;
        if (emitting == _emitting) {
            return;
        }

        _emitting = emitting;
        Changed(Index, emitting);
    }
}
