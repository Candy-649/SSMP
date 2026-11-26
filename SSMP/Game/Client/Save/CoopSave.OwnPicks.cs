using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
///
/// So goes a child of an object of the room that a creature picks at random and then tells something, like the wasp
/// that the hive takes out of those that lie in it to send it out, or goes to, like the place of the maze where the
/// ghost cut down comes back (see <see cref="ChildPicks"/>): the scene host's game picks it as the creature goes into
/// the state, the step tells which, and every game that follows picks that one as the step comes, and goes into the
/// state only as it comes.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What an update of a creature that every game runs by itself is when it tells which part of the room the creature
    /// told in the scene host's game, which it picked by where a player is (see <see cref="TellOwnPick"/>).
    /// </summary>
    private const ushort OwnPickPart = 7;

    /// <summary>
    /// How a child that the scene host's game picked is written (see <see cref="WriteChildPicks"/>): none, by its
    /// place, or by its path.
    /// </summary>
    private const byte ChildNone = 0, ChildByPlace = 1, ChildByPath = 2;

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
    /// The children that a state machine of a creature that every game runs by itself, or of a part of it, picks at
    /// random in a state that it goes into, and then tells something or goes to (see <see cref="ChildPicksIn"/>): the
    /// state, and the child that each action that picks one, by its number in the state, picks there, or null where
    /// the object had none. Each game has the children of an object of the room in an order of its own, and others
    /// than another game for a moment, as the creatures that leave it and come back do so at other moments in each,
    /// and picked another from the same roll, which it then told in every game: the giant bone flyer of the second
    /// fight sent its own pick and the scene host's out of its pack in every game, and the hive sent out a wasp of
    /// those that lay in it here and another there, which flew there still lying in it (review of 81641bc and
    /// 11b3429). The scene host's game picks them as the state machine goes into the state, and the step tells them
    /// (see <see cref="PickChildren"/>, <see cref="WriteChildPicks"/>); a game that follows picks them as the step
    /// comes (see <see cref="ReadChildPicks"/>).
    /// </summary>
    private static readonly Dictionary<Fsm, (string State, Dictionary<int, GameObject?> Picks)> ChildPicks = new();

    /// <summary>
    /// The dice with which the scene host's game picks the children that a creature picks at random (see
    /// <see cref="ChildPicks"/>), apart from the game's own, which the dice that every game shares may have set as it
    /// picks them.
    /// </summary>
    private static readonly System.Random ChildDice = new();

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
    /// it picked by where a player is (see <see cref="PickOf"/>), or a child that it picked at random (see
    /// <see cref="ChildPicksIn"/>).
    /// </summary>
    private bool PicksAPart(FsmState state) {
        foreach (var action in state.Actions) {
            if (PickOf(action) != null) {
                return true;
            }
        }

        return ChildPicksIn(state).Any();
    }

    /// <summary>
    /// The numbers in a state of the actions that pick a child of an object at random into a variable through which a
    /// later action of the state tells it something (see <see cref="ChildPicks"/>), at once or a moment later: the step
    /// tells which child it picked, which a tell a moment later tells as much (review of b48cf5a: the roar of the boss
    /// that sends out spears, which sends one out a moment after it picks it, picked its own in a game that follows,
    /// and the next state, which took the scene host's pick, sent the same spear out again as another stayed). So are
    /// the ones whose pick a later action of the state puts the creature at, or moves (see
    /// <see cref="PlacedVariable"/>), like the place of the maze where the ghost cut down comes back, which it takes
    /// out of the places left for the others: each game would have put it at another.
    /// </summary>
    private static IEnumerable<int> ChildPicksIn(FsmState state) {
        var actions = state.Actions;
        for (var i = 0; i < actions.Length; i++) {
            if (actions[i] is not GetRandomChild {
                    Enabled: true, storeResult: { UseVariable: true, Name.Length: > 0 } kept
                }) {
                continue;
            }

            for (var j = i + 1; j < actions.Length; j++) {
                if (actions[j].Enabled &&
                    (PickedVariable(actions[j], true) == kept.Name || PlacedVariable(actions[j]) == kept.Name)) {
                    yield return i;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The variable that holds the object which an action puts something at, or puts under another object, or null
    /// for an action that does neither so (see <see cref="ChildPicksIn"/>).
    /// </summary>
    private static string? PlacedVariable(FsmStateAction action) {
        var held = action switch {
            SetPositionToObject put => put.targetObject,
            SetPositionToObject2D put => put.targetObject,
            SetParent { gameObject.OwnerOption: OwnerDefaultOption.SpecifyGameObject } put => put.gameObject.GameObject,
            _ => null
        };
        return held is { UseVariable: true, Name.Length: > 0 } ? held.Name : null;
    }

    /// <summary>
    /// Picks, in the scene host's game, the children that a state machine of a creature that every game runs by itself
    /// picks at random in a state that it goes into, for its actions to pick them there and for the step to tell them
    /// (see <see cref="ChildPicks"/>). A child that an earlier action of the state picked is not picked again: in the
    /// game, what the state tells it takes it out of the object before the next pick, as the spike that the boss sends
    /// out of its folder leaves it at once, and picked twice at the start, the second spike went nowhere, one fewer in
    /// every game (review of b48cf5a). Of the three of its last phase, the second waits a moment before it leaves,
    /// which the game may pick again for the third, sending out two: here it sends out three.
    /// </summary>
    private static void PickChildren(Fsm fsm, FsmState state) {
        Dictionary<int, GameObject?>? picks = null;
        foreach (var index in ChildPicksIn(state)) {
            var from = fsm.GetOwnerDefaultTarget(((GetRandomChild) state.Actions[index]).gameObject);
            var left = new List<GameObject>();
            if (from != null) {
                foreach (Transform each in from.transform) {
                    if (picks == null || !picks.ContainsValue(each.gameObject)) {
                        left.Add(each.gameObject);
                    }
                }
            }

            picks ??= new Dictionary<int, GameObject?>();
            var child = left.Count > 0 ? left[ChildDice.Next(left.Count)] : null;
            picks[index] = child;
            Logger.Info(
                $"[Own creatures] '{OwnCreatures.PlaceOf(fsm.GameObject)}' picks " +
                (child != null ? $"'{child.name}'" : "nothing") + $" at '{state.Name}' of '{fsm.Name}', " +
                $"of {left.Count} child(ren) that it did not pick there yet, which the step tells the members in " +
                "the room"
            );
        }

        if (picks != null) {
            ChildPicks[fsm] = (state.Name, picks);
        }
    }

    /// <summary>
    /// Writes the children that the scene host's game picked for a state machine as it went into the state of a step
    /// (see <see cref="PickChildren"/>): how many, then for each the number of its action and the child, by its place
    /// for a creature that every game runs by itself or a part of one, and otherwise by its path, or none.
    /// </summary>
    private static void WriteChildPicks(BinaryWriter writer, Fsm fsm, string stateName) {
        if (!ChildPicks.TryGetValue(fsm, out var pending) || pending.State != stateName) {
            writer.Write(0);
            return;
        }

        writer.Write(pending.Picks.Count);
        foreach (var (index, child) in pending.Picks) {
            writer.Write(index);
            if (child == null) {
                writer.Write(ChildNone);
            } else if (OwnCreatures.Has(child) || OwnCreatures.IsPart(child)) {
                writer.Write(ChildByPlace);
                writer.Write(OwnCreatures.PlaceOf(child));
            } else {
                writer.Write(ChildByPath);
                writer.Write(ScenePath.Get(child.transform));
            }
        }
    }

    /// <summary>
    /// Reads the children that the scene host's game picked as its state machine went into the state of a step (see
    /// <see cref="WriteChildPicks"/>), for the actions of this game's to pick them as it goes into the state. One that
    /// is not here, it picks by itself.
    /// </summary>
    private static void ReadChildPicks(BinaryReader reader, Fsm fsm, CoopSaveUpdate step) {
        var count = reader.ReadInt32();
        var picks = new Dictionary<int, GameObject?>();
        for (var i = 0; i < count; i++) {
            var index = reader.ReadInt32();
            var kind = reader.ReadByte();
            if (kind == ChildNone) {
                picks[index] = null;
                continue;
            }

            var name = reader.ReadString();
            var child = kind == ChildByPlace ? OwnCreatures.Find(null, name) : ScenePath.Find(name, step.Scene);
            if (child != null) {
                picks[index] = child;
            }

            Logger.Info(
                $"[Own creatures] '{step.ObjectPath}' picks '{name}' at '{step.StateName}' of '{fsm.Name}', the " +
                "child that the scene host's game picked" + (child != null ? "" : ", which is not here")
            );
        }

        if (picks.Count > 0) {
            ChildPicks[fsm] = (step.StateName, picks);
        } else {
            ChildPicks.Remove(fsm);
        }
    }

    /// <summary>
    /// Reads past the children that the scene host's game picked as its state machine went into the state of a step
    /// (see <see cref="WriteChildPicks"/>) that this game took already: kept, they were picked again where it went
    /// through the same state later on its way to another step of the scene host's (review of b48cf5a).
    /// </summary>
    private static void SkipChildPicks(BinaryReader reader) {
        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++) {
            reader.ReadInt32();
            if (reader.ReadByte() != ChildNone) {
                reader.ReadString();
            }
        }
    }

    /// <summary>
    /// Hook for an action that picks a child of an object at random, which picks the child that the scene host's game
    /// picked, where it has one for it (see <see cref="ChildPicks"/>), and none where the object had none there, as the
    /// game picks none then.
    /// </summary>
    private static void OnChildPick(Action<GetRandomChild> orig, GetRandomChild self) {
        if (ChildPicks.Count == 0 || self.Fsm is not { } fsm || !ChildPicks.TryGetValue(fsm, out var pending) ||
            pending.State != self.State.Name ||
            !pending.Picks.Remove(Array.IndexOf(self.State.Actions, self), out var child)) {
            orig(self);
            return;
        }

        if (pending.Picks.Count == 0) {
            ChildPicks.Remove(fsm);
        }

        if (child != null) {
            self.storeResult.Value = child;
        }
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
    /// The variable that holds the object which an action tells something once and at once, or, where it may tell it
    /// at any time, whenever it does, or null for an action that tells nothing so.
    /// </summary>
    private static string? PickedVariable(FsmStateAction action, bool anyTime = false) {
        return TellOf(action) is (
                   {
                       target: FsmEventTarget.EventTarget.GameObject or FsmEventTarget.EventTarget.GameObjectFSM,
                       gameObject: {
                           OwnerOption: OwnerDefaultOption.SpecifyGameObject,
                           GameObject: { UseVariable: true, Name.Length: > 0 } held
                       }
                   },
                   { Length: > 0 }, var once) && (once || anyTime)
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
