using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// What a part of a creature set on one of the creature's FSMs along with the event that it told that FSM, like where
/// the thing it caught was: a tendril puts that spot on the creature before it tells the creature to reel the thing in.
/// When a part of a scene client's copy catches something of that player, the copy's FSM plays the catch at once with
/// what the part set on it, and the scene host's FSM needs the same set before it plays the catch too (see
/// <see cref="Entity.TakeInput"/>). Being there at all, even with nothing in it, says that an input is a catch.
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
        if (sender.ActiveState is not { } state) {
            return None;
        }

        var values = new List<(string Name, object Value)>();
        foreach (var action in state.Actions) {
            if (values.Count >= MaxValues) {
                break;
            }

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

            var name = (fields.VariableName.GetValue(action) as FsmString)?.Value;
            if (string.IsNullOrEmpty(name) || ValueOf(told.FsmVariables.GetVariable(name)) is not { } value) {
                continue;
            }

            values.Add((name!, value));
        }

        return values.Count == 0 ? None : new ToldValues(values);
    }

    /// <summary>
    /// Sets the values on the FSM of the same creature in this game, on the variables of the same name and kind.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    public void ApplyTo(HutongGames.PlayMaker.Fsm fsm) {
        var variables = fsm.Variables;
        foreach (var (name, value) in _values) {
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
            _ => null
        };
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
