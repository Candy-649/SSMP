using System;
using System.Collections.Generic;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Asking the partner what they carry at the moment a wish that takes something from both players is handed in. A
/// turn-in takes a full copy from each of them, and whether the partner had theirs used to be read from amounts that
/// their game pushed whenever they changed. One push that never arrived, like one that came while the local game was
/// riding between rooms, which drops what arrives meanwhile, then kept a partner who had plenty "short" until their
/// amount changed again. So nothing about the partner's bag is kept any more: opening a board that would hand such
/// wishes in, or saying yes to a donation, stops for a moment, asks the partner's game, and goes on with its answer.
/// A character's own turn-in needs no question of its own, because the partner says yes at their own prompt, and
/// their game answers no at once, with what it has, when its own copy is short (see CoopSave.WishConfirm).
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How long, in seconds, a turn-in waits for the partner's game to say what it carries. The answer takes one round
    /// trip, so this only runs out when something is wrong. The hero stands at the board meanwhile.
    /// </summary>
    private const float WishCopyCheckTimeout = 5f;

    /// <summary>
    /// The value of <see cref="CoopSaveUpdate.PartCount"/> for the question what the partner carries.
    /// </summary>
    private const ushort WishCopyAsk = 0;

    /// <summary>
    /// The value of <see cref="CoopSaveUpdate.PartCount"/> for the answer.
    /// </summary>
    private const ushort WishCopyAnswer = 1;

    /// <summary>
    /// The index of the target in an answer for a wish that the answering save completed already, whose turn-in takes
    /// nothing from that save.
    /// </summary>
    private const int CopyOfCompletedWish = -1;

    /// <summary>
    /// How many wishes that the partner is short for are named in chat at most, one line each.
    /// </summary>
    private const int MaxShortCopyNotices = 3;

    /// <summary>
    /// The opening of a board that waits for the partner's answer, or null.
    /// </summary>
    private BoardOpenHold? _boardOpenHold;

    /// <summary>
    /// The opening of a board that runs again now that the partner answered or the wait ran out, or null.
    /// </summary>
    private BoardOpenHold? _resumedBoardOpen;

    /// <summary>
    /// The donation that waits for the partner's answer, or null.
    /// </summary>
    private DonationHold? _donationHold;

    /// <summary>
    /// The donation that runs again now that the partner answered or the wait ran out, or null.
    /// </summary>
    private DonationHold? _resumedDonation;

    /// <summary>
    /// The wishes that the board opening right now leaves on it because the partner is short of what they take, or
    /// null.
    /// </summary>
    private HashSet<string>? _boardTurnInsLeftOut;

    /// <summary>
    /// Whether handing a wish in takes something from the partner too, which their game is asked about first. A wish
    /// that the check left completed in only one save is handed in with the local copy alone.
    /// </summary>
    private bool NeedsPartnerCopy(FullQuestBase quest) {
        if (_differentWishNames.Contains(quest.name)) {
            return false;
        }

        foreach (var target in quest.Targets) {
            if (IsTakenFromEach(target)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a target of a wish is something that its turn-in takes from each player, like money or items. A
    /// delivery runs against time for each carrier on their own.
    /// </summary>
    private static bool IsTakenFromEach(FullQuestBase.QuestTarget target) {
        return target.Counter != null && target.Count > 0 && target.Counter is not DeliveryQuestItem &&
               target.Counter.CanConsume;
    }

    /// <summary>
    /// Finds the first thing that a wish takes from each player which a copy is short of.
    /// </summary>
    /// <param name="quest">The wish.</param>
    /// <param name="amounts">How much the copy has of each target of the wish, or null when that isn't known.</param>
    /// <param name="amount">How much the copy has of it, or -1 when that isn't known.</param>
    /// <param name="needed">How much the wish takes of it.</param>
    /// <param name="counter">What the wish counts it with.</param>
    /// <returns>Whether the copy is short of something that the wish takes.</returns>
    private static bool LacksCopy(
        FullQuestBase quest,
        int[]? amounts,
        out int amount,
        out int needed,
        out QuestTargetCounter? counter
    ) {
        var targets = quest.Targets;
        for (var i = 0; i < targets.Count; i++) {
            var target = targets[i];
            if (!IsTakenFromEach(target)) {
                continue;
            }

            amount = amounts != null && i < amounts.Length ? amounts[i] : -1;
            if (amount >= target.Count) {
                continue;
            }

            needed = target.Count;
            counter = target.Counter;
            return true;
        }

        amount = 0;
        needed = 0;
        counter = null;
        return false;
    }

    /// <summary>
    /// Says how short a copy is, for chat: loose rosaries are named as such, because rosaries on strings don't count
    /// until they are broken off, so a player can have plenty and still be short.
    /// </summary>
    /// <param name="who">The player who is short, or null for the local player.</param>
    /// <param name="amount">How much they have, or -1 when their game didn't say.</param>
    /// <param name="needed">How much it takes.</param>
    /// <param name="counter">What the wish counts it with.</param>
    private static string DescribeShortCopy(string? who, int amount, int needed, QuestTargetCounter? counter) {
        if (amount < 0) {
            return Lang.Pick(
                $"{who}'s game didn't say what they carry for this.",
                $"{who} 那边没说身上有多少。"
            );
        }

        var rosaries = counter is QuestTargetCurrency { CurrencyType: CurrencyType.Money };
        if (who == null) {
            return rosaries
                ? Lang.Pick(
                    $"You have only {amount} of the {needed} loose rosaries this takes (rosaries on strings only count " +
                    "once they are broken off).",
                    $"你身上的念珠只有 {amount}/{needed}（念珠串不算，要先拆开）。"
                )
                : Lang.Pick(
                    $"You have only {amount} of the {needed} this takes.",
                    $"你身上只有 {amount}/{needed}。"
                );
        }

        return rosaries
            ? Lang.Pick(
                $"{who} has only {amount} of the {needed} loose rosaries this takes (rosaries on strings only count " +
                "once they are broken off).",
                $"{who} 身上的念珠只有 {amount}/{needed}（念珠串不算，要先拆开）。"
            )
            : Lang.Pick(
                $"{who} has only {amount} of the {needed} this takes.",
                $"{who} 身上只有 {amount}/{needed}。"
            );
    }

    /// <summary>
    /// Adds what the local save carries for a wish to an update: an entry for each target, or one entry that says the
    /// wish is completed here, so its turn-in takes nothing from this save.
    /// </summary>
    private static void AddCopyEntries(CoopSaveUpdate update, FullQuestBase quest, PlayerData playerData) {
        if (quest.IsCompleted) {
            update.WishNames.Add(quest.name);
            update.WishValues.Add(CopyOfCompletedWish);
            update.Amounts.Add(0);
            return;
        }

        var amounts = GetLocalWishProgress(quest, playerData.QuestCompletionData.GetData(quest.name));
        for (var i = 0; i < amounts.Length; i++) {
            update.WishNames.Add(quest.name);
            update.WishValues.Add(i);
            update.Amounts.Add(amounts[i]);
        }
    }

    /// <summary>
    /// Reads what the partner carries for wishes from an update with entries of <see cref="AddCopyEntries"/>.
    /// </summary>
    private static PartnerCopies ReadCopies(CoopSaveUpdate update) {
        var copies = new PartnerCopies();
        for (var i = 0; i < update.WishNames.Count && i < update.WishValues.Count && i < update.Amounts.Count; i++) {
            var name = update.WishNames[i];
            var index = update.WishValues[i];
            if (index == CopyOfCompletedWish) {
                copies.Completed.Add(name);
                continue;
            }

            if (index < 0 || index >= MaxWishTargets) {
                continue;
            }

            if (!copies.Amounts.TryGetValue(name, out var amounts) || amounts.Length <= index) {
                // A target that the answer skipped counts as one whose amount isn't known
                var grown = new int[index + 1];
                for (var j = 0; j < grown.Length; j++) {
                    grown[j] = -1;
                }

                amounts?.CopyTo(grown, 0);
                amounts = grown;
                copies.Amounts[name] = amounts;
            }

            amounts[index] = update.Amounts[i];
        }

        return copies;
    }

    /// <summary>
    /// Asks the partner's game what it carries for wishes.
    /// </summary>
    /// <returns>The key that the answer comes back with.</returns>
    private ulong SendWishCopyAsk(ushort partnerId, List<string> wishes) {
        var bytes = new byte[8];
        Random.NextBytes(bytes);
        var update = new CoopSaveUpdate {
            TargetId = partnerId,
            Kind = CoopSaveUpdateKind.WishCopyCheck,
            PartCount = WishCopyAsk,
            Key = BitConverter.ToUInt64(bytes, 0)
        };
        update.WishNames.AddRange(wishes);
        Send(update);
        return update.Key;
    }

    /// <summary>
    /// The partner asked what the local save carries for wishes, or answered the local game's question.
    /// </summary>
    private void OnWishCopyCheck(ClientPlayerData player, CoopSaveUpdate update) {
        try {
            if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker)) {
                return;
            }

            switch (update.PartCount) {
                case WishCopyAsk:
                    AnswerWishCopyAsk(player, update);
                    break;
                case WishCopyAnswer:
                    TakeWishCopyAnswer(player, update);
                    break;
            }
        } catch (Exception e) {
            LogWishTalkError(e);
        }
    }

    /// <summary>
    /// Tells the partner what the local save carries for the wishes they are handing in, as it is right now.
    /// </summary>
    private void AnswerWishCopyAsk(ClientPlayerData player, CoopSaveUpdate update) {
        if (PlayerData.instance is not { } playerData) {
            return;
        }

        var answer = new CoopSaveUpdate {
            TargetId = player.Id,
            Kind = CoopSaveUpdateKind.WishCopyCheck,
            PartCount = WishCopyAnswer,
            Key = update.Key
        };
        var found = 0;
        foreach (var name in update.WishNames) {
            // A wish that this game can't find is left out, which the partner's game reads as an amount it doesn't know
            if (FindQuest(name) is not { } quest) {
                continue;
            }

            AddCopyEntries(answer, quest, playerData);
            found++;
        }

        Send(answer);
        Logger.Info($"{player.Username} asked what this save carries for {update.WishNames.Count} wishes, told for {found}");
    }

    /// <summary>
    /// Takes the partner's answer for the turn-in that waits for it. An answer that comes after the turn-in gave up
    /// waiting belongs to nothing any more.
    /// </summary>
    private void TakeWishCopyAnswer(ClientPlayerData player, CoopSaveUpdate update) {
        CopyCheckHold? hold = _boardOpenHold is { } open && open.Key == update.Key
            ? open
            : _donationHold is { } donation && donation.Key == update.Key
                ? donation
                : null;
        if (hold == null || hold.Answer != null || hold.PartnerId != player.Id) {
            return;
        }

        hold.Answer = ReadCopies(update);
        Logger.Info(
            $"{player.Username} said what they carry for {hold.Wishes.Count} wishes, " +
            $"{Time.unscaledTime - hold.Started:0.00} s after the question"
        );
    }

    /// <summary>
    /// Goes on with a turn-in that waited for the partner's game once it answered or the wait ran out. Not while the
    /// game is paused, which would open the board under the pause menu.
    /// </summary>
    private void UpdateWishCopyChecks() {
        if (global::GameManager.instance is { } gameManager && gameManager.IsGamePaused()) {
            return;
        }

        if (_boardOpenHold is { IsDue: true } open) {
            _boardOpenHold = null;

            // A board that went away with its room has nothing left to open. Any other one opens, with or without the
            // wishes, because the hero stands in its dialogue until it does
            if (open.Board != null) {
                _resumedBoardOpen = open;
                try {
                    open.Proceed();
                } finally {
                    _resumedBoardOpen = null;
                }
            }
        }

        if (_donationHold is { IsDue: true } donation) {
            _donationHold = null;

            // The board asks about the donation until it is answered, so a question that was answered, taken down or
            // went away with its room meanwhile has nothing left to go on with
            if (donation.ItemBoard != null && IsBoardListOpen(donation.ItemBoard) &&
                BoardYesNoQuestField?.GetValue(donation.ItemBoard) as FullQuestBase == donation.Quest) {
                _resumedDonation = donation;
                try {
                    donation.Proceed();
                } finally {
                    _resumedDonation = null;
                }
            } else {
                Logger.Info($"The donation '{donation.Quest.name}' was no longer asked about when the partner answered");
            }
        }
    }

    /// <summary>
    /// Forgets the turn-ins that wait for the partner, whose board went away with the room.
    /// </summary>
    private void ResetWishCopyChecks() {
        _boardOpenHold = null;
        _donationHold = null;
    }

    /// <summary>
    /// Asks the partner what they carry before a board hands in wishes that take something from both players, which
    /// waits for the answer: the opening of the board runs again once it came (see <see cref="UpdateWishCopyChecks"/>).
    /// Nothing is asked while the partner isn't close by or uses the board, because then the board opens without
    /// handing anything in.
    /// </summary>
    /// <returns>Whether the opening of the board waits for the answer.</returns>
    private bool AskBeforeBoardTurnIns(QuestBoardInteractable board, CoopSaveMarker marker, Action proceed) {
        if (_boardOpenHold != null) {
            Logger.Info("Forgot the opening of a board that still waited for the partner, since another one opens");
            _boardOpenHold = null;
        }

        if (_partnerTalk?.Npc is { } partnerNpc && partnerNpc == board) {
            return false;
        }

        var partner = GetCheckedPartner();
        if (partner == null || GetBoardAbsence(partner, marker, "") != null) {
            return false;
        }

        var wishes = new List<string>();
        foreach (var quest in GetBoardQuests(board)) {
            if (quest.GetIsReadyToTurnIn(true) && NeedsPartnerCopy(quest)) {
                wishes.Add(quest.name);
            }
        }

        if (wishes.Count == 0) {
            return false;
        }

        var key = SendWishCopyAsk(partner.Id, wishes);
        Logger.Info(
            $"The board '{board.name}' waits for {partner.Username} to say what they carry for {wishes.Count} wishes"
        );

        // Last, so that nothing above throwing can leave the board both opened and waiting
        _boardOpenHold = new BoardOpenHold(key, partner, wishes, board, proceed);
        return true;
    }

    /// <summary>
    /// Finds the wishes ready on a board that the partner is short of, by their game's answer, which stay on the board
    /// this time, and tells the local player why. Without an answer, every wish that takes something from the partner
    /// stays.
    /// </summary>
    /// <returns>The wishes that stay on the board, or null if none does.</returns>
    private HashSet<string>? GetBoardTurnInsLeftOut(List<FullQuestBase> ready, BoardOpenHold resumed) {
        HashSet<string>? leftOut = null;
        var notices = 0;
        foreach (var quest in ready) {
            if (!NeedsPartnerCopy(quest)) {
                continue;
            }

            int amount;
            int needed;
            QuestTargetCounter? counter;
            if (resumed.Answer == null) {
                amount = -1;
                needed = 0;
                counter = null;
            } else if (!resumed.Answer.Lacks(quest, out amount, out needed, out counter)) {
                continue;
            }

            leftOut ??= new HashSet<string>(StringComparer.Ordinal);
            leftOut.Add(quest.name);
            Logger.Info(
                $"The wish '{quest.name}' stays on the board, because {resumed.PartnerName} has " +
                (amount < 0 ? "an amount that their game didn't say" : $"{amount}") + $" of the {needed} it takes"
            );

            if (resumed.Answer != null && notices < MaxShortCopyNotices) {
                notices++;
                Chat(
                    DescribeShortCopy(resumed.PartnerName, amount, needed, counter) + Lang.Pick(
                        $" So \"{quest.GetPopupName()}\" stays on the board this time. Both of you pay one to hand it in.",
                        $"所以「{quest.GetPopupName()}」这次留在板子上没交。要交的话，你们两个各出一份。"
                    )
                );
            }
        }

        if (leftOut != null && resumed.Answer == null) {
            Chat(Lang.Pick(
                $"{resumed.PartnerName}'s game didn't answer in time (the connection may be lagging), so the wishes " +
                "that take something from both of you stay on the board this time. Try again in a moment.",
                $"没及时收到 {resumed.PartnerName} 那边的回应（可能是网络卡了），所以要两个人都出东西的愿望这次留在板子上没交。" +
                "过一会儿再试。"
            ));
        }

        return leftOut;
    }

    /// <summary>
    /// Asks the partner what they carry before a donation that the local player said yes to goes through, which waits
    /// for the answer: the yes runs again once it came (see <see cref="UpdateWishCopyChecks"/>). The board keeps
    /// asking meanwhile, and answering no there takes the yes back.
    /// </summary>
    private void AskBeforeDonation(
        QuestItemBoard itemBoard,
        FullQuestBase quest,
        ClientPlayerData partner,
        Action proceed
    ) {
        var wishes = new List<string> { quest.name };
        var key = SendWishCopyAsk(partner.Id, wishes);
        Logger.Info($"The donation '{quest.name}' waits for {partner.Username} to say what they carry");

        // Last, so that nothing above throwing can leave the donation both declined and waiting
        _donationHold = new DonationHold(key, partner, wishes, itemBoard, quest, proceed);
    }

    /// <summary>
    /// Whether the partner's answer lets a donation that waited for it go through, telling the local player why not.
    /// </summary>
    private bool CanPartnerPayDonation(FullQuestBase quest, DonationHold resumed) {
        if (resumed.Answer == null) {
            Chat(Lang.Pick(
                $"{resumed.PartnerName}'s game didn't answer in time (the connection may be lagging), so nothing was " +
                "donated. Try again in a moment.",
                $"没及时收到 {resumed.PartnerName} 那边的回应（可能是网络卡了），所以这次没捐。过一会儿再试。"
            ));
            Logger.Info($"The donation '{quest.name}' didn't go through, because the partner's game didn't answer");
            return false;
        }

        if (!resumed.Answer.Lacks(quest, out var amount, out var needed, out var counter)) {
            return true;
        }

        Chat(DescribeShortCopy(resumed.PartnerName, amount, needed, counter) + Lang.Pick(
            " So nothing was donated. Both of you pay the donation.",
            "所以这次没捐。捐赠需要你们两个各出一份。"
        ));
        Logger.Info(
            $"The donation '{quest.name}' didn't go through, because {resumed.PartnerName} has " +
            (amount < 0 ? "an amount that their game didn't say" : $"{amount}") + $" of the {needed} it takes"
        );
        return false;
    }

    /// <summary>
    /// The message to the local player when the partner's game refused a turn-in at a character because the partner is
    /// short of what it takes, or null when the refusal doesn't say so, like one from an older version of the mod.
    /// </summary>
    private string? GetShortCopyRefusal(ClientPlayerData player, CoopSaveUpdate update) {
        if (update.WishNames.Count == 0 || FindQuest(update.WishNames[0]) is not { } quest ||
            !ReadCopies(update).Lacks(quest, out var amount, out var needed, out var counter)) {
            return null;
        }

        return DescribeShortCopy(player.Username, amount, needed, counter) + Lang.Pick(
            " So nothing was taken. Both of you pay one to hand it in.",
            "所以什么都没拿走。要交的话，你们两个各出一份。"
        );
    }

    /// <summary>
    /// What the partner's game said it carries for wishes.
    /// </summary>
    private sealed class PartnerCopies {
        /// <summary>
        /// How much the partner carries of each target, by wish. A target whose amount wasn't said is -1.
        /// </summary>
        public Dictionary<string, int[]> Amounts { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The wishes that the partner's save completed already, whose turn-in takes nothing from it.
        /// </summary>
        public HashSet<string> Completed { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Finds the first thing that a wish takes which the partner is short of. A wish that their save completed
        /// already takes nothing from them, and one that the answer left out counts as short.
        /// </summary>
        public bool Lacks(FullQuestBase quest, out int amount, out int needed, out QuestTargetCounter? counter) {
            if (Completed.Contains(quest.name)) {
                amount = 0;
                needed = 0;
                counter = null;
                return false;
            }

            Amounts.TryGetValue(quest.name, out var amounts);
            return LacksCopy(quest, amounts, out amount, out needed, out counter);
        }
    }

    /// <summary>
    /// A turn-in that waits for the partner's game to say what it carries.
    /// </summary>
    private abstract class CopyCheckHold {
        protected CopyCheckHold(ulong key, ushort partnerId, string partnerName, List<string> wishes) {
            Key = key;
            PartnerId = partnerId;
            PartnerName = partnerName;
            Wishes = wishes;
        }

        /// <summary>
        /// The key that the answer comes back with.
        /// </summary>
        public ulong Key { get; }

        /// <summary>
        /// Who was asked. Only their answer counts.
        /// </summary>
        public ushort PartnerId { get; }

        /// <summary>
        /// Their name, for what the local player is told.
        /// </summary>
        public string PartnerName { get; }

        /// <summary>
        /// The wishes that they were asked about.
        /// </summary>
        public List<string> Wishes { get; }

        /// <summary>
        /// When they were asked.
        /// </summary>
        public float Started { get; } = Time.unscaledTime;

        /// <summary>
        /// What they said they carry, or null until they answered.
        /// </summary>
        public PartnerCopies? Answer { get; set; }

        /// <summary>
        /// Whether the turn-in goes on now: they answered, or the wait ran out.
        /// </summary>
        public bool IsDue => Answer != null || Time.unscaledTime - Started > WishCopyCheckTimeout;
    }

    /// <summary>
    /// The opening of a board that waits for the partner's answer before it hands wishes in.
    /// </summary>
    private sealed class BoardOpenHold : CopyCheckHold {
        public BoardOpenHold(
            ulong key,
            ClientPlayerData partner,
            List<string> wishes,
            QuestBoardInteractable board,
            Action proceed
        ) : base(key, partner.Id, partner.Username, wishes) {
            Board = board;
            Proceed = proceed;
        }

        /// <summary>
        /// The board, whose dialogue the hero stands in until it opens.
        /// </summary>
        public QuestBoardInteractable Board { get; }

        /// <summary>
        /// Runs the opening of the board again, which then goes on with the answer.
        /// </summary>
        public Action Proceed { get; }
    }

    /// <summary>
    /// A donation that waits for the partner's answer.
    /// </summary>
    private sealed class DonationHold : CopyCheckHold {
        public DonationHold(
            ulong key,
            ClientPlayerData partner,
            List<string> wishes,
            QuestItemBoard itemBoard,
            FullQuestBase quest,
            Action proceed
        ) : base(key, partner.Id, partner.Username, wishes) {
            ItemBoard = itemBoard;
            Quest = quest;
            Proceed = proceed;
        }

        /// <summary>
        /// The list of the board, which asks whether to donate until it is answered.
        /// </summary>
        public QuestItemBoard ItemBoard { get; }

        /// <summary>
        /// The wish that the donation completes.
        /// </summary>
        public FullQuestBase Quest { get; }

        /// <summary>
        /// Runs the yes again, which then goes on with the answer.
        /// </summary>
        public Action Proceed { get; }
    }
}
