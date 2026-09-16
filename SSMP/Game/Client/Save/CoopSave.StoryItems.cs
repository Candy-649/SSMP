using System;
using System.Collections.Generic;
using System.Reflection;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The items that hold the story of the world in a checked two-player save. Picking one up is not shared: every pickup
/// in the world stays one copy per player, like money and every other pickup, so that taking things off the ground
/// never behaves in two different ways. Only the two cases that no pickup can cover sync.
///
/// A character that hands an item over on a first talk gives it to both players, because that talk is not shared and
/// the partner has no pickup of that item anywhere. A one-off story interaction that uses up an item that exists once,
/// like a lock that swallows its key or a machine that builds something out of what it is given, takes it from both
/// players, because the world shows the result to both of them and the partner would otherwise keep a copy that
/// nothing can be done with. Keys that shops also sell stay with the player who used theirs, since the other player may
/// have bought their own.
///
/// What a wish takes and what dialogue about a wish gives is left alone here: the wish sync already takes and gives it
/// in both saves (see CoopSave.WishTalk). A purchase is left alone too, because both players pay for their own.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The embedded list of items that hold the story, made by tools/coop_story_items.py.
    /// </summary>
    private const string StoryItemsFilePath = "SSMP.Resource.coop-story-items.json";

    /// <summary>
    /// The items of the story by <see cref="GetStoryItemKey"/>, loaded when first needed.
    /// </summary>
    private static Dictionary<string, CoopStoryItem>? _storyItems;

    /// <summary>
    /// The field of a character that tells whether it talked before.
    /// </summary>
    private static FieldInfo? _npcTalkStateField;

    /// <summary>
    /// The keys of the story items of the partner that the local save gave or took already, so that a resend of the
    /// same update doesn't give or take the same item twice.
    /// </summary>
    private readonly HashSet<ulong> _appliedStoryItems = [];

    /// <summary>
    /// The gifts of a first talk that both saves settled already, as the scene and the path of the character
    /// and the change of the item. A character hands its items over once for both players: the partner isn't
    /// sent a gift that they gave themselves, and a character doesn't hand the local player an item that the
    /// partner already got from it.
    /// </summary>
    private readonly HashSet<string> _settledTalkGifts = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the local save gives or takes an item of the story that the partner sent, which is never sent back.
    /// </summary>
    private bool _applyingStoryItem;

    /// <summary>
    /// How deep a purchase runs, whose price is paid by the player who bought it alone.
    /// </summary>
    private int _purchaseDepth;

    /// <summary>
    /// Hooks the purchases, which take an item only from the player who bought something, and the first talk of a
    /// character, which hands its items to the local player alone.
    /// </summary>
    private void RegisterStoryItemHooks() {
        AddWishTalkHook(
            typeof(ShopItem).GetMethod("SetPurchased", InstanceFlags, null, [typeof(Action), typeof(int)], null),
            new Action<Action<ShopItem, Action, int>, ShopItem, Action, int>((orig, self, onComplete, subItem) => {
                _purchaseDepth++;
                try {
                    orig(self, onComplete, subItem);
                } finally {
                    _purchaseDepth--;
                }
            })
        );
        AddWishTalkHook(
            typeof(BasicNPC).GetMethod(
                "OnEndDialogue", InstanceFlags | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null
            ),
            new Action<Action<BasicNPC>, BasicNPC>((orig, self) => {
                _npcTalkStateField ??= typeof(BasicNPC).GetField("talkState", InstanceFlags);
                var firstTalk = _npcTalkStateField?.GetValue(self) is 0;
                if (firstTalk) {
                    try {
                        DropGiftsThePartnerGot(self);
                    } catch (Exception e) {
                        Logger.Error($"Could not drop what the partner already got from a character:\n{e}");
                    }
                }

                orig(self);

                try {
                    if (firstTalk) {
                        NoticeFirstTalkGifts(self);
                    }
                } catch (Exception e) {
                    Logger.Error($"Could not send what a character gave on a first talk:\n{e}");
                }
            })
        );
    }

    /// <summary>
    /// Forgets the story items of the partner that the local save applied, when the two-player save is left.
    /// </summary>
    private void ResetStoryItems() {
        _appliedStoryItems.Clear();
        _settledTalkGifts.Clear();
        _applyingStoryItem = false;
        _purchaseDepth = 0;
    }

    /// <summary>
    /// Sends the items of the story that a character handed to the local player on a first talk, which the partner
    /// doesn't read and has no pickup of.
    /// </summary>
    private void NoticeFirstTalkGifts(BasicNPC npc) {
        if (_applyingPartnerTalk || _applyingStoryItem || npc.GiveOnFirstTalk is not { } items) {
            return;
        }

        var character = GetTalkGiftKey(npc);
        foreach (var item in items) {
            if (item == null || !IsStoryItem(item, out _)) {
                continue;
            }

            // A gift that the partner already got from this character doesn't go back to them
            var change = GetItemChangeKey(GetItemChange, item);
            if (_settledTalkGifts.Add(character + "\n" + change)) {
                SendStoryItem(change, 1, $"was given {item.name} by a character", npc);
            }
        }
    }

    /// <summary>
    /// Takes the items that the partner already got from a character out of what it hands over on a first
    /// talk, so that the local player doesn't end up with a second copy of what their save already got.
    /// </summary>
    private void DropGiftsThePartnerGot(BasicNPC npc) {
        if (_settledTalkGifts.Count == 0 || npc.GiveOnFirstTalk is not { } items) {
            return;
        }

        var character = GetTalkGiftKey(npc);
        items.RemoveAll(item =>
            item != null && IsStoryItem(item, out _) &&
            _settledTalkGifts.Contains(character + "\n" + GetItemChangeKey(GetItemChange, item))
        );
    }

    /// <summary>
    /// The entry of a character that hands items over, which is the same in both games.
    /// </summary>
    private static string GetTalkGiftKey(BasicNPC npc) {
        return npc.gameObject.scene.name + "\n" + ScenePath.Get(npc.transform);
    }

    /// <summary>
    /// Sends an item of the story that a one-off story interaction used up, so that the partner doesn't keep a copy of
    /// something that the world already shows as used. What a wish takes, what dialogue about a wish takes and what a
    /// purchase costs are left to their own rules.
    /// </summary>
    private void NoticeStoryRemoval(CollectableItem item, int amount) {
        if (amount <= 0 || _applyingPartnerTalk || _applyingStoryItem || _purchaseDepth > 0 ||
            _consumingWish != null || _endingWishDepth > 0) {
            return;
        }

        // The wish sync takes what dialogue about a wish takes from both players already. It records while the
        // FSM of that dialogue runs, which outlasts the character letting the hero go, so both are asked here.
        // QuestConsumeTargetTake takes without raising _consumingWish; no item whose removal is shared is the
        // target of a wish, and tools/coop_story_items.py asserts that it stays that way.
        if (IsInTalkFsm() || (_wishTalk is { } talk && IsTalking(talk.Npc))) {
            return;
        }

        if (!IsStoryItem(item, out var entry) || !entry.ShareRemoval) {
            return;
        }

        SendStoryItem(GetItemChangeKey(TakeItemChange, item), amount, $"used up {item.name}");
    }

    /// <summary>
    /// Gives or takes an item of the story that the partner was given or used up.
    /// </summary>
    private void OnStoryItem(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker) ||
            !IsFromCurrentCheck(player, update) || !_appliedStoryItems.Add(update.Key)) {
            return;
        }

        var wasApplyingPartnerTalk = _applyingPartnerTalk;
        _applyingStoryItem = true;
        _applyingPartnerTalk = true;
        try {
            for (var i = 0; i < update.Records.Count && i < update.Amounts.Count; i++) {
                var change = update.Records[i];
                var parts = change.Split('\n');

                if (!ApplyTalkItem(change, update.Amounts[i])) {
                    continue;
                }

                // The character that handed this over hands it to the local player only once, but only now that
                // the local save really got it: a gift that never arrived has to stay there to be picked up later
                if (parts[0] == GetItemChange && update.ObjectPath.Length > 0) {
                    _settledTalkGifts.Add(update.Scene + "\n" + update.ObjectPath + "\n" + change);
                }

                var name = parts.Length == 3 ? parts[2] : "an item";
                Chat(parts[0] == TakeItemChange
                    ? $"{GetPartnerName()} used up {name}, so yours is gone too."
                    : $"{GetPartnerName()} was given {name}, so you got one too.");
                Logger.Info($"Applied the story item '{change.Replace('\n', ' ')}' of the partner");
            }
        } catch (Exception e) {
            Logger.Error($"Could not apply an item of the story of the partner:\n{e}");
        } finally {
            _applyingStoryItem = false;
            _applyingPartnerTalk = wasApplyingPartnerTalk;
        }
    }

    /// <summary>
    /// Sends one item of the story that the local save gave or took to the partner.
    /// </summary>
    /// <param name="change">The change, as <see cref="GetItemChangeKey"/> writes it.</param>
    /// <param name="amount">How many of the item were given or taken.</param>
    /// <param name="what">What the local player did, for the log.</param>
    /// <param name="npc">The character that handed the item over, for a gift of a first talk.</param>
    private void SendStoryItem(string change, int amount, string what, BasicNPC? npc = null) {
        if (!_everChecked || _checkedWith is not { } partnerId) {
            return;
        }

        var bytes = new byte[8];
        Random.NextBytes(bytes);
        var update = new CoopSaveUpdate {
            TargetId = partnerId,
            Kind = CoopSaveUpdateKind.StoryItem,
            Key = BitConverter.ToUInt64(bytes, 0)
        };

        if (npc != null) {
            update.Scene = npc.gameObject.scene.name;
            update.ObjectPath = ScenePath.Get(npc.transform);
        }

        update.Records.Add(change);
        update.Amounts.Add(amount);
        Send(update);
        Logger.Info($"Sent to the partner that the local player {what}");
    }

    /// <summary>
    /// Whether an item holds the story of the world, with what the list says about it.
    /// </summary>
    private static bool IsStoryItem(SavedItem item, out CoopStoryItem entry) {
        var key = GetStoryItemKey(item.GetType().FullName ?? "", item.name);
        if (GetStoryItems().TryGetValue(key, out var found) && found != null) {
            entry = found;
            return true;
        }

        entry = new CoopStoryItem();
        return false;
    }

    /// <summary>
    /// The items of the story, loaded from the embedded list when first needed.
    /// </summary>
    private static Dictionary<string, CoopStoryItem> GetStoryItems() {
        if (_storyItems != null) {
            return _storyItems;
        }

        _storyItems = new Dictionary<string, CoopStoryItem>(StringComparer.Ordinal);
        if (FileUtil.LoadObjectFromEmbeddedJson<CoopStoryItems>(StoryItemsFilePath) is not { } loaded) {
            Logger.Warn("Could not load the items of the story; they are not shared");
            return _storyItems;
        }

        foreach (var entry in loaded.Items) {
            if (entry.Type.Length > 0 && entry.Name.Length > 0) {
                _storyItems[GetStoryItemKey(entry.Type, entry.Name)] = entry;
            }
        }

        Logger.Info($"Loaded {_storyItems.Count} items of the story");
        return _storyItems;
    }

    /// <summary>
    /// The entry of an item of the story: the type that it is and its name.
    /// </summary>
    private static string GetStoryItemKey(string type, string name) => type + "\n" + name;
}
