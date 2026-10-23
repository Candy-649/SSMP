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

    public ClientPlayerSettingUpdate() {
        UpdateTypes = new HashSet<PlayerSettingUpdateType>();

        // What another player wears, their crest and their team are each said once, when they change, so an update
        // that the network loses has to come again: otherwise the character of that player kept the look, the crest
        // or the team it had before, for good. An older one comes again before the newer ones that follow it
        IsReliable = true;
        DropReliableDataIfNewerExists = false;
    }

    /// <inheritdoc />
    public override void WriteData(IPacket packet) {
        packet.Write(Self);

        if (!Self) {
            packet.Write(Id);
        }

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
    /// A newer update need not say what a lost one said: one may say the crest and the other what the player wears.
    /// </summary>
    public bool DropReliableDataIfNewerExists => false;
    
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

    public ServerPlayerSettingUpdate() {
        UpdateTypes = new HashSet<PlayerSettingUpdateType>();
    }
    
    /// <inheritdoc />
    public void WriteData(IPacket packet) {
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
