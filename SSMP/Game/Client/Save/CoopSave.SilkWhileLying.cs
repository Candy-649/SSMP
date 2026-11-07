using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// What goes on taking the silk of a player who lies down, waiting to be pulled up.
///
/// The maggots on a maggoted player eat a silk of theirs every so often, wherever they are (HeroController.Update, its
/// maggotedSilkTracker). Maggot water takes a silk every half second from a maggoted player in it, and wears down the
/// charm that keeps its maggots off a player who has one until it lets them on, the times it wore it kept in the save
/// (MaggotRegion.TakeSilk, MaggotRegion.HeroInMaggotRegion). The water of acid and of the void burns a silk each time
/// its sizzle comes round (SilkAcidRegion, HeroSilkAcid). In a game of one player a death ends it, taking them back to
/// a bench. A player lying down is neither marked dead nor taken out of the water (see GoDown), so it all went on
/// taking their silk while they waited (USER 11-07 "中蛆形态还有个问题，死了丝还会继续掉（", "我的本意是中蛆状态死亡的时候也不
/// 掉丝", and for the water that burns silk "该改吧").
///
/// So none of it touches a player while they lie there, asked each time rather than once as they go down, since maggot
/// water starts taking silk while they lie in it too, as the charm wears out. A player pulled up has it go on as
/// before: still maggoted, they lose silk to the maggots again until they wash them off or shake them off with a
/// bind, and still in the water, it takes silk again.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The IL hooks on what takes the silk of the player.
    /// </summary>
    private readonly List<ILHook> _silkWhileLyingILHooks = [];

    /// <summary>
    /// The hook on the water of acid and of the void burning the silk of the player in it.
    /// </summary>
    private Hook? _silkAcidHook;

    /// <summary>
    /// How often the maggots on the local player were kept from eating their silk since they last went down, so that
    /// only the first time of each wait is written down; and the same for maggot water and for the water that burns
    /// silk.
    /// </summary>
    private static int _maggotsSparedLyingPlayer, _maggotWaterSparedLyingPlayer, _silkAcidSparedLyingPlayer;

    /// <summary>
    /// Keeps what takes the silk of the player off them while they lie down.
    /// </summary>
    private void RegisterSilkWhileLyingHooks() {
        AddSilkWhileLyingILHook(
            typeof(HeroController).GetMethod(nameof(HeroController.Update), InstanceFlags, null, Type.EmptyTypes, null),
            "the maggots on the player eating their silk",
            il => {
                // Where the frame hands the tick of the maggots whether they eat now
                var c = new ILCursor(il);
                c.GotoNext(
                    MoveType.Before,
                    i => i.MatchLdarg(0),
                    i => i.MatchLdflda(typeof(HeroController), nameof(HeroController.maggotedSilkTracker))
                );
                c.MoveAfterLabels();
                c.Emit(OpCodes.Ldarg_0);
                c.EmitDelegate<Func<bool, HeroController, bool>>(MaggotsEatSilk);
            }
        );
        AddSilkWhileLyingILHook(
            MaggotWaterRoutine("<TakeSilk>"),
            "maggot water taking silk",
            il => CallInstead<Action<HeroController, int>>(il, nameof(HeroController.TakeSilk), TakeMaggotWaterSilk)
        );
        AddSilkWhileLyingILHook(
            MaggotWaterRoutine("<HeroInMaggotRegion>"),
            "maggot water wearing down the charm against it",
            il => CallInstead<Action<HeroController, float>>(
                il, nameof(HeroController.AddToMaggotCharmTimer), WearMaggotCharm
            )
        );
        _silkAcidHook = CreateHook(
            typeof(HeroSilkAcid).GetMethod(nameof(HeroSilkAcid.TakeSilk), InstanceFlags),
            (Action<Action<HeroSilkAcid, HeroSilkAcid.SizzleTracker, HeroSilkAcid.SizzleConfig>, HeroSilkAcid,
                HeroSilkAcid.SizzleTracker, HeroSilkAcid.SizzleConfig>) OnSilkAcidTakeSilk
        );
    }

    /// <summary>
    /// Changes a method of the game that takes the silk of the player.
    /// </summary>
    /// <param name="method">The method, or null if it was not found.</param>
    /// <param name="what">What the method does, for the log.</param>
    /// <param name="manipulator">What changes it.</param>
    private void AddSilkWhileLyingILHook(MethodBase? method, string what, ILContext.Manipulator manipulator) {
        try {
            _silkWhileLyingILHooks.Add(new ILHook(method, manipulator));
        } catch (Exception e) {
            Logger.Error($"Could not hook {what}:\n{e}");
        }
    }

    /// <summary>
    /// The routine of maggot water of a name, which the compiler made into a type of its own; null if not found.
    /// </summary>
    /// <param name="name">The start of the name of that type, the name of the method of MaggotRegion in brackets.</param>
    private static MethodBase? MaggotWaterRoutine(string name) {
        return typeof(MaggotRegion).GetNestedTypes(BindingFlags.NonPublic)
            .FirstOrDefault(type => type.Name.StartsWith(name, StringComparison.Ordinal))
            ?.GetMethod("MoveNext", InstanceFlags);
    }

    /// <summary>
    /// Has a method of the game call a method of this class in place of the method of the player that it calls.
    /// </summary>
    /// <param name="il">The method of the game.</param>
    /// <param name="heroMethod">The method of the player.</param>
    /// <param name="instead">The method called instead, which takes the player and the same arguments.</param>
    private static void CallInstead<T>(ILContext il, string heroMethod, T instead) where T : Delegate {
        var c = new ILCursor(il);
        c.GotoNext(MoveType.Before, i => i.MatchCallvirt(typeof(HeroController), heroMethod));
        c.Remove();
        c.EmitDelegate(instead);
    }

    /// <summary>
    /// Starts the counts of what was kept off the local player afresh, as they go down.
    /// </summary>
    private static void CountSilkWhileLyingAfresh() {
        _maggotsSparedLyingPlayer = 0;
        _maggotWaterSparedLyingPlayer = 0;
        _silkAcidSparedLyingPlayer = 0;
    }

    /// <summary>
    /// Whether the maggots on the player eat their silk this frame: as the game says, unless they lie down. Told they
    /// do not, the game starts their wait over, so a player pulled up still maggoted waits the first while again.
    /// </summary>
    /// <param name="eat">Whether the game has them eat.</param>
    /// <param name="hero">The player.</param>
    private static bool MaggotsEatSilk(bool eat, HeroController hero) {
        return eat && !SparesLyingPlayer(
            hero, ref _maggotsSparedLyingPlayer,
            "The maggots on the local player eat none of their silk while they lie down"
        );
    }

    /// <summary>
    /// Takes the silk that maggot water takes every half second from the player in it, unless they lie down in it.
    /// </summary>
    /// <param name="hero">The player in the water.</param>
    /// <param name="amount">How much silk the water takes.</param>
    private static void TakeMaggotWaterSilk(HeroController hero, int amount) {
        if (!SparesLyingPlayer(
                hero, ref _maggotWaterSparedLyingPlayer,
                "The maggot water leaves the local player alone while they lie down in it"
            )) {
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
        if (!SparesLyingPlayer(
                hero, ref _maggotWaterSparedLyingPlayer,
                "The maggot water leaves the local player alone while they lie down in it"
            )) {
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
        if (SparesLyingPlayer(
                self.hc, ref _silkAcidSparedLyingPlayer,
                "The water that burns silk leaves the local player alone while they lie down in it"
            )) {
            tracker.SizzleTimeLeft = tracker.SizzleTime;
            return;
        }

        orig(self, tracker, config);
    }

    /// <summary>
    /// Whether what takes the silk of the player leaves them alone, as they lie down (see the summary of this part).
    /// </summary>
    /// <param name="hero">The player.</param>
    /// <param name="times">How often this thing left the local player alone since they last went down.</param>
    /// <param name="line">What the log says the first time.</param>
    private static bool SparesLyingPlayer(HeroController hero, ref int times, string line) {
        if (!PlayerTargetRegistry.IsPlayerDown(hero.gameObject)) {
            return false;
        }

        if (times++ == 0) {
            Logger.Info(line);
        }

        return true;
    }
}
