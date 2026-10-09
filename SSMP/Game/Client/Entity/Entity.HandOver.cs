using System;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Util;
using UnityEngine;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// The local player talking to the copy of a creature through one of its talk parts (see
/// <see cref="Component.PartsComponent"/>), when the talk ends by handing the player over to the creature. A knight
/// asked to spar ends its talk without giving the player back and tells itself to start; it gives the player back once
/// its challenge is over (USER 10-10). Here the copy's own FSMs are switched off, so the knight never started, and the
/// player stood frozen at the end of the talk for good.
///
/// The event goes to the creature in the game that runs it, and what the creature then does to "the player" - turning
/// them, playing their part of the challenge, giving them back - is played here on the local player, who is the one
/// it was talking to. Everywhere else such replays are kept off the local player (EntityFsmActions), because they were
/// about the other game's player.
/// </summary>
internal partial class Entity {
    /// <summary>
    /// How long a creature that a talk handed the local player over to has to give them back, in seconds, before this
    /// game does it itself. The knight's challenge takes about four.
    /// </summary>
    private const float HandOverGiveBackTime = 15f;

    /// <summary>
    /// The creature that a talk handed the local player over to, or null.
    /// </summary>
    private static Entity? _handedOverTo;

    /// <summary>
    /// When the local player was handed over, in unscaled seconds.
    /// </summary>
    private static float _handedOverSince;

    /// <summary>
    /// The character of the copy that the talk which handed the local player over went through, which is done talking
    /// once they are given back, or null.
    /// </summary>
    private static PlayMakerNPC? _handedOverNpc;

    /// <summary>
    /// Whether the hand-over is being watched every frame.
    /// </summary>
    private static bool _handOverWatched;

    /// <summary>
    /// Raised when a talk of the local player with the copy of an entity tells the creature to go on from the talk, with
    /// the index of the FSM that takes it and the event.
    /// </summary>
    public static event Action<Entity, byte, string>? CopyToldByLocalTalk;

    /// <summary>
    /// Whether a part of the copy of a creature is one through which a player talks to it, or lies under one.
    /// </summary>
    /// <param name="part">The part.</param>
    /// <param name="copy">The object of the copy.</param>
    private static bool IsInTalkPart(GameObject part, GameObject copy) {
        for (var current = part.transform; current != null && current != copy.transform; current = current.parent) {
            if (current.GetComponent<NPCControlBase>() != null) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sends the creature in the game that runs it what a talk of the local player told the copy, wherever an FSM of the
    /// copy goes on from it in the state that game says it is in, and hands the player over to the creature.
    /// </summary>
    /// <param name="fsmName">The name of the FSM that the talk told, or null for all of them.</param>
    /// <param name="eventName">The event.</param>
    private void OnCopyToldByTalk(string? fsmName, string eventName) {
        if (!_isControlled || Object.Client == null) {
            return;
        }

        var taken = false;
        for (var fsmIndex = 0; fsmIndex < _fsms.Client.Count; fsmIndex++) {
            var fsm = _fsms.Client[fsmIndex];
            if (fsm == null || fsm.enabled || !string.IsNullOrEmpty(fsmName) && fsm.FsmName != fsmName ||
                StateOfCopy(fsm) is not { } state ||
                Action.EntityFsmActions.FindTransition(fsm.Fsm, state, eventName) == null) {
                continue;
            }

            taken = true;
            CopyToldByLocalTalk?.Invoke(this, (byte) fsmIndex, eventName);
        }

        if (!taken) {
            SSMP.Logging.Logger.Info(
                $"A talk of the local player told the copy of entity {Id} '{eventName}', which no FSM of it takes in " +
                "the state it is in"
            );
        }

        // Whether or not anything took it: the talk has ended the way it ends in the game, and a player it did not give
        // back is given back here in time (WatchHandOver)
        if (HeroController.instance is { controlReqlinquished: true }) {
            HandOverTheLocalPlayer();
        }
    }

    /// <summary>
    /// Hands the local player over to this creature after a talk that did not give them back: what it does to "the
    /// player" from here is played on them, and they are given back here in time if it never does (WatchHandOver).
    /// </summary>
    /// <param name="npc">The character of the copy that the talk went through, which is done talking once the player
    /// is given back, or null for one that switches itself off.</param>
    private void HandOverTheLocalPlayer(PlayMakerNPC? npc = null) {
        _handedOverTo = this;
        _handedOverSince = Time.unscaledTime;
        _handedOverNpc = npc;
        if (!_handOverWatched) {
            _handOverWatched = true;
            MonoBehaviourUtil.Instance.OnUpdateEvent += WatchHandOver;
        }

        SSMP.Logging.Logger.Info($"The talk handed the local player over to the copy of entity {Id}");
    }

    /// <summary>
    /// Whether a replayed action is about the local player because a talk handed them over to its creature, so that it
    /// is played on them rather than kept off them: only what turns them, plays their part of the scene, or takes and
    /// gives back control - a knight's challenge does nothing else to the player it talked to. Whatever else the
    /// creature does to "the player" meanwhile, moving or harming them, was done to the scene host's player, as
    /// everywhere else.
    /// </summary>
    /// <param name="action">The replayed action.</param>
    internal static bool IsHandedOverTo(FsmStateAction action) {
        var fsm = action.Fsm;
        if (_handedOverTo is not { } creature || fsm == null || fsm.GameObject == null ||
            FindByCopyPart(fsm.GameObject) != creature) {
            return false;
        }

        var name = action.GetType().Name;
        return name.StartsWith("Tk2dPlay", StringComparison.Ordinal) ||
               name.StartsWith("FaceObject", StringComparison.Ordinal) ||
               name.StartsWith("SendEventByName", StringComparison.Ordinal) ||
               action is SendMessage { functionCall.FunctionName: { } function } &&
               (function.Contains("Control") || function.StartsWith("Face", StringComparison.Ordinal));
    }

    /// <summary>
    /// Ends a hand-over once the player has control again, and gives it back to them if the creature never did.
    /// </summary>
    private static void WatchHandOver() {
        var hero = HeroController.instance;
        var over = _handedOverTo == null || hero == null || !hero.controlReqlinquished ||
                   _handedOverTo.Object.Client == null;
        if (!over && Time.unscaledTime - _handedOverSince >= HandOverGiveBackTime) {
            SSMP.Logging.Logger.Info(
                $"The copy of entity {_handedOverTo!.Id} did not give the local player back within " +
                $"{HandOverGiveBackTime:0}s of the talk that handed them over, so they are given back here"
            );
            try {
                hero!.RegainControl();
                hero.StartAnimationControl();
            } catch (Exception e) {
                SSMP.Logging.Logger.Warn($"Could not give the local player back after a talk: {e.Message}");
            }

            over = true;
        }

        if (!over) {
            return;
        }

        // Given back, the player is done with the talk too
        if (_handedOverNpc != null) {
            CloseTalk(_handedOverNpc, false);
        }

        _handedOverTo = null;
        _handedOverNpc = null;
        _handOverWatched = false;
        MonoBehaviourUtil.Instance.OnUpdateEvent -= WatchHandOver;
    }
}
