using SSMP.Api.Command;
using SSMP.Api.Command.Client;
using SSMP.Ui;
using SSMP.Util;

namespace SSMP.Game.Command.Client;

/// <summary>
/// Command to open Steam's invite dialog for sending lobby invites.
/// Primarily used for private lobbies where friends can't "Join Game".
/// </summary>
internal class InviteCommand : IClientCommand, ICommandWithDescription {
    /// <inheritdoc />
    public string Trigger => "/invite";

    /// <inheritdoc />
    public string[] Aliases => ["/inv"];

    /// <inheritdoc />
    public string Description => Lang.Pick(
        "Open Steam's invite dialog to invite friends to your lobby.",
        "打开 Steam 的邀请窗口，把好友请进你的房间。"
    );

    /// <inheritdoc />
    public void Execute(string[] arguments) {
        if (!SteamManager.IsInitialized) {
            UiManager.InternalChatBox.AddMessage(Lang.Pick("Steam is not available.", "Steam 用不了。"));
            return;
        }

        if (!SteamManager.IsHostingLobby) {
            UiManager.InternalChatBox.AddMessage(Lang.Pick(
                "You must be hosting a Steam lobby to invite players.",
                "你得先开一个 Steam 房间，才能邀请别人。"
            ));
            return;
        }

        SteamManager.OpenInviteDialog();
        UiManager.InternalChatBox.AddMessage(Lang.Pick("Opening Steam invite dialog...", "正在打开 Steam 邀请窗口……"));
    }
}
