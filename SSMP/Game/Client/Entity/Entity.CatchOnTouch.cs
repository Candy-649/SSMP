using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using SSMP.Game.Client.Entity.Action;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// A creature catching the local player with what it watches for them itself. Most creatures that hold a player grab
/// them with a part of their own, which runs in each game and tells the creature (HearCopyTold). Some watch for the
/// player in the very FSM that then carries them off, like the creature that drops its cage on the player and takes
/// them to the prison. That FSM runs only in the game of the player who runs the room, where the other player is a
/// figure that nothing touches, so only the player who came into the room first could ever be caught there (USER
/// 10-14: "可以吧", to letting the player who comes in later be caught too, both still taken to the prison -
/// CoopSave.PrisonCapture). With the events of such a catch listed for the creature
/// (<see cref="EntityRegistryEntry.CatchEvents"/>), the game of the player who came in later watches what the copy's
/// FSM watches in the state the scene host says it is in, and the player touching it there is a catch that their game
/// leads (see <see cref="PlayHere"/>): it plays all of it for them at once, and the scene host follows it, leaving its
/// own player out (see <see cref="PlayForPartner"/>). Such a catch ends with the player carried off, so a creature
/// whose catch of the partner's is over on the scene host with no way out of it goes away (see
/// <see cref="GoAwayWithTheCatch"/>).
/// </summary>
internal partial class Entity {
    /// <summary>
    /// The events with which the FSMs of the entity catch the player who touches what they watch for them, or null
    /// (see <see cref="ListenForCatches"/>).
    /// </summary>
    private HashSet<string>? _catchEvents;

    /// <summary>
    /// The FSM and the state of it that the local player was last caught in by this copy, so that a catch that did not
    /// take is not tried again every frame while the scene host says its FSM is still there. Forgotten as soon as the
    /// scene host says it is anywhere else (see <see cref="CheckCaughtByCopy"/>).
    /// </summary>
    private (byte FsmIndex, FsmState State)? _caughtIn;

    /// <summary>
    /// What the local player had before the catch that the copy plays here for this game took it from them, or null
    /// (see <see cref="PlayerBeforeTheCatch"/>).
    /// </summary>
    private PlayerBeforeTheCatch? _beforeTheCatch;

    /// <summary>
    /// Raised when a catch that the creature watched for itself, which the copy played here for this game, was cut
    /// short (see <see cref="GiveBackWhatTheCatchTook"/>): it let go of the local player without taking them where it
    /// would have, whether or not anything had to be given back to them. CoopSave tells the other players, whom the
    /// catch held too.
    /// </summary>
    public static event System.Action? CatchGivenBack;

    /// <summary>
    /// The tag of the hit box of the player, which is what a creature that waits for the player to be hurt or caught
    /// watches for.
    /// </summary>
    private const string HitBoxTag = "HeroBox";

    /// <summary>
    /// The fields of each kind of action that watches a trigger: the object it watches, the tag of what it waits for,
    /// the event it sends when that comes in, and whether that is something coming in, staying or leaving, by type;
    /// null for a kind without them.
    /// </summary>
    private static readonly Dictionary<Type, (FieldInfo Watched, FieldInfo? Tag, FieldInfo Event, FieldInfo? Contact)?>
        TriggerFields = new();

    /// <summary>
    /// What each catch that a creature watches for itself takes from the player themselves, or null for nothing (see
    /// <see cref="WhatACatchTakes"/>), by the first state of the catch, found the first time it catches the local
    /// player.
    /// </summary>
    private static readonly ConditionalWeakTable<FsmState, StrongBox<WhatACatchTakes?>> WhatCatchesTake = new();

    /// <summary>
    /// What <see cref="PlayerBeforeTheCatch"/> calls an action that hides the player character.
    /// </summary>
    private const string HideName = "(hide)";

    /// <summary>
    /// The global variable that holds what shows the player their health and silk, which a creature that holds the
    /// player sends away and the game brings back with "IN".
    /// </summary>
    private const string HudCanvasName = "HUD Canvas";

    /// <summary>
    /// How long the screen takes to come back when a catch that blacked it out is cut short, in seconds.
    /// </summary>
    private const float ScreenBackTime = 0.5f;

    /// <summary>
    /// Makes the copy of this entity catch the local player with the given events when they touch what its FSM watches
    /// for them (see <see cref="CheckCaughtByCopy"/>), and the room's own entity take such a catch of the partner's
    /// from where the creature watched for it (see <see cref="StateTheCatchWasWatchedFrom"/>).
    /// </summary>
    /// <param name="events">The events.</param>
    public void ListenForCatches(IEnumerable<string> events) {
        _catchEvents = new HashSet<string>(events);
    }

    /// <summary>
    /// Looks, once a frame on a scene client, whether the local player touches what an FSM of the copy watches for them
    /// in the state the scene host says that FSM is in, to catch them with one of the events of the entity (see
    /// <see cref="ListenForCatches"/>). The copy then plays the catch here at once as one that this game leads, and the
    /// scene host is sent it (CoopHits.PlayOrSend): the stomp that a creature drops its cage with lands here, on the
    /// player it lands on, and the scene host's creature follows what it does to them.
    /// </summary>
    private void CheckCaughtByCopy() {
        if (_catchEvents == null) {
            return;
        }

        // Also while the local player can't be caught: a creature that comes back to where it watches for them, while
        // they could not be caught, has to catch them there again
        if (_caughtIn is { } tried && (tried.FsmIndex >= _fsms.Client.Count ||
                                       _fsms.Client[tried.FsmIndex] is not { } triedFsm || triedFsm == null ||
                                       EntityFsmActions.HostStateOf(triedFsm.Fsm) != tried.State.Name)) {
            _caughtIn = null;
        }

        if (CopyTouchedLocalPlayer == null || _runHere != null || _runHereTalk || Object.Client == null ||
            !Object.Client.activeInHierarchy || !CanBeCaughtHere()) {
            return;
        }

        for (var index = 0; index < _fsms.Client.Count && index < byte.MaxValue; index++) {
            var copyFsm = _fsms.Client[index];
            if (copyFsm == null || copyFsm.enabled || StateOfCopy(copyFsm) is not { } state ||
                _caughtIn is { } caught && caught.FsmIndex == index && caught.State == state) {
                continue;
            }

            if (CatchWatchedIn(copyFsm.Fsm, state) is not { } eventName) {
                continue;
            }

            _caughtIn = ((byte) index, state);
            Logger.Info(
                $"The local player touched what the copy of entity {Id} ({Type}) watches for them in '{state.Name}', " +
                $"which catches them with '{eventName}'"
            );
            CopyTouchedLocalPlayer(this, (byte) index, eventName, ToldValues.None, true);
            return;
        }
    }

    /// <summary>
    /// Whether the local player can be caught by a copy now: they are there, alive and not lying in their cocoon, not
    /// on their way to another room, and no other copy holds them.
    /// </summary>
    private static bool CanBeCaughtHere() {
        var hero = HeroController.SilentInstance;
        return hero != null && !hero.cState.dead && !hero.cState.hazardDeath && !hero.cState.hazardRespawning &&
               !hero.cState.transitioning && !PlayerTargetRegistry.IsPlayerDown(hero.gameObject) && !AnyLeadsACatch();
    }

    /// <summary>
    /// The event with which an FSM catches the local player in the given state, if it watches something for them there
    /// with one of the catch events of the entity, which they touch right now, and the event leads anywhere from there.
    /// What waits for something to leave is passed over: touching it is not what sets it off.
    /// </summary>
    /// <param name="fsm">The FSM of the copy.</param>
    /// <param name="state">The state the scene host says it is in.</param>
    /// <returns>The event, or null.</returns>
    private string? CatchWatchedIn(HutongGames.PlayMaker.Fsm fsm, FsmState state) {
        foreach (var action in state.Actions) {
            if (!action.Enabled || TriggerFieldsOf(action.GetType()) is not { } fields ||
                fields.Contact?.GetValue(action)?.ToString().IndexOf("Exit", StringComparison.Ordinal) >= 0 ||
                fields.Event.GetValue(action) is not FsmEvent { Name: { Length: > 0 } eventName } ||
                !_catchEvents!.Contains(eventName) || EntityFsmActions.FindTransition(fsm, state, eventName) == null ||
                fields.Watched.GetValue(action) is not FsmOwnerDefault watched ||
                fsm.GetOwnerDefaultTarget(watched) is not { } watchedObject || !watchedObject.activeInHierarchy) {
                continue;
            }

            var tag = fields.Tag?.GetValue(action) is FsmString { Value: { } value } ? value : null;
            if (TouchesTheLocalPlayer(watchedObject, tag == HitBoxTag)) {
                return eventName;
            }
        }

        return null;
    }

    /// <summary>
    /// The fields of a kind of action that watches a trigger (see <see cref="TriggerFields"/>), or null.
    /// </summary>
    private static (FieldInfo Watched, FieldInfo? Tag, FieldInfo Event, FieldInfo? Contact)? TriggerFieldsOf(
        Type type
    ) {
        if (TriggerFields.TryGetValue(type, out var known)) {
            return known;
        }

        (FieldInfo, FieldInfo?, FieldInfo, FieldInfo?)? found = null;
        if (type.Name.StartsWith("Trigger2dEvent", StringComparison.Ordinal) &&
            type.GetField("gameObject", BindingFlags.Instance | BindingFlags.Public) is { } watched &&
            watched.FieldType == typeof(FsmOwnerDefault) &&
            type.GetField("sendEvent", BindingFlags.Instance | BindingFlags.Public) is { } sent &&
            sent.FieldType == typeof(FsmEvent)) {
            var tag = type.GetField("collideTag", BindingFlags.Instance | BindingFlags.Public);
            var contact = type.GetField("trigger", BindingFlags.Instance | BindingFlags.Public);
            found = (
                watched,
                tag != null && tag.FieldType == typeof(FsmString) ? tag : null,
                sent,
                contact != null && contact.FieldType.IsEnum ? contact : null
            );
        }

        TriggerFields[type] = found;
        return found;
    }

    /// <summary>
    /// The state that a catch of the partner's, made where they are by what the creature watches for them (see
    /// <see cref="CheckCaughtByCopy"/>), is taken from on the scene host when its FSM can no longer take it from where
    /// it is: the one state from which the event leads to the first state of the catch. What the creature watches for
    /// the player there lasts less than the round trip it takes the catch to come here - a stomp is over as the
    /// creature lands - so its FSM has mostly gone on by then, and the catch was the partner's all the same. Not taken
    /// while the creature holds this game's own player in that catch, or anybody's, nor once it is dead.
    /// </summary>
    /// <param name="fsm">The FSM of the room's own object.</param>
    /// <param name="at">The state it is in.</param>
    /// <param name="eventName">The event of the catch.</param>
    /// <param name="path">The states that the catch went through in the partner's game.</param>
    /// <returns>The state, or null if the catch is not taken.</returns>
    private FsmState? StateTheCatchWasWatchedFrom(
        HutongGames.PlayMaker.Fsm fsm,
        FsmState at,
        string eventName,
        string[] path
    ) {
        if (_catchEvents?.Contains(eventName) != true || path.Length == 0 || fsm.GetState(path[0]) is not { } first ||
            EntityFsmActions.StatesOnlyThrough(fsm, first).Contains(at) ||
            Object.Host != null && Object.Host.TryGetComponent<HealthManager>(out var health) && health.GetIsDead()) {
            return null;
        }

        FsmState? from = null;
        foreach (var state in fsm.States) {
            foreach (var transition in state.Transitions) {
                if (transition.EventName != eventName || transition.ToFsmState != first) {
                    continue;
                }

                if (from != null && from != state) {
                    return null;
                }

                from = state;
            }
        }

        return from;
    }

    /// <summary>
    /// Puts an FSM in a state without doing what the state does, as though it had come there a moment ago: what it does
    /// on going in was done in the other game, and the event that it is put there for takes it on at once.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="state">The state.</param>
    private static void PutInWithoutDoing(HutongGames.PlayMaker.Fsm fsm, FsmState state) {
        var actions = state.Actions;
        state.Actions = [new HoldHere()];
        try {
            fsm.SetState(state.Name);
        } finally {
            state.Actions = actions;
        }
    }

    /// <summary>
    /// Takes the room's own creature away, on the scene host, once a catch of the partner's that it followed is over
    /// with no way out of it (see <see cref="LetGo"/>), if it is one that watches for its catches itself (see
    /// <see cref="ListenForCatches"/>). Such a catch ends with the player carried off to another room, and all its
    /// states lead only further into it: left where it was, the creature played the rest of it on nobody, its cage
    /// standing in the room, its black wipe spread over the screen here and its own player's flags written, and then
    /// stood in its last state for as long as the room was there - also when the partner's game had given up on the
    /// catch, or left. Gone, it is back from the start of its FSM when the room is loaded again.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    private void GoAwayWithTheCatch(HutongGames.PlayMaker.Fsm fsm) {
        if (_catchEvents == null || Object.Host == null || !Object.Host.activeSelf) {
            return;
        }

        Logger.Info(
            $"The '{fsm.Name}' of entity {Id} ({Type}) has nobody left to carry off, so the creature goes away"
        );
        Object.Host.SetActive(false);
    }

    /// <summary>
    /// Notes what the local player has that a catch the creature watches for itself may take from them, as the FSM of
    /// the copy is about to play it for this game (see <see cref="PlayerBeforeTheCatch"/>).
    /// </summary>
    /// <param name="fsm">The FSM of the copy.</param>
    /// <param name="into">The first state of the catch.</param>
    private void NoteThePlayerBeforeTheCatch(HutongGames.PlayMaker.Fsm fsm, FsmState into) {
        _beforeTheCatch = PlayerBeforeTheCatch.Take(fsm, into);
    }

    /// <summary>
    /// Gives the local player back what the catch that the copy played here for this game took from them, if it was
    /// cut short (see <see cref="PlayerBeforeTheCatch"/>), unless another copy holds them or the game has them in a
    /// way that gives it back itself, and says that it was cut short either way (see <see cref="CatchGivenBack"/>).
    /// One that came to its end gave it back itself, or took the player to another room, where the game gives it back.
    /// </summary>
    /// <param name="cut">Whether the copy stopped before the catch came to its end: the scene host did not take the
    /// catch, never answered, or the copy went away while it held the player.</param>
    private void GiveBackWhatTheCatchTook(bool cut) {
        var before = _beforeTheCatch;
        _beforeTheCatch = null;
        if (!cut || before == null) {
            return;
        }

        if (!AnyLeadsACatch(this)) {
            before.GiveBack($"the copy of entity {Id} no longer plays the catch");
        }

        CatchGivenBack?.Invoke();
    }

    /// <summary>
    /// What the local player had before a catch that the creature watches for itself, which this game leads, of what
    /// the states of that catch take from them themselves and give back only at the end: their control, the drawing of
    /// their character and its falling, whether they can pause, open their inventory, use their skills or be hurt, the
    /// states of their character that it sets, the marks the game keeps of a player held in a cage, their screen,
    /// which it blacks out, and what shows them their health, which it sends away. A creature that holds the player
    /// through the grab of the player character's own FSM lets go of them with a release
    /// (EntityFsmActions.LetGoOfTheHeldLocalPlayer); one that takes all of this itself, like the one that cages the
    /// player, would give none of it back when its catch is cut short - the scene host did not take it, never answered,
    /// or the copy went away - and leave the player hidden in the air without control, unable to pause, in the dark,
    /// for good. Only what the states of the catch take is given back, and nothing while the game holds the player in
    /// another way that takes the same: lying in their cocoon, or sitting on a bench.
    /// </summary>
    private sealed class PlayerBeforeTheCatch {
        private readonly WhatACatchTakes _taken;
        private readonly bool _hadControl;
        private readonly bool _hadAnimationControl;
        private readonly bool _wasDrawn;
        private readonly bool _hadGravity;
        private readonly Color _screen;
        private readonly List<(string Name, bool Value)> _flags = [];
        private readonly List<(string Name, bool Value)> _states = [];
        private readonly List<(string Name, bool Value)> _marks = [];

        /// <summary>
        /// Whether the catch went into a state that sends away what shows the player their health.
        /// </summary>
        private bool _hudSentAway;

        private PlayerBeforeTheCatch(WhatACatchTakes taken, HeroController hero) {
            _taken = taken;
            var renderer = hero.GetComponent<MeshRenderer>();
            var animation = hero.GetComponent<HeroAnimationController>();
            _hadControl = !hero.controlReqlinquished;
            _hadAnimationControl = animation == null || animation.controlEnabled;
            _wasDrawn = renderer == null || renderer.enabled;
            _hadGravity = hero.IsGravityApplied;
            _screen = ScreenFaderUtils.GetColour();
        }

        /// <summary>
        /// Notes what the local player has of what the given catch takes, or returns null if there is no player or it
        /// takes nothing.
        /// </summary>
        /// <param name="fsm">The FSM of the copy that is about to play the catch.</param>
        /// <param name="into">The first state of the catch.</param>
        public static PlayerBeforeTheCatch? Take(HutongGames.PlayMaker.Fsm fsm, FsmState into) {
            var hero = HeroController.SilentInstance;
            var playerData = PlayerData.instance;
            if (hero == null || playerData == null || WhatItTakes(fsm, into) is not { } taken) {
                return null;
            }

            var before = new PlayerBeforeTheCatch(taken, hero);
            foreach (var flag in taken.Flags) {
                before._flags.Add((flag, playerData.GetBool(flag)));
            }

            foreach (var state in taken.States) {
                before._states.Add((state, hero.cState.GetState(state)));
            }

            foreach (var mark in taken.Marks) {
                before._marks.Add((mark, IsMarked(mark)));
            }

            return before;
        }

        /// <summary>
        /// Notes a state that the catch went into, whose actions ran.
        /// </summary>
        /// <param name="state">The state.</param>
        public void Entered(FsmState state) {
            _hudSentAway |= _taken.HudStates.Contains(state);
        }

        /// <summary>
        /// Gives the local player back what differs from what they had, unless they are dead or on their way to
        /// another room, which take care of it themselves, or the game holds them in another way (see
        /// <see cref="PlayerBeforeTheCatch"/>). What a hazard that puts the player back takes, it gives back itself.
        /// </summary>
        /// <param name="why">Why, for the log.</param>
        public void GiveBack(string why) {
            var hero = HeroController.SilentInstance;
            var playerData = PlayerData.instance;
            var gameManager = global::GameManager.instance;
            if (hero == null || playerData == null || hero.cState.dead || hero.cState.transitioning ||
                gameManager != null && gameManager.IsInSceneTransition) {
                return;
            }

            if (PlayerTargetRegistry.IsPlayerDown(hero.gameObject) || playerData.atBench) {
                Logger.Info(
                    $"Gave the local player back nothing of what the catch took, as the game holds them: {why}"
                );
                return;
            }

            var given = new List<string>();
            foreach (var (name, value) in _flags) {
                if (playerData.GetBool(name) != value) {
                    playerData.SetBool(name, value);
                    given.Add(name);
                }
            }

            foreach (var (name, value) in _states) {
                if (hero.cState.GetState(name) != value) {
                    hero.cState.SetState(name, value);
                    given.Add(name);
                }
            }

            // Kept until the next change of room, as the catch kept it
            foreach (var (name, value) in _marks) {
                if (IsMarked(name) != value) {
                    StaticVariableList.SetValue(name, value, 1);
                    given.Add(name);
                }
            }

            // Clear unless it was black already: taken while it still faded in from a change of rooms, it stayed dimmed
            var screenBack = _screen.a >= 1f ? _screen : new Color(_screen.r, _screen.g, _screen.b, 0f);
            if (_taken.Screen && ScreenFaderUtils.GetColour() is var screen && screen.a > screenBack.a) {
                ScreenFaderUtils.Fade(screen, screenBack, ScreenBackTime);
                given.Add("screen");
            }

            if (_hudSentAway && HudCanvas() is { } hud) {
                foreach (var hudFsm in hud.GetComponents<PlayMakerFSM>()) {
                    hudFsm.SendEvent("IN");
                }

                given.Add("health display");
            }

            if (!hero.cState.hazardDeath && !hero.cState.hazardRespawning) {
                GiveBackTheBody(hero, given);
            }

            if (given.Count > 0) {
                Logger.Info($"Gave the local player back {string.Join(", ", given)}: {why}");
            }
        }

        /// <summary>
        /// Gives the player character back its falling, its drawing, the player's control of it and the control of its
        /// animation, of what the catch takes, if the player had them before.
        /// </summary>
        private void GiveBackTheBody(HeroController hero, List<string> given) {
            if (_taken.Falling && _hadGravity && !hero.IsGravityApplied) {
                hero.AffectedByGravity(true);
                given.Add("falling");
            }

            var renderer = hero.GetComponent<MeshRenderer>();
            if (_taken.Drawing && _wasDrawn && renderer != null && !renderer.enabled) {
                renderer.enabled = true;
                given.Add("drawing");
            }

            if (_taken.Control && _hadControl && hero.controlReqlinquished) {
                hero.RegainControl();
                given.Add("control");
            }

            var animation = hero.GetComponent<HeroAnimationController>();
            if (_taken.Animation && _hadAnimationControl && animation != null && !animation.controlEnabled) {
                hero.StartAnimationControl();
                given.Add("animation");
            }
        }

        /// <summary>
        /// Whether the game keeps the given mark of the player as set (see <see cref="WhatACatchTakes.Marks"/>).
        /// </summary>
        private static bool IsMarked(string name) {
            return StaticVariableList.Exists(name) && StaticVariableList.GetValue(name) is true;
        }

        /// <summary>
        /// What a catch takes from the player itself (see <see cref="WhatACatchTakes"/>), or null if nothing, found
        /// from the states that only its first state leads to, the first time it catches the local player.
        /// </summary>
        /// <param name="fsm">The FSM.</param>
        /// <param name="into">The first state of the catch.</param>
        private static WhatACatchTakes? WhatItTakes(HutongGames.PlayMaker.Fsm fsm, FsmState into) {
            if (WhatCatchesTake.TryGetValue(into, out var known)) {
                return known.Value;
            }

            var flags = new HashSet<string>();
            var states = new HashSet<string>();
            var marks = new HashSet<string>();
            var hudStates = new HashSet<FsmState>();
            bool control = false, animation = false, drawing = false, falling = false, screen = false;
            var hud = HudCanvas();
            foreach (var state in EntityFsmActions.StatesOnlyThrough(fsm, into)) {
                foreach (var action in state.Actions) {
                    if (EntityFsmActions.HeroStateWrittenBy(action) is { } flag &&
                        typeof(PlayerData).GetField(flag, BindingFlags.Instance | BindingFlags.Public)?.FieldType ==
                        typeof(bool)) {
                        flags.Add(flag);
                        continue;
                    }

                    if (action is HutongGames.PlayMaker.Actions.SetHeroCState {
                            VariableName.Value: { Length: > 0 } name
                        } && typeof(HeroControllerStates).GetField(
                            name, BindingFlags.Instance | BindingFlags.Public
                        )?.FieldType == typeof(bool)) {
                        states.Add(name);
                        continue;
                    }

                    if (MarkSetBy(action) is { } mark) {
                        marks.Add(mark);
                        continue;
                    }

                    if (action is HutongGames.PlayMaker.Actions.ScreenFader { endColour.Value.a: >= 1f } or
                        HutongGames.PlayMaker.Actions.ScreenFaderAlpha { EndAlpha.Value: >= 1f }) {
                        screen = true;
                        continue;
                    }

                    if (hud != null && SendsAway(action, hud)) {
                        hudStates.Add(state);
                        continue;
                    }

                    switch (WhatItDoesToTheHero(action)) {
                        case "RelinquishControl":
                            control = true;
                            break;
                        case "StopAnimationControl":
                            animation = true;
                            break;
                        case "AffectedByGravity":
                            falling = true;
                            break;
                        case HideName:
                            drawing = true;
                            break;
                    }
                }
            }

            WhatACatchTakes? taken = flags.Count > 0 || states.Count > 0 || marks.Count > 0 || hudStates.Count > 0 ||
                                     screen || control || animation || drawing || falling
                ? new WhatACatchTakes(
                    [..flags], [..states], [..marks], hudStates, screen, control, animation, drawing, falling
                )
                : null;
            WhatCatchesTake.Add(into, new StrongBox<WhatACatchTakes?>(taken));
            return taken;
        }

        /// <summary>
        /// The name of the mark of the player that an action sets to true or false (see
        /// <see cref="WhatACatchTakes.Marks"/>), or null for any other action.
        /// </summary>
        private static string? MarkSetBy(FsmStateAction action) {
            var (name, value) = action switch {
                SetStaticVariable set => (set.variableName?.Value, set.setValue),
                SetStaticVariableV2 set => (set.variableName?.Value, set.setValue),
                _ => (null, null)
            };
            return !string.IsNullOrEmpty(name) && value is { Type: VariableType.Bool } ? name : null;
        }

        /// <summary>
        /// Whether an action sends away what shows the player their health, which is the given object.
        /// </summary>
        private static bool SendsAway(FsmStateAction action, GameObject hud) {
            var (target, sent) = action switch {
                HutongGames.PlayMaker.Actions.SendEventByName send => (send.eventTarget, send.sendEvent?.Value),
                HutongGames.PlayMaker.Actions.SendEventByNameV2 send => (send.eventTarget, send.sendEvent?.Value),
                _ => (null, null)
            };
            return sent is "OUT" or "OUT INSTANT" && target is {
                       target: FsmEventTarget.EventTarget.GameObject or FsmEventTarget.EventTarget.GameObjectFSM
                   } && action.Fsm?.GetOwnerDefaultTarget(target.gameObject) == hud;
        }

        /// <summary>
        /// What shows the player their health and silk, or null (see <see cref="HudCanvasName"/>).
        /// </summary>
        private static GameObject? HudCanvas() {
            var hud = FsmVariables.GlobalVariables.FindFsmGameObject(HudCanvasName)?.Value;
            return hud != null ? hud : null;
        }

        /// <summary>
        /// What an action does to the player character, by the name of the method of the character's that it calls -
        /// on its control, on the control of its animation, on its falling - or <see cref="HideName"/> if it hides it;
        /// null for anything else.
        /// </summary>
        private static string? WhatItDoesToTheHero(FsmStateAction action) {
            switch (action) {
                case HutongGames.PlayMaker.Actions.CallMethodProper call
                    when call.behaviour?.Value == nameof(HeroController):
                    return call.methodName?.Value;
                case HutongGames.PlayMaker.Actions.HeroRelinquishControlDynamic:
                    return "RelinquishControl";
                case HutongGames.PlayMaker.Actions.SetHeroAffectedByGravity:
                    return "AffectedByGravity";
                case HutongGames.PlayMaker.Actions.SendMessage send
                    when EntityFsmActions.WorksOnThePlayerCharacter(action, false):
                    return send.functionCall?.FunctionName;
                case HutongGames.PlayMaker.Actions.SetMeshRenderer { active.Value: false } hide
                    when EntityFsmActions.WorksOnThePlayerCharacter(hide, false):
                    return HideName;
                case HutongGames.PlayMaker.Actions.HeroControllerMethods:
                    return action.GetType().GetField("method", BindingFlags.Instance | BindingFlags.Public)
                        ?.GetValue(action) switch {
                            FsmEnum fsmEnum => fsmEnum.Value?.ToString(),
                            { } method => method.ToString(),
                            _ => null
                        };
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// What a catch that a creature watches for itself takes from the player themselves (see
    /// <see cref="PlayerBeforeTheCatch"/>): the fields of the player data that hold the state of the player rather than
    /// progress which it writes, as far as they are booleans, the states of the player character that it sets, the
    /// marks that the game keeps of the player until they change rooms which it sets to true or false - like the one of
    /// a player held in a cage, which keeps anything else from grabbing them - the states in which it sends away what
    /// shows the player their health, and whether it blacks out their screen, takes their control, the control of their
    /// animation, the drawing of their character or its falling.
    /// </summary>
    private sealed record WhatACatchTakes(
        string[] Flags,
        string[] States,
        string[] Marks,
        HashSet<FsmState> HudStates,
        bool Screen,
        bool Control,
        bool Animation,
        bool Drawing,
        bool Falling
    );
}
