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
/// TEMPORARY debug, to try two fights again in a copy of a save (USER 11-08: "给我们整一个可以回去debug…的办法（，我们会先复制
/// 一个存档…比如说按/0…整两个快捷键/1,/2给我们传送到…面前", "你可以直接重置到第一次进电梯", "因为那个第一次进电梯和打…的衔接本来也是
/// 有bug的，修了但没法测，正好测一下", "…打完会进结局，调试能不能不进结局", "结局太长了"). To be taken out again once they are tried.
///
/// What one player types, every player's game carries out for its own player:
/// - /0 writes the fight at the top of the lift back to before the first ride up: not met, not won, its door shut. A won
///   or met boss goes with the scene host's save, so every game writes its own.
/// - /1 takes every player to the bottom of that lift, coming down in it the way the game brings them back from the top.
/// - /2 takes every player to the challenge of the other fight. In a game that went there, winning that fight takes every
///   player back to the challenge instead of on to the end that follows it.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// How long, in seconds, a second /0 goes ahead after the first one.
    /// </summary>
    private const float DebugResetConfirmTime = 15f;

    /// <summary>
    /// How long, in seconds, a sent player waits to be able to go, like for a talk or a bench to end.
    /// </summary>
    private const float DebugTravelWaitTime = 20f;

    /// <summary>
    /// How long, in seconds, after the move the player is put at the place once they are in its room.
    /// </summary>
    private const float DebugPlaceWaitTime = 30f;

    /// <summary>
    /// The places of /1 and /2: the scene, the door to come in through, and the object of the room to be put at once in
    /// it, if any.
    /// </summary>
    private static readonly (string Scene, string Gate, string? StandAt)[] DebugPlaces = [
        ("Cog_Dancers", "door_arriveFromTower", null),
        ("Cradle_03", "left2", "Boss Scene/Intro Sequence/Challenge Region")
    ];

    /// <summary>
    /// The objects of the room of /2 whose FSMs play the end that follows the win, one for each way the fight ends.
    /// </summary>
    private static readonly string[] DebugEndSequences = ["Death Sequence", "Death Sequence Cursed"];

    /// <summary>
    /// The event that the fight of /2 sends to its end as it is won.
    /// </summary>
    private const string DebugEndStartEvent = "DEATH START";

    /// <summary>
    /// When, by Time.unscaledTime, the local player typed a /0 that waits for a second one.
    /// </summary>
    private float _debugResetAskedAt = float.NegativeInfinity;

    /// <summary>
    /// The place (1 or 2) that the local player is to be sent to once they can go, or 0.
    /// </summary>
    private int _debugTravelPlace;

    /// <summary>
    /// Until when, by Time.unscaledTime, the local player waits to be able to go.
    /// </summary>
    private float _debugTravelUntil;

    /// <summary>
    /// The place (1 or 2) that the local player is on their way to, to be put at once in its room, or 0.
    /// </summary>
    private int _debugPlacePlace;

    /// <summary>
    /// Until when, by Time.unscaledTime, the local player is put at the place once in its room.
    /// </summary>
    private float _debugPlaceUntil;

    /// <summary>
    /// Whether this game went to the fight of /2, so that winning it leads back to its challenge.
    /// </summary>
    private bool _debugSkipsEnd;

    /// <summary>
    /// Carries out the debug chat command /0, /1 or /2 that the local player typed, in every player's game.
    /// </summary>
    /// <param name="command">The number of the command.</param>
    public void RunDebugCommand(int command) {
        if (command == 0 && Time.unscaledTime > _debugResetAskedAt + DebugResetConfirmTime) {
            _debugResetAskedAt = Time.unscaledTime;
            UiManager.InternalChatBox.AddMessage(Lang.Pick(
                "Debug: /0 sets the fight at the top of the lift back to before the first ride up, in every player's " +
                "save. Type /0 again within 15 seconds to go ahead.",
                "调试：/0 会把所有人存档里的二见蕾丝改回第一次坐电梯上去之前（没见过、没打过）。确定的话 15 秒内再输一次 /0。"
            ));
            return;
        }

        _debugResetAskedAt = float.NegativeInfinity;
        Logger.Info($"Debug: the local player typed /{command}, which every player's game carries out");
        Send(BossRoomUpdateKind.DebugCommand, "", "", "", "", command.ToString());
        CarryOutDebugCommand(command, null);
    }

    /// <summary>
    /// Another player typed a debug chat command, which this game carries out for its own player.
    /// </summary>
    private void OnDebugCommand(BossRoomUpdate update) {
        var name = _playerData.TryGetValue(update.PlayerId, out var sender) ? sender.Username : $"#{update.PlayerId}";
        Logger.Info($"Debug: {name} typed /{update.EventName}, which this game carries out");
        CarryOutDebugCommand(int.Parse(update.EventName), name);
    }

    /// <summary>
    /// Carries out a debug chat command for the local player.
    /// </summary>
    /// <param name="command">The number of the command.</param>
    /// <param name="by">The username of the player who typed it, or null for the local player.</param>
    private void CarryOutDebugCommand(int command, string? by) {
        if (command == 0) {
            ResetFightAtTopOfLift(PlayerData.instance);
            UiManager.InternalChatBox.AddMessage(by == null
                ? Lang.Pick(
                    "Debug: the fight at the top of the lift is back to before the first ride up. Type /1 to go to the lift.",
                    "调试：二见蕾丝改回没打过了，输入 /1 去电梯。"
                )
                : Lang.Pick(
                    $"{by} set the fight at the top of the lift back to before the first ride up (debug).",
                    $"{by} 用调试命令把二见蕾丝改回了没打过。"
                ));
            return;
        }

        // A trip on its way is not set off again, like by the other game's word of the same win
        if (_debugTravelPlace == command || _debugPlacePlace == command) {
            Logger.Info($"Debug: the local player is already on the way to debug place {command}");
            return;
        }

        if (command == 2) {
            _debugSkipsEnd = true;
        }

        UiManager.InternalChatBox.AddMessage(command == 1
            ? by == null
                ? Lang.Pick(
                    "Debug: sending everyone to the bottom of the lift. Ride it up once you are out of it.",
                    "调试：正在把大家送到电梯下面，下来以后再坐电梯上去。"
                )
                : Lang.Pick(
                    $"{by} sent everyone to the bottom of the lift (debug).",
                    $"{by} 用调试命令把大家送到电梯下面。"
                )
            : by == null
                ? Lang.Pick(
                    "Debug: sending everyone to the challenge of the fight. Winning it skips the ending.",
                    "调试：正在把大家送到丝母面前，打赢了不进结局。"
                )
                : Lang.Pick(
                    $"{by} sent everyone to the challenge of the fight (debug). Winning it skips the ending.",
                    $"{by} 用调试命令把大家送到丝母面前，打赢了不进结局。"
                ));
        QueueDebugTravel(command);
    }

    /// <summary>
    /// Writes the fight at the top of the lift back to before the first ride up: not met, so the film plays on the
    /// way up, not won, so its boss is back, its door shut, and its body's later look undone.
    /// </summary>
    private static void ResetFightAtTopOfLift(PlayerData playerData) {
        playerData.encounteredLaceTower = false;
        playerData.defeatedLaceTower = false;
        playerData.laceTowerDoorOpened = false;
        playerData.laceCorpseAddedEffects = false;
        Logger.Info(
            "Debug: the local save has the fight at the top of the lift as before the first ride up: not met, not " +
            "won, its door shut"
        );
    }

    /// <summary>
    /// Sends the local player to a place once they can go.
    /// </summary>
    private void QueueDebugTravel(int place) {
        _debugTravelPlace = place;
        _debugTravelUntil = Time.unscaledTime + DebugTravelWaitTime;
        _debugPlacePlace = 0;
    }

    /// <summary>
    /// Sends the local player once they can go, and puts them at the place once they are in its room.
    /// </summary>
    private void UpdateDebugCommands() {
        if (_debugTravelPlace != 0) {
            if (Time.unscaledTime > _debugTravelUntil) {
                Logger.Info(
                    $"Debug: the local player could not go to debug place {_debugTravelPlace} in time: " +
                    DescribeWhyNotFree()
                );
                UiManager.InternalChatBox.AddMessage(Lang.Pick(
                    "Debug: you couldn't be sent, as you were busy. Type the command again.",
                    "调试：你一直在忙，没能传送过去，再输一次命令。"
                ));
                _debugTravelPlace = 0;
            } else if (IsLocalHeroFree()) {
                var place = _debugTravelPlace;
                _debugTravelPlace = 0;
                DebugTravelNow(place);
            }
        }

        if (_debugPlacePlace == 0) {
            return;
        }

        if (Time.unscaledTime > _debugPlaceUntil) {
            Logger.Info($"Debug: the local player never came into the room of debug place {_debugPlacePlace}");
            _debugPlacePlace = 0;
            return;
        }

        var (scene, _, standAt) = DebugPlaces[_debugPlacePlace - 1];
        var activeScene = SceneManager.GetActiveScene().name;
        if (!string.Equals(activeScene, scene, StringComparison.OrdinalIgnoreCase) || !IsLocalHeroFree()) {
            return;
        }

        _debugPlacePlace = 0;
        var spot = ScenePath.Find(standAt!, activeScene);
        if (spot == null) {
            Logger.Warn($"Debug: '{standAt}' was not found in '{scene}', so the player stays at the door");
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
    /// Sends the local player to the room of a place. The inventory that the room they leave took away, like the win of
    /// the fight of /2 does for its end, comes back with the trip.
    /// </summary>
    private void DebugTravelNow(int place) {
        var (scene, gate, standAt) = DebugPlaces[place - 1];
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

        Logger.Info($"Debug: sending the local player to door '{gate}' of '{scene}' for debug place {place}");
        if (standAt != null) {
            _debugPlacePlace = place;
            _debugPlaceUntil = Time.unscaledTime + DebugPlaceWaitTime;
        }
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
    /// Takes every player back to the challenge of the fight of /2 as it is won, in a game that went there with /2,
    /// instead of on to the end that follows (USER 11-08 "…打完会进结局，调试能不能不进结局", "结局太长了"). The inventory that
    /// the win took away for that end, in the game that runs the boss and maybe in the other one's copy, comes back with
    /// the trip (see DebugTravelNow).
    /// </summary>
    /// <returns>Whether the end is skipped, so that its FSM is not to hear the event.</returns>
    private bool SkipsDebugEnd(Fsm fsm, string eventName) {
        if (!_debugSkipsEnd || eventName != DebugEndStartEvent || fsm.GameObject is not { } owner ||
            Array.IndexOf(DebugEndSequences, owner.name) < 0 ||
            !string.Equals(owner.scene.name, DebugPlaces[1].Scene, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        Logger.Info(
            $"Debug: '{GetPath(fsm)}' does not hear '{eventName}': the fight of debug place 2 is won, so every " +
            "player goes back to its challenge instead of on to its end"
        );
        UiManager.InternalChatBox.AddMessage(Lang.Pick(
            "Debug: won. The ending is skipped; back to the challenge of the fight.",
            "调试：打赢了，跳过结局，回到丝母面前。"
        ));
        if (_debugTravelPlace == 0 && _debugPlacePlace == 0) {
            Send(BossRoomUpdateKind.DebugCommand, "", "", "", "", "2");
            QueueDebugTravel(2);
        }

        return true;
    }
}
