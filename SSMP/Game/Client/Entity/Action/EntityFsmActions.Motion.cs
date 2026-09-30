using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetPosition action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return false;
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
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetRotation action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return false;
        }

        if (IsObjectInRegistry(gameObject)) {
            //Logger.Debug("Tried getting SetPosition network data, but entity is in registry");
            return false;
        }

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
    /// anything not its own.
    /// </summary>
    /// <param name="data">The network data.</param>
    /// <param name="action">The action.</param>
    /// <param name="gameObject">The object that the action moves.</param>
    /// <returns>Whether it moves a part of its creature, and so anything was written.</returns>
    private static bool WriteMovingPart(EntityNetworkData data, FsmStateAction action, GameObject? gameObject) {
        var creature = action.Fsm.GameObject;
        if (gameObject == null || creature == null || !gameObject.transform.IsChildOf(creature.transform) ||
            !WritePlace(data, action, gameObject)) {
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
        if (go == null || !action.isKinematic.Value && go.transform.IsChildOf(action.Fsm.GameObject.transform)) {
            return;
        }

        var rigidbody = go.GetComponent<Rigidbody2D>();
        if (rigidbody == null) {
            return;
        }

        rigidbody.isKinematic = action.isKinematic.Value;
    }

    #endregion
}
