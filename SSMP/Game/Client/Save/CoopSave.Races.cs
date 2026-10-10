using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Races against the racer of a room in a checked two-player save, which the members run together and win together.
///
/// Each game has its own racer, its own track and its own count of laps, and only its own hero can cross a line, so a
/// race is still one race for each player: each runs theirs in their own game. What is shared is how they start and
/// how they end. A race starts only once every member said yes at their own prompt, which the prompts that need every
/// member see to. They then wait at the start line, in the dark the game puts them in there, until every member in the
/// room is there too, so that the countdowns of their games start together. And all of them win as soon as any of them
/// beats the racer: a player who lost is held where the game would tell them so, until another member either wins -
/// and this game is then told that its player won too - or every one of them is out as well.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How long a player waits at the start line for the other members before running without them. They may still be
    /// reading what the racer says after the yes, so it is long; the one who waits sees why in the chat.
    /// </summary>
    private const float RaceStartTimeout = 60f;

    /// <summary>
    /// The sender is at the start line.
    /// </summary>
    private const ushort RaceReady = 0;

    /// <summary>
    /// The sender beat the racer, which wins the race for every member.
    /// </summary>
    private const ushort RaceWon = 1;

    /// <summary>
    /// The sender did not beat the racer: they finished after it, were disqualified or gave up.
    /// </summary>
    private const ushort RaceOut = 2;

    /// <summary>
    /// The sender is already running and answers a member's word that they are at the start line. Kept apart from
    /// <see cref="RaceReady"/> so that it is never answered in turn: two players who were both already running used to
    /// answer each other's answers for the rest of the race.
    /// </summary>
    private const ushort RaceReadyAnswer = 3;

    /// <summary>
    /// The state of the racer that picks the track, which every try of a race goes through.
    /// </summary>
    private const string RaceSetTrackState = "Set Track";

    /// <summary>
    /// The state that puts the hero on the start line in the dark, and finishes into the countdown.
    /// </summary>
    private const string RaceStartLineState = "Start Line";

    /// <summary>
    /// The state of a race that runs, before the racer or the hero finished.
    /// </summary>
    private const string RaceRunningState = "Go!";

    /// <summary>
    /// The state after the racer finished first, which waits for the hero to finish too.
    /// </summary>
    private const string RaceRunnerEndState = "Runner End";

    /// <summary>
    /// The state that starts everything a won race gives.
    /// </summary>
    private const string RaceHeroWinState = "Hero Win";

    /// <summary>
    /// The state that the racer takes when the player gives a race up.
    /// </summary>
    private const string RaceResetState = "Reset";

    /// <summary>
    /// The state in which the racer waits to be talked to, which ends a race.
    /// </summary>
    private const string RaceIdleState = "Idle";

    private const string RaceFinishedEvent = "FINISHED";

    /// <summary>
    /// The event the racer is sent when the hero finished their laps.
    /// </summary>
    private const string RaceHeroEndEvent = "HERO END";

    /// <summary>
    /// The event the racer is sent when the hero crossed a line in the wrong order.
    /// </summary>
    private const string RaceDisqualifiedEvent = "DISQUALIFIED";

    /// <summary>
    /// The fields and the method that tell the game that the hero finished their laps.
    /// </summary>
    private static readonly FieldInfo? RunnerControllerField =
        typeof(SplineRunner).GetField("raceController", InstanceFlags);

    private static readonly FieldInfo? HeroLapsField =
        typeof(SprintRaceController).GetField("heroLapsCompleted", InstanceFlags);

    private static readonly FieldInfo? LapCountField = typeof(SprintRaceController).GetField("lapCount", InstanceFlags);

    private static readonly MethodInfo? CheckCompletionMethod =
        typeof(SprintRaceController).GetMethod("CheckCompletion", InstanceFlags, null, [typeof(bool)], null);

    /// <summary>
    /// The FSM of the racer that the local player races right now, or null.
    /// </summary>
    private Fsm? _raceFsm;

    /// <summary>
    /// The wishes of that racer. Their changes don't go to the members until the race is over, because the game of a
    /// member decides what a win gives by whether the wish is complete, and a completion of this game that arrived
    /// before that would take the rewards of the last track away from them.
    /// </summary>
    private readonly HashSet<string> _raceWishes = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the local player left the start line.
    /// </summary>
    private bool _raceStarted;

    /// <summary>
    /// The event of the racer that waits for the members, or null.
    /// </summary>
    private RaceHold? _raceHold;

    /// <summary>
    /// Whether an event that waited is being sent on, which is let through.
    /// </summary>
    private bool _releasingRaceEvent;

    /// <summary>
    /// Whether the local player won this race, by beating the racer or with a member.
    /// </summary>
    private bool _raceWonHere;

    /// <summary>
    /// Whether the members were told how the race ended for the local player.
    /// </summary>
    private bool _raceResultSent;

    /// <summary>
    /// The members who run this race too.
    /// </summary>
    private readonly HashSet<ushort> _racingMembers = [];

    /// <summary>
    /// The members who beat the racer in this race, any one of whom wins it for everyone.
    /// </summary>
    private readonly HashSet<ushort> _raceWonBy = [];

    /// <summary>
    /// The members who are out of this race.
    /// </summary>
    private readonly HashSet<ushort> _raceOutMembers = [];

    /// <summary>
    /// When each member said they are at the start line, kept from before the local player got there. It counts for as
    /// long as they wait there.
    /// </summary>
    private readonly Dictionary<ushort, float> _raceReadyAt = new();

    /// <summary>
    /// Whether syncing a race threw, which is only logged once.
    /// </summary>
    private bool _raceSyncFailed;

    /// <summary>
    /// Whether a prompt is a racer asking whether to start a race, and the box on screen is the one it opens.
    /// </summary>
    private static bool IsRacePrompt(YesNoAction action, YesNoBox box) {
        var isBoxOfPrompt = action is QuestYesNo or QuestYesNoV2 ? box is QuestYesNoBox : box is DialogueYesNoBox;
        return isBoxOfPrompt && action.Fsm is { } fsm && IsRacerFsm(fsm);
    }

    /// <summary>
    /// Whether an FSM is the one of a racer: one of its states picks a track.
    /// </summary>
    private static bool IsRacerFsm(Fsm fsm) {
        foreach (var state in fsm.States ?? []) {
            if (state != null && IsRaceStart(state)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a state picks the track of a race.
    /// </summary>
    private static bool IsRaceStart(FsmState state) {
        foreach (var action in state.Actions ?? []) {
            if (action is HutongGames.PlayMaker.Actions.SetCurrentRaceTrack) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What to ask the members about starting a race. It names the racer rather than the prompt, because the first race
    /// asks in the box that accepts the wish and every later one in a plain box, and the players need not be at the same
    /// one.
    /// </summary>
    private static CoopSaveUpdate? CreateRaceConfirm(YesNoAction action, ref string what) {
        var racer = action.Fsm?.GameObject;
        if (racer == null) {
            return null;
        }

        what = "starting a race";
        var update = CreateConfirm(WishConfirmRace);
        update.WishNames.Add(racer.scene.name + "/" + ScenePath.Get(racer.transform));
        return update;
    }

    /// <summary>
    /// Whether the yes that waits for the answer with a key is the start of a race.
    /// </summary>
    private bool IsRaceHold(ulong key) {
        return _wishConfirm is { } held && held.Key == key &&
               held.WishKey.StartsWith(WishConfirmRace + "\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a wish of the local wish log belongs to the race that runs, and waits for it to end before it goes to
    /// the members.
    /// </summary>
    private bool IsHeldByRace(string name) {
        return _raceFsm != null && _raceWishes.Contains(name);
    }

    /// <summary>
    /// Follows the racer of the local player through a race: the try that starts, the win, giving up and the end.
    /// Called before the FSM switches.
    /// </summary>
    private void OnRaceSwitch(Fsm fsm, FsmState toState) {
        if (fsm != _raceFsm) {
            if (toState.Name == RaceSetTrackState && IsRaceStart(toState)) {
                StartRaceRound(fsm);
            }

            return;
        }

        switch (toState.Name) {
            case RaceSetTrackState:
                // Another try of the same racer, after a race that was lost
                StartRaceRound(fsm);
                break;
            case RaceHeroWinState:
                OnLocalRaceWon();
                break;
            case RaceResetState:
                // The player gave the race up, which the game treats like losing it
                _raceHold = null;
                SendRaceResult(RaceOut);
                break;
            case RaceIdleState:
                EndRaceRound();
                break;
        }
    }

    /// <summary>
    /// Starts following a try of a race. What the members said about the start line is kept, since they may have got
    /// there first.
    /// </summary>
    private void StartRaceRound(Fsm fsm) {
        _raceFsm = fsm;
        _raceStarted = false;
        _raceHold = null;
        _raceWonHere = false;
        _raceResultSent = false;
        _racingMembers.Clear();
        _raceWonBy.Clear();
        _raceOutMembers.Clear();

        _raceWishes.Clear();
        foreach (var action in GetWishActions(fsm)) {
            if (action.Quest.Value is FullQuestBase quest && quest != null) {
                _raceWishes.Add(quest.name);
            }
        }

        Logger.Info($"A race against '{fsm.GameObjectName}' starts");
    }

    /// <summary>
    /// Stops following the race of the local player, whose racer is back to waiting to be talked to.
    /// </summary>
    private void EndRaceRound() {
        if (_raceFsm == null) {
            return;
        }

        _raceFsm = null;
        _raceHold = null;
        _raceWishes.Clear();
        Logger.Info("The race is over");
    }

    /// <summary>
    /// Forgets the race, for a new scene or session.
    /// </summary>
    private void ResetRaces() {
        _raceFsm = null;
        _raceHold = null;
        _raceWishes.Clear();
        _raceStarted = false;
        _raceWonHere = false;
        _raceResultSent = false;
        _racingMembers.Clear();
        _raceWonBy.Clear();
        _raceOutMembers.Clear();
        _raceReadyAt.Clear();
    }

    /// <summary>
    /// Whether an event of the racer waits for the members rather than going on: leaving the start line, and hearing
    /// that the race was lost.
    /// </summary>
    private bool HoldsRaceEvent(Fsm fsm, FsmEvent fsmEvent) {
        if (_releasingRaceEvent || fsm != _raceFsm) {
            return false;
        }

        var state = fsm.ActiveStateName;
        var name = fsmEvent.Name;
        if (_raceHold is { } held && held.State == state) {
            // The wait at the start line sends its event and then finishes, which sends it again. A player who lost
            // keeps control while they wait, so they can still cross a line in the wrong order too.
            return held.IsStart
                ? name == RaceFinishedEvent
                : name is RaceHeroEndEvent or RaceDisqualifiedEvent;
        }

        if (state == RaceStartLineState && name == RaceFinishedEvent && !_raceStarted) {
            return HoldRaceStart();
        }

        if ((state == RaceRunnerEndState && name == RaceHeroEndEvent) ||
            (name == RaceDisqualifiedEvent && state is RaceRunningState or RaceRunnerEndState)) {
            return HoldRaceLoss(state!, name);
        }

        return false;
    }

    /// <summary>
    /// Keeps the local player at the start line until every member in the room is there too.
    /// </summary>
    /// <returns>Whether the start waits.</returns>
    private bool HoldRaceStart() {
        var members = GetCheckedMembers();
        if (members.Count == 0) {
            _raceStarted = true;
            return false;
        }

        var room = members.FindAll(member => member.IsInLocalScene);
        if (room.Count == 0) {
            _raceStarted = true;
            var away = JoinNames(members.Select(member => member.Username));
            Chat(Lang.Pick(
                members.Count == 1
                    ? $"{away} isn't in this room, so you race alone."
                    : $"{away} aren't in this room, so you race alone.",
                $"{away} 不在这个房间，你自己跑。"
            ));
            return false;
        }

        var waiting = room.FindAll(member => !IsAtRaceStartLine(member.Id));
        if (waiting.Count == 0) {
            RaceTogetherWith(room);
            _raceStarted = true;
            SendRace(null, RaceReady);
            var names = JoinNames(room.Select(member => member.Username));
            Chat(Lang.Pick(
                room.Count == 1
                    ? $"{names} is at the start line too. Race together!"
                    : $"{names} are at the start line too. Race together!",
                $"{names} 也在起跑线上了，一起跑！"
            ));
            Logger.Info($"{names} were at the start line first, so the race starts at once");
            return false;
        }

        _raceHold = new RaceHold(RaceStartLineState, RaceFinishedEvent, true);
        SendRace(null, RaceReady);
        var missing = JoinNames(waiting.Select(member => member.Username));
        Chat(Lang.Pick(
            $"Waiting for {missing} to reach the start line too (at most a minute)...",
            $"等 {missing} 也到起跑线再一起开跑（最多等一分钟）……"
        ));
        Logger.Info($"Waiting at the start line for {missing}");
        return true;
    }

    /// <summary>
    /// Whether a member said they are at the start line, and still waits there.
    /// </summary>
    private bool IsAtRaceStartLine(ushort playerId) {
        return _raceReadyAt.TryGetValue(playerId, out var at) && Time.unscaledTime - at <= RaceStartTimeout;
    }

    /// <summary>
    /// Counts the given members as running this race with the local player, which their word at the start line was.
    /// </summary>
    private void RaceTogetherWith(List<ClientPlayerData> members) {
        foreach (var member in members) {
            _racingMembers.Add(member.Id);
            _raceReadyAt.Remove(member.Id);
        }
    }

    /// <summary>
    /// The members who run this race with the local player and are still in it: in the room, and not out.
    /// </summary>
    private List<ClientPlayerData> GetMembersStillRacing() {
        return GetCheckedMembers().FindAll(member =>
            _racingMembers.Contains(member.Id) && member.IsInLocalScene && !_raceOutMembers.Contains(member.Id)
        );
    }

    /// <summary>
    /// Keeps the racer from telling the local player that they lost while another member may still win for everyone.
    /// </summary>
    /// <returns>Whether the loss waits.</returns>
    private bool HoldRaceLoss(string state, string eventName) {
        // Taken as a win by the next update, which is where the win of a member is turned into one here
        if (_raceWonBy.Count > 0) {
            _raceHold = new RaceHold(state, eventName, false);
            return true;
        }

        SendRaceResult(RaceOut);
        var racing = GetMembersStillRacing();
        if (racing.Count > 0) {
            _raceHold = new RaceHold(state, eventName, false);
            var names = JoinNames(racing.Select(member => member.Username));
            Chat(Lang.Pick(
                _checkedMembers.Count <= 1
                    ? $"You didn't beat the racer. If {names} does, you both win."
                    : $"You didn't beat the racer. If {names} does, you all win.",
                _checkedMembers.Count <= 1
                    ? $"你没赢过对手。只要 {names} 赢了，就算你们都赢。"
                    : $"你没赢过对手。只要 {names} 有人赢了，就算大家都赢。"
            ));
            Logger.Info($"Holding '{eventName}' of the race until {names} are done");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Lets a race go on once the members decided it: at the start line, when they got there or stopped coming, and
    /// after a loss, when one of them won for everyone or all of them are out too.
    /// </summary>
    private void UpdateRaces() {
        if (_raceFsm is not { } fsm) {
            return;
        }

        try {
            if (fsm.GameObject == null) {
                EndRaceRound();
                return;
            }

            var state = fsm.ActiveStateName;
            if (_raceHold is { } held) {
                // Something else moved the racer on, which leaves nothing waiting
                if (held.State != state) {
                    _raceHold = null;
                    return;
                }

                var members = GetCheckedMembers();
                if (held.IsStart) {
                    var room = members.FindAll(member => member.IsInLocalScene);
                    var waiting = room.FindAll(member => !IsAtRaceStartLine(member.Id));
                    if (room.Count == 0) {
                        var gone = members.Count == 0
                            ? GetCurrentMarker()?.PartnerName ?? Lang.Pick("Your partner", "队友")
                            : JoinNames(members.Select(member => member.Username));
                        ReleaseRaceHold(fsm, Lang.Pick(
                            members.Count <= 1
                                ? $"{gone} isn't in this room any more, so you race alone."
                                : $"{gone} aren't in this room any more, so you race alone.",
                            $"{gone} 不在这个房间了，你先自己跑。"
                        ));
                    } else if (waiting.Count == 0) {
                        // Those who were waited for and left the room aren't waited for any more
                        RaceTogetherWith(room);
                        ReleaseRaceHold(fsm, Lang.Pick(
                            "Everyone in this room is at the start line. Race together!",
                            "这个房间里的人都到起跑线了，一起跑！"
                        ));
                    } else if (Time.unscaledTime - held.Since > RaceStartTimeout) {
                        var ready = room.FindAll(member => IsAtRaceStartLine(member.Id));
                        RaceTogetherWith(ready);
                        var missing = JoinNames(waiting.Select(member => member.Username));
                        ReleaseRaceHold(fsm, ready.Count == 0
                            ? Lang.Pick(
                                $"{missing} didn't reach the start line, so you race alone.",
                                $"{missing} 一直没到起跑线，你先自己跑。"
                            )
                            : Lang.Pick(
                                $"{missing} didn't reach the start line, so you race without them.",
                                $"{missing} 一直没到起跑线，你们先跑。"
                            ));
                    }

                    return;
                }

                if (_raceWonBy.Count > 0) {
                    WinRaceWithPartner(fsm);
                } else if (GetMembersStillRacing().Count == 0) {
                    // Every member who ran it is out, or gone from the room: those who left are named if any did
                    var racers = members.FindAll(member => _racingMembers.Contains(member.Id));
                    var left = racers.FindAll(member => !member.IsInLocalScene);
                    if (left.Count > 0 || racers.Count == 0) {
                        var gone = racers.Count == 0
                            ? GetCurrentMarker()?.PartnerName ?? Lang.Pick("Your partner", "队友")
                            : JoinNames(left.Select(member => member.Username));
                        ReleaseRaceHold(fsm, Lang.Pick(
                            left.Count <= 1
                                ? $"{gone} isn't in this room any more, so this race is lost."
                                : $"{gone} aren't in this room any more, so this race is lost.",
                            $"{gone} 不在这个房间了，这一轮算输。"
                        ));
                    } else {
                        var names = JoinNames(racers.Select(member => member.Username));
                        ReleaseRaceHold(fsm, Lang.Pick(
                            $"{names} didn't beat the racer either, so this race is lost.",
                            $"{names} 也没赢过对手，这一轮算输。"
                        ));
                    }
                }

                return;
            }

            if (_raceWonBy.Count > 0 && !_raceWonHere && state is RaceRunningState or RaceRunnerEndState) {
                WinRaceWithPartner(fsm);
            }
        } catch (Exception e) {
            LogRaceError(e);
        }
    }

    /// <summary>
    /// Sends on the event that waited.
    /// </summary>
    private void ReleaseRaceHold(Fsm fsm, string message) {
        if (_raceHold is not { } held) {
            return;
        }

        _raceHold = null;
        if (held.IsStart) {
            _raceStarted = true;
        }

        Chat(message);
        Logger.Info($"Sending on '{held.EventName}' of the race, which waited for the members");
        _releasingRaceEvent = true;
        try {
            fsm.Event(held.EventName);
        } finally {
            _releasingRaceEvent = false;
        }
    }

    /// <summary>
    /// Makes the race of the local player won, because a member beat the racer.
    /// </summary>
    private void WinRaceWithPartner(Fsm fsm) {
        _raceWonHere = true;
        var held = _raceHold;
        _raceHold = null;

        // In the middle of a race the game is told that the hero finished their laps, which is exactly what it hears
        // when the local player wins, so everything that listens for the end of a race hears it as well
        if (held == null && fsm.ActiveStateName == RaceRunningState && FinishHeroLaps(fsm)) {
            Logger.Info("A member won the race, so the laps of the local player were finished for them");
            return;
        }

        // After the racer finished, finishing the laps would tell the racer that the hero came second. So the win is
        // started directly, and it sets for itself everything a lost race had set on the way.
        FsmExecutionStack.PushFsm(fsm);
        try {
            fsm.SetState(RaceHeroWinState);
            fsm.UpdateStateChanges();
        } finally {
            FsmExecutionStack.PopFsm();
        }

        Logger.Info($"A member won the race, so it is won here too from '{held?.State ?? RaceRunnerEndState}'");
    }

    /// <summary>
    /// Tells the track of the racer that the hero finished all their laps.
    /// </summary>
    /// <returns>Whether it could be told.</returns>
    private static bool FinishHeroLaps(Fsm fsm) {
        if (RunnerControllerField == null || HeroLapsField == null || LapCountField == null ||
            CheckCompletionMethod == null || fsm.GameObject == null) {
            return false;
        }

        var runner = fsm.GameObject.GetComponent<SplineRunner>();
        if (runner == null || RunnerControllerField.GetValue(runner) is not SprintRaceController track ||
            track == null || LapCountField.GetValue(track) is not int laps) {
            return false;
        }

        HeroLapsField.SetValue(track, laps);
        CheckCompletionMethod.Invoke(track, [true]);
        return true;
    }

    /// <summary>
    /// The local player beat the racer, which wins the race for the members too.
    /// </summary>
    private void OnLocalRaceWon() {
        // Won with a member, who knows
        if (_raceWonHere) {
            return;
        }

        _raceWonHere = true;
        if (_raceWonBy.Count > 0) {
            return;
        }

        SendRaceResult(RaceWon);
        var racing = GetCheckedMembers().FindAll(member =>
            _racingMembers.Contains(member.Id) && member.IsInLocalScene
        );
        if (racing.Count > 0) {
            var names = JoinNames(racing.Select(member => member.Username));
            Chat(Lang.Pick(
                racing.Count == 1 ? $"You beat the racer, so {names} wins too!" : $"You beat the racer, so {names} win too!",
                $"你赢了对手，{names} 也算赢！"
            ));
        }
    }

    /// <summary>
    /// A member is at the start line, won or is out.
    /// </summary>
    private void OnRace(ClientPlayerData player, CoopSaveUpdate update) {
        try {
            if (!_checkedMembers.Contains(player.Id) || update.Scene != SceneUtil.GetCurrentSceneName()) {
                return;
            }

            switch (update.PartCount) {
                case RaceReady:
                    OnPartnerRaceReady(player, false);
                    break;
                case RaceReadyAnswer:
                    OnPartnerRaceReady(player, true);
                    break;
                case RaceWon:
                    if (_raceFsm == null || _raceWonHere) {
                        return;
                    }

                    var first = _raceWonBy.Count == 0;
                    _raceWonBy.Add(player.Id);
                    if (first) {
                        Chat(Lang.Pick(
                            _checkedMembers.Count <= 1
                                ? $"{player.Username} beat the racer, so you both win!"
                                : $"{player.Username} beat the racer, so you all win!",
                            _checkedMembers.Count <= 1
                                ? $"{player.Username} 赢了对手，算你们都赢！"
                                : $"{player.Username} 赢了对手，算大家都赢！"
                        ));
                    }

                    Logger.Info($"{player.Username} won the race");
                    break;
                case RaceOut:
                    if (_raceFsm == null) {
                        return;
                    }

                    _raceOutMembers.Add(player.Id);
                    if (_raceHold == null && _raceStarted && !_raceWonHere && _racingMembers.Contains(player.Id)) {
                        var others = GetMembersStillRacing();
                        Chat(others.Count == 0
                            ? Lang.Pick(
                                $"{player.Username} didn't beat the racer. It's up to you now!",
                                $"{player.Username} 没赢过对手，就看你的了！"
                            )
                            : Lang.Pick(
                                $"{player.Username} didn't beat the racer. It's up to the rest of you now!",
                                $"{player.Username} 没赢过对手，就看你们的了！"
                            ));
                    }

                    Logger.Info($"{player.Username} is out of the race");
                    break;
            }
        } catch (Exception e) {
            LogRaceError(e);
        }
    }

    /// <summary>
    /// A member got to the start line: the local player who waits there starts with them once every member in the room
    /// is there, one who is not there yet starts at once when they get there, and one who already started tells them to
    /// start too.
    /// </summary>
    /// <param name="player">The member.</param>
    /// <param name="answer">Whether this answers a word of the local player, which is never answered again.</param>
    private void OnPartnerRaceReady(ClientPlayerData player, bool answer) {
        if (_raceFsm is { } fsm && _raceHold is { IsStart: true }) {
            _raceReadyAt[player.Id] = Time.unscaledTime;
            var room = GetCheckedMembers().FindAll(member => member.IsInLocalScene);
            var waiting = room.FindAll(member => !IsAtRaceStartLine(member.Id));
            if (waiting.Count > 0) {
                var missing = JoinNames(waiting.Select(member => member.Username));
                Chat(Lang.Pick(
                    $"{player.Username} is at the start line too. Still waiting for {missing}.",
                    $"{player.Username} 也到起跑线了。还在等 {missing}。"
                ));
                return;
            }

            RaceTogetherWith(room);
            ReleaseRaceHold(fsm, Lang.Pick(
                $"{player.Username} is at the start line too. Race together!",
                $"{player.Username} 也到起跑线了，一起跑！"
            ));
            return;
        }

        // They got there after the local player stopped waiting. How their race ends still counts for everyone, and
        // they are only waiting for this word.
        if (_raceFsm != null && _raceStarted) {
            _racingMembers.Add(player.Id);
            if (!answer) {
                SendRace(player.Id, RaceReadyAnswer);
                Logger.Info($"{player.Username} reached the start line after the race started here");
            }

            return;
        }

        _raceReadyAt[player.Id] = Time.unscaledTime;
        Logger.Info($"{player.Username} is at the start line");
    }

    /// <summary>
    /// Tells the members how the race ended for the local player, once.
    /// </summary>
    private void SendRaceResult(ushort result) {
        if (_raceResultSent) {
            return;
        }

        _raceResultSent = true;
        if (_checkedMembers.Count > 0) {
            SendRace(null, result);
        }
    }

    /// <summary>
    /// Sends a word about the race to one member, or to every member when no member is given; a game takes it only in
    /// the room of the race.
    /// </summary>
    private void SendRace(ushort? targetId, ushort what) {
        var update = new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.Race,
            PartCount = what,
            Scene = SceneUtil.GetCurrentSceneName()
        };

        if (targetId is { } id) {
            update.TargetId = id;
            Send(update);
        } else {
            SendToMembers(update);
        }
    }

    private void LogRaceError(Exception e) {
        if (_raceSyncFailed) {
            return;
        }

        _raceSyncFailed = true;
        Logger.Error($"Could not sync a race of the two-player save:\n{e}");
    }

    /// <summary>
    /// An event of the racer that waits for the partner.
    /// </summary>
    private sealed class RaceHold {
        public RaceHold(string state, string eventName, bool isStart) {
            State = state;
            EventName = eventName;
            IsStart = isStart;
        }

        /// <summary>
        /// The state the racer waits in. Leaving it for any other reason ends the wait.
        /// </summary>
        public string State { get; }

        /// <summary>
        /// The event that is sent on once the wait is over.
        /// </summary>
        public string EventName { get; }

        /// <summary>
        /// Whether it is the start line rather than a lost race.
        /// </summary>
        public bool IsStart { get; }

        /// <summary>
        /// When the wait started.
        /// </summary>
        public float Since { get; } = Time.unscaledTime;
    }
}
