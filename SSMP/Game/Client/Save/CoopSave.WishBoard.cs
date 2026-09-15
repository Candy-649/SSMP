using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Wish boards in a checked two-player save. Turning in wishes at a board and donating at it work like key dialogue about
/// wishes (see CoopSave.WishTalk): they need the partner close by, and the partner pays their own copy of what the board
/// takes and gets the reward too. Without the partner close by, the board still opens to look at and accept wishes, and
/// its wishes that are ready to turn in stay on it. Nobody else uses a board while a player turns in or donates there.
/// </summary>
internal partial class CoopSave {
    private static readonly FieldInfo? BoardHandInFsmField =
        typeof(QuestBoardInteractable).GetField("handInSequenceFsm", InstanceFlags);

    private static readonly FieldInfo? BoardYesNoQuestField =
        typeof(QuestItemBoard).GetField("yesNoQuest", InstanceFlags);

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
    /// Whether only the local copies count for whether a wish can be completed, to tell what only the partner lacks.
    /// </summary>
    private bool _localCopiesOnly;

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
    }

    /// <summary>
    /// Hook for QuestBoardInteractable.OnStartDialogue: the use of a board is recorded like dialogue about wishes, and its
    /// wishes that are ready to turn in stay on it while the partner isn't close by.
    /// </summary>
    private void OnBoardStartDialogue(Action<QuestBoardInteractable> orig, QuestBoardInteractable self) {
        var holdTurnIns = false;
        try {
            if (_everChecked && GetCurrentMarker() is { } marker) {
                holdTurnIns = StartBoardTalk(self, marker);
            }
        } catch (Exception e) {
            LogWishTalkError(e);
        }

        if (!holdTurnIns) {
            orig(self);
            return;
        }

        // Without wishes to turn in, the board opens its list instead
        _boardTurnInBlockDepth++;
        try {
            orig(self);
        } finally {
            _boardTurnInBlockDepth--;
        }
    }

    /// <summary>
    /// Starts recording the use of a board. A board with wishes that are ready to turn in is used like key dialogue,
    /// which needs the partner close by.
    /// </summary>
    /// <returns>Whether the wishes that are ready to turn in stay on the board, because the partner isn't close by.</returns>
    private bool StartBoardTalk(QuestBoardInteractable board, CoopSaveMarker marker) {
        var fsms = GetBoardFsms(board);
        EndWishTalk();

        var anyReady = false;
        foreach (var quest in GetBoardQuests(board)) {
            if (quest.GetIsReadyToTurnIn(true)) {
                anyReady = true;
                break;
            }
        }

        if (!anyReady) {
            NoticeBoardMissingCopies(board);
            _wishTalk = new WishTalk(board, fsms, false);
            return false;
        }

        var partner = GetCheckedPartner();
        var absence = GetBoardAbsence(partner, marker, "turn in wishes at this board");
        if (partner == null || absence != null) {
            Chat($"{absence} Until then, the board opens without turning them in.");
            if (partner != null) {
                Send(CreateWishTalkUpdate(
                    partner.Id, board.gameObject.scene.name, ScenePath.Get(board.transform), WishTalkRefused
                ));
            }

            _wishTalk = new WishTalk(board, fsms, false);
            Logger.Info($"Wishes stay on the board '{board.name}', because the partner isn't close by");
            return true;
        }

        var talk = new WishTalk(board, fsms, true);
        _wishTalk = talk;
        Send(CreateWishTalkUpdate(partner.Id, talk.Scene, talk.Path, WishTalkStarted));
        Logger.Info($"Wishes are turned in at the board '{board.name}' with {partner.Username} close by");
        return false;
    }

    /// <summary>
    /// Tells the local player about a wish on a board that they could turn in there with their own copy, but that the
    /// partner lacks a full copy for.
    /// </summary>
    private void NoticeBoardMissingCopies(QuestBoardInteractable board) {
        if (_checkedWith == null) {
            return;
        }

        foreach (var quest in GetBoardQuests(board)) {
            var name = quest.name;
            if ((_nextMissingCopyNotices.TryGetValue(name, out var next) && Time.unscaledTime < next) ||
                !WithLocalCopiesOnly(() => quest.GetIsReadyToTurnIn(true))) {
                continue;
            }

            _nextMissingCopyNotices[name] = Time.unscaledTime + MissingCopyNoticeInterval;
            Chat(
                $"{GetPartnerName()} doesn't have a full copy of what a wish on this board takes yet. Both of you pay " +
                "one to turn it in here."
            );
            return;
        }
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
    /// Hook for <see cref="FullQuestBase.GetIsReadyToTurnIn"/>: while a board opens without the partner close by, no wish
    /// is ready to turn in at a board.
    /// </summary>
    private bool OnGetIsReadyToTurnIn(Func<FullQuestBase, bool, bool> orig, FullQuestBase self, bool atQuestBoard) {
        return (!atQuestBoard || _boardTurnInBlockDepth == 0) && orig(self, atQuestBoard);
    }

    /// <summary>
    /// Hook for QuestItemBoard.SubmitQuestSelection: a donation that the local player could pay but the partner can't
    /// shows as not enough, so the local player hears why.
    /// </summary>
    private void OnBoardSubmitSelection(
        Action<QuestItemBoard, BasicQuestBase> orig,
        QuestItemBoard self,
        BasicQuestBase quest
    ) {
        orig(self, quest);
        try {
            if (_checkedWith == null || quest is not FullQuestBase { IsDonateType: true } donation || donation == null ||
                BoardYesNoQuestField?.GetValue(self) as FullQuestBase != donation || donation.CanComplete ||
                !WithLocalCopiesOnly(() => donation.CanComplete)) {
                return;
            }

            Chat($"{GetPartnerName()} doesn't have enough to donate too. Both of you pay the donation.");
        } catch (Exception e) {
            LogWishTalkError(e);
        }
    }

    /// <summary>
    /// Hook for QuestItemBoard.AcceptDonation: a donation needs the partner close by and able to pay too, since both
    /// players pay it. Otherwise the board goes back to its list.
    /// </summary>
    private void OnBoardAcceptDonation(Action<QuestItemBoard> orig, QuestItemBoard self) {
        try {
            if (_everChecked && GetCurrentMarker() is { } marker &&
                BoardYesNoQuestField?.GetValue(self) is FullQuestBase quest && quest != null &&
                !TryStartDonation(quest, marker)) {
                self.DeclineDonation();
                return;
            }
        } catch (Exception e) {
            LogWishTalkError(e);
        }

        orig(self);
    }

    /// <summary>
    /// Checks that a donation at the board that the local player uses can go through, and keeps the board for the local
    /// player until it did.
    /// </summary>
    /// <returns>Whether the donation may go through.</returns>
    private bool TryStartDonation(FullQuestBase quest, CoopSaveMarker marker) {
        var partner = GetCheckedPartner();
        var absence = GetBoardAbsence(partner, marker, "donate, since both of you pay");
        if (partner == null || absence != null) {
            Chat(absence!);
            if (partner != null && _wishTalk is { } refusedTalk) {
                Send(CreateWishTalkUpdate(partner.Id, refusedTalk.Scene, refusedTalk.Path, WishTalkRefused));
            }

            return false;
        }

        // The board doesn't let the local player pay what they lack, but the money of the partner can change meanwhile
        if (!quest.CanComplete) {
            Chat($"{partner.Username} doesn't have enough to donate too. Both of you pay the donation.");
            return false;
        }

        if (_wishTalk is { Npc: QuestBoardInteractable, IsKey: false } talk) {
            talk.IsKey = true;
            Send(CreateWishTalkUpdate(partner.Id, talk.Scene, talk.Path, WishTalkStarted));
        }

        Logger.Info($"Donation '{quest.name}' goes through with {partner.Username} close by");
        return true;
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
            return $"{marker.PartnerName} needs to be here too to {use}.";
        }

        if (GetWishTalkAbsence(partner) == null) {
            return null;
        }

        return partner.IsInLocalScene && partner.PlayerObject != null
            ? $"{partner.Username} needs to come closer to {use}."
            : $"{partner.Username} needs to be here too to {use}.";
    }

    /// <summary>
    /// Checks something about wishes with only the local copies counting for whether a wish can be completed.
    /// </summary>
    private bool WithLocalCopiesOnly(Func<bool> check) {
        var outer = _localCopiesOnly;
        _localCopiesOnly = true;
        try {
            return check();
        } finally {
            _localCopiesOnly = outer;
        }
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
