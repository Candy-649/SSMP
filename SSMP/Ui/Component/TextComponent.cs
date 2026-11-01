using UnityEngine;
using UnityEngine.UI;

namespace SSMP.Ui.Component;

/// <inheritdoc cref="ITextComponent" />
internal class TextComponent : Component, ITextComponent {
    /// <summary>
    /// The Unity Text component.
    /// </summary>
    private readonly Text _textObject;

    public TextComponent(
        ComponentGroup componentGroup,
        Vector2 position,
        Vector2 size,
        string text,
        int fontSize,
        FontStyle fontStyle = FontStyle.Normal,
        TextAnchor alignment = TextAnchor.MiddleCenter,
        bool wrap = false,
        bool anchorLeft = false
    ) : this(
        componentGroup,
        position,
        size,
        new Vector2(0.5f, 0.5f),
        text,
        fontSize,
        fontStyle,
        alignment,
        wrap,
        anchorLeft
    ) {
    }

    public TextComponent(
        ComponentGroup componentGroup,
        Vector2 position,
        Vector2 size,
        Vector2 pivot,
        string text,
        int fontSize,
        FontStyle fontStyle = FontStyle.Normal,
        TextAnchor alignment = TextAnchor.MiddleCenter,
        bool wrap = false,
        bool anchorLeft = false
    ) : base(componentGroup, position, size) {
        _textObject = CreateTextObject(text, fontSize, fontStyle, alignment, pivot, wrap);
        AddSizeFitter();
        AddOutline();

        // Anything sitting against the left edge has to be anchored to it, or it slides off a screen that is
        // narrower than 16:9
        if (anchorLeft) {
            AnchorToLeftEdge();
        }
    }

    /// <inheritdoc />
    public void SetText(string text) {
        _textObject.text = text;
    }

    /// <inheritdoc />
    public void SetColor(Color color) {
        _textObject.color = color;
    }

    /// <summary>
    /// Get the current color of the text.
    /// </summary>
    /// <returns>The color of the text.</returns>
    public Color GetColor() {
        return _textObject.color;
    }
    
    /// <summary>
    /// Create the Unity Text object with all the parameters.
    /// </summary>
    /// <returns>The Unity <see cref="Text"/> object.</returns>
    private Text CreateTextObject(
        string text,
        int fontSize,
        FontStyle fontStyle,
        TextAnchor alignment,
        Vector2 pivot,
        bool wrap
    ) {
        var textObj = GameObject.AddComponent<WrappingText>();
        
        textObj.supportRichText = true;
        textObj.text = text;
        textObj.font = Resources.FontManager.UIFontRegular;
        textObj.fontSize = fontSize;
        textObj.fontStyle = fontStyle;
        textObj.alignment = alignment;
        textObj.horizontalOverflow = wrap ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
        textObj.verticalOverflow = VerticalWrapMode.Overflow;
        textObj.rectTransform.pivot = pivot;
        textObj.raycastTarget = false; // do not block input
        
        return textObj;
    }

    /// <summary>
    /// Add a size fitter to the game object for this text.
    /// </summary>
    private void AddSizeFitter() {
        var sizeFitter = GameObject.AddComponent<ContentSizeFitter>();
        sizeFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        sizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
    }

    /// <summary>
    /// Add an outline to the text.
    /// </summary>
    private void AddOutline() {
        var outline = GameObject.AddComponent<Outline>();
        outline.effectColor = Color.black;
    }
}
