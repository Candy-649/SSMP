using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SSMP.Ui.Component;

/// <summary>
/// The screen the host waits on after opening their game, listing who is in so far.
///
/// It exists because hosting used to go straight from creating a lobby into choosing a save, which loaded the save
/// before anyone could join and left no obvious place to invite anyone from. Waiting here instead means the host can
/// see their teammate arrive, and only then pick the save.
/// </summary>
internal class WaitingRoomPanel : IComponent {
    /// <summary>The root GameObject for this panel.</summary>
    private GameObject GameObject { get; }

    /// <summary>Container the player rows are built in.</summary>
    private readonly RectTransform _content;

    /// <summary>Line above the list saying what is being waited for.</summary>
    private readonly Text _statusText;

    /// <summary>The rows currently shown, destroyed and rebuilt whenever the list changes.</summary>
    private readonly List<GameObject> _rows = [];

    /// <summary>The button that opens the save selection, greyed out until someone else is in.</summary>
    private readonly Image _startImage;

    /// <summary>The label of the start button.</summary>
    private readonly Text _startText;

    /// <summary>The button that opens Steam's invite dialog.</summary>
    private readonly GameObject _inviteButton;

    private Action? _onInvite;
    private Action? _onStart;
    private Action? _onLeave;

    /// <summary>Whether the start button does anything, which is only true once a teammate is in.</summary>
    private bool _canStart;

    /// <summary>Tracks the panel's own active state.</summary>
    private bool _activeSelf;

    /// <summary>Parent component group for visibility management.</summary>
    private readonly ComponentGroup _componentGroup;

    private const float RowHeight = 34f;
    private const float RowSpacing = 6f;
    private const float Padding = 15f;
    private const float HeaderHeight = 32f;
    private const float StatusHeight = 26f;
    private const float ButtonAreaHeight = 58f;

    private static readonly Color Accent = new(1f, 0.85f, 0.6f, 1f);
    private static readonly Color RowBackground = new(0.12f, 0.12f, 0.15f, 1f);
    private static readonly Color StartEnabled = new(0.2f, 0.5f, 0.3f, 1f);
    private static readonly Color StartDisabled = new(0.18f, 0.18f, 0.2f, 1f);

    public WaitingRoomPanel(ComponentGroup parent, Vector2 position, Vector2 size) {
        GameObject = new GameObject("WaitingRoomPanel");
        var rect = GameObject.AddComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(position.x / 1920f, position.y / 1080f);
        rect.sizeDelta = size;
        rect.pivot = new Vector2(0.5f, 1f);

        CreateLabel(
            GameObject.transform,
            "Header",
            "WAITING ROOM",
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(0f, HeaderHeight),
            Vector2.zero,
            18,
            Accent
        );

        _statusText = CreateLabel(
            GameObject.transform,
            "Status",
            "",
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(0f, StatusHeight),
            new Vector2(0f, -HeaderHeight),
            15,
            new Color(0.75f, 0.75f, 0.75f, 1f)
        );

        // The list of who is in, between the status line and the buttons
        var listObj = new GameObject("List");
        _content = listObj.AddComponent<RectTransform>();
        _content.anchorMin = new Vector2(0f, 0f);
        _content.anchorMax = new Vector2(1f, 1f);
        _content.offsetMin = new Vector2(Padding, ButtonAreaHeight);
        _content.offsetMax = new Vector2(-Padding, -HeaderHeight - StatusHeight - 4f);
        listObj.transform.SetParent(GameObject.transform, false);

        var buttonArea = new GameObject("ButtonArea");
        var buttonAreaRect = buttonArea.AddComponent<RectTransform>();
        buttonAreaRect.anchorMin = new Vector2(0f, 0f);
        buttonAreaRect.anchorMax = new Vector2(1f, 0f);
        buttonAreaRect.pivot = new Vector2(0.5f, 0f);
        buttonAreaRect.anchoredPosition = Vector2.zero;
        buttonAreaRect.sizeDelta = new Vector2(0f, ButtonAreaHeight);
        buttonArea.transform.SetParent(GameObject.transform, false);

        _inviteButton = CreateButton(
            buttonArea.transform,
            "InviteButton",
            "INVITE FRIEND",
            new Vector2(0.02f, 0.12f),
            new Vector2(0.35f, 0.88f),
            new Color(0.2f, 0.35f, 0.55f, 1f),
            () => _onInvite?.Invoke(),
            out _,
            out _
        );

        CreateButton(
            buttonArea.transform,
            "LeaveButton",
            "LEAVE",
            new Vector2(0.37f, 0.12f),
            new Vector2(0.62f, 0.88f),
            new Color(0.15f, 0.15f, 0.18f, 1f),
            () => _onLeave?.Invoke(),
            out _,
            out _
        );

        // Refuses rather than silently doing nothing while it is greyed out, because a button that ignores presses
        // with no explanation is exactly what made the old flow so hard to read
        CreateButton(
            buttonArea.transform,
            "StartButton",
            "START",
            new Vector2(0.64f, 0.12f),
            new Vector2(0.98f, 0.88f),
            StartDisabled,
            () => {
                if (_canStart) {
                    _onStart?.Invoke();
                }
            },
            out _startImage,
            out _startText
        );

        _componentGroup = parent;
        _activeSelf = false;
        parent.AddComponent(this);
        GameObject.transform.SetParent(UiManager.UiGameObject!.transform, false);
        Object.DontDestroyOnLoad(GameObject);
        GameObject.SetActive(false);

        SetPlayers([], hosting: true);
    }

    /// <summary>
    /// Builds a text object, returning its <see cref="Text"/> so callers can change it later.
    /// </summary>
    private static Text CreateLabel(
        Transform parent,
        string name,
        string text,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 sizeDelta,
        Vector2 anchoredPosition,
        int fontSize,
        Color color
    ) {
        var obj = new GameObject(name);
        var rect = obj.AddComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = sizeDelta;

        var textComponent = obj.AddComponent<Text>();
        textComponent.text = text;
        textComponent.font = Resources.FontManager.UIFontRegular;
        textComponent.fontSize = fontSize;
        textComponent.alignment = TextAnchor.MiddleCenter;
        textComponent.color = color;
        obj.transform.SetParent(parent, false);

        return textComponent;
    }

    private static GameObject CreateButton(
        Transform parent,
        string name,
        string text,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Color backgroundColor,
        Action onClick,
        out Image image,
        out Text label
    ) {
        var obj = new GameObject(name);
        var rect = obj.AddComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        image = obj.AddComponent<Image>();
        image.color = backgroundColor;

        var textObj = new GameObject("Text");
        var textRect = textObj.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        label = textObj.AddComponent<Text>();
        label.text = text;
        label.font = Resources.FontManager.UIFontRegular;
        label.fontSize = 15;
        label.alignment = TextAnchor.MiddleCenter;
        label.color = Color.white;
        textObj.transform.SetParent(obj.transform, false);

        var button = obj.AddComponent<Button>();
        button.onClick.AddListener(() => onClick());

        obj.transform.SetParent(parent, false);
        Object.DontDestroyOnLoad(obj);
        return obj;
    }

    /// <summary>Sets what happens when the host asks to invite someone.</summary>
    public void SetOnInvite(Action callback) => _onInvite = callback;

    /// <summary>Sets what happens when the host starts, which is only reachable once a teammate is in.</summary>
    public void SetOnStart(Action callback) => _onStart = callback;

    /// <summary>Sets what happens when the waiting is given up on.</summary>
    public void SetOnLeave(Action callback) => _onLeave = callback;

    /// <summary>
    /// Shows who is in. The start button only does anything once somebody other than the host is listed, since a
    /// two-player save has nothing to check against on its own.
    /// </summary>
    /// <param name="names">Everyone in, the local player included.</param>
    /// <param name="hosting">Whether the local player is the one hosting, who alone can invite and start.</param>
    public void SetPlayers(IReadOnlyList<string> names, bool hosting) {
        foreach (var row in _rows) {
            Object.Destroy(row);
        }

        _rows.Clear();

        var y = -2f;
        foreach (var name in names) {
            _rows.Add(CreateRow(name, y));
            y -= RowHeight + RowSpacing;
        }

        _canStart = hosting && names.Count > 1;
        _inviteButton.SetActive(hosting);
        _startImage.color = _canStart ? StartEnabled : StartDisabled;
        _startText.color = _canStart ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f);

        _statusText.text = names.Count > 1
            ? hosting
                ? "Everyone is here. Start when you are ready."
                : "Waiting for the host to choose a save."
            : hosting
                ? "Waiting for your teammate. Invite them, or have them join your lobby."
                : "Waiting for the host.";
    }

    private GameObject CreateRow(string name, float y) {
        var row = new GameObject($"Player_{name}");
        var rect = row.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(0f, y);
        rect.sizeDelta = new Vector2(0f, RowHeight);

        var background = row.AddComponent<Image>();
        background.color = RowBackground;

        var label = CreateLabel(
            row.transform,
            "Name",
            name,
            new Vector2(0f, 0f),
            new Vector2(1f, 1f),
            Vector2.zero,
            Vector2.zero,
            15,
            Color.white
        );
        label.alignment = TextAnchor.MiddleCenter;

        row.transform.SetParent(_content.transform, false);
        Object.DontDestroyOnLoad(row);
        return row;
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

    /// <summary>Gets whether the panel is currently visible.</summary>
    public bool IsVisible => GameObject.activeSelf;

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
