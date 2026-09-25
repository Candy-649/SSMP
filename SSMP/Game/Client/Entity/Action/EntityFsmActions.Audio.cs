using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using UnityEngine.Audio;
using Logger = SSMP.Logging.Logger;
using Random = UnityEngine.Random;

// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedParameter.Local
#pragma warning disable CS0618
#pragma warning disable CS8600
#pragma warning disable CS8618

namespace SSMP.Game.Client.Entity.Action;

internal static partial class EntityFsmActions {
    #region AudioPlay

    ///<summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AudioPlay action) {
        return true;
    }

    ///<summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, AudioPlay action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var audioSource = gameObject.GetComponent<AudioSource>();
        if (audioSource == null || !audioSource.enabled) {
            return;
        }

        var audioClip = action.oneShotClip.Value as AudioClip;
        if (audioClip == null) {
            audioSource.Play();

            if (action.volume.IsNone) {
                return;
            }

            audioSource.volume = action.volume.Value;
            return;
        }

        if (!action.volume.IsNone) {
            audioSource.PlayOneShot(audioClip, action.volume.Value);
            return;
        }

        audioSource.PlayOneShot(audioClip);
    }

    #endregion

    #region AudioPlaySimple

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AudioPlaySimple action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, AudioPlaySimple action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var audioSource = gameObject.GetComponent<AudioSource>();
        if (audioSource == null) {
            return;
        }

        var audioClip = action.oneShotClip.Value as AudioClip;
        if (audioClip == null) {
            if (!audioSource.isPlaying) {
                audioSource.Play();
            }

            if (!action.volume.IsNone) {
                audioSource.volume = action.volume.Value;
            }
        } else {
            if (!action.volume.IsNone) {
                audioSource.PlayOneShot(audioClip, action.volume.Value);
            } else {
                audioSource.PlayOneShot(audioClip);
            }
        }
    }

    #endregion

    #region AudioPlayerOneShot

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AudioPlayerOneShot action) {
        return !IsPlayerHitSound(action);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, AudioPlayerOneShot action) {
        // TODO: delay?

        if (action.audioClips.Length == 0) {
            return;
        }

        var audioPlayerPrefab = action.audioPlayer.Value;
        var audioPlayer = audioPlayerPrefab.Spawn(
            action.spawnPoint.Value.transform.position, Quaternion.Euler(Vector3.up)
        );

        var audioSource = audioPlayer.GetComponent<AudioSource>();

        action.storePlayer.Value = audioPlayer;

        var randomWeightedIndex = ActionHelpers.GetRandomWeightedIndex(action.weights);
        if (randomWeightedIndex != -1) {
            var audioClip = action.audioClips[randomWeightedIndex];
            if (audioClip != null) {
                audioSource.pitch = Random.Range(action.pitchMin.Value, action.pitchMax.Value);
                audioSource.PlayOneShot(audioClip);
            }
        }

        audioSource.volume = action.volume.Value;
    }

    #endregion

    #region AudioPlayerOneShotSingle

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AudioPlayerOneShotSingle action) {
        return !action.audioPlayer.IsNone && !action.spawnPoint.IsNone && action.spawnPoint.Value != null &&
               !IsPlayerHitSound(action);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, AudioPlayerOneShotSingle action) {
        // TODO: delay?

        if (action.audioPlayer.IsNone || action.spawnPoint.IsNone || action.spawnPoint.Value == null) {
            return;
        }

        var audioPlayer = action.audioPlayer.Value;
        var position = action.spawnPoint.Value.transform.position;
        var up = Vector3.up;

        if (audioPlayer == null) {
            return;
        }

        audioPlayer = audioPlayer.Spawn(position, Quaternion.Euler(up));
        var audioSource = audioPlayer.GetComponent<AudioSource>();
        action.storePlayer.Value = audioPlayer;

        var audioClip = action.audioClip.Value as AudioClip;
        audioSource.pitch = Random.Range(action.pitchMin.Value, action.pitchMax.Value);
        audioSource.volume = action.volume.Value;

        if (audioClip == null) {
            return;
        }

        audioSource.PlayOneShot(audioClip);
    }

    #endregion

    #region SetAudioClip

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetAudioClip action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetAudioClip action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var audioSource = gameObject.GetComponent<AudioSource>();
        if (audioSource == null) {
            return;
        }

        audioSource.clip = action.audioClip.Value as AudioClip;
    }

    #endregion

    #region SetAudioPitch

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetAudioPitch action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetAudioPitch action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var audioSource = gameObject.GetComponent<AudioSource>();
        if (audioSource == null) {
            return;
        }

        audioSource.pitch = action.pitch.Value;
    }

    #endregion

    #region AudioStop

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AudioStop action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, AudioStop action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var audioSource = gameObject.GetComponent<AudioSource>();
        if (audioSource == null) {
            return;
        }

        audioSource.Stop();
    }

    #endregion

    #region SetAudioVolume

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetAudioVolume action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetAudioVolume action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var audioSource = gameObject.GetComponent<AudioSource>();
        if (audioSource == null) {
            return;
        }

        audioSource.volume = action.volume.Value;
    }

    #endregion

    #region AudioPlayRandom

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AudioPlayRandom action) {
        return action.audioClips.Length != 0;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, AudioPlayRandom action) {
        if (action.audioClips.Length == 0) {
            return;
        }

        var audioSource = action.gameObject.Value.GetComponent<AudioSource>();

        var randomWeightedIndex = ActionHelpers.GetRandomWeightedIndex(action.weights);
        if (randomWeightedIndex == -1) {
            return;
        }

        var audioClip = action.audioClips[randomWeightedIndex];
        if (audioClip == null) {
            return;
        }

        audioSource.pitch = Random.Range(action.pitchMin.Value, action.pitchMax.Value);
        audioSource.PlayOneShot(audioClip);
    }

    #endregion

    #region AudioPlayInState

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AudioPlayInState action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, AudioPlayInState action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var audioSource = gameObject.GetComponent<AudioSource>();
        if (audioSource == null) {
            return;
        }

        if (!audioSource.isPlaying) {
            audioSource.Play();
        }

        if (!action.volume.IsNone) {
            audioSource.volume = action.volume.Value;
        }

        void ExitAction() {
            audioSource.Stop();
        }

        new ActionInState {
            Fsm = action.Fsm,
            StateName = action.State.Name,
            ExitAction = ExitAction
        }.Register();
    }

    #endregion

    #region PlayAudioEvent

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, PlayAudioEvent action) {
        return !IsPlayerHitSound(action);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, PlayAudioEvent action) {
        // A one-shot sound is not state, so there is nothing to replay when initializing the entity
        if (data == null) {
            return;
        }

        new AudioEvent {
            Clip = action.audioClip.Value as AudioClip,
            PitchMin = action.pitchMin.Value,
            PitchMax = action.pitchMax.Value,
            Volume = action.volume.Value
        }.SpawnAndPlayOneShot(GetAudioPlayerPrefab(action), GetAudioEventPosition(action), (System.Action) null);
    }

    #endregion

    #region PlayAudioEventRandom

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, PlayAudioEventRandom action) {
        return !IsPlayerHitSound(action);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, PlayAudioEventRandom action) {
        if (data == null) {
            return;
        }

        var values = action.audioClips.Values;
        if (values == null) {
            return;
        }

        // The clip and pitch are picked locally, like for AudioPlayRandom
        new AudioEventRandom {
            Clips = System.Array.ConvertAll(values, value => value as AudioClip),
            PitchMin = action.pitchMin.Value,
            PitchMax = action.pitchMax.Value,
            Volume = action.volume.Value
        }.SpawnAndPlayOneShot(GetAudioPlayerPrefab(action), GetAudioEventPosition(action), (System.Action) null);
    }

    #endregion

    #region ApplyMusicCue

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, ApplyMusicCue action) {
        return true;
    }

    /// <summary>
    /// Applies network data to the FSM action. Each game plays its own music, so the music that a boss starts when its
    /// fight begins played only in the game of the scene host, and whoever came into the room second fought it without
    /// any. The cue and its timing are read off this copy's own action, which holds the same ones: every such action
    /// in the game names its cue and times directly rather than through a variable.
    /// </summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, ApplyMusicCue action) {
        var gameManager = global::GameManager.instance;
        if (action.musicCue.Value is not MusicCue musicCue || musicCue == null || gameManager == null) {
            return;
        }

        gameManager.AudioManager.ApplyMusicCue(musicCue, action.delayTime.Value, action.transitionTime.Value, false);
        Logger.Info(
            $"'{action.Fsm.GameObjectName}' started the music '{musicCue.name}' in the scene host's game, and here too"
        );
    }

    #endregion

    #region TransitionToAudioSnapshot

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, TransitionToAudioSnapshot action) {
        return true;
    }

    /// <summary>
    /// Applies network data to the FSM action: the mix that goes with the music, like the room going quiet for a boss,
    /// which each game likewise sets only for itself.
    /// </summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, TransitionToAudioSnapshot action) {
        if (action.snapshot.Value is not AudioMixerSnapshot snapshot || snapshot == null) {
            return;
        }

        snapshot.TransitionTo(action.transitionTime.Value);
        Logger.Info(
            $"'{action.Fsm.GameObjectName}' changed the sound mix to '{snapshot.name}' in the scene host's game, " +
            "and here too"
        );
    }

    #endregion

    // A creature's voice and many of its sounds are picked from tables of clips, and quite a few of its sounds fade,
    // stop by fading, loop while it stays in a state or switch their source on and off. None of those was done again
    // on the other game, which heard little of a creature beyond its plainest sounds. The copy runs them itself, as its
    // FSM would; a table picks its clip there as it does here, which nobody can tell apart.
    //
    // Setting up a creature's first states only sets its sources up - which clip, whether it loops, where it starts -
    // and plays nothing. The copy may be set up long after the creature left those states, for a player who walks in
    // later, and a loop started then would go on with nothing left to stop it.

    #region AudioPlayRandomVoiceFromTable

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AudioPlayRandomVoiceFromTable action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, AudioPlayRandomVoiceFromTable action) {
        RunMoment(data, action);
    }

    #endregion

    #region AudioPlayRandomVoiceFromTableV2

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AudioPlayRandomVoiceFromTableV2 action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, AudioPlayRandomVoiceFromTableV2 action) {
        RunMoment(data, action);
    }

    #endregion

    #region AudioPlayRandomVoiceFromTableBool

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AudioPlayRandomVoiceFromTableBool action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, AudioPlayRandomVoiceFromTableBool action) {
        RunMoment(data, action);
    }

    #endregion

    #region AudioPlayRandomVoice

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AudioPlayRandomVoice action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, AudioPlayRandomVoice action) {
        RunMoment(data, action);
    }

    #endregion

    #region AudioPlayRandomSingle

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AudioPlayRandomSingle action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, AudioPlayRandomSingle action) {
        RunMoment(data, action);
    }

    #endregion

    #region PlayRandomAudioClipTable

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, PlayRandomAudioClipTable action) {
        return !IsPlayerHitSound(action);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, PlayRandomAudioClipTable action) {
        RunMoment(data, action);
    }

    #endregion

    #region PlayRandomAudioClipTableV2

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, PlayRandomAudioClipTableV2 action) {
        return !IsPlayerHitSound(action);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, PlayRandomAudioClipTableV2 action) {
        RunMoment(data, action);
    }

    #endregion

    #region PlayRandomAudioClipTableV3

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, PlayRandomAudioClipTableV3 action) {
        return !IsPlayerHitSound(action);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, PlayRandomAudioClipTableV3 action) {
        RunMoment(data, action);
    }

    #endregion

    #region PlayAudioEventRandomBool

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, PlayAudioEventRandomBool action) {
        return !IsPlayerHitSound(action);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, PlayAudioEventRandomBool action) {
        RunMoment(data, action);
    }

    #endregion

    #region AudioPlaySimpleV2

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AudioPlaySimpleV2 action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, AudioPlaySimpleV2 action) {
        RunMoment(data, action);
    }

    #endregion

    #region AudioPlayV2

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AudioPlayV2 action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, AudioPlayV2 action) {
        RunMoment(data, action);
    }

    #endregion

    #region AudioPlayInStateConditional

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AudioPlayInStateConditional action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, AudioPlayInStateConditional action) {
        RunMoment(data, action);
    }

    #endregion

    #region PlayEnemySingAudio

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, PlayEnemySingAudio action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, PlayEnemySingAudio action) {
        RunMoment(data, action);
    }

    #endregion

    #region SetRandomAudioClipFromTable

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetRandomAudioClipFromTable action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, SetRandomAudioClipFromTable action) {
        if (data != null) {
            RunInState(action);
            return;
        }

        // Only the clip is picked when the creature's first states are set up: the action always makes its source
        // loop, and playing it too would start a loop that the creature may have stopped long ago
        var table = action.Table.Value as RandomAudioClipTable;
        var owner = action.AudioSource.Value;
        var source = owner != null ? owner.GetComponent<AudioSource>() : null;
        if (table == null || source == null) {
            return;
        }

        source.clip = table.SelectClip(true);
        source.volume = table.SelectVolume();
        source.pitch = table.SelectPitch();
        source.loop = true;
    }

    #endregion

    #region SetAudioSource

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetAudioSource action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, SetAudioSource action) {
        RunInState(action);
    }

    #endregion

    #region SetAudioLoop

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetAudioLoop action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, SetAudioLoop action) {
        RunInState(action);
    }

    #endregion

    #region RandomiseAudioPosition

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, RandomiseAudioPosition action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, RandomiseAudioPosition action) {
        RunInState(action);
    }

    #endregion

    #region AudioPause

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AudioPause action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, AudioPause action) {
        RunMoment(data, action);
    }

    #endregion

    #region AudioStopV2

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AudioStopV2 action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, AudioStopV2 action) {
        RunMoment(data, action);
    }

    #endregion

    #region FadeAudio

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, FadeAudio action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, FadeAudio action) {
        RunMoment(data, action);
    }

    #endregion

    #region FadeAudioV2

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, FadeAudioV2 action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, FadeAudioV2 action) {
        RunMoment(data, action);
    }

    #endregion

    /// <summary>
    /// Gets the audio player prefab of a PlayAudioEvent action, or null for the default prefab the action falls
    /// back to.
    /// </summary>
    private static AudioSource? GetAudioPlayerPrefab(PlayAudioEventBase action) {
        return action.audioPlayerPrefab.IsNone ? null : action.audioPlayerPrefab.Value as AudioSource;
    }

    /// <summary>
    /// Gets the position a PlayAudioEvent action plays at: its spawn position, offset by its spawn point if set.
    /// </summary>
    private static Vector3 GetAudioEventPosition(PlayAudioEventBase action) {
        var position = action.spawnPosition.Value;
        var spawnPoint = FSMUtility.GetSafe(action.spawnPoint, action);
        if (spawnPoint != null) {
            position += spawnPoint.transform.position;
        }

        return position;
    }
}
