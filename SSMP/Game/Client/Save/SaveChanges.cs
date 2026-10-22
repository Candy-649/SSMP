using System.Linq;
using SSMP.Util;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Class that handles incoming save changes that have an immediate effect in the current scene for the local player.
/// E.g. breakable walls that also break in another scene, toll machines that are being paid, a fast travel station
/// being unlocked.
/// </summary>
internal class SaveChanges {
    /// <summary>
    /// Apply a change in player data from a save update for the given name immediately. This checks whether
    /// the local player is in a scene where the changes in player data have an effect on the environment.
    /// For example, a breakable wall that also opens up in another scene or a fast travel station being
    /// unlocked.
    /// </summary>
    /// <param name="name">The name of the PlayerData entry.</param>
    public void ApplyPlayerDataSaveChange(string? name) {
        Logger.Debug($"ApplyPlayerData for name: {name}");
        
        var currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
    }

    /// <summary>
    /// Apply a change in persistent values from a save update for the given name immediately. This checks whether
    /// the local player is in a scene where the changes in player data have an effect on the environment.
    /// For example, a breakable wall that also opens up in another scene or a fast travel station being
    /// unlocked.
    /// </summary>
    /// <param name="itemKey">The persistent item key containing the ID and scene name of the changed object.</param>
    public void ApplyPersistentValueSaveChange(PersistentItemKey itemKey) {
        Logger.Debug($"ApplyPersistent for item data: {itemKey}");

        var currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
    }
}
