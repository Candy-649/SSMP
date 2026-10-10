using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GlobalEnums;
using HutongGames.PlayMaker;
using InControl;
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
/// Shared saves. Players whose saves have beaten the same bosses pair them with /coopsave, two of them or more, and from
/// then on no save of theirs can be played without the others. The save menu only opens a paired save while all its
/// other players are on the server; a host opens their game first, and the save loads once the others have joined. A
/// player whose partners haven't loaded their saves yet, or one of whom has left, waits seated on a bench and can't get
/// up until they are all back. Each time they are all in, every save is backed up and each gets the saved objects of
/// the world that only the others changed, like broken walls, pulled levers and paid tolls. Beaten bosses, arenas and
/// anything else with a reward aren't copied, so nobody misses a reward, and what a player owns, like money, items and
/// upgrades, stays with them. A save of two players is a two-player save, which is what most of this calls them all;
/// "the partner" is any other player of the save.
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
    /// Whether the saves of every member were checked together since the save was loaded, after which a missing member
    /// only holds the player once they sit on a bench.
    /// </summary>
    private bool _everChecked;

    /// <summary>
    /// The IDs of the members that the saves were last checked with while they stay in the save. The save can be played
    /// with every member among them; while one of them is gone, the others keep in step with each other.
    /// </summary>
    private readonly HashSet<ushort> _checkedMembers = [];

    /// <summary>
    /// One member that the saves were checked with, or null: what the parts go by that keep in step with one partner at
    /// a time.
    /// </summary>
    private ushort? _checkedWith => _checkedMembers.Count > 0 ? _checkedMembers.Min() : null;

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
    /// A request between the local player and other players about pairing saves, which stays open for
    /// <see cref="PairRequestTime"/> seconds.
    /// </summary>
    private sealed class PairingRequest {
        /// <summary>
        /// The other player: who asked the local player, who is to confirm, or the first of the players that a request of
        /// the local player went to.
        /// </summary>
        public ushort PlayerId { get; init; }

        /// <summary>
        /// Every other player that a request of the local player went to, who all have to agree, or the players whose
        /// cancel undoes a pairing that went through here.
        /// </summary>
        public List<ushort> PlayerIds { get; init; } = [];

        /// <summary>
        /// The players among <see cref="PlayerIds"/> who agreed so far.
        /// </summary>
        public HashSet<ushort> Accepted { get; } = [];

        /// <summary>
        /// The players that the request is for, with their save keys and usernames: for a request, every player it went
        /// to; for a pairing that went through, the other players it paired with.
        /// </summary>
        public List<CoopSaveMember> Group { get; init; } = [];

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
    /// The ID of one member that the saves were checked with while they are in the save, or null: what the parts go by
    /// that keep in step with one partner at a time.
    /// </summary>
    public ushort? CheckedPartnerId => _checkedWith;

    /// <summary>
    /// The IDs of the members that the saves were checked with while they are in the save.
    /// </summary>
    public IReadOnlyCollection<ushort> CheckedMemberIds => _checkedMembers;

    /// <summary>
    /// Whether the saves were checked with a player while they are in the save.
    /// </summary>
    public bool IsCheckedMember(ushort id) => _checkedMembers.Contains(id);

    /// <summary>
    /// Takes the name of a flag of the player data that the partner's game set and that the local save got from it,
    /// live or in a check.
    /// </summary>
    public Action<string>? OnPartnerFlagSet { get; set; }

    /// <summary>
    /// Takes how many fleas the partner has hit in the game of the festival they are playing.
    /// </summary>
    public Action<ClientPlayerData, CoopSaveUpdate>? OnFleaGameScore { get; set; }

    /// <summary>
    /// Takes whether the partner has won every game of the festival.
    /// </summary>
    public Action<ClientPlayerData, CoopSaveUpdate>? OnFleaGamesOutroReady { get; set; }

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
        RegisterRescueHooks();
        RegisterLavaDeathHooks();
        RegisterArrivalHooks();
        RegisterTrapdoorHooks();
        RegisterPrisonCaptureHooks();
        RegisterCreatureDeathHooks();
        RegisterSlidePlatformHooks();

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

            // A board that waited for the partner's answer when the save stopped being paired still has to open, or
            // the hero would stand in its dialogue for good
            try {
                UpdateWishCopyChecks();
            } catch (Exception e) {
                LogWishTalkError(e);
            }

            return;
        }

        var members = FindMembers(marker);
        if (members.Any(member => !_checkedMembers.Contains(member.Id))) {
            // A check that throws must not keep the hold below from working
            try {
                UpdateCheck(marker, members);
            } catch (Exception e) {
                if (!_checkFailed) {
                    _checkFailed = true;
                    Logger.Error($"Could not check the two-player save:\n{e}");
                }
            }
        }

        // The parts that keep in step with one partner at a time go by a checked member, and by any member on the
        // server where they went by an unchecked partner
        var checkedPartner = members.FirstOrDefault(member => _checkedMembers.Contains(member.Id));
        var partner = checkedPartner ?? members.FirstOrDefault();

        // A boss checkpoint that throws must not keep the hold below from working either
        try {
            UpdateCheckpoint(hero, marker, partner);
        } catch (Exception e) {
            if (!_checkpointFailed) {
                _checkpointFailed = true;
                Logger.Error($"Could not update the boss checkpoint of the two-player save:\n{e}");
            }
        }

        UpdateWishTalk();
        UpdateDeliverySummon(hero);
        UpdateRescue(hero, checkedPartner);
        UpdateLavaChase(hero, checkedPartner);
        UpdateChaseStandUp(hero, checkedPartner);
        UpdateRaces(checkedPartner);
        UpdatePrisonCapture(hero);
        UpdateClothesGrab(hero);

        // Also once the partner is gone, so that a bench they kept up flips back
        UpdateTollBenches();

        // Also once the partner is gone, so that machines waiting for their word go by themselves
        try {
            UpdateMachines();
        } catch (Exception e) {
            LogInteractionError(e);
        }

        // Also while another member is gone, so that the members who are in keep in step with each other
        if (checkedPartner != null) {
            UpdateInteractions();
            UpdateWorldChanges();
            UpdateWishes();
            UpdateStoryFlags();
            UpdateLifts(checkedPartner);
            UpdateTrapdoors();
        }

        if (IsGroupComplete(marker, members)) {
            UpdateCheckedPlayTime(marker);
            ReleaseHold(hero);
            return;
        }

        if (_held || !_everChecked || PlayerData.instance.atBench) {
            Hold(hero, marker, members);
        }
    }

    /// <summary>
    /// Makes the hero wait for the other members: seated on a bench, the bench doesn't let them get up, and anywhere
    /// else they can't move.
    /// </summary>
    /// <param name="hero">The hero.</param>
    /// <param name="marker">The pairing of the loaded save.</param>
    /// <param name="members">The members on the server.</param>
    private void Hold(HeroController hero, CoopSaveMarker marker, List<ClientPlayerData> members) {
        var gameManager = global::GameManager.instance;
        if (gameManager.GameState != GameState.PLAYING || gameManager.IsInSceneTransition || hero.cState.dead ||
            hero.cState.transitioning) {
            return;
        }

        var atBench = PlayerData.instance.atBench;
        if (!_held) {
            _held = true;

            // Members who aren't on the server are who it waits for; with all of them there, the ones still loading
            var absent = JoinNames(
                MatchMembers(marker).Where(match => match.Player == null).Select(match => match.Member.Name)
            );
            var loading = JoinNames(
                members.Where(member => !_checkedMembers.Contains(member.Id)).Select(member => member.Username)
            );
            var allThere = members.Count == marker.Members.Count;
            Logger.Info($"Two-player save waits for {(allThere ? loading : absent)}");
            Chat(
                allThere
                    ? Lang.Pick(
                        $"Waiting for {loading} to load your {SaveWord(marker)}.",
                        $"正在等 {loading} 进入你们的{SaveWord(marker)}。"
                    )
                    : atBench
                        ? Lang.Pick(
                            $"Your {SaveWord(marker)} waits for {absent}. You can't get up until they are back.",
                            $"你们的{SaveWord(marker)}在等 {absent}。他们回来之前你起不来。"
                        )
                        : Lang.Pick(
                            $"Your {SaveWord(marker)} waits for {absent}. You can't move until they are back.",
                            $"你们的{SaveWord(marker)}在等 {absent}。他们回来之前你动不了。"
                        )
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
    /// Keeps a waiting hero seated by holding back the events that make them get up from the bench, and holds the
    /// events of a race that wait for the partner.
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

        if (_raceFsm != null && fsmEvent != null) {
            try {
                if (HoldsRaceEvent(self, fsmEvent)) {
                    return;
                }
            } catch (Exception e) {
                LogRaceError(e);
            }
        }

        if (_clothesGrabHeld != null && fsmEvent != null && HoldsClothesGrab(self, fsmEvent)) {
            return;
        }

        // A toll bench here that the partner keeps up in their game doesn't flip back
        if (_partnerTollBenches.Count > 0 && fsmEvent != null && HoldsTollBenchEvent(self, fsmEvent)) {
            return;
        }

        orig(self, fsmEvent!, eventData);
    }

    /// <summary>
    /// Forgets everything about the loaded save, for when another save loads or the player goes to the menu.
    /// </summary>
    /// <param name="notifyPartner">Whether to tell the partner that the local player left the save.</param>
    private void ResetSession(bool notifyPartner) {
        // Every member of the round, and every member that the saves were checked with, hears it
        if (notifyPartner) {
            foreach (var id in _memberChecks.Keys.Union(_checkedMembers).ToList()) {
                if (_playerData.ContainsKey(id)) {
                    Send(new CoopSaveUpdate { TargetId = id, Kind = CoopSaveUpdateKind.Left, Key = _highestCheckKey });
                }
            }
        }

        // The play time that the players played together is kept for the next check
        if (_checkedMembers.Count > 0) {
            SaveMarkers();
        }

        _held = false;
        _tookControl = false;
        _everChecked = false;

        // A step of dialogue that waits for the partner doesn't go on: its scene is going, and going on would run what
        // comes after the wish, like saving the game, on the way out (CoopSave.WishRead)
        EndHeldBegin(null, false);

        // Before the pairing is forgotten, so that a question of the partner still on screen is answered back to them
        // and a button that waited on them is told it is over. ResetWishTalk below runs it again, which does nothing.
        ResetWishConfirm();
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
        ResetRescue();
        ResetRaces();
        ResetPrisonCapture();
        ResetClothesGrab();
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

        // First, so that none of the others throwing on the way can keep them from running. Forgetting the machines
        // only empties lists, which can't throw.
        ResetMachines();
        GrowBackPustulesThePartnerDrew();
        OnCheckpointSceneChanged(newScene.name);
        ResetInteractions();
        OnWishTalkSceneChanged();
        OnLiftSceneChanged();
        OnDeliverySceneChanged();
        OnRescueSceneChanged();
        OnLavaChaseSceneChanged(newScene);
        OnCageSceneChanged();
        OnClothesGrabSceneChanged();
        OnTollBenchSceneChanged();
        ResetRaces();
    }

    /// <summary>
    /// Called when the local player connected and knows the players that were on the server already. With every member
    /// among them, the save that waits in the menu loads.
    /// </summary>
    public void OnLocalConnect() {
        _highestCheckKey = 0;
        if (_waitingMarker != null && HasAllMembers(_waitingMarker)) {
            LoadWaitingSave();
        }
    }

    /// <summary>
    /// Called when a player connects. The last member of a save that waits in the menu to join makes it load, and a
    /// member of the loaded save comes into its next round.
    /// </summary>
    public void OnPlayerConnect(ClientPlayerData player) {
        // A request to pair the saves of the players on the server is for the players who were there. Going through, it
        // would leave the one who joined out of it, so it is dropped, and whoever agreed to it hears so.
        if (_sentPairRequest is { IsOpen: true } sent) {
            _sentPairRequest = null;
            CancelPairRequest(sent, null);
            Chat(Lang.Pick(
                $"{player.Username} joined, so your request to pair saves was dropped. Ask again to include them.",
                $"{player.Username} 加入了，所以刚才的配对请求作废了。重新问一次就会把对方也算进去。"
            ));
        }

        if (_waitingMarker != null && IsMember(player, _waitingMarker)) {
            if (HasAllMembers(_waitingMarker)) {
                LoadWaitingSave();
            }

            return;
        }

        var marker = GetCurrentMarker();
        if (marker == null || !IsMember(player, marker)) {
            return;
        }

        // The game of the member starts counting rounds anew. Members who are still checked with each other go on as
        // they are, and the hello of the new member starts a new round once their game has loaded the save (OnHello).
        _memberChecks.Remove(player.Id);
        if (_checkedMembers.Count == 0) {
            ResetCheck();
            _highestCheckKey = 0;
        }

        Chat(Lang.Pick(
            $"{player.Username} is here. Your {SaveWord(marker)} continues once they have loaded theirs.",
            $"{player.Username} 来了。等对方也进了自己的存档，你们的{SaveWord(marker)}就能继续。"
        ));
    }

    /// <summary>
    /// Called when a player disconnects, after which the loaded save waits for them again if they are a member of it.
    /// </summary>
    public void OnPlayerDisconnect(ushort id) {
        ForgetRequestsOf(id);

        if (_memberChecks.ContainsKey(id) || _checkedMembers.Contains(id)) {
            PartnerLeft(id, Lang.Pick("left", "离开了"));

            // Members who are still checked with each other keep the keys of their round
            if (_checkedMembers.Count == 0) {
                _highestCheckKey = 0;
            }
        }
    }

    /// <summary>
    /// A member is no longer in the save, so the check with them ends and the save waits for them again. The members who
    /// are still checked with each other keep in step until they sit on a bench; with none of them left, the round ends.
    /// </summary>
    /// <param name="id">The ID of the member.</param>
    /// <param name="how">How they left, for the message, or null for no message.</param>
    private void PartnerLeft(ushort id, string? how) {
        if (!_memberChecks.TryGetValue(id, out var check) && !_checkedMembers.Contains(id)) {
            return;
        }

        var wasChecked = _checkedMembers.Contains(id);
        var name = check?.Name;

        // A step of dialogue that waits for them goes on without the wish, which waits for them to read to it too
        if (_heldBegin is { } heldBegin && heldBegin.HasMember(id)) {
            EndHeldBegin(
                GetPartnerGoneMessage(
                    heldBegin.Members.Find(member => member.Id == id).Name ?? name ?? "?", heldBegin.Members.Count
                ),
                true
            );
        }

        // Before the member is forgotten: a question of theirs still on screen is answered back to them, and a
        // button that waited on them tells them it is over. Afterwards there is nobody left to address either to.
        ResetWishConfirm();
        _checkedMembers.Remove(id);
        _memberChecks.Remove(id);
        if (_checkedMembers.Count == 0) {
            ResetCheck();
        } else {
            // The others are still in the save: only what this member held in the world lets go
            ForgetTrapdoorsOf(id);
            ForgetTollBenchesOf(id);
            ForgetPlatesOf(id);
        }

        // This path does not go through ResetSession, so a cocoon of theirs left standing in the room would stay for
        // good, and this player would be remembered as waiting to be pulled up by someone who is gone - which would
        // quietly stop the next death of the local player from waiting for anyone
        ResetRescue();
        OnClothesGrabPartnerLeft();

        if (wasChecked) {
            // The play time that the players played together is kept for the next check
            SaveMarkers();
        }

        // A boss fight that the member was in ends for the local player too
        if (wasChecked && IsInBossFight()) {
            _interruptPending = true;
        }

        if (wasChecked && how != null && GetCurrentMarker() is { } marker) {
            name ??= marker.PartnerName;
            Chat(Lang.Pick(
                $"{name} {how}. Your {SaveWord(marker)} waits for them at the next bench you sit on.",
                $"{name} {how}。你们的{SaveWord(marker)}会在你下次坐上长椅时等他们。"
            ));
        }
    }

    /// <summary>
    /// Called when the local player disconnects from the server.
    /// </summary>
    public void OnLocalDisconnect() {
        // Leaving the server while the save holds the player has to give them back, or they stay frozen with nothing
        // left that could ever free them: the hold only lifts when a partner checks in, and there is no partner any
        // more. Reloading the save does not help either, since a save whose partner is missing holds them again.
        ReleaseHold(HeroController.instance);

        _sentPairRequest = null;
        _receivedPairRequest = null;
        _acceptedPairRequest = null;
        _confirmedPairRequest = null;
        _sentUnpairRequest = null;
        _receivedUnpairRequest = null;
        ResetCheck();

        // This path does not go through ResetSession either, so the same cocoon and the same memory of a partner
        // waiting would be left behind, this time by leaving the server rather than by them leaving it
        ResetRescue();
        OnClothesGrabPartnerLeft();

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
            case CoopSaveUpdateKind.UnpairAccept:
                OnUnpairAccept(player, update);
                break;
            case CoopSaveUpdateKind.WaitingRoomReady:
                // Handled by the menu rather than here: at this point neither player has a save loaded, so there is
                // nothing about a two-player save to act on yet
                _uiManager.OnPartnerReadyChanged(player.Username, update.Part != 0);
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
            case CoopSaveUpdateKind.WorldTrigger:
                OnWorldTrigger(player, update);
                break;
            case CoopSaveUpdateKind.CageSprung:
                OnCageSprung(player, update);
                break;
            case CoopSaveUpdateKind.LavaDeathEnd:
                OnPartnerLavaDeathEnd(player, update);
                break;
            case CoopSaveUpdateKind.PrisonCapture:
                OnPrisonCapture(player);
                break;
            case CoopSaveUpdateKind.ClothesGrab:
                OnClothesGrab(player, update);
                break;
            case CoopSaveUpdateKind.Trapdoor:
                OnTrapdoor(player, update);
                break;
            case CoopSaveUpdateKind.TollBench:
                OnTollBench(player, update);
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
            case CoopSaveUpdateKind.WishCopyCheck:
                OnWishCopyCheck(player, update);
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
            case CoopSaveUpdateKind.LiftUnlock:
                OnLiftUnlock(player, update);
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
            case CoopSaveUpdateKind.Race:
                OnRace(player, update);
                break;
            case CoopSaveUpdateKind.RescueOffer:
                OnRescueOffer(player, update);
                break;
            case CoopSaveUpdateKind.RescueHit:
                OnRescueHit(player, update);
                break;
            case CoopSaveUpdateKind.RescueEnd:
                OnRescueEnd(player, update);
                break;
            case CoopSaveUpdateKind.RescueLost:
                OnRescueLost(player);
                break;
            case CoopSaveUpdateKind.RescueStoodUp:
                OnRescueStoodUp(player);
                break;
            case CoopSaveUpdateKind.MachineRound:
                OnMachineRound(player, update);
                break;
            case CoopSaveUpdateKind.MachineStop:
                OnMachineStop(player, update);
                break;
            case CoopSaveUpdateKind.MachineGo:
                OnMachineGo(player, update);
                break;
            case CoopSaveUpdateKind.LavaChase:
                OnLavaChase(player, update);
                break;
            case CoopSaveUpdateKind.LavaChaseSetDown:
                OnLavaChaseSetDown(player, update);
                break;
            case CoopSaveUpdateKind.FleaGameScore:
                OnFleaGameScore?.Invoke(player, update);
                break;
            case CoopSaveUpdateKind.FleaGamesOutroReady:
                OnFleaGamesOutroReady?.Invoke(player, update);
                break;
            case CoopSaveUpdateKind.CreatureDeath:
                OnCreatureDeath(player, update);
                break;
            case CoopSaveUpdateKind.SlidePlatform:
                OnSlidePlatform(player, update);
                break;
        }
    }

    #endregion

    #region Pairing

    /// <summary>
    /// The name of the key or button that agrees to a two-player save, for the line that offers one. Taken from the
    /// binding that is actually in force rather than written out, so that rebinding it no longer leaves every prompt
    /// naming a key that does nothing.
    /// </summary>
    private string PairKeyName => PromptKeyName(_modSettings.Keybinds.CoopPair);

    /// <summary>
    /// The name of the key or button that leaves a two-player save waiting for a partner who is not coming, and that
    /// gives up on waiting to be pulled back up after a death. The same as <see cref="PairKeyName"/> otherwise.
    /// </summary>
    private string LeaveKeyName => PromptKeyName(_modSettings.Keybinds.CoopLeave);

    /// <summary>
    /// What a prompt should tell the player to press. Someone holding a gamepad is told the button, because the key
    /// of a keyboard they are not touching is worse than useless to them: it reads as though the way out is a key
    /// they cannot reach. Someone on a keyboard is told the key.
    /// </summary>
    /// <param name="action">The action the prompt is about.</param>
    /// <returns>The name to show.</returns>
    private static string PromptKeyName(PlayerAction action) {
        var binding = action.GetKeyOrMouseBinding();
        var key = InputHandler.KeyOrMouseBinding.IsNone(binding) ? null : binding.Key.ToString();

        var inputHandler = InputHandler.Instance;
        if (inputHandler != null && inputHandler.activeGamepadType != GamepadType.NONE) {
            var button = action.GetControllerButtonBinding();
            if (button != InputControlType.None) {
                // Both of them, because holding a pad does not mean the keyboard has gone away, and a player who
                // cannot find a button on their pad has no way of guessing that a key would have done instead. The
                // one that was shown alone was the stick pressed in, which is the hardest of them all to find.
                return key == null ? ButtonName(button) : $"{key} / {ButtonName(button)}";
            }
        }

        return key ?? "the co-op key";
    }

    /// <summary>
    /// The name a player would recognise for a gamepad button, and for the two that are a stick rather than a button,
    /// what to do with it. Pressing a stick in is called L3 and R3 on the pads someone is likely to be holding, which
    /// is not what the button is called in the game's own list - and the name on its own was still not enough. A pad
    /// of the other make has no button printed L3 at all, and "press the stick down" was taken as pushing it towards
    /// the player: a direction, which is bound to nothing here, so the player pushed and nothing happened.
    /// </summary>
    /// <param name="button">The button.</param>
    /// <returns>The name to show.</returns>
    private static string ButtonName(InputControlType button) => button switch {
        InputControlType.LeftStickButton => Lang.Pick("L3 (click the left stick in)", "L3（把左摇杆按进去）"),
        InputControlType.RightStickButton => Lang.Pick("R3 (click the right stick in)", "R3（把右摇杆按进去）"),
        _ => button.ToString()
    };

    /// <summary>
    /// Whether the gamepad button of an action is down right now on the pad the game is reading, asked of the pad
    /// itself rather than of the action. The action goes quiet while the chat has the keys, so on its own it cannot
    /// tell a press that never reached the game from one that arrived while nothing was listening.
    /// </summary>
    /// <param name="action">The action whose button to ask about.</param>
    /// <param name="device">The name of the pad that was asked.</param>
    /// <returns>Whether that button is down.</returns>
    private static bool IsPadButtonDown(PlayerAction action, out string device) {
        var pad = InputManager.ActiveDevice;
        device = pad.Name;

        var button = action.GetControllerButtonBinding();
        return button != InputControlType.None && pad.GetControl(button).IsPressed;
    }

    /// <summary>
    /// Whether the key that agrees to a two-player save was already down last frame.
    /// </summary>
    private bool _pairKeyHeld;

    /// <summary>
    /// Whether the key that leaves a waiting two-player save was already down last frame, so that holding it asks
    /// once rather than every frame.
    /// </summary>
    private bool _leaveKeyHeld;

    /// <summary>
    /// Whether showing the line that offers a two-player save failed, so it is only logged once.
    /// </summary>
    private bool _pairPromptFailed;

    /// <summary>
    /// Offers a shared save in game, so that pairing takes a key rather than a typed command, and pairs when that key is
    /// pressed. Every player presses it: the first press asks the others, and theirs agree, whichever player it is.
    /// </summary>
    private void UpdatePairPrompt() {
        var prompt = _uiManager.CoopPrompt;

        // A held player owns this line, because being unable to move with no idea why is the worst thing this mod
        // can do to someone. The chat line that the hold writes is invisible to anyone playing with chat closed.
        if (_held && IsInGame()) {
            var heldMarker = GetMarker(global::GameManager.instance.profileID);
            if (heldMarker != null) {
                var waitingFor = GetMissingNames(heldMarker);
                prompt.Show(Lang.Pick(
                    $"Waiting for {waitingFor}. Press {LeaveKeyName} to play this save alone",
                    $"正在等 {waitingFor}。按 {LeaveKeyName} 就一个人玩这个存档"
                ));
            } else {
                prompt.Show(Lang.Pick("Waiting for your teammate", "正在等你的队友"));
            }

            _pairKeyHeld = false;

            // A key rather than the chat command, and the leave itself rather than a call to that command. A held
            // player cannot open the chat to type it, and with the partner connected the command only asks them to
            // agree and leaves the player held exactly where they were - so the line above promised a way out that
            // did not exist. Only this side's pairing goes, like when the partner is not on the server at all.
            var leaveHeld = _modSettings.Keybinds.CoopLeave.IsPressed;
            if (leaveHeld && !_leaveKeyHeld) {
                RemoveLocalPairing(global::GameManager.instance.profileID);
                ReleaseHold(HeroController.instance);
                Chat(
                    heldMarker != null
                        ? GetLeftAloneMessage(heldMarker, false)
                        : Lang.Pick("Your save is a normal save again.", "你的存档变回普通存档了。")
                );
            }

            _leaveKeyHeld = leaveHeld;
            return;
        }

        _leaveKeyHeld = false;

        // A shared save needs the other players on the server, and a save loaded to pair
        if (!_netClient.IsConnected || !IsInGame() || _playerData.Count == 0) {
            prompt.Hide();
            _pairKeyHeld = false;
            return;
        }

        var others = GetOthersOnServer();
        var marker = GetMarker(global::GameManager.instance.profileID);
        if (others.Any(other => other.SaveKey.Length == 0) || (marker != null && IsSameGroup(marker, others))) {
            prompt.Hide();
            _pairKeyHeld = false;
            return;
        }

        if (_sentPairRequest is { IsOpen: true } sent && IsRequestTo(sent, others)) {
            var waiting = JoinNames(
                others.Where(other => !sent.Accepted.Contains(other.Id)).Select(other => other.Username)
            );
            prompt.Show(Lang.Pick(
                $"Waiting for {waiting} to press {PairKeyName} too",
                $"等 {waiting} 也按一下 {PairKeyName}"
            ));
        } else if (_receivedPairRequest is { IsOpen: true } received &&
                   _playerData.TryGetValue(received.PlayerId, out var asker)) {
            prompt.Show(
                received.Group.Count <= 1
                    ? Lang.Pick(
                        $"{asker.Username} wants a two-player save. Press {PairKeyName} to agree",
                        $"{asker.Username} 想和你组双人存档。按 {PairKeyName} 同意"
                    )
                    : Lang.Pick(
                        $"{asker.Username} wants a shared save with all of you. Press {PairKeyName} to agree",
                        $"{asker.Username} 想和大家组多人存档。按 {PairKeyName} 同意"
                    )
            );
        } else if (marker != null && HasAllMembers(marker)) {
            // A save with all of its members here is played as it is, also with someone else on the server, who is
            // offered to share it on their side and asks the members from there
            prompt.Hide();
            _pairKeyHeld = false;
            return;
        } else {
            var names = JoinNames(others.Select(other => other.Username));
            prompt.Show(
                others.Count == 1
                    ? Lang.Pick(
                        $"Press {PairKeyName} to play a two-player save with {names}",
                        $"按 {PairKeyName} 和 {names} 组成双人存档"
                    )
                    : Lang.Pick(
                        $"Press {PairKeyName} to share a save with {names}",
                        $"按 {PairKeyName} 和 {names} 组成多人存档"
                    )
            );
        }

        // Only where the key goes down, or holding it would ask again every frame of the whole press
        var held = _modSettings.Keybinds.CoopPair.IsPressed;
        if (held && !_pairKeyHeld) {
            OnCommand(["/coopsave"]);
        }

        _pairKeyHeld = held;
    }

    /// <summary>
    /// Runs /coopsave: asks every other player on the server to pair the current saves as one shared save, or agrees to
    /// their request, or with "off" makes the shared save a normal save again.
    /// </summary>
    public void OnCommand(string[] arguments) {
        if (!IsInGame()) {
            Chat(Lang.Pick("Load a save first.", "先进一个存档。"));
            return;
        }

        var slot = global::GameManager.instance.profileID;
        var marker = GetMarker(slot);

        if (arguments.Length > 1 && arguments[1].Equals("off", StringComparison.OrdinalIgnoreCase)) {
            Unpair(slot, marker);
            return;
        }

        if (!_netClient.IsConnected) {
            Chat(Lang.Pick(
                "Connect to your teammate first to pair your saves.",
                "先和队友连上，才能配对存档。"
            ));
            return;
        }

        if (_playerData.Count == 0) {
            Chat(Lang.Pick(
                "A two-player save needs another player on the server, and nobody else is on it.",
                "双人存档需要服务器上还有别人，现在只有你一个。"
            ));
            return;
        }

        var others = GetOthersOnServer();
        if (others.FirstOrDefault(other => other.SaveKey.Length == 0) is { } unsupported) {
            Chat(Lang.Pick(
                $"The game of {unsupported.Username} doesn't support two-player saves.",
                $"{unsupported.Username} 的游戏不支持双人存档。"
            ));
            return;
        }

        if (_acceptedPairRequest is { IsOpen: true } accepted &&
            _playerData.TryGetValue(accepted.PlayerId, out var confirmer)) {
            Chat(Lang.Pick(
                $"Waiting for the game of {confirmer.Username} to confirm the pairing.",
                $"正在等 {confirmer.Username} 那边确认配对。"
            ));
            return;
        }

        // A request is answered even if the local save is already paired with these players, because a save of theirs
        // may have lost its pairing, like after they moved to another computer
        if (_receivedPairRequest is { IsOpen: true } received &&
            _playerData.TryGetValue(received.PlayerId, out var requester)) {
            _receivedPairRequest = null;
            AcceptPairRequest(slot, requester, received);
            return;
        }

        var names = JoinNames(others.Select(other => other.Username));
        if (marker != null && IsSameGroup(marker, others)) {
            Chat(Lang.Pick(
                $"Your current save is already a {SaveWord(marker)} with {names}.",
                $"你当前的存档已经是和 {names} 的{SaveWord(marker)}了。"
            ));
            return;
        }

        // A request asked before goes to nobody any more, or whoever agreed to it would wait for it to be confirmed
        if (_sentPairRequest is { IsOpen: true } older) {
            CancelPairRequest(older, null);
        }

        _sentPairRequest = new PairingRequest {
            PlayerId = others[0].Id,
            PlayerIds = others.Select(other => other.Id).ToList(),
            Group = others.Select(other => new CoopSaveMember { Key = other.SaveKey, Name = other.Username }).ToList(),
            Id = NewRequestId(),
            Slot = slot
        };

        var defeats = GetDefeatRecords();
        foreach (var other in others) {
            var request = new CoopSaveUpdate {
                TargetId = other.Id,
                Kind = CoopSaveUpdateKind.PairRequest,
                Key = _sentPairRequest.Id,
                Records = defeats
            };
            WriteGroup(request, _sentPairRequest.Group);
            Send(request);
        }

        Chat(
            others.Count == 1
                ? Lang.Pick(
                    $"Asked {names} to pair your current saves as a two-player save, which neither of you can play " +
                    "alone. They need to type /coopsave too.",
                    $"已经问 {names} 要不要把你们当前的存档配成双人存档——配成之后两个人都不能单独玩。" +
                    "对方也要打一次 /coopsave。"
                )
                : Lang.Pick(
                    $"Asked {names} to pair your current saves as one shared save, which none of you can play alone. " +
                    "Each of them needs to type /coopsave too.",
                    $"已经问 {names} 要不要把大家当前的存档配成多人存档——配成之后谁都不能单独玩。" +
                    "每个人也都要打一次 /coopsave。"
                )
        );
    }

    /// <summary>
    /// Another player asks to pair saves. If the local player asked at the same time, one of the two requests is
    /// accepted right away.
    /// </summary>
    private void OnPairRequest(ClientPlayerData player, CoopSaveUpdate update) {
        var request = new PairingRequest {
            PlayerId = player.Id,
            Id = update.Key,
            Defeats = update.Records,
            Group = ReadGroup(update)
        };

        if (_sentPairRequest is { IsOpen: true } sent && IsInGame() &&
            global::GameManager.instance.profileID == sent.Slot) {
            // The request with the larger ID goes ahead, so the other players accept the request of the local player
            if (update.Key < sent.Id) {
                return;
            }

            // Whoever agreed to the request of the local player already hears that it gave way
            _sentPairRequest = null;
            CancelPairRequest(sent, player.Id);
            AcceptPairRequest(sent.Slot, player, request);
            return;
        }

        // Of the open requests of two other players, the one with the larger ID goes ahead here too
        if (_receivedPairRequest is { IsOpen: true } previous && previous.PlayerId != player.Id &&
            previous.Id > update.Key) {
            return;
        }

        _receivedPairRequest = request;
        Chat(
            request.Group.Count <= 1
                ? Lang.Pick(
                    $"{player.Username} wants to pair your current saves as a two-player save, which neither of you " +
                    "can play alone. Type /coopsave to agree.",
                    $"{player.Username} 想把你们当前的存档配成双人存档——配成之后两个人都不能单独玩。" +
                    "打一次 /coopsave 就是同意。"
                )
                : Lang.Pick(
                    $"{player.Username} wants to pair the current saves of all of you, with " +
                    $"{JoinNames(GetOtherNames(request.Group))} too, as one shared save, which none of you can play " +
                    "alone. Type /coopsave to agree.",
                    $"{player.Username} 想把大家当前的存档（还有 {JoinNames(GetOtherNames(request.Group))}）配成多人存档" +
                    "——配成之后谁都不能单独玩。打一次 /coopsave 就是同意。"
                )
        );
    }

    /// <summary>
    /// Agrees to a request to pair saves if both saves have beaten the same bosses. Otherwise the saves would differ in
    /// which bosses are still there, and one player would miss a boss and its reward. Nothing is paired until the game
    /// of the player who asked confirms, which waits until every player it asked has agreed.
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

        _acceptedPairRequest = new PairingRequest {
            PlayerId = player.Id,
            Id = request.Id,
            Slot = slot,
            Group = request.Group
        };
        Send(new CoopSaveUpdate {
            TargetId = player.Id,
            Kind = CoopSaveUpdateKind.PairAccept,
            Key = request.Id,
            Records = localDefeats
        });
        Chat(
            request.Group.Count <= 1
                ? Lang.Pick(
                    $"Agreed to pair your current save with the save of {player.Username}. Waiting for their game to " +
                    "confirm.",
                    $"已同意把你当前的存档和 {player.Username} 的存档配对，正在等对方那边确认。"
                )
                : Lang.Pick(
                    $"Agreed to pair your current save with the others. The game of {player.Username} confirms once " +
                    "everyone has agreed.",
                    $"已同意把你当前的存档和大家的配成多人存档。等所有人都同意了，{player.Username} 那边会确认。"
                )
        );
    }

    /// <summary>
    /// Another player agreed to the request of the local player. Once every player it went to has agreed, and if the
    /// request still stands for the loaded save and the beaten bosses still match, the local save is paired and the
    /// games of the other players are asked to pair theirs.
    /// </summary>
    private void OnPairAccept(ClientPlayerData player, CoopSaveUpdate update) {
        var sent = _sentPairRequest;
        if (sent == null || !sent.PlayerIds.Contains(player.Id) || sent.Id != update.Key) {
            SendPairCancel(player, update.Key);
            return;
        }

        if (!sent.IsOpen || !IsInGame() || global::GameManager.instance.profileID != sent.Slot) {
            _sentPairRequest = null;
            SendPairCancel(player, update.Key);
            CancelPairRequest(sent, player.Id);
            Chat(Lang.Pick(
                $"{player.Username} agreed too late, because your request ran out or you changed saves. Type /coopsave " +
                "to ask again.",
                $"{player.Username} 同意得太晚了——你的请求已经过期，或者你换了存档。打 /coopsave 再问一次。"
            ));
            return;
        }

        // A boss may have been beaten since the request
        if (!HaveSameDefeats(player, update.Records, GetDefeatRecords())) {
            _sentPairRequest = null;
            SendPairCancel(player, update.Key);
            CancelPairRequest(sent, player.Id);
            return;
        }

        sent.Accepted.Add(player.Id);
        var waiting = sent.PlayerIds.Where(id => !sent.Accepted.Contains(id)).ToList();
        if (waiting.Count > 0) {
            var waitingFor = JoinNames(
                waiting.Select(id => _playerData.TryGetValue(id, out var other) ? other.Username : "?")
            );
            Chat(Lang.Pick(
                $"{player.Username} agreed. Waiting for {waitingFor}.",
                $"{player.Username} 同意了，还在等 {waitingFor}。"
            ));
            return;
        }

        _sentPairRequest = null;

        // Everyone agreed, and is paired with as they are on the server now. Someone who left in between would have
        // dropped the request already (ForgetRequestsOf).
        var members = new List<CoopSaveMember>();
        foreach (var id in sent.PlayerIds) {
            if (!_playerData.TryGetValue(id, out var member)) {
                CancelPairRequest(sent, null);
                return;
            }

            members.Add(new CoopSaveMember { Key = member.SaveKey, Name = member.Username });
        }

        Pair(sent.Slot, members);
        _confirmedPairRequest = new PairingRequest {
            PlayerId = player.Id,
            PlayerIds = sent.PlayerIds,
            Id = update.Key,
            Slot = sent.Slot,
            Group = members
        };
        foreach (var id in sent.PlayerIds) {
            var confirm = new CoopSaveUpdate { TargetId = id, Kind = CoopSaveUpdateKind.PairConfirm, Key = update.Key };
            WriteGroup(confirm, members);
            Send(confirm);
        }
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
            Chat(Lang.Pick(
                $"The pairing with {player.Username} didn't go through, because you changed saves. Type /coopsave to " +
                "try again.",
                $"和 {player.Username} 的配对没成功，因为你换了存档。打 /coopsave 再试一次。"
            ));
            return;
        }

        // The player who asked, and the others it paired with apart from the local player
        var members = new List<CoopSaveMember> { new() { Key = player.SaveKey, Name = player.Username } };
        members.AddRange(ReadGroup(update).Where(member => member.Key != LocalKey));
        Pair(accepted.Slot, members);

        // A pairing of more than two players can still fail in the game of another of them, and the game of the player
        // who asked then undoes it everywhere
        if (members.Count > 1) {
            _confirmedPairRequest = new PairingRequest {
                PlayerId = player.Id,
                PlayerIds = [player.Id],
                Id = update.Key,
                Slot = accepted.Slot,
                Group = members
            };
        }
    }

    /// <summary>
    /// A request to pair saves didn't go through. A local save that was already paired for it is unpaired again.
    /// </summary>
    private void OnPairCancel(ClientPlayerData player, CoopSaveUpdate update) {
        if (_acceptedPairRequest is { } accepted && accepted.PlayerId == player.Id && accepted.Id == update.Key) {
            _acceptedPairRequest = null;
            Chat(Lang.Pick(
                $"The pairing with {player.Username} didn't go through. Type /coopsave to try again.",
                $"和 {player.Username} 的配对没成功。打 /coopsave 再试一次。"
            ));
        }

        if (_receivedPairRequest is { } received && received.PlayerId == player.Id && received.Id == update.Key) {
            _receivedPairRequest = null;
        }

        if (_sentPairRequest is { } sent && sent.PlayerIds.Contains(player.Id) && sent.Id == update.Key) {
            _sentPairRequest = null;
            CancelPairRequest(sent, player.Id);
        }

        if (_confirmedPairRequest is not { } confirmed || !confirmed.PlayerIds.Contains(player.Id) ||
            confirmed.Id != update.Key) {
            return;
        }

        _confirmedPairRequest = null;
        if (GetMarker(confirmed.Slot) is { } marker && IsSameGroup(marker, confirmed.Group)) {
            RemoveLocalPairing(confirmed.Slot);
            Chat(
                confirmed.Group.Count <= 1
                    ? Lang.Pick(
                        $"The pairing with {player.Username} was undone, because their game changed saves before it " +
                        "finished. Type /coopsave to try again.",
                        $"和 {player.Username} 的配对被撤销了，因为对方在完成之前换了存档。打 /coopsave 再试一次。"
                    )
                    : Lang.Pick(
                        "The shared save didn't go through for everyone, so it was undone. Type /coopsave to try again.",
                        "多人存档没能在每个人那边都配好，所以撤销了。打 /coopsave 再试一次。"
                    )
            );

            // The other players it paired with undo theirs too
            foreach (var id in confirmed.PlayerIds) {
                if (id != player.Id && _playerData.TryGetValue(id, out var other)) {
                    SendPairCancel(other, confirmed.Id);
                }
            }
        }
    }

    /// <summary>
    /// The other player couldn't agree to the request of the local player, because their save has beaten different
    /// bosses.
    /// </summary>
    private void OnPairRefused(ClientPlayerData player, CoopSaveUpdate update) {
        if (_sentPairRequest is not { } sent || !sent.PlayerIds.Contains(player.Id) || sent.Id != update.Key) {
            return;
        }

        _sentPairRequest = null;
        CancelPairRequest(sent, player.Id);
        if (HaveSameDefeats(player, update.Records, GetDefeatRecords())) {
            Chat(Lang.Pick(
                $"{player.Username} couldn't agree, because their save had beaten different bosses. Type /coopsave to ask again.",
                $"{player.Username} 没能同意，因为对方存档打过的 Boss 和你的对不上。打 /coopsave 再问一次。"
            ));
        }
    }

    /// <summary>
    /// Tells the other player's game that a request to pair saves didn't go through.
    /// </summary>
    private void SendPairCancel(ClientPlayerData player, ulong requestId) {
        Send(new CoopSaveUpdate { TargetId = player.Id, Kind = CoopSaveUpdateKind.PairCancel, Key = requestId });
    }

    /// <summary>
    /// Tells the other players that a request of the local player went to that it didn't go through, so that one who
    /// agreed stops waiting for it and one who didn't yet isn't offered it any more.
    /// </summary>
    /// <param name="sent">The request.</param>
    /// <param name="except">A player who knows already, or null.</param>
    private void CancelPairRequest(PairingRequest sent, ushort? except) {
        foreach (var id in sent.PlayerIds) {
            if (id != except && _playerData.TryGetValue(id, out var other)) {
                SendPairCancel(other, sent.Id);
            }
        }
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
            Lang.Pick(
                $"These saves can't become a two-player save, because they have beaten different bosses: {player.Username} " +
                $"beat {onlyPartner.Count} that you haven't, and you beat {onlyLocal.Count} that they haven't. A two-player " +
                "save needs the same bosses beaten in both saves, like two new games.",
                $"这两个存档没法组成双人存档，因为打过的 Boss 对不上：{player.Username} 打过 {onlyPartner.Count} 个你没打过的，" +
                $"你打过 {onlyLocal.Count} 个他们没打过的。双人存档需要两边打过的 Boss 完全一样，比如两个都是新游戏。"
            )
        );
        return false;
    }

    /// <summary>
    /// Pairs the save in a slot with the saves of other players, after which all of them get checked.
    /// </summary>
    /// <param name="slot">The slot of the local save.</param>
    /// <param name="members">The other players, with their save keys and usernames.</param>
    private void Pair(int slot, List<CoopSaveMember> members) {
        var previous = GetMarker(slot);
        var marker = new CoopSaveMarker { Members = members, PairedUtc = DateTime.UtcNow };
        marker.TakeOverPartner();
        GetMarkers().Slots[GetSlotKey(slot)] = marker;
        SaveMarkers();

        _receivedPairRequest = null;
        ResetCheck();
        _helloKeyMismatchTold = false;

        var names = JoinNames(members.Select(member => member.Name));
        Logger.Info($"Paired save slot {slot} with the saves of {string.Join(", ", members.Select(member => member.Name))}");
        if (members.Count == 1) {
            Chat(
                previous != null && !IsSameGroup(previous, members)
                    ? Lang.Pick(
                        $"Your current save is now a two-player save with {names} instead of " +
                        $"{JoinNames(previous.Members.Select(member => member.Name))}. Both saves get backed up and " +
                        "compared now.",
                        $"你当前的存档现在是和 {names} 的双人存档了，不再是和 " +
                        $"{JoinNames(previous.Members.Select(member => member.Name))} 的。从现在起两边的存档都会备份并互相比对。"
                    )
                    : Lang.Pick(
                        $"Your current save is now a two-player save with {names}. Both saves get backed up and " +
                        "compared now.",
                        $"你当前的存档现在是和 {names} 的双人存档了。从现在起两边的存档都会备份并互相比对。"
                    )
            );
            return;
        }

        Chat(
            previous != null && !IsSameGroup(previous, members)
                ? Lang.Pick(
                    $"Your current save is now a shared save with {names} instead of " +
                    $"{JoinNames(previous.Members.Select(member => member.Name))}. All the saves get backed up and " +
                    "compared now.",
                    $"你当前的存档现在是和 {names} 的多人存档了，不再是和 " +
                    $"{JoinNames(previous.Members.Select(member => member.Name))} 的。从现在起每个人的存档都会备份并互相比对。"
                )
                : Lang.Pick(
                    $"Your current save is now a shared save with {names}. All the saves get backed up and compared now.",
                    $"你当前的存档现在是和 {names} 的多人存档了。从现在起每个人的存档都会备份并互相比对。"
                )
        );
    }

    /// <summary>
    /// Makes the shared save in a slot a normal save again, for every player of it. All of them need to be on the
    /// server, so that it can't be used to play the save alone. If one of them hasn't loaded the save, which their game
    /// may have lost, they need to agree with /coopsave off too. With one of them not on the server, only the local
    /// pairing goes.
    /// </summary>
    private void Unpair(int slot, CoopSaveMarker? marker) {
        if (_receivedUnpairRequest is { IsOpen: true } received &&
            _playerData.TryGetValue(received.PlayerId, out var requester)) {
            _receivedUnpairRequest = null;
            AgreeToUnpair(slot, marker, requester, received.Id);
            return;
        }

        if (marker == null) {
            Chat(Lang.Pick("Your current save isn't a two-player save.", "你当前的存档不是双人存档。"));
            return;
        }

        var members = FindMembers(marker);
        if (members.Count < marker.Members.Count) {
            // Refusing here used to leave the save impossible to play and impossible to leave: it holds the player
            // because the partner is missing, and it turned down unpairing for that same reason. Only this side's
            // pairing goes, so the partner still has to turn theirs off before either of them plays it alone.
            RemoveLocalPairing(slot);
            ReleaseHold(HeroController.instance);
            Chat(GetLeftAloneMessage(marker, true));
            return;
        }

        var names = JoinNames(members.Select(member => member.Username));
        if (members.All(member => _checkedMembers.Contains(member.Id))) {
            RemoveLocalPairing(slot);
            Chat(
                members.Count == 1
                    ? Lang.Pick(
                        $"Your two-player save with {names} is a normal save again, for both of you.",
                        $"你和 {names} 的双人存档已经变回普通存档了，两个人都是。"
                    )
                    : Lang.Pick(
                        $"Your shared save with {names} is a normal save again, for all of you.",
                        $"你和 {names} 的多人存档已经变回普通存档了，每个人都是。"
                    )
            );
            foreach (var member in members) {
                Send(new CoopSaveUpdate { TargetId = member.Id, Kind = CoopSaveUpdateKind.Unpaired });
            }

            return;
        }

        _sentUnpairRequest = new PairingRequest {
            PlayerId = members[0].Id,
            PlayerIds = members.Select(member => member.Id).ToList(),
            Id = NewRequestId(),
            Slot = slot
        };
        foreach (var member in members) {
            Send(new CoopSaveUpdate {
                TargetId = member.Id,
                Kind = CoopSaveUpdateKind.UnpairRequest,
                Key = _sentUnpairRequest.Id
            });
        }

        Chat(
            members.Count == 1
                ? Lang.Pick(
                    $"Asked {names} to agree to make this two-player save a normal save again. They need to type " +
                    "/coopsave off too.",
                    $"已经问 {names} 要不要把这个双人存档变回普通存档。对方也要打 /coopsave off 才算数。"
                )
                : Lang.Pick(
                    $"Asked {names} to agree to make this shared save a normal save again. Each of them needs to type " +
                    "/coopsave off too.",
                    $"已经问 {names} 要不要把这个多人存档变回普通存档。每个人也都要打 /coopsave off 才算数。"
                )
        );
    }

    /// <summary>
    /// What a player hears whose save stops being shared on their side alone: the others still have it as a shared save
    /// until they leave it too.
    /// </summary>
    /// <param name="marker">The pairing that the local save had.</param>
    /// <param name="byCommand">Whether it went with /coopsave off rather than with the key of a held player.</param>
    private static string GetLeftAloneMessage(CoopSaveMarker marker, bool byCommand) {
        var names = JoinNames(marker.Members.Select(member => member.Name));
        if (marker.Members.Count <= 1) {
            return byCommand
                ? Lang.Pick(
                    $"Your save is a normal save again. It stays a two-player save for {names} until they " +
                    "use /coopsave off as well.",
                    $"你的存档变回普通存档了。在 {names} 那边也用 /coopsave off 之前，" +
                    "它对对方来说仍然是双人存档。"
                )
                : Lang.Pick(
                    "Your save is a normal save again. It stays a two-player save for " +
                    $"{names} until they leave it as well.",
                    $"你的存档变回普通存档了。在 {names} 那边也退出之前，" +
                    "它对对方来说仍然是双人存档。"
                );
        }

        return byCommand
            ? Lang.Pick(
                $"Your save is a normal save again. It stays a shared save for {names} until they use /coopsave off " +
                "as well, or pair again without you.",
                $"你的存档变回普通存档了。在 {names} 也用 /coopsave off（或者不带你重新配对）之前，它对他们来说仍然是多人存档。"
            )
            : Lang.Pick(
                $"Your save is a normal save again. It stays a shared save for {names} until they leave it as well, " +
                "or pair again without you.",
                $"你的存档变回普通存档了。在 {names} 也退出（或者不带你重新配对）之前，它对他们来说仍然是多人存档。"
            );
    }

    /// <summary>
    /// A member asks to make the shared save a normal save again. If the local player asked too, both agree.
    /// </summary>
    private void OnUnpairRequest(ClientPlayerData player, CoopSaveUpdate update) {
        if (_sentUnpairRequest is { IsOpen: true } sent && sent.PlayerIds.Contains(player.Id) && IsInGame() &&
            global::GameManager.instance.profileID == sent.Slot) {
            // Of two requests of a save of more than two players, the one with the larger ID goes ahead, and the
            // member who asked with the other agrees to it
            if (sent.PlayerIds.Count > 1 && update.Key < sent.Id) {
                return;
            }

            _sentUnpairRequest = null;
            AgreeToUnpair(sent.Slot, GetMarker(sent.Slot), player, update.Key);
            return;
        }

        _receivedUnpairRequest = new PairingRequest { PlayerId = player.Id, Id = update.Key };
        Chat(
            GetCurrentMarker() is { Members.Count: > 1 }
                ? Lang.Pick(
                    $"{player.Username} wants to make your shared save a normal save again, for all of you. Type " +
                    "/coopsave off to agree.",
                    $"{player.Username} 想把你们的多人存档变回普通存档，每个人都变。打 /coopsave off 表示同意。"
                )
                : Lang.Pick(
                    $"{player.Username} wants to make your two-player save a normal save again, for both of you. Type " +
                    "/coopsave off to agree.",
                    $"{player.Username} 想把你们的双人存档变回普通存档，两个人都变。打 /coopsave off 表示同意。"
                )
        );
    }

    /// <summary>
    /// Agrees to make the shared save with another player a normal save again: the current save is unpaired if it is
    /// paired with them, and their save is unpaired. A save of more than two players stays shared until every member
    /// agreed, or some would be left waiting for players whose saves aren't shared any more: the agreement goes to
    /// every member, and whoever asked unpairs all of them once each agreed (OnUnpairAccept).
    /// </summary>
    private void AgreeToUnpair(int slot, CoopSaveMarker? marker, ClientPlayerData requester, ulong requestId) {
        if (marker != null && IsMember(requester, marker) && marker.Members.Count > 1) {
            SendToMembers(new CoopSaveUpdate { Kind = CoopSaveUpdateKind.UnpairAccept, Key = requestId });
            Chat(Lang.Pick(
                $"Agreed to make your shared save a normal save again. It becomes one once everyone agreed.",
                $"已同意把你们的多人存档变回普通存档。等所有人都同意了才会变。"
            ));
            return;
        }

        if (marker != null && IsMember(requester, marker)) {
            RemoveLocalPairing(slot);
        }

        Send(new CoopSaveUpdate { TargetId = requester.Id, Kind = CoopSaveUpdateKind.Unpaired });
        Chat(Lang.Pick(
            $"Your two-player save with {requester.Username} is a normal save again, for both of you.",
            $"你和 {requester.Username} 的双人存档已经变回普通存档了，两个人都是。"
        ));
    }

    /// <summary>
    /// A member agreed to the request of the local player to make a save of more than two players a normal save again.
    /// Once every member agreed, it is unpaired here and for all of them.
    /// </summary>
    private void OnUnpairAccept(ClientPlayerData player, CoopSaveUpdate update) {
        // An agreement to the request of another member counts too: requests that crossed agree to the same thing
        if (_sentUnpairRequest is not { IsOpen: true } sent || !sent.PlayerIds.Contains(player.Id) || !IsInGame() ||
            global::GameManager.instance.profileID != sent.Slot) {
            return;
        }

        sent.Accepted.Add(player.Id);
        var waiting = sent.PlayerIds.Where(id => !sent.Accepted.Contains(id)).ToList();
        if (waiting.Count > 0) {
            Chat(Lang.Pick(
                $"{player.Username} agreed. Waiting for " +
                $"{JoinNames(waiting.Select(id => _playerData.TryGetValue(id, out var other) ? other.Username : "?"))}.",
                $"{player.Username} 同意了，还在等 " +
                $"{JoinNames(waiting.Select(id => _playerData.TryGetValue(id, out var other) ? other.Username : "?"))}。"
            ));
            return;
        }

        _sentUnpairRequest = null;
        var names = JoinNames(
            sent.PlayerIds.Select(id => _playerData.TryGetValue(id, out var other) ? other.Username : "?")
        );
        RemoveLocalPairing(sent.Slot);
        foreach (var id in sent.PlayerIds) {
            Send(new CoopSaveUpdate { TargetId = id, Kind = CoopSaveUpdateKind.Unpaired });
        }

        Chat(Lang.Pick(
            $"Your shared save with {names} is a normal save again, for all of you.",
            $"你和 {names} 的多人存档已经变回普通存档了，每个人都是。"
        ));
    }

    /// <summary>
    /// A member made the shared save a normal save again, which goes for the local save too.
    /// </summary>
    private void OnUnpaired(ClientPlayerData player) {
        var slot = _sentUnpairRequest is { } sent && sent.PlayerIds.Contains(player.Id)
            ? sent.Slot
            : IsInGame()
                ? global::GameManager.instance.profileID
                : -1;
        if (_sentUnpairRequest is { } asked && asked.PlayerIds.Contains(player.Id)) {
            _sentUnpairRequest = null;
        }

        var marker = GetMarker(slot);
        if (marker == null || !IsMember(player, marker)) {
            return;
        }

        RemoveLocalPairing(slot);
        Logger.Info($"{player.Username} unpaired the shared save");
        Chat(
            marker.Members.Count > 1
                ? Lang.Pick(
                    $"{player.Username} made your shared save a normal save again, for all of you.",
                    $"{player.Username} 把你们的多人存档变回普通存档了，每个人都是。"
                )
                : Lang.Pick(
                    $"{player.Username} made your two-player save a normal save again, for both of you.",
                    $"{player.Username} 把你们的双人存档变回普通存档了，两个人都是。"
                )
        );
    }

    /// <summary>
    /// Removes the pairing of a local save, and lets the player move if it is the loaded save.
    /// </summary>
    private void RemoveLocalPairing(int slot) {
        RemoveMarker(slot);
        if (slot == _sessionSlot) {
            // The save is its player's alone now, and so is a wish that a step of dialogue waits to begin with the
            // partner: the step goes on and takes it (CoopSave.WishRead)
            EndHeldBegin(null, true);
            ReleaseHold(HeroController.instance);
            ResetSession(false);
        }

        Logger.Info($"Save slot {slot} isn't paired anymore");
    }

    /// <summary>
    /// Forgets the requests about pairing saves with a player who left. A request of the local player that they were
    /// to agree to can't go through any more, and whoever else it went to hears so.
    /// </summary>
    private void ForgetRequestsOf(ushort id) {
        if (_sentPairRequest is { } sent && sent.PlayerIds.Contains(id)) {
            _sentPairRequest = null;
            CancelPairRequest(sent, id);
        }

        if (_receivedPairRequest?.PlayerId == id) {
            _receivedPairRequest = null;
        }

        if (_acceptedPairRequest?.PlayerId == id) {
            _acceptedPairRequest = null;
        }

        if (_sentUnpairRequest is { } unpair && unpair.PlayerIds.Contains(id)) {
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

    /// <summary>
    /// The other players on the server, in the order of their IDs.
    /// </summary>
    private List<ClientPlayerData> GetOthersOnServer() {
        return _playerData.Values.OrderBy(player => player.Id).ToList();
    }

    /// <summary>
    /// Whether a request of the local player went to exactly the given players.
    /// </summary>
    private static bool IsRequestTo(PairingRequest request, List<ClientPlayerData> players) {
        return request.PlayerIds.Count == players.Count && players.All(player => request.PlayerIds.Contains(player.Id));
    }

    /// <summary>
    /// Writes the players that a request or a pairing is for into an update: their usernames in
    /// <see cref="CoopSaveUpdate.Names"/> and their save keys in <see cref="CoopSaveUpdate.ItemIds"/>.
    /// </summary>
    private static void WriteGroup(CoopSaveUpdate update, List<CoopSaveMember> group) {
        update.Names = group.Select(member => member.Name).ToList();
        update.ItemIds = group.Select(member => member.Key).ToList();
    }

    /// <summary>
    /// Reads the players that a request or a pairing is for from an update (<see cref="WriteGroup"/>).
    /// </summary>
    private static List<CoopSaveMember> ReadGroup(CoopSaveUpdate update) {
        var group = new List<CoopSaveMember>();
        for (var i = 0; i < update.Names.Count && i < update.ItemIds.Count; i++) {
            group.Add(new CoopSaveMember { Key = update.ItemIds[i], Name = update.Names[i] });
        }

        return group;
    }

    /// <summary>
    /// The names in the players of a request other than the local player.
    /// </summary>
    private List<string> GetOtherNames(List<CoopSaveMember> group) {
        return group.Where(member => member.Key != LocalKey).Select(member => member.Name).ToList();
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
        if (_netClient.IsConnected && HasAllMembers(marker)) {
            return true;
        }

        var names = string.Join(", ", marker.Members.Select(member => member.Name));
        var kind = marker.Members.Count <= 1 ? "two-player save" : "shared save";
        if (_uiManager.IsSelectingHostSave) {
            if (!_uiManager.StartHostBeforeSave()) {
                ShowMessage($"Could not open your game for {names}.");
                return false;
            }

            _menuActionToken++;
            _waitingButton = button;
            _waitingEventData = eventData;
            _waitingMarker = marker;
            Logger.Info(
                $"Hosting before two-player save in slot {button.SaveSlotIndex} loads, waiting for {names}"
            );
            ShowMessage(
                $"Your game is open. Your {kind} with {names} loads once " +
                (marker.Members.Count <= 1 ? "they have" : "all of them have") + " joined. " +
                "Closing this message stops hosting.",
                OnWaitMessageClosed
            );
            return false;
        }

        var missing = MatchMembers(marker).Where(match => match.Player == null).Select(match => match.Member.Name)
            .ToList();
        ShowMessage(
            _netClient.IsConnected
                ? marker.Members.Count <= 1
                    ? $"This is a two-player save with {names}, who isn't on this server."
                    : $"This is a shared save with {names}, and {string.Join(", ", missing)} " +
                      (missing.Count == 1 ? "isn't" : "aren't") + " on this server yet."
                : $"This is a {kind} with {names}. Host a game and wait for them to join, or " +
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
                ShowMessage(
                    $"Your game closed before {string.Join(", ", marker.Members.Select(member => member.Name))} " +
                    "joined. Choose the save again to host again."
                );
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
    /// Whether a player is a member of a paired save: they have the save key of one of its members, or the name of one,
    /// because a member who plays on another computer has another save key.
    /// </summary>
    private static bool IsMember(ClientPlayerData player, CoopSaveMarker marker) {
        return marker.Members.Any(member =>
            (player.SaveKey.Length > 0 && player.SaveKey == member.Key) ||
            (member.Name.Length > 0 && player.Username == member.Name)
        );
    }

    /// <summary>
    /// Whether a player is a member of a paired save, as the parts say that keep in step with one partner at a time.
    /// </summary>
    private static bool IsPartner(ClientPlayerData player, CoopSaveMarker marker) => IsMember(player, marker);

    /// <summary>
    /// Each member of a paired save with the player on the server who is them, or null: the player with their save key,
    /// or else one with their name. No player stands for two members.
    /// </summary>
    private List<(CoopSaveMember Member, ClientPlayerData? Player)> MatchMembers(CoopSaveMarker marker) {
        var players = new ClientPlayerData?[marker.Members.Count];
        var taken = new HashSet<ushort>();
        for (var i = 0; i < players.Length; i++) {
            var key = marker.Members[i].Key;
            if (key.Length == 0) {
                continue;
            }

            foreach (var player in _playerData.Values) {
                if (player.SaveKey == key && taken.Add(player.Id)) {
                    players[i] = player;
                    break;
                }
            }
        }

        for (var i = 0; i < players.Length; i++) {
            var name = marker.Members[i].Name;
            if (players[i] != null || name.Length == 0) {
                continue;
            }

            foreach (var player in _playerData.Values) {
                if (player.Username == name && taken.Add(player.Id)) {
                    players[i] = player;
                    break;
                }
            }
        }

        var matches = new List<(CoopSaveMember Member, ClientPlayerData? Player)>(players.Length);
        for (var i = 0; i < players.Length; i++) {
            matches.Add((marker.Members[i], players[i]));
        }

        return matches;
    }

    /// <summary>
    /// The members of a paired save that are on the server, in the order of the pairing.
    /// </summary>
    private List<ClientPlayerData> FindMembers(CoopSaveMarker marker) {
        var members = new List<ClientPlayerData>();
        foreach (var (_, player) in MatchMembers(marker)) {
            if (player != null) {
                members.Add(player);
            }
        }

        return members;
    }

    /// <summary>
    /// One member of a paired save on the server, or null, for the parts that keep in step with one partner at a time.
    /// </summary>
    private ClientPlayerData? FindPartner(CoopSaveMarker marker) => FindMembers(marker).FirstOrDefault();

    /// <summary>
    /// Whether every member of a paired save is on the server.
    /// </summary>
    private bool HasAllMembers(CoopSaveMarker marker) => FindMembers(marker).Count == marker.Members.Count;

    /// <summary>
    /// Whether every member of a paired save is on the server and was checked with.
    /// </summary>
    private bool IsGroupComplete(CoopSaveMarker marker, List<ClientPlayerData> members) {
        return members.Count == marker.Members.Count && members.All(member => _checkedMembers.Contains(member.Id));
    }

    /// <summary>
    /// The member of a paired save that a player is, or null.
    /// </summary>
    private static CoopSaveMember? FindMarkerMember(CoopSaveMarker marker, ClientPlayerData player) {
        return marker.Members.FirstOrDefault(member => player.SaveKey.Length > 0 && player.SaveKey == member.Key) ??
               marker.Members.FirstOrDefault(member => member.Name.Length > 0 && player.Username == member.Name);
    }

    /// <summary>
    /// The save key that a pairing has for a player: the key that a hello to them names, and that decides between the
    /// saves of the players.
    /// </summary>
    private static string GetMemberKey(CoopSaveMarker? marker, ClientPlayerData player) {
        return marker != null && FindMarkerMember(marker, player) is { } member ? member.Key : player.SaveKey;
    }

    /// <summary>
    /// Whether a pairing is with exactly the given players, by their save keys.
    /// </summary>
    private static bool IsSameGroup(CoopSaveMarker marker, List<ClientPlayerData> players) {
        return marker.Members.Count == players.Count &&
               players.All(player => marker.Members.Any(member => member.Key == player.SaveKey));
    }

    /// <summary>
    /// Whether a pairing is with exactly the given members, by their save keys.
    /// </summary>
    private static bool IsSameGroup(CoopSaveMarker marker, List<CoopSaveMember> members) {
        return marker.Members.Count == members.Count &&
               members.All(member => marker.Members.Any(other => other.Key == member.Key));
    }

    /// <summary>
    /// The members of a paired save that it waits for: those not on the server, and those on it who haven't been
    /// checked with. Joined for a message.
    /// </summary>
    private string GetMissingNames(CoopSaveMarker marker) {
        return JoinNames(
            MatchMembers(marker)
                .Where(match => match.Player == null || !_checkedMembers.Contains(match.Player.Id))
                .Select(match => match.Player?.Username ?? match.Member.Name)
        );
    }

    /// <summary>
    /// What a paired save is called in messages: a two-player save, or a shared save of more players.
    /// </summary>
    private static string SaveWord(CoopSaveMarker? marker) {
        return marker == null || marker.Members.Count <= 1
            ? Lang.Pick("two-player save", "双人存档")
            : Lang.Pick("shared save", "多人存档");
    }

    /// <summary>
    /// The names of players for a message: "A", "A and B" or "A, B and C", or in Chinese "A、B、C".
    /// </summary>
    internal static string JoinNames(IEnumerable<string> names) {
        var list = names.ToList();
        if (list.Count == 0) {
            return Lang.Pick("your teammate", "队友");
        }

        return Lang.Pick(
            list.Count == 1 ? list[0] : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[list.Count - 1],
            string.Join("、", list)
        );
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

        // Pairings from before saves could have more than two players name their only other player alone
        foreach (var marker in _markers.Slots.Values) {
            marker.TakeOverPartner();
        }

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
    /// Tells the other player in the waiting room whether the local player is ready to choose a save. Sent from the
    /// menu, so it deliberately asks nothing about saves being loaded or paired.
    /// </summary>
    /// <param name="ready">Whether the local player is ready.</param>
    public void SendWaitingRoomReady(bool ready) {
        if (!_netClient.IsConnected) {
            return;
        }

        foreach (var other in _playerData.Values) {
            Send(new CoopSaveUpdate {
                TargetId = other.Id,
                Kind = CoopSaveUpdateKind.WaitingRoomReady,
                Part = (ushort) (ready ? 1 : 0)
            });
        }
    }

    /// <summary>
    /// The members that the saves were checked with, as they are on the server, in the order of their IDs.
    /// </summary>
    private List<ClientPlayerData> GetCheckedMembers() {
        var members = new List<ClientPlayerData>(_checkedMembers.Count);
        foreach (var id in _checkedMembers.OrderBy(id => id)) {
            if (_playerData.TryGetValue(id, out var member)) {
                members.Add(member);
            }
        }

        return members;
    }

    /// <summary>
    /// Whether a member that the saves were checked with is in the room of the local player.
    /// </summary>
    private bool IsMemberInRoom() {
        foreach (var id in _checkedMembers) {
            if (_playerData.TryGetValue(id, out var member) && member.IsInLocalScene) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The usernames of the members that the saves were checked with, joined for a message or the log.
    /// </summary>
    private string GetCheckedNames() => JoinNames(GetCheckedMembers().Select(member => member.Username));

    /// <summary>
    /// Whether an update comes from a member who is in the same save as the local player: one that the saves were
    /// checked with, or one who said hello in the current round while the local game still finishes it. A player whose
    /// own pairing still names the local player, but who isn't in the save of the local player any more, is neither.
    /// </summary>
    private bool IsFromMemberInSave(ClientPlayerData player) {
        return _checkedMembers.Contains(player.Id) ||
               (_memberChecks.TryGetValue(player.Id, out var check) && check.Hello);
    }

    /// <summary>
    /// Sends an update to every other member at once, through the server, which copies it to every other player. Each
    /// game takes it only from a member it is in the save with. In a two-player save that is the partner alone.
    /// </summary>
    private void SendToMembers(CoopSaveUpdate update) {
        update.TargetId = CoopTargets.Everyone;
        Send(update);
    }

    /// <summary>
    /// Sends a copy of an update that was sent already to one more player, as it is: with the stamp it got, so that the
    /// same change counts as one wherever it goes.
    /// </summary>
    private void SendCopy(CoopSaveUpdate update, ushort targetId) {
        var copy = update.Copy();
        copy.TargetId = targetId;
        if (_netClient.IsConnected) {
            _netClient.UpdateManager.SetCoopSaveUpdate(copy);
        }
    }

    /// <summary>
    /// Sends an update of a co-op save to another player, or to every other player.
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
