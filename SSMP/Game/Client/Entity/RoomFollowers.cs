using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// Things of a room that follow a point of a creature, like the long hair of a boss that follows its head. They follow
/// the room's own creature, which sleeps in the game that does not run it while that game shows its copy: the hair
/// stayed where the boss first lay, under the floor, for the whole fight (USER 11-07 "我这边看不到…那个长长的头发"). The
/// game's follower goes by the first of its points that is on, so after each point of a creature that it
/// follows comes the same point of the copy: the copy's is followed while the room's own creature sleeps, and the
/// room's own again in the game that takes the creature over, where the copy is put away.
/// </summary>
internal static class RoomFollowers {
    /// <summary>
    /// The binding flags of the follower's fields.
    /// </summary>
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// The point a follower follows first.
    /// </summary>
    private static readonly FieldInfo TargetField = typeof(FollowTransform).GetField("target", Flags)!;

    /// <summary>
    /// The points a follower follows when its first is off, in order.
    /// </summary>
    private static readonly FieldInfo FallbacksField = typeof(FollowTransform).GetField("fallbackTargets", Flags)!;

    /// <summary>
    /// Has every follower of a scene that follows a point of a creature follow the same point of its copy next.
    /// </summary>
    /// <param name="scene">The scene whose creatures were just made entities.</param>
    public static void FollowCopies(Scene scene) {
        foreach (var follower in Object.FindObjectsByType<FollowTransform>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None
                 )) {
            if (follower.gameObject.scene != scene) {
                continue;
            }

            var target = (Transform?) TargetField.GetValue(follower);
            var fallbacks = (Transform[]?) FallbacksField.GetValue(follower) ?? [];
            List<Transform>? points = null;
            if (target != null && Entity.CopyOfRoomPart(target) is { } targetCopy) {
                points = [targetCopy];
            }

            for (var i = 0; i < fallbacks.Length; i++) {
                if (fallbacks[i] == null || Entity.CopyOfRoomPart(fallbacks[i]) is not { } copy) {
                    points?.Add(fallbacks[i]);
                    continue;
                }

                points ??= [..fallbacks[..i]];
                points.Add(fallbacks[i]);
                points.Add(copy);
            }

            if (points == null) {
                continue;
            }

            FallbacksField.SetValue(follower, points.ToArray());
            Logger.Info(
                $"[Copies] '{follower.name}' follows the copy of the creature it follows while the room's own sleeps: " +
                $"{string.Join(", ", points.ConvertAll(point => point == null ? "-" : $"{point.root.name}/{point.name}"))}"
            );
        }
    }
}
