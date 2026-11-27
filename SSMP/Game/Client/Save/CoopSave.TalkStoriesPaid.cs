using System;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// What the local player handed over for the story scenes that a talk sets off, which wake only once every player has
/// handed over their own share (see BossRoomCoop.TalkStories): kept in the marker of the co-op save, as the scene may
/// wait for a player who comes another time, and nothing in the game's save tells a player who handed theirs over from
/// one who never had it.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// Whether the local player handed over their share for a story scene in the loaded co-op save.
    /// </summary>
    /// <param name="story">The scene and path of the story scene.</param>
    public bool IsTalkStoryPaid(string story) {
        return GetCurrentMarker() is { } marker && marker.TalkStoriesPaid.Contains(story);
    }

    /// <summary>
    /// Notes in the loaded co-op save that the local player handed over their share for a story scene.
    /// </summary>
    /// <param name="story">The scene and path of the story scene.</param>
    public void MarkTalkStoryPaid(string story) {
        if (GetCurrentMarker() is not { } marker || marker.TalkStoriesPaid.Contains(story)) {
            return;
        }

        marker.TalkStoriesPaid.Add(story);
        Logger.Info($"The local player handed over their share for the story scene '{story}' in this save");
        try {
            SaveMarkers();
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }
}
