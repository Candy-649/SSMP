using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Says in the log, once each time the map stops showing a head, what the head did while it was shown: where it was
/// drawn and with which picture, how many times it moved and turned from going up to going down or back, how low and
/// high it went, and how many times its picture changed. A head was seen moving up and down now and then, also while
/// both players sat on a bench, ever since they first played together, and nothing was found in the game or in SSMP
/// that would move it (USER 11-04 "有时候看地图那个头像会上下浮动", "可是有时候我们坐在椅子上也会上下浮动", "实在不行就打一下
/// log吧"). One line for each time the map shows it, not one for each move (USER 11-04 "地图开着的时候还是会刷屏").
/// </summary>
internal sealed class MapHeadLog : MonoBehaviour {
    /// <summary>
    /// Whose head it is, as the log says it.
    /// </summary>
    private string _whose = "";

    /// <summary>
    /// Whether it is this player's own head, which is drawn where the hero is.
    /// </summary>
    private bool _own;

    /// <summary>
    /// The head's picture.
    /// </summary>
    private tk2dSprite _sprite = null!;

    /// <summary>
    /// What draws the head.
    /// </summary>
    private Renderer _renderer = null!;

    /// <summary>
    /// Whether the map showed the head last frame.
    /// </summary>
    private bool _shown;

    /// <summary>
    /// When the map started showing the head, in unscaled seconds.
    /// </summary>
    private float _shownSince;

    /// <summary>
    /// Where the head was drawn when the map started showing it, in the space of the map.
    /// </summary>
    private Vector2 _from;

    /// <summary>
    /// The name of the picture the head was drawn with when the map started showing it.
    /// </summary>
    private string _fromPicture = "";

    /// <summary>
    /// Where the head was drawn last frame.
    /// </summary>
    private Vector2 _at;

    /// <summary>
    /// The picture the head was drawn with last frame.
    /// </summary>
    private int _picture;

    /// <summary>
    /// Where the hero was last frame, for this player's own head.
    /// </summary>
    private Vector2 _hero;

    /// <summary>
    /// How many times the head moved.
    /// </summary>
    private int _moves;

    /// <summary>
    /// How many of those moves were made while the hero stood still, for this player's own head.
    /// </summary>
    private int _movesHeroStill;

    /// <summary>
    /// How many times the head turned from going up to going down or back.
    /// </summary>
    private int _turns;

    /// <summary>
    /// Which way the head last went: 1 up, -1 down, 0 neither yet.
    /// </summary>
    private int _goingUp;

    /// <summary>
    /// How low the head was drawn.
    /// </summary>
    private float _lowest;

    /// <summary>
    /// How high the head was drawn.
    /// </summary>
    private float _highest;

    /// <summary>
    /// How many times the head's picture changed.
    /// </summary>
    private int _pictureChanges;

    /// <summary>
    /// The name of the first other picture the head was drawn with.
    /// </summary>
    private string _otherPicture = "";

    /// <summary>
    /// How many places came from the network for a partner's head.
    /// </summary>
    private int _placesCame;

    /// <summary>
    /// Gives a head on the map its log.
    /// </summary>
    /// <param name="head">The head.</param>
    /// <param name="whose">Whose head it is, as the log says it.</param>
    /// <param name="own">Whether it is this player's own head.</param>
    public static void Attach(GameObject head, string whose, bool own) {
        // A partner's head is a copy of this player's own head, and comes with its log
        if (!head.TryGetComponent<MapHeadLog>(out var log)) {
            log = head.AddComponent<MapHeadLog>();
        }

        log._whose = whose;
        log._own = own;
        log._sprite = head.GetComponent<tk2dSprite>();
        log._renderer = head.GetComponent<Renderer>();
    }

    /// <summary>
    /// Counts a place that came from the network for a partner's head while the map shows it.
    /// </summary>
    public void PlaceCame() {
        if (_shown) {
            _placesCame++;
        }
    }

    /// <summary>
    /// Takes in where the head is drawn and with which picture while the map shows it. Only while the map shows it: a
    /// head can be on while no map is shown, as after a death, which puts the cocoon on the map.
    /// </summary>
    private void LateUpdate() {
        if (!_renderer.isVisible) {
            Say();
            return;
        }

        Vector2 at = transform.localPosition;
        var picture = _sprite.spriteId;
        Vector2 hero = _own ? HeroController.instance.transform.position : Vector2.zero;

        if (!_shown) {
            _shown = true;
            _shownSince = Time.unscaledTime;
            _from = at;
            _fromPicture = _sprite.CurrentSprite.name;
            _at = at;
            _picture = picture;
            _hero = hero;
            _moves = 0;
            _movesHeroStill = 0;
            _turns = 0;
            _goingUp = 0;
            _lowest = at.y;
            _highest = at.y;
            _pictureChanges = 0;
            _otherPicture = "";
            _placesCame = 0;
            return;
        }

        if (at != _at) {
            _moves++;
            if (_own && hero == _hero) {
                _movesHeroStill++;
            }

            if (at.y != _at.y) {
                var goingUp = at.y > _at.y ? 1 : -1;
                if (_goingUp != 0 && goingUp != _goingUp) {
                    _turns++;
                }

                _goingUp = goingUp;
            }

            _lowest = Mathf.Min(_lowest, at.y);
            _highest = Mathf.Max(_highest, at.y);
            _at = at;
        }

        if (picture != _picture) {
            _pictureChanges++;
            if (_otherPicture.Length == 0) {
                _otherPicture = _sprite.CurrentSprite.name;
            }

            _picture = picture;
        }

        _hero = hero;
    }

    /// <summary>
    /// Says what the head did while the map showed it, when the head is switched off with the map.
    /// </summary>
    private void OnDisable() {
        Say();
    }

    /// <summary>
    /// Says what the head did while the map showed it, if it did show it.
    /// </summary>
    private void Say() {
        if (!_shown) {
            return;
        }

        _shown = false;

        var moved = _own ? $"{_moves} times ({_movesHeroStill} with the hero still)" : $"{_moves} times";
        var pictures = _pictureChanges == 0 ? "0 times" : $"{_pictureChanges} times, first to {_otherPicture}";
        var came = _own ? "" : $"; {_placesCame} places came";
        Logger.Info(
            $"[MapHeads] {_whose} shown {Time.unscaledTime - _shownSince:0.0} s from ({_from.x:0.000}, {_from.y:0.000}) " +
            $"as {_fromPicture}: moved {moved}, turned up or down {_turns} times, y {_lowest:0.000} to " +
            $"{_highest:0.000}, ended at ({_at.x:0.000}, {_at.y:0.000}); picture changed {pictures}{came}"
        );
    }
}
