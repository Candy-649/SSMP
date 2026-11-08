using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using SSMP.Networking.Packet;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The tables of seeds that go with the names that things roll under (USER 10-22 "不可以分开掷骰子…一次性把能投的都投了，
/// 但是同时用同一个种子的骰子兜底"). The game that names a thing for a word to the other games - a trap that went off, a
/// round of a machine, a part that a controller or a creature told, a hit on an object of the room, a thing that a
/// creature threw, a cluster of Rune Rage - rolls with its own dice, as it names it, a seed for each of the first uses of
/// each action of the thing that rolls, and for its first bounces, and sends them with the name. Every game rolls those
/// uses from those seeds, so what the thing does by the dice comes from the dice of one game. A use past the table, or
/// of an action that it did not foresee, every game rolls from the name, as before (see <see cref="RollByName"/>), and
/// so does what was named in every game alike without a word: what a named thing tells or throws in turn, the flyers of a
/// flock.
/// </summary>
internal static partial class SharedDice {
    /// <summary>
    /// The tables that came with names that nothing here was given yet, and those that this game rolled for names that
    /// it gives later, like the things a creature throws one by one while a state lasts, by the name, until something
    /// is given the name or the room is left. The clusters of the partner's Rune Rage are played from theirs without
    /// being given the name (see <see cref="RollAs{T}"/>).
    /// </summary>
    private static readonly Dictionary<string, SeedTable> WaitingTables = new(StringComparer.Ordinal);

    /// <summary>
    /// The kinds of thing that were said in the log in this room to roll from a table, by their names without
    /// "(Clone)".
    /// </summary>
    private static readonly HashSet<string> SaidTables = new(StringComparer.Ordinal);

    /// <summary>
    /// The kinds of thing that were said in the log in this room to roll past their tables, by their names without
    /// "(Clone)".
    /// </summary>
    private static readonly HashSet<string> SaidPastTables = new(StringComparer.Ordinal);

    /// <summary>
    /// Has what is given the name next here roll from a table (see <see cref="RollByName"/>).
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="table">The table.</param>
    public static void Expect(string name, SeedTable table) {
        WaitingTables[name] = table;
    }

    /// <summary>
    /// Takes the table that waits for a name, or null for none: one that came with the name, or that this game rolled
    /// for it before it gives it, like the things a creature throws one by one while a state lasts (see ThrownThings).
    /// </summary>
    public static SeedTable? TakeTable(string name) {
        return WaitingTables.Count > 0 && WaitingTables.Remove(name, out var table) ? table : null;
    }

    /// <summary>
    /// Whether a table waits for a name.
    /// </summary>
    public static bool HasTable(string name) {
        return WaitingTables.ContainsKey(name);
    }

    /// <summary>
    /// Rolls with this game's dice a table of seeds for what a thing is about to roll: for the actions that roll of the
    /// state machines on it, or on it and on everything under it, and for its bounces.
    /// </summary>
    /// <param name="thing">The thing: an object of the room, a thing that was thrown, or the prefab of one.</param>
    /// <param name="under">Whether the state machines under it roll under its name too, as those of a thing thrown
    /// do.</param>
    /// <param name="bounces">Whether its bounces take seeds too.</param>
    public static SeedTable RollTable(GameObject thing, bool under, bool bounces) {
        return SeedTable.Roll(RollersOf(thing, under), bounces);
    }

    /// <summary>
    /// The numbers of the actions that roll as they start (see <see cref="Identify(string, string, string, int)"/>) of the state machines on a
    /// thing, or on it and on everything under it.
    /// </summary>
    public static List<int> RollersOf(GameObject thing, bool under) {
        var rollers = new List<int>();
        var machines = under ? thing.GetComponentsInChildren<PlayMakerFSM>(true) : thing.GetComponents<PlayMakerFSM>();
        foreach (var machine in machines) {
            if (machine != null) {
                AddRollers(machine, rollers);
            }
        }

        return rollers;
    }

    /// <summary>
    /// Adds the numbers of the actions of a state machine that roll as they start (see <see cref="Identify(string, string, string, int)"/>), found
    /// without waking it: the state of one that hasn't woken has no actions yet, only their names (see
    /// <see cref="HasActions"/>), and one made from a template holds a stand-in of its own until it wakes and makes its
    /// states from the template, which it starts in after (see PlayMakerFSM.InitTemplate).
    /// </summary>
    private static void AddRollers(PlayMakerFSM machine, List<int> rollers) {
        var fsm = machine.FsmTemplate != null && !machine.Fsm.Started ? machine.FsmTemplate.fsm : machine.Fsm;
        var objectName = WithoutClone(machine.gameObject.name);
        var fsmName = machine.FsmName;
        foreach (var state in fsm.States) {
            if (HasActions(state)) {
                var actions = state.Actions;
                for (var place = 0; place < actions.Length; place++) {
                    if (actions[place] is { } action && RollingActionSet.Contains(action.GetType().Name)) {
                        rollers.Add(Identify(objectName, fsmName, state.Name, place));
                    }
                }

                continue;
            }

            if (state.ActionData?.ActionNames is not { } names) {
                continue;
            }

            for (var place = 0; place < names.Count; place++) {
                var typeName = names[place];
                if (RollingActionSet.Contains(typeName.Substring(typeName.LastIndexOf('.') + 1))) {
                    rollers.Add(Identify(objectName, fsmName, state.Name, place));
                }
            }
        }
    }

    /// <summary>
    /// The field of a state that holds its state machine, set once the machine is made.
    /// </summary>
    private static readonly FieldInfo StateFsmField =
        typeof(FsmState).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>
    /// Whether the actions of a state can be asked for: its state machine is made. Asked for them before, or for its
    /// state machine, the game writes an error to its log (FsmState.Actions, FsmState.Fsm), as for every state of a
    /// prefab that rolls, at every throw of a run and every cluster of Rune Rage.
    /// </summary>
    private static bool HasActions(FsmState state) {
        return StateFsmField.GetValue(state) != null;
    }

    /// <summary>
    /// The name of an object without "(Clone)" on the end, as many times as it is there: the copy of something spawned
    /// has it twice.
    /// </summary>
    private static string WithoutClone(string name) {
        const string clone = "(Clone)";
        while (name.EndsWith(clone, StringComparison.Ordinal)) {
            name = name[..^clone.Length];
        }

        return name;
    }

    /// <summary>
    /// The seed for a use of an action of something that rolls under a name: from its table, or from the name past the
    /// table. The first use past the table is said in the log, once for each kind of thing in the room.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="table">The table, or null for none.</param>
    /// <param name="roller">The number of the action (see <see cref="Identify(string, string, string, int)"/>).</param>
    /// <param name="times">How many times the action rolled under the name before.</param>
    /// <param name="thing">The object that rolls, for the log.</param>
    public static int SeedFor(string name, SeedTable? table, int roller, int times, GameObject thing) {
        if (table == null) {
            return ThrownThings.Seed(name, roller, times);
        }

        if (table.TryGet(roller, times, out var seed)) {
            return seed;
        }

        if (SaidPastTables.Add(WithoutClone(thing.name))) {
            Logger.Info(
                $"[Dice] '{thing.name}' rolled where its table under '{name}' holds no seed (use {times + 1} of an " +
                "action), from the name"
            );
        }

        return ThrownThings.Seed(name, roller, times);
    }

    /// <summary>
    /// Says in the log, once for each kind of thing in the room, that it rolls from a table, and whose.
    /// </summary>
    /// <param name="thing">The thing.</param>
    /// <param name="name">The name it rolls under.</param>
    /// <param name="table">The table.</param>
    /// <param name="rolledHere">Whether this game rolled the table as it named the thing, rather than taking one that
    /// waited for the name: one that came with the partner's word, or that this game rolled for a run of throws.
    /// </param>
    public static void SayTable(GameObject thing, string name, SeedTable table, bool rolledHere) {
        if (!SaidTables.Add(WithoutClone(thing.name))) {
            return;
        }

        Logger.Info(
            $"[Dice] '{thing.name}' rolls under '{name}' from the seeds " +
            (rolledHere ? "that this game rolled as it named it" : "that waited for its name") +
            $", for {table.Count} of its actions"
        );
    }

    /// <summary>
    /// Forgets the tables, as the room is left.
    /// </summary>
    private static void ForgetTables() {
        WaitingTables.Clear();
        SaidTables.Clear();
        SaidPastTables.Clear();
    }
}

/// <summary>
/// The seeds that one game rolled for a thing as it named it, by the action that rolls: one for each of its first uses,
/// and for the first bounces of a thing thrown (see <see cref="SharedDice"/>).
/// </summary>
internal sealed class SeedTable {
    /// <summary>
    /// How many uses of each action that rolls have a seed.
    /// </summary>
    private const int Uses = 4;

    /// <summary>
    /// How many bounces have a seed.
    /// </summary>
    private const int Bounces = 8;

    /// <summary>
    /// The most actions that one table holds, which keeps what goes with a name small: the actions past them roll from
    /// the name.
    /// </summary>
    private const int MaxActions = 48;

    /// <summary>
    /// The dice that the seeds are rolled with, apart from the game's own (<see cref="UnityEngine.Random"/>), which the
    /// game goes on rolling for everything else as it would.
    /// </summary>
    private static readonly System.Random Dice = new();

    /// <summary>
    /// The seeds of each action, by its number, in the order of its uses.
    /// </summary>
    private readonly Dictionary<int, int[]> _seeds;

    private SeedTable(Dictionary<int, int[]> seeds) {
        _seeds = seeds;
    }

    /// <summary>
    /// How many actions the table holds seeds for.
    /// </summary>
    public int Count => _seeds.Count;

    /// <summary>
    /// Rolls the seeds of a table.
    /// </summary>
    /// <param name="rollers">The numbers of the actions that roll, the same in every game.</param>
    /// <param name="bounces">Whether the thing's bounces take seeds too.</param>
    public static SeedTable Roll(List<int> rollers, bool bounces) {
        var seeds = new Dictionary<int, int[]>();
        if (bounces) {
            seeds[ThrownMark.BounceRoller] = RollSeeds(Bounces);
        }

        foreach (var roller in rollers) {
            if (seeds.Count >= MaxActions) {
                break;
            }

            if (!seeds.ContainsKey(roller)) {
                seeds[roller] = RollSeeds(Uses);
            }
        }

        return new SeedTable(seeds);
    }

    /// <summary>
    /// Rolls the given number of seeds.
    /// </summary>
    private static int[] RollSeeds(int count) {
        var seeds = new int[count];
        for (var i = 0; i < count; i++) {
            seeds[i] = Dice.Next(int.MinValue, int.MaxValue);
        }

        return seeds;
    }

    /// <summary>
    /// The seed for a use of an action, if the table holds one.
    /// </summary>
    /// <param name="roller">The number of the action.</param>
    /// <param name="times">How many times it rolled under the name before.</param>
    /// <param name="seed">The seed.</param>
    public bool TryGet(int roller, int times, out int seed) {
        seed = 0;
        if (!_seeds.TryGetValue(roller, out var seeds) || times >= seeds.Length) {
            return false;
        }

        seed = seeds[times];
        return true;
    }

    /// <summary>
    /// Writes the table for <see cref="Read"/>: how many actions, then each action's number, how many seeds and the
    /// seeds.
    /// </summary>
    public void Write(BinaryWriter writer) {
        writer.Write((byte) _seeds.Count);
        foreach (var pair in _seeds) {
            writer.Write(pair.Key);
            writer.Write((byte) pair.Value.Length);
            foreach (var seed in pair.Value) {
                writer.Write(seed);
            }
        }
    }

    /// <summary>
    /// Reads a table that <see cref="Write"/> wrote.
    /// </summary>
    public static SeedTable Read(BinaryReader reader) {
        var count = reader.ReadByte();
        var seeds = new Dictionary<int, int[]>(count);
        for (var i = 0; i < count; i++) {
            var roller = reader.ReadInt32();
            var row = new int[reader.ReadByte()];
            for (var use = 0; use < row.Length; use++) {
                row[use] = reader.ReadInt32();
            }

            seeds[roller] = row;
        }

        return new SeedTable(seeds);
    }
}

/// <summary>
/// The tables that a game rolled for the things it named for one word to the other games, by their names, to go with
/// the word: in the other games each waits for its name (see <see cref="SharedDice.Expect"/>).
/// </summary>
internal sealed class NamedSeeds {
    /// <summary>
    /// No tables, for a word that names nothing. Nothing is ever added to it.
    /// </summary>
    public static readonly NamedSeeds None = new();

    /// <summary>
    /// The names and their tables, in the order they were named.
    /// </summary>
    private readonly List<(string Name, SeedTable Table)> _tables = [];

    /// <summary>
    /// How many names have tables.
    /// </summary>
    public int Count => _tables.Count;

    /// <summary>
    /// Adds the table of a name, in place of one it had. A table that holds no seeds goes nowhere, as the many objects
    /// hit or told that roll nothing: every game rolls from the name alike.
    /// </summary>
    public void Add(string name, SeedTable table) {
        if (table.Count == 0) {
            return;
        }

        for (var i = 0; i < _tables.Count; i++) {
            if (_tables[i].Name == name) {
                _tables[i] = (name, table);
                return;
            }
        }

        _tables.Add((name, table));
    }

    /// <summary>
    /// Writes the tables for <see cref="Read(BinaryReader, string)"/>, each name without what the word already says.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="start">What the names start with that the word already says, left out of each that does.</param>
    public void Write(BinaryWriter writer, string start) {
        writer.Write((ushort) _tables.Count);
        foreach (var (name, table) in _tables) {
            var shortened = start.Length > 0 && name.StartsWith(start, StringComparison.Ordinal);
            writer.Write(shortened);
            writer.Write(shortened ? name.Substring(start.Length) : name);
            table.Write(writer);
        }
    }

    /// <summary>
    /// The tables as bytes (see <see cref="Write(BinaryWriter, string)"/>), or none for no tables.
    /// </summary>
    public byte[] ToBytes(string start = "") {
        if (_tables.Count == 0) {
            return [];
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        Write(writer, start);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Writes the tables into a packet, with how many bytes they are first.
    /// </summary>
    public void Write(IPacket packet, string start = "") {
        var bytes = ToBytes(start);
        packet.Write((ushort) bytes.Length);
        packet.Write(bytes);
    }

    /// <summary>
    /// Reads tables that <see cref="Write(BinaryWriter, string)"/> wrote, and has each wait here for its name.
    /// </summary>
    public static void Read(BinaryReader reader, string start) {
        var count = reader.ReadUInt16();
        for (var i = 0; i < count; i++) {
            var shortened = reader.ReadBoolean();
            var name = reader.ReadString();
            SharedDice.Expect(shortened ? start + name : name, SeedTable.Read(reader));
        }
    }

    /// <summary>
    /// Reads tables that <see cref="ToBytes"/> made, and has each wait here for its name.
    /// </summary>
    public static void Read(byte[] bytes, string start = "") {
        if (bytes.Length == 0) {
            return;
        }

        using var reader = new BinaryReader(new MemoryStream(bytes));
        Read(reader, start);
    }

    /// <summary>
    /// Reads tables that <see cref="Write(IPacket, string)"/> wrote into a packet, and has each wait here for its name.
    /// </summary>
    public static void Read(IPacket packet, string start = "") {
        var length = packet.ReadUShort();
        if (length > 0) {
            Read(packet.ReadBytes(length), start);
        }
    }
}
