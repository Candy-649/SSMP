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
/// The hold sits on the button rather than on the answer, because the box pays before it answers: the box wraps the
/// yes it is given so that it takes the currency, locks the tools and takes the items first, and only then runs the
/// wrapped yes, which is what sends the event to the FSM. Holding the event would therefore hold an answer whose price
/// was already paid, and a partner who refused would leave the player short with nothing to show for it. Holding the
/// button is before all of it: agreeing runs the real button and pays exactly as the game always would, refusing runs
/// the no and pays nothing. The box stays open while it waits, so answering no is how the waiting player takes it back.
///
/// What the agreement changes reaches the partner through the syncs that were already there, which is why the box
/// shown to the partner never begins a wish and never consumes anything itself. The game's own accept box does
/// begin it: it is opened with beginQuest true, so its yes runs BeginQuest after the button. Only the copy opened
/// here passes false. Do not read that as "the accept box takes nothing" and move the hook back to the answer.
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
    /// The getter that says why the yes of a box is greyed out, or gives nothing while it can be pressed. The box that
    /// shows items has its own, which reading it through the getter reaches.
    /// </summary>
    private static readonly MethodInfo? BoxInactiveYesGetter =
        PromptBoxType?.GetProperty("InactiveYesText", InstanceFlags)?.GetGetMethod(true);

    /// <summary>
    /// The field with the answer a box runs when it is refused, cleared with its yes so that closing a box that was
    /// let go of runs neither.
    /// </summary>
    private static readonly FieldInfo? BoxCurrentNoField = PromptBoxType?.GetField("currentNo", InstanceFlags);

    /// <summary>
    /// The fields with what the box that shows items is about to take. Reading them says what a prompt costs whoever
    /// opened it, which is how the prompts that belong to no action at all are recognised.
    /// </summary>
    private static readonly FieldInfo? BoxRequiredItemsField =
        typeof(DialogueYesNoBox).GetField("requiredItems", InstanceFlags);

    private static readonly FieldInfo? BoxRequiredAmountsField =
        typeof(DialogueYesNoBox).GetField("requiredItemAmounts", InstanceFlags);

    /// <summary>
    /// The fields with the animations of a panel, looked up from a panel of the game the first time one is seen.
    /// </summary>
    private static FieldInfo? _paneClosingField;

    private static FieldInfo? _paneOpeningField;

    private static bool _paneFieldsLookedUp;

    /// <summary>
    /// The prompt whose box is open on this machine, or null. A box that the mod opened itself to ask about a prompt
    /// of the partner has none, which is how the two are told apart at the button.
    /// </summary>
    private YesNoAction? _openPrompt;

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
    /// Whether the hero was stopped so that the local player could answer about the partner. The box opens while they
    /// are free to move, which the game never does on its own, so it is taken here and given back after.
    /// </summary>
    private bool _heroHeldForConfirm;

    /// <summary>
    /// What keeps the hero from getting hurt while they are stopped to answer about the partner.
    /// </summary>
    private static readonly object ConfirmInvulnerability = new();

    /// <summary>
    /// The control version when the hero was stopped. The game counts every time anyone takes control, so a version
    /// that still matches is what says the hero is ours to give back rather than somebody else's to finish with.
    /// </summary>
    private int _heroControlVersion;

    /// <summary>
    /// Hooks the answer of every yes/no prompt of the game, and the end of one. They all go through the two methods of
    /// the class they share, so the prompts that need both players are picked out here rather than hooked one type at
    /// a time.
    /// </summary>
    private void RegisterWishConfirmHook() {
        AddWishTalkHook(
            typeof(YesNoBox).GetMethod("SelectYes", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null),
            new Action<Action<YesNoBox>, YesNoBox>(OnPromptSelectYes)
        );
        AddWishTalkHook(
            typeof(YesNoBox).GetMethod("SelectNo", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null),
            new Action<Action<YesNoBox>, YesNoBox>(OnPromptSelectNo)
        );
        AddWishTalkHook(
            typeof(YesNoAction).GetMethod(
                "OnEnter", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null
            ),
            new Action<Action<YesNoAction>, YesNoAction>((orig, self) => {
                _openPrompt = self;
                orig(self);
            })
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
                        CancelHeldConfirm("The dialogue ended before your teammate answered.", HeldEnd.Silence);
                    }

                    if (ReferenceEquals(_openPrompt, self)) {
                        _openPrompt = null;
                    }
                } catch (Exception e) {
                    LogWishTalkError(e);
                }

                orig(self);
            })
        );
    }

    /// <summary>
    /// Hook for the yes button of a prompt box: one that changes what both players share waits for the partner to
    /// agree before the button is really pressed, which is before the box takes anything.
    /// </summary>
    private void OnPromptSelectYes(Action<YesNoBox> orig, YesNoBox self) {
        // Pressing yes again on the box that is already waiting must not let it through and pay
        if (_wishConfirm is { } waiting && ReferenceEquals(waiting.Box, self)) {
            return;
        }

        // A yes that the box greys out does nothing when it is pressed, so asking the partner about it would spend
        // their answer on a press that goes nowhere. It is left to be refused the way the game refuses it.
        if (GetInactiveYesText(self).Length > 0) {
            orig(self);
            return;
        }

        // Half the guards of a hold are reflection: clearing the side the box remembers, and telling its answer
        // apart from a later one. Without them a hold is worse than no hold at all, because a box that is closed
        // rather than answered would pay for something nobody agreed to. So the press is simply let through. This
        // stays outside the catch below, which would otherwise swallow a throw here and press the button a second time.
        if (!CanGuardAHold()) {
            orig(self);
            return;
        }

        CoopSaveUpdate? ask = null;
        var what = "";
        try {
            // The box this mod opened to ask about the partner is answered by the player, never held again
            if (_wishConfirm == null && _partnerConfirm == null && _everChecked && _checkedWith is { } partnerId) {
                ask = GetConfirmAsk(GetLivePrompt(), self, out what);
                if (ask != null) {
                    ask.TargetId = partnerId;
                }
            }
        } catch (Exception e) {
            LogWishTalkError(e);
            ask = null;
        }

        if (ask == null) {
            orig(self);
            return;
        }

        // The side a box was last answered on is never forgotten by the game, and closing a box runs the answer of
        // that side. Holding skips the real button, so that side would still say yes from some earlier prompt and any
        // close at all would pay. Cleared here, a box that is closed instead of answered says no and pays nothing.
        BoxSelectedStateField?.SetValue(self, false);

        // Nothing has been taken: the box pays only once the real button runs, which is what the partner agreeing does
        _wishConfirm = new HeldConfirm(
            ask.TargetId, ask.Key, GetLivePrompt(), self, BoxCurrentYesField?.GetValue(self), () => orig(self), what
        );
        Send(ask);
        Chat($"{GetPartnerName()} has to agree to this too. Answer no to take it back.");
        Logger.Info($"Held the button about {what} until the partner agrees");
    }

    /// <summary>
    /// Hook for the no button of a prompt box: answering no while waiting is how the player takes back what they asked
    /// the partner about.
    /// </summary>
    private void OnPromptSelectNo(Action<YesNoBox> orig, YesNoBox self) {
        try {
            if (_wishConfirm is { } held && ReferenceEquals(held.Box, self)) {
                CancelHeldConfirm("You took it back.", HeldEnd.LeaveAlone);
            }
        } catch (Exception e) {
            LogWishTalkError(e);
        }

        orig(self);
    }

    /// <summary>
    /// The prompt whose box is open, or null if the one that was remembered is stale. PlayMaker only runs OnExit when
    /// a state is left, and never when its FSM is destroyed, so a prompt that went away with its scene or its
    /// character would otherwise be remembered for good and taken for the prompt of a later box.
    /// </summary>
    private YesNoAction? GetLivePrompt() {
        if (_openPrompt is not { } prompt) {
            return null;
        }

        if (!IsPromptLive(prompt)) {
            _openPrompt = null;
            return null;
        }

        return prompt;
    }

    /// <summary>
    /// Whether a prompt is still standing in the state its box belongs to. Asked of a named prompt rather than of the
    /// one that is on screen, because a button that waits holds its own and the two need not be the same.
    /// </summary>
    private static bool IsPromptLive(YesNoAction prompt) {
        var fsm = prompt.Fsm;
        return fsm != null && fsm.GameObject != null && fsm.ActiveState?.Name == prompt.State?.Name;
    }

    /// <summary>
    /// What to ask the partner about a prompt that the local player said yes to, or null if it changes nothing that
    /// both players share and they may answer it alone. What the box is about to take is read from the box, so that
    /// the prompts belonging to no action at all - the receptacles that swallow a key, the desk that builds something
    /// out of what it is given - are covered like the rest.
    /// </summary>
    private CoopSaveUpdate? GetConfirmAsk(YesNoAction? action, YesNoBox box, out string what) {
        what = "";

        // Both kinds of box are their own single instance and can be open at the same time, so a prompt is only taken
        // for the one on screen when the box is the kind that prompt opens
        if (action != null && IsWishPrompt(action, box)) {
            var wish = GetWishConfirmAsk(action, ref what);
            if (wish == null) {
                // Falling through to the items would accept the wish without asking anybody, silently
                Logger.Warn("A prompt about a wish could not be read, so the partner was not asked about it");
                Chat("This wish could not be shared with your teammate, so it was left to you.");
            }

            return wish;
        }

        return GetBoxItemConfirm(box, action != null, ref what);
    }

    /// <summary>
    /// Whether a prompt is about a wish and the box on screen is the one it opens.
    /// </summary>
    private static bool IsWishPrompt(YesNoAction action, YesNoBox box) {
        return action switch {
            QuestYesNo or QuestYesNoV2 => box is QuestYesNoBox,
            HutongGames.PlayMaker.Actions.QuestCompleteYesNo => box is DialogueYesNoBox,
            _ => false
        };
    }

    /// <summary>
    /// Whether every part of the game that a hold leans on can be read. A hold with these missing would keep none of
    /// the promises it makes.
    /// </summary>
    private static bool CanGuardAHold() {
        return PromptBoxType != null && BoxCurrentYesField != null && BoxCurrentNoField != null &&
               BoxSelectedStateField != null;
    }

    /// <summary>
    /// What to ask about a prompt that accepts a wish or turns one in.
    /// </summary>
    private static CoopSaveUpdate? GetWishConfirmAsk(YesNoAction action, ref string what) {
        return action switch {
            QuestYesNo accept => CreateWishConfirm(WishConfirmAccept, accept.Quest, ref what, "accepting a wish"),
            QuestYesNoV2 accept => CreateWishConfirm(WishConfirmAccept, accept.Quest, ref what, "accepting a wish"),
            HutongGames.PlayMaker.Actions.QuestCompleteYesNo turnIn =>
                CreateWishConfirm(WishConfirmTurnIn, turnIn.Quest, ref what, "turning a wish in"),
            _ => null
        };
    }

    /// <summary>
    /// What to ask about a box that is about to take items, or null unless it takes one that the story takes from both
    /// players. Everything else is paid out of the copy of whoever answered, so it stays theirs.
    /// </summary>
    private CoopSaveUpdate? GetBoxItemConfirm(YesNoBox box, bool hasPrompt, ref string what) {
        // The two boxes are built on the same class as each other rather than one on the other, and only this one
        // lists items. Reading them off the other throws, and that throw would be swallowed into letting a wish
        // through without asking.
        if (box is not DialogueYesNoBox) {
            return null;
        }

        if (BoxRequiredItemsField?.GetValue(box) is not List<SavedItem> items ||
            BoxRequiredAmountsField?.GetValue(box) is not List<int> amounts) {
            return null;
        }

        // The box lists what it asks for whether or not it takes any of it, because the same lists grey out its yes.
        // A prompt of an action that only wants the player to hold something takes nothing and needs nobody. One with
        // no action behind it is a different matter: those take afterwards, through the machine or desk that asked.
        if (hasPrompt && BoxTakesWhatItLists(box) == false) {
            return null;
        }

        CoopSaveUpdate? update = null;
        for (var i = 0; i < items.Count; i++) {
            if (items[i] == null || !IsStoryItem(items[i], out var story) || !story.ShareRemoval) {
                continue;
            }

            update ??= CreateConfirm(WishConfirmItem);
            update.Names.Add(items[i].GetType().FullName + "\n" + items[i].name);
            update.Amounts.Add(i < amounts.Count ? amounts[i] : 1);
        }

        if (update != null) {
            what = "using up something that the story keeps";
        }

        return update;
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
    /// Whether the box itself takes what it lists, or null when that can't be told. The box wraps the yes it is given
    /// in a closure that carries the flag deciding it, and that closure is the answer the box now holds.
    /// </summary>
    private static bool? BoxTakesWhatItLists(YesNoBox box) {
        if (BoxCurrentYesField?.GetValue(box) is not Delegate yes || yes.Target is not { } closure) {
            return null;
        }

        return closure.GetType().GetField("consumeCurrency", InstanceFlags)?.GetValue(closure) as bool?;
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
                        LetHeroGoAfterConfirm();
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
        if (agreed) {
            // The box is the only one the game has, so another prompt may have taken it over while the partner was
            // deciding. Pressing it now would pay for whatever it shows instead, which nobody agreed to.
            if (held.Box == null || !Equals(BoxCurrentYesField?.GetValue(held.Box), held.Callback)) {
                // Whatever the box holds now belongs to a prompt of its own, which must keep both its answers
                Chat($"{GetPartnerName()} agreed, but the prompt was gone by then, so nothing was taken.");
                return;
            }

            // The real button checks again whether it can be pressed, and would quietly do nothing if it can't
            if (GetInactiveYesText(held.Box).Length > 0) {
                Chat($"{GetPartnerName()} agreed, but you can't do this any more.");
                DeclineHeld(held);
                return;
            }

            Chat(message);
            held.Proceed();
            return;
        }

        Chat(message);
        DeclineHeld(held);
    }

    /// <summary>
    /// Takes both answers off a box, so that closing it runs neither. A box that is let go of rather than answered
    /// would otherwise run the side it remembers, which pays for something nobody agreed to.
    /// </summary>
    private static void ClearBoxAnswers(YesNoBox? box) {
        if (box == null) {
            return;
        }

        BoxCurrentYesField?.SetValue(box, null);
        BoxCurrentNoField?.SetValue(box, null);
    }

    /// <summary>
    /// Answers no on the box that waited, which pays nothing and lets the dialogue take the path it takes whenever a
    /// player declines.
    /// </summary>
    private static void DeclineHeld(HeldConfirm held) {
        if (held.Box == null) {
            return;
        }

        // Only refuse the prompt that was asked about; another one that took the box over is the player's own business
        if (!Equals(BoxCurrentYesField?.GetValue(held.Box), held.Callback)) {
            return;
        }

        held.Box.SelectNo();
    }

    /// <summary>
    /// Gives up on a held answer, telling the partner that the prompt is over so that their box closes.
    /// </summary>
    /// <param name="message">What to tell the local player, or null to tell them nothing.</param>
    /// <param name="end">How to leave the box behind.</param>
    private void CancelHeldConfirm(string? message, HeldEnd end) {
        if (_wishConfirm is not { } held) {
            return;
        }

        _wishConfirm = null;
        if (message != null) {
            Chat(message);
        }

        // Told to whoever was asked, rather than to whoever the pairing names by now. A partner reloading their save
        // clears the pairing without ending this, and the notice would then be dropped and leave their box waiting.
        Send(new CoopSaveUpdate {
            TargetId = held.PartnerId,
            Kind = CoopSaveUpdateKind.WishConfirm,
            PartCount = WishConfirmGone,
            Key = held.Key
        });

        switch (end) {
            case HeldEnd.PressNo:
                DeclineHeld(held);
                return;
            case HeldEnd.LeaveAlone:
                // The player is answering the box themselves. The side it remembers was cleared when the hold began,
                // so closing it runs its no, which is the whole point: taking either answer away would leave the
                // dialogue with nothing to end it.
                return;
            case HeldEnd.Silence:
            default:
                // The prompt is gone and its FSM has moved on, so the box must answer nobody when something closes it
                if (held.Box != null && Equals(BoxCurrentYesField?.GetValue(held.Box), held.Callback)) {
                    ClearBoxAnswers(held.Box);
                }

                return;
        }
    }

    /// <summary>
    /// How a hold that is given up leaves the box behind.
    /// </summary>
    private enum HeldEnd {
        /// <summary>
        /// The prompt is still being asked, so it is answered no, which pays nothing.
        /// </summary>
        PressNo,

        /// <summary>
        /// The player is answering the box themselves, so it is left exactly as it is.
        /// </summary>
        LeaveAlone,

        /// <summary>
        /// The prompt is over, so the box is left with no answer to run.
        /// </summary>
        Silence
    }

    /// <summary>
    /// Takes in a prompt of the partner. Showing a box ends the conversation that the local player is in and takes
    /// over a box they have open themselves, so one that arrives at a bad moment waits for a better one.
    /// </summary>
    private void QueuePartnerConfirm(ClientPlayerData player, CoopSaveUpdate update) {
        // One at a time, so that a second prompt can't replace the one being read or the one waiting. A local
        // button that is itself waiting would keep the box for the whole timeout, so that is refused at once too
        // rather than left to make both players wait it out.
        if (_partnerConfirm != null || _pendingConfirmAsk != null || _wishConfirm != null) {
            SendConfirmAnswer(player.Id, update.Key, false);
            return;
        }

        // Every guard below reads the game through reflection. Without it there is no way to tell a busy box from a
        // free one, or to stop a closing box from answering itself, so the question is refused rather than guessed.
        if (PromptBoxType == null || BoxCurrentYesField == null || BoxCurrentNoField == null ||
            BoxSelectedStateField == null || BoxPaneField == null) {
            SendConfirmAnswer(player.Id, update.Key, false);
            Logger.Warn("A prompt of the partner was refused, because this game's prompt boxes can't be read");
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
        if (IsAnyDialogueRunning() == true || IsBoxBusy(GetPromptBox(true)) || IsBoxBusy(GetPromptBox(false))) {
            return false;
        }

        // Answering stops the hero for as long as it takes, so it waits for a moment when standing still is safe
        // rather than freezing them in the middle of something. A two-player save shares what a death costs.
        var hero = HeroController.instance;
        return hero != null && !hero.controlReqlinquished && !IsHeroTakenByGame(hero);
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
    /// Why the yes of a box is greyed out, or an empty string while it can be pressed. A box that can't be read counts
    /// as pressable, which is how it behaved before this was asked at all.
    /// </summary>
    private static string GetInactiveYesText(YesNoBox box) {
        try {
            return BoxInactiveYesGetter?.Invoke(box, null) as string ?? "";
        } catch (Exception e) {
            Logger.Info($"Could not read whether a prompt can be agreed to: {e.Message}");
            return "";
        }
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

        var shown = new PartnerConfirm(partnerId, key, isWish);

        // Only the showing that is still being asked about may answer: a box that was let go of already, and is only
        // now finishing its closing animation, must not send an answer or wipe a newer question
        void Answer(bool agreed) {
            if (!ReferenceEquals(_partnerConfirm, shown)) {
                return;
            }

            _partnerConfirm = null;
            LetHeroGoAfterConfirm();
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
            StopHeroForConfirm();
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
        StopHeroForConfirm();
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
    /// Stops the hero while the local player is asked about a prompt of the partner. The game only ever opens these
    /// boxes out of dialogue, which stops the hero by itself; this one opens while they are walking around.
    /// </summary>
    private void StopHeroForConfirm() {
        var hero = HeroController.instance;
        if (_heroHeldForConfirm || hero == null || hero.controlReqlinquished) {
            return;
        }

        _heroControlVersion = HeroController.ControlVersion + 1;
        hero.RelinquishControl();
        hero.AddInvulnerabilitySource(ConfirmInvulnerability);
        _heroHeldForConfirm = true;
    }

    /// <summary>
    /// Gives the hero back after the local player answered about a prompt of the partner.
    /// </summary>
    private void LetHeroGoAfterConfirm() {
        if (!_heroHeldForConfirm) {
            return;
        }

        _heroHeldForConfirm = false;
        var hero = HeroController.instance;
        if (hero == null) {
            return;
        }

        hero.RemoveInvulnerabilitySource(ConfirmInvulnerability);

        // Anyone taking control counts up the version, so one that still matches says the hero is ours to give back.
        // A changed one means something else holds them now and will finish with them in its own time.
        if (HeroController.ControlVersion == _heroControlVersion && hero.controlReqlinquished &&
            !IsHeroTakenByGame(hero) && PlayerData.instance?.atBench != true) {
            hero.RegainControl();
        }
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
            CancelHeldConfirm($"{GetPartnerName()} didn't answer, so nothing was taken.", HeldEnd.PressNo);
        }

        if (_partnerConfirm is { } shown && now - shown.Started > WishConfirmTimeout) {
            _partnerConfirm = null;
            LetHeroGoAfterConfirm();
            CloseConfirmBox(shown.IsWish);
            SendConfirmAnswer(shown.PartnerId, shown.Key, false);
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
        // Settled before anything below is cleared, or the clearing decides it by accident. It asks the box rather
        // than whichever prompt is on screen, which need not be the held one: while the box still holds the answer
        // that was held, the dialogue is still standing there to be ended, and pressing no really ends it. Silencing
        // a box that is still open takes away the only way any answer ever reaches its FSM, stranding it for good.
        // The prompt has to still be there as well as the box. A state machine that was destroyed never ran OnExit,
        // so a button can still be holding one that is gone - and pressing no would then fire its event into nothing.
        // A hold with no prompt behind it at all is a receptacle or a desk, which answers through the box itself.
        var end = _wishConfirm is { } waiting && waiting.Box != null &&
                  Equals(BoxCurrentYesField?.GetValue(waiting.Box), waiting.Callback) &&
                  (waiting.Action == null || IsPromptLive(waiting.Action))
            ? HeldEnd.PressNo
            : HeldEnd.Silence;

        // A question that never got its turn is answered as well, or the one who asked sits out their whole timeout
        // waiting on something that was dropped here without a word
        if (_pendingConfirmAsk is { } queued) {
            _pendingConfirmAsk = null;
            SendConfirmAnswer(queued.PartnerId, queued.Update.Key, false);
        }

        _openPrompt = null;
        if (_partnerConfirm is { } shown) {
            _partnerConfirm = null;
            LetHeroGoAfterConfirm();

            // Answered to whoever asked, so they are not left waiting out their timeout for a question that is
            // already off the screen here
            SendConfirmAnswer(shown.PartnerId, shown.Key, false);
            CloseConfirmBox(shown.IsWish);
        }

        CancelHeldConfirm(null, end);
    }

    /// <summary>
    /// A yes of the local player that waits for the partner to agree.
    /// </summary>
    private sealed class HeldConfirm {
        public HeldConfirm(
            ushort partnerId, ulong key, YesNoAction? action, YesNoBox box, object? callback, Action proceed,
            string what
        ) {
            PartnerId = partnerId;
            Key = key;
            Action = action;
            Box = box;
            Callback = callback;
            Proceed = proceed;
            What = what;
        }

        /// <summary>
        /// Who was asked, and who is told when this ends. Kept here rather than read from the pairing, which can be
        /// cleared while the button still waits - and the telling would then go nowhere.
        /// </summary>
        public ushort PartnerId { get; }

        /// <summary>
        /// The answer the box held when it was asked about, which says whether it still shows the same prompt.
        /// </summary>
        public object? Callback { get; }

        /// <summary>
        /// The key that the answer of the partner comes back with.
        /// </summary>
        public ulong Key { get; }

        /// <summary>
        /// The prompt that was answered, so that its end can be told apart from any other, or null when the box was
        /// opened by something that is not an action at all.
        /// </summary>
        public YesNoAction? Action { get; }

        /// <summary>
        /// The box that waits, which stays open until it is answered one way or the other.
        /// </summary>
        public YesNoBox Box { get; }

        /// <summary>
        /// Presses the yes button for real, which is where the box takes what the prompt asks for.
        /// </summary>
        public Action Proceed { get; }

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
        public PartnerConfirm(ushort partnerId, ulong key, bool isWish) {
            PartnerId = partnerId;
            Key = key;
            IsWish = isWish;
        }

        /// <summary>
        /// Who asked, and who the answer goes back to. Kept on the showing itself rather than read from the pairing,
        /// which can already be cleared by the time a question is shown or taken down - and then there would be
        /// nobody to answer, leaving the one who asked to wait out their whole timeout.
        /// </summary>
        public ushort PartnerId { get; }

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
