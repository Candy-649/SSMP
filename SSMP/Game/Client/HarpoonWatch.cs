using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Writes down each throw of the local player's harpoon near one of the creatures that each player has to themselves
/// (see PersonalPlaces: the flies to bounce off and the crystal creatures), and each bounce off one of them that a hook
/// gives. For a throw: where the player was, which way they faced and how they were moving, what came of it, and for each
/// such creature near them where it was against them, where it flies back to and where it was as the room loaded. For a
/// bounce: where it started, how far that was off where the creature lives, how high it took the player and how fast.
///
/// USER 10-25: the flies of the snowy peaks were out of reach of the harpoon many times over, and nothing said where
/// they had been ("所以你根本不知道那三只的位置嘛"); and then "勾上个怪的时候就已经有点低了，基本上攀升不到那个高度":
/// the bounce off one fly left the player too low for the next. The harpoon flies level ahead of the player, along three
/// lines that start at the 'Ray Origin' variables of the hero's "Harpoon Dash" and reach 10.5, and hooks the first
/// creature on one of them; a hooked creature with a HarpoonHook puts the player 1.5 above it and bounces them up as off a
/// down attack (the FSM's "Hook Bounce"). Only written down, nothing changed.
/// </summary>
internal static class HarpoonWatch {
    /// <summary>
    /// The name of the hero's harpoon FSM.
    /// </summary>
    private const string FsmName = "Harpoon Dash";

    /// <summary>
    /// How near the player a creature has to be for a throw to be written down with it.
    /// </summary>
    private const float Near = 16f;

    /// <summary>
    /// How long a bounce is watched at most, in seconds.
    /// </summary>
    private const float BounceWatchTime = 2.5f;

    /// <summary>
    /// The states of the harpoon FSM up to the aim, which takes the moment the needle is thrown: the states it goes
    /// through then pass in one frame, and the needle's own (Air, Enemy, Wall or Tink Needle, then Suspend or Hit Wall)
    /// with them, up to Throw, which plays the throw. A ring goes on to Ring Connect, Thread? and Ring Throw instead.
    /// A throw given up during its antic goes through Cancel All and the like to Wait or Idle, which throw nothing.
    /// </summary>
    private static readonly string[] AimStates = ["Antic", "Dir", "Ring Check L", "Ring Check R", "Left", "Right"];

    /// <summary>
    /// The creatures near the player, filled for each throw.
    /// </summary>
    private static readonly List<PersonalPlaces.StruckThing> NearThings = [];

    /// <summary>
    /// The line being written.
    /// </summary>
    private static readonly StringBuilder Line = new();

    private static HeroController? _hero;
    private static PlayMakerFSM? _fsm;
    private static Rigidbody2D? _body;

    /// <summary>
    /// The state of the harpoon FSM in the last frame.
    /// </summary>
    private static string? _state;

    /// <summary>
    /// Whether the lines of the harpoon were written down already, which is once.
    /// </summary>
    private static bool _linesSaid;

    /// <summary>
    /// Whether watching threw, after which it stops.
    /// </summary>
    private static bool _failed;

    /// <summary>
    /// Whether a bounce off a creature is being watched.
    /// </summary>
    private static bool _bouncing;

    private static string? _bounceOff;
    private static Vector2 _bounceStart;
    private static float? _bounceOffHome;
    private static float _bounceTop;
    private static float _bounceTopX;
    private static float _bounceTopTime;
    private static float _bounceFastest;
    private static float _bounceStartTime;

    /// <summary>
    /// Whether the FSM let go of the player for the bounce, from when the rise is timed.
    /// </summary>
    private static bool _bounceRising;

    /// <summary>
    /// Starts watching, every frame.
    /// </summary>
    public static void Initialize() {
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
    }

    private static void OnUpdate() {
        if (_failed) {
            return;
        }

        try {
            Watch();
        } catch (Exception e) {
            _failed = true;
            Logger.Error($"[Harpoon] Watching the harpoon threw, so it stops:\n{e}");
        }
    }

    private static void Watch() {
        var hero = HeroController.SilentInstance;
        if (hero == null) {
            _hero = null;
            _fsm = null;
            _body = null;
            _state = null;
            _bouncing = false;
            return;
        }

        if (hero != _hero) {
            _hero = hero;
            _fsm = null;
            foreach (var fsm in hero.GetComponents<PlayMakerFSM>()) {
                if (fsm.FsmName == FsmName) {
                    _fsm = fsm;
                    break;
                }
            }

            _body = hero.GetComponent<Rigidbody2D>();
            _state = null;
            _bouncing = false;
        }

        if (_fsm == null) {
            return;
        }

        var state = _fsm.ActiveStateName;
        if (state != _state) {
            var was = _state;
            _state = state;

            if (state == "Antic") {
                EndBounce("a new throw");
            }

            if (was != null && Array.IndexOf(AimStates, was) >= 0 && Array.IndexOf(AimStates, state) < 0 &&
                (state == "Throw" || state == "Thread?" || state.StartsWith("Ring", StringComparison.Ordinal))) {
                TellThrow(hero, state);
            }

            if (state == "Hook Bounce") {
                StartBounce(hero);
            }
        }

        if (_bouncing) {
            WatchBounce(hero);
        }
    }

    /// <summary>
    /// Writes down the throw that was just aimed, if one of the creatures was near or hooked.
    /// </summary>
    /// <param name="hero">The local player's hero.</param>
    /// <param name="state">The state the harpoon FSM went to from its aim.</param>
    private static void TellThrow(HeroController hero, string state) {
        var vars = _fsm!.FsmVariables;
        var at = (Vector2) hero.transform.position;
        var hooked = vars.FindFsmBool("Hit Enemy") is { Value: true }
            ? CreatureOf(vars.FindFsmGameObject("Enemy")?.Value)
            : null;

        PersonalPlaces.FindStruckThingsNear(at, Near, NearThings);
        if (NearThings.Count == 0 && (hooked == null || !PersonalPlaces.IsThingPart(hooked))) {
            return;
        }

        var right = hero.cState.facingRight;
        var ahead = right ? 1f : -1f;

        Line.Clear();
        Line.Append("[Harpoon] Thrown ").Append(right ? "right" : "left").Append(" from ").Append(Place(at))
            .Append(", moving ").Append(Place(_body != null ? _body.velocity : Vector2.zero)).Append(": ");
        if (state != "Throw") {
            Line.Append("took a ring");
        } else if (hooked != null) {
            Line.Append("hooked '").Append(hooked.name).Append('\'');
        } else if (vars.FindFsmGameObject("Tink Candidate")?.Value is { } tink && tink != null &&
                   tink.GetComponent<TinkEffect>() != null) {
            // The needle's Tink Needle passes in the same frame, so only what it struck tells
            Line.Append("struck '").Append(tink.name).Append("' and bounced off");
        } else if (vars.FindFsmBool("Hit Wall") is { Value: true }) {
            Line.Append("hit a wall");
        } else {
            Line.Append("hooked nothing");
        }

        Line.Append(" (").Append(state).Append(')');

        if (!_linesSaid) {
            _linesSaid = true;
            Line.Append("; the harpoon's lines start at");
            for (var index = 1; index <= 3; index++) {
                var origin = vars.FindFsmVector2($"Ray Origin {index}");
                Line.Append(' ').Append(origin == null ? "?" : Place(origin.Value));
            }

            Line.Append(" from the player, x ahead, and reach 10.5");
        }

        foreach (var near in NearThings) {
            var thing = near.Thing;
            var position = (Vector2) thing.transform.position;
            Line.Append("; '").Append(thing.name).Append("' ");

            if (!thing.activeInHierarchy || BodyOf(thing) is not { } body) {
                Line.Append("not there (dead, or not back yet)");
            } else {
                Line.Append(Offset((position.x - at.x) * ahead)).Append(" ahead, its body ")
                    .Append(Offset(body.min.y - at.y)).Append(" to ").Append(Offset(body.max.y - at.y))
                    .Append(" up, at ").Append(Place(position));
            }

            if (HomeOf(thing) is { } home) {
                Line.Append(", flies back to ").Append(Place(home));
            }

            Line.Append(", at ").Append(Place(near.Start)).Append(" as the room loaded");
        }

        Logger.Info(Line.ToString());
    }

    /// <summary>
    /// Starts watching the bounce off a creature that the harpoon hooked, if it is one of those each player has to
    /// themselves.
    /// </summary>
    private static void StartBounce(HeroController hero) {
        EndBounce("another bounce");

        var vars = _fsm!.FsmVariables;
        var creature = CreatureOf(vars.FindFsmGameObject("Enemy")?.Value);
        if (creature == null || !PersonalPlaces.IsThingPart(creature)) {
            return;
        }

        // The FSM puts the player 1.5 above where the creature is as the bounce starts
        _bounceStart = vars.FindFsmVector2("Enemy Pos")?.Value ?? (Vector2) hero.transform.position;
        _bounceOffHome = HomeOf(creature) is { } home ? _bounceStart.y - 1.5f - home.y : null;
        _bounceOff = creature.name;
        _bounceTop = _bounceStart.y;
        _bounceTopX = _bounceStart.x;
        _bounceTopTime = 0f;
        _bounceFastest = 0f;
        _bounceStartTime = Time.time;
        _bounceRising = false;
        _bouncing = true;
    }

    /// <summary>
    /// Follows the player up a bounce, until they come down again. While the FSM is still putting the player above the
    /// creature (Hook Bounce, then Hook Bounce End) nothing counts: a player who came from above it is moved down to it
    /// then (review).
    /// </summary>
    private static void WatchBounce(HeroController hero) {
        var elapsed = Time.time - _bounceStartTime;
        if (!_bounceRising) {
            if (_state is "Hook Bounce" or "Hook Bounce End") {
                if (elapsed > BounceWatchTime) {
                    EndBounce("the catch taking too long");
                }

                return;
            }

            _bounceRising = true;
            _bounceStartTime = Time.time;
            elapsed = 0f;
        }

        var position = (Vector2) hero.transform.position;
        var rising = _body != null ? _body.velocity.y : 0f;

        if (rising > _bounceFastest) {
            _bounceFastest = rising;
        }

        if (position.y > _bounceTop) {
            _bounceTop = position.y;
            _bounceTopX = position.x;
            _bounceTopTime = elapsed;
        }

        if (elapsed > BounceWatchTime) {
            EndBounce(null);
        } else if (_bounceTop > _bounceStart.y + 0.3f && rising <= 0f && position.y < _bounceTop - 0.05f) {
            EndBounce(null);
        }
    }

    /// <summary>
    /// Writes down the bounce being watched, if there is one.
    /// </summary>
    /// <param name="cutShortBy">What cut it short before the player came down, or null.</param>
    private static void EndBounce(string? cutShortBy) {
        if (!_bouncing) {
            return;
        }

        _bouncing = false;

        Line.Clear();
        Line.Append("[Harpoon] The hook on '").Append(_bounceOff).Append("' bounced the player from ")
            .Append(Place(_bounceStart));
        if (_bounceOffHome is { } off) {
            Line.Append(" (the creature ").Append(Offset(off)).Append(" off the height it lives at)");
        }

        Line.Append(" up to ").Append(Number(_bounceTop)).Append(" (").Append(Offset(_bounceTop - _bounceStart.y))
            .Append(") in ").Append(_bounceTopTime.ToString("0.00", CultureInfo.InvariantCulture)).Append(" s, ")
            .Append(Offset(_bounceTopX - _bounceStart.x)).Append(" across, rising at ")
            .Append(Number(_bounceFastest)).Append(" a second at most");
        if (cutShortBy != null) {
            Line.Append(", cut short by ").Append(cutShortBy);
        }

        Logger.Info(Line.ToString());
    }

    /// <summary>
    /// The creature that a part hooked by the harpoon is of: the object with its health, as a part of it (its damage
    /// collider) can be hooked too.
    /// </summary>
    private static GameObject? CreatureOf(GameObject? part) {
        if (part == null) {
            return null;
        }

        var health = part.GetComponentInParent<HealthManager>();
        return health != null ? health.gameObject : part;
    }

    /// <summary>
    /// What the harpoon's lines can hit of a creature: all its own colliders that are switched on together, or null if
    /// none is (dead or not back yet).
    /// </summary>
    private static Bounds? BodyOf(GameObject creature) {
        Bounds? body = null;
        foreach (var collider in creature.GetComponents<Collider2D>()) {
            if (!collider.enabled) {
                continue;
            }

            if (body is { } sofar) {
                sofar.Encapsulate(collider.bounds);
                body = sofar;
            } else {
                body = collider.bounds;
            }
        }

        return body;
    }

    /// <summary>
    /// Where a creature flies back to: the 'Start Pos' of its FSM, which it takes as it starts.
    /// </summary>
    private static Vector3? HomeOf(GameObject creature) {
        foreach (var fsm in creature.GetComponents<PlayMakerFSM>()) {
            if (fsm.FsmVariables.FindFsmVector3("Start Pos") is { } home) {
                return home.Value;
            }
        }

        return null;
    }

    private static string Place(Vector2 place) => $"({Number(place.x)}, {Number(place.y)})";

    private static string Place(Vector3 place) => Place((Vector2) place);

    private static string Number(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Offset(float value) => value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
}
