using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// What a creature that every game runs by itself finds of what a player has on (CheckIfToolEquipped) in a move that a
/// thing of that player's sets off: the barnacles of the aqueduct and the shaman of the swamp swallow a bomb, and look
/// at whether the player has the pouch of poison on, by which the barnacles show the swallow and the burst poisoned or
/// not. Each game has what its own player has on, and the swallow of a bomb of another player's showed by whether that
/// game's player had the pouch on (USER 11-26 "改吧").
///
/// The game of the player whose thing it is, where a state machine that each game runs by itself for its own player
/// tells the creature of it (see CoopSave.OwnTells), finds what the checks of the move find there, and tells every
/// other game with the word of the step into the move: the tell of a game that follows the scene host's, or the scene
/// host's step. In every other game, those checks find the same through the move (see <see cref="OnToolCheck"/>). The
/// shaman, which chooses its way by the pouch, goes on as the game of that player leads besides (see
/// LeadsMoveByWhatPlayerHas).
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What the checks of what the player has on in a move of a state machine of a creature that every game runs by
    /// itself found in the game of the player whose thing set it off, by the state machine: the states of the move,
    /// and what each check in them found (see <see cref="NoteToolChecks"/>).
    /// </summary>
    private readonly Dictionary<Fsm, (HashSet<FsmState> Move, Dictionary<CheckIfToolEquipped, bool> Found)>
        _toolChecks = new();

    /// <summary>
    /// The words that tell what the checks of what the player has on in the move from a state find in this game, one
    /// for each check: the place of the check among the actions of its state, what it finds, and the name of the state.
    /// </summary>
    private static List<string> ToolChecksOf(Fsm fsm, FsmState first) {
        var words = new List<string>();
        foreach (var state in MoveOf(fsm, first)) {
            for (var i = 0; i < state.Actions.Length; i++) {
                if (state.Actions[i] is CheckIfToolEquipped { Enabled: true } check) {
                    words.Add($"{i}:{(check.IsTrue ? 1 : 0)}:{state.Name}");
                }
            }
        }

        return words;
    }

    /// <summary>
    /// Notes what the checks of what the player has on in the move from a state found in the game of the player whose
    /// thing set off the move there, from the words of a step (see <see cref="ToolChecksOf"/>) from the given one on,
    /// for the checks of this game's state machine to find the same through the move.
    /// </summary>
    private void NoteToolChecks(GameObject creature, CoopSaveUpdate update, string first, int from) {
        if (update.Names.Count <= from) {
            return;
        }

        var fsm = PlayMakerFSM.FindFsmOnGameObject(creature, update.FsmName).Fsm;
        var found = new Dictionary<CheckIfToolEquipped, bool>();
        for (var i = from; i < update.Names.Count; i++) {
            var word = update.Names[i].Split([':'], 3);
            found[(CheckIfToolEquipped) fsm.GetState(word[2]).Actions[int.Parse(word[0])]] = word[1] == "1";
        }

        _toolChecks[fsm] = (MoveOf(fsm, fsm.GetState(first)), found);
        Logger.Info(
            $"[Own creatures] '{update.ObjectPath}' goes into '{first}' of '{fsm.Name}' by a thing of another " +
            $"player's, and its {found.Count} check(s) of what the player has on find through the move what they " +
            $"found in that player's game: {string.Join(", ", update.Names.GetRange(from, update.Names.Count - from))}"
        );
    }

    /// <summary>
    /// Forgets what the checks of a move of a state machine found in another game (see <see cref="NoteToolChecks"/>)
    /// as it goes out of the move. A move that a thing of this game's player sets off here finds what they have on
    /// (see OnOwnCreatureStep).
    /// </summary>
    private void ForgetToolChecks(Fsm fsm, FsmState toState) {
        if (_toolChecks.TryGetValue(fsm, out var noted) && fsm.ActiveState is { } from && noted.Move.Contains(from) &&
            !noted.Move.Contains(toState)) {
            _toolChecks.Remove(fsm);
        }
    }

    /// <summary>
    /// Hook for a check of whether the player has something on, which in a move that a thing of another player's set
    /// off finds what it found in that player's game (see <see cref="NoteToolChecks"/>).
    /// </summary>
    private bool OnToolCheck(Func<CheckIfToolEquipped, bool> orig, CheckIfToolEquipped self) {
        if (_toolChecks.Count == 0 || self.Fsm is not { } fsm || !_toolChecks.TryGetValue(fsm, out var noted) ||
            !noted.Found.TryGetValue(self, out var found)) {
            return orig(self);
        }

        Logger.Info(
            $"[Own creatures] '{fsm.GameObject.name}' checks in '{self.State.Name}' of '{fsm.Name}' whether the " +
            $"player has the thing on, and finds {(found ? "it on" : "it not on")}, as in the game of the player " +
            "whose thing it is"
        );
        return found;
    }
}
