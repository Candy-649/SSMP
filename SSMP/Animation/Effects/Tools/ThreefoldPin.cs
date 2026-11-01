using System.Collections.Generic;
using HutongGames.PlayMaker.Actions;
using SSMP.Internals;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// Class for the tool effect of the Threefold Pin.
/// </summary>
internal class ThreefoldPin : BaseAttackTool {
    /// <summary>
    /// Cached prefab for one attacking Threefold Pin, one for each character of another player: what is spawned from
    /// it wears what that player wears (see HeroCopies).
    /// </summary>
    private static readonly Dictionary<GameObject, GameObject> ModifiedPrefabs = new();

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        var poisoned = EffectIsPoisoned(effectInfo);
        var fsm = HeroController.instance.toolsFSM;

        // Set up modified prefab
        if (!ModifiedPrefabs.TryGetValue(playerObject, out var modifiedPrefab) || !modifiedPrefab) {
            var prefab = fsm.GetFirstAction<SpawnObjectFromGlobalPool>("TriPin Ground L");

            modifiedPrefab = EffectUtils.SpawnGlobalPoolObject(prefab, playerObject.transform, 0);
            if (!modifiedPrefab) return;

            modifiedPrefab.SetActive(false);
            modifiedPrefab.name = "THREEFOLD PIN";
            ModifiedPrefabs[playerObject] = modifiedPrefab;
        }

        // Play audio
        var audio = fsm.GetFirstAction<PlayAudioEventRandom>("TriPin Type");
        AudioUtil.PlayAudio(audio, playerObject);

        // Spawn pins where the thrower's went, which their game rolled and which also tells the throw in the air from
        // the one on the ground; an effect that does not say shows none
        var isOnWall = EffectIsOnWall(effectInfo);
        var placements = PlacementsOf(effectInfo);
        if (placements.Length == 0) {
            Logger.Info(
                $"[Tools] The partner's three pins at {playerObject.transform.position} came without where they went, " +
                "so no copies come out"
            );
            return;
        }

        for (var i = 0; i < placements.Length && i < 3; i++) {
            SpawnPin(modifiedPrefab, playerObject, isOnWall, poisoned, placements[i]);
        }
    }

    /// <summary>
    /// Spawns a copy of one of the three pins where the thrower's went.
    /// </summary>
    /// <param name="modifiedPrefab">The player's prefab of the pin.</param>
    /// <param name="playerObject">The player using the pin.</param>
    /// <param name="onWall">If the player is on a wall or not.</param>
    /// <param name="poisoned">If the pin is poisoned or not.</param>
    /// <param name="placement">Where the thrower's pin went.</param>
    private void SpawnPin(
        GameObject modifiedPrefab,
        GameObject playerObject,
        bool onWall,
        bool poisoned,
        ToolPlacements.Placement placement
    ) {
        // Spawn pin
        var player = playerObject.transform.position;
        var pin = modifiedPrefab.Spawn(player);
        if (!pin) return;

        // The way the pin flies: the way the player faces, or away from the wall they are on
        var facingDirection = playerObject.transform.localScale.x * (onWall ? -1 : 1);
        pin.SendMessage("SetDirection", facingDirection < 0 ? 0 : 180, SendMessageOptions.DontRequireReceiver);

        // Put it where the thrower's went, turned and sized the same
        placement.PutAt(pin.transform, player);

        // Set initial velocity
        if (!pin.TryGetComponent<Rigidbody2D>(out var body)) {
            return;
        }

        body.linearVelocity = placement.Velocity;

        SetDamageHeroState(pin, ServerSettings.ThreefoldPinDamage);
        pin.AddComponentIfNotPresent<RemoteAttackComponent>();

        // Set poison settings and deflection
        if (pin.TryGetComponent<ToolPin>(out var controller)) {
            StraightPin.SetPinPoison(controller, poisoned);

            // Allows deflecting pins, but causes some side effects that make it look a bit worse (disappears
            // immediately after hitting walls)
            controller.tinked = ServerSettings.IsPvpEnabled && ShouldDoDamage;
        }
    }
}
