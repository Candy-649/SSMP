using System.Collections.Generic;

namespace SSMP.Networking.Packet.Data;

/// <summary>
/// Packet data for two-player saves: pairing the saves of two players, checking both saves while both players are in
/// them, and the world progress of a save that the other player adds to theirs. It goes to one other player.
/// </summary>
internal class CoopSaveUpdate : IPacketData {
    /// <inheritdoc />
    public bool IsReliable => true;

    /// <inheritdoc />
    public bool DropReliableDataIfNewerExists => false;

    /// <summary>
    /// The ID of the player that the update comes from. The server fills it in for the player who receives it.
    /// </summary>
    public ushort PlayerId { get; set; }

    /// <summary>
    /// The ID of the player that the update goes to.
    /// </summary>
    public ushort TargetId { get; set; }

    /// <summary>
    /// What the update is about.
    /// </summary>
    public CoopSaveUpdateKind Kind { get; set; }

    /// <summary>
    /// For pairing, the ID of the request. For a hello, world progress and leaving, the key of the check, which tells
    /// newer checks from older ones.
    /// </summary>
    public ulong Key { get; set; }

    /// <summary>
    /// For a hello, the save key of the player that the save of the sender is paired with.
    /// </summary>
    public string PartnerKey { get; set; } = "";

    /// <summary>
    /// For world progress, which part of the progress this is, counting from 0.
    /// </summary>
    public ushort Part { get; set; }

    /// <summary>
    /// For world progress, how many parts the progress is sent in. For a hello, 0 for one that starts a check and 1 for
    /// one that answers.
    /// </summary>
    public ushort PartCount { get; set; }

    /// <summary>
    /// For world progress and pairing, the records of beaten bosses in the save of the sender.
    /// </summary>
    public List<string> Records { get; set; } = [];

    /// <summary>
    /// For world progress, the scenes of the saved objects of the world that are set, in the order of
    /// <see cref="ItemIds"/>.
    /// </summary>
    public List<string> ItemScenes { get; set; } = [];

    /// <summary>
    /// For world progress, the IDs of the saved objects of the world that are set, in the order of
    /// <see cref="ItemScenes"/>.
    /// </summary>
    public List<string> ItemIds { get; set; } = [];

    /// <inheritdoc />
    public void WriteData(IPacket packet) {
        packet.Write(PlayerId);
        packet.Write(TargetId);
        packet.Write((byte) Kind);
        packet.Write(Key);
        packet.Write(PartnerKey);
        packet.Write(Part);
        packet.Write(PartCount);
        WriteStrings(packet, Records);
        WriteStrings(packet, ItemScenes);
        WriteStrings(packet, ItemIds);
    }

    /// <inheritdoc />
    public void ReadData(IPacket packet) {
        PlayerId = packet.ReadUShort();
        TargetId = packet.ReadUShort();
        Kind = (CoopSaveUpdateKind) packet.ReadByte();
        Key = packet.ReadULong();
        PartnerKey = packet.ReadString();
        Part = packet.ReadUShort();
        PartCount = packet.ReadUShort();
        Records = ReadStrings(packet);
        ItemScenes = ReadStrings(packet);
        ItemIds = ReadStrings(packet);
    }

    /// <summary>
    /// Writes a list of strings with its length.
    /// </summary>
    private static void WriteStrings(IPacket packet, List<string> strings) {
        packet.Write((ushort) strings.Count);
        foreach (var text in strings) {
            packet.Write(text);
        }
    }

    /// <summary>
    /// Reads a list of strings that was written with its length.
    /// </summary>
    private static List<string> ReadStrings(IPacket packet) {
        var count = packet.ReadUShort();
        var strings = new List<string>(count);
        for (var i = 0; i < count; i++) {
            strings.Add(packet.ReadString());
        }

        return strings;
    }
}

/// <summary>
/// What an update of a two-player save is about.
/// </summary>
internal enum CoopSaveUpdateKind : byte {
    /// <summary>
    /// A player asks the other player to pair their current saves as a two-player save, with the bosses their save has
    /// beaten.
    /// </summary>
    PairRequest,

    /// <summary>
    /// A player agrees to a request to pair saves, with the bosses their save has beaten. Nothing is paired until the
    /// game of the player who asked confirms.
    /// </summary>
    PairAccept,

    /// <summary>
    /// A player can't pair saves with the player who asked, because their saves have beaten different bosses, which the
    /// update lists for the sender.
    /// </summary>
    PairRefused,

    /// <summary>
    /// The player who asked paired their save after the other player agreed, so the other player pairs theirs too.
    /// </summary>
    PairConfirm,

    /// <summary>
    /// A request to pair saves didn't go through, and a save that was already paired for it is unpaired again.
    /// </summary>
    PairCancel,

    /// <summary>
    /// A player asks the other player to make their two-player save a normal save again.
    /// </summary>
    UnpairRequest,

    /// <summary>
    /// A player made their two-player save with the other player a normal save again.
    /// </summary>
    Unpaired,

    /// <summary>
    /// A player plays a save that is paired with the save key in the update.
    /// </summary>
    Hello,

    /// <summary>
    /// A part of the world progress of the save of the sender.
    /// </summary>
    WorldState,

    /// <summary>
    /// A player left the two-player save that they were playing with the other player, like by going to the menu.
    /// </summary>
    Left
}
