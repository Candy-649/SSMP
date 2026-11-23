using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The creatures that every game runs by itself as the room's own (see OwnCreatures), kept to how the scene host's
/// game has them. Each one rolls what the scene host's rolls, by its place in the room, and takes the same hits, so it
/// does what that one does as long as nothing that only one game has, like where its own player stands, takes it
/// somewhere else.
///
/// As a creature of the scene host's goes from one state to another, the members in the room are told which state it
/// went to, how many times its actions had rolled before and where it was. A creature of theirs that took the same
/// step, a moment before or after, but stood somewhere else as it did, is moved by as much as it was off, and where it
/// stood at the steps it kept goes with it, so that a step it took before it was moved is not made up for once more
/// (USER 11-14 "我刚刚看你站在怪身上": each step of a walk that it had taken before the word of the first came moved it
/// again, further off each time, 66 units at last). One that did not take it is put where the scene host's was and
/// takes it from the same rolls, the way the game goes there from the state it is in, so that what the game does on
/// the way is done too. Set straight into its song from a walk, it skipped the step that stops the walk first, which
/// the scene host's had no need of, as it had begun the song standing, and crawled on as it sang (USER 11-14
/// "边唱边移动"; USER 11-15 "唱歌这步就应该带着停下脚步，而不是去问房主走没走"). It hears every player's song in
/// every game, and each number of a song comes from the dice of that song (see SharedDice.RollForSong): which song
/// it is goes with each step, and one that is put to the step goes on from that song. A member who comes into the
/// room is told where each one is and what it does, or that it is dead, which theirs then is too. One that dies in any
/// game dies in the others as well, whatever killed it there: a blow that killed it in one game took less in another
/// that took the blows of both players in another order (see EnemyHealthCoop).
///
/// A step that goes by where the players are - whether one stands in a range, where the one it goes after is, how far,
/// whether it charged past them - is the scene host's to take, and so is a step that another state machine told it,
/// which decided by them too, but for those of a leap at the player of the game, which that game plays to its end by
/// itself (see CoopSave.OwnCatches), and those of a talk, which each game plays to its end at its player's pace (see
/// CoopSave.OwnTalks). In the other games the creature waits for it there: each of them has the players
/// a moment late and a little elsewhere, and a boss chose other moves in each game. Whom it goes after comes with each
/// step, and a member who comes in takes its health too. All else of a move each game plays itself, with the same
/// dice: what it rolls goes by the rolls that came with the step, so nothing waits for a number (USER 11-15
/// "总之以房主那边传过来的招式为单位。尽量需要的随机数就一起过来，应该不会出现中途等房主一个随机数的情况吧").
///
/// A part of such a creature with state machines of its own that looks at a player, like a hand of the boss at the end
/// of the second act, which aims its blades where the player it goes after stands, takes its steps as the creature
/// does: those that go by where the players are, and those that the creature or another part told it, are the scene
/// host's, and those are the ones that the scene host's game tells, as a part steps on many times in each move (see
/// <see cref="TakesOwnSteps"/>).
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How many of its last steps are kept of a state machine of a creature that every game runs by itself, to tell
    /// whether it took a step of the scene host's creature before the word of it came.
    /// </summary>
    private const int OwnStepsKept = 8;

    /// <summary>
    /// How far a creature that every game runs by itself may have stood from where the scene host's stood as both took
    /// the same step, before the log tells it: it is moved only as it makes a move (see OnOwnCreature).
    /// </summary>
    private const float OwnCreatureDrift = 0.5f;

    /// <summary>
    /// What an update of a creature that every game runs by itself is when it tells a member who has just come in
    /// which state it is in here (see TellOwnState), which their own takes as it comes, wherever it is.
    /// </summary>
    private const ushort OwnToldPart = 9;

    /// <summary>
    /// What an update of a creature that every game runs by itself is when it tells a step that a blow of another
    /// player than this game's set off here as the game took the blow, which every other game takes where its own
    /// creature is (see MovesAt), with that player in <see cref="CoopSaveUpdate.Key"/>, as a throw names a player
    /// (see ThrownThings.CodeOf), and the number of the blow in their game in <see cref="CoopSaveUpdate.Sequence"/>:
    /// their game plays it with the moment that the game stands still for, the feel of their blow, which every other
    /// game leaves out (see CoopHits.TakesAnothersBlow).
    /// </summary>
    private const ushort OwnStruckPart = 10;

    /// <summary>
    /// What an update of a creature that every game runs by itself is when it tells a step that a blow of this game's
    /// player set off here as the game took the blow, which every other game puts where it was here, as any other
    /// step, as it comes before that blow (see MovesAt), and takes without the moment that the game stands still for,
    /// which is this game's player's feel of their blow (see CoopHits.TakesAnothersBlow). So is a step that this game
    /// took as it struck back at a blow of another player at the word of their game, which played it first (see
    /// CoopSave.OwnStrikeBacks): the step of the knight's parry stood still the game of a third player (review).
    /// </summary>
    private const ushort OwnHostStruckPart = 11;

    /// <summary>
    /// What an update of a creature that every game runs by itself is when it tells that the scene host's game is
    /// through with a blow of another player on it, whatever the blow set off there, with that player in
    /// <see cref="CoopSaveUpdate.Key"/> and the number of the blow in their game in
    /// <see cref="CoopSaveUpdate.Sequence"/>: their game waits for an answer to it no more (see TellStrikeDone).
    /// </summary>
    private const ushort OwnStrikeDonePart = 14;

    /// <summary>
    /// What an update of a creature that every game runs by itself is when a game that follows the scene host's tells
    /// it that a state machine of the creature there got to a step of the scene host's later than the step came, by
    /// <see cref="CoopSaveUpdate.Values"/>[0] seconds more than it told last: the scene host's creature waits that
    /// long before steps that the others wait for, so that it catches up (see TellBehind, WaitsBehind).
    /// </summary>
    private const ushort OwnBehindPart = 15;

    /// <summary>
    /// Where in the values of an update of a creature that every game runs by itself the place of the player that it
    /// goes after is, beside it, as the game that took the step had them: x, then y (see SeeTargetAsTold).
    /// </summary>
    private const int OwnBesideValue = 8;

    /// <summary>
    /// Where in the values of an update of a creature that every game runs by itself that tells a member who has just
    /// come into the room which state it is in here (see TellOwnState) whether it is shown here is: 1, or 0 for out of
    /// sight.
    /// </summary>
    private const int OwnShownValue = 10;

    /// <summary>
    /// Where in the values of an update of a creature that every game runs by itself its health is, which a member who
    /// has just come into the room is told with it (see TellOwnState).
    /// </summary>
    private const int OwnHealthValue = 11;

    /// <summary>
    /// The most steps that a state machine takes through the states between on its way into a step of the scene host's
    /// (see WalkInto).
    /// </summary>
    private const int MaxWayInSteps = 16;

    /// <summary>
    /// The last steps of the state machines of the creatures that every game runs by itself, the last one last.
    /// </summary>
    private readonly Dictionary<Fsm, List<OwnStep>> _ownSteps = new();

    /// <summary>
    /// The hooks that see the creatures that every game runs by itself go from one state to another and die.
    /// </summary>
    private readonly List<Hook?> _ownCreatureHooks = [];

    /// <summary>
    /// Whether a creature that every game runs by itself dies as another game told, which is not told back.
    /// </summary>
    private bool _dyingAsTold;

    /// <summary>
    /// The state machines of creatures that every game runs by itself that wait here for a step of the scene host's,
    /// with the state each waits in (see OnOwnCreatureStep).
    /// </summary>
    private readonly Dictionary<Fsm, string> _heldOwnSteps = new();

    /// <summary>
    /// The actions by which a state goes by where the players are: those that look at where the player whom the
    /// creature goes after is, how far, whether it passed them, whether a player stands in one of its ranges, or
    /// whether this game's player has come into place. Facing one leaves where it goes as it is, and a song a creature
    /// hears in every game (see SharedDice.RollForSong).
    /// </summary>
    private static readonly HashSet<string> PlayerWayActionTypes = new(
        Entity.Action.ActionRegistry.TargetedFsmActionTypes
            .Where(type => !type.StartsWith("Face") && type is not ("Tk2DAnimationByDirection" or "TakeSilk"))
            .Concat(["CheckAlertRangeByName", "WaitForHeroInPosition"])
    );

    /// <summary>
    /// The variables that the actions by which a state goes by where the players are store, for each state machine of
    /// a creature that every game runs by itself whose steps this game has looked at (see GoesByPlayers).
    /// </summary>
    private readonly Dictionary<Fsm, HashSet<string>> _playerWayValues = new();

    /// <summary>
    /// The variables that a state adds to once as it starts, for each state machine of a creature that every game runs
    /// by itself whose steps this game has looked at (see ChoosesByCount).
    /// </summary>
    private readonly Dictionary<Fsm, HashSet<string>> _countedValues = new();

    /// <summary>
    /// Whether each state machine of a creature that every game runs by itself whose steps this game has taken makes
    /// moves of it (see MakesMoves).
    /// </summary>
    private readonly Dictionary<Fsm, bool> _makesMoves = new();

    /// <summary>
    /// Whether a step of the scene host's into each state of each state machine of a creature that every game runs by
    /// itself, whose steps this game has taken, leads it on a way that puts it at a place of its own (see
    /// PutsItselfOnItsWay).
    /// </summary>
    private readonly Dictionary<(Fsm Fsm, string State), bool> _putsItself = new();

    /// <summary>
    /// The state machines of creatures that every game runs by itself that took a step here that a blow set off, of
    /// this game's player or another's as it came, or a hurt of a player (see CoopSave.OwnHits), and go on from there
    /// by themselves until they next wait for a step of the scene host's (see TakeOwnStep).
    /// </summary>
    private readonly HashSet<Fsm> _ownWays = new();

    /// <summary>
    /// The steps of the scene host's, with whose game they came from and when they came, in seconds of real time, that
    /// a state machine of a creature that every game runs by itself gets to here by itself from where it is, the first
    /// first, as long as it has not got to them (see <see cref="KeepOwnStep"/>).
    /// </summary>
    private readonly Dictionary<Fsm, List<(ClientPlayerData Player, CoopSaveUpdate Step, float Came)>> _keptOwnSteps =
        new();

    /// <summary>
    /// Whether this game takes the steps of the scene host's that a state machine kept (see
    /// <see cref="TakeKeptSteps"/>).
    /// </summary>
    private bool _takingKeptSteps;

    /// <summary>
    /// The steps of the scene host's, with whose game they came from, that came for a state machine of a creature that
    /// every game runs by itself while it was still on its way to steps of the scene host's that it gets to by itself
    /// (see <see cref="_keptOwnSteps"/>), from the first one that it does not get to by itself on, as they came, with
    /// what that game told of what its steps did there, and when, in game time, the first came: it plays its move
    /// through, and takes them then (see <see cref="KeepsForAfterMove"/>).
    /// </summary>
    private readonly Dictionary<Fsm, (float Since, List<(ClientPlayerData Player, CoopSaveUpdate Step)> Steps)>
        _stepsAfterMove = new();

    /// <summary>
    /// How long, in seconds of game time, a state machine plays its move through before steps of the scene host's that
    /// came meanwhile, at the most (see <see cref="KeepsForAfterMove"/>): a move that the game plays by itself ends
    /// within moments, and one that has not by then waits in a state for what does not come here, like a place that it
    /// never gets to (review of 06e0bad).
    /// </summary>
    private const float MoveThroughWait = 1f;

    /// <summary>
    /// Whether this game takes the steps of the scene host's that a state machine kept for after its move (see
    /// <see cref="UpdateStepsAfterMove"/>).
    /// </summary>
    private bool _takingStepsAfterMove;

    /// <summary>
    /// How far behind the scene host's steps a state machine of a creature that every game runs by itself was told to
    /// be here last, in seconds (see <see cref="TellBehind"/>).
    /// </summary>
    private readonly Dictionary<Fsm, float> _toldBehind = new();

    /// <summary>
    /// How much further behind a state machine gets here, in seconds, before it is told: what the network adds or
    /// takes on the way, step by step, is less (see <see cref="TellBehind"/>).
    /// </summary>
    private const float BehindStep = 0.05f;

    /// <summary>
    /// How long, in the scene host's game, a state machine of a creature that every game runs by itself is to wait
    /// still before steps that the others wait for, by each member who told that it is behind there, in seconds (see
    /// <see cref="TakeBehind"/>, <see cref="WaitsBehind"/>).
    /// </summary>
    private readonly Dictionary<Fsm, Dictionary<ushort, float>> _owedWaits = new();

    /// <summary>
    /// The steps that state machines of creatures that every game runs by itself wait before in the scene host's game,
    /// with the state that they wait in, and until when, in seconds of real time (see <see cref="WaitsBehind"/>).
    /// </summary>
    private readonly Dictionary<Fsm, (string From, string To, float Until)> _waitsBehind = new();

    /// <summary>
    /// Whether this game gives a state machine the step that it waited before (see <see cref="UpdateWaitsBehind"/>),
    /// which goes without waiting again.
    /// </summary>
    private bool _givingBack;

    /// <summary>
    /// How long the scene host's creature waits before one step at the most, in seconds: it stands still a little
    /// longer, and waits the rest at its next (USER 11-23 "允许场景主机等一会儿", "场景主机后面的动作就插入一段比较小的等待来平衡
    /// 差距").
    /// </summary>
    private const float MostWaitAtStep = 0.3f;

    /// <summary>
    /// The speed below which the scene host's creature stands still, in units a second, so that it may wait before a
    /// step (see <see cref="WaitsBehind"/>): not in a jump, a dash or a flight.
    /// </summary>
    private const float StillSpeed = 0.5f;

    /// <summary>
    /// The steps of the scene host's that a state machine of a creature that every game runs by itself went on its way
    /// into through a state between, which does its part over a while and has not finished into the step yet (see
    /// OnOwnCreature).
    /// </summary>
    private readonly Dictionary<Fsm, CoopSaveUpdate> _waysThrough = new();

    /// <summary>
    /// The state machines of creatures that every game runs by itself that a blow of this game's player set off on a
    /// way of their own here, in a game that follows the scene host's, with until when, in unscaled time, the steps of
    /// the scene host's of them are from before the blow there (see <see cref="AwaitsLocalBlow"/>), the number of the
    /// blow (CoopHits.LocalStrikeNumber), which the answer of that game names: an answer to an earlier blow is from
    /// before this one there, and the state that the blow set it off into here, the first of its way, into which a
    /// blow there turns it only from some states (see <see cref="CanTurnInto"/>).
    /// </summary>
    private readonly Dictionary<Fsm, (float Until, uint Strike, string Into)> _awaitedBlows = new();

    /// <summary>
    /// The newest step of the scene host's of each state machine in <see cref="_awaitedBlows"/> that was let go of as
    /// from before the blow there, with whose game it came from: taken as the wait for the answer runs out with no
    /// answer, as the scene host's creature, in which the blow set nothing off there, went on meanwhile, and a state
    /// machine here waited for the step after it (review of 45dad32).
    /// </summary>
    private readonly Dictionary<Fsm, (ClientPlayerData Player, CoopSaveUpdate Step)> _stepsBeforeBlow = new();

    /// <summary>
    /// How long, in seconds, the steps of the scene host's of a state machine that a blow of this game's player set off
    /// on a way of its own are taken for steps from before the blow there, at the most (see
    /// <see cref="AwaitsLocalBlow"/>): the scene host's game has the blow a round trip later, and answers it with a
    /// step of its own, unless the blow sets nothing off there, and then tells that it is through with it (see
    /// TellStrikeDone). This is for a word that never comes.
    /// </summary>
    private const float LocalBlowAnswerWait = 1f;

    /// <summary>
    /// While a state machine goes through the states between into a step of the scene host's (see WalkInto): the
    /// states of the part of it that the step goes round in, the step, and the states that it went through.
    /// </summary>
    private HashSet<string>? _wayInLoop;

    private CoopSaveUpdate? _wayInStep;
    private readonly List<string> _wayInStates = [];

    /// <summary>
    /// Watches the creatures that every game runs by itself go from one state to another and die.
    /// </summary>
    private void RegisterOwnCreatureHooks() {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        OwnThreads.SendToRoomMembers = SendToRoomMembers;
        CoopHits.ReplayedStrike = TookOthersBlow;
        CoopHits.WaitsForAMove = PlaysMoveThrough;
        OwnCreatures.KeepsLocalPlayerOut = KeepsLocalPlayerOut;
        ToldParts.OwnPick = OwnPickOf;
        ToldParts.ToldOwnPick = TellOwnPick;
        _ownCreatureHooks.Add(CreateHook(
            typeof(Fsm).GetMethod("SwitchState", flags, null, [typeof(FsmState)], null),
            new Action<Action<Fsm, FsmState>, Fsm, FsmState>(OnOwnCreatureStep)
        ));

        // The largest of the ways to die, which the others call
        _ownCreatureHooks.Add(CreateHook(
            typeof(HealthManager).GetMethod(
                nameof(HealthManager.Die), flags, null,
                [
                    typeof(float?), typeof(AttackTypes), typeof(NailElements), typeof(GameObject), typeof(bool),
                    typeof(float), typeof(bool), typeof(bool)
                ],
                null
            ),
            new Action<Action<HealthManager, float?, AttackTypes, NailElements, GameObject, bool, float, bool, bool>,
                HealthManager, float?, AttackTypes, NailElements, GameObject, bool, float, bool, bool>(OnOwnCreatureDie)
        ));

        // Its look at its health (see CoopSave.OwnHealth)
        _ownCreatureHooks.Add(OwnHealthHook());
    }

    /// <summary>
    /// Hook for a state machine going to another state, which keeps the step of a creature that every game runs by
    /// itself and, in the scene host's game, tells the members in the room of it. It is told as it is taken, before the
    /// state starts, so that a step that the state takes at once is told after it.
    /// </summary>
    private void OnOwnCreatureStep(Action<Fsm, FsmState> orig, Fsm self, FsmState toState) {
        // A switch to no state, which the game makes nothing of, is passed on as it is
        if (OwnCreatures.Count == 0 || toState == null || self.FsmComponent is not { } machine || machine == null) {
            orig(self, toState!);
            return;
        }

        // A part of such a creature catches the local player only while the creature goes after them (see
        // CoopSave.OwnCatches). One that looks at a player takes its steps as the creature does, and the rest of them
        // go as the game has them, with their own dice (see OwnCreatures)
        if (!OwnCreatures.Has(machine.gameObject)) {
            if (TellsACatch(toState) && OwnCreatures.Owning(machine.gameObject) is { } owner &&
                !GoesAfterLocalPlayer(owner)) {
                RefuseCatch(self, owner, toState);
                return;
            }

            if (!TakesOwnSteps(machine)) {
                orig(self, toState);
                return;
            }
        }

        // The creature, or the part of it that takes its steps as it does
        var creature = machine.gameObject;

        // The way into a step of the scene host's, which plays as it came: nothing holds it back, and the step itself
        // is that one, which nothing here keeps to be found again (see TakeOwnStep). On a way through the states
        // between, the first state of the part that the step goes round in that it comes to is the step, where it is
        // put again as the scene host's was, as the states on the way may have moved it or rolled (see WalkInto)
        if (OwnCreatures.PlaysSceneHostsStep(self)) {
            var hostsStep = OwnCreatures.SceneHostsStep!.Value.State;
            if (_wayInLoop != null && toState.Name != hostsStep && _wayInLoop.Contains(toState.Name)) {
                toState = self.GetState(hostsStep);
            }

            var isTheStep = toState.Name == hostsStep;
            if (isTheStep) {
                OwnCreatures.SceneHostsStep = null;
                if (_wayInStep != null) {
                    PutAtSceneHostsStep(self, _wayInStep, KeepsItsPlace(self, _wayInStep));
                }
            } else if (_wayInLoop != null) {
                _wayInStates.Add(toState.Name);
            }

            _heldOwnSteps.Remove(self);
            _ownWays.Remove(self);

            // The step itself may be a leap at this game's player, which this game plays from there, and never a
            // talk, which each game's player has with the creature by themselves (see CoopSave.OwnTalks). A state on
            // the way into it does what it does as it is entered, but on the way into the creature's fight, which may
            // go through its talk, and to where it waits to be talked to, for a line of the talk or a prompt, which
            // would take this game's player into a talk that the way leaves at once (review: a member who came in
            // after the talk went through it on the way, and played every step after it by itself)
            if (isTheStep) {
                StartsOwnLeap(machine, toState);
                EnterOwnState(orig, self, toState);
                return;
            }

            var muted = _waySkipsTalk && Entity.Entity.RunsTalk(toState) ? MuteTalk(toState) : null;
            try {
                EnterOwnState(orig, self, toState);
            } finally {
                if (muted != null) {
                    foreach (var action in muted) {
                        action.Enabled = true;
                    }
                }
            }

            return;
        }

        // The rest of a way into a step of the scene host's through a state between that does its part over a while
        // and then finishes into the step, like the move of a blade of the boss at the end of the second act to where
        // it strikes from: the step plays as it came, put again where the scene host's was as it gets there (see
        // OnOwnCreature). It waited for the step once more, which had come already, hung where it had moved to for
        // 2.3 s, and was then set straight back into its hand, having struck nothing (the user's log of 11-21,
        // 21:52:01.581)
        if (_waysThrough.Count > 0 && _waysThrough.Remove(self, out var through) &&
            toState.Name == through.StateName) {
            Logger.Info(
                $"[Own creatures] '{OwnCreatures.PlaceOf(creature)}' gets from '{self.ActiveStateName}' to " +
                $"'{toState.Name}' of '{self.Name}', the step of the scene host's that it went on its way into"
            );
            _heldOwnSteps.Remove(self);
            _ownWays.Remove(self);
            ReachKeptStep(self, toState.Name);
            PutAtSceneHostsStep(self, through, KeepsItsPlace(self, through));
            StartsOwnLeap(machine, toState);
            EnterOwnState(orig, self, toState);
            return;
        }

        // A step that goes by where the players are or by a count that each game keeps for itself, or that another
        // state machine told it, is the scene host's: it waits for that, which has mostly come already, as this game
        // is a moment behind. So is a step into a state that tells a part of the room which it picks by where a player
        // is, after which the scene host's game tells which part it picked (see CoopSave.OwnPicks). The switch that the
        // game was to make is let go of, as the game tries it again at once until it is made. A leap at this game's
        // player is this game's to play, to its end (see CoopSave.OwnCatches), and so is a talk, at the end of which
        // it waits for the scene host's to come as far, and so is a step before the fight into a state whose steps of
        // the scene host's this game lets go of, which nothing comes to wait for (see CoopSave.OwnTalks). So is what a
        // strike of this game's player sets off as it is made, as in a game of one: a wound, a block or a tink, and
        // what they tell the other
        // state machines of the creature at once, though not where a state that it went into chooses its way on by
        // where the players are or by a count, which stays the scene host's. The scene host's game is told of the
        // strike as it was taken here, and its creature answers it as the game does, which every other game takes as a
        // step of the scene host's (USER 11-17 "如果这个会有反馈，我这边应该先演，然后通知房主，让房主那边的也触发这个弹
        // 开", "本地化就是为了让自己的怪是活的，可以触发各种反应"; a blade of the boss at the end of the second act that the
        // player tinked did nothing, as it went by where the players are, until the scene host's step came)
        var waitedFor = IsWaitedFor(self);
        var setOffByStrike = CoopHits.IsMakingLocalStrike && Fsm.EventData?.SentByFsm != self;
        var picks = PicksAPart(toState);

        // A way that a hurt of a player takes it plays at once where it is. A game that follows the scene host's tells
        // that game of a hurt of its own player, and plays the rest of the move by itself, as it does at the word of
        // another game (see CoopSave.OwnHits)
        var hit = IsHitWay(self, toState, creature);
        if (hit && _takingHit != self && OwnCreatures.FollowsSceneHost(creature)) {
            TellOwnHit(machine, toState, OwnHitPart);
            StartOwnHit(self, self.ActiveState);
        }

        if (OwnCreatures.FollowsSceneHost(creature) && !PlaysOwnLeap(machine, toState) &&
            !GoesByItselfBeforeFight(self, toState) &&
            (WaitsAfterOwnTalk(self) || waitedFor && !setOffByStrike || picks)) {
            MachineSwitchToStateField?.SetValue(self, null);
            _ownWays.Remove(self);
            if (!_heldOwnSteps.TryGetValue(self, out var waits) || waits != self.ActiveStateName) {
                _heldOwnSteps[self] = self.ActiveStateName;
                Logger.Info(
                    $"[Own creatures] '{OwnCreatures.PlaceOf(creature)}' would go from '{self.ActiveStateName}' to " +
                    $"'{toState.Name}' of '{self.Name}' here, and waits for the scene host's step"
                );
            }

            return;
        }

        // In the scene host's game, a step that the others wait for waits a moment, as the creature stands still, while
        // a game that follows this one is behind there (see WaitsBehind)
        if (WaitsBehind(self, toState, creature, waitedFor && !picks, hit)) {
            MachineSwitchToStateField?.SetValue(self, null);
            return;
        }

        // A step that a blow sets off here, of this game's player or another's as it comes, and the steps that its
        // state machine takes from there by itself, come at another moment of its way than in the scene host's game,
        // after other rolls: the scene host's are found among them by their states (see TakeOwnStep)
        _heldOwnSteps.Remove(self);
        var struck = CoopHits.IsMakingLocalStrike || CoopHits.IsReplayingPartnersStrike;
        if (struck) {
            _ownWays.Add(self);
        }

        // A blow of this game's player sets it off on a way of its own here a round trip before the scene host's game
        // has the blow: the steps that that game took before go by (see AwaitsLocalBlow), and so do the steps of the
        // scene host's that it kept to get to by itself (review of a0d1558). Only where that game tells its answer,
        // which a part tells only of the steps that the others wait for or that pick (see below): one that a part takes
        // by itself, like a tink, waits for no answer (review of 45dad32)
        if (CoopHits.IsMakingLocalStrike && OwnCreatures.FollowsSceneHost(creature) &&
            (!OwnCreatures.IsPart(creature) || waitedFor || picks)) {
            var strike = CoopHits.LocalStrikeNumber;
            var into = _awaitedBlows.TryGetValue(self, out var armed) && armed.Strike == strike
                ? armed.Into
                : toState.Name;
            _awaitedBlows[self] = (Time.unscaledTime + LocalBlowAnswerWait, strike, into);
            _keptOwnSteps.Remove(self);
        }

        SharedDice.RollByName(creature, OwnCreatures.RollName(creature));
        var step = new OwnStep(
            toState.Name, SharedDice.RollCounts(creature), creature.transform.position, creature.transform.localScale.x,
            _ownWays.Contains(self)
        );
        if (!_ownSteps.TryGetValue(self, out var steps)) {
            steps = [];
            _ownSteps[self] = steps;
        }

        steps.Add(step);
        if (steps.Count > OwnStepsKept) {
            steps.RemoveAt(0);
        }

        // A step of the scene host's that it was to get to by itself, which it gets to now (see KeepOwnStep)
        ReachKeptStep(self, toState.Name);

        // Where this game decides whom it goes after, it goes after the player it caught through the catch (see
        // GamePatcher.GoAfterGrabbed), and the player whose blow it strikes back at through the strike back (see
        // CoopSave.OwnStrikeBacks), which the step tells every game: the move is that player's
        if (!OwnCreatures.FollowsSceneHost(creature) && HeroController.SilentInstance is { } hero) {
            if (CatchesLocalPlayer(self, toState, creature)) {
                GamePatcher.GoAfterGrabbed(creature, hero.gameObject, self, toState);
            } else if (StrikesBackAtLocalBlow(self, toState)) {
                GamePatcher.GoAfterStriker(creature, hero.gameObject, self, toState);
            }
        }

        // The way that a hurt of a player takes it is told as the hurt, which every other game plays where its own
        // creature is (see CoopSave.OwnHits)
        if (IsSceneHost?.Invoke() == true && hit) {
            TellOwnHit(machine, toState, OwnHostHitPart);
        }

        // A part tells only the steps that the others wait for, which it took by where the players are, by a count or
        // as it was told, or into a state that picks a part of the room: the rest of them the others take by
        // themselves, and a part steps on many times in each move. One that a blow set off is told as such, of another
        // player, with whose it was, or of this game's own, and so is one of a strike back at a blow of another player
        // that this game takes at the word of their game (see MovesAt)
        if (IsSceneHost?.Invoke() == true && !hit && (!OwnCreatures.IsPart(creature) || waitedFor || picks)) {
            Logger.Info(
                $"[Own creatures] '{OwnCreatures.PlaceOf(creature)}' goes from '{self.ActiveStateName}' to " +
                $"'{toState.Name}' of '{self.Name}'" + (struck ? " at a blow" : "") +
                $" after {step.Counts.Count / 2} kinds of rolls, as the members in the room are told"
            );
            var update = OwnCreatureUpdate(machine, step);
            if (CoopHits.IsReplayingPartnersStrike) {
                update.PartCount = OwnStruckPart;
                update.Key = CoopHits.ReplayedStriker;
                update.Sequence = CoopHits.ReplayedStrikeNumber;
            } else if (struck || CoopHits.TakesAnothersBlow) {
                update.PartCount = OwnHostStruckPart;
            }

            SendToRoomMembers(update);
        }

        EnterOwnState(orig, self, toState);
        TakeEarlyCatch(self, toState);
        TakeEarlyHit(self, toState);
    }

    /// <summary>
    /// Hook for the death of a creature, which tells the members in the room that a creature that every game runs by
    /// itself died here, and where, unless it died as another game told, or by a blow of another player that is played
    /// here as it came: that player's game took the blow too, and plays the death of it or tells it. The map seller,
    /// beaten in the scene host's game, where she fell and came back by herself, was told dead there by a member's game
    /// that took the blow late after a stall of the network, and fell once more (the user's log of 11-21, 21:41:49).
    /// It is dead here all the same, which a word of its death from there finds, and the scene host's game tells that
    /// its health came to none at such a blow, which only that game tells (review of a0d1558: no game told it where a
    /// member's blow emptied it, and in theirs the boss at the end of the second act went on in its phase for good).
    /// </summary>
    private void OnOwnCreatureDie(
        Action<HealthManager, float?, AttackTypes, NailElements, GameObject, bool, float, bool, bool> orig,
        HealthManager self,
        float? attackDirection,
        AttackTypes attackType,
        NailElements nailElements,
        GameObject source,
        bool ignoreEvasion,
        float corpseFlingMultiplier,
        bool overrideSpecialDeath,
        bool disallowDropFlying
    ) {
        var tells = !_dyingAsTold && !self.isDead && OwnCreatures.Has(self.gameObject);
        // Where it is and how it is turned as the blow kills it, where every other game has it die too, and where the
        // player that it goes after is beside it, at whom its death looks there too
        var transform = self.transform;
        var where = transform.position;
        var facing = transform.localScale.x;
        var turned = transform.eulerAngles.z;
        var target = tells ? GamePatcher.TargetOf(self.gameObject) : null;
        var beside = target != null ? target.transform.position - where : Vector3.zero;
        orig(
            self, attackDirection, attackType, nailElements, source, ignoreEvasion, corpseFlingMultiplier,
            overrideSpecialDeath, disallowDropFlying
        );

        // One whose state machines play its death, like a boss, is never marked dead, and dies again at each blow
        // that finds it with no health: it is told once, where they play it as the game tells them that it has none.
        // One whose state machines go on by their own look at its health, like the boss at the end of the second act
        // through its phases, comes to no health at the end of each phase, which is no death: every game takes every
        // blow, and its own look plays its phases and its death, as the scene host's game tells that it has none (see
        // CoopSave.OwnHealth; USER 11-17 "转阶段后的报数之类的会有问题吗": the end of its first phase would go to the
        // others as its death, and their game would play a death of it that did nothing but let go of the blows that
        // it still had to take)
        if (tells && !self.isDead && self.hasSpecialDeath && self.hp <= 0 && !HearsNoHealth(self)) {
            TellOwnEmptied(self);
            return;
        }

        if (!tells || !self.isDead && !(self.hasSpecialDeath && self.hp <= 0 && HearsNoHealth(self)) ||
            !OwnCreatures.MarkDead(self.gameObject) || CoopHits.IsReplayingPartnersStrike) {
            return;
        }

        var creature = self.gameObject;
        var place = OwnCreatures.PlaceOf(creature);
        Logger.Info(
            $"[Own creatures] '{place}' died here at ({where.x:F1}, {where.y:F1}), as the members in the room are " +
            "told" + (target != null ? $", the player it goes after {beside.x:F1}, {beside.y:F1} from it" : "")
        );
        SendToRoomMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.OwnCreature,
            Scene = creature.scene.name,
            ObjectPath = place,
            PartCount = 2,
            Part = ThrownThings.CodeOf?.Invoke(target) ?? ThrownThings.NoPlayer,
            Values = attackDirection is { } direction
                ? [where.x, where.y, facing, turned, beside.x, beside.y, direction]
                : [where.x, where.y, facing, turned, beside.x, beside.y]
        });
    }

    /// <summary>
    /// Whether a state machine of a creature goes on as the game tells it that it has no health left (ZERO HP), by
    /// which it plays its death, like the boss whose fight lets rocks fall.
    /// </summary>
    private static bool HearsNoHealth(HealthManager health) {
        foreach (var machine in health.GetComponents<PlayMakerFSM>()) {
            var fsm = machine.Fsm;
            if (fsm.GlobalTransitions.Concat(fsm.States.SelectMany(state => state.Transitions))
                .Any(transition => transition.EventName == "ZERO HP")) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The word of a step that a creature that every game runs by itself took here, with how fast it went and fell,
    /// what the state that it leaves worked with, what its picks at random kept of their past picks (see
    /// SharedDice.WritePastPicks) and where its walk is (see GamePatcher.WriteWalk), which a game that
    /// waits for the step takes it with: a boss reckons the speed of its jump from where the player it goes after
    /// stands, which each game has a little elsewhere, and jumped as far as its own game had it, to be moved where the
    /// scene host's landed as it landed (USER 11-15, of that fight, "只有一步闪了一下，其他时候都很丝滑"). So does where the
    /// player that it goes after was beside it, which the start of the step turns to and measures by in every game
    /// (see SeeTargetAsTold).
    /// </summary>
    private static CoopSaveUpdate OwnCreatureUpdate(PlayMakerFSM machine, OwnStep step) {
        var transform = machine.transform;
        var speed = machine.TryGetComponent<Rigidbody2D>(out var body) ? body.linearVelocity : Vector2.zero;
        var fall = body != null ? body.gravityScale : 0f;
        var fsm = machine.Fsm;

        // Where the player that it goes after is beside it, as this game has them, or as the game whose step it takes
        // had them (see OwnCreatures.TargetThere)
        var target = OwnCreatures.TargetThere is { } there && there.Creature == machine.gameObject
            ? there.There
            : GamePatcher.TargetOf(machine.gameObject);
        var beside = target != null ? target.transform.position - step.Place : Vector3.zero;

        // The first step of a state machine leaves no state
        var worked = fsm.ActiveState is { } state
            ? ToldValues.Of(
                fsm,
                state.Actions.Where(action => action.Enabled)
                    .SelectMany(Entity.Action.EntityFsmActions.ValueVariablesOf).Distinct()
            )
            : ToldValues.None;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream)) {
            worked.Write(writer);
            SharedDice.WritePastPicks(writer, fsm);
            GamePatcher.WriteWalk(writer, machine.gameObject);
        }

        return new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.OwnCreature,
            Scene = machine.gameObject.scene.name,
            ObjectPath = OwnCreatures.PlaceOf(machine.gameObject),
            FsmName = machine.FsmName,
            StateName = step.State,
            Amounts = step.Counts,
            Part = ThrownThings.CodeOf?.Invoke(GamePatcher.TargetOf(machine.gameObject)) ?? ThrownThings.NoPlayer,
            Values = [
                step.Place.x, step.Place.y, transform.localScale.x, transform.eulerAngles.z,
                SharedDice.SongOf(machine.gameObject), speed.x, speed.y, fall, beside.x, beside.y
            ],
            Bytes = stream.ToArray()
        };
    }

    /// <summary>
    /// Tells a member who has come into the room where each creature that every game runs by itself is here and what
    /// it does, or that it is dead, if this game is the scene host's. Their game has them where the room starts them.
    /// Not while the local player is on the debug trip out of the room (see IsLeavingOnDebugTrip), in which a member
    /// came into the new start of the fight first: the silk boss, won here, was told dead to them, died once more in
    /// their game, and as the local player came in, their game told it dead to them in turn, which made the fight won
    /// twice (the user's log of 11-22, 20:50:14.756 and 17.140; USER "我们打了第二次，好诡异，丝母在左下角苏醒，然后最后打
    /// 死了莫名其妙判赢两次（").
    /// </summary>
    /// <param name="player">The member.</param>
    private void SendOwnCreaturesTo(ClientPlayerData player) {
        // A member who comes in takes the creatures where they are here, behind in nothing (review of 9ad6c79: one who
        // came back was waited for as far as they were behind before they left)
        foreach (var owed in _owedWaits.Values) {
            owed.Remove(player.Id);
        }

        if (OwnCreatures.Count == 0 || !_checkedMembers.Contains(player.Id) || IsSceneHost?.Invoke() != true) {
            return;
        }

        if (IsLeavingOnDebugTrip?.Invoke() == true) {
            Logger.Info(
                $"[Own creatures] {player.Username} is told nothing of the creatures here: the local player is on " +
                "the debug trip out of the room, to the new start of the fight that they came into"
            );
            return;
        }

        foreach (var pair in OwnCreatures.All) {
            var creature = pair.Key;
            if (creature == null || creature.TryGetComponent<HealthManager>(out var health) && health.isDead) {
                Logger.Info($"[Own creatures] {player.Username} is told that '{pair.Value.Place}' is dead here");
                Send(new CoopSaveUpdate {
                    TargetId = player.Id,
                    Kind = CoopSaveUpdateKind.OwnCreature,
                    Scene = pair.Value.Scene,
                    ObjectPath = pair.Value.Place,
                    PartCount = 1
                });
                continue;
            }

            // What the third act does to it, out or not, before its steps: the health that comes with them has what
            // the game doubles for one that turns already
            if (OwnThreads.WordFor(creature) is { } thread) {
                thread.TargetId = player.Id;
                Send(thread);
            }

            // One that has not come out of where the room keeps it, as the save has it, does nothing yet
            if (!creature.activeInHierarchy) {
                continue;
            }

            foreach (var machine in creature.GetComponents<PlayMakerFSM>()) {
                TellOwnState(player, machine, health);
            }
        }

        // The parts that take their steps as their creatures do, of those that are alive and out here
        foreach (var (part, creature) in OwnCreatures.AllParts()) {
            if (part == null || creature == null || !creature.activeInHierarchy || !part.activeInHierarchy ||
                creature.TryGetComponent<HealthManager>(out var health) && health.isDead) {
                continue;
            }

            foreach (var machine in part.GetComponents<PlayMakerFSM>()) {
                if (TakesOwnSteps(machine)) {
                    TellOwnState(player, machine, null);
                }
            }
        }
    }

    /// <summary>
    /// Tells a member who has come into the room which state a state machine of a creature that every game runs by
    /// itself, or of a part of one, is in here, whether it is shown here, and the creature's health.
    /// </summary>
    private void TellOwnState(ClientPlayerData player, PlayMakerFSM machine, HealthManager? health) {
        var fsm = machine.Fsm;
        var thing = machine.gameObject;
        var last = _ownSteps.TryGetValue(fsm, out var steps) && steps.Count > 0 ? steps[^1] : (OwnStep?) null;
        var counts = last is { } step && step.State == fsm.ActiveStateName ? step.Counts : SharedDice.RollCounts(thing);
        var update = OwnCreatureUpdate(
            machine,
            new OwnStep(fsm.ActiveStateName, counts, thing.transform.position, thing.transform.localScale.x)
        );
        update.TargetId = player.Id;
        update.PartCount = OwnToldPart;

        // Whether it is shown here, as the states that show it and hide it made it (see OnOwnCreature)
        var shown = thing.TryGetComponent<Renderer>(out var look) && look.enabled;
        update.Values.Add(shown ? 1f : 0f);

        // Their own has all its health, which the blows before they came took here
        if (health != null) {
            update.Values.Add(health.hp);
        }

        Logger.Info(
            $"[Own creatures] {player.Username} is told that '{OwnCreatures.PlaceOf(thing)}' is in " +
            $"'{fsm.ActiveStateName}' of '{fsm.Name}' here" + (look != null && !shown ? ", out of sight" : "") +
            (health != null ? $", with {health.hp} health" : "")
        );
        Send(update);
    }

    /// <summary>
    /// Keeps a creature that every game runs by itself to a step that the scene host's took, or to its death there.
    /// </summary>
    /// <param name="player">The player whose game is the scene host's.</param>
    /// <param name="update">The update.</param>
    private void OnOwnCreature(ClientPlayerData player, CoopSaveUpdate update) {
        var place = update.ObjectPath;
        if (OwnCreatures.Find(update.Scene, place) is not { } creature) {
            Logger.Info($"[Own creatures] {player.Username}'s game told of '{place}', which does not run here");
            return;
        }

        // What the third act does to it there, whether it is out here yet or not: one that comes out later is set up
        // by it as it starts
        if (update.PartCount == OwnThreads.WordPart) {
            OwnThreads.Take(creature, update, player.Username);
            return;
        }

        // The rocks that a fight lets fall have no health, and never die (see OwnCreatures)
        var hasHealth = creature.TryGetComponent<HealthManager>(out var health);
        if (!creature.activeInHierarchy || hasHealth && health.isDead) {
            Logger.Info(
                $"[Own creatures] {player.Username}'s game told of '{place}', which is dead or not out here" +
                (update.PartCount is 1 or 2 ? ", and dead there too" : $", and went to '{update.StateName}' there")
            );
            return;
        }

        // Dead there before the member came in: taken away, as the room has it there
        if (update.PartCount == 1) {
            Logger.Info($"[Own creatures] '{place}' is dead in {player.Username}'s game and is taken away here");
            creature.SetActive(false);
            return;
        }

        // Died there just now: it dies here too, as the game has a creature die, though the blow that killed it there
        // did not here, or has not come yet
        if (update.PartCount == 2) {
            if (!OwnCreatures.MarkDead(creature)) {
                Logger.Info($"[Own creatures] '{place}' died in {player.Username}'s game, and here already");
                return;
            }

            // Where it died there, turned as it was there: what its death leaves, like the boss that falls, talks and
            // leaps away after the fight, comes out where it did there, and not where this game had it in a move that
            // went on otherwise (USER 11-20 "感觉有位置不一致，比如说boss死的位置不一致": the knight that Luminary's
            // pins killed in her game, before it came at her, died in the user's game in its dash at her)
            var dying = creature.transform;
            var diedAt = new Vector3(update.Values[0], update.Values[1], dying.position.z);
            var lookedAt = update.Part != ThrownThings.NoPlayer;
            Logger.Info(
                $"[Own creatures] '{place}' died in {player.Username}'s game and dies here too, where it did there, " +
                $"{Vector2.Distance(dying.position, diedAt):F1} away" + (lookedAt
                    ? $", the player it goes after {update.Values[4]:F1}, {update.Values[5]:F1} from it there"
                    : "")
            );
            dying.position = diedAt;
            var scale = dying.localScale;
            scale.x = update.Values[2];
            dying.localScale = scale;
            dying.rotation = Quaternion.Euler(0f, 0f, update.Values[3]);

            // Its death looks at the player that it went after where that game had them beside it, and falls the way
            // it fell there
            if (lookedAt) {
                OwnCreatures.SeeTargetAtDeath(creature, diedAt + new Vector3(update.Values[4], update.Values[5], 0f));
            }

            OwnCreatures.AwardJournalKill(health);
            _dyingAsTold = true;
            try {
                health.Die(
                    update.Values.Count > 6 ? update.Values[6] : null, AttackTypes.Generic, NailElements.None, null,
                    true, 1f, false, false
                );
            } finally {
                _dyingAsTold = false;
            }

            return;
        }

        // A game that follows this one got to steps of this one later than they came there (see TellBehind)
        if (update.PartCount == OwnBehindPart) {
            TakeBehind(player, creature, update);
            return;
        }

        // One that comes while steps of the scene host's before it wait for the creature here to play its move through
        // waits behind them, as it came, whatever takes it then: a step, what a step did there, a hurt of a player,
        // a catch, a strike back or a pick (see KeepsForAfterMove; review of 06e0bad: a hurt that came was looked at
        // in the move before here, and let go of)
        if (!_takingStepsAfterMove && _stepsAfterMove.Count > 0 &&
            update.PartCount is not (OwnEmptiedPart or OwnStrikeDonePart) &&
            PlayMakerFSM.FindFsmOnGameObject(creature, update.FsmName) is { } waiting &&
            _stepsAfterMove.TryGetValue(waiting.Fsm, out var afterMove)) {
            afterMove.Steps.Add((player, update));
            Logger.Info(
                $"[Own creatures] '{place}' went to '{update.StateName}' of '{update.FsmName}' in " +
                $"{player.Username}'s game (part {update.PartCount}), which waits behind " +
                $"{afterMove.Steps.Count - 1} step(s) of that game until its move is through here"
            );
            return;
        }

        // A part of its own caught the player of that game, which plays the catch by itself
        if (update.PartCount == OwnCatchPart) {
            TakeOwnCatch(player, creature, update);
            return;
        }

        // It struck back at a blow of the player of that game that it blocked there, which that game plays by itself
        if (update.PartCount == OwnStrikeBackPart) {
            TakeOwnStrikeBack(player, creature, update);
            return;
        }

        // It hurt a player there, which it plays here too, where it is (see CoopSave.OwnHits)
        if (update.PartCount is OwnHitPart or OwnHostHitPart) {
            TakeOwnHit(player, creature, update);
            return;
        }

        // It told a part of the room there that it picked by where a player is, which it tells here too
        if (update.PartCount == OwnPickPart) {
            TakeOwnPick(player, creature, update);
            return;
        }

        // Its health came to none there, after which the next blow, of any player, plays here as the game has it
        if (update.PartCount == OwnEmptiedPart && hasHealth) {
            TakeOwnEmptied(player, health, update);
            return;
        }

        // That game is through with a blow of a player on it, whatever the blow set off there
        if (update.PartCount == OwnStrikeDonePart) {
            TakeStrikeDone(player, update);
            return;
        }

        // A step of the scene host's in or before the end of a talk, before the fight here, which the creature here
        // goes through by itself as this game's player talks to it, but for one to where it waits to be talked to,
        // which it goes to as well when it is neither there nor in a talk here, or one while this game plays a leap at
        // its own player or a talk by itself (see CoopSave.OwnTalks, CoopSave.OwnCatches)
        var fsm = PlayMakerFSM.FindFsmOnGameObject(creature, update.FsmName).Fsm;

        // The answer of the scene host's game to a blow of this game's player that set it off on a way of its own here:
        // what that game takes from then on comes after the blow there (see AwaitsLocalBlow), whatever takes this one
        // (review of 45dad32: one that came in a leap was kept for its end, and the wait went on). One to an earlier
        // blow comes from before this one there, and is let go of as such, or found among the steps that it took here
        // (review of f2a55f9: the answer to the first of two blows let go of the wait for the second, and the steps of
        // that game from before the second played over what it set off here)
        if (update.PartCount == OwnStruckPart && IsLocalPlayersBlow(player, update) &&
            _awaitedBlows.TryGetValue(fsm, out var awaited) && awaited.Strike == update.Sequence) {
            _awaitedBlows.Remove(fsm);
            _stepsBeforeBlow.Remove(fsm);
        }

        var toTalk = GoesToTalkAsThere(player, fsm, update);
        if (!toTalk && (LetsGoOfSceneHostsTalk(player, fsm, update) || KeepsForOwnLeap(player, fsm, update))) {
            return;
        }

        // A step of a part while its creature here plays a move by itself: the part goes on as the creature tells it
        // here, and from the latest of these once the creature is through (see TakePartStepsAfter)
        if (OwnCreatures.IsPart(creature) && OwnCreatures.Owning(creature) is { } owner && InOwnLeap(owner)) {
            _partStepsInLeap[fsm] = (player, update);
            _waysThrough.Remove(fsm);
            Logger.Info(
                $"[Own creatures] '{place}' went to '{update.StateName}' of '{fsm.Name}' in {player.Username}'s " +
                "game, while its creature plays a move here by itself, and goes on as the creature tells it here " +
                "until it is through"
            );
            return;
        }

        // One that the scene host's game took before it had a blow of this game's player that set it off on a way of
        // its own here is let go of (see AwaitsLocalBlow), and leaves the steps that it took here as they are, among
        // which the answer finds the one that the blow set off, as the blow turns the creature there into that way as
        // well. One into a state from which no blow turns it into that way is its way there, which the blow, coming
        // later, leaves as it is: it is taken at once, and the wait ends, as what the blow set off here was behind the
        // scene host's creature (USER 11-22 "这个等反应连晕倒起来都能扣下吗，这个优先级怎么看都不对吧", "那如果在这期间出
        // 招了，是不是你出招也会扣下"). The map seller that the user struck in her stun at 16:42:26.849, woken there by
        // Luminary's blow, lay in it here until 27.846, as her waking came at 27.038 and her next move after it, which
        // struck Luminary there at 27.268 (the user's log and Luminary's of 11-22)
        if (update.PartCount != OwnToldPart && AwaitsLocalBlow(fsm) && !HasOwnStep(fsm, update)) {
            var into = _awaitedBlows[fsm].Into;
            if (CanTurnInto(fsm, update.StateName, into)) {
                _stepsBeforeBlow[fsm] = (player, update);
                Logger.Info(
                    $"[Own creatures] '{place}' went to '{update.StateName}' of '{fsm.Name}' in {player.Username}'s " +
                    "game before that game had the blow of this game's player that set it off on a way of its own " +
                    "here, which it goes on with"
                );
                return;
            }

            _awaitedBlows.Remove(fsm);
            _stepsBeforeBlow.Remove(fsm);
            Logger.Info(
                $"[Own creatures] '{place}' went to '{update.StateName}' of '{fsm.Name}' in {player.Username}'s " +
                $"game before that game had the blow of this game's player that set it off into '{into}' here, " +
                "into which no blow turns it from there, and takes the step"
            );
        }

        var transform = creature.transform;
        var there = new Vector3(update.Values[0], update.Values[1], transform.position.z);
        var found = TakeOwnStep(fsm, update, out var newest);

        // One that it gets to here by itself is kept until it gets there (see below). The steps of the scene host's
        // before this one that it was to get to by itself, and has not, are taken first, as they came, before this one
        // tells whom it goes after and that its fight is on, and one that it played here already is past them (see
        // TakeKeptSteps; review of a0d1558: whom the last kept step went after stood, and the first step into the
        // fight, kept, ran the talk on the way), but while it is still on its way to them, as it plays its move
        // through, which this one waits for (see KeepsForAfterMove)
        var keeps = found == null && update.PartCount != OwnToldPart && !_takingKeptSteps &&
                    ReachesByItself(fsm, update.StateName);
        if (found != null && !_takingKeptSteps) {
            _keptOwnSteps.Remove(fsm);
        } else if (found == null && !keeps) {
            if (KeepsForAfterMove(player, fsm, update)) {
                return;
            }

            TakeKeptSteps(fsm);
        }

        // The step is taken, or was played here already, and the fight of the creature is on here, but for one to
        // where it waits to be talked to: a way into it from the talk here runs none of the talk, and nothing of a talk
        // is looked at for it after (see CoopSave.OwnTalks)
        var intoFight = !keeps && !toTalk && _fightsOn.Add(fsm);

        // Whom it goes after there, which nothing here chooses for it
        if (update.Part != ThrownThings.NoPlayer && ThrownThings.FigureOf?.Invoke(update.Part, player.Id) is
                { } target && target != null) {
            GamePatcher.TakeSceneHostsTarget(creature, target);
        }

        // The health that it has there, for a member who has just come in
        if (update.Values.Count > OwnHealthValue && hasHealth) {
            var healthThere = (int) update.Values[OwnHealthValue];
            if (health.hp != healthThere) {
                Logger.Info(
                    $"[Own creatures] '{place}' has {healthThere} health as in {player.Username}'s game, " +
                    $"rather than {health.hp}"
                );
                health.hp = healthThere;
            }

            OwnThreads.TookHealth(creature);
        }

        if (found is { } took) {
            // Ahead of the scene host's game, it is behind there no more (see TellBehind)
            TellBehind(fsm, 0f);

            // Played here already, from a blow that came here at another moment: where it is and the way it faces are
            // its own, until it next waits for a step of the scene host's. What the scene host's game rolled and told
            // on its way there between the blow here and the blow there, this one never does, as the blow stopped it
            // before: it goes on from as many as there, and so do the steps that it took since, so that what comes
            // after the blow rolls the same in every game (USER 11-17 "然后后面得泥石流，战吼，都是统一的")
            if (took.OwnWay) {
                var ahead = SharedDice.CountsAhead(update.Amounts, took.Counts);
                if (ahead.Count > 0) {
                    SharedDice.RollByName(creature, OwnCreatures.RollName(creature));
                    SharedDice.SetRollCounts(creature, SharedDice.AddCounts(SharedDice.RollCounts(creature), ahead));
                    AddToOwnWays(creature, ahead);
                }

                Logger.Info(
                    $"[Own creatures] '{place}' went to '{update.StateName}' of '{fsm.Name}' in {player.Username}'s " +
                    $"game, which it played here already, as a blow or a hurt set it off, {ahead.Count / 2} kinds " +
                    "of rolls later there"
                );
                return;
            }

            // Where it stood as it took the step is its own: a creature is put where the scene host's is only as it
            // makes a move, which a step that the others wait for tells, and goes on from there by itself, as fast as
            // there, through its knockbacks, flights and landings (USER 11-18 "不如位置校准只在出招时做", "因为出招的时候
            // 必定携带位置信息"). A member who has just come in is put where it is there, wherever their own is (review),
            // by where it is here now, as the word tells where it is there now: it took the step before the state
            // moved it. The silk boss, which its sleep puts down below the room, was put as far again down and to the
            // left, 21.4, and rose there as its fight began once that game ran the room (the user's log of 11-22,
            // 20:44:54.061; USER "我是说我们后来又打了一次丝母，点开战之后丝母在左下角出现"), and a member who came in
            // on it after was put twice as far (42.8, Luminary's log, 20:47:06.595)
            var off = there - (update.PartCount == OwnToldPart ? transform.position : took.Place);
            off.z = 0f;
            if (update.PartCount == OwnToldPart && MakesMoves(fsm)) {
                Logger.Info(
                    $"[Own creatures] '{place}' is in '{update.StateName}' as in {player.Username}'s game, which " +
                    $"this member has just come into, {off.magnitude:F1} away from where it is there, and is moved " +
                    "by that"
                );
                transform.position += off;
                MoveOwnSteps(creature, off);
            } else if (off.magnitude > OwnCreatureDrift) {
                Logger.Info(
                    $"[Own creatures] '{place}' went to '{update.StateName}' as in {player.Username}'s game, " +
                    $"{off.magnitude:F1} away from where it did there, and goes on from where it is to its next move"
                );
            }

            // A member who has just come in: its walk goes on where the scene host's walks now, rather than from the
            // fresh start that the room gave it as it came in, which took it to the same step within a few frames
            // (review), and so do its picks at random from what the scene host's kept, where it has none
            if (newest && update.Values.Count > OwnHealthValue) {
                using var reader = new BinaryReader(new MemoryStream(update.Bytes));
                ToldValues.Read(reader);
                SharedDice.PutPastPicks(reader, fsm);
                GamePatcher.PutWalk(reader, creature);
            }

            // Facing the way it faced there as it took the step, when that is the step it is at and the state has not
            // turned it here since: it faced the player it went after where this game has them. A state that turns it
            // as it starts, toward the player it goes after, was turned back to the facing it had before, which the
            // word tells, and turned again at the next step that came (review)
            if (newest && !Mathf.Approximately(took.Facing, update.Values[2]) &&
                Mathf.Approximately(transform.localScale.x, took.Facing)) {
                Logger.Info($"[Own creatures] '{place}' faces the way it does in {player.Username}'s game");
                var turned = transform.localScale;
                turned.x = update.Values[2];
                transform.localScale = turned;
            }

            return;
        }

        // A step that it gets to by itself from where it is, a moment behind, as fast and as far as there, does not
        // move it there before it does (USER 11-18 "不如位置校准只在出招时做"): put there as the step came, a stunned
        // knight was put down where the scene host's had landed while it was still in the air here (the user's log,
        // 20:42:05.971, 3.1 away). A member who has just come in is put where it is there, wherever their own is. The
        // step is kept until it gets there (see KeepOwnStep)
        if (keeps) {
            Logger.Info(
                $"[Own creatures] '{place}' went to '{update.StateName}' of '{fsm.Name}' in {player.Username}'s " +
                $"game, which it gets to here by itself from '{fsm.ActiveStateName}'"
            );
            KeepOwnStep(fsm, player, update);
            return;
        }

        // Put where the scene host's was as it makes a move; a state machine that makes no move of it leaves it where
        // it is, and so does a step that a blow of another player than the scene host's set off there (see MovesAt).
        // What a blow set off there plays here as the creature's own, but for the moment that the game stands still,
        // which is the feel of the blow of the player who struck (see CoopHits.TakesAnothersBlow), and which this
        // game plays when that player is its own: the knight a moment behind here, still on its way to the guard
        // that parried a blow of this game's player there (review)
        // Waiting here for the step, it is behind there no more (see TellBehind)
        if (_heldOwnSteps.TryGetValue(fsm, out var heldAt) && heldAt == fsm.ActiveStateName) {
            TellBehind(fsm, 0f);
        }

        _waysThrough.Remove(fsm);
        var way = WayInto(fsm, update.StateName);
        var from = fsm.ActiveStateName;
        var moves = MovesAt(fsm, update);
        var keepsPlace = KeepsItsPlace(fsm, update);
        var away = Vector2.Distance(transform.position, there);
        if (moves && !keepsPlace) {
            MoveOwnSteps(creature, there - transform.position);
        }

        PutAtSceneHostsStep(fsm, update, keepsPlace);
        OwnCreatures.SceneHostsStep = (fsm, update.StateName);

        // Where it turns to the player that it goes after and how far it has them is the scene host's game's too: where
        // the scene host's had them, where its move takes it to a place of its own as there
        SeeTargetAsTold(creature, update, keepsPlace ? there : null);
        CoopHits.TakesAnothersBlow = update.PartCount == OwnHostStruckPart ||
                                     update.PartCount == OwnStruckPart && !IsLocalPlayersBlow(player, update);
        _waySkipsTalk = intoFight || toTalk;
        try {
            if (way != null) {
                // Put again as the scene host's was as it gets to the step, as on a way through the states between (see
                // WalkInto): a state that finishes into the step does what it does on the way, and a hand of a boss
                // worked out there again where to strike by where this game has the player
                _wayInStep = update;
                fsm.Event(way);
            } else {
                WalkInto(fsm, update);
            }
        } finally {
            OwnCreatures.SceneHostsStep = null;
            _wayInStep = null;
            _waySkipsTalk = false;
            CoopHits.TakesAnothersBlow = false;
            OwnCreatures.SeeTargetHere();
        }

        // A state between that does its part over a while finishes into the step after this, as the step of the scene
        // host's that it is (see OnOwnCreatureStep)
        var through = way != null && fsm.ActiveStateName != update.StateName &&
                      fsm.ActiveState is { Transitions: [var only] } && only.EventName == FsmEvent.Finished.Name &&
                      only.ToState == update.StateName;
        if (through) {
            _waysThrough[fsm] = update;
        }

        // A member who has just come in: shown or out of sight as it is there, as the states that show it and hide it
        // made it there, which the way here leaves out in the part of its state machine that the step goes round in
        // (see WalkInto). The map seller, whom a member came in on in a spar, stayed out of sight for them until she
        // next came back from a leap (review of 3d2f970)
        if (update.PartCount == OwnToldPart && creature.TryGetComponent<Renderer>(out var look) &&
            look.enabled != (update.Values[OwnShownValue] > 0f)) {
            look.enabled = !look.enabled;
            Logger.Info(
                $"[Own creatures] '{place}' is {(look.enabled ? "shown" : "out of sight")} as in {player.Username}'s " +
                "game, which this member has just come into"
            );
        }

        var how = way != null ? $"by '{way.Name}'" + (through ? $", on its way through '{fsm.ActiveStateName}'" : "")
            : _wayInStates.Count > 0 ? $"through '{string.Join("', '", _wayInStates)}'"
            : "straight";
        Logger.Info(
            $"[Own creatures] '{place}' goes from '{from}' to '{update.StateName}' {how} as in {player.Username}'s " +
            (keepsPlace ? $"game, where it is here, {away:F1} away, as its move puts it at a place of its own"
                : moves ? $"game{(update.PartCount == OwnHostStruckPart ? " at a blow there" : "")}, where it was " +
                          $"there, {away:F1} away"
                : update.PartCount == OwnStruckPart ? $"game at a blow there, where it is here, {away:F1} away"
                : "game, where it is here") + BesideAsTold(update)
        );
    }

    /// <summary>
    /// Whether the blow of another player than the scene host's that set off a step of the scene host's (see
    /// <see cref="OwnStruckPart"/>) was this game's player's.
    /// </summary>
    /// <param name="sceneHost">The scene host, whose step it is.</param>
    /// <param name="update">The step.</param>
    private static bool IsLocalPlayersBlow(ClientPlayerData sceneHost, CoopSaveUpdate update) {
        return HeroController.SilentInstance is { } hero &&
               ThrownThings.FigureOf?.Invoke((ushort) update.Key, sceneHost.Id) == hero.gameObject;
    }

    /// <summary>
    /// Has a creature that every game runs by itself, as it starts a step that another game took, look at the player
    /// that it goes after where that game had them beside it (see OwnCreatures.TargetThere): beside where it is here,
    /// whether it was put where it was there or, at a blow here, not, or beside where it was there, where its move
    /// takes it to a place of its own, where it comes out as there (see KeepsItsPlace).
    /// </summary>
    /// <param name="creature">The creature.</param>
    /// <param name="step">The step.</param>
    /// <param name="there">Where it was there, for one that is left where it is, or null.</param>
    private static void SeeTargetAsTold(GameObject creature, CoopSaveUpdate step, Vector3? there = null) {
        if (step.Part != ThrownThings.NoPlayer) {
            var beside = new Vector3(step.Values[OwnBesideValue], step.Values[OwnBesideValue + 1], 0f);
            OwnCreatures.SeeTargetThere(creature, (there ?? creature.transform.position) + beside);
        }
    }

    /// <summary>
    /// Where a step that another game took says that the player that its creature goes after was beside it, for the
    /// log, or nothing for one that went after no one.
    /// </summary>
    /// <param name="step">The step.</param>
    private static string BesideAsTold(CoopSaveUpdate step) {
        return step.Part == ThrownThings.NoPlayer
            ? ""
            : $", the player it goes after {step.Values[OwnBesideValue]:F1}, " +
              $"{step.Values[OwnBesideValue + 1]:F1} from it there";
    }

    /// <summary>
    /// Puts a creature that every game runs by itself where the scene host's was as it took a step, turned the way it
    /// was turned there, going as fast and falling as it fell there, with the rolls and the song that it took the step
    /// with, with what its picks at random kept of their past picks there (see SharedDice.PutPastPicks), with what the
    /// state that its state machine left worked with there, and with its walk where it was there (see
    /// GamePatcher.PutWalk). A state machine that makes no move of it, like the count of the knight's stun, takes
    /// the step where the creature is here, and so does a step that a blow of another player than the scene host's set
    /// off there (see <see cref="MovesAt"/>); one whose move puts it at a place of its own is left where it is, and
    /// turned as it is (see <see cref="KeepsItsPlace"/>).
    /// </summary>
    private void PutAtSceneHostsStep(Fsm fsm, CoopSaveUpdate update, bool keepsPlace = false) {
        var creature = fsm.GameObject;
        using var reader = new BinaryReader(new MemoryStream(update.Bytes));
        var worked = ToldValues.Read(reader);
        SharedDice.PutPastPicks(reader, fsm);
        SharedDice.RollByName(creature, OwnCreatures.RollName(creature));
        if (MovesAt(fsm, update)) {
            // The walk first: the game's own steps of it turn the creature and set it going, which the rest then puts
            // as it was there
            GamePatcher.PutWalk(reader, creature);
            var transform = creature.transform;
            if (!keepsPlace) {
                transform.position = new Vector3(update.Values[0], update.Values[1], transform.position.z);
                transform.rotation = Quaternion.Euler(0f, 0f, update.Values[3]);
            }

            var scale = transform.localScale;
            scale.x = update.Values[2];
            transform.localScale = scale;
            // Its fall too, which a state that the way here left out may have switched off: the knight's choice of a
            // slash of wind in the air did, and the jump that it was then set into never came down (Luminary's log of
            // 11-19, 20:39:34.777)
            if (creature.TryGetComponent<Rigidbody2D>(out var body)) {
                body.linearVelocity = new Vector2(update.Values[5], update.Values[6]);
                body.gravityScale = update.Values[7];
            }
        }

        SharedDice.SetRollCounts(creature, update.Amounts);
        SharedDice.SetSong(creature, (int) update.Values[4]);
        worked.ApplyTo(fsm);
    }

    /// <summary>
    /// Takes a state machine into a step of the scene host's creature that no way out of its state leads to, the way
    /// the game goes there: through the states between, each of which does what it does as it is entered and is left
    /// by the way that leads on, so that what they do on the way is done here too. A boss that a member found in the
    /// room's hiding place as they came in was set straight into its move, and the states that show it in the room, on
    /// its way into the fight in the scene host's game, never ran here (USER 11-15 "我刚进房主世界的时候，这个怪是透明的，
    /// 我看不到但是可以撞我"). Of the part of the state machine that the step goes round in, the first state that it would
    /// go to is the step itself, and where no way leads on it is set to the step.
    /// </summary>
    private void WalkInto(Fsm fsm, CoopSaveUpdate update) {
        _wayInLoop = LoopOf(fsm, update.StateName);
        _wayInStep = update;
        _wayInStates.Clear();
        try {
            for (var i = 0; i < MaxWayInSteps && OwnCreatures.PlaysSceneHostsStep(fsm) &&
                 !_wayInLoop.Contains(fsm.ActiveStateName) && FirstStepOfWay(fsm, update.StateName) is { } next; i++) {
                fsm.Event(next);
            }

            if (OwnCreatures.PlaysSceneHostsStep(fsm)) {
                fsm.SetStateAsItRuns(update.StateName);
            }
        } finally {
            _wayInLoop = null;
            _wayInStep = null;
        }
    }

    /// <summary>
    /// The states of the part of a state machine that a state goes round in: those that it can go to from the state
    /// and come back to it from, by the ways out of the states, the state among them.
    /// </summary>
    private static HashSet<string> LoopOf(Fsm fsm, string stateName) {
        var sources = new Dictionary<string, List<string>>();
        foreach (var state in fsm.States) {
            // A way out that leads nowhere, which the game's state machines have, leads to no state
            foreach (var transition in state.Transitions) {
                if (transition.ToState is not { } to) {
                    continue;
                }

                if (!sources.TryGetValue(to, out var list)) {
                    list = [];
                    sources[to] = list;
                }

                list.Add(state.Name);
            }
        }

        var after = new HashSet<string> { stateName };
        var queue = new Queue<string>(after);
        while (queue.Count > 0) {
            foreach (var transition in fsm.GetState(queue.Dequeue()).Transitions) {
                if (fsm.GetState(transition.ToState) != null && after.Add(transition.ToState)) {
                    queue.Enqueue(transition.ToState);
                }
            }
        }

        // What it comes back to the state from, of what it goes to from it, is all of what leads back
        var loop = new HashSet<string> { stateName };
        queue.Enqueue(stateName);
        while (queue.Count > 0) {
            if (!sources.TryGetValue(queue.Dequeue(), out var list)) {
                continue;
            }

            foreach (var source in list) {
                if (after.Contains(source) && loop.Add(source)) {
                    queue.Enqueue(source);
                }
            }
        }

        return loop;
    }

    /// <summary>
    /// The event of the first step of the shortest way from the state that a state machine is in to the given state, by
    /// the ways out of the states, or null if none leads there. An event that a global transition of the state machine
    /// takes too leads no way: the game goes by the global transition first, wherever the state machine is.
    /// </summary>
    private static FsmEvent? FirstStepOfWay(Fsm fsm, string target) {
        var globalEvents = fsm.GlobalTransitions.Select(transition => transition.EventName).ToHashSet();
        var start = fsm.ActiveStateName;
        var firstSteps = new Dictionary<string, FsmEvent>();
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0) {
            var name = queue.Dequeue();
            foreach (var transition in fsm.GetState(name).Transitions) {
                if (globalEvents.Contains(transition.EventName) || fsm.GetState(transition.ToState) is not { } next ||
                    next.Name == start || firstSteps.ContainsKey(next.Name)) {
                    continue;
                }

                var first = name == start ? transition.FsmEvent : firstSteps[name];
                if (next.Name == target) {
                    return first;
                }

                firstSteps[next.Name] = first;
                queue.Enqueue(next.Name);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a state goes by where the players are: one of its actions looks at them (see
    /// <see cref="LooksAtPlayers"/>), or goes on by what an action of those kinds of its state machine stored, like the
    /// look of a boss before it charges at whether the player it goes after hid, which it saw a state before. What any
    /// of them stored counts, whatever it looked at: a choice by where the creature is itself comes out otherwise in a
    /// game where it stands elsewhere. An action that only runs its course by it, like a flight over a time by how far
    /// it goes, chooses nothing (see Entity.Action.EntityFsmActions.ChoosesWhereItGoes): the flight ends into the same
    /// state in every game.
    /// </summary>
    private bool GoesByPlayers(FsmState state) {
        var fsm = state.Fsm;
        if (!_playerWayValues.TryGetValue(fsm, out var stored)) {
            stored = [];
            foreach (var each in fsm.States) {
                foreach (var action in each.Actions) {
                    if (PlayerWayActionTypes.Contains(action.GetType().Name)) {
                        stored.UnionWith(Entity.Action.EntityFsmActions.ValueVariablesOf(action));
                    }
                }
            }

            _playerWayValues[fsm] = stored;
        }

        foreach (var action in state.Actions) {
            if (action.Enabled && (LooksAtPlayers(action) ||
                                   Entity.Action.EntityFsmActions.ChoosesWhereItGoes(action) &&
                                   Entity.Action.EntityFsmActions.ValueVariablesOf(action).Any(stored.Contains))) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an action looks at where the players are: one of the kinds that do (see
    /// <see cref="PlayerWayActionTypes"/>) that decides where its state goes by what it reads, or looks at anything
    /// else than its creature (see Entity.Action.EntityFsmActions.LooksOnlyAtItsCreature). A needle of the boss at the
    /// end of the second act, which reads where it is itself as it flies to where it strikes from, waited there for the
    /// scene host's step and was put where the scene host's was, after it had stopped there or while it was still on
    /// its way, up to 2.3 away, and so as it flew back to its hand, up to 11.7 away (the user's log of 11-22; USER
    /// "我这边针都摆好了为什么还会动发射位置", "为啥针飞到位置停好之后房主还要挪"). Where it strikes from is where its
    /// hand aimed, which the hand did as the scene host's.
    /// </summary>
    private static bool LooksAtPlayers(FsmStateAction action) {
        return PlayerWayActionTypes.Contains(action.GetType().Name) &&
               (Entity.Action.EntityFsmActions.ChoosesWhereItGoes(action) ||
                !Entity.Action.EntityFsmActions.LooksOnlyAtItsCreature(action));
    }

    /// <summary>
    /// Whether the others wait for a step that a state machine of a creature that every game runs by itself takes now,
    /// in the games that follow the scene host's: one out of a state that goes by where the players are or by a count
    /// that each game keeps for itself, or one that another state machine told it.
    /// </summary>
    private bool IsWaitedFor(Fsm fsm) {
        return fsm.ActiveState is { } from && (GoesByPlayers(from) || ChoosesByCount(from)) ||
               Fsm.EventData?.SentByFsm is { } sender && sender != fsm;
    }

    /// <summary>
    /// Whether a state machine of a creature that every game runs by itself makes moves of it: one with a state that
    /// goes by where the players are, whose steps tell where the creature is as it makes a move, which is where it is
    /// put where the scene host's is (USER 11-18 "不如位置校准只在出招时做", "因为出招的时候必定携带位置信息"). One that makes
    /// none, like the count of the knight's stun, took a step of the scene host's at each blow, which put the creature
    /// where the scene host's was as it counted the blow there, a moment on in its move and before the knockback of a
    /// blow of this game's player there (USER 11-18 "检查位置越频繁，就越是会因为正好客机在做反击或者正好客机攻击到怪而重置部
    /// 分位移"; the user's log: 33 of the 67 puts of a unit or more were the count's).
    /// </summary>
    private bool MakesMoves(Fsm fsm) {
        if (!_makesMoves.TryGetValue(fsm, out var makes)) {
            makes = fsm.States.Any(GoesByPlayers);
            _makesMoves[fsm] = makes;
        }

        return makes;
    }

    /// <summary>
    /// Whether a step of the scene host's puts a creature that every game runs by itself where the scene host's was as
    /// it took it: a step of a state machine that makes its moves (see MakesMoves), which no blow of another player
    /// than the scene host's set off there, all but where it is where its move puts it at a place of its own (see
    /// <see cref="KeepsItsPlace"/>). The game takes what a blow sets off at once, the stun that it counts too,
    /// as the knockback of the blow starts, which moves the creature only over the next moment: where it was there as
    /// it took such a step is where it was before the knockback. The game of the player who struck had the blow and
    /// its knockback a moment before the step came, and so had every other game, which that player's game told of the
    /// blow at once: each takes such a step where its creature is (USER 11-19 "击退本身已经不把怪拉回去了。晕倒凭什么在还
    /// 没击退的时候就把位置发过来？": a slash of the user's set off the knight's jump and its stun there before its
    /// knockback moved it, and the jump put it back 1.6 here, the user's log 20:41:34.606). A step that a blow of the
    /// scene host's own player set off comes to every other game before the blow does, whose knockback then goes from
    /// where the creature was put: taken where its creature was, a moment on in its move, the knight that a slash of
    /// the user's stunned in its run-up had run on past the user in Luminary's game, faced the other way there and was
    /// thrown off the other way into its stun, 10 units from the scene host's (Luminary's log of 11-19, 20:39:14.575;
    /// USER "第一次瘫痪，我是向右飞针的，但是我朋友那边boss是向左弹开虚弱的").
    /// </summary>
    private bool MovesAt(Fsm fsm, CoopSaveUpdate update) {
        return MakesMoves(fsm) && update.PartCount != OwnStruckPart;
    }

    /// <summary>
    /// Whether a step of the scene host's that puts a creature where the scene host's was (see MovesAt) leaves it where
    /// it is, and turned as it is, as its move puts it at a place of its own (see <see cref="PutsItselfOnItsWay"/>):
    /// not one that tells a member who has just come in where it is. The step of a catch or of a strike back, which
    /// takes the scene host's creature to where the other game had it, always puts it (review of 06e0bad).
    /// </summary>
    private bool KeepsItsPlace(Fsm fsm, CoopSaveUpdate update) {
        return update.PartCount != OwnToldPart && MovesAt(fsm, update) && PutsItselfOnItsWay(fsm, update.StateName);
    }

    /// <summary>
    /// Whether a step of the scene host's into a state takes a state machine, on the one way that it goes on from there
    /// by itself and through no state whose way on the others wait for, to an action that puts its creature at a place
    /// of its own, as a whole: a place in the values of its state machine, which come as in every game, or by where a
    /// part of its own is, which is where it is there too, and none that an action on the way found by where a player
    /// is or where it is itself (see <see cref="FindsAPlace"/>). The move takes it there from where it is here, as from
    /// where it was there: put where the scene host's was as the step came, a blade of the boss at the end of the
    /// second act, a moment behind, still on its way back to its hand here as its next strike came, jumped to where it
    /// was there, up to 9.7 away, and flew from there to where it strikes from, which its hand had set as there
    /// (Luminary's log of 11-22, 20:48:39.133; USER 11-23 "那指刃不就是只是要正常走下一招飞过去吗", "为啥要瞬移过去").
    /// </summary>
    private bool PutsItselfOnItsWay(Fsm fsm, string stateName) {
        if (!_putsItself.TryGetValue((fsm, stateName), out var puts)) {
            puts = PutsItselfAtItsOwnPlace(fsm, stateName);
            _putsItself[(fsm, stateName)] = puts;
        }

        return puts;
    }

    /// <summary>
    /// See <see cref="PutsItselfOnItsWay"/>: what an action on the way finds of where a player or the creature itself
    /// is, and what the actions after it work out from that, is followed to the first action that puts the creature
    /// at a place.
    /// </summary>
    private bool PutsItselfAtItsOwnPlace(Fsm fsm, string stateName) {
        var owner = fsm.GameObject;
        var found = new HashSet<string>();
        var seen = new HashSet<FsmState>();
        for (var state = fsm.GetState(stateName); state != null && seen.Count < MaxWayInSteps && seen.Add(state);) {
            foreach (var action in state.Actions) {
                if (!action.Enabled) {
                    continue;
                }

                if (PlaceItPutsItselfAt(action, owner) is { } place) {
                    return !place.Exists(found.Contains);
                }

                if (FindsAPlace(action, owner) || VariablesOf(action, false).Any(found.Contains)) {
                    found.UnionWith(VariablesOf(action, true));
                }
            }

            var ways = state.Transitions.Where(each => TakenByItself(state, each)).ToList();
            if (GoesByPlayers(state) || ChoosesByCount(state) || ways.Count != 1) {
                return false;
            }

            state = ways[0].ToFsmState ?? fsm.GetState(ways[0].ToState);
        }

        return false;
    }

    /// <summary>
    /// The variables of the place where an action puts the creature whose state machine runs it, at once or over a
    /// while, as a whole and not only along one side, or null for an action that does not: none for a place that the
    /// action is given as it is.
    /// </summary>
    private static List<string>? PlaceItPutsItselfAt(FsmStateAction action, GameObject owner) {
        var (thing, place) = action switch {
            HutongGames.PlayMaker.Actions.SetPosition set => (set.gameObject, WholePlace(set.vector, set.x, set.y)),
            HutongGames.PlayMaker.Actions.SetPosition2d set => (set.gameObject, WholePlace(set.vector, set.x, set.y)),
            HutongGames.PlayMaker.Actions.AnimatePositionTo animate => (animate.gameObject,
                animate.toValue.IsNone ? null : new NamedVariable[] { animate.toValue }),
            _ => ((FsmOwnerDefault?) null, (NamedVariable[]?) null)
        };
        if (thing == null || place == null || action.Fsm.GetOwnerDefaultTarget(thing) != owner) {
            return null;
        }

        return place.Where(each => each.UseVariable && each.Name.Length > 0).Select(each => each.Name).ToList();
    }

    /// <summary>
    /// The variables of a place that an action is given as a whole or along both sides, or null for one that it is
    /// given only along one side, or not at all.
    /// </summary>
    private static NamedVariable[]? WholePlace(NamedVariable whole, FsmFloat x, FsmFloat y) {
        return !whole.IsNone || !x.IsNone && !y.IsNone ? [whole, x, y] : null;
    }

    /// <summary>
    /// Whether what an action keeps may come of something that each game has elsewhere: where a player is, by an action
    /// that looks at the players (see <see cref="LooksAtPlayers"/>), or anything that it reads of the creature itself,
    /// like where it is or what a ray cast from it hits, or of a thing that is not its own. What it reads of a part of
    /// its own, like where the hand of a blade is, is as there (review of 06e0bad: only where a thing is counted).
    /// </summary>
    private static bool FindsAPlace(FsmStateAction action, GameObject owner) {
        if (LooksAtPlayers(action)) {
            return true;
        }

        var creature = OwnCreatures.Owning(owner);
        foreach (var field in action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public)) {
            if (field.Name.StartsWith("store", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            GameObject? thing;
            switch (field.GetValue(action)) {
                case FsmOwnerDefault each:
                    thing = action.Fsm.GetOwnerDefaultTarget(each);
                    break;
                case FsmGameObject { IsNone: false } each:
                    thing = each.Value;
                    break;
                default:
                    continue;
            }

            if (thing == null || thing == owner || OwnCreatures.Owning(thing) != creature) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The variables that an action is given, or of those only the ones that it keeps what it works out in: where it
    /// keeps a result, like storeResult, the one that it works on, like floatVariable, and every one of an action that
    /// reads something, like where a thing is, but for how deep a thing is, which is no place.
    /// </summary>
    private static IEnumerable<string> VariablesOf(FsmStateAction action, bool kept) {
        var type = action.GetType();
        var reads = type.Name.StartsWith("Get", StringComparison.Ordinal);
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public)) {
            if (field.GetValue(action) is not NamedVariable { UseVariable: true, Name.Length: > 0 } variable ||
                kept && field.Name.Equals("z", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (!kept || reads || field.Name.StartsWith("store", StringComparison.OrdinalIgnoreCase) ||
                field.Name.EndsWith("Result", StringComparison.OrdinalIgnoreCase) ||
                field.Name.EndsWith("Variable", StringComparison.OrdinalIgnoreCase)) {
                yield return variable.Name;
            }
        }
    }

    /// <summary>
    /// Whether a state machine of a creature that every game runs by itself gets by itself from the state it is in to a
    /// state that the scene host's went to: by ways that the states take by what they do themselves, as their actions
    /// finish or as one of them sends them on, through no state whose way on the others wait for (see IsWaitedFor) and
    /// into none that picks a part of the room. It is a moment behind, and goes the same way as fast.
    /// </summary>
    private bool ReachesByItself(Fsm fsm, string stateName) {
        if (fsm.ActiveState is not { } from || _heldOwnSteps.ContainsKey(fsm)) {
            return false;
        }

        var seen = new HashSet<FsmState> { from };
        var next = new Queue<FsmState>();
        next.Enqueue(from);
        while (next.Count > 0) {
            var state = next.Dequeue();
            if (GoesByPlayers(state) || ChoosesByCount(state)) {
                continue;
            }

            foreach (var transition in state.Transitions) {
                if ((transition.ToFsmState ?? fsm.GetState(transition.ToState)) is not { } to || PicksAPart(to) ||
                    !TakenByItself(state, transition)) {
                    continue;
                }

                if (to.Name == stateName) {
                    return true;
                }

                if (seen.Count < MaxWayInSteps && seen.Add(to)) {
                    next.Enqueue(to);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a state takes a way out of it by what it does itself: as its actions finish, as one of them sends it on
    /// that way, or as one of them picks one of its ways at random. Not as something touches it, which a trigger or a
    /// collision tells: what touches it may be a player, whom each game has elsewhere (review: a falling rock that
    /// broke on the scene host's player there fell on here).
    /// </summary>
    private static bool TakenByItself(FsmState state, FsmTransition transition) {
        if (transition.EventName == FsmEvent.Finished.Name) {
            return true;
        }

        foreach (var action in state.Actions) {
            var kind = action.GetType().Name;
            if (!action.Enabled || kind.StartsWith("Trigger", StringComparison.Ordinal) ||
                kind.StartsWith("Collision", StringComparison.Ordinal)) {
                continue;
            }

            if (action is HutongGames.PlayMaker.Actions.RandomEvent) {
                return true;
            }

            foreach (var field in EventFieldsOf(action.GetType())) {
                var value = field.GetValue(action);
                if (value is FsmEvent sent && sent.Name == transition.EventName ||
                    value is FsmEvent[] events && Array.Exists(events, each => each?.Name == transition.EventName)) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a state chooses its way on by a count that each game keeps for itself: one of its actions decides where
    /// it goes by a number that a state of its state machine adds to once as it starts (see HitCounters.CountedBy),
    /// like the blows that the boss at the end of the second act took since it was last stunned. Each game takes in
    /// the blows of every player, its own player's at once and the others' as they come, and counts some of them in
    /// another state than the others, so that a count is full at another blow in each game: the scene host's is the
    /// one (review: the striker's game stunned the boss at a blow of its own, and played the stun again as the scene
    /// host's step came). Its health is no such count: what it does as it has none goes by the scene host's word that
    /// it has none (see CoopSave.OwnHealth).
    /// </summary>
    private bool ChoosesByCount(FsmState state) {
        var fsm = state.Fsm;
        if (!_countedValues.TryGetValue(fsm, out var counted)) {
            counted = [];
            foreach (var each in fsm.States) {
                foreach (var action in each.Actions) {
                    if (HitCounters.CountedBy(action) is { Length: > 0 } name) {
                        counted.Add(name);
                    }
                }
            }

            _countedValues[fsm] = counted;
        }

        foreach (var action in state.Actions) {
            if (action.Enabled && Entity.Action.EntityFsmActions.ChoosesWhereItGoes(action) &&
                Entity.Action.EntityFsmActions.ValueVariablesOf(action).Any(counted.Contains)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a part of a creature that every game runs by itself takes the steps of a state machine of its own as the
    /// creature does (see <see cref="OnOwnCreatureStep"/>): one that looks at a player, like a hand of the boss at the
    /// end of the second act, which aims its blades where the player it goes after stands, and might take the other
    /// side of them in another game, which has them a little elsewhere. The rest of them, which roll the same in every
    /// game and do what the creature tells them, go as the game has them.
    /// </summary>
    private static bool TakesOwnSteps(PlayMakerFSM machine) {
        return OwnCreatures.IsPart(machine.gameObject) && SharedDice.Aims(machine);
    }

    /// <summary>
    /// Lets the creatures that wait here for a step of the scene host's go on once this game runs the room, as the
    /// scene host left it: each takes its state again, which decides once more where it goes. What it waited at had
    /// told it once, like a roll of its next move, and would not again.
    /// </summary>
    private void UpdateOwnCreatureHolds() {
        if (_heldOwnSteps.Count == 0 || OwnCreatures.SceneRole?.Invoke() != true) {
            return;
        }

        foreach (var pair in _heldOwnSteps.ToList()) {
            var fsm = pair.Key;
            if (fsm.FsmComponent == null || fsm.ActiveStateName != pair.Value) {
                continue;
            }

            Logger.Info(
                $"[Own creatures] '{fsm.FsmComponent.gameObject.name}' takes '{pair.Value}' of '{fsm.Name}' again by " +
                "itself, as this game runs the room now"
            );
            fsm.SetStateAsItRuns(pair.Value);
        }

        _heldOwnSteps.Clear();
    }

    /// <summary>
    /// The event by which a state machine goes from the state it is in to the given state, the way the game has it:
    /// straight, or through a state that does its part and finishes into it, like the step that stops a crab's walk
    /// before its song. A way straight there goes first: the map seller's throw back finishes into her leaving, which
    /// her look at the player she goes after takes her to straight, and a game that went through it threw a ring that
    /// the scene host's never did (the user's log of 11-21). Null when no way out of its state, nor out of every state,
    /// leads there.
    /// </summary>
    private static FsmEvent? WayInto(Fsm fsm, string target) {
        // A way out that leads nowhere, which the game's state machines have, is no way there
        var ways = fsm.ActiveState.Transitions.Concat(fsm.GlobalTransitions).ToList();
        foreach (var transition in ways) {
            if (transition.ToState == target && fsm.GetState(target) != null) {
                return transition.FsmEvent;
            }
        }

        foreach (var transition in ways) {
            if (fsm.GetState(transition.ToState) is { Transitions: [var only] } &&
                only.EventName == FsmEvent.Finished.Name && only.ToState == target) {
                return transition.FsmEvent;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a step of the scene host's of a state machine comes from before a blow of this game's player that set it
    /// off on a way of its own here (see _awaitedBlows): until the scene host's game answers the blow with a step that
    /// it set off there, or tells that it is through with the blow (see <see cref="TakeStrikeDone"/>), or for
    /// <see cref="LocalBlowAnswerWait"/> where neither comes, after which the newest of them is taken (see
    /// <see cref="UpdateAwaitedBlows"/>), or a later one as it comes. Such a step is let go of where a blow there can
    /// turn the creature from its state into what the blow set off here (see <see cref="CanTurnInto"/>), as that is
    /// this game's to play (USER 11-17 "本地化就是为了让自己的怪是活的"): the boss at the end of the second act, killed
    /// by the user's blow here, rose and roared once more, moved 14 units, and two of its blades set off to strike, by
    /// steps of its fight that Luminary's game took in the 0.36 s before it had the blow (the user's log of 11-21,
    /// 21:52:36.194 to 36.656).
    /// </summary>
    private bool AwaitsLocalBlow(Fsm fsm) {
        if (!_awaitedBlows.TryGetValue(fsm, out var awaited)) {
            return false;
        }

        if (Time.unscaledTime < awaited.Until) {
            return true;
        }

        _awaitedBlows.Remove(fsm);
        _stepsBeforeBlow.Remove(fsm);
        return false;
    }

    /// <summary>
    /// Whether anything, a blow among them, can turn a state machine from a state into another at once: by a way on
    /// from that state or from any (see <see cref="_awaitedBlows"/>). The map seller goes into 'Stun Damage' only from
    /// 'Stunned', and into 'Defeat Start' from any state.
    /// </summary>
    private static bool CanTurnInto(Fsm fsm, string from, string into) {
        return fsm.GetState(from).Transitions.Concat(fsm.GlobalTransitions).Any(way => way.ToState == into);
    }

    /// <summary>
    /// Has each state machine whose wait for the answer to a blow of this game's player ran out with no answer take the
    /// newest step of the scene host's that was let go of meanwhile (see <see cref="_stepsBeforeBlow"/>).
    /// </summary>
    private void UpdateAwaitedBlows() {
        if (_awaitedBlows.Count == 0) {
            return;
        }

        var now = Time.unscaledTime;
        foreach (var pair in _awaitedBlows.ToList()) {
            if (now < pair.Value.Until) {
                continue;
            }

            _awaitedBlows.Remove(pair.Key);
            if (!_stepsBeforeBlow.Remove(pair.Key, out var newest)) {
                continue;
            }

            Logger.Info(
                $"[Own creatures] '{newest.Step.ObjectPath}' had no answer of {newest.Player.Username}'s game to the " +
                $"blow of this game's player in {LocalBlowAnswerWait:F0}s, and takes the newest step of " +
                $"'{pair.Key.Name}' that it let go of meanwhile, '{newest.Step.StateName}'"
            );
            OnOwnCreature(newest.Player, newest.Step);
        }
    }

    /// <summary>
    /// Tells the members in the room that this game, the scene host's, is through with a blow or touch of another
    /// player on a creature that every game runs by itself, or on a part of it, after the steps that it set off here,
    /// if any (see <see cref="OwnStrikeDonePart"/>). A blow that came after the creature had left what it set off in
    /// the game of the player who struck, like a stun that a blow of this game's player had ended, set off nothing
    /// here, and that game waited for an answer for a second, its creature held in the stun: the map seller that the
    /// user struck at 16:42:26.849, which Luminary's blow had woken at 26.899 there, rose only at 27.850 in the user's
    /// game and leapt 14 units, and the boss at the end of the second act, struck at 16:32:00.807, lay there until
    /// 01.807, while its hands struck from both sides by Luminary's steps (the user's log of 11-22; USER
    /// "第一次晕的时候针出了从两边射的招式", "那为啥不告诉我醒了啊", "可是为什么怪本身会多晕一秒").
    /// </summary>
    /// <param name="struck">What the blow or touch reached.</param>
    /// <param name="striker">The player who struck.</param>
    /// <param name="strike">The number of the strike in the game of that player.</param>
    private void TellStrikeDone(GameObject struck, ushort striker, uint strike) {
        if (!OwnCreatures.RunsHere(struck) || OwnCreatures.Owning(struck) is not { } creature) {
            return;
        }

        SendToRoomMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.OwnCreature,
            Scene = creature.scene.name,
            ObjectPath = OwnCreatures.PlaceOf(creature),
            PartCount = OwnStrikeDonePart,
            Key = striker,
            Sequence = strike
        });
    }

    /// <summary>
    /// The scene host's game is through with a blow of a player on a creature that every game runs by itself (see
    /// <see cref="TellStrikeDone"/>), after the steps that it set off there, which came before. If the blow was this
    /// game's player's, each state machine that still waits for an answer to that blow, by its number, had none, and
    /// takes the newest step of the scene host's that it let go of meanwhile, as when the wait runs out (see
    /// <see cref="UpdateAwaitedBlows"/>). One that waits for a later blow waits on: the word of the first of two blows
    /// let go of the wait for the second, and the step of that game from before the second played over what it set
    /// off here (review of f2a55f9).
    /// </summary>
    private void TakeStrikeDone(ClientPlayerData player, CoopSaveUpdate update) {
        if (_awaitedBlows.Count == 0 || !IsLocalPlayersBlow(player, update)) {
            return;
        }

        foreach (var pair in _awaitedBlows.ToList()) {
            if (pair.Value.Strike != update.Sequence) {
                continue;
            }

            var fsm = pair.Key;
            _awaitedBlows.Remove(fsm);
            if (!_stepsBeforeBlow.Remove(fsm, out var newest)) {
                continue;
            }

            Logger.Info(
                $"[Own creatures] '{newest.Step.ObjectPath}' had all that {player.Username}'s game set off by blow " +
                $"{update.Sequence} of this game's player, and takes the newest step of '{fsm.Name}' that it let go " +
                $"of meanwhile, '{newest.Step.StateName}'"
            );
            OnOwnCreature(newest.Player, newest.Step);
        }
    }

    /// <summary>
    /// Keeps a step of the scene host's that a state machine gets to here by itself from where it is, until it does
    /// (see <see cref="ReachKeptStep"/>), to be taken as it came before a later step that it does not get to by itself
    /// (see <see cref="TakeKeptSteps"/>).
    /// </summary>
    private void KeepOwnStep(Fsm fsm, ClientPlayerData player, CoopSaveUpdate update) {
        if (!_keptOwnSteps.TryGetValue(fsm, out var kept)) {
            kept = [];
            _keptOwnSteps[fsm] = kept;
        }

        kept.Add((player, update, Time.unscaledTime));
    }

    /// <summary>
    /// A state machine got by itself to the state of a step of the scene host's that it kept, which goes, with those
    /// before it: as long after the step came as it is behind there (see <see cref="TellBehind"/>).
    /// </summary>
    private void ReachKeptStep(Fsm fsm, string stateName) {
        if (!_keptOwnSteps.TryGetValue(fsm, out var kept)) {
            return;
        }

        var index = kept.FindIndex(each => each.Step.StateName == stateName);
        if (index < 0) {
            return;
        }

        var came = kept[index].Came;
        kept.RemoveRange(0, index + 1);
        if (kept.Count == 0) {
            _keptOwnSteps.Remove(fsm);
        }

        // Not while it is set onto them, as it goes on by itself into the state of the next on the way (review of
        // 9ad6c79: that step, which came long before, told it as far behind as it had just caught up)
        if (!_takingKeptSteps) {
            TellBehind(fsm, Time.unscaledTime - came);
        }
    }

    /// <summary>
    /// Takes the steps of the scene host's that a state machine kept, as it was to get to them by itself and has not
    /// (see <see cref="KeepOwnStep"/>), one after another as they came, before a step that it does not get to by
    /// itself, once it does not get to them by itself any more, or goes on a way of its own from a blow or a hurt
    /// here: while it is on its way to them, it plays its move through, and that step waits (see
    /// <see cref="KeepsForAfterMove"/>). Many come at once after a stall of the network, and the state machine, a
    /// moment behind, was set straight to the later one where it goes round in a part of it with that one (see
    /// WalkInto), past the states that it was to go through by itself: the map seller went past the one where she sets
    /// herself to go home, and waited for good for a step of her fight in a game whose fight was over (the user's log
    /// of 11-21, 21:42:03).
    /// </summary>
    private void TakeKeptSteps(Fsm fsm) {
        if (_takingKeptSteps || !_keptOwnSteps.TryGetValue(fsm, out var kept)) {
            return;
        }

        Logger.Info(
            $"[Own creatures] '{fsm.GameObject.name}' takes {kept.Count} step(s) of '{fsm.Name}' of the scene host's " +
            "that it was to get to by itself, as they came: " +
            string.Join(", ", kept.Select(each => $"'{each.Step.StateName}'"))
        );

        // Set onto the latest of them, it is behind there no more (see TellBehind)
        TellBehind(fsm, 0f);
        _takingKeptSteps = true;
        try {
            // One that is taken may take the state machine on by itself into the state of the next, which then goes
            // (see ReachKeptStep; review of a0d1558: it was taken again)
            while (_keptOwnSteps.TryGetValue(fsm, out kept)) {
                var (player, step, _) = kept[0];
                kept.RemoveAt(0);
                if (kept.Count == 0) {
                    _keptOwnSteps.Remove(fsm);
                }

                OnOwnCreature(player, step);
            }
        } finally {
            _takingKeptSteps = false;
        }
    }

    /// <summary>
    /// Keeps a step of the scene host's that a state machine does not get to by itself while it is still on its way
    /// to steps of the scene host's before it that it gets to by itself (see <see cref="KeepOwnStep"/>), with every
    /// step after it, as they came, until it is through its move here, for <see cref="MoveThroughWait"/> at the most
    /// (see <see cref="UpdateStepsAfterMove"/>), and every blow of another player on its creature too (see
    /// CoopHits.WaitsForAMove). Taken
    /// as it came, after the steps before it, all at once, the map seller, still in the air in her stomp here a moment
    /// behind, was put down where the scene host's had landed, 6.7 away, and set into her next move (Luminary's log of
    /// 11-22, 20:39:03.332; USER 11-23 "都说了多少遍了让客机一招演完，怎么还会有沙克拉被直接拉到地上的道理？"). Not one
    /// on a way of its own from a blow or a hurt here, nor one that does not get to the first of the steps before it by
    /// itself any more, whose steps are taken as they came at once (see <see cref="TakeKeptSteps"/>). Whether it keeps
    /// the step.
    /// </summary>
    private bool KeepsForAfterMove(ClientPlayerData player, Fsm fsm, CoopSaveUpdate update) {
        if (_takingKeptSteps || update.PartCount == OwnToldPart || _ownWays.Contains(fsm) ||
            !_keptOwnSteps.TryGetValue(fsm, out var kept) || !ReachesByItself(fsm, kept[0].Step.StateName)) {
            return false;
        }

        _stepsAfterMove[fsm] = (Time.time, [(player, update)]);
        Logger.Info(
            $"[Own creatures] '{update.ObjectPath}' went to '{update.StateName}' of '{fsm.Name}' in " +
            $"{player.Username}'s game, which it does not get to by itself, while it is still on its way here to " +
            $"'{kept[0].Step.StateName}', a step there before it: it plays its move through first"
        );
        return true;
    }

    /// <summary>
    /// Has each state machine that plays its move through here take the steps of the scene host's that came meanwhile
    /// (see <see cref="KeepsForAfterMove"/>), as they came, a frame after it got by itself to the last of the steps
    /// before them, outside of the step of its state machine. One that went on a way of its own from a blow or a hurt
    /// here meanwhile, or that does not get to the first of those by itself any more, or not within
    /// <see cref="MoveThroughWait"/>, takes them now as well, after those, as before (see <see cref="TakeKeptSteps"/>).
    /// Where one of them is a step that it gets to by itself again, the rest wait once more.
    /// </summary>
    private void UpdateStepsAfterMove() {
        if (_stepsAfterMove.Count == 0) {
            return;
        }

        foreach (var pair in _stepsAfterMove.ToList()) {
            var fsm = pair.Key;
            if (fsm.FsmComponent == null) {
                _stepsAfterMove.Remove(fsm);
                continue;
            }

            var played = Time.time - pair.Value.Since;
            var onItsWay = _keptOwnSteps.TryGetValue(fsm, out var kept);
            if (onItsWay && !_ownWays.Contains(fsm) && played < MoveThroughWait &&
                ReachesByItself(fsm, kept![0].Step.StateName)) {
                continue;
            }

            _stepsAfterMove.Remove(fsm);
            var steps = pair.Value.Steps;
            var why = !onItsWay ? $"is through its move in '{fsm.Name}' here after {played:F2}s"
                : _ownWays.Contains(fsm) ? $"went on a way of its own in '{fsm.Name}' here"
                : played >= MoveThroughWait ? $"did not get to '{kept![0].Step.StateName}' by itself in time"
                : $"does not get to '{kept![0].Step.StateName}' by itself any more";
            Logger.Info(
                $"[Own creatures] '{steps[0].Step.ObjectPath}' {why}, and takes the {steps.Count} step(s) of the " +
                "scene host's that came meanwhile, as they came: " +
                string.Join(", ", steps.Select(each => $"'{each.Step.StateName}'"))
            );
            _takingStepsAfterMove = true;
            try {
                for (var i = 0; i < steps.Count; i++) {
                    OnOwnCreature(steps[i].Player, steps[i].Step);
                    if (_stepsAfterMove.TryGetValue(fsm, out var again)) {
                        again.Steps.AddRange(steps.Skip(i + 1));
                        break;
                    }
                }
            } finally {
                _takingStepsAfterMove = false;
            }
        }
    }

    /// <summary>
    /// Whether a creature that every game runs by itself, which an object is or is a part of, plays a move through here
    /// in any of its state machines before steps of the scene host's that came meanwhile (see
    /// <see cref="KeepsForAfterMove"/>; CoopHits.WaitsForAMove).
    /// </summary>
    private bool PlaysMoveThrough(GameObject struck) {
        if (_stepsAfterMove.Count == 0 || OwnCreatures.Owning(struck) is not { } creature) {
            return false;
        }

        foreach (var fsm in _stepsAfterMove.Keys) {
            if (fsm.GameObject != null && OwnCreatures.Owning(fsm.GameObject) == creature) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tells the scene host's game how far behind it a state machine of a creature that every game runs by itself is
    /// here, as it gets to a step of that game's: as long after the step came as it got there by itself, and not at
    /// all where it waited for the step, had taken it already or was set onto it. A hurt of this game's player stops
    /// this game for a moment, as the game has it, and a step of the scene host's comes late at times over the
    /// network: the creature here is behind by as much from then on, as it plays each move through, and nothing here
    /// catches it up. The scene host's creature waits that long, a little at a time, before steps that the others wait
    /// for (see WaitsBehind; USER 11-23 "允许场景主机等一会儿", "那就发消息吧": the map seller in the user's spar with
    /// Luminary on 11-22, 20:38:47 to 20:39:51, through 12 hurts of Luminary's there and 11 of the user's steps that
    /// came over 0.1 s late, was set onto the user's steps 77 times as it was behind). Told only as it gets further
    /// behind than it was told last, by <see cref="BehindStep"/> at least, and as it is behind no more once it was
    /// told behind, with none, so that the scene host's game waits no longer for it (review of 9ad6c79: it waited
    /// still for a game that had caught up by itself).
    /// </summary>
    private void TellBehind(Fsm fsm, float behind) {
        var told = _toldBehind.TryGetValue(fsm, out var last) ? last : 0f;
        var caughtUp = behind < BehindStep && told >= BehindStep;
        if (!caughtUp && behind < told + BehindStep) {
            if (behind < told) {
                _toldBehind[fsm] = behind;
            }

            return;
        }

        _toldBehind[fsm] = behind;
        var machine = fsm.FsmComponent;
        var word = new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.OwnCreature,
            Scene = machine.gameObject.scene.name,
            ObjectPath = OwnCreatures.PlaceOf(machine.gameObject),
            FsmName = machine.FsmName,
            StateName = fsm.ActiveStateName,
            PartCount = OwnBehindPart,
            Values = [caughtUp ? 0f : behind - told]
        };
        Logger.Info(
            caughtUp
                ? $"[Own creatures] '{word.ObjectPath}' is behind the scene host's steps of '{fsm.Name}' no more, " +
                  $"{behind:F2}s, which the scene host's game is told"
                : $"[Own creatures] '{word.ObjectPath}' gets to a step of '{fsm.Name}' of the scene host's " +
                  $"{behind:F2}s after it came, {behind - told:F2}s later than told last, which the scene host's " +
                  "game is told"
        );
        SendToRoomMembers(word);
    }

    /// <summary>
    /// Takes the word of a game that follows this one that a state machine of a creature that every game runs by itself
    /// is further behind there (see <see cref="TellBehind"/>): the scene host's creature waits as much longer before
    /// its next steps that the others wait for (see <see cref="WaitsBehind"/>), up to as long as the creature there
    /// plays its moves through, after which it is set onto the steps there (see <see cref="MoveThroughWait"/>). One
    /// that tells none is behind no more, and is waited for no longer.
    /// </summary>
    private void TakeBehind(ClientPlayerData player, GameObject creature, CoopSaveUpdate update) {
        if (IsSceneHost?.Invoke() != true) {
            return;
        }

        var fsm = PlayMakerFSM.FindFsmOnGameObject(creature, update.FsmName).Fsm;
        if (!_owedWaits.TryGetValue(fsm, out var owed)) {
            owed = new Dictionary<ushort, float>();
            _owedWaits[fsm] = owed;
        }

        var further = update.Values[0];
        var waits = further > 0f
            ? Mathf.Min((owed.TryGetValue(player.Id, out var was) ? was : 0f) + further, MoveThroughWait)
            : 0f;
        owed[player.Id] = waits;
        Logger.Info(
            further > 0f
                ? $"[Own creatures] '{update.ObjectPath}' is {further:F2}s further behind in '{fsm.Name}' in " +
                  $"{player.Username}'s game, and waits {waits:F2}s here before its next steps that the others wait " +
                  "for"
                : $"[Own creatures] '{update.ObjectPath}' is behind in '{fsm.Name}' in {player.Username}'s game no " +
                  "more, and waits for it no longer here"
        );
    }

    /// <summary>
    /// Whether, in the scene host's game, a state machine of a creature that every game runs by itself waits a moment
    /// before a step that the others wait for, out of a state that goes by where the players are, while a game that
    /// follows this one is behind there (see <see cref="TakeBehind"/>): the games that wait for the step take it as
    /// late, and the one behind, which gets there late, catches up, as every game plays each move through. Only as the
    /// creature stands still, not in a jump, a dash or a flight, and not at a step that a blow, a hurt or another state
    /// machine set off, which plays at once; a part goes as its creature tells it. As long as the game furthest behind
    /// of the members in the room is, <see cref="MostWaitAtStep"/> at the most, and the rest at the next such step. The
    /// chosen step goes on once the time is up (see <see cref="UpdateWaitsBehind"/>), and every other way of its own
    /// out of the state waits until then as well: a state whose choice stopped its start finishes in the next frame,
    /// and went on into the way that it takes when it chooses none (review of 9ad6c79). What a blow, a hurt or another
    /// state machine sets off meanwhile goes at once, and the step waits no more.
    /// </summary>
    private bool WaitsBehind(Fsm fsm, FsmState toState, GameObject creature, bool waitedFor, bool hit) {
        if (_givingBack) {
            return false;
        }

        var setOff = hit || CoopHits.IsMakingLocalStrike || CoopHits.IsReplayingPartnersStrike ||
                     CoopHits.TakesAnothersBlow || Fsm.EventData?.SentByFsm is { } sender && sender != fsm;
        if (_waitsBehind.ContainsKey(fsm)) {
            if (setOff) {
                _waitsBehind.Remove(fsm);
                return false;
            }

            return true;
        }

        if (setOff || !waitedFor || !_owedWaits.TryGetValue(fsm, out var owed) || IsSceneHost?.Invoke() != true ||
            fsm.ActiveState is not { } from || !GoesByPlayers(from) || OwnCreatures.IsPart(creature) ||
            creature.TryGetComponent<Rigidbody2D>(out var body) &&
            body.linearVelocity.sqrMagnitude > StillSpeed * StillSpeed) {
            return false;
        }

        var here = GetCheckedMembers().Where(member => member.IsInLocalScene).Select(member => member.Id).ToHashSet();
        var most = owed.Where(each => here.Contains(each.Key)).Select(each => each.Value).DefaultIfEmpty().Max();
        if (most <= 0f) {
            return false;
        }

        var wait = Mathf.Min(most, MostWaitAtStep);
        foreach (var id in owed.Keys.ToList()) {
            owed[id] = Mathf.Max(0f, owed[id] - wait);
        }

        _waitsBehind[fsm] = (from.Name, toState.Name, Time.unscaledTime + wait);
        Logger.Info(
            $"[Own creatures] '{OwnCreatures.PlaceOf(creature)}' waits {wait:F2}s in '{from.Name}' of '{fsm.Name}' " +
            $"before it goes to '{toState.Name}', as a game that follows this one is behind there"
        );
        return true;
    }

    /// <summary>
    /// Has each state machine that waited before a step in the scene host's game (see <see cref="WaitsBehind"/>) take
    /// the step once the time is up, there and then, as its own switch would take it: the state that chose it may
    /// choose only once. Left to it as its switch to take as it next runs, an event that came to it before, like the
    /// hit of a blow, took it from inside the blow, as one that the blow set off (review of 9ad6c79). One that went
    /// elsewhere meanwhile has it no more.
    /// </summary>
    private void UpdateWaitsBehind() {
        if (_waitsBehind.Count == 0) {
            return;
        }

        foreach (var pair in _waitsBehind.ToList()) {
            var (from, to, until) = pair.Value;
            if (Time.unscaledTime < until) {
                continue;
            }

            var fsm = pair.Key;
            _waitsBehind.Remove(fsm);
            if (fsm.FsmComponent == null || fsm.ActiveStateName != from) {
                continue;
            }

            _givingBack = true;
            try {
                fsm.SetStateAsItRuns(to);
            } finally {
                _givingBack = false;
            }
        }
    }

    /// <summary>
    /// The first step of a state machine here, of those that no step of the scene host's answered yet, that went to the
    /// state of a step of the scene host's creature after as many rolls, which is that step, or null if it took none.
    /// It is answered, and so are the steps before it, which the scene host's did not take: a later step of the scene
    /// host's that goes to the same state after as many rolls, like the next of two that a state machine with no
    /// rolls goes between, is not taken for it. One that it took on a way of its own from a blow is found by its state
    /// alone (see _ownWays). With none found, none of them is on the way of the scene host's.
    /// </summary>
    /// <param name="fsm">The state machine.</param>
    /// <param name="update">The step of the scene host's.</param>
    /// <param name="newest">Whether the step is the last that it took, the one it is at.</param>
    private OwnStep? TakeOwnStep(Fsm fsm, CoopSaveUpdate update, out bool newest) {
        newest = false;
        if (!_ownSteps.TryGetValue(fsm, out var steps)) {
            return null;
        }

        var index = OwnStepIndex(steps, update);
        if (index < 0) {
            steps.Clear();
            return null;
        }

        var took = steps[index];
        newest = index == steps.Count - 1;
        steps.RemoveRange(0, index + 1);
        return took;
    }

    /// <summary>
    /// Where among the steps of a state machine here that no step of the scene host's answered yet the one is that a
    /// step of the scene host's is (see <see cref="TakeOwnStep"/>), or -1.
    /// </summary>
    private static int OwnStepIndex(List<OwnStep> steps, CoopSaveUpdate update) {
        var index = steps.FindIndex(
            step => step.State == update.StateName && step.Counts.SequenceEqual(update.Amounts)
        );

        // One that it took on a way of its own from a blow came at another moment of its way here, after other rolls
        // than there: it is the first of the same state (USER 11-17 "房主那边如果自己还没打到，客机打到了就演，自己已经
        // 打到了那就已经演了", "房主传过来同理")
        return index >= 0 ? index : steps.FindIndex(step => step.OwnWay && step.State == update.StateName);
    }

    /// <summary>
    /// Whether a state machine here took the step that a step of the scene host's is, which no step of the scene
    /// host's answered yet (see <see cref="TakeOwnStep"/>), leaving the steps as they are.
    /// </summary>
    private bool HasOwnStep(Fsm fsm, CoopSaveUpdate update) {
        return _ownSteps.TryGetValue(fsm, out var steps) && OwnStepIndex(steps, update) >= 0;
    }

    /// <summary>
    /// Adds to the counts of rolls of the kept steps of a creature that every game runs by itself that it took on a way
    /// of its own from a blow (see TakeOwnStep), as it was just given as many more, so that the scene host's game is
    /// ahead of them by no more.
    /// </summary>
    private void AddToOwnWays(GameObject creature, List<int> more) {
        foreach (var machine in creature.GetComponents<PlayMakerFSM>()) {
            if (!_ownSteps.TryGetValue(machine.Fsm, out var steps)) {
                continue;
            }

            for (var i = 0; i < steps.Count; i++) {
                if (steps[i].OwnWay) {
                    steps[i] = steps[i] with { Counts = SharedDice.AddCounts(steps[i].Counts, more) };
                }
            }
        }
    }

    /// <summary>
    /// Moves where the kept steps of a creature that every game runs by itself say it stood by as much as it was just
    /// moved, so that a step of the scene host's that it took before is not made up for once more.
    /// </summary>
    private void MoveOwnSteps(GameObject creature, Vector3 by) {
        foreach (var machine in creature.GetComponents<PlayMakerFSM>()) {
            if (!_ownSteps.TryGetValue(machine.Fsm, out var steps)) {
                continue;
            }

            for (var i = 0; i < steps.Count; i++) {
                steps[i] = steps[i] with { Place = steps[i].Place + by };
            }
        }
    }

    /// <summary>
    /// A step of a state machine of a creature that every game runs by itself: the state it went to, how many times
    /// its actions had rolled before (see SharedDice.RollCounts), where the creature was, the way it faced, and whether
    /// it took it on a way of its own from a blow (see TakeOwnStep).
    /// </summary>
    private readonly record struct OwnStep(
        string State, List<int> Counts, Vector3 Place, float Facing, bool OwnWay = false
    );
}
