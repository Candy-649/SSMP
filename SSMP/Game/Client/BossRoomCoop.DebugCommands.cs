using System;
using System.Collections.Generic;
using SSMP.Networking.Packet.Data;
using SSMP.Ui;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// TEMPORARY debug, to try an encounter of creatures that every game runs by itself, whose battle every game runs by
/// itself too now (see ArenaCoop.OwnArenas; USER 11-25 "能不能指定一个遭遇战房间我们测一下这个", "做吧，/1传送丝母也可以删
/// 掉了？"). It takes the place of /0 and /1 for the silk boss (26c1307), and is to be taken out again once it is tried.
///
/// /0 with the key turns the commands on (see DebugFightCommand). /2, which one player types, every player's game
/// carries out for its own player: it takes them over the arena of the encounter, which their save holds as never
/// fought by the time they come in.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// How long, in seconds, a sent player waits to be able to go, like for a talk or a bench to end.
    /// </summary>
    private const float DebugTravelWaitTime = 20f;

    /// <summary>
    /// How long, in seconds, after the move the player is put over the arena once they are in its room.
    /// </summary>
    private const float DebugPlaceWaitTime = 30f;

    /// <summary>
    /// The place of /2: the scene, the door to come in through, and the point over the ground to be put on once in it,
    /// in the passage over the arena, a few steps from the floor that gives way under a player and drops them in.
    /// </summary>
    private static readonly (string Scene, string Gate, Vector2 StandOver) DebugPlace =
        ("Bone_01", "left2", new Vector2(106f, 51.5f));

    /// <summary>
    /// What the room of /2 saves of its encounter: its win, the floor that gives way as it starts, and the creature of
    /// its last wave, which stays dead until a rest.
    /// </summary>
    private static readonly string[] DebugEncounterSaves =
        ["Battle Scene", "Collapser Small (1)", "Bone Goomba Large (1)"];

    /// <summary>
    /// Whether the local player is to be sent to the place of /2 once they can go.
    /// </summary>
    private bool _debugTravelling;

    /// <summary>
    /// The scene in which the trip was asked for, by its handle.
    /// </summary>
    private int _debugTravelScene;

    /// <summary>
    /// Until when, by Time.unscaledTime, the local player waits to be able to go.
    /// </summary>
    private float _debugTravelUntil;

    /// <summary>
    /// Whether the local player is on their way to the place of /2, to be put over the arena once in its room.
    /// </summary>
    private bool _debugPlacing;

    /// <summary>
    /// Until when, by Time.unscaledTime, the local player is put over the arena once in its room.
    /// </summary>
    private float _debugPlaceUntil;

    /// <summary>
    /// Whether the local player is on the trip of /2 and still in the room it was asked for in. From the room of the
    /// encounter itself, the trip leads into a load of the same room anew, where a player who comes in before them has
    /// the encounter fresh, which this game is not to tell what the room here has (see CoopSave.IsLeavingOnDebugTrip
    /// and ArenaCoop.IsLeavingOnDebugTrip).
    /// </summary>
    public bool IsLeavingOnDebugTrip =>
        (_debugTravelling || _debugPlacing) && SceneManager.GetActiveScene().handle == _debugTravelScene;

    /// <summary>
    /// Carries out the debug chat command /2 that the local player typed, in every player's game.
    /// </summary>
    public void RunDebugTrip() {
        Logger.Info("Debug: the local player typed /2, which every player's game carries out");
        Send(BossRoomUpdateKind.DebugCommand, "", "", "", "", "2");
        CarryOutDebugTrip(null);
    }

    /// <summary>
    /// Another player typed the debug chat command /2, which this game carries out for its own player.
    /// </summary>
    private void OnDebugCommand(BossRoomUpdate update) {
        var name = _playerData.TryGetValue(update.PlayerId, out var sender) ? sender.Username : $"#{update.PlayerId}";
        Logger.Info($"Debug: {name} typed /{update.EventName}, which this game carries out");
        CarryOutDebugTrip(name);
    }

    /// <summary>
    /// Sends the local player over the arena of the encounter once they can go.
    /// </summary>
    /// <param name="by">The username of the player who typed it, or null for the local player.</param>
    private void CarryOutDebugTrip(string? by) {
        // Both players typing it at once, each game hears the other's word while its own trip is on its way, which,
        // carried out, said once they were there that they had gone to another room first (review of ae5ae80)
        if (_debugTravelling || _debugPlacing) {
            Logger.Info("Debug: the local player is already on the way to the encounter");
            return;
        }

        UiManager.InternalChatBox.AddMessage(by == null
            ? Lang.Pick(
                "Debug: sending everyone to the encounter, which starts anew.",
                "调试：正在把大家送到遭遇战那里，遭遇战重新开始。"
            )
            : Lang.Pick(
                $"{by} sent everyone to the encounter (debug), which starts anew.",
                $"{by} 用调试命令把大家送到遭遇战那里，遭遇战重新开始。"
            ));
        _debugTravelling = true;
        _debugTravelScene = SceneManager.GetActiveScene().handle;
        _debugTravelUntil = Time.unscaledTime + DebugTravelWaitTime;
    }

    /// <summary>
    /// Sends the local player once they can go, and puts them over the arena once they are in its room.
    /// </summary>
    private void UpdateDebugCommands() {
        if (_debugTravelling) {
            // Asked for while busy, a trip that ran once the player was free took them back from where they had gone
            // by then (USER 11-09 chat "怎么又松下来了")
            if (SceneManager.GetActiveScene().handle != _debugTravelScene) {
                Logger.Info("Debug: the local player left the room before going to the encounter");
                UiManager.InternalChatBox.AddMessage(Lang.Pick(
                    "Debug: you went to another room first, so you are not sent. Type the command again to go.",
                    "调试：你先去了别的房间，这次就不传送了，要去的话再输一次命令。"
                ));
                _debugTravelling = false;
            } else if (Time.unscaledTime > _debugTravelUntil) {
                Logger.Info(
                    "Debug: the local player could not go to the encounter in time: " + DescribeWhyNotFree()
                );
                UiManager.InternalChatBox.AddMessage(Lang.Pick(
                    "Debug: you couldn't be sent, as you were busy. Type the command again.",
                    "调试：你一直在忙，没能传送过去，再输一次命令。"
                ));
                _debugTravelling = false;
            } else if (IsLocalHeroFree()) {
                _debugTravelling = false;
                DebugTravelNow();
            }
        }

        if (!_debugPlacing) {
            return;
        }

        if (Time.unscaledTime > _debugPlaceUntil) {
            Logger.Info("Debug: the local player never came into the room of the encounter");
            SceneManager.sceneLoaded -= ClearDebugEncounter;
            _debugPlacing = false;
            return;
        }

        if (!string.Equals(SceneManager.GetActiveScene().name, DebugPlace.Scene, StringComparison.OrdinalIgnoreCase) ||
            !IsLocalHeroFree()) {
            return;
        }

        _debugPlacing = false;
        PlaceOnGround(HeroController.instance, DebugPlace.StandOver);
    }

    /// <summary>
    /// What keeps the local player from being free to go (see IsLocalHeroFree), for the log.
    /// </summary>
    private static string DescribeWhyNotFree() {
        var hero = HeroController.instance;
        if (hero == null) {
            return "no hero";
        }

        var reasons = new List<string>();
        if (!hero.acceptingInput) {
            reasons.Add("not taking input");
        }

        if (hero.controlReqlinquished) {
            reasons.Add("control taken");
        }

        if (hero.cState.dead) {
            reasons.Add("dead");
        }

        if (PlayerTargetRegistry.IsPlayerDown(hero.gameObject)) {
            reasons.Add("down");
        }

        var playerData = PlayerData.instance;
        if (playerData.disablePause) {
            reasons.Add("pausing switched off");
        }

        if (playerData.atBench) {
            reasons.Add("on a bench");
        }

        if (IsDialogueRunning() == true) {
            reasons.Add("in a talk");
        }

        if (global::GameManager.instance is { IsInSceneTransition: true }) {
            reasons.Add("changing rooms");
        }

        return reasons.Count > 0 ? string.Join(", ", reasons) : "free";
    }

    /// <summary>
    /// Sends the local player to the room of the encounter, whose save is to hold it as never fought as it loads.
    /// </summary>
    private void DebugTravelNow() {
        var (scene, gate, _) = DebugPlace;
        var gameManager = global::GameManager.instance;
        HeroController.instance.RecordLeaveSceneCState();
        gameManager.BeginSceneTransition(new global::GameManager.SceneLoadInfo {
            SceneName = scene,
            EntryGateName = gate,
            WaitForSceneTransitionCameraFade = true
        });
        if (!gameManager.IsInSceneTransition) {
            Logger.Warn($"Debug: the game didn't start the move to '{scene}'");
            UiManager.InternalChatBox.AddMessage(Lang.Pick(
                "Debug: the game didn't let you go. Type the command again.",
                "调试：游戏没让你过去，再输一次命令。"
            ));
            return;
        }

        Logger.Info($"Debug: sending the local player to door '{gate}' of '{scene}', the room of the encounter");
        SceneManager.sceneLoaded += ClearDebugEncounter;
        _debugPlacing = true;
        _debugPlaceUntil = Time.unscaledTime + DebugPlaceWaitTime;
    }

    /// <summary>
    /// Has the save hold the encounter of /2 as never fought, as its room loads for the trip, before the objects of
    /// the room read what the save holds of them. Not before the trip: a game that leaves that room has its objects
    /// write into the save what they are as it goes (GameManager.SaveLevelState).
    /// </summary>
    private void ClearDebugEncounter(Scene scene, LoadSceneMode mode) {
        if (!string.Equals(scene.name, DebugPlace.Scene, StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        SceneManager.sceneLoaded -= ClearDebugEncounter;
        var saved = SceneData.instance.PersistentBools;
        foreach (var id in DebugEncounterSaves) {
            if (!saved.TryGetValue(scene.name, id, out var item) || !item.Value) {
                continue;
            }

            saved.SetValue(new PersistentItemData<bool> {
                ID = id,
                SceneName = scene.name,
                Value = false,
                IsSemiPersistent = item.IsSemiPersistent
            });
            Logger.Info($"Debug: '{id}' in '{scene.name}' is no longer saved as done, for the encounter anew");
        }
    }

    /// <summary>
    /// Puts the hero on the ground under a point of the room.
    /// </summary>
    private static void PlaceOnGround(HeroController hero, Vector2 over) {
        var ground = hero.FindGroundPoint(over, true);
        var position = hero.transform.position;
        position.x = ground.x;
        position.y = ground.y;
        hero.transform.position = position;

        var body = hero.GetComponent<Rigidbody2D>();
        body.position = position;
        body.linearVelocity = Vector2.zero;
        GameCameras.instance.cameraController.PositionToHeroInstant(true);
        Logger.Info($"Debug: put the local player at ({position.x:0.0}, {position.y:0.0}), over the arena");
    }
}
