using System.Collections.Generic;
using SSMP.Game.Settings;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Lights an outline around this player's own hero in a fight with another player on the screen, so that in the thick
/// of it they find their own Hornet at a glance. Every player's hero is the same Hornet, and in a fight the two are
/// close together among flashes and creatures. Outside of a fight the hero is drawn as the game draws it.
///
/// The rule is the user's (2026-10-20): "只有两个人在同一个屏幕里的时候，如果有人打怪了，那么亮". It is lit while another
/// player's figure is on this player's screen and a fight is on: a player hit a creature in the room, this player or
/// another as this game hears of it. Each game goes by its own screen. Neither alone lights it: another player
/// fighting far off in a large room is no reason for this player's hero to light up, and neither is the other player
/// standing by while nobody fights. Only this player's own hero is outlined, in the colour of their own name, and only
/// on this machine (the rule of the name: the one that stands out is you). A player can turn it off on the settings
/// page (ModSettings.OutlineOwnHeroInFights).
///
/// The fight is over as soon as the creatures that were hit are all dead or gone, and also once none of those still
/// there was hit for <see cref="QuietTime"/> seconds. At first it went on for 8 seconds after any last hit, which the
/// user found too long ("感觉8秒后有点久了") and chose this instead: the outline goes out as a fight is won, and stays
/// while a creature that was fought keeps out of reach for a moment.
/// </summary>
internal class FightOutline {
    /// <summary>
    /// How long after the last hit on a creature that is still alive the fight goes on, in seconds of the game.
    /// </summary>
    private const float QuietTime = 5f;

    /// <summary>
    /// How far the outline reaches out from the hero, in units of the room: about two pixels of a screen that is 1080
    /// high, where the camera shows about 16.2 units of height.
    /// </summary>
    public const float Reach = 0.03f;

    /// <summary>
    /// The settings of this machine.
    /// </summary>
    private readonly ModSettings _modSettings;

    /// <summary>
    /// The other players, by their IDs.
    /// </summary>
    private readonly Dictionary<ushort, ClientPlayerData> _playerData;

    /// <summary>
    /// The outline around this player's hero, while there is one.
    /// </summary>
    private OwnFigureOutline? _outline;

    /// <summary>
    /// The creatures in this room that a player hit, each with the time of the game of the last hit on it, for as long
    /// as they keep the fight going.
    /// </summary>
    private readonly List<(HealthManager Creature, float Time)> _hits = [];

    public FightOutline(ModSettings modSettings, Dictionary<ushort, ClientPlayerData> playerData) {
        _modSettings = modSettings;
        _playerData = playerData;
    }

    /// <summary>
    /// Registers the hooks, for the time this game is connected.
    /// </summary>
    public void RegisterHooks() {
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
        SceneManager.activeSceneChanged += OnSceneChanged;
    }

    /// <summary>
    /// Deregisters the hooks and takes the outline away.
    /// </summary>
    public void DeregisterHooks() {
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
        SceneManager.activeSceneChanged -= OnSceneChanged;
        Remove();
        _hits.Clear();
    }

    /// <summary>
    /// Notes that a player hit a creature in this room.
    /// </summary>
    /// <param name="creature">The creature.</param>
    public void NoteCreatureHit(HealthManager creature) {
        for (var i = 0; i < _hits.Count; i++) {
            if (_hits[i].Creature == creature) {
                _hits[i] = (creature, Time.time);
                return;
            }
        }

        _hits.Add((creature, Time.time));
    }

    /// <summary>
    /// A new room starts without a fight: a hit just before the door is not one in the room behind it.
    /// </summary>
    private void OnSceneChanged(Scene oldScene, Scene newScene) {
        _hits.Clear();
    }

    private void OnUpdate() {
        var hero = HeroController.instance;
        if (!_modSettings.OutlineOwnHeroInFights || hero == null) {
            Remove();
            return;
        }

        var colour = PlayerManager.OwnNameColour(_modSettings);
        if (_outline == null || _outline.transform.parent != hero.transform) {
            Remove();

            // Only a look, so it never stands in the way of the game
            try {
                _outline = OwnFigureOutline.Attach(hero.gameObject, colour, Reach);
            } catch (System.Exception e) {
                Logger.Error($"Could not outline own hero:\n{e}");
            }

            if (_outline == null) {
                return;
            }

            Logger.Info("Outlined own hero, lit in fights with another player on the screen");
        }

        _outline.SetColour(colour);
        _outline.SetLit(IsFightOn() && IsAnotherPlayerOnScreen());
    }

    /// <summary>
    /// Whether a fight is on: a creature that a player hit is still alive and here, and was hit less than
    /// <see cref="QuietTime"/> seconds ago. The creatures that no longer keep the fight going are let go.
    /// </summary>
    private bool IsFightOn() {
        var now = Time.time;
        for (var i = _hits.Count - 1; i >= 0; i--) {
            var (creature, time) = _hits[i];
            if (creature == null || creature.GetIsDead() || !creature.gameObject.activeInHierarchy ||
                now - time >= QuietTime) {
                _hits.RemoveAt(i);
            }
        }

        return _hits.Count > 0;
    }

    /// <summary>
    /// Whether the figure of another player is on this player's screen.
    /// </summary>
    private bool IsAnotherPlayerOnScreen() {
        var cameras = GameCameras.instance;
        var camera = cameras != null ? cameras.mainCamera : null;
        if (camera == null) {
            return false;
        }

        foreach (var player in _playerData.Values) {
            var container = player.PlayerContainer;
            if (!player.IsInLocalScene || container == null || !container.activeInHierarchy) {
                continue;
            }

            var point = camera.WorldToViewportPoint(container.transform.position);
            if (point.z > 0f && point.x is >= 0f and <= 1f && point.y is >= 0f and <= 1f) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Takes the outline away, if there is one.
    /// </summary>
    private void Remove() {
        if (_outline != null) {
            Object.Destroy(_outline.gameObject);
        }

        _outline = null;
    }
}
