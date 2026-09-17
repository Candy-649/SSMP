using System;
using System.Diagnostics;
using System.Threading;
using SSMP.Game;
using SSMP.Logging;
using SSMP.Networking.Transport.Common;
using Steamworks;

namespace SSMP.Networking.Transport.SteamRelay;

/// <summary>
/// The client half of a transport that carries the game over Steam's relay network.
///
/// Unlike the older Steam transport, this one also listens for the session request and the session failure of its
/// own side. The old one left both to the server, so a player who could not be reached learned nothing at all and
/// their log said nothing either - which is what made a failed game impossible to tell apart from a quiet one.
/// </summary>
internal sealed class SteamRelayTransport : IReliableTransport {
    /// <summary>
    /// Polling interval in milliseconds, matching the old transport's ~58Hz.
    /// </summary>
    private const double PollIntervalMS = 17.2;

    /// <summary>
    /// How far behind the poll schedule may fall before it is simply reset.
    /// </summary>
    private const long MaxLagMS = 500;

    /// <inheritdoc />
    public event Action<byte[], int>? DataReceivedEvent;

    /// <inheritdoc />
    public bool RequiresReliability => false;

    /// <inheritdoc />
    public bool RequiresSequencing => false;

    /// <inheritdoc />
    public int? Ping => _isLoopback ? 0 : SteamRelayMessaging.PingTo(_remoteSteamId);

    /// <inheritdoc />
    public int MaxPacketSize => SteamRelayMessaging.MaxPacketSize;

    /// <summary>
    /// The Steam ID of the host this client is connected to.
    /// </summary>
    private ulong _remoteSteamId;

    /// <summary>
    /// Whether the host is this same player, in which case nothing goes over the network.
    /// </summary>
    private bool _isLoopback;

    /// <summary>
    /// Whether this transport is currently connected.
    /// </summary>
    private volatile bool _isConnected;

    /// <summary>
    /// Reused buffer of message pointers for draining the channel.
    /// </summary>
    private readonly IntPtr[] _messageBuffer = new IntPtr[SteamRelayMessaging.ReceiveBatchSize];

    private Callback<SteamNetworkingMessagesSessionRequest_t>? _sessionRequestCallback;
    private Callback<SteamNetworkingMessagesSessionFailed_t>? _sessionFailedCallback;

    private CancellationTokenSource? _receiveTokenSource;
    private Thread? _receiveThread;
    private SteamRelayLoopbackChannel? _loopbackChannel;

    /// <inheritdoc />
    public void Connect(string address, int port) {
        if (!SteamManager.IsInitialized) {
            throw new InvalidOperationException("Cannot connect over the Steam relay: Steam is not running");
        }

        if (!ulong.TryParse(address, out var steamId)) {
            throw new InvalidOperationException($"Cannot connect over the Steam relay: '{address}' is not a Steam ID");
        }

        _remoteSteamId = steamId;
        _isLoopback = steamId == SteamUser.GetSteamID().m_SteamID;
        _isConnected = true;

        Logger.Info($"Steam relay: connecting to {steamId}");

        // Warms the relay network if it was not warmed already, so the first message does not pay for it
        SteamNetworkingUtils.InitRelayNetworkAccess();

        if (_isLoopback) {
            Logger.Info("Steam relay: the host is this player, using the loopback channel");
            _loopbackChannel = SteamRelayLoopbackChannel.GetOrCreate();
            _loopbackChannel.RegisterClient(this);
        } else {
            // Both sides listen now. The host answers the first message with its own, and a session that fails is
            // reported here rather than only on the other machine.
            _sessionRequestCallback =
                Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
            _sessionFailedCallback =
                Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
        }

        _receiveTokenSource = new CancellationTokenSource();
        _receiveThread = new Thread(ReceiveLoop) {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "Steam Relay Receive"
        };
        _receiveThread.Start();
    }

    /// <inheritdoc />
    public void Send(byte[] buffer, int offset, int length) {
        SendInternal(buffer, offset, length, reliable: false);
    }

    /// <inheritdoc />
    public void SendReliable(byte[] buffer, int offset, int length) {
        SendInternal(buffer, offset, length, reliable: true);
    }

    private void SendInternal(byte[] buffer, int offset, int length, bool reliable) {
        if (!_isConnected || !SteamManager.IsInitialized) {
            throw new InvalidOperationException("Cannot send: not connected, or Steam is not running");
        }

        if (_isLoopback) {
            _loopbackChannel?.SendToServer(buffer, offset, length);
            return;
        }

        SteamRelayMessaging.Send(
            _remoteSteamId, buffer, offset, length, SteamRelayMessaging.ClientToServerChannel, reliable
        );
    }

    /// <summary>
    /// Accepts the host's side of the session, so that what it sends back can arrive.
    /// </summary>
    private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t request) {
        var identity = request.m_identityRemote;
        var steamId = identity.GetSteamID64();

        if (!_isConnected || steamId != _remoteSteamId) {
            Logger.Info($"Steam relay: ignoring a session request from {steamId}, which is not the host");
            return;
        }

        if (SteamNetworkingMessages.AcceptSessionWithUser(ref identity)) {
            Logger.Info($"Steam relay: accepted the session with the host {steamId}");
        } else {
            Logger.Warn($"Steam relay: could not accept the session with the host {steamId}");
        }
    }

    /// <summary>
    /// Says why the session with the host failed. Nothing reported this before.
    /// </summary>
    private void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t failure) {
        Logger.Warn($"Steam relay: the session with the host failed: {SteamRelayMessaging.DescribeFailure(failure.m_info)}");
    }

    /// <summary>
    /// Drains anything the host has sent.
    /// </summary>
    private void ReceivePackets() {
        if (!_isConnected || _isLoopback) return;

        var handler = DataReceivedEvent;
        if (handler == null) return;

        SteamRelayMessaging.Drain(SteamRelayMessaging.ServerToClientChannel, _messageBuffer, (sender, data, size) => {
            if (sender != _remoteSteamId) {
                Logger.Warn($"Steam relay: ignoring a message from {sender}, which is not the host");
                return;
            }

            handler(data, size);
        });
    }

    /// <summary>
    /// Polls for incoming messages on a steady schedule, the same way the older transport does.
    /// </summary>
    private void ReceiveLoop() {
        var tokenSource = _receiveTokenSource;
        if (tokenSource == null) return;

        var cancellationToken = tokenSource.Token;
        var stopwatch = Stopwatch.StartNew();
        var nextPollTime = stopwatch.ElapsedMilliseconds;
        const long pollInterval = (long)PollIntervalMS;

        using var waitHandle = new ManualResetEventSlim(false);

        while (_isConnected) {
            try {
                if (cancellationToken.IsCancellationRequested) break;

                if (!SteamManager.IsInitialized) {
                    Logger.Info("Steam relay: Steam shut down, leaving the receive loop");
                    break;
                }

                var currentTime = stopwatch.ElapsedMilliseconds;
                if (currentTime > nextPollTime + MaxLagMS) {
                    nextPollTime = currentTime;
                }

                var waitTime = nextPollTime - currentTime;
                if (waitTime > 0) {
                    try {
                        waitHandle.Wait((int)waitTime, cancellationToken);
                    } catch (OperationCanceledException) {
                        break;
                    }
                }

                ReceivePackets();
                nextPollTime += pollInterval;
            } catch (InvalidOperationException e) when (e.Message.Contains("Steamworks is not initialized")) {
                Logger.Info("Steam relay: Steamworks shut down while receiving, leaving the loop");
                break;
            } catch (ThreadAbortException) {
                Logger.Info("Steam relay: receive thread aborted, leaving the loop");
                break;
            } catch (Exception e) {
                Logger.Error($"Steam relay: error in the receive loop: {e}");
                nextPollTime = stopwatch.ElapsedMilliseconds + pollInterval;
            }
        }

        Logger.Info("Steam relay: receive loop exited cleanly");
    }

    /// <summary>
    /// Receives a packet that never left the machine.
    /// </summary>
    public void ReceiveLoopbackPacket(byte[] data, int length) {
        if (!_isConnected) return;

        DataReceivedEvent?.Invoke(data, length);
    }

    /// <inheritdoc />
    public void Disconnect() {
        if (!_isConnected) return;

        _isConnected = false;
        _receiveTokenSource?.Cancel();

        if (_loopbackChannel != null) {
            _loopbackChannel.UnregisterClient();
            SteamRelayLoopbackChannel.ReleaseIfEmpty();
            _loopbackChannel = null;
        }

        Logger.Info($"Steam relay: disconnecting from {_remoteSteamId}");

        if (SteamManager.IsInitialized && !_isLoopback) {
            var identity = SteamRelayMessaging.IdentityOf(_remoteSteamId);
            SteamNetworkingMessages.CloseSessionWithUser(ref identity);
        }

        _sessionRequestCallback?.Dispose();
        _sessionRequestCallback = null;

        _sessionFailedCallback?.Dispose();
        _sessionFailedCallback = null;

        if (_receiveThread != null) {
            if (!_receiveThread.Join(5000)) {
                Logger.Warn("Steam relay: receive thread did not finish within five seconds");
            }

            _receiveThread = null;
        }

        _remoteSteamId = 0;

        _receiveTokenSource?.Dispose();
        _receiveTokenSource = null;
    }
}
