using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Packet.Data;

// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedParameter.Local
#pragma warning disable CS0618

namespace SSMP.Game.Client.Entity.Action;

// What a creature shows besides its body and its parts moving: its colour changing and flashing, the screen flashing
// and the camera shaking with its blows, and its roars, which hold the player in place. None of it was done again on
// the other game. The copy runs these actions itself, as its FSM would (RunInState), so a tween, an ease, a delayed or
// repeating shake and a looping one that its state stops all play out there as they do here.
//
// None of it is done when the creature's first states are set up (RunMoment). The copy may be set up long after the
// creature left them, for a player who walks in later, and would keep the colour of a state it left long ago until
// the creature next changed it.
//
// A shake or a flash from a state that deals with the player character stays with that player's game
// (IsAboutThePlayer).
//
// Left out: ScreenFader and CameraBlurPlaneFade, which fade or blur the whole screen where the game that runs the
// creature goes on into a sequence or a scene of its own. The other game does not follow it there, and would be left
// behind the fade. Left out too: SetSpriteRendererColor, whose OnEnter is that of a generic class that 33 kinds of
// action share, the talking of NPCs among them; hooking it would hook all of them.

internal static partial class EntityFsmActions {
    #region Tk2dSpriteSetColor

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, Tk2dSpriteSetColor action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, Tk2dSpriteSetColor action) {
        RunMoment(data, action);
    }

    #endregion

    #region Tk2dSpriteTweenColor

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, Tk2dSpriteTweenColor action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, Tk2dSpriteTweenColor action) {
        RunMoment(data, action);
    }

    #endregion

    #region SetSpriteColor

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetSpriteColor action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, SetSpriteColor action) {
        RunMoment(data, action);
    }

    #endregion

    #region EaseSpriteColor

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, EaseSpriteColor action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, EaseSpriteColor action) {
        RunMoment(data, action);
    }

    #endregion

    #region EaseColor

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, EaseColor action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, EaseColor action) {
        RunMoment(data, action);
    }

    #endregion

    #region SetColorValue

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetColorValue action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, SetColorValue action) {
        RunMoment(data, action);
    }

    #endregion

    #region CharacterAmbientLightLerp

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, CharacterAmbientLightLerp action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, CharacterAmbientLightLerp action) {
        RunMoment(data, action);
    }

    #endregion

    #region FadeNestedFadeGroup

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, FadeNestedFadeGroup action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, FadeNestedFadeGroup action) {
        RunMoment(data, action);
    }

    #endregion

    #region DoSpriteFlash

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, DoSpriteFlash action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, DoSpriteFlash action) {
        RunMoment(data, action);
    }

    #endregion

    #region ScreenFlash

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, ScreenFlash action) {
        return !IsAboutThePlayer(action.State);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, ScreenFlash action) {
        RunMoment(data, action);
    }

    #endregion

    #region ScreenFlashTrobbio

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, ScreenFlashTrobbio action) {
        return !IsAboutThePlayer(action.State);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, ScreenFlashTrobbio action) {
        RunMoment(data, action);
    }

    #endregion

    #region DoCameraShake

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, DoCameraShake action) {
        return !IsAboutThePlayer(action.State);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, DoCameraShake action) {
        RunMoment(data, action);
    }

    #endregion

    #region DoCameraShakeV2

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, DoCameraShakeV2 action) {
        return !IsAboutThePlayer(action.State);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, DoCameraShakeV2 action) {
        RunMoment(data, action);
    }

    #endregion

    #region DoCameraShakeV3

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, DoCameraShakeV3 action) {
        return !IsAboutThePlayer(action.State);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, DoCameraShakeV3 action) {
        RunMoment(data, action);
    }

    #endregion

    #region DoCameraShakeV4

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, DoCameraShakeV4 action) {
        return !IsAboutThePlayer(action.State);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, DoCameraShakeV4 action) {
        RunMoment(data, action);
    }

    #endregion

    #region DoCameraShakeRepeating

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, DoCameraShakeRepeating action) {
        return !IsAboutThePlayer(action.State);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, DoCameraShakeRepeating action) {
        RunMoment(data, action);
    }

    #endregion

    #region DoCameraShakeRepeatingV2

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, DoCameraShakeRepeatingV2 action) {
        return !IsAboutThePlayer(action.State);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, DoCameraShakeRepeatingV2 action) {
        RunMoment(data, action);
    }

    #endregion

    #region CancelCameraShake

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, CancelCameraShake action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, CancelCameraShake action) {
        RunMoment(data, action);
    }

    #endregion

    #region StartRoarEmitter

    // A roar holds the player character of the game that does it in place, and shows its wave on that game's camera.
    // Done only in the game that runs the creature, it held that game's player while the other walked on through it.
    // The copy roars too, at its own game's player. It is sent whatever else its state does, because a roar is felt by
    // everyone in the room, not only by a player the creature is dealing with.

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, StartRoarEmitter action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, StartRoarEmitter action) {
        RunMoment(data, action);
    }

    #endregion

    #region StopRoarEmitter

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, StopRoarEmitter action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, StopRoarEmitter action) {
        RunMoment(data, action);
    }

    #endregion

    /// <summary>
    /// Actions that do something to the player character or check whether it can be hit or caught.
    /// </summary>
    private static readonly HashSet<string> PlayerActionNames = [
        "SetHeroCState", "SetHeroCStateDelay", "HeroControllerMethods", "ClearHeroEffects", "ClearHeroEffectsInstant",
        "ClearHeroEffectsLite", "DamageHeroDirectly", "DamageHeroDirectlyV2", "CanHeroTakeDamage",
        "CanHeroTakeDamageIgnoreInvul", "CanHeroBeGrabbed", "CanHeroBeGrabbedV2", "SetHeroParent", "SetHeroStunned",
        "SetHeroMaggoted", "HeroRelinquishControlDynamic", "HeroInvulnerability", "HeroLockState", "HeroBoxControl",
        "DoHeroRecoil", "AddHeroInputBlocker", "SetHeroAffectedByGravity"
    ];

    /// <summary>
    /// The events that the player character's own FSM takes when a creature wounds, grabs, lets go of or spits it out.
    /// </summary>
    private static readonly HashSet<string> PlayerWoundEvents = [
        "WOUND START", "WOUND END", "HERO GRAB", "HERO GRAB BREAK", "HERO GRAB END", "HERO GRAB RELEASE",
        "HERO GRAB RELEASE FORCED", "HERO GRAB RELEASE SOFT", "HERO GRAB VULNERABLE", "HERO SPIT", "BONK BACK",
        "MULTI WOUND 3", "MULTI WOUND FORCED", "MULTI WOUND HAZARD", "MULTI WOUND HAZARD WEAK", "MULTI WOUND STEAM",
        "MULTI WOUND WEAK", "MULTI LAG HIT", "MULTI ZAP HIT", "MULTI DOUBLE STRIKE", "MULTI POLLEN HIT",
        "MULTI HIT NO EFFECT", "MULTI HIT RELEASE"
    ];

    /// <summary>
    /// The states of each FSM of the scene host's creatures that deal with the player character, found the first time
    /// one of its states shakes or flashes.
    /// </summary>
    private static readonly ConditionalWeakTable<HutongGames.PlayMaker.Fsm, HashSet<FsmState>> PlayerStates = new();

    /// <summary>
    /// Whether a creature's state deals with the player character: catches, stabs, wounds or lets go of them, tells
    /// the room they were hit or caught, or goes on from such states and from no other - the slashes after a catch, the
    /// pull after a stab. A shake of the camera or a flash of the screen there is part of what the game shows the
    /// player it happened to, like the rest of being hit, and is not shared with the other game's player (the camera
    /// shakes of the other game's player being hit are not shared either). The rest - blows on the ground, landings,
    /// roars - are shared. Stopping a shake is always shared, so nothing is left shaking.
    /// </summary>
    /// <param name="state">The state of the scene host's creature.</param>
    private static bool IsAboutThePlayer(FsmState state) {
        return PlayerStatesOf(state.Fsm).Contains(state);
    }

    /// <summary>
    /// The states of an FSM of the scene host's creature that deal with the player character (see
    /// <see cref="IsAboutThePlayer"/>), found the first time they are asked for.
    /// </summary>
    private static HashSet<FsmState> PlayerStatesOf(HutongGames.PlayMaker.Fsm fsm) {
        if (!PlayerStates.TryGetValue(fsm, out var states)) {
            states = FindPlayerStates(fsm);
            PlayerStates.Add(fsm, states);
        }

        return states;
    }

    /// <summary>
    /// Finds the states of an FSM that deal with the player character (see <see cref="IsAboutThePlayer"/>).
    /// </summary>
    private static HashSet<FsmState> FindPlayerStates(HutongGames.PlayMaker.Fsm fsm) {
        var found = new HashSet<FsmState>();
        foreach (var state in fsm.States) {
            if (DealsWithThePlayer(state)) {
                found.Add(state);
            }
        }

        AddStatesEnteredOnlyFrom(fsm, found);
        return found;
    }

    /// <summary>
    /// Adds to some states of an FSM the states entered from those states alone, until there are no more: the slashes
    /// after a catch, the pull after a stab. A state that a global transition leads to can be entered from any state.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="found">The states, which the states found are added to.</param>
    internal static void AddStatesEnteredOnlyFrom(HutongGames.PlayMaker.Fsm fsm, HashSet<FsmState> found) {
        var globalTargets = new HashSet<string>();
        foreach (var transition in fsm.GlobalTransitions) {
            globalTargets.Add(transition.ToState);
        }

        var sources = new Dictionary<string, List<FsmState>>();
        foreach (var state in fsm.States) {
            foreach (var transition in state.Transitions) {
                if (!sources.TryGetValue(transition.ToState, out var from)) {
                    sources[transition.ToState] = from = [];
                }

                from.Add(state);
            }
        }

        bool added;
        do {
            added = false;
            foreach (var state in fsm.States) {
                if (found.Contains(state) || globalTargets.Contains(state.Name) ||
                    !sources.TryGetValue(state.Name, out var from) || !from.TrueForAll(found.Contains)) {
                    continue;
                }

                found.Add(state);
                added = true;
            }
        } while (added);
    }

    /// <summary>
    /// Whether an action of a state does something to the player character, checks whether it can be hit or caught,
    /// sends it the event of being wounded or caught, or tells the room about it.
    /// </summary>
    private static bool DealsWithThePlayer(FsmState state) {
        foreach (var action in state.Actions) {
            if (!action.Enabled) {
                continue;
            }

            if (PlayerActionNames.Contains(action.GetType().Name)) {
                return true;
            }

            var broadcast = action switch {
                SendEventToRegister register => register.eventName.Value,
                SendEventToRegisterV2 register => register.EventName.Value,
                SendEventToRegisterDelay register => register.EventName.Value,
                _ => null
            };
            if (broadcast != null && PlayerEvents.Contains(broadcast)) {
                return true;
            }

            var sent = SentEventName(action);
            if (sent != null && PlayerWoundEvents.Contains(sent)) {
                return true;
            }

            // Anything else done to the player character, apart from holding them in place for a roar, which every
            // player in the room feels
            if (sent?.StartsWith("ROAR") != true && WorksOnThePlayerCharacter(action, false)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The kinds of action that shake the camera or flash the screen.
    /// </summary>
    private static readonly HashSet<string> ShakeAndFlashNames = [
        "ScreenFlash", "ScreenFlashTrobbio", "DoCameraShake", "DoCameraShakeV2", "DoCameraShakeV3", "DoCameraShakeV4",
        "DoCameraShakeRepeating", "DoCameraShakeRepeatingV2"
    ];

    /// <summary>
    /// The fields of each kind of action that hold an object, by type.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> ObjectFields = new();

    /// <summary>
    /// The fields of each kind of action that hold events it may send its own FSM on with, by type.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> EventFields = new();

    /// <summary>
    /// The actions of an FSM of a creature that are this game's player's own (see <see cref="IsThePlayersOwn"/>) and
    /// switched on.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    internal static List<FsmStateAction> PlayersOwnActionsOf(HutongGames.PlayMaker.Fsm fsm) {
        // Which states deal with the player is found once, from the actions that are switched on, so it is found
        // before any of these is switched off
        PlayerStatesOf(fsm);

        var found = new List<FsmStateAction>();
        foreach (var state in fsm.States) {
            foreach (var action in state.Actions) {
                if (action.Enabled && IsThePlayersOwn(action)) {
                    found.Add(action);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Whether an action of a creature's FSM is this game's player's own: it does something to the player character -
    /// moves, holds or turns them, tells them or their FSMs something, plays or spawns something at them, looks at
    /// them - or it shakes the camera or flashes the screen in a state that deals with them (see
    /// <see cref="IsAboutThePlayer"/>). An action that may send its FSM on to another state is never counted, so that
    /// an FSM without these actions still goes where it would have gone.
    ///
    /// A copy that plays what the local player did, or what caught them, runs these on the local player, whose own
    /// they are (see Entity.IsLeftToSceneHost). The scene host, playing the same for its partner, leaves them out: its
    /// own player was not the one caught (see Entity.PlayForPartner).
    /// </summary>
    internal static bool IsThePlayersOwn(FsmStateAction action) {
        var name = action.GetType().Name;
        var own = ShakeAndFlashNames.Contains(name)
            ? action.State != null && IsAboutThePlayer(action.State)
            : PlayerActionNames.Contains(name) || NamesThePlayer(action);
        return own && !DecidesWhereItGoes(action);
    }

    /// <summary>
    /// Whether any object that an action is given, under any name, is this game's player character or something they
    /// carry.
    /// </summary>
    private static bool NamesThePlayer(FsmStateAction action) {
        var hero = HeroController.instance;
        if (hero == null || action.Fsm == null) {
            return false;
        }

        foreach (var field in FieldsOf(action.GetType(), ObjectFields, IsObjectField)) {
            if (IsOnThePlayer(action, field, hero)) {
                return true;
            }
        }

        return false;

        static bool IsObjectField(FieldInfo field) {
            return field.FieldType == typeof(FsmOwnerDefault) || field.FieldType == typeof(FsmGameObject) ||
                   field.FieldType == typeof(FsmEventTarget);
        }
    }

    /// <summary>
    /// Whether an action is given an event that it may send its own FSM on to another state with.
    /// </summary>
    private static bool DecidesWhereItGoes(FsmStateAction action) {
        foreach (var field in FieldsOf(action.GetType(), EventFields, IsEventField)) {
            switch (field.GetValue(action)) {
                case FsmEvent { Name.Length: > 0 }:
                case FsmEvent[] events when Array.Exists(events, fsmEvent => fsmEvent is { Name.Length: > 0 }):
                    return true;
            }
        }

        return false;

        static bool IsEventField(FieldInfo field) {
            return field.FieldType == typeof(FsmEvent) || field.FieldType == typeof(FsmEvent[]);
        }
    }

    /// <summary>
    /// The public fields of a kind of action that the given test picks, found once for each kind.
    /// </summary>
    private static FieldInfo[] FieldsOf(Type type, Dictionary<Type, FieldInfo[]> found, Func<FieldInfo, bool> picks) {
        if (!found.TryGetValue(type, out var fields)) {
            fields = Array.FindAll(type.GetFields(BindingFlags.Public | BindingFlags.Instance), field => picks(field));
            found[type] = fields;
        }

        return fields;
    }

    /// <summary>
    /// The name of the event an action sends, for the actions that send one by its name or as an event.
    /// </summary>
    private static string? SentEventName(FsmStateAction action) {
        return action.GetType().GetField("sendEvent", BindingFlags.Public | BindingFlags.Instance)?.GetValue(action)
            switch {
                FsmString name => name.Value,
                FsmEvent fsmEvent => fsmEvent.Name,
                _ => null
            };
    }
}
