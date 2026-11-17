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
    /// The state machines of creatures that every game runs by itself that leap at this game's player, in a game that
    /// follows the scene host's, which plays the leap here by itself until it is through (see
    /// <see cref="PlaysOwnLeap"/>).
    /// </summary>
    private readonly Dictionary<Fsm, OwnLeap> _ownLeaps = new();

    /// <summary>
    /// The catches of other players that came to the scene host's game before its creature got to the state that the
    /// catch came from there, each to be played as the creature gets there (see <see cref="TakeOwnCatch"/>,
    /// <see cref="TakeEarlyCatch"/>).
    /// </summary>
    private readonly Dictionary<Fsm, (ClientPlayerData Player, CoopSaveUpdate Word)> _earlyCatches = new();

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
        _ownLeaps.TryGetValue(fsm, out var leap);
        if (leap is { Talk: false } && CatchesLocalPlayer(fsm, toState, creature)) {
            TellOwnCatch(machine);
        }

        // A talk that the local player starts is this game's to play, and the scene host's game is told of it (see
        // CoopSave.OwnTalks)
        if (leap is not { Through: false } && OwnCreatures.LocalTalk is { } talk && talk.Fsm == fsm) {
            StartsLocalTalk(machine, toState, talk.Event);
            return true;
        }

        if (leap == null) {
            return StartsOwnStrikeBack(machine, toState) || StartsOwnLeap(machine, toState) ||
                   StartsOwnTalk(machine, toState) || ToldInOwnLeap(fsm, creature);
        }

        if (leap.Through) {
            return false;
        }

        if (leap.Talk ? !Entity.Entity.EndsTalk(fsm, toState) : !GoesByPlayers(toState)) {
            leap.Passed.Add(toState.Name);
            return true;
        }

        leap.Through = true;
        Logger.Info(
            $"[Own creatures] '{leap.Place}' is through its {leap.What} in '{fsm.Name}', as it goes to " +
            $"'{toState.Name}'"
        );
        return false;
    }

    /// <summary>
    /// Whether a state machine of a creature that every game runs by itself, in a game that follows the scene host's,
    /// takes a step that another one of the creature or a part of it told it while the creature leaps at this game's
    /// player here, which is this game's to play as the leap is: the catch of this game's player and what it sets off,
    /// like the knight's hold of the player for its blows and the bounds of its arena that it keeps them in after it,
    /// which waited for a step of the scene host's that never came (review).
    /// </summary>
    private bool ToldInOwnLeap(Fsm fsm, GameObject creature) {
        if (Fsm.EventData?.SentByFsm is not { } sender || sender == fsm || sender.GameObject == null ||
            OwnCreatures.Owning(sender.GameObject) != creature) {
            return false;
        }

        foreach (var pair in _ownLeaps) {
            if (pair.Value is { Talk: false, Through: false } && pair.Key.GameObject == creature) {
                return true;
            }
        }

        return false;
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

        // A talk that is through here waits for the scene host's to come as far (see CoopSave.OwnTalks)
        if (leap is { Talk: true, Through: true }) {
            return WaitsForSceneHostsTalk(player, fsm, leap, update);
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
    /// state machine (see <see cref="PlaysOwnLeap"/>). That step is taken as any step: one that this game took already,
    /// at the same rolls, like the scene host's own landing, moves it only by as much as it stood off (USER 11-16 "扑完
    /// 之后跟最新的动作"). The rest of the steps of the scene host's come after that one.
    /// </summary>
    private void UpdateOwnLeaps() {
        if (_ownLeaps.Count == 0) {
            return;
        }

        foreach (var pair in _ownLeaps.ToList()) {
            var leap = pair.Value;
            if (!leap.Through || leap.Talk && !TalkGoesOn(pair.Key, leap)) {
                continue;
            }

            _ownLeaps.Remove(pair.Key);
            if (leap is not { Latest: { } latest, Host: { } host }) {
                Logger.Info(
                    $"[Own creatures] '{leap.Place}' is through its {leap.What} here, and no step of the scene " +
                    "host's came meanwhile"
                );
                continue;
            }

            Logger.Info(
                $"[Own creatures] '{leap.Place}' is through its {leap.What} here, and goes on from the latest step " +
                $"of {host.Username}'s game, to '{latest.StateName}'"
            );
            OnOwnCreature(host, latest);
        }
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
            return;
        }

        if (!AtRollsOf(fsm, update)) {
            Logger.Info(
                $"[Own creatures] '{place}' caught {player.Username} from '{update.StateName}' in their game, but is " +
                $"in '{fsm.ActiveStateName}' of '{fsm.Name}' here in a later leap, and plays no catch here"
            );
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
            GamePatcher.GoAfterGrabbed(creature, figure);
        }

        fsm.Event(way.FsmEvent);
    }

    /// <summary>
    /// The state that a catch of another player's took their creature to, from the state that it caught them in.
    /// </summary>
    private static string CatchInto(Fsm fsm, CoopSaveUpdate catchWord) {
        return fsm.GetState(catchWord.StateName).Transitions.Concat(fsm.GlobalTransitions).First(
            transition => Entity.Entity.CatchEvents.Contains(transition.EventName)
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
    /// A leap at this game's player, a strike back at their blow, or a talk, that a creature that every game runs by
    /// itself plays here by itself, in a game that follows the scene host's (see <see cref="PlaysOwnLeap"/>,
    /// CoopSave.OwnStrikeBacks, CoopSave.OwnTalks).
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
        /// The states that the creature went to here by itself in it, but for the one it is through at.
        /// </summary>
        public readonly HashSet<string> Passed = [];

        /// <summary>
        /// Whether the creature here is through the leap.
        /// </summary>
        public bool Through;

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
