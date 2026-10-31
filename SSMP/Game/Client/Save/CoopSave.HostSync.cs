using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using GlobalEnums;
using MonoMod.RuntimeDetour;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine.EventSystems;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Joining the host's world with any save (the user's rules of 2026-10-11): a save is no longer bound to the players it
/// was paired with. A player who joins someone's game and picks a save in the menu gets the host's save sent over, and
/// their save is aligned with it before it loads (<see cref="CoopHostSync"/>): the host's world, story, beaten bosses
/// and wishes, the important things that the host found and they haven't, and everything else their own. The save is a
/// co-op save from then on, which opens only with another player on the server. An empty slot starts with the host's
/// world and a new character, with the opening of a new game. During play nothing of this runs: each player picks up
/// their own, as before.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The embedded rules of what a joining save takes from the host's.
    /// </summary>
    private const string HostSyncFilePath = "SSMP.Resource.coop-host-sync.json";

    /// <summary>
    /// How many bytes of the host's save go in one update, which keeps every update well inside one packet.
    /// </summary>
    private const int HostSavePartSize = 900;

    /// <summary>
    /// How many parts of the host's save go out at a time, and how long, in seconds, the host waits before the next
    /// ones. Paced by time rather than by frame: everything sent between two ticks of the network goes in one packet,
    /// which is written with a 16-bit length, and a packet too long to write loses everything in it, the updates of
    /// the rest of the game included. A game running fast on a slow connection put many frames' worth in one.
    /// </summary>
    private const int HostSavePartsPerSend = 8;

    private const float HostSaveSendInterval = 0.05f;

    /// <summary>
    /// How long, in seconds, a joining player waits for a host to answer that it has the save, before they hear that
    /// nobody on the server hosts the game.
    /// </summary>
    private const float HostAnswerTime = 10f;

    /// <summary>
    /// How long, in seconds, a further press on the save that is being entered goes on with its aligned save rather
    /// than starting over, like the presses that break the black threads of a save in the last act, of which only the
    /// last enters it.
    /// </summary>
    private const float PendingJoinKeepTime = 30f;

    /// <summary>
    /// What <see cref="CoopSaveUpdate.FsmName"/> says about a part of the host's save that is compressed.
    /// </summary>
    private const string HostSaveGzip = "gzip";

    /// <summary>
    /// What <see cref="CoopSaveUpdate.StateName"/> says on the host's answer that their save won't come when the
    /// host's game is older than the mod needs and lacks what the mod takes the save with. A joining player of an
    /// earlier version, which reads no reason, hears that the save won't come.
    /// </summary>
    private const string HostGameTooOld = "GameTooOld";

    /// <summary>
    /// The game mode of a save in which a death ends it (PlayerData.permadeathMode).
    /// </summary>
    private const int PermadeathMode = 1;

    /// <summary>
    /// The scene of the opening of a new game. It plays with the save that the game made for the new game before it
    /// (GameManager.StartNewGame), whose world nothing has touched yet.
    /// </summary>
    private const string NewGameOpeningScene = "Opening_Sequence";

    /// <summary>
    /// Whether the local game hosts the server, which only the host's own game knows.
    /// </summary>
    public Func<bool>? IsHosting { get; set; }

    /// <summary>
    /// The rules of what a joining save takes from the host's, loaded when first needed.
    /// </summary>
    private CoopHostSyncData? _hostSyncData;

    /// <summary>
    /// Hook that puts the aligned save in place of the one the game loads.
    /// </summary>
    private Hook? _setLoadedGameDataHook;

    /// <summary>
    /// Hook that starts a new save, aligned with the host's, with the opening of a new game.
    /// </summary>
    private Hook? _readyForRespawnHook;

    /// <summary>
    /// The slot of a new save, aligned with the host's, that is loading and starts with the opening of a new game, or
    /// -1. Set as its data is put in place, taken by the end of that load (<see cref="OnReadyForRespawn"/>), and
    /// dropped as any scene comes in, so that it never reaches a later load.
    /// </summary>
    private int _openingSlot = -1;

    #region Host

    /// <summary>
    /// The requests for the host's save that wait for the host to be in a moment it could save in, by the player who
    /// asked, with the ID of their request.
    /// </summary>
    private readonly Dictionary<ushort, ulong> _hostSaveRequests = new();

    /// <summary>
    /// The parts of the host's save that are still to go out, to whom, in order.
    /// </summary>
    private readonly Queue<CoopSaveUpdate> _hostSaveSends = new();

    /// <summary>
    /// When the next parts of the host's save may go out, in unscaled seconds.
    /// </summary>
    private float _hostSaveNextSend;

    /// <summary>
    /// What each player whose request waits was last told about what keeps the host's save from coming.
    /// </summary>
    private readonly Dictionary<ushort, HostSaveWait> _hostSaveWaitTold = new();

    /// <summary>
    /// The name of the scene that is active, kept as it changes (<see cref="OnActiveSceneChanged"/>), so that asking
    /// it every frame while requests for the host's save wait makes no new string, or null before the first change.
    /// </summary>
    private string? _activeSceneName;

    #endregion

    #region Joining player

    /// <summary>
    /// The ID of the request for the host's save that the local player waits for, or 0.
    /// </summary>
    private ulong _hostSaveRequestId;

    /// <summary>
    /// Whether a host answered the request that the local player waits for.
    /// </summary>
    private bool _hostAnswered;

    /// <summary>
    /// The player whose parts of the save are being put together, or null before the first part.
    /// </summary>
    private ushort? _hostSaveSender;

    /// <summary>
    /// The parts of the host's save that arrived for the request, by part.
    /// </summary>
    private readonly Dictionary<ushort, byte[]> _hostSaveParts = new();

    /// <summary>
    /// The number of the message that the local player waits for the host's save with
    /// (<see cref="CoopMessageBox.ShowWait"/>), and what it says keeps the save from coming.
    /// </summary>
    private int _joinMessage;

    private HostSaveWait _joinWait;

    /// <summary>
    /// The slot that the local player chose to join with, while the host's save is on its way.
    /// </summary>
    private UnityEngine.UI.SaveSlotButton? _joinButton;

    private BaseEventData? _joinEventData;

    /// <summary>
    /// The slot whose warning the local player saw, and the warning, so that choosing it again goes ahead when the
    /// warning would say the same, or -1.
    /// </summary>
    private int _warnedSlot = -1;

    private string? _warnedText;

    /// <summary>
    /// The slot that the local player chose while nobody answered as the host, which choosing again loads as it is,
    /// or -1.
    /// </summary>
    private int _noHostSlot = -1;

    /// <summary>
    /// The aligned save that the game is loading, which <see cref="OnSetLoadedGameData"/> puts in place of the one it
    /// loads.
    /// </summary>
    private PendingJoin? _pendingJoin;

    /// <summary>
    /// What the local player hears about the alignment once the aligned save has loaded, or null.
    /// </summary>
    private string? _joinSummary;

    /// <summary>
    /// An aligned save waiting to load.
    /// </summary>
    private sealed class PendingJoin {
        public int Slot { get; init; }

        public JObject Merged { get; init; } = new();

        public CoopHostSyncReport Report { get; init; } = new();

        public string HostName { get; init; } = "";

        /// <summary>
        /// When it was aligned, in unscaled seconds.
        /// </summary>
        public float Time { get; init; }

        /// <summary>
        /// Whether the save starts as a new game does, with the opening: the slot was empty and the alignment left the
        /// place to wake at where a new save has it, so the host has none yet either (<see cref="GetWakePlace"/>).
        /// </summary>
        public bool Opening { get; init; }
    }

    #endregion

    /// <summary>
    /// What keeps the host's save from coming, which the host's answer to a request carries in
    /// <see cref="CoopSaveUpdate.Part"/>, with a part count of 0, so that the player who waits sees what for. Sent
    /// again whenever it changes.
    /// </summary>
    private enum HostSaveWait : ushort {
        /// <summary>
        /// Nothing: the save comes at once.
        /// </summary>
        None = 0,

        /// <summary>
        /// The host hasn't loaded a save.
        /// </summary>
        InMenu = 1,

        /// <summary>
        /// The host's game changes rooms, loads, or writes a save: a moment.
        /// </summary>
        Busy = 2,

        /// <summary>
        /// The host's game could not take its save, which ends the request.
        /// </summary>
        Failed = 3
    }

    /// <summary>
    /// Registers the hook that loads aligned saves, and sends the host's save from every frame.
    /// </summary>
    private void RegisterHostSyncHooks() {
        _setLoadedGameDataHook = CreateHook(
            typeof(global::GameManager).GetMethod(
                "SetLoadedGameData", InstanceFlags, null, [typeof(SaveGameData), typeof(int)], null
            ),
            new Action<Action<global::GameManager, SaveGameData, int>, global::GameManager, SaveGameData, int>(
                OnSetLoadedGameData
            )
        );

        _readyForRespawnHook = CreateHook(
            typeof(global::GameManager).GetMethod("ReadyForRespawn", InstanceFlags, null, [typeof(bool)], null),
            new Action<Action<global::GameManager, bool>, global::GameManager, bool>(OnReadyForRespawn)
        );

        // The menu included: the host answers from there, and sends from the opening of a new game, where there is no
        // hero whose frames would do it
        MonoBehaviourUtil.Instance.OnUpdateEvent += OnHostSaveUpdate;
    }

    /// <summary>
    /// The rules of what a joining save takes from the host's.
    /// </summary>
    private CoopHostSyncData? GetHostSyncData() {
        if (_hostSyncData != null) {
            return _hostSyncData;
        }

        _hostSyncData = FileUtil.LoadObjectFromEmbeddedJson<CoopHostSyncData>(HostSyncFilePath);
        if (_hostSyncData == null) {
            Logger.Error("Could not load what a joining save takes from the host's save");
        }

        return _hostSyncData;
    }

    #region Host: sending the save

    /// <summary>
    /// A player who chose a save in the menu asks for the host's save. Only the host's game answers: at once, that it
    /// has the save and what keeps it from coming, if anything, and with the save once nothing does.
    /// </summary>
    private void OnHostSaveRequest(ClientPlayerData player, CoopSaveUpdate update) {
        if (IsHosting?.Invoke() != true) {
            return;
        }

        // A part count of none: the answer of a host, which lets a player on a server that nobody hosts hear so
        // rather than wait for good, and lets a host still in the menu be told apart from no host. It goes out even
        // when what keeps the save can't be told, or the player would hear after ten seconds that nobody hosts.
        HostSaveWait wait;
        try {
            wait = GetHostSaveWait();
        } catch (Exception e) {
            Logger.Error($"Could not tell what keeps the host's save from coming:\n{e}");
            wait = HostSaveWait.None;
        }

        SendHostSaveWait(player.Id, update.Key, wait);
        _hostSaveRequests[player.Id] = update.Key;
        _hostSaveWaitTold[player.Id] = wait;
        Logger.Info($"{player.Username} asks for the host's save to join with ({update.Key}), kept by: {wait}");
    }

    /// <summary>
    /// The answer to a request for the host's save that carries no part: that this game hosts, and what keeps the save
    /// from coming, or that it could not be taken, with why when that is known.
    /// </summary>
    private void SendHostSaveWait(ushort id, ulong request, HostSaveWait wait, string reason = "") {
        Send(new CoopSaveUpdate {
            TargetId = id, Kind = CoopSaveUpdateKind.HostSave, Key = request, Part = (ushort) wait, StateName = reason
        });
    }

    /// <summary>
    /// Why the host's save couldn't be taken, for the players who wait for it: <see cref="HostGameTooOld"/> when the
    /// host's game is older than the mod needs and lacks what the mod uses, which the host hears too, or nothing.
    /// </summary>
    private static string TellWhyHostSaveFailed(Exception e) {
        if (!GameVersion.IsTooOldFor(e)) {
            return "";
        }

        Chat(Lang.Pick(
            "Your game is older than this mod needs, so your save can't be sent and your partner can't join. Update " +
            "the game to the latest version, then try again.",
            "你的游戏版本太旧，存档发不出去，队友进不来。请把游戏更新到最新版再试。"
        ));
        return HostGameTooOld;
    }

    /// <summary>
    /// Sends the host's save and tells the players who wait for it what keeps it from coming. Its own try: a player
    /// waiting in the menu for the host's save must get it even when the rest of the game's frame fails, or hear that
    /// it won't come.
    /// </summary>
    private void OnHostSaveUpdate() {
        try {
            UpdateHostSaveRequests();
        } catch (Exception e) {
            Logger.Error($"Could not send the host's save:\n{e}");
            FailHostSaveRequests(e);
        }
    }

    /// <summary>
    /// Tells every player who waits for the host's save, whose request waits or whose parts are still to go, that it
    /// won't come, and forgets them all, so that a failure can't leave anyone waiting for good, nor repeat every frame.
    /// </summary>
    private void FailHostSaveRequests(Exception e) {
        // The parts first: a request that waits is always newer than the parts of an earlier one to the same player
        var failed = new Dictionary<ushort, ulong>();
        foreach (var part in _hostSaveSends) {
            failed[part.TargetId] = part.Key;
        }

        foreach (var request in _hostSaveRequests) {
            failed[request.Key] = request.Value;
        }

        _hostSaveRequests.Clear();
        _hostSaveWaitTold.Clear();
        _hostSaveSends.Clear();
        if (failed.Count == 0) {
            return;
        }

        var reason = "";
        try {
            reason = TellWhyHostSaveFailed(e);
        } catch (Exception reasonException) {
            Logger.Error($"Could not tell why the host's save won't come:\n{reasonException}");
        }

        foreach (var request in failed) {
            try {
                SendHostSaveWait(request.Key, request.Value, HostSaveWait.Failed, reason);
            } catch (Exception sendException) {
                Logger.Error($"Could not tell a player that the host's save won't come:\n{sendException}");
            }
        }
    }

    /// <summary>
    /// Sends the host's save to the players who asked for it once nothing keeps it from coming, tells them what does
    /// while something does, and sends the parts that are still to go.
    /// </summary>
    private void UpdateHostSaveRequests() {
        var now = UnityEngine.Time.unscaledTime;
        if (_hostSaveSends.Count > 0 && now >= _hostSaveNextSend) {
            _hostSaveNextSend = now + HostSaveSendInterval;
            for (var i = 0; i < HostSavePartsPerSend && _hostSaveSends.Count > 0; i++) {
                var part = _hostSaveSends.Dequeue();
                if (_playerData.ContainsKey(part.TargetId)) {
                    Send(part);
                }
            }
        }

        if (_hostSaveRequests.Count == 0) {
            return;
        }

        // Looked over first, so that nothing is made every frame while requests wait
        List<ushort>? gone = null;
        foreach (var id in _hostSaveRequests.Keys) {
            if (!_playerData.ContainsKey(id)) {
                (gone ??= []).Add(id);
            }
        }

        if (gone != null) {
            foreach (var id in gone) {
                _hostSaveRequests.Remove(id);
                _hostSaveWaitTold.Remove(id);
            }
        }

        if (_hostSaveRequests.Count == 0) {
            return;
        }

        var wait = GetHostSaveWait();
        if (wait != HostSaveWait.None) {
            TellHostSaveWait(wait);
            return;
        }

        byte[] bytes;
        bool compressed;
        try {
            var json = TakeHostSave();
            (bytes, compressed) = Pack(json);
            Logger.Info(
                $"Sending the host's save: {json.Length} characters, {bytes.Length} bytes" +
                (compressed ? " compressed" : "")
            );
        } catch (Exception e) {
            Logger.Error($"Could not take the host's save to send:\n{e}");

            // The players who wait hear it, and why when that is known, rather than wait for good
            var reason = TellWhyHostSaveFailed(e);
            foreach (var request in _hostSaveRequests) {
                SendHostSaveWait(request.Key, request.Value, HostSaveWait.Failed, reason);
            }

            _hostSaveRequests.Clear();
            _hostSaveWaitTold.Clear();
            return;
        }

        var partCount = (bytes.Length + HostSavePartSize - 1) / HostSavePartSize;
        foreach (var request in _hostSaveRequests) {
            for (var part = 0; part < partCount; part++) {
                var length = System.Math.Min(HostSavePartSize, bytes.Length - part * HostSavePartSize);
                var chunk = new byte[length];
                Array.Copy(bytes, part * HostSavePartSize, chunk, 0, length);
                _hostSaveSends.Enqueue(new CoopSaveUpdate {
                    TargetId = request.Key,
                    Kind = CoopSaveUpdateKind.HostSave,
                    Key = request.Value,
                    Part = (ushort) part,
                    PartCount = (ushort) partCount,
                    FsmName = compressed ? HostSaveGzip : "",
                    Bytes = chunk
                });
            }
        }

        _hostSaveRequests.Clear();
        _hostSaveWaitTold.Clear();
    }

    /// <summary>
    /// Tells the players whose requests wait what keeps the host's save from coming, each once for as long as that
    /// stays the same.
    /// </summary>
    private void TellHostSaveWait(HostSaveWait wait) {
        var told = 0;
        foreach (var request in _hostSaveRequests) {
            if (_hostSaveWaitTold.TryGetValue(request.Key, out var last) && last == wait) {
                continue;
            }

            SendHostSaveWait(request.Key, request.Value, wait);
            _hostSaveWaitTold[request.Key] = wait;
            told++;
        }

        if (told > 0) {
            Logger.Info($"Told {told} players who wait for the host's save what keeps it: {wait}");
        }
    }

    /// <summary>
    /// What keeps the host's game from taking its save now, or nothing: the menu, a room change or a load, in which
    /// the objects of the room that taking the save puts into the scene data come and go, and the game writing a save
    /// of its own from the same data. Not a cutscene, a talk, a death, a player down or the pause menu, which it used
    /// to wait through too, for a moment that the game could save in, like <see cref="CopySave"/> (USER 10-22: someone
    /// stuck on the wait for the host's save; "就不能无论什么动画都不等待吗"). A host held on the bench at the start of a
    /// session can pause, a host who started a new game watches its opening, and a host whose partner dropped plays on
    /// to the next bench, where they could talk or go down as the partner comes back.
    ///
    /// Nor the start of the last act, until the host wakes in it: the game sets the act up as the save loads, from the
    /// menu, before any room (GameManager.StartAct3 in RunContinueGame), and the host wakes only once the scene of their
    /// waking plays out, which the start of the session holds back until everyone has arrived. Waiting for it, the host
    /// waited for the joining player, who waited for the host's save, for good (USER 10-31: "卡在接收存档了"). A player
    /// who joins then wakes in the last act as the host does, from the start (their merged save has it queued).
    ///
    /// What the host's game writes after the save is taken reaches the joining player at the check of their arrival
    /// if it is a story flag, a wish or an object of the world, and the rest at their next join. That is little: a
    /// session starts with the host held on the bench until everyone has arrived, and boss fights wait for every
    /// player on the server to be in the room. Only a host who already plays when someone connects can be in the
    /// middle of a fight, in which the save would be taken before it ends whether this waited for cutscenes or not.
    /// </summary>
    private HostSaveWait GetHostSaveWait() {
        // The quiet getters: the others search the scenes and log an error in every frame without one, like the frames
        // between quitting to the menu and the menu
        var gameManager = global::GameManager.UnsafeInstance;
        if (gameManager == null) {
            return HostSaveWait.InMenu;
        }

        var scene = _activeSceneName ?? SceneUtil.GetCurrentSceneName();
        if (gameManager.GameState is GameState.MAIN_MENU or GameState.INACTIVE or GameState.PRIMER ||
            scene is "Menu_Title" or "Quit_To_Menu" or "Pre_Menu_Intro") {
            return HostSaveWait.InMenu;
        }

        var playerData = PlayerData.instance;
        if (playerData == null || IsGameSaving() || gameManager.IsInSceneTransition ||
            gameManager.GameState is GameState.LOADING or GameState.ENTERING_LEVEL or GameState.EXITING_LEVEL) {
            return HostSaveWait.Busy;
        }

        // A scene that is no room, like an opening or the cinematic of a ride, has no hero to wait for
        if (IsSceneWithoutRoom(scene)) {
            return HostSaveWait.None;
        }

        var hero = HeroController.UnsafeInstance;
        return hero == null || hero.cState.transitioning ? HostSaveWait.Busy : HostSaveWait.None;
    }

    /// <summary>
    /// Whether a scene is no room of the world, like an opening or the cinematic of a ride: the game's own cinematic
    /// scenes, which SSMP's list of scenes without play lacks two of.
    /// </summary>
    private static bool IsSceneWithoutRoom(string scene) {
        return SceneUtil.IsNonGameplayScene(scene) || scene is "Opening_Sequence_Act3" or "Cinematic_Submarine_travel";
    }

    /// <summary>
    /// Whether the game is writing a save now. It writes the save on a thread of its own from the same scene data that
    /// taking the host's save puts the objects of the room into, and changing it under the writer could fail that save.
    /// </summary>
    private static bool IsGameSaving() {
        return SaveDataUtility._runningSaveTask is { IsCompleted: false } ||
               SaveDataUtility._saveTaskQueue is { Count: > 0 };
    }

    /// <summary>
    /// The host's save as the game would save it now, as JSON: the objects of the room the host is in are saved into
    /// the scene data first, like the game does at every room change, and nothing is written to disk. Not in a scene
    /// that is no room, like the opening of a new game, which has no such objects, while the next room may be loading
    /// behind it.
    /// </summary>
    private static string TakeHostSave() {
        var gameManager = global::GameManager.instance;
        if (!IsSceneWithoutRoom(SceneUtil.GetCurrentSceneName())) {
            gameManager.SaveLevelState();
        }

        return SaveDataUtility.SerializeSaveData(new SaveGameData(gameManager.playerData, gameManager.sceneData), null);
    }

    /// <summary>
    /// The bytes of a save to send: compressed, or as they are if compressing fails.
    /// </summary>
    private static (byte[] Bytes, bool Compressed) Pack(string json) {
        var raw = Encoding.UTF8.GetBytes(json);
        try {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal)) {
                gzip.Write(raw, 0, raw.Length);
            }

            return (output.ToArray(), true);
        } catch (Exception e) {
            Logger.Warn($"Could not compress the host's save, sending it as it is:\n{e}");
            return (raw, false);
        }
    }

    private static string Unpack(byte[] bytes, bool compressed) {
        if (!compressed) {
            return Encoding.UTF8.GetString(bytes);
        }

        using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var reader = new StreamReader(input, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    #endregion

    #region Joining player: the menu

    /// <summary>
    /// Whether the local player chooses a save to join someone else's game with: connected to a server that the local
    /// game doesn't host, with another player on it.
    /// </summary>
    private bool IsJoiningHost() {
        return _netClient.IsConnected && IsHosting?.Invoke() != true && GetOthersOnServer().Count > 0;
    }

    /// <summary>
    /// The choice of a save to join the host's game with: what <see cref="OnSaveSlotSubmit"/> does when the local player
    /// joins. A press on the save that is being entered goes on with it, a press that only breaks the black threads of a
    /// save in the last act breaks them, a save chosen again after nobody answered as the host loads as it is, and any
    /// other choice asks for the host's save.
    /// </summary>
    /// <returns>Whether the game's own choice of the save goes on.</returns>
    private bool OnJoinSlotSubmit(UnityEngine.UI.SaveSlotButton button, BaseEventData eventData) {
        var slot = button.SaveSlotIndex;
        if (_pendingJoin is { } pending && pending.Slot == slot &&
            UnityEngine.Time.unscaledTime - pending.Time < PendingJoinKeepTime) {
            // A save enters through the game's own choice, which can hold it back, like a press that is too soon after
            // the last black thread broke. An empty slot was handed to the game to load already, and a press on it now
            // would start a new game.
            return HasSave(button);
        }

        // A choice starts over: an aligned save of an earlier one that didn't load must not load in its place
        _pendingJoin = null;
        if (HasSave(button) && button.saveStats?.IsBlackThreadInfected == true && button.blackThreadImpactsLeft > 0) {
            return true;
        }

        if (slot == _noHostSlot) {
            _noHostSlot = -1;
            Logger.Info($"Loading save slot {slot} as it is, since nobody answered as the host");
            return OnPlainSlotSubmit(button, eventData);
        }

        StartJoin(button, eventData);
        return false;
    }

    /// <summary>
    /// The local player chose a save to join the host's game with: the host's save is asked for, and the chosen save
    /// is aligned with it once it is here. A save that was warned about is asked for again too, so that it is aligned
    /// with the host's world as it is now, of the host the local player is with now.
    /// </summary>
    private void StartJoin(UnityEngine.UI.SaveSlotButton button, BaseEventData eventData) {
        var slot = button.SaveSlotIndex;
        if (!HasSave(button) && !IsEmptySlot(button)) {
            ShowMessage(Lang.Pick(
                "This save slot can't be read, so it can't join the host's game.",
                "这个存档槽读不出来，没法用它加入房主的游戏。"
            ));
            return;
        }

        if (slot != _warnedSlot) {
            _warnedSlot = -1;
            _warnedText = null;
        }

        _menuActionToken++;
        _joinButton = button;
        _joinEventData = eventData;
        var request = _hostSaveRequestId = NewHostSaveRequestId();
        _hostAnswered = false;
        _hostSaveSender = null;
        _hostSaveParts.Clear();
        _joinWait = HostSaveWait.None;
        SendToMembers(new CoopSaveUpdate { Kind = CoopSaveUpdateKind.HostSaveRequest, Key = request });
        Logger.Info($"Asked for the host's save to join with save slot {slot} ({request})");
        _joinMessage = CoopMessageBox.ShowWait(
            GetJoinWaitText(HostSaveWait.None),
            Lang.Pick("Stop waiting", "不等了"),
            OnJoinMessageClosed
        );

        // On a server that nobody on it hosts, like one run on its own, there is no host's world to join
        RunInMenuAfter(HostAnswerTime, () => {
            if (_hostSaveRequestId != request || _hostAnswered || _joinButton == null) {
                return;
            }

            ClearJoin();
            CloseMessage();
            _noHostSlot = slot;
            Logger.Info($"Nobody answered as the host for save slot {slot} ({request})");
            ShowLater(Lang.Pick(
                "Nobody answered as the host: nobody on this server seems to host the game, like on a server that runs " +
                "on its own. Choose this save again to load it as it is, without aligning it with anyone's save.",
                "没有人作为房主回应：这个服务器上好像没有开房的人（比如连的是单独运行的服务器）。" +
                "再选一次这个存档，就按原样进入，不和任何人的存档对齐。"
            ));
        });
    }

    /// <summary>
    /// What the wait for the host's save says, with what keeps it from coming when the host said so.
    /// </summary>
    private static string GetJoinWaitText(HostSaveWait wait) {
        var getting = Lang.Pick("Getting the host's save.", "正在接收房主的存档。");
        var why = wait switch {
            HostSaveWait.InMenu => Lang.Pick(
                "The host hasn't loaded a save yet. It comes once they have.",
                "房主还没进存档，进了存档就会传过来。"
            ),
            HostSaveWait.Busy => Lang.Pick("The host's game needs a moment.", "房主那边马上就好。"),
            _ => null
        };
        return why == null ? getting : getting + Lang.Pick(" ", "") + why;
    }

    private static ulong NewHostSaveRequestId() {
        var bytes = new byte[8];
        Random.NextBytes(bytes);
        return BitConverter.ToUInt64(bytes, 0) | 1UL;
    }

    /// <summary>
    /// The local player chose to stop waiting while the host's save was on its way, so the choice is dropped.
    /// </summary>
    private void OnJoinMessageClosed() {
        if (_joinButton == null) {
            return;
        }

        ClearJoin();
        Logger.Info("Stopped waiting for the host's save");
    }

    private void ClearJoin() {
        _joinButton = null;
        _joinEventData = null;
        _hostSaveRequestId = 0;
        _hostAnswered = false;
        _hostSaveSender = null;
        _hostSaveParts.Clear();
        _joinWait = HostSaveWait.None;
    }

    /// <summary>
    /// Forgets what the local player was warned about and chose to join with, for a new connection: all of it was about
    /// the host of the connection before.
    /// </summary>
    private void ForgetJoin() {
        _pendingJoin = null;
        _warnedSlot = -1;
        _warnedText = null;
        _noHostSlot = -1;
    }

    /// <summary>
    /// The host answered, or a part of the host's save arrived. Once all are here, the save that the local player
    /// chose goes on.
    /// </summary>
    private void OnHostSave(ClientPlayerData player, CoopSaveUpdate update) {
        if (update.Key != _hostSaveRequestId || _hostSaveRequestId == 0) {
            return;
        }

        if (update.PartCount == 0) {
            if (!_hostAnswered) {
                _hostAnswered = true;
                Logger.Info($"{player.Username} answered as the host ({update.Key})");
            }

            OnHostSaveWait(player, (HostSaveWait) update.Part, update.StateName);
            return;
        }

        if (_hostSaveSender is { } sender && sender != player.Id) {
            return;
        }

        _hostAnswered = true;
        _hostSaveSender = player.Id;
        _hostSaveParts[update.Part] = update.Bytes;
        if (_hostSaveParts.Count < update.PartCount) {
            return;
        }

        JObject save;
        try {
            var bytes = _hostSaveParts.OrderBy(pair => pair.Key).SelectMany(pair => pair.Value).ToArray();
            save = JObject.Parse(Unpack(bytes, update.FsmName == HostSaveGzip));
        } catch (Exception e) {
            Logger.Error($"Could not read the host's save from {player.Username}:\n{e}");
            var failed = _joinButton != null;
            ClearJoin();
            if (failed) {
                CloseMessage();
                ShowLater(Lang.Pick(
                    "The host's save arrived broken. Choose your save again to try again.",
                    "房主的存档传过来是坏的。重新选一次存档再试。"
                ));
            }

            return;
        }

        Logger.Info($"Got the save of the host {player.Username} in {update.PartCount} parts");

        var button = _joinButton;
        var eventData = _joinEventData;
        var hostName = player.Username;
        ClearJoin();
        if (button == null || eventData == null) {
            return;
        }

        CloseMessage();
        var token = ++_menuActionToken;
        RunInMenuLater(() => {
            if (token == _menuActionToken && !IsInGame()) {
                ContinueJoin(button, eventData, save, hostName);
            }
        });
    }

    /// <summary>
    /// The host said what keeps their save from coming, which the wait shows, or that their game could not take it,
    /// which ends the wait, with why when the host knows.
    /// </summary>
    private void OnHostSaveWait(ClientPlayerData player, HostSaveWait wait, string reason) {
        if (wait == HostSaveWait.Failed) {
            Logger.Info(
                $"The game of the host {player.Username} could not take their save to send" +
                (reason == HostGameTooOld ? ": the host's game is older than the mod needs" : "")
            );
            var waiting = _joinButton != null;
            ClearJoin();
            if (waiting) {
                CloseMessage();
                ShowLater(reason == HostGameTooOld
                    ? Lang.Pick(
                        "The host's game is older than this mod needs, so their save can't be sent. Once the host " +
                        "has updated the game to the latest version, choose your save again.",
                        "房主的游戏版本太旧，存档发不过来。请房主把游戏更新到最新版，再重新选一次存档。"
                    )
                    : Lang.Pick(
                        "The host's game couldn't get their save ready. Choose your save again to try again.",
                        "房主那边的存档没能取出来。重新选一次存档再试。"
                    ));
            }

            return;
        }

        if (_joinButton == null || wait == _joinWait) {
            return;
        }

        _joinWait = wait;
        Logger.Info($"The host's save is kept by: {wait}");
        CoopMessageBox.SetText(_joinMessage, GetJoinWaitText(wait));
    }

    /// <summary>
    /// Aligns the chosen save with the host's save, warns about what that changes the first time it would, and loads
    /// it. A failure on the way tells the player and loads nothing: the save as it was would load as a co-op save that
    /// doesn't follow the host's world.
    /// </summary>
    private void ContinueJoin(
        UnityEngine.UI.SaveSlotButton button,
        BaseEventData eventData,
        JObject hostSave,
        string hostName
    ) {
        try {
            AlignAndLoad(button, eventData, hostSave, hostName);
        } catch (Exception e) {
            _pendingJoin = null;
            Logger.Error($"Could not align save slot {button.SaveSlotIndex} with the host's save:\n{e}");
            ShowMessage(GetGameTooOldText(e) ?? Lang.Pick(
                "Your save couldn't be aligned with the host's, so it wasn't loaded. Choose it again to try again.",
                "没能把你的存档按房主的存档对齐，所以没有进入。再选一次这个存档重试。"
            ));
        }
    }

    /// <summary>
    /// What the joining player hears when their own game is older than the mod needs and lacks what the mod uses to
    /// join with their save, or null when that isn't why it failed.
    /// </summary>
    private static string? GetGameTooOldText(Exception e) => GameVersion.IsTooOldFor(e)
        ? Lang.Pick(
            "Your game is older than this mod needs, so it can't join the host's game. Update the game to the latest " +
            "version, then try again.",
            "你的游戏版本太旧，没法加入房主的游戏。请把游戏更新到最新版再试。"
        )
        : null;

    private void AlignAndLoad(
        UnityEngine.UI.SaveSlotButton button,
        BaseEventData eventData,
        JObject hostSave,
        string hostName
    ) {
        var slot = button.SaveSlotIndex;
        var data = GetHostSyncData();
        if (data == null) {
            ShowMessage(Lang.Pick(
                "Your game can't align saves with the host's. Check that you both have the same version.",
                "你的游戏没法按房主的存档对齐。看看你们两个的版本是不是一样。"
            ));
            return;
        }

        var hasSave = HasSave(button);
        JObject client;
        try {
            client = hasSave ? ReadSave(button) : NewSave(hostSave);
        } catch (Exception e) {
            Logger.Error($"Could not read save slot {slot} to align it with the host's save:\n{e}");
            ShowMessage(GetGameTooOldText(e) ?? Lang.Pick("This save can't be read.", "这个存档读不出来。"));
            return;
        }

        // Where a new save wakes, before the alignment gives it the host's place to wake at
        var newGameWake = hasSave ? default : GetWakePlace(client);
        var (version, revisionBreak) = GetGameSaveVersion();
        var merged = CoopHostSync.Merge(hostSave, client, data, out var report, version, revisionBreak);
        if (merged == null) {
            Logger.Info($"Save slot {slot} can't be aligned with the host's save: {report.Refusal}");
            ShowMessage(report.Refusal == CoopHostSyncReport.SteelSoulEnded
                ? Lang.Pick(
                    "A Steel Soul save that a death has ended can't be played together.",
                    "钢魂存档已经因为死亡结束了，不能一起玩。"
                )
                : Lang.Pick(
                    "Your save is a Steel Soul save, which a death ends, and the host's isn't, so it can't follow " +
                    "the host's world. Choose a normal save, or have the host play a Steel Soul save.",
                    "你的存档是钢魂存档（死一次存档就没了），房主的不是，所以不能跟着房主的世界走。" +
                    "换一个普通存档，或者让房主也用钢魂存档。"
                ));
            return;
        }

        var marker = GetMarker(slot);
        var aheadBosses = report.AheadBosses.Count;
        var firstTime = hasSave && marker == null;
        var hostSteelSoul = (int?) hostSave["playerData"]?["permadeathMode"] == PermadeathMode;
        var permadeath = !hasSave && hostSteelSoul;

        // A normal save that follows a Steel Soul host's world stays normal (the mode is never merged), which it hears
        // with the rest of what becoming a co-op save means
        var followsSteelSoul = firstTime && hostSteelSoul &&
                               (int?) client["playerData"]?["permadeathMode"] != PermadeathMode;
        if (firstTime || permadeath || followsSteelSoul || aheadBosses > 0 || report.AheadAct) {
            // A second choice goes ahead when the warning would say the same again. One that would say something
            // else, like after the host played on or another host came, is shown first.
            var warning = GetJoinWarning(
                hostName, firstTime, permadeath, followsSteelSoul, aheadBosses, report.AheadAct
            );
            if (slot != _warnedSlot || warning != _warnedText) {
                _warnedSlot = slot;
                _warnedText = warning;
                ShowMessage(warning);
                return;
            }
        }

        _warnedSlot = -1;
        _warnedText = null;
        if (hasSave) {
            BackUpSave(slot);
        }

        _pendingJoin = new PendingJoin {
            Slot = slot, Merged = merged, Report = report, HostName = hostName, Time = UnityEngine.Time.unscaledTime,
            Opening = !hasSave && GetWakePlace(merged) == newGameWake
        };
        MarkCoopSave(slot, report.ArrivalsToSee);
        Logger.Info(
            $"Joining {hostName}'s game with save slot {slot} aligned to their save: {report}; " +
            $"{aheadBosses} bosses to fight again" + (report.AheadAct ? ", back to the host's act" : "")
        );
        foreach (var line in report.Lines) {
            Logger.Info($"  {line}");
        }

        if (hasSave) {
            _bypassSubmit = true;
            try {
                button.OnSubmit(eventData);
            } finally {
                _bypassSubmit = false;
            }

            return;
        }

        // An empty slot starts a new game in the game's own menu, so the new save is loaded the way a saved one is
        // instead, and gets its file at once, which the game reads in place of the save handed over when it still has
        // to set the screen up first
        var gameManager = global::GameManager.instance;
        var mergedSave = SaveDataUtility.DeserializeSaveData<SaveGameData>(merged.ToString(Formatting.None));
        Platform.Current.WriteSaveSlot(slot, gameManager.GetBytesForSaveData(mergedSave), written => {
            if (!written) {
                Logger.Warn($"Could not write the aligned save to the empty save slot {slot} ahead of loading");
            }
        });
        UIManager.instance.UIContinueGame(slot, mergedSave);
    }

    /// <summary>
    /// What the local player hears before their save is aligned with the host's for the first time (a normal save
    /// following a host's save in the mode in which a death ends it hears what that means), before a new save starts in
    /// that mode, or before it undoes beaten bosses or an act.
    /// </summary>
    private static string GetJoinWarning(
        string hostName,
        bool firstTime,
        bool permadeath,
        bool followsSteelSoul,
        int aheadBosses,
        bool aheadAct
    ) {
        var parts = new List<string>();
        if (firstTime) {
            parts.Add(Lang.Pick(
                $"This save becomes a co-op save that follows the world of {hostName}: their beaten bosses, story, " +
                "wishes, doors and mechanisms. What they found that matters, like abilities, upgrades, tools and keys, " +
                "is added to yours, and keys and story items that their world has used up are used up for you too. " +
                "Your money, materials, purchases and loadout stay yours. The save is backed up first.",
                $"这个存档会变成联机存档，世界按 {hostName} 的进度走：打过的 Boss、剧情、愿望、门和机关都以房主为准。" +
                "房主找到的重要东西（能力、升级、工具、钥匙等）会补给你；房主那边已经用掉的钥匙和剧情道具，你这边也算用掉了。" +
                "钱、材料、买的东西和装备搭配都还是你的。进去之前会先备份。"
            ));
        }

        if (permadeath) {
            parts.Add(Lang.Pick(
                $"{hostName} plays the mode in which a death ends the save, so this new save is in that mode too.",
                $"{hostName} 玩的是死一次存档就没了的模式，这个新存档也会是这个模式。"
            ));
        }

        if (followsSteelSoul) {
            parts.Add(Lang.Pick(
                $"{hostName} plays a Steel Soul save, which a death ends. Yours stays a normal save: when you die, " +
                $"you come back as usual. When {hostName} dies, their save is over and you can't go on in this " +
                "world together; your own save stays.",
                $"{hostName} 玩的是钢魂存档（死一次存档就没了）。你的存档还是普通存档，死了照常能回来。" +
                $"但 {hostName} 死了的话，房主的存档就没了，你们不能再在这个世界一起玩；你自己的存档还在。"
            ));
        }

        if (aheadBosses > 0 || aheadAct) {
            parts.Add(aheadAct
                ? Lang.Pick(
                    $"Your save is further in the story than the host's ({aheadBosses} bosses that the host hasn't " +
                    "beaten). Those bosses have to be fought again, and what you got from them stays yours.",
                    $"你的存档剧情比房主靠后（有 {aheadBosses} 个房主没打过的 Boss）。这些 Boss 要重新打，" +
                    "以前从它们那里拿到的东西还是你的。"
                )
                : Lang.Pick(
                    $"Your save has beaten {aheadBosses} bosses that the host hasn't. Those have to be fought again, and " +
                    "what you got from them stays yours.",
                    $"你的存档打过 {aheadBosses} 个房主没打过的 Boss，这些要重新打，以前拿到的东西还是你的。"
                ));
        }

        parts.Add(Lang.Pick("Choose this save again to go on.", "再选一次这个存档就进入。"));
        return string.Join(Lang.Pick(" ", ""), parts);
    }

    /// <summary>
    /// The save of a slot as loading it gives it to the game: brought up to the version of the game that runs by the
    /// game's own upgrade, which loading a save runs. The merged save is marked with that version, so that loading it
    /// doesn't run the upgrades of older saves over the host's data again.
    /// </summary>
    private static JObject ReadSave(UnityEngine.UI.SaveSlotButton button) {
        var json = SaveDataUtility.SerializeSaveData(button.saveStats.saveGameData, null);
        try {
            var copy = SaveDataUtility.DeserializeSaveData<SaveGameData>(json);
            SaveDataUpgradeHandler.UpgradeSaveData(copy.sceneData, copy.playerData);
            json = SaveDataUtility.SerializeSaveData(copy, null);
        } catch (Exception e) {
            Logger.Warn(
                $"Could not bring save slot {button.SaveSlotIndex} up to the version of the game before aligning it:\n{e}"
            );
        }

        return JObject.Parse(json);
    }

    /// <summary>
    /// The place a save wakes at (PlayerData.respawnScene and respawnMarkerName), with an empty name as none. A new
    /// game has none until its first bench or place to come back to, and the game then starts it in the first room
    /// (GameManager.GetRespawnInfo). A joining save takes the host's, so a new save that joins a host who has played
    /// on wakes where the host last was.
    /// </summary>
    private static (string? Scene, string? Marker) GetWakePlace(JObject save) {
        var playerData = save["playerData"];
        var scene = (string?) playerData?["respawnScene"];
        var marker = (string?) playerData?["respawnMarkerName"];
        return (string.IsNullOrEmpty(scene) ? null : scene, string.IsNullOrEmpty(marker) ? null : marker);
    }

    /// <summary>
    /// A new save, as the game starts one, in the game mode of the host: what an empty slot joins the host's game with.
    /// </summary>
    private static JObject NewSave(JObject hostSave) {
        var playerData = SaveDataUtility.DeserializeSaveData<PlayerData>("{}");
        playerData.SetupNewPlayerData(false);
        playerData.SetupExistingPlayerData();
        var save = JObject.Parse(SaveDataUtility.SerializeSaveData(new SaveGameData(playerData, new SceneData()), null));
        if (hostSave["playerData"]?["permadeathMode"] is { } mode && save["playerData"] is JObject newData) {
            newData["permadeathMode"] = mode.DeepClone();
        }

        return save;
    }

    /// <summary>
    /// The version and revision that the game which runs marks its saves with, or none if they can't be read. The game
    /// writes them in as constants (GameManager.PreparePlayerDataForSave), so they are read from the game's own types
    /// as it runs: written into the mod, they would be left behind by the next update of the game.
    /// </summary>
    private static (string? Version, int? RevisionBreak) GetGameSaveVersion() {
        try {
            var version = GameVersion.Running;
            var revisionBreak = ReadConstant(typeof(SaveDataUtility), "MANUAL_REVISION_BREAK") as int?;
            return (version, revisionBreak);
        } catch (Exception e) {
            Logger.Warn($"Could not read the version of the game for an aligned save:\n{e}");
            return (null, null);
        }
    }

    private static object? ReadConstant(Type type, string name) {
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return field == null ? null : field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
    }

    /// <summary>
    /// Shows a message in the menu after a few frames, once the menu takes input again.
    /// </summary>
    private void ShowLater(string text) {
        var token = ++_menuActionToken;
        RunInMenuLater(() => {
            if (token == _menuActionToken && !IsInGame()) {
                ShowMessage(text);
            }
        });
    }

    /// <summary>
    /// Runs an action after some seconds of real time, in the menu or in the game.
    /// </summary>
    private static void RunInMenuAfter(float seconds, Action action) {
        var gameManager = global::GameManager.instance;
        if (gameManager == null) {
            return;
        }

        gameManager.StartCoroutine(RunAfterSeconds(seconds, action));
    }

    private static IEnumerator RunAfterSeconds(float seconds, Action action) {
        yield return new UnityEngine.WaitForSecondsRealtime(seconds);

        try {
            action();
        } catch (Exception e) {
            Logger.Error($"Could not finish an action of the co-op save in the menu:\n{e}");
        }
    }

    /// <summary>
    /// Makes a slot a co-op save, with the arrivals that the host had which the local player still sees.
    /// </summary>
    private void MarkCoopSave(int slot, IEnumerable<string> arrivalsToSee) {
        var markers = GetMarkers();
        var key = GetSlotKey(slot);
        if (!markers.Slots.TryGetValue(key, out var marker)) {
            marker = markers.Slots[key] = new CoopSaveMarker { PairedUtc = DateTime.UtcNow };
        }

        marker.Members = GetOthersOnServer()
            .Where(player => player.SaveKey.Length > 0)
            .Select(player => new CoopSaveMember { Key = player.SaveKey, Name = player.Username })
            .ToList();
        marker.TakeOverPartner();
        _freshMarkerSlot = slot;
        foreach (var flag in arrivalsToSee) {
            if (!marker.ArrivalsToSee.Contains(flag)) {
                marker.ArrivalsToSee.Add(flag);
            }
        }

        SaveMarkers();
    }

    #endregion

    #region Joining player: loading

    /// <summary>
    /// Puts the aligned save in place of the save that the game loads into the slot it was made for, whichever way the
    /// game loads it: handed over from the menu, or read from its file.
    /// </summary>
    private void OnSetLoadedGameData(
        Action<global::GameManager, SaveGameData, int> orig,
        global::GameManager self,
        SaveGameData gameData,
        int slot
    ) {
        // Every load starts over, so that the mark of a load that failed in the menu never reaches the next one
        _openingSlot = -1;
        if (_pendingJoin is { } join && join.Slot == slot) {
            _pendingJoin = null;
            if (join.Opening) {
                _openingSlot = slot;
            }

            try {
                gameData = SaveDataUtility.DeserializeSaveData<SaveGameData>(join.Merged.ToString(Formatting.None));
                _joinSummary = GetJoinSummary(join);
                Logger.Info($"Loading save slot {slot} aligned to the save of {join.HostName}");
            } catch (Exception e) {
                Logger.Error($"Could not load the aligned save of slot {slot}, loading it as it was:\n{e}");
            }
        }

        orig(self, gameData, slot);
    }

    /// <summary>
    /// Starts a new save, aligned with the host's world, with the opening of a new game, as a new game starts (USER
    /// 10-22: "让加入的人也放一遍动画吧"), where the game would put the player at the place that a loaded save respawns at:
    /// only one that wakes where a new game starts (<see cref="GetWakePlace"/>), like when both players start new
    /// games. One that takes the host's place to wake at wakes there, as any save that is loaded does. Such a save
    /// loads as a saved one does, with all that loading sets up (GameManager.RunContinueGame), which ends here; the
    /// game itself ends that load with an opening instead for a save that wakes in the last act, in the same way. The
    /// opening then loads the first room as for a new game (OpeningSequence: OnWillActivateFirstLevel, the entrance of
    /// a new game), the room that such a save would have woken in.
    /// </summary>
    private void OnReadyForRespawn(
        Action<global::GameManager, bool> orig,
        global::GameManager self,
        bool isFirstLevelForPlayer
    ) {
        var slot = _openingSlot;
        _openingSlot = -1;
        if (slot < 0 || slot != self.profileID || !isFirstLevelForPlayer) {
            orig(self, isFirstLevelForPlayer);
            return;
        }

        Logger.Info($"Save slot {slot} is new, so it starts with the opening of a new game");

        // Loading a save asks for the fade of a loaded save, which only a respawn takes back (EnterHero); a new game
        // enters the first room by its entrance and never asks for it, and leaving it would fade each room after
        self.needFirstFadeIn = false;

        // As the game loads its openings: a new game's (RunStartNewGame) and the last act's (RunContinueGame)
        self.BeginSceneTransition(new global::GameManager.SceneLoadInfo {
            AlwaysUnloadUnusedAssets = true,
            IsFirstLevelForPlayer = true,
            PreventCameraFadeOut = true,
            WaitForSceneTransitionCameraFade = false,
            SceneName = NewGameOpeningScene,
            Visualization = (global::GameManager.SceneLoadVisualizations) (-1)
        });
    }

    /// <summary>
    /// What the local player hears once their save, aligned with the host's, has loaded. Counts only, which say
    /// nothing of the game that is still to come.
    /// </summary>
    private static string GetJoinSummary(PendingJoin join) {
        var report = join.Report;
        var given = report.Items + report.Rewards + report.Shards + report.Spools;
        return given == 0
            ? Lang.Pick(
                $"Your save follows the world of {join.HostName} now, and it was backed up first.",
                $"你的存档已按 {join.HostName} 的世界对齐，进入前做过备份。"
            )
            : Lang.Pick(
                $"Your save follows the world of {join.HostName} now, and it was backed up first. You got " +
                $"{report.Items} things they found, {report.Rewards} rewards of wishes they completed, " +
                $"{report.Shards} mask shards and {report.Spools} spool pieces.",
                $"你的存档已按 {join.HostName} 的世界对齐，进入前做过备份。补给你：房主找到的东西 {report.Items} 件，" +
                $"房主完成的愿望奖励 {report.Rewards} 份，面具碎片 {report.Shards} 个，灵丝轴碎片 {report.Spools} 个。"
            );
    }

    /// <summary>
    /// Tells the local player about the alignment once they are in the game.
    /// </summary>
    private void UpdateJoinSummary() {
        if (_joinSummary == null || !IsInGame()) {
            return;
        }

        Chat(_joinSummary);
        _joinSummary = null;
    }

    #endregion
}
