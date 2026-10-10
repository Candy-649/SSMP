using System;
using System.Collections.Generic;
using System.Linq;
using SSMP.Networking.Packet.Data;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// What dialogue that accepted wishes gave and set, kept until both saves are known to have those wishes. A check of
/// the saves gives a save the wishes that only the partner's save accepted, and a save loses an accept when its game
/// closes before it saves - which one player's game did, just after both players had accepted a wish. The check gave
/// that save the accept back, but not what the dialogue had given and set along with it, which is how the character
/// knows that it offered the wish: it offered the wish again, as if for the first time.
/// Now each game keeps such dialogue in its own marker the moment it ends, whether it was the local player's and went
/// to the partner, or the partner's and came from them. When a check gives the local save back every wish that the
/// dialogue accepted, it gives back what the dialogue gave and set too, the way the partner's dialogue is taken
/// (<see cref="OnWishTurnIn"/>). The dialogue is forgotten once a check finds both saves with its wishes, since the game
/// keeps those in its own save from then on.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How many dialogues are kept at most; the oldest go first.
    /// </summary>
    private const int MaxAcceptTalks = 32;

    /// <summary>
    /// The wishes and rumours whose dialogue the current check gave back, by the keys from
    /// <see cref="GetWishChangeKey"/>, so that the dialogue kept by this game as the local player's and again as the
    /// partner's is not given twice.
    /// </summary>
    private readonly HashSet<string> _giftedWishKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Keeps dialogue that accepted wishes and did nothing else, as it went to or came from the partner. Dialogue that
    /// also completed a wish, or that turned in a delivery before, is left out: a check never gives a completed wish,
    /// so what the rest of such dialogue gave could not be told apart from what went with the completion. So is
    /// dialogue that accepted a delivery: whether a check gives a save a delivery goes by whether its player carries
    /// the item (<see cref="IsUncarriedDelivery"/>), and giving the item back as well would hand out a second one.
    /// </summary>
    /// <param name="update">The dialogue.</param>
    private void KeepAcceptTalk(CoopSaveUpdate update) {
        if (update.WishNames.Count == 0 || update.WishNames.Count != update.WishValues.Count || update.PartCount != 0 ||
            GetCurrentMarker() is not { } marker) {
            return;
        }

        for (var i = 0; i < update.WishNames.Count; i++) {
            var value = update.WishValues[i];
            if ((value & WishAccepted) == 0 || (value & WishCompleted) != 0) {
                return;
            }

            if ((value & RumourEntry) == 0 && FindQuest(update.WishNames[i]) is { } quest &&
                quest.Targets.Any(target => target.Counter is DeliveryQuestItem)) {
                return;
            }
        }

        marker.AcceptTalks.Add(new CoopAcceptTalk {
            WishNames = [..update.WishNames],
            WishValues = [..update.WishValues],
            ItemIds = [..update.ItemIds],
            Amounts = [..update.Amounts],
            FlagNames = [..update.FlagNames],
            FlagValues = [..update.FlagValues],
            Scene = update.Scene,
            Path = update.ObjectPath,
            Utc = DateTime.UtcNow
        });
        if (marker.AcceptTalks.Count > MaxAcceptTalks) {
            marker.AcceptTalks.RemoveRange(0, marker.AcceptTalks.Count - MaxAcceptTalks);
        }

        SaveMarkers();
    }

    /// <summary>
    /// Gives back what kept dialogue gave and set, for each dialogue all of whose wishes the current check just gave
    /// the local save (<see cref="AddWishes"/>).
    /// </summary>
    /// <returns>How many dialogues were given back.</returns>
    private int GiveBackAcceptTalks() {
        if (GetCurrentMarker() is not { } marker || marker.AcceptTalks.Count == 0 || _mergedWishKeys.Count == 0) {
            return 0;
        }

        var given = 0;
        foreach (var talk in marker.AcceptTalks) {
            var keys = GetAcceptTalkKeys(talk);
            if (keys.Count == 0 || !keys.All(_mergedWishKeys.Contains) || keys.Any(_giftedWishKeys.Contains)) {
                continue;
            }

            _giftedWishKeys.UnionWith(keys);

            var update = new CoopSaveUpdate {
                Kind = CoopSaveUpdateKind.WishTurnIn,
                WishNames = [..talk.WishNames],
                WishValues = [..talk.WishValues],
                FlagNames = [..talk.FlagNames],
                FlagValues = [..talk.FlagValues]
            };

            var items = 0;
            var flags = 0;
            _applyingPartnerTalk = true;
            try {
                for (var i = 0; i < talk.ItemIds.Count && i < talk.Amounts.Count; i++) {
                    var entry = talk.ItemIds[i];
                    var split = entry.IndexOf('\n');
                    if (split <= 0 || !int.TryParse(entry.Substring(0, split), out var index) ||
                        (index >= 0 ? index >= talk.WishNames.Count : index != AllWishesIndex)) {
                        continue;
                    }

                    if (ApplyTalkItem(entry.Substring(split + 1), talk.Amounts[i])) {
                        items++;
                    }
                }

                flags = ApplyInteractionFlags(update);
            } finally {
                _applyingPartnerTalk = false;
            }

            given++;
            Logger.Info(
                $"Gave back what the dialogue at '{talk.Path}' in {talk.Scene} gave with " +
                $"{string.Join(", ", talk.WishNames)}, whose accept this save had lost: {items} item changes and " +
                $"{flags} flags"
            );
        }

        return given;
    }

    /// <summary>
    /// Forgets the kept dialogues whose wishes every save had as the current check started, which the game keeps in
    /// every save from then on.
    /// </summary>
    /// <param name="memberEntries">The wish log that the save of each member sent for the check.</param>
    private void ForgetSharedAcceptTalks(List<List<(string Name, int Value)>> memberEntries) {
        if (GetCurrentMarker() is not { } marker || marker.AcceptTalks.Count == 0) {
            return;
        }

        var local = GetHeldWishKeys(_sentWishEntries);
        var members = memberEntries.Select(GetHeldWishKeys).ToList();
        var forgotten = marker.AcceptTalks.RemoveAll(talk => {
            var keys = GetAcceptTalkKeys(talk);
            return keys.All(key => local.Contains(key) && members.All(member => member.Contains(key)));
        });

        if (forgotten > 0) {
            Logger.Info($"Forgot {forgotten} dialogues that accepted wishes, which every save has now");
        }
    }

    /// <summary>
    /// The keys of the wishes and rumours that kept dialogue accepted, from <see cref="GetWishChangeKey"/>.
    /// </summary>
    private static List<string> GetAcceptTalkKeys(CoopAcceptTalk talk) {
        var keys = new List<string>();
        for (var i = 0; i < talk.WishNames.Count && i < talk.WishValues.Count; i++) {
            keys.Add(GetWishChangeKey(talk.WishNames[i], talk.WishValues[i]));
        }

        return keys;
    }

    /// <summary>
    /// The keys of the wishes and rumours of a wish log that are accepted or were ever completed.
    /// </summary>
    private static HashSet<string> GetHeldWishKeys(IEnumerable<(string Name, int Value)> entries) {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, value) in entries) {
            if ((value & (WishAccepted | WishCompleted | WishEverCompleted)) != 0) {
                keys.Add(GetWishChangeKey(name, value));
            }
        }

        return keys;
    }
}
