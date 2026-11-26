using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The tries that each player makes in their own game at a moment of the fight with a creature that every game runs by
/// itself, one success of which is everyone's (see EntityRegistryEntry.Tries): the parry at the prompt in the last
/// clash with the boss of the organ, which each game plays with its own player's character, and at which the boss
/// falls, or else takes the character back into its fight with a blow (USER 11-26 "一人成功就都算赢"). As each game
/// played it before, the scene host's game went the way that its own player's try went, and every other game was
/// taken that way at its step: a miss of the scene host's player took the boss back into its fight in every game,
/// though the player of another had made their parry there, and a parry of theirs fell, to be taken back by that
/// step.
///
/// The game of a player who succeeds goes on into the success at once and tells every other game in the room, each of
/// which goes on into it as its creature comes to the try, whether its own player missed by then or not (see
/// <see cref="UpdateTries"/>): the boss falls in every game, and each player gets what it gives. A miss waits: a game
/// that follows the scene host's holds it and tells the scene host's game, whose own miss waits for those of every
/// other player in the room, and then plays, which every other game takes as its step. A player who missed tries no
/// more: their game holds the miss, and a press after it would have gone on into the success there while the scene
/// host's game took the miss.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What an update of a creature that every game runs by itself is when it tells how the try of the player of the
    /// game that sent it went (see <see cref="TellTry"/>): <see cref="CoopSaveUpdate.Key"/> is 1 for a success, and 0
    /// for a miss.
    /// </summary>
    private const ushort OwnTryPart = 21;

    /// <summary>
    /// The tries at the creatures' state machines here that are not played out yet, by the state machine (see
    /// <see cref="OwnTry"/>).
    /// </summary>
    private readonly Dictionary<Fsm, OwnTry> _ownTries = new();

    /// <summary>
    /// A try at a state machine of a creature that every game runs by itself, until the state machine goes out of the
    /// state of the try: whether another game told of a success, which plays as the creature comes to the try here (see
    /// <see cref="UpdateTries"/>), the way of the miss of this game's player, which waits, and, in the scene host's
    /// game, the players whose games told of a miss.
    /// </summary>
    private sealed class OwnTry {
        public bool Won;
        public FsmEvent? Miss;
        public readonly HashSet<ushort> Missed = [];
    }

    /// <summary>
    /// Holds a step of a state machine of a creature that every game runs by itself out of the state of a try (see
    /// OwnCreatures.IsTry) where it waits for the tries of the other players, returning whether it does: a miss of this
    /// game's player, and a press of theirs after it. A success goes on, which this game tells every other game of if
    /// its own player made it, and so does a step of the scene host's that this game is set onto, which it takes once
    /// every player missed.
    /// </summary>
    private bool HoldsTry(Fsm fsm, FsmState toState) {
        if (!OwnCreatures.HasTries || fsm.FsmComponent is not { } machine || fsm.ActiveState is not { } at ||
            at == toState || !OwnCreatures.IsTry(machine, at.Name, out var success)) {
            return false;
        }

        _ownTries.TryGetValue(fsm, out var tried);
        var way = fsm.LastTransition is { } last && last.ToState == toState.Name ? last : null;
        var bySceneHost = way == null || OwnCreatures.PlaysSceneHostsStep(fsm);
        var place = OwnCreatures.PlaceOf(fsm.GameObject);
        if (!bySceneHost && way!.EventName != success) {
            // A success that another game told plays at the next frame, over the miss (see UpdateTries)
            if (tried is { Won: true }) {
                MachineSwitchToStateField?.SetValue(fsm, null);
                return true;
            }

            return HoldsMiss(machine, at, way, tried);
        }

        if (!bySceneHost && tried?.Miss != null && Fsm.EventData?.SentByFsm == fsm) {
            Logger.Info(
                $"[Own creatures] The local player pressed in '{at.Name}' of '{fsm.Name}' of '{place}' after they " +
                "missed their try there, which counts no more"
            );
            MachineSwitchToStateField?.SetValue(fsm, null);
            return true;
        }

        _ownTries.Remove(fsm);
        _heldOwnSteps.Remove(fsm);
        if (bySceneHost || tried is { Won: true }) {
            return false;
        }

        Logger.Info(
            $"[Own creatures] The local player made their try in '{at.Name}' of '{fsm.Name}' of '{place}', which " +
            $"goes on into '{toState.Name}' here, as every other game in the room is told"
        );
        TellTry(machine, at, true);
        return false;
    }

    /// <summary>
    /// Holds a miss of this game's player at the try in a state (see <see cref="HoldsTry"/>), returning whether it
    /// does: a game that follows the scene host's holds it for the scene host's step and tells the scene host's game,
    /// which holds its own until every other player in the room missed too, and then lets it go on (see
    /// <see cref="UpdateTries"/>).
    /// </summary>
    private bool HoldsMiss(PlayMakerFSM machine, FsmState at, FsmTransition way, OwnTry? tried) {
        var fsm = machine.Fsm;
        if (tried == null) {
            tried = new OwnTry();
            _ownTries[fsm] = tried;
        }

        var first = tried.Miss == null;
        tried.Miss = way.FsmEvent;
        var place = OwnCreatures.PlaceOf(machine.gameObject);
        if (OwnCreatures.FollowsSceneHost(machine.gameObject)) {
            if (first) {
                Logger.Info(
                    $"[Own creatures] The local player missed their try in '{at.Name}' of '{fsm.Name}' of '{place}', " +
                    "which waits here for the scene host's step, as the scene host's game is told"
                );
                TellTry(machine, at, false);
            }

            _heldOwnSteps[fsm] = at.Name;
            _ownWays.Remove(fsm);
        } else if (StillTrying(tried) is not { } waitsFor) {
            _ownTries.Remove(fsm);
            Logger.Info(
                $"[Own creatures] Every player in the room missed their try in '{at.Name}' of '{fsm.Name}' of " +
                $"'{place}', which goes on into '{way.ToState}' here, as the members in the room are told"
            );
            return false;
        } else if (first) {
            Logger.Info(
                $"[Own creatures] The local player missed their try in '{at.Name}' of '{fsm.Name}' of '{place}', " +
                $"which waits for the tries of {waitsFor}"
            );
        }

        MachineSwitchToStateField?.SetValue(fsm, null);
        return true;
    }

    /// <summary>
    /// The names of the players in the room whose games have not told of a miss at a try yet, or null when every one
    /// did.
    /// </summary>
    private string? StillTrying(OwnTry tried) {
        var still = GetCheckedMembers().Where(member => member.IsInLocalScene && !tried.Missed.Contains(member.Id))
            .Select(member => member.Username).ToList();
        return still.Count > 0 ? string.Join(", ", still) : null;
    }

    /// <summary>
    /// Tells every other game in the room how the try of this game's player in a state of a state machine of a creature
    /// that every game runs by itself went (see <see cref="TakeTry"/>).
    /// </summary>
    private void TellTry(PlayMakerFSM machine, FsmState at, bool won) {
        var creature = machine.gameObject;
        SendToRoomMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.OwnCreature,
            Scene = creature.scene.name,
            ObjectPath = OwnCreatures.PlaceOf(creature),
            FsmName = machine.FsmName,
            StateName = at.Name,
            PartCount = OwnTryPart,
            Key = won ? 1UL : 0UL
        });
    }

    /// <summary>
    /// Takes how the try of the player of another game went there (see <see cref="TellTry"/>): a success plays here as
    /// the creature comes to the try, and a miss counts in the scene host's game, which waits for every player's.
    /// </summary>
    private void TakeTry(ClientPlayerData player, GameObject creature, CoopSaveUpdate update) {
        var fsm = PlayMakerFSM.FindFsmOnGameObject(creature, update.FsmName).Fsm;
        if (!_ownTries.TryGetValue(fsm, out var tried)) {
            tried = new OwnTry();
            _ownTries[fsm] = tried;
        }

        var told = $"{player.Username} {(update.Key == 1 ? "made" : "missed")} their try in '{update.StateName}' of " +
                   $"'{update.FsmName}' of '{update.ObjectPath}' there";
        if (update.Key == 1) {
            tried.Won = true;
            Logger.Info(
                $"[Own creatures] {told}, which is everyone's: it goes on into the success here as it comes to the " +
                $"try, from '{fsm.ActiveStateName}'"
            );
            return;
        }

        tried.Missed.Add(player.Id);
        Logger.Info($"[Own creatures] {told}, as the creature is in '{fsm.ActiveStateName}' here");
    }

    /// <summary>
    /// Plays out the tries of the creatures here at their states: a success that another game told goes on as the
    /// creature comes to the try, and, in the scene host's game, a miss of the local player once every other player in
    /// the room missed too, or left it.
    /// </summary>
    private void UpdateTries() {
        if (_ownTries.Count == 0) {
            return;
        }

        foreach (var pair in _ownTries.ToList()) {
            var fsm = pair.Key;
            var tried = pair.Value;
            if (fsm.FsmComponent is not { } machine || machine == null) {
                _ownTries.Remove(fsm);
                continue;
            }

            if (fsm.ActiveState is not { } at || !OwnCreatures.IsTry(machine, at.Name, out var success)) {
                continue;
            }

            if (tried.Won) {
                Logger.Info(
                    $"[Own creatures] '{OwnCreatures.PlaceOf(fsm.GameObject)}' goes on into the success of the try " +
                    $"in '{at.Name}' of '{fsm.Name}' that another game told"
                );
                fsm.Event(at.Transitions.First(transition => transition.EventName == success).FsmEvent);
                continue;
            }

            if (tried.Miss is { } miss && !OwnCreatures.FollowsSceneHost(fsm.GameObject) &&
                StillTrying(tried) == null) {
                fsm.Event(miss);
            }
        }
    }
}
