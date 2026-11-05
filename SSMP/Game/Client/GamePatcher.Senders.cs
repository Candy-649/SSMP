using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity;
using SSMP.Game.Client.Entity.Action;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client;

/// <summary>
/// Things with no health that watch for players with ranges of their own and send creatures out of a pool at the place
/// of the one they find: the spawner that sends creatures up out of the maggot water beside a player. In a game with
/// one player it sends them at that player as often as its FSM goes round. The game that runs it sent them at its own
/// player alone, wherever the others stood, and one who stood alone elsewhere hardly saw any (USER 11-05 "那个会从蛆水里面
/// 跳出来咬人的虫，我这边好像比我朋友那边少很多").
///
/// It now sends at every player in its ranges as often as at one alone, each player having a run of it of their own,
/// however many players there are (USER 11-05 "最好是主机那边的放虫点所有人都咬，只在人的范围重合的时候掷骰子咬人",
/// "因为你的方案总是不考虑人多的情况", "我们可以按照频率出虫而不是按照数量出虫"). Players whose places are so near that the
/// stretches where it sends at each of them overlap, up to its reach on either side of a player, share one run: that of
/// the first of them, which rolls for each creature it sends at whom of them it goes. The run of the local player is the
/// thing itself, and that of each other player a copy of it, made in the game that runs the thing alone. A copy takes
/// its creatures out of the same pool and sends them the way the thing does, so the other games see them as they see
/// those of the thing. The pool holds as many creatures for each of four players as the room gives it for one ("假设一片
/// 蛆水的虫免费不限量", see <see cref="GrowSenderPools"/>).
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// The number of players for each of whom the pool of a thing that sends creatures holds as many as the room gives it
    /// for one.
    /// </summary>
    private const int SenderPoolPlayers = 4;

    /// <summary>
    /// A thing that sends creatures out of a pool at the players in its ranges, with the runs of it that this game makes.
    /// </summary>
    private sealed class Sender {
        /// <summary>
        /// The room's own thing, which is the run of the local player.
        /// </summary>
        public readonly GameObject Thing;

        /// <summary>
        /// How far from a player it sends a creature at the most: the largest that it adds to the place of the player.
        /// </summary>
        public readonly float Reach;

        /// <summary>
        /// Its runs, by player.
        /// </summary>
        public readonly Dictionary<GameObject, SenderRun> Runs = new();

        public Sender(GameObject thing, float reach) {
            Thing = thing;
            Reach = reach;
        }
    }

    /// <summary>
    /// The run of a thing that sends creatures for one player: the thing itself, or a copy of it.
    /// </summary>
    private sealed class SenderRun {
        /// <summary>
        /// The thing.
        /// </summary>
        public readonly Sender Sender;

        /// <summary>
        /// The player it is the run of.
        /// </summary>
        public readonly GameObject Player;

        /// <summary>
        /// The object whose FSM goes round for the player.
        /// </summary>
        public readonly GameObject Run;

        /// <summary>
        /// The range it last asked as it watched for players, between the creatures it sends, or null.
        /// </summary>
        public AlertRange? Watch;

        /// <summary>
        /// The frame in which it last chose whom a creature goes at, and whom.
        /// </summary>
        public int SendFrame = -1;

        public GameObject? SendsAt;

        public SenderRun(Sender sender, GameObject player, GameObject run) {
            Sender = sender;
            Player = player;
            Run = run;
        }
    }

    /// <summary>
    /// The things of the room by instance ID, as far as they were asked about, with null for one that sends nothing.
    /// </summary>
    private static readonly Dictionary<int, Sender?> Senders = new();

    /// <summary>
    /// The runs that are copies, by the instance ID of the copy.
    /// </summary>
    private static readonly Dictionary<int, SenderRun> CopiedRuns = new();

    /// <summary>
    /// The players standing in the room as a thing is asked about, kept so that asking a range about one of them does not
    /// refill the list being gone through (see PlayerTargetRegistry.GetTrackedPlayers).
    /// </summary>
    private static readonly List<GameObject> StandingPlayers = [];

    /// <summary>
    /// The players inside the range of a thing as it is asked about.
    /// </summary>
    private static readonly List<GameObject> PlayersInRange = [];

    /// <summary>
    /// The players who share a run, as it is asked about.
    /// </summary>
    private static readonly List<GameObject> Sharers = [];

    /// <summary>
    /// The runs that are dropped, gathered before they are.
    /// </summary>
    private static readonly List<SenderRun> DroppedRuns = [];

    /// <summary>
    /// Whether each state of the FSM of a run aims at a player, as it was first asked (see AimsAtAPlayerNow), so that
    /// what a run asks every frame names no types.
    /// </summary>
    private static readonly Dictionary<FsmState, bool> AimingStates = new();

    /// <summary>
    /// The run of a thing that sends creatures whose FSM runs on the given object, while this game runs the thing: the
    /// copy's own run, or the run of the local player for the thing itself; otherwise null. A copy whose thing this game
    /// no longer runs is dropped with the others.
    /// </summary>
    /// <param name="owner">The object of the FSM.</param>
    private static SenderRun? SenderRunOf(GameObject owner) {
        var id = owner.GetInstanceID();
        if (CopiedRuns.TryGetValue(id, out var copied)) {
            if (Entity.Entity.RunsCreatureHere(copied.Sender.Thing)) {
                return copied;
            }

            DropRuns(copied.Sender, "this game no longer runs it");
            return null;
        }

        if (!Senders.TryGetValue(id, out var sender)) {
            sender = IsSender(owner) ? new Sender(owner, ReachOf(owner)) : null;
            Senders[id] = sender;
            if (sender != null) {
                Logger.Info(
                    $"[Senders] '{owner.name}' sends creatures at the players in its ranges, up to {sender.Reach} " +
                    $"from the player, each player their own run of it; players less than {2 * sender.Reach} apart " +
                    "share one, which rolls for each creature"
                );
            }
        }

        if (sender == null || !Entity.Entity.RunsCreatureHere(owner) || LocalPlayerObject() is not { } local) {
            return null;
        }

        if (!sender.Runs.TryGetValue(local, out var run)) {
            sender.Runs[local] = run = new SenderRun(sender, local, owner);
        }

        return run;
    }

    /// <summary>
    /// Whether an object is a thing that sends creatures: with no health, watching for players with ranges of its own,
    /// with an FSM that takes creatures out of a pool and aims at a player.
    /// </summary>
    /// <param name="owner">The object.</param>
    private static bool IsSender(GameObject owner) {
        if (owner.GetComponentInParent<HealthManager>(true) != null || !HasAwarenessRanges(owner)) {
            return false;
        }

        foreach (var fsm in owner.GetComponents<PlayMakerFSM>()) {
            if (PoolOf(fsm) != null) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The name of the pool that an FSM which aims at a player takes creatures out of, at random: the variable it takes
    /// them from, after which it finds the pool among the children of its parent (FindNamedChild); null for an FSM that
    /// does not.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    private static string? PoolOf(PlayMakerFSM fsm) {
        string? pool = null;
        var aims = false;
        foreach (var state in fsm.FsmStates) {
            foreach (var action in state.Actions) {
                if (action is GetRandomChild {
                        gameObject: { OwnerOption: OwnerDefaultOption.SpecifyGameObject, GameObject.UseVariable: true }
                    } takes) {
                    pool = takes.gameObject.GameObject.Name;
                } else if (action != null && ActionRegistry.IsRetargeted(action.GetType().Name)) {
                    aims = true;
                }
            }
        }

        return aims ? pool : null;
    }

    /// <summary>
    /// How far from a player a thing sends a creature at the most: the largest that its FSMs add at random to where
    /// they read the player to be.
    /// </summary>
    /// <param name="owner">The thing.</param>
    private static float ReachOf(GameObject owner) {
        var reach = 0f;
        foreach (var fsm in owner.GetComponents<PlayMakerFSM>()) {
            var places = new HashSet<string>();
            foreach (var state in fsm.FsmStates) {
                foreach (var action in state.Actions) {
                    if (action is GetPosition2D { x.UseVariable: true } reads) {
                        places.Add(reads.x.Name);
                    }
                }
            }

            foreach (var state in fsm.FsmStates) {
                foreach (var action in state.Actions) {
                    if (action is FloatAddRandom add && places.Contains(add.floatVariable.Name)) {
                        reach = Mathf.Max(reach, Mathf.Abs(add.addMin.Value), Mathf.Abs(add.addMax.Value));
                    }
                }
            }
        }

        return reach;
    }

    /// <summary>
    /// Answers whether the player that the run of a thing that sends creatures asks about is inside one of its ranges.
    /// In a state of its FSM that aims at a player, it asks about the player whom the creature it sends now goes at
    /// (see <see cref="SendsAt"/>). Otherwise it watches for players: the range answers yes for the first of the players
    /// inside it who share the run of its player, and no to the run of anyone else, which waits meanwhile. The thing
    /// itself makes and drops the runs of the other players as it watches (see <see cref="KeepRuns"/>).
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="range">The range.</param>
    private static bool AnswerForSenderRun(SenderRun run, AlertRange range) {
        if (FsmExecutionStack.ExecutingState is { } state && Aims(state)) {
            return IsValidTargetForAlertRange(range, SendsAt(run));
        }

        run.Watch = range;
        if (run.Run == run.Sender.Thing) {
            KeepRuns(run.Sender);
        }

        FindSharers(run.Sender, range, run.Player);
        return Sharers.Count > 0 && FirstOf(Sharers) == run.Player;
    }

    /// <summary>
    /// Whether a state of the FSM of a run aims at a player (see <see cref="AimingStates"/>).
    /// </summary>
    /// <param name="state">The state.</param>
    private static bool Aims(FsmState state) {
        if (!AimingStates.TryGetValue(state, out var aims)) {
            AimingStates[state] = aims = AimsAtAPlayerNow(state);
        }

        return aims;
    }

    /// <summary>
    /// Whom the creature that the run of a thing sends now goes at, chosen once in each frame in which the run aims:
    /// its player, or, while other players share the run, one of them rolled for, the players whose turn of the dice it
    /// is having equal chances. The FSM's aims at a player read the place of the one chosen.
    /// </summary>
    /// <param name="run">The run.</param>
    private static GameObject SendsAt(SenderRun run) {
        if (run.SendFrame == Time.frameCount && run.SendsAt != null) {
            return run.SendsAt;
        }

        run.SendFrame = Time.frameCount;
        if (run.Watch != null) {
            FindSharers(run.Sender, run.Watch, run.Player);
        } else {
            Sharers.Clear();
        }

        var target = Sharers.Count == 0 ? run.Player : Sharers[Random.Range(0, Sharers.Count)];
        run.SendsAt = target;
        EnemyApprovedTargets[run.Run.GetInstanceID()] = target;
        Logger.Info(
            $"[Senders] '{run.Run.name}' aims what it sends now at {DescribePlayer(target)}" +
            (Sharers.Count > 1 ? $", rolled among the {Sharers.Count} players who share its run" : "")
        );
        return target;
    }

    /// <summary>
    /// Fills <see cref="Sharers"/> with the players who share the run of the given player in a range of a thing that
    /// sends creatures: the standing players inside the range less than twice its reach apart, one from the next,
    /// starting from that player. Empty while the player is not inside it.
    /// </summary>
    /// <param name="sender">The thing.</param>
    /// <param name="range">The range.</param>
    /// <param name="player">The player.</param>
    private static void FindSharers(Sender sender, AlertRange range, GameObject player) {
        Sharers.Clear();
        PlayersInRange.Clear();
        StandingPlayers.Clear();
        StandingPlayers.AddRange(PlayerTargetRegistry.GetTrackedPlayers());
        foreach (var standing in StandingPlayers) {
            if (IsValidTargetForAlertRange(range, standing)) {
                PlayersInRange.Add(standing);
            }
        }

        if (!PlayersInRange.Contains(player)) {
            return;
        }

        Sharers.Add(player);
        for (var i = 0; i < Sharers.Count; i++) {
            var x = Sharers[i].transform.position.x;
            foreach (var other in PlayersInRange) {
                if (!Sharers.Contains(other) && Mathf.Abs(other.transform.position.x - x) < 2 * sender.Reach) {
                    Sharers.Add(other);
                }
            }
        }
    }

    /// <summary>
    /// The first of players who share a run, whose run sends for all of them: the local player, else the one whose
    /// object has the lowest instance ID, which stays the same while they are in the room.
    /// </summary>
    /// <param name="players">The players, of whom there is at least one.</param>
    private static GameObject FirstOf(List<GameObject> players) {
        var first = players[0];
        foreach (var player in players) {
            if (player == LocalPlayerObject()) {
                return player;
            }

            if (player.GetInstanceID() < first.GetInstanceID()) {
                first = player;
            }
        }

        return first;
    }

    /// <summary>
    /// Makes a run of a thing that sends creatures for each other standing player in the room that has none, and drops
    /// the run of a player who has left it.
    /// </summary>
    /// <param name="sender">The thing.</param>
    private static void KeepRuns(Sender sender) {
        DroppedRuns.Clear();
        foreach (var run in sender.Runs.Values) {
            if (run.Run != sender.Thing && (run.Player == null || !run.Player.activeInHierarchy)) {
                DroppedRuns.Add(run);
            }
        }

        foreach (var run in DroppedRuns) {
            DropRun(run, "its player left the room");
        }

        foreach (var player in PlayerTargetRegistry.GetTrackedPlayers()) {
            if (!sender.Runs.ContainsKey(player)) {
                MakeRun(sender, player);
                return;
            }
        }
    }

    /// <summary>
    /// Makes the run of a thing that sends creatures for a player other than the local one: a copy of the thing beside
    /// it, without what this mod put on the thing, made while it is switched off so that none of that wakes. Whatever
    /// of the thing's own its FSM points at is pointed at that of the thing, so that the creatures it sends are told of
    /// the thing's parts, which the other games know of, as when the thing sends them.
    /// </summary>
    /// <param name="sender">The thing.</param>
    /// <param name="player">The player.</param>
    private static void MakeRun(Sender sender, GameObject player) {
        var thing = sender.Thing.transform;
        var holder = new GameObject("Run holder");
        holder.SetActive(false);

        var copy = Object.Instantiate(sender.Thing, holder.transform);
        copy.name = $"Run of {sender.Thing.name} for {player.name}";
        foreach (var behaviour in copy.GetComponentsInChildren<MonoBehaviour>(true)) {
            if (behaviour.GetType().Assembly == typeof(GamePatcher).Assembly) {
                Object.DestroyImmediate(behaviour);
            }
        }

        var run = new SenderRun(sender, player, copy);
        sender.Runs[player] = run;
        CopiedRuns[copy.GetInstanceID()] = run;

        copy.transform.SetParent(thing.parent, false);
        copy.transform.localPosition = thing.localPosition;
        copy.transform.localRotation = thing.localRotation;
        copy.transform.localScale = thing.localScale;
        Object.Destroy(holder);

        foreach (var fsm in copy.GetComponents<PlayMakerFSM>()) {
            foreach (var variable in fsm.FsmVariables.GameObjectVariables) {
                var part = variable.Value;
                if (part != null && part != copy && part.transform.IsChildOf(copy.transform) &&
                    thing.Find(PathUnder(copy.transform, part.transform)) is { } own) {
                    variable.Value = own.gameObject;
                }
            }
        }

        Logger.Info($"[Senders] '{sender.Thing.name}' gets a run of its own for {DescribePlayer(player)}");
    }

    /// <summary>
    /// The path of a part of an object from the object, as Transform.Find takes it.
    /// </summary>
    /// <param name="root">The object.</param>
    /// <param name="part">The part.</param>
    private static string PathUnder(Transform root, Transform part) {
        var path = part.name;
        for (var current = part.parent; current != root; current = current.parent) {
            path = $"{current.name}/{path}";
        }

        return path;
    }

    /// <summary>
    /// Drops every run of a thing that is a copy.
    /// </summary>
    /// <param name="sender">The thing.</param>
    /// <param name="why">Why, for the log.</param>
    private static void DropRuns(Sender sender, string why) {
        DroppedRuns.Clear();
        foreach (var run in sender.Runs.Values) {
            if (run.Run != sender.Thing) {
                DroppedRuns.Add(run);
            }
        }

        foreach (var run in DroppedRuns) {
            DropRun(run, why);
        }
    }

    /// <summary>
    /// Drops a run of a thing that is a copy, and the copy with it.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="why">Why, for the log.</param>
    private static void DropRun(SenderRun run, string why) {
        run.Sender.Runs.Remove(run.Player);
        CopiedRuns.Remove(run.Run.GetInstanceID());
        EnemyApprovedTargets.Remove(run.Run.GetInstanceID());

        // Off at once, so that it sends nothing more in this frame, at the end of which it goes
        run.Run.SetActive(false);
        Object.Destroy(run.Run);
        Logger.Info($"[Senders] The run of '{run.Sender.Thing.name}' for a player is dropped: {why}");
    }

    /// <summary>
    /// Forgets the things that send creatures with the room, whose copies go with it, or as the hooks go with a lost
    /// link: then the copies are taken out here, as they would go on sending at the local player in the room, which
    /// nothing answers for them any more.
    /// </summary>
    private static void ClearSenders() {
        foreach (var run in CopiedRuns.Values) {
            if (run.Run != null) {
                run.Run.SetActive(false);
                Object.Destroy(run.Run);
            }
        }

        Senders.Clear();
        CopiedRuns.Clear();
        AimingStates.Clear();
    }

    /// <summary>
    /// Fills the pool of each thing that sends creatures in a room that starts, so that it holds as many for each of
    /// <see cref="SenderPoolPlayers"/> players as the room gives it for one: copies of its first creature, named after it
    /// with a number. Every run of such a thing takes its creatures out of its pool, and a pool that ran out had a run
    /// take the creature it took last, which it sent again from wherever it was. Done in every game alike as the room
    /// starts, before anything in it is made an entity, so that each game has the same creatures under the same names
    /// (EntityProcessor.GetStableSceneId), and the game that runs the room sends them where the others see them.
    /// </summary>
    /// <param name="scene">The room.</param>
    internal static void GrowSenderPools(Scene scene) {
        var grown = new HashSet<Transform>();
        foreach (var fsm in Object.FindObjectsByType<PlayMakerFSM>(FindObjectsSortMode.None)) {
            if (fsm.gameObject.scene != scene || fsm.transform.parent is not { } parent ||
                !EntityRegistry.TryGetEntry(fsm.gameObject, out _) || !IsSender(fsm.gameObject) ||
                PoolOf(fsm) is not { } name || parent.Find(name) is not { childCount: > 0 } pool || !grown.Add(pool)) {
                continue;
            }

            var first = pool.GetChild(0).gameObject;
            var each = pool.childCount;
            for (var number = 1; number <= each * (SenderPoolPlayers - 1); number++) {
                Object.Instantiate(first, pool).name = $"{first.name} (Coop {number})";
            }

            Logger.Info(
                $"[Senders] The pool '{name}' of '{fsm.name}' holds {pool.childCount} creatures now, {each} for each " +
                $"of {SenderPoolPlayers} players"
            );
        }
    }
}
