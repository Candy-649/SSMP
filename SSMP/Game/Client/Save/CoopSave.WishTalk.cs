using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GlobalEnums;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Dialogue about wishes in a checked two-player save. Talking to a character who can offer a wish right now or take
/// one in is key dialogue: it only starts while the partner is close by, the partner reads the same lines, and the
/// character doesn't talk to the partner meanwhile. Whenever dialogue with a character who deals in wishes accepts or
/// completes a wish, the save of the partner gets that change of the wish together with what the dialogue took from the
/// local player and gave them, so both pay a full copy and both get the reward. Turning in a wish needs a full copy of
/// what it takes in both inventories, while progress that isn't taken, like kills, counts from either player. A wish
/// that only one save completed before is turned in by the other player with their own copy alone. A delivery that runs
/// against time is turned in by whoever gets there first. Other talk stays with the player who talks.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How far sideways from the local hero, in units, the partner may stand for key dialogue, about one screen.
    /// </summary>
    private const float WishTalkRangeX = 16f;

    /// <summary>
    /// How far up or down from the local hero, in units, the partner may stand for key dialogue.
    /// </summary>
    private const float WishTalkRangeY = 10f;

    /// <summary>
    /// How long, in seconds, dialogue about wishes still records after its character stopped talking and nothing
    /// changed, for what the character gives after a pause.
    /// </summary>
    private const float WishTalkEndDelay = 6f;

    /// <summary>
    /// How long, in seconds, dialogue about wishes records at most, so that a character that doesn't stop talking
    /// doesn't hold back its wishes for good.
    /// </summary>
    private const float WishTalkMaxTime = 600f;

    /// <summary>
    /// How long, in seconds, the character that the partner talks to stays locked for the local player at most.
    /// </summary>
    private const float PartnerTalkTimeout = 300f;

    /// <summary>
    /// How often, in seconds, the progress of the wishes is compared with what the partner last got.
    /// </summary>
    private const float WishProgressInterval = 1f;

    /// <summary>
    /// How many targets of wishes one update with progress holds at most.
    /// </summary>
    private const int WishProgressEntriesPerUpdate = 64;

    /// <summary>
    /// How long, in seconds, until the local player hears again that the partner lacks a full copy for a wish.
    /// </summary>
    private const float MissingCopyNoticeInterval = 10f;

    /// <summary>
    /// The most targets of a wish whose progress is kept for the partner.
    /// </summary>
    private const int MaxWishTargets = 64;

    /// <summary>
    /// The value of <see cref="CoopSaveUpdate.PartCount"/> for the end of key dialogue.
    /// </summary>
    private const ushort WishTalkEnded = 0;

    /// <summary>
    /// The value of <see cref="CoopSaveUpdate.PartCount"/> for the start of key dialogue.
    /// </summary>
    private const ushort WishTalkStarted = 1;

    /// <summary>
    /// The value of <see cref="CoopSaveUpdate.PartCount"/> for key dialogue that didn't start because the other player
    /// isn't close by.
    /// </summary>
    private const ushort WishTalkRefused = 2;

    /// <summary>
    /// An item that dialogue took: the type and name of the item follow.
    /// </summary>
    private const string TakeItemChange = "take";

    /// <summary>
    /// A saved item that dialogue gave: the type and name of the item follow.
    /// </summary>
    private const string GetItemChange = "get";

    /// <summary>
    /// An item that dialogue added to the collection: the type and name of the item follow.
    /// </summary>
    private const string CollectItemChange = "collect";

    /// <summary>
    /// Money that dialogue took or gave, with its amount negative when taken: the number of the currency follows.
    /// </summary>
    private const string CurrencyChange = "currency";

    /// <summary>
    /// A counter of the player data that dialogue changed, by the amount of the change: the name of the counter follows.
    /// </summary>
    private const string IntChange = "int";

    /// <summary>
    /// A tool that dialogue unlocked, like as a reward: the type and name of the tool follow.
    /// </summary>
    private const string ToolUnlockChange = "toolunlock";

    /// <summary>
    /// A tool that dialogue locked, like one that a wish took: the type and name of the tool follow.
    /// </summary>
    private const string ToolLockChange = "toollock";

    /// <summary>
    /// The index of the change of a wish that something of dialogue belongs to when it belongs to all of them, like a
    /// counter.
    /// </summary>
    private const int AllWishesIndex = -1;

    /// <summary>
    /// The event that characters send to their dialogue FSM when the hero talks to them, unless they name another.
    /// </summary>
    private const string DefaultInteractEvent = "INTERACT";

    private static readonly FieldInfo? DialogueFsmField = typeof(PlayMakerNPC).GetField("dialogueFsm", InstanceFlags);
    private static readonly FieldInfo? SecondaryFsmsField = typeof(PlayMakerNPC).GetField("secondaryFsms", InstanceFlags);
    private static readonly FieldInfo? InteractEventField = typeof(PlayMakerNPC).GetField("interactEvent", InstanceFlags);
    private static readonly FieldInfo? WaitingToBeginField = typeof(NPCControlBase).GetField("isWaitingToBegin", InstanceFlags);
    private static readonly FieldInfo? TemplateTargetField = typeof(FsmTemplateControl).GetField("target", InstanceFlags);
    private static readonly FieldInfo? TemplateFsmField = typeof(FsmTemplate).GetField("fsm", InstanceFlags);

    /// <summary>
    /// The field of FSM actions that run a template FSM which holds how they run it, by type of action, or null.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo?> TemplateControlFields = new();

    /// <summary>
    /// The saved items of the game by their type and name, found when first needed.
    /// </summary>
    private static readonly Dictionary<string, SavedItem> SavedItems = new(StringComparer.Ordinal);

    /// <summary>
    /// The hooks for dialogue about wishes, which stay for as long as the game runs.
    /// </summary>
    private readonly List<Hook> _wishTalkHooks = [];

    /// <summary>
    /// The dialogue with a character who deals in wishes that the local player is in, or null.
    /// </summary>
    private WishTalk? _wishTalk;

    /// <summary>
    /// The key dialogue that the partner is in, whose character doesn't talk to the local player meanwhile, or null.
    /// </summary>
    private PartnerTalk? _partnerTalk;

    /// <summary>
    /// The actions of dialogue FSMs that offer wishes and take them in, by FSM, found the first time.
    /// </summary>
    private readonly Dictionary<Fsm, List<WishAction>> _wishActions = new();

    /// <summary>
    /// The states of dialogue FSMs that a talk with their character goes through, by FSM, with the event that starts
    /// the talk.
    /// </summary>
    private readonly Dictionary<Fsm, (string Event, HashSet<string> States)> _talkStates = new();

    /// <summary>
    /// The progress of each target of the wishes in the save of the partner, by wish.
    /// </summary>
    private readonly Dictionary<string, int[]> _partnerWishProgress = new(StringComparer.Ordinal);

    /// <summary>
    /// The key of the update that the progress of each wish of the partner last came in, by wish.
    /// </summary>
    private readonly Dictionary<string, ulong> _partnerWishProgressKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// The progress of each target of the wishes that the partner last got, by wish.
    /// </summary>
    private readonly Dictionary<string, int[]> _sentWishProgress = new(StringComparer.Ordinal);

    /// <summary>
    /// Counts the updates with progress of wishes, whose keys tell newer ones from older ones.
    /// </summary>
    private ulong _wishProgressCounter;

    /// <summary>
    /// When the progress of the wishes is compared next.
    /// </summary>
    private float _nextWishProgressTime;

    /// <summary>
    /// When the local player can hear again that the partner lacks a full copy for a wish, by wish.
    /// </summary>
    private readonly Dictionary<string, float> _nextMissingCopyNotices = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the local game plays dialogue of the partner, whose changes aren't recorded for the partner again.
    /// </summary>
    private bool _applyingPartnerTalk;

    /// <summary>
    /// How many completions of wishes run right now, during which what a wish takes counts for dialogue.
    /// </summary>
    private int _endingWishDepth;

    /// <summary>
    /// The wish whose targets are taken right now, which what is taken meanwhile pays for, or null.
    /// </summary>
    private FullQuestBase? _consumingWish;

    /// <summary>
    /// How many writes of the player data run right now, of which only the outermost is recorded, since some writes of
    /// counters make others.
    /// </summary>
    private int _playerDataWriteDepth;

    /// <summary>
    /// How many gains of dialogue that are recorded run right now. What such a gain makes itself, like the money of an
    /// item or a tool that an item unlocks, isn't recorded again.
    /// </summary>
    private int _talkGainDepth;

    /// <summary>
    /// Dialogue about wishes that ended while the game checked the save with the partner again, which goes to the partner
    /// once that check is done.
    /// </summary>
    private readonly List<CoopSaveUpdate> _pendingWishTurnIns = [];

    /// <summary>
    /// Whether dialogue about wishes threw, which is only logged once.
    /// </summary>
    private bool _wishTalkFailed;

    /// <summary>
    /// What dialogue with a character is about right now.
    /// </summary>
    private enum TalkKind {
        /// <summary>
        /// The character can't offer or take in a wish right now.
        /// </summary>
        Casual,

        /// <summary>
        /// The character can offer a wish or take one in, which needs both players.
        /// </summary>
        Key,

        /// <summary>
        /// The character can take in a delivery that runs against time, which whoever gets there first turns in.
        /// </summary>
        Delivery
    }

    /// <summary>
    /// Dialogue of the local player with a character who deals in wishes, or their use of a wish board.
    /// </summary>
    private sealed class WishTalk {
        public WishTalk(NPCControlBase npc, HashSet<PlayMakerFSM> fsms, bool isKey) {
            Npc = npc;
            Fsms = fsms;
            IsKey = isKey;
            Scene = npc.gameObject.scene.name;
            Path = ScenePath.Get(npc.transform);
        }

        /// <summary>
        /// The character, or the wish board.
        /// </summary>
        public NPCControlBase Npc { get; }

        /// <summary>
        /// The FSMs that run the dialogue of the character.
        /// </summary>
        public HashSet<PlayMakerFSM> Fsms { get; }

        /// <summary>
        /// Whether it is key dialogue, which the partner reads too, or a use of a board that needs the partner, like a
        /// donation, which starts as the list of the board.
        /// </summary>
        public bool IsKey { get; set; }

        /// <summary>
        /// Whether it can turn in a delivery, which needs nobody else, whose character doesn't talk to the partner
        /// meanwhile, and whose lines the partner reads too without the dialogue waiting for them.
        /// </summary>
        public bool IsDelivery { get; set; }

        /// <summary>
        /// What the dialogue gives later that went to the partner already, like the reward of a delivery.
        /// </summary>
        public HashSet<string> CreditedGains { get; } = [];

        /// <summary>
        /// How many changes at the start of <see cref="Changes"/> went to the partner already, like a delivery that went
        /// the moment it was turned in, which what the dialogue changes afterwards belongs to.
        /// </summary>
        public int SentChanges { get; set; }

        /// <summary>
        /// The wishes that dialogue of the partner turned in with the local player while this dialogue could take them
        /// in too, which that turn-in paid and rewarded already.
        /// </summary>
        public HashSet<string> TurnedInByPartner { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The wishes of <see cref="TurnedInByPartner"/> that this dialogue ended again, whose reward it doesn't give
        /// again.
        /// </summary>
        public HashSet<string> RewardsToSkip { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The scene of the character.
        /// </summary>
        public string Scene { get; }

        /// <summary>
        /// The path of the character in its scene.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// When the dialogue started.
        /// </summary>
        public float Started { get; } = Time.unscaledTime;

        /// <summary>
        /// The wishes that the dialogue accepted or completed, in the order they first changed.
        /// </summary>
        public List<string> Wishes { get; } = [];

        /// <summary>
        /// The wish of each change in order, with a wish that changed twice in it twice.
        /// </summary>
        public List<string> Changes { get; } = [];

        /// <summary>
        /// The packed state of the wish of each change in <see cref="Changes"/> right after that change.
        /// </summary>
        public List<int> ChangeValues { get; } = [];

        /// <summary>
        /// What the dialogue took from the local player and gave them.
        /// </summary>
        public List<TalkItem> Items { get; } = [];

        /// <summary>
        /// The booleans and enums of the player data that the dialogue set.
        /// </summary>
        public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// How much the dialogue changed each counter of the player data.
        /// </summary>
        public Dictionary<string, int> IntChanges { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The wishes in <see cref="Wishes"/> by name.
        /// </summary>
        public Dictionary<string, FullQuestBase> Quests { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// When the character stopped talking, or -1 while it talks.
        /// </summary>
        public float EndedTime { get; set; } = -1f;

        /// <summary>
        /// When the dialogue last changed a wish, an item or the player data.
        /// </summary>
        public float LastChangeTime { get; set; } = Time.unscaledTime;

        /// <summary>
        /// Forgets what the dialogue changed so far, once it went to the partner.
        /// </summary>
        public void ClearChanges() {
            SentChanges = 0;
            Wishes.Clear();
            Changes.Clear();
            ChangeValues.Clear();
            Items.Clear();
            Flags.Clear();
            IntChanges.Clear();
            Quests.Clear();
        }

        /// <summary>
        /// Whether the dialogue changed something that didn't go to the partner yet.
        /// </summary>
        public bool HasUnsentChanges =>
            Changes.Count > SentChanges || Items.Count > 0 || Flags.Count > 0 || IntChanges.Count > 0;

        /// <summary>
        /// Whether an FSM runs the dialogue of the character.
        /// </summary>
        public bool IsTalkFsm(Fsm? fsm) {
            return fsm?.Owner is PlayMakerFSM owner && owner != null && Fsms.Contains(owner);
        }

        /// <summary>
        /// Remembers that the dialogue accepted or completed a wish.
        /// </summary>
        public void AddWish(FullQuestBase quest, int value) {
            var name = quest.name;
            Changes.Add(name);
            ChangeValues.Add(value);
            if (!Wishes.Contains(name)) {
                Wishes.Add(name);
            }

            Quests[name] = quest;
            LastChangeTime = Time.unscaledTime;
        }

        /// <summary>
        /// Remembers what the dialogue took or gave, with the wish that took it if known.
        /// </summary>
        public void AddItem(string change, int amount, bool isTake, string? forWish = null) {
            Items.Add(new TalkItem(change, amount, Changes.Count, isTake, false, null, null, forWish));
            LastChangeTime = Time.unscaledTime;
        }

        /// <summary>
        /// Remembers a payment during the dialogue that only counts if the wish that it belongs to takes what was paid,
        /// like one in a prompt, which may be for something else, like a shop of the character.
        /// </summary>
        public void AddTargetPayment(string change, int amount, SavedItem? item, CurrencyType? currency) {
            // A payment that may be for something else doesn't keep the dialogue open
            Items.Add(new TalkItem(change, amount, Changes.Count, true, true, item, currency, null));
        }

        /// <summary>
        /// Whether what the dialogue took or gave goes to the partner with the change at an index of
        /// <see cref="Changes"/>: a payment for a known wish needs a change of that wish, and a payment that only counts
        /// for a wish that takes it needs that wish to take what was paid.
        /// </summary>
        public bool Counts(TalkItem item, int index) {
            // A payment for a wish whose change the dialogue didn't record, like one that was completed already, pays for
            // no other wish
            if (item.ForWish != null && (index < 0 || index >= Changes.Count || Changes[index] != item.ForWish)) {
                return false;
            }

            if (!item.OnlyForTarget) {
                return true;
            }

            if (index < 0 || index >= Changes.Count || !Quests.TryGetValue(Changes[index], out var quest) ||
                quest == null) {
                return false;
            }

            foreach (var target in quest.Targets) {
                if (item.PaidItem != null
                        ? ReferenceEquals(target.Counter, item.PaidItem)
                        : target.Counter is QuestTargetCurrency currency && currency != null &&
                          currency.CurrencyType == item.PaidCurrency) {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The index of the change in <see cref="Changes"/> that what the dialogue took or gave belongs to. A payment
        /// belongs to the next change of the wish that took it, or else to the change after it, which it paid for. A
        /// gain belongs to the change before it, which it rewarded.
        /// </summary>
        public int GetChangeIndex(TalkItem item) {
            if (Changes.Count == 0) {
                return AllWishesIndex;
            }

            if (!item.IsTake) {
                return Mathf.Max(item.ChangeCount - 1, 0);
            }

            // A board takes what all its wishes take before it completes them one after another
            if (item.ForWish != null) {
                for (var i = item.ChangeCount; i < Changes.Count; i++) {
                    if (Changes[i] == item.ForWish) {
                        return i;
                    }
                }
            }

            return Mathf.Min(item.ChangeCount, Changes.Count - 1);
        }
    }

    /// <summary>
    /// Something that dialogue took from the local player or gave them.
    /// </summary>
    private readonly struct TalkItem {
        public TalkItem(
            string change,
            int amount,
            int changeCount,
            bool isTake,
            bool onlyForTarget,
            SavedItem? paidItem,
            CurrencyType? paidCurrency,
            string? forWish
        ) {
            Change = change;
            Amount = amount;
            ChangeCount = changeCount;
            IsTake = isTake;
            OnlyForTarget = onlyForTarget;
            PaidItem = paidItem;
            PaidCurrency = paidCurrency;
            ForWish = forWish;
        }

        /// <summary>
        /// What changed: its kind, followed by what it changed.
        /// </summary>
        public string Change { get; }

        /// <summary>
        /// How much changed.
        /// </summary>
        public int Amount { get; }

        /// <summary>
        /// How many changes of wishes the dialogue had made before.
        /// </summary>
        public int ChangeCount { get; }

        /// <summary>
        /// Whether it was taken rather than given.
        /// </summary>
        public bool IsTake { get; }

        /// <summary>
        /// Whether it is a payment that only counts if the wish that it belongs to takes what was paid.
        /// </summary>
        public bool OnlyForTarget { get; }

        /// <summary>
        /// For a payment that only counts for a wish that takes it, the item that was paid, or null for money.
        /// </summary>
        public SavedItem? PaidItem { get; }

        /// <summary>
        /// For a payment of money that only counts for a wish that takes it, the currency that was paid.
        /// </summary>
        public CurrencyType? PaidCurrency { get; }

        /// <summary>
        /// For a payment, the name of the wish that took it, or null if unknown.
        /// </summary>
        public string? ForWish { get; }
    }

    /// <summary>
    /// A write of the player data that a hook runs, for dialogue that records it.
    /// </summary>
    private readonly struct PlayerDataWrite {
        public PlayerDataWrite(bool isNested, int? countBefore) {
            IsNested = isNested;
            CountBefore = countBefore;
        }

        /// <summary>
        /// Whether another write of the player data makes this one.
        /// </summary>
        public bool IsNested { get; }

        /// <summary>
        /// For a write that changes a counter by an amount, the value of the counter before it, or null for a write of a
        /// value.
        /// </summary>
        public int? CountBefore { get; }
    }

    /// <summary>
    /// Key dialogue of the partner with a character in the local scene.
    /// </summary>
    private sealed class PartnerTalk {
        public PartnerTalk(NPCControlBase? npc) {
            Npc = npc;
        }

        /// <summary>
        /// The character, who doesn't talk to the local player until the partner is done, or null.
        /// </summary>
        public NPCControlBase? Npc { get; }

        /// <summary>
        /// When the dialogue started.
        /// </summary>
        public float Started { get; } = Time.unscaledTime;
    }

    /// <summary>
    /// An action of a dialogue FSM that offers a wish or takes one in.
    /// </summary>
    private readonly struct WishAction {
        public WishAction(string state, FsmObject quest, bool isTurnIn) {
            State = state;
            Quest = quest;
            IsTurnIn = isTurnIn;
        }

        /// <summary>
        /// The state that runs the action, or that runs the template FSM with it.
        /// </summary>
        public string State { get; }

        /// <summary>
        /// The variable with the wish.
        /// </summary>
        public FsmObject Quest { get; }

        /// <summary>
        /// Whether the action takes the wish in rather than offers it.
        /// </summary>
        public bool IsTurnIn { get; }
    }

    /// <summary>
    /// Registers the hooks for dialogue about wishes.
    /// </summary>
    private void RegisterWishTalkHooks() {
        AddWishTalkHook(
            typeof(NPCControlBase).GetMethod(
                "Interact", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null
            ),
            new Action<Action<NPCControlBase>, NPCControlBase>(OnNpcInteract)
        );
        AddWishTalkHook(
            typeof(PlayMakerNPC).GetMethod(
                "OnStartDialogue", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null
            ),
            new Action<Action<PlayMakerNPC>, PlayMakerNPC>(OnNpcStartDialogue)
        );
        AddWishTalkHook(
            typeof(FullQuestBase).GetProperty("Counters", InstanceFlags | BindingFlags.DeclaredOnly)?.GetGetMethod(true),
            new Func<Func<FullQuestBase, IEnumerable<int>>, FullQuestBase, IEnumerable<int>>(OnGetWishCounters)
        );
        AddWishTalkHook(
            typeof(FullQuestBase).GetProperty("CanComplete", InstanceFlags | BindingFlags.DeclaredOnly)
                ?.GetGetMethod(true),
            new Func<Func<FullQuestBase, bool>, FullQuestBase, bool>(OnGetWishCanComplete)
        );
        AddWishTalkHook(
            typeof(FullQuestBase).GetMethod("BeginQuest", InstanceFlags, null, [typeof(Action), typeof(bool)], null),
            new Action<Action<FullQuestBase, Action?, bool>, FullQuestBase, Action?, bool>(OnBeginWish)
        );
        AddWishTalkHook(
            typeof(FullQuestBase).GetMethod(
                "TryEndQuest", InstanceFlags, null, [typeof(Action), typeof(bool), typeof(bool), typeof(bool)], null
            ),
            new Func<Func<FullQuestBase, Action?, bool, bool, bool, bool>, FullQuestBase, Action?, bool, bool, bool,
                bool>(OnTryEndWish)
        );
        AddWishTalkHook(
            typeof(FullQuestBase).GetMethod("ConsumeTarget", InstanceFlags, null, Type.EmptyTypes, null),
            new Func<Func<FullQuestBase, bool>, FullQuestBase, bool>(OnConsumeWishTarget)
        );

        // What a character gives in its dialogue. Money that it gives goes through the hook for changes of currency
        AddWishTalkHook(
            typeof(HutongGames.PlayMaker.Actions.SavedItemGet).GetMethod(
                "OnEnter", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null
            ),
            new Action<Action<HutongGames.PlayMaker.Actions.SavedItemGet>, HutongGames.PlayMaker.Actions.SavedItemGet>(
                (orig, self) => {
                    if (SkipsPartnerReward(self.Fsm, self.Item?.Value as SavedItem)) {
                        self.Finish();
                        return;
                    }

                    RecordTalkGain(self.Fsm, self.Item?.Value as SavedItem, 1);
                    RunTalkGain(() => orig(self));
                }
            )
        );
        AddWishTalkHook(
            typeof(HutongGames.PlayMaker.Actions.SavedItemGetV2).GetMethod(
                "OnEnter", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null
            ),
            new Action<Action<HutongGames.PlayMaker.Actions.SavedItemGetV2>,
                HutongGames.PlayMaker.Actions.SavedItemGetV2>((orig, self) => {
                if (SkipsPartnerReward(self.Fsm, self.Item?.Value as SavedItem)) {
                    self.Finish();
                    return;
                }

                RecordTalkGain(self.Fsm, self.Item?.Value as SavedItem, self.Amount?.Value ?? 1);
                RunTalkGain(() => orig(self));
            })
        );
        AddWishTalkHook(
            typeof(HutongGames.PlayMaker.Actions.SavedItemGetDelayed).GetMethod(
                "DoGet", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null
            ),
            new Action<Action<HutongGames.PlayMaker.Actions.SavedItemGetDelayed>,
                HutongGames.PlayMaker.Actions.SavedItemGetDelayed>((orig, self) => {
                RecordTalkGain(self.Fsm, self.Item?.Value as SavedItem, 1);
                RunTalkGain(() => orig(self));
            })
        );
        AddWishTalkHook(
            typeof(HutongGames.PlayMaker.Actions.CollectableItemCollect).GetMethod(
                "DoAction", InstanceFlags | BindingFlags.DeclaredOnly, null, [typeof(CollectableItem)], null
            ),
            new Action<Action<HutongGames.PlayMaker.Actions.CollectableItemCollect, CollectableItem>,
                HutongGames.PlayMaker.Actions.CollectableItemCollect, CollectableItem>((orig, self, item) => {
                if (item != null) {
                    var amount = self.Amount is { IsNone: false } fsmAmount ? fsmAmount.Value : 1;
                    RecordTalkGainChange(self.Fsm, GetItemChangeKey(CollectItemChange, item), amount);
                }

                RunTalkGain(() => orig(self, item!));
            })
        );

        // The reward of a wish that dialogue of the partner turned in with the local player isn't given again by local
        // dialogue that takes in the same wish
        AddWishTalkHook(
            typeof(QuestPlaymakerActions.GetQuestReward).GetMethod(
                "DoQuestAction", InstanceFlags | BindingFlags.DeclaredOnly, null, [typeof(FullQuestBase)], null
            ),
            new Action<Action<QuestPlaymakerActions.GetQuestReward, FullQuestBase>, QuestPlaymakerActions.GetQuestReward,
                FullQuestBase>((orig, self, quest) => {
                orig(self, quest);
                if (TakesPartnerReward(self.Fsm, quest) && self.StoreReward != null) {
                    self.StoreReward.Value = null;
                }
            })
        );
        AddWishTalkHook(
            typeof(QuestPlaymakerActions.GetQuestRewardV2).GetMethod(
                "DoQuestAction", InstanceFlags | BindingFlags.DeclaredOnly, null, [typeof(FullQuestBase)], null
            ),
            new Action<Action<QuestPlaymakerActions.GetQuestRewardV2, FullQuestBase>,
                QuestPlaymakerActions.GetQuestRewardV2, FullQuestBase>((orig, self, quest) => {
                orig(self, quest);
                if (!TakesPartnerReward(self.Fsm, quest)) {
                    return;
                }

                if (self.StoreReward != null) {
                    self.StoreReward.Value = null;
                }

                if (self.StoreAmount != null) {
                    self.StoreAmount.Value = 0;
                }
            })
        );

        // Tools that dialogue unlocks as a reward, or locks as a payment
        AddWishTalkHook(
            typeof(ToolItem).GetMethod(
                "Unlock",
                InstanceFlags | BindingFlags.DeclaredOnly,
                null,
                [typeof(Action), typeof(ToolItem.PopupFlags)],
                null
            ),
            new Action<Action<ToolItem, Action?, ToolItem.PopupFlags>, ToolItem, Action?, ToolItem.PopupFlags>(
                (orig, self, afterTutorialMsg, popupFlags) => {
                    var wasUnlocked = self.IsUnlocked;
                    orig(self, afterTutorialMsg, popupFlags);
                    if (!wasUnlocked && self.IsUnlocked) {
                        RecordTalkGainChange(
                            FsmExecutionStack.ExecutingFsm, GetItemChangeKey(ToolUnlockChange, self), 1
                        );
                    }
                }
            )
        );
        AddWishTalkHook(
            typeof(ToolItem).GetMethod("Lock", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null),
            new Action<Action<ToolItem>, ToolItem>((orig, self) => {
                var wasUnlocked = self.IsUnlocked;
                orig(self);
                if (wasUnlocked && !self.IsUnlocked) {
                    RecordTalkTake(self, null, GetItemChangeKey(ToolLockChange, self), 1);
                }
            })
        );
    }

    /// <summary>
    /// Runs a gain of dialogue that is recorded, during which what the gain makes itself isn't recorded again.
    /// </summary>
    private void RunTalkGain(Action gain) {
        _talkGainDepth++;
        try {
            gain();
        } finally {
            _talkGainDepth--;
        }
    }

    /// <summary>
    /// Creates a hook for dialogue about wishes, logging instead of throwing when the method is missing.
    /// </summary>
    private void AddWishTalkHook(MethodInfo? method, Delegate detour) {
        if (CreateHook(method, detour) is { } hook) {
            _wishTalkHooks.Add(hook);
        }
    }

    /// <summary>
    /// Whether an FSM runs key dialogue of the local player or turns in a delivery, which the partner reads too.
    /// </summary>
    public bool IsSharedTalk(Fsm fsm) {
        return _wishTalk is { } talk && (talk.IsKey || talk.IsDelivery) && talk.IsTalkFsm(fsm);
    }

    /// <summary>
    /// Whether the end of dialogue that the partner reads too waits for them: only key dialogue does, since a delivery
    /// never waits.
    /// </summary>
    public bool HoldsSharedTalkEnd(Fsm fsm) {
        return _wishTalk is { IsKey: true } talk && talk.IsTalkFsm(fsm);
    }

    /// <summary>
    /// Ends dialogue whose character stopped talking, frees the character that the partner talked to, and sends the
    /// progress of the wishes.
    /// </summary>
    private void UpdateWishTalk(ClientPlayerData? partner) {
        try {
            if (_wishTalk is { } talk) {
                var now = Time.unscaledTime;
                if (IsTalking(talk.Npc)) {
                    talk.EndedTime = -1f;
                } else if (talk.EndedTime < 0f) {
                    talk.EndedTime = now;
                }

                if ((talk.EndedTime >= 0f && now - Mathf.Max(talk.EndedTime, talk.LastChangeTime) > WishTalkEndDelay) ||
                    now - talk.Started > WishTalkMaxTime) {
                    EndWishTalk();
                }
            }

            if (_partnerTalk is { } partnerTalk && (partner == null || !partner.IsInLocalScene ||
                                                    Time.unscaledTime - partnerTalk.Started > PartnerTalkTimeout)) {
                _partnerTalk = null;
            }

            if (partner != null && _checkedWith == partner.Id) {
                UpdateWishProgress(partner);
            }
        } catch (Exception e) {
            LogWishTalkError(e);
        }
    }

    /// <summary>
    /// Whether a character talks to the local hero or is about to, while the hero walks up to it. A wish board is in use
    /// until it gives the hero control back, after its last reward or donation.
    /// </summary>
    private static bool IsTalking(NPCControlBase? npc) {
        if (npc == null) {
            return false;
        }

        return WaitingToBeginField?.GetValue(npc) is true ||
               (npc is PlayMakerNPC playMakerNpc
                   ? playMakerNpc.IsRunningDialogue
                   : InteractManager.BlockingInteractable == npc);
    }

    /// <summary>
    /// Whether a wish of the local wish log waits for dialogue that changed it to end, or for the check that such
    /// dialogue waits for, which send it to the partner.
    /// </summary>
    private bool IsHeldByWishTalk(string name) {
        return (_wishTalk != null && _wishTalk.Wishes.Contains(name)) ||
               _pendingWishTurnIns.Exists(update => update.WishNames.Contains(name));
    }

    /// <summary>
    /// Ends dialogue and frees the character of the partner when the scene changes.
    /// </summary>
    private void OnWishTalkSceneChanged() {
        try {
            EndWishTalk();
        } catch (Exception e) {
            LogWishTalkError(e);
        }

        _partnerTalk = null;
        _wishActions.Clear();
        _talkStates.Clear();
    }

    /// <summary>
    /// Forgets dialogue and the progress of the partner, for a new session.
    /// </summary>
    private void ResetWishTalk() {
        _wishTalk = null;
        _partnerTalk = null;
        _wishActions.Clear();
        _talkStates.Clear();
        _pendingWishTurnIns.Clear();
        _nextMissingCopyNotices.Clear();
        ResetWishProgress();
    }

    /// <summary>
    /// Forgets the progress of the wishes of the partner and what the partner got, for a new check.
    /// </summary>
    private void ResetWishProgress() {
        _partnerWishProgress.Clear();
        _partnerWishProgressKeys.Clear();
        _sentWishProgress.Clear();
        _nextWishProgressTime = 0f;
    }

    #region Starting and ending

    /// <summary>
    /// Hook for <see cref="NPCControlBase.Interact"/>: key dialogue only starts while the partner is close by, and
    /// otherwise the local player hears who is missing. The character that the partner talks to doesn't talk.
    /// </summary>
    private void OnNpcInteract(Action<NPCControlBase> orig, NPCControlBase self) {
        var allowed = true;
        try {
            if (_everChecked && GetCurrentMarker() is { } marker) {
                if (_partnerTalk?.Npc is { } partnerNpc && partnerNpc == self) {
                    Chat(
                        self is QuestBoardInteractable
                            ? $"{GetPartnerName()} is using this board right now."
                            : $"{GetPartnerName()} is talking to them right now."
                    );
                    allowed = false;
                } else if (self is PlayMakerNPC npc) {
                    allowed = TryStartWishTalk(npc, marker);
                }
            }
        } catch (Exception e) {
            LogWishTalkError(e);
        }

        if (allowed) {
            orig(self);
        }
    }

    /// <summary>
    /// Starts key dialogue with a character if it can offer or take in a wish right now, which needs the partner close
    /// by.
    /// </summary>
    /// <returns>Whether the character may talk.</returns>
    private bool TryStartWishTalk(PlayMakerNPC npc, CoopSaveMarker marker) {
        var fsms = GetTalkFsms(npc);
        var kind = GetTalkKind(npc, fsms);
        if (kind == TalkKind.Delivery) {
            StartDeliveryTalk(npc, fsms);
            return true;
        }

        if (kind != TalkKind.Key) {
            // Other dialogue with a character who deals in wishes is recorded once it starts
            return true;
        }

        var partner = _checkedWith is { } partnerId && _playerData.TryGetValue(partnerId, out var checkedPartner)
            ? checkedPartner
            : null;
        var absence = partner == null ? $"This wish needs {marker.PartnerName} here too." : GetWishTalkAbsence(partner);
        if (absence != null || partner == null) {
            Chat(absence ?? $"This wish needs {marker.PartnerName} here too.");
            if (partner != null) {
                Send(CreateWishTalkUpdate(partner.Id, npc.gameObject.scene.name, ScenePath.Get(npc.transform), WishTalkRefused));
            }

            Logger.Info($"Key dialogue with '{npc.name}' didn't start, because the partner isn't close by");
            return false;
        }

        EndWishTalk();
        var talk = new WishTalk(npc, fsms, true);
        _wishTalk = talk;
        Send(CreateWishTalkUpdate(partner.Id, talk.Scene, talk.Path, WishTalkStarted));
        Logger.Info($"Key dialogue with '{npc.name}' starts with {partner.Username} close by");
        return true;
    }

    /// <summary>
    /// Hook for PlayMakerNPC.OnStartDialogue: dialogue with a character who deals in wishes is recorded, also when an
    /// FSM starts it, so that a wish that it changes reaches the partner with what it took and gave.
    /// </summary>
    private void OnNpcStartDialogue(Action<PlayMakerNPC> orig, PlayMakerNPC self) {
        try {
            if (_everChecked && GetCurrentMarker() != null && (_wishTalk == null || !IsSameTalk(_wishTalk, self))) {
                var fsms = GetTalkFsms(self);
                if (HasWishActions(fsms)) {
                    EndWishTalk();
                    _wishTalk = new WishTalk(self, fsms, false);
                }
            }
        } catch (Exception e) {
            LogWishTalkError(e);
        }

        orig(self);
    }

    /// <summary>
    /// Whether a character talks as part of dialogue that is recorded already, like a stand-in that an FSM of the
    /// dialogue talks through.
    /// </summary>
    private static bool IsSameTalk(WishTalk talk, PlayMakerNPC npc) {
        return talk.Npc == npc || (npc.CustomEventTarget != null && talk.Fsms.Contains(npc.CustomEventTarget)) ||
               (DialogueFsmField?.GetValue(npc) is PlayMakerFSM dialogueFsm && dialogueFsm != null &&
                talk.Fsms.Contains(dialogueFsm));
    }

    /// <summary>
    /// Why the partner can't join key dialogue now, for the message to the local player, or null if they can.
    /// </summary>
    private static string? GetWishTalkAbsence(ClientPlayerData partner) {
        var hero = HeroController.instance;
        var avatar = partner.PlayerObject;
        if (!partner.IsInLocalScene || avatar == null || hero == null) {
            return $"This wish needs {partner.Username} here too. Come back together.";
        }

        var offset = avatar.transform.position - hero.transform.position;
        return Mathf.Abs(offset.x) > WishTalkRangeX || Mathf.Abs(offset.y) > WishTalkRangeY
            ? $"{partner.Username} needs to come closer for this wish."
            : null;
    }

    private static CoopSaveUpdate CreateWishTalkUpdate(ushort partnerId, string scene, string path, ushort kind) {
        return new CoopSaveUpdate {
            TargetId = partnerId,
            Kind = CoopSaveUpdateKind.WishTalk,
            PartCount = kind,
            Scene = scene,
            ObjectPath = path
        };
    }

    /// <summary>
    /// The FSMs that run the dialogue of a character.
    /// </summary>
    private static HashSet<PlayMakerFSM> GetTalkFsms(PlayMakerNPC npc) {
        var fsms = new HashSet<PlayMakerFSM>();
        if (DialogueFsmField?.GetValue(npc) is PlayMakerFSM dialogueFsm && dialogueFsm != null) {
            fsms.Add(dialogueFsm);
        }

        if (npc.CustomEventTarget != null) {
            fsms.Add(npc.CustomEventTarget);
        }

        if (SecondaryFsmsField?.GetValue(npc) is PlayMakerFSM[] secondaryFsms) {
            foreach (var fsm in secondaryFsms) {
                if (fsm != null) {
                    fsms.Add(fsm);
                }
            }
        }

        foreach (var fsm in npc.GetComponents<PlayMakerFSM>()) {
            if (fsm != null) {
                fsms.Add(fsm);
            }
        }

        return fsms;
    }

    /// <summary>
    /// What talking to a character is about right now: whether a state that the talk goes through can offer a wish that
    /// is available and not accepted, or take in an accepted wish that can be completed.
    /// </summary>
    private TalkKind GetTalkKind(PlayMakerNPC npc, HashSet<PlayMakerFSM> fsms) {
        var interactEvent = InteractEventField?.GetValue(npc) as string;
        if (string.IsNullOrEmpty(interactEvent)) {
            interactEvent = DefaultInteractEvent;
        }

        var kind = TalkKind.Casual;
        foreach (var component in fsms) {
            if (component.Fsm is not { } fsm) {
                continue;
            }

            var states = GetTalkStates(fsm, interactEvent!);
            if (states.Count == 0) {
                continue;
            }

            foreach (var action in GetWishActions(fsm)) {
                if (!states.Contains(action.State) || action.Quest.Value is not FullQuestBase quest || quest == null) {
                    continue;
                }

                if (!action.IsTurnIn) {
                    if (!quest.IsAccepted && !quest.IsCompleted && quest.IsAvailable) {
                        kind = TalkKind.Key;
                    }
                } else if (quest.IsAccepted && !quest.IsCompleted) {
                    // A delivery goes first, since whoever gets there first turns it in without waiting. The character
                    // of a delivery wish takes it in also when the item got damaged on the way, while an item that runs
                    // against time among what a wish gathers only makes a delivery of a wish that can be completed
                    if (IsCarriedDelivery(quest) && (IsBreakableDelivery(quest) || quest.CanComplete)) {
                        return TalkKind.Delivery;
                    }

                    if (quest.CanComplete) {
                        kind = TalkKind.Key;
                    }
                }
            }
        }

        return kind;
    }

    /// <summary>
    /// Gets the states of a dialogue FSM that a talk goes through: from the states that the event of the talk leads to,
    /// up to the states that wait for the next talk.
    /// </summary>
    private HashSet<string> GetTalkStates(Fsm fsm, string interactEvent) {
        if (_talkStates.TryGetValue(fsm, out var cached) && cached.Event == interactEvent) {
            return cached.States;
        }

        var statesByName = new Dictionary<string, FsmState>(StringComparer.Ordinal);
        foreach (var state in fsm.States ?? []) {
            if (state != null) {
                statesByName[state.Name] = state;
            }
        }

        var queue = new Queue<string>();
        foreach (var transition in fsm.GlobalTransitions ?? []) {
            if (transition?.EventName == interactEvent && transition.ToState != null) {
                queue.Enqueue(transition.ToState);
            }
        }

        foreach (var state in statesByName.Values) {
            foreach (var transition in state.Transitions ?? []) {
                if (transition?.EventName == interactEvent && transition.ToState != null) {
                    queue.Enqueue(transition.ToState);
                }
            }
        }

        var states = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0) {
            var name = queue.Dequeue();
            if (!statesByName.TryGetValue(name, out var state) || !states.Add(name)) {
                continue;
            }

            foreach (var transition in state.Transitions ?? []) {
                if (transition?.ToState is { } next && !states.Contains(next) &&
                    statesByName.TryGetValue(next, out var nextState) && !ListensFor(nextState, interactEvent)) {
                    queue.Enqueue(next);
                }
            }
        }

        _talkStates[fsm] = (interactEvent, states);
        return states;
    }

    private static bool ListensFor(FsmState state, string eventName) {
        return (state.Transitions ?? []).Any(transition => transition?.EventName == eventName);
    }

    /// <summary>
    /// Whether any FSM of a character offers wishes or takes them in.
    /// </summary>
    private bool HasWishActions(HashSet<PlayMakerFSM> fsms) {
        foreach (var component in fsms) {
            if (component.Fsm is { } fsm && GetWishActions(fsm).Count > 0) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the actions of a dialogue FSM that offer wishes and take them in, with those of the template FSMs that it
    /// runs, finding them the first time.
    /// </summary>
    private List<WishAction> GetWishActions(Fsm fsm) {
        if (_wishActions.TryGetValue(fsm, out var actions)) {
            return actions;
        }

        actions = [];
        _wishActions[fsm] = actions;
        foreach (var state in fsm.States ?? []) {
            if (state == null) {
                continue;
            }

            foreach (var action in state.Actions ?? []) {
                AddWishAction(actions, state.Name, action, 0);
            }
        }

        return actions;
    }

    /// <summary>
    /// Adds an action to the actions that offer wishes and take them in if it is one, or the ones of the template FSM
    /// that it runs.
    /// </summary>
    private static void AddWishAction(List<WishAction> actions, string stateName, FsmStateAction? action, int depth) {
        switch (action) {
            case QuestPlaymakerActions.BeginQuest or QuestPlaymakerActions.BeginQuestV2 or
                QuestPlaymakerActions.CanBeginQuest:
                AddQuestAction(actions, stateName, ((QuestPlaymakerActions.QuestFsmAction) action).Quest, false);
                break;
            case QuestPlaymakerActions.CanEndQuest or QuestPlaymakerActions.CanEndQuestV2 or
                QuestPlaymakerActions.EndQuest or QuestPlaymakerActions.EndQuestV2 or
                QuestPlaymakerActions.TryEndQuest or QuestPlaymakerActions.TryEndQuestV2:
                AddQuestAction(actions, stateName, ((QuestPlaymakerActions.QuestFsmAction) action).Quest, true);
                break;
            case QuestYesNo questYesNo:
                AddQuestAction(actions, stateName, questYesNo.Quest, false);
                break;
            case QuestYesNoV2 questYesNo:
                AddQuestAction(actions, stateName, questYesNo.Quest, false);
                break;
            case HutongGames.PlayMaker.Actions.QuestCompleteYesNo completeYesNo:
                AddQuestAction(actions, stateName, completeYesNo.Quest, true);
                break;
            case QuestPlaymakerActions.QuestConsumeTargetTake consumeTake:
                AddQuestAction(actions, stateName, consumeTake.Quest, true);
                break;
            default:
                // The actions of a template FSM count for the state that runs it
                if (action != null && depth < 2 && GetTemplateFsm(action) is { } template) {
                    foreach (var templateState in template.States ?? []) {
                        foreach (var templateAction in templateState?.Actions ?? []) {
                            AddWishAction(actions, stateName, templateAction, depth + 1);
                        }
                    }
                }

                break;
        }
    }

    private static void AddQuestAction(List<WishAction> actions, string stateName, FsmObject? quest, bool isTurnIn) {
        if (quest != null) {
            actions.Add(new WishAction(stateName, quest, isTurnIn));
        }
    }

    /// <summary>
    /// Gets the template FSM that an action runs, or null.
    /// </summary>
    private static Fsm? GetTemplateFsm(FsmStateAction action) {
        var type = action.GetType();
        if (!TemplateControlFields.TryGetValue(type, out var field)) {
            field = type.GetField("fsmTemplateControl", InstanceFlags);
            if (field != null && field.FieldType != typeof(FsmTemplateControl)) {
                field = null;
            }

            TemplateControlFields[type] = field;
        }

        if (field?.GetValue(action) is not FsmTemplateControl control) {
            return null;
        }

        return TemplateTargetField?.GetValue(control) switch {
            FsmTemplate template => TemplateFsmField?.GetValue(template) as Fsm,
            PlayMakerFSM component => component.Fsm,
            _ => null
        };
    }

    /// <summary>
    /// Ends the dialogue of the local player. The partner can talk to the character again, and if the dialogue accepted
    /// or completed wishes, the save of the partner gets them with what the dialogue took and gave.
    /// </summary>
    private void EndWishTalk() {
        if (_wishTalk is not { } talk) {
            return;
        }

        _wishTalk = null;
        var partner = GetCurrentMarker() is { } marker ? FindPartner(marker) : null;
        if ((talk.IsKey || talk.IsDelivery) && partner != null) {
            Send(CreateWishTalkUpdate(partner.Id, talk.Scene, talk.Path, WishTalkEnded));
        }

        SendTalkChanges(talk, partner);
    }

    /// <summary>
    /// Sends the wishes that dialogue accepted or completed to the partner, with what it took and gave, if it changed
    /// any. Changes that went to the partner before, like a delivery, go again for what the dialogue changed afterwards,
    /// which the partner takes if they took those changes.
    /// </summary>
    private void SendTalkChanges(WishTalk talk, ClientPlayerData? partner) {
        if (talk.Changes.Count == 0 || !talk.HasUnsentChanges) {
            return;
        }

        if (partner == null) {
            // The next check finds the wishes changed in one save only
            Logger.Warn("Dialogue about wishes ended without the partner, so its wishes stay with the local save");
            return;
        }

        // A wish that changed twice, like one that was completed and accepted again, has an entry for each change
        var update = new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.WishTurnIn,
            PartCount = (ushort) Mathf.Min(talk.SentChanges, ushort.MaxValue),
            Scene = talk.Scene,
            ObjectPath = talk.Path
        };
        for (var i = 0; i < talk.Changes.Count; i++) {
            update.WishNames.Add(talk.Changes[i]);
            update.WishValues.Add(talk.ChangeValues[i]);
        }

        foreach (var item in talk.Items) {
            var index = talk.GetChangeIndex(item);
            if (talk.Counts(item, index)) {
                update.ItemIds.Add(index + "\n" + item.Change);
                update.Amounts.Add(item.Amount);
            }
        }

        foreach (var pair in talk.IntChanges) {
            update.ItemIds.Add(AllWishesIndex + "\n" + IntChange + "\n" + pair.Key);
            update.Amounts.Add(pair.Value);
        }

        foreach (var name in talk.Flags) {
            if (ReadPlayerDataFlag(name) is { } value) {
                update.FlagNames.Add(name);
                update.FlagValues.Add(value);
            }
        }

        if (_checkedWith != partner.Id) {
            // The game checks the save with the partner again, which sends these wishes as they were before, so the
            // dialogue goes to the partner once the check is done
            _pendingWishTurnIns.Add(update);
            Logger.Info($"Dialogue about {talk.Wishes.Count} wishes waits for the check with {partner.Username}");
            return;
        }

        SendWishTurnIn(update, partner);
    }

    /// <summary>
    /// Sends dialogue about wishes to the partner, who gets its wishes with it, so the wish log doesn't send them again.
    /// </summary>
    private void SendWishTurnIn(CoopSaveUpdate update, ClientPlayerData partner) {
        update.TargetId = partner.Id;
        Send(update);
        // Changes that went to the partner before are known already, and the wish log may have changed since
        for (var i = (int) update.PartCount; i < update.WishNames.Count && i < update.WishValues.Count; i++) {
            _knownWishes[update.WishNames[i]] = update.WishValues[i];
        }

        Logger.Info(
            $"Sent dialogue with {update.WishNames.Count} changes of wishes to {partner.Username}, with " +
            $"{update.ItemIds.Count} item changes and {update.FlagNames.Count} flags"
        );
    }

    /// <summary>
    /// Sends the dialogue about wishes that ended during the check with the partner, once the check is done.
    /// </summary>
    private void SendPendingWishTurnIns(ClientPlayerData partner) {
        foreach (var update in _pendingWishTurnIns) {
            SendWishTurnIn(update, partner);
        }

        _pendingWishTurnIns.Clear();
    }

    /// <summary>
    /// The partner started or ended key dialogue, or couldn't start it without the local player close by.
    /// </summary>
    private void OnWishTalk(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker)) {
            return;
        }

        try {
            switch (update.PartCount) {
                case WishTalkRefused:
                    var refusedAt = ScenePath.Find(update.ObjectPath, update.Scene);
                    Chat(
                        refusedAt != null && refusedAt.GetComponent<QuestBoardInteractable>() != null
                            ? $"{player.Username} wants to use a wish board with you, which needs you close by."
                            : $"{player.Username} wants to talk about a wish, which needs you close by."
                    );
                    break;
                case WishTalkEnded:
                    _partnerTalk = null;
                    break;
                case WishTalkStarted:
                    var target = ScenePath.Find(update.ObjectPath, update.Scene);
                    _partnerTalk = new PartnerTalk(target != null ? target.GetComponent<NPCControlBase>() : null);
                    break;
            }
        } catch (Exception e) {
            LogWishTalkError(e);
        }
    }

    #endregion

    #region What dialogue takes and gives

    /// <summary>
    /// Hook for <see cref="FullQuestBase.BeginQuest"/>, which remembers a wish that dialogue accepted, also again after
    /// it was completed.
    /// </summary>
    private void OnBeginWish(Action<FullQuestBase, Action?, bool> orig, FullQuestBase self, Action? afterPrompt, bool showPrompt) {
        var wasActive = self.IsAccepted && !self.IsCompleted;
        orig(self, afterPrompt, showPrompt);
        if (!_applyingPartnerTalk && !wasActive && self.IsAccepted && !self.IsCompleted) {
            AddTalkWish(self);
        }
    }

    /// <summary>
    /// Hook for <see cref="FullQuestBase.TryEndQuest"/>, which remembers a wish that dialogue completed. What the wish
    /// takes while it ends counts for the dialogue.
    /// </summary>
    private bool OnTryEndWish(
        Func<FullQuestBase, Action?, bool, bool, bool, bool> orig,
        FullQuestBase self,
        Action? afterPrompt,
        bool consumeCurrency,
        bool forceEnd,
        bool showPrompt
    ) {
        var wasCompleted = self.IsCompleted;

        // Local dialogue that ends a wish again after the turn-in of the partner completed it with the local player
        // neither takes nor rewards it again, since that turn-in did both
        if (wasCompleted && !_applyingPartnerTalk && _wishTalk is { } partnerTurnIn &&
            (IsTalking(partnerTurnIn.Npc) || partnerTurnIn.IsTalkFsm(FsmExecutionStack.ExecutingFsm)) &&
            partnerTurnIn.TurnedInByPartner.Remove(self.name)) {
            consumeCurrency = false;
            partnerTurnIn.RewardsToSkip.Add(self.name);
            Logger.Info($"Dialogue ends '{self.name}' again after the partner's turn-in, without paying for it again");
        }

        bool ended;
        _endingWishDepth++;
        try {
            ended = orig(self, afterPrompt, consumeCurrency, forceEnd, showPrompt);
        } finally {
            _endingWishDepth--;
        }

        if (ended && !wasCompleted && self.IsCompleted && !_applyingPartnerTalk) {
            // What throws here must not stop the action of the game that ends the wish, which would keep the hero
            // without control
            try {
                AddTalkWish(self);
                if (_boardCompletionDepth > 0) {
                    RecordBoardReward(self);
                } else if (_wishTalk is { IsDelivery: true } talk) {
                    SendDelivery(talk, self);
                }
            } catch (Exception e) {
                LogWishTalkError(e);
            }
        }

        return ended;
    }

    /// <summary>
    /// Hook for <see cref="FullQuestBase.ConsumeTarget"/>: what a wish takes pays for that wish, also when the dialogue
    /// completes it only later, like a board.
    /// </summary>
    private bool OnConsumeWishTarget(Func<FullQuestBase, bool> orig, FullQuestBase self) {
        var outer = _consumingWish;
        _consumingWish = self;
        try {
            return orig(self);
        } finally {
            _consumingWish = outer;
        }
    }

    /// <summary>
    /// Remembers a wish that dialogue of the local player accepted or completed, with its state right after. Once the
    /// character stopped talking, only the FSMs of the dialogue change wishes for it.
    /// </summary>
    private void AddTalkWish(FullQuestBase quest) {
        if (_wishTalk is { } talk && PlayerData.instance is { } playerData &&
            (IsTalking(talk.Npc) || talk.IsTalkFsm(FsmExecutionStack.ExecutingFsm))) {
            talk.AddWish(quest, PackCompletion(playerData.QuestCompletionData.GetData(quest.name)));
        }
    }

    /// <summary>
    /// Records a saved item that an FSM of dialogue gives. Wishes and rumours go to the partner with the wish log.
    /// </summary>
    private void RecordTalkGain(Fsm? fsm, SavedItem? item, int amount) {
        if (item != null && item is not BasicQuestBase) {
            RecordTalkGainChange(fsm, GetItemChangeKey(GetItemChange, item), amount);
        }
    }

    /// <summary>
    /// Records what an FSM of dialogue of the local player gives.
    /// </summary>
    private void RecordTalkGainChange(Fsm? fsm, string change, int amount) {
        // A reward that went to the partner with a delivery isn't recorded again
        if (_wishTalk is { } talk && !_applyingPartnerTalk && _talkGainDepth == 0 && amount > 0 &&
            talk.IsTalkFsm(fsm) && !talk.CreditedGains.Remove(change)) {
            talk.AddItem(change, amount, false);
        }
    }

    /// <summary>
    /// Records what dialogue of the local player takes. What its FSMs take counts for the dialogue, and so does what a
    /// wish takes while it ends as long as the character talks. Other payments while it talks, like in a prompt, only
    /// count if the wish that they belong to takes what was paid, so that a purchase in a shop of the character stays
    /// with the local player.
    /// </summary>
    private void RecordTalkTake(SavedItem? item, CurrencyType? currency, string change, int amount) {
        if (_wishTalk is not { } talk || _applyingPartnerTalk || amount == 0) {
            return;
        }

        var forWish = _consumingWish != null ? _consumingWish.name : null;
        if (talk.IsTalkFsm(FsmExecutionStack.ExecutingFsm)) {
            talk.AddItem(change, amount, true, forWish);
            return;
        }

        // Once the character stopped talking, or in the prompt of a mechanism, a payment is for something else
        if (!IsTalking(talk.Npc) || _promptInteraction != null) {
            return;
        }

        if (_endingWishDepth > 0 || forWish != null) {
            talk.AddItem(change, amount, true, forWish);
        } else {
            talk.AddTargetPayment(change, amount, item, currency);
        }
    }

    /// <summary>
    /// Starts a write of the player data, which the hook ends by lowering <see cref="_playerDataWriteDepth"/>. A write
    /// that another write makes is nested, and isn't recorded for dialogue on its own. For the outermost write that
    /// changes a counter by an amount while dialogue records, the value before it is kept, so that the partner gets the
    /// change.
    /// </summary>
    private PlayerDataWrite BeginPlayerDataWrite(PlayerData? playerData, string? name, bool changesCounter) {
        _playerDataWriteDepth++;
        if (_playerDataWriteDepth > 1) {
            return new PlayerDataWrite(true, null);
        }

        return new PlayerDataWrite(
            false,
            changesCounter && _wishTalk != null && playerData != null && !string.IsNullOrEmpty(name) &&
            GetPlayerDataField(name!)?.FieldType == typeof(int)
                ? playerData.GetInt(name)
                : null
        );
    }

    /// <summary>
    /// Records a write of the player data by an FSM of dialogue of the local player. Booleans, enums and counters that the
    /// dialogue sets to a value go with their values, and a counter that it changes by an amount goes with how much it
    /// changed, since the count of the partner may differ. What a recorded gain writes is left to the gain, and the story
    /// flags share the story state already.
    /// </summary>
    private void RecordTalkFlag(string name, PlayerDataWrite write) {
        if (_wishTalk is not { } talk || _applyingPartnerTalk || write.IsNested || _talkGainDepth > 0 ||
            !talk.IsTalkFsm(FsmExecutionStack.ExecutingFsm) || BossRoomCoop.IsHeroStateName(name) ||
            PlayerData.instance is not { } playerData) {
            return;
        }

        GetStoryFields();
        if (StoryFieldIndices.ContainsKey(name) || GetPlayerDataField(name) is not { } field) {
            return;
        }

        if (field.FieldType == typeof(int) && write.CountBefore is { } before) {
            var change = playerData.GetInt(name) - before;

            // A counter that the dialogue set to a value goes with its last value, which has this change in it
            if (change == 0 || talk.Flags.Contains(name)) {
                return;
            }

            talk.IntChanges[name] = (talk.IntChanges.TryGetValue(name, out var sum) ? sum : 0) + change;
        } else if (field.FieldType == typeof(int) || field.FieldType == typeof(bool) || field.FieldType.IsEnum) {
            talk.IntChanges.Remove(name);
            talk.Flags.Add(name);
        } else {
            return;
        }

        talk.LastChangeTime = Time.unscaledTime;
    }

    /// <summary>
    /// The entry of an item change: its kind, the type of the item and its name.
    /// </summary>
    private static string GetItemChangeKey(string change, SavedItem item) {
        return change + "\n" + item.GetType().FullName + "\n" + item.name;
    }

    /// <summary>
    /// Adds the wishes that dialogue of the partner accepted or completed to the local wish log, and takes and gives in
    /// the local save what the dialogue took from the partner and gave them. A wish that the local save had accepted or
    /// completed itself, or that the check left completed in only one save, isn't paid or rewarded again. Local dialogue
    /// that can take in a wish that this completes doesn't give its reward again.
    /// </summary>
    private void OnWishTurnIn(ClientPlayerData player, CoopSaveUpdate update) {
        var playerData = PlayerData.instance;
        if (playerData == null || GetCurrentMarker() is not { } marker || !IsPartner(player, marker) ||
            !IsFromCurrentCheck(player, update)) {
            return;
        }

        try {
            var count = Mathf.Min(update.WishNames.Count, update.WishValues.Count);
            var applies = new bool[count];
            var newer = new Dictionary<string, bool>(StringComparer.Ordinal);
            var lastBefore = new Dictionary<string, QuestCompletionData.Completion>(StringComparer.Ordinal);
            var anyApplies = false;
            var anyNewApplies = false;
            var anyCompleted = false;
            var completionApplies = false;
            var brokenApplies = false;
            var turnedInHere = false;
            var deliveredHere = false;
            var changed = 0;
            var accepted = 0;
            var completed = 0;
            var refused = 0;
            for (var i = 0; i < count; i++) {
                var name = update.WishNames[i];
                var value = update.WishValues[i];
                var isCompletion = (value & WishCompleted) != 0;

                // A change that came before, like a delivery the moment it was turned in, comes again for what the
                // dialogue changed afterwards, which counts if that change counted
                if (i < update.PartCount) {
                    applies[i] = isCompletion && _appliedPartnerCompletions.TryGetValue(name, out var applied) &&
                                 applied == value;
                    anyApplies |= applies[i];
                    continue;
                }

                // A wish that changed more than once in the dialogue has an entry for each change, in order
                if (!newer.TryGetValue(name, out var isNewer)) {
                    isNewer = IsNewerChange(_wishSequences, update, GetWishChangeKey(name, value));
                    newer[name] = isNewer;
                }

                if (!isNewer) {
                    continue;
                }

                var before = playerData.QuestCompletionData.GetData(name);
                var packedBefore = PackCompletion(before);
                lastBefore[name] = before;

                // A wish that the local player turns in at a board at the same time is paid and rewarded there
                var isTurnedInHere = isCompletion && !before.IsCompleted && IsTurningInAtBoard(name);
                turnedInHere |= isTurnedInHere;

                // A delivery that the local player turned in at the same time was paid and rewarded with that turn-in
                deliveredHere |= isCompletion && before.IsCompleted &&
                                 _deliveredWishes.TryGetValue(name, out var delivered) && delivered == packedBefore;

                // A delivery whose item broke for the local player is rewarded when the partner delivers it, also when
                // the break completed it without a reward, as long as the wish stayed as the break left it
                var isBroken = isCompletion && _brokenDeliveries.TryGetValue(name, out var broken) &&
                               broken.Value == packedBefore;
                if (isCompletion) {
                    _brokenDeliveries.Remove(name);
                }

                // A delivery that the partner accepted isn't accepted for a local player whom the dialogue doesn't give
                // its item either, and nothing that came with that accept counts for them
                if (!isCompletion && (value & WishAccepted) != 0 && !(before.IsAccepted && !before.IsCompleted) &&
                    FindQuest(name) is { } delivery && IsBreakableDelivery(delivery) && !IsCarriedDelivery(delivery) &&
                    !GivesDeliveryItem(update, i, delivery)) {
                    _knownWishes[name] = packedBefore;
                    refused++;
                    Logger.Info($"Not accepting the delivery '{name}' from {player.Username} without its item");
                    continue;
                }

                applies[i] = !_differentWishNames.Contains(name) && !isTurnedInHere &&
                             (isBroken ||
                              (isCompletion ? !before.IsCompleted : !before.IsAccepted || before.IsCompleted));
                anyApplies |= applies[i];
                anyNewApplies |= applies[i];
                anyCompleted |= isCompletion;
                completionApplies |= isCompletion && applies[i];
                brokenApplies |= isBroken && applies[i];
                ApplyPartnerWish(playerData, name, value, ref changed, ref accepted, ref completed);

                // Local dialogue that takes in the same wish, like with the same character, doesn't reward it again
                if (isCompletion && applies[i]) {
                    _appliedPartnerCompletions[name] = value;
                    if (_wishTalk is { } talk && FindQuest(name) is { } quest && CanTalkTurnIn(talk, quest)) {
                        talk.TurnedInByPartner.Add(name);
                    }
                }
            }

            if (changed > 0) {
                QuestManager.IncrementVersion();
            }

            var items = 0;
            if (anyApplies) {
                _applyingPartnerTalk = true;
                try {
                    for (var i = 0; i < update.ItemIds.Count && i < update.Amounts.Count; i++) {
                        var entry = update.ItemIds[i];
                        var split = entry.IndexOf('\n');
                        if (split <= 0 || !int.TryParse(entry.Substring(0, split), out var index) ||
                            (index >= 0 ? index >= count || !applies[index] : index != AllWishesIndex)) {
                            continue;
                        }

                        if (ApplyTalkItem(entry.Substring(split + 1), update.Amounts[i])) {
                            items++;
                        }
                    }

                    ApplyInteractionFlags(update);
                } finally {
                    _applyingPartnerTalk = false;
                }
            }

            // An item that the dialogue gave the partner but the local player didn't get, like one that they can't get
            // more of, doesn't leave an accepted delivery without its item either
            var refusedAfter = 0;
            foreach (var pair in lastBefore) {
                if (RefuseUncarriedDelivery(playerData, pair.Key, pair.Value)) {
                    refusedAfter++;
                    if (!pair.Value.IsAccepted) {
                        accepted--;
                    }
                }
            }

            if (refusedAfter > 0) {
                QuestManager.IncrementVersion();
            }

            refused += refusedAfter;

            if (brokenApplies) {
                Chat($"{player.Username} delivered what broke for you, so you got the reward too.");
            } else if (completionApplies) {
                Chat($"{player.Username} turned in a wish with you. You paid your own copy and got the reward too.");
            } else if (turnedInHere) {
                Chat(
                    $"{player.Username} turned in the same wish at the same time. You pay your copy and get the reward " +
                    "once, at your board."
                );
            } else if (deliveredHere) {
                Chat(
                    $"{player.Username} turned in the same delivery at the same time. You paid your copy and got the " +
                    "reward once, with your own turn-in."
                );
            } else if (anyCompleted) {
                Chat(
                    $"{player.Username} turned in a wish that your save had completed already, so you didn't pay or " +
                    "get the reward again."
                );
            } else if (anyNewApplies && items > 0) {
                Chat($"{player.Username} accepted a wish with you, and you got what came with it too.");
            } else if (accepted > 0) {
                Chat($"{player.Username} accepted {CountWishes(accepted)}. Your wish log has the same now.");
            }

            if (refused > 0) {
                Chat(
                    $"{player.Username} accepted a delivery whose item you didn't get, so it isn't accepted for you."
                );
            }

            Logger.Info(
                $"Added dialogue with {count} changes of wishes from {player.Username}, {changed} changed and {items} " +
                "item changes"
            );
        } catch (Exception e) {
            LogWishTalkError(e);
        }
    }

    /// <summary>
    /// Whether dialogue of the local player can take in a wish, whose FSMs would then give its reward.
    /// </summary>
    private bool CanTalkTurnIn(WishTalk talk, FullQuestBase quest) {
        foreach (var component in talk.Fsms) {
            if (component == null || component.Fsm is not { } fsm) {
                continue;
            }

            foreach (var action in GetWishActions(fsm)) {
                if (action.IsTurnIn && action.Quest.Value == quest) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an FSM of local dialogue gets the reward of a wish that it ended again after the turn-in of the partner,
    /// which gave the local player the reward already, so that the FSM doesn't give it again.
    /// </summary>
    private bool TakesPartnerReward(Fsm? fsm, FullQuestBase? quest) {
        if (quest == null || _wishTalk is not { } talk || !talk.IsTalkFsm(fsm) ||
            !talk.RewardsToSkip.Contains(quest.name)) {
            return false;
        }

        Logger.Info($"Not giving the reward of '{quest.name}' again, which came with the partner's turn-in");
        return true;
    }

    /// <summary>
    /// Whether an FSM of local dialogue gives the reward item of a wish that it ended again after the turn-in of the
    /// partner, which gave the local player the reward already, so that the FSM doesn't give it again. It counts once.
    /// </summary>
    private bool SkipsPartnerReward(Fsm? fsm, SavedItem? item) {
        if (item == null || _wishTalk is not { } talk || talk.RewardsToSkip.Count == 0 || !talk.IsTalkFsm(fsm)) {
            return false;
        }

        foreach (var name in talk.RewardsToSkip) {
            if (FindQuest(name) is { } quest && quest.RewardItem == item) {
                talk.RewardsToSkip.Remove(name);
                Logger.Info($"Not giving the reward of '{name}' again, which came with the partner's turn-in");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an update of the partner belongs to the check that both games are in, also while the local game still
    /// finishes it.
    /// </summary>
    private bool IsFromCurrentCheck(ClientPlayerData player, CoopSaveUpdate update) {
        return _checkedWith == player.Id ||
               (_checkPartnerId == player.Id && _checkKey != 0 && update.Sequence >> 32 == _checkKey >> 16);
    }

    /// <summary>
    /// Takes or gives one change of dialogue of the partner in the local save. What the local player doesn't have is
    /// taken as far as they have it.
    /// </summary>
    /// <returns>Whether something changed.</returns>
    private static bool ApplyTalkItem(string change, int amount) {
        var parts = change.Split('\n');
        switch (parts[0]) {
            case TakeItemChange when parts.Length == 3 && amount > 0: {
                if (FindSavedItem(parts[1], parts[2]) is not CollectableItem item) {
                    return false;
                }

                var taken = Mathf.Min(amount, item.CollectedAmount);
                if (taken > 0) {
                    item.Take(taken, true);
                }

                return taken > 0;
            }
            case CollectItemChange when parts.Length == 3 && amount > 0: {
                // Collecting doesn't look at whether the local player can get more, like an item that they have once
                if (FindSavedItem(parts[1], parts[2]) is not CollectableItem item || !item.CanGetMore()) {
                    return false;
                }

                item.Collect(amount, true);
                return true;
            }
            case GetItemChange when parts.Length == 3 && amount > 0: {
                var item = FindSavedItem(parts[1], parts[2]);
                if (item == null || !item.CanGetMore()) {
                    return false;
                }

                item.Get(amount, true);
                return true;
            }
            case CurrencyChange when parts.Length == 2 && int.TryParse(parts[1], out var number): {
                var type = (CurrencyType) number;
                if (amount > 0) {
                    CurrencyManager.AddCurrency(amount, type, true);
                    return true;
                }

                var taken = Mathf.Min(-amount, CurrencyManager.GetCurrencyAmount(type));
                if (taken > 0) {
                    CurrencyManager.TakeCurrency(taken, type, true);
                }

                return taken > 0;
            }
            case IntChange when parts.Length == 2 && PlayerData.instance is { } playerData &&
                                GetPlayerDataField(parts[1])?.FieldType == typeof(int): {
                playerData.SetInt(parts[1], playerData.GetInt(parts[1]) + amount);
                return true;
            }
            case ToolUnlockChange when parts.Length == 3: {
                if (FindSavedItem(parts[1], parts[2]) is not ToolItem tool || tool.IsUnlocked) {
                    return false;
                }

                tool.Unlock(null, ToolItem.PopupFlags.ItemGet);
                return true;
            }
            case ToolLockChange when parts.Length == 3: {
                if (FindSavedItem(parts[1], parts[2]) is not ToolItem tool || !tool.IsUnlocked) {
                    return false;
                }

                tool.Lock();
                return true;
            }
            default:
                Logger.Warn($"Could not apply the change '{change.Replace('\n', ' ')}' of dialogue about wishes");
                return false;
        }
    }

    /// <summary>
    /// Finds a saved item of the game by its type and name.
    /// </summary>
    private static SavedItem? FindSavedItem(string typeName, string name) {
        var key = typeName + "\n" + name;
        if (SavedItems.TryGetValue(key, out var item) && item != null) {
            return item;
        }

        SavedItems.Clear();
        foreach (var found in Resources.FindObjectsOfTypeAll<SavedItem>()) {
            if (found != null) {
                SavedItems[found.GetType().FullName + "\n" + found.name] = found;
            }
        }

        return SavedItems.TryGetValue(key, out item) ? item : null;
    }

    #endregion

    #region Progress

    /// <summary>
    /// Sends the progress of the targets of wishes that changed since the partner last got it: of the accepted wishes,
    /// and of the wishes that take something, whose copies the partner checks before a turn-in, also before they are
    /// accepted.
    /// </summary>
    private void UpdateWishProgress(ClientPlayerData partner) {
        var playerData = PlayerData.instance;
        if (playerData == null || Time.unscaledTime < _nextWishProgressTime) {
            return;
        }

        _nextWishProgressTime = Time.unscaledTime + WishProgressInterval;

        CoopSaveUpdate? update = null;
        foreach (var basicQuest in QuestManager.GetAllQuests()) {
            if (basicQuest is not FullQuestBase quest || quest == null) {
                continue;
            }

            var name = quest.name;
            int[] amounts;
            try {
                if (quest.IsCompleted || (!quest.IsAccepted && !HasConsumableTarget(quest))) {
                    continue;
                }

                amounts = GetLocalWishProgress(quest, playerData.QuestCompletionData.GetData(name));
            } catch (Exception e) {
                // A wish whose progress can't be read doesn't hold back the others
                LogWishTalkError(e);
                continue;
            }
            if (amounts.Length == 0 ||
                (_sentWishProgress.TryGetValue(name, out var sent) && sent.SequenceEqual(amounts))) {
                continue;
            }

            _sentWishProgress[name] = amounts;

            // The targets of a wish stay together in one update, so newer progress of a wish never mixes with older
            if (update != null && update.WishNames.Count + amounts.Length > WishProgressEntriesPerUpdate) {
                Send(update);
                update = null;
            }

            update ??= new CoopSaveUpdate {
                TargetId = partner.Id,
                Kind = CoopSaveUpdateKind.WishProgress,
                Key = ++_wishProgressCounter,
                Sequence = (_checkKey >> 16) << 32
            };
            for (var i = 0; i < amounts.Length; i++) {
                update.WishNames.Add(name);
                update.WishValues.Add(i);
                update.Amounts.Add(amounts[i]);
            }
        }

        if (update != null) {
            Send(update);
        }
    }

    /// <summary>
    /// Whether a wish takes something to turn it in.
    /// </summary>
    private static bool HasConsumableTarget(FullQuestBase quest) {
        foreach (var target in quest.Targets) {
            if (target.Counter != null && target.Count > 0 && target.Counter.CanConsume) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The progress of each target of a wish in the local save alone, the way the game counts it.
    /// </summary>
    private static int[] GetLocalWishProgress(FullQuestBase quest, QuestCompletionData.Completion completion) {
        var targets = quest.Targets;
        var amounts = new int[Mathf.Min(targets.Count, MaxWishTargets)];
        for (var i = 0; i < amounts.Length; i++) {
            var target = targets[i];
            amounts[i] = target.AltTest != null && target.AltTest.IsDefined && target.AltTest.IsFulfilled
                ? target.Count
                : target.Counter != null
                    ? target.Counter.GetCompletionAmount(completion)
                    : completion.CompletedCount;
        }

        return amounts;
    }

    /// <summary>
    /// Remembers the progress of the wishes of the partner. An update that the network delivered after a newer one
    /// doesn't undo newer progress.
    /// </summary>
    private void OnWishProgress(ClientPlayerData player, CoopSaveUpdate update) {
        // Progress from an earlier check can be older than what the current check agreed on
        if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker) || _checkKey == 0 ||
            update.Sequence >> 32 != _checkKey >> 16) {
            return;
        }

        for (var i = 0; i < update.WishNames.Count && i < update.WishValues.Count && i < update.Amounts.Count; i++) {
            var name = update.WishNames[i];
            var index = update.WishValues[i];
            if (index < 0 || index >= MaxWishTargets ||
                (_partnerWishProgressKeys.TryGetValue(name, out var key) && key > update.Key)) {
                continue;
            }

            _partnerWishProgressKeys[name] = update.Key;
            if (!_partnerWishProgress.TryGetValue(name, out var amounts) || amounts.Length <= index) {
                var grown = new int[index + 1];
                amounts?.CopyTo(grown, 0);
                amounts = grown;
                _partnerWishProgress[name] = amounts;
            }

            amounts[index] = update.Amounts[i];
        }
    }

    /// <summary>
    /// Hook for <see cref="FullQuestBase.Counters"/>: progress that isn't taken, like kills, counts from whichever
    /// player has more of it. What a wish takes counts for each player alone.
    /// </summary>
    private IEnumerable<int> OnGetWishCounters(Func<FullQuestBase, IEnumerable<int>> orig, FullQuestBase self) {
        var counters = orig(self);
        if (_checkedWith == null || _partnerWishProgress.Count == 0 || self == null ||
            !_partnerWishProgress.TryGetValue(self.name, out var partnerAmounts) || self.IsCompleted) {
            return counters;
        }

        return MergeWishCounters(self.Targets, counters, partnerAmounts);
    }

    private static IEnumerable<int> MergeWishCounters(
        IReadOnlyList<FullQuestBase.QuestTarget> targets,
        IEnumerable<int> counters,
        int[] partnerAmounts
    ) {
        var index = 0;
        foreach (var amount in counters) {
            var counter = index < targets.Count ? targets[index].Counter : null;
            yield return index < partnerAmounts.Length && counter != null && !counter.CanConsume
                ? Mathf.Max(amount, partnerAmounts[index])
                : amount;
            index++;
        }
    }

    /// <summary>
    /// Hook for <see cref="FullQuestBase.CanComplete"/>: a wish that takes something can only be completed while the
    /// partner has a full copy of it too, since both pay. A wish that the check left completed in only one save needs
    /// the local copy alone, and a delivery runs against time for each carrier on their own.
    /// </summary>
    private bool OnGetWishCanComplete(Func<FullQuestBase, bool> orig, FullQuestBase self) {
        var canComplete = orig(self);
        if (!canComplete || _checkedWith == null || self == null || _localCopiesOnly) {
            return canComplete;
        }

        var name = self.name;
        if (_differentWishNames.Contains(name)) {
            return true;
        }

        _partnerWishProgress.TryGetValue(name, out var partnerAmounts);
        var targets = self.Targets;
        for (var i = 0; i < targets.Count; i++) {
            var target = targets[i];
            if (target.Counter == null || target.Count <= 0 || target.Counter is DeliveryQuestItem ||
                !target.Counter.CanConsume) {
                continue;
            }

            // Progress that the partner's game didn't send yet doesn't count as a copy
            var amount = partnerAmounts != null && i < partnerAmounts.Length ? partnerAmounts[i] : -1;
            if (amount >= target.Count) {
                continue;
            }

            NoticeMissingCopy(name, amount, target.Count);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Tells the local player that the partner lacks a full copy for a wish, when a character that the local player
    /// talks to checks it.
    /// </summary>
    private void NoticeMissingCopy(string wish, int amount, int needed) {
        if (_wishTalk is not { } talk || !talk.IsTalkFsm(FsmExecutionStack.ExecutingFsm) ||
            (_nextMissingCopyNotices.TryGetValue(wish, out var next) && Time.unscaledTime < next)) {
            return;
        }

        _nextMissingCopyNotices[wish] = Time.unscaledTime + MissingCopyNoticeInterval;
        Chat(
            amount < 0
                ? $"Your game doesn't know yet what {GetPartnerName()} carries for this wish. Try again in a moment."
                : $"{GetPartnerName()} doesn't have a full copy of what this wish takes yet ({amount} of {needed}). " +
                  "Both of you pay one to turn it in."
        );
    }

    #endregion

    /// <summary>
    /// Logs the first error of dialogue about wishes.
    /// </summary>
    private void LogWishTalkError(Exception e) {
        if (!_wishTalkFailed) {
            _wishTalkFailed = true;
            Logger.Error($"Could not sync dialogue about wishes of the two-player save:\n{e}");
        }
    }
}
