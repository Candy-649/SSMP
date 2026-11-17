using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Entity;
using SSMP.Game.Client.Entity.Action;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The parts of the room that a creature that every game runs by itself picks by where a player is and tells, like the
/// spike floor that the boss at the end of the second act lifts, the one nearest the player it goes after. Each game
/// picked by where it has that player, a little behind where they are in another game, and would lift another floor
/// where the player stood between two. What looks at a player is the scene host's move (USER 11-16 "看人的就房主传过来
/// 招式"; USER 11-17 "我现在很怀疑你丝线和手之类的怎么写的（"): the scene host's game picks the part and tells it, and
/// tells the others which part it told, with how many things the creature had told before, so that the part rolls
/// under the same name there (see SharedDice.NextTellName). A game that follows it goes into the state that picks as
/// the scene host's step comes, right before which part comes (see CoopSave.OnOwnCreatureStep), tells none of the
/// parts it picks so itself, and tells the one that the scene host's game picked as it is told (review: it went into
/// the second pick of a move by itself, before or after the word came, and what it told until the word came was
/// named by one less).
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What an update of a creature that every game runs by itself is when it tells which part of the room the creature
    /// told in the scene host's game, which it picked by where a player is (see <see cref="TellOwnPick"/>).
    /// </summary>
    private const ushort OwnPickPart = 7;

    /// <summary>
    /// The fields of each kind of action through which it looks at an object or keeps one (see
    /// <see cref="PickedByPlayer"/>).
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> PickFields = new();

    /// <summary>
    /// The variable through which each action of a creature that every game runs by itself, or of a part of it, that
    /// this game looked at tells a part of the room which it picked by where a player is, or null for one that tells
    /// no such part (see <see cref="PickOf"/>).
    /// </summary>
    private readonly Dictionary<FsmStateAction, string?> _picks = new();

    /// <summary>
    /// How many things the creature had told before the action that tells a part that it picked, in the scene host's
    /// game, from just before the action tells it to just after (see <see cref="OwnPickOf"/>).
    /// </summary>
    private int _pickTells;

    /// <summary>
    /// Whether an action of a creature that every game runs by itself, or of a part of it, tells a part of the room
    /// that it picked by where a player is (see ToldParts.OwnPick): true in a game that does not follow the scene
    /// host's, which tells it, false in one that does, which tells the part that the scene host's game picked instead,
    /// and null for any other action.
    /// </summary>
    private bool? OwnPickOf(FsmStateAction action) {
        if (OwnCreatures.Count == 0 || action.Fsm is not { } fsm || fsm.GameObject is not { } teller ||
            teller == null || !OwnCreatures.RollsUnderPlace(teller) || PickOf(action) == null) {
            return null;
        }

        if (OwnCreatures.FollowsSceneHost(teller)) {
            Logger.Info(
                $"[Own creatures] '{OwnCreatures.PlaceOf(teller)}' would tell a part that it picked by where a " +
                $"player is from '{action.State.Name}' of '{fsm.Name}' here, and tells the one that the scene host's " +
                "game picks"
            );
            return false;
        }

        SharedDice.RollByName(teller, OwnCreatures.RollName(teller));
        _pickTells = SharedDice.TellsOf(teller);
        return true;
    }

    /// <summary>
    /// Tells the members in the room which part of the room a creature that every game runs by itself told just now in
    /// the scene host's game, which it picked by where a player is, and how many things it had told before (see
    /// <see cref="OwnPickOf"/>), for theirs to tell the same part (see <see cref="TakeOwnPick"/>).
    /// </summary>
    private void TellOwnPick(FsmStateAction action) {
        var fsm = action.Fsm;
        var teller = fsm.GameObject;
        var variable = PickOf(action)!;
        if (fsm.Variables.GetFsmGameObject(variable).Value is not { } told || told == null) {
            return;
        }

        var place = OwnCreatures.PlaceOf(teller);
        var path = ScenePath.Get(told.transform);
        Logger.Info(
            $"[Own creatures] '{place}' told '{path}' from '{action.State.Name}' of '{fsm.Name}', a part that it " +
            "picked by where a player is, as the members in the room are told"
        );
        SendToRoomMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.OwnCreature,
            Scene = teller.scene.name,
            ObjectPath = place,
            FsmName = fsm.Name,
            StateName = action.State.Name,
            PartCount = OwnPickPart,
            Amounts = [_pickTells],
            Names = [path, variable],
            Values = [Array.IndexOf(action.State.Actions, action)]
        });
    }

    /// <summary>
    /// Has a creature that every game runs by itself, or a part of it, tell the part of the room that theirs picked by
    /// where a player is and told in the scene host's game (see <see cref="TellOwnPick"/>), as the action that told it
    /// there does, after as many things told as there, so that the part rolls under the same name.
    /// </summary>
    private void TakeOwnPick(ClientPlayerData player, GameObject teller, CoopSaveUpdate update) {
        var fsm = PlayMakerFSM.FindFsmOnGameObject(teller, update.FsmName).Fsm;
        var action = fsm.GetState(update.StateName).Actions[(int) update.Values[0]];
        var (eventTarget, eventName, _) = TellOf(action);
        if (ScenePath.Find(update.Names[0], update.Scene) is not { } told) {
            Logger.Info(
                $"[Own creatures] '{update.ObjectPath}' told '{update.Names[0]}' in {player.Username}'s game, which " +
                "is not here"
            );
            return;
        }

        fsm.Variables.GetFsmGameObject(update.Names[1]).Value = told;
        SharedDice.RollByName(teller, OwnCreatures.RollName(teller));
        SharedDice.SetTells(teller, update.Amounts[0]);
        Logger.Info(
            $"[Own creatures] '{update.ObjectPath}' tells '{update.Names[0]}' from '{update.StateName}' of " +
            $"'{fsm.Name}' here, the part that it picked by where a player is in {player.Username}'s game"
        );
        fsm.Event(eventTarget, eventName);
    }

    /// <summary>
    /// The variable through which an action tells a part of the room that its creature picked by where a player is
    /// (see <see cref="PickedVariable"/>, <see cref="PickedByPlayer"/>), or null for an action that tells no such part,
    /// found once for each action.
    /// </summary>
    private string? PickOf(FsmStateAction action) {
        if (!_picks.TryGetValue(action, out var variable)) {
            variable = PickedVariable(action) is { } held && PickedByPlayer(action, held) ? held : null;
            _picks[action] = variable;
        }

        return variable;
    }

    /// <summary>
    /// Whether a state of a creature that every game runs by itself, or of a part of it, tells a part of the room which
    /// it picked by where a player is (see <see cref="PickOf"/>).
    /// </summary>
    private bool PicksAPart(FsmState state) {
        foreach (var action in state.Actions) {
            if (PickOf(action) != null) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Where an action that tells something sends it, what it sends, and whether it sends it once and at once, or
    /// nothing for any other action.
    /// </summary>
    private static (FsmEventTarget? Target, string? Event, bool Once) TellOf(FsmStateAction action) {
        return action switch {
            SendEventByName send => (send.eventTarget, send.sendEvent?.Value,
                !send.everyFrame && (send.delay?.Value ?? 0f) < 0.001f),
            SendEventByNameV2 send => (send.eventTarget, send.sendEvent?.Value,
                !send.everyFrame && (send.delay?.Value ?? 0f) < 0.001f),
            _ => (null, null, false)
        };
    }

    /// <summary>
    /// The variable that holds the object which an action tells something once and at once, or null for an action
    /// that tells nothing so.
    /// </summary>
    private static string? PickedVariable(FsmStateAction action) {
        return TellOf(action) is (
                   {
                       target: FsmEventTarget.EventTarget.GameObject or FsmEventTarget.EventTarget.GameObjectFSM,
                       gameObject: {
                           OwnerOption: OwnerDefaultOption.SpecifyGameObject,
                           GameObject: { UseVariable: true, Name.Length: > 0 } held
                       }
                   },
                   { Length: > 0 }, true)
            ? held.Name
            : null;
    }

    /// <summary>
    /// Whether an action that comes before one that tells something, in its state, puts what it tells into the
    /// variable that it tells through by looking at a player: an action that looks at the player that the creature
    /// goes after, where it looks at a player (see ActionRegistry.IsRetargeted), through a variable named for them (see
    /// GamePatcher.IsTargetVariableName), like the closest of a kind of object to them. It goes by how the state is
    /// made, the same in every game, rather than by what the variable holds as it is asked, which may be no one yet in
    /// one game and the player in another (review).
    /// </summary>
    private static bool PickedByPlayer(FsmStateAction tell, string variable) {
        foreach (var pick in tell.State.Actions) {
            if (pick == tell) {
                return false;
            }

            if (!ActionRegistry.IsRetargeted(pick.GetType().Name)) {
                continue;
            }

            var keeps = false;
            var looks = false;
            foreach (var field in FieldsOf(pick.GetType())) {
                var held = field.GetValue(pick) switch {
                    FsmGameObject gameObject => gameObject,
                    FsmOwnerDefault { OwnerOption: OwnerDefaultOption.SpecifyGameObject } owner => owner.GameObject,
                    _ => null
                };
                if (held is not { UseVariable: true }) {
                    continue;
                }

                if (field.Name.StartsWith("store", StringComparison.OrdinalIgnoreCase)) {
                    keeps |= held.Name == variable;
                } else {
                    looks |= GamePatcher.IsTargetVariableName(held.Name);
                }
            }

            if (keeps && looks) {
                return true;
            }
        }

        return false;

        static FieldInfo[] FieldsOf(Type type) {
            if (!PickFields.TryGetValue(type, out var fields)) {
                fields = Array.FindAll(
                    type.GetFields(BindingFlags.Instance | BindingFlags.Public),
                    field => field.FieldType == typeof(FsmGameObject) || field.FieldType == typeof(FsmOwnerDefault)
                );
                PickFields[type] = fields;
            }

            return fields;
        }
    }
}
