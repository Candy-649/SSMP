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
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How long a machine waits here for the scene host's word before it starts its round by itself, in seconds. Longer
    /// than any round, so that it only happens when the host's machine has stopped going: its own clock is then the best
    /// there is.
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
        new(["Understore_Grinder"], "Control", [GrinderNextWayStateName], isGrinder: true)
    ];

    /// <summary>
    /// The states of <see cref="MachineKinds"/> that a change of state is asked about at all, kept as a set because
    /// the hook this serves is called for every change of state of every state machine in the game.
    /// </summary>
    private static readonly HashSet<string> MachineStateNames = BuildMachineStateNames();

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
    /// The number of the newest word of the scene host taken for each machine, so that a word that comes late, after a
    /// newer one, is dropped.
    /// </summary>
    private readonly Dictionary<Fsm, ulong> _takenMachineRounds = new();

    /// <summary>
    /// The machines that were said in the log to follow the scene host, so that it is said once for each.
    /// </summary>
    private readonly HashSet<Fsm> _machinesSaid = [];

    /// <summary>
    /// The machine whose round of the scene host's is being started here, which is let through.
    /// </summary>
    private Fsm? _startingMachineRound;

    /// <summary>
    /// The number of the last round that this game, as scene host, said.
    /// </summary>
    private ulong _machineRoundCount;

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
        StayPut
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
        if (_checkedWith == null || !MachineStateNames.Contains(toState.Name) || fsm == _startingMachineRound ||
            fsm.GameObject is not { } gameObject || MachineSwitchToStateField == null) {
            return MachineStep.None;
        }

        kind = FindMachineKind(gameObject.name, fsm.Name);
        if (kind == null) {
            return MachineStep.None;
        }

        if (kind.IsGrinder && toState.Name == GrinderResetStateName) {
            return MachineStep.StayPut;
        }

        if (Array.IndexOf(kind.RoundStateNames, toState.Name) < 0 ||
            GetCheckedPartner() is not { IsInLocalScene: true }) {
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
                TargetId = _checkedWith!.Value,
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
        if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker) || _checkedWith != player.Id ||
            IsSceneHost?.Invoke() == true) {
            return;
        }

        if (ScenePath.Find(update.ObjectPath, update.Scene) is not { activeInHierarchy: true } target ||
            FindMachineKind(target.name, update.FsmName) is not { } kind) {
            return;
        }

        foreach (var playMakerFsm in target.GetComponents<PlayMakerFSM>()) {
            if (playMakerFsm == null || playMakerFsm.FsmName != update.FsmName || playMakerFsm.Fsm is not { } fsm) {
                continue;
            }

            if (_takenMachineRounds.TryGetValue(fsm, out var taken) && update.Sequence <= taken) {
                return;
            }

            _takenMachineRounds[fsm] = update.Sequence;
            if (!TryStartMachineRoundNow(kind, fsm, update)) {
                _pendingMachineRounds[fsm] = update;
            }

            return;
        }
    }

    /// <summary>
    /// Starts the round of a word of the scene host on a machine here, if the machine is where that round can start:
    /// held at the end of its last round, or in a state that goes into the round by itself.
    /// </summary>
    /// <returns>Whether the round started.</returns>
    private bool TryStartMachineRoundNow(MachineKind kind, Fsm fsm, CoopSaveUpdate update) {
        var isHeld = _heldMachines.TryGetValue(fsm, out var held) && fsm.ActiveState != null &&
                     held.From == fsm.ActiveState;
        if (!isHeld && !GoesStraightTo(fsm.ActiveState, update.StateName)) {
            return false;
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
            Logger.Info($"The machine '{update.ObjectPath}' follows the rounds of the scene host's");
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
        if (_heldMachines.Count == 0 && _pendingMachineRounds.Count == 0) {
            return;
        }

        // Words of a scene host that this game took over from, or of a partner who left, are no longer the room's
        var runsTheRoom = _checkedWith == null || IsSceneHost?.Invoke() == true ||
                          GetCheckedPartner() is not { IsInLocalScene: true };
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

            if (FindMachineKind(fsm.GameObject.name, fsm.Name) is { } kind &&
                GoesStraightTo(fsm.ActiveState, update.StateName)) {
                _pendingMachineRounds.Remove(fsm);
                StartMachineRound(kind, fsm, update, () => fsm.SetState(update.StateName));
            }
        }

        if (_heldMachines.Count == 0) {
            return;
        }

        List<Fsm>? released = null;
        foreach (var pair in _heldMachines) {
            var fsm = pair.Key;
            var held = pair.Value;
            if (fsm.GameObject == null || held.From != fsm.ActiveState || runsTheRoom ||
                Time.unscaledTime - held.Since > MachineHoldLimit) {
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
    }

    /// <summary>
    /// Collects the states of <see cref="MachineKinds"/> that a change of state is asked about.
    /// </summary>
    private static HashSet<string> BuildMachineStateNames() {
        var names = new HashSet<string>(StringComparer.Ordinal) { GrinderResetStateName };
        foreach (var kind in MachineKinds) {
            names.UnionWith(kind.RoundStateNames);
        }

        return names;
    }

    /// <summary>
    /// The kind of machine that objects of a name are, going by the FSM.
    /// </summary>
    private static MachineKind? FindMachineKind(string name, string fsmName) {
        foreach (var kind in MachineKinds) {
            if (kind.FsmName != fsmName) {
                continue;
            }

            foreach (var prefix in kind.NamePrefixes) {
                if (name.StartsWith(prefix, StringComparison.Ordinal)) {
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
        public MachineKind(string[] namePrefixes, string fsmName, string[] roundStateNames, bool isGrinder = false) {
            NamePrefixes = namePrefixes;
            FsmName = fsmName;
            RoundStateNames = roundStateNames;
            IsGrinder = isGrinder;
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
    }
}
