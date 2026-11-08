using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity.Action;

/// <summary>
/// What a creature switches on of the room for a catch of a player is that player's. The slashes that one boss plays
/// on the player its strands caught hurt "the player" as they start, with no touch: in the other game, where the copy
/// of the boss switched them on too, that was the player there, who was not caught (USER 11-08 "每次我被抓她都被抓了",
/// "…然后扣三血"). Switched on for a catch of another player - in the game that runs the creature, one that the
/// partner's game leads or led (Entity.CaughtIn); in the others, one of the scene host's player or of another, as the
/// scene host says with the switch (see the replay of ActivateGameObject) - what the FSMs of the object do to this
/// game's player is kept off them, and what turns its things the way the player faces turns them the way the caught
/// player does (see <see cref="PointAtFigure"/>), until the object is switched on again or off; all of it is kept off
/// for a player who has no figure here, as one who left. Not a part of the creature itself, which is its body and
/// strikes whoever it touches, nor another creature.
/// </summary>
internal static partial class EntityFsmActions {
    /// <summary>
    /// The objects of the room that a creature switched on for a catch of another player, with what is kept off this
    /// game's player in their FSMs.
    /// </summary>
    private static readonly Dictionary<GameObject, KeptForTheCaught> KeptOffForTheCaught = new();

    /// <summary>
    /// Gets an object ready to be switched on or off by a creature's FSM: what was kept off this game's player in it
    /// for an earlier catch is let back in, and for a catch of another player what its FSMs that ran before do to this
    /// game's player is kept off, as they start over the moment it is switched on (Fsm.OnEnable).
    /// </summary>
    /// <param name="target">The object.</param>
    /// <param name="switcher">The object of the FSM that switches it.</param>
    /// <param name="forAnother">Whether the creature switches the object on for a catch of another player than the
    /// local one.</param>
    /// <param name="figure">The figure of that player here, or null if they have none.</param>
    /// <returns>What is kept off, to go on with once the object is switched on (see <see cref="AfterSwitchingOn"/>),
    /// or null for nothing.</returns>
    internal static KeptForTheCaught? BeforeSwitchingOn(
        GameObject target,
        GameObject? switcher,
        bool forAnother,
        GameObject? figure
    ) {
        if (KeptOffForTheCaught.Count > 0 && KeptOffForTheCaught.Remove(target, out var earlier)) {
            earlier.LetBackIn();
        }

        if (!forAnother || switcher != null && target.transform.IsChildOf(switcher.transform) ||
            IsObjectInRegistry(target)) {
            return null;
        }

        var kept = new KeptForTheCaught(figure);
        foreach (var machine in target.GetComponentsInChildren<PlayMakerFSM>(true)) {
            if (machine.Fsm.Started) {
                kept.KeepOff(machine);
            }
        }

        return kept;
    }

    /// <summary>
    /// Goes on keeping off this game's player what an object switched on for a catch of another player does to them
    /// (see <see cref="BeforeSwitchingOn"/>), in its FSMs that never ran: they woke as it was switched on, and start
    /// after (PlayMakerFSM.Start).
    /// </summary>
    /// <param name="target">The object.</param>
    /// <param name="kept">What is kept off.</param>
    internal static void AfterSwitchingOn(GameObject target, KeptForTheCaught kept) {
        foreach (var machine in target.GetComponentsInChildren<PlayMakerFSM>(true)) {
            if (!machine.Fsm.Started && machine.gameObject.activeInHierarchy) {
                kept.KeepOff(machine);
            }
        }

        if (kept.Count == 0) {
            return;
        }

        KeptOffForTheCaught[target] = kept;
        Logger.Info(
            $"'{target.name}' is switched on for a catch of " +
            (kept.Caught != null ? $"'{kept.Caught.name}'" : "a player with no figure here") +
            $", so {kept.Count} action(s) of its FSMs keep off the local player until it is switched again"
        );
    }

    /// <summary>
    /// Forgets what was kept off this game's player in the objects of a room that is being left, which go with it.
    /// </summary>
    private static void ForgetKeptForTheCaught() {
        KeptOffForTheCaught.Clear();
    }

    /// <summary>
    /// What is kept off this game's player in the FSMs of an object switched on for a catch of another player.
    /// </summary>
    /// <param name="caught">The figure of the player caught, or null if they have none here.</param>
    internal sealed class KeptForTheCaught(GameObject? caught) {
        /// <summary>
        /// The actions, each with the field that named the player and what it held, for one pointed at the figure
        /// rather than switched off.
        /// </summary>
        private readonly List<(FsmStateAction Action, FieldInfo? Field, object? Before)> _actions = [];

        /// <summary>
        /// The figure of the player caught, or null if they have none here.
        /// </summary>
        public GameObject? Caught { get; } = caught;

        /// <summary>
        /// How many actions are kept off.
        /// </summary>
        public int Count => _actions.Count;

        /// <summary>
        /// Keeps off this game's player what an FSM does to them (see <see cref="IsThePlayersOwn"/>), pointing at the
        /// figure of the player caught what it turns or puts at the player, if they have one here.
        /// </summary>
        public void KeepOff(PlayMakerFSM machine) {
            foreach (var action in PlayersOwnActionsOf(machine.Fsm)) {
                if (Caught != null && PointAtFigure(action, Caught) is { } pointed) {
                    _actions.Add((action, pointed.Field, pointed.Before));
                    continue;
                }

                action.Enabled = false;
                _actions.Add((action, null, null));
            }
        }

        /// <summary>
        /// Lets back in what was kept off.
        /// </summary>
        public void LetBackIn() {
            foreach (var (action, field, before) in _actions) {
                if (field != null) {
                    PointBack(action, field, before);
                } else {
                    action.Enabled = true;
                }
            }
        }
    }
}
