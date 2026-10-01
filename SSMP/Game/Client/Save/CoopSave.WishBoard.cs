using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using SSMP.Util;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Wish boards in a checked two-player save. Turning in wishes at a board and donating at it work like key dialogue about
/// wishes (see CoopSave.WishTalk): they need the partner close by, and the partner pays their own copy of what the board
/// takes and gets the reward too. Whether the partner has that copy is asked right then (see CoopSave.WishCopyCheck), and
/// a wish they are short for stays on the board. Without the partner close by, the board still opens to look at and
/// accept wishes, and its wishes that are ready to turn in stay on it. Nobody else uses a board while a player turns in
/// or donates there.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How long, in seconds, until opening a board without the partner tells both players again why its wishes stay on
    /// it.
    /// </summary>
    private const float BoardHoldNoticeInterval = 30f;

    private static readonly FieldInfo? BoardHandInFsmField =
        typeof(QuestBoardInteractable).GetField("handInSequenceFsm", InstanceFlags);

    private static readonly FieldInfo? BoardItemBoardField =
        typeof(QuestBoardInteractable).GetField("questBoard", InstanceFlags);

    private static readonly FieldInfo? BoardQueuedCompletionsField =
        typeof(QuestBoardInteractable).GetField("queuedCompletions", InstanceFlags);

    private static readonly FieldInfo? BoardDonateQuestField =
        typeof(QuestBoardInteractable).GetField("donateQuest", InstanceFlags);

    private static readonly FieldInfo? BoardYesNoQuestField =
        typeof(QuestItemBoard).GetField("yesNoQuest", InstanceFlags);

    private static readonly MethodInfo? BoardCloseMethod =
        typeof(QuestItemBoard).GetMethod("CloseBoard", InstanceFlags, null, Type.EmptyTypes, null);

    private static readonly MethodInfo? BoardQuestActionedMethod =
        typeof(QuestItemBoard).GetMethod("QuestActioned", InstanceFlags, null, Type.EmptyTypes, null);

    private static readonly FieldInfo? BoardFadeRoutineField =
        typeof(QuestItemBoard).GetField("fadeStateRoutine", InstanceFlags);

    private static readonly FieldInfo? BoardPaneField = typeof(QuestItemBoard).GetField("pane", InstanceFlags);

    /// <summary>
    /// The board that asks the local player whether to donate right now. The partner can complete the wish that it asks
    /// about meanwhile.
    /// </summary>
    private QuestItemBoard? _donationPrompt;

    /// <summary>
    /// How many openings of boards without the partner close by run right now, during which no wish is ready to turn in
    /// at a board.
    /// </summary>
    private int _boardTurnInBlockDepth;

    /// <summary>
    /// How many completions of wishes by a board run right now, whose rewards the board gives itself.
    /// </summary>
    private int _boardCompletionDepth;

    /// <summary>
    /// When opening a board without the partner tells both players again why its wishes stay on it.
    /// </summary>
    private float _nextBoardHoldNoticeTime;

    /// <summary>
    /// Registers the hooks for wish boards.
    /// </summary>
    private void RegisterWishBoardHooks() {
        AddWishTalkHook(
            typeof(QuestBoardInteractable).GetMethod(
                "OnStartDialogue", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null
            ),
            new Action<Action<QuestBoardInteractable>, QuestBoardInteractable>(OnBoardStartDialogue)
        );
        AddWishTalkHook(
            typeof(QuestBoardInteractable).GetMethod(
                "ProcessQueuedCompletions", InstanceFlags, null, Type.EmptyTypes, null
            ),
            new Action<Action<QuestBoardInteractable>, QuestBoardInteractable>(OnBoardProcessCompletions)
        );
        AddWishTalkHook(
            typeof(FullQuestBase).GetMethod("GetIsReadyToTurnIn", InstanceFlags, null, [typeof(bool)], null),
            new Func<Func<FullQuestBase, bool, bool>, FullQuestBase, bool, bool>(OnGetIsReadyToTurnIn)
        );
        AddWishTalkHook(
            typeof(QuestItemBoard).GetMethod(
                "SubmitQuestSelection", InstanceFlags, null, [typeof(BasicQuestBase)], null
            ),
            new Action<Action<QuestItemBoard, BasicQuestBase>, QuestItemBoard, BasicQuestBase>(OnBoardSubmitSelection)
        );
        AddWishTalkHook(
            typeof(QuestItemBoard).GetMethod("AcceptDonation", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<QuestItemBoard>, QuestItemBoard>(OnBoardAcceptDonation)
        );
        AddWishTalkHook(
            typeof(QuestItemBoard).GetMethod("DeclineDonation", InstanceFlags, null, Type.EmptyTypes, null),
            new Action<Action<QuestItemBoard>, QuestItemBoard>(OnBoardDeclineDonation)
        );
    }

    /// <summary>
    /// Hook for QuestBoardInteractable.OnStartDialogue: the use of a board is recorded like dialogue about wishes, and its
    /// wishes that are ready to turn in stay on it while the partner isn't close by or uses the board. Before it hands in
    /// wishes that take something from both players, it waits for the partner's game to say what they carry, and the
    /// wishes they are short for stay on it.
    /// </summary>
    private void OnBoardStartDialogue(Action<QuestBoardInteractable> orig, QuestBoardInteractable self) {
        var resumed = _resumedBoardOpen;
        var holdTurnIns = false;
        HashSet<string>? leftOut = null;
        try {
            if (_everChecked && GetCurrentMarker() is { } marker) {
                // Runs again from UpdateWishCopyChecks once the partner answered or the wait ran out
                if (resumed == null && AskBeforeBoardTurnIns(self, marker, () => OnBoardStartDialogue(orig, self))) {
                    return;
                }

                holdTurnIns = StartBoardTalk(self, marker, resumed, out leftOut);
            } else if (resumed != null) {
                // The save stopped being shared while the board waited, so nothing is handed in for both
                holdTurnIns = true;
            }
        } catch (Exception e) {
            LogWishTalkError(e);

            // Handing wishes in without having checked the partner's copy would give them the wish for nothing
            holdTurnIns = _everChecked;
            leftOut = null;
        }

        if (!holdTurnIns && leftOut == null) {
            orig(self);
            return;
        }

        // Without wishes to turn in, the board opens its list instead
        if (holdTurnIns) {
            _boardTurnInBlockDepth++;
        }

        _boardTurnInsLeftOut = leftOut;
        try {
            orig(self);
        } finally {
            _boardTurnInsLeftOut = null;
            if (holdTurnIns) {
                _boardTurnInBlockDepth--;
            }
        }
    }

    /// <summary>
    /// Starts recording the use of a board. A board with wishes that are ready to turn in is used like key dialogue,
    /// which needs the partner close by. Once the partner's game said what they carry, the wishes they are short for
    /// stay on the board.
    /// </summary>
    /// <param name="board">The board.</param>
    /// <param name="marker">The pairing of the loaded save.</param>
    /// <param name="resumed">The opening that waited for the partner's answer, or null if it didn't wait.</param>
    /// <param name="leftOut">The wishes that stay on the board while the others are turned in, or null.</param>
    /// <returns>Whether all the wishes that are ready to turn in stay on the board.</returns>
    private bool StartBoardTalk(
        QuestBoardInteractable board,
        CoopSaveMarker marker,
        BoardOpenHold? resumed,
        out HashSet<string>? leftOut
    ) {
        leftOut = null;
        var fsms = GetBoardFsms(board);
        EndWishTalk();

        var ready = new List<FullQuestBase>();
        foreach (var quest in GetBoardQuests(board)) {
            if (quest.GetIsReadyToTurnIn(true)) {
                ready.Add(quest);
            }
        }

        if (ready.Count == 0) {
            _wishTalk = new WishTalk(board, fsms, false);
            return false;
        }

        // The partner started to turn in or donate here while the local hero walked up to the board
        if (_partnerTalk?.Npc is { } partnerNpc && partnerNpc == board) {
            Chat(Lang.Pick(
                $"{GetPartnerName()} is using this board right now, so it opens without turning in wishes.",
                $"{GetPartnerName()} 正在用这块板子，所以这次只打开，不交愿望。"
            ));
            _wishTalk = new WishTalk(board, fsms, false);
            return true;
        }

        var partner = GetCheckedPartner();
        var absence = GetBoardAbsence(
            partner,
            marker,
            Lang.Pick("turn in wishes at this board", "在这块板子上交愿望")
        );
        if (partner == null || absence != null) {
            // Looking at the board again soon doesn't repeat why its wishes stay
            if (Time.unscaledTime >= _nextBoardHoldNoticeTime) {
                _nextBoardHoldNoticeTime = Time.unscaledTime + BoardHoldNoticeInterval;
                Chat(Lang.Pick(
                    $"{absence} Until then, the board opens without turning them in.",
                    $"{absence} 在那之前，这块板子只会打开，不交愿望。"
                ));
                if (partner != null) {
                    Send(CreateWishTalkUpdate(
                        partner.Id, board.gameObject.scene.name, ScenePath.Get(board.transform), WishTalkRefused
                    ));
                }
            }

            _wishTalk = new WishTalk(board, fsms, false);
            Logger.Info($"Wishes stay on the board '{board.name}', because the partner isn't close by");
            return true;
        }

        if (resumed != null) {
            leftOut = GetBoardTurnInsLeftOut(ready, resumed);
            if (leftOut != null && leftOut.Count >= ready.Count) {
                // Nothing is left to turn in, so the board opens its list
                leftOut = null;
                _wishTalk = new WishTalk(board, fsms, false);
                return true;
            }
        }

        var talk = new WishTalk(board, fsms, true);
        _wishTalk = talk;
        Send(CreateWishTalkUpdate(partner.Id, talk.Scene, talk.Path, WishTalkStarted));
        Logger.Info($"Wishes are turned in at the board '{board.name}' with {partner.Username} close by");
        return false;
    }

    /// <summary>
    /// Hook for QuestBoardInteractable.ProcessQueuedCompletions, which completes the next wish that the board took in and
    /// gives its reward itself.
    /// </summary>
    private void OnBoardProcessCompletions(Action<QuestBoardInteractable> orig, QuestBoardInteractable self) {
        _boardCompletionDepth++;

        // The reward is recorded from the wish, so what giving it makes isn't recorded again
        _talkGainDepth++;
        try {
            orig(self);
        } finally {
            _talkGainDepth--;
            _boardCompletionDepth--;
        }
    }

    /// <summary>
    /// Records the reward of a wish that a board just completed, which the board gives once the completion was shown,
    /// outside of any FSM, for the change of that wish.
    /// </summary>
    private void RecordBoardReward(FullQuestBase quest) {
        if (_wishTalk is not { Npc: QuestBoardInteractable } talk || talk.Changes.Count == 0 ||
            talk.Changes[talk.Changes.Count - 1] != quest.name) {
            return;
        }

        var reward = quest.RewardItem;
        var count = quest.RewardCount;
        if (reward == null || reward is BasicQuestBase || count <= 0) {
            return;
        }

        talk.AddItem(GetItemChangeKey(GetItemChange, reward), count, false);
    }

    /// <summary>
    /// Whether the board that the local player uses takes in a wish right now, from queueing it or accepting its
    /// donation until it is completed. The local player pays for it and gets its reward there.
    /// </summary>
    private bool IsTurningInAtBoard(string name) {
        if (_wishTalk?.Npc is not QuestBoardInteractable board || board == null) {
            return false;
        }

        if (BoardDonateQuestField?.GetValue(board) is FullQuestBase donation && donation != null &&
            donation.name == name) {
            return true;
        }

        if (BoardQueuedCompletionsField?.GetValue(board) is IEnumerable queued) {
            foreach (var entry in queued) {
                if (entry is FullQuestBase quest && quest != null && quest.name == name) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Hook for <see cref="FullQuestBase.GetIsReadyToTurnIn"/>: while a board opens without turning in wishes, no wish is
    /// ready to turn in at a board, and while it opens without the wishes that the partner is short for, those aren't.
    /// </summary>
    private bool OnGetIsReadyToTurnIn(Func<FullQuestBase, bool, bool> orig, FullQuestBase self, bool atQuestBoard) {
        if (atQuestBoard && (_boardTurnInBlockDepth > 0 ||
                             (_boardTurnInsLeftOut != null && self != null && _boardTurnInsLeftOut.Contains(self.name)))) {
            return false;
        }

        return orig(self!, atQuestBoard);
    }

    /// <summary>
    /// Hook for QuestItemBoard.SubmitQuestSelection: a wish that the partner turned in meanwhile is taken off the list
    /// instead of asking the local player to pay for it, and a question whether to donate is watched, so that it is taken
    /// down once the partner completes its wish. Whether the partner can pay a donation too is asked once the local
    /// player says yes (see CoopSave.WishCopyCheck).
    /// </summary>
    private void OnBoardSubmitSelection(
        Action<QuestItemBoard, BasicQuestBase> orig,
        QuestItemBoard self,
        BasicQuestBase quest
    ) {
        try {
            // It still shows, since the list is only drawn again once something on it was taken, and picking it asked
            // the local player to pay for a wish that is done
            if (_checkedWith != null && quest is FullQuestBase { IsCompleted: true } done && done != null &&
                BoardQuestActionedMethod != null && BoardFadeRoutineField?.GetValue(self) == null) {
                Chat(Lang.Pick(
                    $"{GetPartnerName()} already turned in this wish with you.",
                    $"这个愿望 {GetPartnerName()} 已经和你一起交过了。"
                ));
                BoardQuestActionedMethod.Invoke(self, null);
                return;
            }
        } catch (Exception e) {
            LogWishTalkError(e);
        }

        orig(self, quest);
        try {
            if (_checkedWith != null && quest is FullQuestBase { IsDonateType: true } donation && donation != null &&
                BoardYesNoQuestField?.GetValue(self) as FullQuestBase == donation) {
                _donationPrompt = self;
            }
        } catch (Exception e) {
            LogWishTalkError(e);
        }
    }

    /// <summary>
    /// Hook for QuestItemBoard.AcceptDonation: a donation needs the partner close by and able to pay too, since both
    /// players pay it, and not using the board themselves. Whether they can pay is asked right then: the yes waits for
    /// their game's answer and runs again once it came (see CoopSave.WishCopyCheck). Otherwise the board goes back to its
    /// list. A wish that the partner completed while the board asked is never paid again.
    /// </summary>
    private void OnBoardAcceptDonation(Action<QuestItemBoard> orig, QuestItemBoard self) {
        _donationPrompt = null;
        var resumed = _resumedDonation;
        try {
            if (BoardYesNoQuestField?.GetValue(self) is FullQuestBase { IsCompleted: true } done && done != null) {
                self.DeclineDonation();
                return;
            }

            // Pressing yes again while the partner's game is being asked does nothing more
            if (resumed == null && _donationHold is { } waiting) {
                if (waiting.ItemBoard == self) {
                    return;
                }

                _donationHold = null;
            }

            if (_everChecked && GetCurrentMarker() is { } marker &&
                BoardYesNoQuestField?.GetValue(self) is FullQuestBase quest && quest != null) {
                if (!CanDonateTogether(self, quest, marker, out var board, out var partner)) {
                    self.DeclineDonation();
                    return;
                }

                if (board != null && partner != null) {
                    if (NeedsPartnerCopy(quest)) {
                        if (resumed == null) {
                            // Runs again from UpdateWishCopyChecks once the partner answered or the wait ran out
                            AskBeforeDonation(self, quest, partner, () => OnBoardAcceptDonation(orig, self));
                            return;
                        }

                        if (!CanPartnerPayDonation(quest, resumed)) {
                            self.DeclineDonation();
                            return;
                        }
                    }

                    StartDonationTalk(board, partner, quest);
                }
            } else if (resumed != null) {
                // The save stopped being shared while the donation waited, so it isn't paid for both
                self.DeclineDonation();
                return;
            }
        } catch (Exception e) {
            LogWishTalkError(e);

            // A donation that goes through without having checked the partner's copy would give them the wish for
            // nothing
            if (_everChecked) {
                self.DeclineDonation();
                return;
            }
        }

        orig(self);
    }

    /// <summary>
    /// Hook for QuestItemBoard.DeclineDonation: the board goes back to its list, and closes when the list has nothing
    /// left. The game only ever goes back, because on its own the list keeps the wish that it asks about; the partner
    /// can take the last wishes off it meanwhile, by turning them in or accepting them, which used to leave the local
    /// player in front of a question with no way out. Answering no while the partner's game is asked about a yes takes
    /// that yes back.
    /// </summary>
    private void OnBoardDeclineDonation(Action<QuestItemBoard> orig, QuestItemBoard self) {
        if (_donationPrompt == self) {
            _donationPrompt = null;
        }

        if (_donationHold is { } waiting && waiting.ItemBoard == self) {
            _donationHold = null;
            Logger.Info($"The donation '{waiting.Quest.name}' was taken back while the partner's game was asked");
        }

        var closing = false;
        try {
            if (self.AvailableQuestsCount <= 0 && BoardCloseMethod != null) {
                closing = true;
                if (BoardYesNoQuestField?.GetValue(self) is FullQuestBase quest && quest != null) {
                    self.HideCurrencyCounters(quest);
                }

                BoardYesNoQuestField?.SetValue(self, null);

                // A change of what the board shows that closing cuts off never finishes, and the board never took input
                // again the next time it opened
                if (BoardFadeRoutineField?.GetValue(self) is Coroutine fade) {
                    self.StopCoroutine(fade);
                    BoardFadeRoutineField.SetValue(self, null);
                }

                BoardCloseMethod.Invoke(self, null);
                return;
            }
        } catch (Exception e) {
            LogWishTalkError(e);

            // The game's own way back would hide the counters of a question that isn't there any more, and throw
            if (closing) {
                return;
            }
        }

        orig(self);
    }

    /// <summary>
    /// Takes down the question of a board whether to donate once the partner completed the wish that it asks about, as
    /// the chat about their turn-in says, so the local player isn't asked to pay for a wish that is done.
    /// </summary>
    private void UpdateDonationPrompt() {
        if (_donationPrompt is not { } board) {
            return;
        }

        // Not by the state of the board: it only switches to the question once the list faded out
        if (board == null || BoardYesNoQuestField?.GetValue(board) is not FullQuestBase quest || quest == null) {
            _donationPrompt = null;
            return;
        }

        // Only once the question is up: the board takes nothing while it fades from one to the other, and going back or
        // closing in the middle of that left it half way
        if (quest.IsCompleted && BoardFadeRoutineField?.GetValue(board) == null) {
            Logger.Info($"The donation '{quest.name}' that the board asked about was completed, so the board stops asking");
            board.DeclineDonation();
        }
    }

    /// <summary>
    /// Checks that a donation at a board can go through with the partner: close by, and not using the board
    /// themselves. The local player hears why not.
    /// </summary>
    /// <param name="itemBoard">The list of the board that asked.</param>
    /// <param name="quest">The wish that the donation completes.</param>
    /// <param name="marker">The pairing of the loaded save.</param>
    /// <param name="board">The board, or null if it can't be found, in which case the partner doesn't pay.</param>
    /// <param name="partner">The partner who pays too, or null if they don't.</param>
    /// <returns>Whether the donation may go through.</returns>
    private bool CanDonateTogether(
        QuestItemBoard itemBoard,
        FullQuestBase quest,
        CoopSaveMarker marker,
        out QuestBoardInteractable? board,
        out ClientPlayerData? partner
    ) {
        partner = null;
        board = FindBoard(itemBoard);
        if (board == null) {
            Logger.Warn($"Could not find the board of the donation '{quest.name}', so the partner doesn't pay it");
            return true;
        }

        if (_partnerTalk?.Npc is { } partnerNpc && partnerNpc == board) {
            Chat(Lang.Pick($"{GetPartnerName()} is using this board right now.", $"{GetPartnerName()} 正在用这块板子。"));
            return false;
        }

        partner = GetCheckedPartner();
        var absence = GetBoardAbsence(
            partner,
            marker,
            Lang.Pick("donate, since both of you pay", "一起捐赠（两个人都要出）")
        );
        if (partner == null || absence != null) {
            Chat(absence!);
            if (partner != null) {
                Send(CreateWishTalkUpdate(
                    partner.Id, board.gameObject.scene.name, ScenePath.Get(board.transform), WishTalkRefused
                ));
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Records a donation that goes through with the use of its board, which the partner can't use until it went
    /// through.
    /// </summary>
    private void StartDonationTalk(QuestBoardInteractable board, ClientPlayerData partner, FullQuestBase quest) {
        // The use of the board may have stopped recording while its list stayed open for long
        var talk = _wishTalk;
        if (talk == null || talk.Npc != board) {
            EndWishTalk();
            talk = new WishTalk(board, GetBoardFsms(board), true);
            _wishTalk = talk;
            Send(CreateWishTalkUpdate(partner.Id, talk.Scene, talk.Path, WishTalkStarted));
        } else if (!talk.IsKey) {
            talk.IsKey = true;
            Send(CreateWishTalkUpdate(partner.Id, talk.Scene, talk.Path, WishTalkStarted));
        }

        Logger.Info($"Donation '{quest.name}' goes through with {partner.Username} close by");
    }

    /// <summary>
    /// Finds the board that shows a board list, which isn't a child of the board.
    /// </summary>
    private QuestBoardInteractable? FindBoard(QuestItemBoard itemBoard) {
        if (_wishTalk?.Npc is QuestBoardInteractable talkBoard && talkBoard != null &&
            (BoardItemBoardField?.GetValue(talkBoard) as QuestItemBoard) == itemBoard) {
            return talkBoard;
        }

        foreach (var board in UnityEngine.Object.FindObjectsByType<QuestBoardInteractable>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None
                 )) {
            if (board != null && (BoardItemBoardField?.GetValue(board) as QuestItemBoard) == itemBoard) {
                return board;
            }
        }

        return null;
    }

    /// <summary>
    /// The partner that the save was checked with, or null.
    /// </summary>
    private ClientPlayerData? GetCheckedPartner() {
        return _checkedWith is { } partnerId && _playerData.TryGetValue(partnerId, out var partner) ? partner : null;
    }

    /// <summary>
    /// Why the partner can't join a use of a board now, for the message to the local player, or null if they can. Never
    /// null without a checked partner.
    /// </summary>
    private static string? GetBoardAbsence(ClientPlayerData? partner, CoopSaveMarker marker, string use) {
        if (partner == null) {
            return Lang.Pick(
                $"{marker.PartnerName} needs to be here too to {use}.",
                $"{marker.PartnerName} 也要在场，才能{use}。"
            );
        }

        if (GetWishTalkAbsence(partner) == null) {
            return null;
        }

        return partner.IsInLocalScene && partner.PlayerObject != null
            ? Lang.Pick(
                $"{partner.Username} needs to come closer to {use}.",
                $"{partner.Username} 要再靠近一点，才能{use}。"
            )
            : Lang.Pick(
                $"{partner.Username} needs to be here too to {use}.",
                $"{partner.Username} 也要在场，才能{use}。"
            );
    }

    /// <summary>
    /// Whether the list of a board is open, or the question whether to donate that it shows. A list whose panel can't
    /// be read counts as open.
    /// </summary>
    private static bool IsBoardListOpen(QuestItemBoard itemBoard) {
        return BoardPaneField?.GetValue(itemBoard) is not InventoryPaneBase pane || pane == null || pane.IsPaneActive;
    }

    /// <summary>
    /// The full wishes that a board lists.
    /// </summary>
    private static IEnumerable<FullQuestBase> GetBoardQuests(QuestBoardInteractable board) {
        if (board.Quests == null) {
            yield break;
        }

        foreach (var group in board.Quests) {
            if (group == null) {
                continue;
            }

            foreach (var quest in group.GetQuests()) {
                if (quest is FullQuestBase fullQuest && fullQuest != null) {
                    yield return fullQuest;
                }
            }
        }
    }

    /// <summary>
    /// The FSMs that run what a board does when wishes are turned in or donated, like its hand-in sequence.
    /// </summary>
    private static HashSet<PlayMakerFSM> GetBoardFsms(QuestBoardInteractable board) {
        var fsms = new HashSet<PlayMakerFSM>();
        if (BoardHandInFsmField?.GetValue(board) is PlayMakerFSM handIn && handIn != null) {
            fsms.Add(handIn);
        }

        foreach (var fsm in board.GetComponents<PlayMakerFSM>()) {
            if (fsm != null) {
                fsms.Add(fsm);
            }
        }

        return fsms;
    }
}
