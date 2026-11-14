using System;
using System.Collections.Generic;
using System.Linq;
using GlobalEnums;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Deliveries in a checked co-op save: wishes whose item a player carries to a character (a target that is a
/// DeliveryQuestItem), and which breaks on the way. Every member carries the item of a delivery that they accepted,
/// since the members get what the dialogue gave (see CoopSave.WishTalk).
///
/// Handing the item in and completing the wish are two things. The user's words: "交肉本来就是祈愿的一部分，最后一个交的
/// 不一定是肉，也有可能是别的", "所以要把交肉和祈愿完成分开" (USER 11-14).
///
/// Whoever hands the item in first hands it in for every member. The moment it leaves their inventory, like the meat
/// among the ingredients of the cook's servant, each member's save crosses off what was handed in and loses its own
/// copy of the item, and each member is brought to the character with a short fade where that is safe. A member who
/// can't be moved right now isn't, and the hand-in counts for them all the same. A plain delivery, whose wish takes the
/// item only as it ends, is handed in by reaching that step. What else the character takes, like the other
/// ingredients, counts for every member too, as the dialogue ends; what it gives stays each player's own, from their
/// own dialogue.
///
/// The step of dialogue that ends the wish then waits the way a step that begins one does (CoopSave.WishRead): each
/// member talks to the character in their own game and sees their own lines and show, whoever reads to the step first
/// stands there, and once the last member's dialogue reaches it, all go on and every game ends the wish itself and
/// gives its own reward. With more players it waits for every member. A member who doesn't get there in time
/// (WishConfirmTimeout, and for as long as they talk to the same character) doesn't keep the wish from the others: the
/// game that waited ends it in its own save alone, and the wish stays open in the member's, as one that the saves
/// differ in, for them to end in their own game with their own reward.
///
/// The lines of the player who turned a delivery in used to be copied to the members, which took their control and
/// brought them over only once the show was over (USER 11-14, "她在我交食材的时候就动不了了，但是一直到动画结束才传送
/// 过来，但是实际上应该我交了肉就传送过来", "对话框共享，这是什么古早设计", "我记得很早之前就说把这个设计移除了"). Nothing
/// of the dialogue of a delivery is copied any more.
///
/// A delivery that breaks for one player still counts for all while a member carries theirs, and fails once no member
/// carries it anymore. The player whose item broke doesn't get the delivery back as accepted, because its character
/// takes in an accepted delivery without looking at the item.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The value of <see cref="CoopSaveUpdate.PartCount"/> for a delivery whose item broke for the sender.
    /// </summary>
    private const ushort DeliveryBreakReport = 0;

    /// <summary>
    /// The value of <see cref="CoopSaveUpdate.PartCount"/> for the answer that the sender still carries a delivery whose
    /// item broke for the other player.
    /// </summary>
    private const ushort DeliveryStillCarried = 1;

    /// <summary>
    /// The value of <see cref="CoopSaveUpdate.PartCount"/> for the answer that the sender doesn't carry a delivery whose
    /// item broke for the other player either, so it failed.
    /// </summary>
    private const ushort DeliveryFailed = 2;

    /// <summary>
    /// An ask that the sender's dialogue stands at the step that ends a delivery wish, which waits for every member to
    /// read to the same step (see <see cref="HoldsDeliveryEnd"/>). It carries WishConfirmRead after its kind.
    /// </summary>
    private const string WishConfirmDeliveryEnd = "deliveryend";

    /// <summary>
    /// How long, in seconds, the screen fades out and in when the local player is brought to a delivery.
    /// </summary>
    private const float SummonFadeTime = 0.3f;

    /// <summary>
    /// How long, in seconds, bringing the local player to a delivery waits for moments like a menu or a scene change to
    /// pass.
    /// </summary>
    private const float SummonWaitTime = 5f;

    /// <summary>
    /// How long, in seconds, bringing the local player to a delivery in another scene waits for them to get there.
    /// </summary>
    private const float SummonArriveTime = 20f;

    /// <summary>
    /// How far, in units, the local player may stand from the member who handed in a delivery without being moved.
    /// </summary>
    private const float SummonNearDistance = 3f;

    /// <summary>
    /// What keeps the hero from getting hurt while the screen is dark for a delivery.
    /// </summary>
    private static readonly object SummonInvulnerability = new();

    /// <summary>
    /// Whether the local player is behind the closed gates of an arena, which they aren't taken out of for a delivery.
    /// </summary>
    public Func<bool>? IsHeroLockedInArena { get; set; }

    /// <summary>
    /// Whether the local player is in a boss room that waits for the other players, which they aren't taken out of for a
    /// delivery either.
    /// </summary>
    public Func<bool>? IsHeroInWaitingBossRoom { get; set; }

    /// <summary>
    /// Whether shared dialogue, the lines of a boss that the scene host shares, is shown to the local player and took
    /// control from them.
    /// </summary>
    public Func<bool>? IsReadingSharedDialogue { get; set; }

    /// <summary>
    /// Gives control back to the local player after the co-op save took it for a moment, like to bring them to a
    /// delivery or to pull them up, or leaves it to shared dialogue that they still read, or null to give it back
    /// directly.
    /// </summary>
    public Action<HeroController>? GiveBackHeroControl { get; set; }

    /// <summary>
    /// Ends shared dialogue that is shown to the local player, counting it as read and closing its box, so that
    /// something that has to have the box to itself can have it, or null.
    /// </summary>
    public Action? EndSharedDialogue { get; set; }

    /// <summary>
    /// The deliveries whose item broke for the local player during this session, by wish. The partner delivering such a
    /// delivery rewards the local player too, also when the break completed the wish without a reward, as long as the
    /// wish stayed as the break left it.
    /// </summary>
    private readonly Dictionary<string, BrokenDelivery> _brokenDeliveries = new(StringComparer.Ordinal);

    /// <summary>
    /// The deliveries that the local player handed in during this session, with the packed state of the wish then,
    /// which a break of a member that crossed the hand-in doesn't undo.
    /// </summary>
    private readonly Dictionary<string, int> _handedInDeliveries = new(StringComparer.Ordinal);

    /// <summary>
    /// The wishes that the local player completed during this session, with the packed state of each wish right after
    /// and when. A completion of the partner's that comes for one of them while it is still so, within the time that a
    /// dialogue of the partner's going on meanwhile can take to come (see WishTogetherTime), was made at the same time:
    /// each player paid and was rewarded by their own turn-in. Taken for a wish that the save had completed long before,
    /// both players heard that the other had turned in a wish that they had long completed (USER 10-30,
    /// "我们一起完成的时候会显示xxx完成的这个祈愿你早就完成了").
    /// </summary>
    private readonly Dictionary<string, (int Packed, float Time)> _wishesCompletedHere = new(StringComparer.Ordinal);

    /// <summary>
    /// The full wishes of the game by name, found when first needed.
    /// </summary>
    private readonly Dictionary<string, FullQuestBase> _questsByName = new(StringComparer.Ordinal);

    /// <summary>
    /// Bringing the local player to a delivery that a member handed in, or null.
    /// </summary>
    private PendingSummon? _summon;

    /// <summary>
    /// Whether syncing deliveries threw, which is only logged once.
    /// </summary>
    private bool _deliveryFailed;

    /// <summary>
    /// How far bringing the local player to a delivery got.
    /// </summary>
    private enum SummonStage {
        /// <summary>
        /// Waiting until the local player can be moved.
        /// </summary>
        Waiting,

        /// <summary>
        /// Going to the scene of the delivery.
        /// </summary>
        Loading,

        /// <summary>
        /// The screen fades out before the hero is moved.
        /// </summary>
        FadingOut,

        /// <summary>
        /// The screen fades in after the hero was moved.
        /// </summary>
        FadingIn
    }

    /// <summary>
    /// A delivery whose item broke for the local player.
    /// </summary>
    private sealed class BrokenDelivery {
        public BrokenDelivery(int value, ulong report, IEnumerable<ushort> members) {
            Value = value;
            Report = report;
            Members = [..members];
        }

        /// <summary>
        /// The members who were told of the break, whose answers say whether the delivery still counts.
        /// </summary>
        public HashSet<ushort> Members { get; }

        /// <summary>
        /// The members who said that they still carry theirs, as long as nothing broke theirs since.
        /// </summary>
        public HashSet<ushort> CarriedBy { get; } = [];

        /// <summary>
        /// The members who said that they don't carry theirs either.
        /// </summary>
        public HashSet<ushort> FailedBy { get; } = [];

        /// <summary>
        /// The packed state of the wish right after the break.
        /// </summary>
        public int Value { get; }

        /// <summary>
        /// The sequence of the report of the break to the partner, which the answer of the partner names.
        /// </summary>
        public ulong Report { get; }

        /// <summary>
        /// Whether a member answered that they still carry theirs, which the local player was told.
        /// </summary>
        public bool StillCarried { get; set; }
    }

    /// <summary>
    /// Bringing the local player to a delivery that a member handed in.
    /// </summary>
    private sealed class PendingSummon {
        public PendingSummon(ushort playerId, string scene, string npcPath, string gate, Vector2 position) {
            PlayerId = playerId;
            Scene = scene;
            NpcPath = npcPath;
            Gate = gate;
            Position = position;
        }

        /// <summary>
        /// The member who handed in the delivery.
        /// </summary>
        public ushort PlayerId { get; }

        /// <summary>
        /// The scene of the character that takes in the delivery.
        /// </summary>
        public string Scene { get; }

        /// <summary>
        /// The path of the character in its scene.
        /// </summary>
        public string NpcPath { get; }

        /// <summary>
        /// The door of that scene to come in through from another scene, or empty.
        /// </summary>
        public string Gate { get; }

        /// <summary>
        /// Where the member stood when they handed in the delivery.
        /// </summary>
        public Vector2 Position { get; }

        /// <summary>
        /// How far bringing the local player got.
        /// </summary>
        public SummonStage Stage { get; set; }

        /// <summary>
        /// When the current stage started.
        /// </summary>
        public float StageTime { get; set; } = Time.unscaledTime;

        /// <summary>
        /// Whether the hero was made to not take input or damage while the screen is dark.
        /// </summary>
        public bool TookHero { get; set; }
    }

    /// <summary>
    /// Registers the hooks for deliveries whose item breaks.
    /// </summary>
    private void RegisterDeliveryHooks() {
        AddWishTalkHook(
            typeof(DeliveryQuestItem).GetMethod(
                "TakeHitForItem",
                StaticFlags,
                null,
                [typeof(DeliveryQuestItem.ActiveItem), typeof(bool), typeof(int)],
                null
            ),
            new Action<Action<DeliveryQuestItem.ActiveItem, bool, int>, DeliveryQuestItem.ActiveItem, bool, int>(
                OnDeliveryTakeHit
            )
        );
        AddWishTalkHook(
            typeof(DeliveryQuestItem).GetMethod("BreakAllInternal", StaticFlags, null, [typeof(bool), typeof(bool)], null),
            new Action<Action<bool, bool>, bool, bool>(OnDeliveryBreakAll)
        );
    }

    #region Breaking

    /// <summary>
    /// Hook for DeliveryQuestItem.TakeHitForItem, whose last hit breaks the item and cancels its delivery.
    /// </summary>
    private void OnDeliveryTakeHit(
        Action<DeliveryQuestItem.ActiveItem, bool, int> orig,
        DeliveryQuestItem.ActiveItem item,
        bool hitEffect,
        int amount
    ) {
        var quest = item.Quest;
        var before = quest != null ? GetPackedWish(quest.name) : null;
        orig(item, hitEffect, amount);

        if (quest == null || before is not { } value) {
            return;
        }

        try {
            NoticeDeliveryBreak(quest, value);
        } catch (Exception e) {
            LogDeliveryError(e);
        }
    }

    /// <summary>
    /// Hook for DeliveryQuestItem.BreakAllInternal, which breaks every item that the hero carries, like when they die.
    /// </summary>
    private void OnDeliveryBreakAll(Action<bool, bool> orig, bool withEffects, bool onlyTimed) {
        List<(FullQuestBase Quest, int Value)>? before = null;
        try {
            foreach (var item in DeliveryQuestItem.GetActiveItems()) {
                if (item.Quest != null && GetPackedWish(item.Quest.name) is { } value) {
                    before ??= [];
                    before.Add((item.Quest, value));
                }
            }
        } catch (Exception e) {
            LogDeliveryError(e);
        }

        orig(withEffects, onlyTimed);

        if (before == null) {
            return;
        }

        try {
            foreach (var (quest, value) in before) {
                NoticeDeliveryBreak(quest, value);
            }
        } catch (Exception e) {
            LogDeliveryError(e);
        }
    }

    /// <summary>
    /// Tells the members that the item of a delivery broke for the local player if that cancelled the delivery in the
    /// local save. The members decide whether the delivery still counts, so it doesn't go to them as a normal change of
    /// the wish log.
    /// </summary>
    private void NoticeDeliveryBreak(FullQuestBase quest, int before) {
        var name = quest.name;
        if (GetPackedWish(name) is not { } after || after == before || _checkedMembers.Count == 0) {
            return;
        }

        _knownWishes[name] = after;
        var report = new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.DeliveryBreak,
            PartCount = DeliveryBreakReport,
            WishNames = [name],
            WishValues = [after]
        };
        SendToMembers(report);
        _brokenDeliveries[name] = new BrokenDelivery(after, report.Sequence, _checkedMembers);
        Logger.Info($"The item of the delivery '{name}' broke, telling {GetCheckedNames()}");
    }

    /// <summary>
    /// The item of a delivery broke for a member, or a member answered whether they still carry a delivery whose item
    /// broke for the local player. It failed once every member said they don't carry theirs either.
    /// </summary>
    private void OnDeliveryBreak(ClientPlayerData player, CoopSaveUpdate update) {
        var playerData = PlayerData.instance;
        if (playerData == null || GetCurrentMarker() is not { } marker || !IsMember(player, marker) ||
            !IsFromMemberInSave(player)) {
            return;
        }

        try {
            for (var i = 0; i < update.WishNames.Count && i < update.WishValues.Count; i++) {
                var name = update.WishNames[i];
                if (update.PartCount == DeliveryBreakReport) {
                    OnPartnerDeliveryBroke(player, playerData, update, name, update.WishValues[i]);
                    continue;
                }

                // An answer to an earlier break, like one that the network delivered late, changes nothing
                if (!_brokenDeliveries.TryGetValue(name, out var broken) || broken.Report != update.Key ||
                    !broken.Members.Contains(player.Id)) {
                    continue;
                }

                if (update.PartCount == DeliveryStillCarried) {
                    broken.CarriedBy.Add(player.Id);
                    if (!broken.StillCarried) {
                        broken.StillCarried = true;
                        Chat(
                            Lang.Pick(
                                $"Your delivery broke, but {player.Username} still carries theirs. If they deliver it, " +
                                "you get the reward too.",
                                $"你要送的东西坏了，但 {player.Username} 手上那份还在。只要对方送到，你也能拿到奖励。"
                            )
                        );
                    }
                } else if (update.PartCount == DeliveryFailed) {
                    broken.FailedBy.Add(player.Id);

                    // Failed only once no member carries theirs any more
                    if (broken.CarriedBy.Count > 0 || !broken.Members.All(broken.FailedBy.Contains)) {
                        continue;
                    }

                    _brokenDeliveries.Remove(name);
                    var failed = JoinNames(
                        broken.Members.Select(id => _playerData.TryGetValue(id, out var member) ? member.Username : "?")
                    );
                    Chat(Lang.Pick(
                        broken.Members.Count == 1
                            ? $"{failed} doesn't carry this delivery either, so it failed."
                            : $"{failed} don't carry this delivery either, so it failed.",
                        $"{failed} 手上也没有这份要送的东西，所以这次托运失败了。"
                    ));
                }
            }
        } catch (Exception e) {
            LogDeliveryError(e);
        }
    }

    /// <summary>
    /// The item of a delivery broke for a member. While the local player still carries theirs, or another member said
    /// they still carry theirs, the delivery stays accepted in the local save and still counts for all, and a delivery
    /// that the local player handed in meanwhile counts for the member through that hand-in. Otherwise it failed, and
    /// the local wish log takes the state of the member.
    /// </summary>
    private void OnPartnerDeliveryBroke(
        ClientPlayerData player,
        PlayerData playerData,
        CoopSaveUpdate report,
        string name,
        int value
    ) {
        if (!IsNewerChange(_wishSequences, report, GetWishChangeKey(name, value))) {
            return;
        }

        var local = playerData.QuestCompletionData.GetData(name);
        if (_handedInDeliveries.TryGetValue(name, out var handedIn) && handedIn == PackCompletion(local)) {
            Logger.Info(
                $"[Deliveries] The delivery '{name}' broke for {player.Username} after the local player handed it in"
            );
            return;
        }

        // A delivery that the local player completed already goes to the partner with the dialogue
        if (local.IsCompleted && (value & WishCompleted) == 0) {
            return;
        }

        if (local.IsAccepted && !local.IsCompleted && IsCarriedDelivery(FindQuest(name))) {
            _brokenDeliveries.Remove(name);
            SendDeliveryAnswer(player, report, name, value, DeliveryStillCarried);
            Chat(
                _checkedMembers.Count <= 1
                    ? Lang.Pick(
                        $"{player.Username}'s delivery broke, but yours is still intact. If you deliver it, you both " +
                        "get the reward.",
                        $"{player.Username} 要送的东西坏了，但你那份还好好的。只要你送到，你们两个都能拿到奖励。"
                    )
                    : Lang.Pick(
                        $"{player.Username}'s delivery broke, but yours is still intact. If you deliver it, you all " +
                        "get the reward.",
                        $"{player.Username} 要送的东西坏了，但你那份还好好的。只要你送到，大家都能拿到奖励。"
                    )
            );
            Logger.Info($"The delivery '{name}' broke for {player.Username}, but the local player still carries it");
            return;
        }

        // Another member who said that they still carry theirs keeps the delivery going for the local player too.
        // The member whose item broke now is no longer one of them.
        if (_brokenDeliveries.TryGetValue(name, out var mine)) {
            mine.CarriedBy.Remove(player.Id);
            mine.FailedBy.Add(player.Id);
            if (mine.CarriedBy.Count > 0) {
                SendDeliveryAnswer(player, report, name, value, DeliveryFailed);
                Logger.Info($"The delivery '{name}' broke for {player.Username}, and another member still carries it");
                return;
            }

            // A member who hasn't answered the break of the local player yet may still carry theirs. Failing it now
            // dropped their answer that they do, and both games said it failed while it was still being carried. It
            // fails with the last of the answers instead (OnDeliveryBreak).
            if (!mine.Members.All(mine.FailedBy.Contains)) {
                SendDeliveryAnswer(player, report, name, value, DeliveryFailed);
                Logger.Info(
                    $"The delivery '{name}' broke for {player.Username} too, which waits for the answers of the others"
                );
                return;
            }
        }

        var wasActive = _brokenDeliveries.Remove(name) || (local.IsAccepted && !local.IsCompleted);
        var changed = 0;
        var accepted = 0;
        var completed = 0;
        ApplyPartnerWish(playerData, name, value, ref changed, ref accepted, ref completed);
        if (changed > 0) {
            QuestManager.IncrementVersion();
        }

        SendDeliveryAnswer(player, report, name, value, DeliveryFailed);
        if (wasActive) {
            Chat(Lang.Pick(
                $"{player.Username}'s delivery broke too, so the delivery failed.",
                $"{player.Username} 那份也坏了，所以这次托运失败了。"
            ));
        }

        Logger.Info($"The delivery '{name}' broke for {player.Username}, and no player carries it anymore");
    }

    /// <summary>
    /// Answers the partner whether the local player still carries a delivery whose item broke for the partner.
    /// </summary>
    private void SendDeliveryAnswer(
        ClientPlayerData player,
        CoopSaveUpdate report,
        string name,
        int value,
        ushort answer
    ) {
        Send(new CoopSaveUpdate {
            TargetId = player.Id,
            Kind = CoopSaveUpdateKind.DeliveryBreak,
            Key = report.Sequence,
            PartCount = answer,
            WishNames = [name],
            WishValues = [value]
        });
    }

    /// <summary>
    /// Undoes an accept of a delivery from the partner in the local wish log while the local player doesn't carry its
    /// item, because its character takes in an accepted delivery without looking at the item. The wish goes back to the
    /// state that it had before, which the local game keeps as known.
    /// </summary>
    /// <param name="playerData">The player data of the local save.</param>
    /// <param name="name">The name of the wish.</param>
    /// <param name="before">The state of the wish before the change of the partner.</param>
    /// <returns>Whether the accept was undone.</returns>
    private bool RefuseUncarriedDelivery(PlayerData playerData, string name, QuestCompletionData.Completion before) {
        if (before.IsAccepted && !before.IsCompleted) {
            return false;
        }

        var wish = playerData.QuestCompletionData.GetData(name);
        if (!wish.IsAccepted || wish.IsCompleted || !IsUncarriedDelivery(name)) {
            return false;
        }

        playerData.QuestCompletionData.SetData(name, before);
        _knownWishes[name] = PackCompletion(before);
        Logger.Info($"The delivery '{name}' isn't accepted in the local save, whose player doesn't carry its item");
        return true;
    }

    #endregion

    #region Handing in

    /// <summary>
    /// Starts recording dialogue that can take in a delivery wish, which needs nobody else. The members hear who talks
    /// to the character, so that a step that ends the wish keeps waiting for them while they do.
    /// </summary>
    private void StartDeliveryTalk(PlayMakerNPC npc, HashSet<PlayMakerFSM> fsms) {
        EndWishTalk();
        var talk = new WishTalk(npc, fsms, false) { IsDelivery = true };
        _wishTalk = talk;
        _lastTalk = talk;
        if (_checkedMembers.Count > 0) {
            SendToMembers(CreateWishTalkUpdate(CoopTargets.Everyone, talk.Scene, talk.Path, WishTalkStarted));
        }

        Logger.Info($"[Deliveries] Dialogue with '{npc.name}' can take in a delivery");
    }

    /// <summary>
    /// Notes that the item of a delivery wish left the local player's inventory in dialogue that can take it in, like
    /// the meat that the cook's servant takes among the ingredients before the wish ends. That is the hand-in, which
    /// goes to the members once the step that took it is through, with what the same step set, like the mark that the
    /// ingredient was given (see <see cref="SendPendingHandIn"/>). The end of the wish taking the item is no hand-in.
    /// </summary>
    private void NoteDeliveryItemTaken(WishTalk talk, SavedItem? item) {
        if (!talk.IsDelivery || _endingWishDepth > 0 || item is not DeliveryQuestItem delivery ||
            _checkedMembers.Count == 0 || FindDeliveryWishOf(delivery) is not { } quest) {
            return;
        }

        talk.HandIn = quest;
    }

    /// <summary>
    /// The delivery wish open in the local save that takes an item, or null.
    /// </summary>
    private static FullQuestBase? FindDeliveryWishOf(DeliveryQuestItem item) {
        foreach (var basicQuest in QuestManager.GetAllQuests()) {
            if (basicQuest is FullQuestBase quest && quest != null && quest.IsAccepted && !quest.IsCompleted &&
                quest.Targets.Any(target => ReferenceEquals(target.Counter, item))) {
                return quest;
            }
        }

        return null;
    }

    /// <summary>
    /// Sends the hand-in that dialogue noted, if any, now that the step that took the item is through.
    /// </summary>
    private void SendPendingHandIn(WishTalk talk) {
        if (talk.HandIn is not { } quest) {
            return;
        }

        talk.HandIn = null;
        SendHandIn(talk, quest);
    }

    /// <summary>
    /// Hands a delivery in for every member: their saves take what the dialogue changed so far, like the mark that an
    /// ingredient was given and the items that it took, and each member's own copy of the item of the delivery goes,
    /// also one that the end of the wish takes only later. Then the members are brought to the character. What the
    /// dialogue changed so far goes only this once, and no completion goes with it: each game ends the wish itself.
    /// </summary>
    private void SendHandIn(WishTalk talk, FullQuestBase quest) {
        var name = quest.name;
        if (GetPackedWish(name) is { } value) {
            _handedInDeliveries[name] = value;
        }

        // All of each member's copy goes, however much of it they hold: one that took fewer hits on the way has more
        var update = CreateTalkUpdate(talk);
        foreach (var target in quest.Targets) {
            if (target.Counter is DeliveryQuestItem item && item != null) {
                update.ItemIds.Add(AllWishesIndex + "\n" + GetItemChangeKey(TakeItemChange, item));
                update.Amounts.Add(int.MaxValue);
            }
        }

        talk.ClearChanges();
        SendTalkUpdate(update);
        Logger.Info(
            $"[Deliveries] The local player handed in the delivery '{name}' at '{talk.Path}', sent to " +
            $"{GetCheckedNames()} with {update.ItemIds.Count} item changes and {update.FlagNames.Count} flags"
        );

        SendDeliverySummon(talk);
    }

    /// <summary>
    /// Asks the games of the members to bring each of them to the character that a delivery was handed in at. A member
    /// who is close by already stays where they are.
    /// </summary>
    private void SendDeliverySummon(WishTalk talk) {
        var hero = HeroController.instance;
        if (_checkedMembers.Count == 0 || hero == null) {
            return;
        }

        var position = (Vector2) hero.transform.position;
        SendToMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.DeliverySummon,
            Scene = talk.Scene,
            ObjectPath = talk.Path,
            Records = [GetSummonGate(talk.Scene, position)],
            Values = [position.x, position.y]
        });
        Logger.Info($"[Deliveries] Bringing {GetCheckedNames()} to the delivery at '{talk.Path}'");
    }

    /// <summary>
    /// Whether a wish is a delivery wish: one of its targets is the item of a delivery, like the item of a courier's
    /// delivery or the meat among the ingredients of the cook's servant.
    /// </summary>
    private static bool IsDeliveryWish(FullQuestBase quest) {
        return quest.Targets.Any(target => target.Counter is DeliveryQuestItem);
    }

    /// <summary>
    /// Whether the local player carries the item of a delivery that a wish takes, like the item of a delivery wish or an
    /// item that runs against time among what a wish gathers.
    /// </summary>
    private static bool IsCarriedDelivery(FullQuestBase? quest) {
        if (quest == null) {
            return false;
        }

        foreach (var target in quest.Targets) {
            if (target.Counter is DeliveryQuestItem item && item != null && item.CollectedAmount > 0) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a wish is a delivery that its item breaking cancels: its first target is a delivery item.
    /// </summary>
    private static bool IsBreakableDelivery(FullQuestBase quest) {
        var targets = quest.Targets;
        return targets.Count > 0 && targets[0].Counter is DeliveryQuestItem;
    }

    /// <summary>
    /// Whether a wish is a delivery whose item the local player doesn't carry, which isn't accepted for them.
    /// </summary>
    private bool IsUncarriedDelivery(string name) {
        return FindQuest(name) is { } quest && IsBreakableDelivery(quest) && !IsCarriedDelivery(quest);
    }

    /// <summary>
    /// Whether dialogue of the partner gives the local player the item of a delivery, with the change of a wish at an
    /// index or with all of its changes.
    /// </summary>
    private static bool GivesDeliveryItem(CoopSaveUpdate update, int index, FullQuestBase quest) {
        foreach (var target in quest.Targets) {
            if (target.Counter is not DeliveryQuestItem item || item == null) {
                continue;
            }

            var get = GetItemChangeKey(GetItemChange, item);
            var collect = GetItemChangeKey(CollectItemChange, item);
            for (var i = 0; i < update.ItemIds.Count && i < update.Amounts.Count; i++) {
                var entry = update.ItemIds[i];
                var split = entry.IndexOf('\n');
                if (split <= 0 || update.Amounts[i] <= 0 ||
                    !int.TryParse(entry.Substring(0, split), out var entryIndex) ||
                    (entryIndex != index && entryIndex != AllWishesIndex)) {
                    continue;
                }

                var change = entry.Substring(split + 1);
                if (change == get || change == collect) {
                    return true;
                }
            }
        }

        return false;
    }

    #endregion

    #region Ending

    /// <summary>
    /// Decides whether a step of delivery dialogue that ends a delivery wish waits for the members, the way a step that
    /// begins a wish waits for them (CoopSave.WishRead). Reaching it hands the delivery in when the local player still
    /// carries its item, which the end itself takes: a plain delivery. Meeting members who wait at the same step tells
    /// them to go on, and this step goes on too.
    /// </summary>
    /// <returns>Whether the step waits, in which case it doesn't run now.</returns>
    private bool HoldsDeliveryEnd(
        QuestPlaymakerActions.QuestFsmAction step,
        Action<QuestPlaymakerActions.QuestFsmAction> orig
    ) {
        if (step.Quest?.Value is not FullQuestBase quest || quest == null || !IsDeliveryWish(quest) ||
            PlayerData.instance is not { } playerData) {
            return false;
        }

        // A wish that the save doesn't have open ends the way the game always ends it
        var wish = quest.name;
        var completion = playerData.QuestCompletionData.GetData(wish);
        if (!completion.IsAccepted || completion.IsCompleted) {
            return false;
        }

        // Every member got to this same step first and stands there waiting for this game to get here too. All have
        // now, so they are told to go on and so does this step.
        var members = GetCheckedMembers();
        var waits = members.Count > 0 ? GetWishWaits((WishConfirmDeliveryEnd, wish)) : [];
        if (members.Count > 0 && members.TrueForAll(member => waits.Exists(wait => wait.PartnerId == member.Id))) {
            foreach (var wait in waits) {
                _partnerWishWaits.Remove(wait.PartnerId);
                SendConfirmAnswer(wait.PartnerId, wait.Key, true);
            }

            Chat(GetHeldStepGoesOnMessage(JoinNames(waits.Select(wait => wait.PartnerName)), members.Count, true));
            Logger.Info($"[Deliveries] Every member waited at the step that ends '{wish}', so all go on and end it");
            return false;
        }

        if (_applyingPartnerTalk || !_everChecked || members.Count == 0 || GetCurrentMarker() == null ||
            FindTalkFor(step, true) is not { } talk) {
            return false;
        }

        // One step waits at a time. One that waited and is entered again is simply waited at anew.
        if (_heldStep is { } older) {
            EndHeldStep(null, !ReferenceEquals(older.Step, step));
        }

        if (IsCarriedDelivery(quest)) {
            SendHandIn(talk, quest);
        }

        var ask = CreateConfirm(WishConfirmDeliveryEnd);
        ask.Records.Add(WishConfirmRead);
        ask.WishNames.Add(wish);
        var held = new HeldStep(
            members.ConvertAll(member => (member.Id, member.Username)), ask.Key, step, () => orig(step), wish,
            GetWishKey((WishConfirmDeliveryEnd, wish)), talk.Npc, true
        );

        // A member who read to it already goes on once the rest of them have
        foreach (var wait in waits) {
            held.Yes.Add(wait.PartnerId);
        }

        _heldStep = held;
        SendToMembers(ask);
        var missing = held.GetMissingNames();
        var missingNames = JoinNames(missing);
        Chat(members.Count == 1
            ? Lang.Pick(
                $"The wish here is completed once {missingNames} reads to this point too, by both of you at once. " +
                "What comes after it waits until then.",
                $"要等 {missingNames} 也读到这一步，这个愿望才会完成，你们两个一起完成；这一步后面的事也等到那时候。"
            )
            : Lang.Pick(
                $"The wish here is completed once {missingNames} {(missing.Count == 1 ? "reads" : "read")} to this " +
                "point too, by all of you at once. What comes after it waits until then.",
                $"要等 {missingNames} 也读到这一步，这个愿望才会完成，大家一起完成；这一步后面的事也等到那时候。"
            ));
        Logger.Info(
            $"[Deliveries] A step of dialogue ends the delivery '{wish}' in '{step.State?.Name}' of " +
            $"'{step.Fsm?.GameObjectName}', and waits for {missingNames} to read to it too"
        );
        return true;
    }

    /// <summary>
    /// What the local player is told when the dialogue left the step that ends a delivery wish while it waited.
    /// </summary>
    private static string GetDeliveryEndLeftMessage() {
        return Lang.Pick(
            "The dialogue went on without waiting, so the wish isn't completed here. It is once the others " +
            "complete it.",
            "对话没有停下来等，所以这个愿望在你这边没有完成；等其他人完成后会同步过来。"
        );
    }

    /// <summary>
    /// What the local player is told when members didn't read to the step that ends a delivery wish in time.
    /// </summary>
    /// <param name="missing">The names of the members who didn't.</param>
    /// <param name="memberCount">How many members the step waited for.</param>
    private static string GetDeliveryEndTimeoutMessage(string missing, int memberCount) {
        return memberCount == 1
            ? Lang.Pick(
                $"{missing} didn't read to this point in time, so the wish is completed here first. Theirs is " +
                "completed once they read the dialogue of the same character to this point.",
                $"{missing} 没有及时读到这一步，这个愿望先在你这边完成；对方和同一个角色把对话读到这里，也会在自己那边完成。"
            )
            : Lang.Pick(
                $"{missing} didn't read to this point in time, so the wish is completed here first. Theirs are " +
                "completed once each of them reads the dialogue of the same character to this point.",
                $"{missing} 没有及时读到这一步，这个愿望先在你这边完成；他们各自和同一个角色把对话读到这里，也会在自己那边完成。"
            );
    }

    /// <summary>
    /// What the local player is told when members are gone while the step that ends a delivery wish waits for them.
    /// </summary>
    /// <param name="names">The names of the members who are gone.</param>
    /// <param name="memberCount">How many members the step waited for.</param>
    private static string GetDeliveryEndGoneMessage(string names, int memberCount) {
        return memberCount == 1
            ? Lang.Pick(
                $"{names} is gone for now, so the wish is completed here first. Theirs is completed once they are " +
                "back and read the dialogue of the same character to this point.",
                $"{names} 暂时不在，这个愿望先在你这边完成；对方回来后和同一个角色把对话读到这里，也会在自己那边完成。"
            )
            : Lang.Pick(
                $"{names} left for now, so the wish is completed here first. Theirs are completed once each of them " +
                "is back and reads the dialogue of the same character to this point.",
                $"{names} 暂时不在，这个愿望先在你这边完成；他们回来后各自和同一个角色把对话读到这里，也会在自己那边完成。"
            );
    }

    /// <summary>
    /// What the local player is told when they left the room while the step that ends a delivery wish waited.
    /// </summary>
    /// <param name="missing">The names of the members who didn't read to it.</param>
    private static string GetDeliveryEndSceneLeftMessage(string missing) {
        return Lang.Pick(
            $"You left before {missing} read to that point, so the wish isn't completed yet. Talk to the character " +
            "again to complete it.",
            $"{missing} 还没读到那一步你就离开了，这个愿望还没完成。再去和那个角色说话就能完成。"
        );
    }

    /// <summary>
    /// A member ended a delivery wish in their own game without the local player, who didn't read to the step that ends
    /// it in time. It is theirs alone: the local save keeps it open, as one that the saves differ in, so that their
    /// completion doesn't come here without the reward, and the local player ends it in their own game with their own.
    /// </summary>
    private void KeepDeliveryWishOpen(ClientPlayerData player, string wish) {
        _differentWishNames.Add(wish);
        Chat(Lang.Pick(
            $"{player.Username} completed the wish on their side first. Yours is completed once you read the " +
            "dialogue of the same character to the end, with its reward.",
            $"{player.Username} 那边先完成了这个愿望。你和同一个角色把对话读到最后，你这边也会完成，奖励照样有。"
        ));
        Logger.Info(
            $"[Deliveries] {player.Username} ended '{wish}' without the local player, whose save keeps it open"
        );
    }

    #endregion

    #region Bringing the partner

    /// <summary>
    /// The door of the scene of a delivery that the partner comes in through from another scene: the one that the local
    /// player came in through, or else the one closest to them.
    /// </summary>
    private static string GetSummonGate(string scene, Vector2 position) {
        var gate = global::GameManager.instance != null ? global::GameManager.instance.GetEntryGateName() : null;
        if (!string.IsNullOrEmpty(gate) && GateExists(scene, gate!)) {
            return gate!;
        }

        string? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var door in UnityEngine.Object.FindObjectsByType<TransitionPoint>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None
                 )) {
            if (door == null || door.gameObject.scene.name != scene || !GateExists(scene, door.gameObject.name)) {
                continue;
            }

            var distance = ((Vector2) door.transform.position - position).sqrMagnitude;
            if (distance < nearestDistance) {
                nearestDistance = distance;
                nearest = door.gameObject.name;
            }
        }

        return nearest ?? "";
    }

    /// <summary>
    /// A member handed in a delivery, so the local player is brought to its character once that is safe. A newer one
    /// of another member takes the place of one that still waits.
    /// </summary>
    private void OnDeliverySummon(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker) ||
            !_checkedMembers.Contains(player.Id) || _summon is { Stage: not SummonStage.Waiting }) {
            return;
        }

        _summon = new PendingSummon(
            player.Id,
            update.Scene,
            update.ObjectPath,
            update.Records.Count > 0 ? update.Records[0] : "",
            update.Values.Count >= 2 ? new Vector2(update.Values[0], update.Values[1]) : Vector2.zero
        );
    }

    /// <summary>
    /// Brings the local player to a delivery of a member step by step: waits until they can be moved, goes to the scene
    /// of the delivery, and moves the hero next to the member while the screen is dark.
    /// </summary>
    private void UpdateDeliverySummon(HeroController hero) {
        if (_summon is not { } summon) {
            return;
        }

        try {
            if (!_checkedMembers.Contains(summon.PlayerId) || !_playerData.TryGetValue(summon.PlayerId, out var partner)) {
                FinishSummon(hero);
                return;
            }

            var now = Time.unscaledTime;
            switch (summon.Stage) {
                case SummonStage.Waiting:
                    StartSummonMove(hero, partner, summon, now);
                    break;
                case SummonStage.Loading:
                    UpdateSummonArrival(hero, partner, summon, now);
                    break;
                case SummonStage.FadingOut:
                    if (now - summon.StageTime < SummonFadeTime) {
                        break;
                    }

                    // Something that happened while the screen got dark, like a hazard, leaves the hero where they are
                    if (!CanPlaceSummonedHero(hero, partner, summon)) {
                        FinishSummon(hero);
                        break;
                    }

                    PlaceSummonedHero(hero, partner, summon);
                    ScreenFaderUtils.Fade(Color.black, Color.clear, SummonFadeTime);
                    summon.Stage = SummonStage.FadingIn;
                    summon.StageTime = now;
                    break;
                case SummonStage.FadingIn:
                    if (now - summon.StageTime >= SummonFadeTime) {
                        FinishSummon(hero);
                    }

                    break;
            }
        } catch (Exception e) {
            LogDeliveryError(e);
            FinishSummon(hero);
        }
    }

    /// <summary>
    /// Starts bringing the local player to a delivery once nothing keeps them where they are. A player who is in a fight
    /// or a boss room that waits, at a bench, in dialogue or in a race stays, and the hand-in counts for them all the
    /// same.
    /// </summary>
    private void StartSummonMove(HeroController hero, ClientPlayerData partner, PendingSummon summon, float now) {
        var gameManager = global::GameManager.instance;
        var playerData = PlayerData.instance;
        if (gameManager == null || playerData == null) {
            return;
        }

        if (hero.cState.dead || PlayerTargetRegistry.IsPlayerDown(hero.gameObject) || hero.cState.hazardDeath ||
            hero.cState.hazardRespawning || IsInBossFight() ||
            IsHeroLockedInArena?.Invoke() == true || IsHeroInWaitingBossRoom?.Invoke() == true || playerData.atBench ||
            InteractManager.BlockingInteractable != null || IsRacing()) {
            SkipSummon(partner);
            return;
        }

        // Moments like a scene change, an open menu, a lift ride or the hero being busy pass
        if (!CanMoveHeroNow(gameManager, hero)) {
            if (now - summon.StageTime > SummonWaitTime) {
                SkipSummon(partner);
            }

            return;
        }

        if (SceneUtil.GetCurrentSceneName() == summon.Scene || partner.IsInLocalScene) {
            if (Vector2.Distance(hero.transform.position, GetSummonTarget(partner, summon)) <= SummonNearDistance) {
                _summon = null;
                Chat(Lang.Pick(
                    $"{partner.Username} handed in what was to be delivered, which counts for you too.",
                    $"{partner.Username} 交了要送的东西，你这份也算交了。"
                ));
                return;
            }

            StartSummonFade(hero, summon, now);
            ChatSummoned(partner);
            return;
        }

        if (summon.Gate.Length == 0 || !GateExists(summon.Scene, summon.Gate)) {
            SkipSummon(partner);
            return;
        }

        var started = BeginMove(hero, new global::GameManager.SceneLoadInfo {
            SceneName = summon.Scene,
            EntryGateName = summon.Gate,
            WaitForSceneTransitionCameraFade = true
        });
        if (!started) {
            if (now - summon.StageTime > SummonWaitTime) {
                SkipSummon(partner);
            }

            return;
        }

        summon.Stage = SummonStage.Loading;
        summon.StageTime = now;
        ChatSummoned(partner);
        Logger.Info(
            $"[Deliveries] Bringing the local player to the delivery of {partner.Username} in '{summon.Scene}'"
        );
    }

    /// <summary>
    /// Tells the local player that they are brought to a delivery that a member handed in.
    /// </summary>
    private void ChatSummoned(ClientPlayerData partner) {
        Chat(Lang.Pick(
            $"{partner.Username} handed in what was to be delivered, which counts for you too, so you are brought " +
            "there.",
            $"{partner.Username} 交了要送的东西，你这份也算交了，你被带过去了。"
        ));
    }

    /// <summary>
    /// Whether nothing that passes keeps the hero from being moved right now, like a scene change, an open menu,
    /// swimming, a lift ride or something that took control from them, like dialogue.
    /// </summary>
    private bool CanMoveHeroNow(global::GameManager gameManager, HeroController hero) {
        return gameManager.GameState == GameState.PLAYING && !gameManager.IsInSceneTransition &&
               !gameManager.IsGamePaused() && !hero.cState.transitioning && !hero.cState.swimming &&
               !IsRidingLift(hero) && !hero.controlReqlinquished && hero.CanInput();
    }

    /// <summary>
    /// Waits for the local player to come into the scene of a delivery, then moves them next to the partner unless they
    /// are close by already.
    /// </summary>
    private void UpdateSummonArrival(HeroController hero, ClientPlayerData partner, PendingSummon summon, float now) {
        if (now - summon.StageTime > SummonArriveTime) {
            _summon = null;
            return;
        }

        // Nor out of a cage that has them, which gives them their control back for a while: its black screen is the
        // others' to follow (CoopSave.PrisonCapture), not the fade of this move
        var gameManager = global::GameManager.instance;
        if (gameManager == null || SceneUtil.GetCurrentSceneName() != summon.Scene ||
            !gameManager.HasFinishedEnteringScene || hero.cState.dead || hero.cState.hazardDeath ||
            hero.cState.hazardRespawning || !CanMoveHeroNow(gameManager, hero) || _ownCatch != null) {
            return;
        }

        // The partner shows up a moment after the scene loaded
        if (!partner.IsInLocalScene || partner.PlayerObject == null) {
            return;
        }

        if (Vector2.Distance(hero.transform.position, GetSummonTarget(partner, summon)) <= SummonNearDistance) {
            _summon = null;
            return;
        }

        StartSummonFade(hero, summon, now);
    }

    /// <summary>
    /// Fades the screen out before the hero is moved, while the hero takes no input and no damage.
    /// </summary>
    private void StartSummonFade(HeroController hero, PendingSummon summon, float now) {
        hero.RelinquishControl();
        hero.AddInvulnerabilitySource(SummonInvulnerability);
        summon.TookHero = true;
        ScreenFaderUtils.Fade(Color.clear, Color.black, SummonFadeTime);
        summon.Stage = SummonStage.FadingOut;
        summon.StageTime = now;
    }

    /// <summary>
    /// Whether the hero can still be moved to the delivery once the screen is dark, since nothing like a hazard, death
    /// or a scene change happened meanwhile.
    /// </summary>
    private static bool CanPlaceSummonedHero(HeroController hero, ClientPlayerData partner, PendingSummon summon) {
        var gameManager = global::GameManager.instance;
        return gameManager != null && gameManager.GameState == GameState.PLAYING && !IsHeroTakenByGame(hero) &&
               (SceneUtil.GetCurrentSceneName() == summon.Scene || partner.IsInLocalScene);
    }

    /// <summary>
    /// Whether the game has the hero to itself right now, like during a death, a hazard or a scene change, after which
    /// it gives control back itself, or the hero is lying down waiting to be pulled up.
    /// </summary>
    private static bool IsHeroTakenByGame(HeroController hero) {
        return hero.cState.dead || PlayerTargetRegistry.IsPlayerDown(hero.gameObject) || hero.cState.hazardDeath ||
               hero.cState.hazardRespawning || hero.cState.transitioning ||
               (global::GameManager.instance != null && global::GameManager.instance.IsInSceneTransition);
    }

    /// <summary>
    /// Where the local player is brought to for a delivery: where the member who handed it in stands.
    /// </summary>
    private static Vector2 GetSummonTarget(ClientPlayerData partner, PendingSummon summon) {
        return partner.IsInLocalScene && partner.PlayerObject != null
            ? (Vector2) partner.PlayerObject.transform.position
            : summon.Position;
    }

    /// <summary>
    /// Moves the hero to the member who handed in the delivery, facing the character that took it.
    /// </summary>
    private static void PlaceSummonedHero(HeroController hero, ClientPlayerData partner, PendingSummon summon) {
        var target = GetSummonTarget(partner, summon);
        var position = hero.transform.position;
        position.x = target.x;
        position.y = target.y;
        hero.transform.position = position;

        var body = hero.GetComponent<Rigidbody2D>();
        if (body != null) {
            body.position = position;
        }

        var npc = ScenePath.Find(summon.NpcPath, summon.Scene);
        if (npc != null) {
            if (npc.transform.position.x < position.x) {
                hero.FaceLeft();
            } else {
                hero.FaceRight();
            }
        }

        if (GameCameras.instance != null && GameCameras.instance.cameraController != null) {
            GameCameras.instance.cameraController.PositionToHeroInstant(true);
        }
    }

    /// <summary>
    /// Leaves the local player where they are for a delivery that a member handed in, which counts for them all the
    /// same.
    /// </summary>
    private void SkipSummon(ClientPlayerData partner) {
        _summon = null;
        Chat(Lang.Pick(
            $"{partner.Username} handed in what was to be delivered, which counts for you too. You can't be brought " +
            "there right now.",
            $"{partner.Username} 交了要送的东西，你这份也算交了。现在没法把你带过去。"
        ));
    }

    /// <summary>
    /// Ends bringing the local player to a delivery, giving the hero back and fading the screen in if it was dark.
    /// Shared lines of a boss that the local player still reads keep control until they are read, and after a death, a
    /// hazard or a scene change the game gives control back itself.
    /// </summary>
    private void FinishSummon(HeroController? hero) {
        if (_summon is not { } summon) {
            return;
        }

        _summon = null;
        if (!summon.TookHero) {
            return;
        }

        if (hero != null) {
            hero.RemoveInvulnerabilitySource(SummonInvulnerability);
            if (!IsHeroTakenByGame(hero)) {
                if (GiveBackHeroControl != null) {
                    GiveBackHeroControl(hero);
                } else {
                    hero.RegainControl();
                }
            }
        }

        if (summon.Stage == SummonStage.FadingOut) {
            ScreenFaderUtils.Fade(Color.black, Color.clear, SummonFadeTime);
        }
    }

    /// <summary>
    /// Whether a race of the scene tracks the hero.
    /// </summary>
    private static bool IsRacing() {
        foreach (var race in UnityEngine.Object.FindObjectsByType<SprintRaceController>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None
                 )) {
            if (race != null && race.IsTracking) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the hero rides a lift of the room that moves right now.
    /// </summary>
    private bool IsRidingLift(HeroController hero) {
        foreach (var lift in _lifts.Values) {
            if (lift.Owner != null && lift.IsMoving && lift.ContainsHero(hero)) {
                return true;
            }
        }

        return false;
    }

    #endregion

    /// <summary>
    /// Ends a move of the hero for a delivery that a scene change interrupted, while a move to another scene goes on.
    /// </summary>
    private void OnDeliverySceneChanged() {
        if (_summon is { Stage: SummonStage.FadingOut or SummonStage.FadingIn }) {
            FinishSummon(HeroController.instance);
        }
    }

    /// <summary>
    /// Forgets deliveries and gives the hero back, for a new session.
    /// </summary>
    private void ResetDeliveries() {
        FinishSummon(HeroController.instance);
        _brokenDeliveries.Clear();
        _handedInDeliveries.Clear();
        _wishesCompletedHere.Clear();
        _questsByName.Clear();
    }

    /// <summary>
    /// The packed state of a wish in the local wish log, or null without a save.
    /// </summary>
    private static int? GetPackedWish(string name) {
        return PlayerData.instance is { } playerData
            ? PackCompletion(playerData.QuestCompletionData.GetData(name))
            : null;
    }

    /// <summary>
    /// Finds a full wish of the game by its name, or null.
    /// </summary>
    private FullQuestBase? FindQuest(string name) {
        if (_questsByName.TryGetValue(name, out var quest) && quest != null) {
            return quest;
        }

        _questsByName.Clear();
        foreach (var basicQuest in QuestManager.GetAllQuests()) {
            if (basicQuest is FullQuestBase fullQuest && fullQuest != null) {
                _questsByName[fullQuest.name] = fullQuest;
            }
        }

        return _questsByName.TryGetValue(name, out quest) ? quest : null;
    }

    /// <summary>
    /// Logs the first error of syncing deliveries.
    /// </summary>
    private void LogDeliveryError(Exception e) {
        if (!_deliveryFailed) {
            _deliveryFailed = true;
            Logger.Error($"Could not sync deliveries of the two-player save:\n{e}");
        }
    }
}
