using System;
using System.Collections.Generic;
using SSMP.Game.Client.Entity.Action;
using SSMP.Game.Client.Save;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// What a wound of the local player makes the copy of a creature that the other game runs do. The game tells the state
/// machines of a creature of every blow that wounds it (HealthManager.TakeDamage sends HIT and TOOK DAMAGE), and many
/// go somewhere on it: a creature that walks about turns on whoever struck it and strikes back, one flinches, one
/// curls up. A copy runs none of its state machines, and the game that runs the creature only counted the partner's
/// blows (HitCounters), so a creature that the scene client struck took the damage and did nothing else, whatever
/// kind it was (USER 10-17, "我走到它的攻击范围了都不动，打它也不动", "这样的情况不止这个虫子会有…和虫子是什么反而关系不
/// 大"). Telling the scene host's creature alone had it answer a round trip late in the striker's game (2c53ec5), so
/// the copy plays it at once, as a strike is played (PlayHere), and the creature in the scene host's game the same
/// way where it is, going after the player who struck it unless it fights a player already (CoopHits.GoAfterSetter).
/// A blow that the creature is immune to, that extracts from it, or that leaves it no health sets off nothing here
/// (CoopHits): the game tells it nothing more of the first two, and the scene host's game tells it of the last along
/// with its death.
/// </summary>
internal partial class Entity {
    /// <summary>
    /// The events of a wound that a state machine of the creature may go somewhere on, in the order the game tells
    /// them: a state machine that goes somewhere on the first goes on from there.
    /// </summary>
    private static readonly string[] WoundEvents = ["HIT", "TOOK DAMAGE"];

    /// <summary>
    /// Whether an event is one of a wound (see <see cref="WoundEvents"/>).
    /// </summary>
    /// <param name="eventName">The event.</param>
    internal static bool IsWoundEvent(string eventName) {
        return Array.IndexOf(WoundEvents, eventName) >= 0;
    }

    /// <summary>
    /// The state machines of the copy that a wound of the local player sets off, by their index, with the event each
    /// goes somewhere on from the state the game that runs the creature says it is in, or the state it is in here
    /// while it runs here. A state machine that each game runs by itself heard of the wound here already, and one that
    /// a wound takes into a state that counts it is told of it by the game that runs the creature (HitCounters).
    /// </summary>
    internal List<(byte FsmIndex, string EventName)> FindWoundReactions() {
        var reactions = new List<(byte, string)>();
        if (!_isControlled || Object.Client == null || !Object.Client.activeInHierarchy) {
            return reactions;
        }

        for (var index = 0; index < _fsms.Client.Count && index < byte.MaxValue; index++) {
            var copyFsm = _fsms.Client[index];
            if (copyFsm == null || copyFsm.Fsm == null || index >= _fsms.Host.Count || _fsms.Host[index] == null ||
                IsRunByEachGame(_fsms.Host[index])) {
                continue;
            }

            var fsm = copyFsm.Fsm;
            var stateName = _runHere == copyFsm ? fsm.ActiveStateName : EntityFsmActions.HostStateOf(fsm);
            if (stateName == null || fsm.GetState(stateName) is not { } state) {
                continue;
            }

            foreach (var eventName in WoundEvents) {
                if (EntityFsmActions.FindTransition(fsm, state, eventName) is not { } next) {
                    continue;
                }

                if (!HitCounters.Counts(next)) {
                    reactions.Add(((byte) index, eventName));
                }

                break;
            }
        }

        return reactions;
    }
}
