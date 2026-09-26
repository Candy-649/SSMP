using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using GlobalEnums;
using HutongGames.PlayMaker;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using SSMP.Animation.Effects.Tools;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using SSMP.Game.Client.Entity;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Replays hits between the players of a two-player save. When the local player hits an object of the room, such as a
/// wall that breaks, a lever or a rock that explodes, the game of the partner replays the hit on its own copy of that
/// object, so that both worlds change in the same way. Many objects notice an attack by its touch rather than by its
/// hit, and a touch that such an object answered is replayed the same way. The copies of the partner's attacks in the
/// local game only show the attacks and leave those objects alone, so that nothing is hit twice and nothing is missed.
/// A replayed hit gives the local player nothing: no knockback, no bounce, no silk and no hit pause. What the object
/// drops is dropped in both games, so each player gets their own.
/// Knockback of enemies is local first as well: a hit of the local player knocks back an enemy at once, also when the
/// scene host controls the enemy, and the game of the scene host applies the same knockback when the hit arrives.
/// </summary>
internal class CoopHits {
    /// <summary>
    /// Binding flags for the instance methods that are hooked.
    /// </summary>
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// The kinds of objects of the room that are there for the player who hits them: they fling that player off them,
    /// heal or pay them, or ring or spark against their weapon, where their own game has them. A hit on one is not
    /// sent, and the copy of the partner's attack leaves the one here alone, so each player has their own. Every other
    /// object of the room takes the partner's hits as replays of them (see <see cref="IsRoomObject"/>).
    /// </summary>
    private static readonly Type[] PersonalTypes = [
        typeof(BounceBalloon),
        typeof(BouncePod),
        typeof(CurrencyObjectBase),
        typeof(HealthFlyer),
        typeof(LifebloodPustule),
        typeof(ScuttlerControl),
        typeof(SpikeSlashReaction),
        typeof(TinkEffect)
    ];

    /// <summary>
    /// The kinds of objects whose hits were replayed before those of every other object of the room were. They were
    /// replayed also when the room spawned them as it loaded, which rooms do with some of them, and the other game
    /// finds a spawned one by its place and name like any other. So these count as objects of the room when spawned
    /// too (see <see cref="IsRoomObject"/>).
    /// </summary>
    private static readonly Type[] ReplayedWhenSpawnedTypes = [
        typeof(ActivatorPlatform),
        typeof(Breakable),
        typeof(BreakableHolder),
        typeof(BreakablePole),
        typeof(BreakablePoleSimple),
        typeof(ChainAttackForce),
        typeof(Grass),
        typeof(HealthCocoon),
        typeof(HitResponse),
        typeof(HitRigidbody2D),
        typeof(Lever),
        typeof(Lever_tk2d),
        typeof(RosaryCache),
        typeof(SuspendedPlatformCut)
    ];

    /// <summary>
    /// The types of objects that only take a hit while something is inside a range of theirs, such as the player. The
    /// hit of the partner passed that check in their game, so a replayed hit skips it.
    /// </summary>
    private static readonly Type[] RangeCheckedTypes = [
        typeof(Breakable),
        typeof(Lever),
        typeof(Lever_tk2d),
        typeof(SuspendedPlatformCut)
    ];

    /// <summary>
    /// The components through which the state machines of an object hear of something touching it: coming into it,
    /// staying in it or leaving it. Unity tells each of them, and each passes it on to the state machines of its object
    /// and to the actions elsewhere that listen to it. Many objects of the room notice an attack this way rather than
    /// by its hit, and then only where the attack touched them.
    /// </summary>
    private static readonly Type[] TouchReceiverTypes = [
        typeof(PlayMakerTriggerEnter2D),
        typeof(PlayMakerTriggerStay2D),
        typeof(PlayMakerTriggerExit2D),
        typeof(CustomPlayMakerTriggerStay2D)
    ];

    /// <summary>
    /// Whether objects of a type are there for the player who hits them, for the types that were looked up.
    /// </summary>
    private static readonly Dictionary<Type, bool> IsPersonalByType = new();

    /// <summary>
    /// The state machine that a tink tells of being struck, if it tells one (see <see cref="HitRoomTink"/>).
    /// </summary>
    private static readonly FieldInfo? TinkFsmField = typeof(TinkEffect).GetField("fsm", InstanceFlags);

    /// <summary>
    /// Whether objects of a type count as objects of the room when spawned, for the types that were looked up.
    /// </summary>
    private static readonly Dictionary<Type, bool> IsReplayedWhenSpawnedByType = new();

    /// <summary>
    /// While a hit or touch of the partner is replayed, what each range of the object had inside it in the partner's
    /// game when they struck.
    /// </summary>
    private static readonly Dictionary<TrackTriggerObjects, RangeState> ReplayedRanges = new();

    /// <summary>
    /// How many hits and touches <see cref="Traffic"/> keeps.
    /// </summary>
    private const int TrafficCapacity = 400;

    /// <summary>
    /// The hits and touches on objects of the room that went between the games lately, oldest first, with when, the
    /// scene and path of the object and what happened. <see cref="CoopStateCheck"/> shows them with a difference of the
    /// object that they were for.
    /// </summary>
    private static readonly Queue<(float Time, string Scene, string Path, string Text)> Traffic = new();

    /// <summary>
    /// When a hit or touch of the partner on each object that could not be replayed may next be written down, by the
    /// scene and path of the object, so that swinging at one cannot fill the log.
    /// </summary>
    private static readonly Dictionary<string, float> NextNotReplayedLogTime = new();

    /// <summary>
    /// The shortest time between two lines about a hit or touch on the same object that could not be replayed, in
    /// seconds.
    /// </summary>
    private const float NotReplayedLogInterval = 30f;

    /// <summary>
    /// The longest time that the entity is carried on for when it takes a strike of the partner from where their copy
    /// was struck, in seconds, so that a stalled network cannot throw it across the room.
    /// </summary>
    private const float MaxStrikeCatchUp = 0.5f;

    /// <summary>
    /// The net client for sending hits.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The data of the other players, by ID.
    /// </summary>
    private readonly Dictionary<ushort, ClientPlayerData> _playerData;

    /// <summary>
    /// The game patcher, which keeps replayed hits from knocking back the local player.
    /// </summary>
    private readonly GamePatcher _gamePatcher;

    /// <summary>
    /// The entity manager, which knows the enemies that the scene host controls.
    /// </summary>
    private readonly EntityManager _entityManager;

    /// <summary>
    /// Gets the ID of the partner that the two-player save was checked with, or null outside a checked save.
    /// </summary>
    private readonly Func<ushort?> _getPartnerId;

    /// <summary>
    /// The hooks that are registered while connected to a server.
    /// </summary>
    private readonly List<IDisposable> _hooks = [];

    /// <summary>
    /// The object that replayed hits come from, which is moved to where the attack of the partner was.
    /// </summary>
    private GameObject? _hitSource;

    /// <summary>
    /// The object that replayed hits come from when the attack of the partner moved with a body, like a thrown tool.
    /// </summary>
    private Rigidbody2D? _movingHitSource;

    /// <summary>
    /// The object that replayed touches come from, which takes the name, place and kind of the partner's attack.
    /// </summary>
    private Collider2D? _touchSource;

    /// <summary>
    /// Whether a hit or touch of the partner is being replayed.
    /// </summary>
    private bool _isReplaying;

    /// <summary>
    /// Whose hit is being processed, for the knockback that it causes.
    /// </summary>
    private HitContext _hitContext;

    /// <summary>
    /// Whether sending a hit threw, which is only logged once.
    /// </summary>
    private bool _sendFailed;

    /// <summary>
    /// The state that each state machine was in before it first changed state during a hit or touch of the local
    /// player's attack, which tells whether the object answered it (see <see cref="AnswersAttack"/>).
    /// </summary>
    private readonly Dictionary<Fsm, FsmState?> _statesBefore = new();

    /// <summary>
    /// Whether a hit or touch of the local player's attack is being made, while the states are written down.
    /// </summary>
    private bool _watchingStates;

    /// <summary>
    /// The entity whose copy the local player's hit is landing on, while the hit is made (see <see cref="HitCopy"/>).
    /// </summary>
    private Entity.Entity? _copyBeingHit;

    /// <summary>
    /// What the parts of <see cref="_copyBeingHit"/> told its state machines during the hit: the index of each state
    /// machine and the event, in the order they were told.
    /// </summary>
    private List<(byte FsmIndex, string EventName)> _toldCopy = [];

    /// <summary>
    /// The state machine of the room that a tink of the local player's hit tells of it, while the hit is made (see
    /// <see cref="HitRoomTink"/>).
    /// </summary>
    private PlayMakerFSM? _toldByTink;

    /// <summary>
    /// What the tink told <see cref="_toldByTink"/> during the hit and it answered: each event, with the dice as it was
    /// told.
    /// </summary>
    private readonly List<(string EventName, int[] Dice)> _toldRoom = [];

    public CoopHits(
        NetClient netClient,
        Dictionary<ushort, ClientPlayerData> playerData,
        GamePatcher gamePatcher,
        EntityManager entityManager,
        Func<ushort?> getPartnerId
    ) {
        _netClient = netClient;
        _playerData = playerData;
        _gamePatcher = gamePatcher;
        _entityManager = entityManager;
        _getPartnerId = getPartnerId;
    }

    /// <summary>
    /// Registers the hooks that send, filter and replay hits.
    /// </summary>
    public void RegisterHooks() {
        Entity.Entity.CopyTouchedLocalPlayer += OnCopyTouchedLocalPlayer;

        AddILHook(
            typeof(DamageEnemies).GetMethod("ProcessDamageBuffer", InstanceFlags),
            DamageEnemiesOnProcessDamageBuffer
        );

        foreach (var type in RangeCheckedTypes) {
            AddILHook(
                type.GetMethod(
                    nameof(IHitResponder.Hit), InstanceFlags | BindingFlags.DeclaredOnly, null, [typeof(HitInstance)], null
                ),
                SkipRangeCheckWhileReplaying
            );
        }

        AddHook(
            typeof(HeroController).GetMethod(
                nameof(HeroController.AddSilk),
                InstanceFlags,
                null,
                [typeof(int), typeof(bool), typeof(SilkSpool.SilkAddSource), typeof(bool)],
                null
            ),
            new Action<Action<HeroController, int, bool, SilkSpool.SilkAddSource, bool>, HeroController, int, bool,
                SilkSpool.SilkAddSource, bool>(OnAddSilk)
        );
        AddHook(
            typeof(global::GameManager).GetMethod(
                nameof(global::GameManager.FreezeMoment),
                InstanceFlags,
                null,
                [typeof(FreezeMomentTypes), typeof(Action)],
                null
            ),
            new Action<Action<global::GameManager, FreezeMomentTypes, Action?>, global::GameManager, FreezeMomentTypes,
                Action?>(OnFreezeMoment)
        );
        AddHook(
            typeof(Recoil).GetMethod(
                nameof(Recoil.RecoilByDirection), InstanceFlags, null, [typeof(int), typeof(float)], null
            ),
            new Action<Action<Recoil, int, float>, Recoil, int, float>(OnRecoilByDirection)
        );
        AddHook(
            typeof(TrackTriggerObjects).GetMethod("get_InsideCount", InstanceFlags),
            new Func<Func<TrackTriggerObjects, int>, TrackTriggerObjects, int>(OnInsideCount)
        );
        AddHook(
            typeof(ThreadSpinner).GetMethod("AddSilkDelayed", InstanceFlags),
            new Func<Func<ThreadSpinner, IEnumerator>, ThreadSpinner, IEnumerator>(OnThreadSpinnerAddSilkDelayed)
        );
        AddHook(
            typeof(Fsm).GetMethod("SwitchState", InstanceFlags, null, [typeof(FsmState)], null),
            new Action<Action<Fsm, FsmState>, Fsm, FsmState>(OnSwitchState)
        );
        AddHook(
            typeof(PlayMakerFSM).GetMethod(nameof(PlayMakerFSM.SendEvent), InstanceFlags, null, [typeof(string)], null),
            new Action<Action<PlayMakerFSM, string>, PlayMakerFSM, string>(OnFsmSendEvent)
        );

        // The dice that go with what the local player sets off and come with what the partner did
        foreach (var (method, detour) in SharedDice.GetHooks()) {
            AddHook(method, detour);
        }

        AddHook(
            GetTouchMethod(typeof(PlayMakerTriggerEnter2D), "OnTriggerEnter2D"),
            new Action<Action<PlayMakerTriggerEnter2D, Collider2D>, PlayMakerTriggerEnter2D, Collider2D>(OnTouchEnter)
        );
        AddHook(
            GetTouchMethod(typeof(PlayMakerTriggerStay2D), "OnTriggerStay2D"),
            new Action<Action<PlayMakerTriggerStay2D, Collider2D>, PlayMakerTriggerStay2D, Collider2D>(OnTouchStay)
        );
        AddHook(
            GetTouchMethod(typeof(PlayMakerTriggerExit2D), "OnTriggerExit2D"),
            new Action<Action<PlayMakerTriggerExit2D, Collider2D>, PlayMakerTriggerExit2D, Collider2D>(OnTouchExit)
        );
        AddHook(
            GetTouchMethod(typeof(CustomPlayMakerTriggerStay2D), "OnTriggerStay2D"),
            new Action<Action<CustomPlayMakerTriggerStay2D, Collider2D>, CustomPlayMakerTriggerStay2D, Collider2D>(
                OnCustomTouchStay
            )
        );
        AddHook(
            typeof(HeroController).GetMethod(
                nameof(HeroController.NailHitEnemy),
                InstanceFlags,
                null,
                [typeof(HealthManager), typeof(HitInstance)],
                null
            ),
            new Action<Action<HeroController, HealthManager, HitInstance>, HeroController, HealthManager, HitInstance>(
                OnNailHitEnemy
            )
        );
    }

    /// <summary>
    /// Disposes the hooks and the objects that replayed hits come from.
    /// </summary>
    public void DeregisterHooks() {
        Entity.Entity.CopyTouchedLocalPlayer -= OnCopyTouchedLocalPlayer;

        foreach (var hook in _hooks) {
            hook.Dispose();
        }

        _hooks.Clear();
        _isReplaying = false;
        _hitContext = HitContext.None;
        ReplayedRanges.Clear();

        if (_hitSource != null) {
            Object.Destroy(_hitSource);
        }

        if (_movingHitSource != null) {
            Object.Destroy(_movingHitSource.gameObject);
        }

        if (_touchSource != null) {
            Object.Destroy(_touchSource.gameObject);
        }

        _hitSource = null;
        _movingHitSource = null;
        _touchSource = null;
    }

    /// <summary>
    /// Replays a hit or touch of the partner on the local copy of the object that they hit or touched, or applies the
    /// knockback of their hit on an enemy.
    /// </summary>
    public void OnCoopHitUpdate(CoopHitUpdate update) {
        if (_getPartnerId() != update.PlayerId || !_playerData.TryGetValue(update.PlayerId, out var partner) ||
            !partner.IsInLocalScene) {
            return;
        }

        if (update.Kind == CoopHitKind.EnemyKnockback) {
            ApplyKnockback(update);
            return;
        }

        if (update.Kind == CoopHitKind.EnemyHitEffect) {
            ApplyHitEffect(update);
            return;
        }

        if (update.Kind == CoopHitKind.EnemyBlockEffect) {
            ApplyBlockEffect(update);
            return;
        }

        if (update.Kind == CoopHitKind.ObjectTouch) {
            ReplayTouch(update);
            return;
        }

        if (update.Kind == CoopHitKind.EntityTouch) {
            ApplyEntityTouch(update);
            return;
        }

        if (update.Kind == CoopHitKind.EntityBounce) {
            ApplyEntityBounce(update);
            return;
        }

        if (update.Kind == CoopHitKind.ObjectEvents) {
            ReplayObjectEvents(update);
            return;
        }

        var type = typeof(IHitResponder).Assembly.GetType(update.Responder);
        if (type == null || !typeof(IHitResponder).IsAssignableFrom(type)) {
            NotReplayed(update, "hit", "that kind of object is unknown here");
            return;
        }

        if (IsPersonal(type)) {
            return;
        }

        var target = ScenePath.Find(update.Path, update.Scene);
        if (target == null) {
            NotReplayed(update, "hit", "the object is not here");
            return;
        }

        var components = target.GetComponents(type);
        if (update.Index >= components.Length) {
            NotReplayed(update, "hit", $"the object has only {components.Length} parts of that kind here");
            return;
        }

        var component = components[update.Index];
        if (component is not IHitResponder responder) {
            return;
        }

        if (component is Behaviour { isActiveAndEnabled: false }) {
            NotReplayed(update, "hit", "the object is switched off here");
            return;
        }

        if (!IsRoomObject(component)) {
            NotReplayed(update, "hit", "the object is not a part of the room here");
            return;
        }

        if (!TryReadHit(update.Hit, out var hit, out var source)) {
            Logger.Warn($"Could not read a hit of the partner on {update.Path}");
            return;
        }

        var sourceObject = GetHitSource(source);
        hit.Source = sourceObject;

        var response = default(IHitResponder.HitResponse);
        bool answered;
        _isReplaying = true;
        try {
            // An object that takes a hit only while the player is near goes by where the partner stood in their game
            SetReplayedRanges(target, source.Ranges);
            answered = AnswersAttack(() => _gamePatcher.RunAsRemoteHit(
                () => SharedDice.Throw(source.Dice, () => response = responder.Hit(hit))
            ));
        } catch (Exception e) {
            Logger.Warn($"Could not replay a hit of the partner on {update.Path}:\n{e}");
            NoteTraffic(update.Scene, update.Path, $"got a hit on {update.Responder}, whose replay failed");
            return;
        } finally {
            _isReplaying = false;
            ReplayedRanges.Clear();

            if (_movingHitSource != null) {
                _movingHitSource.linearVelocity = Vector2.zero;
            }
        }

        NoteReplayed(update, "hit", response.response != IHitResponder.Response.None || answered);
    }

    /// <summary>
    /// Creates an IL hook, logging an error instead of throwing if the method does not exist or can't be hooked.
    /// </summary>
    private void AddILHook(MethodInfo? method, ILContext.Manipulator manipulator) {
        if (method == null) {
            Logger.Error($"Could not find the method for {manipulator.Method.Name}; hook was not registered");
            return;
        }

        try {
            _hooks.Add(new ILHook(method, manipulator));
        } catch (Exception e) {
            Logger.Error($"Could not hook {method.DeclaringType?.Name}#{method.Name}:\n{e}");
        }
    }

    /// <summary>
    /// Creates a hook, logging an error instead of throwing if the method does not exist or can't be hooked.
    /// </summary>
    private void AddHook(MethodInfo? method, Delegate detour) {
        if (method == null) {
            Logger.Error($"Could not find the method for {detour.Method.Name}; hook was not registered");
            return;
        }

        try {
            _hooks.Add(new Hook(method, detour));
        } catch (Exception e) {
            Logger.Error($"Could not hook {method.DeclaringType?.Name}#{method.Name}:\n{e}");
        }
    }

    /// <summary>
    /// IL hook that lets <see cref="OnDamagerHit"/> make each hit of an attack on the objects it touched.
    /// </summary>
    private void DamageEnemiesOnProcessDamageBuffer(ILContext il) {
        try {
            var c = new ILCursor(il);

            c.GotoNext(
                MoveType.Before,
                i => i.MatchCallvirt(typeof(IHitResponder), nameof(IHitResponder.Hit))
            );

            // Replace the call of IHitResponder.Hit with a call that also gets the attack
            c.Remove();
            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate<Func<IHitResponder, HitInstance, DamageEnemies, IHitResponder.HitResponse>>(OnDamagerHit);
        } catch (Exception e) {
            Logger.Error($"Could not change DamageEnemies#ProcessDamageBuffer IL:\n{e}");
        }
    }

    /// <summary>
    /// IL hook for the hit method of an object that only takes hits in a range, which lets replayed hits skip the check
    /// of that range.
    /// </summary>
    private void SkipRangeCheckWhileReplaying(ILContext il) {
        try {
            var c = new ILCursor(il);
            var found = false;

            while (c.TryGotoNext(
                       MoveType.Before,
                       i => i.MatchCallvirt(typeof(TrackTriggerObjects), "get_IsInside")
                   )) {
                c.Remove();
                c.EmitDelegate<Func<TrackTriggerObjects, bool>>(trigger => _isReplaying || trigger.IsInside);
                found = true;
            }

            if (!found) {
                Logger.Warn($"Could not find the range check in {il.Method.DeclaringType.Name}#{il.Method.Name}");
            }
        } catch (Exception e) {
            Logger.Error($"Could not change the range check of {il.Method.DeclaringType.Name}#{il.Method.Name}:\n{e}");
        }
    }

    /// <summary>
    /// Hook for <see cref="HeroController.AddSilk(int, bool, SilkSpool.SilkAddSource, bool)"/>, which keeps a replayed
    /// hit, or the copy of a swing of the partner, from giving the local player silk.
    /// </summary>
    private void OnAddSilk(
        Action<HeroController, int, bool, SilkSpool.SilkAddSource, bool> orig,
        HeroController self,
        int amount,
        bool heroEffect,
        SilkSpool.SilkAddSource source,
        bool forceCanBindEffect
    ) {
        // The second of these is what a silk barrier is paid out of. The copy of the partner's swing is a real swing
        // here, and the things it is allowed to touch take its hit for real: that is how the barrier the two of them
        // are cutting through comes down for both of them, which is what it is for. What it must not do is pay. The
        // silk is handed out by the barrier itself, a piece at a time as it is cut, so one player cutting paid both
        // of them - and it cannot be taken out of the barrier, since the barrier here is the same one the player
        // whose swing it really is will cut through themselves.
        if (_isReplaying || _hitContext == HitContext.Remote) {
            return;
        }

        orig(self, amount, heroEffect, source, forceCanBindEffect);
    }

    /// <summary>
    /// Hook for the number of things inside a range of an object, which gives the ranges of an object whose hit or
    /// touch is being replayed the number they had in the game of the partner who struck. An object that takes a hit
    /// only while the player is near counts the player of its own game, and here that is the player who did not strike.
    /// </summary>
    private int OnInsideCount(Func<TrackTriggerObjects, int> orig, TrackTriggerObjects self) {
        return _isReplaying && ReplayedRanges.TryGetValue(self, out var range) ? range.Inside : orig(self);
    }

    /// <summary>
    /// While a hit or touch of the partner is replayed, what an alert range of the object said in their game when they
    /// struck: whether a player was near. What such a range says here goes by who stands where in this game. Asked by
    /// the answer that <see cref="GamePatcher"/> gives for alert ranges.
    /// </summary>
    /// <param name="alertRange">The alert range.</param>
    /// <param name="inRange">What it said in the partner's game.</param>
    /// <returns>Whether the range is one of the object whose hit or touch is being replayed.</returns>
    public static bool TryGetReplayedInRange(AlertRange alertRange, out bool inRange) {
        var replayed = ReplayedRanges.TryGetValue(alertRange, out var range);
        inRange = range.InRange;
        return replayed;
    }

    /// <summary>
    /// Hook for the routine that pays the silk of a spool a moment after it was hit, which keeps a replayed hit of the
    /// partner from paying the local player. The routine pays when the replay is over and <see cref="OnAddSilk"/> can
    /// no longer tell whose hit it was, so it is not started for one at all.
    /// </summary>
    private IEnumerator OnThreadSpinnerAddSilkDelayed(Func<ThreadSpinner, IEnumerator> orig, ThreadSpinner self) {
        return _isReplaying || _hitContext == HitContext.Remote ? Array.Empty<object>().GetEnumerator() : orig(self);
    }

    /// <summary>
    /// Hook for <see cref="HeroController.NailHitEnemy"/>, which keeps a replayed hit, or the copy of a swing of the
    /// partner, from paying the local player for landing it.
    ///
    /// This is what the health of an enemy calls on the player who hit it, and everything it hands out it hands to
    /// <see cref="HeroController.instance"/> - healing on a hit, the time a crest's own state is kept alive for, the
    /// effect that goes with it. In a game with one player that is the only player there is.
    ///
    /// Enemies that both games keep a copy of never reach it from a copy of the partner's swing, because such a copy
    /// is turned away before it touches them at all. Enemies that are not kept in step are a different matter: each
    /// game runs its own, and the copy of the swing is exactly what makes the one here take the same hit as the one
    /// over there. That is wanted. What is not wanted is that it pays for it twice - once over there to the player
    /// who really swung, and once here to the player who did not.
    /// </summary>
    private void OnNailHitEnemy(
        Action<HeroController, HealthManager, HitInstance> orig,
        HeroController self,
        HealthManager target,
        HitInstance hit
    ) {
        if (_isReplaying || _hitContext == HitContext.Remote) {
            return;
        }

        orig(self, target, hit);
    }

    /// <summary>
    /// Hook for <see cref="global::GameManager.FreezeMoment(FreezeMomentTypes, Action)"/>, which keeps a replayed hit
    /// from pausing the game of the local player for a moment.
    /// </summary>
    private void OnFreezeMoment(
        Action<global::GameManager, FreezeMomentTypes, Action?> orig,
        global::GameManager self,
        FreezeMomentTypes type,
        Action? onFinish
    ) {
        if (_isReplaying) {
            onFinish?.Invoke();
            return;
        }

        orig(self, type, onFinish);
    }

    /// <summary>
    /// Makes a hit of an attack on an object. In a checked two-player save, hits of the local player on shared objects
    /// go to the partner, and copies of remote attacks don't hit those objects, because their players send their hits.
    /// </summary>
    /// <param name="responder">The object that is hit.</param>
    /// <param name="hit">The hit.</param>
    /// <param name="damager">The attack that hits the object.</param>
    /// <returns>How the object responded to the hit.</returns>
    private IHitResponder.HitResponse OnDamagerHit(IHitResponder responder, HitInstance hit, DamageEnemies damager) {
        // A thrown tool is set off by the attacks of the player who threw it only, in the game and here: the copies
        // of the partner's attacks leave this player's tools alone, and this player's attacks leave the partner's.
        // That holds in any game with other players, not only in a two-player save.
        if (responder is Component struck && ToolCopies.IsForeignToolHit(struck.gameObject, damager.gameObject)) {
            return IHitResponder.Response.None;
        }

        if (_getPartnerId() is not { } partnerId) {
            return responder.Hit(hit);
        }

        var isRemote = RemoteAttackComponent.IsRemoteAttack(damager.gameObject);

        // A cocoon that a death leaves behind belongs to the player who died and to their save alone: breaking one
        // hands that player back the money it holds and clears what their save keeps of it. Both games spawn one
        // under the same name, so a hit that the partner sends over is found here as this game's own cocoon, and a
        // copy of their attack swung beside it reaches it just as well. Neither may touch it. Hits of this game's own
        // player still land, and stay here.
        if (IsHeroCocoon(responder)) {
            return isRemote ? IHitResponder.Response.None : responder.Hit(hit);
        }

        // An object of the room takes only the hits that really reached it: those of this game's player, and those of
        // the partner as replays of their hits, which arrive through OnCoopHitUpdate. The copy of the partner's attack
        // only shows the attack. It is drawn where this game has the partner, a little off from where they are, so it
        // missed what they hit at the edge of their reach - and an object that counts a hit only while the player is
        // near counted this game's player, who may be anywhere.
        if (responder is Component component && IsRoomObject(component)) {
            if (isRemote) {
                return IHitResponder.Response.None;
            }

            // In a place that each player has to themselves, the local player's hits stay in this game
            if (PersonalPlaces.Contains(component.gameObject)) {
                return responder.Hit(hit);
            }

            // A tink that tells a state machine of the room of being struck is no longer the hitter's alone: the bell
            // that it knocks away fell on in the other game
            if (component is TinkEffect tink && hit.IsHeroDamage &&
                TinkFsmField?.GetValue(tink) is PlayMakerFSM told && told != null) {
                return HitRoomTink(partnerId, tink, told, hit);
            }

            if (IsPersonal(component.GetType())) {
                return responder.Hit(hit);
            }

            // The update is made before the hit, since a hit can break the object and move its parts. An object that
            // lets the attack go on through it says it was not hit, and it still answered when one of its state
            // machines moved on to another state
            var update = hit.IsHeroDamage ? CreateUpdate(partnerId, component, hit) : null;
            var response = default(IHitResponder.HitResponse);
            int[] dice = [];
            var answered = AnswersAttack(() => dice = SharedDice.Record(() => response = responder.Hit(hit))) ||
                           response.response != IHitResponder.Response.None;
            if (update != null && answered && _netClient.IsConnected) {
                update.Hit = SharedDice.Append(update.Hit, dice);
                _netClient.UpdateManager.SetCoopHitUpdate(update);
                NoteTraffic(update.Scene, update.Path, $"sent a hit on {update.Responder}");
            } else if (update != null && !answered) {
                NoteTraffic(update.Scene, update.Path, $"hit its {update.Responder}, which did not answer: not sent");
            }

            return response;
        }

        // An enemy that both games keep a copy of. A copy of the partner's attack doesn't touch it at all: their game
        // works out what their own hit did and sends the result, so a copy of their swing finding its own targets
        // here would be a second hit that never happened. What a copy used to do was take the whole hit and put the
        // health back afterwards, which left behind everything a hit does apart from health - the event it sends to
        // the enemy's own state machine, and the rounding that the co-op damage split carries to the next hit.
        Entity.Entity? enemyEntity = null;
        var isEnemyEntity = responder is Component enemyComponent &&
                            enemyComponent.TryGetComponent<HealthManager>(out _) &&
                            TryGetEntity(enemyComponent.gameObject, out enemyEntity, out _);
        var enemyId = enemyEntity?.Id ?? 0;

        if (isEnemyEntity && isRemote) {
            return IHitResponder.Response.None;
        }

        SayWhatWasHit(responder, hit, isRemote, isEnemyEntity);

        // Where the enemy stood as the hit landed, which is what the direction of a hit that spreads out from its
        // source is worked out against
        var enemyPosition = isEnemyEntity && responder is Component hitComponent
            ? hitComponent.transform.position
            : (Vector3?) null;

        // A part of the copy of something that the scene host runs, like a falling bell, a creature's shield or a flea
        // of a festival game that both players play with the same fleas. What it has for health goes by the hits of
        // enemies instead
        var copied = !isRemote && hit.IsHeroDamage && responder is Component part and not HealthManager
            ? FindCopyHolding(part.gameObject)
            : null;

        // Anything else, like an enemy, takes the hit as usual, and the hook of Recoil knows whose hit it is
        var lastContext = _hitContext;
        _hitContext = isRemote ? HitContext.Remote : hit.IsHeroDamage ? HitContext.Local : HitContext.None;
        try {
            var response = copied != null ? HitCopy(copied, responder, hit) : responder.Hit(hit);

            // Nothing of this hit happens in the partner's game any more, so what it looked like is sent to them. A hit
            // that the enemy blocked showed only the spark of the block here, and that is what goes, not a wound.
            if (isEnemyEntity && hit.IsHeroDamage && response.response != IHitResponder.Response.None) {
                SendHitEffect(
                    partnerId, enemyId, hit, response.response == IHitResponder.Response.Invincible, enemyPosition
                );
            }

            return response;
        } finally {
            _hitContext = lastContext;
        }
    }

    /// <summary>
    /// Hook for the component that tells the state machines of an object of something coming into it (see
    /// <see cref="TouchReceiverTypes"/>). The local player's attack touching an object of the room goes to
    /// <see cref="OnLocalAttackTouch"/>, and the copy of the partner's attack touches nothing there.
    /// </summary>
    private void OnTouchEnter(
        Action<PlayMakerTriggerEnter2D, Collider2D> orig,
        PlayMakerTriggerEnter2D self,
        Collider2D other
    ) {
        if (!IsPlayerAttackOnRoomObject(self, other, out var isLocal)) {
            orig(self, other);
        } else if (isLocal) {
            OnLocalAttackTouch(self, other, () => orig(self, other));
        }
    }

    /// <summary>
    /// Hook for the component that tells the state machines of an object of something staying in it, like
    /// <see cref="OnTouchEnter"/>.
    /// </summary>
    private void OnTouchStay(
        Action<PlayMakerTriggerStay2D, Collider2D> orig,
        PlayMakerTriggerStay2D self,
        Collider2D other
    ) {
        if (!IsPlayerAttackOnRoomObject(self, other, out var isLocal)) {
            orig(self, other);
        } else if (isLocal) {
            OnLocalAttackTouch(self, other, () => orig(self, other));
        }
    }

    /// <summary>
    /// Hook for the component that tells the state machines of an object of something leaving it, like
    /// <see cref="OnTouchEnter"/>.
    /// </summary>
    private void OnTouchExit(
        Action<PlayMakerTriggerExit2D, Collider2D> orig,
        PlayMakerTriggerExit2D self,
        Collider2D other
    ) {
        if (!IsPlayerAttackOnRoomObject(self, other, out var isLocal)) {
            orig(self, other);
        } else if (isLocal) {
            OnLocalAttackTouch(self, other, () => orig(self, other));
        }
    }

    /// <summary>
    /// Hook for the game's own component that tells actions of something staying in an object, like
    /// <see cref="OnTouchEnter"/>.
    /// </summary>
    private void OnCustomTouchStay(
        Action<CustomPlayMakerTriggerStay2D, Collider2D> orig,
        CustomPlayMakerTriggerStay2D self,
        Collider2D other
    ) {
        if (!IsPlayerAttackOnRoomObject(self, other, out var isLocal)) {
            orig(self, other);
        } else if (isLocal) {
            OnLocalAttackTouch(self, other, () => orig(self, other));
        }
    }

    /// <summary>
    /// Gets the method through which Unity tells a component of a touch.
    /// </summary>
    private static MethodInfo? GetTouchMethod(Type type, string name) {
        return type.GetMethod(name, InstanceFlags, null, [typeof(Collider2D)], null);
    }

    /// <summary>
    /// Whether something touching an object is an attack of one of the players and the object one of the room, in a
    /// checked two-player save, and if so whether the attack is the local player's. Every touch in the game comes
    /// through here, so the cheap checks come first. A replayed touch goes straight through.
    /// </summary>
    /// <param name="receiver">The component that tells the object of the touch.</param>
    /// <param name="other">What touches the object.</param>
    /// <param name="isLocal">Whether it is an attack of the local player rather than a copy of the partner's.</param>
    private bool IsPlayerAttackOnRoomObject(Component receiver, Collider2D other, out bool isLocal) {
        isLocal = false;
        if (_isReplaying || other.gameObject.layer != (int) PhysLayers.HERO_ATTACK || _getPartnerId() == null) {
            return false;
        }

        // In a place that each player has to themselves, the local player's touches stay in this game
        var isRemote = RemoteAttackComponent.IsRemoteAttack(other.gameObject);
        isLocal = !isRemote && IsLocalAttack(other);
        return (isRemote || isLocal && !PersonalPlaces.Contains(receiver.gameObject)) && IsRoomObject(receiver);
    }

    /// <summary>
    /// Handles the local player's attack touching an object of the room. A touch that the object answered goes to the
    /// partner, whose game replays it.
    /// </summary>
    /// <param name="receiver">The component that tells the object of the touch.</param>
    /// <param name="attack">The attack.</param>
    /// <param name="touch">Tells the object of the touch.</param>
    private void OnLocalAttackTouch(Component receiver, Collider2D attack, Action touch) {
        // The update is made before the touch, since a touch can break the object and move its parts
        var update = _getPartnerId() is { } partnerId ? CreateTouchUpdate(partnerId, receiver, attack) : null;
        int[] dice = [];
        if (AnswersAttack(() => dice = SharedDice.Record(touch)) && update != null && _netClient.IsConnected) {
            update.Hit = SharedDice.Append(update.Hit, dice);
            _netClient.UpdateManager.SetCoopHitUpdate(update);
            NoteTraffic(update.Scene, update.Path, $"sent a touch of {attack.name} through {update.Responder}");
        }
    }

    /// <summary>
    /// Whether an attack is the local player's own: a part of their character, a thing of a tool they threw or set, or
    /// an attack that a skill of theirs left in the world. The game knows the last by the attack itself, which says
    /// that the player is its source, and that is also what makes a hit of it the player's (see
    /// <see cref="HitInstance.IsHeroDamage"/>).
    /// </summary>
    private static bool IsLocalAttack(Collider2D attack) {
        var hero = HeroController.SilentInstance;
        if (hero != null && attack.transform.IsChildOf(hero.transform) ||
            LocalToolComponent.IsLocalTool(attack.gameObject)) {
            return true;
        }

        var damager = attack.GetComponentInParent<DamageEnemies>();
        return damager != null && (damager.isHeroDamage || damager.sourceIsHero);
    }

    /// <summary>
    /// Hook for a state machine changing state, which writes down the state it was in the first time it changes
    /// during a hit or touch of the local player's attack (see <see cref="AnswersAttack"/>).
    /// </summary>
    private void OnSwitchState(Action<Fsm, FsmState> orig, Fsm self, FsmState toState) {
        if (_watchingStates && !_statesBefore.ContainsKey(self)) {
            _statesBefore[self] = self.ActiveState;
        }

        orig(self, toState);
    }

    /// <summary>
    /// Makes a hit or touch on an object of the room, and says whether the object answered it: whether a state machine
    /// that changed state on it ended up in another state than before. An object that looks at an attack and turns it
    /// down goes straight back to where it was, like a rock struck from too far off or a wall struck from its wrong
    /// side. Such an attack of the local player is not sent: replayed, it would be looked at again, and a check that
    /// goes by something of the partner's game, like where their player stands, could let it through. A replay of the
    /// partner's that is not answered is written down (see <see cref="NoteReplayed"/>).
    /// </summary>
    private bool AnswersAttack(Action attack) {
        _statesBefore.Clear();
        _watchingStates = true;
        try {
            attack();
        } finally {
            _watchingStates = false;
        }

        var answered = false;
        foreach (var pair in _statesBefore) {
            if (pair.Key.ActiveState != pair.Value) {
                answered = true;
                break;
            }
        }

        _statesBefore.Clear();
        return answered;
    }

    /// <summary>
    /// When each thing that is neither an enemy nor one of the replayed kinds may next say that it was hit, by its
    /// name, so that swinging at grass cannot fill the log.
    /// </summary>
    private static readonly Dictionary<string, float> NextHitLogTime = new();

    /// <summary>
    /// The shortest time between two lines about hitting the same thing, in seconds.
    /// </summary>
    private const float HitLogInterval = 1f;

    /// <summary>
    /// Writes down a hit on something that is neither an enemy both games keep nor one of the kinds whose hits are
    /// carried over - which is everything else in a room that answers a nail.
    ///
    /// For the things that can be hit alone and cannot be hit together. Such an object is not turned away anywhere
    /// here and the hit is handed straight to it, so if it never reacts, either the hit never reached this at all -
    /// and the swing is being stopped somewhere before it - or it reached it and the object refused it for a reason
    /// of its own. Those are very different faults and nothing currently tells them apart.
    /// </summary>
    /// <param name="responder">What is being hit.</param>
    /// <param name="hit">The hit.</param>
    /// <param name="isRemote">Whether this is a copy of the partner's swing.</param>
    /// <param name="isEnemyEntity">Whether it is an enemy both games keep a copy of.</param>
    private static void SayWhatWasHit(IHitResponder responder, HitInstance hit, bool isRemote, bool isEnemyEntity) {
        if (isEnemyEntity || responder is not Component component) {
            return;
        }

        try {
            var name = component.gameObject.name;
            if (NextHitLogTime.TryGetValue(name, out var next) && Time.unscaledTime < next) {
                return;
            }

            NextHitLogTime[name] = Time.unscaledTime + HitLogInterval;

            Logger.Info(
                $"A hit reached '{name}' ({component.GetType().Name}) for {hit.DamageDealt} damage, " +
                $"{(isRemote ? "from a copy of the partner's swing" : "from this player")}"
            );
        } catch (Exception e) {
            Logger.Warn($"Could not say what was hit: {e.Message}");
        }
    }

    /// <summary>
    /// Hook for <see cref="Recoil.RecoilByDirection"/>, which makes the knockback of enemies local first in a checked
    /// two-player save. A hit of the local player knocks back an enemy at once, also a copy of an enemy that the scene
    /// host controls, and the knockback goes to the partner, whose game applies it if it is the scene host. Copies of
    /// attacks of the partner don't knock back enemies, since the partner's game sends its knockback.
    /// </summary>
    private void OnRecoilByDirection(Action<Recoil, int, float> orig, Recoil self, int direction, float magnitude) {
        if (_hitContext == HitContext.None || _getPartnerId() is not { } partnerId ||
            !TryGetEntity(self.gameObject, out var entity, out var isClientCopy)) {
            orig(self, direction, magnitude);
            return;
        }

        if (_hitContext == HitContext.Remote) {
            return;
        }

        // Read before the knockback, because what it reads afterwards is the whole of how this tells whether the
        // knockback happened at all
        var recoilBefore = self.recoilTimeRemaining;

        orig(self, direction, magnitude);

        if (!isClientCopy) {
            return;
        }

        // The number goes on the knockback and comes back on the positions of the scene host, which is the whole of
        // how the enemy stops being pulled back to where it was hit: until that number comes back, every position
        // from over there was measured before the hit had arrived, and after it they all have the hit in them.
        //
        // Only taken when the knockback really did move the enemy here. The method above has six ways of doing
        // nothing at all - the enemy already recoiling harder, one that holds it still where it stands, and each of
        // the four directions being blocked, which is what a downward knockback into the ground is - and there is
        // nothing to protect from the scene host's positions when nothing happened. Waiting anyway would be worse
        // than the fault this is here to fix: the local knockback has not moved the enemy, the interpolation is held
        // off while a number is outstanding, and the enemy would stand perfectly still for a whole round trip.
        //
        // The clock it runs on is what says so, rather than whether it is recoiling. Asking whether it is recoiling
        // cannot tell a knockback that just landed from one still running from the swing before it, and in the
        // middle of a combo that is the usual state - so every refused hit of a combo opened a wait of its own for
        // a knockback that never happened. The clock is only ever wound back up by a knockback that was taken, and
        // nothing winds it down between here and there.
        var id = self.recoilTimeRemaining > recoilBefore && self.recoilSpeed > 0f
            ? entity.BeginAnticipation()
            : (byte) 0;

        // Sent whatever happened here, since the scene host may be able to knock the enemy back when this game
        // could not. A number of zero is one that nothing is waiting on, and the scene host answers it with nothing.
        if (!SendKnockback(partnerId, entity.Id, direction, magnitude, id) && id != 0) {
            entity.EndAnticipation();
        }
    }

    /// <summary>
    /// Finds the entity whose host or client object is the given object.
    /// </summary>
    /// <param name="gameObject">The object.</param>
    /// <param name="found">The entity.</param>
    /// <param name="isClientCopy">Whether the object is the client object, which the scene host controls.</param>
    /// <returns>Whether the object belongs to an entity.</returns>
    private bool TryGetEntity(
        GameObject gameObject,
        [NotNullWhen(true)] out Entity.Entity? found,
        out bool isClientCopy
    ) {
        found = null;
        isClientCopy = false;

        try {
            foreach (var entity in _entityManager.ActiveEntities) {
                if (entity.Object.Client == gameObject) {
                    found = entity;
                    isClientCopy = true;
                    return true;
                }

                if (entity.Object.Host == gameObject) {
                    found = entity;
                    return true;
                }
            }
        } catch (InvalidOperationException) {
            // The entities changed while looking, which only happens when a hit spawns one
        }

        return false;
    }

    /// <summary>
    /// Finds the entity with the given ID.
    /// </summary>
    /// <param name="entityId">The ID of the entity.</param>
    /// <returns>The entity, or null if no entity in the scene has that ID.</returns>
    private Entity.Entity? FindEntity(ushort entityId) {
        foreach (var entity in _entityManager.ActiveEntities) {
            if (entity.Id == entityId) {
                return entity;
            }
        }

        return null;
    }

    /// <summary>
    /// Sends the knockback of a hit of the local player on an enemy that the scene host controls to the partner.
    /// </summary>
    /// <param name="partnerId">The ID of the partner.</param>
    /// <param name="entityId">The ID of the entity that was knocked back.</param>
    /// <param name="direction">The way it was knocked.</param>
    /// <param name="magnitude">How hard it was knocked.</param>
    /// <param name="id">The number to send it under, which comes back stamped on the positions that have it.</param>
    /// <returns>Whether it was sent, which is whether there is anyone there to make it happen.</returns>
    private bool SendKnockback(ushort partnerId, ushort entityId, int direction, float magnitude, byte id) {
        if (!_netClient.IsConnected || !_playerData.TryGetValue(partnerId, out var partner) ||
            !partner.IsInLocalScene) {
            return false;
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(direction);
        writer.Write(magnitude);
        writer.Write(id);
        writer.Flush();

        _netClient.UpdateManager.SetCoopHitUpdate(new CoopHitUpdate {
            TargetId = partnerId,
            Kind = CoopHitKind.EnemyKnockback,
            EntityId = entityId,
            Hit = stream.ToArray()
        });

        return true;
    }

    /// <summary>
    /// Applies the knockback of a hit of the partner on an enemy, if the local game is the scene host and so controls
    /// the enemy.
    /// </summary>
    private void ApplyKnockback(CoopHitUpdate update) {
        int direction;
        float magnitude;
        byte id;
        try {
            using var reader = new BinaryReader(new MemoryStream(update.Hit));
            direction = reader.ReadInt32();
            magnitude = reader.ReadSingle();
            id = reader.ReadByte();
        } catch (IOException) {
            Logger.Warn($"Could not read the knockback of a hit of the partner on entity {update.EntityId}");
            return;
        }

        if (!_entityManager.IsSceneHost || FindEntity(update.EntityId) is not { } entity) {
            return;
        }

        // Every way out from here notes the number, the ways that do nothing most of all. The game that sent this
        // is holding the positions of that enemy back until this number comes back to it, and a knockback nobody
        // applied is the one case where it would wait for something that is never coming - so it is sent back with
        // the next position, which is where the enemy really is and always was.
        var enemy = entity.Object.Host;
        if (enemy == null || !enemy.activeInHierarchy || !enemy.TryGetComponent<Recoil>(out var recoil) ||
            enemy.TryGetComponent<HealthManager>(out var healthManager) && healthManager.GetIsDead()) {
            entity.NoteAnticipation(id);
            return;
        }

        recoil.RecoilByDirection(direction, magnitude);

        // What is left on the knockback's clock is how much longer this game goes on carrying the enemy along, and
        // the player waiting on this is told nothing until then. They have already watched their own copy go the
        // whole way; a position from the first moment of the same knockback would only pull it back to the start.
        entity.NoteAnticipation(id, recoil.recoilTimeRemaining);
    }

    /// <summary>
    /// Finds the entity whose copy on show in this game is the given object or holds it as a part.
    /// </summary>
    /// <param name="part">The object.</param>
    /// <returns>The entity, or null if the object is no part of a copy.</returns>
    private Entity.Entity? FindCopyHolding(GameObject part) {
        for (var transform = part.transform; transform != null; transform = transform.parent) {
            if (TryGetEntity(transform.gameObject, out var entity, out var isClientCopy)) {
                return isClientCopy ? entity : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Makes a hit of the local player on a part of the copy of something that the scene host runs, and sends the
    /// scene host what the hit made the part tell the copy's state machines. The copy runs none of them, and a tink
    /// tells them without a word to anyone else: a player whose game showed the copy struck a bell falling at them and
    /// it fell on, while the same hit in the scene host's game knocked it away. The spark and the recoil of the hit
    /// are this game's own and happen here as usual; the copy moves as the scene host's game then moves the thing.
    /// </summary>
    /// <param name="copied">The entity whose copy is hit.</param>
    /// <param name="responder">The part that is hit.</param>
    /// <param name="hit">The hit.</param>
    /// <returns>How the part responded to the hit.</returns>
    private IHitResponder.HitResponse HitCopy(Entity.Entity copied, IHitResponder responder, HitInstance hit) {
        var (lastCopy, lastTold) = (_copyBeingHit, _toldCopy);
        List<(byte FsmIndex, string EventName)> told = [];
        (_copyBeingHit, _toldCopy) = (copied, told);

        IHitResponder.HitResponse response;
        try {
            response = responder.Hit(hit);
        } finally {
            (_copyBeingHit, _toldCopy) = (lastCopy, lastTold);
        }

        // What only changes how the thing moves is played here at once, and the scene host takes it from where the
        // copy was struck. Only when the scene host is there to take it: played here alone, the copy would fly off by
        // itself and wait for an answer that never comes.
        var canSend = CanSendEntityTouch();
        foreach (var (fsmIndex, eventName) in told) {
            var strike = canSend ? copied.PlayStrikeHere(fsmIndex, eventName) : null;

            // What is juggled flies off along a way that its state machine rolls, which is played here at once too
            if (strike == null && canSend && copied.PlayBounceHere(fsmIndex, eventName) is { } bounce) {
                Logger.Info(
                    $"The local player struck the copy of entity {copied.Id}, which took '{eventName}' here at once " +
                    $"and went to '{bounce.State}', and the scene host is sent it with where it was struck"
                );
                SendEntityBounce(copied.Id, fsmIndex, eventName, bounce);
                continue;
            }

            if (strike is { } start) {
                Logger.Info(
                    $"The local player struck the copy of entity {copied.Id}, which took '{eventName}' here at once " +
                    $"from its '{start.FromState}', and the scene host is sent it with where it was struck"
                );
            } else {
                Logger.Info(
                    $"The local player struck the copy of entity {copied.Id}, so the scene host is sent '{eventName}'"
                );
            }

            SendEntityTouch(copied.Id, fsmIndex, eventName, strike);
        }

        return response;
    }

    /// <summary>
    /// Hook for <see cref="PlayMakerFSM.SendEvent(string)"/>. While the local player's hit lands on a copy (see
    /// <see cref="HitCopy"/>), an event for one of the copy's own state machines is written down to go to the scene
    /// host rather than told: the copy's state machines stay where the scene host says they are. While a tink of the
    /// local player's hit tells a state machine of the room (see <see cref="HitRoomTink"/>), what it tells is told and
    /// written down with what it rolled, to go to the partner.
    /// </summary>
    private void OnFsmSendEvent(Action<PlayMakerFSM, string> orig, PlayMakerFSM self, string eventName) {
        if (_copyBeingHit is { } copied) {
            var fsmIndex = copied.ClientFsms.IndexOf(self);
            if (fsmIndex >= 0) {
                _toldCopy.Add(((byte) fsmIndex, eventName));
                return;
            }
        }

        // Told here as usual, and written down with the dice it rolled, for the partner's game to be told the same with
        // the same dice. An event that leads nowhere from where the state machine is does nothing. What the state
        // machine tells itself on the way is not the tink's: over there it follows from this again by itself.
        if (ReferenceEquals(self, _toldByTink) && Answers(self, eventName)) {
            var told = _toldByTink;
            _toldByTink = null;
            try {
                _toldRoom.Add((eventName, SharedDice.Record(() => orig(self, eventName))));
            } finally {
                _toldByTink = told;
            }

            return;
        }

        orig(self, eventName);
    }

    /// <summary>
    /// Whether an event leads a state machine anywhere from the state it is in.
    /// </summary>
    private static bool Answers(PlayMakerFSM fsm, string eventName) {
        var state = fsm.Fsm.ActiveState;
        return state != null && Array.Exists(state.Transitions, transition => transition.EventName == eventName) ||
               Array.Exists(fsm.FsmGlobalTransitions, transition => transition.EventName == eventName);
    }

    /// <summary>
    /// Makes a hit of the local player on a tink of the room that tells a state machine of being struck, and sends
    /// the partner what it told the state machine. A tink was left to the player who struck it, since most only spark
    /// and fling that player off, but some move the world: a bell that a player struck upwards flew off in their game
    /// and fell on in the other. The partner's game tells its state machine the same, with the dice this game had as it
    /// told it, so that the bell flies off at the same angle there (see <see cref="SharedDice"/>). The spark and the
    /// recoil stay here.
    /// </summary>
    /// <param name="partnerId">The ID of the partner.</param>
    /// <param name="tink">The tink that is struck.</param>
    /// <param name="told">The state machine that it tells.</param>
    /// <param name="hit">The hit.</param>
    /// <returns>How the tink responded to the hit.</returns>
    private IHitResponder.HitResponse HitRoomTink(
        ushort partnerId,
        TinkEffect tink,
        PlayMakerFSM told,
        HitInstance hit
    ) {
        _toldByTink = told;
        _toldRoom.Clear();

        IHitResponder.HitResponse response;
        try {
            response = tink.Hit(hit);
        } finally {
            _toldByTink = null;
        }

        try {
            if (_toldRoom.Count > 0 && CreateEventsUpdate(partnerId, told, _toldRoom) is { } update &&
                _netClient.IsConnected) {
                _netClient.UpdateManager.SetCoopHitUpdate(update);
                NoteTraffic(
                    update.Scene, update.Path, $"sent {_toldRoom.Count} event(s) of a tink to {update.Responder}"
                );
            }
        } finally {
            _toldRoom.Clear();
        }

        return response;
    }

    /// <summary>
    /// Sends the partner an event that a state machine of the room was told here, with the dice this game had as it
    /// was told, for their game to tell its own state machine the same (see <see cref="ReplayObjectEvents"/>): what the
    /// director of a game of the festival has the room do, which the scene host's game says for both.
    /// </summary>
    /// <param name="fsm">The state machine.</param>
    /// <param name="eventName">The event.</param>
    /// <param name="dice">The dice.</param>
    public void SendObjectEvent(PlayMakerFSM fsm, string eventName, int[] dice) {
        if (_getPartnerId() is not { } partnerId || !_netClient.IsConnected ||
            CreateEventsUpdate(partnerId, fsm, [(eventName, dice)]) is not { } update) {
            return;
        }

        _netClient.UpdateManager.SetCoopHitUpdate(update);
        NoteTraffic(update.Scene, update.Path, $"sent '{eventName}' to {update.Responder}");
    }

    /// <summary>
    /// Creates the update that sends the events that a state machine of the room was told here to the partner.
    /// </summary>
    /// <returns>The update, or null if the partner isn't in the scene or the object can't be found by others.</returns>
    private CoopHitUpdate? CreateEventsUpdate(
        ushort partnerId,
        PlayMakerFSM fsm,
        List<(string EventName, int[] Dice)> events
    ) {
        if (!_playerData.TryGetValue(partnerId, out var partner) || !partner.IsInLocalScene) {
            return null;
        }

        var target = fsm.gameObject;
        if (!target.scene.IsValid() || target.scene.name == "DontDestroyOnLoad" || !IsRoomObject(fsm)) {
            return null;
        }

        var sameName = Array.FindAll(target.GetComponents<PlayMakerFSM>(), other => other.FsmName == fsm.FsmName);
        var index = Array.IndexOf(sameName, fsm);
        if (index is < 0 or > byte.MaxValue) {
            return null;
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte) events.Count);
        foreach (var (eventName, dice) in events) {
            writer.Write(eventName);
            SharedDice.Write(writer, dice);
        }

        writer.Flush();

        return new CoopHitUpdate {
            TargetId = partnerId,
            Kind = CoopHitKind.ObjectEvents,
            Scene = target.scene.name,
            Path = ScenePath.Get(target.transform),
            Responder = fsm.FsmName,
            Index = (byte) index,
            Hit = stream.ToArray()
        };
    }

    /// <summary>
    /// Tells a state machine of the room the events that it was told in the partner's game, each with the dice their
    /// game had as it was told: by a tink of the partner's attack, or by the director of a game of the festival.
    /// </summary>
    private void ReplayObjectEvents(CoopHitUpdate update) {
        var target = ScenePath.Find(update.Path, update.Scene);
        if (target == null) {
            NotReplayed(update, "events", "the object is not here");
            return;
        }

        var fsms = Array.FindAll(target.GetComponents<PlayMakerFSM>(), fsm => fsm.FsmName == update.Responder);
        if (update.Index >= fsms.Length) {
            NotReplayed(update, "events", $"the object has only {fsms.Length} state machines of that name here");
            return;
        }

        var told = fsms[update.Index];
        if (!told.isActiveAndEnabled || !IsRoomObject(told)) {
            NotReplayed(update, "events", "the state machine is switched off or not a part of the room here");
            return;
        }

        List<(string EventName, int[] Dice)> events = [];
        try {
            using var reader = new BinaryReader(new MemoryStream(update.Hit));
            var count = reader.ReadByte();
            for (var i = 0; i < count; i++) {
                var eventName = reader.ReadString();
                events.Add((eventName, SharedDice.Read(reader) ?? []));
            }
        } catch (IOException) {
            Logger.Warn($"Could not read the events the partner's game told {update.Path}");
            return;
        }

        var answered = false;
        _isReplaying = true;
        try {
            _gamePatcher.RunAsRemoteHit(() => {
                foreach (var (eventName, dice) in events) {
                    answered |= Answers(told, eventName);
                    SharedDice.Throw(dice, () => told.SendEvent(eventName));
                }
            });
        } catch (Exception e) {
            Logger.Warn($"Could not replay the events the partner's game told {update.Path}:\n{e}");
            return;
        } finally {
            _isReplaying = false;
        }

        NoteReplayed(update, "events", answered);
    }

    /// <summary>
    /// Tells the partner that the copy of an entity touched the local player, for the partner's game to send the entity
    /// the event the copy has already played here (see <see cref="Entity.Entity.ListenForTouches"/>), or that the
    /// local player's hit made a part of the copy tell its state machines an event (see <see cref="HitCopy"/>).
    /// </summary>
    /// <param name="entityId">The ID of the entity.</param>
    /// <param name="fsmIndex">The index of the FSM of the entity that the event is for.</param>
    /// <param name="eventName">The event.</param>
    private void OnCopyTouchedLocalPlayer(ushort entityId, byte fsmIndex, string eventName) {
        SendEntityTouch(entityId, fsmIndex, eventName, null);
    }

    /// <summary>
    /// Sends the scene host the event that the copy of an entity was touched or struck with, and for a strike that was
    /// played on the copy at once, how the copy stood and moved as it was struck, with this game's round trip to the
    /// server (see <see cref="Entity.Entity.PlayStrikeHere"/>).
    /// </summary>
    /// <param name="entityId">The ID of the entity.</param>
    /// <param name="fsmIndex">The index of the FSM of the entity that the event is for.</param>
    /// <param name="eventName">The event.</param>
    /// <param name="strike">How the copy stood and moved as it was struck, or null.</param>
    private void SendEntityTouch(ushort entityId, byte fsmIndex, string eventName, Entity.StrikeStart? strike) {
        if (!CanSendEntityTouch() || _getPartnerId() is not { } partnerId) {
            return;
        }

        byte[] data = [];
        if (strike is { } start) {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(start.FromState);
            writer.Write(start.Position.x);
            writer.Write(start.Position.y);
            writer.Write(start.Velocity.x);
            writer.Write(start.Velocity.y);
            writer.Write(start.Angle);
            writer.Write(start.Spin);
            writer.Write(start.Number);
            writer.Write((ushort) Mathf.Clamp(_netClient.UpdateManager.AverageRtt, 0, ushort.MaxValue));
            writer.Flush();
            data = stream.ToArray();
        }

        _netClient.UpdateManager.SetCoopHitUpdate(new CoopHitUpdate {
            TargetId = partnerId,
            Kind = CoopHitKind.EntityTouch,
            EntityId = entityId,
            Index = fsmIndex,
            Responder = eventName,
            Hit = data
        });
    }

    /// <summary>
    /// Sends the scene host a strike of the local player on the copy of something juggled that the copy's FSM played
    /// at once (see <see cref="Entity.Entity.PlayBounceHere"/>), with where the copy was struck, the dice it rolled and
    /// this game's round trip to the server.
    /// </summary>
    /// <param name="entityId">The ID of the entity.</param>
    /// <param name="fsmIndex">The index of the FSM of the entity that the event is for.</param>
    /// <param name="eventName">The event.</param>
    /// <param name="bounce">Where the copy was struck and the dice.</param>
    private void SendEntityBounce(ushort entityId, byte fsmIndex, string eventName, Entity.BounceStart bounce) {
        if (_getPartnerId() is not { } partnerId) {
            return;
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(bounce.State);
        writer.Write(bounce.Position.x);
        writer.Write(bounce.Position.y);
        writer.Write(bounce.Anticipation);
        writer.Write((ushort) Mathf.Clamp(_netClient.UpdateManager.AverageRtt, 0, ushort.MaxValue));
        SharedDice.Write(writer, bounce.Dice);
        writer.Flush();

        _netClient.UpdateManager.SetCoopHitUpdate(new CoopHitUpdate {
            TargetId = partnerId,
            Kind = CoopHitKind.EntityBounce,
            EntityId = entityId,
            Index = fsmIndex,
            Responder = eventName,
            Hit = stream.ToArray()
        });
    }

    /// <summary>
    /// Takes a strike of the partner on the copy of something juggled that their game played at once, if this game is
    /// the scene host and so runs it (see <see cref="Entity.Entity.TakeBounce"/>).
    /// </summary>
    /// <param name="update">The update of the partner's strike.</param>
    private void ApplyEntityBounce(CoopHitUpdate update) {
        if (!_entityManager.IsSceneHost || FindEntity(update.EntityId) is not { } entity) {
            return;
        }

        Entity.BounceStart start;
        int partnerRtt;
        try {
            using var reader = new BinaryReader(new MemoryStream(update.Hit));
            var state = reader.ReadString();
            var position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            var anticipation = reader.ReadByte();
            partnerRtt = reader.ReadUInt16();
            start = new Entity.BounceStart(state, position, SharedDice.Read(reader) ?? [], anticipation);
        } catch (IOException e) {
            Logger.Warn($"Could not read the partner's strike on the copy of entity {update.EntityId}: {e.Message}");
            return;
        }

        var elapsed = Mathf.Min((partnerRtt + _netClient.UpdateManager.AverageRtt) / 1000f, MaxStrikeCatchUp);
        Logger.Info(
            entity.TakeBounce(update.Index, update.Responder, start, elapsed)
                ? $"The partner struck the copy of entity {update.EntityId}, so it is sent '{update.Responder}' " +
                  $"from where they struck it with their dice, and went to '{start.State}' too, {elapsed:0.00} s on"
                : $"The partner struck the copy of entity {update.EntityId}, which went to '{start.State}' there, " +
                  $"but '{update.Responder}' did not take it there here"
        );
    }

    /// <summary>
    /// Whether the partner is there to be sent a touch or strike of the copy of an entity: connected and in this room.
    /// </summary>
    private bool CanSendEntityTouch() {
        return _netClient.IsConnected && _getPartnerId() is { } partnerId &&
               _playerData.TryGetValue(partnerId, out var partner) && partner.IsInLocalScene;
    }

    /// <summary>
    /// Sends an entity the event that the partner's copy of it was touched or struck with, if this game is the scene
    /// host and so runs it. An FSM that has already moved on from where the event leads anywhere takes no notice of it.
    /// </summary>
    /// <param name="update">The update of the partner's touch.</param>
    private void ApplyEntityTouch(CoopHitUpdate update) {
        if (!_entityManager.IsSceneHost || FindEntity(update.EntityId) is not { } entity ||
            update.Index >= entity.HostFsms.Count || entity.HostFsms[update.Index] is not { } fsm) {
            return;
        }

        // A strike that the partner's game played on its copy at once is taken from where the copy was struck, and
        // carried on for the time it took to come: the partner's copy went by this game's entity as it was one way
        // along, and the strike came back the other way. The partner hears back that it was taken either way.
        if (TryReadStrike(update.Hit, out var start, out var partnerRtt)) {
            var stateName = fsm.ActiveStateName;
            var elapsed = Mathf.Min((partnerRtt + _netClient.UpdateManager.AverageRtt) / 1000f, MaxStrikeCatchUp);
            Logger.Info(
                entity.TakeStrike(update.Index, update.Responder, start, elapsed)
                    ? $"The partner struck the copy of entity {update.EntityId} in its '{stateName}', so it is sent " +
                      $"'{update.Responder}' from where they struck it, {elapsed:0.00} s on"
                    : $"The partner struck the copy of entity {update.EntityId} in its '{start.FromState}', but it " +
                      $"is in '{stateName}' here, so it is sent '{update.Responder}' from where it is"
            );
            return;
        }

        Logger.Info(
            $"The partner touched or struck the copy of entity {update.EntityId} in its '{fsm.ActiveStateName}', so " +
            $"it is sent '{update.Responder}' here too"
        );
        fsm.SendEvent(update.Responder);
    }

    /// <summary>
    /// Sends what a hit of the local player on an enemy looked like to the partner, whose copy of the attack no
    /// longer hits that enemy itself.
    /// </summary>
    /// <param name="partnerId">The ID of the partner.</param>
    /// <param name="entityId">The ID of the entity that was hit.</param>
    /// <param name="hit">The hit, which decides which effect is played.</param>
    /// <param name="blocked">Whether the enemy blocked the hit rather than took it.</param>
    /// <param name="enemyPosition">Where the enemy stood as the hit landed.</param>
    private void SendHitEffect(
        ushort partnerId,
        ushort entityId,
        HitInstance hit,
        bool blocked,
        Vector3? enemyPosition
    ) {
        if (!_netClient.IsConnected || !_playerData.TryGetValue(partnerId, out var partner) ||
            !partner.IsInLocalScene) {
            return;
        }

        _netClient.UpdateManager.SetCoopHitUpdate(new CoopHitUpdate {
            TargetId = partnerId,
            Kind = blocked ? CoopHitKind.EnemyBlockEffect : CoopHitKind.EnemyHitEffect,
            EntityId = entityId,
            Hit = WriteHit(hit, hit.CircleDirection ? enemyPosition : null)
        });
    }

    /// <summary>
    /// Plays what a hit of the partner on an enemy looked like, without the hit itself. The enemy's own health comes
    /// from whichever game controls it, so all that is left to do here is show that it was hit.
    /// </summary>
    /// <param name="update">The update of the partner's hit.</param>
    private void ApplyHitEffect(CoopHitUpdate update) {
        if (FindHitEnemy(update.EntityId) is not { } healthManager) {
            return;
        }

        var receiver = healthManager.hitEffectReceiver;
        if (receiver == null) {
            return;
        }

        if (!TryReadHit(update.Hit, out var hit, out var source)) {
            Logger.Warn($"Could not read the hit effect of the partner on entity {update.EntityId}");
            return;
        }

        // The game leaves this one without an effect itself
        if (hit.AttackType == AttackTypes.RuinsWater) {
            return;
        }

        PlaceAroundEnemy(ref source, healthManager.transform);
        hit.Source = GetHitSource(source);
        receiver.ReceiveHitEffect(hit);
    }

    /// <summary>
    /// Plays what a hit of the partner looked like that an enemy blocked: the spark where it struck and the clink,
    /// placed the way the enemy's own blocked hit places them. The rest of a blocked hit - the enemy's state machine
    /// hearing of it, the knockback of the player who struck and the shake of their screen - belongs to the game where
    /// it happened, and is left out.
    /// </summary>
    /// <param name="update">The update of the partner's hit.</param>
    private void ApplyBlockEffect(CoopHitUpdate update) {
        if (FindHitEnemy(update.EntityId) is not { } healthManager) {
            return;
        }

        // The game plays one block at a time for an enemy, and none at all for the ones that never clink
        if (healthManager.tinkTimer > 0f || healthManager.GetComponent<DontClinkGates>() != null) {
            return;
        }

        if (!TryReadHit(update.Hit, out var hit, out var source)) {
            Logger.Warn($"Could not read the blocked hit of the partner on entity {update.EntityId}");
            return;
        }

        PlaceAroundEnemy(ref source, healthManager.transform);

        healthManager.tinkTimer = 0.1f;
        if (healthManager.PreventInvincibleEffect || healthManager.blockHitPrefab == null) {
            return;
        }

        hit.Source = GetHitSource(source);
        var enemy = healthManager.transform;
        var direction = DirectionUtils.GetCardinalDirection(hit.GetActualDirection(enemy, default));
        GetBlockEffectPlace(healthManager, hit.Source.transform.position, direction, out var position, out var angle);

        var spark = healthManager.blockHitPrefab.Spawn();
        spark.transform.position = position;
        spark.transform.eulerAngles = new Vector3(0f, 0f, angle);

        if (!healthManager.hasAlternateInvincibleSound) {
            healthManager.regularInvincibleAudio.SpawnAndPlayOneShot(healthManager.audioPlayerPrefab, enemy.position);
        } else if (healthManager.alternateInvincibleSound != null &&
                   healthManager.TryGetComponent<AudioSource>(out var audioSource)) {
            audioSource.PlayOneShot(healthManager.alternateInvincibleSound);
        }
    }

    /// <summary>
    /// Where the spark of a blocked hit goes and which way it faces, as the game works it out: on the side of the
    /// enemy's box that the hit came from, level with where it came from.
    /// </summary>
    private static void GetBlockEffectPlace(
        HealthManager healthManager,
        Vector3 source,
        int direction,
        out Vector2 position,
        out float angle
    ) {
        var enemy = healthManager.transform.position;
        position = enemy;
        angle = 0f;

        var box = healthManager.boxCollider != null
            ? healthManager.boxCollider
            : healthManager.GetComponent<BoxCollider2D>();
        if (box == null) {
            return;
        }

        var left = enemy.x + box.offset.x - box.size.x * 0.5f;
        var right = enemy.x + box.offset.x + box.size.x * 0.5f;
        var bottom = enemy.y + box.offset.y - box.size.y * 0.5f;
        var top = enemy.y + box.offset.y + box.size.y * 0.5f;
        switch (direction) {
            case 0:
                position = new Vector2(left, source.y);
                break;
            case 1:
                position = new Vector2(source.x, Mathf.Max(source.y, bottom));
                angle = 90f;
                break;
            case 2:
                position = new Vector2(right, source.y);
                angle = 180f;
                break;
            case 3:
                position = new Vector2(Mathf.Clamp(source.x, left, right), Mathf.Min(source.y, top));
                angle = 270f;
                break;
        }
    }

    /// <summary>
    /// The health of the enemy that a hit effect of the partner is about, or null when it is gone or dead here.
    /// </summary>
    /// <param name="entityId">The ID of the entity of the enemy.</param>
    private HealthManager? FindHitEnemy(ushort entityId) {
        GameObject? enemy = null;
        foreach (var entity in _entityManager.ActiveEntities) {
            if (entity.Id == entityId) {
                // The scene host watches its own object, and everyone else the copy that follows it
                enemy = _entityManager.IsSceneHost ? entity.Object.Host : entity.Object.Client;
                break;
            }
        }

        if (enemy == null || !enemy.activeInHierarchy ||
            !enemy.TryGetComponent<HealthManager>(out var healthManager) || healthManager.GetIsDead()) {
            return null;
        }

        return healthManager;
    }

    /// <summary>
    /// Creates the update that sends a hit of the local player to the partner.
    /// </summary>
    /// <returns>The update, or null if the partner isn't in the scene or the object can't be found by others.</returns>
    private CoopHitUpdate? CreateUpdate(ushort partnerId, Component component, HitInstance hit) {
        try {
            if (!_playerData.TryGetValue(partnerId, out var partner) || !partner.IsInLocalScene) {
                return null;
            }

            var target = component.gameObject;
            if (!target.scene.IsValid() || target.scene.name == "DontDestroyOnLoad") {
                return null;
            }

            var type = component.GetType();
            var index = Array.IndexOf(target.GetComponents(type), component);
            if (index is < 0 or > byte.MaxValue || type.FullName == null) {
                return null;
            }

            return new CoopHitUpdate {
                TargetId = partnerId,
                Scene = target.scene.name,
                Path = ScenePath.Get(target.transform),
                Responder = type.FullName,
                Index = (byte) index,
                Hit = WriteHit(hit, null, target)
            };
        } catch (Exception e) {
            if (!_sendFailed) {
                _sendFailed = true;
                Logger.Error($"Could not send a hit to the partner:\n{e}");
            }

            return null;
        }
    }

    /// <summary>
    /// Creates the update that sends a touch of the local player's attack on an object of the room to the partner.
    /// </summary>
    /// <returns>The update, or null if the partner isn't in the scene or the object can't be found by others.</returns>
    private CoopHitUpdate? CreateTouchUpdate(ushort partnerId, Component receiver, Collider2D attack) {
        try {
            if (!_playerData.TryGetValue(partnerId, out var partner) || !partner.IsInLocalScene) {
                return null;
            }

            var target = receiver.gameObject;
            var type = receiver.GetType();
            var index = Array.IndexOf(target.GetComponents(type), receiver);
            if (index is < 0 or > byte.MaxValue || type.FullName == null) {
                return null;
            }

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            var position = attack.transform.position;
            writer.Write(position.x);
            writer.Write(position.y);
            writer.Write(position.z);
            writer.Write(attack.name);
            writer.Write(attack.tag);
            writer.Write(attack.gameObject.layer);
            WriteRanges(writer, target);
            writer.Flush();

            return new CoopHitUpdate {
                TargetId = partnerId,
                Kind = CoopHitKind.ObjectTouch,
                Scene = target.scene.name,
                Path = ScenePath.Get(target.transform),
                Responder = type.FullName,
                Index = (byte) index,
                Hit = stream.ToArray()
            };
        } catch (Exception e) {
            if (!_sendFailed) {
                _sendFailed = true;
                Logger.Error($"Could not send a touch to the partner:\n{e}");
            }

            return null;
        }
    }

    /// <summary>
    /// Replays a touch of the partner's attack on the local copy of the object of the room that it touched, through
    /// the same component that told the object of it in their game.
    /// </summary>
    private void ReplayTouch(CoopHitUpdate update) {
        var type = Array.Find(TouchReceiverTypes, touchType => touchType.FullName == update.Responder);
        if (type == null) {
            NotReplayed(update, "touch", "that kind of receiver is unknown here");
            return;
        }

        var target = ScenePath.Find(update.Path, update.Scene);
        if (target == null) {
            NotReplayed(update, "touch", "the object is not here");
            return;
        }

        if (!target.activeInHierarchy) {
            NotReplayed(update, "touch", "the object is switched off here");
            return;
        }

        var receivers = target.GetComponents(type);
        if (update.Index >= receivers.Length) {
            NotReplayed(update, "touch", $"the object has only {receivers.Length} receivers of that kind here");
            return;
        }

        var receiver = receivers[update.Index];
        if (!IsRoomObject(receiver)) {
            NotReplayed(update, "touch", "the object is not a part of the room here");
            return;
        }

        if (!TryReadTouch(update.Hit, out var touch)) {
            Logger.Warn($"Could not read a touch of the partner on {update.Path}");
            return;
        }

        var attack = GetTouchSource(touch);

        bool answered;
        _isReplaying = true;
        try {
            SetReplayedRanges(target, touch.Ranges);
            answered = AnswersAttack(() => _gamePatcher.RunAsRemoteHit(
                () => SharedDice.Throw(touch.Dice, () => TellOfTouch(receiver, attack))
            ));
        } catch (Exception e) {
            Logger.Warn($"Could not replay a touch of the partner on {update.Path}:\n{e}");
            NoteTraffic(update.Scene, update.Path, $"got a touch through {update.Responder}, whose replay failed");
            return;
        } finally {
            _isReplaying = false;
            ReplayedRanges.Clear();
        }

        NoteReplayed(update, "touch", answered);
    }

    /// <summary>
    /// Notes a hit or touch of the partner that was replayed, and whether the object answered it here. The partner's
    /// game sends only what was answered there, so one that is not answered here means that the object here is not
    /// where it is there, or that a check of it came out another way. That is not written down at once: both players
    /// cutting the same grass does it all the time. <see cref="CoopStateCheck"/> shows it with a difference that lasts.
    /// </summary>
    private static void NoteReplayed(CoopHitUpdate update, string what, bool answered) {
        NoteTraffic(
            update.Scene,
            update.Path,
            answered
                ? $"got a {what} through {update.Responder}, replayed"
                : $"got a {what} through {update.Responder}, replayed, but the object did not answer it here"
        );
    }

    /// <summary>
    /// Notes a hit or touch of the partner that could not be replayed, and writes it down, once in a while for each
    /// object.
    /// </summary>
    private static void NotReplayed(CoopHitUpdate update, string what, string reason) {
        NoteTraffic(update.Scene, update.Path, $"got a {what} through {update.Responder}, not replayed: {reason}");

        var key = update.Scene + "/" + update.Path;
        if (NextNotReplayedLogTime.TryGetValue(key, out var next) && Time.unscaledTime < next) {
            return;
        }

        NextNotReplayedLogTime[key] = Time.unscaledTime + NotReplayedLogInterval;
        Logger.Info(
            $"[Hits] The partner's {what} on {update.Path} in {update.Scene} through {update.Responder}: {reason}"
        );
    }

    /// <summary>
    /// Notes a hit or touch on an object of the room that went between the games.
    /// </summary>
    private static void NoteTraffic(string scene, string path, string text) {
        if (Traffic.Count >= TrafficCapacity) {
            Traffic.Dequeue();
        }

        Traffic.Enqueue((Time.unscaledTime, scene, path, text));
    }

    /// <summary>
    /// The hits and touches that went between the games since the given time for an object, one of its parents or one
    /// of its children, as text with how long ago each was, oldest first.
    /// </summary>
    /// <param name="scene">The scene of the object.</param>
    /// <param name="path">The path of the object in its scene.</param>
    /// <param name="since">The unscaled time to go back to.</param>
    internal static List<string> GetTraffic(string scene, string path, float since) {
        var lines = new List<string>();
        var now = Time.unscaledTime;
        foreach (var (time, trafficScene, trafficPath, text) in Traffic) {
            if (time < since || trafficScene != scene || !IsSameOrRelated(path, trafficPath)) {
                continue;
            }

            var ago = (now - time).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            lines.Add(trafficPath == path ? $"{ago} s ago: {text}" : $"{ago} s ago: {text} (on {trafficPath})");
        }

        return lines;
    }

    /// <summary>
    /// Whether two paths are of the same object, or one is of a parent of the other.
    /// </summary>
    private static bool IsSameOrRelated(string path, string other) {
        return path == other || other.StartsWith(path + "/", StringComparison.Ordinal) ||
               path.StartsWith(other + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Tells a component that hears of touches of an attack touching its object, as Unity does.
    /// </summary>
    private static void TellOfTouch(Component receiver, Collider2D attack) {
        switch (receiver) {
            case PlayMakerTriggerEnter2D enter:
                enter.OnTriggerEnter2D(attack);
                break;
            case PlayMakerTriggerStay2D stay:
                stay.OnTriggerStay2D(attack);
                break;
            case PlayMakerTriggerExit2D exit:
                exit.OnTriggerExit2D(attack);
                break;
            case CustomPlayMakerTriggerStay2D customStay:
                customStay.OnTriggerStay2D(attack);
                break;
        }
    }

    /// <summary>
    /// Gets the object that replayed touches come from and gives it the name, place and kind of the partner's attack,
    /// which is what the actions that listen for a touch look at.
    /// </summary>
    private Collider2D GetTouchSource(TouchSourceState state) {
        if (_touchSource == null) {
            // Switched off, since it only stands for the partner's attack and must not touch anything itself
            _touchSource = CreateHitSource("Coop Touch Source").AddComponent<BoxCollider2D>();
            _touchSource.isTrigger = true;
            _touchSource.enabled = false;
        }

        var source = _touchSource.gameObject;
        source.name = state.Name;
        source.transform.position = state.Position;
        source.layer = state.Layer is >= 0 and < 32 ? state.Layer : (int) PhysLayers.HERO_ATTACK;

        try {
            source.tag = state.Tag;
        } catch (UnityException) {
            source.tag = "Untagged";
        }

        return _touchSource;
    }

    /// <summary>
    /// Reads how the partner's copy of an entity stood and moved as they struck it, which
    /// <see cref="SendEntityTouch"/> wrote, with the partner's round trip to the server in milliseconds.
    /// </summary>
    /// <returns>Whether there was a strike to read.</returns>
    private static bool TryReadStrike(byte[] data, out Entity.StrikeStart start, out int roundTrip) {
        start = default;
        roundTrip = 0;
        if (data.Length == 0) {
            return false;
        }

        try {
            using var reader = new BinaryReader(new MemoryStream(data));
            var fromState = reader.ReadString();
            var position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            var velocity = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            var angle = reader.ReadSingle();
            var spin = reader.ReadSingle();
            var number = reader.ReadByte();
            roundTrip = reader.ReadUInt16();

            start = new Entity.StrikeStart(fromState, position, velocity, angle, spin, number);
            return true;
        } catch (IOException) {
            return false;
        }
    }

    /// <summary>
    /// Reads a touch that <see cref="CreateTouchUpdate"/> wrote.
    /// </summary>
    /// <returns>Whether the touch could be read.</returns>
    private static bool TryReadTouch(byte[] data, out TouchSourceState touch) {
        touch = default;

        try {
            using var reader = new BinaryReader(new MemoryStream(data));
            touch.Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            touch.Name = reader.ReadString();
            touch.Tag = reader.ReadString();
            touch.Layer = reader.ReadInt32();
            touch.Ranges = ReadRanges(reader);

            // What the touch rolled in the game of the player who struck, after the touch
            touch.Dice = SharedDice.Read(reader);
            return true;
        } catch (IOException) {
            return false;
        }
    }

    /// <summary>
    /// Gets the object that replayed hits come from and gives it the state of the object that the hit came from.
    /// </summary>
    private GameObject GetHitSource(HitSourceState state) {
        GameObject source;
        if (state.HasBody) {
            if (_movingHitSource == null) {
                var moving = CreateHitSource("Coop Moving Hit Source");
                _movingHitSource = moving.AddComponent<Rigidbody2D>();
                _movingHitSource.bodyType = RigidbodyType2D.Kinematic;
            }

            source = _movingHitSource.gameObject;
            _movingHitSource.linearVelocity = state.Velocity;
        } else {
            if (_hitSource == null) {
                _hitSource = CreateHitSource("Coop Hit Source");
            }

            source = _hitSource;
        }

        source.transform.position = state.Position;
        source.layer = state.Layer is >= 0 and < 32 ? state.Layer : (int) PhysLayers.HERO_ATTACK;

        try {
            source.tag = state.Tag;
        } catch (UnityException) {
            source.tag = "Untagged";
        }

        return source;
    }

    /// <summary>
    /// Creates an object that replayed hits come from, marked as a remote attack.
    /// </summary>
    private static GameObject CreateHitSource(string name) {
        var source = new GameObject(name);
        source.AddComponent<RemoteAttackComponent>();
        Object.DontDestroyOnLoad(source);
        return source;
    }

    /// <summary>
    /// The name of the cocoon that a death leaves behind, taken once from the prefab that the game spawns it from,
    /// or null while it has not been found yet.
    /// </summary>
    private static string? _heroCocoonName;

    /// <summary>
    /// Whether the object that was hit is a cocoon left behind by a death. The name comes from the prefab the game
    /// spawns rather than a name written out here, so it holds in every language the game runs in.
    /// </summary>
    /// <param name="responder">The object that is hit.</param>
    /// <returns>true if the object is a cocoon left behind by a death; otherwise false.</returns>
    private static bool IsHeroCocoon(IHitResponder responder) {
        if (responder is not Component component) {
            return false;
        }

        if (_heroCocoonName == null) {
            var gameManager = global::GameManager.instance;
            var sceneManager = gameManager == null ? null : gameManager.GetSceneManager();
            var prefab = sceneManager == null
                ? null
                : sceneManager.GetComponent<CustomSceneManager>()?.heroCorpsePrefab;
            if (prefab == null) {
                return false;
            }

            _heroCocoonName = prefab.name;
        }

        return component.gameObject.name.StartsWith(_heroCocoonName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether an object is a part of the room itself, which both games have in the same place: in a scene rather than
    /// kept across scenes, placed there rather than spawned, and neither a creature nor a part of one, since enemy
    /// sync takes care of creatures. The kinds that were replayed before the others count when spawned too (see
    /// <see cref="ReplayedWhenSpawnedTypes"/>).
    /// </summary>
    internal static bool IsRoomObject(Component component) {
        var scene = component.gameObject.scene;
        if (!scene.IsValid() || scene.name == "DontDestroyOnLoad" ||
            component.GetComponentInParent<HealthManager>(true) != null) {
            return false;
        }

        var countsWhenSpawned = IsOfType(
            component.GetType(), ReplayedWhenSpawnedTypes, IsReplayedWhenSpawnedByType
        );
        for (var current = component.transform; current != null; current = current.parent) {
            if (!countsWhenSpawned && current.name.Contains("(Clone)") ||
                EntityProcessor.IsRegistered(current.gameObject)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether objects of the given type are there for the player who hits them (see <see cref="PersonalTypes"/>).
    /// </summary>
    internal static bool IsPersonal(Type type) {
        return IsOfType(type, PersonalTypes, IsPersonalByType);
    }

    /// <summary>
    /// Whether a type is one of the given types or derives from one, remembered per type.
    /// </summary>
    private static bool IsOfType(Type type, Type[] types, Dictionary<Type, bool> known) {
        if (!known.TryGetValue(type, out var isOfType)) {
            isOfType = Array.Exists(types, candidate => candidate.IsAssignableFrom(type));
            known[type] = isOfType;
        }

        return isOfType;
    }

    /// <summary>
    /// Writes what the ranges of an object have inside them, in the order they are found under it: how many of what
    /// each counts, and for an alert range whether it says that a player is near. An object that takes a hit only
    /// while the player is near goes by these. A range of a creature under the object is left out: asking it whether a
    /// player is near picks whom the creature goes after, and the creature is kept in step by its own sync.
    /// </summary>
    private static void WriteRanges(BinaryWriter writer, GameObject? target) {
        var ranges = target != null
            ? target.GetComponentsInChildren<TrackTriggerObjects>(true)
            : Array.Empty<TrackTriggerObjects>();
        var count = Mathf.Min(ranges.Length, byte.MaxValue);
        writer.Write((byte) count);
        for (var i = 0; i < count; i++) {
            var isCreatures = IsCreatureRange(ranges[i]);
            writer.Write((byte) (isCreatures ? 0 : Mathf.Min(ranges[i].InsideCount, byte.MaxValue)));
            writer.Write(!isCreatures && ranges[i] is AlertRange alertRange && alertRange.IsHeroInRange());
        }
    }

    /// <summary>
    /// Reads what <see cref="WriteRanges"/> wrote.
    /// </summary>
    private static RangeState[] ReadRanges(BinaryReader reader) {
        var ranges = new RangeState[reader.ReadByte()];
        for (var i = 0; i < ranges.Length; i++) {
            ranges[i].Inside = reader.ReadByte();
            ranges[i].InRange = reader.ReadBoolean();
        }

        return ranges;
    }

    /// <summary>
    /// Gives the ranges of an object whose hit or touch is replayed what they had inside them in the partner's game,
    /// until the replay is over. What the object checks later, after waiting a moment, goes by this game.
    /// </summary>
    private static void SetReplayedRanges(GameObject target, RangeState[] states) {
        var ranges = target.GetComponentsInChildren<TrackTriggerObjects>(true);
        for (var i = 0; i < ranges.Length && i < states.Length; i++) {
            if (!IsCreatureRange(ranges[i])) {
                ReplayedRanges[ranges[i]] = states[i];
            }
        }
    }

    /// <summary>
    /// Whether a range belongs to a creature: whether something with health is above it.
    /// </summary>
    private static bool IsCreatureRange(TrackTriggerObjects range) {
        return range.GetComponentInParent<HealthManager>(true) != null;
    }

    /// <summary>
    /// Gets the body that the game takes the direction of a hit from when the hit goes the way its source moves: the
    /// body of the source or else of its parent.
    /// </summary>
    private static Rigidbody2D? GetSourceBody(GameObject? source) {
        if (source == null) {
            return null;
        }

        var body = source.GetComponent<Rigidbody2D>();
        if (body == null && source.transform.parent != null) {
            body = source.transform.parent.GetComponent<Rigidbody2D>();
        }

        return body;
    }

    /// <summary>
    /// Writes a hit to bytes, with the state of the object that it came from.
    /// </summary>
    /// <param name="hit">The hit.</param>
    /// <param name="enemyPosition">For a hit that goes the way from its source to the enemy, where the enemy stood.
    /// </param>
    /// <param name="rangesOf">For a hit on an object of the room, the object, whose ranges go with the hit.</param>
    private static byte[] WriteHit(HitInstance hit, Vector3? enemyPosition = null, GameObject? rangesOf = null) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        var source = hit.Source;
        var position = source != null ? source.transform.position : Vector3.zero;
        writer.Write(position.x);
        writer.Write(position.y);
        writer.Write(position.z);
        writer.Write(source != null ? source.tag : "Untagged");
        writer.Write(source != null ? source.layer : (int) PhysLayers.HERO_ATTACK);

        var body = hit.MoveDirection ? GetSourceBody(source) : null;
        writer.Write(body != null);
        writer.Write(body != null ? body.linearVelocity.x : 0f);
        writer.Write(body != null ? body.linearVelocity.y : 0f);

        writer.Write(hit.IsFirstHit);
        writer.Write((int) hit.AttackType);
        writer.Write((int) hit.NailElement);
        writer.Write(hit.IsUsingNeedleDamageMult);
        writer.Write(hit.RepresentingTool != null ? hit.RepresentingTool.name : "");
        writer.Write(hit.PoisonDamageTicks);
        writer.Write(hit.ZapDamageTicks);
        writer.Write(hit.DamageScalingLevel);
        writer.Write((int) hit.ToolDamageFlags);
        writer.Write(hit.CircleDirection);
        writer.Write(hit.DamageDealt);
        writer.Write(hit.StunDamage);
        writer.Write(hit.CanWeakHit);
        writer.Write(hit.Direction);
        writer.Write(hit.UseCorpseDirection);
        writer.Write(hit.CorpseDirection);
        writer.Write(hit.CanTriggerBouncePod);
        writer.Write(hit.UseBouncePodDirection);
        writer.Write(hit.BouncePodDirection);
        writer.Write(hit.ExtraUpDirection.HasValue);
        writer.Write(hit.ExtraUpDirection ?? 0f);
        writer.Write(hit.IgnoreInvulnerable);
        writer.Write(hit.MagnitudeMultiplier);
        writer.Write(hit.UseCorpseMagnitudeMult);
        writer.Write(hit.CorpseMagnitudeMultiplier);
        writer.Write(hit.UseCurrencyMagnitudeMult);
        writer.Write(hit.CurrencyMagnitudeMult);
        writer.Write(hit.MoveAngle);
        writer.Write(hit.MoveDirection);
        writer.Write(hit.Multiplier);
        writer.Write((int) hit.SpecialType);
        writer.Write((int) hit.HitEffectsType);
        writer.Write(hit.NonLethal);
        writer.Write(hit.RageHit);
        writer.Write(hit.CriticalHit);
        writer.Write(hit.HunterCombo);
        writer.Write(hit.IsManualTrigger);
        writer.Write(hit.ForceNotWeakHit);
        writer.Write(hit.IsHeroDamage);
        writer.Write(hit.IsNailTag);
        writer.Write(hit.IgnoreNailPosition);
        writer.Write(hit.IsHarpoon);
        writer.Write(enemyPosition.HasValue);
        writer.Write(enemyPosition?.x ?? 0f);
        writer.Write(enemyPosition?.y ?? 0f);
        writer.Write(enemyPosition?.z ?? 0f);
        WriteRanges(writer, rangesOf);

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Puts the source of a hit that spreads out from it where it was from the enemy in the partner's game, rather than
    /// where it was in the world. Such a hit goes the way from its source to the enemy, and the enemy never stands in
    /// quite the same spot in both games: a burst that pulls enemies onto itself has the source almost inside the
    /// enemy, where the smallest difference turns the spray of the wound around - up out of the head here while it
    /// went sideways over there.
    /// </summary>
    /// <param name="source">The state of the source that was read, whose position is moved.</param>
    /// <param name="enemy">The enemy here.</param>
    private static void PlaceAroundEnemy(ref HitSourceState source, Transform enemy) {
        if (source.EnemyPosition is { } enemyThere) {
            source.Position = enemy.position + (source.Position - enemyThere);
        }
    }

    /// <summary>
    /// Reads a hit that <see cref="WriteHit"/> wrote. The hit has no source object yet and generates no silk.
    /// </summary>
    /// <returns>Whether the hit could be read.</returns>
    private static bool TryReadHit(byte[] data, out HitInstance hit, out HitSourceState source) {
        hit = default;
        source = default;

        try {
            using var reader = new BinaryReader(new MemoryStream(data));

            source.Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            source.Tag = reader.ReadString();
            source.Layer = reader.ReadInt32();
            source.HasBody = reader.ReadBoolean();
            source.Velocity = new Vector2(reader.ReadSingle(), reader.ReadSingle());

            hit.IsFirstHit = reader.ReadBoolean();
            hit.AttackType = (AttackTypes) reader.ReadInt32();
            hit.NailElement = (NailElements) reader.ReadInt32();
            hit.IsUsingNeedleDamageMult = reader.ReadBoolean();
            var toolName = reader.ReadString();
            hit.RepresentingTool = toolName.Length > 0 ? ToolItemManager.GetToolByName(toolName) : null;
            hit.PoisonDamageTicks = reader.ReadInt32();
            hit.ZapDamageTicks = reader.ReadInt32();
            hit.DamageScalingLevel = reader.ReadInt32();
            hit.ToolDamageFlags = (ToolDamageFlags) reader.ReadInt32();
            hit.CircleDirection = reader.ReadBoolean();
            hit.DamageDealt = reader.ReadInt32();
            hit.StunDamage = reader.ReadSingle();
            hit.CanWeakHit = reader.ReadBoolean();
            hit.Direction = reader.ReadSingle();
            hit.UseCorpseDirection = reader.ReadBoolean();
            hit.CorpseDirection = reader.ReadSingle();
            hit.CanTriggerBouncePod = reader.ReadBoolean();
            hit.UseBouncePodDirection = reader.ReadBoolean();
            hit.BouncePodDirection = reader.ReadSingle();
            var hasExtraUpDirection = reader.ReadBoolean();
            var extraUpDirection = reader.ReadSingle();
            hit.ExtraUpDirection = hasExtraUpDirection ? extraUpDirection : null;
            hit.IgnoreInvulnerable = reader.ReadBoolean();
            hit.MagnitudeMultiplier = reader.ReadSingle();
            hit.UseCorpseMagnitudeMult = reader.ReadBoolean();
            hit.CorpseMagnitudeMultiplier = reader.ReadSingle();
            hit.UseCurrencyMagnitudeMult = reader.ReadBoolean();
            hit.CurrencyMagnitudeMult = reader.ReadSingle();
            hit.MoveAngle = reader.ReadSingle();
            // Without a body, the game would look for one on the parent of the source, which has no parent
            hit.MoveDirection = reader.ReadBoolean() && source.HasBody;
            hit.Multiplier = reader.ReadSingle();
            hit.SpecialType = (SpecialTypes) reader.ReadInt32();
            hit.HitEffectsType = (EnemyHitEffectsProfile.EffectsTypes) reader.ReadInt32();
            hit.NonLethal = reader.ReadBoolean();
            hit.RageHit = reader.ReadBoolean();
            hit.CriticalHit = reader.ReadBoolean();
            hit.HunterCombo = reader.ReadBoolean();
            hit.IsManualTrigger = reader.ReadBoolean();
            hit.ForceNotWeakHit = reader.ReadBoolean();
            hit.IsHeroDamage = reader.ReadBoolean();
            hit.IsNailTag = reader.ReadBoolean();
            hit.IgnoreNailPosition = reader.ReadBoolean();
            hit.IsHarpoon = reader.ReadBoolean();
            var hasEnemyPosition = reader.ReadBoolean();
            var enemyPosition = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            source.EnemyPosition = hasEnemyPosition ? enemyPosition : null;
            source.Ranges = ReadRanges(reader);

            // What a hit on an object of the room rolled in the game of the player who struck, after the hit
            source.Dice = SharedDice.Read(reader);

            hit.SilkGeneration = HitSilkGeneration.None;
            return true;
        } catch (IOException) {
            return false;
        }
    }

    /// <summary>
    /// Whose hit is being processed.
    /// </summary>
    private enum HitContext {
        /// <summary>
        /// No hit, or a hit that isn't from a player, like from a hazard.
        /// </summary>
        None,

        /// <summary>
        /// A hit of an attack of the local player.
        /// </summary>
        Local,

        /// <summary>
        /// A hit of the local copy of an attack of a remote player.
        /// </summary>
        Remote
    }

    /// <summary>
    /// The state of the object that a hit came from, which the object that replayed hits come from takes on.
    /// </summary>
    private struct HitSourceState {
        /// <summary>
        /// The position of the object.
        /// </summary>
        public Vector3 Position;

        /// <summary>
        /// The tag of the object.
        /// </summary>
        public string Tag;

        /// <summary>
        /// The layer of the object.
        /// </summary>
        public int Layer;

        /// <summary>
        /// Whether the direction of the hit came from a moving body.
        /// </summary>
        public bool HasBody;

        /// <summary>
        /// The velocity of that body.
        /// </summary>
        public Vector2 Velocity;

        /// <summary>
        /// For a hit that goes the way from its source to the enemy, where the enemy stood in the game of the player
        /// who struck, or null.
        /// </summary>
        public Vector3? EnemyPosition;

        /// <summary>
        /// For a hit on an object of the room, what its ranges had inside them in the game of the player who struck.
        /// </summary>
        public RangeState[] Ranges;

        /// <summary>
        /// For a hit on an object of the room, the dice that the hit rolled in the game of the player who struck, or
        /// null.
        /// </summary>
        public int[]? Dice;
    }

    /// <summary>
    /// What a range of an object had inside it in the game of the player who struck.
    /// </summary>
    private struct RangeState {
        /// <summary>
        /// How many of what it counts were inside it.
        /// </summary>
        public byte Inside;

        /// <summary>
        /// For an alert range, whether it said that a player was near.
        /// </summary>
        public bool InRange;
    }

    /// <summary>
    /// The state of the attack that a touch came from, which the object that replayed touches come from takes on.
    /// </summary>
    private struct TouchSourceState {
        /// <summary>
        /// The position of the attack.
        /// </summary>
        public Vector3 Position;

        /// <summary>
        /// The name of the attack, which some actions look at.
        /// </summary>
        public string Name;

        /// <summary>
        /// The tag of the attack.
        /// </summary>
        public string Tag;

        /// <summary>
        /// The layer of the attack.
        /// </summary>
        public int Layer;

        /// <summary>
        /// What the ranges of the object that was touched had inside them in the game of the player who struck.
        /// </summary>
        public RangeState[] Ranges;

        /// <summary>
        /// The dice that the touch rolled in the game of the player who struck, or null.
        /// </summary>
        public int[]? Dice;
    }
}
