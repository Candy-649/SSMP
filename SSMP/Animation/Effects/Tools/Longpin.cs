using System.Collections.Generic;
using HutongGames.PlayMaker.Actions;
using SSMP.Internals;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// Class for the tool effect of the Longpin.
/// </summary>
internal class Longpin : BaseAttackTool {
    /// <summary>
    /// Cached prefab for one attacking pin, one for each character of another player: what is spawned from it wears
    /// what that player wears (see HeroCopies).
    /// </summary>
    private static readonly Dictionary<GameObject, GameObject> ModifiedPrefabs = new();

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        var poisoned = EffectIsPoisoned(effectInfo);
        var position = playerObject.transform.position;

        // The pin goes where the thrower's went, which their game rolled; an effect that does not say shows none
        var placements = PlacementsOf(effectInfo);
        if (placements.Length == 0) {
            Logger.Info($"[Tools] The partner's long pin at {position} came without where it went, so no copy comes out");
            return;
        }

        var prefab = GetPrefab(playerObject);
        if (!prefab) return;

        SpawnPin(prefab, poisoned, placements[0], position);
    }

    /// <summary>
    /// Gets the modified Longpin prefab.
    /// </summary>
    /// <param name="playerObject">The player using the Longpin.</param>
    /// <returns>The Longpin prefab.</returns>
    private static GameObject? GetPrefab(GameObject playerObject) {
        // If the character's modified prefab exists already, return it
        if (ModifiedPrefabs.TryGetValue(playerObject, out var modifiedPrefab) && modifiedPrefab) {
            return modifiedPrefab;
        }

        // Locate the original prefab
        var fsm = HeroController.instance.toolsFSM;

        var prefab = fsm.GetFirstAction<SpawnProjectileV2>("Fisherpin");

        // Create a new version to modify
        modifiedPrefab = EffectUtils.SpawnGlobalPoolObject(prefab.Prefab.Value, playerObject.transform, 0);
        if (!modifiedPrefab) return null;

        ModifiedPrefabs[playerObject] = modifiedPrefab;

        modifiedPrefab.SetActive(false);
        modifiedPrefab.name = "LONGPIN";

        // Set up rebound effect
        var longPinTool = modifiedPrefab.GetComponent<ToolPin>();
        var straightPin = ToolItemManager.GetToolByName("Straight Pin");

        if (straightPin is ToolItemBasic pinTool) {
            var pinPrefab = pinTool.usageOptions.ThrowPrefab;
            var pinReboundHit = pinPrefab.GetComponent<ToolPin>()?.reboundHitEffectGameobject;

            longPinTool.reboundHitEffectGameobject = pinReboundHit;
        }

        // Set up rebound box
        var reboundBoxObject = new GameObject("Rebound Box");
        reboundBoxObject.SetActive(false);

        var reboundBox = reboundBoxObject.AddComponent<Rigidbody2D>();
        reboundBox.bodyType = RigidbodyType2D.Kinematic;

        longPinTool.reboundBox = reboundBoxObject;

        return modifiedPrefab;
    }

    /// <summary>
    /// Spawns a copy of a Longpin where the thrower's went.
    /// </summary>
    /// <param name="prefab">The Longpin prefab.</param>
    /// <param name="poisoned">Whether the Longpin is poisoned.</param>
    /// <param name="placement">Where the thrower's pin went.</param>
    /// <param name="player">Where the player who threw it is.</param>
    private void SpawnPin(GameObject prefab, bool poisoned, ToolPlacements.Placement placement, Vector3 player) {
        // Spawn pin
        var pin = prefab.Spawn(player);
        if (!pin) return;

        pin.SetActive(false);
        pin.transform.localScale = prefab.transform.localScale;

        if (!pin.TryGetComponent<Rigidbody2D>(out var body)) {
            return;
        }

        // Put it where the thrower's went, turned and sized the same, and activate
        placement.PutAt(pin.transform, player);
        pin.SetActive(true);

        // Set the velocity
        body.linearVelocity = placement.Velocity;

        // Set damage settings
        SetDamageHeroState(pin, ServerSettings.LongpinDamage);
        pin.AddComponentIfNotPresent<RemoteAttackComponent>();

        // Set poison settings and deflection
        if (pin.TryGetComponent<ToolPin>(out var controller)) {
            StraightPin.SetPinPoison(controller, poisoned);

            // Allows deflecting pins, but causes some side effects that make it look a bit worse (disappears
            // immediately after hitting walls)
            controller.tinked = ServerSettings.IsPvpEnabled && ShouldDoDamage;
            controller.VelocityWasSet();
        }
    }
}
