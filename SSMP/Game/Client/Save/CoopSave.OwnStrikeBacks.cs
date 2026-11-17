using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The strike backs of the creatures that every game runs by itself (see OwnCreatures) at a blow that they blocked,
/// like the knight's at a blow on its guard. The game of the player who struck answers at once, as a game of one does,
/// and the scene host's game follows it (USER 11-17 "客机先照旧反击，房主跟着反击吧", "就让客机说有没有被挡住，有没有
/// 触发弹反").
///
/// In a game that follows the scene host's, the blow of its player that the creature blocks takes it into the strike
/// back there and then, where the block leads it by itself into a move that catches: the creature goes after that
/// player, plays the strike back by itself to its end, as a leap at them (see CoopSave.OwnCatches), and tells the scene
/// host's game (see <see cref="TellOwnStrikeBack"/>). That one goes into the strike back too, after that player, from
/// wherever its creature is, on its guard or a moment past it (see <see cref="TakeOwnStrikeBack"/>), and every other
/// game takes it from there as a step of the scene host's. A strike back at a blow of the scene host's player is the
/// scene host's game's, and comes to the others with its steps.
///
/// The blow itself goes to every other game as blocked (CoopHitUpdate.Blocked) and takes no health there: the scene
/// host's creature blocks it as the game does, and what else it does at a block, like the turn of the knight's bow
/// into the fight, comes to the others with its steps, while they show only the spark and the clink of the block (see
/// CoopHits). Before, each game judged the blow again by what its own creature was doing as it came: one whose creature
/// had let go of its guard in the scene host's game took it as a wound, while the striker's game played a strike back
/// that the scene host's never did.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What an update of a creature that every game runs by itself is when it tells that it struck back at a blow of
    /// the player of the game that sent it (see <see cref="TellOwnStrikeBack"/>).
    /// </summary>
    private const ushort OwnStrikeBackPart = 6;

    /// <summary>
    /// The events by which the game tells a creature that it blocked a blow (HealthManager.Invincible).
    /// </summary>
    private static readonly HashSet<string> BlockEvents = [
        "BLOCKED HIT", "BLOCKED HIT R", "BLOCKED HIT L", "BLOCKED HIT U", "BLOCKED DOWN"
    ];

    /// <summary>
    /// Whether a state machine of a creature goes to a state as a strike back at a blow of this game's player that the
    /// creature blocked, which is being made now: by one of the events of a block, into a move that catches or on its
    /// way into one by itself (see IsLeapState). A block that leads to no such move, like the flash of a guard, is a
    /// step as any other (review: played here by itself as a leap, it held the steps of the scene host's for as long as
    /// it came to no state that goes by players).
    /// </summary>
    private static bool StrikesBackAtLocalBlow(Fsm fsm, FsmState toState) {
        return CoopHits.IsMakingLocalStrike && fsm.LastTransition is { } way && way.ToState == toState.Name &&
               BlockEvents.Contains(way.EventName) && (IsLeapState(toState) || GoesOnInto(toState, IsLeapState));
    }

    /// <summary>
    /// Has a state machine of a creature that every game runs by itself, in a game that follows the scene host's, play
    /// a strike back at a blow of this game's player that it blocked here by itself, as it goes to the first state of
    /// it, and tells the scene host's game (see <see cref="TellOwnStrikeBack"/>). Whether it does.
    /// </summary>
    private bool StartsOwnStrikeBack(PlayMakerFSM machine, FsmState toState) {
        var fsm = machine.Fsm;
        if (!StrikesBackAtLocalBlow(fsm, toState) || HeroController.SilentInstance is not { } hero) {
            return false;
        }

        var creature = machine.gameObject;
        GamePatcher.GoAfterStriker(creature, hero.gameObject);
        TellOwnStrikeBack(machine, toState.Name);
        var strikeBack = new OwnLeap(OwnCreatures.PlaceOf(creature), false, "strike back at the local player");
        strikeBack.Passed.Add(toState.Name);
        _ownLeaps[fsm] = strikeBack;
        Logger.Info(
            $"[Own creatures] '{strikeBack.Place}' blocked a blow of the local player and strikes back in " +
            $"'{toState.Name}' of '{machine.FsmName}', which it plays here by itself to its end"
        );
        return true;
    }

    /// <summary>
    /// Tells the scene host's game that a creature that every game runs by itself struck back at a blow of this game's
    /// player that it blocked: the state it struck back from and the state it went to, with the rolls before it, where
    /// it was and how it went, as a step of the scene host's tells (see <see cref="TakeOwnStrikeBack"/>). The blow
    /// goes right after it, as blocked (CoopHitUpdate.Blocked).
    /// </summary>
    private void TellOwnStrikeBack(PlayMakerFSM machine, string into) {
        var creature = machine.gameObject;
        SharedDice.RollByName(creature, OwnCreatures.RollName(creature));
        var word = OwnCreatureUpdate(
            machine,
            new OwnStep(
                machine.Fsm.ActiveStateName, SharedDice.RollCounts(creature), creature.transform.position,
                creature.transform.localScale.x
            )
        );
        word.PartCount = OwnStrikeBackPart;
        word.Part = ThrownThings.ThrowersPlayer;
        word.Names = [into];
        Logger.Info(
            $"[Own creatures] '{word.ObjectPath}' struck back at a blow of the local player from " +
            $"'{word.StateName}' into '{into}' of '{word.FsmName}', as the scene host's game is told"
        );
        SendToRoomMembers(word);
    }

    /// <summary>
    /// Takes a strike back of a creature that every game runs by itself at a blow of the player of another member's
    /// game (see <see cref="TellOwnStrikeBack"/>). In the scene host's game, the creature goes into it too, after that
    /// player, at the rolls and from the place that theirs struck back at, from wherever it is: on the guard that theirs
    /// struck back from, or past it, as it let go of it in the moment that the word took (USER 11-17 "房主跟着反击吧").
    /// One that struck back here already at those rolls goes on with that, and one that rolled since it was past that
    /// guard is long past it and plays none of it. Every other game takes it from there as a step of the scene host's.
    /// </summary>
    private void TakeOwnStrikeBack(ClientPlayerData player, GameObject creature, CoopSaveUpdate update) {
        var place = update.ObjectPath;
        var into = update.Names[0];
        if (IsSceneHost?.Invoke() != true) {
            Logger.Info(
                $"[Own creatures] '{place}' struck back at a blow of {player.Username} in their game, from " +
                $"'{update.StateName}' into '{into}', which comes here with the scene host's steps"
            );
            return;
        }

        var fsm = PlayMakerFSM.FindFsmOnGameObject(creature, update.FsmName).Fsm;
        var steps = _ownSteps.TryGetValue(fsm, out var kept) ? kept : [];
        if (steps.Any(step => step.State == into && step.Counts.SequenceEqual(update.Amounts))) {
            Logger.Info(
                $"[Own creatures] '{place}' struck back at a blow of {player.Username} in their game, and struck " +
                $"back here already, in '{fsm.ActiveStateName}' of '{fsm.Name}', where it goes on"
            );
            return;
        }

        if (!SharedDice.RollCounts(creature).SequenceEqual(update.Amounts) &&
            !steps.Any(step => step.Counts.SequenceEqual(update.Amounts))) {
            Logger.Info(
                $"[Own creatures] '{place}' struck back at a blow of {player.Username} in their game from " +
                $"'{update.StateName}', and is long past it here, in '{fsm.ActiveStateName}' of '{fsm.Name}', where " +
                "it plays none of it"
            );
            return;
        }

        Logger.Info(
            $"[Own creatures] '{place}' struck back at a blow of {player.Username} in their game from " +
            $"'{update.StateName}', and goes from '{fsm.ActiveStateName}' into '{into}' of '{fsm.Name}' here too"
        );
        PutAtSceneHostsStep(fsm, update);
        if (ThrownThings.FigureOf?.Invoke(update.Part, player.Id) is { } figure && figure != null) {
            GamePatcher.GoAfterStriker(creature, figure);
        }

        fsm.SetState(into);
    }
}
