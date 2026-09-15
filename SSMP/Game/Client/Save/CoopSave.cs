using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GlobalEnums;
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

/// <summary>
/// Two-player saves. Two players pair the saves they play with /coopsave, and from then on a paired save only plays
/// while its partner is online. The save menu doesn't open it without the partner, and a player who loads it anyway,
/// like the host before anyone joined, can't move until the partner has loaded their save. Each time both are in, both
/// saves are backed up and each gets the world progress of the other save that it lacks: defeat and encounter records,
/// and saved objects of the world like broken walls, pulled levers and paid tolls. What a player owns, like money,
/// items and upgrades, stays with them. A player whose partner leaves waits at the next bench they sit on.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// Binding flags for instance members of the game.
    /// </summary>
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

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
    /// The key of the text of the message box that says a two-player save can't be opened, without the "_TITLE" or
    /// "_DESC" that the message box adds.
    /// </summary>
    private const string LockedMessageKey = "SSMP_COOP_SAVE_LOCKED";

    /// <summary>
    /// The value of SaveSlotButton.SaveFileStates for a slot with a save in it.
    /// </summary>
    private const int LoadedStatsState = 3;

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
    /// The UI manager, which knows whether the save menu is open for hosting.
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
    /// The field of a save slot button that says whether the slot has a save.
    /// </summary>
    private FieldInfo? _saveFileStateField;

    /// <summary>
    /// The text of the message box that says why a two-player save can't be opened.
    /// </summary>
    private string _lockedMessage = "";

    /// <summary>
    /// The slot of the save that the state of the session belongs to, or -1 outside a save.
    /// </summary>
    private int _sessionSlot = -1;

    /// <summary>
    /// Whether the local player can't move because their two-player save waits for the partner.
    /// </summary>
    private bool _held;

    /// <summary>
    /// Whether this class took control from the hero while holding them, so it gives control back.
    /// </summary>
    private bool _tookControl;

    /// <summary>
    /// Whether both saves were checked since the save was loaded, after which a missing partner only holds the player
    /// at a bench.
    /// </summary>
    private bool _everChecked;

    /// <summary>
    /// The ID of the partner that the saves were last checked with while they stay connected, or null.
    /// </summary>
    private ushort? _checkedWith;

    /// <summary>
    /// The player that the local player asked to pair saves with, and when.
    /// </summary>
    private ushort? _pairRequestTo;

    private float _pairRequestToTime;

    /// <summary>
    /// The player that asked the local player to pair saves, and when.
    /// </summary>
    private ushort? _pairRequestFrom;

    private float _pairRequestFromTime;

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

        EventHooks.LanguageHas += OnLanguageHas;
        EventHooks.LanguageGet += OnLanguageGet;
        EventHooks.HeroControllerUpdate += OnHeroControllerUpdate;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
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
    /// Keeps the local player from moving while their two-player save waits for the partner, and checks both saves once
    /// the partner is in.
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
    /// Whether updating the two-player save threw, which is only logged once.
    /// </summary>
    private bool _updateFailed;

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
    /// Takes control from the hero while their two-player save waits for the partner.
    /// </summary>
    private void Hold(HeroController hero, CoopSaveMarker marker, ClientPlayerData? partner) {
        var gameManager = global::GameManager.instance;
        if (gameManager.GameState != GameState.PLAYING || gameManager.IsInSceneTransition || hero.cState.dead ||
            hero.cState.transitioning) {
            return;
        }

        if (!_held) {
            _held = true;
            Logger.Info($"Two-player save waits for {marker.PartnerName}");
            UiManager.InternalChatBox.AddMessage(
                partner == null
                    ? $"This is a two-player save with {marker.PartnerName}. You can play once they are on the " +
                      "server and have loaded their save. To make it a normal save again, type /coopsave off."
                    : $"Waiting for {partner.Username} to load your two-player save."
            );
        }

        // The game can give control back itself, like when the hero gets up from a bench
        if (!hero.controlReqlinquished) {
            hero.RelinquishControl();
            _tookControl = true;
        }
    }

    /// <summary>
    /// Gives control back to the hero if their two-player save was waiting.
    /// </summary>
    private void ReleaseHold(HeroController? hero) {
        if (!_held) {
            return;
        }

        _held = false;
        if (_tookControl && hero != null && hero.controlReqlinquished) {
            hero.RegainControl();
        }

        _tookControl = false;
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
    /// Called when a player connects, which starts a new check if they are the partner of the loaded save.
    /// </summary>
    public void OnPlayerConnect(ClientPlayerData player) {
        var marker = GetCurrentMarker();
        if (marker == null || player.SaveKey.Length == 0 || player.SaveKey != marker.PartnerKey) {
            return;
        }

        ResetCheck();
        UiManager.InternalChatBox.AddMessage(
            $"{player.Username} is here. Your two-player save continues once they have loaded theirs."
        );
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
            UiManager.InternalChatBox.AddMessage(
                $"{marker.PartnerName} left. Your two-player save waits for them at the next bench you sit on."
            );
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
                OnPairRequest(player);
                break;
            case CoopSaveUpdateKind.PairAccept:
                OnPairAccept(player);
                break;
            case CoopSaveUpdateKind.Unpaired:
                UiManager.InternalChatBox.AddMessage(
                    $"{player.Username} made their save a normal save again, so it isn't paired with yours anymore."
                );
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
    /// current save a normal save again.
    /// </summary>
    public void OnCommand(string[] arguments) {
        if (!IsInGame()) {
            UiManager.InternalChatBox.AddMessage("Load a save first.");
            return;
        }

        var slot = global::GameManager.instance.profileID;
        var marker = GetMarker(slot);

        if (arguments.Length > 1 && arguments[1].Equals("off", StringComparison.OrdinalIgnoreCase)) {
            Unpair(slot, marker);
            return;
        }

        if (!_netClient.IsConnected) {
            UiManager.InternalChatBox.AddMessage("Connect to your teammate first to pair your saves.");
            return;
        }

        if (_playerData.Count != 1) {
            UiManager.InternalChatBox.AddMessage(
                "A two-player save needs you and exactly one other player on the server."
            );
            return;
        }

        var other = _playerData.Values.First();
        if (other.SaveKey.Length == 0) {
            UiManager.InternalChatBox.AddMessage($"The game of {other.Username} doesn't support two-player saves.");
            return;
        }

        // A request is answered even if the local save is already paired with the other player, because their save may
        // have lost its pairing, like after they installed the mod again and got a new save key
        if (_pairRequestFrom == other.Id && Time.unscaledTime - _pairRequestFromTime < PairRequestTime) {
            _pairRequestFrom = null;
            Pair(slot, other);
            Send(new CoopSaveUpdate { TargetId = other.Id, Kind = CoopSaveUpdateKind.PairAccept });
            return;
        }

        if (marker != null && marker.PartnerKey == other.SaveKey) {
            UiManager.InternalChatBox.AddMessage(
                $"Your current save is already a two-player save with {other.Username}."
            );
            return;
        }

        _pairRequestTo = other.Id;
        _pairRequestToTime = Time.unscaledTime;
        Send(new CoopSaveUpdate { TargetId = other.Id, Kind = CoopSaveUpdateKind.PairRequest });
        UiManager.InternalChatBox.AddMessage(
            $"Asked {other.Username} to pair your current saves as a two-player save, which you can only play " +
            "together. They need to type /coopsave too."
        );
    }

    /// <summary>
    /// Another player asks to pair saves. If the local player asked them too, the saves are paired right away.
    /// </summary>
    private void OnPairRequest(ClientPlayerData player) {
        _pairRequestFrom = player.Id;
        _pairRequestFromTime = Time.unscaledTime;

        if (_pairRequestTo == player.Id && Time.unscaledTime - _pairRequestToTime < PairRequestTime && IsInGame()) {
            _pairRequestTo = null;
            _pairRequestFrom = null;
            Pair(global::GameManager.instance.profileID, player);
            Send(new CoopSaveUpdate { TargetId = player.Id, Kind = CoopSaveUpdateKind.PairAccept });
            return;
        }

        UiManager.InternalChatBox.AddMessage(
            $"{player.Username} wants to pair your current saves as a two-player save, which you can only play " +
            "together. Type /coopsave to agree."
        );
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
        UiManager.InternalChatBox.AddMessage(
            previous != null && previous.PartnerKey != player.SaveKey
                ? $"Your current save is now a two-player save with {player.Username} instead of " +
                  $"{previous.PartnerName}. Both saves get backed up and compared now."
                : $"Your current save is now a two-player save with {player.Username}. Both saves get backed up " +
                  "and compared now."
        );
    }

    /// <summary>
    /// Makes the save in a slot a normal save again.
    /// </summary>
    private void Unpair(int slot, CoopSaveMarker? marker) {
        if (marker == null) {
            UiManager.InternalChatBox.AddMessage("Your current save isn't a two-player save.");
            return;
        }

        RemoveMarker(slot);
        ReleaseHold(HeroController.instance);
        ResetSession();

        Logger.Info($"Save slot {slot} isn't paired anymore");
        UiManager.InternalChatBox.AddMessage(
            $"Your current save is a normal save again and isn't paired with {marker.PartnerName} anymore."
        );

        if (FindPartner(marker) is { } partner) {
            Send(new CoopSaveUpdate { TargetId = partner.Id, Kind = CoopSaveUpdateKind.Unpaired });
        }
    }

    #endregion

    #region Save menu

    /// <summary>
    /// Keeps a two-player save closed in the save menu unless its partner can join: the player is on a server with the
    /// partner, or hosts, where the save loads before anyone can join.
    /// </summary>
    private void OnSaveSlotSubmit(
        Action<UnityEngine.UI.SaveSlotButton, BaseEventData> orig,
        UnityEngine.UI.SaveSlotButton self,
        BaseEventData eventData
    ) {
        try {
            var slot = self.SaveSlotIndex;
            var marker = GetMarker(slot);
            if (marker != null && HasSave(self) && !CanOpen(marker, out var reason)) {
                Logger.Info($"Not opening two-player save in slot {slot}: {reason}");
                _lockedMessage = reason;
                GenericMessageCanvas.Show(LockedMessageKey, null);
                return;
            }
        } catch (Exception e) {
            Logger.Error($"Could not check whether the save is a two-player save:\n{e}");
        }

        orig(self, eventData);
    }

    /// <summary>
    /// Whether a save slot button shows a save rather than an empty or broken slot.
    /// </summary>
    private bool HasSave(UnityEngine.UI.SaveSlotButton button) {
        return _saveFileStateField?.GetValue(button) is { } state && Convert.ToInt32(state) == LoadedStatsState;
    }

    /// <summary>
    /// Whether a two-player save can be opened now, and otherwise why not.
    /// </summary>
    private bool CanOpen(CoopSaveMarker marker, out string reason) {
        reason = "";
        if (_uiManager.IsSelectingHostSave || (_netClient.IsConnected && FindPartner(marker) != null)) {
            return true;
        }

        reason = _netClient.IsConnected
            ? $"This is a two-player save with {marker.PartnerName}, who isn't on this server."
            : $"This is a two-player save with {marker.PartnerName}. Host a game or join theirs to play it. To play " +
              "it alone, host a game and type /coopsave off.";
        return false;
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
        return sheet == MessageSheet && key.StartsWith(LockedMessageKey, StringComparison.Ordinal) ? true : null;
    }

    private string? OnLanguageGet(string key, string sheet) {
        if (sheet != MessageSheet) {
            return null;
        }

        return key == LockedMessageKey + "_TITLE" ? "Two-player save" :
            key == LockedMessageKey + "_DESC" ? _lockedMessage : null;
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
    /// The connected partner of a paired save, or null.
    /// </summary>
    private ClientPlayerData? FindPartner(CoopSaveMarker marker) {
        return _playerData.Values.FirstOrDefault(player =>
            player.SaveKey.Length > 0 && player.SaveKey == marker.PartnerKey
        );
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
    /// The name of the folder of the save files once it is known, because the updates of every frame need it.
    /// </summary>
    private static string? _saveFolderName;

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

    /// <summary>
    /// Sends an update of a two-player save to another player.
    /// </summary>
    private void Send(CoopSaveUpdate update) {
        if (_netClient.IsConnected) {
            _netClient.UpdateManager.SetCoopSaveUpdate(update);
        }
    }
}
