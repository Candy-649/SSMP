using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The talks of the creatures that every game runs by itself (see OwnCreatures), like the one that a knight holds with
/// the player who wakes it before its fight. Each player talks to it in their own game, at their own pace, and the
/// creature goes on from the steps of the scene host's once it is over (USER 11-16 "把这个boss本地化了吧",
/// "改成本地状态机就不一定要烦恼这些了").
///
/// In a game that follows the scene host's, the creature runs all that comes before its fight by itself, as in a game
/// of one: what it does before the talk, like its sleep and the bow of a noise that woke it, the talk that this game's
/// player starts with it (see <see cref="StartsLocalTalk"/>), and the talk to its end, each line as this game's player
/// reads it. Nothing of a talk is told to another game, and every step of the scene host's in or before the end of the
/// talk is let go of (see <see cref="LetsGoOfSceneHostsTalk"/>): a player who has yet to talk to it is waited for
/// (USER 11-21 "就两遍分别对话，然后如果有人没对话就等就行了", "客机自己跑对话"). A talk that one game's player started
/// was told to the scene host's game, which took its creature into the talk too and put its player into it, and every
/// other game took it from there, putting its player into a talk they never started: the one whose walk to the
/// creature ended later read it free (USER 11-20 "对话开始时有概率不被游戏锁住"). Once the fight is on here, nothing of
/// a talk is looked at (see <see cref="BeforeFight"/>; USER 11-21 "这个开战前对话，只允许在开战前检查").
///
/// The talk is over as the creature leaves the state that ended its dialogue with no more of the talk to come, as a
/// talk with a copy is (see Entity.EndsTalk). That step waits in every game until every player is through their own
/// talk, as the fight after the talk of a room does (BossRoomCoop.TryHoldFightGate; USER 11-17 "为啥房主读完就开打，
/// 不应该都读完才开打吗"); then the creature here waits for the scene host's step out of the talk, or goes on from it if
/// it came while the talk went on (see <see cref="WaitsAfterOwnTalk"/>), as for a leap at this game's player (see
/// CoopSave.OwnCatches), and its fight is on. A player who comes in after the fight started joins it, on a way into the
/// step of the scene host's that runs none of the lines of the talk (see OnOwnCreatureStep). Each game shows its own
/// lines alone: the boss room shares none of them (BossRoomCoop.OnStartDialogue). The copy of the knight that the room
/// had before played the talk of the player who woke it in their game, and the talk that the scene host's game started
/// a moment later took the box from it (USER 11-16 "为啥我这里没有对话框").
///
/// The talk of the map seller comes again after each of her spars, in a state machine of hers that is no part of her
/// fight, so that every state of it lies before the end of a talk: none of its steps waits for one of the scene
/// host's (see <see cref="GoesByItselfBeforeFight"/>), as none is taken, and it gets to where she waits to be talked to
/// by the words of her fight and of the room in each game, or by the scene host's step there where it never got there
/// here (see <see cref="GoesToTalkAsThere"/>). Each spar after a talk waits for every player again
/// (BossRoomCoop.BeginsNextFight), and a no to her offer leads to no fight (BossRoomCoop.GetFightTransitions).
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The state machines of the creatures that every game runs by itself whose fight is on here, in a game that
    /// follows the scene host's: each took a step of the scene host's, the first of which comes beyond the talk of a
    /// creature that has one (see <see cref="BeforeFight"/>).
    /// </summary>
    private readonly HashSet<Fsm> _fightsOn = new();

    /// <summary>
    /// Whether a way into a step of the scene host's runs no line of a talk of a creature that every game runs by
    /// itself: the way into its fight here, which may go through its talk, and the way to where it waits to be talked
    /// to (see OnOwnCreatureStep, <see cref="GoesToTalkAsThere"/>).
    /// </summary>
    private bool _waySkipsTalk;

    /// <summary>
    /// The states that lie in or before the end of a talk, for each state machine of a creature that every game runs by
    /// itself whose steps this game has looked at before its fight (see <see cref="LiesBeforeTalkEnd"/>).
    /// </summary>
    private readonly Dictionary<Fsm, HashSet<string>> _beforeTalkEnd = new();

    /// <summary>
    /// Whether the fight of a creature that every game runs by itself has yet to come here, in a game that follows the
    /// scene host's: only then is anything of a talk looked at, that the local player starts one (see
    /// CoopSave.OwnCatches) or that a step of the scene host's lies in or before one (see
    /// <see cref="LetsGoOfSceneHostsTalk"/>).
    /// </summary>
    private bool BeforeFight(Fsm fsm) {
        return !_fightsOn.Contains(fsm);
    }

    /// <summary>
    /// Has a state machine of a creature that every game runs by itself, in a game that follows the scene host's, play
    /// a talk that this game's player started with it here by itself, as it goes to the first state of it.
    /// </summary>
    private void StartsLocalTalk(PlayMakerFSM machine, FsmState toState) {
        var talk = new OwnLeap(OwnCreatures.PlaceOf(machine.gameObject), true);
        _ownLeaps[machine.Fsm] = talk;
        Logger.Info(
            $"[Own creatures] The local player started a talk with '{talk.Place}', which goes to '{toState.Name}' of " +
            $"'{machine.FsmName}' and plays the talk here by itself"
        );
    }

    /// <summary>
    /// Whether a state machine of a creature that every game runs by itself, in a game that follows the scene host's,
    /// waits at the end of a talk that it played here by itself for the scene host's to come as far: every step of its
    /// own waits meanwhile, as one of the scene host's takes it on (see <see cref="EndsOwnTalkWait"/>).
    /// </summary>
    private bool WaitsAfterOwnTalk(Fsm fsm) {
        return _ownLeaps.TryGetValue(fsm, out var talk) && talk is { Talk: true, Through: true };
    }

    /// <summary>
    /// Lets go of a step of the scene host's creature, in a game that follows it, before its fight here, into a state
    /// that lies in or before the end of its talk, where the creature here goes by itself as this game's player talks
    /// to it (see CoopSave.OwnTalks). Whether it is let go of.
    /// </summary>
    private bool LetsGoOfSceneHostsTalk(ClientPlayerData player, Fsm fsm, CoopSaveUpdate update) {
        if (!BeforeFight(fsm) || !LiesBeforeTalkEnd(fsm, update.StateName)) {
            return false;
        }

        Logger.Info(
            $"[Own creatures] '{update.ObjectPath}' went to '{update.StateName}' of '{fsm.Name}' in " +
            $"{player.Username}'s game, in or before its talk, which it goes through here by itself"
        );
        return true;
    }

    /// <summary>
    /// Whether a step of a state machine of a creature that every game runs by itself, in a game that follows the
    /// scene host's, is taken by itself before the fight here: one into a state whose steps of the scene host's this
    /// game lets go of (see <see cref="LetsGoOfSceneHostsTalk"/>), as none comes to wait for. Every state of the state
    /// machine of the map seller's talk lies so, and it would wait for good at the call of the room that brings her in,
    /// at the words of her fight that send her away and back, and at the end of her talk.
    /// </summary>
    private bool GoesByItselfBeforeFight(Fsm fsm, FsmState toState) {
        return BeforeFight(fsm) && LiesBeforeTalkEnd(fsm, toState.Name);
    }

    /// <summary>
    /// Whether a creature that every game runs by itself, in a game that follows the scene host's, goes to a step of
    /// the scene host's before its fight here into a state where it waits for a player to talk to it (see
    /// Entity.WaitsForTalk), as it is neither in a talk here nor in such a state, and gets to none by itself (see
    /// <see cref="ComesToTalkByItself"/>): the way there runs no line of a talk, and its fight is not on for it. The
    /// map seller comes into her room at the call of the pole that a player strikes, which came before a member who
    /// came in after it: her talk would never start for them, as she never got to it here. Her song, as a player plays
    /// the needolin, ends into where she waits by itself, and is played to its end here (review: the scene host's end
    /// of the song cut it).
    /// </summary>
    private bool GoesToTalkAsThere(ClientPlayerData player, Fsm fsm, CoopSaveUpdate update) {
        if (!BeforeFight(fsm) || _ownLeaps.ContainsKey(fsm) || fsm.ActiveState is not { } here ||
            fsm.GetState(update.StateName) is not { } there || !Entity.Entity.WaitsForTalk(there) ||
            ComesToTalkByItself(fsm)) {
            return false;
        }

        Logger.Info(
            $"[Own creatures] '{update.ObjectPath}' went to '{update.StateName}' of '{fsm.Name}' in " +
            $"{player.Username}'s game, where it waits to be talked to, and goes there from '{here.Name}' here"
        );
        return true;
    }

    /// <summary>
    /// Whether a state machine is in a state where its creature waits for a player to talk to it (see
    /// Entity.WaitsForTalk), or gets to one by itself from there: by ways that the states take by what they do
    /// themselves (see TakenByItself).
    /// </summary>
    private static bool ComesToTalkByItself(Fsm fsm) {
        if (fsm.ActiveState is not { } from) {
            return false;
        }

        var seen = new HashSet<FsmState> { from };
        var next = new Queue<FsmState>();
        next.Enqueue(from);
        while (next.Count > 0) {
            var state = next.Dequeue();
            if (Entity.Entity.WaitsForTalk(state)) {
                return true;
            }

            foreach (var transition in state.Transitions) {
                if ((transition.ToFsmState ?? fsm.GetState(transition.ToState)) is { } to &&
                    TakenByItself(state, transition) && seen.Count < MaxWayInSteps && seen.Add(to)) {
                    next.Enqueue(to);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Has a creature that every game runs by itself, in a game that follows the scene host's, that waits at the end of
    /// a talk that it played here by itself go on from a step of the scene host's beyond the talk, which is taken as
    /// any step.
    /// </summary>
    private void EndsOwnTalkWait(ClientPlayerData player, Fsm fsm, OwnLeap talk, CoopSaveUpdate update) {
        _ownLeaps.Remove(fsm);
        Logger.Info(
            $"[Own creatures] '{talk.Place}' went to '{update.StateName}' of '{fsm.Name}' in {player.Username}'s " +
            "game, through the talk there too, and goes on from there here"
        );
    }

    /// <summary>
    /// Whether a state of a state machine lies in or before the end of a talk: a way from it leads to a state that ends
    /// its dialogue with a way out of it into no more of the talk (see Entity.EndsTalk), like the sleep of the knight,
    /// the bow of a noise that woke it and every state of its talk. The knight has the one talk, before its fight; the
    /// map seller's comes again after her spar, so every state of the state machine of her talk lies before the end of
    /// one.
    /// </summary>
    private bool LiesBeforeTalkEnd(Fsm fsm, string stateName) {
        if (!_beforeTalkEnd.TryGetValue(fsm, out var before)) {
            before = StatesBeforeTalkEnd(fsm);
            _beforeTalkEnd[fsm] = before;
        }

        return before.Contains(stateName);
    }

    /// <summary>
    /// The states of a state machine that lie in or before the end of a talk (see <see cref="LiesBeforeTalkEnd"/>): the
    /// states that end one, and every state with a way into one found so.
    /// </summary>
    private static HashSet<string> StatesBeforeTalkEnd(Fsm fsm) {
        var before = new HashSet<string>();
        var ledInto = new Dictionary<string, List<string>>();
        foreach (var state in fsm.States) {
            var endsDialogue = Array.Exists(state.Actions, action => action.Enabled && action is EndDialogue);
            foreach (var transition in state.Transitions) {
                // A way out that leads nowhere, which the game's state machines have, leads to no state
                if ((transition.ToFsmState ?? fsm.GetState(transition.ToState)) is not { } to) {
                    continue;
                }

                if (endsDialogue && !Entity.Entity.MoreTalkFollows(fsm, to)) {
                    before.Add(state.Name);
                }

                if (!ledInto.TryGetValue(to.Name, out var from)) {
                    from = [];
                    ledInto[to.Name] = from;
                }

                from.Add(state.Name);
            }
        }

        var queue = new Queue<string>(before);
        while (queue.Count > 0) {
            if (!ledInto.TryGetValue(queue.Dequeue(), out var from)) {
                continue;
            }

            foreach (var name in from) {
                if (before.Add(name)) {
                    queue.Enqueue(name);
                }
            }
        }

        return before;
    }

    /// <summary>
    /// Switches off the lines of a talk and the prompts of a state, for a way through it into a step of the scene
    /// host's (see OnOwnCreatureStep), returning them to be switched on again once it is entered.
    /// </summary>
    private static List<FsmStateAction> MuteTalk(FsmState state) {
        var muted = new List<FsmStateAction>();
        foreach (var action in state.Actions) {
            if (action.Enabled && action is RunDialogueBase or YesNoAction) {
                action.Enabled = false;
                muted.Add(action);
            }
        }

        return muted;
    }
}
