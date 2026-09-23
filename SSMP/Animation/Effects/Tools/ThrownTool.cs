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
                    Spawn(playerObject, key, reader);
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
                    var how = reader.ReadByte();
                    if (Copies.TryGetValue(key, out var broken) && broken != null) {
                        ToolCopyRules.GetState(broken.PrefabName)?.Break(broken.gameObject, how);
                    }

                    break;
                case ToolMessageKind.End:
                    if (Copies.TryGetValue(key, out var copy) && copy != null) {
                        Object.Destroy(copy.gameObject);
                    }

                    Copies.Remove(key);
                    break;
                case ToolMessageKind.Beam:
                    BeamShot.Play(playerObject, reader);
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
    private static void Spawn(GameObject playerObject, (int, byte) key, BinaryReader reader) {
        var spawn = ToolMessages.ReadSpawn(reader);

        // A thing whose state goes all the time sends this again and again, for a copy that is already there
        if (Copies.TryGetValue(key, out var old) && old != null && old.PrefabName == spawn.PrefabName) {
            spawn.Snapshot.ApplyTo(old.gameObject);
            var scale = old.transform.localScale;
            old.transform.localScale = new Vector3(spawn.Scale.x, spawn.Scale.y, scale.z);
            if (spawn.Extra.Length > 0) {
                ToolCopyRules.GetState(spawn.PrefabName)?.Apply(old.gameObject, spawn.Extra);
            }

            return;
        }

        var prefab = ToolCopies.FindPrefab(spawn.PrefabName);
        if (prefab == null) {
            Logger.Warn($"There is no tool thing '{spawn.PrefabName}' to show for the partner");
            return;
        }

        // A number comes round again only long after the thing that had it is gone
        if (old != null) {
            Object.Destroy(old.gameObject);
        }

        var copy = Object.Instantiate(
            ToolCopies.GetCopyPrefab(prefab),
            new Vector3(spawn.Snapshot.Position.x, spawn.Snapshot.Position.y, spawn.Z),
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
        var state = ToolCopyRules.GetState(spawn.PrefabName);
        state?.PrepareCopy(copy, spawn.Poisoned);
        ToolCopies.LeaveToThrower(copy, spawn.PrefabName, marker);
        spawn.Snapshot.ApplyTo(copy);
        if (spawn.Extra.Length > 0) {
            state?.Apply(copy, spawn.Extra);
        }

        if (spawn.Follows) {
            copy.AddComponent<FollowPlayer>().Follow(playerObject.transform, spawn.Offset);
        }

        Copies[key] = marker;
    }

    /// <summary>
    /// Changes the state of a state machine of a copy the way it changed for the thrower's thing, with the numbers the
    /// thrower's had, and puts the copy where the thing was after it.
    /// </summary>
    private static void ChangeState((int, byte) key, BinaryReader reader) {
        var fsmIndex = reader.ReadByte();
        var state = reader.ReadString();
        var snapshot = ToolSnapshot.Read(reader);
        var floats = new float[reader.ReadByte()];
        for (var i = 0; i < floats.Length; i++) {
            floats[i] = reader.ReadSingle();
        }

        if (!Copies.TryGetValue(key, out var copy) || copy == null) {
            return;
        }

        var fsms = copy.GetComponentsInChildren<PlayMakerFSM>(true);
        if (fsmIndex >= fsms.Length || !fsms[fsmIndex].isActiveAndEnabled || fsms[fsmIndex].Fsm == null) {
            return;
        }

        var fsm = fsms[fsmIndex];
        var names = ToolCopyRules.GetCarriedFloats(copy.PrefabName, fsm.FsmName);
        for (var i = 0; i < names.Length && i < floats.Length; i++) {
            var variable = fsm.FsmVariables.FindFsmFloat(names[i]);
            if (variable != null) {
                variable.Value = floats[i];
            }
        }

        fsm.Fsm.SetState(state);
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

/// <summary>
/// Keeps a copy of a thing that stays with the hero of the thrower, like an effect around them, with the character of
/// the thrower here.
/// </summary>
internal class FollowPlayer : MonoBehaviour {
    /// <summary>
    /// The character of the thrower.
    /// </summary>
    private Transform? _target;

    /// <summary>
    /// Where the thing is from the character.
    /// </summary>
    private Vector2 _offset;

    /// <summary>
    /// Puts the thing where it is from the character, and keeps it there.
    /// </summary>
    public void Follow(Transform target, Vector2 offset) {
        _target = target;
        _offset = offset;
        Move();
    }

    private void LateUpdate() {
        Move();
    }

    private void Move() {
        if (_target == null) {
            return;
        }

        var position = _target.position;
        transform.position = new Vector3(position.x + _offset.x, position.y + _offset.y, transform.position.z);
    }
}
