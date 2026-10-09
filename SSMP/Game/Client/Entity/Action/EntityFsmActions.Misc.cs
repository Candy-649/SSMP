using System;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Object = UnityEngine.Object;
using Logger = SSMP.Logging.Logger;

// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedParameter.Local
#pragma warning disable CS0618
#pragma warning disable CS8600
#pragma warning disable CS8618

namespace SSMP.Game.Client.Entity.Action;

internal static partial class EntityFsmActions {
    #region ActivateAllChildren

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(
        EntityNetworkData _,
        HutongGames.PlayMaker.Actions.ActivateAllChildren __
    ) => true;

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(
        EntityNetworkData _,
        HutongGames.PlayMaker.Actions.ActivateAllChildren action
    ) => action.OnEnter();

    #endregion

    #region DisplayBossTitle

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData _, DisplayBossTitle __) => true;

    /// <summary>
    /// Applies network data to the FSM action. The name of a boss comes up when its fight begins, and only in the game
    /// that runs the boss, so whoever came into the room second never saw it. It only shows the name on screen.
    /// </summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData _, DisplayBossTitle action) => action.OnEnter();

    #endregion

    #region SetTag

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetTag action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetTag action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        gameObject.tag = action.tag.Value;
    }

    #endregion

    #region DestroyObject

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, DestroyObject action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, DestroyObject action) {
        var gameObject = action.gameObject.Value;
        if (gameObject == null) {
            return;
        }

        var delay = action.delay.Value;
        if (delay <= 0) {
            Object.Destroy(gameObject);
        } else {
            Object.Destroy(gameObject, delay);
        }

        if (action.detachChildren.Value) {
            gameObject.transform.DetachChildren();
        }
    }

    #endregion

    #region SetStringValue

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetStringValue action) {
        return action is { stringVariable: not null, stringValue: not null };
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetStringValue action) {
        action.stringVariable.Value = action.stringValue.Value;
    }

    #endregion

    #region GetRandomChild

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, GetRandomChild action) {
        if (!RandomActionValues.TryGetValue(action, out var queue)) {
            return false;
        }

        if (queue.Count == 0) {
            //Logger.Debug("Getting data for GetRandomChild has not enough items in queue");
            return false;
        }

        var randomIndex = (int) queue.Dequeue();
        data.Packet.Write((byte) randomIndex);

        queue.Clear();

        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, GetRandomChild action) {
        var randomIndex = data.Packet.ReadByte();

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var childCount = gameObject.transform.childCount;
        if (childCount == 0) {
            return;
        }

        action.storeResult.Value = gameObject.transform.GetChild(randomIndex).gameObject;
    }

    #endregion

    #region DestroyComponent

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, DestroyComponent action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, DestroyComponent action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var component = gameObject.GetComponent(ReflectionUtils.GetGlobalType(action.component.Value));
        if (component == null) {
            return;
        }

        // A client-side controller body can be made kinematic, but destroying it invalidates cached references in
        // native death/corpse components. Corpse-owned bodies must remain intact for the native death lifecycle.
        if (component is Rigidbody2D rigidbody) {
            EntityInitializer.ConfigureClientRigidbody(rigidbody);
            return;
        }

        Object.Destroy(component);
    }

    #endregion

    #region AddComponent

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, AddComponent action) {
        return !action.removeOnExit.Value;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, AddComponent action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var component = gameObject.AddComponent(ReflectionUtils.GetGlobalType(action.component.Value));
        action.storeComponent.Value = component;
    }

    #endregion

    #region PreBuildTK2DSprites

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, PreBuildTK2DSprites action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, PreBuildTK2DSprites action) {
        var gameObject = action.gameObject.Value;
        if (gameObject == null) {
            return;
        }

        var sprites = action.useChildren
            ? gameObject.GetComponentsInChildren<tk2dSprite>(true)
            : gameObject.GetComponents<tk2dSprite>();

        foreach (var sprite in sprites) {
            sprite.ForceBuild();
        }
    }

    #endregion

    #region CallMethodProper

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, CallMethodProper action) {
        //Logger.Debug($"Getting network data for CallMethodProper: {action.Fsm.GameObject.name}, {action.Fsm.Name}");

        return action.Fsm.GameObject.name.StartsWith("Colosseum Manager") &&
               action.Fsm.Name.Equals("Battle Control") ||
               action.Fsm.GameObject.name.StartsWith("Mantis Lord Throne") &&
               action.Fsm.Name.Equals("Mantis Throne Main") ||
               action.Fsm.GameObject.name.Equals("Radiance") &&
               action.Fsm.Name.Equals("Control") ||
               // Where the effects that the health of a creature spawns on a hit come out, like those of a crest's
               // combo: moved along with those of SetHitEffectOrigin
               action.behaviour.Value == nameof(HealthManager) &&
               action.methodName.Value == nameof(HealthManager.SetEffectOrigin);
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, CallMethodProper action) {
        if (action.behaviour.Value == null) {
            return;
        }

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var component = gameObject.GetComponent(action.behaviour.Value) as MonoBehaviour;
        if (component == null) {
            return;
        }

        var type = component.GetType();
        var methodInfo = type.GetMethod(action.methodName.Value);
        if (methodInfo == null) {
            return;
        }

        var parameterInfo = methodInfo.GetParameters();

        object obj;
        if (parameterInfo.Length == 0) {
            obj = methodInfo.Invoke(component, null);
        } else {
            var paramArray = new object[action.parameters.Length];

            for (var i = 0; i < action.parameters.Length; i++) {
                var fsmVar = action.parameters[i];
                fsmVar.UpdateValue();
                paramArray[i] = fsmVar.GetValue();
            }

            try {
                obj = methodInfo.Invoke(component, paramArray);
            } catch (Exception e) {
                Logger.Error($"Error applying CallMethodProper:\n{e}");
                return;
            }
        }

        if (action.storeResult.Type == VariableType.Unknown) {
            return;
        }

        action.storeResult.SetValue(obj);
    }

    #endregion

    #region SetHitEffectOrigin

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData _, SetHitEffectOrigin __) => true;

    /// <summary>
    /// Applies network data to the FSM action. Where the sparks of a hit come out of a creature, which some move for as
    /// long as they hold a pose: a boss lying dazed sparks higher up than one standing. The copy kept them where they
    /// started, and a hit on it in such a pose sparked well away from where it was struck.
    /// </summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData _, SetHitEffectOrigin action) => action.OnEnter();

    #endregion

    #region SetDeathRespawnNonLethal

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData _, SetDeathRespawnNonLethal __) => true;

    /// <summary>
    /// Applies network data to the FSM action. A character that a player spars with tells the game where a player it
    /// beats comes back - beside it, rather than at a bench - and only the game that runs the character heard it, so
    /// the other player, beaten in the same spar, woke up on their bench. It only says where a death that is not lethal
    /// brings the player back, which the game forgets again at the next room (GameManager.OnNextLevelReady).
    /// </summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData _, SetDeathRespawnNonLethal action) =>
        action.OnEnter();

    #endregion

    #region ActivateInteractible

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData _, ActivateInteractible __) => true;

    /// <summary>
    /// Applies network data to the FSM action. Whether a creature that talks can be talked to now: a guardian at rest
    /// can, one that is fighting cannot. Only the game that runs the creature switched it, so the copy offered a talk
    /// whenever its room had loaded with one, and none when it had not. A talk with the copy is run in the local
    /// player's own game (Entity.TalkHere), and it is offered as the creature offers it.
    /// </summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData _, ActivateInteractible action) =>
        action.OnEnter();

    #endregion

    #region SendMessage

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SendMessage action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SendMessage action) {
        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        object parameter = action.functionCall.ParameterType switch {
            "Array" => action.functionCall.ArrayParameter.Values,
            "Color" => action.functionCall.ColorParameter.Value,
            "Enum" => action.functionCall.EnumParameter.Value,
            "GameObject" => action.functionCall.GameObjectParameter.Value,
            "Material" => action.functionCall.MaterialParameter.Value,
            "Object" => action.functionCall.ObjectParameter.Value,
            "Quaternion" => action.functionCall.QuaternionParameter.Value,
            "Rect" => action.functionCall.RectParamater.Value,
            "Texture" => action.functionCall.TextureParameter.Value,
            "Vector2" => action.functionCall.Vector2Parameter.Value,
            "Vector3" => action.functionCall.Vector3Parameter.Value,
            "bool" => action.functionCall.BoolParameter.Value,
            "float" => action.functionCall.FloatParameter.Value,
            "int" => action.functionCall.IntParameter.Value,
            "string" => action.functionCall.StringParameter.Value,
            _ => null
        };

        switch (action.delivery) {
            case SendMessage.MessageType.SendMessage:
                gameObject.SendMessage(action.functionCall.FunctionName, parameter, action.options);
                break;
            case SendMessage.MessageType.SendMessageUpwards:
                gameObject.SendMessageUpwards(action.functionCall.FunctionName, parameter, action.options);
                break;
            case SendMessage.MessageType.BroadcastMessage:
                gameObject.BroadcastMessage(action.functionCall.FunctionName, parameter, action.options);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    #endregion

    #region EndGGBossScene

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, EndGGBossScene action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, EndGGBossScene action) {
        if (BossSceneController.Instance) {
            BossSceneController.Instance.EndBossScene();
        }
    }

    #endregion

    #region SetDarknessLevel

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetDarknessLevel action) {
        if (action.SetLevel.IsNone) {
            return false;
        }

        data.Packet.Write(action.SetLevel.Value);

        return true;
    }

    /// <summary>
    /// Applies network data to the FSM action. The dark of a room is drawn around each player's own character, so when a
    /// creature changes how dark the room is in the game of the scene host, it changes around the local player too.
    /// </summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetDarknessLevel action) {
        var level = data.Packet.ReadInt();
        DarknessRegion.SetDarknessLevel(level);
        Logger.Info(
            $"'{action.Fsm.GameObjectName}' set the darkness of the room to {level} in the scene host's game, " +
            "and here too"
        );
    }

    #endregion
}
