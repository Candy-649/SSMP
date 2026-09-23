using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using GlobalSettings;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Animation.Effects.Tools;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Keeps each thrown tool with the player who threw it. The partner's tools are shown here as copies that only look
/// the part: the thrower's game does the hits and sends what they looked like. For that, a copy has to stay out of this
/// game in every other way too - it must not freeze or shake this player's screen, count against this player's own
/// tools, read this player's equipment, or spawn anything that hits as this player's own. And in the game, only the
/// player who threw a tool can set it off with an attack; here that holds both ways, for the partner's copies and the
/// attacks of this player, and for this player's own tools and the copies of the partner's attacks.
/// </summary>
internal static class ToolCopies {
    /// <summary>
    /// The layer of the attacks of a player, which is how a tool that waits to be set off tells an attack.
    /// </summary>
    private const int HeroAttackLayer = (int) GlobalEnums.PhysLayers.HERO_ATTACK;

    /// <summary>
    /// The tools whose thrown things are sent to the partner and shown there by <see cref="ThrownTool"/>. Each one is
    /// only added once what its thing does has been gone through: some of them come back to the hero or follow them
    /// around, which a copy must do with the thrower instead.
    /// </summary>
    private static readonly HashSet<string> CopiedTools = [
        "Sting Shard"
    ];

    /// <summary>
    /// The tool that the hero is about to throw.
    /// </summary>
    private static readonly FieldInfo? WillThrowToolField =
        typeof(HeroController).GetField("willThrowTool", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// The tool that a limiter counts, which it counts against the local player's own number of them.
    /// </summary>
    private static readonly FieldInfo? LimiterToolField =
        typeof(ToolItemLimiter).GetField("representingTool", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// Breaks the thing that a limiter belongs to, the way the game breaks the oldest one of too many.
    /// </summary>
    private static readonly MethodInfo? LimiterBreakMethod =
        typeof(ToolItemLimiter).GetMethod("Break", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// The events that checks of the equipment had before a copy was made to answer them the thrower's way, since a
    /// pooled copy is used again for a thrower with other equipment.
    /// </summary>
    private static readonly ConditionalWeakTable<CheckIfToolEquipped, FsmEvent[]> CheckEvents = new();

    /// <summary>
    /// Private copies of the things that the partner's tools spawn, by the thing they copy, or null for a thing that
    /// needs none. A copy of the partner's tool spawns these instead, so that nothing it makes shares a pool with the
    /// things that this player's own tools make.
    /// </summary>
    private static readonly Dictionary<GameObject, GameObject?> Twins = new();

    /// <summary>
    /// The copies that the partner's thrown tools are made from, by the prefab that the game throws.
    /// </summary>
    private static readonly Dictionary<GameObject, GameObject> CopyPrefabs = new();

    /// <summary>
    /// The hooks, which stay for as long as the game runs.
    /// </summary>
    private static readonly List<Hook> Hooks = [];

    /// <summary>
    /// Whether the hooks are in place.
    /// </summary>
    private static bool _installed;

    /// <summary>
    /// An object that is never switched on, which holds the prefabs of the copies so that nothing in them runs.
    /// </summary>
    private static GameObject? _holder;

    /// <summary>
    /// The prefab that the hero throws right now, or null.
    /// </summary>
    private static GameObject? _throwPrefab;

    /// <summary>
    /// The thing that the hero threw right now, or null.
    /// </summary>
    private static GameObject? _thrown;

    /// <summary>
    /// Whether the local player runs the enemies of the room, which is where a copy can pull them.
    /// </summary>
    public static Func<bool>? IsSceneHost { get; set; }

    /// <summary>
    /// Called with what a tool that the local player threw looked like a moment after it left their hand, for the
    /// partner to show a copy of.
    /// </summary>
    public static event Action<ThrowInfo>? ToolThrown;

    /// <summary>
    /// Puts the hooks in place, once.
    /// </summary>
    public static void Install() {
        if (_installed) {
            return;
        }

        _installed = true;

        const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        AddHook(
            typeof(HeroController).GetMethod("ThrowTool", instanceFlags, null, [typeof(bool)], null),
            new Action<Action<HeroController, bool>, HeroController, bool>(OnThrowTool)
        );
        AddHook(
            typeof(ObjectPool).GetMethod(
                "Spawn",
                staticFlags,
                null,
                [typeof(GameObject), typeof(Transform), typeof(Vector3), typeof(Quaternion), typeof(bool)],
                null
            ),
            new Func<SpawnMethod, GameObject, Transform, Vector3, Quaternion, bool, GameObject>(OnSpawn)
        );
        AddHook(
            typeof(Fsm).GetMethod("OnTriggerEnter2D", instanceFlags, null, [typeof(Collider2D)], null),
            new Action<Action<Fsm, Collider2D>, Fsm, Collider2D>(OnFsmTrigger)
        );
        AddHook(
            typeof(Fsm).GetMethod("OnTriggerStay2D", instanceFlags, null, [typeof(Collider2D)], null),
            new Action<Action<Fsm, Collider2D>, Fsm, Collider2D>(OnFsmTrigger)
        );
        AddHook(
            typeof(PlayMakerProxyBase).GetMethod(
                "DoTrigger2DEventCallback", instanceFlags, null, [typeof(Collider2D)], null
            ),
            new Action<Action<PlayMakerProxyBase, Collider2D>, PlayMakerProxyBase, Collider2D>(OnProxyTrigger)
        );
        AddHook(
            typeof(CustomPlayMakerTriggerStay2D).GetMethod(
                "OnTriggerStay2D", instanceFlags, null, [typeof(Collider2D)], null
            ),
            new Action<Action<CustomPlayMakerTriggerStay2D, Collider2D>, CustomPlayMakerTriggerStay2D, Collider2D>(
                OnCustomStayTrigger
            )
        );
    }

    /// <summary>
    /// The spawn method of the pool that all others end in.
    /// </summary>
    private delegate GameObject SpawnMethod(
        GameObject prefab,
        Transform parent,
        Vector3 position,
        Quaternion rotation,
        bool stealActiveSpawned
    );

    /// <summary>
    /// Creates a hook, logging instead of throwing when the method is missing.
    /// </summary>
    private static void AddHook(MethodInfo? method, Delegate detour) {
        if (method == null) {
            Logger.Error($"Could not find the method for {detour.Method.Name}; the tool hook was not registered");
            return;
        }

        try {
            Hooks.Add(new Hook(method, detour));
        } catch (Exception e) {
            Logger.Error($"Could not hook the method for {detour.Method.Name}:\n{e}");
        }
    }

    #region The local player's throws

    /// <summary>
    /// Catches the thing that the hero throws, for the tools in <see cref="CopiedTools"/>.
    /// </summary>
    private static void OnThrowTool(Action<HeroController, bool> orig, HeroController self, bool isAutoThrow) {
        ToolItem? tool = null;
        try {
            tool = WillThrowToolField?.GetValue(self) as ToolItem;
            _throwPrefab = tool != null && CopiedTools.Contains(tool.name) ? tool.Usage.ThrowPrefab : null;
        } catch (Exception e) {
            Logger.Warn($"Could not see which tool the hero throws: {e.Message}");
        }

        _thrown = null;
        try {
            orig(self, isAutoThrow);
        } finally {
            _throwPrefab = null;
        }

        var thrown = _thrown;
        _thrown = null;
        if (thrown == null || tool == null) {
            return;
        }

        thrown.AddComponentIfNotPresent<LocalToolComponent>();
        MonoBehaviourUtil.Instance.StartCoroutine(SendThrow(tool, thrown));
    }

    /// <summary>
    /// Tells the partner about a thrown tool once it is on its way. Waiting for the next frame lets the thing start
    /// first, which some of them do with a random push or spin of their own, so the copy takes both over as they are.
    /// </summary>
    private static IEnumerator SendThrow(ToolItem tool, GameObject thrown) {
        yield return null;

        if (thrown == null || !thrown.activeInHierarchy || ToolThrown == null) {
            yield break;
        }

        try {
            // The number of these that may be out at once, which the game works out the same way when it breaks the
            // oldest one of too many
            var usage = tool.Usage;
            var maxActive = usage.UseAltForQuickSling && Gameplay.QuickSlingTool.IsEquipped
                ? usage.MaxActiveAlt
                : usage.MaxActive;

            var transform = thrown.transform;
            var body = thrown.GetComponent<Rigidbody2D>();
            ToolThrown.Invoke(new ThrowInfo {
                ToolName = tool.name,
                Poisoned = Gameplay.PoisonPouchTool.IsEquipped,
                MaxActive = maxActive,
                Position = transform.position,
                Rotation = transform.eulerAngles.z,
                Scale = transform.localScale,
                Velocity = body != null ? body.linearVelocity : Vector2.zero,
                AngularVelocity = body != null ? body.angularVelocity : 0f
            });
        } catch (Exception e) {
            Logger.Warn($"Could not send the thrown '{tool.name}' to the partner: {e.Message}");
        }
    }

    #endregion

    #region Spawning

    /// <summary>
    /// Catches what the hero throws, marks what the local player's tools spawn as theirs, and has the copies of the
    /// partner's tools spawn their own private copies of things. Other copies of the partner's attacks are left to
    /// spawn as they always have.
    /// </summary>
    private static GameObject OnSpawn(
        SpawnMethod orig,
        GameObject prefab,
        Transform parent,
        Vector3 position,
        Quaternion rotation,
        bool stealActiveSpawned
    ) {
        var owner = prefab != null ? FsmExecutionStack.ExecutingFsm?.GameObject : null;
        var copy = owner != null ? owner.GetComponentInParent<RemoteToolCopy>(true) : null;
        var twinned = false;
        if (copy != null && GetTwin(prefab!) is { } twin) {
            prefab = twin;
            twinned = true;
        }

        var spawned = orig(prefab!, parent, position, rotation, stealActiveSpawned);
        if (spawned == null) {
            return spawned!;
        }

        try {
            if (_throwPrefab != null && prefab == _throwPrefab && _thrown == null) {
                _thrown = spawned;
            } else if (twinned) {
                // What it spawns in turn comes from its own private copies as well
                var poisoned = copy!.Poisoned;
                spawned.AddComponentIfNotPresent<RemoteToolCopy>().Poisoned = poisoned;
                PrepareCopy(spawned, poisoned);
            } else if (owner != null && spawned.GetComponentInChildren<Collider2D>(true) != null &&
                       IsLocalToolSpawner(owner)) {
                // Only something that can be touched can be set off; a sound or a puff of dust is left unmarked
                spawned.AddComponentIfNotPresent<LocalToolComponent>();
            }
        } catch (Exception e) {
            Logger.Warn($"Could not sort out whose '{spawned.name}' was spawned: {e.Message}");
        }

        return spawned;
    }

    /// <summary>
    /// Whether an FSM that spawns something belongs to a tool of the local player, or is the one of the hero that
    /// uses their tools.
    /// </summary>
    private static bool IsLocalToolSpawner(GameObject owner) {
        if (LocalToolComponent.IsLocalTool(owner)) {
            return true;
        }

        var hero = HeroController.SilentInstance;
        return hero != null && hero.toolsFSM != null && FsmExecutionStack.ExecutingFsm == hero.toolsFSM.Fsm;
    }

    /// <summary>
    /// Gets the private copy of a thing that the partner's tools spawn, making it the first time, or null for a thing
    /// that can't hit, set anything off or run anything of its own, like a puff of dust or a sound.
    /// </summary>
    private static GameObject? GetTwin(GameObject prefab) {
        if (Twins.TryGetValue(prefab, out var twin)) {
            return twin;
        }

        if (prefab.GetComponentInChildren<DamageEnemies>(true) == null &&
            prefab.GetComponentInChildren<DamageHero>(true) == null &&
            prefab.GetComponentInChildren<PlayMakerFSM>(true) == null) {
            Twins[prefab] = null;
            return null;
        }

        twin = MakeCopyPrefab(prefab);
        Twins[prefab] = twin;
        return twin;
    }

    /// <summary>
    /// Gets what the copies of a thrown tool are made from, making it the first time. It stays switched off, so every
    /// copy made from it starts switched off and can be set up before anything in it runs.
    /// </summary>
    /// <param name="prefab">The prefab that the game throws.</param>
    public static GameObject GetCopyPrefab(GameObject prefab) {
        if (CopyPrefabs.TryGetValue(prefab, out var copyPrefab) && copyPrefab != null) {
            return copyPrefab;
        }

        copyPrefab = MakeCopyPrefab(prefab);
        copyPrefab.SetActive(false);
        CopyPrefabs[prefab] = copyPrefab;
        return copyPrefab;
    }

    /// <summary>
    /// Makes a copy of a prefab that belongs to the partner: it is marked as theirs, counts against none of the local
    /// player's tools and can't hurt the local player.
    /// </summary>
    private static GameObject MakeCopyPrefab(GameObject prefab) {
        if (_holder == null) {
            _holder = new GameObject("Partner Tool Copies");
            _holder.SetActive(false);
            Object.DontDestroyOnLoad(_holder);
        }

        // Under the holder nothing in it wakes up, while the copies made from it are free of it
        var copyPrefab = Object.Instantiate(prefab, _holder.transform);
        copyPrefab.name = prefab.name;
        copyPrefab.AddComponentIfNotPresent<RemoteAttackComponent>();

        // A limiter without a tool counts nothing, and can still break its own thing when told to
        foreach (var limiter in copyPrefab.GetComponentsInChildren<ToolItemLimiter>(true)) {
            LimiterToolField?.SetValue(limiter, null);
        }

        foreach (var damageHero in copyPrefab.GetComponentsInChildren<DamageHero>(true)) {
            Object.DestroyImmediate(damageHero);
        }

        foreach (var freeze in copyPrefab.GetComponentsInChildren<FreezeMomentOnEnable>(true)) {
            Object.DestroyImmediate(freeze);
        }

        return copyPrefab;
    }

    /// <summary>
    /// Sets up a copy of something of the partner's once it is switched on: the thrower's equipment instead of this
    /// player's, nothing that freezes or shakes this player's screen, writes this player's data or hurts them, and a
    /// pull on enemies only where this game runs them.
    /// </summary>
    /// <param name="copy">The copy.</param>
    /// <param name="poisoned">Whether the thrower has the pouch that poisons their tools.</param>
    public static void PrepareCopy(GameObject copy, bool poisoned) {
        ThrownTool.MarkRemote(copy);

        foreach (var tint in copy.GetComponentsInChildren<PoisonTintBase>(true)) {
            tint.SetPoisoned(poisoned);
        }

        // The game pulls enemies where it runs them, and the room is run in one game only: here, if this player runs
        // it, and otherwise in the other game, where the thrower's own tool is the one that pulls
        var pulls = IsSceneHost?.Invoke() ?? true;
        foreach (var sucker in copy.GetComponentsInChildren<RecoilEnemiesToRadius>(true)) {
            sucker.enabled = pulls;
        }

        foreach (var fsm in copy.GetComponentsInChildren<PlayMakerFSM>(true)) {
            QuietFsm(fsm, poisoned);
        }
    }

    /// <summary>
    /// Switches off what an FSM of a copy would do to this player rather than show, and answers its checks of the
    /// equipment the way of the thrower.
    /// </summary>
    private static void QuietFsm(PlayMakerFSM fsm, bool poisoned) {
        var inner = fsm.Fsm;
        if (inner?.States == null) {
            return;
        }

        var startState = inner.StartState;
        foreach (var state in inner.States) {
            if (state?.Actions == null) {
                continue;
            }

            foreach (var action in state.Actions) {
                switch (action) {
                    case FreezeMoment:
                    case FreezeMomentV2:
                    case DoCameraShake:
                    case DamageHeroDirectly:
                    case SetDamageHeroAmount:
                    case ToolItemStatesLiquidReportBottleBroken:
                        action.Enabled = false;
                        break;
                    case SendEventByName send when send.sendEvent?.Value?.Contains("Shake") == true:
                        action.Enabled = false;
                        break;
                    case CheckIfToolEquipped check when check.Tool?.Value == Gameplay.PoisonPouchTool:
                        AnswerCheck(check, poisoned);
                        break;
                    // The push or spin a thing gives itself as it starts is in the motion that the thrower sent
                    case AddForce2d or AddTorque2d when state.Name == startState:
                        action.Enabled = false;
                        break;
                    default:
                        if (action != null && WritesPlayerData(action)) {
                            action.Enabled = false;
                        }

                        break;
                }
            }
        }
    }

    /// <summary>
    /// Makes a check of the equipment go the way it went for the thrower, whatever this player has on.
    /// </summary>
    private static void AnswerCheck(CheckIfToolEquipped check, bool equipped) {
        if (!CheckEvents.TryGetValue(check, out var events)) {
            events = [check.trueEvent, check.falseEvent];
            CheckEvents.Add(check, events);
        }

        check.trueEvent = equipped ? events[0] : events[1];
        check.falseEvent = equipped ? events[0] : events[1];
    }

    /// <summary>
    /// Whether an FSM action writes the data of the local player.
    /// </summary>
    private static bool WritesPlayerData(FsmStateAction action) {
        var name = action.GetType().Name;
        return name.StartsWith("SetPlayerData", StringComparison.Ordinal) ||
               name.StartsWith("IncrementPlayerData", StringComparison.Ordinal) ||
               name.StartsWith("DecrementPlayerData", StringComparison.Ordinal) ||
               name.StartsWith("AddPlayerData", StringComparison.Ordinal);
    }

    /// <summary>
    /// Breaks a copy the way the game breaks the oldest one of too many of a tool.
    /// </summary>
    public static void BreakCopy(GameObject copy) {
        foreach (var limiter in copy.GetComponentsInChildren<ToolItemLimiter>(true)) {
            try {
                LimiterBreakMethod?.Invoke(limiter, null);
            } catch (Exception e) {
                Logger.Warn($"Could not break the partner's '{copy.name}': {e.Message}");
            }
        }
    }

    #endregion

    #region Setting tools off

    /// <summary>
    /// Whether an attack that touches something set by a tool comes from the other player than the one who threw it,
    /// which in the game could never set it off.
    /// </summary>
    /// <param name="owner">What the attack touches.</param>
    /// <param name="other">The attack.</param>
    public static bool IsForeignAttack(GameObject? owner, Collider2D? other) {
        if (other == null || owner == null || other.gameObject.layer != HeroAttackLayer) {
            return false;
        }

        return IsForeignToolHit(owner, other.gameObject);
    }

    /// <summary>
    /// Whether something that a hit reaches is a tool of one player while the hit comes from the other.
    /// </summary>
    /// <param name="struck">What the hit reaches.</param>
    /// <param name="attacker">What the hit comes from.</param>
    public static bool IsForeignToolHit(GameObject struck, GameObject attacker) {
        var struckIsCopy = RemoteAttackComponent.IsRemoteAttack(struck);
        if (!struckIsCopy && !LocalToolComponent.IsLocalTool(struck)) {
            return false;
        }

        return struckIsCopy != RemoteAttackComponent.IsRemoteAttack(attacker);
    }

    private static void OnFsmTrigger(Action<Fsm, Collider2D> orig, Fsm self, Collider2D other) {
        if (IsForeignAttack(self.GameObject, other)) {
            return;
        }

        orig(self, other);
    }

    private static void OnProxyTrigger(
        Action<PlayMakerProxyBase, Collider2D> orig,
        PlayMakerProxyBase self,
        Collider2D other
    ) {
        if (IsForeignAttack(self.gameObject, other)) {
            return;
        }

        orig(self, other);
    }

    private static void OnCustomStayTrigger(
        Action<CustomPlayMakerTriggerStay2D, Collider2D> orig,
        CustomPlayMakerTriggerStay2D self,
        Collider2D other
    ) {
        if (IsForeignAttack(self.gameObject, other)) {
            return;
        }

        orig(self, other);
    }

    #endregion
}

/// <summary>
/// Marks a copy of a thing that the partner threw, with what it needs from the thrower's game.
/// </summary>
internal class RemoteToolCopy : MonoBehaviour {
    /// <summary>
    /// Whether the thrower has the pouch that poisons their tools.
    /// </summary>
    public bool Poisoned { get; set; }
}
