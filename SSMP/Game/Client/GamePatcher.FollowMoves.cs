using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using Mono.Cecil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using OpCodes = Mono.Cecil.Cil.OpCodes;

namespace SSMP.Game.Client;

/// <summary>
/// The actions that move their creature after another object at every frame, to a distance from it or right at it,
/// which a creature that every game runs by itself moves by in a move after a player (see Entity.OwnCreatures). In
/// every game but the scene host's, while that game tells where its creature is (see CoopSave.OwnFollows), they go on
/// as the game has them, with their turns and the clips that they play, but what they set the speed of their object
/// to, the push that they give it and the place that they snap it to carry the motion that makes for where the scene
/// host's is, on the ways that they move it: across, up and down, or both.
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// The kinds of action that move their object after another at every frame, with whether each moves it across and
    /// whether up and down: the other way it leaves to the rest of its state and to its fall.
    /// </summary>
    private static readonly (string Name, bool Across, bool Upright)[] FollowMoverKinds = [
        ("DistanceFly", true, true), ("DistanceFlyV2", true, true), ("DistanceFlyV3", true, true),
        ("DistanceFlyHorizontal", true, true), ("DistanceFlySmooth", true, true), ("ChaseObject", true, true),
        ("ChaseObjectGround", true, false), ("ChaseObjectV2", true, true), ("ChaseObjectV3", true, true),
        ("ChaseObjectVertical", false, true), ("ChaseObjectWisp", true, true), ("DistanceWalk", true, false),
        ("DistanceWalkServitor", true, false), ("DistanceWalkServitorV", false, true)
    ];

    /// <summary>
    /// The methods of the follow movers that move their object, at every frame or as they snap it to its target.
    /// </summary>
    private static readonly string[] FollowMoverSteps = ["DoBuzz", "DoChase", "DoWalk", "DoSnap"];

    /// <summary>
    /// The kinds of action of <see cref="FollowMoverKinds"/> as they are in the game, with the ways that they move
    /// their object.
    /// </summary>
    private static readonly Dictionary<Type, (bool Across, bool Upright)> FollowMovers = new();

    /// <summary>
    /// The fields of each kind of follow mover in which it is given an object, by type (see
    /// <see cref="MovesAfterAPlayer"/>).
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> FollowMoverObjectFields = new();

    /// <summary>
    /// The hooks on the methods by which the follow movers move their object.
    /// </summary>
    private readonly List<ILHook> _followMoveHooks = [];

    /// <summary>
    /// The motion of the object of an action that moves after another at every frame, which makes for where the scene
    /// host's game has it, while that game moves it, in the state of the latest word of it that this game follows;
    /// none otherwise (see CoopSave.OwnFollows). Given by the code of the saves.
    /// </summary>
    public static Func<FsmStateAction, Vector2?>? SceneHostsMotion { get; set; }

    /// <summary>
    /// Has the follow movers move their object by the scene host's motion of it while that game moves it.
    /// </summary>
    private void RegisterFollowMoveHooks() {
        foreach (var (name, across, upright) in FollowMoverKinds) {
            if (GetFsmActionTypeByName(name) is not { } type) {
                Logger.Warn($"Could not find the action {name}, so a move by it goes by each game alone");
                continue;
            }

            FollowMovers[type] = (across, upright);
            foreach (var step in FollowMoverSteps) {
                var method = type.GetMethod(
                    step, InstancePublicFlags | InstanceNonPublicFlags | BindingFlags.DeclaredOnly
                );
                if (method == null) {
                    continue;
                }

                try {
                    _followMoveHooks.Add(new ILHook(method, il => ILFollowMoverStep(il, $"{name}#{step}")));
                } catch (Exception e) {
                    Logger.Error($"Could not hook {name}#{step}:\n{e}");
                }
            }
        }
    }

    /// <summary>
    /// Disposes the hooks on the follow movers.
    /// </summary>
    private void DisposeFollowMoveHooks() {
        foreach (var hook in _followMoveHooks) {
            hook.Dispose();
        }

        _followMoveHooks.Clear();
    }

    /// <summary>
    /// IL hook for a method of a follow mover that moves its object: each speed that it sets the object's body to goes
    /// through <see cref="SetFollowVelocity"/>, each push that it gives it through <see cref="AddFollowPush"/>, and
    /// each place that it snaps it to through <see cref="SetFollowPlace"/>.
    /// </summary>
    private static void ILFollowMoverStep(ILContext il, string where) {
        try {
            var c = new ILCursor(il);
            var writes = 0;
            while (c.TryGotoNext(
                       MoveType.Before,
                       i => i.MatchCallvirt(typeof(Rigidbody2D), "set_linearVelocity") ||
                            i.MatchCallvirt(typeof(Rigidbody2D), nameof(Rigidbody2D.AddForce)) ||
                            i.MatchCallvirt(typeof(Transform), "set_position")
                   )) {
                // The write itself becomes the load of the action, so that what branches to the write comes to it,
                // and the call after it writes as the action's object goes.
                var write = c.Next!;
                var written = ((MethodReference) write.Operand).Name;
                write.OpCode = OpCodes.Ldarg_0;
                write.Operand = null;
                c.Index++;
                switch (written) {
                    case "set_linearVelocity":
                        c.EmitDelegate<Action<Rigidbody2D, Vector2, FsmStateAction>>(SetFollowVelocity);
                        break;
                    case nameof(Rigidbody2D.AddForce):
                        c.EmitDelegate<Action<Rigidbody2D, Vector2, FsmStateAction>>(AddFollowPush);
                        break;
                    default:
                        c.EmitDelegate<Action<Transform, Vector3, FsmStateAction>>(SetFollowPlace);
                        break;
                }

                writes++;
            }

            if (writes == 0) {
                Logger.Warn($"{where} no longer moves its object where it was looked for, so it goes by each game");
            }
        } catch (Exception e) {
            Logger.Error($"Could not change {where} IL:\n{e}");
        }
    }

    /// <summary>
    /// Sets the speed of the body of a follow mover's object, which takes the scene host's motion of it on the ways
    /// that the mover moves it while that game moves it (see <see cref="SceneHostsMotion"/>).
    /// </summary>
    private static void SetFollowVelocity(Rigidbody2D body, Vector2 speed, FsmStateAction mover) {
        body.linearVelocity = SceneHostsMotion?.Invoke(mover) is { } led ? OnMoversWays(speed, led, mover) : speed;
    }

    /// <summary>
    /// Gives the body of a follow mover's object its push, or sets it to the scene host's motion of it on the ways
    /// that the mover moves it while that game moves it (see <see cref="SceneHostsMotion"/>).
    /// </summary>
    private static void AddFollowPush(Rigidbody2D body, Vector2 push, FsmStateAction mover) {
        if (SceneHostsMotion?.Invoke(mover) is { } led) {
            body.linearVelocity = OnMoversWays(body.linearVelocity, led, mover);
        } else {
            body.AddForce(push);
        }
    }

    /// <summary>
    /// Snaps the object of a follow mover to its target, unless the scene host's game moves it (see
    /// <see cref="SceneHostsMotion"/>).
    /// </summary>
    private static void SetFollowPlace(Transform moved, Vector3 place, FsmStateAction mover) {
        if (SceneHostsMotion?.Invoke(mover) == null) {
            moved.position = place;
        }
    }

    /// <summary>
    /// A speed with the scene host's motion on the ways that a follow mover moves its object, and its own on the other.
    /// </summary>
    private static Vector2 OnMoversWays(Vector2 own, Vector2 led, FsmStateAction mover) {
        var (across, upright) = FollowMovers[mover.GetType()];

        // A flyer across that is told to leave the height be moves its object across alone, and writes back the speed
        // up and down that the body has (review of 250a22e)
        upright &= mover is not HutongGames.PlayMaker.Actions.DistanceFlyHorizontal { dontAffectY.Value: true };
        return new Vector2(across ? led.x : own.x, upright ? led.y : own.y);
    }

    /// <summary>
    /// Whether an action moves the object of its state machine after a player at every frame: a follow mover of its
    /// own object that is given a player, this game's or the figure of another (see
    /// Entity.Action.EntityFsmActions.IsAPlayer), as the creature's target is put in its fields.
    /// </summary>
    internal static bool MovesAfterAPlayer(FsmStateAction action) {
        var type = action.GetType();
        if (!FollowMovers.ContainsKey(type)) {
            return false;
        }

        if (!FollowMoverObjectFields.TryGetValue(type, out var fields)) {
            fields = Array.FindAll(
                type.GetFields(BindingFlags.Instance | BindingFlags.Public),
                field => field.FieldType == typeof(FsmGameObject) || field.FieldType == typeof(FsmOwnerDefault)
            );
            FollowMoverObjectFields[type] = fields;
        }

        var movesItsOwn = true;
        var afterAPlayer = false;
        foreach (var field in fields) {
            switch (field.GetValue(action)) {
                case FsmOwnerDefault moved:
                    movesItsOwn &= action.Fsm.GetOwnerDefaultTarget(moved) == action.Fsm.GameObject;
                    break;
                case FsmGameObject { Value: { } followed } when followed != null:
                    afterAPlayer |= Entity.Action.EntityFsmActions.IsAPlayer(followed);
                    break;
            }
        }

        return movesItsOwn && afterAPlayer;
    }
}
