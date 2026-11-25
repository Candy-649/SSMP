using System;
using System.Collections.Generic;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Save;
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
    /// The state machines of the creatures, and of their parts, that each game runs by itself for its own player alone
    /// (see EntityRegistryEntry.EachGameFsms), like the one that feels the player that the creature grabs: no game
    /// takes or tells the steps of another's (see CoopSave.OnOwnCreatureStep).
    /// </summary>
    private static readonly HashSet<PlayMakerFSM> EachGame = [];

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
    /// How many deaths of each creature this game played, its own and those that it was told, which numbers the word
    /// of each death (see <see cref="CountDeath"/>).
    /// </summary>
    private static readonly Dictionary<GameObject, ulong> Deaths = new();

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
    /// Whether the health of a creature that every game runs by itself came to none in the scene host's game, as it
    /// told this game, which follows it, and was given no more here since (see CoopSave.OwnHealth); set by the
    /// CoopSave.
    /// </summary>
    public static Func<HealthManager, bool>? HasNoHealthAsTold { get; set; }

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

        var place = ScenePath.Get(creature.transform);
        var parts = TakeAt(creature, type, place, part => ScenePath.Get(part.transform));
        Logger.Info(
            $"[Own creatures] '{place}' ({type}) runs in each game as the room's own, with no copy, and {parts} " +
            "part(s) with state machines of their own"
        );
    }

    /// <summary>
    /// What starts the place of a summon: a creature that a creature that every game runs by itself made as it ran,
    /// which every game makes by itself and names alike (see <see cref="TakeSummon"/>), rather than a place in the
    /// room, where each game has its own made one at another place among the others.
    /// </summary>
    private const char SummonStart = '%';

    /// <summary>
    /// What stands between the place of a summon and the path of an object of it under it.
    /// </summary>
    private const char SummonPartStart = '|';

    /// <summary>
    /// What made a summon (see <see cref="TakeSummon"/>): the place of the creature that made it, and the state machine
    /// and state of the action that made it, with the number of that action in the state, by which the scene host's
    /// game has a member who comes in make it too (see CoopSave.OwnSummons).
    /// </summary>
    public readonly record struct SummonMaker(string Place, string Fsm, string State, int Action);

    /// <summary>
    /// What made each summon here (see <see cref="SummonMaker"/>).
    /// </summary>
    private static readonly Dictionary<GameObject, SummonMaker> Makers = new();

    /// <summary>
    /// The creature that made each summon here.
    /// </summary>
    private static readonly Dictionary<GameObject, GameObject> SummonedBy = new();

    /// <summary>
    /// Whether two creatures that every game runs by itself are one, or one of them made the other as its summon (see
    /// <see cref="TakeSummon"/>): what one of them tells the other is the creature's own, as what its parts tell one
    /// another (see CoopSave.OwnTells).
    /// </summary>
    public static bool AreKin(GameObject? one, GameObject? other) {
        return one == other || one != null && other != null &&
            (SummonedBy.TryGetValue(one, out var oneBy) && oneBy == other ||
             SummonedBy.TryGetValue(other, out var otherBy) && otherBy == one);
    }

    /// <summary>
    /// Makes what an action of a creature that every game runs by itself, or of a part of one, makes as it starts,
    /// where that is a creature of a kind that every game runs by itself, as a summon of it (see
    /// <see cref="TakeSummon"/>), the way the action makes it (CreateObject.OnEnter): every game makes its own as its
    /// creature runs, under the name that the action gives it there. One of that name that is here already, made at
    /// the word of the scene host's game for the local player, who came in after it was made there (see
    /// CoopSave.OwnSummons), is the one that the action makes. Before, the scene host's game made it alone and every
    /// other showed a copy of it (see RoomCreatures). Whether the action is such a one.
    /// </summary>
    public static bool MakesSummon(CreateObject action) {
        if (Creatures.Count == 0 || action.Fsm?.FsmComponent is not { } machine || machine == null ||
            !RollsUnderPlace(machine.gameObject) || action.gameObject.Value is not { } prefab || prefab == null ||
            !EntityRegistry.TryGetEntry(prefab, out var entry) || !entry.OwnInEachGame) {
            return false;
        }

        var maker = machine.gameObject;
        SharedDice.RollByName(maker, RollName(maker));
        var key = SharedDice.NextOwnThrowKey(action)!;
        var summon = Find(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name, SummonStart + key);
        if (summon != null) {
            Logger.Info(
                $"[Own creatures] '{PlaceOf(maker)}' makes '{SummonStart + key}' in '{action.State.Name}' of " +
                $"'{machine.FsmName}', which is here already"
            );
        } else {
            summon = MakeAs(action, prefab);
            TakeSummon(summon, entry.Type, key, maker, action);
        }

        action.storeObject.Value = summon;
        action.Finish();
        return true;
    }

    /// <summary>
    /// Makes a summon that the scene host's game made before the local player came in, as the action that made it there
    /// makes it (see <see cref="MakesSummon"/>), and takes it at its place there.
    /// </summary>
    /// <param name="place">Its place there.</param>
    /// <param name="maker">What made it there.</param>
    /// <returns>The summon, or null where its maker or the action is not here.</returns>
    public static GameObject? MakeSummonAsTold(string place, SummonMaker maker) {
        if (Find(null, maker.Place) is not { } by ||
            PlayMakerFSM.FindFsmOnGameObject(by, maker.Fsm) is not { } machine || machine == null ||
            machine.Fsm.GetState(maker.State) is not { } state || maker.Action >= state.Actions.Length ||
            state.Actions[maker.Action] is not CreateObject action || action.gameObject.Value is not { } prefab ||
            !EntityRegistry.TryGetEntry(prefab, out var entry)) {
            return null;
        }

        var summon = MakeAs(action, prefab);
        TakeSummon(summon, entry.Type, place.Substring(1), by, action);
        return summon;
    }

    /// <summary>
    /// What made a summon here, or null for a creature that is none (see <see cref="Makers"/>).
    /// </summary>
    public static SummonMaker? MakerOf(GameObject creature) {
        return Makers.TryGetValue(creature, out var maker) ? maker : null;
    }

    /// <summary>
    /// Makes what an action that makes an object makes, as the action does (CreateObject.OnEnter): at its spawn point,
    /// moved by its position and turned as it is or by its rotation, or else at its position and rotation.
    /// </summary>
    private static GameObject MakeAs(CreateObject action, GameObject prefab) {
        var position = Vector3.zero;
        var angles = Vector3.zero;
        if (action.spawnPoint.Value is { } point && point != null) {
            position = point.transform.position;
            if (!action.position.IsNone) {
                position += action.position.Value;
            }

            angles = action.rotation.IsNone ? point.transform.eulerAngles : action.rotation.Value;
        } else {
            if (!action.position.IsNone) {
                position = action.position.Value;
            }

            if (!action.rotation.IsNone) {
                angles = action.rotation.Value;
            }
        }

        return UnityEngine.Object.Instantiate(prefab, position, Quaternion.Euler(angles));
    }

    /// <summary>
    /// Takes a creature that a creature that every game runs by itself made as it ran, its summon, as one that every
    /// game runs by itself too, under the name that the action that made it gave it, the same in every game (see
    /// SharedDice.NextOwnThrowKey): each game makes its own, which rolls under that name, and whose parts are named
    /// after it (USER 11-25 "各生各的，不看人的部分投骰子，看人的部分听房主").
    /// </summary>
    /// <param name="creature">The summon.</param>
    /// <param name="type">Its kind.</param>
    /// <param name="key">The name that the action that made it gave it.</param>
    /// <param name="maker">The creature, or part of one, whose action made it.</param>
    /// <param name="action">The action.</param>
    private static void TakeSummon(
        GameObject creature,
        EntityType type,
        string key,
        GameObject maker,
        CreateObject action
    ) {
        var place = SummonStart + key;
        var parts = TakeAt(
            creature, type, place,
            part => place + SummonPartStart + Component.PartsComponent.GetPath(creature.transform, part.transform)
        );
        var made = new SummonMaker(
            PlaceOf(maker), action.Fsm.FsmComponent.FsmName, action.State.Name,
            Array.IndexOf(action.State.Actions, action)
        );
        Makers[creature] = made;
        SummonedBy[creature] = Owning(maker) ?? maker;
        Logger.Info(
            $"[Own creatures] '{place}' ({type}), which '{made.Place}' made here in '{made.State}' of '{made.Fsm}', " +
            $"runs in each game as the room's own, made by each, with {parts} part(s) with state machines of their own"
        );
    }

    /// <summary>
    /// Takes a creature as one that every game runs by itself at a place (see <see cref="Take"/>), with its parts with
    /// state machines of their own, while they still hang under it, at theirs, and the state machines that each game
    /// runs by itself. A talk with it goes through the hook on how a talking character tells its FSM about a talk,
    /// which a room with no copy in it never put in place.
    /// </summary>
    /// <returns>How many parts it has.</returns>
    private static int TakeAt(GameObject creature, EntityType type, string place, Func<GameObject, string> partPlace) {
        Entity.HookTalks();
        Creatures[creature] = (creature.scene.name, place);
        var parts = 0;
        foreach (var machine in creature.GetComponentsInChildren<PlayMakerFSM>(true)) {
            var part = machine.gameObject;
            if (part != creature && !Parts.ContainsKey(part)) {
                Parts[part] = (creature, creature.scene.name, partPlace(part));
                parts++;
            }

            if (EntityRegistry.IsEachGameFsm(type, machine.FsmName)) {
                EachGame.Add(machine);
            }
        }

        return parts;
    }

    /// <summary>
    /// Whether a place is that of a summon or of a part of one (see <see cref="TakeSummon"/>).
    /// </summary>
    public static bool IsSummonPlace(string place) {
        return place.Length > 0 && place[0] == SummonStart;
    }

    /// <summary>
    /// The path that names an object of a summon in every game (see <see cref="TakeSummon"/>): the place of the summon,
    /// and the path of the object under it, or null for an object of no summon.
    /// </summary>
    public static string? SummonPathOf(GameObject target) {
        if (Creatures.Count == 0) {
            return null;
        }

        if (Parts.TryGetValue(target, out var part)) {
            return IsSummonPlace(part.Place) ? part.Place : null;
        }

        for (var current = target.transform; current != null; current = current.parent) {
            if (Creatures.TryGetValue(current.gameObject, out var at)) {
                return !IsSummonPlace(at.Place) ? null
                    : current == target.transform ? at.Place
                    : at.Place + SummonPartStart + Component.PartsComponent.GetPath(current, target.transform);
            }
        }

        return null;
    }

    /// <summary>
    /// The object of a summon that a path names (see <see cref="SummonPathOf"/>), or null where there is none here.
    /// </summary>
    public static GameObject? FindSummonPath(string scene, string path) {
        if (Find(scene, path) is { } found) {
            return found;
        }

        var start = path.IndexOf(SummonPartStart);
        if (start < 0 || Find(scene, path.Substring(0, start)) is not { } summon) {
            return null;
        }

        var under = summon.transform.Find(path.Substring(start + 1));
        return under != null ? under.gameObject : null;
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
    /// Whether a state machine of a creature that every game runs by itself, or of a part of one, is one that each game
    /// runs by itself for its own player alone (see <see cref="EachGame"/>).
    /// </summary>
    public static bool RunsInEachGame(PlayMakerFSM machine) {
        return EachGame.Count > 0 && EachGame.Contains(machine);
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
    /// Whether the death of a creature that every game runs by itself was told or taken (see <see cref="MarkDead"/>).
    /// </summary>
    public static bool IsMarkedDead(GameObject creature) {
        return Dead.Count > 0 && Dead.Contains(creature);
    }

    /// <summary>
    /// Notes that a creature that every game runs by itself that died has health again, as its state machines play a
    /// death that it comes back from, like the bell creature that sinks into the ground and comes up again, whose next
    /// death here is told once more (see <see cref="MarkDead"/>; a word of another game's goes by its number, see
    /// <see cref="TakeDeath"/>).
    /// </summary>
    public static void MarkAlive(GameObject creature) {
        Dead.Remove(creature);
    }

    /// <summary>
    /// Counts a death of a creature that every game runs by itself that this game tells, returning its number, by
    /// which every other game knows whether it played that death already (see <see cref="TakeDeath"/>).
    /// </summary>
    public static ulong CountDeath(GameObject creature) {
        Deaths.TryGetValue(creature, out var deaths);
        Deaths[creature] = ++deaths;
        return deaths;
    }

    /// <summary>
    /// Takes a death of a creature that every game runs by itself that another game told, by its number there,
    /// returning whether this game has yet to play it: not one that it played already, as its own or at the word of
    /// a third game, which comes again or late. A creature that comes back from its death has health again before such
    /// a word, like the map seller, whose health is set high as her defeat starts, and its death was played once more
    /// (review of cfaf69f; the user's log of 11-21, 21:41:49).
    /// </summary>
    public static bool TakeDeath(GameObject creature, ulong number) {
        if (Deaths.TryGetValue(creature, out var deaths) && deaths >= number) {
            return false;
        }

        Deaths[creature] = number;
        Dead.Add(creature);
        return true;
    }

    /// <summary>
    /// How many deaths of a creature that every game runs by itself this game played, which a member who comes into
    /// the room counts from (see <see cref="SetDeaths"/>).
    /// </summary>
    public static ulong DeathsOf(GameObject creature) {
        return Deaths.TryGetValue(creature, out var deaths) ? deaths : 0;
    }

    /// <summary>
    /// Has this game count the deaths of a creature that every game runs by itself from as many as the scene host's
    /// game played before the local player came in.
    /// </summary>
    public static void SetDeaths(GameObject creature, ulong deaths) {
        Deaths[creature] = deaths;
    }

    /// <summary>
    /// The creature at a place of a scene as the room loaded it, if it is one that every game runs by itself, or the
    /// part with state machines of its own of one there; of any scene of the room for none given, like the creature
    /// that made a summon, which may stand in a part of the room loaded beside it, while what it made is made in the
    /// room itself.
    /// </summary>
    public static GameObject? Find(string? scene, string place) {
        foreach (var pair in Creatures) {
            if (pair.Key != null && (scene == null || pair.Value.Scene == scene) && pair.Value.Place == place) {
                return pair.Key;
            }
        }

        foreach (var pair in Parts) {
            if (pair.Key != null && (scene == null || pair.Value.Scene == scene) && pair.Value.Place == place) {
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
        Makers.Clear();
        SummonedBy.Clear();
        EachGame.Clear();
        Dead.Clear();
        Deaths.Clear();
        DeathLooks.Clear();
        OwnThreads.Clear();
    }
}
