using System;
using System.Collections.Generic;
using GlobalEnums;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Deliveries in a checked two-player save: wishes whose item the player carries to a character, and which breaks on
/// the way. Both players carry the item of a delivery that they accepted, since the partner gets what the dialogue gave
/// (see CoopSave.WishTalk). Whoever gets to the character first turns it in without waiting, while the character doesn't
/// talk to the partner: the partner gets the turn-in at once, pays their copy and gets the reward too, and is brought to
/// the character with a short fade where that is safe. A delivery that breaks for one player still counts for both when
/// the partner delivers theirs, and fails once no player carries it anymore. The player whose item broke doesn't get the
/// delivery back as accepted, because its character takes in an accepted delivery without looking at the item.
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
    /// How far, in units, the local player may stand from the partner who turns in a delivery without being moved.
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
    /// Whether shared dialogue, like the lines of a delivery that the partner turns in, is shown to the local player and
    /// took control from them.
    /// </summary>
    public Func<bool>? IsReadingSharedDialogue { get; set; }

    /// <summary>
    /// Gives control back to the local player after they were brought to a delivery, or leaves it to shared dialogue
    /// that they still read, or null to give it back directly.
    /// </summary>
    public Action<HeroController>? GiveBackHeroControl { get; set; }

    /// <summary>
    /// Takes over the control that shared dialogue took from the local player when the screen fades to bring them to a
    /// delivery, so that closing the dialogue meanwhile doesn't give it back, or null.
    /// </summary>
    public Action? TakeControlFromSharedDialogue { get; set; }

    /// <summary>
    /// The deliveries whose item broke for the local player during this session, by wish. The partner delivering such a
    /// delivery rewards the local player too, also when the break completed the wish without a reward, as long as the
    /// wish stayed as the break left it.
    /// </summary>
    private readonly Dictionary<string, BrokenDelivery> _brokenDeliveries = new(StringComparer.Ordinal);

    /// <summary>
    /// The deliveries that the local player turned in during this session, with the packed state of the wish right
    /// after, which a break of the partner that crossed the turn-in doesn't undo.
    /// </summary>
    private readonly Dictionary<string, int> _deliveredWishes = new(StringComparer.Ordinal);

    /// <summary>
    /// The completions from dialogue of the partner that the local save took during this session, with the packed state
    /// of each wish that came with them. What the same dialogue changes afterwards comes with that state again, and
    /// counts if it matches.
    /// </summary>
    private readonly Dictionary<string, int> _appliedPartnerCompletions = new(StringComparer.Ordinal);

    /// <summary>
    /// The full wishes of the game by name, found when first needed.
    /// </summary>
    private readonly Dictionary<string, FullQuestBase> _questsByName = new(StringComparer.Ordinal);

    /// <summary>
    /// Bringing the local player to a delivery that the partner turns in, or null.
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
        public BrokenDelivery(int value, ulong report) {
            Value = value;
            Report = report;
        }

        /// <summary>
        /// The packed state of the wish right after the break.
        /// </summary>
        public int Value { get; }

        /// <summary>
        /// The sequence of the report of the break to the partner, which the answer of the partner names.
        /// </summary>
        public ulong Report { get; }

        /// <summary>
        /// Whether the partner answered that they still carry theirs.
        /// </summary>
        public bool StillCarried { get; set; }
    }

    /// <summary>
    /// Bringing the local player to a delivery that the partner turns in.
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
        /// The partner who turns in the delivery.
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
        /// Where the partner stood when they turned in the delivery.
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
    /// Tells the partner that the item of a delivery broke for the local player if that cancelled the delivery in the
    /// local save. The partner decides whether the delivery still counts, so it doesn't go to them as a normal change of
    /// the wish log.
    /// </summary>
    private void NoticeDeliveryBreak(FullQuestBase quest, int before) {
        var name = quest.name;
        if (GetPackedWish(name) is not { } after || after == before || GetCheckedPartner() is not { } partner) {
            return;
        }

        _knownWishes[name] = after;
        var report = new CoopSaveUpdate {
            TargetId = partner.Id,
            Kind = CoopSaveUpdateKind.DeliveryBreak,
            PartCount = DeliveryBreakReport,
            WishNames = [name],
            WishValues = [after]
        };
        Send(report);
        _brokenDeliveries[name] = new BrokenDelivery(after, report.Sequence);
        Logger.Info($"The item of the delivery '{name}' broke, telling {partner.Username}");
    }

    /// <summary>
    /// The item of a delivery broke for the partner, or the partner answered whether they still carry a delivery whose
    /// item broke for the local player.
    /// </summary>
    private void OnDeliveryBreak(ClientPlayerData player, CoopSaveUpdate update) {
        var playerData = PlayerData.instance;
        if (playerData == null || GetCurrentMarker() is not { } marker || !IsPartner(player, marker)) {
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
                if (!_brokenDeliveries.TryGetValue(name, out var broken) || broken.Report != update.Key) {
                    continue;
                }

                if (update.PartCount == DeliveryStillCarried && !broken.StillCarried) {
                    broken.StillCarried = true;
                    Chat(
                        Lang.Pick(
                            $"Your delivery broke, but {player.Username} still carries theirs. If they deliver it, you " +
                            "get the reward too.",
                            $"你要送的东西坏了，但 {player.Username} 手上那份还在。只要对方送到，你也能拿到奖励。"
                        )
                    );
                } else if (update.PartCount == DeliveryFailed) {
                    _brokenDeliveries.Remove(name);
                    Chat(Lang.Pick(
                        $"{player.Username} doesn't carry this delivery either, so it failed.",
                        $"{player.Username} 手上也没有这份要送的东西，所以这次托运失败了。"
                    ));
                }
            }
        } catch (Exception e) {
            LogDeliveryError(e);
        }
    }

    /// <summary>
    /// The item of a delivery broke for the partner. While the local player still carries theirs, the delivery stays
    /// accepted in the local save and still counts for both, and a delivery that the local player turned in meanwhile
    /// counts for the partner through that turn-in. Otherwise it failed, and the local wish log takes the state of the
    /// partner.
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
        if (_deliveredWishes.TryGetValue(name, out var delivered) && delivered == PackCompletion(local)) {
            Logger.Info($"The delivery '{name}' broke for {player.Username} after the local player turned it in");
            return;
        }

        // A delivery that the local player turned in already goes to the partner with the dialogue
        if (local.IsCompleted && (value & WishCompleted) == 0) {
            return;
        }

        if (local.IsAccepted && !local.IsCompleted && IsCarriedDelivery(FindQuest(name))) {
            _brokenDeliveries.Remove(name);
            SendDeliveryAnswer(player, report, name, value, DeliveryStillCarried);
            Chat(
                Lang.Pick(
                    $"{player.Username}'s delivery broke, but yours is still intact. If you deliver it, you both get the " +
                    "reward.",
                    $"{player.Username} 要送的东西坏了，但你那份还好好的。只要你送到，你们两个都能拿到奖励。"
                )
            );
            Logger.Info($"The delivery '{name}' broke for {player.Username}, but the local player still carries it");
            return;
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

    #region Turning in

    /// <summary>
    /// Starts recording dialogue that can turn in a delivery, which needs nobody else. The character doesn't talk to the
    /// partner meanwhile, so that they don't turn in the same delivery a second time.
    /// </summary>
    private void StartDeliveryTalk(PlayMakerNPC npc, HashSet<PlayMakerFSM> fsms) {
        EndWishTalk();
        var talk = new WishTalk(npc, fsms, false) { IsDelivery = true };
        _wishTalk = talk;
        if (GetCheckedPartner() is { } partner) {
            Send(CreateWishTalkUpdate(partner.Id, talk.Scene, talk.Path, WishTalkStarted));
        }

        Logger.Info($"Dialogue with '{npc.name}' can turn in a delivery");
    }

    /// <summary>
    /// Sends dialogue that just completed a delivery to the partner at once, so that the copy of the partner can't break
    /// while the dialogue goes on, and brings the partner to the character. The reward that the dialogue gives
    /// afterwards goes along, and isn't recorded again. What else the dialogue changes afterwards goes to the partner
    /// when it ends, together with the delivery that it belongs to.
    /// </summary>
    private void SendDelivery(WishTalk talk, FullQuestBase quest) {
        var name = quest.name;
        var index = talk.Changes.LastIndexOf(name);
        if (index < 0) {
            return;
        }

        var value = talk.ChangeValues[index];
        var reward = quest.RewardItem;
        var count = quest.RewardCount;
        if (reward != null && reward is not BasicQuestBase && count > 0 && GivesQuestReward(talk, quest)) {
            var change = GetItemChangeKey(GetItemChange, reward);
            talk.AddItem(change, count, false);
            talk.CreditedGains.Add(change);
        }

        _deliveredWishes[name] = value;
        SendTalkChanges(talk, GetCurrentMarker() is { } marker ? FindPartner(marker) : null);
        talk.ClearChanges();
        talk.AddWish(quest, value);
        talk.SentChanges = talk.Changes.Count;
        Logger.Info($"The delivery '{name}' was turned in, sent to the partner at once");

        SendDeliverySummon(talk);
    }

    /// <summary>
    /// Asks the game of the partner to bring the partner to the character that took in a delivery.
    /// </summary>
    private void SendDeliverySummon(WishTalk talk) {
        var hero = HeroController.instance;
        if (GetCheckedPartner() is not { } partner || hero == null) {
            return;
        }

        var position = (Vector2) hero.transform.position;
        Send(new CoopSaveUpdate {
            TargetId = partner.Id,
            Kind = CoopSaveUpdateKind.DeliverySummon,
            Scene = talk.Scene,
            ObjectPath = talk.Path,
            Records = [GetSummonGate(talk.Scene, position)],
            Values = [position.x, position.y]
        });
        Logger.Info($"Bringing {partner.Username} to the delivery at '{talk.Path}'");
    }

    /// <summary>
    /// Whether an FSM of dialogue gives the reward of a wish from the wish itself, which is what the partner gets too.
    /// </summary>
    private static bool GivesQuestReward(WishTalk talk, FullQuestBase quest) {
        foreach (var component in talk.Fsms) {
            if (component != null && component.Fsm is { } fsm && HasQuestReward(fsm, quest, 0)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an FSM, or a template FSM that it runs, gets the reward of a wish.
    /// </summary>
    private static bool HasQuestReward(Fsm fsm, FullQuestBase quest, int depth) {
        foreach (var state in fsm.States ?? []) {
            foreach (var action in state?.Actions ?? []) {
                if (action is QuestPlaymakerActions.GetQuestReward or QuestPlaymakerActions.GetQuestRewardV2) {
                    if (((QuestPlaymakerActions.QuestFsmAction) action).Quest?.Value == quest) {
                        return true;
                    }
                } else if (action != null && depth < 2 && GetTemplateFsm(action) is { } template &&
                           HasQuestReward(template, quest, depth + 1)) {
                    return true;
                }
            }
        }

        return false;
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
    /// The partner turned in a delivery, so the local player is brought to its character once that is safe.
    /// </summary>
    private void OnDeliverySummon(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker) || _checkedWith != player.Id ||
            _summon is { Stage: not SummonStage.Waiting }) {
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
    /// Brings the local player to a delivery of the partner step by step: waits until they can be moved, goes to the
    /// scene of the delivery, and moves the hero next to the partner while the screen is dark.
    /// </summary>
    private void UpdateDeliverySummon(HeroController hero, ClientPlayerData? partner) {
        if (_summon is not { } summon) {
            return;
        }

        try {
            if (partner == null || partner.Id != summon.PlayerId) {
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
    /// or a boss room that waits, at a bench, in other dialogue or in a race stays, and gets the delivery all the same.
    /// </summary>
    private void StartSummonMove(HeroController hero, ClientPlayerData partner, PendingSummon summon, float now) {
        var gameManager = global::GameManager.instance;
        var playerData = PlayerData.instance;
        if (gameManager == null || playerData == null) {
            return;
        }

        if (hero.cState.dead || hero.cState.hazardDeath || hero.cState.hazardRespawning || IsInBossFight() ||
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
                return;
            }

            StartSummonFade(hero, summon, now);
            Chat(Lang.Pick(
            $"{partner.Username} turned in a delivery, so you are brought there.",
            $"{partner.Username} 交了一份托运，所以你被带过去了。"
        ));
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
        Chat(Lang.Pick(
            $"{partner.Username} turned in a delivery, so you are brought there.",
            $"{partner.Username} 交了一份托运，所以你被带过去了。"
        ));
        Logger.Info($"Bringing the local player to the delivery of {partner.Username} in '{summon.Scene}'");
    }

    /// <summary>
    /// Whether nothing that passes keeps the hero from being moved right now, like a scene change, an open menu,
    /// swimming, a lift ride or something that took control from them. Shared dialogue that the local player reads, like
    /// the lines of the delivery, takes control too, but doesn't keep them.
    /// </summary>
    private bool CanMoveHeroNow(global::GameManager gameManager, HeroController hero) {
        if (gameManager.GameState != GameState.PLAYING || gameManager.IsInSceneTransition ||
            gameManager.IsGamePaused() || hero.cState.transitioning || hero.cState.swimming || IsRidingLift(hero)) {
            return false;
        }

        return IsReadingSharedDialogue?.Invoke() == true || (!hero.controlReqlinquished && hero.CanInput());
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

        var gameManager = global::GameManager.instance;
        if (gameManager == null || SceneUtil.GetCurrentSceneName() != summon.Scene ||
            !gameManager.HasFinishedEnteringScene || hero.cState.dead || hero.cState.hazardDeath ||
            hero.cState.hazardRespawning || !CanMoveHeroNow(gameManager, hero)) {
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
    /// Fades the screen out before the hero is moved, while the hero takes no input and no damage. Control that shared
    /// dialogue took from the hero stays taken until the move is done, also when the dialogue closes meanwhile.
    /// </summary>
    private void StartSummonFade(HeroController hero, PendingSummon summon, float now) {
        TakeControlFromSharedDialogue?.Invoke();
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
    /// it gives control back itself.
    /// </summary>
    private static bool IsHeroTakenByGame(HeroController hero) {
        return hero.cState.dead || hero.cState.hazardDeath || hero.cState.hazardRespawning ||
               hero.cState.transitioning ||
               (global::GameManager.instance != null && global::GameManager.instance.IsInSceneTransition);
    }

    /// <summary>
    /// Where the local player is brought to for a delivery: where the partner stands.
    /// </summary>
    private static Vector2 GetSummonTarget(ClientPlayerData partner, PendingSummon summon) {
        return partner.IsInLocalScene && partner.PlayerObject != null
            ? (Vector2) partner.PlayerObject.transform.position
            : summon.Position;
    }

    /// <summary>
    /// Moves the hero to the partner, facing the character that takes in the delivery.
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
    /// Leaves the local player where they are for a delivery of the partner, which counts for them all the same.
    /// </summary>
    private void SkipSummon(ClientPlayerData partner) {
        _summon = null;
        Chat(
            Lang.Pick(
                $"{partner.Username} turned in a delivery. You can't be brought there right now, but it counts for you too.",
                $"{partner.Username} 交了一份托运。现在没法把你带过去，不过这份也算你的。"
            )
        );
    }

    /// <summary>
    /// Ends bringing the local player to a delivery, giving the hero back and fading the screen in if it was dark.
    /// Shared dialogue that the local player still reads keeps control until it is read, and after a death, a hazard or
    /// a scene change the game gives control back itself.
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
    /// Forgets deliveries and gives the hero back, for a new session. What broke, was turned in or came from the partner
    /// outlasts a new check, since the packed state that each of them keeps tells whether it still holds.
    /// </summary>
    private void ResetDeliveries() {
        FinishSummon(HeroController.instance);
        _brokenDeliveries.Clear();
        _deliveredWishes.Clear();
        _appliedPartnerCompletions.Clear();
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
