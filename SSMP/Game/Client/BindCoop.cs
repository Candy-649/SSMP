using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Util;
using UnityEngine;

namespace SSMP.Game.Client;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Keeps the local player's heal (bind) from being thrown away by the intro of a fight that another player started.
///
/// Alone, a player starts an intro themselves by walking in, and can't be healing then. With two players the other one
/// starts it, and some intros begin by cancelling everything the player is doing - a heal too, whose silk is spent the
/// moment it starts - before they take hold of the player and strike a pose. The game itself already lets a heal
/// finish before a roar holds the player. The rest of an intro now waits for the heal the same way, while the intro
/// itself goes on: its take-over's cancel passes the heal by, and what other FSMs do to the player's control and
/// animation during the heal is done when it ends, in the order they did it. By then the intro may have given control
/// back or moved on to another pose, so the player ends up as the intro has them at that moment.
/// </summary>
internal class BindCoop {
    /// <summary>
    /// Binding flags for the members of the game.
    /// </summary>
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// The name of the player character's FSM that heals them.
    /// </summary>
    private const string BindFsm = "Bind";

    /// <summary>
    /// The event that a take-over sends to everything the player is doing, to cancel it.
    /// </summary>
    private const string CancelEvent = "FSM CANCEL";

    /// <summary>
    /// How the names of the game's take-overs of the player begin: take_hero_control_soft, _dlg and _nofall. An FSM
    /// runs one as a sub-FSM, and waits in that state until it has the player standing still.
    /// </summary>
    private const string TakeOverPrefix = "take_hero_control";

    /// <summary>
    /// What other FSMs did to the local player's control and animation while they healed, each with the object of the
    /// FSM that did it. A room that is left in the meantime takes its objects along, and what they did goes with them.
    /// </summary>
    private readonly List<(GameObject Source, Action Apply)> _held = [];

    /// <summary>
    /// The hooks, which are disposed together.
    /// </summary>
    private readonly List<Hook> _hooks = [];

    /// <summary>
    /// Registers the hooks.
    /// </summary>
    public void RegisterHooks() {
        // Taking control and giving it back, and switching the animation off and on: a heal does these itself at its
        // start and end, so what an intro does in between would either undo the heal or be undone by it
        foreach (var name in (string[]) [
                     "RelinquishControl", "RelinquishControlNotVelocity", "StopAnimationControl", "StartAnimationControl",
                     "StartAnimationControlToIdle", "StartAnimationControlToIdleForcePlay"
                 ]) {
            _hooks.Add(new Hook(
                typeof(HeroController).GetMethod(name, InstanceFlags, null, Type.EmptyTypes, null)!,
                new Action<Action<HeroController>, HeroController>((orig, self) => {
                    if (!HoldWhileHealing(self, () => orig(self))) {
                        orig(self);
                    }
                })
            ));
        }

        foreach (var name in (string[]) ["RegainControl", "AffectedByGravity"]) {
            _hooks.Add(new Hook(
                typeof(HeroController).GetMethod(name, InstanceFlags, null, [typeof(bool)], null)!,
                new Action<Action<HeroController, bool>, HeroController, bool>((orig, self, value) => {
                    if (!HoldWhileHealing(self, () => orig(self, value))) {
                        orig(self, value);
                    }
                })
            ));
        }

        // A pose, which would take the place of the heal's own clips: the heal is carried on by their frames
        _hooks.Add(new Hook(
            typeof(tk2dSpriteAnimator).GetMethod(
                nameof(tk2dSpriteAnimator.Play),
                InstanceFlags,
                null,
                [typeof(tk2dSpriteAnimationClip), typeof(float), typeof(float)],
                null
            )!,
            new Action<Action<tk2dSpriteAnimator, tk2dSpriteAnimationClip, float, float>, tk2dSpriteAnimator,
                tk2dSpriteAnimationClip, float, float>((orig, self, clip, startTime, fps) => {
                if (!HoldWhileHealing(self, () => orig(self, clip, startTime, fps))) {
                    orig(self, clip, startTime, fps);
                }
            })
        ));

        _hooks.Add(new Hook(
            typeof(Fsm).GetMethod("ProcessEvent", InstanceFlags, null, [typeof(FsmEvent), typeof(FsmEventData)], null)!,
            new Action<Action<Fsm, FsmEvent, FsmEventData>, Fsm, FsmEvent, FsmEventData>(OnProcessEvent)
        ));

        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdate;
    }

    /// <summary>
    /// Deregisters the hooks.
    /// </summary>
    public void DeregisterHooks() {
        foreach (var hook in _hooks) {
            hook.Dispose();
        }

        _hooks.Clear();
        _held.Clear();
        MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdate;
    }

    /// <summary>
    /// Holds back what an FSM other than the player character's own does to the local player while they heal, until
    /// the heal has ended. What such FSMs do after that, before it is all done, is held back behind it, so that it is
    /// done in the same order.
    /// </summary>
    /// <param name="target">The part of the player character it is done to.</param>
    /// <param name="apply">Does it.</param>
    /// <returns>Whether it was held back.</returns>
    private bool HoldWhileHealing(Component target, Action apply) {
        var hero = HeroController.instance;
        if (hero == null || !hero.cState.isBinding && _held.Count == 0 || target.gameObject != hero.gameObject ||
            FsmExecutionStack.ExecutingFsm?.GameObject is not { } source || source.transform.IsChildOf(hero.transform)) {
            return false;
        }

        _held.Add((source, apply));
        return true;
    }

    /// <summary>
    /// Hook for <see cref="Fsm"/>.ProcessEvent, which keeps a take-over's cancel away from the local player's heal.
    /// Everything else the player was doing is cancelled all the same, and the heal ends by itself.
    /// </summary>
    private static void OnProcessEvent(
        Action<Fsm, FsmEvent, FsmEventData> orig,
        Fsm self,
        FsmEvent fsmEvent,
        FsmEventData eventData
    ) {
        // The take-over is a sub-FSM, run by an action of the state that the FSM running it waits in
        if (fsmEvent?.Name == CancelEvent && self.Name == BindFsm &&
            self.GameObject.GetComponent<HeroController>() is { cState.isBinding: true } &&
            FsmExecutionStack.ExecutingFsm?.Host?.ActiveState?.Actions.Any(action =>
                action is RunFSM run && run.fsmTemplateControl.fsmTemplate?.name.StartsWith(TakeOverPrefix) == true
            ) == true) {
            return;
        }

        orig(self, fsmEvent, eventData);
    }

    /// <summary>
    /// Does what was held back from the local player once the heal has given control back to them: its FSM cools down
    /// or waits for the next heal again. With some crests that is one clip later than the heal itself ends, and that
    /// clip only gives control back if nothing plays another one on the player in the meantime.
    /// </summary>
    private void OnUpdate() {
        var hero = HeroController.instance;
        if (_held.Count == 0 || hero == null ||
            hero.gameObject.LocateMyFSM(BindFsm).ActiveStateName is not ("Idle" or "Cooldown")) {
            return;
        }

        var held = _held.ToArray();
        _held.Clear();
        foreach (var (source, apply) in held) {
            if (source != null) {
                apply();
            }
        }
    }
}
