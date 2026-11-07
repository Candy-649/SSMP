using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// What water does to a player who lies down in it, waiting to be pulled up.
///
/// Some water takes the silk of the player in it. Maggot water takes a silk every half second from a maggoted player,
/// and wears down the charm that keeps its maggots off a player who has one until it lets them on; the times it wore it
/// are kept in the save (MaggotRegion.TakeSilk, MaggotRegion.HeroInMaggotRegion). The water of acid and of the void
/// burns a silk each time its sizzle comes round (SilkAcidRegion, HeroSilkAcid). A death in the game of one player
/// takes them out of the water: it puts them on a layer the water does not see, and then in another room. A player
/// lying down stays on their layer (see GoDown), so the water went on taking all of their silk while they waited
/// (USER 11-07 "中蛆形态还有个问题，死了丝还会继续掉（", and for the water that burns silk "该改吧").
///
/// So the water leaves a player alone while they lie in it. It is asked each time rather than once as the player goes
/// down, since maggot water starts taking silk while they lie there too, as the charm wears out. A player pulled up
/// still in the water has it go on as before - their silk taken, their charm worn - and stays maggoted if they were, as
/// they do through a death a member pulls them up from.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The hooks on the routines in which maggot water acts on the player in it.
    /// </summary>
    private readonly List<ILHook> _maggotWaterHooks = [];

    /// <summary>
    /// The hook on the water of acid and of the void burning the silk of the player in it.
    /// </summary>
    private Hook? _silkAcidHook;

    /// <summary>
    /// How often maggot water left the local player alone since they last went down, so that only the first time of
    /// each wait is written down.
    /// </summary>
    private static int _maggotWaterSparedLyingPlayer;

    /// <summary>
    /// How often the water of acid or of the void left the local player alone since they last went down, so that only
    /// the first time of each wait is written down.
    /// </summary>
    private static int _silkAcidSparedLyingPlayer;

    /// <summary>
    /// Has the water that takes silk leave a player lying in it alone.
    /// </summary>
    private void RegisterLyingInWaterHooks() {
        HookMaggotWaterRoutine<Action<HeroController, int>>(
            "<TakeSilk>", nameof(HeroController.TakeSilk), TakeMaggotWaterSilk
        );
        HookMaggotWaterRoutine<Action<HeroController, float>>(
            "<HeroInMaggotRegion>", nameof(HeroController.AddToMaggotCharmTimer), WearMaggotCharm
        );
        _silkAcidHook = CreateHook(
            typeof(HeroSilkAcid).GetMethod(nameof(HeroSilkAcid.TakeSilk), InstanceFlags),
            (Action<Action<HeroSilkAcid, HeroSilkAcid.SizzleTracker, HeroSilkAcid.SizzleConfig>, HeroSilkAcid,
                HeroSilkAcid.SizzleTracker, HeroSilkAcid.SizzleConfig>) OnSilkAcidTakeSilk
        );
    }

    /// <summary>
    /// Has a routine of maggot water call a method of this class in place of the method of the player that it calls.
    /// </summary>
    /// <param name="routine">The start of the name of the type that the compiler made of the routine, which is a
    /// method of MaggotRegion.</param>
    /// <param name="heroMethod">The method of the player.</param>
    /// <param name="instead">The method called instead, which takes the player and the same arguments.</param>
    private void HookMaggotWaterRoutine<T>(string routine, string heroMethod, T instead) where T : Delegate {
        var moveNext = typeof(MaggotRegion).GetNestedTypes(BindingFlags.NonPublic)
            .FirstOrDefault(type => type.Name.StartsWith(routine, StringComparison.Ordinal))
            ?.GetMethod("MoveNext", InstanceFlags);
        try {
            _maggotWaterHooks.Add(new ILHook(moveNext, il => {
                var c = new ILCursor(il);
                c.GotoNext(MoveType.Before, i => i.MatchCallvirt(typeof(HeroController), heroMethod));
                c.Remove();
                c.EmitDelegate(instead);
            }));
        } catch (Exception e) {
            Logger.Error($"Could not hook the routine {routine} of maggot water:\n{e}");
        }
    }

    /// <summary>
    /// Takes the silk that maggot water takes every half second from the player in it, unless they lie down in it.
    /// </summary>
    /// <param name="hero">The player in the water.</param>
    /// <param name="amount">How much silk the water takes.</param>
    private static void TakeMaggotWaterSilk(HeroController hero, int amount) {
        if (!SparesLyingPlayer(hero, ref _maggotWaterSparedLyingPlayer, "maggot water")) {
            hero.TakeSilk(amount);
        }
    }

    /// <summary>
    /// Wears down, by the time of a frame in maggot water, the charm that keeps its maggots off the player, unless they
    /// lie down in it.
    /// </summary>
    /// <param name="hero">The player in the water.</param>
    /// <param name="time">The time of the frame.</param>
    private static void WearMaggotCharm(HeroController hero, float time) {
        if (!SparesLyingPlayer(hero, ref _maggotWaterSparedLyingPlayer, "maggot water")) {
            hero.AddToMaggotCharmTimer(time);
        }
    }

    /// <summary>
    /// Burns a silk of the player in the water of acid or of the void as its sizzle comes round, unless they lie down
    /// in it: the sizzle then starts over with nothing burned, so nothing shows a silk burned either, and the water's
    /// own count of what it burned stays as it was.
    /// </summary>
    private static void OnSilkAcidTakeSilk(
        Action<HeroSilkAcid, HeroSilkAcid.SizzleTracker, HeroSilkAcid.SizzleConfig> orig,
        HeroSilkAcid self,
        HeroSilkAcid.SizzleTracker tracker,
        HeroSilkAcid.SizzleConfig config
    ) {
        if (SparesLyingPlayer(self.hc, ref _silkAcidSparedLyingPlayer, "water that burns silk")) {
            tracker.SizzleTimeLeft = tracker.SizzleTime;
            return;
        }

        orig(self, tracker, config);
    }

    /// <summary>
    /// Whether water leaves the player in it alone, as they lie down in it (see the summary of this part).
    /// </summary>
    /// <param name="hero">The player in the water.</param>
    /// <param name="times">How often this water left the local player alone since they last went down.</param>
    /// <param name="water">What water it is, for the log.</param>
    private static bool SparesLyingPlayer(HeroController hero, ref int times, string water) {
        if (!PlayerTargetRegistry.IsPlayerDown(hero.gameObject)) {
            return false;
        }

        if (times++ == 0) {
            Logger.Info($"The {water} leaves the local player alone while they lie down in it");
        }

        return true;
    }
}
