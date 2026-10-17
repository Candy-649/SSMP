using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using MonoMod.RuntimeDetour;
using Steamworks;
using SSMP.Logging;
using SSMP.Networking.Transport.SteamRelay;
using SSMP.Util;

namespace SSMP.Game;

/// <summary>
/// Manages Steam API initialization and availability checks.
/// Handles graceful fallback when Steam is not available (multi-platform support).
/// Enhanced performance through caching, reduced allocations, and lock optimization.
/// </summary>
public static class SteamManager {
    /// <summary>
    /// The default maximum number of players allowed in a lobby (250 = Steam's max = unlimited).
    /// </summary>
    private const int DefaultMaxPlayers = 250;

    /// <summary>
    /// The default lobby visibility type.
    /// </summary>
    private const ELobbyType DefaultLobbyType = ELobbyType.k_ELobbyTypeFriendsOnly;

    /// <summary>
    /// Whether Steam API has been successfully initialized.
    /// </summary>
    public static bool IsInitialized { get; private set; }

    /// <summary>
    /// The current Steam lobby ID if hosting.
    /// </summary>
    private static CSteamID CurrentLobbyId { get; set; }

    /// <summary>
    /// Whether we are currently hosting a Steam lobby.
    /// </summary>
    public static bool IsHostingLobby { get; private set; }

    /// <summary>
    /// Whether we are currently in a Steam lobby (hosting or client).
    /// </summary>
    public static bool IsInLobby => CurrentLobbyId != NilLobbyId;

    /// <summary>
    /// Event fired when a Steam lobby is successfully created.
    /// Parameters: Lobby ID, Lobby owner's username
    /// </summary>
    public static event Action<CSteamID, string>? LobbyCreatedEvent;

    /// <summary>
    /// Event fired when a list of lobbies is received.
    /// Parameters: Array of Lobby IDs
    /// </summary>
    public static event Action<CSteamID[]>? LobbyListReceivedEvent;

    /// <summary>
    /// Event fired when a lobby is successfully joined.
    /// Parameters: Lobby ID
    /// </summary>
    public static event Action<CSteamID>? LobbyJoinedEvent;

    /// <summary>
    /// Event fired when joining a lobby failed, with what Steam answered, or null when Steam did not answer at all.
    /// Nothing said so before, so a player who accepted an invite to a lobby that was gone saw nothing happen.
    /// </summary>
    public static event Action<EChatRoomEnterResponse?>? LobbyJoinFailedEvent;

    /// <summary>
    /// Stored username for lobby creation callback.
    /// </summary>
    private static string? _pendingLobbyUsername;

    /// <summary>
    /// Stored lobby type for lobby creation callback.
    /// Used to determine if Rich Presence should be set (not set for private lobbies).
    /// </summary>
    private static ELobbyType _pendingLobbyType;

    /// <summary>
    /// The visibility type of the currently active lobby.
    /// </summary>
    private static ELobbyType _currentLobbyType = DefaultLobbyType;

    /// <summary>
    /// Callback timer interval in milliseconds (~60Hz).
    /// </summary>
    private const int CallbackIntervalMs = 17;

    /// <summary>
    /// Cached CSteamID.Nil value to avoid repeated struct creation.
    /// </summary>
    private static readonly CSteamID NilLobbyId = CSteamID.Nil;

    /// <summary>
    /// Cached string keys for lobby metadata to avoid allocations.
    /// </summary>
    private const string LobbyKeyName = "name";
    private const string LobbyKeyVersion = "version";
    /// <summary>
    /// Cached mod version string from assembly metadata.
    /// </summary>
    /// <summary>
    /// The build this game is running, as told to the other player. The informational version carries the commit it
    /// was built from, so two builds of the same mod are told apart; the plain assembly version has stood still for
    /// every build handed out so far and would report two different games as the same one.
    /// </summary>
    private static readonly string ModVersion =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0.0";

    /// <summary>
    /// Reusable callback instances to avoid GC allocations.
    /// </summary>
    private static CallResult<LobbyCreated_t>? _lobbyCreatedCallback;
    private static CallResult<LobbyMatchList_t>? _lobbyMatchListCallback;
    private static CallResult<LobbyEnter_t>? _lobbyEnterCallback;

    /// <summary>
    /// What stands in for the game in taking Steam's messages while the game takes none (see
    /// <see cref="OnCallbackTimerElapsed"/>).
    /// </summary>
    private static Timer? _callbackTimer;

    /// <summary>
    /// Held by whatever takes Steam's messages, the game or the stand-in, so that never both take them at once.
    ///
    /// Steam's messages are taken one at a time: each is fetched, handled and let go, and Steam's own header says the
    /// next may only be fetched once the last is let go (SteamAPI_ManualDispatch_GetNextCallback). Steamworks.NET
    /// fetches them into one buffer that every caller shares, without a lock (CallbackDispatcher.RunFrame). The game
    /// takes them every frame on the main thread (SteamOnlineSubsystem.Update), and the mod took them as well, every
    /// 17 ms on a thread of its own, so the two could fetch at once (review of 7812c04).
    /// </summary>
    private static readonly object PumpGate = new();

    /// <summary>
    /// When the game last took Steam's messages, as a <see cref="Stopwatch"/> timestamp. Set as well once the hook on
    /// its taking is made, so that the stand-in waits for its first. 0 without the hook: the stand-in then takes them
    /// at every look, alongside the game, as it used to.
    /// </summary>
    private static long _gamePumpedAt;

    /// <summary>
    /// When the stand-in began taking Steam's messages for the game, as a <see cref="Stopwatch"/> timestamp, or 0
    /// while it does not.
    /// </summary>
    private static long _standingInSince;

    /// <summary>
    /// Where the game's window was at its last frame when the stand-in began, in words for the log.
    /// </summary>
    private static volatile string _standInWindow = "";

    /// <summary>
    /// How long the game may take none of Steam's messages before the stand-in takes them: longer than any ordinary
    /// frame, so that it only does while the game is stuck on one, or stopped.
    /// </summary>
    private static readonly long StandInAfter = Stopwatch.Frequency;

    /// <summary>
    /// The hook on the game's taking of Steam's messages (see <see cref="TakeMessagesForGame"/>).
    /// </summary>
    private static Hook? _gamePumpHook;

    /// <summary>
    /// Initializes the Steam API if available.
    /// Safe to call multiple times - subsequent calls are no-ops.
    /// </summary>
    /// <returns>True if Steam was initialized successfully, false otherwise.</returns>
    public static bool Initialize() {
        if (IsInitialized) return true;

        try {
            // Check if Steam client is running
            if (!Packsize.Test()) {
                Logger.Warn("Steam: Packsize test failed, Steam may not be available");
                return false;
            }

            // Initialize Steam API
            if (!SteamAPI.Init()) {
                Logger.Warn("Steam: SteamAPI.Init() failed - Steam client may not be running or game may not be launched through Steam");
                return false;
            }

            IsInitialized = true;
            Logger.Info($"Steam: Initialized successfully (SteamID: {SteamUser.GetSteamID()})");

            // Starts working out how to reach Steam's relay network now rather than when someone first tries to
            // connect over it, which otherwise pays for it in the first seconds of a game. Costs nothing if no one
            // uses the relay transport.
            SteamNetworkingUtils.InitRelayNetworkAccess();

            // A session that a player asks for is answered from here on: taken by the game hosted or joined here that
            // is with them, closed if there is none
            SteamRelayMessaging.AnswerSessionRequests();

            // Register callbacks for joining via overlay/friends
            Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
            Callback<GameRichPresenceJoinRequested_t>.Create(OnGameRichPresenceJoinRequested);

            // Pre-allocate callback instances to reuse
            _lobbyCreatedCallback = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyMatchListCallback = CallResult<LobbyMatchList_t>.Create(OnLobbyMatchList);
            _lobbyEnterCallback = CallResult<LobbyEnter_t>.Create(OnLobbyEnter);

            // The game takes Steam's messages every frame, and the mod only while the game takes none. Not being able
            // to hook the game's taking, as after an update of the game that changed it, is no reason to go without
            // Steam: the mod then takes them alongside the game, as it used to. Caught here, as a type that the hook
            // names and that is gone throws once the method naming it is compiled, before any of it runs (review of
            // 33ef6a6)
            try {
                HookGamePump();
            } catch (Exception e) {
                Logger.Error(
                    $"Steam: could not hook where the game takes Steam's messages, so the mod takes them as well:\n{e}"
                );
            }

            StartCallbackTimer();

            return true;
        } catch (Exception e) {
            Logger.Error($"Steam: Exception during initialization: {e}");
            return false;
        }
    }

    /// <summary>
    /// Creates a Steam lobby for multiplayer.
    /// </summary>
    /// <param name="username">Host's username to set as lobby name</param>
    /// <param name="maxPlayers">Maximum number of players (default 250 = unlimited)</param>
    /// <param name="lobbyType">Type of lobby to create (default friends-only)</param>
    public static void CreateLobby(
        string username, 
        int maxPlayers = DefaultMaxPlayers, 
        ELobbyType lobbyType = DefaultLobbyType
    ) {
        if (!IsInitialized) {
            Logger.Warn("Cannot create Steam lobby: Steam is not initialized");
            return;
        }

        if (IsHostingLobby) {
            Logger.Info("Already hosting a Steam lobby, leaving it first");
            LeaveLobby();
        }

        // Use Interlocked for atomic write (faster than lock for simple assignments)
        Volatile.Write(ref _pendingLobbyUsername, username);
        _pendingLobbyType = lobbyType;
        
        Logger.Info($"Creating Steam lobby for {maxPlayers} players (type: {lobbyType})...");

        // Create lobby and register callback (reuse pre-allocated callback)
        var apiCall = SteamMatchmaking.CreateLobby(lobbyType, maxPlayers);
        _lobbyCreatedCallback?.Set(apiCall);
    }

    /// <summary>
    /// Requests a list of lobbies from Steam.
    /// </summary>
    public static void RequestLobbyList() {
        if (!IsInitialized) return;

        Logger.Info("Requesting Steam lobby list...");
        
        // Add filters to only show lobbies with matching game version
        SteamMatchmaking.AddRequestLobbyListStringFilter(
            LobbyKeyVersion, 
            ModVersion, 
            ELobbyComparison.k_ELobbyComparisonEqual
        );
        
        var apiCall = SteamMatchmaking.RequestLobbyList();
        _lobbyMatchListCallback?.Set(apiCall);
    }

    /// <summary>
    /// Joins a Steam lobby.
    /// </summary>
    /// <param name="lobbyId">The ID of the lobby to join.</param>
    public static void JoinLobby(CSteamID lobbyId) {
        if (!IsInitialized) return;

        Logger.Info($"Joining Steam lobby: {lobbyId}");
        
        var apiCall = SteamMatchmaking.JoinLobby(lobbyId);
        _lobbyEnterCallback?.Set(apiCall);
    }

    /// <summary>
    /// The build this game is running, for telling whether the other player is running the same one.
    /// </summary>
    public static string LocalModVersion => ModVersion;

    /// <summary>
    /// The build the host of a lobby is running, as they published it when the lobby was opened.
    /// </summary>
    /// <param name="lobbyId">The lobby.</param>
    /// <returns>The version the host published, or an empty string if the lobby does not say.</returns>
    public static string GetLobbyModVersion(CSteamID lobbyId) {
        if (!IsInitialized) {
            return "";
        }

        return SteamMatchmaking.GetLobbyData(lobbyId, LobbyKeyVersion) ?? "";
    }

    /// <summary>
    /// Leaves the current lobby if hosting one.
    /// </summary>
    public static void LeaveLobby() {
        if (!IsInitialized) return;

        // Fast path check - direct comparison is faster than property access
        if (CurrentLobbyId == NilLobbyId) return;

        // Take local copy and clear state
        var lobbyToLeave = CurrentLobbyId;
        CurrentLobbyId = NilLobbyId;
        IsHostingLobby = false;
        _currentLobbyType = DefaultLobbyType;

        // Clear Rich Presence so friends no longer see "Join Game" option
        SteamFriends.ClearRichPresence();

        Logger.Info($"Leaving Steam lobby: {lobbyToLeave}");
        SteamMatchmaking.LeaveLobby(lobbyToLeave);
    }

    /// <summary>
    /// Opens the Steam overlay invite dialog to invite friends to the current lobby.
    /// Works for all lobby types (Public, Friends Only, Private).
    /// </summary>
    public static void OpenInviteDialog() {
        if (!IsInitialized) {
            Logger.Warn("Cannot open invite dialog: Steam is not initialized");
            return;
        }

        if (CurrentLobbyId == NilLobbyId) {
            Logger.Warn("Cannot open invite dialog: Not in a lobby");
            return;
        }

        Logger.Info($"Opening Steam invite dialog for lobby: {CurrentLobbyId}");
        SteamFriends.ActivateGameOverlayInviteDialog(CurrentLobbyId);
    }

    /// <summary>
    /// Marks the hosted lobby as ready or not ready for inbound players.
    /// Steam join links stay disabled until the host is actually listening.
    /// </summary>
    /// <param name="isReady">Whether the host has finished startup and can accept joins.</param>
    public static void SetLobbyReady(bool isReady) {
        if (!IsInitialized) {
            Logger.Warn("Cannot update Steam lobby readiness: Steam is not initialized");
            return;
        }

        if (CurrentLobbyId == NilLobbyId || !IsHostingLobby) {
            Logger.Warn("Cannot update Steam lobby readiness: no hosted lobby is active");
            return;
        }

        SteamMatchmaking.SetLobbyJoinable(CurrentLobbyId, isReady);

        // Clear first so stale connect keys are not left around while the host is still booting.
        SteamFriends.ClearRichPresence();

        if (!isReady) {
            SteamFriends.SetRichPresence("status", "Starting Lobby");
            Logger.Info($"Steam lobby {CurrentLobbyId} marked not ready");
            return;
        }

        if (_currentLobbyType != ELobbyType.k_ELobbyTypePrivate) {
            SteamFriends.SetRichPresence("connect", CurrentLobbyId.m_SteamID.ToString());
            SteamFriends.SetRichPresence("status", "In Lobby");
            Logger.Info($"Steam lobby {CurrentLobbyId} marked ready with connect={CurrentLobbyId.m_SteamID}");
        } else {
            SteamFriends.SetRichPresence("status", "In Private Lobby");
            Logger.Info($"Steam private lobby {CurrentLobbyId} marked ready");
        }
    }

    /// <summary>
    /// Lets go of Steam for the mod as the game quits, leaving shutting Steam down to the game.
    /// </summary>
    public static void Shutdown() {
        if (!IsInitialized) return;

        try {
            // Leave any active lobby
            LeaveLobby();

            // Stop standing in for the game, which goes on taking Steam's messages by itself
            StopCallbackTimer();

            // Nothing may ask Steam anything or be called by it once it is let go
            SteamRelayRecorder.Halt();
            SteamRelayMessaging.StopAnsweringSessionRequests();

            // Not shut down here: the game started Steam itself and shuts it down as it closes, after this
            // (DesktopPlatform.OnDestroy, SteamOnlineSubsystem.Dispose). Steam's own SteamAPI_Shutdown counts no
            // starts, so the call here, in the mod's quitting, shut Steam down for the whole game while the game was
            // still closing. On 10-17 the host's game crashed in Steam's own networking during that call (see
            // SteamRelayMessaging.AnswerSessionRequests). Only Steamworks.NET counts starts, for its own dispatcher of
            // Steam's messages, which is left set up now until the game is gone
            IsInitialized = false;
            Logger.Info("Steam: the mod is done with Steam, which the game shuts down itself as it closes");
        } catch (Exception e) {
            Logger.Error($"Steam: Exception during shutdown: {e}");
        }
    }

    /// <summary>
    /// Starts the timer-based callback loop.
    /// Uses Threading.Timer for lower overhead and better performance.
    /// </summary>
    private static void StartCallbackTimer() {
        if (_callbackTimer != null) return;

        _callbackTimer = new Timer(
            OnCallbackTimerElapsed,
            null,
            CallbackIntervalMs,
            CallbackIntervalMs
        );

        Logger.Info(
            _gamePumpHook != null
                ? $"Steam: the game takes Steam's messages, and the mod looks every {CallbackIntervalMs} ms to take " +
                  "them while the game takes none"
                : $"Steam: the mod takes Steam's messages every {CallbackIntervalMs} ms, alongside the game"
        );
    }

    /// <summary>
    /// Hooks the game's taking of Steam's messages (see <see cref="TakeMessagesForGame"/>). Without the hook the
    /// stand-in takes them at every look alongside the game, as it used to.
    /// </summary>
    private static void HookGamePump() {
        if (_gamePumpHook != null) {
            return;
        }

        var method = typeof(SteamOnlineSubsystem).GetMethod(
            nameof(SteamOnlineSubsystem.Update), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly
        );
        if (method == null) {
            Logger.Error("Steam: could not find where the game takes Steam's messages, so the mod takes them as well");
            return;
        }

        _gamePumpHook = new Hook(method, TakeMessagesForGame);
        Interlocked.Exchange(ref _gamePumpedAt, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// The game's taking of Steam's messages, which it does every frame.
    /// </summary>
    private delegate void GamePump(SteamOnlineSubsystem self);

    /// <summary>
    /// Takes Steam's messages for the game, as it does every frame on the main thread, unless the stand-in is taking
    /// them that moment, in which case the next frame takes them. Marks the game as taking them, which keeps the
    /// stand-in from doing so. A game whose Steam did not start takes none, and leaves them all to the stand-in.
    /// </summary>
    private static void TakeMessagesForGame(GamePump orig, SteamOnlineSubsystem self) {
        if (!self.DidInitialize) {
            orig(self);
            return;
        }

        Interlocked.Exchange(ref _gamePumpedAt, Stopwatch.GetTimestamp());
        if (!Monitor.TryEnter(PumpGate)) {
            return;
        }

        try {
            orig(self);
        } finally {
            Monitor.Exit(PumpGate);
        }
    }

    /// <summary>
    /// Stops the timer-based callback loop.
    /// </summary>
    private static void StopCallbackTimer() {
        var timer = Interlocked.Exchange(ref _callbackTimer, null);
        if (timer == null) return;

        timer.Dispose();

        // Dispose waits for no look under way. One taking Steam's messages right now is waited for here, and one that
        // comes after finds the timer gone and takes none, so that nothing the mod handles runs on the stand-in's
        // thread once the mod is done with Steam (review of 33ef6a6)
        lock (PumpGate) {
        }

        Logger.Info("Steam: Stopped callback timer");
    }

    /// <summary>
    /// Takes Steam's messages in the game's stead while the game has taken none for <see cref="StandInAfter"/>, as
    /// while it is stuck on a long frame, or stopped. Otherwise the game takes them every frame on the main thread,
    /// where what handles them may change what is shown, which only the main thread may do.
    /// </summary>
    private static void OnCallbackTimerElapsed(object? state) {
        if (!IsInitialized) return;

        var now = Stopwatch.GetTimestamp();
        var gamePumpedAt = Interlocked.Read(ref _gamePumpedAt);
        if (gamePumpedAt != 0 && now - gamePumpedAt < StandInAfter) {
            var since = Interlocked.Exchange(ref _standingInSince, 0);
            if (since != 0) {
                Logger.Info(
                    "Steam: the game takes Steam's messages again; the mod took them for it for " +
                    $"{(double) (now - since) / Stopwatch.Frequency:F1} s, after it took none for " +
                    $"{(double) StandInAfter / Stopwatch.Frequency:F0} s, {_standInWindow}"
                );
            }

            return;
        }

        if (!Monitor.TryEnter(PumpGate)) {
            return;
        }

        try {
            // Not once the stand-in is stopped (see StopCallbackTimer)
            if (Volatile.Read(ref _callbackTimer) == null) {
                return;
            }

            if (Interlocked.CompareExchange(ref _standingInSince, now, 0) == 0) {
                _standInWindow = FrameWatch.Focused switch {
                    true => "with its window in front at its last frame",
                    false => "with its window behind at its last frame",
                    null => "with no frame of it watched to tell where its window was"
                };
            }

            SteamAPI.RunCallbacks();
        } catch (Exception ex) {
            Logger.Error($"Steam: Exception in timer RunCallbacks:\n{ex}");
        } finally {
            Monitor.Exit(PumpGate);
        }
    }

    /// <summary>
    /// Gets the owner of the specified lobby.
    /// </summary>
    /// <param name="lobbyId">The lobby ID.</param>
    /// <returns>The SteamID of the lobby owner.</returns>
    public static CSteamID GetLobbyOwner(CSteamID lobbyId) => SteamMatchmaking.GetLobbyOwner(lobbyId);

    /// <summary>
    /// Callback invoked when a Steam lobby is created.
    /// </summary>
    private static void OnLobbyCreated(LobbyCreated_t callback, bool ioFailure) {
        if (ioFailure || callback.m_eResult != EResult.k_EResultOK) {
            Logger.Error($"Failed to create Steam lobby: {callback.m_eResult}");
            Volatile.Write(ref _pendingLobbyUsername, null);
            return;
        }

        var lobbyId = new CSteamID(callback.m_ulSteamIDLobby);
        CurrentLobbyId = lobbyId;
        IsHostingLobby = true;
        _currentLobbyType = _pendingLobbyType;

        Logger.Info($"Steam lobby created successfully: {lobbyId}");

        // Get username atomically
        var username = Volatile.Read(ref _pendingLobbyUsername);

        // Set lobby metadata using Steam persona name and game version
        var steamName = SteamFriends.GetPersonaName();
        SteamMatchmaking.SetLobbyData(lobbyId, LobbyKeyName, $"{steamName}'s Lobby");
        SteamMatchmaking.SetLobbyData(lobbyId, LobbyKeyVersion, ModVersion);
        SetLobbyReady(false);

        // Fire event for listeners
        LobbyCreatedEvent?.Invoke(lobbyId, username ?? "Unknown");
        
        Volatile.Write(ref _pendingLobbyUsername, null);
    }

    /// <summary>
    /// Callback invoked when a list of lobbies is received.
    /// </summary>
    private static void OnLobbyMatchList(LobbyMatchList_t callback, bool ioFailure) {
        if (ioFailure) {
            Logger.Error("Failed to get lobby list: IO Failure");
            return;
        }

        var count = (int) callback.m_nLobbiesMatching;
        Logger.Info($"Received {count} lobbies");
        
        var lobbyIds = new CSteamID[count];
        for (var i = 0; i < count; i++) {
            lobbyIds[i] = SteamMatchmaking.GetLobbyByIndex(i);
        }

        LobbyListReceivedEvent?.Invoke(lobbyIds);
    }

    /// <summary>
    /// Callback invoked when a lobby is entered.
    /// </summary>
    private static void OnLobbyEnter(LobbyEnter_t callback, bool ioFailure) {
        if (ioFailure) {
            Logger.Error("Failed to join lobby: IO Failure");
            LobbyJoinFailedEvent?.Invoke(null);
            return;
        }

        if (callback.m_EChatRoomEnterResponse != (uint) EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) {
            Logger.Error($"Failed to join lobby: {(EChatRoomEnterResponse) callback.m_EChatRoomEnterResponse}");
            LobbyJoinFailedEvent?.Invoke((EChatRoomEnterResponse) callback.m_EChatRoomEnterResponse);
            return;
        }

        var lobbyId = new CSteamID(callback.m_ulSteamIDLobby);
        CurrentLobbyId = lobbyId;
        // We are a client
        IsHostingLobby = false;
        
        Logger.Info($"Joined lobby successfully: {lobbyId}");
        LobbyJoinedEvent?.Invoke(lobbyId);
    }

    /// <summary>
    /// Callback for when the user accepts a lobby invite via Steam Overlay.
    /// </summary>
    private static void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t callback) {
        Logger.Info($"Accepting lobby invite: {callback.m_steamIDLobby}");
        JoinLobby(callback.m_steamIDLobby);
    }

    /// <summary>
    /// Callback for when the user joins a friend's game via Steam Friends list.
    /// </summary>
    private static void OnGameRichPresenceJoinRequested(GameRichPresenceJoinRequested_t callback) {
        Logger.Info($"Joining friend's game via Rich Presence: {callback.m_rgchConnect}");
        
        // Parse lobby ID from connection string
        if (ulong.TryParse(callback.m_rgchConnect, out var lobbyIdRaw)) {
            JoinLobby(new CSteamID(lobbyIdRaw));
        } else {
            Logger.Warn($"Could not parse lobby ID from connect string: {callback.m_rgchConnect}");
        }
    }
}
