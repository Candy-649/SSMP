using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using MonoMod.RuntimeDetour;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Util;

/// <summary>
/// Writes down in the log how long each change of room takes from its start to the moment the game fades the new room
/// in, and what the time went to, so that a long dark screen can be put down to its cause (USER 10-15: "每次进一个房间都
/// 黑屏很长时间，我朋友应该没有黑屏这么久"). The game times the parts of its own loading itself
/// (<see cref="SceneLoad.Phases"/>): waiting until it may fetch the room, clearing memory, fetching it, waiting until
/// it may wake it, waking its objects, clearing memory again, cleaning up memory, calling Start and loading a boss's
/// room. The line adds the time after the load until the fade in, the frames drawn meanwhile with the slowest, the
/// memory cleanups, the memory the computer had free, and the mod's share: its handlers of Unity's scene events by
/// name, which run inside the load, its per-frame handlers, and its taking of the room's state from the server. The
/// mod's hooks into the game's own methods, which run as the objects of the room wake up, aren't timed: they are part
/// of the waking. A change that the game ends without fading the room in, like coming back to life on a bench, ends
/// when the player character finished coming in; one that ends neither way, like a scene a cutscene loads, is dropped.
/// </summary>
internal static class LoadWatch {
    /// <summary>
    /// How long, in seconds, a change of room is watched at most without one of its ends coming, after which it is
    /// dropped without a line.
    /// </summary>
    private const float MaxChangeTime = 60;

    /// <summary>
    /// The length of a tick of <see cref="Stopwatch"/>, in milliseconds.
    /// </summary>
    private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// The binding flags of Unity's own methods that call the handlers of its scene events, and of their fields.
    /// </summary>
    private const BindingFlags UnityFlags = BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>
    /// What is known of a change of room that is watched.
    /// </summary>
    private sealed class Change {
        /// <summary>
        /// When it began, by <see cref="Time.realtimeSinceStartup"/>, which the game times its loading by.
        /// </summary>
        public readonly float Start = Time.realtimeSinceStartup;

        /// <summary>
        /// How many times memory had been cleaned up when it began.
        /// </summary>
        public readonly int Collections = GC.CollectionCount(0);

        /// <summary>
        /// How much memory the computer had free when it began, in MB, or -1 if that can't be known.
        /// </summary>
        public readonly long FreeMemory = GetFreeMemory();

        /// <summary>
        /// The room it began in, the room it goes to and the door it comes in at.
        /// </summary>
        public string From = "", To = "", Gate = "";

        /// <summary>
        /// The game's loading of the room, which times its own parts.
        /// </summary>
        public SceneLoad? Load;

        /// <summary>
        /// The frames drawn since it began, and the time the slowest of them took, in milliseconds.
        /// </summary>
        public int Frames;

        /// <inheritdoc cref="Frames"/>
        public double Slowest;

        /// <summary>
        /// The time the mod's per-frame handlers took in those frames, in ticks of <see cref="Stopwatch"/>.
        /// </summary>
        public long ModFrameTicks;

        /// <summary>
        /// The time the handlers of Unity's scene events took that aren't the mod's, the game's and other mods', in
        /// ticks.
        /// </summary>
        public long OtherEventTicks;

        /// <summary>
        /// The time the mod's handlers of Unity's scene events took, by name, in ticks.
        /// </summary>
        public readonly Dictionary<string, long> ModEventTicks = new();

        /// <summary>
        /// The time the mod took to take the state of the room from the server, in ticks, and how many times it did.
        /// </summary>
        public long RoomStateTicks;

        /// <inheritdoc cref="RoomStateTicks"/>
        public int RoomStates;
    }

    /// <summary>
    /// The change of room that is watched, from its start to the fade in, or null.
    /// </summary>
    private static Change? _change;

    /// <summary>
    /// When the last change of room was written down, by <see cref="Time.realtimeSinceStartup"/>, for the state of the
    /// room that comes after it; or null.
    /// </summary>
    private static float? _lastFadeIn;

    /// <summary>
    /// When the last frame began, in ticks of <see cref="Stopwatch"/>, or 0.
    /// </summary>
    private static long _lastFrame;

    /// <summary>
    /// Whether watching the changes of room threw, after which they aren't watched anymore.
    /// </summary>
    private static bool _failed;

    /// <summary>
    /// Whether the memory the computer has free can't be asked for, which isn't tried again.
    /// </summary>
    private static bool _noFreeMemory;

    /// <summary>
    /// The fields behind Unity's scene events, whose handlers are called one at a time while a change is watched.
    /// </summary>
    private static FieldInfo? _sceneLoaded, _sceneUnloaded, _activeSceneChanged;

    /// <summary>
    /// The hooks, kept so that they stay in place.
    /// </summary>
    private static readonly List<Hook> Hooks = [];

    /// <summary>
    /// Starts watching the changes of room.
    /// </summary>
    public static void Initialize() {
        try {
            global::GameManager.SceneTransitionBegan += OnTransitionBegan;

            var gameManager = typeof(global::GameManager);
            Hooks.Add(new Hook(
                gameManager.GetMethod(nameof(global::GameManager.FadeSceneIn), Type.EmptyTypes),
                new Action<Action<global::GameManager>, global::GameManager>(OnFadeSceneIn)
            ));
            Hooks.Add(new Hook(
                gameManager.GetMethod(nameof(global::GameManager.FinishedEnteringScene), Type.EmptyTypes),
                new Action<Action<global::GameManager>, global::GameManager>(OnFinishedEnteringScene)
            ));

            // Unity calls the handlers of its scene events from these, all in one go; while a change is watched they
            // are called one at a time instead, in the same order, so that each one's time is known
            _sceneLoaded = HookSceneEvent(
                "sceneLoaded", "Internal_SceneLoaded",
                new Action<Action<Scene, LoadSceneMode>, Scene, LoadSceneMode>(OnSceneLoaded)
            );
            _sceneUnloaded = HookSceneEvent(
                "sceneUnloaded", "Internal_SceneUnloaded",
                new Action<Action<Scene>, Scene>(OnSceneUnloaded)
            );
            _activeSceneChanged = HookSceneEvent(
                "activeSceneChanged", "Internal_ActiveSceneChanged",
                new Action<Action<Scene, Scene>, Scene, Scene>(OnActiveSceneChanged)
            );
        } catch (Exception e) {
            _failed = true;
            Logger.Warn($"Changes of room can't be timed: {e}");
        }
    }

    /// <summary>
    /// Hooks the method of Unity that calls the handlers of a scene event, if it and the field behind the event are
    /// there as expected.
    /// </summary>
    /// <returns>The field behind the event, or null if the event isn't timed.</returns>
    private static FieldInfo? HookSceneEvent(string fieldName, string methodName, Delegate hook) {
        var field = typeof(SceneManager).GetField(fieldName, UnityFlags);
        var method = typeof(SceneManager).GetMethod(methodName, UnityFlags);
        if (field == null || method == null || !typeof(Delegate).IsAssignableFrom(field.FieldType)) {
            Logger.Warn($"The handlers of Unity's '{fieldName}' can't be timed in changes of room");
            return null;
        }

        Hooks.Add(new Hook(method, hook));
        return field;
    }

    /// <summary>
    /// Counts a frame in the change of room that is watched, if any, with the time that the mod's per-frame handlers
    /// took in it, and drops a change that has gone on for too long. Called at the start of each frame.
    /// </summary>
    /// <param name="now">When the frame began, in ticks of <see cref="Stopwatch"/>.</param>
    /// <param name="update">The handlers of each frame.</param>
    /// <param name="lateUpdate">The handlers of the last step of each frame.</param>
    /// <param name="fixedUpdate">The handlers of each step of physics.</param>
    public static void OnFrame(long now, TimedEvent update, TimedEvent lateUpdate, TimedEvent fixedUpdate) {
        var last = _lastFrame;
        _lastFrame = now;
        if (_change is not { } change || last == 0) {
            return;
        }

        if (Time.realtimeSinceStartup - change.Start > MaxChangeTime) {
            _change = null;
            return;
        }

        change.Frames++;
        change.Slowest = System.Math.Max(change.Slowest, (now - last) * TickMs);
        change.ModFrameTicks += FrameWatch.ModTicks(update, lateUpdate, fixedUpdate);
    }

    /// <summary>
    /// Counts the mod's taking of the state of the room from the server: in the change of room if it is still watched,
    /// or in a line of its own about how long after the fade in it came.
    /// </summary>
    /// <param name="ticks">The time it took, in ticks of <see cref="Stopwatch"/>.</param>
    /// <param name="players">How many other players were in the room.</param>
    /// <param name="creatures">How many creatures it told the state of.</param>
    public static void OnRoomState(long ticks, int players, int creatures) {
        if (_failed) {
            return;
        }

        if (_change is { } change) {
            change.RoomStateTicks += ticks;
            change.RoomStates++;
            return;
        }

        if (_lastFadeIn is { } fadeIn) {
            Logger.Info(
                $"[Load] {DateTime.Now:HH:mm:ss} The state of '{SceneUtil.GetCurrentSceneName()}' came " +
                $"{Time.realtimeSinceStartup - fadeIn:0.00} s after the fade in, with {players} other player(s) and " +
                $"{creatures} creature(s), and the mod took {ticks * TickMs:0.0} ms to take it"
            );
            _lastFadeIn = null;
        }
    }

    /// <summary>
    /// A change of room begins: the game starts fading out and fetching the next room.
    /// </summary>
    private static void OnTransitionBegan(SceneLoad sceneLoad) {
        if (_failed) {
            return;
        }

        try {
            // One that is still watched came to neither end, like a scene that a cutscene loaded, and is dropped
            var gameManager = global::GameManager.SilentInstance;
            _lastFadeIn = null;
            _change = new Change {
                From = SceneUtil.GetCurrentSceneName(),
                To = sceneLoad?.TargetSceneName ?? "?",
                Gate = gameManager != null ? gameManager.entryGateName : "",
                Load = sceneLoad
            };
        } catch (Exception e) {
            Fail(e);
        }
    }

    /// <summary>
    /// The game fades the new room in, which ends the change of room that is watched.
    /// </summary>
    private static void OnFadeSceneIn(Action<global::GameManager> orig, global::GameManager self) {
        // While the game waits for the room to be ready, a fade in is only noted down, for when it is
        // (GameManager.OnNextLevelReady fades in then)
        var deferred = global::GameManager.IsWaitingForSceneReady;
        var now = Time.realtimeSinceStartup;
        orig(self);

        if (deferred || _change is not { } change || _failed) {
            return;
        }

        try {
            Write(change, now, "the fade in");
        } catch (Exception e) {
            Fail(e);
        }
    }

    /// <summary>
    /// The player character finished coming into the new room. A change of room that is still watched ended without
    /// the game fading the room in, like coming back to life on a bench or after a hazard, which fade in some other
    /// way; on the way through a door the fade in comes first.
    /// </summary>
    private static void OnFinishedEnteringScene(Action<global::GameManager> orig, global::GameManager self) {
        var now = Time.realtimeSinceStartup;
        orig(self);

        if (_change is not { } change || _failed) {
            return;
        }

        try {
            Write(change, now, "the player character came in, with no fade in by the game manager");
        } catch (Exception e) {
            Fail(e);
        }
    }

    /// <summary>
    /// Unity calls the handlers of its event of a scene that loaded.
    /// </summary>
    private static void OnSceneLoaded(Action<Scene, LoadSceneMode> orig, Scene scene, LoadSceneMode mode) {
        if (HandlersToTime(_sceneLoaded) is { } handlers) {
            CallTimed(handlers, handler => ((UnityAction<Scene, LoadSceneMode>) handler)(scene, mode));
        } else {
            orig(scene, mode);
        }
    }

    /// <summary>
    /// Unity calls the handlers of its event of a scene that unloaded.
    /// </summary>
    private static void OnSceneUnloaded(Action<Scene> orig, Scene scene) {
        if (HandlersToTime(_sceneUnloaded) is { } handlers) {
            CallTimed(handlers, handler => ((UnityAction<Scene>) handler)(scene));
        } else {
            orig(scene);
        }
    }

    /// <summary>
    /// Unity calls the handlers of its event of another scene becoming the active one.
    /// </summary>
    private static void OnActiveSceneChanged(Action<Scene, Scene> orig, Scene previous, Scene next) {
        if (HandlersToTime(_activeSceneChanged) is { } handlers) {
            CallTimed(handlers, handler => ((UnityAction<Scene, Scene>) handler)(previous, next));
        } else {
            orig(previous, next);
        }
    }

    /// <summary>
    /// The handlers of one of Unity's scene events, to be called one at a time while a change of room is watched, in
    /// the same order as Unity calls them, so that each one's time is known; or null, and Unity calls them itself.
    /// Another mod's hook or patch on Unity's methods that call them, put in place before SSMP's, is passed over while
    /// a change is watched; none is known.
    /// </summary>
    /// <param name="field">The field behind the event.</param>
    private static Delegate[]? HandlersToTime(FieldInfo? field) {
        if (_failed || _change == null || field == null) {
            return null;
        }

        try {
            return (field.GetValue(null) as Delegate)?.GetInvocationList();
        } catch (Exception e) {
            Fail(e);
            return null;
        }
    }

    /// <summary>
    /// Calls the handlers of a scene event one at a time, counting each one's time. One that throws stops the rest and
    /// goes on to Unity, as it does when Unity calls them.
    /// </summary>
    /// <param name="handlers">The handlers.</param>
    /// <param name="call">Calls one handler.</param>
    private static void CallTimed(Delegate[] handlers, Action<Delegate> call) {
        foreach (var handler in handlers) {
            var start = Stopwatch.GetTimestamp();
            try {
                call(handler);
            } finally {
                CountEventHandler(handler, Stopwatch.GetTimestamp() - start);
            }
        }
    }

    /// <summary>
    /// Counts the time a handler of a scene event took in the change of room that is watched.
    /// </summary>
    private static void CountEventHandler(Delegate handler, long ticks) {
        if (_change is not { } change) {
            return;
        }

        try {
            if (handler.Method.DeclaringType?.Assembly != typeof(LoadWatch).Assembly) {
                change.OtherEventTicks += ticks;
                return;
            }

            var name = TimedEvent.Describe(handler);
            change.ModEventTicks.TryGetValue(name, out var before);
            change.ModEventTicks[name] = before + ticks;
        } catch (Exception e) {
            Fail(e);
        }
    }

    /// <summary>
    /// Writes a change of room down and stops watching it.
    /// </summary>
    /// <param name="change">The change.</param>
    /// <param name="end">When it ended, by <see cref="Time.realtimeSinceStartup"/>.</param>
    /// <param name="endName">What ended it.</param>
    private static void Write(Change change, float end, string endName) {
        _change = null;
        _lastFadeIn = Time.realtimeSinceStartup;

        // The parts of the game's loading that ran, from the start of the first to the end of the last
        var parts = new List<string>();
        float? loadStart = null, loadEnd = null;
        if (change.Load?.phaseInfos is { } infos) {
            foreach (SceneLoad.Phases phase in Enum.GetValues(typeof(SceneLoad.Phases))) {
                var index = (int) phase;
                if (index < 0 || index >= infos.Length || infos[index] is not { BeginTime: { } begin } info) {
                    continue;
                }

                loadStart = loadStart == null ? begin : System.Math.Min(loadStart.Value, begin);
                if (info.EndTime is { } phaseEnd) {
                    loadEnd = loadEnd == null ? phaseEnd : System.Math.Max(loadEnd.Value, phaseEnd);
                    parts.Add($"{phase} {phaseEnd - begin:0.00}");
                } else {
                    parts.Add($"{phase} unfinished");
                }
            }
        }

        var load = loadStart is { } first && loadEnd is { } last
            ? $"the game's loading {last - first:0.00} s ({string.Join(", ", parts)}), then {end - last:0.00} s to " +
              $"{endName}"
            : $"no loading timed by the game ({string.Join(", ", parts)})";

        var modEvents = change.ModEventTicks.Values.Sum() * TickMs;
        var slowestEvents = change.ModEventTicks
            .OrderByDescending(pair => pair.Value)
            .Where(pair => pair.Value * TickMs >= 0.5)
            .Take(5)
            .Select(pair => $"{pair.Key} {pair.Value * TickMs:0.0}");
        var freeAfter = GetFreeMemory();

        // No frame was counted only if the frames aren't watched (FrameWatch stopped after it threw)
        var frames = change.Frames > 0
            ? $"{change.Frames} frame(s), the slowest {change.Slowest:0} ms"
            : "frames not counted";
        Logger.Info(
            $"[Load] {DateTime.Now:HH:mm:ss} '{change.From}' -> '{change.To}' ({change.Gate}): " +
            $"{end - change.Start:0.00} s from the start to {endName}; {load}; {frames}, " +
            $"{GC.CollectionCount(0) - change.Collections} memory cleanup(s), " +
            $"{change.FreeMemory} -> {freeAfter} MB of the computer's memory free, the game using " +
            $"{GetGameMemory()} MB; the mod: scene events {modEvents:0.0} ms" +
            $"{(modEvents >= 0.5 ? $" ({string.Join(", ", slowestEvents)})" : "")}, per-frame handlers " +
            $"{change.ModFrameTicks * TickMs:0.0} ms, the room's state from the server " +
            (change.RoomStates > 0 ? $"{change.RoomStateTicks * TickMs:0.0} ms" : "not come yet") +
            $"; other handlers of scene events (the game's, other mods') {change.OtherEventTicks * TickMs:0.0} ms"
        );
    }

    /// <summary>
    /// Stops watching the changes of room after it threw.
    /// </summary>
    private static void Fail(Exception e) {
        _failed = true;
        _change = null;
        Logger.Error($"Watching the changes of room threw, and they are no longer watched:\n{e}");
    }

    /// <summary>
    /// The memory the game uses, in MB, or -1 if that can't be known.
    /// </summary>
    private static long GetGameMemory() {
        try {
            using var process = Process.GetCurrentProcess();
            return process.WorkingSet64 / (1024 * 1024);
        } catch (Exception) {
            return -1;
        }
    }

    /// <summary>
    /// The memory the computer has free, in MB, or -1 if that can't be known: Windows only.
    /// </summary>
    private static long GetFreeMemory() {
        if (_noFreeMemory) {
            return -1;
        }

        try {
            var status = new MemoryStatus { Length = (uint) Marshal.SizeOf<MemoryStatus>() };
            return GlobalMemoryStatusEx(ref status) ? (long) (status.AvailablePhysical / (1024 * 1024)) : -1;
        } catch (Exception) {
            _noFreeMemory = true;
            return -1;
        }
    }

    /// <summary>
    /// What Windows says of the memory of the computer (MEMORYSTATUSEX).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
