using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.Cil;
using Mono.Cecil.Cil;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity.Action;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Contains multiplayer-aware aggression, retargeting, and walking patches for enemy AI.
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// Minimum interval, in seconds, between periodic refreshes of cached vanilla enemy target fields.
    /// </summary>
    private const float EnemyRetargetIntervalSeconds = 0.2f;

    /// <summary>
    /// How much nearer the other player has to be, as a share of the distance to the one an enemy is already after,
    /// before it turns to them. A fifth nearer.
    ///
    /// A share rather than a fixed amount, because a fixed one was added to the **square** of the distance and so
    /// meant less and less the further away the enemy was. Four units of it is a real margin at two units' distance
    /// and half a percent at twenty, which is why a flyer far from both players changed its mind six hundred and
    /// sixty times in one session: the two distances crossed each other constantly and there was nothing to stop it
    /// following them. A share is the same margin at every distance.
    /// </summary>
    private const float TargetSwitchNearerShare = 0.2f;

    /// <summary>
    /// <see cref="TargetSwitchNearerShare"/> as it is compared: distances are kept squared to avoid a square root on
    /// every check, so the share has to be squared with them.
    /// </summary>
    private const float TargetSwitchNearerShareSquared =
        (1f - TargetSwitchNearerShare) * (1f - TargetSwitchNearerShare);

    /// <summary>
    /// Approved multiplayer target per enemy owner instance ID.
    /// </summary>
    private static readonly Dictionary<int, GameObject> EnemyApprovedTargets = new();

    /// <summary>
    /// How long an enemy stays with the player it has just turned to, in seconds, before the nearer of the two can
    /// take it away again.
    ///
    /// Without this an enemy re-decides every time it is asked, and with two players in a room it is asked
    /// constantly: one fight of a few enemies changed its mind twenty-one times, one enemy eleven of them. What that
    /// looks like is an enemy that cannot commit to either player and does nothing well to either of them.
    ///
    /// Three seconds because that is roughly what the games that solved this first settled on - Doom holds a target
    /// for a hundred tics, which is just under three - and because it is long enough to see a whole attack out, which
    /// is the thing the changing of minds was interrupting.
    /// </summary>
    private const float TargetLockTime = 3f;

    /// <summary>
    /// When each enemy's hold on the player it turned to runs out, by the same key as
    /// <see cref="EnemyApprovedTargets"/>.
    /// </summary>
    private static readonly Dictionary<int, float> EnemyTargetLocks = new();

    /// <summary>
    /// Cached resolved enemy target owner per GameObject instance ID to avoid deep transform parent lookups.
    /// </summary>
    private static readonly Dictionary<int, GameObject> TargetOwnerCache = new();

    /// <summary>
    /// For each target owner that is no enemy itself, by instance ID, the enemy that last set it off by sending it an
    /// event (see <see cref="NoteWhatAnEnemySetsOff"/>).
    /// </summary>
    private static readonly Dictionary<int, GameObject> SetOffByEnemy = new();

    /// <summary>
    /// For each target owner that is no enemy itself, by instance ID, the player whom the creature that set it off went
    /// after in the game that runs the creature, as that game told (see <see cref="SetOffFor"/>).
    /// </summary>
    private static readonly Dictionary<int, GameObject> SetOffForPlayer = new();

    /// <summary>
    /// Searches active assemblies to find a PlayMaker FSM action type matching the given class name.
    /// </summary>
    /// <param name="name">The name of the action class.</param>
    /// <returns>The resolved action <see cref="Type"/>, or null if not found.</returns>
    internal static Type? GetFsmActionTypeByName(string name) {
        var actionAssembly = typeof(GetHero).Assembly;
        var type = actionAssembly.GetType($"HutongGames.PlayMaker.Actions.{name}");
        if (type != null) return type;

        var gameAssembly = typeof(Walker).Assembly;
        type = gameAssembly.GetType($"HutongGames.PlayMaker.Actions.{name}");
        if (type != null) return type;

        type = gameAssembly.GetType(name);
        if (type != null) return type;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            type = assembly.GetType(name) ?? assembly.GetType($"HutongGames.PlayMaker.Actions.{name}");
            if (type != null) return type;
        }

        return null;
    }

    /// <summary>
    /// Cached reflected <see cref="FsmGameObject"/> fields for target-consuming FSM action types.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> TargetedActionGameObjectFieldCache = new();

    /// <summary>
    /// Cached reflected <see cref="FsmOwnerDefault"/> fields for target-consuming FSM action types.
    ///
    /// Separate because these are not a variable holding an object. They are the pair PlayMaker uses for "my owner,
    /// or that one over there", and only the second half of that may be pointed anywhere.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> TargetedActionOwnerDefaultFieldCache = new();

    /// <summary>
    /// Reflected private field storing the cached hero transform or controller used by <see cref="Walker"/>.
    /// </summary>
    private static readonly FieldInfo? WalkerHeroField =
        typeof(Walker).GetField("hero", InstanceNonPublicFlags);

    /// <summary>
    /// Reflected private field storing the cached hero transform used by <see cref="WalkerV2"/>.
    /// </summary>
    private static readonly FieldInfo? WalkerV2HeroField =
        typeof(WalkerV2).GetField("hero", InstanceNonPublicFlags);

    /// <summary>
    /// Reflected private field storing the cached hero transform or object used by <see cref="ScuttlerControl"/>.
    /// </summary>
    private static readonly FieldInfo? ScuttlerControlHeroField =
        typeof(ScuttlerControl).GetField("hero", InstanceNonPublicFlags);

    private static readonly Func<Walker, HeroController?>? GetWalkerHero =
        CompileGetter<HeroController?>(WalkerHeroField);

    /// <summary>
    /// Compiles a dynamic lambda delegate for retrieving the value of a private field on a <see cref="Walker"/> instance.
    /// </summary>
    /// <typeparam name="TField">The expected return type of the field.</typeparam>
    /// <param name="field">The reflected <see cref="FieldInfo"/> to read.</param>
    /// <returns>A compiled getter delegate, or null if compile fails.</returns>
    private static Func<Walker, TField>? CompileGetter<TField>(FieldInfo? field) {
        if (field == null) return null;
        try {
            var param = Expression.Parameter(typeof(Walker), "self");
            var fieldExp = Expression.Field(param, field);
            return Expression.Lambda<Func<Walker, TField>>(fieldExp, param).Compile();
        } catch (Exception e) {
            Logger.Error($"Failed to compile getter for field {field.Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Hook for replacing <see cref="Walker"/> walking updates with multiplayer-aware target handling.
    /// </summary>
    private ILHook? _walkerUpdateWalkingHook;

    /// <summary>
    /// Hook for replacing <see cref="Walker"/> activation-condition updates with multiplayer-aware target handling.
    /// </summary>
    private ILHook? _walkerUpdateWaitingForConditionsHook;

    /// <summary>
    /// Hooks registered on target-consuming FSM action <c>OnEnter</c> methods.
    /// </summary>
    private readonly List<Hook> _targetedFsmActionEnterHooks = [];

    /// <summary>
    /// Next unscaled time at which cached vanilla enemy target fields should be refreshed.
    /// </summary>
    private float _nextEnemyRetargetTime;

    /// <summary>
    /// Gets the currently approved multiplayer target for an enemy.
    /// </summary>
    /// <param name="requester">The enemy object or one of its child/component objects.</param>
    /// <returns>The approved tracked player target, or <see langword="null"/> when no valid target is approved.</returns>
    private static GameObject? GetApprovedEnemyTarget(GameObject? requester) {
        if (requester == null) {
            return null;
        }

        var owner = GetEnemyTargetOwner(requester);
        return owner != null && EnemyApprovedTargets.TryGetValue(owner.GetInstanceID(), out var target) &&
               target != null && target.activeInHierarchy
            ? target
            : null;
    }

    /// <summary>
    /// Gets whoever the enemy that set something of the room off goes after (see <see cref="NoteWhatAnEnemySetsOff"/>),
    /// or this game's own player while that enemy goes after no one, as the thing would by itself: left at the player
    /// it had, it went on aiming at a partner who had left or gone down.
    /// </summary>
    /// <param name="requester">The thing that was set off, or one of its child/component objects.</param>
    /// <returns>The target, or <see langword="null"/> when no enemy set it off.</returns>
    private static GameObject? GetTargetOfWhoSetItOff(GameObject requester) {
        var owner = GetEnemyTargetOwner(requester);
        if (owner == null) {
            return null;
        }

        // Set off here for the player that the game running the creature said it went after
        if (SetOffForPlayer.TryGetValue(owner.GetInstanceID(), out var player) && player != null &&
            player.activeInHierarchy) {
            return player;
        }

        if (!SetOffByEnemy.TryGetValue(owner.GetInstanceID(), out var enemy)) {
            return null;
        }

        var hero = HeroController.instance;
        var heroObject = hero == null ? null : hero.gameObject;
        return enemy == null ? heroObject : GetFsmActionTarget(enemy) ?? heroObject;
    }

    /// <summary>
    /// Stores the approved multiplayer target for an enemy.
    /// </summary>
    /// <param name="requester">The enemy object or one of its child/component objects.</param>
    /// <param name="target">The tracked player target approved by acquisition.</param>
    private static void ApproveEnemyTarget(GameObject requester, GameObject target) {
        var owner = GetEnemyTargetOwner(requester);
        if (owner == null || target == null) {
            return;
        }

        var id = owner.GetInstanceID();
        var changed = !EnemyApprovedTargets.TryGetValue(id, out var last) || last != target;

        EnemyApprovedTargets[id] = target;

        if (changed) {
            // Only on a change. Set every time it is approved, and an enemy that is asked about the same player five
            // times a second would hold a lock that never runs out and could never turn to the other one at all.
            EnemyTargetLocks[id] = Time.unscaledTime + TargetLockTime;
            NoteTurn(id, last);

            SayWhoAnEnemyGoesAfter(owner, target);
        }
    }

    /// <summary>
    /// Has something that is no enemy go after a player wherever it looks at one, as an enemy that has chosen them
    /// does: something a game sends in to throw at the player, which would otherwise only ever aim at the player of the
    /// game that runs it.
    /// </summary>
    /// <param name="thing">The thing, or one of its parts.</param>
    /// <param name="player">The player.</param>
    /// <param name="kept">Whether a rule of its own sets whom it goes after, so that it never turns to another player
    /// by itself as it starts a move (see TurnAtMoveStart).</param>
    public static void SetTargetOf(GameObject thing, GameObject player, bool kept = false) {
        ApproveEnemyTarget(thing, player);
        if (kept && GetEnemyTargetOwner(thing) is { } owner && owner != null) {
            KeptTargets.Add(owner.GetInstanceID());
        }
    }

    /// <summary>
    /// Whom a creature or a thing went after, until when it held to them and how it had turned (see TargetTurns), to
    /// give back with <see cref="GiveBackTarget"/>.
    /// </summary>
    internal sealed class TargetHold {
        /// <summary>
        /// Whom it went after, or null for no one in particular.
        /// </summary>
        public GameObject? Target;

        /// <summary>
        /// Until when it held to them, if it did.
        /// </summary>
        public float? LockedUntil;

        /// <summary>
        /// A copy of how it had turned, if it picks its moves.
        /// </summary>
        public TargetTurns? Turns;
    }

    /// <summary>
    /// Whom a creature or a thing goes after now, until when it holds to them if it does, and how it has turned, to
    /// give back with <see cref="GiveBackTarget"/>; null for nothing that goes after anyone.
    /// </summary>
    /// <param name="thing">The creature or thing, or one of its parts.</param>
    public static TargetHold? TargetHoldOf(GameObject thing) {
        var owner = GetEnemyTargetOwner(thing);
        if (owner == null) {
            return null;
        }

        var id = owner.GetInstanceID();
        var target = EnemyApprovedTargets.TryGetValue(id, out var approved) && approved != null ? approved : null;
        return new TargetHold {
            Target = target,
            LockedUntil = target != null && EnemyTargetLocks.TryGetValue(id, out var lockedUntil) ? lockedUntil : null,
            Turns = Turns.TryGetValue(id, out var turns) ? turns.Copy() : null
        };
    }

    /// <summary>
    /// Gives a creature or a thing back whom it went after before something turned it to another player for nothing
    /// (see <see cref="TargetHoldOf"/>), and puts back how it had turned, so that a turn that came to nothing does not
    /// hold it to that player or start its chance over (see TurnAtMoveStart). One that went after no one in particular
    /// goes after this game's player, as it did: what its actions aim at was pointed at the other player meanwhile,
    /// and only someone it goes after points them back.
    /// </summary>
    /// <param name="thing">The creature or thing, or one of its parts.</param>
    /// <param name="hold">Whom it went after before, from <see cref="TargetHoldOf"/>.</param>
    public static void GiveBackTarget(GameObject thing, TargetHold? hold) {
        var owner = GetEnemyTargetOwner(thing);
        if (owner == null) {
            return;
        }

        var id = owner.GetInstanceID();
        if (hold?.Turns is { } before && Turns.TryGetValue(id, out var turns)) {
            turns.PutBack(before);
        }

        var hero = HeroController.SilentInstance;
        var target = hold?.Target is { } held && held != null ? held : hero != null ? hero.gameObject : null;
        if (target == null) {
            EnemyApprovedTargets.Remove(id);
            EnemyTargetLocks.Remove(id);
            return;
        }

        EnemyApprovedTargets[id] = target;
        if (hold is { LockedUntil: { } lockedUntil }) {
            EnemyTargetLocks[id] = lockedUntil;
        } else {
            EnemyTargetLocks.Remove(id);
        }
    }

    /// <summary>
    /// Has something of the room that the scene host's creature set off go after the player the creature went after
    /// in the host's game, wherever it looks at a player, as it does in that game where the creature set it off itself
    /// (<see cref="NoteWhatAnEnemySetsOff"/>): the copy of the creature here only passes on what the host's did, which
    /// the creature's own actions never see (see ToldParts).
    /// </summary>
    /// <param name="thing">The thing, or one of its parts.</param>
    /// <param name="player">The player.</param>
    public static void SetOffFor(GameObject thing, GameObject player) {
        var owner = GetEnemyTargetOwner(thing);
        if (owner != null) {
            SetOffForPlayer[owner.GetInstanceID()] = player;
        }
    }

    /// <summary>
    /// The parts of a thing that a creature threw that are asked whom they go after, by instance ID: every part of it
    /// with a state machine of its own, since each is asked about on its own (see <see cref="AimThrownAt"/>).
    /// </summary>
    /// <param name="thing">The thing.</param>
    public static int[] ThrownTargetOwners(GameObject thing) {
        var owners = new List<int>();
        foreach (var fsm in thing.GetComponentsInChildren<PlayMakerFSM>(true)) {
            var owner = GetEnemyTargetOwner(fsm.gameObject);
            if (owner != null && !owners.Contains(owner.GetInstanceID())) {
                owners.Add(owner.GetInstanceID());
            }
        }

        return owners.ToArray();
    }

    /// <summary>
    /// Has a thing that a creature threw go after a player wherever it looks at one (see SetTargetOf), or takes that
    /// away when the player is null, by its parts that are asked (see <see cref="ThrownTargetOwners"/>). Without a word
    /// in the log, since things are thrown many at a time.
    /// </summary>
    /// <param name="owners">The parts of the thing that are asked whom they go after.</param>
    /// <param name="player">The player, or null.</param>
    public static void AimThrownAt(int[] owners, GameObject? player) {
        foreach (var id in owners) {
            if (player == null) {
                EnemyApprovedTargets.Remove(id);
                EnemyTargetLocks.Remove(id);
            } else {
                EnemyApprovedTargets[id] = player;
            }
        }
    }

    /// <summary>
    /// The player whom a creature's actions that aim at one are pointed at (see GetFsmActionTarget), or null when it
    /// goes after no one in particular.
    /// </summary>
    /// <param name="creature">The creature.</param>
    public static GameObject? TargetOf(GameObject creature) {
        return GetFsmActionTarget(creature);
    }

    /// <summary>
    /// What each field of an FSM action that is pointed at a player held before, and the object of its own that it was
    /// pointed with, by action and field: what it held is the FSM's own variable, or the object put in the field in the
    /// editor. Each time the action is pointed, the field is decided again from what it held (see
    /// <see cref="RetargetFsmActionGameObjectFields"/>): pointed at the player while that holds a player or nothing,
    /// and given back while it holds anything else. Pointed for good the first time, the field of a variable that
    /// holds the player at one time and something else at another - the creature's opponent, a place it flies to -
    /// went on aiming at the player after.
    /// </summary>
    private static readonly
        ConditionalWeakTable<FsmStateAction, Dictionary<FieldInfo, (FsmGameObject Held, FsmGameObject Pointed)>>
        PointedAims = new();

    /// <summary>
    /// How many times an action of a state machine has had the object it aims at written into it.
    ///
    /// Said in the same line as the target, because the two failures look identical from outside the game and this is
    /// the only thing that tells them apart: a target that is never chosen, and a target that is chosen and then
    /// never reaches the thing that would act on it.
    /// </summary>
    private static int _targetsWrittenIntoActions;

    /// <summary>
    /// Writes down which of the two players an enemy has just turned its attention to.
    ///
    /// Here because the whole of this - choosing between two players, holding the choice, handing it to everything
    /// that asks an enemy who it is after - cannot be seen from the outside at all. An enemy going for whoever is
    /// nearest and an enemy that never chose anything and is simply looking at the player whose game it is look
    /// exactly the same on the screen of that player. This says which it was.
    /// </summary>
    /// <param name="owner">The enemy that chose.</param>
    /// <param name="target">What it chose.</param>
    private static void SayWhoAnEnemyGoesAfter(GameObject owner, GameObject target) {
        try {
            var hero = HeroController.instance;
            var isLocal = hero != null && target == hero.gameObject;

            Logger.Info(
                $"{owner.name} now goes after {(isLocal ? "the local player" : $"the partner ({target.name})")}, " +
                $"and {_targetsWrittenIntoActions} target(s) have been written into actions so far"
            );
        } catch (Exception e) {
            Logger.Warn($"Could not say who an enemy goes after: {e.Message}");
        }
    }

    /// <summary>
    /// Registers aggression-specific hooks and periodic target refresh logic.
    /// </summary>
    private void RegisterAggressionHooks() {
        _walkerUpdateWalkingHook = new ILHook(
            typeof(Walker).GetMethod("UpdateWalking", InstanceNonPublicFlags)!,
            ILWalkerUpdateWalking
        );

        _walkerUpdateWaitingForConditionsHook = new ILHook(
            typeof(Walker).GetMethod("UpdateWaitingForConditions", InstanceNonPublicFlags)!,
            ILWalkerUpdateWaitingForConditions
        );

        RegisterTargetedFsmActionEnterHooks();
        RegisterMoveStartHook();

        MonoBehaviourUtil.Instance.OnUpdateEvent += OnUpdateRetargetEnemyTransforms;
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    /// <summary>
    /// Disposes aggression-specific hooks and periodic target refresh logic.
    /// </summary>
    private void DisposeAggressionHooks() {
        _walkerUpdateWalkingHook?.Dispose();
        _walkerUpdateWalkingHook = null;

        _walkerUpdateWaitingForConditionsHook?.Dispose();
        _walkerUpdateWaitingForConditionsHook = null;

        DisposeTargetedFsmActionEnterHooks();
        DisposeMoveStartHook();

        if (MonoBehaviourUtil.Instance != null) {
            MonoBehaviourUtil.Instance.OnUpdateEvent -= OnUpdateRetargetEnemyTransforms;
        }

        UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        ClearTargetCaches();
    }

    /// <summary>
    /// Clears the active target caches when the current game scene changes.
    /// </summary>
    /// <param name="from">The previous scene.</param>
    /// <param name="to">The newly loaded active scene.</param>
    private static void OnActiveSceneChanged(
        UnityEngine.SceneManagement.Scene from,
        UnityEngine.SceneManagement.Scene to
    ) {
        ClearTargetCaches();
    }

    /// <summary>
    /// Clears the approved target maps and the enemy target owner resolution caches.
    /// </summary>
    private static void ClearTargetCaches() {
        EnemyApprovedTargets.Clear();
        EnemyTargetLocks.Clear();
        TargetOwnerCache.Clear();
        SetOffByEnemy.Clear();
        SetOffForPlayer.Clear();
        ClearNeedolinTargets();
        ClearMoveStartTargets();
    }

    /// <summary>
    /// Takes a player away from every enemy that had settled on them.
    ///
    /// Leaving them off the list of people worth going after is not enough on its own: an enemy that has already
    /// settled on someone holds on to them for a few seconds whatever the list says, which is deliberate - it is
    /// what stops two enemies swapping between two players every frame - and here it would mean a few more seconds
    /// of hitting a player who is already down.
    /// </summary>
    /// <param name="player">The player to take away.</param>
    public static void ForgetPlayerAsTarget(GameObject? player) {
        if (player == null) {
            return;
        }

        List<int>? owners = null;
        foreach (var pair in EnemyApprovedTargets) {
            if (pair.Value == player) {
                owners ??= [];
                owners.Add(pair.Key);
            }
        }

        if (owners == null) {
            return;
        }

        foreach (var owner in owners) {
            EnemyApprovedTargets.Remove(owner);
            EnemyTargetLocks.Remove(owner);
        }

        var turned = TurnEnemiesAwayFrom(player, owners);
        Logger.Info(
            $"{owners.Count} enemy(s) were going after a player who is now down, and have been let go; " +
            $"{turned} aim(s) were turned to who still stands"
        );
    }

    /// <summary>
    /// Points what the given enemies aim at the given player, who went down, at whoever still stands nearest to each of
    /// them instead, without settling the enemy on anyone. Letting an enemy go of a player clears whom it settled on,
    /// but its actions stay pointed where they were until it settles on someone else, and only its own look around for
    /// players does that: an enemy with nobody else in its reach or in its sight went on striking the cocoon. Pointed at
    /// the player who still stands, it does what it does while it has settled on no one - what the game itself does
    /// with its one player - and its own look around decides from there.
    /// </summary>
    /// <param name="player">The player who went down.</param>
    /// <param name="owners">The instance IDs of the enemies that were going after them.</param>
    /// <returns>How many aims were turned.</returns>
    private static int TurnEnemiesAwayFrom(GameObject player, List<int> owners) {
        if (_entityManagerInstance == null) {
            return 0;
        }

        var turned = 0;
        foreach (var entity in _entityManagerInstance.ActiveEntities) {
            if (entity == null) {
                continue;
            }

            foreach (var fsm in entity.HostFsms) {
                turned += TurnAwayFrom(fsm, player, owners);
            }

            if (entity.ClientFsms != null) {
                foreach (var fsm in entity.ClientFsms) {
                    turned += TurnAwayFrom(fsm, player, owners);
                }
            }

            foreach (var root in new[] { entity.Object.Host, entity.Object.Client }) {
                turned += TurnCachedTargetAwayFrom<WalkerV2>(root, WalkerV2HeroField, player, owners);
                turned += TurnCachedTargetAwayFrom<ScuttlerControl>(root, ScuttlerControlHeroField, player, owners);
            }
        }

        return turned;
    }

    /// <summary>
    /// Points what the actions of one FSM of an enemy that was going after a player who went down aim at that player at
    /// whoever still stands nearest to the enemy (see <see cref="TurnEnemiesAwayFrom"/>), in every state of it: the
    /// actions of a state are pointed as the state is gone into, and were pointed at the player while they were the one
    /// the enemy had settled on, so a state it goes into later would still have aimed at them.
    /// </summary>
    /// <returns>How many aims were turned.</returns>
    private static int TurnAwayFrom(PlayMakerFSM? fsm, GameObject player, List<int> owners) {
        if (fsm == null || fsm.FsmStates == null) {
            return 0;
        }

        var owner = GetEnemyTargetOwner(fsm.gameObject);
        if (owner == null || !owners.Contains(owner.GetInstanceID())) {
            return 0;
        }

        var standing = PlayerTargetRegistry.GetNearestPlayer(owner);
        if (standing == null) {
            return 0;
        }

        var turned = 0;
        foreach (var state in fsm.FsmStates) {
            if (state?.Actions == null) {
                continue;
            }

            foreach (var action in state.Actions) {
                if (action == null || !ActionRegistry.IsRetargeted(action.GetType().Name)) {
                    continue;
                }

                foreach (var field in GetFsmGameObjectFields(action.GetType())) {
                    if (field.GetValue(action) is FsmGameObject aim && aim.Value == player &&
                        CanRetargetFsmGameObjectField(action, field, HeldAim(action, field, aim))) {
                        PointAim(action, field, null, aim, standing);
                        turned++;
                    }
                }

                foreach (var field in GetFsmOwnerDefaultFields(action.GetType())) {
                    if (field.GetValue(action) is FsmOwnerDefault {
                            OwnerOption: OwnerDefaultOption.SpecifyGameObject, GameObject: { } aim
                        } pair && aim.Value == player &&
                        CanRetargetFsmGameObjectField(action, field, HeldAim(action, field, aim))) {
                        PointAim(action, field, pair, aim, standing);
                        turned++;
                    }
                }
            }
        }

        return turned;
    }

    /// <summary>
    /// Points the player that a creature's own component of the given type keeps at whoever still stands nearest to it,
    /// when it is the player who went down and the creature was going after them (see
    /// <see cref="TurnEnemiesAwayFrom"/>). The component is pointed at the player that the creature settled on every
    /// little while, but only while it has settled on someone.
    /// </summary>
    /// <returns>1 when it was turned; otherwise 0.</returns>
    private static int TurnCachedTargetAwayFrom<TComponent>(
        GameObject? root,
        FieldInfo? field,
        GameObject player,
        List<int> owners
    ) where TComponent : Behaviour {
        if (root == null || field == null) {
            return 0;
        }

        var component = root.GetComponentInChildren<TComponent>();
        if (component == null) {
            return 0;
        }

        var aimedAt = field.GetValue(component) switch {
            Transform transform when transform != null => transform.gameObject,
            GameObject gameObject when gameObject != null => gameObject,
            _ => null
        };
        if (aimedAt != player) {
            return 0;
        }

        var owner = GetEnemyTargetOwner(component.gameObject);
        if (owner == null || !owners.Contains(owner.GetInstanceID())) {
            return 0;
        }

        var standing = PlayerTargetRegistry.GetNearestPlayer(owner);
        if (standing == null) {
            return 0;
        }

        if (field.FieldType == typeof(Transform)) {
            field.SetValue(component, standing.transform);
        } else if (field.FieldType == typeof(GameObject)) {
            field.SetValue(component, standing);
        } else {
            return 0;
        }

        return 1;
    }

    /// <summary>
    /// Registers RuntimeDetour hooks for target-consuming FSM action OnEnter methods.
    /// </summary>
    private void RegisterTargetedFsmActionEnterHooks() {
        DisposeTargetedFsmActionEnterHooks();

        foreach (var actionName in ActionRegistry.TargetedFsmActionTypes.Concat(
                     ActionRegistry.TargetedWherePlayerActionTypes
                 )) {
            var actionType = GetFsmActionTypeByName(actionName);
            if (actionType == null) {
                continue;
            }

            var onEnterMethod = actionType.GetMethod("OnEnter", InstancePublicFlags | InstanceNonPublicFlags);
            if (onEnterMethod == null || onEnterMethod.DeclaringType != actionType) {
                continue;
            }

            try {
                _targetedFsmActionEnterHooks.Add(new Hook(onEnterMethod, OnTargetedFsmActionEnter));
            } catch (Exception e) {
                Logger.Debug(
                    $"Could not hook {actionType.Name}.OnEnter for target retargeting: " +
                    $"{e.GetType().Name}: {e.Message}"
                );
            }
        }

        foreach (var actionType in new[] { typeof(SendEventByName), typeof(SendEventByNameV2) }) {
            var onEnterMethod = actionType.GetMethod("OnEnter", InstancePublicFlags | InstanceNonPublicFlags);
            if (onEnterMethod == null) {
                continue;
            }

            try {
                _targetedFsmActionEnterHooks.Add(new Hook(onEnterMethod, OnEventSenderEnter));
            } catch (Exception e) {
                Logger.Debug(
                    $"Could not hook {actionType.Name}.OnEnter for what enemies set off: " +
                    $"{e.GetType().Name}: {e.Message}"
                );
            }
        }
    }

    /// <summary>
    /// Notes what an enemy sets off by sending it an event, before the event goes.
    /// </summary>
    /// <param name="orig">The original action enter method.</param>
    /// <param name="self">The FSM action instance.</param>
    private static void OnEventSenderEnter(Action<FsmStateAction> orig, FsmStateAction self) {
        var eventTarget = self switch {
            SendEventByName action => action.eventTarget,
            SendEventByNameV2 action => action.eventTarget,
            _ => null
        };

        if (eventTarget != null && self.Fsm != null) {
            NoteWhatAnEnemySetsOff(self.Fsm, eventTarget);
        }

        orig(self);
    }

    /// <summary>
    /// Remembers that an enemy set off something of the room that is no enemy itself by sending it an event - the
    /// thing that aims and fires a boss's pins, say - so that where it looks at a player, it looks at whoever the enemy
    /// goes after (<see cref="ForceApprovedTargetOnFsmAction"/>). What such a thing aims at is the player of its own
    /// game, held in a variable like an enemy's, while the enemy that sets it off is the one that has chosen between
    /// the players: the pins went at the player whose game ran the boss, whichever of the two the boss was after.
    ///
    /// Passed on down a chain: a boss tells the thing that drops its boulders to drop, and that thing tells a boulder,
    /// which then drops for whoever the boss goes after too.
    /// </summary>
    /// <param name="fsm">The FSM that sends the event.</param>
    /// <param name="eventTarget">Where the event goes.</param>
    private static void NoteWhatAnEnemySetsOff(HutongGames.PlayMaker.Fsm fsm, FsmEventTarget eventTarget) {
        if (eventTarget.target is not (FsmEventTarget.EventTarget.GameObject or
            FsmEventTarget.EventTarget.GameObjectFSM)) {
            return;
        }

        var sender = fsm.GameObject;
        var senderOwner = sender == null ? null : GetEnemyTargetOwner(sender);
        if (senderOwner == null) {
            return;
        }

        // The enemy behind the event: the sender itself, or the enemy that set the sender off. Or the player that the
        // game which runs the creature said the sender was set off for, which what it sets off goes after as well.
        GameObject? enemy = null;
        GameObject? player = null;
        if (senderOwner.GetComponent<HealthManager>() != null) {
            enemy = senderOwner;
        } else if ((!SetOffForPlayer.TryGetValue(senderOwner.GetInstanceID(), out player) || player == null) &&
                   (!SetOffByEnemy.TryGetValue(senderOwner.GetInstanceID(), out enemy) || enemy == null)) {
            return;
        }

        // Only something that is on and runs an FSM of its own is set off by an event. One that is off may be an enemy
        // still asleep, which would be taken for the room's; one with no FSM would be noted under whatever holds it
        // higher up, which never got the event.
        var target = fsm.GetOwnerDefaultTarget(eventTarget.gameObject);
        if (target == null || !target.activeInHierarchy || target.GetComponent<PlayMakerFSM>() == null ||
            target.GetComponentInParent<HealthManager>() != null) {
            return;
        }

        // Enemies send the player events too, and the player's own actions look at what the player aims at
        if (PlayerTargetRegistry.IsHeroLikeObject(target)) {
            return;
        }

        var setOff = GetEnemyTargetOwner(target);
        if (setOff == null || setOff == senderOwner) {
            return;
        }

        // A player the other game named before is forgotten once an enemy of this game sets it off: that game ran the
        // creature then, and this one may run it now
        var id = setOff.GetInstanceID();
        if (player != null) {
            SetOffForPlayer[id] = player;
        } else {
            SetOffByEnemy[id] = enemy!;
            SetOffForPlayer.Remove(id);
        }
    }

    /// <summary>
    /// Disposes target-consuming FSM action <c>OnEnter</c> hooks.
    /// </summary>
    private void DisposeTargetedFsmActionEnterHooks() {
        foreach (var hook in _targetedFsmActionEnterHooks) {
            hook.Dispose();
        }

        _targetedFsmActionEnterHooks.Clear();
    }

    /// <summary>
    /// Forces an FSM action to use its enemy-approved target before and after its original <c>OnEnter</c> logic.
    /// </summary>
    /// <param name="orig">The original action enter method.</param>
    /// <param name="self">The FSM action instance.</param>
    private static void OnTargetedFsmActionEnter(Action<FsmStateAction> orig, FsmStateAction self) {
        ForceApprovedTargetOnFsmAction(self);
        orig(self);
        ForceApprovedTargetOnFsmAction(self);

        if (self is GetPosition getPosition) {
            ReadCameraOfWhoIsAimedAt(getPosition);
        }
    }

    /// <summary>
    /// The PlayMaker global that holds the game's main camera, once found.
    /// </summary>
    private static FsmGameObject? _mainCameraVariable;

    /// <summary>
    /// Half the width and half the height of what the camera shows, by which the game keeps it inside the room
    /// (<c>CameraController.KeepWithinSceneBounds</c>).
    /// </summary>
    private const float CameraHalfWidth = 14.6f, CameraHalfHeight = 8.3f;

    /// <summary>
    /// Has something of the room that an enemy set off, reading where the main camera is, read where the camera of the
    /// partner that enemy goes after would be. A boss drops some boulders from just above the top of the screen, and
    /// the scene host's screen is not the partner's, who may be well above or below.
    /// </summary>
    /// <param name="action">The GetPosition action that has just read a position.</param>
    private static void ReadCameraOfWhoIsAimedAt(GetPosition action) {
        var requester = action.Fsm?.GameObject;
        if (requester == null || action.space != Space.World || GetFsmActionTarget(requester) != null) {
            return;
        }

        var target = GetTargetOfWhoSetItOff(requester);
        var hero = HeroController.instance;
        if (target == null || hero == null || target == hero.gameObject) {
            return;
        }

        _mainCameraVariable ??= FsmVariables.GlobalVariables.FindFsmGameObject("MainCamera");
        var camera = _mainCameraVariable?.Value;
        if (camera == null || action.Fsm!.GetOwnerDefaultTarget(action.gameObject) != camera) {
            return;
        }

        var position = EstimateCameraPosition(target.transform.position, camera.transform.position.z);
        action.vector.Value = position;
        action.x.Value = position.x;
        action.y.Value = position.y;
        action.z.Value = position.z;
    }

    /// <summary>
    /// Where the game's camera would be for a player at the given place: over the player, kept inside the room the way
    /// the camera keeps itself (<c>CameraController.KeepWithinSceneBounds</c>, which is not called for this because it
    /// also sets flags of the camera itself). Looking up or down, or leading a fall, moves the real one a little
    /// further.
    /// </summary>
    /// <param name="player">Where the player is.</param>
    /// <param name="z">The depth of the camera.</param>
    /// <returns>Where the camera would be.</returns>
    private static Vector3 EstimateCameraPosition(Vector3 player, float z) {
        var position = new Vector3(player.x, player.y, z);

        var cameras = GameCameras.instance;
        var controller = cameras == null ? null : cameras.cameraController;
        if (controller == null || controller.AllowExitingSceneBounds) {
            return position;
        }

        if (position.x < CameraHalfWidth) {
            position.x = CameraHalfWidth;
        }

        if (position.x > controller.xLimit) {
            position.x = controller.xLimit;
        }

        if (position.y < CameraHalfHeight) {
            position.y = CameraHalfHeight;
        }

        if (position.y > controller.yLimit) {
            position.y = controller.yLimit;
        }

        return position;
    }

    /// <summary>
    /// Resolves the approved target transform for a <see cref="Walker"/> instance.
    /// Falls back to the vanilla target field when no approved multiplayer target exists.
    /// </summary>
    /// <param name="self">The walker being queried.</param>
    /// <returns>The transform of the target, or null if none is available.</returns>
    private static Transform? GetApprovedEnemyTargetTransform(Walker self) {
        var approvedTarget = GetApprovedEnemyTarget(self.gameObject);
        if (approvedTarget != null) {
            return approvedTarget.transform;
        }

        var hero = GetWalkerHero?.Invoke(self) ?? (HeroController?) WalkerHeroField?.GetValue(self);
        return hero != null ? hero.transform : null;
    }

    /// <summary>
    /// IL hook that rewrites hero field reads in <see cref="Walker.UpdateWaitingForConditions"/>
    /// to use the approved multiplayer target instead.
    /// </summary>
    /// <param name="il">The IL context to modify.</param>
    private static void ILWalkerUpdateWaitingForConditions(ILContext il) {
        try {
            var c = new ILCursor(il);

            while (c.TryGotoNext(
                       MoveType.Before,
                       i => i.MatchLdarg(0),
                       i => i.MatchLdfld(typeof(Walker), "hero")
                   )) {
                if (c.Next.Next.Next != null &&
                    c.Next.Next.Next.MatchCallvirt(typeof(Component), "get_transform")) {
                    c.RemoveRange(3);
                    c.Emit(OpCodes.Ldarg_0);
                    c.Emit(
                        OpCodes.Call,
                        typeof(GamePatcher).GetMethod(
                            nameof(GetApprovedEnemyTargetTransform), BindingFlags.NonPublic | BindingFlags.Static
                        )!
                    );
                } else {
                    c.RemoveRange(2);
                    c.Emit(OpCodes.Ldarg_0);
                    c.Emit(
                        OpCodes.Call,
                        typeof(GamePatcher).GetMethod(
                            nameof(GetApprovedEnemyTargetTransform), BindingFlags.NonPublic | BindingFlags.Static
                        )!
                    );
                }
            }
        } catch (Exception e) {
            Logger.Error($"Could not apply ILWalkerUpdateWaitingForConditions hook: {e}");
        }
    }

    /// <summary>
    /// IL hook that rewrites hero field reads in <see cref="Walker.UpdateWalking"/>
    /// to use the approved multiplayer target instead.
    /// </summary>
    /// <param name="il">The IL context to modify.</param>
    private static void ILWalkerUpdateWalking(ILContext il) {
        try {
            var c = new ILCursor(il);

            while (c.TryGotoNext(
                       MoveType.Before,
                       i => i.MatchLdarg(0),
                       i => i.MatchLdfld(typeof(Walker), "hero")
                   )) {
                if (c.Next.Next.Next != null &&
                    c.Next.Next.Next.MatchCallvirt(typeof(Component), "get_transform")) {
                    c.RemoveRange(3);
                    c.Emit(OpCodes.Ldarg_0);
                    c.Emit(
                        OpCodes.Call,
                        typeof(GamePatcher).GetMethod(
                            nameof(GetApprovedEnemyTargetTransform), BindingFlags.NonPublic | BindingFlags.Static
                        )!
                    );
                } else {
                    c.RemoveRange(2);
                    c.Emit(OpCodes.Ldarg_0);
                    c.Emit(
                        OpCodes.Call,
                        typeof(GamePatcher).GetMethod(
                            nameof(GetApprovedEnemyTargetTransform), BindingFlags.NonPublic | BindingFlags.Static
                        )!
                    );
                }
            }
        } catch (Exception e) {
            Logger.Error($"Could not apply ILWalkerUpdateWalking hook: {e}");
        }
    }

    /// <summary>
    /// Refreshes enemy target references that need to follow the approved multiplayer target.
    /// </summary>
    private void OnUpdateRetargetEnemyTransforms() {
        if (Time.unscaledTime < _nextEnemyRetargetTime) {
            return;
        }

        _nextEnemyRetargetTime = Time.unscaledTime + EnemyRetargetIntervalSeconds;

        UpdateTargetedFsmActions();
        UpdateCachedTargetField<WalkerV2>(WalkerV2HeroField);
        UpdateCachedTargetField<ScuttlerControl>(ScuttlerControlHeroField);
    }

    /// <summary>
    /// Forces an immediate retargeting update for all targeted FSM actions and components.
    /// </summary>
    public static void ForceImmediateRetarget() {
        UpdateTargetedFsmActions();
        UpdateCachedTargetField<WalkerV2>(WalkerV2HeroField);
        UpdateCachedTargetField<ScuttlerControl>(ScuttlerControlHeroField);
    }

    /// <summary>
    /// Forces a target-consuming FSM action to use the approved multiplayer target of its owning enemy.
    /// </summary>
    /// <param name="action">The FSM action to retarget.</param>
    private static void ForceApprovedTargetOnFsmAction(FsmStateAction action) {
        var requester = action.Fsm?.GameObject;
        if (requester == null) {
            return;
        }

        var approvedTarget = GetFsmActionTarget(requester);
        if (approvedTarget != null) {
            RetargetFsmActionGameObjectFields(
                action, approvedTarget, ActionRegistry.TargetedWherePlayerActionTypes.Contains(action.GetType().Name)
            );
            return;
        }

        // Something of the room that an enemy set off goes after whoever that enemy goes after, but only where it
        // looks at a player: a variable named like a target may hold anything else there, like the enemy an ally fights
        var setOffTarget = GetTargetOfWhoSetItOff(requester);
        if (setOffTarget != null) {
            RetargetFsmActionGameObjectFields(action, setOffTarget, onlyWherePlayer: true);
        }
    }

    /// <summary>
    /// Updates active enemy FSM target-consuming actions so they use their approved multiplayer target.
    /// </summary>
    private static void UpdateTargetedFsmActions() {
        if (_entityManagerInstance == null) {
            return;
        }

        foreach (var entity in _entityManagerInstance.ActiveEntities) {
            if (entity == null) {
                continue;
            }

            var hostFsms = entity.HostFsms;
            foreach (var fsm in hostFsms) {
                RetargetActiveTargetedFsmActions(fsm);
            }

            var clientFsms = entity.ClientFsms;
            foreach (var fsm in clientFsms) {
                RetargetActiveTargetedFsmActions(fsm);
            }
        }
    }

    /// <summary>
    /// Retargets the currently active target-consuming actions of one enemy FSM to the enemy's approved target.
    /// </summary>
    /// <param name="fsm">The FSM to inspect.</param>
    private static void RetargetActiveTargetedFsmActions(PlayMakerFSM fsm) {
        if (fsm == null || !fsm.isActiveAndEnabled) {
            return;
        }

        if (fsm.GetComponentInParent<HealthManager>() == null) {
            return;
        }

        if (PlayerTargetRegistry.GetTrackedPlayerRoot(fsm.gameObject) != null) {
            return;
        }

        var approvedTarget = GetFsmActionTarget(fsm.gameObject);
        if (approvedTarget == null) {
            return;
        }

        var activeState = fsm.Fsm?.ActiveState;
        if (activeState?.Actions == null) {
            return;
        }

        foreach (var action in activeState.Actions) {
            if (action == null || !action.Enabled) {
                continue;
            }

            var typeName = action.GetType().Name;
            if (!ActionRegistry.IsRetargeted(typeName)) {
                continue;
            }

            RetargetFsmActionGameObjectFields(
                action, approvedTarget, ActionRegistry.TargetedWherePlayerActionTypes.Contains(typeName)
            );
        }
    }

    /// <summary>
    /// Rewrites target-specific <see cref="FsmGameObject"/> fields on a target-consuming FSM action to direct
    /// approved-target references, detached from shared FSM variables such as <c>Hero</c>, or gives a field back what
    /// it held, decided from that each time (see <see cref="PointedAims"/>).
    /// </summary>
    /// <param name="action">The active FSM action to retarget.</param>
    /// <param name="approvedTarget">The approved multiplayer target for the owning enemy.</param>
    /// <param name="onlyWherePlayer">Whether only what points at a player is rewritten.</param>
    private static void RetargetFsmActionGameObjectFields(
        FsmStateAction action,
        GameObject approvedTarget,
        bool onlyWherePlayer = false
    ) {
        foreach (var field in GetFsmGameObjectFields(action.GetType())) {
            if (field.GetValue(action) is not FsmGameObject current) {
                continue;
            }

            var held = HeldAim(action, field, current);
            var aims = CanRetargetFsmGameObjectField(action, field, held) &&
                       ShouldWriteDirectTarget(action, held, approvedTarget, onlyWherePlayer);
            PointAim(action, field, null, current, aims ? approvedTarget : null);
        }

        // Some actions do not keep the object they are about in a plain variable at all. They keep it in the pair
        // PlayMaker uses for "my owner, or that one over there", and reading a position is the one of those that
        // counts: it is how a thrown thing is aimed. Only fields are looked at above and that pair is not one, so an
        // aim taken this way went on reading whichever player the room had settled on - which is one enemy facing
        // one player while the rock it threw went at the other.
        foreach (var field in GetFsmOwnerDefaultFields(action.GetType())) {
            if (field.GetValue(action) is not FsmOwnerDefault owner) {
                continue;
            }

            // Left alone when it means the owner: that is the enemy talking about itself, and pointing it at a
            // player would have it read its own position as the player position.
            if (owner.OwnerOption == OwnerDefaultOption.UseOwner || owner.GameObject is not { } current) {
                continue;
            }

            var held = HeldAim(action, field, current);
            var aims = CanRetargetFsmGameObjectField(action, field, held) &&
                       ShouldWriteDirectTarget(action, held, approvedTarget, onlyWherePlayer);
            PointAim(action, field, owner, current, aims ? approvedTarget : null);
        }
    }

    /// <summary>
    /// What a field of an action held before it was pointed at a player (see <see cref="PointedAims"/>), or what it
    /// holds now if it is not pointed.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <param name="field">The field, of its own object or of an owner-or-this-one pair.</param>
    /// <param name="current">What the field holds now.</param>
    private static FsmGameObject HeldAim(FsmStateAction action, FieldInfo field, FsmGameObject current) {
        return PointedAims.TryGetValue(action, out var aims) && aims.TryGetValue(field, out var aim) &&
               ReferenceEquals(aim.Pointed, current)
            ? aim.Held
            : current;
    }

    /// <summary>
    /// Points a field of an action at a player with an object of its own, kept for the field from then on, and keeps
    /// what the field held; or, for no player, gives the field back what it held (see <see cref="PointedAims"/>).
    /// This prevents vanilla PlayMaker variables such as <c>Hero</c> from fighting the multiplayer retargeter. A field
    /// that holds something else than the object it was pointed with, put there since, is taken as it is now.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <param name="field">The field.</param>
    /// <param name="owner">The owner-or-this-one pair that the field holds, or null for a field of its own
    /// object.</param>
    /// <param name="current">What the field holds now.</param>
    /// <param name="player">The player, or null.</param>
    private static void PointAim(
        FsmStateAction action,
        FieldInfo field,
        FsmOwnerDefault? owner,
        FsmGameObject current,
        GameObject? player
    ) {
        // What the field held, while it holds what it was pointed with
        PointedAims.TryGetValue(action, out var aims);
        FsmGameObject? held = null;
        if (aims != null && aims.TryGetValue(field, out var aim) && ReferenceEquals(aim.Pointed, current)) {
            held = aim.Held;
        }

        if (player == null) {
            if (aims != null && aims.Remove(field) && held != null) {
                Set(held);
            }

            return;
        }

        if (held != null) {
            if (current.Value != player) {
                current.Value = player;
                _targetsWrittenIntoActions++;
            }

            return;
        }

        var own = new FsmGameObject { Value = player };
        PointedAims.GetOrCreateValue(action)[field] = (current, own);
        Set(own);
        _targetsWrittenIntoActions++;

        void Set(FsmGameObject value) {
            if (owner != null) {
                owner.GameObject = value;
            } else {
                field.SetValue(action, value);
            }
        }
    }

    /// <summary>
    /// Whether what an action is currently pointed at should be replaced with the approved target.
    /// </summary>
    /// <param name="action">The action being retargeted.</param>
    /// <param name="fsmGameObject">What the action currently points at.</param>
    /// <param name="approvedTarget">The approved multiplayer target.</param>
    /// <param name="onlyWherePlayer">Whether it is written only over a player.</param>
    /// <returns><see langword="true"/> when it should be written; otherwise <see langword="false"/>.</returns>
    private static bool ShouldWriteDirectTarget(
        FsmStateAction action,
        FsmGameObject fsmGameObject,
        GameObject approvedTarget,
        bool onlyWherePlayer
    ) {
        var variableName = fsmGameObject.Name;

        if (!string.IsNullOrEmpty(variableName) &&
            variableName.Equals("Self", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        var currentTarget = fsmGameObject.Value;

        if (currentTarget == approvedTarget && string.IsNullOrEmpty(variableName)) {
            return false;
        }

        // Only over a player (see ForceApprovedTargetOnFsmAction)
        if (onlyWherePlayer) {
            return currentTarget != null && PlayerTargetRegistry.IsHeroLikeObject(currentTarget);
        }

        if (currentTarget != null &&
            currentTarget != approvedTarget &&
            IsSameEnemyOwner(action.Fsm?.GameObject, currentTarget)) {
            return false;
        }

        // A player, or nothing yet. A variable named like a target that holds anything else is about that: the
        // creature an ally fights, the one a pilgrim goes for, the marker a boss aims by
        return currentTarget == null || currentTarget == approvedTarget ||
               PlayerTargetRegistry.IsHeroLikeObject(currentTarget);
    }

    /// <summary>
    /// Determines whether a reflected <see cref="FsmGameObject"/> field is a target field that may be rewritten.
    /// </summary>
    /// <param name="action">The action that owns the field.</param>
    /// <param name="field">The reflected FSM action field.</param>
    /// <param name="fsmGameObject">The field value.</param>
    /// <returns>
    /// <see langword="true"/> when the field is known to represent a target object; otherwise
    /// <see langword="false"/>.
    /// </returns>
    private static bool CanRetargetFsmGameObjectField(
        FsmStateAction action,
        FieldInfo field,
        FsmGameObject fsmGameObject
    ) {
        // Where the action keeps what it found or worked out, which it writes rather than looks at: a variable named
        // like a target kept there would be cut off from the FSM, which then never got what the action found
        if (field.Name.StartsWith("store", StringComparison.OrdinalIgnoreCase) ||
            action is SetGameObject && field.Name.Equals("variable", StringComparison.Ordinal)) {
            return false;
        }

        var variableName = fsmGameObject.Name;
        if (!string.IsNullOrEmpty(variableName)) {
            if (variableName.Equals("Self", StringComparison.OrdinalIgnoreCase)) {
                return false;
            }

            if (IsTargetVariableName(variableName)) {
                return true;
            }
        }

        var actionType = action.GetType();

        if (actionType == typeof(FaceObject) || actionType == typeof(FaceObjectV2)) {
            return field.Name.Equals("objectB", StringComparison.OrdinalIgnoreCase) ||
                   field.Name.Equals("target", StringComparison.OrdinalIgnoreCase);
        }

        return field.Name.Equals("target", StringComparison.OrdinalIgnoreCase) ||
               field.Name.Equals("targetObject", StringComparison.OrdinalIgnoreCase) ||
               field.Name.Equals("targetGameObject", StringComparison.OrdinalIgnoreCase) ||
               field.Name.Equals("targetObj", StringComparison.OrdinalIgnoreCase) ||
               field.Name.Equals("gameObject", StringComparison.OrdinalIgnoreCase) &&
               PlayerTargetRegistry.IsHeroLikeObject(fsmGameObject.Value);
    }

    /// <summary>
    /// Determines whether an FSM variable name represents a player target reference.
    /// </summary>
    /// <param name="variableName">The FSM variable name.</param>
    /// <returns>
    /// <see langword="true"/> when the variable is safe to rewrite to an approved player target; otherwise
    /// <see langword="false"/>.
    /// </returns>
    internal static bool IsTargetVariableName(string variableName) {
        return variableName.Equals("Hero", StringComparison.OrdinalIgnoreCase) ||
               variableName.Equals("Target", StringComparison.OrdinalIgnoreCase) ||
               variableName.Equals("Target Object", StringComparison.OrdinalIgnoreCase) ||
               variableName.Equals("TargetObject", StringComparison.OrdinalIgnoreCase) ||
               variableName.Equals("Player", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets all instance fields of an FSM action type that store <see cref="FsmGameObject"/> values.
    /// </summary>
    /// <param name="actionType">The FSM action type to inspect.</param>
    /// <returns>The reflected <see cref="FsmGameObject"/> fields for this action type.</returns>
    private static FieldInfo[] GetFsmGameObjectFields(Type actionType) {
        if (TargetedActionGameObjectFieldCache.TryGetValue(actionType, out var cachedFields)) {
            return cachedFields;
        }

        var fields = actionType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var fsmGameObjectFields = new List<FieldInfo>();

        foreach (var field in fields) {
            if (typeof(FsmGameObject).IsAssignableFrom(field.FieldType)) {
                fsmGameObjectFields.Add(field);
            }
        }

        var result = fsmGameObjectFields.ToArray();
        TargetedActionGameObjectFieldCache[actionType] = result;

        return result;
    }

    /// <summary>
    /// Gets all instance fields of an FSM action type that hold an owner-or-this-one pair.
    /// </summary>
    /// <param name="actionType">The FSM action type to inspect.</param>
    /// <returns>The reflected <see cref="FsmOwnerDefault"/> fields for this action type.</returns>
    private static FieldInfo[] GetFsmOwnerDefaultFields(Type actionType) {
        if (TargetedActionOwnerDefaultFieldCache.TryGetValue(actionType, out var cachedFields)) {
            return cachedFields;
        }

        var fields = actionType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var ownerDefaultFields = new List<FieldInfo>();

        foreach (var field in fields) {
            if (typeof(FsmOwnerDefault).IsAssignableFrom(field.FieldType)) {
                ownerDefaultFields.Add(field);
            }
        }

        var result = ownerDefaultFields.ToArray();
        TargetedActionOwnerDefaultFieldCache[actionType] = result;

        return result;
    }

    /// <summary>
    /// Determines whether two objects resolve to the same enemy target owner.
    /// </summary>
    /// <param name="left">The first object.</param>
    /// <param name="right">The second object.</param>
    /// <returns>
    /// <see langword="true"/> when both objects belong to the same enemy owner; otherwise
    /// <see langword="false"/>.
    /// </returns>
    private static bool IsSameEnemyOwner(GameObject? left, GameObject? right) {
        if (left == null || right == null) {
            return false;
        }

        var leftOwner = GetEnemyTargetOwner(left);
        var rightOwner = GetEnemyTargetOwner(right);

        return leftOwner != null && rightOwner != null && leftOwner == rightOwner;
    }

    /// <summary>
    /// Determines whether an enemy should transfer its approved target to a new candidate.
    /// </summary>
    /// <param name="owner">The enemy owner.</param>
    /// <param name="approvedTarget">The currently approved target.</param>
    /// <param name="candidateTarget">The candidate target found by the acquisition range.</param>
    /// <returns>
    /// <see langword="true"/> when the candidate is clearly closer than the current approved target; otherwise
    /// <see langword="false"/>.
    /// </returns>
    private static bool ShouldSwitchApprovedTarget(
        GameObject owner,
        GameObject approvedTarget,
        GameObject candidateTarget
    ) {
        if (approvedTarget == candidateTarget) {
            return false;
        }

        if (!PlayerTargetRegistry.IsHeroLikeObject(approvedTarget)) {
            return true;
        }

        // Held to the player it turned to for a moment, so that being asked again does not mean deciding again. This
        // is only ever reached while the player it is holding to is still there and still in sight: a target that has
        // died, left or gone out of sight never gets this far, it is taken away before anything is asked.
        if (EnemyTargetLocks.TryGetValue(owner.GetInstanceID(), out var lockedUntil) &&
            Time.unscaledTime < lockedUntil) {
            return false;
        }

        var ownerPosition = (Vector2) owner.transform.position;
        var approvedDistance = ((Vector2) approvedTarget.transform.position - ownerPosition).sqrMagnitude;
        var candidateDistance = ((Vector2) candidateTarget.transform.position - ownerPosition).sqrMagnitude;

        return candidateDistance < approvedDistance * TargetSwitchNearerShareSquared;
    }

    /// <summary>
    /// Updates a cached target field on all active behaviour instances of the given type.
    /// </summary>
    /// <typeparam name="TComponent">The behaviour type containing the cached target field.</typeparam>
    /// <param name="field">The field that stores the cached target.</param>
    private static void UpdateCachedTargetField<TComponent>(FieldInfo? field)
        where TComponent : Behaviour {
        if (field == null || _entityManagerInstance == null) {
            return;
        }

        foreach (var entity in _entityManagerInstance.ActiveEntities) {
            if (entity == null) {
                continue;
            }

            if (entity.Object.Host != null) {
                var component = entity.Object.Host.GetComponentInChildren<TComponent>();
                if (component != null && component.isActiveAndEnabled) {
                    UpdateComponentTarget(component, field);
                }
            }

            if (entity.Object.Client != null) {
                var component = entity.Object.Client.GetComponentInChildren<TComponent>();
                if (component != null && component.isActiveAndEnabled) {
                    UpdateComponentTarget(component, field);
                }
            }
        }
    }

    /// <summary>
    /// Updates the target reference of a component instance with the approved enemy target.
    /// </summary>
    /// <typeparam name="TComponent">The behaviour type containing the cached target field.</typeparam>
    /// <param name="component">The component instance to update.</param>
    /// <param name="field">The target field to write the approved target to.</param>
    private static void UpdateComponentTarget<TComponent>(TComponent component, FieldInfo field)
        where TComponent : Behaviour {
        var target = GetApprovedEnemyTarget(component.gameObject);
        if (target == null) {
            return;
        }

        if (field.FieldType == typeof(Transform)) {
            field.SetValue(component, target.transform);
        } else if (field.FieldType == typeof(GameObject)) {
            field.SetValue(component, target);
        }
    }

    /// <summary>
    /// Resolves the enemy object that owns targeting state for the supplied object.
    /// </summary>
    /// <param name="obj">An object inside an enemy hierarchy.</param>
    /// <returns>The enemy object that should own target state.</returns>
    /// <remarks>
    /// Enemy targeting hooks can run from child objects such as alert ranges, line-of-sight detectors, FSM objects,
    /// or movement controllers. This method normalizes those callers back to the actual enemy instance so target state
    /// is stored per mob instead of accidentally shared through a scene/root container.
    /// </remarks>
    private static GameObject? GetEnemyTargetOwner(GameObject obj) {
        if (obj == null) {
            return obj;
        }

        var instanceId = obj.GetInstanceID();
        if (TargetOwnerCache.TryGetValue(instanceId, out var cachedOwner) && cachedOwner != null) {
            return cachedOwner;
        }

        GameObject resolvedOwner;
        var healthManager = obj.GetComponentInParent<HealthManager>();
        if (healthManager != null) {
            resolvedOwner = healthManager.gameObject;
        } else {
            var walker = obj.GetComponentInParent<Walker>();
            if (walker != null) {
                resolvedOwner = walker.gameObject;
            } else {
                var walkerV2 = obj.GetComponentInParent<WalkerV2>();
                if (walkerV2 != null) {
                    resolvedOwner = walkerV2.gameObject;
                } else {
                    var fsm = obj.GetComponentInParent<PlayMakerFSM>();
                    resolvedOwner = fsm != null ? fsm.gameObject : obj;
                }
            }
        }

        TargetOwnerCache[instanceId] = resolvedOwner;
        return resolvedOwner;
    }

    /// <summary>
    /// Removes cached targeting data for an entity GameObject that is being destroyed.
    /// </summary>
    public static void OnEntityDestroyed(GameObject? obj) {
        if (obj == null) return;
        var instanceId = obj.GetInstanceID();
        EnemyApprovedTargets.Remove(instanceId);
        EnemyTargetLocks.Remove(instanceId);
        TargetOwnerCache.Remove(instanceId);
        SongTargets.Remove(instanceId);
        PendingSongTargets.Remove(instanceId);
        ForgetMoveStartTargets(instanceId);
    }
}
