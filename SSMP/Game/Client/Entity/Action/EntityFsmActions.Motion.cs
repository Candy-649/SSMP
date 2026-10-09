using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity.Component;
using SSMP.Networking.Packet.Data;
using UnityEngine;

// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedParameter.Local
#pragma warning disable CS0618
#pragma warning disable CS8600
#pragma warning disable CS8618

namespace SSMP.Game.Client.Entity.Action;

internal static partial class EntityFsmActions {
    #region SetScale

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetScale action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == action.Fsm.GameObject) {
            return false;
        }

        if (IsObjectInRegistry(gameObject)) {
            return false;
        }

        var scale = action.vector.IsNone ? gameObject.transform.localScale : action.vector.Value;
        if (!action.x.IsNone) {
            scale.x = action.x.Value;
        }

        if (!action.y.IsNone) {
            scale.y = action.y.Value;
        }

        if (!action.z.IsNone) {
            scale.z = action.z.Value;
        }

        data.Packet.Write(scale.x);
        data.Packet.Write(scale.y);
        data.Packet.Write(scale.z);

        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, SetScale action) {
        Vector3 scale;

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);

        if (data == null) {
            scale = action.vector.IsNone ? gameObject.transform.localScale : action.vector.Value;

            if (!action.x.IsNone) {
                scale.x = action.x.Value;
            }

            if (!action.y.IsNone) {
                scale.y = action.y.Value;
            }

            if (!action.z.IsNone) {
                scale.z = action.z.Value;
            }
        } else {
            scale = new Vector3(
                data.Packet.ReadFloat(),
                data.Packet.ReadFloat(),
                data.Packet.ReadFloat()
            );
        }

        gameObject.transform.localScale = scale;

        // Scaled every frame, like a sickle a boss grows in its hand by easing the size it is given, it goes on being
        // scaled here until the scene host's FSM leaves the state. Done once, the sickle stayed the size it started at.
        // The size is read from the copy's FSM variables, which the scene host sends as they change and which can
        // arrive a little after this, still holding the size the last throw ended at. They start from what the scene
        // host's held when it scaled, so the sickle does not first flash at that old size.
        if (data != null && action.everyFrame) {
            if (!action.vector.IsNone) {
                action.vector.Value = scale;
            }

            if (!action.x.IsNone) {
                action.x.Value = scale.x;
            }

            if (!action.y.IsNone) {
                action.y.Value = scale.y;
            }

            if (!action.z.IsNone) {
                action.z.Value = scale.z;
            }

            RunInState(action);
        }
    }

    #endregion

    #region SetVelocity2d

#pragma warning disable CS0618 // Type or member is obsolete

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetVelocity2d action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return false;
        }

        if (IsObjectInRegistry(gameObject)) {
            //Logger.Debug("Tried getting SetVelocity2d network data, but entity is in registry");
            return false;
        }

        var rigidbody = gameObject.GetComponent<Rigidbody2D>();
        if (rigidbody == null) {
            return false;
        }

        var vector = action.vector.IsNone ? rigidbody.velocity : action.vector.Value;
        if (!action.x.IsNone) {
            vector.x = action.x.Value;
        }

        if (!action.y.IsNone) {
            vector.y = action.y.Value;
        }

        data.Packet.Write(vector.x);
        data.Packet.Write(vector.y);

        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetVelocity2d action) {
        var vector = new Vector2(
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat()
        );

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var rigidbody = gameObject.GetComponent<Rigidbody2D>();
        if (rigidbody == null) {
            return;
        }

        rigidbody.velocity = vector;
    }

    #endregion

    #region SetPosition

    /// <summary>Builds network data from the FSM action.</summary>
    /// <remarks>
    /// One that puts its object in place every frame from then on has not put it anywhere yet. A part of the creature
    /// is then put in place by the copy itself every frame, from the values that the scene host keeps sending (see
    /// <see cref="WriteMovingPart"/>). A boss that throws a censer on a chain flies it by putting it every frame on a
    /// curve that it eases along: sent once as each half of the throw started, the copy's censer stood where that half
    /// started it and showed up on the ground when it landed (USER 10-09).
    /// </remarks>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetPosition action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return false;
        }

        if (action.everyFrame && !action.lateUpdate) {
            var movesHere = IsMovingPart(action, gameObject);
            data.Packet.Write(movesHere);
            if (movesHere) {
                return WritePartPlace(data, action, gameObject);
            }
        }

        Vector3 vector3;
        if (!action.vector.IsNone) {
            vector3 = action.vector.Value;
        } else {
            vector3 = action.space == Space.World ? gameObject.transform.position : gameObject.transform.localPosition;
        }

        if (!action.x.IsNone) {
            vector3.x = action.x.Value;
        }

        if (!action.y.IsNone) {
            vector3.y = action.y.Value;
        }

        if (!action.z.IsNone) {
            vector3.z = action.z.Value;
        }

        data.Packet.Write(vector3.x);
        data.Packet.Write(vector3.y);
        data.Packet.Write(vector3.z);

        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, SetPosition action) {
        Vector3 vector3;

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);

        if (data == null) {
            if (gameObject == null) {
                return;
            }

            if (!action.vector.IsNone) {
                vector3 = action.vector.Value;
            } else {
                vector3 = action.space == Space.World
                    ? gameObject.transform.position
                    : gameObject.transform.localPosition;
            }

            if (!action.x.IsNone) {
                vector3.x = action.x.Value;
            }

            if (!action.y.IsNone) {
                vector3.y = action.y.Value;
            }

            if (!action.z.IsNone) {
                vector3.z = action.z.Value;
            }
        } else if (action.everyFrame && !action.lateUpdate && data.Packet.ReadBool()) {
            PutPartHere(data, action, gameObject);
            return;
        } else {
            vector3 = new Vector3(
                data.Packet.ReadFloat(),
                data.Packet.ReadFloat(),
                data.Packet.ReadFloat()
            );

            if (gameObject == null) {
                return;
            }
        }

        if (action.space == Space.World) {
            gameObject.transform.position = vector3;
        } else {
            gameObject.transform.localPosition = vector3;
        }
    }

    #endregion

    #region SetRotation

    /// <summary>Builds network data from the FSM action.</summary>
    /// <remarks>
    /// An action that turns its object as its state starts has done so by now, and the object is sent turned the way it
    /// sits under its parent, like the places and sizes of parts (WritePlace, WriteScale). A turn in the world was put
    /// on the copy's part as it was, with the copy's body facing whichever way it faced when the word came. The engine
    /// turns a part under a body that is flipped the mirror way round, and the body's own turn travels separately,
    /// with its position. A creature that turns to face something and points a part at it in the same step - the
    /// dancers turning to the middle and pointing their eye beams at it as the ring appears - had the beam set for the
    /// way the copy faced before it turned, and once it had turned the beam pointed away from the middle (USER 10-09).
    ///
    /// Only an object under the creature itself, or under nothing, is sent this way: the copy's object sits under the
    /// same parent there. One under something else, like the hero that a part was put on, may not on the copy.
    ///
    /// One that turns a part of the creature every frame from then on, by angles, is turned by the copy itself every
    /// frame, from the values that the scene host keeps sending (see <see cref="WriteTurningPart"/>), like a chain that
    /// a boss pulls back, turned each frame to the angle the boss works out for it.
    /// </remarks>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetRotation action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return false;
        }

        if (IsObjectInRegistry(gameObject)) {
            //Logger.Debug("Tried getting SetPosition network data, but entity is in registry");
            return false;
        }

        var parent = gameObject.transform.parent;
        if (!action.everyFrame && !action.lateUpdate &&
            (parent == null || PathFromOwner(action.Fsm.GameObject, parent.gameObject) != null)) {
            var turn = gameObject.transform.localRotation;
            data.Packet.Write(true);
            data.Packet.Write(turn.x);
            data.Packet.Write(turn.y);
            data.Packet.Write(turn.z);
            data.Packet.Write(turn.w);
            return true;
        }

        data.Packet.Write(false);
        if (action.everyFrame && !action.lateUpdate) {
            // Not a turn held in a quaternion, whose variables the scene host does not send
            var turnsHere = action.quaternion.IsNone && IsMovingPart(action, gameObject);
            data.Packet.Write(turnsHere);
            if (turnsHere) {
                WriteTurningPart(data, action, gameObject);
                return true;
            }
        }

        // Turned from the next frame on, the object isn't turned yet, and what it is about to be turned to is sent, as
        // is the turn of an object under something other than the creature
        Vector3 vector3;
        if (action.quaternion.IsNone) {
            if (action.vector.IsNone) {
                vector3 = action.space == Space.Self
                    ? gameObject.transform.localEulerAngles
                    : gameObject.transform.eulerAngles;
            } else {
                vector3 = action.vector.Value;
            }
        } else {
            vector3 = action.quaternion.Value.eulerAngles;
        }

        if (!action.xAngle.IsNone) {
            vector3.x = action.xAngle.Value;
        }

        if (!action.yAngle.IsNone) {
            vector3.y = action.yAngle.Value;
        }

        if (!action.zAngle.IsNone) {
            vector3.z = action.zAngle.Value;
        }

        data.Packet.Write(vector3.x);
        data.Packet.Write(vector3.y);
        data.Packet.Write(vector3.z);

        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, SetRotation action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);

        Vector3 euler;
        if (data == null) {
            // Host path: read current state from the FSM action.
            if (gameObject == null) return;

            if (!action.quaternion.IsNone) {
                euler = action.quaternion.Value.eulerAngles;
            } else if (!action.vector.IsNone) {
                euler = action.vector.Value;
            } else {
                euler = action.space == Space.Self
                    ? gameObject.transform.localEulerAngles
                    : gameObject.transform.eulerAngles;
            }

            if (!action.xAngle.IsNone) euler.x = action.xAngle.Value;
            if (!action.yAngle.IsNone) euler.y = action.yAngle.Value;
            if (!action.zAngle.IsNone) euler.z = action.zAngle.Value;
        } else if (data.Packet.ReadBool()) {
            var turn = new Quaternion(
                data.Packet.ReadFloat(),
                data.Packet.ReadFloat(),
                data.Packet.ReadFloat(),
                data.Packet.ReadFloat()
            );
            if (gameObject != null) {
                gameObject.transform.localRotation = turn;
            }

            return;
        } else if (action.everyFrame && !action.lateUpdate && data.Packet.ReadBool()) {
            TurnPartHere(data, action, gameObject);
            return;
        } else {
            // Client path: always consume packet bytes to keep the stream in sync,
            // even if the target object is gone.
            euler = new Vector3(
                data.Packet.ReadFloat(),
                data.Packet.ReadFloat(),
                data.Packet.ReadFloat()
            );

            if (gameObject == null) return;
        }

        if (action.space == Space.Self) {
            gameObject.transform.localEulerAngles = euler;
        } else {
            gameObject.transform.eulerAngles = euler;
        }
    }

    #endregion

    #region SetBoxCollider2DSizeVector

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetBoxCollider2DSizeVector action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetBoxCollider2DSizeVector action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject1);
        if (gameObject == null) {
            return;
        }

        var collider = gameObject.GetComponent<BoxCollider2D>();
        if (collider == null) {
            return;
        }

        if (!action.size.IsNone) {
            collider.size = action.size.Value;
        }

        if (!action.offset.IsNone) {
            collider.offset = action.offset.Value;
        }
    }

    #endregion

    #region SetVelocityAsAngle

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetVelocityAsAngle action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return false;
        }

        if (IsObjectInRegistry(gameObject)) {
            //Logger.Debug("Tried getting SetVelocityAsAngle network data, but entity is in registry");
            return false;
        }

        data.Packet.Write(action.speed.Value);
        data.Packet.Write(action.angle.Value);

        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetVelocityAsAngle action) {
        var speed = data.Packet.ReadFloat();
        var angle = data.Packet.ReadFloat();

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var rigidbody = gameObject.GetComponent<Rigidbody2D>();
        if (rigidbody == null) {
            return;
        }

        var x = speed * Mathf.Cos(angle * ((float) System.Math.PI / 180f));
        var y = speed * Mathf.Sin(angle * ((float) System.Math.PI / 180f));

        rigidbody.velocity = new Vector2(x, y);
    }

    #endregion

    #region AnimatePositionTo

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AnimatePositionTo action) {
        return WriteMovingPart(data, action, action.Fsm.GetOwnerDefaultTarget(action.gameObject));
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, AnimatePositionTo action) {
        MovePartHere(data, action, action.Fsm.GetOwnerDefaultTarget(action.gameObject), false);
    }

    #endregion

    #region AnimatePositionToV2

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AnimatePositionToV2 action) {
        return WriteMovingPart(data, action, action.Fsm.GetOwnerDefaultTarget(action.GameObject));
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, AnimatePositionToV2 action) {
        MovePartHere(data, action, action.Fsm.GetOwnerDefaultTarget(action.GameObject), false);
    }

    #endregion

    #region AnimateXPositionTo

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AnimateXPositionTo action) {
        return WriteMovingPart(data, action, action.Fsm.GetOwnerDefaultTarget(action.GameObject));
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, AnimateXPositionTo action) {
        MovePartHere(data, action, action.Fsm.GetOwnerDefaultTarget(action.GameObject), false);
    }

    #endregion

    #region AnimateYPositionTo

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AnimateYPositionTo action) {
        return WriteMovingPart(data, action, action.Fsm.GetOwnerDefaultTarget(action.GameObject));
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, AnimateYPositionTo action) {
        MovePartHere(data, action, action.Fsm.GetOwnerDefaultTarget(action.GameObject), false);
    }

    #endregion

    #region AnimateZPositionTo

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AnimateZPositionTo action) {
        return WriteMovingPart(data, action, action.Fsm.GetOwnerDefaultTarget(action.GameObject));
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, AnimateZPositionTo action) {
        MovePartHere(data, action, action.Fsm.GetOwnerDefaultTarget(action.GameObject), false);
    }

    #endregion

    /// <summary>
    /// The fields of each kind of action by which it is given numbers, points and switches, by type (see
    /// <see cref="WriteValues"/>).
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> ValueFields = new();

    /// <summary>
    /// Writes where an action that moves a part of its creature over a while - every frame from then on, or sliding it
    /// somewhere over a given time - starts it, and all that the action is given as it starts in this game, whether
    /// set in the FSM or held by its variables, for the copy to move its own part the same way (see
    /// <see cref="MovePartHere"/>). Only the decision goes: the moving is each game's own, and goes on through a
    /// stall in between. The creature itself is left out, which is kept in step by its own position, and so is
    /// anything not its own (see <see cref="IsOwnPart"/>).
    /// </summary>
    /// <param name="data">The network data.</param>
    /// <param name="action">The action.</param>
    /// <param name="gameObject">The object that the action moves.</param>
    /// <returns>Whether it moves a part of its creature, and so anything was written.</returns>
    private static bool WriteMovingPart(EntityNetworkData data, FsmStateAction action, GameObject? gameObject) {
        return IsOwnPart(action, gameObject) && WritePartPlace(data, action, gameObject);
    }

    /// <summary>
    /// Writes where a part of the creature starts and all that the action is given as it starts (see
    /// <see cref="WriteMovingPart"/>), for an object already known to be such a part.
    /// </summary>
    private static bool WritePartPlace(EntityNetworkData data, FsmStateAction action, GameObject? gameObject) {
        if (!WritePlace(data, action, gameObject)) {
            return false;
        }

        WriteValues(data, action);
        return true;
    }

    /// <summary>
    /// Moves a part of the copy the way the game that runs the creature moves its own (see
    /// <see cref="WriteMovingPart"/>): puts it where it starts there, gives the action all that it was given there,
    /// and runs it here until the scene host's FSM leaves the state. What it is given as it goes on, like the speed of
    /// a tendril that eases up, reaches the copy's FSM variables as the scene host sends them. Not in the replay that
    /// sets up a creature's first states, which has nothing to go by: the part would move on from wherever it was.
    /// </summary>
    /// <param name="data">The network data, or null when it sets up the creature's first states.</param>
    /// <param name="action">The action.</param>
    /// <param name="gameObject">The part that the action moves.</param>
    /// <param name="everyStep">Whether the action moves it in each step of physics.</param>
    private static void MovePartHere(EntityNetworkData? data, FsmStateAction action, GameObject? gameObject,
        bool everyStep) {
        if (data == null) {
            return;
        }

        ReadPlace(data, gameObject);
        var ownValues = ReadValues(data, action);
        RunInState(action, everyStep);
        PutBack(ownValues);
    }

    /// <summary>
    /// Writes how a part of the creature that an action turns every frame from now on starts out turned under its
    /// parent, and all that the action is given as it starts in this game (see <see cref="WriteValues"/>), for the
    /// copy to go on turning its own part the same way (see <see cref="TurnPartHere"/>).
    /// </summary>
    private static void WriteTurningPart(EntityNetworkData data, FsmStateAction action, GameObject gameObject) {
        var turn = gameObject.transform.localRotation;
        data.Packet.Write(turn.x);
        data.Packet.Write(turn.y);
        data.Packet.Write(turn.z);
        data.Packet.Write(turn.w);
        WriteValues(data, action);
    }

    /// <summary>
    /// Turns a part of the copy every frame the way the game that runs the creature turns its own (see
    /// <see cref="WriteTurningPart"/>) until the scene host's FSM leaves the state, as <see cref="MovePartHere"/>
    /// moves one.
    /// </summary>
    private static void TurnPartHere(EntityNetworkData data, FsmStateAction action, GameObject? gameObject) {
        var turn = new Quaternion(
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat()
        );
        if (gameObject != null) {
            gameObject.transform.localRotation = turn;
        }

        var ownValues = ReadValues(data, action);
        RunInState(action);
        PutBackNewer(ownValues);
    }

    /// <summary>
    /// Puts a part of the copy in place every frame the way the game that runs the creature puts its own (see
    /// <see cref="WriteMovingPart"/>) until the scene host's FSM leaves the state. Unlike a part that an action moves on
    /// from where it starts (<see cref="MovePartHere"/>), what this one is put at is read again every frame, from the
    /// copy's variables, which keep what the action was given unless newer came with it (see
    /// <see cref="PutBackNewer"/>).
    /// </summary>
    private static void PutPartHere(EntityNetworkData? data, FsmStateAction action, GameObject? gameObject) {
        if (data == null) {
            return;
        }

        ReadPlace(data, gameObject);
        var ownValues = ReadValues(data, action);
        RunInState(action);
        PutBackNewer(ownValues);
    }

    /// <summary>
    /// Whether an object is a part of its creature that the copy moves, turns or puts in place itself when an action
    /// does so to it over a while: one of its own parts (see <see cref="IsOwnPart"/>) that nothing but replays keeps
    /// in step.
    /// </summary>
    private static bool IsMovingPart(FsmStateAction action, GameObject? gameObject) {
        return IsOwnPart(action, gameObject) && IsKeptInStepByReplays(gameObject, action);
    }

    /// <summary>
    /// Whether an object is a part of the creature whose FSM an action is in: something under the creature, or
    /// something that one of the creature's own FSMs lets go of into the room by giving it no parent, with what is
    /// under it. A boss that throws a censer on a chain lets go of both as it starts, and they are its own all the
    /// same: the copy has its own of each, let go of the same way. Neither the creature itself nor a player is.
    /// </summary>
    private static bool IsOwnPart(FsmStateAction action, GameObject? gameObject) {
        var creature = action.Fsm.GameObject;
        if (gameObject == null || creature == null || gameObject == creature || IsAPlayer(gameObject)) {
            return false;
        }

        var transform = gameObject.transform;
        if (transform.IsChildOf(creature.transform)) {
            return true;
        }

        foreach (var letGo in PartsLetGoBy(creature)) {
            if (transform.IsChildOf(letGo.transform)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What a creature's own FSMs let go of into the room by giving it no parent, as their variables hold it now:
    /// neither the creature itself nor a player.
    /// </summary>
    private static List<GameObject> PartsLetGoBy(GameObject creature) {
        var parts = new List<GameObject>();
        foreach (var fsm in creature.GetComponents<PlayMakerFSM>()) {
            foreach (var state in fsm.FsmStates) {
                var actions = state.Actions;
                if (actions == null) {
                    continue;
                }

                foreach (var stateAction in actions) {
                    var letGo = stateAction switch {
                        SetParent { gameObject: not null } setParent when IsNoParent(setParent.parent) =>
                            fsm.Fsm.GetOwnerDefaultTarget(setParent.gameObject),
                        SetTransformParent { gameObject: not null } setTransformParent
                            when IsNoParent(setTransformParent.parent) =>
                            fsm.Fsm.GetOwnerDefaultTarget(setTransformParent.gameObject),
                        _ => null
                    };

                    if (letGo != null && letGo != creature && !IsAPlayer(letGo) && !parts.Contains(letGo)) {
                        parts.Add(letGo);
                    }
                }
            }
        }

        return parts;
    }

    /// <summary>
    /// Switches off what a creature's copy let go of into the room, when this game takes the creature over and the copy
    /// itself is switched off: it is the copy's, like the parts under it, which go off with it, and the room's own
    /// creature goes on with its own. Left on, the censer of a boss that was thrown when the other player left stayed
    /// for good where the copy's throw stopped - in the air, with the hurting part of it on - and its chain beside it.
    /// Only what was made with the copy is switched off: not the room's own, nor another creature, nor what the
    /// copy's variables may have been pointed at in the room.
    /// </summary>
    /// <param name="copy">The copy of the creature.</param>
    /// <param name="room">The room's own creature, which takes over.</param>
    /// <param name="madeWithCopy">Everything that was under the copy when it was made.</param>
    internal static void SwitchOffWhatTheCopyLetGo(GameObject copy, GameObject? room, Transform[] madeWithCopy) {
        var roomOwn = room == null ? [] : PartsLetGoBy(room);
        foreach (var part in PartsLetGoBy(copy)) {
            var transform = part.transform;
            if (Array.IndexOf(madeWithCopy, transform) < 0 || transform.IsChildOf(copy.transform) ||
                roomOwn.Contains(part) || IsObjectInRegistry(part)) {
                continue;
            }

            part.SetActive(false);
        }
    }

    /// <summary>
    /// Whether the parent that an action puts something under is none at all, as set in the FSM, rather than a
    /// variable that happens to hold none just now.
    /// </summary>
    private static bool IsNoParent(FsmGameObject? parent) {
        return parent == null || string.IsNullOrEmpty(parent.Name) && parent.Value == null;
    }

    /// <summary>
    /// Whether an object is a player's character, or something on one: this game's own, or the figure of a partner.
    /// </summary>
    private static bool IsAPlayer(GameObject gameObject) {
        var hero = HeroController.instance;
        if (hero != null && gameObject.transform.IsChildOf(hero.transform)) {
            return true;
        }

        return gameObject.transform.root.name.StartsWith(PlayerManager.PlayerContainerName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether what an action puts its object at is the creature itself or one of its own parts (see
    /// <see cref="IsOwnPart"/>), which the copy has too. What else the copy's variables hold may not be what the scene
    /// host's held: the player that a creature is after is the scene host's there and this game's own here.
    /// </summary>
    private static bool FollowsItsOwn(FsmStateAction action, GameObject? followed) {
        return followed != null && (followed == action.Fsm.GameObject || IsOwnPart(action, followed));
    }

    /// <summary>
    /// Writes what an action that puts its object at a place every frame from then on did as its state started: for
    /// a part of the creature, all that the copy needs to go on putting its own part in place every frame (see
    /// <see cref="WriteMovingPart"/>), and for anything else where the object was put, as for an action done once.
    /// </summary>
    /// <param name="data">The network data.</param>
    /// <param name="action">The action.</param>
    /// <param name="gameObject">The object that the action puts in place.</param>
    /// <param name="followsItsOwn">
    /// Whether what the action puts the object at is something the copy has too (see <see cref="FollowsItsOwn"/>).
    /// </param>
    private static bool WriteEveryFramePlace(
        EntityNetworkData data,
        FsmStateAction action,
        GameObject? gameObject,
        bool followsItsOwn = true
    ) {
        var movesHere = followsItsOwn && IsMovingPart(action, gameObject);
        if (!movesHere && !IsKeptInStepByReplays(gameObject, action)) {
            return false;
        }

        data.Packet.Write(movesHere);
        return movesHere ? WritePartPlace(data, action, gameObject) : WritePlace(data, action, gameObject);
    }

    /// <summary>
    /// Reads what <see cref="WriteEveryFramePlace"/> wrote: a part goes on being put in place by the copy itself, and
    /// anything else is put where the game that runs the creature put it.
    /// </summary>
    /// <param name="data">The network data, or null when it sets up the creature's first states.</param>
    /// <param name="action">The action.</param>
    /// <param name="gameObject">The object that the action puts in place.</param>
    /// <param name="followsItsOwn">
    /// Whether what the copy's action puts the object at is the copy or its own part (see
    /// <see cref="FollowsItsOwn"/>): the copy's variable for it is its own, which the scene host does not send, and
    /// one holding anything else is put where the scene host's part was put, once, as before.
    /// </param>
    private static void ReadEveryFramePlace(
        EntityNetworkData? data,
        FsmStateAction action,
        GameObject? gameObject,
        bool followsItsOwn = true
    ) {
        if (data != null && data.Packet.ReadBool()) {
            if (followsItsOwn) {
                PutPartHere(data, action, gameObject);
            } else {
                ReadPlace(data, gameObject);
                PutBack(ReadValues(data, action));
            }

            return;
        }

        ReadPlace(data, gameObject);
    }

    /// <summary>
    /// Writes what an action is given by each of its numbers, points and switches, as it is in this game: what the FSM
    /// sets, or what its variables hold (see <see cref="ReadValues"/>).
    /// </summary>
    private static void WriteValues(EntityNetworkData data, FsmStateAction action) {
        foreach (var field in ValueFieldsOf(action.GetType())) {
            var value = field.GetValue(action);
            if (field.FieldType == typeof(FsmFloat)) {
                data.Packet.Write(value is FsmFloat number ? number.Value : 0f);
            } else if (field.FieldType == typeof(FsmInt)) {
                data.Packet.Write(value is FsmInt number ? number.Value : 0);
            } else if (field.FieldType == typeof(FsmBool)) {
                data.Packet.Write(value is FsmBool flag && flag.Value);
            } else if (field.FieldType == typeof(FsmVector2)) {
                var point = value is FsmVector2 vector ? vector.Value : Vector2.zero;
                data.Packet.Write(point.x);
                data.Packet.Write(point.y);
            } else {
                var point = value is FsmVector3 vector ? vector.Value : Vector3.zero;
                data.Packet.Write(point.x);
                data.Packet.Write(point.y);
                data.Packet.Write(point.z);
            }
        }
    }

    /// <summary>
    /// Gives an action of the copy what the same action was given in the game that runs the creature (see
    /// <see cref="WriteValues"/>). Where a variable holds it, the copy's variable takes it for the action to start
    /// with, and gets back what it held before once the action has started (see <see cref="PutBack"/>): the scene host
    /// sends its variables before the actions that read them, so the variable may already hold a newer value, which it
    /// would not send again.
    /// </summary>
    /// <returns>The variables that were given a value, with what they held before and what they were given.</returns>
    private static List<(NamedVariable Variable, object Own, object Given)>? ReadValues(
        EntityNetworkData data,
        FsmStateAction action
    ) {
        List<(NamedVariable, object, object)>? ownValues = null;
        foreach (var field in ValueFieldsOf(action.GetType())) {
            object given;
            if (field.FieldType == typeof(FsmFloat)) {
                given = data.Packet.ReadFloat();
            } else if (field.FieldType == typeof(FsmInt)) {
                given = data.Packet.ReadInt();
            } else if (field.FieldType == typeof(FsmBool)) {
                given = data.Packet.ReadBool();
            } else if (field.FieldType == typeof(FsmVector2)) {
                given = new Vector2(data.Packet.ReadFloat(), data.Packet.ReadFloat());
            } else {
                given = new Vector3(data.Packet.ReadFloat(), data.Packet.ReadFloat(), data.Packet.ReadFloat());
            }

            if (field.GetValue(action) is not NamedVariable value) {
                continue;
            }

            // A variable has a name, which a number set in the FSM itself does not
            if (!string.IsNullOrEmpty(value.Name)) {
                (ownValues ??= []).Add((value, value.RawValue, given));
            }

            value.RawValue = given;
        }

        return ownValues;
    }

    /// <summary>
    /// Gives the copy's variables back what they held before an action that reads them was given the values that it
    /// started with in the game that runs the creature (see <see cref="ReadValues"/>), unless starting the action
    /// changed them.
    /// </summary>
    private static void PutBack(List<(NamedVariable Variable, object Own, object Given)>? ownValues) {
        if (ownValues == null) {
            return;
        }

        foreach (var (variable, own, given) in ownValues) {
            if (Equals(variable.RawValue, given)) {
                variable.RawValue = own;
            }
        }
    }

    /// <summary>
    /// Like <see cref="PutBack"/>, for an action that reads its variables again every frame: a variable gets back what
    /// it held only if the scene host's value of it came in this frame, with the action's data or just before it,
    /// and is then at least as new as what the action was given. One that came earlier is older than the start of the
    /// action. The scene host sends a variable when it sees it change, which can be a frame after the state it changed
    /// in was entered, so the copy can get the action first: a censer that a boss puts on a curve every frame was put
    /// back on the end of the last throw's curve, where it showed until the new curve came in.
    /// </summary>
    private static void PutBackNewer(List<(NamedVariable Variable, object Own, object Given)>? ownValues) {
        if (ownValues == null) {
            return;
        }

        foreach (var (variable, own, given) in ownValues) {
            if (Equals(variable.RawValue, given) && HostWroteThisFrame(variable)) {
                variable.RawValue = own;
            }
        }
    }

    /// <summary>
    /// The frame in which the scene host's value of each variable of a copy's FSM last reached it (see
    /// <see cref="NoteHostWrote"/>).
    /// </summary>
    private static readonly ConditionalWeakTable<NamedVariable, StrongBox<int>> HostWriteFrames = new();

    /// <summary>
    /// Notes that the scene host's value of a variable has just been written into the copy's FSM.
    /// </summary>
    internal static void NoteHostWrote(NamedVariable variable) {
        HostWriteFrames.GetValue(variable, _ => new StrongBox<int>()).Value = Time.frameCount;
    }

    /// <summary>
    /// Whether the scene host's value of a variable of a copy's FSM was written into it in this frame.
    /// </summary>
    private static bool HostWroteThisFrame(NamedVariable variable) {
        return HostWriteFrames.TryGetValue(variable, out var frame) && frame.Value == Time.frameCount;
    }

    /// <summary>
    /// The fields of a kind of action by which it is given numbers, points and switches, in the same order in both
    /// games.
    /// </summary>
    private static FieldInfo[] ValueFieldsOf(Type type) {
        if (!ValueFields.TryGetValue(type, out var fields)) {
            fields = Array.FindAll(
                type.GetFields(BindingFlags.Instance | BindingFlags.Public),
                field => field.FieldType == typeof(FsmFloat) || field.FieldType == typeof(FsmInt) ||
                         field.FieldType == typeof(FsmBool) || field.FieldType == typeof(FsmVector2) ||
                         field.FieldType == typeof(FsmVector3)
            );
            ValueFields[type] = fields;
        }

        return fields;
    }

    #region iTweenMoveBy

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, iTweenMoveBy action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return false;
        }

        return !IsObjectInRegistry(gameObject);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, iTweenMoveBy action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var id = action.itweenID;

        var args = new Hashtable {
            { "amount", action.vector.IsNone ? Vector3.zero : action.vector.Value }, {
                action.speed.IsNone ? "time" : "speed",
                (float) (action.speed.IsNone
                    ? (action.time.IsNone ? 1.0 : action.time.Value)
                    : (double) action.speed.Value)
            },
            { "delay", (float) (action.delay.IsNone ? 0.0 : (double) action.delay.Value) },
            { "easetype", action.easeType },
            { "looptype", action.loopType },
            { "oncomplete", "iTweenOnComplete" },
            { "oncompleteparams", id },
            { "onstart", "iTweenOnStart" },
            { "onstartparams", id },
            { "ignoretimescale", !action.realTime.IsNone && action.realTime.Value },
            { "space", action.space },
            { "name", action.id.IsNone ? "" : (object) action.id.Value }, {
                "axis",
                action.axis == iTweenFsmAction.AxisRestriction.none
                    ? ""
                    : Enum.GetName(typeof(iTweenFsmAction.AxisRestriction), action.axis) ?? ""
            }
        };

        if (!action.orientToPath.IsNone) {
            args.Add("orienttopath", action.orientToPath.Value);
        }

        if (!action.lookAtObject.IsNone) {
            args.Add(
                "looktarget",
                action.lookAtVector.IsNone
                    ? action.lookAtObject.Value.transform.position
                    : action.lookAtObject.Value.transform.position + action.lookAtVector.Value
            );
        } else if (!action.lookAtVector.IsNone) {
            args.Add("looktarget", action.lookAtVector.Value);
        }

        if (!action.lookAtObject.IsNone || !action.lookAtVector.IsNone) {
            args.Add("looktime", (float) (action.lookTime.IsNone ? 0.0 : (double) action.lookTime.Value));
        }

        action.itweenType = "move";

        iTween.MoveBy(gameObject, args);
        StopTweenOnExit(action, gameObject, "move");
    }

    /// <summary>
    /// Stops a replayed tween when the scene host's FSM leaves its state, as the game's iTweenFsmAction.OnExitiTween
    /// does unless it is told not to (IL: a stopOnExit left unset stops it too).
    /// </summary>
    private static void StopTweenOnExit(iTweenFsmAction action, GameObject gameObject, string tweenType) {
        if (!UndoesOnExit(action)) {
            return;
        }

        UndoOnExit(action, () => {
            if (gameObject != null) {
                iTween.Stop(gameObject, tweenType);
            }
        });
    }

    #endregion

    #region iTweenScaleTo

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, iTweenScaleTo action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return false;
        }

        if (IsObjectInRegistry(gameObject)) {
            return false;
        }

        return action.loopType == iTween.LoopType.none;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, iTweenScaleTo action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var vector = action.vectorScale.IsNone ? Vector3.zero : action.vectorScale.Value;

        if (!action.transformScale.IsNone && action.transformScale.Value) {
            vector = action.transformScale.Value.transform.localScale + vector;
        }

        var id = action.itweenID;

        iTween.ScaleTo(
            gameObject, iTween.Hash(
                "scale",
                vector,
                "name",
                action.id.IsNone ? "" : action.id.Value,
                action.speed.IsNone ? "time" : "speed",
                (float) (action.speed.IsNone
                    ? action.time.IsNone ? 1.0 : action.time.Value
                    : (double) action.speed.Value),
                "delay",
                (float) (action.delay.IsNone ? 0.0 : (double) action.delay.Value),
                "easetype",
                action.easeType,
                "looptype",
                action.loopType,
                "oncomplete",
                "iTweenOnComplete",
                "oncompleteparams",
                id,
                "onstart",
                "iTweenOnStart",
                "onstartparams",
                id,
                "ignoretimescale",
                (action.realTime.IsNone ? 0 : action.realTime.Value ? 1 : 0) > 0
            )
        );
        StopTweenOnExit(action, gameObject, "scale");
    }

    #endregion

    #region SetGravity2dScale

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetGravity2dScale action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return false;
        }

        return !IsObjectInRegistry(gameObject);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetGravity2dScale action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var rigidBody = gameObject.GetComponent<Rigidbody2D>();
        if (rigidBody == null) {
            return;
        }

        rigidBody.gravityScale = action.gravityScale.Value;
    }

    #endregion

    #region SetCollider

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetCollider action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return false;
        }

        if (IsObjectInRegistry(gameObject)) {
            //Logger.Debug("Tried getting SetCollider network data, but entity is in registry");
            return false;
        }

        data.Packet.Write(action.active.Value);
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, SetCollider action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var collider = gameObject.GetComponent<Collider2D>();
        if (collider == null) {
            return;
        }

        var active = data == null ? action.active.Value : data.Packet.ReadBool();
        collider.enabled = active;
        ResetColliderOnExit(action, action.resetOnExit, collider, active);
    }

    #endregion

    #region SetCircleCollider

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetCircleCollider action) {
        if (action.gameObject == null) {
            return false;
        }

        data.Packet.Write(action.active.Value);
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, SetCircleCollider action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var collider = gameObject.GetComponent<CircleCollider2D>();
        if (collider != null) {
            collider.enabled = data == null ? action.active.Value : data.Packet.ReadBool();
        }
    }

    #endregion

    #region SetPolygonCollider

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetPolygonCollider action) {
        if (action.gameObject == null) {
            return false;
        }

        data.Packet.Write(action.active.Value);
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, SetPolygonCollider action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var collider = gameObject.GetComponent<PolygonCollider2D>();
        if (collider != null) {
            var active = data == null ? action.active.Value : data.Packet.ReadBool();
            collider.enabled = active;
            ResetColliderOnExit(action, action.resetOnExit, collider, active);
        }
    }

    #endregion

    /// <summary>
    /// Does for a replayed collider switch what the action itself does when its state is left: switches the collider
    /// the other way again, once the scene host's FSM has moved on from the state the switch was made in.
    ///
    /// Only the switch was ever sent, never the switching back, so on the scene client a collider that is meant to be
    /// live for one state stayed live for good. The worst of them is a grab: a boss that switches on the box of a
    /// grab for the length of one attack left it on here after the first time, and it caught the player every time
    /// they came near her, took a mask, held them in the wounded pose and let go, only to catch them again at once.
    /// </summary>
    /// <param name="action">The action that was replayed.</param>
    /// <param name="resetOnExit">Whether the action switches the collider back when its state is left.</param>
    /// <param name="collider">The collider it switched.</param>
    /// <param name="active">What it switched the collider to.</param>
    private static void ResetColliderOnExit(
        HutongGames.PlayMaker.FsmStateAction action,
        bool resetOnExit,
        Collider2D collider,
        bool active
    ) {
        if (!resetOnExit) {
            return;
        }

        new ActionInState {
            Fsm = action.Fsm,
            StateName = action.State.Name,
            ExitAction = () => {
                if (collider == null) {
                    return;
                }

                collider.enabled = !active;

                if (active) {
                    LetGoOfTheLocalPlayer(collider.gameObject);
                }
            }
        }.Register();
    }

    /// <summary>
    /// Lets the local player out of the wounded pose that a grab by this object put them in, now that the attack the
    /// grab belonged to is over in the scene host's game.
    ///
    /// On the scene client such a grab cannot be finished: the flurry and the last blow that end it belong to the
    /// creature's own FSM, which only runs in the other game. Nothing but the pose's own four seconds let the player
    /// go, so they stood frozen long after the attack was gone. The game ends a grab it does not finish the way a boss
    /// does when she is knocked out of one: the grab box goes off and the player is sent WOUND END.
    /// </summary>
    /// <param name="grabber">The object whose grab box just closed.</param>
    private static void LetGoOfTheLocalPlayer(GameObject grabber) {
        var hero = HeroController.instance;
        if (hero == null) {
            return;
        }

        foreach (var fsm in hero.GetComponents<PlayMakerFSM>()) {
            if (fsm.FsmName != "Roar and Wound States") {
                continue;
            }

            if (fsm.ActiveStateName != "Wound Start" || fsm.FsmVariables.FindFsmGameObject("Wound Sender")?.Value != grabber) {
                return;
            }

            SSMP.Logging.Logger.Info($"Letting the player go from the grab of '{grabber.name}', whose attack is over");
            fsm.SendEvent("WOUND END");
            return;
        }
    }

    /// <summary>
    /// Lets the local player out of the hold of a creature that grabbed them with the game's own grab (HERO GRAB),
    /// when nothing in this game is left to let them go. The hold hides the player, stuns them and takes their
    /// control away, and it has no end of its own: only the creature that holds them ends it, with the blow, the spit
    /// or the release that its own FSM comes to. A copy that stopped playing such a creature before that - the scene
    /// host went another way, or never took the catch - left the player hidden and frozen for good. They are let go
    /// the way a creature lets go of a player who struggled free.
    /// </summary>
    /// <param name="why">Why, for the log.</param>
    internal static void LetGoOfTheHeldLocalPlayer(string why) {
        var hero = HeroController.instance;
        if (hero == null) {
            return;
        }

        foreach (var fsm in hero.GetComponents<PlayMakerFSM>()) {
            if (fsm.FsmName != "Roar and Wound States") {
                continue;
            }

            if (fsm.ActiveStateName != "Hero Grab") {
                return;
            }

            SSMP.Logging.Logger.Info($"Letting the player go from the hold of a creature: {why}");
            fsm.SendEvent("HERO GRAB RELEASE");
            return;
        }
    }

    #region MoveLiftChain

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, MoveLiftChain action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, MoveLiftChain action) {
        var go = action.target.GetSafe(action);
        if (go == null) {
            return;
        }

        var liftChain = go.GetComponent<LiftChain>();
        if (liftChain == null) {
            return;
        }

        action.Apply(liftChain);
    }

    #endregion

    #region StopLiftChain

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, StopLiftChain action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, StopLiftChain action) {
        var go = action.target.GetSafe(action);
        if (go == null) {
            return;
        }

        var liftChain = go.GetComponent<LiftChain>();
        if (liftChain == null) {
            return;
        }

        action.Apply(liftChain);
    }

    #endregion

    #region AccelerateToY

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AccelerateToY action) {
        // Run again only for an entity whose copy moves itself (OwnMotionComponent); the copy of any other is moved by
        // the positions the scene host sends
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        return gameObject != null && OwnMotionComponent.Moves(gameObject);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, AccelerateToY action) {
        // Not in the replay that sets a copy up for a player walking in: that one is placed by positions
        if (data != null) {
            RunInState(action, everyStep: true);
        }
    }

    #endregion

    #region DecelerateV2

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, DecelerateV2 action) {
        // As AccelerateToY: slowed down step by step on a copy that moves itself, left to positions on any other
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        return gameObject != null && OwnMotionComponent.Moves(gameObject);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, DecelerateV2 action) {
        if (data != null) {
            RunInState(action, everyStep: true);
        }
    }

    #endregion

    #region SetIsKinematic2d

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetIsKinematic2d action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetIsKinematic2d action) {
        // Nothing sent makes the creature's own body move by itself. The copy shown while the other game runs it is
        // put where that game has it, frame by frame, and none of its bodies moves by itself
        // (EntityInitializer.RemoveClientTypes): made to, it fell between two of those frames onto the floor under
        // where it was drawn, and a player standing there was hit by what looked like thin air. The other way is left
        // to happen, since a body that does not move by itself is always safe - and it takes back one that a run of
        // the copy's own FSM here set moving (PlayHere). The room's own body, taken over, keeps the kind the other
        // game last left it as (BodyTypeComponent), not one its FSM starts with.
        var go = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (go == null) {
            return;
        }

        var rigidbody = go.GetComponent<Rigidbody2D>();
        if (rigidbody == null) {
            return;
        }

        // What the other game made one of the creature's bodies is noted all the same, for when the creature lets go of
        // it (RestoreOwnPhysics): one set moving while it was carried, and kept still here, moves by itself from then on
        if (go != action.Fsm.GameObject) {
            EntityInitializer.NoteBodyType(
                rigidbody,
                action.isKinematic.Value ? RigidbodyType2D.Kinematic : RigidbodyType2D.Dynamic
            );
        }

        if (!action.isKinematic.Value && go.transform.IsChildOf(action.Fsm.GameObject.transform)) {
            return;
        }

        rigidbody.isKinematic = action.isKinematic.Value;
    }

    #endregion
}
