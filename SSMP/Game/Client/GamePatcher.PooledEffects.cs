using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Pooled effects that the pool took back but couldn't switch off. An effect that hangs on an object, like the daze
/// ring that a stunned creature carries, takes itself back to the pool when that object is switched off. The pool then
/// can't move it yet, since Unity lets nothing change a hierarchy while it is being switched off, so it moves it at the
/// end of the frame; and it switches it off right away, which Unity doesn't let happen either at that moment. The pool
/// then counts the effect as taken back while it is still on, so once it has been moved it shows again, and nothing
/// ever takes it back again: taking back what was taken back already does nothing. The next time the pool hands that
/// effect out, it moves it to the new place and leaves it on.
///
/// The game itself hardly ever switches off a creature with a ring on it. The mod switches copies off whenever their
/// game changes, as when this game takes a creature over, and copies carry their rings since the copy's replays put
/// them on it (they used to stay loose where they appeared). So here the pool's own switch-off is finished once Unity
/// allows it.
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// The binding flags of the pool's private static fields.
    /// </summary>
    private const BindingFlags PoolStaticFlags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    /// <summary>
    /// The objects that the pool took back since the scene began, whichever were handed out again since.
    /// </summary>
    private static readonly FieldInfo? PoolRecentlyRecycledField =
        typeof(ObjectPool).GetField("recentlyRecycled", PoolStaticFlags);

    /// <summary>
    /// The objects taken back that the pool still has to move under itself, which it does at the end of the frame.
    /// </summary>
    private static readonly FieldInfo? PoolReparentListField = typeof(ObjectPool).GetField("reparentList", PoolStaticFlags);

    /// <summary>
    /// How many frames of the call stack the log keeps of what switched off the object that an effect hung on.
    /// </summary>
    private const int StuckEffectStackFrames = 14;

    /// <summary>
    /// The effects that the pool took back while they were still on, which are switched off at the end of the frame
    /// unless they were handed out again meanwhile, with what they hung on and what switched that off.
    /// </summary>
    private static readonly Dictionary<GameObject, string> StuckEffects = new();

    /// <summary>
    /// The names of the effects that were found still on after the pool took them back, which are said once each.
    /// </summary>
    private static readonly HashSet<string> ToldStuckEffects = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether finishing the pool's switch-off threw, which is said once.
    /// </summary>
    private static bool _stuckEffectFailed;

    /// <summary>
    /// Registers the hooks that finish switching off what the pool took back.
    /// </summary>
    private void RegisterPooledEffectHooks() {
        if (TryCreateHook(
                typeof(ObjectPool),
                nameof(ObjectPool.Recycle),
                new Action<Action<GameObject>, GameObject>(OnObjectPoolRecycle),
                StaticNonPublicPublicFlags,
                typeof(GameObject)
            ) is { } recycleHook) {
            _hooks.Add(recycleHook);
        }

        AddHook(typeof(ObjectPool), "LateUpdate", new Action<Action<ObjectPool>, ObjectPool>(OnObjectPoolLateUpdate));
    }

    /// <summary>
    /// Hook for <see cref="ObjectPool.Recycle(GameObject)"/>: an object that is still on after the pool took it back
    /// is switched off at the end of the frame, once nothing is being switched off any more.
    /// </summary>
    private static void OnObjectPoolRecycle(Action<GameObject> orig, GameObject obj) {
        orig(obj);

        try {
            if (obj == null || !obj.activeSelf || StuckEffects.ContainsKey(obj) || !IsTakenBack(obj) ||
                obj.GetComponent<ActiveRecycler>() != null) {
                return;
            }

            var parent = obj.transform.parent;
            var hungOn = parent != null ? ScenePath.Get(parent) : "nothing";

            // What switched off the object that it hung on is only kept for the first one of each effect, which is
            // the one that is logged
            StuckEffects[obj] = ToldStuckEffects.Contains(obj.name)
                ? hungOn
                : hungOn + (IsAwaitingReparent(obj)
                    ? " while that was being switched off, by:\n" + string.Join(
                        "\n", new StackTrace(1, false).ToString().Split('\n').Take(StuckEffectStackFrames)
                    )
                    : ", but it was on again after the pool had taken it back before");
        } catch (Exception e) {
            LogStuckEffectError(e);
        }
    }

    /// <summary>
    /// Hook for ObjectPool.LateUpdate, which moves what the pool took back under itself: what is still on after that
    /// is switched off, unless the pool handed it out again meanwhile. One that the pool couldn't move yet waits for
    /// the next frame.
    /// </summary>
    private static void OnObjectPoolLateUpdate(Action<ObjectPool> orig, ObjectPool self) {
        orig(self);
        if (StuckEffects.Count == 0) {
            return;
        }

        try {
            // A copy, because switching one off takes back what hangs on it in turn, which can add to the list
            foreach (var pair in StuckEffects.ToList()) {
                var obj = pair.Key;
                if (obj != null && IsAwaitingReparent(obj)) {
                    continue;
                }

                StuckEffects.Remove(obj!);
                if (obj == null || !obj.activeSelf || !IsTakenBack(obj)) {
                    continue;
                }

                if (ToldStuckEffects.Add(obj.name)) {
                    Logger.Info(
                        $"Switched off the pooled '{obj.name}', which was still on after the pool had taken it back. " +
                        $"It hung on {pair.Value}"
                    );
                }

                obj.SetActive(false);
            }
        } catch (Exception e) {
            StuckEffects.Clear();
            LogStuckEffectError(e);
        }
    }

    /// <summary>
    /// Whether the pool took an object back and hasn't handed it out again since.
    /// </summary>
    private static bool IsTakenBack(GameObject obj) {
        return ObjectPool.instance != null && !ObjectPool.IsSpawned(obj) &&
               PoolRecentlyRecycledField?.GetValue(null) is HashSet<GameObject> recycled && recycled.Contains(obj);
    }

    /// <summary>
    /// Whether the pool still has to move an object that it took back under itself, which it couldn't while the object
    /// that it hung on was being switched off.
    /// </summary>
    private static bool IsAwaitingReparent(GameObject obj) {
        return PoolReparentListField?.GetValue(null) is List<GameObject> reparent && reparent.Contains(obj);
    }

    /// <summary>
    /// Logs the first error of finishing the pool's switch-off.
    /// </summary>
    private static void LogStuckEffectError(Exception e) {
        if (!_stuckEffectFailed) {
            _stuckEffectFailed = true;
            Logger.Error($"Could not switch off a pooled effect that was still on after the pool took it back:\n{e}");
        }
    }
}
