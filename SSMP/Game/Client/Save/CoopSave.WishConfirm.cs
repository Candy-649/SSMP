using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The yes/no prompts of a checked two-player save that change something both players share, which both of them have
/// to agree to. A wish belongs to both saves, and an item that the story takes for good is taken from both, so letting
/// whoever pressed the button decide alone would spend what the other player owns without asking them.
///
/// The player who opened the prompt answers first. A no needs nobody, because it takes nothing. A yes is held back and
/// the partner is asked the same question; their answer runs the held one. A refusal answers no, so the dialogue takes
/// the path it takes whenever a player declines, and nothing else has to know that two of them were asked.
///
/// Holding the answer is safe: the boxes only show what is being asked for, and everything is taken by the FSM after
/// the yes event, so a prompt that is never agreed to leaves both saves untouched. Nearly every prompt answers with
/// nothing but that event; one version shows a "taken" popup first, which changes no save either. What the agreement
/// then changes reaches the partner through the syncs that were already there, which is why the box shown to the
/// partner never begins a wish and never consumes anything itself.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How long a held answer waits for the partner before it counts as a no. The player who answered stands in their
    /// dialogue while it runs, so it is short.
    /// </summary>
    private const float WishConfirmTimeout = 20f;

    /// <summary>
    /// The binding flags of a static member that the game keeps to itself.
    /// </summary>
    private const BindingFlags ConfirmStaticFlags =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// The sender is asking the partner to agree to the prompt they answered yes to.
    /// </summary>
    private const ushort WishConfirmAsk = 0;

    /// <summary>
    /// The sender agreed to the prompt of the partner.
    /// </summary>
    private const ushort WishConfirmYes = 1;

    /// <summary>
    /// The sender did not agree to the prompt of the partner.
    /// </summary>
    private const ushort WishConfirmNo = 2;

    /// <summary>
    /// The prompt of the sender is over, so the box that asked about it closes.
    /// </summary>
    private const ushort WishConfirmGone = 3;

    /// <summary>
    /// A prompt that accepts a wish, which the partner is shown as the wish box.
    /// </summary>
    private const string WishConfirmAccept = "wish";

    /// <summary>
    /// A prompt that turns a wish in, which the partner is shown as the wish box.
    /// </summary>
    private const string WishConfirmTurnIn = "wishdone";

    /// <summary>
    /// A prompt that uses up items that the story takes from both players.
    /// </summary>
    private const string WishConfirmItem = "item";

    /// <summary>
    /// The fields of the prompt actions, which are named differently between their versions.
    /// </summary>
    private static readonly Dictionary<string, FieldInfo?> ConfirmFields = new(StringComparer.Ordinal);

    /// <summary>
    /// The field with the dialogue box of the game.
    /// </summary>
    private static readonly FieldInfo? DialogueBoxInstanceField =
        typeof(DialogueBox).GetField("_instance", ConfirmStaticFlags);

    /// <summary>
    /// The field with whether the dialogue box shows dialogue.
    /// </summary>
    private static readonly FieldInfo? DialogueBoxRunningField =
        typeof(DialogueBox).GetField("isDialogueRunning", InstanceFlags);

    /// <summary>
    /// The field with the box that shows items, of which the game keeps one.
    /// </summary>
    private static readonly FieldInfo? ItemBoxInstanceField =
        typeof(DialogueYesNoBox).GetField("_instance", ConfirmStaticFlags);

    /// <summary>
    /// The field with the box that shows a wish, of which the game keeps one.
    /// </summary>
    private static readonly FieldInfo? WishBoxInstanceField =
        typeof(QuestYesNoBox).GetField("_instance", ConfirmStaticFlags);

    /// <summary>
    /// The class that both prompt boxes are built on, which holds what a box is doing. It is reached through the box
    /// rather than by name, so that the mod doesn't depend on the class being open to it.
    /// </summary>
    private static readonly Type? PromptBoxType = typeof(DialogueYesNoBox).BaseType;

    /// <summary>
    /// The field with the answer that a box runs when it is agreed to, which says whether a box is in use.
    /// </summary>
    private static readonly FieldInfo? BoxCurrentYesField = PromptBoxType?.GetField("currentYes", InstanceFlags);

    /// <summary>
    /// The field with the side of a box that was picked last, which the game never clears by itself.
    /// </summary>
    private static readonly FieldInfo? BoxSelectedStateField = PromptBoxType?.GetField("selectedState", InstanceFlags);

    /// <summary>
    /// The field with the panel that a box lives on.
    /// </summary>
    private static readonly FieldInfo? BoxPaneField = PromptBoxType?.GetField("pane", InstanceFlags);

    /// <summary>
    /// The fields with the animations of a panel, looked up from a panel of the game the first time one is seen.
    /// </summary>
    private static FieldInfo? _paneClosingField;

    private static FieldInfo? _paneOpeningField;

    private static bool _paneFieldsLookedUp;

    /// <summary>
    /// The yes of the local player that waits for the partner to agree, or null.
    /// </summary>
    private HeldConfirm? _wishConfirm;

    /// <summary>
    /// The prompt of the partner that the local player is being asked about, or null.
    /// </summary>
    private PartnerConfirm? _partnerConfirm;

    /// <summary>
    /// The prompt of the partner that waits for a moment when it can be shown, or null.
    /// </summary>
    private PendingAsk? _pendingConfirmAsk;

    /// <summary>
    /// Hooks the answer of every yes/no prompt of the game, and the end of one. They all go through the two methods of
    /// the class they share, so the prompts that need both players are picked out here rather than hooked one type at
    /// a time.
    /// </summary>
    private void RegisterWishConfirmHook() {
        AddWishTalkHook(
            typeof(YesNoAction).GetMethod(
                "SendEvent", InstanceFlags | BindingFlags.DeclaredOnly, null, [typeof(bool)], null
            ),
            new Action<Action<YesNoAction, bool>, YesNoAction, bool>(OnYesNoAnswer)
        );
        AddWishTalkHook(
            typeof(YesNoAction).GetMethod(
                "OnExit", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null
            ),
            new Action<Action<YesNoAction>, YesNoAction>((orig, self) => {
                try {
                    // A held answer never finishes its action, so the prompt stays open until the partner agrees. Any
                    // other reason to leave the state ends the prompt, and the answer then has nothing to run on: the
                    // FSM has moved on, and sending the event now would fire it at whatever it is doing instead.
                    if (_wishConfirm is { } held && ReferenceEquals(held.Action, self)) {
                        CancelHeldConfirm("The dialogue ended before your teammate answered.", false);
                    }
                } catch (Exception e) {
                    LogWishTalkError(e);
                }

                orig(self);
            })
        );
    }

    /// <summary>
    /// Hook for the answer of a yes/no prompt: one that changes what both players share waits for the partner to agree
    /// to it.
    /// </summary>
    private void OnYesNoAnswer(Action<YesNoAction, bool> orig, YesNoAction self, bool isYes) {
        CoopSaveUpdate? ask = null;
        var what = "";
        try {
            // A no is never held: it takes nothing, so there is nothing for the partner to agree to
            if (isYes && _everChecked && _checkedWith is { } partnerId) {
                ask = GetConfirmAsk(self, out what);
                if (ask != null) {
                    ask.TargetId = partnerId;
                }
            }
        } catch (Exception e) {
            LogWishTalkError(e);
            ask = null;
        }

        if (ask == null) {
            orig(self, isYes);
            return;
        }

        // One at a time: a second prompt is declined rather than asked about, so neither can hide the other
        if (_wishConfirm != null) {
            Chat($"{GetPartnerName()} is already being asked about something else.");
            orig(self, false);
            return;
        }

        // The answer is held rather than dropped, and the partner agreeing is what finally runs it
        _wishConfirm = new HeldConfirm(ask.Key, self, agreed => orig(self, agreed), what);
        Send(ask);
        Chat($"{GetPartnerName()} has to agree to this too.");
        Logger.Info($"Held the answer about {what} until the partner agrees");
    }

    /// <summary>
    /// What to ask the partner about a prompt that the local player said yes to, or null if it changes nothing that
    /// both players share and they may answer it alone.
    /// </summary>
    private CoopSaveUpdate? GetConfirmAsk(YesNoAction action, out string what) {
        what = "";
        return action switch {
            QuestYesNo accept => CreateWishConfirm(WishConfirmAccept, accept.Quest, ref what, "accepting a wish"),
            QuestYesNoV2 accept => CreateWishConfirm(WishConfirmAccept, accept.Quest, ref what, "accepting a wish"),
            HutongGames.PlayMaker.Actions.QuestCompleteYesNo turnIn =>
                CreateWishConfirm(WishConfirmTurnIn, turnIn.Quest, ref what, "turning a wish in"),
            _ => GetItemConfirm(action, ref what)
        };
    }

    /// <summary>
    /// What to ask the partner about a prompt that accepts a wish or turns one in. Both belong to both saves, so both
    /// players decide.
    /// </summary>
    private static CoopSaveUpdate? CreateWishConfirm(string kind, FsmObject? quest, ref string what, string about) {
        if (quest?.Value is not FullQuestBase full || full == null) {
            return null;
        }

        what = about;
        var update = CreateConfirm(kind);
        update.WishNames.Add(full.name);
        return update;
    }

    /// <summary>
    /// What to ask the partner about a prompt that uses items up, or null unless it uses up one that the story takes
    /// from both players. Everything else is paid out of the copy of the player who answered, so it stays theirs.
    /// </summary>
    private CoopSaveUpdate? GetItemConfirm(YesNoAction action, ref string what) {
        if (GetConfirmField(action, "ConsumeItem") is not FsmBool { Value: true }) {
            return null;
        }

        var items = new List<SavedItem>();
        var amounts = new List<int>();
        if (GetConfirmField(action, "RequiredItem") is FsmObject single && single.Value is SavedItem one && one != null) {
            items.Add(one);
            amounts.Add((GetConfirmField(action, "RequiredAmount") as FsmInt)?.Value ?? 1);
        }

        // The later versions of the prompt ask for a list instead of one item
        if (GetConfirmField(action, "RequiredItems") is FsmArray many && many.objectReferences is { } references) {
            var counts = (GetConfirmField(action, "RequiredAmounts") as FsmArray)?.intValues;
            for (var i = 0; i < references.Length; i++) {
                if (references[i] is SavedItem item && item != null) {
                    items.Add(item);
                    amounts.Add(counts != null && i < counts.Length ? counts[i] : 1);
                }
            }
        }

        CoopSaveUpdate? update = null;
        for (var i = 0; i < items.Count; i++) {
            if (!IsStoryItem(items[i], out var story) || !story.ShareRemoval) {
                continue;
            }

            update ??= CreateConfirm(WishConfirmItem);
            update.Names.Add(items[i].GetType().FullName + "\n" + items[i].name);
            update.Amounts.Add(amounts[i]);
        }

        if (update != null) {
            what = "using up something that the story keeps";
        }

        return update;
    }

    /// <summary>
    /// An update that asks the partner about a prompt, with the key that their answer comes back with.
    /// </summary>
    private static CoopSaveUpdate CreateConfirm(string kind) {
        var bytes = new byte[8];
        Random.NextBytes(bytes);
        var update = new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.WishConfirm,
            PartCount = WishConfirmAsk,
            Key = BitConverter.ToUInt64(bytes, 0)
        };
        update.Records.Add(kind);
        return update;
    }

    /// <summary>
    /// The field of a prompt action by name, which the versions of the prompts have or don't have.
    /// </summary>
    private static object? GetConfirmField(YesNoAction action, string name) {
        var type = action.GetType();
        var key = type.FullName + "\n" + name;
        if (!ConfirmFields.TryGetValue(key, out var field)) {
            field = type.GetField(name, InstanceFlags);
            ConfirmFields[key] = field;
        }

        return field?.GetValue(action);
    }

    /// <summary>
    /// The partner asked about a prompt, answered one, or their prompt is over.
    /// </summary>
    private void OnWishConfirm(ClientPlayerData player, CoopSaveUpdate update) {
        try {
            // Only the partner of the check that runs right now shares anything with this save, so an answer from an
            // older pairing belongs to nothing that still waits
            if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker) || _checkedWith != player.Id) {
                // Never leave the other player standing in their dialogue waiting for an answer that can't come
                if (update.PartCount == WishConfirmAsk) {
                    SendConfirmAnswer(player.Id, update.Key, false);
                }

                return;
            }

            switch (update.PartCount) {
                case WishConfirmAsk:
                    QueuePartnerConfirm(player, update);
                    break;
                case WishConfirmYes:
                    AnswerHeldConfirm(update.Key, true, $"{player.Username} agreed.");
                    break;
                case WishConfirmNo:
                    AnswerHeldConfirm(update.Key, false, $"{player.Username} didn't agree, so nothing was taken.");
                    break;
                case WishConfirmGone:
                    if (_pendingConfirmAsk is { } waiting && waiting.Update.Key == update.Key) {
                        _pendingConfirmAsk = null;
                    }

                    if (_partnerConfirm is { } shown && shown.Key == update.Key) {
                        _partnerConfirm = null;
                        CloseConfirmBox(shown.IsWish);
                    }

                    break;
            }
        } catch (Exception e) {
            LogWishTalkError(e);
        }
    }

    /// <summary>
    /// Runs the answer that waited for the partner, once they agreed or refused.
    /// </summary>
    private void AnswerHeldConfirm(ulong key, bool agreed, string message) {
        if (_wishConfirm is not { } held || held.Key != key) {
            return;
        }

        _wishConfirm = null;
        Chat(message);
        held.Answer(agreed);
    }

    /// <summary>
    /// Gives up on a held answer, telling the partner that the prompt is over so that their box closes.
    /// </summary>
    /// <param name="message">What to tell the local player, or null to tell them nothing.</param>
    /// <param name="answerNo">Whether the prompt is still being asked and can be answered no. A prompt that already
    /// ended must not be answered, because its FSM has moved on and the event would fire at whatever it does now.</param>
    private void CancelHeldConfirm(string? message, bool answerNo) {
        if (_wishConfirm is not { } held) {
            return;
        }

        _wishConfirm = null;
        if (message != null) {
            Chat(message);
        }

        if (_checkedWith is { } partnerId) {
            Send(new CoopSaveUpdate {
                TargetId = partnerId,
                Kind = CoopSaveUpdateKind.WishConfirm,
                PartCount = WishConfirmGone,
                Key = held.Key
            });
        }

        if (answerNo) {
            held.Answer(false);
        }
    }

    /// <summary>
    /// Takes in a prompt of the partner. Showing a box ends the conversation that the local player is in and takes
    /// over a box they have open themselves, so one that arrives at a bad moment waits for a better one.
    /// </summary>
    private void QueuePartnerConfirm(ClientPlayerData player, CoopSaveUpdate update) {
        // One at a time, so that a second prompt can't replace the one being read or the one waiting
        if (_partnerConfirm != null || _pendingConfirmAsk != null) {
            SendConfirmAnswer(player.Id, update.Key, false);
            return;
        }

        if (!CanShowConfirmNow()) {
            _pendingConfirmAsk = new PendingAsk(player.Id, player.Username, update);
            Logger.Info($"A prompt of {player.Username} waits for a moment when it can be shown");
            return;
        }

        ShowPartnerConfirm(player.Id, player.Username, update);
    }

    /// <summary>
    /// Whether a box can be shown right now. Opening one ends dialogue that runs, and it writes over the answers of a
    /// box that is already open, which would leave the dialogue of the local player with nothing to answer it. A panel
    /// that is still animating is worse: opening on top of it runs the answer of the old one at once, on the box that
    /// was just filled in.
    /// </summary>
    private bool CanShowConfirmNow() {
        return IsAnyDialogueRunning() != true && !IsBoxBusy(GetPromptBox(true)) && !IsBoxBusy(GetPromptBox(false));
    }

    /// <summary>
    /// Whether the dialogue box of the game shows dialogue, or null if that can't be read.
    /// </summary>
    private static bool? IsAnyDialogueRunning() {
        if (DialogueBoxInstanceField == null || DialogueBoxRunningField == null) {
            return null;
        }

        var dialogueBox = DialogueBoxInstanceField.GetValue(null) as DialogueBox;
        return dialogueBox != null && DialogueBoxRunningField.GetValue(dialogueBox) is true;
    }

    /// <summary>
    /// The one box of a kind that the game keeps, or null if it has none right now.
    /// </summary>
    private static object? GetPromptBox(bool isWish) {
        var box = (isWish ? WishBoxInstanceField : ItemBoxInstanceField)?.GetValue(null);
        return box is UnityEngine.Object unityBox && unityBox == null ? null : box;
    }

    /// <summary>
    /// Whether a box is in use, either because it holds an answer of its own or because its panel is still animating.
    /// </summary>
    private static bool IsBoxBusy(object? box) {
        if (box == null) {
            return false;
        }

        if (BoxCurrentYesField?.GetValue(box) != null) {
            return true;
        }

        if (BoxPaneField?.GetValue(box) is not UnityEngine.Object pane || pane == null) {
            return false;
        }

        if (!_paneFieldsLookedUp) {
            _paneFieldsLookedUp = true;
            _paneClosingField = pane.GetType().GetField("closeAnimRoutine", InstanceFlags);
            _paneOpeningField = pane.GetType().GetField("openAnimRoutine", InstanceFlags);
        }

        return _paneClosingField?.GetValue(pane) != null || _paneOpeningField?.GetValue(pane) != null;
    }

    /// <summary>
    /// Forgets which side of a box was picked last. The game keeps that on the box for good, and closing a box runs
    /// the answer of the side it remembers, so a box that is closed instead of answered would otherwise answer with a
    /// choice the player made somewhere else. Cleared before opening, a box that is closed unanswered says no.
    /// </summary>
    private static void ClearBoxChoice(bool isWish) {
        if (GetPromptBox(isWish) is { } box) {
            BoxSelectedStateField?.SetValue(box, false);
        }
    }

    /// <summary>
    /// Shows the local player what the partner answered yes to, so that they see what it costs before they agree. The
    /// box only asks: agreeing sends the answer back, and the partner is the one who goes on.
    /// </summary>
    private void ShowPartnerConfirm(ushort partnerId, string partnerName, CoopSaveUpdate update) {
        var key = update.Key;
        var kind = update.Records.Count > 0 ? update.Records[0] : "";
        var isWish = kind is WishConfirmAccept or WishConfirmTurnIn;
        if (GetPromptBox(isWish) == null) {
            SendConfirmAnswer(partnerId, key, false);
            return;
        }

        var shown = new PartnerConfirm(key, isWish);

        // Only the showing that is still being asked about may answer: a box that was let go of already, and is only
        // now finishing its closing animation, must not send an answer or wipe a newer question
        void Answer(bool agreed) {
            if (!ReferenceEquals(_partnerConfirm, shown)) {
                return;
            }

            _partnerConfirm = null;
            SendConfirmAnswer(partnerId, key, agreed);
        }

        void Yes() {
            Answer(true);
        }

        void No() {
            Answer(false);
        }

        if (isWish) {
            if ((update.WishNames.Count > 0 ? FindQuest(update.WishNames[0]) : null) is not { } quest) {
                SendConfirmAnswer(partnerId, key, false);
                return;
            }

            _partnerConfirm = shown;
            ClearBoxChoice(true);
            // The wish itself is begun or turned in by the sync of wishes in both saves, so this box only asks. The
            // HUD comes back afterwards, which the box does only when it is told to.
            QuestYesNoBox.Open(Yes, No, true, quest, false);
            Logger.Info($"Asked the local player to agree to a wish of {partnerName}");
            return;
        }

        var names = new List<string>();
        for (var i = 0; i < update.Names.Count; i++) {
            var parts = update.Names[i].Split('\n');
            if (parts.Length == 2 && FindSavedItem(parts[0], parts[1]) is { } item) {
                var amount = i < update.Amounts.Count ? update.Amounts[i] : 1;
                var name = item.GetPopupName();
                names.Add(amount > 1 ? $"{name} x{amount}" : name);
            }
        }

        if (names.Count == 0) {
            SendConfirmAnswer(partnerId, key, false);
            return;
        }

        _partnerConfirm = shown;
        ClearBoxChoice(false);
        // The items themselves aren't handed to the box: it greys its yes out when the local save is short of them,
        // and both players keep their own copies, so the box would often be impossible to agree to. It names them
        // instead. Nothing is taken here either, since the story items are taken in both saves once the partner goes on
        DialogueYesNoBox.Open(
            Yes,
            No,
            true,
            $"{partnerName} wants to use up {string.Join(", ", names)} for both of you. Agree?",
            null
        );
        Logger.Info($"Asked the local player to agree to what {partnerName} is using up");
    }

    /// <summary>
    /// Tells the partner whether the local player agreed to their prompt.
    /// </summary>
    private void SendConfirmAnswer(ushort partnerId, ulong key, bool agreed) {
        Send(new CoopSaveUpdate {
            TargetId = partnerId,
            Kind = CoopSaveUpdateKind.WishConfirm,
            PartCount = agreed ? WishConfirmYes : WishConfirmNo,
            Key = key
        });
    }

    /// <summary>
    /// Closes the box that asked the local player about a prompt of the partner.
    /// </summary>
    private static void CloseConfirmBox(bool isWish) {
        if (isWish) {
            QuestYesNoBox.ForceClose();
        } else {
            DialogueYesNoBox.ForceClose();
        }
    }

    /// <summary>
    /// Answers no for a partner who never answered, so that nobody stands in a dialogue forever, closes a box that the
    /// partner stopped waiting for, and shows a prompt that waited once there is room for it.
    /// </summary>
    private void UpdateWishConfirm() {
        var now = Time.unscaledTime;
        if (_wishConfirm is { } held && now - held.Started > WishConfirmTimeout) {
            CancelHeldConfirm($"{GetPartnerName()} didn't answer, so nothing was taken.", true);
        }

        if (_partnerConfirm is { } shown && now - shown.Started > WishConfirmTimeout) {
            _partnerConfirm = null;
            CloseConfirmBox(shown.IsWish);
            if (_checkedWith is { } waitingId) {
                SendConfirmAnswer(waitingId, shown.Key, false);
            }
        }

        if (_pendingConfirmAsk is { } pending) {
            if (now - pending.Started > WishConfirmTimeout) {
                _pendingConfirmAsk = null;
                SendConfirmAnswer(pending.PartnerId, pending.Update.Key, false);
            } else if (_partnerConfirm == null && CanShowConfirmNow()) {
                _pendingConfirmAsk = null;
                ShowPartnerConfirm(pending.PartnerId, pending.PartnerName, pending.Update);
            }
        }
    }

    /// <summary>
    /// Forgets a prompt that waited, whose dialogue is over with the scene or the session, and closes a box that asked
    /// about one.
    /// </summary>
    private void ResetWishConfirm() {
        _pendingConfirmAsk = null;
        if (_partnerConfirm is { } shown) {
            _partnerConfirm = null;
            CloseConfirmBox(shown.IsWish);
        }

        // Usually the dialogue that asked is gone with the scene, but a pairing that ends leaves it standing and
        // nothing would tick it out of its wait, so the answer is let go of rather than left hanging
        CancelHeldConfirm(null, false);
    }

    /// <summary>
    /// A yes of the local player that waits for the partner to agree.
    /// </summary>
    private sealed class HeldConfirm {
        public HeldConfirm(ulong key, YesNoAction action, Action<bool> answer, string what) {
            Key = key;
            Action = action;
            Answer = answer;
            What = what;
        }

        /// <summary>
        /// The key that the answer of the partner comes back with.
        /// </summary>
        public ulong Key { get; }

        /// <summary>
        /// The prompt that was answered, so that its end can be told apart from any other.
        /// </summary>
        public YesNoAction Action { get; }

        /// <summary>
        /// Runs the held answer, with whether the partner agreed.
        /// </summary>
        public Action<bool> Answer { get; }

        /// <summary>
        /// What the prompt was about, for the log.
        /// </summary>
        public string What { get; }

        /// <summary>
        /// When the answer started waiting.
        /// </summary>
        public float Started { get; } = Time.unscaledTime;
    }

    /// <summary>
    /// A prompt of the partner that waits for a moment when it can be shown.
    /// </summary>
    private sealed class PendingAsk {
        public PendingAsk(ushort partnerId, string partnerName, CoopSaveUpdate update) {
            PartnerId = partnerId;
            PartnerName = partnerName;
            Update = update;
        }

        /// <summary>
        /// The player to answer.
        /// </summary>
        public ushort PartnerId { get; }

        /// <summary>
        /// Their name, for the box and the log.
        /// </summary>
        public string PartnerName { get; }

        /// <summary>
        /// What they asked.
        /// </summary>
        public CoopSaveUpdate Update { get; }

        /// <summary>
        /// When it arrived.
        /// </summary>
        public float Started { get; } = Time.unscaledTime;
    }

    /// <summary>
    /// A prompt of the partner that the local player is being asked about.
    /// </summary>
    private sealed class PartnerConfirm {
        public PartnerConfirm(ulong key, bool isWish) {
            Key = key;
            IsWish = isWish;
        }

        /// <summary>
        /// The key that the answer goes back with.
        /// </summary>
        public ulong Key { get; }

        /// <summary>
        /// Whether the wish box is open rather than the box that shows items.
        /// </summary>
        public bool IsWish { get; }

        /// <summary>
        /// When the box opened.
        /// </summary>
        public float Started { get; } = Time.unscaledTime;
    }
}
