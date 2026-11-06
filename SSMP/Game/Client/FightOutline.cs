using System.Collections.Generic;
using SSMP.Game.Client.Entity.Component;
using SSMP.Game.Client.Save;
using SSMP.Game.Settings;
using SSMP.Ui;
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
/// A creature counts as hit as an attack of a player reaches it here, a block too (CoopHits.CreatureHitEvent), and as
/// damage of a player lands on it however it comes (HealthManagerComponent.DamageLanded): a blow of this player's, a
/// tool's too, and the damage of another player as this game takes it in or is told it with the creature's health. The
/// other player's tools had shown here only as copies, which touch the creature where this game has it, if at all,
/// and their hits started no fight ("道具命中怪没有成功唤出高亮").
///
/// The fight is over as the creatures that were hit are all dead or gone, or the room changes. A boss keeps it going for
/// as long as it lives, told apart the way its bar of health is (EnemyHealthBars.IsBoss). Every other creature stops
/// keeping it going once <see cref="QuietTime"/> seconds went by in which no player's attack reached a creature and no
/// creature's attack hurt this player (USER 11-06 "那个高光改回小怪或者精英怪5秒没有攻击或者被攻击就按掉"): with deaths
/// alone, a creature of the room that a player hit once and left alive kept it lit after the boss of the fight was
/// beaten. How another player is hurt is not known here, so only this player's hurts count, and that player's game
/// counts theirs.
///
/// At first it went on for 8 seconds after any last hit, which the user found too long ("感觉8秒后有点久了") and chose
/// that it ends as the creatures die ("打死就灭"). A time without a hit put it out in the middle of boss fights, while
/// the boss was out of reach and as a player got back up after a death ("被打死了复活之后高亮会消失，但是其实一直在boss战
/// 里", "中间boss没死高亮也消失了一会儿"), which bosses have been kept out of since; the time went for every creature
/// (USER 11-05 "删掉 5 秒，打死才灭") and came back for those that are no boss, counting attacks either way.
/// </summary>
internal class FightOutline {
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
    /// How long a fight goes on without an attack either way before the creatures in it that are no boss stop keeping
    /// it going, in seconds of the game: no attack of a player reaching a creature, and no attack of a creature hurting
    /// this player.
    /// </summary>
    private const float QuietTime = 5f;

    /// <summary>
    /// The creatures in this room that a player hit, for as long as they keep the fight going: each with its name,
    /// whether it is a boss, the time of the game of the last hit on it, and what struck that hit, if this game knows.
    /// </summary>
    private readonly List<Hit> _hits = [];

    /// <summary>
    /// The time of the game of the last attack of the fight either way: an attack of a player that reached a creature,
    /// or an attack of a creature that hurt this player.
    /// </summary>
    private float _lastAttackAt;

    /// <summary>
    /// Whether a fight was on as this game last looked, for the log to say as one starts and ends.
    /// </summary>
    private bool _fightWasOn;

    /// <summary>
    /// What ended the last fight, for the log.
    /// </summary>
    private string _endedBy = "";

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
        HealthManagerComponent.DamageLanded += NoteCreatureHit;
        CoopSave.HurtByCreature += NoteHurtByCreature;
    }

    /// <summary>
    /// Deregisters the hooks and takes the outline away.
    /// </summary>
    public void DeregisterHooks() {
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
        SceneManager.activeSceneChanged -= OnSceneChanged;
        HealthManagerComponent.DamageLanded -= NoteCreatureHit;
        CoopSave.HurtByCreature -= NoteHurtByCreature;
        Remove();
        _hits.Clear();
        _fightWasOn = false;
    }

    /// <summary>
    /// Notes that a player hit a creature in this room.
    /// </summary>
    /// <param name="creature">The creature.</param>
    /// <param name="source">What struck the hit, if this game knows.</param>
    public void NoteCreatureHit(HealthManager creature, GameObject? source) {
        if (creature == null) {
            return;
        }

        _lastAttackAt = Time.time;
        for (var i = 0; i < _hits.Count; i++) {
            if (_hits[i].Creature == creature) {
                _hits[i].Time = Time.time;
                _hits[i].Source = source;
                return;
            }
        }

        _hits.Add(new Hit(creature, creature.name, EnemyHealthBars.IsBoss(creature), Time.time, source));
    }

    /// <summary>
    /// Notes that an attack of a creature hurt this player, which keeps a fight that is on going as an attack of a
    /// player that reaches a creature does. It starts none.
    /// </summary>
    private void NoteHurtByCreature() {
        _lastAttackAt = Time.time;
    }

    /// <summary>
    /// A new room starts without a fight: a hit just before the door is not one in the room behind it.
    /// </summary>
    private void OnSceneChanged(Scene oldScene, Scene newScene) {
        if (_hits.Count > 0) {
            _endedBy = "the room changed";
        }

        _hits.Clear();
    }

    private void OnUpdate() {
        var hero = HeroController.instance;
        if (!_modSettings.OutlineOwnHeroInFights || hero == null) {
            Remove();
            return;
        }

        var colour = PlayerManager.OwnColour(_modSettings);
        if (_outline == null || _outline.transform.parent != hero.transform) {
            Remove();
            _outline = OwnFigureOutline.Attach(hero.gameObject, colour, Reach);
            if (_outline == null) {
                return;
            }

            Logger.Info("Outlined own hero, lit in fights with another player on the screen");
        }

        var fightOn = IsFightOn();
        if (fightOn != _fightWasOn) {
            _fightWasOn = fightOn;
            Logger.Info(fightOn ? DescribeStart() : $"[Outline] The fight is over: {_endedBy}");
        }

        _outline.SetColour(colour);
        _outline.SetLit(fightOn && IsAnotherPlayerOnScreen());
    }

    /// <summary>
    /// Whether a fight is on: a creature that a player hit is still alive and here, and it is a boss or the fight saw
    /// an attack either way less than <see cref="QuietTime"/> seconds ago. The creatures that no longer keep the fight
    /// going are let go.
    /// </summary>
    private bool IsFightOn() {
        var quiet = Time.time - _lastAttackAt >= QuietTime;
        for (var i = _hits.Count - 1; i >= 0; i--) {
            var hit = _hits[i];
            var creature = hit.Creature;
            string? why = null;
            if (creature == null) {
                why = "is gone";
            } else if (creature.GetIsDead()) {
                why = "died";
            } else if (!creature.gameObject.activeInHierarchy) {
                why = "was switched off";
            } else if (!hit.IsBoss && quiet) {
                why = $"is no boss, and {QuietTime:0} s went by without an attack either way";
            }

            if (why != null) {
                _endedBy = $"'{hit.Name}' {why}";
                _hits.RemoveAt(i);
            }
        }

        return _hits.Count > 0;
    }

    /// <summary>
    /// Says for the log which hit started the fight that is on: the last one of those that keep it going.
    /// </summary>
    private string DescribeStart() {
        var last = _hits[0];
        foreach (var hit in _hits) {
            if (hit.Time > last.Time) {
                last = hit;
            }
        }

        var source = last.Source;
        return $"[Outline] A fight is on: '{last.Name}'{(last.IsBoss ? ", a boss," : "")} was hit " +
               (source != null ? $"by '{source.name}'" : "by a player, as this game heard of it");
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

    /// <summary>
    /// A creature that a player hit, while it keeps the fight going.
    /// </summary>
    private class Hit(HealthManager creature, string name, bool isBoss, float time, GameObject? source) {
        /// <summary>
        /// The creature.
        /// </summary>
        public HealthManager Creature { get; } = creature;

        /// <summary>
        /// Its name, for the log once it is gone.
        /// </summary>
        public string Name { get; } = name;

        /// <summary>
        /// Whether it is a boss, which keeps the fight going for as long as it lives.
        /// </summary>
        public bool IsBoss { get; } = isBoss;

        /// <summary>
        /// The time of the game of the last hit on it.
        /// </summary>
        public float Time { get; set; } = time;

        /// <summary>
        /// What struck the last hit on it, if this game knows.
        /// </summary>
        public GameObject? Source { get; set; } = source;
    }
}
