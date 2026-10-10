using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using SSMP.Util;
using SSMP.Animation;
using SSMP.Api.Command.Server;
using SSMP.Api.Eventing.ServerEvents;
using SSMP.Api.Server;
using SSMP.Eventing;
using SSMP.Eventing.ServerEvents;
using SSMP.Game.Client.Entity.Component;
using SSMP.Game.Command.Server;
using SSMP.Game.Server.Auth;
using SSMP.Game.Settings;
using SSMP.Logging;
using SSMP.Math;
using SSMP.Networking;
using SSMP.Networking.Packet;
using SSMP.Networking.Packet.Data;
using SSMP.Networking.Packet.Update;
using SSMP.Networking.Server;
using SSMP.Networking.Transport.Common;

// ReSharper disable InconsistentlySynchronizedField

namespace SSMP.Game.Server;

/// <summary>
/// Class that manages the server state (similar to ClientManager). For example the current scene of
/// each player, to prevent sending redundant traffic.
/// </summary>
internal abstract class ServerManager : IServerManager {
    #region Internal server manager variables and properties

    /// <summary>
    /// The name of the authorized file.
    /// </summary>
    private const string AuthorizedFileName = "authorized.json";

    /// <summary>
    /// The maximum length of a username, for validation purposes in multiple places.
    /// </summary>
    public const int MaxUsernameLength = 20;

    /// <summary>
    /// The net server instance.
    /// </summary>
    private readonly NetServer _netServer;

    /// <summary>
    /// The packet manager instance for register and deregistering packet handlers.
    /// </summary>
    private readonly PacketManager _packetManager;

    /// <summary>
    /// Dictionary mapping player IDs to their server player data instances.
    /// </summary>
    private readonly ConcurrentDictionary<ushort, ServerPlayerData> _playerData;

    /// <summary>
    /// Dictionary mapping entity keys to their server entity data instances.
    /// </summary>
    private readonly ConcurrentDictionary<ServerEntityKey, ServerEntityData> _entityData;

    /// <summary>
    /// Dictionary mapping scene names to their current scene host epochs.
    /// </summary>
    private readonly ConcurrentDictionary<string, uint> _sceneHostEpochs;

    /// <summary>
    /// The white-list for managing player logins.
    /// </summary>
    private readonly WhiteList _whiteList;

    /// <summary>
    /// Authorized list for managing player permission.
    /// </summary>
    private readonly AuthKeyList _authorizedList;

    /// <summary>
    /// The list of banned users.
    /// </summary>
    private readonly BanList _banList;

    /// <summary>
    /// The server settings.
    /// </summary>
    public readonly ServerSettings InternalServerSettings;

    /// <summary>
    /// Lock to synchronize Start/Stop operations, ensuring cleanup completes before restart.
    /// </summary>
    private readonly object _serverStateLock = new();

    /// <summary>
    /// The server command manager instance.
    /// </summary>
    protected readonly ServerCommandManager CommandManager;

    /// <summary>
    /// The addon manager instance.
    /// </summary>
    protected readonly ServerAddonManager AddonManager;

    /// <summary>
    /// Whether full synchronisation is enabled for the server.
    /// </summary>
    private bool _fullSynchronisation;

    // /// <summary>
    // /// The save data for the server. The instance will be created in the constructor and is passed around to other
    // /// objects. Therefore, it should not change instances.
    // /// </summary>
    // protected ServerSaveData ServerSaveData;

    #endregion

    #region Internal server manager commands

    /// <summary>
    /// The help command.
    /// </summary>
    private readonly IServerCommand _helpCommand;

    /// <summary>
    /// The list command.
    /// </summary>
    private readonly IServerCommand _listCommand;

    /// <summary>
    /// The whitelist command.
    /// </summary>
    private readonly IServerCommand _whiteListCommand;

    /// <summary>
    /// The authorize command.
    /// </summary>
    private readonly IServerCommand _authorizeCommand;

    /// <summary>
    /// The announce command.
    /// </summary>
    private readonly IServerCommand _announceCommand;

    /// <summary>
    /// The ban command.
    /// </summary>
    private readonly IServerCommand _banCommand;

    /// <summary>
    /// The kick command.
    /// </summary>
    private readonly IServerCommand _kickCommand;

    /// <summary>
    /// The team command.
    /// </summary>
    private readonly IServerCommand _teamCommand;

    /// <summary>
    /// The skin command.
    /// </summary>
    private readonly IServerCommand _skinCommand;
    // /// <summary>
    // /// The copy save command.
    // /// </summary>
    // private readonly IServerCommand _copySaveCommand;

    #endregion

    #region IServerManager properties

    /// <inheritdoc />
    public IReadOnlyCollection<IServerPlayer> Players => new List<IServerPlayer>(_playerData.Values);

    /// <inheritdoc />
    public IServerSettings ServerSettings => InternalServerSettings;

    /// <inheritdoc />
    public event Action<IServerPlayer>? PlayerConnectEvent;

    /// <inheritdoc />
    public event Action<IServerPlayer>? PlayerDisconnectEvent;

    /// <inheritdoc />
    public event Action<IServerPlayer>? PlayerEnterSceneEvent;

    /// <inheritdoc />
    public event Action<IServerPlayer>? PlayerLeaveSceneEvent;

    /// <inheritdoc />
    public event Action<IServerPlayer, Team>? PlayerTeamChangedEvent;

    /// <inheritdoc />
    public event Action<IPlayerChatEvent>? PlayerChatEvent;

    /// <inheritdoc />
    public event Action? ServerShutdownEvent;

    #endregion

    /// <summary>
    /// Constructs the server manager.
    /// </summary>
    /// <param name="netServer">The net server instance.</param>
    /// <param name="packetManager">The packet manager instance.</param>
    /// <param name="serverSettings">The server settings.</param>
    protected ServerManager(
        NetServer netServer,
        PacketManager packetManager,
        ServerSettings serverSettings
    ) {
        _netServer = netServer;
        _packetManager = packetManager;
        InternalServerSettings = serverSettings;
        _playerData = new ConcurrentDictionary<ushort, ServerPlayerData>();
        _entityData = new ConcurrentDictionary<ServerEntityKey, ServerEntityData>();
        _sceneHostEpochs = new ConcurrentDictionary<string, uint>();

        CommandManager = new ServerCommandManager();
        var eventAggregator = new EventAggregator();

        var serverApi = new ServerApi(this, CommandManager, _netServer, eventAggregator);
        AddonManager = new ServerAddonManager(serverApi);

        // ServerSaveData = new ServerSaveData();

        // Load the lists
        _whiteList = WhiteList.LoadFromFile();
        _authorizedList = AuthKeyList.LoadFromFile(AuthorizedFileName);
        _banList = BanList.LoadFromFile();

        _listCommand = new ListCommand(this);
        _whiteListCommand = new WhiteListCommand(_whiteList, this);
        _authorizeCommand = new AuthorizeCommand(_authorizedList, this);
        _announceCommand = new AnnounceCommand(_playerData, _netServer);
        _banCommand = new BanCommand(_banList, this);
        _kickCommand = new KickCommand(this);
        _teamCommand = new TeamCommand(this);
        _skinCommand = new SkinCommand(this);
        _helpCommand = new HelpCommand(this);
        // _copySaveCommand = new CopySaveCommand(this, ServerSaveData);
    }

    /// <summary>
    /// Gets the next scene host epoch for the given scene name, incrementing it if it already exists.
    /// </summary>
    /// <param name="sceneName">The name of the scene.</param>
    /// <returns>The next scene host epoch.</returns>
    private uint GetNextSceneHostEpoch(string sceneName) {
        return _sceneHostEpochs.AddOrUpdate(sceneName, 1, (_, current) => current + 1);
    }

    #region Internal server manager methods

    /// <summary>
    /// Initializes the server manager.
    /// </summary>
    public virtual void Initialize() {
        // Register a timeout handler
        _netServer.ClientTimeoutEvent += OnClientTimeout;

        // Register server shutdown handler
        _netServer.ShutdownEvent += OnServerShutdown;

        // Register a handler for when a client wants to connect
        _netServer.ConnectionRequestEvent += OnConnectionRequest;

        // And one for when something arrives from a client after a stretch in which nothing did
        _netServer.ClientReceiveResumedEvent += OnClientReceiveResumed;
    }

    /// <summary>
    /// Register the default server commands.
    /// </summary>
    protected virtual void RegisterCommands() {
        CommandManager.RegisterCommand(_listCommand);
        CommandManager.RegisterCommand(_whiteListCommand);
        CommandManager.RegisterCommand(_authorizeCommand);
        CommandManager.RegisterCommand(_announceCommand);
        CommandManager.RegisterCommand(_banCommand);
        CommandManager.RegisterCommand(_kickCommand);
        CommandManager.RegisterCommand(_teamCommand);
        CommandManager.RegisterCommand(_skinCommand);
        CommandManager.RegisterCommand(_helpCommand);

        // if (FullSynchronisation) {
        //     CommandManager.RegisterCommand(_copySaveCommand);
        // }
    }

    /// <summary>
    /// Deregister the default server commands.
    /// </summary>
    protected virtual void DeregisterCommands() {
        CommandManager.DeregisterCommand(_listCommand);
        CommandManager.DeregisterCommand(_whiteListCommand);
        CommandManager.DeregisterCommand(_authorizeCommand);
        CommandManager.DeregisterCommand(_announceCommand);
        CommandManager.DeregisterCommand(_banCommand);
        CommandManager.DeregisterCommand(_kickCommand);
        CommandManager.DeregisterCommand(_teamCommand);
        CommandManager.DeregisterCommand(_skinCommand);
        CommandManager.DeregisterCommand(_helpCommand);

        // if (FullSynchronisation) {
        //     CommandManager.DeregisterCommand(_copySaveCommand);
        // }
    }

    /// <summary>
    /// Register the packet handlers for handling incoming packet data.
    /// </summary>
    private void RegisterPacketHandlers() {
        Logger.Debug("Registering packet handlers");

        _packetManager.RegisterServerUpdatePacketHandler<ServerPlayerEnterScene>(
            ServerUpdatePacketId.PlayerEnterScene,
            OnClientEnterScene
        );
        _packetManager.RegisterServerUpdatePacketHandler<ServerPlayerLeaveScene>(
            ServerUpdatePacketId.PlayerLeaveScene,
            OnClientLeaveScene
        );
        _packetManager.RegisterServerUpdatePacketHandler<PlayerUpdate>(
            ServerUpdatePacketId.PlayerUpdate,
            OnPlayerUpdate
        );
        _packetManager.RegisterServerUpdatePacketHandler<PlayerMapUpdate>(
            ServerUpdatePacketId.PlayerMapUpdate,
            OnPlayerMapUpdate
        );
        _packetManager.RegisterServerUpdatePacketHandler(
            ServerUpdatePacketId.PlayerDisconnect,
            OnPlayerDisconnect
        );
        _packetManager.RegisterServerUpdatePacketHandler(
            ServerUpdatePacketId.PlayerDeath,
            OnPlayerDeath
        );
        _packetManager.RegisterServerUpdatePacketHandler(
            ServerUpdatePacketId.SemiPersistentReset,
            OnSemiPersistentReset
        );
        _packetManager.RegisterServerUpdatePacketHandler(
            ServerUpdatePacketId.SceneResyncRequest,
            OnSceneResyncRequest
        );
        _packetManager.RegisterServerUpdatePacketHandler(
            ServerUpdatePacketId.RoomStateRequest,
            OnRoomStateRequest
        );
        _packetManager.RegisterServerUpdatePacketHandler<BattleSceneUpdate>(
            ServerUpdatePacketId.BattleSceneUpdate,
            OnBattleSceneUpdate
        );
        _packetManager.RegisterServerUpdatePacketHandler<BossRoomUpdate>(
            ServerUpdatePacketId.BossRoomUpdate,
            OnBossRoomUpdate
        );
        _packetManager.RegisterServerUpdatePacketHandler<CoopSaveUpdate>(
            ServerUpdatePacketId.CoopSaveUpdate,
            OnCoopSaveUpdate
        );
        _packetManager.RegisterServerUpdatePacketHandler<CoopHitUpdate>(
            ServerUpdatePacketId.CoopHitUpdate,
            OnCoopHitUpdate
        );
        _packetManager.RegisterServerUpdatePacketHandler<CoopCheckUpdate>(
            ServerUpdatePacketId.CoopCheckUpdate,
            OnCoopCheckUpdate
        );
        _packetManager.RegisterServerUpdatePacketHandler<ChatMessage>(
            ServerUpdatePacketId.ChatMessage,
            OnChatMessage
        );
        _packetManager.RegisterServerUpdatePacketHandler<ServerSettingsUpdate>(
            ServerUpdatePacketId.ServerSettings,
            OnServerSettingsUpdate
        );
        _packetManager.RegisterServerUpdatePacketHandler<ServerPlayerSettingUpdate>(
            ServerUpdatePacketId.PlayerSetting,
            OnPlayerSettingUpdate
        );

        if (_fullSynchronisation) {
            _packetManager.RegisterServerUpdatePacketHandler<EntitySpawn>(
                ServerUpdatePacketId.EntitySpawn,
                OnEntitySpawn
            );
            _packetManager.RegisterServerUpdatePacketHandler<EntityUpdate>(
                ServerUpdatePacketId.EntityUpdate,
                OnEntityUpdate
            );
            _packetManager.RegisterServerUpdatePacketHandler<ReliableEntityUpdate>(
                ServerUpdatePacketId.ReliableEntityUpdate,
                OnReliableEntityUpdate
            );
            _packetManager.RegisterServerUpdatePacketHandler<ClientPlayerAlreadyInScene>(
                ServerUpdatePacketId.RoomSnapshot,
                OnRoomSnapshot
            );
            _packetManager.RegisterServerUpdatePacketHandler<SaveUpdate>(
                ServerUpdatePacketId.SaveUpdate,
                OnSaveUpdate
            );
        }
    }

    /// <summary>
    /// Deregister the packet handlers for handling incoming packet data.
    /// </summary>
    private void DeregisterPacketHandlers() {
        Logger.Debug("Deregistering packet handlers");

        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.PlayerEnterScene);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.PlayerLeaveScene);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.PlayerUpdate);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.PlayerMapUpdate);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.PlayerDisconnect);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.PlayerDeath);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.SemiPersistentReset);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.SceneResyncRequest);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.RoomStateRequest);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.RoomSnapshot);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.BattleSceneUpdate);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.BossRoomUpdate);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.CoopSaveUpdate);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.CoopHitUpdate);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.CoopCheckUpdate);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.ChatMessage);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.ServerSettings);
        _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.PlayerSetting);

        if (_fullSynchronisation) {
            _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.EntitySpawn);
            _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.EntityUpdate);
            _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.ReliableEntityUpdate);
            _packetManager.DeregisterServerUpdatePacketHandler(ServerUpdatePacketId.SaveUpdate);
        }
    }

    /// <summary>
    /// Starts a server with the given port.
    /// </summary>
    /// <param name="port">The port the server should run on.</param>
    /// <param name="fullSynchronisation">Whether full synchronisation should be enabled.</param>
    /// <param name="transportServer">The transport server to use.</param>
    public virtual void Start(int port, bool fullSynchronisation, IEncryptedTransportServer transportServer) {
        lock (_serverStateLock) {
            // Stop existing server (including deregistering commands)
            if (_netServer.IsStarted) {
                Logger.Info("Server was running, shutting it down before starting");
                StopInternal();
            }

            _fullSynchronisation = fullSynchronisation;

            RegisterCommands();
            RegisterPacketHandlers();

            // Start server again with given port
            _netServer.Start(port, transportServer);
        }
    }

    /// <summary>
    /// Stops the currently running server.
    /// </summary>
    public void Stop() {
        lock (_serverStateLock) {
            StopInternal();
        }
    }

    /// <summary>
    /// Internal stop logic without locking (called from Start and Stop).
    /// </summary>
    private void StopInternal() {
        if (!_netServer.IsStarted) return;

        // Before shutting down, send TCP packets to all clients indicating
        // that the server is shutting down
        _netServer.SetDataForAllClients(updateManager => { updateManager.SetDisconnect(DisconnectReason.Shutdown); });

        _netServer.Stop();

        DeregisterCommands();
        DeregisterPacketHandlers();

        _playerData.Clear();
        _entityData.Clear();
    }

    /// <summary>
    /// Authorizes a given authentication key.
    /// </summary>
    /// <param name="authKey">The authentication key to authorize.</param>
    public void AuthorizeKey(string authKey) {
        _authorizedList.Add(authKey);
    }

    /// <summary>
    /// Called when the server settings are updated, and need to be broadcast.
    /// </summary>
    public void OnUpdateServerSettings() {
        if (!_netServer.IsStarted) {
            return;
        }

        _netServer.SetDataForAllClients(updateManager => { updateManager.UpdateServerSettings(InternalServerSettings); }
        );
    }

    /// <summary>
    /// Tells every other player which room a player is in now. The room goes to whoever is in it first, so this is
    /// how a game walking into a room knows beforehand whether it will be the one running it.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="sceneName">The name of the scene the player is in, or empty while they are in none.</param>
    private void SendPlayerRoom(ushort id, string sceneName) {
        foreach (var otherId in _playerData.Keys) {
            if (otherId != id) {
                _netServer.GetUpdateManagerForClient(otherId)?.AddPlayerRoomData(id, sceneName);
            }
        }
    }

    /// <summary>
    /// Callback method for when a player enters a scene.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="playerEnterScene">The ServerPlayerEnterScene packet data.</param>
    private void OnClientEnterScene(ushort id, ServerPlayerEnterScene playerEnterScene) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received EnterScene data from {id}, but player is not in mapping");
            return;
        }

        var newSceneName = playerEnterScene.NewSceneName;

        Logger.Info($"Received EnterScene data from ({id}, {playerData.Username}), new scene: {newSceneName}");

        // Store it in their PlayerData object
        playerData.CurrentScene = newSceneName;
        SendPlayerRoom(id, newSceneName);
        playerData.Position = playerEnterScene.Position;
        playerData.Scale = playerEnterScene.Scale;
        playerData.AnimationId = playerEnterScene.AnimationClipId;

        OnClientEnterScene(playerData);

        try {
            PlayerEnterSceneEvent?.Invoke(playerData);
        } catch (Exception e) {
            Logger.Error($"Exception thrown while invoking PlayerEnterScene event:\n{e}");
        }
    }

    /// <summary>
    /// Method that handles a player entering a scene.
    /// </summary>
    /// <param name="playerData">The ServerPlayerData corresponding to the player.</param>
    /// <param name="resync">
    /// Whether this is the same arrival being answered again because the answer never reached the player, rather
    /// than a fresh one. Nothing is decided over: the room keeps the host it has and the other player, who was told
    /// the first time, is not told twice.
    /// </param>
    /// <param name="roomState">
    /// Whether this answers a player who has been in the room all along and asks for its state after a gap in what
    /// reached them (<see cref="OnRoomStateRequest"/>), which is sent as <see cref="ClientUpdatePacketId.RoomState"/>.
    /// It is answered like <paramref name="resync"/>, and without a line in the log for every entity.
    /// </param>
    private void OnClientEnterScene(ServerPlayerData playerData, bool resync = false, bool roomState = false) {
        resync |= roomState;

        var enterSceneList = new List<ClientPlayerEnterScene>();
        var alreadyPlayersInScene = false;

        foreach (var (key, otherPlayerData) in _playerData) {
            // Skip source player
            if (key == playerData.Id) {
                continue;
            }

            // Send the packet to all clients on the new scene
            // to indicate that this client has entered their scene
            if (otherPlayerData.CurrentScene.Equals(playerData.CurrentScene)) {
                if (!resync) {
                    Logger.Debug($"Sending EnterScene data to {key}");

                    _netServer.GetUpdateManagerForClient(key)?.AddPlayerEnterSceneData(
                        playerData.Id,
                        playerData.CurrentScene,
                        playerData.Position ?? Vector2.Zero,
                        playerData.Scale,
                        playerData.AnimationId
                    );
                }

                Logger.Debug($"Sending that {key} is already in scene to {playerData.Id}");

                alreadyPlayersInScene = true;

                // Also send a packet to the client that switched scenes,
                // notifying that these players are already in this new scene.
                enterSceneList.Add(
                    new ClientPlayerEnterScene {
                        Id = key,
                        SceneName = playerData.CurrentScene,
                        Position = otherPlayerData.Position ?? Vector2.Zero,
                        Scale = otherPlayerData.Scale,
                        AnimationClipId = otherPlayerData.AnimationId
                    }
                );
            }
        }

        var entitySpawnList = new List<EntitySpawn>();
        var entityUpdateList = new List<EntityUpdate>();
        var reliableEntityUpdateList = new List<ReliableEntityUpdate>();
        var makeEnteringPlayerHost = false;
        var sceneHostEpoch = 0u;

        if (_fullSynchronisation) {
            foreach (var (entityKey, entityData) in _entityData) {
                // Check which entities are actually in the scene that the player is entering
                if (!entityKey.Scene.Equals(playerData.CurrentScene)) {
                    continue;
                }

                if (entityData.Spawned) {
                    if (!roomState) {
                        Logger.Info(
                            $"Sending that entity '{entityKey.EntityId}' has spawned in the scene to '{playerData.Id}'"
                        );
                    }

                    var entitySpawn = new EntitySpawn {
                        Id = entityKey.EntityId,
                        SpawningType = entityData.SpawningType,
                        SpawnedType = entityData.SpawnedType
                    };

                    entitySpawnList.Add(entitySpawn);
                }

                if (!roomState) {
                    Logger.Info(
                        $"Sending that entity '{entityKey.EntityId}' is already in scene to '{playerData.Id}'"
                    );
                }

                var entityUpdate = new EntityUpdate {
                    Id = entityKey.EntityId
                };

                if (entityData.Position != null) {
                    entityUpdate.UpdateTypes.Add(EntityUpdateType.Position);
                    entityUpdate.Position = entityData.Position;
                }

                // Copies of what is kept, here and for the FSMs below: the packet is written on another thread, while
                // what is kept goes on changing on this one as it is heard
                if (!entityData.Scale.IsEmpty) {
                    entityUpdate.UpdateTypes.Add(EntityUpdateType.Scale);
                    entityUpdate.Scale = new EntityUpdate.ScaleData();
                    entityUpdate.Scale.Merge(entityData.Scale);
                }

                if (entityData.AnimationId.HasValue) {
                    entityUpdate.UpdateTypes.Add(EntityUpdateType.Animation);

                    entityUpdate.AnimationId = entityData.AnimationId.Value;
                    entityUpdate.AnimationWrapMode = entityData.AnimationWrapMode;
                }

                var reliableEntityUpdate = new ReliableEntityUpdate {
                    Id = entityKey.EntityId
                };

                if (entityData.IsActive.HasValue) {
                    reliableEntityUpdate.UpdateTypes.Add(EntityUpdateType.Active);
                    reliableEntityUpdate.IsActive = entityData.IsActive.Value;
                }

                if (entityData.GenericData.Count > 0) {
                    reliableEntityUpdate.UpdateTypes.Add(EntityUpdateType.Data);

                    foreach (var genericData in entityData.GenericData) {
                        reliableEntityUpdate.GenericData.Add(genericData.Clone());
                    }
                }

                if (entityData.HostFsmData.Count > 0) {
                    reliableEntityUpdate.UpdateTypes.Add(EntityUpdateType.HostFsm);

                    foreach (var pair in entityData.HostFsmData) {
                        var fsmData = new EntityHostFsmData();
                        fsmData.MergeData(pair.Value);
                        reliableEntityUpdate.HostFsmData[pair.Key] = fsmData;
                    }
                }

                entityUpdateList.Add(entityUpdate);
                reliableEntityUpdateList.Add(reliableEntityUpdate);
            }

            // Answering again decides nothing again: the room keeps whoever has been running it, and this player
            // keeps whatever they already were. Running the choice a second time would hand the room to the player
            // whose first answer went missing, taking it off the one that has been playing it all along.
            var isReturningPreviousHost = !resync && playerData.LastHostedScene == playerData.CurrentScene;
            var shouldDemoteCurrentHost = !resync && alreadyPlayersInScene && isReturningPreviousHost;
            makeEnteringPlayerHost = resync
                ? playerData.IsSceneHost
                : !alreadyPlayersInScene || isReturningPreviousHost;

            if (shouldDemoteCurrentHost) {
                var epoch = GetNextSceneHostEpoch(playerData.CurrentScene);
                foreach (var (key, otherPlayerData) in _playerData) {
                    if (key == playerData.Id) continue;
                    if (otherPlayerData.CurrentScene != playerData.CurrentScene) continue;

                    if (otherPlayerData.IsSceneHost) {
                        otherPlayerData.IsSceneHost = false;

                        _netServer
                            .GetUpdateManagerForClient(key)
                            ?.SetSceneHostTransfer(playerData.CurrentScene, epoch, demote: true);

                        Logger.Info(
                            $"Demoted player {key} ({otherPlayerData.Username}) in scene {playerData.CurrentScene} since the previous host {playerData.Id} ({playerData.Username}) re-entered (epoch {epoch})"
                        );
                    } else {
                        _netServer
                            .GetUpdateManagerForClient(key)
                            ?.SetSceneHostTransfer(playerData.CurrentScene, epoch, demote: true);
                    }
                }
            }

            if (makeEnteringPlayerHost) {
                Logger.Debug($"Making {playerData.Id} the scene host");
                playerData.IsSceneHost = true;
            }

            if (!resync) {
                playerData.LastHostedScene = null;
            }

            sceneHostEpoch = _sceneHostEpochs.GetOrAdd(playerData.CurrentScene, 0u);
        }

        _netServer.GetUpdateManagerForClient(playerData.Id)?.AddPlayerAlreadyInSceneData(
            enterSceneList,
            entitySpawnList,
            entityUpdateList,
            reliableEntityUpdateList,
            _fullSynchronisation && makeEnteringPlayerHost,
            sceneHostEpoch,
            playerData.CurrentScene,
            roomState ? ClientUpdatePacketId.RoomState : ClientUpdatePacketId.PlayerAlreadyInScene
        );

        if (roomState) {
            Logger.Info(
                $"Told ({playerData.Id}, {playerData.Username}) the state of '{playerData.CurrentScene}' again: " +
                $"{enterSceneList.Count} other players, {entityUpdateList.Count} entities"
            );
        }
    }

    /// <summary>
    /// Callback method for when a player leaves a scene.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="playerLeaveScene">The leave scene data for the player.</param>
    private void OnClientLeaveScene(ushort id, ServerPlayerLeaveScene playerLeaveScene) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received LeaveScene data from {id}, but player is not in mapping");
            return;
        }

        HandlePlayerLeaveScene(id, false, false, playerLeaveScene.SceneName);

        try {
            PlayerLeaveSceneEvent?.Invoke(playerData);
        } catch (Exception e) {
            Logger.Error($"Exception thrown while invoking PlayerLeaveScene event:\n{e}");
        }
    }

    /// <summary>
    /// Callback method for when a player update is received.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="playerUpdate">The PlayerUpdate packet data.</param>
    private void OnPlayerUpdate(ushort id, PlayerUpdate playerUpdate) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received PlayerUpdate data, but player with ID {id} is not in mapping");
            return;
        }

        if (playerUpdate.UpdateTypes.Contains(PlayerUpdateType.Position)) {
            playerData.Position = playerUpdate.Position;

            foreach (var (otherId, otherPd) in _playerData) {
                if (otherId == id) {
                    continue;
                }

                if (!string.Equals(otherPd.CurrentScene, playerData.CurrentScene)) {
                    continue;
                }

                _netServer.GetUpdateManagerForClient(otherId)?.UpdatePlayerPosition(id, playerUpdate.Position);
            }
        }

        if (playerUpdate.UpdateTypes.Contains(PlayerUpdateType.Scale)) {
            playerData.Scale = playerUpdate.Scale;

            foreach (var (otherId, otherPd) in _playerData) {
                if (otherId == id) {
                    continue;
                }

                if (!string.Equals(otherPd.CurrentScene, playerData.CurrentScene)) {
                    continue;
                }

                _netServer.GetUpdateManagerForClient(otherId)?.UpdatePlayerScale(id, playerUpdate.Scale);
            }
        }

        if (playerUpdate.UpdateTypes.Contains(PlayerUpdateType.MapPosition)) {
            playerData.MapPosition = playerUpdate.MapPosition;

            // If the player does not have an active map icon, we do not send the map position update
            if (!playerData.HasMapIcon) {
                return;
            }

            // If the map icons need to be broadcast, we add the data to the next packet
            if (InternalServerSettings.AlwaysShowMapIcons || InternalServerSettings.OnlyBroadcastMapIconWithCompass) {
                foreach (var idPlayerDataPair in _playerData) {
                    if (idPlayerDataPair.Key == id) {
                        continue;
                    }

                    _netServer.GetUpdateManagerForClient(idPlayerDataPair.Key)?
                        .UpdatePlayerMapPosition(id, playerUpdate.MapPosition);
                }
            }
        }

        if (playerUpdate.UpdateTypes.Contains(PlayerUpdateType.Animation)) {
            var animationInfos = playerUpdate.AnimationInfos;

            // Check whether there is any animation info to be stored
            if (animationInfos.Count != 0) {
                // Find the last animation clip that is not a custom clip to set as the players animation ID
                // Since that is the last clip that the player updated
                for (var i = animationInfos.Count - 1; i >= 0; i--) {
                    var clipId = animationInfos[i].ClipId;
                    if (clipId < (ushort) AnimationClip.DashEnd) {
                        playerData.AnimationId = clipId;
                        break;
                    }
                }

                // Set the animation data for each player in the same scene
                foreach (var (otherId, otherPd) in _playerData) {
                    if (otherId == id) {
                        continue;
                    }

                    if (!string.Equals(otherPd.CurrentScene, playerData.CurrentScene)) {
                        continue;
                    }

                    var updateManager = _netServer.GetUpdateManagerForClient(otherId);
                    if (updateManager != null) {
                        foreach (var animationInfo in animationInfos) {
                            updateManager.UpdatePlayerAnimation(
                                id,
                                animationInfo.ClipId,
                                animationInfo.Frame,
                                animationInfo.EffectInfo
                            );
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Callback method for when a player map update is received from a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="playerMapUpdate">The PlayerMapUpdate packet data.</param>
    private void OnPlayerMapUpdate(ushort id, PlayerMapUpdate playerMapUpdate) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received PlayerMapUpdate data, but player with ID {id} is not in mapping");
            return;
        }

        playerData.HasMapIcon = playerMapUpdate.HasIcon;

        foreach (var idPlayerDataPair in _playerData) {
            if (idPlayerDataPair.Key == id) {
                continue;
            }

            _netServer.GetUpdateManagerForClient(idPlayerDataPair.Key)?
                .UpdatePlayerMapIcon(id, playerData.HasMapIcon);

            if (playerData.HasMapIcon && playerData.MapPosition != null) {
                // If the player now has a map icon, we also send the map position
                _netServer.GetUpdateManagerForClient(idPlayerDataPair.Key)?
                    .UpdatePlayerMapPosition(id, playerData.MapPosition);
            }
        }
    }

    /// <summary>
    /// Callback method for when an entity spawn is received from a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="entitySpawn">The EntitySpawn packet data.</param>
    private void OnEntitySpawn(ushort id, EntitySpawn entitySpawn) {
        if (!_fullSynchronisation) {
            return;
        }

        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Info($"Received EntitySpawn data, but player with ID {id} is not in mapping");
            return;
        }

        // If the player is not the scene host, ignore this data
        if (!playerData.IsSceneHost) {
            return;
        }

        // Create the key for the entity data
        var serverEntityKey = new ServerEntityKey(
            playerData.CurrentScene,
            entitySpawn.Id
        );

        // Check with the created key whether we have an existing entry
        if (!_entityData.TryGetValue(serverEntityKey, out var entityData)) {
            // If the entry for this entity did not yet exist, we insert a new one
            entityData = new ServerEntityData();
            _entityData[serverEntityKey] = entityData;
        }

        Logger.Info(
            $"Received EntitySpawn from {id}, with entity {entitySpawn.Id}, {entitySpawn.SpawningType}, {entitySpawn.SpawnedType}"
        );

        entityData.Spawned = true;
        entityData.SpawningType = entitySpawn.SpawningType;
        entityData.SpawnedType = entitySpawn.SpawnedType;

        SendDataInSameScene(
            id,
            playerData.CurrentScene,
            otherId => {
                _netServer.GetUpdateManagerForClient(otherId)?.SetEntitySpawn(
                    entitySpawn.Id,
                    entitySpawn.SpawningType,
                    entitySpawn.SpawnedType
                );
            }
        );
    }

    /// <summary>
    /// Callback method for when an entity update is received from a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="entityUpdate">The EntityUpdate packet data.</param>
    private void OnEntityUpdate(ushort id, EntityUpdate entityUpdate) {
        if (!_fullSynchronisation) {
            return;
        }

        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received EntityUpdate data, but player with ID {id} is not in mapping");
            return;
        }

        // Create the key for the entity data
        var serverEntityKey = new ServerEntityKey(
            playerData.CurrentScene,
            entityUpdate.Id
        );

        // Check with the created key whether we have an existing entry
        if (!_entityData.TryGetValue(serverEntityKey, out var entityData)) {
            // If the entry for this entity did not yet exist, we insert a new one
            entityData = new ServerEntityData();
            _entityData[serverEntityKey] = entityData;
        }

        var now = DateTime.UtcNow;
        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Position)) {
            SendDataInSameScene(
                id,
                playerData.CurrentScene,
                otherId => {
                    _netServer.GetUpdateManagerForClient(otherId)?.UpdateEntityPosition(
                        entityUpdate.Id,
                        entityUpdate.Position
                    );
                }
            );

            entityData.Position = entityUpdate.Position;
            entityData.HeardAt[HeardPosition] = now;
        }

        // Not kept for players who walk in later, unlike the position: this says how far the scene host has got
        // through what somebody else asked of the entity, which is nothing to a player who asked for none of it.
        // With two players there is only ever the one of them to send it to.
        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Anticipation)) {
            SendDataInSameScene(
                id,
                playerData.CurrentScene,
                otherId => {
                    _netServer.GetUpdateManagerForClient(otherId)?.UpdateEntityAnticipation(
                        entityUpdate.Id,
                        entityUpdate.Anticipation
                    );
                }
            );
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Scale)) {
            SendDataInSameScene(
                id,
                playerData.CurrentScene,
                otherId => {
                    _netServer.GetUpdateManagerForClient(otherId)?.UpdateEntityScale(
                        entityUpdate.Id,
                        entityUpdate.Scale
                    );
                }
            );

            entityData.Scale.Merge(entityUpdate.Scale);
            entityData.HeardAt[HeardScale] = now;
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Animation)) {
            SendDataInSameScene(
                id,
                playerData.CurrentScene,
                otherId => {
                    _netServer.GetUpdateManagerForClient(otherId)?.UpdateEntityAnimation(
                        entityUpdate.Id,
                        entityUpdate.AnimationId,
                        entityUpdate.AnimationWrapMode
                    );
                }
            );

            entityData.AnimationId = entityUpdate.AnimationId;
            entityData.AnimationWrapMode = entityUpdate.AnimationWrapMode;
            entityData.HeardAt[HeardClip] = now;
        }
    }

    /// <summary>
    /// Callback method for when a reliable entity update is received from a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="entityUpdate">The ReliableEntityUpdate packet data.</param>
    private void OnReliableEntityUpdate(ushort id, ReliableEntityUpdate entityUpdate) {
        if (!_fullSynchronisation) {
            return;
        }

        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received ReliableEntityUpdate data, but player with ID {id} is not in mapping");
            return;
        }

        // Create the key for the entity data
        var serverEntityKey = new ServerEntityKey(
            playerData.CurrentScene,
            entityUpdate.Id
        );

        // Check with the created key whether we have an existing entry
        if (!_entityData.TryGetValue(serverEntityKey, out var entityData)) {
            // If the entry for this entity did not yet exist, we insert a new one
            entityData = new ServerEntityData();
            _entityData[serverEntityKey] = entityData;
        }

        var now = DateTime.UtcNow;
        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Active)) {
            SendDataInSameScene(
                id,
                playerData.CurrentScene,
                otherId => {
                    _netServer.GetUpdateManagerForClient(otherId)?.UpdateEntityIsActive(
                        entityUpdate.Id,
                        entityUpdate.IsActive
                    );
                }
            );

            entityData.IsActive = entityUpdate.IsActive;
            entityData.HeardAt[HeardActive] = now;
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.Data)) {
            var filteredGenericData = new List<EntityNetworkData>(entityUpdate.GenericData.Count);

            foreach (var updateData in entityUpdate.GenericData) {
                updateData.SenderId = id;

                if (updateData.Type == EntityComponentType.Health &&
                    !playerData.IsSceneHost &&
                    IsHealingHealthUpdate(updateData)) {
                    Logger.Info(
                        $"Ignoring non-host health increase for entity {entityUpdate.Id} from player {id} in scene '{playerData.CurrentScene}'"
                    );
                    continue;
                }

                filteredGenericData.Add(updateData);
            }

            if (filteredGenericData.Count > 0) {
                SendDataInSameScene(
                    id,
                    playerData.CurrentScene,
                    otherId => {
                        _netServer.GetUpdateManagerForClient(otherId)?.AddEntityData(
                            entityUpdate.Id,
                            filteredGenericData
                        );
                    }
                );
            }

            if (filteredGenericData.Count > 0) {
                foreach (var updateData in filteredGenericData) {
                    if (updateData.Type > EntityComponentType.Death) {
                        KeepGenericData(entityData, updateData);
                        entityData.HeardAt[(int) updateData.Type] = now;
                    }
                }
            }

            // Helper to inspect an entity health packet and check if it represents a health increase.
            // Returns: True if the update heals the entity; otherwise false.
            static bool IsHealingHealthUpdate(EntityNetworkData updateData) {
                var packet = new Packet(updateData.Packet.ToArray());
                var previousHp = packet.ReadInt();
                var newHp = packet.ReadInt();
                return newHp > previousHp;
            }
        }

        if (entityUpdate.UpdateTypes.Contains(EntityUpdateType.HostFsm)) {
            foreach (var (fsmIndex, data) in entityUpdate.HostFsmData) {
                if (!entityData.HostFsmData.TryGetValue(fsmIndex, out var existingData)) {
                    existingData = new EntityHostFsmData();
                    entityData.HostFsmData[fsmIndex] = existingData;
                }

                existingData.MergeData(data);
                entityData.HeardAt[HeardFsmVariables + fsmIndex] = now;

                SendDataInSameScene(
                    id,
                    playerData.CurrentScene,
                    otherId => {
                        _netServer.GetUpdateManagerForClient(otherId)?.AddEntityHostFsmData(
                            entityUpdate.Id,
                            fsmIndex,
                            data
                        );
                    }
                );
            }
        }
    }

    /// <summary>
    /// Callback method for when a player disconnect is received.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    private void OnPlayerDisconnect(ushort id) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received PlayerDisconnect data, but player with ID {id} is not in mapping");
            return;
        }

        Logger.Info($"Received PlayerDisconnect data from ({id}, {playerData.Username})");

        ProcessPlayerDisconnect(id);
    }

    /// <summary>
    /// Internal method for disconnecting a player with the given ID for the given reason.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="reason">The reason for the disconnect.</param>
    public void InternalDisconnectPlayer(ushort id, DisconnectReason reason) {
        _netServer.GetUpdateManagerForClient(id)?.SetDisconnect(reason);

        ProcessPlayerDisconnect(id);
    }

    /// <summary>
    /// Handle a player leaving a scene by transition, disconnect or timeout.
    /// </summary>
    /// <param name="id">The ID of the player that left the scene.</param>
    /// <param name="disconnected">Whether the player disconnected from the server.</param>
    /// <param name="timeout">Whether the disconnect was due to connection timeout.</param>
    /// <param name="sceneName">The name of the scene that the player left (if known from a received packet), or null
    /// if no such name is known.</param>
    private void HandlePlayerLeaveScene(ushort id, bool disconnected, bool timeout = false, string? sceneName = null) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Handling player leave scene (dc: {disconnected}) for ID {id}, but player is not in mapping");
            return;
        }

        sceneName ??= playerData.CurrentScene;

        if (!disconnected && sceneName.Length == 0) {
            Logger.Warn($"Handling player leave scene for ID {id}, but there was no last scene registered");
            return;
        }

        Logger.Info($"Handling player leave scene (dc: {disconnected}) for ID {id}, left scene: {sceneName}");

        // If the current scene of the player is the one being left, we can set it to an empty string
        // It can happen that enter scene data arrives earlier than the leave scene packet, in which case this will
        // not be true, and we don't want to set the current scene (which is now already something else) to empty
        if (playerData.CurrentScene == sceneName) {
            playerData.CurrentScene = "";
            SendPlayerRoom(id, "");
        }

        var username = playerData.Username;

        // Keep track of whether the scene that the player has left is now empty
        var isSceneNowEmpty = true;
        var scenePlayers = new List<KeyValuePair<ushort, ServerPlayerData>>();
        foreach (var pair in _playerData) {
            if (pair.Key != id && pair.Value.CurrentScene == sceneName) {
                isSceneNowEmpty = false;
                scenePlayers.Add(pair);
            }
        }

        if (_fullSynchronisation && playerData.IsSceneHost && scenePlayers.Count > 0) {
            var epoch = GetNextSceneHostEpoch(sceneName);
            var newHostPair = scenePlayers[0];
            newHostPair.Value.IsSceneHost = true;
            playerData.IsSceneHost = false;

            Logger.Info(
                $"Player {id} left scene. Host transferred to {newHostPair.Key} in scene {sceneName} (epoch {epoch})"
            );

            foreach (var pair in scenePlayers) {
                var updateManager = _netServer.GetUpdateManagerForClient(pair.Key);
                bool isNewHost = (pair.Key == newHostPair.Key);
                updateManager?.SetSceneHostTransfer(sceneName, epoch, demote: !isNewHost);
            }
        }

        foreach (var pair in scenePlayers) {
            Logger.Info($"Sending leave scene packet to {pair.Key}");
            var updateManager = _netServer.GetUpdateManagerForClient(pair.Key);
            if (disconnected) {
                updateManager?.AddPlayerDisconnectData(id, username, timeout);
            } else {
                updateManager?.AddPlayerLeaveSceneData(id, sceneName);
            }
        }

        if (_fullSynchronisation) {
            // In case there were no other players to make scene host, we still need to reset the leaving
            // player's status of scene host
            playerData.IsSceneHost = false;

            // If the scene is now empty, we can remove all data from stored entities in that scene
            if (isSceneNowEmpty) {
                foreach (var keyDataPair in _entityData) {
                    if (keyDataPair.Key.Scene == sceneName) {
                        _entityData.TryRemove(keyDataPair.Key, out _);
                    }
                }
            }
        }

        if (disconnected) {
            // Now remove the client from the player data mapping
            _playerData.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Process a disconnect for the player with the given ID.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="timeout">Whether this player timed out or disconnected normally.</param>
    private void ProcessPlayerDisconnect(ushort id, bool timeout = false) {
        if (!timeout) {
            // If this isn't a timeout, then we need to propagate this packet to the NetServer
            _netServer.OnClientDisconnect(id);
        }

        if (!_playerData.TryGetValue(id, out var playerData)) {
            return;
        }

        var username = playerData.Username;

        foreach (var idPlayerDataPair in _playerData) {
            if (idPlayerDataPair.Key == id) {
                continue;
            }

            _netServer.GetUpdateManagerForClient(idPlayerDataPair.Key)?.AddPlayerDisconnectData(
                id,
                username,
                timeout
            );
        }

        // Taken out of the mapping by what is called next, and not here. Here was too early: the first thing it does
        // is look the player up, so it found nothing, said so and gave up - on every disconnect there has ever been.
        // What it gave up on was handing the scene host to whoever is left, telling the other player that this one
        // has left the room, and letting go of the entity data of a room that now has nobody in it.
        HandlePlayerLeaveScene(id, true, timeout);

        try {
            PlayerDisconnectEvent?.Invoke(playerData);
        } catch (Exception e) {
            Logger.Error($"Exception thrown while invoking PlayerDisconnect event:\n{e}");
        }
    }

    /// <summary>
    /// Callback method for when a player dies. 
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    private void OnPlayerDeath(ushort id) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received PlayerDeath data, but player with ID {id} is not in mapping");
            return;
        }

        Logger.Info($"Received PlayerDeath data from ({id}, {playerData.Username})");

        // if (ServerSaveData.IsSteelSoul()) {
        //     // We are running a Steel Soul save file, so we wipe the player-specific data for the player
        //     ServerSaveData.PlayerSaveData.Remove(playerData.AuthKey);
        //     
        //     Logger.Info("  Wiped player save data (Steel Soul)");
        // }

        if (_fullSynchronisation && playerData.IsSceneHost) {
            var sceneName = playerData.CurrentScene;
            var scenePlayers = new List<KeyValuePair<ushort, ServerPlayerData>>();
            foreach (var pair in _playerData) {
                if (pair.Key != id && pair.Value.CurrentScene == sceneName) {
                    scenePlayers.Add(pair);
                }
            }

            if (scenePlayers.Count > 0) {
                var epoch = GetNextSceneHostEpoch(sceneName);
                var newHostPair = scenePlayers[0];
                newHostPair.Value.IsSceneHost = true;
                playerData.IsSceneHost = false;
                playerData.LastHostedScene = sceneName;

                Logger.Info(
                    $"Player {id} ({playerData.Username}) died. Host transferred to {newHostPair.Key} ({newHostPair.Value.Username}) in scene {sceneName} (epoch {epoch})"
                );

                foreach (var pair in scenePlayers) {
                    var updateManager = _netServer.GetUpdateManagerForClient(pair.Key);
                    var isNewHost = (pair.Key == newHostPair.Key);
                    updateManager?.SetSceneHostTransfer(sceneName, epoch, demote: !isNewHost);
                }
            }
        }

        SendDataInSameScene(
            id,
            playerData.CurrentScene,
            otherId => { _netServer.GetUpdateManagerForClient(otherId)?.AddPlayerDeathData(id); }
        );
    }

    /// <summary>
    /// Keeps the data of a kind about an entity in place of what was kept of that kind before, for a player who walks in
    /// later and for the state of the room told again.
    /// </summary>
    /// <param name="entityData">What the server keeps of the entity.</param>
    /// <param name="updateData">The data.</param>
    private static void KeepGenericData(ServerEntityData entityData, EntityNetworkData updateData) {
        var existingData = entityData.GenericData.Find(d => d.Type == updateData.Type);
        if (existingData == null) {
            entityData.GenericData.Add(updateData.Clone());
        } else {
            existingData.Packet = new Packet(updateData.Packet.ToArray());
            existingData.SenderId = updateData.SenderId;
        }
    }

    /// <summary>
    /// The kinds of things heard of an entity besides its data, which go by their component type (see
    /// <see cref="ServerEntityData.HeardAt"/>): whether it is on, its clip, its scale, where it stands, and the variables
    /// of each FSM from this one on.
    /// </summary>
    private const int HeardActive = -1, HeardClip = -2, HeardScale = -3, HeardPosition = -4, HeardFsmVariables = 1000;

    /// <summary>
    /// How recently a kind of thing must have been heard as it happened for the state of a room to leave it be, in
    /// seconds (see <see cref="OnRoomSnapshot"/>).
    /// </summary>
    private const double RoomSnapshotYieldTime = 1.0;

    /// <summary>
    /// The shortest stretch without anything arriving from the player whose game runs a room, in seconds, after which
    /// that player is asked for the state of it (see <see cref="OnClientReceiveResumed"/>).
    /// </summary>
    private const double RoomResendGap = 3.0;

    /// <summary>
    /// The least time between two requests to the same player for the state of the room their game runs, in seconds.
    /// </summary>
    private const double RoomResendMinInterval = 5.0;

    /// <summary>
    /// When each player was last asked for the state of the room their game runs.
    /// </summary>
    private readonly ConcurrentDictionary<ushort, DateTime> _roomResendAskedAt = new();

    /// <summary>
    /// Callback for something arriving from a player after a stretch in which nothing did. What their game sent in it
    /// may never come, and for the room it runs that is what every other player sees of it, and what a player who walks
    /// in later is given: an enemy killed there stays alive for everyone else. So the player is asked for the state of
    /// the room (USER 10-10: "把反方向也做了"). The other way round - a player who missed what the server sent - is
    /// that player's to ask for (ClientManager.OnReceiveResumed).
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="gap">How long nothing arrived from them, in seconds.</param>
    private void OnClientReceiveResumed(ushort id, double gap) {
        if (gap < RoomResendGap || !_fullSynchronisation || !_playerData.TryGetValue(id, out var playerData) ||
            !playerData.IsSceneHost || string.IsNullOrEmpty(playerData.CurrentScene)) {
            return;
        }

        var now = DateTime.UtcNow;
        if (_roomResendAskedAt.TryGetValue(id, out var askedAt) && (now - askedAt).TotalSeconds < RoomResendMinInterval) {
            return;
        }

        _roomResendAskedAt[id] = now;
        Logger.Info(
            $"Nothing arrived from ({id}, {playerData.Username}) for {gap:F1}s, and their game runs " +
            $"'{playerData.CurrentScene}', so they are asked for the state of it"
        );
        _netServer.GetUpdateManagerForClient(id)?.SetRoomResendRequest();
    }

    /// <summary>
    /// Callback for the state of a room from the game that runs it, which the server asked for (see
    /// <see cref="OnClientReceiveResumed"/>). It is kept in place of what the server had of the room, except what was
    /// heard as it happened just before: that came in the same packet and was taken in first, since a packet's contents
    /// are taken in in the order of their kind, so it is the newer of the two. The room's other players are then told
    /// the state of the room again, which they take as a correction (ClientManager.OnRoomState).
    /// </summary>
    /// <param name="id">The ID of the player whose game runs the room.</param>
    /// <param name="snapshot">The state of the room.</param>
    private void OnRoomSnapshot(ushort id, ClientPlayerAlreadyInScene snapshot) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            return;
        }

        var sceneName = playerData.CurrentScene;
        if (string.IsNullOrEmpty(sceneName) || snapshot.SceneName != sceneName || !playerData.IsSceneHost) {
            Logger.Info(
                $"The state of '{snapshot.SceneName}' came from ({id}, {playerData.Username}), who " +
                (snapshot.SceneName != sceneName ? $"is in '{sceneName}' now" : "does not run it any more") +
                ", so it is not kept"
            );
            return;
        }

        var now = DateTime.UtcNow;

        bool HeardJustNow(ServerEntityData data, int kind) =>
            data.HeardAt.TryGetValue(kind, out var at) && (now - at).TotalSeconds < RoomSnapshotYieldTime;

        // Health is the exception: what the other players' games send of it is what their copies took from their own
        // hits, which go on while the creature they show is already dead in the game that runs it; and nothing heard
        // is newer than a death
        bool YieldsTo(ServerEntityData data, EntityNetworkData genericData) {
            if (!HeardJustNow(data, (int) genericData.Type)) {
                return false;
            }

            if (genericData.Type != EntityComponentType.Health) {
                return true;
            }

            var packet = new Packet(genericData.Packet.ToArray());
            packet.ReadInt();
            return packet.ReadInt() > 0 &&
                   data.GenericData.Find(kept => kept.Type == EntityComponentType.Health)?.SenderId == id;
        }

        ServerEntityData DataOf(ushort entityId) {
            var key = new ServerEntityKey(sceneName, entityId);
            if (!_entityData.TryGetValue(key, out var data)) {
                data = new ServerEntityData();
                _entityData[key] = data;
            }

            return data;
        }

        foreach (var spawn in snapshot.EntitySpawnList) {
            var data = DataOf(spawn.Id);
            data.Spawned = true;
            data.SpawningType = spawn.SpawningType;
            data.SpawnedType = spawn.SpawnedType;
        }

        foreach (var update in snapshot.EntityUpdateList) {
            var data = DataOf(update.Id);
            if (update.UpdateTypes.Contains(EntityUpdateType.Position) && !HeardJustNow(data, HeardPosition)) {
                data.Position = update.Position;
            }

            if (update.UpdateTypes.Contains(EntityUpdateType.Scale) && !HeardJustNow(data, HeardScale)) {
                data.Scale.Merge(update.Scale);
            }

            if (update.UpdateTypes.Contains(EntityUpdateType.Animation) && !HeardJustNow(data, HeardClip)) {
                data.AnimationId = update.AnimationId;
                data.AnimationWrapMode = update.AnimationWrapMode;
            }
        }

        foreach (var update in snapshot.ReliableEntityUpdateList) {
            var data = DataOf(update.Id);
            if (update.UpdateTypes.Contains(EntityUpdateType.Active) && !HeardJustNow(data, HeardActive)) {
                data.IsActive = update.IsActive;
            }

            if (update.UpdateTypes.Contains(EntityUpdateType.Data)) {
                foreach (var genericData in update.GenericData) {
                    if (genericData.Type > EntityComponentType.Death && !YieldsTo(data, genericData)) {
                        genericData.SenderId = id;
                        KeepGenericData(data, genericData);
                    }
                }
            }

            if (update.UpdateTypes.Contains(EntityUpdateType.HostFsm)) {
                foreach (var (fsmIndex, fsmData) in update.HostFsmData) {
                    if (HeardJustNow(data, HeardFsmVariables + fsmIndex)) {
                        continue;
                    }

                    if (!data.HostFsmData.TryGetValue(fsmIndex, out var kept)) {
                        kept = new EntityHostFsmData();
                        data.HostFsmData[fsmIndex] = kept;
                    }

                    kept.MergeData(fsmData);
                }
            }
        }

        var told = 0;
        foreach (var otherPlayerData in _playerData.Values) {
            if (otherPlayerData.Id != id && otherPlayerData.CurrentScene == sceneName) {
                OnClientEnterScene(otherPlayerData, roomState: true);
                told++;
            }
        }

        Logger.Info(
            $"Kept the state of '{sceneName}' from ({id}, {playerData.Username}): " +
            $"{snapshot.ReliableEntityUpdateList.Count} entities, {snapshot.EntitySpawnList.Count} spawned; told " +
            $"{told} other players"
        );
    }

    /// <summary>
    /// When each player was last told the state of their room again (see <see cref="OnRoomStateRequest"/>).
    /// </summary>
    private readonly ConcurrentDictionary<ushort, DateTime> _roomStateToldAt = new();

    /// <summary>
    /// The least time between two answers to the same player's requests for the state of their room, in seconds.
    /// </summary>
    private const double RoomStateMinInterval = 3.0;

    /// <summary>
    /// Callback for when a player who has been in their room all along asks for its state, after a stretch in which
    /// nothing reached them. What was sent in it may never come: the death of an enemy, a creature switched off, the
    /// other player leaving. The answer holds what the answer to entering the room holds, and decides nothing over.
    /// </summary>
    /// <param name="id">The ID of the player asking.</param>
    private void OnRoomStateRequest(ushort id) {
        if (!_playerData.TryGetValue(id, out var playerData) || string.IsNullOrEmpty(playerData.CurrentScene)) {
            return;
        }

        var now = DateTime.UtcNow;
        if (_roomStateToldAt.TryGetValue(id, out var toldAt) && (now - toldAt).TotalSeconds < RoomStateMinInterval) {
            return;
        }

        _roomStateToldAt[id] = now;
        OnClientEnterScene(playerData, roomState: true);
    }

    /// <summary>
    /// Callback for when a player asks to be told again who and what is in the room they are in, because what was
    /// sent when they entered it never reached them.
    /// </summary>
    /// <param name="id">The ID of the player asking.</param>
    private void OnSceneResyncRequest(ushort id) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Asked to tell player with ID {id} about their room again, but they are not in mapping");

            return;
        }

        if (string.IsNullOrEmpty(playerData.CurrentScene)) {
            return;
        }

        Logger.Info(
            $"Telling ({id}, {playerData.Username}) what is in '{playerData.CurrentScene}' again, because what was " +
            "sent when they entered it never reached them"
        );

        OnClientEnterScene(playerData, true);
    }

    /// <summary>
    /// Callback method for when a player rests at a bench or dies, which respawns semi-persistent objects such as
    /// enemies. Every other player respawns them too, wherever they are, so that all players keep the same world.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    private void OnSemiPersistentReset(ushort id) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received SemiPersistentReset data, but player with ID {id} is not in mapping");
            return;
        }

        Logger.Info($"Received SemiPersistentReset data from ({id}, {playerData.Username})");

        foreach (var otherId in _playerData.Keys) {
            if (otherId != id) {
                _netServer.GetUpdateManagerForClient(otherId)?.AddSemiPersistentResetData(id);
            }
        }
    }

    /// <summary>
    /// Callback method for when a player sends the state of an arena, or asks the scene host to start its battle. It
    /// goes to the other players in the scene of the arena.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="update">The BattleSceneUpdate packet data.</param>
    private void OnBattleSceneUpdate(ushort id, BattleSceneUpdate update) {
        if (!_playerData.ContainsKey(id)) {
            Logger.Warn($"Received BattleSceneUpdate data, but player with ID {id} is not in mapping");
            return;
        }

        SendDataInSameScene(
            id,
            update.SceneName,
            otherId => _netServer.GetUpdateManagerForClient(otherId)?.AddBattleSceneUpdateData(update)
        );
    }

    /// <summary>
    /// Callback method for when a player sends something that happened in a room whose gates are closed by an FSM. It
    /// goes to the other players in the scene of the room, or to all other players when the player waits for them or
    /// for a story scene that the game sets in several rooms.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="update">The BossRoomUpdate packet data.</param>
    private void OnBossRoomUpdate(ushort id, BossRoomUpdate update) {
        if (!_playerData.ContainsKey(id)) {
            Logger.Warn($"Received BossRoomUpdate data, but player with ID {id} is not in mapping");
            return;
        }

        update.PlayerId = id;

        if (update.Kind is BossRoomUpdateKind.Waiting or BossRoomUpdateKind.WaitingAtStory or
            BossRoomUpdateKind.StartedStory or BossRoomUpdateKind.LeftStory) {
            foreach (var otherId in _playerData.Keys) {
                if (otherId != id) {
                    _netServer.GetUpdateManagerForClient(otherId)?.AddBossRoomUpdateData(update);
                }
            }

            return;
        }

        SendDataInSameScene(
            id,
            update.SceneName,
            otherId => _netServer.GetUpdateManagerForClient(otherId)?.AddBossRoomUpdateData(update)
        );
    }

    /// <summary>
    /// Callback method for when a player sends an update of a two-player save, which goes to the player it names.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="update">The CoopSaveUpdate packet data.</param>
    private void OnCoopSaveUpdate(ushort id, CoopSaveUpdate update) {
        if (!_playerData.ContainsKey(id)) {
            Logger.Warn($"Received CoopSaveUpdate data, but player with ID {id} is not in mapping");
            return;
        }

        if (update.TargetId == id || !_playerData.ContainsKey(update.TargetId)) {
            return;
        }

        update.PlayerId = id;
        _netServer.GetUpdateManagerForClient(update.TargetId)?.AddCoopSaveUpdateData(update);
    }

    /// <summary>
    /// Callback method for when a player sends a hit in a two-player save, which goes to the player it names.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="update">The CoopHitUpdate packet data.</param>
    private void OnCoopHitUpdate(ushort id, CoopHitUpdate update) {
        if (!_playerData.ContainsKey(id)) {
            Logger.Warn($"Received CoopHitUpdate data, but player with ID {id} is not in mapping");
            return;
        }

        if (update.TargetId == id || !_playerData.ContainsKey(update.TargetId)) {
            return;
        }

        update.PlayerId = id;
        _netServer.GetUpdateManagerForClient(update.TargetId)?.AddCoopHitUpdateData(update);
    }

    /// <summary>
    /// Callback method for when a player sends a comparison of the state of the room in a two-player save, which goes
    /// to the player it names.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="update">The CoopCheckUpdate packet data.</param>
    private void OnCoopCheckUpdate(ushort id, CoopCheckUpdate update) {
        if (!_playerData.ContainsKey(id)) {
            Logger.Warn($"Received CoopCheckUpdate data, but player with ID {id} is not in mapping");
            return;
        }

        if (update.TargetId == id || !_playerData.ContainsKey(update.TargetId)) {
            return;
        }

        update.PlayerId = id;
        _netServer.GetUpdateManagerForClient(update.TargetId)?.AddCoopCheckUpdateData(update);
    }

    /// <summary>
    /// Try to update the team for the player with the given ID.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="team">The team to change the player to.</param>
    /// <param name="reason">The reason if the team could not be updated, otherwise null.</param>
    /// <returns>True if the player's team was updated, false otherwise.</returns>
    public bool TryUpdatePlayerTeam(ushort id, Team team, [MaybeNullWhen(true)] out string reason) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received PlayerTeamUpdate data, but player with ID {id} is not in mapping");

            reason = "Could not find player";
            return false;
        }

        Logger.Info($"Received PlayerTeamUpdate data from ({id}, {playerData.Username}) for team: {team}");

        if (!ServerSettings.TeamsEnabled) {
            Logger.Info("  Teams are not enabled, won't update team");

            reason = "Unable to change team";
            return false;
        }

        if (playerData.Team == team) {
            Logger.Info("  Team is the same as current, won't update team");

            reason = "Already in team";
            return false;
        }

        // Update the team in the player data
        playerData.Team = team;

        // Broadcast the packet to all players except the player we received the update from
        foreach (var playerId in _playerData.Keys) {
            if (id == playerId) {
                _netServer.GetUpdateManagerForClient(playerId)?.AddPlayerSettingUpdateData(team: team);
                continue;
            }

            _netServer.GetUpdateManagerForClient(playerId)?.AddOtherPlayerSettingUpdateData(
                id,
                team: team
            );
        }

        PlayerTeamChangedEvent?.Invoke(playerData, team);

        reason = null;
        return true;
    }

    /// <summary>
    /// Try to update the skin for the player with the given ID.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="skinId">The ID of the skin to change the player to.</param>
    /// <param name="reason">The reason if the skin could not be updated, otherwise null.</param>
    /// <returns>True if the player's team was updated, false otherwise.</returns>
    public bool TryUpdatePlayerSkin(ushort id, byte skinId, [MaybeNullWhen(true)] out string reason) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Warn($"Received PlayerSkinUpdate data, but player with ID {id} is not in mapping");

            reason = "Could not find player";
            return false;
        }

        Logger.Info($"Received PlayerSkinUpdate data from ({id}, {playerData.Username}) for skin ID: {skinId}");

        if (!ServerSettings.AllowSkins) {
            Logger.Info("  Skins are not allowed, won't update skin");

            reason = "Unable to change skin";
            return false;
        }

        if (playerData.SkinId == skinId) {
            Logger.Info("  Skins is the same as current, won't update skin");

            reason = "Skin is already in use";
            return false;
        }

        // Update the skin ID in the player data
        playerData.SkinId = skinId;

        foreach (var idPlayerDataPair in _playerData) {
            var otherId = idPlayerDataPair.Key;

            if (otherId == id) {
                _netServer.GetUpdateManagerForClient(id)?.AddPlayerSettingUpdateData(skinId: skinId);
                continue;
            }

            var otherPd = idPlayerDataPair.Value;

            // Skip sending skin to players not in the same scene
            if (!string.Equals(otherPd.CurrentScene, playerData.CurrentScene)) {
                continue;
            }

            _netServer.GetUpdateManagerForClient(otherId)?.AddOtherPlayerSettingUpdateData(id, skinId: skinId);
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Callback method for when the server is shut down.
    /// </summary>
    private void OnServerShutdown() {
        // Clear all existing player data
        _playerData.Clear();

        try {
            ServerShutdownEvent?.Invoke();
        } catch (Exception e) {
            Logger.Error($"Exception thrown while invoking ServerShutdown event:\n{e}");
        }
    }

    /// <summary>
    /// Handle a login request for a client that has invalid addons.
    /// </summary>
    /// <param name="serverInfo">The server info instance to send to the client containing the invalid addons
    /// result.</param>
    private void HandleInvalidLoginAddons(ServerInfo serverInfo) {
        serverInfo.ConnectionResult = ServerConnectionResult.InvalidAddons;
        serverInfo.AddonData.AddRange(AddonManager.GetNetworkedAddonData());
    }

    /// <summary>
    /// Method for handling a login request for a new client.
    /// </summary>
    /// <param name="netServerClient">The net server client that tries to connect.</param>
    /// <param name="clientInfo">The information from the client.</param>
    /// <param name="serverInfo">The server info instance to modify based on whether the client should be accepted
    /// or not.</param>
    private void OnConnectionRequest(NetServerClient netServerClient, ClientInfo clientInfo, ServerInfo serverInfo) {
        var clientDisplayString = netServerClient.TransportClient.ToDisplayString();
        Logger.Info($"Received connection request from {clientDisplayString}, username: {clientInfo.Username}");

        // Get the unique identifier (IP address for UDP, Steam ID for Steam clients)
        var uniqueIdentifier = netServerClient.TransportClient.GetUniqueIdentifier();

        // Check if the unique identifier is banned (supports both IPEndPoint and SteamID)
        if (_banList.IsIpBanned(uniqueIdentifier)) {
            var displayType = IPAddress.TryParse(uniqueIdentifier, out _) ? "IP" : "Steam ID";
            Logger.Debug($"  Client is banned from the server ({displayType}), rejected connection");

            serverInfo.ConnectionResult = ServerConnectionResult.RejectedOther;
            serverInfo.ConnectionRejectedMessage = "Banned from the server";
            return;
        }

        if (_banList.Contains(clientInfo.AuthKey)) {
            Logger.Debug("  Client is banned from the server (AuthKey), rejected connection");

            serverInfo.ConnectionResult = ServerConnectionResult.RejectedOther;
            serverInfo.ConnectionRejectedMessage = "Banned from the server";
            return;
        }

        if (_whiteList.IsEnabled) {
            if (!_whiteList.Contains(clientInfo.AuthKey)) {
                if (!_whiteList.IsPreListed(clientInfo.Username)) {
                    Logger.Debug("  Client is not whitelisted on the server, rejected connection");

                    serverInfo.ConnectionResult = ServerConnectionResult.RejectedOther;
                    serverInfo.ConnectionRejectedMessage = "Not whitelisted on the server";
                    return;
                }

                Logger.Info("  Username was pre-listed, auth key has been added to whitelist");

                _whiteList.Add(clientInfo.AuthKey);
                _whiteList.RemovePreList(clientInfo.Username);
            }
        }

        // Check whether the username is valid
        if (clientInfo.Username.Length > MaxUsernameLength) {
            Logger.Debug("  Client has username that is too long, rejected connection");

            serverInfo.ConnectionResult = ServerConnectionResult.RejectedOther;
            serverInfo.ConnectionRejectedMessage = "Invalid username";
            return;
        }

        foreach (var character in clientInfo.Username) {
            if (!char.IsLetterOrDigit(character)) {
                Logger.Debug("  Client has invalid characters in username, rejected connection");

                serverInfo.ConnectionResult = ServerConnectionResult.RejectedOther;
                serverInfo.ConnectionRejectedMessage = "Invalid username";
                return;
            }
        }

        // If this is the same player reconnecting, clear the old session before we evaluate username collisions
        ReplaceExistingSessionIfPresent(netServerClient, clientInfo, uniqueIdentifier);

        // Check whether the username is not already in use
        foreach (var existingPlayerData in _playerData.Values) {
            if (existingPlayerData.Username.ToLower().Equals(clientInfo.Username.ToLower())) {
                Logger.Debug("  Client username is already in use, rejected connection");

                serverInfo.ConnectionResult = ServerConnectionResult.RejectedOther;
                serverInfo.ConnectionRejectedMessage = "Username already in use";
                return;
            }
        }

        var addonData = clientInfo.AddonData;
        if (addonData == null) {
            Logger.Warn("  Addon data was null for client");

            serverInfo.ConnectionResult = ServerConnectionResult.RejectedOther;
            serverInfo.ConnectionRejectedMessage = "Internal error";
            return;
        }

        // Construct a string that contains all addons and respective versions by mapping the items in the addon data
        var addonStringList = string.Join(", ", addonData.Select(addon => $"{addon.Identifier} v{addon.Version}"));
        Logger.Info($"  Client tries to connect with following addons: {addonStringList}");

        // If there is a mismatch between the number of networked addons of the client and the server,
        // we can immediately invalidate the request
        if (addonData.Count != AddonManager.GetNetworkedAddonData().Count) {
            Logger.Debug("  Client addons are invalid, rejected connection");

            HandleInvalidLoginAddons(serverInfo);
            return;
        }

        // Create a byte list denoting the order of the addons on the server
        var addonOrder = new List<byte>();

        foreach (var addon in addonData) {
            // Check and retrieve the server addon with the same name and version
            if (!AddonManager.TryGetNetworkedAddon(
                    addon.Identifier,
                    addon.Version,
                    out var correspondingServerAddon
                )) {
                Logger.Debug("  Client addons are invalid, rejected connection");

                // There was no corresponding server addon, so we send a login response with an invalid status
                // and the addon data that is present on the server, so the client knows what is invalid
                HandleInvalidLoginAddons(serverInfo);
                return;
            }

            if (!correspondingServerAddon.Id.HasValue) {
                continue;
            }

            // If the addon is also present on the server, we append the addon order with the correct index
            addonOrder.Add(correspondingServerAddon.Id.Value);
        }

        Logger.Debug("  Accepting client connection, preparing server info");

        // Finally after all the checks, the client is accepted, and we note that in the server info
        serverInfo.ConnectionResult = ServerConnectionResult.Accepted;
        serverInfo.AddonOrder = addonOrder.ToArray();

        serverInfo.ServerSettingsUpdate = new ServerSettingsUpdate {
            ServerSettings = InternalServerSettings
        };

        serverInfo.FullSynchronisation = _fullSynchronisation;

        // Construct the player info to send to the new client in the server info
        var playerInfo = new List<ServerInfo.PlayerInfo>();

        foreach (var idPlayerDataPair in _playerData) {
            var otherId = idPlayerDataPair.Key;
            if (otherId == netServerClient.Id) {
                continue;
            }

            var otherPd = idPlayerDataPair.Value;

            playerInfo.Add(
                new ServerInfo.PlayerInfo {
                    Id = otherId,
                    Username = otherPd.Username,
                    Team = otherPd.Team,
                    SkinId = otherPd.SkinId,
                    CrestType = otherPd.CrestType,
                    SaveKey = AuthUtil.GetSaveKey(otherPd.AuthKey),
                    Room = otherPd.CurrentScene
                }
            );

            // Send to the other players that this client has just connected
            _netServer.GetUpdateManagerForClient(otherId)?.AddPlayerConnectData(
                netServerClient.Id,
                clientInfo.Username,
                AuthUtil.GetSaveKey(clientInfo.AuthKey)
            );
        }

        serverInfo.PlayerInfos = playerInfo;

        // if (FullSynchronisation) {
        //     // Obtain the save data for the connecting client and add it to the server info
        //     serverInfo.CurrentSave = ServerSaveData.GetCurrentSaveData(clientInfo.AuthKey);
        // }

        // Create new player data and store it
        var playerData = new ServerPlayerData(
            netServerClient.Id,
            uniqueIdentifier,
            clientInfo.Username,
            clientInfo.AuthKey,
            _authorizedList
        );
        _playerData[netServerClient.Id] = playerData;

        try {
            PlayerConnectEvent?.Invoke(playerData);
        } catch (Exception e) {
            Logger.Error($"Exception thrown while invoking PlayerConnect event:\n{e}");
        }
    }

    /// <summary>
    /// If the connecting client matches an active player identity, disconnect the old session so the new
    /// transport can take over cleanly.
    /// </summary>
    /// <param name="netServerClient">The connecting net server client.</param>
    /// <param name="clientInfo">The connection info for the new session.</param>
    /// <param name="uniqueIdentifier">The transport-level unique identifier for the new session.</param>
    private void ReplaceExistingSessionIfPresent(
        NetServerClient netServerClient,
        ClientInfo clientInfo,
        string uniqueIdentifier
    ) {
        var existingPlayer = _playerData.Values.FirstOrDefault(playerData =>
            playerData.Id != netServerClient.Id
            && (string.Equals(playerData.UniqueClientIdentifier, uniqueIdentifier, StringComparison.OrdinalIgnoreCase)
                || string.Equals(playerData.AuthKey, clientInfo.AuthKey, StringComparison.OrdinalIgnoreCase))
        );

        if (existingPlayer is null)
            return;

        Logger.Warn(
            $"Replacing existing session for player '{existingPlayer.Username}' " +
            $"(ID {existingPlayer.Id}) with new connection from {uniqueIdentifier}"
        );

        ProcessPlayerDisconnect(existingPlayer.Id);
    }

    /// <summary>
    /// Callback method for when a client times out.
    /// </summary>
    /// <param name="id">The ID of the client.</param>
    private void OnClientTimeout(ushort id) {
        if (!_playerData.TryGetValue(id, out _)) {
            Logger.Debug($"Received timeout from unknown player with ID: {id}");
            return;
        }

        // Since the client has timed out, we can formally disconnect them
        ProcessPlayerDisconnect(id, true);
    }

    /// <summary>
    /// Execute a given action by passing the ID of each player that is in the same scene as the given
    /// scene name except for the source ID.
    /// </summary>
    /// <param name="sourceId">The ID of the source player.</param>
    /// <param name="sceneName">The name of the scene to send to.</param>
    /// <param name="dataAction">The action to execute with each ID.</param>
    private void SendDataInSameScene(ushort sourceId, string sceneName, Action<ushort> dataAction) {
        foreach (var idPlayerDataPair in _playerData) {
            // Skip sending to same ID
            if (idPlayerDataPair.Key == sourceId) {
                continue;
            }

            var otherPd = idPlayerDataPair.Value;

            // Skip sending to players not in the same scene
            if (!string.Equals(otherPd.CurrentScene, sceneName)) {
                continue;
            }

            dataAction(idPlayerDataPair.Key);
        }
    }

    /// <summary>
    /// Try and process a given message by a given command sender as a command.
    /// </summary>
    /// <param name="commandSender">The command sender that sent the message.</param>
    /// <param name="message">The message that was sent.</param>
    /// <returns>true if the message was processed as a command, false otherwise.</returns>
    public bool TryProcessCommand(ICommandSender commandSender, string message) {
        return CommandManager.ProcessCommand(commandSender, message);
    }

    /// <summary>
    /// Callback method for when a chat message is received from a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="chatMessage">The ChatMessage packet data.</param>
    private void OnChatMessage(ushort id, ChatMessage chatMessage) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Debug($"Could not process chat message from unknown player ID: {id}");
            return;
        }

        Logger.Info($"Chat from ({id}, {playerData.Username}): \"{chatMessage.Message}\"");

        if (TryProcessCommand(
                new PlayerCommandSender(
                    _authorizedList.Contains(playerData.AuthKey),
                    id,
                    _netServer.GetUpdateManagerForClient(id)
                ),
                chatMessage.Message
            )) {
            Logger.Debug("Chat message was processed as command");
            return;
        }

        var playerChatEvent = new PlayerChatEvent(playerData, chatMessage.Message);

        try {
            PlayerChatEvent?.Invoke(playerChatEvent);
        } catch (Exception e) {
            Logger.Error($"Exception thrown while invoking PlayerChat event:\n{e}");
        }

        // If the event has been cancelled, we don't proceed with sending the chat message to other players
        if (playerChatEvent.Cancelled) {
            return;
        }

        var messages = playerChatEvent.Message.Split('\n');
        foreach (var message in messages) {
            var formattedMsg = $"[{playerData.Username}]: {message}";

            foreach (var idPlayerDataPair in _playerData) {
                _netServer.GetUpdateManagerForClient(idPlayerDataPair.Key)?.AddChatMessage(formattedMsg);
            }
        }
    }

    /// <summary>
    /// Callback method for when a server settings update is received from a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="serverSettingsUpdate">The <see cref="ServerSettingsUpdate"/> packet data.</param>
    private void OnServerSettingsUpdate(ushort id, ServerSettingsUpdate serverSettingsUpdate) {
        if (!_playerData.TryGetValue(id, out var playerData)) {
            Logger.Debug($"Could not process server settings update from unknown player ID: {id}");
            return;
        }

        Logger.Info($"Received server settings update from ({id}, {playerData.Username})");

        if (!playerData.IsAuthorized) {
            Logger.Info("  Player is not authorized");

            SendMessage(id, "You are not authorized to change server settings");
            _netServer.GetUpdateManagerForClient(id)?.UpdateServerSettings(InternalServerSettings);

            return;
        }

        InternalServerSettings.SetAllProperties(serverSettingsUpdate.ServerSettings);
        OnUpdateServerSettings();
    }

    /// <summary>
    /// Callback method for when a player setting update is received from a player. This can include changes to their
    /// team, skin, etc.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="playerSettingUpdate">The <see cref="ServerPlayerSettingUpdate"/> packet data.</param>
    private void OnPlayerSettingUpdate(ushort id, ServerPlayerSettingUpdate playerSettingUpdate) {
        if (playerSettingUpdate.UpdateTypes.Contains(PlayerSettingUpdateType.Team)) {
            if (TryUpdatePlayerTeam(id, playerSettingUpdate.Team, out var reason)) {
                SendMessage(id, $"Team changed to '{playerSettingUpdate.Team}'");
            } else {
                SendMessage(id, reason);
            }
        }

        if (playerSettingUpdate.UpdateTypes.Contains(PlayerSettingUpdateType.Skin)) {
            if (TryUpdatePlayerSkin(id, playerSettingUpdate.SkinId, out var reason)) {
                SendMessage(id, $"Skin ID changed to '{playerSettingUpdate.SkinId}'");
            } else {
                SendMessage(id, reason);
            }
        }

        if (playerSettingUpdate.UpdateTypes.Contains(PlayerSettingUpdateType.Crest)) {
            if (!_playerData.TryGetValue(id, out var playerData)) {
                Logger.Warn($"Received crest update, but player with ID {id} is not in mapping");
                return;
            }

            var crestType = playerSettingUpdate.CrestType;

            Logger.Info(
                $"Received crest update for player: ({id}, {playerData.Username}), from '{playerData.CrestType}' to '{crestType}'"
            );

            playerData.CrestType = crestType;

            foreach (var otherId in _playerData.Keys) {
                if (otherId == id) {
                    continue;
                }

                _netServer.GetUpdateManagerForClient(otherId)
                          ?.AddOtherPlayerSettingUpdateData(id, crestType: crestType);
            }
        }
    }

    /// <summary>
    /// Callback method for when a save update is received from a player.
    /// </summary>
    /// <param name="id">The ID of the player.</param>
    /// <param name="packet">The SaveUpdate packet data.</param>
    protected virtual void OnSaveUpdate(ushort id, SaveUpdate packet) {
        // if (!FullSynchronisation) {
        //     return;
        // }
        //
        // if (!_playerData.TryGetValue(id, out var playerData)) {
        //     Logger.Debug($"Could not process save update from unknown player ID: {id}");
        //     return;
        // }
        //
        // Logger.Info($"Save update from ({id}, {playerData.Username}), index: {packet.SaveDataIndex}");
        //
        // // Find the properties for syncing this save update, based on whether it is a geo rock, player data or 
        // // persistent bool/int item
        // SaveDataMapping.VarProperties varProps;
        // string? pdVarName = null;
        // if (SaveDataMapping.Instance.GeoRockIndices.TryGetValue(packet.SaveDataIndex, out var persistentItemData)) {
        //     Logger.Debug($"  Found GeoRockData: {persistentItemData.Id}, {persistentItemData.SceneName}");
        //     
        //     if (!SaveDataMapping.Instance.GeoRockBools.TryGetValue(persistentItemData, out _)) {
        //         return;
        //     }
        //
        //     varProps = new SaveDataMapping.VarProperties {
        //         Sync = true,
        //         SyncType = SaveDataMapping.SyncType.Server,
        //         IgnoreSceneHost = false
        //     };
        // } else if (SaveDataMapping.Instance.PlayerDataIndices.TryGetValue(packet.SaveDataIndex, out pdVarName)) {
        //     Logger.Debug($"  Found PlayerData: {pdVarName}");
        //     
        //     if (!SaveDataMapping.Instance.PlayerDataVarProperties.TryGetValue(pdVarName, out varProps)) {
        //         return;
        //     }
        // } else if (SaveDataMapping.Instance.PersistentBoolIndices.TryGetValue(
        //     packet.SaveDataIndex, 
        //     out persistentItemData)
        // ) {
        //     Logger.Debug($"  Found PersistentBoolData: {persistentItemData.Id}, {persistentItemData.SceneName}");
        //     
        //     if (!SaveDataMapping.Instance.PersistentBoolVarProperties.TryGetValue(persistentItemData, out varProps))
        // {
        //         return;
        //     }
        // } else if (SaveDataMapping.Instance.PersistentIntIndices.TryGetValue(
        //     packet.SaveDataIndex, 
        //     out persistentItemData)
        // ) {
        //     Logger.Debug($"  Found PersistentIntData: {persistentItemData.Id}, {persistentItemData.SceneName}");
        //     
        //     if (!SaveDataMapping.Instance.PersistentIntVarProperties.TryGetValue(persistentItemData, out varProps)) {
        //         return;
        //     }
        // } else {
        //     Logger.Debug("  Could not find sync props for save update");
        //     return;
        // }
        //
        // // Check whether this save update requires the player to be scene host and do the check for it
        // if (!varProps.IgnoreSceneHost && !playerData.IsSceneHost) {
        //     Logger.Debug("  Player is not scene host, but should be for update, not broadcasting");
        //     return;
        // }
        //
        // if (varProps.SyncType == SaveDataMapping.SyncType.Player) {
        //     Logger.Debug("  SyncType is Player");
        //     
        //     if (!ServerSaveData.PlayerSaveData.TryGetValue(playerData.AuthKey, out var playerSaveData)) {
        //         Logger.Debug("  No PlayerSaveData for player yet, creating one");
        //         playerSaveData = new Dictionary<ushort, byte[]>();
        //         ServerSaveData.PlayerSaveData[playerData.AuthKey] = playerSaveData;
        //     }
        //     
        //     Logger.Debug("  Storing player data");
        //
        //     playerSaveData[packet.SaveDataIndex] = packet.Value;
        // } else if (varProps.SyncType == SaveDataMapping.SyncType.Server) {
        //     if (varProps.Additive) {
        //         if (pdVarName == null) {
        //             Logger.Debug("  Cannot decode value, name for variable is null");
        //             return;
        //         }
        //
        //         object? decodedCurrentValue = null;
        //         var decodedDeltaValue = EncodeUtil.DecodeSaveDataValue(pdVarName, packet.Value);
        //
        //         if (!ServerSaveData.GlobalSaveData.TryGetValue(packet.SaveDataIndex, out var currentValue)) {
        //             Logger.Debug($"No current value is stored in the global save data for: {pdVarName}");
        //
        //             if (varProps.InitialValue != null) {
        //                 Logger.Debug($"  Taking initial value: {varProps.InitialValue}");
        //                 decodedCurrentValue = varProps.InitialValue;
        //             } else {
        //                 Logger.Debug("  No initial value defined, using delta as absolute");
        //                 packet.Value = EncodeUtil.EncodeSaveDataValue(decodedDeltaValue);
        //             }
        //         } else {
        //             decodedCurrentValue = EncodeUtil.DecodeSaveDataValue(pdVarName, currentValue);
        //         }
        //
        //         if (decodedCurrentValue != null) {
        //             object? decodedNewValue;
        //
        //             if (decodedCurrentValue is int decodedCurrentInt && decodedDeltaValue is int decodedDeltaInt) {
        //                 decodedNewValue = decodedCurrentInt + decodedDeltaInt;
        //             } else if (decodedCurrentValue is List<string> decodedCurrentStringList &&
        //                        decodedDeltaValue is List<string> decodedDeltaStringList) {
        //
        //                 // Loop over the delta list and add only non-duplicates
        //                 foreach (var str in decodedDeltaStringList) {
        //                     if (!decodedCurrentStringList.Contains(str)) {
        //                         decodedCurrentStringList.Add(str);
        //                     }
        //                 }
        //
        //                 decodedNewValue = decodedCurrentStringList;
        //             } else {
        //                 Logger.Debug($"  Type of decoded values did not match: {decodedCurrentValue.GetType()}");
        //                 return;
        //             }
        //
        //             packet.Value = EncodeUtil.EncodeSaveDataValue(decodedNewValue);
        //         }
        //     }
        //     
        //     Logger.Debug("  SyncType is Server, broadcasting save update");
        //     
        //     ServerSaveData.GlobalSaveData[packet.SaveDataIndex] = packet.Value;
        //     
        //     foreach (var idPlayerDataPair in _playerData) {
        //         var otherId = idPlayerDataPair.Key;
        //         // For additive properties, it might happen (due to race conditions) that the resulting value needs
        // to
        //         // be sent to the sender of this packet as well
        //         if (id == otherId && !varProps.Additive) {
        //             continue;
        //         }
        //
        //         _netServer.GetUpdateManagerForClient(otherId)?.SetSaveUpdate(packet.SaveDataIndex, packet.Value);
        //     }
        // }
    }

    #endregion

    #region IServerManager methods

    /// <inheritdoc />
    public IServerPlayer? GetPlayer(ushort id) {
        return TryGetPlayer(id, out var player) ? player : null;
    }

    /// <inheritdoc />
    public bool TryGetPlayer(ushort id, [MaybeNullWhen(false)] out IServerPlayer player) {
        var found = _playerData.TryGetValue(id, out var playerData);
        player = playerData;

        return found;
    }

    /// <summary>
    /// Check whether a given string message is valid for sending over the network.
    /// </summary>
    /// <param name="message">The string message to check.</param>
    /// <exception cref="ArgumentException">Thrown if the message is null, exceeds the max length or contains
    /// invalid characters.</exception>
    private void CheckValidMessage(string message) {
        if (message == null) {
            throw new ArgumentException("Message cannot be null");
        }

        if (message.Length > ChatMessage.MaxMessageLength) {
            throw new ArgumentException($"Message length exceeds max length of {ChatMessage.MaxMessageLength}");
        }
    }

    /// <inheritdoc />
    public void SendMessage(ushort id, string message) {
        CheckValidMessage(message);

        var updateManager = _netServer.GetUpdateManagerForClient(id);

        // Break message up in parts denoted by newline
        var messages = message.Split('\n');
        foreach (var line in messages) {
            updateManager?.AddChatMessage(line);
        }
    }

    /// <inheritdoc />
    public void SendMessage(IServerPlayer player, string message) {
        if (player == null) {
            throw new ArgumentException("Player cannot be null");
        }

        SendMessage(player.Id, message);
    }

    /// <inheritdoc />
    public void BroadcastMessage(string message) {
        CheckValidMessage(message);

        foreach (var player in _playerData.Values) {
            SendMessage(player.Id, message);
        }
    }

    /// <inheritdoc />
    public void DisconnectPlayer(ushort id, DisconnectReason reason) {
        if (!_playerData.TryGetValue(id, out _)) {
            throw new ArgumentException("There is no player connected with the given ID");
        }

        InternalDisconnectPlayer(id, reason);
    }

    /// <inheritdoc />
    public void ApplyServerSettings(ServerSettings serverSettings) {
        if (serverSettings == null) {
            throw new ArgumentException("Cannot apply null ServerSettings", nameof(serverSettings));
        }

        // If these ServerSettings instances are equal in value, we can immediately return
        if (InternalServerSettings.Equals(serverSettings)) {
            return;
        }

        // Set all properties of the given instance and then call the OnUpdate method to network the changes
        InternalServerSettings.SetAllProperties(serverSettings);
        OnUpdateServerSettings();
    }

    /// <summary>
    /// Expose the set of registered server commands (aliases collapsed) for helpers like HelpCommand.
    /// </summary>
    public IEnumerable<IServerCommand> GetRegisteredCommands() {
        return CommandManager.GetRegisteredCommands();
    }

    #endregion
}
