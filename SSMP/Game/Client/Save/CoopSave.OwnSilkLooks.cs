using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The looks of the creatures that every game runs by itself at the silk of the player that they go after, by which
/// they go on: the handmaiden's as it wakes, as it starts to spin and at each draw of its suck, which takes a silk and
/// breaks off where there is none. Each game has the silk of its own player only, and the scene host's looked at its
/// own player's, whomever the handmaiden sucked. The move from such a look acts on one player and goes by what befalls
/// them, which the game of that player leads (see CoopSave.OwnLedMoves; USER 11-25 "套现有规则": the caught player's
/// game leads): the scene host's game numbers it as its creature makes the look at another player, with the step into
/// it, and takes the steps of that player's game in it (see <see cref="StartsOthersSilkLook"/>), and every other game
/// takes the steps of the scene host's, holding every switch of its own in it, in a look at the scene host's player
/// too (see <see cref="FollowsSilkLookAsTold"/>). What the move does to the player, like the drain that their silk
/// shows and the flash of their character, and what it tells the creature's other state machines to do to them, is
/// kept off the player of every other game (see KeepOut), as their silk is taken in their own game only. With three or
/// four players, the game of the player looked at leads, and every other one shows it.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The field of the player data that holds the silk of the player.
    /// </summary>
    private const string SilkField = "silk";

    /// <summary>
    /// The number of the latest look at the silk of another player that a creature made in the game of a scene host,
    /// this game's or one before it as far as this game heard, after which the next one of this game's goes (see
    /// <see cref="StartsOthersSilkLook"/>, <see cref="NoteSilkLook"/>).
    /// </summary>
    private ulong _silkLooks;

    /// <summary>
    /// The state that looks at silk whose move each state of a state machine is of (see <see cref="LookStateOf"/>).
    /// </summary>
    private readonly Dictionary<Fsm, Dictionary<FsmState, FsmState>> _lookStates = new();

    /// <summary>
    /// Whether a state looks at the silk of the player.
    /// </summary>
    private static bool LooksAtSilk(FsmState state) {
        foreach (var action in state.Actions) {
            if (action.Enabled && action switch {
                    GetPlayerDataInt get => get.intName.Value == SilkField,
                    PlayerdataIntCompare compare => compare.playerdataInt.Value == SilkField,
                    _ => false
                }) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The state that looks at silk whose move a state is of (see CoopSave.MoveOf), the look
    /// itself for one, or null.
    /// </summary>
    private FsmState? LookStateOf(Fsm fsm, FsmState state) {
        if (!_lookStates.TryGetValue(fsm, out var firsts)) {
            firsts = new Dictionary<FsmState, FsmState>();
            foreach (var each in fsm.States) {
                if (LooksAtSilk(each)) {
                    firsts[each] = each;
                }
            }

            foreach (var look in firsts.Values.ToList()) {
                foreach (var inMove in MoveOf(fsm, look)) {
                    firsts.TryAdd(inMove, look);
                }
            }

            _lookStates[fsm] = firsts;
        }

        return firsts.TryGetValue(state, out var first) ? first : null;
    }

    /// <summary>
    /// Notes the number of a move that this game heard of, from a look or from a step that another game leads, so that
    /// a look of its own, as it becomes the scene host, is numbered after it. A move of the scene host before, whose
    /// steps its player's game sends on to the new one, was otherwise taken for the new one's move of the same number,
    /// steps and all (review of facfc46, with three players or more): one numbered after it goes, and so do they.
    /// </summary>
    private void NoteSilkLook(ulong number) {
        if (number > _silkLooks) {
            _silkLooks = number;
        }
    }

    /// <summary>
    /// Has a state machine of a creature in the scene host's game, as it goes into a state that looks at the silk of
    /// another player whom it goes after, follow the move from there, which the game of that player leads, under the
    /// next number of this game's (see CoopSave.OwnLedMoves), with what it does to that player kept off this game's
    /// (see KeepOut). The number, which the step into the look tells every other game, or 0 for a look at this game's
    /// player, or at no one, which this game takes on by itself.
    /// </summary>
    private ulong StartsOthersSilkLook(Fsm fsm, FsmState toState, GameObject creature) {
        if (!LooksAtSilk(toState) || _ledMoves.ContainsKey(fsm) ||
            ThrownThings.CodeOf?.Invoke(GamePatcher.TargetOf(creature)) is not { } leader ||
            leader is ThrownThings.NoPlayer or ThrownThings.ThrowersPlayer) {
            return 0;
        }

        var number = ++_silkLooks;
        FollowLedMove(fsm, MoveOf(fsm, toState), null, leader, number);
        MeetLedMove(fsm, leader, number);
        if (!_keptOut.ContainsKey(fsm)) {
            KeepOut(fsm, toState);
        }

        Logger.Info(
            $"[Own creatures] '{OwnCreatures.PlaceOf(creature)}' looks at the silk of the player {leader} in " +
            $"'{toState.Name}' of '{fsm.Name}', move {number}, which goes on as their game leads it"
        );
        return number;
    }

    /// <summary>
    /// Has a state machine of a creature, in a game that follows the scene host's, as it goes into a state that looks
    /// at the silk of the player that it goes after with a step of the scene host's, lead the move from there where
    /// that player is this game's, telling every step of it (see CoopSave.OwnLedMoves), or else follow it, taking the
    /// steps of the scene host's in it and none of its own, with what it does to the player kept off this game's (see
    /// KeepOut): a look at the scene host's player, or at no one, which the step tells by no number, is the scene
    /// host's game's to lead, and so is one that a member who has just come in is put into, at the look or in the
    /// move after it, whose number they do not have.
    /// </summary>
    private void FollowsSilkLookAsTold(PlayMakerFSM machine, FsmState toState, CoopSaveUpdate step, ushort host) {
        var fsm = machine.Fsm;
        if (_ledMoves.ContainsKey(fsm) || _ownLeaps.ContainsKey(fsm) || LookStateOf(fsm, toState) is not { } look) {
            return;
        }

        var creature = machine.gameObject;
        var place = OwnCreatures.PlaceOf(creature);
        var states = MoveOf(fsm, look);

        // The number comes with the step into the look itself. One into the move after it, which a member who has just
        // come in is put into, has none, and the move is followed as the scene host's game tells it all the same:
        // otherwise what it does to the player, like the flash and the drain of the spin that another player's game
        // led, went on that member's own (review of facfc46)
        var number = toState == look && step.PartCount is not (OwnStruckPart or OwnToldPart) ? step.Sequence : 0UL;
        NoteSilkLook(number);

        // Whom the scene host's game looked at, as its step says, rather than whom the creature goes after here: a
        // song or another move here may point it at someone else, and a game that went on with a move of its own
        // player's as led elsewhere waited for itself, as the scene host's game waited for it (review of facfc46)
        var looked = ThrownThings.FigureOf?.Invoke(step.Part, host);
        var atLocalPlayer = looked != null && HeroController.SilentInstance is { } hero && looked == hero.gameObject;
        if (number > 0 && atLocalPlayer) {
            _ownLeaps[fsm] = new OwnLeap(place, false, "look at the silk of the local player") {
                Catch = states,
                Led = true,
                Move = number
            };
            Logger.Info(
                $"[Own creatures] '{place}' looks at the silk of the local player in '{look.Name}' of " +
                $"'{fsm.Name}', move {number}, which it plays here by itself to its end, leading it"
            );
            return;
        }

        var leader = number > 0 ? step.Part : host;
        FollowLedMove(fsm, states, null, leader, number);
        if (looked != null && !atLocalPlayer && !_keptOut.ContainsKey(fsm)) {
            KeepOut(fsm, look);
        }

        Logger.Info(
            $"[Own creatures] '{place}' looks at the silk of the player {leader} in '{look.Name}' of '{fsm.Name}'" +
            (toState != look
                ? $", here put into '{toState.Name}' of the move,"
                : number > 0 ? $", move {number}," : "") +
            " which goes on as the scene host's game tells"
        );
    }
}
