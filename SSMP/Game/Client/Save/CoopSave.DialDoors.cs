using System;
using System.Collections.Generic;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// How the dial stands that levers turn a way through a room with, in a checked two-player save (DialDoorBridge, one in
/// the game). A lever hit turns the dial from one way through to the other, and its item saves which of the two it
/// stands in. Each game turns its own dial for a lever hit of either player (CoopHits replays the partner's), but the
/// changes of the world that reach the partner's save only go from not set to set, and only once: a dial turned while
/// the partner was in another room stood where it had been in their save, they walked into a room whose dial stood
/// the other way, and from then on every lever turned both dials and kept them apart (USER 10-17,
/// "应该是我进房间之前她转了一次，然后我们的状态就不一样了"). So, as for the platforms that slide along their rails (see
/// CoopSave.SlidePlatforms), each time a dial comes to rest, the game that runs the room says how it stands, and with
/// the partner in another room the game it turned in says so, for the partner's save. The changes of the world leave
/// dials out: they only go from not set to set, and the partner's game locked a loaded dial to save as turned whichever
/// way it then stood. When two saves are paired, a dial turned in either is still turned in both, and a loaded one is
/// put that way (see <see cref="PutDial"/>).
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// Hook for a dial being set up, which has it say when it comes to rest.
    /// </summary>
    private Hook? _dialAwakeHook;

    /// <summary>
    /// How the game that runs the room said a dial came to rest while this game's own was still turning, by the dial,
    /// to be put that way once it stops.
    /// </summary>
    private readonly Dictionary<DialDoorBridge, bool> _dialRestsHeld = new();

    /// <summary>
    /// The keys of the dials that were loaded here or that the partner said came to rest, which the changes of the
    /// world that the save shows leave out.
    /// </summary>
    private readonly HashSet<string> _dialKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether telling the partner how a dial came to rest threw, which is only logged once.
    /// </summary>
    private bool _dialRestFailed;

    /// <summary>
    /// Registers the hook on dials being set up.
    /// </summary>
    private void RegisterDialDoorHooks() {
        _dialAwakeHook = CreateHook(
            typeof(DialDoorBridge).GetMethod("Awake", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<DialDoorBridge>, DialDoorBridge>(OnDialAwake)
        );
    }

    /// <summary>
    /// Has a dial say when it comes to rest, by the event that it fires itself as its doors open again at the end of a
    /// turn.
    /// </summary>
    private void OnDialAwake(Action<DialDoorBridge> orig, DialDoorBridge self) {
        orig(self);
        self.OnDoorsOpen?.AddListener(() => OnDialRest(self));
    }

    /// <summary>
    /// Tells the partner how a dial came to rest: with the partner in this room, only from the game that runs it, which
    /// the other game's dial follows; with the partner elsewhere, from this game, for the partner's save. A dial that
    /// the game running the room said how to stop while it was still turning here is put that way.
    /// </summary>
    private void OnDialRest(DialDoorBridge self) {
        try {
            if (_dialRestsHeld.TryGetValue(self, out var held)) {
                _dialRestsHeld.Remove(self);
                if (held != self.isRotated) {
                    self.SetInitialRotation(held);
                }

                return;
            }

            // With a member in the room, the game that runs the room says how its dial came to rest
            if (_checkedMembers.Count == 0 || self.persistent == null ||
                IsMemberInRoom() && IsSceneHost?.Invoke() != true) {
                return;
            }

            GetItemSceneAndId(self.persistent, out var scene, out var id);
            var update = new CoopSaveUpdate { TargetId = CoopTargets.Everyone, Kind = CoopSaveUpdateKind.DialRest };
            update.ItemScenes.Add(scene);
            update.ItemIds.Add(id);
            update.Amounts.Add(self.isRotated ? 1 : 0);
            Send(update);
        } catch (Exception e) {
            if (!_dialRestFailed) {
                _dialRestFailed = true;
                Logger.Error($"Could not tell the partner how '{self.name}' came to rest:\n{e}");
            }
        }
    }

    /// <summary>
    /// Keeps a dial the way it came to rest in the partner's game, in this save and, where this game has it loaded, on
    /// the dial here - unless this game runs the room with the partner in it, whose dial follows this one.
    /// </summary>
    private void OnDialRest(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker) || !IsFromMemberInSave(player) ||
            SceneData.instance is not { } sceneData || update.ItemScenes.Count < 1 || update.ItemIds.Count < 1 ||
            update.Amounts.Count < 1) {
            return;
        }

        var scene = update.ItemScenes[0];
        var id = update.ItemIds[0];
        var rotated = update.Amounts[0] != 0;
        _dialKeys.Add(GetItemKey(scene, id));
        if (player.IsInLocalScene && IsSceneHost?.Invoke() == true) {
            return;
        }

        sceneData.PersistentBools.SetValue(new PersistentItemData<bool> {
            ID = id,
            SceneName = scene,
            Value = rotated,
            IsSemiPersistent = false
        });

        foreach (var dial in UnityEngine.Object.FindObjectsByType<DialDoorBridge>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None
                 )) {
            if (dial.persistent == null) {
                continue;
            }

            GetItemSceneAndId(dial.persistent, out var dialScene, out var dialId);
            if (dialId != id || !string.Equals(dialScene, scene, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            PutDial(dial, rotated);
        }

        Logger.Info(
            $"'{id}' in {scene} came to rest {(rotated ? "turned" : "not turned")} in the game of {player.Username}"
        );
    }

    /// <summary>
    /// Puts a loaded dial the way the partner's game or the paired saves have it: at once if it stands still, or once
    /// it comes to rest if it still turns. It then saves that way itself, as it would after a turn.
    /// </summary>
    private void PutDial(DialDoorBridge dial, bool rotated) {
        if (dial.rotateRoutine == null) {
            if (dial.isRotated != rotated) {
                dial.SetInitialRotation(rotated);
            }

            return;
        }

        // Those of dials that went with their room while they turned are let go here
        var gone = new List<DialDoorBridge>();
        foreach (var held in _dialRestsHeld.Keys) {
            if (held == null) {
                gone.Add(held!);
            }
        }

        foreach (var held in gone) {
            _dialRestsHeld.Remove(held);
        }

        _dialRestsHeld[dial] = rotated;
    }
}
