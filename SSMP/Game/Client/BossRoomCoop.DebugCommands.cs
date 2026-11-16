using System.Collections.Generic;
using SSMP.Networking.Packet.Data;
using SSMP.Ui;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// TEMPORARY debug, to fight the second sentinel again (USER 11-16 "把这个boss本地化了吧，然后出/0和/1调试命令，这个怪想
/// 后面再打一遍", "/0要把剧情变回祈愿还没做完的样子，然后把这战重新打开。因为这个祈愿对话本身也有问题"). To be taken out
/// again once it is tried, as the commands of two other fights were (b7db833).
///
/// What one player types, every player's game carries out for its own player:
/// - /0 writes the fight back to before it was met and won, and the wish that leads to it back to accepted and not
///   done, so the knight sleeps in its room again and both talks, before the fight and with its body after it, are
///   there to have again. A met or won boss is a field that each save keeps, and is shared only as it is set, so every
///   game writes its own; the wish goes from each to the other as any change of the wish log, and is the same in both.
/// - /1 takes every player to the door of the knight's room.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// The wish that leads to the fight of /0, by its name.
    /// </summary>
    private const string DebugWishName = "Song Knight";

    /// <summary>
    /// How long, in seconds, a second /0 goes ahead after the first one.
    /// </summary>
    private const float DebugResetConfirmTime = 15f;

    /// <summary>
    /// How long, in seconds, a sent player waits to be able to go, like for a talk or a bench to end.
    /// </summary>
    private const float DebugTravelWaitTime = 20f;

    /// <summary>
    /// The place of /1: the scene and the door to come in through.
    /// </summary>
    private static readonly (string Scene, string Gate) DebugPlace = ("Hang_17b", "left1");

    /// <summary>
    /// When, by Time.unscaledTime, the local player typed a /0 that waits for a second one.
    /// </summary>
    private float _debugResetAskedAt = float.NegativeInfinity;

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
    /// Carries out the debug chat command /0 or /1 that the local player typed, in every player's game.
    /// </summary>
    /// <param name="command">The number of the command.</param>
    public void RunDebugCommand(int command) {
        if (command == 0 && Time.unscaledTime > _debugResetAskedAt + DebugResetConfirmTime) {
            _debugResetAskedAt = Time.unscaledTime;
            UiManager.InternalChatBox.AddMessage(Lang.Pick(
                "Debug: /0 sets the fight with the second sentinel back to before it was met, and its wish back to " +
                "accepted and not done, in every player's save. Type /0 again within 15 seconds to go ahead.",
                "调试：/0 会把所有人存档里次席戍卫这一战改回没见过、没打过，它的祈愿改回接了还没完成。" +
                "确定的话 15 秒内再输一次 /0。"
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
            ResetKnightFight(PlayerData.instance);
            UiManager.InternalChatBox.AddMessage(by == null
                ? Lang.Pick(
                    "Debug: the fight with the second sentinel is back to before it was met, and its wish is not done. " +
                    "Type /1 to go to its room.",
                    "调试：次席戍卫这一战改回没打过了，祈愿也改回没完成，输入 /1 去它的房间。"
                )
                : Lang.Pick(
                    $"{by} set the fight with the second sentinel back to before it was met (debug).",
                    $"{by} 用调试命令把次席戍卫这一战改回了没打过。"
                ));
            return;
        }

        // A trip on its way is not set off again
        if (_debugTravelling) {
            Logger.Info("Debug: the local player is already on the way to the room of the second sentinel");
            return;
        }

        UiManager.InternalChatBox.AddMessage(by == null
            ? Lang.Pick(
                "Debug: sending everyone to the room of the second sentinel.",
                "调试：正在把大家送到次席戍卫的房间。"
            )
            : Lang.Pick(
                $"{by} sent everyone to the room of the second sentinel (debug).",
                $"{by} 用调试命令把大家送到次席戍卫的房间。"
            ));
        _debugTravelling = true;
        _debugTravelScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        _debugTravelUntil = Time.unscaledTime + DebugTravelWaitTime;
    }

    /// <summary>
    /// Writes the fight with the second sentinel back to before it was met and won, so that the knight sleeps in its
    /// room again (its 'Control' starts in 'Sleeping' while it is not met, and its room is there while it is not won),
    /// and its wish back to accepted and not done, which the knight's body completes after the fight and which clears
    /// the offer of it (songChevalierQuestReady) as it gives its reward.
    /// </summary>
    private static void ResetKnightFight(PlayerData playerData) {
        playerData.encounteredSongChevalierBoss = false;
        playerData.defeatedSongChevalierBoss = false;
        playerData.songChevalierQuestReady = true;
        playerData.QuestCompletionData.SetData(
            DebugWishName, new QuestCompletionData.Completion { HasBeenSeen = true, IsAccepted = true }
        );
        QuestManager.IncrementVersion();
        Logger.Info(
            $"Debug: the local save has the fight with the second sentinel as before it was met, not won, and the " +
            $"wish '{DebugWishName}' accepted and not done"
        );
    }

    /// <summary>
    /// Sends the local player to the room of /1 once they can go.
    /// </summary>
    private void UpdateDebugCommands() {
        if (!_debugTravelling) {
            return;
        }

        // Asked for while busy, a trip that ran once the player was free took them back from where they had gone by
        // then (USER 11-09 chat "怎么又松下来了")
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle != _debugTravelScene) {
            Logger.Info("Debug: the local player left the room before going to the room of the second sentinel");
            UiManager.InternalChatBox.AddMessage(Lang.Pick(
                "Debug: you went to another room first, so you are not sent. Type the command again to go.",
                "调试：你先去了别的房间，这次就不传送了，要去的话再输一次命令。"
            ));
            _debugTravelling = false;
        } else if (Time.unscaledTime > _debugTravelUntil) {
            Logger.Info(
                "Debug: the local player could not go to the room of the second sentinel in time: " +
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
    /// Sends the local player to the door of the room of /1. The inventory that a room took away comes back with the
    /// trip.
    /// </summary>
    private static void DebugTravelNow() {
        var (scene, gate) = DebugPlace;
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

        Logger.Info($"Debug: sending the local player to door '{gate}' of '{scene}'");
    }
}
