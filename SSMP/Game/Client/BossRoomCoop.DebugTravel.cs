using System;
using System.Collections.Generic;
using GlobalEnums;
using SSMP.Networking.Packet.Data;
using SSMP.Ui;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// TEMPORARY debug, to try the late dreams without playing up to them (USER 10-31: "你整一个临时调试用的，比如说/1,/2,/3这样的快捷键，
/// 输入就直接给我们传送到下一个弹琴的地方"). To be taken out again once the dreams are tried.
///
/// The chat commands /1, /2 and /3 take every player to the place in the real room where one of the dreams starts. Each
/// game first gives its own player what a player has when they come there in a playthrough, written the way the game
/// writes it: the jump that the shrine on the way gives, without which the way there can't be gone (USER 10-31 "万一是
/// 地图里需要那个跳跃呢"), and the power to play their way into a memory, with the main quest that the characters who
/// teach it begin. Only a save that is in the game's third act goes, as the world of the third act changes many
/// creatures and places, and the dreams are to be tried in it (USER 10-31 "第三幕是一个大环境了"); the chat command /0
/// turns a save into it the way the game does.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// The places where the dreams start: the scene, the door to come in through, and the door that the dream sends the
    /// player back out to, which stands where the dream starts.
    /// </summary>
    private static readonly (string Scene, string Gate, string WakeDoor)[] DebugDreamPlaces = [
        ("Ant_Queen", "left1", "Memory Group/door_wakeOnGround"),
        ("Coral_Tower_01", "left1", "Memory Group/door_wakeOnGround"),
        ("Shellwood_11b", "right1", "door_wakeOnGround")
    ];

    /// <summary>
    /// The main quest that the characters who teach the power to play into a memory begin as they teach it.
    /// </summary>
    private const string DebugDreamsQuestName = "Black Thread Pt5 Heart";

    /// <summary>
    /// The child of a door of the dreams where the game puts a player who dies after coming out of the dream.
    /// </summary>
    private const string DebugRespawnMarkerName = "Death Respawn Marker";

    /// <summary>
    /// How long, in seconds, a sent player waits to be able to go, like for a cutscene or a bench to end.
    /// </summary>
    private const float DebugTravelWaitTime = 20f;

    /// <summary>
    /// How long, in seconds, after the move the player is put at the place once they are in its room.
    /// </summary>
    private const float DebugPlaceWaitTime = 30f;

    /// <summary>
    /// The place (1 to 3) that the local player is to be sent to once they can go, or 0.
    /// </summary>
    private int _debugTravelPlace;

    /// <summary>
    /// Until when, by Time.unscaledTime, the local player waits to be able to go.
    /// </summary>
    private float _debugTravelUntil;

    /// <summary>
    /// The place (1 to 3) that the local player is on their way to, to be put at once in its room, or 0.
    /// </summary>
    private int _debugPlacePlace;

    /// <summary>
    /// Until when, by Time.unscaledTime, the local player is put at the place once in its room.
    /// </summary>
    private float _debugPlaceUntil;

    /// <summary>
    /// Sends every player to where a dream starts, for the chat commands /1, /2 and /3.
    /// </summary>
    /// <param name="place">The place, 1 to 3.</param>
    public void StartDebugTravel(int place) {
        if (place < 1 || place > DebugDreamPlaces.Length) {
            return;
        }

        if (DescribeWhyNotInThirdAct() is { } why) {
            UiManager.InternalChatBox.AddMessage(why);
            return;
        }

        Logger.Info($"Debug: the local player sends every player to dream place {place}");
        Send(BossRoomUpdateKind.DebugTravel, "", "", "", "", place.ToString());
        UiManager.InternalChatBox.AddMessage(Lang.Pick(
            $"Debug: sending everyone to dream entrance {place}.",
            $"调试：正在把大家送到梦境入口 {place}。"
        ));
        QueueDebugTravel(place);
    }

    /// <summary>
    /// Another player sent every player to where a dream starts.
    /// </summary>
    private void OnDebugTravel(BossRoomUpdate update) {
        if (!int.TryParse(update.EventName, out var place) || place < 1 || place > DebugDreamPlaces.Length) {
            return;
        }

        var name = _playerData.TryGetValue(update.PlayerId, out var sender) ? sender.Username : $"#{update.PlayerId}";
        Logger.Info($"Debug: {name} sent every player to dream place {place}");
        if (DescribeWhyNotInThirdAct() is { } why) {
            UiManager.InternalChatBox.AddMessage(Lang.Pick(
                $"{name} sent everyone to dream entrance {place} (debug), but you stay here: {why}",
                $"{name} 用调试命令把大家送到梦境入口 {place}，但你留在原地：{why}"
            ));
            return;
        }

        UiManager.InternalChatBox.AddMessage(Lang.Pick(
            $"{name} sent everyone to dream entrance {place} (debug).",
            $"{name} 用调试命令把大家送到梦境入口 {place}。"
        ));
        QueueDebugTravel(place);
    }

    /// <summary>
    /// Why the local save can't go to the dreams, as it is not in the third act, or null if it can.
    /// </summary>
    private static string? DescribeWhyNotInThirdAct() {
        var gameManager = global::GameManager.UnsafeInstance;
        var playerData = PlayerData.instance;
        if (gameManager == null || playerData == null || HeroController.UnsafeInstance == null ||
            gameManager.GameState is GameState.MAIN_MENU or GameState.INACTIVE or GameState.PRIMER) {
            return Lang.Pick("Load a save first.", "先进入一个存档。");
        }

        if (!playerData.blackThreadWorld) {
            return Lang.Pick("Your save isn't in the third act yet. Type /0 first.", "你的存档还没进第三幕，先输入 /0。");
        }

        if (!playerData.act3_wokeUp) {
            return Lang.Pick(
                "Your save starts the third act the next time it loads: go back to the main menu and load it again.",
                "你的存档下次读档时才开始第三幕：回主菜单再读一次这个存档。"
            );
        }

        return null;
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
    private void UpdateDebugTravel() {
        if (_debugTravelPlace != 0) {
            LeaveStaleBench();
            if (Time.unscaledTime > _debugTravelUntil) {
                Logger.Info(
                    $"Debug: the local player could not go to dream place {_debugTravelPlace} in time: " +
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
            Logger.Info($"Debug: the local player never came into the room of dream place {_debugPlacePlace}");
            _debugPlacePlace = 0;
            return;
        }

        var (scene, _, wakeDoor) = DebugDreamPlaces[_debugPlacePlace - 1];
        var activeScene = SceneManager.GetActiveScene().name;
        if (!string.Equals(activeScene, scene, StringComparison.OrdinalIgnoreCase) || !IsLocalHeroFree()) {
            return;
        }

        _debugPlacePlace = 0;
        var door = ScenePath.Find(wakeDoor, activeScene);
        if (door == null) {
            Logger.Warn($"Debug: door '{wakeDoor}' of the dream in '{scene}' was not found, so the player stays at the door");
            return;
        }

        PlaceAtDream(HeroController.instance, door.transform);
    }

    /// <summary>
    /// Gets the local player up from a bench that the game takes them to sit on while they move about. A save that /0
    /// turned into the third act while its player sat on a bench kept them sitting there for good, as only getting up
    /// from a bench clears it and the third act starts at none: the trips waited for them to get up, and the needolin
    /// did nothing (USER 10-31: "/1失败了", "而且弹琴键点不了"). /0 gets them up now; this is for the saves it turned before.
    /// </summary>
    private static void LeaveStaleBench() {
        var hero = HeroController.instance;
        var playerData = PlayerData.instance;
        if (hero == null || playerData is not { atBench: true } || !hero.acceptingInput || hero.controlReqlinquished) {
            return;
        }

        playerData.atBench = false;
        Logger.Info("Debug: the game took the local player to sit on a bench while they moved about; got them up from it");
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
        if (playerData == null) {
            reasons.Add("no player data");
        } else {
            if (playerData.disablePause) {
                reasons.Add("pausing switched off");
            }

            if (playerData.atBench) {
                reasons.Add("on a bench");
            }
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
    /// Gives the local player what the dreams need and sends them to the room of a place.
    /// </summary>
    private void DebugTravelNow(int place) {
        var (scene, gate, _) = DebugDreamPlaces[place - 1];
        var gameManager = global::GameManager.instance;
        var hero = HeroController.instance;
        GiveWhatTheDreamsNeed(PlayerData.instance);

        hero.RecordLeaveSceneCState();
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

        Logger.Info($"Debug: sending the local player to door '{gate}' of '{scene}' for dream place {place}");
        _debugPlacePlace = place;
        _debugPlaceUntil = Time.unscaledTime + DebugPlaceWaitTime;
    }

    /// <summary>
    /// Writes what a player has when they come to the dreams in a playthrough, the way the game writes it, where the
    /// local player lacks it.
    /// </summary>
    private static void GiveWhatTheDreamsNeed(PlayerData playerData) {
        // The shrine that gives the jump writes these two, the second of which the player has from an earlier shrine
        if (!playerData.hasSuperJump) {
            playerData.hasSuperJump = true;
            playerData.hasSilkSpecial = true;
            Logger.Info("Debug: gave the local player the jump of the shrine on the way to the dreams");
        }

        // The characters who teach the power begin the main quest that goes with it as they do
        if (!playerData.hasNeedolinMemoryPowerup) {
            playerData.hasNeedolinMemoryPowerup = true;
            Logger.Info("Debug: gave the local player the power to play into a memory");
        }

        var quest = QuestManager.GetQuest(DebugDreamsQuestName);
        if (quest == null) {
            Logger.Warn($"Debug: quest '{DebugDreamsQuestName}' was not found");
        } else if (!quest.IsAccepted && !quest.IsCompleted) {
            quest.BeginQuest(() => { }, false);
            Logger.Info($"Debug: began quest '{DebugDreamsQuestName}' for the local player");
        }
    }

    /// <summary>
    /// Puts the hero on the ground where the dream that a door belongs to starts: where the game puts a player who dies
    /// after coming out of it, or by the door.
    /// </summary>
    private static void PlaceAtDream(HeroController hero, Transform door) {
        var marker = door.Find(DebugRespawnMarkerName);
        var from = (Vector2) (marker != null ? marker : door).position + Vector2.up;
        var ground = hero.FindGroundPoint(from, true);

        var position = hero.transform.position;
        position.x = ground.x;
        position.y = ground.y;
        hero.transform.position = position;

        var body = hero.GetComponent<Rigidbody2D>();
        if (body != null) {
            body.position = position;
            body.linearVelocity = Vector2.zero;
        }

        if (GameCameras.instance != null && GameCameras.instance.cameraController != null) {
            GameCameras.instance.cameraController.PositionToHeroInstant(true);
        }

        Logger.Info($"Debug: put the local player at ({position.x:0.0}, {position.y:0.0}) by '{door.name}'");
    }
}
