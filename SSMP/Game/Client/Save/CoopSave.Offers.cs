using System;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Packet.Data;

namespace SSMP.Game.Client.Save;

/// <summary>
/// A creature asking at a prompt of its own whether to fight, in a checked two-player save: a knight offering to
/// spar, a guardian offering its challenge. The creature is one for both players - its fight is fought by both of
/// them - so neither starts it alone (USER 10-10: "骑虫子的骑士两边都选是才开始吧"). Like asking for help in a fight,
/// both say yes at their own prompt: each talks to the creature in their own game (a talk with a copy runs there, see
/// Entity.TalkHere), and a no from either of them, or no answer in time, is a no for both.
///
/// A prompt is one of these when it belongs to a creature of the room (a registered entity), whatever it asks: the
/// census of the game's prompts (10-10) found exactly the four fight offers and the asking for help, which is checked
/// before this.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// A prompt of a creature of the room, which both players say yes to at their own prompt.
    /// </summary>
    private const string WishConfirmOffer = "offer";

    /// <summary>
    /// How long a yes to a creature's offer waits for the partner's, in seconds. Longer than for the rest, since the
    /// partner first has to walk up to the creature and hear it out before they are asked.
    /// </summary>
    private const float OfferConfirmTimeout = 45f;

    /// <summary>
    /// Whether a prompt is one that a creature of the room asks, and the box on screen is the one it opens.
    /// </summary>
    private static bool IsOfferPrompt(YesNoAction action, YesNoBox box) {
        return box is DialogueYesNoBox && GetOfferCreature(action) != null;
    }

    /// <summary>
    /// The creature of the room that a prompt belongs to - the room's own object, the copy of it, or a part of either -
    /// or null for any other prompt.
    /// </summary>
    private static Entity.Entity? GetOfferCreature(YesNoAction action) {
        return action is DialogueYesNo or DialogueYesNoV2 && action.Fsm?.GameObject is { } owner
            ? Entity.Entity.FindByPart(owner)
            : null;
    }

    /// <summary>
    /// What a prompt asks: the sheet and key of its text, or its own text where it has one.
    /// </summary>
    private static string GetOfferQuestion(YesNoAction action) {
        return action switch {
            DialogueYesNo { Text.IsNone: false } literal => literal.Text.Value ?? "",
            DialogueYesNo v1 => v1.TranslationSheet?.Value + "/" + v1.TranslationKey?.Value,
            DialogueYesNoV2 v2 => v2.TranslationSheet?.Value + "/" + v2.TranslationKey?.Value,
            _ => ""
        };
    }

    /// <summary>
    /// What to ask the partner about a creature's prompt. It names the creature by its entity, which is the same in
    /// both games, and what it asks.
    /// </summary>
    private static CoopSaveUpdate? CreateOfferConfirm(YesNoAction action, ref string what) {
        if (GetOfferCreature(action) is not { } creature) {
            return null;
        }

        var question = GetOfferQuestion(action);
        what = $"a prompt of entity {creature.Id} ({creature.Type}, {question})";
        var update = CreateConfirm(WishConfirmOffer);
        update.WishNames.Add(creature.Id + "\n" + question);
        return update;
    }

    /// <summary>
    /// Whether the yes that waits for the answer with a key answers a creature's prompt.
    /// </summary>
    private bool IsOfferHold(ulong key) {
        return _wishConfirm is { } held && held.Key == key && IsOfferWishKey(held.WishKey);
    }

    /// <summary>
    /// Whether what a wait is matched on is a creature's prompt.
    /// </summary>
    private static bool IsOfferWishKey(string wishKey) {
        return wishKey.StartsWith(WishConfirmOffer + "\n", StringComparison.Ordinal);
    }
}
