namespace SSMP.Networking.Packet.Data;

/// <summary>
/// Packet data for a room whose gates are closed by an FSM or that starts a boss, such as a boss room.
/// </summary>
internal class BossRoomUpdate : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => false;

    /// <summary>
    /// The ID of the player that the update comes from. The server fills it in for the players who receive it.
    /// </summary>
    public ushort PlayerId { get; set; }

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
    /// The state that the FSM left, for a room that started, or the state that runs shared dialogue.
    /// </summary>
    public string FromState { get; set; } = "";

    /// <summary>
    /// The state that the FSM entered, for a room that started.
    /// </summary>
    public string ToState { get; set; } = "";

    /// <summary>
    /// The event that started the room, or the event that opened a gate.
    /// </summary>
    public string EventName { get; set; } = "";

    /// <summary>
    /// The name of the record in the player data, for a shared record.
    /// </summary>
    public string VariableName { get; set; } = "";

    /// <summary>
    /// The sheet of the localised text of shared dialogue, or empty if the dialogue isn't localised.
    /// </summary>
    public string DialogueSheet { get; set; } = "";

    /// <summary>
    /// The key of the localised text of shared dialogue, or empty if the dialogue isn't localised.
    /// </summary>
    public string DialogueKey { get; set; } = "";

    /// <summary>
    /// The text of shared dialogue that isn't localised.
    /// </summary>
    public string DialogueText { get; set; } = "";

    /// <summary>
    /// Whether shared dialogue hides the decorations of the dialogue box.
    /// </summary>
    public bool HideDecorators { get; set; }

    /// <summary>
    /// Whether shared dialogue overrides how the dialogue box continues.
    /// </summary>
    public bool OverrideContinue { get; set; }

    /// <summary>
    /// The text alignment of shared dialogue, or -1 for the default alignment.
    /// </summary>
    public int TextAlignment { get; set; } = -1;

    /// <summary>
    /// The vertical offset of the dialogue box of shared dialogue.
    /// </summary>
    public float OffsetY { get; set; }

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(PlayerId);
        packet.Write(SceneName);
        packet.Write((byte) Kind);
        packet.Write(Path);
        packet.Write(FsmName);
        packet.Write(FromState);
        packet.Write(ToState);
        packet.Write(EventName);
        packet.Write(VariableName);
        packet.Write(DialogueSheet);
        packet.Write(DialogueKey);
        packet.Write(DialogueText);
        packet.Write(HideDecorators);
        packet.Write(OverrideContinue);
        packet.Write(TextAlignment);
        packet.Write(OffsetY);
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        PlayerId = packet.ReadUShort();
        SceneName = packet.ReadString();
        Kind = (BossRoomUpdateKind) packet.ReadByte();
        Path = packet.ReadString();
        FsmName = packet.ReadString();
        FromState = packet.ReadString();
        ToState = packet.ReadString();
        EventName = packet.ReadString();
        VariableName = packet.ReadString();
        DialogueSheet = packet.ReadString();
        DialogueKey = packet.ReadString();
        DialogueText = packet.ReadString();
        HideDecorators = packet.ReadBool();
        OverrideContinue = packet.ReadBool();
        TextAlignment = packet.ReadInt();
        OffsetY = packet.ReadFloat();
    }
}

/// <summary>
/// What happened in a room whose gates are closed by an FSM or that starts a boss.
/// </summary>
internal enum BossRoomUpdateKind : byte {
    /// <summary>
    /// A player reached the trigger of a room, which only starts once all players reached it.
    /// </summary>
    Arrived,

    /// <summary>
    /// A player waits in a room for the other players. The other players hear about it in any scene.
    /// </summary>
    Waiting,

    /// <summary>
    /// A room started for a player who is not the scene host, so the scene host checks that it started there too.
    /// </summary>
    Started,

    /// <summary>
    /// A room that started for another player didn't start for the scene host, for example because it already won the
    /// fight, so the gates that the room closed for the other player open again.
    /// </summary>
    RoomDone,

    /// <summary>
    /// A gate of the scene host opened after it had closed, so it opens for the other players too.
    /// </summary>
    GateOpened,

    /// <summary>
    /// A room or a boss wrote a defeat or encounter record, which the other players in the scene write too.
    /// </summary>
    RecordSet,

    /// <summary>
    /// A boss of the scene host started dialogue, which the other players in the scene read too before it continues.
    /// </summary>
    DialogueStarted,

    /// <summary>
    /// A player read dialogue that the scene host shared.
    /// </summary>
    DialogueDone,

    /// <summary>
    /// A player got to the fight of a room with dialogue, which only starts once every player got there.
    /// </summary>
    Ready
}
