using System;
using System.Collections.Generic;
using UnityEngine;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// Where the things that a tool of the local player threw went, sent with the tool's effect. The game rolls where tacks
/// scatter and at what angle pins fly, and the copies of them on the other screens, which the tools' effects make
/// rather than the game, rolled their own and threw some at other angles altogether. A copy is now put where the
/// thrower's thing went: as far from the player, moving the same way as fast, turned and sized the same.
/// </summary>
internal static class ToolPlacements {
    /// <summary>
    /// The states of the hero's tool FSM that throw the things whose copies the tools' effects make: the tacks, the
    /// three pins on the ground and in the air, and the long pin.
    /// </summary>
    private static readonly HashSet<string> ThrowingStates = [
        "Tacks L", "Tacks R", "TriPin Ground L", "TriPin Ground R", "TriPin Air L", "TriPin Air R", "Fisherpin"
    ];

    /// <summary>
    /// The things that the hero's tool FSM threw in the throw going on now.
    /// </summary>
    private static readonly List<GameObject> Thrown = [];

    /// <summary>
    /// How many bytes a placement takes: two for each of its seven numbers.
    /// </summary>
    private const int PlacementSize = 14;

    /// <summary>
    /// How finely each kind of number is sent, in parts of a unit, of a unit a second and of a degree.
    /// </summary>
    private const float PlaceScale = 1000f, SpeedScale = 100f, AngleScale = 50f, SizeScale = 1000f;

    /// <summary>
    /// Forgets what was noted, as a throw of the local player's tool begins and ends.
    /// </summary>
    public static void Clear() {
        Thrown.Clear();
    }

    /// <summary>
    /// Notes a thing that the hero's tool FSM spawned, if its state throws something whose copy an effect makes.
    /// </summary>
    public static void Note(GameObject spawned) {
        var hero = HeroController.SilentInstance;
        if (hero != null && hero.toolsFSM != null && ThrowingStates.Contains(hero.toolsFSM.ActiveStateName)) {
            Thrown.Add(spawned);
        }
    }

    /// <summary>
    /// The effect info of a tool followed by where the things that it threw went, which are then forgotten. The game
    /// throws them all and has them moving before it says that the tool was used, which is when the effect is sent.
    /// </summary>
    /// <param name="info">The effect info of the tool.</param>
    public static byte[] Append(byte[] info) {
        var hero = HeroController.SilentInstance;
        var things = new List<GameObject>(Thrown.Count);
        foreach (var thing in Thrown) {
            if (thing != null) {
                things.Add(thing);
            }
        }

        Thrown.Clear();
        var count = Mathf.Min(things.Count, (byte.MaxValue - info.Length - 1) / PlacementSize);
        if (hero == null || count == 0) {
            return info;
        }

        var result = new byte[info.Length + 1 + count * PlacementSize];
        Array.Copy(info, result, info.Length);
        var at = info.Length;
        result[at++] = (byte) count;

        var origin = hero.transform.position;
        for (var i = 0; i < count; i++) {
            var transform = things[i].transform;
            var velocity = things[i].TryGetComponent<Rigidbody2D>(out var body) ? body.linearVelocity : Vector2.zero;
            var position = transform.position;
            var scale = transform.localScale;
            Write(result, ref at, position.x - origin.x, PlaceScale);
            Write(result, ref at, position.y - origin.y, PlaceScale);
            Write(result, ref at, velocity.x, SpeedScale);
            Write(result, ref at, velocity.y, SpeedScale);
            Write(result, ref at, transform.eulerAngles.z, AngleScale);
            Write(result, ref at, scale.x, SizeScale);
            Write(result, ref at, scale.y, SizeScale);
        }

        return result;
    }

    /// <summary>
    /// Reads where the things of a throw went, from the effect info of the tool, or none if it says nothing of it.
    /// </summary>
    /// <param name="info">The effect info.</param>
    /// <param name="start">Where the placements begin in it, after the tool's own info.</param>
    public static Placement[] Read(byte[]? info, int start) {
        if (info == null || info.Length <= start) {
            return [];
        }

        var count = info[start];
        if (info.Length < start + 1 + count * PlacementSize) {
            return [];
        }

        var placements = new Placement[count];
        var at = start + 1;
        for (var i = 0; i < count; i++) {
            placements[i] = new Placement(
                new Vector2(Read(info, ref at, PlaceScale), Read(info, ref at, PlaceScale)),
                new Vector2(Read(info, ref at, SpeedScale), Read(info, ref at, SpeedScale)),
                Read(info, ref at, AngleScale),
                new Vector2(Read(info, ref at, SizeScale), Read(info, ref at, SizeScale))
            );
        }

        return placements;
    }

    /// <summary>
    /// Writes a number as two bytes, in parts of the given size.
    /// </summary>
    private static void Write(byte[] to, ref int at, float value, float scale) {
        var number = (short) Mathf.Clamp(Mathf.Round(value * scale), short.MinValue, short.MaxValue);
        to[at++] = (byte) number;
        to[at++] = (byte) (number >> 8);
    }

    /// <summary>
    /// Reads a number that <see cref="Write"/> wrote.
    /// </summary>
    private static float Read(byte[] from, ref int at, float scale) {
        var number = (short) (from[at] | from[at + 1] << 8);
        at += 2;
        return number / scale;
    }

    /// <summary>
    /// Where a thrown thing went: how far from the player, how it moved, its turn and its size.
    /// </summary>
    /// <param name="Offset">How far from the player it was.</param>
    /// <param name="Velocity">How it moved.</param>
    /// <param name="Angle">How far it was turned, in degrees.</param>
    /// <param name="Size">Its size.</param>
    public readonly record struct Placement(Vector2 Offset, Vector2 Velocity, float Angle, Vector2 Size) {
        /// <summary>
        /// Puts a copy where the thing was by the player, turned and sized as it was. Its depth stays as it is.
        /// </summary>
        /// <param name="thing">The copy.</param>
        /// <param name="player">Where the player who threw it is.</param>
        public void PutAt(Transform thing, Vector3 player) {
            thing.position = new Vector3(player.x + Offset.x, player.y + Offset.y, thing.position.z);
            thing.rotation = Quaternion.Euler(0f, 0f, Angle);
            thing.localScale = new Vector3(Size.x, Size.y, thing.localScale.z);
        }
    }
}
