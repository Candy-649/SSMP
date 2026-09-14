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
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Co-op rules for resting at benches.
/// When a player rests at a bench or dies, semi-persistent objects such as enemies respawn for every player. A room
/// that a player is in at that moment keeps its state until it is loaded again, so enemies that are fighting a player
/// don't respawn around them. Players who sit on the same bench take opposite sides of it: the host sits on the right
/// and other players on the left.
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
    /// How far, in units, each player sits from the middle of the seat while other players are connected. This is a
    /// guess that keeps two sitting Hornets apart on a normal bench, to be tuned in game.
    /// </summary>
    private const float SeatOffset = 0.75f;

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
    /// Hook for sharing semi-persistent resets with other players.
    /// </summary>
    private Hook? _resetSemiPersistentItemsHook;

    /// <summary>
    /// Hook for moving the hero to their side of the bench.
    /// </summary>
    private Hook? _moveToEnterHook;

    public BenchCoop(NetClient netClient, Dictionary<ushort, ClientPlayerData> playerData, SaveManager saveManager) {
        _netClient = netClient;
        _playerData = playerData;
        _saveManager = saveManager;
    }

    /// <summary>
    /// Registers the hooks for bench co-op.
    /// </summary>
    public void RegisterHooks() {
        _resetSemiPersistentItemsHook = CreateHook(
            typeof(global::GameManager).GetMethod("ResetSemiPersistentItems", InstanceFlags),
            new Action<Action<global::GameManager>, global::GameManager>(OnResetSemiPersistentItems)
        );
        _moveToEnterHook = CreateHook(
            typeof(iTweenMoveTo).GetMethod("OnEnter", InstanceFlags),
            new Action<Action<iTweenMoveTo>, iTweenMoveTo>(OnMoveToEnter)
        );
    }

    /// <summary>
    /// Disposes the hooks for bench co-op.
    /// </summary>
    public void DeregisterHooks() {
        _resetSemiPersistentItemsHook?.Dispose();
        _resetSemiPersistentItemsHook = null;

        _moveToEnterHook?.Dispose();
        _moveToEnterHook = null;
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
    /// Creates a hook, logging an error instead of throwing if the method does not exist.
    /// </summary>
    private static Hook? CreateHook(MethodInfo? method, Delegate detour) {
        if (method == null) {
            Logger.Error($"Could not find the method for {detour.Method.Name}; hook was not registered");
            return null;
        }

        return new Hook(method, detour);
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
    /// Moves the seat that the bench FSM tweens the hero to towards the local player's side of the bench while other
    /// players are connected. The target is only changed for the call, so the FSM's variables stay untouched.
    /// </summary>
    private void OnMoveToEnter(Action<iTweenMoveTo> orig, iTweenMoveTo self) {
        if (_playerData.Count == 0 || self.Fsm?.Name != BenchFsmName || self.State?.Name != SitStateName) {
            orig(self);
            return;
        }

        var offset = _saveManager.IsHostingServer ? SeatOffset : -SeatOffset;
        var originalPosition = self.vectorPosition;
        var basePosition = originalPosition == null || originalPosition.IsNone ? Vector3.zero : originalPosition.Value;

        self.vectorPosition = new FsmVector3 { Value = basePosition + new Vector3(offset, 0f, 0f) };
        try {
            orig(self);
        } finally {
            self.vectorPosition = originalPosition;
        }
    }
}
