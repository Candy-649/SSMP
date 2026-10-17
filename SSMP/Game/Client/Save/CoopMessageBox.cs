using System;
using System.Reflection;
using GlobalEnums;
using SSMP.Util;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The message box of the two-player save in the save menu. It looks like the game's text box for something with text
/// that the player inspects (DialogueBox: a dark backboard between two fleurs) and uses the menu's own buttons. It is
/// built on a copy of the game's error box, which darkens the screen behind it and takes the menu's input while it is
/// up.
///
/// It used to be the game's error box itself (GenericMessageCanvas): a plain black panel with one button, which the
/// game selects as the box opens. For a message that waits, like the one for the host's save, that button stopped
/// the waiting, so pressing the button that had just chosen the save once more stopped it at once (the user's report
/// of 10-17, which also asked for the look of the game's box for text in the world). A message that waits now has a
/// button that keeps waiting, selected as it opens, and one that stops; the back button stops as well.
/// </summary>
internal static class CoopMessageBox {
    /// <summary>
    /// How many units of the game's message canvas one unit of the HUD camera is: the camera shows 17.42 of its units
    /// over the height of the screen (orthographic size 8.71), which the canvas fills with 1080 of its own.
    /// </summary>
    private const float CanvasUnitsPerWorldUnit = 1080f / (2f * 8.710664f);

    // Where the parts of the game's text box are, in units of the HUD camera, from its prefab: its text is 17.85 wide,
    // and its backboard reaches 2.98 past the text on each side, 1.19 above it and 0.91 below it. The middle of its top
    // fleur is 0.2 below the top of the backboard, and the middle of its bottom fleur 0.39 above the bottom
    private const float TextWidth = 17.85f;
    private const float SideMargin = 2.98f;
    private const float TopMargin = 1.19f;
    private const float BottomMargin = 0.91f;
    private const float TopFleurDrop = 0.2f;
    private const float BottomFleurRise = 0.39f;

    /// <summary>
    /// How wide the box is, in units of the message canvas: as wide as the backboard of the game's text box.
    /// </summary>
    private const float BoxWidth = (TextWidth + 2f * SideMargin) * CanvasUnitsPerWorldUnit;

    /// <summary>
    /// The space between the text and the buttons, in units of the message canvas.
    /// </summary>
    private const float ButtonSpacing = 30f;

    /// <summary>
    /// Where the game's text box is under the HUD camera, whose backboard and fleurs are used.
    /// </summary>
    private const string DialogueBoxPath = "In-game/DialogueManager/DialogueBox";

    // Where the fleurs of the text box are in it: the ones it shows for any text that isn't a line of the player's own
    private const string TopFleurPath = "Fluers/Fluers/Fluers_Default/Dialogue_fleur_top_NPC0008";
    private const string BottomFleurPath = "Fluers/Fluers/Fluers_Default/Dialogue_fleur_bottom_NPC0008";

    /// <summary>
    /// The colour of the backboard: the game's text box draws it at 0.8 of its fade.
    /// </summary>
    private static readonly Color BackboardColor = new(1f, 1f, 1f, 0.8f);

    /// <summary>
    /// The field of a menu button that makes its back button run the cancel entry of its event trigger.
    /// </summary>
    private static readonly FieldInfo? HasCancelEventTriggerField = typeof(MenuSelectable).GetField(
        "hasCancelEventTrigger", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
    );

    /// <summary>
    /// The method of a menu button that finds the menu's sound player for it, which the button runs in its Start.
    /// </summary>
    private static readonly MethodInfo? HookUpAudioPlayerMethod = typeof(MenuSelectable).GetMethod(
        "HookUpAudioPlayer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
    );

    private static GameObject? _root;
    private static CanvasGroup? _group;
    private static Text? _text;
    private static MenuButton? _first;
    private static Text? _firstLabel;
    private static MenuButton? _second;
    private static Text? _secondLabel;

    /// <summary>
    /// The label of the game's own OK button, which the game keeps in its language.
    /// </summary>
    private static Text? _gameOkLabel;

    private static Action? _onFirst;
    private static Action? _onSecond;
    private static Action? _onBack;

    /// <summary>
    /// Whether the first button leaves the box up, as the one that keeps waiting does.
    /// </summary>
    private static bool _firstKeepsOpen;

    /// <summary>
    /// What the menu had selected before the box opened, which it selects again once the box closes.
    /// </summary>
    private static GameObject? _previouslySelected;

    /// <summary>
    /// Whether the game took the menu's input before the box opened, which the box takes while it is up.
    /// </summary>
    private static bool _wasInputActive;

    /// <summary>
    /// Whether the box is up.
    /// </summary>
    public static bool IsOpen { get; private set; }

    /// <summary>
    /// Shows a message with the game's OK button, which closes it, as the back button does.
    /// </summary>
    /// <param name="text">The message.</param>
    /// <param name="onClose">What runs once the player closed it, if anything.</param>
    public static void Show(string text, Action? onClose = null) {
        Open(text, null, false, onClose, null, null, onClose);
    }

    /// <summary>
    /// Shows a message about something that waits. Its first button, selected as it opens, keeps waiting and leaves the
    /// message up; the second stops waiting and closes it, as the back button does.
    /// </summary>
    /// <param name="text">The message.</param>
    /// <param name="stopLabel">The text of the button that stops waiting.</param>
    /// <param name="onStop">What runs once the player stopped waiting.</param>
    public static void ShowWait(string text, string stopLabel, Action onStop) {
        Open(text, Lang.Pick("Keep waiting", "继续等"), true, null, stopLabel, onStop, onStop);
    }

    /// <summary>
    /// Closes the box, if it is up, without running anything that it was shown with.
    /// </summary>
    public static void Close() {
        if (IsOpen) {
            Hide();
        }
    }

    private static void Open(
        string text,
        string? firstLabel,
        bool firstKeepsOpen,
        Action? onFirst,
        string? secondLabel,
        Action? onSecond,
        Action? onBack
    ) {
        if (!Build()) {
            return;
        }

        _text!.text = text;
        _firstLabel!.text = ButtonLabel(firstLabel ?? OkLabel());
        _firstKeepsOpen = firstKeepsOpen;
        _onFirst = onFirst;
        _onSecond = onSecond;
        _onBack = onBack;

        // A button that keeps the box up must stay selected when pressed, or the back button reaches nothing after it
        _first!.buttonType = firstKeepsOpen ? MenuButton.MenuButtonType.Activate : MenuButton.MenuButtonType.Proceed;

        var twoButtons = secondLabel != null;
        _second!.gameObject.SetActive(twoButtons);
        if (secondLabel != null) {
            _secondLabel!.text = ButtonLabel(secondLabel);
        }

        _first.navigation = new Navigation {
            mode = Navigation.Mode.Explicit,
            selectOnRight = twoButtons ? _second : null
        };
        _second.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnLeft = _first };

        // A message shown while another is up takes its place, and the menu gets back what it had before the first
        if (!IsOpen) {
            var eventSystem = EventSystem.current;
            _previouslySelected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
            _wasInputActive = InputHandler.Instance is { acceptingInput: true };
            IsOpen = true;
        }

        _group!.alpha = 1f;
        _group.interactable = true;
        _group.blocksRaycasts = true;
        if (InputHandler.Instance is { } inputHandler) {
            inputHandler.StartUIInput();
        }

        Select(_first);
    }

    /// <summary>
    /// Hides the box and gives the menu back its input and what it had selected, as the game's error box does.
    /// </summary>
    private static void Hide() {
        IsOpen = false;
        _onFirst = null;
        _onSecond = null;
        _onBack = null;

        var eventSystem = EventSystem.current;
        if (eventSystem != null) {
            eventSystem.SetSelectedGameObject(null);
        }

        if (_group != null) {
            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;
        }

        if (!_wasInputActive && InputHandler.Instance is { } inputHandler) {
            inputHandler.StopUIInput();
        }

        if (_previouslySelected != null && eventSystem != null) {
            eventSystem.SetSelectedGameObject(_previouslySelected);
        }

        _previouslySelected = null;
    }

    private static void OnFirstPressed() {
        if (!IsOpen || _firstKeepsOpen) {
            return;
        }

        var then = _onFirst;
        Hide();
        then?.Invoke();
    }

    private static void OnSecondPressed() {
        if (!IsOpen) {
            return;
        }

        var then = _onSecond;
        Hide();
        then?.Invoke();
    }

    private static void OnBackPressed() {
        if (!IsOpen) {
            return;
        }

        var then = _onBack;
        Hide();
        then?.Invoke();
    }

    private static void Select(Selectable? selectable) {
        var eventSystem = EventSystem.current;
        if (selectable != null && eventSystem != null) {
            eventSystem.SetSelectedGameObject(selectable.gameObject);
        }
    }

    /// <summary>
    /// The text of the game's OK button, in the language the game is in now.
    /// </summary>
    private static string OkLabel() {
        return _gameOkLabel != null && _gameOkLabel.text is { Length: > 0 } ok ? ok : Lang.Pick("OK", "确认");
    }

    /// <summary>
    /// A label as the game's menu buttons have theirs: ending in a line break, which the game adds to them
    /// (FixVerticalAlign, with a line spacing of its own) to bring the text to the middle of the button.
    /// </summary>
    private static string ButtonLabel(string label) {
        return label.EndsWith("\n") ? label : label + "\n";
    }

    /// <summary>
    /// A length in units of the HUD camera, in whole units of the message canvas.
    /// </summary>
    private static int ToCanvas(float worldUnits) {
        return Mathf.RoundToInt(worldUnits * CanvasUnitsPerWorldUnit);
    }

    /// <summary>
    /// Makes the box the first time it is shown, from a copy of the game's error box, laid out as the game's text box
    /// and in its look when that box is found. The copy stays under the game's UI manager, as the error box does, so
    /// it lasts as long as the menu.
    /// </summary>
    /// <returns>Whether the box is there.</returns>
    private static bool Build() {
        if (_root != null) {
            return true;
        }

        var source = Object.FindAnyObjectByType<GenericMessageCanvas>(FindObjectsInactive.Include);
        if (source == null) {
            Logger.Error("Co-op save message: the game's message box was not found, so nothing can be shown");
            return false;
        }

        var root = Object.Instantiate(source.gameObject, source.transform.parent);
        root.name = "SSMP Co-op Save Message";

        // The copy is driven from here: its own script would answer to nothing, and the game's box stays the game's
        Object.Destroy(root.GetComponent<GenericMessageCanvas>());

        var panel = root.transform.Find("Panel");
        var group = root.GetComponent<CanvasGroup>();
        var text = panel != null ? panel.Find("DescText")?.GetComponent<Text>() : null;
        var buttons = panel != null ? panel.Find("Buttons") : null;
        var first = buttons != null ? buttons.Find("OkButton")?.GetComponent<MenuButton>() : null;
        var firstLabel = first != null ? first.transform.Find("Text")?.GetComponent<Text>() : null;
        if (panel == null || group == null || text == null || buttons == null || first == null || firstLabel == null) {
            Logger.Error("Co-op save message: the game's message box has changed, so nothing can be shown");
            Object.Destroy(root);
            return false;
        }

        _gameOkLabel = source.transform.Find("Panel/Buttons/OkButton/Text")?.GetComponent<Text>();

        // Its own label would put the game's OK back each time the button is switched on. At once, so that the second
        // button, copied from this one, is made without it
        if (firstLabel.GetComponent<AutoLocalizeTextUI>() is { } localizer) {
            Object.DestroyImmediate(localizer);
        }
        HookUp(first, OnFirstPressed);

        var second = Object.Instantiate(first.gameObject, buttons).GetComponent<MenuButton>();
        second.name = "SecondButton";
        HookUp(second, OnSecondPressed);

        var title = panel.Find("LabelText");
        if (title != null) {
            title.gameObject.SetActive(false);
        }

        text.alignment = TextAnchor.UpperCenter;
        text.transform.SetSiblingIndex(0);
        buttons.SetSiblingIndex(1);

        // The box is as wide as the game's text box, with the text as far inside it, and as tall as what it holds, in
        // the middle of the screen
        var panelRect = (RectTransform) panel;
        panelRect.anchorMin = panelRect.anchorMax = new Vector2(0.5f, 0.5f);
        panelRect.pivot = new Vector2(0.5f, 0.5f);
        panelRect.anchoredPosition = Vector2.zero;
        panelRect.sizeDelta = new Vector2(BoxWidth, panelRect.sizeDelta.y);
        if (panel.GetComponent<VerticalLayoutGroup>() is { } layout) {
            layout.padding = new RectOffset(
                ToCanvas(SideMargin), ToCanvas(SideMargin), ToCanvas(TopMargin), ToCanvas(BottomMargin)
            );
            layout.spacing = ButtonSpacing;
            layout.childAlignment = TextAnchor.UpperCenter;
        }

        Restyle(panel);

        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false;

        _root = root;
        _group = group;
        _text = text;
        _first = first;
        _firstLabel = firstLabel;
        _second = second;
        _secondLabel = second.transform.Find("Text")?.GetComponent<Text>() ?? firstLabel;
        return true;
    }

    /// <summary>
    /// Clears what a copied button did, which still pointed at the game's error box, and has it run the given action
    /// when pressed, and <see cref="OnBackPressed"/> on the back button.
    /// </summary>
    private static void HookUp(MenuButton button, UnityAction onPressed) {
        button.OnSubmitPressed = new UnityEvent();
        button.OnSubmitPressed.AddListener(onPressed);

        var trigger = button.GetComponent<EventTrigger>() ?? button.gameObject.AddComponent<EventTrigger>();
        trigger.triggers.Clear();
        var back = new EventTrigger.Entry { eventID = EventTriggerType.Cancel };
        back.callback.AddListener(_ => OnBackPressed());
        trigger.triggers.Add(back);

        // Not one of the menu's own ways back, which would leave the save menu behind the box
        button.cancelAction = CancelAction.DoNothing;
        HasCancelEventTriggerField?.SetValue(button, true);

        // The button would find its sound player only in its Start, a frame after the box is made, but the box selects
        // its first button at once, and a selection without one logs an error and plays no sound
        HookUpAudioPlayerMethod?.Invoke(button, null);
    }

    /// <summary>
    /// Gives the box the backboard and the fleurs of the game's text box. The plain dark panel of the error box stays
    /// where that box is not found.
    /// </summary>
    private static void Restyle(Transform panel) {
        var cameras = GameCameras.instance;
        var hud = cameras != null ? cameras.hudCamera : null;
        var dialogueBox = hud != null ? hud.transform.Find(DialogueBoxPath) : null;
        if (dialogueBox == null) {
            Logger.Warn("Co-op save message: the game's text box was not found, so the message stays plain");
            return;
        }

        var backboard = dialogueBox.Find("backboard")?.GetComponent<SpriteRenderer>();
        var image = panel.GetComponent<Image>();
        if (backboard != null && backboard.sprite != null && image != null) {
            image.sprite = backboard.sprite;
            image.type = Image.Type.Simple;
            image.color = BackboardColor;
        }

        AddFleur(panel, dialogueBox.Find(TopFleurPath)?.GetComponent<SpriteRenderer>(), true);
        AddFleur(panel, dialogueBox.Find(BottomFleurPath)?.GetComponent<SpriteRenderer>(), false);
    }

    /// <summary>
    /// Draws a fleur of the game's text box as large as it is there, where it sits on the top or the bottom of the
    /// backboard.
    /// </summary>
    private static void AddFleur(Transform panel, SpriteRenderer? renderer, bool top) {
        if (renderer == null || renderer.sprite is not { } sprite) {
            return;
        }

        var fleur = new GameObject(top ? "Top Fleur" : "Bottom Fleur", typeof(RectTransform), typeof(Image));
        fleur.layer = panel.gameObject.layer;
        fleur.AddComponent<LayoutElement>().ignoreLayout = true;

        var rect = (RectTransform) fleur.transform;
        rect.SetParent(panel, false);
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, top ? 1f : 0f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(0f, (top ? -TopFleurDrop : BottomFleurRise) * CanvasUnitsPerWorldUnit);
        var size = Vector2.Scale(sprite.rect.size / sprite.pixelsPerUnit, renderer.transform.localScale);
        rect.sizeDelta = size * CanvasUnitsPerWorldUnit;

        var image = fleur.GetComponent<Image>();
        image.sprite = sprite;
        image.preserveAspect = true;
        image.raycastTarget = false;
    }
}
