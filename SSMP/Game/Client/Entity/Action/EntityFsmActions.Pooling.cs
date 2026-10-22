using System.Collections;
using System.Collections.Generic;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Save;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Random = UnityEngine.Random;

// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedParameter.Local
#pragma warning disable CS0618
#pragma warning disable CS8600
#pragma warning disable CS8618

namespace SSMP.Game.Client.Entity.Action;

internal static partial class EntityFsmActions {
    #region SpawnObjectFromGlobalPool

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SpawnObjectFromGlobalPool action) {
        // We first check whether this action results in the spawning of an entity that is managed by the
        // system. Because if so, it would already be handled by an EntitySpawn packet instead, and this will only
        // duplicate the spawning and leave it uncontrolled. So we don't send the data at all
        if (EntitySpawnEvent.Invoke(
                new EntitySpawnDetails {
                    Type = EntitySpawnType.FsmAction,
                    Action = action,
                    GameObject = action.storeObject.Value
                }
            )) {
            return false;
        }

        var position = Vector3.zero;
        var euler = Vector3.up;

        var spawnPoint = action.spawnPoint.Value;
        if (spawnPoint != null) {
            position = spawnPoint.transform.position;
            if (!action.position.IsNone) {
                position += action.position.Value;
            }

            euler = !action.rotation.IsNone ? action.rotation.Value : spawnPoint.transform.eulerAngles;
        } else {
            if (!action.position.IsNone)
                position = action.position.Value;
            if (!action.rotation.IsNone)
                euler = action.rotation.Value;
        }

        SayIfThrownUnseen(action, position, "on the game that runs it");

        data.Packet.Write(position.x);
        data.Packet.Write(position.y);
        data.Packet.Write(position.z);

        data.Packet.Write(euler.x);
        data.Packet.Write(euler.y);
        data.Packet.Write(euler.z);

        ThrownThings.WriteThrows(data, action);
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SpawnObjectFromGlobalPool action) {
        var position = new Vector3(
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat()
        );
        var euler = new Vector3(
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat()
        );
        var throws = ThrownThings.ReadThrows(data);

        if (ThrownThings.PrefabToThrow(action, action.gameObject?.Value, throws) is not { } prefab) {
            return;
        }

        SayIfThrownUnseen(action, position, "on the game that watches it");

        // Under the name that the game that runs the creature gave it, which what happens to it is sent under
        using (ThrownThings.Expect(prefab, throws)) {
            action.storeObject.Value = prefab.Spawn(position, Quaternion.Euler(euler));
        }
    }

    /// <summary>
    /// The creatures already named for throwing something while they could not be seen, so that each one is named
    /// once rather than on every throw.
    /// </summary>
    private static readonly HashSet<int> NamedForThrowingUnseen = [];

    /// <summary>
    /// Says, once per creature, that it threw something from a place where it could not be seen: switched off, not
    /// drawn, or standing somewhere else entirely. A player saw a flyer throw rocks at them that was nowhere to be
    /// seen, and the log could not tell which of those it was, nor on which of the two games.
    /// </summary>
    /// <param name="action">The action doing the throwing.</param>
    /// <param name="from">Where the thrown thing appears.</param>
    /// <param name="where">Which of the two games this is, as it should read in the log.</param>
    private static void SayIfThrownUnseen(SpawnObjectFromGlobalPool action, Vector3 from, string where) {
        try {
            var thrower = action.Fsm?.GameObject;
            if (thrower == null || NamedForThrowingUnseen.Contains(thrower.GetInstanceID())) {
                return;
            }

            var body = thrower.GetComponent<Renderer>();
            var drawn = body == null || body.enabled;
            var distance = Vector2.Distance(thrower.transform.position, from);
            if (thrower.activeInHierarchy && drawn && distance <= 6f) {
                return;
            }

            NamedForThrowingUnseen.Add(thrower.GetInstanceID());

            var thrown = action.gameObject?.Value;
            Logger.Info(
                $"'{thrower.name}' threw '{(thrown == null ? "nothing" : thrown.name)}' {where} while it could not " +
                $"be seen there: on: {thrower.activeInHierarchy}, drawn: {(body == null ? "-" : body.enabled.ToString())}, " +
                $"standing at {thrower.transform.position}, {distance:0.0} away from where it threw from, {from}"
            );
        } catch (System.Exception e) {
            Logger.Warn($"Could not say whether a throw was seen: {e.Message}");
        }
    }

    #endregion

    #region FlingObjectsFromGlobalPool

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, FlingObjectsFromGlobalPool action) {
        // We first check whether the game object belonging to the Rigidbody2D in the action is an object that is
        // managed by the system. Because if so, it means that we have already caught its spawning in the IL hook
        // for the action and sent an EntitySpawn packet instead. So we need not also network this action separately.
        var rigidbody = action.rb2d;
        if (rigidbody != null && rigidbody.gameObject != null && IsObjectInRegistry(rigidbody.gameObject)) {
            return false;
        }

        var position = Vector3.zero;

        var spawnPoint = action.spawnPoint.Value;
        if (spawnPoint != null) {
            position = spawnPoint.transform.position;
            if (!action.position.IsNone) {
                position += action.position.Value;
            }
        } else if (!action.position.IsNone) {
            position = action.position.Value;
        }

        if (!RandomActionValues.TryGetValue(action, out var queue)) {
            return false;
        }

        if (queue.Count == 0) {
            return false;
        }

        data.Packet.Write(position.x);
        data.Packet.Write(position.y);
        data.Packet.Write(position.z);

        var numSpawns = (int) queue.Dequeue();
        data.Packet.Write((byte) numSpawns);

        for (var i = 0; i < numSpawns; i++) {
            if (action.originVariationX != null) {
                if (queue.Count == 0) {
                    return false;
                }

                var originVariationX = (float) queue.Dequeue();
                data.Packet.Write(originVariationX);
            } else {
                data.Packet.Write(0f);
            }

            if (action.originVariationY != null) {
                if (queue.Count == 0) {
                    return false;
                }

                var originVariationY = (float) queue.Dequeue();
                data.Packet.Write(originVariationY);
            } else {
                data.Packet.Write(0f);
            }

            if (queue.Count < 2) {
                queue.Clear();
                return false;
            }

            var speed = (float) queue.Dequeue();
            var angle = (float) queue.Dequeue();

            data.Packet.Write(speed);
            data.Packet.Write(angle);
        }

        queue.Clear();
        ThrownThings.WriteThrows(data, action);
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, FlingObjectsFromGlobalPool action) {
        var position = new Vector3(
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat()
        );

        var flings = ReadFlings(data);
        var throws = ThrownThings.ReadThrows(data);
        if (ThrownThings.PrefabToThrow(action, action.gameObject.Value, throws) is not { } prefab) {
            return;
        }

        using var named = ThrownThings.Expect(prefab, throws);
        foreach (var (originVariationX, originVariationY, speed, angle) in flings) {
            // Each from the point it is flung from, as the game does it, rather than from where the one before went
            var go = prefab.Spawn(position, Quaternion.Euler(Vector3.zero));
            go.transform.position = new Vector3(position.x + originVariationX, position.y + originVariationY,
                position.z);

            var x = speed * Mathf.Cos(angle * ((float) System.Math.PI / 180f));
            var y = speed * Mathf.Sin(angle * ((float) System.Math.PI / 180f));

            var rigidBody = go.GetComponent<Rigidbody2D>();
            if (rigidBody == null) {
                return;
            }

            rigidBody.velocity = new Vector2(x, y);

            if (!action.FSM.IsNone) {
                FSMUtility.LocateFSM(go, action.FSM.Value).SendEvent(action.FSMEvent.Value);
            }
        }
    }

    /// <summary>
    /// Reads what a fling sent of each thing it flung: how far from the point it is flung from it starts, and how fast
    /// and which way it goes, as two numbers.
    /// </summary>
    private static (float OriginX, float OriginY, float First, float Second)[] ReadFlings(EntityNetworkData data) {
        var flings = new (float, float, float, float)[data.Packet.ReadByte()];
        for (var i = 0; i < flings.Length; i++) {
            flings[i] = (data.Packet.ReadFloat(), data.Packet.ReadFloat(), data.Packet.ReadFloat(),
                data.Packet.ReadFloat());
        }

        return flings;
    }

    #endregion

    #region SpawnRandomObjectsRadialV2

    // A ring of things that a creature bursts into, such as the spikes of the flyers that shatter when they are struck.
    // Left to the game that runs the creature, the copy in the other game only played the shattering and was gone,
    // with no spikes: a flyer that the other player saw burst into spikes only vanished (USER 10-16 "为什么有的虫子被打
    // 之后会爆出来刺有的虫子被打了就直接消失了"). What the action rolled goes as where each thing lies, and the things are
    // named in both games like everything else creatures throw (ThrownThings).

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SpawnRandomObjectsRadialV2 action) {
        // What the action rolled as it ran: how many it spawned, then how far the angle of each was off its share of
        // the circle (see SpawnRandomObjectsRadialV2OnEnter)
        if (!RandomActionValues.TryGetValue(action, out var queue) || queue.Count == 0) {
            return false;
        }

        var count = (int) queue.Dequeue();
        var spawnPoint = action.Fsm.GetOwnerDefaultTarget(action.SpawnPoint);
        if (count <= 0 || count > byte.MaxValue || queue.Count < count || spawnPoint == null) {
            queue.Clear();
            ThrownThings.DropHostThrows(action);
            return false;
        }

        var position = spawnPoint.transform.position;
        data.Packet.Write(position.x);
        data.Packet.Write(position.y);
        data.Packet.Write(position.z);

        data.Packet.Write((byte) count);
        for (var i = 0; i < count; i++) {
            var share = Mathf.Lerp(action.MinAngle.Value, action.MaxAngle.Value, i / (float) count);
            data.Packet.Write(share + (float) queue.Dequeue());
        }

        queue.Clear();
        ThrownThings.WriteThrows(data, action);
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SpawnRandomObjectsRadialV2 action) {
        var position = new Vector3(
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat()
        );

        var angles = new float[data.Packet.ReadByte()];
        for (var i = 0; i < angles.Length; i++) {
            angles[i] = data.Packet.ReadFloat();
        }

        var throws = ThrownThings.ReadThrows(data);
        if (ThrownThings.PrefabToThrow(action, action.Prefab?.Value, throws) is not { } prefab) {
            return;
        }

        var spawnPoint = action.Fsm.GetOwnerDefaultTarget(action.SpawnPoint);
        Transform? parent = action.SpawnAsChildren.Value && spawnPoint != null ? spawnPoint.transform : null;
        var radius = action.SpawnRadius.Value;

        // As the game does it: each comes out of the pool first, and is then turned and put at its place on the ring
        using var named = ThrownThings.Expect(prefab, throws);
        foreach (var angle in angles) {
            var spawned = prefab.Spawn(parent);
            spawned.transform.SetRotation2D(angle);

            var radians = angle * Mathf.Deg2Rad;
            spawned.transform.position = position + new Vector3(Mathf.Cos(radians), Mathf.Sin(radians), 0f) * radius;
        }
    }

    #endregion

    #region FlingObjectsFromGlobalPoolVel

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, FlingObjectsFromGlobalPoolVel action) {
        // We first check whether the game object belonging to the Rigidbody2D in the action is an object that is
        // managed by the system. Because if so, it means that we have already caught its spawning in the IL hook
        // for the action and sent an EntitySpawn packet instead. So we need not also network this action separately.
        var rigidbody = action.rb2d;
        if (rigidbody != null && rigidbody.gameObject != null && IsObjectInRegistry(rigidbody.gameObject)) {
            return false;
        }

        var position = Vector3.zero;

        var spawnPoint = action.spawnPoint.Value;
        if (spawnPoint != null) {
            position = spawnPoint.transform.position;
            if (!action.position.IsNone) {
                position += action.position.Value;
            }
        } else if (!action.position.IsNone) {
            position = action.position.Value;
        }

        if (!RandomActionValues.TryGetValue(action, out var queue)) {
            return false;
        }

        if (queue.Count == 0) {
            return false;
        }

        data.Packet.Write(position.x);
        data.Packet.Write(position.y);
        data.Packet.Write(position.z);

        var numSpawns = (int) queue.Dequeue();
        data.Packet.Write((byte) numSpawns);

        for (var i = 0; i < numSpawns; i++) {
            if (action.originVariationX != null) {
                if (queue.Count == 0) {
                    return false;
                }

                var originVariationX = (float) queue.Dequeue();
                data.Packet.Write(originVariationX);
            } else {
                data.Packet.Write(0f);
            }

            if (action.originVariationY != null) {
                if (queue.Count == 0) {
                    return false;
                }

                var originVariationY = (float) queue.Dequeue();
                data.Packet.Write(originVariationY);
            } else {
                data.Packet.Write(0f);
            }

            if (queue.Count < 2) {
                queue.Clear();
                return false;
            }

            var speedX = (float) queue.Dequeue();
            var speedY = (float) queue.Dequeue();

            data.Packet.Write(speedX);
            data.Packet.Write(speedY);
        }

        queue.Clear();
        ThrownThings.WriteThrows(data, action);
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, FlingObjectsFromGlobalPoolVel action) {
        var position = new Vector3(
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat()
        );

        var flings = ReadFlings(data);
        var throws = ThrownThings.ReadThrows(data);
        if (ThrownThings.PrefabToThrow(action, action.gameObject.Value, throws) is not { } prefab) {
            return;
        }

        using var named = ThrownThings.Expect(prefab, throws);
        foreach (var (originVariationX, originVariationY, speedX, speedY) in flings) {
            var go = prefab.Spawn(position, Quaternion.Euler(Vector3.zero));
            go.transform.position = new Vector3(position.x + originVariationX, position.y + originVariationY,
                position.z);

            var rigidBody = go.GetComponent<Rigidbody2D>();
            if (rigidBody == null) {
                return;
            }

            rigidBody.velocity = new Vector2(speedX, speedY);
        }
    }

    #endregion
}
