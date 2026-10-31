using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity.Action;
using SSMP.Networking.Packet.Data;
using SSMP.Ui;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The spots of boss rooms where a player challenges the boss to start its fight, with the interact button or a taunt.
/// A challenge switches its spot off, so that the fight it started is not challenged again, but only in the game of the
/// player who made it. In the other player's game the spot stayed as it was all through the fight, on the floor in the
/// middle of it: the interact button there, or a taunt, took control from that player for a challenge that started
/// nothing, and only a hit gave it back. A spot that a player used is now switched off in the games of the other
/// players in the scene as well, and in the game of a player who comes into the scene later. The game of the player
/// who used it tells them.
///
/// Most rooms keep the start of their fight in step between the games by themselves, as it closes gates or shows the
/// title of the boss (see BossRoomCoop.Events). One room's challenge does neither: its spot tells an object of the room,
/// which plays the intro - the camera, the passage that falls in behind the players, the cocoon - and tells the boss to
/// rise. That played out in the game of the player who challenged alone. Made in a game that does not run the boss, it
/// woke nobody, and the player who made it stood frozen until something hit them, as only the boss gives them back. A
/// challenge there now waits until every player is in the room and past the passage, and the game that makes it tells
/// the others what its spot told the intro, so that the intro plays in every game and the scene host's tells the boss
/// to rise. What the intro does to the figure of the player who challenged is kept off the others, and one who
/// challenged in a game that does not run the boss is handed over to its copy, which gives them back when the fight
/// begins (see Entity.HandOver).
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// The name of the FSM of the spots where a player challenges a boss.
    /// </summary>
    private const string ChallengeFsmName = "Challenge";

    /// <summary>
    /// The name of the variable of a challenge spot's FSM that holds the object its challenge is told to.
    /// </summary>
    private const string ReceiverVariableName = "Receiver";

    /// <summary>
    /// The event that the interact button sends to the FSM of what it was pressed on.
    /// </summary>
    private const string InteractEventName = "INTERACT";

    /// <summary>
    /// The event that a taunt of the local player sends to the challenge spot they stand in, instead of taunting.
    /// </summary>
    private const string SpecialChallengeEventName = "SPECIAL CHALLENGE";

    /// <summary>
    /// How often, in seconds, a spot that another player used is looked at again while it can't be switched off yet.
    /// </summary>
    private const float ChallengeSpotRetryTime = 0.5f;

    /// <summary>
    /// How long, in seconds, what another player's challenge told the receiver of its spot waits for the receiver here
    /// to take it.
    /// </summary>
    private const float ToldChallengeTime = 10f;

    /// <summary>
    /// How often, in seconds, what another player's challenge told is tried again here while nothing takes it yet.
    /// </summary>
    private const float ToldChallengeRetryTime = 0.25f;

    /// <summary>
    /// How long, in seconds, after the local player's challenge what its receiver tells a creature hands the local
    /// player over to it. The intro of the challenge tells the boss to rise about twelve seconds in.
    /// </summary>
    private const float ChallengeHandOverTime = 30f;

    /// <summary>
    /// How far, in units, a player must be inside a gate that the intro of a challenge shuts for it to start: one who
    /// stands right by it could step into it before the other game's intro shuts it.
    /// </summary>
    private const float ChallengeGateMargin = 3f;

    /// <summary>
    /// How often, in seconds, the local player is told at most that their challenge waits for the other players.
    /// </summary>
    private const float ChallengeNoticeInterval = 5f;

    /// <summary>
    /// What separates what a challenge told the receiver of its spot, in the word that tells the other players.
    /// </summary>
    private const char ChallengeTellSeparator = '\n';

    /// <summary>
    /// The paths of the challenge spots of the current scene that a player used, in this game or in another one.
    /// </summary>
    private readonly HashSet<string> _usedChallengeSpots = [];

    /// <summary>
    /// The paths of the challenge spots that another player used, which are still to be switched off here.
    /// </summary>
    private readonly List<string> _challengeSpotsToSwitchOff = [];

    /// <summary>
    /// When the spots still to be switched off are looked at again, in unscaled time.
    /// </summary>
    private float _nextChallengeSpotTry;

    /// <summary>
    /// What the local player's challenges told the receivers of their spots in this visit, in order, by the path of the
    /// receiver.
    /// </summary>
    private readonly Dictionary<string, List<string>> _localChallengeTells = new();

    /// <summary>
    /// The receivers that a challenge of the local player set going in this visit, with when, in unscaled time.
    /// </summary>
    private readonly Dictionary<GameObject, float> _challengedReceivers = new();

    /// <summary>
    /// What other players' challenges told the receivers of their spots, to be told to this game's receivers, by the
    /// path of the receiver.
    /// </summary>
    private readonly Dictionary<string, ToldChallenge> _toldChallenges = new();

    /// <summary>
    /// The rooms whose challenge another player made, which this game's receiver was told while the local player had
    /// not challenged there.
    /// </summary>
    private readonly HashSet<BossRoom> _roomsChallengedElsewhere = [];

    /// <summary>
    /// When what other players' challenges told is tried again, in unscaled time.
    /// </summary>
    private float _nextToldChallengeTry;

    /// <summary>
    /// The gates that the intro of the challenge at each spot shuts.
    /// </summary>
    private readonly Dictionary<Fsm, List<Collider2D>> _challengeGates = new();

    /// <summary>
    /// When the local player may be told again that their challenge waits for the other players, in unscaled time.
    /// </summary>
    private float _nextChallengeNoticeTime;

    /// <summary>
    /// Tells the other players in the scene when the local player used a challenge spot, which its challenge shows by
    /// switching off the spot's own collider.
    /// </summary>
    private void NoteChallengeSpotUse(Fsm fsm) {
        if (fsm.Name != ChallengeFsmName || fsm.GameObject is not { } spot || !spot ||
            spot.GetComponent<Collider2D>() is not { enabled: false } || GetInfo(fsm).IsEntity) {
            return;
        }

        var path = ScenePath.Get(spot.transform);
        if (!_usedChallengeSpots.Add(path)) {
            return;
        }

        Logger.Info($"The local player used challenge spot '{path}', switching it off for the other players");
        Send(BossRoomUpdateKind.ChallengeUsed, path, fsm.Name, "", "", "");
    }

    /// <summary>
    /// Switches off a challenge spot that another player in the scene used, now or once it waits for the local player.
    /// </summary>
    private void OnChallengeUsed(BossRoomUpdate update) {
        if (update.Path.Length == 0 || !_usedChallengeSpots.Add(update.Path)) {
            return;
        }

        Logger.Info($"Another player used challenge spot '{update.Path}'");
        _challengeSpotsToSwitchOff.Add(update.Path);
        _nextChallengeSpotTry = 0f;
        SwitchOffUsedChallengeSpots();
    }

    /// <summary>
    /// Tells a player who came into the scene which challenge spots were used before they came.
    /// </summary>
    private void TellUsedChallengeSpotsToNewPlayer() {
        foreach (var path in _usedChallengeSpots) {
            Send(BossRoomUpdateKind.ChallengeUsed, path, ChallengeFsmName, "", "", "");
        }
    }

    /// <summary>
    /// Switches off the spots that other players used once each waits for the local player to walk into it or to
    /// challenge in it. A spot that the local player is challenging in plays out as it does in the game of the player
    /// who challenged first.
    /// </summary>
    private void SwitchOffUsedChallengeSpots() {
        if (_challengeSpotsToSwitchOff.Count == 0 || Time.unscaledTime < _nextChallengeSpotTry) {
            return;
        }

        _nextChallengeSpotTry = Time.unscaledTime + ChallengeSpotRetryTime;
        var sceneName = SceneManager.GetActiveScene().name;
        for (var i = _challengeSpotsToSwitchOff.Count - 1; i >= 0; i--) {
            var path = _challengeSpotsToSwitchOff[i];
            if (ScenePath.Find(path, sceneName) is not { } spot ||
                PlayMakerFSM.FindFsmOnGameObject(spot, ChallengeFsmName)?.Fsm is not { } fsm) {
                continue;
            }

            // The local player used it as well
            if (spot.GetComponent<Collider2D>() is not { enabled: true }) {
                _challengeSpotsToSwitchOff.RemoveAt(i);
                continue;
            }

            // A challenge reaches the fight of the other games in a room whose fight start is kept in step between the
            // games, which takes a state that closes its gates or shows the title of its boss, and in one whose spot
            // tells an object of the room, as the challenge is told to every game then (see TellsEveryGame). Anywhere
            // else it plays its part of the fight in the game of the player who made it alone, so the spot stays, as
            // this game could not start its own part otherwise.
            if (!ReachesEveryGame(fsm)) {
                continue;
            }

            // Not started yet, passing between states, or the local player is challenging in it right now
            if (!spot.activeInHierarchy || fsm.ActiveState is not { } state || !WaitsForThePlayer(state) ||
                spot.GetComponent<InteractableBase>() is { } interactable &&
                InteractManager.BlockingInteractable == interactable) {
                continue;
            }

            _challengeSpotsToSwitchOff.RemoveAt(i);
            Logger.Info($"Switching off challenge spot '{path}', which another player used ('{state.Name}')");
            try {
                UndoWhatTheStateSet(fsm, state);
                spot.SetActive(false);
            } catch (Exception e) {
                Logger.Warn($"Could not switch off challenge spot '{path}':\n{e}");
            }
        }
    }

    /// <summary>
    /// Holds back a challenge at a spot that is told to every game (see TellsEveryGame) until every player is in its
    /// room and none of them in or behind a gate that its intro shuts: the interact button or a taunt there before then
    /// only says that the local player waits. Like a sign of a player (USER 10-14), it is not kept for later. The
    /// interaction that the button began ends by itself and gives control back, as nothing took the event
    /// (PlayMakerNPC.OnStartDialogue), and the taunt sends nothing but the challenge.
    /// </summary>
    /// <returns>Whether the challenge is held back.</returns>
    private bool TryHoldChallenge(Fsm fsm, FsmState state, string eventName) {
        if (fsm.Name != ChallengeFsmName || eventName is not (InteractEventName or SpecialChallengeEventName) ||
            !CanTakeEvent(fsm, state, eventName) || GetBossRoom(fsm) is not { } room || !TellsEveryGame(fsm, room)) {
            return false;
        }

        if (DescribeWhoKeepsChallengeWaiting(fsm, room) is not { } reason) {
            Logger.Info($"Challenge '{eventName}' at '{GetPath(fsm)}' in '{state.Name}' goes on: every player is in the room");
            OnBossFightStarting(room);
            return false;
        }

        Logger.Info($"Holding back challenge '{eventName}' at '{GetPath(fsm)}' in '{state.Name}': {reason}");
        if (Time.unscaledTime >= _nextChallengeNoticeTime) {
            _nextChallengeNoticeTime = Time.unscaledTime + ChallengeNoticeInterval;
            UiManager.InternalChatBox.AddMessage(WaitingMessage);
            Send(BossRoomUpdateKind.Waiting, "", "", "", "", "");
        }

        return true;
    }

    /// <summary>
    /// Says what keeps a challenge at a spot from starting the fight, or null when nothing does: a player who is not in
    /// the spot's room - in another scene, out of it, or the partner of a two-player save who is not on the server - or
    /// one who stands in, behind or right by a gate that the intro of the challenge shuts, like the passage that falls
    /// in behind the players, which counts as inside the room's camera locks.
    /// </summary>
    private string? DescribeWhoKeepsChallengeWaiting(Fsm spot, BossRoom room) {
        if (!AreAllInRoom(GetRoomShape(room, GetLocalAnchor(room, spot)))) {
            return "not every player is in the room";
        }

        var gates = GetChallengeGates(spot);
        if (!IsHoldActive() || gates.Count == 0 || spot.GameObject is not { } owner ||
            HeroController.instance is not { } hero) {
            return null;
        }

        var inside = (Vector2) owner.transform.position;
        if (IsOutsideGates(GetBody(hero, hero.transform.position), gates, inside, ChallengeGateMargin)) {
            return "the local player is in, behind or right by a gate that the challenge shuts";
        }

        foreach (var playerData in _playerData.Values) {
            if (playerData.PlayerContainer is { } container && container &&
                IsOutsideGates(GetBody(hero, container.transform.position), gates, inside, ChallengeGateMargin)) {
                return $"player {playerData.Id} is in, behind or right by a gate that the challenge shuts";
            }
        }

        return null;
    }

    /// <summary>
    /// Notes what an FSM tells another object, for challenges that are told to every game: what the local player's
    /// challenge spot tells its receiver goes to the other players in the scene, and what that receiver tells a creature
    /// that another game runs, while the challenge holds the local player, hands them over to the creature. The
    /// receiver hands over only for a while after the challenge, as later on something else holds the player.
    /// </summary>
    private void NoteChallengeTell(Fsm fsm, FsmEventTarget eventTarget, string eventName) {
        // Cheap tests first, as this hears every event that an FSM sends to another object
        var isSpot = fsm.Name == ChallengeFsmName;
        if ((!isSpot && _challengedReceivers.Count == 0) || fsm.GameObject is not { } owner) {
            return;
        }

        if (!isSpot) {
            if (!_challengedReceivers.TryGetValue(owner, out var challengedAt)) {
                return;
            }

            if (Time.unscaledTime - challengedAt > ChallengeHandOverTime) {
                _challengedReceivers.Remove(owner);
                return;
            }
        }

        try {
            if (GetTarget(fsm, eventTarget) is not { } target) {
                return;
            }

            if (!isSpot) {
                Entity.Entity.HandOverTheLocalPlayerTo(
                    target, $"intro of the challenge ('{eventName}' from '{GetPath(fsm)}')"
                );
                return;
            }

            if (GetReceiver(fsm) != target || GetBossRoom(fsm) is not { } room || !TellsEveryGame(fsm, room)) {
                return;
            }

            var path = ScenePath.Get(target.transform);
            if (!_localChallengeTells.TryGetValue(path, out var told)) {
                told = [];
                _localChallengeTells[path] = told;
            }

            told.Add(eventName);
            if (!_challengedReceivers.ContainsKey(target)) {
                _challengedReceivers[target] = Time.unscaledTime;
            }

            Logger.Info(
                $"The local player's challenge at '{GetPath(fsm)}' told '{path}' '{eventName}', telling the other players"
            );
            Send(
                BossRoomUpdateKind.ChallengeTold, path, fsm.Name, ScenePath.Get(owner.transform), "",
                string.Join(ChallengeTellSeparator.ToString(), told)
            );
        } catch (Exception e) {
            Logger.Warn($"Could not note what '{fsm.Name}' told another object:\n{e}");
        }
    }

    /// <summary>
    /// Takes what another player's challenge told the receiver of its spot, which this game's receiver is told once it
    /// takes it (DeliverToldChallenges). Each word carries all that the challenge told so far, in order, so a word that
    /// comes late or twice changes nothing.
    /// </summary>
    private void OnChallengeTold(BossRoomUpdate update) {
        if (update.Path.Length == 0) {
            return;
        }

        var events = update.EventName.Split(ChallengeTellSeparator).Where(name => name.Length > 0).ToList();
        if (!_toldChallenges.TryGetValue(update.Path, out var told)) {
            told = new ToldChallenge();
            _toldChallenges[update.Path] = told;
        }

        if (events.Count <= told.Events.Count) {
            return;
        }

        Logger.Info(
            $"Another player's challenge at '{update.FromState}' told '{update.Path}': {string.Join(", ", events)}"
        );
        told.Events.Clear();
        told.Events.AddRange(events);
        told.GiveUpTime = Time.unscaledTime + ToldChallengeTime;
        _nextToldChallengeTry = 0f;
        DeliverToldChallenges();
    }

    /// <summary>
    /// Tells this game's receivers of challenge spots what other players' challenges told theirs, each word once an FSM
    /// of the receiver takes it, so that the intro of the room plays here as well and the scene host's tells its boss to
    /// rise. What the intro does to the figure of the player who challenged is kept off the local player first. A word
    /// that nothing takes in time, like when the local player challenged too and the intro went on from theirs, is let
    /// go of with what came after it.
    /// </summary>
    private void DeliverToldChallenges() {
        if (_toldChallenges.Count == 0 || Time.unscaledTime < _nextToldChallengeTry) {
            return;
        }

        // Nearly always all told already, and this runs every frame
        var isPending = false;
        foreach (var told in _toldChallenges.Values) {
            isPending |= told.Delivered < told.Events.Count;
        }

        if (!isPending) {
            return;
        }

        _nextToldChallengeTry = Time.unscaledTime + ToldChallengeRetryTime;
        var sceneName = SceneManager.GetActiveScene().name;
        foreach (var pair in _toldChallenges) {
            var told = pair.Value;
            while (told.Delivered < told.Events.Count) {
                var eventName = told.Events[told.Delivered];
                var takers = FindToldChallengeTakers(pair.Key, sceneName, eventName);
                if (takers.Count == 0) {
                    if (Time.unscaledTime >= told.GiveUpTime) {
                        Logger.Info(
                            $"Nothing at '{pair.Key}' took what another player's challenge told it, letting go of: " +
                            string.Join(", ", told.Events.Skip(told.Delivered))
                        );
                        told.Delivered = told.Events.Count;
                    }

                    break;
                }

                told.Delivered++;
                told.GiveUpTime = Time.unscaledTime + ToldChallengeTime;
                foreach (var fsm in takers) {
                    TellToldChallenge(pair.Key, fsm, eventName, told);
                }
            }
        }
    }

    /// <summary>
    /// Tells an FSM of this game's receiver at a path an event that another player's challenge told theirs.
    /// </summary>
    private void TellToldChallenge(string path, Fsm fsm, string eventName, ToldChallenge told) {
        try {
            if (told.KeptOff.Add(fsm)) {
                KeepChallengeOffLocalPlayer(fsm);
            }

            if (GetBossRoom(fsm) is { } room) {
                if (!_localChallengeTells.ContainsKey(path)) {
                    _roomsChallengedElsewhere.Add(room);
                }

                if (!told.Started) {
                    told.Started = true;
                    OnBossFightStarting(room);
                }
            }
        } catch (Exception e) {
            Logger.Warn($"Could not ready '{GetPath(fsm)}' for what another player's challenge told it:\n{e}");
        }

        Logger.Info(
            $"Telling '{GetPath(fsm)}' in '{fsm.ActiveState?.Name}' '{eventName}', which another player's challenge " +
            "told it"
        );
        try {
            fsm.Event(eventName);
        } catch (Exception e) {
            Logger.Warn($"Could not tell '{GetPath(fsm)}' '{eventName}':\n{e}");
        }
    }

    /// <summary>
    /// Whether the game is about to give control back to the local player, or to start their figure's animation over,
    /// for a creature of a room whose challenge another player made, while the local player has both: the creature
    /// gives back the player whom its challenge took, who is in another game. Giving back what was never taken still
    /// sets the figure going from standing, cutting short a run or a hard landing, and plays the end of a run, at the
    /// start of the fight.
    /// </summary>
    /// <param name="isFree">Whether the local player has what is being given back.</param>
    private bool IsGiveBackForTheChallenger(bool isFree) {
        if (_roomsChallengedElsewhere.Count == 0 || !isFree || FsmExecutionStack.ExecutingFsm is not { } sender ||
            !GetInfo(sender).IsEntity || GetBossRoom(sender) is not { } room ||
            !_roomsChallengedElsewhere.Contains(room)) {
            return false;
        }

        Logger.Info(
            $"'{GetPath(sender)}' gives back the player who challenged in another game, leaving the local player as " +
            "they are"
        );
        return true;
    }

    /// <summary>
    /// Finds the FSMs of this game's receiver at a path that take an event in the state they are in. A receiver that is
    /// a creature takes nothing here, as it moves only in the game that runs it.
    /// </summary>
    private static List<Fsm> FindToldChallengeTakers(string path, string sceneName, string eventName) {
        var takers = new List<Fsm>();
        if (ScenePath.Find(path, sceneName) is not { } receiver || !receiver.activeInHierarchy ||
            Entity.Entity.FindByPart(receiver) != null) {
            return takers;
        }

        foreach (var playMakerFsm in receiver.GetComponents<PlayMakerFSM>()) {
            var fsm = playMakerFsm.Fsm;
            if (!playMakerFsm.enabled || fsm == null || !fsm.Started || fsm.IsSwitchingState ||
                fsm.ActiveState is not { } state || !CanTakeEvent(fsm, state, eventName)) {
                continue;
            }

            takers.Add(fsm);
        }

        return takers;
    }

    /// <summary>
    /// Switches off the actions of an FSM of a receiver that do something to the local player's figure - turn it, play
    /// its part of the challenge, move it, or send it a message, an event or a value - as the intro does that to the
    /// player who challenged, who is someone else. Sounds played where the figure is are left. The FSM is the room's own
    /// and plays its intro once a visit, so the actions stay off.
    /// </summary>
    private static void KeepChallengeOffLocalPlayer(Fsm fsm) {
        var kept = new List<string>();
        foreach (var state in fsm.States ?? []) {
            foreach (var action in state.Actions ?? []) {
                if (action is not { Enabled: true } || !IsDoneToTheChallenger(action)) {
                    continue;
                }

                action.Enabled = false;
                kept.Add($"{action.GetType().Name} in '{state.Name}'");
            }
        }

        if (kept.Count > 0) {
            Logger.Info(
                $"Keeping what '{GetPath(fsm)}' does to the player who challenged off the local player: " +
                string.Join(", ", kept)
            );
        }
    }

    /// <summary>
    /// Whether an action of the intro of a challenge does something to the local player's figure that is about the
    /// player who challenged.
    /// </summary>
    private static bool IsDoneToTheChallenger(FsmStateAction action) {
        var name = action.GetType().Name;
        return (name.StartsWith("Tk2dPlay", StringComparison.Ordinal) ||
                name.StartsWith("FaceObject", StringComparison.Ordinal) ||
                name.StartsWith("SendMessage", StringComparison.Ordinal) ||
                name.StartsWith("SendEventByName", StringComparison.Ordinal) ||
                name.StartsWith("SetFsm", StringComparison.Ordinal) ||
                name.StartsWith("SetPosition", StringComparison.Ordinal) ||
                name.StartsWith("SetVelocity", StringComparison.Ordinal)) &&
               EntityFsmActions.WorksOnThePlayerCharacter(action, false);
    }

    /// <summary>
    /// Whether a challenge at a spot reaches the fight of every game: through the start of the room's fight, which the
    /// games keep in step (see BossRoomCoop.Events), or by telling every game itself (see TellsEveryGame).
    /// </summary>
    private bool ReachesEveryGame(Fsm spot) {
        return GetBossRoom(spot) is { } room && (HasFightStates(room) || TellsEveryGame(spot, room));
    }

    /// <summary>
    /// Whether a challenge at a spot is told to every game, which plays the intro of the room there (see
    /// OnChallengeTold): the room does not keep the start of its fight in step by itself, as nothing of it closes gates
    /// or shows a title, and the spot tells an object of the room, which every game runs, rather than a creature.
    /// </summary>
    private static bool TellsEveryGame(Fsm spot, BossRoom room) {
        return !HasFightStates(room) && GetReceiver(spot) is { } receiver && Entity.Entity.FindByPart(receiver) == null;
    }

    /// <summary>
    /// Whether an object of a boss room starts its fight in a state that closes gates or shows the title of a boss.
    /// </summary>
    private static bool HasFightStates(BossRoom room) {
        return room.BaseFightStates.Values.Any(states => states.Count > 0);
    }

    /// <summary>
    /// Gets the object that a challenge spot tells its challenge, or null.
    /// </summary>
    private static GameObject? GetReceiver(Fsm spot) {
        var receiver = spot.Variables.FindFsmGameObject(ReceiverVariableName)?.Value;
        return receiver != null ? receiver : null;
    }

    /// <summary>
    /// Gets the object that an FSM sends an event to, or null for an event to itself or to many objects.
    /// </summary>
    private static GameObject? GetTarget(Fsm fsm, FsmEventTarget eventTarget) {
        var target = eventTarget.target switch {
            FsmEventTarget.EventTarget.GameObject or FsmEventTarget.EventTarget.GameObjectFSM =>
                fsm.GetOwnerDefaultTarget(eventTarget.gameObject),
            FsmEventTarget.EventTarget.FSMComponent => eventTarget.fsmComponent != null
                ? eventTarget.fsmComponent.gameObject
                : null,
            _ => null
        };

        return target != null ? target : null;
    }

    /// <summary>
    /// Gets the gates that the intro of the challenge at a spot shuts: the solid colliders in the objects that the FSMs
    /// of the spot's receiver switch on, whichever way they lie, as the passage that falls in behind the players fills it
    /// from end to end. Kept once the receiver's FSMs have found what they switch on.
    /// </summary>
    private List<Collider2D> GetChallengeGates(Fsm spot) {
        if (_challengeGates.TryGetValue(spot, out var gates)) {
            return gates;
        }

        gates = [];
        var isFound = true;
        if (GetReceiver(spot) is { } receiver) {
            foreach (var playMakerFsm in receiver.GetComponents<PlayMakerFSM>()) {
                if (playMakerFsm.Fsm is not { } fsm) {
                    continue;
                }

                isFound &= fsm.Started;
                foreach (var state in fsm.States ?? []) {
                    foreach (var action in state.Actions ?? []) {
                        if (action is not ActivateGameObject { Enabled: true } activate ||
                            activate.activate?.Value != true ||
                            fsm.GetOwnerDefaultTarget(activate.gameObject) is not { } target ||
                            receiver.transform.IsChildOf(target.transform)) {
                            continue;
                        }

                        foreach (var collider in target.GetComponentsInChildren<Collider2D>(true)) {
                            if (!collider.isTrigger && !gates.Contains(collider)) {
                                gates.Add(collider);
                            }
                        }
                    }
                }
            }
        }

        if (isFound) {
            _challengeGates[spot] = gates;
        }

        return gates;
    }

    /// <summary>
    /// Forgets the challenge spots of the previous scene.
    /// </summary>
    private void ClearChallengeSpots() {
        _usedChallengeSpots.Clear();
        _challengeSpotsToSwitchOff.Clear();
        _nextChallengeSpotTry = 0f;
        _localChallengeTells.Clear();
        _challengedReceivers.Clear();
        _toldChallenges.Clear();
        _roomsChallengedElsewhere.Clear();
        _nextToldChallengeTry = 0f;
        _challengeGates.Clear();
    }

    /// <summary>
    /// Whether a state of a challenge spot waits for the local player: to walk into it, or to challenge in it.
    /// </summary>
    private static bool WaitsForThePlayer(FsmState state) {
        foreach (var action in state.Actions ?? []) {
            if (action is Trigger2dEvent) {
                return true;
            }
        }

        foreach (var transition in state.Transitions ?? []) {
            if (transition.EventName == InteractEventName) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Undoes what a state of a spot set on other FSMs, like on the local player's while they stand in it, which
    /// leaving the spot would have undone: standing in a challenge spot makes the local player's taunt a challenge.
    /// </summary>
    private static void UndoWhatTheStateSet(Fsm fsm, FsmState state) {
        foreach (var action in state.Actions ?? []) {
            if (action is not SetFsmBool { setValue.Value: true } set || set.fsmName?.Value is not { Length: > 0 } fsmName ||
                set.variableName?.Value is not { Length: > 0 } variableName ||
                fsm.GetOwnerDefaultTarget(set.gameObject) is not { } target ||
                PlayMakerFSM.FindFsmOnGameObject(target, fsmName)?.FsmVariables.GetFsmBool(variableName) is not { } variable) {
                continue;
            }

            variable.Value = false;
        }
    }

    /// <summary>
    /// What another player's challenge told the receiver of its spot, and how much of it this game's receiver was told.
    /// </summary>
    private sealed class ToldChallenge {
        /// <summary>
        /// The events, in the order the challenge told them.
        /// </summary>
        public readonly List<string> Events = [];

        /// <summary>
        /// How many of the events this game's receiver was told, or let go of.
        /// </summary>
        public int Delivered;

        /// <summary>
        /// When the next event is let go of if nothing takes it, in unscaled time.
        /// </summary>
        public float GiveUpTime;

        /// <summary>
        /// Whether the fight was said to start for it.
        /// </summary>
        public bool Started;

        /// <summary>
        /// The FSMs of the receiver whose actions on the local player's figure are switched off.
        /// </summary>
        public readonly HashSet<Fsm> KeptOff = [];
    }
}
