using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using GlobalEnums;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using SSMP.Game.Client.Entity;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Replays hits between the players of a two-player save. When the local player hits an object of the world, such as a
/// wall that breaks or a lever, the game of the partner replays the hit on its own copy of that object, so that both
/// worlds change in the same way. The copies of the partner's attacks in the local game leave those objects alone, so
/// that nothing is hit twice.
/// A replayed hit gives the local player nothing: no knockback, no silk and no hit pause. What the object drops is
/// dropped in both games, so each player gets their own.
/// Knockback of enemies is local first as well: a hit of the local player knocks back an enemy at once, also when the
/// scene host controls the enemy, and the game of the scene host applies the same knockback when the hit arrives.
/// </summary>
internal class CoopHits {
    /// <summary>
    /// Binding flags for the instance methods that are hooked.
    /// </summary>
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// The types of objects whose hits are replayed: objects of the world that both players share. Objects that give
    /// the player who hits them something, that move that player, or that only react to hits, are left out.
    /// </summary>
    private static readonly Type[] ReplayedTypes = [
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
    /// Whether hits on objects of a type are replayed, for the types that were looked up.
    /// </summary>
    private static readonly Dictionary<Type, bool> IsReplayedByType = new();

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
    /// Whether a hit of the partner is being replayed.
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
    }

    /// <summary>
    /// Disposes the hooks and the objects that replayed hits come from.
    /// </summary>
    public void DeregisterHooks() {
        foreach (var hook in _hooks) {
            hook.Dispose();
        }

        _hooks.Clear();
        _isReplaying = false;
        _hitContext = HitContext.None;

        if (_hitSource != null) {
            Object.Destroy(_hitSource);
        }

        if (_movingHitSource != null) {
            Object.Destroy(_movingHitSource.gameObject);
        }

        _hitSource = null;
        _movingHitSource = null;
    }

    /// <summary>
    /// Replays a hit of the partner on the local copy of the object that they hit, or applies the knockback of their
    /// hit on an enemy.
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

        var type = typeof(IHitResponder).Assembly.GetType(update.Responder);
        if (type == null || !typeof(IHitResponder).IsAssignableFrom(type) || !IsReplayed(type)) {
            return;
        }

        var target = ScenePath.Find(update.Path, update.Scene);
        if (target == null) {
            return;
        }

        var components = target.GetComponents(type);
        if (update.Index >= components.Length) {
            return;
        }

        var component = components[update.Index];
        if (component is not IHitResponder responder || component is Behaviour { isActiveAndEnabled: false } ||
            !IsReplayed(component)) {
            return;
        }

        if (!TryReadHit(update.Hit, out var hit, out var source)) {
            Logger.Warn($"Could not read a hit of the partner on {update.Path}");
            return;
        }

        var sourceObject = GetHitSource(source);
        hit.Source = sourceObject;

        _isReplaying = true;
        try {
            _gamePatcher.RunAsRemoteHit(() => responder.Hit(hit));
        } catch (Exception e) {
            Logger.Warn($"Could not replay a hit of the partner on {update.Path}:\n{e}");
        } finally {
            _isReplaying = false;

            if (_movingHitSource != null) {
                _movingHitSource.linearVelocity = Vector2.zero;
            }
        }
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
    /// hit from giving the local player silk.
    /// </summary>
    private void OnAddSilk(
        Action<HeroController, int, bool, SilkSpool.SilkAddSource, bool> orig,
        HeroController self,
        int amount,
        bool heroEffect,
        SilkSpool.SilkAddSource source,
        bool forceCanBindEffect
    ) {
        if (_isReplaying) {
            return;
        }

        orig(self, amount, heroEffect, source, forceCanBindEffect);
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

        if (responder is Component component && IsReplayed(component)) {
            if (isRemote) {
                return IHitResponder.Response.None;
            }

            // The update is made before the hit, since a hit can break the object and move its parts
            var update = hit.IsHeroDamage ? CreateUpdate(partnerId, component, hit) : null;
            var response = responder.Hit(hit);
            if (update != null && response.response != IHitResponder.Response.None && _netClient.IsConnected) {
                _netClient.UpdateManager.SetCoopHitUpdate(update);
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

        // Anything else, like an enemy, takes the hit as usual, and the hook of Recoil knows whose hit it is
        var lastContext = _hitContext;
        _hitContext = isRemote ? HitContext.Remote : hit.IsHeroDamage ? HitContext.Local : HitContext.None;
        try {
            var response = responder.Hit(hit);

            // Nothing of this hit happens in the partner's game any more, so what it looked like is sent to them
            if (isEnemyEntity && hit.IsHeroDamage && response.response != IHitResponder.Response.None) {
                SendHitEffect(partnerId, enemyId, hit);
            }

            return response;
        } finally {
            _hitContext = lastContext;
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

        orig(self, direction, magnitude);

        if (!isClientCopy) {
            return;
        }

        // The number goes on the knockback and comes back on the positions of the scene host, which is the whole of
        // how the enemy stops being pulled back to where it was hit: until that number comes back, every position
        // from over there was measured before the hit had arrived, and after it they all have the hit in them.
        //
        // Taken before the knockback is sent so that it goes with it, and only kept if it was sent: a knockback that
        // cannot be sent is one the scene host will never make, and waiting for it to come back would hold the enemy
        // away from where it really is for the whole of the timeout.
        var id = entity.BeginAnticipation();
        if (!SendKnockback(partnerId, entity.Id, direction, magnitude, id)) {
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
        entity.NoteAnticipation(id);
    }

    /// <summary>
    /// Sends what a hit of the local player on an enemy looked like to the partner, whose copy of the attack no
    /// longer hits that enemy itself.
    /// </summary>
    /// <param name="partnerId">The ID of the partner.</param>
    /// <param name="entityId">The ID of the entity that was hit.</param>
    /// <param name="hit">The hit, which decides which effect is played.</param>
    private void SendHitEffect(ushort partnerId, ushort entityId, HitInstance hit) {
        if (!_netClient.IsConnected || !_playerData.TryGetValue(partnerId, out var partner) ||
            !partner.IsInLocalScene) {
            return;
        }

        _netClient.UpdateManager.SetCoopHitUpdate(new CoopHitUpdate {
            TargetId = partnerId,
            Kind = CoopHitKind.EnemyHitEffect,
            EntityId = entityId,
            Hit = WriteHit(hit)
        });
    }

    /// <summary>
    /// Plays what a hit of the partner on an enemy looked like, without the hit itself. The enemy's own health comes
    /// from whichever game controls it, so all that is left to do here is show that it was hit.
    /// </summary>
    /// <param name="update">The update of the partner's hit.</param>
    private void ApplyHitEffect(CoopHitUpdate update) {
        GameObject? enemy = null;
        foreach (var entity in _entityManager.ActiveEntities) {
            if (entity.Id == update.EntityId) {
                // The scene host watches its own object, and everyone else the copy that follows it
                enemy = _entityManager.IsSceneHost ? entity.Object.Host : entity.Object.Client;
                break;
            }
        }

        if (enemy == null || !enemy.activeInHierarchy ||
            !enemy.TryGetComponent<HealthManager>(out var healthManager) || healthManager.GetIsDead()) {
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

        hit.Source = GetHitSource(source);
        receiver.ReceiveHitEffect(hit);
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
                Hit = WriteHit(hit)
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
    /// Whether hits on the given object are replayed: it is of a replayed type and not part of an enemy, since enemy
    /// sync takes care of enemies.
    /// </summary>
    private static bool IsReplayed(Component component) {
        return IsReplayed(component.GetType()) && component.GetComponentInParent<HealthManager>(true) == null;
    }

    /// <summary>
    /// Whether hits on objects of the given type are replayed.
    /// </summary>
    private static bool IsReplayed(Type type) {
        if (!IsReplayedByType.TryGetValue(type, out var replayed)) {
            replayed = Array.Exists(ReplayedTypes, replayedType => replayedType.IsAssignableFrom(type));
            IsReplayedByType[type] = replayed;
        }

        return replayed;
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
    private static byte[] WriteHit(HitInstance hit) {
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

        writer.Flush();
        return stream.ToArray();
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
    }
}
