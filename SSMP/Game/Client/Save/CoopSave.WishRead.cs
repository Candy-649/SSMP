using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Wishes that a character's dialogue takes on by itself rather than asking: the dialogue reaches a step that begins
/// the wish, shows that it was taken and goes on. In a checked two-player save such a wish follows the rule of a wish
/// that is asked about (CoopSave.WishConfirm): it is taken once both players have got there, each in their own game,
/// and never by one of them for the other.
///
/// The first one to read to the step stands there. Their dialogue goes no further - whatever the step does after the
/// wish, like opening the way out of a room, waits as well - and the partner is told. When the partner's dialogue
/// reaches the same step, both go on together and both games take the wish.
///
/// A dialogue can't be answered no, so a partner who doesn't get there doesn't keep the first player standing for
/// good. After the time a prompt waits (WishConfirmTimeout, counted from when the partner last read the dialogue of
/// the same character), the dialogue goes on without the wish, and the save remembers that its player read to it. The
/// wish is then taken in both games the moment the partner reads to the step too, or says yes to it at a prompt, and
/// two saves that both remember reading to it take it when they are next checked together.
///
/// This used to be left to the sync of the wish log, which copied the accept of whoever got there first into the other
/// game in the middle of their own dialogue. A dialogue that looks at the wish to tell a first hearing from a later one
/// then took the way of a later one for them and skipped what only the first hearing does: the second listener after
/// the two dancers wasn't let out of the room (USER 10-08).
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What an ask about taking a wish carries after its kind when the wish is begun by a step of dialogue rather than
    /// in a prompt. There is no answer to take back, so the partner's game keeps its word until the step goes on.
    /// </summary>
    private const string WishConfirmRead = "read";

    /// <summary>
    /// What an ask about taking a wish carries after its kind when it only asks whether the partner's save remembers
    /// reading to it too, as a check ends. Answered yes only by a save that does, and otherwise in silence.
    /// </summary>
    private const string WishConfirmReadCheck = "readcheck";

    /// <summary>
    /// How many wishes a save remembers that its player read to before the partner.
    /// </summary>
    private const int MaxWishesRead = 32;

    /// <summary>
    /// How long a wish that both players said yes to at a prompt is begun by a later step of the same dialogue without
    /// waiting again, in seconds.
    /// </summary>
    private const float AgreedAtPromptTime = 60f;

    /// <summary>
    /// How many waits that ended without the partner are remembered, for a partner's word that crossed the end.
    /// </summary>
    private const int MaxEndedBegins = 8;

    /// <summary>
    /// The step of dialogue that begins a wish and waits for the partner to read to the same step, or null.
    /// </summary>
    private HeldBegin? _heldBegin;

    /// <summary>
    /// The key dialogue that started last, kept after the character stops talking for as long as what it set going is
    /// still running: a dialogue can end with a scene that plays out first and begins the wish only after it, like a
    /// character fading out of the room. Null once its FSMs went back to waiting for the next talk.
    /// </summary>
    private WishTalk? _lastKeyTalk;

    /// <summary>
    /// Wishes that both players said yes to at their own prompt, by name, with when. Some dialogues ask first and begin
    /// the wish in a later step, which has nothing left to wait for.
    /// </summary>
    private readonly Dictionary<string, float> _wishesAgreedAtPrompt = new(StringComparer.Ordinal);

    /// <summary>
    /// The keys of waits that ended without the partner, with their wishes, newest last. A partner who got to the step
    /// just as the wait ended answers a key that no longer waits.
    /// </summary>
    private readonly List<(ulong Key, string Wish)> _endedBegins = [];

    /// <summary>
    /// The asks sent as a check ended about wishes that this save remembers reading to, by key.
    /// </summary>
    private readonly Dictionary<ulong, string> _readChecks = new();

    /// <summary>
    /// Hooks the start of the steps of FSMs that do something with a wish, of which only those that begin one are
    /// looked at.
    /// </summary>
    private void RegisterWishReadHook() {
        AddWishTalkHook(
            typeof(QuestPlaymakerActions.QuestFsmAction).GetMethod(
                "OnEnter", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null
            ),
            new Action<Action<QuestPlaymakerActions.QuestFsmAction>, QuestPlaymakerActions.QuestFsmAction>(
                OnWishStepEnter
            )
        );
    }

    /// <summary>
    /// Hook for the start of a step that does something with a wish. A step of key dialogue that begins a wish waits
    /// for the partner to read to the same step, and one that the partner is already waiting at goes on for both.
    /// </summary>
    private void OnWishStepEnter(
        Action<QuestPlaymakerActions.QuestFsmAction> orig,
        QuestPlaymakerActions.QuestFsmAction self
    ) {
        if (self is not (QuestPlaymakerActions.BeginQuest or QuestPlaymakerActions.BeginQuestV2)) {
            orig(self);
            return;
        }

        var waits = false;
        try {
            waits = HoldsBegin(self, orig);
        } catch (Exception e) {
            LogWishTalkError(e);
        }

        if (!waits) {
            orig(self);
        }
    }

    /// <summary>
    /// Decides whether a step that begins a wish waits for the partner. Meeting a partner who waits at the same wish
    /// tells them to go on, and this step goes on too.
    /// </summary>
    /// <returns>Whether the step waits, in which case it doesn't run now.</returns>
    private bool HoldsBegin(
        QuestPlaymakerActions.QuestFsmAction step,
        Action<QuestPlaymakerActions.QuestFsmAction> orig
    ) {
        if (step.Quest?.Value is not FullQuestBase quest || quest == null || PlayerData.instance is not { } playerData) {
            return false;
        }

        // A wish the save has already is begun again the way the game always does, which changes nothing both share
        var wish = quest.name;
        var completion = playerData.QuestCompletionData.GetData(wish);
        if (completion.IsAccepted || completion.IsCompleted) {
            return false;
        }

        // Both said yes to it at their own prompt a moment ago, and this is the step of the same dialogue that begins
        // it after the prompt. Waiting again would only ask what has been answered.
        if (_wishesAgreedAtPrompt.TryGetValue(wish, out var agreedAt) &&
            Time.unscaledTime - agreedAt < AgreedAtPromptTime) {
            ForgetWishRead(wish);
            Logger.Info($"Both players said yes to '{wish}' at their prompts, so the step that begins it goes on");
            return false;
        }

        // The partner got to this same wish first and stands there waiting for this game to get here too. Both have
        // now, so they are told to go on and so does this step. A word from an earlier pairing is nobody's any more.
        if (_partnerWishWait is { Kind: WishConfirmAccept } partnerAt && partnerAt.Wish == wish &&
            partnerAt.PartnerId == _checkedWith) {
            _partnerWishWait = null;
            SendConfirmAnswer(partnerAt.PartnerId, partnerAt.Key, true);
            ForgetWishRead(wish);
            Chat(Lang.Pick(
                $"{partnerAt.PartnerName} got to it too, so you both take the wish.",
                $"{partnerAt.PartnerName} 也到了这一步，你们一起接下这个愿望。"
            ));
            Logger.Info($"The partner waited at the wish '{wish}', which a step of dialogue begins here, so both go on");
            return false;
        }

        if (_applyingPartnerTalk || !_everChecked || _checkedWith is not { } partnerId || GetCurrentMarker() == null ||
            FindKeyTalkFor(step) is not { } talk) {
            return false;
        }

        // One step waits at a time. One that waited and is entered again is simply waited at anew, and finishing it
        // would finish the step that was entered just now.
        if (_heldBegin is { } older) {
            EndHeldBegin(null, !ReferenceEquals(older.Step, step));
        }

        // Kept from the start, so that a game that closes while the step waits still knows its player read to it
        NoteWishRead(wish);

        var ask = CreateConfirm(WishConfirmAccept);
        ask.Records.Add(WishConfirmRead);
        ask.WishNames.Add(wish);
        ask.TargetId = partnerId;
        _heldBegin = new HeldBegin(
            partnerId, ask.Key, step, () => orig(step), wish, GetWishKey((WishConfirmAccept, wish)), talk.Npc
        );
        Send(ask);
        Chat(Lang.Pick(
            $"The wish here is taken once {GetPartnerName()} reads to this point too, by both of you at once. What " +
            "comes after it waits until then.",
            $"要等 {GetPartnerName()} 也读到这一步，这个愿望才会接下，你们两个一起接；这一步后面的事也等到那时候。"
        ));
        Logger.Info(
            $"A step of dialogue begins the wish '{wish}' in '{step.State?.Name}' of '{step.Fsm?.GameObjectName}', " +
            "and waits for the partner to read to it too"
        );
        return true;
    }

    /// <summary>
    /// The key dialogue that a step belongs to: the one running now, or the last one while what it set going still
    /// runs, or null when the step belongs to no key dialogue.
    /// </summary>
    private WishTalk? FindKeyTalkFor(QuestPlaymakerActions.QuestFsmAction step) {
        if (_wishTalk is { IsKey: true } current && current.IsTalkFsm(step.Fsm)) {
            return current;
        }

        return _lastKeyTalk is { } last && last.IsTalkFsm(step.Fsm) && IsInConversation(last, GetHostFsm(step.Fsm))
            ? last
            : null;
    }

    /// <summary>
    /// The FSM that a step belongs to, or for a step of a template that an FSM runs, the FSM that runs it, whose state
    /// is the one the conversation is in.
    /// </summary>
    private static Fsm? GetHostFsm(Fsm? fsm) {
        return fsm?.Owner is PlayMakerFSM owner && owner != null && owner.Fsm != null ? owner.Fsm : fsm;
    }

    /// <summary>
    /// Whether an FSM of key dialogue is still in the conversation that talking to its character started: in a state
    /// the talk leads to, rather than back to waiting for the next talk.
    /// </summary>
    private bool IsInConversation(WishTalk talk, Fsm? fsm) {
        if (fsm == null || talk.Npc is not PlayMakerNPC npc || npc == null || fsm.ActiveStateName is not { } active) {
            return false;
        }

        var interactEvent = InteractEventField?.GetValue(npc) as string;
        if (string.IsNullOrEmpty(interactEvent)) {
            interactEvent = DefaultInteractEvent;
        }

        return GetTalkStates(fsm, interactEvent!).Contains(active);
    }

    /// <summary>
    /// Forgets the last key dialogue once none of its FSMs is in its conversation any more.
    /// </summary>
    private void UpdateLastKeyTalk() {
        if (_lastKeyTalk is not { } last || ReferenceEquals(last, _wishTalk)) {
            return;
        }

        foreach (var component in last.Fsms) {
            if (component != null && IsInConversation(last, component.Fsm)) {
                return;
            }
        }

        _lastKeyTalk = null;
    }

    /// <summary>
    /// The partner read to the same step: the step that waited goes on and takes the wish, the way it would have
    /// without the wait.
    /// </summary>
    /// <param name="message">What to tell the local player, or null to tell them nothing.</param>
    private void ReleaseHeldBegin(string? message) {
        if (_heldBegin is not { } held) {
            return;
        }

        _heldBegin = null;
        ForgetWishRead(held.Wish);
        if (message != null) {
            Chat(message);
        }

        if (IsStepLive(held.Step)) {
            Logger.Info($"Both players read to the step that begins '{held.Wish}', so it goes on");
            RunHeldStep(held);
            return;
        }

        // The dialogue went on by some other way meanwhile, so the wish is taken without it
        TakeReadWish(held.Wish);
    }

    /// <summary>
    /// Stops waiting for the partner at a step that begins a wish, without the partner having got there.
    /// </summary>
    /// <param name="message">What to tell the local player, or null to tell them nothing.</param>
    /// <param name="goOn">Whether the dialogue goes on from the step. Not when its scene is going, where going on
    /// would run what comes after the wish - like saving the game - on the way out.</param>
    private void EndHeldBegin(string? message, bool goOn) {
        if (_heldBegin is not { } held) {
            return;
        }

        _heldBegin = null;

        // The wish waits for the partner's own reading to it all the same, since this save remembers that its player
        // read to it. A word of theirs that crossed this end still counts.
        TellHeldBeginIsOver(held);
        _endedBegins.Add((held.Key, held.Wish));
        if (_endedBegins.Count > MaxEndedBegins) {
            _endedBegins.RemoveAt(0);
        }

        if (message != null) {
            Chat(message);
        }

        if (!goOn || !IsStepLive(held.Step) ||
            (global::GameManager.instance is { } gameManager && gameManager.IsInSceneTransition)) {
            Logger.Info($"Stopped waiting for the partner at the step that begins '{held.Wish}'");
            return;
        }

        // A save that stopped being a two-player save is its player's alone, and so is the wish
        if (GetCurrentMarker() == null) {
            Logger.Info($"The save isn't shared any more, so the step that begins '{held.Wish}' goes on and takes it");
            RunHeldStep(held);
            return;
        }

        // The dialogue goes on without the wish, which waits for the partner to read to the same step
        Logger.Info(
            $"The partner didn't read to the step that begins '{held.Wish}', so the dialogue goes on without it until " +
            "they do"
        );
        held.Step.Finish();
    }

    /// <summary>
    /// Ends the wait at a step that begins a wish once it can't go on as it is: the step was left, the save has the
    /// wish by now, the partner is gone, or the partner didn't read to the same step in time.
    /// </summary>
    private void UpdateHeldBegin() {
        if (_heldBegin is not { } held) {
            return;
        }

        // The FSM left the step some other way, like an event of its own a moment later, or went with its scene
        if (!IsStepLive(held.Step)) {
            EndHeldBegin(Lang.Pick(
                $"The dialogue went on without waiting, so the wish isn't taken yet. Once {GetPartnerName()} reads " +
                "it to the same point too, you both take it.",
                $"对话没有停下来等，所以这个愿望先不接。等 {GetPartnerName()} 也把这段对话读到同一步，你们会一起接下。"
            ), false);
            return;
        }

        // The save isn't shared any more, and the wish is its player's alone, or no save is loaded at all
        if (GetCurrentMarker() == null) {
            EndHeldBegin(null, IsInGame());
            return;
        }

        // The save got the wish meanwhile some other way, so the step has nothing left to wait for
        if (PlayerData.instance is { } playerData) {
            var completion = playerData.QuestCompletionData.GetData(held.Wish);
            if (completion.IsAccepted || completion.IsCompleted) {
                TellHeldBeginIsOver(held);
                ReleaseHeldBegin(null);
                return;
            }
        }

        if (_checkedWith != held.PartnerId) {
            EndHeldBegin(GetPartnerGoneMessage(), true);
            return;
        }

        // The partner reading the dialogue of the same character is on the way to the same step
        var now = Time.unscaledTime;
        if (_partnerTalk?.Npc is { } partnerNpc && partnerNpc != null && partnerNpc == held.Npc) {
            held.PartnerReadAt = now;
        }

        if (now - Mathf.Max(held.Started, held.PartnerReadAt) > WishConfirmTimeout) {
            EndHeldBegin(Lang.Pick(
                $"{GetPartnerName()} didn't read to this point, so the dialogue goes on and the wish isn't taken yet. " +
                "Once they read this dialogue to the same point too, you both take it.",
                $"{GetPartnerName()} 还没读到这一步，对话先继续，这个愿望先不接。等 {GetPartnerName()} 也把这段对话读到同一步，你们会一起接下。"
            ), true);
        }
    }

    /// <summary>
    /// What the local player is told when the partner is gone while a step waits for them.
    /// </summary>
    private string GetPartnerGoneMessage() {
        return Lang.Pick(
            $"{GetPartnerName()} is gone for now, so the dialogue goes on and the wish isn't taken yet. Once they " +
            "read this dialogue to the same point too, you both take it.",
            $"{GetPartnerName()} 暂时不在，对话先继续，这个愿望先不接。等 {GetPartnerName()} 也把这段对话读到同一步，你们会一起接下。"
        );
    }

    /// <summary>
    /// The partner said yes to a wait that already ended, or answered an ask sent as a check ended. Either way they
    /// got to the wish too, and a save that remembers reading to it takes it now.
    /// </summary>
    /// <returns>Whether the key was one of these.</returns>
    private bool AnswerEndedRead(ulong key, bool agreed) {
        string? wish = null;
        if (_readChecks.Remove(key, out var checkedWish)) {
            wish = checkedWish;
        } else {
            var index = _endedBegins.FindIndex(ended => ended.Key == key);
            if (index >= 0) {
                wish = _endedBegins[index].Wish;
                _endedBegins.RemoveAt(index);
            }
        }

        if (wish == null) {
            return false;
        }

        if (agreed && HasReadWish(wish)) {
            TakeReadWish(wish);
            Chat(Lang.Pick(
                $"{GetPartnerName()} got to the wish that you read to before, so you both took it.",
                $"{GetPartnerName()} 也到了你之前读到的那个愿望，你们一起接下了它。"
            ));
        }

        return true;
    }

    /// <summary>
    /// Asks the partner, as a check with them ends, about each wish that this save remembers reading to: a save that
    /// remembers reading to it too answers yes, and both take it. Two players who each read to it while the other
    /// couldn't hear never asked each other at a moment when both could answer.
    /// </summary>
    private void AskAboutWishesRead(ClientPlayerData partner) {
        // The check finishes whatever happens here
        try {
            if (GetCurrentMarker() is not { } marker || marker.WishesRead.Count == 0 ||
                PlayerData.instance is not { } playerData) {
                return;
            }

            foreach (var wish in marker.WishesRead.ToArray()) {
                // The check may have brought the wish in from the partner's save already
                var completion = playerData.QuestCompletionData.GetData(wish);
                if (completion.IsAccepted || completion.IsCompleted) {
                    ForgetWishRead(wish);
                    continue;
                }

                var ask = CreateConfirm(WishConfirmAccept);
                ask.Records.Add(WishConfirmReadCheck);
                ask.WishNames.Add(wish);
                ask.TargetId = partner.Id;
                _readChecks[ask.Key] = wish;
                Send(ask);
            }
        } catch (Exception e) {
            LogWishTalkError(e);
        }
    }

    /// <summary>
    /// Answers an ask of the partner about whether this save remembers reading to a wish too, taking it if it does.
    /// </summary>
    /// <returns>Whether the ask was one of these.</returns>
    private bool AnswerReadCheck(ClientPlayerData player, CoopSaveUpdate update) {
        if (update.Records.Count < 2 || update.Records[1] != WishConfirmReadCheck || update.WishNames.Count == 0) {
            return false;
        }

        var wish = update.WishNames[0];
        if (!HasReadWish(wish)) {
            SendConfirmAnswer(player.Id, update.Key, false);
            return true;
        }

        SendConfirmAnswer(player.Id, update.Key, true);
        TakeReadWish(wish);
        Chat(Lang.Pick(
            $"You and {player.Username} had both read to a wish while the other couldn't hear, so you both took it.",
            $"你和 {player.Username} 都读到过同一个愿望，之前没能对上，现在你们一起接下了它。"
        ));
        return true;
    }

    /// <summary>
    /// Remembers that both players said yes to a wish at their own prompt, which a later step of the same dialogue
    /// begins without waiting again.
    /// </summary>
    private void NoteWishAgreedAtPrompt(string wishKey) {
        var prefix = WishConfirmAccept + "\n";
        if (wishKey.StartsWith(prefix, StringComparison.Ordinal)) {
            _wishesAgreedAtPrompt[wishKey[prefix.Length..]] = Time.unscaledTime;
        }
    }

    /// <summary>
    /// Forgets the waits and answers of a session.
    /// </summary>
    private void ResetWishRead() {
        _lastKeyTalk = null;
        _wishesAgreedAtPrompt.Clear();
        _endedBegins.Clear();
        _readChecks.Clear();
    }

    /// <summary>
    /// Tells the partner's game that the local player no longer stands at a step that begins a wish, so that it stops
    /// keeping their word.
    /// </summary>
    private void TellHeldBeginIsOver(HeldBegin held) {
        Send(new CoopSaveUpdate {
            TargetId = held.PartnerId,
            Kind = CoopSaveUpdateKind.WishConfirm,
            PartCount = WishConfirmGone,
            Key = held.Key
        });
    }

    /// <summary>
    /// Runs a step that waited, with its FSM the one that runs, as it is while the FSM runs its own steps. A step that
    /// throws is finished all the same, or nothing after it would ever run.
    /// </summary>
    private void RunHeldStep(HeldBegin held) {
        var fsm = held.Step.Fsm;
        FsmExecutionStack.PushFsm(fsm);
        try {
            held.Proceed();
        } catch (Exception e) {
            LogWishTalkError(e);
            if (IsStepLive(held.Step)) {
                held.Step.Finish();
            }
        } finally {
            FsmExecutionStack.PopFsm();
        }
    }

    /// <summary>
    /// Whether a step still stands where it waits: its FSM is there and in its state, and it hasn't finished.
    /// PlayMaker leaves a state without telling the step when its FSM is destroyed or moved on by an event.
    /// </summary>
    private static bool IsStepLive(QuestPlaymakerActions.QuestFsmAction step) {
        var fsm = step.Fsm;
        return fsm != null && fsm.GameObject != null && fsm.ActiveState != null && fsm.ActiveState == step.State &&
               !step.Finished;
    }

    /// <summary>
    /// Takes a wish that the local player read to before, now that the partner got there too, without a step of the
    /// dialogue, which went on from there long ago.
    /// </summary>
    private void TakeReadWish(string wish) {
        ForgetWishRead(wish);
        if (FindQuest(wish) is not { } quest || PlayerData.instance is not { } playerData) {
            Logger.Warn($"Could not find the wish '{wish}' that both players read to");
            return;
        }

        var completion = playerData.QuestCompletionData.GetData(wish);
        if (completion.IsAccepted || completion.IsCompleted) {
            return;
        }

        // Not counted as dialogue of the local player, whatever they happen to be talking to right now
        var wasApplying = _applyingPartnerTalk;
        _applyingPartnerTalk = true;
        try {
            quest.BeginQuest(null, false);
        } finally {
            _applyingPartnerTalk = wasApplying;
        }

        Logger.Info($"Took the wish '{wish}', which the local player read to before and the partner did now");
    }

    /// <summary>
    /// Remembers that the local player read to the step that begins a wish.
    /// </summary>
    private void NoteWishRead(string wish) {
        if (GetCurrentMarker() is not { } marker || marker.WishesRead.Contains(wish)) {
            return;
        }

        marker.WishesRead.Add(wish);
        if (marker.WishesRead.Count > MaxWishesRead) {
            marker.WishesRead.RemoveRange(0, marker.WishesRead.Count - MaxWishesRead);
        }

        SaveWishesRead();
    }

    /// <summary>
    /// Whether the local player read to the step that begins a wish, which waits for the partner to read to it too.
    /// </summary>
    private bool HasReadWish(string wish) {
        return GetCurrentMarker() is { } marker && marker.WishesRead.Contains(wish);
    }

    /// <summary>
    /// Forgets that the local player read to the step that begins a wish, once the wish is taken.
    /// </summary>
    private void ForgetWishRead(string wish) {
        if (GetCurrentMarker() is not { } marker || !marker.WishesRead.Remove(wish)) {
            return;
        }

        SaveWishesRead();
    }

    private void SaveWishesRead() {
        try {
            SaveMarkers();
        } catch (Exception e) {
            LogWishTalkError(e);
        }
    }

    /// <summary>
    /// A step of key dialogue that begins a wish and waits for the partner to read to the same step.
    /// </summary>
    private sealed class HeldBegin {
        public HeldBegin(
            ushort partnerId, ulong key, QuestPlaymakerActions.QuestFsmAction step, Action proceed, string wish,
            string wishKey, NPCControlBase npc
        ) {
            PartnerId = partnerId;
            Key = key;
            Step = step;
            Proceed = proceed;
            Wish = wish;
            WishKey = wishKey;
            Npc = npc;
        }

        /// <summary>
        /// The partner who was told, and who is told when the wait ends.
        /// </summary>
        public ushort PartnerId { get; }

        /// <summary>
        /// The key that the partner's answer comes back with.
        /// </summary>
        public ulong Key { get; }

        /// <summary>
        /// The step that waits, whose start didn't run.
        /// </summary>
        public QuestPlaymakerActions.QuestFsmAction Step { get; }

        /// <summary>
        /// Runs the start of the step for real, which begins the wish and goes on the way it always does.
        /// </summary>
        public Action Proceed { get; }

        /// <summary>
        /// The name of the wish.
        /// </summary>
        public string Wish { get; }

        /// <summary>
        /// What the two players are matched on, the same as for a wish that is asked about.
        /// </summary>
        public string WishKey { get; }

        /// <summary>
        /// The character whose dialogue it is, which the partner reads on the way to the same step.
        /// </summary>
        public NPCControlBase Npc { get; }

        /// <summary>
        /// When the step started waiting.
        /// </summary>
        public float Started { get; } = Time.unscaledTime;

        /// <summary>
        /// When the partner was last seen reading the dialogue of the same character.
        /// </summary>
        public float PartnerReadAt { get; set; } = -1f;
    }
}
