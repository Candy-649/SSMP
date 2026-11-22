using System;
using System.Collections.Generic;
using SSMP.Networking.Packet.Data;
using SSMP.Ui;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// TEMPORARY debug, to try the fight with the silk boss, which every game runs by itself now (USER 11-21 "说起来这个丝母
/// 能不能让我们测一下，还是调试命令/0开启调试，/1传送到丝母", "需要把之前那个打完丝母之后不进结局，直接回到丝母这个开战这里的
/// 功能恢复一下"), as /2 did before (15c0337, taken out b7db833). It takes the place of /0 and /1 for the knight (71a1119),
/// and is to be taken out again once it is tried.
///
/// /0 with the key turns the commands on (see DebugFightCommand). /1, which one player types, every player's game
/// carries out for its own player: it takes them to the challenge of the fight, and from then on until the game is
/// closed, winning the fight takes every player back there instead of on to the end that follows it.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// How long, in seconds, a sent player waits to be able to go, like for a talk or a bench to end.
    /// </summary>
    private const float DebugTravelWaitTime = 20f;

    /// <summary>
    /// How long, in seconds, after the move the player is put at the challenge once they are in its room.
    /// </summary>
    private const float DebugPlaceWaitTime = 30f;

    /// <summary>
    /// The place of /1: the scene, the door to come in through, and the object of the room to be put at once in it.
    /// </summary>
    private static readonly (string Scene, string Gate, string StandAt) DebugPlace =
        ("Cradle_03", "left2", "Boss Scene/Intro Sequence/Challenge Region");

    /// <summary>
    /// The objects of the room of /1 whose FSMs play the end that follows the win, one for each way the fight ends.
    /// </summary>
    private static readonly string[] DebugEndSequences = ["Death Sequence", "Death Sequence Cursed"];

    /// <summary>
    /// The event that the fight of /1 sends to its end as it is won.
    /// </summary>
    private const string DebugEndStartEvent = "DEATH START";

    /// <summary>
    /// Whether the local player is to be sent to the place of /1 once they can go.
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
    /// Whether the local player is on their way to the place of /1, to be put at the challenge once in its room.
    /// </summary>
    private bool _debugPlacing;

    /// <summary>
    /// Until when, by Time.unscaledTime, the local player is put at the challenge once in its room.
    /// </summary>
    private float _debugPlaceUntil;

    /// <summary>
    /// Whether this game went to the fight of /1, so that winning it leads back to its challenge.
    /// </summary>
    private bool _debugSkipsEnd;

    /// <summary>
    /// Whether the local player is on the trip of /1 and still in the room it was asked for in. From the fight itself,
    /// the trip leads into a load of the same room anew, where a player who comes in before them has the fight fresh,
    /// which this game is not to tell what the room here has (see CoopSave.IsLeavingOnDebugTrip).
    /// </summary>
    public bool IsLeavingOnDebugTrip =>
        (_debugTravelling || _debugPlacing) && SceneManager.GetActiveScene().handle == _debugTravelScene;

    /// <summary>
    /// Carries out the debug chat command /1 that the local player typed, in every player's game.
    /// </summary>
    public void RunDebugTrip() {
        Logger.Info("Debug: the local player typed /1, which every player's game carries out");
        Send(BossRoomUpdateKind.DebugCommand, "", "", "", "", "1");
        CarryOutDebugTrip(null);
    }

    /// <summary>
    /// Another player typed the debug chat command /1, which this game carries out for its own player.
    /// </summary>
    private void OnDebugCommand(BossRoomUpdate update) {
        var name = _playerData.TryGetValue(update.PlayerId, out var sender) ? sender.Username : $"#{update.PlayerId}";
        Logger.Info($"Debug: {name} typed /{update.EventName}, which this game carries out");
        CarryOutDebugTrip(name);
    }

    /// <summary>
    /// Sends the local player to the challenge of the fight once they can go, and has its win lead back there.
    /// </summary>
    /// <param name="by">The username of the player who typed it, or null for the local player.</param>
    private void CarryOutDebugTrip(string? by) {
        // A trip on its way is not set off again, like by the other game's word of the same win
        if (_debugTravelling || _debugPlacing) {
            Logger.Info("Debug: the local player is already on the way to the challenge of the silk boss");
            return;
        }

        _debugSkipsEnd = true;
        UiManager.InternalChatBox.AddMessage(by == null
            ? Lang.Pick(
                "Debug: sending everyone to the silk boss. Winning the fight skips the ending and takes everyone " +
                "back to its start.",
                "调试：正在把大家送到丝母那里，打赢了不进结局，直接回到开战的地方。"
            )
            : Lang.Pick(
                $"{by} sent everyone to the silk boss (debug). Winning the fight skips the ending.",
                $"{by} 用调试命令把大家送到丝母那里，打赢了不进结局。"
            ));
        QueueDebugTravel();
    }

    /// <summary>
    /// Sends the local player to the place of /1 once they can go.
    /// </summary>
    private void QueueDebugTravel() {
        _debugTravelling = true;
        _debugTravelScene = SceneManager.GetActiveScene().handle;
        _debugTravelUntil = Time.unscaledTime + DebugTravelWaitTime;
        _debugPlacing = false;
    }

    /// <summary>
    /// Sends the local player once they can go, and puts them at the challenge once they are in its room.
    /// </summary>
    private void UpdateDebugCommands() {
        if (_debugTravelling) {
            // Asked for while busy, a trip that ran once the player was free took them back from where they had gone
            // by then (USER 11-09 chat "怎么又松下来了")
            if (SceneManager.GetActiveScene().handle != _debugTravelScene) {
                Logger.Info("Debug: the local player left the room before going to the challenge of the silk boss");
                UiManager.InternalChatBox.AddMessage(Lang.Pick(
                    "Debug: you went to another room first, so you are not sent. Type the command again to go.",
                    "调试：你先去了别的房间，这次就不传送了，要去的话再输一次命令。"
                ));
                _debugTravelling = false;
            } else if (Time.unscaledTime > _debugTravelUntil) {
                Logger.Info(
                    "Debug: the local player could not go to the challenge of the silk boss in time: " +
                    DescribeWhyNotFree()
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
            Logger.Info("Debug: the local player never came into the room of the silk boss");
            _debugPlacing = false;
            return;
        }

        var activeScene = SceneManager.GetActiveScene().name;
        if (!string.Equals(activeScene, DebugPlace.Scene, StringComparison.OrdinalIgnoreCase) || !IsLocalHeroFree()) {
            return;
        }

        _debugPlacing = false;
        var spot = ScenePath.Find(DebugPlace.StandAt, activeScene);
        if (spot == null) {
            Logger.Warn(
                $"Debug: '{DebugPlace.StandAt}' was not found in '{activeScene}', so the player stays at the door"
            );
            return;
        }

        PlaceOnGround(HeroController.instance, spot.transform);
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
    /// Sends the local player to the room of the silk boss. The inventory that the room they leave took away, like the
    /// win of the fight does for its end, comes back with the trip.
    /// </summary>
    private void DebugTravelNow() {
        var (scene, gate, _) = DebugPlace;
        var playerData = PlayerData.instance;
        if (playerData.disableInventory) {
            playerData.disableInventory = false;
            Logger.Info("Debug: the inventory, which the room took away, is back for the trip");
        }

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

        Logger.Info($"Debug: sending the local player to door '{gate}' of '{scene}', the room of the silk boss");
        _debugPlacing = true;
        _debugPlaceUntil = Time.unscaledTime + DebugPlaceWaitTime;
    }

    /// <summary>
    /// Puts the hero on the ground under an object of the room.
    /// </summary>
    private static void PlaceOnGround(HeroController hero, Transform spot) {
        var ground = hero.FindGroundPoint((Vector2) spot.position + Vector2.up, true);
        var position = hero.transform.position;
        position.x = ground.x;
        position.y = ground.y;
        hero.transform.position = position;

        var body = hero.GetComponent<Rigidbody2D>();
        body.position = position;
        body.linearVelocity = Vector2.zero;
        GameCameras.instance.cameraController.PositionToHeroInstant(true);
        Logger.Info($"Debug: put the local player at ({position.x:0.0}, {position.y:0.0}) by '{spot.name}'");
    }

    /// <summary>
    /// Takes every player back to the challenge of the fight of /1 as it is won, in a game that went there with /1,
    /// instead of on to the end that follows (USER 11-08 "…打完会进结局，调试能不能不进结局", "结局太长了"; 11-21). The
    /// inventory that the win took away for that end comes back with the trip (see DebugTravelNow).
    /// </summary>
    /// <returns>Whether the end is skipped, so that its FSM is not to hear the event.</returns>
    private bool SkipsDebugEnd(Fsm fsm, string eventName) {
        if (!_debugSkipsEnd || eventName != DebugEndStartEvent || fsm.GameObject is not { } owner ||
            Array.IndexOf(DebugEndSequences, owner.name) < 0 ||
            !string.Equals(owner.scene.name, DebugPlace.Scene, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        Logger.Info(
            $"Debug: '{GetPath(fsm)}' does not hear '{eventName}': the fight with the silk boss is won, so every " +
            "player goes back to its challenge instead of on to its end"
        );
        UiManager.InternalChatBox.AddMessage(Lang.Pick(
            "Debug: won. The ending is skipped; back to the start of the fight.",
            "调试：打赢了，跳过结局，回到丝母开战的地方。"
        ));
        if (!_debugTravelling && !_debugPlacing) {
            Send(BossRoomUpdateKind.DebugCommand, "", "", "", "", "1");
            QueueDebugTravel();
        }

        return true;
    }
}
