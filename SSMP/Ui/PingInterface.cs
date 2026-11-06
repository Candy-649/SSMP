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
    /// Where the text starts, from the left of the screen: just right of the icon.
    /// </summary>
    private const float TextLeft = 40f;

    /// <summary>
    /// The maximum width of the text component, which holds the round trip and the share of packets lost.
    /// </summary>
    private const float TextWidth = 200f;

    /// <summary>
    /// The maximum height of the text component.
    /// </summary>
    private const float TextHeight = 25f;

    /// <summary>
    /// The size (width and height) of the icon displayed in front of the text.
    /// </summary>
    private const float IconSize = 20f;

    /// <summary>
    /// How long the link has to have been held up, in milliseconds, before the display shows that time instead of the
    /// round trip, when it is longer: how long nothing has arrived, or how long the oldest packet with reliable data
    /// that the other side has not said it took in has waited. The other side sends many times a second and says with
    /// each packet what it took in, so either that long is a hold-up of the link. Steam measures the round trip by
    /// what comes back and evens it out, so its number grew only seconds after the link was held up (USER 11-01
    /// "我都已经聊天框打不出字了，左上角的延迟还要好几秒后数字才变大"). Counting only the time in which nothing arrived,
    /// it did not grow at all while some things still came, late, or while only what this side sent was held up (USER
    /// 11-04 "左上角的延迟数字还是不会网络卡了马上变大").
    /// </summary>
    private const int QuietShownFrom = 500;

    /// <summary>
    /// The steps, in milliseconds, in which how long the link has been held up is shown.
    /// </summary>
    private const int QuietShownStep = 100;

    /// <summary>
    /// How often the share of packets lost is read, in seconds: it is of the last few seconds, and read every frame it
    /// would only flicker.
    /// </summary>
    private const float LossReadEvery = 0.5f;

    /// <summary>
    /// The number the display shows, so that its text is made anew only when it changes.
    /// </summary>
    private int _shown = -1;

    /// <summary>
    /// The share of packets lost that the display shows, in percent, the one read last, and when it is read next.
    /// </summary>
    private int _shownLoss = -1;

    /// <inheritdoc cref="_shownLoss" />
    private int _loss;

    /// <inheritdoc cref="_shownLoss" />
    private float _lossReadAt;

    /// <summary>
    /// For the player who hosts, the largest share of what one of the clients sends to their server that does not
    /// arrive, or null while no server of this game runs (USER 11-06 "主机要不然不显示，或者显示丢包率最高的", "不需要带
    /// 名字"). Their own game is linked to its own server and loses nothing; another player's game shows what it loses
    /// of what comes from the server.
    /// </summary>
    public Func<float?>? HostedLoss { get; set; }

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
            new Vector2(TextLeft + TextWidth / 2f, 1080f - ScreenBorderMargin),
            new Vector2(TextWidth, TextHeight),
            "",
            UiManager.NormalFontSize,
            alignment: TextAnchor.MiddleLeft
        );

        // Register on update so we can set the text to the latest round trip, or to how long the link has been held up,
        // and to the share of packets lost
        MonoBehaviourUtil.Instance.OnUpdateEvent += () => {
            if (!netClient.IsConnected) {
                return;
            }

            var updateManager = netClient.UpdateManager;
            var shown = updateManager.AverageRtt;
            var quiet = System.Math.Max(updateManager.QuietSeconds, updateManager.UntakenSeconds) * 1000;
            if (quiet >= QuietShownFrom && quiet > shown) {
                shown = System.Math.Max(shown, (int) (quiet / QuietShownStep) * QuietShownStep);
            }

            var now = Time.unscaledTime;
            if (now >= _lossReadAt) {
                _lossReadAt = now + LossReadEvery;
                _loss = Mathf.RoundToInt((HostedLoss?.Invoke() ?? updateManager.LossShare) * 100f);
            }

            // Made into text only when it changes: text made every frame is garbage every frame
            if (shown != _shown || _loss != _shownLoss) {
                _shown = shown;
                _shownLoss = _loss;
                pingTextComponent.SetText($"{shown}  {Lang.Pick("loss", "丢包")} {_loss}%");
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
