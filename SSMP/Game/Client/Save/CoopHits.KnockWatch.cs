using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The [Knock] lines: how far each knockback of a creature moved it on this player's screen, beside how far the game's
/// own knockback carries it. USER 2026-10-22: "感觉打怪的时候怪的受击位移有点大", and "击退距离一样手感才是一样的".
/// Nothing in the log said how far a creature went, so a knockback that looked too long could not be told from the
/// game's own. A line is written for each knockback of this player's hit on a creature, whether this game runs the
/// creature or shows a copy of the other game's - also with nobody else in the room, which is the game's own knockback
/// to hold the others up against - and for each knockback of the partner's hit that this game plays as the scene host.
/// </summary>
internal partial class CoopHits {
    /// <summary>
    /// How long a knockback is watched, in seconds of game time: the knockback itself, which takes a fraction of that,
    /// and what follows it, like a copy sliding to where the other game has the creature.
    /// </summary>
    private const float KnockWatchTime = 1f;

    /// <summary>
    /// The most [Knock] lines in a minute, as many as the [Damage] lines of the same hits may take.
    /// </summary>
    private const int KnockLinesPerMinute = 60;

    /// <summary>
    /// The names of the ways a knockback goes, by its direction (Recoil.RecoilByDirection).
    /// </summary>
    private static readonly string[] KnockWays = ["right", "up", "left", "down"];

    /// <summary>
    /// The knockbacks being watched, one for each knockback of a creature: a new one cuts the one before short.
    /// </summary>
    private readonly Dictionary<Recoil, KnockWatch> _knockWatches = new();

    /// <summary>
    /// The watches that are over in this frame, kept to be written once the look at all of them is done.
    /// </summary>
    private readonly List<KnockWatch> _knockWatchesOver = [];

    /// <summary>
    /// When the minute that the [Knock] lines are counted in started, in real time.
    /// </summary>
    private float _knockMinuteStart = float.NegativeInfinity;

    /// <summary>
    /// How many [Knock] lines were written in the minute, and how many were left out.
    /// </summary>
    private int _knockLinesThisMinute, _knockLinesLeftOut;

    /// <summary>
    /// A knockback being watched (see <see cref="WatchKnock"/>).
    /// </summary>
    private sealed class KnockWatch {
        public Recoil Recoil = null!;
        public Entity.Entity Entity = null!;

        /// <summary>
        /// The object that the knockback moves, which is the one on show here: the room's own creature where this
        /// game runs it, the copy where it shows the other game's.
        /// </summary>
        public Transform Shown = null!;

        public bool IsCopy;
        public string Whose = "";
        public int Direction;
        public float Magnitude;

        /// <summary>
        /// How fast the knockback goes and for how long, as the game set it for this one.
        /// </summary>
        public float Speed, Duration;

        /// <summary>
        /// The way the knockback goes, as a vector of length one.
        /// </summary>
        public Vector2 Axis;

        /// <summary>
        /// Where the creature was on show as the knockback started, and when, in game time.
        /// </summary>
        public Vector2 From;

        public float StartedAt;

        /// <summary>
        /// How far the steps of the knockback itself moved the creature here, and how many steps did.
        /// </summary>
        public Vector2 Pushed;

        public int Steps;

        /// <summary>
        /// How far the creature on show was the way of the knockback when the knockback was over here, and how long
        /// after it started; null while it goes on.
        /// </summary>
        public float? AtEnd;

        public float EndedAt;

        /// <summary>
        /// The furthest the creature on show got the way of the knockback, and the other way, and where it was at the
        /// last look.
        /// </summary>
        public float Furthest, Nearest, Last;

        /// <summary>
        /// The state that the copy's own FSM ran in here, if one did while it was watched.
        /// </summary>
        public string? RanHere;

        /// <summary>
        /// Why the watch ended before its time, or null.
        /// </summary>
        public string? Over;
    }

    /// <summary>
    /// Whether a knockback was taken: the game's own way of telling, which winds the knockback's clock back up for one
    /// that it takes, and leaves it alone for one that it refuses.
    /// </summary>
    /// <param name="recoil">The knockback.</param>
    /// <param name="before">What was left on its clock before.</param>
    private static bool KnockbackStarted(Recoil recoil, float before) {
        return recoil.recoilTimeRemaining > before && recoil.recoilSpeed > 0f;
    }

    /// <summary>
    /// Starts watching a knockback that has just started on a creature, in place of the one before on the same
    /// creature, which it cuts short.
    /// </summary>
    /// <param name="recoil">The knockback.</param>
    /// <param name="entity">The entity of the creature.</param>
    /// <param name="isCopy">Whether the creature on show is the copy of the other game's.</param>
    /// <param name="direction">The way it goes (Recoil.RecoilByDirection).</param>
    /// <param name="magnitude">How hard it was knocked.</param>
    /// <param name="whose">Whose hit it was, for the line.</param>
    private void WatchKnock(
        Recoil recoil,
        Entity.Entity entity,
        bool isCopy,
        int direction,
        float magnitude,
        string whose
    ) {
        try {
            var now = Time.time;
            if (_knockWatches.TryGetValue(recoil, out var before)) {
                _knockWatches.Remove(recoil);
                before.Over = $"the next knockback came {now - before.StartedAt:0.00} s on";
                WriteKnock(before);
            }

            var way = direction & 3;
            var shown = recoil.transform;
            _knockWatches[recoil] = new KnockWatch {
                Recoil = recoil,
                Entity = entity,
                Shown = shown,
                IsCopy = isCopy,
                Whose = whose,
                Direction = way,
                Magnitude = magnitude,
                Speed = recoil.recoilSpeed,
                Duration = recoil.recoilDuration,
                Axis = way switch {
                    0 => Vector2.right,
                    1 => Vector2.up,
                    2 => Vector2.left,
                    _ => Vector2.down
                },
                From = shown.position,
                StartedAt = now
            };
        } catch (Exception e) {
            Logger.Warn($"Could not watch a knockback: {e.Message}");
        }
    }

    /// <summary>
    /// Follows the knockbacks being watched at the end of the frame, after all that moves the creatures on show has
    /// moved them, and writes those that are over.
    /// </summary>
    private void WatchKnocks() {
        if (_knockWatches.Count == 0) {
            return;
        }

        try {
            var now = Time.time;
            foreach (var watch in _knockWatches.Values) {
                if (watch.Recoil == null || watch.Shown == null) {
                    watch.Over = "it was gone";
                    _knockWatchesOver.Add(watch);
                    continue;
                }

                var along = Vector2.Dot((Vector2) watch.Shown.position - watch.From, watch.Axis);
                watch.Furthest = Mathf.Max(watch.Furthest, along);
                watch.Nearest = Mathf.Min(watch.Nearest, along);
                watch.Last = along;
                if (watch.AtEnd == null && !watch.Recoil.GetIsRecoiling()) {
                    watch.AtEnd = along;
                    watch.EndedAt = now - watch.StartedAt;
                }

                if (watch.IsCopy && watch.RanHere == null) {
                    watch.RanHere = watch.Entity.RunsHereIn;
                }

                if (now - watch.StartedAt >= KnockWatchTime) {
                    _knockWatchesOver.Add(watch);
                }
            }

            foreach (var watch in _knockWatchesOver) {
                _knockWatches.Remove(watch.Recoil);
                WriteKnock(watch);
            }
        } catch (Exception e) {
            Logger.Warn($"Could not follow the knockbacks: {e.Message}");
            _knockWatches.Clear();
        } finally {
            _knockWatchesOver.Clear();
        }
    }

    /// <summary>
    /// Writes the [Knock] line of a knockback that was watched (see the class doc).
    /// </summary>
    /// <param name="watch">The watch.</param>
    private void WriteKnock(KnockWatch watch) {
        var now = Time.unscaledTime;
        if (now - _knockMinuteStart >= 60f) {
            if (_knockLinesLeftOut > 0) {
                Logger.Info($"[Knock] {_knockLinesLeftOut} more in the minute before were not written");
            }

            _knockMinuteStart = now;
            _knockLinesThisMinute = 0;
            _knockLinesLeftOut = 0;
        }

        if (_knockLinesThisMinute >= KnockLinesPerMinute) {
            _knockLinesLeftOut++;
            return;
        }

        _knockLinesThisMinute++;

        var way = KnockWays[watch.Direction];
        var line = new StringBuilder("[Knock] '");
        line.Append(watch.Shown != null ? watch.Shown.name : "a creature that is gone");
        line.Append(watch.IsCopy ? "', the copy of the other game's," : "', which this game runs,");
        line.Append($" was knocked {way} by {watch.Whose} (magnitude {watch.Magnitude:0.00}): the knockback, ");
        line.Append($"{watch.Speed:0.0} a second for {watch.Duration:0.00} s, moved it {watch.Pushed.magnitude:0.00} ");
        line.Append($"here in {watch.Steps} step(s); on screen it went ");
        if (watch.AtEnd is { } atEnd) {
            line.Append($"{atEnd:0.00} {way} by the end of it, {watch.EndedAt:0.00} s on, ");
        }

        line.Append($"{watch.Furthest:0.00} {way} at most");
        if (watch.Nearest < -0.05f) {
            line.Append($" and {-watch.Nearest:0.00} the other way");
        }

        line.Append($", and was {watch.Last:0.00} {way} {Time.time - watch.StartedAt:0.00} s on");
        if (watch.Over != null) {
            line.Append($" ({watch.Over})");
        }

        if (watch.IsCopy) {
            AppendCopyKnock(line, watch, way);
        }

        Logger.Info(line.ToString());
    }

    /// <summary>
    /// Adds to a [Knock] line of a copy what else moved it: its own FSM run here, the wait on the scene host for the
    /// knockback, with how far the scene host said it pushed the creature and how far the copy went on by itself
    /// meanwhile, and the slide to where the scene host had the creature once the copy followed it again.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="watch">The watch of the copy's knockback.</param>
    /// <param name="way">The name of the way the knockback went.</param>
    private static void AppendCopyKnock(StringBuilder line, KnockWatch watch, string way) {
        if (watch.RanHere != null) {
            line.Append($"; its own FSM ran here in '{watch.RanHere}'");
        }

        if (watch.Entity.LastKnockbackWait is { } wait && wait.At >= watch.StartedAt) {
            line.Append(
                $"; the wait on the scene host ended {wait.At - watch.StartedAt:0.00} s on as {wait.How}: it said " +
                $"it pushed it {Vector2.Dot(wait.Told, watch.Axis):0.00} {way} ({wait.Told.magnitude:0.00} in all), " +
                $"and the copy went on {wait.OwnMoved.magnitude:0.00} by what the creature did by itself"
            );
        }

        if (watch.Entity.LastHandOver is { } handOver && handOver.At >= watch.StartedAt) {
            line.Append(
                $"; {handOver.At - watch.StartedAt:0.00} s on it followed the scene host again, sliding " +
                $"{Vector2.Dot(handOver.Slide, watch.Axis):0.00} {way} ({handOver.Slide.magnitude:0.00} in all) " +
                "to where it has the creature"
            );
        }
    }
}
