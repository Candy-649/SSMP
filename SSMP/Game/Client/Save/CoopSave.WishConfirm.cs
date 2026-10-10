using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using SSMP.Util;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The yes/no prompts of a checked co-op save that change something all players share, which all of them have to agree
/// to. A wish belongs to every save, and an item that the story takes for good is taken from each, so letting whoever
/// pressed the button decide alone would spend what the other players own without asking them.
///
/// The player who opened the prompt answers first. A no needs nobody, because it takes nothing. A yes is held back and
/// every other member is asked the same question; once all of them said yes the held one runs, and a no from any of
/// them answers it no. A refusal answers no, so the dialogue takes the path it takes whenever a player declines, and
/// nothing else has to know that several of them were asked.
///
/// A racer asking whether to start a race is one of them too, because both players run it together: see
/// <c>CoopSave.Races</c>. So is a character asking whether to help in a fight, which both players fight together:
/// see <c>CoopSave.Aid</c>.
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
    /// A prompt that starts a race against the racer of a room, which both players run together. Like a wish, both of
    /// them say yes at their own prompt.
    /// </summary>
    private const string WishConfirmRace = "race";

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
    /// The wish each other member is standing at their own box waiting to be agreed on, by member, one each.
    ///
    /// A wish is not shown to them in a box of ours. Every player walks up to the character themselves and is asked by
    /// the game, in their own conversation, in their own box - and this is only the word that another one has already
    /// said yes at theirs. Nothing is taken until all of them have.
    /// </summary>
    private readonly Dictionary<ushort, PartnerWishWait> _partnerWishWaits = new();

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
                        CancelHeldConfirm(
                            held.Members.Count == 1
                                ? Lang.Pick(
                                    "The dialogue ended before your teammate answered.", "队友还没回答，对话就结束了。"
                                )
                                : Lang.Pick(
                                    "The dialogue ended before your teammates answered.", "队友们还没回答完，对话就结束了。"
                                ),
                            HeldEnd.Silence
                        );
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
        List<ClientPlayerData>? members = null;
        var what = "";
        try {
            // The box this mod opened to ask about another member's prompt is answered by the player, never held again
            if (_wishConfirm == null && _partnerConfirm == null && _everChecked && _checkedMembers.Count > 0) {
                ask = GetConfirmAsk(GetLivePrompt(), self, out what);
                if (ask != null) {
                    members = GetCheckedMembers();
                }
            }
        } catch (Exception e) {
            LogWishTalkError(e);
            ask = null;
        }

        if (ask == null || members == null || members.Count == 0) {
            orig(self);
            return;
        }

        // The yeses have met. Every player walked up to the character themselves, was asked by their own game, and
        // said yes - so there is nothing left to wait for: the others are told, and this press runs now rather than
        // being held for answers that have already come.
        var meeting = GetAskWish(ask);
        var waits = meeting is { } asked ? GetWishWaits(asked) : [];
        if (meeting is { } met && members.TrueForAll(member => waits.Exists(wait => wait.PartnerId == member.Id))) {
            foreach (var wait in waits) {
                _partnerWishWaits.Remove(wait.PartnerId);
                SendConfirmAnswer(wait.PartnerId, wait.Key, true);
            }

            NoteWishAgreedAtPrompt(GetWishKey(met));
            var all = members.Count > 1;
            Chat(met.Kind switch {
                WishConfirmRace => Lang.Pick(
                    all ? "You all said yes, so you race together." : "You both said yes, so you race together.",
                    "你们都选了「是」，一起跑。"
                ),
                WishConfirmAid => Lang.Pick(
                    all
                        ? "You all asked for help, so the help comes to the fight."
                        : "You both asked for help, so the help comes to the fight.",
                    "你们都请求了援助，援助成立。"
                ),
                WishConfirmOffer => Lang.Pick(
                    all ? "You all said yes, so the fight starts." : "You both said yes, so the fight starts.",
                    "你们都选了「是」，开打。"
                ),
                WishConfirmFleaGame => Lang.Pick(
                    all ? "You all said yes, so you start together." : "You both said yes, so you start together.",
                    "你们都选了「是」，一起开始。"
                ),
                _ => Lang.Pick(
                    all ? "You all agreed, so it is done." : "You both agreed, so it is done.",
                    "你们都同意了，成立。"
                )
            });
            Logger.Info($"Every player agreed at their own box about {what}, so it goes through");
            orig(self);
            return;
        }

        // The side a box was last answered on is never forgotten by the game, and closing a box runs the answer of
        // that side. Holding skips the real button, so that side would still say yes from some earlier prompt and any
        // close at all would pay. Cleared here, a box that is closed instead of answered says no and pays nothing.
        BoxSelectedStateField?.SetValue(self, false);

        // Nothing has been taken: the box pays only once the real button runs, which is what every member agreeing does
        var held = new HeldConfirm(
            members.ConvertAll(member => (member.Id, member.Username)), ask.Key, GetLivePrompt(), self,
            BoxCurrentYesField?.GetValue(self), () => orig(self), what, GetWishKey(meeting)
        );

        // A member who said yes at their own box already said yes to this, and goes on once the rest of them have
        foreach (var wait in waits) {
            held.Yes.Add(wait.PartnerId);
        }

        _wishConfirm = held;
        SendToMembers(ask);
        var missing = held.GetMissingNames();
        var names = JoinNames(missing);
        var one = missing.Count == 1;
        Chat(meeting is { Kind: WishConfirmRace }
            ? Lang.Pick(
                one
                    ? $"The race starts once {names} talks to the racer and says yes too. Answer no to take it back."
                    : $"The race starts once {names} talk to the racer and say yes too. Answer no to take it back.",
                $"等 {names} 也去跟对手说话、选「是」，比赛才会开始。选「否」就可以收回。"
            )
            : meeting is { Kind: WishConfirmFleaGame }
            ? Lang.Pick(
                one
                    ? $"The game starts once {names} says yes at the same game too. Answer no to take it back."
                    : $"The game starts once {names} say yes at the same game too. Answer no to take it back.",
                $"等 {names} 也在同一个游戏那里选「是」，才会一起开始。选「否」就可以收回。"
            )
            : meeting is { Kind: WishConfirmAid }
            ? Lang.Pick(
                one
                    ? $"The help comes only once {names} asks the same character for it too. Answer no to take it back."
                    : $"The help comes only once {names} ask the same character for it too. Answer no to take it back.",
                $"要等 {names} 也去找同一个角色请求援助，援助才成立。选「否」就可以收回。"
            )
            : meeting is { Kind: WishConfirmOffer }
            ? Lang.Pick(
                one
                    ? $"The fight starts once {names} talks to the same character and says yes too. Answer no to " +
                      "take it back."
                    : $"The fight starts once {names} talk to the same character and say yes too. Answer no to take " +
                      "it back.",
                $"等 {names} 也去和同一个角色对话、选「是」，才会开打。选「否」就可以收回。"
            )
            : meeting != null
            ? Lang.Pick(
                one
                    ? $"{names} has to say yes at their own prompt too. Answer no to take it back."
                    : $"{names} have to say yes at their own prompts too. Answer no to take it back.",
                $"{names} 也要在他们自己的选项上选「是」才行。选「否」就可以收回。"
            )
            : Lang.Pick(
                one
                    ? $"{names} has to agree to this too. Answer no to take it back."
                    : $"{names} have to agree to this too. Answer no to take it back.",
                $"{names} 也要同意才行。选「否」就可以收回。"
            ));
        Logger.Info($"Held the button about {what} until {names} agree");
    }

    /// <summary>
    /// The waits of checked members at their own box, or at the step of dialogue that begins it, about the same wish
    /// and the same thing done to it.
    /// </summary>
    private List<PartnerWishWait> GetWishWaits((string Kind, string Wish) wish) {
        var waits = new List<PartnerWishWait>();
        foreach (var wait in _partnerWishWaits.Values) {
            if (wait.Kind == wish.Kind && wait.Wish == wish.Wish && _checkedMembers.Contains(wait.PartnerId)) {
                waits.Add(wait);
            }
        }

        return waits;
    }

    /// <summary>
    /// Forgets the waits of members about a wish that went through here: each of them said yes already, and goes on with
    /// the yes of this player that reached them.
    /// </summary>
    private void ForgetWishWaits(string wishKey) {
        if (wishKey.Length == 0) {
            return;
        }

        foreach (var wait in _partnerWishWaits.Values.ToList()) {
            if (GetWishKey((wait.Kind, wait.Wish)) == wishKey) {
                _partnerWishWaits.Remove(wait.PartnerId);
            }
        }
    }

    /// <summary>
    /// Keeps the word of a member that they said yes at their own box or read to the step. One wait of theirs at a
    /// time: an older one is answered no rather than forgotten, or it would stand at its box until its own time ran out
    /// over a question nobody was listening for any more.
    /// </summary>
    private void KeepWishWait(PartnerWishWait wait) {
        if (_partnerWishWaits.TryGetValue(wait.PartnerId, out var older) && older.Key != wait.Key) {
            SendConfirmAnswer(older.PartnerId, older.Key, false);
            DropWishWait(older);
        }

        _partnerWishWaits[wait.PartnerId] = wait;
    }

    /// <summary>
    /// Forgets the wait of a member that ended without going through. Their yes stops counting for a yes of this player
    /// that waits on the rest, and for a step of dialogue that waits on them: a yes counted from a wait that is gone
    /// let the rest go through without them. A member who read to a step of dialogue keeps their yes, since reading to
    /// it is not taken back.
    /// </summary>
    private void DropWishWait(PartnerWishWait wait) {
        _partnerWishWaits.Remove(wait.PartnerId);
        if (wait.IsRead) {
            return;
        }

        var wishKey = GetWishKey((wait.Kind, wait.Wish));
        if (_wishConfirm is { } heldYes && heldYes.WishKey == wishKey) {
            heldYes.Yes.Remove(wait.PartnerId);
        }

        if (_heldBegin is { } heldRead && heldRead.WishKey == wishKey) {
            heldRead.Yes.Remove(wait.PartnerId);
        }
    }

    /// <summary>
    /// Forgets without a word the waits at their own box of the members who were asked about a yes of the local player
    /// that ended without going through, about the same wish. Their yes counted for it and has nothing left here to
    /// meet: a new yes of this player asks them again. Kept, the word each of them sends as their own wait ends was
    /// one more line about a yes that no longer stands, right after the line that said why.
    /// </summary>
    private void DropWaitsOfEndedHold(HeldConfirm held) {
        foreach (var wait in _partnerWishWaits.Values.ToList()) {
            if (!wait.IsRead && held.IsAsked(wait.PartnerId) && GetWishKey((wait.Kind, wait.Wish)) == held.WishKey) {
                DropWishWait(wait);
            }
        }
    }

    /// <summary>
    /// Hook for the no button of a prompt box: answering no while waiting is how the player takes back what they asked
    /// the partner about.
    /// </summary>
    private void OnPromptSelectNo(Action<YesNoBox> orig, YesNoBox self) {
        try {
            if (_wishConfirm is { } held && ReferenceEquals(held.Box, self)) {
                CancelHeldConfirm(Lang.Pick("You took it back.", "你收回了。"), HeldEnd.LeaveAlone);
            }

            // Said no at this player's own box, which is the answer the members standing at theirs are waiting for.
            // Without this they would wait out their whole time for a no that was already given. A member who read to
            // a step of dialogue that begins the wish has nothing to take back, and gets it with this player whenever
            // they say yes to it after all (CoopSave.WishRead).
            var told = new List<string>();
            foreach (var waiting in _partnerWishWaits.Values.ToList()) {
                if (waiting.IsRead || !IsPromptAboutWish(self, waiting)) {
                    continue;
                }

                DropWishWait(waiting);
                SendConfirmAnswer(waiting.PartnerId, waiting.Key, false);
                told.Add(waiting.PartnerName);
            }

            if (told.Count > 0) {
                var names = JoinNames(told);
                Chat(Lang.Pick(
                    told.Count == 1
                        ? $"You said no, so {names} doesn't get it either."
                        : $"You said no, so {names} don't get it either.",
                    $"你选了「否」，所以 {names} 那边也不会成立。"
                ));
            }
        } catch (Exception e) {
            LogWishTalkError(e);
        }

        orig(self);
    }

    /// <summary>
    /// Whether the box being answered is the one about the wish the partner is waiting on.
    /// </summary>
    private bool IsPromptAboutWish(YesNoBox box, PartnerWishWait waiting) {
        var prompt = GetLivePrompt();
        if (prompt == null) {
            return false;
        }

        var what = "";
        var ask = IsRacePrompt(prompt, box)
            ? CreateRaceConfirm(prompt, ref what)
            : IsFleaGamePrompt(prompt, box)
                ? CreateFleaGameConfirm(prompt, ref what)
                : IsAidPrompt(prompt, box)
                    ? CreateAidConfirm(prompt, ref what)
                    : IsOfferPrompt(prompt, box)
                        ? CreateOfferConfirm(prompt, ref what)
                        : IsWishPrompt(prompt, box)
                            ? GetWishConfirmAsk(prompt, ref what)
                            : null;

        return GetAskWish(ask) is { } wish && wish.Kind == waiting.Kind && wish.Wish == waiting.Wish;
    }

    /// <summary>
    /// What a wish and the thing being done to it are matched on between the two players, or an empty string for
    /// something that is not about a wish.
    /// </summary>
    private static string GetWishKey((string Kind, string Wish)? asked) {
        return asked is { } wish ? wish.Kind + "\n" + wish.Wish : "";
    }

    /// <summary>
    /// How long a yes waits for the partner's, by what it is matched on (see <see cref="GetWishKey"/>).
    /// </summary>
    private static float GetConfirmTimeout(string wishKey) {
        return IsOfferWishKey(wishKey) ? OfferConfirmTimeout : WishConfirmTimeout;
    }

    /// <summary>
    /// The wish an ask is about, or null when it is not about one.
    /// </summary>
    private static (string Kind, string Wish)? GetAskWish(CoopSaveUpdate? ask) {
        if (ask == null || ask.Records.Count == 0 || ask.WishNames.Count == 0) {
            return null;
        }

        var kind = ask.Records[0];

        return kind is WishConfirmAccept or WishConfirmTurnIn or WishConfirmRace or WishConfirmFleaGame
                   or WishConfirmAid or WishConfirmOffer
            ? (kind, ask.WishNames[0])
            : null;
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

        // Before the wishes, because the first race is asked in the box that accepts its wish. Whichever box each of
        // the two players is shown, what they agree on is starting a race against the same racer.
        if (action != null && IsRacePrompt(action, box)) {
            return CreateRaceConfirm(action, ref what);
        }

        // No game of the festival is started by one player alone
        if (action != null && IsFleaGamePrompt(action, box)) {
            return CreateFleaGameConfirm(action, ref what);
        }

        // Nor is a character asked to help in a fight that both of them fight
        if (action != null && IsAidPrompt(action, box)) {
            return CreateAidConfirm(action, ref what);
        }

        // Nor does a creature of the room start a fight that only one of them said yes to
        if (action != null && IsOfferPrompt(action, box)) {
            return CreateOfferConfirm(action, ref what);
        }

        // Both kinds of box are their own single instance and can be open at the same time, so a prompt is only taken
        // for the one on screen when the box is the kind that prompt opens
        if (action != null && IsWishPrompt(action, box)) {
            var wish = GetWishConfirmAsk(action, ref what);
            if (wish == null) {
                // Falling through to the items would accept the wish without asking anybody, silently
                Logger.Warn("A prompt about a wish could not be read, so the partner was not asked about it");
                Chat(Lang.Pick(
                    "This wish could not be shared with your teammate, so it was left to you.",
                    "这个愿望没能和队友共享，所以只留给你了。"
                ));
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
            // Only a member of the check that runs right now shares anything with this save, so an answer from an
            // older pairing belongs to nothing that still waits
            if (GetCurrentMarker() is not { } marker || !IsMember(player, marker) ||
                !_checkedMembers.Contains(player.Id)) {
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
                    AnswerHeldConfirm(player, update.Key, true, IsRaceHold(update.Key)
                        ? Lang.Pick(
                            $"{player.Username} said yes too, so you race together.",
                            $"{player.Username} 也选了「是」，一起跑。"
                        )
                        : IsFleaGameHold(update.Key)
                        ? Lang.Pick(
                            $"{player.Username} said yes too, so you start together.",
                            $"{player.Username} 也选了「是」，一起开始。"
                        )
                        : IsAidHold(update.Key)
                        ? Lang.Pick(
                            $"{player.Username} asked for help too, so the help comes to the fight.",
                            $"{player.Username} 也请求了援助，援助成立。"
                        )
                        : IsOfferHold(update.Key)
                        ? Lang.Pick(
                            $"{player.Username} said yes too, so the fight starts.",
                            $"{player.Username} 也选了「是」，开打。"
                        )
                        : Lang.Pick($"{player.Username} agreed.", $"{player.Username} 同意了。"));
                    break;
                case WishConfirmNo:
                    // A turn-in that the partner's game refused because their copy is short says what they have
                    AnswerHeldConfirm(player, update.Key, false, GetShortCopyRefusal(player, update) ??
                                                                 (IsRaceHold(update.Key)
                        ? Lang.Pick(
                            $"{player.Username} didn't say yes, so the race didn't start.",
                            $"{player.Username} 没有选「是」，比赛没有开始。"
                        )
                        : IsFleaGameHold(update.Key)
                        ? Lang.Pick(
                            $"{player.Username} didn't say yes, so the game didn't start.",
                            $"{player.Username} 没有选「是」，游戏没有开始。"
                        )
                        : IsAidHold(update.Key)
                        ? Lang.Pick(
                            $"{player.Username} didn't ask for help, so nobody comes to help.",
                            $"{player.Username} 没有请求援助，援助没有成立。"
                        )
                        : IsOfferHold(update.Key)
                        ? Lang.Pick(
                            $"{player.Username} didn't say yes, so the fight didn't start.",
                            $"{player.Username} 没有选「是」，没有开打。"
                        )
                        : Lang.Pick(
                            $"{player.Username} didn't agree, so nothing was taken.",
                            $"{player.Username} 没有同意，所以什么都没有拿走。"
                        )));
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

                    // The member is no longer standing at their own box. Kept, their yes was still here to meet, and
                    // a yes at this player's box went straight through without them: the wish taken, handed in or
                    // raced by some of them after another had said no
                    if (_partnerWishWaits.TryGetValue(player.Id, out var partnerAt) && partnerAt.Key == update.Key) {
                        // A yes they took back no longer counts for a yes of this player that waits on the rest
                        DropWishWait(partnerAt);

                        // A member whose dialogue went on from the step that begins the wish still read to it, and gets
                        // it with this player once they read to the same step, which is what they were told already
                        if (partnerAt.IsRead) {
                            break;
                        }

                        Chat(Lang.Pick(
                            $"The yes of {partnerAt.PartnerName} no longer stands.",
                            $"{partnerAt.PartnerName} 的「是」已经不算数了。"
                        ));
                    }

                    break;
            }
        } catch (Exception e) {
            LogWishTalkError(e);
        }
    }

    /// <summary>
    /// Takes in the answer of a member to a yes of the local player that waited: once every member agreed, the held one
    /// runs, and a refusal of any of them answers it no.
    /// </summary>
    /// <param name="player">The member who answered.</param>
    /// <param name="key">The key of the yes that they answered.</param>
    /// <param name="agreed">Whether they agreed.</param>
    /// <param name="message">What to tell the local player once this answer decides it.</param>
    private void AnswerHeldConfirm(ClientPlayerData player, ulong key, bool agreed, string message) {
        // A step of dialogue that begins a wish has no no to answer. It waits until every member reads to the same
        // step, or its own time runs out (CoopSave.WishRead).
        if (_heldBegin is { } begin && begin.Key == key) {
            if (!agreed) {
                Logger.Info(
                    $"The game of {player.Username} turned down the wait at '{begin.Wish}', which waits on all the same"
                );
                return;
            }

            begin.Yes.Add(player.Id);
            NoteMemberReadWish(begin.Wish, player.Id);
            if (begin.IsAgreed) {
                ReleaseHeldBegin(begin.Members.Count == 1
                    ? Lang.Pick(
                        $"{player.Username} got to it too, so you both take the wish.",
                        $"{player.Username} 也到了这一步，你们一起接下这个愿望。"
                    )
                    : Lang.Pick(
                        $"{player.Username} got to it too, so you all take the wish.",
                        $"{player.Username} 也到了这一步，你们一起接下这个愿望。"
                    ));
            } else {
                ChatStillWaiting(player.Username, begin.GetMissingNames());
            }

            return;
        }

        // The answer to a wait at a step of dialogue that ended already, or to an ask as a check ended
        if (AnswerEndedRead(player, key, agreed)) {
            return;
        }

        if (_wishConfirm is not { } held || held.Key != key || !held.IsAsked(player.Id)) {
            return;
        }

        if (agreed) {
            held.Yes.Add(player.Id);
            if (!held.IsAgreed) {
                ChatStillWaiting(player.Username, held.GetMissingNames());
                return;
            }

            ProceedHeldConfirm(held, player.Username, message);
            return;
        }

        // A no from any member is a no for this yes. The others who were asked stop counting it.
        _wishConfirm = null;
        TellHeldConfirmIsOver(held, player.Id);
        DropWaitsOfEndedHold(held);
        Chat(message);
        DeclineHeld(held);
    }

    /// <summary>
    /// Runs a yes that every member agreed to: the real button, which pays as the game always would.
    /// </summary>
    /// <param name="held">The yes that waited.</param>
    /// <param name="lastName">The name of the member whose yes came last.</param>
    /// <param name="message">What to tell the local player.</param>
    private void ProceedHeldConfirm(HeldConfirm held, string lastName, string message) {
        _wishConfirm = null;

        // The members who said yes at their own box waited for this yes, which reached them with the ask, so they go
        // on whatever happens at this box. Kept, a wait that already went through was answered no once its time ran
        // out, with a line saying they didn't take what they took, and a no pressed at this box took it from them in
        // words as well.
        ForgetWishWaits(held.WishKey);

        // The box is the only one the game has, so another prompt may have taken it over while the others were
        // deciding. Pressing it now would pay for whatever it shows instead, which nobody agreed to.
        if (held.Box == null || !Equals(BoxCurrentYesField?.GetValue(held.Box), held.Callback)) {
            // Whatever the box holds now belongs to a prompt of its own, which must keep both its answers
            Chat(Lang.Pick(
                $"{lastName} agreed, but the prompt was gone by then, so nothing was taken.",
                $"{lastName} 同意了，但那时候提示已经没了，所以什么都没拿走。"
            ));
            return;
        }

        // The real button checks again whether it can be pressed, and would quietly do nothing if it can't
        if (GetInactiveYesText(held.Box).Length > 0) {
            Chat(Lang.Pick(
                $"{lastName} agreed, but you can't do this any more.",
                $"{lastName} 同意了，但你已经不能这么做了。"
            ));
            DeclineHeld(held);
            return;
        }

        Chat(message);
        NoteWishAgreedAtPrompt(held.WishKey);
        held.Proceed();
    }

    /// <summary>
    /// Tells the local player that a member said yes too, while others still have to.
    /// </summary>
    private void ChatStillWaiting(string name, List<string> missing) {
        var names = JoinNames(missing);
        Chat(Lang.Pick(
            $"{name} said yes too. Still waiting for {names}.",
            $"{name} 也同意了。还在等 {names}。"
        ));
    }

    /// <summary>
    /// Tells the members who were asked about a yes of the local player that it is over, so that their boxes close and
    /// their own yes stops counting it. Each is told on their own, rather than whoever the pairing names by now: a
    /// member reloading their save clears the pairing without ending this, and the notice would then be dropped and
    /// leave their box waiting.
    /// </summary>
    /// <param name="held">The yes that is over.</param>
    /// <param name="except">A member who needs no telling, like the one who said no, or null.</param>
    private void TellHeldConfirmIsOver(HeldConfirm held, ushort? except) {
        foreach (var (id, _) in held.Members) {
            if (id == except) {
                continue;
            }

            Send(new CoopSaveUpdate {
                TargetId = id,
                Kind = CoopSaveUpdateKind.WishConfirm,
                PartCount = WishConfirmGone,
                Key = held.Key
            });
        }
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
    /// Gives up on a held answer, telling the members that the prompt is over so that their boxes close.
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

        TellHeldConfirmIsOver(held, null);

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
        // A wish this save has already taken, or already turned in, is not a question. A wish belongs to both saves -
        // that is the whole reason both players are asked about one - so once this save has it there is nothing left
        // to agree to and nothing anyone could sensibly refuse. Asking anyway is worse than pointless: it puts a box
        // in front of this player for something they did an hour ago, and if it cannot be shown to them at all, the
        // partner standing at the character is told no and cannot take a wish this save already holds. Which is
        // exactly what happened to the player who went away and came back to find their partner had taken it.
        if (IsWishThisSaveAlreadyHas(update)) {
            if (GetAskWish(update) is { Kind: WishConfirmAccept } had) {
                ForgetWishRead(had.Wish);
            }

            SendConfirmAnswer(player.Id, update.Key, true);
            Logger.Info(
                $"Agreed to the wish of {player.Username} without asking, because this save already has it"
            );
            return;
        }

        // A turn-in takes a full copy from this save too, and this game looks in its own bag right now. When it is
        // short, the partner hears it at once, with what this save has, rather than standing at their prompt until
        // their time runs out for a yes that could never come
        if (RefuseTurnInThisSaveCannotPay(player, update)) {
            return;
        }

        // An ask as a check ends whether this save remembers reading to a wish too, which puts nothing in front of
        // this player (CoopSave.WishRead)
        if (AnswerReadCheck(player, update)) {
            return;
        }

        // A wish is never put in front of this player in a box of ours. Both of them walk up to the character
        // themselves, both are asked by their own game in their own conversation, and both answer their own box -
        // which is the whole of what makes it their decision rather than a copy of somebody else's. All that crosses
        // the wire is the word that one of them has said yes, and this remembers it until the other reaches theirs.
        if (GetAskWish(update) is { } asked) {
            TakeWishWait(player, update, asked);
            return;
        }

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
    /// Takes in that the partner said yes at their own box about a wish, and matches it against a yes this player
    /// has already given at theirs.
    /// </summary>
    private void TakeWishWait(ClientPlayerData player, CoopSaveUpdate update, (string Kind, string Wish) asked) {
        var wishKey = GetWishKey(asked);
        var isRead = update.Records.Count > 1 && update.Records[1] == WishConfirmRead;
        var wait = new PartnerWishWait(player.Id, player.Username, update.Key, asked.Kind, asked.Wish, isRead);

        // A member who reads to the step that begins a wish has read to it, which counts for any save that remembers
        // reading to it too (CoopSave.WishRead)
        if (asked.Kind == WishConfirmAccept && isRead) {
            NoteMemberReadWish(asked.Wish, player.Id);
        }

        // This player got there first and has been standing at their own box waiting for exactly this. This member
        // has now said yes at their own prompt, which is part of what was being waited for: they are told, and once
        // every member has, this one is let out of its hold.
        if (_wishConfirm is { } held && held.WishKey == wishKey && held.IsAsked(player.Id)) {
            SendConfirmAnswer(player.Id, update.Key, true);
            KeepWishWait(wait);
            held.Yes.Add(player.Id);
            if (!held.IsAgreed) {
                ChatStillWaiting(player.Username, held.GetMissingNames());
                return;
            }

            ProceedHeldConfirm(held, player.Username, asked.Kind == WishConfirmRace
                ? Lang.Pick(
                    $"{player.Username} said yes too, so you race together.",
                    $"{player.Username} 也选了「是」，一起跑。"
                )
                : asked.Kind == WishConfirmFleaGame
                ? Lang.Pick(
                    $"{player.Username} said yes too, so you start together.",
                    $"{player.Username} 也选了「是」，一起开始。"
                )
                : asked.Kind == WishConfirmAid
                ? Lang.Pick(
                    $"{player.Username} asked for help too, so the help comes to the fight.",
                    $"{player.Username} 也请求了援助，援助成立。"
                )
                : asked.Kind == WishConfirmOffer
                ? Lang.Pick(
                    $"{player.Username} said yes too, so the fight starts.",
                    $"{player.Username} 也选了「是」，开打。"
                )
                : Lang.Pick(
                    $"{player.Username} said yes at their own prompt too, so it is done.",
                    $"{player.Username} 也在他们自己的选项上选了「是」，成立。"
                ));

            return;
        }

        // This player stands at the step of dialogue that begins the same wish, waiting for exactly this
        if (_heldBegin is { } begin && begin.WishKey == wishKey && begin.HasMember(player.Id)) {
            SendConfirmAnswer(player.Id, update.Key, true);
            KeepWishWait(wait);
            begin.Yes.Add(player.Id);
            if (!begin.IsAgreed) {
                ChatStillWaiting(player.Username, begin.GetMissingNames());
                return;
            }

            ReleaseHeldBegin(begin.Members.Count == 1
                ? Lang.Pick(
                    $"{player.Username} got to it too, so you both take the wish.",
                    $"{player.Username} 也到了这一步，你们一起接下这个愿望。"
                )
                : Lang.Pick(
                    $"{player.Username} got to it too, so you all take the wish.",
                    $"{player.Username} 也到了这一步，你们一起接下这个愿望。"
                ));
            return;
        }

        // This player read to that step before and the dialogue went on without the wish, which waited for the
        // others to get to it too. This member now has; the wish is taken once every member is known to have.
        if (asked.Kind == WishConfirmAccept && HasReadWish(asked.Wish)) {
            SendConfirmAnswer(player.Id, update.Key, true);
            NoteMemberReadWish(asked.Wish, player.Id);
            if (!HaveAllMembersReadWish(asked.Wish)) {
                Logger.Info(
                    $"{player.Username} got to the wish '{asked.Wish}' that the local player read to before, which " +
                    "waits for the other members too"
                );
                return;
            }

            TakeReadWish(asked.Wish);
            Chat(_checkedMembers.Count <= 1
                ? Lang.Pick(
                    $"{player.Username} got to the wish that you read to before, so you both took it.",
                    $"{player.Username} 也到了你之前读到的那个愿望，你们一起接下了它。"
                )
                : Lang.Pick(
                    $"{player.Username} got to the wish that you read to before, the last of you to, so you all " +
                    "took it.",
                    $"{player.Username} 也到了你之前读到的那个愿望，大家都读到了，你们一起接下了它。"
                ));
            return;
        }

        KeepWishWait(wait);
        Chat(isRead
            ? Lang.Pick(
                $"{player.Username} read to where a wish is taken. Read the dialogue of the same character to that " +
                "point too, and you take it together.",
                $"{player.Username} 读到了接下一个愿望的那一步。你也和同一个角色把对话读到那里，你们就一起接下。"
            )
            : asked.Kind switch {
            WishConfirmRace => Lang.Pick(
                $"{player.Username} wants to start a race. Talk to the racer and say yes too, and you run together.",
                $"{player.Username} 想开始比赛。你也去跟对手说话、选「是」，就一起跑。"
            ),
            WishConfirmFleaGame => Lang.Pick(
                $"{player.Username} wants to start a game of the festival. Say yes at the same game too, and you " +
                "start together.",
                $"{player.Username} 想开始跳蚤游戏。你也去同一个游戏那里选「是」，就一起开始。"
            ),
            WishConfirmAid => Lang.Pick(
                $"{player.Username} asked a character for help in a fight. Ask the same character and say yes too, " +
                "and the help comes for you together.",
                $"{player.Username} 向一个角色请求了援助。你也去找同一个角色、选「是」，援助才成立。"
            ),
            WishConfirmOffer => Lang.Pick(
                $"{player.Username} said yes to a fight that a character offers. Talk to the same character and say " +
                "yes too, and the fight starts for you together.",
                $"{player.Username} 在一个角色那里选了「是」要开打。你也去和同一个角色对话、选「是」，就一起开打。"
            ),
            WishConfirmAccept => Lang.Pick(
                $"{player.Username} said yes to taking this wish. Say yes at your own prompt and you take it together.",
                $"{player.Username} 在他们那边选了接下这个愿望。你走到自己的选项上也选「是」，就一起接下。"
            ),
            _ => Lang.Pick(
                $"{player.Username} said yes to handing this wish in. Say yes at your own prompt too.",
                $"{player.Username} 在他们那边同意交付这个愿望了。你也在自己的选项上选「是」。"
            )
        });
        Logger.Info(
            $"{player.Username} is waiting at their own {(isRead ? "step of dialogue" : "box")} about the wish " +
            $"'{asked.Wish}' ({asked.Kind})"
        );
    }

    /// <summary>
    /// Whether a question of the partner is one this save has answered by living: a wish it has already taken, when
    /// they are taking it, or one it has already turned in, when they are turning it in.
    ///
    /// A character asked to help in a fight is the same: this save has asked them already. What the story takes for
    /// good is asked about every time, because that is a thing being spent and two players spending it are spending
    /// it twice.
    /// </summary>
    private static bool IsWishThisSaveAlreadyHas(CoopSaveUpdate update) {
        if (update.Records.Count == 0 || update.WishNames.Count == 0) {
            return false;
        }

        var playerData = PlayerData.instance;
        if (playerData == null) {
            return false;
        }

        var wish = playerData.QuestCompletionData.GetData(update.WishNames[0]);

        return update.Records[0] switch {
            WishConfirmAccept => wish.IsAccepted || wish.IsCompleted,
            WishConfirmTurnIn => wish.IsCompleted,
            WishConfirmAid => playerData.GetBool(update.WishNames[0]),
            _ => false
        };
    }

    /// <summary>
    /// Answers no to a turn-in of the partner when this save is short of what it takes, with what this save has, and
    /// tells the local player.
    /// </summary>
    /// <returns>Whether the turn-in was refused.</returns>
    private bool RefuseTurnInThisSaveCannotPay(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetAskWish(update) is not { Kind: WishConfirmTurnIn } turnIn || FindQuest(turnIn.Wish) is not { } quest ||
            PlayerData.instance is not { } playerData || _differentWishNames.Contains(quest.name)) {
            return false;
        }

        var amounts = GetLocalWishProgress(quest, playerData.QuestCompletionData.GetData(quest.name));
        if (!LacksCopy(quest, amounts, out var amount, out var needed, out var counter)) {
            return false;
        }

        var refusal = new CoopSaveUpdate {
            TargetId = player.Id,
            Kind = CoopSaveUpdateKind.WishConfirm,
            PartCount = WishConfirmNo,
            Key = update.Key
        };
        AddCopyEntries(refusal, quest, playerData);
        Send(refusal);

        Chat(DescribeShortCopy(null, amount, needed, counter) + (_checkedMembers.Count <= 1
            ? Lang.Pick(
                $" So {player.Username} couldn't hand this wish in. Both of you pay one to hand it in.",
                $"所以 {player.Username} 这次交不了这个愿望。要交的话，你们两个各出一份。"
            )
            : Lang.Pick(
                $" So {player.Username} couldn't hand this wish in. Each of you pays one to hand it in.",
                $"所以 {player.Username} 这次交不了这个愿望。要交的话，每个人各出一份。"
            )));
        Logger.Info(
            $"Refused the turn-in of '{quest.name}' by {player.Username}, because this save has {amount} of the " +
            $"{needed} it takes"
        );
        return true;
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
            _checkedMembers.Count <= 1
                ? Lang.Pick(
                    $"{partnerName} wants to use up {string.Join(", ", names)} for both of you. Agree?",
                    $"{partnerName} 想用掉 {string.Join("、", names)}，你们两个的都会用掉。同意吗？"
                )
                : Lang.Pick(
                    $"{partnerName} wants to use up {string.Join(", ", names)} for all of you. Agree?",
                    $"{partnerName} 想用掉 {string.Join("、", names)}，大家的都会用掉。同意吗？"
                ),
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
        if (_wishConfirm is { } held && now - held.Started > GetConfirmTimeout(held.WishKey)) {
            DropWaitsOfEndedHold(held);
            var missing = held.GetMissingNames();
            var names = JoinNames(missing);
            var one = missing.Count == 1;
            CancelHeldConfirm(IsRaceHold(held.Key)
                ? Lang.Pick(
                    $"{names} didn't say yes in time, so the race didn't start.",
                    $"{names} 没有及时选「是」，比赛没有开始。"
                )
                : IsFleaGameHold(held.Key)
                ? Lang.Pick(
                    $"{names} didn't say yes in time, so the game didn't start.",
                    $"{names} 没有及时选「是」，游戏没有开始。"
                )
                : IsAidHold(held.Key)
                ? Lang.Pick(
                    $"{names} didn't ask for help in time, so nobody comes to help.",
                    $"{names} 没有及时请求援助，援助没有成立。"
                )
                : IsOfferHold(held.Key)
                ? Lang.Pick(
                    $"{names} didn't say yes in time, so the fight didn't start.",
                    $"{names} 没有及时选「是」，没有开打。"
                )
                : Lang.Pick(
                    one ? $"{names} didn't answer, so nothing was taken." : $"{names} didn't all answer, so nothing was taken.",
                    $"{names} 没有回答，所以什么都没有拿走。"
                ), HeldEnd.PressNo);
        }

        if (_partnerConfirm is { } shown && now - shown.Started > WishConfirmTimeout) {
            _partnerConfirm = null;
            LetHeroGoAfterConfirm();
            CloseConfirmBox(shown.IsWish);
            SendConfirmAnswer(shown.PartnerId, shown.Key, false);
        }

        foreach (var wishWait in _partnerWishWaits.Values.ToList()) {
            // A member who read to a step of dialogue waits for as long as they stay a member of the check, and one who
            // left took their wait with them
            if (wishWait.IsRead) {
                if (!_checkedMembers.Contains(wishWait.PartnerId)) {
                    _partnerWishWaits.Remove(wishWait.PartnerId);
                }

                continue;
            }

            // A member standing at their own box is not waited on forever. They are told no, and this player is told
            // why, since from their side nothing visible ever happened at all.
            // A member who read to a step of dialogue is not, though: they have nothing to be told no about, and get
            // the wish with this player whenever this player reads to the same step (CoopSave.WishRead)
            var wishKey = GetWishKey((wishWait.Kind, wishWait.Wish));
            if (now - wishWait.Started <= GetConfirmTimeout(wishKey)) {
                continue;
            }

            DropWishWait(wishWait);

            // A yes of this player about the same wish waits on them as well, so this player did get to it. Their own
            // wait ends by its own time or by an answer like this one does, and a no from here would end it as if this
            // player had said no.
            if (_wishConfirm is { } live && live.WishKey == wishKey ||
                _heldBegin is { } begin && begin.WishKey == wishKey) {
                continue;
            }

            SendConfirmAnswer(wishWait.PartnerId, wishWait.Key, false);
            Chat(wishWait.Kind == WishConfirmRace
                ? Lang.Pick(
                    $"You didn't say yes to the racer in time, so the race of {wishWait.PartnerName} didn't start.",
                    $"你没有及时去跟对手说话选「是」，所以 {wishWait.PartnerName} 那边的比赛没有开始。"
                )
                : wishWait.Kind == WishConfirmFleaGame
                ? Lang.Pick(
                    $"You didn't say yes at the same game in time, so the game of {wishWait.PartnerName} didn't start.",
                    $"你没有及时去同一个游戏那里选「是」，所以 {wishWait.PartnerName} 那边的游戏没有开始。"
                )
                : wishWait.Kind == WishConfirmAid
                ? Lang.Pick(
                    $"You didn't ask the same character for help in time, so {wishWait.PartnerName} gets no help.",
                    $"你没有及时去找同一个角色请求援助，所以 {wishWait.PartnerName} 那边的援助没有成立。"
                )
                : wishWait.Kind == WishConfirmOffer
                ? Lang.Pick(
                    $"You didn't say yes to the same character in time, so the fight {wishWait.PartnerName} said " +
                    "yes to didn't start.",
                    $"你没有及时去和同一个角色对话选「是」，所以 {wishWait.PartnerName} 那边没有开打。"
                )
                : Lang.Pick(
                    $"You didn't get to the same prompt in time, so {wishWait.PartnerName} didn't take it.",
                    $"你没有及时走到同一个选项，所以 {wishWait.PartnerName} 那边没有接下。"
                ));
        }

        if (_pendingConfirmAsk is { } pending) {
            if (now - pending.Started > WishConfirmTimeout) {
                _pendingConfirmAsk = null;
                SendConfirmAnswer(pending.PartnerId, pending.Update.Key, false);
            } else if (_partnerConfirm == null) {
                // A line of dialogue the partner shared holds the one box this question can be asked in, and between
                // the two of them it is the question that costs something. Left alone the line waits out its own
                // time, and then this waits out its own, and the partner is told no for a wish they have been
                // standing in front of the whole while. So the line gives way.
                if (IsReadingSharedDialogue?.Invoke() == true && !CanShowConfirmNow()) {
                    Logger.Info(
                        $"Ending the shared dialogue, so that the question {pending.PartnerName} is waiting on can " +
                        "be asked here"
                    );
                    EndSharedDialogue?.Invoke();
                }

                if (CanShowConfirmNow()) {
                    _pendingConfirmAsk = null;
                    ShowPartnerConfirm(pending.PartnerId, pending.PartnerName, pending.Update);
                }
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

        // A member standing at their own box is answered too, for the same reason: this game is not going to be
        // able to reach the same prompt any more.
        foreach (var wishWait in _partnerWishWaits.Values) {
            SendConfirmAnswer(wishWait.PartnerId, wishWait.Key, false);
        }

        _partnerWishWaits.Clear();

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
    /// A yes of the local player that waits for every other member to agree.
    /// </summary>
    private sealed class HeldConfirm {
        public HeldConfirm(
            List<(ushort Id, string Name)> members, ulong key, YesNoAction? action, YesNoBox box, object? callback,
            Action proceed, string what, string wish
        ) {
            Members = members;
            Key = key;
            Action = action;
            Box = box;
            Callback = callback;
            Proceed = proceed;
            What = what;
            WishKey = wish;
        }

        /// <summary>
        /// The wish this is about and what is being done with it, or an empty string when it is not about a wish at
        /// all. It is what the players are matched on: the same wish, the same thing done to it, answered at each of
        /// their own boxes.
        /// </summary>
        public string WishKey { get; }

        /// <summary>
        /// Who was asked, with their names, and who is told when this ends. Kept here rather than read from the
        /// pairing, which can be cleared while the button still waits - and the telling would then go nowhere.
        /// </summary>
        public List<(ushort Id, string Name)> Members { get; }

        /// <summary>
        /// The members who said yes, in answer to this one or at their own box.
        /// </summary>
        public HashSet<ushort> Yes { get; } = [];

        /// <summary>
        /// Whether every member who was asked said yes.
        /// </summary>
        public bool IsAgreed => Members.TrueForAll(member => Yes.Contains(member.Id));

        /// <summary>
        /// Whether a member was asked.
        /// </summary>
        public bool IsAsked(ushort id) => Members.Exists(member => member.Id == id);

        /// <summary>
        /// The names of the members who haven't said yes yet.
        /// </summary>
        public List<string> GetMissingNames() =>
            Members.Where(member => !Yes.Contains(member.Id)).Select(member => member.Name).ToList();

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
    /// A wish the partner has said yes to at their own box, waiting for the local player to say yes at theirs.
    /// </summary>
    private sealed class PartnerWishWait {
        public PartnerWishWait(ushort partnerId, string partnerName, ulong key, string kind, string wish, bool isRead) {
            PartnerId = partnerId;
            PartnerName = partnerName;
            Key = key;
            Kind = kind;
            Wish = wish;
            IsRead = isRead;
        }

        /// <summary>
        /// Whether they read to a step of dialogue that begins the wish, rather than said yes at a prompt. Such a step
        /// can't be answered no, so it is never told no either: it waits for this player to read to the same step.
        /// </summary>
        public bool IsRead { get; }

        /// <summary>
        /// The player waiting, who is answered.
        /// </summary>
        public ushort PartnerId { get; }

        /// <summary>
        /// Their name, for what is said to the local player.
        /// </summary>
        public string PartnerName { get; }

        /// <summary>
        /// The key their answer comes back with.
        /// </summary>
        public ulong Key { get; }

        /// <summary>
        /// Whether they are taking the wish or turning it in, so that saying yes to one is not taken for the other.
        /// </summary>
        public string Kind { get; }

        /// <summary>
        /// The wish itself.
        /// </summary>
        public string Wish { get; }

        /// <summary>
        /// When they started waiting.
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
