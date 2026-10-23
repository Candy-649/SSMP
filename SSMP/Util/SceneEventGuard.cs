using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using MonoMod.RuntimeDetour;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Util;

/// <summary>
/// Calls the handlers of Unity's scene events one at a time, in the order Unity calls them, so that one that throws
/// ends only its own run: it is written to the log, and the handlers after it run as ever, as with
/// <see cref="TimedEvent"/>. Unity calls them all in one go and stops at the first that throws, which left every
/// handler after it out of that change of room, the game's and other mods' as well as the mod's, with nothing of it in
/// the log that a player's game keeps, as Unity's own messages are not written there. A creature that could not be
/// made as one player's game came into its room first left out the handler that tells the server that the player left
/// the room they came from, and the other player, still in that room, saw them stand in its door, walking on the spot,
/// until they came back (USER 10-23: "我会看到她先离开然后又在门口原地踏步"). The time each handler takes goes to
/// <see cref="LoadWatch"/>. Another mod's hook on Unity's methods that call the handlers, put in place before this
/// one, is passed over; none is known.
/// </summary>
internal static class SceneEventGuard {
    /// <summary>
    /// The binding flags of Unity's own methods that call the handlers of its scene events, and of their fields.
    /// </summary>
    private const BindingFlags UnityFlags = BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>
    /// Unity's scene events.
    /// </summary>
    private enum SceneEvent {
        Loaded,
        Unloaded,
        ActiveChanged
    }

    /// <summary>
    /// The fields behind Unity's scene events, or null for an event whose handlers Unity calls itself.
    /// </summary>
    private static FieldInfo? _sceneLoaded, _sceneUnloaded, _activeSceneChanged;

    /// <summary>
    /// The hooks, kept so that they stay in place.
    /// </summary>
    private static readonly List<Hook> Hooks = [];

    /// <summary>
    /// How many times each handler threw, by its name.
    /// </summary>
    private static readonly Dictionary<string, int> Failures = new();

    /// <summary>
    /// Whether reading the handlers of an event threw, which is written down once.
    /// </summary>
    private static bool _saidUnreadable;

    /// <summary>
    /// Takes over the calling of the handlers of Unity's scene events.
    /// </summary>
    public static void Initialize() {
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
    }

    /// <summary>
    /// Hooks the method of Unity that calls the handlers of a scene event, if it and the field behind the event are
    /// there as expected.
    /// </summary>
    /// <returns>The field behind the event, or null if Unity calls its handlers itself.</returns>
    private static FieldInfo? HookSceneEvent(string fieldName, string methodName, Delegate hook) {
        try {
            var field = typeof(SceneManager).GetField(fieldName, UnityFlags);
            var method = typeof(SceneManager).GetMethod(methodName, UnityFlags);
            if (field != null && method != null && typeof(Delegate).IsAssignableFrom(field.FieldType)) {
                Hooks.Add(new Hook(method, hook));
                return field;
            }

            Logger.Warn($"Unity calls the handlers of its '{fieldName}' itself, and one that throws stops the rest");
        } catch (Exception e) {
            Logger.Warn(
                $"Unity calls the handlers of its '{fieldName}' itself, and one that throws stops the rest:\n{e}"
            );
        }

        return null;
    }

    /// <summary>
    /// Unity calls the handlers of its event of a scene that loaded.
    /// </summary>
    private static void OnSceneLoaded(Action<Scene, LoadSceneMode> orig, Scene scene, LoadSceneMode mode) {
        if (HandlersOf(_sceneLoaded) is { } handlers) {
            CallEach(
                handlers, SceneEvent.Loaded, scene, default,
                handler => ((UnityAction<Scene, LoadSceneMode>) handler)(scene, mode)
            );
        } else {
            orig(scene, mode);
        }
    }

    /// <summary>
    /// Unity calls the handlers of its event of a scene that unloaded.
    /// </summary>
    private static void OnSceneUnloaded(Action<Scene> orig, Scene scene) {
        if (HandlersOf(_sceneUnloaded) is { } handlers) {
            CallEach(handlers, SceneEvent.Unloaded, scene, default, handler => ((UnityAction<Scene>) handler)(scene));
        } else {
            orig(scene);
        }
    }

    /// <summary>
    /// Unity calls the handlers of its event of another scene becoming the active one.
    /// </summary>
    private static void OnActiveSceneChanged(Action<Scene, Scene> orig, Scene previous, Scene next) {
        if (HandlersOf(_activeSceneChanged) is { } handlers) {
            CallEach(
                handlers, SceneEvent.ActiveChanged, previous, next,
                handler => ((UnityAction<Scene, Scene>) handler)(previous, next)
            );
        } else {
            orig(previous, next);
        }
    }

    /// <summary>
    /// The handlers of one of Unity's scene events as they are now, in the order Unity calls them; or null, and Unity
    /// calls them itself.
    /// </summary>
    /// <param name="field">The field behind the event.</param>
    private static Delegate[]? HandlersOf(FieldInfo? field) {
        if (field == null) {
            return null;
        }

        try {
            return (field.GetValue(null) as Delegate)?.GetInvocationList() ?? [];
        } catch (Exception e) {
            if (!_saidUnreadable) {
                _saidUnreadable = true;
                Logger.Warn($"The handlers of '{field.Name}' could not be read, and Unity calls them itself:\n{e}");
            }

            return null;
        }
    }

    /// <summary>
    /// Calls the handlers of a scene event one at a time. One that throws is written to the log, and the ones after it
    /// are called as ever.
    /// </summary>
    /// <param name="handlers">The handlers.</param>
    /// <param name="sceneEvent">The event, for the log.</param>
    /// <param name="scene">The scene of the event, or the scene that was active before, for the log.</param>
    /// <param name="next">The scene that is active now, for <see cref="SceneEvent.ActiveChanged"/>.</param>
    /// <param name="call">Calls one handler.</param>
    private static void CallEach(
        Delegate[] handlers,
        SceneEvent sceneEvent,
        Scene scene,
        Scene next,
        Action<Delegate> call
    ) {
        foreach (var handler in handlers) {
            var start = Stopwatch.GetTimestamp();
            try {
                call(handler);
            } catch (Exception e) {
                SayItFailed(handler, sceneEvent, scene, next, e);
            }

            LoadWatch.CountSceneEventHandler(handler, Stopwatch.GetTimestamp() - start);
        }
    }

    /// <summary>
    /// Writes down that a handler of a scene event threw: the first time with all of it, and after that in a line.
    /// </summary>
    private static void SayItFailed(Delegate handler, SceneEvent sceneEvent, Scene scene, Scene next, Exception e) {
        try {
            var name = TimedEvent.Describe(handler);
            Failures.TryGetValue(name, out var before);
            Failures[name] = before + 1;

            var when = sceneEvent switch {
                SceneEvent.Loaded => $"'{scene.name}' is loaded",
                SceneEvent.Unloaded => $"'{scene.name}' is unloaded",
                _ => $"the room changes from '{scene.name}' to '{next.name}'"
            };

            if (before == 0) {
                Logger.Error($"'{name}', which runs when {when}, failed; what runs after it still does:\n{e}");
            } else {
                Logger.Error(
                    $"'{name}', which runs when {when}, failed again, {before + 1} times now: " +
                    $"{e.GetType().Name}: {e.Message}"
                );
            }
        } catch (Exception inner) {
            Logger.Error($"A handler of a scene event failed, and saying so failed too:\n{e}\n{inner}");
        }
    }
}
