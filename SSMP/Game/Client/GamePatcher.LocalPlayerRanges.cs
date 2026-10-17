using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace SSMP.Game.Client;

/// <summary>
/// Alert ranges that are about the player of this game alone. A range with nothing that can be fought above it is
/// answered with "is either player standing there" (see AnswerWhetherAPlayerIsInAlertRange), which suits a piece of the
/// world that anybody may set off. But a few of them belong to a state machine that does what it does to the player
/// of its own game, through the game's global "Hero" variable: a shower splashes and cleans the player standing under
/// it and leaves them dripping, a cage trap puts the player it caught inside it. Answered for either player, the shower
/// in one game splashed and cleaned that game's player wherever they stood while the other player showered, and left
/// them dripping once the other one walked out (USER 10-17, the showers of the citadel's spa, "水滴在我身上，过了很久才
/// 没掉"), and a cage would put that game's player inside it for the other one standing there. Such a range answers
/// for the local player alone, as in a game with one player, also while a hit or touch of the partner is replayed;
/// the partner's game does the same for them.
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// The kinds of action with which a state machine moves the player or puts something on them, by the objects they
    /// name, with the fields that name objects. Other uses of the player, like reading where they are, are left out:
    /// silk flies that scatter, platforms that crumble and many more ask about either player as they should.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> PlayerPlacingActionFields = new();

    /// <summary>
    /// Whether an alert range is about the player of this game alone: a range with nothing that can be fought above
    /// it, under a state machine that moves the player or puts something on them by the global "Hero" variable. It
    /// is worked out once the state machines there have started, and again when the range is moved.
    /// </summary>
    private static bool IsAboutTheLocalPlayer(AlertRange? alertRange) {
        if (alertRange == null || BelongsToAnEnemy(alertRange) ||
            !EnemyAlertRanges.TryGetValue(alertRange, out var note)) {
            return false;
        }

        if (note.AboutTheLocalPlayer is { } known) {
            return known;
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
            // break it; it is looked at again the next time the range is asked
            if (fsm.Fsm is not { Started: true }) {
                unknown = true;
                continue;
            }

            if (!PlacesThePlayer(fsm)) {
                continue;
            }

            note.AboutTheLocalPlayer = true;
            Logging.Logger.Info(
                $"The range '{alertRange.name}' under '{owner.name}' answers for this game's player alone: " +
                $"'{fsm.FsmName}' moves them or puts something on them"
            );
            return true;
        }

        if (!unknown) {
            note.AboutTheLocalPlayer = false;
        }

        return false;
    }

    /// <summary>
    /// Whether a state machine moves the player of its game or puts something on them, by the global "Hero" variable.
    /// One that keeps a "Hero" of its own found somebody itself, like the spa's pool by whoever stepped in.
    /// </summary>
    private static bool PlacesThePlayer(PlayMakerFSM fsm) {
        if (fsm.FsmVariables.FindFsmGameObject("Hero") != null) {
            return false;
        }

        foreach (var state in fsm.FsmStates) {
            foreach (var action in state.Actions) {
                if (action == null || GetPlayerPlacingFields(action.GetType()) is not { } fields) {
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

    /// <summary>
    /// Whether the local player stands inside an alert range and can be seen from it. Nobody while the local player
    /// is between two rooms or down (PlayerTargetRegistry).
    /// </summary>
    private static bool IsLocalPlayerInsideAlertRange(AlertRange alertRange) {
        var hero = HeroController.instance;
        if (hero == null) {
            return false;
        }

        var heroObject = hero.gameObject;
        return PlayerTargetRegistry.GetTrackedPlayers().Contains(heroObject) &&
               IsPlayerInsideAlertRange(alertRange, heroObject) &&
               HasLineOfSightToAlertRangeTarget(alertRange, heroObject);
    }
}
