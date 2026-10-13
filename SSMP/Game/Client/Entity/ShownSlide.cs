using System;
using System.Collections;
using System.Collections.Generic;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// Shows a creature that this game put somewhere at once sliding there instead, only in how it is drawn. The scene host
/// puts its creature where the partner's copy of it was as it takes what the partner did to that copy (see
/// Entity.TakeInput), and the copy is drawn behind the creature by the time its positions take to arrive and the time
/// the partner's input took to come back: a fast creature that caught the partner jumped back by a few units in the
/// host's game, in front of its player (USER 10-13: 没被卷死的人那边看会闪，就是整个旋风和里面的boss往旁边移了一点).
///
/// Right before each frame is drawn, the creature is moved by what is left of the way back to where it was drawn
/// before, and as soon as the frame is drawn it is put back where it is. Everything else - its state machines, its
/// body and what it hits, what this game sends of it - has it where it is all along, so the event the partner's input
/// played went from the copy's place as before.
/// </summary>
internal static class ShownSlide {
    /// <summary>
    /// The shortest way a creature is shown sliding: shorter ones aren't seen.
    /// </summary>
    private const float LeastDistance = 0.05f;

    /// <summary>
    /// The longest way a creature is shown sliding. Farther than that, the copy was not where the creature was for
    /// long, and it is put there at once as before.
    /// </summary>
    private const float MostDistance = 12f;

    /// <summary>
    /// How long a slide takes at least and at most, in seconds; in between it takes longer the longer the way.
    /// </summary>
    private const float LeastTime = 0.12f;

    /// <inheritdoc cref="LeastTime"/>
    private const float MostTime = 0.3f;

    /// <summary>
    /// The way shown sliding from which a slide is said in the log.
    /// </summary>
    private const float LoggedDistance = 1f;

    /// <summary>
    /// The creatures shown sliding.
    /// </summary>
    private static readonly List<Slide> Slides = [];

    /// <summary>
    /// Whether the hook before each frame is drawn is registered.
    /// </summary>
    private static bool _registered;

    /// <summary>
    /// Whether what puts the creatures back after each frame is drawn runs.
    /// </summary>
    private static bool _puttingBack;

    /// <summary>
    /// Whether something went wrong here already and was said in the log, which is said once.
    /// </summary>
    private static bool _failed;

    /// <summary>
    /// A creature shown sliding to where it is.
    /// </summary>
    private sealed class Slide(Transform target, Vector2 way, float duration) {
        /// <summary>
        /// The creature.
        /// </summary>
        public readonly Transform Target = target;

        /// <summary>
        /// From where it is to where it is shown at the start.
        /// </summary>
        public Vector2 Way = way;

        /// <summary>
        /// When it started, in the game's time.
        /// </summary>
        public float StartedAt = Time.time;

        /// <summary>
        /// How long it takes, in seconds.
        /// </summary>
        public float Duration = duration;

        /// <summary>
        /// Where the creature is while it is moved for the frame being drawn, to be put back there; null when it isn't.
        /// </summary>
        public Vector3? MovedFrom;

        /// <summary>
        /// What is left of the way, at a time: all of it at the start, none at the end, slowing in and out.
        /// </summary>
        public Vector2 LeftAt(float time) {
            var done = Mathf.Clamp01((time - StartedAt) / Duration);
            return Way * (1f - done * done * (3f - 2f * done));
        }
    }

    /// <summary>
    /// Shows a creature that this game just put somewhere at once sliding there from where it was drawn before.
    /// </summary>
    /// <param name="target">The creature.</param>
    /// <param name="way">The way it was put: from where it is to where it was.</param>
    /// <param name="why">What put it there, for the log.</param>
    public static void Start(Transform target, Vector2 way, string why) {
        try {
            // Still sliding from before, it was drawn short of where it was
            var index = Slides.FindIndex(slide => slide.Target == target);
            if (index >= 0) {
                way += Slides[index].LeftAt(Time.time);
                Slides.RemoveAt(index);
            }

            // A body drawn between the steps of physics would be put where it was drawn by moving it for a frame, which
            // physics takes as moving the body there
            var distance = way.magnitude;
            if (distance < LeastDistance || distance > MostDistance ||
                target.TryGetComponent<Rigidbody2D>(out var body) &&
                body.interpolation != RigidbodyInterpolation2D.None) {
                return;
            }

            var duration = Mathf.Clamp(0.1f + 0.03f * distance, LeastTime, MostTime);
            Slides.Add(new Slide(target, way, duration));
            if (distance >= LoggedDistance) {
                Logger.Info($"'{target.name}' is shown sliding {distance:0.00} units in {duration:0.00} s to {why}");
            }

            if (!_registered) {
                _registered = true;
                Application.onBeforeRender += OnBeforeRender;
            }

            if (!_puttingBack) {
                _puttingBack = true;
                MonoBehaviourUtil.Instance.StartCoroutine(PutBackAfterDrawing());
            }
        } catch (Exception e) {
            SayFailed(e);
        }
    }

    /// <summary>
    /// Right before a frame is drawn: moves each creature shown sliding by what is left of its way.
    /// </summary>
    private static void OnBeforeRender() {
        if (Slides.Count == 0) {
            return;
        }

        try {
            // Any that were not put back after the last frame, which should not happen, are put back first, so as not
            // to be moved twice
            PutBack();

            var time = Time.time;
            for (var i = Slides.Count - 1; i >= 0; i--) {
                var slide = Slides[i];
                if (slide.Target == null || time - slide.StartedAt >= slide.Duration) {
                    Slides.RemoveAt(i);
                    continue;
                }

                var position = slide.Target.position;
                var left = slide.LeftAt(time);
                slide.MovedFrom = position;
                slide.Target.position = new Vector3(position.x + left.x, position.y + left.y, position.z);
            }
        } catch (Exception e) {
            SayFailed(e);
        }
    }

    /// <summary>
    /// Puts the creatures moved for each frame back where they are once the frame is drawn, for as long as any slides.
    /// </summary>
    private static IEnumerator PutBackAfterDrawing() {
        var endOfFrame = new WaitForEndOfFrame();
        try {
            while (Slides.Count > 0) {
                yield return endOfFrame;

                PutBack();
            }
        } finally {
            _puttingBack = false;
        }
    }

    /// <summary>
    /// Puts each creature moved for the frame back where it is.
    /// </summary>
    private static void PutBack() {
        foreach (var slide in Slides) {
            if (slide.MovedFrom is not { } position) {
                continue;
            }

            slide.MovedFrom = null;
            if (slide.Target != null) {
                slide.Target.position = position;
            }
        }
    }

    /// <summary>
    /// Says in the log, once, that showing a slide went wrong.
    /// </summary>
    private static void SayFailed(Exception e) {
        if (_failed) {
            return;
        }

        _failed = true;
        Logger.Warn($"Could not show a creature sliding to where it was put; it may jump there:\n{e}");
    }
}
