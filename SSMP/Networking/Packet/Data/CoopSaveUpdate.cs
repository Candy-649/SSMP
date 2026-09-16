using System.Collections.Generic;

namespace SSMP.Networking.Packet.Data;

/// <summary>
/// Packet data for two-player saves: pairing the saves of two players, checking both saves while both players are in
/// them, the world progress of a save that the other player adds to theirs, and mechanisms that a player used, which
/// the game of the other player replays. It goes to one other player.
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
    /// newer checks from older ones. For a lift, a count that grows with every ride that the sender starts. For an
    /// answer about a delivery that broke, the <see cref="Sequence"/> of the report that it answers.
    /// </summary>
    public ulong Key { get; set; }

    /// <summary>
    /// For a hello, the save key of the player that the save of the sender is paired with.
    /// </summary>
    public string PartnerKey { get; set; } = "";

    /// <summary>
    /// For world progress, which part of the progress this is, counting from 0. For a lift that stops at stops, the
    /// stop.
    /// </summary>
    public ushort Part { get; set; }

    /// <summary>
    /// For world progress, how many parts the progress is sent in. For a hello, 0 for one that starts a check and 1 for
    /// one that answers. For an interaction, 1 if the use of the other player had arrived before the sender used it.
    /// For key dialogue, 1 when it starts, 0 when it ends and 2 when it couldn't start without the other player. For a
    /// call of a lift, 1 if the sender is inside it. For the state of a lift, 1 while it moves. For driving a carriage,
    /// 1 while the sender holds its button. For a delivery that broke, 0 when it broke for the sender, 1 when the sender
    /// still carries theirs and 2 when the sender doesn't either. For dialogue about wishes, how many of its changes of
    /// wishes went to the other player before, which come again for what the dialogue changed afterwards.
    /// </summary>
    public ushort PartCount { get; set; }

    /// <summary>
    /// For world progress and pairing, the records of beaten bosses in the save of the sender. For bringing the other
    /// player to a delivery, the door of its scene to come in through.
    /// </summary>
    public List<string> Records { get; set; } = [];

    /// <summary>
    /// For world progress, the scenes of the saved objects of the world that are set, in the order of
    /// <see cref="ItemIds"/>.
    /// </summary>
    public List<string> ItemScenes { get; set; } = [];

    /// <summary>
    /// For world progress, the IDs of the saved objects of the world that are set, in the order of
    /// <see cref="ItemScenes"/>. For dialogue that accepted or completed wishes, what it took and gave, each after the
    /// index of its wish in <see cref="WishNames"/> or -1 for all of them, in the order of <see cref="Amounts"/>.
    /// </summary>
    public List<string> ItemIds { get; set; } = [];

    /// <summary>
    /// For changes of the world, interactions and world progress, the names of flags of the player data: booleans,
    /// integers and enums. In the order of <see cref="FlagValues"/>.
    /// </summary>
    public List<string> FlagNames { get; set; } = [];

    /// <summary>
    /// The values of the flags of the player data in <see cref="FlagNames"/>, with 1 and 0 for booleans and the numbers
    /// of enum values.
    /// </summary>
    public List<int> FlagValues { get; set; } = [];

    /// <summary>
    /// For changes of the wish log and world progress, the names of wishes and rumours, in the order of
    /// <see cref="WishValues"/>. For dialogue, the wishes that it accepted or completed, and for the progress of
    /// wishes, a wish for each of its targets.
    /// </summary>
    public List<string> WishNames { get; set; } = [];

    /// <summary>
    /// The packed states of the wishes and rumours in <see cref="WishNames"/>. For the progress of wishes, the index of
    /// each target.
    /// </summary>
    public List<int> WishValues { get; set; } = [];

    /// <summary>
    /// For world progress, the play time of the save of the sender in seconds. For the state of a lift, how long the
    /// sender has been in its room.
    /// </summary>
    public float PlayTime { get; set; }

    /// <summary>
    /// For changes of the world, changes of the wish log, interactions and deliveries that broke for the sender, the
    /// check that the sender was in, in the upper half, and a count that grows with every such update of the sender, in
    /// the lower half. The network can deliver an update that it sent again after a newer one, which this tells apart.
    /// </summary>
    public ulong Sequence { get; set; }

    /// <summary>
    /// For key dialogue, the amounts of what it took and gave in <see cref="ItemIds"/>, and for the progress of wishes,
    /// the progress of the targets in <see cref="WishNames"/>.
    /// </summary>
    public List<int> Amounts { get; set; } = [];

    /// <summary>
    /// For an interaction, the scene of the object that the sender used. For a lift, the scene of the lift. For a
    /// delivery, the scene of the character that takes it in.
    /// </summary>
    public string Scene { get; set; } = "";

    /// <summary>
    /// For an interaction or a lift, the path of the object in its scene.
    /// </summary>
    public string ObjectPath { get; set; } = "";

    /// <summary>
    /// For an interaction, the name of the FSM of the object that runs it, or empty for an item receptacle. For a lift,
    /// the name of the FSM that runs it, empty for a cage lift, or ManualLift for a carriage.
    /// </summary>
    public string FsmName { get; set; } = "";

    /// <summary>
    /// For an interaction, the state of the FSM in which its change to the world starts. For a lift that an FSM runs,
    /// the state that its ride goes to.
    /// </summary>
    public string StateName { get; set; } = "";

    /// <summary>
    /// For a lift, where it was when the update was sent: its height, or for a carriage the part of its way, its
    /// speed and the direction that it speeds up to. For bringing the other player to a delivery, where the sender
    /// stands.
    /// </summary>
    public List<float> Values { get; set; } = [];

    /// <summary>
    /// Names that a kind of update carries as a set, like the boss scenes that the save of the sender has
    /// unlocked. They only ever get added, so both games take the union of them.
    /// </summary>
    public List<string> Names { get; set; } = [];

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
        WriteStrings(packet, FlagNames);
        packet.Write((ushort) FlagValues.Count);
        foreach (var value in FlagValues) {
            packet.Write(value);
        }

        packet.Write(Scene);
        packet.Write(ObjectPath);
        packet.Write(FsmName);
        packet.Write(StateName);
        WriteStrings(packet, WishNames);
        packet.Write((ushort) WishValues.Count);
        foreach (var value in WishValues) {
            packet.Write(value);
        }

        packet.Write(PlayTime);
        packet.Write(Sequence);
        packet.Write((ushort) Amounts.Count);
        foreach (var amount in Amounts) {
            packet.Write(amount);
        }

        packet.Write((ushort) Values.Count);
        foreach (var value in Values) {
            packet.Write(value);
        }

        WriteStrings(packet, Names);
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
        FlagNames = ReadStrings(packet);
        var valueCount = packet.ReadUShort();
        FlagValues = new List<int>(valueCount);
        for (var i = 0; i < valueCount; i++) {
            FlagValues.Add(packet.ReadInt());
        }

        Scene = packet.ReadString();
        ObjectPath = packet.ReadString();
        FsmName = packet.ReadString();
        StateName = packet.ReadString();
        WishNames = ReadStrings(packet);
        var wishCount = packet.ReadUShort();
        WishValues = new List<int>(wishCount);
        for (var i = 0; i < wishCount; i++) {
            WishValues.Add(packet.ReadInt());
        }

        PlayTime = packet.ReadFloat();
        Sequence = packet.ReadULong();
        var amountCount = packet.ReadUShort();
        Amounts = new List<int>(amountCount);
        for (var i = 0; i < amountCount; i++) {
            Amounts.Add(packet.ReadInt());
        }

        var floatCount = packet.ReadUShort();
        Values = new List<float>(floatCount);
        for (var i = 0; i < floatCount; i++) {
            Values.Add(packet.ReadFloat());
        }

        Names = ReadStrings(packet);
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
    Left,

    /// <summary>
    /// A boss fight started with both players in the scene that the update lists, so the game of the other player gives
    /// it a checkpoint too.
    /// </summary>
    BossFight,

    /// <summary>
    /// Saved objects of the world and flags of the player data that got set in the game of the sender during the
    /// two-player save, which the other game adds to its save at once.
    /// </summary>
    WorldChange,

    /// <summary>
    /// The sender paid for or confirmed a mechanism, which now changes the world, so the game of the other player
    /// replays that change on its copy of the mechanism.
    /// </summary>
    Interaction,

    /// <summary>
    /// Wishes and rumours whose state changed in the wish log of the sender during the two-player save, which the other
    /// game adds to its wish log at once.
    /// </summary>
    WishChange,

    /// <summary>
    /// The sender started or ended key dialogue with a character, like about a wish, whom the other player can't talk
    /// to meanwhile, or couldn't start it because the other player isn't close by.
    /// </summary>
    WishTalk,

    /// <summary>
    /// Dialogue of the sender accepted or completed wishes, with their new states and what it took from the sender and
    /// gave them, which the game of the other player takes and gives too.
    /// </summary>
    WishTurnIn,

    /// <summary>
    /// The progress of the targets of the accepted wishes, and of the wishes that take something, in the save of the
    /// sender.
    /// </summary>
    WishProgress,

    /// <summary>
    /// A lift started a ride in the game that decides about the lift, so the game of the other player starts the same
    /// ride.
    /// </summary>
    LiftMove,

    /// <summary>
    /// The local player called a lift or started a ride from inside it in a game that doesn't decide about the lift, so
    /// the game that decides serves the call.
    /// </summary>
    LiftCall,

    /// <summary>
    /// The sender entered a room and asks for the state of its lifts, which the game of a player in that room sends.
    /// </summary>
    LiftStateRequest,

    /// <summary>
    /// Where a lift in the room of the sender is and whether it moves, for a player who entered that room.
    /// </summary>
    LiftState,

    /// <summary>
    /// The sender drives a carriage with its buttons, or let go of them, with where the carriage is.
    /// </summary>
    LiftDrive,

    /// <summary>
    /// The item of a delivery broke for the sender, with the new state of its wish, or the sender answers whether they
    /// still carry theirs.
    /// </summary>
    DeliveryBreak,

    /// <summary>
    /// The sender turned in a delivery at a character, so the game of the other player brings them there.
    /// </summary>
    DeliverySummon,

    /// <summary>
    /// A character gave the sender an item of the story in dialogue that the other player doesn't read, or a one-off
    /// story interaction used one up, so the game of the other player gives or takes it too. Picking one up in the
    /// world is not sent: every pickup stays one copy per player.
    /// </summary>
    StoryItem,

    /// <summary>
    /// The sender answered yes to a prompt that changes what both players share, so the other player is asked
    /// to agree to it, answers one they were asked about, or hears that the prompt is over.
    /// </summary>
    WishConfirm
}
