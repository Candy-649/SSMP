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
/// Co-op saves. A save isn't bound to the players it is played with (the user's rules of 2026-10-11): a save becomes a
/// co-op save when its player hosts a game that someone joins, or joins someone's game with it, and it opens only with
/// another player on the server. A joining player's save takes the host's world as it loads (CoopSave.HostSync): the
/// host's beaten bosses, story, wishes and mechanisms, the important things the host found, and their own belongings.
/// The players of a session are whoever is in it. While one of them hasn't loaded yet, or one has left, the others wait
/// seated on a bench, until they are back or the others stop waiting for them; a player left alone can make the save a
/// normal save again. Each time they are all in, every save is backed up and checked with the others, and from then on
/// they keep in step as they play.
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
    /// The updates of other players that came while the loaded save played a scene that is no room, like the cinematic
    /// of a ride, in the order they came. They are taken once the local game is in a room again.
    /// </summary>
    private readonly List<CoopSaveUpdate> _updatesOutOfRoom = new();

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
    /// Whether updating the session threw, which is only logged once.
    /// </summary>
    private bool _updateFailed;

    /// <summary>
    /// Whether moving the check along threw, which is only logged once.
    /// </summary>
    private bool _checkFailed;

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
        RegisterRoomMixHooks();
        RegisterLavaDeathHooks();
        RegisterArrivalHooks();
        RegisterTrapdoorHooks();
        RegisterPrisonCaptureHooks();
        RegisterCreatureDeathHooks();
        RegisterSlidePlatformHooks();
        RegisterSpikedCogHooks();
        RegisterDialDoorHooks();
        RegisterPodSequenceHooks();
        RegisterShownPartHooks();
        RegisterRoomLayoutHooks();
        ToldParts.RegisterHooks();
        WishDrops.RegisterHooks();
        ToldParts.IsFollowedInEveryState = machine =>
            FindMachineKind(machine.gameObject, machine.FsmName) is { IsDirector: true };
        ToldParts.IsPaced = machine => FindMachineKind(machine.gameObject, machine.FsmName) != null;
        RegisterHostSyncHooks();

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
            // First, so that what the partner did before the local game came back to a room is in the save before
            // anything of the local save is looked at or sent
            TakeUpdatesOutOfRoom();
            TakeHeldBeastLocation();
            UpdateSession(hero);
        } catch (Exception e) {
            if (!_updateFailed) {
                _updateFailed = true;
                Logger.Error($"Could not update the two-player save:\n{e}");
            }
        }

        // Its own try, because a line of text failing must not stop the save from keeping step with the partner, and
        // a session that fails must still tell a held player what they wait for
        try {
            UpdateHoldPrompt();
        } catch (Exception e) {
            if (!_holdPromptFailed) {
                _holdPromptFailed = true;
                Logger.Error($"Could not show the co-op save prompt:\n{e}");
            }
        }

        // Its own try too. The host's save goes out from every frame, the menu included (OnHostSaveUpdate)
        try {
            UpdateJoinSummary();
        } catch (Exception e) {
            Logger.Error($"Could not tell what joining the host's save changed:\n{e}");
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
        var newSession = slot != _sessionSlot;
        if (newSession) {
            ResetSession(true);
            _sessionSlot = slot;
            _declinedSlot = -1;
            _loadedWithCheckpoint = GetMarker(slot)?.BossScene != null;
        }

        var marker = GetMarker(slot);
        if (marker == null && BecomesCoopSave(slot)) {
            MarkCoopSave(slot, []);
            marker = GetMarker(slot);

            // A host who was already playing when someone joined plays on, and waits for them at the next bench they
            // sit on, like for a player who left: frozen where they stand, they could be in the middle of a fight or
            // a jump for as long as the other player takes to choose a save and load it
            _holdOnlyAtBench = !newSession;
            var names = JoinNames(GetOthersOnServer().Select(player => player.Username));
            Logger.Info($"The hosted save in slot {slot} became a co-op save with {names}");
            Chat(Lang.Pick(
                $"{names} joined your game, so this save is a co-op save now: it opens with another player on the " +
                "server, and the world of those who join follows yours.",
                $"{names} 加入了你的游戏，这个存档成了联机存档：之后要有别人在服务器上才能打开，加入的人世界都按你的进度走。"
            ));
        }

        if (marker == null) {
            FinishSummon(hero);
            ReleaseHold(hero);
            ReleasePartnerCatch(hero, "the save is no longer a co-op save", null);

            // A board that waited for the partner's answer when the save stopped being paired still has to open, or
            // the hero would stand in its dialogue for good
            try {
                UpdateWishCopyChecks();
            } catch (Exception e) {
                LogWishTalkError(e);
            }

            return;
        }

        if (!_rosterStarted) {
            _rosterStarted = true;
            StartRoster(marker, slot);
        } else {
            UpdateRoster(marker);
        }

        var members = FindMembers(marker);
        if (HasUncheckedMember(members)) {
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
        var checkedPartner = FirstCheckedMember(members);

        // A boss checkpoint that throws must not keep the hold below from working either
        try {
            UpdateCheckpoint(hero, marker, members);
        } catch (Exception e) {
            if (!_checkpointFailed) {
                _checkpointFailed = true;
                Logger.Error($"Could not update the boss checkpoint of the two-player save:\n{e}");
            }
        }

        // First and in its own try: one of the others throwing must not keep a hold for the partner's catch from
        // letting go of the player, who would stand still until they changed rooms
        try {
            UpdatePrisonCapture(hero);
        } catch (Exception e) {
            if (!_prisonCaptureFailed) {
                _prisonCaptureFailed = true;
                Logger.Error($"Could not update the capture of the two-player save:\n{e}");
            }
        }

        UpdateWishTalk();
        UpdateDeliverySummon(hero);
        UpdateRescue(hero);
        UpdateLavaChase(hero);
        UpdateChaseStandUp(hero);
        UpdateRaces();
        UpdateClothesGrab(hero);

        // Also once the partner is gone, so that a bench they kept up flips back
        UpdateTollBenches();

        // Also once the partner is gone, so that machines waiting for their word go by themselves
        try {
            UpdateMachines();
        } catch (Exception e) {
            LogInteractionError(e);
        }

        // Also once the partner is gone, so that a clock on its way to theirs gets there
        try {
            UpdateRoomClocks();
        } catch (Exception e) {
            LogInteractionError(e);
        }

        // Also while nobody else is in, so that a member who comes into the room later gets its layout
        try {
            UpdateRoomLayouts();
        } catch (Exception e) {
            LogInteractionError(e);
        }

        // Also while another member is gone, so that the members who are in keep in step with each other
        if (checkedPartner != null) {
            UpdateInteractions();
            UpdateWorldChanges();
            UpdateWishes();
            UpdateStoryFlags();
            UpdateLifts();
            UpdateTrapdoors();
        }

        if (IsGroupComplete(marker, members)) {
            UpdateCheckedPlayTime(marker);
            ReleaseHold(hero);
            return;
        }

        // Off the server nobody can come into the session, so a hold would freeze the player for good.
        // OnLocalDisconnect lets go of them for that reason, and this keeps the hold from coming straight back.
        if (!_netClient.IsConnected) {
            ReleaseHold(hero);
            return;
        }

        if (_held || (!_everChecked && !_holdOnlyAtBench) || PlayerData.instance.atBench) {
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
        _holdOnlyAtBench = false;
        _rosterStarted = false;
        _declinedBy.Clear();
        _updatesOutOfRoom.Clear();

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
        ResetRoomLayouts();
    }

    /// <summary>
    /// Leaves the loaded save when the game goes to the main menu.
    /// </summary>
    private void OnReturnToMainMenu() {
        ResetSession(true);
        ForgetLocalLayout();
        _sessionSlot = -1;

        // Here rather than on the first frame of the next session: a hello that comes in between would be answered as
        // though the save loaded again were still a normal save (Check.cs, OnHello)
        _declinedSlot = -1;
    }

    /// <summary>
    /// Leaves the loaded save when the scene of the main menu loads, and ends the boss checkpoint of the loaded save
    /// when the local player leaves the scene of its fight.
    /// </summary>
    private void OnActiveSceneChanged(Scene oldScene, Scene newScene) {
        _activeSceneName = newScene.name;
        if (newScene.name == MenuSceneName) {
            OnReturnToMainMenu();
            return;
        }

        // First, so that none of the others throwing on the way can keep them from running. Forgetting the machines
        // only empties lists, which can't throw.
        ResetMachines();
        _layoutPickingStates.Clear();
        GrowBackPustulesThePartnerDrew();
        OnCheckpointSceneChanged(newScene.name);
        ResetInteractions();
        OnWishTalkSceneChanged();
        OnLiftSceneChanged();
        OnDeliverySceneChanged();
        OnRescueSceneChanged();
        OnLavaChaseSceneChanged(newScene);
        OnStorySceneChanged(newScene);
        OnCageSceneChanged();
        OnPrisonCatchSceneChanged();
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
        ForgetJoin();
        if (_waitingMarker != null && GetOthersOnServer().Count > 0) {
            LoadWaitingSave();
        }
    }

    /// <summary>
    /// Called when a player connects. The last member of a save that waits in the menu to join makes it load, and a
    /// member of the loaded save comes into its next round.
    /// </summary>
    public void OnPlayerConnect(ClientPlayerData player) {
        if (_waitingMarker != null) {
            LoadWaitingSave();
            return;
        }

        var marker = GetCurrentMarker();
        if (marker == null) {
            return;
        }

        // The game of the player starts counting rounds anew. Members who are still checked with each other go on as
        // they are, and the hello of the new player starts a new round once their game has loaded a save (OnHello).
        _memberChecks.Remove(player.Id);
        if (_checkedMembers.Count == 0) {
            ResetCheck();
            _highestCheckKey = 0;
        }

        Chat(Lang.Pick(
            $"{player.Username} is here. Your {SaveWord(marker)} continues once they have loaded a save.",
            $"{player.Username} 来了。等对方也进了存档，你们的{SaveWord(marker)}就能继续。"
        ));
    }

    /// <summary>
    /// Called when a player disconnects, after which the loaded save waits for them again if they are a member of it.
    /// </summary>
    public void OnPlayerDisconnect(ushort id, string username) {
        _hostSaveRequests.Remove(id);
        _hostSaveWaitTold.Remove(id);
        _declinedBy.Remove(id);

        // What they sent while this game was out of the rooms belongs to the session they just left. A player who
        // comes back with the same number would have their old round started again from it, and the next round
        // exchanges their world anyway.
        _updatesOutOfRoom.RemoveAll(update => update.PlayerId == id);

        if (_memberChecks.ContainsKey(id) || _checkedMembers.Contains(id)) {
            PartnerLeft(id, Lang.Pick("left", "离开了"), username);

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
    /// <param name="username">Their name, for a member who is already gone from the players.</param>
    private void PartnerLeft(ushort id, string? how, string? username = null) {
        if (!_memberChecks.TryGetValue(id, out var check) && !_checkedMembers.Contains(id)) {
            return;
        }

        var wasChecked = _checkedMembers.Contains(id);
        var name = check?.Name ?? username ?? (_playerData.TryGetValue(id, out var gone) ? gone.Username : null);

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

        // What they held down here lets go, also as the last of them leaves: the end of the check forgets who stands
        // on a plate or hangs on a ring, but lets go of neither, which then stayed held for good
        ForgetPlatesOf(id);
        ForgetRingHoldsOf(id, name ?? "?");
        if (_checkedMembers.Count == 0) {
            ResetCheck();
        } else {
            // The others are still in the save: only what this member held in the world lets go
            ForgetTrapdoorsOf(id);
            ForgetTollBenchesOf(id);
        }

        // Also for the last of them: the rooms of the maze they were in lay themselves out anew without them
        ForgetLayoutPlaceOf(id);

        // This path does not go through ResetSession, so a cocoon of theirs left standing in the room would stay for
        // good, and this player would be remembered as waiting to be pulled up by someone who is gone - which would
        // quietly stop the next death of the local player from waiting for anyone. With other members still checked,
        // only what is known of this one goes.
        if (_checkedMembers.Count == 0) {
            ResetRescue();
        } else {
            ForgetRescueOf(id);
        }

        OnClothesGrabMemberLeft(id);
        OnPrisonCatchMemberLeft(id);

        if (wasChecked) {
            // The play time that the players played together is kept for the next check
            SaveMarkers();
        }

        // A boss fight that the member was in ends for the local player too
        if (wasChecked && IsInBossFight()) {
            _interruptPending = true;
            _interruptName = name;
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

        // The IDs are handed out again on the next connection, to whoever comes
        _declinedBy.Clear();

        _hostSaveRequests.Clear();
        _hostSaveWaitTold.Clear();
        _hostSaveSends.Clear();
        _updatesOutOfRoom.Clear();
        ForgetJoin();
        if (_joinButton != null) {
            ClearJoin();
            CloseMessage();
            ShowLater(Lang.Pick(
                "The connection to the host was lost before their save arrived.",
                "房主的存档还没传完，连接就断了。"
            ));
        }

        ResetCheck();

        // This path does not go through ResetSession either, so the same cocoon and the same memory of a partner
        // waiting would be left behind, this time by leaving the server rather than by them leaving it
        ResetRescue();
        ForgetClothesGrabMembers();

        // Nobody is left to say how a catch that holds the local player ends
        ReleasePartnerCatch(HeroController.SilentInstance, "the connection to the server was lost", null);

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
    ///
    /// While the loaded save plays a scene that is no room, like the cinematic of a ride, nothing of the save can be
    /// looked up (GetCurrentMarker), so every update would be dropped there as if no save were loaded. One that comes
    /// then waits for the next room instead, in the order it came. A partner who rode right after the local player had
    /// what the passing of time rolled for them reach this game during its own ride, among it whether the seller of
    /// maps was out, and the two saves stayed apart in it for the rest of the session (USER 10-12, "有不一致，我这里
    /// ……不用敲环就在，但是我朋友那边需要敲").
    /// </summary>
    public void OnCoopSaveUpdate(CoopSaveUpdate update) {
        // Not the ones about joining a host from the menu, which read nothing of the loaded save: a host on a ride who
        // held the first answer to a join kept the joining player waiting, and after ten seconds of that they are told
        // nobody answered and offered their save as it is
        if (IsSaveOutOfRoom() && update.Kind is not (CoopSaveUpdateKind.HostSaveRequest or CoopSaveUpdateKind.HostSave
                or CoopSaveUpdateKind.WaitingRoomReady)) {
            if (_updatesOutOfRoom.Count == 0) {
                var scene = SceneUtil.GetCurrentSceneName();
                Logger.Info($"Keeping the updates of other players for the next room while '{scene}' plays");
            }

            _updatesOutOfRoom.Add(update);
            return;
        }

        TakeUpdatesOutOfRoom();
        TakeUpdate(update);
    }

    /// <summary>
    /// Whether the loaded save plays a scene that is no room, like the cinematic of a ride.
    /// </summary>
    private bool IsSaveOutOfRoom() => _sessionSlot >= 0 && !IsInGame();

    /// <summary>
    /// Takes the updates of other players that came while the loaded save played a scene that is no room, once the
    /// local game is in a room again.
    /// </summary>
    private void TakeUpdatesOutOfRoom() {
        if (_updatesOutOfRoom.Count == 0 || IsSaveOutOfRoom()) {
            return;
        }

        var updates = _updatesOutOfRoom.ToList();
        _updatesOutOfRoom.Clear();
        Logger.Info($"Taking {updates.Count} updates of other players that came while out of the rooms");
        foreach (var update in updates) {
            // One that throws must not lose the ones after it
            try {
                TakeUpdate(update);
            } catch (Exception e) {
                Logger.Error($"Could not take an update ({update.Kind}) that came while out of the rooms:\n{e}");
            }
        }
    }

    /// <summary>
    /// Hands an update of another player to what handles its kind.
    /// </summary>
    private void TakeUpdate(CoopSaveUpdate update) {
        if (!_playerData.TryGetValue(update.PlayerId, out var player)) {
            return;
        }

        switch (update.Kind) {
            case CoopSaveUpdateKind.HostSaveRequest:
                OnHostSaveRequest(player, update);
                break;
            case CoopSaveUpdateKind.HostSave:
                OnHostSave(player, update);
                break;
            case CoopSaveUpdateKind.WaitingRoomReady:
                // Handled by the menu rather than here: at this point neither player has a save loaded, so there is
                // nothing about a two-player save to act on yet
                _uiManager.OnPartnerReadyChanged(player.Username, update.Part != 0);
                break;
            case CoopSaveUpdateKind.Hello:
                OnHello(player, update);
                break;
            case CoopSaveUpdateKind.HelloDeclined:
                OnHelloDeclined(player);
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
            case CoopSaveUpdateKind.RingHold:
                OnRingHold(player, update);
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
            case CoopSaveUpdateKind.PrisonCatch:
                OnPrisonCatch(player, update.PartCount);
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
            case CoopSaveUpdateKind.MachinePart:
                OnMachinePart(player, update);
                break;
            case CoopSaveUpdateKind.RoomLayoutPlace:
                OnRoomLayoutPlace(player, update);
                break;
            case CoopSaveUpdateKind.RoomLayout:
                OnRoomLayout(player, update);
                break;
            case CoopSaveUpdateKind.RoomDoorTaken:
                OnRoomDoorTaken(player, update);
                break;
            case CoopSaveUpdateKind.PutBack:
                OnPutBack(player, update);
                break;
            case CoopSaveUpdateKind.RoomClocks:
                OnRoomClocks(player, update);
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
            case CoopSaveUpdateKind.DialRest:
                OnDialRest(player, update);
                break;
            case CoopSaveUpdateKind.PodStrike:
                OnPodStrike(player, update);
                break;
            case CoopSaveUpdateKind.PodOutcome:
                OnPodOutcome(player, update);
                break;
            case CoopSaveUpdateKind.PodProgress:
                OnPodProgress(player, update);
                break;
            case CoopSaveUpdateKind.SharedPurchase:
                OnSharedPurchase(player, update);
                break;
        }
    }

    #endregion

    #region Roster

    /// <summary>
    /// The name of the key or button that gives up on waiting to be pulled back up after a death. Taken from the
    /// binding that is actually in force rather than written out, so that rebinding it no longer leaves every prompt
    /// naming a key that does nothing.
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
    /// Whether showing the line of a held player failed, so it is only logged once.
    /// </summary>
    private bool _holdPromptFailed;

    /// <summary>
    /// Whether the players of the session were taken for the loaded co-op save (<see cref="StartRoster"/>).
    /// </summary>
    private bool _rosterStarted;

    /// <summary>
    /// The slot that the local player joined the host's game with from the menu just now, whose players were taken as
    /// it was aligned, or -1. Its session keeps them rather than starting over with the players on the server, so that
    /// a host who left while the save loaded is still waited for.
    /// </summary>
    private int _freshMarkerSlot = -1;

    /// <summary>
    /// The slot that the local player made a normal save again in this session, which doesn't become a co-op save again
    /// by itself until another save is loaded, or -1.
    /// </summary>
    private int _declinedSlot = -1;

    /// <summary>
    /// Whether the loaded save became a co-op save while its host was playing it, so that the host waits for the
    /// others only at a bench, and not wherever they are until the first check with them.
    /// </summary>
    private bool _holdOnlyAtBench;

    /// <summary>
    /// The players of the session, by ID, whose game turned down the check of this one because they made their save a
    /// normal save (<see cref="OnHelloDeclined"/>), until they say hello from a co-op save again or leave.
    /// </summary>
    private readonly HashSet<ushort> _declinedBy = [];

    /// <summary>
    /// Takes the players of a session of the loaded co-op save: those it was aligned with if it was joined with just
    /// now, and everyone else on the server. A co-op save isn't bound to its players beyond the session, as the user
    /// decided on 2026-10-11 ("下次联机重新记"): the next session takes whoever is in it.
    /// </summary>
    private void StartRoster(CoopSaveMarker marker, int slot) {
        if (_freshMarkerSlot != slot) {
            marker.Members.Clear();
            marker.PartnerKey = "";
            marker.PartnerName = "";
        }

        _freshMarkerSlot = -1;
        UpdateRoster(marker);

        // Nobody to take, like when the player who was joining left while the save loaded: the save is played alone
        // until someone joins, and like a host who was playing when someone joined, it waits for them at a bench
        // rather than where the player stands
        if (marker.Members.Count == 0) {
            _holdOnlyAtBench = true;
        }
        Logger.Info(
            $"The co-op save in slot {slot} is played with " +
            (marker.Members.Count == 0 ? "nobody yet" : string.Join(", ", marker.Members.Select(member => member.Name)))
        );
    }

    /// <summary>
    /// Adds the players who came onto the server to the players of the session.
    /// </summary>
    private void UpdateRoster(CoopSaveMarker marker) {
        // Looked for before the players are sorted, as this runs every frame and a newcomer is rare
        if (!HasNewcomer(marker)) {
            return;
        }

        var changed = false;
        foreach (var player in GetOthersOnServer()) {
            if (player.SaveKey.Length == 0 || IsMember(player, marker)) {
                continue;
            }

            marker.Members.Add(new CoopSaveMember { Key = player.SaveKey, Name = player.Username });
            changed = true;
        }

        if (changed) {
            marker.TakeOverPartner();
        }
    }

    /// <summary>
    /// Whether a player on the server with a save key isn't a member of a paired save yet.
    /// </summary>
    private bool HasNewcomer(CoopSaveMarker marker) {
        foreach (var player in _playerData.Values) {
            if (player.SaveKey.Length > 0 && !IsMember(player, marker)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the save that the local player hosts becomes a co-op save by itself, now that another player is on the
    /// server: a host's save is the world that the others join.
    /// </summary>
    private bool BecomesCoopSave(int slot) {
        return IsHosting?.Invoke() == true && slot != _declinedSlot && _netClient.IsConnected &&
               GetOthersOnServer().Any(player => player.SaveKey.Length > 0);
    }

    /// <summary>
    /// The line of a held player, which says who they wait for. No key stops waiting any more. The players of a session
    /// are whoever is in it this time (2026-10-11), so a held player waits for players who are loading or who come
    /// back, and the key that made the save a normal save was pressed by accident: a host clicked the left stick in
    /// while their partner loaded into the save, and the partner could not move from then on (2026-10-14, the user:
    /// "把不等了删掉"). A player who wants to stop waiting pauses and goes to the menu, where a co-op save chosen with
    /// nobody else on the server becomes a normal save on a second choice.
    /// </summary>
    private void UpdateHoldPrompt() {
        var prompt = _uiManager.CoopPrompt;

        // A held player owns this line, because being unable to move with no idea why is the worst thing this mod
        // can do to someone. The chat line that the hold writes is invisible to anyone playing with chat closed.
        if (!_held || !IsInGame()) {
            prompt.Hide();
            return;
        }

        var heldMarker = GetMarker(global::GameManager.instance.profileID);
        if (heldMarker == null) {
            prompt.Show(Lang.Pick("Waiting for your teammate", "正在等你的队友"));
            return;
        }

        // A player whose game turned the check down, because they made their save a normal save, comes into this one
        // only once they load theirs again
        var declined = MatchMembers(heldMarker)
            .Where(match => match.Player != null && _declinedBy.Contains(match.Player.Id))
            .Select(match => match.Player!.Username)
            .ToList();
        if (declined.Count > 0) {
            var names = JoinNames(declined);
            prompt.Show(Lang.Pick(
                $"{names} made their save a normal save. Wait for them to load it again, or pause and go to the menu",
                $"{names} 把存档变回了普通存档。等对方回主菜单重进存档，或者你暂停回主菜单"
            ));
            return;
        }

        var waitingFor = GetMissingNames(heldMarker);
        prompt.Show(Lang.Pick(
            $"Waiting for {waitingFor}. To stop waiting, pause and go to the menu",
            $"正在等 {waitingFor}。不想等了就暂停，回主菜单"
        ));
    }

    /// <summary>
    /// The game of a player turned down the hello of this one, because its player made their loaded save a normal save
    /// in this session (<see cref="CoopSaveUpdateKind.HelloDeclined"/>). Without being told, the local player would
    /// wait for them with nothing saying why, which is how a host's key pressed by accident left their partner
    /// standing still (2026-10-14).
    /// </summary>
    private void OnHelloDeclined(ClientPlayerData player) {
        var marker = GetCurrentMarker();
        if (marker == null || !IsMember(player, marker) || _checkedMembers.Contains(player.Id) ||
            !_declinedBy.Add(player.Id)) {
            return;
        }

        Logger.Info($"{player.Username} made their save a normal save, so their game turns down the check");
        Chat(Lang.Pick(
            $"{player.Username} made their save a normal save, so you are not in one co-op save now. It becomes one " +
            $"again when {player.Username} goes to the menu and loads it again.",
            $"{player.Username} 把存档变回了普通存档，所以你们现在不在同一个联机存档里。" +
            $"{player.Username} 回主菜单再进一次这个存档，就会重新变成联机存档。"
        ));
    }

    /// <summary>
    /// /coopsave: says whether the loaded save is a co-op save and who plays it in this session, and with "off" makes
    /// it a normal save again.
    /// </summary>
    public void OnCommand(string[] arguments) {
        if (!IsInGame()) {
            Chat(Lang.Pick("Load a save first.", "先进一个存档。"));
            return;
        }

        var slot = global::GameManager.instance.profileID;
        var marker = GetMarker(slot);
        if (arguments.Length > 1 && arguments[1].Equals("off", StringComparison.OrdinalIgnoreCase)) {
            if (marker == null) {
                Chat(Lang.Pick("Your current save isn't a co-op save.", "你当前的存档不是联机存档。"));
                return;
            }

            RemoveLocalPairing(slot);
            Chat(Lang.Pick(
                "Your save is a normal save again. Joining someone's game with it makes it a co-op save again.",
                "你的存档变回普通存档了。以后用它加入别人的游戏时，会重新变成联机存档。"
            ));
            return;
        }

        if (marker == null) {
            Chat(Lang.Pick(
                "Your current save is a normal save. It becomes a co-op save when you join someone's game with it, or " +
                "when someone joins yours.",
                "你当前的存档是普通存档。用它加入别人的游戏，或者有人加入你的游戏时，它会变成联机存档。"
            ));
            return;
        }

        var names = JoinNames(marker.Members.Select(member => member.Name));
        Chat(Lang.Pick(
            $"Your current save is a co-op save, played with {names} this time. Type /coopsave off to make it a " +
            "normal save again.",
            $"你当前的存档是联机存档，这次一起玩的是 {names}。打 /coopsave off 可以把它变回普通存档。"
        ));
    }

    /// <summary>
    /// Makes a local save a normal save again, and lets the player move if it is the loaded save. It doesn't become a
    /// co-op save again by itself in this session.
    /// </summary>
    /// <param name="slot">The slot of the save.</param>
    private void RemoveLocalPairing(int slot) {
        RemoveMarker(slot);
        if (slot == _sessionSlot) {
            _declinedSlot = slot;

            // The save is its player's alone now, and so is a wish that a step of dialogue waits to begin with the
            // partner: the step goes on and takes it (CoopSave.WishRead)
            EndHeldBegin(null, true);
            ReleaseHold(HeroController.instance);
            ResetSession(true);
        }

        Logger.Info($"Save slot {slot} isn't a co-op save anymore");
    }

    /// <summary>
    /// The other players on the server, in the order of their IDs.
    /// </summary>
    private List<ClientPlayerData> GetOthersOnServer() {
        return _playerData.Values.OrderBy(player => player.Id).ToList();
    }

    #endregion

    #region Save menu

    /// <summary>
    /// The save menu of a co-op game. A player who joins someone else's game aligns the save they choose with the
    /// host's save (<see cref="StartJoin"/>). A co-op save opens only with another player on the server: a host waits
    /// for one in the menu, and a player alone can make it a normal save again on a second choice.
    /// </summary>
    private void OnSaveSlotSubmit(
        Action<UnityEngine.UI.SaveSlotButton, BaseEventData> orig,
        UnityEngine.UI.SaveSlotButton self,
        BaseEventData eventData
    ) {
        if (!_bypassSubmit) {
            var joining = false;
            try {
                joining = IsJoiningHost();
                if (joining) {
                    if (!OnJoinSlotSubmit(self, eventData)) {
                        return;
                    }
                } else if (!OnPlainSlotSubmit(self, eventData)) {
                    return;
                }
            } catch (Exception e) {
                Logger.Error($"Could not check whether the save is a co-op save:\n{e}");

                // Joining loads only a save aligned with the host's: the save as it was would load as a co-op save
                // that doesn't follow the host's world
                if (joining) {
                    ShowMessage(Lang.Pick(
                        "Your save couldn't be aligned with the host's, so it wasn't loaded. Choose it again to try " +
                        "again.",
                        "没能把你的存档按房主的存档对齐，所以没有进入。再选一次这个存档重试。"
                    ));
                    return;
                }
            }
        }

        orig(self, eventData);
    }

    /// <summary>
    /// The choice of a save by a player who doesn't join someone else's game: a host, or a player alone.
    /// </summary>
    /// <returns>Whether the game's own choice of the save goes on.</returns>
    private bool OnPlainSlotSubmit(UnityEngine.UI.SaveSlotButton button, BaseEventData eventData) {
        // A choice starts over: an aligned save of an earlier one that didn't load must not load in its place
        _pendingJoin = null;
        var slot = button.SaveSlotIndex;
        if (slot != _soloWarnedSlot) {
            _soloWarnedSlot = -1;
        }

        var marker = GetMarker(slot);
        if (IsEmptySlot(button)) {
            // The save of a co-op slot is gone, like after its files were deleted, so a new game there starts as a
            // normal save
            if (marker != null) {
                RemoveMarker(slot);
            }

            return true;
        }

        return marker == null || !HasSave(button) || TryOpen(button, eventData, marker);
    }

    /// <summary>
    /// The co-op save that a player alone was told about, which choosing again makes a normal save, or -1.
    /// </summary>
    private int _soloWarnedSlot = -1;

    /// <summary>
    /// Whether a co-op save opens now, which it does with another player on the server: a co-op save is for playing
    /// together, at least two players (the user's rule of 2026-10-11). A host opens their game first and waits in the
    /// menu, and the save loads once someone has joined. A player alone hears so, and choosing the save again makes it a
    /// normal save and opens it.
    /// </summary>
    private bool TryOpen(UnityEngine.UI.SaveSlotButton button, BaseEventData eventData, CoopSaveMarker marker) {
        if (_netClient.IsConnected && GetOthersOnServer().Count > 0) {
            return true;
        }

        var slot = button.SaveSlotIndex;
        if (_uiManager.IsSelectingHostSave) {
            if (!_uiManager.StartHostBeforeSave()) {
                ShowMessage(Lang.Pick("Could not open your game.", "没能开好你的游戏。"));
                return false;
            }

            _menuActionToken++;
            _waitingButton = button;
            _waitingEventData = eventData;
            _waitingMarker = marker;
            Logger.Info($"Hosting before co-op save in slot {slot} loads, waiting for someone to join");
            CoopMessageBox.ShowWait(
                Lang.Pick(
                    "Your game is open. Your co-op save loads once someone has joined.",
                    "你的游戏已经开好了，有人加入后这个联机存档就会进入。"
                ),
                Lang.Pick("Stop hosting", "不开了"),
                OnWaitMessageClosed
            );
            return false;
        }

        if (slot == _soloWarnedSlot) {
            _soloWarnedSlot = -1;
            RemoveMarker(slot);
            Logger.Info($"Save slot {slot} was made a normal save to be played alone");
            return true;
        }

        _soloWarnedSlot = slot;
        ShowMessage(Lang.Pick(
            "This is a co-op save, which is played with at least two players: host a game and wait for someone to " +
            "join, or join someone's game with it. Choose it again to make it a normal save and play it alone.",
            "这是联机存档，要至少两个人一起玩：开房等人加入，或者用它加入别人的游戏。" +
            "再选一次这个存档，就把它变回普通存档一个人玩。"
        ));
        return false;
    }

    /// <summary>
    /// The host chose to stop waiting while their save waited for the partner, so they stop hosting.
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
                ShowMessage(Lang.Pick(
                    "Your game closed before anyone joined. Choose the save again to host again.",
                    "还没人加入，你的游戏就关了。再选一次存档就重新开房。"
                ));
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
        Logger.Info("Someone joined, loading the co-op save");

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
    /// Shows a message about two-player saves (see <see cref="CoopMessageBox"/>).
    /// </summary>
    private static void ShowMessage(string text, Action? onClose = null) {
        CoopMessageBox.Show(text, onClose);
    }

    /// <summary>
    /// Closes the message about two-player saves, if one is up, which also gives input back to the menu. What it was
    /// shown with doesn't run: every caller has let go of what that was about first.
    /// </summary>
    private static void CloseMessage() {
        CoopMessageBox.Close();
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
            Logger.Info($"Save slot {slot} was cleared, so it isn't a co-op save anymore");
        }
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
        // A loop rather than a query with a closure, as this is asked for every player every frame
        foreach (var member in marker.Members) {
            if ((player.SaveKey.Length > 0 && player.SaveKey == member.Key) ||
                (member.Name.Length > 0 && player.Username == member.Name)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The player on the server who is each member of a paired save, by the index of the member, from the last
    /// <see cref="MatchPlayers"/>; kept rather than made anew, as the members are matched a few times every frame.
    /// </summary>
    private readonly List<ClientPlayerData?> _matchedPlayers = [];

    /// <summary>
    /// The IDs of the players that <see cref="MatchPlayers"/> matched to a member so far.
    /// </summary>
    private readonly HashSet<ushort> _matchTaken = [];

    /// <summary>
    /// Matches each member of a paired save with the player on the server who is them, or null: the player with their
    /// save key, or else one with their name. No player stands for two members. The list is reused by the next call,
    /// so it is read straight away.
    /// </summary>
    private List<ClientPlayerData?> MatchPlayers(CoopSaveMarker marker) {
        var players = _matchedPlayers;
        players.Clear();
        _matchTaken.Clear();
        for (var i = 0; i < marker.Members.Count; i++) {
            players.Add(null);
        }

        for (var i = 0; i < players.Count; i++) {
            var key = marker.Members[i].Key;
            if (key.Length == 0) {
                continue;
            }

            foreach (var player in _playerData.Values) {
                if (player.SaveKey == key && _matchTaken.Add(player.Id)) {
                    players[i] = player;
                    break;
                }
            }
        }

        for (var i = 0; i < players.Count; i++) {
            var name = marker.Members[i].Name;
            if (players[i] != null || name.Length == 0) {
                continue;
            }

            foreach (var player in _playerData.Values) {
                if (player.Username == name && _matchTaken.Add(player.Id)) {
                    players[i] = player;
                    break;
                }
            }
        }

        return players;
    }

    /// <summary>
    /// Each member of a paired save with the player on the server who is them, or null: the player with their save key,
    /// or else one with their name. No player stands for two members.
    /// </summary>
    private List<(CoopSaveMember Member, ClientPlayerData? Player)> MatchMembers(CoopSaveMarker marker) {
        var players = MatchPlayers(marker);
        var matches = new List<(CoopSaveMember Member, ClientPlayerData? Player)>(players.Count);
        for (var i = 0; i < players.Count; i++) {
            matches.Add((marker.Members[i], players[i]));
        }

        return matches;
    }

    /// <summary>
    /// The members of a paired save that are on the server, in the order of the pairing.
    /// </summary>
    private List<ClientPlayerData> FindMembers(CoopSaveMarker marker) {
        var members = new List<ClientPlayerData>();
        foreach (var player in MatchPlayers(marker)) {
            if (player != null) {
                members.Add(player);
            }
        }

        return members;
    }

    /// <summary>
    /// Whether every member of a paired save is on the server.
    /// </summary>
    private bool HasAllMembers(CoopSaveMarker marker) {
        foreach (var player in MatchPlayers(marker)) {
            if (player == null) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether every member of a paired save is on the server and was checked with.
    /// </summary>
    private bool IsGroupComplete(CoopSaveMarker marker, List<ClientPlayerData> members) {
        return members.Count == marker.Members.Count && !HasUncheckedMember(members);
    }

    /// <summary>
    /// Whether one of the given members on the server wasn't checked with yet. A loop rather than a query, as this is
    /// asked a few times every frame.
    /// </summary>
    private bool HasUncheckedMember(List<ClientPlayerData> members) {
        foreach (var member in members) {
            if (!_checkedMembers.Contains(member.Id)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The first of the given members on the server who was checked with, or null.
    /// </summary>
    private ClientPlayerData? FirstCheckedMember(List<ClientPlayerData> members) {
        foreach (var member in members) {
            if (_checkedMembers.Contains(member.Id)) {
                return member;
            }
        }

        return null;
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
    /// What a co-op save is called in messages, however many play it.
    /// </summary>
    private static string SaveWord(CoopSaveMarker? marker) {
        return Lang.Pick("co-op save", "联机存档");
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
    private static string GetSlotKey(int slot) {
        // Kept, as the updates of every frame ask for it and each time made a new string with the slot boxed
        var folder = GetSaveFolderName();
        if (folder != _slotKeysFolder) {
            _slotKeysFolder = folder;
            SlotKeys.Clear();
        }

        if (!SlotKeys.TryGetValue(slot, out var key)) {
            key = $"{folder}/{slot}";
            SlotKeys[slot] = key;
        }

        return key;
    }

    /// <summary>
    /// The keys of the save slots made so far, for the folder <see cref="_slotKeysFolder"/>.
    /// </summary>
    private static readonly Dictionary<int, string> SlotKeys = new();

    /// <summary>
    /// The folder of the save files that <see cref="SlotKeys"/> were made with.
    /// </summary>
    private static string? _slotKeysFolder;

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
    /// Sends an update to each member that the saves were checked with who is in the room of the local player, and to
    /// nobody else: what only matters in a room doesn't go to the members elsewhere.
    /// </summary>
    private void SendToRoomMembers(CoopSaveUpdate update) {
        var sent = false;
        foreach (var member in GetCheckedMembers()) {
            if (!member.IsInLocalScene) {
                continue;
            }

            if (sent) {
                SendCopy(update, member.Id);
                continue;
            }

            update.TargetId = member.Id;
            Send(update);
            sent = true;
        }
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
            CoopSaveUpdateKind.Interaction or CoopSaveUpdateKind.WishTurnIn or CoopSaveUpdateKind.SharedPurchase) {
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
