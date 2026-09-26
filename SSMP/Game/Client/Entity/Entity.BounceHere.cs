using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity.Action;
using SSMP.Game.Client.Save;
using SSMP.Networking.Packet.Data;
using UnityEngine;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// Strikes on the copy of something juggled, played at once in the game of the player who struck it.
///
/// A juggled flea flies up from where it is struck to a spot and a height that its FSM rolls there and then, and the
/// eased movements of that FSM carry it rather than a body of its own. The copy runs none of its FSMs, so it could do
/// nothing with a strike (see <see cref="PlayStrikeHere"/> for what a copy can do by itself), and the strike went to
/// the scene host and back while the flea fell on through the player's nail. Now the game of the player who struck it
/// runs the copy's own FSM, from the state the scene host says it is in, through the strike and on, and sends the
/// scene host where the copy was and the dice that the FSM rolled on the way (see <see cref="SharedDice"/>). The scene
/// host puts its flea there and takes the strike with the same dice, so that it flies the same way, carried on by the
/// time the strike took to come. Meanwhile the game of the player who struck it waits for the scene host to say that it
/// has the strike, the way it waits for a knockback (see <see cref="BeginAnticipation"/>), and holds on to what the
/// scene host sends of the flea, which is from before. If the scene host's flea went another way - it had landed there
/// before the strike came - the copy is given all of that when it follows the scene host again.
///
/// Once the scene host has the strike and went the same way, the two are in step, and the copy runs on by itself until
/// the scene host's game does something with the FSM that the copy did not: the flea is struck there too, or reaches
/// the top of its flight a moment sooner. Only then does the copy follow the scene host again. Handed back at once, it
/// lost what its FSM was still in the middle of, which nothing sent again: the flea only becomes something to strike a
/// moment after it flies off, and struck again by the player who had just struck it, it passed through their nail for
/// the rest of its flight.
/// </summary>
internal partial class Entity {
    /// <summary>
    /// The kinds of entity whose copy runs its own FSM for a strike of the local player: what is juggled in a game of
    /// the festival.
    /// </summary>
    private static readonly HashSet<EntityType> BouncedHereTypes = [
        EntityType.BellfleaJuggler, EntityType.BellfleaJugglerGiant, EntityType.JuggleGameGuest
    ];

    /// <summary>
    /// The most times that an FSM waiting for the next frame is stepped on at once (see <see cref="Settle"/>).
    /// </summary>
    private const int MaxSettleSteps = 8;

    /// <summary>
    /// How far an eased movement has got, in seconds, which the scene host moves on to carry a strike on.
    /// </summary>
    private static readonly FieldInfo? EaseRunningTimeField = typeof(EaseFsmAction).GetField(
        "runningTime", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
    );

    /// <summary>
    /// The frame that an action waiting for the next frame started on, which is moved back to step it on at once.
    /// </summary>
    private static readonly FieldInfo? NextFrameEnterField = typeof(NextFrameEvent).GetField(
        "enterFrameCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
    );

    /// <summary>
    /// The FSM of the copy that runs by itself for a strike of the local player, or null while none does.
    /// </summary>
    private PlayMakerFSM? _runHere;

    /// <summary>
    /// Whether the scene host has taken the strike that <see cref="_runHere"/> runs for and went the same way, after
    /// which the copy runs on only until the scene host's game does something with the FSM (see
    /// <see cref="HearFromSceneHost"/>).
    /// </summary>
    private bool _runHereInStep;

    /// <summary>
    /// Whether the copy's FSM runs by itself for a strike that the scene host has yet to take. What the scene host
    /// sends meanwhile of what the FSM does and what the entity plays is from before the strike, and is held.
    /// </summary>
    private bool WaitsForBounce => _runHere != null && !_runHereInStep;

    /// <summary>
    /// The replays of the FSM that runs here that the scene host sent while the copy waited for it to take a strike,
    /// in the order they came, copied out of the packets they came in.
    /// </summary>
    private readonly List<EntityNetworkData> _heldData = [];

    /// <summary>
    /// The last animation that the scene host sent while the copy waited for it to take a strike, or null for none.
    /// </summary>
    private (byte Id, tk2dSpriteAnimationClip.WrapMode WrapMode)? _heldAnimation;

    /// <summary>
    /// The actions of <see cref="_runHere"/> that tell the room something, switched off while the copy runs by itself.
    /// The scene host's game tells the room when it takes the strike, and the room here is told the same from there, so
    /// a point that the copy told of too would count twice.
    /// </summary>
    private readonly List<FsmStateAction> _mutedHere = [];

    /// <summary>
    /// Plays at once what the local player's strike on a part of the copy makes the FSM at the given index do, for
    /// something juggled: the copy's own FSM takes the event in the state the scene host says it is in - or where it is
    /// if it runs by itself already - and runs on by itself until the scene host has taken the strike too.
    /// </summary>
    /// <param name="fsmIndex">The index of the FSM.</param>
    /// <param name="eventName">The event that the strike told the FSM.</param>
    /// <returns>Where the copy was struck and the dice, or null if the strike was not played here.</returns>
    public BounceStart? PlayBounceHere(byte fsmIndex, string eventName) {
        if (!_isControlled || !BouncedHereTypes.Contains(Type) || fsmIndex >= _fsms.Client.Count ||
            _fsms.Client[fsmIndex] is not { } copyFsm || copyFsm == null || Object.Client == null ||
            _runHere != null && _runHere != copyFsm) {
            return null;
        }

        var fsm = copyFsm.Fsm;
        var fromName = _runHere != null ? fsm.ActiveStateName : EntityFsmActions.HostStateOf(fsm);
        if (fromName == null || fsm.GetState(fromName) is not { } from ||
            EntityFsmActions.FindTransition(fsm, from, eventName) == null) {
            return null;
        }

        if (_runHere == null) {
            StartRunningHere(copyFsm, from);
        }

        Vector2 position = Object.Client.transform.position;
        var dice = SharedDice.Record(() => {
            fsm.Event(eventName);
            Settle(fsm);
        }, fsm);

        _runHereInStep = false;
        return new BounceStart(fsm.ActiveStateName, position, dice, BeginAnticipation());
    }

    /// <summary>
    /// Takes a strike that the partner's game has already played on its copy (see <see cref="PlayBounceHere"/>), on
    /// the scene host. If the event leads anywhere from where the FSM is, the entity is put where the copy was struck
    /// and told the event with the dice that the partner's game rolled, which does here all that it did there and all
    /// that only this game does, like counting the tink. If that takes it to the state it took the copy to, it is
    /// carried on for the time the strike took to arrive, so that what this game sends of it shows it where the
    /// partner already sees it. Either way the partner hears back that the strike was taken in.
    /// </summary>
    /// <param name="fsmIndex">The index of the FSM.</param>
    /// <param name="eventName">The event that the strike told the FSM.</param>
    /// <param name="start">Where the copy was struck and the dice.</param>
    /// <param name="elapsed">How long ago the partner's copy was struck, in seconds.</param>
    /// <returns>Whether the entity went the way the partner's copy went.</returns>
    public bool TakeBounce(byte fsmIndex, string eventName, BounceStart start, float elapsed) {
        if (_isControlled || fsmIndex >= _fsms.Host.Count || _fsms.Host[fsmIndex] is not { } hostFsm ||
            hostFsm == null || Object.Host == null) {
            return false;
        }

        NoteAnticipation(start.Anticipation);

        var fsm = hostFsm.Fsm;
        if (fsm.ActiveState is not { } state || EntityFsmActions.FindTransition(fsm, state, eventName) == null) {
            return false;
        }

        var transform = Object.Host.transform;
        transform.position = new Vector3(start.Position.x, start.Position.y, transform.position.z);
        SharedDice.Throw(start.Dice, () => {
            fsm.Event(eventName);
            Settle(fsm);
        }, fsm);

        if (fsm.ActiveState is not { } reached || reached.Name != start.State) {
            return false;
        }

        CarryEasesOn(reached, elapsed);
        return true;
    }

    /// <summary>
    /// Runs the FSM of the copy that runs by itself on by a frame, unless waiting for the scene host to take the strike
    /// gave up (see <see cref="DecideRunHere"/>).
    /// </summary>
    private void UpdateRunHere() {
        DecideRunHere();
        if (_runHere != null) {
            _runHere.Fsm.Update();
        }
    }

    /// <summary>
    /// Decides how the copy goes on once the scene host has said that it took the strike, or waiting for that gave up
    /// (see <see cref="IsAnticipating"/>). If the scene host's FSM went the same way, the copy runs on in step with it
    /// and what it held is dropped: its own FSM did all of it. If not, the copy follows the scene host again at once,
    /// with all that it held.
    /// </summary>
    private void DecideRunHere() {
        if (!WaitsForBounce || IsAnticipating()) {
            return;
        }

        var fsm = _runHere!.Fsm;
        if (EntityFsmActions.HostStateOf(fsm) == fsm.ActiveStateName) {
            _runHereInStep = true;
            _heldData.Clear();
            _heldAnimation = null;
            return;
        }

        var heldData = new List<EntityNetworkData>(_heldData);
        var heldAnimation = _heldAnimation;
        StopRunningHere();

        UpdateData(heldData, false);
        if (heldAnimation is { } animation) {
            UpdateAnimation(animation.Id, animation.WrapMode, false);
        }
    }

    /// <summary>
    /// Holds a replay of the FSM that runs here, which the scene host sent while the copy waited for it to take a
    /// strike (see <see cref="DecideRunHere"/>).
    /// </summary>
    /// <param name="data">The replay, whose packet is only lent for as long as it is being read.</param>
    private void HoldForBounce(EntityNetworkData data) {
        _heldData.Add(new EntityNetworkData {
            Type = data.Type,
            SenderId = data.SenderId,
            Packet = new global::SSMP.Networking.Packet.Packet(data.Packet.ToArray())
        });
    }

    /// <summary>
    /// Takes it that the scene host's game did something with an FSM of the copy: it sent a replay of it, or it went to
    /// another state. While the copy runs that FSM by itself in step with the scene host, that is something the copy
    /// did not do, and the copy follows the scene host again from here.
    /// </summary>
    /// <param name="fsm">The FSM of the copy.</param>
    private void HearFromSceneHost(PlayMakerFSM fsm) {
        if (_runHereInStep && fsm == _runHere) {
            StopRunningHere();
        }
    }

    /// <summary>
    /// Starts the copy's FSM running by itself, in the state it is struck in.
    /// </summary>
    /// <param name="copyFsm">The FSM of the copy.</param>
    /// <param name="from">The state it is struck in.</param>
    private void StartRunningHere(PlayMakerFSM copyFsm, FsmState from) {
        var fsm = copyFsm.Fsm;

        // What the copy replayed of the scene host's state is left: its own FSM moves it from here
        EntityFsmActions.LeaveStatesOf([copyFsm]);
        CreateOwnObjects(fsm);

        foreach (var state in fsm.States) {
            foreach (var action in state.Actions) {
                if (action is SendEventToRegister { Enabled: true }) {
                    action.Enabled = false;
                    _mutedHere.Add(action);
                }
            }
        }

        // Put in the state it is struck in without doing that state again: done again, a flea falling from the top of
        // its flight was put back at the top. A state with nothing to do goes on at once, so for the moment of going in
        // it has only something that does nothing.
        var actions = from.Actions;
        from.Actions = [new HoldHere()];
        try {
            fsm.SetState(from.Name);
            fsm.Start();
        } finally {
            from.Actions = actions;
        }

        _runHere = copyFsm;
    }

    /// <summary>
    /// Stops the copy's FSM running by itself, after which the copy follows the scene host again from where it is.
    /// </summary>
    private void StopRunningHere() {
        if (_runHere is not { } copyFsm) {
            return;
        }

        _runHere = null;
        _runHereInStep = false;
        _heldData.Clear();
        _heldAnimation = null;
        foreach (var action in _mutedHere) {
            action.Enabled = true;
        }

        _mutedHere.Clear();
        if (copyFsm != null) {
            copyFsm.Fsm.Stop();
        }
    }

    /// <summary>
    /// Creates what the first state of the copy's FSM creates for the FSM to use, which the copy never had, since its
    /// FSM never started: the empty object that a flea judges where it can fly by. Without it, a flea judged every spot
    /// too far and rolled for ever.
    /// </summary>
    /// <param name="fsm">The FSM of the copy.</param>
    private static void CreateOwnObjects(HutongGames.PlayMaker.Fsm fsm) {
        if (fsm.GetState(fsm.StartState) is not { } first) {
            return;
        }

        foreach (var action in first.Actions) {
            if (action is CreateEmptyObject { storeObject.Value: null } create) {
                // Marked done already, so that it does not tell a state that is not running that it is
                create.Init(first);
                create.Finished = true;
                create.OnEnter();
            }
        }
    }

    /// <summary>
    /// Steps an FSM on at once for as long as it waits for the next frame, up to <see cref="MaxSettleSteps"/> times. A
    /// flea that rolls a spot too near or too far rolls again at once, but after a number of those it waits for the
    /// next frame first. Stepped on here instead, it rolls within the strike, with the dice that go with it, rather
    /// than on a frame of its own, for which each game would roll its own.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    private static void Settle(HutongGames.PlayMaker.Fsm fsm) {
        for (var step = 0; step < MaxSettleSteps && NextFrameEnterField != null; step++) {
            if (fsm.ActiveState is not { } state || Array.Find(
                    state.Actions, action => action is NextFrameEvent { Entered: true, Finished: false }
                ) is not { } next) {
                return;
            }

            NextFrameEnterField.SetValue(next, -1);
            fsm.Update();
        }
    }

    /// <summary>
    /// Carries the eased movements of a state on by the given time, as if the state had been entered that long ago.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="elapsed">The time, in seconds.</param>
    private static void CarryEasesOn(FsmState state, float elapsed) {
        if (EaseRunningTimeField == null) {
            return;
        }

        foreach (var action in state.Actions) {
            if (action is EaseFsmAction { Enabled: true, Finished: false } ease) {
                EaseRunningTimeField.SetValue(ease, (float) EaseRunningTimeField.GetValue(ease) + elapsed);
            }
        }
    }

    /// <summary>
    /// An action that does nothing and is never done, which holds an FSM in a state without doing what the state does.
    /// </summary>
    private sealed class HoldHere : FsmStateAction {
    }
}
