using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Purchases in shops of what both saves share: the furnishings and the paint of the one house that the players share
/// are bought by one player for both, and the other can't buy them again (the user's rule of 2026-10-18). What such an
/// item sets are story flags (tools/coop_story_flags.py), so the purchase goes to the partner with them. A partner who
/// has the shop's list open sees the item gone from it, once they are back in the list if they were confirming
/// something, and one who confirms it after it was bought is told so and pays nothing. Two players who bought it at the
/// same moment, each before the other's purchase arrived, are settled as a toll is (RefundIfBothPaid): the one whose
/// purchase has the larger key gets their payment back.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The FSM of the shop's prompt that asks to confirm a purchase, the state in which it pays and sets what the item
    /// sets, the state that a purchase without an event after it goes on to, and the variable of the item asked about.
    /// </summary>
    private const string ConfirmFsmName = "Confirm Control";
    private const string ConfirmPayState = "Deduct Geo and set PD";
    private const string ConfirmNoStockState = "No Stock?";
    private const string ConfirmItemVariable = "Item GameObject";
    private const string ConfirmShopVariable = "Shop Window";

    /// <summary>
    /// The FSM of the shop's window, its state while the player looks through the list, the variables of the item the
    /// list shows first and of a prompt open over the list, and the events that build the list again and close it.
    /// </summary>
    private const string ShopFsmName = "shop_control";
    private const string ShopOpenState = "Open";
    private const string ShopCurrentItemVariable = "Current Item";
    private const string ShopSecondMenuVariable = "In Second Menu";
    private const string ShopResetEvent = "RESET SHOP WINDOW";
    private const string ShopCloseEvent = "CLOSE SHOP WINDOW";

    private static readonly FieldInfo? ShopItemBoolField =
        typeof(ShopItem).GetField("playerDataBoolName", InstanceFlags);
    private static readonly FieldInfo? ShopItemIntField =
        typeof(ShopItem).GetField("playerDataIntName", InstanceFlags);

    /// <summary>
    /// The purchases of what both saves share that the local player made in this session, by the flag they set.
    /// </summary>
    private readonly Dictionary<string, LocalInteraction> _localPurchases = new(StringComparer.Ordinal);

    /// <summary>
    /// The purchases of what both saves share that other members made in this session, by the flag they set, with the
    /// key of the first of them and the name of who made it, for messages.
    /// </summary>
    private readonly Dictionary<string, (ulong Key, string Name)> _remotePurchases = new(StringComparer.Ordinal);

    /// <summary>
    /// The flags of the purchases of other members that a shop's list which the local player has open still shows, and
    /// drops once the local player is back in it from a prompt over it (see <see cref="UpdateSharedPurchases"/>).
    /// </summary>
    private readonly HashSet<string> _purchasesToDropFromShop = new(StringComparer.Ordinal);

    /// <summary>
    /// A purchase of the local player of something both saves share, from before it is paid for: the item, the flag it
    /// sets with its value before, and what it costs.
    /// </summary>
    private sealed record SharedPurchase(ShopItem Item, string Flag, int? Before, int Cost, CurrencyType Currency);

    /// <summary>
    /// Forgets the purchases of what both saves share, for a new session.
    /// </summary>
    private void ResetSharedPurchases() {
        _localPurchases.Clear();
        _remotePurchases.Clear();
        _purchasesToDropFromShop.Clear();
    }

    /// <summary>
    /// The story flag that a shop item sets, if both saves share it, or null.
    /// </summary>
    private static string? GetSharedPurchaseFlag(ShopItem? item) {
        if (item == null) {
            return null;
        }

        GetStoryFields();
        foreach (var field in (FieldInfo?[]) [ShopItemBoolField, ShopItemIntField]) {
            if (field?.GetValue(item) is string { Length: > 0 } name && StoryFieldIndices.ContainsKey(name)) {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// Takes note of what a purchase of the local player is about to cost, if it is of something both saves share.
    /// </summary>
    /// <returns>The purchase, or null for one of anything else.</returns>
    private SharedPurchase? BeforeSharedPurchase(ShopItem item) {
        try {
            if (!_everChecked || GetSharedPurchaseFlag(item) is not { } flag) {
                return null;
            }

            return new SharedPurchase(item, flag, ReadPlayerDataFlag(flag), item.Cost, item.CurrencyType);
        } catch (Exception e) {
            LogInteractionError(e);
            return null;
        }
    }

    /// <summary>
    /// The local player bought something both saves share: the purchase goes to the other members with what it set,
    /// and a purchase of another member that had arrived already makes it theirs, so the local payment goes back.
    /// </summary>
    private void AfterSharedPurchase(SharedPurchase? purchase) {
        if (purchase == null) {
            return;
        }

        try {
            if (ReadPlayerDataFlag(purchase.Flag) is not { } value || value == purchase.Before) {
                return;
            }

            var interaction = new LocalInteraction(purchase.Item);
            interaction.Currency.Add((purchase.Currency, purchase.Cost));
            _localPurchases[purchase.Flag] = interaction;
            var afterPartner = _remotePurchases.TryGetValue(purchase.Flag, out var first);
            var name = purchase.Item.DisplayName;
            if (_checkedMembers.Count > 0) {
                var update = new CoopSaveUpdate {
                    Kind = CoopSaveUpdateKind.SharedPurchase,
                    Key = interaction.Key,
                    PartCount = afterPartner ? InteractionAfterPartner : InteractionFirst,
                    StateName = name
                };
                update.FlagNames.Add(purchase.Flag);
                update.FlagValues.Add(value);
                SendToMembers(update);

                // It goes to the partner with the purchase, so the story flags don't send it again
                RememberStoryValues([purchase.Flag]);
                Logger.Info($"Bought '{name}' for both saves ({purchase.Flag} = {value}), sent to {GetCheckedNames()}");
            }

            if (afterPartner) {
                Refund(interaction, Lang.Pick("had already bought it", "已经买下了它"), first.Name);
            }
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Takes the purchase of another member of something both saves share: what it set goes into the local save, a
    /// purchase of the same thing by the local player that crossed it on the way is settled, and a shop's list that
    /// the local player has open drops it.
    /// </summary>
    private void OnSharedPurchase(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker)) {
            return;
        }

        if (!_checkedMembers.Contains(player.Id)) {
            AddInteractionDuringCheck(player, update);
            return;
        }

        try {
            ApplyInteractionFlags(update);
            foreach (var flag in update.FlagNames) {
                _remotePurchases.TryAdd(flag, (update.Key, player.Username));

                // Both players bought it. One who bought it after the purchase of the other had arrived gets the
                // payment back in their own game, so only purchases that crossed on the way are settled here
                if (_localPurchases.TryGetValue(flag, out var local)) {
                    if (update.PartCount != InteractionAfterPartner) {
                        RefundIfBothPaid(local, update.Key, player.Username);
                    }

                    continue;
                }

                Chat(Lang.Pick(
                    $"{player.Username} bought {update.StateName}, and you have it too.",
                    $"{player.Username}买下了{update.StateName}，你这边也有了。"
                ));
                _purchasesToDropFromShop.Add(flag);
            }

            UpdateSharedPurchases();
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Builds a shop's list that the local player has open again without what another member bought, once the local
    /// player looks through the list rather than at a prompt over it, or closes the shop if nothing is left in it.
    /// </summary>
    private void UpdateSharedPurchases() {
        if (_purchasesToDropFromShop.Count == 0) {
            return;
        }

        var waits = false;
        var stocks = Object.FindObjectsByType<ShopMenuStock>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (var stock in stocks) {
            if (stock == null || !stock.EnumerateStock().Any(item =>
                    GetSharedPurchaseFlag(item) is { } flag && _purchasesToDropFromShop.Contains(flag))) {
                continue;
            }

            var shop = FindShopFsm(stock.gameObject);
            if (shop == null || shop.ActiveStateName != ShopOpenState ||
                shop.FsmVariables.FindFsmBool(ShopSecondMenuVariable) is { Value: true }) {
                waits |= shop != null;
                continue;
            }

            if (!stock.StockLeft()) {
                Logger.Info("Closing the shop: what the partner bought was the last thing in it");
                EventRegister.SendEvent(ShopCloseEvent, null);
                continue;
            }

            // The list shows from the same place as before, but past its end
            var count = stock.EnumerateStock().Count(item => item.IsAvailable);
            if (shop.FsmVariables.FindFsmInt(ShopCurrentItemVariable) is { } current && current.Value >= count) {
                current.Value = System.Math.Max(0, count - 1);
            }

            Logger.Info("Building the open shop's list again without what the partner bought");
            EventRegister.SendEvent(ShopResetEvent, null);
        }

        // A shop that the local player still confirms something in, or picks a colour in, drops it once they are back
        // in its list; one that is closed has nothing to drop
        if (!waits) {
            _purchasesToDropFromShop.Clear();
        }
    }

    /// <summary>
    /// The FSM of a shop's window, which sits on the object of its stock.
    /// </summary>
    private static PlayMakerFSM? FindShopFsm(GameObject gameObject) {
        return gameObject.GetComponents<PlayMakerFSM>()
            .FirstOrDefault(fsm => fsm != null && fsm.FsmName == ShopFsmName);
    }

    /// <summary>
    /// The prompt of a shop that asks to confirm a purchase is about to pay for something both saves share that another
    /// member bought while the local player looked at it, which is not bought again: the prompt goes on as after a
    /// purchase without an event after it, which builds the list again, or closes the shop if nothing is left in it,
    /// and the list shows from its first item as after a purchase (state "Bob").
    /// </summary>
    private FsmState RedirectBoughtPurchase(Fsm fsm, FsmState toState) {
        if (toState.Name != ConfirmPayState || fsm.Name != ConfirmFsmName) {
            return toState;
        }

        var itemObject = fsm.Variables.FindFsmGameObject(ConfirmItemVariable)?.Value;
        var item = itemObject != null && itemObject.TryGetComponent<ShopItemStats>(out var stats) ? stats.Item : null;
        if (item == null || GetSharedPurchaseFlag(item) is not { } flag || !item.IsPurchased ||
            fsm.GetState(ConfirmNoStockState) is not { } noStock) {
            return toState;
        }

        var shopWindow = fsm.Variables.FindFsmGameObject(ConfirmShopVariable)?.Value;
        var shop = shopWindow != null ? FindShopFsm(shopWindow) : null;
        if (shop != null && shop.FsmVariables.FindFsmInt(ShopCurrentItemVariable) is { } current) {
            current.Value = 0;
        }

        var buyer = _remotePurchases.TryGetValue(flag, out var first) ? first.Name : GetPartnerName();
        Chat(Lang.Pick(
            $"{buyer} has already bought {item.DisplayName}, so you paid nothing.",
            $"{buyer}已经买下了{item.DisplayName}，这次没有收你的钱。"
        ));
        Logger.Info($"Not buying '{item.DisplayName}' again: {buyer} bought it for both saves already");
        return noStock;
    }
}
