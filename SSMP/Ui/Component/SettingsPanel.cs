using System;
using System.Collections.Generic;
using System.Globalization;
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

    /// <summary>
    /// The button that has the outlines take the colour of the cloak, shown only where Player Color is installed.
    /// </summary>
    private readonly GameObject _outlineCloakButton;

    /// <summary>The background of the button that has the outlines take the colour of the cloak.</summary>
    private readonly Image _outlineCloakImage;

    /// <summary>The label of the button that has the outlines take the colour of the cloak.</summary>
    private readonly Text _outlineCloakText;

    /// <summary>The note under the outline buttons, which says what they outline and in what colour.</summary>
    private readonly Text _outlineNoteText;

    /// <summary>
    /// The page of this player's own choices, which scrolls down where it is taller than the panel, and the boxes of
    /// the outlines and of enemies on it, laid out as the outlines take a row more or less (see
    /// <see cref="LayOutOwnPage"/>).
    /// </summary>
    private readonly RectTransform _ownContent;
    private readonly RectTransform _mapArea;
    private readonly RectTransform _enemyArea;

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

    /// <summary>The row of what this player's slashes are drawn from, hidden where there is no skin to choose.</summary>
    private readonly GameObject _slashRow;

    /// <summary>The name of what this player's slashes are drawn from.</summary>
    private readonly Text _slashNameText;

    /// <summary>The buttons that draw the slashes from the choice before and after the one taken.</summary>
    private readonly Button _slashBackButton;
    private readonly Button _slashOnButton;

    /// <summary>What goes on the skin's part of the page, which scrolls, and its height set as it is laid out.</summary>
    private readonly RectTransform _skinContent;

    /// <summary>
    /// The rows of the skin's part of the page, laid out from its top as it is drawn (see <see cref="LayOutSkinPage"/>):
    /// the skin's name and status, the skin, the colour of the cloak's name and status, its colours to press, its strips
    /// and its code.
    /// </summary>
    private readonly RectTransform _skinHeadRow;
    private readonly RectTransform _skinChoiceRow;
    private readonly RectTransform _cloakHeadRow;
    private readonly RectTransform _cloakPresetRow;
    private readonly RectTransform _hueRow;
    private readonly RectTransform _lightRow;
    private readonly RectTransform _codeRow;

    /// <summary>Whether this player wears a skin, which keeps its own colours, as the skin's part last drew it.</summary>
    private bool _wearingSkin;

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
    /// The strip of every hue that the cloak may be given and the strip of how light it is, from black to white, with
    /// the picture of each; that of the second is drawn again for each hue.
    /// </summary>
    private readonly ColourStrip _hueStrip;
    private readonly RawImage _hueImage;
    private readonly ColourStrip _lightStrip;
    private readonly RawImage _lightImage;
    private readonly Texture2D _lightTexture;
    private readonly Color32[] _lightPixels = new Color32[LightTexels];

    /// <summary>The hue and saturation that the strip of how light the cloak is was last drawn for.</summary>
    private (float Hue, float Saturation)? _lightDrawnFor;

    /// <summary>
    /// What the controls of the colour of the cloak were last linked for, for a gamepad: where the colour chosen is in
    /// the row of colours, and whether the row of the slashes is over them.
    /// </summary>
    private (int Pressed, bool OverSlashes)? _linkedFor;

    /// <summary>The box that takes the code of a colour, and the swatch beside it of the colour chosen.</summary>
    private readonly InputField _cloakCodeField;
    private readonly Image _cloakChosenImage;

    /// <summary>
    /// Whether a code is being typed into the box, and the colour that the cloak was drawn in as it began, or null for
    /// its own, which the escape key goes back to.
    /// </summary>
    private bool _codeTyping;
    private Color? _colourBeforeCode;

    /// <summary>
    /// The hue, saturation and lightness of the colour of the cloak, as the strips show it, each from 0 to 1. They are
    /// kept apart from the colour, which has no hue while it is grey, black or white: a strip moved there keeps the hue
    /// it had.
    /// </summary>
    private float _hue;
    private float _saturation = 1f;
    private float _lightness = 0.5f;

    /// <summary>
    /// The colour that the strips show, as Player Color keeps it; a colour chosen elsewhere moves them to it.
    /// </summary>
    private Color32? _stripColour;

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

    /// <summary>How tall a row of buttons or of a name and its status is on the skin's part of the page.</summary>
    private const float RowHeight = 28f;

    /// <summary>How wide the bar at the right of a page that scrolls is, which shows only while it can scroll.</summary>
    private const float ScrollbarWidth = 6f;

    /// <summary>How tall each strip of the cloak's colour is.</summary>
    private const float StripHeight = 22f;

    /// <summary>How many colours each strip is drawn with, from one end to the other.</summary>
    private const int HueTexels = 128;
    private const int LightTexels = 64;

    /// <summary>
    /// How much room the choices about this player's own name take: a row of switches over a row of colours.
    /// </summary>
    private const float NameSectionHeight = 68f;

    /// <summary>
    /// How much room the choices about outlines take: two switches over a line or two saying what they do, and with
    /// Player Color installed a row of <see cref="OutlineRowStep"/> more, for the switch of the cloak's colour.
    /// </summary>
    private const float MapSectionHeight = 74f;

    /// <summary>
    /// How far each row of switches of the outlines lies below the one above it.
    /// </summary>
    private const float OutlineRowStep = 34f;

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
        SlashSkin.Start(modSettings);
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

        // This player's own choices, on a part that scrolls down where it is taller than the page, as the outlines take
        // a row more where the colour of the cloak can be followed (USER 10-24: "放不下就滚动向下呐")
        var ownArea = CreateSection("OwnSettings", HeaderHeight + NoteHeight + SectionGap, RoomSectionHeight);
        _ownSections.Add(ownArea.gameObject);
        _ownContent = CreateScrollingPage(ownArea);

        var nameArea = CreateSection("NameSettings", 0f, NameSectionHeight, _ownContent);

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

        var mapArea = CreateSection("MapSettings", NameSectionHeight + SectionGap, MapSectionHeight, _ownContent);
        _mapArea = (RectTransform) mapArea;

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

        // A row under them, where Player Color is installed: the outlines may take the colour that it gives the
        // cloak (USER 10-25: "加一个按钮在高光这里，如果玩家装了player color这个mod，就显示这个按钮")
        _outlineCloakButton = WaitingRoomPanel.CreateButton(
            mapArea,
            "OutlineCloakButton",
            "",
            new Vector2(0.32f, 1f),
            new Vector2(0.97f, 1f),
            WaitingRoomPanel.ReadyOff,
            () => {
                _modSettings.OutlineInCloakColour = !_modSettings.OutlineInCloakColour;
                _modSettings.Save();
                Refresh();
            },
            out _outlineCloakImage,
            out _outlineCloakText
        );
        PlaceInRow(_outlineCloakButton, OutlineRowStep + 1f, 28f);

        // Placed as the page is laid out, under the one row of buttons or the two
        _outlineNoteText = WaitingRoomPanel.CreateLabel(
            mapArea,
            "OutlineNote",
            "",
            new Vector2(0.03f, 1f),
            new Vector2(0.97f, 1f),
            new Vector2(0f, MapSectionHeight - OutlineRowStep),
            new Vector2(0f, -OutlineRowStep),
            13,
            Muted,
            TextAnchor.UpperLeft
        );

        var enemyArea = CreateSection(
            "EnemySettings",
            NameSectionHeight + SectionGap + MapSectionHeight + SectionGap,
            EnemySectionHeight,
            _ownContent
        );
        _enemyArea = (RectTransform) enemyArea;

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

        // What this player's Hornet looks like: the skin, the slashes and the colour of the cloak, on one part that
        // scrolls down where it is taller than the page (USER 10-24: "放不下就滚动向下呐")
        var skinArea = CreateSection("SkinSettings", HeaderHeight + NoteHeight + SectionGap, RoomSectionHeight);
        _skinSection = skinArea.gameObject;
        _skinContent = CreateScrollingPage(skinArea);

        // The skin, laid out like the n of the room's choices: a button either side of the name of the one worn
        _skinHeadRow = CreateRow(_skinContent, "SkinHead", RowHeight);
        WaitingRoomPanel.CreateLabel(
            _skinHeadRow,
            "SkinLabel",
            Lang.Pick("SKIN", "皮肤"),
            new Vector2(0.03f, 1f),
            new Vector2(0.40f, 1f),
            new Vector2(0f, RowHeight),
            new Vector2(0f, -1f),
            14,
            WaitingRoomPanel.Accent,
            TextAnchor.MiddleLeft
        );

        _skinStatusText = WaitingRoomPanel.CreateLabel(
            _skinHeadRow,
            "SkinStatus",
            "",
            new Vector2(0.40f, 1f),
            new Vector2(0.97f, 1f),
            new Vector2(0f, RowHeight),
            new Vector2(0f, -1f),
            13,
            Muted,
            TextAnchor.MiddleRight
        );

        _skinChoiceRow = CreateRow(_skinContent, "SkinChoice", RowHeight);
        var skinBack = WaitingRoomPanel.CreateButton(
            _skinChoiceRow,
            "SkinBack",
            "<",
            new Vector2(0.03f, 1f),
            new Vector2(0.13f, 1f),
            WaitingRoomPanel.ReadyOff,
            () => StepSkin(-1),
            out _,
            out _
        );
        PlaceInRow(skinBack, 0f, RowHeight);
        _skinBackButton = skinBack.GetComponent<Button>();

        _skinNameText = WaitingRoomPanel.CreateLabel(
            _skinChoiceRow,
            "SkinName",
            "",
            new Vector2(0.13f, 1f),
            new Vector2(0.87f, 1f),
            new Vector2(0f, RowHeight),
            Vector2.zero,
            16,
            Color.white,
            TextAnchor.MiddleCenter
        );

        var skinOn = WaitingRoomPanel.CreateButton(
            _skinChoiceRow,
            "SkinOn",
            ">",
            new Vector2(0.87f, 1f),
            new Vector2(0.97f, 1f),
            WaitingRoomPanel.ReadyOff,
            () => StepSkin(1),
            out _,
            out _
        );
        PlaceInRow(skinOn, 0f, RowHeight);
        _skinOnButton = skinOn.GetComponent<Button>();

        // What the slashes are drawn from, laid out as the skin over it, with its name on the row: the skin worn, the
        // game's own art, or another skin of Hornet's, whose slashes she wears with the rest of the skin worn
        var slashRow = CreateRow(_skinContent, "SlashRow", RowHeight);
        _slashRow = slashRow.gameObject;

        WaitingRoomPanel.CreateLabel(
            slashRow,
            "SlashLabel",
            Lang.Pick("SLASH", "刀光"),
            new Vector2(0.03f, 1f),
            new Vector2(0.20f, 1f),
            new Vector2(0f, RowHeight),
            Vector2.zero,
            14,
            WaitingRoomPanel.Accent,
            TextAnchor.MiddleLeft
        );

        var slashBack = WaitingRoomPanel.CreateButton(
            slashRow,
            "SlashBack",
            "<",
            new Vector2(0.20f, 1f),
            new Vector2(0.30f, 1f),
            WaitingRoomPanel.ReadyOff,
            () => StepSlashes(-1),
            out _,
            out _
        );
        PlaceInRow(slashBack, 0f, RowHeight);
        _slashBackButton = slashBack.GetComponent<Button>();

        _slashNameText = WaitingRoomPanel.CreateLabel(
            slashRow,
            "SlashName",
            "",
            new Vector2(0.30f, 1f),
            new Vector2(0.87f, 1f),
            new Vector2(0f, RowHeight),
            Vector2.zero,
            15,
            Color.white,
            TextAnchor.MiddleCenter
        );

        var slashOn = WaitingRoomPanel.CreateButton(
            slashRow,
            "SlashOn",
            ">",
            new Vector2(0.87f, 1f),
            new Vector2(0.97f, 1f),
            WaitingRoomPanel.ReadyOff,
            () => StepSlashes(1),
            out _,
            out _
        );
        PlaceInRow(slashOn, 0f, RowHeight);
        _slashOnButton = slashOn.GetComponent<Button>();

        _skinNoteText = WaitingRoomPanel.CreateLabel(
            _skinContent,
            "SkinNote",
            "",
            new Vector2(0.03f, 1f),
            new Vector2(0.97f, 1f),
            Vector2.zero,
            Vector2.zero,
            13,
            Muted,
            TextAnchor.UpperLeft
        );

        // The colour of the cloak, which Player Color draws: the colours of before, one press each like the colours of
        // the name, then a strip of every hue, a strip of how light, from black to white, and a box that takes the code
        // of any colour (USER 10-24: "只能在几个里面换太局限了", "或者加个色盘？色带？")
        _cloakHeadRow = CreateRow(_skinContent, "CloakHead", RowHeight);
        WaitingRoomPanel.CreateLabel(
            _cloakHeadRow,
            "CloakLabel",
            Lang.Pick("CLOAK", "斗篷颜色"),
            new Vector2(0.03f, 1f),
            new Vector2(0.40f, 1f),
            new Vector2(0f, RowHeight),
            Vector2.zero,
            14,
            WaitingRoomPanel.Accent,
            TextAnchor.MiddleLeft
        );

        _cloakStatusText = WaitingRoomPanel.CreateLabel(
            _cloakHeadRow,
            "CloakStatus",
            "",
            new Vector2(0.40f, 1f),
            new Vector2(0.97f, 1f),
            new Vector2(0f, RowHeight),
            Vector2.zero,
            13,
            Muted,
            TextAnchor.MiddleRight
        );

        _cloakPresetRow = CreateRow(_skinContent, "CloakPresets", RowHeight);
        var cloakOwn = WaitingRoomPanel.CreateButton(
            _cloakPresetRow,
            "CloakOwn",
            Lang.Pick("Own", "原色"),
            new Vector2(0.03f, 1f),
            new Vector2(0.17f, 1f),
            WaitingRoomPanel.ReadyOff,
            () => ChooseCloak(null),
            out _cloakOwnImage,
            out _
        );
        PlaceInRow(cloakOwn, 0f, RowHeight);
        _cloakOwnButton = cloakOwn.GetComponent<Button>();

        for (var i = 0; i < CloakColours.Length; i++) {
            // Copied out, because the button keeps the action rather than running it
            Color colour = CloakColours[i].Colour;
            var slot = 0.78f / CloakColours.Length;
            var left = 0.19f + slot * i;

            var swatch = WaitingRoomPanel.CreateButton(
                _cloakPresetRow,
                $"Cloak{CloakColours[i].English}",
                "",
                new Vector2(left + 0.006f, 1f),
                new Vector2(left + slot - 0.006f, 1f),
                Color.white,
                () => ChooseCloak(colour),
                out var swatchImage,
                out _
            );
            PlaceInRow(swatch, 0f, RowHeight);
            _cloakSwatches.Add((swatch.GetComponent<Button>(), swatchImage));
        }

        _hueRow = CreateRow(_skinContent, "CloakHueRow", StripHeight);
        _hueStrip = CreateStrip(
            _hueRow,
            "CloakHue",
            Lang.Pick("Colour", "颜色"),
            0f,
            HueTexture(),
            out _hueImage
        );
        _hueStrip.onValueChanged.AddListener(ChooseHue);

        _lightTexture = new Texture2D(LightTexels, 1, TextureFormat.RGBA32, false) {
            name = "SSMP cloak lightness",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        _lightRow = CreateRow(_skinContent, "CloakLightRow", StripHeight);
        _lightStrip = CreateStrip(
            _lightRow,
            "CloakLight",
            Lang.Pick("Shade", "深浅"),
            0f,
            _lightTexture,
            out _lightImage
        );
        _lightStrip.onValueChanged.AddListener(ChooseLightness);

        // Player Color writes its file once a colour moved along a strip settles, rather than at every step of the way
        _hueStrip.Settled = _lightStrip.Settled = PlayerColorMod.Settle;

        _codeRow = CreateRow(_skinContent, "CloakCodeRow", RowHeight);
        WaitingRoomPanel.CreateLabel(
            _codeRow,
            "CloakCodeLabel",
            Lang.Pick("Code", "代码"),
            new Vector2(0.03f, 1f),
            new Vector2(0.19f, 1f),
            new Vector2(0f, RowHeight),
            Vector2.zero,
            13,
            Muted,
            TextAnchor.MiddleLeft
        );

        _cloakCodeField = CreateCodeField(_codeRow, 0f);
        _cloakCodeField.onValueChanged.AddListener(text => {
                if (!_codeTyping) {
                    _codeTyping = true;
                    _colourBeforeCode = PlayerColorMod.CanChoose ? PlayerColorMod.ChosenColour() : null;
                }

                // Taken as soon as the six digits of a colour are in
                if (ReadCode(text, false) is { } colour) {
                    ChooseCode(colour);
                }
            }
        );
        _cloakCodeField.onEndEdit.AddListener(text => {
                // The escape key takes back what was typed, and the colour with it, also where there was no code before
                if (_cloakCodeField.wasCanceled) {
                    if (_codeTyping && PlayerColorMod.CanChoose) {
                        PlayerColorMod.Choose(_colourBeforeCode);
                    }
                } else if (ReadCode(text, true) is { } colour) {
                    ChooseCode(colour);
                }

                _codeTyping = false;

                // Whatever is left in the box, it shows the code of the colour chosen again
                RefreshCloak();
            }
        );

        var chosenSwatch = new GameObject("CloakChosen");
        var chosenRect = chosenSwatch.AddComponent<RectTransform>();
        chosenRect.anchorMin = new Vector2(0.52f, 1f);
        chosenRect.anchorMax = new Vector2(0.62f, 1f);
        chosenRect.offsetMin = new Vector2(0f, -RowHeight);
        chosenRect.offsetMax = Vector2.zero;
        chosenSwatch.transform.SetParent(_codeRow, false);
        _cloakChosenImage = chosenSwatch.AddComponent<Image>();
        _cloakChosenImage.raycastTarget = false;

        _cloakNoteText = WaitingRoomPanel.CreateLabel(
            _skinContent,
            "CloakNote",
            "",
            new Vector2(0.03f, 1f),
            new Vector2(0.97f, 1f),
            Vector2.zero,
            Vector2.zero,
            13,
            Muted,
            TextAnchor.UpperLeft
        );

        Refresh();

        // The art of a skin takes a moment to load, and the page says when Hornet wears it, and whether the colour of
        // the cloak shows, which it doesn't on a skin
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
    /// <param name="top">How far below the top of the panel, or of the page it goes on, the box begins.</param>
    /// <param name="height">How tall the box is.</param>
    /// <param name="page">The page that it goes on, or null for the panel itself.</param>
    /// <returns>The transform to put the choices under.</returns>
    private Transform CreateSection(string name, float top, float height, Transform? page = null) {
        var section = new GameObject(name);
        var sectionRect = section.AddComponent<RectTransform>();
        sectionRect.anchorMin = new Vector2(0f, 1f);
        sectionRect.anchorMax = new Vector2(1f, 1f);
        sectionRect.pivot = new Vector2(0.5f, 1f);
        sectionRect.anchoredPosition = new Vector2(0f, -top);
        sectionRect.sizeDelta = new Vector2(0f, height);
        section.transform.SetParent(page != null ? page : GameObject.transform, false);

        return section.transform;
    }

    /// <summary>
    /// Lays the page of this player's own choices out: the switch of the cloak's colour under the outline switches
    /// only where Player Color is installed, the note of the outlines as tall as its text under the switches, the box
    /// of enemies under it, and the page as tall as all of them, which scrolls where that is taller than the panel.
    /// </summary>
    /// <param name="cloakShown">Whether the switch of the cloak's colour shows.</param>
    private void LayOutOwnPage(bool cloakShown) {
        _outlineCloakButton.SetActive(cloakShown);

        var noteTop = cloakShown ? 2f * OutlineRowStep : OutlineRowStep;
        var noteHeight = NoteHeightOf(_outlineNoteText);
        var note = _outlineNoteText.rectTransform;
        note.anchoredPosition = new Vector2(note.anchoredPosition.x, -noteTop);
        note.sizeDelta = new Vector2(note.sizeDelta.x, noteHeight);

        var mapHeight = Mathf.Max(MapSectionHeight, noteTop + noteHeight);
        _mapArea.sizeDelta = new Vector2(_mapArea.sizeDelta.x, mapHeight);

        var enemyTop = NameSectionHeight + SectionGap + mapHeight + SectionGap;
        _enemyArea.anchoredPosition = new Vector2(_enemyArea.anchoredPosition.x, -enemyTop);
        _ownContent.sizeDelta = new Vector2(0f, enemyTop + EnemySectionHeight);
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
    /// Makes a page in a box that scrolls down where what is on it is taller than the box: with the mouse's wheel, by
    /// dragging the page or the bar at its right, which shows only then, and with a gamepad as it moves onto what is
    /// out of sight (see <see cref="Util.PadNavigation"/>). The bar has room of its own, so the page is as wide either
    /// way.
    /// </summary>
    /// <param name="box">The box.</param>
    /// <returns>What goes on the page, across it from its top, whose height is set as it is laid out.</returns>
    private static RectTransform CreateScrollingPage(Transform box) {
        var scroll = new GameObject("Scroll");
        var scrollRect = scroll.AddComponent<RectTransform>();
        scrollRect.anchorMin = Vector2.zero;
        scrollRect.anchorMax = Vector2.one;
        scrollRect.offsetMin = Vector2.zero;
        scrollRect.offsetMax = Vector2.zero;
        scroll.transform.SetParent(box, false);

        // Something that the mouse points at all over the page, for the wheel and for dragging, and that draws nothing
        var pointedAt = scroll.AddComponent<Image>();
        pointedAt.color = Color.clear;

        var viewport = new GameObject("Viewport");
        var viewportRect = viewport.AddComponent<RectTransform>();
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = Vector2.zero;
        viewportRect.offsetMax = new Vector2(-(ScrollbarWidth + 2f), 0f);
        viewport.AddComponent<RectMask2D>();
        viewport.transform.SetParent(scroll.transform, false);

        var content = new GameObject("Content");
        var contentRect = content.AddComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.anchoredPosition = Vector2.zero;
        contentRect.sizeDelta = Vector2.zero;
        content.transform.SetParent(viewport.transform, false);

        var bar = new GameObject("Scrollbar");
        var barRect = bar.AddComponent<RectTransform>();
        barRect.anchorMin = new Vector2(1f, 0f);
        barRect.anchorMax = new Vector2(1f, 1f);
        barRect.pivot = new Vector2(1f, 0.5f);
        barRect.sizeDelta = new Vector2(ScrollbarWidth, 0f);
        barRect.anchoredPosition = Vector2.zero;
        bar.transform.SetParent(scroll.transform, false);
        var track = bar.AddComponent<Image>();
        track.color = new Color(1f, 1f, 1f, 0.08f);

        var slidingArea = new GameObject("Sliding Area");
        var areaRect = slidingArea.AddComponent<RectTransform>();
        areaRect.anchorMin = Vector2.zero;
        areaRect.anchorMax = Vector2.one;
        areaRect.offsetMin = Vector2.zero;
        areaRect.offsetMax = Vector2.zero;
        slidingArea.transform.SetParent(bar.transform, false);

        // Its anchors are set by the bar, by where the page is scrolled to and how much of it shows
        var handle = new GameObject("Handle");
        var handleRect = handle.AddComponent<RectTransform>();
        handleRect.offsetMin = Vector2.zero;
        handleRect.offsetMax = Vector2.zero;
        handle.transform.SetParent(slidingArea.transform, false);
        var handleImage = handle.AddComponent<Image>();
        var accent = WaitingRoomPanel.Accent;
        handleImage.color = new Color(accent.r, accent.g, accent.b, 0.5f);

        var scrollbar = bar.AddComponent<Scrollbar>();
        scrollbar.transition = Selectable.Transition.None;
        scrollbar.targetGraphic = handleImage;
        // A gamepad scrolls by moving onto what is out of sight, rather than stopping at the bar on its way
        scrollbar.navigation = new Navigation { mode = Navigation.Mode.None };
        scrollbar.direction = Scrollbar.Direction.BottomToTop;
        scrollbar.handleRect = handleRect;

        var scrollView = scroll.AddComponent<ScrollRect>();
        scrollView.viewport = viewportRect;
        scrollView.content = contentRect;
        scrollView.horizontal = false;
        scrollView.vertical = true;
        scrollView.movementType = ScrollRect.MovementType.Clamped;
        scrollView.scrollSensitivity = 25f;
        scrollView.verticalScrollbar = scrollbar;
        scrollView.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        return contentRect;
    }

    /// <summary>
    /// Makes a row across a page that scrolls, as tall as given; where it sits is set as the page is laid out.
    /// </summary>
    private static RectTransform CreateRow(Transform page, string name, float height) {
        var row = new GameObject(name);
        var rect = row.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.sizeDelta = new Vector2(0f, height);
        row.transform.SetParent(page, false);
        return rect;
    }

    /// <summary>
    /// Lays the rows of the skin's part of the page out from its top, each under the one before: the slashes only where
    /// there are skins to draw them from, and each note as tall as its text; the page is as tall as all of them.
    /// </summary>
    private void LayOutSkinPage() {
        var top = 0f;
        PlaceRow(_skinHeadRow, ref top, RowHeight, 4f);
        PlaceRow(_skinChoiceRow, ref top, RowHeight, 4f);
        if (_slashRow.activeSelf) {
            PlaceRow((RectTransform) _slashRow.transform, ref top, RowHeight, 4f);
        }

        PlaceRow(_skinNoteText.rectTransform, ref top, NoteHeightOf(_skinNoteText), 10f);
        PlaceRow(_cloakHeadRow, ref top, RowHeight, 3f);
        PlaceRow(_cloakPresetRow, ref top, RowHeight, 8f);
        PlaceRow(_hueRow, ref top, StripHeight, 8f);
        PlaceRow(_lightRow, ref top, StripHeight, 8f);
        PlaceRow(_codeRow, ref top, RowHeight, 8f);
        PlaceRow(_cloakNoteText.rectTransform, ref top, NoteHeightOf(_cloakNoteText), 0f);
        _skinContent.sizeDelta = new Vector2(0f, top + 4f);
    }

    /// <summary>
    /// Puts a row at a depth below the top of its page, as tall as given, and moves the depth under it with a gap.
    /// </summary>
    private static void PlaceRow(RectTransform row, ref float top, float height, float gap) {
        row.offsetMin = new Vector2(row.offsetMin.x, -(top + height));
        row.offsetMax = new Vector2(row.offsetMax.x, -top);
        top += height + gap;
    }

    /// <summary>
    /// How tall a note is with its text, at the width it has.
    /// </summary>
    private static float NoteHeightOf(Text note) => Mathf.Ceil(note.preferredHeight) + 2f;

    /// <summary>
    /// Makes a strip that a value from 0 at its left end to 1 at its right is chosen on, under a label at the left of
    /// its row: by a press of the mouse on it or a drag along it, or by holding the stick of a gamepad left or right
    /// while it is selected (see <see cref="ColourStrip"/>), as up and down move to the rows above and below.
    /// </summary>
    /// <param name="parent">What it is in.</param>
    /// <param name="name">Its name.</param>
    /// <param name="label">What its label says.</param>
    /// <param name="top">How far below the top of what it is in it is.</param>
    /// <param name="texture">Its picture, one row of colours, the first at its left end and the last at its right.</param>
    /// <param name="image">What draws its picture.</param>
    private static ColourStrip CreateStrip(
        Transform parent,
        string name,
        string label,
        float top,
        Texture texture,
        out RawImage image
    ) {
        WaitingRoomPanel.CreateLabel(
            parent,
            name + "Label",
            label,
            new Vector2(0.03f, 1f),
            new Vector2(0.19f, 1f),
            new Vector2(0f, StripHeight),
            new Vector2(0f, -top),
            13,
            Muted,
            TextAnchor.MiddleLeft
        );

        var strip = new GameObject(name);
        var rect = strip.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.19f, 1f);
        rect.anchorMax = new Vector2(0.97f, 1f);
        rect.offsetMin = new Vector2(0f, -(top + StripHeight));
        rect.offsetMax = new Vector2(0f, -top);
        strip.transform.SetParent(parent, false);

        // From the middle of the first colour to the middle of the last, so that each end shows its own
        image = strip.AddComponent<RawImage>();
        image.texture = texture;
        var half = 0.5f / texture.width;
        image.uvRect = new Rect(half, 0f, 1f - 2f * half, 1f);

        // The marker that says where on the strip the value is: a light line in a dark one, which shows on any colour
        var area = new GameObject("Area");
        var areaRect = area.AddComponent<RectTransform>();
        areaRect.anchorMin = Vector2.zero;
        areaRect.anchorMax = Vector2.one;
        areaRect.offsetMin = Vector2.zero;
        areaRect.offsetMax = Vector2.zero;
        area.transform.SetParent(strip.transform, false);

        var marker = new GameObject("Marker");
        var markerRect = marker.AddComponent<RectTransform>();
        markerRect.anchorMin = new Vector2(0f, 0f);
        markerRect.anchorMax = new Vector2(0f, 1f);
        markerRect.sizeDelta = new Vector2(6f, 6f);
        marker.transform.SetParent(area.transform, false);
        var markerImage = marker.AddComponent<Image>();
        markerImage.color = new Color(0f, 0f, 0f, 0.85f);
        markerImage.raycastTarget = false;

        var line = new GameObject("Line");
        var lineRect = line.AddComponent<RectTransform>();
        lineRect.anchorMin = Vector2.zero;
        lineRect.anchorMax = Vector2.one;
        lineRect.offsetMin = new Vector2(2f, 2f);
        lineRect.offsetMax = new Vector2(-2f, -2f);
        line.transform.SetParent(marker.transform, false);
        var lineImage = line.AddComponent<Image>();
        lineImage.color = Color.white;
        lineImage.raycastTarget = false;

        var slider = strip.AddComponent<ColourStrip>();
        slider.transition = Selectable.Transition.None;
        slider.targetGraphic = image;
        slider.navigation = new Navigation { mode = Navigation.Mode.Vertical };
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.wholeNumbers = false;
        slider.handleRect = markerRect;
        return slider;
    }

    /// <summary>
    /// Makes the box that takes the code of a colour, such as #7A3CFF, in the row at the given depth.
    /// </summary>
    private static InputField CreateCodeField(Transform parent, float top) {
        var box = new GameObject("CloakCode");
        var rect = box.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.19f, 1f);
        rect.anchorMax = new Vector2(0.49f, 1f);
        rect.offsetMin = new Vector2(0f, -(top + 28f));
        rect.offsetMax = new Vector2(0f, -top);
        box.transform.SetParent(parent, false);

        var background = box.AddComponent<Image>();
        background.sprite = Resources.TextureManager.InputFieldBg.Neutral;
        background.type = Image.Type.Sliced;

        var placeholder = CreateBoxText(box.transform, "Placeholder", new Color(1f, 1f, 1f, 0.5f));
        placeholder.text = "#RRGGBB";
        var text = CreateBoxText(box.transform, "Text", Color.white);

        var field = box.AddComponent<InputField>();
        field.targetGraphic = background;
        field.placeholder = placeholder;
        field.textComponent = text;
        field.characterLimit = 7;
        field.lineType = InputField.LineType.SingleLine;

        // Only what a code is made of, in capitals: a # at the start and the digits of base sixteen
        field.onValidateInput += (_, index, added) => added == '#'
            ? index == 0 ? '#' : '\0'
            : Uri.IsHexDigit(added)
                ? char.ToUpperInvariant(added)
                : '\0';

        // As the mod's other boxes: a gamepad that moves onto it doesn't start typing, and the caret can be found
        field.shouldActivateOnSelect = false;
        field.caretWidth = 2;
        return field;
    }

    /// <summary>
    /// Makes a line of text across a box, a little in from its sides, drawn even where it is taller than the box.
    /// </summary>
    private static Text CreateBoxText(Transform box, string name, Color colour) {
        var textObject = new GameObject(name);
        var rect = textObject.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(5f, 0f);
        rect.offsetMax = new Vector2(-5f, 0f);
        textObject.transform.SetParent(box, false);

        var text = textObject.AddComponent<Text>();
        text.font = Resources.FontManager.UIFontRegular;
        text.fontSize = 15;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = colour;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.supportRichText = false;
        return text;
    }

    /// <summary>
    /// The picture of the strip of hues: every hue at its fullest, red at both ends.
    /// </summary>
    private static Texture2D HueTexture() {
        var texture = new Texture2D(HueTexels, 1, TextureFormat.RGBA32, false) {
            name = "SSMP cloak hues",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        var pixels = new Color32[HueTexels];
        for (var i = 0; i < HueTexels; i++) {
            pixels[i] = FromHsl((float) i / (HueTexels - 1), 1f, 0.5f);
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return texture;
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
    /// Draws this player's slashes from the choice before or after the one taken: the skin worn, the game's own art,
    /// then each skin of Hornet's.
    /// </summary>
    /// <param name="step">One to go to the next, minus one to go back.</param>
    private void StepSlashes(int step) {
        if (!OwnSkin.CanChoose) {
            return;
        }

        var choices = SlashChoices(out var at);
        var next = ((at + step) % choices.Count + choices.Count) % choices.Count;
        SlashSkin.Choose(choices[next]);
        Refresh();
    }

    /// <summary>
    /// What the slashes can be drawn from, as the settings keep each: the skin worn, the game's own art, then each skin
    /// of Hornet's on this computer; with the place of the one taken, the skin worn when the one taken is not here.
    /// </summary>
    private static List<string> SlashChoices(out int taken) {
        var choices = new List<string> { "", SlashSkin.GameChoice };
        var packs = SlashSkin.Choices();
        foreach (var pack in packs) {
            if (!choices.Exists(name => string.Equals(name, pack.Name, StringComparison.OrdinalIgnoreCase))) {
                choices.Add(pack.Name);
            }
        }

        var chosen = SlashSkin.ChosenPack(packs);
        taken = SlashSkin.Choice == SlashSkin.GameChoice
            ? 1
            : chosen != null
                ? choices.FindIndex(name => string.Equals(name, chosen.Name, StringComparison.OrdinalIgnoreCase))
                : 0;
        return choices;
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
    /// Gives the cloak the hue at a place on the strip of hues, as light as it is. A grey has no hue to change, so it
    /// takes the hue at its fullest.
    /// </summary>
    /// <param name="hue">The hue, from 0 to 1.</param>
    private void ChooseHue(float hue) {
        if (!PlayerColorMod.CanChoose) {
            return;
        }

        _hue = hue;
        if (_saturation < 0.05f) {
            _saturation = 1f;
        }

        ChooseFromStrips();
    }

    /// <summary>
    /// Makes the cloak as light as a place on the strip of how light it is, from black to white, in its hue.
    /// </summary>
    /// <param name="lightness">How light, from 0 to 1.</param>
    private void ChooseLightness(float lightness) {
        if (!PlayerColorMod.CanChoose) {
            return;
        }

        _lightness = lightness;
        ChooseFromStrips();
    }

    /// <summary>
    /// Has the cloak drawn in the colour that the strips show.
    /// </summary>
    private void ChooseFromStrips() {
        var colour = FromHsl(_hue, _saturation, _lightness);
        _stripColour = colour;
        PlayerColorMod.ChooseMoving(colour);
        RefreshCloak();
    }

    /// <summary>
    /// Has the cloak drawn in a colour typed as a code, unless it already is.
    /// </summary>
    private void ChooseCode(Color colour) {
        if (!PlayerColorMod.CanChoose) {
            return;
        }

        if (PlayerColorMod.ChosenColour() is { } chosen && SameColour(chosen, colour)) {
            return;
        }

        PlayerColorMod.Choose(colour);
        RefreshCloak();
    }

    /// <summary>
    /// The colour of a code, such as #7A3CFF or 7A3CFF; with short ones too, such as #73F for #7733FF, once the code
    /// is all typed.
    /// </summary>
    /// <param name="text">The code.</param>
    /// <param name="shortToo">Whether a code of three digits is taken too.</param>
    /// <returns>The colour, or null for a code of none.</returns>
    private static Color? ReadCode(string text, bool shortToo) {
        var digits = text.Trim().TrimStart('#');
        if (shortToo && digits.Length == 3) {
            digits = $"{digits[0]}{digits[0]}{digits[1]}{digits[1]}{digits[2]}{digits[2]}";
        }

        if (digits.Length != 6 ||
            !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) {
            return null;
        }

        return (Color) new Color32((byte) (value >> 16), (byte) (value >> 8), (byte) value, 255);
    }

    /// <summary>
    /// Whether two colours are the same as Player Color saves them, in bytes.
    /// </summary>
    private static bool SameColour(Color one, Color other) {
        Color32 a = one;
        Color32 b = other;
        return a.r == b.r && a.g == b.g && a.b == b.b;
    }

    /// <summary>
    /// Moves the strips to a colour, unless they show it: to its hue, saturation and lightness, keeping the hue they
    /// had for a colour that has none.
    /// </summary>
    private void FollowColour(Color colour) {
        if (_stripColour is { } shown && SameColour(shown, colour)) {
            return;
        }

        _stripColour = colour;
        var max = Mathf.Max(colour.r, Mathf.Max(colour.g, colour.b));
        var min = Mathf.Min(colour.r, Mathf.Min(colour.g, colour.b));
        var chroma = max - min;
        _lightness = (max + min) / 2f;
        if (chroma <= 0f) {
            _saturation = 0f;
            return;
        }

        _saturation = Mathf.Clamp01(chroma / (1f - Mathf.Abs(2f * _lightness - 1f)));
        var sector = max == colour.r
            ? Mathf.Repeat((colour.g - colour.b) / chroma, 6f)
            : max == colour.g
                ? (colour.b - colour.r) / chroma + 2f
                : (colour.r - colour.g) / chroma + 4f;
        _hue = sector / 6f;
    }

    /// <summary>
    /// The colour of a hue, saturation and lightness, each from 0 to 1: black at no lightness, white at the most, and
    /// the hue at its fullest halfway.
    /// </summary>
    private static Color FromHsl(float hue, float saturation, float lightness) {
        var chroma = (1f - Mathf.Abs(2f * lightness - 1f)) * saturation;
        var sector = Mathf.Repeat(hue, 1f) * 6f;
        var second = chroma * (1f - Mathf.Abs(sector % 2f - 1f));
        var (r, g, b) = sector switch {
            < 1f => (chroma, second, 0f),
            < 2f => (second, chroma, 0f),
            < 3f => (0f, chroma, second),
            < 4f => (0f, second, chroma),
            < 5f => (second, 0f, chroma),
            _ => (chroma, 0f, second)
        };
        var light = lightness - chroma / 2f;
        return new Color(r + light, g + light, b + light, 1f);
    }

    /// <summary>
    /// Links the controls of the colour of the cloak for a gamepad by hand, up and down from the colours to press to the
    /// strips and the code. Unity's own choice goes by how far the middle of each control is, and the middle of a strip
    /// as wide as the row is far from the colours at its left: down from those went past both strips to the code, a box
    /// that a gamepad can't type into (review of 6f65520). Left and right on a strip, which lead nowhere, move it.
    /// </summary>
    /// <param name="pressed">Where the colour chosen is in the row of colours, the cloak's own first: the strip of hues
    /// goes up to it.</param>
    private void LinkCloakControls(int pressed) {
        // Linked again only when what they link to changes, and not at every frame of a strip moving
        if (_linkedFor is { } linked && linked.Pressed == pressed && linked.OverSlashes == _slashRow.activeSelf) {
            return;
        }

        _linkedFor = (pressed, _slashRow.activeSelf);

        // Over the colours, the row of the slashes, or of the skin where there are no slashes to choose
        Selectable overLeft = _slashRow.activeSelf ? _slashBackButton : _skinBackButton;
        Selectable overRight = _slashRow.activeSelf ? _slashOnButton : _skinOnButton;

        var row = new List<Selectable>(_cloakSwatches.Count + 1) { _cloakOwnButton };
        foreach (var swatch in _cloakSwatches) {
            row.Add(swatch.Button);
        }

        for (var i = 0; i < row.Count; i++) {
            row[i].navigation = new Navigation {
                mode = Navigation.Mode.Explicit,
                selectOnLeft = i > 0 ? row[i - 1] : null,
                selectOnRight = i < row.Count - 1 ? row[i + 1] : null,
                selectOnUp = i < row.Count / 2 ? overLeft : overRight,
                selectOnDown = _hueStrip
            };
        }

        _hueStrip.navigation = new Navigation {
            mode = Navigation.Mode.Explicit,
            selectOnUp = row[Mathf.Clamp(pressed, 0, row.Count - 1)],
            selectOnDown = _lightStrip
        };
        _lightStrip.navigation = new Navigation {
            mode = Navigation.Mode.Explicit,
            selectOnUp = _hueStrip,
            selectOnDown = _cloakCodeField
        };
        _cloakCodeField.navigation = new Navigation {
            mode = Navigation.Mode.Explicit,
            selectOnUp = _lightStrip
        };
    }

    /// <summary>
    /// Draws the strip of how light the cloak is in its hue and saturation, from black to white, unless it is drawn so.
    /// </summary>
    private void DrawLightStrip() {
        if (_lightDrawnFor is { } drawn && Mathf.Approximately(drawn.Hue, _hue) &&
            Mathf.Approximately(drawn.Saturation, _saturation)) {
            return;
        }

        _lightDrawnFor = (_hue, _saturation);
        for (var i = 0; i < LightTexels; i++) {
            _lightPixels[i] = FromHsl(_hue, _saturation, (float) i / (LightTexels - 1));
        }

        _lightTexture.SetPixels32(_lightPixels);
        _lightTexture.Apply(false);
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

        // The slashes, with nothing to choose from where no skin of Hornet's is here; the note then takes their room
        var slashChoices = canChoose && choices.Count > 0;
        _slashRow.SetActive(slashChoices);
        var slashPack = slashChoices ? SlashSkin.ChosenPack(choices) : null;
        var gameSlashes = slashChoices && SlashSkin.Choice == SlashSkin.GameChoice;
        _slashNameText.text = slashPack != null
            ? slashPack.Name
            : gameSlashes
                ? Lang.Pick("Game's own", "游戏原版")
                : Lang.Pick("As the skin", "跟着皮肤");

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
                $"有几个皮肤同时开着，穿的是排在前面的「{current!.Name}」。在这里选一下就只开它。"
            );
        } else if (slashPack != null) {
            _skinNoteText.text = Lang.Pick(
                "Worn at once, and still the next time the game starts. The other player sees them if they have the " +
                "same skins.",
                "选了马上换上，下次开游戏也还是它们。队友也装了这些皮肤，才看得到你穿的。"
            );
        } else {
            _skinNoteText.text = Lang.Pick(
                "Worn at once, and still the next time the game starts. The other player sees it if they have the " +
                "same skin.",
                "选了马上换上，下次开游戏也还是它。队友装了同一个皮肤，才看得到你穿的。"
            );
        }

        // The colour of the cloak under it, which a skin keeps its own colours over, and the page laid out for what
        // the notes say
        _wearingSkin = current != null;
        RefreshCloak();
        LayOutSkinPage();
    }

    /// <summary>
    /// Draws the colour of the cloak that this player chose, while the skin's part of the page is shown.
    /// </summary>
    private void RefreshCloak() {
        if (_page != Page.Skin) {
            return;
        }

        var canChoose = PlayerColorMod.CanChoose;
        var chosen = canChoose ? PlayerColorMod.ChosenColour() : null;
        var wearingSkin = _wearingSkin;

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

        // The strips stand at the colour that Player Color keeps, also while the cloak keeps its own colour, so that
        // moving one goes on from the colour last chosen
        var kept = canChoose ? PlayerColorMod.KeptColour() : null;
        if (kept is { } keptColour) {
            FollowColour(keptColour);
        }

        LinkCloakControls(named >= 0 ? named + 1 : 0);

        _hueStrip.interactable = _lightStrip.interactable = canChoose;
        _hueStrip.SetValueWithoutNotify(_hue);
        _lightStrip.SetValueWithoutNotify(_lightness);
        _hueImage.color = _lightImage.color = canChoose ? Color.white : new Color(1f, 1f, 1f, 0.3f);
        DrawLightStrip();

        // The code of the colour chosen, and the colour itself beside it, faded while the cloak keeps its own; not
        // while the box is typed into
        _cloakCodeField.interactable = canChoose;
        if (!_cloakCodeField.isFocused) {
            _cloakCodeField.SetTextWithoutNotify(chosen is { } code ? "#" + ColorUtility.ToHtmlStringRGB(code) : "");
        }

        var shownColour = chosen ?? kept;
        _cloakChosenImage.color = shownColour is { } shown
            ? chosen != null ? shown : new Color(shown.r, shown.g, shown.b, 0.3f)
            : Color.clear;

        // Whose the colours are is said on the row, as the row goes by another player's mod: a friend who saw only
        // "没装全" there asked what it was that was not fully installed (USER 10-23: "斗篷颜色切换识别的是别人的mod，
        // 最好还是标注一下")
        _cloakStatusText.text = !PlayerColorMod.IsLoaded
            ? Lang.Pick("Player Color mod isn't installed", "没装 Player Color 模组")
            : !canChoose
                ? Lang.Pick("Player Color mod isn't fully installed", "Player Color 模组没装全")
                : wearingSkin
                    ? Lang.Pick("Not on a skin", "穿着皮肤时不变色")
                    : chosen == null
                        ? Lang.Pick("Own colour", "原色")
                        : named >= 0
                            ? Lang.Pick(CloakColours[named].English, CloakColours[named].Chinese)
                            : Lang.Pick("Another colour", "别的颜色");

        if (!PlayerColorMod.IsLoaded) {
            _cloakNoteText.text = Lang.Pick(
                "The cloak's colour comes from another mod, Player Color by PDE26jjk, which this mod doesn't " +
                "include. With it installed, the cloak can be given a colour here, so that the two of you are told " +
                "apart at a glance.",
                "斗篷颜色用的是另一个模组 Player Color（作者 PDE26jjk），联机模组里不带它。装了它，就能在这里" +
                "给斗篷换颜色，两个人一眼就分得清。"
            );
        } else if (!PlayerColorMod.HasMasks) {
            _cloakNoteText.text = Lang.Pick(
                "The masks of Player Color, another mod (by PDE26jjk), weren't found, so the cloak keeps its colour. " +
                "They go in Hollow Knight Silksong_Data\\Mods\\Mask in the game's folder.",
                "没找到另一个模组 Player Color（作者 PDE26jjk）的 Mask 文件夹，斗篷变不了色。它要放在游戏目录的 " +
                "Hollow Knight Silksong_Data\\Mods\\Mask 里。"
            );
        } else if (!canChoose) {
            _cloakNoteText.text = Lang.Pick(
                "Player Color, another mod (by PDE26jjk), didn't load fully, so the cloak keeps its colour. Most " +
                "likely its file ssmod isn't in BepInEx\\plugins\\PlayerColor.",
                "另一个模组 Player Color（作者 PDE26jjk）没有完整加载，斗篷变不了色。多半是它的 ssmod 文件" +
                "没放在 BepInEx\\plugins\\PlayerColor 文件夹里。"
            );
        } else if (wearingSkin) {
            // The author on this note too, which is the one that a player who wears a skin sees (USER 10-25: "某个版本
            // 开始player color的原作者名字没了")
            _cloakNoteText.text = Lang.Pick(
                "A skin keeps its own colours. The colour chosen shows on the game's own look, and is kept for it. " +
                "The colour is drawn by another mod, Player Color by PDE26jjk.",
                "穿着皮肤时斗篷不变色，换回“原样”才看得到；选的颜色会留着。颜色由另一个模组 Player Color（作者 " +
                "PDE26jjk）来画。"
            );
        } else {
            _cloakNoteText.text = Lang.Pick(
                "Worn at once, and still the next time the game starts. The colour is drawn by another mod, Player " +
                "Color by PDE26jjk: the other player sees it if they have it too.",
                "选了马上换上，下次开游戏也还在。颜色由另一个模组 Player Color（作者 PDE26jjk）来画，" +
                "队友也装了它，才看得到你的颜色。"
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

        // The outlines in the colour chosen for the cloak, where Player Color is installed and has made its settings,
        // which they keep the name's colour for while the cloak keeps its own; whose mod that is goes on the note, as it
        // isn't this one
        var cloakShown = PlayerColorMod.HasColourSettings;
        var inCloak = cloakShown && _modSettings.OutlineInCloakColour;
        _outlineCloakImage.color = inCloak ? WaitingRoomPanel.ReadyOn : WaitingRoomPanel.ReadyOff;
        _outlineCloakText.text = inCloak
            ? Lang.Pick("CLOAK'S COLOUR: ON", "跟斗篷同色：开")
            : Lang.Pick("CLOAK'S COLOUR: OFF", "跟斗篷同色：关");
        var outlinesInCloak = inCloak && PlayerColorMod.ChosenColour() != null;
        _outlineNoteText.text = !cloakShown
            ? Lang.Pick(
                "In the colour above: your map head (with the compass) and your Hornet in fights beside the other " +
                "player.",
                "用上面选的颜色：戴着罗盘时描地图上你的头像；队友在你画面里、有人在打怪时描你的大黄蜂。"
            )
            : inCloak
                ? Lang.Pick(
                    "In the colour you chose for your cloak in Player Color, another mod (by PDE26jjk), or in the " +
                    "colour above while you keep the cloak's own: your map head (with the compass) and your Hornet " +
                    "in fights beside the other player.",
                    "用你给斗篷选的颜色（来自另一个模组 Player Color，作者 PDE26jjk；斗篷选的是原色时用上面选的颜色）：" +
                    "戴着罗盘时描地图上你的头像；队友在你画面里、有人在打怪时描你的大黄蜂。"
                )
                : Lang.Pick(
                    "In the colour above, or in the colour you chose for your cloak in Player Color, another mod " +
                    "(by PDE26jjk): your map head (with the compass) and your Hornet in fights beside the other " +
                    "player.",
                    "用上面选的颜色，也可以跟斗篷同色，用你给斗篷选的颜色（来自另一个模组 Player Color，作者 " +
                    "PDE26jjk）：戴着罗盘时描地图上你的头像；队友在你画面里、有人在打怪时描你的大黄蜂。"
                );
        LayOutOwnPage(cloakShown);

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
            // use means by the name or by an outline, which take the same colour unless they take the cloak's.
            var picked = (coloured || (outlined || fightOutlined) && !outlinesInCloak) &&
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
        GameObject.SetActive(_componentGroup.IsActive());
        Refresh();
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
        var shown = _activeSelf && groupActive;
        GameObject.SetActive(shown);

        // Drawn again now that it is in the canvas, where its notes are measured at the scale they are drawn at
        if (shown) {
            Refresh();
        }

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

        /// <summary>What this player's own Hornet looks like: her skin, her slashes and the colour of her cloak.</summary>
        Skin,

        /// <summary>The choices of the room, which the host makes for everyone.</summary>
        Room
    }
}
