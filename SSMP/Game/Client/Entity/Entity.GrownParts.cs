using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity.Action;
using SSMP.Game.Client.Entity.Component;
using SSMP.Game.Client.Save;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// What a creature grows onto itself, said again to a player who walks in after it grew: the spines that a floater
/// grows around itself, or anything else that a state of a creature's FSM spawns and then puts under one of the
/// creature's own parts. The game that runs the creature sends what its actions do as they do it, and nothing of it is
/// kept for later, so a player who walked in afterwards had a copy without them; and what a creature grows in the first
/// frames of its room, before the games have settled which of them runs it, went nowhere at all. A floater's copy then
/// had no spines and shot nothing the first time it fired (USER 10-24 "初始状态的针没有长出来").
///
/// Each thing that is on the creature now goes out again as the actions of its growth: the spawn, under the name the
/// thing has here (see <see cref="ThrownThings.SayAgain"/>), then the actions of the same state that put it in its
/// place, which send where it sits and how it is turned under its parent now. The other game's copy plays them as it
/// plays them when they come as they happen.
/// </summary>
internal partial class Entity {
    /// <summary>
    /// The kinds of action that put a grown thing in its place after its spawn, or show it: under its parent, where it
    /// sits and how it is turned there, sent the way it is now, how big it is, and the clip it plays. Found in the
    /// game's data: the floaters' spines are turned, and the rings that a stun puts over a creature's head are sized and
    /// set playing before they are put on it. A growth whose state does anything else with the thing - calls a method
    /// of it, tells it something - is not said again: half of its making would leave a thing that is not what it is in
    /// the game that runs the creature.
    /// </summary>
    private static readonly HashSet<System.Type> PlacingActionTypes = [
        typeof(SetParent), typeof(SetRotation), typeof(SetScale), typeof(Tk2dPlayAnimation)
    ];

    /// <summary>
    /// How each FSM of the creature grows things onto it, found the first time it is needed; null until then.
    /// </summary>
    private List<Growth>? _growths;

    /// <summary>
    /// One way of the creature's FSMs to grow a thing onto it: an action that spawns a prefab into a variable, and the
    /// actions of the same state after it that put what the variable holds in its place, under a part of the creature.
    /// </summary>
    /// <param name="Spawn">The action that spawns it.</param>
    /// <param name="Prefab">What it spawns.</param>
    /// <param name="Parenting">The action that puts it under a part of the creature.</param>
    /// <param name="Placing">The actions that put it in its place, in their order, the parenting among them.</param>
    private sealed record Growth(
        HookedEntityAction Spawn,
        GameObject Prefab,
        SetParent Parenting,
        List<HookedEntityAction> Placing
    );

    /// <summary>
    /// Sends again what the creature grew onto itself (see the summary of this part), for a player who walked into the
    /// room of this game, which runs it. Each thing goes once: by the first growth that puts things of its prefab under
    /// its parent, as the replays send where each thing sits and how it is turned rather than where its own growth put
    /// it.
    /// </summary>
    internal void SendGrownPartsAgain() {
        if (_isControlled || Object.Host == null || !Object.Host.activeInHierarchy) {
            return;
        }

        _growths ??= FindGrowths();
        if (_growths.Count == 0) {
            return;
        }

        var done = new HashSet<(Transform, GameObject)>();
        var things = new List<GameObject>();
        var sent = 0;
        foreach (var growth in _growths) {
            // Under a part of the creature, which a growth that puts things somewhere else does not grow onto it
            var parent = growth.Parenting.parent.Value;
            if (parent == null || !parent.activeInHierarchy || !parent.transform.IsChildOf(Object.Host.transform) ||
                !done.Add((parent.transform, growth.Prefab))) {
                continue;
            }

            things.Clear();
            foreach (Transform child in parent.transform) {
                if (child.gameObject.activeSelf && IsMadeFrom(child.gameObject, growth.Prefab)) {
                    things.Add(child.gameObject);
                }
            }

            foreach (var thing in things) {
                SendGrowthAgain(growth, thing);
                sent++;
            }
        }

        if (sent > 0) {
            Logger.Info($"[Grown] Sent again the {sent} thing(s) that '{Object.Host.name}' ({Id}) grew onto itself");
        }
    }

    /// <summary>
    /// Sends the actions of a growth again for one thing that it grew: the variable of the growth holds the thing
    /// meanwhile, as it did when the thing grew, and what it held before afterwards.
    /// </summary>
    /// <param name="growth">The growth.</param>
    /// <param name="thing">The thing.</param>
    private void SendGrowthAgain(Growth growth, GameObject thing) {
        var variable = ((SpawnObjectFromGlobalPool) growth.Spawn.Action).storeObject;
        var held = variable.Value;
        try {
            variable.Value = thing;
            using (ThrownThings.SayAgain(thing, growth.Prefab.name)) {
                SendReplay(growth.Spawn, null);
            }

            // What came before it was put under its parent went out with the thing still loose, which the copy reads
            // as the thing that its own spawn just made rather than by a place under the creature: it is said so again,
            // as the thing is under its parent here by now
            var loose = variable;
            foreach (var placing in growth.Placing) {
                if (placing.Action == growth.Parenting) {
                    loose = null;
                }

                SendReplay(placing, loose);
            }
        } finally {
            variable.Value = held;
        }
    }

    /// <summary>
    /// Sends the replay of an action as <see cref="OnActionEntered"/> does, without the state it is in: the FSM is
    /// told in its own state, which may be another by now.
    /// </summary>
    /// <param name="hooked">The action.</param>
    /// <param name="loose">A variable whose object is said to be loose, not a part of the creature, or null.</param>
    private void SendReplay(HookedEntityAction hooked, FsmGameObject? loose) {
        var networkData = new EntityNetworkData {
            Type = EntityComponentType.Fsm
        };

        if (_fsms.Host.Count > 1) {
            networkData.Packet.Write((byte) hooked.FsmIndex);
        }

        networkData.Packet.Write((byte) hooked.StateIndex);
        networkData.Packet.Write((byte) hooked.ActionIndex);
        EntityFsmActions.WriteSubject(networkData, hooked.Action, loose);
        if (EntityFsmActions.GetNetworkDataFromAction(networkData, hooked.Action)) {
            SendEntityData(networkData);
        }
    }

    /// <summary>
    /// Finds how the creature's FSMs grow things onto it: in each state, a spawn of something that is not a creature
    /// into a variable, followed by an action that puts what the variable holds under a parent, with the actions
    /// between it and the next spawn into that variable that place it. Whether the parent is a part of the creature is
    /// asked as things are sent: the variable that holds it may be filled later than this is found.
    /// </summary>
    private List<Growth> FindGrowths() {
        var growths = new List<Growth>();
        var byState = new Dictionary<FsmState, List<HookedEntityAction>>();
        foreach (var hooked in _hookedActions.Values) {
            if (hooked.Action.State is not { } state) {
                continue;
            }

            if (!byState.TryGetValue(state, out var actions)) {
                actions = [];
                byState[state] = actions;
            }

            actions.Add(hooked);
        }

        foreach (var actions in byState.Values) {
            actions.Sort((a, b) => a.ActionIndex.CompareTo(b.ActionIndex));
            for (var i = 0; i < actions.Count; i++) {
                if (actions[i].Action is not SpawnObjectFromGlobalPool spawn ||
                    spawn.storeObject is not { UseVariable: true } variable || spawn.gameObject?.Value is not { } prefab ||
                    prefab.GetComponentInChildren<HealthManager>(true) != null) {
                    continue;
                }

                SetParent? parenting = null;
                var placing = new List<HookedEntityAction>();
                var sayable = true;
                for (var j = i + 1; j < actions.Count; j++) {
                    var next = actions[j].Action;
                    if (next is SpawnObjectFromGlobalPool { storeObject: { UseVariable: true } stored } &&
                        stored.Name == variable.Name) {
                        break;
                    }

                    var uses = UsesOf(next, variable.Name);
                    if (uses == 0) {
                        continue;
                    }

                    // Only as the object it places or plays, and nowhere else in it
                    if (uses > 1 || !PlacingActionTypes.Contains(next.GetType()) || !WorksOn(next, variable)) {
                        sayable = false;
                        break;
                    }

                    placing.Add(actions[j]);
                    if (next is SetParent setParent && parenting == null) {
                        parenting = setParent;
                    }
                }

                if (sayable && parenting != null) {
                    growths.Add(new Growth(actions[i], prefab, parenting, placing));
                }
            }
        }

        return growths;
    }

    /// <summary>
    /// Whether an action works on what a variable holds: its object is that variable.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <param name="variable">The variable.</param>
    private static bool WorksOn(FsmStateAction action, FsmGameObject variable) {
        var owner = action switch {
            SetParent setParent => setParent.gameObject,
            SetRotation setRotation => setRotation.gameObject,
            SetScale setScale => setScale.gameObject,
            Tk2dPlayAnimation play => play.gameObject,
            _ => null
        };

        return owner is { OwnerOption: OwnerDefaultOption.SpecifyGameObject, GameObject.UseVariable: true } &&
               owner.GameObject.Name == variable.Name;
    }

    /// <summary>
    /// How many of an action's parameters name a variable, as the object itself, as the owner-or-that-one pair, or as
    /// where an event goes.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <param name="variableName">The name of the variable.</param>
    private static int UsesOf(FsmStateAction action, string variableName) {
        var uses = 0;
        foreach (var field in action.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)) {
            var named = field.GetValue(action) switch {
                FsmGameObject variable => variable,
                FsmOwnerDefault { OwnerOption: OwnerDefaultOption.SpecifyGameObject } owner => owner.GameObject,
                FsmEventTarget { gameObject.OwnerOption: OwnerDefaultOption.SpecifyGameObject } target =>
                    target.gameObject.GameObject,
                _ => null
            };

            if (named is { UseVariable: true } && named.Name == variableName) {
                uses++;
            }
        }

        return uses;
    }

    /// <summary>
    /// Whether an object was made from a prefab: the pool makes its things as copies of the prefab, which the engine
    /// names after it with "(Clone)", and the pool never renames them. A part that the creature was built with under
    /// the prefab's own name is not one, and its copy has it already.
    /// </summary>
    /// <param name="gameObject">The object.</param>
    /// <param name="prefab">The prefab.</param>
    private static bool IsMadeFrom(GameObject gameObject, GameObject prefab) {
        return gameObject.name.StartsWith(prefab.name + "(Clone)", System.StringComparison.Ordinal);
    }
}
