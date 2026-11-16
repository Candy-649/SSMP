using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using OpCodes = Mono.Cecil.Cil.OpCodes;
using Random = UnityEngine.Random;

namespace SSMP.Game.Client;

/// <summary>
/// The walk of a creature that every game runs by itself (see Entity.OwnCreatures), which its Walker goes on with in
/// the game's code rather than in a state machine: it walks a while, stands a while, then turns round or walks on, and
/// rolls how long each goes on and which it does. Each game rolled them with its own dice, so that the creature walked
/// and stood by other times in each game, and was somewhere else in each before long, until a step of the scene host's
/// put it right. Now it rolls under the creature's name, the same in every game (see Save.SharedDice.RollForWalk), and
/// where its walk is goes with each step of the scene host's creature, which a creature that is put to the step takes
/// (see <see cref="PutWalk"/>): a member who comes in has theirs walk on where the scene host's walks, rather than set
/// off afresh, and both walk alike from there, set straight at the steps that come. Where it walks by a player, turning
/// to one it sees behind it and walking on while it sees them, it does so in the scene host's game alone (see
/// <see cref="SeesHere"/>), whose step comes to the others (USER 11-16 "总之不看人的优先采用两边同种子掷骰子然后轻微矫正
/// 的方式，看人的就房主传过来招式").
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// The methods of the Walker that roll how it walks.
    /// </summary>
    private static readonly string[] WalkRollers = ["StartMoving", "BeginStopped", "EndStopping", "BeginWalking"];

    /// <summary>
    /// The hooks on the methods of the Walker that roll and that look at the players.
    /// </summary>
    private readonly List<ILHook> _ownWalkHooks = [];

    /// <summary>
    /// The methods of the Walker that look at whether a player stands in its range as it walks.
    /// </summary>
    private static readonly string[] WalkLooks = ["UpdateWalking", "Update"];

    /// <summary>
    /// Has the Walker of a creature that every game runs by itself roll under the creature's name, and look at the
    /// players in the scene host's game alone.
    /// </summary>
    private void RegisterOwnWalkHooks() {
        foreach (var method in WalkRollers) {
            if (TryCreateILHook(typeof(Walker), method, il => ILOwnWalkDice(il, method)) is { } hook) {
                _ownWalkHooks.Add(hook);
            }
        }

        foreach (var method in WalkLooks) {
            if (TryCreateILHook(typeof(Walker), method, il => ILOwnWalkLooks(il, method)) is { } hook) {
                _ownWalkHooks.Add(hook);
            }
        }
    }

    /// <summary>
    /// Disposes the hooks on the methods of the Walker that roll and that look at the players.
    /// </summary>
    private void DisposeOwnWalkHooks() {
        foreach (var hook in _ownWalkHooks) {
            hook.Dispose();
        }

        _ownWalkHooks.Clear();
    }

    /// <summary>
    /// IL hook for a method of the Walker that rolls: each of its rolls comes through
    /// <see cref="RollWalk(float, float, Walker)"/>.
    /// </summary>
    private static void ILOwnWalkDice(ILContext il, string method) {
        try {
            var c = new ILCursor(il);
            var rolls = 0;
            while (c.TryGotoNext(MoveType.Before, i => i.MatchCall(typeof(Random), nameof(Random.Range)))) {
                var range = (MethodReference) c.Next!.Operand;
                var ofWholeNumbers = range.Parameters[0].ParameterType.MetadataType == MetadataType.Int32;
                c.Remove();
                c.Emit(OpCodes.Ldarg_0);
                if (ofWholeNumbers) {
                    c.EmitDelegate<Func<int, int, Walker, int>>(RollWalk);
                } else {
                    c.EmitDelegate<Func<float, float, Walker, float>>(RollWalk);
                }

                rolls++;
            }

            if (rolls == 0) {
                Logger.Warn($"Walker#{method} no longer rolls where it was looked for, so its walk is not the same");
            }
        } catch (Exception e) {
            Logger.Error($"Could not change Walker#{method} IL:\n{e}");
        }
    }

    /// <summary>
    /// Rolls a number of the walk of a creature, for its Walker: under the creature's name if it is one that every game
    /// runs by itself, else with the game's own dice, as the game rolls it.
    /// </summary>
    private static float RollWalk(float min, float max, Walker walker) {
        return Entity.OwnCreatures.Owning(walker.gameObject) is { } creature
            ? Save.SharedDice.RollForWalk(creature, () => Random.Range(min, max))
            : Random.Range(min, max);
    }

    /// <inheritdoc cref="RollWalk(float, float, Walker)"/>
    private static int RollWalk(int min, int max, Walker walker) {
        return Entity.OwnCreatures.Owning(walker.gameObject) is { } creature
            ? Save.SharedDice.RollForWalk(creature, () => Random.Range(min, max))
            : Random.Range(min, max);
    }

    /// <summary>
    /// IL hook for a method of the Walker that looks at whether a player stands in its range as it walks: each answer
    /// goes through <see cref="SeesHere"/>.
    /// </summary>
    private static void ILOwnWalkLooks(ILContext il, string method) {
        try {
            var c = new ILCursor(il);
            var looks = 0;
            while (c.TryGotoNext(
                       MoveType.After, i => i.MatchCallvirt(typeof(AlertRange), nameof(AlertRange.IsHeroInRange))
                   )) {
                c.Emit(OpCodes.Ldarg_0);
                c.EmitDelegate<Func<bool, Walker, bool>>(SeesHere);
                looks++;
            }

            if (looks == 0) {
                Logger.Warn($"Walker#{method} no longer looks at its range where it was looked for");
            }
        } catch (Exception e) {
            Logger.Error($"Could not change Walker#{method} IL:\n{e}");
        }
    }

    /// <summary>
    /// Whether a player stands in the range of a Walker, for what it does by itself as it walks: turning to one it
    /// sees behind it, walking on while it sees them, and the cooldown of its turn at an edge. Never in a game that
    /// follows the scene host's, for a creature that every game runs by itself: there the scene host's sees the
    /// player, and its state machine looks through the same sight and goes after them at once (the pilgrims' Walk
    /// stores CheckCanSeeHero and goes on ATTACK by a BoolTest), by a step that the others are told. One that looked by
    /// itself turned and walked on by where it had the players, a moment late and a little elsewhere, and rolled its
    /// walk otherwise from then on, as the scene host's did not (review).
    /// </summary>
    private static bool SeesHere(bool seen, Walker walker) {
        return seen && !Entity.OwnCreatures.FollowsSceneHost(walker.gameObject);
    }

    /// <summary>
    /// Writes where the walk of a creature that every game runs by itself is, for a step of the scene host's: whether
    /// it walks, stands a while or turns, which way it faces, how long each goes on yet, and how many numbers of its
    /// walk it rolled. Nothing for a creature with no Walker.
    /// </summary>
    internal static void WriteWalk(BinaryWriter writer, GameObject creature) {
        if (!creature.TryGetComponent<Walker>(out var walker)) {
            return;
        }

        writer.Write((byte) walker.state);
        writer.Write((byte) walker.stopReason);
        writer.Write((sbyte) walker.currentFacing);
        writer.Write((sbyte) walker.turningFacing);
        writer.Write(walker.walkTimeRemaining);
        writer.Write(walker.pauseTimeRemaining);
        writer.Write(walker.turnCooldownRemaining);
        writer.Write(walker.aggroEdgeTurnCooldownRemaining);
        writer.Write(Save.SharedDice.WalksOf(creature));
    }

    /// <summary>
    /// Puts the walk of a creature that every game runs by itself where the scene host's was at a step (see
    /// <see cref="WriteWalk"/>), by the game's own steps of the walk where it is otherwise here: one that walks there
    /// sets off the way it walks there, one that stands there stops, one that turns there begins to turn; then each
    /// goes on as long as there, and rolls on from where the scene host's rolled. A member who came in found theirs
    /// setting off afresh where the scene host's was halfway along, and each walked and stood by other times from then:
    /// they take it at the step that their own took already too (see CoopSave.OnOwnCreature).
    /// </summary>
    internal static void PutWalk(BinaryReader reader, GameObject creature) {
        if (!creature.TryGetComponent<Walker>(out var walker)) {
            return;
        }

        var state = (Walker.States) reader.ReadByte();
        var reason = (Walker.StopReasons) reader.ReadByte();
        int facing = reader.ReadSByte();
        int turning = reader.ReadSByte();
        switch (state) {
            case Walker.States.Walking when walker.state != state || walker.currentFacing != facing:
                walker.currentFacing = facing;
                walker.BeginWalking(facing);
                break;
            case Walker.States.Stopped when walker.state != state || walker.stopReason != reason:
                walker.BeginStopped(reason);
                break;
            case Walker.States.Turning when walker.state != state:
                walker.BeginTurning(turning);
                break;
        }

        walker.currentFacing = facing;
        walker.turningFacing = turning;
        walker.walkTimeRemaining = reader.ReadSingle();
        walker.pauseTimeRemaining = reader.ReadSingle();
        walker.turnCooldownRemaining = reader.ReadSingle();
        walker.aggroEdgeTurnCooldownRemaining = reader.ReadSingle();
        Save.SharedDice.SetWalks(creature, reader.ReadInt32());
    }
}
