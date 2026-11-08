using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using SSMP.Game.Client.Entity.Action;
using SSMP.Game.Client.Save;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// Catches that a part of the room tells a creature of. The strands that one boss puts up catch the player who
/// touches them, and tell the boss so with an event to everything that listens for it (SendEventToRegister), which
/// starts its slashes on them. The strands are the room's own in each game (see EntityRegistry.TryGetEntry), so each
/// game's strands catch that game's player only, and the copy of the boss heard nothing of it, its FSMs being switched
/// off: only the scene host's player was ever caught (USER 11-08 "…丝线的动作是不是一致的"). Now the copy takes what
/// the part tells it as a catch of the local player that this game leads (see <see cref="OnCopyCaught"/>), and the
/// game that runs the creature notes that what it plays from there is a catch of its own player (see
/// <see cref="CaughtIn"/>). Two players caught at once are each slashed in their own game (see TakeInput).
/// </summary>
internal partial class Entity {
    /// <summary>
    /// The catches of this game's player that the FSMs of the creature play, which a part of the room told it of (see
    /// <see cref="HearRoomPartCatch"/>), each with its states.
    /// </summary>
    private readonly Dictionary<HutongGames.PlayMaker.Fsm, HashSet<FsmState>> _ownCatches = new();

    /// <summary>
    /// Whether an FSM that tells everything that listens for it an event tells it because it caught this game's
    /// player: it is an FSM of a part of the room, its state asks whether the player can be hurt or grabbed, and the
    /// event is one about the player (see EntityFsmActions.IsPlayerEvent). The part runs in each game by itself, so
    /// what it caught is this game's player, of whom the other games never hear it.
    /// </summary>
    /// <param name="sender">The FSM.</param>
    /// <param name="eventName">The event.</param>
    internal static bool CatchesTheLocalPlayer(HutongGames.PlayMaker.Fsm sender, string eventName) {
        return EntityFsmActions.IsPlayerEvent(eventName) && sender.Owner is PlayMakerFSM { enabled: true } owner &&
               sender.ActiveState is { } state && AsksWhetherThePlayerCanBeCaught(state) &&
               CoopHits.IsRoomObject(owner);
    }

    /// <summary>
    /// Whether a state asks whether the player can be hurt or grabbed, as a part does that catches them.
    /// </summary>
    private static bool AsksWhetherThePlayerCanBeCaught(FsmState state) {
        foreach (var action in state.Actions) {
            var name = action.GetType().Name;
            if (name.StartsWith("CanHeroTakeDamage", StringComparison.Ordinal) ||
                name.StartsWith("CanHeroBeGrabbed", StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Has what a part of the room that caught the local player tells everything that listens for it heard as a catch
    /// of them (see <see cref="CatchesTheLocalPlayer"/>). A creature that this game runs hears it itself, and what its
    /// FSM plays from where the event takes it is noted first as a catch of this game's player (see
    /// <see cref="CaughtIn"/>); the copy of one that another game runs takes it as a catch that this game leads, as the
    /// creature would have heard it in a game of one (see <see cref="OnCopyCaught"/>).
    /// </summary>
    /// <param name="sender">The FSM of the part.</param>
    /// <param name="eventName">The event.</param>
    /// <param name="tell">What tells it to everything that listens (the game's own SendEventToRegister).</param>
    internal static void HearRoomPartCatch(HutongGames.PlayMaker.Fsm sender, string eventName, System.Action tell) {
        foreach (var entity in EntitiesByRoomObject.Values) {
            entity.NoteOwnCatch(sender, eventName);
        }

        tell();

        foreach (var entity in new List<Entity>(EntitiesByCopy.Values)) {
            entity.HearRoomPartCatchOnCopy(sender, eventName);
        }
    }

    /// <summary>
    /// The event under which a register passes an event on to the FSMs it names: its alias, if it has one.
    /// </summary>
    /// <param name="register">The register.</param>
    /// <param name="eventName">The event it is told.</param>
    private static string PassedOnAs(EventRegister register, string eventName) {
        return (int) register.aliasEventMode == 1 && !string.IsNullOrEmpty(register.aliasEvent)
            ? register.aliasEvent
            : eventName;
    }

    /// <summary>
    /// Notes, for a creature that this game runs, that each FSM a register of it passes the event of a part that caught
    /// this game's player on to plays a catch of that player from the state the event takes it to (see
    /// <see cref="CaughtIn"/>). Noted before the FSM hears it, which takes it there and on at once.
    /// </summary>
    /// <param name="sender">The FSM of the part.</param>
    /// <param name="eventName">The event.</param>
    private void NoteOwnCatch(HutongGames.PlayMaker.Fsm sender, string eventName) {
        if (_isControlled || Object.Host == null || !Object.Host.activeInHierarchy) {
            return;
        }

        foreach (var register in Object.Host.GetComponents<EventRegister>()) {
            if (register.SubscribedEvent != eventName) {
                continue;
            }

            var told = PassedOnAs(register, eventName);
            foreach (var fsm in _fsms.Host) {
                if (fsm == null || register.targetFsm != null && register.targetFsm != fsm ||
                    fsm.Fsm.ActiveState is not { } state ||
                    EntityFsmActions.FindTransition(fsm.Fsm, state, told) is not { } into) {
                    continue;
                }

                _ownCatches[fsm.Fsm] = EntityFsmActions.StatesOnlyThrough(fsm.Fsm, into);
                Logger.Info(
                    $"'{sender.GameObjectName}' of the room caught the local player and tells entity {Id} ({Type}) " +
                    $"'{told}', so what its '{fsm.FsmName}' plays from '{into.Name}' is a catch of them"
                );
            }
        }
    }

    /// <summary>
    /// Has the copy take what a part of the room that caught the local player told everything that listens for it, as
    /// each register of the creature passes it on to the FSM it names, or to all of them: a catch that this game
    /// leads (see <see cref="OnCopyCaught"/>).
    /// </summary>
    /// <param name="sender">The FSM of the part.</param>
    /// <param name="eventName">The event.</param>
    private void HearRoomPartCatchOnCopy(HutongGames.PlayMaker.Fsm sender, string eventName) {
        if (!_isControlled || Object.Client == null || !Object.Client.activeInHierarchy) {
            return;
        }

        foreach (var register in Object.Client.GetComponents<EventRegister>()) {
            if (register.SubscribedEvent != eventName) {
                continue;
            }

            var told = PassedOnAs(register, eventName);
            var taken = OnCopyCaught(
                sender, register.targetFsm != null ? register.targetFsm.FsmName : null, told, false
            );
            Logger.Info(
                $"'{sender.GameObjectName}' of the room caught the local player and tells the copy of entity {Id} " +
                $"({Type}) '{told}', " + (taken ? "which this game leads" : "which no FSM of the copy takes from where it is")
            );
        }
    }

    /// <summary>
    /// The character of the player that an FSM of a creature this game runs plays a catch of in a state: this game's
    /// own, in a catch that a part of the room told it of (see <see cref="HearRoomPartCatch"/>), or the figure of the
    /// partner whose game leads it (see <see cref="Lead"/>). Null in no catch, for a copy, and for a partner with no
    /// figure here.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="state">The state.</param>
    internal static GameObject? CaughtIn(HutongGames.PlayMaker.Fsm fsm, FsmState? state) {
        if (state == null || EntitiesByRoomObject.Count == 0 || fsm.GameObject is not { } owner || owner == null ||
            !EntitiesByRoomObject.TryGetValue(owner, out var entity) || entity._isControlled) {
            return null;
        }

        foreach (var lead in entity._leads) {
            if (lead.Fsm == fsm && lead.States.Contains(state)) {
                return FigureOf?.Invoke(lead.LeaderId);
            }
        }

        if (entity._ownCatches.Count > 0 && entity._ownCatches.TryGetValue(fsm, out var states) &&
            states.Contains(state)) {
            var hero = HeroController.SilentInstance;
            return hero != null ? hero.gameObject : null;
        }

        return null;
    }
}
