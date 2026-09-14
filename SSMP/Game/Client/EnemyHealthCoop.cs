using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Makes enemies tougher while several players are in the same scene, like co-op games that raise enemy HP per player:
/// each extra player adds half of the solo health.
/// Enemy HP comes from the scene every time it loads, and many FSMs set or compare fixed HP values to change phases,
/// so the HP itself is left alone. Instead, the damage that enemies take is divided by the health multiplier. With two
/// players an enemy takes 2/3 of the damage, which needs as many hits as 1.5 times the HP. The fraction lost to
/// rounding carries over to the enemy's next hit, so the total stays the same.
/// </summary>
internal class EnemyHealthCoop {
    /// <summary>
    /// How much health each extra player in the scene adds, as a fraction of the solo health.
    /// </summary>
    private const float HealthPerExtraPlayer = 0.5f;

    /// <summary>
    /// Binding flags for the private members of the game.
    /// </summary>
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// The data of the other connected players, by player ID.
    /// </summary>
    private readonly Dictionary<ushort, ClientPlayerData> _playerData;

    /// <summary>
    /// The damage left over from rounding, per enemy instance ID.
    /// </summary>
    private readonly Dictionary<int, float> _damageRemainders = new();

    /// <summary>
    /// Hook for scaling the damage of hits, which all go through <c>HealthManager.ApplyDamageScaling</c>.
    /// </summary>
    private Hook? _applyDamageScalingHook;

    /// <summary>
    /// Hook for scaling extra damage that is applied as a plain amount, such as damage over time.
    /// </summary>
    private Hook? _applyExtraDamageHook;

    public EnemyHealthCoop(Dictionary<ushort, ClientPlayerData> playerData) {
        _playerData = playerData;
    }

    /// <summary>
    /// Registers the hooks for enemy health co-op.
    /// </summary>
    public void RegisterHooks() {
        _applyDamageScalingHook = CreateHook(
            typeof(HealthManager).GetMethod(
                "ApplyDamageScaling", InstanceFlags, null, [typeof(HitInstance)], null
            ),
            new Func<Func<HealthManager, HitInstance, HitInstance>, HealthManager, HitInstance, HitInstance>(
                OnApplyDamageScaling
            )
        );
        _applyExtraDamageHook = CreateHook(
            typeof(HealthManager).GetMethod("ApplyExtraDamage", InstanceFlags, null, [typeof(int)], null),
            new Action<Action<HealthManager, int>, HealthManager, int>(OnApplyExtraDamage)
        );

        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    /// <summary>
    /// Disposes the hooks for enemy health co-op.
    /// </summary>
    public void DeregisterHooks() {
        _applyDamageScalingHook?.Dispose();
        _applyDamageScalingHook = null;

        _applyExtraDamageHook?.Dispose();
        _applyExtraDamageHook = null;

        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        _damageRemainders.Clear();
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
    /// Forgets the rounding remainders of the enemies in the previous scene.
    /// </summary>
    private void OnActiveSceneChanged(Scene oldScene, Scene newScene) {
        _damageRemainders.Clear();
    }

    /// <summary>
    /// Scales the damage of a hit after the game applied its own damage scaling.
    /// </summary>
    private HitInstance OnApplyDamageScaling(
        Func<HealthManager, HitInstance, HitInstance> orig,
        HealthManager self,
        HitInstance hitInstance
    ) {
        var result = orig(self, hitInstance);
        result.DamageDealt = ScaleDamage(self, result.DamageDealt);
        return result;
    }

    /// <summary>
    /// Scales extra damage that is applied as a plain amount.
    /// </summary>
    private void OnApplyExtraDamage(Action<HealthManager, int> orig, HealthManager self, int damageAmount) {
        orig(self, ScaleDamage(self, damageAmount));
    }

    /// <summary>
    /// Divides damage to an enemy by the health multiplier for the players in the scene. Hits keep at least 1 damage
    /// so that they still count as hits.
    /// </summary>
    /// <param name="healthManager">The enemy that takes the damage.</param>
    /// <param name="damage">The damage before co-op scaling.</param>
    /// <returns>The scaled damage.</returns>
    private int ScaleDamage(HealthManager healthManager, int damage) {
        var multiplier = GetHealthMultiplier();
        if (damage <= 0 || multiplier <= 1f) {
            return damage;
        }

        var id = healthManager.GetInstanceID();
        _damageRemainders.TryGetValue(id, out var remainder);

        var scaled = damage / multiplier + remainder;
        var result = Mathf.Max(1, Mathf.RoundToInt(scaled));

        // Keep the remainder small, so that hits raised to 1 damage don't pile up a debt for later hits
        _damageRemainders[id] = Mathf.Clamp(scaled - result, -0.5f, 0.5f);
        return result;
    }

    /// <summary>
    /// Gets the health multiplier for the number of connected players. It doesn't matter who is in the scene: an enemy
    /// is as tough for a player who fights it alone as for players who fight it together.
    /// </summary>
    private float GetHealthMultiplier() {
        return 1f + HealthPerExtraPlayer * _playerData.Count;
    }
}
