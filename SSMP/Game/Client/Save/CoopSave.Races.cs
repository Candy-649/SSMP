using System;
using System.Collections.Generic;
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
/// Races against the racer of a room in a checked two-player save, which both players run together and win together.
///
/// Each game has its own racer, its own track and its own count of laps, and only its own hero can cross a line, so a
/// race is still two races: each player runs theirs in their own game. What is shared is how they start and how they
/// end. A race starts only once both players said yes at their own prompt, which the prompts that need both players
/// see to. Both then wait at the start line, in the dark the game puts them in there, until the other one is there
/// too, so that the countdowns of both games start together. And both win as soon as either of them beats the racer:
/// a player who lost is held where the game would tell them so, until the partner either wins - and this game is then
/// told that its player won too - or is out as well.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How long a player waits at the start line for the partner before running alone. The partner may still be
    /// reading what the racer says after the yes, so it is long; the one who waits sees why in the chat.
    /// </summary>
    private const float RaceStartTimeout = 60f;

    /// <summary>
    /// The sender is at the start line.
    /// </summary>
    private const ushort RaceReady = 0;

    /// <summary>
    /// The sender beat the racer, which wins the race for both players.
    /// </summary>
    private const ushort RaceWon = 1;

    /// <summary>
    /// The sender did not beat the racer: they finished after it, were disqualified or gave up.
    /// </summary>
    private const ushort RaceOut = 2;

    /// <summary>
    /// The sender is already running and answers the partner's word that they are at the start line. Kept apart from
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
    /// The wishes of that racer. Their changes don't go to the partner until the race is over, because the game of the
    /// partner decides what a win gives by whether the wish is complete, and a completion of this game that arrived
    /// before that would take the rewards of the last track away from them.
    /// </summary>
    private readonly HashSet<string> _raceWishes = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the local player left the start line.
    /// </summary>
    private bool _raceStarted;

    /// <summary>
    /// The event of the racer that waits for the partner, or null.
    /// </summary>
    private RaceHold? _raceHold;

    /// <summary>
    /// Whether an event that waited is being sent on, which is let through.
    /// </summary>
    private bool _releasingRaceEvent;

    /// <summary>
    /// Whether the local player won this race, by beating the racer or with the partner.
    /// </summary>
    private bool _raceWonHere;

    /// <summary>
    /// Whether the partner was told how the race ended for the local player.
    /// </summary>
    private bool _raceResultSent;

    /// <summary>
    /// Whether the partner runs this race too.
    /// </summary>
    private bool _partnerRacing;

    /// <summary>
    /// Whether the partner beat the racer in this race.
    /// </summary>
    private bool _partnerRaceWon;

    /// <summary>
    /// Whether the partner is out of this race.
    /// </summary>
    private bool _partnerRaceOut;

    /// <summary>
    /// When the partner said they are at the start line before the local player got there. It counts for as long as
    /// the partner waits there.
    /// </summary>
    private float _partnerRaceReadyAt = float.NegativeInfinity;

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
    /// What to ask the partner about starting a race. It names the racer rather than the prompt, because the first race
    /// asks in the box that accepts the wish and every later one in a plain box, and the two players need not be at
    /// the same one.
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
    /// the partner.
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
    /// Starts following a try of a race. What the partner said about the start line is kept, since they may have got
    /// there first.
    /// </summary>
    private void StartRaceRound(Fsm fsm) {
        _raceFsm = fsm;
        _raceStarted = false;
        _raceHold = null;
        _raceWonHere = false;
        _raceResultSent = false;
        _partnerRacing = false;
        _partnerRaceWon = false;
        _partnerRaceOut = false;

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
        _partnerRacing = false;
        _partnerRaceWon = false;
        _partnerRaceOut = false;
        _partnerRaceReadyAt = float.NegativeInfinity;
    }

    /// <summary>
    /// Whether an event of the racer waits for the partner rather than going on: leaving the start line, and hearing
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
    /// Keeps the local player at the start line until the partner is there too.
    /// </summary>
    /// <returns>Whether the start waits.</returns>
    private bool HoldRaceStart() {
        if (GetRacePartner() is not { } partner) {
            _raceStarted = true;
            return false;
        }

        if (!partner.IsInLocalScene) {
            _raceStarted = true;
            Chat(Lang.Pick(
                $"{partner.Username} isn't in this room, so you race alone.",
                $"{partner.Username} 不在这个房间，你自己跑。"
            ));
            return false;
        }

        if (Time.unscaledTime - _partnerRaceReadyAt <= RaceStartTimeout) {
            _partnerRaceReadyAt = float.NegativeInfinity;
            _partnerRacing = true;
            _raceStarted = true;
            SendRace(partner.Id, RaceReady);
            Chat(Lang.Pick(
                $"{partner.Username} is at the start line too. Race together!",
                $"{partner.Username} 也在起跑线上了，一起跑！"
            ));
            Logger.Info($"{partner.Username} was at the start line first, so the race starts at once");
            return false;
        }

        _raceHold = new RaceHold(RaceStartLineState, RaceFinishedEvent, true);
        SendRace(partner.Id, RaceReady);
        Chat(Lang.Pick(
            $"Waiting for {partner.Username} to reach the start line too (at most a minute)...",
            $"等 {partner.Username} 也到起跑线再一起开跑（最多等一分钟）……"
        ));
        Logger.Info($"Waiting at the start line for {partner.Username}");
        return true;
    }

    /// <summary>
    /// Keeps the racer from telling the local player that they lost while the partner may still win for both.
    /// </summary>
    /// <returns>Whether the loss waits.</returns>
    private bool HoldRaceLoss(string state, string eventName) {
        // Taken as a win by the next update, which is where the partner's win is turned into one here
        if (_partnerRaceWon) {
            _raceHold = new RaceHold(state, eventName, false);
            return true;
        }

        SendRaceResult(RaceOut);
        if (_partnerRacing && !_partnerRaceOut && GetRacePartner() is { IsInLocalScene: true } partner) {
            _raceHold = new RaceHold(state, eventName, false);
            Chat(Lang.Pick(
                $"You didn't beat the racer. If {partner.Username} does, you both win.",
                $"你没赢过对手。只要 {partner.Username} 赢了，就算你们都赢。"
            ));
            Logger.Info($"Holding '{eventName}' of the race until {partner.Username} is done");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Lets a race go on once the partner decided it: at the start line, when they got there or stopped coming, and
    /// after a loss, when they won for both or are out too.
    /// </summary>
    private void UpdateRaces(ClientPlayerData? partner) {
        if (_raceFsm is not { } fsm) {
            return;
        }

        try {
            if (fsm.GameObject == null) {
                EndRaceRound();
                return;
            }

            var state = fsm.ActiveStateName;
            var partnerHere = partner != null && partner.IsInLocalScene;
            var partnerName = partner?.Username ??
                              GetCurrentMarker()?.PartnerName ?? Lang.Pick("Your partner", "队友");
            if (_raceHold is { } held) {
                // Something else moved the racer on, which leaves nothing waiting
                if (held.State != state) {
                    _raceHold = null;
                    return;
                }

                if (held.IsStart) {
                    if (!partnerHere) {
                        ReleaseRaceHold(fsm, Lang.Pick(
                            $"{partnerName} isn't in this room any more, so you race alone.",
                            $"{partnerName} 不在这个房间了，你先自己跑。"
                        ));
                    } else if (Time.unscaledTime - held.Since > RaceStartTimeout) {
                        ReleaseRaceHold(fsm, Lang.Pick(
                            $"{partnerName} didn't reach the start line, so you race alone.",
                            $"{partnerName} 一直没到起跑线，你先自己跑。"
                        ));
                    }

                    return;
                }

                if (_partnerRaceWon) {
                    WinRaceWithPartner(fsm);
                } else if (!partnerHere) {
                    ReleaseRaceHold(fsm, Lang.Pick(
                        $"{partnerName} isn't in this room any more, so this race is lost.",
                        $"{partnerName} 不在这个房间了，这一轮算输。"
                    ));
                } else if (_partnerRaceOut) {
                    ReleaseRaceHold(fsm, Lang.Pick(
                        $"{partnerName} didn't beat the racer either, so this race is lost.",
                        $"{partnerName} 也没赢过对手，这一轮算输。"
                    ));
                }

                return;
            }

            if (_partnerRaceWon && !_raceWonHere && state is RaceRunningState or RaceRunnerEndState) {
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
        Logger.Info($"Sending on '{held.EventName}' of the race, which waited for the partner");
        _releasingRaceEvent = true;
        try {
            fsm.Event(held.EventName);
        } finally {
            _releasingRaceEvent = false;
        }
    }

    /// <summary>
    /// Makes the race of the local player won, because the partner beat the racer.
    /// </summary>
    private void WinRaceWithPartner(Fsm fsm) {
        _raceWonHere = true;
        var held = _raceHold;
        _raceHold = null;

        // In the middle of a race the game is told that the hero finished their laps, which is exactly what it hears
        // when the local player wins, so everything that listens for the end of a race hears it as well
        if (held == null && fsm.ActiveStateName == RaceRunningState && FinishHeroLaps(fsm)) {
            Logger.Info("The partner won the race, so the laps of the local player were finished for them");
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

        Logger.Info($"The partner won the race, so it is won here too from '{held?.State ?? RaceRunnerEndState}'");
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
    /// The local player beat the racer, which wins the race for the partner too.
    /// </summary>
    private void OnLocalRaceWon() {
        // Won with the partner, who knows
        if (_raceWonHere) {
            return;
        }

        _raceWonHere = true;
        if (_partnerRaceWon) {
            return;
        }

        SendRaceResult(RaceWon);
        if (_partnerRacing && GetRacePartner() is { IsInLocalScene: true } partner) {
            Chat(Lang.Pick(
                $"You beat the racer, so {partner.Username} wins too!",
                $"你赢了对手，{partner.Username} 也算赢！"
            ));
        }
    }

    /// <summary>
    /// The partner is at the start line, won or is out.
    /// </summary>
    private void OnRace(ClientPlayerData player, CoopSaveUpdate update) {
        try {
            if (_checkedWith != player.Id || update.Scene != SceneUtil.GetCurrentSceneName()) {
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

                    _partnerRaceWon = true;
                    Chat(Lang.Pick(
                        $"{player.Username} beat the racer, so you both win!",
                        $"{player.Username} 赢了对手，算你们都赢！"
                    ));
                    Logger.Info($"{player.Username} won the race");
                    break;
                case RaceOut:
                    if (_raceFsm == null) {
                        return;
                    }

                    _partnerRaceOut = true;
                    if (_raceHold == null && _raceStarted && !_raceWonHere && _partnerRacing) {
                        Chat(Lang.Pick(
                            $"{player.Username} didn't beat the racer. It's up to you now!",
                            $"{player.Username} 没赢过对手，就看你的了！"
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
    /// The partner got to the start line: the local player who waits there starts with them, one who is not there yet
    /// starts at once when they get there, and one who already started tells them to start too.
    /// </summary>
    /// <param name="player">The partner.</param>
    /// <param name="answer">Whether this answers a word of the local player, which is never answered again.</param>
    private void OnPartnerRaceReady(ClientPlayerData player, bool answer) {
        if (_raceFsm is { } fsm && _raceHold is { IsStart: true }) {
            _partnerRacing = true;
            ReleaseRaceHold(fsm, Lang.Pick(
                $"{player.Username} is at the start line too. Race together!",
                $"{player.Username} 也到起跑线了，一起跑！"
            ));
            return;
        }

        // They got there after the local player stopped waiting. How their race ends still counts for both, and they
        // are only waiting for this word.
        if (_raceFsm != null && _raceStarted) {
            _partnerRacing = true;
            if (!answer) {
                SendRace(player.Id, RaceReadyAnswer);
                Logger.Info($"{player.Username} reached the start line after the race started here");
            }

            return;
        }

        _partnerRaceReadyAt = Time.unscaledTime;
        Logger.Info($"{player.Username} is at the start line");
    }

    /// <summary>
    /// Tells the partner how the race ended for the local player, once.
    /// </summary>
    private void SendRaceResult(ushort result) {
        if (_raceResultSent) {
            return;
        }

        _raceResultSent = true;
        if (GetRacePartner() is { } partner) {
            SendRace(partner.Id, result);
        }
    }

    private void SendRace(ushort partnerId, ushort what) {
        Send(new CoopSaveUpdate {
            TargetId = partnerId,
            Kind = CoopSaveUpdateKind.Race,
            PartCount = what,
            Scene = SceneUtil.GetCurrentSceneName()
        });
    }

    /// <summary>
    /// The checked partner, or null.
    /// </summary>
    private ClientPlayerData? GetRacePartner() {
        return _checkedWith is { } id && _playerData.TryGetValue(id, out var partner) ? partner : null;
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
