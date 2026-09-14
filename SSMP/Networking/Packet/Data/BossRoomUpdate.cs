namespace SSMP.Networking.Packet.Data;

/// <summary>
/// Packet data for a room whose gates are closed by an FSM, such as a boss room.
/// </summary>
internal class BossRoomUpdate : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => false;

    /// <summary>
    /// The name of the scene that the room is in.
    /// </summary>
    public string SceneName { get; set; } = "";

    /// <summary>
    /// What happened in the room.
    /// </summary>
    public BossRoomUpdateKind Kind { get; set; }

    /// <summary>
    /// The path of the object with the FSM in the scene.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// The name of the FSM.
    /// </summary>
    public string FsmName { get; set; } = "";

    /// <summary>
    /// The state that the FSM left, for a transition.
    /// </summary>
    public string FromState { get; set; } = "";

    /// <summary>
    /// The state that the FSM entered, for a transition.
    /// </summary>
    public string ToState { get; set; } = "";

    /// <summary>
    /// The event of a transition, or the event that opened a gate.
    /// </summary>
    public string EventName { get; set; } = "";

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(SceneName);
        packet.Write((byte) Kind);
        packet.Write(Path);
        packet.Write(FsmName);
        packet.Write(FromState);
        packet.Write(ToState);
        packet.Write(EventName);
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        SceneName = packet.ReadString();
        Kind = (BossRoomUpdateKind) packet.ReadByte();
        Path = packet.ReadString();
        FsmName = packet.ReadString();
        FromState = packet.ReadString();
        ToState = packet.ReadString();
        EventName = packet.ReadString();
    }
}

/// <summary>
/// What happened in a room whose gates are closed by an FSM.
/// </summary>
internal enum BossRoomUpdateKind : byte {
    /// <summary>
    /// A player who is not the scene host walked into the room, and the FSM that controls the room left the state that
    /// waited for them. The scene host moves its FSM the same way, so that the fight starts there too.
    /// </summary>
    Transition,

    /// <summary>
    /// The scene host can't start the fight of a room that another player walked into, for example because it already
    /// won it, so the gates that the room closed for the other players open again.
    /// </summary>
    RoomDone,

    /// <summary>
    /// A gate of the scene host opened after it had closed, so it opens for the other players too.
    /// </summary>
    GateOpened
}
