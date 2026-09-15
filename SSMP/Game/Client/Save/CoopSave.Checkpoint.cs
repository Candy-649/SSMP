using System.Collections.Generic;
using System.Linq;
using GlobalEnums;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Boss checkpoints of two-player saves. When a boss fight starts with both players there, each game saves and
/// remembers the door that its player came into the scene through. If a player drops out while the fight lasts, it ends
/// for both: the player who stays leaves through that door and the boss waits for both players again, and the player
/// who dropped out goes back to that door once they are back in the save. Dying in the fight stays like it is in the
/// game. The checkpoint ends when the boss is beaten or the player leaves its scene another way.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The name of the scene that the game loads on its way to the main menu.
    /// </summary>
    private const string QuitToMenuSceneName = "Quit_To_Menu";

    /// <summary>
    /// How often, in seconds, a checkpoint checks whether its boss was beaten.
    /// </summary>
    private const float DefeatCheckInterval = 1f;

    /// <summary>
    /// How long, in seconds, the first scene change after a move for a checkpoint counts as that move.
    /// </summary>
    private const float CheckpointMoveTime = 30f;

    /// <summary>
    /// How long, in seconds, a move for a checkpoint that the game dropped waits before it is tried again.
    /// </summary>
    private const float MoveRetryInterval = 0.5f;

    /// <summary>
    /// The scene in which a boss fight started with both players since the local player came into it, or null.
    /// </summary>
    private string? _fightStartedScene;

    /// <summary>
    /// The scene of a boss fight that started, while the game waits to save for it until the intro of the fight lets
    /// the player pause again, or null.
    /// </summary>
    private string? _fightSaveScene;

    /// <summary>
    /// Whether the partner dropped out of the boss fight of the loaded save, so the local player leaves it.
    /// </summary>
    private bool _interruptPending;

    /// <summary>
    /// Whether the save loaded while its boss fight lasted, so the player goes back to its door once the partner is in.
    /// </summary>
    private bool _loadedWithCheckpoint;

    /// <summary>
    /// Whether the player heard that getting up takes them back to the door of their boss fight.
    /// </summary>
    private bool _checkpointMoveNoticed;

    /// <summary>
    /// When the local player was last moved for a checkpoint, in unscaled seconds, or -1.
    /// </summary>
    private float _checkpointMoveTime = -1f;

    /// <summary>
    /// When a move for a checkpoint that the game dropped is tried again, in unscaled seconds.
    /// </summary>
    private float _nextMoveTryTime;

    /// <summary>
    /// When a checkpoint next checks whether its boss was beaten, in unscaled seconds.
    /// </summary>
    private float _nextDefeatCheckTime;

    /// <summary>
    /// Whether the partner was in the local scene in the previous frame, to notice them coming into a boss fight.
    /// </summary>
    private bool _partnerWasInScene;

    /// <summary>
    /// Whether updating the boss checkpoint threw, which is only logged once.
    /// </summary>
    private bool _checkpointFailed;

    /// <summary>
    /// The frame that <see cref="_partnerMissing"/> was worked out in.
    /// </summary>
    private int _partnerMissingFrame = -1;

    /// <summary>
    /// Whether the partner of the loaded two-player save was missing in <see cref="_partnerMissingFrame"/>.
    /// </summary>
    private bool _partnerMissing;

    /// <summary>
    /// Whether the loaded save is a two-player save whose partner isn't on the server, so that boss fights wait for
    /// them. It is worked out once each frame, because boss rooms ask for the events of every object.
    /// </summary>
    public bool IsPartnerMissing() {
        var frame = Time.frameCount;
        if (_partnerMissingFrame != frame) {
            _partnerMissingFrame = frame;
            _partnerMissing = GetCurrentMarker() is { } marker && FindPartner(marker) == null;
        }

        return _partnerMissing;
    }

    /// <summary>
    /// Called when a boss fight starts with every player there. In a two-player save that both players are in, the
    /// fight gets a checkpoint, and the partner's game hears about it, because a boss only starts for one of them.
    /// Reports that come too early, like before the partner is in the scene, are ignored, so a later one still counts.
    /// </summary>
    public void OnBossFightStarted() {
        var scene = SceneUtil.GetCurrentSceneName();
        if (GetFightPartner(scene, out var marker) is not { } partner || marker == null) {
            return;
        }

        _fightStartedScene = scene;
        StartCheckpoint(marker, scene);
        Send(new CoopSaveUpdate { TargetId = partner.Id, Kind = CoopSaveUpdateKind.BossFight, Records = [scene] });
    }

    /// <summary>
    /// The partner's game saw a boss fight start with both players, or the local player came into a fight that goes
    /// on, so it gets a checkpoint here too if the local player is in its scene.
    /// </summary>
    private void OnBossFight(ClientPlayerData player, CoopSaveUpdate update) {
        var scene = SceneUtil.GetCurrentSceneName();
        if (update.Records.Count == 0 || update.Records[0] != scene ||
            GetFightPartner(scene, out var marker) is not { } partner || marker == null || partner.Id != player.Id) {
            return;
        }

        _fightStartedScene = scene;
        StartCheckpoint(marker, scene);
    }

    /// <summary>
    /// The partner of the loaded two-player save if a boss fight in a scene can get a checkpoint: the fight didn't get
    /// one since the local player came in, both saves were checked, the partner is in the scene and the local player
    /// is alive.
    /// </summary>
    private ClientPlayerData? GetFightPartner(string scene, out CoopSaveMarker? marker) {
        marker = null;
        var hero = HeroController.instance;
        if (_fightStartedScene == scene || !IsInGame() || hero == null || hero.cState.dead ||
            GetCurrentMarker() is not { } current || FindPartner(current) is not { } partner ||
            _checkedWith != partner.Id || !partner.IsInLocalScene) {
            return null;
        }

        marker = current;
        return partner;
    }

    /// <summary>
    /// Whether the local player is in a boss fight that started with both players since they came into its scene, and
    /// that has a checkpoint. Only such a fight ends when the partner drops out, not a room whose boss waits after an
    /// earlier fight ended.
    /// </summary>
    private bool IsInBossFight() {
        return _fightStartedScene != null && _fightStartedScene == SceneUtil.GetCurrentSceneName() &&
               GetCurrentMarker() is { BossScene: { } bossScene } && bossScene == _fightStartedScene;
    }

    /// <summary>
    /// Gives the boss fight in a scene a checkpoint at the door that the local player came in through, and saves the
    /// game once the intro of the fight lets the player pause again.
    /// </summary>
    private void StartCheckpoint(CoopSaveMarker marker, string scene) {
        _fightSaveScene = scene;

        var gate = global::GameManager.instance.GetEntryGateName();
        if (string.IsNullOrEmpty(gate) || FindDoor(scene, gate) == null) {
            Logger.Warn(
                $"The boss fight in '{scene}' started, but the door that the local player came in through ('{gate}') " +
                "isn't known, so it has no checkpoint"
            );
            return;
        }

        marker.BossScene = scene;
        marker.BossGate = gate;
        marker.BossDefeats = GetDefeatRecords().Count;
        SaveMarkers();

        _interruptPending = false;
        _loadedWithCheckpoint = false;
        _nextDefeatCheckTime = Time.unscaledTime + DefeatCheckInterval;
        Logger.Info($"The boss fight in '{scene}' started with both players, checkpoint at door '{gate}'");
    }

    /// <summary>
    /// Keeps the boss checkpoint of the loaded save up to date every frame: ends it when its boss was beaten, gives a
    /// partner who comes into the fight a checkpoint too, takes the local player out of a fight that their partner
    /// dropped out of, saves the game for a fight that started, and takes the local player back to the door after
    /// loading a save whose fight lasted.
    /// </summary>
    private void UpdateCheckpoint(HeroController hero, CoopSaveMarker marker, ClientPlayerData? partner) {
        var scene = SceneUtil.GetCurrentSceneName();
        var bossScene = marker.BossScene;
        if (bossScene != null && scene == bossScene && Time.unscaledTime >= _nextDefeatCheckTime) {
            _nextDefeatCheckTime = Time.unscaledTime + DefeatCheckInterval;
            if (EndCheckpointIfBeaten(marker)) {
                bossScene = null;
            }
        }

        if (bossScene == null) {
            _interruptPending = false;
            _loadedWithCheckpoint = false;
        }

        // Without a connection the game goes back to the main menu, and the checkpoint stays for when the player is back
        if (!_netClient.IsConnected) {
            _interruptPending = false;
            _partnerWasInScene = false;
            return;
        }

        // A partner who comes into a fight that goes on, like after dying, gets a checkpoint for it too
        var partnerIn = partner != null && _checkedWith == partner.Id;
        var partnerInScene = partner != null && partnerIn && partner.IsInLocalScene;
        if (partner != null && partnerInScene && !_partnerWasInScene && _fightStartedScene == scene &&
            bossScene == scene) {
            Send(new CoopSaveUpdate { TargetId = partner.Id, Kind = CoopSaveUpdateKind.BossFight, Records = [scene] });
        }

        _partnerWasInScene = partnerInScene;

        var gameManager = global::GameManager.instance;
        if (gameManager.GameState != GameState.PLAYING || gameManager.IsInSceneTransition || hero.cState.dead ||
            hero.cState.transitioning || hero.cState.hazardRespawning) {
            return;
        }

        if (_interruptPending && (scene != bossScene || partnerIn)) {
            _interruptPending = false;
        }

        if (_interruptPending) {
            // A boss that fell right before the partner dropped out doesn't need its door anymore
            if (Time.unscaledTime >= _nextMoveTryTime && !EndCheckpointIfBeaten(marker)) {
                _fightSaveScene = null;
                if (LeaveBossFight(hero, marker, scene)) {
                    _interruptPending = false;
                } else {
                    _nextMoveTryTime = Time.unscaledTime + MoveRetryInterval;
                }
            }

            return;
        }

        var playerData = PlayerData.instance;
        if (_fightSaveScene == scene && !playerData.disablePause && !playerData.isInvincible) {
            _fightSaveScene = null;
            Logger.Info($"Saving the two-player save for the boss fight in '{scene}'");
            gameManager.SaveGame(success => Logger.Info($"Saved the two-player save for the boss fight: {success}"));
            Chat("Saved your game at the start of the boss fight.");
        }

        if (bossScene == null || !_loadedWithCheckpoint || !partnerIn) {
            return;
        }

        if (scene == bossScene) {
            _loadedWithCheckpoint = false;
            return;
        }

        // The player goes back once they got up from the bench that the save loaded at, or got control otherwise
        if (playerData.atBench || hero.controlReqlinquished) {
            if (playerData.atBench && !_checkpointMoveNoticed) {
                _checkpointMoveNoticed = true;
                Chat("Get up to go back to the door of your boss fight.");
            }

            return;
        }

        if (Time.unscaledTime < _nextMoveTryTime) {
            return;
        }

        if (ReturnToCheckpoint(hero, marker)) {
            _loadedWithCheckpoint = false;
        } else {
            _nextMoveTryTime = Time.unscaledTime + MoveRetryInterval;
        }
    }

    /// <summary>
    /// Ends the boss fight for the local player because their partner dropped out of it: they leave through the door
    /// they came in through, and the boss waits for both players again when they come back in.
    /// </summary>
    /// <returns>Whether the player started leaving.</returns>
    private bool LeaveBossFight(HeroController hero, CoopSaveMarker marker, string scene) {
        var gate = marker.BossGate ?? "";
        var door = FindDoor(scene, gate);
        var leadsOut = false;
        global::GameManager.SceneLoadInfo info;
        if (door != null && !string.IsNullOrEmpty(door.targetScene) && door.targetScene != scene &&
            !string.IsNullOrEmpty(door.entryPoint) && GateExists(door.targetScene, door.entryPoint)) {
            leadsOut = true;
            info = new global::GameManager.SceneLoadInfo {
                SceneName = door.targetScene,
                EntryGateName = door.entryPoint,
                HeroLeaveDirection = door.GetGatePosition(),
                EntryDelay = door.entryDelay,
                WaitForSceneTransitionCameraFade = true,
                Visualization = door.sceneLoadVisualization,
                ForceWaitFetch = door.forceWaitFetch
            };
        } else {
            // Like the game does with a door that leads nowhere, the player comes back in through it
            info = new global::GameManager.SceneLoadInfo {
                SceneName = scene,
                EntryGateName = gate,
                HeroLeaveDirection = door != null ? door.GetGatePosition() : null,
                WaitForSceneTransitionCameraFade = true
            };
        }

        if (!MoveTo(hero, info)) {
            return false;
        }

        // What the intro of the fight set for the hero, like not being able to be hurt, doesn't come along
        var playerData = PlayerData.instance;
        playerData.disablePause = false;
        playerData.isInvincible = false;

        Logger.Info(
            leadsOut
                ? $"The partner dropped out of the boss fight, leaving through door '{gate}'"
                : $"The partner dropped out of the boss fight, coming back in through door '{gate}', which doesn't lead out"
        );
        Chat(
            $"{marker.PartnerName} dropped out of the boss fight, so it ends for you too. The boss waits until you are " +
            "both back in the room."
        );
        return true;
    }

    /// <summary>
    /// Takes the local player back to the door of the boss fight that lasted when the save loaded.
    /// </summary>
    /// <returns>Whether the checkpoint is done with, because the player started going back or its door is gone.</returns>
    private bool ReturnToCheckpoint(HeroController hero, CoopSaveMarker marker) {
        if (marker.BossScene is not { } scene || marker.BossGate is not { } gate || !GateExists(scene, gate)) {
            Logger.Warn(
                $"Door '{marker.BossGate}' of the boss fight in '{marker.BossScene}' doesn't exist, so its checkpoint ends"
            );
            ClearCheckpoint(marker);
            return true;
        }

        var started = MoveTo(hero, new global::GameManager.SceneLoadInfo {
            SceneName = scene,
            EntryGateName = gate,
            WaitForSceneTransitionCameraFade = true
        });
        if (!started) {
            return false;
        }

        Logger.Info($"Taking the local player back to door '{gate}' of '{scene}'");
        if (!_checkpointMoveNoticed) {
            Chat("Going back to the door of your boss fight.");
        }

        return true;
    }

    /// <summary>
    /// Takes the local player to a scene for a checkpoint the way a door does, which keeps the checkpoint. Input isn't
    /// ignored first like a door does, because the game drops the move while another scene still loads, and the hero
    /// would stay without input; the move itself makes the hero leave the scene.
    /// </summary>
    /// <returns>Whether the move started.</returns>
    private bool MoveTo(HeroController hero, global::GameManager.SceneLoadInfo info) {
        var gameManager = global::GameManager.instance;
        hero.RecordLeaveSceneCState();
        gameManager.BeginSceneTransition(info);

        // The move starts right away unless the game dropped it
        if (!gameManager.IsInSceneTransition) {
            Logger.Warn($"The game didn't start the move to '{info.SceneName}' for the boss checkpoint");
            return false;
        }

        _checkpointMoveTime = Time.unscaledTime;
        return true;
    }

    /// <summary>
    /// Finds the door with the given name, preferring one in the given scene, or null.
    /// </summary>
    private static TransitionPoint? FindDoor(string scene, string gate) {
        var doors = UnityEngine.Object
            .FindObjectsByType<TransitionPoint>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(point => point != null && point.gameObject.name == gate)
            .ToList();
        return doors.FirstOrDefault(point => point.gameObject.scene.name == scene) ?? doors.FirstOrDefault();
    }

    /// <summary>
    /// Whether a scene has a door with the given name, by the map of scenes that the game checks doors against, which
    /// also counts the parts that a scene is split into.
    /// </summary>
    private static bool GateExists(string scene, string gate) {
        var map = SceneTeleportMap.GetTeleportMap();
        return map != null && (HasGate(map, scene, gate) ||
                               (WorldInfo.SubSceneNameSuffixes ?? []).Any(suffix => HasGate(map, scene + suffix, gate)));
    }

    private static bool HasGate(Dictionary<string, SceneTeleportMap.SceneInfo> map, string scene, string gate) {
        return map.TryGetValue(scene, out var info) && info.TransitionGates != null && info.TransitionGates.Contains(gate);
    }

    /// <summary>
    /// Ends the boss checkpoint of a save if the save has beaten more bosses than when its fight started.
    /// </summary>
    /// <returns>Whether the checkpoint ended.</returns>
    private bool EndCheckpointIfBeaten(CoopSaveMarker marker) {
        if (GetDefeatRecords().Count <= marker.BossDefeats) {
            return false;
        }

        Logger.Info($"The boss in '{marker.BossScene}' was beaten, so its checkpoint ends");
        ClearCheckpoint(marker);
        return true;
    }

    /// <summary>
    /// Ends the boss checkpoint of a save.
    /// </summary>
    private void ClearCheckpoint(CoopSaveMarker marker) {
        marker.BossScene = null;
        marker.BossGate = null;
        marker.BossDefeats = 0;
        SaveMarkers();

        _interruptPending = false;
        _loadedWithCheckpoint = false;
    }

    /// <summary>
    /// Forgets what the session knows about boss checkpoints, while the checkpoint itself stays with the save.
    /// </summary>
    private void ResetCheckpointSession() {
        _fightStartedScene = null;
        _fightSaveScene = null;
        _interruptPending = false;
        _loadedWithCheckpoint = false;
        _checkpointMoveNoticed = false;
        _checkpointMoveTime = -1f;
        _nextMoveTryTime = 0f;
        _partnerWasInScene = false;
    }

    /// <summary>
    /// Ends the boss checkpoint of the loaded save when the local player leaves the scene of its fight some other way
    /// than a move for the checkpoint, like by walking out or dying. Going to the menu keeps it.
    /// </summary>
    private void OnCheckpointSceneChanged(string sceneName) {
        _fightStartedScene = null;
        _fightSaveScene = null;
        _partnerWasInScene = false;

        // Loading a save doesn't leave its boss fight, and neither do scenes like the one on the way to the menu
        var gameManager = global::GameManager.instance;
        if (gameManager == null || _sessionSlot != gameManager.profileID || sceneName == QuitToMenuSceneName ||
            SceneUtil.IsNonGameplayScene(sceneName)) {
            return;
        }

        var moved = _checkpointMoveTime >= 0f && Time.unscaledTime - _checkpointMoveTime < CheckpointMoveTime;
        _checkpointMoveTime = -1f;
        if (moved || GetMarker(_sessionSlot) is not { BossScene: { } bossScene } marker || sceneName == bossScene) {
            return;
        }

        Logger.Info($"The local player left '{bossScene}', so its boss checkpoint ends");
        ClearCheckpoint(marker);
    }
}
