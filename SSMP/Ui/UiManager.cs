using System;
using System.Collections;
using System.Collections.Generic;
using GlobalEnums;
using MonoMod.RuntimeDetour;
using SSMP.Api.Client;
using SSMP.Game.Settings;
using SSMP.Hooks;
using SSMP.Networking.Client;
using SSMP.Networking.Transport.Common;
using SSMP.Ui.Chat;
using SSMP.Ui.Util;
using SSMP.Util;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Ui;

/// <inheritdoc />
internal class UiManager : IUiManager {
    #region Constants
    
    /// <summary>
    /// The font size for normal text elements (24 pixels).
    /// Used for general UI labels and buttons.
    /// </summary>
    public const int NormalFontSize = 24;

    /// <summary>
    /// The font size for chat messages (22 pixels).
    /// Slightly smaller for better chat readability.
    /// </summary>
    public const int ChatFontSize = 22;

    /// <summary>
    /// The font size for subtitle and secondary text (22 pixels).
    /// </summary>
    public const int SubTextFontSize = 22;
    
    /// <summary>
    /// The localhost IP address used for self-connecting.
    /// When hosting, the host automatically connects to their own server using this address.
    /// </summary>
    private const string LocalhostAddress = "127.0.0.1";
    
    /// <summary>
    /// Button name for the multiplayer menu button in the main menu.
    /// </summary>
    private const string MultiplayerButtonName = "StartMultiplayerButton";
    
    /// <summary>
    /// Localization key for the multiplayer button text.
    /// </summary>
    private const string MultiplayerButtonKey = "StartMultiplayerBtn";
    
    /// <summary>
    /// Localization sheet name for main menu text.
    /// </summary>
    private const string MainMenuSheet = "MainMenu";
    
    /// <summary>
    /// Name of the back button in save profile menu.
    /// </summary>
    private const string BackButtonName = "BackButton";

    /// <summary>
    /// Ratio for scaling UI elements based on screen height.
    /// Calculated as (actual screen height) / 1080, where 1080 is the reference resolution.
    /// </summary>
    public static readonly float ScreenHeightRatio = Screen.height / 1080f;

    #endregion

    #region Singleton Accessors

    /// <summary>
    /// Shorthand accessor for the GameManager singleton instance.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    private static GameManager GM => GameManager.instance;

    /// <summary>
    /// Shorthand accessor for the UIManager singleton instance.
    /// The game's built-in UI manager for menu navigation.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    private static UIManager UM => UIManager.instance;

    /// <summary>
    /// Shorthand accessor for the InputHandler singleton instance.
    /// Handles keyboard/controller input for the game.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    private static InputHandler IH => InputHandler.Instance;

    #endregion

    #region Static Fields

    /// <summary>
    /// Root GameObject containing all multiplayer UI elements.
    /// Persists across scene changes (DontDestroyOnLoad).
    /// </summary>
    internal static GameObject? UiGameObject;

    /// <summary>
    /// The chat box component for in-game text communication.
    /// Visible during gameplay for sending and receiving messages.
    /// </summary>
    internal static ChatBox InternalChatBox = null!;

    /// <summary>
    /// Event raised when text is submitted in the chat box.
    /// Subscribers process the message for network transmission.
    /// </summary>
    internal static event Action<string>? ChatInputEvent;

    #endregion

    #region Events

    /// <summary>
    /// Event raised when the user requests to start hosting a server from the UI.
    /// Parameters: address, port, username, transport type, fallback address.
    /// </summary>
    public event Action<string, int, string, TransportType, string?>? RequestServerStartHostEvent;

    /// <summary>
    /// Event raised when the user requests to stop hosting the current server.
    /// </summary>
    public event Action? RequestServerStopHostEvent;

    /// <summary>
    /// Event raised when the user requests to connect to a server from the UI.
    /// Parameters: address, port, username, transport type, is auto-connect (localhost), fallback address.
    /// </summary>
    public event Action<string, int, string, TransportType, bool, string?>? RequestClientConnectEvent;

    /// <summary>
    /// Event raised when the user requests to disconnect from the current server.
    /// </summary>
    public event Action? RequestClientDisconnectEvent;

    /// <summary>
    /// Event raised when a save loads after hosting already started for it, which a two-player save does while it
    /// waits in the menu for its partner.
    /// </summary>
    public event Action? HostSaveLoadedEvent;

    /// <summary>
    /// Event raised when hosting that started before a save loaded stops without the save loading, because the
    /// connection failed or was lost.
    /// </summary>
    public event Action? HostBeforeSaveStoppedEvent;

    /// <summary>
    /// Event raised when the save menu of hosting closes without a save, like when Back is pressed.
    /// </summary>
    public event Action? HostSaveSelectionClosedEvent;

    /// <summary>
    /// Event raised when the local player says they are ready in the waiting room, or takes it back, so that the
    /// other player is told.
    /// </summary>
    public event Action<bool>? ReadyToggledEvent;

    /// <summary>
    /// Event raised when everyone in the waiting room is ready, so a player who joined chooses the save their game
    /// held back.
    /// </summary>
    public event Action? BothReadyEvent;

    /// <inheritdoc />
    public event Action? MultiplayerButtonPressed;

    #endregion

    #region Fields

    /// <summary>
    /// Mod settings instance containing user preferences.
    /// Used to load saved connection details and display settings.
    /// </summary>
    private readonly ModSettings _modSettings;

    /// <summary>
    /// Network client instance for checking connection state.
    /// Used to determine if UI should show connected/disconnected state.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The connection interface UI component.
    /// Provides host/join controls and connection status display.
    /// </summary>
    private ConnectInterface _connectInterface = null!;

    /// <summary>
    /// Unity EventSystem for handling UI input and navigation.
    /// Required for button clicks and keyboard navigation.
    /// </summary>
    private EventSystem _eventSystem = null!;
    
    /// <summary>
    /// Component group controlling visibility of connection UI elements.
    /// Shown in main menu, hidden during gameplay.
    /// </summary>
    private ComponentGroup _connectGroup = null!;

    /// <summary>
    /// Component group for in-game UI elements (chat, ping).
    /// Shown during gameplay, hidden in menus and non-gameplay scenes.
    /// </summary>
    private ComponentGroup? _inGameGroup;
    
    /// <summary>
    /// The ping display interface showing network latency.
    /// Only visible when connected to a server.
    /// </summary>
    private PingInterface _pingInterface = null!;

    /// <summary>
    /// The line in game that offers a two-player save with the other player.
    /// </summary>
    private CoopPromptInterface _coopPromptInterface = null!;

    /// <summary>
    /// The line in game, at the bottom of the screen, that says whose round of a game a player watches.
    /// </summary>
    private CoopPromptInterface _minigamePromptInterface = null!;

    /// <summary>
    /// The health bars of creatures that were hurt.
    /// </summary>
    private EnemyHealthBars? _enemyHealthBars;

    /// <summary>
    /// Original event triggers for the save selection screen's back button.
    /// Stored when overridden to return to multiplayer menu instead of main menu.
    /// Restored when exiting save selection.
    /// </summary>
    private List<EventTrigger.Entry> _originalBackTriggers = null!;

    /// <summary>
    /// Callback action executed when save slot selection finishes.
    /// Boolean parameter: true if save was selected, false if back button was pressed.
    /// </summary>
    private Action<bool>? _saveSlotSelectedAction;

    /// <summary>
    /// Whether the save slot selection menu is currently active/opening.
    /// Prevents re-entrancy and duplicate hook registration.
    /// </summary>
    private bool _isSlotSelectionActive;

    /// <summary>
    /// Whether the save selection opened from the multiplayer menu is on screen and waiting for a save to be chosen.
    /// Narrower than <see cref="_isSlotSelectionActive"/>, which stays set while a chosen save loads.
    /// </summary>
    private bool _saveMenuAwaitingChoice;

    /// <summary>
    /// The hook on the one method every menu button of the game answers a cancel with.
    /// </summary>
    private Hook? _menuCancelHook;

    /// <summary>
    /// The hook on the game's way back to the main menu, which lets one go at a time.
    /// </summary>
    private Hook? _menuReturnHook;

    /// <summary>
    /// The game manager whose way back to the main menu is under way, if one is. The game manager goes with the game
    /// that it ran, so a way back that reaches the menu ends with it, and the next game starts with none.
    /// </summary>
    private static GameManager? _returningManager;

    /// <summary>
    /// Whether the save slot selection is open for hosting, which the host reaches from the waiting room with their
    /// game open already.
    /// </summary>
    public bool IsSelectingHostSave { get; private set; }

    /// <summary>
    /// The head of the multiplayer menu hook chain.
    /// Starts as the bare transition; each registered hook wraps it.
    /// </summary>
    private Action _multiplayerMenuChain;

    #endregion

    #region Properties

    /// <summary>
    /// Public accessor for the connect interface.
    /// Used by server manager to access MmsClient for HolePunch lobby cleanup.
    /// </summary>
    public ConnectInterface ConnectInterface => _connectInterface;

    /// <summary>
    /// The line in game that offers a two-player save, which the two-player saves themselves put text into.
    /// </summary>
    public CoopPromptInterface CoopPrompt => _coopPromptInterface;

    /// <summary>
    /// The line in game, at the bottom of the screen, that says whose round of a game a player watches, and how to
    /// watch another player's or stop.
    /// </summary>
    public CoopPromptInterface MinigamePrompt => _minigamePromptInterface;

    /// <summary>
    /// Gets the chat box interface for sending and receiving messages.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if UiManager not initialized yet</exception>
    public IChatBox ChatBox {
        get {
            if (InternalChatBox == null) {
                throw new InvalidOperationException("UiManager is not initialized yet, cannot obtain chat box");
            }

            return InternalChatBox;
        }
    }

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="UiManager"/> class.
    /// </summary>
    /// <param name="modSettings">Mod settings for loading user preferences</param>
    /// <param name="netClient">Network client for checking connection state</param>
    public UiManager(ModSettings modSettings, NetClient netClient) {
        _modSettings = modSettings;
        _netClient = netClient;
        _multiplayerMenuChain = () => UM.StartCoroutine(GoToMultiplayerMenu());
    }

    /// <inheritdoc />
    public void RegisterMultiplayerMenuHook(Action<Action> hook) {
        var previous = _multiplayerMenuChain;
        _multiplayerMenuChain = () => hook(previous);
    }

    #endregion

    #region Initialization

    /// <summary>
    /// Initializes the UI manager by creating all UI components and registering event hooks.
    /// Should be called once during mod startup.
    /// </summary>
    /// <remarks>
    /// Initialization process:
    /// 1. Registers hooks for UI state changes (paused, scene changes)
    /// 2. Registers language hooks for multiplayer button text
    /// 3. Sets up event hooks for menu navigation
    /// 4. Creates all UI components (connection menu, chat, ping)
    /// 5. Registers input checking for hotkeys
    /// </remarks>
    public void Initialize() {
        RegisterEventHooks();
        SetupUi();
        
        MonoBehaviourUtil.Instance.OnUpdateEvent += CheckKeyBinds;
    }

    /// <summary>
    /// Registers all event hooks for UI state management and menu integration.
    /// </summary>
    private void RegisterEventHooks() {
        RegisterUiStateHooks();
        RegisterLanguageHooks();
        RegisterMenuHooks();
    }

    /// <summary>
    /// Registers hooks for UI state changes (pause, scene changes).
    /// </summary>
    private void RegisterUiStateHooks() {
        EventHooks.UIManagerSetState += OnUiStateChanged;
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged += OnSceneChanged;
    }

    /// <summary>
    /// Registers language system hooks for multiplayer button text.
    /// </summary>
    private void RegisterLanguageHooks() {
        EventHooks.LanguageHas += OnLanguageHas;
        EventHooks.LanguageGet += OnLanguageGet;
    }

    /// <summary>
    /// Registers menu navigation hooks.
    /// </summary>
    private void RegisterMenuHooks() {
        EventHooks.UIManagerUIGoToMainMenu += TryAddMultiplayerScreen;
        EventHooks.UIManagerReturnToMainMenu += OnReturnToMainMenu;
        _menuCancelHook = HookMenuCancel();
        _menuReturnHook = HookMenuReturn();
    }

    /// <summary>
    /// Lets the game go back to the main menu one way at a time (USER 10-22 "啥都点不了"). Two at once, as when the
    /// mod went back for a lost connection while the quit of the pause menu was pressed too, each load the scene of
    /// the main menu, the second over the first. The menu manager of the first then stays, with the title that it
    /// knows gone with the first scene, and the one of the second is thrown away as a spare. The game's own step into
    /// the main menu threw on that title after it had taken the menu's input away, and nothing could be pressed.
    /// </summary>
    private static Hook? HookMenuReturn() {
        try {
            var method = typeof(GameManager).GetMethod(
                nameof(GameManager.ReturnToMainMenu),
                [typeof(bool), typeof(Action<bool>), typeof(bool), typeof(bool)]
            );
            if (method == null) {
                Logger.Warn("Could not find how the game goes back to the main menu, so it may go there twice at once");
                return null;
            }

            return new Hook(
                method,
                (Func<Func<GameManager, bool, Action<bool>, bool, bool, IEnumerator>, GameManager, bool, Action<bool>,
                    bool, bool, IEnumerator>) OnMenuReturn
            );
        } catch (Exception e) {
            Logger.Error($"Could not hook how the game goes back to the main menu:\n{e}");
            return null;
        }
    }

    /// <summary>
    /// Starts the game's way back to the main menu, unless one is under way already.
    /// </summary>
    private static IEnumerator OnMenuReturn(
        Func<GameManager, bool, Action<bool>, bool, bool, IEnumerator> orig,
        GameManager self,
        bool willSave,
        Action<bool> callback,
        bool isEndGame,
        bool forceMainMenu
    ) {
        if (_returningManager != null && _returningManager == self) {
            Logger.Info("The game is on its way back to the main menu already, so a second way there is not started");
            return NoMenuReturn();
        }

        _returningManager = self;
        return ReturnOnce(orig(self, willSave, callback, isEndGame, forceMainMenu), self);
    }

    /// <summary>
    /// Runs the game's way back to the main menu, which ends as one when it stops short of the menu: when the save
    /// before it fails, the game stays where it is, and when a step of it throws, nothing more of it runs.
    /// </summary>
    private static IEnumerator ReturnOnce(IEnumerator menuReturn, GameManager manager) {
        try {
            while (menuReturn.MoveNext()) {
                yield return menuReturn.Current;
            }
        } finally {
            if (_returningManager == manager) {
                _returningManager = null;
            }
        }
    }

    /// <summary>
    /// The way back to the main menu that the second one is: none.
    /// </summary>
    private static IEnumerator NoMenuReturn() {
        yield break;
    }

    /// <summary>
    /// Takes a cancel on the save selection opened from the multiplayer menu back to the multiplayer menu, the way
    /// the back button of that screen already does.
    ///
    /// A cancel from a gamepad or a keyboard goes to whatever is selected, which on that screen is a save slot, and a
    /// slot answers it the way the game's own save selection does: by going to the main menu. That leaves this menu
    /// believing a save is still being chosen, so the next request to choose one was quietly ignored. It never came
    /// up only because every cancel on that screen used to be thrown away along with every confirm.
    /// </summary>
    private Hook? HookMenuCancel() {
        try {
            var method = typeof(MenuSelectable).GetMethod(nameof(MenuSelectable.OnCancel), [typeof(BaseEventData)]);
            if (method == null) {
                Logger.Warn("Could not find how a menu button answers a cancel, so one leaves the save selection " +
                            "for the main menu");
                return null;
            }

            return new Hook(
                method,
                (Action<Action<MenuSelectable, BaseEventData>, MenuSelectable, BaseEventData>) OnMenuCancel
            );
        } catch (Exception e) {
            Logger.Error($"Could not hook how a menu button answers a cancel:\n{e}");
            return null;
        }
    }

    /// <summary>
    /// Answers a cancel on a save slot or the back button of the save selection opened from the multiplayer menu
    /// with that screen's own way back, and leaves every other cancel to the game - including the ones inside a
    /// slot, such as the prompt that asks whether to clear it.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The menu button that was cancelled on.</param>
    /// <param name="eventData">The event.</param>
    private void OnMenuCancel(Action<MenuSelectable, BaseEventData> orig, MenuSelectable self, BaseEventData eventData) {
        if (_saveMenuAwaitingChoice && _isSlotSelectionActive && self.interactable &&
            (self is SaveSlotButton || self.gameObject == FindSaveMenuBackButton())) {
            OnSaveMenuBackPressed();
            return;
        }

        orig(self, eventData);
    }

    #endregion

    #region Event Handlers

    /// <summary>
    /// Handles UI state changes to show/hide in-game UI elements.
    /// </summary>
    private void OnUiStateChanged(object _, UIState state) {
        var shouldShow = state != UIState.PAUSED && !SceneUtil.IsNonGameplayScene(SceneUtil.GetCurrentSceneName());
        _inGameGroup?.SetActive(shouldShow);
    }

    /// <summary>
    /// Handles scene changes to manage UI visibility and event system state.
    /// </summary>
    private void OnSceneChanged(UnityEngine.SceneManagement.Scene oldScene, UnityEngine.SceneManagement.Scene newScene) {
        var isNonGameplayScene = SceneUtil.IsNonGameplayScene(newScene.name);

        if (_eventSystem != null) {
            _eventSystem.enabled = !isNonGameplayScene;
        }

        _inGameGroup?.SetActive(!isNonGameplayScene);
    }

    /// <summary>
    /// Handles language system queries for custom text keys.
    /// </summary>
    private bool? OnLanguageHas(string key, string sheet) =>
        IsMultiplayerButtonKey(key, sheet) ? true : null;

    /// <summary>
    /// Provides localized text for custom UI elements.
    /// </summary>
    private string? OnLanguageGet(string key, string sheet) =>
        IsMultiplayerButtonKey(key, sheet) ? "Start Multiplayer" : null;

    /// <summary>
    /// Checks if the provided key and sheet match the multiplayer button localization.
    /// </summary>
    private static bool IsMultiplayerButtonKey(string key, string sheet) =>
        key == MultiplayerButtonKey && sheet == MainMenuSheet;

    /// <summary>
    /// Handles return to main menu by disconnecting from server.
    /// </summary>
    private void OnReturnToMainMenu() {
        RequestClientDisconnectEvent?.Invoke();
        RequestServerStopHostEvent?.Invoke();

        // Choosing a save deliberately leaves the slot selection marked active, so that the menu cannot be reopened
        // while the game loads it. Nothing cleared it afterwards, so hosting again after coming back here did nothing
        // whatsoever: the save menu found the selection still active and never opened. No message either, because
        // there was no failure - only a press that quietly led nowhere.
        _isSlotSelectionActive = false;
        IsSelectingHostSave = false;
        _hostStartedBeforeSave = false;
    }

    #endregion

    #region UI Setup

    /// <summary>
    /// Sets up all UI components including canvas, event system, and multiplayer interfaces.
    /// Creates the UI hierarchy and initializes all interface components.
    /// </summary>
    private void SetupUi() {
        Resources.FontManager.LoadFonts();
        
        CreateRootCanvas();
        CreateEventSystem();
        PadNavigation.KeepKeysFromPressing();
        CreateUiComponents();
        RegisterInterfaceCallbacks();

        _enemyHealthBars = new EnemyHealthBars(_modSettings);
        _enemyHealthBars.Start();
        
        TryAddMultiplayerScreen();
    }

    /// <summary>
    /// Creates the root canvas for all multiplayer UI.
    /// </summary>
    private void CreateRootCanvas() {
        UiGameObject = new GameObject("SSMP_UI");

        var canvas = UiGameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        var canvasScaler = UiGameObject.AddComponent<CanvasScaler>();
        canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasScaler.referenceResolution = new Vector2(1920f, 1080f);
        canvasScaler.matchWidthOrHeight = 1f;
        canvasScaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        
        UiGameObject.AddComponent<GraphicRaycaster>();

        // The frame around what a gamepad has selected in the mod's menus
        PadFocusFrame.Create(UiGameObject);
        Object.DontDestroyOnLoad(UiGameObject);
    }

    /// <summary>
    /// Creates the Unity EventSystem for handling UI input.
    /// </summary>
    private void CreateEventSystem() {
        var eventSystemObj = new GameObject("SSMP_EventSystem");

        _eventSystem = eventSystemObj.AddComponent<EventSystem>();
        _eventSystem.sendNavigationEvents = true;
        _eventSystem.pixelDragThreshold = 10;

        // The rule that keeps this one out of the way in menus lives in the scene change handler, and a scene change
        // is exactly what does not happen next: this is built while the main menu is already loaded, so nothing
        // applied the rule until the game was entered. That left a second event system running through the
        // multiplayer menu, the waiting room and the save selection - the screens where the game's own handling of a
        // gamepad has to work, and where a gamepad could move between saves but not open one.
        _eventSystem.enabled = !SceneUtil.IsNonGameplayScene(SceneUtil.GetCurrentSceneName());

        eventSystemObj.AddComponent<StandaloneInputModule>();
        Object.DontDestroyOnLoad(eventSystemObj);
    }

    /// <summary>
    /// Creates all UI component groups and interfaces.
    /// </summary>
    private void CreateUiComponents() {
        var uiGroup = new ComponentGroup();

        CreateConnectionInterface(uiGroup);
        CreateInGameInterface(uiGroup);
    }

    /// <summary>
    /// Creates the connection interface for the multiplayer menu.
    /// </summary>
    private void CreateConnectionInterface(ComponentGroup parent) {
        _connectGroup = new ComponentGroup(false, parent);
        _connectInterface = new ConnectInterface(_modSettings, _connectGroup);
    }

    /// <summary>
    /// Creates the in-game interface (chat and ping display).
    /// </summary>
    private void CreateInGameInterface(ComponentGroup parent) {
        _inGameGroup = new ComponentGroup(parent: parent);

        var infoBoxGroup = new ComponentGroup();
        InternalChatBox = new ChatBox(infoBoxGroup, _modSettings);
        InternalChatBox.ChatInputEvent += input => ChatInputEvent?.Invoke(input);

        var pingGroup = new ComponentGroup(parent: _inGameGroup);
        _pingInterface = new PingInterface(pingGroup, _modSettings, _netClient);

        var coopPromptGroup = new ComponentGroup(parent: _inGameGroup);
        _coopPromptInterface = new CoopPromptInterface(coopPromptGroup);

        var minigamePromptGroup = new ComponentGroup(parent: _inGameGroup);
        _minigamePromptInterface = new CoopPromptInterface(
            minigamePromptGroup, CoopPromptInterface.BottomLineTopMargin
        );
    }

    /// <summary>
    /// Registers callbacks for interface button events.
    /// </summary>
    private void RegisterInterfaceCallbacks() {
        _connectInterface.ConnectButtonPressed += OnConnectRequested;
        _connectInterface.StartHostWithoutSavePressed += StartHostWithoutSave;
        _connectInterface.HostSaveSelectionRequested += OpenHostSaveSelection;
        _connectInterface.StopWaitingRoomHostingEvent += StopHostBeforeSave;
        _connectInterface.LeaveJoinedRoomEvent += () => RequestClientDisconnectEvent?.Invoke();
        _connectInterface.JoinSaveSelectionRequested += () => BothReadyEvent?.Invoke();
        _connectInterface.WaitingRoomReadyToggled += ready => ReadyToggledEvent?.Invoke(ready);

        // Both are multicast and already have listeners elsewhere, so the waiting room just adds itself
        HostSaveSelectionClosedEvent += _connectInterface.OnHostSaveSelectionClosed;
        HostSaveLoadedEvent += _connectInterface.OnHostSaveLoaded;
    }

    /// <summary>
    /// Whether hosting started before the chosen save loaded. Every host opens their game in the waiting room before
    /// choosing a save, and a two-player save waits in the menu for its partner on it.
    /// </summary>
    private bool _hostStartedBeforeSave;

    /// <summary>
    /// Whether hosting runs while the save menu is open for hosting, so a two-player save can wait there for its
    /// partner.
    /// </summary>
    public bool IsHostingBeforeSave => _hostStartedBeforeSave;

    /// <summary>
    /// Stops hosting that started before the save loaded, for when the player stops waiting for their partner.
    /// </summary>
    public void StopHostBeforeSave() {
        if (!_hostStartedBeforeSave) {
            return;
        }

        _hostStartedBeforeSave = false;
        RequestClientDisconnectEvent?.Invoke();
        RequestServerStopHostEvent?.Invoke();
    }

    /// <summary>
    /// Starts hosting without opening the save selection, so the host can wait on a screen of their own until their
    /// teammate is in. The save is chosen afterwards through <see cref="OpenHostSaveSelection"/>.
    /// </summary>
    public void StartHostWithoutSave(
        string address,
        int port,
        string username,
        TransportType transportType,
        string? fallbackAddress
    ) {
        if (_hostStartedBeforeSave) {
            return;
        }

        // The same state the save menu uses when a two-player save hosts before loading, so that choosing the save
        // later does not try to start a second server
        _hostStartedBeforeSave = true;

        try {
            RequestServerStartHostEvent?.Invoke(address, port, username, transportType, fallbackAddress);
        } catch (Exception e) {
            // Like a port of direct IP that another program has. Left as it was, the state above kept every later
            // press from hosting, and the waiting room waited on a server that never ran.
            Logger.Error($"Could not start hosting on port {port}:\n{e}");
            _hostStartedBeforeSave = false;
            RequestServerStopHostEvent?.Invoke();
            _connectInterface.OnHostStartFailed(port, e);
            return;
        }

        RequestClientConnectEvent?.Invoke(LocalhostAddress, port, username, transportType, true, null);
    }

    /// <summary>
    /// Opens the save selection for a host that is already hosting, for when they are done waiting for their teammate.
    /// </summary>
    public void OpenHostSaveSelection() {
        if (_isSlotSelectionActive) {
            return;
        }

        IsSelectingHostSave = true;

        OpenSaveSlotSelection(saveSelected => {
            IsSelectingHostSave = false;

            if (!saveSelected) {
                // Deliberately not stopping the hosting that the waiting room started: the game stays open and the
                // teammate stays connected, and this event puts the host back on the waiting room so a mis-press does
                // not cost them the lobby.
                HostSaveSelectionClosedEvent?.Invoke();
                return;
            }

            _hostStartedBeforeSave = false;
            HostSaveLoadedEvent?.Invoke();
        });
    }

    /// <summary>
    /// Tells the connection UI that a player joined, so a host waiting for them sees it.
    /// </summary>
    /// <param name="username">The name of the player that joined.</param>
    public void OnPlayerJoined(string username) => _connectInterface.OnPlayerJoined(username);

    /// <summary>
    /// Tells the connection UI that a player left.
    /// </summary>
    /// <param name="username">The name of the player that left.</param>
    public void OnPlayerLeft(string username) => _connectInterface.OnPlayerLeft(username);

    /// <summary>
    /// Shows the waiting room to a player who joined someone else's game, rather than opening their save selection
    /// straight away.
    /// </summary>
    /// <param name="others">The players already on the server.</param>
    public void ShowJoinWaitingRoom(IReadOnlyList<string> others) => _connectInterface.ShowJoinWaitingRoom(others);

    /// <summary>
    /// Tells the waiting room that the other player said they are ready, or took it back.
    /// </summary>
    /// <param name="username">The name of the player whose readiness changed.</param>
    /// <param name="ready">Whether they are ready now.</param>
    public void OnPartnerReadyChanged(string username, bool ready) =>
        _connectInterface.OnPartnerReadyChanged(username, ready);

    /// <summary>
    /// Handles connect button press.
    /// </summary>
    private void OnConnectRequested(
        string address,
        int port,
        string username,
        TransportType transportType,
        string? fallbackAddress
    ) =>
        RequestClientConnectEvent?.Invoke(address, port, username, transportType, false, fallbackAddress);

    #endregion

    #region Public Methods

    /// <summary>
    /// Enters the game from the multiplayer menu with current PlayerData.
    /// Assumes PlayerData is already populated with save file data.
    /// </summary>
    /// <param name="newGame">True to start a new game, false to continue existing save</param>
    public void EnterGameFromMultiplayerMenu(bool newGame) {
        IH.StopUIInput();
        _connectGroup.SetActive(false);
        PlayMenuTransitionAudio();

        Logger.Debug($"Entering game from MP menu for {(newGame ? "new" : "continued")} game");
        
        if (newGame) {
            GM.StartCoroutine(GM.RunStartNewGame());
        } else {
            GM.ContinueGame();
        }
    }

    /// <summary>
    /// Plays audio for menu transition to game.
    /// </summary>
    private void PlayMenuTransitionAudio() {
        UM.uiAudioPlayer.PlayStartGame();
        MenuStyles.Instance?.StopAudio();
    }

    /// <summary>
    /// Returns to the main menu from in-game.
    /// Used when player disconnects from the current server.
    /// </summary>
    /// <param name="save">Whether to save the game before returning to menu</param>
    public void ReturnToMainMenuFromGame(bool save = true) =>
        GM.StartCoroutine(ReturnToMainMenuAfterSceneLoad(save));

    /// <summary>
    /// Returns to the main menu once a scene that is still loading has loaded, because the game doesn't wait for it and
    /// would load both at once, like when the connection is lost while the hero goes through a door.
    /// </summary>
    /// <param name="save">Whether to save the game before returning to menu</param>
    private IEnumerator ReturnToMainMenuAfterSceneLoad(bool save) {
        const float maxWaitTime = 10f;
        var waitEnd = Time.unscaledTime + maxWaitTime;
        while (GM.IsInSceneTransition && Time.unscaledTime < waitEnd) {
            yield return null;
        }

        yield return GM.ReturnToMainMenu(save);
    }

    /// <summary>
    /// Callback invoked when client successfully connects to a server.
    /// Updates UI to show connected state and enables ping display.
    /// </summary>
    public void OnSuccessfulConnect() {
        _connectInterface.OnSuccessfulConnect();
        _pingInterface.SetEnabled(true);
    }

    /// <summary>
    /// Callback invoked when client fails to connect to a server.
    /// Updates UI to show error message based on failure reason.
    /// </summary>
    /// <param name="result">The reason for connection failure</param>
    /// <param name="fallbackAddress">Optional fallback address (IP:Port) to attempt on failure.</param>
    public void OnFailedConnect(ConnectionFailedResult result, string? fallbackAddress = null) {
        _connectInterface.OnFailedConnect(result, fallbackAddress);
        EndHostBeforeSave();
    }

    /// <summary>
    /// Callback invoked when client disconnects from the server.
    /// Updates UI to show disconnected state and disables ping display.
    /// </summary>
    public void OnClientDisconnect() {
        _connectInterface.OnClientDisconnect();
        _pingInterface.SetEnabled(false);
        _coopPromptInterface.Hide();
        _isSlotSelectionActive = false;
        EndHostBeforeSave();
    }

    /// <summary>
    /// Stops the server of hosting that started before a save loaded, after its connection failed or was lost, so that
    /// choosing the save again hosts again.
    /// </summary>
    private void EndHostBeforeSave() {
        if (!_hostStartedBeforeSave) {
            return;
        }

        _hostStartedBeforeSave = false;
        RequestServerStopHostEvent?.Invoke();
        HostBeforeSaveStoppedEvent?.Invoke();
    }

    #endregion

    #region Save Slot Selection

    /// <summary>
    /// Opens the save slot selection screen from the multiplayer menu.
    /// Used when hosting to select which save file to load.
    /// </summary>
    /// <param name="callback">
    /// Action executed when save selection finishes.
    /// Boolean parameter: true if save selected, false if back pressed.
    /// </param>
    public void OpenSaveSlotSelection(Action<bool>? callback = null) {
        if (_isSlotSelectionActive) {
            Logger.Info("Save slot selection already active, ignoring request");
            return;
        }

        _isSlotSelectionActive = true;
        _saveSlotSelectedAction = CreateSaveSlotCallback(callback);
        
        // Ensure we don't have duplicate hooks
        UnregisterSaveSlotHooks();
        RegisterSaveSlotHooks();
        
        UM.StartCoroutine(GoToSaveMenu());
    }

    /// <summary>
    /// Creates a wrapped callback that cleans up event hooks.
    /// </summary>
    private Action<bool> CreateSaveSlotCallback(Action<bool>? callback) {
        return saveSelected => {
            callback?.Invoke(saveSelected);
            UnregisterSaveSlotHooks();
            // Note: _isSlotSelectionActive is NOT reset here because if save is selected, 
            // the game loads and we don't want to allow reopening the menu.
            // It will be reset if Back is pressed.
            if (!saveSelected) {
                _isSlotSelectionActive = false;
            }
        };
    }

    /// <summary>
    /// Registers event hooks for save slot selection.
    /// </summary>
    private void RegisterSaveSlotHooks() {
        EventHooks.GameManagerStartNewGame += OnSaveSlotSelected;
        EventHooks.GameManagerContinueGame += OnSaveSlotSelected;
    }

    /// <summary>
    /// Removes save slot event hooks.
    /// </summary>
    private void UnregisterSaveSlotHooks() {
        EventHooks.GameManagerStartNewGame -= OnSaveSlotSelected;
        EventHooks.GameManagerContinueGame -= OnSaveSlotSelected;
    }

    /// <summary>
    /// Callback for when a save slot is selected (new game or continue).
    /// </summary>
    private void OnSaveSlotSelected() {
        _saveMenuAwaitingChoice = false;
        _saveSlotSelectedAction?.Invoke(true);
    }

    #endregion

    #region Multiplayer Button

    /// <summary>
    /// Attempts to add the "Start Multiplayer" button to the main menu.
    /// Does nothing if button already exists.
    /// </summary>
    private void TryAddMultiplayerScreen() {
        if (!ValidateMainMenuExists()) return;

        var existingButton = FindMultiplayerButton();
        if (existingButton != null) {
            FixMultiplayerButtonNavigation(existingButton);
            return;
        }

        CreateMultiplayerButton();
    }

    /// <summary>
    /// Validates that the main menu exists and is accessible.
    /// </summary>
    private bool ValidateMainMenuExists() {
        if (UM.mainMenuButtons?.gameObject != null) return true;
        
        Logger.Info("Main menu not available yet");
        return false;
    }

    /// <summary>
    /// Finds the existing multiplayer button if it exists.
    /// </summary>
    private GameObject? FindMultiplayerButton() {
        var button = UM.mainMenuButtons.gameObject.FindGameObjectInChildren(MultiplayerButtonName);
        if (button != null) {
            Logger.Info("Multiplayer button already exists");
        }
        return button;
    }

    /// <summary>
    /// Creates the multiplayer button by cloning the start game button.
    /// </summary>
    private void CreateMultiplayerButton() {
        var startGameBtn = UM.mainMenuButtons.startButton?.gameObject;
        if (startGameBtn == null) {
            Logger.Info("Start game button not found");
            return;
        }

        var multiplayerBtn = Object.Instantiate(startGameBtn, startGameBtn.transform.parent);
        ConfigureMultiplayerButton(multiplayerBtn);
        FixMultiplayerButtonNavigation(multiplayerBtn);
        
        UM.StartCoroutine(FixNavigationAfterInput(multiplayerBtn));
    }

    /// <summary>
    /// Configures the multiplayer button properties.
    /// </summary>
    private void ConfigureMultiplayerButton(GameObject button) {
        button.name = MultiplayerButtonName;
        button.transform.SetSiblingIndex(1);

        ConfigureButtonLocalization(button);
        ConfigureButtonTriggers(button);
    }

    /// <summary>
    /// Configures button text via localization system.
    /// </summary>
    private void ConfigureButtonLocalization(GameObject button) {
        var autoLocalize = button.GetComponent<AutoLocalizeTextUI>();
        if (autoLocalize == null) return;

        autoLocalize.textKey = MultiplayerButtonKey;
        autoLocalize.sheetTitle = MainMenuSheet;
        autoLocalize.OnValidate();
        autoLocalize.RefreshTextFromLocalization();
    }

    /// <summary>
    /// Configures button click event triggers.
    /// </summary>
    private void ConfigureButtonTriggers(GameObject button) {
        var eventTrigger = button.GetComponent<EventTrigger>();
        if (eventTrigger == null) return;

        eventTrigger.triggers.Clear();
        AddButtonTriggers(eventTrigger, OnMultiplayerMenuRequested);
    }

    /// <summary>
    /// Fixes navigation after input system is ready.
    /// </summary>
    private IEnumerator FixNavigationAfterInput(GameObject button) {
        yield return new WaitUntil(() => IH.acceptingInput);
        FixMultiplayerButtonNavigation(button);
    }

    /// <summary>
    /// Fixes keyboard/controller navigation for the multiplayer button.
    /// Ensures proper up/down navigation between menu buttons.
    /// </summary>
    private void FixMultiplayerButtonNavigation(GameObject buttonObject) {
        var multiplayerBtn = buttonObject.GetComponent<MenuButton>();
        if (multiplayerBtn == null) return;

        var startBtn = UM.mainMenuButtons.startButton;
        var optionsBtn = UM.mainMenuButtons.optionsButton;

        SetNavigation(startBtn, selectOnDown: multiplayerBtn);
        SetNavigation(optionsBtn, selectOnUp: multiplayerBtn);
        SetNavigation(multiplayerBtn, selectOnUp: startBtn);
    }

    /// <summary>
    /// Helper method to set navigation for a button.
    /// </summary>
    private void SetNavigation(MenuButton button, MenuButton? selectOnUp = null, MenuButton? selectOnDown = null) {
        if (button == null) return;

        var nav = button.navigation;
        if (selectOnUp != null) nav.selectOnUp = selectOnUp;
        if (selectOnDown != null) nav.selectOnDown = selectOnDown;
        button.navigation = nav;
    }

    /// <summary>
    /// Handles the multiplayer menu request by firing the notification event, then invoking the hook chain.
    /// </summary>
    private void OnMultiplayerMenuRequested() {
        // A gamepad starts from the top of the menu when it is opened from the main menu. Back from the save selection
        // it stays by the control that went there
        PadNavigation.StartOver();
        MultiplayerButtonPressed?.Invoke();
        _multiplayerMenuChain.Invoke();
    }

    #endregion

    #region Menu Navigation

    /// <summary>
    /// Coroutine to transition from main menu to multiplayer connection menu.
    /// </summary>
    private IEnumerator GoToMultiplayerMenu() {
        IH.StopUIInput();
        yield return FadeOutCurrentMenu();
        IH.StartUIInput();
        ShowMultiplayerMenu();
    }

    /// <summary>
    /// Fades out the current menu based on state.
    /// </summary>
    private IEnumerator FadeOutCurrentMenu() {
        switch (UM.menuState) {
            case MainMenuState.MAIN_MENU:
                UM.StartCoroutine(UM.FadeOutSprite(UM.gameTitle));

                // The line under the title can be gone (see HookMenuReturn). Sending to it would throw here, with the
                // menu's input taken away, and nothing would give it back
                if (UM.subtitleFSM != null) {
                    UM.subtitleFSM.SendEvent("FADE OUT");
                } else {
                    Logger.Warn("The line under the title of the main menu is gone: the menu manager is a stale one");
                }

                yield return UM.StartCoroutine(UM.FadeOutCanvasGroup(UM.mainMenuScreen));
                break;
            
            case MainMenuState.SAVE_PROFILES:
                yield return UM.StartCoroutine(UM.HideSaveProfileMenu(false));
                break;
        }
    }

    /// <summary>
    /// Shows the multiplayer connection interface.
    /// </summary>
    private void ShowMultiplayerMenu() {
        _connectGroup.SetActive(true);
        _connectInterface.SetMenuActive(true);
    }

    /// <summary>
    /// Hides the multiplayer connection interface.
    /// </summary>
    private void HideMultiplayerMenu() {
        _connectGroup.SetActive(false);
        _connectInterface.SetMenuActive(false);
    }

    /// <summary>
    /// Coroutine to transition from multiplayer menu to save selection screen.
    /// </summary>
    private IEnumerator GoToSaveMenu() {
        HideMultiplayerMenu();

        // No HideCurrentMenu first - that was why a gamepad could move between the saves and never open one. It only
        // knows the menus under the main one. For the main menu, which is what the game still believes is showing
        // behind the multiplayer one, it marks the menus as fading and returns without ever taking the mark down
        // again. The game's menu input module drops every confirm and every cancel while that mark is up, and moving
        // is the one thing it does not check the mark for. GoToProfileMenu hides whatever it is coming from by
        // itself, and leaves the main menu out of HideCurrentMenu for exactly this reason.

        // Safety check before verifying game UI state
        if (UM != null) {
             yield return UM.GoToProfileMenu();
             OverrideSaveMenuBackButton();

             // Only now, because the game takes input away for the whole of the way in and gives it back at the end
             _saveMenuAwaitingChoice = true;

             // Which event system drives this screen decides whether a gamepad can open a save at all. The game's own
             // one asks the gamepad properly; a plain Unity one does not treat the confirm the same way, and this is
             // the screen where a gamepad could move between saves but not open one. Which of the two ends up in
             // charge cannot be told from the code alone, so it is written into the log rather than guessed at.
             var eventSystem = EventSystem.current;
             Logger.Info(
                 "Save selection is driven by " +
                 (eventSystem == null
                     ? "no event system at all"
                     : $"'{eventSystem.gameObject.name}' using " +
                       $"{eventSystem.currentInputModule?.GetType().Name ?? "no input module"}")
             );

             // That answered which event system is in charge, and the gamepad still cannot open a save with it. The
             // game's own menu ends this screen by highlighting a slot and turning UI input back on, so the next
             // thing to know is whether either actually took: a gamepad confirm needs something selected to send to,
             // and the action it sends. Both are read here rather than reasoned about from the code, which has now
             // been wrong about this three times.
             var selected = eventSystem == null ? null : eventSystem.currentSelectedGameObject;
             var submit = IH.inputActions?.MenuSubmit;
             Logger.Info(
                 $"Save selection has '{selected?.name ?? "nothing"}' selected, " +
                 $"MenuSubmit is {(submit == null ? "unreadable" : submit.Enabled ? "enabled" : "disabled")}, " +
                 $"menus marked as fading: {UM.IsFadingMenu}"
             );
        } else {
             Logger.Error("UIManager instance is null, cannot go to profile menu");
             _isSlotSelectionActive = false;
        }
    }

    /// <summary>
    /// Overrides the save menu back button to return to multiplayer menu.
    /// </summary>
    private void OverrideSaveMenuBackButton() {
        var backButton = FindSaveMenuBackButton();
        if (backButton == null) return;

        var eventTrigger = backButton.GetComponent<EventTrigger>();
        if (eventTrigger == null) return;

        _originalBackTriggers = eventTrigger.triggers;
        eventTrigger.triggers = [];
        
        AddButtonTriggers(eventTrigger, OnSaveMenuBackPressed);
    }

    /// <summary>
    /// Finds the back button in the save profile menu.
    /// </summary>
    private GameObject? FindSaveMenuBackButton() {
        var backButton = UM.saveProfileControls?.gameObject.FindGameObjectInChildren(BackButtonName);
        if (backButton == null) {
            Logger.Info("Save profiles back button not found");
        }
        return backButton;
    }

    /// <summary>
    /// Handles back button press in save menu.
    /// </summary>
    private void OnSaveMenuBackPressed() {
        _saveMenuAwaitingChoice = false;
        UnregisterSaveSlotHooks();
        _isSlotSelectionActive = false;
        _saveSlotSelectedAction?.Invoke(false);
        
        UM.StartCoroutine(GoToMultiplayerMenu());
        RestoreSaveMenuBackButton();
    }

    /// <summary>
    /// Restores original back button behavior for save menu.
    /// </summary>
    private void RestoreSaveMenuBackButton() {
        var backButton = FindSaveMenuBackButton();
        var eventTrigger = backButton?.GetComponent<EventTrigger>();
        if (eventTrigger != null) {
            eventTrigger.triggers = _originalBackTriggers;
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Adds click and submit triggers to a button.
    /// </summary>
    private void AddButtonTriggers(EventTrigger eventTrigger, Action action) {
        AddTrigger(eventTrigger, EventTriggerType.Submit, action);
        AddTrigger(eventTrigger, EventTriggerType.PointerClick, action);
    }

    /// <summary>
    /// Adds a single trigger to an EventTrigger component.
    /// </summary>
    private void AddTrigger(EventTrigger eventTrigger, EventTriggerType triggerType, Action action) {
        var entry = new EventTrigger.Entry { eventID = triggerType };
        entry.callback.AddListener(_ => action.Invoke());
        eventTrigger.triggers.Add(entry);
    }

    /// <summary>
    /// Checks for hotkey presses to exit multiplayer menu, and keeps the menu moved around with a gamepad.
    /// Called every frame by Unity's update system.
    /// </summary>
    private void CheckKeyBinds() {
        if (!_connectGroup.IsActive()) {
            return;
        }

        var actions = InputHandler.Instance.inputActions;
        if (actions.Pause.IsPressed) {
            // Not out of a waiting room, which is left with its own button, as the back button doesn't leave it either:
            // the pause key left the room up, the game hosted or joined, and a host who then started a save from the
            // game's own menu went into it while whoever had joined waited for them to be ready, forever
            if (!_connectInterface.IsInWaitingRoom) {
                HandlePauseKey();
            }

            return;
        }

        // A gamepad moves around this menu the way it moves around the game's own, which needs something selected to
        // move from (see PadNavigation)
        PadNavigation.KeepSelection(UiGameObject!.transform);

        // The back button of a gamepad first stops typing into a text box, then closes what the menu closes with it,
        // and otherwise leaves the menu, as the pause key does
        if (actions.MenuCancel.WasPressed && !PadNavigation.StopTyping() && !_connectInterface.TryGoBack()) {
            HandlePauseKey();
        }
    }

    /// <summary>
    /// Handles pause key press to return to main menu.
    /// </summary>
    private void HandlePauseKey() {
        // UIGoToMainMenu hides the menu it is leaving by itself. Hiding it here as well did nothing for the main menu,
        // which is what the game believes is behind this one, except leave the menus marked as fading for good - and
        // with that mark up, the main menu took no confirm from a gamepad or a keyboard at all.
        UM.UIGoToMainMenu();
        HideMultiplayerMenu();
    }

    #endregion
}
