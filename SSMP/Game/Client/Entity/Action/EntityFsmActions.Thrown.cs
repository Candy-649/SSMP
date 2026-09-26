using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Packet.Data;
using UnityEngine;

// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedParameter.Local

namespace SSMP.Game.Client.Entity.Action;

// What a creature does to the things it throws or puts in place: turns them the way it faces itself, sends them off,
// puts them somewhere, sets how hard they hit. A thrown thing is this game's own object, made by the replay of the
// throw, and none of this reached it before. A flying boss turns each sickle it throws to face its own way, and the
// sickle then flies off the way it faces: the copy of the sickle kept whichever way its pooled object last faced, and
// flew off behind the boss about half the time. The wind slashes, fireballs and whirlwinds that other bosses send off
// by the way they face hung in the air where they were made.
//
// Each of these is sent as what the action did in the game that runs the creature - which way the thing faces, how
// fast it goes, where it is - and not done again here, where the thrower may not face the same way yet and the dice
// come up differently. It reaches the thrown thing before the event that sends it off, as it does there. The creature
// itself and any other creature are left out: their own position and scale keep them in step.
internal static partial class EntityFsmActions {
    /// <summary>
    /// Whether an object is kept in step with the other game by nothing but replays: anything but the creature whose
    /// FSM it is, other creatures, whose position and scale are sent by themselves, and the cameras with what hangs on
    /// them. Each game moves its cameras for its own player: a creature that lands hard jolts the camera of the game it
    /// runs in, and put where the scene host's camera was, the scene client's view jumped to the partner's.
    /// </summary>
    private static bool IsKeptInStepByReplays(GameObject? gameObject, FsmStateAction action) {
        var cameras = GameCameras.instance;
        return gameObject != null && gameObject != action.Fsm.GameObject && !IsObjectInRegistry(gameObject) &&
               (cameras == null || !gameObject.transform.IsChildOf(cameras.transform));
    }

    /// <summary>Writes the scale an action left an object with, if nothing else keeps that object in step.</summary>
    private static bool WriteScale(EntityNetworkData data, FsmStateAction action, GameObject? gameObject) {
        if (!IsKeptInStepByReplays(gameObject, action)) {
            return false;
        }

        var scale = gameObject!.transform.localScale;
        data.Packet.Write(scale.x);
        data.Packet.Write(scale.y);
        data.Packet.Write(scale.z);
        return true;
    }

    /// <summary>
    /// Gives an object the scale that the game running the creature left it with. The replay that sets up a creature's
    /// first states has nothing to go on, since only that game knows what was done, and leaves it alone.
    /// </summary>
    private static void ReadScale(EntityNetworkData? data, GameObject? gameObject) {
        if (data == null || gameObject == null) {
            return;
        }

        gameObject.transform.localScale = new Vector3(
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat()
        );
    }

    /// <summary>Writes the speed an action left an object with, if nothing else keeps that object in step.</summary>
    private static bool WriteVelocity(EntityNetworkData data, FsmStateAction action, GameObject? gameObject) {
        if (!IsKeptInStepByReplays(gameObject, action)) {
            return false;
        }

        var body = gameObject!.GetComponent<Rigidbody2D>();
        if (body == null) {
            return false;
        }

        data.Packet.Write(body.linearVelocity.x);
        data.Packet.Write(body.linearVelocity.y);
        return true;
    }

    /// <summary>Gives an object the speed that the game running the creature left it with.</summary>
    private static void ReadVelocity(EntityNetworkData? data, GameObject? gameObject) {
        if (data == null || gameObject == null) {
            return;
        }

        var body = gameObject.GetComponent<Rigidbody2D>();
        if (body != null) {
            body.linearVelocity = new Vector2(data.Packet.ReadFloat(), data.Packet.ReadFloat());
        }
    }

    /// <summary>
    /// Writes where an action left an object, if nothing else keeps that object in step. The place is taken from the
    /// object's parent, which is the same object in both games: a part of a creature then stays where it was put on
    /// the creature's copy, which trails the creature a little.
    /// </summary>
    private static bool WritePlace(EntityNetworkData data, FsmStateAction action, GameObject? gameObject) {
        if (!IsKeptInStepByReplays(gameObject, action)) {
            return false;
        }

        var place = gameObject!.transform.localPosition;
        data.Packet.Write(place.x);
        data.Packet.Write(place.y);
        data.Packet.Write(place.z);
        return true;
    }

    /// <summary>Puts an object where the game running the creature left it.</summary>
    private static void ReadPlace(EntityNetworkData? data, GameObject? gameObject) {
        if (data == null || gameObject == null) {
            return;
        }

        gameObject.transform.localPosition = new Vector3(
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat()
        );
    }

    #region MatchScaleSign

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, MatchScaleSign action) {
        return WriteScale(data, action, action.Fsm.GetOwnerDefaultTarget(action.Target));
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, MatchScaleSign action) {
        ReadScale(data, action.Fsm.GetOwnerDefaultTarget(action.Target));
    }

    #endregion

    #region MatchScaleSignV2

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, MatchScaleSignV2 action) {
        return WriteScale(data, action, action.Fsm.GetOwnerDefaultTarget(action.Target));
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, MatchScaleSignV2 action) {
        ReadScale(data, action.Fsm.GetOwnerDefaultTarget(action.Target));
    }

    #endregion

    #region FlipScale

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, FlipScale action) {
        return WriteScale(data, action, action.Fsm.GetOwnerDefaultTarget(action.gameObject));
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, FlipScale action) {
        ReadScale(data, action.Fsm.GetOwnerDefaultTarget(action.gameObject));
    }

    #endregion

    #region SetVelocityByScale

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetVelocityByScale action) {
        return WriteVelocity(data, action, action.Fsm.GetOwnerDefaultTarget(action.gameObject));
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, SetVelocityByScale action) {
        ReadVelocity(data, action.Fsm.GetOwnerDefaultTarget(action.gameObject));
    }

    #endregion

    #region FlingObject

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, FlingObject action) {
        return WriteVelocity(data, action, action.Fsm.GetOwnerDefaultTarget(action.flungObject));
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, FlingObject action) {
        ReadVelocity(data, action.Fsm.GetOwnerDefaultTarget(action.flungObject));
    }

    #endregion

    #region AddForce2d

    // A push only moves the object in the next step of physics, so the push itself is sent rather than a speed

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AddForce2d action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (!IsKeptInStepByReplays(gameObject, action) || gameObject!.GetComponent<Rigidbody2D>() == null) {
            return false;
        }

        // Worked out as the action does it
        var force = action.vector.IsNone ? new Vector2(action.x.Value, action.y.Value) : action.vector.Value;
        if (!action.vector3.IsNone) {
            force.x = action.vector3.Value.x;
            force.y = action.vector3.Value.y;
        }

        if (!action.x.IsNone) {
            force.x = action.x.Value;
        }

        if (!action.y.IsNone) {
            force.y = action.y.Value;
        }

        data.Packet.Write(force.x);
        data.Packet.Write(force.y);

        data.Packet.Write(!action.atPosition.IsNone);
        if (!action.atPosition.IsNone) {
            data.Packet.Write(action.atPosition.Value.x);
            data.Packet.Write(action.atPosition.Value.y);
        }

        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, AddForce2d action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (data == null || gameObject == null) {
            return;
        }

        var force = new Vector2(data.Packet.ReadFloat(), data.Packet.ReadFloat());
        var body = gameObject.GetComponent<Rigidbody2D>();
        if (body == null) {
            return;
        }

        if (data.Packet.ReadBool()) {
            var at = new Vector2(data.Packet.ReadFloat(), data.Packet.ReadFloat());
            body.AddForceAtPosition(force, at, action.forceMode);
        } else {
            body.AddForce(force, action.forceMode);
        }
    }

    #endregion

    #region SetPosition2d

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetPosition2d action) {
        // Put in place every frame from then on, it was not put anywhere yet
        if (action.everyFrame || action.lateUpdate) {
            return false;
        }

        return WritePlace(data, action, action.Fsm.GetOwnerDefaultTarget(action.gameObject));
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, SetPosition2d action) {
        ReadPlace(data, action.Fsm.GetOwnerDefaultTarget(action.gameObject));
    }

    #endregion

    #region SetPosition2D

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetPosition2D action) {
        return WritePlace(data, action, action.Fsm.GetOwnerDefaultTarget(action.GameObject));
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, SetPosition2D action) {
        ReadPlace(data, action.Fsm.GetOwnerDefaultTarget(action.GameObject));
    }

    #endregion

    #region SetPositionToObject

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetPositionToObject action) {
        return WritePlace(data, action, action.Fsm.GetOwnerDefaultTarget(action.gameObject));
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, SetPositionToObject action) {
        ReadPlace(data, action.Fsm.GetOwnerDefaultTarget(action.gameObject));
    }

    #endregion

    #region SetPositionToObject2D

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetPositionToObject2D action) {
        return WritePlace(data, action, action.Fsm.GetOwnerDefaultTarget(action.gameObject));
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, SetPositionToObject2D action) {
        ReadPlace(data, action.Fsm.GetOwnerDefaultTarget(action.gameObject));
    }

    #endregion

    #region Translate

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, Translate action) {
        // Moved every frame or step from then on, it was not moved yet
        if (action.everyFrame || action.lateUpdate || action.fixedUpdate) {
            return false;
        }

        return WritePlace(data, action, action.Fsm.GetOwnerDefaultTarget(action.gameObject));
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, Translate action) {
        ReadPlace(data, action.Fsm.GetOwnerDefaultTarget(action.gameObject));
    }

    #endregion

    #region SetRandomRotation

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetRandomRotation action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (!IsKeptInStepByReplays(gameObject, action)) {
            return false;
        }

        var angles = gameObject!.transform.localEulerAngles;
        data.Packet.Write(angles.x);
        data.Packet.Write(angles.y);
        data.Packet.Write(angles.z);
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, SetRandomRotation action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (data == null || gameObject == null) {
            return;
        }

        gameObject.transform.localEulerAngles = new Vector3(
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat(),
            data.Packet.ReadFloat()
        );
    }

    #endregion

    #region SetDamageHeroAmount

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetDamageHeroAmount action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.target);
        if (!IsKeptInStepByReplays(gameObject, action)) {
            return false;
        }

        var damageHero = gameObject!.GetComponent<DamageHero>();
        if (damageHero == null) {
            return false;
        }

        data.Packet.Write(damageHero.damageDealt);
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, SetDamageHeroAmount action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.target);
        if (data == null || gameObject == null) {
            return;
        }

        var damageHero = gameObject.GetComponent<DamageHero>();
        if (damageHero != null) {
            damageHero.damageDealt = data.Packet.ReadInt();
        }
    }

    #endregion
}
