using System;
using System.Collections.Generic;
using SSMP.Internals;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation.Effects;

/// <summary>
/// The particles on the hero that the game starts and stops without switching anything on or off, like the smoke of a
/// shot or the spatter of a syringe, for the tools that show them, and the dust of sliding down a wall. Each is watched
/// on the local hero, and a copy of it on the character of the player it belongs to starts and stops with it. Particles
/// that give off are said again for as long as they do, so that a lost word that they stopped leaves the copy giving off
/// no longer than a moment.
/// </summary>
internal class HeroChildParticles : AnimationEffect {
    /// <summary>
    /// The single instance, which <see cref="AnimationManager"/> plays for every player.
    /// </summary>
    public static readonly HeroChildParticles Instance = new();

    /// <summary>
    /// How often the local hero's particles that give off are said again, in seconds.
    /// </summary>
    internal const float SaidEvery = 0.5f;

    /// <summary>
    /// How long a copy gives off after it was last said to, in seconds.
    /// </summary>
    internal const float HeardFor = 1.5f;

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
        "Tool Effects/Pt Extract Swamp",
        "Effects/Wallslide Dust"
    ];

    /// <summary>
    /// The watched particles that always play, which the game starts and stops by switching what they give off on and
    /// off instead, like the dust of sliding down a wall.
    /// </summary>
    private static readonly HashSet<string> ByEmission = new(StringComparer.Ordinal) { "Effects/Wallslide Dust" };

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
            watcher.ByEmission = ByEmission.Contains(Paths[i]);
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
        var path = Paths[effectInfo[0]];
        var copy = HeroChildEffects.GetCopy(playerObject, path, emitting);
        if (copy == null || !copy.TryGetComponent<ParticleSystem>(out var particles)) {
            return;
        }

        var byEmission = ByEmission.Contains(path);
        if (!copy.TryGetComponent<HeroParticleCopy>(out var heard)) {
            heard = copy.AddComponent<HeroParticleCopy>();
        }

        heard.Particles = particles;
        heard.ByEmission = byEmission;
        heard.HeardAt = emitting ? Time.unscaledTime : null;

        // Particles that always play give off only while the player's own do
        if (byEmission) {
            if (!copy.activeSelf) {
                copy.SetActive(true);
            }

            if (!particles.isPlaying) {
                particles.Play(true);
            }

            var emission = particles.emission;
            emission.enabled = emitting;
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
    /// Stops particles of a copy from giving off, for a copy that was told to or that nobody said gives off for a while.
    /// </summary>
    internal static void StopGivingOff(ParticleSystem particles, bool byEmission) {
        if (byEmission) {
            var emission = particles.emission;
            emission.enabled = false;
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
                if (ByEmission.Contains(path)) {
                    var emission = particles.emission;
                    emission.enabled = false;
                }

                if (copy.TryGetComponent<HeroParticleCopy>(out var heard)) {
                    heard.HeardAt = null;
                }
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
    /// Whether the particles always play, and the game switches what they give off instead (see
    /// <see cref="HeroChildParticles"/>).
    /// </summary>
    [NonSerialized]
    public bool ByEmission;

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

    /// <summary>
    /// When to say again that the particles give off, in unscaled time.
    /// </summary>
    private float _sayAgainAt;

    private void LateUpdate() {
        if (Changed == null || Particles == null) {
            return;
        }

        var emitting = ByEmission ? Particles.emission.enabled : Particles.isEmitting;
        if (emitting == _emitting && (!emitting || Time.unscaledTime < _sayAgainAt)) {
            return;
        }

        _emitting = emitting;
        _sayAgainAt = Time.unscaledTime + HeroChildParticles.SaidEvery;
        Changed(Index, emitting);
    }
}

/// <summary>
/// Stops the particles of a copy on another player's character from giving off when nobody said for a while that the
/// player's own do, for <see cref="HeroChildParticles"/>: the word that they stopped may have been lost.
/// </summary>
internal class HeroParticleCopy : MonoBehaviour {
    /// <summary>
    /// The particles of the copy.
    /// </summary>
    [NonSerialized]
    public ParticleSystem? Particles;

    /// <summary>
    /// Whether the particles always play, and give off or not instead.
    /// </summary>
    [NonSerialized]
    public bool ByEmission;

    /// <summary>
    /// When the player last said that their particles give off, in unscaled time, or null if they don't.
    /// </summary>
    [NonSerialized]
    public float? HeardAt;

    private void Update() {
        if (HeardAt is not { } heardAt || Time.unscaledTime - heardAt <= HeroChildParticles.HeardFor) {
            return;
        }

        HeardAt = null;
        if (Particles != null) {
            HeroChildParticles.StopGivingOff(Particles, ByEmission);
        }
    }
}
