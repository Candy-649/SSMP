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
    /// CoopSave.OwnStrikeBacks): the step of the knight's parry stood still the game of a third player (review). The
    /// number of a blow of this game's player is in <see cref="CoopSaveUpdate.Key"/> (see CoopSave.OwnBlowSteps).
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
    /// whether this game's player has come into place, and those that look at whether a player sees the creature or
    /// at how many things are in a zone that a player comes into, like a creature that waits to be seen, or until no
    /// player is near to come out again: each game's player sees it and comes near at other moments (USER 11-24 "可以
    /// 把更多的怪本地化吗"). Facing one leaves where it goes as it is, and a song a creature hears in every game (see
    /// SharedDice.RollForSong).
    /// </summary>
    private static readonly HashSet<string> PlayerWayActionTypes = new(
        Entity.Action.ActionRegistry.TargetedFsmActionTypes
            .Where(type => !type.StartsWith("Face") && type is not ("Tk2DAnimationByDirection" or "TakeSilk"))
            .Concat([
                "CheckAlertRangeByName", "WaitForHeroInPosition", "CheckOutOfCamera", "GameObjectIsVisible",
                "CheckTrackTriggerCount", "CheckTrackTriggerCountV2"
            ])
    );

    /// <summary>
    /// The variables that the actions by which a state goes by where the players are store, for each state machine of
    /// a creature that every game runs by itself whose steps this game has looked at (see GoesByPlayers).
    /// </summary>
    private readonly Dictionary<Fsm, HashSet<string>> _playerWayValues = new();

    /// <summary>
    /// The variables that hold a value that each game has for itself, for each state machine of a creature that every
    /// game runs by itself whose steps this game has looked at (see ChoosesByOwnValue).
    /// </summary>
    private readonly Dictionary<Fsm, HashSet<string>> _ownValues = new();

    /// <summary>
    /// The variables that the machines of each loaded room that run by the scene host's word, and the creatures of it
    /// that every game runs by itself, set on other state machines, by the name of the state machine and of the
    /// variable (see MachineSetValues).
    /// </summary>
    private readonly Dictionary<string, HashSet<(string Fsm, string Variable)>> _machineSetValues = new();

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
    /// Whether each state of a creature that every game runs by itself, or of a part of it, whose steps this game has
    /// looked at puts something where the player that the creature goes after is (see PlacesByPlayer).
    /// </summary>
    private readonly Dictionary<FsmState, bool> _placesByPlayer = new();

    /// <summary>
    /// The state machines of creatures that every game runs by itself that took a step here that a blow set off, of
    /// this game's player or another's as it came, or a hurt of a player (see CoopSave.OwnHits), and go on from there
    /// by themselves until they next wait for a step of the scene host's (see TakeOwnStep).
    /// </summary>
    private readonly HashSet<Fsm> _ownWays = new();

    /// <summary>
    /// The steps of the scene host's, with whose game they came from, that a state machine of a creature that every
    /// game runs by itself gets to here by itself from where it is, the first first, as long as it has not got to
    /// them (see <see cref="KeepOwnStep"/>).
    /// </summary>
    private readonly Dictionary<Fsm, List<(ClientPlayerData Player, CoopSaveUpdate Step)>> _keptOwnSteps = new();

    /// <summary>
    /// Whether this game takes the steps of the scene host's that a state machine kept (see
    /// <see cref="TakeKeptSteps"/>).
    /// </summary>
    private bool _takingKeptSteps;

    /// <summary>
    /// The steps of the scene host's, with whose game they came from, that came for a state machine of a creature that
    /// every game runs by itself while it was still on its way to steps of the scene host's that it gets to by itself
    /// (see <see cref="_keptOwnSteps"/>), from the first one that it does not get to by itself on, as they came, with
    /// what that game told of what its steps did there, and every step after them of the creature's other state
    /// machines and of its parts: it plays its move through, and takes them then (see
    /// <see cref="KeepsForAfterMove"/>). A creature has them for one of its state machines at a time.
    /// </summary>
    private readonly Dictionary<Fsm, List<(ClientPlayerData Player, CoopSaveUpdate Step)>> _stepsAfterMove = new();

    /// <summary>
    /// How long after a step of the scene host's came here, in seconds of real time, a state machine of a creature
    /// that every game runs by itself that gets to it by itself may still be on its way to it: later than that, the
    /// creature is set onto the scene host's latest (see <see cref="UpdateKeptSteps"/>). A move that the game plays
    /// by itself ends within moments, and one that has not by then waits in a state for what does not come here, like
    /// a place that it never gets to (review of 06e0bad), and a game that stopped for a moment is that much behind
    /// from then on (USER 11-23 "迟了1秒就直接跳到房主传过来最新的状态").
    /// </summary>
    private const float MoveThroughWait = 1f;

    /// <summary>
    /// Whether this game takes the steps of the scene host's that a state machine kept for after its move (see
    /// <see cref="UpdateStepsAfterMove"/>).
    /// </summary>
    private bool _takingStepsAfterMove;

    /// <summary>
    /// Whether this game sets a creature onto the scene host's latest steps, a second behind them (see
    /// <see cref="UpdateKeptSteps"/>): none of them is kept to be got to by itself, nor waits for a move.
    /// </summary>
    private bool _settingOntoLatest;

    /// <summary>
    /// While this game sets a creature onto the scene host's latest steps: the last of them of each state machine of
    /// the creature and of its parts, and apart from it its last pick, which play as the game has them (see
    /// <see cref="EnterQuietOnTheWay"/>).
    /// </summary>
    private readonly HashSet<CoopSaveUpdate> _lastSteps = new();

    /// <summary>
    /// The state machines that this game set on their way into the last of the scene host's latest steps (see
    /// <see cref="_lastSteps"/>), or that got to its state by themselves, what they go to from there on playing as
    /// the game has it.
    /// </summary>
    private readonly HashSet<Fsm> _atLastStep = new();

    /// <summary>
    /// How many states that play as the game has them, one inside another, this game takes state machines into as it
    /// sets them through steps of the scene host's one after another (see <see cref="EnterQuietOnTheWay"/>): what
    /// such a state sets off in other state machines as it starts plays so too.
    /// </summary>
    private int _playingAsIs;

    /// <summary>
    /// The kinds of actions that play a sound once, which is heard over every other one, or shake the camera, that a
    /// state leaves out where this game sets a state machine through it on its way to a later step of the scene
    /// host's (see <see cref="EnterQuietOnTheWay"/>). Not those that play a sound on a source of the creature's own,
    /// which the next play of it starts over, nor a loop, which goes on after the state.
    /// </summary>
    private static readonly HashSet<string> NoisesOnTheWay = [
        "PlayAudioEvent", "PlayAudioEventV2", "PlayAudioEventRandom", "PlayAudioEventRandomV2",
        "PlayAudioEventRandomBool", "PlayRandomAudioClipTable", "PlayRandomAudioClipTableV2",
        "PlayRandomAudioClipTableV3", "AudioPlayerOneShot", "AudioPlayerOneShotSingle", "AudioPlayerOneShotSingleV2",
        "AudioPlayRandom", "AudioPlayRandomSingle", "AudioPlayRandomVoice", "AudioPlayRandomVoiceFromTable",
        "AudioPlayRandomVoiceFromTableV2", "AudioPlayRandomVoiceFromTableBool", "PlayOneShotOnAudioSource",
        "PlaySound", "PlayRandomSound", "LimitedPlayAudioClipSpawn", "DoCameraShake", "DoCameraShakeV2",
        "DoCameraShakeV3", "DoCameraShakeV4", "DoCameraShakeRepeating", "DoCameraShakeRepeatingV2",
        "CameraRumbleSequence"
    ];

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
    /// The player whose game's step of a creature that every game runs by itself this game took last, as the scene
    /// host's (see OnOwnCreature).
    /// </summary>
    private ushort _stepsHost;

    /// <summary>
    /// Watches the creatures that every game runs by itself go from one state to another and die.
    /// </summary>
    private void RegisterOwnCreatureHooks() {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        OwnThreads.SendToRoomMembers = SendToRoomMembers;
        CoopHits.ReplayedStrike = TookOthersBlow;
        CoopHits.TakesOthersExtract = TakeOthersExtract;
        CoopHits.WaitsForAMove = struck => PlaysMoveThrough(struck) != null;
        OwnCreatures.KeepsLocalPlayerOut = KeepsLocalPlayerOut;
        OwnCreatures.HasNoHealthAsTold = HasNoHealthAsTold;
        OwnCreatures.MakesAlike = machine => LayoutOf(machine.gameObject) != null;
        ToldParts.OwnPick = OwnPickOf;
        ToldParts.ToldOwnPick = TellOwnPick;
        EntityManager.PicksOwnGroup = PickOwnGroup;
        _ownCreatureHooks.Add(CreateHook(
            typeof(Fsm).GetMethod("SwitchState", flags, null, [typeof(FsmState)], null),
            new Action<Action<Fsm, FsmState>, Fsm, FsmState>(OnOwnCreatureStep)
        ));

        // Its picks of a child of an object of the room at random, which go as the scene host's game picked (see
        // CoopSave.OwnPicks)
        _ownCreatureHooks.Add(CreateHook(
            typeof(HutongGames.PlayMaker.Actions.GetRandomChild).GetMethod(
                "DoGetRandomChild", flags, null, Type.EmptyTypes, null
            ),
            new Action<Action<HutongGames.PlayMaker.Actions.GetRandomChild>,
                HutongGames.PlayMaker.Actions.GetRandomChild>(OnChildPick)
        ));

        // Its checks of what a player has on (see CoopSave.OwnToolChecks)
        _ownCreatureHooks.Add(CreateHook(
            typeof(HutongGames.PlayMaker.Actions.CheckIfToolEquipped).GetProperty("IsTrue", flags)?.GetGetMethod(true),
            new Func<Func<HutongGames.PlayMaker.Actions.CheckIfToolEquipped, bool>,
                HutongGames.PlayMaker.Actions.CheckIfToolEquipped, bool>(OnToolCheck)
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

        // The way of a catch of another player's that this game plays is one from outside the state machine, which
        // it takes as it runs (see CoopSave.OwnCatches), and which stays marked as such through the switch
        if (_othersCatchWay is { } othersCatch && othersCatch.Fsm == self && self.LastTransition == othersCatch.Way &&
            Fsm.EventData != null) {
            Fsm.EventData.SentByFsm = null;
        }

        // A living creature that a player's put-back would send back to where it started, while another player is in
        // the room, stays as it is (see CoopSave.PutBack), here before the step is kept or told: passed on, the switch
        // would be held later, after the scene host's game had told the others of a step that it never took, which
        // would send their spiked flyer back to its start
        if (KeepsCreatureInPlace(self, toState)) {
            MachineSwitchToStateField?.SetValue(self, null);
            return;
        }

        // A state machine that each game runs by itself for its own player, like the one that feels the player that the
        // creature grabs, goes as the game has it, and no game tells another of its steps: what it tells the rest of
        // the creature is as if something outside it told it (see ToldFromOutside). Taken as the scene host's step, the
        // one of a game that follows the scene host's told the creature that it grabbed that game's player, who had
        // been nowhere near it. A strike of this game's player that it feels may free another player whom the creature
        // holds, whose game is told (see CoopSave.OwnStruckFree)
        if (OwnCreatures.RunsInEachGame(machine)) {
            TellStruckFree(machine, toState);
            orig(self, toState);
            return;
        }

        // A try of each player's at a moment of the fight, of which one success is everyone's: a miss here waits for
        // the tries of the others (see CoopSave.OwnTries)
        if (HoldsTry(self, toState)) {
            return;
        }

        // The end of a boss that takes the player's character, which each game plays on its own player from the state
        // where it begins (see OwnCreatures.BeginsEachGamePart): nothing of it is told or taken from then on, and a
        // player lying in their cocoon is stood up for it first, as for the copy's (see Entity.EachGamePartBegan)
        if (OwnCreatures.BeginsEachGamePart(machine, toState.Name)) {
            Logger.Info(
                $"[Own creatures] '{OwnCreatures.PlaceOf(machine.gameObject)}' goes into '{toState.Name}' of " +
                $"'{self.Name}', from which each game runs it by itself for its own player"
            );
            Entity.Entity.TellEachGamePartBegan();
            orig(self, toState);
            return;
        }

        // A part of such a creature catches the local player only while the creature goes after them (see
        // CoopSave.OwnCatches), but for one that catches whoever touches it, like the drill of a driller that sweeps
        // the room aiming at nobody (see CatchesWhoeverTouches), as it tells the creature of the catch, or as it goes
        // into a grab of the player that it plays itself, before it tells the creature (see GrabFeltBy). One that
        // looks at a player takes its steps as the creature does, and the rest of them go as the game has them, with
        // their own dice (see OwnCreatures)
        if (!OwnCreatures.Has(machine.gameObject)) {
            if ((CatchToldIn(toState) ?? GrabFeltBy(self, toState)) is { } told &&
                OwnCreatures.Owning(machine.gameObject) is { } owner && !GoesAfterLocalPlayer(owner) &&
                !CatchesWhoeverTouches(owner, told)) {
                RefuseCatch(self, owner, toState);
                return;
            }

            if (!TakesOwnSteps(machine)) {
                EnterQuietOnTheWay(orig, self, toState);
                return;
            }
        }

        // The creature, or the part of it that takes its steps as it does
        var creature = machine.gameObject;

        // What the checks of a move found in the game of the player whose thing set it off, as it goes out of the move
        // (see CoopSave.OwnToolChecks)
        ForgetToolChecks(self, toState);

        // One that played a death that it comes back from has health again, and its next death is told again: a
        // death of such a creature was told once, and the creature, back in the game that killed it, was killed there
        // again with no word of it to the others
        if (OwnCreatures.IsMarkedDead(creature) && creature.TryGetComponent<HealthManager>(out var lives) &&
            lives.hp > 0 && !lives.isDead) {
            OwnCreatures.MarkAlive(creature);
            Logger.Info(
                $"[Own creatures] '{OwnCreatures.PlaceOf(creature)}' has {lives.hp} health again in " +
                $"'{toState.Name}' of '{self.Name}', after a death that it comes back from"
            );
        }

        // In a move of another player's, which the game of that player leads (see CoopSave.OwnLedMoves)
        if (_ledMoves.Count > 0 && HoldsInLedMove(self, toState)) {
            MachineSwitchToStateField?.SetValue(self, null);
            return;
        }

        // A catch of this game's player that something of the creature's own tells it of, as a part that felt them or
        // a state machine that each game runs by itself does, while it goes after another player: it catches whom it
        // goes after, as a part of it that tells it so does (see RefuseCatch), but for a catch of whoever touches what
        // it watches (see CatchesWhoeverTouches)
        if (CatchesLocalPlayer(self, toState, creature) &&
            OwnCreatures.Owning(Fsm.EventData?.SentByFsm?.GameObject ?? creature) is { } catching &&
            !GoesAfterLocalPlayer(catching) && !CatchesWhoeverTouches(catching, self.LastTransition!.EventName)) {
            RefuseCatch(self, catching, toState);
            return;
        }

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

            // The last of the scene host's latest steps that this game sets the creature onto plays as the game has it
            // from the way into it on, but for the states that it is set through on the way there (see WalkInto)
            if ((isTheStep || _wayInLoop == null) && _lastSteps.Contains(_wayInStep!)) {
                _atLastStep.Add(self);
            }

            _heldOwnSteps.Remove(self);
            _ownWays.Remove(self);

            // The step itself may be a leap at this game's player, which this game plays from there, or a look at the
            // silk of a player, whose game leads the move from it (see CoopSave.OwnSilkLooks), and never a talk,
            // which each game's player has with the creature by themselves (see CoopSave.OwnTalks). A state on the way
            // into it does what it does as it is entered, but on the way into the creature's fight, which may go
            // through its talk, and to where it waits to be talked to, for a line of the talk or a prompt, which
            // would take this game's player into a talk that the way leaves at once (review: a member who came in
            // after the talk went through it on the way, and played every step after it by itself)
            if (isTheStep) {
                StartsOwnLeap(machine, toState);
                FollowsSilkLookAsTold(machine, toState, _wayInStep!, _stepsHost);
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
        // 21:52:01.581). What the step looks at the player it goes after for, like a mark that it puts where they are,
        // looks where the scene host's game had them, as on the way into a step (see SeeTargetAsTold)
        if (_waysThrough.Count > 0 && _waysThrough.Remove(self, out var through) &&
            toState.Name == through.StateName) {
            Logger.Info(
                $"[Own creatures] '{OwnCreatures.PlaceOf(creature)}' gets from '{self.ActiveStateName}' to " +
                $"'{toState.Name}' of '{self.Name}', the step of the scene host's that it went on its way into"
            );
            _heldOwnSteps.Remove(self);
            _ownWays.Remove(self);
            ReachKeptStep(self, toState.Name);
            var keepsPlace = KeepsItsPlace(self, through);
            PutAtSceneHostsStep(self, through, keepsPlace);
            StartsOwnLeap(machine, toState);
            FollowsSilkLookAsTold(machine, toState, through, _stepsHost);
            SeeTargetAsTold(
                creature, through,
                keepsPlace ? new Vector3(through.Values[0], through.Values[1], creature.transform.position.z) : null
            );
            try {
                EnterOwnState(orig, self, toState);
            } finally {
                OwnCreatures.SeeTargetHere();
            }

            return;
        }

        // A step that goes by where the players are or by a count that each game keeps for itself, or that another
        // state machine told it, is the scene host's: it waits for that, which has mostly come already, as this game
        // is a moment behind. So is a step into a state that tells a part of the room which it picks by where a player
        // is, after which the scene host's game tells which part it picked (see CoopSave.OwnPicks), and one into a
        // state that puts something where the player it goes after is, like the mark that it aims at, which goes where
        // the scene host's game had the player as it starts the step here (see PlacesByPlayer). The switch that the
        // game was to make is let go of, as the game tries it again at once until it is made. A leap at this game's
        // player is this game's to play, to its end (see CoopSave.OwnCatches), and so is a talk, at the end of which
        // it waits for the scene host's to come as far, and so is a step before the fight into a state whose steps of
        // the scene host's this game lets go of, which nothing comes to wait for (see CoopSave.OwnTalks). So is what a
        // strike of this game's player sets off as it is made, as in a game of one: a wound, a block or a tink, a touch
        // of it that a state machine of the creature feels itself, and what they tell the other
        // state machines of the creature at once, though not where a state that it went into chooses its way on by
        // where the players are or by a count, which stays the scene host's. The scene host's game is told of the
        // strike as it was taken here, and its creature answers it as the game does, which every other game takes as a
        // step of the scene host's (USER 11-17 "如果这个会有反馈，我这边应该先演，然后通知房主，让房主那边的也触发这个弹
        // 开", "本地化就是为了让自己的怪是活的，可以触发各种反应"; a blade of the boss at the end of the second act that the
        // player tinked did nothing, as it went by where the players are, until the scene host's step came). So is what
        // something outside the creature tells it here, like a part of the room that this game's player came into,
        // which the scene host's game is told of and plays too (see CoopSave.OwnTells)
        var waitedFor = IsWaitedFor(self);
        var teller = Fsm.EventData?.SentByFsm;

        // A touch of this game's player that the state machine felt itself (see CoopSave.OwnTells): a strike of theirs
        // goes to the others as the touch that it was, which they replay, and nothing more (review of fab59c0)
        var felt = FeltByLocalPlayer(self, toState, creature);
        var setOffByStrike = CoopHits.IsMakingLocalStrike && (teller != self || felt);
        var setOffHere = !LeadsMove(self) && !CatchesLocalPlayer(self, toState, creature) &&
                         (ToldFromOutside(self, toState, creature) || felt && !CoopHits.IsMakingLocalStrike);
        var picks = PicksAPart(toState);
        var places = PlacesByPlayer(toState);

        // A move that a thing of this game's player sets off, which a state machine that each game runs by itself for
        // its own player tells it of, finds what they have on here, which every other game is told (see
        // CoopSave.OwnToolChecks)
        var byLocalThing = setOffHere && ToldByEachGame();
        if (byLocalThing) {
            _toolChecks.Remove(self);
        }

        // A tell from outside that the scene host's game played at the word of another game a moment before, which
        // every game had, is played once, but one of a state machine that each game runs by itself, which is of this
        // game's player (see ToldByEachGame)
        if (setOffHere && IsSceneHost?.Invoke() == true && !ToldByEachGame() &&
            TellTakenAlready(self, self.LastTransition!.EventName, false)) {
            MachineSwitchToStateField?.SetValue(self, null);
            Logger.Info(
                $"[Own creatures] '{OwnCreatures.PlaceOf(creature)}' is told to go from '{self.ActiveStateName}' to " +
                $"'{toState.Name}' of '{self.Name}' here, which it was told at the word of another game a moment before"
            );
            return;
        }

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
            (WaitsAfterOwnTalk(self) || (waitedFor || places) && !setOffByStrike && !setOffHere || picks)) {
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

        // A step that a blow sets off here, of this game's player or another's as it comes, and the steps that its
        // state machine takes from there by itself, come at another moment of its way than in the scene host's game,
        // after other rolls: the scene host's are found among them by their states (see TakeOwnStep)
        _heldOwnSteps.Remove(self);
        var struck = CoopHits.IsMakingLocalStrike || CoopHits.IsReplayingPartnersStrike;
        if (struck || setOffHere) {
            _ownWays.Add(self);
        }

        if (setOffHere && OwnCreatures.FollowsSceneHost(creature)) {
            TellOwnTell(machine, toState);
        }

        // A blow of this game's player sets it off on a way of its own here a round trip before the scene host's game
        // has the blow: the steps that that game took before go by (see AwaitsLocalBlow), and so do the steps of the
        // scene host's that it kept to get to by itself (review of a0d1558). Only where that game tells its answer,
        // which a part tells only of the steps that the others wait for, that pick or that put something where the
        // player is (see below): one that a part takes by itself, like a tink, waits for no answer (review of 45dad32)
        if (CoopHits.IsMakingLocalStrike && OwnCreatures.FollowsSceneHost(creature) &&
            (!OwnCreatures.IsPart(creature) || waitedFor || picks || places)) {
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
                GamePatcher.GoAfterGrabbed(creature, hero.gameObject, self, MoveOf(self, toState));
            } else if (StrikesBackAtLocalBlow(self, toState)) {
                GamePatcher.GoAfterStriker(creature, hero.gameObject, self, MoveOf(self, toState));
            }
        }

        // The way that a hurt of a player takes it is told as the hurt, which every other game plays where its own
        // creature is (see CoopSave.OwnHits)
        if (IsSceneHost?.Invoke() == true && hit) {
            TellOwnHit(machine, toState, OwnHostHitPart);
        }

        // A part tells only the steps that the others wait for, which it took by where the players are, by a count or
        // as it was told, or into a state that picks a part of the room or puts something where the player is: the
        // rest of them the others take by themselves, and a part steps on many times in each move. One that a blow set
        // off is told as such, of another player, with whose it was, or of this game's own, and so is one of a strike
        // back at a blow of another player that this game takes at the word of their game (see MovesAt)
        if (IsSceneHost?.Invoke() == true && !hit &&
            (!OwnCreatures.IsPart(creature) || waitedFor || picks || places)) {
            Logger.Info(
                $"[Own creatures] '{OwnCreatures.PlaceOf(creature)}' goes from '{self.ActiveStateName}' to " +
                $"'{toState.Name}' of '{self.Name}'" + (struck ? " at a blow" : "") +
                $" after {step.Counts.Count / 2} kinds of rolls, as the members in the room are told"
            );
            // With the children that it picks at random in the state, which the others pick as the step comes (see
            // CoopSave.OwnPicks), and with what another state machine that told it to go on set on it there, which
            // the others take with the step: the nest that drops a worm sets on it which nest it is, which it comes
            // out of, and in a game that follows, whose nest had picked another worm of the pool or none, the worm
            // came out of a nest it went out of before, or none (review of 5670703)
            PickChildren(self, toState);
            var update = OwnCreatureUpdate(
                machine, step, teller != null && teller != self ? ToldValues.SetBy(teller, creature, machine) : null
            );
            if (CoopHits.IsReplayingPartnersStrike) {
                update.PartCount = OwnStruckPart;
                update.Key = CoopHits.ReplayedStriker;
                update.Sequence = CoopHits.ReplayedStrikeNumber;
            } else {
                if (struck || CoopHits.TakesAnothersBlow) {
                    update.PartCount = OwnHostStruckPart;

                    // The number of the blow of this game's player, by which every game knows the steps at one blow
                    // (see CoopSave.OwnBlowSteps)
                    if (CoopHits.IsMakingLocalStrike) {
                        update.Key = CoopHits.LocalStrikeNumber;
                    }
                }

                // A look at the silk of another player, whose game leads the move from it under this number (see
                // CoopSave.OwnSilkLooks)
                update.Sequence = StartsOthersSilkLook(self, toState, creature);
            }

            // What the checks of what the player has on in a move that a thing of this game's player sets off find
            // here (see CoopSave.OwnToolChecks)
            if (byLocalThing) {
                update.Names = ToolChecksOf(self, toState);
            }

            SendToRoomMembers(update);
        }

        // The state of the last of the scene host's latest steps of a state machine that this game sets the creature
        // onto, which it gets to by itself on the way, plays as the game has it, and the step, if it is still to
        // come, finds it there (review of 4362d0d: the last step kept, got to by itself as the one before it
        // finished at once, made no sound)
        if (_lastSteps.Count > 0 && IsLastStepState(self, toState)) {
            _atLastStep.Add(self);
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
        // A part with health of its own, like the chomper under the lily pad of the clover woods, dies in every game
        // too, which tells its own death to its lily pad: struck to death in one game, it lived on in another, where
        // the blow that killed it came as it had sunk back under the water, and bit there at the next landing
        var tells = !_dyingAsTold && !self.isDead && OwnCreatures.RollsUnderPlace(self.gameObject);
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

        // A boss that dies here, at a blow or as told, stands the local player up if they lie in their cocoon, as the
        // death of a copy does (see StandUpAsABossDies): the end of a boss that takes the character found the player
        // lying there, as each game plays it on its own player (see BossRoomCoop.Endings)
        if (OwnCreatures.Has(self.gameObject) &&
            (self.isDead || self.hasSpecialDeath && self.hp <= 0 && HearsNoHealth(self))) {
            StandUpAsABossDies(self);
        }

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

        // A death at a blow of another player that is played here is theirs to tell, as their game plays it too, but
        // for one that a state machine outside the creature deals as it hears of the blow, which their game may hold
        // for this game's word: the controller of the fight with the two drillers of the coral, which follows the
        // scene host's (see CoopSave.DirectedFights), kills the last of them as it is struck with no health left
        if (!tells || !self.isDead && !(self.hasSpecialDeath && self.hp <= 0 && HearsNoHealth(self)) ||
            !OwnCreatures.MarkDead(self.gameObject) || CoopHits.IsReplayingPartnersStrike && !DealtFromOutside(self)) {
            return;
        }

        var creature = self.gameObject;
        var place = OwnCreatures.PlaceOf(creature);
        var death = OwnCreatures.CountDeath(creature);
        Logger.Info(
            $"[Own creatures] '{place}' died here at ({where.x:F1}, {where.y:F1}), its death {death} here, as the " +
            "members in the room are told" +
            (target != null ? $", the player it goes after {beside.x:F1}, {beside.y:F1} from it" : "")
        );
        SendToRoomMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.OwnCreature,
            Scene = creature.scene.name,
            ObjectPath = place,
            PartCount = 2,
            Sequence = death,
            Key = self.isDead ? 1UL : 0UL,
            Part = ThrownThings.CodeOf?.Invoke(target) ?? ThrownThings.NoPlayer,
            Values = attackDirection is { } direction
                ? [where.x, where.y, facing, turned, beside.x, beside.y, direction]
                : [where.x, where.y, facing, turned, beside.x, beside.y]
        });
    }

    /// <summary>
    /// Whether the death of a creature, or of a part of one with health of its own, is dealt by a state machine that
    /// is not of the creature, nor of a part of it, which runs now (InstaDeath), like the controller of a fight. A part
    /// of the creature, taken for one from outside, told a death of its own that the replay of a partner's blow dealt
    /// here, which the partner's game tells (review of 1d687b9).
    /// </summary>
    private static bool DealtFromOutside(HealthManager health) {
        return HutongGames.PlayMaker.FsmExecutionStack.ExecutingFsm?.GameObject is { } dealer && dealer != null &&
               OwnCreatures.Owning(dealer) != OwnCreatures.Owning(health.gameObject);
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
    /// (see SeeTargetAsTold). The variables named in <paramref name="setOnIt"/> go with what the state worked with.
    /// </summary>
    private static CoopSaveUpdate OwnCreatureUpdate(
        PlayMakerFSM machine,
        OwnStep step,
        IEnumerable<string>? setOnIt = null
    ) {
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

        // The first step of a state machine leaves no state. What it worked out goes, and the objects that it found by
        // where a player is (see LooksAtPlayersAtTimes), and with every step the creature that it fights, if it fights
        // the room's creatures (see CoopSave.OwnFighters)
        var fought = FoughtBy(fsm);
        var worked = fsm.ActiveState is { } state
            ? ToldValues.Of(
                fsm,
                state.Actions.Where(action => action.Enabled).SelectMany(
                    action => Entity.Action.EntityFsmActions.ValueVariablesOf(action).Concat(
                        LooksAtPlayersAtTimes(action) ? ToldValues.ObjectsStoredBy(action) : []
                    )
                ).Concat(setOnIt ?? []).Concat(fought).Distinct(),
                fought
            )
            : ToldValues.None;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream)) {
            worked.Write(writer);
            SharedDice.WritePastPicks(writer, fsm);
            writer.Write(SharedDice.BouncesOf(machine.gameObject));
            WriteChildPicks(writer, fsm, step.State);
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
    /// came into the new load of the room first: on the trip before, the silk boss, won here, was told dead to them,
    /// died once more in their game, and as the local player came in, their game told it dead to them in turn, which
    /// made the fight won twice (the user's log of 11-22, 20:50:14.756 and 17.140; USER "我们打了第二次，好诡异，丝母在左下
    /// 角苏醒，然后最后打死了莫名其妙判赢两次（").
    /// </summary>
    /// <param name="player">The member.</param>
    private void SendOwnCreaturesTo(ClientPlayerData player) {
        if (OwnCreatures.Count == 0 || !_checkedMembers.Contains(player.Id) || IsSceneHost?.Invoke() != true) {
            return;
        }

        if (IsLeavingOnDebugTrip?.Invoke() == true) {
            Logger.Info(
                $"[Own creatures] {player.Username} is told nothing of the creatures here: the local player is on " +
                "the debug trip out of the room, to the new load of it that they came into"
            );
            return;
        }

        // What made each summon that is alive here, before what any creature here does, which their game makes as it
        // did here (see CoopSave.OwnSummons), and which its maker there takes for its own as the step of its own here
        // takes it there through the state that made it (see OwnCreatures.MakesSummon; review of 3653911)
        foreach (var pair in OwnCreatures.All) {
            var summon = pair.Key;
            if (summon != null && OwnCreatures.MakerOf(summon) is { } maker &&
                !(summon.TryGetComponent<HealthManager>(out var health) && health.isDead)) {
                TellOwnMade(player, pair.Value.Scene, pair.Value.Place, maker);
            }
        }

        // A dead one is taken away there, but for one whose end each game plays by itself, which dies there too, as
        // it died here, so that their game plays the end on its player as well (USER 11-26 "各演各的"; see
        // OwnCreatures.EachGameFrom): the widow, who marks herself dead as her end begins, was taken away there, and
        // the member who came in neither bound her nor went into the memory that her end sends them to (review of
        // b18af06..c5de11d)
        foreach (var pair in OwnCreatures.All) {
            var creature = pair.Key;
            if (creature == null || creature.TryGetComponent<HealthManager>(out var health) && health.isDead &&
                !(OwnCreatures.IsMarkedDead(creature) && OwnCreatures.PlaysEndInEachGame(creature))) {
                Logger.Info($"[Own creatures] {player.Username} is told that '{pair.Value.Place}' is dead here");
                TellTakenAway(player, pair.Value.Scene, pair.Value.Place);
                continue;
            }

            // What the third act does to it, out or not, before its steps: the health that comes with them has what
            // the game doubles for one that turns already
            if (OwnThreads.WordFor(creature) is { } thread) {
                thread.TargetId = player.Id;
                Send(thread);
            }

            // One that has not come out of where the room keeps it, as the save has it, does nothing yet, and a summon
            // that is gone here, like a fly that flies off once the conductor is dead, is taken away there: their game
            // had made it, and it flew about there, as nothing told it what it does (review of 3653911). One that has
            // not come out yet, which never ran, like the fly that a cocoon keeps until it lets it out, waits there as
            // here, as their maker made it so too: taken away, it died there as it came out (see UpdateDeathsOwed)
            if (!creature.activeInHierarchy) {
                if (OwnCreatures.MakerOf(creature) != null &&
                    creature.GetComponents<PlayMakerFSM>().Any(machine => machine.Fsm.Started)) {
                    Logger.Info($"[Own creatures] {player.Username} is told that '{pair.Value.Place}' is gone here");
                    TellTakenAway(player, pair.Value.Scene, pair.Value.Place);
                }

                continue;
            }

            // One that lies dead here, of a death that it comes back from, like a fly of the conductor's until it is
            // called back, dies there as it died here: set straight into the state where it lies, it stood there as
            // the states before had left it, seen and in the way of the players (review of 3653911)
            if (health != null && OwnCreatures.IsMarkedDead(creature)) {
                TellLyingDead(player, pair.Value.Scene, pair.Value.Place, creature);
                continue;
            }

            foreach (var machine in creature.GetComponents<PlayMakerFSM>()) {
                if (!OwnCreatures.RunsInEachGame(machine)) {
                    TellOwnState(player, machine, health);
                }
            }
        }

        // The parts that take their steps as their creatures do, of those that are alive and out here
        foreach (var (part, creature) in OwnCreatures.AllParts()) {
            if (part == null || creature == null || !creature.activeInHierarchy || !part.activeInHierarchy ||
                creature.TryGetComponent<HealthManager>(out var health) && health.isDead) {
                continue;
            }

            foreach (var machine in part.GetComponents<PlayMakerFSM>()) {
                if (TakesOwnSteps(machine) && !OwnCreatures.RunsInEachGame(machine)) {
                    TellOwnState(player, machine, null);
                }
            }
        }
    }

    /// <summary>
    /// Tells a member who has come into the room that a creature that every game runs by itself is not out here, dead
    /// or gone, at which their game takes it away (see OnOwnCreature).
    /// </summary>
    private void TellTakenAway(ClientPlayerData player, string scene, string place) {
        Send(new CoopSaveUpdate {
            TargetId = player.Id,
            Kind = CoopSaveUpdateKind.OwnCreature,
            Scene = scene,
            ObjectPath = place,
            PartCount = 1
        });
    }

    /// <summary>
    /// Tells a member who has come into the room of the death of a creature that every game runs by itself that lies
    /// dead here, of a death that it comes back from, where it lies, which their game plays as one that it was just
    /// told of, by its number here (see OnOwnCreature).
    /// </summary>
    private void TellLyingDead(ClientPlayerData player, string scene, string place, GameObject creature) {
        var lying = creature.transform;
        var at = lying.position;
        Logger.Info($"[Own creatures] {player.Username} is told that '{place}' lies dead here, and dies there too");
        Send(new CoopSaveUpdate {
            TargetId = player.Id,
            Kind = CoopSaveUpdateKind.OwnCreature,
            Scene = scene,
            ObjectPath = place,
            PartCount = 2,
            Sequence = OwnCreatures.DeathsOf(creature),
            Part = ThrownThings.NoPlayer,
            Values = [at.x, at.y, lying.localScale.x, lying.eulerAngles.z, 0f, 0f]
        });
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

        // The player whose move it is in here, which their own follows as it goes into it (see FollowExtractAsTold)
        update.Key = LeaderCodeOf(fsm);

        // Whether it is shown here, as the states that show it and hide it made it (see OnOwnCreature)
        var shown = thing.TryGetComponent<Renderer>(out var look) && look.enabled;
        update.Values.Add(shown ? 1f : 0f);

        // Their own has all its health, which the blows before they came took here, and counts its deaths from as many
        // as it had here, of one that came back from them (see OwnCreatures.TakeDeath)
        if (health != null) {
            update.Values.Add(health.hp);
            update.Sequence = OwnCreatures.DeathsOf(thing);
        }

        Logger.Info(
            $"[Own creatures] {player.Username} is told that '{OwnCreatures.PlaceOf(thing)}' is in " +
            $"'{fsm.ActiveStateName}' of '{fsm.Name}' here" + (look != null && !shown ? ", out of sight" : "") +
            (health != null ? $", with {health.hp} health" : "")
        );
        Send(update);
    }

    /// <summary>
    /// The creatures that every game runs by itself which died in another player's game, or were dead there as the
    /// local player came in, while they were not out here, with the word of it (see <see cref="UpdateDeathsOwed"/>).
    /// </summary>
    private readonly Dictionary<GameObject, (ClientPlayerData Player, CoopSaveUpdate Word)> _deathsOwed = new();

    /// <summary>
    /// Has the creatures that died in another player's game while they were not out here die here as they come out,
    /// and those whose cage had the local player as they let go of them, by the word that told of it (see
    /// <see cref="OnOwnCreature"/>).
    /// </summary>
    private void UpdateDeathsOwed() {
        if (_deathsOwed.Count == 0) {
            return;
        }

        foreach (var pair in _deathsOwed.ToList()) {
            var creature = pair.Key;
            if (creature != null &&
                (!creature.activeInHierarchy || ArenaCoop.WaitsForItsWave(creature) || CagesLocalPlayer(creature))) {
                continue;
            }

            _deathsOwed.Remove(creature!);
            if (creature != null) {
                OnOwnCreature(pair.Value.Player, pair.Value.Word);
            }
        }
    }

    /// <summary>
    /// Keeps a creature that every game runs by itself to a step that the scene host's took, or to its death there.
    /// </summary>
    /// <param name="player">The player whose game is the scene host's.</param>
    /// <param name="update">The update.</param>
    private void OnOwnCreature(ClientPlayerData player, CoopSaveUpdate update) {
        var place = update.ObjectPath;

        // A summon that is not made or started here yet waits for it (see CoopSave.OwnSummons)
        if (OwnCreatures.IsSummonPlace(place) && KeepsSummonWord(player, update)) {
            return;
        }

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

        // One that is not out here yet dies here as it comes out, and one of a wave of an arena once the wave called
        // it out here, which counts it then (see UpdateDeathsOwed): let go of, it came out alive later and fought on
        // in this game alone, as a member came in after it died there, or as the wave that lets it out started there
        // a moment before it did here (11-24 review; USER 11-25 "做吧"). So does one whose cage has the local player,
        // which the game keeps from harm until the capture takes them away, and whose death in the game lets go of
        // them only by halves, as it never comes then: they were left hidden, floating and unable to save, as the
        // partner killed it in their game, where it had caught nobody (review of 59f478f)
        if (update.PartCount is 1 or 2 && hasHealth && !health.isDead &&
            (!creature.activeInHierarchy || ArenaCoop.WaitsForItsWave(creature) || CagesLocalPlayer(creature))) {
            if (_deathsOwed.TryAdd(creature, (player, update))) {
                var told = update.PartCount == 1 ? "is dead" : "died";
                Logger.Info(
                    $"[Own creatures] '{place}' {told} in {player.Username}'s game, and " +
                    (CagesLocalPlayer(creature)
                        ? "has the local player in its cage here: it dies here if it lets go of them"
                        : "is not out here yet: it dies here as it comes out")
                );
            }

            return;
        }

        // A creature that fights the room's creatures beside the players is out where it is out in another game (see
        // CoopSave.OwnFighters), and goes on as there once it runs
        if (update.PartCount is not (1 or 2) && ComesOutAsThere(player, update, creature)) {
            return;
        }

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
            if (!OwnCreatures.TakeDeath(creature, update.Sequence)) {
                Logger.Info(
                    $"[Own creatures] '{place}' died in {player.Username}'s game, its death {update.Sequence} there, " +
                    $"which it played here already, of {OwnCreatures.DeathsOf(creature)} here"
                );
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
                $"[Own creatures] '{place}' died in {player.Username}'s game, its death {update.Sequence} there, and " +
                $"dies here too, where it did there, {Vector2.Distance(dying.position, diedAt):F1} away, with " +
                $"{health.hp} health here" + (lookedAt
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

            // It has no health here either, as there, where the blow that killed it did not land here, or has not come
            // yet: the health that it still had here was taken for health that it has again after a death that it comes
            // back from at the first step of its death, and a blow of this game's player in it told a death of it once
            // more (review of cfaf69f)
            health.hp = 0;
            OwnCreatures.AwardJournalKill(health);

            // One that died there for good, not by a death of its own that its state machines play, dies so here too:
            // the last of the two drillers of the coral, which have such a death, is killed by the controller of
            // their fight, which takes that death off the one struck with no health left and kills it, and the
            // controller here, going into that state at the scene host's word, has no one struck to kill, so that
            // the driller here only took the blow and fought on
            if (update.Key == 1 && health.hasSpecialDeath) {
                health.hasSpecialDeath = false;
                Logger.Info($"[Own creatures] '{place}' died for good there, and so here too");
            }

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

        // How the try of the player of that game went at a moment of the fight where each player has their own, of
        // which one success is everyone's (see CoopSave.OwnTries)
        if (update.PartCount == OwnTryPart) {
            TakeTry(player, creature, update);
            return;
        }

        // A step into a move that a thing of the scene host's player set off there tells what the checks of what the
        // player has on in it found there (see CoopSave.OwnToolChecks)
        if (update.PartCount is 0 or OwnStruckPart or OwnHostStruckPart) {
            NoteToolChecks(creature, update, update.StateName, 0);
        }

        // One that comes while steps of the scene host's before it wait for the creature here to play its move through
        // waits behind them, as it came, whatever takes it then: a step, what a step did there, a hurt of a player,
        // a catch, a strike back or a pick (see KeepsForAfterMove; review of 06e0bad: a hurt that came was looked at
        // in the move before here, and let go of), and whichever state machine of the creature or of a part of it it
        // is of, as one of them tells another what to do there, in the order that they took their steps. The silk
        // boss, still turning here in the state machine of its moves, which kept the step there into its next attack
        // for after the turn, took the step there of the state machine of its attacks into its cast of the web at
        // once, which the other had told it there as it went into that attack: the turn here then ended into its
        // rest, whose look took the place of the cast, and the boss waited in its lift for a moment of the cast that
        // never came, until it was set on 10 s later (the user's log of 11-23, 20:07:35.097 to 20:07:45.433; USER
        // "这个丝母闪来闪去"). Not one for a state machine that waits for the answer to a blow of this game's
        // player, which is looked at as it comes against what the blow set off here (see AwaitsLocalBlow): the word
        // that the scene host's game is through with the blow, which goes at once, went past the steps there from
        // before the blow, which waited behind the move, and these were then taken over what the blow set off here,
        // and its answer played that again from its start (review of 1397e10). Nor the word that another player struck
        // it free of the local player, or that the scene host's game took no grab of them, which is of the move that
        // it plays here (see CoopSave.OwnStruckFree)
        if (!_takingStepsAfterMove &&
            update.PartCount is not (OwnEmptiedPart or OwnStrikeDonePart or OwnStruckFreePart or OwnLetGoPart) &&
            PlaysMoveThrough(creature) is { } waiting &&
            (PlayMakerFSM.FindFsmOnGameObject(creature, update.FsmName) is not { } its || !AwaitsLocalBlow(its.Fsm))) {
            var afterMove = _stepsAfterMove[waiting];
            afterMove.Add((player, update));
            Logger.Info(
                $"[Own creatures] '{place}' went to '{update.StateName}' of '{update.FsmName}' in " +
                $"{player.Username}'s game (part {update.PartCount}), which waits behind " +
                $"{afterMove.Count - 1} step(s) of that game until its move in '{waiting.Name}' is through here"
            );
            return;
        }

        // A part of its own caught the player of that game, which plays the catch by itself
        if (update.PartCount == OwnCatchPart) {
            TakeWhatStepDid(update, () => TakeOwnCatch(player, creature, update));
            return;
        }

        // It struck back at a blow of the player of that game that it blocked there, which that game plays by itself
        if (update.PartCount == OwnStrikeBackPart) {
            TakeWhatStepDid(update, () => TakeOwnStrikeBack(player, creature, update));
            return;
        }

        // It hurt a player there, which it plays here too, where it is (see CoopSave.OwnHits)
        if (update.PartCount is OwnHitPart or OwnHostHitPart) {
            TakeWhatStepDid(update, () => TakeOwnHit(player, creature, update));
            return;
        }

        // Something outside it told it to go on there, which it is told here too (see CoopSave.OwnTells)
        if (update.PartCount == OwnTellPart) {
            TakeOwnTell(player, creature, update);
            return;
        }

        // It went on in a move that the game of the player it acts on leads (see CoopSave.OwnLedMoves)
        if (update.PartCount == OwnLedStepPart) {
            TakeLedStep(player, creature, update);
            return;
        }

        // The player of that game struck it free of the player it holds, or the scene host's game took no grab of
        // the player it grabbed: their game lets them go (see CoopSave.OwnStruckFree)
        if (update.PartCount is OwnStruckFreePart or OwnLetGoPart) {
            TakeStruckFree(player, creature, update);
            return;
        }

        // It told a part of the room there that it picked by where a player is, which it tells here too
        if (update.PartCount == OwnPickPart) {
            TakeWhatStepDid(update, () => TakeOwnPick(player, creature, update));
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

        // One that each game runs by itself here from the end of a boss on goes as this game's player plays it, and a
        // step of the scene host's, whose end began later there, is of that game's player (see
        // OwnCreatures.BeginsEachGamePart)
        if (OwnCreatures.RunsInEachGame(fsm.FsmComponent)) {
            Logger.Info(
                $"[Own creatures] '{place}' went to '{update.StateName}' of '{fsm.Name}' in {player.Username}'s " +
                "game, which each game runs by itself for its own player here"
            );
            return;
        }

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

        // A step at a blow of another player on a thing that takes blows by itself waits for the replay of the blow
        // here, and so does every one after it (see CoopSave.OwnBlowSteps), also as the creature is set onto the scene
        // host's latest, after which the replays that waited for its move are made (review of 638a2ec: walked in,
        // the step counted the blow, and the replay counted it again)
        if (update.PartCount != OwnToldPart && WaitsForReplayedBlow(player, fsm, update)) {
            return;
        }

        var transform = creature.transform;
        var there = new Vector3(update.Values[0], update.Values[1], transform.position.z);
        var found = TakeOwnStep(fsm, update, out var newest);

        // One that it gets to here by itself is kept until it gets there (see below). The steps of the scene host's
        // before this one that it was to get to by itself, and has not, are taken first, as they came, before this one
        // tells whom it goes after and that its fight is on, and one that it played here already is past them (see
        // TakeKeptSteps; review of a0d1558: whom the last kept step went after stood, and the first step into the
        // fight, kept, ran the talk on the way), but while it is still on its way to them, as it plays its move
        // through, which this one waits for (see KeepsForAfterMove). Not in a move that another player's game leads,
        // where it goes on by the scene host's steps only (see HoldsInLedMove)
        var keeps = found == null && update.PartCount != OwnToldPart && !_takingKeptSteps && !_settingOntoLatest &&
                    !_ledMoves.ContainsKey(fsm) && ReachesByItself(fsm, update.StateName);
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

            // And the deaths that it came back from there, from which the next is counted here as there: a death of
            // it by the blow of this game's player was told by a number that the scene host's game played already
            if (update.Sequence > 0) {
                Logger.Info(
                    $"[Own creatures] '{place}' died {update.Sequence} time(s) in {player.Username}'s game before"
                );
                OwnCreatures.SetDeaths(creature, update.Sequence);
            }

            OwnThreads.TookHealth(creature);
        }

        if (found is { } took) {
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
            // (review), and so do its picks at random from what the scene host's kept, where it has none, and so does
            // the creature it fights, if it fights the room's creatures (see CoopSave.OwnFighters)
            if (newest && update.Values.Count > OwnHealthValue) {
                using var reader = new BinaryReader(new MemoryStream(update.Bytes));
                ToldValues.Read(reader).ApplyTo(fsm, FoughtBy(fsm));
                SharedDice.PutPastPicks(reader, fsm);
                SharedDice.SetBounces(creature, reader.ReadInt32());
                SkipChildPicks(reader);
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
        _waysThrough.Remove(fsm);
        var way = WayInto(fsm, update.StateName);
        var from = fsm.ActiveStateName;
        var moves = MovesAt(fsm, update);
        var keepsPlace = KeepsItsPlace(fsm, update);
        var away = Vector2.Distance(transform.position, there);
        if (moves && !keepsPlace) {
            MoveOwnSteps(creature, there - transform.position);
        }

        // Drawn to the character of the player who extracts from it, of an extraction that the step goes into
        FollowExtractAsTold(player, fsm, update);
        PutAtSceneHostsStep(fsm, update, keepsPlace);
        OwnCreatures.SceneHostsStep = (fsm, update.StateName);
        _stepsHost = player.Id;

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
        SharedDice.SetBounces(creature, reader.ReadInt32());
        ReadChildPicks(reader, fsm, update);
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
    /// state in every game. So does the pick of the creature that a creature that fights the room's creatures goes for
    /// (see CoopSave.OwnFighters).
    /// </summary>
    private bool GoesByPlayers(FsmState state) {
        var fsm = state.Fsm;
        if (!_playerWayValues.TryGetValue(fsm, out var stored)) {
            stored = [];
            foreach (var each in fsm.States) {
                foreach (var action in each.Actions) {
                    if (PlayerWayActionTypes.Contains(action.GetType().Name) || LooksAtPlayersAtTimes(action)) {
                        stored.UnionWith(Entity.Action.EntityFsmActions.ValueVariablesOf(action));
                    }
                }
            }

            _playerWayValues[fsm] = stored;
        }

        var fought = FoughtBy(fsm);
        foreach (var action in state.Actions) {
            if (action.Enabled && (LooksAtPlayers(action) || PicksFought(action, fought) ||
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
    /// hand aimed, which the hand did as the scene host's. So does one of the kinds that look at a player only now and
    /// then, where it does (see <see cref="LooksAtPlayersAtTimes"/>).
    /// </summary>
    private static bool LooksAtPlayers(FsmStateAction action) {
        return PlayerWayActionTypes.Contains(action.GetType().Name) &&
               (Entity.Action.EntityFsmActions.ChoosesWhereItGoes(action) ||
                !Entity.Action.EntityFsmActions.LooksOnlyAtItsCreature(action)) ||
               LooksAtPlayersAtTimes(action);
    }

    /// <summary>
    /// Whether an action of the kinds that look at a player only now and then, like where an object is, how high it
    /// stands or what a ray cast from it hits (see Entity.Action.ActionRegistry.TargetedWherePlayerActionTypes), looks
    /// at the player whom the creature goes after to choose where its state goes or to work out a number of its state
    /// machine (see Entity.Action.EntityFsmActions.ValueVariablesOf): one made to look at that player (see
    /// GamePatcher.LooksAtAPlayerByMake), in a state that does nothing to a player. One in a state that does is about
    /// the player that the state acts on, like the hold of a knight that keeps a caught player within its room as it
    /// lets them go, which reads where they are and puts them back within it. A creature that digs under the ground
    /// bursts out where the player is along it, which each game has a little elsewhere: it would come out in another
    /// place in each game until the scene host's next step put it where the scene host's came out (11-16 survey; USER
    /// 11-24 "可以把更多的怪本地化吗"). So does one that finds an object by where that player is, like the platform
    /// nearest them that the small bone hunter comes up out of, which the step tells (see Entity.ToldValues): each
    /// game would find its own, by where it has the player, and the hunter would come up out of another platform where
    /// the player stands between two, until the scene host's next step put it where the scene host's was. One that
    /// only keeps an object, like a player in a variable, works out nothing to tell.
    /// </summary>
    private static bool LooksAtPlayersAtTimes(FsmStateAction action) {
        return Entity.Action.ActionRegistry.TargetedWherePlayerActionTypes.Contains(action.GetType().Name) &&
               (Entity.Action.EntityFsmActions.ChoosesWhereItGoes(action) ||
                Entity.Action.EntityFsmActions.ValueVariablesOf(action).Any() ||
                ToldValues.ObjectsStoredBy(action).Any()) &&
               GamePatcher.LooksAtAPlayerByMake(action) &&
               !Entity.Action.EntityFsmActions.DealsWithThePlayerIn(action.State);
    }

    /// <summary>
    /// Whether a state puts something where the player that its creature goes after is, like the mark that a boss aims
    /// its dash or throw at, the point by which a grasshopper of the clover woods comes out of its jump or the creature
    /// itself: each game would put it where it has that player, a little elsewhere in each, and the creature would come
    /// at another place in each game until the scene host's next step put it where the scene host's was. Its step in is
    /// the scene host's, whose first look at the player looks where the scene host's game had them (see
    /// OwnCreatures.TargetThere): what looks at the players goes by the scene host's game (USER 11-25 "看人的部分听房主";
    /// "客机就不应该挑招"). Not in a state that does something to the player, like a catch that puts a stand-in of the
    /// caught player where they are, which is about the player caught (see
    /// GamePatcher.LooksAtAPlayerByMake), found once for each state.
    /// </summary>
    private bool PlacesByPlayer(FsmState state) {
        if (!_placesByPlayer.TryGetValue(state, out var places)) {
            places = state.Actions.Any(
                action => action is { Enabled: true } and (HutongGames.PlayMaker.Actions.SetPositionToObject
                              or HutongGames.PlayMaker.Actions.SetPositionToObject2D
                              or HutongGames.PlayMaker.Actions.SetPositionToObject2DV2) &&
                          GamePatcher.LooksAtAPlayerByMake(action)
            );
            _placesByPlayer[state] = places;
        }

        return places;
    }

    /// <summary>
    /// Whether the others wait for a step that a state machine of a creature that every game runs by itself takes now,
    /// in the games that follow the scene host's: one out of a state that goes by where the players are or by a value
    /// that each game has for itself (see <see cref="ChoosesByOwnValue"/>), or one that another state machine told it.
    /// </summary>
    private bool IsWaitedFor(Fsm fsm) {
        return fsm.ActiveState is { } from && (GoesByPlayers(from) || ChoosesByOwnValue(from)) ||
               Fsm.EventData?.SentByFsm is { } sender && sender != fsm;
    }

    /// <summary>
    /// The variables that the machines of a room that run by the scene host's word set on other state machines, by the
    /// name of the state machine and of the variable (see CoopSave.PacedMachines, CoopSave.DirectedFights), and those
    /// that the creatures of the room that every game runs by itself, and their parts, set so, found once for the room
    /// (the room's creatures are taken as it loads, see OwnCreatures.Take).
    /// </summary>
    private HashSet<(string Fsm, string Variable)> MachineSetValues(UnityEngine.SceneManagement.Scene room) {
        if (_machineSetValues.TryGetValue(room.name, out var set)) {
            return set;
        }

        set = [];
        foreach (var root in room.GetRootGameObjects()) {
            foreach (var machine in root.GetComponentsInChildren<PlayMakerFSM>(true)) {
                if (FindMachineKind(machine.gameObject, machine.FsmName) == null &&
                    OwnCreatures.Owning(machine.gameObject) == null) {
                    continue;
                }

                foreach (var each in machine.FsmStates) {
                    foreach (var action in each.Actions) {
                        if (VariableSetOnAnother(action) is { } written) {
                            set.Add(written);
                        }
                    }
                }
            }
        }

        _machineSetValues[room.name] = set;
        return set;
    }

    /// <summary>
    /// The state machine and the variable of it that an action sets a switch or a number of, or adds to a number of,
    /// on another state machine than its own, or null for an action that sets none.
    /// </summary>
    private static (string Fsm, string Variable)? VariableSetOnAnother(FsmStateAction action) {
        var (fsmName, variableName) = action switch {
            HutongGames.PlayMaker.Actions.SetFsmBool set => (set.fsmName, set.variableName),
            HutongGames.PlayMaker.Actions.SetFsmInt set => (set.fsmName, set.variableName),
            HutongGames.PlayMaker.Actions.SetFsmFloat set => (set.fsmName, set.variableName),
            HutongGames.PlayMaker.Actions.FsmIntAdd add => (add.fsmName, add.variableName),
            _ => (null, null)
        };
        return fsmName?.Value is { Length: > 0 } fsm && variableName?.Value is { Length: > 0 } variable
            ? (fsm, variable)
            : null;
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
            if (GoesByPlayers(state) || ChoosesByOwnValue(state) || ways.Count != 1) {
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
    /// into none that picks a part of the room or puts something where the player is, whose way in waits for the scene
    /// host's step (see PicksAPart, PlacesByPlayer). It is a moment behind, and goes the same way as fast. Kept to get
    /// to by itself, a step into a state that puts something where the player is would stand where its way in was held,
    /// as a state finishes only once, until the scene host's next step or a second later, and then jump on through it
    /// (review of eac9c4f).
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
            if (GoesByPlayers(state) || ChoosesByOwnValue(state)) {
                continue;
            }

            foreach (var transition in state.Transitions) {
                if ((transition.ToFsmState ?? fsm.GetState(transition.ToState)) is not { } to || PicksAPart(to) ||
                    PlacesByPlayer(to) || !TakenByItself(state, transition)) {
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
    /// Whether a state chooses its way on by a value that each game has for itself, other than the scene host's or at
    /// another moment: the scene host's is the one.
    ///
    /// A count: one of its actions decides where it goes by a number that a state of its state machine adds to once as
    /// it starts (see HitCounters.CountedBy), like the blows that the boss at the end of the second act took since it
    /// was last stunned. Each game takes in the blows of every player, its own player's at once and the others' as they
    /// come, and counts some of them in another state than the others, so that a count is full at another blow in each
    /// game (review: the striker's game stunned the boss at a blow of its own, and played the stun again as the scene
    /// host's step came). So is how many objects there are under one (GetChildCount), like the flies that a conductor
    /// made, under the object of the dead ones, which each game has more or fewer of for a moment as a fly dies at
    /// another moment there, and calls back.
    ///
    /// Its health, as it reads it to choose a part of its fight by (GetHP): it comes under the mark of the next part at
    /// another blow in each game, and the giant driller, a blow short of it in one game, chose a move of its first part
    /// there while the scene host's roared into its second (review of d71f2ce; USER 11-17 "我说白了血条听房主的"). What
    /// it does as it has none goes by the scene host's word that it has none (see CoopSave.OwnHealth).
    ///
    /// The save of the player of the game (a test of PlayerData, or what GetPlayerData stores), which differs between
    /// players: the last judge, at the first sight of the player, threw its censer in a game whose player had never met
    /// it and waited in the other's (review of 556ca6e). So does what the player has on (CheckIfToolEquipped): the
    /// shaman of the swamp swallows a bomb into the burst of poison where the player has the pouch on, and a game whose
    /// player had not would burst it plain, to be put back into the swallow as the scene host's step came. A bomb of
    /// another player than the scene host's is theirs, and so is the move that swallows it, which their game leads (see
    /// CoopSave.OwnTells, LeadsMoveByWhatPlayerHas).
    ///
    /// A value that a machine of the room that runs by the scene host's word sets on its state machine (see
    /// <see cref="MachineSetValues"/>), like the flag by which the foremen of the docks leap out of the way of the lava
    /// that the fall of their fight pours. A game that follows the scene host's starts each round of such a machine at
    /// the scene host's word, a moment late, and sets the value as late: its foremen, back at their rest in that
    /// moment, found the lava still coming and leapt away from lava that the scene host's foremen knew was gone, to be
    /// put back at the scene host's next step (review of 790db7f). So too one that a creature that every game runs by
    /// itself sets on it at a moment of each game's own, as at its death: a juror of the court of the crows takes one
    /// off the count of the summoned as it falls, by which their father calls more, and a juror felled by a blow of
    /// one game's player a moment before the scene host's game had it fall, or after, made the father call there and
    /// not in the other.
    ///
    /// A value that it reads off a state machine of another object (GetFsmBool, GetFsmInt, GetFsmFloat), like one of
    /// the room that each game runs by itself: the flower boss of the dream room leaves its brambles where the platform
    /// that a player who fell on them comes back up on is up, which each game has for its own player. The scene host's
    /// boss, its player on the platform, left them, while the other game's went on to bring them down by itself, and
    /// was set onto the scene host's later steps past the step that brings them up again: the spikes on its walls
    /// stood otherwise than in the scene host's game until the next leap (review of b37f128).
    /// </summary>
    private bool ChoosesByOwnValue(FsmState state) {
        var fsm = state.Fsm;
        if (!_ownValues.TryGetValue(fsm, out var own)) {
            own = [];
            foreach (var each in fsm.States) {
                foreach (var action in each.Actions) {
                    if (HitCounters.CountedBy(action) is { Length: > 0 } name) {
                        own.Add(name);
                    }

                    if (OwnValueStoredBy(action) is { UseVariable: true, Name.Length: > 0 } stored) {
                        own.Add(stored.Name);
                    }
                }
            }

            foreach (var (machineFsm, variable) in MachineSetValues(fsm.GameObject.scene)) {
                if (machineFsm == fsm.Name) {
                    own.Add(variable);
                }
            }

            _ownValues[fsm] = own;
        }

        foreach (var action in state.Actions) {
            if (action.Enabled && Entity.Action.EntityFsmActions.ChoosesWhereItGoes(action) &&
                (LooksAtWhatPlayerHas(action) ||
                 Entity.Action.EntityFsmActions.ValueVariablesOf(action).Any(own.Contains))) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an action looks at what the player of the game has: their save (a test of PlayerData) or what they have
    /// on (CheckIfToolEquipped).
    /// </summary>
    private static bool LooksAtWhatPlayerHas(FsmStateAction action) {
        return action.GetType().Name.IndexOf("PlayerData", StringComparison.OrdinalIgnoreCase) >= 0 ||
               action is HutongGames.PlayMaker.Actions.CheckIfToolEquipped;
    }

    /// <summary>
    /// Whether a state chooses its way on by what the player of the game has (see <see cref="LooksAtWhatPlayerHas"/>).
    /// </summary>
    private static bool ChoosesByWhatPlayerHas(FsmState state) {
        return state.Actions.Any(
            action => action.Enabled && Entity.Action.EntityFsmActions.ChoosesWhereItGoes(action) &&
                      LooksAtWhatPlayerHas(action)
        );
    }

    /// <summary>
    /// The variable in which an action stores a value that each game has for itself (see
    /// <see cref="ChoosesByOwnValue"/>), or null for one that stores none.
    /// </summary>
    private static NamedVariable? OwnValueStoredBy(FsmStateAction action) {
        return action switch {
            HutongGames.PlayMaker.Actions.GetChildCount get => get.storeResult,
            HutongGames.PlayMaker.Actions.GetHP get => get.storeValue,
            HutongGames.PlayMaker.Actions.GetPlayerDataBool get => get.storeValue,
            HutongGames.PlayMaker.Actions.GetPlayerDataInt get => get.storeValue,
            HutongGames.PlayMaker.Actions.GetPlayerDataFloat get => get.storeValue,
            HutongGames.PlayMaker.Actions.GetFsmBool get when ReadsAnother(get.gameObject) => get.storeValue,
            HutongGames.PlayMaker.Actions.GetFsmInt get when ReadsAnother(get.gameObject) => get.storeValue,
            HutongGames.PlayMaker.Actions.GetFsmFloat get when ReadsAnother(get.gameObject) => get.storeValue,
            _ => null
        };
    }

    /// <summary>
    /// Whether an action that reads a variable of a state machine reads one of another object than its own.
    /// </summary>
    private static bool ReadsAnother(FsmOwnerDefault owner) {
        return owner.OwnerOption != OwnerDefaultOption.UseOwner;
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

        kept.Add((player, update));
    }

    /// <summary>
    /// A state machine got by itself to the state of a step of the scene host's that it kept, which goes, with those
    /// before it.
    /// </summary>
    private void ReachKeptStep(Fsm fsm, string stateName) {
        if (!_keptOwnSteps.TryGetValue(fsm, out var kept)) {
            return;
        }

        var index = kept.FindIndex(each => each.Step.StateName == stateName);
        if (index < 0) {
            return;
        }

        kept.RemoveRange(0, index + 1);
        if (kept.Count == 0) {
            _keptOwnSteps.Remove(fsm);
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
    /// of 11-21, 21:42:03). The states that it is set through on the way make no noise (see
    /// <see cref="EnterQuietOnTheWay"/>).
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
        _takingKeptSteps = true;
        try {
            // One that is taken may take the state machine on by itself into the state of the next, which then goes
            // (see ReachKeptStep; review of a0d1558: it was taken again)
            while (_keptOwnSteps.TryGetValue(fsm, out kept)) {
                var (player, step) = kept[0];
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
    /// step after it of the creature's state machines and of its parts, as they came, until it is through its move
    /// here (see <see cref="UpdateStepsAfterMove"/>), or is set onto them a second after the first of the steps
    /// before it came (see <see cref="UpdateKeptSteps"/>), and every blow of another player on its creature too (see
    /// CoopHits.WaitsForAMove). Taken
    /// as it came, after the steps before it, all at once, the map seller, still in the air in her stomp here a moment
    /// behind, was put down where the scene host's had landed, 6.7 away, and set into her next move (Luminary's log of
    /// 11-22, 20:39:03.332; USER 11-23 "都说了多少遍了让客机一招演完，怎么还会有沙克拉被直接拉到地上的道理？"). Not one
    /// on a way of its own from a blow or a hurt here, nor one that does not get to the first of the steps before it by
    /// itself any more, whose steps are taken as they came at once (see <see cref="TakeKeptSteps"/>). Whether it keeps
    /// the step.
    /// </summary>
    private bool KeepsForAfterMove(ClientPlayerData player, Fsm fsm, CoopSaveUpdate update) {
        if (_takingKeptSteps || _settingOntoLatest || update.PartCount == OwnToldPart || _ownWays.Contains(fsm) ||
            !_keptOwnSteps.TryGetValue(fsm, out var kept) || !ReachesByItself(fsm, kept[0].Step.StateName)) {
            return false;
        }

        _stepsAfterMove[fsm] = [(player, update)];
        Logger.Info(
            $"[Own creatures] '{update.ObjectPath}' went to '{update.StateName}' of '{fsm.Name}' in " +
            $"{player.Username}'s game, which it does not get to by itself, while it is still on its way here to " +
            $"'{kept[0].Step.StateName}', a step there before it: it plays its move through first"
        );
        return true;
    }

    /// <summary>
    /// Sets each creature that every game runs by itself, one of whose state machines has not got by itself to the
    /// first of the steps of the scene host's that it kept within <see cref="MoveThroughWait"/> of when that step
    /// came, onto the scene host's latest: that state machine onto those steps, and then every step of the creature
    /// that waited for its move (see <see cref="KeepsForAfterMove"/>), as they came, none of them kept to be got to
    /// by itself again nor waiting for a move (USER 11-23 "删掉等人吧，还是最简单的两道，一招演完立刻演下一招，和迟了1秒
    /// 就直接跳到房主传过来最新的状态"), with no noise on the way to the last of them of each state machine (see
    /// <see cref="EnterQuietOnTheWay"/>). The silk boss waited 4.5 s in its lift for a moment of a look that never
    /// came, with the steps there that it was to get to by itself kept, before a step came that it does not get to
    /// by itself, and 4.7 s more as that step and those after it, taken at the second of the move, waited once more
    /// behind the steps kept (the user's log of 11-23, 20:07:35.161 to 20:07:45.367). Not one that plays a move
    /// here by itself (see <see cref="PlaysOwnMove"/>), which goes on from the latest of the steps that come
    /// meanwhile once it is through: taken, the steps that it kept took the place of that one, as they came before
    /// it, and it went on from an older one (review of c0764ad).
    /// </summary>
    private void UpdateKeptSteps() {
        if (_keptOwnSteps.Count == 0) {
            return;
        }

        var now = Time.unscaledTime;
        foreach (var fsm in _keptOwnSteps.Keys.ToList()) {
            // Set onto the latest, another state machine of the creature may have got to its own as well
            if (!_keptOwnSteps.TryGetValue(fsm, out var kept) || now - kept[0].Step.CameAt < MoveThroughWait) {
                continue;
            }

            if (fsm.FsmComponent == null) {
                _keptOwnSteps.Remove(fsm);
                continue;
            }

            if (PlaysOwnMove(fsm)) {
                continue;
            }

            Logger.Info(
                $"[Own creatures] '{kept[0].Step.ObjectPath}' did not get to '{kept[0].Step.StateName}' of " +
                $"'{fsm.Name}' by itself within {MoveThroughWait:F0}s of when it came, and is set onto the scene " +
                "host's latest"
            );
            List<(ClientPlayerData Player, CoopSaveUpdate Step)> after =
                PlaysMoveThrough(fsm.GameObject) is { } waiting && _stepsAfterMove.Remove(waiting, out var queued)
                    ? queued
                    : [];
            // The last word of each state machine of the creature and of its parts, and apart from it its last pick,
            // which tells a part of the room and takes the state machine nowhere (review of 4362d0d: the step into
            // the state that picks, which the creature stayed in, made no sound, as the pick was the last word)
            var lastOf = new Dictionary<(string, string, bool), CoopSaveUpdate>();
            foreach (var (_, step) in kept.Concat(after)) {
                lastOf[(step.ObjectPath, step.FsmName, step.PartCount == OwnPickPart)] = step;
            }

            _lastSteps.UnionWith(lastOf.Values);
            _takingStepsAfterMove = true;
            _settingOntoLatest = true;
            try {
                TakeKeptSteps(fsm);
                foreach (var (player, step) in after) {
                    OnOwnCreature(player, step);
                }
            } finally {
                _takingStepsAfterMove = false;
                _settingOntoLatest = false;
                _lastSteps.Clear();
                _atLastStep.Clear();
            }
        }
    }

    /// <summary>
    /// Whether a state machine of a creature that every game runs by itself plays a move here by itself, a leap, a
    /// strike back, a catch, a talk or the rest of a move after a hurt, or is a part of a creature that does: it goes
    /// on from the latest of the steps of the scene host's that came meanwhile once it is through (see
    /// KeepsForOwnLeap, TakePartStepsAfter).
    /// </summary>
    private bool PlaysOwnMove(Fsm fsm) {
        return _ownLeaps.ContainsKey(fsm) ||
               OwnCreatures.IsPart(fsm.GameObject) && OwnCreatures.Owning(fsm.GameObject) is { } owner &&
               InOwnLeap(owner);
    }

    /// <summary>
    /// Whether a state that a state machine of a creature that every game runs by itself goes to is that of the last
    /// of the scene host's latest words of it that this game sets the creature onto, a pick aside (see
    /// <see cref="_lastSteps"/>).
    /// </summary>
    private bool IsLastStepState(Fsm fsm, FsmState state) {
        var place = OwnCreatures.PlaceOf(fsm.GameObject);
        return _lastSteps.Any(
            last => last.PartCount != OwnPickPart && last.ObjectPath == place && last.FsmName == fsm.Name &&
                    last.StateName == state.Name
        );
    }

    /// <summary>
    /// Takes a word of what a step of the scene host's did there, a catch, a strike back, a hurt of a player or a pick,
    /// which, as the last of the scene host's latest words of its state machine that this game sets the creature onto
    /// (see <see cref="_lastSteps"/>), plays as the game has it, with what it sets off (see
    /// <see cref="EnterQuietOnTheWay"/>).
    /// </summary>
    private void TakeWhatStepDid(CoopSaveUpdate update, Action take) {
        if (!_lastSteps.Contains(update)) {
            take();
            return;
        }

        _playingAsIs++;
        try {
            take();
        } finally {
            _playingAsIs--;
        }
    }

    /// <summary>
    /// Lets a state machine go to a state as the game has it, but while this game sets state machines through steps
    /// of the scene host's one after another in a frame, those that they kept to get to by themselves (see
    /// <see cref="TakeKeptSteps"/>) and those up to the latest, a second behind it (see
    /// <see cref="UpdateKeptSteps"/>): a state on the way, which the next step takes it out of at once, plays no sound
    /// once and shakes no camera (see <see cref="NoisesOnTheWay"/>), as the sounds of all of them would come at once
    /// and their shakes pile up (USER 11-23 "那就把声音和震屏关了"). The last of the latest steps of each state machine
    /// plays as the game has it, from the way into it on, or from where it gets to its state by itself (see
    /// <see cref="_atLastStep"/>), and so do a word of what a step did there that is the last (see
    /// <see cref="TakeWhatStepDid"/>), a move that this game plays by itself, the steps after which wait for its end
    /// (see <see cref="PlaysOwnMove"/>), and what such a state sets off in other state machines as it starts. What a
    /// state on the way sets off in a part stays as quiet as it.
    /// </summary>
    private void EnterQuietOnTheWay(Action<Fsm, FsmState> orig, Fsm fsm, FsmState toState) {
        if (!_takingKeptSteps && !_settingOntoLatest) {
            orig(fsm, toState);
            return;
        }

        if (_playingAsIs > 0 || _atLastStep.Contains(fsm) || PlaysOwnMove(fsm)) {
            _playingAsIs++;
            try {
                orig(fsm, toState);
            } finally {
                _playingAsIs--;
            }

            return;
        }

        var muted = new List<FsmStateAction>();
        foreach (var action in toState.Actions) {
            if (action.Enabled && NoisesOnTheWay.Contains(action.GetType().Name)) {
                action.Enabled = false;
                muted.Add(action);
            }
        }

        try {
            orig(fsm, toState);
        } finally {
            foreach (var action in muted) {
                action.Enabled = true;
            }
        }
    }

    /// <summary>
    /// Has each state machine that plays its move through here take the steps of the scene host's that came meanwhile
    /// (see <see cref="KeepsForAfterMove"/>), as they came, a frame after it got by itself to the last of the steps
    /// before them, outside of the step of its state machine (USER 11-23 "一招演完立刻演下一招"). One that went on a
    /// way of its own from a blow or a hurt here meanwhile, or that does not get to the first of those by itself any
    /// more, takes them now as well, after those, as before (see <see cref="TakeKeptSteps"/>), and one that does not
    /// get to those in time is set onto them and these (see <see cref="UpdateKeptSteps"/>). Where one of them is a
    /// step that a state machine of the creature gets to by itself again, the rest wait once more.
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

            var onItsWay = _keptOwnSteps.TryGetValue(fsm, out var kept);
            if (onItsWay && !_ownWays.Contains(fsm) && ReachesByItself(fsm, kept![0].Step.StateName)) {
                continue;
            }

            _stepsAfterMove.Remove(fsm);
            var steps = pair.Value;
            var why = !onItsWay
                ? $"is through its move in '{fsm.Name}' here, {Time.unscaledTime - steps[0].Step.CameAt:F2}s " +
                  "after the first of them came"
                : _ownWays.Contains(fsm) ? $"went on a way of its own in '{fsm.Name}' here"
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
                    if (PlaysMoveThrough(fsm.GameObject) is { } again) {
                        _stepsAfterMove[again].AddRange(steps.Skip(i + 1));
                        break;
                    }
                }
            } finally {
                _takingStepsAfterMove = false;
            }
        }
    }

    /// <summary>
    /// The state machine of a creature that every game runs by itself, which an object is or is a part of, that plays
    /// a move through here before steps of the scene host's that came meanwhile, behind which every step of the
    /// creature and every blow of another player on it waits (see <see cref="KeepsForAfterMove"/>;
    /// CoopHits.WaitsForAMove), or null.
    /// </summary>
    private Fsm? PlaysMoveThrough(GameObject of) {
        if (_stepsAfterMove.Count == 0 || OwnCreatures.Owning(of) is not { } creature) {
            return null;
        }

        foreach (var fsm in _stepsAfterMove.Keys) {
            if (fsm.GameObject != null && OwnCreatures.Owning(fsm.GameObject) == creature) {
                return fsm;
            }
        }

        return null;
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
