using System;
using SSMP.Game.Settings;
using SSMP.Networking.Client;
using SSMP.Ui.Component;
using SSMP.Ui.Resources;
using SSMP.Util;
using UnityEngine;

namespace SSMP.Ui;

/// <summary>
/// Class for creating and managing the ping interface.
/// </summary>
internal class PingInterface {
    /// <summary>
    /// The margin between the image and text, and the borders of the screen.
    /// </summary>
    private const float ScreenBorderMargin = 20f;

    /// <summary>
    /// The margin between the icon and the text.
    /// </summary>
    private const float IconTextMargin = 25f;

    /// <summary>
    /// The maximum width of the text component.
    /// </summary>
    private const float TextWidth = 50f;

    /// <summary>
    /// The maximum height of the text component.
    /// </summary>
    private const float TextHeight = 25f;

    /// <summary>
    /// The size (width and height) of the icon displayed in front of the text.
    /// </summary>
    private const float IconSize = 20f;

    /// <summary>
    /// How long nothing has to have arrived, in milliseconds, before the display shows that time instead of the round
    /// trip, when it is longer. The other side sends many times a second, so this long is a gap in the link. Steam
    /// measures the round trip by what comes back, so while nothing does, the round trip it reports stands still at the
    /// last one measured, and the number grew only seconds after the link had stopped (USER 11-01
    /// "我都已经聊天框打不出字了，左上角的延迟还要好几秒后数字才变大").
    /// </summary>
    private const int QuietShownFrom = 500;

    /// <summary>
    /// The steps, in milliseconds, in which how long nothing has arrived is shown.
    /// </summary>
    private const int QuietShownStep = 100;

    /// <summary>
    /// The number the display shows, so that its text is made anew only when it changes.
    /// </summary>
    private int _shown = -1;

    /// <summary>
    /// The component group for the ping display.
    /// </summary>
    private readonly ComponentGroup _pingComponentGroup;

    /// <summary>
    /// The mod settings.
    /// </summary>
    private readonly ModSettings _modSettings;

    /// <summary>
    /// The net client instance for retrieving the current ping.
    /// </summary>
    private readonly NetClient _netClient;

    public PingInterface(
        ComponentGroup pingComponentGroup,
        ModSettings modSettings,
        NetClient netClient
    ) {
        _pingComponentGroup = pingComponentGroup;
        _modSettings = modSettings;
        _netClient = netClient;

        // Since we are initially not connected, we disable the object by default
        pingComponentGroup.SetActive(false);

        if (TextureManager.NetworkIcon == null) {
            throw new Exception("Network icon is not defined, cannot create ping interface");
        }

        // ReSharper disable once ObjectCreationAsStatement
        new ImageComponent(
            pingComponentGroup,
            new Vector2(
                ScreenBorderMargin, 1080f - ScreenBorderMargin),
            new Vector2(IconSize, IconSize),
            TextureManager.NetworkIcon
        );

        var pingTextComponent = new TextComponent(
            pingComponentGroup,
            new Vector2(
                ScreenBorderMargin + IconSize + IconTextMargin, 1080f - ScreenBorderMargin),
            new Vector2(TextWidth, TextHeight),
            "",
            UiManager.NormalFontSize,
            alignment: TextAnchor.MiddleLeft
        );

        // Register on update so we can set the text to the latest round trip, or to how long nothing has arrived
        MonoBehaviourUtil.Instance.OnUpdateEvent += () => {
            if (!netClient.IsConnected) {
                return;
            }

            var updateManager = netClient.UpdateManager;
            var shown = updateManager.AverageRtt;
            var quiet = updateManager.QuietSeconds * 1000;
            if (quiet >= QuietShownFrom && quiet > shown) {
                shown = System.Math.Max(shown, (int) (quiet / QuietShownStep) * QuietShownStep);
            }

            // Made into text only when it changes: text made every frame is garbage every frame
            if (shown != _shown) {
                _shown = shown;
                pingTextComponent.SetText(shown.ToString());
            }
        };
    }

    /// <summary>
    /// Set whether the display is enabled or not.
    /// </summary>
    /// <param name="enabled">Whether the display should be enabled.</param>
    public void SetEnabled(bool enabled) {
        _pingComponentGroup.SetActive(enabled && _netClient.IsConnected && _modSettings.DisplayPing);
    }
}
