using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Machines of a room that run by themselves on a clock of their own, in a checked two-player save: boxes that drop to
/// be stood on and are ground away, chutes that pour debris, and grinders that smash back and forth. Each game ran its
/// own from the moment its room loaded, so the two players saw them at different points of their rounds and stood on
/// boxes that weren't there for the other. A player that a grinder caught had their own grinders go back to where they
/// began as they were put back at the last safe place, and only theirs. The user decided (10-06) that both games show
/// these machines the way the scene host's game has them, and that being caught no longer sends them back.
///
/// The scene host runs its own and tells the other game each time one of them starts a round, with the dice the round
/// rolled. The other game runs its own copy too, but holds each round back until the host says that round started: the
/// machine waits where it is, at the end of its last round, and then goes the way the host's went. A word that comes
/// while the machine is still in the middle of a round is kept and taken where the round ends. A grinder is also put
/// where the host's is and given the host's next way to go, since a grinder stopped by a catch in one game would go on
/// from somewhere else. Machines that a controller sets going, the boxes and chutes of a pattern, start their rounds on
/// the host's word as well, so they keep to the host's even where the controller here is late.
///
/// A grinder that catches a player stops, with the grinders that go with it, until the player is put back at the last
/// safe place, and that only happened in the game of the player it caught. The user decided (10-06) that whoever is
/// caught, they stop in both games: the other game stops the same grinders where they are, and they go on in both once
/// the caught player's go on. A player who went down instead of being put back is never put back, so their grinders go
/// on as they go down, the way enemies go on while a player lies waiting to be stood up (USER 10-06): they can't hurt a
/// player who lies there.
///
/// The controller of a fight is paced the same way, but in every state rather than at the starts of rounds (see
/// CoopSave.DirectedFights).
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How long a machine waits here for the scene host's word before it starts its round by itself, in seconds. Longer
    /// than any round, so that it only happens when the host's machine has stopped going: its own clock is then the best
    /// there is. The controller of a fight waits for as long as it takes (see CoopSave.DirectedFights).
    /// </summary>
    private const float MachineHoldLimit = 10f;

    /// <summary>
    /// How close, in units, a grinder may be put to the local player when it is put where the scene host's is: one that
    /// would land closer stays where it is, since landing on the player would catch them.
    /// </summary>
    private const float GrinderPutClearance = 0.5f;

    /// <summary>
    /// The state that a grinder goes back to its start from, as the player it caught is put back at the last safe place.
    /// </summary>
    private const string GrinderResetStateName = "Reset";

    /// <summary>
    /// The state in which a grinder picks its next way to go.
    /// </summary>
    private const string GrinderNextWayStateName = "Get Dir";

    /// <summary>
    /// The state that a grinder goes to as it catches the local player, which tells the grinders that go with it to stop.
    /// </summary>
    private const string GrinderCatchStateName = "Buddy?";

    /// <summary>
    /// The FSM of the part of a grinder that notices the local player in it and catches them.
    /// </summary>
    private const string GrinderCatcherFsmName = "Multihitter";

    /// <summary>
    /// The state in which the part of a grinder that catches the local player starts to, as they are in it. It catches
    /// even a player who can't be hurt.
    /// </summary>
    private const string GrinderCatcherStartStateName = "Start Hit?";

    /// <summary>
    /// The state in which a grinder that caught the local player waits for them to be put back at the last safe place.
    /// </summary>
    private const string GrinderHazardHitStateName = "Hazard Hit";

    /// <summary>
    /// The state in which a grinder that was told to stop waits to be told to go on.
    /// </summary>
    private const string GrinderStoppedStateName = "Buddy Stop";

    /// <summary>
    /// The event that stops a grinder where it is, which a grinder that caught the player sends the ones that go with it.
    /// </summary>
    private const string GrinderStopEventName = "BUDDY STOP";

    /// <summary>
    /// The event that sets a stopped grinder going again, by way of its state that sends it on.
    /// </summary>
    private const string GrinderGoEventName = "GO";

    /// <summary>
    /// The event that a grinder that caught the local player waits for, which the game sends everything as it puts the
    /// player back at the last safe place.
    /// </summary>
    private const string GrinderPutBackEventName = "HAZARD RELOAD";

    /// <summary>
    /// How long grinders stopped for the partner wait for them to go on, in seconds: longer than a grinder holds a
    /// player it caught, so that only a word that was lost makes them go on by themselves.
    /// </summary>
    private const float PartnerGrinderStopLimit = 10f;

    /// <summary>
    /// The FSM of a grinder.
    /// </summary>
    private const string GrinderFsmName = "Control";

    /// <summary>
    /// The variables of a grinder that hold the grinders that go with it, which it stops as it catches the player.
    /// </summary>
    private static readonly string[] GrinderGroupVariableNames = ["Buddy", "Buddy 2", "Boss"];

    /// <summary>
    /// The states of a grinder catching the local player, which a stop for the partner leaves alone.
    /// </summary>
    private static readonly HashSet<string> GrinderCatchStateNames =
        new(StringComparer.Ordinal) { GrinderCatchStateName, "Multihitting", GrinderHazardHitStateName };

    /// <summary>
    /// The machines that the scene host paces, each named by what its objects are called and by the states its rounds
    /// start with.
    /// </summary>
    private static readonly MachineKind[] MachineKinds = [
        // Controllers that drop boxes and pour chutes in a pattern, which way going by where the players are
        new(["Grind Plat Control"], "Control", ["Pattern R", "Pattern L", "State 2", "State 3", "State 4"]),

        // The boxes: those of a controller drop when it says, the others by themselves. Which of three looks each
        // round has is rolled.
        new(["Understore Grind Plat"], "Control", ["Drop Antic", "Choose Sprite"]),

        // Chutes that pour debris, by themselves or when a controller says. Only the pitch of their sound is rolled.
        new(["junk_chute_"], "Control", ["Flow"]),

        // Grinders, in groups that go together, which go through their list of ways without rolling anything
        new(["Understore_Grinder"], "Control", [GrinderNextWayStateName], isGrinder: true),

        // The controller of the fight with the two dancers, which the other game follows in every state from the first
        // beat on (see CoopSave.DirectedFights). The start of the fight before that, with the gates, the lights and the
        // beat setting off, each game has by itself.
        new(
            ["Dancer Control"], "Control", [], sceneName: "Cog_Dancers_boss", ownStateNames: [
                "Init", "Deactivate Positions", "Encountered?", "Dormant", "Gate Close", "Light Open",
                "Beat Start Pause", "Pendulum Prepare", "Beat Start"
            ]
        )
    ];

    /// <summary>
    /// The states of <see cref="MachineKinds"/> that a change of state is asked about at all, kept as a set because
    /// the hook this serves is called for every change of state of every state machine in the game.
    /// </summary>
    private static readonly HashSet<string> MachineStateNames = BuildMachineStateNames();

    /// <summary>
    /// The FSMs of the controllers of fights among <see cref="MachineKinds"/>, which a change of state is asked about
    /// in every state.
    /// </summary>
    private static readonly HashSet<string> DirectorFsmNames = new(
        MachineKinds.Where(kind => kind.IsDirector).Select(kind => kind.FsmName), StringComparer.Ordinal
    );

    /// <summary>
    /// The machines here that wait for the scene host's word: the state each waits in, the state it was about to start
    /// its round with, and since when it waits, in unscaled time.
    /// </summary>
    private readonly Dictionary<Fsm, (FsmState From, FsmState To, float Since)> _heldMachines = new();

    /// <summary>
    /// The scene host's words that came while their machine here was in the middle of a round, the newest for each,
    /// which the machine takes as the round ends.
    /// </summary>
    private readonly Dictionary<Fsm, CoopSaveUpdate> _pendingMachineRounds = new();

    /// <summary>
    /// The number of the newest word taken for each machine from each member, whose words count up by themselves, so
    /// that a word that comes late, after a newer one, is dropped.
    /// </summary>
    private readonly Dictionary<(Fsm, ushort), ulong> _takenMachineRounds = new();

    /// <summary>
    /// The machines that were said in the log to follow the scene host, so that it is said once for each.
    /// </summary>
    private readonly HashSet<Fsm> _machinesSaid = [];

    /// <summary>
    /// The machine whose round of the scene host's is being started here, which is let through.
    /// </summary>
    private Fsm? _startingMachineRound;

    /// <summary>
    /// The state of the scene host's word that the controller of a fight in <see cref="_startingMachineRound"/> goes
    /// into here, which alone is let through: anything it would go on to by itself in the same breath is held as ever.
    /// Null for the other machines, which are let through in any state while their round starts.
    /// </summary>
    private FsmState? _startingMachineState;

    /// <summary>
    /// The grinders that caught the local player and haven't gone on yet.
    /// </summary>
    private readonly HashSet<Fsm> _caughtGrinders = [];

    /// <summary>
    /// The grinders here that stopped because the same grinder caught the partner in their game, with when, in unscaled
    /// time. Each goes on when the partner's does, sending the grinders that go with it on too.
    /// </summary>
    private readonly Dictionary<Fsm, float> _grindersStoppedForPartner = new();

    /// <summary>
    /// The number of the last word about a machine that this game said: a round as scene host, or a grinder that caught
    /// the local player stopping or going on. It starts from the clock, so that the words of a game that was started
    /// again come after those it said before.
    /// </summary>
    private ulong _machineRoundCount = (ulong) DateTime.UtcNow.Ticks;

    /// <summary>
    /// What a machine changing state does in the hook on changes of FSM state.
    /// </summary>
    private enum MachineStep {
        /// <summary>
        /// Nothing of this: the change goes on as usual.
        /// </summary>
        None,

        /// <summary>
        /// The scene host's machine starts a round, which goes to the other game with its dice.
        /// </summary>
        Tell,

        /// <summary>
        /// The machine here starts a round by itself, which waits for the scene host's word.
        /// </summary>
        Hold,

        /// <summary>
        /// The machine here starts the round of the scene host's word that came while it was busy.
        /// </summary>
        TakePending,

        /// <summary>
        /// A grinder goes back to its start, which it doesn't any more in a checked save.
        /// </summary>
        StayPut,

        /// <summary>
        /// A grinder that caught the local player goes on: it stays where it is, and the partner's goes on too.
        /// </summary>
        GoOnAfterCatch,

        /// <summary>
        /// A grinder catches the local player, which stops it in the partner's game as well.
        /// </summary>
        Catch,

        /// <summary>
        /// The part of a grinder that catches the local player would catch them while they lie waiting to be stood up,
        /// where nothing can hurt them: it doesn't, and the grinder goes on over them.
        /// </summary>
        SpareLyingPlayer,

        /// <summary>
        /// The controller of a fight here, still in the start of the fight, would be sent into the fight by something
        /// else than the end of its start: it stays where it is (see CoopSave.DirectedFights).
        /// </summary>
        StayInStart
    }

    /// <summary>
    /// Decides what a change of state of a machine of <see cref="MachineKinds"/> does. Asks nothing of the game that
    /// could change it, so that what throws here only leaves the change to go on as usual.
    /// </summary>
    /// <param name="fsm">The FSM that is changing state.</param>
    /// <param name="toState">The state it is changing into.</param>
    /// <param name="kind">The kind of machine, if it is one.</param>
    private MachineStep DecideMachineSwitch(Fsm fsm, FsmState toState, out MachineKind? kind) {
        kind = null;
        if (_checkedMembers.Count == 0 || MachineSwitchToStateField == null) {
            return MachineStep.None;
        }

        // The controller of a fight, which is paced in every state rather than at the starts of rounds
        if (FindDirector(fsm) is { } director) {
            kind = director;
            return DecideDirectorSwitch(fsm, toState, director);
        }

        if (!MachineStateNames.Contains(toState.Name) || fsm == _startingMachineRound ||
            fsm.GameObject is not { } gameObject) {
            return MachineStep.None;
        }

        // The part that catches the player sits under the grinder, and would catch a player lying in its way again and
        // again, stopping the grinders in both games each time
        if (toState.Name == GrinderCatcherStartStateName) {
            if (fsm.Name != GrinderCatcherFsmName || gameObject.transform.parent is not { } grinder ||
                HeroController.instance is not { } hero || !PlayerTargetRegistry.IsPlayerDown(hero.gameObject)) {
                return MachineStep.None;
            }

            kind = FindMachineKind(grinder.gameObject, GrinderFsmName);
            return kind is { IsGrinder: true } ? MachineStep.SpareLyingPlayer : MachineStep.None;
        }

        kind = FindMachineKind(gameObject, fsm.Name);
        if (kind == null) {
            return MachineStep.None;
        }

        // A grinder that caught the local player goes on once they are put back, or once a grinder that went with it
        // and stopped it as well sends it on
        if (kind.IsGrinder && toState.Name == GrinderResetStateName) {
            return fsm.ActiveState?.Name == GrinderHazardHitStateName || _caughtGrinders.Contains(fsm)
                ? MachineStep.GoOnAfterCatch
                : MachineStep.StayPut;
        }

        if (kind.IsGrinder && toState.Name == GrinderCatchStateName) {
            return MachineStep.Catch;
        }

        if (Array.IndexOf(kind.RoundStateNames, toState.Name) < 0 || !IsMemberInRoom()) {
            return MachineStep.None;
        }

        if (IsSceneHost?.Invoke() == true) {
            // A grinder at the end of its list only goes back to the first of its ways and picks again, which is the
            // round that is said
            return kind.IsGrinder && IsAtEndOfWays(toState) ? MachineStep.None : MachineStep.Tell;
        }

        return _pendingMachineRounds.ContainsKey(fsm) ? MachineStep.TakePending : MachineStep.Hold;
    }

    /// <summary>
    /// Whether a grinder going into the state that picks its next way finds its list at its end, the way the game's
    /// action that picks it reckons it: it then picks nothing, starts the list again, and comes back to pick the first.
    /// </summary>
    private static bool IsAtEndOfWays(FsmState nextWayState) {
        if (GetNextWay(nextWayState) is not { array: { } ways } nextWay) {
            return false;
        }

        var index = nextWay.nextItemIndex;
        var startIndex = nextWay.startIndex?.Value ?? 0;
        if (index == 0 && startIndex > 0) {
            index = startIndex;
        }

        if (nextWay.resetFlag is { Value: true }) {
            index = startIndex;
        }

        var endIndex = nextWay.endIndex?.Value ?? 0;
        return index >= ways.Length || (endIndex > 0 && index >= endIndex);
    }

    /// <summary>
    /// Does what <see cref="DecideMachineSwitch"/> decided for a change of state of a machine, letting the change itself
    /// go on at most once.
    /// </summary>
    private void TakeMachineStep(
        MachineStep step,
        MachineKind kind,
        Fsm fsm,
        FsmState toState,
        Action<Fsm, FsmState> orig
    ) {
        switch (step) {
            case MachineStep.Tell:
                TellMachineRound(kind, fsm, toState, orig);
                return;
            case MachineStep.Hold:
                HoldMachineRound(fsm, toState);
                return;
            case MachineStep.TakePending:
                _pendingMachineRounds.Remove(fsm, out var pending);
                if (pending == null || fsm.GetState(pending.StateName) is not { } pendingState) {
                    HoldMachineRound(fsm, toState);
                    return;
                }

                StartMachineRound(kind, fsm, pending, () => orig(fsm, pendingState));
                return;
            case MachineStep.StayPut:
                StayPutOnReset(fsm, toState, orig);
                return;
            case MachineStep.GoOnAfterCatch:
                StayPutOnReset(fsm, toState, orig);
                _caughtGrinders.Remove(fsm);
                // A word for a round that came while it held the player is for a round the scene host's broke off
                DropPendingRounds(GetGrinderGroup(fsm));
                TellPartnerAboutGrinder(CoopSaveUpdateKind.MachineGo, fsm);
                return;
            case MachineStep.SpareLyingPlayer:
                MachineSwitchToStateField?.SetValue(fsm, null);
                return;
            case MachineStep.StayInStart:
                MachineSwitchToStateField?.SetValue(fsm, null);
                CountDirectedState(fsm, DirectedStep.StayedInStart);
                return;
            case MachineStep.Catch:
                orig(fsm, toState);
                _caughtGrinders.Add(fsm);
                TellPartnerAboutGrinder(CoopSaveUpdateKind.MachineStop, fsm);
                return;
            default:
                orig(fsm, toState);
                return;
        }
    }

    /// <summary>
    /// Lets the scene host's machine start a round, and tells the other game with the dice it rolled. A grinder also
    /// says where it is and which of its ways comes next, as it was before the round started.
    /// </summary>
    private void TellMachineRound(MachineKind kind, Fsm fsm, FsmState toState, Action<Fsm, FsmState> orig) {
        CoopSaveUpdate? word = null;
        try {
            var gameObject = fsm.GameObject;
            word = new CoopSaveUpdate {
                TargetId = CoopTargets.Everyone,
                Kind = CoopSaveUpdateKind.MachineRound,
                Scene = gameObject.scene.name,
                ObjectPath = ScenePath.Get(gameObject.transform),
                FsmName = fsm.Name,
                StateName = toState.Name,
                Sequence = ++_machineRoundCount
            };

            if (kind.IsGrinder) {
                var position = gameObject.transform.position;
                word.Values = [position.x, position.y];
                if (GetNextWay(toState) is { } nextWay) {
                    word.Part = (ushort) Mathf.Clamp(nextWay.nextItemIndex, 0, ushort.MaxValue);
                    word.PartCount = (ushort) (nextWay.resetFlag is { Value: true } ? 1 : 0);
                }
            }
        } catch (Exception e) {
            LogInteractionError(e);
            word = null;
        }

        if (word == null) {
            orig(fsm, toState);
            return;
        }

        try {
            word.Amounts = [.. SharedDice.Record(() => orig(fsm, toState))];
        } finally {
            Send(word);
            if (kind.IsDirector) {
                CountDirectedState(fsm, DirectedStep.Told);
            }
        }
    }

    /// <summary>
    /// Keeps a machine here where it is instead of starting a round by itself, until the scene host's word comes. The
    /// state it was about to go to is cleared, as the game clears it once it has gone there, so that the game does not
    /// try again in a loop (see <see cref="Entity.Entity"/>).
    /// </summary>
    private void HoldMachineRound(Fsm fsm, FsmState toState) {
        MachineSwitchToStateField?.SetValue(fsm, null);
        if (!_heldMachines.TryGetValue(fsm, out var held) || held.From != fsm.ActiveState) {
            _heldMachines[fsm] = (fsm.ActiveState, toState, Time.unscaledTime);
        }
    }

    /// <summary>
    /// Takes the scene host's word that a machine started a round: the machine here starts the same round now if it is
    /// where a round can start, and otherwise once its own round ends.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update, which names the machine by its path in its scene.</param>
    private void OnMachineRound(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker) ||
            !_checkedMembers.Contains(player.Id) || IsSceneHost?.Invoke() == true) {
            return;
        }

        if (ScenePath.Find(update.ObjectPath, update.Scene) is not { activeInHierarchy: true } target ||
            FindMachineKind(target, update.FsmName) is not { } kind) {
            return;
        }

        foreach (var playMakerFsm in target.GetComponents<PlayMakerFSM>()) {
            if (playMakerFsm == null || playMakerFsm.FsmName != update.FsmName || playMakerFsm.Fsm is not { } fsm) {
                continue;
            }

            if (_takenMachineRounds.TryGetValue((fsm, player.Id), out var taken) && update.Sequence <= taken) {
                return;
            }

            _takenMachineRounds[(fsm, player.Id)] = update.Sequence;
            if (!TryStartMachineRoundNow(kind, fsm, update)) {
                _pendingMachineRounds[fsm] = update;
            }

            return;
        }
    }

    /// <summary>
    /// Starts the round of a word of the scene host on a machine here, if the machine is where that round can start:
    /// held at the end of its last round, or in a state that goes into the round by itself. The controller of a fight
    /// goes into the host's state from anywhere in the fight.
    /// </summary>
    /// <returns>Whether the round started.</returns>
    private bool TryStartMachineRoundNow(MachineKind kind, Fsm fsm, CoopSaveUpdate update) {
        var isHeld = _heldMachines.TryGetValue(fsm, out var held) && fsm.ActiveState != null &&
                     held.From == fsm.ActiveState;
        if (!isHeld && !GoesStraightTo(fsm.ActiveState, update.StateName) && !IsInDirectedPart(kind, fsm)) {
            return false;
        }

        // A state that the controller of a fight doesn't have is nothing to go into, and leaves it waiting as it was
        if (kind.IsDirector && fsm.GetState(update.StateName) == null) {
            return true;
        }

        _heldMachines.Remove(fsm);
        StartMachineRound(kind, fsm, update, () => fsm.SetState(update.StateName));
        return true;
    }

    /// <summary>
    /// Whether a state goes into another by one of its own ways out.
    /// </summary>
    private static bool GoesStraightTo(FsmState? state, string toStateName) {
        if (state == null) {
            return false;
        }

        foreach (var transition in state.Transitions) {
            if (transition.ToState == toStateName) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Starts a round of the scene host's on a machine here, with the host's dice, letting the change of state through
    /// the hook. A grinder is first put where the host's was and given the host's next way, unless putting it there
    /// would put it on the local player.
    /// </summary>
    private void StartMachineRound(MachineKind kind, Fsm fsm, CoopSaveUpdate update, Action start) {
        if (kind.IsGrinder) {
            try {
                PutGrinderWhereTheHostsIs(fsm, update);
            } catch (Exception e) {
                LogInteractionError(e);
            }
        }

        if (_machinesSaid.Add(fsm)) {
            Logger.Info(
                kind.IsDirector
                    ? $"The fight controller '{update.ObjectPath}' follows the scene host's in every state"
                    : $"The machine '{update.ObjectPath}' follows the rounds of the scene host's"
            );
        }

        if (kind.IsDirector) {
            if (fsm.GetState(update.StateName) is { } state) {
                EnterDirectedState(fsm, state, update.Amounts, start);
                CountDirectedState(fsm, DirectedStep.Followed);
            }

            return;
        }

        var lastStarting = _startingMachineRound;
        _startingMachineRound = fsm;
        try {
            SharedDice.Throw(update.Amounts, start);
        } finally {
            _startingMachineRound = lastStarting;
        }
    }

    /// <summary>
    /// Puts a grinder here where the scene host's was as its round started, and gives it the host's next way to go.
    /// </summary>
    private static void PutGrinderWhereTheHostsIs(Fsm fsm, CoopSaveUpdate update) {
        if (fsm.GetState(GrinderNextWayStateName) is { } nextWayState && GetNextWay(nextWayState) is { } nextWay) {
            nextWay.nextItemIndex = update.Part;
            if (nextWay.resetFlag != null) {
                nextWay.resetFlag.Value = update.PartCount == 1;
            }
        }

        if (update.Values.Count < 2) {
            return;
        }

        var transform = fsm.GameObject.transform;
        var now = transform.position;
        var there = new Vector3(update.Values[0], update.Values[1], now.z);
        if (WouldLandOnLocalPlayer(fsm.GameObject, there - now)) {
            return;
        }

        transform.position = there;
        if (fsm.GameObject.TryGetComponent<Rigidbody2D>(out var body)) {
            body.position = there;
            body.linearVelocity = Vector2.zero;
        }
    }

    /// <summary>
    /// Whether a grinder moved by an offset would have any of its parts, the one that catches the player among them, on
    /// the local player or closer to them than <see cref="GrinderPutClearance"/>.
    /// </summary>
    private static bool WouldLandOnLocalPlayer(GameObject grinder, Vector3 offset) {
        if (HeroController.SilentInstance is not { } hero || hero.col2d is not { } heroCollider) {
            return false;
        }

        var heroBounds = heroCollider.bounds;
        foreach (var collider in grinder.GetComponentsInChildren<Collider2D>()) {
            if (!collider.enabled) {
                continue;
            }

            var bounds = collider.bounds;
            var min = bounds.min + offset;
            var max = bounds.max + offset;
            if (min.x - GrinderPutClearance <= heroBounds.max.x && max.x + GrinderPutClearance >= heroBounds.min.x &&
                min.y - GrinderPutClearance <= heroBounds.max.y && max.y + GrinderPutClearance >= heroBounds.min.y) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The action of a grinder that picks its next way to go, in the state that does that.
    /// </summary>
    private static ArrayGetNext? GetNextWay(FsmState state) {
        foreach (var action in state.Actions) {
            if (action is ArrayGetNext nextWay) {
                return nextWay;
            }
        }

        return null;
    }

    /// <summary>
    /// Lets a grinder that caught the local player stop and send its group on as the game has it, but without going back
    /// to where it began and to the first of its ways: the user decided (10-06) that being caught no longer sends the
    /// machines back, which only ever happened in the game of the player who was caught.
    /// </summary>
    private static void StayPutOnReset(Fsm fsm, FsmState toState, Action<Fsm, FsmState> orig) {
        var switchedOff = new List<FsmStateAction>();
        foreach (var action in toState.Actions) {
            if (action is { Enabled: true } and (SetPosition or SetBoolValue)) {
                action.Enabled = false;
                switchedOff.Add(action);
            }
        }

        try {
            orig(fsm, toState);
        } finally {
            foreach (var action in switchedOff) {
                action.Enabled = true;
            }
        }

        Logger.Info($"The grinder '{fsm.GameObject.name}' stays where it is instead of going back to its start");
    }

    /// <summary>
    /// Takes words of the scene host that waited for their machine here to be ready, and lets machines that wait go by
    /// themselves once this game runs the room or the host's word has been too long in coming.
    /// </summary>
    private void UpdateMachines() {
        UpdateGrinderStops();
        if (_heldMachines.Count == 0 && _pendingMachineRounds.Count == 0) {
            return;
        }

        // Words of a scene host that this game took over from, or of a partner who left, are no longer the room's
        var runsTheRoom = _checkedMembers.Count == 0 || IsSceneHost?.Invoke() == true || !IsMemberInRoom();
        if (runsTheRoom) {
            _pendingMachineRounds.Clear();
            _takenMachineRounds.Clear();
        }

        // Starting a round can start rounds of other machines on the way, which take their own words, so each word is
        // looked up again before it is taken
        foreach (var pair in _pendingMachineRounds.ToList()) {
            var fsm = pair.Key;
            if (!_pendingMachineRounds.TryGetValue(fsm, out var update) || update != pair.Value) {
                continue;
            }

            if (fsm.GameObject == null) {
                _pendingMachineRounds.Remove(fsm);
                continue;
            }

            if (FindMachineKind(fsm.GameObject, fsm.Name) is { } kind &&
                (GoesStraightTo(fsm.ActiveState, update.StateName) || IsInDirectedPart(kind, fsm))) {
                _pendingMachineRounds.Remove(fsm);
                StartMachineRound(kind, fsm, update, () => fsm.SetState(update.StateName));
            }
        }

        if (_heldMachines.Count == 0) {
            return;
        }

        // The controller of a fight waits for the host's word for as long as it takes: the host's tells every state it
        // goes into, so a word that doesn't come means it is still where it was, and a step of its own here would only
        // go another way
        List<Fsm>? released = null;
        foreach (var pair in _heldMachines) {
            var fsm = pair.Key;
            var held = pair.Value;
            if (fsm.GameObject == null || held.From != fsm.ActiveState || runsTheRoom ||
                (Time.unscaledTime - held.Since > MachineHoldLimit && FindDirector(fsm) == null)) {
                (released ??= []).Add(fsm);
            }
        }

        if (released == null) {
            return;
        }

        foreach (var fsm in released) {
            // Looked up again, since the rounds started on the way may have changed what is held
            if (!_heldMachines.Remove(fsm, out var held)) {
                continue;
            }

            var (from, state, _) = held;

            // One that something else took on in the meantime, like a grinder stopped by a catch, waits no more
            if (fsm.GameObject == null || from != fsm.ActiveState) {
                continue;
            }

            Logger.Info(
                runsTheRoom
                    ? $"The machine '{fsm.GameObject.name}' goes by itself, since this game runs the room now"
                    : $"The machine '{fsm.GameObject.name}' goes by itself, since the scene host's word didn't come"
            );

            // A machine of a game that now runs the room goes through the hook as any other, so that the round is said
            // to a partner in the room; one that only gave up on the word is let through
            if (FindDirector(fsm) != null) {
                try {
                    EnterDirectedState(fsm, null, null, () => fsm.SetState(state.Name));
                } catch (Exception e) {
                    LogInteractionError(e);
                }

                continue;
            }

            var lastStarting = _startingMachineRound;
            if (!runsTheRoom) {
                _startingMachineRound = fsm;
            }

            try {
                fsm.SetState(state.Name);
            } catch (Exception e) {
                LogInteractionError(e);
            } finally {
                _startingMachineRound = lastStarting;
            }
        }
    }

    /// <summary>
    /// Forgets the machines of the room that was left.
    /// </summary>
    private void ResetMachines() {
        _heldMachines.Clear();
        _pendingMachineRounds.Clear();
        _takenMachineRounds.Clear();
        _machinesSaid.Clear();
        _caughtGrinders.Clear();
        _grindersStoppedForPartner.Clear();
        _directors.Clear();
        LogDirectedStates();
    }

    /// <summary>
    /// Tells the members in the room that a grinder caught the local player and stopped, or that it goes on again.
    /// </summary>
    private void TellPartnerAboutGrinder(CoopSaveUpdateKind kind, Fsm fsm) {
        if (!IsMemberInRoom() || fsm.GameObject is not { } gameObject) {
            return;
        }

        // The change of state already went on, so what throws here must not reach the game
        try {
            SendToMembers(new CoopSaveUpdate {
                Kind = kind,
                Scene = gameObject.scene.name,
                ObjectPath = ScenePath.Get(gameObject.transform),
                FsmName = fsm.Name,
                Sequence = ++_machineRoundCount
            });
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Takes the partner's word that a grinder caught them: the same grinder here stops where it is, with the grinders
    /// that go with it, as the game stops them in the partner's game. One that is catching the local player is left
    /// to it.
    /// </summary>
    private void OnMachineStop(ClientPlayerData player, CoopSaveUpdate update) {
        if (FindPartnerGrinder(player, update) is not { } fsm || IsOldMachineWord(fsm, update)) {
            return;
        }

        foreach (var grinder in GetGrinderGroup(fsm)) {
            if (grinder.ActiveState?.Name is { } state && GrinderCatchStateNames.Contains(state)) {
                continue;
            }

            // A word for a round that came before the stop is old by the time it goes on
            _pendingMachineRounds.Remove(grinder);
            grinder.Event(GrinderStopEventName);
        }

        if (fsm.ActiveState?.Name == GrinderStoppedStateName) {
            _grindersStoppedForPartner[fsm] = Time.unscaledTime;
            Logger.Info($"The grinder '{update.ObjectPath}' stops here too, since it caught {player.Username}");
        }
    }

    /// <summary>
    /// Takes the partner's word that a grinder that caught them goes on: the same grinder here goes on, and sends the
    /// grinders that go with it on as the game does.
    /// </summary>
    private void OnMachineGo(ClientPlayerData player, CoopSaveUpdate update) {
        if (FindPartnerGrinder(player, update) is not { } fsm || IsOldMachineWord(fsm, update) ||
            !_grindersStoppedForPartner.Remove(fsm) || fsm.ActiveState?.Name != GrinderStoppedStateName) {
            return;
        }

        // A word for a round that came while it was stopped is for a round the scene host's broke off
        DropPendingRounds(GetGrinderGroup(fsm));
        Logger.Info($"The grinder '{update.ObjectPath}' goes on here too, since it let {player.Username} go");
        fsm.Event(GrinderGoEventName);
    }

    /// <summary>
    /// Whether a word of the partner about a machine is older than one already taken for it, which a word sent again
    /// late can be: a stop that comes after the go would stop the grinder with nothing left to send it on. Takes the
    /// word's number otherwise.
    /// </summary>
    private bool IsOldMachineWord(Fsm fsm, CoopSaveUpdate update) {
        if (_takenMachineRounds.TryGetValue((fsm, update.PlayerId), out var taken) && update.Sequence <= taken) {
            return true;
        }

        _takenMachineRounds[(fsm, update.PlayerId)] = update.Sequence;
        return false;
    }

    /// <summary>
    /// A grinder with the grinders that go with it, which it stops as it catches the player and sends on after.
    /// </summary>
    private static List<Fsm> GetGrinderGroup(Fsm fsm) {
        var group = new List<Fsm> { fsm };
        foreach (var name in GrinderGroupVariableNames) {
            if (fsm.Variables.GetFsmGameObject(name)?.Value is { } other &&
                FindMachineFsm(other, fsm.Name) is { } otherFsm && !group.Contains(otherFsm)) {
                group.Add(otherFsm);
            }
        }

        return group;
    }

    /// <summary>
    /// Forgets the scene host's words that waited for machines to take them.
    /// </summary>
    private void DropPendingRounds(List<Fsm> machines) {
        foreach (var machine in machines) {
            _pendingMachineRounds.Remove(machine);
        }
    }

    /// <summary>
    /// Finds the grinder here that a word of the partner names, if the word comes from the partner of the checked save
    /// and the grinder is in the room.
    /// </summary>
    private Fsm? FindPartnerGrinder(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker) ||
            !_checkedMembers.Contains(player.Id)) {
            return null;
        }

        if (ScenePath.Find(update.ObjectPath, update.Scene) is not { activeInHierarchy: true } target ||
            FindMachineKind(target, update.FsmName) is not { IsGrinder: true }) {
            return null;
        }

        return FindMachineFsm(target, update.FsmName);
    }

    /// <summary>
    /// The FSM of a name on an object, if it has one.
    /// </summary>
    private static Fsm? FindMachineFsm(GameObject gameObject, string fsmName) {
        foreach (var playMakerFsm in gameObject.GetComponents<PlayMakerFSM>()) {
            if (playMakerFsm != null && playMakerFsm.FsmName == fsmName && playMakerFsm.Fsm is { } fsm) {
                return fsm;
            }
        }

        return null;
    }

    /// <summary>
    /// Sends grinders on that wait for something that won't come: a grinder that caught the local player, who went down
    /// instead of being put back at the last safe place, goes on as they go down, as it would once they were put back.
    /// Grinders stopped for the partner go on once the partner is no longer in the room, or when the word that theirs
    /// went on was lost.
    /// </summary>
    private void UpdateGrinderStops() {
        if (_caughtGrinders.Count > 0) {
            var hero = HeroController.instance;
            foreach (var fsm in _caughtGrinders.ToList()) {
                // One that another grinder that went with it stopped still has to say that it goes on
                if (fsm.GameObject == null || fsm.ActiveState?.Name is not { } state ||
                    (!GrinderCatchStateNames.Contains(state) && state != GrinderStoppedStateName)) {
                    _caughtGrinders.Remove(fsm);
                    continue;
                }

                if (hero == null || !PlayerTargetRegistry.IsPlayerDown(hero.gameObject)) {
                    continue;
                }

                // Stood up again where it caught them, they would be caught again as soon as they were on their feet
                if (_rescue is { ByGrinder: false } rescue) {
                    rescue.ByGrinder = true;
                    Logger.Info($"The grinder '{fsm.GameObject.name}' held the player as they went down");
                }

                if (state == GrinderHazardHitStateName) {
                    Logger.Info($"The grinder '{fsm.GameObject.name}' goes on, since the player it caught went down");
                    fsm.Event(GrinderPutBackEventName);
                }
            }
        }

        if (_grindersStoppedForPartner.Count == 0) {
            return;
        }

        var isPartnerHere = IsMemberInRoom();
        foreach (var pair in _grindersStoppedForPartner.ToList()) {
            var fsm = pair.Key;
            if (fsm.GameObject == null || fsm.ActiveState?.Name != GrinderStoppedStateName) {
                _grindersStoppedForPartner.Remove(fsm);
                continue;
            }

            if (isPartnerHere && Time.unscaledTime - pair.Value <= PartnerGrinderStopLimit) {
                continue;
            }

            _grindersStoppedForPartner.Remove(fsm);
            Logger.Info(
                isPartnerHere
                    ? $"The grinder '{fsm.GameObject.name}' goes on by itself, since the partner's word didn't come"
                    : $"The grinder '{fsm.GameObject.name}' goes on, since the partner left the room"
            );
            fsm.Event(GrinderGoEventName);
        }
    }

    /// <summary>
    /// Collects the states of <see cref="MachineKinds"/> that a change of state is asked about.
    /// </summary>
    private static HashSet<string> BuildMachineStateNames() {
        var names = new HashSet<string>(StringComparer.Ordinal) {
            GrinderResetStateName, GrinderCatchStateName, GrinderCatcherStartStateName
        };
        foreach (var kind in MachineKinds) {
            names.UnionWith(kind.RoundStateNames);
        }

        return names;
    }

    /// <summary>
    /// The kind of machine that an object is, going by its name, the FSM and, for a kind of one room, its scene.
    /// </summary>
    private static MachineKind? FindMachineKind(GameObject gameObject, string fsmName) {
        string? name = null;
        foreach (var kind in MachineKinds) {
            if (kind.FsmName != fsmName) {
                continue;
            }

            name ??= gameObject.name;
            foreach (var prefix in kind.NamePrefixes) {
                if (name.StartsWith(prefix, StringComparison.Ordinal) && (kind.SceneName == null ||
                        string.Equals(gameObject.scene.name, kind.SceneName, StringComparison.OrdinalIgnoreCase))) {
                    return kind;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The state an FSM is about to go to, which is cleared to keep a machine where it is.
    /// </summary>
    private static readonly FieldInfo? MachineSwitchToStateField = typeof(Fsm).GetField(
        "switchToState", BindingFlags.Instance | BindingFlags.NonPublic
    );

    /// <summary>
    /// One kind of machine that the scene host paces.
    /// </summary>
    private sealed class MachineKind {
        public MachineKind(
            string[] namePrefixes,
            string fsmName,
            string[] roundStateNames,
            bool isGrinder = false,
            string? sceneName = null,
            string[]? ownStateNames = null
        ) {
            NamePrefixes = namePrefixes;
            FsmName = fsmName;
            RoundStateNames = roundStateNames;
            IsGrinder = isGrinder;
            SceneName = sceneName;
            OwnStateNames = ownStateNames == null ? null : new HashSet<string>(ownStateNames, StringComparer.Ordinal);
        }

        /// <summary>
        /// What the objects of this kind are called, of which the name of one starts with any of them.
        /// </summary>
        public string[] NamePrefixes { get; }

        /// <summary>
        /// The state machine that runs it.
        /// </summary>
        public string FsmName { get; }

        /// <summary>
        /// The states that its rounds start with.
        /// </summary>
        public string[] RoundStateNames { get; }

        /// <summary>
        /// Whether it is a grinder, which also goes by where it is and by its list of ways.
        /// </summary>
        public bool IsGrinder { get; }

        /// <summary>
        /// The scene that the objects of this kind are in, for a kind of one room, or null for any scene.
        /// </summary>
        public string? SceneName { get; }

        /// <summary>
        /// For the controller of a fight, the states that each game goes into by itself: the start of the fight. Every
        /// other state is paced. Null for a machine that is paced at the starts of its rounds.
        /// </summary>
        public HashSet<string>? OwnStateNames { get; }

        /// <summary>
        /// Whether it is the controller of a fight, which is paced in every state but <see cref="OwnStateNames"/>.
        /// </summary>
        public bool IsDirector => OwnStateNames != null;
    }
}
