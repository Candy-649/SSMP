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
/// creature feels. A leap is one whole move, which that game plays to its end by itself, the combo of blows and the
/// backing off after it if it caught them, the landing if not, and then goes on from the latest step of the scene
/// host's (USER 11-16 "扑人不管扑没扑到都是一个完整的动作", "客机应该自己完成连斩，或者扑空", "求你了，让螃蟹自己扑好吗",
/// "扑完之后跟最新的动作"; see <see cref="PlaysOwnLeap"/>, <see cref="UpdateOwnLeaps"/>). It tells the scene host's game
/// that it caught them (USER 11-16 "实际上当然是客机自己回复扑到了"; see <see cref="TellOwnCatch"/>), which plays the
/// catch too while its creature is still in that leap, and not once it landed (USER 11-16 "如果延迟大，房主那边已经没扑
/// 到了，那就不演扑到了"; see <see cref="TakeOwnCatch"/>): every other game takes it from there as a step of the scene
/// host's. Before, the catch waited there for a step of the scene host's, as a step that another state machine told
/// the creature does, and the scene host's game said whether it caught them by where its own creature was as the word
/// came, a round trip late: the player was held for the blows of a creature that landed and walked off. In a game
/// whose player the creature does not go after, what it does to the player it caught is kept off that game's player
/// (see <see cref="KeepOut"/>): the large mite pulls the player it caught up to its blows, and played as a step of the
/// scene host's, it would pull the player of every game there.
///
/// A catch that something of the room tells the creature of, in whatever state it is, as the strands of the boss at
/// the end of the second act tell it, is played so too by the game of the player caught, through the whole move of
/// the catch, and the scene host's game plays it as well, but over a catch that it plays already: whenever a
/// player is caught, the catch is played, and only their own game hurts them (USER 11-09 "只要有玩家被抓了就演抓人，至于
/// 扣不扣血看本机先前的判定"; see <see cref="StartsOwnRoomCatch"/>).
///
/// A grab (see <see cref="GrabEvents"/>), which a part of the creature, or a state machine of it that each game runs by
/// itself, tells it of as it feels the player of its game, goes on by what befalls the player held, like a hurt that
/// lets them drop, which only their own game has: their game leads it, the scene host's game takes its every step
/// from there, and every other game takes those of the scene host's (see CoopSave.OwnLedMoves; USER 11-25 "套现有规
/// 则"). The creature grabs only the player it goes after, as any catch of its own.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What an update of a creature that every game runs by itself is when it tells that a part of the creature's own
    /// caught the player of the game that sent it (see <see cref="TellOwnCatch"/>).
    /// </summary>
    private const ushort OwnCatchPart = 4;

    /// <summary>
    /// The events by which a part of a creature tells it that it grabbed the player, whom the creature then holds in a
    /// move that goes on by what befalls them, which the game of that player leads (see CoopSave.OwnLedMoves).
    /// </summary>
    private static readonly HashSet<string> GrabEvents = ["GRAB HERO"];

    /// <summary>
    /// How many grabs of this game's player this game led, which numbers each (see OwnLeap.Move).
    /// </summary>
    private ulong _grabsLed;

    /// <summary>
    /// The state machines of creatures that every game runs by itself that play a catch of another player's, with the
    /// states of the catch, what is kept off this game's player meanwhile and the state that the catch came from (see
    /// <see cref="KeepOut"/>).
    /// </summary>
    private readonly Dictionary<Fsm, (HashSet<FsmState> States, List<FsmStateAction> Muted, FsmState From)> _keptOut =
        new();

    /// <summary>
    /// The other state machines of creatures that every game runs by itself that a catch of another player tells to do
    /// something, with the states it tells them into and what is kept off this game's player in those (see
    /// <see cref="KeepOutTold"/>).
    /// </summary>
    private readonly Dictionary<Fsm, (HashSet<FsmState> States, List<FsmStateAction> Muted)> _keptOutTold = new();

    /// <summary>
    /// The state machines of creatures that every game runs by itself, each with the first state of a catch of each of
    /// its states that are of the move of a catch (see <see cref="CatchStateOf"/>).
    /// </summary>
    private readonly Dictionary<Fsm, Dictionary<FsmState, FsmState>> _catchStates = new();

    /// <summary>
    /// The state machines of creatures that every game runs by itself that leap at this game's player, in a game that
    /// follows the scene host's, which plays the leap here by itself until it is through (see
    /// <see cref="PlaysOwnLeap"/>).
    /// </summary>
    private readonly Dictionary<Fsm, OwnLeap> _ownLeaps = new();

    /// <summary>
    /// The latest step of the scene host's of each part of a creature that every game runs by itself that came while
    /// the creature played a move here by itself, which the part goes on from once the creature is through (see
    /// <see cref="TakePartStepsAfter"/>).
    /// </summary>
    private readonly Dictionary<Fsm, (ClientPlayerData Host, CoopSaveUpdate Step)> _partStepsInLeap = new();

    /// <summary>
    /// The catches of other players that came to the scene host's game before its creature got to the state that the
    /// catch came from there, each to be played as the creature gets there (see <see cref="TakeOwnCatch"/>,
    /// <see cref="TakeEarlyCatch"/>).
    /// </summary>
    private readonly Dictionary<Fsm, (ClientPlayerData Player, CoopSaveUpdate Word)> _earlyCatches = new();

    /// <summary>
    /// Whether an event is one by which a part of a creature tells it that it caught the player: for a combo of blows
    /// (see Entity.CatchEvents), or a grab (see <see cref="GrabEvents"/>).
    /// </summary>
    private static bool IsCatchEvent(string name) {
        return Entity.Entity.CatchEvents.Contains(name) || GrabEvents.Contains(name);
    }

    /// <summary>
    /// Whether a state tells a creature that its part caught the player, by one of the events of a catch (see
    /// <see cref="IsCatchEvent"/>).
    /// </summary>
    private static bool TellsACatch(FsmState state) {
        foreach (var action in state.Actions) {
            if (action is SendEventByName { sendEvent.Value: { } name } && IsCatchEvent(name)) {
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
    /// creature's own caught this game's player and told it so (see Entity.CatchEvents), or a part of the room did, as
    /// the strands of the boss at the end of the second act tell it: they catch the player of their own game only (see
    /// Entity.SetsOffRoomPartCatch).
    /// </summary>
    private static bool CatchesLocalPlayer(Fsm fsm, FsmState toState, GameObject creature) {
        return fsm.LastTransition is { } transition && transition.ToState == toState.Name &&
               IsCatchEvent(transition.EventName) && Fsm.EventData?.SentByFsm is { } part &&
               part != fsm && part.GameObject is { } catcher && catcher != null &&
               (OwnCreatures.Owning(catcher) is { } owner
                   ? owner == creature
                   : CoopHits.IsRoomObject(catcher.transform));
    }

    /// <summary>
    /// Whether a catch takes a state machine to a state (see Entity.CatchEvents).
    /// </summary>
    private static bool IsCatchState(Fsm fsm, FsmState state) {
        return fsm.GlobalTransitions.Concat(fsm.States.SelectMany(each => each.Transitions)).Any(
            transition => transition.ToState == state.Name && IsCatchEvent(transition.EventName)
        );
    }

    /// <summary>
    /// Whether a grab takes a state machine to a state (see <see cref="GrabEvents"/>).
    /// </summary>
    private static bool IsGrabState(Fsm fsm, FsmState state) {
        return fsm.GlobalTransitions.Concat(fsm.States.SelectMany(each => each.Transitions)).Any(
            transition => transition.ToState == state.Name && GrabEvents.Contains(transition.EventName)
        );
    }

    /// <summary>
    /// The state that a catch takes a state machine to (see <see cref="IsCatchState"/>), if a state is of the move of
    /// the catch from there (see Entity.Action.EntityFsmActions.MoveFrom), or null.
    /// </summary>
    private FsmState? CatchStateOf(Fsm fsm, FsmState state) {
        if (!_catchStates.TryGetValue(fsm, out var firsts)) {
            firsts = new Dictionary<FsmState, FsmState>();
            foreach (var each in fsm.States) {
                if (!IsCatchState(fsm, each)) {
                    continue;
                }

                foreach (var inCatch in Entity.Action.EntityFsmActions.MoveFrom(fsm, each)) {
                    firsts.TryAdd(inCatch, each);
                }
            }

            _catchStates[fsm] = firsts;
        }

        return firsts.TryGetValue(state, out var first) ? first : null;
    }

    /// <summary>
    /// Whether a state is one of a leap that catches: one that a catch can come from (see Entity.CatchEvents), and that
    /// does something. A state of nothing but ways out is one where a state machine waits to hear of a catch, like the
    /// one by which the knight holds the player for its blows, which waits so all the time (review: one that came to it
    /// as a game that follows the scene host's took the room played every step of its own there ever after, and told
    /// the scene host's game of each catch of its own too).
    /// </summary>
    private static bool IsLeapState(FsmState state) {
        return state.Actions.Length > 0 &&
               state.Transitions.Any(transition => Entity.Entity.CatchEvents.Contains(transition.EventName));
    }

    /// <summary>
    /// The way of a catch out of a state into the given state, by a way out of the state or out of every state of its
    /// state machine, or null if none leads there.
    /// </summary>
    private static FsmTransition? CatchWay(FsmState state, string into) {
        return state.Transitions.Concat(state.Fsm.GlobalTransitions).FirstOrDefault(
            transition => transition.ToState == into && IsCatchEvent(transition.EventName)
        );
    }

    /// <summary>
    /// Lets a state machine of a creature that every game runs by itself go to a state. Out of the states of a catch
    /// that was not of this game's player, what it does to the player it caught is let back in, but on a way that every
    /// state has, into what it does as it lets go of them (see <see cref="KeepOutOfTheAftermath"/>), and so it is as
    /// something tells it that it caught this game's player meanwhile, whose catch it is then (review: the strands of
    /// the boss at the end of the second act caught the player of a game that played a catch of another player, who
    /// was neither held nor hurt by their own catch). Into a state of a catch of another player from outside it, its
    /// first or one after it, as a member who comes in while it plays the catch is put there, it is kept off this
    /// game's player (see <see cref="KeepOut"/>; review: put into the slash of that boss, they were cut by it).
    /// </summary>
    private void EnterOwnState(Action<Fsm, FsmState> orig, Fsm fsm, FsmState toState) {
        if (_keptOutTold.TryGetValue(fsm, out var told) &&
            (!told.States.Contains(toState) || GoesAfterLocalPlayer(fsm.GameObject))) {
            foreach (var action in told.Muted) {
                action.Enabled = true;
            }

            _keptOutTold.Remove(fsm);
            Logger.Info(
                $"[Own creatures] '{OwnCreatures.PlaceOf(fsm.GameObject)}' goes to '{toState.Name}' of '{fsm.Name}', " +
                "out of what a catch of another player told it, with the local player let back in"
            );
        }

        if (_keptOut.TryGetValue(fsm, out var kept) &&
            (CatchesLocalPlayer(fsm, toState, fsm.GameObject) ||
             !kept.States.Contains(toState) && !KeepOutOfTheAftermath(fsm, toState, kept))) {
            foreach (var action in kept.Muted) {
                action.Enabled = true;
            }

            _keptOut.Remove(fsm);
            Logger.Info(
                $"[Own creatures] '{OwnCreatures.PlaceOf(fsm.GameObject)}' is through the catch in '{fsm.Name}' and " +
                $"goes to '{toState.Name}', with the local player let back in"
            );
        }

        if (!_keptOut.ContainsKey(fsm) && CatchStateOf(fsm, toState) is { } catchState &&
            CatchStateOf(fsm, fsm.ActiveState) != catchState && !GoesAfterLocalPlayer(fsm.GameObject)) {
            KeepOut(fsm, catchState);
            FollowGrab(fsm, catchState);
        }

        EnterQuietOnTheWay(orig, fsm, toState);
    }

    /// <summary>
    /// Keeps off this game's player what a state machine of a creature that every game runs by itself does to the
    /// player it caught, as it goes into a catch of another player, of which the given state is the first, until it is
    /// through the move of the catch (see Entity.Action.EntityFsmActions.MoveFrom): the knight, as it held another
    /// player for its blows, pulled this game's player up to it too. So is what it does to the player whose silk it
    /// looks at, in the move from the look (see CoopSave.OwnSilkLooks).
    /// </summary>
    private void KeepOut(Fsm fsm, FsmState toState) {
        var states = Entity.Action.EntityFsmActions.MoveFrom(fsm, toState);
        var muted = Entity.Action.EntityFsmActions.PlayersOwnActionsOf(fsm).FindAll(
            action => states.Contains(action.State)
        );
        foreach (var action in muted) {
            action.Enabled = false;
        }

        _keptOut[fsm] = (states, muted, fsm.ActiveState);
        Logger.Info(
            $"[Own creatures] '{OwnCreatures.PlaceOf(fsm.GameObject)}' goes into '{toState.Name}' of '{fsm.Name}', a " +
            $"catch of another player, with {muted.Count} of what it does to the player caught kept off the local " +
            "player"
        );

        // What it tells its other state machines in the catch, which they go into here too, as it tells them here or
        // as the scene host's step of theirs comes
        foreach (var state in states) {
            foreach (var action in state.Actions) {
                if (!action.Enabled && !muted.Contains(action)) {
                    continue;
                }

                foreach (var (other, into) in Entity.Action.EntityFsmActions.StatesToldBy(action)) {
                    KeepOutTold(other, into);
                }
            }
        }
    }

    /// <summary>
    /// Has a state machine of a creature that every game runs by itself, in a game that follows the scene host's,
    /// follow a grab of another player's as it goes into it, with what it does to the player held kept off this
    /// game's (see <see cref="KeepOut"/>): the game of the player that it goes after leads it (see
    /// CoopSave.OwnLedMoves), and a hurt of this game's player would let that player drop here. The scene host's game
    /// follows a grab of another player's as it plays it, by the number that their game gave it (see
    /// <see cref="PlayOthersCatch"/>), and plays one that their game told it of with no number as a catch that it does
    /// not lead by itself (review of 4aae450: it followed it by no number, whose steps never came).
    /// </summary>
    private void FollowGrab(Fsm fsm, FsmState grabState) {
        if (IsSceneHost?.Invoke() == true || _ledMoves.ContainsKey(fsm) || !IsGrabState(fsm, grabState) ||
            ThrownThings.CodeOf?.Invoke(GamePatcher.TargetOf(fsm.GameObject)) is not { } leader ||
            leader is ThrownThings.NoPlayer or ThrownThings.ThrowersPlayer) {
            return;
        }

        FollowLedMove(fsm, Entity.Action.EntityFsmActions.MoveFrom(fsm, grabState), null, leader, 0);
        Logger.Info(
            $"[Own creatures] '{OwnCreatures.PlaceOf(fsm.GameObject)}' goes into '{grabState.Name}' of '{fsm.Name}', " +
            $"a grab of the player {leader}, which goes on as their game leads it"
        );
    }

    /// <summary>
    /// Keeps off this game's player what a catch of another player tells another state machine of the creature to do
    /// to the player caught, in the states of the move that it tells it into, until it goes out of them: the knight's
    /// that puts the player it let go of back inside its arena, at where it has the player it goes after, put
    /// Luminary where it had let the user go, 12 units off, as the scene host's step of it came (her log of 11-19,
    /// 20:39:19.792; USER "还有我朋友有一次角色瞬移了"). Not into a catch of that state machine's own, which keeps itself
    /// (see <see cref="KeepOut"/>). Every move that the catch tells it into is kept so, as one: the handmaiden's spin
    /// tells its suck to start and to stop, and only the first of these that came up was kept, which was now the one
    /// and now the other (see CoopSave.OwnSilkLooks).
    /// </summary>
    private void KeepOutTold(Fsm fsm, FsmState into) {
        if (_keptOut.ContainsKey(fsm) || CatchStateOf(fsm, into) != null ||
            _keptOutTold.TryGetValue(fsm, out var kept) && kept.States.Contains(into)) {
            return;
        }

        var states = Entity.Action.EntityFsmActions.MoveFrom(fsm, into);
        var muted = Entity.Action.EntityFsmActions.PlayersOwnActionsOf(fsm).FindAll(
            action => states.Contains(action.State)
        );
        if (muted.Count == 0) {
            return;
        }

        foreach (var action in muted) {
            action.Enabled = false;
        }

        if (_keptOutTold.TryGetValue(fsm, out var told)) {
            told.States.UnionWith(states);
            told.Muted.AddRange(muted);
        } else {
            _keptOutTold[fsm] = (states, muted);
        }

        Logger.Info(
            $"[Own creatures] '{OwnCreatures.PlaceOf(fsm.GameObject)}' may be told into '{into.Name}' of " +
            $"'{fsm.Name}' by a catch of another player, with {muted.Count} of what it does there kept off the local " +
            "player"
        );
    }

    /// <summary>
    /// Keeps off this game's player what a state machine of a creature that every game runs by itself does to the
    /// player it caught on its way out of a catch of another player by a way that every state has, in the states that
    /// only that way leads to, until it is through them: a stun or the death of the creature as it holds a player lets
    /// go of them, and here that player is another: the boss at the end of the second act, stunned as it holds a
    /// player, gives them back control, their drawing and their gravity, which would take this game's player out of a
    /// dash. Not if the way leads back to where the creature was before the catch, which is its own life again, as for
    /// the copies of the room (see Entity.KeepOffForTheAftermath). Whether it does.
    /// </summary>
    private bool KeepOutOfTheAftermath(
        Fsm fsm,
        FsmState toState,
        (HashSet<FsmState> States, List<FsmStateAction> Muted, FsmState From) kept
    ) {
        if (fsm.LastTransition is not { } way || way.ToState != toState.Name ||
            Array.IndexOf(fsm.GlobalTransitions, way) < 0) {
            return false;
        }

        var after = Entity.Action.EntityFsmActions.StatesOnlyThrough(fsm, toState);
        if (after.Contains(kept.From)) {
            return false;
        }

        var muted = Entity.Action.EntityFsmActions.PlayersOwnActionsOf(fsm).FindAll(
            action => after.Contains(action.State)
        );
        foreach (var action in muted) {
            action.Enabled = false;
        }

        kept.States.UnionWith(after);
        kept.Muted.AddRange(muted);
        Logger.Info(
            $"[Own creatures] '{OwnCreatures.PlaceOf(fsm.GameObject)}' goes out of the catch in '{fsm.Name}' by " +
            $"'{way.EventName}' to '{toState.Name}', with {muted.Count} more of what it does to the player caught " +
            "kept off the local player"
        );
        return true;
    }

    /// <summary>
    /// Whether a state machine of a creature that every game runs by itself, in a game that follows the scene host's,
    /// takes a step by itself as a part of a leap at this game's player, which this game plays to its end: from the
    /// first state that a catch can come from, through the catch, the combo and what the creature does after it by
    /// itself, or through the landing, until it comes to a state that goes by where the players are, whose way on is
    /// the scene host's. The steps of the scene host's that come meanwhile wait, and it goes on from the latest of
    /// them once it is through (see <see cref="UpdateOwnLeaps"/>). A catch of this game's player is told to the scene
    /// host's game by the state machine that leapt (see <see cref="TellOwnCatch"/>), and the steps that the others of
    /// the creature are told meanwhile are this game's too (see <see cref="ToldInOwnLeap"/>). A talk is played so too,
    /// to its end (see CoopSave.OwnTalks).
    /// </summary>
    private bool PlaysOwnLeap(PlayMakerFSM machine, FsmState toState) {
        var fsm = machine.Fsm;
        var creature = machine.gameObject;
        // A catch that the leap plays, not one after it, which waits for the scene host's step here, nor one within a
        // move that this game leads, whose every step is told (review of 4aae450: the scene host's game played a grab
        // that this one did not, and waited for good for steps of it)
        _ownLeaps.TryGetValue(fsm, out var leap);
        if (leap is { Talk: false, Through: false, Led: false } && CatchesLocalPlayer(fsm, toState, creature)) {
            TellOwnCatch(machine);
        }

        // A talk that the local player starts before the fight is this game's to play (see CoopSave.OwnTalks)
        if (BeforeFight(fsm) && leap is not { Through: false } && OwnCreatures.LocalTalk == fsm) {
            StartsLocalTalk(machine, toState);
            return true;
        }

        if (leap == null) {
            return StartsOwnExtract(machine, toState) || StartsOwnRoomCatch(machine, toState) ||
                   StartsOwnStrikeBack(machine, toState) || StartsOwnLeap(machine, toState) ||
                   ToldInOwnLeap(fsm, creature);
        }

        if (leap.Through) {
            return false;
        }

        // A talk goes on to its end, a catch that no leap led to through its move, and a leap until a state that goes
        // by where the players are. Every step of a move that this game leads is told, and so is the one out of it,
        // which is this game's too
        if (leap.Led) {
            TellLedStep(machine, toState, leap.Move, ++leap.Told);
        }

        if (leap.Talk ? !Entity.Entity.EndsTalk(fsm, toState)
            : leap.Catch?.Contains(toState) ?? !GoesByPlayers(toState)) {
            return true;
        }

        leap.Through = true;
        Logger.Info(
            $"[Own creatures] '{leap.Place}' is through its {leap.What} in '{fsm.Name}', as it goes to " +
            $"'{toState.Name}'"
        );
        return leap.Led;
    }

    /// <summary>
    /// Whether a state machine of a creature that every game runs by itself, in a game that follows the scene host's,
    /// takes a step that another one of the creature or a part of it told it while the creature leaps at this game's
    /// player here, which is this game's to play as the leap is: the catch of this game's player and what it sets off,
    /// like the knight's hold of the player for its blows and the bounds of its arena that it keeps them in after it,
    /// which waited for a step of the scene host's that never came (review). So does a state machine of a part of it
    /// that takes its steps as the creature does, like the blades that the boss at the end of the second act sends
    /// away as it slashes the player it caught.
    /// </summary>
    private bool ToldInOwnLeap(Fsm fsm, GameObject creature) {
        var owner = OwnCreatures.Owning(creature);
        if (owner == null || Fsm.EventData?.SentByFsm is not { } sender || sender == fsm || sender.GameObject == null ||
            OwnCreatures.Owning(sender.GameObject) != owner) {
            return false;
        }

        foreach (var pair in _ownLeaps) {
            if (pair.Value is { Talk: false, Through: false } && pair.Key.GameObject == owner) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a creature that every game runs by itself plays a leap, a strike back, a catch or a talk here by itself
    /// now, in a game that follows the scene host's (see <see cref="PlaysOwnLeap"/>).
    /// </summary>
    private bool InOwnLeap(GameObject creature) {
        foreach (var pair in _ownLeaps) {
            if (!pair.Value.Through && pair.Key.GameObject == creature) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a state machine of a creature that every game runs by itself plays a catch of another player in a state,
    /// with what it does to the player caught kept off this game's player (see <see cref="KeepOut"/>).
    /// </summary>
    private bool KeepsLocalPlayerOut(Fsm fsm, FsmState state) {
        return _keptOut.TryGetValue(fsm, out var kept) && kept.States.Contains(state);
    }

    /// <summary>
    /// Has a creature that every game runs by itself, in a game that follows the scene host's, play by itself a catch
    /// of this game's player that no leap of its own led to: one that something of the room tells it of, in whatever
    /// state it is, as the strands of the boss at the end of the second act tell it that they caught the player. They
    /// catch the player of their own game only (see Entity.SetsOffRoomPartCatch), so the catch is that player's: the
    /// creature goes after them, plays the catch here to its end, through its whole move, from the hold to the let go
    /// (see Entity.Action.EntityFsmActions.MoveFrom), and the scene host's game is told, which plays it too (see
    /// <see cref="TakeOwnCatch"/>). Before, it waited for a step of the scene host's that never came, as a step that
    /// another state machine told it does, and the knight held the player it caught for as long, as its catch was over
    /// at the hold, which its other catch starts at (review of the user's log). Whether it does.
    /// </summary>
    private bool StartsOwnRoomCatch(PlayMakerFSM machine, FsmState toState) {
        var creature = machine.gameObject;
        if (!CatchesLocalPlayer(machine.Fsm, toState, creature) || HeroController.SilentInstance is not { } hero) {
            return false;
        }

        GamePatcher.GoAfterGrabbed(creature, hero.gameObject, machine.Fsm, toState);
        var grab = GrabEvents.Contains(machine.Fsm.LastTransition!.EventName);
        var what = grab ? "grab of the local player" : "catch of the local player";
        var leap = new OwnLeap(OwnCreatures.PlaceOf(creature), false, what) {
            Catch = Entity.Action.EntityFsmActions.MoveFrom(machine.Fsm, toState),
            Led = grab,
            Move = grab ? ++_grabsLed : 0
        };
        _ownLeaps[machine.Fsm] = leap;
        Logger.Info(
            $"[Own creatures] '{leap.Place}' caught the local player into '{toState.Name}' of '{machine.FsmName}', " +
            "which it plays here by itself to its end" + (grab ? $", leading it as move {leap.Move}" : "")
        );
        TellOwnCatch(machine, leap.Move);
        return true;
    }

    /// <summary>
    /// Has a state machine of a creature that every game runs by itself, in a game that follows the scene host's,
    /// play a leap at this game's player here by itself, as it goes to the first state of it, by itself or as a step
    /// of the scene host's (see <see cref="PlaysOwnLeap"/>). Whether it does.
    /// </summary>
    private bool StartsOwnLeap(PlayMakerFSM machine, FsmState toState) {
        if (!IsLeapState(toState) || !GoesAfterLocalPlayer(machine.gameObject) || _ownLeaps.ContainsKey(machine.Fsm)) {
            return false;
        }

        var leap = new OwnLeap(OwnCreatures.PlaceOf(machine.gameObject), false);
        _ownLeaps[machine.Fsm] = leap;
        Logger.Info(
            $"[Own creatures] '{leap.Place}' leaps at the local player in '{toState.Name}' of '{machine.FsmName}', " +
            "which it plays here by itself to its end"
        );
        return true;
    }

    /// <summary>
    /// Keeps a step of the scene host's creature, in a game that follows it, while its creature here plays a leap at
    /// this game's player by itself (see <see cref="PlaysOwnLeap"/>), to go on from the latest once it is through.
    /// Whether it does.
    /// </summary>
    private bool KeepsForOwnLeap(ClientPlayerData player, Fsm fsm, CoopSaveUpdate update) {
        if (!_ownLeaps.TryGetValue(fsm, out var leap)) {
            return false;
        }

        // The steps of the scene host's of the rest of a move after a hurt here go, whatever its creature did in it,
        // and the first one out of it ends that move here: the next move, or what cut the move short there (see
        // CoopSave.OwnHits; USER 11-22 "然后消失，等主机下一招")
        if (leap.Hit) {
            if (leap.Catch!.Contains(fsm.GetState(update.StateName))) {
                Logger.Info(
                    $"[Own creatures] '{leap.Place}' went to '{update.StateName}' of '{fsm.Name}' in " +
                    $"{player.Username}'s game, of the move after the hurt, which it plays here by itself"
                );
                return true;
            }

            _ownLeaps.Remove(fsm);
            Logger.Info(
                $"[Own creatures] '{leap.Place}' went to '{update.StateName}' of '{fsm.Name}' in " +
                $"{player.Username}'s game, out of the move after the hurt, and goes on from there here"
            );
            TakePartStepsAfter(fsm.GameObject);
            return false;
        }

        // A talk that is through here waits for the scene host's step out of it, as every one before that is let go
        // of (see CoopSave.OwnTalks)
        if (leap is { Talk: true, Through: true }) {
            EndsOwnTalkWait(player, fsm, leap, update);
            return false;
        }

        leap.Latest = update;
        leap.Host = player;
        Logger.Info(
            $"[Own creatures] '{leap.Place}' went to '{update.StateName}' of '{fsm.Name}' in {player.Username}'s " +
            $"game, which waits until its {leap.What} is through here"
        );
        return true;
    }

    /// <summary>
    /// Has a creature that every game runs by itself go on from the latest step of the scene host's that came while it
    /// played a leap at this game's player by itself, a frame after it is through here, outside of the step of its
    /// state machine (see <see cref="PlaysOwnLeap"/>). That step is taken as any step (USER 11-16 "扑完之后跟最新的动
    /// 作"): one that this game took already, at the same rolls, like the scene host's own landing, leaves it where it
    /// is, and it is put where the scene host's is at its next move (see OnOwnCreature). The rest of the steps of the
    /// scene host's come after that one.
    /// </summary>
    private void UpdateOwnLeaps() {
        if (_ownLeaps.Count == 0) {
            return;
        }

        foreach (var pair in _ownLeaps.ToList()) {
            // A talk that is through waits for the scene host's step out of it, if none came while it went on (see
            // CoopSave.OwnTalks), and the rest of a move after a hurt waits so always (see KeepsForOwnLeap)
            var leap = pair.Value;
            if (!leap.Through || leap.Hit || leap is { Talk: true, Latest: null }) {
                continue;
            }

            _ownLeaps.Remove(pair.Key);
            if (leap is not { Latest: { } latest, Host: { } host }) {
                Logger.Info(
                    $"[Own creatures] '{leap.Place}' is through its {leap.What} here, and no step of the scene " +
                    "host's came meanwhile"
                );
            } else {
                Logger.Info(
                    $"[Own creatures] '{leap.Place}' is through its {leap.What} here, and goes on from the latest " +
                    $"step of {host.Username}'s game, to '{latest.StateName}'"
                );
                OnOwnCreature(host, latest);
            }

            TakePartStepsAfter(pair.Key.GameObject);
        }
    }

    /// <summary>
    /// Has the parts of a creature that every game runs by itself go on from the latest steps of the scene host's that
    /// came while it played a move here by itself, once it is through every one of them: they went on as the creature
    /// told them here meanwhile (review: one that the move told something, like the blades that the boss at the end of
    /// the second act sends away as it slashes, waited after it for the scene host's next word, at its next move).
    /// </summary>
    private void TakePartStepsAfter(GameObject leapt) {
        if (_partStepsInLeap.Count == 0 || OwnCreatures.Owning(leapt) is not { } creature || InOwnLeap(creature)) {
            return;
        }

        foreach (var pair in _partStepsInLeap.ToList()) {
            if (OwnCreatures.Owning(pair.Key.GameObject) != creature) {
                continue;
            }

            _partStepsInLeap.Remove(pair.Key);
            Logger.Info(
                $"[Own creatures] '{OwnCreatures.PlaceOf(pair.Key.GameObject)}' goes on from the latest step of " +
                $"{pair.Value.Host.Username}'s game, to '{pair.Value.Step.StateName}' of '{pair.Key.Name}', as its " +
                "creature is through its move here"
            );
            OnOwnCreature(pair.Value.Host, pair.Value.Step);
        }
    }

    /// <summary>
    /// Tells the scene host's game that a part of a creature that every game runs by itself caught this game's player
    /// in a game that follows it, which plays the catch by itself: the state it caught them from, with the rolls before
    /// it, where it was and how it went, as a step of the scene host's tells (see <see cref="TakeOwnCatch"/>), and the
    /// number of a grab, which this game leads (see CoopSave.OwnLedMoves), or 0.
    /// </summary>
    private void TellOwnCatch(PlayMakerFSM machine, ulong move = 0) {
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
        word.Sequence = move;
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
    /// came with (see <see cref="PlayOthersCatch"/>). One that landed already, as the word came late, plays none of it
    /// (USER 11-16 "如果延迟大，房主那边已经没扑到了，那就不演扑到了"), and nor does one in a later leap, which rolled
    /// since (review: a word held up a second or two by a stall of the network came in the next leap, which was turned
    /// to that player with the dice set back, and its catch played again where it was over).
    ///
    /// Their game may be ahead of this one, by a step that it took by itself, like the knight's strike back at a blow
    /// of their player that it blocked, which this game hears of a moment later: the catch at the start of its flurry
    /// came while the creature here was still in the dash before it. One that is still on its way into the state that
    /// the catch came from, and was not in it at those rolls yet, plays the catch as it gets there, so that the blows
    /// that only a catch leads to show here too (see <see cref="TakeEarlyCatch"/>; USER 11-17 "只要在不一样之前房主收到
    /// 消息客机抓到人了就可以演最后的横扫"). The rolls are the ones of the state that the catch came from, which the states
    /// on the way there roll on to (review: a word held for the same rolls as the step before the catch was never held,
    /// as the strike back rolls its sounds on the way).
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
        var into = CatchInto(fsm, update);
        var way = CatchWay(fsm.ActiveState, into);
        if (way == null && !WasAt(fsm, update) &&
            GoesOnInto(fsm.ActiveState, state => state.Name == update.StateName)) {
            _earlyCatches[fsm] = (player, update);
            Logger.Info(
                $"[Own creatures] '{place}' caught {player.Username} from '{update.StateName}' in their game, and is " +
                $"in '{fsm.ActiveStateName}' of '{fsm.Name}' here on its way there, where it plays the catch"
            );
            return;
        }

        if (way == null) {
            Logger.Info(
                $"[Own creatures] '{place}' caught {player.Username} from '{update.StateName}' in their game, but is " +
                $"in '{fsm.ActiveStateName}' of '{fsm.Name}' here, through its leap, and plays no catch here"
            );
            MeetOthersGrab(player, fsm, update);
            return;
        }

        // A catch that the creature goes into from wherever it is, as the boss at the end of the second act into the
        // one that its strands tell it of, is played whenever a player was caught, but over one that it plays already
        // (USER 11-09 "只要有玩家被抓了就演抓人")
        if (fsm.GlobalTransitions.Contains(way)) {
            if (Entity.Action.EntityFsmActions.MoveFrom(fsm, fsm.GetState(into)).Contains(fsm.ActiveState)) {
                Logger.Info(
                    $"[Own creatures] '{place}' caught {player.Username} in their game, but plays a catch in " +
                    $"'{fsm.ActiveStateName}' of '{fsm.Name}' here already, and plays no other"
                );
                MeetOthersGrab(player, fsm, update);
                return;
            }
        } else if (!AtRollsOf(fsm, update)) {
            Logger.Info(
                $"[Own creatures] '{place}' caught {player.Username} from '{update.StateName}' in their game, but is " +
                $"in '{fsm.ActiveStateName}' of '{fsm.Name}' here in a later leap, and plays no catch here"
            );
            MeetOthersGrab(player, fsm, update);
            return;
        }

        PlayOthersCatch(player, fsm, update, into, way);
    }

    /// <summary>
    /// Plays in the scene host's game a catch of another player's that came before its creature got to the state that
    /// the catch came from there, as the creature gets there (see <see cref="TakeOwnCatch"/>). One whose creature went
    /// another way, or rolled on its way there, plays none of it.
    /// </summary>
    private void TakeEarlyCatch(Fsm fsm, FsmState toState) {
        if (!_earlyCatches.TryGetValue(fsm, out var early) || fsm.ActiveState != toState) {
            return;
        }

        var word = early.Word;
        if (toState.Name != word.StateName && GoesOnInto(toState, state => state.Name == word.StateName)) {
            return;
        }

        _earlyCatches.Remove(fsm);
        var into = CatchInto(fsm, word);
        if (toState.Name != word.StateName || !AtRollsOf(fsm, word) || CatchWay(toState, into) is not { } way) {
            Logger.Info(
                $"[Own creatures] '{word.ObjectPath}' went to '{toState.Name}' of '{fsm.Name}' here on its way to " +
                $"'{word.StateName}', where it caught {early.Player.Username} in their game, and plays no catch here"
            );
            MeetOthersGrab(early.Player, fsm, word);
            return;
        }

        PlayOthersCatch(early.Player, fsm, word, into, way);
    }

    /// <summary>
    /// Plays in the scene host's game the catch of the player of another member's game (see
    /// <see cref="TakeOwnCatch"/>): the creature is put where theirs caught them, goes after the player it caught (see
    /// GamePatcher.GoAfterGrabbed) and takes the catch, which every other game takes from here, with what it does to
    /// that player kept off this game's (see <see cref="KeepOut"/>).
    /// </summary>
    private void PlayOthersCatch(
        ClientPlayerData player,
        Fsm fsm,
        CoopSaveUpdate update,
        string into,
        FsmTransition way
    ) {
        var creature = fsm.GameObject;
        var away = Vector2.Distance(creature.transform.position, new Vector2(update.Values[0], update.Values[1]));
        Logger.Info(
            $"[Own creatures] '{update.ObjectPath}' caught {player.Username} from '{update.StateName}' in their " +
            $"game, and goes from '{fsm.ActiveStateName}' to '{into}' of '{fsm.Name}' here too, where it was there, " +
            $"{away:F1} away"
        );
        PutAtSceneHostsStep(fsm, update);
        if (ThrownThings.FigureOf?.Invoke(update.Part, player.Id) is { } figure && figure != null) {
            GamePatcher.GoAfterGrabbed(creature, figure, fsm, fsm.GetState(into));
        }

        // A grab, which their game leads, goes on by their steps from its start (see CoopSave.OwnLedMoves)
        var grab = update.Sequence > 0;
        if (grab) {
            FollowLedMove(
                fsm, Entity.Action.EntityFsmActions.MoveFrom(fsm, fsm.GetState(into)), null, player.Id,
                update.Sequence
            );
        }

        // It turns to the player it caught where their game had them (see SeeTargetAsTold), as every other game then
        // does from here
        SeeTargetAsTold(creature, update);
        try {
            fsm.Event(way.FsmEvent);
        } finally {
            OwnCreatures.SeeTargetHere();
        }

        if (grab) {
            MeetLedMove(fsm, player.Id, update.Sequence);
        }
    }

    /// <summary>
    /// Has the scene host's game note that it plays none of a grab of another player's (see
    /// <see cref="TakeOwnCatch"/>), whose steps go nowhere here (see CoopSave.OwnLedMoves).
    /// </summary>
    private void MeetOthersGrab(ClientPlayerData player, Fsm fsm, CoopSaveUpdate update) {
        if (update.Sequence > 0) {
            MeetLedMove(fsm, player.Id, update.Sequence);
        }
    }

    /// <summary>
    /// The state that a catch of another player's took their creature to, from the state that it caught them in.
    /// </summary>
    private static string CatchInto(Fsm fsm, CoopSaveUpdate catchWord) {
        return fsm.GetState(catchWord.StateName).Transitions.Concat(fsm.GlobalTransitions).First(
            transition => IsCatchEvent(transition.EventName)
        ).ToState;
    }

    /// <summary>
    /// Whether the creature of a state machine in the scene host's game took its latest step at the rolls that a catch
    /// of another player's came with: it is in the leap that the catch came from, not in a later one.
    /// </summary>
    private bool AtRollsOf(Fsm fsm, CoopSaveUpdate catchWord) {
        return _ownSteps.TryGetValue(fsm, out var steps) && steps.Count > 0 &&
               steps[^1].Counts.SequenceEqual(catchWord.Amounts);
    }

    /// <summary>
    /// Whether the creature of a state machine in the scene host's game went into the state that a catch of another
    /// player's came from, at the rolls that the catch came with, so that it is through that state or in it (see
    /// <see cref="TakeOwnCatch"/>).
    /// </summary>
    private bool WasAt(Fsm fsm, CoopSaveUpdate catchWord) {
        return _ownSteps.TryGetValue(fsm, out var steps) && steps.Any(
            step => step.State == catchWord.StateName && step.Counts.SequenceEqual(catchWord.Amounts)
        );
    }

    /// <summary>
    /// Whether a state machine in a state goes on by itself into a state that is one of those looked for, from state to
    /// state as each one finishes, like the knight from its strike back at a blocked blow through its dash into its
    /// flurry.
    /// </summary>
    private static bool GoesOnInto(FsmState state, Func<FsmState, bool> isTarget) {
        var seen = new HashSet<string>();
        for (var at = state; seen.Add(at.Name);) {
            if (at.Transitions.FirstOrDefault(transition => transition.EventName == "FINISHED") is not { } finished ||
                at.Fsm.GetState(finished.ToState) is not { } next) {
                return false;
            }

            if (isTarget(next)) {
                return true;
            }

            at = next;
        }

        return false;
    }

    /// <summary>
    /// A leap at this game's player, a strike back at their blow, a talk, or the rest of a move after a hurt of a
    /// player, that a creature that every game runs by itself plays here by itself, in a game that follows the scene
    /// host's (see <see cref="PlaysOwnLeap"/>, CoopSave.OwnStrikeBacks, CoopSave.OwnTalks, CoopSave.OwnHits).
    /// </summary>
    /// <param name="place">Where the creature is in the room (see OwnCreatures.PlaceOf).</param>
    /// <param name="talk">Whether it is a talk.</param>
    /// <param name="what">What it is, for the log, if it is not a leap or a talk.</param>
    private sealed class OwnLeap(string place, bool talk, string? what = null) {
        /// <summary>
        /// Where the creature is in the room.
        /// </summary>
        public readonly string Place = place;

        /// <summary>
        /// Whether it is a talk.
        /// </summary>
        public readonly bool Talk = talk;

        /// <summary>
        /// What it is, for the log.
        /// </summary>
        public string What => what ?? (Talk ? "talk" : "leap at the local player");

        /// <summary>
        /// Whether the creature here is through the leap.
        /// </summary>
        public bool Through;

        /// <summary>
        /// For a catch that no leap led to, the states of its move, through which it goes on (see
        /// <see cref="StartsOwnRoomCatch"/>), and for the rest of a move after a hurt, the states of that (see
        /// <see cref="Hit"/>); null for anything else.
        /// </summary>
        public HashSet<FsmState>? Catch;

        /// <summary>
        /// Whether it is the rest of a move after a hurt of a player, through the states in <see cref="Catch"/> (see
        /// CoopSave.OwnHits): the steps of the scene host's into those are let go of, through it and after it, until
        /// the first one out of them, which the creature here goes on from.
        /// </summary>
        public bool Hit;

        /// <summary>
        /// Whether it is a move that acts on this game's player that this game leads, through the states in
        /// <see cref="Catch"/>, whose every step it tells the scene host's game (see CoopSave.OwnLedMoves).
        /// </summary>
        public bool Led;

        /// <summary>
        /// The number that this game gave a move that it leads (see <see cref="Led"/>), which its steps carry: the
        /// number of the blow of this game's player that set off an extraction, and of the grab, for a grab.
        /// </summary>
        public ulong Move;

        /// <summary>
        /// How many steps of a move that this game leads it told, whose numbers the scene host's game takes them in
        /// the order of (see CoopSave.TakeLedStep).
        /// </summary>
        public ulong Told;

        /// <summary>
        /// The latest step of the scene host's creature that came meanwhile, which the creature here goes on from once
        /// it is through.
        /// </summary>
        public CoopSaveUpdate? Latest;

        /// <summary>
        /// The game that the step came from.
        /// </summary>
        public ClientPlayerData? Host;
    }
}
