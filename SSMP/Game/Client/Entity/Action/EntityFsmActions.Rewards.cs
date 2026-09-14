using System;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Packet.Data;
using Logger = SSMP.Logging.Logger;

// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedParameter.Local

namespace SSMP.Game.Client.Entity.Action;

/// <summary>
/// What entities give the player once they are defeated: journal entries, items, achievements, the messages for new
/// abilities and skills, saves, and the memories that some bosses send the player to. Entities only run for the scene
/// host, so the other players in the scene get the same rewards from their own copy of the action.
/// </summary>
internal static partial class EntityFsmActions {
    /// <summary>
    /// The prefix of the names of the memory scenes that some bosses send the player to once they are defeated.
    /// </summary>
    private const string MemorySceneNamePrefix = "Memory";

    #region RecordJournalKill

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData _, global::RecordJournalKill __) => true;

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData _, global::RecordJournalKill action) =>
        action.OnEnter();

    #endregion

    #region RecordJournalKillV2

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData _, global::RecordJournalKillV2 __) => true;

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData _, global::RecordJournalKillV2 action) =>
        action.OnEnter();

    #endregion

    #region CompleteJournalRecord

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData _, global::CompleteJournalRecord __) => true;

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData _, global::CompleteJournalRecord action) =>
        action.OnEnter();

    #endregion

    #region CollectableItemCollect

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData _, CollectableItemCollect __) => true;

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData _, CollectableItemCollect action) =>
        action.OnEnter();

    #endregion

    #region QueueAchievement

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData _, QueueAchievement __) => true;

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData _, QueueAchievement action) => action.OnEnter();

    #endregion

    #region AwardQueuedAchievements

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData _, AwardQueuedAchievements __) => true;

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData _, AwardQueuedAchievements action) =>
        action.OnEnter();

    #endregion

    #region AwardAchievementProgress

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData _, AwardAchievementProgress __) => true;

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData _, AwardAchievementProgress action) =>
        action.OnEnter();

    #endregion

    #region SpawnPowerUpGetMsg

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData _, SpawnPowerUpGetMsg __) => true;

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData _, SpawnPowerUpGetMsg action) =>
        action.OnEnter();

    #endregion

    #region SpawnSkillGetMsg

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData _, SpawnSkillGetMsg __) => true;

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData _, SpawnSkillGetMsg action) => action.OnEnter();

    #endregion

    #region SaveGame

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData _, global::SaveGame __) => true;

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData _, global::SaveGame action) => action.OnEnter();

    #endregion

    #region SaveGameV2

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData _, global::SaveGameV2 __) => true;

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData _, global::SaveGameV2 action) =>
        action.OnEnter();

    #endregion

    #region BeginSceneTransition

    /// <summary>
    /// Builds network data from the FSM action. Only the memories that bosses send the player to take the other
    /// players along; other scene changes of entities stay with the scene host.
    /// </summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData _, BeginSceneTransition action) {
        return action.sceneName.Value?.StartsWith(MemorySceneNamePrefix, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData _, BeginSceneTransition action) {
        // A player who is dying or already changing scenes can't be sent anywhere
        var heroController = HeroController.instance;
        var gameManager = global::GameManager.instance;
        if (heroController == null || gameManager == null || heroController.cState.dead ||
            gameManager.IsInSceneTransition) {
            Logger.Info($"Not following the scene host into memory scene '{action.sceneName.Value}'");
            return;
        }

        Logger.Info($"Following the scene host into memory scene '{action.sceneName.Value}'");
        action.OnEnter();
    }

    #endregion
}
