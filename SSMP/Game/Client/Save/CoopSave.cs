using System;
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
    /// How long, in seconds, a request to pair saves stays open.
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
    /// The name of the FSM of benches that seats the hero.
    /// </summary>
    private const string BenchFsmName = "Bench Control";

    /// <summary>
    /// The state of the bench FSM while the hero sits.
    /// </summary>
    private const string BenchRestingStateName = "Resting";

    /// <summary>
    /// The events that make the hero get up from a bench, which wait while the save waits for the partner.
    /// </summary>
    private static readonly HashSet<string> GetUpEventNames = ["GET UP", "GET LEFT", "GET RIGHT"];

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
    /// The ID of the partner that the saves were last checked with while they stay connected, or null.
    /// </summary>
    private ushort? _checkedWith;

    /// <summary>
    /// Whether updating the two-player save threw, which is only logged once.
    /// </summary>
    private bool _updateFailed;

    /// <summary>
    /// The player that the local player asked to pair saves with, and when.
    /// </summary>
    private ushort? _pairRequestTo;

    private float _pairRequestToTime;

    /// <summary>
    /// The player that asked the local player to pair saves, when, and the bosses that their save has beaten.
    /// </summary>
    private ushort? _pairRequestFrom;

    private float _pairRequestFromTime;

    private List<string> _pairRequestFromDefeats = [];

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
    /// The save key of the local player.
    /// </summary>
    private string LocalKey => AuthUtil.GetSaveKey(_modSettings.AuthKey);

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

        EventHooks.LanguageHas += OnLanguageHas;
        EventHooks.LanguageGet += OnLanguageGet;
        EventHooks.HeroControllerUpdate += OnHeroControllerUpdate;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
        _uiManager.HostBeforeSaveStoppedEvent += OnHostBeforeSaveStopped;
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
    }

    /// <summary>
    /// Holds or releases the local player and moves the check along for the loaded save.
    /// </summary>
    private void UpdateSession(HeroController hero) {
        var gameManager = global::GameManager.instance;
        if (gameManager == null || PlayerData.instance == null) {
            return;
        }

        var slot = gameManager.profileID;
        if (slot != _sessionSlot) {
            ResetSession();
            _sessionSlot = slot;
        }

        var marker = GetMarker(slot);
        if (marker == null) {
            ReleaseHold(hero);
            return;
        }

        var partner = FindPartner(marker);
        if (partner != null && _checkedWith != partner.Id) {
            UpdateCheck(marker, partner);
        }

        if (partner != null && _checkedWith == partner.Id) {
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
    private void OnProcessEvent(Action<Fsm, FsmEvent, FsmEventData> orig, Fsm self, FsmEvent fsmEvent, FsmEventData eventData) {
        if (_held && fsmEvent != null && GetUpEventNames.Contains(fsmEvent.Name) && self.Name == BenchFsmName &&
            self.ActiveStateName == BenchRestingStateName) {
            return;
        }

        orig(self, fsmEvent!, eventData);
    }

    /// <summary>
    /// Forgets everything about the loaded save, for when another save loads or the player goes to the menu.
    /// </summary>
    private void ResetSession() {
        _held = false;
        _tookControl = false;
        _everChecked = false;
        _checkedWith = null;
        ResetCheck();
    }

    /// <summary>
    /// Forgets the state of the session when the game goes to the menu.
    /// </summary>
    private void OnActiveSceneChanged(Scene oldScene, Scene newScene) {
        if (SceneUtil.IsNonGameplayScene(newScene.name)) {
            ResetSession();
            _sessionSlot = -1;
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

        ResetCheck();
        Chat($"{player.Username} is here. Your two-player save continues once they have loaded theirs.");
    }

    /// <summary>
    /// Called when a player disconnects, after which the loaded save waits for them again if they are its partner.
    /// </summary>
    public void OnPlayerDisconnect(ushort id) {
        if (_pairRequestTo == id) {
            _pairRequestTo = null;
        }

        if (_pairRequestFrom == id) {
            _pairRequestFrom = null;
        }

        if (_checkPartnerId != id && _checkedWith != id) {
            return;
        }

        var wasChecked = _checkedWith == id;
        _checkedWith = null;
        ResetCheck();

        var marker = GetCurrentMarker();
        if (wasChecked && marker != null) {
            Chat($"{marker.PartnerName} left. Your two-player save waits for them at the next bench you sit on.");
        }
    }

    /// <summary>
    /// Called when the local player disconnects from the server.
    /// </summary>
    public void OnLocalDisconnect() {
        _pairRequestTo = null;
        _pairRequestFrom = null;
        _checkedWith = null;
        ResetCheck();

        if (_waitingMarker != null) {
            OnHostBeforeSaveStopped();
            _uiManager.StopHostBeforeSave();
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
                OnPairAccept(player);
                break;
            case CoopSaveUpdateKind.PairRefused:
                OnPairRefused(player, update);
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
        }
    }

    #endregion

    #region Pairing

    /// <summary>
    /// Runs /coopsave: asks the other player to pair the current saves, agrees to their request, or with "off" makes the
    /// two-player save a normal save again.
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

        // A request is answered even if the local save is already paired with the other player, because their save may
        // have lost its pairing, like after they moved to another computer
        if (_pairRequestFrom == other.Id && Time.unscaledTime - _pairRequestFromTime < PairRequestTime) {
            _pairRequestFrom = null;
            TryPair(slot, other, _pairRequestFromDefeats);
            return;
        }

        if (marker != null && marker.PartnerKey == other.SaveKey) {
            Chat($"Your current save is already a two-player save with {other.Username}.");
            return;
        }

        _pairRequestTo = other.Id;
        _pairRequestToTime = Time.unscaledTime;
        Send(new CoopSaveUpdate {
            TargetId = other.Id,
            Kind = CoopSaveUpdateKind.PairRequest,
            Records = GetDefeatRecords()
        });
        Chat(
            $"Asked {other.Username} to pair your current saves as a two-player save, which neither of you can play " +
            "alone. They need to type /coopsave too."
        );
    }

    /// <summary>
    /// Another player asks to pair saves. If the local player asked them too, the saves are paired right away.
    /// </summary>
    private void OnPairRequest(ClientPlayerData player, CoopSaveUpdate update) {
        _pairRequestFrom = player.Id;
        _pairRequestFromTime = Time.unscaledTime;
        _pairRequestFromDefeats = update.Records;

        if (_pairRequestTo == player.Id && Time.unscaledTime - _pairRequestToTime < PairRequestTime && IsInGame()) {
            _pairRequestTo = null;
            _pairRequestFrom = null;
            TryPair(global::GameManager.instance.profileID, player, update.Records);
            return;
        }

        Chat(
            $"{player.Username} wants to pair your current saves as a two-player save, which neither of you can play " +
            "alone. Type /coopsave to agree."
        );
    }

    /// <summary>
    /// Pairs the save in a slot with the save of another player if both saves have beaten the same bosses. Otherwise the
    /// saves would differ in which bosses are still there, and one player would miss a boss and its reward.
    /// </summary>
    private void TryPair(int slot, ClientPlayerData player, List<string> partnerDefeats) {
        var localDefeats = GetDefeatRecords();
        var onlyPartner = partnerDefeats.Except(localDefeats).ToList();
        var onlyLocal = localDefeats.Except(partnerDefeats).ToList();
        if (onlyPartner.Count > 0 || onlyLocal.Count > 0) {
            Logger.Info(
                $"Not pairing with {player.Username}, beaten bosses differ. Only theirs: {string.Join(", ", onlyPartner)}; " +
                $"only local: {string.Join(", ", onlyLocal)}"
            );
            Chat(GetRefusedMessage(player.Username, onlyPartner.Count, onlyLocal.Count));
            Send(new CoopSaveUpdate {
                TargetId = player.Id,
                Kind = CoopSaveUpdateKind.PairRefused,
                Records = localDefeats
            });
            return;
        }

        Pair(slot, player);
        Send(new CoopSaveUpdate { TargetId = player.Id, Kind = CoopSaveUpdateKind.PairAccept });
    }

    /// <summary>
    /// Another player agreed to the request of the local player and paired their save, so the local save is paired too.
    /// </summary>
    private void OnPairAccept(ClientPlayerData player) {
        if (_pairRequestTo != player.Id || Time.unscaledTime - _pairRequestToTime >= PairRequestTime || !IsInGame()) {
            return;
        }

        _pairRequestTo = null;
        Pair(global::GameManager.instance.profileID, player);
    }

    /// <summary>
    /// Another player couldn't pair saves with the local player, because their saves have beaten different bosses.
    /// </summary>
    private void OnPairRefused(ClientPlayerData player, CoopSaveUpdate update) {
        if (_pairRequestTo != player.Id) {
            return;
        }

        _pairRequestTo = null;
        var localDefeats = GetDefeatRecords();
        Chat(GetRefusedMessage(
            player.Username,
            update.Records.Except(localDefeats).Count(),
            localDefeats.Except(update.Records).Count()
        ));
    }

    private static string GetRefusedMessage(string partnerName, int onlyPartner, int onlyLocal) {
        return $"These saves can't become a two-player save, because they have beaten different bosses: {partnerName} " +
               $"beat {onlyPartner} that you haven't, and you beat {onlyLocal} that they haven't. A two-player save " +
               "needs the same bosses beaten in both saves, like two new games.";
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
    /// Makes the two-player save in a slot a normal save again, for both players. Both need to be in it, so that it
    /// can't be used to play the save alone.
    /// </summary>
    private void Unpair(int slot, CoopSaveMarker? marker) {
        if (marker == null) {
            Chat("Your current save isn't a two-player save.");
            return;
        }

        var partner = FindPartner(marker);
        if (partner == null || _checkedWith != partner.Id) {
            Chat(
                $"A two-player save only becomes a normal save again while {marker.PartnerName} is here and has " +
                "loaded it too."
            );
            return;
        }

        RemoveMarker(slot);
        ReleaseHold(HeroController.instance);
        ResetSession();

        Logger.Info($"Save slot {slot} isn't paired anymore");
        Chat($"Your two-player save with {partner.Username} is a normal save again, for both of you.");
        Send(new CoopSaveUpdate { TargetId = partner.Id, Kind = CoopSaveUpdateKind.Unpaired });
    }

    /// <summary>
    /// The partner made the two-player save a normal save again, which goes for the local save too.
    /// </summary>
    private void OnUnpaired(ClientPlayerData player) {
        var marker = GetCurrentMarker();
        if (marker == null || !IsPartner(player, marker)) {
            return;
        }

        RemoveMarker(global::GameManager.instance.profileID);
        ReleaseHold(HeroController.instance);
        ResetSession();

        Logger.Info($"{player.Username} unpaired the two-player save");
        Chat($"{player.Username} made your two-player save a normal save again, for both of you.");
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

            _waitingButton = button;
            _waitingEventData = eventData;
            _waitingMarker = marker;
            Logger.Info($"Hosting before two-player save in slot {button.SaveSlotIndex} loads, waiting for {marker.PartnerName}");
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
    /// was lost, so the save stops waiting.
    /// </summary>
    private void OnHostBeforeSaveStopped() {
        var marker = _waitingMarker;
        if (marker == null) {
            return;
        }

        ClearWait();
        CloseMessage();
        Logger.Info("Hosting stopped before the partner of the two-player save joined");
        ShowMessage($"Your game closed before {marker.PartnerName} joined. Choose the save again to host again.");
    }

    /// <summary>
    /// The partner joined the host, so the save that waited in the menu loads.
    /// </summary>
    private void LoadWaitingSave() {
        var button = _waitingButton;
        var eventData = _waitingEventData;
        ClearWait();
        CloseMessage();

        if (button == null) {
            return;
        }

        Logger.Info("The partner joined, loading the two-player save");
        _bypassSubmit = true;
        try {
            button.OnSubmit(eventData!);
        } finally {
            _bypassSubmit = false;
        }
    }

    private void ClearWait() {
        _waitingButton = null;
        _waitingEventData = null;
        _waitingMarker = null;
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
        if (!GetMarkers().Slots.Remove(GetSlotKey(slot))) {
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
        if (_netClient.IsConnected) {
            _netClient.UpdateManager.SetCoopSaveUpdate(update);
        }
    }
}
