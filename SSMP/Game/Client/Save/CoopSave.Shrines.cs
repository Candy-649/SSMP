using System;
using System.Collections.Generic;
using System.Reflection;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The bell shrines of a checked two-player save. Each save keeps how far the sequence of a shrine got in its room
/// (<see cref="StateChangeSequence"/> with a saved number), and both saves share the flag that the shrine's lever sets
/// when it is hit. The game shows a shrine whose flag is set as finished when its room loads.
/// </summary>
internal partial class CoopSave {
    private static readonly FieldInfo? SequenceCompleteBoolField =
        typeof(StateChangeSequence).GetField("isCompleteBool", InstanceFlags);

    private static readonly FieldInfo? SequenceStatesField = typeof(StateChangeSequence).GetField("states", InstanceFlags);
    private static readonly FieldInfo? SequenceValueField = typeof(StateChangeSequence).GetField("stateValue", InstanceFlags);

    /// <summary>
    /// Finishes the shrines in the loaded rooms whose flags the partner's game just set, as the room does when it loads
    /// with the flag set. A hit on a shrine's lever only goes to a partner who is in the room already, so a lever that
    /// the partner hit while the local player was still on the way into the room never reached this game, nor did the
    /// hits that brought the bell down. The flag still arrived, but the room had set itself up by then and went on
    /// showing the shrine as it was here, so the local player had to do it all again.
    ///
    /// A shrine whose lever was hit here as well set the flag here already, which the partner's flag then doesn't
    /// change, so this never cuts short a shrine that rings here.
    /// </summary>
    /// <param name="flags">The names of the flags that the partner's game set and that were not set here.</param>
    private static void FinishLoadedShrines(List<string> flags) {
        if (SequenceCompleteBoolField == null || SequenceStatesField == null || SequenceValueField == null) {
            return;
        }

        foreach (var sequence in Object.FindObjectsByType<StateChangeSequence>(UnityEngine.FindObjectsSortMode.None)) {
            try {
                if (SequenceCompleteBoolField.GetValue(sequence) is not string flag || !flags.Contains(flag) ||
                    SequenceStatesField.GetValue(sequence) is not Array states ||
                    SequenceValueField.GetValue(sequence) is not int value) {
                    continue;
                }

                // A sequence that has not started yet finishes itself when it starts, since the flag is set now
                var last = states.Length - 1;
                if (value < 0 || value >= last) {
                    continue;
                }

                Logger.Info(
                    $"The partner finished the shrine '{sequence.name}' in {sequence.gameObject.scene.name} " +
                    $"(flag {flag}), which stood at {value} of {last} here: finishing it as its room does"
                );
                sequence.SetStateReturn(last);
            } catch (Exception e) {
                Logger.Warn($"Could not finish the shrine '{sequence.name}' that the partner finished:\n{e}");
            }
        }
    }
}
