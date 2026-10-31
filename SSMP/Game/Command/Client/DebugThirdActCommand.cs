using GlobalEnums;
using SSMP.Api.Command;
using SSMP.Api.Command.Client;
using SSMP.Game.Client;
using SSMP.Networking.Client;
using SSMP.Ui;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Command.Client;

/// <summary>
/// TEMPORARY debug command (/0) that turns the save into the game's third act the way the game does, for trying the
/// late dreams in the world they are in (USER 10-31 "第三幕是一个大环境了，如果这个都不对我怕有些地方反而会有新的bug"). To be
/// taken out again once the dreams are tried.
///
/// It writes into the save what the end of the second act that leads on into the third writes: the game's own switch
/// of the world (GameManager.StartBlackThreadWorld) and the record of that ending. Its achievement and the title screen
/// it unlocks are left out, as they are outside the save. Then the game saves and goes back to the main menu, as it does
/// after that ending: loading the save again starts the third act, which the game does itself (GameManager.StartAct3,
/// and the scene the player wakes up in). The save can't be turned back, so the command asks first.
/// </summary>
internal class DebugThirdActCommand : IClientCommand, ICommandWithDescription {
    /// <summary>
    /// How long, in seconds, the command waits to be typed again to go ahead.
    /// </summary>
    private const float ConfirmTime = 15f;

    /// <summary>
    /// The ending of the second act that leads on into the third, as the save records it.
    /// </summary>
    private const SaveSlotCompletionIcons.CompletionState ThirdActEnding =
        SaveSlotCompletionIcons.CompletionState.Act2SoulSnare;

    /// <summary>
    /// The UI, which goes back to the main menu.
    /// </summary>
    private readonly UiManager _uiManager;

    /// <summary>
    /// The connection, as the command is for a save played alone.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// When the command last asked whether to go ahead, by Time.unscaledTime.
    /// </summary>
    private float _askedAt = float.NegativeInfinity;

    public DebugThirdActCommand(UiManager uiManager, NetClient netClient) {
        _uiManager = uiManager;
        _netClient = netClient;
    }

    /// <inheritdoc />
    public string Trigger => "/0";

    /// <inheritdoc />
    public string[] Aliases => [];

    /// <inheritdoc />
    public string Description =>
        "Temporary debug: turn this save into the third act the way the game does, for good (asks first).";

    /// <inheritdoc />
    public void Execute(string[] arguments) {
        var gameManager = global::GameManager.UnsafeInstance;
        var playerData = PlayerData.instance;
        var hero = HeroController.UnsafeInstance;
        if (gameManager == null || playerData == null || hero == null ||
            gameManager.GameState is GameState.MAIN_MENU or GameState.INACTIVE or GameState.PRIMER) {
            Chat(Lang.Pick("Load a save first.", "先进入一个存档。"));
            return;
        }

        if (playerData.blackThreadWorld) {
            Chat(playerData.act3_wokeUp
                ? Lang.Pick("This save is in the third act already.", "这个存档已经在第三幕了。")
                : Lang.Pick(
                    "This save starts the third act the next time it loads: go back to the main menu and load it again.",
                    "这个存档下次读档时开始第三幕：回主菜单再读一次这个存档。"
                ));
            return;
        }

        if (_netClient.IsConnected) {
            Chat(Lang.Pick(
                "Use this in a save played alone: disconnect first.",
                "这个命令要在单人进存档时用：先断开联机。"
            ));
            return;
        }

        // Not in the middle of a room change, a death or a scene that the game won't let the player pause in, where
        // the game itself never saves
        if (gameManager.GameState != GameState.PLAYING || gameManager.IsInSceneTransition || hero.cState.dead ||
            hero.cState.transitioning || hero.cState.hazardRespawning ||
            PlayerTargetRegistry.IsPlayerDown(hero.gameObject) || playerData.disablePause) {
            Chat(Lang.Pick(
                "The game can't be saved right now. Try again when you could pause it.",
                "现在没法存档，等能暂停游戏的时候再试。"
            ));
            return;
        }

        if (Time.unscaledTime - _askedAt > ConfirmTime) {
            _askedAt = Time.unscaledTime;
            Chat(Lang.Pick(
                "Debug: this turns this save into the third act for good. Copy it with /copy first and use the copy. " +
                $"To go ahead, type /0 again within {ConfirmTime:0} seconds.",
                $"调试：这会把当前存档永久切进第三幕，回不去。建议先用 /copy 复制一份，在复制出的存档里用。确定的话 {ConfirmTime:0} 秒内再输一次 /0。"
            ));
            return;
        }

        _askedAt = float.NegativeInfinity;
        gameManager.StartBlackThreadWorld();
        playerData.CompletedEndings |= ThirdActEnding;
        playerData.LastCompletedEnding = ThirdActEnding;
        Logger.Info("Debug: turned the save into the third act the way the game's ending does; saving and going to the menu");
        Chat(Lang.Pick(
            "Debug: the save is in the third act now. Saving and going back to the main menu: load this save again, " +
            "and the game starts the third act itself.",
            "调试：存档已切进第三幕，正在存档并回到主菜单。回到菜单后再读这个存档，游戏会自己开始第三幕。"
        ));
        _uiManager.ReturnToMainMenuFromGame(true);
    }

    private static void Chat(string message) {
        UiManager.InternalChatBox.AddMessage(message);
    }
}
