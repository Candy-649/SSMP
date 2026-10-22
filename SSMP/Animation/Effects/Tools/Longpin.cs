using System;
using System.Collections.Generic;
using HutongGames.PlayMaker.Actions;
using SSMP.Internals;
using SSMP.Util;
using UnityEngine;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// Class for the tool effect of the Longpin.
/// </summary>
internal class Longpin : BaseAttackTool {
    /// <summary>
    /// Cached prefab for one attacking pin.
    /// </summary>
    private static GameObject? _modifiedPrefab;

    /// <summary>
    /// Map of player object IDs to their last pin throw time.
    /// </summary>
    private static readonly Dictionary<int, long> PlayerLastFireTime = [];

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        var poisoned = EffectIsPoisoned(effectInfo);
        var isOnWall = EffectIsOnWall(effectInfo);

        // Determine spawn position
        var spawnPosition = playerObject.transform.position + new Vector3(0, 0.2f, 0);
        var facingRight = playerObject.transform.localScale.x < 0;

        if (isOnWall) {
            facingRight = !facingRight;
        }

        var prefab = GetPrefab(playerObject);
        if (!prefab) return;

        // Get the last time a Longpin was fired
        var key = playerObject.GetInstanceID();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var lastFireTime = PlayerLastFireTime.GetValueOrDefault(key, now);

        PlayerLastFireTime[key] = now;

        // Determine angle based on if the last pin was recent enough (second quick sling shot)
        var angle = UnityEngine.Random.Range(147, 153);

        if (now - lastFireTime < 500) {
            angle = UnityEngine.Random.Range(134, 140);
        }

        var placements = PlacementsOf(effectInfo);
        var placement = placements.Length > 0 ? placements[0] : (ToolPlacements.Placement?) null;
        SpawnPin(prefab, spawnPosition, angle, facingRight, poisoned, placement, playerObject.transform.position);
    }

    /// <summary>
    /// Gets the modified Longpin prefab.
    /// </summary>
    /// <param name="playerObject">The player using the Longpin.</param>
    /// <returns>The Longpin prefab.</returns>
    private static GameObject? GetPrefab(GameObject playerObject) {
        // If the modified prefab exists already, return it
        if (_modifiedPrefab) return _modifiedPrefab;

        // Locate the original prefab
        var fsm = HeroController.instance.toolsFSM;

        var prefab = fsm.GetFirstAction<SpawnProjectileV2>("Fisherpin");

        // Create a new version to modify
        _modifiedPrefab = EffectUtils.SpawnGlobalPoolObject(prefab.Prefab.Value, playerObject.transform, 0);
        if (!_modifiedPrefab) return null;

        _modifiedPrefab.SetActive(false);
        _modifiedPrefab.name = "LONGPIN";

        // Set up rebound effect
        var longPinTool = _modifiedPrefab.GetComponent<ToolPin>();
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

        return _modifiedPrefab;
    }

    /// <summary>
    /// Spawns a Longpin at the given position.
    /// </summary>
    /// <param name="prefab">The Longpin prefab.</param>
    /// <param name="spawnPosition">The position to spawn the Longpin.</param>
    /// <param name="angle">The angle to fire the Longpin at.</param>
    /// <param name="facingRight">Whether the player is facing right.</param>
    /// <param name="poisoned">Whether the Longpin is poisoned.</param>
    /// <param name="placement">Where the thrower's pin went, if known.</param>
    /// <param name="player">Where the player who threw it is.</param>
    private void SpawnPin(
        GameObject prefab,
        Vector3 spawnPosition,
        float angle,
        bool facingRight,
        bool poisoned,
        ToolPlacements.Placement? placement,
        Vector3 player
    ) {
        // Spawn pin
        var pin = prefab.Spawn(spawnPosition);
        if (!pin) return;

        pin.SetActive(false);
        pin.transform.localScale = prefab.transform.localScale;

        var x = 35 * Mathf.Cos(angle * (Mathf.PI / 180f));
        var y = 35 * Mathf.Sin(angle * (Mathf.PI / 180f));

        // Set initial velocity
        if (!pin.TryGetComponent<Rigidbody2D>(out var body)) {
            return;
        }

        // Make the pin face the correct way
        if (facingRight) {
            x *= -1;
        }

        if (x < 0) {
            pin.transform.FlipLocalScale(true);
        }

        // Set the rotation and activate
        pin.transform.SetRotationZ(angle);
        var velocity = new Vector2(x, y);
        if (placement is { } place) {
            place.PutAt(pin.transform, player);
            velocity = place.Velocity;
        }

        pin.SetActive(true);

        // Set the velocity
        body.linearVelocity = velocity;

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
