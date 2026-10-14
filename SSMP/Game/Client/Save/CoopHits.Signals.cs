using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity.Component;
using UnityEngine;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// What the local player's own state machines tell a creature straight, rather than through a hit: the harpoon tells
/// what it strikes that it is hooked (HARPOON HOOKED, HARPOONED), and what it bounces off that it took the harpoon's
/// blow (HARPOON DAMAGE). Told to the copy of a creature that the scene host runs, none of it went further: the copy
/// runs no state machine of its own, and the creature in the scene host's game never heard of it. A flying automaton
/// that stops and turns on the player it hooks flew on, under the player dashing at it, and one that the harpoon kills
/// at once lived (USER 10-14, "飞针了之后一直到不了合适的高度"). Now the copy takes what leads it anywhere at once, as
/// the creature would in a game of one, and the scene host's game plays the same from where the copy was and has the
/// creature go after this player (see <see cref="PlayOrSend"/>).
/// </summary>
internal partial class CoopHits {
    /// <summary>
    /// Registers the hooks on what the local player's state machines tell a creature.
    /// </summary>
    private void RegisterSignalHooks() {
        AddHook(
            typeof(SendEventByName).GetMethod(
                nameof(SendEventByName.OnEnter), InstanceFlags, null, Type.EmptyTypes, null
            ),
            new Action<Action<SendEventByName>, SendEventByName>(OnSendEventByName)
        );
        AddHook(
            typeof(SendEventByNameUpwards).GetMethod(
                nameof(SendEventByNameUpwards.OnEnter), InstanceFlags, null, Type.EmptyTypes, null
            ),
            new Action<Action<SendEventByNameUpwards>, SendEventByNameUpwards>(OnSendEventByNameUpwards)
        );
    }

    /// <summary>
    /// An event told to an object at once (SendEventByName): one that a state machine of the local player tells the
    /// copy of a creature goes on to it (see <see cref="TellCopy"/>).
    /// </summary>
    private void OnSendEventByName(Action<SendEventByName> orig, SendEventByName self) {
        orig(self);

        if (self.everyFrame || self.delay.Value > 0f || !IsLocalPlayers(self.Fsm) ||
            self.eventTarget is not { target: FsmEventTarget.EventTarget.GameObject or
                FsmEventTarget.EventTarget.GameObjectFSM } eventTarget) {
            return;
        }

        var fsmName = eventTarget.target == FsmEventTarget.EventTarget.GameObjectFSM
            ? eventTarget.fsmName.Value
            : null;
        TellCopy(
            self.Fsm, self.Fsm.GetOwnerDefaultTarget(eventTarget.gameObject), self.sendEvent.Value, fsmName,
            eventTarget.sendToChildren.Value
        );
    }

    /// <summary>
    /// An event told to an object and everything above it (SendEventByNameUpwards): one that a state machine of the
    /// local player tells the copy of a creature, or a part of it, goes on to it (see <see cref="TellCopy"/>).
    /// </summary>
    private void OnSendEventByNameUpwards(Action<SendEventByNameUpwards> orig, SendEventByNameUpwards self) {
        orig(self);

        if (!IsLocalPlayers(self.Fsm)) {
            return;
        }

        var target = self.Fsm.GetOwnerDefaultTarget(self.Target);
        if (target != null) {
            TellCopyUpwards(self.Fsm, target.transform, self.EventName.Value, []);
        }
    }

    /// <summary>
    /// Tells the copies an event that goes to an object and on up, the way the game passes it (SendUpRecursive): to the
    /// object, on up through what holds it, and from a part that follows another object by its body
    /// (Rb2dFollowWithVelocity) on to that object and up through what holds it too.
    /// </summary>
    /// <param name="sender">The local player's state machine.</param>
    /// <param name="transform">The object.</param>
    /// <param name="eventName">The event.</param>
    /// <param name="told">The objects told already, each of which is told once.</param>
    private void TellCopyUpwards(Fsm sender, Transform? transform, string? eventName, HashSet<Transform> told) {
        if (transform == null || !told.Add(transform)) {
            return;
        }

        TellCopy(sender, transform.gameObject, eventName, null, false);
        TellCopyUpwards(sender, transform.parent, eventName, told);
        if (transform.TryGetComponent<Rb2dFollowWithVelocity>(out var follow)) {
            TellCopyUpwards(sender, follow.Target, eventName, told);
        }
    }

    /// <summary>
    /// Whether a state machine is one of the local player's own, on their hero or on a part of it.
    /// </summary>
    private static bool IsLocalPlayers(Fsm? fsm) {
        var hero = HeroController.SilentInstance;
        return hero != null && fsm?.GameObject is { } owner && owner != null &&
               owner.transform.IsChildOf(hero.transform);
    }

    /// <summary>
    /// Has the copy of a creature take an event that a state machine of the local player told an object of it, in each
    /// of the copy's state machines on the object that it leads anywhere from where they are: here at once if the copy
    /// can, and in the scene host's game, where the creature goes after this player (see <see cref="PlayOrSend"/>).
    /// What it leads nowhere is not sent: it leads nowhere there either.
    /// </summary>
    /// <param name="sender">The local player's state machine.</param>
    /// <param name="target">The object it told the event.</param>
    /// <param name="eventName">The event.</param>
    /// <param name="fsmName">The one state machine of the object that it told, or null for all of them.</param>
    /// <param name="children">Whether it told the state machines of everything under the object as well.</param>
    private void TellCopy(Fsm sender, GameObject? target, string? eventName, string? fsmName, bool children) {
        if (target == null || string.IsNullOrEmpty(eventName) || !CanSendEntityTouch() ||
            FindCopyHolding(target) is not { } copied) {
            return;
        }

        var fsms = children ? target.GetComponentsInChildren<PlayMakerFSM>() : target.GetComponents<PlayMakerFSM>();
        foreach (var fsm in fsms) {
            if (fsmName != null && fsm.FsmName != fsmName) {
                continue;
            }

            var index = copied.ClientFsms.IndexOf(fsm);
            if (index < 0 || index > byte.MaxValue || !copied.CopyTakes((byte) index, eventName!)) {
                continue;
            }

            HealthManagerComponent.OwnSignalPlaying = true;
            try {
                PlayOrSend(copied, (byte) index, eventName!, $"used '{sender.Name}' on", setOff: true);
            } finally {
                HealthManagerComponent.OwnSignalPlaying = false;
            }
        }
    }
}
