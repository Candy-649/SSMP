using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity;
using SSMP.Game.Client.Entity.Action;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

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
///
/// The striker's game, behind the game of the player held, tells the strike wherever it has the creature hold them,
/// and that game lets them go at the first state of the hold that the event leads anywhere from, or not at all where
/// the creature let go of them by itself meanwhile (see <see cref="UpdateFreesOwed"/>; review of 5284dca: the
/// striker's game, still in the start of the grab, where the event leads nowhere, told nothing of a strike that the
/// game of the player held, past it, would have taken).
///
/// The scene host's game has the game of another player let them go so too where it takes no grab of theirs, as it
/// plays a grab of its own player's already, or as its creature went on from where the grab came from (see
/// <see cref="TellLetGo"/>; review of 5284dca: two players touched the tendril within the time a word takes, and the
/// second played the whole grab by itself, the chewing too, while every other game showed the first held). Only by
/// the event that a strike frees them by: the other grabbers, which no strike frees the player held of, play such a
/// grab to its end in that game, as before, as the gloom fly, which dies as the player it holds is hurt.
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
    /// What an update of a creature that every game runs by itself is when the scene host's game tells the game of the
    /// player that it grabbed there that it takes no grab of theirs (see <see cref="TellLetGo"/>): the state that it
    /// grabbed them from there, the player in <see cref="CoopSaveUpdate.Part"/>, and the event that lets them go in
    /// <see cref="CoopSaveUpdate.Names"/>.
    /// </summary>
    private const ushort OwnLetGoPart = 20;

    /// <summary>
    /// The state machines of creatures that every game runs by itself that hold this game's player here and are to let
    /// them go, as another game told, at the first state of the hold that the event leads anywhere from (see
    /// <see cref="TakeStruckFree"/>): the event, and what was told, for the log.
    /// </summary>
    private readonly Dictionary<Fsm, (string Freeing, string Told)> _freesOwed = new();

    /// <summary>
    /// Tells the game of the player whom a creature that every game runs by itself holds here, in a move that their
    /// game leads (see CoopSave.OwnLedMoves), that this game's player struck it free of them, as a state machine of it
    /// that each game runs by itself goes into a state that tells the creature that a part of it was struck, by an
    /// event that frees the player it holds (see EntityRegistry.StruckFreeBy): the strike of this game's player, which
    /// only this game felt. In whichever state of the move it is here, which the event may lead nowhere from: their
    /// game, ahead of this one, looks at its own (see <see cref="TakeStruckFree"/>).
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
                    !move.States.Contains(at)) {
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
    /// Tells the game of another player that a creature that every game runs by itself grabbed there, whose grab the
    /// scene host's game takes no part in (see CoopSave.OwnCatches), to let them go, if a strike would free them, by
    /// the event that it frees them by (see EntityRegistry.FreeingOf): their game, which plays the grab by itself,
    /// leads the way out of it too, which no other game takes, and goes on from the scene host's latest step after it
    /// (see CoopSave.OwnLedMoves).
    /// </summary>
    private void TellLetGo(ClientPlayerData player, Fsm fsm, CoopSaveUpdate catchWord) {
        var machine = fsm.FsmComponent;
        var creature = machine.gameObject;
        if (!EntityRegistry.TryGetEntry(creature, out var entry) ||
            EntityRegistry.FreeingOf(entry.Type) is not { } freeing) {
            return;
        }

        SharedDice.RollByName(creature, OwnCreatures.RollName(creature));
        var word = OwnCreatureUpdate(
            machine,
            new OwnStep(
                catchWord.StateName, SharedDice.RollCounts(creature), creature.transform.position,
                creature.transform.localScale.x
            )
        );
        word.PartCount = OwnLetGoPart;
        word.Part = player.Id;
        word.Names = [freeing];
        Logger.Info(
            $"[Own creatures] '{word.ObjectPath}' grabbed {player.Username} from '{catchWord.StateName}' of " +
            $"'{fsm.Name}' in their game, which lets them go by '{freeing}', as it is told"
        );
        SendToRoomMembers(word);
    }

    /// <summary>
    /// Takes the word of another game that its player struck a creature that every game runs by itself free of the
    /// player it holds (see <see cref="TellStruckFree"/>), or the word of the scene host's game that it takes no grab
    /// of that player's (see <see cref="TellLetGo"/>): in the game of that player, the state machine that holds them
    /// takes the event that lets them go, now or at the first state of the hold that it leads anywhere from (see
    /// <see cref="UpdateFreesOwed"/>), and goes on from there as it leads it, a game that follows the scene host's
    /// telling every step of it (see CoopSave.OwnLedMoves).
    /// </summary>
    private void TakeStruckFree(ClientPlayerData player, GameObject creature, CoopSaveUpdate update) {
        var freeing = update.Names[0];
        var told = update.PartCount == OwnLetGoPart
            ? $"'{update.ObjectPath}' grabbed the player {update.Part} from '{update.StateName}' of " +
              $"'{update.FsmName}' in their game, and {player.Username}'s game, the scene host's, took no grab"
            : $"'{update.ObjectPath}' held the player {update.Part} in '{update.StateName}' of " +
              $"'{update.FsmName}' in {player.Username}'s game, whose player struck it free of them";
        if (ThrownThings.FigureOf?.Invoke(update.Part, player.Id) is not { } held ||
            HeroController.SilentInstance is not { } hero || held != hero.gameObject) {
            Logger.Info($"[Own creatures] {told}, which their game lets go of");
            return;
        }

        var fsm = PlayMakerFSM.FindFsmOnGameObject(creature, update.FsmName).Fsm;
        if (!HoldsLocalPlayer(fsm)) {
            Logger.Info(
                $"[Own creatures] {told}, and no longer holds the local player here, in '{fsm.ActiveStateName}'"
            );
            return;
        }

        _freesOwed[fsm] = (freeing, told);
        if (!LetGoOwed(fsm)) {
            Logger.Info(
                $"[Own creatures] {told}, and is in '{fsm.ActiveStateName}' here, which '{freeing}' leads nowhere " +
                "from: it lets the local player go at the first state of the hold that it leads anywhere from"
            );
        }
    }

    /// <summary>
    /// Whether a state machine of a creature that every game runs by itself holds this game's player here: in a move
    /// that acts on them, which this game leads (see CoopSave.OwnLedMoves), or, in the scene host's game, in a catch
    /// that is not of another player's, whose game would lead it.
    /// </summary>
    private bool HoldsLocalPlayer(Fsm fsm) {
        return LeadsMove(fsm) || IsSceneHost?.Invoke() == true && !_ledMoves.ContainsKey(fsm) &&
               fsm.ActiveState is { } at && CatchStateOf(fsm, at) != null;
    }

    /// <summary>
    /// Has each state machine of a creature that every game runs by itself that is to let go of this game's player, as
    /// another game told (see <see cref="TakeStruckFree"/>), take the event that lets them go as soon as it is in a
    /// state that the event leads anywhere from: in the start of the grab, before the maw has them, it leads nowhere.
    /// </summary>
    private void UpdateFreesOwed() {
        if (_freesOwed.Count == 0) {
            return;
        }

        foreach (var fsm in _freesOwed.Keys.ToList()) {
            LetGoOwed(fsm);
        }
    }

    /// <summary>
    /// Has a state machine of a creature that every game runs by itself that is to let go of this game's player, as
    /// another game told (see <see cref="TakeStruckFree"/>), take the event that lets them go, if it is in a state that
    /// the event leads anywhere from. Whether it took it.
    /// </summary>
    private bool LetGoOwed(Fsm fsm) {
        var (freeing, told) = _freesOwed[fsm];
        if (fsm.ActiveState is not { } at || EntityFsmActions.FindTransition(fsm, at, freeing) == null) {
            return false;
        }

        _freesOwed.Remove(fsm);
        Logger.Info($"[Own creatures] {told}, and lets the local player go here, from '{at.Name}' by '{freeing}'");
        fsm.Event(freeing);
        return true;
    }

    /// <summary>
    /// Forgets that a state machine of a creature that every game runs by itself is to let go of this game's player
    /// (see <see cref="TakeStruckFree"/>), as it goes out of the hold, to a state that no catch leads into, having let
    /// go of them by itself meanwhile: a strike that came as the maw threw the player away after its chewing would have
    /// let them go of at once in its next grab of them.
    /// </summary>
    private void ForgetFreeOwed(Fsm fsm, FsmState toState) {
        if (_freesOwed.Count == 0 || CatchStateOf(fsm, toState) != null || !_freesOwed.Remove(fsm, out var owed)) {
            return;
        }

        Logger.Info(
            $"[Own creatures] {owed.Told}, and goes out of the hold here meanwhile, to '{toState.Name}' of " +
            $"'{fsm.Name}', having let go of the local player"
        );
    }
}
