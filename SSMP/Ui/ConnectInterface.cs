using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using SSMP.Game;
using SSMP.Game.Settings;
using SSMP.Networking.Client;
using SSMP.Networking.Matchmaking;
using SSMP.Networking.Matchmaking.Protocol;
using SSMP.Ui.Component;
using Steamworks;
using SSMP.Networking.Transport.Common;
using SSMP.Networking.Transport.HolePunch;
using SSMP.Ui.Util;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

// ReSharper disable ObjectCreationAsStatement

namespace SSMP.Ui;

/// <summary>
/// Manages the multiplayer connection interface with tabbed navigation for Matchmaking, Steam, and Direct IP connections.
/// </summary>
internal class ConnectInterface {
    #region Layout Constants

    /// <summary>
    /// Horizontal indentation for text labels relative to the panel edge.
    /// </summary>
    private const float TextIndentWidth = 5f;

    /// <summary>
    /// Default width for content elements like input fields and buttons.
    /// </summary>
    private const float ContentWidth = 360f;

    /// <summary>
    /// Standard height for input fields and buttons.
    /// </summary>
    private const float UniformHeight = 50f;

    /// <summary>
    /// Initial X position for the UI panel (centered at 1920x1080).
    /// </summary>
    private const float InitialX = 960f;

    /// <summary>
    /// Initial Y position for the UI panel.
    /// </summary>
    private const float InitialY = 850f;

    // Header dimensions

    /// <summary>
    /// Width of the "MULTIPLAYER" header text.
    /// </summary>
    private const float HeaderWidth = 400f;

    /// <summary>
    /// Height of the "MULTIPLAYER" header text.
    /// </summary>
    private const float HeaderHeight = 40f;

    /// <summary>
    /// Spacing between the header and the glowing notch.
    /// </summary>
    private const float HeaderToNotchSpacing = 45f;

    /// <summary>
    /// Spacing between the glowing notch and the main panel.
    /// </summary>
    private const float NotchToPanelSpacing = 35f;

    // Panel and element spacing

    /// <summary>
    /// Padding at the top of the background panel.
    /// </summary>
    private const float PanelPaddingTop = 35f;

    /// <summary>
    /// Standard height for text labels.
    /// </summary>
    private const float LabelHeight = 20f;

    /// <summary>
    /// Spacing between a label and its associated input field.
    /// </summary>
    private const float LabelToInputSpacing = 38f;

    /// <summary>
    /// Spacing between consecutive input fields.
    /// </summary>
    private const float InputSpacing = 30f;

    // Tab configuration

    /// <summary>
    /// Width of each tab button.
    /// </summary>
    private const float TabButtonWidth = 150f;

    /// <summary>
    /// Vertical spacing reserved for the tab row.
    /// </summary>
    private const float TabSpacing = 60f;

    // Content-specific spacing

    /// <summary>
    /// Height allocated for description text blocks.
    /// </summary>
    private const float DescriptionHeight = 40f;

    /// <summary>
    /// Spacing for the "JOIN SESSION" header.
    /// </summary>
    private const float JoinHeaderSpacing = 45f;

    /// <summary>
    /// Spacing for the join session description text.
    /// </summary>
    private const float JoinDescSpacing = 55f;

    /// <summary>
    /// Spacing for the Lobby ID label.
    /// </summary>
    private const float LobbyIdLabelSpacing = 42f;

    /// <summary>
    /// Spacing for simple text messages in the Steam tab.
    /// </summary>
    private const float SteamTextSpacing = 40f;

    /// <summary>
    /// Spacing between buttons in the Steam tab.
    /// </summary>
    private const float SteamButtonSpacing = 10f;

    /// <summary>
    /// Spacing for the Server Address label.
    /// </summary>
    private const float ServerAddressLabelSpacing = 44f;

    /// <summary>
    /// Spacing between Server Address input and the next element.
    /// </summary>
    private const float ServerAddressInputSpacing = 12f;

    /// <summary>
    /// Spacing for the Port label.
    /// </summary>
    private const float PortLabelSpacing = 38f;

    /// <summary>
    /// Y-offset for the feedback/error text relative to the content area.
    /// </summary>
    private const float FeedbackTextOffset = 310f;

    /// <summary>
    /// Height of the bottom feedback area so longer matchmaking messages can wrap cleanly.
    /// </summary>
    private const float FeedbackTextHeight = 72f;

    #endregion

    #region UI Text Constants

    /// <summary>
    /// Text displayed on buttons while a connection is being attempted.
    /// </summary>
    private static string ConnectingText => Lang.Pick("Connecting...", "正在连接……");

    /// <summary>
    /// Main title text for the multiplayer interface.
    /// </summary>
    private static string HeaderText => Lang.Pick("M U L T I P L A Y E R", "多 人 游 戏");

    /// <summary>
    /// Label text for the player identification section.
    /// </summary>
    private static string IdentityLabelText => Lang.Pick("Identity", "身份");

    /// <summary>
    /// Placeholder text for the username input field.
    /// </summary>
    private static string UsernamePlaceholder => Lang.Pick("Enter Username", "输入昵称");

    // Tab names

    /// <summary>
    /// Label for the Matchmaking tab.
    /// </summary>
    private static string MatchmakingTabText => Lang.Pick("Matchmaking", "匹配大厅");

    /// <summary>
    /// Label for the Steam tab.
    /// </summary>
    private const string SteamTabText = "Steam";

    /// <summary>
    /// Label for the Direct IP tab.
    /// </summary>
    private static string DirectIpTabText => Lang.Pick("Direct IP", "直连 IP");

    // Matchmaking tab

    /// <summary>
    /// Header text for the Matchmaking tab.
    /// </summary>
    private static string JoinSessionText => Lang.Pick("JOIN SESSION", "加入房间");

    /// <summary>
    /// Description/instructions for the Matchmaking tab.
    /// </summary>
    private static string JoinSessionDescText => Lang.Pick(
        "Enter the unique Lobby ID\nto join an existing session.",
        "输入房间 ID，\n加入一个已经开好的房间。"
    );

    /// <summary>
    /// Label for the Lobby ID input field.
    /// </summary>
    private static string LobbyIdLabelText => Lang.Pick("Lobby ID", "房间 ID");

    /// <summary>
    /// Placeholder text for the Lobby ID input.
    /// </summary>
    private static string LobbyIdPlaceholder => Lang.Pick("e.g. 8x92-AC44", "例如 8x92-AC44");

    /// <summary>
    /// Text for the Connect button in the Matchmaking tab.
    /// </summary>
    private static string LobbyConnectButtonText => Lang.Pick("CONNECT", "连接");

    /// <summary>
    /// Text for the Host Lobby button in the Matchmaking tab.
    /// </summary>
    private static string HostLobbyButtonText => Lang.Pick("HOST LOBBY", "创建房间");

    // Steam tab

    /// <summary>
    /// Status text when connected to Steam.
    /// </summary>
    private static string SteamConnectedText => Lang.Pick("Connected to Steam Workshop", "已连接到 Steam");

    /// <summary>
    /// Text for the Create Lobby button.
    /// </summary>
    private static string CreateLobbyButtonText => Lang.Pick("+ CREATE LOBBY", "+ 创建房间");

    /// <summary>
    /// Text for the Browse Lobbies button.
    /// </summary>
    private static string BrowseLobbyButtonText => Lang.Pick("☰ BROWSE PUBLIC LOBBIES", "☰ 浏览公开房间");

    /// <summary>
    /// Text for the Join Friend button.
    /// </summary>
    private static string JoinFriendButtonText => Lang.Pick("→ JOIN FRIEND (INVITE)", "→ 加入好友（邀请）");

    // Direct IP tab

    /// <summary>
    /// Label for the Server Address input.
    /// </summary>
    private static string ServerAddressLabelText => Lang.Pick("Server Address", "服务器地址");

    /// <summary>
    /// Placeholder for the Server Address input.
    /// </summary>
    private const string ServerAddressPlaceholder = "127.0.0.1";

    /// <summary>
    /// Label for the Port input.
    /// </summary>
    private static string PortLabelText => Lang.Pick("Port", "端口");

    /// <summary>
    /// Placeholder for the Port input.
    /// </summary>
    private const string PortPlaceholder = "26960";

    /// <summary>
    /// Text for the Connect button in Direct IP tab. Public for access by helpers if needed.
    /// </summary>
    public static string DirectConnectButtonText => Lang.Pick("CONNECT", "连接");

    /// <summary>
    /// Text for the Host button in Direct IP tab.
    /// </summary>
    private static string HostButtonText => Lang.Pick("HOST", "创建");

    #endregion

    #region Error & Status Messages

    /// <summary>
    /// Error message when address input is missing.
    /// </summary>
    private static string ErrorEnterAddress => Lang.Pick(
        "Failed to connect:\nYou must enter an address",
        "连接失败：\n必须填写地址"
    );

    /// <summary>
    /// Error message when port input is invalid or missing.
    /// </summary>
    private static string ErrorEnterValidPort => Lang.Pick(
        "Failed to connect:\nYou must enter a valid port",
        "连接失败：\n必须填写有效的端口"
    );

    /// <summary>
    /// Error message when hosting port is invalid.
    /// </summary>
    private static string ErrorEnterValidPortHost => Lang.Pick(
        "Failed to host:\nYou must enter a valid port",
        "创建失败：\n必须填写有效的端口"
    );

    /// <summary>
    /// Status message for successful connection.
    /// </summary>
    private static string MsgConnected => Lang.Pick("Successfully connected", "连接成功");

    /// <summary>
    /// Error message for addon mismatch.
    /// </summary>
    private static string ErrorInvalidAddons => Lang.Pick(
        "Failed to connect:\nInvalid addons",
        "连接失败：\n附加组件对不上"
    );

    /// <summary>
    /// Error message for internal exceptions (Socket/IO).
    /// </summary>
    private static string ErrorInternal => Lang.Pick("Failed to connect:\nInternal error", "连接失败：\n内部错误");

    /// <summary>
    /// Error message for connection timeout.
    /// </summary>
    private static string ErrorTimeout => Lang.Pick("Failed to connect:\nConnection timed out", "连接失败：\n连接超时");

    /// <summary>
    /// Fallback error message for unknown failures.
    /// </summary>
    private static string ErrorUnknown => Lang.Pick("Failed to connect:\nUnknown reason", "连接失败：\n原因不明");

    /// <summary>
    /// Warning shown when matchmaking HTTP traffic and UDP discovery use different network paths.
    /// </summary>
    private static string ErrorSplitTunnelDetected => Lang.Pick(
        "Failed to connect:\nMatchmaking detected split-tunneling or interfering network software. " +
        "Ensure MMS and gameplay traffic use the same network path.",
        "连接失败：\n匹配服务检测到分流，或者有别的网络软件在干扰。" +
        "请让 MMS 和游戏的流量走同一条网络路径。"
    );

    /// <summary>
    /// Large blocking message shown when the client must update before using matchmaking.
    /// </summary>
    private static string MatchmakingUpdateRequiredText => Lang.Pick(
        "Please update to the latest version in order to use matchmaking!",
        "请先更新到最新版本，才能使用匹配功能！"
    );

    /// <summary>
    /// Temporary message shown while the client verifies matchmaking compatibility.
    /// </summary>
    private static string MatchmakingCheckingText => Lang.Pick(
        "Checking matchmaking compatibility...",
        "正在检查匹配兼容性……"
    );

    /// <summary>
    /// Blocking message shown when MMS cannot be reached, so matchmaking stays unavailable.
    /// </summary>
    private static string MatchmakingUnavailableText => Lang.Pick(
        "Unable to contact matchmaking server right now.",
        "现在联系不上匹配服务器。"
    );

    #endregion

    #region Fields

    /// <summary>
    /// Persistent mod settings for storing connection preferences.
    /// </summary>
    private readonly ModSettings _modSettings;

    // Core UI components
    /// <summary>
    /// Input field for the username.
    /// </summary>
    private readonly IInputComponent _usernameInput;

    /// <summary>
    /// Text component used to display status messages and errors.
    /// </summary>
    private readonly ITextComponent _feedbackText;

    /// <summary>
    /// Main background panel of the interface.
    /// </summary>
    private readonly GameObject _backgroundPanel;

    /// <summary>
    /// Decorative glowing notch at the top of the interface.
    /// </summary>
    private readonly GameObject _glowingNotch;

    /// <summary>
    /// Component group containing the background elements.
    /// </summary>
    private readonly ComponentGroup _backgroundGroup;

    // Tab system
    /// <summary>
    /// Tab button for the Matchmaking section.
    /// </summary>
    private readonly TabButtonComponent _matchmakingTab;

    /// <summary>
    /// Tab button for the Steam section (nullable if Steam is not active).
    /// </summary>
    private readonly TabButtonComponent? _steamTab;

    /// <summary>
    /// Tab button for the Direct IP section.
    /// </summary>
    private readonly TabButtonComponent _directIpTab;

    // Tab content groups
    /// <summary>
    /// Component group holding Matchmaking tab content.
    /// </summary>
    private readonly ComponentGroup _matchmakingGroup;

    /// <summary>
    /// Component group holding Steam tab content.
    /// </summary>
    private readonly ComponentGroup? _steamGroup;

    /// <summary>
    /// Component group holding Direct IP tab content.
    /// </summary>
    private readonly ComponentGroup _directIpGroup;

    // Matchmaking tab components
    /// <summary>
    /// Input field for entering a Lobby ID.
    /// </summary>
    // ReSharper disable once NotAccessedField.Local
    private readonly IInputComponent _lobbyIdInput;

    /// <summary>
    /// Button to connect to a lobby via ID.
    /// </summary>
    private readonly IButtonComponent _lobbyConnectButton;

    /// <summary>
    /// Scrollable panel for browsing public lobbies.
    /// </summary>
    private readonly LobbyBrowserPanel _lobbyBrowserPanel;

    /// <summary>
    /// Configuration panel for hosting a matchmaking lobby.
    /// </summary>
    private readonly LobbyConfigPanel _lobbyConfigPanel;

    // Steam tab components
    /// <summary>
    /// Button to create a new Steam lobby.
    /// </summary>
    // ReSharper disable once NotAccessedField.Local
    private IButtonComponent? _createLobbyButton;

    /// <summary>
    /// Button to open the lobby browser.
    /// </summary>
    // ReSharper disable once NotAccessedField.Local
    private IButtonComponent? _browseLobbyButton;

    /// <summary>
    /// Scrollable panel for browsing public lobbies on Steam tab.
    /// </summary>
    private readonly LobbyBrowserPanel? _steamLobbyBrowserPanel;

    /// <summary>
    /// Configuration panel for hosting a Steam lobby.
    /// </summary>
    private readonly LobbyConfigPanel? _steamLobbyConfigPanel;

    /// <summary>
    /// The screen a host waits on until their teammate is in, before any save is chosen.
    /// </summary>
    private readonly WaitingRoomPanel _waitingRoomPanel;

    /// <summary>
    /// Everyone in while the waiting room is up, the local player first.
    /// </summary>
    private readonly List<string> _waitingRoomPlayers = [];

    /// <summary>
    /// Whether the host is still waiting, so that leaving the tab and coming back shows the waiting room again
    /// instead of the buttons that would start a second lobby.
    /// </summary>
    private bool _waitingRoomActive;

    /// <summary>
    /// The lobby the waiting room belongs to, kept for inviting people to it.
    /// </summary>
    private CSteamID _waitingRoomLobbyId;

    /// <summary>
    /// Whether the local player is the one hosting the waiting room, who alone has a lobby to invite to.
    /// </summary>
    private bool _waitingRoomHosting;

    /// <summary>
    /// Whether the local player has said they are ready to choose a save.
    /// </summary>
    private bool _waitingRoomLocalReady;

    /// <summary>
    /// The other players who have said they are ready, by name.
    /// </summary>
    private readonly HashSet<string> _waitingRoomReadyNames = [];

    /// <summary>
    /// Whether the room has already sent everyone off to choose their saves, so that it happens once.
    /// </summary>
    private bool _waitingRoomProceeded;

    /// <summary>
    /// Button to join a friend via invite.
    /// </summary>
    // ReSharper disable once NotAccessedField.Local
    private IButtonComponent? _joinFriendButton;

    /// <summary>
    /// Steam lobby ID for a newly created hosted Steam lobby that still needs post-start finalization.
    /// </summary>
    private string? _pendingHostedSteamLobbyId;

    /// <summary>
    /// Whether the pending hosted Steam lobby should be registered with MMS after the host is ready.
    /// </summary>
    private bool _pendingHostedSteamLobbyIsPublic;

    // Direct IP tab components
    /// <summary>
    /// Input field for the server IP address.
    /// </summary>
    private readonly IInputComponent _addressInput;

    /// <summary>
    /// Input field for the server port.
    /// </summary>
    private readonly IInputComponent _portInput;

    /// <summary>
    /// Button to connect via Direct IP.
    /// </summary>
    private readonly IButtonComponent _directConnectButton;

    /// <summary>
    /// Button to start hosting a server.
    /// </summary>
    private readonly IButtonComponent _serverButton;

    /// <summary>
    /// Coroutine handle for hiding feedback text after a delay.
    /// </summary>
    private Coroutine? _feedbackHideCoroutine;

    /// <summary>
    /// Whether the current bottom feedback message is being driven by matchmaking status UI.
    /// </summary>
    private bool _isMatchmakingFeedbackActive;

    /// <summary>
    /// Whether MMS has reported that this client is too old for matchmaking.
    /// </summary>
    private bool _isMatchmakingVersionBlocked;

    /// <summary>
    /// Whether the UI is currently verifying matchmaking compatibility with MMS.
    /// </summary>
    private bool _isCheckingMatchmakingVersion;

    /// <summary>
    /// Whether MMS has been reached successfully and the matchmaking version check passed.
    /// </summary>
    private bool _isMatchmakingReady;

    /// <summary>
    /// Tracks the currently selected tab so async UI refreshes preserve the active view.
    /// </summary>
    private Tab _activeTab = Tab.Matchmaking;

    /// <summary>
    /// If non-null, indicates the Lobby ID we should retry connecting to once if a hole punch times out.
    /// </summary>
    private string? _pendingHolePunchRetryLobbyId;

    /// <summary>
    /// Whether a matchmaking lobby join is already in progress.
    /// </summary>
    private bool _isLobbyJoinInProgress;

    /// <summary>
    /// Public accessor for the MMS client.
    /// Used by server manager to pass to HolePunch transport for lobby cleanup.
    /// </summary>
    public MmsClient MmsClient { get; }

    #endregion

    #region Events

    /// <summary>
    /// Fired when the user attempts to connect to a server.
    /// Parameters: address, port, username, transportType, fallbackAddress
    /// </summary>
    public event Action<string, int, string, TransportType, string?>? ConnectButtonPressed;

    /// <summary>
    /// Fired when the user attempts to start hosting a server.
    /// Parameters: address, port, username, transportType, fallbackAddress
    /// </summary>
    public event Action<string, int, string, TransportType, string?>? StartHostButtonPressed;

    /// <summary>
    /// Raised to start hosting without opening the save selection, so the host waits for their teammate first.
    /// </summary>
    public event Action<string, int, string, TransportType, string?>? StartHostWithoutSavePressed;

    /// <summary>
    /// Raised when a host that is already hosting is done waiting and wants to choose their save.
    /// </summary>
    public event Action? HostSaveSelectionRequested;

    /// <summary>
    /// Raised when a player who joined is done waiting and chooses their save, which their game held back until now.
    /// </summary>
    public event Action? JoinSaveSelectionRequested;

    /// <summary>
    /// Raised when the local player says they are ready, or takes it back, so the other player is told.
    /// </summary>
    public event Action<bool>? WaitingRoomReadyToggled;

    /// <summary>
    /// Raised when a host gives up waiting, so the server they opened is closed again.
    /// </summary>
    public event Action? StopWaitingRoomHostingEvent;

    #endregion

    #region Tab Enum

    /// <summary>
    /// Available tabs in the connection interface.
    /// </summary>
    private enum Tab {
        Matchmaking,
        Steam,
        DirectIp
    }

    #endregion

    #region Initialization

    /// <summary>
    /// Initializes the multiplayer connection interface with all tabs and UI elements.
    /// </summary>
    /// <param name="modSettings">Persistent mod settings for storing connection preferences.</param>
    /// <param name="connectGroup">Parent component group for the interface.</param>
    public ConnectInterface(ModSettings modSettings, ComponentGroup connectGroup) {
        _modSettings = modSettings;
        MmsClient = new MmsClient(modSettings.MmsSettings.MmsUrl, modSettings.MmsSettings.UdpDiscoveryPort);

        SubscribeToSteamEvents();

        var currentY = InitialY;

        _backgroundGroup = new ComponentGroup(parent: connectGroup);

        // Build UI from top to bottom
        CreateHeader(connectGroup, ref currentY);
        _glowingNotch = CreateNotch(ref currentY);
        _backgroundPanel = CreateBackgroundPanel(ref currentY);

        _usernameInput = CreateUsernameSection(ref currentY);
        var tabElements = CreateTabButtons(ref currentY);
        _matchmakingTab = tabElements.matchmaking;
        _steamTab = tabElements.steam;
        _directIpTab = tabElements.directIp;

        // Create tab-specific content
        var matchmakingComponents = CreateMatchmakingTab(currentY);
        _matchmakingGroup = matchmakingComponents.group;
        _lobbyIdInput = matchmakingComponents.lobbyIdInput;
        _lobbyConnectButton = matchmakingComponents.connectButton;

        // Create lobby browser panel
        _lobbyBrowserPanel = new LobbyBrowserPanel(
            _backgroundGroup,
            new Vector2(InitialX, currentY),
            new Vector2(ContentWidth, 280f)
        );
        _lobbyBrowserPanel.SetOnLobbySelected(lobby => {
                _lobbyIdInput.SetInput(lobby.LobbyCode);
                _lobbyBrowserPanel.Hide();
                _matchmakingGroup.SetActive(true);
                ShowFeedback(Color.green, Lang.Pick($"Selected lobby: {lobby.LobbyCode}", $"已选择房间：{lobby.LobbyCode}"));
            }
        );
        _lobbyBrowserPanel.SetOnBack(() => {
                _lobbyBrowserPanel.Hide();
                _matchmakingGroup.SetActive(true);
            }
        );
        _lobbyBrowserPanel.SetOnRefresh(() => { MonoBehaviourUtil.Instance.StartCoroutine(FetchLobbiesCoroutine()); });

        _waitingRoomPanel = new WaitingRoomPanel(
            _backgroundGroup,
            new Vector2(InitialX, currentY),
            new Vector2(ContentWidth, 280f + WaitingRoomPanel.NameSettingsHeight),
            _modSettings
        );
        _waitingRoomPanel.SetOnInvite(() => {
                if (!SteamManager.IsInitialized || _waitingRoomLobbyId == default) {
                    ShowFeedback(Color.red, Lang.Pick("Cannot invite: no Steam lobby is open.", "没法邀请：当前没有打开的 Steam 房间。"));
                    return;
                }

                // Steam's own invite dialog, so the host never has to go looking for the overlay themselves
                SteamFriends.ActivateGameOverlayInviteDialog(_waitingRoomLobbyId);
            }
        );
        _waitingRoomPanel.SetOnReady(() => {
                _waitingRoomLocalReady = !_waitingRoomLocalReady;
                WaitingRoomReadyToggled?.Invoke(_waitingRoomLocalReady);
                RefreshWaitingRoom();
            }
        );
        _waitingRoomPanel.SetOnLeave(() => {
                HideWaitingRoom();
                _steamGroup?.SetActive(_activeTab == Tab.Steam);
                ShowFeedback(Color.yellow, Lang.Pick("Stopped waiting. Your game is closed again.", "已停止等待。你的游戏重新关上了。"));
                StopWaitingRoomHostingEvent?.Invoke();
            }
        );

        var steamComponents = CreateSteamTab(currentY);
        _steamGroup = steamComponents.group;
        _createLobbyButton = steamComponents.createButton;
        _browseLobbyButton = steamComponents.browseButton;
        _joinFriendButton = steamComponents.joinButton;

        // Create Steam lobby browser panel (same layout as matchmaking)
        if (_steamGroup != null) {
            _steamLobbyBrowserPanel = new LobbyBrowserPanel(
                _backgroundGroup,
                new Vector2(InitialX, currentY),
                new Vector2(ContentWidth, 280f)
            );
            _steamLobbyBrowserPanel.SetOnLobbySelected(lobby => {
                    _steamLobbyBrowserPanel.Hide();
                    _steamGroup.SetActive(true);

                    // Steam lobbies join via Steam ID (ConnectionData)
                    if (lobby.LobbyType == PublicLobbyType.Steam) {
                        JoinSteamLobbyFromBrowser(lobby.ConnectionData);
                    } else {
                        ShowFeedback(Color.red, Lang.Pick("Invalid Steam lobby", "无效的 Steam 房间"));
                    }
                }
            );
            _steamLobbyBrowserPanel.SetOnBack(() => {
                    _steamLobbyBrowserPanel.Hide();
                    _steamGroup.SetActive(true);
                }
            );
            _steamLobbyBrowserPanel.SetOnRefresh(() => {
                    MonoBehaviourUtil.Instance.StartCoroutine(FetchSteamLobbiesCoroutine());
                }
            );

            // Create Steam lobby config panel
            _steamLobbyConfigPanel = new LobbyConfigPanel(
                _backgroundGroup,
                new Vector2(InitialX, currentY),
                new Vector2(ContentWidth, 280f),
                PublicLobbyType.Steam
            );
            _steamLobbyConfigPanel.SetOnCreate(visibility => {
                    _steamLobbyConfigPanel.Hide();
                    _steamGroup?.SetActive(true);
                    CreateSteamLobbyWithConfig(visibility);
                }
            );
            _steamLobbyConfigPanel.SetOnCancel(() => {
                    _steamLobbyConfigPanel.Hide();
                    _steamGroup?.SetActive(true);
                }
            );
        }

        // Create matchmaking lobby config panel
        _lobbyConfigPanel = new LobbyConfigPanel(
            _backgroundGroup,
            new Vector2(InitialX, currentY),
            new Vector2(ContentWidth, 280f)
        );
        _lobbyConfigPanel.SetOnCreate(visibility => {
                _lobbyConfigPanel.Hide();
                _matchmakingGroup.SetActive(true);
                CreateMatchmakingLobbyWithConfig(visibility);
            }
        );
        _lobbyConfigPanel.SetOnCancel(() => {
                _lobbyConfigPanel.Hide();
                _matchmakingGroup.SetActive(true);
            }
        );

        var directIpComponents = CreateDirectIpTab(currentY);
        _directIpGroup = directIpComponents.group;
        _addressInput = directIpComponents.addressInput;
        _portInput = directIpComponents.portInput;
        _directConnectButton = directIpComponents.connectButton;
        _serverButton = directIpComponents.hostButton;

        currentY -= FeedbackTextOffset / UiManager.ScreenHeightRatio;

        _feedbackText = CreateFeedbackText(currentY);

        FinalizeLayout();

        BeginMatchmakingVersionCheck();
        SwitchTab(Tab.Matchmaking);
    }

    /// <summary>
    /// Subscribes to Steam lobby-related events if Steam is available.
    /// </summary>
    private void SubscribeToSteamEvents() {
        SteamManager.LobbyListReceivedEvent += OnLobbyListReceived;
        SteamManager.LobbyJoinedEvent += OnLobbyJoined;
    }

    /// <summary>
    /// Creates the main header text at the top of the interface.
    /// </summary>
    private void CreateHeader(ComponentGroup parent, ref float currentY) {
        new TextComponent(
            parent,
            new Vector2(InitialX, currentY),
            new Vector2(HeaderWidth, HeaderHeight),
            HeaderText,
            fontSize: 32,
            alignment: TextAnchor.MiddleCenter
        );

        currentY -= HeaderToNotchSpacing / UiManager.ScreenHeightRatio;
    }

    /// <summary>
    /// Creates the glowing decorative notch below the header.
    /// </summary>
    private GameObject CreateNotch(ref float currentY) {
        var notch = ConnectInterfaceHelpers.CreateGlowingNotch(InitialX, currentY);
        return notch;
    }

    /// <summary>
    /// Creates the main background panel with resolution-aware height scaling.
    /// </summary>
    private GameObject CreateBackgroundPanel(ref float currentY) {
        return ConnectInterfaceHelpers.CreateBackgroundPanel(
            InitialX,
            currentY - NotchToPanelSpacing / UiManager.ScreenHeightRatio
        );
    }

    /// <summary>
    /// Creates the username input section at the top of the panel.
    /// </summary>
    private IInputComponent CreateUsernameSection(ref float currentY) {
        currentY -= (NotchToPanelSpacing + PanelPaddingTop) / UiManager.ScreenHeightRatio;

        new TextComponent(
            _backgroundGroup,
            new Vector2(InitialX + TextIndentWidth, currentY),
            new Vector2(ContentWidth, LabelHeight),
            IdentityLabelText,
            UiManager.NormalFontSize,
            alignment: TextAnchor.MiddleLeft
        );

        currentY -= LabelToInputSpacing / UiManager.ScreenHeightRatio;

        var usernameInput = new InputComponent(
            _backgroundGroup,
            new Vector2(InitialX, currentY),
            new Vector2(ContentWidth, UniformHeight),
            _modSettings.Username,
            UsernamePlaceholder,
            characterLimit: 32,
            onValidateInput: (_, _, addedChar) => char.IsLetterOrDigit(addedChar) ? addedChar : '\0'
        );

        currentY -= (UniformHeight + InputSpacing) / UiManager.ScreenHeightRatio;

        return usernameInput;
    }

    /// <summary>
    /// Creates the tab navigation buttons (Matchmaking, Steam, Direct IP).
    /// </summary>
    private (TabButtonComponent matchmaking, TabButtonComponent? steam, TabButtonComponent directIp)
        CreateTabButtons(ref float currentY) {
        var matchmaking = ConnectInterfaceHelpers.CreateTabButton(
            _backgroundGroup,
            InitialX - TabButtonWidth,
            currentY,
            TabButtonWidth,
            MatchmakingTabText,
            () => SwitchTab(Tab.Matchmaking)
        );

        TabButtonComponent? steam = null;
        if (SteamManager.IsInitialized) {
            steam = ConnectInterfaceHelpers.CreateTabButton(
                _backgroundGroup,
                InitialX,
                currentY,
                TabButtonWidth,
                SteamTabText,
                () => SwitchTab(Tab.Steam)
            );
        }

        // Position DirectIp tab next to Steam, or in center if Steam not available
        var directIpX = SteamManager.IsInitialized ? InitialX + TabButtonWidth : InitialX;
        var directIp = ConnectInterfaceHelpers.CreateTabButton(
            _backgroundGroup,
            directIpX,
            currentY,
            TabButtonWidth,
            DirectIpTabText,
            () => SwitchTab(Tab.DirectIp)
        );

        currentY -= TabSpacing / UiManager.ScreenHeightRatio;

        return (matchmaking, steam, directIp);
    }

    #endregion

    #region Tab Content Creation

    /// <summary>
    /// Creates the Matchmaking tab content with lobby ID input and connect/host buttons.
    /// </summary>
    private (ComponentGroup group, IInputComponent lobbyIdInput, IButtonComponent connectButton, IButtonComponent
        hostButton)
        CreateMatchmakingTab(float startY) {
        var group = new ComponentGroup(parent: _backgroundGroup);
        var y = startY;

        // Header
        new TextComponent(
            group,
            new Vector2(InitialX, y),
            new Vector2(ContentWidth, LabelHeight),
            JoinSessionText,
            fontSize: 18,
            alignment: TextAnchor.MiddleCenter
        );
        y -= JoinHeaderSpacing / UiManager.ScreenHeightRatio;

        // Description
        new TextComponent(
            group,
            new Vector2(InitialX, y),
            new Vector2(ContentWidth, DescriptionHeight),
            JoinSessionDescText,
            UiManager.SubTextFontSize,
            alignment: TextAnchor.MiddleCenter
        );
        y -= JoinDescSpacing / UiManager.ScreenHeightRatio;

        // Lobby ID label
        new TextComponent(
            group,
            new Vector2(InitialX + TextIndentWidth, y),
            new Vector2(ContentWidth, LabelHeight),
            LobbyIdLabelText,
            UiManager.NormalFontSize,
            alignment: TextAnchor.MiddleLeft
        );
        y -= LobbyIdLabelSpacing / UiManager.ScreenHeightRatio;

        // Lobby ID input
        var lobbyIdInput = new InputComponent(
            group,
            new Vector2(InitialX, y),
            new Vector2(ContentWidth, UniformHeight),
            "",
            LobbyIdPlaceholder,
            characterLimit: 12
        );
        y -= (UniformHeight + 20f) / UiManager.ScreenHeightRatio;

        // Two buttons side-by-side (same layout as Direct IP tab)
        var buttonGap = 10f;
        var buttonWidth = (ContentWidth - buttonGap) / 2f;
        var buttonOffset = ((buttonWidth + buttonGap) / 2f) /
                           (float) System.Math.Pow(UiManager.ScreenHeightRatio, 2);

        // Connect button (left)
        var connectButton = new ButtonComponent(
            group,
            new Vector2(InitialX - buttonOffset, y),
            new Vector2(buttonWidth, UniformHeight),
            LobbyConnectButtonText,
            Resources.TextureManager.ButtonBg,
            Resources.FontManager.UIFontRegular,
            UiManager.NormalFontSize
        );
        connectButton.SetOnPress(OnLobbyConnectButtonPressed);

        // Host Lobby button (right)
        var hostButton = new ButtonComponent(
            group,
            new Vector2(InitialX + buttonOffset, y),
            new Vector2(buttonWidth, UniformHeight),
            HostLobbyButtonText,
            Resources.TextureManager.ButtonBg,
            Resources.FontManager.UIFontRegular,
            UiManager.NormalFontSize
        );
        hostButton.SetOnPress(OnHostLobbyButtonPressed);

        y -= (UniformHeight + 15f) / UiManager.ScreenHeightRatio;

        // Browse Lobbies button (full width)
        var browseButton = new ButtonComponent(
            group,
            new Vector2(InitialX, y),
            new Vector2(ContentWidth, UniformHeight),
            Lang.Pick("☰ BROWSE PUBLIC LOBBIES", "☰ 浏览公开房间"),
            Resources.TextureManager.ButtonBg,
            Resources.FontManager.UIFontRegular,
            UiManager.NormalFontSize
        );
        browseButton.SetOnPress(OnBrowseMatchmakingLobbiesPressed);

        return (group, lobbyIdInput, connectButton, hostButton);
    }

    /// <summary>
    /// Creates the Steam tab content with lobby management buttons.
    /// </summary>
    private (ComponentGroup? group, IButtonComponent? createButton, IButtonComponent? browseButton,
        IButtonComponent? joinButton) CreateSteamTab(float startY) {
        if (!SteamManager.IsInitialized) {
            return (null, null, null, null);
        }

        var group = new ComponentGroup(activeSelf: false, parent: _backgroundGroup);
        var y = startY;

        // Status text
        new TextComponent(
            group,
            new Vector2(InitialX, y),
            new Vector2(ContentWidth, LabelHeight),
            SteamConnectedText,
            UiManager.SubTextFontSize,
            alignment: TextAnchor.MiddleCenter
        );
        y -= SteamTextSpacing / UiManager.ScreenHeightRatio;

        // Create lobby button
        var createButton = new ButtonComponent(
            group,
            new Vector2(InitialX, y),
            new Vector2(ContentWidth, UniformHeight),
            CreateLobbyButtonText,
            Resources.TextureManager.ButtonBg,
            Resources.FontManager.UIFontRegular,
            UiManager.NormalFontSize
        );
        createButton.SetOnPress(OnCreateLobbyButtonPressed);
        y -= (UniformHeight + SteamButtonSpacing) / UiManager.ScreenHeightRatio;

        // Browse lobbies button
        var browseButton = new ButtonComponent(
            group,
            new Vector2(InitialX, y),
            new Vector2(ContentWidth, UniformHeight),
            BrowseLobbyButtonText,
            Resources.TextureManager.ButtonBg,
            Resources.FontManager.UIFontRegular,
            UiManager.NormalFontSize
        );
        browseButton.SetOnPress(OnBrowseLobbyButtonPressed);
        y -= (UniformHeight + SteamButtonSpacing) / UiManager.ScreenHeightRatio;

        // Join friend button
        var joinButton = new ButtonComponent(
            group,
            new Vector2(InitialX, y),
            new Vector2(ContentWidth, UniformHeight),
            JoinFriendButtonText,
            Resources.TextureManager.ButtonBg,
            Resources.FontManager.UIFontRegular,
            UiManager.NormalFontSize
        );
        joinButton.SetOnPress(OnJoinFriendButtonPressed);

        return (group, createButton, browseButton, joinButton);
    }

    /// <summary>
    /// Creates the Direct IP tab content with address/port inputs and connect/host buttons.
    /// </summary>
    private (ComponentGroup group, IInputComponent addressInput, IInputComponent portInput,
        IButtonComponent connectButton, IButtonComponent hostButton)
        CreateDirectIpTab(float startY) {
        var group = new ComponentGroup(activeSelf: false, parent: _backgroundGroup);
        var y = startY;

        // Server address section
        new TextComponent(
            group,
            new Vector2(InitialX + TextIndentWidth, y),
            new Vector2(ContentWidth, LabelHeight),
            ServerAddressLabelText,
            UiManager.NormalFontSize,
            alignment: TextAnchor.MiddleLeft
        );
        y -= ServerAddressLabelSpacing / UiManager.ScreenHeightRatio;

        var addressInput = new IpInputComponent(
            group,
            new Vector2(InitialX, y),
            new Vector2(ContentWidth, UniformHeight),
            _modSettings.ConnectAddress,
            ServerAddressPlaceholder
        );
        y -= (UniformHeight + ServerAddressInputSpacing) / UiManager.ScreenHeightRatio;

        // Port section
        new TextComponent(
            group,
            new Vector2(InitialX + TextIndentWidth, y),
            new Vector2(ContentWidth, LabelHeight),
            PortLabelText,
            UiManager.NormalFontSize,
            alignment: TextAnchor.MiddleLeft
        );
        y -= PortLabelSpacing / UiManager.ScreenHeightRatio;

        var joinPort = _modSettings.ConnectPort;
        var portInput = new PortInputComponent(
            group,
            new Vector2(InitialX, y),
            new Vector2(ContentWidth, UniformHeight),
            joinPort == -1 ? "" : joinPort.ToString(),
            PortPlaceholder
        );
        y -= (UniformHeight + 20f) / UiManager.ScreenHeightRatio;

        // Direct IP button values
        var buttonGap = 10f;
        var buttonWidth = (ContentWidth - buttonGap) / 2f;
        var buttonOffset = ((buttonWidth + buttonGap) / 2f) /
                           (float) System.Math.Pow(UiManager.ScreenHeightRatio, 2);

        // Connect button (left)
        var connectButton = new ButtonComponent(
            group,
            new Vector2(InitialX - buttonOffset, y),
            new Vector2(buttonWidth, UniformHeight),
            DirectConnectButtonText,
            Resources.TextureManager.ButtonBg,
            Resources.FontManager.UIFontRegular,
            UiManager.NormalFontSize
        );
        connectButton.SetOnPress(OnDirectConnectButtonPressed);

        // Host button (right)
        var hostButton = new ButtonComponent(
            group,
            new Vector2(InitialX + buttonOffset, y),
            new Vector2(buttonWidth, UniformHeight),
            HostButtonText,
            Resources.TextureManager.ButtonBg,
            Resources.FontManager.UIFontRegular,
            UiManager.NormalFontSize
        );
        hostButton.SetOnPress(OnStartButtonPressed);

        return (group, addressInput, portInput, connectButton, hostButton);
    }

    /// <summary>
    /// Creates the feedback text component that displays connection status and errors.
    /// </summary>
    private ITextComponent CreateFeedbackText(float contentY) {
        var feedback = new TextComponent(
            _backgroundGroup,
            new Vector2(InitialX, contentY),
            new Vector2(ContentWidth, FeedbackTextHeight),
            new Vector2(0.5f, 1f),
            "",
            UiManager.SubTextFontSize,
            alignment: TextAnchor.UpperCenter,
            wrap: true
        );

        feedback.SetActive(false);

        return feedback;
    }

    /// <summary>
    /// Finalizes the UI layout by reparenting components and positioning tabs correctly.
    /// </summary>
    private void FinalizeLayout() {
        ConnectInterfaceHelpers.ReparentComponentGroup(_backgroundGroup, _backgroundPanel);
        ConnectInterfaceHelpers.PositionTabButtonsFixed(
            _backgroundPanel,
            _matchmakingTab,
            _steamTab,
            _directIpTab
        );

        LogFinalPositions();
    }

    /// <summary>
    /// Logs the final Unity RectTransform positions after reparenting for debugging.
    /// </summary>
    private void LogFinalPositions() {
        if (_portInput is Component.Component portComp &&
            _directConnectButton is Component.Component connectComp &&
            _serverButton is Component.Component hostComp) {
            var portRect = portComp.GameObject.GetComponent<RectTransform>();
            var connectRect = connectComp.GameObject.GetComponent<RectTransform>();
            var hostRect = hostComp.GameObject.GetComponent<RectTransform>();

            Logger.Info($"[ConnectInterface] Final Unity positions:");
            Logger.Info($"  Port: pos={portRect.anchoredPosition}, size={portRect.sizeDelta}");
            Logger.Info($"  Connect: pos={connectRect.anchoredPosition}, size={connectRect.sizeDelta}");
            Logger.Info($"  Host: pos={hostRect.anchoredPosition}, size={hostRect.sizeDelta}");
        }
    }

    #endregion

    #region Tab Management

    /// <summary>
    /// Switches the active tab and updates button states and content visibility.
    /// </summary>
    /// <param name="tab">The tab to activate.</param>
    private void SwitchTab(Tab tab) {
        _activeTab = tab;

        // Hide lobby browsers and config panels if visible
        _lobbyBrowserPanel.Hide();
        _steamLobbyBrowserPanel?.Hide();
        _lobbyConfigPanel.Hide();
        _steamLobbyConfigPanel?.Hide();
        // Update tab button visual states
        _matchmakingTab.SetTabActive(tab == Tab.Matchmaking);
        _steamTab?.SetTabActive(tab == Tab.Steam);
        _directIpTab.SetTabActive(tab == Tab.DirectIp);

        // Show only the active tab's content
        _matchmakingGroup.SetActive(
            tab == Tab.Matchmaking &&
            _isMatchmakingReady &&
            !_isMatchmakingVersionBlocked &&
            !_isCheckingMatchmakingVersion
        );
        _steamGroup?.SetActive(tab == Tab.Steam);
        _directIpGroup.SetActive(tab == Tab.DirectIp);

        // A host that is still waiting gets their waiting room back rather than the buttons that would open a
        // second lobby underneath it
        _waitingRoomPanel.Hide();
        if (_waitingRoomActive && tab == Tab.Steam) {
            _steamGroup?.SetActive(false);
            _waitingRoomPanel.Show();
        }

        RefreshMatchmakingStatusFeedback();
    }

    /// <summary>
    /// Shows the waiting room for a host that just opened their game, listing only themselves to begin with.
    /// </summary>
    /// <param name="lobbyId">The lobby that people are invited to.</param>
    private void ShowWaitingRoom(CSteamID lobbyId) {
        _waitingRoomLobbyId = lobbyId;
        OpenWaitingRoom(hosting: true, [_usernameInput.GetInput()]);
    }

    /// <summary>
    /// Shows the waiting room to a player who just joined someone else's game, listing everyone already in. Their own
    /// save selection stays shut until both players are ready, so that neither of them is left staring at a menu the
    /// other cannot see.
    /// </summary>
    /// <param name="others">The players already on the server.</param>
    public void ShowJoinWaitingRoom(IReadOnlyList<string> others) {
        var players = new List<string> { _usernameInput.GetInput() };
        players.AddRange(others);
        OpenWaitingRoom(hosting: false, players);
    }

    /// <summary>
    /// Puts the waiting room on screen with the given players, nobody ready yet.
    /// </summary>
    private void OpenWaitingRoom(bool hosting, List<string> players) {
        _waitingRoomActive = true;
        _waitingRoomHosting = hosting;
        _waitingRoomLocalReady = false;
        _waitingRoomProceeded = false;
        _waitingRoomReadyNames.Clear();

        _waitingRoomPlayers.Clear();
        _waitingRoomPlayers.AddRange(players);

        _steamGroup?.SetActive(false);
        _steamLobbyConfigPanel?.Hide();
        _steamLobbyBrowserPanel?.Hide();

        _waitingRoomPanel.Show();
        RefreshWaitingRoom();
    }

    /// <summary>
    /// Redraws the room, and sends everyone off to choose their saves once every player in it is ready.
    /// </summary>
    private void RefreshWaitingRoom() {
        if (!_waitingRoomActive) {
            return;
        }

        // By position, not by name: the local player is always put in first, and two players who happened to pick the
        // same name would otherwise both be read as the local one, so one of them pressing ready would count for both
        var members = new List<WaitingRoomMember>();
        for (var i = 0; i < _waitingRoomPlayers.Count; i++) {
            var name = _waitingRoomPlayers[i];
            members.Add(
                new WaitingRoomMember(name, i == 0 ? _waitingRoomLocalReady : _waitingRoomReadyNames.Contains(name))
            );
        }

        _waitingRoomPanel.SetRoom(members, _waitingRoomLocalReady, _waitingRoomHosting);

        if (_waitingRoomProceeded || members.Count < 2) {
            return;
        }

        foreach (var member in members) {
            if (!member.Ready) {
                return;
            }
        }

        _waitingRoomProceeded = true;
        if (_waitingRoomHosting) {
            // Put away rather than torn down, because backing out of the save menu has to come back here: the game
            // is still hosted and the teammate is still connected at that point
            SuspendWaitingRoom();
            HostSaveSelectionRequested?.Invoke();
        } else {
            HideWaitingRoom();
            JoinSaveSelectionRequested?.Invoke();
        }
    }

    /// <summary>
    /// The other player said they are ready, or took it back.
    /// </summary>
    /// <param name="username">The name of the player whose readiness changed.</param>
    /// <param name="ready">Whether they are ready now.</param>
    public void OnPartnerReadyChanged(string username, bool ready) {
        if (!_waitingRoomActive) {
            return;
        }

        if (ready) {
            _waitingRoomReadyNames.Add(username);
        } else {
            _waitingRoomReadyNames.Remove(username);
        }

        RefreshWaitingRoom();
    }

    /// <summary>
    /// Closes the waiting room for good, for when the host gives up, loses the connection, or their save loads.
    /// </summary>
    private void HideWaitingRoom() {
        _waitingRoomActive = false;
        _waitingRoomHosting = false;
        _waitingRoomLocalReady = false;
        _waitingRoomProceeded = false;
        _waitingRoomLobbyId = default;
        _waitingRoomPlayers.Clear();
        _waitingRoomReadyNames.Clear();
        _waitingRoomPanel.Hide();
    }

    /// <summary>
    /// Takes the waiting room off the screen while the save menu is open, keeping who is in so that coming back
    /// shows the same room rather than an empty one.
    /// </summary>
    private void SuspendWaitingRoom() {
        _waitingRoomPanel.Hide();
    }

    /// <summary>
    /// The host closed the save menu without choosing, so they go back to waiting. Their game stayed open the whole
    /// time and their teammate is still connected, so there is a room to go back to.
    /// </summary>
    public void OnHostSaveSelectionClosed() {
        if (!_waitingRoomActive) {
            return;
        }

        _steamGroup?.SetActive(false);
        _waitingRoomProceeded = false;

        // Backing out of the save menu means the host is not ready after all. Without this the room would see both
        // players still ready the moment it reappeared and send the host straight back into the save menu, over and
        // over, with no way to return here.
        if (_waitingRoomLocalReady) {
            _waitingRoomLocalReady = false;
            WaitingRoomReadyToggled?.Invoke(false);
        }

        _waitingRoomPanel.Show();
        RefreshWaitingRoom();
    }

    /// <summary>
    /// The host chose a save and the game is loading it, so there is nothing left to wait for.
    /// </summary>
    public void OnHostSaveLoaded() {
        if (_waitingRoomActive) {
            HideWaitingRoom();
        }
    }

    /// <summary>
    /// Adds a player that joined to the waiting room, if one is up.
    /// </summary>
    /// <param name="username">The name of the player that joined.</param>
    public void OnPlayerJoined(string username) {
        if (!_waitingRoomActive || _waitingRoomPlayers.Contains(username)) {
            return;
        }

        _waitingRoomPlayers.Add(username);
        RefreshWaitingRoom();
    }

    /// <summary>
    /// Takes a player that left back out of the waiting room, if one is up.
    /// </summary>
    /// <param name="username">The name of the player that left.</param>
    public void OnPlayerLeft(string username) {
        if (!_waitingRoomActive || !_waitingRoomPlayers.Remove(username)) {
            return;
        }

        // Their readiness leaves with them, or a player who readied up and then dropped would still be counted
        _waitingRoomReadyNames.Remove(username);
        RefreshWaitingRoom();
    }

    /// <summary>
    /// Shows or hides the entire multiplayer menu interface.
    /// </summary>
    /// <param name="active">True to show the menu, false to hide it.</param>
    public void SetMenuActive(bool active) {
        _backgroundPanel.SetActive(active);
        _glowingNotch.SetActive(active);

        if (active && !_isMatchmakingVersionBlocked && !_isCheckingMatchmakingVersion) {
            BeginMatchmakingVersionCheck();
        }
    }

    #endregion

    #region Button Callbacks - Matchmaking Tab

    /// <summary>
    /// Handles the Matchmaking tab's "Connect to Lobby" button press.
    /// Looks up lobby via MMS and connects to the host.
    /// </summary>
    private void OnLobbyConnectButtonPressed() {
        if (_isMatchmakingVersionBlocked) {
            return;
        }

        if (_isLobbyJoinInProgress) {
            ShowFeedback(Color.yellow, Lang.Pick("Already connecting...", "已经在连接了……"));
            return;
        }

        if (!ValidateUsername(out var username)) {
            return;
        }

        var lobbyId = _lobbyIdInput.GetInput();
        if (string.IsNullOrWhiteSpace(lobbyId)) {
            ShowFeedback(Color.red, Lang.Pick("Enter a lobby ID", "请输入房间 ID"));
            return;
        }

        // Arm the one-time retry for a hole-punch failure
        _pendingHolePunchRetryLobbyId = lobbyId;

        SetLobbyJoinInProgress();
        ShowFeedback(Color.yellow, Lang.Pick("Connecting...", "正在连接……"));
        MonoBehaviourUtil.Instance.StartCoroutine(JoinLobbyCoroutine(lobbyId, username));
    }

    /// <summary>
    /// Coroutine to join a lobby, handling both Matchmaking and Steam types.
    /// </summary>
    private IEnumerator JoinLobbyCoroutine(string lobbyId, string username) {
        ShowFeedback(Color.yellow, Lang.Pick("Joining lobby...", "正在加入房间……"));

        // Create hole-punch socket for non-Steam lobbies
        var holePunchSocket = CreateHolePunchSocket(_modSettings.MmsSettings.LocalBindIp);
        var clientPort = GetSocketPort(holePunchSocket);

        // Join lobby and get connection info
        var task = MmsClient.JoinLobbyAsync(lobbyId, clientPort);
        yield return new WaitUntil(() => task.IsCompleted);

        if (!task.IsCompletedSuccessfully) {
            CleanupHolePunchSocket(holePunchSocket);
            ResetConnectionButtons();
            Logger.Error(
                $"ConnectInterface: JoinLobbyAsync failed: {task.Exception?.GetBaseException().Message ?? "cancelled"}"
            );
            ShowFeedback(Color.red, Lang.Pick("Lobby not found, offline, or join failed", "找不到房间，可能已关闭，或者加入失败"));
            yield break;
        }

        var lobbyInfo = task.Result;
        if (lobbyInfo == null) {
            CleanupHolePunchSocket(holePunchSocket);
            ResetConnectionButtons();

            if (MmsClient.LastMatchmakingError == MatchmakingError.UpdateRequired) {
                ActivateMatchmakingVersionBlock();
                yield break;
            }

            ShowFeedback(Color.red, Lang.Pick("Lobby not found, offline, or join failed", "找不到房间，可能已关闭，或者加入失败"));
            yield break;
        }

        var connectionData = lobbyInfo.ConnectionData;
        var lobbyType = lobbyInfo.LobbyType;
        var lanConnectionData = lobbyInfo.LanConnectionData;
        var joinId = lobbyInfo.JoinId;

        // Handle connection based on lobby type
        if (lobbyType == PublicLobbyType.Steam) {
            CleanupHolePunchSocket(holePunchSocket);
            ConnectToSteamLobby(connectionData);
        } else {
            if (string.IsNullOrEmpty(joinId)) {
                CleanupHolePunchSocket(holePunchSocket);
                ResetConnectionButtons();
                ShowFeedback(Color.red, Lang.Pick("Lobby not found, offline, or join failed", "找不到房间，可能已关闭，或者加入失败"));
                yield break;
            }

            var joinTask = MmsClient.CoordinateMatchmakingJoinAsync(
                joinId,
                (data, endpoint) => { holePunchSocket.SendTo(data, endpoint); }
            );

            yield return new WaitUntil(() => joinTask.IsCompleted);

            if (!joinTask.IsCompletedSuccessfully) {
                CleanupHolePunchSocket(holePunchSocket);
                ResetConnectionButtons();
                Logger.Error(
                    $"ConnectInterface: CoordinateMatchmakingJoinAsync failed: {joinTask.Exception?.GetBaseException().Message ?? "cancelled"}"
                );
                ShowFeedback(Color.red, Lang.Pick("Lobby not found, offline, or join failed", "找不到房间，可能已关闭，或者加入失败"));
                yield break;
            }

            var joinStart = joinTask.Result;
            if (joinStart == null) {
                CleanupHolePunchSocket(holePunchSocket);
                ResetConnectionButtons();

                if (MmsClient.LastMatchmakingError == MatchmakingError.UpdateRequired) {
                    ActivateMatchmakingVersionBlock();
                    yield break;
                }

                if (MmsClient.LastJoinFailureReason == "client_path_mismatch") {
                    ShowFeedback(Color.red, ErrorSplitTunnelDetected);
                    yield break;
                }

                ShowFeedback(Color.red, Lang.Pick("Lobby not found, offline, or join failed", "找不到房间，可能已关闭，或者加入失败"));
                yield break;
            }

            ConnectToMatchmakingLobby(
                $"{joinStart.HostIp}:{joinStart.HostPort}",
                lanConnectionData,
                _modSettings.MmsSettings.PreferLanFastPath,
                username,
                holePunchSocket
            );
        }
    }

    /// <summary>
    /// Handles the Matchmaking tab's "Host Lobby" button press.
    /// Shows the lobby configuration panel.
    /// </summary>
    private void OnHostLobbyButtonPressed() {
        if (_isMatchmakingVersionBlocked) {
            return;
        }

        if (!ValidateUsername(out _)) {
            return;
        }

        // Show config panel with default name
        _matchmakingGroup.SetActive(false);
        _lobbyConfigPanel.Show();
    }

    /// <summary>
    /// Creates a matchmaking lobby with the specified configuration.
    /// Called from the config panel's Create callback.
    /// </summary>
    private void CreateMatchmakingLobbyWithConfig(LobbyVisibility visibility) {
        if (!ValidateUsername(out var username)) {
            return;
        }

        ShowFeedback(Color.yellow, Lang.Pick("Creating lobby...", "正在创建房间……"));
        Logger.Info($"Host lobby requested: ({visibility}) - HolePunch transport");

        MonoBehaviourUtil.Instance.StartCoroutine(
            CreateLobbyWithConfigCoroutine(visibility, PublicLobbyType.Matchmaking, username)
        );
    }

    /// <summary>
    /// Creates a Steam lobby with the specified configuration.
    /// Called from the Steam config panel's Create callback.
    /// </summary>
    private void CreateSteamLobbyWithConfig(LobbyVisibility visibility) {
        if (!ValidateUsername(out var username)) {
            return;
        }

        ShowFeedback(Color.yellow, Lang.Pick("Creating Steam lobby...", "正在创建 Steam 房间……"));
        Logger.Info($"Steam lobby requested: ({visibility})");

        // Convert visibility to Steam lobby type
        var steamLobbyType = visibility switch {
            LobbyVisibility.Public => ELobbyType.k_ELobbyTypePublic,
            LobbyVisibility.FriendsOnly => ELobbyType.k_ELobbyTypeFriendsOnly,
            LobbyVisibility.Private => ELobbyType.k_ELobbyTypePrivate,
            _ => ELobbyType.k_ELobbyTypeFriendsOnly
        };

        // Capture visibility for callback closure
        var isPublic = visibility == LobbyVisibility.Public;

        SteamManager.LobbyCreatedEvent += OnLobbyCreatedCallback;

        // Create native Steam lobby (uses Steam's default max = 250)
        SteamManager.CreateLobby(username, lobbyType: steamLobbyType);
        return;

        // Subscribe to lobby created event (one-time)
        void OnLobbyCreatedCallback(CSteamID steamLobbyId, string _) {
            // Unsubscribe immediately
            SteamManager.LobbyCreatedEvent -= OnLobbyCreatedCallback;

            _pendingHostedSteamLobbyId = steamLobbyId.m_SteamID.ToString();
            _pendingHostedSteamLobbyIsPublic = isPublic;

            // Hosting starts here, but the save is not chosen until the waiting room says everyone is in. Going
            // straight into the save selection was what made the old flow so strange: the game loaded before anyone
            // could join, and there was nowhere obvious to invite them from.
            ShowFeedback(Color.yellow, Lang.Pick("Steam lobby created. Opening your game...", "Steam 房间已创建。正在打开你的游戏……"));
            StartHostWithoutSavePressed?.Invoke("0.0.0.0", 0, username, TransportType.SteamRelay, null);
            ShowWaitingRoom(steamLobbyId);
        }
    }

    /// <summary>
    /// Finalizes a hosted Steam lobby after the local host has fully connected.
    /// Public lobbies are registered with MMS only after the Steam P2P server is actually live.
    /// </summary>
    private IEnumerator FinalizeHostedSteamLobbyCoroutine(string steamLobbyId, bool isPublic) {
        if (isPublic) {
            var task = MmsClient.RegisterSteamLobbyAsync(
                steamLobbyId,
                isPublic: true,
                gameVersion: Application.version
            );

            yield return new WaitUntil(() => task.IsCompleted);

            if (!task.IsCompletedSuccessfully || task.Result == null) {
                ShowFeedback(Color.yellow, Lang.Pick("Steam lobby created (browser listing failed)", "Steam 房间已创建（但没能列进房间列表）"));
            } else {
                ShowFeedback(Color.green, Lang.Pick("Steam lobby created!", "Steam 房间已创建！"));
            }
        } else {
            ShowFeedback(Color.green, Lang.Pick("Steam lobby created!", "Steam 房间已创建！"));
        }

        SteamManager.SetLobbyReady(true);
    }


    /// <summary>
    /// Coroutine for async lobby creation with config.
    /// </summary>
    private IEnumerator CreateLobbyWithConfigCoroutine(
        LobbyVisibility visibility,
        PublicLobbyType lobbyType,
        string username
    ) {
        var isPublic = visibility == LobbyVisibility.Public;

        // Create socket first to get actual port
        var holePunchSocket = CreateHolePunchSocket(_modSettings.MmsSettings.LocalBindIp);
        var actualPort = GetSocketPort(holePunchSocket);

        var task = MmsClient.CreateLobbyAsync(
            hostPort: actualPort,
            isPublic: isPublic,
            gameVersion: Application.version,
            lobbyType: lobbyType,
            hostIpOverride: _modSettings.MmsSettings.HostIpOverride,
            hostLanIpOverride: _modSettings.MmsSettings.HostLanIpOverride
        );

        yield return new WaitUntil(() => task.IsCompleted);

        if (!task.IsCompletedSuccessfully) {
            CleanupHolePunchSocket(holePunchSocket);
            Logger.Error(
                $"ConnectInterface: CreateLobbyAsync failed: {task.Exception?.GetBaseException().Message ?? "cancelled"}"
            );
            ShowFeedback(Color.red, Lang.Pick("Failed to create lobby. Is MMS running?", "创建房间失败。MMS 在运行吗？"));
            yield break;
        }

        var (lobbyId, lobbyName, _) = task.Result;
        if (lobbyId == null || lobbyName == null) {
            if (MmsClient.LastMatchmakingError == MatchmakingError.UpdateRequired) {
                CleanupHolePunchSocket(holePunchSocket);
                ActivateMatchmakingVersionBlock();
                yield break;
            }

            ShowFeedback(Color.red, Lang.Pick("Failed to create lobby. Is MMS running?", "创建房间失败。MMS 在运行吗？"));
            CleanupHolePunchSocket(holePunchSocket);
            yield break;
        }

        // For private lobbies, show invite code in ChatBox so it's easily shareable
        if (visibility == LobbyVisibility.Private) {
            UiManager.InternalChatBox.AddMessage(
                $"<color=yellow>[Private Lobby]</color> Invite code: <color=lime>{lobbyId}</color>"
            );
            ShowFeedback(Color.green, Lang.Pick("Private lobby created!", "私人房间已创建！"));
        } else {
            UiManager.InternalChatBox.AddMessage(
                $"<color=yellow>[Public Lobby]</color> Lobby name: <color=lime>{lobbyName}</color>, invite code: <color=lime>{lobbyId}</color>"
            );
            ShowFeedback(Color.green, Lang.Pick($"Lobby: {lobbyId}", $"房间：{lobbyId}"));
        }

        // Pass the pre-bound socket to the transport layer before hosting
        HolePunchEncryptedTransportServer.PreBoundSocket = holePunchSocket;

        StartHostButtonPressed?.Invoke("0.0.0.0", actualPort, username, TransportType.HolePunch, null);
    }

    /// <summary>
    /// Handles the Matchmaking tab's "Browse Lobbies" button press.
    /// Fetches and displays public lobbies from the MMS.
    /// </summary>
    private void OnBrowseMatchmakingLobbiesPressed() {
        if (_isMatchmakingVersionBlocked) {
            return;
        }

        // Hide matchmaking content and show lobby browser
        _matchmakingGroup.SetActive(false);
        _lobbyBrowserPanel.Show();

        ShowFeedback(Color.yellow, Lang.Pick("Fetching lobbies...", "正在获取房间列表……"));
        MonoBehaviourUtil.Instance.StartCoroutine(FetchLobbiesCoroutine());
    }

    /// <summary>
    /// Coroutine for async lobby fetching (Matchmaking tab).
    /// </summary>
    private IEnumerator FetchLobbiesCoroutine() {
        var task = MmsClient.GetPublicLobbiesAsync(PublicLobbyType.Matchmaking);

        // Wait for async operation without blocking main thread
        yield return new WaitUntil(() => task.IsCompleted);

        var lobbies = task.Result;
        if (lobbies == null) {
            if (MmsClient.LastMatchmakingError == MatchmakingError.UpdateRequired) {
                _lobbyBrowserPanel.Hide();
                ActivateMatchmakingVersionBlock();
                yield break;
            }

            ShowFeedback(Color.red, Lang.Pick("Failed to fetch lobbies. Is MMS running?", "获取房间列表失败。MMS 在运行吗？"));
            yield break;
        }

        // Update panel with lobbies and show
        _lobbyBrowserPanel.SetLobbies(lobbies);
        _lobbyBrowserPanel.Show();

        if (lobbies.Count == 0) {
            ShowFeedback(Color.yellow, Lang.Pick("No public lobbies found.", "没有找到公开房间。"));
        } else {
            var word = lobbies.Count == 1 ? "Lobby" : "Lobbies";
            ShowFeedback(Color.green, Lang.Pick($"Found {lobbies.Count} {word}", $"找到 {lobbies.Count} 个房间"));
        }

        Logger.Info($"ConnectInterface: Displaying {lobbies.Count} public lobbies");
    }

    #endregion

    #region Button Callbacks - Steam Tab

    /// <summary>
    /// Handles the Steam tab's "Create Lobby" button press.
    /// Shows the Steam lobby configuration panel.
    /// </summary>
    private void OnCreateLobbyButtonPressed() {
        if (!SteamManager.IsInitialized) {
            ShowFeedback(Color.red, Lang.Pick("Steam is not available. Please ensure Steam is running.", "Steam 不可用。请确认 Steam 正在运行。"));
            Logger.Warn("Cannot create Steam lobby: Steam is not initialized");
            return;
        }

        if (!ValidateUsername(out _)) {
            return;
        }

        if (_steamLobbyConfigPanel == null || _steamGroup == null) return;

        // Show config panel with default name
        _steamGroup.SetActive(false);
        _steamLobbyConfigPanel.Show();
    }

    /// <summary>
    /// Handles the Steam tab's "Browse Public Lobbies" button press.
    /// Requests a list of available public Steam lobbies from MMS.
    /// </summary>
    private void OnBrowseLobbyButtonPressed() {
        if (!SteamManager.IsInitialized) {
            ShowFeedback(Color.red, Lang.Pick("Steam is not available.", "Steam 不可用。"));
            return;
        }

        if (_steamLobbyBrowserPanel == null || _steamGroup == null) return;

        // Hide Steam content and show lobby browser
        _steamGroup.SetActive(false);
        _steamLobbyBrowserPanel.Show();

        ShowFeedback(Color.yellow, Lang.Pick("Fetching lobbies...", "正在获取房间列表……"));
        MonoBehaviourUtil.Instance.StartCoroutine(FetchSteamLobbiesCoroutine());
    }

    /// <summary>
    /// Coroutine for async Steam lobby fetching from MMS.
    /// </summary>
    private IEnumerator FetchSteamLobbiesCoroutine() {
        var task = MmsClient.GetPublicLobbiesAsync(PublicLobbyType.Steam); // Filter by steam type

        yield return new WaitUntil(() => task.IsCompleted);

        var lobbies = task.Result;
        if (lobbies == null) {
            ShowFeedback(Color.red, Lang.Pick("Failed to fetch lobbies. Is MMS running?", "获取房间列表失败。MMS 在运行吗？"));
            yield break;
        }

        _steamLobbyBrowserPanel?.SetLobbies(lobbies);

        ShowFeedback(
            lobbies.Count == 0 ? Color.yellow : Color.green,
            lobbies.Count == 0
                ? Lang.Pick("No public lobbies found.", "没有找到公开房间。")
                : Lang.Pick(
                    $"Found {lobbies.Count} {(lobbies.Count == 1 ? "Lobby" : "Lobbies")}",
                    $"找到 {lobbies.Count} 个房间"
                )
        );

        Logger.Info($"ConnectInterface: Displaying {lobbies.Count} public lobbies (Steam tab)");
    }

    /// <summary>
    /// Joins a Steam lobby from the browser using the Steam lobby ID.
    /// Uses Steam's native join flow, not MMS invite codes.
    /// </summary>
    /// <param name="steamLobbyIdString">The Steam lobby ID as a string.</param>
    private void JoinSteamLobbyFromBrowser(string steamLobbyIdString) {
        if (!SteamManager.IsInitialized) {
            ShowFeedback(Color.red, Lang.Pick("Steam is not available.", "Steam 不可用。"));
            return;
        }

        if (!ulong.TryParse(steamLobbyIdString, out var steamLobbyId)) {
            ShowFeedback(Color.red, Lang.Pick("Invalid Steam lobby ID.", "无效的 Steam 房间 ID。"));
            return;
        }

        ShowFeedback(Color.yellow, Lang.Pick("Joining Steam lobby...", "正在加入 Steam 房间……"));
        SteamManager.JoinLobby(new CSteamID(steamLobbyId));
    }

    /// <summary>
    /// Handles the Steam tab's "Join Friend" button press.
    /// Opens the Steam Friends overlay to allow joining via friend invite.
    /// </summary>
    private void OnJoinFriendButtonPressed() {
        if (!SteamManager.IsInitialized) {
            ShowFeedback(Color.red, Lang.Pick("Steam is not available.", "Steam 不可用。"));
            return;
        }

        SteamFriends.ActivateGameOverlay("Friends");
        ShowFeedback(Color.yellow, Lang.Pick(
            "Opened Steam Friends. Right-click friend to Join Game.",
            "已打开 Steam 好友列表。右键点击好友，选择加入游戏。"
        ));
    }

    #endregion

    #region Button Callbacks - Direct IP Tab

    /// <summary>
    /// Handles the Direct IP tab's "Connect" button press.
    /// Validates inputs and initiates a direct IP connection to a server.
    /// </summary>
    private void OnDirectConnectButtonPressed() {
        var address = _addressInput.GetInput();
        if (string.IsNullOrEmpty(address)) {
            ShowFeedback(Color.red, ErrorEnterAddress);
            return;
        }

        if (!TryParsePort(_portInput.GetInput(), out var port)) {
            ShowFeedback(Color.red, ErrorEnterValidPort);
            return;
        }

        if (!ValidateUsername(out var username)) {
            return;
        }

        SaveConnectionSettings(address, port, username);

        _directConnectButton.SetText(ConnectingText);
        _directConnectButton.SetInteractable(false);

        Logger.Debug($"Connecting to {address}:{port} as {username}");
        ConnectButtonPressed?.Invoke(address, port, username, TransportType.Udp, null);
    }

    /// <summary>
    /// Handles the Direct IP tab's "Host" button press.
    /// Validates inputs and starts hosting a server on the specified port.
    /// </summary>
    private void OnStartButtonPressed() {
        if (!TryParsePort(_portInput.GetInput(), out var port)) {
            ShowFeedback(Color.red, ErrorEnterValidPortHost);
            return;
        }

        if (!ValidateUsername(out var username)) {
            return;
        }

        SaveConnectionSettings(null, port, username);

        StartHostButtonPressed?.Invoke("", port, username, TransportType.Udp, null);
    }

    #endregion

    #region Steam Event Callbacks

    /// <summary>
    /// Called when the list of available Steam lobbies is received.
    /// Auto-joins the first lobby if any are found.
    /// </summary>
    /// <param name="lobbyIds">Array of Steam lobby IDs found in the search.</param>
    private void OnLobbyListReceived(CSteamID[] lobbyIds) {
        if (lobbyIds.Length == 0) {
            ShowFeedback(Color.yellow, Lang.Pick("No lobbies found.", "没有找到房间。"));
            return;
        }

        Logger.Info($"Found {lobbyIds.Length} lobbies. Auto-joining first one.");
        ShowFeedback(Color.yellow, Lang.Pick(
            $"Found {lobbyIds.Length} lobbies. Joining first...",
            $"找到 {lobbyIds.Length} 个房间。正在加入第一个……"
        ));

        SteamManager.JoinLobby(lobbyIds[0]);
    }

    /// <summary>
    /// Called when successfully joined a Steam lobby.
    /// Initiates connection to the lobby host via Steam P2P.
    /// </summary>
    /// <param name="lobbyId">The Steam ID of the joined lobby.</param>
    private void OnLobbyJoined(CSteamID lobbyId) {
        Logger.Info($"Joined lobby: {lobbyId}");

        // Both games have to be the same build. Picking a lobby out of the list already filters on this, but an
        // invite and joining through a friends list never go near that filter, which is how two people actually play
        // together. Until now a mismatch was silent: nothing refused it, the two saves simply behaved differently and
        // it read as a fresh bug. Leaving the lobby and putting the buttons back matters as much as the message - an
        // early return on its own would strand the connect button on "Connecting..." with no way back.
        var hostVersion = SteamManager.GetLobbyModVersion(lobbyId);
        if (hostVersion.Length > 0 && hostVersion != SteamManager.LocalModVersion) {
            Logger.Warn($"Lobby runs '{hostVersion}', this game runs '{SteamManager.LocalModVersion}'");

            SteamManager.LeaveLobby();
            ResetConnectionButtons();
            ShowFeedback(Color.red, Lang.Pick(
                "You are on different versions of the mod. Both players need the same one.",
                "你们两个装的模组版本不一样。两个人必须用同一个版本。"
            ));

            return;
        }

        ShowFeedback(Color.green, Lang.Pick("Joined lobby! Connecting to host...", "已加入房间！正在连接房主……"));

        var hostId = SteamManager.GetLobbyOwner(lobbyId);

        if (!ValidateUsername(out var username)) {
            return;
        }

        // Connect using Steam ID as address, over the relay network
        ConnectButtonPressed?.Invoke(hostId.ToString(), 0, username, TransportType.SteamRelay, null);
    }

    /// <summary>
    /// Handles connection to a Steam lobby.
    /// </summary>
    private void ConnectToSteamLobby(string connectionData) {
        // Both of these ends the join, so the buttons have to come back with them. Without that the connect button
        // stays on "Connecting..." and refuses every further press, and the only way out is restarting the game.
        if (!SteamManager.IsInitialized) {
            ResetConnectionButtons();
            ShowFeedback(Color.red, Lang.Pick("Steam is not initialized", "Steam 还没有初始化"));
            return;
        }

        if (!ulong.TryParse(connectionData, out var steamLobbyId)) {
            ResetConnectionButtons();
            ShowFeedback(Color.red, Lang.Pick("Invalid Steam lobby ID.", "无效的 Steam 房间 ID。"));
            Logger.Warn($"ConnectInterface: MMS returned invalid Steam lobby ID '{connectionData}'");
            return;
        }

        ShowFeedback(Color.yellow, Lang.Pick("Joining Steam lobby...", "正在加入 Steam 房间……"));
        SteamManager.JoinLobby(new CSteamID(steamLobbyId));
    }

    #endregion

    #region Connection Event Callbacks

    /// <summary>
    /// Called when the client successfully establishes a connection to the server.
    /// Resets UI state and displays success message.
    /// </summary>
    public void OnSuccessfulConnect() {
        if (SteamManager.IsHostingLobby && _pendingHostedSteamLobbyId != null) {
            var steamLobbyId = _pendingHostedSteamLobbyId;
            var isPublic = _pendingHostedSteamLobbyIsPublic;

            _pendingHostedSteamLobbyId = null;
            _pendingHostedSteamLobbyIsPublic = false;

            MonoBehaviourUtil.Instance.StartCoroutine(FinalizeHostedSteamLobbyCoroutine(steamLobbyId, isPublic));
        }

        ShowFeedback(Color.green, MsgConnected);
        ResetConnectionButtons();
    }

    /// <summary>
    /// Called when the client disconnects from the server.
    /// Resets the connection UI to allow reconnection.
    /// </summary>
    public void OnClientDisconnect() {
        // The waiting room belongs to a game that is open; without a connection there is nothing left to wait in
        if (_waitingRoomActive) {
            HideWaitingRoom();
            _steamGroup?.SetActive(_activeTab == Tab.Steam);
        }

        ResetConnectionButtons();
    }

    /// <summary>
    /// Called when a connection attempt fails.
    /// Displays an appropriate error message based on the failure reason.
    /// </summary>
    /// <param name="result">Details about why the connection failed.</param>
    /// <param name="fallbackAddress">Optional fallback address (IP:Port) to attempt on failure.</param>
    public void OnFailedConnect(ConnectionFailedResult result, string? fallbackAddress = null) {
        // If we have a fallback connection to try, we do so now
        if (!string.IsNullOrEmpty(fallbackAddress) &&
            TryParseConnectionData(fallbackAddress, out var address, out var port)) {
            ShowFeedback(Color.yellow, Lang.Pick("LAN failed, retrying Public...", "局域网连接失败，正在改用公开方式重试……"));
            Logger.Info($"ConnectInterface: LAN connection failed, retrying Public at {address}:{port}");

            // Trigger the fallback connection using the current username input
            var username = _usernameInput.GetInput();
            ConnectButtonPressed?.Invoke(address, port, username, TransportType.HolePunch, null);
            return;
        }

        // If this was a timeout and we have a pending retry, consume it and re-run the full connect flow.
        if (_pendingHolePunchRetryLobbyId != null && result.Reason == ConnectionFailedReason.TimedOut) {
            var lobbyIdToRetry = _pendingHolePunchRetryLobbyId;
            _pendingHolePunchRetryLobbyId = null;

            if (ValidateUsername(out var username)) {
                Logger.Info(
                    $"ConnectInterface: Connection timed out. Retrying full join flow for lobby {lobbyIdToRetry} once."
                );
                SetLobbyJoinInProgress();
                ShowFeedback(Color.yellow, Lang.Pick("Connection timed out. Retrying...", "连接超时。正在重试……"));
                MonoBehaviourUtil.Instance.StartCoroutine(JoinLobbyCoroutine(lobbyIdToRetry, username));
                return;
            }
        }

        ResetConnectionButtons();
        _pendingHolePunchRetryLobbyId = null;

        var message = GetFailureMessage(result);
        ShowFeedback(Color.red, message);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Validates the username input field.
    /// </summary>
    /// <param name="username">Output parameter containing the validated username.</param>
    /// <returns>True if username is valid, false otherwise.</returns>
    private bool ValidateUsername(out string username) {
        if (ConnectInterfaceHelpers.ValidateUsername(
                _usernameInput,
                _feedbackText,
                out username,
                _feedbackHideCoroutine,
                out var newCoroutine
            )) {
            // Remembered here rather than in each caller. The name was only ever saved alongside an address and a
            // port, which is written by the two paths that take one - so a player who opens a lobby, joins one, or
            // picks from the lobby list was asked for their name again every single time, and it was never their
            // forgetfulness. Every one of those paths comes through here. Only written when it actually changed, so
            // that pressing a button does not rewrite the settings file each time.
            if (_modSettings.Username != username) {
                _modSettings.Username = username;
                _modSettings.Save();
            }

            return true;
        }

        _feedbackHideCoroutine = newCoroutine;
        return false;
    }

    /// <summary>
    /// Attempts to parse a port number string into a valid integer port.
    /// </summary>
    /// <param name="portString">The string to parse.</param>
    /// <param name="port">Output parameter containing the parsed port number.</param>
    /// <returns>True if parsing succeeded and port is valid (non-zero), false otherwise.</returns>
    private static bool TryParsePort(string portString, out int port) {
        return int.TryParse(portString, out port) && port != 0;
    }

    /// <summary>
    /// Saves connection settings (address if non-null, port, username) to persistent storage.
    /// </summary>
    private void SaveConnectionSettings(string? address, int port, string username) {
        if (address != null) {
            _modSettings.ConnectAddress = address;
        }

        _modSettings.ConnectPort = port;
        _modSettings.Username = username;
        _modSettings.Save();
    }

    /// <summary>
    /// Displays a feedback message to the user with the specified color.
    /// Automatically hides the message after a delay.
    /// </summary>
    /// <param name="color">The color of the feedback text.</param>
    /// <param name="message">The message to display.</param>
    private void ShowFeedback(Color color, string message) {
        _isMatchmakingFeedbackActive = false;
        _feedbackHideCoroutine = ConnectInterfaceHelpers.SetFeedbackText(
            _feedbackText,
            color,
            message,
            _feedbackHideCoroutine
        );
    }

    /// <summary>
    /// Activates the persistent matchmaking version block UI.
    /// </summary>
    private void ActivateMatchmakingVersionBlock() {
        _isCheckingMatchmakingVersion = false;
        _isMatchmakingReady = false;
        _isMatchmakingVersionBlocked = true;
        _lobbyBrowserPanel.Hide();
        _lobbyConfigPanel.Hide();
        SwitchTab(Tab.Matchmaking);
    }

    /// <summary>
    /// Starts an immediate matchmaking compatibility probe so the UI can block before showing actions.
    /// </summary>
    private void BeginMatchmakingVersionCheck() {
        if (_isCheckingMatchmakingVersion || _isMatchmakingVersionBlocked) {
            return;
        }

        _isMatchmakingReady = false;
        _isCheckingMatchmakingVersion = true;
        SwitchTab(_activeTab);
        MonoBehaviourUtil.Instance.StartCoroutine(CheckMatchmakingVersionCoroutine());
    }

    /// <summary>
    /// Contacts MMS first and only enables matchmaking when the server is both reachable and compatible.
    /// </summary>
    private IEnumerator CheckMatchmakingVersionCoroutine() {
        var task = MmsClient.ProbeMatchmakingCompatibilityAsync();
        yield return new WaitUntil(() => task.IsCompleted);

        if (MmsClient.LastMatchmakingError == MatchmakingError.UpdateRequired) {
            ActivateMatchmakingVersionBlock();
            yield break;
        }

        _isCheckingMatchmakingVersion = false;
        _isMatchmakingReady = task.Result == true;
        SwitchTab(_activeTab);
    }

    /// <summary>
    /// Keeps matchmaking status messages aligned with the standard bottom feedback area.
    /// </summary>
    private void RefreshMatchmakingStatusFeedback() {
        if (_activeTab != Tab.Matchmaking) {
            if (_isMatchmakingFeedbackActive) {
                _feedbackText.SetActive(false);
                _isMatchmakingFeedbackActive = false;
            }

            return;
        }

        if (_isCheckingMatchmakingVersion) {
            SetMatchmakingStatusFeedback(MatchmakingCheckingText, Color.yellow);
            return;
        }

        if (_isMatchmakingVersionBlocked) {
            SetMatchmakingStatusFeedback(MatchmakingUpdateRequiredText, Color.red);
            return;
        }

        if (!_isMatchmakingReady) {
            SetMatchmakingStatusFeedback(MatchmakingUnavailableText, Color.red);
            return;
        }

        if (_isMatchmakingFeedbackActive) {
            _feedbackText.SetActive(false);
            _isMatchmakingFeedbackActive = false;
        }
    }

    /// <summary>
    /// Displays matchmaking-owned status feedback without clobbering feedback from other tabs.
    /// </summary>
    private void SetMatchmakingStatusFeedback(string message, Color color) {
        if (_feedbackHideCoroutine != null) {
            MonoBehaviourUtil.Instance.StopCoroutine(_feedbackHideCoroutine);
            _feedbackHideCoroutine = null;
        }

        _isMatchmakingFeedbackActive = true;
        _feedbackText.SetText(message);
        _feedbackText.SetColor(color);
        _feedbackText.SetActive(true);
    }

    /// <summary>
    /// Resets the connection buttons to their default state after a connection attempt.
    /// </summary>
    private void ResetConnectionButtons() {
        _isLobbyJoinInProgress = false;
        ConnectInterfaceHelpers.ResetConnectButtons(_directConnectButton, _lobbyConnectButton);
    }

    /// <summary>
    /// Marks the matchmaking lobby join path as busy and disables duplicate join presses.
    /// </summary>
    private void SetLobbyJoinInProgress() {
        _isLobbyJoinInProgress = true;
        _lobbyConnectButton.SetText(ConnectingText);
        _lobbyConnectButton.SetInteractable(false);
    }

    /// <summary>
    /// Converts a connection failure result into a user-friendly error message.
    /// </summary>
    /// <param name="result">The connection failure details.</param>
    /// <returns>A formatted error message string.</returns>
    private static string GetFailureMessage(ConnectionFailedResult result) {
        return result.Reason switch {
            ConnectionFailedReason.InvalidAddons => ErrorInvalidAddons,
            ConnectionFailedReason.SocketException or
                ConnectionFailedReason.IOException => ErrorInternal,
            ConnectionFailedReason.TimedOut => ErrorTimeout,
            ConnectionFailedReason.Other =>
                $"Failed to connect:\n{((ConnectionFailedMessageResult) result).Message}",
            _ => ErrorUnknown
        };
    }

    /// <summary>
    /// Creates and configures a UDP socket for hole-punching.
    /// </summary>
    private static Socket CreateHolePunchSocket(string? bindIpAddress) {
        var socket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp
        );

        var bindAddress = NetworkingUtil.ResolveBindAddress(bindIpAddress);

        try {
            socket.Bind(new IPEndPoint(bindAddress, 26960));
        } catch (SocketException) {
            socket.Bind(new IPEndPoint(bindAddress, 0));
        }

        return socket;
    }

    /// <summary>
    /// Gets the local port from a bound socket.
    /// </summary>
    private static int GetSocketPort(Socket socket) {
        return ((IPEndPoint) socket.LocalEndPoint!).Port;
    }

    /// <summary>
    /// Handles connection to a matchmaking lobby with LAN/public fallback.
    /// </summary>
    private void ConnectToMatchmakingLobby(
        string connectionData,
        string? lanConnectionData,
        bool preferLanFastPath,
        string username,
        Socket? holePunchSocket
    ) {
        var connectionInfo = DetermineConnectionInfo(connectionData, lanConnectionData, preferLanFastPath);

        if (connectionInfo == null) {
            ShowFeedback(Color.red, Lang.Pick("Invalid connection data", "连接数据无效"));
            CleanupHolePunchSocket(holePunchSocket);
            return;
        }

        ShowFeedback(Color.green, connectionInfo.Value.FeedbackMessage);

        // Pass the pre-bound socket to the transport layer before connecting
        HolePunchEncryptedTransport.HolePunchSocket = holePunchSocket;

        ConnectButtonPressed?.Invoke(
            connectionInfo.Value.PrimaryIp,
            connectionInfo.Value.PrimaryPort,
            username,
            TransportType.HolePunch,
            connectionInfo.Value.FallbackAddress
        );
    }

    /// <summary>
    /// Determines the optimal connection strategy (LAN first, then public).
    /// </summary>
    private static ConnectionInfo? DetermineConnectionInfo(
        string publicConnectionData,
        string? lanConnectionData,
        bool preferLanFastPath
    ) {
        // Public connection is required in all cases
        if (!TryParseConnectionData(publicConnectionData, out var publicIp, out var publicPort))
            return null;

        // Prefer LAN if available, using public as the fallback relay
        if (!preferLanFastPath || string.IsNullOrEmpty(lanConnectionData) ||
            !TryParseConnectionData(lanConnectionData, out var lanIp, out var lanPort)) {
            return new ConnectionInfo(publicIp, publicPort, null, $"Connecting to {publicIp}:{publicPort}...");
        }

        // If the LAN endpoint resolves to one of this machine's own IPv4 addresses,
        // the client and host are running on the same device. In that case, prefer
        // loopback instead of connecting back through the LAN adapter, since some
        // network setups handle that path inconsistently.
        // The public endpoint is still kept as the fallback if the local attempt fails.
        return NetworkingUtil.IsLocalInterfaceIpv4(lanIp)
            ? new ConnectionInfo("127.0.0.1", lanPort, $"{publicIp}:{publicPort}", $"Connecting locally on 127.0.0.1:{lanPort}...")
            : new ConnectionInfo(lanIp, lanPort, $"{publicIp}:{publicPort}", $"Connecting to LAN {lanIp}:{lanPort}...");
    }

    /// <summary>
    /// Parses connection data in format "IP:Port".
    /// </summary>
    private static bool TryParseConnectionData(string connectionData, out string ip, out int port) {
        ip = string.Empty;
        port = 0;

        var parts = connectionData.Split(':');
        if (parts.Length != 2)
            return false;

        ip = parts[0];
        return int.TryParse(parts[1], out port);
    }

    /// <summary>
    /// Safely disposes the hole-punch socket.
    /// </summary>
    private static void CleanupHolePunchSocket(Socket? socket) {
        if (socket == null) {
            return;
        }

        socket.Dispose();
        HolePunchEncryptedTransport.HolePunchSocket = null;
    }

    #endregion
}
