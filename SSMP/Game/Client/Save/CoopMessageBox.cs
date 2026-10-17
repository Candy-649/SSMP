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
/// The message box of the two-player save in the save menu. It looks like the game's own box for questions in the
/// world (DialogueYesNoBox: a dark backboard between two fleurs) and uses the menu's own buttons. It is built on a copy
/// of the game's error box, which darkens the screen behind it and takes the menu's input while it is up.
///
/// It used to be the game's error box itself (GenericMessageCanvas): a plain black panel with one button, which the
/// game selects as the box opens. For a message that waits, like the one for the host's save, that button stopped
/// the waiting, so pressing the button that had just chosen the save once more stopped it at once (the user's report
/// of 10-17, which also asked for the look of the game's boxes for text in the world). A message that waits now has a
/// button that keeps waiting, selected as it opens, and one that stops; the back button stops as well.
/// </summary>
internal static class CoopMessageBox {
    /// <summary>
    /// How wide the box is, in units of the game's message canvas, which is 1920 wide.
    /// </summary>
    private const float BoxWidth = 1000f;

    /// <summary>
    /// How wide a fleur is drawn, in the same units.
    /// </summary>
    private const float FleurWidth = 760f;

    /// <summary>
    /// Where the game's box for questions in the world is under the HUD camera, whose backboard and fleurs are used.
    /// </summary>
    private const string YesNoPanePath = "In-game/DialogueManager/DialogueYesNoBox/Pane";

    /// <summary>
    /// The colour of the backboard in the game's box for questions in the world.
    /// </summary>
    private static readonly Color BackboardColor = new(1f, 1f, 1f, 0.8f);

    /// <summary>
    /// The field of a menu button that makes its back button run the cancel entry of its event trigger.
    /// </summary>
    private static readonly FieldInfo? HasCancelEventTriggerField = typeof(MenuSelectable).GetField(
        "hasCancelEventTrigger", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
    );

    private static GameObject? _root;
    private static CanvasGroup? _group;
    private static Text? _text;
    private static MenuButton? _first;
    private static Text? _firstLabel;
    private static MenuButton? _second;
    private static Text? _secondLabel;

    /// <summary>
    /// The text of the game's own OK button, in the game's language.
    /// </summary>
    private static string _okLabel = "";

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
        _firstLabel!.text = firstLabel ?? _okLabel;
        _firstKeepsOpen = firstKeepsOpen;
        _onFirst = onFirst;
        _onSecond = onSecond;
        _onBack = onBack;

        // A button that keeps the box up must stay selected when pressed, or the back button reaches nothing after it
        _first!.buttonType = firstKeepsOpen ? MenuButton.MenuButtonType.Activate : MenuButton.MenuButtonType.Proceed;

        var twoButtons = secondLabel != null;
        _second!.gameObject.SetActive(twoButtons);
        if (twoButtons) {
            _secondLabel!.text = secondLabel;
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
    /// Makes the box the first time it is shown, from a copy of the game's error box, in the look of the game's box for
    /// questions in the world when that box is found. The copy stays under the game's UI manager, as the error box
    /// does, so it lasts as long as the menu.
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

        _okLabel = firstLabel.text is { Length: > 0 } ok ? ok : Lang.Pick("OK", "确认");

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

        // The box is as wide as given and as tall as what it holds, in the middle of the screen
        var panelRect = (RectTransform) panel;
        panelRect.anchorMin = panelRect.anchorMax = new Vector2(0.5f, 0.5f);
        panelRect.pivot = new Vector2(0.5f, 0.5f);
        panelRect.anchoredPosition = Vector2.zero;
        panelRect.sizeDelta = new Vector2(BoxWidth, panelRect.sizeDelta.y);

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
    }

    /// <summary>
    /// Gives the box the backboard and the fleurs of the game's box for questions in the world, with room inside for
    /// the backboard's soft edges. The plain dark panel of the error box stays where that box is not found.
    /// </summary>
    private static void Restyle(Transform panel) {
        var cameras = GameCameras.instance;
        var hud = cameras != null ? cameras.hudCamera : null;
        var pane = hud != null ? hud.transform.Find(YesNoPanePath) : null;
        if (pane == null) {
            Logger.Warn("Co-op save message: the game's box for questions was not found, so the message stays plain");
            return;
        }

        var backboard = pane.Find("Contents Layout/backboard")?.GetComponent<Image>();
        var image = panel.GetComponent<Image>();
        if (backboard != null && backboard.sprite != null && image != null) {
            image.sprite = backboard.sprite;
            image.type = Image.Type.Simple;
            image.color = BackboardColor;
        }

        var topFleur = pane.Find("Top Fluer")?.GetComponent<SpriteRenderer>();
        var bottomFleur = pane.Find("Contents Layout/Bottom Fluer")?.GetComponent<SpriteRenderer>();
        var topHeight = AddFleur(panel, topFleur, true);
        var bottomHeight = AddFleur(panel, bottomFleur, false);

        // The fleurs sit on the edges, half inside the box, so the text keeps clear of the half that is
        if (panel.GetComponent<VerticalLayoutGroup>() is { } layout) {
            layout.padding = new RectOffset(
                110, 110, Mathf.Max(70, Mathf.CeilToInt(topHeight / 2f) + 30),
                Mathf.Max(55, Mathf.CeilToInt(bottomHeight / 2f) + 25)
            );
            layout.spacing = 30f;
            layout.childAlignment = TextAnchor.UpperCenter;
        }
    }

    /// <summary>
    /// Draws a fleur of the game's box for questions on the middle of the top or the bottom edge of the box.
    /// </summary>
    /// <returns>How tall the fleur is drawn, or 0 without one.</returns>
    private static float AddFleur(Transform panel, SpriteRenderer? renderer, bool top) {
        if (renderer == null || renderer.sprite is not { } sprite || sprite.rect.width <= 0f) {
            return 0f;
        }

        var fleur = new GameObject(top ? "Top Fleur" : "Bottom Fleur", typeof(RectTransform), typeof(Image));
        fleur.layer = panel.gameObject.layer;
        fleur.AddComponent<LayoutElement>().ignoreLayout = true;

        var rect = (RectTransform) fleur.transform;
        rect.SetParent(panel, false);
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, top ? 1f : 0f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = sprite.rect.size * (FleurWidth / sprite.rect.width);

        var image = fleur.GetComponent<Image>();
        image.sprite = sprite;
        image.preserveAspect = true;
        image.raycastTarget = false;
        return rect.sizeDelta.y;
    }
}
