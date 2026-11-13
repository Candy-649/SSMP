using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Points at a partner's head on the map while it is outside the part of the map that is shown, the way the game points
/// at the cocoon and the quest markers there (MapMarkerArrow): the head waits at the edge of what is shown, at the
/// place nearest to where it is, with the game's own arrow pointing the rest of the way. The quick map gets it too,
/// where the game points at nothing: it shows only the area the local player is in, and a partner in another area was
/// nowhere on it (USER 10-28: "我们都带罗盘看不到对方的头像了，在地图上"; then "茧和任务图标不在视野里的时候会有一个指向它们的
/// 图标，用那个"). What the quick map shows is the whole screen: the edge the game keeps its markers within is the frame
/// of the inventory, a good way in from the edges of the screen, and a head right beside the local player's own, near
/// the edge of the screen, got the ring (USER 11-13 "明明她就在我旁边，为什么会有不在画面中的情况下才有的大圆圈").
/// </summary>
internal sealed class MapHeadPointer : MonoBehaviour {
    /// <summary>
    /// Whether the map is shown as the quick map rather than in the inventory, as the game said the last time it
    /// showed one (GameMap.UpdateQuickMapDisplay, which its own markers go by).
    /// </summary>
    private static bool _quickMap;

    /// <summary>
    /// The map that is listened to for which way it is shown.
    /// </summary>
    private static GameMap? _listenedTo;

    /// <summary>
    /// The map the head is on.
    /// </summary>
    private GameMap? _map;

    /// <summary>
    /// Where the head is on the map, in the space of the map, whichever part of it is shown.
    /// </summary>
    private Vector2 _place;

    /// <summary>
    /// How far the middle of the drawn head is from its place, in the space of the map: the head is drawn above its
    /// place, the way a pin's head is above its point.
    /// </summary>
    private Vector2 _middle;

    /// <summary>
    /// The game's arrow under the head, or null if the game had none to copy.
    /// </summary>
    private GameObject? _arrow;

    /// <summary>
    /// How far the game's arrow reaches out from the middle of the head, in the space of the map, whichever way it
    /// points: how far in from the edge of the screen the middle waits, for the ring to show whole.
    /// </summary>
    private float _arrowReach;

    /// <summary>
    /// Whether the head was outside what is shown when it was last placed.
    /// </summary>
    private bool _wasOutside;

    /// <summary>
    /// Whether it was said that the game had no arrow to copy, which is said once.
    /// </summary>
    private static bool _saidNoArrow;

    /// <summary>
    /// Listens to the map saying which way it is shown, before it first says so: the quick map and the inventory show
    /// it as they open, right after the heads are put on it.
    /// </summary>
    /// <param name="map">The map.</param>
    public static void Listen(GameMap map) {
        if (_listenedTo == map) {
            return;
        }

        map.UpdateQuickMapDisplay += (quickMap, _) => _quickMap = quickMap;
        _listenedTo = map;
    }

    /// <summary>
    /// Gives a partner's head on the map its pointer.
    /// </summary>
    /// <param name="head">The head, a child of the map.</param>
    /// <param name="map">The map.</param>
    /// <param name="place">Where the head is on the map.</param>
    public static void Attach(GameObject head, GameMap map, Vector2 place) {
        var pointer = head.AddComponent<MapHeadPointer>();
        pointer._map = map;
        pointer._place = place;

        // The game's ring goes around the place of the marker it was made for, which is drawn around its place; the
        // head is drawn above its place, so the ring goes around the middle of the drawn head instead, and that middle
        // is what waits at the edge (USER 11-04 "现在的队友头像并不完全在圈的正中央")
        Vector2 middle = head.GetComponent<tk2dSprite>().GetBounds().center;
        pointer._middle = Vector2.Scale(middle, head.transform.localScale);

        // The cocoon's marker keeps the game's arrow; the head gets a copy of it the way the marker made its own
        MapMarkerArrow? marker = map.shadeMarker;
        if (marker == null) {
            marker = map.GetComponentInChildren<MapMarkerArrow>(true);
        }

        var prefab = marker != null ? marker.arrowPrefab : null;
        if (prefab == null) {
            if (!_saidNoArrow) {
                _saidNoArrow = true;
                Logger.Warn(
                    "The map has no arrow to point at heads outside what is shown, so they wait at its edge without one"
                );
            }

            return;
        }

        var arrow = Instantiate(prefab, head.transform);
        global::Extensions.Reset(arrow.transform);
        arrow.transform.localPosition = middle;

        // As large as the cocoon's arrow, which sits under a marker of its own size, while the head is drawn smaller
        var headScale = head.transform.localScale;
        if (headScale.x != 0f && headScale.y != 0f) {
            arrow.transform.localScale = new Vector3(1f / headScale.x, 1f / headScale.y, 1f);
        }

        // The ring and its arrow are one picture, with the arrow pointing out of the ring
        var drawn = arrow.GetComponentInChildren<SpriteRenderer>(true);
        var size = Vector2.Scale(drawn.sprite.bounds.size, drawn.transform.localScale);
        pointer._arrowReach = Mathf.Max(Mathf.Abs(size.x), Mathf.Abs(size.y)) / 2f;

        arrow.SetActive(false);
        pointer._arrow = arrow;
    }

    /// <summary>
    /// Takes where the head is on the map now.
    /// </summary>
    /// <param name="place">Where the head is on the map.</param>
    public void SetPlace(Vector2 place) {
        _place = place;
    }

    /// <summary>
    /// Puts the head where it is, or at the edge of what is shown with the arrow pointing to it, every frame the map is
    /// shown: the quick map does not tell when it moves, and the partner can move while it is open.
    /// </summary>
    private void LateUpdate() {
        var parent = transform.parent;
        if (_map == null || parent == null) {
            return;
        }

        var middle = parent.TransformPoint(_place + _middle);
        var outside = _quickMap
            ? IsOffScreen(middle, _arrowReach * Mathf.Abs(parent.lossyScale.x), out var nearest)
            : IsOutsideFrame(_map.ViewportEdge, middle, out nearest);
        var shownAt = outside ? (Vector2) parent.InverseTransformPoint(nearest) - _middle : _place;

        global::Extensions.SetLocalPosition2D(transform, shownAt);

        if (_arrow == null) {
            _wasOutside = outside;
            return;
        }

        if (outside) {
            if (!_wasOutside) {
                _arrow.SetActive(true);
            }

            global::Extensions.SetLocalRotation2D(
                _arrow.transform,
                global::Extensions.DirectionToAngle(_place - shownAt)
            );
        } else if (_wasOutside) {
            _arrow.SetActive(false);
        }

        _wasOutside = outside;
    }

    /// <summary>
    /// Whether the middle of the head is outside the frame of the inventory, where the game keeps its own markers on
    /// the map (MapMarkerArrow), and the nearest place on that frame.
    /// </summary>
    /// <param name="edge">The frame.</param>
    /// <param name="middle">The middle of the head, in the world.</param>
    /// <param name="nearest">The nearest place on the frame, in the world.</param>
    private static bool IsOutsideFrame(Collider2D? edge, Vector3 middle, out Vector3 nearest) {
        nearest = middle;
        if (edge == null || !edge.isActiveAndEnabled || edge.OverlapPoint(middle)) {
            return false;
        }

        nearest = edge.ClosestPoint(middle);
        return true;
    }

    /// <summary>
    /// Whether the middle of the head is off the screen, which the quick map fills, and the nearest place where it is
    /// on the screen with the ring around it whole.
    /// </summary>
    /// <param name="middle">The middle of the head, in the world.</param>
    /// <param name="reach">How far the ring and its arrow reach out from the middle, in the world.</param>
    /// <param name="nearest">The nearest such place, in the world.</param>
    private static bool IsOffScreen(Vector3 middle, float reach, out Vector3 nearest) {
        nearest = middle;
        var camera = GameCameras.instance.hudCamera;
        var view = camera.WorldToViewportPoint(middle);
        if (view.x is >= 0f and <= 1f && view.y is >= 0f and <= 1f) {
            return false;
        }

        var low = camera.ViewportToWorldPoint(new Vector3(0f, 0f, view.z));
        var high = camera.ViewportToWorldPoint(new Vector3(1f, 1f, view.z));
        nearest = new Vector3(
            Mathf.Clamp(middle.x, low.x + reach, high.x - reach),
            Mathf.Clamp(middle.y, low.y + reach, high.y - reach),
            middle.z
        );
        return true;
    }
}
