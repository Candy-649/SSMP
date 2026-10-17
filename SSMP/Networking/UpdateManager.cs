using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SSMP.Logging;
using SSMP.Networking.Packet;
using SSMP.Networking.Packet.Data;
using SSMP.Networking.Packet.Update;
using SSMP.Networking.Transport.Common;

namespace SSMP.Networking;

/// <summary>
/// Class that manages sending the update packet. Has a simple congestion avoidance system to
/// avoid flooding the channel.
/// </summary>
internal abstract class UpdateManager<TOutgoing, TPacketId>
    where TOutgoing : UpdatePacket<TPacketId>, new()
    where TPacketId : Enum {
    /// <summary>
    /// The time in milliseconds to disconnect after not receiving any updates.
    /// </summary>
    private const int ConnectionTimeout = 5000;

    /// <summary>
    /// How long a quiet connection is kept in total, in milliseconds, while the transport still says the session is
    /// up. Several seconds of silence is ordinary on a relay between continents - one route change is enough - and
    /// ending the game for it is worse than waiting a little. This is where waiting stops being reasonable even so,
    /// which is what keeps a host that has frozen from holding the other player there for ever.
    /// </summary>
    private const int ConnectionTimeoutCeiling = 30000;

    /// <summary>
    /// How long, in milliseconds, nothing has to have arrived before the transport's link is opened anew, while it
    /// still counts as connected (see <see cref="RenewStalledLink"/>). A shorter stretch is left to come back by
    /// itself, as stretches of 13 to 19 s did in the players' logs (09-21, 09-22, 10-09): Steam keeps what was sent in
    /// one and delivers it once the stretch is over, for 45 s (SteamRelayMessaging.SessionTimeout), where opening the
    /// link anew loses it. The two stretches that never came back (10-12, 10-15) went on past
    /// <see cref="ConnectionTimeoutCeiling"/>.
    /// </summary>
    private const int RenewLinkAfter = 15000;

    /// <summary>
    /// How long, in milliseconds, nothing has arrived since the transport's link was last opened anew before it is
    /// opened anew again.
    /// </summary>
    private const int RenewLinkInterval = 8000;

    /// <summary>
    /// How long, in milliseconds, a link has to have counted as connected before it is opened anew for carrying
    /// nothing. One that has only just been opened, maybe by the other side giving up on the old one at the same time,
    /// gets this long to carry the first things sent over it.
    /// </summary>
    private const int LinkSettleTime = 2000;

    /// <summary>
    /// How long, in milliseconds, nothing has to have arrived before the send loop watches whether the transport's
    /// link counts as connected, for <see cref="LinkSettleTime"/>.
    /// </summary>
    private const int LinkWatchFrom = 1000;

    /// <summary>
    /// How often the send loop asks the transport for its round trip, in milliseconds (see <see cref="AverageRtt"/>).
    /// </summary>
    private const int TransportPingEvery = 500;

    /// <summary>
    /// The MTU (maximum transfer unit) to use to send packets with. If the length of a packet exceeds this, we break
    /// it up into smaller packets before sending. This ensures that we control the breaking of packets in most
    /// cases and do not rely on smaller network devices for the breaking up as this could impact performance.
    /// This size is lower than the limit for DTLS packets, since there is a slight DTLS overhead for packets.
    /// </summary>
    private const int PacketMtu = 1200;

    /// <summary>
    /// Threshold for sequence number wrap-around detection.
    /// </summary>
    private const ushort SequenceWrapThreshold = 32768;

    /// <summary>
    /// The RTT tracker for measuring round-trip times.
    /// Lazily initialized only when transport requires sequencing.
    /// </summary>
    private RttTracker? _rttTracker;

    /// <summary>
    /// Asks the transport what the round trip to the other end is, when the transport measures one itself. Held as a
    /// call rather than the transport, because the two ends of this class are set through different interfaces.
    /// </summary>
    private Func<int?>? _transportPing;

    /// <summary>
    /// The round trip that the transport last reported, in milliseconds, or -1 while it has reported none. The send
    /// loop asks for it every <see cref="TransportPingEvery"/> milliseconds, and everything else reads it here. On
    /// Steam's relay the question waits for the lock of Steam's networking, which Steam's own threads hold for
    /// milliseconds at a time, and for over a second while a link is being made. Asked every frame by every creature
    /// copy, every other player and the ping display, as it used to be, it cost the joining player 2 to 10 ms a frame,
    /// and froze their game for 0.1 to 0.17 s at a time (USER 10-16 "画面和网络都卡").
    /// </summary>
    private volatile int _transportPingMs = -1;

    /// <summary>
    /// Asks the transport whether it still has a link to the other side, or null for a transport that cannot answer.
    /// Kept apart from <see cref="_transportPing"/>: a link that is being rebuilt has no round trip to report yet,
    /// and reading that as "there is no link" is what ended a game that was about to carry on.
    /// </summary>
    private Func<bool>? _transportSessionUp;

    /// <summary>
    /// Asks the transport how its link to the other side stands in words, or null for a transport that cannot say.
    /// </summary>
    private Func<string>? _transportDescribeSession;

    /// <summary>
    /// Asks the transport to write down how its link to the other side has stood each second, or null for a transport
    /// that keeps no record of it (see <see cref="RecordTransportSession"/>).
    /// </summary>
    private Action<string, int>? _transportRecordSession;

    /// <summary>
    /// Asks the transport to close a link that counts as connected so that the next send opens a new one, or null for
    /// a transport that cannot (see <see cref="ISessionStateTransport.RenewStalledSession"/>).
    /// </summary>
    private Func<bool>? _transportRenewSession;

    /// <summary>
    /// Asks the transport whether its link counts as connected, or null for a transport that cannot say.
    /// </summary>
    private Func<bool>? _transportSessionConnected;

    /// <summary>
    /// The quiet stretch that the three fields below are about, by when the last thing before it arrived. Only the send
    /// loop reads and writes these, and starts them over when it sees that something arrived since.
    /// </summary>
    private DateTime _linkStretch;

    /// <summary>
    /// How long nothing had arrived, in milliseconds, when the link was last seen not to count as connected in this
    /// quiet stretch - still being built, or closed by the other side - or -1 if it was not.
    /// </summary>
    private int _linkUnsettledAtQuiet = -1;

    /// <summary>
    /// How long nothing had arrived, in milliseconds, when the link was last opened anew in this quiet stretch, or -1
    /// if it was not.
    /// </summary>
    private int _renewedAtQuiet = -1;

    /// <summary>
    /// How many times the link was opened anew in this quiet stretch.
    /// </summary>
    private int _renewals;

    /// <summary>
    /// How many seconds from before a quiet stretch began are written down with it, to show how the link stood as it
    /// went quiet.
    /// </summary>
    private const int SecondsBeforeQuiet = 15;

    /// <summary>
    /// The reliability manager for packet loss detection and resending.
    /// Lazily initialized only when transport requires reliability.
    /// </summary>
    private ReliabilityManager<TOutgoing, TPacketId>? _reliabilityManager;

    /// <summary>
    /// The UDP congestion manager instance. Null if congestion management is disabled.
    /// Lazily initialized only when transport requires sequencing.
    /// </summary>
    private CongestionManager<TOutgoing, TPacketId>? _congestionManager;

    /// <summary>
    /// List containing sequence numbers that have been received within the ACK window.
    /// Uses linear lookups which is faster than HashSet for small collections (N=64).
    /// Lazily initialized only when transport requires sequencing.
    /// </summary>
    private List<ushort>? _receivedSequences;

    /// <summary>
    /// Thread for running the precise send loop.
    /// Replaces System.Timers.Timer for better consistency and reduced jitter.
    /// </summary>
    private Thread? _sendThread;

    /// <summary>
    /// Cancellation token source to stop the send loop safely.
    /// </summary>
    private CancellationTokenSource? _cancellationTokenSource;

    /// <summary>
    /// The timestamp of the last received packet.
    /// Used to check for connection timeouts.
    /// </summary>
    private DateTime _lastReceiveTime;

    /// <summary>
    /// Whether this quiet stretch has already been reported, so that a link which goes quiet says so once rather
    /// than on every pass of the send loop.
    /// </summary>
    private bool _timeoutGraceTold;

    /// <summary>
    /// The most packets with reliable data that are kept to be sent again, and how far apart two numbers of them can be
    /// and still belong to the same run of numbers (see <see cref="ReliableNumbering"/>).
    /// </summary>
    private const int MostKeptToSendAgain = 8192;

    /// <summary>
    /// How long, in milliseconds, packets with reliable data that arrived before one sent earlier are held for it while
    /// things arrive, before they are taken in without it; a stretch in which nothing arrives counts for at most
    /// <see cref="HeldWaitStepAtMost"/>, as the missing ones can only come over a link that carries something. While
    /// things arrive, the other side hears the ask for them within a round trip, unless the other way of the link
    /// stands still, which ends in opening the link anew or giving up on it within
    /// <see cref="ConnectionTimeoutCeiling"/>. Counted in time alone, a link that went quiet for nearly that long just
    /// after something went missing lost it after all in a simulation.
    /// </summary>
    private const int HoldForMissingAtMost = ConnectionTimeoutCeiling + 5000;

    /// <summary>
    /// The most that one stretch between two packets that arrive counts towards <see cref="HoldForMissingAtMost"/>, in
    /// milliseconds.
    /// </summary>
    private const int HeldWaitStepAtMost = 1000;

    /// <summary>
    /// How long, in milliseconds, after a packet with reliable data that the other side has not taken in last went,
    /// before an ask of the other side sends it again: doubled each time it goes again, up to
    /// <see cref="SendAgainDoublingsAtMost"/> times. The other side asks in every packet until it arrives, which takes
    /// a round trip at least, and the transport goes on sending what it was given, so a link that only lets a little
    /// through gets few copies of the same thing.
    /// </summary>
    private const int SendAgainAfter = 2000;

    /// <summary>
    /// How often, at most, the wait of <see cref="SendAgainAfter"/> is doubled for one packet: up to 16 s.
    /// </summary>
    private const int SendAgainDoublingsAtMost = 3;

    /// <summary>
    /// How long, in milliseconds, this side has to have taken in less than the other side says it sent before it asks
    /// for the rest again. What was sent a moment ago may still be on its way, and the transport sends what it lost
    /// again itself, which takes a round trip or a few.
    /// </summary>
    private const int AskWhenBehindFor = 1000;

    /// <summary>
    /// Guards the numbering of the packets with reliable data, which the send loop, the thread that receives and a
    /// reset all use.
    /// </summary>
    private readonly object _reliableLock = new();

    /// <summary>
    /// The number of the last packet with reliable data that was sent.
    /// </summary>
    private ushort _lastReliableSent;

    /// <summary>
    /// The packets with reliable data that the other side has not said it took in, oldest first, as they were sent.
    /// </summary>
    private readonly Queue<KeptReliable> _keptToSendAgain = new();

    /// <summary>
    /// Whether letting go of the oldest of <see cref="_keptToSendAgain"/> for want of room was said since the other
    /// side last said what it took in.
    /// </summary>
    private bool _keptFullTold;

    /// <summary>
    /// Whether the other side asked for the packets with reliable data that it has not taken in again.
    /// </summary>
    private bool _sendAgainAsked;

    /// <summary>
    /// Whether the packets that the other side has not taken in are sent again as soon as it says what it took in,
    /// which this side asks for after opening the link anew.
    /// </summary>
    private bool _sendAgainOnNextAck;

    /// <summary>
    /// Why the packets that the other side has not taken in are sent again before the next packet, whenever they were
    /// last sent again, or null if they are not.
    /// </summary>
    private string? _sendAgainNow;

    /// <summary>
    /// The number of the last packet with reliable data taken in from the other side, in the order they were sent: 0
    /// before any, as the other side numbers them from 1.
    /// </summary>
    private ushort _lastReliableTaken;

    /// <summary>
    /// The number of the last packet with reliable data that the other side said it sent (see
    /// <see cref="HearSentUpTo"/>).
    /// </summary>
    private ushort _otherSentUpTo;

    /// <summary>
    /// Since when this side has taken in less than the other side said it sent, or <see cref="DateTime.MinValue"/>
    /// while it has not (see <see cref="WatchBehind"/>).
    /// </summary>
    private DateTime _behindSince = DateTime.MinValue;

    /// <summary>
    /// Packets with reliable data that arrived before one sent earlier, held for it, by their number. They are of the
    /// type of packet that this side receives, which this class does not name.
    /// </summary>
    private readonly Dictionary<ushort, object> _heldForMissing = new();

    /// <summary>
    /// When the first of <see cref="_heldForMissing"/> was held.
    /// </summary>
    private DateTime _heldSince;

    /// <summary>
    /// How long the packets in <see cref="_heldForMissing"/> have waited, in milliseconds, as it counts towards
    /// <see cref="HoldForMissingAtMost"/>.
    /// </summary>
    private double _heldWaited;

    /// <summary>
    /// When the last packet arrived that the numbering took in.
    /// </summary>
    private DateTime _lastArrival;

    /// <summary>
    /// How many packets with reliable data that were taken in already arrived again, since it was last said.
    /// </summary>
    private int _arrivedAgain;

    /// <summary>
    /// Whether the packets with reliable data are numbered: on a transport that keeps them reliable itself, where this
    /// mod does no acknowledging and sending again of its own, which are Steam's relay and Steam's older peer-to-peer
    /// transport (see <see cref="ReliableNumbering"/>).
    /// </summary>
    private bool NumbersReliable => !RequiresReliability;

    /// <summary>
    /// Whether a packet whose headers were read but not the rest of it is still taken in by its number, which it is
    /// where the packets with reliable data are numbered: nothing of it is handled, but what came after it does not
    /// wait for it (see <see cref="TakeInOrder{TIncoming,TOtherPacketId}"/>).
    /// </summary>
    /// <param name="packet">The packet that could not be read.</param>
    /// <typeparam name="TOtherPacketId">The packet ID type of the packet.</typeparam>
    /// <returns>Whether it is taken in by its number.</returns>
    public bool TakesInUnreadable<TOtherPacketId>(UpdatePacket<TOtherPacketId> packet) where TOtherPacketId : Enum =>
        NumbersReliable && packet.OnlyHeadersRead;

    /// <summary>
    /// The shortest stretch without anything arriving, in seconds, that is said to be over once something arrives
    /// again (see <see cref="ReceiveResumed"/>). The other side sends every tick, empty or not, so anything this long is
    /// a gap in the link rather than a quiet moment.
    /// </summary>
    private const double ReceiveGapTold = 2.0;

    /// <summary>
    /// Raised on the thread that receives when something arrives after a stretch of at least
    /// <see cref="ReceiveGapTold"/> seconds in which nothing did, with its length in seconds. What was sent in the
    /// stretch may still be on its way, or lost for good.
    /// </summary>
    public event Action<double>? ReceiveResumed;

    /// <summary>
    /// Cached capability: whether the transport requires application-level sequencing.
    /// </summary>
    private bool _requiresSequencing = true;

    /// <summary>
    /// The last sent sequence number.
    /// </summary>
    private ushort _localSequence;

    /// <summary>
    /// The last received sequence number.
    /// </summary>
    private ushort _remoteSequence;

    /// <summary>
    /// Whether this update manager is actually updating and sending packets.
    /// </summary>
    private bool _isUpdating;

    /// <summary>
    /// The transport sender instance to use to send packets.
    /// Can be either IEncryptedTransport (client-side) or IEncryptedTransportClient (server-side).
    /// </summary>
    private volatile object? _transportSender;

    /// <summary>
    /// The current update packet being assembled. Protected for subclass access.
    /// </summary>
    protected TOutgoing CurrentUpdatePacket { get; private set; }

    /// <summary>
    /// Lock object for synchronizing packet assembly. Protected for subclass access.
    /// </summary>
    protected object Lock { get; } = new();

    /// <summary>
    /// Lock object for serializing all transport writes to prevent concurrent/parallel socket writes.
    /// </summary>
    private readonly object _transportSendLock = new();

    /// <summary>
    /// Whether the transport requires application-level reliability. Protected for subclass access.
    /// </summary>
    protected bool RequiresReliability { get; private set; } = true;

    /// <summary>
    /// The largest message the transport underneath carries in one piece, which is what a longer packet is cut up
    /// for. <see cref="PacketMtu"/> until a transport says otherwise, which is what every one of them used to be
    /// given whatever it could actually carry.
    /// </summary>
    private int _transportMaxPacketSize = PacketMtu;

    /// <summary>
    /// Gets or sets the transport for client-side communication.
    /// Captures transport capabilities when set.
    /// </summary>
    public IEncryptedTransport? Transport {
        set {
            _transportSender = value;
            if (value == null) return;

            _transportPing = () => value.Ping;
            _transportPingMs = -1;
            _transportSessionUp = value is ISessionStateTransport session ? () => session.SessionUp : null;
            _transportDescribeSession = value is ISessionStateTransport described ? described.DescribeSession : null;
            _transportRecordSession = value is ISessionStateTransport recorded ? recorded.RecordSession : null;
            _transportRenewSession = value is ISessionStateTransport renewed ? renewed.RenewStalledSession : null;
            _transportSessionConnected = value is ISessionStateTransport connected
                ? () => connected.SessionConnected
                : null;
            _requiresSequencing = value.RequiresSequencing;
            RequiresReliability = value.RequiresReliability;
            _transportMaxPacketSize = value.MaxPacketSize > 0 ? value.MaxPacketSize : PacketMtu;
            InitializeManagersIfNeeded();
        }
    }

    /// <summary>
    /// Sets the transport client for server-side communication.
    /// Captures transport capabilities when set.
    /// </summary>
    public IEncryptedTransportClient? TransportClient {
        set {
            _transportSender = value;
            if (value == null) return;

            _transportPing = () => value.Ping;
            _transportPingMs = -1;
            _transportSessionUp = value is ISessionStateTransport session ? () => session.SessionUp : null;
            _transportDescribeSession = value is ISessionStateTransport described ? described.DescribeSession : null;
            _transportRecordSession = value is ISessionStateTransport recorded ? recorded.RecordSession : null;
            _transportRenewSession = value is ISessionStateTransport renewed ? renewed.RenewStalledSession : null;
            _transportSessionConnected = value is ISessionStateTransport connected
                ? () => connected.SessionConnected
                : null;
            _requiresSequencing = value.RequiresSequencing;
            RequiresReliability = value.RequiresReliability;
            _transportMaxPacketSize = value.MaxPacketSize > 0 ? value.MaxPacketSize : PacketMtu;
            InitializeManagersIfNeeded();
        }
    }

    /// <summary>
    /// Lazily initializes managers only when the transport requires them.
    /// RttTracker is always initialized for ping display, even for Steam.
    /// </summary>
    private void InitializeManagersIfNeeded() {
        // Always initialize RTT tracker for ping display (all transports)
        _rttTracker ??= new RttTracker();

        if (_requiresSequencing) {
            _receivedSequences ??= [];
            _congestionManager ??= new CongestionManager<TOutgoing, TPacketId>(this, _rttTracker);
        }

        if (RequiresReliability) {
            _reliabilityManager ??= new ReliabilityManager<TOutgoing, TPacketId>(this, _rttTracker);
        }
    }

    /// <summary>
    /// The current send rate in milliseconds between sending packets.
    /// </summary>
    public int CurrentSendRate { get; set; } = CongestionManager<TOutgoing, TPacketId>.HighSendRate;

    /// <summary>
    /// Round trip time to the other end in milliseconds.
    ///
    /// A transport that measures the real time to the other player is believed over anything timed here, as the send
    /// loop last heard it (see <see cref="_transportPingMs"/>). Steam's relayed messaging is the case that matters: it
    /// carries no sequence numbers of its own, so the moving average below has nothing to time against and used to
    /// report the send interval rather than the distance to the other player - the same small number whether the two
    /// were in one room or on different continents.
    /// </summary>
    public int AverageRtt {
        get {
            var ping = _transportPingMs;
            if (ping >= 0) {
                return ping;
            }

            return _rttTracker != null ? (int) System.Math.Round(_rttTracker.AverageRtt) : 0;
        }
    }

    /// <summary>
    /// Event that is called when the client times out.
    /// </summary>
    public event Action? TimeoutEvent;

    /// <summary>
    /// Action to enqueue a packet for chunked sending.
    /// </summary>
    public Action<Packet.Packet>? EnqueueChunkPacketAction { get; set; }

    /// <summary>
    /// Enqueues a packet to be sent reliably as a chunk to the destination.
    /// </summary>
    /// <param name="packet">The packet to send.</param>
    public void SendChunkPacket(Packet.Packet packet) => EnqueueChunkPacketAction?.Invoke(packet);

    /// <summary>
    /// Construct the update manager with a UDP socket.
    /// </summary>
    protected UpdateManager() {
        CurrentUpdatePacket = new TOutgoing();
    }

    /// <summary>
    /// Start the update manager. This will start the send thread and heartbeat timer.
    /// </summary>
    public void StartUpdates() {
        _lastReceiveTime = DateTime.UtcNow;

        _cancellationTokenSource = new CancellationTokenSource();
        _sendThread = new Thread(SendLoop) {
            IsBackground = true,
            Name = "SSMP Send Loop",
            Priority = ThreadPriority.Normal
        };
        _sendThread.Start();
        _isUpdating = true;
    }

    /// <summary>
    /// Stop sending the periodic UDP update packets after sending the current one.
    /// </summary>
    public void StopUpdates() {
        if (!_isUpdating) {
            return;
        }

        _isUpdating = false;

        Logger.Debug("Stopping UDP updates, sending last packet");
        // TODO: Split "flush final packet" from teardown so disconnect/shutdown does not depend on a synchronous
        // TODO: transport write on the caller thread.
        CreateAndSendPacket();
        _cancellationTokenSource?.Cancel();

        lock (Lock) {
            Monitor.PulseAll(Lock);
        }

        // Wait for thread to finish before disposing shared resources
        if (Thread.CurrentThread != _sendThread)
            _sendThread?.Join();

        _sendThread = null;
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
    }

    /// <summary>
    /// Callback method for when a packet is received.
    /// </summary>
    /// <param name="packet">The received packet.</param>
    /// <typeparam name="TIncoming">The type of the incoming packet.</typeparam>
    /// <typeparam name="TOtherPacketId">The packet ID type of the incoming packet.</typeparam>
    public void OnReceivePacket<TIncoming, TOtherPacketId>(TIncoming packet)
        where TIncoming : UpdatePacket<TOtherPacketId>
        where TOtherPacketId : Enum {
        // Reset the connection timeout timer, saying how long the stretch was if anything was missed in it. The log
        // said when a quiet stretch began and never when it ended, so how long one had lasted could only be guessed.
        var now = DateTime.UtcNow;
        var gap = (now - _lastReceiveTime).TotalSeconds;
        _lastReceiveTime = now;
        _timeoutGraceTold = false;
        if (gap >= ReceiveGapTold) {
            Logger.Info($"Packets arrive again after {gap:F1}s in which none did");
            try {
                ReceiveResumed?.Invoke(gap);
            } catch (Exception e) {
                Logger.Warn($"Could not take in the end of a gap in what arrives: {e.Message}");
            }
        }

        // A transport without sequence numbers has nothing here to acknowledge: the packet that just arrived answers
        // nothing that was sent. Closing the round trip of the last sent sequence anyway, as this used to, timed the
        // gap between sending and the next arrival of anything - the send interval - and fed that to the tracker as
        // though it were the distance to the other player. Such a transport reports its own ping instead.
        if (!_requiresSequencing) {
            return;
        }

        // Transports requiring sequencing: Handle congestion, sequence tracking, and deduplication
        // Notify RTT tracker and reliability manager of received ACKs
        NotifyAckReceived(packet.Ack);

        // Process ACK field efficiently with cached reference
        var ackField = packet.AckField;
        for (ushort i = 0; i < ConnectionManager.AckSize; i++) {
            if (ackField[i]) {
                var sequenceToCheck = (ushort) (packet.Ack - i - 1);
                NotifyAckReceived(sequenceToCheck);
            }
        }

        _congestionManager?.OnReceivePacket();
        var sequence = packet.Sequence;

        // Add sequence to received set and manage window size
        AddReceivedSequence(sequence);

        // Check for duplicate resend data using O(1) HashSet lookup
        packet.DropDuplicateResendData(_receivedSequences!);

        if (IsSequenceGreaterThan(sequence, _remoteSequence)) {
            _remoteSequence = sequence;
        }
    }

    /// <summary>
    /// Creates an update packet with current data and sends it through the transport.
    /// For UDP/HolePunch: handles sequence numbers, ACK fields, and congestion management.
    /// For Steam: bypasses reliability features and sends packet directly.
    /// Automatically fragments packets that exceed MTU size.
    /// </summary>
    private void CreateAndSendPacket() {
        var rawPacket = new Packet.Packet();
        TOutgoing packetToSend;

        lock (Lock) {
            // Numbered for every transport, not only the ones this mod keeps reliable itself. The number is written
            // into the packet either way, so this costs nothing, and without it there is no way at all to tell an
            // older packet from a newer one on a transport that does not order what it carries - which is how an
            // enemy ends up back where it was standing before a player hit it.
            CurrentUpdatePacket.Sequence = _localSequence;

            // Transports requiring sequencing: Configure ACK data
            if (_requiresSequencing) {
                CurrentUpdatePacket.Ack = _remoteSequence;
                // TODO: PopulateAckField is called while Lock is already held and still re-locks internally.
                // TODO: Flatten this re-entrant lock boundary in a dedicated concurrency cleanup pass.
                PopulateAckField();
            }

            try {
                CurrentUpdatePacket.CreatePacket(rawPacket);
            } catch (Exception e) {
                // What cannot be written is let go of, and said. Kept, it stayed in the packet that every later tick
                // adds to, which then failed the same way for good: nothing was ever sent again, while the connection
                // looked alive. The rest of what was to go - much of it reliable, which nothing would send again -
                // still goes.
                var dropped = CurrentUpdatePacket.DropUnwritableData();
                Logger.Error(
                    $"Failed to create packet, so what could not be written is dropped " +
                    $"({(dropped.Count > 0 ? string.Join(", ", dropped) : "none found")}):\n{e}"
                );

                rawPacket = new Packet.Packet();
                try {
                    CurrentUpdatePacket.CreatePacket(rawPacket);
                } catch (Exception again) {
                    Logger.Error($"Failed to create packet again, so all that was in it is dropped:\n{again}");
                    CurrentUpdatePacket = new TOutgoing();
                    Monitor.PulseAll(Lock);
                    return;
                }
            }

            // Reset the packet by creating a new instance,
            // but keep the original instance for reliability data re-sending
            packetToSend = CurrentUpdatePacket;
            CurrentUpdatePacket = new TOutgoing();

            // Pulse to wake up any threads waiting to set new slice data
            Monitor.PulseAll(Lock);
        }

        // Track send time for RTT measurement (all transports)
        _rttTracker?.OnSendPacket(_localSequence);

        // Transports requiring sequencing: reliability and sequence increment
        if (_requiresSequencing) {
            if (RequiresReliability) {
                _reliabilityManager!.OnSendPacket(_localSequence, packetToSend);
            }
        }

        // For Steam: increment sequence for RTT tracking purposes only
        lock (Lock) {
            _localSequence++;
        }

        if (NumbersReliable) {
            SendNumbered(rawPacket.ToArray(), packetToSend.ReliableHeaderAt, packetToSend.ContainsReliableData);
            return;
        }

        SendWithFragmentation(rawPacket, packetToSend.ContainsReliableData);
    }

    /// <summary>
    /// Sends a packet on a transport whose packets with reliable data are numbered (see
    /// <see cref="ReliableNumbering"/>): first the packets that the other side asked for again, then this one, numbered
    /// and kept until the other side took it in if it carries reliable data, and saying what this side took in from the
    /// other.
    /// </summary>
    /// <param name="raw">The packet as it is sent.</param>
    /// <param name="headerAt">Where its numbering goes.</param>
    /// <param name="reliable">Whether it carries reliable data.</param>
    private void SendNumbered(byte[] raw, int headerAt, bool reliable) {
        // Held for all of it, so that what goes again and what goes now reach the transport in the order of their
        // numbers, whichever thread sends
        lock (_transportSendLock) {
            SendAgainIfAsked();

            lock (_reliableLock) {
                var flags = WhatWasTakenIn(out var ack);

                // One without reliable data says how far the numbering went, so that the other side still asks for
                // the last ones if they were lost and nothing with reliable data follows them
                var number = _lastReliableSent;
                if (reliable) {
                    number = ++_lastReliableSent;
                    flags |= NumberingFlags.Numbered;
                }

                ReliableNumbering.Stamp(raw, headerAt, flags, number, ack);
                if (reliable) {
                    KeepToSendAgain(new KeptReliable(number, raw, headerAt));
                }
            }

            SendWhole(raw, reliable);
        }
    }

    /// <summary>
    /// Sends a packet as it is sent, in one piece if the transport carries it in one.
    /// </summary>
    /// <param name="raw">The packet as it is sent.</param>
    /// <param name="reliable">Whether it carries reliable data.</param>
    private void SendWhole(byte[] raw, bool reliable) {
        if (raw.Length <= _transportMaxPacketSize) {
            SendBytes(raw, reliable);
            return;
        }

        SendWithFragmentation(new Packet.Packet(raw), reliable);
    }

    /// <summary>
    /// What a packet sent now says of what this side took in from the other: the number of the last packet with
    /// reliable data that it took in, in order, and whether it asks for the ones after it again, which it does while
    /// it holds packets that arrived before one of them, and once it has taken in less than the other side said it
    /// sent for <see cref="AskWhenBehindFor"/>.
    /// </summary>
    /// <param name="ack">The number of the last packet with reliable data taken in.</param>
    /// <returns>What the packet says.</returns>
    private NumberingFlags WhatWasTakenIn(out ushort ack) {
        ack = _lastReliableTaken;
        var flags = NumberingFlags.TookIn;
        if (_heldForMissing.Count > 0 || _behindSince != DateTime.MinValue &&
            (DateTime.UtcNow - _behindSince).TotalMilliseconds >= AskWhenBehindFor) {
            flags |= NumberingFlags.AsksAgain;
        }

        return flags;
    }

    /// <summary>
    /// Keeps a packet with reliable data until the other side says it took it in, letting go of the oldest one when
    /// there is no room left, which a link that is up never comes near: it is given up on long before.
    /// </summary>
    /// <param name="kept">The packet.</param>
    private void KeepToSendAgain(KeptReliable kept) {
        if (_keptToSendAgain.Count >= MostKeptToSendAgain) {
            var dropped = _keptToSendAgain.Dequeue();
            if (!_keptFullTold) {
                _keptFullTold = true;
                Logger.Warn(
                    $"The other side has not taken in {MostKeptToSendAgain} packets with reliable data, so packet " +
                    $"{dropped.Number} and any older one after it can no longer be sent again"
                );
            }
        }

        _keptToSendAgain.Enqueue(kept);
    }

    /// <summary>
    /// Sends the packets with reliable data that the other side has not taken in again, in the order of their numbers:
    /// all of them once the link was opened anew, which loses what was on its way over it, and when the other side asks
    /// for them, the ones that went long enough ago to be lost rather than on their way (see
    /// <see cref="KeptReliable.DueAgain"/>). They go as they went the first time, saying what this side took in now,
    /// as many in one message as the transport carries: each message waits for the lock of Steam's networking, which
    /// is slow while a link is being made.
    /// </summary>
    private void SendAgainIfAsked() {
        List<KeptReliable>? again = null;
        string why;
        NumberingFlags flags;
        ushort ack;
        lock (_reliableLock) {
            if (_sendAgainNow == null && !_sendAgainAsked) {
                return;
            }

            var all = _sendAgainNow != null;
            why = _sendAgainNow ?? "the other side asked for them";
            _sendAgainNow = null;
            _sendAgainAsked = false;

            var now = DateTime.UtcNow;
            foreach (var kept in _keptToSendAgain) {
                if (all || kept.DueAgain(now)) {
                    kept.SentAgain(now);
                    (again ??= []).Add(kept);
                }
            }

            if (again == null) {
                return;
            }

            flags = WhatWasTakenIn(out ack) | NumberingFlags.Numbered;
        }

        var bytes = 0;
        var messages = 0;
        var together = new List<byte[]>();
        var togetherLength = 0;
        foreach (var kept in again) {
            ReliableNumbering.Stamp(kept.Raw, kept.HeaderAt, flags, kept.Number, ack);
            bytes += kept.Raw.Length;
            if (togetherLength + kept.Raw.Length > _transportMaxPacketSize) {
                messages += SendTogether(together, togetherLength);
                togetherLength = 0;
            }

            together.Add(kept.Raw);
            togetherLength += kept.Raw.Length;
        }

        messages += SendTogether(together, togetherLength);

        // A packet goes again as soon as it is due while the other side asks, which can be one every send, so this is
        // said at most every few seconds, with what went since
        var toldAt = DateTime.UtcNow;
        if ((toldAt - _sentAgainToldAt).TotalMilliseconds < SendAgainAfter) {
            _sentAgainUntold += again.Count;
            _sendingsAgainUntold++;
            return;
        }

        var more = _sendingsAgainUntold > 0
            ? $"; {_sentAgainUntold} more went again in {_sendingsAgainUntold} sending(s) since the last line"
            : "";
        _sentAgainToldAt = toldAt;
        _sentAgainUntold = 0;
        _sendingsAgainUntold = 0;
        Logger.Info(
            $"Sent the {again.Count} packet(s) with reliable data that the other side has not taken in again, " +
            $"{again[0].Number} to {again[^1].Number} ({bytes / 1024f:F1} kB in {messages} message(s)), as {why}" +
            more
        );
    }

    /// <summary>
    /// When sending packets with reliable data again was last said in the log.
    /// </summary>
    private DateTime _sentAgainToldAt = DateTime.MinValue;

    /// <summary>
    /// How many packets went again since it was last said, and in how many sendings.
    /// </summary>
    private int _sentAgainUntold, _sendingsAgainUntold;

    /// <summary>
    /// Sends whole packets with reliable data as one message, one after the other, which the other side takes apart
    /// again (PacketManager.HandleReceivedData), and empties the list. A single packet that is longer than the
    /// transport carries in one message goes in pieces.
    /// </summary>
    /// <param name="packets">The packets as they are sent.</param>
    /// <param name="length">Their length together.</param>
    /// <returns>How many messages were sent: none for no packets, else one.</returns>
    private int SendTogether(List<byte[]> packets, int length) {
        if (packets.Count == 0) {
            return 0;
        }

        if (packets.Count == 1) {
            SendWhole(packets[0], true);
            packets.Clear();
            return 1;
        }

        var joined = new byte[length];
        var at = 0;
        foreach (var raw in packets) {
            Buffer.BlockCopy(raw, 0, joined, at, raw.Length);
            at += raw.Length;
        }

        packets.Clear();
        SendBytes(joined, true);
        return 1;
    }

    /// <summary>
    /// Takes in a packet that arrived on a transport whose packets with reliable data are numbered (see
    /// <see cref="ReliableNumbering"/>), and says which packets are to be taken in now, in this order. A packet without
    /// reliable data goes at once. One with reliable data goes in the order it was sent: one that arrives before a
    /// packet sent earlier waits for it, and the other side is asked for what is missing, which it sends again; one
    /// that was taken in already goes nowhere. What the packet says the other side took in lets go of what this side
    /// kept for sending again, and when the other side asks for what it has not taken in again, that is sent again.
    /// A packet on a transport that this mod keeps reliable itself carries none of this and goes at once.
    /// </summary>
    /// <param name="packet">The packet that arrived.</param>
    /// <typeparam name="TIncoming">The type of the incoming packet.</typeparam>
    /// <typeparam name="TOtherPacketId">The packet ID type of the incoming packet.</typeparam>
    /// <returns>The packets to take in now, in order, which may be none.</returns>
    public List<TIncoming> TakeInOrder<TIncoming, TOtherPacketId>(TIncoming packet)
        where TIncoming : UpdatePacket<TOtherPacketId>
        where TOtherPacketId : Enum {
        var inOrder = new List<TIncoming>();
        if (!NumbersReliable) {
            inOrder.Add(packet);
            return inOrder;
        }

        var flags = packet.Numbering;
        lock (_reliableLock) {
            var now = DateTime.UtcNow;
            if (_heldForMissing.Count > 0) {
                _heldWaited += System.Math.Min((now - _lastArrival).TotalMilliseconds, HeldWaitStepAtMost);
            }

            _lastArrival = now;

            if ((flags & NumberingFlags.TookIn) != 0) {
                LetGoOfTakenIn(packet.ReliableAck);
                if (_sendAgainOnNextAck) {
                    _sendAgainOnNextAck = false;
                    _sendAgainNow = "the link was opened anew, which loses what was on its way over it";
                }
            }

            if ((flags & NumberingFlags.AsksAgain) != 0) {
                _sendAgainAsked = true;
            }

            if ((flags & NumberingFlags.Numbered) == 0) {
                inOrder.Add(packet);
            } else {
                TakeNumbered(packet, packet.ReliableNumber, inOrder);
            }

            if ((flags & NumberingFlags.TookIn) != 0) {
                HearSentUpTo(packet.ReliableNumber);
            }

            if (_heldForMissing.Count > 0 && _heldWaited > HoldForMissingAtMost) {
                Logger.Warn(
                    $"The packets with reliable data from {(ushort) (_lastReliableTaken + 1)} on did not arrive in " +
                    $"{(DateTime.UtcNow - _heldSince).TotalSeconds:F1}s ({_heldWaited / 1000:F1}s of it while " +
                    $"things arrived), so the {_heldForMissing.Count} that waited for them are taken in without them"
                );
                TakeAllHeld(inOrder);
            }

            WatchBehind();
        }

        // Nothing of a packet that could not be read is handled; its number was taken in all the same
        inOrder.RemoveAll(taken => taken.OnlyHeadersRead);
        return inOrder;
    }

    /// <summary>
    /// Takes note of how far the other side says its numbering of packets with reliable data went: the number of a
    /// packet with reliable data, or the one that a packet without reliable data names. A number before the furthest
    /// one heard of, like that of a packet sent again, says nothing new.
    /// </summary>
    /// <param name="number">The number.</param>
    private void HearSentUpTo(ushort number) {
        var ahead = (ushort) (number - _otherSentUpTo);
        if (ahead is > 0 and <= MostKeptToSendAgain) {
            _otherSentUpTo = number;
        }
    }

    /// <summary>
    /// Notes since when this side has taken in less than the other side said it sent, which it asks for again after
    /// <see cref="AskWhenBehindFor"/> (see <see cref="WhatWasTakenIn"/>): what a link opened anew lost on its way
    /// here, which nothing else shows when nothing with reliable data comes after it, like the death of a player who
    /// lies still in a cocoon.
    /// </summary>
    private void WatchBehind() {
        var behind = (ushort) (_otherSentUpTo - _lastReliableTaken);
        if (behind is 0 or > MostKeptToSendAgain) {
            _behindSince = DateTime.MinValue;
        } else if (_behindSince == DateTime.MinValue) {
            _behindSince = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Takes in a packet with reliable data by its number: at once if it is the next one, along with the ones held for
    /// it; held if it arrived before one sent earlier; not at all if it was taken in already.
    /// </summary>
    /// <param name="packet">The packet.</param>
    /// <param name="number">Its number.</param>
    /// <param name="inOrder">The packets to take in now, which it adds to.</param>
    private void TakeNumbered<TIncoming>(TIncoming packet, ushort number, List<TIncoming> inOrder)
        where TIncoming : class {
        var ahead = (ushort) (number - _lastReliableTaken);
        if (ahead == 1) {
            _lastReliableTaken = number;
            inOrder.Add(packet);
            TakeHeldInOrder(inOrder);

            // Said once the run of them is over, which the next new one shows
            if (_arrivedAgain > 0) {
                Logger.Info(
                    $"{_arrivedAgain} packet(s) with reliable data that were taken in already arrived again, sent " +
                    "again by the other side, and were let go"
                );
                _arrivedAgain = 0;
            }

            return;
        }

        // Taken in already: one sent again that had arrived the first time after all
        if (ahead == 0 || ahead > ushort.MaxValue - MostKeptToSendAgain) {
            _arrivedAgain++;
            return;
        }

        // Too far from the last one to be of the same run of numbers, which only numbering that started over gives
        if (ahead > MostKeptToSendAgain) {
            Logger.Warn(
                $"A packet with reliable data numbered {number} arrived after {_lastReliableTaken} was taken in, too " +
                "far apart to be of the same run of numbers, so they are taken to have started over"
            );
            TakeAllHeld(inOrder);
            _lastReliableTaken = number;
            _otherSentUpTo = number;
            inOrder.Add(packet);
            return;
        }

        if (_heldForMissing.Count == 0) {
            _heldSince = DateTime.UtcNow;
            _heldWaited = 0;
            Logger.Info(
                $"The packet with reliable data numbered {number} arrived before {ahead - 1} sent before it, so it " +
                "waits for them, and the other side is asked for them again"
            );
        }

        _heldForMissing.TryAdd(number, packet);
    }

    /// <summary>
    /// Takes in the held packets that come next, in order, now that the one before them was taken in.
    /// </summary>
    /// <param name="inOrder">The packets to take in now, which it adds to.</param>
    private void TakeHeldInOrder<TIncoming>(List<TIncoming> inOrder) where TIncoming : class {
        if (_heldForMissing.Count == 0) {
            return;
        }

        var taken = 0;
        while (_heldForMissing.Remove((ushort) (_lastReliableTaken + 1), out var next)) {
            _lastReliableTaken++;
            inOrder.Add((TIncoming) next);
            taken++;
        }

        if (taken > 0 && _heldForMissing.Count == 0) {
            Logger.Info(
                $"The packets with reliable data that were missing arrived after " +
                $"{(DateTime.UtcNow - _heldSince).TotalSeconds:F1}s, so the {taken} that waited for them are " +
                "taken in now"
            );
        } else if (taken > 0) {
            // The ones still held wait for a later gap, which they are given the whole time for from now
            _heldSince = DateTime.UtcNow;
            _heldWaited = 0;
        }
    }

    /// <summary>
    /// Takes in every held packet in the order of their numbers, without the ones missing between them.
    /// </summary>
    /// <param name="inOrder">The packets to take in now, which it adds to.</param>
    private void TakeAllHeld<TIncoming>(List<TIncoming> inOrder) where TIncoming : class {
        var last = _lastReliableTaken;
        var held = _heldForMissing.OrderBy(pair => (ushort) (pair.Key - last)).ToList();
        _heldForMissing.Clear();
        foreach (var pair in held) {
            _lastReliableTaken = pair.Key;
            inOrder.Add((TIncoming) pair.Value);
        }
    }

    /// <summary>
    /// Lets go of the kept packets with reliable data that the other side says it took in: the one with the given
    /// number and every one before it.
    /// </summary>
    /// <param name="ack">The number of the last packet with reliable data that the other side took in.</param>
    private void LetGoOfTakenIn(ushort ack) {
        while (_keptToSendAgain.Count > 0 &&
               (ushort) (ack - _keptToSendAgain.Peek().Number) <= MostKeptToSendAgain) {
            _keptToSendAgain.Dequeue();
            _keptFullTold = false;
        }
    }

    /// <summary>
    /// What follows opening the link anew, which loses what was on its way over the old one both ways: what this side
    /// sent with reliable data and the other side has not taken in goes again once the next packet that arrives says
    /// what the other side took in. What the other side sent is asked for once this side hears how far it went (see
    /// <see cref="WatchBehind"/>).
    /// </summary>
    private void SendAgainAfterRenewal() {
        if (!NumbersReliable) {
            return;
        }

        lock (_reliableLock) {
            _sendAgainOnNextAck = true;
        }
    }

    /// <summary>
    /// A packet with reliable data as it was sent, kept until the other side says it took it in.
    /// </summary>
    /// <param name="number">Its number.</param>
    /// <param name="raw">It as it was sent.</param>
    /// <param name="headerAt">Where its numbering is.</param>
    private sealed class KeptReliable(ushort number, byte[] raw, int headerAt) {
        /// <summary>
        /// Its number.
        /// </summary>
        public ushort Number { get; } = number;

        /// <summary>
        /// It as it was sent.
        /// </summary>
        public byte[] Raw { get; } = raw;

        /// <summary>
        /// Where its numbering is.
        /// </summary>
        public int HeaderAt { get; } = headerAt;

        /// <summary>
        /// When it last went.
        /// </summary>
        private DateTime _sentAt = DateTime.UtcNow;

        /// <summary>
        /// How often it went again.
        /// </summary>
        private int _timesSentAgain;

        /// <summary>
        /// Whether it went long enough ago to go again when the other side asks for what it has not taken in (see
        /// <see cref="SendAgainAfter"/>).
        /// </summary>
        /// <param name="now">The time now.</param>
        /// <returns>Whether it goes again.</returns>
        public bool DueAgain(DateTime now) =>
            (now - _sentAt).TotalMilliseconds >=
            SendAgainAfter << System.Math.Min(_timesSentAgain, SendAgainDoublingsAtMost);

        /// <summary>
        /// Notes that it goes again now.
        /// </summary>
        /// <param name="now">The time now.</param>
        public void SentAgain(DateTime now) {
            _sentAt = now;
            _timesSentAgain++;
        }
    }

    /// <summary>
    /// Populates the ACK field with acknowledgment bits for recently received packets.
    /// Each bit indicates whether a packet with that sequence number was received.
    /// Only used for UDP/HolePunch transports.
    /// Uses linear search (Contains) which is efficient for small size (N=64).
    /// </summary>
    private void PopulateAckField() {
        var ackField = CurrentUpdatePacket.AckField;

        lock (Lock) {
            for (ushort i = 0; i < ConnectionManager.AckSize; i++) {
                var pastSequence = (ushort) (_remoteSequence - i - 1);
                ackField[i] = _receivedSequences!.Contains(pastSequence);
            }
        }
    }

    /// <summary>
    /// Adds a received sequence number to the tracking set and maintains window size.
    /// Removes sequences outside the ACK window to prevent unbounded growth.
    /// </summary>
    /// <param name="sequence">The sequence number to add.</param>
    private void AddReceivedSequence(ushort sequence) {
        lock (Lock) {
            _receivedSequences!.Add(sequence);

            // Prune sequences outside the ACK window to prevent unbounded growth
            // Keep sequences within (remoteSequence - AckSize) to remoteSequence
            if (_receivedSequences.Count > ConnectionManager.AckSize) {
                var threshold = (ushort) (_remoteSequence - ConnectionManager.AckSize);
                _receivedSequences.RemoveAll(seq =>
                    !IsSequenceGreaterThan(seq, threshold) && seq != _remoteSequence
                );
            }
        }
    }

    /// <summary>
    /// Sends a packet, fragmenting it into smaller chunks if it exceeds the MTU size.
    /// Fragments are sent sequentially to ensure they can be reassembled by the receiver.
    /// Uses CopyTo to avoid an extra full-buffer ToArray() allocation when fragmenting.
    /// </summary>
    /// <param name="packet">The packet to send, which may be fragmented if too large.</param>
    /// <param name="isReliable">Whether the packet data needs to be delivered reliably.</param>
    private void SendWithFragmentation(Packet.Packet packet, bool isReliable) {
        // What the transport underneath says it carries, rather than one size for all of them. A transport that
        // takes the whole thing never reaches the loop below, which is the point: the pieces it would make carry no
        // mark of their own, so the far end can only glue them back together in the order they arrive.
        var mostInOnePiece = _transportMaxPacketSize;
        if (packet.Length <= mostInOnePiece) {
            SendPacket(packet, isReliable);
            return;
        }

        // Held for the whole run of pieces and not for each one on its own. The far end has nothing to tell one
        // piece from another by, so anything that goes out between two of them is glued into the middle of this
        // packet - and the sending is done from more than one thread.
        lock (_transportSendLock) {
            // Copy directly into fragment buffers, avoiding an intermediate ToArray() of the full packet
            var remaining = packet.Length;
            var offset = 0;

            while (remaining > 0) {
                var chunkSize = System.Math.Min(remaining, mostInOnePiece);
                var fragment = new byte[chunkSize];

                packet.CopyTo(fragment, 0, offset, chunkSize);

                SendPacket(new Packet.Packet(fragment), isReliable);

                offset += chunkSize;
                remaining -= chunkSize;
            }
        }
    }

    /// <summary>
    /// Notifies RTT tracker and reliability manager that an ACK was received for the given sequence.
    /// </summary>
    /// <param name="sequence">The acknowledged sequence number.</param>
    private void NotifyAckReceived(ushort sequence) {
        _rttTracker?.OnAckReceived(sequence);
        _reliabilityManager?.OnAckReceived(sequence);
    }

    /// <summary>
    /// Resets the update manager state, clearing queues and sequences.
    /// </summary>
    public void Reset() {
        StopUpdates();

        // Said again for a new connection, which the last one's silence must not keep quiet
        _timeoutGraceTold = false;

        lock (Lock) {
            _receivedSequences?.Clear();
            _localSequence = 0;
            _remoteSequence = 0;
            CurrentUpdatePacket = new TOutgoing();

            _rttTracker?.Reset();

            if (RequiresReliability && _rttTracker != null) {
                _reliabilityManager = new ReliabilityManager<TOutgoing, TPacketId>(this, _rttTracker);
            }

            if (_requiresSequencing && _rttTracker != null) {
                _congestionManager = new CongestionManager<TOutgoing, TPacketId>(this, _rttTracker);
            }
        }

        // A new connection numbers its packets with reliable data from the start, as the other side's does
        lock (_reliableLock) {
            _lastReliableSent = 0;
            _keptToSendAgain.Clear();
            _keptFullTold = false;
            _sendAgainAsked = false;
            _sendAgainOnNextAck = false;
            _sendAgainNow = null;
            _lastReliableTaken = 0;
            _otherSentUpTo = 0;
            _behindSince = DateTime.MinValue;
            _heldForMissing.Clear();
            _arrivedAgain = 0;
        }
    }

    /// <summary>
    /// Has the transport write down how its link to the other side has stood each second, for the last
    /// <paramref name="secondsBack"/> seconds and from then on until things arrive from the other side again; nothing
    /// for a transport that keeps no record of it. For a stretch in which nothing arrives, so that it can be told
    /// afterwards where things stopped.
    /// </summary>
    /// <param name="why">Why, as it ends a sentence of the log: "written down as ...".</param>
    /// <param name="secondsBack">How many seconds of the record to write down first.</param>
    public void RecordTransportSession(string why, int secondsBack) {
        try {
            _transportRecordSession?.Invoke(why, secondsBack);
        } catch (Exception e) {
            Logger.Warn($"Could not write down how the link stands: {e.Message}");
        }
    }

    /// <summary>
    /// How the transport's link to the other side stands, as a part to add to a line of the log, or nothing for a
    /// transport that cannot say.
    /// </summary>
    private string DescribeTransportSession() {
        var described = _transportDescribeSession?.Invoke();
        return string.IsNullOrEmpty(described) ? "" : $" ({described})";
    }

    /// <summary>
    /// Precision loop running on a dedicated thread to send updates at regular intervals.
    /// Uses ManualResetEventSlim for efficient waiting with cancellation support.
    /// </summary>
    private void SendLoop() {
        var stopwatch = Stopwatch.StartNew();
        var nextSendTime = stopwatch.ElapsedMilliseconds;
        var nextPingTime = nextSendTime;
        using var waitHandle = new ManualResetEventSlim(false);
        var cts = _cancellationTokenSource;
        if (cts == null) {
            return;
        }

        var token = cts.Token;

        // Safety constant: how many ms can we fall behind before giving up?
        const long maxLagMS = 500;

        while (!token.IsCancellationRequested) {
            try {
                var currentTime = stopwatch.ElapsedMilliseconds;

                // Drift correction: if we fell too far behind (e.g. system freeze), reset to now
                if (currentTime > nextSendTime + maxLagMS) {
                    nextSendTime = currentTime;
                }

                var waitTime = nextSendTime - currentTime;
                if (waitTime > 0) {
                    try {
                        waitHandle.Wait((int) waitTime, token);
                    } catch (OperationCanceledException) {
                        break;
                    }
                }

                if (currentTime >= nextPingTime) {
                    nextPingTime = currentTime + TransportPingEvery;
                    AskTransportPing();
                }

                // Check for connection timeout
                // We use DateTime.UtcNow which is consistent with _lastReceiveTime
                var lastReceive = _lastReceiveTime;
                var quietFor = (DateTime.UtcNow - lastReceive).TotalMilliseconds;
                WatchLink(lastReceive, quietFor);
                if (quietFor > ConnectionTimeout) {
                    // Ask the transport before giving up on the other player. A relay between continents can stop
                    // delivering for seconds at a time without the session being gone: one player was dropped in
                    // the middle of a fight while the host's own log showed it sending throughout and Steam never
                    // reported the session as failed.
                    //
                    // Asked about the session, not about the round trip. Asking for a round trip was the first
                    // attempt at this and it dropped a player anyway, because a relay rebuilding a broken session
                    // has a session and no measurement of it yet - which read as "there is no link" at the one
                    // moment the link was coming back. The transports that cannot answer at all keep exactly the
                    // behaviour they had.
                    var sessionUp = _transportSessionUp?.Invoke() == true;
                    var stillUp = quietFor < ConnectionTimeoutCeiling && sessionUp;
                    if (!stillUp) {
                        // Which of the two it was, because nothing else in the log can tell them apart afterwards
                        // and they call for opposite answers: one is a connection that really went away, the other
                        // is this giving up on one that was merely slow.
                        Logger.Info(
                            sessionUp
                                ? $"Nothing has arrived for {quietFor / 1000:F1}s, which is as long as a quiet " +
                                  "connection is kept, so this counts as a disconnect" + DescribeTransportSession()
                                : $"Nothing has arrived for {quietFor / 1000:F1}s and the transport says the " +
                                  "session is over, so this counts as a disconnect" + DescribeTransportSession()
                        );

                        // Already being written down if the stretch was told of as it began, which it was unless this
                        // loop stood still for all of it
                        RecordQuietStretch(quietFor);

                        TimeoutEvent?.Invoke();
                        // We don't break immediately, we might want to let the user decide via the event (e.g. disconnect)
                        // usually the event handler will call Disconnect() which stops updates.
                        // However, we must stop checking to avoid spamming the event in this loop.
                        break;
                    }

                    if (!_timeoutGraceTold) {
                        _timeoutGraceTold = true;
                        Logger.Info(
                            $"Nothing has arrived for {quietFor / 1000:F1}s, but the transport still has the " +
                            "session, so this is not being taken as a disconnect yet" + DescribeTransportSession()
                        );
                        RecordQuietStretch(quietFor);
                    }

                    RenewStalledLink(quietFor);
                }

                // Send Packet
                CreateAndSendPacket();

                // Calculate next target time
                // Add CurrentSendRate to preserve cadence and avoid drift
                nextSendTime += CurrentSendRate;
            } catch (Exception e) {
                Logger.Error($"Critical error in SendLoop: {e}");
                // If an error occurs, we still advance the target time to maintain cadence.
                nextSendTime += CurrentSendRate;
            }
        }
    }

    /// <summary>
    /// Asks the transport for its round trip, which <see cref="AverageRtt"/> gives everyone else from then on.
    /// </summary>
    private void AskTransportPing() {
        try {
            _transportPingMs = _transportPing?.Invoke() is { } ping and >= 0 ? ping : -1;
        } catch (Exception) {
            // Nothing is known of the round trip this time round, which the next asks again
        }
    }

    /// <summary>
    /// Starts the watch of the link over when something arrived since the send loop last looked, and notes when the
    /// link did not count as connected while nothing has arrived for a while: still being built, maybe after the other
    /// side opened it anew, which it is then given <see cref="LinkSettleTime"/> to finish (see
    /// <see cref="RenewStalledLink"/>).
    /// </summary>
    /// <param name="lastReceive">When the last thing arrived.</param>
    /// <param name="quietFor">How long nothing has arrived since, in milliseconds.</param>
    private void WatchLink(DateTime lastReceive, double quietFor) {
        if (lastReceive != _linkStretch) {
            _linkStretch = lastReceive;
            _linkUnsettledAtQuiet = -1;
            _renewedAtQuiet = -1;
            _renewals = 0;
        }

        if (quietFor < LinkWatchFrom || _transportSessionConnected == null) {
            return;
        }

        try {
            if (!_transportSessionConnected()) {
                _linkUnsettledAtQuiet = (int) quietFor;
            }
        } catch (Exception) {
            // Nothing is known of the link this time round, which the next asks again
        }
    }

    /// <summary>
    /// Has the transport open its link to the other side anew, when nothing has arrived over it for
    /// <see cref="RenewLinkAfter"/> although it counts as connected, and again every <see cref="RenewLinkInterval"/>
    /// while still nothing arrives; the next send opens the new link. Giving up still comes at
    /// <see cref="ConnectionTimeoutCeiling"/>.
    ///
    /// USER 10-15 "断线去做": nothing from the host reached the joining player for 30 s while what they sent reached the
    /// host throughout, and Steam called the link connected the whole time; it ended the game, and a new lobby two
    /// minutes later connected at once. What was still on its way over the old link is lost with it, which the
    /// numbering of the packets with reliable data makes good: what either side has not taken in goes again, in order
    /// (see <see cref="ReliableNumbering"/>). For the room, the catching up after a gap in what arrives
    /// (<see cref="ReceiveResumed"/>) makes it good as well.
    /// </summary>
    /// <param name="quietFor">How long nothing has arrived, in milliseconds.</param>
    private void RenewStalledLink(double quietFor) {
        if (_transportRenewSession == null || quietFor < RenewLinkAfter ||
            _renewedAtQuiet >= 0 && quietFor - _renewedAtQuiet < RenewLinkInterval) {
            return;
        }

        // A link that is still being built, or that the other side has just closed to open it anew, is left to finish,
        // and to carry the first things over it once it has (WatchLink)
        if (_linkUnsettledAtQuiet >= 0 && quietFor - _linkUnsettledAtQuiet < LinkSettleTime) {
            return;
        }

        bool renewed;
        try {
            renewed = _transportRenewSession();
        } catch (Exception e) {
            Logger.Warn($"Could not open the link anew: {e.Message}");
            renewed = false;
        }

        if (!renewed) {
            return;
        }

        _renewedAtQuiet = (int) quietFor;
        _renewals++;
        Logger.Info(
            $"Nothing has arrived for {quietFor / 1000:F1}s although the link counted as connected, so it was opened " +
            $"anew (time {_renewals} in this quiet stretch); this is taken as a disconnect only after " +
            $"{ConnectionTimeoutCeiling / 1000}s"
        );

        SendAgainAfterRenewal();
    }

    /// <summary>
    /// Has the transport write down how its link stood through a stretch in which nothing arrived, and a little before
    /// it, and go on until things arrive again (see <see cref="RecordTransportSession"/>).
    /// </summary>
    /// <param name="quietFor">How long nothing has arrived, in milliseconds.</param>
    private void RecordQuietStretch(double quietFor) {
        RecordTransportSession(
            $"nothing has arrived from them for {quietFor / 1000:F1}s",
            (int) System.Math.Ceiling(quietFor / 1000) + SecondsBeforeQuiet
        );
    }

    /// <summary>
    /// Check whether the first given sequence number is greater than the second given sequence number.
    /// Accounts for sequence number wrap-around, by inverse comparison if differences are larger than half
    /// of the sequence number space.
    /// </summary>
    /// <param name="sequence1">The first sequence number to compare.</param>
    /// <param name="sequence2">The second sequence number to compare.</param>
    /// <returns>True if the first sequence number is greater than the second sequence number.</returns>
    private static bool IsSequenceGreaterThan(ushort sequence1, ushort sequence2) {
        return (sequence1 > sequence2 && sequence1 - sequence2 <= SequenceWrapThreshold) ||
               (sequence1 < sequence2 && sequence2 - sequence1 > SequenceWrapThreshold);
    }

    /// <summary>
    /// Resend the given packet that was (supposedly) lost by adding data that needs to be reliable to the
    /// current update packet.
    /// </summary>
    /// <param name="lostPacket">The packet instance that was lost.</param>
    public abstract void ResendReliableData(TOutgoing lostPacket);

    /// <summary>
    /// Sends the given packet over the corresponding medium.
    /// </summary>
    /// <param name="packet">The raw packet instance.</param>
    /// <param name="isReliable">Whether the packet contains reliable data.</param>
    private void SendPacket(Packet.Packet packet, bool isReliable) => SendBytes(packet.ToArray(), isReliable);

    /// <summary>
    /// Sends the bytes of a packet, or of one piece of one, over the corresponding medium. Every transport copies them
    /// before this returns.
    /// </summary>
    /// <param name="buffer">The bytes.</param>
    /// <param name="isReliable">Whether the packet contains reliable data.</param>
    private void SendBytes(byte[] buffer, bool isReliable) {
        var length = buffer.Length;

        lock (_transportSendLock) {
            switch (_transportSender) {
                case IReliableTransport reliableTransport when isReliable:
                    reliableTransport.SendReliable(buffer, 0, length);
                    break;

                case IEncryptedTransport transport:
                    transport.Send(buffer, 0, length);
                    break;

                case IReliableTransportClient reliableTransportClient when isReliable:
                    reliableTransportClient.SendReliable(buffer, 0, length);
                    break;

                case IEncryptedTransportClient transportClient:
                    transportClient.Send(buffer, 0, length);
                    break;
            }
        }
    }

    /// <summary>
    /// Set (non-collection) addon data to be networked for the addon with the given ID.
    /// </summary>
    /// <param name="addonId">The ID of the addon.</param>
    /// <param name="packetId">The ID of the packet data.</param>
    /// <param name="packetIdSize">The size of the packet ID space.</param>
    /// <param name="packetData">The packet data to send.</param>
    public void SetAddonData(
        byte addonId,
        byte packetId,
        byte packetIdSize,
        IPacketData packetData
    ) {
        lock (Lock) {
            Packet.Packet.ValidateSize(packetData);
            var addonPacketData = GetOrCreateAddonPacketData(addonId, packetIdSize);
            addonPacketData.PacketData[packetId] = packetData;
        }
    }

    /// <summary>
    /// Set addon data as a collection to be networked for the addon with the given ID.
    /// </summary>
    /// <param name="addonId">The ID of the addon.</param>
    /// <param name="packetId">The ID of the packet data.</param>
    /// <param name="packetIdSize">The size of the packet ID space.</param>
    /// <param name="packetData">The packet data to send.</param>
    /// <typeparam name="TPacketData">The type of the packet data in the collection.</typeparam>
    /// <exception cref="InvalidOperationException">Thrown if the packet data could not be added.</exception>
    public void SetAddonDataAsCollection<TPacketData>(
        byte addonId,
        byte packetId,
        byte packetIdSize,
        TPacketData packetData
    ) where TPacketData : IPacketData, new() {
        lock (Lock) {
            var addonPacketData = GetOrCreateAddonPacketData(addonId, packetIdSize);
            var isNew = false;

            if (!addonPacketData.PacketData.TryGetValue(packetId, out var existingPacketData)) {
                existingPacketData = new PacketDataCollection<TPacketData>();
                addonPacketData.PacketData[packetId] = existingPacketData;
                isNew = true;
            }

            if (existingPacketData is not RawPacketDataCollection existingDataCollection) {
                throw new InvalidOperationException("Could not add addon data with existing non-collection data");
            }

            var previousCount = existingDataCollection.DataInstances.Count;
            if (packetData is RawPacketDataCollection packetDataAsCollection) {
                existingDataCollection.DataInstances.AddRange(packetDataAsCollection.DataInstances);
            } else {
                existingDataCollection.DataInstances.Add(packetData);
            }

            try {
                Packet.Packet.ValidateSize(existingPacketData);
            } catch {
                var addedCount = existingDataCollection.DataInstances.Count - previousCount;
                if (addedCount > 0) {
                    existingDataCollection.DataInstances.RemoveRange(previousCount, addedCount);
                }

                if (isNew) {
                    addonPacketData.PacketData.Remove(packetId);
                }

                throw;
            }
        }
    }

    /// <summary>
    /// Either get or create an AddonPacketData instance for the given addon.
    /// </summary>
    /// <param name="addonId">The ID of the addon.</param>
    /// <param name="packetIdSize">The size of the packet ID space.</param>
    /// <returns>The addon packet data instance.</returns>
    private AddonPacketData GetOrCreateAddonPacketData(byte addonId, byte packetIdSize) {
        if (CurrentUpdatePacket.TryGetSendingAddonPacketData(addonId, out var addonPacketData)) {
            return addonPacketData;
        }

        addonPacketData = new AddonPacketData(packetIdSize);
        CurrentUpdatePacket.SetSendingAddonPacketData(addonId, addonPacketData);
        return addonPacketData;
    }

    /// <summary>
    /// Sends a slice packet immediately, bypassing the gameplay send loop.
    /// </summary>
    protected void SendSlicePacket(TOutgoing slicePacket) {
        var rawPacket = new Packet.Packet();
        lock (Lock) {
            if (_requiresSequencing) {
                slicePacket.Sequence = _localSequence;
                slicePacket.Ack = _remoteSequence;

                var ackField = slicePacket.AckField;
                for (ushort i = 0; i < ConnectionManager.AckSize; i++) {
                    var pastSequence = (ushort) (_remoteSequence - i - 1);
                    ackField[i] = _receivedSequences!.Contains(pastSequence);
                }
            }

            try {
                slicePacket.CreatePacket(rawPacket);
            } catch (Exception e) {
                Logger.Error($"Failed to create slice packet: {e}");
                return;
            }

            if (_requiresSequencing)
                _localSequence++;
        }

        SendPacket(rawPacket, slicePacket.ContainsReliableData);
    }
}
