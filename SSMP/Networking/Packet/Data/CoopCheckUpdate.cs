namespace SSMP.Networking.Packet.Data;

/// <summary>
/// Packet data for comparing the state of the room that both players of a two-player save are in, between their two
/// games. It goes to one other player. Only the games of the players read what it carries.
/// </summary>
internal class CoopCheckUpdate : IPacketData {
    /// <summary>
    /// A digest is sent again every few seconds, so one that goes missing is not sent again. The rest are answers
    /// that the other game waits for.
    /// </summary>
    public bool IsReliable => Kind != CoopCheckKind.Digest;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => false;

    /// <summary>
    /// The ID of the player that the update comes from. The server fills it in for the player who receives it.
    /// </summary>
    public ushort PlayerId { get; set; }

    /// <summary>
    /// The ID of the player that the update goes to.
    /// </summary>
    public ushort TargetId { get; set; }

    /// <summary>
    /// What the update is.
    /// </summary>
    public CoopCheckKind Kind { get; set; }

    /// <summary>
    /// What the update carries, which only the games of the players read.
    /// </summary>
    public byte[] Data { get; set; } = [];

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(PlayerId);
        packet.Write(TargetId);
        packet.Write((byte) Kind);
        packet.Write((ushort) Data.Length);
        packet.Write(Data);
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        PlayerId = packet.ReadUShort();
        TargetId = packet.ReadUShort();
        Kind = (CoopCheckKind) packet.ReadByte();
        Data = packet.ReadBytes(packet.ReadUShort());
    }
}

/// <summary>
/// What an update that compares the state of a room is.
/// </summary>
internal enum CoopCheckKind : byte {
    /// <summary>
    /// A digest of the values of the room that did not change for a while, in groups, which the other game compares
    /// with its own.
    /// </summary>
    Digest,

    /// <summary>
    /// The groups whose digests differed, with the values of the sender in them, for the other game to answer with the
    /// values of its own that differ.
    /// </summary>
    DetailRequest,

    /// <summary>
    /// The values of the sender that differ from those in a request, with their names and how long they have been so.
    /// </summary>
    Detail
}
