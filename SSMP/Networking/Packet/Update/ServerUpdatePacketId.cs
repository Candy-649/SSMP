namespace SSMP.Networking.Packet.Update;

/// <summary>
/// Enumeration of packet IDs for the update packet for client to server communication.
/// </summary>
public enum ServerUpdatePacketId {
    /// <summary>
    /// Indicates slice data from a chunk for large data transfer.
    /// </summary>
    Slice = 0,

    /// <summary>
    /// Indicates the acknowledgement for a slice from a chunk for large data transfer.
    /// </summary>
    SliceAck = 1,

    /// <summary>
    /// Indicating that a client is disconnecting.
    /// </summary>
    PlayerDisconnect = 2,

    /// <summary>
    /// Update of realtime player values.
    /// </summary>
    PlayerUpdate = 3,

    /// <summary>
    /// Update of player map position.
    /// </summary>
    PlayerMapUpdate = 4,
    
    /// <summary>
    /// Notify that an entity has spawned.
    /// </summary>
    EntitySpawn = 5,

    /// <summary>
    /// Update of realtime entity values.
    /// </summary>
    EntityUpdate = 6,
    
    /// <summary>
    /// Update of realtime reliable entity values.
    /// </summary>
    ReliableEntityUpdate = 7,

    /// <summary>
    /// Notify that the player has entered a new scene.
    /// </summary>
    PlayerEnterScene = 8,

    /// <summary>
    /// Notify that the player has left their current scene.
    /// </summary>
    PlayerLeaveScene = 9,

    /// <summary>
    /// Notify that a player has died.
    /// </summary>
    PlayerDeath = 10,

    /// <summary>
    /// Player sent chat message.
    /// </summary>
    ChatMessage = 11,
    
    /// <summary>
    /// Value in the save file has updated.
    /// </summary>
    SaveUpdate = 12,
    
    /// <summary>
    /// Server settings are updated.
    /// </summary>
    ServerSettings = 13,
 
    /// <summary>
    /// Player settings are update for the local player.
    /// </summary>
    PlayerSetting = 14,

    /// <summary>
    /// Notify that the player rested at a bench or died, which respawns semi-persistent objects such as enemies.
    /// </summary>
    SemiPersistentReset = 15,

    /// <summary>
    /// The state of an arena in the current scene changed, or the player asks the scene host to start its battle.
    /// </summary>
    BattleSceneUpdate = 16,

    /// <summary>
    /// Something happened in a room whose gates are closed by an FSM, such as a boss room.
    /// </summary>
    BossRoomUpdate = 17,

    /// <summary>
    /// An update of a two-player save for another player.
    /// </summary>
    CoopSaveUpdate = 18,

    /// <summary>
    /// A hit of the player in a two-player save, which the game of the other player replays.
    /// </summary>
    CoopHitUpdate = 19,

    /// <summary>
    /// A request to be told again who and what is in the room this player is in.
    ///
    /// The server says that once, when a player says they have entered a room, and until this there was no second
    /// chance: a player whose game never heard it spent the rest of its stay in that room unable to see their
    /// partner and deaf to everything the room contained, with no way back but leaving.
    /// </summary>
    SceneResyncRequest = 20
}
