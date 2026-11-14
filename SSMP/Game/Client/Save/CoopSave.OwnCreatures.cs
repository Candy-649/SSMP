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
/// went to, how many times its actions had rolled before, where it was and whether it crawled. A creature of theirs
/// that took the same step, a moment before or after, but stood somewhere else as it did, is moved by as much as it
/// was off, and where it stood at the steps it kept goes with it, so that a step it took before it was moved is not
/// made up for once more (USER 11-14 "我刚刚看你站在怪身上": each step of a walk that it had taken before the word of
/// the first came moved it again, further off each time, 66 units at last). One that did not take it is put where the
/// scene host's was, crawling or not as that one was, and takes it, from the same rolls (USER 11-14 "边唱边移动": put
/// into its song straight from a walk, where the scene host's had begun it standing, it crawled on as it sang). A song
/// is the scene host's to begin and end (see GamePatcher.FollowsSceneHostsSong). A member who comes into the room
/// is told where each one is and what it does, or that it is dead, which theirs then is too. One that dies in any
/// game dies in the others as well, whatever killed it there: a blow that killed it in one game took less in another
/// that took the blows of both players in another order (see EnemyHealthCoop).
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
    /// The hooks that see the creatures that every game runs by itself go from one state to another and die.
    /// </summary>
    private readonly List<Hook?> _ownCreatureHooks = [];

    /// <summary>
    /// Whether a creature that every game runs by itself dies as another game told, which is not told back.
    /// </summary>
    private bool _dyingAsTold;

    /// <summary>
    /// Watches the creatures that every game runs by itself go from one state to another and die.
    /// </summary>
    private void RegisterOwnCreatureHooks() {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        _ownCreatureHooks.Add(CreateHook(
            typeof(Fsm).GetMethod("SwitchState", flags, null, [typeof(FsmState)], null),
            new Action<Action<Fsm, FsmState>, Fsm, FsmState>(OnOwnCreatureStep)
        ));

        // The largest of the ways to die, which the others call
        _ownCreatureHooks.Add(CreateHook(
            typeof(HealthManager).GetMethod(
                nameof(HealthManager.Die), flags, null,
                [
                    typeof(float?), typeof(AttackTypes), typeof(NailElements), typeof(GameObject), typeof(bool),
                    typeof(float), typeof(bool), typeof(bool)
                ],
                null
            ),
            new Action<Action<HealthManager, float?, AttackTypes, NailElements, GameObject, bool, float, bool, bool>,
                HealthManager, float?, AttackTypes, NailElements, GameObject, bool, float, bool, bool>(OnOwnCreatureDie)
        ));
    }

    /// <summary>
    /// Hook for a state machine going to another state, which keeps the step of a creature that every game runs by
    /// itself and, in the scene host's game, tells the members in the room of it. It is told as it is taken, before the
    /// state starts, so that a step that the state takes at once is told after it.
    /// </summary>
    private void OnOwnCreatureStep(Action<Fsm, FsmState> orig, Fsm self, FsmState toState) {
        // A switch to no state, which the game makes nothing of, is passed on as it is
        if (OwnCreatures.Count == 0 || toState == null || self.FsmComponent is not { } machine || machine == null ||
            !OwnCreatures.Has(machine.gameObject)) {
            orig(self, toState!);
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
    /// Hook for the death of a creature, which tells the members in the room that a creature that every game runs by
    /// itself died here, unless it died as another game told.
    /// </summary>
    private void OnOwnCreatureDie(
        Action<HealthManager, float?, AttackTypes, NailElements, GameObject, bool, float, bool, bool> orig,
        HealthManager self,
        float? attackDirection,
        AttackTypes attackType,
        NailElements nailElements,
        GameObject source,
        bool ignoreEvasion,
        float corpseFlingMultiplier,
        bool overrideSpecialDeath,
        bool disallowDropFlying
    ) {
        var tells = !_dyingAsTold && !self.isDead && OwnCreatures.Has(self.gameObject);
        orig(
            self, attackDirection, attackType, nailElements, source, ignoreEvasion, corpseFlingMultiplier,
            overrideSpecialDeath, disallowDropFlying
        );
        if (!tells || !self.isDead) {
            return;
        }

        var creature = self.gameObject;
        var place = OwnCreatures.PlaceOf(creature);
        Logger.Info($"[Own creatures] '{place}' died here, as the members in the room are told");
        SendToRoomMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.OwnCreature,
            Scene = creature.scene.name,
            ObjectPath = place,
            PartCount = 2,
            Values = attackDirection is { } direction ? [direction] : []
        });
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
            Values = [
                step.Place.x, step.Place.y, transform.localScale.x, transform.eulerAngles.z,
                machine.GetComponent<Crawler>() is { IsCrawling: true } ? 1f : 0f
            ]
        };
    }

    /// <summary>
    /// Tells a member who has come into the room where each creature that every game runs by itself is here and what
    /// it does, or that it is dead, if this game is the scene host's. Their game has them where the room starts them.
    /// </summary>
    /// <param name="player">The member.</param>
    private void SendOwnCreaturesTo(ClientPlayerData player) {
        if (OwnCreatures.Count == 0 || !_checkedMembers.Contains(player.Id) || IsSceneHost?.Invoke() != true) {
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

            // One that has not come out of where the room keeps it, as the save has it, does nothing yet
            if (!creature.activeInHierarchy) {
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

        var health = creature.GetComponent<HealthManager>();
        if (!creature.activeInHierarchy || health.isDead) {
            Logger.Info(
                $"[Own creatures] {player.Username}'s game told of '{place}', which is dead or not out here" +
                (update.PartCount > 0 ? ", and dead there too" : $", and went to '{update.StateName}' there")
            );
            return;
        }

        // Dead there before the member came in: taken away, as the room has it there
        if (update.PartCount == 1) {
            Logger.Info($"[Own creatures] '{place}' is dead in {player.Username}'s game and is taken away here");
            creature.SetActive(false);
            return;
        }

        // Died there just now: it dies here too, as the game has a creature die, though the blow that killed it there
        // did not here, or has not come yet
        if (update.PartCount == 2) {
            Logger.Info($"[Own creatures] '{place}' died in {player.Username}'s game and dies here too");
            OwnCreatures.AwardJournalKill(health);
            _dyingAsTold = true;
            try {
                health.Die(
                    update.Values.Count > 0 ? update.Values[0] : null, AttackTypes.Generic, NailElements.None, null,
                    true, 1f, false, false
                );
            } finally {
                _dyingAsTold = false;
            }

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
                MoveOwnSteps(creature, off);
            }

            return;
        }

        Logger.Info(
            $"[Own creatures] '{place}' goes from '{fsm.ActiveStateName}' to '{update.StateName}' as in " +
            $"{player.Username}'s game, where it was there, {Vector2.Distance(transform.position, there):F1} away"
        );
        MoveOwnSteps(creature, there - transform.position);
        transform.position = there;
        var scale = transform.localScale;
        scale.x = update.Values[2];
        transform.localScale = scale;
        transform.rotation = Quaternion.Euler(0f, 0f, update.Values[3]);

        // Crawling or not as the scene host's was, which the state it goes to need not set: a song is begun from a
        // walk by a state that stops the crawl first, but straight from standing
        var crawling = update.Values[4] > 0f;
        if (creature.GetComponent<Crawler>() is { } crawler && crawler.IsCrawling != crawling) {
            if (crawling) {
                crawler.StartCrawling();
            } else {
                crawler.StopCrawling();
            }
        }

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
    /// Moves where the kept steps of a creature that every game runs by itself say it stood by as much as it was just
    /// moved, so that a step of the scene host's that it took before is not made up for once more.
    /// </summary>
    private void MoveOwnSteps(GameObject creature, Vector3 by) {
        foreach (var machine in creature.GetComponents<PlayMakerFSM>()) {
            if (!_ownSteps.TryGetValue(machine.Fsm, out var steps)) {
                continue;
            }

            for (var i = 0; i < steps.Count; i++) {
                steps[i] = steps[i] with { Place = steps[i].Place + by };
            }
        }
    }

    /// <summary>
    /// A step of a state machine of a creature that every game runs by itself: the state it went to, how many times
    /// its actions had rolled before (see SharedDice.RollCounts), and where the creature was.
    /// </summary>
    private readonly record struct OwnStep(string State, List<int> Counts, Vector3 Place);
}
