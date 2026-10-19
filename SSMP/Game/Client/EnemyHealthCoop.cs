using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SSMP.Api.Server;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// How the health of enemies grows with the number of players connected, which the host of the server chooses for
/// everyone (<see cref="IServerSettings.EnemyHealthMode"/>, on the settings page of the multiplayer menu). Asked for by
/// a player on 2026-10-19, with these four ways to choose from.
/// </summary>
internal enum EnemyHealthMode : byte {
    /// <summary>Each extra player adds half of the health of one player alone, as it always was.</summary>
    Default = 0,

    /// <summary>Each extra player multiplies the health by n.</summary>
    Exponential = 1,

    /// <summary>Each extra player adds n times the health of one player alone.</summary>
    Linear = 2,

    /// <summary>The health is n times that of one player alone, however many play.</summary>
    Fixed = 3
}

/// <summary>
/// Makes enemies tougher while several players are connected, like co-op games that raise enemy HP per player: by
/// default each extra player adds half of the solo health, and the host of the server can choose another way
/// (<see cref="EnemyHealthMode"/>).
/// Enemy HP comes from the scene every time it loads, and many FSMs set or compare fixed HP values to change phases,
/// so the HP itself is left alone. Instead, the damage that enemies take is divided by the health multiplier. With two
/// players an enemy takes 2/3 of the damage, which needs as many hits as 1.5 times the HP. The fraction lost to
/// rounding carries over to the enemy's next hit, so the total stays the same. A multiplier below one makes the
/// damage larger the same way.
///
/// One thing is left out of it: a blow that would kill an untouched creature outright still kills it. Creatures built
/// to die to a single hit of a particular weapon are used that way by the game, and some wishes only work if they do.
/// Since it is asked only of a creature at full health, nothing that takes more than one hit is made any easier.
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
    /// The settings of the server this game is connected to, which say how enemy health grows with the players.
    /// </summary>
    private readonly IServerSettings _serverSettings;

    /// <summary>
    /// The multiplier last written to the log, so that it is written down once each time it changes.
    /// </summary>
    private float _toldMultiplier = 1f;

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

    public EnemyHealthCoop(Dictionary<ushort, ClientPlayerData> playerData, IServerSettings serverSettings) {
        _playerData = playerData;
        _serverSettings = serverSettings;
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
        if (damage <= 0 || Mathf.Approximately(multiplier, 1f)) {
            return damage;
        }

        // A blow that was meant to kill outright still does. Some creatures are built to die to one hit of a
        // particular weapon, and some wishes are written around that being true - so scaling that one hit down turns
        // something that was designed as a single action into a fight, or into something that cannot be done at all.
        //
        // Only a full-strength blow on a creature that has not been touched counts. Nothing else is made easier by
        // it: a creature that takes three hits alone still takes five together, because by the second hit it is no
        // longer whole and this is not asked again. It is only the one that was never meant to take more than one
        // that is left alone.
        if (healthManager.initHp > 0 && healthManager.hp >= healthManager.initHp && damage >= healthManager.hp) {
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
        var multiplier = HealthMultiplier(_serverSettings, _playerData.Count);
        if (!Mathf.Approximately(multiplier, _toldMultiplier)) {
            _toldMultiplier = multiplier;
            Logger.Info(
                $"[EnemyHealth] Enemies take their damage divided by {multiplier:0.###}: " +
                $"{(EnemyHealthMode) _serverSettings.EnemyHealthMode} with n " +
                $"{Factor(_serverSettings, (EnemyHealthMode) _serverSettings.EnemyHealthMode):0.0}, " +
                $"{_playerData.Count + 1} players connected"
            );
        }

        return multiplier;
    }

    /// <summary>
    /// The health multiplier that the given settings make for the given number of other players connected.
    /// </summary>
    /// <param name="settings">The settings of the server.</param>
    /// <param name="otherPlayers">How many players are connected besides this one.</param>
    internal static float HealthMultiplier(IServerSettings settings, int otherPlayers) {
        var others = Mathf.Max(0, otherPlayers);
        var mode = (EnemyHealthMode) settings.EnemyHealthMode;
        return mode switch {
            EnemyHealthMode.Exponential => Mathf.Pow(Factor(settings, mode), others),
            EnemyHealthMode.Linear => 1f + Factor(settings, mode) * others,
            EnemyHealthMode.Fixed => Factor(settings, mode),
            _ => 1f + HealthPerExtraPlayer * others
        };
    }

    /// <summary>
    /// The smallest and the largest n of each way that has one, in tenths, as the settings keep it.
    /// </summary>
    /// <param name="mode">The way.</param>
    internal static (byte Min, byte Max) FactorRange(EnemyHealthMode mode) => mode switch {
        EnemyHealthMode.Exponential => (10, 30),
        EnemyHealthMode.Linear => (0, 30),
        EnemyHealthMode.Fixed => (5, 50),
        _ => (5, 5)
    };

    /// <summary>
    /// The n of the given way as the settings keep it, in tenths: each way keeps its own.
    /// </summary>
    /// <param name="settings">The settings of the server.</param>
    /// <param name="mode">The way.</param>
    internal static byte FactorTenths(IServerSettings settings, EnemyHealthMode mode) => mode switch {
        EnemyHealthMode.Exponential => settings.EnemyHealthExponentialFactor,
        EnemyHealthMode.Linear => settings.EnemyHealthLinearFactor,
        EnemyHealthMode.Fixed => settings.EnemyHealthFixedFactor,
        _ => (byte) Mathf.RoundToInt(HealthPerExtraPlayer * 10f)
    };

    /// <summary>
    /// The n of the given way, kept within its range (<see cref="FactorRange"/>): a setting written by hand on the
    /// server may lie outside it.
    /// </summary>
    /// <param name="settings">The settings of the server.</param>
    /// <param name="mode">The way.</param>
    internal static float Factor(IServerSettings settings, EnemyHealthMode mode) {
        var (min, max) = FactorRange(mode);
        return Mathf.Clamp(FactorTenths(settings, mode), min, max) / 10f;
    }

    /// <summary>
    /// The name of a way, for the buttons that choose it.
    /// </summary>
    /// <param name="mode">The way.</param>
    internal static string ModeName(EnemyHealthMode mode) => mode switch {
        EnemyHealthMode.Exponential => Lang.Pick("POWER", "指数"),
        EnemyHealthMode.Linear => Lang.Pick("LINEAR", "线性"),
        EnemyHealthMode.Fixed => Lang.Pick("FIXED", "固定"),
        _ => Lang.Pick("DEFAULT", "联机默认")
    };

    /// <summary>
    /// Says in a sentence what the given settings make of enemy health, with what two and three players fight against.
    /// </summary>
    /// <param name="settings">The settings of the server.</param>
    internal static string Describe(IServerSettings settings) {
        var mode = (EnemyHealthMode) settings.EnemyHealthMode;
        var n = Factor(settings, mode);
        var two = HealthMultiplier(settings, 1);
        var three = HealthMultiplier(settings, 2);
        return mode switch {
            EnemyHealthMode.Exponential => Lang.Pick(
                $"Each extra player multiplies enemy health by {n:0.0}: x{two:0.##} for two, x{three:0.##} for three.",
                $"每多一个人，怪血再乘 {n:0.0}：两个人 ×{two:0.##}，三个人 ×{three:0.##}。"
            ),
            EnemyHealthMode.Linear => Lang.Pick(
                $"Each extra player adds {n:0.0} times the solo health: x{two:0.##} for two, x{three:0.##} for three.",
                $"每多一个人，怪血多 {n:0.0} 倍：两个人 ×{two:0.##}，三个人 ×{three:0.##}。"
            ),
            EnemyHealthMode.Fixed => Lang.Pick(
                $"Enemy health is x{n:0.0} however many play, even one alone on the server.",
                $"不管几个人，怪血都是 ×{n:0.0}，服务器上只有一个人时也是。"
            ),
            _ => Lang.Pick(
                $"Each extra player adds half the solo health: x{two:0.##} for two, x{three:0.##} for three.",
                $"每多一个人，怪血多 0.5 倍：两个人 ×{two:0.##}，三个人 ×{three:0.##}。"
            )
        };
    }
}
