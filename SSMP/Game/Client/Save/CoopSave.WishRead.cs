using System;
using System.Collections.Generic;
using System.Linq;
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
/// the wish, shows that it was taken and goes on. In a checked co-op save such a wish follows the rule of a wish that
/// is asked about (CoopSave.WishConfirm): it is taken once every player has got there, each in their own game, and
/// never by some of them for the others.
///
/// Whoever reads to the step before the last of them stands there. Their dialogue goes no further - whatever the step
/// does after the wish, like opening the way out of a room, waits as well - and the others are told. When the last
/// member's dialogue reaches the same step, all go on together and every game takes the wish.
///
/// A dialogue can't be answered no, so a member who doesn't get there doesn't keep the others standing for good. After
/// the time a prompt waits (WishConfirmTimeout, counted from when a member who is still to get there last read the
/// dialogue of the same character), the dialogue goes on without the wish, and the save remembers that its player read
/// to it. The wish is then taken the moment the last member reads to the step too, or says yes to it at a prompt, and
/// saves that all remember reading to it take it when they are next checked together.
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
    /// The waits that ended without every member, newest last, with who had got there. A member who got to the step
    /// just as the wait ended answers a key that no longer waits.
    /// </summary>
    private readonly List<ReadWait> _endedBegins = [];

    /// <summary>
    /// The asks sent as a check ended about wishes that this save remembers reading to, by key, with who answered yes.
    /// </summary>
    private readonly Dictionary<ulong, ReadWait> _readChecks = new();

    /// <summary>
    /// The members known during this session to have read to the step that begins a wish, by wish. A save that
    /// remembers reading to it takes it once every member is among them.
    /// </summary>
    private readonly Dictionary<string, HashSet<ushort>> _wishReadBy = new(StringComparer.Ordinal);

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

        // Every member got to this same wish first and stands there waiting for this game to get here too. All have
        // now, so they are told to go on and so does this step. A word from an earlier pairing is nobody's any more.
        var members = GetCheckedMembers();
        var waits = members.Count > 0 ? GetWishWaits((WishConfirmAccept, wish)) : [];
        if (members.Count > 0 && members.TrueForAll(member => waits.Exists(wait => wait.PartnerId == member.Id))) {
            foreach (var wait in waits) {
                _partnerWishWaits.Remove(wait.PartnerId);
                SendConfirmAnswer(wait.PartnerId, wait.Key, true);
            }

            ForgetWishRead(wish);
            var names = JoinNames(waits.Select(wait => wait.PartnerName));
            Chat(members.Count == 1
                ? Lang.Pick(
                    $"{names} got to it too, so you both take the wish.",
                    $"{names} 也到了这一步，你们一起接下这个愿望。"
                )
                : Lang.Pick(
                    $"{names} got to it too, so you all take the wish.",
                    $"{names} 也到了这一步，你们一起接下这个愿望。"
                ));
            Logger.Info($"Every member waited at the wish '{wish}', which a step of dialogue begins here, so all go on");
            return false;
        }

        if (_applyingPartnerTalk || !_everChecked || members.Count == 0 || GetCurrentMarker() == null ||
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
        var held = new HeldBegin(
            members.ConvertAll(member => (member.Id, member.Username)), ask.Key, step, () => orig(step), wish,
            GetWishKey((WishConfirmAccept, wish)), talk.Npc
        );

        // A member who read to it already said yes to it, and goes on once the rest of them have
        foreach (var wait in waits) {
            held.Yes.Add(wait.PartnerId);
        }

        _heldBegin = held;
        SendToMembers(ask);
        var missing = held.GetMissingNames();
        var missingNames = JoinNames(missing);
        Chat(members.Count == 1
            ? Lang.Pick(
                $"The wish here is taken once {missingNames} reads to this point too, by both of you at once. What " +
                "comes after it waits until then.",
                $"要等 {missingNames} 也读到这一步，这个愿望才会接下，你们两个一起接；这一步后面的事也等到那时候。"
            )
            : Lang.Pick(
                $"The wish here is taken once {missingNames} {(missing.Count == 1 ? "reads" : "read")} to this point " +
                "too, by all of you at once. What comes after it waits until then.",
                $"要等 {missingNames} 也读到这一步，这个愿望才会接下，大家一起接；这一步后面的事也等到那时候。"
            ));
        Logger.Info(
            $"A step of dialogue begins the wish '{wish}' in '{step.State?.Name}' of '{step.Fsm?.GameObjectName}', " +
            $"and waits for {missingNames} to read to it too"
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

        // The members who waited at it answered this wait, and go on with the yes of this player that reached them
        ForgetWishWaits(held.WishKey);
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
        _endedBegins.Add(new ReadWait(held.Key, held.Wish, held.Members.ConvertAll(member => member.Id), held.Yes));
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
            var notYet = held.GetMissingNames();
            EndHeldBegin(held.Members.Count == 1
                ? Lang.Pick(
                    $"The dialogue went on without waiting, so the wish isn't taken yet. Once {JoinNames(notYet)} reads " +
                    "it to the same point too, you both take it.",
                    $"对话没有停下来等，所以这个愿望先不接。等 {JoinNames(notYet)} 也把这段对话读到同一步，你们会一起接下。"
                )
                : Lang.Pick(
                    "The dialogue went on without waiting, so the wish isn't taken yet. Once everyone has read it to " +
                    "the same point, you take it together.",
                    "对话没有停下来等，所以这个愿望先不接。等大家都把这段对话读到同一步，你们会一起接下。"
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

        // A member who left can't get there now
        var gone = held.Members.Where(member => !_checkedMembers.Contains(member.Id)).ToList();
        if (gone.Count > 0) {
            EndHeldBegin(GetPartnerGoneMessage(JoinNames(gone.Select(member => member.Name)), held.Members.Count), true);
            return;
        }

        // A member who is still to get there and reads the dialogue of the same character is on the way to the same
        // step
        var now = Time.unscaledTime;
        foreach (var (id, _) in held.Members) {
            if (!held.Yes.Contains(id) && _partnerTalks.TryGetValue(id, out var partnerTalk) &&
                partnerTalk.Npc is { } partnerNpc && partnerNpc != null && partnerNpc == held.Npc) {
                held.PartnerReadAt = now;
                break;
            }
        }

        if (now - Mathf.Max(held.Started, held.PartnerReadAt) > WishConfirmTimeout) {
            var missing = JoinNames(held.GetMissingNames());
            EndHeldBegin(held.Members.Count == 1
                ? Lang.Pick(
                    $"{missing} didn't read to this point, so the dialogue goes on and the wish isn't taken yet. " +
                    "Once they read this dialogue to the same point too, you both take it.",
                    $"{missing} 还没读到这一步，对话先继续，这个愿望先不接。等 {missing} 也把这段对话读到同一步，你们会一起接下。"
                )
                : Lang.Pick(
                    $"{missing} didn't read to this point, so the dialogue goes on and the wish isn't taken yet. " +
                    "Once everyone has read this dialogue to the same point, you take it together.",
                    $"{missing} 还没读到这一步，对话先继续，这个愿望先不接。等大家都把这段对话读到同一步，你们会一起接下。"
                ), true);
        }
    }

    /// <summary>
    /// What the local player is told when members are gone while a step waits for them.
    /// </summary>
    /// <param name="names">The names of the members who are gone.</param>
    /// <param name="memberCount">How many members the step waited for.</param>
    private static string GetPartnerGoneMessage(string names, int memberCount) {
        return memberCount == 1
            ? Lang.Pick(
                $"{names} is gone for now, so the dialogue goes on and the wish isn't taken yet. Once they read this " +
                "dialogue to the same point too, you both take it.",
                $"{names} 暂时不在，对话先继续，这个愿望先不接。等 {names} 也把这段对话读到同一步，你们会一起接下。"
            )
            : Lang.Pick(
                $"{names} left for now, so the dialogue goes on and the wish isn't taken yet. Once everyone has read " +
                "this dialogue to the same point, you take it together.",
                $"{names} 暂时不在，对话先继续，这个愿望先不接。等大家都把这段对话读到同一步，你们会一起接下。"
            );
    }

    /// <summary>
    /// A member said yes to a wait that already ended, or answered an ask sent as a check ended. Either way they got to
    /// the wish too, and a save that remembers reading to it takes it once every member has.
    /// </summary>
    /// <returns>Whether the key was one of these.</returns>
    private bool AnswerEndedRead(ClientPlayerData player, ulong key, bool agreed) {
        var fromCheck = _readChecks.TryGetValue(key, out var wait);
        if (!fromCheck) {
            wait = _endedBegins.Find(ended => ended.Key == key);
        }

        if (wait == null) {
            return false;
        }

        if (!wait.Members.Contains(player.Id)) {
            return true;
        }

        // A no of one member keeps the wait for the rest: a yes of another that came after it was otherwise taken for
        // an answer to nothing, and their reading to the wish never written down
        if (agreed) {
            wait.Yes.Add(player.Id);
            NoteMemberReadWish(wait.Wish, player.Id);
        } else {
            wait.No.Add(player.Id);
        }

        if (!wait.IsAnswered) {
            Logger.Info(
                agreed
                    ? $"{player.Username} got to the wish '{wait.Wish}' too, which waits for the other members"
                    : $"{player.Username} didn't get to the wish '{wait.Wish}', which still hears from the others"
            );
            return true;
        }

        if (fromCheck) {
            _readChecks.Remove(key);
        } else {
            _endedBegins.Remove(wait);
        }

        if (agreed && wait.IsAgreed && HasReadWish(wait.Wish)) {
            TakeReadWish(wait.Wish);
            Chat(wait.Members.Count == 1
                ? Lang.Pick(
                    $"{player.Username} got to the wish that you read to before, so you both took it.",
                    $"{player.Username} 也到了你之前读到的那个愿望，你们一起接下了它。"
                )
                : Lang.Pick(
                    $"{player.Username} got to the wish that you read to before, the last of you to, so you all took it.",
                    $"{player.Username} 也到了你之前读到的那个愿望，大家都读到了，你们一起接下了它。"
                ));
        }

        return true;
    }

    /// <summary>
    /// Asks every member, as a check with them ends, about each wish that this save remembers reading to: a save that
    /// remembers reading to it too answers yes, and once all of them did, the wish is taken. Players who each read to
    /// it while the others couldn't hear never asked each other at a moment when all could answer.
    /// </summary>
    private void AskAboutWishesRead(List<ClientPlayerData> members) {
        // The check finishes whatever happens here
        try {
            if (members.Count == 0 || GetCurrentMarker() is not { } marker || marker.WishesRead.Count == 0 ||
                PlayerData.instance is not { } playerData) {
                return;
            }

            var ids = members.ConvertAll(member => member.Id);
            foreach (var wish in marker.WishesRead.ToArray()) {
                // The check may have brought the wish in from the save of a member already
                var completion = playerData.QuestCompletionData.GetData(wish);
                if (completion.IsAccepted || completion.IsCompleted) {
                    ForgetWishRead(wish);
                    continue;
                }

                var ask = CreateConfirm(WishConfirmAccept);
                ask.Records.Add(WishConfirmReadCheck);
                ask.WishNames.Add(wish);
                _readChecks[ask.Key] = new ReadWait(ask.Key, wish, ids, []);
                SendToMembers(ask);
            }
        } catch (Exception e) {
            LogWishTalkError(e);
        }
    }

    /// <summary>
    /// Answers an ask of a member about whether this save remembers reading to a wish too, taking it once every member
    /// is known to have read to it.
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
        NoteMemberReadWish(wish, player.Id);
        if (!HaveAllMembersReadWish(wish)) {
            return true;
        }

        TakeReadWish(wish);
        Chat(_checkedMembers.Count <= 1
            ? Lang.Pick(
                $"You and {player.Username} had both read to a wish while the other couldn't hear, so you both took it.",
                $"你和 {player.Username} 都读到过同一个愿望，之前没能对上，现在你们一起接下了它。"
            )
            : Lang.Pick(
                "All of you had read to a wish while the others couldn't hear, so you all took it.",
                "你们每个人都读到过同一个愿望，之前没能对上，现在大家一起接下了它。"
            ));
        return true;
    }

    /// <summary>
    /// Remembers that a member read to the step that begins a wish.
    /// </summary>
    private void NoteMemberReadWish(string wish, ushort id) {
        if (!_wishReadBy.TryGetValue(wish, out var readers)) {
            readers = _wishReadBy[wish] = [];
        }

        readers.Add(id);
    }

    /// <summary>
    /// Whether every checked member is known to have read to the step that begins a wish.
    /// </summary>
    private bool HaveAllMembersReadWish(string wish) {
        return _checkedMembers.Count > 0 && _wishReadBy.TryGetValue(wish, out var readers) &&
               _checkedMembers.All(readers.Contains);
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
        _wishReadBy.Clear();
    }

    /// <summary>
    /// Tells the games of the members that the local player no longer stands at a step that begins a wish, so that they
    /// stop keeping their word.
    /// </summary>
    private void TellHeldBeginIsOver(HeldBegin held) {
        foreach (var (id, _) in held.Members) {
            Send(new CoopSaveUpdate {
                TargetId = id,
                Kind = CoopSaveUpdateKind.WishConfirm,
                PartCount = WishConfirmGone,
                Key = held.Key
            });
        }
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
        _wishReadBy.Remove(wish);
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
    /// A step of key dialogue that begins a wish and waits for every member to read to the same step.
    /// </summary>
    private sealed class HeldBegin {
        public HeldBegin(
            List<(ushort Id, string Name)> members, ulong key, QuestPlaymakerActions.QuestFsmAction step,
            Action proceed, string wish, string wishKey, NPCControlBase npc
        ) {
            Members = members;
            Key = key;
            Step = step;
            Proceed = proceed;
            Wish = wish;
            WishKey = wishKey;
            Npc = npc;
        }

        /// <summary>
        /// The members who were told, with their names, who are told when the wait ends.
        /// </summary>
        public List<(ushort Id, string Name)> Members { get; }

        /// <summary>
        /// The members who got to the same step, or said yes to the wish at a prompt.
        /// </summary>
        public HashSet<ushort> Yes { get; } = [];

        /// <summary>
        /// Whether every member got there.
        /// </summary>
        public bool IsAgreed => Members.TrueForAll(member => Yes.Contains(member.Id));

        /// <summary>
        /// Whether a player is one of the members it waits for.
        /// </summary>
        public bool HasMember(ushort id) => Members.Exists(member => member.Id == id);

        /// <summary>
        /// The names of the members who haven't got there yet.
        /// </summary>
        public List<string> GetMissingNames() =>
            Members.Where(member => !Yes.Contains(member.Id)).Select(member => member.Name).ToList();

        /// <summary>
        /// The key that the answers of the members come back with.
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
        /// When a member who is still to get there was last seen reading the dialogue of the same character.
        /// </summary>
        public float PartnerReadAt { get; set; } = -1f;
    }

    /// <summary>
    /// A wish that this save read to and that waits for the members' word that they read to it too: a wait that ended,
    /// or an ask as a check ended.
    /// </summary>
    private sealed class ReadWait {
        public ReadWait(ulong key, string wish, List<ushort> members, IEnumerable<ushort> yes) {
            Key = key;
            Wish = wish;
            Members = members;
            Yes = [..yes];
        }

        /// <summary>
        /// The key that the answers come back with.
        /// </summary>
        public ulong Key { get; }

        /// <summary>
        /// The name of the wish.
        /// </summary>
        public string Wish { get; }

        /// <summary>
        /// The members it waits for.
        /// </summary>
        public List<ushort> Members { get; }

        /// <summary>
        /// The members who said they got there.
        /// </summary>
        public HashSet<ushort> Yes { get; }

        /// <summary>
        /// The members who said they didn't.
        /// </summary>
        public HashSet<ushort> No { get; } = [];

        /// <summary>
        /// Whether every member said they got there.
        /// </summary>
        public bool IsAgreed => Members.TrueForAll(Yes.Contains);

        /// <summary>
        /// Whether every member answered.
        /// </summary>
        public bool IsAnswered => Members.TrueForAll(member => Yes.Contains(member) || No.Contains(member));
    }
}
