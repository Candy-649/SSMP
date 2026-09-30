using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
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
    #region SetFsmBool

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetFsmBool action) {
        if (action.setValue == null) {
            return false;
        }

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == action.Fsm.GameObject) {
            return false;
        }

        var setValue = action.setValue.Value;
        data.Packet.Write(setValue);

        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetFsmBool action) {
        var setValue = data.Packet.ReadBool();

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var fsm = ActionHelpers.GetGameObjectFsm(gameObject, action.fsmName.Value);
        if (fsm == null) {
            return;
        }

        var fsmBool = fsm.FsmVariables.FindFsmBool(action.variableName.Value);

        fsmBool?.Value = setValue;
    }

    #endregion

    #region SetFsmInt

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetFsmInt action) {
        if (action.setValue == null) {
            return false;
        }

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == action.Fsm.GameObject) {
            return false;
        }

        var setValue = action.setValue.Value;
        data.Packet.Write(setValue);

        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetFsmInt action) {
        var setValue = data.Packet.ReadInt();

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var fsm = ActionHelpers.GetGameObjectFsm(gameObject, action.fsmName.Value);
        if (fsm == null) {
            return;
        }

        var fsmInt = fsm.FsmVariables.GetFsmInt(action.variableName.Value);

        fsmInt?.Value = setValue;
    }

    #endregion

    #region SetFsmFloat

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetFsmFloat action) {
        // TODO: if action.setValue can be a reference, make sure to network it
        if (action.setValue == null) {
            return false;
        }

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        return gameObject != action.Fsm.GameObject;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetFsmFloat action) {
        if (action.setValue == null) {
            return;
        }

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var fsm = ActionHelpers.GetGameObjectFsm(gameObject, action.fsmName.Value);
        if (fsm == null) {
            return;
        }

        var fsmFloat = fsm.FsmVariables.GetFsmFloat(action.variableName.Value);

        fsmFloat?.Value = action.setValue.Value;
    }

    #endregion

    #region SetFsmString

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SetFsmString action) {
        // TODO: if action.setValue can be a reference, make sure to network it
        if (action.setValue == null) {
            return false;
        }

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        return gameObject != action.Fsm.GameObject;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SetFsmString action) {
        if (action.setValue == null) {
            return;
        }

        var gameObject = action.Fsm.GetOwnerDefaultTarget(action.gameObject);
        if (gameObject == null) {
            return;
        }

        var fsm = ActionHelpers.GetGameObjectFsm(gameObject, action.fsmName.Value);
        if (fsm == null) {
            return;
        }

        var fsmString = fsm.FsmVariables.GetFsmString(action.variableName.Value);
        if (fsmString == null) {
            return;
        }

        fsmString.Value = action.setValue.Value;
    }

    #endregion

    #region SendEventByName

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SendEventByName action) {
        return action.eventTarget.gameObject.GameObject.Value != action.Fsm.GameObject.gameObject;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SendEventByName action) {
        if (action.delay.Value < 1.0 / 1000.0) {
            BossRoomCoop.SendNetworkEvent(action.Fsm, action.eventTarget, action.sendEvent.Value);
        } else {
            // We need to delay the event sending ourselves, because the FSM that we are executing in is not enabled
            // The usual implementation of SendEventByName will thus not work
            MonoBehaviourUtil.Instance.StartCoroutine(DelayEvent());

            IEnumerator DelayEvent() {
                yield return new WaitForSeconds(action.delay.Value);

                BossRoomCoop.SendNetworkEvent(action.Fsm, action.eventTarget, action.sendEvent.Value);
            }
        }
    }

    #endregion

    #region SendEventByNameV2

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SendEventByNameV2 action) {
        return action.eventTarget.gameObject.GameObject.Value != action.Fsm.GameObject.gameObject;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SendEventByNameV2 action) {
        if (action.delay.Value < 1.0 / 1000.0) {
            BossRoomCoop.SendNetworkEvent(action.Fsm, action.eventTarget, action.sendEvent.Value);
        } else {
            // We need to delay the event sending ourselves, because the FSM that we are executing in is not enabled
            // The usual implementation of SendEventByNameV2 will thus not work
            MonoBehaviourUtil.Instance.StartCoroutine(DelayEvent());

            IEnumerator DelayEvent() {
                yield return new WaitForSeconds(action.delay.Value);

                BossRoomCoop.SendNetworkEvent(action.Fsm, action.eventTarget, action.sendEvent.Value);
            }
        }
    }

    #endregion

    #region SendEventToRegister

    // A creature tells the room what it is doing by broadcasting an event to everything registered for it: the floor
    // that grows back at once when a boss is stunned, the rocks that burst in the air, the camera that starts to follow
    // the player up for the last part of a fight. Only the scene host runs the creature, so none of it happened in the
    // other game before.

    /// <summary>
    /// Events about the player that a creature has just dealt with, which each game counts for its own player.
    /// Broadcast again in the other game they would land on that game's player: the benches, lifts and camera locks
    /// that react to the player being hit, grabbed or caught in a tendril, the followers and effects that are cleared
    /// away. Also a festival flea being tinked, which only the game that runs the fleas counts: from it the other
    /// game's copy of that festival game would finish rounds of its own and send out a rival that only it has.
    /// </summary>
    private static readonly HashSet<string> PlayerEvents = [
        "HERO DAMAGED", "FSM CANCEL", "HORNET CAUGHT", "TENDRIL HORNET CAPTURED", "TENDRIL HORNET ESCAPED",
        "HORNET BONKED", "HORNET BONKED HEAVY", "REGOOPED", "END FOLLOWERS INSTANT", "CLEAR EFFECTS", "DID PARRY",
        "PARRY REMINDER", "FLEA TINKED"
    ];

    /// <summary>
    /// Whether an event is one about this game's player that a creature has just dealt with (see
    /// <see cref="PlayerEvents"/>).
    /// </summary>
    internal static bool IsPlayerEvent(string eventName) {
        return PlayerEvents.Contains(eventName);
    }

    /// <summary>
    /// Events of a festival game that a flea sends when it scores or is dropped. Both players play such a game with the
    /// same fleas, so they count in both games: a point is a point for both players, whoever hit the flea or got past
    /// it, and a dropped flea counts against both. Only the game the flea belongs to takes them, though: broadcast to
    /// the whole room, a point would also land on another game of the festival that the local player is playing there.
    /// </summary>
    private static readonly HashSet<string> GameEvents = ["SCORE", "FLEA FAIL"];

    /// <summary>
    /// The game's own lists of what is registered for each event, by the hash of the event's name.
    /// </summary>
    private static readonly FieldInfo? EventRegistersField =
        typeof(EventRegister).GetField("_eventRegister", StaticNonPublicFlags);

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SendEventToRegister action) {
        // Sent, because some creatures keep the event in a variable
        data.Packet.Write(action.eventName.Value);
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SendEventToRegister action) {
        SendEventToRoom(action.Fsm, data == null ? action.eventName.Value : data.Packet.ReadString());
    }

    #endregion

    #region SendEventToRegisterV2

    // The owner it leaves out is a creature kept in step, which SendEventToRoom leaves out anyway

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SendEventToRegisterV2 action) {
        data.Packet.Write(action.EventName.Value);
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SendEventToRegisterV2 action) {
        SendEventToRoom(action.Fsm, data == null ? action.EventName.Value : data.Packet.ReadString());
    }

    #endregion

    #region SendEventToRegisterDelay

    // Sent when the delay runs out in the scene host's game rather than on entering the state (see FsmActionHooks), so
    // it is not sent at all when the creature leaves the state first, as the game does not send it then either

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SendEventToRegisterDelay action) {
        data.Packet.Write(action.EventName.Value);
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SendEventToRegisterDelay action) {
        SendEventToRoom(action.Fsm, data == null ? action.EventName.Value : data.Packet.ReadString());
    }

    #endregion

    /// <summary>
    /// Broadcasts an event that a creature broadcast in the scene host's game to what is registered for it in this
    /// game, the way EventRegister.SendEvent does, except to what is left to the scene host's game.
    /// </summary>
    /// <param name="fsm">The FSM of the creature that broadcast the event.</param>
    /// <param name="eventName">The name of the event.</param>
    private static void SendEventToRoom(HutongGames.PlayMaker.Fsm fsm, string eventName) {
        if (PlayerEvents.Contains(eventName) ||
            EventRegistersField?.GetValue(null) is not Dictionary<int, List<EventRegister>> registers ||
            !registers.TryGetValue(EventRegister.GetEventHashCode(eventName), out var list)) {
            return;
        }

        // An event of a game goes only to what is registered on the way up from where the flea belongs in the room,
        // which is where the room's own copy of it still sits: it sleeps here, so it never flies off
        Transform? within = null;
        if (GameEvents.Contains(eventName)) {
            within = EntityProcessor.GetRoomObjectOfCopy(fsm.GameObject)?.transform;
            if (within == null) {
                return;
            }
        }

        BossRoomCoop.RunAsNetworkSender(fsm, () => {
            foreach (var register in list) {
                if (!IsLeftToTheSceneHost(register) && (within == null || within.IsChildOf(register.transform))) {
                    register.ReceiveEvent();
                }
            }
        });
    }

    /// <summary>
    /// Whether what is registered for a broadcast is left to the scene host's game:
    /// - a creature's copy, whose own FSMs follow the scene host's through replays and could be switched on by the
    ///   event; the parts under the copy run here as they do there, so they take it like the scene host's parts do;
    /// - anything in the room's own copy of a creature, which sleeps while the other game runs the creature;
    /// - an arena, which follows the scene host's game its own way: one woken here would lock this game's player in
    ///   even when they are outside it. What lies inside an arena, such as a floor that breaks, takes the event.
    /// </summary>
    private static bool IsLeftToTheSceneHost(EventRegister register) {
        if (EntityProcessor.IsRegistered(register.gameObject) || register.GetComponent<BattleScene>() != null) {
            return true;
        }

        for (var current = register.transform.parent; current != null; current = current.parent) {
            if (EntityProcessor.IsRoomObject(current.gameObject)) {
                return true;
            }
        }

        return false;
    }

    #region SendHealthManagerDeathEvent

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, SendHealthManagerDeathEvent action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData data, SendHealthManagerDeathEvent action) {
        var gameObject = action.target.OwnerOption == OwnerDefaultOption.UseOwner
            ? action.Owner
            : action.target.GameObject.Value;

        if (gameObject == null) {
            return;
        }

        var healthManager = gameObject.GetComponent<HealthManager>();
        if (healthManager == null) {
            return;
        }

        healthManager.SendDeathEvent();
    }

    #endregion
}
