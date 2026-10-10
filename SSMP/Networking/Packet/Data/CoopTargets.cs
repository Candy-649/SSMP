namespace SSMP.Networking.Packet.Data;

/// <summary>
/// Where an update of a co-op save goes, besides the ID of one player: <see cref="CoopSaveUpdate"/>,
/// <see cref="CoopHitUpdate"/> and <see cref="CoopCheckUpdate"/> go through the server to the player they name.
/// </summary>
internal static class CoopTargets {
    /// <summary>
    /// The target of an update that goes to every other player on the server, which the server copies to each of them.
    /// A save shared by more than two players tells all of its other players the same thing with one update rather than
    /// one per player. No player ever gets this ID (see NetServerClient.GetId).
    /// </summary>
    public const ushort Everyone = ushort.MaxValue;
}
