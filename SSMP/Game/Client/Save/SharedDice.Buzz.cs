using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using Logger = SSMP.Logging.Logger;
using Random = UnityEngine.Random;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The dice of the flight of a creature that every game runs by itself that buzzes about in the air, or chases
/// something with a buzz: its action rolls where it flies to next, and for how long, as it goes, at every step of the
/// game's physics, which comes as often in every game, rather than as it starts. Each game's creature flew its own way
/// with the game's own dice, up to its whole range apart. Now each visit of its state rolls from a seed that every game
/// makes alike (see <see cref="NextOwnRunSeed"/>), so that it flies the same way from where the scene host's step put
/// it (USER 11-16 "总之不看人的优先采用两边同种子掷骰子然后轻微矫正的方式").
/// </summary>
internal static partial class SharedDice {
    /// <summary>
    /// The actions that buzz about with dice as they go, each with the methods that roll: the one that each step of
    /// the game's physics and its start call, and for one, its start, which rolls before it.
    /// </summary>
    private static readonly (string Action, string Method)[] BuzzMethods = [
        ("IdleBuzz", "DoBuzz"), ("IdleBuzzV2", "DoBuzz"), ("IdleBuzzV3", "DoBuzz"), ("IdleBuzzV4", "DoBuzz"),
        ("ChaseObject", "DoBuzz"), ("ChaseObjectSpread", "DoBuzz"), ("SilkBossIdleBuzz", "DoBuzz"),
        ("WispBuzz", "OnEnter"), ("WispBuzz", "DoBuzz")
    ];

    /// <summary>
    /// The dice of each buzzing action of a creature that every game runs by itself, for the visit of its state that
    /// they are for, by when the visit began, as they are after its rolls so far.
    /// </summary>
    private static readonly Dictionary<FsmStateAction, (float Visit, Random.State Dice)> BuzzDice = new();

    /// <summary>
    /// The hooks on the buzzing actions (see <see cref="BuzzMethods"/>).
    /// </summary>
    private static IEnumerable<(MethodInfo Method, Delegate Detour)> GetBuzzHooks() {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var (name, methodName) in BuzzMethods) {
            var type = GamePatcher.GetFsmActionTypeByName(name);
            var method = type?.GetMethod(methodName, flags, null, Type.EmptyTypes, null);
            if (type == null || method == null || method.DeclaringType != type) {
                Logger.Debug($"No {name}.{methodName} that buzzes with dice, so its dice are not shared");
                continue;
            }

            yield return (method, new Action<Action<FsmStateAction>, FsmStateAction>(OnBuzz));
        }
    }

    /// <summary>
    /// Hook for a roll of a buzzing action, which an action of a creature that every game runs by itself, or of a part
    /// of it, makes with the dice of the visit of its state (see <see cref="BuzzDice"/>).
    /// </summary>
    private static void OnBuzz(Action<FsmStateAction> orig, FsmStateAction self) {
        // The start of the wisp's buzz rolls once and then buzzes, with the dice of the visit already
        if (Entity.OwnCreatures.Count == 0 || self.State is not { } state || _buzzing == self) {
            orig(self);
            return;
        }

        var visit = state.RealStartTime;
        if (!BuzzDice.TryGetValue(self, out var dice) || !dice.Visit.Equals(visit)) {
            if (NextOwnRunSeed(self) is not { } seed) {
                orig(self);
                return;
            }

            var game = Random.state;
            Random.InitState(seed);
            dice = (visit, Random.state);
            Random.state = game;
        }

        var own = Random.state;
        Random.state = dice.Dice;
        _buzzing = self;
        try {
            orig(self);
        } finally {
            _buzzing = null;
            BuzzDice[self] = (visit, Random.state);
            Random.state = own;
        }
    }

    /// <summary>
    /// The buzzing action that rolls with the dice of its visit now, or null (see <see cref="OnBuzz"/>).
    /// </summary>
    private static FsmStateAction? _buzzing;
}
