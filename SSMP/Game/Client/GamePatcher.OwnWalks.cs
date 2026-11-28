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
/// off afresh, and both walk alike from there, set straight at the steps that come. Where it walks by a player, it does
/// so by the scene host's game (USER 11-16 "总之不看人的优先采用两边同种子掷骰子然后轻微矫正的方式，看人的就房主传过来招式"):
/// it turns to one it sees behind it there alone, which tells the others its walk as it turns (see
/// <see cref="TurnToFace"/>), and walks on in every game while the scene host's sees one in its range and its sight,
/// which that game tells as it comes to see one and no longer does (see <see cref="SeesToWalkOn"/>,
/// <see cref="SightAsItWalks"/>).
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
    /// The Walkers of creatures that every game runs by itself whose creature sees a player in its range in the scene
    /// host's game, as that game told this one, which follows it (see <see cref="TakeWalkWord"/>).
    /// </summary>
    private static readonly HashSet<Walker> SeenThere = [];

    /// <summary>
    /// What the Walker of each creature that every game runs by itself saw when this game, the scene host's, last told
    /// the members in the room, a player in its range and its sight or none (see <see cref="NoteSight"/>). One that is
    /// not here is told at its next look, whatever it sees then: one never told, or told by another game before this
    /// one came to run the room (see <see cref="RetellOwnWalks"/>).
    /// </summary>
    private static readonly Dictionary<Walker, bool> SightTold = [];

    /// <summary>
    /// Tells the members in the room where the walk of a creature that every game runs by itself is in the scene host's
    /// game, this one, as the creature comes to see a player in its range or no longer does, and as it turns to face
    /// one, given the creature, whether it sees one and whether it turned (see CoopSave.TellOwnWalk). Whether the word
    /// went to any member. Given by the code of the saves.
    /// </summary>
    public static Func<GameObject, bool, bool, bool>? TellsWalk { get; set; }

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
    /// IL hook for a method of the Walker that looks at whether a player stands in its range and in its sight. As it
    /// walks (UpdateWalking), the answer of the look at its range by which it turns to face a player behind it, the
    /// first, goes through <see cref="SeesToFace"/>, and the turn that it begins then through
    /// <see cref="TurnToFace"/>; that of the other, by which it walks on while it sees one, through
    /// <see cref="SeesToWalkOn"/>; and those of its sight, which it looks at before its range, through
    /// <see cref="SightAsItWalks"/>. As it turns (Update), by which it holds off its next turn at an edge while it
    /// sees one, through <see cref="SeesAsItTurns"/> and <see cref="SightAsItTurns"/>.
    /// </summary>
    private static void ILOwnWalkLooks(ILContext il, string method) {
        try {
            var walking = method == "UpdateWalking";
            Func<bool, Walker, bool> sight = walking ? SightAsItWalks : SightAsItTurns;
            var c = new ILCursor(il);
            var sights = 0;
            while (c.TryGotoNext(
                       MoveType.After, i => i.MatchCallvirt(typeof(LineOfSightDetector), "get_CanSeeHero")
                   )) {
                c.Emit(OpCodes.Ldarg_0);
                c.EmitDelegate(sight);
                sights++;
            }

            c.Index = 0;
            var looks = 0;
            while (c.TryGotoNext(
                       MoveType.After, i => i.MatchCallvirt(typeof(AlertRange), nameof(AlertRange.IsHeroInRange))
                   )) {
                c.Emit(OpCodes.Ldarg_0);
                if (!walking) {
                    c.EmitDelegate<Func<bool, Walker, bool>>(SeesAsItTurns);
                } else if (looks > 0) {
                    c.EmitDelegate<Func<bool, Walker, bool>>(SeesToWalkOn);
                } else {
                    c.EmitDelegate<Func<bool, Walker, bool>>(SeesToFace);
                    if (c.TryGotoNext(i => i.MatchCall(typeof(Walker), nameof(Walker.BeginTurning)))) {
                        c.Remove();
                        c.EmitDelegate<Action<Walker, int>>(TurnToFace);
                    } else {
                        Logger.Warn("Walker#UpdateWalking no longer turns where it looks at its range");
                    }
                }

                looks++;
            }

            if (looks == 0 || sights == 0) {
                Logger.Warn($"Walker#{method} no longer looks at its range and its sight where they were looked for");
            }
        } catch (Exception e) {
            Logger.Error($"Could not change Walker#{method} IL:\n{e}");
        }
    }

    /// <summary>
    /// Whether a player stands in the range of a Walker, for its turn to face one that it sees behind it as it walks.
    /// Never in a game that follows the scene host's, for a creature that every game runs by itself: the scene host's
    /// sees the player, and tells its turn as it begins (see <see cref="TurnToFace"/>), and its state machine looks
    /// through the same sight and goes after them at once (the pilgrims' Walk stores CheckCanSeeHero and goes on
    /// ATTACK by a BoolTest), by a step that the others are told. One that looked by itself turned by where it had the
    /// players, a moment late and a little elsewhere, and rolled its walk otherwise from then on, as the scene host's
    /// did not (review).
    /// </summary>
    private static bool SeesToFace(bool seen, Walker walker) {
        if (Entity.OwnCreatures.FollowsSceneHost(walker.gameObject)) {
            return false;
        }

        NoteSight(walker, seen);
        return seen;
    }

    /// <summary>
    /// Whether a player stands in the range of a Walker, for its walking on while it sees one rather than standing a
    /// while. In a game that follows the scene host's, for a creature that every game runs by itself, whether the scene
    /// host's creature sees one there, as that game told (see <see cref="TakeWalkWord"/>): one that saw none here
    /// stood a while by itself as the scene host's walked on.
    /// </summary>
    private static bool SeesToWalkOn(bool seen, Walker walker) {
        if (Entity.OwnCreatures.FollowsSceneHost(walker.gameObject)) {
            return SeenThere.Contains(walker);
        }

        NoteSight(walker, seen);
        return seen;
    }

    /// <summary>
    /// Whether a Walker sees a player through its line of sight as it walks, which it looks at before its range. In a
    /// game that follows the scene host's, for a creature that every game runs by itself, it does: whether it sees one
    /// there is the scene host's word, which the look at its range that comes next takes (see
    /// <see cref="SeesToWalkOn"/>), and one that lost sight here of a player whom the scene host's still saw stood a
    /// while by itself as that one walked on. In the scene host's game, one that sees no one so sees no one in its
    /// range either, which is told as it comes (see <see cref="NoteSight"/>): it does not look at its range then, and
    /// the others walked on by the word that it saw one as it stood a while there.
    /// </summary>
    private static bool SightAsItWalks(bool canSee, Walker walker) {
        if (Entity.OwnCreatures.FollowsSceneHost(walker.gameObject)) {
            return true;
        }

        if (!canSee) {
            NoteSight(walker, false);
        }

        return canSee;
    }

    /// <summary>
    /// Whether a player stands in the range of a Walker as it turns, by which it holds off its next turn at an edge
    /// while it sees one. In a game that follows the scene host's, for a creature that every game runs by itself,
    /// whether the scene host's creature sees one there, as that game told as it walked (see
    /// <see cref="SeesToWalkOn"/>). That game tells nothing of what it sees as it turns, as it looks at its range
    /// before its sight then: it would tell that it sees one and that it does not at every frame of the turn while a
    /// player stood in its range out of its sight.
    /// </summary>
    private static bool SeesAsItTurns(bool seen, Walker walker) {
        return Entity.OwnCreatures.FollowsSceneHost(walker.gameObject) ? SeenThere.Contains(walker) : seen;
    }

    /// <summary>
    /// Whether a Walker sees a player through its line of sight as it turns, after its range (see
    /// <see cref="SeesAsItTurns"/>). In a game that follows the scene host's, for a creature that every game runs by
    /// itself, it does: the word of the scene host's game, which the look at its range took, says it all.
    /// </summary>
    private static bool SightAsItTurns(bool canSee, Walker walker) {
        return Entity.OwnCreatures.FollowsSceneHost(walker.gameObject) || canSee;
    }

    /// <summary>
    /// Notes what the Walker of a creature that every game runs by itself sees in the scene host's game, this one, and
    /// tells the members in the room as it comes to see a player or no longer does (see <see cref="TellsWalk"/>), or
    /// whatever it sees at its first look since they were last told (see <see cref="SightTold"/>), but for a creature
    /// that walks for no other game, whose sight is told to the first who comes in as it looks next.
    /// </summary>
    private static void NoteSight(Walker walker, bool seen) {
        if (TellsWalk == null || SightTold.TryGetValue(walker, out var told) && told == seen ||
            Entity.OwnCreatures.Owning(walker.gameObject) is not { } creature || !TellsWalk(creature, seen, false)) {
            return;
        }

        SightTold[walker] = seen;
    }

    /// <summary>
    /// Begins the turn of a Walker to face a player that it saw behind it, as the game does (Walker.BeginTurning), and
    /// tells the members in the room where the walk of a creature that every game runs by itself is as the turn
    /// begins in the scene host's game, this one: no other game turns it by itself (see <see cref="SeesToFace"/>), and
    /// one that did not have it went on walking away from the player that the scene host's turned to, 17 units by the
    /// scene host's next step, where it was set (the user's log of 11-28, 16:44:08.735, the keeper of the roaches,
    /// which walked to x 232.3 as Luminary's turned at 223.6; USER "这个怪会凭空闪现"). A turn that the game does not
    /// begin, with no ground below the creature, tells nothing: the creature tries it again at every frame (review of
    /// 06b0a8e).
    /// </summary>
    private static void TurnToFace(Walker walker, int facing) {
        var was = (walker.state, walker.currentFacing);
        walker.BeginTurning(facing);
        if ((walker.state, walker.currentFacing) != was && TellsWalk != null &&
            Entity.OwnCreatures.Owning(walker.gameObject) is { } creature) {
            TellsWalk(creature, true, true);
        }
    }

    /// <summary>
    /// Takes the word of the scene host's game of where the walk of a creature that every game runs by itself is there,
    /// as its creature comes to see a player in its range or no longer does, or turns to face one (see
    /// <see cref="TellsWalk"/>): whether it sees one goes for its walking on here (see <see cref="SeesToWalkOn"/>), and
    /// its walk is put where the scene host's is (see <see cref="PutWalk"/>) while it walks here too, rather than while
    /// its state machine holds it in a move, which the step of the scene host's that the move ends at sets straight.
    /// </summary>
    /// <param name="creature">The creature.</param>
    /// <param name="sees">Whether it sees a player in the scene host's game.</param>
    /// <param name="turned">Whether it turned there to face one.</param>
    /// <param name="reader">Where its walk is there (see <see cref="WriteWalk"/>).</param>
    /// <param name="from">Whose game told it, for the log.</param>
    internal static void TakeWalkWord(GameObject creature, bool sees, bool turned, BinaryReader reader, string from) {
        if (!creature.TryGetComponent<Walker>(out var walker)) {
            return;
        }

        if (sees) {
            SeenThere.Add(walker);
        } else {
            SeenThere.Remove(walker);
        }

        var walks = walker.isActiveAndEnabled &&
                    (walker.state is Walker.States.Walking or Walker.States.Turning ||
                     walker.state == Walker.States.Stopped && walker.stopReason == Walker.StopReasons.Bored);
        if (!turned || !walks) {
            Logger.Info(
                $"[Own creatures] '{Entity.OwnCreatures.PlaceOf(creature)}' " +
                (turned ? "turns to face a player" : sees ? "sees a player" : "no longer sees a player") +
                $" in {from}'s game as it walks there" + (walks ? "" : ", and does not walk here")
            );
        }

        if (walks) {
            PutWalk(reader, creature);
        }
    }

    /// <summary>
    /// Has what the Walkers of the creatures here that every game runs by itself see in this game, the scene host's,
    /// told again to the members in the room at their next look (see <see cref="NoteSight"/>): as a member comes in,
    /// who was told nothing of it before, and as this game comes to run the room, whose members went by the word of the
    /// game that ran it before, which is not told that it no longer does: one that it told saw a player walked on in
    /// their games for good as this one's stood a while, which saw none (review of 06b0a8e).
    /// </summary>
    internal static void RetellOwnWalks() {
        SightTold.Clear();
    }

    /// <summary>
    /// Forgets what the Walkers of the creatures of the room that every game runs by itself see in the scene host's
    /// game, as told or as told by this one, as the creatures of the room are forgotten (see OwnCreatures.Clear).
    /// </summary>
    internal static void ForgetOwnWalks() {
        SeenThere.Clear();
        SightTold.Clear();
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
        string? how = null;
        switch (state) {
            case Walker.States.Walking when walker.state != state || walker.currentFacing != facing:
                how = "sets off walking";
                walker.currentFacing = facing;
                walker.BeginWalking(facing);
                break;
            case Walker.States.Stopped when walker.state != state || walker.stopReason != reason:
                how = reason == Walker.StopReasons.Bored ? "stands a while" : "stops";
                walker.BeginStopped(reason);
                break;
            case Walker.States.Turning when walker.state != state:
                how = "begins to turn";
                walker.BeginTurning(turning);
                break;
        }

        walker.currentFacing = facing;
        walker.turningFacing = turning;
        walker.walkTimeRemaining = reader.ReadSingle();
        walker.pauseTimeRemaining = reader.ReadSingle();
        walker.turnCooldownRemaining = reader.ReadSingle();
        walker.aggroEdgeTurnCooldownRemaining = reader.ReadSingle();
        var walks = reader.ReadInt32();
        if (how != null || Save.SharedDice.WalksOf(creature) != walks) {
            Logger.Info(
                $"[Own creatures] '{Entity.OwnCreatures.PlaceOf(creature)}' {how ?? "walks on"} as its walk goes in " +
                $"the scene host's game, from number {walks} of its walk"
            );
        }

        Save.SharedDice.SetWalks(creature, walks);
    }
}
