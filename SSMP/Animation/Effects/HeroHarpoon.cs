using System;
using System.IO;
using HutongGames.PlayMaker.Actions;
using SSMP.Internals;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Animation.Effects;

/// <summary>
/// The needle that the hero throws with the harpoon, the thread behind it, the thread drawn to a ring, and the spark of
/// a needle that strikes a wall (USER 10-25: "那个飞针的特效，我这边看队友的样子好像简陋一点"). None of these is the
/// hero's own animation, nor a part that the game only switches on and off where it is (see
/// <see cref="HeroChildEffects"/>): the game puts the needle where the throw ends and leaves it there while the hero
/// dashes to it, plays clips of its own on it and starts its thread, and takes the thread to a ring and the spark from
/// its pool, so the other players saw the hero throw nothing. What the local needle shows is told with where it is,
/// and said again while it shows and for a moment after it went, as animation updates can be lost; a thread to a ring
/// is told as it starts, and said again for a moment. The other players' games show a copy of the needle on that
/// player's character at that place, play the same clips on it, start the same threads and raise the same spark,
/// without its sound or the noise that creatures hear, which are for the player whose needle it is.
/// </summary>
internal class HeroHarpoon : AnimationEffect {
    /// <summary>
    /// The single instance, which <see cref="AnimationManager"/> plays for every player.
    /// </summary>
    public static readonly HeroHarpoon Instance = new();

    /// <summary>
    /// What a message is about, as it goes over the network: new kinds go at the end.
    /// </summary>
    private enum Kind : byte {
        Needle,
        RingThread,

        // What the ring that the needle struck shows of the hold (see HarpoonRingLook)
        RingLook
    }

    /// <summary>
    /// The name of the hero's harpoon FSM.
    /// </summary>
    private const string FsmName = "Harpoon Dash";

    /// <summary>
    /// The path of the needle under the hero until its first throw. The game puts it back under the hero itself after
    /// each throw, where it is looked for next.
    /// </summary>
    private const string NeedlePath = "Effects/Harpoon Needle";

    /// <summary>
    /// The name of the needle, which its copy has too.
    /// </summary>
    private const string NeedleName = "Harpoon Needle";

    /// <summary>
    /// The variable of the harpoon FSM that holds the needle, wherever the throw left it.
    /// </summary>
    private const string NeedleVariable = "Needle";

    /// <summary>
    /// The name of the needle's thread, under it.
    /// </summary>
    private const string ThreadName = "Thread";

    /// <summary>
    /// The state of the harpoon FSM that throws the thread to a ring.
    /// </summary>
    private const string RingThrowState = "Ring Throw";

    /// <summary>
    /// The variable of the harpoon FSM that holds the thread to a ring.
    /// </summary>
    private const string RingThreadVariable = "Harpoon Thread Ring";

    /// <summary>
    /// The state of the harpoon FSM that takes the thread to a ring from the pool.
    /// </summary>
    private const string RingThreadSpawnState = "Thread?";

    /// <summary>
    /// The state of the harpoon FSM where the needle strikes a wall, which takes the spark from the pool.
    /// </summary>
    private const string WallHitState = "Hit Wall";

    /// <summary>
    /// The clip that the needle plays as it strikes a wall.
    /// </summary>
    private const string WallHitClip = "Harpoon Needle Wall Hit";

    /// <summary>
    /// How often what the needle shows is said again while it shows, in seconds.
    /// </summary>
    private const float SaidEvery = 0.25f;

    /// <summary>
    /// For how long it is said again that the needle went, in seconds.
    /// </summary>
    private const float GoneSaidFor = 1f;

    /// <summary>
    /// For how long a thread to a ring is said again after it started, in seconds. A word of it that comes later starts
    /// none, as the thread is drawn and gone by then.
    /// </summary>
    private const float RingSaidFor = 0.5f;

    /// <summary>
    /// How far into the clip of a needle striking a wall a word of it may come and still raise the spark, in seconds.
    /// </summary>
    private const float SparkLateBy = 0.3f;

    /// <summary>
    /// How long a spark or a thread to a ring raised for another player stays at most, in seconds.
    /// </summary>
    private const float CopyLasts = 2f;

    /// <summary>
    /// How far the needle has to move, within the hero or the room, to be told again at once.
    /// </summary>
    private const float Moved = 0.01f;

    /// <summary>
    /// How long a copy of another player's needle shows without a word that it shows, in seconds. Their game says it
    /// again every <see cref="SaidEvery"/> while it shows, so a copy that hears nothing for longer went without the word
    /// of it getting here, or was shown by a word that came late.
    /// </summary>
    internal const float ShownUnheardFor = 1f;

    /// <summary>
    /// How often to look for a local needle that could not be found, in seconds.
    /// </summary>
    private const float LookAgainEvery = 0.5f;

    /// <summary>
    /// Sends the effect info of the needle to the other players, or null before the local hero is watched.
    /// </summary>
    private static Action<byte[]>? _send;

    /// <summary>
    /// Whether watching the local needle threw, which is logged once and stops it.
    /// </summary>
    private static bool _failed;

    /// <summary>
    /// When to look again for a local needle that could not be found, in unscaled time.
    /// </summary>
    private static float _lookAgainAt;

    /// <summary>
    /// Whether showing another player's hold of a ring threw, which is logged once.
    /// </summary>
    private static bool _ringLookFailed;

    /// <summary>
    /// The local hero, its needle, the needle's thread and the harpoon FSM.
    /// </summary>
    private static Transform? _hero;
    private static Transform? _needle;
    private static GameObject? _thread;
    private static PlayMakerFSM? _fsm;

    /// <summary>
    /// The animator of the local needle, whose clips <see cref="HeroPartClips"/> tells this.
    /// </summary>
    internal static tk2dSpriteAnimator? LocalAnimator { get; private set; }

    /// <summary>
    /// What the local needle played last: counted round, so that playing the same clip again counts, its clip, where in
    /// it the clip started and at what speed, and when, in game time.
    /// </summary>
    private static byte _playId;
    private static string _clip = "";
    private static float _clipStart;
    private static float _fps;
    private static float _playedAt;

    /// <summary>
    /// How many times the local needle's thread started, counted round, and whether it was on in the last frame.
    /// </summary>
    private static byte _threadId;
    private static bool _threadWasOn;

    /// <summary>
    /// What was said of the local needle last.
    /// </summary>
    private static bool _saidOn;
    private static bool _saidAttached;
    private static Vector3 _saidPosition;
    private static Vector2 _saidScale;
    private static float _saidAngle;
    private static byte _saidPlayId;
    private static byte _saidThreadId;

    /// <summary>
    /// When to say it again, and until when it is said that it went, in unscaled time.
    /// </summary>
    private static float _sayAt;
    private static float _goneSaidUntil;

    /// <summary>
    /// The local thread to a ring: whether the harpoon FSM threw one in the last frame, how many were thrown, counted
    /// round, where the last one is and how it is turned, when it started and when to say it again, and until when it
    /// is said, in unscaled time.
    /// </summary>
    private static bool _wasRingThrow;
    private static byte _ringId;
    private static Vector3 _ringPosition;
    private static float _ringAngle;
    private static float _ringStartedAt;
    private static float _ringSayAt;
    private static float _ringSaidUntil;

    /// <summary>
    /// The game's own spark of a needle striking a wall and thread to a ring, from the local hero's harpoon FSM.
    /// </summary>
    private static GameObject? _sparkPrefab;
    private static GameObject? _ringThreadPrefab;

    /// <summary>
    /// Starts watching the local hero's needle, which may be a new hero or the same one again. This runs while the hero
    /// may still be being made, like <see cref="HeroChildEffects.Watch"/>.
    /// </summary>
    /// <param name="hero">The local hero.</param>
    /// <param name="send">Sends the effect info of the needle to the other players.</param>
    public static void Watch(HeroController hero, Action<byte[]> send) {
        _send = send;

        _hero = hero.gameObject.transform;
        _fsm = null;
        foreach (var fsm in hero.GetComponents<PlayMakerFSM>()) {
            if (fsm.FsmName == FsmName) {
                _fsm = fsm;
                break;
            }
        }

        if (_fsm == null) {
            Logger.Warn($"The hero has no '{FsmName}' FSM, so its threads to rings don't show to other players");
        }

        _needle = null;
        _thread = null;
        LocalAnimator = null;
        _lookAgainAt = 0f;
        if (!FindNeedle()) {
            // Out in the room as this connected, or not made yet: it is looked for again while watching
            Logger.Info("The hero's harpoon needle isn't under the hero now; it is looked for again");
        } else if (_thread == null || LocalAnimator == null) {
            Logger.Warn("The hero's harpoon needle has no thread or no animator to show to other players");
        }

        HarpoonRingLook.Forget();
        _saidOn = false;
        _threadWasOn = false;
        _goneSaidUntil = 0f;
        _wasRingThrow = false;
        _ringSaidUntil = 0f;

        if (!hero.TryGetComponent<HeroHarpoonWatcher>(out _)) {
            hero.gameObject.AddComponent<HeroHarpoonWatcher>();
        }
    }

    /// <summary>
    /// Finds the local needle, its thread and its animator when they aren't known yet: under the hero, where the game
    /// keeps the needle until a throw lets go of it and puts it back after, or else where the harpoon FSM holds it.
    /// </summary>
    /// <returns>Whether the needle is known.</returns>
    private static bool FindNeedle() {
        if (_needle != null) {
            return true;
        }

        var hero = _hero;
        if (hero == null) {
            return false;
        }

        var needle = hero.Find(NeedlePath);
        if (needle == null) {
            needle = hero.Find(NeedleName);
        }

        if (needle == null && _fsm != null) {
            var held = _fsm.FsmVariables.GetFsmGameObject(NeedleVariable)?.Value;
            if (held != null) {
                needle = held.transform;
            }
        }

        if (needle == null) {
            return false;
        }

        _needle = needle;
        var thread = needle.Find(ThreadName);
        _thread = thread != null ? thread.gameObject : null;
        LocalAnimator = needle.GetComponent<tk2dSpriteAnimator>();
        return true;
    }

    /// <summary>
    /// Whether a transform is the local hero's needle, which the game lets go of into the room where a throw ends: what
    /// strikes from it there is the local player's own.
    /// </summary>
    internal static bool IsLocalNeedle(Transform transform) {
        return _needle != null && transform == _needle;
    }

    /// <summary>
    /// Notes a clip that the local needle plays, which goes with the next word of it.
    /// </summary>
    internal static void OnLocalClip(tk2dSpriteAnimationClip clip, float clipStartTime, float overrideFps) {
        _playId++;
        _clip = clip.name ?? "";
        _clipStart = clipStartTime;
        _fps = overrideFps;
        _playedAt = Time.time;
    }

    /// <summary>
    /// Tells the other players what the local needle and the thread to a ring show, once a frame.
    /// </summary>
    internal static void WatchLocal() {
        if (_send == null || _failed || _hero == null) {
            return;
        }

        try {
            WatchNeedle();
            WatchRingThread();
            if (_fsm != null) {
                HarpoonRingLook.WatchLocal(_fsm, body => _send!(WithKind(Kind.RingLook, body)));
            }
        } catch (Exception e) {
            _failed = true;
            Logger.Error($"Watching the harpoon's needle to show it to other players threw, so it stops:\n{e}");
        }
    }

    /// <summary>
    /// Tells the other players what the local needle shows, when it changed or when it is time to say it again.
    /// </summary>
    private static void WatchNeedle() {
        if (_needle == null) {
            var at = Time.unscaledTime;
            if (at < _lookAgainAt) {
                return;
            }

            _lookAgainAt = at + LookAgainEvery;
            if (!FindNeedle()) {
                return;
            }
        }

        var on = _needle!.gameObject.activeInHierarchy;
        var threadOn = on && _thread != null && _thread.activeSelf;
        if (threadOn && !_threadWasOn) {
            _threadId++;
        }

        _threadWasOn = threadOn;

        var attached = false;
        var position = Vector3.zero;
        var scale = Vector2.one;
        var angle = 0f;
        if (on) {
            ReadPlace(out attached, out position, out scale, out angle);
        }

        var now = Time.unscaledTime;
        var changed = on != _saidOn || _playId != _saidPlayId || _threadId != _saidThreadId ||
                      on && (attached != _saidAttached ||
                             (position - _saidPosition).sqrMagnitude > Moved * Moved ||
                             (scale - _saidScale).sqrMagnitude > Moved * Moved ||
                             Mathf.Abs(Mathf.DeltaAngle(angle, _saidAngle)) > 1f);
        if (!on && _saidOn) {
            _goneSaidUntil = now + GoneSaidFor;
        }

        if (!changed && (now < _sayAt || !on && now >= _goneSaidUntil)) {
            return;
        }

        _saidOn = on;
        _saidAttached = attached;
        _saidPosition = position;
        _saidScale = scale;
        _saidAngle = angle;
        _saidPlayId = _playId;
        _saidThreadId = _threadId;
        _sayAt = now + SaidEvery;
        _send!(WriteNeedle(on, attached, position, scale, angle, threadOn));
    }

    /// <summary>
    /// Where the local needle is: within the hero while it hangs on the hero, which the game has it do until the throw
    /// lets go of it, or else in the room, with its height above the hero.
    /// </summary>
    private static void ReadPlace(out bool attached, out Vector3 position, out Vector2 scale, out float angle) {
        var hero = _hero!;
        var needle = _needle!;
        attached = needle.parent != null && needle.IsChildOf(hero);
        if (attached) {
            position = hero.InverseTransformPoint(needle.position);
            var heroScale = hero.lossyScale;
            var needleScale = needle.lossyScale;
            scale = new Vector2(Ratio(needleScale.x, heroScale.x), Ratio(needleScale.y, heroScale.y));
            angle = needle.eulerAngles.z - hero.eulerAngles.z;
            return;
        }

        var place = needle.position;
        position = new Vector3(place.x, place.y, place.z - hero.position.z);
        scale = needle.lossyScale;
        angle = needle.eulerAngles.z;
    }

    /// <summary>
    /// Tells the other players of a thread to a ring as the harpoon FSM throws it, and again for a moment after.
    /// </summary>
    private static void WatchRingThread() {
        if (_fsm == null) {
            return;
        }

        var now = Time.unscaledTime;
        var ringThrow = _fsm.ActiveStateName == RingThrowState;
        if (ringThrow && !_wasRingThrow) {
            var ring = _fsm.FsmVariables.GetFsmGameObject(RingThreadVariable)?.Value;
            if (ring != null) {
                _ringId++;
                var place = ring.transform.position;
                _ringPosition = new Vector3(place.x, place.y, place.z - _hero!.position.z);
                _ringAngle = ring.transform.eulerAngles.z;
                _ringStartedAt = now;
                _ringSayAt = now;
                _ringSaidUntil = now + RingSaidFor;
            }
        }

        _wasRingThrow = ringThrow;
        if (now >= _ringSaidUntil || now < _ringSayAt) {
            return;
        }

        _ringSayAt = now + SaidEvery;
        _send!(WriteRingThread(now - _ringStartedAt));
    }

    /// <summary>
    /// Writes what the local needle shows to effect info.
    /// </summary>
    private static byte[] WriteNeedle(
        bool on,
        bool attached,
        Vector3 position,
        Vector2 scale,
        float angle,
        bool threadOn
    ) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte) Kind.Needle);
        writer.Write((byte) ((on ? 1 : 0) | (attached ? 2 : 0) | (threadOn ? 4 : 0)));
        writer.Write(position.x);
        writer.Write(position.y);
        writer.Write(position.z);
        writer.Write(scale.x);
        writer.Write(scale.y);
        writer.Write(angle);
        writer.Write(_playId);
        writer.Write(_clip);
        // How far into the clip the needle is now, so that a clip told again starts there
        writer.Write(_clipStart + Mathf.Max(0f, Time.time - _playedAt));
        writer.Write(_fps);
        writer.Write(_threadId);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Effect info of a kind, with what is said of it after.
    /// </summary>
    private static byte[] WithKind(Kind kind, byte[] body) {
        var info = new byte[body.Length + 1];
        info[0] = (byte) kind;
        body.CopyTo(info, 1);
        return info;
    }

    /// <summary>
    /// Writes the local thread to a ring to effect info, with how long ago it started.
    /// </summary>
    private static byte[] WriteRingThread(float since) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte) Kind.RingThread);
        writer.Write(_ringId);
        writer.Write(_ringPosition.x);
        writer.Write(_ringPosition.y);
        writer.Write(_ringPosition.z);
        writer.Write(_ringAngle);
        writer.Write(since);
        writer.Flush();
        return stream.ToArray();
    }

    /// <inheritdoc/>
    public override byte[]? GetEffectInfo() {
        return null;
    }

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        if (effectInfo == null || effectInfo.Length == 0) {
            return;
        }

        try {
            using var reader = new BinaryReader(new MemoryStream(effectInfo));
            switch ((Kind) reader.ReadByte()) {
                case Kind.Needle:
                    PlayNeedle(playerObject, reader);
                    break;
                case Kind.RingThread:
                    PlayRingThread(playerObject, reader);
                    break;
                case Kind.RingLook:
                    try {
                        HarpoonRingLook.Play(reader);
                    } catch (Exception e) when (e is not IOException) {
                        if (!_ringLookFailed) {
                            _ringLookFailed = true;
                            Logger.Warn($"Could not show another player's hold of a ring: {e}");
                        }
                    }

                    break;
            }
        } catch (IOException) {
            // A message cut short, or from a newer version that says more
        }
    }

    /// <summary>
    /// Shows on the character of another player what their needle shows.
    /// </summary>
    private static void PlayNeedle(GameObject playerObject, BinaryReader reader) {
        var flags = reader.ReadByte();
        var on = (flags & 1) != 0;
        var attached = (flags & 2) != 0;
        var threadOn = (flags & 4) != 0;
        var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var scale = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        var angle = reader.ReadSingle();
        var playId = reader.ReadByte();
        var clipName = reader.ReadString();
        var clipTime = reader.ReadSingle();
        var fps = reader.ReadSingle();
        var threadId = reader.ReadByte();

        var copy = NeedleCopy(playerObject, on);
        if (copy == null) {
            return;
        }

        if (!on) {
            copy.Hide();
            return;
        }

        copy.HeardAt = Time.unscaledTime;
        copy.Attached = attached;
        copy.Position = position;
        copy.Scale = scale;
        copy.Angle = angle;
        if (!copy.gameObject.activeSelf) {
            copy.gameObject.SetActive(true);
        }

        copy.Pin();

        if (playId != copy.PlayId) {
            copy.PlayId = playId;
            if (PlayClip(copy, clipName, clipTime, fps) && clipName == WallHitClip && clipTime <= SparkLateBy) {
                RaiseSpark(copy.transform.position);
            }
        }

        if (threadId != copy.ThreadId) {
            copy.ThreadId = threadId;
            if (threadOn && copy.Thread != null) {
                // Its own end switches it off again, as on the local hero
                copy.Thread.SetActive(false);
                copy.Thread.SetActive(true);
            }
        }
    }

    /// <summary>
    /// Plays a clip of the needle on its copy, from where the other player's needle is in it.
    /// </summary>
    /// <returns>Whether the clip was started.</returns>
    private static bool PlayClip(HeroHarpoonNeedleCopy copy, string clipName, float clipTime, float fps) {
        if (clipName.Length == 0 || !copy.TryGetComponent<tk2dSpriteAnimator>(out var animator)) {
            return false;
        }

        var clip = animator.GetClipByName(clipName);
        if (clip == null) {
            Logger.Warn($"The harpoon's needle has no clip '{clipName}' to play for another player");
            return false;
        }

        animator.Play(clip, clipTime, fps);
        return true;
    }

    /// <summary>
    /// The copy of the needle on the character of another player, made from the local hero's needle when there is none
    /// yet. It goes with the copies of the hero's parts, which go when the character is used for someone else.
    /// </summary>
    /// <param name="playerObject">The character of the other player.</param>
    /// <param name="make">Whether to make it when there is none.</param>
    private static HeroHarpoonNeedleCopy? NeedleCopy(GameObject playerObject, bool make) {
        var effects = playerObject.transform.Find("Effects");
        var holder = effects != null ? effects : playerObject.transform;
        var existing = holder.Find(NeedleName);
        if (existing != null) {
            return existing.GetComponent<HeroHarpoonNeedleCopy>();
        }

        if (!make || _needle == null) {
            return null;
        }

        // Made inside a holder that is switched off, so that nothing on it starts before it is ready
        var copyHolder = new GameObject("Harpoon Needle Holder");
        copyHolder.SetActive(false);
        try {
            var copyObject = Object.Instantiate(_needle.gameObject, copyHolder.transform, false);
            copyObject.name = NeedleName;
            copyObject.SetActive(false);

            // In what the other player wears, rather than in what the local player wears (see HeroCopies)
            HeroCopies.DressCopy(playerObject, copyObject);

            // What could strike anything in the room goes: the needle's striker, which the game switches on as the
            // needle strikes what sends it back
            foreach (var damager in copyObject.GetComponentsInChildren<DamageEnemies>(true)) {
                if (damager.gameObject == copyObject) {
                    damager.enabled = false;
                    continue;
                }

                damager.gameObject.SetActive(false);
                Object.Destroy(damager.gameObject);
            }

            foreach (var collider in copyObject.GetComponentsInChildren<Collider2D>(true)) {
                collider.enabled = false;
            }

            // The clips come from the other player; the default clip at its first start would play over the first
            if (copyObject.TryGetComponent<tk2dSpriteAnimator>(out var animator)) {
                animator.playAutomatically = false;
            }

            var copy = copyObject.AddComponent<HeroHarpoonNeedleCopy>();
            copy.Character = playerObject.transform;
            var thread = copyObject.transform.Find(ThreadName);
            if (thread != null) {
                thread.gameObject.SetActive(false);
                copy.Thread = thread.gameObject;
            }

            copyObject.transform.SetParent(holder, false);
            return copy;
        } finally {
            Object.Destroy(copyHolder);
        }
    }

    /// <summary>
    /// Raises the game's spark of a needle striking a wall, for another player's needle.
    /// </summary>
    private static void RaiseSpark(Vector3 position) {
        if (_sparkPrefab == null) {
            _sparkPrefab = PoolPrefab(WallHitState);
        }

        var spark = HeroDust.MakeDust(_sparkPrefab, position);
        if (spark == null) {
            return;
        }

        spark.SetActive(true);
        Object.Destroy(spark, CopyLasts);
    }

    /// <summary>
    /// Draws the thread of another player to a ring, as the game draws the local hero's.
    /// </summary>
    private static void PlayRingThread(GameObject playerObject, BinaryReader reader) {
        var ringId = reader.ReadByte();
        var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var angle = reader.ReadSingle();
        var since = reader.ReadSingle();

        if (!playerObject.TryGetComponent<HeroHarpoonHeard>(out var heard)) {
            heard = playerObject.AddComponent<HeroHarpoonHeard>();
        }

        if (heard.RingId == ringId) {
            return;
        }

        heard.RingId = ringId;
        if (since > RingSaidFor) {
            return;
        }

        if (_ringThreadPrefab == null) {
            _ringThreadPrefab = PoolPrefab(RingThreadSpawnState);
        }

        if (_ringThreadPrefab == null) {
            return;
        }

        var place = new Vector3(position.x, position.y, playerObject.transform.position.z + position.z);
        var ring = HeroCopies.Make(_ringThreadPrefab, playerObject, place, Quaternion.Euler(0f, 0f, angle));
        ring.SetActive(true);
        EffectUtils.SafelyRemoveAutoRecycle(ring);

        // As the game does as it throws the thread: what is under it switches on, and the thread plays and ends itself
        foreach (Transform child in ring.transform) {
            child.gameObject.SetActive(true);
        }

        Object.Destroy(ring, CopyLasts);
    }

    /// <summary>
    /// The game's own object that a state of the local hero's harpoon FSM takes from the pool, or null.
    /// </summary>
    private static GameObject? PoolPrefab(string stateName) {
        if (_fsm == null) {
            return null;
        }

        try {
            return _fsm.GetFirstAction<SpawnObjectFromGlobalPool>(stateName).gameObject.Value;
        } catch (ArgumentException) {
            return null;
        }
    }

    /// <summary>
    /// Hides the needle on the character of another player, for a character that leaves the room, and forgets what was
    /// heard of it: it may come back, or be used for another player.
    /// </summary>
    /// <param name="playerObject">The character of the other player.</param>
    public static void Reset(GameObject playerObject) {
        var copy = NeedleCopy(playerObject, false);
        if (copy != null) {
            copy.Hide();
            copy.PlayId = -1;
            copy.ThreadId = -1;
        }

        if (playerObject.TryGetComponent<HeroHarpoonHeard>(out var heard)) {
            heard.RingId = -1;
        }
    }

    /// <summary>
    /// The ratio of two scales, or the first one if the second is too small to divide by.
    /// </summary>
    internal static float Ratio(float value, float of) {
        return Mathf.Abs(of) < 0.0001f ? value : value / of;
    }
}

/// <summary>
/// Watches the local hero's needle once a frame, for <see cref="HeroHarpoon"/>.
/// </summary>
internal class HeroHarpoonWatcher : MonoBehaviour {
    private void LateUpdate() {
        HeroHarpoon.WatchLocal();
    }
}

/// <summary>
/// The copy of another player's needle, kept where their needle is: within their character while it hangs on them,
/// or at its place in the room once the throw let go of it.
/// </summary>
internal class HeroHarpoonNeedleCopy : MonoBehaviour {
    /// <summary>
    /// The character of the player whose needle it is.
    /// </summary>
    [NonSerialized]
    public Transform? Character;

    /// <summary>
    /// The copy of the needle's thread.
    /// </summary>
    [NonSerialized]
    public GameObject? Thread;

    /// <summary>
    /// Whether the needle hangs on the character, and where it is: within the character then, or else in the room,
    /// with its height above the character; its scale, within the character or in the room; and how it is turned.
    /// </summary>
    [NonSerialized]
    public bool Attached;

    [NonSerialized]
    public Vector3 Position;

    [NonSerialized]
    public Vector2 Scale = Vector2.one;

    [NonSerialized]
    public float Angle;

    /// <summary>
    /// The last clip and thread of the other player's needle that this played, or -1 for none.
    /// </summary>
    [NonSerialized]
    public int PlayId = -1;

    [NonSerialized]
    public int ThreadId = -1;

    /// <summary>
    /// When the other player's game last said that their needle shows, in unscaled time.
    /// </summary>
    [NonSerialized]
    public float HeardAt;

    private void LateUpdate() {
        // A needle whose going wasn't heard, or that a late word showed again, goes once nothing more is heard of it
        if (Time.unscaledTime - HeardAt > HeroHarpoon.ShownUnheardFor) {
            Hide();
            return;
        }

        Pin();
    }

    /// <summary>
    /// Puts the copy where the other player's needle is.
    /// </summary>
    public void Pin() {
        if (Character == null) {
            return;
        }

        Vector3 world;
        Vector2 worldScale;
        float worldAngle;
        if (Attached) {
            world = Character.TransformPoint(Position);
            var characterScale = Character.lossyScale;
            worldScale = new Vector2(Scale.x * characterScale.x, Scale.y * characterScale.y);
            worldAngle = Character.eulerAngles.z + Angle;
        } else {
            world = new Vector3(Position.x, Position.y, Character.position.z + Position.z);
            worldScale = Scale;
            worldAngle = Angle;
        }

        transform.position = world;
        transform.rotation = Quaternion.Euler(0f, 0f, worldAngle);
        var parent = transform.parent;
        var parentScale = parent != null ? parent.lossyScale : Vector3.one;
        transform.localScale = new Vector3(
            HeroHarpoon.Ratio(worldScale.x, parentScale.x),
            HeroHarpoon.Ratio(worldScale.y, parentScale.y),
            transform.localScale.z
        );
    }

    /// <summary>
    /// Hides the copy and its thread.
    /// </summary>
    public void Hide() {
        if (Thread != null) {
            Thread.SetActive(false);
        }

        if (gameObject.activeSelf) {
            gameObject.SetActive(false);
        }
    }
}

/// <summary>
/// What was heard last of another player's threads to rings, on their character.
/// </summary>
internal class HeroHarpoonHeard : MonoBehaviour {
    /// <summary>
    /// The last thread to a ring that was drawn, or -1 for none.
    /// </summary>
    [NonSerialized]
    public int RingId = -1;
}
