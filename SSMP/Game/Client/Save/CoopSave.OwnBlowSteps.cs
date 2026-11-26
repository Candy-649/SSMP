using System.Collections.Generic;
using System.Linq;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The steps of the scene host's at a blow of another player on a thing of a creature that every game runs by itself
/// that takes blows in its own state machine rather than by health, like the crust walls of the coral creature that
/// hides in its crust and the burning parts of the effigy (see HitMachines.CountsHits): the blow comes here as well,
/// as a replay (see CoopHits), which takes the thing where the blow took it there, with what the blow dealt, and the
/// step waits for it, to be taken where the replay leaves the thing waiting for the scene host's way on. Taken as it
/// came, before the replay, the step ran the thing through the states that count the blow, and the replay, made once
/// the thing waited for blows again, counted it once more; a crust wall, which the replay took into the state that
/// looks at where the players are, then waited there for a step that never came, and took no blow of this game's
/// player until the scene host's next (found 11-26). Every later step of the state machine waits behind it, as they
/// came, and they are taken as the replay is made here, but for the next one at a blow, which waits for its own; one
/// whose replay does not come within <see cref="MoveThroughWait"/> is taken then, as the blow here.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The steps of the scene host's of each state machine that wait for the replay of a blow here, the first at that
    /// blow, as they came (see <see cref="WaitsForReplayedBlow"/>).
    /// </summary>
    private readonly Dictionary<Fsm, List<(ClientPlayerData Player, CoopSaveUpdate Step)>> _stepsForBlows = new();

    /// <summary>
    /// The state machine whose steps that waited for the replay of a blow this game takes now, which wait no more.
    /// </summary>
    private Fsm? _takingStepsForBlow;

    /// <summary>
    /// Whether a step of the scene host's of a state machine waits for the replay of a blow here (see
    /// CoopSave.OwnBlowSteps): one at a blow of another player than this game's, on a thing that takes blows by itself
    /// where it waits for them now or comes back to that by itself, and every one after it. Not one that the thing
    /// took here already, as the replay came first, nor one that the replay left it waiting for.
    /// </summary>
    private bool WaitsForReplayedBlow(ClientPlayerData player, Fsm fsm, CoopSaveUpdate update) {
        if (_takingStepsForBlow == fsm) {
            return false;
        }

        if (_stepsForBlows.TryGetValue(fsm, out var waiting)) {
            waiting.Add((player, update));
            Logger.Info(
                $"[Own creatures] '{update.ObjectPath}' went to '{update.StateName}' of '{fsm.Name}' in " +
                $"{player.Username}'s game, which waits behind {waiting.Count - 1} step(s) of that game from a blow " +
                "whose replay has not come here yet"
            );
            return true;
        }

        if (!IsAtOthersBlow(player, update) || _heldOwnSteps.ContainsKey(fsm) || HasOwnStep(fsm, update) ||
            !TakesBlowsItself(fsm)) {
            return false;
        }

        _stepsForBlows[fsm] = [(player, update)];
        Logger.Info(
            $"[Own creatures] '{update.ObjectPath}' went to '{update.StateName}' of '{fsm.Name}' in " +
            $"{player.Username}'s game at a blow there, and waits in '{fsm.ActiveStateName}' for the replay of the " +
            "blow here, which takes it there"
        );
        return true;
    }

    /// <summary>
    /// Whether a step of the scene host's is at a blow of another player than this game's: of the scene host's player,
    /// or of a third one, whose blow comes here too.
    /// </summary>
    private static bool IsAtOthersBlow(ClientPlayerData sceneHost, CoopSaveUpdate update) {
        return update.PartCount == OwnHostStruckPart ||
               update.PartCount == OwnStruckPart && !IsLocalPlayersBlow(sceneHost, update);
    }

    /// <summary>
    /// Whether a state machine of a thing with no health of its own takes blows by itself and counts them (see
    /// HitMachines.CountsHits), and waits for one where it is or comes back to waiting for one by itself. Only one
    /// that has started, whose actions are loaded.
    /// </summary>
    private static bool TakesBlowsItself(Fsm fsm) {
        return fsm is { Started: true, ActiveState: { } at, FsmComponent: { } machine } &&
               !machine.TryGetComponent<HealthManager>(out _) && HitMachines.CountsHits(machine) &&
               (HitMachines.ListensForHits(at) ||
                HitMachines.FindOnWay(machine, at, HitMachines.ListensForHits) != null);
    }

    /// <summary>
    /// Takes the steps of the scene host's of each state machine of a thing that waited for the replay of a blow on it,
    /// as it is made here (see <see cref="TookOthersBlow"/>).
    /// </summary>
    private void TakeStepsForBlow(GameObject struck) {
        if (_stepsForBlows.Count == 0) {
            return;
        }

        foreach (var fsm in _stepsForBlows.Keys.ToList()) {
            if (fsm.GameObject == struck) {
                TakeStepsForBlow(fsm, "as the replay of the blow is made here");
            }
        }
    }

    /// <summary>
    /// Takes the steps of the scene host's of a state machine that waited for the replay of a blow, as they came, up to
    /// the next one at a blow, which waits for its own.
    /// </summary>
    private void TakeStepsForBlow(Fsm fsm, string why) {
        var steps = _stepsForBlows[fsm];
        _stepsForBlows.Remove(fsm);
        var count = 1;
        while (count < steps.Count && !IsAtOthersBlow(steps[count].Player, steps[count].Step)) {
            count++;
        }

        Logger.Info(
            $"[Own creatures] '{steps[0].Step.ObjectPath}' takes {count} step(s) of '{fsm.Name}' of the scene host's " +
            $"from a blow there, {why}: " + string.Join(", ", steps.Take(count).Select(
                each => $"'{each.Step.StateName}'"
            ))
        );
        var before = _takingStepsForBlow;
        _takingStepsForBlow = fsm;
        try {
            for (var i = 0; i < count; i++) {
                OnOwnCreature(steps[i].Player, steps[i].Step);
            }
        } finally {
            _takingStepsForBlow = before;
        }

        // The next one at a blow waits for its own replay, with those after it
        if (count < steps.Count) {
            _stepsForBlows[fsm] = steps.GetRange(count, steps.Count - count);
        }
    }

    /// <summary>
    /// Takes the steps of the scene host's of each state machine whose replay of a blow did not come here within
    /// <see cref="MoveThroughWait"/> of the step at it, the step as the blow here.
    /// </summary>
    private void UpdateStepsForBlows() {
        if (_stepsForBlows.Count == 0) {
            return;
        }

        var now = Time.unscaledTime;
        foreach (var fsm in _stepsForBlows.Keys.ToList()) {
            if (!_stepsForBlows.TryGetValue(fsm, out var steps)) {
                continue;
            }

            if (fsm.FsmComponent == null) {
                _stepsForBlows.Remove(fsm);
                continue;
            }

            if (now - steps[0].Step.CameAt >= MoveThroughWait) {
                TakeStepsForBlow(fsm, $"as no replay of the blow came here within {MoveThroughWait:F0}s");
            }
        }
    }
}
