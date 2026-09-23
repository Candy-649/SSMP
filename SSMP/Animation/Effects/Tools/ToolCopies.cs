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
/// the part: the thrower's game does the hits, and sends what the copy can't work out by itself. For that, a copy has
/// to stay out of this game in every other way too - it must not freeze or shake this player's screen, make a noise
/// that enemies hear, count against this player's own tools or spend them, read this player's equipment, write this
/// player's data, or spawn anything that hits as this player's own. And in the game, only the player who threw a tool
/// can set it off with an attack; here that holds both ways, for the partner's copies and the attacks of this player,
/// and for this player's own tools and the copies of the partner's attacks.
/// </summary>
internal static class ToolCopies {
    /// <summary>
    /// The layer of the attacks of a player, which is how a tool that waits to be set off tells an attack.
    /// </summary>
    private const int HeroAttackLayer = (int) GlobalEnums.PhysLayers.HERO_ATTACK;

    /// <summary>
    /// The binding flags of the instance members that are looked up.
    /// </summary>
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// The binding flags of the static members that are looked up.
    /// </summary>
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// The prefixes of the names of the FSM actions that a copy must not run: they shake or flash the screen or the
    /// controller of this player, freeze this game for a moment, or make a noise that the enemies of this game hear.
    /// </summary>
    private static readonly string[] QuietActionPrefixes = [
        "FreezeMoment",
        "DoCameraShake",
        "SendCameraShake",
        "ScreenFlash",
        "PlayVibration",
        "CreateNoise"
    ];

    /// <summary>
    /// The tool that the hero is about to throw.
    /// </summary>
    private static readonly FieldInfo? WillThrowToolField =
        typeof(HeroController).GetField("willThrowTool", InstanceFlags);

    /// <summary>
    /// The tool that a limiter counts, which it counts against the local player's own number of them.
    /// </summary>
    private static readonly FieldInfo? LimiterToolField =
        typeof(ToolItemLimiter).GetField("representingTool", InstanceFlags);

    /// <summary>
    /// What a damager calls when a hit of it lands on an enemy, which is what its thing reacts to.
    /// </summary>
    private static readonly FieldInfo? DamagedEnemyField =
        typeof(DamageEnemies).GetField("DamagedEnemy", InstanceFlags);

    /// <summary>
    /// The two states of a tool that can be switched between, each with a thing of its own to throw.
    /// </summary>
    private static readonly FieldInfo?[] ToggleStateFields = [
        typeof(ToolItemToggleState).GetField("offState", InstanceFlags),
        typeof(ToolItemToggleState).GetField("onState", InstanceFlags)
    ];

    /// <summary>
    /// The events that checks of the equipment had before a copy was made to answer them the thrower's way.
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
    /// The state machines of the things of the local player's tools that are followed, with the thing, the index of the
    /// state machine in it and the events of it that only this game can know of.
    /// </summary>
    private static readonly Dictionary<Fsm, (TrackedTool Tool, byte Index, HashSet<string> Events)> LocalFsms = new();

    /// <summary>
    /// The damagers of the things of the local player's tools that are watched for landed hits, which the pool keeps
    /// and hands out again.
    /// </summary>
    private static readonly ConditionalWeakTable<DamageEnemies, object> WatchedDamagers = new();

    /// <summary>
    /// The state machines of the copies of the partner's things, with the events that only come from the thrower.
    /// </summary>
    private static readonly Dictionary<Fsm, HashSet<string>> CopyFsms = new();

    /// <summary>
    /// The hooks, which stay for as long as the game runs.
    /// </summary>
    private static readonly List<Hook> Hooks = [];

    /// <summary>
    /// The prefabs of the copied things, by their name, or null until they are needed.
    /// </summary>
    private static Dictionary<string, GameObject>? _prefabs;

    /// <summary>
    /// The names of the prefabs that were looked for and are not there, so that they are not looked for again.
    /// </summary>
    private static readonly HashSet<string> MissingPrefabs = [];

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
    /// The number of the last thing that was followed.
    /// </summary>
    private static byte _lastId;

    /// <summary>
    /// Whether the local player runs the enemies of the room, which is where a copy can pull them.
    /// </summary>
    public static Func<bool>? IsSceneHost { get; set; }

    /// <summary>
    /// Called with each message about a thing of the local player's tools, for the partner.
    /// </summary>
    public static event Action<byte[]>? MessageReady;

    /// <summary>
    /// Puts the hooks in place, once.
    /// </summary>
    public static void Install() {
        if (_installed) {
            return;
        }

        _installed = true;

        AddHook(
            typeof(HeroController).GetMethod("ThrowTool", InstanceFlags, null, [typeof(bool)], null),
            new Action<Action<HeroController, bool>, HeroController, bool>(OnThrowTool)
        );
        AddHook(
            typeof(ObjectPool).GetMethod(
                "Spawn",
                StaticFlags,
                null,
                [typeof(GameObject), typeof(Transform), typeof(Vector3), typeof(Quaternion), typeof(bool)],
                null
            ),
            new Func<SpawnMethod, GameObject, Transform, Vector3, Quaternion, bool, GameObject>(OnSpawn)
        );
        AddHook(
            typeof(Fsm).GetMethod("DoTransition", InstanceFlags, null, [typeof(FsmTransition), typeof(bool)], null),
            new Func<Func<Fsm, FsmTransition, bool, bool>, Fsm, FsmTransition, bool, bool>(OnDoTransition)
        );
        AddHook(
            typeof(ToolBoomerang).GetMethod("Hit", InstanceFlags, null, [typeof(HitInstance)], null),
            new Func<Func<ToolBoomerang, HitInstance, IHitResponder.HitResponse>, ToolBoomerang, HitInstance,
                IHitResponder.HitResponse>(OnClawsHit)
        );
        AddHook(
            typeof(ToolBoomerang).GetMethod("Tinked", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<ToolBoomerang>, ToolBoomerang>(OnClawsTinked)
        );
        AddHook(
            typeof(ToolBoomerang).GetMethod("Break", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<ToolBoomerang>, ToolBoomerang>(OnClawsBreak)
        );
        AddHook(
            typeof(ToolRing).GetMethod("OnCollisionEnter2D", InstanceFlags, null, [typeof(Collision2D)], null),
            new Action<Action<ToolRing, Collision2D>, ToolRing, Collision2D>(OnRingCollision)
        );
        AddHook(
            typeof(ToolRing).GetMethod("OnDamagedEnemy", InstanceFlags, null, [typeof(GameObject)], null),
            new Action<Action<ToolRing, GameObject>, ToolRing, GameObject>(OnRingDamagedEnemy)
        );
        AddHook(
            typeof(ToolRing).GetMethod("TinkBounce", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<ToolRing>, ToolRing>(OnRingTinkBounce)
        );
        AddHook(
            typeof(ToolRing).GetMethod("Break", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<ToolRing>, ToolRing>(OnRingBreak)
        );
        AddHook(
            typeof(Fsm).GetMethod("OnTriggerEnter2D", InstanceFlags, null, [typeof(Collider2D)], null),
            new Action<Action<Fsm, Collider2D>, Fsm, Collider2D>(OnFsmTrigger)
        );
        AddHook(
            typeof(Fsm).GetMethod("OnTriggerStay2D", InstanceFlags, null, [typeof(Collider2D)], null),
            new Action<Action<Fsm, Collider2D>, Fsm, Collider2D>(OnFsmTrigger)
        );
        AddHook(
            typeof(PlayMakerProxyBase).GetMethod(
                "DoTrigger2DEventCallback", InstanceFlags, null, [typeof(Collider2D)], null
            ),
            new Action<Action<PlayMakerProxyBase, Collider2D>, PlayMakerProxyBase, Collider2D>(OnProxyTrigger)
        );
        AddHook(
            typeof(CustomPlayMakerTriggerStay2D).GetMethod(
                "OnTriggerStay2D", InstanceFlags, null, [typeof(Collider2D)], null
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

    #region The local player's tools

    /// <summary>
    /// Catches the thing that the hero throws, for the tools in <see cref="ToolCopyRules.ThrownTools"/>.
    /// </summary>
    private static void OnThrowTool(Action<HeroController, bool> orig, HeroController self, bool isAutoThrow) {
        GameObject? prefab = null;
        try {
            var tool = WillThrowToolField?.GetValue(self) as ToolItem;
            prefab = tool != null && ToolCopyRules.ThrownTools.Contains(tool.name) ? tool.Usage.ThrowPrefab : null;
        } catch (Exception e) {
            Logger.Warn($"Could not see which tool the hero throws: {e.Message}");
        }

        _throwPrefab = prefab;
        _thrown = null;
        try {
            orig(self, isAutoThrow);
        } finally {
            _throwPrefab = null;
        }

        var thrown = _thrown;
        _thrown = null;
        if (thrown == null || prefab == null) {
            return;
        }

        thrown.AddComponentIfNotPresent<LocalToolComponent>();
        Track(thrown, prefab.name);
    }

    /// <summary>
    /// Starts following a thing of a tool of the local player: gives it a number, tells the partner of it once it is on
    /// its way, and from then on sends what the partner's copy can't work out by itself.
    /// </summary>
    /// <param name="thing">The thing.</param>
    /// <param name="prefabName">The name of its prefab.</param>
    private static void Track(GameObject thing, string prefabName) {
        var tracked = thing.AddComponentIfNotPresent<TrackedTool>();

        // The pool can hand out a thing that is still out, when there are too many of it
        tracked.End();
        tracked.Send = message => MessageReady?.Invoke(message);
        tracked.WriteSpawn = () => WriteSpawn(tracked);
        tracked.Ended = Forget;
        tracked.Begin(++_lastId, prefabName);

        var fsms = thing.GetComponentsInChildren<PlayMakerFSM>(true);
        for (var i = 0; i < fsms.Length; i++) {
            var events = ToolCopyRules.GetThrowerEvents(prefabName, fsms[i].FsmName);
            if (events != null && fsms[i].Fsm != null) {
                LocalFsms[fsms[i].Fsm] = (tracked, (byte) i, events);
            }
        }

        foreach (var damager in tracked.Damagers) {
            if (WatchedDamagers.TryGetValue(damager, out _)) {
                continue;
            }

            WatchedDamagers.Add(damager, damager);
            damager.DamagedEnemy += () => OnLocalDamagedEnemy(damager);
        }

        MonoBehaviourUtil.Instance.StartCoroutine(Announce(tracked, tracked.Id));
    }

    /// <summary>
    /// Tells the partner of a thing once it is on its way. Waiting for the next frame lets the thing start first, which
    /// some of them do with a random push or spin of their own, or with the speed that the hero's tool gives them after
    /// spawning them, so the copy takes all of it over as it is.
    /// </summary>
    private static IEnumerator Announce(TrackedTool tracked, byte id) {
        yield return null;

        if (tracked == null || !tracked.Live || tracked.Id != id) {
            yield break;
        }

        try {
            tracked.Announce(WriteSpawn(tracked));
        } catch (Exception e) {
            Logger.Warn($"Could not send the '{tracked.PrefabName}' of the local player to the partner: {e.Message}");
        }
    }

    /// <summary>
    /// Writes the message that tells the partner of a thing as it is now. A thing that its own code moves goes with its
    /// state once it has set itself up, and otherwise sets itself up for the partner too.
    /// </summary>
    private static byte[] WriteSpawn(TrackedTool tracked) {
        var thing = tracked.gameObject;
        var state = ToolCopyRules.GetState(tracked.PrefabName);
        return ToolMessages.WriteSpawn(tracked.Id, new ToolSpawn {
            PrefabName = tracked.PrefabName,
            Poisoned = Gameplay.PoisonPouchTool.IsEquipped,
            Scale = thing.transform.localScale,
            Snapshot = ToolSnapshot.Of(thing),
            Extra = state != null && state.IsSettled(thing) ? state.Write(thing) : []
        });
    }

    /// <summary>
    /// Stops following a thing that is gone.
    /// </summary>
    private static void Forget(TrackedTool tracked) {
        List<Fsm>? gone = null;
        foreach (var pair in LocalFsms) {
            if (pair.Value.Tool == tracked) {
                (gone ??= []).Add(pair.Key);
            }
        }

        if (gone == null) {
            return;
        }

        foreach (var fsm in gone) {
            LocalFsms.Remove(fsm);
        }
    }

    /// <summary>
    /// Sends that a damager of a followed thing landed a hit on an enemy.
    /// </summary>
    private static void OnLocalDamagedEnemy(DamageEnemies damager) {
        if (damager == null) {
            return;
        }

        var tracked = damager.GetComponentInParent<TrackedTool>();
        if (tracked == null || !tracked.Live) {
            return;
        }

        var index = Array.IndexOf(tracked.Damagers, damager);
        if (index >= 0) {
            tracked.Post(ToolMessages.WriteSimple(ToolMessageKind.Hit, tracked.Id, (byte) index));
        }
    }

    /// <summary>
    /// Makes a change of state. A change of a followed thing for a reason that only this game knows of goes to the
    /// partner, and the same change of a copy of the partner's thing is left to the thrower's game, which sends it.
    /// </summary>
    private static bool OnDoTransition(
        Func<Fsm, FsmTransition, bool, bool> orig,
        Fsm self,
        FsmTransition transition,
        bool isGlobal
    ) {
        var eventName = transition?.FsmEvent?.Name;
        if (eventName != null && CopyFsms.TryGetValue(self, out var copyEvents) && copyEvents.Contains(eventName)) {
            return false;
        }

        var changed = orig(self, transition!, isGlobal);
        if (changed && eventName != null && LocalFsms.TryGetValue(self, out var local) &&
            local.Events.Contains(eventName)) {
            local.Tool.QueueStateChange(local.Index, transition!.ToState);
        }

        return changed;
    }

    /// <summary>
    /// Sends the flight of the local player's claws after a hit on them sent them off another way.
    /// </summary>
    private static IHitResponder.HitResponse OnClawsHit(
        Func<ToolBoomerang, HitInstance, IHitResponder.HitResponse> orig,
        ToolBoomerang self,
        HitInstance hit
    ) {
        var response = orig(self, hit);
        if (response.response != IHitResponder.Response.None && self.TryGetComponent<TrackedTool>(out var tracked)) {
            tracked.QueueMotion();
        }

        return response;
    }

    /// <summary>
    /// Knocks claws back off a blocking enemy: the local player's claws, which then send their flight, and never a
    /// copy of the partner's claws, which takes it from the thrower's game.
    /// </summary>
    private static void OnClawsTinked(Action<ToolBoomerang> orig, ToolBoomerang self) {
        if (IsThrowerCopy(self.gameObject)) {
            return;
        }

        orig(self);
        if (self.TryGetComponent<TrackedTool>(out var tracked)) {
            tracked.QueueMotion();
        }
    }

    /// <summary>
    /// Sends that the local player's claws broke, which they may do because they left the local player's view.
    /// </summary>
    private static void OnClawsBreak(Action<ToolBoomerang> orig, ToolBoomerang self) {
        orig(self);
        if (self.TryGetComponent<TrackedTool>(out var tracked)) {
            tracked.Post(ToolMessages.WriteSimple(ToolMessageKind.Break, tracked.Id));
        }
    }

    /// <summary>
    /// Bounces a ring off a wall: the local player's ring, which then sends where it goes, and never a copy of the
    /// partner's ring, which takes every bounce from the thrower's game and meanwhile stops at the wall.
    /// </summary>
    private static void OnRingCollision(Action<ToolRing, Collision2D> orig, ToolRing self, Collision2D collision) {
        if (IsThrowerCopy(self.gameObject)) {
            return;
        }

        orig(self, collision);
        if (self.TryGetComponent<TrackedTool>(out var tracked)) {
            tracked.QueueMotion();
        }
    }

    /// <summary>
    /// Bounces a ring off an enemy it hit, like <see cref="OnRingCollision"/>.
    /// </summary>
    private static void OnRingDamagedEnemy(Action<ToolRing, GameObject> orig, ToolRing self, GameObject enemy) {
        if (IsThrowerCopy(self.gameObject)) {
            return;
        }

        orig(self, enemy);
        if (self.TryGetComponent<TrackedTool>(out var tracked)) {
            tracked.QueueMotion();
        }
    }

    /// <summary>
    /// Bounces a ring back off something that turned it away, like <see cref="OnRingCollision"/>.
    /// </summary>
    private static void OnRingTinkBounce(Action<ToolRing> orig, ToolRing self) {
        if (IsThrowerCopy(self.gameObject)) {
            return;
        }

        orig(self);
        if (self.TryGetComponent<TrackedTool>(out var tracked)) {
            tracked.QueueMotion();
        }
    }

    /// <summary>
    /// Sends that the local player's ring broke.
    /// </summary>
    private static void OnRingBreak(Action<ToolRing> orig, ToolRing self) {
        orig(self);
        if (self.TryGetComponent<TrackedTool>(out var tracked)) {
            tracked.Post(ToolMessages.WriteSimple(ToolMessageKind.Break, tracked.Id));
        }
    }

    /// <summary>
    /// Whether something is part of a copy of a thing of the partner's that the thrower's game keeps up to date.
    /// </summary>
    public static bool IsThrowerCopy(GameObject gameObject) {
        var copy = gameObject.GetComponentInParent<RemoteToolCopy>(true);
        return copy != null && copy.FromThrower;
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
    /// The prefab of a copied thing by its name, from the things that the copied tools throw.
    /// </summary>
    /// <param name="name">The name of the prefab.</param>
    /// <returns>The prefab, or null if there is no copied thing of that name.</returns>
    public static GameObject? FindPrefab(string name) {
        if (_prefabs == null || !_prefabs.ContainsKey(name) && MissingPrefabs.Add(name)) {
            _prefabs = FindPrefabs();
        }

        return _prefabs.TryGetValue(name, out var prefab) ? prefab : null;
    }

    /// <summary>
    /// Finds the prefabs of the copied things: what each copied tool throws, in either of its states for a tool that
    /// can be switched between two.
    /// </summary>
    private static Dictionary<string, GameObject> FindPrefabs() {
        var prefabs = new Dictionary<string, GameObject>();
        foreach (var tool in ToolItemManager.GetAllTools()) {
            if (tool == null || !ToolCopyRules.ThrownTools.Contains(tool.name)) {
                continue;
            }

            AddPrefab(prefabs, tool.Usage.ThrowPrefab);
            if (tool is not ToolItemToggleState) {
                continue;
            }

            foreach (var stateField in ToggleStateFields) {
                var state = stateField?.GetValue(tool);
                var usage = state?.GetType().GetField("Usage", InstanceFlags)?.GetValue(state);
                var throwPrefab = usage?.GetType().GetField("ThrowPrefab", InstanceFlags)?.GetValue(usage);
                AddPrefab(prefabs, throwPrefab as GameObject);
            }
        }

        return prefabs;
    }

    /// <summary>
    /// Adds a prefab by its name, if there is one.
    /// </summary>
    private static void AddPrefab(Dictionary<string, GameObject> prefabs, GameObject? prefab) {
        if (prefab != null) {
            prefabs[prefab.name] = prefab;
        }
    }

    /// <summary>
    /// Gets what the copies of a thrown thing are made from, making it the first time. It stays switched off, so every
    /// copy made from it starts switched off and can be set up before anything in it runs.
    /// </summary>
    /// <param name="prefab">The prefab of the thing.</param>
    public static GameObject GetCopyPrefab(GameObject prefab) {
        if (CopyPrefabs.TryGetValue(prefab, out var copyPrefab) && copyPrefab != null) {
            return copyPrefab;
        }

        copyPrefab = MakeCopyPrefab(prefab);
        copyPrefab.SetActive(false);
        ToolCopyRules.GetState(prefab.name)?.PrepareCopyPrefab(copyPrefab);
        CopyPrefabs[prefab] = copyPrefab;
        return copyPrefab;
    }

    /// <summary>
    /// Makes a copy of a prefab that belongs to the partner: it is marked as theirs, counts against none of the local
    /// player's tools and can't hurt the local player. Taken out of it is what would reach into this game: what breaks
    /// it by this player's view, spends this player's tools, makes a noise, deals its damage by any other way than a
    /// hit, ends the local player's own dust clouds, or lets this player's attacks knock it about. Switched off rather
    /// than taken out is what shakes the screen, since the animation events that call it still find it.
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

        RemoveAll<DamageHero>(copyPrefab);
        RemoveAll<FreezeMomentOnEnable>(copyPrefab);
        RemoveAll<ToolBreakRangeHandler>(copyPrefab);
        RemoveAll<ToolUsageCounter>(copyPrefab);
        RemoveAll<NoiseMaker>(copyPrefab);
        RemoveAll<TagDamager>(copyPrefab);
        RemoveAll<DustpiloExplosion>(copyPrefab);
        RemoveAll<TinkEffect>(copyPrefab);

        foreach (var shaker in copyPrefab.GetComponentsInChildren<CameraControlAnimationEvents>(true)) {
            shaker.enabled = false;
        }

        foreach (var shaker in copyPrefab.GetComponentsInChildren<CameraShakeOnEnable>(true)) {
            shaker.enabled = false;
        }

        foreach (var vibration in copyPrefab.GetComponentsInChildren<VibrationPlayer>(true)) {
            vibration.enabled = false;
        }

        return copyPrefab;
    }

    /// <summary>
    /// Takes every component of a type out of an object and its children.
    /// </summary>
    private static void RemoveAll<T>(GameObject gameObject) where T : Component {
        foreach (var component in gameObject.GetComponentsInChildren<T>(true)) {
            Object.DestroyImmediate(component);
        }
    }

    /// <summary>
    /// Sets up a copy of something of the partner's once it is switched on: the thrower's equipment instead of this
    /// player's, nothing that freezes or shakes this player's screen, makes a noise, writes this player's data or hurts
    /// them, and a pull on enemies only where this game runs them.
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
    /// Leaves the changes of state of a copy that only the thrower's game can know of to the thrower's game.
    /// </summary>
    /// <param name="copy">The copy.</param>
    /// <param name="prefabName">The name of the prefab of the thing it copies.</param>
    /// <param name="marker">The marker of the copy, which keeps what is left to the thrower until it is gone.</param>
    public static void LeaveToThrower(GameObject copy, string prefabName, RemoteToolCopy marker) {
        foreach (var fsm in copy.GetComponentsInChildren<PlayMakerFSM>(true)) {
            var events = ToolCopyRules.GetThrowerEvents(prefabName, fsm.FsmName);
            if (events == null || fsm.Fsm == null) {
                continue;
            }

            CopyFsms[fsm.Fsm] = events;
            marker.Fsms.Add(fsm.Fsm);
        }
    }

    /// <summary>
    /// Forgets the state machines of a copy that is gone.
    /// </summary>
    public static void ForgetCopy(RemoteToolCopy marker) {
        foreach (var fsm in marker.Fsms) {
            CopyFsms.Remove(fsm);
        }

        marker.Fsms.Clear();
    }

    /// <summary>
    /// Has a damager of a copy react to a hit that the thrower's thing landed, as the thing reacts to it: the thing
    /// may slow down, bounce or break.
    /// </summary>
    public static void LandHit(DamageEnemies damager) {
        (DamagedEnemyField?.GetValue(damager) as Action)?.Invoke();
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
                    case null:
                        break;
                    case DamageHeroDirectly:
                    case SetDamageHeroAmount:
                    case ToolItemStatesLiquidReportBottleBroken:
                        action.Enabled = false;
                        break;
                    case SendEventByName send when send.sendEvent?.Value?.Contains("Shake") == true:
                        action.Enabled = false;
                        break;
                    // An explosion in water reports a kill of the creatures in it to the journal of the local player
                    case CallMethodProper call when call.behaviour?.Value == "MaggotRegion":
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
                        if (IsQuieted(action) || WritesPlayerData(action)) {
                            action.Enabled = false;
                        }

                        break;
                }
            }
        }
    }

    /// <summary>
    /// Whether an FSM action shakes or flashes something of this player, freezes this game or makes a noise.
    /// </summary>
    private static bool IsQuieted(FsmStateAction action) {
        var name = action.GetType().Name;
        foreach (var prefix in QuietActionPrefixes) {
            if (name.StartsWith(prefix, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
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
    /// Whether something that a hit reaches is a tool of one player while the hit comes from the other, or a copy that
    /// takes whatever hits it from the thrower's game.
    /// </summary>
    /// <param name="struck">What the hit reaches.</param>
    /// <param name="attacker">What the hit comes from.</param>
    public static bool IsForeignToolHit(GameObject struck, GameObject attacker) {
        if (IsThrowerCopy(struck)) {
            return true;
        }

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
/// Marks a copy of a thing of the partner's, with what it needs from the thrower's game.
/// </summary>
internal class RemoteToolCopy : MonoBehaviour {
    /// <summary>
    /// Whether the thrower has the pouch that poisons their tools.
    /// </summary>
    public bool Poisoned { get; set; }

    /// <summary>
    /// Whether the copy is of a thing that the thrower's game keeps up to date, rather than of something that such a
    /// copy spawned, which runs by itself.
    /// </summary>
    public bool FromThrower { get; set; }

    /// <summary>
    /// The name of the prefab of the thing that the copy is of.
    /// </summary>
    public string PrefabName { get; set; } = "";

    /// <summary>
    /// The state machines of the copy whose changes for reasons that only the thrower's game knows of come from there.
    /// </summary>
    public List<Fsm> Fsms { get; } = [];

    /// <summary>
    /// Called when the copy is gone.
    /// </summary>
    [NonSerialized]
    public Action<RemoteToolCopy>? Destroyed;

    private void OnDestroy() {
        ToolCopies.ForgetCopy(this);
        Destroyed?.Invoke(this);
    }
}
