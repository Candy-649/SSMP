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
/// Whether it caught them is the scene host's game's to say (USER 11-16 "房主说抓了，那就是抓了"). In a game that follows
/// it, the step that the part told the creature waits for the scene host's, as any step that another state machine told
/// it does, and the scene host's game is told where the creature caught them from (see <see cref="TellOwnCatch"/>).
/// Before, nothing told it, and its creature, whose part never feels another player, went its own way: the player
/// caught was held for the blows of a creature that landed and walked off. It caught them if its creature took the same
/// step, the leap that the catch came from, though it may have landed by the time the word comes, and is in no catch
/// now: it is put where theirs caught them, goes after the player it caught and takes the catch (see
/// <see cref="TakeOwnCatch"/>), and every game takes it from there as a step of the scene host's, as it does a catch of
/// the scene host's own player. In a game whose player the creature does not go after, what it does to the player it
/// caught is kept off that game's player (see <see cref="KeepOut"/>): the large mite pulls the player it caught up to
/// its blows, and played as a step of the scene host's, it would pull the player of every game there.
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
    /// Tells the scene host's game that a part of a creature that every game runs by itself caught this game's player,
    /// in a game that follows it, where the creature waits for its step: the state it caught them from, with the rolls
    /// before it, where it was and how it went, as a step of the scene host's tells (see <see cref="TakeOwnCatch"/>).
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
            $"'{word.FsmName}', and the scene host's game is told, which says whether it did"
        );
        SendToRoomMembers(word);
    }

    /// <summary>
    /// Says in the scene host's game whether a creature that every game runs by itself caught the player of another
    /// member's game, whose part caught them there (see <see cref="TellOwnCatch"/>). It did if its creature here is in
    /// the state that the catch came from, or took it at the same rolls a moment ago, and is in no catch now: a leap
    /// that caught a player near its end had landed here by the time the word came, a round trip late, and the player
    /// was held for blows that never came (review). Then it is put where theirs caught them, goes after the player it
    /// caught (see GamePatcher.GoAfterGrabbed) and takes the catch, which every game takes from here, with what it does
    /// to that player kept off this game's (see <see cref="KeepOut"/>). Otherwise it caught nobody.
    /// </summary>
    private void TakeOwnCatch(ClientPlayerData player, GameObject creature, CoopSaveUpdate update) {
        var place = update.ObjectPath;
        if (IsSceneHost?.Invoke() != true) {
            Logger.Info(
                $"[Own creatures] '{place}' caught {player.Username} in their game, which the scene host's game says " +
                "whether it did"
            );
            return;
        }

        var fsm = PlayMakerFSM.FindFsmOnGameObject(creature, update.FsmName).Fsm;
        var from = fsm.GetState(update.StateName);
        var way = from.Transitions.Concat(fsm.GlobalTransitions).First(
            transition => Entity.Entity.CatchEvents.Contains(transition.EventName)
        );
        var into = way.ToFsmState;
        var tookIt = fsm.ActiveState == from || _ownSteps.TryGetValue(fsm, out var steps) &&
            steps.Exists(step => step.State == from.Name && step.Counts.SequenceEqual(update.Amounts));
        if (!tookIt || Entity.Action.EntityFsmActions.StatesOnlyThrough(fsm, into).Contains(fsm.ActiveState)) {
            Logger.Info(
                $"[Own creatures] '{place}' caught {player.Username} from '{from.Name}' in their game, but is in " +
                $"'{fsm.ActiveStateName}' of '{fsm.Name}' here" +
                (tookIt ? ", a catch already" : $", and took no '{from.Name}' at those rolls") + ", and caught nobody"
            );
            return;
        }

        var away = Vector2.Distance(creature.transform.position, new Vector2(update.Values[0], update.Values[1]));
        Logger.Info(
            $"[Own creatures] '{place}' caught {player.Username} from '{from.Name}' in their game, and goes from " +
            $"'{fsm.ActiveStateName}' to '{into.Name}' of '{fsm.Name}' here too, where it was there, {away:F1} away"
        );
        PutAtSceneHostsStep(fsm, update);
        if (ThrownThings.FigureOf?.Invoke(update.Part, player.Id) is { } figure && figure != null) {
            GamePatcher.GoAfterGrabbed(creature, figure);
        }

        if (fsm.ActiveState == from) {
            fsm.Event(way.FsmEvent);
        } else {
            fsm.SetState(into.Name);
        }
    }
}
