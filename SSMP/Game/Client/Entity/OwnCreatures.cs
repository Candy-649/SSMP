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
///
/// And so is the boss at the end of the second act, for the one fight of it before the third (USER 11-17 "话说今天可能
/// 会打丝母，打完进第三幕就打不了丝母了，要不然你把丝母也本地化了（"). Its hands and the blades on them are parts with state
/// machines of their own, which leave it as the fight wakes them and aim at the player it goes after: each part rolls
/// under its own place in the room as the creature does, goes after whom the creature goes after, and takes the steps
/// by which it looks at a player from the scene host's game as the creature does (see CoopSave.OwnCreatures).
/// </summary>
internal static class OwnCreatures {
    /// <summary>
    /// The creatures of the rooms loaded now, with their scenes and their places in them as the rooms loaded them.
    /// </summary>
    private static readonly Dictionary<GameObject, (string Scene, string Place)> Creatures = new();

    /// <summary>
    /// The parts of the creatures that have state machines of their own, with the creature that each is of, its scene
    /// and its place in the room, as the room loaded them: the silk boss's hands leave it as the fight wakes them, with
    /// the blades on them, and hang in the room by themselves after.
    /// </summary>
    private static readonly Dictionary<GameObject, (GameObject Creature, string Scene, string Place)> Parts = new();

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
    /// A creature that takes a step that another game took, with a stand-in for the player that it goes after, put
    /// where that game had them beside it as it took the step (see CoopSave.OwnCreatures). As the step starts, the
    /// creature's looks at where that player is, its turn to them, a distance, an aim, look at the stand-in (see
    /// GamePatcher.EnemyTargeting): a judgment by where the players are is the scene host's game's, and each game has
    /// the players a little elsewhere. Set into its stun, the knight turned to the player that it went after and
    /// sprang off backwards; where Luminary's game had the user on its other side, it sprang off the other way
    /// (Luminary's log of 11-19, 20:39:14.575; USER "确定这个瘫痪的函数没有什么我们没能接管的看玩家位置的变量？").
    /// </summary>
    public static (GameObject Creature, GameObject There)? TargetThere { get; private set; }

    /// <summary>
    /// The stand-in of <see cref="TargetThere"/>, made once.
    /// </summary>
    private static GameObject? _targetThere;

    /// <summary>
    /// Has a creature look at a place for the player that it goes after, as it starts a step that another game took
    /// (see <see cref="TargetThere"/>), until <see cref="SeeTargetHere"/>.
    /// </summary>
    /// <param name="creature">The creature.</param>
    /// <param name="place">Where that game had the player.</param>
    public static void SeeTargetThere(GameObject creature, Vector3 place) {
        if (_targetThere == null) {
            _targetThere = new GameObject("Where another game had the player");
            UnityEngine.Object.DontDestroyOnLoad(_targetThere);
        }

        _targetThere.transform.position = place;
        TargetThere = (creature, _targetThere);
    }

    /// <summary>
    /// Has the creature that looked at a place for the player that it goes after look at that player again (see
    /// <see cref="SeeTargetThere"/>).
    /// </summary>
    public static void SeeTargetHere() {
        TargetThere = null;
    }

    /// <summary>
    /// For a creature that died as another game told, where that game had the player that it went after, beside where
    /// it died (see <see cref="SeeTargetAtDeath"/>).
    /// </summary>
    private static readonly Dictionary<GameObject, Vector3> DeathLooks = new();

    /// <summary>
    /// The stand-in of <see cref="DeathLooks"/>, made once.
    /// </summary>
    private static GameObject? _deathThere;

    /// <summary>
    /// Has the first look at the player of the death of a creature that died as another game told, its own or a
    /// part's, look where that game had the player beside it (see GamePatcher.EnemyTargeting), as a step that another
    /// game took looks as it starts (see <see cref="TargetThere"/>), but whenever that look comes: the knight's body
    /// waits a frame, then turns to the player that the knight went after and falls away from them. Each game has the
    /// players a little elsewhere, and where one stood by the creature, it fell the other way in the other game (USER
    /// 11-20 "可是我们两边boss向相反的方向弹出去了", "应该和击退那次是差不多的问题").
    /// </summary>
    /// <param name="creature">The creature.</param>
    /// <param name="place">Where that game had the player.</param>
    public static void SeeTargetAtDeath(GameObject creature, Vector3 place) {
        DeathLooks[creature] = place;
    }

    /// <summary>
    /// The stand-in where the death of the creature that an object is, or is a part of, looks at the player first, if
    /// it died as another game told and has not looked yet (see <see cref="SeeTargetAtDeath"/>).
    /// </summary>
    /// <param name="looker">The object that looks.</param>
    public static GameObject? DeathLookOf(GameObject looker) {
        if (DeathLooks.Count == 0 || Owning(looker) is not { } creature ||
            !DeathLooks.TryGetValue(creature, out var place)) {
            return null;
        }

        if (_deathThere == null) {
            _deathThere = new GameObject("Where another game had the player at a death");
            UnityEngine.Object.DontDestroyOnLoad(_deathThere);
        }

        _deathThere.transform.position = place;
        return _deathThere;
    }

    /// <summary>
    /// Has the death of the creature that an object is, or is a part of, look at the player where they are, once it has
    /// looked where the game that told of it had them (see <see cref="SeeTargetAtDeath"/>).
    /// </summary>
    /// <param name="looker">The object that looked.</param>
    public static void LookedAtDeath(GameObject looker) {
        if (Owning(looker) is { } creature) {
            DeathLooks.Remove(creature);
        }
    }

    /// <summary>
    /// The state machine of a creature that every game runs by itself that a talk of the local player tells its event
    /// now, as a character tells its FSM about a talk (see Entity.TalkHere and CoopSave.OwnTalks).
    /// </summary>
    public static HutongGames.PlayMaker.Fsm? LocalTalk { get; set; }

    /// <summary>
    /// Whether a state machine of a creature that every game runs by itself plays a catch of another player in a
    /// state, with what it does to the player caught kept off the local player (see CoopSave.OwnCatches); what it
    /// switches on of the room there keeps off the local player too (see Entity.OnAnyActivateGameObject).
    /// </summary>
    public static Func<HutongGames.PlayMaker.Fsm, HutongGames.PlayMaker.FsmState, bool>? KeepsLocalPlayerOut {
        get;
        set;
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

        // A talk with it goes through the hook on how a talking character tells its FSM about a talk, which a room with
        // no copy in it never put in place
        Entity.HookTalks();
        var place = ScenePath.Get(creature.transform);
        Creatures[creature] = (creature.scene.name, place);

        // Its parts with state machines of their own, while they still hang under it
        var parts = 0;
        foreach (var machine in creature.GetComponentsInChildren<PlayMakerFSM>(true)) {
            var part = machine.gameObject;
            if (part != creature && !Parts.ContainsKey(part)) {
                Parts[part] = (creature, creature.scene.name, ScenePath.Get(part.transform));
                parts++;
            }
        }

        Logger.Info(
            $"[Own creatures] '{place}' ({type}) runs in each game as the room's own, with no copy, and {parts} " +
            "part(s) with state machines of their own"
        );
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

        // A part that left the creature is its part still
        if (Parts.TryGetValue(part, out var of)) {
            return of.Creature;
        }

        for (var current = part.transform; current != null; current = current.parent) {
            if (Creatures.ContainsKey(current.gameObject)) {
                return current.gameObject;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an object is a part with state machines of its own of a creature that every game runs by itself (see
    /// <see cref="Parts"/>).
    /// </summary>
    public static bool IsPart(GameObject gameObject) {
        return Parts.Count > 0 && Parts.ContainsKey(gameObject);
    }

    /// <summary>
    /// Whether an object rolls under its place in the room, the same in every game (see <see cref="RollName"/>): a
    /// creature that every game runs by itself, or a part of one with state machines of its own.
    /// </summary>
    public static bool RollsUnderPlace(GameObject gameObject) {
        return Has(gameObject) || IsPart(gameObject);
    }

    /// <summary>
    /// The parts with state machines of their own of the creatures of the rooms loaded now (see <see cref="Parts"/>),
    /// with the creature that each is of.
    /// </summary>
    public static IEnumerable<(GameObject Part, GameObject Creature)> AllParts() {
        foreach (var pair in Parts) {
            yield return (pair.Key, pair.Value.Creature);
        }
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
    /// The creature at a place of a scene as the room loaded it, if it is one that every game runs by itself, or the
    /// part with state machines of its own of one there.
    /// </summary>
    public static GameObject? Find(string scene, string place) {
        foreach (var pair in Creatures) {
            if (pair.Key != null && pair.Value.Scene == scene && pair.Value.Place == place) {
                return pair.Key;
            }
        }

        foreach (var pair in Parts) {
            if (pair.Key != null && pair.Value.Scene == scene && pair.Value.Place == place) {
                return pair.Key;
            }
        }

        return null;
    }

    /// <summary>
    /// The place of a creature, or of a part of one with state machines of its own, as the room loaded it.
    /// </summary>
    public static string PlaceOf(GameObject creature) {
        return Creatures.TryGetValue(creature, out var at) ? at.Place : Parts[creature].Place;
    }

    /// <summary>
    /// The name that a creature, or a part of one with state machines of its own, rolls under, the same in every game
    /// (see SharedDice.RollByName).
    /// </summary>
    public static string RollName(GameObject creature) {
        return "Own " + PlaceOf(creature);
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
    /// Forgets the creatures, as the room is left, with what is known of what the third act does to them.
    /// </summary>
    public static void Clear() {
        Creatures.Clear();
        Parts.Clear();
        Dead.Clear();
        DeathLooks.Clear();
        OwnThreads.Clear();
    }
}
