using System;
using System.Collections.Generic;
using System.Reflection;
using InControl;
using MonoMod.RuntimeDetour;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Ui.Util;

/// <summary>
/// Moving around the mod's own menus with a gamepad (USER 2026-10-22: "顺手把手柄在联机界面的移动做了吧"). The game's
/// own menus are driven by its input module, which moves between Unity's selectable objects with the stick and the
/// pad's buttons, and presses the one that is selected. The mod's buttons, tabs and boxes to tick were images that only
/// a mouse could click, so the module had nothing to move to: each of them now is such an object too, which the module
/// moves to and presses like the game's own (see <see cref="MakeSelectable"/>). The mod's lists and panels were made of
/// Unity's buttons already, and its text boxes are Unity's text boxes.
///
/// Since the mod's controls draw their own looks rather than Unity's, the one that is selected is shown by a frame
/// around it (see <see cref="PadFocusFrame"/>), and something is always selected while a menu of the mod shows, so that
/// the first press of the stick has somewhere to start from (see <see cref="KeepSelection"/>).
/// </summary>
internal static class PadNavigation {
    /// <summary>
    /// The selectable objects under the mod's interface, kept to be filled again.
    /// </summary>
    private static readonly List<Selectable> Found = [];

    /// <summary>
    /// Makes a control of the mod's own a selectable object that the game's input module moves to with the stick and
    /// presses with the pad's confirm button, which then does what a click does.
    /// </summary>
    /// <param name="gameObject">The object of the control.</param>
    /// <param name="eventTrigger">The trigger of the control's mouse events, which the press is added to.</param>
    /// <param name="press">What a press does.</param>
    /// <returns>The selectable, whose interactable follows the control's.</returns>
    public static Selectable MakeSelectable(GameObject gameObject, EventTrigger eventTrigger, Action press) {
        var selectable = gameObject.AddComponent<Selectable>();
        selectable.transition = Selectable.Transition.None;
        selectable.navigation = new Navigation { mode = Navigation.Mode.Automatic };

        var entry = new EventTrigger.Entry { eventID = EventTriggerType.Submit };
        entry.callback.AddListener(_ => press());
        eventTrigger.triggers.Add(entry);
        return selectable;
    }

    /// <summary>
    /// Where the last control of the mod's interface that was selected is, if one was.
    /// </summary>
    private static Vector3? _lastSelected;

    /// <summary>
    /// The control that a list was last scrolled to show, so that it is only scrolled when the selection moves.
    /// </summary>
    private static GameObject? _scrolledTo;

    /// <summary>
    /// The frame in which a menu of the mod last kept its selection, which is when the frame around it may show.
    /// </summary>
    private static int _keptInFrame = -1;

    /// <summary>
    /// The corners of a control, kept to be filled again.
    /// </summary>
    private static readonly Vector3[] Corners = new Vector3[4];

    /// <summary>
    /// Whether a menu of the mod keeps a selection this frame, which is when the frame around what is selected shows:
    /// not around the chat's box while typing in game, which is under the same root.
    /// </summary>
    public static bool IsMenuShown => _keptInFrame == Time.frameCount;

    /// <summary>
    /// Makes sure that something of the mod's interface under a root is selected while it shows. When nothing is, or
    /// what is selected is hidden, switched off or not the mod's - as when a button opened a panel in its place, or a
    /// panel was closed - the control nearest to the one selected last is selected, so that a player stays where they
    /// were; with none selected before, the top one, the leftmost of a row. A button is preferred over a text box, as
    /// a gamepad cannot type into one. A control that greys out stays selected, as one pressed to connect does while
    /// it connects: moving on to the nearest would have the next press start something else.
    /// </summary>
    /// <param name="root">The root of the menu that shows.</param>
    public static void KeepSelection(Transform root) {
        _keptInFrame = Time.frameCount;
        var eventSystem = EventSystem.current;
        if (eventSystem == null) {
            return;
        }

        var selected = eventSystem.currentSelectedGameObject;
        if (selected != null && selected.activeInHierarchy && selected.transform.IsChildOf(root) &&
            selected.TryGetComponent<Selectable>(out _)) {
            _lastSelected = selected.transform.position;
            ScrollIntoView(selected);
            return;
        }

        root.GetComponentsInChildren(false, Found);
        Selectable? best = null;
        var bestScore = float.MaxValue;
        foreach (var selectable in Found) {
            if (!selectable.IsInteractable() || selectable.navigation.mode == Navigation.Mode.None) {
                continue;
            }

            // Lower is better: nearness to the last one, or else height and then how far left, with a text box always
            // after every button
            var position = selectable.transform.position;
            var score = _lastSelected is { } last
                ? (position - last).sqrMagnitude
                : -position.y * 10000f + position.x;
            if (selectable is InputField) {
                score += 1e9f;
            }

            if (score < bestScore) {
                best = selectable;
                bestScore = score;
            }
        }

        Found.Clear();
        if (best != null) {
            eventSystem.SetSelectedGameObject(best.gameObject);
            _lastSelected = best.transform.position;
            ScrollIntoView(best.gameObject);
        }
    }

    /// <summary>
    /// Scrolls the list that a newly selected control is in, if it is in one, so that the control shows: the list of
    /// lobbies shows fewer than it has, and a gamepad moved on to the ones it hid.
    /// </summary>
    private static void ScrollIntoView(GameObject selected) {
        if (selected == _scrolledTo) {
            return;
        }

        _scrolledTo = selected;
        var scroll = selected.GetComponentInParent<ScrollRect>();
        if (scroll == null || scroll.content is not { } content || !selected.transform.IsChildOf(content) ||
            selected.transform is not RectTransform shown) {
            return;
        }

        var viewport = scroll.viewport != null ? scroll.viewport : (RectTransform) scroll.transform;
        shown.GetWorldCorners(Corners);
        var bottom = viewport.InverseTransformPoint(Corners[0]).y;
        var top = viewport.InverseTransformPoint(Corners[1]).y;
        var view = viewport.rect;
        var shift = top > view.yMax ? top - view.yMax : bottom < view.yMin ? bottom - view.yMin : 0f;
        if (shift == 0f) {
            return;
        }

        scroll.StopMovement();
        var position = content.anchoredPosition;
        position.y -= shift;
        content.anchoredPosition = position;
    }

    /// <summary>
    /// The hook that keeps the keys of a keyboard from pressing the mod's controls, made once.
    /// </summary>
    private static Hook? _keyPressHook;

    /// <summary>
    /// Keeps the keys of a keyboard from pressing the mod's controls (USER 10-22 "我一打字就已停止等待，断开连接"). The
    /// game's menus take the jump key and the confirm key of a keyboard as a press of what is selected, and the
    /// attack and spell keys as going back, and a menu of the mod always has something selected for a gamepad to start
    /// from (see <see cref="KeepSelection"/>). So typing where the chat did not take the keys could press whatever was
    /// selected, and the host's waiting room was left as they typed, which closed their game for both players. The
    /// mod's controls are clicked with the mouse, or pressed with a gamepad, which still works; the game's own menus
    /// are left as they are.
    /// </summary>
    public static void KeepKeysFromPressing() {
        if (_keyPressHook != null) {
            return;
        }

        var method = typeof(HollowKnightInputModule).GetMethod(
            "SendButtonEventToSelectedObject",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );
        if (method == null) {
            Logger.Warn("Could not find where the game's menus press what is selected, so keys may press the mod's");
            return;
        }

        _keyPressHook = new Hook(
            method,
            (Action<Action<HollowKnightInputModule>, HollowKnightInputModule>) OnPressSelected
        );
    }

    /// <summary>
    /// The game's menu input pressing, or going back from, what is selected: not a control of the mod's from a key.
    /// </summary>
    private static void OnPressSelected(Action<HollowKnightInputModule> orig, HollowKnightInputModule self) {
        var selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
        var root = UiManager.UiGameObject;
        if (selected != null && root != null && selected.transform.IsChildOf(root.transform) && IsFromKeys(self)) {
            return;
        }

        orig(self);
    }

    /// <summary>
    /// Whether the press that the game's menu input acts on this frame came from the keyboard or the mouse rather than
    /// a gamepad: the input of the action pressed, which it has taken in by then. Not the input the hero actions took
    /// last, which follows whichever of them comes first in their list among those held, so a stick held off its
    /// middle let a typed key through, and a key held kept a gamepad's press out (review of d82c3f0).
    /// </summary>
    private static bool IsFromKeys(HollowKnightInputModule module) {
        var pressed = Pressed(module.SubmitAction) ?? Pressed(module.CancelAction) ?? Pressed(module.JumpAction) ??
                      Pressed(module.AttackAction) ?? Pressed(module.CastAction);
        return pressed != null && pressed.LastInputType is BindingSourceType.KeyBindingSource
            or BindingSourceType.MouseBindingSource;
    }

    /// <summary>
    /// The action if it was pressed this frame.
    /// </summary>
    private static PlayerAction? Pressed(PlayerAction? action) => action != null && action.WasPressed ? action : null;

    /// <summary>
    /// Forgets the control that was selected last, for a menu that is shown anew, which starts from its top.
    /// </summary>
    public static void StartOver() {
        _lastSelected = null;
    }

    /// <summary>
    /// Stops typing into the text box that is selected, if one is being typed into, as the back button of a gamepad
    /// does before anything else.
    /// </summary>
    /// <returns>Whether typing was stopped.</returns>
    public static bool StopTyping() {
        var selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
        if (selected == null || !selected.TryGetComponent<InputField>(out var input) || !input.isFocused) {
            return false;
        }

        input.DeactivateInputField();
        return true;
    }
}

/// <summary>
/// A frame around the control of the mod's interface that is selected, so that a player with a gamepad sees where they
/// are. It only shows in the mod's menus and while a gamepad is plugged in, since a player on the mouse sees what they
/// point at, and not around a text box that is being typed into, which shows where it is with its caret: the chat's
/// box always is while the chat is open (USER 10-22 "聊天框的橙色高光是啥，不是很有必要哎").
/// </summary>
internal class PadFocusFrame : MonoBehaviour {
    /// <summary>
    /// How thick the frame is, in units of the interface.
    /// </summary>
    private const float Thickness = 3f;

    /// <summary>
    /// How far outside the control the frame is drawn, in units of the interface.
    /// </summary>
    private const float Margin = 4f;

    /// <summary>
    /// The colour of the frame: the warm orange of the mod's highlights.
    /// </summary>
    private static readonly Color FrameColor = new(1f, 0.7f, 0.3f, 0.9f);

    /// <summary>
    /// The root of the mod's interface, whose controls the frame goes around.
    /// </summary>
    private Transform _root = null!;

    /// <summary>
    /// The frame's own transform, and the four sides of it.
    /// </summary>
    private RectTransform _frame = null!;

    private readonly RectTransform[] _sides = new RectTransform[4];

    /// <summary>
    /// The corners of the selected control, kept to be filled again.
    /// </summary>
    private readonly Vector3[] _corners = new Vector3[4];

    /// <summary>
    /// Makes the frame under the root of the mod's interface.
    /// </summary>
    /// <param name="root">The root, which is a canvas.</param>
    public static PadFocusFrame Create(GameObject root) {
        var frameObject = new GameObject("SSMP Pad Focus Frame");
        var frame = frameObject.AddComponent<RectTransform>();
        frame.SetParent(root.transform, false);
        frame.anchorMin = frame.anchorMax = Vector2.zero;
        frame.pivot = Vector2.zero;

        var focusFrame = frameObject.AddComponent<PadFocusFrame>();
        focusFrame._root = root.transform;
        focusFrame._frame = frame;
        for (var index = 0; index < 4; index++) {
            var side = new GameObject("Side");
            side.AddComponent<CanvasRenderer>();
            var image = side.AddComponent<Image>();
            image.color = FrameColor;
            image.raycastTarget = false;
            focusFrame._sides[index] = side.GetComponent<RectTransform>();
            focusFrame._sides[index].SetParent(frame, false);
        }

        return focusFrame;
    }

    private void LateUpdate() {
        var selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
        var show = PadNavigation.IsMenuShown && selected != null && selected.activeInHierarchy &&
                   selected.transform.IsChildOf(_root) && selected.transform is RectTransform &&
                   !IsTypedInto(selected) && InputHandler.Instance != null &&
                   InputHandler.Instance.activeGamepadType != GlobalEnums.GamepadType.NONE;
        for (var index = 0; index < _sides.Length; index++) {
            if (_sides[index].gameObject.activeSelf != show) {
                _sides[index].gameObject.SetActive(show);
            }
        }

        if (!show) {
            return;
        }

        // Drawn last, so that it is on top of everything else of the interface
        if (_frame.GetSiblingIndex() != _root.childCount - 1) {
            _frame.SetAsLastSibling();
        }

        ((RectTransform) selected!.transform).GetWorldCorners(_corners);
        var min = _root.InverseTransformPoint(_corners[0]);
        var max = _root.InverseTransformPoint(_corners[2]);
        var rootRect = (RectTransform) _root;
        var origin = rootRect.rect.min;
        var x = min.x - origin.x - Margin;
        var y = min.y - origin.y - Margin;
        var width = max.x - min.x + Margin * 2f;
        var height = max.y - min.y + Margin * 2f;

        Place(_sides[0], x, y, width, Thickness);
        Place(_sides[1], x, y + height - Thickness, width, Thickness);
        Place(_sides[2], x, y, Thickness, height);
        Place(_sides[3], x + width - Thickness, y, Thickness, height);
    }

    /// <summary>
    /// Whether a control is a text box that is being typed into: the chat's is TextMeshPro's, the others are Unity's.
    /// </summary>
    private static bool IsTypedInto(GameObject selected) =>
        selected.TryGetComponent<TMP_InputField>(out var chatField) && chatField.isFocused ||
        selected.TryGetComponent<InputField>(out var field) && field.isFocused;

    /// <summary>
    /// Puts a side of the frame at a place, measured from the bottom left of the interface.
    /// </summary>
    private static void Place(RectTransform side, float x, float y, float width, float height) {
        side.anchorMin = side.anchorMax = Vector2.zero;
        side.pivot = Vector2.zero;
        side.anchoredPosition = new Vector2(x, y);
        side.sizeDelta = new Vector2(width, height);
    }
}
