using System;
using System.Linq;
using System.Reflection;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity.Component;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using OpCodes = Mono.Cecil.Cil.OpCodes;

namespace SSMP.Game.Client;

/// <summary>
/// What the third act does to a creature (BlackThreadState), kept in step between the games (see
/// <see cref="BlackThreadComponent"/>): the copy of a creature that another game runs neither rolls whether it turns nor
/// turns or strikes by itself. The creature that this game runs turns when any player stays near it, strikes whom it
/// goes after by the one rule for every creature (see MoverTarget), turns at the blows of every player, and counts as on
/// screen while it is on the screen of any player in the room. The game goes by the player of its own game alone: a
/// creature turned and struck only for the player of the game that runs it, and slept off that player's screen while it
/// stood before the other.
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// The hooks on the game's code of the third act's creatures.
    /// </summary>
    private readonly System.Collections.Generic.List<IDisposable> _blackThreadHooks = [];

    /// <summary>
    /// Registers the hooks on the game's code of the third act's creatures.
    /// </summary>
    private void RegisterBlackThreadHooks() {
        var type = typeof(BlackThreadState);
        AddBlackThreadHook(TryCreateHook(type, "SetupThreaded", OnBlackThreadSetupThreaded));
        AddBlackThreadHook(TryCreateHook(type, "StartAttackTest", OnBlackThreadStartAttackTest));
        AddBlackThreadHook(TryCreateHook(type, "BecomeThreaded", OnBlackThreadBecomeThreaded));
        AddBlackThreadHook(TryCreateHook(type, "BecomeThreadedNoSing", OnBlackThreadBecomeThreadedNoSing));
        AddBlackThreadHook(TryCreateHook(type, "DoAttack", OnBlackThreadDoAttack));
        AddBlackThreadHook(TryCreateHook(type, "OnEnable", OnBlackThreadOnEnable));
        AddBlackThreadHook(TryCreateHook(type, "get_IsInActiveRange", OnBlackThreadIsInActiveRange));
        AddBlackThreadHook(TryCreateILHook(type, "DoAttack", ILBlackThreadDoAttack));
        AddBlackThreadHook(CreateRoutineHook(type, "ThreadAttackTest", il => {
            ILBlackThreadRoutine(il, nameof(BlackThreadTarget));
            ILBlackThreadDarkens(il);
        }));
        AddBlackThreadHook(CreateRoutineHook(
            type, "WaitForInRange", il => ILBlackThreadRoutine(il, nameof(BlackThreadNearest))
        ));
        HealthManagerComponent.DamageLanded += BlackThreadComponent.OnDamageLanded;
    }

    /// <summary>
    /// Disposes the hooks on the game's code of the third act's creatures.
    /// </summary>
    private void DisposeBlackThreadHooks() {
        foreach (var hook in _blackThreadHooks) {
            hook.Dispose();
        }

        _blackThreadHooks.Clear();
        HealthManagerComponent.DamageLanded -= BlackThreadComponent.OnDamageLanded;
    }

    private void AddBlackThreadHook(IDisposable? hook) {
        if (hook != null) {
            _blackThreadHooks.Add(hook);
        }
    }

    /// <summary>
    /// An IL hook on the MoveNext of the coroutine that a method of a type starts, found by the method's name, which the
    /// compiler puts in the name of the coroutine's class.
    /// </summary>
    private static ILHook? CreateRoutineHook(Type type, string methodName, ILContext.Manipulator manipulator) {
        var routine = type.GetNestedTypes(BindingFlags.NonPublic)
            .FirstOrDefault(nested => nested.Name.StartsWith($"<{methodName}>", StringComparison.Ordinal));
        if (routine == null) {
            Logger.Error($"Could not find the coroutine of {type.FullName}#{methodName}; hook was not registered");
            return null;
        }

        return TryCreateILHook(routine, "MoveNext", manipulator, InstanceNonPublicFlags | InstancePublicFlags);
    }

    /// <summary>
    /// Has a creature that another game runs set up by what that game says instead of rolling (see
    /// <see cref="BlackThreadComponent.SetUp"/>).
    /// </summary>
    private static void OnBlackThreadSetupThreaded(
        Action<BlackThreadState, bool> orig,
        BlackThreadState self,
        bool isFirst
    ) {
        if (BlackThreadComponent.TryFind(self, out var component, out var isCopy) &&
            component.SetUp(self, isFirst, isCopy)) {
            return;
        }

        orig(self, isFirst);
    }

    /// <summary>
    /// Keeps the copy of a creature from striking by itself: it strikes when the creature of the game that runs it does.
    /// </summary>
    private static void OnBlackThreadStartAttackTest(Action<BlackThreadState> orig, BlackThreadState self) {
        if (IsBlackThreadCopy(self)) {
            return;
        }

        orig(self);
    }

    /// <summary>
    /// Keeps the copy of a creature from turning by itself, as when it is hit: it turns when the creature of the game
    /// that runs it does.
    /// </summary>
    private static void OnBlackThreadBecomeThreaded(Action<BlackThreadState> orig, BlackThreadState self) {
        if (IsBlackThreadCopy(self) && !BlackThreadComponent.IsApplying) {
            return;
        }

        orig(self);
    }

    /// <inheritdoc cref="OnBlackThreadBecomeThreaded"/>
    private static void OnBlackThreadBecomeThreadedNoSing(Action<BlackThreadState> orig, BlackThreadState self) {
        if (IsBlackThreadCopy(self) && !BlackThreadComponent.IsApplying) {
            return;
        }

        orig(self);
    }

    /// <summary>
    /// Says that a creature that this game runs struck, so that the copies strike too; a copy strikes only when it is
    /// told to.
    /// </summary>
    private static void OnBlackThreadDoAttack(
        Action<BlackThreadState, BlackThreadAttack> orig,
        BlackThreadState self,
        BlackThreadAttack attack
    ) {
        if (!BlackThreadComponent.TryFind(self, out var component, out var isCopy)) {
            orig(self, attack);
            return;
        }

        if (isCopy && !BlackThreadComponent.IsApplying) {
            return;
        }

        var strikes = BlackThreadStrikes(self, attack);
        orig(self, attack);
        if (!isCopy && strikes) {
            component.OnStruck(self);
        }
    }

    /// <summary>
    /// Whether the game strikes with an attack, as it does not without the attack's object or once the creature is dead.
    /// </summary>
    private static bool BlackThreadStrikes(BlackThreadState self, BlackThreadAttack? attack) {
        return attack != null && self.spawnedAttackObjs.TryGetValue(attack, out var attackObject) &&
               attackObject != null && !(self.healthManager != null && self.healthManager.isDead);
    }

    /// <summary>
    /// Has the copy of a creature that is switched on take what the game that runs the creature said while it was off.
    /// </summary>
    private static void OnBlackThreadOnEnable(Action<BlackThreadState> orig, BlackThreadState self) {
        orig(self);
        if (BlackThreadComponent.TryFind(self, out var component, out _)) {
            component.OnSwitchedOn(self);
        }
    }

    /// <summary>
    /// Has a creature that this game runs count as on screen while it is on the screen of another player in the room.
    /// </summary>
    private static bool OnBlackThreadIsInActiveRange(Func<BlackThreadState, bool> orig, BlackThreadState self) {
        if (orig(self)) {
            return true;
        }

        if (!BlackThreadComponent.RunsHere(self)) {
            return false;
        }

        var hero = HeroController.instance;
        var position = (Vector2) self.transform.position;
        var buffer = self.activityRangeBuffer;
        foreach (var player in PlayerTargetRegistry.GetTrackedPlayers()) {
            if (player == null || hero != null && player == hero.gameObject) {
                continue;
            }

            var screen = EstimateCameraPosition(player.transform.position, 0f);
            if (Mathf.Abs(position.x - screen.x) <= CameraInfoCache.HalfWidth + buffer.x &&
                Mathf.Abs(position.y - screen.y) <= CameraInfoCache.HalfHeight + buffer.y) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a state is the copy of a creature that another game runs.
    /// </summary>
    private static bool IsBlackThreadCopy(BlackThreadState self) {
        return BlackThreadComponent.TryFind(self, out _, out var isCopy) && isCopy;
    }

    /// <summary>
    /// Has the attack of a creature face whom the creature goes after rather than the player of this game: the game
    /// turns it towards the hero as it strikes.
    /// </summary>
    private static void ILBlackThreadDoAttack(ILContext il) {
        try {
            var c = new ILCursor(il);
            var found = 0;
            while (c.TryGotoNext(
                       MoveType.After,
                       i => i.MatchCall(typeof(HeroController), "get_instance"),
                       i => i.MatchCallvirt(typeof(Component), "get_transform")
                   )) {
                c.Emit(OpCodes.Ldarg_0);
                c.Emit(OpCodes.Call, typeof(GamePatcher).GetMethod(
                    nameof(BlackThreadTarget), BindingFlags.NonPublic | BindingFlags.Static
                )!);
                found++;
            }

            if (found == 0) {
                Logger.Warn("The third act's attack no longer reads the hero where it was looked for");
            }

            // Last, the game switches the attack on, which is when what the attack is made of looks at how it sits: a
            // copy's strike puts it the way it sat in the game that runs the creature just before
            c.Index = 0;
            var switchOn = -1;
            while (c.TryGotoNext(
                       MoveType.Before,
                       i => i.MatchLdloc(out _),
                       i => i.MatchLdcI4(1),
                       i => i.MatchCallvirt(typeof(GameObject), "SetActive")
                   )) {
                switchOn = c.Index;
                c.Index++;
            }

            if (switchOn < 0) {
                Logger.Warn("The third act's attack is no longer switched on where it was looked for");
                return;
            }

            c.Index = switchOn;
            c.Emit(OpCodes.Ldarg_0);
            c.Emit(OpCodes.Call, typeof(GamePatcher).GetMethod(
                nameof(BlackThreadAttackSwitchedOn), BindingFlags.NonPublic | BindingFlags.Static
            )!);
        } catch (Exception e) {
            Logger.Error($"Could not apply ILBlackThreadDoAttack hook: {e}");
        }
    }

    /// <summary>
    /// Puts the attack of a copy's strike the way it sat in the game that runs the creature, as the attack is about to
    /// be switched on (see <see cref="BlackThreadComponent.ApplyToldStrike"/>).
    /// </summary>
    private static void BlackThreadAttackSwitchedOn(BlackThreadState state) {
        if (BlackThreadComponent.IsApplying && BlackThreadComponent.TryFind(state, out var component, out var isCopy) &&
            isCopy) {
            component.ApplyToldStrike(state);
        }
    }

    /// <summary>
    /// Has a coroutine of a creature that looks for the hero - whether one stays near enough for it to turn, or is within
    /// reach of its attack - look for a player of the room instead: each time it reads where the hero it kept is, it
    /// asks the method named.
    /// </summary>
    /// <param name="il">The coroutine's MoveNext.</param>
    /// <param name="targetMethod">The method of this class that answers whom it looks at, from the hero and the state.</param>
    private static void ILBlackThreadRoutine(ILContext il, string targetMethod) {
        try {
            var routine = il.Method.DeclaringType;
            var self = routine.Fields.FirstOrDefault(field => field.Name == "<>4__this");
            var heroes = routine.Fields
                .Where(field => field.Name.StartsWith("<hero>", StringComparison.Ordinal) &&
                                field.FieldType.FullName == typeof(Transform).FullName)
                .Select(field => field.Name)
                .ToHashSet();
            if (self == null || heroes.Count == 0) {
                Logger.Warn($"The third act's {routine.Name} no longer keeps the hero where it was looked for");
                return;
            }

            var c = new ILCursor(il);
            var found = 0;
            while (c.TryGotoNext(
                       MoveType.After,
                       i => i.MatchLdfld(out var field) && field.DeclaringType.FullName == routine.FullName &&
                            heroes.Contains(field.Name)
                   )) {
                c.Emit(OpCodes.Ldarg_0);
                c.Emit(OpCodes.Ldfld, self);
                c.Emit(OpCodes.Call, typeof(GamePatcher).GetMethod(
                    targetMethod, BindingFlags.NonPublic | BindingFlags.Static
                )!);
                found++;
            }

            if (found == 0) {
                Logger.Warn($"The third act's {routine.Name} no longer reads the hero where it was looked for");
            }
        } catch (Exception e) {
            Logger.Error($"Could not apply ILBlackThreadRoutine hook: {e}");
        }
    }

    /// <summary>
    /// Has the attack loop of a creature say when the creature begins to darken to strike, which the game does once the
    /// creature sings, just as it stops its pulse (see <see cref="BlackThreadComponent.OnDarkens"/>): the copies darken
    /// over the same while instead of going dark at once as the creature strikes.
    /// </summary>
    private static void ILBlackThreadDarkens(ILContext il) {
        try {
            var self = il.Method.DeclaringType.Fields.FirstOrDefault(field => field.Name == "<>4__this");
            var c = new ILCursor(il);
            if (self == null ||
                !c.TryGotoNext(MoveType.After, i => i.MatchCall(typeof(BlackThreadState), "StopPulseRoutine"))) {
                Logger.Warn("The third act's attack no longer darkens the creature where it was looked for");
                return;
            }

            c.Emit(OpCodes.Ldarg_0);
            c.Emit(OpCodes.Ldfld, self);
            c.Emit(OpCodes.Call, typeof(GamePatcher).GetMethod(
                nameof(BlackThreadDarkens), BindingFlags.NonPublic | BindingFlags.Static
            )!);
        } catch (Exception e) {
            Logger.Error($"Could not apply ILBlackThreadDarkens hook: {e}");
        }
    }

    /// <summary>
    /// Says that a creature that this game runs began to darken to strike.
    /// </summary>
    private static void BlackThreadDarkens(BlackThreadState state) {
        if (BlackThreadComponent.TryFind(state, out var component, out var isCopy) && !isCopy) {
            component.OnDarkens(state);
        }
    }

    /// <summary>
    /// Whom a creature that this game runs measures from as it waits for a player to stay near before it turns: the
    /// player of the room nearest to it, so that it turns for whoever stays near, as the game has it turn for its own
    /// player. A creature of another game, or of no entity, keeps the hero.
    /// </summary>
    /// <param name="hero">The hero that the game looked for.</param>
    /// <param name="state">The state of the creature.</param>
    private static Transform BlackThreadNearest(Transform hero, BlackThreadState state) {
        if (state == null || !BlackThreadComponent.RunsHere(state)) {
            return hero;
        }

        var nearest = PlayerTargetRegistry.GetNearestPlayer(state.gameObject);
        return nearest != null ? nearest.transform : hero;
    }

    /// <summary>
    /// Whom a creature that this game runs looks at as it looks for the hero, by the one rule for every creature (see
    /// MoverTarget): the player it goes after, or else the hero it looked for. A creature of another game, or of no
    /// entity, keeps the hero.
    /// </summary>
    /// <param name="hero">The hero that the game looked for.</param>
    /// <param name="state">The state of the creature.</param>
    private static Transform BlackThreadTarget(Transform hero, BlackThreadState state) {
        if (state == null || !BlackThreadComponent.RunsHere(state)) {
            return hero;
        }

        var target = MoverTarget(state.gameObject, "as its third-act state looked for a player");
        return target != null ? target.transform : hero;
    }
}
