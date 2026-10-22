using System;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The games that each player plays on their own in a checked two-player save - the games of the festival and the
/// contest of throwing pins - which no player starts alone. The character who runs a game asks whether to play it, and
/// a yes begins a round. Like the start of a race, that yes is held until the partner has said yes at the same game
/// too, and then both rounds begin; answering no takes it back. How the players then play is up to
/// <see cref="MinigameCoop"/>.
///
/// The character of the contest offers a player no round at times: once they have won every round that is open while
/// the last one is not open yet, and once it has nothing left to pay out. Such a player could never say yes, and the
/// partner would be kept from the contest for as long as that lasts, which for the second is for good. Their game
/// agrees for them without playing, and the partner plays on their own (<see cref="AgreeWithoutPlaying"/>).
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
    /// The event with which the character who runs the contest of throwing pins begins a round of it, once its entry is
    /// paid and its talk is over. Nothing else in the game sends it.
    /// </summary>
    private const string PinGameBeginEvent = "PIN CHALLENGE START";

    /// <summary>
    /// The path of the place of the contest of throwing pins, which its character stands under: what an ask about it
    /// names after the name of the room (see <see cref="CreateFleaGameConfirm"/>).
    /// </summary>
    private const string PinGamePlace = "Black Thread States/Normal World/Pin Gallery States/Here";

    /// <summary>
    /// Why a yes was given, sent with it (CoopSaveUpdate.Part) about the contest of throwing pins: the member agreed
    /// without playing, since the character of the contest offers them no round now.
    /// </summary>
    private const ushort YesWithoutPlaying = 1;

    /// <summary>
    /// Whether a prompt is the character of a game that each player plays on their own asking whether to play, and the
    /// box on screen is the one it opens.
    /// </summary>
    private static bool IsFleaGamePrompt(YesNoAction action, YesNoBox box) {
        var isBoxOfPrompt = action is QuestYesNo or QuestYesNoV2 ? box is QuestYesNoBox : box is DialogueYesNoBox;
        return isBoxOfPrompt && action.Fsm is { } fsm && BeginsFleaGame(fsm);
    }

    /// <summary>
    /// Whether an FSM begins a round of a game that each player plays on their own: one of its states sends the event
    /// that does.
    /// </summary>
    private static bool BeginsFleaGame(Fsm fsm) {
        foreach (var state in fsm.States ?? []) {
            foreach (var action in state?.Actions ?? []) {
                if (action is SendEventByName { sendEvent.Value: FleaGameBeginEvent } or
                    SendEventToRegister { eventName.Value: PinGameBeginEvent }) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// What to ask the partner about starting a game that each player plays on their own. It names the game rather than
    /// the prompt, so that the two players meet at the same game: the object that the character of a festival game
    /// stands under, which is the game, or the place that the character of the contest stands in.
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
    /// Whether an ask about starting a game that each player plays on their own is about the contest of throwing pins,
    /// by the game it names.
    /// </summary>
    private static bool IsPinGameWish(string wish) => wish.EndsWith("/" + PinGamePlace, StringComparison.Ordinal);

    /// <summary>
    /// Whether the character of the contest of throwing pins would offer the local player a round if they talked to it
    /// now. It reads the save the way the character's own dialogue does (its Convo state machine, from "Met?"): no
    /// round once it has nothing left to pay out, nor once the player has won the second round while the last one is
    /// not open yet.
    /// </summary>
    private static bool CanPlayPinGameNow() {
        var playerData = PlayerData.instance;
        if (playerData == null) {
            return true;
        }

        return playerData.PinGalleryWallet > 0 &&
               (playerData.WasInPinChallenge || !playerData.MetPinChallengeBug ||
                playerData.pinGalleriesCompleted != 2 || playerData.PinGalleryLastChallengeOpen);
    }

    /// <summary>
    /// Answers a member's yes at the contest of throwing pins at once, as a yes without playing, when its character
    /// would offer the local player no round now (see the class).
    /// </summary>
    /// <param name="player">The member who said yes.</param>
    /// <param name="update">Their ask.</param>
    /// <returns>Whether it was answered.</returns>
    private bool AgreeWithoutPlaying(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetAskWish(update) is not { Kind: WishConfirmFleaGame } asked || !IsPinGameWish(asked.Wish) ||
            CanPlayPinGameNow()) {
            return false;
        }

        SendConfirmAnswer(player.Id, update.Key, true, YesWithoutPlaying);
        Chat(Lang.Pick(
            $"{player.Username} wants to start the pin contest. It can't be started on your side right now, so they " +
            "don't wait for you.",
            $"{player.Username} 想开始扔飞镖的比赛。你这边现在开不了，所以不用等你。"
        ));
        Logger.Info(
            $"Agreed to the pin contest of {player.Username} without playing, since it offers the local player no round"
        );
        return true;
    }

    /// <summary>
    /// Whether the yes that waits for the answer with a key is the start of a game that each player plays on their own.
    /// </summary>
    private bool IsFleaGameHold(ulong key) {
        return _wishConfirm is { } held && held.Key == key &&
               held.WishKey.StartsWith(WishConfirmFleaGame + "\n", StringComparison.Ordinal);
    }
}
