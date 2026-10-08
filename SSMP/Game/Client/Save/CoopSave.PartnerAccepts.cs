using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Wishes that only the game of the partner accepted, in a checked two-player save, and the dialogue whose end begins
/// one.
///
/// A wish that the partner accepts is copied into the local wish log at once (CoopSave.Wishes). Most dialogue looks
/// at the wish log only to choose what is said, but the dialogue after the fight with the two dancers begins its wish
/// at its end and opens the room in the same step: the gates of the fight and the ways down. It asks whether the wish
/// is accepted to know whether that end has been played already, which in a game of one only happens once it opened
/// the room. Both players listened, each in their own game; the partner's dialogue ended first and its accept came in,
/// so the dialogue of the local player took the way of dialogue heard again, opened nothing, and left them shut in the
/// room (USER 10-08).
///
/// A wish that only the partner's game accepted now counts as not accepted for a check whose "not accepted" leads
/// straight into the step that begins that same wish: the local game plays that step as the partner's did, with its
/// own prompt, and opens its own room. Once the local game has begun the wish itself, the check answers as the game
/// would. Of all the dialogue in the game, only the one after the dancers has such a check.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The names of the wishes that the partner's game accepted and that were copied into the local wish log, and that
    /// the local game hasn't begun by itself since.
    /// </summary>
    private readonly HashSet<string> _wishesAcceptedByPartner = new(StringComparer.Ordinal);

    /// <summary>
    /// Notes a wish that the partner's game accepted, which the local wish log had not.
    /// </summary>
    private void NoteWishAcceptedByPartner(string name) {
        _wishesAcceptedByPartner.Add(name);
    }

    /// <summary>
    /// Forgets that a wish was accepted only by the partner's game, once the local game began it itself.
    /// </summary>
    private void NoteWishBegunHere(string name) {
        _wishesAcceptedByPartner.Remove(name);
    }

    /// <summary>
    /// Forgets the wishes that only the partner's game accepted, for another save.
    /// </summary>
    private void ResetPartnerAccepts() {
        _wishesAcceptedByPartner.Clear();
    }

    /// <summary>
    /// Hook for the check of an FSM whether a wish is accepted: one whose answer "not accepted" leads straight into
    /// beginning the wish answers that for a wish that only the partner's game accepted.
    /// </summary>
    private void OnCheckWishState(
        Action<QuestPlaymakerActions.CheckQuestState, FullQuestBase> orig,
        QuestPlaymakerActions.CheckQuestState self,
        FullQuestBase quest
    ) {
        try {
            if (quest != null && _wishesAcceptedByPartner.Contains(quest.name) && !quest.IsCompleted &&
                LeadsToBeginning(self, quest)) {
                Logger.Info(
                    $"'{quest.name}' was accepted only by the partner's game, so '{self.State?.Name}' of " +
                    $"'{self.Fsm?.GameObjectName}' goes on as if it wasn't, into the step that begins it here too"
                );
                self.Fsm!.Event(self.NotTrackedEvent);
                return;
            }
        } catch (Exception e) {
            LogWishTalkError(e);
        }

        orig(self, quest!);
    }

    /// <summary>
    /// Whether the answer "not accepted" of a check leads straight into a state that begins the same wish.
    /// </summary>
    private static bool LeadsToBeginning(QuestPlaymakerActions.CheckQuestState check, FullQuestBase quest) {
        var notTracked = check.NotTrackedEvent;
        if (notTracked == null || string.IsNullOrEmpty(notTracked.Name) || check.State is not { } state ||
            check.Fsm is not { } fsm) {
            return false;
        }

        foreach (var transition in state.Transitions) {
            if (transition.EventName != notTracked.Name) {
                continue;
            }

            return fsm.GetState(transition.ToState) is { } next && next.Actions.Any(action =>
                action is QuestPlaymakerActions.BeginQuest or QuestPlaymakerActions.BeginQuestV2 &&
                ((QuestPlaymakerActions.QuestFsmAction) action).Quest?.Value == quest
            );
        }

        return false;
    }
}
