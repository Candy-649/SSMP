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
    /// What the hit hit.
    /// </summary>
    public CoopHitKind Kind { get; set; }

    /// <summary>
    /// For the knockback of an enemy, the ID of its entity.
    /// </summary>
    public ushort EntityId { get; set; }

    /// <summary>
    /// For a hit on an object, the name of the scene of the object.
    /// </summary>
    public string Scene { get; set; } = "";

    /// <summary>
    /// For a hit on an object, the path of the object in its scene.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// For a hit on an object, the name of the type of the component on the object that took the hit.
    /// </summary>
    public string Responder { get; set; } = "";

    /// <summary>
    /// For a hit on an object, which component of that type on the object took the hit, counting from 0.
    /// </summary>
    public byte Index { get; set; }

    /// <summary>
    /// The hit itself, or the direction and magnitude of the knockback of an enemy, which only the games of the
    /// players read.
    /// </summary>
    public byte[] Hit { get; set; } = [];

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(PlayerId);
        packet.Write(TargetId);
        packet.Write((byte) Kind);
        packet.Write(EntityId);
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
        Kind = (CoopHitKind) packet.ReadByte();
        EntityId = packet.ReadUShort();
        Scene = packet.ReadString();
        Path = packet.ReadString();
        Responder = packet.ReadString();
        Index = packet.ReadByte();
        Hit = packet.ReadBytes(packet.ReadUShort());
    }
}

/// <summary>
/// What a hit in a two-player save hit.
/// </summary>
internal enum CoopHitKind : byte {
    /// <summary>
    /// A shared object of the world, found by its path, on which the other game replays the hit.
    /// </summary>
    Object,

    /// <summary>
    /// An enemy, found by the ID of its entity, whose knockback the game of the scene host applies.
    /// </summary>
    EnemyKnockback,

    /// <summary>
    /// An enemy, found by the ID of its entity, whose hit effect the other game plays.
    ///
    /// A copy of an attack no longer hits enemies at all, so the other game has nothing left to show for a hit that
    /// landed here. The hit itself is sent so that the effect it plays there is the one this hit really was, rather
    /// than a generic one guessed from a direction.
    /// </summary>
    EnemyHitEffect
}
