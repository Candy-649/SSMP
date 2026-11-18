using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
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
    /// The state machines of creatures that every game runs by itself that took a step here that a blow set off, of
    /// this game's player or another's as it came, and go on from there by themselves until they next wait for a step
    /// of the scene host's (see TakeOwnStep).
    /// </summary>
    private readonly HashSet<Fsm> _ownWays = new();

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
                    PutAtSceneHostsStep(self, _wayInStep);
                }
            } else if (_wayInLoop != null) {
                _wayInStates.Add(toState.Name);
            }

            _heldOwnSteps.Remove(self);
            _ownWays.Remove(self);

            // The step itself may be a leap at this game's player or a talk, which this game plays from there; a state
            // on the way into it is neither, and does what it does as it is entered but for a line of a talk or a
            // prompt, which would take this game's player into a talk that the way leaves at once (review: a member
            // who came in after the talk went through it on the way, and played every step after it by itself)
            if (isTheStep) {
                if (!StartsOwnLeap(machine, toState)) {
                    StartsOwnTalk(machine, toState);
                }

                EnterOwnState(orig, self, toState);
                return;
            }

            var muted = Entity.Entity.RunsTalk(toState) ? MuteTalk(toState) : null;
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

        // A step that goes by where the players are or by a count that each game keeps for itself, or that another
        // state machine told it, is the scene host's: it waits for that, which has mostly come already, as this game
        // is a moment behind. So is a step into a state that tells a part of the room which it picks by where a player
        // is, after which the scene host's game tells which part it picked (see CoopSave.OwnPicks). The switch that the
        // game was to make is let go of, as the game tries it again at once until it is made. A leap at this game's
        // player is this game's to play, to its end (see CoopSave.OwnCatches), and so is a talk, at the end of which
        // it waits for the scene host's to come as far (see CoopSave.OwnTalks). So is what a strike of this game's
        // player sets off as it is made, as in a game of one: a wound, a block or a tink, and what they tell the other
        // state machines of the creature at once, though not where a state that it went into chooses its way on by
        // where the players are or by a count, which stays the scene host's. The scene host's game is told of the
        // strike as it was taken here, and its creature answers it as the game does, which every other game takes as a
        // step of the scene host's (USER 11-17 "如果这个会有反馈，我这边应该先演，然后通知房主，让房主那边的也触发这个弹
        // 开", "本地化就是为了让自己的怪是活的，可以触发各种反应"; a blade of the boss at the end of the second act that the
        // player tinked did nothing, as it went by where the players are, until the scene host's step came)
        var waitedFor = IsWaitedFor(self);
        var setOffByStrike = CoopHits.IsMakingLocalStrike && Fsm.EventData?.SentByFsm != self;
        var picks = PicksAPart(toState);
        if (OwnCreatures.FollowsSceneHost(creature) && !PlaysOwnLeap(machine, toState) &&
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

        // A step that a blow sets off here, of this game's player or another's as it comes, and the steps that its
        // state machine takes from there by itself, come at another moment of its way than in the scene host's game,
        // after other rolls: the scene host's are found among them by their states (see TakeOwnStep)
        _heldOwnSteps.Remove(self);
        if (CoopHits.IsMakingLocalStrike || CoopHits.IsReplayingPartnersStrike) {
            _ownWays.Add(self);
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

        // A part tells only the steps that the others wait for, which it took by where the players are, by a count or
        // as it was told, or into a state that picks a part of the room: the rest of them the others take by
        // themselves, and a part steps on many times in each move
        if (IsSceneHost?.Invoke() == true && (!OwnCreatures.IsPart(creature) || waitedFor || picks)) {
            Logger.Info(
                $"[Own creatures] '{OwnCreatures.PlaceOf(creature)}' goes from '{self.ActiveStateName}' to " +
                $"'{toState.Name}' of '{self.Name}' after {step.Counts.Count / 2} kinds of rolls, as the members in " +
                "the room are told"
            );
            SendToRoomMembers(OwnCreatureUpdate(machine, step));
        }

        EnterOwnState(orig, self, toState);
        TakeEarlyCatch(self, toState);
    }

    /// <summary>
    /// Hook for the death of a creature, which tells the members in the room that a creature that every game runs by
    /// itself died here, unless it died as another game told.
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
            !OwnCreatures.MarkDead(self.gameObject)) {
            return;
        }

        var creature = self.gameObject;
        var place = OwnCreatures.PlaceOf(creature);
        Logger.Info($"[Own creatures] '{place}' died here, as the members in the room are told");
        SendToRoomMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.OwnCreature,
            Scene = creature.scene.name,
            ObjectPath = place,
            PartCount = 2,
            Values = attackDirection is { } direction ? [direction] : []
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
    /// The word of a step that a creature that every game runs by itself took here, with how fast it went, what the
    /// state that it leaves worked with and where its walk is (see GamePatcher.WriteWalk), which a game that waits for
    /// the step takes it with: a boss reckons the speed of its jump from where the player it goes after stands, which
    /// each game has a little elsewhere, and jumped as far as its own game had it, to be moved where the scene host's
    /// landed as it landed (USER 11-15, of that fight, "只有一步闪了一下，其他时候都很丝滑").
    /// </summary>
    private static CoopSaveUpdate OwnCreatureUpdate(PlayMakerFSM machine, OwnStep step) {
        var transform = machine.transform;
        var speed = machine.TryGetComponent<Rigidbody2D>(out var body) ? body.linearVelocity : Vector2.zero;
        var fsm = machine.Fsm;

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
                SharedDice.SongOf(machine.gameObject), speed.x, speed.y
            ],
            Bytes = stream.ToArray()
        };
    }

    /// <summary>
    /// Tells a member who has come into the room where each creature that every game runs by itself is here and what
    /// it does, or that it is dead, if this game is the scene host's. Their game has them where the room starts them.
    /// </summary>
    /// <param name="player">The member.</param>
    private void SendOwnCreaturesTo(ClientPlayerData player) {
        if (OwnCreatures.Count == 0 || !_checkedMembers.Contains(player.Id) || IsSceneHost?.Invoke() != true) {
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
    /// itself, or of a part of one, is in here, and the creature's health.
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

        // Their own has all its health, which the blows before they came took here
        if (health != null) {
            update.Values.Add(health.hp);
        }

        Logger.Info(
            $"[Own creatures] {player.Username} is told that '{OwnCreatures.PlaceOf(thing)}' is in " +
            $"'{fsm.ActiveStateName}' of '{fsm.Name}' here" + (health != null ? $", with {health.hp} health" : "")
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

            Logger.Info($"[Own creatures] '{place}' died in {player.Username}'s game and dies here too");
            OwnCreatures.AwardJournalKill(health);
            _dyingAsTold = true;
            try {
                health.Die(
                    update.Values.Count > 0 ? update.Values[0] : null, AttackTypes.Generic, NailElements.None, null,
                    true, 1f, false, false
                );
            } finally {
                _dyingAsTold = false;
            }

            return;
        }

        // A part of its own caught the player of that game, which plays the catch by itself
        if (update.PartCount == OwnCatchPart) {
            TakeOwnCatch(player, creature, update);
            return;
        }

        // The player of that game started a talk with it, which that game plays by itself
        if (update.PartCount == OwnTalkPart) {
            TakeOwnTalk(player, creature, update);
            return;
        }

        // It struck back at a blow of the player of that game that it blocked there, which that game plays by itself
        if (update.PartCount == OwnStrikeBackPart) {
            TakeOwnStrikeBack(player, creature, update);
            return;
        }

        // It told a part of the room there that it picked by where a player is, which it tells here too
        if (update.PartCount == OwnPickPart) {
            TakeOwnPick(player, creature, update);
            return;
        }

        // Its health came to none there, after which the next blow, of any player, plays here as the game has it
        if (update.PartCount == OwnEmptiedPart && hasHealth) {
            TakeOwnEmptied(player, health);
            return;
        }

        // A step of the scene host's while this game plays a leap at its own player or a talk by itself (see
        // CoopSave.OwnCatches, CoopSave.OwnTalks)
        var fsm = PlayMakerFSM.FindFsmOnGameObject(creature, update.FsmName).Fsm;
        if (KeepsForOwnLeap(player, fsm, update)) {
            return;
        }

        // A step of a part while its creature here plays a move by itself: the part goes on as the creature tells it
        // here, and from the latest of these once the creature is through (see TakePartStepsAfter)
        if (OwnCreatures.IsPart(creature) && OwnCreatures.Owning(creature) is { } owner && InOwnLeap(owner)) {
            _partStepsInLeap[fsm] = (player, update);
            Logger.Info(
                $"[Own creatures] '{place}' went to '{update.StateName}' of '{fsm.Name}' in {player.Username}'s " +
                "game, while its creature plays a move here by itself, and goes on as the creature tells it here " +
                "until it is through"
            );
            return;
        }

        // Whom it goes after there, which nothing here chooses for it
        if (update.Part != ThrownThings.NoPlayer && ThrownThings.FigureOf?.Invoke(update.Part, player.Id) is
                { } target && target != null) {
            GamePatcher.TakeSceneHostsTarget(creature, target);
        }

        // The health that it has there, for a member who has just come in
        if (update.Values.Count > 7 && hasHealth) {
            if (health.hp != (int) update.Values[7]) {
                Logger.Info(
                    $"[Own creatures] '{place}' has {(int) update.Values[7]} health as in {player.Username}'s game, " +
                    $"rather than {health.hp}"
                );
                health.hp = (int) update.Values[7];
            }

            OwnThreads.TookHealth(creature);
        }

        var transform = creature.transform;
        var there = new Vector3(update.Values[0], update.Values[1], transform.position.z);
        if (TakeOwnStep(fsm, update, out var newest) is { } took) {
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
                    $"game, which it played here already, as a blow set it off, {ahead.Count / 2} kinds of rolls " +
                    "later there"
                );
                return;
            }

            // Where it stood as it took the step is its own: a creature is put where the scene host's is only as it
            // makes a move, which a step that the others wait for tells, and goes on from there by itself, as fast as
            // there, through its knockbacks, flights and landings (USER 11-18 "不如位置校准只在出招时做", "因为出招的时候
            // 必定携带位置信息")
            var off = there - took.Place;
            off.z = 0f;
            if (off.magnitude > OwnCreatureDrift) {
                Logger.Info(
                    $"[Own creatures] '{place}' went to '{update.StateName}' as in {player.Username}'s game, " +
                    $"{off.magnitude:F1} away from where it did there, and goes on from where it is to its next move"
                );
            }

            // A member who has just come in: its walk goes on where the scene host's walks now, rather than from the
            // fresh start that the room gave it as it came in, which took it to the same step within a few frames
            // (review)
            if (newest && update.Values.Count > 7) {
                using var reader = new BinaryReader(new MemoryStream(update.Bytes));
                ToldValues.Read(reader);
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
        // 20:42:05.971, 3.1 away). A member who has just come in is put where it is there, wherever their own is
        if (update.PartCount != OwnToldPart && ReachesByItself(fsm, update.StateName)) {
            Logger.Info(
                $"[Own creatures] '{place}' went to '{update.StateName}' of '{fsm.Name}' in {player.Username}'s " +
                $"game, which it gets to here by itself from '{fsm.ActiveStateName}'"
            );
            return;
        }

        // Put where the scene host's was as it makes a move; a state machine that makes no move of it leaves it where
        // it is (see MakesMoves)
        var way = WayInto(fsm, update.StateName);
        var from = fsm.ActiveStateName;
        var moves = MakesMoves(fsm);
        var away = Vector2.Distance(transform.position, there);
        if (moves) {
            MoveOwnSteps(creature, there - transform.position);
        }

        PutAtSceneHostsStep(fsm, update);
        OwnCreatures.SceneHostsStep = (fsm, update.StateName);
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
        }

        var how = way != null ? $"by '{way.Name}'"
            : _wayInStates.Count > 0 ? $"through '{string.Join("', '", _wayInStates)}'"
            : "straight";
        Logger.Info(
            $"[Own creatures] '{place}' goes from '{from}' to '{update.StateName}' {how} as in {player.Username}'s " +
            (moves ? $"game, where it was there, {away:F1} away" : "game, where it is here")
        );
    }

    /// <summary>
    /// Puts a creature that every game runs by itself where the scene host's was as it took a step, turned the way it
    /// was turned there, going as fast, with the rolls and the song that it took the step with, with what the state
    /// that its state machine left worked with there, and with its walk where it was there (see GamePatcher.PutWalk).
    /// A state machine that makes no move of it, like the count of the knight's stun, takes the step where the creature
    /// is here (see <see cref="MakesMoves"/>).
    /// </summary>
    private void PutAtSceneHostsStep(Fsm fsm, CoopSaveUpdate update) {
        var creature = fsm.GameObject;
        using var reader = new BinaryReader(new MemoryStream(update.Bytes));
        var worked = ToldValues.Read(reader);
        SharedDice.RollByName(creature, OwnCreatures.RollName(creature));
        if (MakesMoves(fsm)) {
            // The walk first: the game's own steps of it turn the creature and set it going, which the rest then puts
            // as it was there
            GamePatcher.PutWalk(reader, creature);
            var transform = creature.transform;
            transform.position = new Vector3(update.Values[0], update.Values[1], transform.position.z);
            var scale = transform.localScale;
            scale.x = update.Values[2];
            transform.localScale = scale;
            transform.rotation = Quaternion.Euler(0f, 0f, update.Values[3]);
            if (creature.TryGetComponent<Rigidbody2D>(out var body)) {
                body.linearVelocity = new Vector2(update.Values[5], update.Values[6]);
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
                fsm.SetState(update.StateName);
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
    /// <see cref="PlayerWayActionTypes"/>), or goes on by what such an action of its state machine stored, like the
    /// look of a boss before it charges at whether the player it goes after hid, which it saw a state before.
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
            if (action.Enabled && (PlayerWayActionTypes.Contains(action.GetType().Name) ||
                                   Entity.Action.EntityFsmActions.DecidesWhereItGoes(action) &&
                                   Entity.Action.EntityFsmActions.ValueVariablesOf(action).Any(stored.Contains))) {
                return true;
            }
        }

        return false;
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
    /// that way, or as one of them picks one of its ways at random.
    /// </summary>
    private static bool TakenByItself(FsmState state, FsmTransition transition) {
        if (transition.EventName == FsmEvent.Finished.Name) {
            return true;
        }

        foreach (var action in state.Actions) {
            if (!action.Enabled) {
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
            if (action.Enabled && Entity.Action.EntityFsmActions.DecidesWhereItGoes(action) &&
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
            fsm.SetState(pair.Value);
        }

        _heldOwnSteps.Clear();
    }

    /// <summary>
    /// The event by which a state machine goes from the state it is in to the given state, the way the game has it:
    /// straight, or through a state that does its part and finishes into it, like the step that stops a crab's walk
    /// before its song. Null when no way out of its state, nor out of every state, leads there.
    /// </summary>
    private static FsmEvent? WayInto(Fsm fsm, string target) {
        // A way out that leads nowhere, which the game's state machines have, is no way there
        foreach (var transition in fsm.ActiveState.Transitions.Concat(fsm.GlobalTransitions)) {
            if (fsm.GetState(transition.ToState) is { } next && (next.Name == target ||
                    next.Transitions is [var only] && only.EventName == FsmEvent.Finished.Name &&
                    only.ToState == target)) {
                return transition.FsmEvent;
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

        var index = steps.FindIndex(
            step => step.State == update.StateName && step.Counts.SequenceEqual(update.Amounts)
        );

        // One that it took on a way of its own from a blow came at another moment of its way here, after other rolls
        // than there: it is the first of the same state (USER 11-17 "房主那边如果自己还没打到，客机打到了就演，自己已经
        // 打到了那就已经演了", "房主传过来同理")
        if (index < 0) {
            index = steps.FindIndex(step => step.OwnWay && step.State == update.StateName);
        }

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
