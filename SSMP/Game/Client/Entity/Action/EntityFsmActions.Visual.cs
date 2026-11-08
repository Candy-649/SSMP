using System.Collections;
using System.Collections.Generic;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Save;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

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

            // Taken off again with the state, or with the room (EveryFrame)
            new ActionInState {
                Fsm = action.Fsm,
                StateName = action.State.Name,
                EveryFrame = Action
            }.Register();
        }

        return;

        void Action() {
            // Gone with the creature or the room it was in, while still set to run
            if (particleSystem == null) {
                return;
            }

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

            // Taken off again with the state, or with the room (EveryFrame)
            new ActionInState {
                Fsm = action.Fsm,
                StateName = action.State.Name,
                EveryFrame = Action
            }.Register();
        }

        void Action() {
            // Gone with the creature or the room it was in, while still set to run
            if (particleSystem == null) {
                return;
            }

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

    #region PlayParticleEmitterChildren

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, PlayParticleEmitterChildren action) {
        return true;
    }

    /// <summary>
    /// Applies network data to the FSM action: plays the particles of each child of the object, and stops them again
    /// as the state is left if the action does, as the game does. Nothing played them on the copy, so the spray that a
    /// creature spat at the players never showed in the other game, nor hurt anyone there (USER 11-04:
    /// "全是蛆的地方有个会喷东西的怪，那个怪喷东西没有同步", "我朋友说她根本没看到喷绿水").
    /// </summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, PlayParticleEmitterChildren action) {
        if (action.gameObject == null) {
            return;
        }

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        foreach (Transform child in gameObject.transform) {
            var particleSystem = child.GetComponent<ParticleSystem>();
            if (particleSystem == null) {
                continue;
            }

            particleSystem.Play();
            if (action.resetTimeIfPlaying) {
                particleSystem.time = 0f;
            }
        }

        if (!action.stopOnStateExit) {
            return;
        }

        new ActionInState {
            Fsm = action.Fsm,
            StateName = action.State.Name,
            ExitAction = () => {
                foreach (Transform child in gameObject.transform) {
                    var particleSystem = child.GetComponent<ParticleSystem>();
                    if (particleSystem != null) {
                        particleSystem.Stop();
                    }
                }
            }
        }.Register();
    }

    #endregion

    #region StopParticleEmittersInChildren

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, StopParticleEmittersInChildren action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action: stops every playing particle system under the object.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, StopParticleEmittersInChildren action) {
        if (action.gameObject == null) {
            return;
        }

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        foreach (var particleSystem in gameObject.GetComponentsInChildren<ParticleSystem>()) {
            if (particleSystem.isPlaying) {
                particleSystem.Stop();
            }
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

    /// <summary>Builds network data from the FSM action: the player whose catch the creature switches the object on
    /// for, if any (see Entity.CaughtIn, <see cref="BeforeSwitchingOn"/>).</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, ActivateGameObject action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null || IsObjectInRegistry(gameObject)) {
            return false;
        }

        var caught = action.activate.Value ? Entity.CaughtIn(action.Fsm, action.State) : null;
        data.Packet.Write(ThrownThings.CodeOf?.Invoke(caught) ?? ThrownThings.NoPlayer);
        return true;
    }

    /// <summary>Applies network data to the FSM action. What it switches on for a catch of another player than the
    /// local one keeps off the local player (see <see cref="BeforeSwitchingOn"/>). The replay that sets up a
    /// creature's first states has no data, and no catch.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, ActivateGameObject action) {
        // Read whatever comes of it, so that what comes after is read from where it starts
        var caught = data != null ? ThrownThings.FigureOf?.Invoke(data.Packet.ReadUShort(), data.SenderId) : null;

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
        if (activate && gameObject.activeSelf) {
            SayOnAlready(action, gameObject);
        }

        var kept = BeforeSwitchingOn(gameObject, action.Fsm.GameObject, activate ? caught : null);
        if (recursive) {
            SetActiveRecursively(gameObject, activate);
        } else {
            gameObject.SetActive(activate);
        }

        if (kept != null) {
            AfterSwitchingOn(gameObject, kept);
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

    /// <summary>
    /// The parts with an FSM of their own that a replay switched on while they were on already, said once a room each.
    /// </summary>
    private static readonly HashSet<string> SaidOnAlready = [];

    /// <summary>
    /// Says in the log, once a room for each, that a replay switches on a part of the copy with an FSM of its own that
    /// is on already, which does not start over as it does from off. The slash of a boss's dash is such a part, which
    /// plays its slash out as it is switched on, and the copy showed none at some dashes and at the end of some others
    /// (USER 11-07 "有时候有刀光有时候没有").
    /// </summary>
    private static void SayOnAlready(ActivateGameObject action, GameObject part) {
        if (part.GetComponent<PlayMakerFSM>() is not { } partFsm) {
            return;
        }

        var owner = action.Fsm.GameObject;
        if (!SaidOnAlready.Add($"{owner.name}/{action.State.Name}/{part.name}")) {
            return;
        }

        Logger.Info(
            $"[Replay] '{owner.name}'s ActivateGameObject in '{action.State.Name}' of '{action.Fsm.Name}' switches on " +
            $"'{part.name}', which is on already with its '{partFsm.FsmName}' in '{partFsm.ActiveStateName}', so that " +
            "does not start over as it would from off (said once a room)"
        );
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
        return false;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SpawnBlood action) {
    }

    #endregion

    #region SpawnBloodTime

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SpawnBloodTime action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SpawnBloodTime action) {
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
