using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity.Action;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.

namespace SSMP.Game.Client.Entity;

/// <summary>
/// Class that manages initializing client-side entities to ensure they have correct references within FSM actions
/// to game objects (such as child objects).
/// </summary>
internal static class EntityInitializer {
    /// <summary>
    /// Array of state names that indicates that it is a initializing state.
    /// </summary>
    private static readonly string[] InitStateNames = [
        "init",
        "initiate",
        "initialise",
        "initialize",
        "dormant",
        "pause",
        "init pause",
        "deparents",
        "opened" // For battle gates
    ];

    /// <summary>
    /// Array of types that should be removed from client-side enemies so it doesn't interfere with remote behaviour.
    /// </summary>
    private static readonly Type[] ToRemoveTypes = [
        typeof(Walker),
        typeof(BigCentipede),
        typeof(Crawler)
    ];

    /// <summary>
    /// Array of types of actions that should be skipped during initialization. 
    /// </summary>
    private static readonly Type[] ToSkipTypes = [
        typeof(Tk2dPlayAnimation),
        typeof(HutongGames.PlayMaker.Actions.ActivateAllChildren),
        typeof(SetCollider), // TODO: test whether this has effects on other entities during host transfer (this was
        // added for battle gates)
        // The music of the whole game rather than anything about the creature. The other game's copy only plays what
        // lies on the way it went - a boss that is already beaten never passes the music it waits in - and a player
        // in the room when it does hears it from the replay
        typeof(ApplyMusicCue),
        typeof(TransitionToAudioSnapshot)
    ];

    /// <summary>
    /// The states among <see cref="InitStateNames"/> that are not setting up at all in this game, but a creature
    /// behaving: "Dormant" is where a creature hides itself away (renderer and collider off, parts switched off, even
    /// moved out of the way) until something wakes it, and "Pause" is as often a wait between two attacks as a first
    /// frame. Replayed on a creature that is up and about, they put it back into hiding.
    /// </summary>
    private static readonly string[] BehaviourStateNames = [
        "dormant",
        "pause",
        "init pause",
        "opened"
    ];

    /// <summary>
    /// Initialize the FSM of a client entity by finding initialize states and executing the actions in those states.
    /// </summary>
    /// <param name="fsm">The FSM to initialize.</param>
    /// <param name="takingOver">Whether this is the room's own copy of a creature that this game takes over from the
    /// other game, which has been running it all along. Only what the copy never did for itself is done then: the
    /// variables it looks things up into, and the rest of the first state's setting up. How it looks and whether it
    /// hides is what the other game's copy is doing right now, and that has already been carried over.</param>
    /// <param name="currentState">When taking over, the state the other game's copy of this FSM is in now.</param>
    public static void InitializeFsm(PlayMakerFSM fsm, bool takingOver = false, string? currentState = null) {
        // Create a list of states to initialize later
        var statesToInit = new List<FsmState>();
        // Keep track of the indices where the individual initialization states begin in our final list
        var indices = new int[InitStateNames.Length];

        CheckPreProcessFsm(fsm);

        // Go over each state in the FSM
        foreach (var state in fsm.FsmStates) {
            var stateName = state.Name.ToLower();
            var index = Array.IndexOf(InitStateNames, stateName);
            // Check if it is a "init" state
            if (index == -1) {
                continue;
            }

            // Then insert it at the correct index according to our tracked indices
            statesToInit.Insert(indices[index], state);
            // Increase all indices that come after, since we inserted something before
            for (var i = index; i < indices.Length; i++) {
                indices[i]++;
            }
        }

        // Now we can loop over the states in the same order as our "InitStateNames" array
        foreach (var state in statesToInit) {
            Logger.Debug($"Found initialization state: {state.Name}, executing actions");

            // Hiding or waiting is only done again for a creature that is still hiding or waiting
            var leftBehind = Array.IndexOf(BehaviourStateNames, state.Name.ToLower()) != -1 &&
                             state.Name != currentState;

            // Go over each action and try to execute it by applying empty data to it
            foreach (var action in state.Actions) {
                if (!action.Enabled) {
                    continue;
                }

                if (ToSkipTypes.Contains(action.GetType())) {
                    continue;
                }

                if (!EntityFsmActions.SupportedActionTypes.Contains(action.GetType())) {
                    continue;
                }

                if (takingOver && !IsWantedWhenTakingOver(action, leftBehind)) {
                    continue;
                }

                if (action.Fsm == null) {
                    Logger.Error($"FSM in action for state '{state.Name}', action '{action.GetType()}' is null");
                    continue;
                }

                Logger.Debug($"  Executing action {action.GetType()} for initialization");

                EntityFsmActions.ApplyNetworkDataFromAction(null, action);
            }
        }
    }

    /// <summary>
    /// Whether an action of a first state is still to be done to the room's own copy of a creature this game takes
    /// over. Every handover used to do all of them to a creature in the middle of whatever it was doing: 29 kinds of
    /// creature, several bosses among them, hide themselves in their "Dormant" state - renderer and collider off, made
    /// weightless, some of them moved far out of the room or back to where they started - and doing that state again
    /// made them vanish or turn invisible for both players while they went on fighting: a flyer throwing rocks out of
    /// thin air. The first states proper did the same on a smaller scale: turned creatures round to face one way, set
    /// some untouchable.
    /// </summary>
    /// <param name="action">The action of the first state.</param>
    /// <param name="leftBehind">Whether the state is one of <see cref="BehaviourStateNames"/> that the creature is
    /// not in now.</param>
    private static bool IsWantedWhenTakingOver(FsmStateAction action, bool leftBehind) {
        // Looking things up into variables is what the copy, which never ran, is missing; it changes nothing
        if (ActionRegistry.IsActionTransferSafeSetup(action)) {
            return true;
        }

        if (leftBehind) {
            return false;
        }

        if (IsCarriedOver(action)) {
            return false;
        }

        // The game undoes these when it leaves the state, which it did long ago or does now without them: done here,
        // they would stay
        return !EntityFsmActions.UndoesOnExit(action);
    }

    /// <summary>
    /// Whether an action sets something about the creature itself that the other game's copy has now and that has
    /// already been put on this copy: where it stands and its size and facing (just before the FSMs are started), and
    /// whether it can be seen and touched (the renderer and collider data, which reaches this copy too).
    /// </summary>
    private static bool IsCarriedOver(FsmStateAction action) {
        var owner = action.Fsm.GameObject;
        return action switch {
            SetPosition position => action.Fsm.GetOwnerDefaultTarget(position.gameObject) == owner,
            SetScale scale => action.Fsm.GetOwnerDefaultTarget(scale.gameObject) == owner,
            SetMeshRenderer meshRenderer => action.Fsm.GetOwnerDefaultTarget(meshRenderer.gameObject) == owner,
            // The collider data is about the first collider on the creature
            SetPolygonCollider polygon => action.Fsm.GetOwnerDefaultTarget(polygon.gameObject) == owner &&
                                          owner.GetComponent<Collider2D>() == owner.GetComponent<PolygonCollider2D>(),
            SetCircleCollider circle => action.Fsm.GetOwnerDefaultTarget(circle.gameObject) == owner &&
                                        owner.GetComponent<Collider2D>() == owner.GetComponent<CircleCollider2D>(),
            _ => false
        };
    }

    /// <summary>
    /// Remove all types that should be removed from a client-side entity object.
    /// </summary>
    /// <param name="gameObject">The game object on which to remove the types.</param>
    /// <param name="entityType">The registered entity type.</param>
    public static void RemoveClientTypes(GameObject gameObject, EntityType entityType) {
        // Keep Rigidbody2D components alive. Native death and corpse components cache their bodies during Awake;
        // destroying one here leaves callbacks such as ActiveCorpse.Update and SetParticleScale.OnUpdate with a
        // Unity-null reference. Remote controller bodies are still made kinematic so they cannot simulate locally.
        if (entityType != EntityType.GrassBall) {
            foreach (var rigidbody in gameObject.GetComponentsInChildren<Rigidbody2D>(true)) {
                if (rigidbody != null) {
                    ConfigureClientRigidbody(rigidbody);
                }
            }
        }

        foreach (var type in ToRemoveTypes) {
            foreach (var component in gameObject.GetComponentsInChildren(type, true)) {
                if (component == null) {
                    continue;
                }

                if (component is Behaviour behaviour) {
                    behaviour.enabled = false;
                }

                UnityEngine.Object.Destroy(component);
            }
        }

        // A copy's flock flyer flew off from this game's player and put itself away, while the scene host's stayed
        // where the partner saw it. The copy follows the scene host's instead. It is only switched off, since its
        // triggers call back into it
        foreach (var flyer in gameObject.GetComponentsInChildren<FlockFlyer>(true)) {
            flyer.enabled = false;
        }
    }

    /// <summary>
    /// Makes one client-side rigidbody non-authoritative unless it is owned by corpse logic.
    /// </summary>
    /// <param name="rigidbody">The client rigidbody to configure.</param>
    internal static void ConfigureClientRigidbody(Rigidbody2D rigidbody) {
        if (IsCorpseRigidbody(rigidbody)) {
            return;
        }

        rigidbody.bodyType = RigidbodyType2D.Kinematic;
    }

    /// <summary>
    /// Checks whether a rigidbody belongs to a corpse hierarchy whose native lifecycle requires the original body.
    /// </summary>
    /// <param name="rigidbody">The rigidbody to classify.</param>
    /// <returns>Whether the rigidbody is owned by corpse logic.</returns>
    private static bool IsCorpseRigidbody(Rigidbody2D rigidbody) {
        if (rigidbody.GetComponent<Corpse>() != null ||
            rigidbody.GetComponent<CorpseItems>() != null ||
            rigidbody.GetComponentInParent<Corpse>() != null ||
            rigidbody.GetComponentInParent<CorpseItems>() != null) {
            return true;
        }

        // ActiveCorpse caches GetComponent<Rigidbody2D>() on the same object.
        return rigidbody.GetComponent<ActiveCorpse>() != null;
    }

    /// <summary>
    /// Check whether the given FSM needs to be pre-processed by doing a loop over all states and actions to see if
    /// any references to the FSM are missing, which causes issues if we want to hook or initialize the FSM.
    /// </summary>
    /// <param name="fsm">The PlayMaker FSM to check and potentially pre-process.</param>
    public static void CheckPreProcessFsm(PlayMakerFSM fsm) {
        foreach (var state in fsm.FsmStates) {
            if (state.Fsm == null) {
                Logger.Debug($"Reference to FSM in state '{state.Name}' was null, pre-processing FSM...");
                fsm.Preprocess();
                break;
            }

            var wasPreProcessed = false;
            foreach (var action in state.Actions) {
                if (action.Fsm == null) {
                    Logger.Debug(
                        $"Reference to FSM in action '{action.GetType()}' in state '{state.Name}' was null, pre-processing FSM..."
                    );
                    fsm.Preprocess();
                    wasPreProcessed = true;
                    break;
                }
            }

            if (wasPreProcessed) {
                break;
            }
        }
    }
}
