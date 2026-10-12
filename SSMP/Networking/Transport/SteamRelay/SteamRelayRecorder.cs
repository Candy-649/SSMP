using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading;
using SSMP.Game;
using SSMP.Logging;
using Steamworks;

namespace SSMP.Networking.Transport.SteamRelay;

/// <summary>
/// Keeps, for each player this game talks to over the relay, what Steam says about the link to them once a second for
/// the last <see cref="Kept"/> seconds, and writes it into the log when a stretch in which nothing arrives calls for
/// it; and writes what Steam itself says about its networking into the log as well.
///
/// On 10-12 a player heard nothing from the host for 30 seconds and was let go, while the host's game went on as
/// usual and kept receiving everything that player sent, neither game failed to send anything, and Steam called the
/// session connected throughout. Neither log could say where the host's messages stopped: in the host's game, in Steam
/// on the host's machine, on the relay, or in Steam or the game on the other machine. What is written down here tells
/// these apart: how much the mod handed to Steam and took from it each second, how much Steam sent and received, what
/// it held back and for how long, how much of what was sent the other side says arrived, which relays the link went
/// through, how long ago the mod last looked for what had arrived, and what Steam reported in its own words.
/// </summary>
internal static class SteamRelayRecorder {
    /// <summary>
    /// How often, in milliseconds, Steam is asked how each link stands.
    /// </summary>
    private const int SampleEvery = 1000;

    /// <summary>
    /// How many seconds of the record are kept for each player: a link is given up on after 30 seconds of silence, and
    /// what came just before that silence belongs to it.
    /// </summary>
    private const int Kept = 45;

    /// <summary>
    /// How many seconds at most a link is written down as it goes, once a stretch of silence starts that.
    /// </summary>
    private const int LiveAtMost = 120;

    /// <summary>
    /// For how many seconds in a row things must have arrived from a player for a link written down as it goes to be
    /// taken as working again.
    /// </summary>
    private const int HeardAgainFor = 5;

    /// <summary>
    /// How many of Steam's lines may wait to be written, beyond which they are left out and counted.
    /// </summary>
    private const int SteamLinesWaiting = 200;

    /// <summary>
    /// How many of Steam's lines are written each second at most.
    /// </summary>
    private const int SteamLinesEachSecond = 20;

    /// <summary>
    /// How many of Steam's lines are written in all, after which this stops listening to Steam, so that Steam saying a
    /// lot for a long time cannot fill the log.
    /// </summary>
    private const int SteamLinesAtMost = 2000;

    /// <summary>
    /// What Steam's own messages are asked for down to: its messages about each connection - one going wrong, one
    /// closing, a route lost or chosen - and about the relay network. Below this are messages about each packet.
    /// </summary>
    private const ESteamNetworkingSocketsDebugOutputType SteamLevel =
        ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_Msg;

    /// <summary>
    /// Guards starting and stopping.
    /// </summary>
    private static readonly object Gate = new();

    /// <summary>
    /// How many transports use the record now (see <see cref="Start"/>).
    /// </summary>
    private static int _users;

    /// <summary>
    /// What asks Steam how each link stands, once a second, while anything uses the record.
    /// </summary>
    private static Timer? _timer;

    /// <summary>
    /// Whether a pass of <see cref="_timer"/> is under way, so that a slow one is not run over by the next.
    /// </summary>
    private static int _ticking;

    /// <summary>
    /// Whether a pass that went wrong has been written down, so that one going wrong every second says so once.
    /// </summary>
    private static bool _tickFailureTold;

    /// <summary>
    /// The record of each player this game talks to over the relay, by Steam ID.
    /// </summary>
    private static readonly ConcurrentDictionary<ulong, Peer> Peers = new();

    /// <summary>
    /// The time since the game started, in which the looking for messages is marked.
    /// </summary>
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>
    /// When the mod last looked for what had arrived on each of the two channels, on <see cref="Clock"/>.
    /// </summary>
    private static readonly long[] LastLookedMs = new long[2];

    /// <summary>
    /// What Steam calls with its own messages. Held here for ever: Steam keeps only the address of what it is given,
    /// and a delegate the runtime collected would be called by Steam all the same.
    /// </summary>
    private static readonly FSteamNetworkingSocketsDebugOutput SteamSays = OnSteamSays;

    /// <summary>
    /// Steam's messages, waiting to be written by the next pass: Steam says them on its own threads, which must not
    /// wait for the log.
    /// </summary>
    private static readonly ConcurrentQueue<string> SteamLines = new();

    /// <summary>
    /// How many of Steam's messages wait in <see cref="SteamLines"/>.
    /// </summary>
    private static int _steamLinesWaiting;

    /// <summary>
    /// How many of Steam's messages were left out since that was last written down.
    /// </summary>
    private static int _steamLinesLeftOut;

    /// <summary>
    /// How many of Steam's messages have been written in all.
    /// </summary>
    private static int _steamLinesWritten;

    /// <summary>
    /// Starts keeping the record and listening to Steam, for one more transport that uses the relay. Each call is
    /// matched by one to <see cref="Stop"/>.
    /// </summary>
    public static void Start() {
        lock (Gate) {
            if (_users++ > 0) {
                return;
            }

            _timer = new Timer(_ => Tick(), null, SampleEvery, SampleEvery);
            if (Volatile.Read(ref _steamLinesWritten) < SteamLinesAtMost) {
                ListenToSteam(true);
            }
        }
    }

    /// <summary>
    /// Stops keeping the record and listening to Steam, once the last transport that used them is done with them:
    /// before the game lets Steam go, which it does as it closes.
    /// </summary>
    public static void Stop() {
        lock (Gate) {
            if (_users == 0 || --_users > 0) {
                return;
            }

            _timer?.Dispose();
            _timer = null;
            ListenToSteam(false);
            WriteSteamLines(SteamLinesWaiting);
            Peers.Clear();
        }
    }

    /// <summary>
    /// Starts keeping a record of the link to a player.
    /// </summary>
    /// <param name="steamId">The player.</param>
    /// <param name="channel">The channel what they send arrives on, whose looking for messages is written down.</param>
    public static void Watch(ulong steamId, int channel) {
        Peers[steamId] = new Peer(channel);
    }

    /// <summary>
    /// Stops keeping a record of the link to a player, writing down first what the record holds if
    /// <paramref name="writeDownAs"/> says why, and it is not being written down as it goes already.
    /// </summary>
    public static void Forget(ulong steamId, string? writeDownAs) {
        if (!Peers.TryRemove(steamId, out var peer)) {
            return;
        }

        lock (peer) {
            if (peer.LiveLeft > 0) {
                peer.LiveLeft = 0;
                Logger.Info($"[Link] Stopped writing down the link with {steamId}: it was closed");
            } else if (writeDownAs != null) {
                WriteDown(steamId, peer, writeDownAs, Kept);
            }
        }
    }

    /// <summary>
    /// Writes down how the link to a player has stood each second for the last <paramref name="secondsBack"/>
    /// seconds, and goes on writing it down each second until things have arrived from them for
    /// <see cref="HeardAgainFor"/> seconds in a row, or for <see cref="LiveAtMost"/> seconds. Nothing is written again
    /// while it is being written down already.
    /// </summary>
    /// <param name="steamId">The player.</param>
    /// <param name="why">Why, as it ends a sentence of the log: "written down as ...".</param>
    /// <param name="secondsBack">How many seconds of what was kept to write down first.</param>
    public static void Record(ulong steamId, string why, int secondsBack) {
        if (!Peers.TryGetValue(steamId, out var peer)) {
            return;
        }

        lock (peer) {
            if (peer.LiveLeft > 0) {
                return;
            }

            WriteDown(steamId, peer, why, secondsBack);
            peer.LiveLeft = LiveAtMost;
            peer.HeardRun = 0;
        }
    }

    /// <summary>
    /// Counts a message the mod handed to Steam for a player, or one Steam would not take.
    /// </summary>
    public static void Gave(ulong steamId, int bytes, bool taken) {
        if (!Peers.TryGetValue(steamId, out var peer)) {
            return;
        }

        if (taken) {
            Interlocked.Increment(ref peer.Gave);
            Interlocked.Add(ref peer.GaveBytes, bytes);
        } else {
            Interlocked.Increment(ref peer.Refused);
        }
    }

    /// <summary>
    /// Counts a message the mod took from Steam that came from a player.
    /// </summary>
    public static void Took(ulong steamId, int bytes) {
        if (!Peers.TryGetValue(steamId, out var peer)) {
            return;
        }

        Interlocked.Increment(ref peer.Took);
        Interlocked.Add(ref peer.TookBytes, bytes);
    }

    /// <summary>
    /// Marks the mod looking for what has arrived on a channel, whether anything had or not.
    /// </summary>
    public static void Looked(int channel) {
        if (channel >= 0 && channel < LastLookedMs.Length) {
            Interlocked.Exchange(ref LastLookedMs[channel], Clock.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// The name of a relay of Steam's from its ID: the airport code of the city it stands in, such as 'sgp'.
    /// </summary>
    public static string RelayName(SteamNetworkingPOPID pop) {
        var id = pop.m_SteamNetworkingPOPID;
        if (id == 0) {
            return "none";
        }

        // Steam packs the fourth letter of a code into the highest byte, after the other three (steamnetworkingtypes.h)
        var name = new StringBuilder(4);
        foreach (var shift in new[] { 16, 8, 0, 24 }) {
            var letter = (char) ((id >> shift) & 0xFF);
            if (letter != 0) {
                name.Append(letter);
            }
        }

        return name.ToString();
    }

    /// <summary>
    /// One pass of <see cref="_timer"/>: asks Steam how each link stands and writes what Steam said.
    /// </summary>
    private static void Tick() {
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) {
            return;
        }

        // Nothing may leave a timer's thread: an exception there would close the game
        try {
            if (Volatile.Read(ref _users) == 0 || !SteamManager.IsInitialized) {
                return;
            }

            foreach (var pair in Peers) {
                TakeSample(pair.Key, pair.Value);
            }

            WriteSteamLines(SteamLinesEachSecond);
        } catch (Exception e) {
            if (!_tickFailureTold) {
                _tickFailureTold = true;
                try {
                    Logger.Warn($"[Link] Could not write down how the links stand: {e}");
                } catch (Exception) {
                    // Only a line of the log is lost
                }
            }
        } finally {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>
    /// Asks Steam how the link to a player stands, keeps it with what the mod gave and took in the last second, and
    /// writes it down if the link is being written down as it goes.
    /// </summary>
    private static void TakeSample(ulong steamId, Peer peer) {
        var identity = SteamRelayMessaging.IdentityOf(steamId);
        var state = SteamNetworkingMessages.GetSessionConnectionInfo(ref identity, out var info, out var status);
        var sample = new Sample {
            At = DateTime.Now,
            State = state,
            Relay = info.m_idPOPRelay,
            TheirRelay = info.m_idPOPRemote,
            Ping = status.m_nPing,
            QualityHere = status.m_flConnectionQualityLocal,
            QualityThere = status.m_flConnectionQualityRemote,
            OutPackets = status.m_flOutPacketsPerSec,
            OutBytes = status.m_flOutBytesPerSec,
            InPackets = status.m_flInPacketsPerSec,
            InBytes = status.m_flInBytesPerSec,
            SendRate = status.m_nSendRateBytesPerSecond,
            WaitingUnreliable = status.m_cbPendingUnreliable,
            WaitingReliable = status.m_cbPendingReliable,
            Unacked = status.m_cbSentUnackedReliable,
            QueuedMicroseconds = status.m_usecQueueTime.m_SteamNetworkingMicroseconds,
            Gave = Interlocked.Exchange(ref peer.Gave, 0),
            GaveBytes = Interlocked.Exchange(ref peer.GaveBytes, 0),
            Refused = Interlocked.Exchange(ref peer.Refused, 0),
            Took = Interlocked.Exchange(ref peer.Took, 0),
            TookBytes = Interlocked.Exchange(ref peer.TookBytes, 0),
            LookedAgoMs = Clock.ElapsedMilliseconds - Interlocked.Read(ref LastLookedMs[peer.Channel])
        };

        lock (peer) {
            peer.Ring[peer.Next] = sample;
            peer.Next = (peer.Next + 1) % Kept;
            peer.Count = System.Math.Min(peer.Count + 1, Kept);

            if (peer.LiveLeft <= 0) {
                return;
            }

            Logger.Info(Describe(steamId, sample));
            peer.HeardRun = sample.Took > 0 ? peer.HeardRun + 1 : 0;
            if (peer.HeardRun >= HeardAgainFor) {
                peer.LiveLeft = 0;
                Logger.Info(
                    $"[Link] Stopped writing down the link with {steamId}: things have arrived from them for " +
                    $"{HeardAgainFor} s in a row"
                );
            } else if (--peer.LiveLeft == 0) {
                Logger.Info($"[Link] Stopped writing down the link with {steamId} after {LiveAtMost} s");
            }
        }
    }

    /// <summary>
    /// Writes down, as one entry of the log, the last <paramref name="secondsBack"/> seconds of the record of a link.
    /// Called with the record locked.
    /// </summary>
    private static void WriteDown(ulong steamId, Peer peer, string why, int secondsBack) {
        var count = System.Math.Min(System.Math.Max(secondsBack, 0), peer.Count);
        var entry = new StringBuilder(
            $"[Link] How the link with {steamId} has stood each second, written down as {why}: " +
            $"{(count == 0 ? "nothing was kept yet" : $"the last {count} s")}"
        );

        for (var back = count; back > 0; back--) {
            entry.Append('\n').Append(Describe(steamId, peer.Ring[(peer.Next - back + Kept) % Kept]));
        }

        Logger.Info(entry.ToString());
    }

    /// <summary>
    /// Says one second of the record of a link in one line of the log.
    /// </summary>
    private static string Describe(ulong steamId, in Sample sample) {
        var mod = $"the mod gave Steam {sample.Gave} ({sample.GaveBytes / 1000.0:F1} kB" +
                  $"{(sample.Refused > 0 ? $", {sample.Refused} refused" : "")}), took {sample.Took} " +
                  $"({sample.TookBytes / 1000.0:F1} kB), looked {sample.LookedAgoMs} ms ago";

        if (sample.State == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None) {
            return $"[Link] {sample.At:HH:mm:ss} {steamId}: no session; {mod}";
        }

        var state = sample.State.ToString().Replace("k_ESteamNetworkingConnectionState_", "");
        return $"[Link] {sample.At:HH:mm:ss} {steamId}: {state} via {RelayName(sample.Relay)} " +
               $"(theirs {RelayName(sample.TheirRelay)}), ping {sample.Ping} ms, arrived {Share(sample.QualityHere)} " +
               $"here {Share(sample.QualityThere)} there, out {sample.OutPackets:F0}/s " +
               $"{sample.OutBytes / 1000:F1} kB/s, in {sample.InPackets:F0}/s {sample.InBytes / 1000:F1} kB/s, " +
               $"may send {sample.SendRate / 1000} kB/s, waiting {sample.WaitingUnreliable} B + " +
               $"{sample.WaitingReliable} B reliable, unacked {sample.Unacked} B, queued " +
               $"{sample.QueuedMicroseconds / 1000} ms; {mod}";
    }

    /// <summary>
    /// A share of the packets that arrived, as Steam measures it, or '?' while it has no measure.
    /// </summary>
    private static string Share(float quality) => quality < 0 ? "?" : $"{quality * 100:F0}%";

    /// <summary>
    /// Asks Steam for its own messages about its networking, or stops asking.
    /// </summary>
    private static void ListenToSteam(bool listen) {
        try {
            if (!SteamManager.IsInitialized) {
                return;
            }

            if (listen) {
                SteamNetworkingUtils.SetDebugOutputFunction(SteamLevel, SteamSays);
            } else {
                SteamNetworkingUtils.SetDebugOutputFunction(
                    ESteamNetworkingSocketsDebugOutputType.k_ESteamNetworkingSocketsDebugOutputType_None, null
                );
            }
        } catch (Exception e) {
            Logger.Warn($"[Steam] Could not {(listen ? "start" : "stop")} listening to Steam: {e.Message}");
        }
    }

    /// <summary>
    /// Takes one of Steam's messages, on whatever thread of Steam's says it.
    /// </summary>
    private static void OnSteamSays(ESteamNetworkingSocketsDebugOutputType type, StringBuilder message) {
        // Steam calls this from its own threads, where nothing may be thrown
        try {
            if (Interlocked.Increment(ref _steamLinesWaiting) > SteamLinesWaiting) {
                Interlocked.Decrement(ref _steamLinesWaiting);
                Interlocked.Increment(ref _steamLinesLeftOut);
                return;
            }

            var level = type.ToString().Replace("k_ESteamNetworkingSocketsDebugOutputType_", "");
            SteamLines.Enqueue($"{DateTime.Now:HH:mm:ss} {level}: {message?.ToString().TrimEnd()}");
        } catch (Exception) {
            // Only a line of the log is lost
        }
    }

    /// <summary>
    /// Writes at most <paramref name="atMost"/> of Steam's waiting messages, says how many were left out, and stops
    /// listening to Steam once <see cref="SteamLinesAtMost"/> have been written.
    /// </summary>
    private static void WriteSteamLines(int atMost) {
        for (var written = 0; written < atMost && SteamLines.TryDequeue(out var line); written++) {
            Interlocked.Decrement(ref _steamLinesWaiting);
            Logger.Info($"[Steam] {line}");

            if (Interlocked.Increment(ref _steamLinesWritten) != SteamLinesAtMost) {
                continue;
            }

            lock (Gate) {
                ListenToSteam(false);
            }

            Logger.Info($"[Steam] Stopped listening to Steam, after {SteamLinesAtMost} of its lines were written");
            break;
        }

        var leftOut = Interlocked.Exchange(ref _steamLinesLeftOut, 0);
        if (leftOut > 0) {
            Logger.Info($"[Steam] {leftOut} of Steam's lines were left out, as more came than could wait");
        }
    }

    /// <summary>
    /// The record of the link to one player.
    /// </summary>
    private sealed class Peer {
        /// <summary>
        /// The channel what this player sends arrives on.
        /// </summary>
        public readonly int Channel;

        /// <summary>
        /// The last <see cref="Kept"/> seconds, oldest overwritten first.
        /// </summary>
        public readonly Sample[] Ring = new Sample[Kept];

        /// <summary>
        /// Where the next second goes in <see cref="Ring"/>.
        /// </summary>
        public int Next;

        /// <summary>
        /// How many seconds <see cref="Ring"/> holds.
        /// </summary>
        public int Count;

        /// <summary>
        /// How many more seconds are written down as they go; none while the link is not being written down.
        /// </summary>
        public int LiveLeft;

        /// <summary>
        /// For how many seconds in a row things have arrived from this player while the link is written down.
        /// </summary>
        public int HeardRun;

        // What the mod gave Steam for this player and took from Steam from them since the last second was kept,
        // added to on the threads that send and receive
        public int Gave;
        public int GaveBytes;
        public int Refused;
        public int Took;
        public int TookBytes;

        public Peer(int channel) {
            Channel = channel;
        }
    }

    /// <summary>
    /// One second of the record of a link.
    /// </summary>
    private struct Sample {
        public DateTime At;
        public ESteamNetworkingConnectionState State;
        public SteamNetworkingPOPID Relay;
        public SteamNetworkingPOPID TheirRelay;
        public int Ping;
        public float QualityHere;
        public float QualityThere;
        public float OutPackets;
        public float OutBytes;
        public float InPackets;
        public float InBytes;
        public int SendRate;
        public int WaitingUnreliable;
        public int WaitingReliable;
        public int Unacked;
        public long QueuedMicroseconds;
        public int Gave;
        public int GaveBytes;
        public int Refused;
        public int Took;
        public int TookBytes;
        public long LookedAgoMs;
    }
}
