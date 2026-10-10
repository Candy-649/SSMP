using ProtocolCheck.Explore;

namespace ProtocolCheck.Models;

/// <summary>
/// The check that the games of a shared save run every time its players meet: a round with a key, a hello from each
/// game to each other game, the world progress of each game in parts to each other game, and a leave. Transcribed from
/// <c>CoopSave.cs</c> and <c>CoopSave.Check.cs</c>, with the names of their fields and methods.
/// <para>
/// Player A hosts: the server runs in their game, which waits in the menu until every other player is on the server and
/// then loads the save. The others join over the relay and load the save once every other player is on the server,
/// which is what the save menu enforces. Going to the menu disconnects: <c>UiManager.OnReturnToMainMenu</c>
/// disconnects the client and stops the server, after <c>CoopSave.OnReturnToMainMenu</c> already ran, because CoopSave
/// subscribes first (in the ClientManager constructor, before <c>UiManager.Initialize</c>).
/// </para>
/// </summary>
internal sealed class SaveCheck(int players = 2, int disruptions = 2, int inFlight = 3, int partCount = 2)
    : Model<SaveCheck.World> {
    private const ushort HelloStart = 0;
    private const ushort HelloAnswer = 1;

    public override string Name => $"save check ({players} players, {disruptions} disruptions)";

    public override string Scope =>
        "every player on the server is a member; no pairing, no save key mismatch, no benches, world progress in " +
        $"{partCount} parts; time passes for a hello only while at most {inFlight} messages are on their way";

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

    /// <summary>
    /// <c>MemberCheck</c>: the check with one member in a round. <c>HelloRecent</c> stands for <c>LastHelloUtc</c>
    /// being less than <c>HelloRetryDelay</c> ago, and <c>Parts</c> for the parts in <c>Parts</c>, one bit each.
    /// </summary>
    public readonly record struct MemberCheck(
        ushort Id,
        bool Hello,
        bool StateSent,
        bool StateReceived,
        int Parts,
        bool HelloRecent
    );

    public sealed class Game : Rec {
        public string Name = "";
        public bool Connected;
        public ushort? Id;
        public bool InGame;

        /// <summary>
        /// The other players this game knows, ClientManager's <c>_playerData</c>, which are all members.
        /// </summary>
        public Seq<ushort> Players = Seq<ushort>.Empty;

        public int _sessionSlot = -1;
        public bool _held;
        public bool _everChecked;

        /// <summary>
        /// <c>_checkedMembers</c>, in the order of the IDs.
        /// </summary>
        public Seq<ushort> _checkedMembers = Seq<ushort>.Empty;

        /// <summary>
        /// <c>_memberChecks</c>, in the order of the IDs.
        /// </summary>
        public Seq<MemberCheck> _memberChecks = Seq<MemberCheck>.Empty;

        public ulong _highestCheckKey;
        public ulong _checkKey;
        public bool _stateAdded;
        public bool _roundFinished;
    }

    public sealed class World : Rec {
        public Game A = new() { Name = "A" };
        public Game B = new() { Name = "B" };
        public Game C = new() { Name = "C" };
        public bool ServerUp;
        public ushort NextId;
        public Channel ToA = new();
        public Channel ToB = new();
        public Channel ToC = new();
        public int Disruptions;
    }

    public override World Initial() => new() { Disruptions = disruptions };

    /// <summary>
    /// The games of the players of the model: A and B, and C with three players.
    /// </summary>
    private Game[] Games(World w) => players >= 3 ? [w.A, w.B, w.C] : [w.A, w.B];

    /// <summary>
    /// Renumbers the counts in the upper bits of the check keys from 1 up. The games only ever compare keys and make one
    /// that is one count above the largest they saw, so what matters is the order of the counts and which of them are
    /// exactly one apart, and both stay the same when every larger gap becomes 2.
    /// </summary>
    public override void Normalize(World w) {
        var games = Games(w);

        // Sharing a packet only changes the order when a player connecting or disconnecting joins it, because every
        // other message here has the same packet ID. Once that can't happen any more, the server may as well send
        // everything right away, and the search doesn't have to try every moment it could.
        if (w.Disruptions == 0 && games.All(g => g.Connected)) {
            foreach (var g in games) {
                ChannelOf(w, g).Flush();
            }
        }

        var counts = new SortedSet<ulong>();

        void Note(ulong key) {
            if (key != 0) {
                counts.Add(key >> 16);
            }
        }

        foreach (var g in games) {
            Note(g._highestCheckKey);
            Note(g._checkKey);
            foreach (var message in ChannelOf(w, g).Sent.Concat(ChannelOf(w, g).Open)) {
                Note(KeyOf(message));
            }
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

        foreach (var g in games) {
            g._highestCheckKey = Map(g._highestCheckKey);
            g._checkKey = Map(g._checkKey);
            var channel = ChannelOf(w, g);
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

    public override bool Goal(World w) {
        var games = Games(w);
        return games.All(g =>
            g is { InGame: true, Connected: true } &&
            games.Where(other => other != g).All(other => other.Id is { } id && g._checkedMembers.Contains(id))
        );
    }

    public override string Describe(World w) => string.Join("; ", Games(w).Select(Describe));

    private static string Describe(Game g) =>
        !g.Connected ? $"{g.Name} not connected" :
        !g.InGame ? $"{g.Name} in the menu" :
        g._roundFinished ? $"{g.Name} checked with {g._checkedMembers.Count}" :
        $"{g.Name} waiting ({string.Join(", ", g._memberChecks.Select(check =>
            $"{check.Id}: hello {check.Hello}, sent {check.StateSent}, received {check.StateReceived}"))})";

    public override void Moves(World w, Moves<World> moves) {
        var games = Games(w);
        if (!w.ServerUp && !w.A.Connected) {
            moves.Add("A hosts", Host);
        }

        foreach (var g in games.Skip(1)) {
            var name = g.Name;
            if (w.ServerUp && !g.Connected) {
                moves.Add($"{name} joins", s => Join(s, GameOf(s, name)));
            }
        }

        var pending = games.Sum(g => ChannelOf(w, g).Pending);
        foreach (var g in games) {
            var name = g.Name;
            var channel = ChannelOf(w, g);
            if (channel.Sent.Count > 0) {
                moves.Add($"{name} gets {channel.Sent[0]}", s => Handle(s, GameOf(s, name), ChannelOf(s, name).Take()));
            }

            if (channel.Open.Count > 0) {
                moves.Add($"server sends to {name}", s => ChannelOf(s, name).Flush());
            }

            // The save menu only opens the save with every other member on the server
            if (g.Connected && !g.InGame && g.Players.Count == players - 1) {
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
            if (g._memberChecks.Any(check => check.HelloRecent) && pending <= inFlight) {
                moves.Add($"{name}: 2s pass", s => {
                    var game = GameOf(s, name);
                    game._memberChecks = Seq<MemberCheck>.Of(
                        game._memberChecks.Select(check => check with { HelloRecent = false }).ToArray()
                    );
                });
            }
        }

        if (w.Disruptions > 0) {
            foreach (var g in games.Skip(1)) {
                var name = g.Name;
                if (g.InGame) {
                    moves.Disrupt($"{name} quits to the menu", s => { Quits(s, GameOf(s, name)); s.Disruptions--; });
                }

                if (g.Connected) {
                    moves.Disrupt($"{name}'s connection drops", s => { Drops(s, GameOf(s, name)); s.Disruptions--; });
                }
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
        var members = g.Players;
        if (members.Count == 0) {
            return false;
        }

        if (g._sessionSlot != 1) {
            return true;
        }

        return members.Any(member => !g._checkedMembers.Contains(member)) && !g._roundFinished && g._checkKey == 0;
    }

    private static Game GameOf(World w, string name) => name switch { "A" => w.A, "B" => w.B, _ => w.C };

    private static Channel ChannelOf(World w, Game g) => ChannelOf(w, g.Name);

    private static Channel ChannelOf(World w, string name) => name switch { "A" => w.ToA, "B" => w.ToB, _ => w.ToC };

    /// <summary>
    /// CoopSave.Send: goes out only while connected, and the server drops it for a player who isn't there.
    /// </summary>
    private void Send(World w, Game g, ushort target, Msg message) {
        if (!g.Connected) {
            return;
        }

        var receiver = Games(w).FirstOrDefault(other => other.Connected && other.Id == target);
        if (receiver != null) {
            ChannelOf(w, receiver).Put(message);
        }
    }

    private static int FindCheck(Game g, ushort id) {
        for (var i = 0; i < g._memberChecks.Count; i++) {
            if (g._memberChecks[i].Id == id) {
                return i;
            }
        }

        return -1;
    }

    private static MemberCheck? GetCheck(Game g, ushort id) {
        var index = FindCheck(g, id);
        return index < 0 ? null : g._memberChecks[index];
    }

    /// <summary>
    /// Puts the check with a member in, in the place of an older one or in the order of the IDs.
    /// </summary>
    private static void SetCheck(Game g, MemberCheck check) {
        var index = FindCheck(g, check.Id);
        if (index >= 0) {
            g._memberChecks = g._memberChecks.RemoveAt(index).Insert(index, check);
            return;
        }

        var at = 0;
        while (at < g._memberChecks.Count && g._memberChecks[at].Id < check.Id) {
            at++;
        }

        g._memberChecks = g._memberChecks.Insert(at, check);
    }

    private static void RemoveCheck(Game g, ushort id) {
        var index = FindCheck(g, id);
        if (index >= 0) {
            g._memberChecks = g._memberChecks.RemoveAt(index);
        }
    }

    private static Seq<ushort> Sorted(IEnumerable<ushort> ids) => Seq<ushort>.Of(ids.Distinct().OrderBy(id => id).ToArray());

    #region CoopSave.Check.cs

    private static void ResetCheck(Game g) {
        g._checkedMembers = Seq<ushort>.Empty;
        g._memberChecks = Seq<MemberCheck>.Empty;
        g._checkKey = 0;
        g._stateAdded = false;
        g._roundFinished = false;
    }

    private void UpdateCheck(World w, Game g, Seq<ushort> members, ulong low) {
        if (g._roundFinished) {
            return;
        }

        if (g._checkKey == 0) {
            g._checkKey = NewCheckKey(g, low);
        }

        foreach (var member in members) {
            if (GetCheck(g, member) is not { } check) {
                SetCheck(g, new MemberCheck(member, false, false, false, 0, false));
                SendHello(w, g, member, HelloStart);
            } else if ((!check.Hello || !check.StateReceived) && !check.HelloRecent) {
                SendHello(w, g, member, HelloStart);
            }

            if (GetCheck(g, member) is { Hello: true, StateSent: false } ready) {
                SetCheck(g, ready with { StateSent = true });
                SendWorldState(w, g, member);
            }
        }

        if (members.Count == 0 || !members.All(member => GetCheck(g, member) is { StateSent: true, StateReceived: true })) {
            return;
        }

        if (!g._stateAdded) {
            g._stateAdded = true;
        }

        FinishCheck(g, members);
    }

    private static ulong NewCheckKey(Game g, ulong low) {
        var key = (((g._highestCheckKey >> 16) + 1) << 16) | low;
        g._highestCheckKey = key;
        return key;
    }

    private void SendHello(World w, Game g, ushort member, ushort kind) {
        if (GetCheck(g, member) is { } check) {
            SetCheck(g, check with { HelloRecent = true });
        }

        Send(w, g, member, new Hello(g.Id!.Value, g._checkKey, kind));
    }

    private void OnHello(World w, Game g, ushort player, Hello update) {
        if (!g.InGame) {
            return;
        }

        g._highestCheckKey = Math.Max(g._highestCheckKey, update.Key);
        var check = GetCheck(g, player);

        if (update.PartCount == HelloAnswer) {
            if (update.Key == g._checkKey && check != null) {
                SetCheck(g, check.Value with { Hello = true });
                ResendWorldState(w, g, player);
            }

            return;
        }

        if (update.Key > g._checkKey) {
            ResetCheck(g);
            g._checkKey = update.Key;
            SetCheck(g, new MemberCheck(player, true, false, false, 0, false));
            SendHello(w, g, player, HelloAnswer);
            return;
        }

        if (g._roundFinished && !g._checkedMembers.Contains(player)) {
            ResetCheck(g);
            return;
        }

        if (update.Key == g._checkKey) {
            SetCheck(g, (check ?? new MemberCheck(player, false, false, false, 0, false)) with { Hello = true });
            SendHello(w, g, player, HelloAnswer);
            ResendWorldState(w, g, player);
            return;
        }

        if (!g._checkedMembers.Contains(player)) {
            if (check == null) {
                SetCheck(g, new MemberCheck(player, false, false, false, 0, false));
            }

            SendHello(w, g, player, HelloStart);
        }
    }

    private static void OnWorldState(Game g, ushort player, WorldState update) {
        var check = GetCheck(g, player);
        if (check is { StateReceived: true }) {
            return;
        }

        if (!g.InGame || g._checkKey == 0 || update.Key != g._checkKey || check == null) {
            return;
        }

        var parts = check.Value.Parts | (1 << update.Part);
        SetCheck(g, check.Value with { Parts = parts, StateReceived = int.PopCount(parts) >= update.PartCount });
    }

    private static void OnLeft(Game g, ushort player, Left update) {
        if (GetCheck(g, player) != null && g._checkKey > update.Key) {
            return;
        }

        PartnerLeft(g, player);
    }

    private void FinishCheck(Game g, Seq<ushort> members) {
        g._roundFinished = true;
        g._checkedMembers = Sorted(members);
        if (IsGroupComplete(g, members)) {
            g._everChecked = true;
        }
    }

    private void SendWorldState(World w, Game g, ushort member) {
        for (ushort part = 0; part < partCount; part++) {
            Send(w, g, member, new WorldState(g.Id!.Value, g._checkKey, part, (ushort) partCount));
        }
    }

    private void ResendWorldState(World w, Game g, ushort member) {
        if (GetCheck(g, member) is not { StateSent: true } || g._checkKey == 0) {
            return;
        }

        SendWorldState(w, g, member);
    }

    #endregion

    #region CoopSave.cs

    /// <summary>
    /// IsGroupComplete: every member on the server and checked with.
    /// </summary>
    private bool IsGroupComplete(Game g, Seq<ushort> members) =>
        members.Count == players - 1 && members.All(member => g._checkedMembers.Contains(member));

    private void UpdateSession(World w, Game g, ulong low) {
        if (g._sessionSlot != 1) {
            ResetSession(w, g, true);
            g._sessionSlot = 1;
        }

        var members = g.Players;
        if (members.Any(member => !g._checkedMembers.Contains(member))) {
            UpdateCheck(w, g, members, low);
        }

        if (IsGroupComplete(g, members)) {
            g._held = false;
            return;
        }

        if (g._held || !g._everChecked) {
            g._held = true;
        }
    }

    private void ResetSession(World w, Game g, bool notifyPartner) {
        if (notifyPartner) {
            foreach (var id in Sorted(g._memberChecks.Select(check => check.Id).Concat(g._checkedMembers))) {
                if (g.Players.Contains(id)) {
                    Send(w, g, id, new Left(g.Id!.Value, g._highestCheckKey));
                }
            }
        }

        g._held = false;
        g._everChecked = false;
        ResetCheck(g);
    }

    private void OnReturnToMainMenu(World w, Game g) {
        ResetSession(w, g, true);
        g._sessionSlot = -1;
    }

    /// <summary>
    /// OnPlayerConnect, after ClientManager added the player.
    /// </summary>
    private static void OnPlayerConnect(Game g, ushort player) {
        g.Players = Sorted(g.Players.Append(player));
        if (!g.InGame) {
            return;
        }

        RemoveCheck(g, player);
        if (g._checkedMembers.Count == 0) {
            ResetCheck(g);
            g._highestCheckKey = 0;
        }
    }

    /// <summary>
    /// OnPlayerDisconnect, after ClientManager forgot the player.
    /// </summary>
    private static void OnPlayerDisconnect(Game g, ushort id) {
        g.Players = g.Players.Remove(id);
        if (GetCheck(g, id) != null || g._checkedMembers.Contains(id)) {
            PartnerLeft(g, id);
            if (g._checkedMembers.Count == 0) {
                g._highestCheckKey = 0;
            }
        }
    }

    private static void PartnerLeft(Game g, ushort id) {
        if (GetCheck(g, id) == null && !g._checkedMembers.Contains(id)) {
            return;
        }

        g._checkedMembers = g._checkedMembers.Remove(id);
        RemoveCheck(g, id);
        if (g._checkedMembers.Count == 0) {
            ResetCheck(g);
        }
    }

    private static void OnLocalConnect(Game g) {
        g._highestCheckKey = 0;
    }

    private static void OnLocalDisconnect(Game g) {
        g._held = false;
        ResetCheck(g);
        g._highestCheckKey = 0;
    }

    /// <summary>
    /// ClientManager's handlers for the messages of this model. OnCoopSaveUpdate drops an update from a player that the
    /// game doesn't know.
    /// </summary>
    private void Handle(World w, Game g, Msg message) {
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

    private void Join(World w, Game g) {
        var others = Games(w).Where(other => other != g && other.Connected).ToList();
        g.Connected = true;
        g.Id = w.NextId++;
        g.Players = Sorted(others.Select(other => other.Id!.Value));
        OnLocalConnect(g);
        foreach (var other in others) {
            ChannelOf(w, other).Put(new PlayerConnect(g.Id.Value));
        }
    }

    /// <summary>
    /// A game disconnects, from the menu or because the connection is lost, and goes to the menu.
    /// </summary>
    private void Leave(World w, Game g) {
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

    /// <summary>
    /// The server tells every other game that a player left.
    /// </summary>
    private void TellLeft(World w, Game g, ushort id) {
        foreach (var other in Games(w)) {
            if (other != g && other.Connected) {
                ChannelOf(w, other).Put(new PlayerDisconnect(id));
            }
        }
    }

    private void Quits(World w, Game g) {
        var id = g.Id!.Value;
        OnReturnToMainMenu(w, g);
        g.InGame = false;
        Leave(w, g);
        TellLeft(w, g, id);
    }

    private void Drops(World w, Game g) {
        var id = g.Id!.Value;
        Leave(w, g);
        TellLeft(w, g, id);
    }

    private void AQuits(World w) {
        var a = w.A;
        OnReturnToMainMenu(w, a);
        a.InGame = false;
        Leave(w, a);
        foreach (var g in Games(w).Skip(1)) {
            if (g.Connected) {
                Leave(w, g);
            }
        }

        w.ServerUp = false;
    }

    #endregion
}
