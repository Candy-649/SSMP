using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity.Action;
using SSMP.Game.Client.Save;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// Catches that a part of the room tells a creature of. The strands that one boss puts up catch the player who
/// touches them, and tell the boss so with an event to everything that listens for it (SendEventToRegister), which
/// starts its slashes on them. The strands are the room's own in each game (see EntityRegistry.TryGetEntry), and each
/// game alone says whether its own player was caught: only that player's own body sets them off (see
/// <see cref="SetsOffRoomPartCatch"/>), and only a catch of that game's own touches them (USER 11-09 "两边丝线各自识别本机
/// 玩家有没有在线上", "总之本机自己认吧，没被丝线碰就不扣血"). The game that runs the creature plays its catch as the game does
/// (see <see cref="NoteOwnCatch"/>); the copy of one that another game runs plays it on the local player here by itself
/// to its end, and that game, told of it, plays it for everyone to see with its own player kept out of it (see
/// <see cref="HearRoomPartCatchOnCopy"/>, TakeInput). What a creature switches on of the room in a catch keeps off
/// every player whose own game did not catch them (see <see cref="CatchOf"/>).
/// </summary>
internal partial class Entity {
    /// <summary>
    /// The catches of this game's player that the FSMs of the creature play, which a part of the room told it of (see
    /// <see cref="HearRoomPartCatch"/>), each with its states, until the FSM plays a catch of a partner's (see
    /// PlayForPartner).
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
    /// Whether what came into a part of the room that catches the player sets it off: only the local player's own body
    /// does, as in a game of one. The strands of one boss take whatever of a few kinds of thing comes into them - they
    /// name no tag - and then ask only whether the player can be hurt, so anything else that came into them caught this
    /// game's player wherever they stood (USER 11-09 "有几次我绝对没有在丝线的范围内但是被抓了"). Only a part of the room
    /// whose touch leads to a state that asks it (see <see cref="AsksWhetherThePlayerCanBeCaught"/>).
    /// </summary>
    /// <param name="action">The action that heard the touch.</param>
    /// <param name="other">What came into the part.</param>
    internal static bool SetsOffRoomPartCatch(Trigger2dEvent action, Collider2D other) {
        if (other != null && HeroController.SilentInstance is { } hero && other.transform.IsChildOf(hero.transform) ||
            !string.IsNullOrEmpty(action.collideTag.Value) || action.sendEvent == null ||
            action.Fsm is not { ActiveState: { } state } fsm ||
            EntityFsmActions.FindTransition(fsm, state, action.sendEvent.Name) is not { } into ||
            !AsksWhetherThePlayerCanBeCaught(into)) {
            return true;
        }

        return fsm.Owner is not PlayMakerFSM owner || !CoopHits.IsRoomObject(owner);
    }

    /// <summary>
    /// Has what a part of the room that caught the local player tells everything that listens for it heard as a catch
    /// of them (see <see cref="CatchesTheLocalPlayer"/>). A creature that this game runs hears it itself, and what its
    /// FSM plays from where the event takes it is noted first as a catch of this game's player (see
    /// <see cref="CatchOf"/>); the copy of one that another game runs plays it on them here by itself (see
    /// <see cref="HearRoomPartCatchOnCopy"/>).
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
    /// Whether the object of a creature hears an event through a register of its own, as it hears what a part of the
    /// room tells everything that listens for it (see <see cref="HearRoomPartCatch"/>), rather than from a part of its
    /// own that tells it directly.
    /// </summary>
    /// <param name="creature">The object.</param>
    /// <param name="eventName">The event, by the name that the register passes it on under.</param>
    private static bool HearsByRegister(GameObject creature, string eventName) {
        foreach (var register in creature.GetComponents<EventRegister>()) {
            if (PassedOnAs(register, register.SubscribedEvent) == eventName) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Notes, for a creature that this game runs, that each FSM a register of it passes the event of a part that caught
    /// this game's player on to plays a catch of that player from the state the event takes it to (see
    /// <see cref="CatchOf"/>). Noted before the FSM hears it, which takes it there and on at once. What was kept off
    /// this game's player while the creature played a catch of another's comes back: it is this player's catch now.
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
                LetThePlayerBackIn(true);
                Logger.Info(
                    $"'{sender.GameObjectName}' of the room caught the local player and tells entity {Id} ({Type}) " +
                    $"'{told}', so what its '{fsm.FsmName}' plays from '{into.Name}' is a catch of them"
                );
            }
        }
    }

    /// <summary>
    /// Has the copy play what a part of the room that caught the local player told everything that listens for it, as
    /// each register of the creature passes it on to the FSM it names, or to all of them: on the local player, here, by
    /// itself to its end, the game that runs the creature being only told of it (see
    /// <see cref="PlayTheCatchByItself"/>, TakeInput). Whatever else the copy plays here gives way to it, as it does to
    /// any catch of the local player (see <see cref="PlayHere"/>). Nothing is sent of one that the copy can't play, and
    /// another part that caught the player in the same catch changes nothing.
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

            if (PlaysACatchByItself) {
                Logger.Info(
                    $"'{sender.GameObjectName}' of the room caught the local player too, of whom the copy of entity " +
                    $"{Id} ({Type}) plays a catch already"
                );
                return;
            }

            var told = PassedOnAs(register, eventName);
            var taken = OnCopyCaught(
                sender, register.targetFsm != null ? register.targetFsm.FsmName : null, told, true
            );
            if (taken && _runHereLed) {
                PlayTheCatchByItself("the scene host is only told of it");
            }

            Logger.Info(
                $"'{sender.GameObjectName}' of the room caught the local player and tells the copy of entity {Id} " +
                $"({Type}) '{told}', " +
                (taken ? "which it plays on them here by itself" : "which no FSM of the copy takes from where it is")
            );
        }
    }

    /// <summary>
    /// Whose catch an FSM of a creature that this game runs plays in a state: another player's, in one that it plays
    /// for a partner (see <see cref="PlayForPartner"/>), one that their game leads (see <see cref="Lead"/>), or one
    /// that it goes out of the shortest way as their game no longer does (see <see cref="LetGo"/>) - taken for none
    /// then, the slashes it walks through would hurt this game's player; this game's player's own, in one that a part
    /// of the room told it of (see <see cref="HearRoomPartCatch"/>); none in no catch, and for a copy.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="state">The state.</param>
    internal static CatchKind CatchOf(HutongGames.PlayMaker.Fsm fsm, FsmState? state) {
        if (state == null || EntitiesByRoomObject.Count == 0 || fsm.GameObject is not { } owner || owner == null ||
            !EntitiesByRoomObject.TryGetValue(owner, out var entity) || entity._isControlled) {
            return CatchKind.None;
        }

        foreach (var lead in entity._leads) {
            if (lead.Fsm == fsm && lead.States.Contains(state)) {
                return CatchKind.Another;
            }
        }

        if (entity._lettingGo is { } going && going.Fsm == fsm && going.States.Contains(state)) {
            return CatchKind.Another;
        }

        foreach (var played in entity._playedForPartner) {
            if (played.Fsm == fsm && played.States.Contains(state)) {
                return CatchKind.Another;
            }
        }

        return entity._ownCatches.Count > 0 && entity._ownCatches.TryGetValue(fsm, out var states) &&
               states.Contains(state)
            ? CatchKind.Own
            : CatchKind.None;
    }

    /// <summary>
    /// Whose catch a creature plays (see <see cref="CatchOf"/>).
    /// </summary>
    internal enum CatchKind {
        /// <summary>None.</summary>
        None,

        /// <summary>This game's player's own.</summary>
        Own,

        /// <summary>Another player's, whom this game's player is kept out of.</summary>
        Another
    }
}
