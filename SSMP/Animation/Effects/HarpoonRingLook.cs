using System;
using System.IO;
using SSMP.Game.Client;
using UnityEngine;

namespace SSMP.Animation.Effects;

/// <summary>
/// What a ring that a player hangs on with the harpoon shows of the hold: the ring turns to the side the needle struck
/// it from, shows the needle in it with a burst, sheds snow where it is snowy, and plays its own clips as the player
/// takes hold, turns, drops off or is thrown off. The ring's FSM does all that only in the game of the player whose
/// needle struck it, so the other players saw the ring hang still while the player hung on it (USER 10-25: "那个飞针
/// 的特效，我这边看队友的样子好像简陋一点"). The ring that the local player's harpoon struck last is watched, and what it
/// shows is said on change, again while it is held and for a moment after, as animation updates can be lost. The other
/// games show the same on that ring as a look only: its FSM, its sounds, the shake of the screen and what it tells what
/// it hangs from stay with the player who holds it (what a ring pulls down, the bend of a pole and the swing of a rope go
/// by CoopSave.PullRings). A ring that a player holds in their own game shows only their own hold there. Rings that carry
/// the player along a rail or up and down move in that player's game only, and show nothing of the hold elsewhere.
/// </summary>
internal static class HarpoonRingLook {
    /// <summary>
    /// The variable of the harpoon FSM that holds the ring the needle struck.
    /// </summary>
    private const string RingVariable = "Ring";

    /// <summary>
    /// The FSM of a ring, and the state that it rests in while nobody holds it.
    /// </summary>
    private const string RingFsmName = "Control";

    private const string RestState = "Idle";

    /// <summary>
    /// The FSMs of the rings that carry the player who holds them, along a rail or up and down: those move in that
    /// player's game only, so their look would show on a ring left behind.
    /// </summary>
    private static readonly string[] CarryingFsmNames = ["Rail Control", "ride"];

    /// <summary>
    /// The parts of a ring that show a hold: what turns it to the side it was struck from, its sprite, the needle that
    /// shows in it until it is taken hold of, the burst as the needle strikes it, and the snow it sheds then.
    /// </summary>
    private const string ParentName = "Ring Parent";

    private const string SpriteName = "Ring";
    private const string NeedleName = "Harpoon Connect L";
    private const string BurstName = "Connect Effect";
    private const string SnowName = "Hit Effect Snowy";

    /// <summary>
    /// How often what the ring shows is said again while it is held, and for how long after it rests again, in seconds.
    /// </summary>
    private const float SaidEvery = 0.25f;

    private const float RestSaidFor = 1f;

    /// <summary>
    /// The ring that the local player's harpoon struck last, with its parts.
    /// </summary>
    private static RingParts? _local;

    /// <summary>
    /// The ring that the local player's harpoon struck last, also when it is one that isn't watched.
    /// </summary>
    private static GameObject? _struck;

    /// <summary>
    /// What the local ring showed: the clip on its sprite as last seen, how many clips it started and how many times
    /// the burst and the snow came, counted round, and whether the burst and the snow showed in the last frame.
    /// </summary>
    private static tk2dSpriteAnimationClip? _lastClip;
    private static float _lastClipTime;
    private static byte _playId;
    private static byte _burstId;
    private static byte _snowId;
    private static bool _burstWasOn;
    private static bool _snowWasOn;

    /// <summary>
    /// What was said of the local ring last, whether anything was, when to say it again, and until when it is said
    /// that it rests, in unscaled time.
    /// </summary>
    private static Look _said;
    private static bool _saidAny;
    private static float _sayAt;
    private static float _restSaidUntil;

    /// <summary>
    /// What a ring shows of a hold.
    /// </summary>
    private struct Look {
        public bool Held;
        public bool Flipped;
        public bool NeedleOn;
        public bool BurstOn;
        public bool SnowOn;
        public byte BurstId;
        public byte SnowId;
        public byte PlayId;

        public bool SameAs(Look other) {
            return Held == other.Held && Flipped == other.Flipped && NeedleOn == other.NeedleOn &&
                   BurstOn == other.BurstOn && SnowOn == other.SnowOn && BurstId == other.BurstId &&
                   SnowId == other.SnowId && PlayId == other.PlayId;
        }
    }

    /// <summary>
    /// Forgets the local ring, for a new hero or the same one again.
    /// </summary>
    internal static void Forget() {
        _local = null;
        _struck = null;
        _saidAny = false;
        _restSaidUntil = 0f;
    }

    /// <summary>
    /// Tells the other players what the ring that the local player's harpoon struck last shows, once a frame.
    /// </summary>
    /// <param name="harpoon">The local hero's harpoon FSM.</param>
    /// <param name="send">Sends what the ring shows, without the kind of message, to the other players.</param>
    internal static void WatchLocal(PlayMakerFSM harpoon, Action<byte[]> send) {
        var struck = harpoon.FsmVariables.GetFsmGameObject(RingVariable)?.Value;
        if (struck != null && struck != _struck) {
            _struck = struck;
            _local = CarriesPlayer(struck) ? null : RingParts.Of(struck);
            _lastClip = null;
            _saidAny = false;
            _restSaidUntil = 0f;

            // The game takes hold of a ring in the same frame that the harpoon struck it, before this sees the ring:
            // what shows on a ring that is held already came with this hold
            var held = _local?.Fsm != null && _local.Fsm.ActiveStateName != RestState;
            _burstWasOn = !held && _local?.Burst != null && _local.Burst.activeSelf;
            _snowWasOn = !held && _local?.Snow != null && _local.Snow.activeSelf;
        }

        if (_local == null) {
            return;
        }

        if (_local.Root == null) {
            Forget();
            return;
        }

        var look = ReadLocal(_local);
        var now = Time.unscaledTime;
        if (_saidAny && _said.Held && !look.Held) {
            _restSaidUntil = now + RestSaidFor;
        }

        // A ring at rest is said only for a moment after a hold: one that rests as it is first seen was held before
        // this watched it, and what the look of another player's hold shows on it here is not said back to them
        if (!look.Held && now >= _restSaidUntil) {
            return;
        }

        var changed = !_saidAny || !look.SameAs(_said);
        if (!changed && now < _sayAt) {
            return;
        }

        _said = look;
        _saidAny = true;
        _sayAt = now + SaidEvery;
        send(Write(_local, look));
    }

    /// <summary>
    /// Reads what the local ring shows now, counting the clips that its sprite started and the times that the burst
    /// and the snow came since the last frame.
    /// </summary>
    private static Look ReadLocal(RingParts ring) {
        var animator = ring.Animator;
        var clip = animator != null ? animator.CurrentClip : null;
        if (clip != null) {
            // Read only with a clip, which the time in it is worked out from
            var time = animator!.ClipTimeSeconds;
            if (!ReferenceEquals(clip, _lastClip) || time + 0.0001f < _lastClipTime) {
                _playId++;
            }

            _lastClipTime = time;
        }

        _lastClip = clip;

        var burstOn = ring.Burst != null && ring.Burst.activeSelf;
        if (burstOn && !_burstWasOn) {
            _burstId++;
        }

        _burstWasOn = burstOn;

        var snowOn = ring.Snow != null && ring.Snow.activeSelf;
        if (snowOn && !_snowWasOn) {
            _snowId++;
        }

        _snowWasOn = snowOn;

        return new Look {
            Held = ring.Fsm != null && ring.Fsm.ActiveStateName != RestState,
            Flipped = ring.Parent != null && ring.Parent.localScale.x < 0f,
            NeedleOn = ring.Needle != null && ring.Needle.activeSelf,
            BurstOn = burstOn,
            SnowOn = snowOn,
            BurstId = _burstId,
            SnowId = _snowId,
            PlayId = _playId
        };
    }

    /// <summary>
    /// Writes what the local ring shows: where it is in the room, what shows on it, and its sprite's clip, where in it
    /// the sprite is and at what speed.
    /// </summary>
    private static byte[] Write(RingParts ring, Look look) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(ring.Root.scene.name);
        writer.Write(ring.Path);
        writer.Write((byte) ((look.Held ? 1 : 0) | (look.Flipped ? 2 : 0) | (look.NeedleOn ? 4 : 0) |
                             (look.BurstOn ? 8 : 0) | (look.SnowOn ? 16 : 0)));
        writer.Write(look.BurstId);
        writer.Write(look.SnowId);
        writer.Write(look.PlayId);
        var animator = ring.Animator;
        var clip = animator != null ? animator.CurrentClip : null;
        writer.Write(clip?.name ?? "");
        // Never quite its start: a clip played again from its very start while it plays doesn't start again
        writer.Write(clip != null ? Mathf.Max(animator!.ClipTimeSeconds, 0.0001f) : 0f);
        writer.Write(clip != null ? animator!.ClipFps : 0f);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Shows on a ring in this game what it shows in the game of the player who holds it, unless the local player holds
    /// it here.
    /// </summary>
    internal static void Play(BinaryReader reader) {
        var scene = reader.ReadString();
        var path = reader.ReadString();
        var flags = reader.ReadByte();
        var burstId = reader.ReadByte();
        var snowId = reader.ReadByte();
        var playId = reader.ReadByte();
        var clipName = reader.ReadString();
        var clipTime = reader.ReadSingle();
        var fps = reader.ReadSingle();

        var root = ScenePath.Find(path, scene);
        if (root == null || CarriesPlayer(root)) {
            return;
        }

        if (!root.TryGetComponent<HarpoonRingHeard>(out var heard)) {
            heard = root.AddComponent<HarpoonRingHeard>();
            heard.Parts = RingParts.Of(root);
        }

        var ring = heard.Parts!;
        if (ring.Fsm != null && ring.Fsm.ActiveStateName != RestState) {
            return;
        }

        var held = (flags & 1) != 0;
        if (ring.Parent != null) {
            var scale = ring.Parent.localScale;
            ring.Parent.localScale = new Vector3((flags & 2) != 0 ? -1f : 1f, scale.y, scale.z);
        }

        var needleOn = (flags & 4) != 0;
        if (ring.Needle != null && ring.Needle.activeSelf != needleOn) {
            ring.Needle.SetActive(needleOn);
        }

        // What comes once as the needle strikes, while it shows there and the ring is held: a late word of it plays
        // nothing, and a ring that sheds no snow never shows any
        if (burstId != heard.BurstId) {
            heard.BurstId = burstId;
            if (held && (flags & 8) != 0) {
                Restart(ring.Burst);
            }
        }

        if (snowId != heard.SnowId) {
            heard.SnowId = snowId;
            if (held && (flags & 16) != 0) {
                Restart(ring.Snow);
            }
        }

        if (playId == heard.PlayId) {
            return;
        }

        heard.PlayId = playId;
        if (ring.Animator == null || clipName.Length == 0) {
            return;
        }

        var clip = ring.Animator.GetClipByName(clipName);
        if (clip != null) {
            ring.Animator.Play(clip, clipTime, fps);
        }
    }

    /// <summary>
    /// Whether a ring carries the player who holds it, along a rail or up and down.
    /// </summary>
    private static bool CarriesPlayer(GameObject ring) {
        if (ring.GetComponentInParent<HarpoonRingSlider>() != null) {
            return true;
        }

        foreach (var fsm in ring.GetComponents<PlayMakerFSM>()) {
            if (Array.IndexOf(CarryingFsmNames, fsm.FsmName) >= 0) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Switches a part off and on again, which plays it from its start.
    /// </summary>
    private static void Restart(GameObject? part) {
        if (part == null) {
            return;
        }

        part.SetActive(false);
        part.SetActive(true);
    }

    /// <summary>
    /// A ring and the parts of it that show a hold, found by their names as the ring's FSM finds them.
    /// </summary>
    internal sealed class RingParts {
        public GameObject Root = null!;
        public string Path = "";
        public PlayMakerFSM? Fsm;
        public Transform? Parent;
        public tk2dSpriteAnimator? Animator;
        public GameObject? Needle;
        public GameObject? Burst;
        public GameObject? Snow;

        public static RingParts Of(GameObject root) {
            var parts = new RingParts { Root = root, Path = ScenePath.Get(root.transform) };
            foreach (var fsm in root.GetComponents<PlayMakerFSM>()) {
                if (fsm.FsmName == RingFsmName) {
                    parts.Fsm = fsm;
                    break;
                }
            }

            parts.Parent = FindNamed(root.transform, ParentName);
            var under = parts.Parent != null ? parts.Parent : root.transform;
            var sprite = FindNamed(under, SpriteName);
            parts.Animator = sprite != null ? sprite.GetComponent<tk2dSpriteAnimator>() : null;
            parts.Needle = FindNamed(under, NeedleName)?.gameObject;
            parts.Burst = FindNamed(under, BurstName)?.gameObject;
            parts.Snow = FindNamed(root.transform, SnowName)?.gameObject;
            return parts;
        }

        /// <summary>
        /// The first object under a transform with a name, looked for down through its children.
        /// </summary>
        private static Transform? FindNamed(Transform root, string name) {
            for (var i = 0; i < root.childCount; i++) {
                var child = root.GetChild(i);
                if (child.name == name) {
                    return child;
                }

                var below = FindNamed(child, name);
                if (below != null) {
                    return below;
                }
            }

            return null;
        }
    }
}

/// <summary>
/// What was heard last of another player's hold of a ring, on the ring, for <see cref="HarpoonRingLook"/>.
/// </summary>
internal class HarpoonRingHeard : MonoBehaviour {
    /// <summary>
    /// The ring's parts.
    /// </summary>
    [NonSerialized]
    public HarpoonRingLook.RingParts? Parts;

    /// <summary>
    /// The last burst, snow and clip shown, or -1 for none.
    /// </summary>
    [NonSerialized]
    public int BurstId = -1;

    [NonSerialized]
    public int SnowId = -1;

    [NonSerialized]
    public int PlayId = -1;
}
