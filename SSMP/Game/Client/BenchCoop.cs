using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Save;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Co-op rules for resting at benches.
/// When a player rests at a bench or dies, semi-persistent objects such as enemies respawn for every player. A room
/// that a player is in at that moment keeps its state until it is loaded again, so enemies that are fighting a player
/// don't respawn around them. Players who sit on the same bench take opposite sides of it: while other players are
/// connected, the host sits right of the seat and other players left of it, whether they sit down, sit down without
/// the tween to the seat, or wake up on the bench.
/// </summary>
internal class BenchCoop {
    /// <summary>
    /// Binding flags for the private members of the game.
    /// </summary>
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Name of the FSM that makes the hero sit on and get off a bench.
    /// </summary>
    private const string BenchFsmName = "Bench Control";

    /// <summary>
    /// State of the bench FSM that moves the hero onto the seat with a tween.
    /// </summary>
    private const string SitStateName = "Start Rest";

    /// <summary>
    /// State of the bench FSM that makes the hero sit down where they stand, which the FSM picks instead of
    /// <see cref="SitStateName"/> with its NO TWEEN event.
    /// </summary>
    private const string SitNoTweenStateName = "Start Rest NoTween";

    /// <summary>
    /// State of the bench FSM that the tween path goes to next, which sets a position outright. That put the hero
    /// back in the middle of the seat right after the tween had taken them to their side.
    /// </summary>
    private const string SitSettleStateName = "Fake?";

    /// <summary>
    /// How far, in units, each player sits from the middle of the seat while other players are connected. One player
    /// is moved this far one way and the other the same distance the other way, so the gap between them is twice this.
    ///
    /// It was 1, which the sprite widths said would have them "barely touching" - and in game on 2026-09-17 that read
    /// as two people sitting pointedly apart rather than resting together. The sprites are drawn on a canvas about
    /// 3.2 units wide while the trimmed art is 1.5 to 2.3, so touching art and touching canvases are far apart, and
    /// the arithmetic was measuring the wrong one. 0.6 lets the canvases overlap and the drawn shapes sit close
    /// without either player disappearing behind the other.
    /// </summary>
    private const float SeatOffset = 0.6f;

    /// <summary>
    /// The largest distance, in units, between the hero and the position they were moved to on waking up for the hero
    /// to count as not placed on the bench again.
    /// </summary>
    private const float MovedPositionTolerance = 0.01f;

    /// <summary>
    /// States of the bench FSM that make the hero wake up sitting on the bench, after the game placed them on it as the
    /// respawn marker. The first runs when the scene starts with the hero on the bench and the second when the hero
    /// respawns there, for example after dying, loading a save or connecting to a server.
    /// </summary>
    private static readonly string[] WakeUpStateNames = ["Init Resting", "Init Resting 2"];

    /// <summary>
    /// Reflected field with the scene data of <see cref="global::GameManager"/>.
    /// </summary>
    private static readonly FieldInfo? GameManagerSceneDataField =
        typeof(global::GameManager).GetField("sceneData", InstanceFlags);

    /// <summary>
    /// Reflected method that clears the semi-persistent entries of the scene data for all scenes.
    /// </summary>
    private static readonly MethodInfo? SceneDataResetMethod =
        typeof(global::SceneData).GetMethod("ResetSemiPersistentItems", InstanceFlags);

    /// <summary>
    /// The net client for sending the reset to other players.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The data of the other connected players, by player ID.
    /// </summary>
    private readonly Dictionary<ushort, ClientPlayerData> _playerData;

    /// <summary>
    /// The save manager, which knows whether the local player hosts the server.
    /// </summary>
    private readonly SaveManager _saveManager;

    /// <summary>
    /// The X position that the hero was moved to on waking up, per bench object instance ID. Both wake-up states can
    /// run for one wake-up, and the hero is only moved again if the game placed them on the bench again in between.
    /// </summary>
    private readonly Dictionary<int, float> _wakeUpPositions = new();

    /// <summary>
    /// Hook for sharing semi-persistent resets with other players.
    /// </summary>
    private Hook? _resetSemiPersistentItemsHook;

    /// <summary>
    /// Hook for moving the seat that the hero tweens to towards their side of the bench.
    /// </summary>
    private Hook? _moveToEnterHook;

    /// <summary>
    /// Hook for moving the hero to their side of the bench when they wake up on it.
    /// </summary>
    private Hook? _restBenchHelperStateEnterHook;

    /// <summary>
    /// Hook for moving the hero to their side of the bench when they sit down without the tween.
    /// </summary>
    private Hook? _vector3AddEnterHook;

    /// <summary>
    /// Hook for keeping the hero on their side when the bench settles them onto the seat after the tween.
    /// </summary>
    private Hook? _setPositionEnterHook;

    public BenchCoop(NetClient netClient, Dictionary<ushort, ClientPlayerData> playerData, SaveManager saveManager) {
        _netClient = netClient;
        _playerData = playerData;
        _saveManager = saveManager;
    }

    /// <summary>
    /// Registers the hooks for bench co-op.
    /// </summary>
    public void RegisterHooks() {
        var resetMethod = typeof(global::GameManager).GetMethod("ResetSemiPersistentItems", InstanceFlags);
        if (resetMethod == null) {
            Logger.Error("Could not find GameManager#ResetSemiPersistentItems; hook was not registered");
        } else {
            _resetSemiPersistentItemsHook = new Hook(
                resetMethod,
                new Action<Action<global::GameManager>, global::GameManager>(OnResetSemiPersistentItems)
            );
        }

        _moveToEnterHook = CreateOnEnterHook(
            typeof(iTweenMoveTo),
            new Action<Action<iTweenMoveTo>, iTweenMoveTo>(OnMoveToEnter)
        );
        _restBenchHelperStateEnterHook = CreateOnEnterHook(
            typeof(RestBenchHelperState),
            new Action<Action<RestBenchHelperState>, RestBenchHelperState>(OnRestBenchHelperStateEnter)
        );
        _vector3AddEnterHook = CreateOnEnterHook(
            typeof(Vector3Add),
            new Action<Action<Vector3Add>, Vector3Add>(OnVector3AddEnter)
        );
        _setPositionEnterHook = CreateOnEnterHook(
            typeof(SetPosition),
            new Action<Action<SetPosition>, SetPosition>(OnSetPositionEnter)
        );

        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    /// <summary>
    /// Disposes the hooks for bench co-op.
    /// </summary>
    public void DeregisterHooks() {
        _resetSemiPersistentItemsHook?.Dispose();
        _resetSemiPersistentItemsHook = null;

        _moveToEnterHook?.Dispose();
        _moveToEnterHook = null;

        _restBenchHelperStateEnterHook?.Dispose();
        _restBenchHelperStateEnterHook = null;

        _vector3AddEnterHook?.Dispose();
        _vector3AddEnterHook = null;

        _setPositionEnterHook?.Dispose();
        _setPositionEnterHook = null;

        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        _wakeUpPositions.Clear();
    }

    /// <summary>
    /// Respawns semi-persistent objects outside the current room after another player rested at a bench or died.
    /// The local player is in the current room, so it keeps its state until it is loaded again.
    /// </summary>
    /// <param name="data">The data with the ID of the player who rested or died.</param>
    public void OnSemiPersistentReset(GenericClientData data) {
        // Packets can arrive on the network receive thread, and the scene data belongs to the game's main thread
        ThreadUtil.RunActionOnMainThread(() => {
            var gameManager = global::GameManager.instance;
            if (gameManager == null) {
                return;
            }

            Logger.Info($"Player {data.Id} rested or died, respawning semi-persistent objects outside the current room");
            ResetSceneData(gameManager);
        });
    }

    /// <summary>
    /// Creates a hook on the OnEnter method of an FSM action type. The action type must declare the method itself,
    /// otherwise the hook would change the OnEnter of every action that inherits it.
    /// </summary>
    private static Hook? CreateOnEnterHook(Type actionType, Delegate detour) {
        var method = actionType.GetMethod("OnEnter", InstanceFlags);
        if (method == null || method.DeclaringType != actionType) {
            Logger.Error($"{actionType.Name} does not declare OnEnter; hook was not registered");
            return null;
        }

        return new Hook(method, detour);
    }

    /// <summary>
    /// Forgets the wake-up positions of the benches in the previous scene.
    /// </summary>
    private void OnActiveSceneChanged(Scene oldScene, Scene newScene) {
        _wakeUpPositions.Clear();
    }

    /// <summary>
    /// Shares a semi-persistent reset of the local player, from resting at a bench or dying, with the other players.
    /// While another player is in the same room, only the saved state is reset, so that the room itself stays as it is
    /// for both players until it is loaded again.
    /// </summary>
    private void OnResetSemiPersistentItems(Action<global::GameManager> orig, global::GameManager self) {
        if (IsOtherPlayerInLocalScene()) {
            ResetSceneData(self);
        } else {
            orig(self);
        }

        if (_netClient.IsConnected) {
            _netClient.UpdateManager.SetSemiPersistentReset();
        }
    }

    /// <summary>
    /// Checks whether another player is in the local player's scene.
    /// </summary>
    private bool IsOtherPlayerInLocalScene() {
        foreach (var playerData in _playerData.Values) {
            if (playerData.IsInLocalScene) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Clears the semi-persistent entries of the saved scene data, without resetting the objects of the loaded room.
    /// This is the second half of <c>GameManager.ResetSemiPersistentItems</c>, whose first half resets the loaded
    /// objects through the <c>ResetSemiPersistentObjects</c> event.
    /// </summary>
    private static void ResetSceneData(global::GameManager gameManager) {
        var sceneData = GameManagerSceneDataField?.GetValue(gameManager);
        if (sceneData == null || SceneDataResetMethod == null) {
            Logger.Warn("Could not reset the semi-persistent scene data");
            return;
        }

        SceneDataResetMethod.Invoke(sceneData, null);
    }

    /// <summary>
    /// Moves the seat that the bench FSM tweens the hero to towards the local player's side of the bench. The target is
    /// only changed for the call, so the FSM's variables stay untouched.
    /// </summary>
    private void OnMoveToEnter(Action<iTweenMoveTo> orig, iTweenMoveTo self) {
        if (!ShouldUseSides() || !IsBenchState(self, SitStateName)) {
            orig(self);
            return;
        }

        var originalPosition = self.vectorPosition;
        var basePosition = originalPosition == null || originalPosition.IsNone ? Vector3.zero : originalPosition.Value;

        self.vectorPosition = new FsmVector3 { Value = basePosition + new Vector3(GetSeatOffset(), 0f, 0f) };
        try {
            orig(self);
        } finally {
            self.vectorPosition = originalPosition;
        }
    }

    /// <summary>
    /// Moves the hero to their side of the bench when they wake up sitting on it. The game places the hero in the
    /// middle of the seat as the bench's respawn marker before the wake-up states run.
    /// </summary>
    private void OnRestBenchHelperStateEnter(Action<RestBenchHelperState> orig, RestBenchHelperState self) {
        orig(self);

        var hero = HeroController.instance;
        var bench = self.Fsm?.GameObject;
        if (!ShouldUseSides() || hero == null || bench == null || !IsBenchState(self, WakeUpStateNames)) {
            return;
        }

        var benchId = bench.GetInstanceID();
        if (_wakeUpPositions.TryGetValue(benchId, out var movedX) &&
            Mathf.Abs(hero.transform.position.x - movedX) <= MovedPositionTolerance) {
            return;
        }

        MoveHero(hero, GetSeatOffset());
        _wakeUpPositions[benchId] = hero.transform.position.x;
    }

    /// <summary>
    /// Moves the hero towards their side when they sit down without the tween, after the bench FSM snapped them to
    /// where they stand.
    /// </summary>
    private void OnVector3AddEnter(Action<Vector3Add> orig, Vector3Add self) {
        orig(self);

        var hero = HeroController.instance;
        if (!ShouldUseSides() || hero == null || !IsBenchState(self, SitNoTweenStateName)) {
            return;
        }

        MoveHero(hero, GetSeatOffset());
    }

    /// <summary>
    /// Keeps the hero on their side of the bench when the FSM settles them onto the seat in the state right after the
    /// tween. That state sets a position outright, which undid the offset the tween had just taken them to, so both
    /// players ended up in the middle of the seat after briefly moving to their side.
    ///
    /// Only a position being given to the hero is moved. The same action runs on all kinds of objects, and the target
    /// is resolved the way the game itself resolves it rather than assumed to be the hero: if this state turns out to
    /// position the bench rather than the hero, this does nothing at all instead of moving the wrong thing.
    /// </summary>
    private void OnSetPositionEnter(Action<SetPosition> orig, SetPosition self) {
        var hero = HeroController.instance;
        if (!ShouldUseSides() || hero == null || !IsBenchState(self, SitSettleStateName) ||
            self.Fsm?.GetOwnerDefaultTarget(self.gameObject) != hero.gameObject) {
            orig(self);
            return;
        }

        // Whichever of the two carries the position. The action lets x override the vector's x, so both are moved
        // when both are set, and both are put back afterwards so the FSM's own variables stay untouched.
        var originalVector = self.vector;
        var originalX = self.x;
        var offset = GetSeatOffset();

        if (originalVector is { IsNone: false }) {
            self.vector = new FsmVector3 { Value = originalVector.Value + new Vector3(offset, 0f, 0f) };
        }

        if (originalX is { IsNone: false }) {
            self.x = new FsmFloat { Value = originalX.Value + offset };
        }

        try {
            orig(self);
        } finally {
            self.vector = originalVector;
            self.x = originalX;
        }
    }

    /// <summary>
    /// Whether players sit on sides of benches, which they do while other players are connected.
    /// </summary>
    private bool ShouldUseSides() {
        return _playerData.Count > 0;
    }

    /// <summary>
    /// Gets how far the local player sits from the middle of the seat: to the right for the host and to the left for
    /// other players.
    /// </summary>
    private float GetSeatOffset() {
        return _saveManager.IsHostingServer ? SeatOffset : -SeatOffset;
    }

    /// <summary>
    /// Checks whether an action runs in one of the given states of the bench FSM.
    /// </summary>
    private static bool IsBenchState(FsmStateAction action, params string[] stateNames) {
        if (action.Fsm?.Name != BenchFsmName) {
            return false;
        }

        var stateName = action.State?.Name;
        return stateName != null && Array.IndexOf(stateNames, stateName) >= 0;
    }

    /// <summary>
    /// Moves the hero sideways, together with their rigidbody.
    /// </summary>
    private static void MoveHero(HeroController hero, float offset) {
        var position = hero.transform.position;
        position.x += offset;
        hero.transform.position = position;

        var body = hero.GetComponent<Rigidbody2D>();
        if (body != null) {
            body.position = position;
        }
    }
}
