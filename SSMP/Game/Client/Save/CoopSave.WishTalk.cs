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
/// Key dialogue in a checked two-player save: talking to a character who offers a wish or takes one in. It only starts
/// while the partner is close by, the partner reads the same lines, and what the talk takes from the local player and
/// gives them happens in the save of the partner too, so both pay a full copy and both get the reward. Turning in a wish
/// needs a full copy of what it takes in both inventories, while progress that isn't taken, like kills, counts from
/// either player. A wish that only the save of the partner completed before is turned in with the local copy alone, and
/// the partner pays and gets nothing for it. Other talk stays with the player who talks.
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
    /// How long, in seconds, key dialogue still records after its character stopped talking, for what the character
    /// gives right after.
    /// </summary>
    private const float WishTalkEndDelay = 2f;

    /// <summary>
    /// How long, in seconds, the character that the partner talks to stays locked for the local player at most.
    /// </summary>
    private const float PartnerTalkTimeout = 300f;

    /// <summary>
    /// How often, in seconds, the progress of the accepted wishes is compared with what the partner last got.
    /// </summary>
    private const float WishProgressInterval = 1f;

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
    /// An item that key dialogue took: the type and name of the item follow.
    /// </summary>
    private const string TakeItemChange = "take";

    /// <summary>
    /// A saved item that key dialogue gave: the type and name of the item follow.
    /// </summary>
    private const string GetItemChange = "get";

    /// <summary>
    /// An item that key dialogue added to the collection: the type and name of the item follow.
    /// </summary>
    private const string CollectItemChange = "collect";

    /// <summary>
    /// Money that key dialogue took or gave, with its amount negative when taken: the number of the currency follows.
    /// </summary>
    private const string CurrencyChange = "currency";

    private static readonly FieldInfo? DialogueFsmField = typeof(PlayMakerNPC).GetField("dialogueFsm", InstanceFlags);
    private static readonly FieldInfo? SecondaryFsmsField = typeof(PlayMakerNPC).GetField("secondaryFsms", InstanceFlags);
    private static readonly FieldInfo? WaitingToBeginField = typeof(NPCControlBase).GetField("isWaitingToBegin", InstanceFlags);

    /// <summary>
    /// The saved items of the game by their type and name, found when first needed.
    /// </summary>
    private static readonly Dictionary<string, SavedItem> SavedItems = new(StringComparer.Ordinal);

    /// <summary>
    /// The hooks for key dialogue, which stay for as long as the game runs.
    /// </summary>
    private readonly List<Hook> _wishTalkHooks = [];

    /// <summary>
    /// The key dialogue that the local player is in, or null.
    /// </summary>
    private WishTalk? _wishTalk;

    /// <summary>
    /// The key dialogue that the partner is in, whose character the local player can't talk to meanwhile, or null.
    /// </summary>
    private PartnerTalk? _partnerTalk;

    /// <summary>
    /// The quests that the actions of dialogue FSMs offer and take in, by FSM, found the first time.
    /// </summary>
    private readonly Dictionary<Fsm, WishActions> _wishActions = new();

    /// <summary>
    /// The progress of each target of the accepted wishes in the save of the partner, by wish.
    /// </summary>
    private readonly Dictionary<string, int[]> _partnerWishProgress = new(StringComparer.Ordinal);

    /// <summary>
    /// The key of the update that the progress of each wish of the partner last came in, by wish.
    /// </summary>
    private readonly Dictionary<string, ulong> _partnerWishProgressKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// The progress of each target of the accepted wishes that the partner last got, by wish.
    /// </summary>
    private readonly Dictionary<string, int[]> _sentWishProgress = new(StringComparer.Ordinal);

    /// <summary>
    /// Counts the updates with progress of wishes, whose keys tell newer ones from older ones.
    /// </summary>
    private ulong _wishProgressCounter;

    /// <summary>
    /// When the progress of the accepted wishes is compared next.
    /// </summary>
    private float _nextWishProgressTime;

    /// <summary>
    /// Wishes that the partner completed in the local wish log, whose key dialogue makes the local player pay and get the
    /// reward. A wish that the local save completed itself isn't paid or rewarded again.
    /// </summary>
    private readonly HashSet<string> _partnerCompletedWishes = new(StringComparer.Ordinal);

    /// <summary>
    /// Wishes that the partner accepted in the local wish log, whose key dialogue gives the local player what came with
    /// them.
    /// </summary>
    private readonly HashSet<string> _partnerAcceptedWishes = new(StringComparer.Ordinal);

    /// <summary>
    /// When the local player can hear again that the partner lacks a full copy for a wish, by wish.
    /// </summary>
    private readonly Dictionary<string, float> _nextMissingCopyNotices = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the local game plays key dialogue of the partner, whose changes aren't recorded for the partner again.
    /// </summary>
    private bool _applyingPartnerTalk;

    /// <summary>
    /// Whether key dialogue threw, which is only logged once.
    /// </summary>
    private bool _wishTalkFailed;

    /// <summary>
    /// Key dialogue of the local player with a character.
    /// </summary>
    private sealed class WishTalk {
        public WishTalk(PlayMakerNPC npc, HashSet<PlayMakerFSM> fsms) {
            Npc = npc;
            Fsms = fsms;
            Scene = npc.gameObject.scene.name;
            Path = ScenePath.Get(npc.transform);
        }

        /// <summary>
        /// The character.
        /// </summary>
        public PlayMakerNPC Npc { get; }

        /// <summary>
        /// The FSMs that run the dialogue of the character.
        /// </summary>
        public HashSet<PlayMakerFSM> Fsms { get; }

        /// <summary>
        /// The scene of the character.
        /// </summary>
        public string Scene { get; }

        /// <summary>
        /// The path of the character in its scene.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// The wishes that the dialogue accepted or completed, with <see cref="WishAccepted"/> and
        /// <see cref="WishCompleted"/>.
        /// </summary>
        public List<(string Name, int Change)> Wishes { get; } = [];

        /// <summary>
        /// What the dialogue took from the local player and gave them, with their amounts.
        /// </summary>
        public List<(string Change, int Amount)> Items { get; } = [];

        /// <summary>
        /// The flags of the player data that the dialogue set.
        /// </summary>
        public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// When the character stopped talking, or -1 while it talks.
        /// </summary>
        public float EndedTime { get; set; } = -1f;

        /// <summary>
        /// Whether an FSM runs the dialogue of the character.
        /// </summary>
        public bool IsTalkFsm(Fsm? fsm) {
            return fsm?.Owner is PlayMakerFSM owner && owner != null && Fsms.Contains(owner);
        }

        /// <summary>
        /// Remembers that the dialogue accepted or completed a wish.
        /// </summary>
        public void AddWish(string name, int change) {
            var index = Wishes.FindIndex(entry => entry.Name == name);
            if (index < 0) {
                Wishes.Add((name, change));
            } else {
                Wishes[index] = (name, Wishes[index].Change | change);
            }
        }
    }

    /// <summary>
    /// Key dialogue of the partner with a character in the local scene.
    /// </summary>
    private sealed class PartnerTalk {
        public PartnerTalk(InteractableBase? interactable) {
            Interactable = interactable;
        }

        /// <summary>
        /// The character, whom the local player can't talk to until the partner is done, or null.
        /// </summary>
        public InteractableBase? Interactable { get; }

        /// <summary>
        /// When the dialogue started.
        /// </summary>
        public float Started { get; } = Time.unscaledTime;
    }

    /// <summary>
    /// The quests that a dialogue FSM offers and takes in, as the variables that hold them.
    /// </summary>
    private sealed class WishActions {
        public List<FsmObject> Offers { get; } = [];

        public List<FsmObject> TurnIns { get; } = [];
    }

    /// <summary>
    /// Registers the hooks for key dialogue.
    /// </summary>
    private void RegisterWishTalkHooks() {
        AddWishTalkHook(
            typeof(NPCControlBase).GetMethod(
                "Interact", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null
            ),
            new Action<Action<NPCControlBase>, NPCControlBase>(OnNpcInteract)
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

        // What a character gives in its dialogue
        AddWishTalkHook(
            typeof(HutongGames.PlayMaker.Actions.SavedItemGet).GetMethod(
                "OnEnter", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null
            ),
            new Action<Action<HutongGames.PlayMaker.Actions.SavedItemGet>, HutongGames.PlayMaker.Actions.SavedItemGet>(
                (orig, self) => {
                    RecordTalkGain(self.Fsm, self.Item?.Value as SavedItem, 1);
                    orig(self);
                }
            )
        );
        AddWishTalkHook(
            typeof(HutongGames.PlayMaker.Actions.SavedItemGetV2).GetMethod(
                "OnEnter", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null
            ),
            new Action<Action<HutongGames.PlayMaker.Actions.SavedItemGetV2>,
                HutongGames.PlayMaker.Actions.SavedItemGetV2>((orig, self) => {
                RecordTalkGain(self.Fsm, self.Item?.Value as SavedItem, self.Amount?.Value ?? 1);
                orig(self);
            })
        );
        AddWishTalkHook(
            typeof(HutongGames.PlayMaker.Actions.SavedItemGetDelayed).GetMethod(
                "DoGet", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null
            ),
            new Action<Action<HutongGames.PlayMaker.Actions.SavedItemGetDelayed>,
                HutongGames.PlayMaker.Actions.SavedItemGetDelayed>((orig, self) => {
                RecordTalkGain(self.Fsm, self.Item?.Value as SavedItem, 1);
                orig(self);
            })
        );
        AddWishTalkHook(
            typeof(HutongGames.PlayMaker.Actions.CollectableItemCollect).GetMethod(
                "DoAction", InstanceFlags | BindingFlags.DeclaredOnly, null, [typeof(CollectableItem)], null
            ),
            new Action<Action<HutongGames.PlayMaker.Actions.CollectableItemCollect, CollectableItem>,
                HutongGames.PlayMaker.Actions.CollectableItemCollect, CollectableItem>((orig, self, item) => {
                if (item != null) {
                    RecordTalkItem(self.Fsm, GetItemChangeKey(CollectItemChange, item), self.Amount?.Value ?? 1);
                }

                orig(self, item!);
            })
        );
        AddWishTalkHook(
            typeof(HutongGames.PlayMaker.Actions.AddCurrency).GetMethod(
                "OnEnter", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null
            ),
            new Action<Action<HutongGames.PlayMaker.Actions.AddCurrency>, HutongGames.PlayMaker.Actions.AddCurrency>(
                (orig, self) => {
                    if (self.CurrencyType is { IsNone: false, Value: { } type } && self.Amount != null) {
                        RecordTalkItem(self.Fsm, CurrencyChange + "\n" + Convert.ToInt32(type), self.Amount.Value);
                    }

                    orig(self);
                }
            )
        );
    }

    /// <summary>
    /// Creates a hook for key dialogue, logging instead of throwing when the method is missing.
    /// </summary>
    private void AddWishTalkHook(MethodInfo? method, Delegate detour) {
        if (CreateHook(method, detour) is { } hook) {
            _wishTalkHooks.Add(hook);
        }
    }

    /// <summary>
    /// Whether an FSM runs key dialogue of the local player, which the partner reads too.
    /// </summary>
    public bool IsSharedTalk(Fsm fsm) {
        return _wishTalk is { } talk && talk.IsTalkFsm(fsm);
    }

    /// <summary>
    /// Ends key dialogue whose character stopped talking, frees the character that the partner talked to, and sends the
    /// progress of the accepted wishes.
    /// </summary>
    private void UpdateWishTalk(ClientPlayerData? partner) {
        try {
            if (_wishTalk is { } talk) {
                if (IsTalking(talk.Npc)) {
                    talk.EndedTime = -1f;
                } else if (talk.EndedTime < 0f) {
                    talk.EndedTime = Time.unscaledTime;
                } else if (Time.unscaledTime - talk.EndedTime > WishTalkEndDelay) {
                    EndWishTalk();
                }
            }

            if (_partnerTalk is { } partnerTalk &&
                (partner == null || !partner.IsInLocalScene || Time.unscaledTime - partnerTalk.Started > PartnerTalkTimeout)) {
                EndPartnerTalk();
            }

            if (partner != null && _checkedWith == partner.Id) {
                UpdateWishProgress(partner);
            }
        } catch (Exception e) {
            LogWishTalkError(e);
        }
    }

    /// <summary>
    /// Whether a character talks to the local hero or is about to, while the hero walks up to it.
    /// </summary>
    private static bool IsTalking(PlayMakerNPC? npc) {
        return npc != null && (npc.IsRunningDialogue || WaitingToBeginField?.GetValue(npc) is true);
    }

    /// <summary>
    /// Ends key dialogue and frees the character of the partner when the scene changes.
    /// </summary>
    private void OnWishTalkSceneChanged() {
        try {
            EndWishTalk();
        } catch (Exception e) {
            LogWishTalkError(e);
        }

        _partnerTalk = null;
        _wishActions.Clear();
    }

    /// <summary>
    /// Forgets key dialogue and the progress of the partner, for a new session.
    /// </summary>
    private void ResetWishTalk() {
        _wishTalk = null;
        EndPartnerTalk();
        _wishActions.Clear();
        ResetWishProgress();
        _partnerCompletedWishes.Clear();
        _partnerAcceptedWishes.Clear();
        _nextMissingCopyNotices.Clear();
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
    /// otherwise the local player hears who is missing.
    /// </summary>
    private void OnNpcInteract(Action<NPCControlBase> orig, NPCControlBase self) {
        var allowed = true;
        try {
            if (self is PlayMakerNPC npc && _wishTalk == null && _everChecked && GetCurrentMarker() is { } marker) {
                allowed = TryStartWishTalk(npc, marker);
            }
        } catch (Exception e) {
            LogWishTalkError(e);
        }

        if (allowed) {
            orig(self);
        }
    }

    /// <summary>
    /// Starts key dialogue with a character if the talk is key, which needs the partner close by.
    /// </summary>
    /// <returns>Whether the character may talk.</returns>
    private bool TryStartWishTalk(PlayMakerNPC npc, CoopSaveMarker marker) {
        var fsms = GetTalkFsms(npc);
        if (!IsKeyTalk(fsms)) {
            return true;
        }

        var partner = _checkedWith is { } partnerId && _playerData.TryGetValue(partnerId, out var checkedPartner)
            ? checkedPartner
            : null;
        var absence = GetWishTalkAbsence(partner, marker);
        if (absence != null || partner == null) {
            Chat(absence ?? $"This wish needs {marker.PartnerName} here too.");
            if (partner != null) {
                Send(CreateWishTalkUpdate(partner.Id, npc.gameObject.scene.name, ScenePath.Get(npc.transform), WishTalkRefused));
            }

            Logger.Info($"Key dialogue with '{npc.name}' didn't start, because the partner isn't close by");
            return false;
        }

        var talk = new WishTalk(npc, fsms);
        _wishTalk = talk;
        Send(CreateWishTalkUpdate(partner.Id, talk.Scene, talk.Path, WishTalkStarted));
        Logger.Info($"Key dialogue with '{npc.name}' starts with {partner.Username} close by");
        return true;
    }

    /// <summary>
    /// Why the partner can't join key dialogue now, for the message to the local player, or null if they can.
    /// </summary>
    private static string? GetWishTalkAbsence(ClientPlayerData? partner, CoopSaveMarker marker) {
        if (partner == null) {
            return $"This wish needs {marker.PartnerName} here too.";
        }

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
    /// Whether the dialogue of a character is key right now: it can offer a wish that is available and not accepted, or
    /// take in an accepted wish that can be completed.
    /// </summary>
    private bool IsKeyTalk(HashSet<PlayMakerFSM> fsms) {
        foreach (var component in fsms) {
            if (component.Fsm is not { } fsm) {
                continue;
            }

            var actions = GetWishActions(fsm);
            foreach (var variable in actions.TurnIns) {
                if (variable.Value is FullQuestBase quest && quest != null && quest.IsAccepted && !quest.IsCompleted &&
                    quest.CanComplete) {
                    return true;
                }
            }

            foreach (var variable in actions.Offers) {
                if (variable.Value is FullQuestBase quest && quest != null && !quest.IsAccepted && !quest.IsCompleted &&
                    quest.IsAvailable) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the quests that the actions of a dialogue FSM offer and take in, finding them the first time.
    /// </summary>
    private WishActions GetWishActions(Fsm fsm) {
        if (_wishActions.TryGetValue(fsm, out var actions)) {
            return actions;
        }

        actions = new WishActions();
        foreach (var state in fsm.States ?? []) {
            foreach (var action in state?.Actions ?? []) {
                switch (action) {
                    case QuestPlaymakerActions.BeginQuest or QuestPlaymakerActions.BeginQuestV2 or
                        QuestPlaymakerActions.CanBeginQuest:
                        AddQuestVariable(actions.Offers, ((QuestPlaymakerActions.QuestFsmAction) action).Quest);
                        break;
                    case QuestPlaymakerActions.CanEndQuest or QuestPlaymakerActions.CanEndQuestV2 or
                        QuestPlaymakerActions.EndQuest or QuestPlaymakerActions.EndQuestV2 or
                        QuestPlaymakerActions.TryEndQuest or QuestPlaymakerActions.TryEndQuestV2:
                        AddQuestVariable(actions.TurnIns, ((QuestPlaymakerActions.QuestFsmAction) action).Quest);
                        break;
                    case QuestYesNo questYesNo:
                        AddQuestVariable(actions.Offers, questYesNo.Quest);
                        break;
                    case QuestYesNoV2 questYesNo:
                        AddQuestVariable(actions.Offers, questYesNo.Quest);
                        break;
                    case HutongGames.PlayMaker.Actions.QuestCompleteYesNo completeYesNo:
                        AddQuestVariable(actions.TurnIns, completeYesNo.Quest);
                        break;
                    case QuestPlaymakerActions.QuestConsumeTargetTake consumeTake:
                        AddQuestVariable(actions.TurnIns, consumeTake.Quest);
                        break;
                }
            }
        }

        _wishActions[fsm] = actions;
        return actions;
    }

    private static void AddQuestVariable(List<FsmObject> variables, FsmObject? variable) {
        if (variable != null && !variables.Contains(variable)) {
            variables.Add(variable);
        }
    }

    /// <summary>
    /// Ends the key dialogue of the local player. The partner can talk to the character again, and if the dialogue
    /// accepted or completed wishes, the partner's game takes and gives what the dialogue took and gave.
    /// </summary>
    private void EndWishTalk() {
        if (_wishTalk is not { } talk) {
            return;
        }

        _wishTalk = null;
        if (_checkedWith is not { } partnerId || !_playerData.TryGetValue(partnerId, out var partner)) {
            if (talk.Wishes.Count > 0) {
                Logger.Warn("The partner left during key dialogue, so what it took and gave stays with the local save");
            }

            return;
        }

        Send(CreateWishTalkUpdate(partnerId, talk.Scene, talk.Path, WishTalkEnded));
        if (talk.Wishes.Count == 0) {
            return;
        }

        var update = new CoopSaveUpdate {
            TargetId = partnerId,
            Kind = CoopSaveUpdateKind.WishTurnIn,
            Scene = talk.Scene,
            ObjectPath = talk.Path
        };
        foreach (var (name, change) in talk.Wishes) {
            update.WishNames.Add(name);
            update.WishValues.Add(change);
        }

        foreach (var (change, amount) in talk.Items) {
            update.ItemIds.Add(change);
            update.Amounts.Add(amount);
        }

        foreach (var name in talk.Flags) {
            if (ReadPlayerDataFlag(name) is { } value) {
                update.FlagNames.Add(name);
                update.FlagValues.Add(value);
            }
        }

        Send(update);
        Logger.Info(
            $"Sent key dialogue to {partner.Username}: {talk.Wishes.Count} wishes, {talk.Items.Count} item changes and " +
            $"{update.FlagNames.Count} flags"
        );
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
                    Chat($"{player.Username} wants to talk about a wish, which needs you close by.");
                    break;
                case WishTalkEnded:
                    EndPartnerTalk();
                    break;
                case WishTalkStarted:
                    EndPartnerTalk();
                    var target = ScenePath.Find(update.ObjectPath, update.Scene);
                    var interactable = target != null ? target.GetComponent<InteractableBase>() : null;

                    // A local hero who talks to the same character already goes on
                    if (interactable != null && InteractManager.BlockingInteractable != interactable) {
                        interactable.Deactivate(false);
                        _partnerTalk = new PartnerTalk(interactable);
                    } else {
                        _partnerTalk = new PartnerTalk(null);
                    }

                    break;
            }
        } catch (Exception e) {
            LogWishTalkError(e);
        }
    }

    /// <summary>
    /// Lets the local player talk to the character of the partner's key dialogue again.
    /// </summary>
    private void EndPartnerTalk() {
        var interactable = _partnerTalk?.Interactable;
        _partnerTalk = null;
        if (interactable != null) {
            interactable.Activate();
        }
    }

    #endregion

    #region What key dialogue takes and gives

    /// <summary>
    /// Hook for <see cref="FullQuestBase.BeginQuest"/>, which remembers a wish that key dialogue accepted.
    /// </summary>
    private void OnBeginWish(Action<FullQuestBase, Action?, bool> orig, FullQuestBase self, Action? afterPrompt, bool showPrompt) {
        var wasAccepted = self.IsAccepted;
        orig(self, afterPrompt, showPrompt);
        if (_applyingPartnerTalk || wasAccepted || !self.IsAccepted) {
            return;
        }

        _partnerAcceptedWishes.Remove(self.name);
        _wishTalk?.AddWish(self.name, WishAccepted);
    }

    /// <summary>
    /// Hook for <see cref="FullQuestBase.TryEndQuest"/>, which remembers a wish that key dialogue completed.
    /// </summary>
    private bool OnTryEndWish(
        Func<FullQuestBase, Action?, bool, bool, bool, bool> orig,
        FullQuestBase self,
        Action? afterPrompt,
        bool consumeCurrency,
        bool forceEnd,
        bool showPrompt
    ) {
        var ended = orig(self, afterPrompt, consumeCurrency, forceEnd, showPrompt);
        if (ended && !_applyingPartnerTalk) {
            _partnerCompletedWishes.Remove(self.name);
            _wishTalk?.AddWish(self.name, WishCompleted);
        }

        return ended;
    }

    /// <summary>
    /// Records a saved item that an FSM of key dialogue gives. Wishes and rumours go to the partner with the wish log.
    /// </summary>
    private void RecordTalkGain(Fsm? fsm, SavedItem? item, int amount) {
        if (item != null && item is not BasicQuestBase && fsm != null) {
            RecordTalkItem(fsm, GetItemChangeKey(GetItemChange, item), amount);
        }
    }

    /// <summary>
    /// Records what key dialogue of the local player took or gave. Without an FSM, like for a payment in a prompt of the
    /// dialogue, it counts for the dialogue as a whole.
    /// </summary>
    private void RecordTalkItem(Fsm? fsm, string change, int amount) {
        if (_wishTalk is not { } talk || _applyingPartnerTalk || amount == 0 || (fsm != null && !talk.IsTalkFsm(fsm))) {
            return;
        }

        talk.Items.Add((change, amount));
    }

    /// <summary>
    /// Records a flag of the player data that an FSM of key dialogue of the local player set.
    /// </summary>
    private void RecordTalkFlag(string name) {
        if (_wishTalk is { } talk && !_applyingPartnerTalk && talk.IsTalkFsm(FsmExecutionStack.ExecutingFsm) &&
            !BossRoomCoop.IsHeroStateName(name)) {
            talk.Flags.Add(name);
        }
    }

    /// <summary>
    /// The entry of an item change: its kind, the type of the item and its name.
    /// </summary>
    private static string GetItemChangeKey(string change, SavedItem item) {
        return change + "\n" + item.GetType().FullName + "\n" + item.name;
    }

    /// <summary>
    /// Takes and gives in the local save what key dialogue of the partner took from them and gave them, unless the local
    /// save had accepted or completed its wishes itself.
    /// </summary>
    private void OnWishTurnIn(ClientPlayerData player, CoopSaveUpdate update) {
        var playerData = PlayerData.instance;
        if (playerData == null || GetCurrentMarker() is not { } marker || !IsPartner(player, marker) ||
            !IsFromCurrentCheck(player, update)) {
            return;
        }

        try {
            var completed = false;
            var applies = false;
            for (var i = 0; i < update.WishNames.Count && i < update.WishValues.Count; i++) {
                var name = update.WishNames[i];
                var wish = playerData.QuestCompletionData.GetData(name);
                if ((update.WishValues[i] & WishCompleted) != 0) {
                    completed = true;
                    var byPartner = _partnerCompletedWishes.Remove(name);
                    applies |= byPartner || !wish.IsCompleted;
                } else if ((update.WishValues[i] & WishAccepted) != 0) {
                    var byPartner = _partnerAcceptedWishes.Remove(name);
                    applies |= byPartner || !wish.IsAccepted;
                }
            }

            if (!applies) {
                if (completed) {
                    Chat(
                        $"{player.Username} turned in a wish that your save had completed already, so you didn't pay or " +
                        "get the reward again."
                    );
                }

                Logger.Info($"Key dialogue of {player.Username} was for wishes that the local save had done itself");
                return;
            }

            var items = 0;
            _applyingPartnerTalk = true;
            try {
                for (var i = 0; i < update.ItemIds.Count && i < update.Amounts.Count; i++) {
                    if (ApplyTalkItem(update.ItemIds[i], update.Amounts[i])) {
                        items++;
                    }
                }

                ApplyInteractionFlags(update);
            } finally {
                _applyingPartnerTalk = false;
            }

            if (completed) {
                Chat($"{player.Username} turned in a wish with you. You paid your own copy and got the reward too.");
            } else if (items > 0) {
                Chat($"{player.Username} accepted a wish with you, and you got what came with it too.");
            }

            Logger.Info($"Played key dialogue of {player.Username} in the local save with {items} item changes");
        } catch (Exception e) {
            LogWishTalkError(e);
        }
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
    /// Takes or gives one change of key dialogue of the partner in the local save. What the local player doesn't have is
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
                if (FindSavedItem(parts[1], parts[2]) is not CollectableItem item) {
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
            default:
                Logger.Warn($"Could not apply the item change '{change.Replace('\n', ' ')}' of key dialogue");
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
    /// Sends the progress of the targets of the accepted wishes that changed since the partner last got it.
    /// </summary>
    private void UpdateWishProgress(ClientPlayerData partner) {
        var playerData = PlayerData.instance;
        if (playerData == null || Time.unscaledTime < _nextWishProgressTime) {
            return;
        }

        _nextWishProgressTime = Time.unscaledTime + WishProgressInterval;

        CoopSaveUpdate? update = null;
        foreach (var basicQuest in QuestManager.GetAllQuests()) {
            if (basicQuest is not FullQuestBase quest || quest == null || !quest.IsAccepted || quest.IsCompleted) {
                continue;
            }

            var name = quest.name;
            var amounts = GetLocalWishProgress(quest, playerData.QuestCompletionData.GetData(name));
            if (amounts.Length == 0 ||
                (_sentWishProgress.TryGetValue(name, out var sent) && sent.SequenceEqual(amounts))) {
                continue;
            }

            _sentWishProgress[name] = amounts;
            update ??= new CoopSaveUpdate {
                TargetId = partner.Id,
                Kind = CoopSaveUpdateKind.WishProgress,
                Key = ++_wishProgressCounter
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
        if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker)) {
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
    /// partner has a full copy of it too, since both pay. A wish that only the save of the partner completed before
    /// needs the local copy alone.
    /// </summary>
    private bool OnGetWishCanComplete(Func<FullQuestBase, bool> orig, FullQuestBase self) {
        var canComplete = orig(self);
        if (!canComplete || _checkedWith == null || _partnerWishProgress.Count == 0 || self == null) {
            return canComplete;
        }

        var name = self.name;
        if (_differentWishNames.Contains(name) || !_partnerWishProgress.TryGetValue(name, out var partnerAmounts)) {
            return true;
        }

        var targets = self.Targets;
        for (var i = 0; i < targets.Count && i < partnerAmounts.Length; i++) {
            var target = targets[i];
            if (target.Counter == null || !target.Counter.CanConsume || target.Count <= 0 ||
                partnerAmounts[i] >= target.Count) {
                continue;
            }

            NoticeMissingCopy(name, partnerAmounts[i], target.Count);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Tells the local player that the partner lacks a full copy for a wish, when a character checks it.
    /// </summary>
    private void NoticeMissingCopy(string wish, int amount, int needed) {
        if (FsmExecutionStack.ExecutingFsm == null ||
            (_nextMissingCopyNotices.TryGetValue(wish, out var next) && Time.unscaledTime < next)) {
            return;
        }

        _nextMissingCopyNotices[wish] = Time.unscaledTime + MissingCopyNoticeInterval;
        Chat(
            $"{GetPartnerName()} doesn't have a full copy of what this wish takes yet ({amount} of {needed}). Both of " +
            "you pay one to turn it in."
        );
    }

    #endregion

    /// <summary>
    /// Logs the first error of key dialogue.
    /// </summary>
    private void LogWishTalkError(Exception e) {
        if (!_wishTalkFailed) {
            _wishTalkFailed = true;
            Logger.Error($"Could not sync key dialogue of the two-player save:\n{e}");
        }
    }
}
