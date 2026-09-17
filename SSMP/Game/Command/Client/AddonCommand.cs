using System.Linq;
using SSMP.Api.Command;
using SSMP.Api.Client;
using SSMP.Api.Command.Client;
using SSMP.Networking.Client;
using SSMP.Ui;
using SSMP.Util;

namespace SSMP.Game.Command.Client;

/// <summary>
/// Command for managing client-side addons, such as enabling and disabling them.
/// </summary>
internal class AddonCommand : IClientCommand, ICommandWithDescription {
    /// <inheritdoc />
    public string Trigger => "/addon";

    /// <inheritdoc />
    public string[] Aliases => [];

    /// <inheritdoc />
    public string Description => "Enable, disable, or list client addons.";

    /// <summary>
    /// The client addon manager instance.
    /// </summary>
    private readonly ClientAddonManager _addonManager;

    /// <summary>
    /// The net client instance.
    /// </summary>
    private readonly NetClient _netClient;

    public AddonCommand(ClientAddonManager addonManager, NetClient netClient) {
        _addonManager = addonManager;
        _netClient = netClient;
    }

    /// <inheritdoc />
    public void Execute(string[] arguments) {
        if (arguments.Length < 2) {
            SendUsage();
            return;
        }

        var action = arguments[1];

        if (action == "list") {
            string message;
            var addons = _addonManager.GetLoadedAddons();
            if (addons.Count == 0) {
                message = "No addons loaded.";
            } else {
                message = "Loaded addons: ";
                message += string.Join(
                    ", ",
                    addons.Select(addon => {
                        var msg = $"{addon.GetName()} {addon.GetVersion()}";
                        if (addon is TogglableClientAddon { Disabled: true }) {
                            msg += " (disabled)";
                        }

                        return msg;
                    })
                );
            }

            UiManager.InternalChatBox.AddMessage(message);
            return;
        }

        if ((action != "enable" && action != "disable") || arguments.Length < 3) {
            SendUsage();
            return;
        }

        if (_netClient.ConnectionStatus != ClientConnectionStatus.NotConnected) {
            UiManager.InternalChatBox.AddMessage(Lang.Pick(
                "Cannot toggle addons while connecting or connected to a server.",
                "连接中或已连上服务器时不能开关附加组件。"
            ));
            return;
        }

        if (action == "enable") {
            for (var i = 2; i < arguments.Length; i++) {
                var addonName = arguments[i];

                if (_addonManager.TryEnableAddon(addonName)) {
                    UiManager.InternalChatBox.AddMessage(Lang.Pick(
                        $"Successfully enabled '{addonName}'",
                        $"已启用「{addonName}」"
                    ));
                } else {
                    UiManager.InternalChatBox.AddMessage(Lang.Pick(
                        $"Could not enable addon '{addonName}'",
                        $"启用不了附加组件「{addonName}」"
                    ));
                }
            }
        } else if (action == "disable") {
            for (var i = 2; i < arguments.Length; i++) {
                var addonName = arguments[i];

                if (_addonManager.TryDisableAddon(addonName)) {
                    UiManager.InternalChatBox.AddMessage(Lang.Pick(
                        $"Successfully disabled '{addonName}'",
                        $"已停用「{addonName}」"
                    ));
                } else {
                    UiManager.InternalChatBox.AddMessage(Lang.Pick(
                        $"Could not disable addon '{addonName}'",
                        $"停用不了附加组件「{addonName}」"
                    ));
                }
            }
        }
    }

    /// <summary>
    /// Sends the command usage to the chat box.
    /// </summary>
    private void SendUsage() {
        UiManager.InternalChatBox.AddMessage(Lang.Pick(
            $"Usage: {Trigger} <enable|disable|list> [addon(s)]",
            $"用法：{Trigger} <enable|disable|list> [附加组件]"
        ));
    }
}
