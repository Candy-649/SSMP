using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Packet.Data;
using UnityEngine;

// ReSharper disable UnusedMember.Local

namespace SSMP.Game.Client.Entity.Action;

/// <summary>
/// How much money entities drop. Some enemies change their drops while they fight, but entities only run for the scene
/// host, so the copies of the other players would still drop what the enemy started with. Every player spawns the drops
/// of their own copy once it dies, so each player gets their own drops.
/// </summary>
internal static partial class EntityFsmActions {
    #region SetGeoDrop

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetGeoDrop action) {
        WriteOptionalInt(data, action.smallGeo);
        WriteOptionalInt(data, action.mediumGeo);
        WriteOptionalInt(data, action.largeGeo);
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetGeoDrop action) {
        var small = ReadOptionalInt(data);
        var medium = ReadOptionalInt(data);
        var large = ReadOptionalInt(data);

        var healthManager = GetHealthManager(action.Fsm.GetOwnerDefaultTarget(action.target));
        if (healthManager == null) {
            return;
        }

        if (small.HasValue) {
            healthManager.SetGeoSmall(small.Value);
        }

        if (medium.HasValue) {
            healthManager.SetGeoMedium(medium.Value);
        }

        if (large.HasValue) {
            healthManager.SetGeoLarge(large.Value);
        }
    }

    #endregion

    #region SetShardDrop

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetShardDrop action) {
        WriteOptionalInt(data, action.shards);
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetShardDrop action) {
        var shards = ReadOptionalInt(data);
        var healthManager = GetHealthManager(action.Fsm.GetOwnerDefaultTarget(action.target));
        if (healthManager != null && shards.HasValue) {
            healthManager.SetShellShards(shards.Value);
        }
    }

    #endregion

    /// <summary>
    /// Writes an FSM integer, or that it isn't set.
    /// </summary>
    private static void WriteOptionalInt(EntityNetworkData data, FsmInt value) {
        data.Packet.Write(!value.IsNone);
        if (!value.IsNone) {
            data.Packet.Write(value.Value);
        }
    }

    /// <summary>
    /// Reads an FSM integer that <see cref="WriteOptionalInt"/> wrote, or null if it wasn't set.
    /// </summary>
    private static int? ReadOptionalInt(EntityNetworkData data) {
        return data.Packet.ReadBool() ? data.Packet.ReadInt() : null;
    }

    /// <summary>
    /// The health manager of the object that a drop action targets, or null.
    /// </summary>
    private static HealthManager? GetHealthManager(GameObject? gameObject) {
        return gameObject == null ? null : gameObject.GetComponent<HealthManager>();
    }
}
