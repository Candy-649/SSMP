using SSMP.Ui.Component;
using UnityEngine;

namespace SSMP.Ui;

/// <summary>
/// A single line shown in game that offers a two-player save with the other player and names the key that agrees to
/// one. It is a line with a key rather than a box with buttons, because nothing in game shows a mouse cursor to click
/// a button with: the chat is driven by a keybind for the same reason.
/// </summary>
internal class CoopPromptInterface {
    /// <summary>
    /// The width of the line, wide enough for a long player name.
    /// </summary>
    private const float Width = 900f;

    /// <summary>
    /// The height of the line.
    /// </summary>
    private const float Height = 40f;

    /// <summary>
    /// How far below the top of the screen the line sits, clear of the ping display in the corner.
    /// </summary>
    private const float TopMargin = 140f;

    /// <summary>
    /// The component group of the line, which is what hides and shows it.
    /// </summary>
    private readonly ComponentGroup _group;

    /// <summary>
    /// The line itself.
    /// </summary>
    private readonly TextComponent _text;

    /// <summary>
    /// Whether the line is being shown, kept here rather than asked of the group, whose own answer also depends on
    /// the hierarchy above it.
    /// </summary>
    private bool _visible;

    /// <summary>
    /// The text that is on screen, so that the same text is not set again every frame.
    /// </summary>
    private string _shown = "";

    public CoopPromptInterface(ComponentGroup group) {
        _group = group;

        // Nothing to offer until a game is joined
        group.SetActive(false);

        _text = new TextComponent(
            group,
            new Vector2(1920f / 2f, 1080f - TopMargin),
            new Vector2(Width, Height),
            "",
            UiManager.NormalFontSize
        );
    }

    /// <summary>
    /// Shows the given text, or only changes it when the line already shows something else.
    /// </summary>
    /// <param name="text">The text to show.</param>
    public void Show(string text) {
        if (text != _shown) {
            _shown = text;
            _text.SetText(text);
        }

        if (!_visible) {
            _visible = true;
            _group.SetActive(true);
        }
    }

    /// <summary>
    /// Takes the line off the screen.
    /// </summary>
    public void Hide() {
        _shown = "";

        if (_visible) {
            _visible = false;
            _group.SetActive(false);
        }
    }
}
