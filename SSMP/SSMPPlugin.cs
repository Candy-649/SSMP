using BepInEx;
using SSMP.Hooks;
using SSMP.Logging;
using SSMP.Util;

namespace SSMP;

/// <summary>
/// BepInEx Plugin class for SSMP.
/// </summary>
[BepInAutoPlugin(id: "ssmp")]
[BepInDependency(Game.Client.Skin.CustomizerT2D.PluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
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

        // The game's own version and build: two players' logs then tell whether they run the same game
        Logging.Logger.Info(GameVersion.Describe());

        PluginBackups.Tidy();
        StackOverflowRecovery.Apply();

        // A film of the game never drops a frame, while its sound plays on by itself from the start: on a computer that
        // draws the film slower than it runs, the sound gets further and further ahead (USER 11-08 "每次只要一进动画，音频
        // 就比动画要快很多", "音画不同步太难受了"). The game's own setting to drop the frames a film is late for is on; only the
        // switch that lets it count was off.
        CheatManager.OverrideSkipFrameOnDrop = true;
        Logging.Logger.Info("Films of the game drop the frames they are late for, so that their picture keeps up with their sound");

        // Before CustomizerT2D starts, which chooses its skins then (see CustomizerT2D)
        Game.Client.Skin.CustomizerT2D.Hook();

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
        SceneEventGuard.Initialize();
        LoadWatch.Initialize();
        Game.Client.HarpoonWatch.Initialize();
        Game.Client.HudWatch.Initialize();

        _gameManager = new Game.GameManager();
        _gameManager.Initialize();
    }

    private void OnApplicationQuit() {
        _gameManager?.Shutdown();
    }
}
