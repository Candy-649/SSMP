using System;
using System.Collections.Generic;
using System.Reflection;
using GlobalEnums;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity.Action;
using SSMP.Game.Client.Entity.Component;
using SSMP.Game.Settings;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Ui;

/// <summary>
/// Health bars for creatures that were hurt: a small one above the head of a creature, and a wide one along the bottom
/// of the screen for a boss. They are drawn for the local player only; nothing about them goes over the network.
///
/// A bar shows from the first hit until the creature dies, at the part of its health that is left; a boss's from the
/// start of its fight on, full until it is hit (see <see cref="_fightStarted"/>). The part that a hit just took
/// lingers for a moment before it drains away, so that a hit can be seen even when both players land them. The health
/// of a creature is where its own game keeps it, which in co-op is the scene host's health of it: the bar of a copy
/// shows what the scene host told of it (HealthManagerComponent.ToldHealthOf).
///
/// A boss is full at the health it has as the first blow of a player lands on it, which its room can set below what it
/// was made with as its fight starts, and a fight that puts its boss out of reach between its parts and then gives it
/// health for the next part starts that part full.
///
/// A player can switch them off on the settings page of the multiplayer menu (ModSettings.ShowEnemyHealthBars). While
/// off, no bar is drawn and none follows its creature; switched on again, they take up the health creatures have then.
/// </summary>
internal class EnemyHealthBars(ModSettings modSettings) {
    /// <summary>
    /// The width of the bar above a creature, in units of the canvas, which is 1080 units high.
    /// </summary>
    private const float HeadBarWidth = 84f;

    /// <summary>
    /// The height of the bar above a creature, without its edge.
    /// </summary>
    private const float HeadBarHeight = 6f;

    /// <summary>
    /// The space between the top of a creature and its bar.
    /// </summary>
    private const float HeadBarGap = 14f;

    /// <summary>
    /// The share of the width of the screen that the bar of a boss takes.
    /// </summary>
    private const float BossBarWidthShare = 0.5f;

    /// <summary>
    /// The height of the bar of a boss, without its edge.
    /// </summary>
    private const float BossBarHeight = 11f;

    /// <summary>
    /// The space between the bottom of the screen and the lowest bar of a boss. It puts the bar in the middle of the
    /// ground below the hero's feet in a boss room, whose camera keeps that ground about 141 units high.
    /// </summary>
    private const float BossBarBottom = 63f;

    /// <summary>
    /// The space between the bars of bosses that fight at the same time.
    /// </summary>
    private const float BossBarSpacing = 9f;

    /// <summary>
    /// The width of the dark edge around a bar.
    /// </summary>
    private const float Edge = 1.5f;

    /// <summary>
    /// The size of the number above a boss that fights together with others.
    /// </summary>
    private const int NumberAboveSize = 24;

    /// <summary>
    /// The size of the number at the left end of the bar of a boss that fights together with others.
    /// </summary>
    private const int NumberOnBarSize = 18;

    /// <summary>
    /// The space between the left end of the bar of a boss and its number.
    /// </summary>
    private const float NumberOnBarGap = 8f;

    /// <summary>
    /// How long, in seconds, the part of the health that a hit took lingers before it drains away.
    /// </summary>
    private const float LostLingerTime = 0.35f;

    /// <summary>
    /// How fast the part of the health that a hit took drains away, in full bars per second.
    /// </summary>
    private const float LostDrainRate = 1.2f;

    /// <summary>
    /// How far outside of the screen, in pixels, a creature can be and still have its bar drawn at the edge of it.
    /// </summary>
    private const float ScreenMargin = 40f;

    /// <summary>
    /// The health from which a part of a creature is scenery that can't be beaten, like the coils of a boss that only
    /// block the way, or from which a creature can't be beaten for the moment, like a boss between the parts of its
    /// fight. Their bars would never move.
    /// </summary>
    private const int UnbeatableHealth = 9999;

    /// <summary>
    /// The bosses whose title the room or another part of their body shows instead of the boss itself, by the name of
    /// their object. Two tell an object of their room to fade in a title of its own making, with no title action
    /// anywhere (see <see cref="TitleUpEvent"/>), and were taken for common creatures (USER 11-07 "…被血条显示那部分当成
    /// 小怪了"). Of one the body that its head is part of shows the title, and the head is all of it that has health;
    /// one has a second part to strike, with health of its own (census of every boss the user named, 11-10).
    /// </summary>
    private static readonly HashSet<string> RoomTitledBosses = new(StringComparer.Ordinal) {
        "Phantom", "Giant Centipede Head", "Giant Centipede Butt", "Conductor Boss", "Bone Beast", "Flower Queen Boss",
        "Silk Boss", "Lost Lace Boss", "SG_head"
    };

    /// <summary>
    /// The bosses that nothing shows a title of, duels that a character offers, by the name of their object; with the
    /// events that an FSM of their own goes on as their fight starts and as it ends with nobody beaten - the player
    /// walked away - and the room where they are a boss when the same name is no boss elsewhere: one fights at the
    /// player's side in other rooms (census 11-10, USER 11-10 "你的血条要能覆盖这些boss，包括boss特有的底部宽血条规则
    /// 也要能覆盖这些boss"). Ended so, the duel left its bar on screen, the duelist alive (review of 4c098c7).
    /// </summary>
    private static readonly (string Name, string StartEvent, string EndEvent, string? Scene)[] UntitledBosses = [
        ("Mapper Spar NPC", "BATTLE HERO", "MAPPER SPAR END", null),
        ("Garmond Fighter", "BATTLE START", "UNALERT", "library_09")
    ];

    /// <summary>
    /// The event with which two bosses tell the title of their room's own making to show itself (see
    /// <see cref="RoomTitledBosses"/>): no other FSM sends or takes it (BundleGrep, 11-09).
    /// </summary>
    private const string TitleUpEvent = "TITLE UP";

    /// <summary>
    /// Whether the fight of a boss started in the room, in this game: the room started it - an arena that counts as a
    /// boss (ArenaCoop) - or showed the name of a boss (see <see cref="NoteFightStarted"/>). A boss's bar shows from
    /// then on, full until it is hit, or from the first blow that landed on it, for a player who came in after its name
    /// was shown or a way into a fight that shows none. It showed as the room was entered, before anyone fought, and
    /// then not until the first blow (USER 11-09 "boss场地就不应该还没开战就显示血条", "开战为什么不能是房间说开战的时
    /// 候（"). Of the boss rooms, 9 are arenas that start their fight, each showing the boss's name after; nearly all
    /// others show it as their fight starts (census 11-09).
    /// </summary>
    private static bool _fightStarted;

    /// <summary>
    /// Whether the fight of a boss that started in the room ended with nobody beaten, a duel that the player walked
    /// away from (see <see cref="UntitledBosses"/>), until a fight starts again: the bars of the bosses are away.
    /// </summary>
    private static bool _fightOver;

    /// <summary>
    /// The colour of what is left of the health: the white of silk.
    /// </summary>
    private static readonly Color HealthColor = new(0.97f, 0.95f, 0.91f, 0.95f);

    /// <summary>
    /// The colour of the part of the health that a hit just took: a blush of the red that silk takes when it is spent.
    /// </summary>
    private static readonly Color LostColor = new(0.9f, 0.62f, 0.62f, 0.85f);

    /// <summary>
    /// The colour behind a bar and of its edge.
    /// </summary>
    private static readonly Color BackColor = new(0.04f, 0.03f, 0.04f, 0.62f);

    /// <summary>
    /// The health that a creature starts with, which the game keeps to itself.
    /// </summary>
    private static readonly FieldInfo? InitialHealthField =
        typeof(HealthManager).GetField("initHp", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// The bars of the creatures of the scene, shown or not, by the health of the creature.
    /// </summary>
    private readonly Dictionary<HealthManager, Bar> _bars = new();

    /// <summary>
    /// The bars of bosses that are shown, by their numbers.
    /// </summary>
    private readonly List<Bar> _bossBars = [];

    /// <summary>
    /// The bosses of the scene that have had a bar along the bottom of the screen and are still there, in the order of
    /// their numbers: by the name of the room's own boss, which its copy in the other game has too, and bosses of one
    /// name in the order they first had a bar. Numbered in the order they were hit first, as they were, each game numbered them as it saw the
    /// first hits, and the time that the partner's hits take to arrive can turn those round: the boss that was 1 for
    /// one player could be 2 for the other.
    /// </summary>
    private readonly List<Bar> _numberedBosses = [];

    /// <summary>
    /// The creatures whose health the game counts as active, gathered anew every frame.
    /// </summary>
    private readonly List<HealthManager> _active = [];

    /// <summary>
    /// The bars of creatures that are gone, gathered while going through the bars.
    /// </summary>
    private readonly List<HealthManager> _gone = [];

    /// <summary>
    /// The canvas that the bars are drawn on, under the canvas of the rest of the mod.
    /// </summary>
    private Canvas? _canvas;

    /// <summary>
    /// The transform of that canvas.
    /// </summary>
    private RectTransform? _canvasTransform;

    /// <summary>
    /// Whether a failure to draw the bars was logged already, which is done once.
    /// </summary>
    private bool _failed;

    /// <summary>
    /// The creatures of the scene whose own FSM recorded their kill, whose bars are gone from then on (see
    /// <see cref="NoteKillRecorded"/>).
    /// </summary>
    private readonly HashSet<HealthManager> _killRecorded = [];

    /// <summary>
    /// The hooks of the two actions with which an FSM records the kill of its creature.
    /// </summary>
    private Hook? _recordKillHook, _recordKillV2Hook;

    /// <summary>
    /// The hooks with which the name of a boss is shown: the title action, and the event of a title of the room's own
    /// making (see <see cref="TitleUpEvent"/>).
    /// </summary>
    private Hook? _bossTitleHook, _titleUpHook;

    /// <summary>
    /// The creatures of the scene that a blow of a player landed on, as this game knows of it: a blow of its own
    /// player, and one of another player's that the game that runs the creature took (HealthManagerComponent
    /// .DamageLanded). Where a boss starts is settled by the first (see <see cref="Bar"/>).
    /// </summary>
    private readonly HashSet<HealthManager> _blowLanded = [];

    /// <summary>
    /// The bars of the bosses whose FSMs keep their health instead of a HealthManager (see
    /// <see cref="FsmHealthComponent"/>), by what reads it.
    /// </summary>
    private readonly Dictionary<FsmHealthComponent, Bar> _fsmBars = new();

    /// <summary>
    /// Creates the canvas of the bars and starts drawing them.
    /// </summary>
    public void Start() {
        var canvasObject = new GameObject("SSMP_EnemyHealthBars");
        Object.DontDestroyOnLoad(canvasObject);

        _canvas = canvasObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = -1;
        _canvas.enabled = false;

        var scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 1f;

        _canvasTransform = (RectTransform) canvasObject.transform;

        // Placed after the camera moved and before the canvases are drawn, so that the bars don't trail the creatures
        Canvas.preWillRenderCanvases += OnPreWillRenderCanvases;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
        HealthManagerComponent.DamageLanded += (healthManager, _) => _blowLanded.Add(healthManager);

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        _recordKillHook = new Hook(
            typeof(RecordJournalKill).GetMethod(nameof(RecordJournalKill.OnEnter), flags),
            new Action<Action<RecordJournalKill>, RecordJournalKill>((orig, self) => {
                orig(self);
                NoteKillRecorded(self.Fsm);
            })
        );
        _recordKillV2Hook = new Hook(
            typeof(RecordJournalKillV2).GetMethod(nameof(RecordJournalKillV2.OnEnter), flags),
            new Action<Action<RecordJournalKillV2>, RecordJournalKillV2>((orig, self) => {
                orig(self);
                NoteKillRecorded(self.Fsm);
            })
        );

        // The name of a boss shown by an FSM of the boss, which the copy of it plays too, or of its room
        _bossTitleHook = new Hook(
            typeof(HutongGames.PlayMaker.Actions.DisplayBossTitle).GetMethod(
                nameof(HutongGames.PlayMaker.Actions.DisplayBossTitle.OnEnter), flags
            ),
            new Action<Action<HutongGames.PlayMaker.Actions.DisplayBossTitle>,
                HutongGames.PlayMaker.Actions.DisplayBossTitle>((orig, self) => {
                orig(self);
                NoteFightStarted($"'{self.Fsm.Name}' of '{self.Fsm.GameObjectName}' shows the name of a boss");
            })
        );
        _titleUpHook = new Hook(
            typeof(HutongGames.PlayMaker.Fsm).GetMethod(
                "ProcessEvent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                [typeof(HutongGames.PlayMaker.FsmEvent), typeof(HutongGames.PlayMaker.FsmEventData)], null
            )!,
            new Action<Action<HutongGames.PlayMaker.Fsm, HutongGames.PlayMaker.FsmEvent?,
                    HutongGames.PlayMaker.FsmEventData?>, HutongGames.PlayMaker.Fsm, HutongGames.PlayMaker.FsmEvent?,
                HutongGames.PlayMaker.FsmEventData?>((orig, self, fsmEvent, data) => {
                if (fsmEvent != null && fsmEvent.Name == TitleUpEvent) {
                    NoteFightStarted($"'{self.Name}' of '{self.GameObjectName}' is told to show the name of a boss");
                } else if (fsmEvent != null && IsDuelEvent(fsmEvent.Name) && self.GameObject is { } owner &&
                           self.ActiveState is { } state &&
                           EntityFsmActions.FindTransition(self, state, fsmEvent.Name) != null &&
                           DuelOf(owner) is { } duel) {
                    var what = $"'{self.Name}' of '{owner.name}' goes on '{fsmEvent.Name}' from '{state.Name}'";
                    if (fsmEvent.Name == duel.StartEvent) {
                        NoteFightStarted($"{what}, its duel's start");
                    } else if (fsmEvent.Name == duel.EndEvent) {
                        NoteFightOver($"{what}, its duel's end with nobody beaten");
                    }
                }

                orig(self, fsmEvent, data);
            })
        );
    }

    /// <summary>
    /// Takes it that the fight of a boss started in the room (see <see cref="_fightStarted"/>).
    /// </summary>
    /// <param name="what">What started it, for the log.</param>
    internal static void NoteFightStarted(string what) {
        if (_fightStarted && !_fightOver) {
            return;
        }

        _fightStarted = true;
        _fightOver = false;
        Logger.Info($"[Bars] The fight of a boss starts here: {what}");
    }

    /// <summary>
    /// Takes it that the fight of a boss in the room ended with nobody beaten (see <see cref="_fightOver"/>).
    /// </summary>
    /// <param name="what">What ended it, for the log.</param>
    private static void NoteFightOver(string what) {
        if (_fightOver) {
            return;
        }

        _fightOver = true;
        Logger.Info($"[Bars] The fight of a boss here is over: {what}");
    }

    /// <summary>
    /// Takes the bar of a creature down for good once its own FSM recorded its kill. A boss that the game finishes by a
    /// scene of its own, rather than by its health running out, never dies by its health, and its bar stayed on screen
    /// with health left in it until the players left the room (USER 11-04 "那个boss的血条后来一直跟着我们直到出了房间").
    /// Both games record the kill: the scene client's copy of the action is played there too.
    /// </summary>
    /// <param name="fsm">The FSM that recorded it.</param>
    private void NoteKillRecorded(HutongGames.PlayMaker.Fsm? fsm) {
        var owner = fsm?.GameObject;
        if (owner == null || owner.GetComponentInParent<HealthManager>() is not { } healthManager ||
            !_killRecorded.Add(healthManager)) {
            return;
        }

        Logger.Info($"[Bars] '{healthManager.name}' recorded its own kill, so its health bar goes");
    }

    /// <summary>
    /// Places the bars for this frame, keeping what fails to itself.
    /// </summary>
    private void OnPreWillRenderCanvases() {
        try {
            UpdateBars();
        } catch (Exception e) {
            if (!_failed) {
                _failed = true;
                Logger.Error($"Could not draw the health bars of creatures:\n{e}");
            }
        }
    }

    /// <summary>
    /// Forgets the bars of the creatures of the previous scene.
    /// </summary>
    private void OnActiveSceneChanged(Scene oldScene, Scene newScene) {
        foreach (var bar in _bars.Values) {
            bar.Destroy();
        }

        _bars.Clear();
        foreach (var bar in _fsmBars.Values) {
            bar.Destroy();
        }

        _fsmBars.Clear();
        _bossBars.Clear();
        _numberedBosses.Clear();
        _killRecorded.Clear();
        _blowLanded.Clear();
        _fightStarted = false;
        _fightOver = false;
    }

    /// <summary>
    /// Brings the bars of the creatures of the scene up to date and places them.
    /// </summary>
    private void UpdateBars() {
        if (_canvas == null || _canvasTransform == null) {
            return;
        }

        var camera = GameCameras.SilentInstance?.mainCamera;
        var isShown = camera != null && modSettings.ShowEnemyHealthBars && IsPlaying();
        if (_canvas.enabled != isShown) {
            _canvas.enabled = isShown;
        }

        if (!isShown) {
            return;
        }

        _active.Clear();
        _active.AddRange(HealthManager.EnumerateActiveEnemies());
        foreach (var healthManager in _active) {
            if (healthManager == null) {
                continue;
            }

            if (!_bars.TryGetValue(healthManager, out var bar)) {
                // Known from the start, so that the full health is known before the first hit, and whether it is a
                // boss, whose full health is known differently
                bar = Bar.Of(healthManager, IsBoss(healthManager));
                _bars[healthManager] = bar;
            }

            bar.SeenFrame = Time.frameCount;
        }

        foreach (var fsmHealth in FsmHealthComponent.All) {
            if (!fsmHealth.IsShown) {
                continue;
            }

            if (!_fsmBars.TryGetValue(fsmHealth, out var bar)) {
                bar = new Bar(
                    fsmHealth.Creature, () => fsmHealth.Health, () => fsmHealth.IsBeaten, fsmHealth.Full, true,
                    () => fsmHealth.Full
                );
                _fsmBars[fsmHealth] = bar;
            }

            bar.SeenFrame = Time.frameCount;
        }

        var scale = _canvas.scaleFactor;
        var bossBarsChanged = false;
        foreach (var pair in _bars) {
            var healthManager = pair.Key;
            var bar = pair.Value;
            if (healthManager == null) {
                _gone.Add(healthManager!);
                bossBarsChanged |= _bossBars.Remove(bar);
                bar.Destroy();
                continue;
            }

            bossBarsChanged |= ShowBar(
                bar, _blowLanded.Contains(healthManager) || healthManager.HasTakenDamage(),
                _killRecorded.Contains(healthManager), camera!, scale
            );
        }

        foreach (var healthManager in _gone) {
            _bars.Remove(healthManager);
        }

        _gone.Clear();
        foreach (var pair in _fsmBars) {
            bossBarsChanged |= ShowBar(pair.Value, pair.Key.WasHit, false, camera!, scale);
        }
        if (bossBarsChanged) {
            PlaceBossBars();
        }

        // Bosses that fight together are told apart by a number above each of them and at the end of its bar
        var showNumbers = _bossBars.Count >= 2;
        foreach (var bossBar in _bossBars) {
            bossBar.ShowNumber(showNumbers);
            if (showNumbers) {
                PlaceNumberAboveHead(bossBar, camera!, scale);
            }
        }
    }

    /// <summary>
    /// Brings a bar up to date and draws it, or hides it.
    /// </summary>
    /// <param name="bar">The bar.</param>
    /// <param name="blowLanded">Whether a blow of a player landed on its creature, as this game knows.</param>
    /// <param name="killRecorded">Whether the game recorded the kill of its creature.</param>
    /// <param name="camera">The camera.</param>
    /// <param name="scale">The scale of the canvas.</param>
    /// <returns>Whether the bars of the bosses changed.</returns>
    private bool ShowBar(Bar bar, bool blowLanded, bool killRecorded, Camera camera, float scale) {
        bar.Update(bar.SeenFrame == Time.frameCount, blowLanded);
        if (!bar.IsShown || killRecorded) {
            bar.Hide();
            return _bossBars.Remove(bar);
        }

        if (!bar.HasView) {
            bar.CreateView(_canvasTransform!);
        }

        var bossBarsChanged = false;
        if (bar.IsBoss && !_bossBars.Contains(bar)) {
            AddBossBar(bar);
            bossBarsChanged = true;
        }

        bar.Draw(Time.unscaledTime);
        if (!bar.IsBoss) {
            PlaceAboveHead(bar, camera, scale);
        }

        return bossBarsChanged;
    }

    /// <summary>
    /// Adds the bar of a boss to the bars along the bottom of the screen, with the number that its name gives it among
    /// the bosses of the scene (see <see cref="_numberedBosses"/>). A boss that comes back keeps its number.
    /// </summary>
    private void AddBossBar(Bar bar) {
        if (!_numberedBosses.Contains(bar)) {
            // A boss that died or is gone counts no more, so that a game that never had its bar, like one whose player
            // walked in after it died, numbers the bosses that are left the same way
            _numberedBosses.RemoveAll(other => other.IsGone);

            // A boss of the same name whose bar is gone hands its number on: the copy of a boss that this game has just
            // taken over, whose bar is now the one of the room's own boss, or a boss that came back as another object.
            // One that the game no longer counts as active is gone even before its bar is taken down in this frame
            var gone = _numberedBosses.FindIndex(other =>
                other.BossName == bar.BossName &&
                (other.SeenFrame != Time.frameCount || !_bossBars.Contains(other))
            );
            if (gone >= 0) {
                _numberedBosses[gone] = bar;
            } else {
                var at = _numberedBosses.FindIndex(other => string.CompareOrdinal(other.BossName, bar.BossName) > 0);
                _numberedBosses.Insert(at < 0 ? _numberedBosses.Count : at, bar);
            }

            for (var i = 0; i < _numberedBosses.Count; i++) {
                _numberedBosses[i].Number = i + 1;
            }
        }

        _bossBars.Add(bar);
        _bossBars.Sort((a, b) => a.Number.CompareTo(b.Number));
    }

    /// <summary>
    /// Whether the player is playing: not in a menu, the inventory or a cutscene, not between rooms, and not dead on the
    /// way to a bench or caught on the way back to the last safe place, while the screen goes black.
    /// </summary>
    private static bool IsPlaying() {
        var gameManager = GameManager.SilentInstance;
        return gameManager != null && gameManager.GameState == GameState.PLAYING && !gameManager.IsInSceneTransition &&
               HeroController.SilentInstance is {
                   cState: { dead: false, hazardDeath: false, hazardRespawning: false }
               } && PlayerData.instance is { isInventoryOpen: false } &&
               GameCameras.SilentInstance is { IsInCinematic: false } &&
               !SceneUtil.IsNonGameplayScene(SceneManager.GetActiveScene().name);
    }

    /// <summary>
    /// Puts the bar of a creature above its head, or hides it while the creature is off the screen or can't be seen.
    /// </summary>
    private static void PlaceAboveHead(Bar bar, Camera camera, float scale) {
        if (!bar.TryGetTop(out var top)) {
            bar.SetVisible(false);
            return;
        }

        var point = camera.WorldToScreenPoint(top);
        var isOnScreen = point.z > 0f && point.x > -ScreenMargin && point.x < Screen.width + ScreenMargin &&
                         point.y > -ScreenMargin && point.y < Screen.height + ScreenMargin;
        bar.SetVisible(isOnScreen);
        if (isOnScreen) {
            bar.SetScreenPosition(new Vector2(point.x, point.y + (HeadBarGap + HeadBarHeight / 2f + Edge) * scale));
        }
    }

    /// <summary>
    /// Puts the number of a boss above its head, or hides it while the boss is off the screen or can't be seen.
    /// </summary>
    private void PlaceNumberAboveHead(Bar bar, Camera camera, float scale) {
        if (_canvasTransform == null || !bar.TryGetTop(out var top)) {
            bar.SetNumberAbove(null, null);
            return;
        }

        var point = camera.WorldToScreenPoint(top);
        var isOnScreen = point.z > 0f && point.x > -ScreenMargin && point.x < Screen.width + ScreenMargin &&
                         point.y > -ScreenMargin && point.y < Screen.height + ScreenMargin;
        bar.SetNumberAbove(
            _canvasTransform,
            isOnScreen ? new Vector2(point.x, point.y + (HeadBarGap + NumberAboveSize / 2f) * scale) : null
        );
    }

    /// <summary>
    /// Stacks the bars of the bosses that are shown from the bottom of the screen up, the lowest number on top, so that
    /// they read 1, 2 from the top down. With 1 at the bottom and 2 above it, they were read the wrong way round (USER
    /// 10-08, "上2下1"). A single bar stays at the bottom.
    /// </summary>
    private void PlaceBossBars() {
        for (var i = 0; i < _bossBars.Count; i++) {
            var fromTheBottom = _bossBars.Count - 1 - i;
            _bossBars[i].PlaceAtBottom(BossBarBottom + fromTheBottom * (BossBarHeight + 2f * Edge + BossBarSpacing));
        }
    }

    /// <summary>
    /// Gets the health that a creature starts with, or its health now if that can't be read.
    /// </summary>
    private static int GetInitialHealth(HealthManager healthManager) {
        return InitialHealthField?.GetValue(healthManager) is int initialHealth ? initialHealth : healthManager.hp;
    }

    /// <summary>
    /// Whether an event is one that starts or ends the fight of a boss that nothing shows a title of, in an FSM of the
    /// boss's own (see <see cref="UntitledBosses"/>): looked at first, as every event of every FSM passes by.
    /// </summary>
    private static bool IsDuelEvent(string eventName) {
        foreach (var boss in UntitledBosses) {
            if (boss.StartEvent == eventName || boss.EndEvent == eventName) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The boss that nothing shows a title of which an object is, in the room it is in (see
    /// <see cref="UntitledBosses"/>), or null if it is none.
    /// </summary>
    private static (string Name, string StartEvent, string EndEvent, string? Scene)? DuelOf(GameObject gameObject) {
        var name = gameObject.name.Replace("(Clone)", "").Trim();
        foreach (var boss in UntitledBosses) {
            if (boss.Name == name &&
                (boss.Scene == null ||
                 string.Equals(gameObject.scene.name, boss.Scene, StringComparison.OrdinalIgnoreCase))) {
                return boss;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a creature is a boss: one of its FSMs shows the title of a boss, as an FSM of almost every boss's own
    /// does, or the room or another part of it shows its title, or it is a duel that shows none (see
    /// <see cref="RoomTitledBosses"/>, <see cref="UntitledBosses"/>). Also what keeps a fight going for the outline of
    /// the player's own hero for as long as the creature lives (Game.Client.FightOutline).
    /// </summary>
    internal static bool IsBoss(HealthManager healthManager) {
        var gameObject = healthManager.gameObject;
        if (RoomTitledBosses.Contains(gameObject.name.Replace("(Clone)", "").Trim()) || DuelOf(gameObject) != null) {
            return true;
        }

        foreach (var playMakerFsm in gameObject.GetComponents<PlayMakerFSM>()) {
            foreach (var state in playMakerFsm.FsmStates ?? []) {
                foreach (var action in state.Actions ?? []) {
                    if (action is HutongGames.PlayMaker.Actions.DisplayBossTitle) {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The bar of the health of a creature, and what is known about the creature for it.
    /// </summary>
    /// <param name="creature">The object of the creature.</param>
    /// <param name="readHealth">What reads the health that the bar shows.</param>
    /// <param name="isDead">What says whether the creature died.</param>
    /// <param name="initialHealth">The health that the creature starts with.</param>
    /// <param name="isBoss">Whether the creature is a boss.</param>
    /// <param name="readFull">What reads the full health of the creature, for one whose full health is known (see
    /// <see cref="FsmHealthComponent.Full"/>); null to learn it from the health (see <see cref="FollowHealth"/>).
    /// </param>
    private class Bar(
        GameObject creature,
        Func<int> readHealth,
        Func<bool> isDead,
        int initialHealth,
        bool isBoss,
        Func<int>? readFull = null
    ) {
        /// <summary>
        /// The object of the creature.
        /// </summary>
        private readonly GameObject _creature = creature;

        /// <summary>
        /// The collider of the body of the creature, whose top the bar is drawn above.
        /// </summary>
        private readonly Collider2D? _body = creature.GetComponent<Collider2D>();

        /// <summary>
        /// The renderer of the creature, for where it is drawn and whether it is drawn at all.
        /// </summary>
        private readonly Renderer? _renderer = creature.GetComponent<Renderer>();

        /// <summary>
        /// The bar of a creature with a HealthManager. A copy's own steps can set its health ahead of what it is told
        /// for a moment, and are not shown: the health is the scene host's to say (USER 11-09 "客机也不应该自己摆弄血条，
        /// 因为客机那边有个复制体，自己搞太容易搞砸了").
        /// </summary>
        public static Bar Of(HealthManager healthManager, bool isBoss) {
            return new Bar(
                healthManager.gameObject, () => HealthManagerComponent.ToldHealthOf(healthManager) ?? healthManager.hp,
                () => healthManager == null || healthManager.isDead, GetInitialHealth(healthManager), isBoss
            );
        }

        /// <summary>
        /// The full health of the creature: what it started with, or more if it was given more since, like by the
        /// start of a fight. A boss can also start lower and start each part of its fight over (see
        /// <see cref="FollowHealth"/>).
        /// </summary>
        private int _fullHealth = initialHealth;

        /// <summary>
        /// Whether where a boss starts is settled, by the first blow that landed on it, after which its full health no
        /// longer follows what it has (see <see cref="FollowHealth"/>).
        /// </summary>
        private bool _isStartSettled;

        /// <summary>
        /// The last health of its own that the creature was seen with while alive, or -1 before any.
        /// </summary>
        private int _lastHealth = -1;

        /// <summary>
        /// Whether the creature was given a health that it can't be beaten from (see <see cref="UnbeatableHealth"/>)
        /// and was not given health of its own since.
        /// </summary>
        private bool _isUnbeatable;

        /// <summary>
        /// The health of its own that the creature had just before it was made unbeatable, or -1 when none was seen.
        /// </summary>
        private int _healthBeforeUnbeatable = -1;

        /// <summary>
        /// The health that the bar shows: what the creature has, or for a copy what it was told (see
        /// <see cref="Update"/>).
        /// </summary>
        private int _health;

        /// <summary>
        /// The share of the full health that is shown as left.
        /// </summary>
        private float _shownShare = 1f;

        /// <summary>
        /// The share of the full health that is shown as lost a moment ago, which drains to the share that is left.
        /// </summary>
        private float _lostShare = 1f;

        /// <summary>
        /// When the part that was lost last starts to drain away, in unscaled seconds.
        /// </summary>
        private float _drainTime;

        /// <summary>
        /// Whether the creature was hurt since it was last at full health, after which its bar shows until it dies.
        /// </summary>
        private bool _isHurt;

        /// <summary>
        /// Whether the creature came to the end of its health without dying and was not given health since (see
        /// <see cref="FollowHealth"/>).
        /// </summary>
        private bool _isSpent;

        /// <summary>
        /// Whether the creature was active in the previous frame.
        /// </summary>
        private bool _wasActive;

        /// <summary>
        /// The objects that draw the bar, once it was shown.
        /// </summary>
        private GameObject? _view;

        /// <summary>
        /// The transform of the bar.
        /// </summary>
        private RectTransform? _viewTransform;

        /// <summary>
        /// The part of the bar that shows what was lost a moment ago.
        /// </summary>
        private RectTransform? _lost;

        /// <summary>
        /// The part of the bar that shows what is left.
        /// </summary>
        private RectTransform? _left;

        /// <summary>
        /// The number at the left end of the bar of a boss, shown while several bosses fight together.
        /// </summary>
        private Text? _numberOnBar;

        /// <summary>
        /// The number above the head of a boss, shown while several bosses fight together.
        /// </summary>
        private Text? _numberAbove;

        /// <summary>
        /// The frame in which the game last counted the creature as active.
        /// </summary>
        public int SeenFrame;

        /// <summary>
        /// The number of a boss among the bosses that fight together, or 0 before it had one.
        /// </summary>
        public int Number;

        /// <summary>
        /// The name of the room's own creature, which its copy in the other game has with "(Clone)" after it.
        /// </summary>
        public string BossName { get; } = creature.name.Replace("(Clone)", "").Trim();

        /// <summary>
        /// Whether the creature is dead or gone altogether.
        /// </summary>
        public bool IsGone => _creature == null || isDead();

        /// <summary>
        /// Whether the creature is a boss, whose bar is along the bottom of the screen.
        /// </summary>
        public bool IsBoss { get; } = isBoss;

        /// <summary>
        /// Whether the bar is to be shown: the creature is active, alive, can be beaten, and was hurt, or for a boss,
        /// its fight started (see <see cref="_fightStarted"/>).
        /// </summary>
        public bool IsShown { get; private set; }

        /// <summary>
        /// Whether the objects that draw the bar were made.
        /// </summary>
        public bool HasView => _view != null;

        /// <summary>
        /// Follows the health of the creature.
        /// </summary>
        /// <param name="isActive">Whether the game counts the creature as active in this frame.</param>
        /// <param name="blowLanded">Whether a blow of a player landed on the creature, as this game knows.</param>
        public void Update(bool isActive, bool blowLanded) {
            var health = readHealth();
            _health = health;
            var isAlive = isActive && !isDead() && health > 0;

            // A creature that comes back, like one of a pool that is used again, starts over
            if (isActive && !_wasActive && health >= _fullHealth) {
                _isHurt = false;
            }

            _wasActive = isActive;
            if (isActive && !isDead() && health <= 0) {
                _isSpent = true;
            }

            if (isAlive) {
                FollowHealth(health, blowLanded);
            }

            IsShown = isAlive && !_isUnbeatable && _fullHealth > 1 && _fullHealth < UnbeatableHealth &&
                      (IsBoss ? !_fightOver && (_fightStarted || _isStartSettled) : _isHurt);
        }

        /// <summary>
        /// Follows the health of a creature that is alive.
        /// </summary>
        /// <param name="health">The health that the creature has.</param>
        /// <param name="blowLanded">Whether a blow of a player landed on the creature, as this game knows.</param>
        private void FollowHealth(int health, bool blowLanded) {
            // A creature whose full health is known: a player who came in late saw it full at what was left
            if (readFull != null) {
                _fullHealth = readFull();
                _isStartSettled |= blowLanded;
                _isHurt |= health < _fullHealth;
                _lastHealth = health;
                return;
            }

            // Given a health that it can't be beaten from for a while: a fight puts its boss out of reach between its
            // parts, and a creature that swallowed something it has to spit out can't be hurt until it does. That is
            // no health of the creature's own, so it is never taken for its full health, and the bar is away
            // meanwhile. Taken for it, the full health of two bosses was 99999 from the second part of their fight
            // on, which this can't show, and their bars never came back.
            if (health >= UnbeatableHealth) {
                if (!_isUnbeatable) {
                    _isUnbeatable = true;
                    _healthBeforeUnbeatable = _lastHealth;
                }

                return;
            }

            if (_isUnbeatable) {
                // A hit takes a little off what it was given, so it is over only once what it has is health again
                if (health >= UnbeatableHealth / 2) {
                    return;
                }

                _isUnbeatable = false;

                // A boss that is given health of its own afterwards starts the next part of its fight with it, full.
                // One given back what it had goes on from there, as does any other creature: one that spat out what it
                // swallowed is left with what it had or less
                if (IsBoss && health != _healthBeforeUnbeatable) {
                    _fullHealth = health;

                    // Shown for the new part whether or not it was hit in the last one, once it was seen fighting:
                    // of two bosses that start the part together, only the one hit before showed a bar
                    if (_healthBeforeUnbeatable >= 0) {
                        _isHurt = true;
                    }

                    Logger.Info(
                        $"[Bars] The boss '{_creature.name}' was out of reach and starts again with {health} " +
                        $"health, from {_healthBeforeUnbeatable}"
                    );
                }
            }

            // A boss that came to the end of its health without dying starts the next part of its fight with the health it
            // is given for it, full. Taken as more of what it had, a part with less health than an earlier one started
            // at that share of the earlier one: the fourth part of a fight at less than half (USER 11-08 "…的血条很奇怪")
            if (_isSpent) {
                _isSpent = false;
                if (IsBoss) {
                    _fullHealth = health;
                    Logger.Info(
                        $"[Bars] The boss '{_creature.name}' came to the end of its health without dying and " +
                        $"starts the next part of its fight with {health} health"
                    );
                }
            }

            // Where a boss starts, which its room can set lower than what the boss was made with as its fight starts:
            // its full health is what it has until the first blow of a player lands on it, and a bar made after that
            // starts from what it has then. Taken for a hit, the bars of two bosses that are given 190 of the 300 they
            // are made with showed, part empty, as soon as their room was entered. A second after the bar was made
            // used to settle it instead, and a boss given 100 of its 1204 later than that showed a sliver before
            // anyone struck it (USER 11-09 "去掉“推测”，就是拿到1204当成满血了")
            if (IsBoss && !_isStartSettled) {
                if (!blowLanded || _lastHealth < 0) {
                    _fullHealth = health;
                }

                if (blowLanded) {
                    _isStartSettled = true;
                    Logger.Info(
                        $"[Bars] The boss '{_creature.name}' starts its fight full at {_fullHealth}, the first " +
                        $"blow on it leaving {health}"
                    );
                }
            }

            _lastHealth = health;
            if (health > _fullHealth) {
                _fullHealth = health;
            }

            if (health < _fullHealth) {
                _isHurt = true;
            }
        }

        /// <summary>
        /// Makes the objects that draw the bar.
        /// </summary>
        public void CreateView(RectTransform canvas) {
            _view = new GameObject(IsBoss ? "Boss Health Bar" : "Health Bar", typeof(RectTransform));
            _viewTransform = (RectTransform) _view.transform;
            _viewTransform.SetParent(canvas, false);
            AddImage(_view, BackColor);

            var inner = new GameObject("Inner", typeof(RectTransform));
            var innerTransform = (RectTransform) inner.transform;
            innerTransform.SetParent(_viewTransform, false);
            innerTransform.anchorMin = Vector2.zero;
            innerTransform.anchorMax = Vector2.one;
            innerTransform.offsetMin = new Vector2(Edge, Edge);
            innerTransform.offsetMax = new Vector2(-Edge, -Edge);

            _lost = CreatePart(innerTransform, "Lost", LostColor);
            _left = CreatePart(innerTransform, "Left", HealthColor);

            if (IsBoss) {
                _viewTransform.anchorMin = new Vector2(0.5f - BossBarWidthShare / 2f, 0f);
                _viewTransform.anchorMax = new Vector2(0.5f + BossBarWidthShare / 2f, 0f);
                _viewTransform.pivot = new Vector2(0.5f, 0f);
                _viewTransform.sizeDelta = new Vector2(0f, BossBarHeight + 2f * Edge);

                _numberOnBar = CreateNumber(_viewTransform, "Number", NumberOnBarSize, TextAnchor.MiddleRight);
                var numberTransform = _numberOnBar.rectTransform;
                numberTransform.anchorMin = new Vector2(0f, 0.5f);
                numberTransform.anchorMax = new Vector2(0f, 0.5f);
                numberTransform.pivot = new Vector2(1f, 0.5f);
                numberTransform.anchoredPosition = new Vector2(-NumberOnBarGap, 0f);
                _numberOnBar.gameObject.SetActive(false);
            } else {
                _viewTransform.anchorMin = Vector2.zero;
                _viewTransform.anchorMax = Vector2.zero;
                _viewTransform.pivot = new Vector2(0.5f, 0.5f);
                _viewTransform.sizeDelta = new Vector2(HeadBarWidth + 2f * Edge, HeadBarHeight + 2f * Edge);
            }

            // A new bar starts full and drains to what is left, so that the first hit shows too
            _shownShare = 1f;
            _lostShare = 1f;
            _drainTime = Time.unscaledTime + LostLingerTime;
        }

        /// <summary>
        /// Shows the health that is left, and drains what was lost a moment ago.
        /// </summary>
        public void Draw(float now) {
            if (_view == null || _left == null || _lost == null) {
                return;
            }

            if (!_view.activeSelf) {
                _view.SetActive(true);
            }

            var share = Mathf.Clamp01((float) _health / _fullHealth);
            if (share < _shownShare) {
                // A new hit lingers from where the last one left off
                _lostShare = Mathf.Max(_lostShare, _shownShare);
                _drainTime = now + LostLingerTime;
            }

            _shownShare = share;
            if (_lostShare < share) {
                _lostShare = share;
            } else if (_lostShare > share && now >= _drainTime) {
                _lostShare = Mathf.Max(share, _lostShare - LostDrainRate * Time.unscaledDeltaTime);
            }

            SetShare(_left, _shownShare);
            SetShare(_lost, _lostShare);
        }

        /// <summary>
        /// Gets the point above the middle of the creature that its bar is drawn over.
        /// </summary>
        /// <returns>Whether the creature can be seen, so that its bar is drawn.</returns>
        public bool TryGetTop(out Vector3 top) {
            var position = _creature.transform.position;
            if (_renderer != null && !_renderer.enabled) {
                top = position;
                return false;
            }

            Bounds bounds;
            if (_body != null && _body.enabled) {
                bounds = _body.bounds;
            } else if (_renderer != null) {
                bounds = _renderer.bounds;
            } else {
                top = position;
                return true;
            }

            top = new Vector3(bounds.center.x, bounds.max.y, position.z);
            return true;
        }

        /// <summary>
        /// Puts the bar above a creature at a point on the screen, in pixels.
        /// </summary>
        public void SetScreenPosition(Vector2 point) {
            if (_viewTransform != null) {
                _viewTransform.position = point;
            }
        }

        /// <summary>
        /// Puts the bar of a boss along the bottom of the screen, at a height in units of the canvas.
        /// </summary>
        public void PlaceAtBottom(float height) {
            if (_viewTransform != null) {
                _viewTransform.anchoredPosition = new Vector2(0f, height);
            }
        }

        /// <summary>
        /// Shows or hides the bar while it is meant to be shown, like while the creature is off the screen.
        /// </summary>
        public void SetVisible(bool isVisible) {
            if (_view != null && _view.activeSelf != isVisible) {
                _view.SetActive(isVisible);
            }
        }

        /// <summary>
        /// Hides the bar, and the number above the creature.
        /// </summary>
        public void Hide() {
            SetVisible(false);
            SetNumberAbove(null, null);
        }

        /// <summary>
        /// Shows or hides the number at the left end of the bar of a boss, and hides the one above it with it.
        /// </summary>
        public void ShowNumber(bool isShown) {
            if (_numberOnBar != null) {
                var text = Number.ToString();
                if (_numberOnBar.text != text) {
                    _numberOnBar.text = text;
                }

                if (_numberOnBar.gameObject.activeSelf != isShown) {
                    _numberOnBar.gameObject.SetActive(isShown);
                }
            }

            if (!isShown) {
                SetNumberAbove(null, null);
            }
        }

        /// <summary>
        /// Puts the number of a boss above it at a point on the screen, in pixels, or hides it without a point. The
        /// number is made on the canvas the first time it is shown.
        /// </summary>
        public void SetNumberAbove(RectTransform? canvas, Vector2? point) {
            if (point is not { } screenPoint) {
                if (_numberAbove != null && _numberAbove.gameObject.activeSelf) {
                    _numberAbove.gameObject.SetActive(false);
                }

                return;
            }

            if (_numberAbove == null) {
                if (canvas == null) {
                    return;
                }

                _numberAbove = CreateNumber(canvas, "Boss Number", NumberAboveSize, TextAnchor.MiddleCenter);
                var numberTransform = _numberAbove.rectTransform;
                numberTransform.anchorMin = Vector2.zero;
                numberTransform.anchorMax = Vector2.zero;
                numberTransform.pivot = new Vector2(0.5f, 0.5f);
            }

            var text = Number.ToString();
            if (_numberAbove.text != text) {
                _numberAbove.text = text;
            }

            if (!_numberAbove.gameObject.activeSelf) {
                _numberAbove.gameObject.SetActive(true);
            }

            _numberAbove.rectTransform.position = screenPoint;
        }

        /// <summary>
        /// Removes the objects that draw the bar, and the number above the creature.
        /// </summary>
        public void Destroy() {
            if (_view != null) {
                Object.Destroy(_view);
            }

            if (_numberAbove != null) {
                Object.Destroy(_numberAbove.gameObject);
            }

            _view = null;
            _numberAbove = null;
            _numberOnBar = null;
        }

        /// <summary>
        /// Makes a number in the white of silk with a dark edge, which reads against any background.
        /// </summary>
        private static Text CreateNumber(RectTransform parent, string name, int size, TextAnchor alignment) {
            var numberObject = new GameObject(name, typeof(RectTransform));
            var numberTransform = (RectTransform) numberObject.transform;
            numberTransform.SetParent(parent, false);
            numberTransform.sizeDelta = new Vector2(size * 2f, size * 1.5f);

            var text = numberObject.AddComponent<Text>();
            text.font = Resources.FontManager.UIFontRegular;
            text.fontSize = size;
            text.fontStyle = FontStyle.Bold;
            text.alignment = alignment;
            text.color = HealthColor;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;

            var outline = numberObject.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);
            return text;
        }

        /// <summary>
        /// Makes a part of the bar that fills it from the left.
        /// </summary>
        private static RectTransform CreatePart(RectTransform parent, string name, Color color) {
            var part = new GameObject(name, typeof(RectTransform));
            var partTransform = (RectTransform) part.transform;
            partTransform.SetParent(parent, false);
            partTransform.anchorMin = Vector2.zero;
            partTransform.anchorMax = Vector2.one;
            partTransform.offsetMin = Vector2.zero;
            partTransform.offsetMax = Vector2.zero;
            AddImage(part, color);
            return partTransform;
        }

        /// <summary>
        /// Fills a part of the bar from the left up to a share of its width.
        /// </summary>
        private static void SetShare(RectTransform part, float share) {
            if (!Mathf.Approximately(part.anchorMax.x, share)) {
                part.anchorMax = new Vector2(share, 1f);
            }
        }

        /// <summary>
        /// Adds a plain coloured image that doesn't take clicks to an object.
        /// </summary>
        private static void AddImage(GameObject gameObject, Color color) {
            var image = gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
        }
    }
}
