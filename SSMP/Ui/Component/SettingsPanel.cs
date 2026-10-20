using System;
using System.Collections.Generic;
using SSMP.Api.Server;
using SSMP.Game.Client;
using SSMP.Game.Settings;
using SSMP.Util;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SSMP.Ui.Component;

/// <summary>
/// What the settings page needs for the choices that the host of a server makes for everyone in it: the settings the
/// server this game is connected to sent, whether this game runs the server and whether it is connected to one, and
/// how a change of the host's reaches everyone.
/// </summary>
/// <param name="joined">The settings of the server this game is connected to, as that server sent them.</param>
/// <param name="isHosting">Whether this game runs the server.</param>
/// <param name="isConnected">Whether this game is connected to a server, its own or another's.</param>
/// <param name="sendToAll">Sends the settings of the server this game runs to everyone connected to it.</param>
internal sealed class RoomSettingsLink(
    IServerSettings joined,
    Func<bool> isHosting,
    Func<bool> isConnected,
    Action sendToAll
) {
    /// <inheritdoc cref="RoomSettingsLink"/>
    public IServerSettings Joined { get; } = joined;

    /// <inheritdoc cref="RoomSettingsLink"/>
    public Func<bool> IsHosting { get; } = isHosting;

    /// <inheritdoc cref="RoomSettingsLink"/>
    public Func<bool> IsConnected { get; } = isConnected;

    /// <inheritdoc cref="RoomSettingsLink"/>
    public Action SendToAll { get; } = sendToAll;
}

/// <summary>
/// The settings page of the multiplayer menu, in two parts. The first holds choices that only change how this
/// player's own game draws things, such as their own name, their own head on the map and the health bars of enemies:
/// none of it is sent anywhere, and the other players do not have to agree. The second holds the choices that the host
/// of the server makes for everyone in it, which are the same in every game: how tough enemies get with more players.
/// The host changes them there and everyone else sees what they are; a player who is not connected chooses what a
/// server of their own will use.
///
/// The choices about the name used to sit under the list in the waiting room, the one place in the menu where a
/// player stands still. With more of them that room got crowded, so they have a page of their own, which can be opened
/// while waiting and left again without leaving the room. It may be no taller than the waiting room, so the choices of
/// the room have a part of their own rather than a place under the others.
/// </summary>
internal class SettingsPanel : IRootedComponent {
    /// <summary>The root GameObject for this panel.</summary>
    private GameObject GameObject { get; }

    /// <inheritdoc />
    public GameObject Root => GameObject;

    /// <summary>The settings of this machine, which this page changes.</summary>
    private readonly Game.Settings.ModSettings _modSettings;

    /// <summary>The background of the heavier-name button, which says at a glance whether it is on.</summary>
    private readonly Image _boldImage;

    /// <summary>The label of the heavier-name button.</summary>
    private readonly Text _boldText;

    /// <summary>The background of the coloured-name button.</summary>
    private readonly Image _colourImage;

    /// <summary>The label of the coloured-name button.</summary>
    private readonly Text _colourText;

    /// <summary>The swatch of each colour that may be picked, by the name of that colour.</summary>
    private readonly Dictionary<string, Image> _swatches = [];

    /// <summary>The background of the map outline button.</summary>
    private readonly Image _outlineImage;

    /// <summary>The label of the map outline button.</summary>
    private readonly Text _outlineText;

    /// <summary>The background of the button of the outline in a fight.</summary>
    private readonly Image _fightOutlineImage;

    /// <summary>The label of the button of the outline in a fight.</summary>
    private readonly Text _fightOutlineText;

    /// <summary>This player's Hornet, who sits on the bottom of the menu while this page is open.</summary>
    private readonly SittingPreview _preview;

    /// <summary>The background of the health bar button.</summary>
    private readonly Image _healthBarsImage;

    /// <summary>The label of the health bar button.</summary>
    private readonly Text _healthBarsText;

    /// <summary>Whether the part with the choices of the room is shown, rather than this player's own.</summary>
    private bool _roomPage;

    /// <summary>The background of the button that shows this player's own choices.</summary>
    private readonly Image _ownTabImage;

    /// <summary>The background of the button that shows the choices of the room.</summary>
    private readonly Image _roomTabImage;

    /// <summary>The boxes of this player's own choices, shown together.</summary>
    private readonly List<GameObject> _ownSections = [];

    /// <summary>The box of the choices of the room.</summary>
    private readonly GameObject _roomSection;

    /// <summary>What the choices of the room are read from and sent through, once the game has set it.</summary>
    private RoomSettingsLink? _room;

    /// <summary>The line that says who can change the choices of the room.</summary>
    private readonly Text _roomStatusText;

    /// <summary>The button of each way enemy health can grow, with its background.</summary>
    private readonly Dictionary<EnemyHealthMode, (Button Button, Image Image)> _modeButtons = [];

    /// <summary>The row with the n of the way chosen, which the default way has none of.</summary>
    private readonly GameObject _factorRow;

    /// <summary>The button that makes n smaller.</summary>
    private readonly Button _lessButton;

    /// <summary>The button that makes n larger.</summary>
    private readonly Button _moreButton;

    /// <summary>The n of the way chosen.</summary>
    private readonly Text _factorText;

    /// <summary>What two players fight against with the way chosen.</summary>
    private readonly Text _twoPlayersText;

    /// <summary>What the way chosen does, in a sentence or two.</summary>
    private readonly Text _roomNoteText;

    /// <summary>Tracks the panel's own active state.</summary>
    private bool _activeSelf;

    /// <summary>Parent component group for visibility management.</summary>
    private readonly ComponentGroup _componentGroup;

    private const float HeaderHeight = 32f;
    private const float NoteHeight = 26f;
    private const float SectionGap = 14f;

    /// <summary>
    /// How much room the choices about this player's own name take: a row of switches over a row of colours.
    /// </summary>
    private const float NameSectionHeight = 68f;

    /// <summary>
    /// How much room the choices about outlines take: two switches over a line or two saying what they do.
    /// </summary>
    private const float MapSectionHeight = 74f;

    /// <summary>
    /// How much room the choices about enemies take: a switch over a line or two saying what it does.
    /// </summary>
    private const float EnemySectionHeight = 74f;

    /// <summary>
    /// How much room the choices of the room take: all that this player's own choices take, in their place.
    /// </summary>
    private const float RoomSectionHeight = NameSectionHeight + SectionGap + MapSectionHeight + SectionGap +
                                            EnemySectionHeight;

    /// <summary>
    /// Height of the panel. Less than that of the waiting room, so that it ends above the feedback text of the menu
    /// as well.
    /// </summary>
    public const float Height = HeaderHeight + NoteHeight + SectionGap + NameSectionHeight + SectionGap +
                                MapSectionHeight + SectionGap + EnemySectionHeight;

    private static readonly Color Muted = new(0.75f, 0.75f, 0.75f, 1f);

    /// <param name="parent">The component group of the menu.</param>
    /// <param name="position">Where the page goes.</param>
    /// <param name="size">How large it is.</param>
    /// <param name="modSettings">The settings of this machine.</param>
    /// <param name="menu">The menu the page is in, on whose bottom edge this player's Hornet sits.</param>
    /// <param name="username">The name that this player goes by, as it stands in the menu.</param>
    public SettingsPanel(
        ComponentGroup parent,
        Vector2 position,
        Vector2 size,
        Game.Settings.ModSettings modSettings,
        RectTransform menu,
        Func<string> username
    ) {
        _modSettings = modSettings;
        _preview = SittingPreview.Create(menu, modSettings, username);
        GameObject = new GameObject("SettingsPanel");
        var rect = GameObject.AddComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(position.x / 1920f, position.y / 1080f);
        rect.sizeDelta = size;
        rect.pivot = new Vector2(0.5f, 1f);

        WaitingRoomPanel.CreateLabel(
            GameObject.transform,
            "Header",
            Lang.Pick("CO-OP SETTINGS", "联机设置"),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(0f, HeaderHeight),
            Vector2.zero,
            18,
            WaitingRoomPanel.Accent,
            TextAnchor.MiddleCenter
        );

        // Where a line said that all of this stays on this computer, two buttons now choose between what does and
        // what the host of the server sets for everyone
        var ownTab = WaitingRoomPanel.CreateButton(
            GameObject.transform,
            "OwnTab",
            Lang.Pick("MINE ONLY", "只改我这边"),
            new Vector2(0.03f, 1f),
            new Vector2(0.49f, 1f),
            WaitingRoomPanel.ReadyOn,
            () => ShowPage(false),
            out _ownTabImage,
            out _
        );
        PlaceInRow(ownTab, HeaderHeight + 1f, NoteHeight - 2f);

        var roomTab = WaitingRoomPanel.CreateButton(
            GameObject.transform,
            "RoomTab",
            Lang.Pick("WHOLE ROOM (HOST)", "整个房间（房主定）"),
            new Vector2(0.51f, 1f),
            new Vector2(0.97f, 1f),
            WaitingRoomPanel.ReadyOff,
            () => ShowPage(true),
            out _roomTabImage,
            out _
        );
        PlaceInRow(roomTab, HeaderHeight + 1f, NoteHeight - 2f);

        var nameArea = CreateSection("NameSettings", HeaderHeight + NoteHeight + SectionGap, NameSectionHeight);
        _ownSections.Add(nameArea.gameObject);

        WaitingRoomPanel.CreateLabel(
            nameArea,
            "NameSettingsLabel",
            Lang.Pick("MY NAME", "我的名字"),
            new Vector2(0.03f, 1f),
            new Vector2(0.30f, 1f),
            new Vector2(0f, 28f),
            new Vector2(0f, -1f),
            14,
            WaitingRoomPanel.Accent,
            TextAnchor.MiddleLeft
        );

        WaitingRoomPanel.CreateLabel(
            nameArea,
            "NameColourLabel",
            Lang.Pick("COLOUR", "颜色"),
            new Vector2(0.03f, 1f),
            new Vector2(0.30f, 1f),
            new Vector2(0f, 28f),
            new Vector2(0f, -34f),
            14,
            Muted,
            TextAnchor.MiddleLeft
        );

        WaitingRoomPanel.CreateButton(
            nameArea,
            "BoldButton",
            "",
            new Vector2(0.32f, 0.54f),
            new Vector2(0.63f, 0.96f),
            WaitingRoomPanel.ReadyOff,
            () => {
                _modSettings.BoldOwnName = !_modSettings.BoldOwnName;
                _modSettings.Save();
                Refresh();
            },
            out _boldImage,
            out _boldText
        );

        WaitingRoomPanel.CreateButton(
            nameArea,
            "ColourButton",
            "",
            new Vector2(0.66f, 0.54f),
            new Vector2(0.97f, 0.96f),
            WaitingRoomPanel.ReadyOff,
            () => {
                _modSettings.ColourOwnName = !_modSettings.ColourOwnName;
                _modSettings.Save();
                Refresh();
            },
            out _colourImage,
            out _colourText
        );

        var colourNames = Game.Client.PlayerManager.OwnNameColourNames;
        for (var i = 0; i < colourNames.Length; i++) {
            // Copied out, because the button keeps the action rather than running it, and one shared counter would
            // have every swatch pick whatever the last one was
            var colourName = colourNames[i];
            var slot = 0.65f / colourNames.Length;
            var left = 0.32f + slot * i;

            WaitingRoomPanel.CreateButton(
                nameArea,
                $"Swatch{colourName}",
                "",
                new Vector2(left + 0.008f, 0.06f),
                new Vector2(left + slot - 0.008f, 0.46f),
                Color.white,
                () => {
                    _modSettings.OwnNameColour = colourName;
                    // Picking a colour is asking for one, so it turns the colour on as well rather than being
                    // silently stored against a switch that is off
                    _modSettings.ColourOwnName = true;
                    _modSettings.Save();
                    Refresh();
                },
                out var swatch,
                out _
            );

            _swatches[colourName] = swatch;
        }

        var mapArea = CreateSection(
            "MapSettings",
            HeaderHeight + NoteHeight + SectionGap + NameSectionHeight + SectionGap,
            MapSectionHeight
        );
        _ownSections.Add(mapArea.gameObject);

        WaitingRoomPanel.CreateLabel(
            mapArea,
            "MapSettingsLabel",
            Lang.Pick("OUTLINE", "描边"),
            new Vector2(0.03f, 1f),
            new Vector2(0.30f, 1f),
            new Vector2(0f, 28f),
            new Vector2(0f, -1f),
            14,
            WaitingRoomPanel.Accent,
            TextAnchor.MiddleLeft
        );

        // Same height as the switches above it: the top 28 of the section, between 1 and 29 down. Side by side like the
        // switches of the name, since the page may be no taller
        var outlineButton = WaitingRoomPanel.CreateButton(
            mapArea,
            "OutlineButton",
            "",
            new Vector2(0.32f, 1f),
            new Vector2(0.63f, 1f),
            WaitingRoomPanel.ReadyOff,
            () => {
                _modSettings.OutlineOwnMapIcon = !_modSettings.OutlineOwnMapIcon;
                _modSettings.Save();
                Refresh();
            },
            out _outlineImage,
            out _outlineText
        );
        PlaceInRow(outlineButton, 1f, 28f);

        var fightOutlineButton = WaitingRoomPanel.CreateButton(
            mapArea,
            "FightOutlineButton",
            "",
            new Vector2(0.66f, 1f),
            new Vector2(0.97f, 1f),
            WaitingRoomPanel.ReadyOff,
            () => {
                _modSettings.OutlineOwnHeroInFights = !_modSettings.OutlineOwnHeroInFights;
                _modSettings.Save();
                Refresh();
            },
            out _fightOutlineImage,
            out _fightOutlineText
        );
        PlaceInRow(fightOutlineButton, 1f, 28f);

        WaitingRoomPanel.CreateLabel(
            mapArea,
            "OutlineNote",
            Lang.Pick(
                "In the colour above: your map head (with the compass) and your Hornet in fights beside the other " +
                "player.",
                "用上面选的颜色：戴着罗盘时描地图上你的头像；队友在你画面里、有人在打怪时描你的大黄蜂。"
            ),
            new Vector2(0.03f, 1f),
            new Vector2(0.97f, 1f),
            new Vector2(0f, MapSectionHeight - 34f),
            new Vector2(0f, -34f),
            13,
            Muted,
            TextAnchor.UpperLeft
        );

        var enemyArea = CreateSection(
            "EnemySettings",
            HeaderHeight + NoteHeight + SectionGap + NameSectionHeight + SectionGap + MapSectionHeight + SectionGap,
            EnemySectionHeight
        );
        _ownSections.Add(enemyArea.gameObject);

        WaitingRoomPanel.CreateLabel(
            enemyArea,
            "EnemySettingsLabel",
            Lang.Pick("ENEMIES", "怪物"),
            new Vector2(0.03f, 1f),
            new Vector2(0.30f, 1f),
            new Vector2(0f, 28f),
            new Vector2(0f, -1f),
            14,
            WaitingRoomPanel.Accent,
            TextAnchor.MiddleLeft
        );

        // Laid out like the map's switch above it
        var healthBarsButton = WaitingRoomPanel.CreateButton(
            enemyArea,
            "HealthBarsButton",
            "",
            new Vector2(0.32f, 1f),
            new Vector2(0.97f, 1f),
            WaitingRoomPanel.ReadyOff,
            () => {
                _modSettings.ShowEnemyHealthBars = !_modSettings.ShowEnemyHealthBars;
                _modSettings.Save();
                Refresh();
            },
            out _healthBarsImage,
            out _healthBarsText
        );
        var healthBarsRect = healthBarsButton.GetComponent<RectTransform>();
        healthBarsRect.offsetMin = new Vector2(0f, -29f);
        healthBarsRect.offsetMax = new Vector2(0f, -1f);

        WaitingRoomPanel.CreateLabel(
            enemyArea,
            "HealthBarsNote",
            Lang.Pick(
                "A bar over each enemy that was hit, and a boss's bar along the bottom of the screen.",
                "被打过的怪头上显示血条，首领的血条显示在屏幕底部。"
            ),
            new Vector2(0.03f, 1f),
            new Vector2(0.97f, 1f),
            new Vector2(0f, EnemySectionHeight - 34f),
            new Vector2(0f, -34f),
            13,
            Muted,
            TextAnchor.UpperLeft
        );

        var roomArea = CreateSection("RoomSettings", HeaderHeight + NoteHeight + SectionGap, RoomSectionHeight);
        _roomSection = roomArea.gameObject;

        WaitingRoomPanel.CreateLabel(
            roomArea,
            "EnemyHealthLabel",
            Lang.Pick("ENEMY HEALTH", "怪物血量"),
            new Vector2(0.03f, 1f),
            new Vector2(0.40f, 1f),
            new Vector2(0f, 28f),
            new Vector2(0f, -1f),
            14,
            WaitingRoomPanel.Accent,
            TextAnchor.MiddleLeft
        );

        _roomStatusText = WaitingRoomPanel.CreateLabel(
            roomArea,
            "EnemyHealthStatus",
            "",
            new Vector2(0.40f, 1f),
            new Vector2(0.97f, 1f),
            new Vector2(0f, 28f),
            new Vector2(0f, -1f),
            13,
            Muted,
            TextAnchor.MiddleRight
        );

        // The four ways side by side, the one in use lit like a switch that is on
        EnemyHealthMode[] modes = [
            EnemyHealthMode.Default, EnemyHealthMode.Exponential, EnemyHealthMode.Linear, EnemyHealthMode.Fixed
        ];
        const float modeGap = 0.015f;
        var modeWidth = (0.94f - modeGap * (modes.Length - 1)) / modes.Length;
        for (var i = 0; i < modes.Length; i++) {
            // Copied out, for the same reason as the colours above
            var mode = modes[i];
            var left = 0.03f + (modeWidth + modeGap) * i;
            var modeButton = WaitingRoomPanel.CreateButton(
                roomArea,
                $"EnemyHealth{mode}",
                EnemyHealthCoop.ModeName(mode),
                new Vector2(left, 1f),
                new Vector2(left + modeWidth, 1f),
                WaitingRoomPanel.ReadyOff,
                () => ChooseMode(mode),
                out var modeImage,
                out _
            );
            PlaceInRow(modeButton, 35f, 28f);
            _modeButtons[mode] = (modeButton.GetComponent<Button>(), modeImage);
        }

        // A row of its own, laid over the whole box, so that it can be hidden for the default way, which has no n
        _factorRow = new GameObject("FactorRow");
        var factorRowRect = _factorRow.AddComponent<RectTransform>();
        factorRowRect.anchorMin = Vector2.zero;
        factorRowRect.anchorMax = Vector2.one;
        factorRowRect.offsetMin = Vector2.zero;
        factorRowRect.offsetMax = Vector2.zero;
        _factorRow.transform.SetParent(roomArea, false);

        WaitingRoomPanel.CreateLabel(
            _factorRow.transform,
            "FactorLabel",
            Lang.Pick("n", "倍数 n"),
            new Vector2(0.03f, 1f),
            new Vector2(0.25f, 1f),
            new Vector2(0f, 28f),
            new Vector2(0f, -69f),
            14,
            Muted,
            TextAnchor.MiddleLeft
        );

        var lessButton = WaitingRoomPanel.CreateButton(
            _factorRow.transform,
            "FactorLess",
            "-",
            new Vector2(0.26f, 1f),
            new Vector2(0.36f, 1f),
            WaitingRoomPanel.ReadyOff,
            () => StepFactor(-1),
            out _,
            out _
        );
        PlaceInRow(lessButton, 69f, 28f);
        _lessButton = lessButton.GetComponent<Button>();

        _factorText = WaitingRoomPanel.CreateLabel(
            _factorRow.transform,
            "FactorValue",
            "",
            new Vector2(0.36f, 1f),
            new Vector2(0.52f, 1f),
            new Vector2(0f, 28f),
            new Vector2(0f, -69f),
            16,
            Color.white,
            TextAnchor.MiddleCenter
        );

        var moreButton = WaitingRoomPanel.CreateButton(
            _factorRow.transform,
            "FactorMore",
            "+",
            new Vector2(0.52f, 1f),
            new Vector2(0.62f, 1f),
            WaitingRoomPanel.ReadyOff,
            () => StepFactor(1),
            out _,
            out _
        );
        PlaceInRow(moreButton, 69f, 28f);
        _moreButton = moreButton.GetComponent<Button>();

        _twoPlayersText = WaitingRoomPanel.CreateLabel(
            _factorRow.transform,
            "TwoPlayers",
            "",
            new Vector2(0.64f, 1f),
            new Vector2(0.97f, 1f),
            new Vector2(0f, 28f),
            new Vector2(0f, -69f),
            13,
            Muted,
            TextAnchor.MiddleRight
        );

        _roomNoteText = WaitingRoomPanel.CreateLabel(
            roomArea,
            "EnemyHealthNote",
            "",
            new Vector2(0.03f, 1f),
            new Vector2(0.97f, 1f),
            new Vector2(0f, RoomSectionHeight - 105f),
            new Vector2(0f, -105f),
            13,
            Muted,
            TextAnchor.UpperLeft
        );

        Refresh();

        _componentGroup = parent;
        _activeSelf = false;
        parent.AddComponent(this);
        GameObject.transform.SetParent(UiManager.UiGameObject!.transform, false);
        Object.DontDestroyOnLoad(GameObject);
        GameObject.SetActive(false);
    }

    /// <summary>
    /// Makes the box that one group of choices is laid out in, across the panel at the given depth from its top.
    /// </summary>
    /// <param name="name">The name of the box.</param>
    /// <param name="top">How far below the top of the panel the box begins.</param>
    /// <param name="height">How tall the box is.</param>
    /// <returns>The transform to put the choices under.</returns>
    private Transform CreateSection(string name, float top, float height) {
        var section = new GameObject(name);
        var sectionRect = section.AddComponent<RectTransform>();
        sectionRect.anchorMin = new Vector2(0f, 1f);
        sectionRect.anchorMax = new Vector2(1f, 1f);
        sectionRect.pivot = new Vector2(0.5f, 1f);
        sectionRect.anchoredPosition = new Vector2(0f, -top);
        sectionRect.sizeDelta = new Vector2(0f, height);
        section.transform.SetParent(GameObject.transform, false);

        return section.transform;
    }

    /// <summary>
    /// Puts a button made across the given part of the width at the given depth below the top of what it is in, at the
    /// given height.
    /// </summary>
    private static void PlaceInRow(GameObject button, float top, float height) {
        var rect = button.GetComponent<RectTransform>();
        rect.offsetMin = new Vector2(0f, -(top + height));
        rect.offsetMax = new Vector2(0f, -top);
    }

    /// <summary>
    /// Gives the page what the choices of the room are read from and sent through. What the server sends is drawn as
    /// it comes, while the page is open.
    /// </summary>
    /// <param name="link">The link.</param>
    public void SetRoomSettings(RoomSettingsLink link) {
        _room = link;
        link.Joined.ChangeEvent += _ => ThreadUtil.RunActionOnMainThread(() => {
                if (_activeSelf) {
                    Refresh();
                }
            }
        );
        Refresh();
    }

    /// <summary>
    /// Shows this player's own choices, or those of the room.
    /// </summary>
    /// <param name="room">Whether to show those of the room.</param>
    private void ShowPage(bool room) {
        _roomPage = room;
        Refresh();
    }

    /// <summary>
    /// The settings that a server this game runs uses, which this machine keeps: the ones the host changes here.
    /// </summary>
    private ServerSettings OwnServerSettings => _modSettings.ServerSettings ??= new ServerSettings();

    /// <summary>
    /// Whether this player can change the choices of the room here: while they run the server, or are connected to
    /// none and choose for one of their own. Connected to another player's server, they see that host's choices.
    /// </summary>
    private bool CanChangeRoom => _room == null || _room.IsHosting() || !_room.IsConnected();

    /// <summary>
    /// The choices of the room that are shown: this machine's own where they can be changed, the host's otherwise.
    /// </summary>
    private IServerSettings ShownRoomSettings => CanChangeRoom ? OwnServerSettings : _room!.Joined;

    /// <summary>
    /// Chooses how enemy health grows with the players.
    /// </summary>
    /// <param name="mode">The way.</param>
    private void ChooseMode(EnemyHealthMode mode) {
        if (!CanChangeRoom) {
            return;
        }

        OwnServerSettings.EnemyHealthMode = (byte) mode;
        RoomChanged();
    }

    /// <summary>
    /// Makes the n of the way chosen a tenth larger or smaller, within its range. Each way keeps its own n, so going
    /// back to one finds it as it was left.
    /// </summary>
    /// <param name="step">Tenths to add, less than none to take away.</param>
    private void StepFactor(int step) {
        if (!CanChangeRoom) {
            return;
        }

        var settings = OwnServerSettings;
        var mode = (EnemyHealthMode) settings.EnemyHealthMode;
        var (min, max) = EnemyHealthCoop.FactorRange(mode);
        var tenths = (byte) Mathf.Clamp(EnemyHealthCoop.FactorTenths(settings, mode) + step, min, max);
        switch (mode) {
            case EnemyHealthMode.Exponential:
                settings.EnemyHealthExponentialFactor = tenths;
                break;
            case EnemyHealthMode.Linear:
                settings.EnemyHealthLinearFactor = tenths;
                break;
            case EnemyHealthMode.Fixed:
                settings.EnemyHealthFixedFactor = tenths;
                break;
            default:
                return;
        }

        RoomChanged();
    }

    /// <summary>
    /// Keeps a change of the choices of the room and, while this game runs the server, sends it to everyone in it.
    /// </summary>
    private void RoomChanged() {
        _modSettings.Save();
        _room?.SendToAll();
        Refresh();
    }

    /// <summary>
    /// Draws the choices as they currently stand.
    /// </summary>
    private void Refresh() {
        _ownTabImage.color = _roomPage ? WaitingRoomPanel.ReadyOff : WaitingRoomPanel.ReadyOn;
        _roomTabImage.color = _roomPage ? WaitingRoomPanel.ReadyOn : WaitingRoomPanel.ReadyOff;
        foreach (var section in _ownSections) {
            section.SetActive(!_roomPage);
        }

        _roomSection.SetActive(_roomPage);
        RefreshRoom();

        var bold = _modSettings.BoldOwnName;
        _boldImage.color = bold ? WaitingRoomPanel.ReadyOn : WaitingRoomPanel.ReadyOff;
        _boldText.text = bold
            ? Lang.Pick("BOLD: ON", "加粗：开")
            : Lang.Pick("BOLD: OFF", "加粗：关");

        var coloured = _modSettings.ColourOwnName;
        _colourImage.color = coloured ? WaitingRoomPanel.ReadyOn : WaitingRoomPanel.ReadyOff;
        _colourText.text = coloured
            ? Lang.Pick("COLOUR: ON", "换色：开")
            : Lang.Pick("COLOUR: OFF", "换色：关");

        var outlined = _modSettings.OutlineOwnMapIcon;
        _outlineImage.color = outlined ? WaitingRoomPanel.ReadyOn : WaitingRoomPanel.ReadyOff;
        _outlineText.text = outlined
            ? Lang.Pick("MAP: ON", "地图头像：开")
            : Lang.Pick("MAP: OFF", "地图头像：关");

        var fightOutlined = _modSettings.OutlineOwnHeroInFights;
        _fightOutlineImage.color = fightOutlined ? WaitingRoomPanel.ReadyOn : WaitingRoomPanel.ReadyOff;
        _fightOutlineText.text = fightOutlined
            ? Lang.Pick("FIGHTS: ON", "战斗中：开")
            : Lang.Pick("FIGHTS: OFF", "战斗中：关");

        var healthBars = _modSettings.ShowEnemyHealthBars;
        _healthBarsImage.color = healthBars ? WaitingRoomPanel.ReadyOn : WaitingRoomPanel.ReadyOff;
        _healthBarsText.text = healthBars
            ? Lang.Pick("HEALTH BARS: ON", "血条：开")
            : Lang.Pick("HEALTH BARS: OFF", "血条：关");

        foreach (var pair in _swatches) {
            if (!Game.Client.PlayerManager.OwnNameColours.TryGetValue(pair.Key, out var colour)) {
                continue;
            }

            // The one in use is shown at its full strength and the rest are faded. A tick or an outline would need
            // a colour of its own, and a colour laid over a row of colours is the one thing that cannot be read. In
            // use means by the name or by an outline, which take the same colour.
            var picked = (coloured || outlined || fightOutlined) &&
                         string.Equals(pair.Key, _modSettings.OwnNameColour, StringComparison.OrdinalIgnoreCase);
            pair.Value.color = picked ? colour : new Color(colour.r, colour.g, colour.b, 0.3f);
        }

        _preview.Refresh();
    }

    /// <summary>
    /// Draws the choices of the room as they currently stand, and whether this player can change them.
    /// </summary>
    private void RefreshRoom() {
        var settings = ShownRoomSettings;
        var canChange = CanChangeRoom;
        var mode = (EnemyHealthMode) settings.EnemyHealthMode;

        _roomStatusText.text = _room != null && _room.IsHosting()
            ? Lang.Pick("You host: it reaches everyone", "你是房主，改了对所有人生效")
            : canChange
                ? Lang.Pick("Used when you host", "你开房时按这个来")
                : Lang.Pick("Set by the host", "房主设的，你这边只能看");

        foreach (var pair in _modeButtons) {
            pair.Value.Image.color = pair.Key == mode ? WaitingRoomPanel.ReadyOn : WaitingRoomPanel.ReadyOff;
            pair.Value.Button.interactable = canChange;
        }

        var hasFactor = mode is EnemyHealthMode.Exponential or EnemyHealthMode.Linear or EnemyHealthMode.Fixed;
        _factorRow.SetActive(hasFactor);
        if (hasFactor) {
            var (min, max) = EnemyHealthCoop.FactorRange(mode);
            var tenths = Mathf.Clamp(EnemyHealthCoop.FactorTenths(settings, mode), min, max);
            _factorText.text = (tenths / 10f).ToString("0.0");
            _lessButton.interactable = canChange && tenths > min;
            _moreButton.interactable = canChange && tenths < max;

            var two = EnemyHealthCoop.HealthMultiplier(settings, 1);
            _twoPlayersText.text = Lang.Pick($"two players: x{two:0.##}", $"两个人 ×{two:0.##}");
        }

        _roomNoteText.text = EnemyHealthCoop.Describe(settings) + "\n" + Lang.Pick(
            "It is the damage you deal that is cut down: an enemy's own health and when it changes phase stay as they " +
            "are, and what dies to one blow still does. Everyone connected to the server counts.",
            "做法是按比例减少你们打出的伤害：怪的血量数字和换阶段都不变，一下就能打死的怪照样一下打死。人数按连上服务" +
            "器的人算。"
        );
    }

    /// <summary>Shows the panel, with the choices of the room as they stand now.</summary>
    public void Show() {
        _activeSelf = true;
        Refresh();
        GameObject.SetActive(_componentGroup.IsActive());
        ShowPreviewWithPage();
    }

    /// <summary>Hides the panel.</summary>
    public void Hide() {
        _activeSelf = false;
        GameObject.SetActive(false);
        ShowPreviewWithPage();
    }

    /// <inheritdoc />
    public void SetGroupActive(bool groupActive) {
        if (GameObject == null) return;
        GameObject.SetActive(_activeSelf && groupActive);
        ShowPreviewWithPage();
    }

    /// <inheritdoc />
    public void SetActive(bool active) {
        _activeSelf = active;
        GameObject.SetActive(_activeSelf && _componentGroup.IsActive());
        ShowPreviewWithPage();
    }

    /// <summary>
    /// Has this player's Hornet sit on the menu while the page is shown, and let her go while it is not. She sits on
    /// the menu rather than in the page, which ends above its bottom edge.
    /// </summary>
    private void ShowPreviewWithPage() {
        if (_preview != null) {
            _preview.gameObject.SetActive(GameObject.activeSelf);
        }
    }

    /// <inheritdoc />
    public Vector2 GetPosition() {
        var position = GameObject.GetComponent<RectTransform>().anchorMin;
        return new Vector2(position.x * 1920f, position.y * 1080f);
    }

    /// <inheritdoc />
    public void SetPosition(Vector2 position) {
        var rectTransform = GameObject.GetComponent<RectTransform>();
        rectTransform.anchorMin = rectTransform.anchorMax = new Vector2(position.x / 1920f, position.y / 1080f);
    }

    /// <inheritdoc />
    public Vector2 GetSize() => GameObject.GetComponent<RectTransform>().sizeDelta;
}
