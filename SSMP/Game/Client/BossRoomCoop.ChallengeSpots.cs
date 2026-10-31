using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Packet.Data;
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
/// who used it tells them; their own fight does not say so in every room, as one room's fight neither closes gates nor
/// shows a title. Nothing keeps that room's start in step, so its challenge plays out in the game of the player who made
/// it alone, and the other games keep their spot to start their own part.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// The name of the FSM of the spots where a player challenges a boss.
    /// </summary>
    private const string ChallengeFsmName = "Challenge";

    /// <summary>
    /// The event that the interact button sends to the FSM of what it was pressed on.
    /// </summary>
    private const string InteractEventName = "INTERACT";

    /// <summary>
    /// How often, in seconds, a spot that another player used is looked at again while it can't be switched off yet.
    /// </summary>
    private const float ChallengeSpotRetryTime = 0.5f;

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

            // A challenge reaches the fight of the other games only in a room whose fight start is kept in step between
            // the games, which takes a state that closes its gates or shows the title of its boss. In a room without
            // one the challenge plays its part of the fight in the game of the player who made it alone, so the spot
            // stays, as this game could not start its own part otherwise.
            if (GetBossRoom(fsm) is not { } room || !room.BaseFightStates.Values.Any(states => states.Count > 0)) {
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
    /// Forgets the challenge spots of the previous scene.
    /// </summary>
    private void ClearChallengeSpots() {
        _usedChallengeSpots.Clear();
        _challengeSpotsToSwitchOff.Clear();
        _nextChallengeSpotTry = 0f;
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
}
