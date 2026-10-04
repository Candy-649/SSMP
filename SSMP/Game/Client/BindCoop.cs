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
/// Keeps the local player's heal (bind) from being thrown away by a fight that another player started.
///
/// Alone, a player starts a fight themselves by walking in, and can't be healing then. With two players the other one
/// starts it, while this one may be in the middle of a heal, whose silk is spent the moment it starts. Only what ends a
/// heal alone ends such a heal early now, like a hit or a creature catching the player:
/// - A roar that holds the player waits until the heal is over, and holds them then if it still goes on. The game has
///   its roar wait for a heal too, yet a roar of a fight that the other player started was seen cutting the heal short,
///   so the player character's FSM for roars doesn't hear of one until the heal is over.
/// - An intro that cancels whatever the player is doing, before it takes hold of them and strikes a pose, passes the
///   heal by. Only the player character itself and creatures that catch it call the heal off.
/// What other FSMs do to the player's control and animation during the heal is done when it ends, in the order they did
/// it. By then the intro may have given control back or moved on to another pose, so the player ends up as the intro
/// has them at that moment.
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
    /// The game's own roar waits for a heal while it is true.
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
    /// The player character's FSM that holds them during a roar.
    /// </summary>
    private const string RoarFsm = "Roar and Wound States";

    /// <summary>
    /// How the events that a roar sends to the player begin: ROAR WOUND CANCEL and ROAR ENTER when it starts, with the
    /// burst and forced forms of the latter, and ROAR EXIT when it stops.
    /// </summary>
    private const string RoarEventPrefix = "ROAR ";

    /// <summary>
    /// How the events of a short roar begin, which the game lets go of while the player heals.
    /// </summary>
    private const string RoarBurstPrefix = "ROAR BURST ";

    /// <summary>
    /// The events with which a roar stops holding the player.
    /// </summary>
    private static readonly HashSet<string> RoarExitEvents = ["ROAR EXIT", "ROAR EXIT SCENE"];

    /// <summary>
    /// The events that end the game's own wait of a roar for a heal: being hit, and leaving the room.
    /// </summary>
    private static readonly HashSet<string> RoarWaitEndEvents = ["HERO DAMAGED", "LEAVING SCENE", "LEVEL LOADED"];

    /// <summary>
    /// The states of the player character's FSM for roars that hold the player during a roar, or wait to.
    /// </summary>
    private static readonly HashSet<string> RoarLockStates = ["Lock Grounded", "Lock Air", "Burst Lock", "Binding"];

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
    /// The events of roars that reached the local player while they healed, in the order they came, which the player
    /// character's FSM for roars hears once the heal is over.
    /// </summary>
    private static readonly List<string> HeldRoarEvents = [];

    /// <summary>
    /// The player character that the events of roars are held for.
    /// </summary>
    private static HeroController? _heldRoarHero;

    /// <summary>
    /// Whether the held events of roars are being let through now that the heal is over.
    /// </summary>
    private static bool _lettingRoarGoOn;

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
        HeldRoarEvents.Clear();
        _heldRoarHero = null;
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
    /// Hook for <see cref="Fsm"/>.ProcessEvent, which keeps a roar away from the local player's FSM for roars while
    /// they heal, and keeps the cancel of a take-over away from the heal. Everything else the player was doing is
    /// cancelled all the same, and the heal ends by itself.
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

        // Like the game's own wait of a roar for a heal, a roar that waits doesn't hold a player who was hit or left
        if (RoarWaitEndEvents.Contains(eventName) && HeldRoarEvents.Count > 0) {
            Logger.Info($"Let go of the roar that waited for the heal to be over, because of '{eventName}'");
            HeldRoarEvents.Clear();
        }

        if (fsmName == RoarFsm) {
            if (!HoldRoarWhileHealing(hero, eventName)) {
                orig(self, fsmEvent, eventData);
            }

            return;
        }

        if (eventName == CancelEvent && IsHealing(hero) && FsmExecutionStack.ExecutingFsm is { } sender &&
            IsTakeOver(sender, hero)) {
            Logger.Info($"Kept the cancel from {DescribeFsm(sender)} away from the heal");
            return;
        }

        SayWhatCutTheHealShort(self, eventName);
        orig(self, fsmEvent, eventData);
    }

    /// <summary>
    /// Whether an FSM that cancels what the local player is doing takes them over, like the intro of a fight or a
    /// dialogue does, rather than catching them. The player character's own FSMs cancel a heal when the player is wounded
    /// or grabbed, a creature's own FSM when it catches or knocks them, and the game outside of any FSM when they die or
    /// leave the room: those end a heal alone too. A creature that takes the player over for its intro does so with one
    /// of the game's take-overs.
    /// </summary>
    private static bool IsTakeOver(Fsm sender, HeroController hero) {
        var source = sender.GameObject;
        if (source == null) {
            return true;
        }

        if (source.transform.IsChildOf(hero.transform)) {
            return false;
        }

        // The take-over is a sub-FSM, run by an action of the state that the FSM running it waits in
        return source.GetComponentInParent<HealthManager>(true) == null ||
               sender.Host?.ActiveState?.Actions.Any(action =>
                   action is RunFSM run && run.fsmTemplateControl.fsmTemplate?.name.StartsWith(TakeOverPrefix) == true
               ) == true;
    }

    /// <summary>
    /// Whether the local player is healing: from the moment the heal starts until it is over, whatever it was spent on.
    /// </summary>
    private static bool IsHealing(HeroController hero) {
        return hero.cState.isBinding ||
               hero.gameObject.LocateMyFSM(BindFsm)?.FsmVariables.FindFsmBool(IsBindingVariable) is { Value: true };
    }

    /// <summary>
    /// Holds back an event of a roar from the local player's FSM for roars while they heal, together with the events of
    /// roars that come after it before the heal is over. Taking control from the player and striking a pose, or giving
    /// control back and playing their idle clip, would throw the heal away.
    /// </summary>
    /// <returns>Whether the event was held back.</returns>
    private static bool HoldRoarWhileHealing(HeroController hero, string eventName) {
        if (_lettingRoarGoOn || !eventName.StartsWith(RoarEventPrefix, StringComparison.Ordinal)) {
            return false;
        }

        ForgetRoarOfOtherHero(hero);
        if (HeldRoarEvents.Count == 0) {
            if (!IsHealing(hero)) {
                return false;
            }

            _heldRoarHero = hero;
            Logger.Info(
                $"A roar ('{eventName}') from {DescribeFsm(FsmExecutionStack.ExecutingFsm)} reached the player while " +
                $"they healed ('{hero.gameObject.LocateMyFSM(BindFsm)?.ActiveStateName}'), so it waits for the heal " +
                "to be over"
            );
        }

        HeldRoarEvents.Add(eventName);
        return true;
    }

    /// <summary>
    /// Forgets the events of roars held for a player character that was replaced, like after going back to the menu.
    /// </summary>
    private static void ForgetRoarOfOtherHero(HeroController hero) {
        if (HeldRoarEvents.Count > 0 && _heldRoarHero != hero) {
            HeldRoarEvents.Clear();
        }
    }

    /// <summary>
    /// Lets the roars that reached the local player while they healed go on. Only a roar that still goes on holds the
    /// player, like the game's own wait for a heal does it: one that stopped in the meantime holds nobody, and a short
    /// one the game lets go of while the player heals. A roar that held the player before the heal still lets go of them,
    /// and a player who is being caught or wounded by now is left to that.
    /// </summary>
    private static void LetHeldRoarGoOn(HeroController hero) {
        var events = HeldRoarEvents.ToArray();
        HeldRoarEvents.Clear();
        if (hero.gameObject.LocateMyFSM(RoarFsm) is not { } roar) {
            return;
        }

        var state = roar.ActiveStateName;
        var lastExit = Array.FindLastIndex(events, RoarExitEvents.Contains);
        var from = state == "Idle"
            ? lastExit + 1
            : RoarLockStates.Contains(state)
                ? Mathf.Max(lastExit, 0)
                : events.Length;
        var goingOn = events.Skip(from)
            .Where(eventName => !eventName.StartsWith(RoarBurstPrefix, StringComparison.Ordinal))
            .ToArray();
        if (goingOn.Length == 0) {
            Logger.Info($"The heal is over, and the roar that waited for it holds nobody now ('{state}')");
            return;
        }

        Logger.Info($"The heal is over, so the roar that waited for it goes on: {string.Join(", ", goingOn)}");
        _lettingRoarGoOn = true;
        try {
            foreach (var eventName in goingOn) {
                roar.SendEvent(eventName);
            }
        } finally {
            _lettingRoarGoOn = false;
        }
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
        var from = sender == self ? "by the heal itself" : $"by {DescribeFsm(sender)}";
        Logger.Info(
            $"The heal was cut short in '{state.Name}' before it healed, by '{eventName}' {from}, taking it to '{to}' " +
            $"(crest '{PlayerData.instance?.CurrentCrestID}')"
        );
    }

    /// <summary>
    /// Names an FSM that does something to the local player, for the log: its object, its name and state, and the FSM
    /// that runs it if it is a sub-FSM.
    /// </summary>
    private static string DescribeFsm(Fsm? fsm) {
        if (fsm == null) {
            return "outside of any FSM";
        }

        return $"'{(fsm.GameObject == null ? "?" : fsm.GameObject.name)}' ({fsm.Name} in '{fsm.ActiveStateName}'" +
               $"{(fsm.Host == null ? "" : $", run by '{fsm.Host.Name}'")})";
    }

    /// <summary>
    /// Does what was held back from the local player once the heal has given control back to them: its FSM cools down
    /// or waits for the next heal again. With some crests that is one clip later than the heal itself ends, and that
    /// clip only gives control back if nothing plays another one on the player in the meantime. A roar that waited for
    /// the heal goes on after that.
    /// </summary>
    private void OnUpdate() {
        var hero = HeroController.instance;
        if (hero == null) {
            return;
        }

        ForgetRoarOfOtherHero(hero);
        if (_held.Count == 0 && HeldRoarEvents.Count == 0 ||
            hero.gameObject.LocateMyFSM(BindFsm).ActiveStateName is not ("Idle" or "Cooldown")) {
            return;
        }

        if (_held.Count > 0) {
            var held = _held.ToArray();
            _held.Clear();
            Logger.Info($"The heal ended, doing the {held.Length} thing(s) that other objects did to the player during it");
            foreach (var (source, apply) in held) {
                if (source != null) {
                    apply();
                }
            }
        }

        if (HeldRoarEvents.Count > 0) {
            LetHeldRoarGoOn(hero);
        }
    }
}
