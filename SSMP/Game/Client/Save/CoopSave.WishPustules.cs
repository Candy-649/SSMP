using System;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Gives a two-player save back the pustules it lost to the partner's draws.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The wish that the pustules give their samples for.
    /// </summary>
    private const string PustuleWishName = "Extractor Blue";

    /// <summary>
    /// The sample that drawing a pustule gives, one each time.
    /// </summary>
    private const string PustuleSampleName = "Plasmium";

    /// <summary>
    /// The pustules that can be drawn before the wish is done, one in each of three rooms. The rooms swap them for
    /// scenery once the wish is done (BlueScientistSceneryPustulesGrown), and every other pustule only grows after that.
    /// </summary>
    private static readonly (string Scene, string Id)[] WishPustules = [
        ("Crawl_03", "pustule_set_small (1)"),
        ("Crawl_07", "pustule_set_small (1)"),
        ("Crawl_09", "pustule_set_small (1)")
    ];

    /// <summary>
    /// Whether growing back the pustules failed once, which is said once.
    /// </summary>
    private bool _growingBackPustulesFailed;

    /// <summary>
    /// Grows back the pustules that this save shows drawn only because the partner drew them.
    ///
    /// Up to 0.4.65 a pustule being drawn went to the partner's save like an opened wall, while the sample went only
    /// into the bag of the one who drew it. The wish takes a full set of samples from each player, and before it is
    /// done these three are all there is, so a single pustule lost that way kept both players from ever turning it in.
    /// Each draw gives one sample and nothing but the wish takes them, so a save that shows more of them drawn than it
    /// holds samples lost that many to the partner, and gets them back. One in a room that is loaded right now is left
    /// for later: the pustule there would put its own "drawn" back as the room is left.
    /// </summary>
    private void GrowBackPustulesThePartnerDrew() {
        try {
            var playerData = PlayerData.instance;
            var sceneData = SceneData.instance;
            if (GetCurrentMarker() == null || playerData == null || sceneData == null ||
                playerData.BlueScientistSceneryPustulesGrown || CollectableItemManager.IsInHiddenMode()) {
                return;
            }

            var wish = FindQuest(PustuleWishName);
            var sample = CollectableItemManager.GetItemByName(PustuleSampleName);
            if (wish == null || wish.IsCompleted || sample == null) {
                return;
            }

            var lost = -sample.CollectedAmount;
            foreach (var (scene, id) in WishPustules) {
                if (sceneData.PersistentBools.TryGetValue(scene, id, out var drawn) && drawn.Value) {
                    lost++;
                }
            }

            foreach (var (scene, id) in WishPustules) {
                if (lost <= 0) {
                    return;
                }

                if (!sceneData.PersistentBools.TryGetValue(scene, id, out var drawn) || !drawn.Value ||
                    SceneManager.GetSceneByName(scene).isLoaded) {
                    continue;
                }

                sceneData.PersistentBools.SetValue(new PersistentItemData<bool> {
                    ID = id,
                    SceneName = scene,
                    Value = false,
                    IsSemiPersistent = drawn.IsSemiPersistent
                });
                lost--;

                Logger.Info(
                    $"Grew back the pustule '{id}' in {scene}: this save showed more of them drawn than it holds " +
                    "samples, the rest having gone to the partner"
                );
            }
        } catch (Exception e) {
            if (!_growingBackPustulesFailed) {
                _growingBackPustulesFailed = true;
                Logger.Warn($"Could not grow back the pustules the partner drew: {e}");
            }
        }
    }
}
