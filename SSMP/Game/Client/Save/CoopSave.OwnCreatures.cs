using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The creatures that every game runs by itself as the room's own (see OwnCreatures), kept to how the scene host's
/// game has them. Each one rolls what the scene host's rolls, by its place in the room, and takes the same hits, so it
/// does what that one does as long as nothing that only one game has, like where its own player stands, takes it
/// somewhere else.
///
/// As a creature of the scene host's goes from one state to another, the members in the room are told which state it
/// went to, how many times its actions had rolled before, and where it was. A creature of theirs that took the same
/// step, a moment before or after, but stood somewhere else as it did, is moved by as much as it was off. One that did
/// not take it is put where the scene host's was and takes it, from the same rolls. A member who comes into the room
/// is told where each one is and what it does, or that it is dead, which theirs then is too.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How many of its last steps are kept of a state machine of a creature that every game runs by itself, to tell
    /// whether it took a step of the scene host's creature before the word of it came.
    /// </summary>
    private const int OwnStepsKept = 8;

    /// <summary>
    /// How far a creature that every game runs by itself may have stood from where the scene host's stood as both took
    /// the same step, before it is moved.
    /// </summary>
    private const float OwnCreatureDrift = 0.5f;

    /// <summary>
    /// The last steps of the state machines of the creatures that every game runs by itself, the last one last.
    /// </summary>
    private readonly Dictionary<Fsm, List<OwnStep>> _ownSteps = new();

    /// <summary>
    /// The hook that sees the creatures that every game runs by itself go from one state to another.
    /// </summary>
    private readonly List<Hook?> _ownCreatureHooks = [];

    /// <summary>
    /// Watches the creatures that every game runs by itself go from one state to another.
    /// </summary>
    private void RegisterOwnCreatureHooks() {
        _ownCreatureHooks.Add(CreateHook(
            typeof(Fsm).GetMethod(
                "SwitchState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                [typeof(FsmState)], null
            ),
            new Action<Action<Fsm, FsmState>, Fsm, FsmState>(OnOwnCreatureStep)
        ));
    }

    /// <summary>
    /// Hook for a state machine going to another state, which keeps the step of a creature that every game runs by
    /// itself and, in the scene host's game, tells the members in the room of it. It is told as it is taken, before the
    /// state starts, so that a step that the state takes at once is told after it.
    /// </summary>
    private void OnOwnCreatureStep(Action<Fsm, FsmState> orig, Fsm self, FsmState toState) {
        if (OwnCreatures.Count == 0 || self.FsmComponent is not { } machine || machine == null ||
            !OwnCreatures.Has(machine.gameObject)) {
            orig(self, toState);
            return;
        }

        var creature = machine.gameObject;
        SharedDice.RollByName(creature, OwnCreatures.RollName(creature));
        var step = new OwnStep(toState.Name, SharedDice.RollCounts(creature), creature.transform.position);
        if (!_ownSteps.TryGetValue(self, out var steps)) {
            steps = [];
            _ownSteps[self] = steps;
        }

        steps.Add(step);
        if (steps.Count > OwnStepsKept) {
            steps.RemoveAt(0);
        }

        if (IsSceneHost?.Invoke() == true) {
            Logger.Info(
                $"[Own creatures] '{OwnCreatures.PlaceOf(creature)}' goes from '{self.ActiveStateName}' to " +
                $"'{toState.Name}' after {step.Counts.Count / 2} kinds of rolls, as the members in the room are told"
            );
            SendToRoomMembers(OwnCreatureUpdate(machine, step));
        }

        orig(self, toState);
    }

    /// <summary>
    /// The word of a step that a creature that every game runs by itself took here.
    /// </summary>
    private static CoopSaveUpdate OwnCreatureUpdate(PlayMakerFSM machine, OwnStep step) {
        var transform = machine.transform;
        return new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.OwnCreature,
            Scene = machine.gameObject.scene.name,
            ObjectPath = OwnCreatures.PlaceOf(machine.gameObject),
            FsmName = machine.FsmName,
            StateName = step.State,
            Amounts = step.Counts,
            Values = [step.Place.x, step.Place.y, transform.localScale.x, transform.eulerAngles.z]
        };
    }

    /// <summary>
    /// Tells a member who has come into the room where each creature that every game runs by itself is here and what
    /// it does, or that it is dead, if this game is the scene host's. Their game has them where the room starts them.
    /// </summary>
    /// <param name="player">The member.</param>
    private void SendOwnCreaturesTo(ClientPlayerData player) {
        if (OwnCreatures.Count == 0 || IsSceneHost?.Invoke() != true) {
            return;
        }

        foreach (var pair in OwnCreatures.All) {
            var creature = pair.Key;
            if (creature == null || creature.GetComponent<HealthManager>().isDead) {
                Logger.Info($"[Own creatures] {player.Username} is told that '{pair.Value.Place}' is dead here");
                Send(new CoopSaveUpdate {
                    TargetId = player.Id,
                    Kind = CoopSaveUpdateKind.OwnCreature,
                    Scene = pair.Value.Scene,
                    ObjectPath = pair.Value.Place,
                    PartCount = 1
                });
                continue;
            }

            foreach (var machine in creature.GetComponents<PlayMakerFSM>()) {
                var fsm = machine.Fsm;
                var last = _ownSteps.TryGetValue(fsm, out var steps) ? steps[steps.Count - 1] : (OwnStep?) null;
                var counts = last is { } step && step.State == fsm.ActiveStateName
                    ? step.Counts
                    : SharedDice.RollCounts(creature);
                var update = OwnCreatureUpdate(
                    machine, new OwnStep(fsm.ActiveStateName, counts, creature.transform.position)
                );
                update.TargetId = player.Id;
                Logger.Info(
                    $"[Own creatures] {player.Username} is told that '{pair.Value.Place}' is in " +
                    $"'{fsm.ActiveStateName}' here"
                );
                Send(update);
            }
        }
    }

    /// <summary>
    /// Keeps a creature that every game runs by itself to a step that the scene host's took, or to its death there.
    /// </summary>
    /// <param name="player">The player whose game is the scene host's.</param>
    /// <param name="update">The update.</param>
    private void OnOwnCreature(ClientPlayerData player, CoopSaveUpdate update) {
        var place = update.ObjectPath;
        if (OwnCreatures.Find(update.Scene, place) is not { } creature) {
            Logger.Info($"[Own creatures] {player.Username}'s game told of '{place}', which does not run here");
            return;
        }

        if (!creature.activeSelf) {
            Logger.Info(
                $"[Own creatures] {player.Username}'s game told of '{place}', which is dead here" +
                (update.PartCount == 1 ? " too" : $" and went to '{update.StateName}' there")
            );
            return;
        }

        if (update.PartCount == 1) {
            Logger.Info($"[Own creatures] '{place}' is dead in {player.Username}'s game and is taken away here");
            creature.SetActive(false);
            return;
        }

        var machine = PlayMakerFSM.FindFsmOnGameObject(creature, update.FsmName);
        var fsm = machine.Fsm;
        var transform = creature.transform;
        var there = new Vector3(update.Values[0], update.Values[1], transform.position.z);
        if (FindOwnStep(fsm, update) is { } took) {
            var off = there - took.Place;
            off.z = 0f;
            if (off.magnitude > OwnCreatureDrift) {
                Logger.Info(
                    $"[Own creatures] '{place}' went to '{update.StateName}' as in {player.Username}'s game, " +
                    $"{off.magnitude:F1} away from where it did there, and is moved by that"
                );
                transform.position += off;
            }

            return;
        }

        Logger.Info(
            $"[Own creatures] '{place}' goes from '{fsm.ActiveStateName}' to '{update.StateName}' as in " +
            $"{player.Username}'s game, where it was there, {Vector2.Distance(transform.position, there):F1} away"
        );
        transform.position = there;
        var scale = transform.localScale;
        scale.x = update.Values[2];
        transform.localScale = scale;
        transform.rotation = Quaternion.Euler(0f, 0f, update.Values[3]);
        SharedDice.RollByName(creature, OwnCreatures.RollName(creature));
        SharedDice.SetRollCounts(creature, update.Amounts);
        fsm.SetState(update.StateName);
    }

    /// <summary>
    /// The last step of a state machine here that went to the state of a step of the scene host's creature after as
    /// many rolls, which is that step, or null if it took none.
    /// </summary>
    private OwnStep? FindOwnStep(Fsm fsm, CoopSaveUpdate update) {
        if (!_ownSteps.TryGetValue(fsm, out var steps)) {
            return null;
        }

        for (var i = steps.Count - 1; i >= 0; i--) {
            if (steps[i].State == update.StateName && steps[i].Counts.SequenceEqual(update.Amounts)) {
                return steps[i];
            }
        }

        return null;
    }

    /// <summary>
    /// A step of a state machine of a creature that every game runs by itself: the state it went to, how many times
    /// its actions had rolled before (see SharedDice.RollCounts), and where the creature was.
    /// </summary>
    private readonly record struct OwnStep(string State, List<int> Counts, Vector3 Place);
}
