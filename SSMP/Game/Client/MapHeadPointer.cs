using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Points at a partner's head on the map while it is outside the part of the map that is shown, the way the game points
/// at the cocoon and the quest markers there (MapMarkerArrow): the head waits at the edge of what is shown, at the
/// place nearest to where it is, with the game's own arrow pointing the rest of the way. The quick map gets it too,
/// where the game points at nothing: it shows only the area the local player is in, and a partner in another area was
/// nowhere on it (USER 10-28: "我们都带罗盘看不到对方的头像了，在地图上"; then "茧和任务图标不在视野里的时候会有一个指向它们的
/// 图标，用那个").
/// </summary>
internal sealed class MapHeadPointer : MonoBehaviour {
    /// <summary>
    /// The map the head is on.
    /// </summary>
    private GameMap? _map;

    /// <summary>
    /// Where the head is on the map, in the space of the map, whichever part of it is shown.
    /// </summary>
    private Vector2 _place;

    /// <summary>
    /// The game's arrow under the head, or null if the game had none to copy.
    /// </summary>
    private GameObject? _arrow;

    /// <summary>
    /// Whether the head was outside what is shown when it was last placed.
    /// </summary>
    private bool _wasOutside;

    /// <summary>
    /// Whether it was said that the game had no arrow to copy, which is said once.
    /// </summary>
    private static bool _saidNoArrow;

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

        // As large as the cocoon's arrow, which sits under a marker of its own size, while the head is drawn smaller
        var headScale = head.transform.localScale;
        if (headScale.x != 0f && headScale.y != 0f) {
            arrow.transform.localScale = new Vector3(1f / headScale.x, 1f / headScale.y, 1f);
        }

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

        var edge = _map.ViewportEdge;
        var outside = false;
        var shownAt = _place;
        if (edge != null && edge.isActiveAndEnabled) {
            var world = parent.TransformPoint(_place);
            outside = !edge.OverlapPoint(world);
            if (outside) {
                shownAt = parent.InverseTransformPoint(edge.ClosestPoint(world));
            }
        }

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
}
