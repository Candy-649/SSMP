using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// it had not yet sent or not yet had acknowledged, how long a new message would wait, how much of what was sent the
/// other side says arrived, which relay the link went through, how long ago the mod last looked for what had arrived,
/// and what Steam reported in its own words.
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
    /// How long, in milliseconds, stopping waits for a pass that is under way to finish.
    /// </summary>
    private const int PassWait = 1000;

    /// <summary>
    /// How many of Steam's lines may wait to be written, beyond which they are left out and counted.
    /// </summary>
    private const int SteamLinesWaiting = 200;

    /// <summary>
    /// How many of Steam's lines are written as they come each minute at most, so that Steam saying a lot for a long
    /// time cannot fill the log. The rest are held back (see <see cref="SteamLinesHeldBack"/>).
    /// </summary>
    private const int SteamLinesEachMinute = 20;

    /// <summary>
    /// How many of Steam's lines beyond those of the minute are held back, the newest, to be written with the next
    /// record of a link that is written down: what Steam said just before that is the part worth having.
    /// </summary>
    private const int SteamLinesHeldBack = 50;

    /// <summary>
    /// What Steam's own messages are asked for down to: its messages about each connection - one going wrong, one
    /// closing - and about the relay network. Messages about routes and packets have levels of their own, which are
    /// left as Steam has them.
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
    /// Whether a pass of <see cref="_timer"/> is under way, so that a slow one is not run over by the next, and so that
    /// stopping can wait for it.
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
    /// When the mod last looked for what had arrived on each of the two channels, on <see cref="Clock"/>, or 0 before
    /// it ever did.
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
    /// How many of Steam's messages were left out, as more came at once than may wait, since that was last told.
    /// </summary>
    private static int _steamLinesLeftOut;

    /// <summary>
    /// Steam's messages beyond those written in their minute, the newest <see cref="SteamLinesHeldBack"/>. Also what
    /// guards the writing of Steam's messages.
    /// </summary>
    private static readonly Queue<string> HeldBack = new();

    /// <summary>
    /// How many held-back messages of Steam's were let go for newer ones since they were last written.
    /// </summary>
    private static int _heldBackDropped;

    /// <summary>
    /// The minute, on <see cref="Clock"/>, whose messages of Steam's are being counted.
    /// </summary>
    private static long _steamMinute = -1;

    /// <summary>
    /// How many of Steam's messages were written as they came in <see cref="_steamMinute"/>.
    /// </summary>
    private static int _steamWrittenThisMinute;

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
            ListenToSteam(true);
        }
    }

    /// <summary>
    /// Stops keeping the record and listening to Steam, once the last transport that used them is done with them.
    /// </summary>
    public static void Stop() {
        lock (Gate) {
            if (_users == 0 || --_users > 0) {
                return;
            }

            _timer?.Dispose();
            _timer = null;
        }

        Finish();
    }

    /// <summary>
    /// Stops keeping the record and listening to Steam at once, whatever still uses them: for the game letting Steam
    /// go, which must not happen while Steam is being asked something here or may still call this game. A transport
    /// whose connecting and disconnecting crossed is never stopped, and would otherwise keep both going for as long as
    /// the game runs.
    /// </summary>
    public static void Halt() {
        lock (Gate) {
            if (_users == 0) {
                return;
            }

            _users = 0;
            _timer?.Dispose();
            _timer = null;
        }

        Finish();
    }

    /// <summary>
    /// What stopping does once the timer is let go: waits for a pass that is under way, stops listening to Steam,
    /// writes what Steam said last and lets go of the records.
    /// </summary>
    private static void Finish() {
        // A pass under way goes on after its timer is let go. It asks Steam about no further player once nothing uses
        // the record, and only waiting for it here keeps it from asking while the game lets Steam go. Not waited for
        // while holding the gate, which nothing in a pass takes
        SpinWait.SpinUntil(() => Volatile.Read(ref _ticking) == 0, PassWait);

        lock (Gate) {
            // Started again in the meantime
            if (_users > 0) {
                return;
            }

            ListenToSteam(false);
            Peers.Clear();
        }

        WriteSteamLines(ignoreTheMinute: true);
        var held = new StringBuilder("[Steam] What Steam said last that was held back:");
        if (AppendHeldBack(held)) {
            Logger.Info(held.ToString());
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
    /// seconds, leaving out seconds written down before, and goes on writing it down each second until things have
    /// arrived from them for <see cref="HeardAgainFor"/> seconds in a row, or for <see cref="LiveAtMost"/> seconds.
    /// Nothing is written again while it is being written down already.
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
    /// Counts a message the mod took from Steam that came from a player. The first message from a player who just
    /// joined comes before the host keeps a record of them, and is not counted.
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
            foreach (var pair in Peers) {
                // Asked again before each player: whatever stops the record stops the pass at the next one
                if (Volatile.Read(ref _users) == 0 || !SteamManager.IsInitialized) {
                    return;
                }

                // One player that cannot be asked about does not keep the others from being asked about
                try {
                    TakeSample(pair.Key, pair.Value);
                } catch (Exception e) {
                    TellTickFailure(e);
                }
            }

            if (Volatile.Read(ref _users) > 0) {
                WriteSteamLines(ignoreTheMinute: false);
            }
        } catch (Exception e) {
            TellTickFailure(e);
        } finally {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>
    /// Writes down, the first time only, that a pass went wrong.
    /// </summary>
    private static void TellTickFailure(Exception e) {
        if (_tickFailureTold) {
            return;
        }

        _tickFailureTold = true;
        try {
            Logger.Warn($"[Link] Could not write down how the links stand: {e}");
        } catch (Exception) {
            // Only a line of the log is lost
        }
    }

    /// <summary>
    /// Asks Steam how the link to a player stands, keeps it with what the mod gave and took in the last second, and
    /// writes it down if the link is being written down as it goes.
    /// </summary>
    private static void TakeSample(ulong steamId, Peer peer) {
        var identity = SteamRelayMessaging.IdentityOf(steamId);
        var state = SteamNetworkingMessages.GetSessionConnectionInfo(ref identity, out var info, out var status);
        var looked = Interlocked.Read(ref LastLookedMs[peer.Channel]);
        var sample = new Sample {
            At = DateTime.Now,
            State = state,
            Relay = info.m_idPOPRelay,
            OtherEnd = info.m_idPOPRemote,
            Ping = status.m_nPing,
            QualityHere = status.m_flConnectionQualityLocal,
            QualityThere = status.m_flConnectionQualityRemote,
            OutPackets = status.m_flOutPacketsPerSec,
            OutBytes = status.m_flOutBytesPerSec,
            InPackets = status.m_flInPacketsPerSec,
            InBytes = status.m_flInBytesPerSec,
            SendRate = status.m_nSendRateBytesPerSecond,
            UnsentUnreliable = status.m_cbPendingUnreliable,
            UnsentReliable = status.m_cbPendingReliable,
            Unacked = status.m_cbSentUnackedReliable,
            WaitMicroseconds = status.m_usecQueueTime.m_SteamNetworkingMicroseconds,
            Gave = Interlocked.Exchange(ref peer.Gave, 0),
            GaveBytes = Interlocked.Exchange(ref peer.GaveBytes, 0),
            Refused = Interlocked.Exchange(ref peer.Refused, 0),
            Took = Interlocked.Exchange(ref peer.Took, 0),
            TookBytes = Interlocked.Exchange(ref peer.TookBytes, 0),
            LookedAgoMs = looked == 0 ? -1 : Clock.ElapsedMilliseconds - looked
        };

        lock (peer) {
            sample.Number = ++peer.Numbered;
            peer.Ring[peer.Next] = sample;
            peer.Next = (peer.Next + 1) % Kept;
            peer.Count = System.Math.Min(peer.Count + 1, Kept);

            if (peer.LiveLeft <= 0) {
                return;
            }

            Logger.Info(Describe(steamId, sample));
            peer.WrittenUpTo = sample.Number;
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
    /// Writes down, as one entry of the log, the seconds of the last <paramref name="secondsBack"/> of the record of a
    /// link that were not written down before, with what Steam said that was held back. Seconds are written down once
    /// only: a player whose link keeps faltering asks for the state of their room every few seconds, and each time
    /// wrote the same 45 seconds again. Called with the record locked.
    /// </summary>
    private static void WriteDown(ulong steamId, Peer peer, string why, int secondsBack) {
        var count = System.Math.Min(System.Math.Max(secondsBack, 0), peer.Count);
        var lines = new List<string>();
        for (var back = count; back > 0; back--) {
            var sample = peer.Ring[(peer.Next - back + Kept) % Kept];
            if (sample.Number <= peer.WrittenUpTo) {
                continue;
            }

            lines.Add(Describe(steamId, sample));
            peer.WrittenUpTo = sample.Number;
        }

        var entry = new StringBuilder(
            $"[Link] How the link with {steamId} has stood each second, written down as {why}: " +
            (lines.Count > 0 ? $"{lines.Count} s not written down before" : "nothing kept that was not written down")
        );
        foreach (var line in lines) {
            entry.Append('\n').Append(line);
        }

        AppendHeldBack(entry);
        Logger.Info(entry.ToString());
    }

    /// <summary>
    /// Says one second of the record of a link in one line of the log.
    /// </summary>
    private static string Describe(ulong steamId, in Sample sample) {
        var mod = $"the mod gave Steam {sample.Gave} ({sample.GaveBytes / 1000.0:F1} kB" +
                  $"{(sample.Refused > 0 ? $", {sample.Refused} refused" : "")}), took {sample.Took} " +
                  $"({sample.TookBytes / 1000.0:F1} kB), " +
                  (sample.LookedAgoMs < 0 ? "never looked for messages yet" : $"looked {sample.LookedAgoMs} ms ago");

        if (sample.State == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None) {
            return $"[Link] {sample.At:HH:mm:ss} {steamId}: no session; {mod}";
        }

        var state = sample.State.ToString().Replace("k_ESteamNetworkingConnectionState_", "");
        var otherEnd = sample.OtherEnd.m_SteamNetworkingPOPID == 0
            ? ""
            : $", the other end in {RelayName(sample.OtherEnd)}";
        return $"[Link] {sample.At:HH:mm:ss} {steamId}: {state} via relay {RelayName(sample.Relay)}{otherEnd}, " +
               $"ping {sample.Ping} ms, arrived {Share(sample.QualityHere)} here {Share(sample.QualityThere)} there, " +
               $"out {sample.OutPackets:F0}/s {sample.OutBytes / 1000:F1} kB/s, in {sample.InPackets:F0}/s " +
               $"{sample.InBytes / 1000:F1} kB/s, may send {sample.SendRate / 1000} kB/s, unsent " +
               $"{sample.UnsentUnreliable} B unreliable + {sample.UnsentReliable} B reliable, unacked " +
               $"{sample.Unacked} B, a new message would wait {sample.WaitMicroseconds / 1000} ms; {mod}";
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
    /// Writes Steam's waiting messages, as many as their minute allows unless told to write them all, and holds back
    /// the rest.
    /// </summary>
    private static void WriteSteamLines(bool ignoreTheMinute) {
        lock (HeldBack) {
            var minute = Clock.ElapsedMilliseconds / 60000;
            if (minute != _steamMinute) {
                _steamMinute = minute;
                _steamWrittenThisMinute = 0;
            }

            var leftOut = Interlocked.Exchange(ref _steamLinesLeftOut, 0);
            if (leftOut > 0) {
                SteamLines.Enqueue($"{DateTime.Now:HH:mm:ss} ({leftOut} more of Steam's lines came at once than wait)");
                Interlocked.Increment(ref _steamLinesWaiting);
            }

            while (SteamLines.TryDequeue(out var line)) {
                Interlocked.Decrement(ref _steamLinesWaiting);
                if (ignoreTheMinute || _steamWrittenThisMinute < SteamLinesEachMinute) {
                    _steamWrittenThisMinute++;
                    Logger.Info($"[Steam] {line}");
                    continue;
                }

                if (HeldBack.Count == SteamLinesHeldBack) {
                    HeldBack.Dequeue();
                    _heldBackDropped++;
                }

                HeldBack.Enqueue(line);
            }
        }
    }

    /// <summary>
    /// Adds Steam's held-back messages to an entry of the log, and lets go of them.
    /// </summary>
    /// <returns>Whether there were any.</returns>
    private static bool AppendHeldBack(StringBuilder entry) {
        lock (HeldBack) {
            if (HeldBack.Count == 0) {
                return false;
            }

            entry.Append(
                $"\n[Steam] {HeldBack.Count} of Steam's own lines held back, as more came than the log is given" +
                (_heldBackDropped > 0 ? $", after {_heldBackDropped} older ones were let go:" : ":")
            );
            while (HeldBack.Count > 0) {
                entry.Append("\n[Steam] ").Append(HeldBack.Dequeue());
            }

            _heldBackDropped = 0;
            return true;
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
        /// The number of the last second kept, counted from 1.
        /// </summary>
        public long Numbered;

        /// <summary>
        /// The number of the last second written down, or 0 for none.
        /// </summary>
        public long WrittenUpTo;

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
        public long Number;
        public DateTime At;
        public ESteamNetworkingConnectionState State;
        public SteamNetworkingPOPID Relay;
        public SteamNetworkingPOPID OtherEnd;
        public int Ping;
        public float QualityHere;
        public float QualityThere;
        public float OutPackets;
        public float OutBytes;
        public float InPackets;
        public float InBytes;
        public int SendRate;
        public int UnsentUnreliable;
        public int UnsentReliable;
        public int Unacked;
        public long WaitMicroseconds;
        public int Gave;
        public int GaveBytes;
        public int Refused;
        public int Took;
        public int TookBytes;
        public long LookedAgoMs;
    }
}
