using System;
using System.Collections.Generic;
using SSMP.Logging;
using SSMP.Util;

namespace SSMP.Networking.Packet;

/// <summary>
/// Generic registry for packet handlers that eliminates repetitive registration/execution code.
/// Supports both client handlers (no ID parameter) and server handlers (with client ID parameter).
/// </summary>
/// <typeparam name="TPacketId">The enum type for packet IDs.</typeparam>
/// <typeparam name="THandler">The delegate type for packet handlers.</typeparam>
internal class PacketHandlerRegistry<TPacketId, THandler>
    where TPacketId : notnull
    where THandler : Delegate {
    
    /// <summary>
    /// The registered handlers indexed by packet ID.
    /// </summary>
    private readonly Dictionary<TPacketId, THandler> _handlers = new();
    
    /// <summary>
    /// The dispatcher that handles how to call the packet handlers.
    /// For clients, this will be the <see cref="ClientPacketHandlerRegistryDispatcher"/>, which invokes packet
    /// handlers on the Unity main thread.
    /// For servers, this will be the <see cref="ServerPacketHandlerRegistryDispatcher"/>, which directly invokes the
    /// packet handlers.
    /// </summary>
    private readonly IPacketHandlerRegistryDispatcher _dispatcher;
    
    /// <summary>
    /// Descriptive name for logging messages.
    /// </summary>
    private readonly string _registryName;

    /// <summary>
    /// Data that arrived before anything had registered to handle it, kept per packet ID until something does.
    ///
    /// Handlers are registered while the server info is being handled, and a reliable packet sent right behind that
    /// info arrives before they exist. The transport has already acknowledged such a packet, so it is never sent
    /// again: dropping it here loses it for good. Pairing a two-player save was lost exactly this way, which left
    /// both players waiting on a check that could no longer complete.
    /// </summary>
    private readonly Dictionary<TPacketId, List<Action<THandler>>> _waiting = new();

    /// <summary>
    /// How much data one packet ID keeps while nothing handles it, after which the oldest is dropped. Without a
    /// limit, an ID that nothing ever registers for would grow without end.
    /// </summary>
    private const int MaxWaitingPerId = 64;

    /// <summary>
    /// How many calls of handlers are kept for reuse at most (see <see cref="HandlerCall"/>).
    /// </summary>
    private const int MaxKeptCalls = 256;

    /// <summary>
    /// Calls of handlers that ran, kept for the data that arrives next.
    /// </summary>
    private readonly Stack<HandlerCall> _keptCalls = new();

    /// <summary>
    /// Guards both dictionaries, which the network thread reads and the main thread writes, and the kept calls.
    /// </summary>
    private readonly object _lock = new();

    /// <summary>
    /// Constructs a new packet handler registry.
    /// </summary>
    /// <param name="registryName">Name for logging purposes (e.g., "client update", "server connection").</param>
    /// <param name="dispatcher">The dispatcher that handles how to call the packet handlers.</param>
    public PacketHandlerRegistry(string registryName, IPacketHandlerRegistryDispatcher dispatcher) {
        _registryName = registryName;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Registers a handler for the given packet ID.
    /// </summary>
    /// <param name="packetId">The packet ID to register the handler for.</param>
    /// <param name="handler">The handler delegate.</param>
    /// <returns>True if registration successful, false if handler already exists.</returns>
    public void Register(TPacketId packetId, THandler handler) {
        List<Action<THandler>>? waiting;
        lock (_lock) {
            if (!_handlers.TryAdd(packetId, handler)) {
                Logger.Warn($"Tried to register already existing {_registryName} packet handler: {packetId}");
                return;
            }

            _waiting.Remove(packetId, out waiting);
        }

        if (waiting == null || waiting.Count == 0) {
            return;
        }

        Logger.Info(
            $"Handing {waiting.Count} {_registryName} packet(s) for ID {packetId} to the handler that just registered"
        );

        // Outside the lock, because a server dispatcher runs the handler right here and handler code must never run
        // while this registry is locked
        foreach (var invoker in waiting) {
            _dispatcher.Dispatch(() => SafeInvoke(packetId, handler, invoker));
        }
    }

    /// <summary>
    /// Deregisters a handler for the given packet ID.
    /// </summary>
    /// <param name="packetId">The packet ID to deregister.</param>
    /// <returns>True if deregistration successful, false if handler didn't exist.</returns>
    public bool Deregister(TPacketId packetId) {
        lock (_lock) {
            // Anything still waiting belongs to the session that is ending, so it must not reach the next one
            _waiting.Remove(packetId);

            if (!_handlers.Remove(packetId)) {
                Logger.Warn($"Tried to remove nonexistent {_registryName} packet handler: {packetId}");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Executes a handler for a client packet (no client ID parameter).
    /// </summary>
    /// <param name="packetId">The packet ID.</param>
    /// <param name="invoker">Action that invokes the handler with appropriate parameters.</param>
    /// <returns>True if handler was found and invoked, false otherwise.</returns>
    public void Execute(TPacketId packetId, Action<THandler> invoker) {
        THandler? handler;
        lock (_lock) {
            if (!_handlers.TryGetValue(packetId, out handler)) {
                Wait(packetId, invoker);
                return;
            }
        }

        _dispatcher.Dispatch(() => SafeInvoke(packetId, handler, invoker));
    }

    /// <summary>
    /// Executes the handler for the given packet ID with a piece of data that arrived, as
    /// <see cref="Execute(TPacketId, Action{THandler})"/> does, but with a call kept from earlier data rather than a
    /// closure and two delegates made for each piece: the positions and such that arrive dozens of times a second each
    /// made garbage, which the game then stopped to clean up.
    /// </summary>
    /// <param name="packetId">The packet ID.</param>
    /// <param name="clientId">The ID of the client that sent the data, for the server's handlers.</param>
    /// <param name="data">The data.</param>
    /// <param name="invoke">Calls a handler with the client ID and the data, a delegate made once.</param>
    public void Execute(
        TPacketId packetId,
        ushort clientId,
        IPacketData data,
        Action<THandler, ushort, IPacketData> invoke
    ) {
        HandlerCall call;
        lock (_lock) {
            if (!_handlers.TryGetValue(packetId, out var handler)) {
                Wait(packetId, Invoker(invoke, clientId, data));
                return;
            }

            call = _keptCalls.Count > 0 ? _keptCalls.Pop() : new HandlerCall(this);
            call.Set(packetId, handler, invoke, clientId, data);
        }

        _dispatcher.Dispatch(call.Run);
    }

    /// <summary>
    /// Keeps a call of a handler for data that arrived before anything registered to handle it, until something does.
    /// Called with the registry locked.
    /// </summary>
    private void Wait(TPacketId packetId, Action<THandler> invoker) {
        // Kept rather than dropped, and handed over as soon as something registers for this ID
        if (!_waiting.TryGetValue(packetId, out var waiting)) {
            waiting = [];
            _waiting[packetId] = waiting;
        }

        if (waiting.Count >= MaxWaitingPerId) {
            Logger.Warn(
                $"Nothing has registered for {_registryName} packet ID {packetId}, dropping the oldest of " +
                $"{waiting.Count} kept for it"
            );
            waiting.RemoveAt(0);
        }

        waiting.Add(invoker);
    }

    /// <summary>
    /// The call of a handler with a piece of data, for data that waits for its handler. Made apart from
    /// <see cref="Execute(TPacketId, ushort, IPacketData, Action{THandler, ushort, IPacketData})"/>, whose closure
    /// would otherwise be made on every call.
    /// </summary>
    private static Action<THandler> Invoker(
        Action<THandler, ushort, IPacketData> invoke,
        ushort clientId,
        IPacketData data
    ) => handler => invoke(handler, clientId, data);

    /// <summary>
    /// Takes back a call that ran, for the next piece of data.
    /// </summary>
    private void Keep(HandlerCall call) {
        lock (_lock) {
            if (_keptCalls.Count < MaxKeptCalls) {
                _keptCalls.Push(call);
            }
        }
    }

    /// <summary>
    /// Safely invokes a handler with exception handling.
    /// </summary>
    private void SafeInvoke(TPacketId packetId, THandler handler, Action<THandler> invoker) {
        try {
            invoker(handler);
        } catch (Exception e) {
            Logger.Error($"Exception occurred while executing {_registryName} packet handler for ID {packetId}:\n{e}");
        }
    }

    /// <summary>
    /// A call of a handler with a piece of data that arrived, which goes back to its registry once it ran, for the next
    /// piece of data.
    /// </summary>
    private sealed class HandlerCall {
        /// <summary>
        /// The registry it belongs to.
        /// </summary>
        private readonly PacketHandlerRegistry<TPacketId, THandler> _registry;

        /// <summary>
        /// Makes the call; the dispatcher runs it.
        /// </summary>
        public readonly Action Run;

        /// <summary>
        /// The packet ID of the data.
        /// </summary>
        private TPacketId _packetId = default!;

        /// <summary>
        /// The handler.
        /// </summary>
        private THandler? _handler;

        /// <summary>
        /// Calls the handler with the client ID and the data.
        /// </summary>
        private Action<THandler, ushort, IPacketData>? _invoke;

        /// <summary>
        /// The ID of the client that sent the data, for the server's handlers.
        /// </summary>
        private ushort _clientId;

        /// <summary>
        /// The data.
        /// </summary>
        private IPacketData? _data;

        public HandlerCall(PacketHandlerRegistry<TPacketId, THandler> registry) {
            _registry = registry;
            Run = RunOnce;
        }

        /// <summary>
        /// Sets what to call with what.
        /// </summary>
        public void Set(
            TPacketId packetId,
            THandler handler,
            Action<THandler, ushort, IPacketData> invoke,
            ushort clientId,
            IPacketData data
        ) {
            _packetId = packetId;
            _handler = handler;
            _invoke = invoke;
            _clientId = clientId;
            _data = data;
        }

        /// <summary>
        /// Makes the call, having given itself back first: a handler that the server runs right away may hand on data
        /// of its own, which can then take this call again.
        /// </summary>
        private void RunOnce() {
            var packetId = _packetId;
            var handler = _handler!;
            var invoke = _invoke!;
            var clientId = _clientId;
            var data = _data!;
            _handler = null;
            _invoke = null;
            _data = null;
            _registry.Keep(this);

            try {
                invoke(handler, clientId, data);
            } catch (Exception e) {
                Logger.Error(
                    $"Exception occurred while executing {_registry._registryName} packet handler for ID {packetId}:" +
                    $"\n{e}"
                );
            }
        }
    }
}

/// <summary>
/// Interface for packet handler dispatchers.
/// </summary>
internal interface IPacketHandlerRegistryDispatcher {
    /// <summary>
    /// Dispatch this handler with the given action.
    /// </summary>
    void Dispatch(Action action);
}

/// <summary>
/// Implementation of packet handler dispatcher to immediately invoke the handler directly for the server-side.
/// </summary>
internal class ServerPacketHandlerRegistryDispatcher : IPacketHandlerRegistryDispatcher {
    /// <summary>
    /// Publicly accessible static instance, since instances can not vary.
    /// </summary>
    public static readonly ServerPacketHandlerRegistryDispatcher Instance = new();

    /// <summary>
    /// Private constructor to prevent access outside of static instance.
    /// </summary>
    private ServerPacketHandlerRegistryDispatcher() {
    }

    /// <inheritdoc/>
    public void Dispatch(Action action) {
        action.Invoke();
    }
}

/// <summary>
/// Implementation of packet handler dispatcher to invoke the handler on Unity's main thread.
/// </summary>
internal class ClientPacketHandlerRegistryDispatcher : IPacketHandlerRegistryDispatcher {
    /// <summary>
    /// Publicly accessible static instance, since instances can not vary.
    /// </summary>
    public static readonly ClientPacketHandlerRegistryDispatcher Instance = new();

    /// <summary>
    /// Private constructor to prevent access outside of static instance.
    /// </summary>
    private ClientPacketHandlerRegistryDispatcher() {
    }
    
    /// <inheritdoc/>
    public void Dispatch(Action action) {
        ThreadUtil.RunActionOnMainThread(action);
    }
}
