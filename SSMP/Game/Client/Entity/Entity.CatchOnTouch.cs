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
/// own player out (see <see cref="PlayForPartner"/>).
/// </summary>
internal partial class Entity {
    /// <summary>
    /// The events with which the FSMs of the entity catch the player who touches what they watch for them, or null
    /// (see <see cref="ListenForCatches"/>).
    /// </summary>
    private HashSet<string>? _catchEvents;

    /// <summary>
    /// The FSM and the state of it that the local player was last caught in by this copy, so that a catch that did not
    /// take is not tried again every frame while the scene host says its FSM is still there.
    /// </summary>
    private (byte FsmIndex, FsmState State)? _caughtIn;

    /// <summary>
    /// What the local player had before the catch that the copy plays here for this game took it from them, or null
    /// (see <see cref="PlayerBeforeTheCatch"/>).
    /// </summary>
    private PlayerBeforeTheCatch? _beforeTheCatch;

    /// <summary>
    /// The tag of the hit box of the player, which is what a creature that waits for the player to be hurt or caught
    /// watches for.
    /// </summary>
    private const string HitBoxTag = "HeroBox";

    /// <summary>
    /// The fields of each kind of action that watches a trigger: the object it watches, the tag of what it waits for,
    /// and the event it sends when that comes in, by type; null for a kind without them.
    /// </summary>
    private static readonly Dictionary<Type, (FieldInfo Watched, FieldInfo? Tag, FieldInfo Event)?> TriggerFields =
        new();

    /// <summary>
    /// What each FSM that catches the player takes from them itself, or null for nothing (see
    /// <see cref="WhatACatchTakes"/>), found the first time it catches the local player.
    /// </summary>
    private static readonly ConditionalWeakTable<HutongGames.PlayMaker.Fsm, StrongBox<WhatACatchTakes?>>
        WhatCatchesTake = new();

    /// <summary>
    /// What <see cref="PlayerBeforeTheCatch"/> calls an action that hides the player character.
    /// </summary>
    private const string HideName = "(hide)";

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
        if (_catchEvents == null || CopyTouchedLocalPlayer == null || _runHere != null || _runHereTalk ||
            Object.Client == null || !Object.Client.activeInHierarchy || !CanBeCaughtHere()) {
            return;
        }

        for (var index = 0; index < _fsms.Client.Count && index < byte.MaxValue; index++) {
            var copyFsm = _fsms.Client[index];
            if (copyFsm == null || copyFsm.enabled || StateOfCopy(copyFsm) is not { } state) {
                continue;
            }

            if (_caughtIn is { } tried && tried.FsmIndex == index) {
                if (tried.State == state) {
                    continue;
                }

                _caughtIn = null;
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
    /// </summary>
    /// <param name="fsm">The FSM of the copy.</param>
    /// <param name="state">The state the scene host says it is in.</param>
    /// <returns>The event, or null.</returns>
    private string? CatchWatchedIn(HutongGames.PlayMaker.Fsm fsm, FsmState state) {
        foreach (var action in state.Actions) {
            if (!action.Enabled || TriggerFieldsOf(action.GetType()) is not { } fields ||
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
    private static (FieldInfo Watched, FieldInfo? Tag, FieldInfo Event)? TriggerFieldsOf(Type type) {
        if (TriggerFields.TryGetValue(type, out var known)) {
            return known;
        }

        (FieldInfo, FieldInfo?, FieldInfo)? found = null;
        if (type.Name.StartsWith("Trigger2dEvent", StringComparison.Ordinal) &&
            type.GetField("gameObject", BindingFlags.Instance | BindingFlags.Public) is { } watched &&
            watched.FieldType == typeof(FsmOwnerDefault) &&
            type.GetField("sendEvent", BindingFlags.Instance | BindingFlags.Public) is { } sent &&
            sent.FieldType == typeof(FsmEvent)) {
            var tag = type.GetField("collideTag", BindingFlags.Instance | BindingFlags.Public);
            found = (watched, tag != null && tag.FieldType == typeof(FsmString) ? tag : null, sent);
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
    /// Notes what the local player has that the FSM of a copy about to play a catch that this game leads may take from
    /// them (see <see cref="PlayerBeforeTheCatch"/>).
    /// </summary>
    /// <param name="fsm">The FSM of the copy.</param>
    private void NoteThePlayerBeforeTheCatch(HutongGames.PlayMaker.Fsm fsm) {
        _beforeTheCatch = PlayerBeforeTheCatch.Take(fsm);
    }

    /// <summary>
    /// Gives the local player back what the catch that the copy played here for this game took from them, if it was
    /// cut short (see <see cref="PlayerBeforeTheCatch"/>). One played through to what follows it gave it back itself,
    /// and one that took the player to another room is given back there by the game.
    /// </summary>
    /// <param name="cut">Whether the copy stopped before it played what follows the catch (see
    /// <see cref="AftermathOf"/>): the scene host did not take the catch, never answered, or the copy went away or
    /// followed the scene host out of it while it held the player.</param>
    private void GiveBackWhatTheCatchTook(bool cut) {
        var before = _beforeTheCatch;
        _beforeTheCatch = null;
        if (cut && before != null && !AnyLeadsACatch(this)) {
            before.GiveBack($"the copy of entity {Id} no longer plays the catch");
        }
    }

    /// <summary>
    /// What the local player had before a catch that this game leads, of what the FSM of the creature takes from them
    /// itself while it holds them and gives back only at the end: their control, the drawing of their character and
    /// its falling, whether they can pause, open their inventory, use their skills or be hurt, and the states of their
    /// character that it sets. A creature that holds the player through the grab of the player character's own FSM
    /// lets go of them with a release (EntityFsmActions.LetGoOfTheHeldLocalPlayer); one that takes all of this itself,
    /// like the one that cages the player, would give none of it back when its catch is cut short - the scene host did
    /// not take it, never answered, or the copy went away - and leave the player hidden in the air without control,
    /// unable to pause, for good. Only what the FSM itself takes is given back, so that the release of a grab, or a
    /// player thrown off at the end, goes on as the game has it.
    /// </summary>
    private sealed class PlayerBeforeTheCatch {
        private readonly WhatACatchTakes _taken;
        private readonly bool _hadControl;
        private readonly bool _hadAnimationControl;
        private readonly bool _wasDrawn;
        private readonly bool _hadGravity;
        private readonly List<(string Name, bool Value)> _flags = [];
        private readonly List<(string Name, bool Value)> _states = [];

        private PlayerBeforeTheCatch(WhatACatchTakes taken, HeroController hero) {
            _taken = taken;
            var renderer = hero.GetComponent<MeshRenderer>();
            var animation = hero.GetComponent<HeroAnimationController>();
            _hadControl = !hero.controlReqlinquished;
            _hadAnimationControl = animation == null || animation.controlEnabled;
            _wasDrawn = renderer == null || renderer.enabled;
            _hadGravity = hero.IsGravityApplied;
        }

        /// <summary>
        /// Notes what the local player has of what the given FSM takes, or returns null if there is no player or it
        /// takes nothing.
        /// </summary>
        /// <param name="fsm">The FSM of the copy that is about to play the catch.</param>
        public static PlayerBeforeTheCatch? Take(HutongGames.PlayMaker.Fsm fsm) {
            var hero = HeroController.SilentInstance;
            var playerData = PlayerData.instance;
            if (hero == null || playerData == null || WhatItTakes(fsm) is not { } taken) {
                return null;
            }

            var before = new PlayerBeforeTheCatch(taken, hero);
            foreach (var flag in taken.Flags) {
                before._flags.Add((flag, playerData.GetBool(flag)));
            }

            foreach (var state in taken.States) {
                before._states.Add((state, hero.cState.GetState(state)));
            }

            return before;
        }

        /// <summary>
        /// Gives the local player back what differs from what they had, unless they are dead or on their way to
        /// another room, which take care of it themselves.
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

            if (given.Count > 0) {
                Logger.Info($"Gave the local player back {string.Join(", ", given)}: {why}");
            }
        }

        /// <summary>
        /// What an FSM takes from the player itself (see <see cref="WhatACatchTakes"/>), or null if nothing, found
        /// the first time it catches the local player.
        /// </summary>
        private static WhatACatchTakes? WhatItTakes(HutongGames.PlayMaker.Fsm fsm) {
            if (WhatCatchesTake.TryGetValue(fsm, out var known)) {
                return known.Value;
            }

            var flags = new HashSet<string>();
            var states = new HashSet<string>();
            bool control = false, animation = false, drawing = false, falling = false;
            foreach (var state in fsm.States) {
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

            WhatACatchTakes? taken = flags.Count > 0 || states.Count > 0 || control || animation || drawing || falling
                ? new WhatACatchTakes([..flags], [..states], control, animation, drawing, falling)
                : null;
            WhatCatchesTake.Add(fsm, new StrongBox<WhatACatchTakes?>(taken));
            return taken;
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
    /// What the FSM of a creature that catches the player takes from them itself (see
    /// <see cref="PlayerBeforeTheCatch"/>): the fields of the player data that hold the state of the player rather than
    /// progress which it writes, as far as they are booleans, the states of the player character that it sets, and
    /// whether it takes their control, the control of their animation, the drawing of their character or its falling.
    /// </summary>
    private sealed record WhatACatchTakes(
        string[] Flags,
        string[] States,
        bool Control,
        bool Animation,
        bool Drawing,
        bool Falling
    );
}
