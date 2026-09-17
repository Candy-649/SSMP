using System;
using System.Net;
using SSMP.Game;
using SSMP.Logging;
using SSMP.Networking.Transport.Common;
using Steamworks;

namespace SSMP.Networking.Transport.SteamRelay;

/// <summary>
/// One player as the server sees them, over Steam's relayed messaging.
///
/// This says <see cref="IReliableTransportClient"/> and not just <see cref="IEncryptedTransportClient"/> because
/// that is what decides whether a reliable send stays reliable: the sender picks the reliable path by testing for
/// this interface, and anything without it is quietly sent unreliably instead.
/// </summary>
internal class SteamRelayTransportClient : IReliableTransportClient {
    /// <summary>
    /// The Steam ID of the player.
    /// </summary>
    public ulong SteamId { get; }

    /// <inheritdoc />
    public string ToDisplayString() => "SteamRelay";

    /// <inheritdoc />
    public string GetUniqueIdentifier() => SteamId.ToString();

    /// <inheritdoc />
    public IPEndPoint? EndPoint => null; // Steam identifies the player, so there is nothing to throttle by address

    /// <inheritdoc />
    public bool RequiresReliability => false;

    /// <inheritdoc />
    public bool RequiresSequencing => false;

    /// <inheritdoc />
    public int? Ping => SteamRelayMessaging.PingTo(SteamId);

    /// <inheritdoc />
    public event Action<byte[], int>? DataReceivedEvent;

    public SteamRelayTransportClient(ulong steamId) {
        SteamId = steamId;
    }

    /// <inheritdoc/>
    public void Send(byte[] buffer, int offset, int length) {
        SendInternal(buffer, offset, length, reliable: false);
    }

    /// <inheritdoc/>
    public void SendReliable(byte[] buffer, int offset, int length) {
        SendInternal(buffer, offset, length, reliable: true);
    }

    private void SendInternal(byte[] buffer, int offset, int length, bool reliable) {
        if (!SteamManager.IsInitialized) {
            Logger.Warn($"Steam relay: cannot send to {SteamId}, Steam is not running");
            return;
        }

        // The host playing their own game never leaves the machine
        if (SteamId == SteamUser.GetSteamID().m_SteamID) {
            SteamRelayLoopbackChannel.GetOrCreate().SendToClient(buffer, offset, length);
            return;
        }

        SteamRelayMessaging.Send(
            SteamId, buffer, offset, length, SteamRelayMessaging.ServerToClientChannel, reliable
        );
    }

    /// <summary>
    /// Hands data that arrived from this player to whoever is listening.
    /// </summary>
    internal void RaiseDataReceived(byte[] data, int length) {
        DataReceivedEvent?.Invoke(data, length);
    }
}
