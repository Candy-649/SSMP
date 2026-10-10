using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Toll benches in a checked two-player save: a toll machine that flips over into a bench once a player pays, stays a
/// bench while someone sits on it or is about to, and flips back into the machine a few seconds after the last one
/// left. Nothing about them is saved; each payment buys one rest.
///
/// The game only ever flips one for its own hero. When the partner pays, the bench comes up in their game and the
/// machine stays where it is in this one, so the local player cannot sit down next to them.
///
/// So each game says which of its toll benches its own player keeps up - by paying for it, or by sitting on it or
/// having just got up from it - and the other game flips its copy up without a payment and keeps it from flipping back
/// for as long as that lasts. Either player can then sit on it. Once the partner no longer keeps it up, the copy waits
/// out the game's own timer again and flips back the way the game flips it back for its own hero.
///
/// A game only ever says what its own player does, never that it keeps a bench up for the partner. Two games that each
/// kept a bench up for as long as the other one had it up would keep it up for good.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The name of the FSM that runs a toll bench.
    /// </summary>
    private const string TollBenchFsmName = "Behaviour (special)";

    /// <summary>
    /// What the name of the object of a toll bench contains.
    /// </summary>
    private const string TollBenchObjectName = "Toll Bench";

    /// <summary>
    /// The state of a toll bench that shows the machine and waits for a player to pay.
    /// </summary>
    private const string TollBenchInertState = "Inert";

    /// <summary>
    /// The state of a toll bench that runs the payment.
    /// </summary>
    private const string TollBenchConfirmState = "Confirm";

    /// <summary>
    /// The state of a toll bench that flips the machine over into the bench once a player paid.
    /// </summary>
    private const string TollBenchFlipUpState = "Flip to Bench";

    /// <summary>
    /// The state of a toll bench that fades the bench in and lets it be sat on.
    /// </summary>
    private const string TollBenchFadeUpState = "Fade Bench Up";

    /// <summary>
    /// The state of a toll bench that waits a few seconds for a player to sit down after paying.
    /// </summary>
    private const string TollBenchWaitForSitState = "Wait For Sit";

    /// <summary>
    /// The state of a toll bench while the local hero sits on it.
    /// </summary>
    private const string TollBenchOnBenchState = "On Bench";

    /// <summary>
    /// The state of a toll bench that plays the sound of the local hero getting up.
    /// </summary>
    private const string TollBenchSitAudioState = "Play Sit Audio";

    /// <summary>
    /// The state of a toll bench that waits a moment after the local hero got up, in case they sit down again.
    /// </summary>
    private const string TollBenchTimerState = "Timer";

    /// <summary>
    /// The state of a toll bench that flips the bench back into the machine.
    /// </summary>
    private const string TollBenchFlipDownState = "Flip To Toll";

    /// <summary>
    /// The state of a toll bench that puts the local hero on it straight away, as when they wake up at it.
    /// </summary>
    private const string TollBenchStartSittingState = "Start Sitting";

    /// <summary>
    /// The event that ends the wait of a toll bench for a player to sit down, after which it flips back.
    /// </summary>
    private const string TollBenchFinishedEvent = "FINISHED";

    /// <summary>
    /// The toll benches of the loaded rooms that were seen, by their FSM.
    /// </summary>
    private readonly Dictionary<Fsm, TollBench> _tollBenchesByFsm = new();

    /// <summary>
    /// The same toll benches, in a list that is gone through every frame.
    /// </summary>
    private readonly List<TollBench> _tollBenches = [];

    /// <summary>
    /// The toll benches that other members keep up in their games, by the scene and path of theirs, whether or not a
    /// bench here goes with them: those of <see cref="_tollBenchHolders"/>.
    /// </summary>
    private readonly HashSet<string> _partnerTollBenches = [];

    /// <summary>
    /// The members who keep each toll bench of <see cref="_partnerTollBenches"/> up, by the scene and path. A bench lets
    /// go once the last of them does.
    /// </summary>
    private readonly Dictionary<string, HashSet<ushort>> _tollBenchHolders = new();

    /// <summary>
    /// The count of the last update about each toll bench that came from each member, by the member and the scene and
    /// path of theirs, so that an older update that the network delivers after a newer one is left alone.
    /// </summary>
    private readonly Dictionary<(ushort, string), ulong> _partnerTollBenchSequences = new();

    /// <summary>
    /// A count that grows with every update about a toll bench that the local game sends.
    /// </summary>
    private ulong _tollBenchSequence;

    /// <summary>
    /// Whether the toll benches of the current room were looked for since the local player came into it.
    /// </summary>
    private bool _tollBenchesSearched;

    /// <summary>
    /// Whether a toll bench is being flipped up because the partner keeps theirs up, which is not the local player
    /// paying for it.
    /// </summary>
    private bool _flippingPartnerTollBench;

    /// <summary>
    /// Whether following the toll benches failed once already, so that it is logged only once.
    /// </summary>
    private bool _tollBenchFailed;

    /// <summary>
    /// Follows a toll bench that changes state: whether the local player now keeps it up, and turns the actions back on
    /// that were turned off while it flipped up for the partner. Called from the hook on FSMs changing state, before the
    /// change.
    /// </summary>
    /// <param name="fsm">The FSM that changes state.</param>
    /// <param name="toState">The state it goes to.</param>
    private void OnTollBenchSwitch(Fsm fsm, FsmState toState) {
        if (fsm.Name != TollBenchFsmName) {
            return;
        }

        if (!_tollBenchesByFsm.TryGetValue(fsm, out var bench)) {
            bench = TryAddTollBench(fsm);
            if (bench == null) {
                return;
            }
        }

        // It leaves the state that it flipped up in for the partner, whose end of the dialogue is no longer at risk
        if (bench.Muted != null) {
            foreach (var action in bench.Muted) {
                action.Enabled = true;
            }

            bench.Muted = null;
        }

        bench.HeldFinish = false;
        bench.RestartPending = false;

        var to = toState.Name;
        switch (to) {
            case TollBenchFlipUpState:
                // Only a payment of the local player flips it up from the payment; one that flips up for the partner
                // is theirs to keep up
                bench.Own = !_flippingPartnerTollBench && fsm.ActiveStateName == TollBenchConfirmState;
                if (bench.Own) {
                    Logger.Info($"The local player paid for the toll bench '{bench.Path}' in {bench.Scene}");
                }

                break;
            case TollBenchStartSittingState:
            case TollBenchOnBenchState:
                // Only the local hero sits on the bench in this game
                bench.Own = true;
                break;
            case TollBenchFadeUpState:
            case TollBenchWaitForSitState:
            case TollBenchSitAudioState:
            case TollBenchTimerState:
                // On the way from paying or sitting to the end of the wait, which keeps whoever it belongs to
                break;
            default:
                bench.Own = false;
                break;
        }
    }

    /// <summary>
    /// Keeps a toll bench up that the partner keeps up in their game, by holding back the end of its wait for a player
    /// to sit down. The local player's own wait is over either way. Called from the hook on FSM events.
    /// </summary>
    /// <returns>Whether the event is held back.</returns>
    private bool HoldsTollBenchEvent(Fsm fsm, FsmEvent fsmEvent) {
        if (fsmEvent.Name != TollBenchFinishedEvent || !_tollBenchesByFsm.TryGetValue(fsm, out var bench)) {
            return false;
        }

        var state = fsm.ActiveStateName;
        if (state != TollBenchWaitForSitState && state != TollBenchTimerState) {
            return false;
        }

        bench.Own = false;
        if (!_partnerTollBenches.Contains(bench.Key)) {
            return false;
        }

        if (!bench.HeldFinish) {
            bench.HeldFinish = true;
            Logger.Info($"Kept the toll bench '{bench.Path}' in {bench.Scene} up, because the partner keeps it up");
        }

        return true;
    }

    /// <summary>
    /// Tells the partner about the toll benches that the local player keeps up, flips up the ones here that the partner
    /// keeps up, and lets the ones wait again that the partner let go of. Called every frame of a paired save.
    /// </summary>
    private void UpdateTollBenches() {
        try {
            if (_partnerTollBenches.Count > 0 && !_tollBenchesSearched) {
                _tollBenchesSearched = true;
                FindTollBenches();
            }

            for (var i = _tollBenches.Count - 1; i >= 0; i--) {
                var bench = _tollBenches[i];
                if (bench.Owner == null) {
                    // Its room is gone, and with it everything that kept the bench up here
                    if (bench.Sent) {
                        SendTollBench(bench, false);
                    }

                    _tollBenchesByFsm.Remove(bench.Fsm);
                    _tollBenches.RemoveAt(i);
                    continue;
                }

                if (bench.Sent != bench.Own) {
                    SendTollBench(bench, bench.Own);
                }

                if (!bench.Owner.isActiveAndEnabled) {
                    continue;
                }

                var state = bench.Fsm.ActiveStateName;
                if (_partnerTollBenches.Contains(bench.Key)) {
                    if (state == TollBenchInertState) {
                        FlipTollBenchUpForPartner(bench);
                    }
                } else if (bench.RestartPending) {
                    RestartTollBenchWait(bench, state);
                }
            }
        } catch (Exception e) {
            if (!_tollBenchFailed) {
                _tollBenchFailed = true;
                Logger.Error($"Could not keep the toll benches in step with the partner:\n{e}");
            }
        }
    }

    /// <summary>
    /// Looks for the toll benches of the current room, for a partner keeping one up while the local player is there.
    /// The benches that change state are found as they do, but one that has stood still since the room loaded is not.
    /// </summary>
    private void FindTollBenches() {
        foreach (var component in UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None
                 )) {
            if (component != null && component.FsmName == TollBenchFsmName && component.Fsm is { } fsm &&
                !_tollBenchesByFsm.ContainsKey(fsm)) {
                TryAddTollBench(fsm);
            }
        }
    }

    /// <summary>
    /// Starts following an FSM if it runs a toll bench.
    /// </summary>
    /// <returns>The toll bench, or null if the FSM does not run one.</returns>
    private TollBench? TryAddTollBench(Fsm fsm) {
        if (fsm.Owner is not { } owner || owner == null || fsm.GetState(TollBenchWaitForSitState) == null ||
            fsm.GetState(TollBenchFlipUpState) == null || fsm.GetState(TollBenchFlipDownState) == null ||
            !owner.gameObject.name.Contains(TollBenchObjectName)) {
            return null;
        }

        var bench = new TollBench(fsm, owner, owner.gameObject.scene.name, ScenePath.Get(owner.transform));
        _tollBenchesByFsm[fsm] = bench;
        _tollBenches.Add(bench);
        return bench;
    }

    /// <summary>
    /// Flips up a toll bench here that the partner keeps up in their game, the way it flips up after a payment but
    /// without one. Ending the dialogue of the payment is left out, since the local player never started one, and
    /// ending it anyway would hand control back to a hero that something else holds.
    /// </summary>
    private void FlipTollBenchUpForPartner(TollBench bench) {
        var fsm = bench.Fsm;
        var flipUp = fsm.GetState(TollBenchFlipUpState);
        if (flipUp == null) {
            return;
        }

        List<FsmStateAction>? muted = null;
        foreach (var action in flipUp.Actions ?? []) {
            if (action != null && action.Enabled && action.GetType().Name == "EndDialogue") {
                action.Enabled = false;
                (muted ??= []).Add(action);
            }
        }

        _flippingPartnerTollBench = true;
        try {
            // As in an update of the FSM, the transitions that the state fires while it starts wait until it has
            // started
            FsmExecutionStack.PushFsm(fsm);
            try {
                fsm.SetState(TollBenchFlipUpState);
                fsm.UpdateStateChanges();
            } finally {
                FsmExecutionStack.PopFsm();
            }
        } finally {
            _flippingPartnerTollBench = false;
        }

        // Turned back on as the bench leaves the state, which ran them already if it left it straight away
        if (muted != null) {
            if (fsm.ActiveStateName == TollBenchFlipUpState) {
                bench.Muted = muted;
            } else {
                foreach (var action in muted) {
                    action.Enabled = true;
                }
            }
        }

        Logger.Info($"Flipped up the toll bench '{bench.Path}' in {bench.Scene}, because the partner keeps it up");
    }

    /// <summary>
    /// Starts the wait of a toll bench for a player to sit down over again, after the partner let go of it, so that it
    /// flips back once the game's own timer runs out. A local hero on a bench gets up first; the bench they sit on
    /// is no longer waiting by then.
    /// </summary>
    /// <param name="bench">The toll bench.</param>
    /// <param name="state">The state that it is in.</param>
    private void RestartTollBenchWait(TollBench bench, string? state) {
        string wait;
        if (state == TollBenchWaitForSitState) {
            wait = TollBenchWaitForSitState;
        } else if (state == TollBenchTimerState) {
            wait = TollBenchTimerState;
        } else {
            bench.RestartPending = false;
            return;
        }

        if (PlayerData.instance?.atBench == true) {
            return;
        }

        var fsm = bench.Fsm;
        FsmExecutionStack.PushFsm(fsm);
        try {
            fsm.SetState(wait);
            fsm.UpdateStateChanges();
        } finally {
            FsmExecutionStack.PopFsm();
        }

        bench.RestartPending = false;
        Logger.Info(
            $"Let the toll bench '{bench.Path}' in {bench.Scene} wait in '{wait}' again, because the partner no " +
            "longer keeps it up"
        );
    }

    /// <summary>
    /// Tells the other members that the local player keeps a toll bench up, or no longer does.
    /// </summary>
    private void SendTollBench(TollBench bench, bool keptUp) {
        if (_checkedMembers.Count == 0) {
            bench.Sent = false;
            return;
        }

        SendToMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.TollBench,
            Scene = bench.Scene,
            ObjectPath = bench.Path,
            PartCount = (ushort) (keptUp ? 1 : 0),
            Sequence = ++_tollBenchSequence
        });
        bench.Sent = keptUp;

        Logger.Info(
            keptUp
                ? $"Told the partner that the toll bench '{bench.Path}' in {bench.Scene} is kept up here"
                : $"Told the partner that the toll bench '{bench.Path}' in {bench.Scene} is no longer kept up here"
        );
    }

    /// <summary>
    /// The partner keeps a toll bench up in their game, or no longer does.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update, which names their toll bench by its scene and path.</param>
    private void OnTollBench(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker) ||
            !_checkedMembers.Contains(player.Id)) {
            return;
        }

        var key = update.Scene + "/" + update.ObjectPath;
        if (_partnerTollBenchSequences.TryGetValue((player.Id, key), out var last) && update.Sequence <= last) {
            return;
        }

        _partnerTollBenchSequences[(player.Id, key)] = update.Sequence;
        if (update.PartCount == 0) {
            if (!_tollBenchHolders.TryGetValue(key, out var holders) || !holders.Remove(player.Id)) {
                return;
            }

            Logger.Info($"{player.Username} no longer keeps the toll bench '{update.ObjectPath}' in {update.Scene} up");
            if (holders.Count == 0) {
                LetGoOfTollBench(key);
            }

            return;
        }

        if (!_tollBenchHolders.TryGetValue(key, out var keepers)) {
            keepers = _tollBenchHolders[key] = [];
        }

        if (keepers.Add(player.Id)) {
            _partnerTollBenches.Add(key);
            Logger.Info($"{player.Username} keeps the toll bench '{update.ObjectPath}' in {update.Scene} up");
        }
    }

    /// <summary>
    /// No member keeps a toll bench up any more, so the bench here goes back to the game's own timer, which may have run
    /// out while they kept it up.
    /// </summary>
    private void LetGoOfTollBench(string key) {
        _tollBenchHolders.Remove(key);
        if (!_partnerTollBenches.Remove(key)) {
            return;
        }

        foreach (var bench in _tollBenches) {
            if (bench.Key == key) {
                bench.RestartPending = true;
            }
        }
    }

    /// <summary>
    /// Lets go of the toll benches that a member who left the save kept up, where no other member keeps them up.
    /// </summary>
    private void ForgetTollBenchesOf(ushort id) {
        foreach (var pair in _tollBenchHolders.ToList()) {
            if (pair.Value.Remove(id) && pair.Value.Count == 0) {
                LetGoOfTollBench(pair.Key);
            }
        }

        foreach (var key in _partnerTollBenchSequences.Keys.Where(key => key.Item1 == id).ToList()) {
            _partnerTollBenchSequences.Remove(key);
        }
    }

    /// <summary>
    /// Notes that the local player came into another room, whose toll benches are looked for once the partner keeps
    /// one up.
    /// </summary>
    private void OnTollBenchSceneChanged() {
        _tollBenchesSearched = false;
    }

    /// <summary>
    /// Forgets the toll benches that the partner keeps up, and that the partner was told about, as the check with them
    /// ends. The benches here that they kept up wait out the game's own timer again and flip back.
    /// </summary>
    private void ResetTollBenches() {
        foreach (var bench in _tollBenches) {
            bench.Sent = false;
            if (_partnerTollBenches.Contains(bench.Key)) {
                bench.RestartPending = true;
            }
        }

        _partnerTollBenches.Clear();
        _tollBenchHolders.Clear();
        _partnerTollBenchSequences.Clear();
    }

    /// <summary>
    /// A toll bench of a loaded room.
    /// </summary>
    private sealed class TollBench {
        public TollBench(Fsm fsm, MonoBehaviour owner, string scene, string path) {
            Fsm = fsm;
            Owner = owner;
            Scene = scene;
            Path = path;
            Key = scene + "/" + path;
        }

        /// <summary>
        /// The FSM that runs it.
        /// </summary>
        public Fsm Fsm { get; }

        /// <summary>
        /// The component of the FSM, which is gone once its room is.
        /// </summary>
        public MonoBehaviour Owner { get; }

        /// <summary>
        /// The scene of the bench, kept for telling the partner after the bench is gone.
        /// </summary>
        public string Scene { get; }

        /// <summary>
        /// The path of the bench in its scene, kept for the same reason.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// The scene and path together, as the partner's updates name the bench.
        /// </summary>
        public string Key { get; }

        /// <summary>
        /// Whether the local player keeps it up: they paid for it and its wait for them to sit down hasn't run out, or
        /// they sit on it, or they got up from it and its wait for them to sit down again hasn't run out.
        /// </summary>
        public bool Own { get; set; }

        /// <summary>
        /// Whether the partner was last told that the local player keeps it up.
        /// </summary>
        public bool Sent { get; set; }

        /// <summary>
        /// Whether the end of its wait was held back because the partner keeps it up.
        /// </summary>
        public bool HeldFinish { get; set; }

        /// <summary>
        /// Whether its wait starts over once nothing stops it, since the partner let go of it.
        /// </summary>
        public bool RestartPending { get; set; }

        /// <summary>
        /// The actions that were turned off while it flipped up for the partner, or null.
        /// </summary>
        public List<FsmStateAction>? Muted { get; set; }
    }
}
