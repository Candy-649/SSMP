using System.Collections;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;

// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedParameter.Local
#pragma warning disable CS0618
#pragma warning disable CS8600
#pragma warning disable CS8618

namespace SSMP.Game.Client.Entity.Action;

internal static partial class EntityFsmActions {
    #region SetParticleEmission

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetParticleEmission action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetParticleEmission action) {
        if (action.Fsm == null) {
            return;
        }

        if (action.emission == null) {
            return;
        }

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var particleSystem = gameObject.GetComponent<ParticleSystem>();
        if (particleSystem == null) {
            return;
        }

        var emission = action.emission.Value;
#pragma warning disable CS0618
        particleSystem.enableEmission = emission;
#pragma warning restore CS0618

        // For the length of the state only, as the game does it (see ActivateGameObject)
        if (action.resetOnExit) {
            UndoOnExit(action, () => {
                if (particleSystem == null) {
                    return;
                }

#pragma warning disable CS0618
                particleSystem.enableEmission = !emission;
#pragma warning restore CS0618
            });
        }
    }

    #endregion

    #region SetParticleEmissionRate

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetParticleEmissionRate action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetParticleEmissionRate action) {
        if (action.gameObject == null) {
            return;
        }

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var particleSystem = gameObject.GetComponent<ParticleSystem>();
        if (particleSystem == null) {
            return;
        }

        Action();

        if (action.everyFrame) {
            MonoBehaviourUtil.Instance.OnUpdateEvent += Action;

            new ActionInState {
                Fsm = action.Fsm,
                StateName = action.State.Name,
                ExitAction = () => MonoBehaviourUtil.Instance.OnUpdateEvent -= Action
            }.Register();
        }

        return;

        void Action() {
#pragma warning disable CS0618
            particleSystem.emissionRate = action.emissionRate.Value;
#pragma warning restore CS0618
        }
    }

    #endregion

    #region SetParticleEmissionSpeed

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetParticleEmissionSpeed action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetParticleEmissionSpeed action) {
        if (action.gameObject == null) {
            return;
        }

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var particleSystem = gameObject.GetComponent<ParticleSystem>();
        if (particleSystem == null) {
            return;
        }

        Action();

        if (action.everyFrame) {
            MonoBehaviourUtil.Instance.OnUpdateEvent += Action;

            new ActionInState {
                Fsm = action.Fsm,
                StateName = action.State.Name,
                ExitAction = () => MonoBehaviourUtil.Instance.OnUpdateEvent -= Action
            }.Register();
        }

        void Action() {
#pragma warning disable CS0618
            particleSystem.startSpeed = action.emissionSpeed.Value;
#pragma warning restore CS0618
        }
    }

    #endregion

    #region PlayParticleEmitter

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, PlayParticleEmitter action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, PlayParticleEmitter action) {
        if (action.emit == null) {
            return;
        }

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var particleSystem = gameObject.GetComponent<ParticleSystem>();
        if (particleSystem == null) {
            return;
        }

        if (!particleSystem.isPlaying && action.emit.Value <= 0) {
            particleSystem.Play();
        } else if (action.emit.Value > 0) {
            particleSystem.Emit(action.emit.Value);
        }
    }

    #endregion

    #region PlayParticleEmitterInState

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, PlayParticleEmitterInState action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, PlayParticleEmitterInState action) {
        if (action.gameObject == null) {
            return;
        }

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var particleSystem = gameObject.GetComponent<ParticleSystem>();
        if (particleSystem == null) {
            return;
        }

        particleSystem.Play();

        new ActionInState {
            Fsm = action.Fsm,
            StateName = action.State.Name,
            ExitAction = () => {
                if (particleSystem.isPlaying) {
                    particleSystem.Stop();
                }
            }
        }.Register();
    }

    #endregion

    #region StopParticleEmitter

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, StopParticleEmitter action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, StopParticleEmitter action) {
        if (action.gameObject == null) {
            return;
        }

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var particleSystem = gameObject.GetComponent<ParticleSystem>();
        if (particleSystem == null) {
            return;
        }

        if (particleSystem.isPlaying) {
            particleSystem.Stop();
        }
    }

    #endregion

    #region SetMeshRenderer

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetMeshRenderer action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetMeshRenderer action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var meshRenderer = gameObject.GetComponent<MeshRenderer>();
        if (meshRenderer == null) {
            return;
        }

        meshRenderer.enabled = action.active.Value;
    }

    #endregion

    #region ActivateGameObject

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, ActivateGameObject action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return false;
        }

        return !IsObjectInRegistry(gameObject);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, ActivateGameObject action) {
        // An object held in a variable has already been named by the scene host (see ReadSubject)
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        void SetActiveRecursively(GameObject go, bool state) {
            go.SetActive(state);

            foreach (UnityEngine.Component component in go.transform) {
                SetActiveRecursively(component.gameObject, state);
            }
        }

        var activate = action.activate.Value;
        var recursive = action.recursive.Value;
        if (recursive) {
            SetActiveRecursively(gameObject, activate);
        } else {
            gameObject.SetActive(activate);
        }

        // For the length of the state only, as the game does it: 6530 such switches in the creatures' own FSMs, and the
        // other way again was never sent, so everything they switch on for one attack stayed on here
        if (action.resetOnExit) {
            var switched = gameObject;
            UndoOnExit(action, () => {
                if (switched == null) {
                    return;
                }

                if (recursive) {
                    SetActiveRecursively(switched, !activate);
                } else {
                    switched.SetActive(!activate);
                }
            });
        }
    }

    #endregion

    #region ActivateGameObjectDelay

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, ActivateGameObjectDelay action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null || IsObjectInRegistry(gameObject)) {
            return false;
        }

        data.Packet.Write(action.delay.Value);
        data.Packet.Write(action.activate.Value);

        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, ActivateGameObjectDelay action) {
        float delay;
        bool activate;
        if (data == null) {
            delay = action.delay.Value;
            activate = action.activate.Value;
        } else {
            delay = data.Packet.ReadFloat();
            activate = data.Packet.ReadBool();
        }

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var activated = false;
        var initialActiveState = false;

        if (delay > 0f) {
            new ActionInState {
                Fsm = action.Fsm,
                StateName = action.State.Name,
                Coroutine = MonoBehaviourUtil.Instance.StartCoroutine(Behaviour()),
                ExitAction = ExitAction
            }.Register();
            return;
        }

        Activate();

        if (action.resetOnExit) {
            new ActionInState {
                Fsm = action.Fsm,
                StateName = action.State.Name,
                ExitAction = ExitAction
            }.Register();
        }

        return;

        void Activate() {
            if (gameObject == null) {
                return;
            }

            initialActiveState = gameObject.activeSelf;
            gameObject.SetActive(activate);
            activated = true;
        }

        void ExitAction() {
            // Like the action's OnExit, only an object that was actually toggled gets its previous state back
            if (activated && action.resetOnExit && gameObject != null) {
                gameObject.SetActive(initialActiveState);
            }
        }

        IEnumerator Behaviour() {
            yield return new WaitForSeconds(delay);
            Activate();
        }
    }

    #endregion

    #region Tk2dPlayAnimation

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, Tk2dPlayAnimation action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return false;
        }

        return !IsObjectInRegistry(gameObject);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, Tk2dPlayAnimation action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var animator = gameObject.GetComponent<tk2dSpriteAnimator>();
        if (animator == null) {
            return;
        }

        animator.Play(action.clipName.Value);
    }

    #endregion

    #region Tk2dPlayFrame

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, Tk2dPlayFrame action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return false;
        }

        return !IsObjectInRegistry(gameObject);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, Tk2dPlayFrame action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var animator = gameObject.GetComponent<tk2dSpriteAnimator>();
        if (animator == null) {
            return;
        }

        animator.PlayFromFrame(action.frame.Value);
    }

    #endregion

    #region Tk2dPlayAnimationWithEvents

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, Tk2dPlayAnimationWithEvents action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return false;
        }

        return !IsObjectInRegistry(gameObject);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, Tk2dPlayAnimationWithEvents action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var animator = gameObject.GetComponent<tk2dSpriteAnimator>();
        if (animator == null) {
            return;
        }

        animator.Play(action.clipName.Value);
    }

    #endregion

    #region SpawnBlood

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SpawnBlood action) {
        // if (GlobalPref.Instance == null) {
        //     return false;
        // }
        //
        // data.Packet.Write((short) action.spawnMin.Value);
        // data.Packet.Write((short) action.spawnMax.Value);
        //
        // data.Packet.Write(action.speedMin.Value);
        // data.Packet.Write(action.speedMax.Value);
        // data.Packet.Write(action.angleMin.Value);
        // data.Packet.Write(action.angleMax.Value);
        //
        // return true;
        return false;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SpawnBlood action) {
        // var spawnMin = data.Packet.ReadShort();
        // var spawnMax = data.Packet.ReadShort();
        //
        // var speedMin = data.Packet.ReadFloat();
        // var speedMax = data.Packet.ReadFloat();
        //
        // var angleMin = data.Packet.ReadFloat();
        // var angleMax = data.Packet.ReadFloat();
        //
        // var position = action.position.Value;
        // if (action.spawnPoint.Value != null) {
        //     position += action.spawnPoint.Value.transform.position;
        // }
        //
        // GlobalPrefabDefaults.Instance.SpawnBlood(
        //     position,
        //     spawnMin,
        //     spawnMax,
        //     speedMin,
        //     speedMax,
        //     angleMin,
        //     angleMax,
        //     action.colorOverride.IsNone ? new Color?() : action.colorOverride.Value
        // );
    }

    #endregion

    #region SpawnBloodTime

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SpawnBloodTime action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SpawnBloodTime action) {
        // var position = action.position.Value;
        // if (action.spawnPoint.Value != null) {
        //     position += action.spawnPoint.Value.transform.position;
        // }
        //
        // var spawnMin = (short) action.spawnMin.Value;
        // var spawnMax = (short) action.spawnMax.Value;
        //
        // var speedMin = action.speedMin.Value;
        // var speedMax = action.speedMax.Value;
        //
        // var angleMin = action.angleMin.Value;
        // var angleMax = action.angleMax.Value;
        //
        // var color = action.colorOverride.IsNone ? new Color?() : action.colorOverride.Value;
        //
        // var coroutine = MonoBehaviourUtil.Instance.StartCoroutine(Behaviour());
        //
        // new ActionInState {
        //     Fsm = action.Fsm,
        //     StateName = action.State.Name,
        //     Coroutine = coroutine
        // }.Register();
        //
        // IEnumerator Behaviour() {
        //     while (true) {
        //         yield return new WaitForSeconds(action.delay.Value);
        //
        //         if (GlobalPrefabDefaults.Instance == null) {
        //             break;
        //         }
        //
        //         GlobalPrefabDefaults.Instance.SpawnBlood(
        //             position,
        //             spawnMin,
        //             spawnMax,
        //             speedMin,
        //             speedMax,
        //             angleMin,
        //             angleMax,
        //             color
        //         );
        //     }
        // }
    }

    #endregion

    #region SetAnimatorTrigger

    // A creature built on Unity's animator rather than on sprite clips moves its whole body by these: a boss that
    // rears up, sweeps and slams did all of it on one game only, while its copy on the other kept the pose it started
    // in. None of the three was done again on the other game before.

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetAnimatorTrigger action) {
        return action.Fsm.GetOwnerDefaultTarget(action.gameObject) != null;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetAnimatorTrigger action) {
        var animator = GetUnityAnimator(action.Fsm.GetOwnerDefaultTarget(action.gameObject));
        if (animator != null) {
            animator.SetTrigger(action.trigger.Value);
        }
    }

    #endregion

    #region ResetAnimatorTrigger

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, ResetAnimatorTrigger action) {
        return action.Fsm.GetOwnerDefaultTarget(action.gameObject) != null;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, ResetAnimatorTrigger action) {
        var animator = GetUnityAnimator(action.Fsm.GetOwnerDefaultTarget(action.gameObject));
        if (animator != null) {
            animator.ResetTrigger(action.trigger.Value);
        }
    }

    #endregion

    #region AnimatorPlay

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AnimatorPlay action) {
        if (action.Fsm.GetOwnerDefaultTarget(action.gameObject) == null) {
            return false;
        }

        // Sent, because one creature keeps the state to play in a variable that only the scene host's FSM fills in
        data.Packet.Write(action.stateName.Value);
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, AnimatorPlay action) {
        var stateName = data == null ? action.stateName.Value : data.Packet.ReadString();

        var animator = GetUnityAnimator(action.Fsm.GetOwnerDefaultTarget(action.gameObject));
        if (animator == null) {
            return;
        }

        animator.Play(
            stateName,
            action.layer.IsNone ? -1 : action.layer.Value,
            action.normalizedTime.IsNone ? float.NegativeInfinity : action.normalizedTime.Value
        );
    }

    /// <summary>
    /// The Unity animator on the given object, as opposed to the sprite animator most replays here work with.
    /// </summary>
    private static Animator? GetUnityAnimator(GameObject? gameObject) {
        return gameObject == null ? null : gameObject.GetComponent<Animator>();
    }

    #endregion

    #region SetSpriteRenderer

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetSpriteRenderer action) {
        return action.gameObject != null;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetSpriteRenderer action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var spriteRenderer = gameObject.GetComponent<SpriteRenderer>();
        if (spriteRenderer != null) {
            spriteRenderer.enabled = action.active.Value;
        }
    }

    #endregion
}
