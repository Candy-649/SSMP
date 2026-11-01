using System;
using System.Diagnostics;
using System.Threading;
using SSMP.Util;

namespace SSMP.Networking;

/// <summary>
/// Tells the smoothing of positions (PredictiveInterpolation) whether what the other players' games send is getting
/// through. A thing stops being sent when it stops moving, and nothing at all comes while the connection is quiet; to
/// the thing itself the two look the same, and only the packets around it tell them apart: when they keep coming and
/// none of them moves it, it stopped, and when none come, it is most likely still going.
///
/// The packets counted are the ones that bring what the other players' games send: in a game that joined, those from
/// the server; in the game that hosts the server, those the server takes in from the other players, since its own
/// packets on to this game never stop or get lost, however quiet the others are.
///
/// The count is kept on the main thread, where the contents of the packets are handled, and goes up as each packet's
/// turn comes there, so a position and the packet that brought it are counted together. In the game that hosts, a
/// packet from another player is counted as the server takes it in, a little before what it brought comes on to this
/// game.
///
/// With three or more players, the game that hosts hears all the others together, so one of them going quiet while
/// another is heard counts as heard, and a game that joined hears the server either way.
/// </summary>
internal static class LinkWatch {
    /// <summary>
    /// How long nothing has to come in for the connection to count as having gone quiet, in seconds.
    ///
    /// Packets are sent about every 17 ms and read about as often, and a read that finds nothing is ordinary: 34 ms
    /// between two packets is nothing, while 51 ms is two that didn't come.
    /// </summary>
    private const double QuietAfter = 0.04;

    /// <summary>
    /// <see cref="QuietAfter"/> in the ticks of <see cref="Stopwatch"/>.
    /// </summary>
    private static readonly long QuietTicks = (long) (QuietAfter * Stopwatch.Frequency);

    /// <summary>
    /// How many of the usual gaps between two packets have to pass for the connection to count as gone quiet, when
    /// that is longer than <see cref="QuietAfter"/>: the sender sends a packet only every 50 ms on a slow link
    /// (CongestionManager), and the connection went quiet between any two packets then, so that a copy never stopped.
    /// </summary>
    private const double QuietGaps = 2.5;

    /// <summary>
    /// The longest a gap between two packets counts for the usual gap, in the ticks of <see cref="Stopwatch"/>: a long
    /// quiet stretch lengthens the usual gap no more than a gap of 100 ms would.
    /// </summary>
    private static readonly long LongestLearntGap = (long) (0.1 * Stopwatch.Frequency);

    #region Network threads

    /// <summary>
    /// Whether this game runs the server.
    /// </summary>
    private static volatile bool _hosting;

    /// <summary>
    /// When the last packet from the server came in, in the ticks of <see cref="Stopwatch"/>. The client's receive
    /// thread only.
    /// </summary>
    private static long _lastFromServer;

    /// <summary>
    /// The packets the server in this game took in from the other players' games. The server's thread adds to it.
    /// </summary>
    private static int _fromOtherPlayers;

    /// <summary>
    /// When the last of those came in, in the ticks of <see cref="Stopwatch"/>. The server's thread only.
    /// </summary>
    private static long _lastFromOtherPlayers;

    /// <summary>
    /// How many times what the other players send came in again after going quiet, as the server took it in. The
    /// server's thread adds to it.
    /// </summary>
    private static int _othersCameBack;

    /// <summary>
    /// The usual gap between two packets from the server, in the ticks of <see cref="Stopwatch"/>. The client's
    /// receive thread only.
    /// </summary>
    private static long _usualGapFromServer;

    /// <summary>
    /// The usual gap between two packets from the other players' games, in the ticks of <see cref="Stopwatch"/>. The
    /// server's thread only.
    /// </summary>
    private static long _usualGapFromOtherPlayers;

    /// <summary>
    /// How long the last quiet stretch lasted, in milliseconds.
    /// </summary>
    private static volatile int _quietMs;

    #endregion

    #region Main thread

    /// <summary>
    /// How many of the server thread's <see cref="_fromOtherPlayers"/> are counted here.
    /// </summary>
    private static int _seenFromOtherPlayers;

    /// <summary>
    /// How many of the server thread's <see cref="_othersCameBack"/> are counted here.
    /// </summary>
    private static int _seenOthersCameBack;

    /// <summary>
    /// When what the other players send last came in again after going quiet, in the ticks of
    /// <see cref="Stopwatch"/>.
    /// </summary>
    private static long _cameBackAt;

    /// <summary>
    /// The quiet stretches since they were last taken (<see cref="TakeQuietStretches"/>).
    /// </summary>
    private static int _quietStretches;

    /// <summary>
    /// The longest of those, in milliseconds.
    /// </summary>
    private static int _longestQuietMs;

    /// <summary>
    /// The packets heard, counted in their turn on the main thread.
    /// </summary>
    public static int Heard { get; private set; }

    /// <summary>
    /// What <see cref="Heard"/> was when what the other players send last came in again after going quiet, with the
    /// packets that came in at that moment counted. The smoothing counts the packets that keep coming from there, not
    /// from before the quiet stretch, which would read a burst of packets held up by it as many that came while the
    /// thing stood still.
    /// </summary>
    public static int HeardAtReturn { get; private set; }

    /// <summary>
    /// How many times what the other players send came in again after going quiet.
    /// </summary>
    public static int Returns { get; private set; }

    /// <summary>
    /// How long ago what the other players send last came in again after going quiet, in seconds.
    /// </summary>
    public static double SecondsSinceReturn =>
        (double) (Stopwatch.GetTimestamp() - _cameBackAt) / Stopwatch.Frequency;

    #endregion

    /// <summary>
    /// Counts one packet from the server, made once.
    /// </summary>
    private static readonly Action HeardOne = () => Hear(1, false);

    /// <summary>
    /// Counts one packet from the server that came after a quiet stretch, made once.
    /// </summary>
    private static readonly Action HeardOneAfterQuiet = () => Hear(1, true);

    /// <summary>
    /// Counts the packets the server took in from the other players since the last count, made once.
    /// </summary>
    private static readonly Action CatchUpWithOthers = CatchUp;

    /// <summary>
    /// Whether this game runs the server (NetServer).
    /// </summary>
    public static bool Hosting {
        get => _hosting;
        set => _hosting = value;
    }

    /// <summary>
    /// Takes in a packet this game got from the server, on the client's receive thread, before its contents are handed
    /// to the main thread (NetClient). In the game that hosts, the packets from the server are not what is counted,
    /// and this only has the main thread catch up with what the server took in from the others, in this packet's turn.
    /// </summary>
    public static void TookInFromServer() {
        if (_hosting) {
            ThreadUtil.RunActionOnMainThread(CatchUpWithOthers);
            return;
        }

        ThreadUtil.RunActionOnMainThread(
            Arrived(ref _lastFromServer, ref _usualGapFromServer) ? HeardOneAfterQuiet : HeardOne
        );
    }

    /// <summary>
    /// Takes in a packet the server in this game got from another player's game, on the server's thread (NetServer).
    /// The packets of the host's own game are not counted: they never leave the machine.
    /// </summary>
    public static void TookInFromOtherPlayer() {
        if (Arrived(ref _lastFromOtherPlayers, ref _usualGapFromOtherPlayers)) {
            Interlocked.Increment(ref _othersCameBack);
        }

        Interlocked.Increment(ref _fromOtherPlayers);
    }

    /// <summary>
    /// Takes out the quiet stretches since this was last called: how many there were and the longest of them.
    /// </summary>
    /// <param name="count">How many there were.</param>
    /// <param name="longestMs">How long the longest lasted, in milliseconds.</param>
    public static void TakeQuietStretches(out int count, out int longestMs) {
        count = _quietStretches;
        longestMs = _longestQuietMs;
        _quietStretches = 0;
        _longestQuietMs = 0;
    }

    /// <summary>
    /// Notes that a packet came in now, on the one thread that takes in these packets.
    /// </summary>
    /// <param name="last">When the last one came in.</param>
    /// <param name="usualGap">The usual gap between two of them, which this one goes into.</param>
    /// <returns>Whether it came after a quiet stretch.</returns>
    private static bool Arrived(ref long last, ref long usualGap) {
        var now = Stopwatch.GetTimestamp();
        var before = last;
        last = now;
        if (before == 0) {
            return false;
        }

        // The usual gap follows every gap, a long one counted as the longest learnt: a sender that slows down is
        // followed within about a second, while a quiet stretch hardly moves it
        var gap = now - before;
        var usual = usualGap;
        var learnt = System.Math.Min(gap, LongestLearntGap);
        usualGap = usual == 0 ? learnt : usual + (learnt - usual) / 16;
        if (gap <= System.Math.Max(QuietTicks, (long) (usual * QuietGaps))) {
            return false;
        }

        _quietMs = (int) System.Math.Min(int.MaxValue, gap * 1000 / Stopwatch.Frequency);
        return true;
    }

    /// <summary>
    /// Counts what the server took in from the other players since the last count, on the main thread. The count is
    /// read before whether one came after a quiet stretch, which the server's thread adds to first, so a packet is
    /// never counted without that.
    /// </summary>
    private static void CatchUp() {
        var total = Volatile.Read(ref _fromOtherPlayers);
        var cameBack = Volatile.Read(ref _othersCameBack);
        var more = total - _seenFromOtherPlayers;
        if (more <= 0) {
            return;
        }

        _seenFromOtherPlayers = total;
        var afterQuiet = cameBack != _seenOthersCameBack;
        _seenOthersCameBack = cameBack;
        Hear(more, afterQuiet);
    }

    /// <summary>
    /// Counts packets heard, on the main thread.
    /// </summary>
    /// <param name="count">How many.</param>
    /// <param name="afterQuiet">Whether they came after a quiet stretch.</param>
    private static void Hear(int count, bool afterQuiet) {
        Heard += count;
        if (!afterQuiet) {
            return;
        }

        HeardAtReturn = Heard;
        Returns++;
        _cameBackAt = Stopwatch.GetTimestamp();
        _quietStretches++;
        var quietMs = _quietMs;
        if (quietMs > _longestQuietMs) {
            _longestQuietMs = quietMs;
        }
    }
}
