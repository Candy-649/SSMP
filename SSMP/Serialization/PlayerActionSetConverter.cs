using System;
using InControl;
using Newtonsoft.Json;
using SSMP.Util;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Serialization;

/// <summary>
/// JsonConverter to serialize and deserialize classes that derive from <c>PlayerActionSet</c>.<br/>
/// The target class needs to have a parameterless constructor
/// that initializes the player actions that get read/written.<br/>
/// All the added actions will get processed,
/// so if there are unmappable actions, an <c>IMappablePlayerActions</c> interface should be added
/// to filter the mappable keybinds.
/// </summary>
public class PlayerActionSetConverter : JsonConverter {
    /// <inheritdoc />
    public override bool CanConvert(Type objectType) => objectType.IsSubclassOf(typeof(PlayerActionSet));

    /// <inheritdoc />
    public override object ReadJson(
        JsonReader reader, 
        Type objectType, 
        object? existingValue, 
        JsonSerializer serializer
    ) {
        var set = (PlayerActionSet) Activator.CreateInstance(objectType);
        // ReSharper disable once SuspiciousTypeConversion.Global
        Predicate<string> filter = set is IMappablePlayerActions mpa ? mpa.IsMappable : _ => true;
        reader.Read();
        while (reader.TokenType == JsonToken.PropertyName) {
            var name = (string) reader.Value!;
            if (!filter(name)) {
                // value
                reader.Read();
                // JsonToken.PropertyName
                reader.Read();
                continue;
            }

            var ac = set.GetPlayerActionByName(name);
            reader.Read();
            if (ac != null) {
                if (reader.TokenType == JsonToken.StartArray) {
                    ac.ClearBindings();

                    reader.Read();
                    while (reader.TokenType != JsonToken.EndArray) {
                        if (reader.Value is string entry) {
                            AddBinding(ac, entry);
                        }

                        reader.Read();
                    }
                } else if (reader.Value is string val) {
                    // A file written before a binding could be a list holds a single value. Clearing everything for
                    // it would also throw away the gamepad button the action was just built with, and the file names
                    // no gamepad button to put back - which is how a gamepad binding disappeared on first read. The
                    // key it does name replaces the key, and the gamepad button it is silent about is kept.
                    var controller = ac.GetControllerButtonBinding();

                    if (val == new InputHandler.KeyOrMouseBinding().ToString() ||
                        val == nameof(InputControlType.None)) {
                        ac.ClearBindings();
                    } else if (KeybindUtil.ParseBinding(val) is { } bind) {
                        ac.ClearBindings();
                        ac.AddKeyOrMouseBinding(bind);
                        ac.AddInputControlType(controller);
                    } else if (KeybindUtil.ParseInputControlTypeBinding(val) is { } button) {
                        ac.ClearBindings();
                        ac.AddInputControlType(button);
                    } else {
                        Logger.Warn($"Invalid keybinding {val}");
                    }
                } else {
                    Logger.Warn($"Expected a string or a list for keybind, got `{reader.Value}");
                }
            } else {
                Logger.Warn($"Invalid keybind name {name}");
            }

            // JsonToken.PropertyName
            reader.Read();
        }

        return set;

        static void AddBinding(PlayerAction action, string value) {
            if (value == nameof(InputControlType.None)) {
                return;
            }

            if (KeybindUtil.ParseBinding(value) is { } bind) {
                action.AddKeyOrMouseBinding(bind);
            } else if (KeybindUtil.ParseInputControlTypeBinding(value) is { } button) {
                action.AddInputControlType(button);
            } else {
                Logger.Warn($"Invalid keybinding {value}");
            }
        }
    }

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) {
        var set = (PlayerActionSet) value!;
        Predicate<string> filter;
        // ReSharper disable once SuspiciousTypeConversion.Global
        if (set is IMappablePlayerActions mpa) filter = mpa.IsMappable;
        else filter = _ => true;
        writer.WriteStartObject();
        foreach (var ac in set.Actions) {
            if (filter(ac.Name)) {
                writer.WritePropertyName(ac.Name);

                // A list rather than one value. An action can be reached from a key and from a gamepad button at the
                // same time, and writing only one of the two threw the other away the first time the settings were
                // saved - which is why a gamepad button could never survive a session.
                writer.WriteStartArray();

                var keyOrMouse = ac.GetKeyOrMouseBinding();
                if (keyOrMouse.Key != Key.None) {
                    writer.WriteValue(keyOrMouse.ToString());
                }

                var controllerButton = ac.GetControllerButtonBinding();
                if (controllerButton != InputControlType.None) {
                    writer.WriteValue(controllerButton.ToString());
                }

                writer.WriteEndArray();
            }
        }

        writer.WriteEndObject();
    }
}

/// <summary>
/// An interface to signify mappable player actions to be used in conjunction with <c>PlayerActionSetConverter</c>.
/// </summary>
public interface IMappablePlayerActions {
    /// <summary>
    /// Checks if the passed in string should be read/written from the JSON stream
    /// </summary>
    /// <param name="name">The name of the player action</param>
    /// <returns></returns>
    public bool IsMappable(string name);
}
