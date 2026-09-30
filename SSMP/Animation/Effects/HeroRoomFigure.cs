using System;
using System.Collections;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client;
using SSMP.Internals;
using TeamCherry.NestedFadeGroup;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Animation.Effects;

/// <summary>
/// A figure of the hero that the room shows in the hero's place. A bed does: a while after the hero sits down on it, it
/// hides the hero and plays lying down, lying there and sitting up again on a figure of the hero that belongs to the
/// bed, which lies where the bed put it; and so do a web that catches the hero, a game of dice that the hero kneels
/// at, and a scene or two. Only the hero's own animation reached the other players, so they saw the player go on
/// sitting while the player saw themselves lie down. While the room shows a player so, their character hides for the
/// other players (see <see cref="HeroHidden"/>), and a copy of the room's own figure plays there what it plays for the
/// player, in the same place: two players lying on one bed lie one over the other, where the bed lays down anyone,
/// rather than on the sides of it that they sit on (see <see cref="Game.Client.BenchCoop"/>), which on a bed seen from
/// its side would put one on top of the other. A figure that moves in the player's game stays where it started in the
/// copy.
/// </summary>
internal class HeroRoomFigure : AnimationEffect {
    /// <summary>
    /// The single instance, which <see cref="AnimationManager"/> plays for every player.
    /// </summary>
    public static readonly HeroRoomFigure Instance = new();

    /// <summary>
    /// How often a player whom the room shows says again what their figure plays, in seconds. It is said the way
    /// animations are, which may be lost on the way, and so may the word that the room shows them no more (see
    /// <see cref="ShownFor"/>); and a player who comes into the room meanwhile sees the figure from then on.
    /// </summary>
    internal const float SayAgainAfter = 0.5f;

    /// <summary>
    /// How long the copy of a figure stays after its player last said that the room shows them, in seconds: a word that
    /// it shows them no more that got lost on the way leaves it no longer than this.
    /// </summary>
    private const float ShownFor = 1.5f;

    /// <summary>
    /// How far behind the room's own figure its copy is drawn, in units, the way the characters of other players are
    /// drawn behind the local hero: where two players lie in one place, each sees their own figure in front. Less than
    /// what lies between a figure and what the room draws right behind it: the chair of the desk is drawn 0.0001 behind
    /// the figure that sits on it.
    /// </summary>
    private const float CopyBehind = 0.00001f;

    /// <summary>
    /// How long a word that the room shows a player no more keeps words that were said before it from showing the
    /// figure again, in seconds, for words that arrive out of order. Longer, and a figure of the same player made anew
    /// after the hero was, which counts its clips from nothing again, could be taken for an old one.
    /// </summary>
    private const float EndedFor = 2f;

    /// <summary>
    /// The most effect info that an animation carries, in bytes, which is counted in one byte.
    /// </summary>
    internal const int MostEffectInfo = byte.MaxValue;

    /// <summary>
    /// The first byte of the effect info that says what the room's figure of a player plays (see <see cref="Write"/>).
    /// </summary>
    private const byte Shown = 1;

    /// <summary>
    /// The first byte of the effect info that says that the room shows a player no more (see
    /// <see cref="WriteNotShown"/>).
    /// </summary>
    private const byte NotShown = 0;

    /// <summary>
    /// What watches the local hero, which the hooks tell about a figure of it and what the figure plays.
    /// </summary>
    private static HeroRoomFigureWatcher? _watcher;

    /// <summary>
    /// The hook of the action that switches a sprite on or off, by which the room hides the hero and shows its own
    /// figure, which stays for as long as the game runs.
    /// </summary>
    private static Hook? _showHook;

    /// <summary>
    /// The hook of the method that every way of playing a clip ends in, which stays for as long as the game runs.
    /// </summary>
    private static Hook? _playHook;

    /// <summary>
    /// The hook of the routine that seats the hero at the desk that shows the collection, which hides the hero from code
    /// rather than from an FSM and shows a figure of its own, and stays for as long as the game runs.
    /// </summary>
    private static Hook? _deskHook;

    /// <summary>
    /// The hook of the routine of the lift that carries the hero up or down its shaft, which hides the hero from code
    /// on the way, and stays for as long as the game runs.
    /// </summary>
    private static Hook? _liftHook;

    /// <summary>
    /// Whether the routines that hide the hero from code were hooked, or tried to be: a routine that is not there is
    /// looked for once.
    /// </summary>
    private static bool _routinesHooked;

    /// <summary>
    /// The figure of the hero sitting at the desk that shows the collection.
    /// </summary>
    private static readonly FieldInfo? DeskFigureField = typeof(CollectionViewerDesk).GetField(
        "sitDownHornet",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
    );

    /// <summary>
    /// Starts watching whether the room shows the local hero by a figure of its own, which may be a new hero or the same
    /// one again. Like the other watches of the hero, this can run while the hero is still being made (see
    /// <see cref="HeroChildEffects.Watch"/>).
    /// </summary>
    /// <param name="hero">The local hero.</param>
    /// <param name="send">Sends the effect info of a change to the other players.</param>
    public static void Watch(HeroController hero, Action<byte[]> send) {
        if (!hero.gameObject.TryGetComponent<HeroRoomFigureWatcher>(out var watcher)) {
            watcher = hero.gameObject.AddComponent<HeroRoomFigureWatcher>();
        }

        watcher.StartOver(send);
        _watcher = watcher;

        if (_showHook == null) {
            var enter = typeof(SetMeshRenderer).GetMethod("OnEnter", BindingFlags.Instance | BindingFlags.Public);
            if (enter == null) {
                Logger.Error("Could not find SetMeshRenderer#OnEnter; figures of the hero in rooms will not show");
            } else {
                _showHook = new Hook(
                    enter,
                    new Action<Action<SetMeshRenderer>, SetMeshRenderer>(OnSetMeshRendererEnter)
                );
            }
        }

        if (!_routinesHooked) {
            _routinesHooked = true;
            _deskHook = HookRoutine(
                typeof(CollectionViewerDesk),
                "SitDownSequence",
                new Func<Func<CollectionViewerDesk, int, IEnumerator>, CollectionViewerDesk, int, IEnumerator>(
                    OnDeskSitDown
                )
            );
            _liftHook = HookRoutine(
                typeof(WeaverLift),
                "TeleportRoutine",
                new Func<
                    Func<WeaverLift, WeaverLift, NestedFadeGroupBase, IEnumerator>,
                    WeaverLift,
                    WeaverLift,
                    NestedFadeGroupBase,
                    IEnumerator
                >(OnLiftTeleport)
            );
        }

        if (_playHook != null) {
            return;
        }

        var play = typeof(tk2dSpriteAnimator).GetMethod(
            "Play",
            BindingFlags.Instance | BindingFlags.Public,
            null,
            [typeof(tk2dSpriteAnimationClip), typeof(float), typeof(float)],
            null
        );
        if (play == null) {
            Logger.Error("Could not find the method that plays a clip; figures of the hero in rooms will not animate");
            return;
        }

        _playHook = new Hook(play, OnPlay);
    }

    /// <summary>
    /// Switches a sprite on or off. When an FSM of the room, or of a creature, switches the sprite of the local hero off,
    /// the hero is hidden by it (see <see cref="HeroHidden"/>); and when it switches on a figure of its own in a state
    /// that hides the hero, the other players are told about the figure. The hero's own FSMs hide it for moves that
    /// show it by effects of their own, which reach the other players otherwise.
    /// </summary>
    private static void OnSetMeshRendererEnter(Action<SetMeshRenderer> orig, SetMeshRenderer self) {
        orig(self);

        var watcher = _watcher;
        var fsm = self.Fsm;
        if (watcher == null || fsm == null || self.active == null) {
            return;
        }

        var hero = watcher.gameObject;
        var target = fsm.GetOwnerDefaultTarget(self.gameObject);
        if (target == null) {
            return;
        }

        if (!self.active.Value) {
            if (target == hero && !IsTheHerosOwn(fsm, hero)) {
                HeroHidden.HiddenByTheRoom();
            }

            return;
        }

        // A creature shows the hero it holds itself, on its copy in the other players' games too
        if (target != hero && !IsTheHerosOwn(fsm, hero) && HidesTheHero(self.State, fsm, hero) &&
            !IsPartOfACreature(target)) {
            watcher.Show(target);
        }
    }

    /// <summary>
    /// Hooks a routine of the game that hides the hero from code.
    /// </summary>
    private static Hook? HookRoutine(Type type, string name, Delegate detour) {
        var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (method == null) {
            Logger.Error($"Could not find {type.Name}#{name}; the hero hidden by it will show to other players");
            return null;
        }

        try {
            return new Hook(method, detour);
        } catch (Exception e) {
            Logger.Error($"Could not hook {type.Name}#{name}; the hero hidden by it will show to other players: {e}");
            return null;
        }
    }

    /// <summary>
    /// Seats the hero at the desk that shows the collection, which hides the hero and shows the desk's figure of them.
    /// </summary>
    private static IEnumerator OnDeskSitDown(
        Func<CollectionViewerDesk, int, IEnumerator> orig,
        CollectionViewerDesk self,
        int constructIndex
    ) {
        var figure = DeskFigureField?.GetValue(self) as tk2dSpriteAnimator;
        return WhileHiding(orig(self, constructIndex), figure != null ? figure.gameObject : null);
    }

    /// <summary>
    /// Carries the hero from one lift to another, which hides the hero on the way.
    /// </summary>
    private static IEnumerator OnLiftTeleport(
        Func<WeaverLift, WeaverLift, NestedFadeGroupBase, IEnumerator> orig,
        WeaverLift self,
        WeaverLift target,
        NestedFadeGroupBase shaftGlows
    ) {
        return WhileHiding(orig(self, target, shaftGlows), null);
    }

    /// <summary>
    /// Runs a routine that hides the hero from code, step by step as the game would, and after each step tells whether
    /// the hero is hidden: then the room hid it (see <see cref="HeroHidden"/>), and a figure that the routine shows in
    /// its place goes to the other players.
    /// </summary>
    /// <param name="routine">The routine.</param>
    /// <param name="figure">The figure of the hero that the routine shows, or null if it shows none.</param>
    private static IEnumerator WhileHiding(IEnumerator routine, GameObject? figure) {
        while (true) {
            var more = routine.MoveNext();

            var watcher = _watcher;
            if (watcher != null && watcher.TryGetComponent<MeshRenderer>(out var body) && !body.enabled) {
                HeroHidden.HiddenByTheRoom();
                if (figure != null && figure.activeInHierarchy) {
                    watcher.Show(figure);
                }
            }

            if (!more) {
                yield break;
            }

            yield return routine.Current;
        }
    }

    /// <summary>
    /// Whether a state of an FSM switches the sprite of the local hero off: then the sprite that it switches on is the
    /// figure it shows in the hero's place.
    /// </summary>
    private static bool HidesTheHero(FsmState? state, HutongGames.PlayMaker.Fsm fsm, GameObject hero) {
        if (state?.Actions == null) {
            return false;
        }

        foreach (var action in state.Actions) {
            if (action is SetMeshRenderer { Enabled: true, active.Value: false } hide &&
                fsm.GetOwnerDefaultTarget(hide.gameObject) == hero) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an FSM is the hero's own, or one of its parts'.
    /// </summary>
    private static bool IsTheHerosOwn(HutongGames.PlayMaker.Fsm fsm, GameObject hero) {
        var owner = fsm.GameObject;
        return owner != null && owner.transform.IsChildOf(hero.transform);
    }

    /// <summary>
    /// Whether an object belongs to a creature, which has health.
    /// </summary>
    private static bool IsPartOfACreature(GameObject gameObject) {
        return gameObject.GetComponentInParent<HealthManager>(true) != null;
    }

    /// <summary>
    /// The method that every way of playing a clip ends in.
    /// </summary>
    private delegate void PlayMethod(
        tk2dSpriteAnimator self,
        tk2dSpriteAnimationClip clip,
        float clipStartTime,
        float overrideFps
    );

    /// <summary>
    /// Plays a clip, and tells the other players when it is the figure of the local hero that plays it.
    /// </summary>
    private static void OnPlay(
        PlayMethod orig,
        tk2dSpriteAnimator self,
        tk2dSpriteAnimationClip clip,
        float clipStartTime,
        float overrideFps
    ) {
        orig(self, clip, clipStartTime, overrideFps);

        _watcher?.Played(self);
    }

    /// <summary>
    /// Writes what the room's figure of the local hero plays to effect info: how many clips it started so far, so that
    /// a clip is started once however often it is said, where the figure is in the room, and the clip it plays, from
    /// where it is in it now and how fast.
    /// </summary>
    /// <param name="plays">How many clips the figure started so far, counted round.</param>
    /// <param name="path">The path of the figure in its room.</param>
    /// <param name="figure">The animator of the figure.</param>
    internal static byte[] Write(byte plays, string path, tk2dSpriteAnimator figure) {
        var clip = figure.CurrentClip;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Shown);
        writer.Write(plays);
        writer.Write(path);
        writer.Write(clip != null ? clip.name : "");
        writer.Write(clip != null ? figure.ClipTimeSeconds : 0f);
        writer.Write(figure.ClipFps);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Says often for a while what the room's figure of the local hero plays, once the local player or another one came
    /// into the room (see <see cref="HeroHidden.SayAgainSoon"/>).
    /// </summary>
    internal static void SayAgainSoon() {
        _watcher?.SayAgainSoon();
    }

    /// <summary>
    /// Writes to effect info that the room shows the local hero no more, with how many clips its figure had started, so
    /// that what was said of the figure before, arriving after this, does not show it again.
    /// </summary>
    /// <param name="plays">How many clips the figure started, counted round.</param>
    internal static byte[] WriteNotShown(byte plays) {
        return [NotShown, plays];
    }

    /// <inheritdoc/>
    public override byte[]? GetEffectInfo() {
        return null;
    }

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        if (effectInfo is not { Length: > 0 }) {
            return;
        }

        if (!playerObject.TryGetComponent<RoomFigureCopy>(out var copy)) {
            if (effectInfo[0] != Shown) {
                return;
            }

            copy = playerObject.AddComponent<RoomFigureCopy>();
        }

        if (effectInfo[0] != Shown) {
            copy.End(effectInfo.Length > 1 ? effectInfo[1] : null, EndedFor);
            return;
        }

        byte plays;
        string path;
        string clipName;
        float clipTime;
        float fps;
        try {
            using var reader = new BinaryReader(new MemoryStream(effectInfo, 1, effectInfo.Length - 1));
            plays = reader.ReadByte();
            path = reader.ReadString();
            clipName = reader.ReadString();
            clipTime = reader.ReadSingle();
            fps = reader.ReadSingle();
        } catch (IOException) {
            return;
        }

        copy.Show(plays, path, clipName, clipTime, fps, ShownFor);
    }

    /// <summary>
    /// Whether the room shows another player by a figure of its own, as far as that player said, so that their
    /// character hides.
    /// </summary>
    /// <param name="playerObject">The character of the other player.</param>
    public static bool IsShown(GameObject playerObject) {
        return playerObject.TryGetComponent<RoomFigureCopy>(out var copy) && copy.IsShown;
    }

    /// <summary>
    /// Forgets the figure of another player, for a character that leaves the room: it may come back, or be used for
    /// another player, long after the word that would have shown it again.
    /// </summary>
    /// <param name="playerObject">The character of the other player.</param>
    public static void Reset(GameObject playerObject) {
        if (playerObject.TryGetComponent<RoomFigureCopy>(out var copy)) {
            copy.Hide();
        }
    }

    /// <summary>
    /// Copies the room's own figure of the hero at a path for another player, in the same place in the room and drawn
    /// just behind it, without what plays the figure for the local player: its FSM, and what tells it about the local
    /// hero.
    /// </summary>
    /// <param name="path">The path of the figure in its room.</param>
    /// <returns>The copy, or null if the room has no such figure.</returns>
    internal static GameObject? MakeCopy(string path) {
        // A figure in a part of the room that is switched off here would make a copy that never shows. The figure
        // itself may be off, as the room switches it on only for the player it shows.
        var original = ScenePath.Find(path);
        var parent = original != null ? original.transform.parent : null;
        if (original == null || parent != null && !parent.gameObject.activeInHierarchy ||
            !original.TryGetComponent<tk2dSpriteAnimator>(out _)) {
            return null;
        }

        // Made inside a holder that is switched off, so that nothing on the copy starts before what plays the figure
        // for the local player is gone from it
        var holder = new GameObject("Room Figure Holder");
        holder.SetActive(false);

        var copy = Object.Instantiate(original, holder.transform, false);
        copy.name = original.name + " (Other Player)";

        // Other renderers go last, as what they draw for depends on them: particles on their renderer
        var components = copy.GetComponentsInChildren<Component>(true);
        foreach (var lastOnes in new[] { false, true }) {
            for (var i = components.Length - 1; i >= 0; i--) {
                var component = components[i];
                if (component != null && component is Renderer == lastOnes &&
                    component is not (Transform or MeshFilter or MeshRenderer or tk2dBaseSprite or tk2dSpriteAnimator)) {
                    Object.DestroyImmediate(component);
                }
            }
        }

        var place = copy.transform;
        var originalPlace = original.transform;
        place.SetParent(originalPlace.parent, false);
        place.localPosition = originalPlace.localPosition + new Vector3(0f, 0f, CopyBehind);
        place.localRotation = originalPlace.localRotation;
        place.localScale = originalPlace.localScale;
        Object.Destroy(holder);

        copy.SetActive(true);
        if (copy.TryGetComponent<MeshRenderer>(out var body)) {
            body.enabled = true;
        }

        return copy;
    }
}

/// <summary>
/// Watches whether the room shows the local hero by a figure of its own, for <see cref="HeroRoomFigure"/>, and tells the
/// other players what the figure plays when that changes, and again every so often while it lasts.
/// </summary>
internal class HeroRoomFigureWatcher : MonoBehaviour {
    /// <summary>
    /// Sends the effect info of a change to the other players.
    /// </summary>
    [NonSerialized]
    private Action<byte[]>? _send;

    /// <summary>
    /// The hero's own sprite, which the room hides while it shows its figure.
    /// </summary>
    [NonSerialized]
    private MeshRenderer? _body;

    /// <summary>
    /// The animator of the figure that the room shows in the hero's place, or null while it shows none.
    /// </summary>
    [NonSerialized]
    private tk2dSpriteAnimator? _figure;

    /// <summary>
    /// The sprite of that figure.
    /// </summary>
    [NonSerialized]
    private MeshRenderer? _figureBody;

    /// <summary>
    /// The path of that figure in its room.
    /// </summary>
    [NonSerialized]
    private string _path = "";

    /// <summary>
    /// How many clips the figure started, counted round, so that the other players start each once.
    /// </summary>
    [NonSerialized]
    private byte _plays;

    /// <summary>
    /// Whether the figure started a clip, or was shown, since the other players were last told.
    /// </summary>
    [NonSerialized]
    private bool _changed;

    /// <summary>
    /// Whether the other players were last told that the room shows the hero.
    /// </summary>
    [NonSerialized]
    private bool _shown;

    /// <summary>
    /// When the other players are told again what the figure plays, in unscaled time.
    /// </summary>
    [NonSerialized]
    private float _sayAgainAt;

    /// <summary>
    /// Until when the other players are told often (see <see cref="HeroHidden.SayAgainSoon"/>), in unscaled time.
    /// </summary>
    [NonSerialized]
    private float _sayOftenUntil;

    /// <summary>
    /// Starts watching again, telling the other players with the given callback, for a hero that may be shown by a
    /// figure already: a player who connects again while lying in a bed is said to lie there at once, and a figure
    /// that is gone meanwhile is found gone at the next look.
    /// </summary>
    public void StartOver(Action<byte[]> send) {
        _send = send;
        _shown = false;
        _changed = _figure != null;
    }

    /// <summary>
    /// Tells the other players often for a while (see <see cref="HeroHidden.SayAgainSoon"/>), starting now.
    /// </summary>
    public void SayAgainSoon() {
        _sayOftenUntil = Time.unscaledTime + HeroHidden.SayOftenFor;
        _changed = _figure != null;
    }

    /// <summary>
    /// Takes a figure that the room just switched on in place of the hero, unless it is the one that shows already.
    /// </summary>
    public void Show(GameObject figure) {
        if (_send == null || !figure.TryGetComponent<tk2dSpriteAnimator>(out var animator) ||
            !figure.TryGetComponent<MeshRenderer>(out var body) || ReferenceEquals(animator, _figure)) {
            return;
        }

        _figure = animator;
        _figureBody = body;
        _path = ScenePath.Get(figure.transform);
        _plays++;
        _changed = true;
    }

    /// <summary>
    /// Notes that an animator started a clip, which matters when it is the figure's.
    /// </summary>
    public void Played(tk2dSpriteAnimator animator) {
        if (_figure is not null && ReferenceEquals(animator, _figure)) {
            _plays++;
            _changed = true;
        }
    }

    private void LateUpdate() {
        if (_send == null) {
            return;
        }

        if (_body == null) {
            _body = GetComponent<MeshRenderer>();
        }

        // The figure is gone with its room, or the room showed the hero again, or hid the figure
        var shown = _figure != null && _figureBody != null && _figureBody.enabled &&
                    _figure.gameObject.activeInHierarchy && (_body == null || !_body.enabled);
        if (!shown) {
            _figure = null;
            if (_shown) {
                _shown = false;
                _send(HeroRoomFigure.WriteNotShown(_plays));
            }

            return;
        }

        if (!_changed && Time.unscaledTime < _sayAgainAt) {
            return;
        }

        var effectInfo = HeroRoomFigure.Write(_plays, _path, _figure!);
        if (effectInfo.Length > HeroRoomFigure.MostEffectInfo) {
            Logger.Warn($"The figure '{_path}' has too long a path to show to other players");
            _figure = null;
            return;
        }

        _shown = true;
        _changed = false;
        _sayAgainAt = Time.unscaledTime + (Time.unscaledTime < _sayOftenUntil
            ? HeroHidden.SayOftenEvery
            : HeroRoomFigure.SayAgainAfter);
        _send(effectInfo);
    }
}

/// <summary>
/// The copy of the room's figure of another player, for <see cref="HeroRoomFigure"/>, which plays what the figure plays
/// in that player's game while the room shows them, and goes when they have not said for a while that it still does.
/// </summary>
internal class RoomFigureCopy : MonoBehaviour {
    /// <summary>
    /// The copy of the figure, or null while there is none.
    /// </summary>
    [NonSerialized]
    private GameObject? _copy;

    /// <summary>
    /// The animator of the copy.
    /// </summary>
    [NonSerialized]
    private tk2dSpriteAnimator? _animator;

    /// <summary>
    /// The path of the figure that the copy was made from.
    /// </summary>
    [NonSerialized]
    private string _path = "";

    /// <summary>
    /// How many clips the player's figure had started when the copy last started one (see
    /// <see cref="HeroRoomFigureWatcher"/>).
    /// </summary>
    [NonSerialized]
    private byte _plays;

    /// <summary>
    /// Until when the copy stays, in unscaled time, or 0 while there is none.
    /// </summary>
    [NonSerialized]
    private float _shownUntil;

    /// <summary>
    /// How many clips the player's figure had started when the player last said that the room shows them no more.
    /// </summary>
    [NonSerialized]
    private byte _endedPlays;

    /// <summary>
    /// Until when words about the figure that were said before <see cref="_endedPlays"/> are taken for late ones and
    /// left alone, in unscaled time.
    /// </summary>
    [NonSerialized]
    private float _endedUntil;

    /// <summary>
    /// Whether the copy shows now.
    /// </summary>
    public bool IsShown => _copy != null && _shownUntil > Time.unscaledTime;

    /// <summary>
    /// Shows the copy of the figure at a path, made now if there is none of it yet, and plays on it the clip that the
    /// player's figure plays when the player's figure started one since.
    /// </summary>
    /// <param name="plays">How many clips the player's figure started so far.</param>
    /// <param name="path">The path of the figure in its room.</param>
    /// <param name="clipName">The name of the clip that it plays.</param>
    /// <param name="clipTime">How far into the clip it is, in seconds.</param>
    /// <param name="fps">How fast it plays the clip.</param>
    /// <param name="shownFor">How long the copy stays unless the player says again that the room shows them.</param>
    public void Show(byte plays, string path, string clipName, float clipTime, float fps, float shownFor) {
        // Said before the player said that the room shows them no more, and only arriving now
        if (Time.unscaledTime < _endedUntil && (sbyte) (plays - _endedPlays) <= 0) {
            return;
        }

        var made = false;
        if (_copy == null || _path != path) {
            RemoveCopy();

            _copy = HeroRoomFigure.MakeCopy(path);
            if (_copy == null) {
                Hide();
                return;
            }

            _animator = _copy.GetComponent<tk2dSpriteAnimator>();
            _path = path;
            made = true;
        }

        // Said again while the same clip goes on, it is not started again; a clip started since that was lost on the
        // way starts from where the player's figure is in it now
        if (made || (sbyte) (plays - _plays) > 0) {
            _plays = plays;

            var clip = _animator != null && clipName.Length > 0 ? _animator.GetClipByName(clipName) : null;
            if (clip != null) {
                // A clip that plays once stays on its last frame after its end, where a time past the end would show
                // its first frame until the next update
                var clipFps = fps > 0f ? fps : clip.fps;
                if (clip.wrapMode == tk2dSpriteAnimationClip.WrapMode.Once && clipFps > 0f && clip.frames.Length > 0) {
                    clipTime = Mathf.Min(clipTime, (clip.frames.Length - 0.01f) / clipFps);
                }

                _animator!.Play(clip, clipTime, fps);
            }
        }

        _shownUntil = Time.unscaledTime + shownFor;
        HeroChildEffects.ShowBodyUnlessHidden(gameObject);
    }

    /// <summary>
    /// Removes the copy for a player who said that the room shows them no more, and leaves alone what they said of the
    /// figure before that, should it arrive later.
    /// </summary>
    /// <param name="plays">How many clips their figure had started by then, or null if they did not say.</param>
    /// <param name="endedFor">How long what they said before is left alone, in seconds.</param>
    public void End(byte? plays, float endedFor) {
        if (plays is { } ended) {
            _endedPlays = ended;
            _endedUntil = Time.unscaledTime + endedFor;
        }

        Hide();
    }

    /// <summary>
    /// Removes the copy and shows the character of the player again.
    /// </summary>
    public void Hide() {
        _shownUntil = 0f;
        RemoveCopy();
        HeroChildEffects.ShowBodyUnlessHidden(gameObject);
    }

    /// <summary>
    /// Removes the copy, if it was not gone with its room already.
    /// </summary>
    private void RemoveCopy() {
        if (_copy != null) {
            Destroy(_copy);
        }

        _copy = null;
        _animator = null;
    }

    private void Update() {
        if (_shownUntil > 0f && (_copy == null || Time.unscaledTime >= _shownUntil)) {
            Hide();
        }
    }

    private void OnDestroy() {
        RemoveCopy();
    }
}
