using System;
using System.Collections.Generic;
using SSMP.Networking.Packet.Data;
using SSMP.Ui.Resources;
using SSMP.Util;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SSMP.Ui.Component;

/// <summary>
/// An input component specifically for the chat. It is TextMeshPro's input field, not Unity's like every other input
/// of this mod: Unity's turns away any character its face has no glyph for, so an emoji could never be typed into it,
/// and it could not have drawn one either.
/// </summary>
internal class ChatInputComponent : Component {
    /// <summary>
    /// The margin of the text with the borders of the component.
    /// </summary>
    private const float TextMargin = 5f;

    /// <summary>
    /// List of characters that are disallowed to be input.
    /// </summary>
    private static readonly List<char> DisallowedChars = ['\n'];

    /// <summary>
    /// Action that is executed when the user submits the input field.
    /// </summary>
    public event Action<string>? OnSubmit;

    /// <summary>
    /// The TextMeshPro input field.
    /// </summary>
    private readonly TMP_InputField _inputField;

    public ChatInputComponent(
        ComponentGroup componentGroup,
        Vector2 position,
        Vector2 size,
        int fontSize
    ) : base(componentGroup, position, size) {
        // The chat sits against the left edge, so it has to be anchored to that edge rather than to a fraction of the
        // screen's width, which slides inwards and off on a screen narrower than 16:9
        AnchorToLeftEdge();

        var image = GameObject.AddComponent<Image>();
        image.sprite = TextureManager.InputFieldBg.Neutral;
        image.type = Image.Type.Sliced;

        // A line longer than the box is scrolled through rather than cut short, and this keeps what is scrolled
        // aside from being drawn outside the box
        var textArea = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
        textArea.transform.SetParent(GameObject.transform, false);
        var textAreaTransform = (RectTransform) textArea.transform;
        textAreaTransform.anchorMin = Vector2.zero;
        textAreaTransform.anchorMax = Vector2.one;
        textAreaTransform.offsetMin = new Vector2(TextMargin, 0f);
        textAreaTransform.offsetMax = new Vector2(-TextMargin, 0f);

        var textObject = new GameObject("Text", typeof(RectTransform));
        textObject.transform.SetParent(textArea.transform, false);
        var textTransform = (RectTransform) textObject.transform;
        textTransform.anchorMin = Vector2.zero;
        textTransform.anchorMax = Vector2.one;
        textTransform.offsetMin = textTransform.offsetMax = Vector2.zero;

        var text = textObject.AddComponent<TextMeshProUGUI>();
        text.font = Resources.FontManager.ChatInputFont;
        text.fontSize = fontSize;
        text.alignment = TextAlignmentOptions.MidlineLeft;

        // The field makes its caret as it is switched on, next to the text it has been given by then. Added to an
        // object that is on, it is switched on before it can be given one, and never makes a caret at all
        GameObject.SetActive(false);
        _inputField = GameObject.AddComponent<TMP_InputField>();
        _inputField.textViewport = textAreaTransform;
        _inputField.textComponent = text;
        _inputField.richText = false;
        _inputField.characterLimit = ChatMessage.MaxMessageLength;
        // Two units wide, as in InputComponent: one is drawn a single pixel wide on any screen short of 4K
        _inputField.caretWidth = 2;

        _inputField.onValidateInput += (_, _, addedChar) => DisallowedChars.Contains(addedChar) ? '\0' : addedChar;

        // This listens for as long as the game runs, not only while the chat is open, so it has to ask whether anyone
        // is actually typing. Without that, Enter pressed anywhere - it is also the confirm key of the game's own
        // menus, and not one a player can rebind - submits an empty line here, and submitting closes the chat, which
        // hands back mouse input, the pause menu and every hero action even though nothing had taken them away.
        MonoBehaviourUtil.Instance.OnUpdateEvent += () => {
            if (_inputField.gameObject.activeInHierarchy && Input.GetKeyDown(KeyCode.Return)) {
                OnSubmit?.Invoke(_inputField.text);

                _inputField.text = "";
            }
        };
    }

    /// <summary>
    /// Focus the input field.
    /// </summary>
    public void Focus() {
        _inputField.ActivateInputField();
    }
}
