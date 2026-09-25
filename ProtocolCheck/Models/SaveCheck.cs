using ProtocolCheck.Explore;

namespace ProtocolCheck.Models;

/// <summary>
/// The check that both games of a two-player save run every time the two players meet: a hello with a key, the world
/// progress of each game in parts, and a leave. Transcribed from <c>CoopSave.cs</c> and <c>CoopSave.Check.cs</c>, with
/// the names of their fields and methods.
/// <para>
/// Player A hosts: the server runs in their game, which waits in the menu until B is on the server and then loads the
/// save. B joins over the relay and loads the save once A is on the server, which is what the save menu enforces.
/// Going to the menu disconnects: <c>UiManager.OnReturnToMainMenu</c> disconnects the client and stops the server, after
/// <c>CoopSave.OnReturnToMainMenu</c> already ran, because CoopSave subscribes first (in the ClientManager
/// constructor, before <c>UiManager.Initialize</c>).
/// </para>
/// </summary>
internal sealed class SaveCheck(int disruptions = 2, int inFlight = 3) : Model<SaveCheck.World> {
    private const int PartCount = 2;
    private const ushort HelloStart = 0;
    private const ushort HelloAnswer = 1;

    public override string Name => $"save check ({disruptions} disruptions)";

    public override string Scope =>
        "no pairing, no save key mismatch, no benches, world progress in 2 parts; time passes for a hello only " +
        $"while at most {inFlight} messages are on their way";

    public sealed record Hello(ushort PlayerId, ulong Key, ushort PartCount) : Msg {
        public override int Packet => Packets.CoopSaveUpdate;
    }

    public sealed record WorldState(ushort PlayerId, ulong Key, ushort Part, ushort PartCount) : Msg {
        public override int Packet => Packets.CoopSaveUpdate;
    }

    public sealed record Left(ushort PlayerId, ulong Key) : Msg {
        public override int Packet => Packets.CoopSaveUpdate;
    }

    public sealed record PlayerConnect(ushort Id) : Msg {
        public override int Packet => Packets.PlayerConnect;
    }

    public sealed record PlayerDisconnect(ushort Id) : Msg {
        public override int Packet => Packets.PlayerDisconnect;
    }

    public sealed class Game : Rec {
        public string Name = "";
        public bool Connected;
        public ushort? Id;
        public bool InGame;

        /// <summary>
        /// The other players this game knows, ClientManager's <c>_playerData</c>.
        /// </summary>
        public Seq<ushort> Players = Seq<ushort>.Empty;

        public int _sessionSlot = -1;
        public bool _held;
        public bool _everChecked;
        public ushort? _checkedWith;
        public ulong _highestCheckKey;
        public ushort? _checkPartnerId;
        public ulong _checkKey;
        public bool _partnerHello;
        public bool _stateSent;
        public bool _stateReceived;
        public bool _stateAdded;

        /// <summary>
        /// The parts in <c>_stateParts</c>, one bit each.
        /// </summary>
        public int _stateParts;

        /// <summary>
        /// Whether the last hello went out less than <c>HelloRetryDelay</c> ago.
        /// </summary>
        public bool HelloRecent;
    }

    public sealed class World : Rec {
        public Game A = new() { Name = "A" };
        public Game B = new() { Name = "B" };
        public bool ServerUp;
        public ushort NextId;
        public Channel ToA = new();
        public Channel ToB = new();
        public int Disruptions;
    }

    public override World Initial() => new() { Disruptions = disruptions };

    /// <summary>
    /// Renumbers the counts in the upper bits of the check keys from 1 up. The games only ever compare keys and make one
    /// that is one count above the largest they saw, so what matters is the order of the counts and which of them are
    /// exactly one apart, and both stay the same when every larger gap becomes 2.
    /// </summary>
    public override void Normalize(World w) {
        // Sharing a packet only changes the order when a player connecting or disconnecting joins it, because every
        // other message here has the same packet ID. Once that can't happen any more, the server may as well send
        // everything right away, and the search doesn't have to try every moment it could.
        if (w.Disruptions == 0 && w.B.Connected) {
            w.ToA.Flush();
            w.ToB.Flush();
        }

        var counts = new SortedSet<ulong>();

        void Note(ulong key) {
            if (key != 0) {
                counts.Add(key >> 16);
            }
        }

        foreach (var g in new[] { w.A, w.B }) {
            Note(g._highestCheckKey);
            Note(g._checkKey);
        }

        foreach (var message in w.ToA.Sent.Concat(w.ToA.Open).Concat(w.ToB.Sent).Concat(w.ToB.Open)) {
            Note(KeyOf(message));
        }

        var renumbered = new Dictionary<ulong, ulong>();
        ulong previous = 0, next = 0;
        foreach (var count in counts) {
            next += Math.Min(count - previous, 2);
            renumbered[count] = next;
            previous = count;
        }

        if (renumbered.All(pair => pair.Key == pair.Value)) {
            return;
        }

        ulong Map(ulong key) => key == 0 ? 0 : (renumbered[key >> 16] << 16) | (key & 0xFFFF);

        Msg MapMessage(Msg message) => message switch {
            Hello hello => hello with { Key = Map(hello.Key) },
            WorldState state => state with { Key = Map(state.Key) },
            Left left => left with { Key = Map(left.Key) },
            _ => message
        };

        foreach (var g in new[] { w.A, w.B }) {
            g._highestCheckKey = Map(g._highestCheckKey);
            g._checkKey = Map(g._checkKey);
        }

        foreach (var channel in new[] { w.ToA, w.ToB }) {
            channel.Sent = Seq<Msg>.Of(channel.Sent.Select(MapMessage).ToArray());
            channel.Open = Seq<Msg>.Of(channel.Open.Select(MapMessage).ToArray());
        }
    }

    private static ulong KeyOf(Msg message) => message switch {
        Hello hello => hello.Key,
        WorldState state => state.Key,
        Left left => left.Key,
        _ => 0
    };

    public override bool Goal(World w) =>
        w.A is { InGame: true, Connected: true } && w.B is { InGame: true, Connected: true } &&
        w.A._checkedWith == w.B.Id && w.B._checkedWith == w.A.Id;

    public override string Describe(World w) => $"{Describe(w.A)}; {Describe(w.B)}";

    private static string Describe(Game g) =>
        !g.Connected ? $"{g.Name} not connected" :
        !g.InGame ? $"{g.Name} in the menu" :
        g._checkedWith != null ? $"{g.Name} checked" :
        $"{g.Name} waiting (hello from the partner: {g._partnerHello}, progress sent: {g._stateSent}, " +
        $"received: {g._stateReceived})";

    public override void Moves(World w, Moves<World> moves) {
        if (!w.ServerUp && !w.A.Connected) {
            moves.Add("A hosts", Host);
        }

        if (w.ServerUp && !w.B.Connected) {
            moves.Add("B joins", Join);
        }

        foreach (var g in new[] { w.A, w.B }) {
            var name = g.Name;
            var channel = ChannelOf(w, g);
            if (channel.Sent.Count > 0) {
                moves.Add($"{name} gets {channel.Sent[0]}", s => Handle(s, GameOf(s, name), ChannelOf(s, name).Take()));
            }

            if (channel.Open.Count > 0) {
                moves.Add($"server sends to {name}", s => ChannelOf(s, name).Flush());
            }

            if (g.Connected && !g.InGame && FindPartner(g) != null) {
                moves.Add($"{name} loads the save", s => GameOf(s, name).InGame = true);
            }

            if (!g.InGame) {
                continue;
            }

            if (MakesKey(g)) {
                var next = (g._highestCheckKey >> 16) + 1;
                moves.Add($"{name} frame, new check key {next}.1", s => UpdateSession(s, GameOf(s, name), 1));
                moves.Add($"{name} frame, new check key {next}.2", s => UpdateSession(s, GameOf(s, name), 2));
            } else {
                moves.Add($"{name} frame", s => UpdateSession(s, GameOf(s, name), 1));
            }

            // Time passing is what sends a hello again, and a hello can bring the world progress back with it, so the
            // search lets time pass only while few enough messages are on their way
            if (g.HelloRecent && w.ToA.Pending + w.ToB.Pending <= inFlight) {
                moves.Add($"{name}: 2s pass", s => GameOf(s, name).HelloRecent = false);
            }
        }

        if (w.Disruptions > 0) {
            if (w.B.InGame) {
                moves.Disrupt("B quits to the menu", s => { BQuits(s); s.Disruptions--; });
            }

            if (w.B.Connected) {
                moves.Disrupt("B's connection drops", s => { BDrops(s); s.Disruptions--; });
            }

            if (w.A.InGame) {
                moves.Disrupt("A quits to the menu", s => { AQuits(s); s.Disruptions--; });
            }
        }
    }

    /// <summary>
    /// Whether the next frame of a game makes a new check key, which has random lower bits.
    /// </summary>
    private static bool MakesKey(Game g) {
        var partner = FindPartner(g);
        if (partner == null) {
            return false;
        }

        if (g._sessionSlot != 1) {
            return true;
        }

        return g._checkedWith != partner && (g._checkPartnerId != partner || g._checkKey == 0);
    }

    private static Game GameOf(World w, string name) => name == "A" ? w.A : w.B;

    private static Channel ChannelOf(World w, Game g) => g.Name == "A" ? w.ToA : w.ToB;

    private static Channel ChannelOf(World w, string name) => name == "A" ? w.ToA : w.ToB;

    /// <summary>
    /// CoopSave.Send: goes out only while connected, and the server drops it for a player who isn't there.
    /// </summary>
    private static void Send(World w, Game g, ushort target, Msg message) {
        if (!g.Connected) {
            return;
        }

        var receiver = w.A.Connected && w.A.Id == target ? w.A : w.B.Connected && w.B.Id == target ? w.B : null;
        if (receiver != null) {
            ChannelOf(w, receiver).Put(message);
        }
    }

    /// <summary>
    /// FindPartner: the other player, if this game knows them.
    /// </summary>
    private static ushort? FindPartner(Game g) => g.Players.Count > 0 ? g.Players[0] : null;

    #region CoopSave.Check.cs

    private static void ResetCheck(Game g) {
        g.HelloRecent = false;
        g._checkPartnerId = null;
        g._checkKey = 0;
        g._partnerHello = false;
        g._stateSent = false;
        g._stateReceived = false;
        g._stateAdded = false;
        g._stateParts = 0;
    }

    private static void UpdateCheck(World w, Game g, ushort partner, ulong low) {
        if (g._checkPartnerId != partner) {
            ResetCheck(g);
            g._checkPartnerId = partner;
        }

        if (g._checkKey == 0) {
            g._checkKey = NewCheckKey(g, low);
            SendHello(w, g, partner, HelloStart);
        } else if ((!g._partnerHello || !g._stateReceived) && !g.HelloRecent) {
            SendHello(w, g, partner, HelloStart);
        }

        if (g._partnerHello && !g._stateSent) {
            g._stateSent = true;
            SendWorldState(w, g, partner);
        }

        if (g._stateSent && g._stateReceived) {
            if (!g._stateAdded) {
                g._stateAdded = true;
            }

            FinishCheck(g, partner);
        }
    }

    private static ulong NewCheckKey(Game g, ulong low) {
        var key = (((g._highestCheckKey >> 16) + 1) << 16) | low;
        g._highestCheckKey = key;
        return key;
    }

    private static void SendHello(World w, Game g, ushort partner, ushort kind) {
        g.HelloRecent = true;
        Send(w, g, partner, new Hello(g.Id!.Value, g._checkKey, kind));
    }

    private static void OnHello(World w, Game g, ushort player, Hello update) {
        if (!g.InGame) {
            return;
        }

        g._highestCheckKey = Math.Max(g._highestCheckKey, update.Key);

        if (update.PartCount == HelloAnswer) {
            if (update.Key == g._checkKey && g._checkPartnerId == player) {
                g._partnerHello = true;
                ResendWorldState(w, g, player);
            }

            return;
        }

        if (update.Key == g._checkKey && g._checkPartnerId == player) {
            g._partnerHello = true;
            SendHello(w, g, player, HelloAnswer);
            ResendWorldState(w, g, player);
            return;
        }

        if (update.Key < g._checkKey && g._checkPartnerId == player) {
            if (g._checkedWith != player) {
                SendHello(w, g, player, HelloStart);
            }

            return;
        }

        if (g._checkedWith == player) {
            g._checkedWith = null;
        }

        ResetCheck(g);
        g._checkPartnerId = player;
        g._checkKey = update.Key;
        g._partnerHello = true;
        SendHello(w, g, player, HelloAnswer);
    }

    private static void OnWorldState(Game g, ushort player, WorldState update) {
        if (g._stateReceived) {
            return;
        }

        if (!g.InGame || g._checkKey == 0 || update.Key != g._checkKey || g._checkPartnerId != player) {
            return;
        }

        g._stateParts |= 1 << update.Part;
        if (int.PopCount(g._stateParts) < update.PartCount) {
            return;
        }

        g._stateReceived = true;
    }

    private static void OnLeft(Game g, ushort player, Left update) {
        if (g._checkPartnerId == player && g._checkKey > update.Key) {
            return;
        }

        PartnerLeft(g, player);
    }

    private static void FinishCheck(Game g, ushort partner) {
        g._checkedWith = partner;
        g._everChecked = true;
    }

    private static void SendWorldState(World w, Game g, ushort partner) {
        for (ushort part = 0; part < PartCount; part++) {
            Send(w, g, partner, new WorldState(g.Id!.Value, g._checkKey, part, PartCount));
        }
    }

    private static void ResendWorldState(World w, Game g, ushort partner) {
        if (!g._stateSent || g._checkKey == 0) {
            return;
        }

        SendWorldState(w, g, partner);
    }

    #endregion

    #region CoopSave.cs

    private static void UpdateSession(World w, Game g, ulong low) {
        if (g._sessionSlot != 1) {
            ResetSession(w, g, true);
            g._sessionSlot = 1;
        }

        var partner = FindPartner(g);
        if (partner != null && g._checkedWith != partner) {
            UpdateCheck(w, g, partner.Value, low);
        }

        if (partner != null && g._checkedWith == partner) {
            g._held = false;
            return;
        }

        if (g._held || !g._everChecked) {
            g._held = true;
        }
    }

    private static void ResetSession(World w, Game g, bool notifyPartner) {
        var partnerId = g._checkedWith ?? g._checkPartnerId;
        if (notifyPartner && partnerId is { } id && g.Players.Contains(id)) {
            Send(w, g, id, new Left(g.Id!.Value, g._highestCheckKey));
        }

        g._held = false;
        g._everChecked = false;
        g._checkedWith = null;
        ResetCheck(g);
    }

    private static void OnReturnToMainMenu(World w, Game g) {
        ResetSession(w, g, true);
        g._sessionSlot = -1;
    }

    /// <summary>
    /// OnPlayerConnect, after ClientManager added the player.
    /// </summary>
    private static void OnPlayerConnect(Game g, ushort player) {
        g.Players = g.Players.Add(player);
        if (!g.InGame) {
            return;
        }

        g._checkedWith = null;
        ResetCheck(g);
        g._highestCheckKey = 0;
    }

    /// <summary>
    /// OnPlayerDisconnect, after which ClientManager forgets the player.
    /// </summary>
    private static void OnPlayerDisconnect(Game g, ushort id) {
        if (g._checkPartnerId == id || g._checkedWith == id) {
            PartnerLeft(g, id);
            g._highestCheckKey = 0;
        }

        g.Players = g.Players.Remove(id);
    }

    private static void PartnerLeft(Game g, ushort id) {
        if (g._checkPartnerId != id && g._checkedWith != id) {
            return;
        }

        g._checkedWith = null;
        ResetCheck(g);
    }

    private static void OnLocalConnect(Game g) {
        g._highestCheckKey = 0;
    }

    private static void OnLocalDisconnect(Game g) {
        g._held = false;
        g._checkedWith = null;
        ResetCheck(g);
        g._highestCheckKey = 0;
    }

    /// <summary>
    /// ClientManager's handlers for the messages of this model. OnCoopSaveUpdate drops an update from a player that the
    /// game doesn't know.
    /// </summary>
    private static void Handle(World w, Game g, Msg message) {
        switch (message) {
            case PlayerConnect connect:
                OnPlayerConnect(g, connect.Id);
                break;
            case PlayerDisconnect disconnect:
                OnPlayerDisconnect(g, disconnect.Id);
                break;
            case Hello hello when g.Players.Contains(hello.PlayerId):
                OnHello(w, g, hello.PlayerId, hello);
                break;
            case WorldState state when g.Players.Contains(state.PlayerId):
                OnWorldState(g, state.PlayerId, state);
                break;
            case Left left when g.Players.Contains(left.PlayerId):
                OnLeft(g, left.PlayerId, left);
                break;
        }
    }

    #endregion

    #region Connecting and leaving

    private static void Host(World w) {
        w.ServerUp = true;
        w.NextId = 1;
        w.A.Connected = true;
        w.A.Id = 0;
        w.A.Players = Seq<ushort>.Empty;
        OnLocalConnect(w.A);
    }

    private static void Join(World w) {
        var b = w.B;
        b.Connected = true;
        b.Id = w.NextId++;
        b.Players = Seq<ushort>.Of(w.A.Id!.Value);
        OnLocalConnect(b);
        w.ToA.Put(new PlayerConnect(b.Id.Value));
    }

    /// <summary>
    /// A game disconnects, from the menu or because the connection is lost, and goes to the menu.
    /// </summary>
    private static void Leave(World w, Game g) {
        ChannelOf(w, g).Clear();
        OnLocalDisconnect(g);
        g.Connected = false;
        g.Players = Seq<ushort>.Empty;
        if (g.InGame) {
            g.InGame = false;
            OnReturnToMainMenu(w, g);
        }

        g.Id = null;
    }

    private static void BQuits(World w) {
        var b = w.B;
        var id = b.Id!.Value;
        OnReturnToMainMenu(w, b);
        b.InGame = false;
        Leave(w, b);
        w.ToA.Put(new PlayerDisconnect(id));
    }

    private static void BDrops(World w) {
        var id = w.B.Id!.Value;
        Leave(w, w.B);
        w.ToA.Put(new PlayerDisconnect(id));
    }

    private static void AQuits(World w) {
        var a = w.A;
        OnReturnToMainMenu(w, a);
        a.InGame = false;
        Leave(w, a);
        if (w.B.Connected) {
            Leave(w, w.B);
        }

        w.ServerUp = false;
    }

    #endregion
}
