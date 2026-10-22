using System;
using System.Collections.Generic;
using SSMP.Logging;
using SSMP.Networking.Packet.Data;

namespace SSMP.Networking.Packet.Update;

/// <summary>
/// Abstract base class for the update packet.
/// </summary>
/// <typeparam name="TPacketId"></typeparam>
internal abstract class UpdatePacket<TPacketId> : BasePacket<TPacketId> where TPacketId : Enum {
    /// <summary>
    /// The sequence number of this packet.
    /// </summary>
    public ushort Sequence { get; set; }

    /// <summary>
    /// The acknowledgement number of this packet.
    /// </summary>
    public ushort Ack { get; set; }

    /// <summary>
    /// An array containing booleans that indicate whether sequence number (Ack - x) is also acknowledged
    /// for the x-th value in the array.
    /// </summary>
    public bool[] AckField { get; private set; } = new bool[ConnectionManager.AckSize];

    /// <summary>
    /// What the numbering of the packets with reliable data says in this packet, on a transport that keeps them
    /// reliable itself rather than this mod (see <see cref="ReliableNumbering"/>).
    /// </summary>
    public NumberingFlags Numbering { get; private set; }

    /// <summary>
    /// The number of this packet among the packets of its sender that carry reliable data, when
    /// <see cref="Numbering"/> says it is one of them; in a packet without reliable data that went through the
    /// numbering, the number of the last one that its sender sent.
    /// </summary>
    public ushort ReliableNumber { get; private set; }

    /// <summary>
    /// The number of the last packet with reliable data that the sender of this one took in from the other side, in
    /// the order they were sent, when <see cref="Numbering"/> says the packet went through the numbering.
    /// </summary>
    public ushort ReliableAck { get; private set; }

    /// <summary>
    /// Whether <see cref="ReadPacket"/> read the headers of this packet, its numbering among them, but not the rest of
    /// it. On a transport whose packets with reliable data are numbered, it is still taken in by its number, so that
    /// what came after it does not wait for it, and nothing of it is handled (UpdateManager.TakeInOrder).
    /// </summary>
    public bool OnlyHeadersRead { get; private set; }

    /// <summary>
    /// Where the numbering of reliable data is in the packet that <see cref="CreatePacket"/> made last, counted from
    /// its start, so that it can be written there once it is known (see <see cref="ReliableNumbering.Stamp"/>).
    /// </summary>
    public int ReliableHeaderAt { get; private set; }

    /// <summary>
    /// Resend packet data indexed by sequence number it originates from.
    /// </summary>
    private readonly Dictionary<ushort, Dictionary<TPacketId, IPacketData>> _resendPacketData = new();

    /// <summary>
    /// Resend addon packet data indexed by sequence number it originates from.
    /// </summary>
    private readonly Dictionary<ushort, Dictionary<byte, AddonPacketData>> _resendAddonPacketData = new();

    /// <summary>
    /// Write header info into the given packet (sequence number, acknowledgement number and ack field).
    /// </summary>
    /// <param name="packet">The packet to write the header info into.</param>
    private void WriteHeaders(Packet packet) {
        packet.Write(Sequence);
        packet.Write(Ack);

        ulong ackFieldInt = 0;
        ulong currentFieldValue = 1;
        for (var i = 0; i < ConnectionManager.AckSize; i++) {
            if (AckField[i]) {
                ackFieldInt |= currentFieldValue;
            }

            currentFieldValue *= 2;
        }

        packet.Write(ackFieldInt);

        // Written as nothing here and filled in once the packet is made, when it is known whether it carries reliable
        // data at all (UpdateManager.CreateAndSendPacket)
        ReliableHeaderAt = packet.Length;
        packet.Write((byte) NumberingFlags.None);
        packet.Write((ushort) 0);
        packet.Write((ushort) 0);
    }

    /// <summary>
    /// Read header info from the given packet (sequence number, acknowledgement number and ack field).
    /// </summary>
    /// <param name="packet">The packet to read header info from.</param>
    private void ReadHeaders(Packet packet) {
        Sequence = packet.ReadUShort();
        Ack = packet.ReadUShort();

        // Reuse the AckField array if it's already allocated with the correct size.
        // This avoids allocating a new bool array on every single received packet, greatly reducing GC pressure.
        if (AckField.Length != ConnectionManager.AckSize) {
            AckField = new bool[ConnectionManager.AckSize];
        }

        var ackFieldInt = packet.ReadULong();
        ulong currentFieldValue = 1;
        for (var i = 0; i < ConnectionManager.AckSize; i++) {
            AckField[i] = (ackFieldInt & currentFieldValue) != 0;

            currentFieldValue *= 2;
        }

        Numbering = (NumberingFlags) packet.ReadByte();
        ReliableNumber = packet.ReadUShort();
        ReliableAck = packet.ReadUShort();
    }

    /// <inheritdoc />
    public override void CreatePacket(Packet packet) {
        WriteHeaders(packet);

        base.CreatePacket(packet);

        // Put the length of the resend data as an ushort in the packet
        var resendLength = (ushort) _resendPacketData.Count;
        if (_resendPacketData.Count > ushort.MaxValue) {
            resendLength = ushort.MaxValue;

            Logger.Error("Length of resend packet data dictionary does not fit in ushort");
        }

        packet.Write(resendLength);

        // Add each entry of lost data to resend to the packet
        foreach (var seqPacketDataPair in _resendPacketData) {
            var seq = seqPacketDataPair.Key;
            var packetData = seqPacketDataPair.Value;

            // Make sure to not put more resend data in the packet than we specified
            if (resendLength-- == 0) {
                break;
            }

            // First write the sequence number it belongs to
            packet.Write(seq);

            // Then write the reliable packet data and note that this packet now contains reliable data
            WritePacketData(packet, packetData);
            ContainsReliableData = true;
        }

        // Put the length of the addon resend data as an ushort in the packet
        resendLength = (ushort) _resendAddonPacketData.Count;
        if (_resendAddonPacketData.Count > ushort.MaxValue) {
            resendLength = ushort.MaxValue;

            Logger.Error("Length of addon resend packet data dictionary does not fit in ushort");
        }

        packet.Write(resendLength);

        // Add each entry of lost addon data to resend to the packet
        foreach (var seqAddonDictPair in _resendAddonPacketData) {
            var seq = seqAddonDictPair.Key;
            var addonDataDict = seqAddonDictPair.Value;

            // Make sure to not put more resend data in the packet than we specified
            if (resendLength-- == 0) {
                break;
            }

            // First write the sequence number it belongs to
            packet.Write(seq);

            // Then write the reliable addon data for all addons and note that this packet
            // now contains reliable data
            WriteAddonDataDict(packet, addonDataDict);
            ContainsReliableData = true;
        }

        // The length goes in front of everything, which moves the numbering of reliable data along with the rest
        var unprefixed = packet.Length;
        packet.WriteLength();
        ReliableHeaderAt += packet.Length - unprefixed;
    }

    /// <inheritdoc />
    public override bool ReadPacket(Packet packet) {
        // TODO: maybe get rid of exception catching in packet reading and rely on bool return values for all
        // packet reading methods (including Packet class)
        try {
            ReadHeaders(packet);
        } catch (Exception e) {
            WarnUnreadable("its headers", e);
            return false;
        }

        if (!base.ReadPacket(packet)) {
            OnlyHeadersRead = true;
            return false;
        }

        StampReliableEntityUpdates(NormalPacketData, Sequence);

        try {
            // Read the length of the resend data
            var resendLength = packet.ReadUShort();

            while (resendLength-- > 0) {
                // Read the sequence number of the packet it was lost from
                var seq = packet.ReadUShort();

                // Create a new dictionary for the packet data and read the data from the packet into it
                var packetData = new Dictionary<TPacketId, IPacketData>();
                ReadPacketData(packet, packetData);
                StampReliableEntityUpdates(packetData, seq);

                // Input the data into the resend dictionary keyed by its sequence number
                _resendPacketData[seq] = packetData;
            }

            // Read the length of the addon resend data
            resendLength = packet.ReadUShort();

            while (resendLength-- > 0) {
                // Read the sequence number of the packet it was lost from
                var seq = packet.ReadUShort();

                // Create a new dictionary for the addon data and read the data from the packet into it
                var addonDataDict = new Dictionary<byte, AddonPacketData>();
                ReadAddonDataDict(packet, addonDataDict);

                // Input the dictionary into the resend dictionary keyed by its sequence number
                _resendAddonPacketData[seq] = addonDataDict;
            }
        } catch (Exception e) {
            WarnUnreadable("its resent data", e);
            OnlyHeadersRead = true;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Puts on each reliable entity update in the given data the number of the packet it was first sent in (see
    /// <see cref="ReliableEntityUpdate.ReceivedSequence"/>).
    /// </summary>
    /// <param name="packetData">The data.</param>
    /// <param name="sequence">The number: of this packet for its own data, of the lost one for data sent again.</param>
    private static void StampReliableEntityUpdates(Dictionary<TPacketId, IPacketData> packetData, ushort sequence) {
        foreach (var data in packetData.Values) {
            if (data is not PacketDataCollection<ReliableEntityUpdate> updates) {
                continue;
            }

            foreach (var update in updates.DataInstances) {
                ((ReliableEntityUpdate) update).ReceivedSequence = sequence;
            }
        }
    }

    /// <summary>
    /// Set the reliable packet data contained in the lost packet as resend data in this one.
    /// </summary>
    /// <param name="lostPacket">The update packet instance that was lost.</param>
    public void SetLostReliableData(UpdatePacket<TPacketId> lostPacket) {
        // Retrieve the lost packet data
        var lostPacketData = lostPacket.GetPacketData();

        // Finally, put the packet data dictionary in the resend dictionary keyed by its sequence number
        var reliablePacketData = CopyReliableDataDict(
            lostPacketData,
            t => NormalPacketData.ContainsKey(t)
        );

        foreach (var (packetId, lostData) in lostPacketData) {
            if (lostData is not PacketDataCollection<ReliableEntityUpdate> lostEntityUpdates
                || !NormalPacketData.TryGetValue(packetId, out var currentData)
                || currentData is not PacketDataCollection<ReliableEntityUpdate> currentEntityUpdates) {
                continue;
            }

            var resendEntityUpdates = new PacketDataCollection<ReliableEntityUpdate>();
            var currentHostFsmDataByEntity = new Dictionary<ushort, IReadOnlyDictionary<byte, EntityHostFsmData>>();

            foreach (var currentEntityUpdate in currentEntityUpdates.DataInstances) {
                var reliableEntityUpdate = (ReliableEntityUpdate) currentEntityUpdate;
                if (reliableEntityUpdate.UpdateTypes.Contains(EntityUpdateType.HostFsm)) {
                    currentHostFsmDataByEntity[reliableEntityUpdate.Id] = reliableEntityUpdate.HostFsmData;
                }
            }

            foreach (var lostEntityUpdate in lostEntityUpdates.DataInstances) {
                var reliableEntityUpdate = (ReliableEntityUpdate) lostEntityUpdate;
                if (!currentHostFsmDataByEntity.TryGetValue(reliableEntityUpdate.Id, out var newerHostFsmData)
                    || !reliableEntityUpdate.UpdateTypes.Contains(EntityUpdateType.HostFsm)) {
                    resendEntityUpdates.DataInstances.Add(reliableEntityUpdate);
                    continue;
                }

                var reliableCopy = reliableEntityUpdate.CloneWithoutSupersededHostFsm(newerHostFsmData);
                if (reliableCopy != null) {
                    resendEntityUpdates.DataInstances.Add(reliableCopy);
                }
            }

            if (resendEntityUpdates.DataInstances.Count > 0) {
                reliablePacketData[packetId] = resendEntityUpdates;
            } else {
                reliablePacketData.Remove(packetId);
            }
        }

        _resendPacketData[lostPacket.Sequence] = reliablePacketData;

        // Retrieve the lost addon data
        var lostAddonData = lostPacket.GetAddonPacketData();
        // Create a new dictionary of addon data in which we store all reliable data from the lost packet
        // for all addons in the dictionary
        var toResendAddonData = new Dictionary<byte, AddonPacketData>();

        foreach (var idLostDataPair in lostAddonData) {
            var addonId = idLostDataPair.Key;
            var addonPacketData = idLostDataPair.Value;

            // Construct a new AddonPacketData instance that holds the reliable data only
            var newAddonPacketData = addonPacketData.GetEmptyCopy();
            newAddonPacketData.PacketData = CopyReliableDataDict(
                addonPacketData.PacketData,
                rawPacketId =>
                    AddonPacketData.TryGetValue(addonId, out var existingAddonData)
                    && existingAddonData.PacketData.ContainsKey(rawPacketId)
            );

            toResendAddonData[addonId] = newAddonPacketData;
        }

        // Put the addon data dictionary in the resend dictionary keyed by its sequence number
        _resendAddonPacketData[lostPacket.Sequence] = toResendAddonData;
    }

    /// <summary>
    /// Copy all reliable data in the given dictionary of lost packet data into a new dictionary.
    /// </summary>
    /// <param name="lostPacketData">The dictionary containing all packet data from a lost packet.</param>
    /// <param name="reliabilityCheck">Function that checks whether for a given key there is newer data
    /// available. If it returns true, lost data will be dropped.</param>
    /// <typeparam name="TKey">The key parameter of the dictionaries to copy.</typeparam>
    /// <returns>A new dictionary containing only the reliable data.</returns>
    private Dictionary<TKey, IPacketData> CopyReliableDataDict<TKey>(
        Dictionary<TKey, IPacketData> lostPacketData,
        Func<TKey, bool> reliabilityCheck
    ) {
        // Create a new dictionary of packet data in which we store all reliable data
        var reliablePacketData = new Dictionary<TKey, IPacketData>();

        foreach (var keyDataPair in lostPacketData) {
            var key = keyDataPair.Key;
            var data = keyDataPair.Value;

            // Check if the packet data is supposed to be reliable
            if (!data.IsReliable) {
                continue;
            }

            // Check whether we can drop it since a newer version of that data already exists
            if (data.DropReliableDataIfNewerExists && reliabilityCheck(key)) {
                continue;
            }

            reliablePacketData[key] = data;
        }

        return reliablePacketData;
    }

    /// <inheritdoc />
    protected override void CacheAllPacketData() {
        base.CacheAllPacketData();

        void AddResendData<TKey>(
            Dictionary<TKey, IPacketData> dataDict,
            Dictionary<TKey, IPacketData> cachedData
        ) {
            foreach (var packetIdDataPair in dataDict) {
                // Get the ID and the data itself
                var packetId = packetIdDataPair.Key;
                var packetData = packetIdDataPair.Value;

                // Check whether for this ID there already exists data
                if (cachedData.TryGetValue(packetId, out var existingPacketData)) {
                    // If the existing data is a PacketDataCollection, we can simply add all the data instance to it
                    // If not, we simply discard the resent data, since it is older
                    if (existingPacketData is RawPacketDataCollection existingPacketDataCollection
                        && packetData is RawPacketDataCollection packetDataCollection) {
                        existingPacketDataCollection.DataInstances.AddRange(packetDataCollection.DataInstances);
                    }
                } else {
                    // If no data exists for this ID, we can simply set the resent data for that key
                    cachedData[packetId] = packetData;
                }
            }
        }

        // Iteratively add the resent packet data, but make sure to merge it with existing data
        foreach (var resentPacketData in _resendPacketData.Values) {
            AddResendData(resentPacketData, CachedAllPacketData!);
        }

        // Iteratively add the resent addon data, but make sure to merge it with existing data
        foreach (var resentAddonData in _resendAddonPacketData.Values) {
            foreach (var addonIdDataPair in resentAddonData) {
                var addonId = addonIdDataPair.Key;
                var addonPacketData = addonIdDataPair.Value;

                if (CachedAllAddonData!.TryGetValue(addonId, out var existingAddonPacketData)) {
                    AddResendData(addonPacketData.PacketData, existingAddonPacketData.PacketData);
                } else {
                    CachedAllAddonData[addonId] = addonPacketData;
                }
            }
        }

        IsAllPacketDataCached = true;
    }

    /// <summary>
    /// Drops resend data that is duplicate, i.e. that we already received in an earlier packet.
    /// Optimized to minimize iterations and avoid allocations when possible.
    /// </summary>
    /// <param name="receivedSequenceNumbers">A list containing sequence numbers that were already
    /// received. Uses List for efficient linear iteration.</param>
    public void DropDuplicateResendData(List<ushort> receivedSequenceNumbers) {
        // Handle packet data duplicates
        if (_resendPacketData.Count > 0) {
            foreach (var seq in receivedSequenceNumbers) {
                _resendPacketData.Remove(seq);
            }
        }

        // Handle addon data duplicates
        if (_resendAddonPacketData.Count > 0) {
            foreach (var seq in receivedSequenceNumbers) {
                _resendAddonPacketData.Remove(seq);
            }
        }
    }
}

/// <summary>
/// What the numbering of the packets with reliable data says in an update packet (see
/// <see cref="ReliableNumbering"/>).
/// </summary>
[Flags]
internal enum NumberingFlags : byte {
    /// <summary>
    /// Nothing: a packet that did not go through the numbering, like a slice, or one on a transport that this mod keeps
    /// reliable itself.
    /// </summary>
    None = 0,

    /// <summary>
    /// The packet carries reliable data, and <see cref="UpdatePacket{TPacketId}.ReliableNumber"/> numbers it.
    /// </summary>
    Numbered = 1,

    /// <summary>
    /// The packet went through the numbering: <see cref="UpdatePacket{TPacketId}.ReliableAck"/> names the last packet
    /// with reliable data that the sender took in from the other side, in order (0 before any, as they are numbered
    /// from 1), and in a packet without reliable data, <see cref="UpdatePacket{TPacketId}.ReliableNumber"/> names the
    /// last one that the sender sent.
    /// </summary>
    TookIn = 2,

    /// <summary>
    /// The sender asks for the packets with reliable data after <see cref="UpdatePacket{TPacketId}.ReliableAck"/>
    /// again: a later one arrived before them, or the other side said a while ago that it sent more than the sender
    /// took in.
    /// </summary>
    AsksAgain = 4
}

/// <summary>
/// The numbering of the packets that carry reliable data, on a transport that keeps what it carries reliable itself,
/// like Steam's relay, where this mod does no acknowledging and sending again of its own.
///
/// Such a transport still loses what it had not delivered when its link is opened anew
/// (UpdateManager.RenewStalledLink): on 10-17 the link went quiet for 21 s and was opened anew, and about 30 kB of
/// reliable data that the host had sent in that time never arrived, the death of the host's player among it, which
/// never showed on the other player's screen (USER "三令五申死掉要走可靠通道了", "你该修的是走可靠通道那一整批，
/// 断线重连该怎么办"). So each side numbers the packets it sends with reliable data, keeps them until the other side
/// says it took them in, sends them again when they may be lost, and takes in what arrives in the order it was sent,
/// each one once (UpdateManager.TakeInOrder). Every packet says how far the numbering of its sender went, so that a
/// side that lost the last ones before nothing more with reliable data came still asks for them.
/// </summary>
internal static class ReliableNumbering {
    /// <summary>
    /// Writes the numbering of reliable data into a packet as it is sent, where
    /// <see cref="UpdatePacket{TPacketId}.ReliableHeaderAt"/> says it goes.
    /// </summary>
    /// <param name="raw">The packet as it is sent.</param>
    /// <param name="at">Where the numbering goes in it.</param>
    /// <param name="flags">What the numbering says.</param>
    /// <param name="number">The number of the packet among those with reliable data.</param>
    /// <param name="ack">The number of the last packet with reliable data taken in from the other side.</param>
    public static void Stamp(byte[] raw, int at, NumberingFlags flags, ushort number, ushort ack) {
        raw[at] = (byte) flags;
        raw[at + 1] = (byte) number;
        raw[at + 2] = (byte) (number >> 8);
        raw[at + 3] = (byte) ack;
        raw[at + 4] = (byte) (ack >> 8);
    }
}
