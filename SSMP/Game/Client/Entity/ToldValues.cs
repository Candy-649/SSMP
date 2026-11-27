using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// What a part of a creature set on one of the creature's FSMs along with the event that it told that FSM, like where
/// the thing it caught was: a tendril puts that spot on the creature before it tells the creature to reel the thing in.
/// When a part of a scene client's copy catches something of that player, the copy's FSM plays the catch at once with
/// what the part set on it, and the scene host's FSM needs the same set before it plays the catch too (see
/// <see cref="Entity.TakeInput"/>). Being there at all, even with nothing in it, says that an input is a catch. The
/// same carries what the state that a creature that every game runs by itself leaves worked out, with the step of the
/// scene host's creature (see CoopSave.OwnCreatures), and an object placed in the room that it picked, like the
/// platform nearest the player that a creature comes up out of, by its path there (see <see cref="RoomObject"/>), and
/// the creature that an ally fights (see <see cref="FoughtCreature"/>).
/// </summary>
internal sealed class ToldValues {
    /// <summary>
    /// A catch with nothing set along with it.
    /// </summary>
    public static readonly ToldValues None = new([]);

    /// <summary>
    /// The most values that are sent.
    /// </summary>
    private const int MaxValues = byte.MaxValue;

    /// <summary>
    /// The fields of the actions that set a variable of another FSM, by the type of the action, or null for a type
    /// without them.
    /// </summary>
    private static readonly Dictionary<Type, (FieldInfo Target, FieldInfo FsmName, FieldInfo VariableName)?>
        SetterFields = new();

    /// <summary>
    /// The fields of each kind of action in which it stores an object that it found, by type (see
    /// <see cref="ObjectsStoredBy"/>).
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> ObjectStoreFields = new();

    /// <summary>
    /// The values, by the name of their variable.
    /// </summary>
    private readonly List<(string Name, object Value)> _values;

    private ToldValues(List<(string Name, object Value)> values) {
        _values = values;
    }

    /// <summary>
    /// Takes what a part set on an FSM in the state from which it told that FSM an event: the values that the actions
    /// of that state which set variables of the FSM left in it.
    /// </summary>
    /// <param name="sender">The FSM of the part.</param>
    /// <param name="target">The object of the FSM that was told.</param>
    /// <param name="told">The FSM that was told.</param>
    public static ToldValues From(HutongGames.PlayMaker.Fsm sender, GameObject target, PlayMakerFSM told) {
        return Of(told.Fsm, SetBy(sender, target, told));
    }

    /// <summary>
    /// The names of the variables of an FSM that a part set on it in the state from which it told that FSM an event:
    /// those that the actions of that state which set variables of the FSM set.
    /// </summary>
    /// <param name="sender">The FSM of the part.</param>
    /// <param name="target">The object of the FSM that was told.</param>
    /// <param name="told">The FSM that was told.</param>
    public static IEnumerable<string> SetBy(HutongGames.PlayMaker.Fsm sender, GameObject target, PlayMakerFSM told) {
        if (sender.ActiveState is not { } state) {
            yield break;
        }

        foreach (var action in state.Actions) {
            // The actions that read a variable of another FSM have the same fields
            if (action == null || !action.GetType().Name.StartsWith("SetFsm", StringComparison.Ordinal) ||
                GetSetterFields(action.GetType()) is not { } fields ||
                fields.Target.GetValue(action) is not FsmOwnerDefault owner ||
                sender.GetOwnerDefaultTarget(owner) != target) {
                continue;
            }

            // An action without the name of an FSM sets the first FSM of the object
            var fsmName = (fields.FsmName.GetValue(action) as FsmString)?.Value;
            if (string.IsNullOrEmpty(fsmName) ? target.GetComponent<PlayMakerFSM>() != told : fsmName != told.FsmName) {
                continue;
            }

            if ((fields.VariableName.GetValue(action) as FsmString)?.Value is { Length: > 0 } name) {
                yield return name;
            }
        }
    }

    /// <summary>
    /// Takes the values that the given variables of an FSM hold, of those of a kind that is sent.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="names">The names of the variables.</param>
    /// <param name="fought">The names of those in which it keeps the creature that it fights (see
    /// <see cref="FoughtCreature"/>).</param>
    public static ToldValues Of(
        HutongGames.PlayMaker.Fsm fsm,
        IEnumerable<string> names,
        ICollection<string>? fought = null
    ) {
        var values = new List<(string Name, object Value)>();
        foreach (var name in names) {
            if (values.Count >= MaxValues) {
                break;
            }

            var variable = fsm.Variables.GetVariable(name);
            var value = fought?.Contains(name) == true && variable is FsmGameObject creature
                ? FoughtCreature.Of(creature.Value)
                : ValueOf(variable);
            if (value != null) {
                values.Add((name, value));
            }
        }

        return values.Count == 0 ? None : new ToldValues(values);
    }

    /// <summary>
    /// Sets the values on the FSM of the same creature in this game, on the variables of the same name and kind.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="only">The names of the variables to set, or null for all.</param>
    public void ApplyTo(HutongGames.PlayMaker.Fsm fsm, ICollection<string>? only = null) {
        var variables = fsm.Variables;
        foreach (var (name, value) in _values) {
            if (only != null && !only.Contains(name)) {
                continue;
            }

            switch (value) {
                case float number when variables.FindFsmFloat(name) is { } variable:
                    variable.Value = number;
                    break;
                case int number when variables.FindFsmInt(name) is { } variable:
                    variable.Value = number;
                    break;
                case bool flag when variables.FindFsmBool(name) is { } variable:
                    variable.Value = flag;
                    break;
                case string text when variables.FindFsmString(name) is { } variable:
                    variable.Value = text;
                    break;
                case Vector2 vector when variables.FindFsmVector2(name) is { } variable:
                    variable.Value = vector;
                    break;
                case Vector3 vector when variables.FindFsmVector3(name) is { } variable:
                    variable.Value = vector;
                    break;
                case RoomObject thing when variables.FindFsmGameObject(name) is { } variable &&
                                           ScenePath.Find(thing.Path, thing.Scene) is { } found:
                    variable.Value = found;
                    break;
                case FoughtCreature creature when variables.FindFsmGameObject(name) is { } variable:
                    var here = creature.Find();
                    if (here != variable.Value) {
                        Logger.Info(
                            $"[Own creatures] '{fsm.GameObject.name}' fights " +
                            (here != null ? $"'{here.name}'" : "nothing") + " as in the scene host's game"
                        );
                    }

                    variable.Value = here;
                    break;
            }
        }
    }

    /// <summary>
    /// The names of the variables of its FSM in which an action stores an object that it found, like the nearest of a
    /// kind (see CoopSave.OwnCreatures).
    /// </summary>
    public static IEnumerable<string> ObjectsStoredBy(FsmStateAction action) {
        var type = action.GetType();
        if (!ObjectStoreFields.TryGetValue(type, out var fields)) {
            fields = Array.FindAll(
                type.GetFields(BindingFlags.Instance | BindingFlags.Public),
                field => field.FieldType == typeof(FsmGameObject) &&
                         field.Name.StartsWith("store", StringComparison.OrdinalIgnoreCase)
            );
            ObjectStoreFields[type] = fields;
        }

        foreach (var field in fields) {
            if (field.GetValue(action) is FsmGameObject { UseVariable: true, Name.Length: > 0 } variable) {
                yield return variable.Name;
            }
        }
    }

    /// <summary>
    /// Writes the values.
    /// </summary>
    public void Write(BinaryWriter writer) {
        writer.Write((byte) _values.Count);
        foreach (var (name, value) in _values) {
            writer.Write(name);
            switch (value) {
                case float number:
                    writer.Write((byte) VariableType.Float);
                    writer.Write(number);
                    break;
                case int number:
                    writer.Write((byte) VariableType.Int);
                    writer.Write(number);
                    break;
                case bool flag:
                    writer.Write((byte) VariableType.Bool);
                    writer.Write(flag);
                    break;
                case string text:
                    writer.Write((byte) VariableType.String);
                    writer.Write(text);
                    break;
                case Vector2 vector:
                    writer.Write((byte) VariableType.Vector2);
                    writer.Write(vector.x);
                    writer.Write(vector.y);
                    break;
                case Vector3 vector:
                    writer.Write((byte) VariableType.Vector3);
                    writer.Write(vector.x);
                    writer.Write(vector.y);
                    writer.Write(vector.z);
                    break;
                case RoomObject thing:
                    writer.Write((byte) VariableType.GameObject);
                    writer.Write(thing.Scene);
                    writer.Write(thing.Path);
                    break;
                // Under the kind of a variable of any object, which no other value is sent as
                case FoughtCreature creature:
                    writer.Write((byte) VariableType.Object);
                    writer.Write(creature.Scene);
                    writer.Write(creature.Place);
                    break;
            }
        }
    }

    /// <summary>
    /// Reads values that <see cref="Write"/> wrote.
    /// </summary>
    /// <exception cref="IOException">The values are cut short or of a kind that is never written.</exception>
    public static ToldValues Read(BinaryReader reader) {
        var count = reader.ReadByte();
        var values = new List<(string Name, object Value)>(count);
        for (var i = 0; i < count; i++) {
            var name = reader.ReadString();
            object value = (VariableType) reader.ReadByte() switch {
                VariableType.Float => reader.ReadSingle(),
                VariableType.Int => reader.ReadInt32(),
                VariableType.Bool => reader.ReadBoolean(),
                VariableType.String => reader.ReadString(),
                VariableType.Vector2 => new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                VariableType.Vector3 => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                VariableType.GameObject => new RoomObject(reader.ReadString(), reader.ReadString()),
                VariableType.Object => new FoughtCreature(reader.ReadString(), reader.ReadString()),
                var kind => throw new IOException($"A told value of the kind {kind} is never written")
            };
            values.Add((name, value));
        }

        return values.Count == 0 ? None : new ToldValues(values);
    }

    /// <summary>
    /// The value of a variable of a kind that is sent, or null.
    /// </summary>
    private static object? ValueOf(NamedVariable? variable) {
        return variable switch {
            FsmFloat number => number.Value,
            FsmInt number => number.Value,
            FsmBool flag => flag.Value,
            FsmString text => text.Value ?? "",
            FsmVector2 vector => vector.Value,
            FsmVector3 vector => vector.Value,
            FsmGameObject { Value: { } thing } when thing != null => RoomObject.Of(thing),
            _ => null
        };
    }

    /// <summary>
    /// An object placed in the room, by its scene and its path there (see ScenePath), which finds the same object in
    /// every game.
    /// </summary>
    private sealed record RoomObject(string Scene, string Path) {
        /// <summary>
        /// The object as one placed in the room, or null for one that is not: a player, kept across rooms, or one made
        /// as the room went on, which each game makes by itself.
        /// </summary>
        public static RoomObject? Of(GameObject thing) {
            if (!thing.scene.IsValid() || thing.scene.name == "DontDestroyOnLoad") {
                return null;
            }

            var path = ScenePath.Get(thing.transform);
            return path.Contains("(Clone)") ? null : new RoomObject(thing.scene.name, path);
        }
    }

    /// <summary>
    /// The creature that a creature that fights the room's creatures beside the players picked to fight (see
    /// CoopSave.OwnFighters), by its scene and a name that finds it in every game: its path in the room, or the place
    /// of a summon that every game makes by itself (see OwnCreatures.SummonPathOf); an empty one for no creature.
    /// </summary>
    private sealed record FoughtCreature(string Scene, string Place) {
        /// <summary>
        /// The creature as one fought, an empty one for none, or null for one that has no such name: kept across rooms,
        /// or made as the room went on by one game alone, which leaves the creature that this game's picked.
        /// </summary>
        public static FoughtCreature? Of(GameObject? thing) {
            if (thing == null) {
                return new FoughtCreature("", "");
            }

            if (OwnCreatures.SummonPathOf(thing) is { } summon) {
                return new FoughtCreature(thing.scene.name, summon);
            }

            return RoomObject.Of(thing) is { } placed ? new FoughtCreature(placed.Scene, placed.Path) : null;
        }

        /// <summary>
        /// The creature here, or null for none, or for one that is not here.
        /// </summary>
        public GameObject? Find() {
            return Place.Length == 0 ? null
                : OwnCreatures.IsSummonPlace(Place) ? OwnCreatures.FindSummonPath(Scene, Place)
                : ScenePath.Find(Place, Scene);
        }
    }

    /// <summary>
    /// The fields of an action that sets a variable of another FSM: the object of that FSM, its name and the name of the
    /// variable, or null for an action without them.
    /// </summary>
    private static (FieldInfo Target, FieldInfo FsmName, FieldInfo VariableName)? GetSetterFields(Type type) {
        if (SetterFields.TryGetValue(type, out var fields)) {
            return fields;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var target = type.GetField("gameObject", flags);
        var fsmName = type.GetField("fsmName", flags);
        var variableName = type.GetField("variableName", flags);
        fields = target?.FieldType == typeof(FsmOwnerDefault) && fsmName?.FieldType == typeof(FsmString) &&
                 variableName?.FieldType == typeof(FsmString)
            ? (target, fsmName, variableName)
            : null;
        SetterFields[type] = fields;
        return fields;
    }
}
