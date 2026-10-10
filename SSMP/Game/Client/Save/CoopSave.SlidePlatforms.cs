using System;
using System.Collections.Generic;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Where the platforms that slide along their rails when struck stand, in a checked two-player save. Each game moves its
/// own platform for a hit of either player (CoopHits.OnSendHitInDirection) and saves where its own came to rest. A
/// platform struck while the partner was in another room stood where it had been in the partner's save, and two hits a
/// moment apart, each refused by the other game's platform while it was still sliding, left the two games' platforms at
/// different ends (USER 10-10, "有个打一下会随着轨道滑动过去的方块没有同步"). So each time a platform comes to rest, the
/// game that runs the room says where, and with the partner in another room the game it slid in says so, for the
/// partner's save.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// Hook for a sliding platform coming to rest.
    /// </summary>
    private Hook? _slideEndHook;

    /// <summary>
    /// Where the game that runs the room said a platform came to rest while this game's own was still on its way, by
    /// the platform, to be put there once it stops.
    /// </summary>
    private readonly Dictionary<HitSlidePlatform, int> _slideEndsHeld = new();

    /// <summary>
    /// Whether telling the partner where a platform came to rest threw, which is only logged once.
    /// </summary>
    private bool _slideEndFailed;

    /// <summary>
    /// Registers the hook on sliding platforms coming to rest.
    /// </summary>
    private void RegisterSlidePlatformHooks() {
        _slideEndHook = CreateHook(
            typeof(HitSlidePlatform).GetMethod(nameof(HitSlidePlatform.OnMoveEnd), InstanceFlags, null, [], null),
            new Action<Action<HitSlidePlatform>, HitSlidePlatform>(OnSlideEnd)
        );
    }

    /// <summary>
    /// Tells the partner where a platform came to rest: with the partner in this room, only from the game that runs it,
    /// which the other game's platform follows; with the partner elsewhere, from this game, for the partner's save. A
    /// platform that the game running the room said where to stop while it was still sliding here is put there.
    /// </summary>
    private void OnSlideEnd(Action<HitSlidePlatform> orig, HitSlidePlatform self) {
        orig(self);

        try {
            if (_slideEndsHeld.TryGetValue(self, out var held)) {
                _slideEndsHeld.Remove(self);
                if (held != self.currentNodeIndex) {
                    PutSlideAt(self, held);
                }

                return;
            }

            if (_checkedWith is not { } partnerId || !_playerData.TryGetValue(partnerId, out var partner) ||
                self.persistent == null || partner.IsInLocalScene && IsSceneHost?.Invoke() != true) {
                return;
            }

            GetItemSceneAndId(self.persistent, out var scene, out var id);
            var update = new CoopSaveUpdate { TargetId = partner.Id, Kind = CoopSaveUpdateKind.SlidePlatform };
            update.ItemScenes.Add(scene);
            update.ItemIds.Add(id);
            update.Amounts.Add(self.currentNodeIndex);
            update.Amounts.Add(self.persistent.ItemData.IsSemiPersistent ? 1 : 0);
            Send(update);
        } catch (Exception e) {
            if (!_slideEndFailed) {
                _slideEndFailed = true;
                Logger.Error($"Could not tell the partner where '{self.name}' came to rest:\n{e}");
            }
        }
    }

    /// <summary>
    /// Keeps a platform where it came to rest in the partner's game, in this save and, where this game has it loaded,
    /// on the platform here - unless this game runs the room with the partner in it, whose platform follows this one.
    /// </summary>
    private void OnSlidePlatform(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker) ||
            SceneData.instance is not { } sceneData || update.ItemScenes.Count < 1 || update.ItemIds.Count < 1 ||
            update.Amounts.Count < 2) {
            return;
        }

        var scene = update.ItemScenes[0];
        var id = update.ItemIds[0];
        var node = update.Amounts[0];
        if (player.IsInLocalScene && IsSceneHost?.Invoke() == true) {
            return;
        }

        sceneData.PersistentInts.SetValue(new PersistentItemData<int> {
            ID = id,
            SceneName = scene,
            Value = node,
            IsSemiPersistent = update.Amounts[1] != 0
        });

        foreach (var platform in UnityEngine.Object.FindObjectsByType<HitSlidePlatform>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None
                 )) {
            if (platform.persistent == null) {
                continue;
            }

            GetItemSceneAndId(platform.persistent, out var platformScene, out var platformId);
            if (platformId != id || !string.Equals(platformScene, scene, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (platform.moveRoutine != null) {
                // Those of platforms that went with their room while they slid are let go here
                var gone = new List<HitSlidePlatform>();
                foreach (var held in _slideEndsHeld.Keys) {
                    if (held == null) {
                        gone.Add(held!);
                    }
                }

                foreach (var held in gone) {
                    _slideEndsHeld.Remove(held);
                }

                _slideEndsHeld[platform] = node;
            } else if (platform.currentNodeIndex != node) {
                PutSlideAt(platform, node);
            }
        }

        Logger.Info($"'{id}' in {scene} came to rest at {node} in the game of {player.Username}");
    }

    /// <summary>
    /// Puts a platform at a place on its rails, as its item does with a saved one as the room loads.
    /// </summary>
    private static void PutSlideAt(HitSlidePlatform platform, int node) {
        if (node >= platform.nodes.Count) {
            return;
        }

        if (node < 0 && platform.initialNode != null) {
            platform.SetAtNode(platform.initialNode);
            platform.currentNodeIndex = -1;
        } else {
            platform.SetAtNode(node);
        }
    }

    /// <summary>
    /// Gets the scene and the ID that a saved number is saved under.
    /// </summary>
    private static void GetItemSceneAndId(PersistentIntItem item, out string scene, out string id) {
        var data = item.ItemData;
        id = string.IsNullOrEmpty(data?.ID) ? item.gameObject.name : data!.ID;
        scene = string.IsNullOrEmpty(data?.SceneName) ? item.gameObject.scene.name : data!.SceneName;
    }
}
