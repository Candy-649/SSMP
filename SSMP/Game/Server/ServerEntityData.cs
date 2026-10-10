using System.Collections.Generic;
using SSMP.Game.Client.Entity;
using SSMP.Math;
using SSMP.Networking.Packet.Data;

namespace SSMP.Game.Server;

/// <summary>
/// Class containing all the relevant data managed by the server about an entity.
/// </summary>
internal class ServerEntityData {
    /// <summary>
    /// Whether this entity spawned while in a scene already.
    /// </summary>
    public bool Spawned { get; set; }
    /// <summary>
    /// The type of the entity that spawned the new entity.
    /// </summary>
    public EntityType SpawningType { get; set; }
    /// <summary>
    /// The type of the entity that was spawned.
    /// </summary>
    public EntityType SpawnedType { get; set; }

    /// <summary>
    /// The last position of the entity.
    /// </summary>
    public Vector3? Position { get; set; }
    /// <summary>
    /// The last scale data of the entity.
    /// </summary>
    public EntityUpdate.ScaleData Scale { get; set; }
    /// <summary>
    /// The ID of the last played animation.
    /// </summary>
    public byte? AnimationId { get; set; }
    /// <summary>
    /// The wrap mode of the last played animation.
    /// </summary>
    public byte AnimationWrapMode { get; set; }
    /// <summary>
    /// Whether the entity is active.
    /// </summary>
    public bool? IsActive { get; set; }
    
    /// <summary>
    /// Generic data associated with this entity.
    /// </summary>
    public List<EntityNetworkData> GenericData { get; }
    
    /// <summary>
    /// Host FSM data to keep track of for transferring scene host.
    /// </summary>
    public Dictionary<byte, EntityHostFsmData> HostFsmData { get; }

    /// <summary>
    /// When the server last took in each kind of thing about this entity as it happened, by the kind (see
    /// ServerManager.OnRoomSnapshot).
    /// </summary>
    public Dictionary<int, System.DateTime> HeardAt { get; } = new();

    /// <summary>
    /// The player whose packets <see cref="_order"/> counts.
    /// </summary>
    private ushort _orderOf;

    /// <summary>
    /// The order of the packets that each kind of thing about this entity was last taken from, by the kind (see
    /// ServerManager.HeardPosition and the others).
    /// </summary>
    private readonly Dictionary<int, Util.PositionSequence> _order = new();

    /// <summary>
    /// Whether to take a kind of thing about this entity that a player sent in a packet: not if it is older than the one
    /// of that kind last taken from them. A message held back on the way, while something before it was sent again,
    /// arrives after ones sent later, and passed on it put a copy back where the creature stood or faced a moment before
    /// - for good, when nothing came after it, as with the last turn before a creature charged in a straight line. The
    /// count starts again with another player, whose packets are numbered apart.
    /// </summary>
    /// <param name="playerId">The ID of the player who sent it.</param>
    /// <param name="kind">The kind of thing.</param>
    /// <param name="sequence">The number of the packet it came in.</param>
    public bool TakesNewer(ushort playerId, int kind, ushort sequence) {
        if (_order.Count > 0 && _orderOf != playerId) {
            _order.Clear();
        }

        _orderOf = playerId;
        if (!_order.TryGetValue(kind, out var order)) {
            order = new Util.PositionSequence($"what player {playerId} sends of an entity ({kind})");
            _order[kind] = order;
        }

        return order.Accepts(sequence, out _);
    }

    public ServerEntityData() {
        Scale = new EntityUpdate.ScaleData();
        GenericData = [];
        HostFsmData = new Dictionary<byte, EntityHostFsmData>();
    }
}
