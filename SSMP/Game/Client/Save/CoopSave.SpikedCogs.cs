using System;
using System.Collections.Generic;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The spiked cogs that catch a player who touches them (CogMultiHitter: 290 in the game's rooms, 30 of them the cogs
/// that run back and forth along their rails). A cog holds the player for a moment, a cog on a rail stopping where it
/// is, then hurts them, and lets go once the game has put them back at the last safe place. A player whose last health
/// it took went down instead of being put back, so their cog stood where it was for the whole wait and after it (USER
/// 10-17, "死在那个轮子上了，然后在死亡视角那个轮子就会一直卡在那里"), and once pulled back up they stood in the cogs where
/// they fell ("复活掉在齿轮地上不会回到安全区域…而是直接站在齿轮里了"). A cog also caught them again where they lay: being
/// caught holds a player in the game's wounded pose for up to four seconds, which the hurt that follows ends, and with
/// that hurt kept off a player lying down (OnTakeDamage), the pose ran out, and its end showed their body standing on
/// the cog ("而且角色也没有正确消失"). Cogs now go the way of the grinders of the machines (see CoopSave.PacedMachines,
/// USER 10-06): a cog that holds the local player as they go down lets go of them as it would once they were put back,
/// they stand up at the last safe place, and no cog catches a player lying down.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The hook on something touching a cog.
    /// </summary>
    private Hook? _cogTouchHook;

    /// <summary>
    /// The cogs that caught the local player and haven't let go of them yet.
    /// </summary>
    private readonly HashSet<CogMultiHitter> _caughtCogs = [];

    /// <summary>
    /// How many times a cog touched the player since they last went down, so that only the first of each wait is
    /// written down.
    /// </summary>
    private static int _cogTouchesOfLyingPlayer;

    /// <summary>
    /// Registers the hook on something touching a cog.
    /// </summary>
    private void RegisterSpikedCogHooks() {
        _cogTouchHook = CreateHook(
            typeof(CogMultiHitter).GetMethod("OnTriggerEnter2D", InstanceFlags, null, [typeof(Collider2D)], null),
            new Action<Action<CogMultiHitter, Collider2D>, CogMultiHitter, Collider2D>(OnCogTouched)
        );
    }

    /// <summary>
    /// Keeps a cog from catching the local player while they lie down, and notes a cog that caught them. A cog catches
    /// nothing but the local player, so while they lie down, a cog is left alone whatever touched it.
    /// </summary>
    private void OnCogTouched(Action<CogMultiHitter, Collider2D> orig, CogMultiHitter self, Collider2D collision) {
        var hero = HeroController.instance;
        if (hero != null && PlayerTargetRegistry.IsPlayerDown(hero.gameObject)) {
            // Going down switches the hit box of the player off, and a cog only feels it switched on, so this also says
            // that something switched it back on
            if (collision != null && collision.GetComponentInParent<HeroController>() == hero &&
                _cogTouchesOfLyingPlayer++ == 0) {
                Logger.Info($"The cog '{PathOf(self.transform)}' touched the player lying down and did not catch them");
            }

            return;
        }

        orig(self, collision);

        if (hero != null && self.damagingHc == hero) {
            _caughtCogs.Add(self);
        }
    }

    /// <summary>
    /// Has the cogs that hold the local player let go of them once they are down, the way a cog lets go once the game
    /// has put the player back at the last safe place (CogMultiHitter.OnHeroHazardRespawn): a cog on a rail goes on,
    /// and the player stands up at that place rather than where the cog caught them.
    /// </summary>
    private void UpdateCaughtCogs() {
        if (_caughtCogs.Count == 0) {
            return;
        }

        // Gone, or let go of the player by the game: they were put back, or the cog was switched off or another one
        // caught them
        var hero = HeroController.instance;
        _caughtCogs.RemoveWhere(cog => cog == null || hero == null || cog.damagingHc != hero);
        if (_caughtCogs.Count == 0 || hero == null || !PlayerTargetRegistry.IsPlayerDown(hero.gameObject)) {
            return;
        }

        if (_rescue is { ByGrinder: false } rescue) {
            rescue.ByGrinder = true;
        }

        foreach (var cog in _caughtCogs) {
            Logger.Info($"The cog '{PathOf(cog.transform)}' held the player as they went down, and lets go of them");
            cog.OnHeroHazardRespawn();
        }

        _caughtCogs.Clear();
    }
}
