using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
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
/// the copy plays it at once, as a strike is played (PlayHere), with the body that the room's creature has, which the
/// walls of the room stop, and the creature in the scene host's game the same way where it is, answering it at the
/// player who struck it whomever it fights (GamePatcher.AnswerWound). A blow that the creature is immune to, that
/// extracts from it, or that leaves it no health sets off nothing here (CoopHits): the game tells it nothing more of
/// the first two, and the scene host's game tells it of the last along with its death. Nor does a wound that takes
/// the creature into a choice of what it does next: the scene host's game chooses that (see
/// <see cref="ChoosesOnAWound"/>).
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
    internal List<(byte FsmIndex, string EventName, bool Chooses)> FindWoundReactions() {
        var reactions = new List<(byte, string, bool)>();
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
                    reactions.Add(((byte) index, eventName, ChoosesOnAWound(next)));
                }

                break;
            }
        }

        return reactions;
    }

    /// <summary>
    /// Whether a wound that takes an FSM of the creature to a state has it choose what it does next: that state, or
    /// the first past those that only pass it on, does nothing but look about, decide and wait, and goes on one of
    /// several ways - a boss at rest that a blow sends to pick its next move at once, a check of its health for its
    /// next phase, the choice of how to answer the blow. About a thousand of the some 7500 ways that the game's FSMs go
    /// on a wound are such (FsmScan --events, 11-12). What a creature does next is the scene host's game's to choose,
    /// with its own dice, whoever struck it: the copy is not to choose it. One that did, with this game's dice and
    /// where it saw the local player, mostly started another move than the scene host's creature had chosen already,
    /// which then refused it, and jumped from its own to that one (13 of 16 late blows on a boss at rest, 11-05 to
    /// 11-11; USER 11-12 "客机就不应该挑招啊", "挑招也不能让客机来啊", "拒绝了就好了呀").
    /// </summary>
    /// <param name="state">The state that the wound takes it to.</param>
    private static bool ChoosesOnAWound(FsmState state) {
        var passed = new HashSet<FsmState>();
        for (FsmState? at = state; at != null && passed.Add(at);) {
            if (!OnlyDecides(at)) {
                return false;
            }

            FsmState? only = null;
            var ways = 0;
            foreach (var transition in at.Transitions) {
                if (transition.ToFsmState is { } to) {
                    only = to;
                    ways++;
                }
            }

            if (ways != 1) {
                return ways > 1;
            }

            at = only;
        }

        return false;
    }

    /// <summary>
    /// Whether every action of a state that is on does nothing but look about, decide or wait: by its name (see
    /// <see cref="DecidesName"/>), or an event that it tells its own FSM.
    /// </summary>
    /// <param name="state">The state.</param>
    private static bool OnlyDecides(FsmState state) {
        foreach (var action in state.Actions) {
            if (!action.Enabled) {
                continue;
            }

            var decides = action switch {
                SendEvent send => send.eventTarget.target == FsmEventTarget.EventTarget.Self,
                SendEventByName send => send.eventTarget.target == FsmEventTarget.EventTarget.Self,
                _ => DecidesName.IsMatch(action.GetType().Name)
            };
            if (!decides) {
                return false;
            }
        }

        return true;
    }
}
