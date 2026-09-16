using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using SSMP.Game;
using SSMP.Logging;
using SSMP.Networking.Transport.Common;
using Steamworks;

namespace SSMP.Networking.Transport.SteamRelay;

/// <summary>
/// The server half of a transport that carries the game over Steam's relay network.
/// </summary>
internal sealed class SteamRelayTransportServer : IEncryptedTransportServer {
    /// <summary>
    /// Polling interval in milliseconds, matching the old transport's ~58Hz.
    /// </summary>
    private const double PollIntervalMS = 17.2;

    /// <summary>
    /// How far behind the poll schedule may fall before it is simply reset.
    /// </summary>
    private const long MaxLagMS = 500;

    /// <inheritdoc />
    public event Action<IEncryptedTransportClient>? ClientConnectedEvent;

    /// <summary>
    /// Connected players by Steam ID.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, SteamRelayTransportClient> _clients =
        new(Environment.ProcessorCount, 8);

    /// <summary>
    /// Reused buffer of message pointers for draining the channel.
    /// </summary>
    private readonly IntPtr[] _messageBuffer = new IntPtr[SteamRelayMessaging.ReceiveBatchSize];

    private volatile bool _isRunning;

    private Callback<SteamNetworkingMessagesSessionRequest_t>? _sessionRequestCallback;
    private Callback<SteamNetworkingMessagesSessionFailed_t>? _sessionFailedCallback;

    private CancellationTokenSource? _receiveTokenSource;
    private Thread? _receiveThread;
    private SteamRelayLoopbackChannel? _loopbackChannel;

    /// <inheritdoc />
    public void Start(int port) {
        if (!SteamManager.IsInitialized) {
            throw new InvalidOperationException("Cannot host over the Steam relay: Steam is not running");
        }

        if (_isRunning) {
            Logger.Warn("Steam relay: server already running");
            return;
        }

        _isRunning = true;

        // Warms the relay network before anyone tries to join
        SteamNetworkingUtils.InitRelayNetworkAccess();

        _sessionRequestCallback = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
        _sessionFailedCallback = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);

        _loopbackChannel = SteamRelayLoopbackChannel.GetOrCreate();
        _loopbackChannel.RegisterServer(this);

        Logger.Info("Steam relay: server started, waiting for players");

        _receiveTokenSource = new CancellationTokenSource();
        _receiveThread = new Thread(ReceiveLoop) {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "Steam Relay Server Receive"
        };
        _receiveThread.Start();
    }

    /// <inheritdoc />
    public void Stop() {
        if (!_isRunning) return;

        Logger.Info("Steam relay: stopping server");

        _isRunning = false;
        _receiveTokenSource?.Cancel();

        if (_receiveThread != null) {
            if (!_receiveThread.Join(5000)) {
                Logger.Warn("Steam relay: server receive thread did not finish within five seconds");
            }

            _receiveThread = null;
        }

        _receiveTokenSource?.Dispose();
        _receiveTokenSource = null;

        foreach (var client in _clients.Values) {
            DisconnectClientInternal(client);
        }

        _clients.Clear();

        if (_loopbackChannel != null) {
            _loopbackChannel.UnregisterServer();
            SteamRelayLoopbackChannel.ReleaseIfEmpty();
            _loopbackChannel = null;
        }

        _sessionRequestCallback?.Dispose();
        _sessionRequestCallback = null;

        _sessionFailedCallback?.Dispose();
        _sessionFailedCallback = null;

        Logger.Info("Steam relay: server stopped");
    }

    /// <inheritdoc />
    public void DisconnectClient(IEncryptedTransportClient client) {
        if (client is SteamRelayTransportClient relayClient) {
            DisconnectClientInternal(relayClient);
        }
    }

    private void DisconnectClientInternal(SteamRelayTransportClient client) {
        if (!_clients.TryRemove(client.SteamId, out _)) return;

        if (SteamManager.IsInitialized && client.SteamId != SteamUser.GetSteamID().m_SteamID) {
            var identity = SteamRelayMessaging.IdentityOf(client.SteamId);
            SteamNetworkingMessages.CloseSessionWithUser(ref identity);
        }

        Logger.Info($"Steam relay: disconnected {client.SteamId}");
    }

    /// <summary>
    /// Accepts a player who wants to talk to this server.
    /// </summary>
    private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t request) {
        if (!_isRunning) return;

        var identity = request.m_identityRemote;
        var steamId = identity.GetSteamID64();

        Logger.Info($"Steam relay: session request from {steamId}");

        if (!SteamNetworkingMessages.AcceptSessionWithUser(ref identity)) {
            Logger.Warn($"Steam relay: could not accept the session with {steamId}");
        }
    }

    /// <summary>
    /// Says why a player's session failed, and lets them go.
    /// </summary>
    private void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t failure) {
        if (!_isRunning) return;

        var info = failure.m_info;
        var steamId = info.m_identityRemote.GetSteamID64();

        Logger.Warn($"Steam relay: the session with {steamId} failed: {SteamRelayMessaging.DescribeFailure(info)}");

        if (_clients.TryGetValue(steamId, out var client)) {
            DisconnectClientInternal(client);
        }
    }

    /// <summary>
    /// Drains everything players have sent, registering anyone not seen before.
    /// </summary>
    private void ProcessIncomingPackets() {
        if (!_isRunning) return;

        SteamRelayMessaging.Drain(SteamRelayMessaging.ClientToServerChannel, _messageBuffer, (sender, data, size) => {
            var client = GetOrAddClient(sender);
            client?.RaiseDataReceived(data, size);
        });
    }

    /// <summary>
    /// The player this message came from, registering them as newly connected if this is the first thing heard from
    /// them.
    /// </summary>
    private SteamRelayTransportClient? GetOrAddClient(ulong steamId) {
        if (_clients.TryGetValue(steamId, out var existing)) {
            return existing;
        }

        var client = new SteamRelayTransportClient(steamId);
        if (_clients.TryAdd(steamId, client)) {
            Logger.Info($"Steam relay: new player connected: {steamId}");
            ClientConnectedEvent?.Invoke(client);
            return client;
        }

        _clients.TryGetValue(steamId, out var raced);
        return raced;
    }

    /// <summary>
    /// Receives a packet from the local player hosting their own game.
    /// </summary>
    public void ReceiveLoopbackPacket(byte[] data, int length) {
        if (!_isRunning) return;

        try {
            var client = GetOrAddClient(SteamUser.GetSteamID().m_SteamID);
            client?.RaiseDataReceived(data, length);
        } catch (Exception e) {
            Logger.Error($"Steam relay: error handling a loopback packet: {e}");
        }
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

        while (_isRunning) {
            try {
                if (cancellationToken.IsCancellationRequested) break;

                if (!SteamManager.IsInitialized) {
                    Logger.Info("Steam relay: Steam shut down, leaving the server receive loop");
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

                ProcessIncomingPackets();
                nextPollTime += pollInterval;
            } catch (InvalidOperationException e) when (e.Message.Contains("Steamworks is not initialized")) {
                Logger.Info("Steam relay: Steamworks shut down while receiving, leaving the server loop");
                break;
            } catch (ThreadAbortException) {
                Logger.Info("Steam relay: server receive thread aborted, leaving the loop");
                break;
            } catch (Exception e) {
                Logger.Error($"Steam relay: error in the server receive loop: {e}");
                nextPollTime = stopwatch.ElapsedMilliseconds + pollInterval;
            }
        }

        Logger.Info("Steam relay: server receive loop exited cleanly");
    }
}
