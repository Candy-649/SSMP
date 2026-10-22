using System;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Save;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Random = UnityEngine.Random;

namespace SSMP.Animation.Effects.SilkSkills;

/// <summary>
/// The dice of Rune Rage, which every game rolls alike. Each cluster of blasts that a cast of the local player puts
/// down rolls under a name made of the cast and its place among the cast's clusters, and each blast of a cluster
/// under the cluster's name and its place among the cluster's blasts (see <see cref="SharedDice.RollByName"/>). The
/// number of the cast goes to the others with where its clusters are (see <see cref="RuneRage"/>), and their games
/// play each cluster as its state machine would, from the same names (see <see cref="SharedDice.RollAs{T}"/>): when it
/// goes off, how it is turned, where each of its blasts comes down and how large each one is.
/// </summary>
internal static class RuneRolls {
    /// <summary>
    /// The name of the prefab of a cluster.
    /// </summary>
    private const string ClusterPrefabName = "Weaver Bomb Blast Cluster";

    /// <summary>
    /// The name of the state machine of a cluster.
    /// </summary>
    private const string ClusterFsmName = "Control";

    /// <summary>
    /// The state of a cluster that waits before it goes off, and the one that puts its blasts down.
    /// </summary>
    private const string WaitState = "Init";

    /// <inheritdoc cref="WaitState"/>
    private const string BlastsState = "Do Explosions";

    /// <summary>
    /// The variable of a blast's state machine that holds how long it waits before it goes off.
    /// </summary>
    private const string WaitTimeVariable = "Wait Time";

    /// <summary>
    /// The number of the local player's cast, counted round. It starts at random, so that the clusters of two players
    /// that go off in the same game are not named alike.
    /// </summary>
    private static ushort _cast = (ushort) new System.Random().Next(ushort.MaxValue + 1);

    /// <summary>
    /// The clusters of the local player's cast, in the order they came.
    /// </summary>
    private static readonly List<GameObject> CastClusters = [];

    /// <summary>
    /// The clusters of the local player by the object, with their names and how many blasts each put down.
    /// </summary>
    private static readonly Dictionary<GameObject, Cluster> Clusters = new();

    /// <summary>
    /// What the pool is to switch on next as a cluster or a blast of the local player, or null.
    /// </summary>
    private static Expected? _expected;

    /// <summary>
    /// How a cluster plays, read from its state machine once, or null before then.
    /// </summary>
    private static ClusterPlan? _plan;

    /// <summary>
    /// Whether reading how a cluster plays failed, which is only logged once.
    /// </summary>
    private static bool _planFailed;

    /// <summary>
    /// The number of the local player's cast.
    /// </summary>
    public static ushort Cast => _cast;

    /// <summary>
    /// The name a cluster rolls under.
    /// </summary>
    /// <param name="cast">The number of the cast.</param>
    /// <param name="index">Its place among the cast's clusters.</param>
    public static string ClusterName(ushort cast, int index) {
        return "rune:" + cast.ToString(CultureInfo.InvariantCulture) + ":" +
               index.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The name a blast of a cluster rolls under.
    /// </summary>
    /// <param name="cluster">The name of the cluster.</param>
    /// <param name="index">Its place among the cluster's blasts.</param>
    public static string BlastName(string cluster, int index) {
        return cluster + ":" + index.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Begins a cast of the local player: the clusters from here on are its own.
    /// </summary>
    public static void BeginCast() {
        _cast++;
        CastClusters.Clear();

        // The pool keeps its clusters, but a room may take its own along when it goes
        if (Clusters.Count > 0) {
            var gone = new List<GameObject>();
            foreach (var cluster in Clusters.Keys) {
                if (cluster == null) {
                    gone.Add(cluster!);
                }
            }

            foreach (var cluster in gone) {
                Clusters.Remove(cluster);
            }
        }
    }

    /// <summary>
    /// Takes where the clusters of the local player's cast are now, in the order they came, and forgets them.
    /// </summary>
    public static List<Vector2> TakeCastClusters() {
        var positions = new List<Vector2>(CastClusters.Count);
        foreach (var cluster in CastClusters) {
            if (cluster != null) {
                positions.Add(cluster.transform.position);
            }
        }

        CastClusters.Clear();
        return positions;
    }

    /// <summary>
    /// Has the next thing that comes out of the pool named if it is a cluster of the local player's cast, which the
    /// hero's state machine of the silk skills puts down, or a blast that a cluster of the local player puts down.
    /// </summary>
    /// <param name="prefab">What comes out of the pool.</param>
    /// <returns>What stops the naming once it is out, or null if it is not named.</returns>
    public static IDisposable? ExpectSpawn(GameObject? prefab) {
        if (prefab == null || FsmExecutionStack.ExecutingFsm is not { } fsm) {
            return null;
        }

        Expected expected;
        var hero = HeroController.SilentInstance;
        if (hero != null && hero.silkSpecialFSM != null && fsm == hero.silkSpecialFSM.Fsm) {
            if (prefab.name != ClusterPrefabName) {
                return null;
            }

            expected = new Expected(prefab.name, null);
        } else if (Clusters.Count > 0 && fsm.GameObject is { } owner && owner != null &&
                   Clusters.TryGetValue(owner, out var cluster) &&
                   FsmExecutionStack.ExecutingAction is SpawnObjectFromGlobalPool spawn &&
                   spawn.gameObject?.Value == prefab) {
            // Only what the action puts down, not what a black thread spawns with it
            expected = new Expected(prefab.name, cluster);
        } else {
            return null;
        }

        var before = _expected;
        _expected = expected;
        return new Restore(before);
    }

    /// <summary>
    /// Names a thing that the pool is about to switch on, if it is the one expected, before its state machine starts
    /// and rolls.
    /// </summary>
    /// <param name="gameObject">The thing.</param>
    public static void OnActivating(GameObject gameObject) {
        if (_expected is not { } expected || gameObject == null || !expected.Matches(gameObject)) {
            return;
        }

        _expected = null;
        if (expected.Cluster is { } cluster) {
            SharedDice.RollByName(gameObject, BlastName(cluster.Name, cluster.Blasts++));
            return;
        }

        var name = ClusterName(_cast, CastClusters.Count);
        SharedDice.RollByName(gameObject, name);
        Clusters[gameObject] = new Cluster(name);
        CastClusters.Add(gameObject);
    }

    /// <summary>
    /// How a cluster plays, read from the state machine of its prefab the first time, or null if it can't be read.
    /// </summary>
    /// <param name="clusterPrefab">The prefab of a cluster.</param>
    public static ClusterPlan? GetPlan(GameObject? clusterPrefab) {
        if (_plan != null || _planFailed || clusterPrefab == null) {
            return _plan;
        }

        try {
            _plan = ClusterPlan.Read(clusterPrefab);
        } catch (Exception e) {
            Logger.Warn($"Could not read how a cluster of Rune Rage plays: {e.Message}");
        }

        _planFailed = _plan == null;
        return _plan;
    }

    /// <summary>
    /// A cluster of the local player: its name and how many blasts it put down.
    /// </summary>
    private sealed class Cluster(string name) {
        public string Name { get; } = name;
        public int Blasts { get; set; }
    }

    /// <summary>
    /// What the pool is to switch on next: a cluster of the cast, or a blast of the given cluster.
    /// </summary>
    private sealed class Expected(string prefabName, Cluster? cluster) {
        public Cluster? Cluster { get; } = cluster;

        /// <summary>
        /// Whether a thing being switched on is of the prefab. The pool makes its things as copies of the prefab, named
        /// after it.
        /// </summary>
        public bool Matches(GameObject gameObject) {
            var name = gameObject.name;
            return name == prefabName || name.StartsWith(prefabName + "(Clone)", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Puts back what was expected before, once disposed of.
    /// </summary>
    private sealed class Restore(Expected? before) : IDisposable {
        public void Dispose() {
            _expected = before;
        }
    }

    /// <summary>
    /// How a cluster plays, as its state machine has it: how long it waits at random before it goes off, how it is
    /// turned at random, and where each blast comes down - at a point of the cluster moved by a shift at random - and
    /// how long each waits before it goes off. Each roll goes with the number of the action that rolls it (see
    /// <see cref="SharedDice.Identify(string, string, string, int)"/>).
    /// </summary>
    internal sealed class ClusterPlan {
        private int _waitRoller;
        private float _waitMin;
        private float _waitMax;
        private int? _turnRoller;

        /// <summary>
        /// The blasts of a cluster, in the order it puts them down.
        /// </summary>
        public List<BlastPlan> Blasts { get; } = [];

        /// <summary>
        /// Reads how a cluster plays from the state machine of its prefab.
        /// </summary>
        public static ClusterPlan? Read(GameObject prefab) {
            var fsm = prefab.LocateMyFSM(ClusterFsmName);
            if (fsm == null || fsm.GetStateOrNull(WaitState) is not { } waitState ||
                fsm.GetStateOrNull(BlastsState) is not { } blastsState) {
                return null;
            }

            // The actions of each state are asked for once: the state machine of a prefab never wakes, and the game
            // writes an error to its log each time they are asked for
            var waitActions = waitState.Actions;
            var waitPlace = Array.FindIndex(waitActions, action => action is WaitRandom);
            if (waitPlace < 0) {
                return null;
            }

            var wait = (WaitRandom) waitActions[waitPlace];
            var plan = new ClusterPlan {
                _waitRoller = Roller(WaitState, waitPlace),
                _waitMin = wait.timeMin.Value,
                _waitMax = wait.timeMax.Value
            };

            Shift? shift = null;
            var actions = blastsState.Actions;
            for (var place = 0; place < actions.Length; place++) {
                switch (actions[place]) {
                    case SetRandomRotation turn when turn.z.Value:
                        plan._turnRoller = Roller(BlastsState, place);
                        break;
                    case RandomVector3 random:
                        shift = new Shift(Roller(BlastsState, place), random.Start.Value, random.End.Value);
                        break;
                    case SpawnObjectFromGlobalPool spawn:
                        // The point of the cluster that the blast comes down at, as the cluster holds it
                        var point = spawn.spawnPoint.Value;
                        var at = point != null
                            ? prefab.transform.InverseTransformPoint(point.transform.position)
                            : Vector3.zero;
                        plan.Blasts.Add(new BlastPlan(at, spawn.position.IsNone ? null : shift));
                        break;
                    case SetFsmFloat set when set.variableName.Value == WaitTimeVariable && plan.Blasts.Count > 0:
                        plan.Blasts[^1].WaitTime = set.setValue.Value;
                        break;
                }
            }

            return plan.Blasts.Count > 0 ? plan : null;

            static int Roller(string state, int place) {
                return SharedDice.Identify(ClusterPrefabName, ClusterFsmName, state, place);
            }
        }

        /// <summary>
        /// How long the cluster of the given name waits before it goes off.
        /// </summary>
        public float Wait(string cluster) {
            return SharedDice.RollAs(cluster, _waitRoller, () => Random.Range(_waitMin, _waitMax));
        }

        /// <summary>
        /// How the cluster of the given name is turned as it goes off.
        /// </summary>
        public Quaternion Turn(string cluster) {
            if (_turnRoller is not { } roller) {
                return Quaternion.identity;
            }

            // As SetRandomRotation turns it: a whole number of degrees
            return Quaternion.Euler(0f, 0f, SharedDice.RollAs(cluster, roller, () => (float) Random.Range(0, 360)));
        }

        /// <summary>
        /// Where a blast of the cluster of the given name comes down, as the cluster is at the given place and turned.
        /// </summary>
        public Vector3 Place(string cluster, BlastPlan blast, Vector3 position, Quaternion turn) {
            var place = position + turn * blast.Point;
            if (blast.Shift is not { } shift) {
                return place;
            }

            // As RandomVector3 rolls it: x, y and z in turn
            return place + SharedDice.RollAs(cluster, shift.Roller, () => new Vector3(
                Random.Range(shift.Min.x, shift.Max.x),
                Random.Range(shift.Min.y, shift.Max.y),
                Random.Range(shift.Min.z, shift.Max.z)
            ));
        }
    }

    /// <summary>
    /// A shift at random between two corners, with the number of the action that rolls it.
    /// </summary>
    internal readonly record struct Shift(int Roller, Vector3 Min, Vector3 Max);

    /// <summary>
    /// A blast of a cluster: the point of the cluster it comes down at, the shift from there, and how long it waits
    /// before it goes off.
    /// </summary>
    internal sealed class BlastPlan(Vector3 point, Shift? shift) {
        public Vector3 Point { get; } = point;
        public Shift? Shift { get; } = shift;
        public float WaitTime { get; set; }
    }
}
