namespace SSMP.Networking.Packet.Data;

/// <summary>
/// Packet data for the state of an arena, where gates close and waves of enemies attack.
/// </summary>
internal class BattleSceneUpdate : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => false;

    /// <summary>
    /// The name of the scene that the arena is in.
    /// </summary>
    public string SceneName { get; set; } = "";

    /// <summary>
    /// The path of the arena object in the scene.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// The state of the battle in the arena.
    /// </summary>
    public BattleSceneStatus Status { get; set; }

    /// <summary>
    /// The last wave that started, or -1 if no wave started yet.
    /// </summary>
    public short Wave { get; set; }

    /// <summary>
    /// The number of enemies that the arena counts as remaining.
    /// </summary>
    public short Enemies { get; set; }

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(SceneName);
        packet.Write(Path);
        packet.Write((byte) Status);
        packet.Write(Wave);
        packet.Write(Enemies);
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        SceneName = packet.ReadString();
        Path = packet.ReadString();
        Status = (BattleSceneStatus) packet.ReadByte();
        Wave = packet.ReadShort();
        Enemies = packet.ReadShort();
    }
}

/// <summary>
/// The state of the battle in an arena.
/// </summary>
internal enum BattleSceneStatus : byte {
    /// <summary>
    /// A player who is not the scene host walked into the arena and asks the scene host to start the battle.
    /// </summary>
    StartRequest,

    /// <summary>
    /// The battle runs on the scene host.
    /// </summary>
    Running,

    /// <summary>
    /// The scene host just won the battle, so the players in the room win it too.
    /// </summary>
    Won,

    /// <summary>
    /// The scene host had already won the battle before, so there is nothing to fight.
    /// </summary>
    AlreadyWon,

    /// <summary>
    /// The arena is not active for the scene host, so its battle can't run.
    /// </summary>
    Unavailable
}
