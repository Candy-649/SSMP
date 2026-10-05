using System;
using System.Collections.Generic;
using System.Reflection;
using GlobalEnums;
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
/// A bar shows from the first hit until the creature dies, at the part of its health that is left. The part that a hit
/// just took lingers for a moment before it drains away, so that a hit can be seen even when both players land them.
/// The health of a creature is where its own game keeps it, which in co-op is the scene host's health of it.
/// </summary>
internal class EnemyHealthBars {
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
    /// block the way. Their bars would never move.
    /// </summary>
    private const int UnbeatableHealth = 9999;

    /// <summary>
    /// The name of the action that shows the title of a boss, which almost every boss has in an FSM of its own.
    /// </summary>
    private const string BossTitleActionName = "DisplayBossTitle";

    /// <summary>
    /// The bosses whose title the room shows instead of the boss itself, by the name of their object.
    /// </summary>
    private static readonly HashSet<string> RoomTitledBosses = new(StringComparer.Ordinal) {
        "Phantom", "Giant Centipede Head", "Conductor Boss", "Bone Beast", "Flower Queen Boss"
    };

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
    /// The bars of bosses that are shown, from the bottom of the screen up, in the order that they were hit first.
    /// </summary>
    private readonly List<Bar> _bossBars = [];

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
        _bossBars.Clear();
    }

    /// <summary>
    /// Brings the bars of the creatures of the scene up to date and places them.
    /// </summary>
    private void UpdateBars() {
        if (_canvas == null || _canvasTransform == null) {
            return;
        }

        var camera = GameCameras.SilentInstance?.mainCamera;
        var isShown = camera != null && IsPlaying();
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
                // Known from the start, so that the full health is known before the first hit
                bar = new Bar(healthManager, GetInitialHealth(healthManager));
                _bars[healthManager] = bar;
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

            var isActive = bar.SeenFrame == Time.frameCount;
            bar.Update(isActive);
            if (!bar.IsShown) {
                bar.Hide();
                bossBarsChanged |= _bossBars.Remove(bar);
                continue;
            }

            if (!bar.HasView) {
                bar.IsBoss = IsBoss(healthManager);
                bar.CreateView(_canvasTransform);
            }

            if (bar.IsBoss && !_bossBars.Contains(bar)) {
                AddBossBar(bar);
                bossBarsChanged = true;
            }

            bar.Draw(Time.unscaledTime);
            if (!bar.IsBoss) {
                PlaceAboveHead(bar, camera!, scale);
            }
        }

        foreach (var healthManager in _gone) {
            _bars.Remove(healthManager);
        }

        _gone.Clear();
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
    /// Adds the bar of a boss to the bars along the bottom of the screen, with a number that no other boss there has.
    /// A boss that comes back keeps its number if it is still free.
    /// </summary>
    private void AddBossBar(Bar bar) {
        if (bar.Number <= 0 || _bossBars.Exists(other => other.Number == bar.Number)) {
            bar.Number = 1;
            while (_bossBars.Exists(other => other.Number == bar.Number)) {
                bar.Number++;
            }
        }

        _bossBars.Add(bar);
        _bossBars.Sort((a, b) => a.Number.CompareTo(b.Number));
    }

    /// <summary>
    /// Whether the player is playing: not in a menu, the inventory or a cutscene, and not between rooms.
    /// </summary>
    private static bool IsPlaying() {
        var gameManager = GameManager.SilentInstance;
        return gameManager != null && gameManager.GameState == GameState.PLAYING && !gameManager.IsInSceneTransition &&
               HeroController.SilentInstance != null && PlayerData.instance is { isInventoryOpen: false } &&
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
    /// Stacks the bars of the bosses that are shown from the bottom of the screen up.
    /// </summary>
    private void PlaceBossBars() {
        for (var i = 0; i < _bossBars.Count; i++) {
            _bossBars[i].PlaceAtBottom(BossBarBottom + i * (BossBarHeight + 2f * Edge + BossBarSpacing));
        }
    }

    /// <summary>
    /// Gets the health that a creature starts with, or its health now if that can't be read.
    /// </summary>
    private static int GetInitialHealth(HealthManager healthManager) {
        return InitialHealthField?.GetValue(healthManager) is int initialHealth ? initialHealth : healthManager.hp;
    }

    /// <summary>
    /// Whether a creature is a boss: one of its FSMs shows the title of a boss, or the room shows its title.
    /// </summary>
    private static bool IsBoss(HealthManager healthManager) {
        var gameObject = healthManager.gameObject;
        if (RoomTitledBosses.Contains(gameObject.name.Replace("(Clone)", "").Trim())) {
            return true;
        }

        try {
            foreach (var playMakerFsm in gameObject.GetComponents<PlayMakerFSM>()) {
                foreach (var state in playMakerFsm.FsmStates ?? []) {
                    foreach (var action in state.Actions ?? []) {
                        if (action?.GetType().Name == BossTitleActionName) {
                            return true;
                        }
                    }
                }
            }
        } catch (Exception e) {
            Logger.Warn($"Could not read the FSMs of '{gameObject.name}' to tell whether it is a boss:\n{e}");
        }

        return false;
    }

    /// <summary>
    /// The bar of the health of a creature, and what is known about the creature for it.
    /// </summary>
    private class Bar(HealthManager healthManager, int initialHealth) {
        /// <summary>
        /// The health of the creature.
        /// </summary>
        private readonly HealthManager _healthManager = healthManager;

        /// <summary>
        /// The collider of the body of the creature, whose top the bar is drawn above.
        /// </summary>
        private readonly Collider2D? _body = healthManager.GetComponent<Collider2D>();

        /// <summary>
        /// The renderer of the creature, for where it is drawn and whether it is drawn at all.
        /// </summary>
        private readonly Renderer? _renderer = healthManager.GetComponent<Renderer>();

        /// <summary>
        /// The full health of the creature: what it started with, or more if it was given more since, like by the
        /// start of a fight.
        /// </summary>
        private int _fullHealth = initialHealth;

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
        /// Whether the creature is a boss, whose bar is along the bottom of the screen.
        /// </summary>
        public bool IsBoss;

        /// <summary>
        /// Whether the bar is to be shown: the creature is active, alive, can be beaten, and was hurt.
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
        public void Update(bool isActive) {
            var health = _healthManager.hp;
            var isAlive = isActive && !_healthManager.isDead && health > 0;

            // A creature that comes back, like one of a pool that is used again, starts over
            if (isActive && !_wasActive && health >= _fullHealth) {
                _isHurt = false;
            }

            _wasActive = isActive;
            if (isAlive && health > _fullHealth) {
                _fullHealth = health;
            }

            if (isAlive && health < _fullHealth) {
                _isHurt = true;
            }

            IsShown = isAlive && _isHurt && _fullHealth > 1 && _fullHealth < UnbeatableHealth;
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

            var share = Mathf.Clamp01((float) _healthManager.hp / _fullHealth);
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
            var position = _healthManager.transform.position;
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
