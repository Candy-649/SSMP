using System;
using System.Collections.Generic;
using SSMP.Game.Client.Entity.Action;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// The signs of the local player: events that one of their own state machines tells everything in the game that
/// listens for it (SendEventToRegister) - the harpoon thrown, a taunt, a bind. In a game of one, all that hears a sign
/// hears it of the one player there is, and a creature that it sets off goes after that player. Here each game hears
/// its own player's signs: what of a creature's copy listens for one plays what it leads to here and in the scene
/// host's game, where the creature goes after this player (see <see cref="OnCopyHeardOwnSign"/>), and a creature that
/// this game runs goes after the player whose sign set it off (see <see cref="HearOwnSign"/>).
/// </summary>
internal partial class Entity {
    /// <summary>
    /// The signs of a player that creatures listen for: the harpoon thrown, a taunt, a taunt with the ring, a bind.
    /// What else the player's own state machines tell everything - that they were hurt, came into a room, opened the
    /// map - says how the player is rather than tells anyone something, and is heard as it was. The start of a bind,
    /// which a few bosses read to fight a player who binds another way, waits for a rule of its own.
    /// </summary>
    internal static readonly HashSet<string> PlayerSigns =
        ["THROWING HARPOON", "DID TAUNT", "DID TAUNT POSHANKA", "DID BIND"];

    /// <summary>
    /// The sign that the local player gives right now, or null (see <see cref="HearOwnSign"/>). What hears it in the
    /// same breath hears it of them: the FSMs of a copy that each game runs by itself and that listen for it tell the
    /// creature what to make of it (see <see cref="HearCopyTold"/>), and whatever looks at a player for it looks at
    /// them (GamePatcher.GetFsmActionTarget).
    /// </summary>
    internal static string? OwnSign;

    /// <summary>
    /// Raised on a scene client when an FSM of the copy of an entity that each game runs by itself heard a sign of the
    /// local player and told the creature an event for it (see <see cref="OnCopyHeardOwnSign"/>): the entity, the
    /// index of the FSM that takes the event, the event and the sign. What the event leads to is played on the copy at
    /// once and sent to the scene host, where the creature goes after the local player (see <see cref="PlayHere"/>).
    /// </summary>
    public static event Action<Entity, byte, string, string>? CopyHeardOwnSign;

    /// <summary>
    /// Has all that listens for a sign of the local player hear it of them. The game tells it to all that is
    /// registered for it, with <see cref="OwnSign"/> set while it does, and each creature that this game runs and that
    /// the sign set off goes after the local player (see <see cref="GoAfterOwnSign"/>).
    /// </summary>
    /// <param name="sign">The sign.</param>
    /// <param name="tell">What tells it to all that is registered for it (the game's own SendEventToRegister).</param>
    internal static void HearOwnSign(string sign, System.Action tell) {
        var before = StatesOfRunCreatures();
        var outer = OwnSign;
        OwnSign = sign;
        try {
            tell();
        } finally {
            OwnSign = outer;
        }

        GoAfterOwnSign(before, sign);
    }

    /// <summary>
    /// The state of each FSM of the creatures that this game runs and that can be hurt, other than one that each game
    /// runs by itself, to tell after a sign of the local player which of them it set off (see
    /// <see cref="GoAfterOwnSign"/>).
    /// </summary>
    private static List<(Entity Entity, string?[] States)> StatesOfRunCreatures() {
        var states = new List<(Entity, string?[])>();
        foreach (var entity in EntitiesByRoomObject.Values) {
            var creature = entity.Object.Host;
            if (entity._isControlled || creature == null || !creature.activeInHierarchy ||
                !creature.TryGetComponent<HealthManager>(out _)) {
                continue;
            }

            var fsmStates = new string?[entity._fsms.Host.Count];
            for (var index = 0; index < fsmStates.Length; index++) {
                var fsm = entity._fsms.Host[index];
                fsmStates[index] = fsm == null || entity.IsRunByEachGame(fsm) ? null : fsm.ActiveStateName;
            }

            states.Add((entity, fsmStates));
        }

        return states;
    }

    /// <summary>
    /// Has each creature that this game runs and that a sign of the local player set off - an FSM of it went elsewhere
    /// as it heard it, like one that leapt out of the harpoon's way or woke to a taunt - go after the local player, as
    /// it goes after the one player there is in a game of one. One that the partner's sign set off goes after the
    /// partner (CoopHits.GoAfterSetter). One that cannot be hurt, like an ally that fights beside the players, keeps to
    /// what it goes after, which may be the creature it fights.
    /// </summary>
    /// <param name="before">The states of the FSMs before the sign (see <see cref="StatesOfRunCreatures"/>).</param>
    /// <param name="sign">The sign.</param>
    private static void GoAfterOwnSign(List<(Entity Entity, string?[] States)> before, string sign) {
        var hero = HeroController.SilentInstance;
        if (hero == null) {
            return;
        }

        foreach (var (entity, states) in before) {
            for (var index = 0; index < states.Length && index < entity._fsms.Host.Count; index++) {
                var fsm = entity._fsms.Host[index];
                if (states[index] is not { } from || fsm == null || entity.Object.Host == null ||
                    fsm.ActiveStateName == from) {
                    continue;
                }

                Logger.Info(
                    $"The local player's sign '{sign}' took the '{fsm.FsmName}' of entity {entity.Id} " +
                    $"({entity.Type}) from '{from}' to '{fsm.ActiveStateName}', so it goes after them"
                );
                GamePatcher.SetTargetOf(entity.Object.Host, hero.gameObject);
                break;
            }
        }
    }

    /// <summary>
    /// Takes it that an FSM of the copy that each game runs by itself heard a sign of the local player and told the
    /// creature an event for it: the harpoon thrown its way, which it leaps out of; a taunt, which it turns on; a bind
    /// beside an ally that is down, who gets up. In the game that runs the creature, its FSM goes on from there at
    /// once; the copy's FSMs are switched off and did not hear it, and the scene host's game never heard the sign at
    /// all, so the creature let the other player's harpoon hook it. Each FSM of the copy that it told is told the event
    /// (see <see cref="TellCopyOwnSign"/>).
    /// </summary>
    /// <param name="fsmName">The name of the FSM that the listening one told, or null for all of them.</param>
    /// <param name="eventName">The event.</param>
    /// <param name="sign">The sign of the local player that it heard.</param>
    private void OnCopyHeardOwnSign(string? fsmName, string eventName, string sign) {
        if (!_isControlled || Object.Client == null || CopyHeardOwnSign == null) {
            return;
        }

        for (var index = 0; index < _fsms.Client.Count && index <= byte.MaxValue; index++) {
            if (string.IsNullOrEmpty(fsmName) ||
                _fsms.Client[index] is { } fsm && fsm != null && fsm.FsmName == fsmName) {
                TellCopyOwnSign(index, eventName, sign);
            }
        }
    }

    /// <summary>
    /// Tells an FSM of the copy an event for a sign of the local player, if the FSM is switched off here and goes
    /// anywhere on it from where the scene host says it is: that plays here at once and in the scene host's game (see
    /// <see cref="CopyHeardOwnSign"/>). One that runs here heard it itself, and one that each game runs by itself
    /// hears its own player only.
    /// </summary>
    /// <param name="index">The index of the FSM.</param>
    /// <param name="eventName">The event.</param>
    /// <param name="sign">The sign.</param>
    private void TellCopyOwnSign(int index, string eventName, string sign) {
        var fsm = _fsms.Client[index];
        if (fsm == null || fsm.enabled || IsRunByEachGame(fsm) || StateOfCopy(fsm) is not { } state ||
            EntityFsmActions.FindTransition(fsm.Fsm, state, eventName) == null) {
            return;
        }

        CopyHeardOwnSign?.Invoke(this, (byte) index, eventName, sign);
    }

    /// <summary>
    /// Whether an FSM leaves a state anywhere on an event, which one that listens for a sign of the player does.
    /// </summary>
    private static bool ListensFor(HutongGames.PlayMaker.Fsm fsm, string eventName) {
        foreach (var transition in fsm.GlobalTransitions) {
            if (transition.EventName == eventName) {
                return true;
            }
        }

        foreach (var state in fsm.States) {
            foreach (var transition in state.Transitions) {
                if (transition.EventName == eventName) {
                    return true;
                }
            }
        }

        return false;
    }
}
