using System.Linq;
using HutongGames.PlayMaker.Actions;
using SSMP.Internals;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation.Effects;

/// <summary>
/// The voice of the hero as it taunts. Only the animation of a taunt reached the other players, so they saw it without
/// hearing it. Of all the hero's sounds this one is played on purpose: the hero's other sounds stay with the player
/// whose hero it is, so that two heroes do not make twice the noise.
///
/// It goes by the clips of the character, like the parts of its moves (see <see cref="PartsByClips"/>), rather than by a
/// word of its own: the game says it right as the taunt begins, in the voice of the beast crest for that crest, and the
/// taunt with the rings says its own as it begins.
/// </summary>
internal static class TauntVoice {
    /// <summary>
    /// The clip of a taunt, which the game starts right after the voice.
    /// </summary>
    private const string TauntClip = "Taunt";

    /// <summary>
    /// The clip of the taunt with the rings, which the game starts with its voice.
    /// </summary>
    private const string RingsClip = "Taunt Rings";

    /// <summary>
    /// The state of the hero's taunt that picks the voice by the crest.
    /// </summary>
    private const string VoiceTypeState = "Voice Type";

    /// <summary>
    /// Says the voice of a taunt that the character of another player begins, from the local hero's own state, whose
    /// voice is picked at random each time just as it is there.
    /// </summary>
    /// <param name="playerObject">The character.</param>
    /// <param name="id">The ID of the player, for the log.</param>
    /// <param name="clipName">The name of the clip that the character starts.</param>
    /// <param name="crestType">The crest of the player.</param>
    /// <param name="startTime">How far into the clip it starts, in seconds: more than 0 for a clip that was put right
    /// after its change got lost, whose voice is not said when it comes too late.</param>
    public static void OnCharacterClip(GameObject playerObject, ushort id, string clipName, CrestType crestType, float startTime) {
        if (clipName != TauntClip && clipName != RingsClip || startTime > PartsByClips.LateBy) {
            return;
        }

        var fsm = HeroController.instance != null ? HeroController.instance.silkSpecialFSM : null;
        if (fsm == null) {
            return;
        }

        var stateName = clipName == RingsClip
            ? "Taunt Antic Rings"
            : IsBeastVoice(fsm, crestType) ? "Beast" : "Standard";
        var state = fsm.GetStateOrNull(stateName);
        if (state == null) {
            Logger.Warn($"Unable to find taunt state '{stateName}' to say the voice of another player's taunt");
            return;
        }

        foreach (var voice in state.Actions.OfType<PlayRandomAudioClipTable>()) {
            AudioUtil.PlayAudio(voice, playerObject);
        }

        Logger.Info($"[Parts] Player {id} starts '{clipName}': says the voice of '{stateName}'");
    }

    /// <summary>
    /// Whether a crest taunts in the voice of the beast crest, by the crest that the local hero's taunt checks for.
    /// </summary>
    private static bool IsBeastVoice(PlayMakerFSM fsm, CrestType crestType) {
        var check = fsm.GetStateOrNull(VoiceTypeState)?.Actions.OfType<CheckIfCrestEquipped>().FirstOrDefault();
        return check?.Crest?.Value is ToolCrest crest && crest.name == crestType.ToInternal();
    }
}
