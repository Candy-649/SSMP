using System;
using System.Collections.Generic;
using SSMP.Util;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SSMP.Ui.Component;

/// <summary>
/// The settings page of the multiplayer menu: choices that only change how this player's own game draws things, such
/// as their own name and their own head on the map. None of it is sent anywhere, and the other players do not have to
/// agree.
///
/// The choices about the name used to sit under the list in the waiting room, the one place in the menu where a
/// player stands still. With more of them that room got crowded, so they have a page of their own, which can be opened
/// while waiting and left again without leaving the room.
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
    /// How much room the choices about the map take: a switch over a line or two saying what it does.
    /// </summary>
    private const float MapSectionHeight = 74f;

    /// <summary>
    /// Height of the panel. Less than that of the waiting room, so that it ends above the feedback text of the menu
    /// as well.
    /// </summary>
    public const float Height = HeaderHeight + NoteHeight + SectionGap + NameSectionHeight + SectionGap +
                                MapSectionHeight;

    private static readonly Color Muted = new(0.75f, 0.75f, 0.75f, 1f);

    public SettingsPanel(
        ComponentGroup parent,
        Vector2 position,
        Vector2 size,
        Game.Settings.ModSettings modSettings
    ) {
        _modSettings = modSettings;
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

        WaitingRoomPanel.CreateLabel(
            GameObject.transform,
            "Note",
            Lang.Pick("Only on this computer. Your teammates do not see these.", "只改你自己这边，队友看不到。"),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(0f, NoteHeight),
            new Vector2(0f, -HeaderHeight),
            15,
            Muted,
            TextAnchor.MiddleCenter
        );

        var nameArea = CreateSection("NameSettings", HeaderHeight + NoteHeight + SectionGap, NameSectionHeight);

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

        WaitingRoomPanel.CreateLabel(
            mapArea,
            "MapSettingsLabel",
            Lang.Pick("MAP", "地图"),
            new Vector2(0.03f, 1f),
            new Vector2(0.30f, 1f),
            new Vector2(0f, 28f),
            new Vector2(0f, -1f),
            14,
            WaitingRoomPanel.Accent,
            TextAnchor.MiddleLeft
        );

        // Same height as the switches above it: the top 28 of the section, between 1 and 29 down
        var outlineButton = WaitingRoomPanel.CreateButton(
            mapArea,
            "OutlineButton",
            "",
            new Vector2(0.32f, 1f),
            new Vector2(0.97f, 1f),
            WaitingRoomPanel.ReadyOff,
            () => {
                _modSettings.OutlineOwnMapIcon = !_modSettings.OutlineOwnMapIcon;
                _modSettings.Save();
                Refresh();
            },
            out _outlineImage,
            out _outlineText
        );
        var outlineRect = outlineButton.GetComponent<RectTransform>();
        outlineRect.offsetMin = new Vector2(0f, -29f);
        outlineRect.offsetMax = new Vector2(0f, -1f);

        WaitingRoomPanel.CreateLabel(
            mapArea,
            "OutlineNote",
            Lang.Pick(
                "With the compass worn, your own head on the map gets an outline in the colour above.",
                "戴着罗盘时，地图上你自己的头像描一圈上面选的颜色。"
            ),
            new Vector2(0.03f, 1f),
            new Vector2(0.97f, 1f),
            new Vector2(0f, MapSectionHeight - 34f),
            new Vector2(0f, -34f),
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
    /// Draws the choices as they currently stand.
    /// </summary>
    private void Refresh() {
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
            ? Lang.Pick("OUTLINE MY HEAD: ON", "头像描边：开")
            : Lang.Pick("OUTLINE MY HEAD: OFF", "头像描边：关");

        foreach (var pair in _swatches) {
            if (!Game.Client.PlayerManager.OwnNameColours.TryGetValue(pair.Key, out var colour)) {
                continue;
            }

            // The one in use is shown at its full strength and the rest are faded. A tick or an outline would need
            // a colour of its own, and a colour laid over a row of colours is the one thing that cannot be read. In
            // use means by the name or by the outline on the map, which takes the same colour.
            var picked = (coloured || outlined) &&
                         string.Equals(pair.Key, _modSettings.OwnNameColour, StringComparison.OrdinalIgnoreCase);
            pair.Value.color = picked ? colour : new Color(colour.r, colour.g, colour.b, 0.3f);
        }
    }

    /// <summary>Shows the panel.</summary>
    public void Show() {
        _activeSelf = true;
        GameObject.SetActive(_componentGroup.IsActive());
    }

    /// <summary>Hides the panel.</summary>
    public void Hide() {
        _activeSelf = false;
        GameObject.SetActive(false);
    }

    /// <inheritdoc />
    public void SetGroupActive(bool groupActive) {
        if (GameObject == null) return;
        GameObject.SetActive(_activeSelf && groupActive);
    }

    /// <inheritdoc />
    public void SetActive(bool active) {
        _activeSelf = active;
        GameObject.SetActive(_activeSelf && _componentGroup.IsActive());
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
