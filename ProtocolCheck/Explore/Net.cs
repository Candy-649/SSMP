using System.Text.RegularExpressions;

namespace ProtocolCheck.Explore;

/// <summary>
/// The update packet IDs, read from SSMP's own <c>ClientUpdatePacketId.cs</c> and <c>ServerUpdatePacketId.cs</c> so
/// that the order a game or the server handles a packet's data in can't drift from the code.
/// </summary>
public static partial class Packets {
    private static readonly Dictionary<string, int> ClientIds = Read("ClientUpdatePacketId.cs");
    private static readonly Dictionary<string, int> ServerIds = Read("ServerUpdatePacketId.cs");

    /// <summary>
    /// The ID of data that the server sends to a game.
    /// </summary>
    public static int Client(string name) => ClientIds.TryGetValue(name, out var id)
        ? id
        : throw new InvalidOperationException($"ClientUpdatePacketId has no '{name}'");

    /// <summary>
    /// The ID of data that a game sends to the server.
    /// </summary>
    public static int Server(string name) => ServerIds.TryGetValue(name, out var id)
        ? id
        : throw new InvalidOperationException($"ServerUpdatePacketId has no '{name}'");

    public static int PlayerConnect => Client("PlayerConnect");
    public static int PlayerDisconnect => Client("PlayerDisconnect");
    public static int BossRoomUpdate => Client("BossRoomUpdate");
    public static int CoopSaveUpdate => Client("CoopSaveUpdate");
    public static int CoopHitUpdate => Client("CoopHitUpdate");

    private static Dictionary<string, int> Read(string file) {
        var path = Repo.File(Path.Combine("SSMP", "Networking", "Packet", "Update", file));
        return EnumMember().Matches(File.ReadAllText(path))
            .ToDictionary(match => match.Groups[1].Value, match => int.Parse(match.Groups[2].Value));
    }

    [GeneratedRegex(@"^\s*([A-Z][A-Za-z]+)\s*=\s*(\d+)\s*,?\s*$", RegexOptions.Multiline)]
    private static partial Regex EnumMember();
}

/// <summary>
/// Where the SSMP repository is, found by walking up from the program.
/// </summary>
public static class Repo {
    private static readonly string Root = Find();

    public static string File(string relative) => Path.Combine(Root, relative);

    private static string Find() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent) {
            if (System.IO.File.Exists(Path.Combine(directory.FullName, "SSMP.sln"))) {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find SSMP.sln above the program");
    }
}

/// <summary>
/// What the server has for one game that the game hasn't handled yet.
/// <para>
/// Both players reach each other through the server, which runs in the host's game: the host's own game over a
/// loopback, the other game over Steam's relay, whose reliable messages arrive in order. What changes the order is how
/// SSMP packs messages. Everything the server has for one game between two sends goes out in one update packet, and
/// the game handles a packet's data in the order of the packet IDs rather than the order it was added in
/// (<c>BasePacket.ReadPacketData</c> reads the IDs in enum order and <c>PacketManager.UnpackPacketDataDict</c> hands
/// them over in that order; data of one ID keeps its order). So two messages that share a packet can be handled the
/// other way round, and messages in different packets can't.
/// </para>
/// <para>
/// The packet still being filled is <see cref="Open"/>, and everything already on its way is <see cref="Sent"/>, in
/// the order the game will handle it. The server sending the open packet is an event of its own, so the search tries
/// every place it can fall between the others. A message from one game to the other is put straight into the other's
/// channel: the server hands it on in the order it came, and whatever reordering its trip to the server adds is the
/// same kind that sharing a packet on the way out adds.
/// </para>
/// </summary>
public sealed class Channel : Rec {
    public Seq<Msg> Sent = Seq<Msg>.Empty;
    public Seq<Msg> Open = Seq<Msg>.Empty;

    /// <summary>
    /// Adds a message to the packet being filled, after everything in it with the same or a lower packet ID. A message
    /// with a <see cref="Msg.Slot"/> takes the place of one of the same kind and slot that is already in it, the way
    /// <c>SetSendingPacketData</c> and <c>FindOrCreatePacketData</c> keep one of a kind per packet or per player.
    /// </summary>
    public void Put(Msg message) {
        if (message.Slot is { } slot) {
            for (var k = 0; k < Open.Count; k++) {
                if (Open[k].GetType() == message.GetType() && Equals(Open[k].Slot, slot)) {
                    Open = Open.RemoveAt(k).Insert(k, message);
                    return;
                }
            }
        }

        var i = Open.Count;
        while (i > 0 && Open[i - 1].Packet > message.Packet) {
            i--;
        }

        Open = Open.Insert(i, message);
    }

    /// <summary>
    /// The server sends the packet it was filling.
    /// </summary>
    public void Flush() {
        Sent = Sent.AddRange(Open);
        Open = Seq<Msg>.Empty;
    }

    /// <summary>
    /// The next message the game handles.
    /// </summary>
    public Msg Take() {
        var message = Sent[0];
        Sent = Sent.RemoveAt(0);
        return message;
    }

    public void Clear() {
        Sent = Seq<Msg>.Empty;
        Open = Seq<Msg>.Empty;
    }

    public int Pending => Sent.Count + Open.Count;

    public int Count(Func<Msg, bool> test) => Sent.Count(test) + Open.Count(test);
}
