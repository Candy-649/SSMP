using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client;

/// <summary>
/// Needolin co-op patches for scene objects that listen to the local hero starting and stopping the needolin instead of
/// checking its range. Audio that plays along with the song, and range attackers that sing while a performer stands in
/// their sing area, also react to remote players performing in the scene.
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// Reflected private method that starts a <see cref="NeedolinSyncedAudioPlayer"/>.
    /// </summary>
    private static readonly MethodInfo? SyncedAudioStartMethod = typeof(NeedolinSyncedAudioPlayer).GetMethod(
        "OnStartedPerforming",
        InstanceNonPublicFlags | InstancePublicFlags
    );

    /// <summary>
    /// Reflected private method that stops a <see cref="NeedolinSyncedAudioPlayer"/>.
    /// </summary>
    private static readonly MethodInfo? SyncedAudioStopMethod = typeof(NeedolinSyncedAudioPlayer).GetMethod(
        "OnStoppedPerforming",
        InstanceNonPublicFlags | InstancePublicFlags
    );

    /// <summary>
    /// Reflected private field with the audio source that a <see cref="SyncNeedolinLoop"/> plays.
    /// </summary>
    private static readonly FieldInfo? SyncLoopSourceField =
        typeof(SyncNeedolinLoop).GetField("source", InstanceNonPublicFlags | InstancePublicFlags);

    /// <summary>
    /// Reflected private field that is set while a <see cref="SyncNeedolinLoop"/> should play.
    /// </summary>
    private static readonly FieldInfo? SyncLoopIsPlayingField =
        typeof(SyncNeedolinLoop).GetField("isPlaying", InstanceNonPublicFlags | InstancePublicFlags);

    /// <summary>
    /// Reflected private field with the coroutine that plays a <see cref="SyncNeedolinLoop"/> for the local hero.
    /// </summary>
    private static readonly FieldInfo? SyncLoopPlayRoutineField =
        typeof(SyncNeedolinLoop).GetField("playRoutine", InstanceNonPublicFlags | InstancePublicFlags);

    /// <summary>
    /// Reflected private field with the area in which a performer makes a <see cref="RangeAttacker"/> sing.
    /// </summary>
    private static readonly FieldInfo? RangeAttackerSingRangeField =
        typeof(RangeAttacker).GetField("singRange", InstanceNonPublicFlags | InstancePublicFlags);

    /// <summary>
    /// Reflected private field with the area in which a performer does not make a <see cref="RangeAttacker"/> sing.
    /// </summary>
    private static readonly FieldInfo? RangeAttackerSingExcludeRangeField =
        typeof(RangeAttacker).GetField("singExcludeRange", InstanceNonPublicFlags | InstancePublicFlags);

    /// <summary>
    /// Reflected private field that makes a <see cref="RangeAttacker"/> come out and sing while set.
    /// </summary>
    private static readonly FieldInfo? RangeAttackerIsHeroPerformingField =
        typeof(RangeAttacker).GetField("isHeroPerforming", InstanceNonPublicFlags | InstancePublicFlags);

    /// <summary>
    /// Reflected private method that starts the animation coroutine of a <see cref="RangeAttacker"/>.
    /// </summary>
    private static readonly MethodInfo? RangeAttackerEnsureAnimStartedMethod =
        typeof(RangeAttacker).GetMethod("EnsureAnimStarted", InstanceNonPublicFlags | InstancePublicFlags);

    /// <summary>
    /// Reflected private method that makes a <see cref="RangeAttacker"/> stop singing.
    /// </summary>
    private static readonly MethodInfo? RangeAttackerStopMethod =
        typeof(RangeAttacker).GetMethod("OnHeroStoppedPerforming", InstanceNonPublicFlags | InstancePublicFlags);

    /// <summary>
    /// The needolin synced audio players in the scene.
    /// </summary>
    private static readonly List<NeedolinSyncedAudioPlayer> SceneSyncedAudioPlayers = new();

    /// <summary>
    /// The needolin sync loops in the scene.
    /// </summary>
    private static readonly List<SyncNeedolinLoop> SceneSyncLoops = new();

    /// <summary>
    /// The range attackers in the scene that have a sing area.
    /// </summary>
    private static readonly List<RangeAttacker> SceneSingingRangeAttackers = new();

    /// <summary>
    /// Range attackers that sing because a remote performer stands in their sing area.
    /// </summary>
    private static readonly HashSet<RangeAttacker> RemoteSingingRangeAttackers = new();

    /// <summary>
    /// Whether the needolin listeners of the current scene were looked up.
    /// </summary>
    private static bool _sceneNeedolinListenersFound;

    /// <summary>
    /// Whether a remote player performed in the scene in the previous frame.
    /// </summary>
    private static bool _remoteWasPerforming;

    /// <summary>
    /// Whether the listeners may stop even though a remote player still performs, set while stopping them for the
    /// remote players.
    /// </summary>
    private static bool _allowNeedolinListenerStop;

    /// <summary>
    /// Hook for keeping a <see cref="NeedolinSyncedAudioPlayer"/> playing while a remote player still performs.
    /// </summary>
    private Hook? _syncedAudioStoppedHook;

    /// <summary>
    /// Hook for keeping a <see cref="SyncNeedolinLoop"/> playing while a remote player still performs.
    /// </summary>
    private Hook? _syncLoopStoppedHook;

    /// <summary>
    /// Hook for keeping a <see cref="RangeAttacker"/> singing while a remote performer stands in its sing area.
    /// </summary>
    private Hook? _rangeAttackerStoppedHook;

    /// <summary>
    /// Registers the hooks for needolin listeners.
    /// </summary>
    private void RegisterNeedolinListenerHooks() {
        _syncedAudioStoppedHook = TryCreateHook(
            typeof(NeedolinSyncedAudioPlayer), "OnStoppedPerforming", OnSyncedAudioStoppedPerforming
        );
        _syncLoopStoppedHook = TryCreateHook(typeof(SyncNeedolinLoop), "OnNeedolinStopped", OnSyncLoopNeedolinStopped);
        _rangeAttackerStoppedHook = TryCreateHook(
            typeof(RangeAttacker), "OnHeroStoppedPerforming", OnRangeAttackerStoppedPerforming
        );
    }

    /// <summary>
    /// Disposes the hooks for needolin listeners.
    /// </summary>
    private void DisposeNeedolinListenerHooks() {
        _syncedAudioStoppedHook?.Dispose();
        _syncedAudioStoppedHook = null;

        _syncLoopStoppedHook?.Dispose();
        _syncLoopStoppedHook = null;

        _rangeAttackerStoppedHook?.Dispose();
        _rangeAttackerStoppedHook = null;
    }

    /// <summary>
    /// Forgets the needolin listeners of the scene, for example when the scene changes.
    /// </summary>
    private static void ClearNeedolinListeners() {
        SceneSyncedAudioPlayers.Clear();
        SceneSyncLoops.Clear();
        SceneSingingRangeAttackers.Clear();
        RemoteSingingRangeAttackers.Clear();
        _sceneNeedolinListenersFound = false;
        _remoteWasPerforming = false;
    }

    /// <summary>
    /// Starts and stops the needolin listeners of the scene for remote players. Called every frame.
    /// </summary>
    private static void UpdateNeedolinListeners() {
        var remoteIsPerforming = NeedolinCoop.HasRemotePerformers;
        if (remoteIsPerforming && !_sceneNeedolinListenersFound) {
            FindSceneNeedolinListeners();
        }

        if (remoteIsPerforming != _remoteWasPerforming) {
            _remoteWasPerforming = remoteIsPerforming;
            if (remoteIsPerforming) {
                StartListenersForRemotePerformers();
            } else {
                StopListenersForRemotePerformers();
            }
        }

        if (remoteIsPerforming || RemoteSingingRangeAttackers.Count > 0) {
            UpdateRemoteSingingRangeAttackers();
        }
    }

    /// <summary>
    /// Looks up the needolin listeners in the scene.
    /// </summary>
    private static void FindSceneNeedolinListeners() {
        _sceneNeedolinListenersFound = true;

        SceneSyncedAudioPlayers.Clear();
        SceneSyncedAudioPlayers.AddRange(Object.FindObjectsByType<NeedolinSyncedAudioPlayer>(FindObjectsSortMode.None));

        SceneSyncLoops.Clear();
        SceneSyncLoops.AddRange(Object.FindObjectsByType<SyncNeedolinLoop>(FindObjectsSortMode.None));

        SceneSingingRangeAttackers.Clear();
        foreach (var attacker in Object.FindObjectsByType<RangeAttacker>(FindObjectsSortMode.None)) {
            if (RangeAttackerSingRangeField?.GetValue(attacker) is TrackTriggerObjects singRange && singRange != null) {
                SceneSingingRangeAttackers.Add(attacker);
            }
        }
    }

    /// <summary>
    /// Starts the audio that plays along with the needolin once a remote player starts performing, unless the local
    /// player already started it.
    /// </summary>
    private static void StartListenersForRemotePerformers() {
        if (HeroPerformanceRegion.IsPerforming) {
            return;
        }

        foreach (var player in SceneSyncedAudioPlayers) {
            // It disables itself while silent, so only its object has to be active
            if (player != null && player.gameObject.activeInHierarchy) {
                SyncedAudioStartMethod?.Invoke(player, null);
            }
        }

        var loopSource = NeedolinRemoteAudio.GetPlayingLoopSource();
        foreach (var syncLoop in SceneSyncLoops) {
            StartSyncLoopForRemotePerformers(syncLoop, loopSource);
        }
    }

    /// <summary>
    /// Starts a <see cref="SyncNeedolinLoop"/> in time with a remote player's song. Its own coroutine waits for the
    /// local hero's needolin audio, so the loop is played directly instead.
    /// </summary>
    /// <param name="syncLoop">The sync loop.</param>
    /// <param name="loopSource">The audio source of the remote player's song, if it plays.</param>
    private static void StartSyncLoopForRemotePerformers(SyncNeedolinLoop syncLoop, AudioSource? loopSource) {
        if (syncLoop == null || !syncLoop.isActiveAndEnabled || SyncLoopIsPlayingField == null ||
            SyncLoopIsPlayingField.GetValue(syncLoop) is true ||
            SyncLoopSourceField?.GetValue(syncLoop) is not AudioSource source || source == null) {
            return;
        }

        SyncLoopIsPlayingField.SetValue(syncLoop, true);
        source.Play();

        if (loopSource != null && loopSource.clip != null && source.clip != null &&
            loopSource.clip.frequency == source.clip.frequency && source.clip.samples > 0) {
            source.timeSamples = loopSource.timeSamples % source.clip.samples;
        }
    }

    /// <summary>
    /// Stops the audio that plays along with the needolin once no remote player performs, unless the local player
    /// still performs.
    /// </summary>
    private static void StopListenersForRemotePerformers() {
        if (HeroPerformanceRegion.IsPerforming) {
            return;
        }

        _allowNeedolinListenerStop = true;
        try {
            foreach (var player in SceneSyncedAudioPlayers) {
                if (player != null) {
                    SyncedAudioStopMethod?.Invoke(player, null);
                }
            }
        } finally {
            _allowNeedolinListenerStop = false;
        }

        foreach (var syncLoop in SceneSyncLoops) {
            if (syncLoop == null) {
                continue;
            }

            // A loop started for the local hero stops through its coroutine once the flag is off; one started for
            // remote players has no coroutine
            var hasRoutine = SyncLoopPlayRoutineField?.GetValue(syncLoop) != null;
            SyncLoopIsPlayingField?.SetValue(syncLoop, false);
            if (!hasRoutine && SyncLoopSourceField?.GetValue(syncLoop) is AudioSource source && source != null) {
                source.Stop();
            }
        }
    }

    /// <summary>
    /// Makes range attackers sing while a remote performer stands in their sing area, and stop once none does.
    /// </summary>
    private static void UpdateRemoteSingingRangeAttackers() {
        foreach (var attacker in SceneSingingRangeAttackers) {
            if (attacker == null) {
                continue;
            }

            var remoteIsInside = attacker.isActiveAndEnabled && IsRemotePerformerInSingRange(attacker);
            var isSinging = RemoteSingingRangeAttackers.Contains(attacker);
            if (remoteIsInside == isSinging) {
                continue;
            }

            if (remoteIsInside) {
                RemoteSingingRangeAttackers.Add(attacker);
                if (RangeAttackerIsHeroPerformingField?.GetValue(attacker) is not true) {
                    RangeAttackerIsHeroPerformingField?.SetValue(attacker, true);
                    RangeAttackerEnsureAnimStartedMethod?.Invoke(attacker, null);
                }

                continue;
            }

            RemoteSingingRangeAttackers.Remove(attacker);
            if (!IsLocalPerformerInSingRange(attacker)) {
                RangeAttackerStopMethod?.Invoke(attacker, null);
            }
        }
    }

    /// <summary>
    /// Checks whether a remote performer stands in the sing area of a range attacker and outside its exclude area, like
    /// <c>RangeAttacker.OnHeroStartedPerforming</c> checks for the local hero.
    /// </summary>
    private static bool IsRemotePerformerInSingRange(RangeAttacker attacker) {
        if (RangeAttackerSingRangeField?.GetValue(attacker) is not TrackTriggerObjects singRange || singRange == null) {
            return false;
        }

        var excludeRange = RangeAttackerSingExcludeRangeField?.GetValue(attacker) as TrackTriggerObjects;
        foreach (var performer in NeedolinCoop.RemotePerformerValues) {
            if (performer.Object == null || !performer.Object.activeInHierarchy) {
                continue;
            }

            var position = performer.Object.transform.position;
            if (!NeedolinRemoteAudio.IsInside(singRange, position)) {
                continue;
            }

            if (excludeRange != null && NeedolinRemoteAudio.IsInside(excludeRange, position)) {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks whether the local player performs inside the sing area of a range attacker, in which case the local
    /// performance keeps it singing.
    /// </summary>
    private static bool IsLocalPerformerInSingRange(RangeAttacker attacker) {
        return HeroPerformanceRegion.IsPerforming &&
               RangeAttackerSingRangeField?.GetValue(attacker) is TrackTriggerObjects singRange && singRange != null &&
               singRange.IsInside;
    }

    /// <summary>
    /// Keeps a <see cref="NeedolinSyncedAudioPlayer"/> playing when the local player stops while a remote player still
    /// performs.
    /// </summary>
    private static void OnSyncedAudioStoppedPerforming(
        Action<NeedolinSyncedAudioPlayer> orig,
        NeedolinSyncedAudioPlayer self
    ) {
        if (!_allowNeedolinListenerStop && NeedolinCoop.HasRemotePerformers) {
            return;
        }

        orig(self);
    }

    /// <summary>
    /// Keeps a <see cref="SyncNeedolinLoop"/> playing when the local player stops while a remote player still performs.
    /// </summary>
    private static void OnSyncLoopNeedolinStopped(Action<SyncNeedolinLoop> orig, SyncNeedolinLoop self) {
        if (!_allowNeedolinListenerStop && NeedolinCoop.HasRemotePerformers) {
            return;
        }

        orig(self);
    }

    /// <summary>
    /// Keeps a <see cref="RangeAttacker"/> singing when the local player stops while a remote performer still stands in
    /// its sing area.
    /// </summary>
    private static void OnRangeAttackerStoppedPerforming(Action<RangeAttacker> orig, RangeAttacker self) {
        if (RemoteSingingRangeAttackers.Contains(self)) {
            return;
        }

        orig(self);
    }
}
