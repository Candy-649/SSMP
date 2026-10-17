using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity.Action;
using UnityEngine;

namespace SSMP.Game.Client;

/// <summary>
/// Alert ranges that are about the player of this game alone. A range with nothing that can be fought above it is
/// answered with "is either player standing there" (see AnswerWhetherAPlayerIsInAlertRange), which suits a piece of the
/// world that anybody may set off. But a few of them belong to a state machine that, as soon as it has asked the range,
/// does something to the player of its own game, through the game's global "Hero" variable: a shower splashes and
/// cleans the player standing under it and leaves them dripping, a cage trap puts the player it caught inside it.
/// Answered for either player, the shower in one game splashed and cleaned that game's player wherever they stood while
/// the other player showered, and left them dripping once the other one walked out (USER 10-17, the showers of the
/// citadel's spa, "水滴在我身上，过了很久才没掉"), and a cage that the other player sprang from its plate put that
/// game's player inside it from anywhere in the room. Such a range answers as in a game with one player, with the
/// game's own answer: it lies on the layer of the ranges that look for the player, which meets the player of its own
/// game and never the partner's figure. So it does also while a hit or touch of the partner is replayed; the partner's
/// game does the same for them.
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// How long a range whose state machines have not all started yet is answered for either player before it is
    /// looked at again, so that a state machine that never starts, being switched off, is not looked through every
    /// time the range is asked.
    /// </summary>
    private const float LocalPlayerRangeRetryTime = 1f;

    /// <summary>
    /// The kinds of action with which a state machine moves the player or puts something on them, by the objects they
    /// name, with the fields that name objects. Other uses of the player, like reading where they are, are left out:
    /// silk flies that scatter, platforms that crumble and many more ask about either player as they should.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> PlayerPlacingActionFields = new();

    /// <summary>
    /// Whether an alert range is about the player of this game alone: a range with nothing that can be fought above
    /// it, asked by a state machine that does something to the player by the global "Hero" variable as soon as it has
    /// asked it (see <see cref="PlacesThePlayer(PlayMakerFSM, string)"/>). It is worked out once the state machines
    /// there have started, and again when the range is moved.
    /// </summary>
    private static bool IsAboutTheLocalPlayer(AlertRange? alertRange) {
        if (alertRange == null || BelongsToAnEnemy(alertRange) ||
            !EnemyAlertRanges.TryGetValue(alertRange, out var note)) {
            return false;
        }

        if (note.AboutTheLocalPlayer is { } known) {
            return known;
        }

        if (Time.unscaledTime < note.LocalPlayerRetryAt) {
            return false;
        }

        var owner = alertRange.transform.parent;
        if (owner == null) {
            note.AboutTheLocalPlayer = false;
            return false;
        }

        // CheckAlertRangeByName looks for the range among the children of the state machine's own object
        var unknown = false;
        foreach (var fsm in owner.GetComponents<PlayMakerFSM>()) {
            if (fsm == null) {
                continue;
            }

            // The actions of a state machine that has not started yet are not loaded, and loading them early would
            // break it; it is looked at again a moment later
            if (fsm.Fsm is not { Started: true }) {
                unknown = true;
                continue;
            }

            if (!PlacesThePlayer(fsm, alertRange.name)) {
                continue;
            }

            note.AboutTheLocalPlayer = true;
            Logging.Logger.Info(
                $"The range '{alertRange.name}' under '{owner.name}' answers for this game's player alone: " +
                $"'{fsm.FsmName}' moves them or puts something on them once it has asked it"
            );
            return true;
        }

        if (unknown) {
            note.LocalPlayerRetryAt = Time.unscaledTime + LocalPlayerRangeRetryTime;
        } else {
            note.AboutTheLocalPlayer = false;
        }

        return false;
    }

    /// <summary>
    /// Whether a state machine moves the player of its game or puts something on them, by the global "Hero" variable,
    /// as soon as it has asked a range: the state that asks it does so itself, or the event that it sends as the player
    /// comes into the range or leaves it takes the state machine straight into a state that does. What a state machine
    /// does to the player at some other time, long after it asked, has nothing to do with whose answer it was given:
    /// a creature that stops singing once nobody is near any more hears of either player (USER 09-14: one sings on
    /// until it is out of the reach of every player). One that keeps a "Hero" of its own found somebody itself, like
    /// the spa's pool by whoever stepped in.
    /// </summary>
    /// <param name="fsm">The state machine.</param>
    /// <param name="rangeName">The name of the range, by which the state machine asks it.</param>
    private static bool PlacesThePlayer(PlayMakerFSM fsm, string rangeName) {
        if (fsm.FsmVariables.FindFsmGameObject("Hero") != null) {
            return false;
        }

        foreach (var state in fsm.FsmStates) {
            foreach (var action in state.Actions) {
                if (action is not CheckAlertRangeByName { Enabled: true } asks || asks.alertRangeName != rangeName) {
                    continue;
                }

                if (PlacesThePlayer(state)) {
                    return true;
                }

                // An event sent elsewhere takes this state machine nowhere
                if (asks.eventTarget != null && asks.eventTarget.target != FsmEventTarget.EventTarget.Self) {
                    continue;
                }

                foreach (var sent in new[] { asks.sendEvent, asks.outOfRangeEvent }) {
                    if (sent is { IsNone: false } && !string.IsNullOrEmpty(sent.Value) &&
                        EntityFsmActions.FindTransition(fsm.Fsm, state, sent.Value) is { } next &&
                        PlacesThePlayer(next)) {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a state moves the player of its game or puts something on them, by the global "Hero" variable.
    /// </summary>
    private static bool PlacesThePlayer(FsmState state) {
        foreach (var action in state.Actions) {
            if (action is not { Enabled: true } || GetPlayerPlacingFields(action.GetType()) is not { } fields) {
                continue;
            }

            foreach (var field in fields) {
                switch (field.GetValue(action)) {
                    case FsmGameObject { UseVariable: true, Name: "Hero" }:
                    case FsmOwnerDefault {
                        OwnerOption: OwnerDefaultOption.SpecifyGameObject,
                        GameObject: { UseVariable: true, Name: "Hero" }
                    }:
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The fields that name objects of an action that moves things or puts them on something, or null for any other
    /// action.
    /// </summary>
    private static FieldInfo[]? GetPlayerPlacingFields(Type type) {
        if (type != typeof(SetPositionToObject) && type != typeof(SetPositionToObject2D) &&
            type != typeof(SetPosition) && type != typeof(SetPosition2d)) {
            return null;
        }

        if (!PlayerPlacingActionFields.TryGetValue(type, out var fields)) {
            fields = Array.FindAll(
                type.GetFields(BindingFlags.Public | BindingFlags.Instance),
                field => field.FieldType == typeof(FsmGameObject) || field.FieldType == typeof(FsmOwnerDefault)
            );
            PlayerPlacingActionFields[type] = fields;
        }

        return fields;
    }
}
