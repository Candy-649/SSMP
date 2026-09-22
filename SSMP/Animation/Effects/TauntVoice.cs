using System;
using System.Linq;
using HutongGames.PlayMaker.Actions;
using SSMP.Fsm;
using SSMP.Internals;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Animation.Effects;

/// <summary>
/// The voice of the hero as it taunts. Only the animation of a taunt reached the other players, so they saw it without
/// hearing it. Of all the hero's sounds this one is sent on purpose: the hero's other sounds stay with the player whose
/// hero it is, so that two heroes do not make twice the noise.
/// </summary>
internal class TauntVoice : AnimationEffect {
    /// <summary>
    /// The states of the local hero's taunt that say something, by what goes over the network: the voice of most
    /// crests, the voice of the beast crest, and the taunt with the rings.
    /// </summary>
    private static readonly string[] VoiceStates = ["Standard", "Beast", "Taunt Antic Rings"];

    /// <summary>
    /// Hooks the states of the local hero's taunt that say something.
    /// </summary>
    /// <param name="silkSkillFsm">The FSM of the hero's silk skills, which also taunts.</param>
    /// <param name="send">Sends the effect info of a taunt that says something to the other players.</param>
    public static void Hook(PlayMakerFSM silkSkillFsm, Action<byte[]> send) {
        for (var i = 0; i < VoiceStates.Length; i++) {
            var state = silkSkillFsm.GetStateOrNull(VoiceStates[i]);
            if (state == null) {
                Logger.Warn($"Unable to find taunt state '{VoiceStates[i]}' to hook");
                continue;
            }

            byte[] effectInfo = [(byte) i];
            FsmStateActionInjector.Inject(state, _ => send(effectInfo));
        }
    }

    /// <inheritdoc/>
    public override byte[]? GetEffectInfo() {
        return null;
    }

    /// <inheritdoc/>
    public override void Play(GameObject playerObject, CrestType crestType, byte[]? effectInfo) {
        if (effectInfo is not [var index] || index >= VoiceStates.Length) {
            return;
        }

        // Said from the local hero's own state, whose voice is picked at random each time just as it is there
        var state = HeroController.instance.silkSpecialFSM.GetState(VoiceStates[index]);
        foreach (var voice in state.Actions.OfType<PlayRandomAudioClipTable>()) {
            AudioUtil.PlayAudio(voice, playerObject);
        }
    }
}
