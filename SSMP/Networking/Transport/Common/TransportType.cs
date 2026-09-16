namespace SSMP.Networking.Transport.Common;

/// <summary>
/// Enum representing the type of transport to use.
/// </summary>
public enum TransportType {
    /// <summary>
    /// UDP transport (Direct Connect).
    /// </summary>
    Udp,

    /// <summary>
    /// Steam P2P transport (Lobby).
    /// </summary>
    Steam,

    /// <summary>
    /// UDP Hole Punch transport (NAT traversal).
    /// </summary>
    HolePunch,

    /// <summary>
    /// Steam transport that carries the game over Valve's relay network instead of trying to reach the other player
    /// directly. Slower in the best case, but it holds up between players far apart from each other, where a direct
    /// route can be established and still carry nothing.
    /// </summary>
    SteamRelay
}
