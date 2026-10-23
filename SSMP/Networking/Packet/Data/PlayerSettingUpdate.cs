using System.Collections.Generic;
using SSMP.Game;
using SSMP.Internals;

namespace SSMP.Networking.Packet.Data;

/// <summary>
/// Packet data for client-bound player setting update.
/// </summary>
internal class ClientPlayerSettingUpdate : GenericClientData {
    /// <summary>
    /// Whether the update is for the player receiving this packet.
    /// </summary>
    public bool Self { get; set; }

    /// <summary>
    /// Set of the types of updates that this packet contains. For example, only a skin update, or a skin and a team
    /// update.
    /// </summary>
    public ISet<PlayerSettingUpdateType> UpdateTypes { get; set; }

    /// <summary>
    /// The team of the player.
    /// </summary>
    public Team Team { get; set; }

    /// <summary>
    /// What the player wears through skin mods, as the text of a <c>SkinLook</c>, empty for the game's own look.
    /// </summary>
    public string Skin { get; set; } = "";

    /// <summary>
    /// The type of crest the player is using.
    /// </summary>
    public CrestType CrestType { get; set; }

    /// <summary>
    /// How many changes of the player's settings the server had told of when it sent this. Each update says all of
    /// them, and a game takes in none older than the last one it took in for that player.
    /// </summary>
    public ushort Version { get; set; }

    public ClientPlayerSettingUpdate() {
        UpdateTypes = new HashSet<PlayerSettingUpdateType>();

        // What another player wears, their crest and their team are said when they change, so an update that the
        // network loses has to come again: otherwise the character of that player kept the look, the crest or the
        // team it had before, for good. One that comes again may arrive after a newer one, which said all of it
        // already, so the game that takes them in goes by Version
        IsReliable = true;
        DropReliableDataIfNewerExists = false;
    }

    /// <inheritdoc />
    public override void WriteData(IPacket packet) {
        packet.Write(Self);

        if (!Self) {
            packet.Write(Id);
        }

        packet.Write(Version);
        packet.WriteBitFlag(UpdateTypes);

        if (UpdateTypes.Contains(PlayerSettingUpdateType.Team)) {
            packet.Write((byte) Team);
        }

        if (UpdateTypes.Contains(PlayerSettingUpdateType.Skin)) {
            packet.Write(Skin);
        }

        if (UpdateTypes.Contains(PlayerSettingUpdateType.Crest)) {
            packet.Write((byte) CrestType);
        }
    }

    /// <inheritdoc />
    public override void ReadData(IPacket packet) {
        Self = packet.ReadBool();

        if (!Self) {
            Id = packet.ReadUShort();
        }

        Version = packet.ReadUShort();
        UpdateTypes = packet.ReadBitFlag<PlayerSettingUpdateType>();

        if (UpdateTypes.Contains(PlayerSettingUpdateType.Team)) {
            Team = (Team) packet.ReadByte();
        }

        if (UpdateTypes.Contains(PlayerSettingUpdateType.Skin)) {
            Skin = packet.ReadString();
        }

        if (UpdateTypes.Contains(PlayerSettingUpdateType.Crest)) {
            CrestType = (CrestType) packet.ReadByte();
        }
    }
}

/// <summary>
/// Packet data for server-bound player setting update.
/// </summary>
internal class ServerPlayerSettingUpdate : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <summary>
    /// Each update says all of the player's settings, so a lost one need not come again with a newer one.
    /// </summary>
    public bool DropReliableDataIfNewerExists => true;
    
    /// <summary>
    /// Set of the types of updates that this packet contains. For example, only a skin update, or a skin and a team
    /// update.
    /// </summary>
    public ISet<PlayerSettingUpdateType> UpdateTypes { get; set; }

    /// <summary>
    /// The team of the player.
    /// </summary>
    public Team Team { get; set; }

    /// <summary>
    /// What the player wears through skin mods, as the text of a <c>SkinLook</c>, empty for the game's own look.
    /// </summary>
    public string Skin { get; set; } = "";

    /// <summary>
    /// The type of crest the player is using.
    /// </summary>
    public CrestType CrestType { get; set; }

    /// <summary>
    /// How many updates of the player's settings their game had sent when it sent this. Each says all of them, the
    /// team aside, which is asked for; the server takes in none older than the last one it took in.
    /// </summary>
    public ushort Version { get; set; }

    public ServerPlayerSettingUpdate() {
        UpdateTypes = new HashSet<PlayerSettingUpdateType>();
    }
    
    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(Version);
        packet.WriteBitFlag(UpdateTypes);

        if (UpdateTypes.Contains(PlayerSettingUpdateType.Team)) {
            packet.Write((byte) Team);
        }

        if (UpdateTypes.Contains(PlayerSettingUpdateType.Skin)) {
            packet.Write(Skin);
        }
        
        if (UpdateTypes.Contains(PlayerSettingUpdateType.Crest)) {
            packet.Write((byte) CrestType);
        }
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        Version = packet.ReadUShort();
        UpdateTypes = packet.ReadBitFlag<PlayerSettingUpdateType>();

        if (UpdateTypes.Contains(PlayerSettingUpdateType.Team)) {
            Team = (Team) packet.ReadByte();
        }

        if (UpdateTypes.Contains(PlayerSettingUpdateType.Skin)) {
            Skin = packet.ReadString();
        }
        
        if (UpdateTypes.Contains(PlayerSettingUpdateType.Crest)) {
            CrestType = (CrestType) packet.ReadByte();
        }
    }
}

/// <summary>
/// Enum for the type of player setting update.
/// </summary>
internal enum PlayerSettingUpdateType {
    Team = 0,
    Skin = 1,
    Crest = 2
}
