using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The ends of the bosses that are fought in a memory, which play the template heart_death_outro. Such an end gives
/// nothing but what its state writes to the player data: that the boss is beaten, and what the rest of the world asks
/// about it. A player who dies in a memory is out of it, back in the room they came from, while the others may fight on;
/// the end then counted only for the players still in the memory, as the records of a boss went to the players in its
/// scene alone. The boss stayed unbeaten for the one who was out, who could not fight it either, since a boss fight
/// starts only once every player is in its room. What the state of such an end writes now reaches every other player,
/// wherever they are, and what the room they are in shows of it, like the entrance of the memory, is looked at again.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// The name of the template that the end of the boss of a memory plays.
    /// </summary>
    private const string HeartEndTemplateName = "heart_death_outro";

    /// <summary>
    /// The method of the game that switches an object off when its record of the player data is set.
    /// </summary>
    private static readonly MethodInfo? DeactivatorEvaluate =
        typeof(DeactivateIfPlayerdataTrue).GetMethod("ForceEvaluate", InstanceFlags, null, Type.EmptyTypes, null);

    /// <summary>
    /// The method of the game that switches objects on or off by a test of the player data.
    /// </summary>
    private static readonly MethodInfo? ActivatorEvaluate =
        typeof(TestGameObjectActivator).GetMethod("DoEvaluate", InstanceFlags, null, Type.EmptyTypes, null);

    /// <summary>
    /// The test of the player data of an object that switches objects on or off by it.
    /// </summary>
    private static readonly FieldInfo? ActivatorTest =
        typeof(TestGameObjectActivator).GetField("playerDataTest", InstanceFlags);

    /// <summary>
    /// The records from the ends of other players' memories whose objects in the loaded rooms are still to be looked at
    /// again, once the local player is free to move.
    /// </summary>
    private readonly HashSet<string> _heartRecordsToShow = [];

    /// <summary>
    /// Shares a write of the end of the boss of a memory with every other player, wherever they are. A write whose value
    /// the state reads from the player's own data first, like whether they had beaten the boss after another memory of
    /// their own, goes along with each of the others as a write from the receiving player's own data.
    /// </summary>
    /// <returns>Whether the write was made by such an end, which is then not shared in any other way.</returns>
    private bool TryShareHeartEnd(string name, Fsm fsm) {
        if (fsm.ActiveState is not { } state || !PlaysHeartEnd(state)) {
            return false;
        }

        if (IsHeroStateName(name) || !_netClient.IsConnected || !_isFullSynchronisation() ||
            GetOwnDataWrites(state).Any(write => write.Written == name)) {
            return true;
        }

        Logger.Info($"Sharing '{name}' of the end of the boss of a memory with every other player, wherever they are");
        Send(BossRoomUpdateKind.HeartEndRecord, "", "", "", "", "", name);
        foreach (var (written, source) in GetOwnDataWrites(state)) {
            Send(BossRoomUpdateKind.HeartEndRecord, "", "", source, "", "", written);
        }

        return true;
    }

    /// <summary>
    /// Writes what the end of the boss of a memory wrote in the game of another player, wherever the local player is.
    /// A write that goes by the player's own data takes the local player's.
    /// </summary>
    private void OnHeartEndRecord(BossRoomUpdate update) {
        var playerData = PlayerData.instance;
        var name = update.VariableName;
        if (playerData == null || name.Length == 0 || IsHeroStateName(name)) {
            return;
        }

        var source = update.FromState;
        if (source.Length > 0 && !playerData.GetBool(source)) {
            return;
        }

        Logger.Info(
            $"Another player shared '{name}' of the end of the boss of a memory in '{update.SceneName}'" +
            (source.Length > 0 ? $", which goes by the local player's own '{source}'" : "")
        );
        _applyingRecord = true;
        try {
            playerData.SetBool(name, true);
        } finally {
            _applyingRecord = false;
        }

        // A player in the same memory plays its end in their own game
        if (SceneManager.GetActiveScene().name != update.SceneName) {
            _heartRecordsToShow.Add(name);
            ShowHeartRecords();
        }
    }

    /// <summary>
    /// Looks again at the objects of the loaded rooms that the game switches on or off by the records that came from the
    /// end of another player's memory, which it only does as their room loads: the entrance of the memory itself would
    /// stay open, into a memory whose boss is beaten, which has no way out then. That waits while the local player is
    /// not free to move, so that an entrance they are on the way through is not switched off under them.
    /// </summary>
    private void ShowHeartRecords() {
        if (_heartRecordsToShow.Count == 0 || !IsLocalHeroFree()) {
            return;
        }

        var names = _heartRecordsToShow.ToList();
        _heartRecordsToShow.Clear();

        foreach (var deactivator in Object.FindObjectsByType<DeactivateIfPlayerdataTrue>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None
                 )) {
            if (!names.Contains(deactivator.boolName)) {
                continue;
            }

            Logger.Info($"Looking again at '{ScenePath.Get(deactivator.transform)}', which goes by '{deactivator.boolName}'");
            try {
                DeactivatorEvaluate?.Invoke(deactivator, null);
            } catch (Exception e) {
                Logger.Warn($"Could not look again at '{deactivator.name}':\n{e}");
            }
        }

        foreach (var activator in Object.FindObjectsByType<TestGameObjectActivator>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None
                 )) {
            if (!activator.isActiveAndEnabled || ActivatorTest?.GetValue(activator) is not PlayerDataTest test ||
                !TestsAny(test, names)) {
                continue;
            }

            Logger.Info($"Looking again at '{ScenePath.Get(activator.transform)}', which tests a record of a memory's end");
            try {
                ActivatorEvaluate?.Invoke(activator, null);
            } catch (Exception e) {
                Logger.Warn($"Could not look again at '{activator.name}':\n{e}");
            }
        }
    }

    /// <summary>
    /// Forgets the records still to be shown, as the next room shows them by itself when it loads.
    /// </summary>
    private void ClearHeartRecords() {
        _heartRecordsToShow.Clear();
    }

    /// <summary>
    /// Whether a test of the player data tests any of the given records.
    /// </summary>
    private static bool TestsAny(PlayerDataTest test, ICollection<string> names) {
        foreach (var group in test.TestGroups ?? []) {
            foreach (var condition in group.Tests ?? []) {
                if (names.Contains(condition.FieldName)) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a state plays the end of the boss of a memory.
    /// </summary>
    private static bool PlaysHeartEnd(FsmState state) {
        foreach (var action in state.Actions ?? []) {
            if (action is RunFSM { fsmTemplateControl.fsmTemplate: { } template } &&
                template.name == HeartEndTemplateName) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The writes of a state whose value the state reads from the player's own data first, with the record each is read
    /// from.
    /// </summary>
    private static IEnumerable<(string Written, string Source)> GetOwnDataWrites(FsmState state) {
        foreach (var action in state.Actions ?? []) {
            if (action is not HutongGames.PlayMaker.Actions.SetPlayerDataBool {
                    boolName.Value: { Length: > 0 } written, value: { UseVariable: true, Name: { Length: > 0 } variable }
                }) {
                continue;
            }

            foreach (var read in state.Actions ?? []) {
                if (read is HutongGames.PlayMaker.Actions.GetPlayerDataBool {
                        boolName.Value: { Length: > 0 } source, storeValue.Name: { } stored
                    } && stored == variable) {
                    yield return (written, source);
                    break;
                }
            }
        }
    }
}
