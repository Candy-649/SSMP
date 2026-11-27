using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The creatures that fight the room's creatures beside the players, as the allies of some fights do, which every
/// game runs by itself (see EntityRegistryEntry.FightsCreatures). Which creature one goes for is the scene host's
/// game's pick, and so is what it does by where that creature is, as what a creature does by where a player is (USER
/// 11-27 "这么定行", of the pick following the scene host's as the look at a player does). Each game's would pick the
/// creature nearest it there, and choose its charge, jump or slash by how far that one is: a creature a little
/// elsewhere in each game, or one that waits for a step of the scene host's there, would have it pick another and
/// choose another move (USER "两边的NPC会因为怪的位置有微小的不同而用不一样的招吗"). The state that picks it goes by the
/// players (see GoesByPlayers), and every step tells the creature that it fights there (see ToldValues.FoughtCreature):
/// the games that follow wait there for the scene host's step and fight that one. What it does by where that creature
/// is already went so, as the actions of the kinds that look at a player look at anything else than the creature
/// itself (see LooksAtPlayers). Whether that creature is still there is each game's own look, which needs no wait in
/// a move: its death goes to every game, as any death does, and the next pick is the scene host's again. Its blows
/// land in each game on that game's creatures.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The kind of action by which a creature that fights the room's creatures picks the one it goes for: the nearest
    /// of those in its range.
    /// </summary>
    private const string FightPickActionType = "GetTrackTriggerClosestObject";

    /// <summary>
    /// The variables in which each state machine of a creature that fights the room's creatures keeps the creature
    /// that it fights, found the first time they are asked for (see <see cref="FoughtBy"/>).
    /// </summary>
    private static readonly ConditionalWeakTable<Fsm, HashSet<string>> Fought = new();

    /// <summary>
    /// The variables in which a state machine keeps the creature that it fights, those in which its picks keep what
    /// they found, like the 'Target' of one ally and the 'Target Enemy' of another, for one of a creature that fights
    /// the room's creatures; none for any other.
    /// </summary>
    private static HashSet<string> FoughtBy(Fsm fsm) {
        if (Fought.TryGetValue(fsm, out var names)) {
            return names;
        }

        names = new HashSet<string>(StringComparer.Ordinal);
        if (fsm.GameObject is { } owner && OwnCreatures.FightsCreatures(owner)) {
            foreach (var state in fsm.States) {
                foreach (var action in state.Actions) {
                    if (IsFightPick(action)) {
                        names.UnionWith(ToldValues.ObjectsStoredBy(action));
                    }
                }
            }
        }

        Fought.Add(fsm, names);
        return names;
    }

    /// <summary>
    /// Whether an action is a pick of the creature to fight (see <see cref="FightPickActionType"/>).
    /// </summary>
    private static bool IsFightPick(FsmStateAction action) {
        return action.GetType().Name == FightPickActionType;
    }

    /// <summary>
    /// Whether an action of a state machine that keeps the creature it fights in the given variables (see
    /// <see cref="FoughtBy"/>) picks that creature.
    /// </summary>
    private static bool PicksFought(FsmStateAction action, HashSet<string> fought) {
        return fought.Count > 0 && IsFightPick(action);
    }

    /// <summary>
    /// The words of other games for each creature that fights the room's creatures that came out here at one of them
    /// (see <see cref="ComesOutAsThere"/>), in the order they came, taken once its state machines run.
    /// </summary>
    private readonly Dictionary<GameObject, List<(ClientPlayerData Player, CoopSaveUpdate Word)>> _wordsAsItComesOut =
        new();

    /// <summary>
    /// The creatures that fight the room's creatures that came out here at a word of another game (see
    /// <see cref="ComesOutAsThere"/>), each once in a stay in the room: one that the room switches off again stays so.
    /// </summary>
    private readonly HashSet<GameObject> _broughtOut = [];

    /// <summary>
    /// Switches on a creature that fights the room's creatures, which is out in the game whose word came, where it is
    /// off while what it stands in is on, once in a stay in the room, and tells whether it did or waits so. What has it
    /// come out reads each player's own save or waits for each player's own touch: whether the player asked for its
    /// aid, where they have been, whether they walked into the part of the room where it waits. One game had it fight
    /// beside the players and another had it nowhere, and the copy of it that the scene host's game ran had come out in
    /// every game. The word, and those that come after it, are taken once its state machines run (see
    /// <see cref="UpdateComingOut"/>): what the word of a member who has just come in tells it does there would be lost
    /// otherwise, and a creature at the end of its fight there sends no more of them (review of c3c6288: it stood
    /// unseen at its start).
    /// </summary>
    private bool ComesOutAsThere(ClientPlayerData player, CoopSaveUpdate update, GameObject creature) {
        if (_wordsAsItComesOut.TryGetValue(creature, out var words)) {
            words.Add((player, update));
            return true;
        }

        if (creature.activeSelf || !OwnCreatures.FightsCreatures(creature) ||
            creature.transform.parent is { } stand && !stand.gameObject.activeInHierarchy ||
            !_broughtOut.Add(creature)) {
            return false;
        }

        Logger.Info(
            $"[Own creatures] '{update.ObjectPath}' is out in {player.Username}'s game, in '{update.StateName}', and " +
            "comes out here too, to go on as there once it runs"
        );
        creature.SetActive(true);
        _wordsAsItComesOut[creature] = [(player, update)];
        return true;
    }

    /// <summary>
    /// Takes the words that came for a creature that came out here at them (see <see cref="ComesOutAsThere"/>) once its
    /// state machines run, or lets go of them if the room switched it off again.
    /// </summary>
    private void UpdateComingOut() {
        if (_wordsAsItComesOut.Count == 0) {
            return;
        }

        foreach (var pair in _wordsAsItComesOut.ToList()) {
            var creature = pair.Key;
            if (creature != null && creature.activeInHierarchy &&
                creature.GetComponents<PlayMakerFSM>().Any(machine => machine.enabled && !machine.Fsm.Started)) {
                continue;
            }

            _wordsAsItComesOut.Remove(creature!);
            if (creature == null || !creature.activeInHierarchy) {
                Logger.Info("[Own creatures] A creature that came out here at another game's word is gone again");
                continue;
            }

            foreach (var (player, word) in pair.Value) {
                OnOwnCreature(player, word);
            }
        }
    }
}
