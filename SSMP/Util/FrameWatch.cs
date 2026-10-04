using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SSMP.Game.Client;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Util;

/// <summary>
/// Writes down in the log when the game stops drawing its frames smoothly while it is played: a single frame that takes
/// long, a few seconds with few frames or many stutters, and a game that draws no frame at all for a while. Each line
/// says how much of it went to the mod's per-frame handlers and to which of them, and whether memory was cleaned up
/// meanwhile, so that a freeze or a stutter can be put down to its cause. The mod's coroutines and its hooks into the
/// game's own methods aren't timed, so a small share of the handlers doesn't clear the mod. Normal play writes nothing,
/// and neither loading a room nor the game window being behind is counted.
/// </summary>
internal static class FrameWatch {
    /// <summary>
    /// How long, in seconds, a frame takes at least for it to be written down by itself.
    /// </summary>
    private const double LongFrameTime = 0.15;

    /// <summary>
    /// How long, in seconds, a frame takes at least for it to count as a stutter.
    /// </summary>
    private const double StutterTime = 0.06;

    /// <summary>
    /// How many stutters a stretch of frames has at least for it to be written down.
    /// </summary>
    private const int StuttersToWrite = 3;

    /// <summary>
    /// How long, in seconds, the stretches are that frames are counted together in.
    /// </summary>
    private const double StretchTime = 5;

    /// <summary>
    /// How long, in seconds, the frames of a stretch take on average at least for it to be written down.
    /// </summary>
    private const double SlowStretchFrameTime = 0.05;

    /// <summary>
    /// How long, in seconds, there is at least between two lines about single frames, which a stretch sums up.
    /// </summary>
    private const double LongFrameLineInterval = 1;

    /// <summary>
    /// How long, in seconds, the game draws no frame before the watching thread writes it down.
    /// </summary>
    private const double StallTime = 1;

    /// <summary>
    /// The length of a tick of <see cref="Stopwatch"/>, in seconds.
    /// </summary>
    private static readonly double TickTime = 1.0 / Stopwatch.Frequency;

    /// <summary>
    /// The name of the part of the mod's work of each frame that runs right now, if any, for the watching thread.
    /// </summary>
    public static volatile string? Running;

    /// <summary>
    /// When the last frame began, in ticks of <see cref="Stopwatch"/>, or 0 when it isn't watched, like while a room
    /// loads. The watching thread reads it.
    /// </summary>
    private static long _lastFrame;

    /// <summary>
    /// Whether the game's window was in front at the last frame. A game that is behind may draw nothing, which is no
    /// freeze.
    /// </summary>
    private static volatile bool _focused;

    /// <summary>
    /// The scene that was active at the last frame.
    /// </summary>
    private static int _lastScene;

    /// <summary>
    /// How many times memory was cleaned up before the last frame.
    /// </summary>
    private static int _lastCollections;

    /// <summary>
    /// When a single frame was last written down, in ticks of <see cref="Stopwatch"/>.
    /// </summary>
    private static long _lastLongFrameLine;

    /// <summary>
    /// When the stretch of frames counted now began, in ticks of <see cref="Stopwatch"/>.
    /// </summary>
    private static long _stretchStart;

    /// <summary>
    /// How many frames the stretch has.
    /// </summary>
    private static int _stretchFrames;

    /// <summary>
    /// The time the frames of the stretch took together, in seconds.
    /// </summary>
    private static double _stretchTime;

    /// <summary>
    /// The time the slowest frame of the stretch took, in seconds.
    /// </summary>
    private static double _stretchSlowest;

    /// <summary>
    /// How many frames of the stretch were stutters.
    /// </summary>
    private static int _stretchStutters;

    /// <summary>
    /// How many frames of the stretch were long enough to be written down by themselves.
    /// </summary>
    private static int _stretchLongFrames;

    /// <summary>
    /// How many times memory was cleaned up in the stretch.
    /// </summary>
    private static int _stretchCollections;

    /// <summary>
    /// The time the mod's own work took in the frames of the stretch, in ticks of <see cref="Stopwatch"/>.
    /// </summary>
    private static long _stretchModTicks;

    /// <summary>
    /// The thread that notices when the game draws no frame for a while.
    /// </summary>
    private static Thread? _watcher;

    /// <summary>
    /// Whether watching the frames threw, after which they aren't watched anymore.
    /// </summary>
    private static bool _failed;

    /// <summary>
    /// Parts of the mod's work of each frame that don't run in the events of <see cref="MonoBehaviourUtil"/>, which
    /// time themselves, by name.
    /// </summary>
    private static readonly Dictionary<string, TimedPart> PartsByName = new();

    /// <summary>
    /// The parts of <see cref="PartsByName"/>, which a new array replaces when a part is added.
    /// </summary>
    private static TimedPart[] _parts = [];

    /// <summary>
    /// Gives the part of the mod's work of each frame of the given name, which the caller times itself, adding it if
    /// it's new.
    /// </summary>
    public static TimedPart Part(string name) {
        lock (PartsByName) {
            if (!PartsByName.TryGetValue(name, out var part)) {
                part = new TimedPart(name);
                PartsByName[name] = part;
                _parts = PartsByName.Values.ToArray();
            }

            return part;
        }
    }

    /// <summary>
    /// Counts the frame that just ended, from the start of the mod's per-frame handlers of the last frame to the start of
    /// this one, with the handlers of each frame, of the last steps of frames and of the steps of physics in between.
    /// </summary>
    public static void OnFrame(TimedEvent update, TimedEvent lateUpdate, TimedEvent fixedUpdate) {
        if (_failed) {
            return;
        }

        // What only watches must never keep the mod's own work of the frame from running
        try {
            WatchFrame(update, lateUpdate, fixedUpdate);
        } catch (Exception e) {
            _failed = true;
            Interlocked.Exchange(ref _lastFrame, 0);
            Logger.Error($"Watching the frames threw, and they are no longer watched:\n{e}");
        }
    }

    /// <inheritdoc cref="OnFrame"/>
    private static void WatchFrame(TimedEvent update, TimedEvent lateUpdate, TimedEvent fixedUpdate) {
        var now = Stopwatch.GetTimestamp();
        var collections = GC.CollectionCount(0);
        var scene = SceneManager.GetActiveScene().handle;
        var focused = Application.isFocused;

        // Only the field, as the property looks for the player character through the whole scene when there is none,
        // which is every frame of the menus
        var watched = HeroController.UnsafeInstance != null && focused &&
                      global::GameManager.SilentInstance is { IsInSceneTransition: false } && scene == _lastScene;
        var lastFrame = Interlocked.Read(ref _lastFrame);
        var counts = watched && lastFrame != 0;
        if (counts) {
            CountFrame(now, (now - lastFrame) * TickTime, collections - _lastCollections, update, lateUpdate, fixedUpdate);
        }

        update.EndFrame(counts);
        lateUpdate.EndFrame(counts);
        fixedUpdate.EndFrame(counts);
        foreach (var part in _parts) {
            part.EndFrame(counts);
        }

        // A frame that isn't counted ends the stretch where it began
        if (!counts) {
            EndStretch(lastFrame != 0 ? lastFrame : now, now, update, lateUpdate, fixedUpdate);
        } else if ((now - _stretchStart) * TickTime >= StretchTime) {
            EndStretch(now, now, update, lateUpdate, fixedUpdate);
        }

        _lastScene = scene;
        _lastCollections = collections;
        _focused = focused;
        Interlocked.Exchange(ref _lastFrame, watched ? now : 0);

        if (_watcher == null) {
            _watcher = new Thread(Watch) { IsBackground = true, Name = "SSMP frame watch" };
            _watcher.Start();
        }
    }

    /// <summary>
    /// Counts a frame in the stretch, and writes it down by itself if it took long.
    /// </summary>
    private static void CountFrame(
        long now,
        double frameTime,
        int collections,
        TimedEvent update,
        TimedEvent lateUpdate,
        TimedEvent fixedUpdate
    ) {
        var modTicks = update.FrameTicks + lateUpdate.FrameTicks + fixedUpdate.FrameTicks;
        foreach (var part in _parts) {
            modTicks += part.FrameTicks;
        }

        _stretchFrames++;
        _stretchTime += frameTime;
        _stretchSlowest = System.Math.Max(_stretchSlowest, frameTime);
        _stretchCollections += collections;
        _stretchModTicks += modTicks;
        if (frameTime >= StutterTime) {
            _stretchStutters++;
        }

        if (frameTime < LongFrameTime) {
            return;
        }

        _stretchLongFrames++;
        if ((now - _lastLongFrameLine) * TickTime < LongFrameLineInterval) {
            return;
        }

        _lastLongFrameLine = now;
        Logger.Info(
            $"[Frames] {DateTime.Now:HH:mm:ss} A frame took {frameTime * 1000:0} ms in " +
            $"'{SceneUtil.GetCurrentSceneName()}', with {collections} memory cleanup(s), " +
            $"{fixedUpdate.FrameRuns} step(s) of physics; the mod's per-frame handlers took " +
            $"{modTicks * TickTime * 1000:0.0} ms of it" +
            $"{DescribeSlowest(part => part.FrameTicks, 1, update, lateUpdate, fixedUpdate)}; {DescribeState()}"
        );
    }

    /// <summary>
    /// Writes the stretch of frames down if it went badly, and starts a new one.
    /// </summary>
    /// <param name="end">When the last frame counted in the stretch ended, in ticks of <see cref="Stopwatch"/>.</param>
    /// <param name="now">When the new stretch begins, in ticks of <see cref="Stopwatch"/>.</param>
    private static void EndStretch(long end, long now, TimedEvent update, TimedEvent lateUpdate, TimedEvent fixedUpdate) {
        var length = (end - _stretchStart) * TickTime;
        if (_stretchFrames > 0 && (_stretchTime / _stretchFrames >= SlowStretchFrameTime || _stretchLongFrames > 0 ||
                                   _stretchStutters >= StuttersToWrite)) {
            Logger.Info(
                $"[Frames] {DateTime.Now:HH:mm:ss} The last {length:0.0} s in '{SceneUtil.GetCurrentSceneName()}' " +
                $"had {_stretchFrames} frame(s), {_stretchFrames / System.Math.Max(_stretchTime, 0.001):0} a second, " +
                $"the slowest {_stretchSlowest * 1000:0} ms, {_stretchStutters} over {StutterTime * 1000:0} ms and " +
                $"{_stretchLongFrames} over {LongFrameTime * 1000:0} ms, with {_stretchCollections} memory cleanup(s) " +
                $"and {GC.GetTotalMemory(false) / (1024 * 1024)} MB of memory in use by scripts; the mod's per-frame " +
                $"handlers took {_stretchModTicks * TickTime * 1000 / _stretchFrames:0.0} ms a frame" +
                $"{DescribeSlowest(part => part.StretchTicks, _stretchFrames, update, lateUpdate, fixedUpdate)}; " +
                DescribeState()
            );
        }

        _stretchStart = now;
        _stretchFrames = 0;
        _stretchTime = 0;
        _stretchSlowest = 0;
        _stretchStutters = 0;
        _stretchLongFrames = 0;
        _stretchCollections = 0;
        _stretchModTicks = 0;
        update.EndStretch();
        lateUpdate.EndStretch();
        fixedUpdate.EndStretch();
        foreach (var part in _parts) {
            part.StretchTicks = 0;
        }
    }

    /// <summary>
    /// Names the mod's per-frame handlers that took the most time, with the milliseconds they took a frame; handlers of
    /// the same name, like those of each creature, are added up.
    /// </summary>
    private static string DescribeSlowest(Func<TimedPart, long> ticksOf, int frames, params TimedEvent[] events) {
        var slowest = events
            .SelectMany<TimedEvent, TimedPart>(timedEvent => timedEvent.Handlers)
            .Concat(_parts)
            .GroupBy(part => part.Name)
            .Select(group => (Name: group.Key, Count: group.Count(), Time: group.Sum(ticksOf) * TickTime * 1000 / frames))
            .Where(part => part.Time >= 0.1)
            .OrderByDescending(part => part.Time)
            .Take(5)
            .Select(part => $"{part.Name}{(part.Count > 1 ? $" x{part.Count}" : "")} {part.Time:0.0}")
            .ToList();

        return slowest.Count == 0 ? "" : $", most in {string.Join(", ", slowest)}";
    }

    /// <summary>
    /// What the players were doing that is worth knowing about a slow frame.
    /// </summary>
    private static string DescribeState() {
        return $"the partner {(NeedolinCoop.HasRemotePerformers ? "plays" : "doesn't play")} the needolin here, " +
               $"this player {(HeroPerformanceRegion.IsPerforming ? "plays" : "doesn't play")} it";
    }

    /// <summary>
    /// Looks every quarter of a second whether the game drew a frame lately, and writes it down once for each frame that
    /// it waits for for long, with the mod's per-frame handler that was running, if any. The line about the frame itself
    /// follows once it is drawn.
    /// </summary>
    private static void Watch() {
        long written = 0;
        while (true) {
            Thread.Sleep(250);

            // A thread of its own that throws could take the whole game down with it
            try {
                var lastFrame = Interlocked.Read(ref _lastFrame);
                if (lastFrame == 0 || lastFrame == written || !_focused) {
                    continue;
                }

                var waited = (Stopwatch.GetTimestamp() - lastFrame) * TickTime;
                if (waited < StallTime) {
                    continue;
                }

                written = lastFrame;
                var running = Running;
                Logger.Info(
                    $"[Frames] {DateTime.Now:HH:mm:ss} The game has drawn no frame for {waited:0.0} s; " +
                    (running == null
                        ? "none of the mod's per-frame handlers was running"
                        : $"the mod's per-frame handler '{running}' was running")
                );
            } catch (Exception e) {
                Logger.Error($"Watching for frames that never come threw:\n{e}");
                return;
            }
        }
    }
}
