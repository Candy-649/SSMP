using System.Collections.Generic;
using HutongGames.PlayMaker;
using SSMP.Animation.Effects;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
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
/// The game of the player who extracts plays it as the game does, by itself, and leads it (see CoopSave.OwnLedMoves):
/// in a game that follows the scene host's, it tells the scene host's game its steps, and the scene host's game, which
/// gives way to the blow as it comes, drawn to their character there, takes them (see <see cref="TakeOthersExtract"/>).
/// Every other game takes it into the extraction with the step of the scene host's that the blow set off there, which
/// names the player who struck, drawn to their character there (see <see cref="FollowExtractAsTold"/>), and so does a
/// member who comes in during it. No other game extracts anything for its own player's hero, and nothing of its own
/// takes the creature out of it, like a hurt of its own player or the end of an extraction of theirs (see
/// HoldsInLedMove). A blow that extracts from a creature in an extraction already is the scene host's game's to let
/// go of, as only it knows which came first: two players who extracted from the same one at once each had theirs go
/// on in their own game, and the scene host's, set to the second, waited for the end of it for good (review of
/// 92d7aab). Every game took the blow as the game takes one of its own player's: the creature was drawn to the player
/// of each game and waited there for good, and a hero in the middle of an extraction of its own was set to extract it
/// too. With three or four players, each extraction is the game of its own player's, and every other game shows it at
/// their character.
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
    /// Has a state machine of a creature that every game runs by itself follow an extraction by another player, drawn
    /// to their character here: its variable of the hero holds that character, with the places under it that a
    /// creature is drawn to (see HeroChildEffects.GetFigurePart), until it is out of the extraction (see
    /// CoopSave.OwnLedMoves).
    /// </summary>
    private void FollowExtract(Fsm fsm, HashSet<FsmState> states, GameObject figure, ushort leader, ulong number) {
        foreach (var path in ExtractPoints) {
            HeroChildEffects.GetFigurePart(figure, path);
        }

        var hero = fsm.Variables.FindFsmGameObject("Hero");
        if (hero != null) {
            hero.Value = figure;
        }

        FollowLedMove(fsm, states, hero, leader, number);
    }

    /// <summary>
    /// Has a creature that every game runs by itself, in the scene host's game, give way to a blow of another player
    /// that extracts from it (see CoopHits.TakeAsExtract), drawn to their character here, as the game does but for
    /// this game's hero, which extracts nothing: their game leads the extraction, whose steps the creature here takes,
    /// those that came before the blow as well (see CoopSave.OwnLedMoves). One in an extraction already, of this game's
    /// player or of another, goes on with that, and the blow goes nowhere. Every other game takes the extraction from
    /// the step that the blow sets off here (see <see cref="FollowExtractAsTold"/>): the blow there, which comes there
    /// at another moment, may come after it, or never, for a blow of this game's player (review of 92d7aab: their
    /// creature, drawn to their own player there, stayed there).
    /// </summary>
    private void TakeOthersExtract(HealthManager health, GameObject figure, ushort striker, uint strike) {
        var place = OwnCreatures.PlaceOf(health.gameObject);
        if (IsSceneHost?.Invoke() != true) {
            Logger.Info(
                $"[Own creatures] '{place}' takes extracting blow {strike} of the player {striker}, which it gives " +
                "way to with the scene host's step"
            );
            return;
        }

        var machines = new List<(PlayMakerFSM Machine, HashSet<FsmState> States)>();
        var extracted = false;
        foreach (var machine in health.GetComponents<PlayMakerFSM>()) {
            if (ExtractStates(machine.Fsm) is { } states) {
                machines.Add((machine, states));
                extracted |= machine.Fsm.ActiveState is { } at && states.Contains(at);
            }
        }

        foreach (var (machine, states) in machines) {
            var fsm = machine.Fsm;
            if (extracted) {
                Logger.Info(
                    $"[Own creatures] '{place}' takes extracting blow {strike} of the player {striker} in " +
                    $"'{fsm.ActiveStateName}' of '{fsm.Name}', in an extraction already, which goes on"
                );
            } else {
                FollowExtract(fsm, states, figure, striker, strike);
                Logger.Info(
                    $"[Own creatures] '{place}' gives way to extracting blow {strike} of the player {striker} in " +
                    $"'{fsm.ActiveStateName}' of '{fsm.Name}', drawn to their character here, as their game leads"
                );
                machine.SendEvent(ExtractEvent);
            }

            MeetLedMove(fsm, striker, strike);
        }
    }

    /// <summary>
    /// Has a state machine of a creature that every game runs by itself, in a game that follows the scene host's,
    /// follow an extraction of another player's that a step of the scene host's takes it into, before it does (see
    /// <see cref="FollowExtract"/>): one that a blow set off there, of the scene host's player or of another, which
    /// names the player who struck, or the state that it is in there, for a member who has just come in, which names
    /// the player who extracts (see TellOwnState). An extraction of this game's player's is its own, and one that it
    /// follows already goes on.
    /// </summary>
    private void FollowExtractAsTold(ClientPlayerData sceneHost, Fsm fsm, CoopSaveUpdate update) {
        var leader = update.PartCount switch {
            OwnStruckPart or OwnToldPart => (ushort) update.Key,
            OwnHostStruckPart => ThrownThings.ThrowersPlayer,
            _ => ThrownThings.NoPlayer
        };
        if (leader == ThrownThings.NoPlayer || _ledMoves.ContainsKey(fsm) || ExtractStates(fsm) is not { } states ||
            fsm.GetState(update.StateName) is not { } into || !states.Contains(into) ||
            ThrownThings.FigureOf?.Invoke(leader, sceneHost.Id) is not { } figure || figure == null ||
            HeroController.SilentInstance is { } own && figure == own.gameObject) {
            return;
        }

        var player = leader == ThrownThings.ThrowersPlayer ? sceneHost.Id : leader;
        FollowExtract(fsm, states, figure, player, update.PartCount == OwnStruckPart ? update.Sequence : 0);
        Logger.Info(
            $"[Own creatures] '{update.ObjectPath}' goes into '{update.StateName}' of '{fsm.Name}' in an extraction " +
            $"by the player {player}, drawn to their character here, as the scene host's game tells"
        );
    }

    /// <summary>
    /// Has a state machine of a creature that every game runs by itself, in a game that follows the scene host's, play
    /// an extraction of this game's player by itself to its end, as it gives way to their blow, telling the scene
    /// host's game its steps with the number of the blow (see CoopSave.OwnLedMoves): the steps of the scene host's that
    /// come meanwhile wait (see CoopSave.OwnCatches). Whether it does.
    /// </summary>
    private bool StartsOwnExtract(PlayMakerFSM machine, FsmState toState) {
        var fsm = machine.Fsm;
        if (!CoopHits.IsMakingLocalStrike || fsm.LastTransition?.EventName != ExtractEvent ||
            ExtractStates(fsm) is not { } states || !states.Contains(toState)) {
            return false;
        }

        var leap = new OwnLeap(OwnCreatures.PlaceOf(machine.gameObject), false, "extraction by the local player") {
            Catch = states,
            Led = true,
            Move = CoopHits.LocalStrikeNumber
        };
        _ownLeaps[fsm] = leap;
        Logger.Info(
            $"[Own creatures] '{leap.Place}' gives way to extracting blow {leap.Move} of the local player into " +
            $"'{toState.Name}' of '{machine.FsmName}', which it plays here by itself to its end"
        );
        return true;
    }
}
