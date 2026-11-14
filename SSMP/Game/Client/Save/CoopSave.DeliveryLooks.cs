using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// What another player carries for a delivery, shown on their character.
///
/// A player who carries a delivery has its look on them - the gravy that drips off the meat of a courier's delivery -
/// which the game makes on the hero as they take the item (HeroController.SetupDeliveryItems) rather than switching on
/// a part of the hero, so the other players never saw it (USER 11-14 "拿到送餐要用的肉之后身上会有一个肉汁的特效，这个也
/// 没有同步"). Each game tells the members in its room which deliveries its own hero shows, as that changes and to a
/// member who comes into the room, and the character of that player shows the same until they leave the room.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The start of the name of the copy of the look of a delivery on the character of another player, which the name
    /// of the item ends.
    /// </summary>
    private const string PartnerDeliveryName = "Partner Delivery ";

    /// <summary>
    /// The looks of the deliveries that a hero carries, by item (HeroController.spawnedDeliveryEffects).
    /// </summary>
    private static readonly FieldInfo DeliveryLooksField =
        typeof(HeroController).GetField("spawnedDeliveryEffects", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>
    /// The deliveries whose looks the members in the room were last told the local hero shows, by the name of the item.
    /// </summary>
    private readonly HashSet<string> _deliveryLooksTold = [];

    /// <summary>
    /// The hooks that see the looks of the local hero's deliveries come and go.
    /// </summary>
    private readonly List<Hook?> _deliveryLookHooks = [];

    /// <summary>
    /// Watches the looks of the local hero's deliveries come and go: they are made as an item is taken, and taken away
    /// as one is handed over, breaks or is no longer carried as the hero leaves a room.
    /// </summary>
    private void RegisterDeliveryLookHooks() {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        _deliveryLookHooks.Add(CreateHook(
            typeof(HeroController).GetMethod("SetupDeliveryItems", flags, null, Type.EmptyTypes, null),
            (Action<Action<HeroController>, HeroController>) ((orig, self) => {
                orig(self);
                TellDeliveryLooks(self);
            })
        ));
        _deliveryLookHooks.Add(CreateHook(
            typeof(HeroController).GetMethod("CleanSpawnedDeliveryEffects", flags, null, Type.EmptyTypes, null),
            (Action<Action<HeroController>, HeroController>) ((orig, self) => {
                orig(self);
                TellDeliveryLooks(self);
            })
        ));
        _deliveryLookHooks.Add(CreateHook(
            typeof(HeroController).GetMethod(
                "RemoveDeliveryItemEffect", flags, null, [typeof(DeliveryQuestItem)], null
            ),
            (Action<Action<HeroController, DeliveryQuestItem>, HeroController, DeliveryQuestItem>) (
                (orig, self, item) => {
                    orig(self, item);
                    TellDeliveryLooks(self);
                }
            )
        ));
    }

    /// <summary>
    /// The names of the deliveries whose looks a hero shows.
    /// </summary>
    /// <param name="hero">The hero.</param>
    private static List<string> DeliveryLooksOf(HeroController hero) {
        var looks = new List<string>();
        foreach (DictionaryEntry look in (IDictionary) DeliveryLooksField.GetValue(hero)) {
            if (look.Key is DeliveryQuestItem item && item != null && look.Value is Object shown && shown != null) {
                looks.Add(item.name);
            }
        }

        return looks;
    }

    /// <summary>
    /// Tells the members in the room which deliveries the local hero shows the looks of, if that changed.
    /// </summary>
    /// <param name="hero">The local hero.</param>
    private void TellDeliveryLooks(HeroController hero) {
        var looks = DeliveryLooksOf(hero);
        if (_deliveryLooksTold.SetEquals(looks)) {
            return;
        }

        _deliveryLooksTold.Clear();
        _deliveryLooksTold.UnionWith(looks);
        Logger.Info(
            $"[Deliveries] The local player shows the looks of {NamesOrNone(looks)}, which the members in the room " +
            "are told"
        );
        SendToRoomMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.DeliveryLooks,
            Names = looks
        });
    }

    /// <summary>
    /// Tells a member who has come into the room, or into whose room the local player has come, which deliveries the
    /// local hero shows the looks of, if any. Their game makes the character of the local player anew, without them.
    /// </summary>
    /// <param name="player">The member.</param>
    private void SendDeliveryLooksTo(ClientPlayerData player) {
        if (!_checkedMembers.Contains(player.Id) || HeroController.instance is not { } hero ||
            DeliveryLooksOf(hero) is not { Count: > 0 } looks) {
            return;
        }

        Logger.Info(
            $"[Deliveries] {player.Username} is told that the local player shows the looks of {NamesOrNone(looks)}, " +
            "as they meet in the room"
        );
        Send(new CoopSaveUpdate {
            TargetId = player.Id,
            Kind = CoopSaveUpdateKind.DeliveryLooks,
            Names = looks
        });
    }

    /// <summary>
    /// Shows on the character of another player the looks of the deliveries they carry in their game, and no others.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="update">The update, whose <see cref="CoopSaveUpdate.Names"/> names the items.</param>
    private void OnDeliveryLooks(ClientPlayerData player, CoopSaveUpdate update) {
        var character = player.PlayerObject;
        if (!player.IsInLocalScene || character == null) {
            Logger.Info(
                $"[Deliveries] {player.Username} shows the looks of {NamesOrNone(update.Names)}, with no character " +
                "of theirs in this room to show them on"
            );
            return;
        }

        foreach (var shown in ShownDeliveryLooks(character)) {
            if (!update.Names.Contains(shown.name.Substring(PartnerDeliveryName.Length))) {
                Object.Destroy(shown);
            }
        }

        foreach (var name in update.Names) {
            if (character.transform.Find(PartnerDeliveryName + name) != null) {
                continue;
            }

            // A delivery with no wish is of a kind of its own (DeliveryQuestItemStandalone)
            if ((FindSavedItem(typeof(DeliveryQuestItem).FullName!, name) ??
                 FindSavedItem(typeof(DeliveryQuestItemStandalone).FullName!, name)) is not DeliveryQuestItem item ||
                item.HeroLoopEffect == null) {
                Logger.Info($"[Deliveries] {player.Username} carries '{name}', which has no look known here");
                continue;
            }

            var look = item.HeroLoopEffect.Spawn(character.transform, Vector3.zero);
            look.name = PartnerDeliveryName + name;
        }

        Logger.Info(
            $"[Deliveries] {player.Username} shows the looks of {NamesOrNone(update.Names)}, as their character " +
            "does now"
        );
    }

    /// <summary>
    /// The names of items for the log, or "none".
    /// </summary>
    private static string NamesOrNone(List<string> names) => names.Count > 0 ? string.Join(", ", names) : "none";

    /// <summary>
    /// A member left the room: their character stops showing the looks of their deliveries, which they tell the room
    /// again when they come back.
    /// </summary>
    /// <param name="player">The member.</param>
    private static void HideDeliveryLooksOf(ClientPlayerData player) {
        if (player.PlayerObject is not { } character) {
            return;
        }

        foreach (var shown in ShownDeliveryLooks(character)) {
            Object.Destroy(shown);
        }
    }

    /// <summary>
    /// The copies of the looks of deliveries on the character of another player.
    /// </summary>
    /// <param name="character">The character.</param>
    private static List<GameObject> ShownDeliveryLooks(GameObject character) {
        var shown = new List<GameObject>();
        foreach (Transform child in character.transform) {
            if (child.name.StartsWith(PartnerDeliveryName, StringComparison.Ordinal)) {
                shown.Add(child.gameObject);
            }
        }

        return shown;
    }
}
