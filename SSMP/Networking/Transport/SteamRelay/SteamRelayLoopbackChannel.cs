using System;
using SSMP.Logging;

namespace SSMP.Networking.Transport.SteamRelay;

/// <summary>
/// Carries packets between the local client and the local server when one player hosts and plays at the same time.
/// Steam will not open a session with yourself, so the host's own connection never touches the network at all.
///
/// This is a second copy of the same idea that <see cref="SteamP2P.SteamLoopbackChannel"/> implements, because that
/// one names the old transport's types in its fields. Leaving it alone keeps the old transport working exactly as it
/// did, which matters while both are selectable.
/// </summary>
internal class SteamRelayLoopbackChannel {
    /// <summary>
    /// Lock for thread-safe singleton access.
    /// </summary>
    private static readonly object Lock = new();

    /// <summary>
    /// Singleton instance, created on first use.
    /// </summary>
    private static SteamRelayLoopbackChannel? _instance;

    /// <summary>
    /// The server transport for looping communication.
    /// </summary>
    private SteamRelayTransportServer? _server;

    /// <summary>
    /// The client transport for looping communication.
    /// </summary>
    private SteamRelayTransport? _client;

    private SteamRelayLoopbackChannel() {
    }

    /// <summary>
    /// Gets or creates the singleton loopback channel instance.
    /// </summary>
    public static SteamRelayLoopbackChannel GetOrCreate() {
        lock (Lock) {
            return _instance ??= new SteamRelayLoopbackChannel();
        }
    }

    /// <summary>
    /// Releases the singleton instance once neither side is registered any more.
    /// </summary>
    public static void ReleaseIfEmpty() {
        lock (Lock) {
            if (_instance?._server == null && _instance?._client == null) {
                _instance = null;
            }
        }
    }

    /// <summary>
    /// Registers the server instance to receive loopback packets.
    /// </summary>
    public void RegisterServer(SteamRelayTransportServer server) {
        lock (Lock) {
            _server = server;
        }
    }

    /// <summary>
    /// Unregisters the server instance.
    /// </summary>
    public void UnregisterServer() {
        lock (Lock) {
            _server = null;
        }
    }

    /// <summary>
    /// Registers the client instance to receive loopback packets.
    /// </summary>
    public void RegisterClient(SteamRelayTransport client) {
        lock (Lock) {
            _client = client;
        }
    }

    /// <summary>
    /// Unregisters the client instance.
    /// </summary>
    public void UnregisterClient() {
        lock (Lock) {
            _client = null;
        }
    }

    /// <summary>
    /// Sends a packet from the local client to the local server.
    /// </summary>
    public void SendToServer(byte[] data, int offset, int length) {
        SteamRelayTransportServer? server;
        lock (Lock) {
            server = _server;
        }

        if (server == null) {
            Logger.Debug("Steam relay loopback: server not registered, dropping packet");
            return;
        }

        // An exactly sized array, because whatever reads this assumes the whole array is the packet
        var copy = new byte[length];
        try {
            Array.Copy(data, offset, copy, 0, length);
            server.ReceiveLoopbackPacket(copy, length);
        } catch (Exception e) {
            Logger.Error($"Steam relay loopback: error sending to server: {e}");
        }
    }

    /// <summary>
    /// Sends a packet from the local server to the local client.
    /// </summary>
    public void SendToClient(byte[] data, int offset, int length) {
        SteamRelayTransport? client;
        lock (Lock) {
            client = _client;
        }

        if (client == null) {
            Logger.Debug("Steam relay loopback: client not registered, dropping packet");
            return;
        }

        var copy = new byte[length];
        try {
            Array.Copy(data, offset, copy, 0, length);
            client.ReceiveLoopbackPacket(copy, length);
        } catch (Exception e) {
            Logger.Error($"Steam relay loopback: error sending to client: {e}");
        }
    }
}
