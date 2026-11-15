using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The wishes that a shop of wishes picks anew to offer, like the deliveries of the couriers. The game picks them at
/// random, from those it may give again, when the shop opens with fewer than two of them left, keeps them in the save,
/// and offers them until one of them is handed in, which uses them up (DeliveryQuestItem.Consume). Each game picked its
/// own, and the players were offered different ones (USER 11-15 "这个钟道镇送货员这里我和我朋友看到的货物不一样").
/// Now every change to them goes to the other players, whose saves take it: what the game whose player opens the shop
/// picks, and their using up in the game whose player hands one in. A change counts one more than the last one that its
/// game knew of and has a key of its own, and a game takes one only if it counts more than the last one it took, or as
/// many under a larger key: of two changes made at the same moment, each before the other's came, every game keeps the
/// same one, and a change that the network brings after a newer one is passed over. A member who comes into the save
/// took the host's picks as their save loaded (coop-host-sync.json), and once the round finished, each game tells them
/// the last change that it knows.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The field of a shop of wishes that names the field of the player data in which it keeps what it picked.
    /// </summary>
    private static readonly FieldInfo? QuestShopPicksField =
        typeof(SimpleQuestsShopOwner).GetField("genericQuestsList", InstanceFlags);

    /// <summary>
    /// The last change to what each shop of wishes picked that this game made or took in this session, by the field of
    /// the player data that keeps the picks: how many changes it counts, and its key.
    /// </summary>
    private readonly Dictionary<string, (ulong Count, ulong Key)> _questShopChanges = new(StringComparer.Ordinal);

    /// <summary>
    /// How deep the list of a shop of wishes is being made: the game makes it again from within when it picked more
    /// than it could offer.
    /// </summary>
    private int _questShopDepth;

    /// <summary>
    /// The hook that sees the shops of wishes make their lists.
    /// </summary>
    private Hook? _questShopHook;

    /// <summary>
    /// The hooks that see a delivery that is handed in, or anything else, use up the couriers' picks.
    /// </summary>
    private Hook? _deliveryConsumeHook;

    private Hook? _clearCourierPicksHook;

    /// <summary>
    /// Whether the couriers' picks were told used up while a delivery was handed in (see OnDeliveryConsume).
    /// </summary>
    private bool _courierPicksUseTold;

    /// <summary>
    /// Watches the shops of wishes make their lists, and the deliveries handed in use up the couriers' picks.
    /// </summary>
    private void RegisterQuestShopHooks() {
        _questShopHook = CreateHook(
            typeof(SimpleQuestsShopOwner).GetMethod("GetItems", InstanceFlags, null, Type.EmptyTypes, null),
            new Func<Func<SimpleQuestsShopOwner, List<ISimpleShopItem>>, SimpleQuestsShopOwner, List<ISimpleShopItem>>(
                OnQuestShopItems
            )
        );

        // Consume as well as the ClearGenericQuests that it calls, which is short enough to be inlined into it. A
        // state machine calls ClearGenericQuests itself too, as the wish that readies the last fight's other end is
        // granted (song_enclave, 'Enclave Caretaker' 'Wish Granted')
        _deliveryConsumeHook = CreateHook(
            typeof(DeliveryQuestItem).GetMethod(
                nameof(DeliveryQuestItem.Consume), InstanceFlags, null, [typeof(int), typeof(bool)], null
            ),
            new Action<Action<DeliveryQuestItem, int, bool>, DeliveryQuestItem, int, bool>(OnDeliveryConsume)
        );
        _clearCourierPicksHook = CreateHook(
            typeof(DeliveryQuestItem).GetMethod(
                nameof(DeliveryQuestItem.ClearGenericQuests), StaticFlags, null, Type.EmptyTypes, null
            ),
            new Action<Action>(OnClearCourierPicks)
        );
    }

    /// <summary>
    /// Hook for the list of a shop of wishes, which tells the other players what it picked anew here.
    /// </summary>
    private List<ISimpleShopItem> OnQuestShopItems(
        Func<SimpleQuestsShopOwner, List<ISimpleShopItem>> orig,
        SimpleQuestsShopOwner self
    ) {
        if (QuestShopPicksField?.GetValue(self) is not string { Length: > 0 } name ||
            GetPlayerDataField(name) is not { } field) {
            return orig(self);
        }

        var before = PicksIn(field);
        _questShopDepth++;
        List<ISimpleShopItem> items;
        try {
            items = orig(self);
        } finally {
            _questShopDepth--;
        }

        var after = PicksIn(field);
        if (_questShopDepth == 0 && !after.SequenceEqual(before)) {
            TellQuestShopPicks(name, after);
        }

        return items;
    }

    /// <summary>
    /// Hook for a delivery that is handed in, which uses up the couriers' picks (DeliveryQuestItem.ClearGenericQuests),
    /// and so does it in the saves of the other players.
    /// </summary>
    private void OnDeliveryConsume(
        Action<DeliveryQuestItem, int, bool> orig,
        DeliveryQuestItem self,
        int amount,
        bool showCounter
    ) {
        var had = PlayerData.instance.BelltownCouriersGenericQuests is { Count: > 0 };
        _courierPicksUseTold = false;
        orig(self, amount, showCounter);
        if (had && !_courierPicksUseTold) {
            TellQuestShopPicks(nameof(PlayerData.BelltownCouriersGenericQuests), []);
        }
    }

    /// <summary>
    /// Hook for the couriers' picks being used up, which does so in the saves of the other players too.
    /// </summary>
    private void OnClearCourierPicks(Action orig) {
        var had = PlayerData.instance.BelltownCouriersGenericQuests is { Count: > 0 };
        orig();
        if (had) {
            TellQuestShopPicks(nameof(PlayerData.BelltownCouriersGenericQuests), []);
            _courierPicksUseTold = true;
        }
    }

    /// <summary>
    /// The wishes that the save keeps in a field of the player data as picked to offer.
    /// </summary>
    private static List<string> PicksIn(FieldInfo field) {
        return field.GetValue(PlayerData.instance) is List<string> picks ? picks.ToList() : [];
    }

    /// <summary>
    /// The picks of a shop of wishes, for the log.
    /// </summary>
    private static string DescribePicks(List<string> picks) {
        return picks.Count > 0 ? string.Join(", ", picks) : "none";
    }

    /// <summary>
    /// Tells the other players a change made here to what a shop of wishes picked: one more than the last change that
    /// this game knows, under a new key.
    /// </summary>
    /// <param name="name">The field of the player data that keeps the picks.</param>
    /// <param name="picks">The picks after the change.</param>
    private void TellQuestShopPicks(string name, List<string> picks) {
        var count = (_questShopChanges.TryGetValue(name, out var last) ? last.Count : 0) + 1;
        var bytes = new byte[8];
        Random.NextBytes(bytes);
        var key = BitConverter.ToUInt64(bytes, 0);
        _questShopChanges[name] = (count, key);
        Logger.Info(
            $"[Wishes] The shop that keeps its offers in '{name}' holds {DescribePicks(picks)} here, change {count}, " +
            "which the other players are told"
        );
        SendToMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.QuestShopPicks,
            ObjectPath = name,
            Names = picks,
            Sequence = count,
            Key = key
        });
    }

    /// <summary>
    /// Takes a change to what a shop of wishes picked from another player's game into the save, unless this game knows
    /// a later change, or one as late under a larger key.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="update">The update.</param>
    private void OnQuestShopPicks(ClientPlayerData player, CoopSaveUpdate update) {
        if (!IsFromMemberInSave(player) || GetPlayerDataField(update.ObjectPath) is not { } field) {
            return;
        }

        var name = update.ObjectPath;
        var picks = DescribePicks(update.Names);
        if (_questShopChanges.TryGetValue(name, out var last) &&
            (last.Count > update.Sequence || last.Count == update.Sequence && last.Key >= update.Key)) {
            Logger.Info(
                $"[Wishes] {player.Username}'s game holds {picks} for '{name}', change {update.Sequence}, which this " +
                $"game passes over for its change {last.Count}"
            );
            return;
        }

        var here = PicksIn(field);
        _questShopChanges[name] = (update.Sequence, update.Key);
        field.SetValue(PlayerData.instance, update.Names.ToList());
        Logger.Info(
            $"[Wishes] {player.Username}'s game holds {picks} for '{name}', change {update.Sequence}, which this " +
            $"save takes in place of {DescribePicks(here)}"
        );
    }

    /// <summary>
    /// Tells the members of a round that just finished the last change that this game knows to what each shop of wishes
    /// picked: a member who came into the save took the host's picks as their save loaded, but not how many changes
    /// they count, and one who joined while a change went never heard it.
    /// </summary>
    private void TellQuestShopPicksAfterCheck(List<ClientPlayerData> members) {
        foreach (var pair in _questShopChanges) {
            var picks = PicksIn(GetPlayerDataField(pair.Key)!);
            foreach (var member in members) {
                Send(new CoopSaveUpdate {
                    TargetId = member.Id,
                    Kind = CoopSaveUpdateKind.QuestShopPicks,
                    ObjectPath = pair.Key,
                    Names = picks,
                    Sequence = pair.Value.Count,
                    Key = pair.Value.Key
                });
            }
        }
    }
}
