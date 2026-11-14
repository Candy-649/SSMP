using System;
using SSMP.Game.Client.Entity.Action;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// A part of a creature that a player strikes and that tells the creature so, as the tendril of a vine tells it to
/// pull back. The part runs in each game and felt the local player's strike on the copy, but the copy's own FSMs are
/// switched off and heard nothing of it: the vine took no notice of the scene client's blade or harpoon on its tendril
/// (USER 11-14 "我飞针打到藤蔓，藤蔓并没有缩回去"). The copy takes it as any other strike of the local player
/// (CoopHits.PlayOrSend).
///
/// A creature that holds one player lets them go when another player strikes such a part of it, where its registry
/// entry says with what (EntityRegistryEntry.StruckFree): the vine pulling one player up lets go of them when the
/// other strikes its tendril (USER 11-14 "加", asked whether a strike should free the one it holds). In the game itself
/// nobody else is there to strike it. The scene host's game lets go of its own player, and tells the game of a player
/// whose catch it follows to let go of theirs (see <see cref="StrikeFrees"/>). With more players, any of them but the
/// one held frees them.
/// </summary>
internal partial class Entity {
    /// <summary>
    /// Raised on a scene client when the local player struck a part of the copy of an entity that told the copy's FSM
    /// at the given index the given event, which leads anywhere from where the FSM is or frees the player it holds
    /// there: CoopHits plays it and sends it to the scene host.
    /// </summary>
    public static event Action<Entity, byte, string>? CopyPartStruck;

    /// <summary>
    /// Raised on the scene host when a strike freed the player whose game leads the catch that the FSM of an entity at
    /// the given index follows (see <see cref="StrikeFrees"/>): CoopHits tells that game, by the ID of its player, the
    /// event that lets them go.
    /// </summary>
    public static event Action<Entity, ushort, byte, string>? LedCatchStruckFree;

    /// <summary>
    /// Takes it that a part of the copy felt the local player strike it and told the copy so (see
    /// <see cref="HearCopyTold"/>). Each FSM of the copy that the event leads anywhere from the state it is in, or that
    /// holds another player there whom a strike with it frees, is struck (see <see cref="CopyPartStruck"/>); one that
    /// each game runs by itself heard it already. So is one that plays a move by itself, whatever its own state says:
    /// the scene host's creature may be past the move already, where the strike leads somewhere, and the scene host's
    /// game judges it then. A catch of the local player that the copy plays here is theirs: their own strike frees
    /// nobody.
    /// </summary>
    /// <param name="fsmName">The name of the FSM that the part told, or null for all of them.</param>
    /// <param name="eventName">The event with which the part told the copy.</param>
    private void OnCopyPartStruck(string? fsmName, string eventName) {
        if (!_isControlled) {
            return;
        }

        var freeing = EntityRegistry.StruckFreeBy(Type, eventName);
        for (var index = 0; index < _fsms.Client.Count && index < byte.MaxValue; index++) {
            var fsm = _fsms.Client[index];
            if (fsm == null || fsm.enabled || !string.IsNullOrEmpty(fsmName) && fsm.FsmName != fsmName) {
                continue;
            }

            if (_runHere == fsm && _runHereMove || CopyTakes((byte) index, eventName) ||
                freeing != null && _runHere != fsm && CopyTakes((byte) index, freeing)) {
                CopyPartStruck?.Invoke(this, (byte) index, eventName);
                continue;
            }

            Logger.Info(
                $"The local player struck a part of the copy of entity {Id}, which told '{fsm.FsmName}' " +
                $"'{eventName}', which leads nowhere from '{StateOfCopy(fsm)?.Name}'"
            );
        }
    }

    /// <summary>
    /// Frees, in the scene host's game, the player whom the FSM of the room's creature at the given index holds, as
    /// another player struck a part of it that tells the FSM the given event, if the FSM is where the freeing event of
    /// the creature's entry takes it (EntityRegistryEntry.StruckFree): this game's player is let go here, and the game
    /// of a player whose catch the FSM follows is told to let them go (see <see cref="LedCatchStruckFree"/>). One that
    /// plays another player's catch that their game no longer leads holds nobody here (see <see cref="CatchOf"/>).
    /// </summary>
    /// <param name="fsmIndex">The index of the FSM.</param>
    /// <param name="eventName">The event that the struck part tells the FSM.</param>
    /// <param name="striker">Who struck it, for the log.</param>
    /// <returns>Whether the strike freed anyone.</returns>
    public bool StrikeFrees(byte fsmIndex, string eventName, string striker) {
        if (EntityRegistry.StruckFreeBy(Type, eventName) is not { } freeing || fsmIndex >= _fsms.Host.Count ||
            _fsms.Host[fsmIndex] is not { } fsm || fsm == null || fsm.Fsm.ActiveState is not { } state ||
            EntityFsmActions.FindTransition(fsm.Fsm, state, freeing) == null) {
            return false;
        }

        if (LedFsms.TryGetValue(fsm.Fsm, out var lead)) {
            Logger.Info(
                $"[Catches] {striker} struck entity {Id} free of player {lead.LeaderId}, whom its '{fsm.FsmName}' " +
                $"holds in '{state.Name}' as their game leads it, so their game is told '{freeing}'"
            );
            LedCatchStruckFree?.Invoke(this, lead.LeaderId, fsmIndex, freeing);
            return true;
        }

        if (CatchOf(fsm.Fsm, state) == CatchKind.Another) {
            return false;
        }

        Logger.Info(
            $"[Catches] {striker} struck entity {Id}, whose '{fsm.FsmName}' holds this game's player in " +
            $"'{state.Name}', so it takes '{freeing}', which lets them go"
        );
        fsm.SendEvent(freeing);
        return true;
    }

    /// <summary>
    /// Lets the local player go from the catch of an entity that this game leads on the FSM at the given index, which
    /// another player struck free of them in the scene host's game (see <see cref="StrikeFrees"/>): the copy's FSM,
    /// which plays the catch here, takes the freeing event, if it still holds them where that leads anywhere. Where it
    /// goes from there is said to the scene host as every step of the catch is.
    /// </summary>
    /// <param name="fsmIndex">The index of the FSM.</param>
    /// <param name="freeing">The event that lets them go.</param>
    public void FreeFromTheCatch(byte fsmIndex, string freeing) {
        if (_runHere is not { } copyFsm || copyFsm == null || !_runHereLed || _runHereIndex != fsmIndex ||
            copyFsm.Fsm.ActiveState is not { } state ||
            EntityFsmActions.FindTransition(copyFsm.Fsm, state, freeing) == null) {
            Logger.Info(
                $"[Catches] Another player struck entity {Id} free of the local player, whom its copy no longer " +
                "holds here"
            );
            return;
        }

        Logger.Info(
            $"[Catches] Another player struck entity {Id} free of the local player, so its '{copyFsm.FsmName}' " +
            $"takes '{freeing}' from '{state.Name}' here"
        );
        copyFsm.Fsm.Event(freeing);
    }

    /// <summary>
    /// Hears a part of the room's own creature, which this game runs, telling the creature that the local player struck
    /// it while the creature holds another player in a catch that their game leads, which the strike may free them of
    /// (see <see cref="StrikeFrees"/>). The FSM that holds them goes only where their game leads it, so it takes no
    /// notice of the strike here.
    /// </summary>
    /// <param name="sender">The FSM of the part.</param>
    /// <param name="target">The object that it tells.</param>
    /// <param name="fsmName">The name of the FSM of the object that it tells, or null for all of them.</param>
    /// <param name="eventName">The event.</param>
    private static void HearRoomPartStruck(
        HutongGames.PlayMaker.Fsm sender,
        GameObject target,
        string? fsmName,
        string eventName
    ) {
        if (LedFsms.Count == 0 || sender != _triggeredFsm ||
            !EntitiesByRoomObject.TryGetValue(target, out var entity) || sender.GameObject is not { } part ||
            part == target || !part.transform.IsChildOf(target.transform) ||
            !IsStrikeOfTheLocalPlayer(sender.TriggerCollider2D)) {
            return;
        }

        foreach (var lead in entity._leads) {
            if (string.IsNullOrEmpty(fsmName) || lead.Fsm.Name == fsmName) {
                entity.StrikeFrees(lead.FsmIndex, eventName, "The local player");
            }
        }
    }
}
