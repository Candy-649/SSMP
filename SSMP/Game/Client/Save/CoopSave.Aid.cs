using System;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Packet.Data;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Asking a character to help in a fight, in a checked two-player save. The fight is fought by both players, so the
/// help is for both of them, and neither asks for it alone: like the accepting of a wish, both say yes at their own
/// prompt, and a no from either of them - or no answer - is a no for both. The fight then has the one helper, which the
/// game that runs the fight runs and the other game shows.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// A prompt that asks a character to help in a fight, which both players say yes to at their own prompt.
    /// </summary>
    private const string WishConfirmAid = "aid";

    /// <summary>
    /// The variable of a character who can be asked to help in a fight that names what the game writes down once they
    /// have been asked. It names nothing where they only talk.
    /// </summary>
    private const string AidFlagVariable = "PD Aid Bool";

    /// <summary>
    /// Whether a prompt is a character asking whether to help in a fight, and the box on screen is the one it opens.
    /// </summary>
    private static bool IsAidPrompt(YesNoAction action, YesNoBox box) {
        return box is DialogueYesNoBox && GetAidFlag(action.Fsm) != null;
    }

    /// <summary>
    /// What the game writes down once the character of an FSM has been asked to help, or null for an FSM of no such
    /// character.
    /// </summary>
    private static string? GetAidFlag(Fsm? fsm) {
        var flag = fsm?.Variables.FindFsmString(AidFlagVariable)?.Value;
        return string.IsNullOrEmpty(flag) ? null : flag;
    }

    /// <summary>
    /// What to ask the partner about asking a character to help. It names what the game writes down for it, which is
    /// the same in both games and different for each character and fight.
    /// </summary>
    private static CoopSaveUpdate? CreateAidConfirm(YesNoAction action, ref string what) {
        if (GetAidFlag(action.Fsm) is not { } flag) {
            return null;
        }

        what = $"asking for help in a fight ({flag})";
        var update = CreateConfirm(WishConfirmAid);
        update.WishNames.Add(flag);
        return update;
    }

    /// <summary>
    /// Whether the yes that waits for the answer with a key asks a character to help.
    /// </summary>
    private bool IsAidHold(ulong key) {
        return _wishConfirm is { } held && held.Key == key &&
               held.WishKey.StartsWith(WishConfirmAid + "\n", StringComparison.Ordinal);
    }
}
