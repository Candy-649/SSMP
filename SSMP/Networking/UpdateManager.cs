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
    /// up, until the other side has accepted it: a player joining a host that stops answering is told so after this
    /// long. Several seconds of silence is ordinary on a relay between continents - one route change is enough - and
    /// ending the game for it is worse than waiting a little.
    /// </summary>
    private const int ConnectionTimeoutCeiling = 30000;

    /// <summary>
    /// How long a quiet connection is kept in total, in milliseconds, while the transport still has the session or is
    /// making it anew, once the other side has accepted it (see <see cref="QuietLinkKept"/>). This is where waiting
    /// stops being reasonable, which keeps a host that has frozen from holding the other player there for ever. On
    /// 11-01 nothing came over the relay from the host for 30 s, and Steam had made the link anew less than half a
    /// second after the joining player's game gave up on it at 30 s and went back to the main menu (USER "改吧"). The
    /// chat says "a minute" for it (ClientManager.OnLinkStalled).
    /// </summary>
    private const int ReconnectCeiling = 60000;

    /// <summary>
    /// How long, in milliseconds, nothing has to have arrived before the transport's link is opened anew, while it
    /// still counts as connected (see <see cref="RenewStalledLink"/>). It was 15 s, which left stretches of 13 to 19 s
    /// (09-21, 09-22, 10-09) to come back by themselves; on 11-08 nothing arrived for 19 s, things arrived again 4 s
    /// after the link was opened anew at 15 s, and the user chose 10 s (USER 11-08 "10秒呢"). What was on its way over
    /// the old link is lost; the packets with reliable data among it go again over the new one
    /// (<see cref="SendAgainAfterRenewal"/>).
    /// </summary>
    private const int RenewLinkAfter = 10000;

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
    /// Until when, in UTC ticks, the send loop sends nothing after this side opened the link anew, or 0 (see
    /// <see cref="RenewalHoldMs"/>). Only the send loop reads and writes it.
    /// </summary>
    private long _heldUntil;

    /// <summary>
    /// How long, in milliseconds, this side sends nothing after it opened the link anew, unless the link counts as
    /// connected again before, which the other side opening the new link makes it (see <see cref="HeldAfterRenewal"/>).
    /// Shorter on the host than in a joining game, so that when both open it anew at once, the host's new link reaches
    /// the other game while that one still waits.
    ///
    /// USER 11-08 "我真的死机了": the game stood still for 1.8 s, with the sound crackling, as the host's game opened the
    /// link anew. Steam wrote down the state of the whole game at that moment, which stops it, because a check of its own
    /// failed ('Assert( !IsLocked() )' in Steam's folder 'dumps'), in the code that makes a direct link. The other side,
    /// told of the close, opens the link anew by its next send, and this side's next send, 16 ms after the close, made a
    /// second one: in the other game's log of each time its game stood still on such a close (11-01, 11-04, 11-07), Steam
    /// took apart the old direct link and made a new one in the same moment. With this side waiting, only the other
    /// side's new link is made, which this side takes (USER 11-08 "steam尝试直连是正常的").
    /// </summary>
    protected abstract int RenewalHoldMs { get; }

    /// <summary>
    /// Whether <see cref="LinkStalled"/> was raised in this quiet stretch.
    /// </summary>
    private bool _stallTold;

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
    /// How long nothing has arrived from the other side, in seconds.
    /// </summary>
    public double QuietSeconds => (DateTime.UtcNow - _lastReceiveTime).TotalSeconds;

    /// <summary>
    /// How long the oldest packet with reliable data that the other side has not said it took in has waited since it
    /// was first sent, in seconds, or 0 when the other side took in all that was sent. The other side says what it took
    /// in with every packet, so on a link that is up this is a round trip and a send at most; it grows as soon as the
    /// link is held up either way, even while some things still arrive.
    /// </summary>
    public double UntakenSeconds {
        get {
            var ticks = Volatile.Read(ref _oldestUntakenTicks);
            return ticks == 0 ? 0 : (DateTime.UtcNow.Ticks - ticks) / (double) TimeSpan.TicksPerSecond;
        }
    }

    /// <summary>
    /// Whether the other side has accepted this connection since it started (see <see cref="MarkAccepted"/>), after
    /// which a quiet stretch is kept for <see cref="ReconnectCeiling"/> rather than
    /// <see cref="ConnectionTimeoutCeiling"/>. Not whether anything has come over it: a host's server sends to a
    /// joining player from their first message on, before it has accepted them, which made a join that stalls wait
    /// as long as a game that does (review).
    /// </summary>
    private volatile bool _accepted;

    /// <summary>
    /// How long a quiet connection is kept in total, in milliseconds, while the transport still has the session or is
    /// making it anew: longer once the other side has accepted it.
    /// </summary>
    private int QuietLinkKept => _accepted ? ReconnectCeiling : ConnectionTimeoutCeiling;

    /// <summary>
    /// Says that the other side has accepted this connection: the server has taken the joining player in. From then on
    /// a quiet stretch is kept for <see cref="ReconnectCeiling"/>, and told of (<see cref="LinkStalled"/>).
    /// </summary>
    public void MarkAccepted() => _accepted = true;

    /// <summary>
    /// Whether nothing has arrived over a connection the other side has accepted for as long as
    /// <see cref="LinkStalled"/> waits. Read where <see cref="LinkStalled"/> is handled, on another thread than the one
    /// that raised it, it tells a stretch that goes on from one that something has ended since.
    /// </summary>
    public bool Stalled => _accepted && QuietSeconds * 1000 >= RenewLinkAfter;

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
    /// stands still, which ends in opening the link anew or giving up on it within <see cref="ReconnectCeiling"/>.
    /// Counted in time alone, a link that went quiet for nearly that long just after something went missing lost it
    /// after all in a simulation.
    /// </summary>
    private const int HoldForMissingAtMost = ReconnectCeiling + 5000;

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
    /// When the oldest of <see cref="_keptToSendAgain"/> was first sent, in UTC ticks, or 0 when there is none: set
    /// where the queue changes, for <see cref="UntakenSeconds"/> to read without the lock every frame.
    /// </summary>
    private long _oldestUntakenTicks;

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
    /// Raised on the send loop's thread once in a stretch in which nothing arrives over a connection the other side has
    /// accepted, as the stretch reaches <see cref="RenewLinkAfter"/> while the transport still has the session or is
    /// making it anew: the link is being made anew from then on. The stretch ends in things arriving again
    /// (<see cref="ReceiveResumed"/>) or in giving up at <see cref="ReconnectCeiling"/> (<see cref="TimeoutEvent"/>).
    /// Something may arrive while this is handled on another thread, which <see cref="Stalled"/> tells.
    /// </summary>
    public event Action? LinkStalled;

    /// <summary>
    /// How long the link may be held up either way before it is said to be, in seconds: nothing has arrived from the
    /// other side for that long, or the newest of what this side sent that the other side says it had went that long
    /// ago (<see cref="NoteSeen"/>), which grows while the way to the other side is held up although things still come
    /// from it. On a link that is up the second is the first and a round trip more. In the logs of 11-01 to 11-04 the
    /// longest wait between two packets in play was 0.75 s, on a night that lost a quarter to two fifths of them, and a
    /// hit and its answer took 0.79 s at the most there; every wait past 1 s was the link held up, for 1 to 19 s (USER
    /// 11-05, the host never knew that the line to the other player had broken: "只要能及时说出有人卡了").
    /// </summary>
    private const double HeldUpTold = 1.5;

    /// <summary>
    /// How short both waits are again once a hold-up that was told of is over, in seconds.
    /// </summary>
    private const double HeldUpOverBelow = 0.5;

    /// <summary>
    /// How long the send loop may go between two looks at the link before this game counts as having stood still
    /// meanwhile, as a whole game does for a moment now and then, in ticks of DateTime. What it did not send then the
    /// other side cannot have had, and what arrived then still waits to be taken in: counted, it made a host whose game
    /// froze for 2 s say that the other player was stuck (review).
    /// </summary>
    private const long StoodStillAfter = TimeSpan.TicksPerSecond / 4;

    /// <summary>
    /// When the send loop last looked at the link (<see cref="WatchHeldUp"/>), in ticks of DateTime. Only used on the
    /// send loop's thread.
    /// </summary>
    private long _lookedAt;

    /// <summary>
    /// When the send loop went on after this game last stood still (<see cref="StoodStillAfter"/>), in ticks of
    /// DateTime: a hold-up is counted from no earlier than then before it is told of. Only used on the send loop's
    /// thread.
    /// </summary>
    private long _wentOnAt;

    /// <summary>
    /// When the hold-up that was told of began (see <see cref="LinkHeldUp"/>), in ticks of DateTime, or 0 while none
    /// was told of. Only used on the send loop's thread.
    /// </summary>
    private long _heldUpSince;

    /// <summary>
    /// Raised on the send loop's thread once the link over a connection the other side has accepted has been held up
    /// either way for <see cref="HeldUpTold"/> seconds, with how long it has been.
    /// </summary>
    public event Action<double>? LinkHeldUp;

    /// <summary>
    /// Raised on the send loop's thread once a hold-up told of by <see cref="LinkHeldUp"/> is over, with how long it
    /// lasted, in seconds.
    /// </summary>
    public event Action<double>? LinkFreed;

    /// <summary>
    /// How many of the last packets sent <see cref="_sentAt"/> holds, a power of two: 17 s of them at the usual rate.
    /// </summary>
    private const int SentAtKept = 1024;

    /// <summary>
    /// When each of the last <see cref="SentAtKept"/> packets went, by its sequence number: the time in milliseconds of
    /// DateTime shifted up 16 bits, with the sequence number in them, so that one read tells a packet from the one that
    /// took its place. Written where the number is given out, under <see cref="Lock"/>; read where packets arrive.
    /// </summary>
    private readonly long[] _sentAt = new long[SentAtKept];

    /// <summary>
    /// When the newest of what this side sent that the other side says it had went, in milliseconds of DateTime, or 0
    /// until it said so of any. Written only where packets arrive.
    /// </summary>
    private long _seenSentAtMs;

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
    /// How many stretches of time what arrives is counted in for <see cref="LossShare"/>, and how long each is, in
    /// seconds: the share is of the last few seconds, which a stretch that ends lets go of.
    /// </summary>
    private const int LossStretches = 10;

    /// <inheritdoc cref="LossStretches" />
    private const double LossStretchSeconds = 0.5;

    /// <summary>
    /// How many packets the other side sent in each stretch of time, by their numbers, and how many of them arrived
    /// (see <see cref="CountArrival"/>), the stretch that goes on now at <see cref="_lossStretch"/>.
    /// </summary>
    private readonly int[] _lossSent = new int[LossStretches];

    /// <inheritdoc cref="_lossSent" />
    private readonly int[] _lossArrived = new int[LossStretches];

    /// <inheritdoc cref="_lossSent" />
    private int _lossStretch;

    /// <summary>
    /// When the stretch of time that goes on now began.
    /// </summary>
    private DateTime _lossStretchStart;

    /// <summary>
    /// The number of the newest packet that arrived, which the next one that arrives counts from, and whether any did.
    /// </summary>
    private ushort _lossNewest;

    /// <inheritdoc cref="_lossNewest" />
    private bool _lossCounting;

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
        _accepted = false;
        _heldUpSince = 0;

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
        CountArrival(packet.Sequence, now);
        if (gap >= ReceiveGapTold) {
            Logger.Info($"Packets arrive again after {gap:F1}s in which none did");
            try {
                ReceiveResumed?.Invoke(gap);
            } catch (Exception e) {
                Logger.Warn($"Could not take in the end of a gap in what arrives: {e.Message}");
            }
        }

        // The newest of what this side sent that the other side had when it sent this
        NoteSeen(packet.Ack);

        // A transport without sequence numbers of its own times no round trip here: closing the round trip of the last
        // sent sequence, as this used to, timed the gap between sending and the next arrival of anything - the send
        // interval - and fed that to the tracker as though it were the distance to the other player. Such a transport
        // reports its own ping instead. It only notes the newest packet that came, which this side's packets say (Ack).
        if (!_requiresSequencing) {
            if (IsSequenceGreaterThan(packet.Sequence, _remoteSequence)) {
                _remoteSequence = packet.Sequence;
            }

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

            // The newest packet that came from the other side, on every transport, by which the other side tells how
            // long ago the newest of what it sent that got here went (NoteSeen)
            CurrentUpdatePacket.Ack = _remoteSequence;

            // Transports requiring sequencing: Configure ACK data
            if (_requiresSequencing) {
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
            NoteSent(packetToSend.Sequence);
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
        NoteOldestUntaken();
    }

    /// <summary>
    /// Notes when the oldest packet with reliable data that the other side has not taken in was first sent (see
    /// <see cref="_oldestUntakenTicks"/>). Called with <see cref="_reliableLock"/> held.
    /// </summary>
    private void NoteOldestUntaken() {
        Volatile.Write(
            ref _oldestUntakenTicks,
            _keptToSendAgain.Count > 0 ? _keptToSendAgain.Peek().FirstSentTicks : 0
        );
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

        NoteOldestUntaken();
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
        /// When it first went, in UTC ticks.
        /// </summary>
        public long FirstSentTicks { get; } = DateTime.UtcNow.Ticks;

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
    /// What share of the packets that the other side sent over the last few seconds did not arrive, from 0 to 1, by the
    /// numbers they carry (USER 11-06 "丢包率可不可以在左上角显示"). Every transport numbers its packets, and the other
    /// side sends many times a second whether it has anything to say or not, so a number that is skipped is a packet
    /// that got lost. Nothing while nothing arrives: how long that has been is what the ping display shows then.
    /// </summary>
    public float LossShare {
        get {
            lock (Lock) {
                MoveLossStretches(DateTime.UtcNow);

                var sent = 0;
                var arrived = 0;
                for (var i = 0; i < LossStretches; i++) {
                    sent += _lossSent[i];
                    arrived += _lossArrived[i];
                }

                return sent == 0 ? 0f : System.Math.Max(0f, 1f - (float) arrived / sent);
            }
        }
    }

    /// <summary>
    /// Counts a packet that arrived for <see cref="LossShare"/>: the numbers that the other side sent since the newest
    /// that arrived before, and this one. One older than that, which came after newer ones, was counted as sent with
    /// them, and now as arrived.
    /// </summary>
    /// <param name="sequence">The number of the packet.</param>
    /// <param name="now">When it arrived.</param>
    private void CountArrival(ushort sequence, DateTime now) {
        lock (Lock) {
            MoveLossStretches(now);

            if (!_lossCounting) {
                _lossCounting = true;
                _lossNewest = sequence;
                _lossSent[_lossStretch]++;
                _lossArrived[_lossStretch]++;
                return;
            }

            if (IsSequenceGreaterThan(sequence, _lossNewest)) {
                _lossSent[_lossStretch] += (ushort) (sequence - _lossNewest);
                _lossNewest = sequence;
            }

            _lossArrived[_lossStretch]++;
        }
    }

    /// <summary>
    /// Goes on to the stretch of time that goes on at the given time, letting go of what the stretches that ended
    /// more than a few seconds before it counted.
    /// </summary>
    private void MoveLossStretches(DateTime now) {
        var elapsed = (now - _lossStretchStart).TotalSeconds;
        if (elapsed < LossStretchSeconds) {
            return;
        }

        var moves = elapsed >= LossStretches * LossStretchSeconds
            ? LossStretches
            : (int) (elapsed / LossStretchSeconds);
        for (var i = 0; i < moves; i++) {
            _lossStretch = (_lossStretch + 1) % LossStretches;
            _lossSent[_lossStretch] = 0;
            _lossArrived[_lossStretch] = 0;
        }

        _lossStretchStart = moves == LossStretches ? now : _lossStretchStart.AddSeconds(moves * LossStretchSeconds);
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

            // A new connection numbers its packets from 0 again
            Array.Clear(_lossSent, 0, LossStretches);
            Array.Clear(_lossArrived, 0, LossStretches);
            _lossCounting = false;
            CurrentUpdatePacket = new TOutgoing();

            // A new connection numbers its packets from 0 again, which the last one's must not be taken for
            Array.Clear(_sentAt, 0, SentAtKept);
            _seenSentAtMs = 0;

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
            NoteOldestUntaken();
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
                WatchHeldUp();
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
                    var stillUp = quietFor < QuietLinkKept && sessionUp;
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
                    TellStall(lastReceive, quietFor);
                }

                // Send Packet, unless this side waits for the other side's new link: what is set meanwhile goes in the
                // first packet after, and no number is skipped that the other side would count as lost
                if (!HeldAfterRenewal()) {
                    CreateAndSendPacket();
                }

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
    /// Asks the transport for its round trip, which <see cref="AverageRtt"/> gives everyone else from then on. While
    /// the transport knows none, as while Steam makes a link anew, the last one it gave stands: forgotten, the round
    /// trip fell back to one that Steam's relay never measures, 0, and the display showed 0 as things arrived again
    /// after a long hold-up (USER 11-04 "反而有时候卡久了变成0了").
    /// </summary>
    private void AskTransportPing() {
        try {
            if (_transportPing?.Invoke() is { } ping and >= 0) {
                _transportPingMs = ping;
            }
        } catch (Exception) {
            // Nothing is known of the round trip this time round, which the next asks again
        }
    }

    /// <summary>
    /// Notes when a packet went, for <see cref="NoteSeen"/> to find by its sequence number. Under <see cref="Lock"/>.
    /// </summary>
    /// <param name="sequence">The sequence number of the packet.</param>
    private void NoteSent(ushort sequence) {
        Volatile.Write(
            ref _sentAt[sequence & (SentAtKept - 1)],
            (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond << 16) | sequence
        );
    }

    /// <summary>
    /// Notes when the newest of what this side sent that the other side says it had went (<see cref="_seenSentAtMs"/>),
    /// where packets arrive. A packet sent again later says what it said the first time, which is older, and one that
    /// names a packet older than the last <see cref="SentAtKept"/> names none of them, so neither changes anything.
    /// </summary>
    /// <param name="ack">The sequence number of the newest packet of this side's that the other side had.</param>
    private void NoteSeen(ushort ack) {
        var sent = Volatile.Read(ref _sentAt[ack & (SentAtKept - 1)]);
        var sentAtMs = sent >> 16;
        if ((ushort) sent == ack && sentAtMs > _seenSentAtMs) {
            Volatile.Write(ref _seenSentAtMs, sentAtMs);
        }
    }

    /// <summary>
    /// Tells once the link has been held up either way for <see cref="HeldUpTold"/> seconds (<see cref="LinkHeldUp"/>),
    /// counted from no earlier than when this game last went on after standing still, and once that is over
    /// (<see cref="LinkFreed"/>), each in the log too. Only on the send loop's thread.
    /// </summary>
    private void WatchHeldUp() {
        var now = DateTime.UtcNow.Ticks;
        if (now - _lookedAt > StoodStillAfter) {
            _wentOnAt = now;
        }

        _lookedAt = now;
        if (!_accepted) {
            return;
        }

        var lastReceive = _lastReceiveTime.Ticks;
        var seenSent = Volatile.Read(ref _seenSentAtMs) * TimeSpan.TicksPerMillisecond;
        if (_heldUpSince == 0) {
            var quiet = now - System.Math.Max(lastReceive, _wentOnAt);
            var unseen = seenSent == 0 ? 0 : now - System.Math.Max(seenSent, _wentOnAt);
            var held = System.Math.Max(quiet, unseen);
            if (held < HeldUpTold * TimeSpan.TicksPerSecond) {
                return;
            }

            _heldUpSince = now - held;
            Logger.Info(
                $"[Link] The link is held up: nothing has arrived for {new TimeSpan(quiet).TotalSeconds:F1}s, and the " +
                $"newest of what was sent that the other side had went {new TimeSpan(unseen).TotalSeconds:F1}s ago" +
                DescribeTransportSession()
            );
            LinkHeldUp?.Invoke(new TimeSpan(held).TotalSeconds);
            return;
        }

        // Over once both are short again, counted in full
        var stillHeld = System.Math.Max(now - lastReceive, seenSent == 0 ? 0 : now - seenSent);
        if (stillHeld >= HeldUpOverBelow * TimeSpan.TicksPerSecond) {
            return;
        }

        var heldFor = new TimeSpan(now - _heldUpSince).TotalSeconds;
        _heldUpSince = 0;
        Logger.Info($"[Link] The link is no longer held up, after {heldFor:F1}s");
        LinkFreed?.Invoke(heldFor);
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
            _stallTold = false;
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
    /// while still nothing arrives; the other side's next send opens the new link, which this side waits for
    /// (<see cref="RenewalHoldMs"/>). Giving up still comes at <see cref="QuietLinkKept"/>.
    ///
    /// USER 10-15 "断线去做": nothing from the host reached the joining player for 30 s while what they sent reached the
    /// host throughout, and Steam called the link connected the whole time; it ended the game, and a new lobby two
    /// minutes later connected at once. What was still on its way over the old link is lost with it, which the
    /// numbering of the packets with reliable data makes good: what either side has not taken in goes again, in order
    /// (see <see cref="ReliableNumbering"/>).
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
        _heldUntil = DateTime.UtcNow.Ticks + RenewalHoldMs * TimeSpan.TicksPerMillisecond;
        Logger.Info(
            $"Nothing has arrived for {quietFor / 1000:F1}s although the link counted as connected, so it was opened " +
            $"anew (time {_renewals} in this quiet stretch), and nothing is sent until the other side's new link " +
            $"counts as connected, for at most {RenewalHoldMs / 1000}s; this is taken as a disconnect only after " +
            $"{QuietLinkKept / 1000}s"
        );

        SendAgainAfterRenewal();
    }

    /// <summary>
    /// Whether the send loop sends nothing now, as this side opened the link anew a moment ago (see
    /// <see cref="RenewalHoldMs"/>): until the link counts as connected again or the time is over, after which the
    /// next send opens the link from this side. Says in the log which of the two ended it.
    /// </summary>
    private bool HeldAfterRenewal() {
        var until = _heldUntil;
        if (until == 0) {
            return false;
        }

        var connected = false;
        try {
            connected = _transportSessionConnected?.Invoke() == true;
        } catch (Exception) {
            // Taken as not connected yet, which the time ends
        }

        var now = DateTime.UtcNow.Ticks;
        if (!connected && now < until) {
            return true;
        }

        _heldUntil = 0;
        var waited = (double) (now - until) / TimeSpan.TicksPerSecond + RenewalHoldMs / 1000.0;
        Logger.Info(
            connected
                ? $"The link counts as connected again {waited:F1}s after this side opened it anew, so sending goes " +
                  "on over it"
                : $"The link did not count as connected in the {RenewalHoldMs / 1000}s after this side opened it " +
                  "anew, so this side sends again, which opens it from here"
        );
        return false;
    }

    /// <summary>
    /// Raises <see cref="LinkStalled"/> once in a quiet stretch over a connection the other side has accepted, as it
    /// reaches <see cref="RenewLinkAfter"/>: whether this side opened the link anew then or the transport or the other
    /// side is making it anew already, it is being made anew, which the players are told of.
    /// </summary>
    /// <param name="lastReceive">When the last thing arrived, as the send loop looked.</param>
    /// <param name="quietFor">How long nothing has arrived since, in milliseconds.</param>
    private void TellStall(DateTime lastReceive, double quietFor) {
        // Something that arrived since the send loop looked ended the stretch, which this would tell of too late
        if (_stallTold || !_accepted || quietFor < RenewLinkAfter || _lastReceiveTime != lastReceive) {
            return;
        }

        _stallTold = true;
        Logger.Info($"Nothing has arrived for {quietFor / 1000:F1}s, so the players are told the link is made anew");
        try {
            LinkStalled?.Invoke();
        } catch (Exception e) {
            Logger.Warn($"Could not tell that the link is made anew: {e.Message}");
        }
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
