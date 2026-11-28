using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity;
using SSMP.Game.Client.Save;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using RunFSM = HutongGames.PlayMaker.Actions.RunFSM;

namespace SSMP.Game.Client;

/// <summary>
/// The [Creatures] lines of the log: every state that a state machine of a creature goes through in this game, one line
/// as it leaves the state, with how long it was in it, where the creature (or the part of it with the state machine)
/// was as it came into it and as it left, how far it went each way meanwhile, what took it on, its health and whom it
/// goes after (USER 11-25 "所有怪，日志里面记录位置移动，出招之类的，日志量会很大"). The lines of two games side by side show
/// where a creature that every game runs by itself went otherwise. A creature here is one that every game runs by
/// itself, or an entity, of which only the state machines that run in this game are seen: those of its own object
/// where this game runs it, and those that a copy plays by itself.
/// </summary>
internal static class CreatureMoves {
    /// <summary>
    /// The most lines a second, past which the rest of that second's are counted rather than written: a state machine
    /// that goes round its states every frame would fill the log.
    /// </summary>
    private const int MaxLinesPerSecond = 400;

    /// <summary>
    /// How long, in seconds, a state machine found to be of no creature is taken as such, before it is looked at again:
    /// an object can become an entity once the room settles.
    /// </summary>
    private const float NotCreatureTime = 2f;

    /// <summary>
    /// The state machines of creatures seen in this room, each with the state it is in and where it went in it.
    /// </summary>
    private static readonly Dictionary<HutongGames.PlayMaker.Fsm, Track> Tracks = new();

    /// <summary>
    /// The state machines found to be of no creature since <see cref="_notCreaturesSince"/>.
    /// </summary>
    private static readonly HashSet<HutongGames.PlayMaker.Fsm> NotCreatures = [];

    /// <summary>
    /// Since when, by Time.unscaledTime, <see cref="NotCreatures"/> holds what it holds.
    /// </summary>
    private static float _notCreaturesSince;

    /// <summary>
    /// The hook on states being entered.
    /// </summary>
    private static Hook? _enterHook;

    /// <summary>
    /// Whether the frames are watched for where the creatures go (see <see cref="OnUpdate"/>).
    /// </summary>
    private static bool _watching;

    /// <summary>
    /// The second, by Time.unscaledTime cut down to whole seconds, that <see cref="_lines"/> counts the lines of.
    /// </summary>
    private static int _second;

    /// <summary>
    /// How many lines were written in <see cref="_second"/>, and how many were left out past the most.
    /// </summary>
    private static int _lines, _leftOut;

    /// <summary>
    /// Whether a line failed to be made, which is told once.
    /// </summary>
    private static bool _failed;

    /// <summary>
    /// Hooks states being entered, and forgets the creatures of a room as it is left.
    /// </summary>
    internal static void Initialize() {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        _enterHook = new Hook(
            typeof(FsmState).GetMethod("OnEnter", flags, null, Type.EmptyTypes, null),
            new Action<Action<FsmState>, FsmState>(OnEnter)
        );
        SceneManager.activeSceneChanged += (_, _) => {
            Tracks.Clear();
            NotCreatures.Clear();
        };
    }

    /// <summary>
    /// Hook for a state being entered, which writes the line of the state that a creature's state machine leaves.
    /// </summary>
    private static void OnEnter(Action<FsmState> orig, FsmState self) {
        try {
            if (self.Fsm is { } fsm) {
                Note(fsm, self.Name, () => CauseOf(fsm, self));
            }
        } catch (Exception e) {
            Failed(e);
        }

        orig(self);
    }

    /// <summary>
    /// Notes that the copy of an entity here was told that a state machine of the creature went into a state in the
    /// game that runs it, which the copy shows rather than runs, and writes the line of the state that it leaves.
    /// </summary>
    internal static void NoteShown(HutongGames.PlayMaker.Fsm fsm, string state) {
        // A state that the copy is in already: the same word again, or a state machine that the copy plays itself
        if (Tracks.TryGetValue(fsm, out var track) && track.State == state) {
            return;
        }

        try {
            Note(fsm, state, static () => ", as the game that runs it told");
        } catch (Exception e) {
            Failed(e);
        }
    }

    /// <summary>
    /// Says once that a line failed to be made.
    /// </summary>
    private static void Failed(Exception e) {
        if (!_failed) {
            _failed = true;
            Logger.Error($"Could not write where a creature went:\n{e}");
        }
    }

    /// <summary>
    /// Notes that a state machine goes into a state, and writes the line of the state that it leaves if it is a
    /// creature's, with what took it there.
    /// </summary>
    private static void Note(HutongGames.PlayMaker.Fsm fsm, string stateName, Func<string> cause) {
        var now = Time.time;
        if (!Tracks.TryGetValue(fsm, out var track)) {
            if (Time.unscaledTime - _notCreaturesSince > NotCreatureTime) {
                NotCreatures.Clear();
                _notCreaturesSince = Time.unscaledTime;
            }

            if (NotCreatures.Contains(fsm) || fsm.GameObject is not { } body || Find(body) is not { } found) {
                NotCreatures.Add(fsm);
                return;
            }

            track = found;
            track.Machine = MachineName(fsm);
            Tracks[fsm] = track;
            WatchFrames();
            Start(track, stateName, now);
            Write(
                $"[Creatures] '{track.Name}' '{track.Machine}' is first seen in '{stateName}' at " +
                $"({track.From.x:F1}, {track.From.y:F1}){cause()}{About(track)}"
            );
            return;
        }

        if (track.Body == null) {
            return;
        }

        var at = (Vector2) track.Body.transform.position;
        Grow(track, at);
        Write(
            $"[Creatures] '{track.Name}' '{track.Machine}': '{track.State}' {now - track.Since:F2} s, " +
            $"({track.From.x:F1}, {track.From.y:F1}) to ({at.x:F1}, {at.y:F1}), " +
            $"x {track.Min.x:F1}..{track.Max.x:F1}, y {track.Min.y:F1}..{track.Max.y:F1}; " +
            $"then '{stateName}'{cause()}{About(track)}"
        );
        Start(track, stateName, now);
    }

    /// <summary>
    /// The track of a state machine on an object if the object is a creature or a part of one, or null: one that every
    /// game runs by itself, named by its place in the room, or an entity, named by its kind and number.
    /// </summary>
    private static Track? Find(GameObject body) {
        if (OwnCreatures.Owning(body) is { } own) {
            var name = OwnCreatures.Has(body) || OwnCreatures.IsPart(body)
                ? OwnCreatures.PlaceOf(body)
                : $"{OwnCreatures.PlaceOf(own)}/{body.name}";
            return new Track(body, name, own, own.GetComponent<HealthManager>());
        }

        if (Entity.Entity.FindByPart(body) is { } entity) {
            var copy = entity.Object.Client != null && body.transform.IsChildOf(entity.Object.Client.transform);
            return new Track(
                body, $"{entity.Type} {entity.Id}{(copy ? " (copy)" : "")} {body.name}", null,
                body.GetComponentInParent<HealthManager>()
            );
        }

        return null;
    }

    /// <summary>
    /// The name of a state machine for the log. One that a state of another runs as a part of it (RunFSM) is named by
    /// that state machine, the state and the template that it is made of: its states are none of the one that runs
    /// it, though some have the same names, like 'Grab Start' of the grab with which a creature's grab hurts and holds
    /// the player, and were written as that one's.
    /// </summary>
    private static string MachineName(HutongGames.PlayMaker.Fsm fsm) {
        if (fsm.Host is not { } host) {
            return fsm.Name;
        }

        foreach (var state in host.States) {
            foreach (var action in state.Actions) {
                if (action is RunFSM { fsmTemplateControl.fsmTemplate: { } template } run &&
                    Entity.Action.EntityFsmActions.SubFsmOf(run) == fsm) {
                    return $"{host.Name}' > '{state.Name}' > '{template.name}";
                }
            }
        }

        return $"{host.Name}' > '{fsm.Name}";
    }

    /// <summary>
    /// Begins the track of a state.
    /// </summary>
    private static void Start(Track track, string state, float now) {
        var at = (Vector2) track.Body.transform.position;
        track.State = state;
        track.Since = now;
        track.From = at;
        track.Min = at;
        track.Max = at;
    }

    /// <summary>
    /// Takes a place of a creature into how far it went each way in its state.
    /// </summary>
    private static void Grow(Track track, Vector2 at) {
        track.Min = Vector2.Min(track.Min, at);
        track.Max = Vector2.Max(track.Max, at);
    }

    /// <summary>
    /// What took a state machine into a state, for the log: the event, and what sent it if another state machine did,
    /// and a blow that it came at.
    /// </summary>
    private static string CauseOf(HutongGames.PlayMaker.Fsm fsm, FsmState state) {
        var way = fsm.LastTransition is { } last && last.ToState == state.Name
            ? $" by '{last.EventName}'"
            : " set straight there";
        var teller = HutongGames.PlayMaker.Fsm.EventData?.SentByFsm;
        var told = teller != null && teller != fsm
            ? $" from '{teller.Name}' of '{(teller.GameObject != null ? teller.GameObject.name : "?")}'"
            : "";
        var blow = CoopHits.IsMakingLocalStrike ? ", at a blow of the local player"
            : CoopHits.IsReplayingPartnersStrike ? ", at a blow of another player"
            : "";
        return way + told + blow;
    }

    /// <summary>
    /// The health of a creature and whom it goes after, for the log.
    /// </summary>
    private static string About(Track track) {
        var health = track.Health != null ? $", hp {track.Health.hp}" : "";
        var target = track.Own != null && GamePatcher.TargetOf(track.Own) is { } player
            ? player == (HeroController.SilentInstance is { } hero ? hero.gameObject : null)
                ? ", after the local player"
                : $", after {player.name}"
            : "";
        return health + target;
    }

    /// <summary>
    /// Has the frames watched for where the creatures go between their steps, once there is one.
    /// </summary>
    private static void WatchFrames() {
        if (_watching || MonoBehaviourUtil.Instance == null) {
            return;
        }

        _watching = true;
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
    }

    /// <summary>
    /// Takes where each creature of the room is now into how far it went each way in its state.
    /// </summary>
    private static void OnUpdate() {
        foreach (var track in Tracks.Values) {
            if (track.Body != null) {
                Grow(track, track.Body.transform.position);
            }
        }
    }

    /// <summary>
    /// Writes a line, unless the lines of this second are many already, which are counted and told once the second
    /// is over.
    /// </summary>
    private static void Write(string line) {
        var second = (int) Time.unscaledTime;
        if (second != _second) {
            if (_leftOut > 0) {
                Logger.Info($"[Creatures] {_leftOut} lines left out in the second before, past {MaxLinesPerSecond}");
            }

            _second = second;
            _lines = 0;
            _leftOut = 0;
        }

        if (++_lines > MaxLinesPerSecond) {
            _leftOut++;
            return;
        }

        Logger.Info(line);
    }

    /// <summary>
    /// A state machine of a creature: where it is in it, and how it went.
    /// </summary>
    private sealed class Track(GameObject body, string name, GameObject? own, HealthManager? health) {
        /// <summary>
        /// The object of the state machine, whose place is followed.
        /// </summary>
        public readonly GameObject Body = body;

        /// <summary>
        /// The name of the creature, or of the part, for the log.
        /// </summary>
        public readonly string Name = name;

        /// <summary>
        /// The creature that every game runs by itself that it is, or is a part of, or null for an entity.
        /// </summary>
        public readonly GameObject? Own = own;

        /// <summary>
        /// The health of the creature, or null for one with none.
        /// </summary>
        public readonly HealthManager? Health = health;

        /// <summary>
        /// The name of the state machine, for the log (see <see cref="MachineName"/>).
        /// </summary>
        public string Machine = "";

        /// <summary>
        /// The state it is in.
        /// </summary>
        public string State = "";

        /// <summary>
        /// When it came into the state, by Time.time.
        /// </summary>
        public float Since;

        /// <summary>
        /// Where it was as it came into the state, and how far it went each way since.
        /// </summary>
        public Vector2 From, Min, Max;
    }
}
