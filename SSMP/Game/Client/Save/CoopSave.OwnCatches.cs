using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The catches of the creatures that every game runs by itself (see OwnCreatures): a part of the creature's own feels
/// the player of its game and tells the creature that it caught them for a combo of blows (see Entity.CatchEvents), as
/// the mites that hide under the cloth catch with their leap. Such a creature goes after one player, whom the scene
/// host's game decides, and catches only them: they get away or they do not (USER 11-16 "小蜘蛛是主动抓人，只有两种可能，
/// 要么抓到了，要么没抓到", "所以不存在在你的机子上抓你，在我的机子上抓我的可能", "也不是人能主动被抓的，人只有要么
/// 逃开了要么没逃开"). So the part of each game catches that game's player only while the creature goes after them (see
/// <see cref="RefuseCatch"/>).
///
/// Whether it caught them only the game of that player can tell: in another game they are a figure that no part of a
/// creature feels. A leap is one whole move, which that game plays to its end by itself, the combo of blows if it
/// caught them or the landing if not, and it tells the scene host's game that it caught them (USER 11-16 "扑人不管扑没
/// 扑到都是一个完整的动作", "客机应该自己完成连斩，或者扑空", "实际上当然是客机自己回复扑到了"; see
/// <see cref="TakesOwnCatchStep"/>, <see cref="TellOwnCatch"/>). Before, the catch waited there for a step of the
/// scene host's, as a step that another state machine told the creature does, and the scene host's game said whether
/// it caught them by where its own creature was as the word came, a round trip late: the player was held for the blows
/// of a creature that landed and walked off. The scene host's game plays the catch too while its creature is still in
/// the leap, and not once it landed (USER 11-16 "如果延迟大，房主那边已经没扑到了，那就不演扑到了"; see
/// <see cref="TakeOwnCatch"/>), and every other game takes it from there as a step of the scene host's. The game of
/// the player caught keeps to the scene host's steps again once its catch is through (USER 11-16 "至于客机什么时候做
/// 下一招的校准，扑完再说"; see <see cref="SkipsForOwnCatch"/>). In a game whose player the creature does not go after,
/// what it does to the player it caught is kept off that game's player (see <see cref="KeepOut"/>): the large mite
/// pulls the player it caught up to its blows, and played as a step of the scene host's, it would pull the player of
/// every game there.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What an update of a creature that every game runs by itself is when it tells that a part of the creature's own
    /// caught the player of the game that sent it (see <see cref="TellOwnCatch"/>).
    /// </summary>
    private const ushort OwnCatchPart = 4;

    /// <summary>
    /// The state machines of creatures that every game runs by itself that play a catch of another player's, with the
    /// states of the catch and what is kept off this game's player meanwhile (see <see cref="KeepOut"/>).
    /// </summary>
    private readonly Dictionary<Fsm, (HashSet<FsmState> States, List<FsmStateAction> Muted)> _keptOut = new();

    /// <summary>
    /// The state machines of creatures that every game runs by itself that caught this game's player, in a game that
    /// follows the scene host's, until it is known what the scene host's game made of the catch (see
    /// <see cref="SkipsForOwnCatch"/>).
    /// </summary>
    private readonly Dictionary<Fsm, OwnCatchHere> _ownCatchesHere = new();

    /// <summary>
    /// Whether a state tells a creature that its part caught the player, by one of the events of a catch (see
    /// Entity.CatchEvents).
    /// </summary>
    private static bool TellsACatch(FsmState state) {
        foreach (var action in state.Actions) {
            if (action is SendEventByName { sendEvent.Value: { } name } && Entity.Entity.CatchEvents.Contains(name)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a creature goes after this game's player, or after no one in particular, so that what it aims at a
    /// player goes at this game's player, as it does in a game of one.
    /// </summary>
    private static bool GoesAfterLocalPlayer(GameObject creature) {
        return GamePatcher.TargetOf(creature) is not { } target ||
               HeroController.SilentInstance is { } hero && target == hero.gameObject;
    }

    /// <summary>
    /// Lets go of the step of a part of a creature that every game runs by itself into a catch of this game's player,
    /// which the creature does not go after: it catches whom it goes after (USER 11-16 "也不是人能主动被抓的"). The part
    /// stays where it was, as if it felt nothing, until the player comes into it again.
    /// </summary>
    private void RefuseCatch(Fsm part, GameObject creature, FsmState toState) {
        MachineSwitchToStateField?.SetValue(part, null);
        Logger.Info(
            $"[Own creatures] '{part.Name}' of '{OwnCreatures.PlaceOf(creature)}' would go to '{toState.Name}' as it " +
            "touched the local player, whom the creature does not go after, and catches nobody"
        );
    }

    /// <summary>
    /// Whether a state machine of a creature that every game runs by itself goes to a state because a part of the
    /// creature's own caught this game's player and told it so (see Entity.CatchEvents).
    /// </summary>
    private static bool CatchesLocalPlayer(Fsm fsm, FsmState toState, GameObject creature) {
        return fsm.LastTransition is { } transition && transition.ToState == toState.Name &&
               Entity.Entity.CatchEvents.Contains(transition.EventName) && Fsm.EventData?.SentByFsm is { } part &&
               part != fsm && part.GameObject != null && OwnCreatures.Owning(part.GameObject) == creature;
    }

    /// <summary>
    /// Whether a catch takes a state machine to a state (see Entity.CatchEvents).
    /// </summary>
    private static bool IsCatchState(Fsm fsm, FsmState state) {
        return fsm.GlobalTransitions.Concat(fsm.States.SelectMany(each => each.Transitions)).Any(
            transition => transition.ToState == state.Name && Entity.Entity.CatchEvents.Contains(transition.EventName)
        );
    }

    /// <summary>
    /// The way of a catch out of a state into the given state, by a way out of the state or out of every state of its
    /// state machine, or null if none leads there.
    /// </summary>
    private static FsmTransition? CatchWay(FsmState state, string into) {
        return state.Transitions.Concat(state.Fsm.GlobalTransitions).FirstOrDefault(
            transition => transition.ToState == into && Entity.Entity.CatchEvents.Contains(transition.EventName)
        );
    }

    /// <summary>
    /// Lets a state machine of a creature that every game runs by itself go to a state. Out of the states of a catch
    /// that was not of this game's player, what it does to the player it caught is let back in; into one, it is kept
    /// off this game's player (see <see cref="KeepOut"/>).
    /// </summary>
    private void EnterOwnState(Action<Fsm, FsmState> orig, Fsm fsm, FsmState toState) {
        if (_keptOut.TryGetValue(fsm, out var kept) && !kept.States.Contains(toState)) {
            foreach (var action in kept.Muted) {
                action.Enabled = true;
            }

            _keptOut.Remove(fsm);
            Logger.Info(
                $"[Own creatures] '{OwnCreatures.PlaceOf(fsm.GameObject)}' is through the catch in '{fsm.Name}' and " +
                $"goes to '{toState.Name}', with the local player let back in"
            );
        }

        if (!_keptOut.ContainsKey(fsm) && IsCatchState(fsm, toState) && !GoesAfterLocalPlayer(fsm.GameObject)) {
            KeepOut(fsm, toState);
        }

        orig(fsm, toState);
    }

    /// <summary>
    /// Keeps off this game's player what a state machine of a creature that every game runs by itself does to the
    /// player it caught, as it goes into a catch of another player, until it is through the states that only the catch
    /// leads to.
    /// </summary>
    private void KeepOut(Fsm fsm, FsmState toState) {
        var states = Entity.Action.EntityFsmActions.StatesOnlyThrough(fsm, toState);
        var muted = Entity.Action.EntityFsmActions.PlayersOwnActionsOf(fsm).FindAll(
            action => states.Contains(action.State)
        );
        foreach (var action in muted) {
            action.Enabled = false;
        }

        _keptOut[fsm] = (states, muted);
        Logger.Info(
            $"[Own creatures] '{OwnCreatures.PlaceOf(fsm.GameObject)}' goes into '{toState.Name}' of '{fsm.Name}', a " +
            $"catch of another player, with {muted.Count} of what it does to the player caught kept off the local " +
            "player"
        );
    }

    /// <summary>
    /// Whether a state machine of a creature that every game runs by itself, in a game that follows the scene host's,
    /// takes a step as a part of a catch of this game's player: the catch, which it starts here and tells the scene
    /// host's game of, or a step to one of the states that only the catch leads to, which it plays here to their end.
    /// After them it goes on by itself as far as it does in such a game, and its move is through once it comes to a
    /// state that goes by where the players are, whose way on is the scene host's: it keeps to the scene host's steps
    /// again from there (see <see cref="SkipsForOwnCatch"/>). The large mite backs off after its blows (review: cut off
    /// at the first state out of the catch, it was set straight to where the scene host's walked as it began to).
    /// </summary>
    private bool TakesOwnCatchStep(PlayMakerFSM machine, FsmState toState) {
        var fsm = machine.Fsm;
        if (_ownCatchesHere.TryGetValue(fsm, out var catchHere)) {
            if (catchHere.States.Contains(toState)) {
                return true;
            }

            if (!catchHere.Through && GoesByPlayers(toState)) {
                catchHere.Through = true;
                Logger.Info(
                    $"[Own creatures] '{OwnCreatures.PlaceOf(machine.gameObject)}' is through its catch of the " +
                    $"local player in '{fsm.Name}' and the move after it, as it goes to '{toState.Name}'"
                );
            }

            return false;
        }

        if (!CatchesLocalPlayer(fsm, toState, machine.gameObject)) {
            return false;
        }

        _ownCatchesHere[fsm] = new OwnCatchHere(
            toState.Name, Entity.Action.EntityFsmActions.StatesOnlyThrough(fsm, toState)
        );
        TellOwnCatch(machine);
        return true;
    }

    /// <summary>
    /// Tells the scene host's game that a part of a creature that every game runs by itself caught this game's player
    /// in a game that follows it, which plays the catch by itself: the state it caught them from, with the rolls before
    /// it, where it was and how it went, as a step of the scene host's tells (see <see cref="TakeOwnCatch"/>).
    /// </summary>
    private void TellOwnCatch(PlayMakerFSM machine) {
        var creature = machine.gameObject;
        SharedDice.RollByName(creature, OwnCreatures.RollName(creature));
        var word = OwnCreatureUpdate(
            machine,
            new OwnStep(
                machine.Fsm.ActiveStateName, SharedDice.RollCounts(creature), creature.transform.position,
                creature.transform.localScale.x
            )
        );
        word.PartCount = OwnCatchPart;
        word.Part = ThrownThings.ThrowersPlayer;
        Logger.Info(
            $"[Own creatures] '{word.ObjectPath}' caught the local player from '{word.StateName}' of " +
            $"'{word.FsmName}' and plays the catch here by itself, as the scene host's game is told"
        );
        SendToRoomMembers(word);
    }

    /// <summary>
    /// Plays in the scene host's game the catch of the player of another member's game, whose creature caught them
    /// there and plays it by itself (see <see cref="TellOwnCatch"/>), while its creature here is still in the leap: in
    /// a state that the catch can come from into the state that it went to there, taken at the rolls that the catch
    /// came with. Then it is put where theirs caught them, goes after the player it caught (see
    /// GamePatcher.GoAfterGrabbed) and takes the catch, which every other game takes from here, with what it does to
    /// that player kept off this game's (see <see cref="KeepOut"/>). One that landed already, as the word came late,
    /// plays none of it (USER 11-16 "如果延迟大，房主那边已经没扑到了，那就不演扑到了"), and nor does one in a later
    /// leap, which rolled since (review: a word held up a second or two by a stall of the network came in the next
    /// leap, which was turned to that player with the dice set back, and its catch played again where it was over).
    /// </summary>
    private void TakeOwnCatch(ClientPlayerData player, GameObject creature, CoopSaveUpdate update) {
        var place = update.ObjectPath;
        if (IsSceneHost?.Invoke() != true) {
            Logger.Info(
                $"[Own creatures] '{place}' caught {player.Username} in their game, which plays the catch, and so " +
                "does the scene host's game if its creature is still in the leap"
            );
            return;
        }

        var fsm = PlayMakerFSM.FindFsmOnGameObject(creature, update.FsmName).Fsm;
        var into = fsm.GetState(update.StateName).Transitions.Concat(fsm.GlobalTransitions).First(
            transition => Entity.Entity.CatchEvents.Contains(transition.EventName)
        ).ToState;
        if (CatchWay(fsm.ActiveState, into) is not { } way) {
            Logger.Info(
                $"[Own creatures] '{place}' caught {player.Username} from '{update.StateName}' in their game, but is " +
                $"in '{fsm.ActiveStateName}' of '{fsm.Name}' here, through its leap, and plays no catch here"
            );
            return;
        }

        if (!_ownSteps.TryGetValue(fsm, out var steps) || steps.Count == 0 ||
            !steps[^1].Counts.SequenceEqual(update.Amounts)) {
            Logger.Info(
                $"[Own creatures] '{place}' caught {player.Username} from '{update.StateName}' in their game, but is " +
                $"in '{fsm.ActiveStateName}' of '{fsm.Name}' here in a later leap, and plays no catch here"
            );
            return;
        }

        var away = Vector2.Distance(creature.transform.position, new Vector2(update.Values[0], update.Values[1]));
        Logger.Info(
            $"[Own creatures] '{place}' caught {player.Username} from '{update.StateName}' in their game, and goes " +
            $"from '{fsm.ActiveStateName}' to '{into}' of '{fsm.Name}' here too, where it was there, {away:F1} away"
        );
        PutAtSceneHostsStep(fsm, update);
        if (ThrownThings.FigureOf?.Invoke(update.Part, player.Id) is { } figure && figure != null) {
            GamePatcher.GoAfterGrabbed(creature, figure);
        }

        fsm.Event(way.FsmEvent);
    }

    /// <summary>
    /// Whether a step of the scene host's creature is left, in a game that follows it, while this game's creature
    /// plays a catch of this game's player by itself, or after it, until it is known what the scene host's game made of
    /// the catch. A step into the catch is the scene host's taking it, and so are the ones after it, which this game
    /// took already: they are taken as any step. A step into a state that the catch could still come from is of the
    /// leap that the creature here caught from, which it is past. Any other step tells that the scene host's creature
    /// was through its leap as the word came, and caught nobody there: the creature here goes on from the last such
    /// step once it is through its catch and the move after it (see <see cref="UpdateOwnCatches"/>).
    /// </summary>
    private bool SkipsForOwnCatch(ClientPlayerData player, Fsm fsm, CoopSaveUpdate update) {
        if (!_ownCatchesHere.TryGetValue(fsm, out var catchHere)) {
            return false;
        }

        var place = update.ObjectPath;
        var state = fsm.GetState(update.StateName);
        if (catchHere.States.Contains(state)) {
            _ownCatchesHere.Remove(fsm);
            Logger.Info(
                $"[Own creatures] '{place}' went to '{state.Name}' of '{fsm.Name}' in {player.Username}'s game, " +
                "which took the catch of the local player too, and keeps to the scene host's steps from there"
            );
            return false;
        }

        if (CatchWay(state, catchHere.Into) != null) {
            Logger.Info(
                $"[Own creatures] '{place}' went to '{state.Name}' of '{fsm.Name}' in {player.Username}'s game, in " +
                "the leap that caught the local player here, which it is past"
            );
            return true;
        }

        if (catchHere.Through) {
            _ownCatchesHere.Remove(fsm);
            Logger.Info(
                $"[Own creatures] '{place}' went to '{state.Name}' of '{fsm.Name}' in {player.Username}'s game, " +
                "through its leap there without a catch, and goes on from there, as it is through its catch here"
            );
            return false;
        }

        catchHere.Missed = update;
        catchHere.Host = player;
        Logger.Info(
            $"[Own creatures] '{place}' went to '{state.Name}' of '{fsm.Name}' in {player.Username}'s game, through " +
            "its leap there without a catch, and goes on from there once it is through its catch here"
        );
        return true;
    }

    /// <summary>
    /// Has a creature that every game runs by itself, through a catch of this game's player that the scene host's did
    /// not take, go on from the last step of the scene host's (see <see cref="SkipsForOwnCatch"/>): a frame after its
    /// move was through here, outside of the step of its state machine.
    /// </summary>
    private void UpdateOwnCatches() {
        if (_ownCatchesHere.Count == 0) {
            return;
        }

        foreach (var pair in _ownCatchesHere.ToList()) {
            if (pair.Value is not { Through: true, Missed: { } missed, Host: { } host }) {
                continue;
            }

            _ownCatchesHere.Remove(pair.Key);
            Logger.Info(
                $"[Own creatures] '{missed.ObjectPath}' is through its catch of the local player here, and goes on " +
                $"from the step to '{missed.StateName}' of {host.Username}'s game, where it caught nobody"
            );
            OnOwnCreature(host, missed);
        }
    }

    /// <summary>
    /// A catch of this game's player that a creature that every game runs by itself plays here by itself, in a game
    /// that follows the scene host's (see <see cref="TakesOwnCatchStep"/>), and what the scene host's game made of it.
    /// </summary>
    /// <param name="into">The state that the catch went to.</param>
    /// <param name="states">The states that only the catch leads to, that one among them.</param>
    private sealed class OwnCatchHere(string into, HashSet<FsmState> states) {
        /// <summary>
        /// The state that the catch went to.
        /// </summary>
        public readonly string Into = into;

        /// <summary>
        /// The states that only the catch leads to, which the creature plays here by itself.
        /// </summary>
        public readonly HashSet<FsmState> States = states;

        /// <summary>
        /// Whether the creature here is through the catch and the move after it.
        /// </summary>
        public bool Through;

        /// <summary>
        /// The last step of the scene host's creature after its leap, in which it caught nobody, which the creature
        /// here goes on from once it is through.
        /// </summary>
        public CoopSaveUpdate? Missed;

        /// <summary>
        /// The game that the step came from.
        /// </summary>
        public ClientPlayerData? Host;
    }
}
