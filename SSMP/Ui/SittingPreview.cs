using System;
using SSMP.Game.Client;
using SSMP.Game.Client.Skin;
using SSMP.Game.Settings;
using TMProOld;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;
using Logger = SSMP.Logging.Logger;
using Random = UnityEngine.Random;

namespace SSMP.Ui;

/// <summary>
/// This player's Hornet, sitting on the bottom edge of the multiplayer menu while its settings page is open, the way
/// she sits on a bench, so that the choices of that page can be seen on her: the outline that a fight lights and the
/// name over her head. USER 2026-10-20: "可不可以把大黄蜂坐在椅子上的那组动态放到联机设置调整高亮不高亮的地方，虽然没有
/// 椅子，但是可以坐在窗口左下角，拿窗口的下栏当椅子…最好名字也在".
///
/// The menu has no hero: the game loads her as a save starts and lets her go again on the way back. So she is loaded
/// as the page opens, and let go as it closes or the menu does. A camera of her own, far from anything that the
/// cameras of the menu show, draws her into a texture that the page shows. She sits down, then breathes and looks
/// around the way the bench has her do: one of its five clips at random, each time one ends.
/// </summary>
internal class SittingPreview : MonoBehaviour {
    /// <summary>
    /// The address under which the game loads its hero.
    /// </summary>
    internal const string HeroAddress = "Hero_Hornet";

    /// <summary>
    /// The clip of sitting down.
    /// </summary>
    private const string SitClip = "Sit";

    /// <summary>
    /// The clips that the bench picks from at random while the hero sits, each time one ends.
    /// </summary>
    private static readonly string[] RestClips = ["Sit Idle", "SitLook 1", "SitLook 2", "SitLook 3", "SitLook 4"];

    /// <summary>
    /// How many units of the menu, which is 1080 high, one unit of the room is drawn as. In the room the camera shows
    /// about 16.2 units of height, about 67 of the menu's units each; on the page she is a little over half that size.
    /// </summary>
    private const float MenuUnitsPerUnit = 35.2f;

    /// <summary>
    /// How many units of the menu one unit of the room is drawn as in the room itself.
    /// </summary>
    private const float RoomMenuUnitsPerUnit = 1080f / 16.2f;

    /// <summary>
    /// The part of the room that her camera shows, around where she sits: her needle reaches about 1.7 units to her
    /// left and 1.5 to her right, her legs 1.7 down and her head 1.5 up, and the name hangs 1.5 above her. The name
    /// sets the width: a name that a player can connect with has up to 20 letters, which in capitals take about 0.4
    /// units each and in Chinese about 0.55, so up to 11 units on one line. It is drawn over the menu and past its
    /// left edge if need be, as it hangs over her in the room; the rest of the view is clear.
    /// </summary>
    private static readonly Rect View = new(-6f, -1.8f, 12f, 4.6f);

    /// <summary>
    /// How far below her middle she sits, in units of the room: the bottom of her cloak.
    /// </summary>
    private const float SeatBelowMiddle = 0.9f;

    /// <summary>
    /// How far from the left edge of the menu her middle is, in units of the menu. The player moved her one body
    /// further left than she was first drawn ("往左一个大黄蜂身位"), so the tip of her needle just passes the edge.
    /// </summary>
    private const float MiddleFromLeft = 57f;

    /// <summary>
    /// How high the edge of the menu that she sits on is, in units of the menu: its border.
    /// </summary>
    private const float SeatHeight = 6f;

    /// <summary>
    /// Where in the room she sits, far from anything that a camera of the menu shows.
    /// </summary>
    private static readonly Vector3 RigPosition = new(-20000f, -20000f, 0f);

    /// <summary>
    /// How far her outline reaches, in units of the room: as far on the page as it does in a fight.
    /// </summary>
    private const float Reach = FightOutline.Reach * RoomMenuUnitsPerUnit / MenuUnitsPerUnit;

    /// <summary>
    /// How long she takes to come into sight once she is loaded, in seconds, with her name and outline. She came all at
    /// once a moment after the page opened (USER 2026-10-22: "大黄蜂出现的有点突然，可以淡入这样子吗").
    /// </summary>
    private const float FadeInTime = 0.4f;

    /// <summary>
    /// The most time that one frame counts for in her fade, in seconds: that of a frame at 30 a second.
    /// </summary>
    private const float MaxFadeStep = 1f / 30f;

    /// <summary>
    /// The settings of this machine.
    /// </summary>
    private ModSettings _modSettings = null!;

    /// <summary>
    /// The name that this player goes by, as it stands in the menu.
    /// </summary>
    private Func<string> _username = null!;

    /// <summary>
    /// The image on the page that shows her.
    /// </summary>
    private RawImage _image = null!;

    /// <summary>
    /// The loading of the hero, while she is loaded or being loaded.
    /// </summary>
    private AsyncOperationHandle<GameObject> _hero;

    /// <summary>
    /// Whether loading the hero failed, so that it is not tried again and again.
    /// </summary>
    private bool _failed;

    /// <summary>
    /// What she sits in: her figure, her name and her camera, once she is loaded.
    /// </summary>
    private GameObject? _rig;

    /// <summary>
    /// What her camera draws her into.
    /// </summary>
    private RenderTexture? _texture;

    /// <summary>
    /// Her name.
    /// </summary>
    private TextMeshPro? _name;

    /// <summary>
    /// The name she shows.
    /// </summary>
    private string? _shownName;

    /// <summary>
    /// Her outline.
    /// </summary>
    private OwnFigureOutline? _outline;

    /// <summary>
    /// Make her place at the bottom left of the menu, hidden until the settings page opens.
    /// </summary>
    /// <param name="menu">The menu she sits on.</param>
    /// <param name="modSettings">The settings of this machine.</param>
    /// <param name="username">The name that this player goes by, as it stands in the menu.</param>
    /// <returns>Her place, which shows her while it is active.</returns>
    public static SittingPreview Create(RectTransform menu, ModSettings modSettings, Func<string> username) {
        var holder = new GameObject("SSMP Sitting Preview");
        holder.SetActive(false);

        var rect = holder.AddComponent<RectTransform>();
        rect.SetParent(menu, false);
        rect.anchorMin = rect.anchorMax = Vector2.zero;
        rect.pivot = Vector2.zero;
        rect.sizeDelta = new Vector2(View.width, View.height) * MenuUnitsPerUnit;
        // Her middle at its place from the left edge, and her seat on the border
        rect.anchoredPosition = new Vector2(
            MiddleFromLeft + View.x * MenuUnitsPerUnit,
            SeatHeight - (-View.y - SeatBelowMiddle) * MenuUnitsPerUnit
        );

        var image = holder.AddComponent<RawImage>();
        image.raycastTarget = false;
        image.enabled = false;

        var preview = holder.AddComponent<SittingPreview>();
        preview._modSettings = modSettings;
        preview._username = username;
        preview._image = image;
        return preview;
    }

    /// <summary>
    /// Show the choices as they stand now: the colour and weight of her name, and her outline.
    /// </summary>
    public void Refresh() {
        if (_name != null) {
            _name.color = Color.white;
            PlayerManager.DrawAsOwnName(_name, _modSettings);
        }

        if (_outline != null) {
            _outline.SetColour(PlayerManager.OwnNameColour(_modSettings));
            _outline.SetLit(_modSettings.OutlineOwnHeroInFights);
        }
    }

    private void OnEnable() {
        if (!_hero.IsValid() && !_failed) {
            _hero = Addressables.LoadAssetAsync<GameObject>(HeroAddress);
        }
    }

    private void OnDisable() {
        LetGo();
    }

    private void OnDestroy() {
        LetGo();
    }

    private void Update() {
        // A little more of her at each frame from the one after she sat down, by the clock of the screen rather than
        // the game's, which a pause stops. A long frame, like the one her pictures are first drawn in, brings no more
        // of her than a short one: in one long enough she would have come all at once again
        if (_rig != null && _image.color.a < 1f) {
            var colour = _image.color;
            colour.a = Mathf.MoveTowards(colour.a, 1f, Mathf.Min(Time.unscaledDeltaTime, MaxFadeStep) / FadeInTime);
            _image.color = colour;
        }

        if (_rig == null && _hero.IsValid() && _hero.IsDone) {
            if (_hero.Status == AsyncOperationStatus.Succeeded && _hero.Result != null) {
                SitDown(_hero.Result);
            } else {
                Logger.Warn("Could not load the hero to sit on the settings page");
                _failed = true;
                LetGo();
            }
        }

        if (_name != null) {
            var username = _username();
            if (username != _shownName) {
                ShowName(username);
            }
        }
    }

    /// <summary>
    /// Make her figure from the loaded hero, with her name, her outline and the camera that draws her, and have her sit
    /// down.
    /// </summary>
    /// <param name="hero">The hero as the game loads it, which is never put in the room itself.</param>
    private void SitDown(GameObject hero) {
        if (!hero.TryGetComponent<tk2dSprite>(out var heroSprite) ||
            !hero.TryGetComponent<tk2dSpriteAnimator>(out var heroAnimator)) {
            Logger.Warn("The hero has no sprite to sit on the settings page");
            _failed = true;
            LetGo();
            return;
        }

        _rig = new GameObject("SSMP Sitting Preview Rig");
        DontDestroyOnLoad(_rig);
        _rig.transform.position = RigPosition;

        // The sprite makes its mesh as it wakes, so it is given its picture after that, or the mesh that the picture
        // would be built in first is left behind
        var figure = new GameObject("Figure");
        figure.SetActive(false);
        figure.transform.SetParent(_rig.transform, false);
        figure.AddComponent<MeshFilter>();
        figure.AddComponent<MeshRenderer>();
        var sprite = figure.AddComponent<tk2dSprite>();
        var animator = figure.AddComponent<tk2dSpriteAnimator>();
        animator.Library = heroAnimator.Library;
        figure.SetActive(true);
        sprite.SetSprite(heroSprite.Collection, heroSprite.spriteId);

        animator.AnimationCompleted = (sitting, _) => sitting.Play(RestClips[Random.Range(0, RestClips.Length)]);
        animator.Play(SitClip);

        _outline = OwnFigureOutline.Attach(figure, PlayerManager.OwnNameColour(_modSettings), Reach);

        _name = PlayerManager.CreateUsername(_rig).GetComponent<TextMeshPro>();
        ShowName(_username());

        var scale = Screen.height / 1080f;
        _texture = new RenderTexture(
            Mathf.CeilToInt(View.width * MenuUnitsPerUnit * scale),
            Mathf.CeilToInt(View.height * MenuUnitsPerUnit * scale),
            0,
            RenderTextureFormat.ARGB32
        );

        var cameraObject = new GameObject("Camera");
        cameraObject.transform.SetParent(_rig.transform, false);
        cameraObject.transform.localPosition = new Vector3(View.center.x, View.center.y, -10f);
        var camera = cameraObject.AddComponent<Camera>();
        camera.orthographic = true;
        camera.orthographicSize = View.height / 2f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.clear;
        camera.cullingMask = 1 << figure.layer;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 20f;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.targetTexture = _texture;
        CloakColourPreview.Attach(camera, figure);

        _image.texture = _texture;
        _image.color = new Color(1f, 1f, 1f, 0f);
        _image.enabled = true;
        Refresh();

        // In the skin that the player wears: Customizer dresses only what a room finds as it loads, and she is loaded
        // after the menu
        OwnSkin.DressLoaded();
    }

    /// <summary>
    /// Put the given name over her head.
    /// </summary>
    /// <param name="username">The name.</param>
    private void ShowName(string username) {
        _shownName = username;
        if (_name == null) {
            return;
        }

        // Drawn as the name over the hero in the room is
        var drawn = username.ToUpper();
        _name.font = Resources.FontManager.PickInGameNameFont(drawn);
        _name.text = drawn;
        Refresh();
    }

    /// <summary>
    /// Let her go: her figure and everything that draws her, and the hero that the game loaded for her.
    /// </summary>
    private void LetGo() {
        if (_image != null) {
            _image.enabled = false;
            _image.texture = null;
        }

        if (_rig != null) {
            Destroy(_rig);
        }

        if (_texture != null) {
            _texture.Release();
            Destroy(_texture);
        }

        _rig = null;
        _texture = null;
        _name = null;
        _outline = null;
        _shownName = null;

        // A load that has not finished is let go of as it finishes, not halfway through
        if (_hero.IsValid()) {
            if (_hero.IsDone) {
                Addressables.Release(_hero);
            } else {
                _hero.Completed += loaded => Addressables.Release(loaded);
            }
        }

        _hero = default;
    }
}
