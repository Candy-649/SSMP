using SSMP.Game.Client;
using SSMP.Game.Server;
using SSMP.Game.Settings;
using SSMP.Networking.Client;
using SSMP.Networking.Packet;
using SSMP.Networking.Server;
using SSMP.Ui;
using SSMP.Ui.Component;
using SSMP.Ui.Resources;
using SSMP.Util;

namespace SSMP.Game;

/// <summary>
/// Instantiates all necessary classes to start multiplayer activities.
/// </summary>
internal class GameManager {
    /// <summary>
    /// The UI manager instance for the mod.
    /// </summary>
    private readonly UiManager _uiManager;

    /// <summary>
    /// The client manager instance for the mod.
    /// </summary>
    private readonly ClientManager _clientManager;

    /// <summary>
    /// The server manager instance for the mod.
    /// </summary>
    private readonly ModServerManager _serverManager;

    /// <summary>
    /// The connection of this game to a server, its own or another player's.
    /// </summary>
    private readonly NetClient _netClient;
    
    /// <summary>
    /// Constructs this GameManager instance by instantiating all other necessary classes.
    /// </summary>
    public GameManager() {
        var modSettings = ModSettings.Load();

        var packetManager = new PacketManager();

        var netClient = new NetClient(packetManager);
        _netClient = netClient;
        var netServer = new NetServer(packetManager);

        var clientServerSettings = new ServerSettings();
        if (modSettings.ServerSettings == null) {
            modSettings.ServerSettings = new ServerSettings();
        }
        var serverServerSettings = modSettings.ServerSettings;

        _uiManager = new UiManager(
            modSettings,
            netClient
        );

        _serverManager = new ModServerManager(
            netServer,
            packetManager,
            serverServerSettings,
            _uiManager,
            modSettings
        );

        _clientManager = new ClientManager(
            netClient,
            packetManager,
            _uiManager,
            clientServerSettings,
            modSettings
        );
    }

    /// <summary>
    /// Initialize all the managers and static utilities.
    /// </summary>
    public void Initialize() {
        ThreadUtil.Instantiate();

        TextureManager.LoadTextures();

        // Initialize Steam if available
        if (SteamManager.Initialize()) {
            // Hook lobby cleanup to UI's stop request (explicit user action), NOT ServerShutdownEvent
            // ServerShutdownEvent fires on server restarts too, which would prematurely clean up lobbies
            _uiManager.RequestServerStopHostEvent += () => {
                SteamManager.LeaveLobby();
                // Also close MMS lobby registration if any (for public Steam lobbies)
                _uiManager.ConnectInterface.MmsClient.CloseLobby();
            };
        }

        _uiManager.Initialize();
        _serverManager.Initialize();
        _clientManager.Initialize(_serverManager);

        // The choices that the host of a server makes for everyone in it, such as how tough enemies get with more
        // players: changed on the settings page by the host while their server runs, or for a server of their own
        // while not connected, and seen there by everyone else connected
        _uiManager.ConnectInterface.SetRoomSettings(new RoomSettingsLink(
            _clientManager.ServerSettings,
            () => _serverManager.IsRunning,
            () => _netClient.IsConnected,
            _serverManager.OnUpdateServerSettings
        ));
    }

    /// <summary>
    /// Shuts down the game manager and all its subsystems.
    /// </summary>
    public void Shutdown() {
        Logging.Logger.Info("GameManager: Shutting down...");

        // Each step on its own, so that one that fails does not keep the ones after it from running: Steam is let go
        // last, and only once nothing is left asking it anything (SteamRelayRecorder)

        // Stop client first to disconnect from any server
        ShutDownStep("disconnect from the server", () => _clientManager.Disconnect());

        // Stop server if hosting
        ShutDownStep("stop the server", () => _serverManager.Stop());

        // Clean up Steam if initialized
        if (SteamManager.IsInitialized) {
            ShutDownStep("let Steam go", SteamManager.Shutdown);
        }
    }

    /// <summary>
    /// Does one step of shutting down, saying so if it fails rather than leaving the rest undone.
    /// </summary>
    private static void ShutDownStep(string what, System.Action step) {
        try {
            step();
        } catch (System.Exception e) {
            Logging.Logger.Error($"GameManager: could not {what} while shutting down: {e}");
        }
    }
}
