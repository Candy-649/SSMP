namespace SSMP.Networking.Packet.Data;

/// <summary>
/// Packet data for a hit of a player in a two-player save, which the game of the other player replays on its copy of
/// the object that was hit. It goes to one other player.
/// </summary>
internal class CoopHitUpdate : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => false;

    /// <summary>
    /// The ID of the player that the hit comes from. The server fills it in for the player who receives it.
    /// </summary>
    public ushort PlayerId { get; set; }

    /// <summary>
    /// The ID of the player that the hit goes to.
    /// </summary>
    public ushort TargetId { get; set; }

    /// <summary>
    /// The name of the scene of the object that was hit.
    /// </summary>
    public string Scene { get; set; } = "";

    /// <summary>
    /// The path of the object that was hit in its scene.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// The name of the type of the component on the object that took the hit.
    /// </summary>
    public string Responder { get; set; } = "";

    /// <summary>
    /// Which component of that type on the object took the hit, counting from 0.
    /// </summary>
    public byte Index { get; set; }

    /// <summary>
    /// The hit itself, which only the games of the players read.
    /// </summary>
    public byte[] Hit { get; set; } = [];

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(PlayerId);
        packet.Write(TargetId);
        packet.Write(Scene);
        packet.Write(Path);
        packet.Write(Responder);
        packet.Write(Index);
        packet.Write((ushort) Hit.Length);
        packet.Write(Hit);
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        PlayerId = packet.ReadUShort();
        TargetId = packet.ReadUShort();
        Scene = packet.ReadString();
        Path = packet.ReadString();
        Responder = packet.ReadString();
        Index = packet.ReadByte();
        Hit = packet.ReadBytes(packet.ReadUShort());
    }
}
