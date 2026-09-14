using System.Collections.Generic;
using System.Reflection;
using GlobalSettings;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using AnimationClip = SSMP.Animation.AnimationClip;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client;

/// <summary>
/// Plays the needolin songs of remote players at their position. The loops come from the local hero's needolin FSM
/// template: the default song, or the song of a scene's needolin override area that the player stands in, with the high
/// and low tunes faded in over it.
/// </summary>
internal static class NeedolinRemoteAudio {
    /// <summary>
    /// Name of the FSM template that plays the hero's needolin.
    /// </summary>
    private const string NeedolinTemplateName = "needolin_play_sub";

    /// <summary>
    /// State of the needolin template that starts the default loop with <see cref="StartNeedolinAudioLoop"/>.
    /// </summary>
    private const string LoopStateName = "Start Needolin Proper";

    /// <summary>
    /// State of the needolin template that starts the high tune, played with the "Needolin Play High" clips.
    /// </summary>
    private const string HighTuneStateName = "Needolin FT In";

    /// <summary>
    /// State of the needolin template that starts the low tune, played with the "Needolin Play Low" clips.
    /// </summary>
    private const string LowTuneStateName = "Needolin Mem In";

    /// <summary>
    /// Path of the local hero's needolin audio source, used to keep a remote song in time with the local one.
    /// </summary>
    private const string LocalNeedolinSourcePath = "Sounds/Needolin";

    /// <summary>
    /// How long, in seconds, to wait before searching the needolin template again after it was not found.
    /// </summary>
    private const float ClipSearchIntervalSeconds = 5f;

    /// <summary>
    /// Binding flags for the serialized fields of <see cref="OverrideNeedolinLoop"/>.
    /// </summary>
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Reflected field with the trigger area in which a needolin override applies.
    /// </summary>
    private static readonly FieldInfo? OverrideHeroRangeField =
        typeof(OverrideNeedolinLoop).GetField("heroRange", InstanceFlags);

    /// <summary>
    /// Reflected field with the loop that a needolin override plays instead of the default one.
    /// </summary>
    private static readonly FieldInfo? OverrideNeedolinClipField =
        typeof(OverrideNeedolinLoop).GetField("needolinClip", InstanceFlags);

    /// <summary>
    /// Reflected field with the scene audio source that a needolin override keeps the song in time with.
    /// </summary>
    private static readonly FieldInfo? OverrideSyncToSourceField =
        typeof(OverrideNeedolinLoop).GetField("syncToSource", InstanceFlags);

    /// <summary>
    /// Reflected field that makes a needolin override play its loop only once.
    /// </summary>
    private static readonly FieldInfo? OverrideDontLoopField =
        typeof(OverrideNeedolinLoop).GetField("dontLoop", InstanceFlags);

    /// <summary>
    /// Reflected property that makes a needolin override keep the song in time with its scene audio source.
    /// </summary>
    private static readonly PropertyInfo? OverrideDoSyncProperty =
        typeof(OverrideNeedolinLoop).GetProperty("DoSync", InstanceFlags);

    /// <summary>
    /// The song players of remote players, by player object.
    /// </summary>
    private static readonly Dictionary<GameObject, NeedolinRemoteAudioPlayer> Players = new();

    /// <summary>
    /// The default needolin loop.
    /// </summary>
    private static AudioClip? _loopClip;

    /// <summary>
    /// The high needolin tune.
    /// </summary>
    private static AudioClip? _highClip;

    /// <summary>
    /// The low needolin tune.
    /// </summary>
    private static AudioClip? _lowClip;

    /// <summary>
    /// The time after which the needolin template may be searched again.
    /// </summary>
    private static float _nextClipSearchTime;

    /// <summary>
    /// Whether a missing needolin template was already logged.
    /// </summary>
    private static bool _loggedMissingClips;

    /// <summary>
    /// Starts, changes or stops the song of a remote player from an animation clip they started playing.
    /// </summary>
    /// <param name="playerObject">The player object of the remote player.</param>
    /// <param name="clip">The animation clip that the remote player plays.</param>
    public static void OnRemoteAnimation(GameObject playerObject, AnimationClip clip) {
        Players.TryGetValue(playerObject, out var player);

        var tune = GetTune(clip);
        if (tune == null) {
            if (player != null) {
                player.Stop(false);
            }

            return;
        }

        if (!TryFindClips()) {
            return;
        }

        if (player == null) {
            player = NeedolinRemoteAudioPlayer.Create(playerObject);
            Players[playerObject] = player;
        }

        if (!player.IsPlaying) {
            GetLoop(playerObject.transform.position, out var loopClip, out var loop, out var syncSource);
            player.Play(loopClip, loop, syncSource);
        }

        player.SetTune(tune.Value, _highClip, _lowClip);
    }

    /// <summary>
    /// Gets the loop audio source of a remote player whose song plays, to keep scene audio in time with it.
    /// </summary>
    /// <returns>The audio source, or null if no remote player's loop plays.</returns>
    public static AudioSource? GetPlayingLoopSource() {
        foreach (var player in Players.Values) {
            if (player != null && player.IsPlaying && player.LoopSource.isPlaying) {
                return player.LoopSource;
            }
        }

        return null;
    }

    /// <summary>
    /// Stops the song of a remote player at once, for example because they left the scene.
    /// </summary>
    /// <param name="playerObject">The player object of the remote player.</param>
    public static void Stop(GameObject playerObject) {
        if (Players.TryGetValue(playerObject, out var player) && player != null) {
            player.Stop(true);
        }
    }

    /// <summary>
    /// Stops the songs of all remote players at once and forgets song players of destroyed player objects.
    /// </summary>
    public static void StopAll() {
        var destroyed = new List<GameObject>();
        foreach (var pair in Players) {
            if (pair.Key == null || pair.Value == null) {
                destroyed.Add(pair.Key!);
                continue;
            }

            pair.Value.Stop(true);
        }

        foreach (var playerObject in destroyed) {
            Players.Remove(playerObject);
        }
    }

    /// <summary>
    /// Gets which tune a needolin animation clip plays, following the clips that the needolin template plays in its
    /// loop, high tune and low tune states.
    /// </summary>
    /// <param name="clip">The animation clip.</param>
    /// <returns>The tune, or null if the clip is not a needolin play clip.</returns>
    private static NeedolinTune? GetTune(AnimationClip clip) {
        return clip switch {
            AnimationClip.NeedolinPlay or AnimationClip.NeedolinSitPlay or AnimationClip.NeedolinTurn
                or AnimationClip.NeedolinSitTurn => NeedolinTune.Loop,
            AnimationClip.NeedolinPlayHigh or AnimationClip.NeedolinPlayHighTransition => NeedolinTune.High,
            AnimationClip.NeedolinPlayLow or AnimationClip.NeedolinPlayLowTransition => NeedolinTune.Low,
            _ => null
        };
    }

    /// <summary>
    /// Finds the needolin loops in the hero's needolin template.
    /// </summary>
    /// <returns>true if at least the default loop was found; otherwise false.</returns>
    private static bool TryFindClips() {
        if (_loopClip != null) {
            return true;
        }

        if (Time.unscaledTime < _nextClipSearchTime) {
            return false;
        }

        _nextClipSearchTime = Time.unscaledTime + ClipSearchIntervalSeconds;

        foreach (var template in Resources.FindObjectsOfTypeAll<FsmTemplate>()) {
            if (template == null || template.name != NeedolinTemplateName || template.fsm == null) {
                continue;
            }

            _loopClip = GetFirstAction<StartNeedolinAudioLoop>(template.fsm, LoopStateName)?.DefaultClip.Value
                as AudioClip;
            _highClip = GetFirstAction<SetAudioClip>(template.fsm, HighTuneStateName)?.audioClip.Value as AudioClip;
            _lowClip = GetFirstAction<SetAudioClip>(template.fsm, LowTuneStateName)?.audioClip.Value as AudioClip;
            break;
        }

        if (_loopClip == null && !_loggedMissingClips) {
            _loggedMissingClips = true;
            Logger.Warn("Could not find the needolin loop in the hero's needolin template");
        }

        return _loopClip != null;
    }

    /// <summary>
    /// Gets the first action of a type in a state of an FSM.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="stateName">The name of the state.</param>
    /// <typeparam name="T">The type of the action.</typeparam>
    /// <returns>The action, or null if the state or action does not exist.</returns>
    private static T? GetFirstAction<T>(HutongGames.PlayMaker.Fsm fsm, string stateName) where T : FsmStateAction {
        var state = fsm.GetState(stateName);
        if (state == null) {
            return null;
        }

        foreach (var action in state.Actions) {
            if (action is T typedAction) {
                return typedAction;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the loop that a player at the given position plays, like <c>OverrideNeedolinLoop.StartSyncedAudio</c> does
    /// for the local hero: the loop of the needolin override area the player stands in, or the default loop.
    /// </summary>
    /// <param name="position">The position of the player.</param>
    /// <param name="clip">The loop, or null if the override area silences the needolin.</param>
    /// <param name="loop">Whether the loop repeats.</param>
    /// <param name="syncSource">The audio source to keep the loop in time with, if any.</param>
    private static void GetLoop(Vector2 position, out AudioClip? clip, out bool loop, out AudioSource? syncSource) {
        foreach (var loopOverride in Object.FindObjectsByType<OverrideNeedolinLoop>(FindObjectsSortMode.None)) {
            if (!loopOverride.isActiveAndEnabled ||
                OverrideHeroRangeField?.GetValue(loopOverride) is not TrackTriggerObjects heroRange ||
                heroRange == null ||
                !IsInside(heroRange, position)) {
                continue;
            }

            clip = OverrideNeedolinClipField?.GetValue(loopOverride) as AudioClip;
            loop = OverrideDontLoopField?.GetValue(loopOverride) is not true;
            syncSource = OverrideDoSyncProperty?.GetValue(loopOverride) is true
                ? OverrideSyncToSourceField?.GetValue(loopOverride) as AudioSource
                : null;
            return;
        }

        clip = _loopClip;
        loop = true;

        // Stay in time with the local player's song when both play the default loop
        var hero = HeroController.instance;
        var localSourceTransform = hero != null ? hero.transform.Find(LocalNeedolinSourcePath) : null;
        var localSource = localSourceTransform != null ? localSourceTransform.GetComponent<AudioSource>() : null;
        syncSource = localSource != null && localSource.isPlaying && localSource.clip == clip ? localSource : null;
    }

    /// <summary>
    /// Checks whether a position is inside one of the colliders of a trigger area.
    /// </summary>
    /// <param name="area">The trigger area.</param>
    /// <param name="position">The position.</param>
    /// <returns>true if the position is inside the area; otherwise false.</returns>
    public static bool IsInside(Component area, Vector2 position) {
        foreach (var areaCollider in area.GetComponents<Collider2D>()) {
            if (areaCollider.enabled && areaCollider.OverlapPoint(position)) {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The tunes of the needolin.
/// </summary>
internal enum NeedolinTune {
    /// <summary>
    /// The loop played while holding the needolin.
    /// </summary>
    Loop,

    /// <summary>
    /// The high tune.
    /// </summary>
    High,

    /// <summary>
    /// The low tune.
    /// </summary>
    Low
}

/// <summary>
/// Plays the needolin song of one remote player: a loop with the high and low tunes faded in over it.
/// </summary>
internal sealed class NeedolinRemoteAudioPlayer : MonoBehaviour {
    /// <summary>
    /// How fast, in volume per second, the tunes fade when the player switches between them.
    /// </summary>
    private const float TuneFadeRate = 4f;

    /// <summary>
    /// How fast, in volume per second, the song fades out when the player stops playing.
    /// </summary>
    private const float StopFadeRate = 2f;

    /// <summary>
    /// Audio source for the loop.
    /// </summary>
    private AudioSource _loopSource = null!;

    /// <summary>
    /// Audio source for the high tune.
    /// </summary>
    private AudioSource _highSource = null!;

    /// <summary>
    /// Audio source for the low tune.
    /// </summary>
    private AudioSource _lowSource = null!;

    /// <summary>
    /// The tune that the player plays.
    /// </summary>
    private NeedolinTune _tune;

    /// <summary>
    /// Whether the player is playing; the song fades out after this turns false.
    /// </summary>
    public bool IsPlaying { get; private set; }

    /// <summary>
    /// The audio source that plays the loop.
    /// </summary>
    public AudioSource LoopSource => _loopSource;

    /// <summary>
    /// Creates a song player under a remote player object, with audio sources set up like the game's default positional
    /// audio source.
    /// </summary>
    /// <param name="playerObject">The player object of the remote player.</param>
    /// <returns>The song player.</returns>
    public static NeedolinRemoteAudioPlayer Create(GameObject playerObject) {
        var audioObject = new GameObject("Needolin Audio");
        audioObject.transform.SetParent(playerObject.transform, false);

        var player = audioObject.AddComponent<NeedolinRemoteAudioPlayer>();
        player._loopSource = CreateSource(audioObject);
        player._highSource = CreateSource(audioObject);
        player._lowSource = CreateSource(audioObject);
        player.enabled = false;
        return player;
    }

    /// <summary>
    /// Adds a looping audio source that copies the positional settings of the game's default audio source.
    /// </summary>
    private static AudioSource CreateSource(GameObject audioObject) {
        var source = audioObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = true;
        source.volume = 0f;

        var prefab = Audio.DefaultAudioSourcePrefab;
        if (prefab != null) {
            source.outputAudioMixerGroup = prefab.outputAudioMixerGroup;
            source.spatialBlend = prefab.spatialBlend;
            source.rolloffMode = prefab.rolloffMode;
            source.minDistance = prefab.minDistance;
            source.maxDistance = prefab.maxDistance;
            source.dopplerLevel = prefab.dopplerLevel;
            if (prefab.rolloffMode == AudioRolloffMode.Custom) {
                source.SetCustomCurve(
                    AudioSourceCurveType.CustomRolloff,
                    prefab.GetCustomCurve(AudioSourceCurveType.CustomRolloff)
                );
            }
        }

        return source;
    }

    /// <summary>
    /// Starts the song with the given loop.
    /// </summary>
    /// <param name="loopClip">The loop, or null to play only the tunes.</param>
    /// <param name="loop">Whether the loop repeats.</param>
    /// <param name="syncSource">The audio source to start the loop in time with, if any.</param>
    public void Play(AudioClip? loopClip, bool loop, AudioSource? syncSource) {
        StopSources();

        IsPlaying = true;
        _tune = NeedolinTune.Loop;
        enabled = true;

        _loopSource.clip = loopClip;
        _loopSource.loop = loop;
        if (loopClip == null) {
            return;
        }

        _loopSource.volume = 1f;
        _loopSource.Play();
        SyncTime(_loopSource, syncSource);
    }

    /// <summary>
    /// Switches to a tune, fading the others out.
    /// </summary>
    /// <param name="tune">The tune to play.</param>
    /// <param name="highClip">The high tune.</param>
    /// <param name="lowClip">The low tune.</param>
    public void SetTune(NeedolinTune tune, AudioClip? highClip, AudioClip? lowClip) {
        if (tune == _tune) {
            return;
        }

        _tune = tune;

        var (source, clip) = tune switch {
            NeedolinTune.High => (_highSource, highClip),
            NeedolinTune.Low => (_lowSource, lowClip),
            _ => (null, null)
        };
        if (source == null || clip == null || source.isPlaying && source.clip == clip) {
            return;
        }

        source.clip = clip;
        source.volume = 0f;
        source.Play();
        SyncTime(source, _loopSource);
    }

    /// <summary>
    /// Stops the song.
    /// </summary>
    /// <param name="immediate">Whether to cut the song off instead of fading it out.</param>
    public void Stop(bool immediate) {
        IsPlaying = false;
        if (immediate) {
            StopSources();
            enabled = false;
        }
    }

    /// <summary>
    /// Fades the tunes towards the one being played, and ends the song once it has faded out.
    /// </summary>
    private void Update() {
        var step = (IsPlaying ? TuneFadeRate : StopFadeRate) * Time.deltaTime;

        var audible = Fade(_loopSource, IsPlaying && _tune == NeedolinTune.Loop, step);
        audible |= Fade(_highSource, IsPlaying && _tune == NeedolinTune.High, step);
        audible |= Fade(_lowSource, IsPlaying && _tune == NeedolinTune.Low, step);

        if (!IsPlaying && !audible) {
            StopSources();
            enabled = false;
        }
    }

    /// <summary>
    /// Stops the song when the player object is deactivated, for example when the player is recycled.
    /// </summary>
    private void OnDisable() {
        IsPlaying = false;
        StopSources();
    }

    /// <summary>
    /// Moves the volume of an audio source towards full or silent.
    /// </summary>
    /// <returns>true if the source can still be heard; otherwise false.</returns>
    private static bool Fade(AudioSource source, bool isOn, float step) {
        source.volume = Mathf.MoveTowards(source.volume, isOn ? 1f : 0f, step);
        return source.isPlaying && source.volume > 0f;
    }

    /// <summary>
    /// Puts an audio source at the same point in its clip as another, like <c>AudioPlaySynced</c> and
    /// <c>OverrideNeedolinLoop.StartSyncedAudio</c> do.
    /// </summary>
    private static void SyncTime(AudioSource source, AudioSource? syncSource) {
        if (syncSource == null || syncSource.clip == null || !syncSource.isPlaying || source.clip == null) {
            return;
        }

        if (source.clip.frequency == syncSource.clip.frequency && source.clip.samples > 0) {
            source.timeSamples = syncSource.timeSamples % source.clip.samples;
        } else if (source.clip.length > 0f) {
            source.time = syncSource.time % source.clip.length;
        }
    }

    /// <summary>
    /// Stops all audio sources of the song.
    /// </summary>
    private void StopSources() {
        StopSource(_loopSource);
        StopSource(_highSource);
        StopSource(_lowSource);
    }

    /// <summary>
    /// Stops an audio source and silences it.
    /// </summary>
    private static void StopSource(AudioSource? source) {
        if (source == null) {
            return;
        }

        source.Stop();
        source.volume = 0f;
    }
}
