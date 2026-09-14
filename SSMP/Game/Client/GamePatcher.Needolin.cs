using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Util;
using UnityEngine;

namespace SSMP.Game.Client;

/// <summary>
/// Needolin co-op patches. Needolin range checks count every performing player, an enemy that reacts to a song faces
/// the player it reacts to until its song ends, and songs started by a remote player last as long as that player's
/// Musician Charm allows.
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// How long, in seconds, the performer that set off a needolin check stays the enemy's target if no song starts.
    /// This covers the state change between the check sending its event and the next state facing the target.
    /// </summary>
    private const float PendingSongTargetSeconds = 1f;

    /// <summary>
    /// Reflected private field with the remaining sing duration of <see cref="EnemySingControl"/>.
    /// </summary>
    private static readonly FieldInfo? EnemySingControlSingDurationField =
        typeof(EnemySingControl).GetField("singDuration", InstanceNonPublicFlags | InstancePublicFlags);

    /// <summary>
    /// Reflected private field that <see cref="NeedolinTextOwner"/> sets while the local player performs.
    /// </summary>
    private static readonly FieldInfo? NeedolinTextOwnerIsPlayingField =
        typeof(NeedolinTextOwner).GetField("isPlaying", InstanceNonPublicFlags | InstancePublicFlags);

    /// <summary>
    /// The performer that each singing enemy faces until its song ends, per enemy owner instance ID.
    /// </summary>
    private static readonly Dictionary<int, SongTarget> SongTargets = new();

    /// <summary>
    /// The performer that set off a needolin check that just sent a reaction event, per enemy owner instance ID.
    /// </summary>
    private static readonly Dictionary<int, SongTarget> PendingSongTargets = new();

    /// <summary>
    /// Whether a transform checked by needolin checks belongs to an enemy, per transform instance ID.
    /// </summary>
    private static readonly Dictionary<int, bool> NeedolinEnemyTargets = new();

    /// <summary>
    /// Hook for counting remote performers in <see cref="HeroPerformanceRegion.GetAffectedState"/>.
    /// </summary>
    private Hook? _getAffectedStateHook;

    /// <summary>
    /// Hook for counting remote performers in <see cref="HeroPerformanceRegion.GetAffectedStateWithRadius"/>.
    /// </summary>
    private Hook? _getAffectedStateWithRadiusHook;

    /// <summary>
    /// Hook for counting remote performers in <see cref="HeroPerformanceRegion.IsPlayingInRange"/>.
    /// </summary>
    private Hook? _isPlayingInRangeHook;

    /// <summary>
    /// Hook for remembering which performer set off a <see cref="CheckHeroPerformanceRegion"/> reaction.
    /// </summary>
    private Hook? _checkPerformanceRegionSendEventsHook;

    /// <summary>
    /// Hook for remembering which performer set off a <see cref="CheckHeroPerformanceRegionV2"/> reaction.
    /// </summary>
    private Hook? _checkPerformanceRegionV2SendEventsHook;

    /// <summary>
    /// Hook for locking a singing enemy onto its performer and setting the sing duration for that performer.
    /// </summary>
    private Hook? _enemySingControlEnterHook;

    /// <summary>
    /// Hook for releasing a singing enemy from its performer when the song ends.
    /// </summary>
    private Hook? _enemySingControlExitHook;

    /// <summary>
    /// Hook for showing needolin texts while only remote players perform.
    /// </summary>
    private Hook? _needolinTextOwnerUpdateHook;

    /// <summary>
    /// Registers the needolin co-op hooks.
    /// </summary>
    private void RegisterNeedolinHooks() {
        _getAffectedStateHook = TryCreateHook(
            typeof(HeroPerformanceRegion), "GetAffectedState", OnGetAffectedState, StaticNonPublicPublicFlags
        );
        _getAffectedStateWithRadiusHook = TryCreateHook(
            typeof(HeroPerformanceRegion),
            "GetAffectedStateWithRadius",
            OnGetAffectedStateWithRadius,
            StaticNonPublicPublicFlags
        );
        _isPlayingInRangeHook = TryCreateHook(
            typeof(HeroPerformanceRegion), "IsPlayingInRange", OnIsPlayingInRange, StaticNonPublicPublicFlags
        );

        _checkPerformanceRegionSendEventsHook = TryCreateHook(
            typeof(CheckHeroPerformanceRegion), "SendEvents", OnCheckHeroPerformanceRegionSendEvents
        );
        _checkPerformanceRegionV2SendEventsHook = TryCreateHook(
            typeof(CheckHeroPerformanceRegionV2), "SendEvents", OnCheckHeroPerformanceRegionV2SendEvents
        );

        _enemySingControlEnterHook = TryCreateHook(typeof(EnemySingControl), "OnEnter", OnEnemySingControlEnter);
        _enemySingControlExitHook = TryCreateHook(typeof(EnemySingControl), "OnExit", OnEnemySingControlExit);

        _needolinTextOwnerUpdateHook = TryCreateHook(typeof(NeedolinTextOwner), "Update", OnNeedolinTextOwnerUpdate);

        MonoBehaviourUtil.Instance.OnUpdateEvent += NeedolinCoop.UpdateLocalPerformer;
    }

    /// <summary>
    /// Disposes the needolin co-op hooks.
    /// </summary>
    private void DisposeNeedolinHooks() {
        _getAffectedStateHook?.Dispose();
        _getAffectedStateHook = null;

        _getAffectedStateWithRadiusHook?.Dispose();
        _getAffectedStateWithRadiusHook = null;

        _isPlayingInRangeHook?.Dispose();
        _isPlayingInRangeHook = null;

        _checkPerformanceRegionSendEventsHook?.Dispose();
        _checkPerformanceRegionSendEventsHook = null;

        _checkPerformanceRegionV2SendEventsHook?.Dispose();
        _checkPerformanceRegionV2SendEventsHook = null;

        _enemySingControlEnterHook?.Dispose();
        _enemySingControlEnterHook = null;

        _enemySingControlExitHook?.Dispose();
        _enemySingControlExitHook = null;

        _needolinTextOwnerUpdateHook?.Dispose();
        _needolinTextOwnerUpdateHook = null;

        if (MonoBehaviourUtil.Instance != null) {
            MonoBehaviourUtil.Instance.OnUpdateEvent -= NeedolinCoop.UpdateLocalPerformer;
        }

        ClearNeedolinTargets();
    }

    /// <summary>
    /// Clears the remote performers, song targets and cached needolin target lookups, for example when the scene
    /// changes. Remote players that are performing in the new scene are added again from their animation.
    /// </summary>
    private static void ClearNeedolinTargets() {
        NeedolinCoop.ClearRemotePerformers();
        SongTargets.Clear();
        PendingSongTargets.Clear();
        NeedolinEnemyTargets.Clear();
    }

    /// <summary>
    /// Gets the target that target-consuming FSM actions of an enemy should use: the performer the enemy sings or
    /// just reacted to, or otherwise its approved multiplayer target.
    /// </summary>
    /// <param name="requester">The enemy object or one of its child/component objects.</param>
    /// <returns>The target, or <see langword="null"/> when there is none.</returns>
    private static GameObject? GetFsmActionTarget(GameObject? requester) {
        if (requester == null) {
            return null;
        }

        var owner = GetEnemyTargetOwner(requester);
        if (owner != null && TryGetSongTarget(owner.GetInstanceID(), out var songTarget)) {
            return songTarget;
        }

        return GetApprovedEnemyTarget(requester);
    }

    /// <summary>
    /// Gets the performer that an enemy is singing to, or that it reacted to within the last moment.
    /// </summary>
    /// <param name="ownerId">The instance ID of the enemy owner.</param>
    /// <param name="target">The performer's player object, if found.</param>
    /// <returns>true if the enemy has a song target; otherwise false.</returns>
    private static bool TryGetSongTarget(int ownerId, out GameObject? target) {
        if (SongTargets.TryGetValue(ownerId, out var song)) {
            if (song.Performer != null && song.Performer.activeInHierarchy) {
                target = song.Performer;
                return true;
            }

            SongTargets.Remove(ownerId);
        }

        if (PendingSongTargets.TryGetValue(ownerId, out var pending)) {
            if (IsPendingSongTargetValid(pending)) {
                target = pending.Performer;
                return true;
            }

            PendingSongTargets.Remove(ownerId);
        }

        target = null;
        return false;
    }

    /// <summary>
    /// Checks whether a pending song target is recent enough and its performer is still in the scene.
    /// </summary>
    private static bool IsPendingSongTargetValid(SongTarget pending) {
        return pending.Performer != null && pending.Performer.activeInHierarchy &&
               Time.time - pending.FoundTime <= PendingSongTargetSeconds;
    }

    /// <summary>
    /// Whether remote performers count for a needolin check on the given target. Checks that ignore range usually
    /// run after the object's own trigger zone found the local hero, as for thread memories, so for anything that
    /// is not an enemy they only count the local player. Enemies find their players with multiplayer-aware alert
    /// ranges, so any performer counts for them.
    /// </summary>
    /// <param name="target">The transform being checked.</param>
    /// <param name="ignoreRange">Whether the check ignores range.</param>
    /// <returns>true if remote performers count; otherwise false.</returns>
    private static bool CountsRemotePerformers(Transform target, bool ignoreRange) {
        if (!ignoreRange) {
            return true;
        }

        var id = target.GetInstanceID();
        if (!NeedolinEnemyTargets.TryGetValue(id, out var isEnemy)) {
            isEnemy = target.GetComponentInParent<HealthManager>(true) != null;
            NeedolinEnemyTargets[id] = isEnemy;
        }

        return isEnemy;
    }

    /// <summary>
    /// Adds remote performers to <see cref="HeroPerformanceRegion.GetAffectedState"/>.
    /// </summary>
    private static HeroPerformanceRegion.AffectedState OnGetAffectedState(
        Func<Transform, bool, HeroPerformanceRegion.AffectedState> orig,
        Transform target,
        bool ignoreRange
    ) {
        var state = orig(target, ignoreRange);
        if (!NeedolinCoop.HasRemotePerformers || target == null || !CountsRemotePerformers(target, ignoreRange)) {
            return state;
        }

        var remoteState = NeedolinCoop.Evaluate(target, ignoreRange, 0f, false, true, out _);
        return NeedolinCoop.GetStronger(state, remoteState);
    }

    /// <summary>
    /// Adds remote performers to <see cref="HeroPerformanceRegion.GetAffectedStateWithRadius"/>.
    /// </summary>
    private static HeroPerformanceRegion.AffectedState OnGetAffectedStateWithRadius(
        Func<Transform, float, HeroPerformanceRegion.AffectedState> orig,
        Transform target,
        float radius
    ) {
        var state = orig(target, radius);
        if (!NeedolinCoop.HasRemotePerformers || target == null) {
            return state;
        }

        var remoteState = NeedolinCoop.Evaluate(target, false, radius, false, true, out _);
        return NeedolinCoop.GetStronger(state, remoteState);
    }

    /// <summary>
    /// Adds remote performers to <see cref="HeroPerformanceRegion.IsPlayingInRange"/>.
    /// </summary>
    private static bool OnIsPlayingInRange(Func<Vector2, float, bool> orig, Vector2 position, float range) {
        return orig(position, range) || NeedolinCoop.IsRemotePerformerInRange(position, range);
    }

    /// <summary>
    /// Remembers the performer that set off a reaction event of a <see cref="CheckHeroPerformanceRegion"/>.
    /// </summary>
    private static void OnCheckHeroPerformanceRegionSendEvents(
        Action<CheckHeroPerformanceRegion, HeroPerformanceRegion.AffectedState> orig,
        CheckHeroPerformanceRegion self,
        HeroPerformanceRegion.AffectedState state
    ) {
        if (SendsReactionEvent(state, self.ActiveInner, self.ActiveOuter)) {
            RememberPendingSongTarget(self.Fsm, self.Target, self.IgnoreNeedolinRange.Value, 0f);
        }

        orig(self, state);
    }

    /// <summary>
    /// Remembers the performer that set off a reaction event of a <see cref="CheckHeroPerformanceRegionV2"/>.
    /// </summary>
    private static void OnCheckHeroPerformanceRegionV2SendEvents(
        Action<CheckHeroPerformanceRegionV2, HeroPerformanceRegion.AffectedState> orig,
        CheckHeroPerformanceRegionV2 self,
        HeroPerformanceRegion.AffectedState state
    ) {
        if (SendsReactionEvent(state, self.ActiveInner, self.ActiveOuter)) {
            RememberPendingSongTarget(self.Fsm, self.Target, self.IgnoreNeedolinRange.Value, self.Radius.Value);
        }

        orig(self, state);
    }

    /// <summary>
    /// Checks whether a needolin check sends an event for the given state. The check sends both events for the inner
    /// range and only the outer event for the outer range.
    /// </summary>
    private static bool SendsReactionEvent(
        HeroPerformanceRegion.AffectedState state,
        FsmEvent? activeInner,
        FsmEvent? activeOuter
    ) {
        return state switch {
            HeroPerformanceRegion.AffectedState.ActiveInner => IsEventSet(activeInner) || IsEventSet(activeOuter),
            HeroPerformanceRegion.AffectedState.ActiveOuter => IsEventSet(activeOuter),
            _ => false
        };
    }

    /// <summary>
    /// Checks whether an FSM event field of an action refers to an event.
    /// </summary>
    private static bool IsEventSet(FsmEvent? fsmEvent) {
        return fsmEvent != null && !string.IsNullOrEmpty(fsmEvent.Name);
    }

    /// <summary>
    /// Stores the performer that affects a needolin check's target the most as the pending song target of the enemy,
    /// unless the enemy is already singing to someone.
    /// </summary>
    /// <param name="fsm">The FSM of the check.</param>
    /// <param name="targetOwner">The check's target.</param>
    /// <param name="ignoreRange">Whether the check ignores range.</param>
    /// <param name="radius">The radius of the check's target, or 0.</param>
    private static void RememberPendingSongTarget(
        HutongGames.PlayMaker.Fsm? fsm,
        FsmOwnerDefault targetOwner,
        bool ignoreRange,
        float radius
    ) {
        var requester = fsm?.GameObject;
        var target = fsm?.GetOwnerDefaultTarget(targetOwner);
        if (requester == null || target == null) {
            return;
        }

        var owner = GetEnemyTargetOwner(requester);
        if (owner == null) {
            return;
        }

        // An enemy keeps singing to the same player until its song ends
        var ownerId = owner.GetInstanceID();
        if (SongTargets.ContainsKey(ownerId)) {
            return;
        }

        var targetTransform = target.transform;
        NeedolinCoop.Evaluate(
            targetTransform,
            ignoreRange,
            ignoreRange ? 0f : radius,
            true,
            CountsRemotePerformers(targetTransform, ignoreRange),
            out var performer
        );
        if (performer == null) {
            return;
        }

        PendingSongTargets[ownerId] = new SongTarget {
            Performer = performer.Object,
            IsLocal = performer.IsLocal,
            HasMusicianCharm = performer.HasMusicianCharm,
            FoundTime = Time.time
        };
    }

    /// <summary>
    /// Locks a singing enemy onto the performer it reacted to, and sets the sing duration from that performer's
    /// Musician Charm when they are a remote player.
    /// </summary>
    private static void OnEnemySingControlEnter(Action<EnemySingControl> orig, EnemySingControl self) {
        orig(self);

        var enemy = self.Fsm?.GetOwnerDefaultTarget(self.enemyGameObject);
        var owner = enemy == null ? null : GetEnemyTargetOwner(enemy);
        if (owner == null) {
            return;
        }

        var ownerId = owner.GetInstanceID();
        if (!PendingSongTargets.TryGetValue(ownerId, out var pending)) {
            return;
        }

        PendingSongTargets.Remove(ownerId);
        if (!IsPendingSongTargetValid(pending)) {
            return;
        }

        SongTargets[ownerId] = pending;

        // EnemySingControl picks its duration from the local player's Musician Charm, see its OnEnter
        if (!pending.IsLocal && pending.HasMusicianCharm != NeedolinCoop.IsLocalMusicianCharmEquipped()) {
            var duration = pending.HasMusicianCharm
                ? UnityEngine.Random.Range(6.5f, 8f)
                : UnityEngine.Random.Range(4f, 6.75f);
            EnemySingControlSingDurationField?.SetValue(self, duration);
        }
    }

    /// <summary>
    /// Releases a singing enemy from its performer once its song ends.
    /// </summary>
    private static void OnEnemySingControlExit(Action<EnemySingControl> orig, EnemySingControl self) {
        orig(self);

        var enemy = self.Fsm?.GetOwnerDefaultTarget(self.enemyGameObject);
        var owner = enemy == null ? null : GetEnemyTargetOwner(enemy);
        if (owner != null) {
            SongTargets.Remove(owner.GetInstanceID());
        }
    }

    /// <summary>
    /// Lets <see cref="NeedolinTextOwner"/> show its text while a remote player performs in range. It only checks its
    /// range while its playing flag is set, which the game sets when the local player starts performing.
    /// </summary>
    private static void OnNeedolinTextOwnerUpdate(Action<NeedolinTextOwner> orig, NeedolinTextOwner self) {
        if (NeedolinTextOwnerIsPlayingField == null || !NeedolinCoop.HasRemotePerformers ||
            NeedolinTextOwnerIsPlayingField.GetValue(self) is true) {
            orig(self);
            return;
        }

        NeedolinTextOwnerIsPlayingField.SetValue(self, true);
        try {
            orig(self);
        } finally {
            NeedolinTextOwnerIsPlayingField.SetValue(self, false);
        }
    }

    /// <summary>
    /// A performer that an enemy faces because of their song.
    /// </summary>
    private sealed class SongTarget {
        /// <summary>
        /// The player object of the performer.
        /// </summary>
        public GameObject Performer = null!;

        /// <summary>
        /// Whether the performer is the local player.
        /// </summary>
        public bool IsLocal;

        /// <summary>
        /// Whether the performer has the Musician Charm equipped.
        /// </summary>
        public bool HasMusicianCharm;

        /// <summary>
        /// The time at which the performer was found.
        /// </summary>
        public float FoundTime;
    }
}
