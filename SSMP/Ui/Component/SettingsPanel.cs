using System;
using System.Collections.Generic;
using SSMP.Api.Server;
using SSMP.Game.Client;
using SSMP.Game.Client.Skin;
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

    /// <summary>Which part of the page is shown.</summary>
    private Page _page;

    /// <summary>The background of the button that shows this player's own choices.</summary>
    private readonly Image _ownTabImage;

    /// <summary>The background of the button that shows the skin that this player wears.</summary>
    private readonly Image _skinTabImage;

    /// <summary>The box of the skin that this player wears.</summary>
    private readonly GameObject _skinSection;

    /// <summary>The name of the skin that this player wears.</summary>
    private readonly Text _skinNameText;

    /// <summary>The line that says how many skins there are, or that one is being put on.</summary>
    private readonly Text _skinStatusText;

    /// <summary>What the choice of a skin comes to, or why there is none.</summary>
    private readonly Text _skinNoteText;

    /// <summary>The buttons that put on the skin before and after the one worn.</summary>
    private readonly Button _skinBackButton;
    private readonly Button _skinOnButton;

    /// <summary>The line beside the colour of the cloak that says which it is, or why there is none.</summary>
    private readonly Text _cloakStatusText;

    /// <summary>What the choice of a colour of the cloak comes to, or why there is none.</summary>
    private readonly Text _cloakNoteText;

    /// <summary>The button that leaves the cloak in its own colour, and its background.</summary>
    private readonly Button _cloakOwnButton;
    private readonly Image _cloakOwnImage;

    /// <summary>The swatch of each colour of <see cref="CloakColours"/>, in the same order.</summary>
    private readonly List<(Button Button, Image Image)> _cloakSwatches = [];

    /// <summary>
    /// The colours that the cloak may be given, by their names: far apart, so that two players who chose two of them
    /// are told apart at a glance. The first is Player Color's own.
    /// </summary>
    private static readonly (string English, string Chinese, Color32 Colour)[] CloakColours = [
        ("Light blue", "浅蓝", new Color32(0x66, 0xCC, 0xFF, 0xFF)),
        ("Blue", "蓝", new Color32(0x33, 0x55, 0xFF, 0xFF)),
        ("Teal", "青", new Color32(0x22, 0xCC, 0xAA, 0xFF)),
        ("Green", "绿", new Color32(0x55, 0xDD, 0x33, 0xFF)),
        ("Yellow", "黄", new Color32(0xFF, 0xDD, 0x22, 0xFF)),
        ("Orange", "橙", new Color32(0xFF, 0x88, 0x22, 0xFF)),
        ("Purple", "紫", new Color32(0x99, 0x44, 0xFF, 0xFF)),
        ("Pink", "粉", new Color32(0xFF, 0x55, 0xAA, 0xFF))
    ];

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

        // Where a line said that all of this stays on this computer, buttons now choose between what does, the skin
        // that this player wears, and what the host of the server sets for everyone
        var ownTab = WaitingRoomPanel.CreateButton(
            GameObject.transform,
            "OwnTab",
            Lang.Pick("MINE ONLY", "只改我这边"),
            new Vector2(0.03f, 1f),
            new Vector2(0.33f, 1f),
            WaitingRoomPanel.ReadyOn,
            () => ShowPage(Page.Own),
            out _ownTabImage,
            out _
        );
        PlaceInRow(ownTab, HeaderHeight + 1f, NoteHeight - 2f);

        var skinTab = WaitingRoomPanel.CreateButton(
            GameObject.transform,
            "SkinTab",
            Lang.Pick("SKIN", "我的皮肤"),
            new Vector2(0.35f, 1f),
            new Vector2(0.53f, 1f),
            WaitingRoomPanel.ReadyOff,
            () => ShowPage(Page.Skin),
            out _skinTabImage,
            out _
        );
        PlaceInRow(skinTab, HeaderHeight + 1f, NoteHeight - 2f);

        var roomTab = WaitingRoomPanel.CreateButton(
            GameObject.transform,
            "RoomTab",
            Lang.Pick("ROOM (HOST)", "整个房间（房主定）"),
            new Vector2(0.55f, 1f),
            new Vector2(0.97f, 1f),
            WaitingRoomPanel.ReadyOff,
            () => ShowPage(Page.Room),
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

        // The skin, laid out like the n of the room's choices: a button either side of the name of the one worn
        var skinArea = CreateSection("SkinSettings", HeaderHeight + NoteHeight + SectionGap, RoomSectionHeight);
        _skinSection = skinArea.gameObject;

        WaitingRoomPanel.CreateLabel(
            skinArea,
            "SkinLabel",
            Lang.Pick("SKIN", "皮肤"),
            new Vector2(0.03f, 1f),
            new Vector2(0.40f, 1f),
            new Vector2(0f, 28f),
            new Vector2(0f, -1f),
            14,
            WaitingRoomPanel.Accent,
            TextAnchor.MiddleLeft
        );

        _skinStatusText = WaitingRoomPanel.CreateLabel(
            skinArea,
            "SkinStatus",
            "",
            new Vector2(0.40f, 1f),
            new Vector2(0.97f, 1f),
            new Vector2(0f, 28f),
            new Vector2(0f, -1f),
            13,
            Muted,
            TextAnchor.MiddleRight
        );

        var skinBack = WaitingRoomPanel.CreateButton(
            skinArea,
            "SkinBack",
            "<",
            new Vector2(0.03f, 1f),
            new Vector2(0.13f, 1f),
            WaitingRoomPanel.ReadyOff,
            () => StepSkin(-1),
            out _,
            out _
        );
        PlaceInRow(skinBack, 35f, 28f);
        _skinBackButton = skinBack.GetComponent<Button>();

        _skinNameText = WaitingRoomPanel.CreateLabel(
            skinArea,
            "SkinName",
            "",
            new Vector2(0.13f, 1f),
            new Vector2(0.87f, 1f),
            new Vector2(0f, 28f),
            new Vector2(0f, -35f),
            16,
            Color.white,
            TextAnchor.MiddleCenter
        );

        var skinOn = WaitingRoomPanel.CreateButton(
            skinArea,
            "SkinOn",
            ">",
            new Vector2(0.87f, 1f),
            new Vector2(0.97f, 1f),
            WaitingRoomPanel.ReadyOff,
            () => StepSkin(1),
            out _,
            out _
        );
        PlaceInRow(skinOn, 35f, 28f);
        _skinOnButton = skinOn.GetComponent<Button>();

        _skinNoteText = WaitingRoomPanel.CreateLabel(
            skinArea,
            "SkinNote",
            "",
            new Vector2(0.03f, 1f),
            new Vector2(0.97f, 1f),
            new Vector2(0f, 52f),
            new Vector2(0f, -67f),
            13,
            Muted,
            TextAnchor.UpperLeft
        );

        // The colour of the cloak, which Player Color draws: one press on a colour, like the colours of the name
        WaitingRoomPanel.CreateLabel(
            skinArea,
            "CloakLabel",
            Lang.Pick("CLOAK", "斗篷颜色"),
            new Vector2(0.03f, 1f),
            new Vector2(0.40f, 1f),
            new Vector2(0f, 28f),
            new Vector2(0f, -121f),
            14,
            WaitingRoomPanel.Accent,
            TextAnchor.MiddleLeft
        );

        _cloakStatusText = WaitingRoomPanel.CreateLabel(
            skinArea,
            "CloakStatus",
            "",
            new Vector2(0.40f, 1f),
            new Vector2(0.97f, 1f),
            new Vector2(0f, 28f),
            new Vector2(0f, -121f),
            13,
            Muted,
            TextAnchor.MiddleRight
        );

        var cloakOwn = WaitingRoomPanel.CreateButton(
            skinArea,
            "CloakOwn",
            Lang.Pick("Own", "原色"),
            new Vector2(0.03f, 1f),
            new Vector2(0.17f, 1f),
            WaitingRoomPanel.ReadyOff,
            () => ChooseCloak(null),
            out _cloakOwnImage,
            out _
        );
        PlaceInRow(cloakOwn, 153f, 28f);
        _cloakOwnButton = cloakOwn.GetComponent<Button>();

        for (var i = 0; i < CloakColours.Length; i++) {
            // Copied out, because the button keeps the action rather than running it
            Color colour = CloakColours[i].Colour;
            var slot = 0.78f / CloakColours.Length;
            var left = 0.19f + slot * i;

            var swatch = WaitingRoomPanel.CreateButton(
                skinArea,
                $"Cloak{CloakColours[i].English}",
                "",
                new Vector2(left + 0.006f, 1f),
                new Vector2(left + slot - 0.006f, 1f),
                Color.white,
                () => ChooseCloak(colour),
                out var swatchImage,
                out _
            );
            PlaceInRow(swatch, 153f, 28f);
            _cloakSwatches.Add((swatch.GetComponent<Button>(), swatchImage));
        }

        _cloakNoteText = WaitingRoomPanel.CreateLabel(
            skinArea,
            "CloakNote",
            "",
            new Vector2(0.03f, 1f),
            new Vector2(0.97f, 1f),
            new Vector2(0f, RoomSectionHeight - 185f),
            new Vector2(0f, -185f),
            13,
            Muted,
            TextAnchor.UpperLeft
        );

        Refresh();

        // The art of a skin takes a moment to load, and the page says when Hornet wears it
        OwnSkin.Changed += () => {
            if (_activeSelf && _page == Page.Skin) {
                RefreshSkin();
            }
        };

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
    /// Shows a part of the page.
    /// </summary>
    /// <param name="page">The part.</param>
    private void ShowPage(Page page) {
        _page = page;
        Refresh();
    }

    /// <summary>
    /// Puts on the skin before or after the one worn, the game's own look counting as one before the first.
    /// </summary>
    /// <param name="step">One to go to the next, minus one to go back.</param>
    private void StepSkin(int step) {
        if (!OwnSkin.CanChoose) {
            return;
        }

        var choices = OwnSkin.Choices();
        if (choices.Count == 0) {
            return;
        }

        var current = OwnSkin.Current(choices);
        var at = current == null ? 0 : choices.IndexOf(current) + 1;
        var count = choices.Count + 1;
        var next = ((at + step) % count + count) % count;
        OwnSkin.Choose(next == 0 ? null : choices[next - 1]);
        Refresh();
    }

    /// <summary>
    /// Has the cloak drawn in the given colour, or in its own for null.
    /// </summary>
    private void ChooseCloak(Color? colour) {
        if (!PlayerColorMod.CanChoose) {
            return;
        }

        PlayerColorMod.Choose(colour);
        Refresh();
    }

    /// <summary>
    /// Draws the skin that this player wears, while that part of the page is shown.
    /// </summary>
    private void RefreshSkin() {
        if (_page != Page.Skin) {
            return;
        }

        var canChoose = OwnSkin.CanChoose;
        var choices = canChoose ? OwnSkin.Choices() : new List<SkinPack>();
        var current = OwnSkin.Current(choices);
        _skinNameText.text = current != null ? current.Name : Lang.Pick("Game's own look", "原样");
        _skinBackButton.interactable = _skinOnButton.interactable = canChoose && choices.Count > 0;

        _skinStatusText.text = !canChoose
            ? ""
            : OwnSkin.IsDressing
                ? Lang.Pick("Putting it on...", "换装中…")
                : Lang.Pick($"{choices.Count} installed", $"装了 {choices.Count} 个");

        // What it comes to, as the notes of the other choices
        if (!canChoose) {
            _skinNoteText.text = Lang.Pick(
                "Silksong Customizer isn't installed, so no skin can be worn.",
                "没装 Silksong Customizer，穿不了皮肤。"
            );
        } else if (choices.Count == 0) {
            _skinNoteText.text = Lang.Pick(
                "No skin of Hornet was found. A skin goes in a folder of its own in Hollow Knight Silksong_Data" +
                "\\Mods\\Customizer in the game's folder.",
                "没找到大黄蜂的皮肤。皮肤放在游戏目录的 Hollow Knight Silksong_Data\\Mods\\Customizer 里，" +
                "一个皮肤一个文件夹。"
            );
        } else if (OwnSkin.SwitchedOn(choices) > 1) {
            _skinNoteText.text = Lang.Pick(
                $"Several skins are switched on, and Hornet wears \"{current!.Name}\", the first. Choose one here " +
                "to keep it alone.",
                $"有几个皮肤同时开着，穿的是排在前面的「{current!.Name}」。在这里选一下，就只开选中的那个。"
            );
        } else {
            _skinNoteText.text = Lang.Pick(
                "Worn at once, and still the next time the game starts. The other player sees it if they have the " +
                "same skin.",
                "选了马上换上，下次开游戏也还是它。队友装了同一个皮肤，才看得到你穿的。"
            );
        }

        RefreshCloak(current != null);
    }

    /// <summary>
    /// Draws the colour of the cloak that this player chose.
    /// </summary>
    /// <param name="wearingSkin">Whether this player wears a skin, which keeps its own colours.</param>
    private void RefreshCloak(bool wearingSkin) {
        var canChoose = PlayerColorMod.CanChoose;
        var chosen = canChoose ? PlayerColorMod.ChosenColour() : null;

        _cloakOwnButton.interactable = canChoose;
        _cloakOwnImage.color = canChoose && chosen == null ? WaitingRoomPanel.ReadyOn : WaitingRoomPanel.ReadyOff;

        // As the colours of the name: the one chosen at its full strength and the rest faded. Player Color keeps the
        // colour in its file in bytes, as these are
        Color32? chosen32 = chosen.HasValue ? (Color32) chosen.Value : null;
        var named = -1;
        for (var i = 0; i < _cloakSwatches.Count; i++) {
            var swatch = CloakColours[i].Colour;
            var picked = chosen32 is { } colour && colour.r == swatch.r && colour.g == swatch.g && colour.b == swatch.b;
            if (picked) {
                named = i;
            }

            _cloakSwatches[i].Button.interactable = canChoose;
            Color full = swatch;
            _cloakSwatches[i].Image.color = picked ? full : new Color(full.r, full.g, full.b, 0.3f);
        }

        _cloakStatusText.text = !PlayerColorMod.IsLoaded
            ? Lang.Pick("Player Color isn't installed", "没装 Player Color")
            : !canChoose
                ? Lang.Pick("Not fully installed", "没装全")
                : wearingSkin
                    ? Lang.Pick("Not on a skin", "穿着皮肤时不变色")
                    : chosen == null
                        ? Lang.Pick("Own colour", "原色")
                        : named >= 0
                            ? Lang.Pick(CloakColours[named].English, CloakColours[named].Chinese)
                            : Lang.Pick("Another colour", "别的颜色");

        if (!PlayerColorMod.IsLoaded) {
            _cloakNoteText.text = Lang.Pick(
                "With Player Color installed, the cloak can be given a colour here, so that the two of you are told " +
                "apart at a glance.",
                "装了 Player Color，就能在这里给斗篷换颜色，两个人一眼就分得清。"
            );
        } else if (!PlayerColorMod.HasMasks) {
            _cloakNoteText.text = Lang.Pick(
                "Player Color's masks weren't found, so the cloak keeps its colour. They go in Hollow Knight " +
                "Silksong_Data\\Mods\\Mask in the game's folder.",
                "没找到 Player Color 的 Mask 文件夹，斗篷变不了色。它要放在游戏目录的 " +
                "Hollow Knight Silksong_Data\\Mods\\Mask 里。"
            );
        } else if (!canChoose) {
            _cloakNoteText.text = Lang.Pick(
                "Player Color didn't load fully, so the cloak keeps its colour.",
                "Player Color 没有完整加载，斗篷变不了色。"
            );
        } else if (wearingSkin) {
            _cloakNoteText.text = Lang.Pick(
                "A skin keeps its own colours. The colour chosen shows on the game's own look, and is kept for it.",
                "穿着皮肤时斗篷不变色，换回“原样”才看得到；选的颜色会留着。"
            );
        } else {
            _cloakNoteText.text = Lang.Pick(
                "Worn at once, and still the next time the game starts. The other player sees it if they have " +
                "Player Color too.",
                "选了马上换上，下次开游戏也还在。队友也装了 Player Color，才看得到你的颜色。"
            );
        }
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
        _ownTabImage.color = _page == Page.Own ? WaitingRoomPanel.ReadyOn : WaitingRoomPanel.ReadyOff;
        _skinTabImage.color = _page == Page.Skin ? WaitingRoomPanel.ReadyOn : WaitingRoomPanel.ReadyOff;
        _roomTabImage.color = _page == Page.Room ? WaitingRoomPanel.ReadyOn : WaitingRoomPanel.ReadyOff;
        foreach (var section in _ownSections) {
            section.SetActive(_page == Page.Own);
        }

        _skinSection.SetActive(_page == Page.Skin);
        _roomSection.SetActive(_page == Page.Room);
        RefreshRoom();
        RefreshSkin();

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

        // What it comes to, and not how it is done: the user (2026-10-20) wanted no more on the page than that,
        // since every further word is one that someone may dispute ("不需要写做法，阐述的太多万一有人说不对很难纠缠")
        _roomNoteText.text = EnemyHealthCoop.Describe(settings);
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

    /// <summary>
    /// The parts of the page.
    /// </summary>
    private enum Page {
        /// <summary>The choices that change only how this player's own game draws things.</summary>
        Own,

        /// <summary>The skin that this player's own Hornet wears.</summary>
        Skin,

        /// <summary>The choices of the room, which the host makes for everyone.</summary>
        Room
    }
}
