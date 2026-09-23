using System;
using System.Collections.Generic;
using System.IO;
using SSMP.Internals;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// Shows the things that the partner's tools throw and spawn. Each copy starts where the thrower's own thing was a
/// moment after it came out, moving the way it moved, and then runs as the game runs it: it flies, bounces, sticks and
/// bursts. What only the thrower's game can know of - what the thing touched there, what hit it, what broke it - comes
/// from there as it happens. A copy is only there to be seen: see <see cref="ToolCopies"/> for all it is kept out of.
/// </summary>
internal class ThrownTool : BaseAttackTool {
    /// <summary>
    /// The copies that are out, by the character of the player who threw them and the number the thrower gave them.
    /// </summary>
    private static readonly Dictionary<(int Player, byte Id), RemoteToolCopy> Copies = new();

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        if (effectInfo == null || effectInfo.Length < 2) {
            return;
        }

        try {
            using var reader = new BinaryReader(new MemoryStream(effectInfo));
            var kind = (ToolMessageKind) reader.ReadByte();
            var key = (playerObject.GetInstanceID(), reader.ReadByte());
            switch (kind) {
                case ToolMessageKind.Spawn:
                    Spawn(key, reader);
                    break;
                case ToolMessageKind.State:
                    ChangeState(key, reader);
                    break;
                case ToolMessageKind.Hit:
                    LandHit(key, reader.ReadByte());
                    break;
                case ToolMessageKind.Motion:
                    Move(key, reader);
                    break;
                case ToolMessageKind.Break:
                    if (Copies.TryGetValue(key, out var broken) && broken != null) {
                        ToolCopyRules.GetState(broken.PrefabName)?.Break(broken.gameObject);
                    }

                    break;
                case ToolMessageKind.End:
                    if (Copies.TryGetValue(key, out var copy) && copy != null) {
                        Object.Destroy(copy.gameObject);
                    }

                    Copies.Remove(key);
                    break;
            }
        } catch (IOException) {
            Logger.Warn("Could not read a message about a tool of the partner");
        } catch (Exception e) {
            Logger.Warn($"Could not show a tool of the partner: {e.Message}");
        }
    }

    /// <summary>
    /// Makes the copy of a thing that the partner threw.
    /// </summary>
    private static void Spawn((int, byte) key, BinaryReader reader) {
        var spawn = new ToolSpawn {
            PrefabName = reader.ReadString(),
            Poisoned = reader.ReadBoolean(),
            Scale = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
            Snapshot = ToolSnapshot.Read(reader)
        };
        spawn.Extra = reader.ReadBytes(reader.ReadByte());

        var prefab = ToolCopies.FindPrefab(spawn.PrefabName);
        if (prefab == null) {
            Logger.Warn($"There is no tool thing '{spawn.PrefabName}' to show for the partner");
            return;
        }

        // A number comes round again only long after the thing that had it is gone
        if (Copies.TryGetValue(key, out var old) && old != null) {
            Object.Destroy(old.gameObject);
        }

        var copy = Object.Instantiate(
            ToolCopies.GetCopyPrefab(prefab),
            spawn.Snapshot.Position,
            Quaternion.Euler(0f, 0f, spawn.Snapshot.Rotation)
        );
        copy.name = prefab.name;
        copy.transform.localScale = new Vector3(spawn.Scale.x, spawn.Scale.y, prefab.transform.localScale.z);

        var marker = copy.AddComponentIfNotPresent<RemoteToolCopy>();
        marker.Poisoned = spawn.Poisoned;
        marker.FromThrower = true;
        marker.PrefabName = spawn.PrefabName;
        marker.Destroyed = gone => {
            if (Copies.TryGetValue(key, out var current) && current == gone) {
                Copies.Remove(key);
            }
        };

        copy.SetActive(true);
        ToolCopies.PrepareCopy(copy, spawn.Poisoned);
        ToolCopies.LeaveToThrower(copy, spawn.PrefabName, marker);
        spawn.Snapshot.ApplyTo(copy);
        if (spawn.Extra.Length > 0) {
            ToolCopyRules.GetState(spawn.PrefabName)?.Apply(copy, spawn.Extra);
        }

        Copies[key] = marker;
    }

    /// <summary>
    /// Changes the state of a state machine of a copy the way it changed for the thrower's thing, and puts the copy
    /// where the thing was after it.
    /// </summary>
    private static void ChangeState((int, byte) key, BinaryReader reader) {
        var fsmIndex = reader.ReadByte();
        var state = reader.ReadString();
        var snapshot = ToolSnapshot.Read(reader);

        if (!Copies.TryGetValue(key, out var copy) || copy == null) {
            return;
        }

        var fsms = copy.GetComponentsInChildren<PlayMakerFSM>(true);
        if (fsmIndex >= fsms.Length || !fsms[fsmIndex].isActiveAndEnabled || fsms[fsmIndex].Fsm == null) {
            return;
        }

        fsms[fsmIndex].Fsm.SetState(state);
        snapshot.ApplyTo(copy.gameObject);
    }

    /// <summary>
    /// Puts a copy of a thing that its own code moves where the thrower's thing was and in its state.
    /// </summary>
    private static void Move((int, byte) key, BinaryReader reader) {
        var snapshot = ToolSnapshot.Read(reader);
        var state = reader.ReadBytes(reader.ReadByte());

        if (!Copies.TryGetValue(key, out var copy) || copy == null) {
            return;
        }

        snapshot.ApplyTo(copy.gameObject);
        ToolCopyRules.GetState(copy.PrefabName)?.Apply(copy.gameObject, state);
    }

    /// <summary>
    /// Has a damager of a copy react to a hit that the thrower's thing landed.
    /// </summary>
    private static void LandHit((int, byte) key, byte damagerIndex) {
        if (!Copies.TryGetValue(key, out var copy) || copy == null) {
            return;
        }

        var damagers = copy.GetComponentsInChildren<DamageEnemies>(true);
        if (damagerIndex < damagers.Length) {
            ToolCopies.LandHit(damagers[damagerIndex]);
        }
    }

    /// <summary>
    /// Takes away the copies of the things of a player who leaves the room, as their things are gone with the room
    /// they were in.
    /// </summary>
    /// <param name="playerObject">The character of the player.</param>
    public static void RemoveCopies(GameObject playerObject) {
        var player = playerObject.GetInstanceID();
        List<(int, byte)>? gone = null;
        foreach (var pair in Copies) {
            if (pair.Key.Player != player) {
                continue;
            }

            if (pair.Value != null) {
                Object.Destroy(pair.Value.gameObject);
            }

            (gone ??= []).Add(pair.Key);
        }

        if (gone == null) {
            return;
        }

        foreach (var key in gone) {
            Copies.Remove(key);
        }
    }

    /// <inheritdoc/>
    public override byte[] GetEffectInfo() {
        return [];
    }

    /// <summary>
    /// Marks a copy as the partner's, with hits that play on enemies without harming them.
    /// </summary>
    public static void MarkRemote(GameObject copy) {
        FixRemoteAttack(copy);
    }
}
