using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Save;
using SSMP.Internals;
using SSMP.Util;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SSMP.Animation.Effects.SilkSkills;

/// <summary>
/// Effect class for the Rune Rage Silk Skill.
/// </summary>
internal class RuneRage : BaseSilkSkill {
    /// <summary>
    /// Name of the game object for the normal antic.
    /// </summary>
    private const string AnticName = "attack_bomb_cast_antic_thread";

    /// <summary>
    /// Name of the game object for the volt filament antic.
    /// </summary>
    private const string AnticVoltName = "antic_thread_zap";

    /// <summary>
    /// Where the number of the cast is in the effect info, after the flags of
    /// <see cref="BaseSilkSkill.GetEffectFlags"/>, and where the places of its clusters begin after it: x and y of
    /// each, as floats.
    /// </summary>
    private const int CastAt = 1;

    /// <inheritdoc cref="CastAt"/>
    private const int ClustersAt = CastAt + 2;

    /// <summary>
    /// How many bytes the place of a cluster takes.
    /// </summary>
    private const int ClusterSize = 8;


    /// <summary>
    /// Keeps track of if this instance is made to run the antic or not.
    /// </summary>
    public bool IsAntic = false;

    /// <summary>
    /// Cached object for Hornet's normal Rune Rage burst.
    /// </summary>
    private static GameObject? _localRuneBlast;

    /// <summary>
    /// Cached object for Hornet's volt filament Rune Rage burst.
    /// </summary>
    private static GameObject? _localRuneBlastVolt;

    /// <summary>
    /// Cached prefab of a cluster of Rune Rage blasts.
    /// </summary>
    private static GameObject? _clusterPrefab;

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        var isVolt = IsVolt(effectInfo);
        var isShaman = crestType == CrestType.Shaman;

        // Play antic if appropriate
        if (IsAntic) {
            PlayRageAntic(playerObject, isVolt);
            return;
        }

        // Decode the cast and where its clusters are from effect info
        var positions = DecodeClusters(effectInfo, playerObject, out var cast);

        PlaySonar(playerObject, isVolt, isShaman);

        // There are runes to spawn; do at the same time as the sonar
        if (positions.Count > 0) {
            PlayRuneRage(playerObject, cast, positions, isVolt, isShaman);
        }
    }

    /// <summary>
    /// Plays the silk-based antic for Rune Rage.
    /// </summary>
    /// <param name="playerObject">The player using the effect.</param>
    /// <param name="isVolt">Whether the volt filament effects should be used.</param>
    private static void PlayRageAntic(GameObject playerObject, bool isVolt) {
        PlayHornetAttackSound(playerObject);

        // Play antic
        if (TryGetAntic(playerObject, out var antic)) {
            antic.SetActive(false);
            antic.SetActive(true);

            var volt = antic.FindGameObjectInChildren(AnticVoltName);
            if (volt != null) {
                volt.SetActive(false);
                volt.SetActive(isVolt);
            }
        }

        var fsm = GetSkillFsm();
        
        // Play volt audio
        if (isVolt) {
            var voltAntic = fsm.GetFirstAction<PlayAudioEvent>("S Bomb Zap FX");
            AudioUtil.PlayAudio(voltAntic, playerObject);
        }

        // Play normal audio
        var runeAnticAudio = fsm.GetAction<PlayAudioEvent>("Silk Bomb Start", 15);
        if (runeAnticAudio != null) {
            AudioUtil.PlayAudio(runeAnticAudio, playerObject);
        }
    }

    /// <summary>
    /// Plays the sonar blast effect. Purely visual.
    /// </summary>
    /// <param name="playerObject">The player using the effect.</param>
    /// <param name="isVolt">Whether the volt filament effects should be used.</param>
    /// <param name="isShaman">Whether the shaman crest effects should be used.</param>
    private static void PlaySonar(GameObject playerObject, bool isVolt, bool isShaman) {
        var fsm = GetSkillFsm();

        // Play general audio
        var runeBurstAudio = fsm.GetFirstAction<PlayAudioEvent>("Initial Silk Cost");
        AudioUtil.PlayAudio(runeBurstAudio, playerObject);

        // Play volt audio
        if (isVolt) {
            var zapAudioBug = fsm.GetFirstAction<PlayAudioEvent>("S Bomb Zap FX 2");
            AudioUtil.PlayAudio(zapAudioBug, playerObject);
        }

        // Spawn sonar, picking the right one for the volt filament setting
        var sonarPicker = fsm.GetFirstAction<BoolTestToGameObject>("Sonar Cast Effects");

        GameObject localSonar;

        if (isVolt) {
            localSonar = sonarPicker.TrueGameObject.Value;
        } else {
            localSonar = sonarPicker.FalseGameObject.Value;
        }

        // Spawn and remove components
        var sonarParent = EffectUtils.SpawnGlobalPoolObject(localSonar, playerObject.transform, 2f);
        if (sonarParent != null) {
            sonarParent.DestroyComponentsInChildren<CameraControlAnimationEvents>();
        }

        if (!isShaman) return;

        // Spawn shaman effect and remove components
        var localShaman = fsm.GetAction<SpawnObjectFromGlobalPool>("Sonar Cast Effects", 6);
        if (localShaman == null) return;

        var shaman = EffectUtils.SpawnGlobalPoolObject(localShaman, playerObject.transform, 0.7f);
        if (shaman == null) return;

        shaman.DestroyComponent<HeroShamanRuneEffect>();
        shaman.DestroyComponent<CameraControlAnimationEvents>();
    }

    /// <summary>
    /// Spawns clusters of Rune Rage blasts at the given positions.
    /// </summary>
    /// <param name="playerObject">The character of the player whose blasts they are.</param>
    /// <param name="cast">The number of the cast in the thrower's game, which names its clusters (see
    /// <see cref="RuneRolls"/>).</param>
    /// <param name="positions">The positions to spawn blast clusters at.</param>
    /// <param name="isVolt">Whether the volt filament effects should be used.</param>
    /// <param name="isShaman">Whether the shaman crest effects should be used.</param>
    private void PlayRuneRage(
        GameObject playerObject,
        ushort cast,
        List<Vector3> positions,
        bool isVolt,
        bool isShaman
    ) {
        // Spawn clusters at each position
        for (var i = 0; i < positions.Count; i++) {
            MonoBehaviourUtil.Instance.StartCoroutine(
                PlayRuneCluster(playerObject, RuneRolls.ClusterName(cast, i), positions[i], isVolt, isShaman)
            );
        }
    }

    /// <summary>
    /// Gets the Rune Rage antic effect.
    /// </summary>
    /// <param name="playerObject">The player that is using the antic.</param>
    /// <param name="antic">The found or created antic.</param>
    /// <returns>True if the effect was found, otherwise false.</returns>
    private static bool TryGetAntic(GameObject playerObject, [MaybeNullWhen(false)] out GameObject antic) {
        // Find or create the antic object
        var created = FindOrCreateSkill(playerObject, AnticName, out var anticObj);
        if (anticObj == null) {
            antic = null;
            return false;
        }

        antic = anticObj;
        if (!created) return true;

        // Remove problematic component from newly created object
        antic.DestroyComponent<ToolEquipChecker>();

        return true;
    }

    /// <summary>
    /// Gets the prefab for a single Rune Rage blast.
    /// </summary>
    /// <param name="isVolt">Whether the volt filament effects should be used.</param>
    /// <returns>The blast prefab, if found.</returns>
    private static GameObject? TryGetLocalBlast(bool isVolt) {
        // Return existing if possible
        if (isVolt) {
            if (_localRuneBlastVolt) return _localRuneBlastVolt;
        } else if (_localRuneBlast) {
            return _localRuneBlast;
        }

        // Get the rune cluster object from the FSM
        var cluster = TryGetClusterPrefab();
        if (cluster == null) return null;

        // Find the individual rune from within the cluster FSM
        var clusterFsm = cluster.LocateMyFSM("Control");
        if (clusterFsm == null) return null;

        var blaster = clusterFsm.GetFirstAction<BoolTestToGameObject>("Do Explosions");

        // Fill out both blast prefabs
        _localRuneBlastVolt = blaster.TrueGameObject.Value;
        _localRuneBlast = blaster.FalseGameObject.Value;

        // Return correct blast
        return isVolt ? _localRuneBlastVolt : _localRuneBlast;
    }

    /// <summary>
    /// Gets the prefab of a cluster of Rune Rage blasts.
    /// </summary>
    /// <returns>The cluster prefab, if found.</returns>
    private static GameObject? TryGetClusterPrefab() {
        if (_clusterPrefab) return _clusterPrefab;

        _clusterPrefab = GetSkillFsm().GetFirstAction<SpawnObjectFromGlobalPool>("Blast Enemy").gameObject.Value;
        return _clusterPrefab;
    }

    /// <summary>
    /// Plays a cluster of Rune Rage blasts as its state machine does in the thrower's game, rolling as it rolls there
    /// under the name it has there (see <see cref="RuneRolls"/>): it waits, is turned, and puts its blasts down at
    /// once, each at a point of the cluster moved by a shift and waiting a time of its own before it goes off.
    /// </summary>
    /// <param name="playerObject">The character of the player whose blasts they are.</param>
    /// <param name="name">The name of the cluster.</param>
    /// <param name="position">The position of the cluster.</param>
    /// <param name="isVolt">Whether the volt filament effects should be used.</param>
    /// <param name="isShaman">Whether the shaman crest effects should be used.</param>
    private IEnumerator PlayRuneCluster(
        GameObject playerObject,
        string name,
        Vector3 position,
        bool isVolt,
        bool isShaman
    ) {
        if (RuneRolls.GetPlan(TryGetClusterPrefab()) is not { } plan) yield break;

        var wait = plan.Wait(name);
        if (wait > 0f) {
            yield return new WaitForSeconds(wait);
        }

        // The blasts are made under an object of the room, so that they go with the room rather than with the player
        // objects, which outlast it
        var spawnPoint = new GameObject("Rune Cluster").transform;
        spawnPoint.position = position;

        var turn = plan.Turn(name);
        for (var i = 0; i < plan.Blasts.Count; i++) {
            var blast = plan.Blasts[i];
            CreateBlast(
                playerObject,
                spawnPoint,
                isVolt,
                isShaman,
                plan.Place(name, blast, position, turn),
                blast.WaitTime,
                RuneRolls.BlastName(name, i)
            );
        }

        Object.Destroy(spawnPoint.gameObject);
    }

    /// <summary>
    /// Spawns a single Rune Rage blast.
    /// </summary>
    /// <param name="playerObject">The character of the player whose blast it is, which it is drawn as, though it is
    /// made under the room.</param>
    /// <param name="spawnTransform">The initial parent to use for spawning.</param>
    /// <param name="isVolt">Whether the volt filament effects should be used.</param>
    /// <param name="isShaman">Whether the shaman crest effects should be used.</param>
    /// <param name="position">The final position of the blast.</param>
    /// <param name="waitTime">How long the blast waits before it goes off.</param>
    /// <param name="name">The name the blast rolls under (see <see cref="RuneRolls"/>).</param>
    private void CreateBlast(
        GameObject playerObject,
        Transform spawnTransform,
        bool isVolt,
        bool isShaman,
        Vector3 position,
        float waitTime,
        string name
    ) {
        var localBlast = TryGetLocalBlast(isVolt);
        if (localBlast == null) return;

        // Create copy of blast and set position
        var blast = EffectUtils.SpawnGlobalPoolObject(localBlast, spawnTransform, 3, character: playerObject);
        if (blast == null) return;

        // Unturned, as the cluster puts its blasts down
        blast.transform.SetPositionAndRotation(position, Quaternion.identity);

        // How large it is, as it rolls in the thrower's game
        SharedDice.RollByName(blast, name);

        // Add damager
        var damager = blast
            .FindGameObjectInChildren("Blast")?
            .FindGameObjectInChildren("damager");

        if (damager != null) {
            SetDamageHeroStateCalculated(damager, ServerSettings.RuneRageDamage, isVolt, isShaman);
        }

        // Remove extra recycling component
        if (blast.TryGetComponent<RecycleResetHandler>(out var recycler)) {
            recycler.resetActions = null;
        }

        // Set shaman effect
        if (blast.TryGetComponent<HeroShamanRuneEffect>(out var rune)) {
            var runeObj = rune.rune;
            Object.DestroyImmediate(rune);

            if (runeObj) {
                runeObj.SetActive(isShaman);

                // Remove extra bloom object
                if (isShaman) {
                    var bloom = runeObj.FindGameObjectInChildren("Shaman Rune Camera Bloom");
                    if (bloom) {
                        bloom.SetActive(false);
                    }
                }
            }

        }

        // Remove camera controller
        blast.DestroyComponentsInChildren<CameraControlAnimationEvents>();

        // Cull while offscreen (fixes bug)
        var animator = blast.GetComponentInChildren<Animator>();
        if (animator) {
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        // Remove recycle action
        var fsm = blast.LocateMyFSM("Control");
        var end = fsm.GetState("End");
        end.Actions = [];
        end.SaveActions();

        // How long it waits, as the cluster sets it
        var wait = fsm.FsmVariables.FindFsmFloat("Wait Time");
        if (wait != null) {
            wait.Value = waitTime;
        }
    }

    /// <summary>
    /// Makes the effect info of a cast: the flags, the number of the cast and where each of its clusters is in the
    /// room, as many as the effect info has room for.
    /// </summary>
    /// <param name="flags">The flags of the skill (see <see cref="BaseSilkSkill.GetEffectFlags"/>).</param>
    /// <param name="cast">The number of the cast, which names its clusters (see <see cref="RuneRolls"/>).</param>
    /// <param name="positions">Where the clusters are, in the order they came.</param>
    /// <returns>The effect info.</returns>
    internal static byte[] EncodeClusters(byte[] flags, ushort cast, List<Vector2> positions) {
        var count = System.Math.Min(positions.Count, (byte.MaxValue - ClustersAt) / ClusterSize);
        var info = new byte[ClustersAt + count * ClusterSize];
        Array.Copy(flags, info, System.Math.Min(flags.Length, CastAt));
        info[CastAt] = (byte) cast;
        info[CastAt + 1] = (byte) (cast >> 8);

        for (var i = 0; i < count; i++) {
            BitConverter.GetBytes(positions[i].x).CopyTo(info, ClustersAt + i * ClusterSize);
            BitConverter.GetBytes(positions[i].y).CopyTo(info, ClustersAt + i * ClusterSize + 4);
        }

        return info;
    }

    /// <summary>
    /// Reads the number of the cast and where its clusters are from the effect info (see
    /// <see cref="EncodeClusters"/>).
    /// </summary>
    /// <param name="info">The effect info.</param>
    /// <param name="playerObject">The player that used the effect, whose depth the clusters take.</param>
    /// <param name="cast">The number of the cast.</param>
    /// <returns>Where the clusters are.</returns>
    private static List<Vector3> DecodeClusters(byte[]? info, GameObject playerObject, out ushort cast) {
        cast = 0;
        if (info == null || info.Length < ClustersAt + ClusterSize) {
            return [];
        }

        cast = (ushort) (info[CastAt] | info[CastAt + 1] << 8);
        var positions = new List<Vector3>();
        var z = playerObject.transform.position.z;
        for (var at = ClustersAt; at + ClusterSize <= info.Length; at += ClusterSize) {
            positions.Add(new Vector3(BitConverter.ToSingle(info, at), BitConverter.ToSingle(info, at + 4), z));
        }

        return positions;
    }
}
