using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

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
    /// The variable of that FSM that is true from the moment a heal begins, before it spends any silk, until it ends.
    /// </summary>
    private const string IsBindingVariable = "Is Binding";

    /// <summary>
    /// The state of that FSM that sets that variable, before any silk is spent.
    /// </summary>
    private const string HealStartState = "Quick Craft?";

    /// <summary>
    /// The state of that FSM that gives the health.
    /// </summary>
    private const string HealState = "Heal";

    /// <summary>
    /// The states of that FSM that a heal ends in, early or not: cancelled, ended, hit, and left behind by a scene change.
    /// </summary>
    private static readonly HashSet<string> HealEndStates = ["Cancel All", "End Bind", "Witch Binding?", "Leave Scene Bind?"];

    /// <summary>
    /// The player character's FSM that holds them during a roar, which waits for a heal to end first.
    /// </summary>
    private const string RoarFsm = "Roar and Wound States";

    /// <summary>
    /// How the events that a roar sends to the player begin: ROAR WOUND CANCEL and ROAR ENTER when it starts, with the
    /// burst and forced forms of the latter, and ROAR EXIT when it stops.
    /// </summary>
    private const string RoarEventPrefix = "ROAR ";

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
        var fsmName = self.Name;
        if (fsmName != BindFsm && fsmName != RoarFsm || fsmEvent?.Name is not { } eventName ||
            self.GameObject == null || self.GameObject.GetComponent<HeroController>() is not { } hero) {
            orig(self, fsmEvent!, eventData);
            return;
        }

        if (fsmName == RoarFsm) {
            SayWhatTheRoarFound(self, hero, eventName);
            orig(self, fsmEvent, eventData);
            return;
        }

        // The take-over is a sub-FSM, run by an action of the state that the FSM running it waits in
        if (eventName == CancelEvent && hero.cState.isBinding &&
            FsmExecutionStack.ExecutingFsm?.Host?.ActiveState?.Actions.Any(action =>
                action is RunFSM run && run.fsmTemplateControl.fsmTemplate?.name.StartsWith(TakeOverPrefix) == true
            ) == true) {
            Logger.Info($"Kept the cancel of a take-over by '{FsmExecutionStack.ExecutingFsm?.Host?.Name}' away from the heal");
            return;
        }

        SayWhatCutTheHealShort(self, eventName);
        orig(self, fsmEvent, eventData);
    }

    /// <summary>
    /// Whether the heal going on now has healed already, so that its end is the ordinary one.
    /// </summary>
    private static bool _healReached;

    /// <summary>
    /// Says what ends the local player's heal after it spent its silk and before it healed, and who sent it: the heal
    /// itself, another FSM, or something outside any FSM. A heal thrown away leaves nothing else behind, and one was
    /// reported broken off by the roar of a boss, which in the game itself waits for a heal to end.
    /// </summary>
    private static void SayWhatCutTheHealShort(Fsm self, string eventName) {
        if (self.ActiveState is not { } state) {
            return;
        }

        var to = state.Transitions?.FirstOrDefault(transition => transition.EventName == eventName)?.ToState ??
                 self.GlobalTransitions?.FirstOrDefault(transition => transition.EventName == eventName)?.ToState;
        switch (to) {
            case null:
                return;
            case HealStartState:
                _healReached = false;
                return;
            case HealState:
                _healReached = true;
                return;
        }

        if (_healReached || !HealEndStates.Contains(to) ||
            self.Variables.FindFsmBool(IsBindingVariable) is not { Value: true }) {
            return;
        }

        var sender = FsmExecutionStack.ExecutingFsm;
        var from = sender == self
            ? "by the heal itself"
            : sender == null
                ? "from outside of any FSM"
                : $"by '{(sender.GameObject == null ? "?" : sender.GameObject.name)}' ({sender.Name} in " +
                  $"'{sender.ActiveStateName}'{(sender.Host == null ? "" : $", run by '{sender.Host.Name}'")})";
        Logger.Info(
            $"The heal was cut short in '{state.Name}' before it healed, by '{eventName}' {from}, taking it to '{to}' " +
            $"(crest '{PlayerData.instance?.CurrentCrestID}')"
        );
    }

    /// <summary>
    /// Says what a roar found the local player's heal doing when it reached them, if a heal was going on. The game's own
    /// roar holds a player who heals until the heal is over.
    /// </summary>
    private static void SayWhatTheRoarFound(Fsm self, HeroController hero, string eventName) {
        if (!eventName.StartsWith(RoarEventPrefix, StringComparison.Ordinal)) {
            return;
        }

        var bind = hero.gameObject.LocateMyFSM(BindFsm);
        if (bind == null || bind.ActiveStateName is "Idle" or "Cooldown") {
            return;
        }

        Logger.Info(
            $"A roar ('{eventName}') reached the player while the heal was in '{bind.ActiveStateName}' " +
            $"(Is Binding: {bind.FsmVariables.FindFsmBool(IsBindingVariable)?.Value}, " +
            $"isBinding: {hero.cState.isBinding}), the roar handling being in '{self.ActiveStateName}'"
        );
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
        Logger.Info($"The heal ended, doing the {held.Length} thing(s) that other objects did to the player during it");
        foreach (var (source, apply) in held) {
            if (source != null) {
                apply();
            }
        }
    }
}
