using SSMP.Game.Client.Entity;
using SSMP.Math;

namespace SSMP.Networking.Packet.Data;

/// <summary>
/// Packet data for an entity spawn. Either from entering a scene or from spawning while in a scene.
/// </summary>
internal class EntitySpawn : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => false;

    /// <summary>
    /// The ID of the spawned entity.
    /// </summary>
    public ushort Id { get; set; }
    
    /// <summary>
    /// The type of the entity that spawned the new entity.
    /// </summary>
    public EntityType SpawningType { get; set; }
    
    /// <summary>
    /// The type of the entity that was spawned.
    /// </summary>
    public EntityType SpawnedType { get; set; }

    /// <summary>
    /// Where the game that runs the room made the entity, in the world, or null for one that the room makes, which
    /// every game makes where the room makes such creatures (RoomCreatures.Make). What a creature sets up as it starts
    /// is left where it was made: a summoned creature that hangs from a pivot above it leaves the pivot there, swaying
    /// and creaking for good. The scene host's was made outside the room and could not be heard; the copy here was
    /// made beside the creature that summoned it, in the middle of the fight, and moved out only by its first
    /// position, which left the pivot creaking above the fight (USER 10-30, "我明明打死了，我这里还有它在天上待机的
    /// 声音", "我跑远一点就没有了").
    /// </summary>
    public Vector3? Position { get; set; }

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(Id);
        packet.Write((ushort) SpawningType);
        packet.Write((ushort) SpawnedType);
        packet.Write(Position != null);
        if (Position != null) {
            packet.Write(Position);
        }
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        Id = packet.ReadUShort();
        SpawningType = (EntityType) packet.ReadUShort();
        SpawnedType = (EntityType) packet.ReadUShort();
        Position = packet.ReadBool() ? packet.ReadVector3() : null;
    }
}
