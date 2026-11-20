using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Writes down in the log how the characters move where nobody steers them, and what moved them ([Moves] lines), so
/// that a character seen to move by itself can be put down to its cause (USER 11-20 "你打log吧，log一下角色移动和为什么
/// 移动": standing in the talk before a fight, the player's character went up and down four times in the other
/// player's game, and neither log said where it had been or what had moved it).
///
/// The local character: each stretch of its moves while the player doesn't steer it, as in a talk, a film of the game
/// or a rest, goes in one line, with how far it went each way and by what: a state machine, named with the state and
/// the object it is on, the character's own code, a part of the mod's work of each frame, or what is left, its speed
/// in the game's physics and what none of these covers. A move further than its speed takes it in a frame while the
/// player steers it goes in a line of its own.
///
/// The character of another player: each stretch of its moves in this game from where it stood still that stays on
/// the spot, like going up and down, and each that this game draws while their positions hold it still, goes in one
/// line, with how far their positions moved it and how far this game drew it from the latest of them: going on from it
/// by itself, smoothing a step away, carrying it. A move that their positions make has its cause in their own log.
/// </summary>
internal static class MoveWatch {
    /// <summary>
    /// How far a character has to move in a frame for the frame to be a move, in units.
    /// </summary>
    private const float MoveDistance = 0.0005f;

    /// <summary>
    /// How long a character has to stand still for a stretch of its moves to be over, in seconds.
    /// </summary>
    private const float StillEnds = 0.5f;

    /// <summary>
    /// The longest stretch of moves that one line tells of, in seconds: one that goes on longer goes on in the next.
    /// </summary>
    private const float LongestStretch = 10f;

    /// <summary>
    /// How much further than its speed takes it the local character has to be moved in a frame while the player steers
    /// it for the move to go in a line of its own, in units.
    /// </summary>
    private const float PutDistance = 1f;

    /// <summary>
    /// How long the character of another player has to have stood still for a stretch of its moves to be watched, in
    /// seconds.
    /// </summary>
    private const float StoodStill = 0.3f;

    /// <summary>
    /// How far the character of another player may go to either side, and end up from where it stood, for a stretch of
    /// its moves to be on the spot, in units.
    /// </summary>
    private const float OnTheSpot = 0.5f;

    /// <summary>
    /// How far the character of another player has to go for a stretch of its moves to be written down, in units.
    /// </summary>
    private const float SeenDistance = 0.05f;

    /// <summary>
    /// The most causes that a line names, the ones that moved the character furthest.
    /// </summary>
    private const int CausesNamed = 6;

    /// <summary>
    /// What is left of a move of the local character once the calls that are watched are taken out of it, for a
    /// character that is on nothing.
    /// </summary>
    private const string RestCause = "its speed in the game's physics, or what nothing watched did";

    /// <summary>
    /// The character's own code in each frame, which moves it as the game has it move.
    /// </summary>
    internal const string HeroUpdate = "the character's own code (HeroController.Update)";

    /// <summary>
    /// The character's own code in each step of the game's physics.
    /// </summary>
    private const string HeroFixedUpdate = "the character's own code (HeroController.FixedUpdate)";

    /// <summary>
    /// The character's own code at the end of each frame.
    /// </summary>
    private const string HeroLateUpdate = "the character's own code (HeroController.LateUpdate)";

    /// <summary>
    /// The hooks into the game's methods in which the local character may be moved, kept for the whole session.
    /// </summary>
    private static readonly List<Hook> Hooks = [];

    /// <summary>
    /// The local character, as of the last frame in which it was updated.
    /// </summary>
    private static Transform? _hero;

    /// <summary>
    /// The animator of the local character, for the clip it plays.
    /// </summary>
    private static tk2dSpriteAnimator? _heroAnimator;

    /// <summary>
    /// The calls that are watched now, the outermost first: where the local character was as each began, and how far
    /// the calls inside it moved it, which are not its own doing.
    /// </summary>
    private static readonly List<(Vector2 Before, Vector2 Inner)> Calls = [];

    /// <summary>
    /// What moved the local character since the last frame began, and how far each did.
    /// </summary>
    private static readonly Dictionary<string, Vector2> Moved = new();

    /// <summary>
    /// How far what <see cref="Moved"/> holds moved the local character in all.
    /// </summary>
    private static Vector2 _movedByCauses;

    /// <summary>
    /// The names of the states of state machines that moved the local character, made once each.
    /// </summary>
    private static readonly Dictionary<FsmState, string> StateNames = new();

    /// <summary>
    /// Whether <see cref="_last"/> holds where the local character was as the last frame began.
    /// </summary>
    private static bool _hasLast;

    /// <summary>
    /// Where the local character was as the last frame began.
    /// </summary>
    private static Vector2 _last;

    /// <summary>
    /// The scene that was active as the last frame began, by its handle.
    /// </summary>
    private static int _scene;

    /// <summary>
    /// The stretch of moves of the local character while the player doesn't steer it, if one goes on.
    /// </summary>
    private static Stretch? _own;

    /// <summary>
    /// Hooks the calls of the game in which the local character may be moved: the states of the state machines, as
    /// they are entered and as they go on, and the character's own code in the steps of the game's physics and at the
    /// end of each frame. Its code in each frame and the mod's work of each frame are watched where they are called
    /// (EventHooks.OnHeroControllerUpdate).
    /// </summary>
    internal static void Initialize() {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var name in new[] { "OnEnter", "OnUpdate", "OnFixedUpdate", "OnLateUpdate" }) {
            Hooks.Add(
                new Hook(
                    typeof(FsmState).GetMethod(name, flags, null, Type.EmptyTypes, null),
                    new Action<Action<FsmState>, FsmState>(OnState)
                )
            );
        }

        Hooks.Add(
            new Hook(
                typeof(HeroController).GetMethod("FixedUpdate", flags, null, Type.EmptyTypes, null),
                new Action<Action<HeroController>, HeroController>(OnHeroFixedUpdate)
            )
        );
        Hooks.Add(
            new Hook(
                typeof(HeroController).GetMethod("LateUpdate", flags, null, Type.EmptyTypes, null),
                new Action<Action<HeroController>, HeroController>(OnHeroLateUpdate)
            )
        );
    }

    /// <summary>
    /// Hook for a state of a state machine as it is entered or goes on, which notes how far it moved the local
    /// character.
    /// </summary>
    private static void OnState(Action<FsmState> orig, FsmState self) {
        var call = Begin();
        try {
            orig(self);
        } finally {
            End(call, self);
        }
    }

    /// <summary>
    /// Hook for the character's own code in each step of the game's physics.
    /// </summary>
    private static void OnHeroFixedUpdate(Action<HeroController> orig, HeroController self) {
        var call = Begin();
        try {
            orig(self);
        } finally {
            End(call, HeroFixedUpdate);
        }
    }

    /// <summary>
    /// Hook for the character's own code at the end of each frame.
    /// </summary>
    private static void OnHeroLateUpdate(Action<HeroController> orig, HeroController self) {
        var call = Begin();
        try {
            orig(self);
        } finally {
            End(call, HeroLateUpdate);
        }
    }

    /// <summary>
    /// Begins to watch a call, which may move the local character. Returns what <see cref="End(int, string)"/> takes,
    /// or -1 while there is no local character.
    /// </summary>
    internal static int Begin() {
        // The character is gone in the main menu, and its last one may linger there
        if (_hero == null) {
            return -1;
        }

        Calls.Add((_hero.position, Vector2.zero));
        return Calls.Count - 1;
    }

    /// <summary>
    /// Ends to watch a call, which moved the local character as far as it did but for the calls inside it.
    /// </summary>
    /// <param name="call">What <see cref="Begin"/> returned for it.</param>
    /// <param name="cause">What the call is, for the log.</param>
    internal static void End(int call, string cause) {
        if (TakeCall(call) is { } own) {
            Note(cause, own);
        }
    }

    /// <summary>
    /// Ends to watch a state of a state machine, which moved the local character as far as it did but for the calls
    /// inside it.
    /// </summary>
    private static void End(int call, FsmState state) {
        if (TakeCall(call) is { } own) {
            Note(NameOf(state), own);
        }
    }

    /// <summary>
    /// Takes a call that is watched off the calls, and gives how far it moved the local character but for the calls
    /// inside it, or null for not at all. The call that it is inside of is told how far it moved the character.
    /// </summary>
    private static Vector2? TakeCall(int call) {
        if (call < 0) {
            return null;
        }

        var (before, inner) = Calls[call];
        Calls.RemoveRange(call, Calls.Count - call);
        if (_hero == null) {
            return null;
        }

        var moved = (Vector2) _hero.position - before;
        if (moved == Vector2.zero) {
            return null;
        }

        if (call > 0) {
            var outer = Calls[call - 1];
            Calls[call - 1] = (outer.Before, outer.Inner + moved);
        }

        var own = moved - inner;
        return own == Vector2.zero ? null : own;
    }

    /// <summary>
    /// Notes that something moved the local character since the last frame began.
    /// </summary>
    private static void Note(string cause, Vector2 by) {
        Moved[cause] = Moved.TryGetValue(cause, out var sum) ? sum + by : by;
        _movedByCauses += by;
    }

    /// <summary>
    /// A state of a state machine as the log names it: the state machine, the object it is on, the state and the kinds
    /// of action in it.
    /// </summary>
    private static string NameOf(FsmState state) {
        if (StateNames.TryGetValue(state, out var name)) {
            return name;
        }

        var actions = string.Join(", ", state.Actions.Where(action => action.Enabled).Select(
            action => action.GetType().Name
        ).Distinct());
        name = $"'{state.Fsm.Name}' of '{state.Fsm.GameObjectName}' in '{state.Name}' ({actions})";
        StateNames[state] = name;
        return name;
    }

    /// <summary>
    /// Takes a frame of the local character as it begins (EventHooks.OnHeroControllerUpdate): how far it moved since
    /// the last one began and by what, which goes into the stretch of its moves while the player doesn't steer it, or
    /// into a line of its own for a move further than its speed takes it while they do. A frame of a room that comes
    /// in or out is none, as the room puts the character where it comes in.
    /// </summary>
    internal static void FrameStart(HeroController hero) {
        var transform = hero.transform;
        if (!ReferenceEquals(_hero, transform)) {
            _hero = transform;
            _heroAnimator = hero.GetComponent<tk2dSpriteAnimator>();
            _hasLast = false;
        }

        var scene = SceneManager.GetActiveScene().handle;
        Vector2 position = transform.position;
        var now = Time.time;
        if (!_hasLast || scene != _scene || hero.cState.transitioning) {
            if (_own != null) {
                FinishOwn(hero);
            }

            if (scene != _scene) {
                StateNames.Clear();
            }

            _hasLast = true;
            _scene = scene;
            NextFrame(position);
            return;
        }

        var frame = position - _last;
        var rest = frame - _movedByCauses;
        if (rest.sqrMagnitude > MoveDistance * MoveDistance) {
            var parent = transform.parent;
            Note(parent == null ? RestCause : $"its speed, or '{parent.name}' that it is on", rest);
        }

        var steered = !hero.controlReqlinquished && hero.acceptingInput;
        if (frame.sqrMagnitude > MoveDistance * MoveDistance) {
            if (!steered) {
                _own ??= new Stretch(now, _last, ClipOf(), Situation(hero));
                _own.Take(position, frame, hero.Body.linearVelocity, now);
                if (now - _own.Start >= LongestStretch) {
                    FinishOwn(hero);
                }
            } else {
                if (_own != null) {
                    FinishOwn(hero);
                }

                SayPut(hero, position, frame);
            }
        } else if (_own != null && (steered || now - _own.LastMove >= StillEnds)) {
            FinishOwn(hero);
        }

        NextFrame(position);
    }

    /// <summary>
    /// Begins the next frame of the local character where it is.
    /// </summary>
    private static void NextFrame(Vector2 position) {
        _last = position;
        Moved.Clear();
        _movedByCauses = Vector2.zero;
    }

    /// <summary>
    /// Writes down a move of the local character further than its speed takes it in a frame, while the player steers
    /// it.
    /// </summary>
    private static void SayPut(HeroController hero, Vector2 position, Vector2 frame) {
        var bySpeed = hero.Body.linearVelocity.magnitude * Time.deltaTime;
        if (frame.magnitude - bySpeed < PutDistance) {
            return;
        }

        var from = position - frame;
        var causes = Moved.OrderByDescending(pair => pair.Value.sqrMagnitude).Take(CausesNamed)
            .Select(pair => $"{pair.Key} {pair.Value.magnitude:F2}");
        Logger.Info(
            $"[Moves] The character was moved {frame.magnitude:F2} in a frame, where its speed takes it " +
            $"{bySpeed:F2}, from ({from.x:F2}, {from.y:F2}) to ({position.x:F2}, {position.y:F2}), in clip " +
            $"'{ClipOf()}', by {string.Join("; ", causes)}"
        );
    }

    /// <summary>
    /// Writes down the stretch of moves of the local character while the player didn't steer it.
    /// </summary>
    private static void FinishOwn(HeroController hero) {
        var stretch = _own!;
        _own = null;
        Logger.Info(stretch.Describe(ClipOf(), Situation(hero)));
    }

    /// <summary>
    /// The clip that the local character plays.
    /// </summary>
    private static string ClipOf() {
        return _heroAnimator != null && _heroAnimator.CurrentClip != null ? _heroAnimator.CurrentClip.name : "";
    }

    /// <summary>
    /// Whether the player has the control of the local character and its input, and whether it stands.
    /// </summary>
    private static string Situation(HeroController hero) {
        return $"control {(hero.controlReqlinquished ? "taken" : "kept")}, input " +
               $"{(hero.acceptingInput ? "on" : "off")}, {(hero.cState.onGround ? "on the ground" : "in the air")}";
    }

    /// <summary>
    /// The moves of the local character while the player doesn't steer it, from the first until it stands still again.
    /// </summary>
    private sealed class Stretch(float start, Vector2 from, string clip, string situation) {
        /// <summary>
        /// When the first move came, in seconds of game time.
        /// </summary>
        public readonly float Start = start;

        /// <summary>
        /// When the last move came, in seconds of game time.
        /// </summary>
        public float LastMove = start;

        /// <summary>
        /// Where the character began.
        /// </summary>
        private readonly Vector2 _from = from;

        /// <summary>
        /// Where the character is after the last move.
        /// </summary>
        private Vector2 _to = from;

        /// <summary>
        /// How far it went in all, in units.
        /// </summary>
        private float _path;

        /// <summary>
        /// How far it was at most above, below, to the left and to the right of where it began, in units.
        /// </summary>
        private float _up, _down, _left, _right;

        /// <summary>
        /// Its greatest speed up and down, in units a second.
        /// </summary>
        private float _fastestUp, _fastestDown;

        /// <summary>
        /// How many times it went the other way up or down, and which way it went last: 1 up, -1 down, 0 neither yet.
        /// </summary>
        private int _turns, _going;

        /// <summary>
        /// What moved it, how far each did and in how many frames.
        /// </summary>
        private readonly Dictionary<string, (float Path, int Frames)> _causes = new();

        /// <summary>
        /// Takes a frame in which it moved, by what <see cref="MoveWatch.Moved"/> holds.
        /// </summary>
        public void Take(Vector2 position, Vector2 frame, Vector2 speed, float now) {
            LastMove = now;
            _to = position;
            _path += frame.magnitude;
            var off = position - _from;
            _up = Mathf.Max(_up, off.y);
            _down = Mathf.Max(_down, -off.y);
            _right = Mathf.Max(_right, off.x);
            _left = Mathf.Max(_left, -off.x);
            _fastestUp = Mathf.Max(_fastestUp, speed.y);
            _fastestDown = Mathf.Max(_fastestDown, -speed.y);
            TakeTurn(ref _turns, ref _going, frame.y);

            foreach (var pair in Moved) {
                var had = _causes.TryGetValue(pair.Key, out var sum) ? sum : default;
                _causes[pair.Key] = (had.Path + pair.Value.magnitude, had.Frames + 1);
            }
        }

        /// <summary>
        /// The line of the stretch.
        /// </summary>
        public string Describe(string clipNow, string situationNow) {
            var line = new StringBuilder();
            line.Append($"[Moves] The character moved for {LastMove - Start:F2} s while the player didn't steer it ");
            line.Append($"({situation}, clip '{clip}'; at the end {situationNow}, clip '{clipNow}'): from ");
            line.Append($"({_from.x:F2}, {_from.y:F2}) to ({_to.x:F2}, {_to.y:F2}), {_path:F2} in all, up to ");
            line.Append($"{_up:F2} up, {_down:F2} down, {_left:F2} left and {_right:F2} right of where it began, ");
            line.Append($"turning between up and down {_turns} time(s), its speed up to {_fastestUp:F1} up and ");
            line.Append($"{_fastestDown:F1} down; moved by ");
            line.Append(string.Join("; ", _causes.OrderByDescending(pair => pair.Value.Path).Take(CausesNamed)
                .Select(pair => $"{pair.Key} {pair.Value.Path:F2} in {pair.Value.Frames} frame(s)")));
            return line.ToString();
        }
    }

    /// <summary>
    /// Counts a turn between up and down of a character, by how far it went up in a frame.
    /// </summary>
    private static void TakeTurn(ref int turns, ref int going, float up) {
        var now = up > MoveDistance ? 1 : up < -MoveDistance ? -1 : 0;
        if (now == 0) {
            return;
        }

        if (going != 0 && now != going) {
            turns++;
        }

        going = now;
    }

    /// <summary>
    /// Watches how the character of another player moves in this game (see Fsm.PredictiveInterpolation.Moves).
    /// </summary>
    /// <param name="who">The name of the player.</param>
    internal sealed class FigureMoves(string who) {
        /// <summary>
        /// Whether a frame was taken yet.
        /// </summary>
        private bool _has;

        /// <summary>
        /// Where the character was drawn in the last frame.
        /// </summary>
        private Vector2 _lastDrawn;

        /// <summary>
        /// Where the latest position of the player had the character in the last frame.
        /// </summary>
        private Vector2 _lastTheirs;

        /// <summary>
        /// Since when the character has stood still here, in seconds of game time.
        /// </summary>
        private float _stillSince;

        /// <summary>
        /// The stretch of moves of the character from where it stood still, if one goes on.
        /// </summary>
        private FigureStretch? _stretch;

        /// <summary>
        /// Takes a frame of the character: where it is drawn, where the latest position of the player has it, and
        /// what this game draws it by on top of that.
        /// </summary>
        /// <param name="drawn">Where it is drawn.</param>
        /// <param name="theirs">Where the latest position of the player has it.</param>
        /// <param name="onBy">How far it went on from there by itself.</param>
        /// <param name="smoothing">How far the smoothing draws it from where it goes.</param>
        /// <param name="carried">How far this game carries it.</param>
        /// <param name="snapped">Whether it was put straight where a position has it since the last frame.</param>
        public void Frame(Vector3 drawn, Vector3 theirs, Vector3 onBy, Vector3 smoothing, Vector3 carried,
            bool snapped) {
            var now = Time.time;
            if (!_has) {
                _has = true;
                _lastDrawn = drawn;
                _lastTheirs = theirs;
                _stillSince = now;
                return;
            }

            var frame = (Vector2) drawn - _lastDrawn;
            var moved = frame.sqrMagnitude > MoveDistance * MoveDistance;
            if (moved) {
                if (_stretch == null && now - _stillSince >= StoodStill) {
                    _stretch = new FigureStretch(now, _lastDrawn, _lastTheirs);
                }

                _stillSince = now;
            }

            if (_stretch != null) {
                _stretch.Take(drawn, theirs, frame, (Vector2) theirs - _lastTheirs, onBy, smoothing, carried, snapped,
                    moved, now);
                if (now - _stretch.LastMove >= StillEnds || now - _stretch.Start >= LongestStretch) {
                    var stretch = _stretch;
                    _stretch = null;
                    if (stretch.Worth()) {
                        Logger.Info(stretch.Describe(who));
                    }
                }
            }

            _lastDrawn = drawn;
            _lastTheirs = theirs;
        }
    }

    /// <summary>
    /// The moves of the character of another player in this game from where it stood still, until it stands still
    /// again.
    /// </summary>
    private sealed class FigureStretch(float start, Vector2 from, Vector2 theirFrom) {
        /// <summary>
        /// When the first move came, in seconds of game time.
        /// </summary>
        public readonly float Start = start;

        /// <summary>
        /// When the last move came, in seconds of game time.
        /// </summary>
        public float LastMove = start;

        /// <summary>
        /// Where the character stood.
        /// </summary>
        private readonly Vector2 _from = from;

        /// <summary>
        /// Where the character is drawn after the last move.
        /// </summary>
        private Vector2 _to = from;

        /// <summary>
        /// How far it was drawn going in all, and how far the positions of the player went in all, in units.
        /// </summary>
        private float _path, _theirPath;

        /// <summary>
        /// How far it was drawn at most above, below, to the left and to the right of where it stood, in units.
        /// </summary>
        private float _up, _down, _left, _right;

        /// <summary>
        /// How far the positions of the player had it at most above and below where they had it as it stood, in units.
        /// </summary>
        private float _theirUp, _theirDown;

        /// <summary>
        /// How far it was drawn at most from where the latest position of the player had it, and how far at most each
        /// part of that went: going on by itself, the smoothing and what this game carries it by, in units.
        /// </summary>
        private float _apart, _onBy, _smoothing, _carried;

        /// <summary>
        /// How many times it was put straight where a position had it.
        /// </summary>
        private int _snaps;

        /// <summary>
        /// How many times it was drawn going the other way up or down, and which way it went last.
        /// </summary>
        private int _turns, _going;

        /// <summary>
        /// Takes a frame of the stretch.
        /// </summary>
        public void Take(Vector3 drawn, Vector3 theirs, Vector2 frame, Vector2 theirFrame, Vector3 onBy,
            Vector3 smoothing, Vector3 carried, bool snapped, bool moved, float now) {
            if (moved) {
                LastMove = now;
                _to = drawn;
                _path += frame.magnitude;
                TakeTurn(ref _turns, ref _going, frame.y);
            }

            var off = (Vector2) drawn - _from;
            _up = Mathf.Max(_up, off.y);
            _down = Mathf.Max(_down, -off.y);
            _right = Mathf.Max(_right, off.x);
            _left = Mathf.Max(_left, -off.x);
            _theirPath += theirFrame.magnitude;
            var theirOff = (Vector2) theirs - theirFrom;
            _theirUp = Mathf.Max(_theirUp, theirOff.y);
            _theirDown = Mathf.Max(_theirDown, -theirOff.y);
            _apart = Mathf.Max(_apart, ((Vector2) (drawn - theirs)).magnitude);
            _onBy = Mathf.Max(_onBy, ((Vector2) onBy).magnitude);
            _smoothing = Mathf.Max(_smoothing, ((Vector2) smoothing).magnitude);
            _carried = Mathf.Max(_carried, ((Vector2) carried).magnitude);
            if (snapped) {
                _snaps++;
            }
        }

        /// <summary>
        /// Whether the stretch goes in a line: it went far enough to be seen and stayed on the spot, or this game drew
        /// it while the positions of the player held it still.
        /// </summary>
        public bool Worth() {
            var farthest = Mathf.Max(Mathf.Max(_up, _down), Mathf.Max(_left, _right));
            var onTheSpot = _left < OnTheSpot && _right < OnTheSpot && (_to - _from).magnitude < OnTheSpot;
            return farthest >= SeenDistance && (onTheSpot || _theirPath < MoveDistance);
        }

        /// <summary>
        /// The line of the stretch.
        /// </summary>
        public string Describe(string who) {
            return $"[Moves] {who}'s character moved here for {LastMove - Start:F2} s from where it stood at " +
                   $"({_from.x:F2}, {_from.y:F2}) to ({_to.x:F2}, {_to.y:F2}), {_path:F2} in all: up to {_up:F2} " +
                   $"up, " +
                   $"{_down:F2} down, {_left:F2} left and {_right:F2} right, turning between up and down {_turns} " +
                   $"time(s); their positions went {_theirPath:F2} (up to {_theirUp:F2} up and {_theirDown:F2} " +
                   $"down), and this game drew it up to {_apart:F2} from the latest of them (going on by itself up " +
                   $"to {_onBy:F2}, smoothing up to {_smoothing:F2}, carried up to {_carried:F2}, put straight " +
                   $"there {_snaps} time(s))";
        }
    }
}
