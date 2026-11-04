using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Says in the log where a head on the map is drawn, and with which picture, when the map shows it and each time either
/// changes while it does. A head was seen moving up and down now and then, also while both players sat on a bench, ever
/// since they first played together, and nothing was found in the game or in SSMP that would move it (USER 11-04
/// "有时候看地图那个头像会上下浮动", "可是有时候我们坐在椅子上也会上下浮动", "实在不行就打一下log吧").
/// </summary>
internal sealed class MapHeadLog : MonoBehaviour {
    /// <summary>
    /// Whose head it is, as the log says it.
    /// </summary>
    private string _whose = "";

    /// <summary>
    /// Whether it is this player's own head, which is said with where the hero is, the place it is drawn from.
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
    /// Whether the head was drawn when it was last looked at.
    /// </summary>
    private bool _shown;

    /// <summary>
    /// Where the head was last said to be drawn, in the space of the map.
    /// </summary>
    private Vector2 _saidAt;

    /// <summary>
    /// The picture the head was last said to be drawn with.
    /// </summary>
    private int _saidPicture;

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
        log._shown = false;
    }

    /// <summary>
    /// Says where the head is drawn if the map shows it and that changed. Only while the map shows it: a head can be
    /// on while no map is shown, as after a death, which puts the cocoon on the map.
    /// </summary>
    private void LateUpdate() {
        if (!_renderer.isVisible) {
            _shown = false;
            return;
        }

        Vector2 at = transform.localPosition;
        var picture = _sprite.spriteId;
        if (_shown && picture == _saidPicture && at == _saidAt) {
            return;
        }

        var how = _shown ? "moved to" : "shown at";
        _shown = true;
        _saidAt = at;
        _saidPicture = picture;

        var drawn = $"[MapHeads] {_whose} {how} ({at.x:0.000}, {at.y:0.000}) as {_sprite.CurrentSprite.name}";
        if (!_own) {
            Logger.Info(drawn);
            return;
        }

        Vector2 hero = HeroController.instance.transform.position;
        Logger.Info($"{drawn}, hero at ({hero.x:0.00}, {hero.y:0.00})");
    }
}
