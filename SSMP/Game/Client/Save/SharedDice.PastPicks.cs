using System.Collections.Generic;
using System.IO;
using System.Linq;
using HutongGames.PlayMaker.Actions;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// What the picks at random of a creature that every game runs by itself keep of their past picks, which goes with each
/// step of the creature as its rolls do (see CoopSave.OwnCreatures). SendRandomEventV4 weighs its pick by how many
/// times in a row each way came and how many times it did not, which it keeps for itself in each game: from the same
/// roll, a game whose counts came apart from the scene host's picked otherwise, until both happened to take, at the
/// same pick, a way that starts the counts over. The map seller's pick whether to charge came out otherwise in the
/// user's game than in the scene host's 13 times in the spars of 11-21, each time to be set into the scene host's move
/// (21:30:02.828, 8.3 away): the user's game had gone past the pick straight 61 times there, set to a later step of
/// the scene host's that came as it was a moment behind (0.5.17, before the kept steps of a0d1558). A member who comes
/// into the room, or back into it, starts with none, while the scene host's counted on.
///
/// A creature that is put to a step of another game goes on from what the picks of that game kept, and picks by itself
/// as before, from the same roll. A wait for the scene host's pick, tried first, held the knight in its pick of a move
/// for a round trip after a blow here, which takes it there from where it stands (USER 11-22
/// "感觉不需要一刀切直接完全听房主？因为这种其实没有那么会乱？", "我觉得可以，多发一个校准没有问题").
/// </summary>
internal static partial class SharedDice {
    /// <summary>
    /// The picks at random by their past picks of each state machine that started whose steps were written or put here,
    /// in the order of its states and of their actions, the same in every game.
    /// </summary>
    private static readonly Dictionary<HutongGames.PlayMaker.Fsm, SendRandomEventV4[]> PastPickers = new();

    /// <summary>
    /// Writes what the picks at random of a state machine kept of their past picks (see <see cref="PutPastPicks"/>):
    /// for each, whether it picked yet, and if so how many times in a row each of its ways came, then how many times
    /// each did not.
    /// </summary>
    public static void WritePastPicks(BinaryWriter writer, HutongGames.PlayMaker.Fsm fsm) {
        var picks = PastPickersOf(fsm);
        WriteCount(writer, picks.Length);
        foreach (var pick in picks) {
            writer.Write(pick.setupArrays);
            if (pick.setupArrays) {
                WriteCounts(writer, pick.trackingInts);
                WriteCounts(writer, pick.trackingIntsMissed);
            }
        }
    }

    /// <summary>
    /// Has the picks at random of a state machine go on from what the same picks of another game kept of their past
    /// picks, as that game wrote them with a step that this one is put to (see <see cref="WritePastPicks"/>).
    /// </summary>
    public static void PutPastPicks(BinaryReader reader, HutongGames.PlayMaker.Fsm fsm) {
        var picks = PastPickersOf(fsm);
        var count = ReadCount(reader);
        for (var i = 0; i < count; i++) {
            var setUp = reader.ReadBoolean();
            int[] inARow = setUp ? ReadCounts(reader) : [];
            int[] missed = setUp ? ReadCounts(reader) : [];

            // One that has not started here has none to go on from yet (see PastPickersOf)
            if (i >= picks.Length) {
                continue;
            }

            var pick = picks[i];
            if (pick.setupArrays == setUp && (!setUp || pick.trackingInts.SequenceEqual(inARow) &&
                                              pick.trackingIntsMissed.SequenceEqual(missed))) {
                continue;
            }

            Logger.Info(
                $"[Own creatures] '{Entity.OwnCreatures.PlaceOf(fsm.GameObject)}' picks at '{pick.State.Name}' of " +
                $"'{fsm.Name}' from the past picks there in the game whose step it is put to, " +
                $"{PastOf(setUp, inARow, missed)}, rather than its own, " +
                PastOf(pick.setupArrays, pick.trackingInts, pick.trackingIntsMissed)
            );
            pick.setupArrays = setUp;
            if (setUp) {
                pick.trackingInts = inARow;
                pick.trackingIntsMissed = missed;
            }
        }
    }

    /// <summary>
    /// The picks at random by their past picks of a state machine (see <see cref="PastPickers"/>), none for one that
    /// has not started: it picked nothing yet, and one made from a template holds a stand-in of its own until then,
    /// whose states have no actions to ask for (see AddRollers).
    /// </summary>
    private static SendRandomEventV4[] PastPickersOf(HutongGames.PlayMaker.Fsm fsm) {
        if (!fsm.Started) {
            return [];
        }

        if (!PastPickers.TryGetValue(fsm, out var picks)) {
            picks = fsm.States.SelectMany(state => state.Actions).OfType<SendRandomEventV4>().ToArray();
            PastPickers[fsm] = picks;
        }

        return picks;
    }

    /// <summary>
    /// What a pick at random kept of its past picks, for the log.
    /// </summary>
    private static string PastOf(bool setUp, int[] inARow, int[] missed) {
        return setUp ? $"in a row {string.Join(", ", inARow)}, not {string.Join(", ", missed)}" : "none yet";
    }

    /// <summary>
    /// Writes a row of counts, with how many there are (see <see cref="WriteCount"/>).
    /// </summary>
    private static void WriteCounts(BinaryWriter writer, int[] counts) {
        WriteCount(writer, counts.Length);
        foreach (var count in counts) {
            WriteCount(writer, count);
        }
    }

    /// <summary>
    /// Reads a row of counts (see <see cref="WriteCounts"/>).
    /// </summary>
    private static int[] ReadCounts(BinaryReader reader) {
        var counts = new int[ReadCount(reader)];
        for (var i = 0; i < counts.Length; i++) {
            counts[i] = ReadCount(reader);
        }

        return counts;
    }

    /// <summary>
    /// Writes a count, none or more, in as few bytes as it takes: seven bits in each, all but the last with the eighth
    /// bit set. A count of past picks is below 128, and takes one.
    /// </summary>
    private static void WriteCount(BinaryWriter writer, int count) {
        var rest = (uint) count;
        while (rest >= 0x80) {
            writer.Write((byte) (rest | 0x80));
            rest >>= 7;
        }

        writer.Write((byte) rest);
    }

    /// <summary>
    /// Reads a count (see <see cref="WriteCount"/>).
    /// </summary>
    private static int ReadCount(BinaryReader reader) {
        var count = 0;
        for (var shift = 0;; shift += 7) {
            var part = reader.ReadByte();
            count |= (part & 0x7F) << shift;
            if (part < 0x80) {
                return count;
            }
        }
    }
}
