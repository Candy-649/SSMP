namespace SSMP.Networking.Packet.Data;

/// <summary>
/// Packet data for the client-bound room of another player.
/// </summary>
internal class ClientPlayerRoom : GenericClientData {
    /// <summary>
    /// The name of the scene the player is in, or empty while they are in none.
    /// </summary>
    public string SceneName { get; set; } = "";

    /// <summary>
    /// Construct the client player room data.
    /// </summary>
    public ClientPlayerRoom() {
        IsReliable = true;
        // Only the room the player is in now matters, so a lost one is not sent again over a newer one
        DropReliableDataIfNewerExists = true;
    }

    /// <inheritdoc />
    public override void WriteData(IPacket packet) {
        packet.Write(Id);
        packet.Write(SceneName);
    }

    /// <inheritdoc />
    public override void ReadData(IPacket packet) {
        Id = packet.ReadUShort();
        SceneName = packet.ReadString();
    }
}
