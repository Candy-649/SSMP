using BepInEx;
using SSMP.Hooks;
using SSMP.Logging;
using SSMP.Util;

namespace SSMP;

/// <summary>
/// BepInEx Plugin class for SSMP.
/// </summary>
[BepInAutoPlugin(id: "ssmp")]
public partial class SSMPPlugin : BaseUnityPlugin {
    /// <summary>
    /// The game manager instance for the mod.
    /// </summary>
    private Game.GameManager? _gameManager;

    /// <summary>
    /// Plugin constructor that initializes the static classes with hooks.
    /// </summary>
    public SSMPPlugin() {
        Logging.Logger.AddLogger(new BepInExLogger());

        EventHooks.Initialize();
        CustomHooks.Initialize();
    }

    private void Awake() {
        // BepInEx's own "Loading [SSMP x.y.z]" line is read from its plugin cache, which it throws away by the
        // timestamp of the file - and every build unpacked from the npm package carries the same frozen
        // timestamp, so that line can name a version that has not been on disk for days. This one comes from the
        // assembly that is really running, and is the same wording the other player's build is compared against.
        Logging.Logger.Info($"Plugin {Name} ({Id}) has loaded! Running build: {Game.SteamManager.LocalModVersion}");

        // Register the event to initialize SSMP once we enter the main menu.
        EventHooks.UIManagerUIGoToMainMenu += Initialize;
    }

    /// <summary>
    /// Initializes the mod by initializing the <see cref="GameManager"/>.
    /// </summary>
    private void Initialize() {
        Logging.Logger.Info("Initializing SSMP");

        EventHooks.UIManagerUIGoToMainMenu -= Initialize;

        // Add the MonoBehaviourUtil to the game object associated with this plugin
        gameObject.AddComponent<MonoBehaviourUtil>();

        _gameManager = new Game.GameManager();
        _gameManager.Initialize();
    }

    private void OnApplicationQuit() {
        _gameManager?.Shutdown();
    }
}
