using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using SSMP.Util;

namespace SSMP.Ui.Component;

/// <summary>
/// One player in the waiting room, and whether they have said they are ready.
/// </summary>
internal readonly struct WaitingRoomMember {
    /// <summary>The name shown for this player.</summary>
    public string Name { get; }

    /// <summary>Whether this player has said they are ready to choose a save.</summary>
    public bool Ready { get; }

    public WaitingRoomMember(string name, bool ready) {
        Name = name;
        Ready = ready;
    }
}

/// <summary>
/// The screen both players wait on before either of them chooses a save, listing who is in and who has said they are
/// ready.
///
/// Hosting used to go from creating a lobby straight into choosing a save, which loaded the game before anyone could
/// join and left nowhere obvious to invite from. Worse, whoever joined was dropped into their own save menu while the
/// other player was still waiting - the two sides were never looking at the same thing. Both sides wait here instead,
/// and neither save menu opens until both have said they are ready.
/// </summary>
internal class WaitingRoomPanel : IComponent {
    /// <summary>The root GameObject for this panel.</summary>
    private GameObject GameObject { get; }

    /// <summary>Container the player rows are built in.</summary>
    private readonly RectTransform _content;

    /// <summary>Line above the list saying what is being waited for.</summary>
    private readonly Text _statusText;

    /// <summary>The rows currently shown, destroyed and rebuilt whenever anything about the room changes.</summary>
    private readonly List<GameObject> _rows = [];

    /// <summary>The background of the ready button, which says at a glance whether it is on.</summary>
    private readonly Image _readyImage;

    /// <summary>The label of the ready button, which says what pressing it does next.</summary>
    private readonly Text _readyText;

    /// <summary>The button that opens Steam's invite dialog, which only the host has anyone to invite with.</summary>
    private readonly GameObject _inviteButton;

    private Action? _onInvite;
    private Action? _onReady;
    private Action? _onLeave;

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
    private static readonly Color ReadyOn = new(0.2f, 0.5f, 0.3f, 1f);
    private static readonly Color ReadyOff = new(0.35f, 0.3f, 0.15f, 1f);
    private static readonly Color ReadyMark = new(0.45f, 0.85f, 0.55f, 1f);
    private static readonly Color WaitingMark = new(0.6f, 0.6f, 0.6f, 1f);

    public WaitingRoomPanel(ComponentGroup parent, Vector2 position, Vector2 size) {
        GameObject = new GameObject("WaitingRoomPanel");
        var rect = GameObject.AddComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(position.x / 1920f, position.y / 1080f);
        rect.sizeDelta = size;
        rect.pivot = new Vector2(0.5f, 1f);

        CreateLabel(
            GameObject.transform,
            "Header",
            Lang.Pick("WAITING ROOM", "等待房间"),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(0f, HeaderHeight),
            Vector2.zero,
            18,
            Accent,
            TextAnchor.MiddleCenter
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
            new Color(0.75f, 0.75f, 0.75f, 1f),
            TextAnchor.MiddleCenter
        );

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
            Lang.Pick("INVITE FRIEND", "邀请好友"),
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
            Lang.Pick("LEAVE", "离开"),
            new Vector2(0.37f, 0.12f),
            new Vector2(0.62f, 0.88f),
            new Color(0.15f, 0.15f, 0.18f, 1f),
            () => _onLeave?.Invoke(),
            out _,
            out _
        );

        // Always pressable, unlike the start button this replaces. Both players say they are ready and both save
        // menus open together, so neither side is left pressing something that silently does nothing.
        CreateButton(
            buttonArea.transform,
            "ReadyButton",
            Lang.Pick("I'M READY", "我准备好了"),
            new Vector2(0.64f, 0.12f),
            new Vector2(0.98f, 0.88f),
            ReadyOff,
            () => _onReady?.Invoke(),
            out _readyImage,
            out _readyText
        );

        _componentGroup = parent;
        _activeSelf = false;
        parent.AddComponent(this);
        GameObject.transform.SetParent(UiManager.UiGameObject!.transform, false);
        Object.DontDestroyOnLoad(GameObject);
        GameObject.SetActive(false);

        SetRoom([], localReady: false, hosting: true);
    }

    private static Text CreateLabel(
        Transform parent,
        string name,
        string text,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 sizeDelta,
        Vector2 anchoredPosition,
        int fontSize,
        Color color,
        TextAnchor alignment
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
        textComponent.alignment = alignment;
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

    /// <summary>Sets what happens when the local player says they are ready, or takes it back.</summary>
    public void SetOnReady(Action callback) => _onReady = callback;

    /// <summary>Sets what happens when the waiting is given up on.</summary>
    public void SetOnLeave(Action callback) => _onLeave = callback;

    /// <summary>
    /// Shows who is in and who is ready.
    /// </summary>
    /// <param name="members">Everyone in, the local player included.</param>
    /// <param name="localReady">Whether the local player has said they are ready.</param>
    /// <param name="hosting">Whether the local player is hosting, who alone has a lobby to invite to.</param>
    public void SetRoom(IReadOnlyList<WaitingRoomMember> members, bool localReady, bool hosting) {
        foreach (var row in _rows) {
            Object.Destroy(row);
        }

        _rows.Clear();

        var y = -2f;
        foreach (var member in members) {
            _rows.Add(CreateRow(member, y));
            y -= RowHeight + RowSpacing;
        }

        _inviteButton.SetActive(hosting);
        _readyImage.color = localReady ? ReadyOn : ReadyOff;
        _readyText.text = localReady
            ? Lang.Pick("CANCEL READY", "取消准备")
            : Lang.Pick("I'M READY", "我准备好了");

        var alone = members.Count < 2;
        var waitingOn = 0;
        foreach (var member in members) {
            if (!member.Ready) {
                waitingOn++;
            }
        }

        // Each line is picked whole rather than assembled from translated pieces: a sentence stitched together from
        // fragments reads like one in English and like nothing at all in Chinese, and it is how the other half of
        // this panel ended up half-translated.
        _statusText.text = alone
            ? hosting
                ? Lang.Pick(
                    "Waiting for your teammate. Invite them, or have them join your lobby.",
                    "正在等队友。邀请他们，或者让他们自己进你的房间。"
                )
                : Lang.Pick("Waiting for your teammate.", "正在等队友。")
            : waitingOn == 0
                ? Lang.Pick("Everyone is ready. Choose your saves.", "都准备好了，挑存档吧。")
                : localReady
                    ? Lang.Pick("Waiting for your teammate to be ready.", "正在等队友准备。")
                    : Lang.Pick(
                        "Say you are ready when you want to choose your saves.",
                        "想开始挑存档的话，点一下「我准备好了」。"
                    );
    }

    private GameObject CreateRow(WaitingRoomMember member, float y) {
        var row = new GameObject($"Player_{member.Name}");
        var rect = row.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(0f, y);
        rect.sizeDelta = new Vector2(0f, RowHeight);

        var background = row.AddComponent<Image>();
        background.color = RowBackground;

        CreateLabel(
            row.transform,
            "Name",
            member.Name,
            new Vector2(0f, 0f),
            new Vector2(0.65f, 1f),
            Vector2.zero,
            Vector2.zero,
            15,
            Color.white,
            TextAnchor.MiddleLeft
        ).rectTransform.offsetMin = new Vector2(12f, 0f);

        CreateLabel(
            row.transform,
            "State",
            member.Ready ? Lang.Pick("READY", "已准备") : Lang.Pick("NOT READY", "未准备"),
            new Vector2(0.65f, 0f),
            new Vector2(1f, 1f),
            Vector2.zero,
            Vector2.zero,
            14,
            member.Ready ? ReadyMark : WaitingMark,
            TextAnchor.MiddleRight
        ).rectTransform.offsetMax = new Vector2(-12f, 0f);

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
