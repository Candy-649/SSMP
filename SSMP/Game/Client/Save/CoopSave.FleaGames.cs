using System;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Packet.Data;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The games of the festival in a checked two-player save, which no player starts alone. The character who runs a game
/// asks whether to play it, and a yes begins a round. Like the start of a race, that yes is held until the partner has
/// said yes at the same game too, and then both rounds begin; answering no takes it back. How the two players then play
/// is up to <see cref="FleaGameCoop"/>.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// A prompt that starts a game of the festival, which both players begin together. Like a race, both of them say
    /// yes at their own prompt.
    /// </summary>
    private const string WishConfirmFleaGame = "fleagame";

    /// <summary>
    /// The event with which the character who runs a game of the festival begins a round of it. Nothing else in the
    /// game sends it.
    /// </summary>
    private const string FleaGameBeginEvent = "GAME BEGIN";

    /// <summary>
    /// Whether a prompt is the character of a festival game asking whether to play, and the box on screen is the one it
    /// opens.
    /// </summary>
    private static bool IsFleaGamePrompt(YesNoAction action, YesNoBox box) {
        var isBoxOfPrompt = action is QuestYesNo or QuestYesNoV2 ? box is QuestYesNoBox : box is DialogueYesNoBox;
        return isBoxOfPrompt && action.Fsm is { } fsm && BeginsFleaGame(fsm);
    }

    /// <summary>
    /// Whether an FSM begins a round of a festival game: one of its states sends the event that does.
    /// </summary>
    private static bool BeginsFleaGame(Fsm fsm) {
        foreach (var state in fsm.States ?? []) {
            foreach (var action in state?.Actions ?? []) {
                if (action is SendEventByName { sendEvent.Value: FleaGameBeginEvent }) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// What to ask the partner about starting a festival game. It names the game rather than the prompt, so that the
    /// two players meet at the same game.
    /// </summary>
    private static CoopSaveUpdate? CreateFleaGameConfirm(YesNoAction action, ref string what) {
        var game = action.Fsm?.GameObject?.transform.parent;
        if (game == null) {
            return null;
        }

        what = $"starting '{game.name}'";
        var update = CreateConfirm(WishConfirmFleaGame);
        update.WishNames.Add(game.gameObject.scene.name + "/" + ScenePath.Get(game));
        return update;
    }

    /// <summary>
    /// Whether the yes that waits for the answer with a key is the start of a festival game.
    /// </summary>
    private bool IsFleaGameHold(ulong key) {
        return _wishConfirm is { } held && held.Key == key &&
               held.WishKey.StartsWith(WishConfirmFleaGame + "\n", StringComparison.Ordinal);
    }
}
