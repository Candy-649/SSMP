using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GlobalEnums;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using SSMP.Game.Settings;
using SSMP.Hooks;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Ui;
using SSMP.Util;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Two-player saves. Two players whose saves have beaten the same bosses pair them with /coopsave, and from then on
/// neither save can be played alone. The save menu only opens a paired save while its partner is on the server; a host
/// opens their game first, and the save loads once the partner has joined. A player whose partner hasn't loaded their
/// save yet, or has left, waits seated on a bench and can't get up until the partner is back. Each time both are in,
/// both saves are backed up and each gets the saved objects of the world that only the other save changed, like broken
/// walls, pulled levers and paid tolls. Beaten bosses, arenas and anything else with a reward aren't copied, so nobody
/// misses a reward, and what a player owns, like money, items and upgrades, stays with them.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// Binding flags for instance members of the game.
    /// </summary>
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Binding flags for static members of the game.
    /// </summary>
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// The name of the file in the config folder that holds the paired saves.
    /// </summary>
    private const string MarkersFileName = "coop-saves.json";

    /// <summary>
    /// How long, in seconds, a request about pairing saves stays open.
    /// </summary>
    private const float PairRequestTime = 120f;

    /// <summary>
    /// The sheet of the texts of the game's message box.
    /// </summary>
    private const string MessageSheet = "Error";

    /// <summary>
    /// The key of the texts of the message box about two-player saves, without the "_TITLE" or "_DESC" that the message
    /// box adds.
    /// </summary>
    private const string MessageKey = "SSMP_COOP_SAVE";

    /// <summary>
    /// The value of SaveSlotButton.SaveFileStates for a slot with a save in it.
    /// </summary>
    private const int LoadedStatsState = 3;

    /// <summary>
    /// The name of the value of SaveSlotButton.SaveFileStates for an empty slot.
    /// </summary>
    private const string EmptyStateName = "Empty";

    /// <summary>
    /// The name of the scene of the main menu.
    /// </summary>
    private const string MenuSceneName = "Menu_Title";

    /// <summary>
    /// How many frames an action in the menu waits after the message box closes, so the menu takes input again first.
    /// </summary>
    private const int MenuDelayFrames = 2;

    /// <summary>
    /// The name of the FSM of benches that seats the hero.
    /// </summary>
    private const string BenchFsmName = "Bench Control";

    /// <summary>
    /// The state of the bench FSM while the hero sits, which is the only state that lets the hero get up.
    /// </summary>
    private const string BenchRestingStateName = "Resting";

    /// <summary>
    /// The events that make the hero get up from a bench, which wait while the save waits for the partner.
    /// </summary>
    private static readonly HashSet<string> GetUpEventNames = ["GET UP", "GET LEFT", "GET RIGHT"];

    /// <summary>
    /// Random numbers for the IDs of requests.
    /// </summary>
    private static readonly System.Random Random = new();

    /// <summary>
    /// The net client for sending updates.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The connected players, by ID.
    /// </summary>
    private readonly Dictionary<ushort, ClientPlayerData> _playerData;

    /// <summary>
    /// The settings of the mod, for the authentication key that the save key of the local player comes from.
    /// </summary>
    private readonly ModSettings _modSettings;

    /// <summary>
    /// The UI manager, which hosts for a two-player save before it loads.
    /// </summary>
    private readonly UiManager _uiManager;

    /// <summary>
    /// The paired saves, loaded when first needed.
    /// </summary>
    private CoopSaveMarkers? _markers;

    /// <summary>
    /// Hook for keeping two-player saves closed in the save menu without their partner.
    /// </summary>
    private Hook? _slotSubmitHook;

    /// <summary>
    /// Hook for forgetting the pairing of saves that are cleared.
    /// </summary>
    private Hook? _clearSaveHook;

    /// <summary>
    /// Hook for keeping a waiting player seated on their bench.
    /// </summary>
    private Hook? _processEventHook;

    /// <summary>
    /// The field of a save slot button that says whether the slot has a save.
    /// </summary>
    private FieldInfo? _saveFileStateField;

    /// <summary>
    /// The text of the message box about two-player saves.
    /// </summary>
    private string _messageText = "";

    /// <summary>
    /// The save slot button of a two-player save that waits in the menu for its partner to join the host, or null.
    /// </summary>
    private UnityEngine.UI.SaveSlotButton? _waitingButton;

    /// <summary>
    /// The event data that the waiting save slot button was submitted with.
    /// </summary>
    private BaseEventData? _waitingEventData;

    /// <summary>
    /// The pairing of the save that waits in the menu for its partner, or null.
    /// </summary>
    private CoopSaveMarker? _waitingMarker;

    /// <summary>
    /// Changes whenever the wait in the menu ends another way, which cancels the actions in the menu that wait for a
    /// few frames.
    /// </summary>
    private int _menuActionToken;

    /// <summary>
    /// Whether this class submits a save slot button itself, which the hook lets through.
    /// </summary>
    private bool _bypassSubmit;

    /// <summary>
    /// The slot of the save that the state of the session belongs to, or -1 outside a save.
    /// </summary>
    private int _sessionSlot = -1;

    /// <summary>
    /// Whether the local player waits for the partner and can't move or get up from their bench.
    /// </summary>
    private bool _held;

    /// <summary>
    /// Whether this class took control from the hero while holding them away from a bench, so it gives control back.
    /// </summary>
    private bool _tookControl;

    /// <summary>
    /// Whether both saves were checked since the save was loaded, after which a missing partner only holds the player
    /// once they sit on a bench.
    /// </summary>
    private bool _everChecked;

    /// <summary>
    /// The ID of the partner that the saves were last checked with while they stay in the save, or null.
    /// </summary>
    private ushort? _checkedWith;

    /// <summary>
    /// Whether updating the session threw, which is only logged once.
    /// </summary>
    private bool _updateFailed;

    /// <summary>
    /// Whether moving the check along threw, which is only logged once.
    /// </summary>
    private bool _checkFailed;

    /// <summary>
    /// The request of the local player to pair saves, until the other player answers.
    /// </summary>
    private PairingRequest? _sentPairRequest;

    /// <summary>
    /// A request of another player to pair saves, until the local player answers.
    /// </summary>
    private PairingRequest? _receivedPairRequest;

    /// <summary>
    /// A request to pair saves that the local player agreed to, until the game of the player who asked confirms it.
    /// </summary>
    private PairingRequest? _acceptedPairRequest;

    /// <summary>
    /// The last request that paired a local save before the other player's game paired theirs, which their game can
    /// still cancel.
    /// </summary>
    private PairingRequest? _confirmedPairRequest;

    /// <summary>
    /// The request of the local player to make a two-player save a normal save again, until the partner agrees.
    /// </summary>
    private PairingRequest? _sentUnpairRequest;

    /// <summary>
    /// A request of the partner to make a two-player save a normal save again, until the local player agrees.
    /// </summary>
    private PairingRequest? _receivedUnpairRequest;

    public CoopSave(
        NetClient netClient,
        Dictionary<ushort, ClientPlayerData> playerData,
        ModSettings modSettings,
        UiManager uiManager
    ) {
        _netClient = netClient;
        _playerData = playerData;
        _modSettings = modSettings;
        _uiManager = uiManager;
    }

    /// <summary>
    /// A request between the local player and another player about pairing saves, which stays open for
    /// <see cref="PairRequestTime"/> seconds.
    /// </summary>
    private sealed class PairingRequest {
        /// <summary>
        /// The other player.
        /// </summary>
        public ushort PlayerId { get; init; }

        /// <summary>
        /// The ID of the request, which the answers repeat.
        /// </summary>
        public ulong Id { get; init; }

        /// <summary>
        /// The slot of the local save that the request is for.
        /// </summary>
        public int Slot { get; init; }

        /// <summary>
        /// The bosses that the save of the other player has beaten, for a request of theirs.
        /// </summary>
        public List<string> Defeats { get; init; } = [];

        /// <summary>
        /// The save key of the other player, for undoing a pairing.
        /// </summary>
        public string PartnerKey { get; init; } = "";

        /// <summary>
        /// When the request was made or answered.
        /// </summary>
        public float Started { get; } = Time.unscaledTime;

        /// <summary>
        /// Whether the request is still open.
        /// </summary>
        public bool IsOpen => Time.unscaledTime - Started < PairRequestTime;
    }

    /// <summary>
    /// The save key of the local player.
    /// </summary>
    private string LocalKey => AuthUtil.GetSaveKey(_modSettings.AuthKey);

    /// <summary>
    /// The ID of the partner that the saves were checked with while both players are in the save, or null.
    /// </summary>
    public ushort? CheckedPartnerId => _checkedWith;

    /// <summary>
    /// Registers the hooks, which stay for as long as the game runs because the save menu is used before connecting.
    /// </summary>
    public void Initialize() {
        var buttonType = typeof(UnityEngine.UI.SaveSlotButton);
        _saveFileStateField = buttonType.GetField("saveFileState", InstanceFlags);
        _slotSubmitHook = CreateHook(
            buttonType.GetMethod("OnSubmit", InstanceFlags, null, [typeof(BaseEventData)], null),
            new Action<Action<UnityEngine.UI.SaveSlotButton, BaseEventData>, UnityEngine.UI.SaveSlotButton,
                BaseEventData>(OnSaveSlotSubmit)
        );
        _clearSaveHook = CreateHook(
            typeof(global::GameManager).GetMethod(
                "ClearSaveFile", InstanceFlags, null, [typeof(int), typeof(Action<bool>)], null
            ),
            new Action<Action<global::GameManager, int, Action<bool>>, global::GameManager, int, Action<bool>>(
                OnClearSaveFile
            )
        );
        _processEventHook = CreateHook(
            typeof(Fsm).GetMethod("ProcessEvent", InstanceFlags, null, [typeof(FsmEvent), typeof(FsmEventData)], null),
            new Action<Action<Fsm, FsmEvent, FsmEventData>, Fsm, FsmEvent, FsmEventData>(OnProcessEvent)
        );
        RegisterInteractionHooks();
        RegisterWishTalkHooks();
        RegisterWishBoardHooks();
        RegisterDeliveryHooks();
        RegisterLiftHooks();
        RegisterStoryItemHooks();

        EventHooks.LanguageHas += OnLanguageHas;
        EventHooks.LanguageGet += OnLanguageGet;
        EventHooks.HeroControllerUpdate += OnHeroControllerUpdate;
        EventHooks.UIManagerReturnToMainMenu += OnReturnToMainMenu;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
        SceneManager.sceneLoaded += OnWorldSceneLoaded;
        SceneManager.sceneUnloaded += OnWorldSceneUnloaded;
        _uiManager.HostBeforeSaveStoppedEvent += OnHostBeforeSaveStopped;
        _uiManager.HostSaveSelectionClosedEvent += OnHostSaveSelectionClosed;
    }

    /// <summary>
    /// Creates a hook, logging instead of throwing when the method is missing.
    /// </summary>
    private static Hook? CreateHook(MethodInfo? method, Delegate detour) {
        if (method == null) {
            Logger.Error($"Could not find the method for {detour.Method.Name}; hook was not registered");
            return null;
        }

        try {
            return new Hook(method, detour);
        } catch (Exception e) {
            Logger.Error($"Could not hook the method for {detour.Method.Name}:\n{e}");
            return null;
        }
    }

    #region Session

    /// <summary>
    /// Keeps the local player waiting while their two-player save waits for the partner, and checks both saves once the
    /// partner is in.
    /// </summary>
    private void OnHeroControllerUpdate(HeroController hero) {
        try {
            UpdateSession(hero);
        } catch (Exception e) {
            if (!_updateFailed) {
                _updateFailed = true;
                Logger.Error($"Could not update the two-player save:\n{e}");
            }
        }

        // Its own try, because a line of text failing must not stop the save from keeping step with the partner, and
        // a session that fails must still leave the players a way to pair without typing
        try {
            UpdatePairPrompt();
        } catch (Exception e) {
            if (!_pairPromptFailed) {
                _pairPromptFailed = true;
                Logger.Error($"Could not show the two-player save prompt:\n{e}");
            }
        }
    }

    /// <summary>
    /// Holds or releases the local player and moves the check along for the loaded save.
    /// </summary>
    private void UpdateSession(HeroController hero) {
        var gameManager = global::GameManager.instance;
        if (gameManager == null || PlayerData.instance == null) {
            return;
        }

        WatchLevelSaves(gameManager);

        var slot = gameManager.profileID;
        if (slot != _sessionSlot) {
            ResetSession(true);
            _sessionSlot = slot;
            _loadedWithCheckpoint = GetMarker(slot)?.BossScene != null;
        }

        var marker = GetMarker(slot);
        if (marker == null) {
            FinishSummon(hero);
            ReleaseHold(hero);
            return;
        }

        var partner = FindPartner(marker);
        if (partner != null && _checkedWith != partner.Id) {
            // A check that throws must not keep the hold below from working
            try {
                UpdateCheck(marker, partner);
            } catch (Exception e) {
                if (!_checkFailed) {
                    _checkFailed = true;
                    Logger.Error($"Could not check the two-player save:\n{e}");
                }
            }
        }

        // A boss checkpoint that throws must not keep the hold below from working either
        try {
            UpdateCheckpoint(hero, marker, partner);
        } catch (Exception e) {
            if (!_checkpointFailed) {
                _checkpointFailed = true;
                Logger.Error($"Could not update the boss checkpoint of the two-player save:\n{e}");
            }
        }

        UpdateWishTalk(partner);
        UpdateDeliverySummon(hero, partner != null && _checkedWith == partner.Id ? partner : null);

        if (partner != null && _checkedWith == partner.Id) {
            UpdateCheckedPlayTime(marker);
            UpdateInteractions(partner);
            UpdateWorldChanges(partner);
            UpdateWishes(partner);
            UpdateStoryFlags(partner);
            UpdateLifts(partner);
            ReleaseHold(hero);
            return;
        }

        if (_held || !_everChecked || PlayerData.instance.atBench) {
            Hold(hero, marker, partner);
        }
    }

    /// <summary>
    /// Makes the hero wait for the partner: seated on a bench, the bench doesn't let them get up, and anywhere else they
    /// can't move.
    /// </summary>
    private void Hold(HeroController hero, CoopSaveMarker marker, ClientPlayerData? partner) {
        var gameManager = global::GameManager.instance;
        if (gameManager.GameState != GameState.PLAYING || gameManager.IsInSceneTransition || hero.cState.dead ||
            hero.cState.transitioning) {
            return;
        }

        var atBench = PlayerData.instance.atBench;
        if (!_held) {
            _held = true;
            Logger.Info($"Two-player save waits for {marker.PartnerName}");
            Chat(
                partner != null
                    ? $"Waiting for {partner.Username} to load your two-player save."
                    : atBench
                        ? $"Your two-player save waits for {marker.PartnerName}. You can't get up until they are back."
                        : $"Your two-player save waits for {marker.PartnerName}. You can't move until they are back."
            );
        }

        // The bench keeps a seated hero without control, so control is only taken away from a bench
        if (!atBench && !hero.controlReqlinquished) {
            hero.RelinquishControl();
            _tookControl = true;
        }
    }

    /// <summary>
    /// Lets the hero get up or move again once the partner is in.
    /// </summary>
    private void ReleaseHold(HeroController? hero) {
        if (!_held) {
            return;
        }

        _held = false;
        if (_tookControl && hero != null && hero.controlReqlinquished && PlayerData.instance?.atBench != true) {
            hero.RegainControl();
        }

        _tookControl = false;
    }

    /// <summary>
    /// Keeps a waiting hero seated by holding back the events that make them get up from the bench.
    /// </summary>
    private void OnProcessEvent(
        Action<Fsm, FsmEvent, FsmEventData> orig,
        Fsm self,
        FsmEvent fsmEvent,
        FsmEventData eventData
    ) {
        if (_held && fsmEvent != null && GetUpEventNames.Contains(fsmEvent.Name) && self.Name == BenchFsmName &&
            self.ActiveStateName == BenchRestingStateName) {
            return;
        }

        orig(self, fsmEvent!, eventData);
    }

    /// <summary>
    /// Forgets everything about the loaded save, for when another save loads or the player goes to the menu.
    /// </summary>
    /// <param name="notifyPartner">Whether to tell the partner that the local player left the save.</param>
    private void ResetSession(bool notifyPartner) {
        var partnerId = _checkedWith ?? _checkPartnerId;
        if (notifyPartner && partnerId is { } id && _playerData.ContainsKey(id)) {
            Send(new CoopSaveUpdate { TargetId = id, Kind = CoopSaveUpdateKind.Left, Key = _highestCheckKey });
        }

        // The play time that both players played together is kept for the next check
        if (_checkedWith != null) {
            SaveMarkers();
        }

        _held = false;
        _tookControl = false;
        _everChecked = false;

        // Before the pairing is forgotten, so that a question of the partner still on screen is answered back to them
        // and a button that waited on them is told it is over. ResetWishTalk below runs it again, which does nothing.
        ResetWishConfirm();
        _checkedWith = null;
        ResetCheck();
        ResetCheckpointSession();
        ResetWorldChanges();
        ResetInteractionSession();
        ResetWishes();
        ResetStoryFlags();
        ResetWishTalk();
        ResetLifts();
        ResetDeliveries();
        ResetStoryItems();
    }

    /// <summary>
    /// Leaves the loaded save when the game goes to the main menu.
    /// </summary>
    private void OnReturnToMainMenu() {
        ResetSession(true);
        _sessionSlot = -1;
    }

    /// <summary>
    /// Leaves the loaded save when the scene of the main menu loads, and ends the boss checkpoint of the loaded save
    /// when the local player leaves the scene of its fight.
    /// </summary>
    private void OnActiveSceneChanged(Scene oldScene, Scene newScene) {
        if (newScene.name == MenuSceneName) {
            OnReturnToMainMenu();
            return;
        }

        OnCheckpointSceneChanged(newScene.name);
        ResetInteractions();
        OnWishTalkSceneChanged();
        OnLiftSceneChanged();
        OnDeliverySceneChanged();
    }

    /// <summary>
    /// Called when the local player connected and knows the players that were on the server already. A partner among
    /// them makes the save that waits in the menu load.
    /// </summary>
    public void OnLocalConnect() {
        _highestCheckKey = 0;
        if (_waitingMarker != null && FindPartner(_waitingMarker) != null) {
            LoadWaitingSave();
        }
    }

    /// <summary>
    /// Called when a player connects. The partner of a save that waits in the menu makes it load, and the partner of
    /// the loaded save starts a new check.
    /// </summary>
    public void OnPlayerConnect(ClientPlayerData player) {
        if (_waitingMarker != null && IsPartner(player, _waitingMarker)) {
            LoadWaitingSave();
            return;
        }

        var marker = GetCurrentMarker();
        if (marker == null || !IsPartner(player, marker)) {
            return;
        }

        // The game of the partner starts counting checks anew
        _checkedWith = null;
        ResetCheck();
        _highestCheckKey = 0;
        Chat($"{player.Username} is here. Your two-player save continues once they have loaded theirs.");
    }

    /// <summary>
    /// Called when a player disconnects, after which the loaded save waits for them again if they are its partner.
    /// </summary>
    public void OnPlayerDisconnect(ushort id) {
        ForgetRequestsOf(id);

        if (_checkPartnerId == id || _checkedWith == id) {
            PartnerLeft(id, "left");
            _highestCheckKey = 0;
        }
    }

    /// <summary>
    /// The partner is no longer in the save, so the check with them ends and the save waits for them again.
    /// </summary>
    /// <param name="id">The ID of the partner.</param>
    /// <param name="how">How they left, for the message, or null for no message.</param>
    private void PartnerLeft(ushort id, string? how) {
        if (_checkPartnerId != id && _checkedWith != id) {
            return;
        }

        var wasChecked = _checkedWith == id;

        // Before the partner is forgotten: a question of theirs still on screen is answered back to them, and a
        // button that waited on them tells them it is over. Afterwards there is nobody left to address either to.
        ResetWishConfirm();
        _checkedWith = null;
        ResetCheck();
        if (wasChecked) {
            // The play time that both players played together is kept for the next check
            SaveMarkers();
        }

        // A boss fight that the partner was in ends for the local player too
        if (wasChecked && IsInBossFight()) {
            _interruptPending = true;
        }

        if (wasChecked && how != null && GetCurrentMarker() is { } marker) {
            Chat($"{marker.PartnerName} {how}. Your two-player save waits for them at the next bench you sit on.");
        }
    }

    /// <summary>
    /// Called when the local player disconnects from the server.
    /// </summary>
    public void OnLocalDisconnect() {
        _sentPairRequest = null;
        _receivedPairRequest = null;
        _acceptedPairRequest = null;
        _confirmedPairRequest = null;
        _sentUnpairRequest = null;
        _receivedUnpairRequest = null;
        _checkedWith = null;
        ResetCheck();
        _highestCheckKey = 0;

        if (_waitingMarker != null) {
            OnHostBeforeSaveStopped();
            _uiManager.StopHostBeforeSave();
        } else {
            _menuActionToken++;
        }
    }

    /// <summary>
    /// Handles an update of a two-player save from another player.
    /// </summary>
    public void OnCoopSaveUpdate(CoopSaveUpdate update) {
        if (!_playerData.TryGetValue(update.PlayerId, out var player)) {
            return;
        }

        switch (update.Kind) {
            case CoopSaveUpdateKind.PairRequest:
                OnPairRequest(player, update);
                break;
            case CoopSaveUpdateKind.PairAccept:
                OnPairAccept(player, update);
                break;
            case CoopSaveUpdateKind.PairRefused:
                OnPairRefused(player, update);
                break;
            case CoopSaveUpdateKind.PairConfirm:
                OnPairConfirm(player, update);
                break;
            case CoopSaveUpdateKind.PairCancel:
                OnPairCancel(player, update);
                break;
            case CoopSaveUpdateKind.UnpairRequest:
                OnUnpairRequest(player, update);
                break;
            case CoopSaveUpdateKind.Unpaired:
                OnUnpaired(player);
                break;
            case CoopSaveUpdateKind.Hello:
                OnHello(player, update);
                break;
            case CoopSaveUpdateKind.WorldState:
                OnWorldState(player, update);
                break;
            case CoopSaveUpdateKind.Left:
                OnLeft(player, update);
                break;
            case CoopSaveUpdateKind.BossFight:
                OnBossFight(player, update);
                break;
            case CoopSaveUpdateKind.WorldChange:
                OnWorldChange(player, update);
                break;
            case CoopSaveUpdateKind.Interaction:
                OnInteraction(player, update);
                break;
            case CoopSaveUpdateKind.WishChange:
                OnWishChange(player, update);
                break;
            case CoopSaveUpdateKind.WishTalk:
                OnWishTalk(player, update);
                break;
            case CoopSaveUpdateKind.WishTurnIn:
                OnWishTurnIn(player, update);
                break;
            case CoopSaveUpdateKind.WishProgress:
                OnWishProgress(player, update);
                break;
            case CoopSaveUpdateKind.LiftMove:
                OnLiftMove(player, update);
                break;
            case CoopSaveUpdateKind.LiftCall:
                OnLiftCall(player, update);
                break;
            case CoopSaveUpdateKind.LiftStateRequest:
                OnLiftStateRequest(player, update);
                break;
            case CoopSaveUpdateKind.LiftState:
                OnLiftState(player, update);
                break;
            case CoopSaveUpdateKind.LiftDrive:
                OnLiftDrive(player, update);
                break;
            case CoopSaveUpdateKind.DeliveryBreak:
                OnDeliveryBreak(player, update);
                break;
            case CoopSaveUpdateKind.DeliverySummon:
                OnDeliverySummon(player, update);
                break;
            case CoopSaveUpdateKind.StoryItem:
                OnStoryItem(player, update);
                break;
            case CoopSaveUpdateKind.WishConfirm:
                OnWishConfirm(player, update);
                break;
        }
    }

    #endregion

    #region Pairing

    /// <summary>
    /// The name of the key that agrees to a two-player save, for the line that offers one. It is the default binding:
    /// a player who rebinds it in the settings file presses their own key, and only reads the wrong name here.
    /// </summary>
    private const string PairKeyName = "J";

    /// <summary>
    /// Whether the key that agrees to a two-player save was already down last frame.
    /// </summary>
    private bool _pairKeyHeld;

    /// <summary>
    /// Whether showing the line that offers a two-player save failed, so it is only logged once.
    /// </summary>
    private bool _pairPromptFailed;

    /// <summary>
    /// Offers a two-player save in game, so that pairing takes a key rather than a typed command, and pairs when that
    /// key is pressed. Both players press it: this asks for them and agrees for them, whichever of the two it is.
    /// </summary>
    private void UpdatePairPrompt() {
        var prompt = _uiManager.CoopPrompt;

        // A two-player save needs both players on the server, and a save loaded to pair
        if (!_netClient.IsConnected || !IsInGame() || _playerData.Count != 1) {
            prompt.Hide();
            _pairKeyHeld = false;
            return;
        }

        var other = _playerData.Values.First();
        var marker = GetMarker(global::GameManager.instance.profileID);
        if (other.SaveKey.Length == 0 || (marker != null && marker.PartnerKey == other.SaveKey)) {
            prompt.Hide();
            _pairKeyHeld = false;
            return;
        }

        if (_sentPairRequest is { IsOpen: true } sent && sent.PlayerId == other.Id) {
            prompt.Show($"Waiting for {other.Username} to press {PairKeyName} too");
        } else if (_receivedPairRequest is { IsOpen: true } received && received.PlayerId == other.Id) {
            prompt.Show($"{other.Username} wants a two-player save. Press {PairKeyName} to agree");
        } else {
            prompt.Show($"Press {PairKeyName} to play a two-player save with {other.Username}");
        }

        // Only where the key goes down, or holding it would ask again every frame of the whole press
        var held = _modSettings.Keybinds.CoopPair.IsPressed;
        if (held && !_pairKeyHeld) {
            OnCommand(["/coopsave"]);
        }

        _pairKeyHeld = held;
    }

    /// <summary>
    /// Runs /coopsave: asks the other player to pair the current saves or agrees to their request, or with "off" makes
    /// the two-player save a normal save again.
    /// </summary>
    public void OnCommand(string[] arguments) {
        if (!IsInGame()) {
            Chat("Load a save first.");
            return;
        }

        var slot = global::GameManager.instance.profileID;
        var marker = GetMarker(slot);

        if (arguments.Length > 1 && arguments[1].Equals("off", StringComparison.OrdinalIgnoreCase)) {
            Unpair(slot, marker);
            return;
        }

        if (!_netClient.IsConnected) {
            Chat("Connect to your teammate first to pair your saves.");
            return;
        }

        if (_playerData.Count != 1) {
            Chat("A two-player save needs you and exactly one other player on the server.");
            return;
        }

        var other = _playerData.Values.First();
        if (other.SaveKey.Length == 0) {
            Chat($"The game of {other.Username} doesn't support two-player saves.");
            return;
        }

        if (_acceptedPairRequest is { IsOpen: true } accepted && accepted.PlayerId == other.Id) {
            Chat($"Waiting for the game of {other.Username} to confirm the pairing.");
            return;
        }

        // A request is answered even if the local save is already paired with the other player, because their save may
        // have lost its pairing, like after they moved to another computer
        if (_receivedPairRequest is { IsOpen: true } received && received.PlayerId == other.Id) {
            _receivedPairRequest = null;
            AcceptPairRequest(slot, other, received);
            return;
        }

        if (marker != null && marker.PartnerKey == other.SaveKey) {
            Chat($"Your current save is already a two-player save with {other.Username}.");
            return;
        }

        _sentPairRequest = new PairingRequest { PlayerId = other.Id, Id = NewRequestId(), Slot = slot };
        Send(new CoopSaveUpdate {
            TargetId = other.Id,
            Kind = CoopSaveUpdateKind.PairRequest,
            Key = _sentPairRequest.Id,
            Records = GetDefeatRecords()
        });
        Chat(
            $"Asked {other.Username} to pair your current saves as a two-player save, which neither of you can play " +
            "alone. They need to type /coopsave too."
        );
    }

    /// <summary>
    /// Another player asks to pair saves. If the local player asked them at the same time, one of the two requests is
    /// accepted right away.
    /// </summary>
    private void OnPairRequest(ClientPlayerData player, CoopSaveUpdate update) {
        var request = new PairingRequest { PlayerId = player.Id, Id = update.Key, Defeats = update.Records };

        if (_sentPairRequest is { IsOpen: true } sent && sent.PlayerId == player.Id && IsInGame() &&
            global::GameManager.instance.profileID == sent.Slot) {
            // The request with the larger ID goes ahead, so the other player accepts the request of the local player
            if (update.Key < sent.Id) {
                return;
            }

            _sentPairRequest = null;
            AcceptPairRequest(sent.Slot, player, request);
            return;
        }

        _receivedPairRequest = request;
        Chat(
            $"{player.Username} wants to pair your current saves as a two-player save, which neither of you can play " +
            "alone. Type /coopsave to agree."
        );
    }

    /// <summary>
    /// Agrees to a request to pair saves if both saves have beaten the same bosses. Otherwise the saves would differ in
    /// which bosses are still there, and one player would miss a boss and its reward. Nothing is paired until the game
    /// of the player who asked confirms.
    /// </summary>
    private void AcceptPairRequest(int slot, ClientPlayerData player, PairingRequest request) {
        var localDefeats = GetDefeatRecords();
        if (!HaveSameDefeats(player, request.Defeats, localDefeats)) {
            Send(new CoopSaveUpdate {
                TargetId = player.Id,
                Kind = CoopSaveUpdateKind.PairRefused,
                Key = request.Id,
                Records = localDefeats
            });
            return;
        }

        _acceptedPairRequest = new PairingRequest { PlayerId = player.Id, Id = request.Id, Slot = slot };
        Send(new CoopSaveUpdate {
            TargetId = player.Id,
            Kind = CoopSaveUpdateKind.PairAccept,
            Key = request.Id,
            Records = localDefeats
        });
        Chat($"Agreed to pair your current save with the save of {player.Username}. Waiting for their game to confirm.");
    }

    /// <summary>
    /// The other player agreed to the request of the local player. If the request still stands for the loaded save and
    /// the beaten bosses still match, the local save is paired and the other player's game is asked to pair theirs.
    /// </summary>
    private void OnPairAccept(ClientPlayerData player, CoopSaveUpdate update) {
        var sent = _sentPairRequest;
        if (sent == null || sent.PlayerId != player.Id || sent.Id != update.Key) {
            SendPairCancel(player, update.Key);
            return;
        }

        _sentPairRequest = null;
        if (!sent.IsOpen || !IsInGame() || global::GameManager.instance.profileID != sent.Slot) {
            SendPairCancel(player, update.Key);
            Chat(
                $"{player.Username} agreed too late, because your request ran out or you changed saves. Type /coopsave " +
                "to ask again."
            );
            return;
        }

        // A boss may have been beaten since the request
        if (!HaveSameDefeats(player, update.Records, GetDefeatRecords())) {
            SendPairCancel(player, update.Key);
            return;
        }

        Pair(sent.Slot, player);
        _confirmedPairRequest = new PairingRequest {
            PlayerId = player.Id,
            Id = update.Key,
            Slot = sent.Slot,
            PartnerKey = player.SaveKey
        };
        Send(new CoopSaveUpdate { TargetId = player.Id, Kind = CoopSaveUpdateKind.PairConfirm, Key = update.Key });
    }

    /// <summary>
    /// The game of the player who asked paired their save, so the local save is paired too if it is still the save that
    /// agreed. Otherwise the other player's game undoes its pairing.
    /// </summary>
    private void OnPairConfirm(ClientPlayerData player, CoopSaveUpdate update) {
        var accepted = _acceptedPairRequest;
        if (accepted == null || accepted.PlayerId != player.Id || accepted.Id != update.Key) {
            SendPairCancel(player, update.Key);
            return;
        }

        _acceptedPairRequest = null;
        if (!accepted.IsOpen || !IsInGame() || global::GameManager.instance.profileID != accepted.Slot) {
            SendPairCancel(player, update.Key);
            Chat(
                $"The pairing with {player.Username} didn't go through, because you changed saves. Type /coopsave to " +
                "try again."
            );
            return;
        }

        Pair(accepted.Slot, player);
    }

    /// <summary>
    /// A request to pair saves didn't go through. A local save that was already paired for it is unpaired again.
    /// </summary>
    private void OnPairCancel(ClientPlayerData player, CoopSaveUpdate update) {
        if (_acceptedPairRequest is { } accepted && accepted.PlayerId == player.Id && accepted.Id == update.Key) {
            _acceptedPairRequest = null;
            Chat($"The pairing with {player.Username} didn't go through. Type /coopsave to try again.");
        }

        if (_sentPairRequest is { } sent && sent.PlayerId == player.Id && sent.Id == update.Key) {
            _sentPairRequest = null;
        }

        if (_confirmedPairRequest is not { } confirmed || confirmed.PlayerId != player.Id ||
            confirmed.Id != update.Key) {
            return;
        }

        _confirmedPairRequest = null;
        if (GetMarker(confirmed.Slot) is { } marker && marker.PartnerKey == confirmed.PartnerKey) {
            RemoveLocalPairing(confirmed.Slot);
            Chat(
                $"The pairing with {player.Username} was undone, because their game changed saves before it finished. " +
                "Type /coopsave to try again."
            );
        }
    }

    /// <summary>
    /// The other player couldn't agree to the request of the local player, because their save has beaten different
    /// bosses.
    /// </summary>
    private void OnPairRefused(ClientPlayerData player, CoopSaveUpdate update) {
        if (_sentPairRequest is not { } sent || sent.PlayerId != player.Id || sent.Id != update.Key) {
            return;
        }

        _sentPairRequest = null;
        if (HaveSameDefeats(player, update.Records, GetDefeatRecords())) {
            Chat($"{player.Username} couldn't agree, because their save had beaten different bosses. Type /coopsave to ask again.");
        }
    }

    /// <summary>
    /// Tells the other player's game that a request to pair saves didn't go through.
    /// </summary>
    private void SendPairCancel(ClientPlayerData player, ulong requestId) {
        Send(new CoopSaveUpdate { TargetId = player.Id, Kind = CoopSaveUpdateKind.PairCancel, Key = requestId });
    }

    /// <summary>
    /// Whether two saves have beaten the same bosses. If not, the local player hears how many differ.
    /// </summary>
    private static bool HaveSameDefeats(ClientPlayerData player, List<string> partnerDefeats, List<string> localDefeats) {
        var onlyPartner = partnerDefeats.Except(localDefeats).ToList();
        var onlyLocal = localDefeats.Except(partnerDefeats).ToList();
        if (onlyPartner.Count == 0 && onlyLocal.Count == 0) {
            return true;
        }

        Logger.Info(
            $"Not pairing with {player.Username}, beaten bosses differ. Only theirs: {string.Join(", ", onlyPartner)}; " +
            $"only local: {string.Join(", ", onlyLocal)}"
        );
        Chat(
            $"These saves can't become a two-player save, because they have beaten different bosses: {player.Username} " +
            $"beat {onlyPartner.Count} that you haven't, and you beat {onlyLocal.Count} that they haven't. A two-player " +
            "save needs the same bosses beaten in both saves, like two new games."
        );
        return false;
    }

    /// <summary>
    /// Pairs the save in a slot with the save of another player, after which both saves get checked.
    /// </summary>
    private void Pair(int slot, ClientPlayerData player) {
        var previous = GetMarker(slot);
        GetMarkers().Slots[GetSlotKey(slot)] = new CoopSaveMarker {
            PartnerKey = player.SaveKey,
            PartnerName = player.Username,
            PairedUtc = DateTime.UtcNow
        };
        SaveMarkers();

        _receivedPairRequest = null;
        _checkedWith = null;
        ResetCheck();

        Logger.Info($"Paired save slot {slot} with the save of {player.Username}");
        Chat(
            previous != null && previous.PartnerKey != player.SaveKey
                ? $"Your current save is now a two-player save with {player.Username} instead of " +
                  $"{previous.PartnerName}. Both saves get backed up and compared now."
                : $"Your current save is now a two-player save with {player.Username}. Both saves get backed up and " +
                  "compared now."
        );
    }

    /// <summary>
    /// Makes the two-player save in a slot a normal save again, for both players. Both need to be on the server, so
    /// that it can't be used to play the save alone. If the partner hasn't loaded the save, which their game may have
    /// lost, they need to agree with /coopsave off too.
    /// </summary>
    private void Unpair(int slot, CoopSaveMarker? marker) {
        if (_receivedUnpairRequest is { IsOpen: true } received &&
            _playerData.TryGetValue(received.PlayerId, out var requester)) {
            _receivedUnpairRequest = null;
            AgreeToUnpair(slot, marker, requester);
            return;
        }

        if (marker == null) {
            Chat("Your current save isn't a two-player save.");
            return;
        }

        var partner = FindPartner(marker);
        if (partner == null) {
            Chat($"A two-player save only becomes a normal save again while {marker.PartnerName} is here.");
            return;
        }

        if (_checkedWith == partner.Id) {
            RemoveLocalPairing(slot);
            Chat($"Your two-player save with {partner.Username} is a normal save again, for both of you.");
            Send(new CoopSaveUpdate { TargetId = partner.Id, Kind = CoopSaveUpdateKind.Unpaired });
            return;
        }

        _sentUnpairRequest = new PairingRequest { PlayerId = partner.Id, Id = NewRequestId(), Slot = slot };
        Send(new CoopSaveUpdate {
            TargetId = partner.Id,
            Kind = CoopSaveUpdateKind.UnpairRequest,
            Key = _sentUnpairRequest.Id
        });
        Chat(
            $"Asked {partner.Username} to agree to make this two-player save a normal save again. They need to type " +
            "/coopsave off too."
        );
    }

    /// <summary>
    /// The partner asks to make the two-player save a normal save again. If the local player asked too, both agree.
    /// </summary>
    private void OnUnpairRequest(ClientPlayerData player, CoopSaveUpdate update) {
        if (_sentUnpairRequest is { IsOpen: true } sent && sent.PlayerId == player.Id && IsInGame() &&
            global::GameManager.instance.profileID == sent.Slot) {
            _sentUnpairRequest = null;
            AgreeToUnpair(sent.Slot, GetMarker(sent.Slot), player);
            return;
        }

        _receivedUnpairRequest = new PairingRequest { PlayerId = player.Id, Id = update.Key };
        Chat(
            $"{player.Username} wants to make your two-player save a normal save again, for both of you. Type " +
            "/coopsave off to agree."
        );
    }

    /// <summary>
    /// Agrees to make the two-player save with another player a normal save again: the current save is unpaired if it
    /// is paired with them, and their save is unpaired.
    /// </summary>
    private void AgreeToUnpair(int slot, CoopSaveMarker? marker, ClientPlayerData requester) {
        if (marker != null && IsPartner(requester, marker)) {
            RemoveLocalPairing(slot);
        }

        Send(new CoopSaveUpdate { TargetId = requester.Id, Kind = CoopSaveUpdateKind.Unpaired });
        Chat($"Your two-player save with {requester.Username} is a normal save again, for both of you.");
    }

    /// <summary>
    /// The partner made the two-player save a normal save again, which goes for the local save too.
    /// </summary>
    private void OnUnpaired(ClientPlayerData player) {
        var slot = _sentUnpairRequest is { } sent && sent.PlayerId == player.Id
            ? sent.Slot
            : IsInGame()
                ? global::GameManager.instance.profileID
                : -1;
        if (_sentUnpairRequest?.PlayerId == player.Id) {
            _sentUnpairRequest = null;
        }

        var marker = GetMarker(slot);
        if (marker == null || !IsPartner(player, marker)) {
            return;
        }

        RemoveLocalPairing(slot);
        Logger.Info($"{player.Username} unpaired the two-player save");
        Chat($"{player.Username} made your two-player save a normal save again, for both of you.");
    }

    /// <summary>
    /// Removes the pairing of a local save, and lets the player move if it is the loaded save.
    /// </summary>
    private void RemoveLocalPairing(int slot) {
        RemoveMarker(slot);
        if (slot == _sessionSlot) {
            ReleaseHold(HeroController.instance);
            ResetSession(false);
        }

        Logger.Info($"Save slot {slot} isn't paired anymore");
    }

    /// <summary>
    /// Forgets the requests about pairing saves with a player who left.
    /// </summary>
    private void ForgetRequestsOf(ushort id) {
        if (_sentPairRequest?.PlayerId == id) {
            _sentPairRequest = null;
        }

        if (_receivedPairRequest?.PlayerId == id) {
            _receivedPairRequest = null;
        }

        if (_acceptedPairRequest?.PlayerId == id) {
            _acceptedPairRequest = null;
        }

        if (_sentUnpairRequest?.PlayerId == id) {
            _sentUnpairRequest = null;
        }

        if (_receivedUnpairRequest?.PlayerId == id) {
            _receivedUnpairRequest = null;
        }
    }

    /// <summary>
    /// A random ID for a request that isn't 0.
    /// </summary>
    private static ulong NewRequestId() {
        var bytes = new byte[8];
        Random.NextBytes(bytes);
        var id = BitConverter.ToUInt64(bytes, 0);
        return id == 0 ? 1 : id;
    }

    #endregion

    #region Save menu

    /// <summary>
    /// Keeps a two-player save closed in the save menu unless its partner is on the server.
    /// </summary>
    private void OnSaveSlotSubmit(
        Action<UnityEngine.UI.SaveSlotButton, BaseEventData> orig,
        UnityEngine.UI.SaveSlotButton self,
        BaseEventData eventData
    ) {
        if (!_bypassSubmit) {
            try {
                var marker = GetMarker(self.SaveSlotIndex);
                if (marker != null && IsEmptySlot(self)) {
                    // The save of a paired slot is gone, like after its files were deleted, so a new game there starts
                    // as a normal save
                    RemoveMarker(self.SaveSlotIndex);
                } else if (marker != null && HasSave(self) && !TryOpen(self, eventData, marker)) {
                    return;
                }
            } catch (Exception e) {
                Logger.Error($"Could not check whether the save is a two-player save:\n{e}");
            }
        }

        orig(self, eventData);
    }

    /// <summary>
    /// Whether a two-player save opens now, which it only does while its partner is on the server. A host opens their
    /// game first and waits in the menu, and the save loads once the partner has joined.
    /// </summary>
    private bool TryOpen(UnityEngine.UI.SaveSlotButton button, BaseEventData eventData, CoopSaveMarker marker) {
        if (_netClient.IsConnected && FindPartner(marker) != null) {
            return true;
        }

        if (_uiManager.IsSelectingHostSave) {
            if (!_uiManager.StartHostBeforeSave()) {
                ShowMessage($"Could not open your game for {marker.PartnerName}.");
                return false;
            }

            _menuActionToken++;
            _waitingButton = button;
            _waitingEventData = eventData;
            _waitingMarker = marker;
            Logger.Info(
                $"Hosting before two-player save in slot {button.SaveSlotIndex} loads, waiting for {marker.PartnerName}"
            );
            ShowMessage(
                $"Your game is open. Your two-player save with {marker.PartnerName} loads once they have joined. " +
                "Closing this message stops hosting.",
                OnWaitMessageClosed
            );
            return false;
        }

        ShowMessage(
            _netClient.IsConnected
                ? $"This is a two-player save with {marker.PartnerName}, who isn't on this server."
                : $"This is a two-player save with {marker.PartnerName}. Host a game and wait for them to join, or " +
                  "join their game, to play it."
        );
        return false;
    }

    /// <summary>
    /// The host closed the message while their save waited for the partner, so they stop hosting.
    /// </summary>
    private void OnWaitMessageClosed() {
        if (_waitingButton == null) {
            return;
        }

        ClearWait();
        _uiManager.StopHostBeforeSave();
        Logger.Info("Stopped hosting before the partner of the two-player save joined");
    }

    /// <summary>
    /// Hosting for the save that waits in the menu stopped before the partner joined, because the connection failed or
    /// was lost, so the save stops waiting and the host hears why.
    /// </summary>
    private void OnHostBeforeSaveStopped() {
        _menuActionToken++;
        var marker = _waitingMarker;
        if (marker == null) {
            return;
        }

        ClearWait();
        CloseMessage();
        Logger.Info("Hosting stopped before the partner of the two-player save joined");

        var token = _menuActionToken;
        RunInMenuLater(() => {
            if (token == _menuActionToken) {
                ShowMessage($"Your game closed before {marker.PartnerName} joined. Choose the save again to host again.");
            }
        });
    }

    /// <summary>
    /// The save menu of hosting closed without a save, like when Back was pressed, so a save that waits there stops
    /// waiting.
    /// </summary>
    private void OnHostSaveSelectionClosed() {
        _menuActionToken++;
        if (_waitingMarker == null) {
            return;
        }

        ClearWait();
        CloseMessage();
    }

    /// <summary>
    /// The partner is on the server, so the save that waited in the menu loads, a few frames after its message closed.
    /// </summary>
    private void LoadWaitingSave() {
        var button = _waitingButton;
        var eventData = _waitingEventData;
        ClearWait();
        if (button == null || !_uiManager.IsSelectingHostSave) {
            return;
        }

        CloseMessage();
        Logger.Info("The partner joined, loading the two-player save");

        var token = ++_menuActionToken;
        RunInMenuLater(() => {
            if (token != _menuActionToken || !_uiManager.IsSelectingHostSave || button == null) {
                return;
            }

            _bypassSubmit = true;
            try {
                button.OnSubmit(eventData!);
            } finally {
                _bypassSubmit = false;
            }
        });
    }

    private void ClearWait() {
        _waitingButton = null;
        _waitingEventData = null;
        _waitingMarker = null;
    }

    /// <summary>
    /// Runs an action in the menu after a few frames, so the menu takes input again after a message box closed.
    /// </summary>
    private static void RunInMenuLater(Action action) {
        var gameManager = global::GameManager.instance;
        if (gameManager == null) {
            action();
            return;
        }

        gameManager.StartCoroutine(RunAfterFrames(action));
    }

    private static IEnumerator RunAfterFrames(Action action) {
        for (var i = 0; i < MenuDelayFrames; i++) {
            yield return null;
        }

        try {
            action();
        } catch (Exception e) {
            Logger.Error($"Could not finish an action of the two-player save in the menu:\n{e}");
        }
    }

    /// <summary>
    /// Whether a save slot button shows a save rather than an empty or broken slot.
    /// </summary>
    private bool HasSave(UnityEngine.UI.SaveSlotButton button) {
        return _saveFileStateField?.GetValue(button) is { } state && Convert.ToInt32(state) == LoadedStatsState;
    }

    /// <summary>
    /// Whether a save slot button shows an empty slot, rather than a save, a broken save or one that still loads.
    /// </summary>
    private bool IsEmptySlot(UnityEngine.UI.SaveSlotButton button) {
        return _saveFileStateField?.GetValue(button)?.ToString() == EmptyStateName;
    }

    /// <summary>
    /// Shows the game's message box with a text about two-player saves.
    /// </summary>
    private void ShowMessage(string text, Action? onClose = null) {
        _messageText = text;
        GenericMessageCanvas.Show(MessageKey, onClose);
    }

    /// <summary>
    /// Closes the game's message box the way its button does, which also gives input back to the menu.
    /// </summary>
    private static void CloseMessage() {
        var canvas = typeof(GenericMessageCanvas).GetFields(StaticFlags)
            .FirstOrDefault(field => field.FieldType == typeof(GenericMessageCanvas))?
            .GetValue(null) as GenericMessageCanvas;
        if (canvas == null) {
            canvas = UnityEngine.Object.FindAnyObjectByType<GenericMessageCanvas>();
        }

        if (canvas == null) {
            return;
        }

        var close = typeof(GenericMessageCanvas).GetMethod("OkButtonClicked", InstanceFlags, null, Type.EmptyTypes, null) ??
                    typeof(GenericMessageCanvas).GetMethod("Hide", InstanceFlags, null, Type.EmptyTypes, null);
        close?.Invoke(canvas, null);
    }

    /// <summary>
    /// Forgets the pairing of a save that the player cleared.
    /// </summary>
    private void OnClearSaveFile(
        Action<global::GameManager, int, Action<bool>> orig,
        global::GameManager self,
        int slot,
        Action<bool> callback
    ) {
        orig(self, slot, callback);

        if (RemoveMarker(slot)) {
            Logger.Info($"Save slot {slot} was cleared, so it isn't a two-player save anymore");
        }
    }

    private bool? OnLanguageHas(string key, string sheet) {
        return sheet == MessageSheet && key.StartsWith(MessageKey + "_", StringComparison.Ordinal) ? true : null;
    }

    private string? OnLanguageGet(string key, string sheet) {
        if (sheet != MessageSheet) {
            return null;
        }

        return key == MessageKey + "_TITLE" ? "Two-player save" : key == MessageKey + "_DESC" ? _messageText : null;
    }

    #endregion

    #region Markers

    /// <summary>
    /// Whether the local player is in a save rather than in a menu.
    /// </summary>
    private static bool IsInGame() {
        return HeroController.instance != null && global::GameManager.instance != null &&
               !SceneUtil.IsNonGameplayScene(SceneUtil.GetCurrentSceneName());
    }

    /// <summary>
    /// The pairing of the loaded save, or null if no save is loaded or it isn't paired.
    /// </summary>
    private CoopSaveMarker? GetCurrentMarker() {
        return IsInGame() ? GetMarker(global::GameManager.instance.profileID) : null;
    }

    /// <summary>
    /// Whether a player is the partner of a paired save: they have its save key, or its partner's name, because a
    /// partner who plays on another computer has another save key.
    /// </summary>
    private static bool IsPartner(ClientPlayerData player, CoopSaveMarker marker) {
        return (player.SaveKey.Length > 0 && player.SaveKey == marker.PartnerKey) ||
               (marker.PartnerName.Length > 0 && player.Username == marker.PartnerName);
    }

    /// <summary>
    /// The connected partner of a paired save, preferring a player with its save key, or null.
    /// </summary>
    private ClientPlayerData? FindPartner(CoopSaveMarker marker) {
        return _playerData.Values.FirstOrDefault(player =>
                   player.SaveKey.Length > 0 && player.SaveKey == marker.PartnerKey
               ) ??
               _playerData.Values.FirstOrDefault(player => IsPartner(player, marker));
    }

    private CoopSaveMarker? GetMarker(int slot) {
        return slot > 0 && GetMarkers().Slots.TryGetValue(GetSlotKey(slot), out var marker) ? marker : null;
    }

    private bool RemoveMarker(int slot) {
        if (slot <= 0 || !GetMarkers().Slots.Remove(GetSlotKey(slot))) {
            return false;
        }

        SaveMarkers();
        return true;
    }

    private CoopSaveMarkers GetMarkers() {
        if (_markers != null) {
            return _markers;
        }

        var path = Path.Combine(FileUtil.GetConfigPath(), MarkersFileName);
        _markers = File.Exists(path) ? FileUtil.LoadObjectFromJsonFile<CoopSaveMarkers>(path) : null;
        _markers ??= new CoopSaveMarkers();
        return _markers;
    }

    private void SaveMarkers() {
        var folder = FileUtil.GetConfigPath();
        Directory.CreateDirectory(folder);
        FileUtil.WriteObjectToJsonFile(GetMarkers(), Path.Combine(folder, MarkersFileName));
    }

    /// <summary>
    /// The key of a save slot in the markers: the folder of the game's save files, which is different for each account,
    /// and the slot.
    /// </summary>
    private static string GetSlotKey(int slot) => $"{GetSaveFolderName()}/{slot}";

    /// <summary>
    /// The name of the folder of the save files once it is known, because the updates of every frame need it.
    /// </summary>
    private static string? _saveFolderName;

    /// <summary>
    /// The name of the folder that the game keeps the save files of the current account in.
    /// </summary>
    private static string GetSaveFolderName() {
        if (_saveFolderName != null) {
            return _saveFolderName;
        }

        var folder = GetSaveFolder();
        return string.IsNullOrEmpty(folder)
            ? "default"
            : _saveFolderName = Path.GetFileName(folder!.TrimEnd('/', '\\'));
    }

    /// <summary>
    /// The folder that the game keeps the save files of the current account in, or null if it isn't known.
    /// </summary>
    private static string? GetSaveFolder() {
        var platform = Platform.Current;
        return platform == null
            ? null
            : platform.GetType().GetField("saveDirPath", InstanceFlags)?.GetValue(platform) as string;
    }

    #endregion

    private static void Chat(string message) => UiManager.InternalChatBox.AddMessage(message);

    /// <summary>
    /// Sends an update of a two-player save to another player.
    /// </summary>
    private void Send(CoopSaveUpdate update) {
        if (update.Kind is CoopSaveUpdateKind.WorldChange or CoopSaveUpdateKind.WishChange or
            CoopSaveUpdateKind.Interaction or CoopSaveUpdateKind.WishTurnIn) {
            update.Sequence = NextChangeSequence();
            StampLocalChanges(update);
        } else if (update is { Kind: CoopSaveUpdateKind.DeliveryBreak, PartCount: DeliveryBreakReport }) {
            // A break doesn't count as the last change of its wish, so that a delivery of the partner that crossed it
            // still counts for the local player
            update.Sequence = NextChangeSequence();
        }

        if (_netClient.IsConnected) {
            _netClient.UpdateManager.SetCoopSaveUpdate(update);
        }
    }
}
