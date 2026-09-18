using System;
using System.Runtime.InteropServices;
using SSMP.Logging;
using Steamworks;

namespace SSMP.Networking.Transport.SteamRelay;

/// <summary>
/// The parts of Steam's relayed messaging that both ends of this transport need in the same shape: sending a buffer
/// to a player, draining a channel, and saying in words why a session failed.
///
/// Steam's relayed messaging carries traffic over Valve's own relay network rather than trying to reach the other
/// player directly. That is the whole reason this transport exists: a direct route between two players on different
/// continents can be established and still fail to carry anything, which is what the older transport ran into.
/// </summary>
internal static class SteamRelayMessaging {
    /// <summary>
    /// Channel the client sends to the server on.
    /// </summary>
    public const int ClientToServerChannel = 0;

    /// <summary>
    /// Channel the server sends to the client on.
    /// </summary>
    public const int ServerToClientChannel = 1;

    /// <summary>
    /// Largest packet this transport carries. Steam's relayed messaging allows far more, but the rest of the mod
    /// splits its data for the old transport's limit and there is no reason to change that here.
    /// </summary>
    public const int MaxPacketSize = 1200;

    /// <summary>
    /// How many messages one drain of a channel takes at most.
    /// </summary>
    public const int ReceiveBatchSize = 64;

    /// <summary>
    /// Flags for an unreliable send. The session is rebuilt by Steam if it broke, rather than the send being lost -
    /// a session that dies mid-game is exactly the failure this transport is meant to survive.
    /// </summary>
    // Spelled out in full because this mod has a Constants of its own, which an unqualified name finds first
    public const int UnreliableFlags =
        Steamworks.Constants.k_nSteamNetworkingSend_UnreliableNoDelay |
        Steamworks.Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;

    /// <summary>
    /// Flags for a reliable send.
    /// </summary>
    public const int ReliableFlags =
        Steamworks.Constants.k_nSteamNetworkingSend_Reliable |
        Steamworks.Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;

    /// <summary>
    /// Builds an identity for a player from their Steam ID.
    /// </summary>
    public static SteamNetworkingIdentity IdentityOf(ulong steamId) {
        var identity = new SteamNetworkingIdentity();
        identity.SetSteamID64(steamId);
        return identity;
    }

    /// <summary>
    /// The round trip to a player in milliseconds, as Valve's own relay network measures it, or null while there is
    /// no session to measure.
    ///
    /// This transport carries none of the sequence numbers that the rest of the mod times a round trip from, so
    /// asking the relay is the only honest answer available here. Timing the gap between a send and the next arrival
    /// of anything, as this used to do instead, reports the send interval - a small number that stays the same
    /// whether the two players are in one room or on different continents.
    /// </summary>
    /// <param name="steamId">The player to measure the round trip to.</param>
    /// <returns>The round trip in milliseconds, or null if the relay has no connected session to report on.</returns>
    public static int? PingTo(ulong steamId) {
        if (steamId == 0) {
            return null;
        }

        // Hosting for yourself never leaves the machine, and Steam has no session with you to ask about
        if (steamId == SteamUser.GetSteamID().m_SteamID) {
            return 0;
        }

        var identity = IdentityOf(steamId);
        var state = SteamNetworkingMessages.GetSessionConnectionInfo(ref identity, out _, out var status);
        if (state != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected) {
            return null;
        }

        return status.m_nPing >= 0 ? status.m_nPing : null;
    }

    /// <summary>
    /// Whether the relay still has a link to a player, or is building one, as opposed to having closed it or given
    /// up on it.
    ///
    /// Deliberately not <see cref="PingTo"/>. That answers "how far away are they", and says nothing at all in two
    /// cases that have nothing to do with each other: there is no session, and there is a session whose round trip
    /// the relay has not measured yet. Sending carries <c>AutoRestartBrokenSession</c>, so a session that breaks is
    /// rebuilt by the next thing sent - and while that is happening there is a session, being connected, with no
    /// round trip to report. Reading that as "they are gone" is what ended a game that was seconds from carrying on.
    ///
    /// Having no session at all counts as up for the same reason: it means the relay has not started rebuilding
    /// one yet, and the send loop is about to make it. What counts as down is the relay saying it is over - the
    /// other side closed it, or this machine could not make it work - which is answered at once, so a player whose
    /// game really has gone is still noticed in seconds rather than in half a minute.
    /// </summary>
    /// <param name="steamId">The player to ask about.</param>
    /// <returns>Whether there is a link, or the making of one.</returns>
    public static bool SessionUp(ulong steamId) {
        if (steamId == 0) {
            return false;
        }

        // Hosting for yourself never leaves the machine
        if (steamId == SteamUser.GetSteamID().m_SteamID) {
            return true;
        }

        var identity = IdentityOf(steamId);
        var state = SteamNetworkingMessages.GetSessionConnectionInfo(ref identity, out _, out _);

        return state is ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None
            or ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting
            or ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_FindingRoute
            or ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected;
    }

    /// <summary>
    /// Sends a buffer to a player over the relay network.
    /// </summary>
    /// <returns>Whether Steam accepted the message for sending.</returns>
    public static bool Send(ulong steamId, byte[] buffer, int offset, int length, int channel, bool reliable) {
        if (length <= 0 || length > MaxPacketSize) {
            Logger.Warn($"Steam relay: refusing to send {length} bytes, which is outside what this transport carries");
            return false;
        }

        // Steam takes unmanaged memory, so the bytes are copied out and freed again around the call. The buffer is
        // small and a send is not frequent enough for this to be worth pooling.
        var unmanaged = Marshal.AllocHGlobal(length);
        try {
            Marshal.Copy(buffer, offset, unmanaged, length);
            var identity = IdentityOf(steamId);
            var result = SteamNetworkingMessages.SendMessageToUser(
                ref identity,
                unmanaged,
                (uint)length,
                reliable ? ReliableFlags : UnreliableFlags,
                channel
            );

            if (result == EResult.k_EResultOK) {
                return true;
            }

            Logger.Warn($"Steam relay: could not send to {steamId}: {result}");
            return false;
        } finally {
            Marshal.FreeHGlobal(unmanaged);
        }
    }

    /// <summary>
    /// Takes everything waiting on a channel and hands each message to <paramref name="handle"/> as the sender's
    /// Steam ID and a copy of its bytes.
    /// </summary>
    /// <returns>How many messages were handled.</returns>
    public static int Drain(int channel, IntPtr[] messageBuffer, Action<ulong, byte[], int> handle) {
        var count = SteamNetworkingMessages.ReceiveMessagesOnChannel(channel, messageBuffer, messageBuffer.Length);
        if (count <= 0) {
            return 0;
        }

        for (var i = 0; i < count; i++) {
            var pointer = messageBuffer[i];
            if (pointer == IntPtr.Zero) {
                continue;
            }

            try {
                var message = SteamNetworkingMessage_t.FromIntPtr(pointer);
                var size = message.m_cbSize;
                if (size > 0 && message.m_pData != IntPtr.Zero) {
                    var data = new byte[size];
                    Marshal.Copy(message.m_pData, data, 0, size);
                    handle(message.m_identityPeer.GetSteamID64(), data, size);
                }
            } catch (Exception e) {
                Logger.Error($"Steam relay: error handling a received message: {e}");
            } finally {
                // Always, or Steam's own buffers are never given back
                SteamNetworkingMessage_t.Release(pointer);
            }
        }

        return count;
    }

    /// <summary>
    /// Describes a failed session in terms that say what to do about it, which is what the old transport's bare
    /// "timed out" never did.
    /// </summary>
    public static string DescribeFailure(SteamNetConnectionInfo_t info) {
        var reason = (ESteamNetConnectionEnd)info.m_eEndReason;
        var detail = reason switch {
            ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_Misc_NoRelaySessionsToClient =>
                "no relay could reach the other player",
            ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_Local_ManyRelayConnectivity =>
                "this machine could not reach Steam's relays",
            ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_Misc_SteamConnectivity =>
                "this machine lost its connection to Steam",
            ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_Misc_P2P_NAT_Firewall =>
                "a firewall or router on one side refused the traffic",
            ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_Remote_Timeout or
                ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_Misc_Timeout =>
                "the other player stopped answering",
            ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_Remote_BadProtocolVersion =>
                "the other player's game speaks a different version",
            _ => "no further detail"
        };

        return $"{reason} ({detail}), state {info.m_eState}, relay {info.m_idPOPRelay}";
    }
}
