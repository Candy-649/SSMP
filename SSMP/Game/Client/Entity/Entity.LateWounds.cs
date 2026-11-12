using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity.Action;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// A wound of the partner's that comes after the room's own creature went on from the state that their copy took it
/// in. Their copy showed the creature in that state as they struck, and answered the wound at once (D14), while the
/// FSM here had gone on, mostly from waiting into the start of its next move, where a wound leads nowhere. It was not
/// taken, and their copy cut its answer short and jumped back to the creature here, turning about and starting another
/// move before their eyes (USER 11-04 "这局boss经常很奇怪的转向", four of the five answers of the fight that this was
/// for). Now the FSM here is put back in the state their copy took it in, if it went on from there only by itself,
/// through nothing that it leaves behind, and not long ago, with what it went through since as if it had never been:
/// the numbers of its FSM that those states set, and how fast the creature moved, are put back as they were there (see
/// <see cref="PutBackWhereTheWoundCameIn"/>). The wound is taken from there with the partner's dice, so that the
/// creature goes the way it went there. What it had gone on to here is cut off instead, which the scene host's player
/// sees as the partner's blow breaking into the start of a move. A catch of the partner's for a combo that comes
/// late is put back the same way, to the very step of the FSM that their copy took it in, told by its number (see
/// <see cref="PutBackWhereTheCatchCameIn"/>). A wound that takes the creature into a choice of what it does next
/// their copy does not answer at all, and the creature here chooses (see <see cref="ChoosesOnAWound"/>).
/// </summary>
internal partial class Entity {
    /// <summary>
    /// How many of the ways that each FSM of a room's own creature went from one state to another are kept (see
    /// <see cref="FsmWays"/>): more than an FSM goes through between a state that a wound leads somewhere from and the
    /// first state of its next move, which for the boss of the fight that this was for is eight.
    /// </summary>
    private const int WaysKept = 16;

    /// <summary>
    /// How long ago, at most, the room's own creature may have left the state that the partner's copy took a wound in,
    /// in seconds, for the wound to be taken from there: longer than the round trips of the evening that this was for
    /// (0.36 to 0.79 s, 11-04), and short enough that a stalled connection does not take back a whole move that the
    /// scene host's player saw the creature make.
    /// </summary>
    private const float LateWoundAtMost = 1f;

    /// <summary>
    /// The ways that each FSM of a room's own creature went lately (see <see cref="OnDoTransition"/>), by the FSM's
    /// component: the game makes a new FSM for one that comes from a template when its object is first switched on.
    /// </summary>
    private static readonly Dictionary<PlayMakerFSM, FsmWays> RoomFsmWays = new();

    /// <summary>
    /// The hook that notes the ways of the FSMs of the room's own creatures, put in place with the first such FSM.
    /// </summary>
    private static Hook? _doTransitionHook;

    /// <summary>
    /// How far a state got in going into its actions: the index of the last one it went into (FsmState's
    /// activeActionIndex). The game goes into none of the rest once one has taken the FSM on, like a test of a
    /// yes-or-no that sends it to the next state at once, so they were never done.
    /// </summary>
    private static Func<FsmState, int>? _enteredUpTo;

    /// <summary>
    /// Whether each kind of action leaves nothing behind by its name (see <see cref="NothingLeftBehindName"/>), by
    /// kind.
    /// </summary>
    private static readonly Dictionary<Type, bool> NothingLeftBehindKinds = new();

    /// <summary>
    /// The events that each kind of action that plays a clip tells its FSM as the clip ends or reaches a frame, by kind
    /// (see <see cref="WentByItself"/>).
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> ClipEventFields = new();

    /// <summary>
    /// The fields of each kind of action that name numbers of its FSM, singly or in a list, by kind (see
    /// <see cref="PutBackWhatItSet"/>).
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> NumberFields = new();

    /// <summary>
    /// What the name of a kind of action has in it that only looks about and decides, with numbers of the FSM's own, or
    /// waits, of the kinds that the creatures' FSMs have (reports/fsm-usage.tsv). A name can have this in it and do more
    /// all the same - one that flips the creature at random - which what reads it looks for first.
    /// </summary>
    private const string DecidesPattern =
        "^(Get|Check|Compare|Find|Has|Can)[A-Z0-9]|^(GameObjectIs|GameObjectCompare|DistanceBetween|RayCast|" +
        "PlayerData(Bool|Int|Float)Test|Trigger2dEvent|Collision2dEvent|EnumCompare|SendRandomEvent)|" +
        "^(Bool|Float|Int|String|Vector[23]|Convert|Select)[A-Z]|^Random|^Ease(Float|Vector|Color)|^TimeLimit|" +
        "^(BuildString|MultiplyIntByFloat)$|^Set(Bool|Float|Int|String|Vector2|Vector3|Object)Value|" +
        "^Set(GameObject|FloatSign|Vector2XY|Vector3XYZ)$|" +
        "^(Wait|WaitRandom|WaitBool|NextFrameEvent|NextFixedFrameEvent|Comment)$";

    /// <summary>
    /// What the name of a kind of action has in it that leaves nothing behind once its state is left but the numbers of
    /// its FSM, how the creature looks, sounds and faces, and where it is and how fast it moves, which are put back or
    /// told the other game by themselves: one that looks about and decides, works with the FSM's own numbers or waits;
    /// one that plays a clip, a sound, particles, a shake or a flash; one that turns, moves or steers the creature. The
    /// kinds that the creatures' FSMs have of each were read off all of them (reports/fsm-usage.tsv).
    /// </summary>
    private static readonly Regex NothingLeftBehindName = new(
        DecidesPattern + "|" +
        // Plays a clip, a sound, particles, a shake or a flash
        "^(Tk2d|Animator|Audio|PlayAudio|PlayRandomAudio|SetAudio|SetRandomAudioClip|FadeAudio|RandomiseAudio|" +
        "PlayParticle|StopParticle|SetParticleEmission|ScreenFlash|ObjectJitter|EaseSpriteColor|" +
        "SetSpriteRendererColor|CancelCameraShake)|^DoCameraShake(V[0-9])?$|" +
        // Turns, moves or steers the creature
        "Velocity|^(Decelerat|Accelerat|Face|FlipScale|RandomlyFlipScale|MatchScaleSign|Rotate|Tilt|" +
        "Animate[XYZ]?Position|AnimateRotation|AnimateRigidBody2DPosition|Translate|AddForce|Chase|DistanceFly|" +
        "IdleBuzz|WalkLeftRight|ShoveFromWall|ClampSpeed|ClampAngle|ClampPosition|FireAtTarget|SetCrawlerSpeed|" +
        "(Start|Stop)(Walker|Crawler|Climber))|^Set(Rotation|Scale)$"
    );

    /// <summary>
    /// Notes from now on the ways that an FSM of the room's own creature goes (see <see cref="FsmWays"/>).
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    private void NoteTheWaysOf(PlayMakerFSM fsm) {
        RoomFsmWays[fsm] = new FsmWays(this, Object.Host.TryGetComponent<Rigidbody2D>(out var body) ? body : null);
        if (_doTransitionHook != null) {
            return;
        }

        var state = Expression.Parameter(typeof(FsmState), "state");
        var index = typeof(FsmState).GetField("activeActionIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;
        _enteredUpTo = Expression.Lambda<Func<FsmState, int>>(Expression.Field(state, index), state).Compile();
        _doTransitionHook = new Hook(
            typeof(HutongGames.PlayMaker.Fsm).GetMethod(
                "DoTransition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                [typeof(FsmTransition), typeof(bool)], null
            )!,
            new Func<Func<HutongGames.PlayMaker.Fsm, FsmTransition, bool, bool>, HutongGames.PlayMaker.Fsm,
                FsmTransition, bool, bool>(OnDoTransition)
        );
    }

    /// <summary>
    /// The number of the step by which an FSM of the room's own creature went into the state it is in, the number of
    /// the way it went in by (see <see cref="FsmWays"/>), which goes to the other games with the state
    /// (EntityHostFsmData.StateStep): zero if it went in by a way that is not noted.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    private static ushort StepOf(PlayMakerFSM fsm) {
        return RoomFsmWays.TryGetValue(fsm, out var ways) && ways.Count > 0 && ways.Back(0).To == fsm.Fsm.ActiveState
            ? (ushort) ways.Back(0).Number
            : (ushort) 0;
    }

    /// <summary>
    /// Hook for an FSM taking a way from its state to another, which notes it for an FSM of a room's own creature (see
    /// <see cref="Way"/>).
    /// </summary>
    private static bool OnDoTransition(
        Func<HutongGames.PlayMaker.Fsm, FsmTransition, bool, bool> orig,
        HutongGames.PlayMaker.Fsm self,
        FsmTransition transition,
        bool isGlobal
    ) {
        if (RoomFsmWays.Count > 0 && self.FsmComponent is { } component &&
            RoomFsmWays.TryGetValue(component, out var ways) && component.Fsm == self &&
            self.ActiveState is { } from && transition.ToFsmState is { } to) {
            ways.Add(
                self, from, _enteredUpTo!(from), to, transition.FsmEvent, HutongGames.PlayMaker.Fsm.EventData.SentByFsm,
                isGlobal
            );
        }

        return orig(self, transition, isGlobal);
    }

    /// <summary>
    /// Puts the FSM of the room's own creature back in the state that the partner's copy took a wound in, which leads
    /// nowhere from where the FSM is, on the scene host: if it left that state no longer ago than
    /// <see cref="LateWoundAtMost"/>, and went from there to where it is by itself all the way (see
    /// <see cref="WentByItself"/>), through states whose actions that it went into leave nothing behind (see
    /// <see cref="LeftBehindBy"/>). Not when something told it from outside took it on, like a wound of this game's
    /// player, a stun or another input of the partner's, which would be undone, nor when a state it went through set
    /// something that stays, like going without gravity, invincible or into a catch of this game's player, which would
    /// be left so. What its FSM held there, of the numbers that the states it went through work with, and how fast the
    /// creature moved there are put back: the partner's copy took the wound with those, and drops what it was told of
    /// them since once the wound is taken. Said in the log either way.
    /// </summary>
    /// <param name="hostFsm">The FSM's component.</param>
    /// <param name="fsm">The FSM.</param>
    /// <param name="at">The state it is in.</param>
    /// <param name="eventName">The wound event.</param>
    /// <param name="fromName">The state that the partner's copy took it in.</param>
    /// <returns>The state it is put back in, or null if it is not.</returns>
    private FsmState? PutBackWhereTheWoundCameIn(
        PlayMakerFSM hostFsm,
        HutongGames.PlayMaker.Fsm fsm,
        FsmState at,
        string eventName,
        string fromName
    ) {
        if (fromName == at.Name || fsm.GetState(fromName) is not { } from ||
            EntityFsmActions.FindTransition(fsm, from, eventName) == null ||
            !RoomFsmWays.TryGetValue(hostFsm, out var ways)) {
            return null;
        }

        var told = $"[Wounds] The '{fsm.Name}' of entity {Id} is in '{at.Name}' as the partner's '{eventName}' " +
                   $"comes, which their copy took in '{fromName}'";
        return PutBack(fsm, at, from, ways, null, told, "so it does not take it");
    }

    /// <summary>
    /// Puts the FSM of the room's own creature back in the step of it that the partner's copy took a catch for a combo
    /// in (see <see cref="CatchEvents"/>), on the scene host, once the FSM has gone on from that step: as a wound is
    /// put back (see <see cref="PutBackWhereTheWoundCameIn"/>), to the way by which it left that very step. Not when it
    /// left the step by a catch, of this game's player or of another's: the combo of that step is someone else's, and
    /// the creature plays none of it for the partner, whose game plays theirs on them by itself if it caught them (USER
    /// 11-11 "本地几乎肯定抓本地大黄蜂"). Each step is told apart by its number (see
    /// <see cref="FsmWays"/>), which the copy had of the scene host's FSM with the state it took the catch in. One
    /// that can't be put back there is taken where the FSM is, if it takes the catch there too: put back through a
    /// state of a combo of slashes that switches its hurting part, or past another state that takes a catch, it was
    /// not taken at all, and the boss let go of the partner it had caught as it went on to its next slash (review of
    /// ce8e5e6). Said in the log either way.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="ways">The ways of the FSM.</param>
    /// <param name="at">The state it is in.</param>
    /// <param name="eventName">The catch event.</param>
    /// <param name="start">What the partner's game sent with it: the state and the step that the copy took it in.
    /// </param>
    /// <param name="takenHere">Whether the state that the FSM is in takes the catch, for the log.</param>
    /// <param name="passedBy">Whether it left that step by a catch of someone else's, which plays none of the
    /// partner's.</param>
    /// <returns>The state it is put back in, or null if it is not.</returns>
    private FsmState? PutBackWhereTheCatchCameIn(
        HutongGames.PlayMaker.Fsm fsm,
        FsmWays ways,
        FsmState at,
        string eventName,
        InputStart start,
        bool takenHere,
        out bool passedBy
    ) {
        passedBy = false;
        var told = $"[Catches] The '{fsm.Name}' of entity {Id} is in '{at.Name}' as the partner's '{eventName}' " +
                   $"comes, which their copy took in '{start.From}' as step {start.FromStep} of it";
        var otherwise = takenHere ? "so it takes it where it is" : "so it does not take it";
        if (ways.LeftBy(start.FromStep) is not { } wayOut) {
            Logger.Info($"{told}, which it left longer ago than it notes, {otherwise}");
            return null;
        }

        if (wayOut.Event is { } left && CatchEvents.Contains(left.Name)) {
            passedBy = true;
            Logger.Info(
                $"{told}, which it left by a catch of someone else's, to '{wayOut.To.Name}', so it plays none " +
                "of it for the partner"
            );
            return null;
        }

        if (fsm.GetState(start.From) is not { } from || EntityFsmActions.FindTransition(fsm, from, eventName) == null) {
            Logger.Info($"{told}, which leads nowhere from there here, {otherwise}");
            return null;
        }

        return PutBack(fsm, at, from, ways, wayOut, told, otherwise);
    }

    /// <summary>
    /// Puts an FSM of the room's own creature back in the state that the partner's copy took something in, on the scene
    /// host: back through the ways it went since, to the given way out of that state - for a catch, whose step is known
    /// by its number - or else to the latest, if it went every one of them by itself (see <see cref="WentByItself"/>),
    /// through states whose actions that it went into leave nothing behind (see <see cref="LeftBehindBy"/>), and left
    /// that state no longer ago than <see cref="LateWoundAtMost"/>. What its FSM held there is put back (see
    /// <see cref="PutBackWhatItSet"/>).
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="at">The state it is in.</param>
    /// <param name="from">The state that the partner's copy took it in.</param>
    /// <param name="ways">The ways of the FSM.</param>
    /// <param name="wayOut">The way it left that state by, for a catch; null for a wound.</param>
    /// <param name="told">What the log says of it first.</param>
    /// <param name="otherwise">What the log says becomes of it if it is not put back.</param>
    /// <returns>The state it is put back in, or null if it is not.</returns>
    private FsmState? PutBack(
        HutongGames.PlayMaker.Fsm fsm,
        FsmState at,
        FsmState from,
        FsmWays ways,
        Way? wayOut,
        string told,
        string otherwise
    ) {
        var (held, takes) = wayOut == null ? (ways.Held, "wounds") : (ways.HeldForACatch, "catches");
        var reached = at;
        var reachedEntered = _enteredUpTo!(at);
        for (var back = 0; back < ways.Count; back++) {
            var way = ways.Back(back);
            if (way.To != reached) {
                Logger.Info($"{told}, and it came to '{reached.Name}' by a way it does not note, {otherwise}");
                return null;
            }

            if (!WentByItself(fsm, way)) {
                Logger.Info(
                    $"{told}, and it went from '{way.From.Name}' to '{way.To.Name}' by what came from outside it, " +
                    otherwise
                );
                return null;
            }

            if (LeftBehindBy(reached, reachedEntered) is { } kind) {
                Logger.Info($"{told}, and it went through '{reached.Name}', whose {kind} stays, {otherwise}");
                return null;
            }

            if (wayOut is { } outOf ? way.Number != outOf.Number : way.From != from) {
                reached = way.From;
                reachedEntered = way.FromEntered;
                continue;
            }

            if (way.From != from) {
                Logger.Info($"{told}, and it was in '{way.From.Name}' in that step here, {otherwise}");
                return null;
            }

            var ago = Time.unscaledTime - way.At;
            if (ago > LateWoundAtMost) {
                Logger.Info($"{told}, which it left {ago:0.00} s ago, {otherwise}");
                return null;
            }

            if (held.Number != way.Number) {
                Logger.Info($"{told}, which it left before another state that takes {takes}, {otherwise}");
                return null;
            }

            // A catch of the partner's that it followed before would keep it from going back there (LetsLedFsmGo)
            EndLeadOf(fsm);
            PutInWithoutDoing(fsm, from);
            var putBack = PutBackWhatItSet(ways, held, at, back);
            Logger.Info(
                $"{told}, which it left {ago:0.00} s ago by itself through {back + 1} state(s) that leave nothing " +
                $"behind, so it takes it from there, with {putBack} number(s) of its FSM put back as they were there"
            );
            return from;
        }

        Logger.Info($"{told}, which it left longer ago than it notes, {otherwise}");
        return null;
    }

    /// <summary>
    /// Whether an FSM of a room's own creature went a way by itself: by an event that it told itself as it ran, like
    /// the end of its state or of what it waited for, or by one that an action of the state it left told it as the clip
    /// it played ended or reached a frame, which the game tells it as the clip plays, apart from its FSM; not by one
    /// that something else told it, like a wound, a stun or an input of the partner's, nor by a way that every state
    /// takes. (An event that the game hands an FSM without saying who sent it, like one told every FSM at once, keeps
    /// the sender of the one told before it.)
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="way">The way.</param>
    private static bool WentByItself(HutongGames.PlayMaker.Fsm fsm, Way way) {
        if (way.IsGlobal) {
            return false;
        }

        if (way.SentBy != null || way.Event == null) {
            return way.SentBy == fsm;
        }

        var actions = way.From.Actions;
        for (var i = 0; i <= way.FromEntered && i < actions.Length; i++) {
            var action = actions[i];
            if (!action.Enabled) {
                continue;
            }

            foreach (var field in ClipEventFieldsOf(action.GetType())) {
                if (field.GetValue(action) is FsmEvent told && told.Name == way.Event.Name) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The events that a kind of action that plays a clip tells its FSM as the clip ends or reaches a frame.
    /// </summary>
    /// <param name="kind">The kind of action.</param>
    private static FieldInfo[] ClipEventFieldsOf(Type kind) {
        if (!ClipEventFields.TryGetValue(kind, out var fields)) {
            ClipEventFields[kind] = fields = kind.Name.StartsWith("Tk2d", StringComparison.Ordinal)
                ? Array.FindAll(
                    kind.GetFields(BindingFlags.Instance | BindingFlags.Public),
                    field => field.FieldType == typeof(FsmEvent) &&
                             field.Name.StartsWith("animation", StringComparison.OrdinalIgnoreCase)
                )
                : [];
        }

        return fields;
    }

    /// <summary>
    /// The first kind of action that a state went into, of those up to the given index, that leaves something behind
    /// once the state is left, or null if none does: any but those whose names say they leave nothing behind (see
    /// <see cref="NothingLeftBehindName"/>), a part that is switched on or off for the state only, and an event told to
    /// the FSM itself at once.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="enteredUpTo">The index of the last action that the state went into (see
    /// <see cref="_enteredUpTo"/>).</param>
    private static string? LeftBehindBy(FsmState state, int enteredUpTo) {
        var actions = state.Actions;
        for (var i = 0; i <= enteredUpTo && i < actions.Length; i++) {
            var action = actions[i];
            if (!action.Enabled) {
                continue;
            }

            var leavesNothing = action switch {
                ActivateGameObject activate => activate.resetOnExit,
                SendEvent send => send.eventTarget.target == FsmEventTarget.EventTarget.Self &&
                                  send.delay.Value <= 0f,
                SendEventByName send => send.eventTarget.target == FsmEventTarget.EventTarget.Self &&
                                        send.delay.Value <= 0f,
                _ => LeavesNothingBehindByName(action.GetType())
            };
            if (!leavesNothing) {
                return action.GetType().Name;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a kind of action leaves nothing behind by its name (see <see cref="NothingLeftBehindName"/>).
    /// </summary>
    /// <param name="kind">The kind of action.</param>
    private static bool LeavesNothingBehindByName(Type kind) {
        if (!NothingLeftBehindKinds.TryGetValue(kind, out var leavesNothing)) {
            NothingLeftBehindKinds[kind] = leavesNothing = NothingLeftBehindName.IsMatch(kind.Name);
        }

        return leavesNothing;
    }

    /// <summary>
    /// Puts back what an FSM of a room's own creature held, and how fast the creature moved, as it left the state that
    /// it is put back in (see <see cref="HeldThere"/>): every number of the FSM that an action that a state it went
    /// through since went into works with. The rest is left as it is, which something else may have set meanwhile.
    /// </summary>
    /// <param name="ways">The ways of the FSM.</param>
    /// <param name="held">What it held as it left the state that it is put back in.</param>
    /// <param name="at">The state that it was in.</param>
    /// <param name="last">How many ways back from the newest the way out of the state it is put back in is.</param>
    /// <returns>How many numbers were put back.</returns>
    private static int PutBackWhatItSet(FsmWays ways, HeldThere held, FsmState at, int last) {
        var putBack = 0;
        for (var back = 0; back <= last; back++) {
            var state = back == 0 ? at : ways.Back(back - 1).From;
            var enteredUpTo = back == 0 ? _enteredUpTo!(at) : ways.Back(back - 1).FromEntered;
            var actions = state.Actions;
            for (var i = 0; i <= enteredUpTo && i < actions.Length; i++) {
                var action = actions[i];
                if (!action.Enabled) {
                    continue;
                }

                foreach (var field in NumberFieldsOf(action.GetType())) {
                    switch (field.GetValue(action)) {
                        case NamedVariable { UseVariable: true } number:
                            putBack += held.PutBack(number) ? 1 : 0;
                            break;
                        case NamedVariable[] numbers:
                            foreach (var number in numbers) {
                                putBack += number is { UseVariable: true } && held.PutBack(number) ? 1 : 0;
                            }

                            break;
                    }
                }
            }
        }

        if (ways.Body != null) {
            ways.Body.linearVelocity = held.Velocity;
        }

        return putBack;
    }

    /// <summary>
    /// The fields of a kind of action that name numbers of its FSM, singly or in a list.
    /// </summary>
    /// <param name="kind">The kind of action.</param>
    private static FieldInfo[] NumberFieldsOf(Type kind) {
        if (!NumberFields.TryGetValue(kind, out var fields)) {
            NumberFields[kind] = fields = Array.FindAll(
                kind.GetFields(BindingFlags.Instance | BindingFlags.Public),
                field => typeof(NamedVariable).IsAssignableFrom(field.FieldType) ||
                         field.FieldType.IsArray &&
                         typeof(NamedVariable).IsAssignableFrom(field.FieldType.GetElementType())
            );
        }

        return fields;
    }

    /// <summary>
    /// Whether a wound leads anywhere from a state by a way of its own.
    /// </summary>
    /// <param name="state">The state.</param>
    private static bool HasWoundWay(FsmState state) {
        foreach (var transition in state.Transitions) {
            if (IsWoundEvent(transition.EventName)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a catch for a combo leads anywhere from a state by a way of its own (see <see cref="CatchEvents"/>).
    /// </summary>
    /// <param name="state">The state.</param>
    private static bool HasCatchWay(FsmState state) {
        foreach (var transition in state.Transitions) {
            if (CatchEvents.Contains(transition.EventName)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The last ways that an FSM of a room's own creature went from one state to another, as many as
    /// <see cref="WaysKept"/> (see <see cref="OnDoTransition"/>), numbered one after another, and what it held as it
    /// last left a state that a wound leads somewhere from, and one that a catch for a combo does.
    /// </summary>
    /// <param name="owner">The entity whose FSM it is.</param>
    /// <param name="body">The body of the creature, if it has one.</param>
    private sealed class FsmWays(Entity owner, Rigidbody2D? body) {
        /// <summary>
        /// The ways, going round: the next one is written over the oldest.
        /// </summary>
        private readonly Way[] _ways = new Way[WaysKept];

        /// <summary>
        /// Where the next way goes in <see cref="_ways"/>.
        /// </summary>
        private int _next;

        /// <summary>
        /// How many ways have been noted, which numbers each.
        /// </summary>
        private int _noted;

        /// <summary>
        /// The entity whose FSM it is.
        /// </summary>
        public Entity Owner { get; } = owner;

        /// <summary>
        /// The body of the creature, if it has one.
        /// </summary>
        public Rigidbody2D? Body { get; } = body;

        /// <summary>
        /// How many ways there are.
        /// </summary>
        public int Count { get; private set; }

        /// <summary>
        /// What the FSM held as it last left a state that a wound leads somewhere from.
        /// </summary>
        public HeldThere Held { get; } = new();

        /// <summary>
        /// What the FSM held as it last left a state that a catch for a combo leads somewhere from (see
        /// <see cref="CatchEvents"/>).
        /// </summary>
        public HeldThere HeldForACatch { get; } = new();

        /// <summary>
        /// Notes a way, and what the FSM holds as it goes, if it leaves a state that a wound or a catch for a combo
        /// leads somewhere from.
        /// </summary>
        public void Add(
            HutongGames.PlayMaker.Fsm fsm,
            FsmState from,
            int fromEntered,
            FsmState to,
            FsmEvent? fsmEvent,
            HutongGames.PlayMaker.Fsm? sentBy,
            bool isGlobal
        ) {
            var number = ++_noted;
            _ways[_next] = new Way(from, fromEntered, to, fsmEvent, sentBy, isGlobal, Time.unscaledTime, number);
            _next = (_next + 1) % WaysKept;
            Count = Mathf.Min(Count + 1, WaysKept);
            if (HasWoundWay(from)) {
                Held.Take(fsm, Body, number);
            }

            if (HasCatchWay(from)) {
                HeldForACatch.Take(fsm, Body, number);
            }
        }

        /// <summary>
        /// The way that came the given number of ways before the newest, which is 0.
        /// </summary>
        public Way Back(int back) => _ways[(_next - 1 - back + WaysKept) % WaysKept];

        /// <summary>
        /// Whether the FSM is in the step with the given number still: the newest way is the one it went in by.
        /// </summary>
        /// <param name="step">The number, as the other games have it (see <see cref="StepOf"/>).</param>
        public bool IsIn(ushort step) => Count > 0 && (ushort) Back(0).Number == step;

        /// <summary>
        /// The way by which the FSM left the step with the given number, the next one after it, or null if that is
        /// not among those kept: it is in that step still, or left it longer ago than they go back.
        /// </summary>
        /// <param name="step">The number, as the other games have it (see <see cref="StepOf"/>).</param>
        public Way? LeftBy(ushort step) {
            for (var back = 0; back < Count; back++) {
                var way = Back(back);
                if ((ushort) (way.Number - 1) == step) {
                    return way;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// A way that an FSM of a room's own creature went: from which state, the index of the last action of it that it
    /// went into (see <see cref="_enteredUpTo"/>), to which state, by which event, which FSM was running as the event was
    /// told, or none, whether it is a way that every state takes, when it went in unscaled time, and its number (see
    /// <see cref="FsmWays"/>).
    /// </summary>
    private readonly record struct Way(
        FsmState From,
        int FromEntered,
        FsmState To,
        FsmEvent? Event,
        HutongGames.PlayMaker.Fsm? SentBy,
        bool IsGlobal,
        float At,
        int Number
    );

    /// <summary>
    /// What an FSM of a room's own creature held as it left a state, of its numbers, yes-or-no's, words, points and
    /// objects, and how fast the creature moved then, with the number of the way it left by (see
    /// <see cref="FsmWays"/>).
    /// </summary>
    private sealed class HeldThere {
        /// <summary>
        /// The FSM whose variables the lists below are of, which the game makes anew for one that comes from a
        /// template.
        /// </summary>
        private HutongGames.PlayMaker.Fsm? _of;

        private FsmFloat[] _floats = [];
        private float[] _floatValues = [];
        private FsmInt[] _ints = [];
        private int[] _intValues = [];
        private FsmBool[] _bools = [];
        private bool[] _boolValues = [];
        private FsmString[] _strings = [];
        private string[] _stringValues = [];
        private FsmVector2[] _vector2s = [];
        private Vector2[] _vector2Values = [];
        private FsmVector3[] _vector3s = [];
        private Vector3[] _vector3Values = [];
        private FsmGameObject[] _gameObjects = [];
        private GameObject?[] _gameObjectValues = [];

        /// <summary>
        /// The number of the way that the FSM left the state by.
        /// </summary>
        public int Number { get; private set; }

        /// <summary>
        /// How fast the creature moved.
        /// </summary>
        public Vector2 Velocity { get; private set; }

        /// <summary>
        /// Takes what an FSM holds now, and how fast the creature moves.
        /// </summary>
        public void Take(HutongGames.PlayMaker.Fsm fsm, Rigidbody2D? body, int number) {
            if (_of != fsm) {
                var variables = fsm.Variables;
                _floats = variables.FloatVariables;
                _floatValues = new float[_floats.Length];
                _ints = variables.IntVariables;
                _intValues = new int[_ints.Length];
                _bools = variables.BoolVariables;
                _boolValues = new bool[_bools.Length];
                _strings = variables.StringVariables;
                _stringValues = new string[_strings.Length];
                _vector2s = variables.Vector2Variables;
                _vector2Values = new Vector2[_vector2s.Length];
                _vector3s = variables.Vector3Variables;
                _vector3Values = new Vector3[_vector3s.Length];
                _gameObjects = variables.GameObjectVariables;
                _gameObjectValues = new GameObject?[_gameObjects.Length];
                _of = fsm;
            }

            for (var i = 0; i < _floats.Length; i++) {
                _floatValues[i] = _floats[i].Value;
            }

            for (var i = 0; i < _ints.Length; i++) {
                _intValues[i] = _ints[i].Value;
            }

            for (var i = 0; i < _bools.Length; i++) {
                _boolValues[i] = _bools[i].Value;
            }

            for (var i = 0; i < _strings.Length; i++) {
                _stringValues[i] = _strings[i].Value;
            }

            for (var i = 0; i < _vector2s.Length; i++) {
                _vector2Values[i] = _vector2s[i].Value;
            }

            for (var i = 0; i < _vector3s.Length; i++) {
                _vector3Values[i] = _vector3s[i].Value;
            }

            for (var i = 0; i < _gameObjects.Length; i++) {
                _gameObjectValues[i] = _gameObjects[i].Value;
            }

            Velocity = body != null ? body.linearVelocity : Vector2.zero;
            Number = number;
        }

        /// <summary>
        /// Puts back what a variable of the FSM held, if it is one of those taken and holds something else now.
        /// </summary>
        /// <param name="variable">The variable, as an action names it.</param>
        /// <returns>Whether it was put back.</returns>
        public bool PutBack(NamedVariable variable) {
            var name = variable.Name;
            int i;
            switch (variable) {
                case FsmFloat when (i = IndexOf(_floats, name)) >= 0 && _floats[i].Value != _floatValues[i]:
                    _floats[i].Value = _floatValues[i];
                    return true;
                case FsmInt when (i = IndexOf(_ints, name)) >= 0 && _ints[i].Value != _intValues[i]:
                    _ints[i].Value = _intValues[i];
                    return true;
                case FsmBool when (i = IndexOf(_bools, name)) >= 0 && _bools[i].Value != _boolValues[i]:
                    _bools[i].Value = _boolValues[i];
                    return true;
                case FsmString when (i = IndexOf(_strings, name)) >= 0 && _strings[i].Value != _stringValues[i]:
                    _strings[i].Value = _stringValues[i];
                    return true;
                case FsmVector2 when (i = IndexOf(_vector2s, name)) >= 0 && _vector2s[i].Value != _vector2Values[i]:
                    _vector2s[i].Value = _vector2Values[i];
                    return true;
                case FsmVector3 when (i = IndexOf(_vector3s, name)) >= 0 && _vector3s[i].Value != _vector3Values[i]:
                    _vector3s[i].Value = _vector3Values[i];
                    return true;
                case FsmGameObject when (i = IndexOf(_gameObjects, name)) >= 0 &&
                                        _gameObjects[i].Value != _gameObjectValues[i]:
                    _gameObjects[i].Value = _gameObjectValues[i];
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The index of the variable with the given name in a list of them, or -1.
        /// </summary>
        private static int IndexOf(NamedVariable[] variables, string name) {
            for (var i = 0; i < variables.Length; i++) {
                if (variables[i].Name == name) {
                    return i;
                }
            }

            return -1;
        }
    }
}
