using System;
using InControl;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SSMP.Ui.Component;

/// <summary>
/// A strip that a value is chosen on, as the colour of the cloak is on the settings page. The mouse presses or drags
/// along it, as on any of Unity's sliders. The stick of a gamepad, or the keys that move, held left or right while it is
/// selected slide its marker along smoothly, faster the further the stick is pushed. Unity's own slider moved a step at
/// each move of the game's menus, which come at the pace at which they repeat a direction held, and showed as jolts
/// (USER 10-24: "斗篷换色颜色条手柄滚动一卡一卡"). Up and down move to what is over and under it.
/// </summary>
internal sealed class ColourStrip : Slider {
    /// <summary>
    /// How long the marker takes from one end of the strip to the other with the stick pushed all the way, in seconds.
    /// </summary>
    private const float SweepSeconds = 2f;

    /// <summary>
    /// How far the stick has to be pushed before the marker moves, as a share of the way.
    /// </summary>
    private const float DeadZone = 0.2f;

    /// <summary>
    /// How long after its last move a value counts as settled, in seconds.
    /// </summary>
    private const float SettleAfter = 0.4f;

    /// <summary>
    /// Called once a value chosen on the strip has settled: the mouse let go of it, the stick was left alone for a
    /// moment, or the strip was left or hidden.
    /// </summary>
    public Action? Settled;

    /// <summary>
    /// Whether the value moved since it last settled, and when it last moved.
    /// </summary>
    private bool _unsettled;
    private float _movedAt;

    /// <summary>
    /// Whether the mouse holds the strip.
    /// </summary>
    private bool _held;

    /// <inheritdoc />
    protected override void Awake() {
        base.Awake();
        onValueChanged.AddListener(_ => {
                _unsettled = true;
                _movedAt = Time.unscaledTime;
            }
        );
    }

    /// <inheritdoc />
    public override void OnMove(AxisEventData eventData) {
        // Left and right are read from the stick for as long as it is held (see Update)
        if (eventData.moveDir is MoveDirection.Left or MoveDirection.Right) {
            eventData.Use();
            return;
        }

        base.OnMove(eventData);
    }

    /// <inheritdoc />
    public override void OnPointerDown(PointerEventData eventData) {
        _held = eventData.button == PointerEventData.InputButton.Left;
        base.OnPointerDown(eventData);
    }

    /// <inheritdoc />
    public override void OnPointerUp(PointerEventData eventData) {
        base.OnPointerUp(eventData);
        _held = false;
        Settle();
    }

    /// <inheritdoc />
    public override void OnDeselect(BaseEventData eventData) {
        base.OnDeselect(eventData);
        Settle();
    }

    /// <inheritdoc />
    protected override void OnDisable() {
        _held = false;
        Settle();
        base.OnDisable();
    }

    /// <inheritdoc />
    protected override void Update() {
        base.Update();

        var eventSystem = EventSystem.current;
        if (IsActive() && IsInteractable() && eventSystem != null &&
            eventSystem.currentSelectedGameObject == gameObject) {
            var tilt = Tilt(eventSystem);
            if (tilt != 0f) {
                var range = maxValue - minValue;
                value = Mathf.Clamp(
                    value + tilt * range * Time.unscaledDeltaTime / SweepSeconds,
                    minValue,
                    maxValue
                );
            }
        }

        if (_unsettled && !_held && Time.unscaledTime - _movedAt >= SettleAfter) {
            Settle();
        }
    }

    /// <summary>
    /// Says that the value settled, if it moved since it last did.
    /// </summary>
    private void Settle() {
        if (!_unsettled) {
            return;
        }

        _unsettled = false;
        Settled?.Invoke();
    }

    /// <summary>
    /// How far left or right the stick of the menus is pushed, from -1 to 1, with what is inside the dead zone as none:
    /// what the game's menus move with, or else what the hero moves with.
    /// </summary>
    private static float Tilt(EventSystem eventSystem) {
        TwoAxisInputControl? move = eventSystem.currentInputModule is HollowKnightInputModule module
            ? module.MoveAction
            : null;
        if (move == null && InputHandler.Instance != null) {
            move = InputHandler.Instance.inputActions?.MoveVector;
        }

        if (move == null) {
            return 0f;
        }

        var x = move.X;
        var past = Mathf.Abs(x) - DeadZone;
        return past <= 0f ? 0f : Mathf.Sign(x) * Mathf.Min(1f, past / (1f - DeadZone));
    }
}
