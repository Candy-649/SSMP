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
/// Shows the tools that the partner throws, for the tools whose thing flies off by itself once thrown, like the barbed
/// shard. The copy starts where the thrower's own one was a moment after it left their hand, moving the way it moved,
/// and then runs as the game runs it: it flies, sticks, bursts on an enemy and breaks the way the thrower's does. It is
/// only there to be seen - see <see cref="ToolCopies"/> for all it is kept out of.
/// </summary>
internal class ThrownTool : BaseAttackTool {
    /// <summary>
    /// What the partner's tools do differently from the thrower's own, by the name of the tool, for the copies that
    /// need more than <see cref="ToolCopies.PrepareCopy"/>: the copy and the object of the player who threw it.
    /// </summary>
    private static readonly Dictionary<string, Action<GameObject, GameObject>> Fixups = new();

    /// <summary>
    /// The copies that are out, oldest first, for each player and tool, so that throwing one too many breaks the oldest
    /// one here as it does for the thrower.
    /// </summary>
    private static readonly Dictionary<(int, string), List<GameObject>> LiveCopies = new();

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        if (!ThrowInfo.TryRead(effectInfo, out var info)) {
            Logger.Warn("Could not read a tool that the partner threw");
            return;
        }

        if (ToolItemManager.GetToolByName(info.ToolName) is not ToolItem tool ||
            tool.Usage.ThrowPrefab is not { } prefab) {
            return;
        }

        try {
            var copy = Object.Instantiate(
                ToolCopies.GetCopyPrefab(prefab),
                info.Position,
                Quaternion.Euler(0f, 0f, info.Rotation)
            );
            copy.name = prefab.name;
            copy.transform.localScale = new Vector3(info.Scale.x, info.Scale.y, prefab.transform.localScale.z);
            copy.AddComponentIfNotPresent<RemoteToolCopy>().Poisoned = info.Poisoned;

            copy.SetActive(true);
            ToolCopies.PrepareCopy(copy, info.Poisoned);

            if (copy.TryGetComponent<Rigidbody2D>(out var body)) {
                body.linearVelocity = info.Velocity;
                body.angularVelocity = info.AngularVelocity;
            }

            if (Fixups.TryGetValue(tool.name, out var fixup)) {
                fixup(copy, playerObject);
            }

            KeepCount(playerObject, tool.name, copy, info.MaxActive);
        } catch (Exception e) {
            Logger.Warn($"Could not show the '{info.ToolName}' that the partner threw: {e.Message}");
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

    /// <summary>
    /// Adds a copy to the ones of its player and tool, breaking the oldest ones while there are more than the thrower's
    /// game lets be out at once.
    /// </summary>
    private static void KeepCount(GameObject playerObject, string toolName, GameObject copy, int maxActive) {
        var key = (playerObject.GetInstanceID(), toolName);
        if (!LiveCopies.TryGetValue(key, out var copies)) {
            copies = [];
            LiveCopies[key] = copies;
        }

        // A copy is gone once it breaks, as nothing pools them
        copies.RemoveAll(existing => existing == null);
        copies.Add(copy);

        while (maxActive > 0 && copies.Count > maxActive) {
            var oldest = copies[0];
            copies.RemoveAt(0);
            ToolCopies.BreakCopy(oldest);
        }
    }
}

/// <summary>
/// What a tool that a player threw looked like a moment after it left their hand: which tool, what the thrower had on
/// that changes it, and where it was and how it moved.
/// </summary>
internal struct ThrowInfo {
    /// <summary>
    /// The name of the tool.
    /// </summary>
    public string ToolName;

    /// <summary>
    /// Whether the thrower has the pouch that poisons their tools.
    /// </summary>
    public bool Poisoned;

    /// <summary>
    /// How many of this tool the thrower's game lets be out at once, or 0 for any number.
    /// </summary>
    public int MaxActive;

    /// <summary>
    /// Where the thing was.
    /// </summary>
    public Vector2 Position;

    /// <summary>
    /// How it was turned, in degrees.
    /// </summary>
    public float Rotation;

    /// <summary>
    /// Its scale, which is what faces it one way or the other.
    /// </summary>
    public Vector2 Scale;

    /// <summary>
    /// How fast it moved.
    /// </summary>
    public Vector2 Velocity;

    /// <summary>
    /// How fast it spun, in degrees per second.
    /// </summary>
    public float AngularVelocity;

    /// <summary>
    /// Writes the throw to bytes for the effect info of an animation.
    /// </summary>
    public byte[] Write() {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(ToolName);
        writer.Write(Poisoned);
        writer.Write((byte) Mathf.Clamp(MaxActive, 0, byte.MaxValue));
        writer.Write(Position.x);
        writer.Write(Position.y);
        writer.Write(Rotation);
        writer.Write(Scale.x);
        writer.Write(Scale.y);
        writer.Write(Velocity.x);
        writer.Write(Velocity.y);
        writer.Write(AngularVelocity);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Reads a throw that <see cref="Write"/> wrote.
    /// </summary>
    /// <returns>Whether it could be read.</returns>
    public static bool TryRead(byte[]? data, out ThrowInfo info) {
        info = default;
        if (data == null) {
            return false;
        }

        try {
            using var reader = new BinaryReader(new MemoryStream(data));
            info.ToolName = reader.ReadString();
            info.Poisoned = reader.ReadBoolean();
            info.MaxActive = reader.ReadByte();
            info.Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            info.Rotation = reader.ReadSingle();
            info.Scale = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            info.Velocity = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            info.AngularVelocity = reader.ReadSingle();
            return true;
        } catch (IOException) {
            return false;
        }
    }
}
