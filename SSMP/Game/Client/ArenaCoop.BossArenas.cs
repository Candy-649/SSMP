using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using SSMP.Ui;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Arenas that count as bosses (<see cref="BossArenaScenes"/>). Like a boss room, such an arena only starts its fight
/// once every player stands in it, and the players fight it together. What starts the fight differs from arena to arena:
/// the arena's own trigger, a boss that wakes when a player comes near or hits it, a trigger of the room, or something
/// the player strikes. Each of them waits until every player is in the arena, and the players hear that someone waits
/// for them. Where a talk comes before the fight, the fight also waits until every player finished the talk, and a
/// player who finished first can move while they wait, like in a boss room.
/// </summary>
internal partial class ArenaCoop {
    /// <summary>
    /// The events in boss arenas that start the fight, which wait until every player is in the arena. Arenas that only
    /// start from their own trigger have no entry: <see cref="OnStartBattle"/> holds that start.
    /// </summary>
    private static readonly BossStart[] BossStarts = [
        // A boss that wakes when a player comes near it or hits it the first time it is met. When it is met again, the
        // arena's own trigger starts the fight
        new("tut_03", "Mossbone Mother", "Control", "Dormant", ["WAKE", "BLOCKED HIT", "COCOON KILL", "NOISE"]),
        // The trigger of the room that starts the show before the fight
        new("room_crowcourt_02", "Battle Start", "Battle Start", "Idle", ["ENTER"]),
        // The trigger of the room that wakes the boss
        new("song_04", "Wake Scene", "Control", "Idle", ["ENTER"]),
        // What the player strikes to call the boss
        new("dust_chef", "Kitchen Pipe Gong", "Gong Hit Reaction", "Wait For Hit", ["GONG HIT"]),
        // The trigger of the room that starts the talk before the fight
        new("bone_steel_servant", "Steel Servant Scene", "Control", "Idle", ["ENTER"])
    ];

    /// <summary>
    /// The ends of talks before the fight in boss arenas, which wait until every player finished the talk, so that the
    /// fight doesn't start around a player who is still reading.
    /// </summary>
    private static readonly BossStart[] BossTalkEnds = [
        new("bone_steel_servant", "Steel Servant Scene", "Control", "End Dialogue", ["FINISHED"])
    ];

    /// <summary>
    /// How long, in seconds, a message about waiting players isn't repeated.
    /// </summary>
    private const float BossNoticeInterval = 30f;

    /// <summary>
    /// How far, in units, a player may be outside the camera locks of a boss arena and still count as in it, since a
    /// player who stands on the floor can be a little lower than where a camera lock begins.
    /// </summary>
    private const float BossLockAreaMargin = 1.5f;

    /// <summary>
    /// How far, in units, a gate of a boss arena may lie to the side of the arena's middle and still bound the arena.
    /// Gates further along belong to other ways through the room and would cut off part of the arena.
    /// </summary>
    private const float BossGateReach = 3f;

    /// <summary>
    /// How far, in units, from its middle a player counts as in a boss arena that has no camera lock around its middle.
    /// </summary>
    private const float BossArenaRadius = 60f;

    /// <summary>
    /// Reflected field with the object that holds the gates of an arena.
    /// </summary>
    private static readonly FieldInfo? GatesField = typeof(BattleScene).GetField("gates", InstanceFlags);

    /// <summary>
    /// The message for a player who waits in a boss arena for the other players.
    /// </summary>
    private string BossWaitingMessage => _playerData.Count <= 1
        ? Lang.Pick("Waiting for your teammate to catch up...", "正在等队友跟上来……")
        : Lang.Pick("Waiting for your teammates to catch up...", "正在等队友们跟上来……");

    /// <summary>
    /// The message for a player whose teammate waits in a boss arena.
    /// </summary>
    private string BossTeammateWaitingMessage => _playerData.Count <= 1
        ? Lang.Pick("Your teammate is waiting for you.", "你的队友正在等你。")
        : Lang.Pick("A teammate is waiting for you.", "有队友正在等你。");

    /// <summary>
    /// The message for a player who finished the talk before the fight of a boss arena while a teammate still reads.
    /// </summary>
    private string BossTalkWaitingMessage => _playerData.Count <= 1
        ? Lang.Pick("Waiting for your teammate to finish the dialogue...", "正在等队友看完对话……")
        : Lang.Pick("Waiting for your teammates to finish the dialogue...", "正在等队友们看完对话……");

    /// <summary>
    /// The events of boss arenas that wait for the other players, by the FSM that waits for them.
    /// </summary>
    private readonly Dictionary<Fsm, HeldBossEvent> _heldBossEvents = new();

    /// <summary>
    /// Reused while sending the held events whose players arrived, so that the dictionary is not written while it is
    /// read.
    /// </summary>
    private readonly List<Fsm> _releasedBossEvents = [];

    /// <summary>
    /// Reused while forgetting the held events whose FSM went on by itself.
    /// </summary>
    private readonly List<Fsm> _forgottenBossEvents = [];

    /// <summary>
    /// Whether this game is sending an event of a boss arena that waited, which must not wait again.
    /// </summary>
    private bool _resendingBossEvent;

    /// <summary>
    /// Whether the active scene has a boss arena, so that the events of all other scenes pass straight through.
    /// </summary>
    private bool _isBossArenaScene;

    /// <summary>
    /// When the local player may hear again that they wait in a boss arena, or that a teammate waits for them.
    /// </summary>
    private float _nextBossNoticeTime;

    /// <summary>
    /// The hook on PlayMaker's event processing, which holds the events that start boss arenas.
    /// </summary>
    private Hook? _processEventHook;

    /// <summary>
    /// Tells the other players, wherever they are, that the local player waits for them in a room.
    /// </summary>
    private readonly Action _tellTeammatesWaiting;

    /// <summary>
    /// Whether the loaded save is a two-player save whose partner isn't on the server, so that boss arenas wait for them.
    /// </summary>
    private readonly Func<bool> _isPartnerMissing;

    /// <summary>
    /// Registers the hook that holds the events which start boss arenas.
    /// </summary>
    private void RegisterBossArenaHooks() {
        var method = typeof(Fsm).GetMethod(
            "ProcessEvent", InstanceFlags, null, [typeof(FsmEvent), typeof(FsmEventData)], null
        );
        if (method == null) {
            Logger.Error("Could not find Fsm.ProcessEvent; boss arenas don't wait for every player");
            return;
        }

        try {
            _processEventHook = new Hook(
                method,
                new Action<Action<Fsm, FsmEvent, FsmEventData>, Fsm, FsmEvent, FsmEventData>(OnProcessEvent)
            );
        } catch (Exception e) {
            Logger.Error($"Could not hook Fsm.ProcessEvent; boss arenas don't wait for every player:\n{e}");
        }

        SceneManager.sceneLoaded += OnBossArenaSceneLoaded;
        SceneManager.sceneUnloaded += OnBossArenaSceneUnloaded;
        UpdateBossArenaScene();
    }

    /// <summary>
    /// Disposes the hook that holds the events which start boss arenas, and lets the events that still wait go on, so
    /// that a game that is no longer connected carries on like it does alone.
    /// </summary>
    private void DeregisterBossArenaHooks() {
        _processEventHook?.Dispose();
        _processEventHook = null;
        SceneManager.sceneLoaded -= OnBossArenaSceneLoaded;
        SceneManager.sceneUnloaded -= OnBossArenaSceneUnloaded;

        foreach (var pair in _heldBossEvents.ToList()) {
            SendHeldBossEvent(pair.Key, pair.Value, "the local player is no longer connected");
        }

        _heldBossEvents.Clear();
        _isBossArenaScene = false;
    }

    /// <summary>
    /// Holds back an event that starts the fight of a boss arena, or ends the talk before it, until every player is
    /// there.
    /// </summary>
    private void OnProcessEvent(
        Action<Fsm, FsmEvent, FsmEventData> orig,
        Fsm self,
        FsmEvent fsmEvent,
        FsmEventData eventData
    ) {
        if (_isBossArenaScene && !_resendingBossEvent && fsmEvent?.Name is { } eventName &&
            self.ActiveState?.Name is { } stateName && TryHoldBossEvent(self, stateName, eventName)) {
            return;
        }

        orig(self, fsmEvent!, eventData);
    }

    /// <summary>
    /// Holds back an event of a boss arena that starts its fight or ends the talk before it, while not every player is
    /// there for it.
    /// </summary>
    /// <returns>Whether the event is held back.</returns>
    private bool TryHoldBossEvent(Fsm fsm, string stateName, string eventName) {
        var gameObject = fsm.GameObject;
        if (gameObject == null) {
            return false;
        }

        var sceneName = gameObject.scene.name;
        var isTalkEnd = FindBossStart(BossTalkEnds, sceneName, gameObject.name, fsm.Name, stateName, eventName);
        if (!isTalkEnd && !FindBossStart(BossStarts, sceneName, gameObject.name, fsm.Name, stateName, eventName)) {
            return false;
        }

        var battleScene = FindBossArena(gameObject);
        if (battleScene == null || IsCompleted(battleScene)) {
            return false;
        }

        var state = GetState(battleScene);
        if (state.Started) {
            return false;
        }

        if (isTalkEnd) {
            if (!state.LocalTalkDone) {
                state.LocalTalkDone = true;
                Send(state.Path, BattleSceneStatus.TalkDone);
            }

            if (!IsBossHoldActive() || HaveAllFinishedTheTalk(state)) {
                return false;
            }
        } else if (HaveAllArrivedAtBossArena(battleScene, state)) {
            return false;
        }

        if (!_heldBossEvents.TryGetValue(fsm, out var held) || held.StateName != stateName ||
            held.EventName != eventName) {
            held = new HeldBossEvent(battleScene, stateName, eventName, isTalkEnd);
            _heldBossEvents[fsm] = held;
            Logger.Info(
                $"Holding back '{eventName}' of '{gameObject.name}' in boss arena '{state.Path}' until every player " +
                (isTalkEnd ? "finished the talk" : "is in the arena")
            );

            // Like in a boss room, a player who finished the talk can move while the others still read, instead of
            // standing still for as long as that takes
            if (isTalkEnd) {
                BossRoomCoop.FreeHero(held);
            }
        }

        NotifyBossWait(battleScene, state, isTalkEnd);
        return true;
    }

    /// <summary>
    /// Whether an FSM state and event are one of the given starts of boss arenas.
    /// </summary>
    private static bool FindBossStart(
        BossStart[] starts,
        string sceneName,
        string objectName,
        string fsmName,
        string stateName,
        string eventName
    ) {
        foreach (var start in starts) {
            if (start.StateName == stateName && start.ObjectName == objectName && start.FsmName == fsmName &&
                start.EventNames.Contains(eventName) &&
                string.Equals(start.SceneName, sceneName, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sends the held events of a boss arena whose players are all there now, and forgets the ones whose FSM went on by
    /// itself. Once the fight started for the scene host, the events still go on, so that the local room catches up
    /// like it does for a player who comes in late.
    /// </summary>
    private void ReleaseHeldBossEvents(BattleScene battleScene, ArenaState state) {
        // A player who left the scene finishes the talk again when they come back
        state.TalkDoneBy.RemoveWhere(id => !_playerData.TryGetValue(id, out var other) || !other.IsInLocalScene);

        if (_heldBossEvents.Count == 0) {
            return;
        }

        _releasedBossEvents.Clear();
        _forgottenBossEvents.Clear();
        foreach (var pair in _heldBossEvents) {
            var fsm = pair.Key;
            var held = pair.Value;
            if (held.BattleScene != battleScene) {
                continue;
            }

            // A won arena never starts its fight again
            if (fsm.GameObject == null || fsm.ActiveState?.Name != held.StateName || IsCompleted(battleScene)) {
                _forgottenBossEvents.Add(fsm);
                continue;
            }

            var ready = state.Started || (held.IsTalkEnd
                ? !IsBossHoldActive() || HaveAllFinishedTheTalk(state)
                : HaveAllArrivedAtBossArena(battleScene, state));
            if (ready) {
                _releasedBossEvents.Add(fsm);
                continue;
            }

            NotifyBossWait(battleScene, state, held.IsTalkEnd);
        }

        foreach (var fsm in _forgottenBossEvents) {
            _heldBossEvents.Remove(fsm);
        }

        foreach (var fsm in _releasedBossEvents) {
            if (_heldBossEvents.TryGetValue(fsm, out var held)) {
                _heldBossEvents.Remove(fsm);
                SendHeldBossEvent(
                    fsm, held, state.Started ? "the fight started for the scene host" : "every player is there"
                );
            }
        }

        _releasedBossEvents.Clear();
        _forgottenBossEvents.Clear();
    }

    /// <summary>
    /// Sends an event of a boss arena that waited, if its FSM still waits for it. The end of a talk first takes control
    /// from the local player again, as the talk had it before they were let move while waiting.
    /// </summary>
    private void SendHeldBossEvent(Fsm fsm, HeldBossEvent held, string reason) {
        if (fsm.GameObject == null || fsm.ActiveState?.Name != held.StateName) {
            return;
        }

        Logger.Info($"Sending '{held.EventName}' of '{fsm.GameObject.name}' that waited, since {reason}");
        if (held.IsTalkEnd) {
            BossRoomCoop.RestoreHero(held);
        }

        _resendingBossEvent = true;
        try {
            fsm.Event(held.EventName);
        } catch (Exception e) {
            Logger.Error($"Could not send '{held.EventName}' of '{fsm.GameObject.name}':\n{e}");
        } finally {
            _resendingBossEvent = false;
        }
    }

    /// <summary>
    /// Holds back the start of a boss arena from its own trigger while not every player is in the arena.
    /// </summary>
    /// <returns>Whether the start is held back.</returns>
    private bool TryHoldBossTriggerStart(BattleScene battleScene, ArenaState state) {
        if (_heroTriggerTarget != battleScene || !IsBossArena(battleScene) || IsCompleted(battleScene) ||
            state.Started || HaveAllArrivedAtBossArena(battleScene, state)) {
            return false;
        }

        if (!state.TriggerStartHeld) {
            state.TriggerStartHeld = true;
            Logger.Info($"Holding back the start of boss arena '{state.Path}' until every player is in the arena");
        }

        NotifyBossWait(battleScene, state, false);
        return true;
    }

    /// <summary>
    /// Starts a boss arena whose start from its own trigger waited, once every player is in it.
    /// </summary>
    private void ReleaseHeldTriggerStart(BattleScene battleScene, ArenaState state) {
        if (!state.TriggerStartHeld) {
            return;
        }

        if (state.Started || IsCompleted(battleScene)) {
            state.TriggerStartHeld = false;
            return;
        }

        if (!HaveAllArrivedAtBossArena(battleScene, state)) {
            NotifyBossWait(battleScene, state, false);
            return;
        }

        state.TriggerStartHeld = false;
        Logger.Info($"Every player is in boss arena '{state.Path}', starting it");
        CallArenaMethod(StartBattleMethod!, battleScene);
    }

    /// <summary>
    /// Whether an arena counts as a boss.
    /// </summary>
    private static bool IsBossArena(BattleScene battleScene) {
        return BossArenaScenes.Contains(battleScene.gameObject.scene.name);
    }

    /// <summary>
    /// Finds the boss arena of the scene that an object is in: the one that isn't won, preferring the one the object is
    /// part of.
    /// </summary>
    private static BattleScene? FindBossArena(GameObject gameObject) {
        var parentArena = gameObject.GetComponentInParent<BattleScene>(true);
        if (parentArena != null) {
            return parentArena;
        }

        BattleScene? found = null;
        foreach (var battleScene in Object.FindObjectsByType<BattleScene>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None
                 )) {
            if (battleScene.gameObject.scene != gameObject.scene || IsCompleted(battleScene)) {
                continue;
            }

            if (found != null) {
                // Two open arenas in one scene: the starts above all lie inside their arena, so this doesn't happen
                return found;
            }

            found = battleScene;
        }

        return found;
    }

    /// <summary>
    /// Whether boss arenas wait for other players: other players are connected, or the partner of a two-player save is
    /// expected, and the server synchronises entities.
    /// </summary>
    private bool IsBossHoldActive() {
        return (_playerData.Count > 0 || _isPartnerMissing()) && _netClient.IsConnected && _isFullSynchronisation();
    }

    /// <summary>
    /// Whether the local player and every other player stand in a boss arena. Players in other scenes, and the missing
    /// partner of a two-player save, aren't there.
    /// </summary>
    private bool HaveAllArrivedAtBossArena(BattleScene battleScene, ArenaState state) {
        if (!IsBossHoldActive()) {
            return true;
        }

        if (_isPartnerMissing()) {
            return false;
        }

        var heroController = HeroController.instance;
        if (heroController == null || !IsInBossArena(battleScene, state, heroController.transform.position)) {
            return false;
        }

        foreach (var playerData in _playerData.Values) {
            var container = playerData.PlayerContainer;
            if (!playerData.IsInLocalScene || container == null ||
                !IsInBossArena(battleScene, state, container.transform.position)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the other players in the scene finished the talk before the fight of a boss arena, every one of them. A
    /// player who left the scene isn't waited for.
    /// </summary>
    private bool HaveAllFinishedTheTalk(ArenaState state) {
        foreach (var playerData in _playerData.Values) {
            if (playerData.IsInLocalScene && !state.TalkDoneBy.Contains(playerData.Id)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a point lies in a boss arena (see <see cref="GetBossShape"/>).
    /// </summary>
    private static bool IsInBossArena(BattleScene battleScene, ArenaState state, Vector2 point) {
        return GetBossShape(battleScene, state).Contains(point);
    }

    /// <summary>
    /// Gets the part of the room that a boss arena takes, which players must be in for its fight to start: within the
    /// arena's camera locks around its middle, on the inner side of each of its gates, the way boss rooms are worked
    /// out. The arena's own trigger always counts. Triggers of what starts the fight don't: some of them reach far past
    /// the arena's door, and some cover only a small part of the arena.
    /// </summary>
    private static BossArenaShape GetBossShape(BattleScene battleScene, ArenaState state) {
        if (state.BossShape is { } known) {
            return known;
        }

        var middle = GetBossArenaMiddle(battleScene);
        var shape = new BossArenaShape(battleScene, middle);
        if (CamLocksField?.GetValue(battleScene) is GameObject camLocks && camLocks != null) {
            foreach (var collider in camLocks.GetComponentsInChildren<Collider2D>(true)) {
                if (BossRoomCoop.GetWorldBounds(collider) is { } bounds && bounds.Contains(middle)) {
                    shape.LockAreas.Add(new Rect(
                        bounds.x - BossLockAreaMargin,
                        bounds.y - BossLockAreaMargin,
                        bounds.width + 2f * BossLockAreaMargin,
                        bounds.height + 2f * BossLockAreaMargin
                    ));
                }
            }
        }

        if (GatesField?.GetValue(battleScene) is GameObject gates && gates != null) {
            foreach (var collider in gates.GetComponentsInChildren<Collider2D>(true)) {
                if (collider.isTrigger || BossRoomCoop.GetWorldBounds(collider) is not { } bounds) {
                    continue;
                }

                // A gate blocks along its thin side. One that lies too far to the side of the middle closes another way
                var isVertical = bounds.height >= bounds.width;
                var along = isVertical ? middle.y : middle.x;
                if (along < (isVertical ? bounds.yMin : bounds.xMin) - BossGateReach ||
                    along > (isVertical ? bounds.yMax : bounds.xMax) + BossGateReach) {
                    continue;
                }

                var across = isVertical ? middle.x : middle.y;
                var min = isVertical ? bounds.xMin : bounds.yMin;
                var max = isVertical ? bounds.xMax : bounds.yMax;
                if (across < min) {
                    shape.Limits.Add(new BossArenaLimit(isVertical, min, true));
                } else if (across > max) {
                    shape.Limits.Add(new BossArenaLimit(isVertical, max, false));
                }
            }
        }

        Logger.Info(
            $"Boss arena '{state.Path}' is around ({middle.x:0.0}, {middle.y:0.0}): " +
            $"{shape.LockAreas.Count} camera locks, gates at " +
            (shape.Limits.Count == 0
                ? "none"
                : string.Join(", ", shape.Limits.Select(limit =>
                    $"{(limit.IsVertical ? "x" : "y")} {(limit.IsBelow ? "<" : ">")} {limit.Value:0.0}"
                )))
        );
        state.BossShape = shape;
        return shape;
    }

    /// <summary>
    /// Gets the middle of a boss arena: the middle of its own trigger, switched on or not, or else of the trigger under it
    /// that starts its fight, or else where the arena is.
    /// </summary>
    private static Vector2 GetBossArenaMiddle(BattleScene battleScene) {
        if (GetMiddle(battleScene.GetComponents<Collider2D>()) is { } middle) {
            return middle;
        }

        var sceneName = battleScene.gameObject.scene.name;
        foreach (var start in BossStarts) {
            if (!string.Equals(start.SceneName, sceneName, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            foreach (var child in battleScene.GetComponentsInChildren<Transform>(true)) {
                if (child.name == start.ObjectName && GetMiddle(child.GetComponents<Collider2D>()) is { } startMiddle) {
                    return startMiddle;
                }
            }
        }

        return battleScene.transform.position;
    }

    /// <summary>
    /// Gets the middle of the area that some colliders cover together, or null without colliders.
    /// </summary>
    private static Vector2? GetMiddle(Collider2D[] colliders) {
        Rect? area = null;
        foreach (var collider in colliders) {
            if (collider == null || BossRoomCoop.GetWorldBounds(collider) is not { } bounds) {
                continue;
            }

            area = area is { } known
                ? Rect.MinMaxRect(
                    Mathf.Min(known.xMin, bounds.xMin), Mathf.Min(known.yMin, bounds.yMin),
                    Mathf.Max(known.xMax, bounds.xMax), Mathf.Max(known.yMax, bounds.yMax)
                )
                : bounds;
        }

        return area?.center;
    }

    /// <summary>
    /// Tells the players who waits for whom at a boss arena whose start is held back: a local player who is in the arena
    /// waits for the others, who hear that a teammate waits for them, and a local player whose teammate stands in the
    /// arena hears that the teammate waits for them, while the teammate hears that they wait. A local player who
    /// finished the talk before the fight waits for the others to finish it, who hear that a teammate waits for them.
    /// </summary>
    private void NotifyBossWait(BattleScene battleScene, ArenaState state, bool isTalkEnd) {
        if (Time.unscaledTime < _nextBossNoticeTime || !IsBossHoldActive()) {
            return;
        }

        _nextBossNoticeTime = Time.unscaledTime + BossNoticeInterval;
        if (isTalkEnd) {
            UiManager.InternalChatBox.AddMessage(BossTalkWaitingMessage);
            _tellTeammatesWaiting();
            return;
        }

        var heroController = HeroController.instance;
        if (heroController != null && IsInBossArena(battleScene, state, heroController.transform.position)) {
            UiManager.InternalChatBox.AddMessage(BossWaitingMessage);
            _tellTeammatesWaiting();
            return;
        }

        // A boss that only moves in this game woke for a teammate who stands in the arena
        UiManager.InternalChatBox.AddMessage(BossTeammateWaitingMessage);
        Send(state.Path, BattleSceneStatus.Waiting);
    }

    /// <summary>
    /// Tells the local player that they wait in a boss arena, after the game of a teammate held its start for them.
    /// </summary>
    private void OnBossWaiting() {
        if (Time.unscaledTime < _nextBossNoticeTime) {
            return;
        }

        _nextBossNoticeTime = Time.unscaledTime + BossNoticeInterval;
        UiManager.InternalChatBox.AddMessage(BossWaitingMessage);
    }

    /// <summary>
    /// Remembers that another player finished the talk before the fight of a boss arena.
    /// </summary>
    private void OnBossTalkDone(string path, ushort playerId) {
        var battleScene = FindBattleScene(path);
        if (battleScene == null) {
            return;
        }

        var state = GetState(battleScene);
        if (state.TalkDoneBy.Add(playerId)) {
            Logger.Info($"Player {playerId} finished the talk of boss arena '{state.Path}'");
        }
    }

    /// <summary>
    /// Locks in a player who follows the scene host and stands in a boss arena whose fight just started, since in most
    /// of them the fight starts without the arena's own trigger, which locks in the players who walk in.
    /// </summary>
    private void LockInIfInBossArena(BattleScene battleScene, ArenaState state) {
        var heroController = HeroController.instance;
        if (state.LockedIn || !IsBossArena(battleScene) || heroController == null ||
            !IsInBossArena(battleScene, state, heroController.transform.position)) {
            return;
        }

        Logger.Info($"The fight of boss arena '{state.Path}' started with the local player in it, locking them in");
        LockIn(battleScene, state);
    }

    /// <summary>
    /// Notes whether a loaded scene has a boss arena, after scenes loaded or unloaded.
    /// </summary>
    private void UpdateBossArenaScene() {
        _isBossArenaScene = false;
        for (var i = 0; i < SceneManager.sceneCount; i++) {
            if (BossArenaScenes.Contains(SceneManager.GetSceneAt(i).name)) {
                _isBossArenaScene = true;
                return;
            }
        }
    }

    /// <summary>
    /// Notes whether the loaded scenes have a boss arena after a scene loaded.
    /// </summary>
    private void OnBossArenaSceneLoaded(Scene scene, LoadSceneMode mode) {
        UpdateBossArenaScene();
    }

    /// <summary>
    /// Notes whether the loaded scenes have a boss arena after a scene unloaded.
    /// </summary>
    private void OnBossArenaSceneUnloaded(Scene scene) {
        UpdateBossArenaScene();
    }

    /// <summary>
    /// An event of an FSM state that starts the fight of a boss arena, or ends the talk before it.
    /// </summary>
    private class BossStart {
        public BossStart(string sceneName, string objectName, string fsmName, string stateName, string[] eventNames) {
            SceneName = sceneName;
            ObjectName = objectName;
            FsmName = fsmName;
            StateName = stateName;
            EventNames = eventNames;
        }

        /// <summary>
        /// The scene of the arena.
        /// </summary>
        public string SceneName { get; }

        /// <summary>
        /// The name of the object of the FSM.
        /// </summary>
        public string ObjectName { get; }

        /// <summary>
        /// The name of the FSM.
        /// </summary>
        public string FsmName { get; }

        /// <summary>
        /// The state that waits for the event.
        /// </summary>
        public string StateName { get; }

        /// <summary>
        /// The events that start the fight from that state.
        /// </summary>
        public string[] EventNames { get; }
    }

    /// <summary>
    /// An event of a boss arena that waits for the other players. For the end of a talk, it also remembers what was
    /// given back to the local player while they wait.
    /// </summary>
    private class HeldBossEvent : BossRoomCoop.FreedHero {
        public HeldBossEvent(BattleScene battleScene, string stateName, string eventName, bool isTalkEnd) {
            BattleScene = battleScene;
            StateName = stateName;
            EventName = eventName;
            IsTalkEnd = isTalkEnd;
        }

        /// <summary>
        /// The arena whose fight the event starts.
        /// </summary>
        public BattleScene BattleScene { get; }

        /// <summary>
        /// The state the FSM was in when the event came, which it must still be in when the event is sent again.
        /// </summary>
        public string StateName { get; }

        /// <summary>
        /// The event.
        /// </summary>
        public string EventName { get; }

        /// <summary>
        /// Whether the event ends the talk before the fight, which waits until every player finished the talk rather
        /// than until every player is in the arena.
        /// </summary>
        public bool IsTalkEnd { get; }
    }

    /// <summary>
    /// The part of the room that a boss arena takes (see <see cref="GetBossShape"/>).
    /// </summary>
    private sealed class BossArenaShape {
        public BossArenaShape(BattleScene battleScene, Vector2 middle) {
            BattleScene = battleScene;
            Middle = middle;
        }

        /// <summary>
        /// The arena, whose own trigger is always part of it.
        /// </summary>
        private BattleScene BattleScene { get; }

        /// <summary>
        /// The middle of the arena that the shape was worked out from.
        /// </summary>
        private Vector2 Middle { get; }

        /// <summary>
        /// The camera locks of the arena around its middle, with a margin.
        /// </summary>
        public List<Rect> LockAreas { get; } = [];

        /// <summary>
        /// The sides of the gates that the arena is on.
        /// </summary>
        public List<BossArenaLimit> Limits { get; } = [];

        /// <summary>
        /// Whether a point is in the arena.
        /// </summary>
        public bool Contains(Vector2 point) {
            if (BattleScene != null) {
                foreach (var collider in BattleScene.GetComponents<Collider2D>()) {
                    if (collider != null && BossRoomCoop.ContainsPoint(collider, point)) {
                        return true;
                    }
                }
            }

            if (LockAreas.Count > 0
                    ? !LockAreas.Any(area => area.Contains(point))
                    : Vector2.Distance(point, Middle) > BossArenaRadius) {
                return false;
            }

            foreach (var limit in Limits) {
                var across = limit.IsVertical ? point.x : point.y;
                if (limit.IsBelow ? across >= limit.Value : across <= limit.Value) {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// The side of a gate that a boss arena is on.
    /// </summary>
    private readonly struct BossArenaLimit {
        public BossArenaLimit(bool isVertical, float value, bool isBelow) {
            IsVertical = isVertical;
            Value = value;
            IsBelow = isBelow;
        }

        /// <summary>
        /// Whether the gate stands upright, so that it bounds the arena from the left or right.
        /// </summary>
        public bool IsVertical { get; }

        /// <summary>
        /// The edge of the gate on the side of the arena.
        /// </summary>
        public float Value { get; }

        /// <summary>
        /// Whether the arena lies below that edge (left of it, for an upright gate) rather than above it.
        /// </summary>
        public bool IsBelow { get; }
    }
}
