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
///
/// Physics takes a body whose object was moved as moved there, and puts it where its object is at its next step. That
/// is where the body is already for a body drawn where physics left it, but not for one drawn between the steps of
/// physics, or one put somewhere by its body since the last step: a creature with such a body anywhere under it is not
/// shown sliding, or stops sliding, and goes the rest of the way at once as before.
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
    /// How far a body may be from its object for the two to count as in the same place.
    /// </summary>
    private const float SamePlace = 0.0001f;

    /// <summary>
    /// The creatures shown sliding.
    /// </summary>
    private static readonly List<Slide> Slides = [];

    /// <summary>
    /// Whether the hook before each frame is drawn is registered.
    /// </summary>
    private static bool _registered;

    /// <summary>
    /// Whether what puts the creatures back after each frame is drawn runs, and the last frame it ran in or started.
    /// </summary>
    private static bool _puttingBack;

    /// <inheritdoc cref="_puttingBack"/>
    private static int _putBackFrame;

    /// <summary>
    /// Whether something went wrong here already and was said in the log, which is said once.
    /// </summary>
    private static bool _failed;

    /// <summary>
    /// A creature shown sliding to where it is.
    /// </summary>
    private sealed class Slide(Transform target, Rigidbody2D[] bodies, Vector2 way, float duration) {
        /// <summary>
        /// The creature.
        /// </summary>
        public readonly Transform Target = target;

        /// <summary>
        /// The bodies of the creature and of everything under it, which move with it.
        /// </summary>
        public readonly Rigidbody2D[] Bodies = bodies;

        /// <summary>
        /// From where it is to where it is shown at the start.
        /// </summary>
        public readonly Vector2 Way = way;

        /// <summary>
        /// When it started, in the game's time.
        /// </summary>
        public readonly float StartedAt = Time.time;

        /// <summary>
        /// How long it takes, in seconds.
        /// </summary>
        public readonly float Duration = duration;

        /// <summary>
        /// While the creature is moved for the frame being drawn: where it was before and where it was moved to, to be
        /// put back; null when it isn't moved.
        /// </summary>
        public (Vector3 From, Vector3 To)? Moved;

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

            var distance = way.magnitude;
            if (distance < LeastDistance || distance > MostDistance) {
                return;
            }

            var bodies = target.GetComponentsInChildren<Rigidbody2D>();
            foreach (var body in bodies) {
                if (body.interpolation != RigidbodyInterpolation2D.None) {
                    return;
                }
            }

            var duration = Mathf.Clamp(0.1f + 0.03f * distance, LeastTime, MostTime);
            Slides.Add(new Slide(target, bodies, way, duration));
            if (distance >= LoggedDistance) {
                Logger.Info($"'{target.name}' is shown sliding {distance:0.00} units in {duration:0.00} s to {why}");
            }

            if (!_registered) {
                _registered = true;
                Application.onBeforeRender += OnBeforeRender;
            }

            KeepPuttingBack();
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
            if (PutBack()) {
                Logger.Warn("Creatures shown sliding were not put back after the last frame was drawn");
            }

            var time = Time.time;
            for (var i = Slides.Count - 1; i >= 0; i--) {
                var slide = Slides[i];
                if (slide.Target == null || time - slide.StartedAt >= slide.Duration || !BodiesInPlace(slide)) {
                    Slides.RemoveAt(i);
                    continue;
                }

                var position = slide.Target.position;
                var left = slide.LeftAt(time);
                var shown = new Vector3(position.x + left.x, position.y + left.y, position.z);
                slide.Target.position = shown;
                slide.Moved = (position, shown);
            }

            if (Slides.Count > 0) {
                KeepPuttingBack();
            }
        } catch (Exception e) {
            SayFailed(e);
        }
    }

    /// <summary>
    /// Whether each body that moves with a creature is where its object is, so that moving the object for the frame
    /// and back leaves the body where it is (see <see cref="ShownSlide"/>).
    /// </summary>
    private static bool BodiesInPlace(Slide slide) {
        foreach (var body in slide.Bodies) {
            if (body == null || !body.simulated || !body.gameObject.activeInHierarchy) {
                continue;
            }

            if (body.interpolation != RigidbodyInterpolation2D.None ||
                ((Vector2) body.transform.position - body.position).sqrMagnitude > SamePlace * SamePlace) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Starts what puts the creatures back after each frame is drawn, unless it runs: it ran in the last frame or
    /// this one.
    /// </summary>
    private static void KeepPuttingBack() {
        if (_puttingBack && Time.frameCount - _putBackFrame <= 1) {
            return;
        }

        _putBackFrame = Time.frameCount;
        _puttingBack = MonoBehaviourUtil.Instance.StartCoroutine(PutBackAfterDrawing()) != null;
    }

    /// <summary>
    /// Puts the creatures moved for each frame back where they are once the frame is drawn, for as long as any slides.
    /// </summary>
    private static IEnumerator PutBackAfterDrawing() {
        var endOfFrame = new WaitForEndOfFrame();
        try {
            while (Slides.Count > 0) {
                yield return endOfFrame;

                _putBackFrame = Time.frameCount;
                PutBack();
            }
        } finally {
            _puttingBack = false;
        }
    }

    /// <summary>
    /// Puts each creature moved for the frame back where it is: where it was before, or, should something have moved
    /// it since, by the way it was moved.
    /// </summary>
    /// <returns>Whether any creature was moved.</returns>
    private static bool PutBack() {
        var any = false;
        foreach (var slide in Slides) {
            if (slide.Moved is not { } moved) {
                continue;
            }

            var (from, to) = moved;
            any = true;
            slide.Moved = null;
            if (slide.Target == null) {
                continue;
            }

            var position = slide.Target.position;
            slide.Target.position = position == to ? from : position - (to - from);
        }

        return any;
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
