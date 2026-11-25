using System.Collections.Generic;
using HutongGames.PlayMaker;
using SSMP.Animation.Effects;
using SSMP.Game.Client.Entity;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The extractions of the creatures that every game runs by itself that a player extracts from, like the moss
/// creatures and the worms of blue blood: the creature gives way to the blow, is drawn to the hero of the player who
/// struck it and waits there until that hero is done, at which it dies or is hit, or, for some, until that hero is
/// hurt, at which it lets go (USER 11-25 "你先把能本地化的都本地化了吧").
///
/// The game of the player who extracts plays it as the game does, by itself. In a game that follows the scene host's,
/// that is a move of its own to its end, like a leap at this game's player (see CoopSave.OwnCatches), and what ends it
/// is told to the scene host's game (see CoopSave.OwnTells), whose step every other game takes. Every other game
/// gives way to that player's blow as it comes, drawn to their character there, and extracts nothing for its own
/// player's hero (see <see cref="TakeOthersExtract"/>), and nothing of its own takes the creature out of it, like a
/// hurt of its own player or the end of an extraction of theirs, until the word of the game that extracts does (see
/// <see cref="HoldsInOthersExtract"/>). Every game took the blow as the game takes one of its own player's: the
/// creature was drawn to the player of each game and waited there for good, and a hero in the middle of an extraction
/// of its own was set to extract it too. With three or four players, each extraction is the game of its own player's,
/// and every other game shows it at their character.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The event of the game by which a creature gives way to a blow that extracts from it (HealthManager.TakeDamage),
    /// which a global transition of its state machine takes it on from.
    /// </summary>
    private const string ExtractEvent = "EXTRACT";

    /// <summary>
    /// What the hero of the player who extracts tells the creature: that it is done, at which the creature dies or is
    /// hit, and that it was hurt, at which some let go.
    /// </summary>
    private static readonly HashSet<string> ExtractEnds = ["EXTRACT FINISH", "HERO DAMAGED"];

    /// <summary>
    /// The places under the hero that an extracted creature is drawn to.
    /// </summary>
    private static readonly string[] ExtractPoints = ["Tool Effects/Extract Point", "Tool Effects/Extract Point Enemy"];

    /// <summary>
    /// The states of the extraction of each state machine looked at, or null for one that gives way to no extracting
    /// (see <see cref="ExtractStates"/>).
    /// </summary>
    private readonly Dictionary<Fsm, HashSet<FsmState>?> _extractStates = new();

    /// <summary>
    /// The state machines of creatures that every game runs by itself in an extraction of another player's here, with
    /// its states and the variable of the hero, which holds that player's character meanwhile (see
    /// <see cref="TakeOthersExtract"/>).
    /// </summary>
    private readonly Dictionary<Fsm, (HashSet<FsmState> States, FsmGameObject? Hero)> _othersExtracts = new();

    /// <summary>
    /// Whether the scene host's game takes the tell of another game now (see TakeOwnTell), which takes a creature out
    /// of an extraction of another player's (see <see cref="HoldsInOthersExtract"/>).
    /// </summary>
    private bool _takingTell;

    /// <summary>
    /// The states of the extraction of a state machine: the one that it gives way to extracting in, and those that it
    /// goes on to by itself up to the one where it waits for the hero of the player who extracts (see
    /// <see cref="ExtractEnds"/>), or null for one that gives way to no extracting.
    /// </summary>
    private HashSet<FsmState>? ExtractStates(Fsm fsm) {
        if (_extractStates.TryGetValue(fsm, out var states)) {
            return states;
        }

        foreach (var global in fsm.GlobalTransitions) {
            if (global.EventName != ExtractEvent || fsm.GetState(global.ToState) is not { } start) {
                continue;
            }

            states = [start];
            var next = new Stack<FsmState>(states);
            while (next.Count > 0) {
                foreach (var way in next.Pop().Transitions) {
                    if (!ExtractEnds.Contains(way.EventName) && fsm.GetState(way.ToState) is { } state &&
                        states.Add(state)) {
                        next.Push(state);
                    }
                }
            }
        }

        _extractStates[fsm] = states;
        return states;
    }

    /// <summary>
    /// Has a creature that every game runs by itself give way to a blow of another player that extracts from it (see
    /// CoopHits.TakeAsExtract), drawn to their character here: each of its state machines that gives way to extracting
    /// has its variable of the hero hold that character, with the places under it that a creature is drawn to (see
    /// HeroChildEffects.GetFigurePart), until it is out of the extraction. The scene host's game tells it of the blow
    /// as the game does, but for this game's hero, which extracts nothing, and every other game takes it into the
    /// extraction with the step of the scene host's that comes after the blow: steps of that game from before the blow
    /// still to come would take it out again, and its own way in may be held for one of the scene host's anyway.
    /// </summary>
    private void TakeOthersExtract(HealthManager health, GameObject figure, ushort striker) {
        foreach (var path in ExtractPoints) {
            HeroChildEffects.GetFigurePart(figure, path);
        }

        var sceneHost = IsSceneHost?.Invoke() == true;
        foreach (var machine in health.GetComponents<PlayMakerFSM>()) {
            var fsm = machine.Fsm;
            if (ExtractStates(fsm) is not { } states) {
                continue;
            }

            var hero = fsm.Variables.FindFsmGameObject("Hero");
            if (hero != null) {
                hero.Value = figure;
            }

            _othersExtracts[fsm] = (states, hero);
            Logger.Info(
                $"[Own creatures] '{OwnCreatures.PlaceOf(health.gameObject)}' gives way to the extracting blow of " +
                $"the player {striker} in '{fsm.ActiveStateName}' of '{fsm.Name}', drawn to their character here" +
                (sceneHost ? "" : " with the scene host's step")
            );
            if (sceneHost) {
                machine.SendEvent(ExtractEvent);
            }
        }
    }

    /// <summary>
    /// Whether a state machine in an extraction of another player's here (see <see cref="TakeOthersExtract"/>) is held
    /// in it at a switch of its own out of it, like at a hurt of this game's player, at which some let go, or at the
    /// end of an extraction of theirs, which every creature in one hears. Only the word of the game that extracts takes
    /// it out, as a step of the scene host's, a tell that the scene host's game takes (see CoopSave.OwnTells) or its
    /// death, which the game tells its state machines of (HealthManager.Die), and its hero is this game's player again.
    /// </summary>
    private bool HoldsInOthersExtract(Fsm fsm, FsmState toState) {
        if (!_othersExtracts.TryGetValue(fsm, out var extract) || fsm.ActiveState is not { } at ||
            !extract.States.Contains(at) || extract.States.Contains(toState)) {
            return false;
        }

        var place = OwnCreatures.PlaceOf(fsm.GameObject);
        if (OwnCreatures.PlaysSceneHostsStep(fsm) || _takingTell || _dyingAsTold) {
            _othersExtracts.Remove(fsm);
            if (extract.Hero != null) {
                extract.Hero.Value = HeroController.instance.gameObject;
            }

            Logger.Info(
                $"[Own creatures] '{place}' goes out of the extraction of another player's from '{at.Name}' to " +
                $"'{toState.Name}' of '{fsm.Name}', as the game that extracts tells"
            );
            return false;
        }

        Logger.Info(
            $"[Own creatures] '{place}' would go from '{at.Name}' to '{toState.Name}' of '{fsm.Name}' here, and " +
            "stays in the extraction of another player's until the game that extracts tells its end"
        );
        return true;
    }

    /// <summary>
    /// Has a state machine of a creature that every game runs by itself, in a game that follows the scene host's, play
    /// an extraction of this game's player by itself to its end, as it gives way to their blow: the steps of the scene
    /// host's that come meanwhile wait (see CoopSave.OwnCatches). Whether it does.
    /// </summary>
    private bool StartsOwnExtract(PlayMakerFSM machine, FsmState toState) {
        var fsm = machine.Fsm;
        if (!CoopHits.IsMakingLocalStrike || fsm.LastTransition?.EventName != ExtractEvent ||
            ExtractStates(fsm) is not { } states || !states.Contains(toState)) {
            return false;
        }

        var leap = new OwnLeap(OwnCreatures.PlaceOf(machine.gameObject), false, "extraction by the local player") {
            Catch = states,
            Extract = true
        };
        _ownLeaps[fsm] = leap;
        Logger.Info(
            $"[Own creatures] '{leap.Place}' gives way to the extracting blow of the local player into " +
            $"'{toState.Name}' of '{machine.FsmName}', which it plays here by itself to its end"
        );
        return true;
    }

    /// <summary>
    /// Whether a state machine in an extraction of this game's player (see <see cref="StartsOwnExtract"/>) goes out of
    /// it at what their hero tells it, done or hurt, which the scene host's game is told of as a tell from outside it
    /// (see CoopSave.OwnTells): the hurt comes from the game itself, which no tell from outside does.
    /// </summary>
    private bool EndsOwnExtract(Fsm fsm, FsmState toState) {
        return _ownLeaps.TryGetValue(fsm, out var leap) && leap is { Extract: true, Through: false } &&
               leap.Catch!.Contains(fsm.ActiveState) && !leap.Catch.Contains(toState) &&
               fsm.LastTransition is { } way && way.ToState == toState.Name && ExtractEnds.Contains(way.EventName);
    }
}
