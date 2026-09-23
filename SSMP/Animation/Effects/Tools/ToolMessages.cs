using System.IO;
using UnityEngine;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// The kinds of the messages about a thing of a tool that go from the thrower's game to the partner.
/// </summary>
internal enum ToolMessageKind : byte {
    /// <summary>
    /// The thing is on its way: what it is and how it moves, a moment after it came out.
    /// </summary>
    Spawn,

    /// <summary>
    /// A state machine of the thing changed state for a reason that only the thrower's game knows of.
    /// </summary>
    State,

    /// <summary>
    /// A damager of the thing landed a hit on an enemy, which the copy of the thing reacts to as the thing does.
    /// </summary>
    Hit,

    /// <summary>
    /// The thing broke by what only the thrower's game knows of.
    /// </summary>
    Break,

    /// <summary>
    /// A thing that its own code moves was changed by what only the thrower's game knows of, with its state after it.
    /// </summary>
    Motion,

    /// <summary>
    /// The thing is gone.
    /// </summary>
    End
}

/// <summary>
/// Where a thing is and how it moves at one moment.
/// </summary>
internal struct ToolSnapshot {
    /// <summary>
    /// Where the thing is.
    /// </summary>
    public Vector2 Position;

    /// <summary>
    /// How it is turned, in degrees.
    /// </summary>
    public float Rotation;

    /// <summary>
    /// How fast it moves.
    /// </summary>
    public Vector2 Velocity;

    /// <summary>
    /// How fast it spins, in degrees per second.
    /// </summary>
    public float AngularVelocity;

    /// <summary>
    /// Takes a snapshot of a thing now.
    /// </summary>
    public static ToolSnapshot Of(GameObject thing) {
        var transform = thing.transform;
        var body = thing.GetComponent<Rigidbody2D>();
        return new ToolSnapshot {
            Position = transform.position,
            Rotation = transform.eulerAngles.z,
            Velocity = body != null ? body.linearVelocity : Vector2.zero,
            AngularVelocity = body != null ? body.angularVelocity : 0f
        };
    }

    /// <summary>
    /// Puts a thing where the snapshot has it and moving the way it moved.
    /// </summary>
    public void ApplyTo(GameObject thing) {
        var transform = thing.transform;
        transform.position = new Vector3(Position.x, Position.y, transform.position.z);
        transform.rotation = Quaternion.Euler(0f, 0f, Rotation);

        if (thing.TryGetComponent<Rigidbody2D>(out var body)) {
            body.position = Position;
            body.rotation = Rotation;
            body.linearVelocity = Velocity;
            body.angularVelocity = AngularVelocity;
        }
    }

    /// <summary>
    /// Writes the snapshot.
    /// </summary>
    public void Write(BinaryWriter writer) {
        writer.Write(Position.x);
        writer.Write(Position.y);
        writer.Write(Rotation);
        writer.Write(Velocity.x);
        writer.Write(Velocity.y);
        writer.Write(AngularVelocity);
    }

    /// <summary>
    /// Reads a snapshot that <see cref="Write"/> wrote.
    /// </summary>
    public static ToolSnapshot Read(BinaryReader reader) {
        return new ToolSnapshot {
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
            Rotation = reader.ReadSingle(),
            Velocity = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
            AngularVelocity = reader.ReadSingle()
        };
    }
}

/// <summary>
/// A thing of a tool that is on its way, as the thrower's game sends it.
/// </summary>
internal struct ToolSpawn {
    /// <summary>
    /// The name of the prefab of the thing.
    /// </summary>
    public string PrefabName;

    /// <summary>
    /// Whether the thrower has the pouch that poisons their tools.
    /// </summary>
    public bool Poisoned;

    /// <summary>
    /// Its scale, which is what faces it one way or the other.
    /// </summary>
    public Vector2 Scale;

    /// <summary>
    /// Where it is and how it moves.
    /// </summary>
    public ToolSnapshot Snapshot;

    /// <summary>
    /// What else a copy of this kind of thing needs to start the way it started, or empty.
    /// </summary>
    public byte[] Extra;
}

/// <summary>
/// Writes and reads the messages about the things of tools.
/// </summary>
internal static class ToolMessages {
    /// <summary>
    /// Writes a message that a thing is on its way.
    /// </summary>
    public static byte[] WriteSpawn(byte id, ToolSpawn spawn) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte) ToolMessageKind.Spawn);
        writer.Write(id);
        writer.Write(spawn.PrefabName);
        writer.Write(spawn.Poisoned);
        writer.Write(spawn.Scale.x);
        writer.Write(spawn.Scale.y);
        spawn.Snapshot.Write(writer);
        writer.Write((byte) spawn.Extra.Length);
        writer.Write(spawn.Extra);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Writes a message that a state machine of a thing changed state.
    /// </summary>
    public static byte[] WriteState(byte id, byte fsmIndex, string state, ToolSnapshot snapshot) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte) ToolMessageKind.State);
        writer.Write(id);
        writer.Write(fsmIndex);
        writer.Write(state);
        snapshot.Write(writer);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Writes a message that a thing that its own code moves was changed, with its state after it.
    /// </summary>
    public static byte[] WriteMotion(byte id, ToolSnapshot snapshot, byte[] state) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte) ToolMessageKind.Motion);
        writer.Write(id);
        snapshot.Write(writer);
        writer.Write((byte) state.Length);
        writer.Write(state);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Writes a message that only says something happened to a thing, or to one of its parts.
    /// </summary>
    public static byte[] WriteSimple(ToolMessageKind kind, byte id, byte part = 0) {
        return [(byte) kind, id, part];
    }
}
