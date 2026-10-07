using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// What a creature shows as it goes: the clip that its sprite animator plays.
///
/// A player saw a boss show the wind-up of one attack and the wind-up of another by turns, a frame each, and could not
/// tell which was coming. The boss's own FSM never does that: once it has chosen an attack, it goes through with it.
/// Nothing in the logs of that fight could say where it came from either, so this says it as it happens:
/// - when a creature goes back and forth between two clips within a moment, which game chose each clip and how: in the
///   game that runs the creature, the FSM, state and action that played it; in the other game, the packet that brought
///   it. And whether a second body of the same creature was drawn at the time;
/// - when the copy is sent a clip in a packet older than the one that brought the clip it shows. Such a clip is turned
///   away, as an older position always was: this transport does not keep in order what it carries, and showing it put
///   the copy back in a pose that the creature had already left. The numbers are those of the server's packets to this
///   game, so this covers that way only: what the scene host's game sent to the server out of order, the server passes
///   on in the order it came;
/// - when the copy is drawn in the game that runs the creature, where only the room's own body should be.
/// Every line starts with "[Poses]".
/// </summary>
internal partial class Entity {
    /// <summary>
    /// How many of the last clips are kept.
    /// </summary>
    private const int PosesKept = 8;

    /// <summary>
    /// How many clips taking turns make a back and forth worth writing down: two clips, five times between them, which
    /// is four changes.
    /// </summary>
    private const int PosesInBackAndForth = 5;

    /// <summary>
    /// The moment in seconds within which they must all fall. The boss of that report takes half a second at the least
    /// for each turn it makes, so turning round to one player and back again does not get there by itself.
    /// </summary>
    private const float PosesMoment = 1f;

    /// <summary>
    /// How long in seconds a creature's next line of the same kind waits, counting what it leaves out meanwhile.
    /// </summary>
    private const float PosesQuiet = 5f;

    /// <summary>
    /// How many clips in a row may be turned away as older than the one shown, before one is taken anyway and counted
    /// from. That many in a row does not happen to a connection that is merely out of order, only to a numbering that
    /// is wrong, and a creature should not be held in one pose by that.
    /// </summary>
    private const int LatePosesRefusedAtMost = 8;

    /// <summary>
    /// How many packets back a clip may come from and still be taken for one that merely came late. Further back than
    /// that, the number it is compared with was never a real one.
    /// </summary>
    private const int LatePoseWindow = 1024;

    /// <summary>
    /// How long in seconds the number of the packet that brought the copy's last clip is compared with at all. A packet
    /// goes out every frame or so whether it carries anything or not, so the numbers go all the way round in less than
    /// twenty minutes, and a creature that keeps one clip that long would have its next clips taken for old ones. A
    /// clip held back on the way is late by a round trip or two, which is well inside this.
    /// </summary>
    private const float LatePoseMaxAge = 3f;

    /// <summary>
    /// A clip that a body of the creature played, and where it came from.
    /// </summary>
    private struct Pose {
        /// <summary>
        /// The name of the clip.
        /// </summary>
        public string Clip;

        /// <summary>
        /// When it was played, in unscaled seconds.
        /// </summary>
        public float At;

        /// <summary>
        /// The frame it was played in.
        /// </summary>
        public int Frame;

        /// <summary>
        /// The FSM that played it in this game, named with its object when that is not the creature, or null for a clip
        /// that no FSM played.
        /// </summary>
        public string? Fsm;

        /// <summary>
        /// The state that FSM was in.
        /// </summary>
        public string? State;

        /// <summary>
        /// The kind of action of that state that played it.
        /// </summary>
        public string? Action;

        /// <summary>
        /// The packet that brought the scene host's clip, or 0 for a clip played in this game.
        /// </summary>
        public ushort Packet;
    }

    /// <summary>
    /// The last clips that the creature changed to, in a ring.
    /// </summary>
    private readonly Pose[] _poses = new Pose[PosesKept];

    /// <summary>
    /// How many of <see cref="_poses"/> are filled.
    /// </summary>
    private int _posesFilled;

    /// <summary>
    /// Where the newest of <see cref="_poses"/> is.
    /// </summary>
    private int _newestPose;

    /// <summary>
    /// When the last back and forth of this creature was written down.
    /// </summary>
    private float _backAndForthToldAt = float.NegativeInfinity;

    /// <summary>
    /// How many back and forths came since then that were not.
    /// </summary>
    private int _backAndForthsUntold;

    /// <summary>
    /// The packet that brought the newest clip the copy was sent, and that clip.
    /// </summary>
    private ushort _posePacket;

    /// <summary>
    /// The clip that came in <see cref="_posePacket"/>.
    /// </summary>
    private string? _posePacketClip;

    /// <summary>
    /// Whether the copy was sent a clip in a packet yet.
    /// </summary>
    private bool _hasPosePacket;

    /// <summary>
    /// When the clip of <see cref="_posePacket"/> was taken, in unscaled seconds.
    /// </summary>
    private float _posePacketAt;

    /// <summary>
    /// How many clips in a row were turned away as older than the one shown.
    /// </summary>
    private int _latePosesRefused;

    /// <summary>
    /// When the last clip turned away was written down.
    /// </summary>
    private float _latePoseToldAt = float.NegativeInfinity;

    /// <summary>
    /// How many were turned away since then without being.
    /// </summary>
    private int _latePosesUntold;

    /// <summary>
    /// The packet that brought the clip the copy is playing now, set just before the copy plays it, or 0.
    /// </summary>
    private ushort _copyPosePacket;

    /// <summary>
    /// When it was last written down that the copy is drawn in the game that runs the creature.
    /// </summary>
    private float _copyDrawnToldAt = float.NegativeInfinity;

    /// <summary>
    /// Notes a clip that a body of this creature has just played: the room's own in the game that runs it, or the copy
    /// in the other game.
    /// </summary>
    /// <param name="clip">The clip.</param>
    /// <param name="packet">The packet that brought the scene host's clip, or 0 for a clip played in this game.</param>
    private void NotePose(tk2dSpriteAnimationClip? clip, ushort packet) {
        if (clip == null || _posesFilled > 0 && _poses[_newestPose].Clip == clip.name) {
            return;
        }

        var fsm = packet == 0 ? FsmExecutionStack.ExecutingFsm : null;
        string? fsmName = null;
        if (fsm != null) {
            var owner = fsm.GameObject;
            fsmName = owner == Object.Host || owner == Object.Client
                ? fsm.Name
                : $"{(owner == null ? "?" : owner.name)} {fsm.Name}";
        }

        _newestPose = (_newestPose + 1) % PosesKept;
        _poses[_newestPose] = new Pose {
            Clip = clip.name,
            At = Time.unscaledTime,
            Frame = Time.frameCount,
            Fsm = fsmName,
            State = fsm?.ActiveStateName,
            Action = fsm == null ? null : FsmExecutionStack.ExecutingAction?.GetType().Name,
            Packet = packet
        };

        if (_posesFilled < PosesKept) {
            _posesFilled++;
        }

        // Only as it gets there, so that one back and forth that goes on is told once and counted once
        if (CountBackAndForth() == PosesInBackAndForth) {
            TellBackAndForth();
        }
    }

    /// <summary>
    /// How many of the newest clips take turns between the newest two, all of them within the moment.
    /// </summary>
    private int CountBackAndForth() {
        if (_posesFilled < 2) {
            return _posesFilled;
        }

        var newest = _poses[_newestPose];
        var before = _poses[(_newestPose + PosesKept - 1) % PosesKept].Clip;
        var count = 1;
        for (var back = 1; back < _posesFilled; back++) {
            var pose = _poses[(_newestPose + PosesKept - back) % PosesKept];
            if (newest.At - pose.At > PosesMoment || pose.Clip != (back % 2 == 0 ? newest.Clip : before)) {
                break;
            }

            count++;
        }

        return count;
    }

    /// <summary>
    /// Writes down the back and forth that the newest clips make, unless one of this creature was a moment ago.
    /// </summary>
    private void TellBackAndForth() {
        var now = Time.unscaledTime;
        if (now - _backAndForthToldAt < PosesQuiet) {
            _backAndForthsUntold++;
            return;
        }

        var copy = _isControlled;
        var body = copy ? Object.Client : Object.Host;
        var oldest = _poses[(_newestPose + PosesKept - (PosesInBackAndForth - 1)) % PosesKept];

        var text = new StringBuilder();
        text.Append($"[Poses] '{(body == null ? $"entity {Id}" : body.name)}', ");
        text.Append(copy ? "the copy of the other game's," : "which this game runs,");
        text.Append($" went back and forth {PosesInBackAndForth - 1} times in {now - oldest.At:0.00} s");
        if (_backAndForthsUntold > 0) {
            text.Append($" (and {_backAndForthsUntold} more times since the last line of these)");
        }

        text.Append(':');
        for (var back = PosesInBackAndForth - 1; back >= 0; back--) {
            var pose = _poses[(_newestPose + PosesKept - back) % PosesKept];
            text.Append($" '{pose.Clip}' (frame {pose.Frame}, ");
            if (pose.Packet != 0) {
                text.Append($"packet {pose.Packet}");
            } else if (pose.Fsm != null) {
                text.Append($"{pose.Fsm} in '{pose.State}', {pose.Action ?? "no action"}");
            } else {
                text.Append("no FSM");
            }

            text.Append(back == 0 ? ")." : "),");
        }

        if (copy) {
            AppendHostStates(text);
        }

        text.Append(' ');
        text.Append(copy
            ? DescribeOtherBody(Object.Host, body, "The room's own body")
            : DescribeOtherBody(Object.Client, body, "The copy"));
        Logger.Info(text.ToString());

        _backAndForthToldAt = now;
        _backAndForthsUntold = 0;
    }

    /// <summary>
    /// Adds what the scene host last said the creature's FSMs are doing.
    /// </summary>
    private void AppendHostStates(StringBuilder text) {
        var any = false;
        for (var fsmIndex = 0; fsmIndex < _fsms.Host.Count && fsmIndex < _fsmSnapshots.Count; fsmIndex++) {
            var state = _fsmSnapshots[fsmIndex].CurrentState;
            var fsm = _fsms.Host[fsmIndex];
            if (string.IsNullOrEmpty(state) || fsm == null) {
                continue;
            }

            text.Append(any ? ", " : " The scene host's last word: ");
            text.Append($"{fsm.FsmName} in '{state}'");
            any = true;
        }

        if (any) {
            text.Append('.');
        }
    }

    /// <summary>
    /// Says whether the other body of the creature is drawn, and if so where and in what pose.
    /// </summary>
    /// <param name="other">The other body.</param>
    /// <param name="body">The body the line is about.</param>
    /// <param name="named">What the other body is called in the line.</param>
    private static string DescribeOtherBody(GameObject? other, GameObject? body, string named) {
        if (other == null || !other.activeInHierarchy) {
            return $"{named} is off.";
        }

        var renderer = other.GetComponent<Renderer>();
        var animator = other.GetComponent<tk2dSpriteAnimator>();
        var clip = animator == null || animator.CurrentClip == null ? "nothing" : $"'{animator.CurrentClip.name}'";
        var distance = body == null ? -1f : Vector2.Distance(other.transform.position, body.transform.position);
        return $"{named} is on too{(renderer != null && !renderer.enabled ? ", but not drawn" : ", and drawn")}, " +
               $"{distance:0.00} from it, showing {clip}.";
    }

    /// <summary>
    /// Whether a clip that the scene host sent in the given packet is older than the one the copy was last sent, which
    /// is then turned away and written down. A clip that came in no packet is never older.
    /// </summary>
    /// <param name="packet">The packet that brought the clip, or 0.</param>
    /// <param name="clipName">The clip.</param>
    private bool IsLatePose(ushort packet, string clipName) {
        if (packet == 0) {
            return false;
        }

        var now = Time.unscaledTime;
        if (_hasPosePacket && now - _posePacketAt < LatePoseMaxAge) {
            // By the sign of the difference in the size the numbers are kept in, so that the step from the largest
            // back to zero reads as one forward
            var difference = (short) (packet - _posePacket);
            if (difference < 0 && difference > -LatePoseWindow && _latePosesRefused < LatePosesRefusedAtMost) {
                _latePosesRefused++;
                TellLatePose(packet, clipName);
                return true;
            }
        }

        _hasPosePacket = true;
        _posePacket = packet;
        _posePacketAt = now;
        _posePacketClip = clipName;
        _latePosesRefused = 0;
        return false;
    }

    /// <summary>
    /// Forgets the packet that brought the copy's last clip, when the game that runs the creature changes: the numbers
    /// that come after are not to be held against it.
    /// </summary>
    private void ForgetPosePackets() {
        _hasPosePacket = false;
        _latePosesRefused = 0;
    }

    /// <summary>
    /// Writes down a clip turned away as older than the one shown, unless one of this creature was a moment ago.
    /// </summary>
    private void TellLatePose(ushort packet, string clipName) {
        var now = Time.unscaledTime;
        if (now - _latePoseToldAt < PosesQuiet) {
            _latePosesUntold++;
            return;
        }

        Logger.Info(
            $"[Poses] The copy '{(Object.Client == null ? $"entity {Id}" : Object.Client.name)}' was sent " +
            $"'{clipName}' in packet {packet} after '{_posePacketClip}' in packet {_posePacket} had come: the older " +
            $"clip is turned away{(_latePosesUntold > 0 ? $" (and {_latePosesUntold} more since the last line)" : "")}"
        );

        _latePoseToldAt = now;
        _latePosesUntold = 0;
    }

    /// <summary>
    /// Writes down, now and then, that the copy is switched on in the game that runs this creature, beside the room's
    /// own body.
    /// </summary>
    private void SayIfTheCopyIsOnHere() {
        if (Object.Client == null || !Object.Client.activeInHierarchy) {
            return;
        }

        var now = Time.unscaledTime;
        if (now - _copyDrawnToldAt < PosesQuiet * 2) {
            return;
        }

        _copyDrawnToldAt = now;
        Logger.Info(
            $"[Poses] The copy of '{Object.Host.name}' is switched on in this game, which runs it. " +
            DescribeOtherBody(Object.Client, Object.Host, "The copy")
        );
    }
}
