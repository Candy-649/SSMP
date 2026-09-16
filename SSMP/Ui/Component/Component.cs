using System;
using UnityEngine;
using UnityEngine.EventSystems;
using Object = UnityEngine.Object;

namespace SSMP.Ui.Component;

/// <inheritdoc />
internal abstract class Component : IComponent {
    /// <summary>
    /// The opacity of non-interactable components.
    /// </summary>
    protected const float NotInteractableOpacity = 0.5f;

    /// <summary>
    /// The underlying GameObject of the component. 
    /// </summary>
    internal GameObject GameObject { get; }

    /// <summary>
    /// The Unity RectTransform instance.
    /// </summary>
    private readonly RectTransform _transform;

    /// <summary>
    /// Whether this component is active.
    /// </summary>
    private bool _activeSelf;

    /// <summary>
    /// The component group this component belongs to.
    /// </summary>
    private readonly ComponentGroup _componentGroup;

    /// <summary>
    /// Whether this component stays a fixed distance from the left edge of the screen, instead of sitting at a fixed
    /// fraction of the screen's width.
    ///
    /// The canvas scales by height alone, so it is always 1080 units tall but only as many units wide as the screen's
    /// shape allows - 1620 on a 3:2 screen, not 1920. Placing something by the fraction x/1920 therefore drifts left
    /// as the screen gets narrower, while its width in units does not, and anything near the left edge ends up partly
    /// off the screen. Anything that belongs against an edge has to be anchored to that edge instead.
    /// </summary>
    private bool _anchorToLeftEdge;

    /// <summary>
    /// The last position this component was given, in reference units, so that the anchoring can be changed after it
    /// has been placed.
    /// </summary>
    private Vector2 _position;

    protected Component(ComponentGroup componentGroup, Vector2 position, Vector2 size) {
        // Create a gameobject with the CanvasRenderer component, so we can render as GUI
        GameObject = new GameObject();
        GameObject.AddComponent<CanvasRenderer>();
        // Make sure game object persists
        Object.DontDestroyOnLoad(GameObject);

        // Create a RectTransform with the desired size
        _transform = GameObject.AddComponent<RectTransform>();

        // Kept before it is converted, because anchoring can be changed afterwards - including from the constructor
        // of a subclass, which would otherwise re-place the component at the origin
        _position = position;

        position = new Vector2(
            position.x / 1920f,
            position.y / 1080f
        );
        _transform.anchorMin = _transform.anchorMax = position;

        _transform.sizeDelta = size;

        GameObject.transform.SetParent(UiManager.UiGameObject!.transform, false);

        _activeSelf = true;

        _componentGroup = componentGroup;
        componentGroup.AddComponent(this);
    }

    /// <inheritdoc />
    public virtual void SetGroupActive(bool groupActive) {
        // TODO: figure out why this could be happening
        if (GameObject == null) {
            // Logger.Info(
            //     $"The GameObject belonging to this component (type: {GetType()}) is null, this shouldn't happen");
            return;
        }

        GameObject.SetActive(_activeSelf && groupActive);
    }

    /// <inheritdoc />
    public virtual void SetActive(bool active) {
        _activeSelf = active;

        GameObject.SetActive(_activeSelf && _componentGroup.IsActive());
    }

    /// <summary>
    /// Keeps this component a fixed distance from the left edge of the screen, whatever shape the screen is. Its
    /// given x is read as units from that edge to the component's middle, so a component sits exactly as far in as
    /// the layout asks on every screen.
    /// </summary>
    protected void AnchorToLeftEdge() {
        _anchorToLeftEdge = true;
        SetPosition(_position);
    }

    /// <inheritdoc />
    public Vector2 GetPosition() {
        if (_anchorToLeftEdge) {
            return new Vector2(_transform.anchoredPosition.x, _transform.anchorMin.y * 1080f);
        }

        var position = _transform.anchorMin;
        return new Vector2(
            position.x * 1920f,
            position.y * 1080f
        );
    }

    /// <inheritdoc />
    public void SetPosition(Vector2 position) {
        _position = position;

        if (_anchorToLeftEdge) {
            // The height still divides by 1080 because the canvas is always exactly that many units tall; only the
            // width of it changes with the shape of the screen
            _transform.anchorMin = _transform.anchorMax = new Vector2(0f, position.y / 1080f);
            _transform.anchoredPosition = new Vector2(position.x, 0f);
            return;
        }

        _transform.anchorMin = _transform.anchorMax = new Vector2(
            position.x / 1920f,
            position.y / 1080f
        );
    }

    /// <inheritdoc />
    public Vector2 GetSize() {
        return _transform.sizeDelta;
    }

    /// <summary>
    /// Destroys the component.
    /// </summary>
    public void Destroy() {
        Object.Destroy(GameObject);
    }

    /// <summary>
    /// Add an event trigger to this component object.
    /// </summary>
    /// <param name="eventTrigger">The event trigger.</param>
    /// <param name="type">The type of the event trigger.</param>
    /// <param name="action">The action that is executed on the event.</param>
    protected void AddEventTrigger(
        EventTrigger eventTrigger,
        EventTriggerType type,
        Action<BaseEventData> action
    ) {
        var eventTriggerEntry = new EventTrigger.Entry {
            eventID = type
        };
        eventTriggerEntry.callback.AddListener(action.Invoke);

        eventTrigger.triggers.Add(eventTriggerEntry);
    }
}
