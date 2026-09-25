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
// and the camera shaking with its blows. None of it was done again on the other game. The copy runs these actions
// itself, as its FSM would (RunInState), so a tween, an ease, a delayed or repeating shake and a looping one that its
// state stops all play out there as they do here.
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
        if (!PlayerStates.TryGetValue(state.Fsm, out var states)) {
            states = FindPlayerStates(state.Fsm);
            PlayerStates.Add(state.Fsm, states);
        }

        return states.Contains(state);
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

        // Then the states entered from those states alone, until there are no more. A state that a global transition
        // leads to can be entered from any state.
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

        return found;
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
