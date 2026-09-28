using TMPro;
using UnityEngine;

namespace SSMP.Ui.Component;

/// <summary>
/// A line of the chat. Drawn by TextMeshPro rather than as a <see cref="TextComponent"/>, because a Unity text draws
/// no emoji: see <see cref="Resources.FontManager.DrawInChatFont"/>.
/// </summary>
internal class ChatTextComponent : Component, ITextComponent {
    /// <summary>
    /// The TextMeshPro text.
    /// </summary>
    private readonly TextMeshProUGUI _text;

    public ChatTextComponent(
        ComponentGroup componentGroup,
        Vector2 position,
        Vector2 size,
        string text,
        int fontSize
    ) : base(componentGroup, position, size) {
        _text = GameObject.AddComponent<TextMeshProUGUI>();
        Resources.FontManager.DrawInChatFont(_text);
        _text.fontSize = fontSize;
        _text.alignment = TextAlignmentOptions.BottomLeft;
        _text.textWrappingMode = TextWrappingModes.NoWrap;
        _text.raycastTarget = false;
        _text.text = text;

        // The chat sits against the left edge, so it has to be anchored to it, or it slides off a screen that is
        // narrower than 16:9
        AnchorToLeftEdge();
    }

    /// <inheritdoc />
    public void SetText(string text) {
        _text.text = text;
    }

    /// <inheritdoc />
    public void SetColor(Color color) {
        _text.color = color;
    }

    /// <summary>
    /// Get the current color of the text.
    /// </summary>
    /// <returns>The color of the text.</returns>
    public Color GetColor() {
        return _text.color;
    }

    /// <summary>
    /// How wide a text would be drawn as a line of the chat. Asking leaves this line holding the text it measured,
    /// so it is for a line that is never shown.
    /// </summary>
    /// <param name="text">The text to measure.</param>
    /// <returns>Its width in the units of the UI.</returns>
    public float GetPreferredWidth(string text) {
        return _text.GetPreferredValues(text).x;
    }
}
