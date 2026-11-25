using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity;
using SSMP.Game.Client.Entity.Action;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The strikes that free the player whom a creature that every game runs by itself holds, as another player strikes a
/// part of it that tells it so, where its entry says with what (EntityRegistryEntry.StruckFree): the maw that hangs its
/// tendril from the ceiling lets go of the player it pulls up as another player strikes the tendril (USER 11-14 "加",
/// asked whether a strike should free the one it holds). In the game itself nobody else is there to strike it. What
/// feels the strike runs in each game for its own player (see OwnCreatures.RunsInEachGame), and the creature that holds
/// another player goes only where the game of that player leads it (see CoopSave.OwnLedMoves), which takes no notice
/// of the strike. So the game of the player who struck tells the game of the player held (see
/// <see cref="TellStruckFree"/>), which lets them go as the creature lets go of a player who is hurt, and leads that
/// too (see <see cref="TakeStruckFree"/>). The player held does not free themselves. With three or four players, any
/// of them but the one held frees them.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What an update of a creature that every game runs by itself is when it tells the game of the player that it
    /// holds that the player of the game that sent it struck it free of them (see <see cref="TellStruckFree"/>): the
    /// state that it holds them in there, the player held in <see cref="CoopSaveUpdate.Part"/>, and the event that
    /// lets them go in <see cref="CoopSaveUpdate.Names"/>.
    /// </summary>
    private const ushort OwnStruckFreePart = 19;

    /// <summary>
    /// Tells the game of the player whom a creature that every game runs by itself holds here, in a move that their
    /// game leads (see CoopSave.OwnLedMoves), that this game's player struck it free of them, as a state machine of it
    /// that each game runs by itself goes into a state that tells the creature that a part of it was struck, by an
    /// event that frees the player it holds (see EntityRegistry.StruckFreeBy): the strike of this game's player, which
    /// only this game felt.
    /// </summary>
    private void TellStruckFree(PlayMakerFSM machine, FsmState toState) {
        if (_ledMoves.Count == 0 || OwnCreatures.Owning(machine.gameObject) is not { } creature) {
            return;
        }

        foreach (var action in toState.Actions) {
            if (!action.Enabled || action is not SendEventByName { sendEvent.Value: { } struck } ||
                !EntityRegistry.TryGetEntry(creature, out var entry) ||
                EntityRegistry.StruckFreeBy(entry.Type, struck) is not { } freeing) {
                continue;
            }

            foreach (var holder in creature.GetComponents<PlayMakerFSM>()) {
                var fsm = holder.Fsm;
                if (!_ledMoves.TryGetValue(fsm, out var move) || fsm.ActiveState is not { } at ||
                    !move.States.Contains(at) || EntityFsmActions.FindTransition(fsm, at, freeing) == null) {
                    continue;
                }

                SharedDice.RollByName(creature, OwnCreatures.RollName(creature));
                var word = OwnCreatureUpdate(
                    holder,
                    new OwnStep(
                        at.Name, SharedDice.RollCounts(creature), creature.transform.position,
                        creature.transform.localScale.x
                    )
                );
                word.PartCount = OwnStruckFreePart;
                word.Part = move.Leader;
                word.Names = [freeing];
                Logger.Info(
                    $"[Own creatures] '{word.ObjectPath}' holds the player {move.Leader} in '{at.Name}' of " +
                    $"'{fsm.Name}', and the local player struck it by '{struck}', which lets them go by '{freeing}', " +
                    "as their game is told"
                );
                SendToRoomMembers(word);
            }
        }
    }

    /// <summary>
    /// Takes the word of another game that its player struck a creature that every game runs by itself free of the
    /// player it holds (see <see cref="TellStruckFree"/>): in the game of that player, the state machine that holds
    /// them takes the event that lets them go, if it still holds them where that leads anywhere, and goes on from
    /// there as it leads it, a game that follows the scene host's telling every step of it (see CoopSave.OwnLedMoves).
    /// </summary>
    private void TakeStruckFree(ClientPlayerData player, GameObject creature, CoopSaveUpdate update) {
        var freeing = update.Names[0];
        var told = $"'{update.ObjectPath}' held the player {update.Part} in '{update.StateName}' of " +
                   $"'{update.FsmName}' in {player.Username}'s game, whose player struck it free of them";
        if (ThrownThings.FigureOf?.Invoke(update.Part, player.Id) is not { } held ||
            HeroController.SilentInstance is not { } hero || held != hero.gameObject) {
            Logger.Info($"[Own creatures] {told}, which their game lets go of");
            return;
        }

        var fsm = PlayMakerFSM.FindFsmOnGameObject(creature, update.FsmName).Fsm;
        if (!LeadsMove(fsm) && (IsSceneHost?.Invoke() != true || _ledMoves.ContainsKey(fsm)) ||
            fsm.ActiveState is not { } at || EntityFsmActions.FindTransition(fsm, at, freeing) == null) {
            Logger.Info(
                $"[Own creatures] {told}, and no longer holds the local player here, in '{fsm.ActiveStateName}'"
            );
            return;
        }

        Logger.Info($"[Own creatures] {told}, and lets the local player go here, from '{at.Name}' by '{freeing}'");
        fsm.Event(freeing);
    }
}
