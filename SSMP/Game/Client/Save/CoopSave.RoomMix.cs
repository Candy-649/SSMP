using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using UnityEngine.Audio;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The sound the room asked for, written down as it is asked for, so that a player who is pulled back up hears the
/// room as it is now.
///
/// A death moves the mixers onto settings of its own and plays music of its own, and the game puts them back by loading
/// the room the bench is in. A player who is pulled back up loads nothing, so the rescue put the sound back the way the
/// room set it as it loaded. That is not always how the room is: an arena that started has moved on to settings and
/// music of its own, and the cellar whose fight starts at a table loads silent. A player pulled up in the middle of
/// that fight fought the rest of it with no music at all (USER 10-12, "后来又放音乐了，但是确实有一次没放，好奇怪").
/// What a mixer is set to cannot be read back, so every setting and every music that the game asks for is written
/// down here, except what the death itself asks for (its FSM, "Hero Death Anim"), and the rescue puts back what was
/// written down.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The hook on a mixer being set to one setting.
    /// </summary>
    private Hook? _snapshotHook;

    /// <summary>
    /// The hook on a mixer being set to a blend of settings.
    /// </summary>
    private Hook? _snapshotBlendHook;

    /// <summary>
    /// The hook on music being asked for.
    /// </summary>
    private Hook? _musicCueHook;

    /// <summary>
    /// The last setting asked of each mixer other than by a death of the local player: the settings and how much of
    /// each.
    /// </summary>
    private readonly Dictionary<AudioMixer, (AudioMixerSnapshot[] Snapshots, float[] Weights)> _roomMix = new();

    /// <summary>
    /// The last music asked for other than by a death of the local player.
    /// </summary>
    private MusicCue? _roomMusic;

    /// <summary>
    /// Writes down the sound that the game asks for.
    /// </summary>
    private void RegisterRoomMixHooks() {
        // Not on AudioMixerSnapshot.TransitionTo, which is what the game calls: it is short enough to be copied into
        // the code that calls it, and the room's own code that was compiled before this hook was made - the settings
        // a room gives itself as it loads, the FSM actions that set them - would never reach the hook. All it does is
        // call this one, which is too long to be copied anywhere
        _snapshotHook = CreateHook(
            typeof(AudioMixer).GetMethod(
                "TransitionToSnapshot",
                InstanceFlags,
                null,
                [typeof(AudioMixerSnapshot), typeof(float)],
                null
            ),
            new Action<Action<AudioMixer, AudioMixerSnapshot, float>, AudioMixer, AudioMixerSnapshot, float>(
                OnSnapshotTransition
            )
        );
        _snapshotBlendHook = CreateHook(
            typeof(AudioMixer).GetMethod(
                "TransitionToSnapshots",
                InstanceFlags,
                null,
                [typeof(AudioMixerSnapshot[]), typeof(float[]), typeof(float)],
                null
            ),
            new Action<Action<AudioMixer, AudioMixerSnapshot[], float[], float>, AudioMixer, AudioMixerSnapshot[],
                float[], float>(OnSnapshotBlend)
        );
        _musicCueHook = CreateHook(
            typeof(AudioManager).GetMethod(
                "ApplyMusicCue",
                InstanceFlags,
                null,
                [typeof(MusicCue), typeof(float), typeof(float), typeof(bool)],
                null
            ),
            new Action<Action<AudioManager, MusicCue, float, float, bool>, AudioManager, MusicCue, float, float,
                bool>(OnApplyMusicCue)
        );
    }

    /// <summary>
    /// Whether the death of the local player is what asks for a sound right now: the FSM that plays it out, in every
    /// kind of death there is.
    /// </summary>
    private static bool IsAskedByTheDeath() => FsmExecutionStack.ExecutingFsm?.Name == DeathAnimFsmName;

    /// <summary>
    /// Hook for AudioMixer.TransitionToSnapshot, which AudioMixerSnapshot.TransitionTo calls: writes down the setting
    /// of the mixer.
    /// </summary>
    private void OnSnapshotTransition(
        Action<AudioMixer, AudioMixerSnapshot, float> orig,
        AudioMixer self,
        AudioMixerSnapshot snapshot,
        float time
    ) {
        orig(self, snapshot, time);

        if (self != null && snapshot != null && !IsAskedByTheDeath()) {
            _roomMix[self] = ([snapshot], [1f]);
        }
    }

    /// <summary>
    /// Hook for AudioMixer.TransitionToSnapshots: writes down the blend of settings of the mixer.
    /// </summary>
    private void OnSnapshotBlend(
        Action<AudioMixer, AudioMixerSnapshot[], float[], float> orig,
        AudioMixer self,
        AudioMixerSnapshot[] snapshots,
        float[] weights,
        float time
    ) {
        orig(self, snapshots, weights, time);

        if (self == null || snapshots == null || weights == null || IsAskedByTheDeath()) {
            return;
        }

        // Copies, because the game fills the same arrays again for the next blend. A room with spots that change its
        // sound blends again each time the camera moves, so the copies are kept and filled again when they fit
        if (_roomMix.TryGetValue(self, out var kept) && kept.Snapshots.Length == snapshots.Length &&
            kept.Weights.Length == weights.Length) {
            Array.Copy(snapshots, kept.Snapshots, snapshots.Length);
            Array.Copy(weights, kept.Weights, weights.Length);
        } else {
            _roomMix[self] = ((AudioMixerSnapshot[]) snapshots.Clone(), (float[]) weights.Clone());
        }
    }

    /// <summary>
    /// Hook for AudioManager.ApplyMusicCue: writes down the music.
    /// </summary>
    private void OnApplyMusicCue(
        Action<AudioManager, MusicCue, float, float, bool> orig,
        AudioManager self,
        MusicCue musicCue,
        float delayTime,
        float transitionTime,
        bool applySnapshot
    ) {
        orig(self, musicCue, delayTime, transitionTime, applySnapshot);

        // Music that the game turned away is not playing
        if (musicCue != null && !AudioManager.BlockAudioChange && !IsAskedByTheDeath()) {
            _roomMusic = musicCue;
        }
    }

    /// <summary>
    /// Puts the music and the mixers back the way they were last asked for other than by the death: the room as it is
    /// now. A mixer that nothing asked anything of yet gets the setting the room gives it as it loads.
    /// </summary>
    /// <param name="gameManager">The game manager.</param>
    /// <param name="music">The music to put back if none was written down yet.</param>
    /// <param name="fadeTime">How long the mixers take to get there, in seconds.</param>
    private void RestoreRoomMix(global::GameManager gameManager, MusicCue? music, float fadeTime) {
        // Whatever goes wrong here, the rest of the rescue still has to stand the player up
        try {
            PutBackRoomMix(gameManager, music, fadeTime);
        } catch (Exception e) {
            Logger.Error($"Could not put the sound of the room back:\n{e}");
        }
    }

    /// <inheritdoc cref="RestoreRoomMix" />
    private void PutBackRoomMix(global::GameManager gameManager, MusicCue? music, float fadeTime) {
        // Music written down whose asset is gone since counts as none
        var cue = _roomMusic != null ? _roomMusic : music;
        if (cue != null) {
            // Music that is playing already only stays: the game sees it is the same and does not start it again
            gameManager.AudioManager.ApplyMusicCue(cue, 0f, 0f, false);
        }

        var sceneManager = gameManager.GetSceneManager();
        var customSceneManager = sceneManager == null ? null : sceneManager.GetComponent<CustomSceneManager>();
        if (customSceneManager != null) {
            foreach (var snapshot in new[] {
                         customSceneManager.musicSnapshot,
                         customSceneManager.atmosSnapshot,
                         customSceneManager.enviroSnapshot,
                         customSceneManager.actorSnapshot,
                         customSceneManager.shadeSnapshot
                     }) {
                if (snapshot != null && snapshot.audioMixer is { } mixer && mixer != null &&
                    !_roomMix.ContainsKey(mixer)) {
                    snapshot.TransitionTo(fadeTime);
                }
            }
        }

        // A copy, because setting them writes them down again
        foreach (var pair in new List<KeyValuePair<AudioMixer, (AudioMixerSnapshot[] Snapshots, float[] Weights)>>(
                     _roomMix
                 )) {
            var mixer = pair.Key;
            var (snapshots, weights) = pair.Value;
            if (mixer == null) {
                continue;
            }

            if (snapshots.Length == 1 && snapshots[0] != null) {
                snapshots[0].TransitionTo(fadeTime);
            } else if (snapshots.Length > 1) {
                mixer.TransitionToSnapshots(snapshots, weights, fadeTime);
            }
        }
    }
}
