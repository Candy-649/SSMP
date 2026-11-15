using System;
using System.Collections.Generic;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// The creatures that every game runs by itself as the room's own, with no copy made of them, of the kinds that the
/// registry says so of (<see cref="EntityRegistryEntry.OwnInEachGame"/>). The game runs each one in every game as it
/// runs it for a player alone, and only what decides it is shared: what it rolls, by its place in the room (see
/// SharedDice), the hits it takes, as the room's own objects take them (see CoopHits.IsRoomObject), and the state that
/// the scene host's game has it in (see CoopSave.OwnCreatures).
///
/// Begun as a trial of keeping to the game's own creature and sending what is listed, rather than showing a copy of
/// it whose every state machine the mod has to teach (USER 11-14, "用游戏本身那只，更像是一种白名单",
/// "先微微试一下"). A kind whose copy goes wrong comes over to this rather than the copy being taught more (USER 11-15
/// "再也不想干复制品这种蠢事了").
///
/// The rocks that a boss's fight lets fall are run so too, though they have no health. The game keeps them hidden
/// where they hang until the fight calls them down; a copy never ran the step that hides them, and hung in the air
/// in every player's sight, to be struck and stood on (USER 11-15 "这个石头停在半空中是正常的吗",
/// "我这边石头也停在半空"). Which ones fall is the fight's own roll, the same in every game, and each one's steps
/// are the scene host's as a creature's are.
///
/// So is a boss, the town's, for the one fight of it that there is to try it in (USER 11-15 "如果这次没问题了，那么就
/// 再也不会打到这个怪了，所以这就是最好的测试时间"). The scene host's game decides every way that goes by where the
/// players are, and whom it goes after; every other game plays each move that comes from there itself, with the
/// same dice, rather than deciding it (USER 11-15 "总之以房主那边传过来的招式为单位。尽量需要的随机数就一起过来").
/// </summary>
internal static class OwnCreatures {
    /// <summary>
    /// The creatures of the rooms loaded now, with their scenes and their places in them as the rooms loaded them.
    /// </summary>
    private static readonly Dictionary<GameObject, (string Scene, string Place)> Creatures = new();

    /// <summary>
    /// How many there are.
    /// </summary>
    public static int Count => Creatures.Count;

    /// <summary>
    /// The creatures of the rooms loaded now, with their scenes and places as the rooms loaded them, including those
    /// that died since.
    /// </summary>
    public static IEnumerable<KeyValuePair<GameObject, (string Scene, string Place)>> All => Creatures;

    /// <summary>
    /// The creatures whose death this game has told or taken, the boss whose death the game leaves to its state
    /// machines among them, which takes no blow as its last and dies more than once.
    /// </summary>
    private static readonly HashSet<GameObject> Dead = [];

    /// <summary>
    /// Whether this game is the scene host's, null while that is not settled yet; set by the ClientManager.
    /// </summary>
    public static Func<bool?>? SceneRole { get; set; }

    /// <summary>
    /// The state machine that goes into a step of the scene host's creature now, and the state that the step goes to
    /// (see CoopSave.OnOwnCreature). Nothing here holds back the way into it; what the state does once it is in goes
    /// as anything else of the creature here does.
    /// </summary>
    public static (HutongGames.PlayMaker.Fsm Fsm, string State)? SceneHostsStep { get; set; }

    /// <summary>
    /// Whether a state machine goes into a step of the scene host's creature now (see <see cref="SceneHostsStep"/>).
    /// </summary>
    public static bool PlaysSceneHostsStep(HutongGames.PlayMaker.Fsm fsm) {
        return SceneHostsStep is { } step && step.Fsm == fsm;
    }

    /// <summary>
    /// Takes a creature of the room as one that every game runs by itself.
    /// </summary>
    /// <param name="creature">The creature.</param>
    /// <param name="type">Its kind.</param>
    public static void Take(GameObject creature, EntityType type) {
        if (Creatures.ContainsKey(creature)) {
            return;
        }

        var place = ScenePath.Get(creature.transform);
        Creatures[creature] = (creature.scene.name, place);
        Logger.Info($"[Own creatures] '{place}' ({type}) runs in each game as the room's own, with no copy");
    }

    /// <summary>
    /// Whether an object is a creature that every game runs by itself.
    /// </summary>
    public static bool Has(GameObject gameObject) {
        return Creatures.Count > 0 && Creatures.ContainsKey(gameObject);
    }

    /// <summary>
    /// The creature that every game runs by itself that an object is, or is a part of, or null.
    /// </summary>
    public static GameObject? Owning(GameObject part) {
        if (Creatures.Count == 0) {
            return null;
        }

        for (var current = part.transform; current != null; current = current.parent) {
            if (Creatures.ContainsKey(current.gameObject)) {
                return current.gameObject;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an object is, or is a part of, a creature that every game runs by itself, in the game of the scene host,
    /// which decides for every game whom it goes after and where it goes by where the players are.
    /// </summary>
    public static bool RunsHere(GameObject part) {
        return Creatures.Count > 0 && SceneRole?.Invoke() == true && Owning(part) != null;
    }

    /// <summary>
    /// Whether an object is, or is a part of, a creature that every game runs by itself, in a game that follows the
    /// scene host's: it goes after whom the scene host's goes after, and takes the steps that go by where the players
    /// are from there.
    /// </summary>
    public static bool FollowsSceneHost(GameObject part) {
        return Creatures.Count > 0 && SceneRole?.Invoke() == false && Owning(part) != null;
    }

    /// <summary>
    /// Notes the death of a creature that every game runs by itself, which this game tells or takes once, returning
    /// whether it is the first.
    /// </summary>
    public static bool MarkDead(GameObject creature) {
        return Dead.Add(creature);
    }

    /// <summary>
    /// The creature at a place of a scene as the room loaded it, if it is one that every game runs by itself.
    /// </summary>
    public static GameObject? Find(string scene, string place) {
        foreach (var pair in Creatures) {
            if (pair.Key != null && pair.Value.Scene == scene && pair.Value.Place == place) {
                return pair.Key;
            }
        }

        return null;
    }

    /// <summary>
    /// The place of a creature as the room loaded it.
    /// </summary>
    public static string PlaceOf(GameObject creature) {
        return Creatures[creature].Place;
    }

    /// <summary>
    /// The name that a creature rolls under, the same in every game (see SharedDice.RollByName).
    /// </summary>
    public static string RollName(GameObject creature) {
        return "Own " + Creatures[creature].Place;
    }

    /// <summary>
    /// Has the death of a creature that every game runs by itself go into the journal of the local player, as the game
    /// has it when a blow of the player's struck it (HealthManager.TakeDamage, by the AwardJournalKill of the
    /// DamageEnemies that struck). A blow of another player is replayed here by a stand-in that is no attack of the
    /// player's, and a death that another game told comes with no blow at all: the creature went into the journal of
    /// the player who killed it only, where a copy of it had gone into both (USER 11-14 "我打的只算我的，她打的只算她的，
    /// 感觉还是需要同步，不然的话沿用到一些只能打一次的怪上会有问题").
    /// </summary>
    public static void AwardJournalKill(HealthManager health) {
        health.WillAwardJournalKill = true;
    }

    /// <summary>
    /// Forgets the creatures, as the room is left.
    /// </summary>
    public static void Clear() {
        Creatures.Clear();
        Dead.Clear();
    }
}
