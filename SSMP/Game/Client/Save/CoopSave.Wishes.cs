using System;
using System.Collections.Generic;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The wish log of a checked two-player save. When a wish or rumour gets accepted, completed or dropped in the game of
/// one player, the wish log of the partner gets the same at once, even when the partner is in another room, so both
/// players follow the same wishes. What each player has seen in their log stays their own. The check of the saves adds
/// the wishes that only the partner accepted, and counts the wishes that only one save completed, which aren't copied
/// because they come with rewards. Later changes of those wishes only share whether they are accepted.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How often the wish log is compared with the known one when the game didn't count a change, in seconds, because
    /// a few changes of the game aren't counted.
    /// </summary>
    private const float WishCheckInterval = 1f;

    /// <summary>
    /// The bit of a packed wish or rumour that says it is accepted.
    /// </summary>
    private const int WishAccepted = 1 << 1;

    /// <summary>
    /// The bit of a packed wish that says it is completed.
    /// </summary>
    private const int WishCompleted = 1 << 2;

    /// <summary>
    /// The bit of a packed wish that says it was ever completed.
    /// </summary>
    private const int WishEverCompleted = 1 << 3;

    /// <summary>
    /// The bit at which the number of completions starts in a packed wish.
    /// </summary>
    private const int WishCountShift = 4;

    /// <summary>
    /// The largest number of completions that a packed wish holds.
    /// </summary>
    private const int MaxWishCount = (1 << 26) - 1;

    /// <summary>
    /// The bit of a packed entry of the wish log that says it is a rumour rather than a wish.
    /// </summary>
    private const int RumourEntry = 1 << 30;

    /// <summary>
    /// The packed wishes of the local wish log as both games last knew them, by name.
    /// </summary>
    private readonly Dictionary<string, int> _knownWishes = new(StringComparer.Ordinal);

    /// <summary>
    /// The packed rumours of the local wish log as both games last knew them, by name.
    /// </summary>
    private readonly Dictionary<string, int> _knownRumours = new(StringComparer.Ordinal);

    /// <summary>
    /// The version of the quests of the game when the wish log was last compared, or -1.
    /// </summary>
    private int _knownQuestVersion = -1;

    /// <summary>
    /// When the wish log is compared next without a counted change.
    /// </summary>
    private float _nextWishCheckTime;

    /// <summary>
    /// How many wishes the current check found completed in only one of the saves.
    /// </summary>
    private int _differentWishes;

    /// <summary>
    /// The wishes that the current check found completed in only one of the saves. Their changes only share whether they
    /// are accepted, so that the completion of one save doesn't get copied later.
    /// </summary>
    private readonly HashSet<string> _differentWishNames = new(StringComparer.Ordinal);

    /// <summary>
    /// The wishes and rumours that the local save sent for the current check.
    /// </summary>
    private List<(string Name, int Value)> _sentWishEntries = [];

    /// <summary>
    /// The wishes and rumours that the current check changed in the local wish log, by the keys from
    /// <see cref="GetWishChangeKey"/>.
    /// </summary>
    private readonly HashSet<string> _mergedWishKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether syncing the wish log threw, which is only logged once.
    /// </summary>
    private bool _wishSyncFailed;

    /// <summary>
    /// Forgets the wish log that both games know, for a new check or session.
    /// </summary>
    private void ResetWishes() {
        _knownWishes.Clear();
        _knownRumours.Clear();
        _knownQuestVersion = -1;
        _nextWishCheckTime = 0f;
    }

    /// <summary>
    /// Takes the wish log that the check made both games agree on as the one that both games know: what the local save
    /// sent, with what the check and the partner changed since. What the local player changed after the local save sent
    /// its wish log differs from it, so it goes to the partner.
    /// </summary>
    private void RememberWishes() {
        ResetWishes();
        foreach (var (name, value) in _sentWishEntries) {
            RememberWish(name, value);
        }

        foreach (var (name, value) in GetWishEntries()) {
            var key = GetWishChangeKey(name, value);
            if (_mergedWishKeys.Contains(key) || _wishSequences.ContainsKey(key)) {
                RememberWish(name, value);
            }
        }

        _knownQuestVersion = QuestManager.Version;
    }

    /// <summary>
    /// Remembers the packed state of a wish or rumour as known by both games.
    /// </summary>
    private void RememberWish(string name, int value) {
        if ((value & RumourEntry) != 0) {
            _knownRumours[name] = value;
        } else {
            _knownWishes[name] = value;
        }
    }

    /// <summary>
    /// The key of a wish or rumour for its last change: its name, with "rumour:" before the name of a rumour.
    /// </summary>
    private static string GetWishChangeKey(string name, int value) {
        return (value & RumourEntry) != 0 ? "rumour:" + name : name;
    }

    /// <summary>
    /// Sends the wishes and rumours whose state changed in the local wish log to the partner.
    /// </summary>
    private void UpdateWishes(ClientPlayerData partner) {
        var playerData = PlayerData.instance;
        if (playerData == null) {
            return;
        }

        var version = QuestManager.Version;
        if (version == _knownQuestVersion && Time.unscaledTime < _nextWishCheckTime) {
            return;
        }

        _knownQuestVersion = version;
        _nextWishCheckTime = Time.unscaledTime + WishCheckInterval;

        try {
            CoopSaveUpdate? update = null;
            foreach (var pair in playerData.QuestCompletionData.Enumerate()) {
                AddWishChange(ref update, partner, _knownWishes, pair.Key, PackCompletion(pair.Value));
            }

            foreach (var pair in playerData.QuestRumourData.Enumerate()) {
                AddWishChange(ref update, partner, _knownRumours, pair.Key, PackRumour(pair.Value));
            }

            if (update != null) {
                Send(update);
                Logger.Info($"Sent {update.WishNames.Count} changes of the wish log to {partner.Username}");
            }
        } catch (Exception e) {
            LogWishError(e);
        }
    }

    /// <summary>
    /// Adds a wish or rumour to the update with the changes of the wish log for the partner if its state isn't the known
    /// one, and remembers the new state as known.
    /// </summary>
    private static void AddWishChange(
        ref CoopSaveUpdate? update,
        ClientPlayerData partner,
        Dictionary<string, int> known,
        string name,
        int value
    ) {
        // An entry that isn't known is in its first state, which packs to the kind of entry alone
        var knownValue = known.TryGetValue(name, out var previous) ? previous : value & RumourEntry;
        if (knownValue == value) {
            return;
        }

        known[name] = value;
        update ??= new CoopSaveUpdate { TargetId = partner.Id, Kind = CoopSaveUpdateKind.WishChange };
        update.WishNames.Add(name);
        update.WishValues.Add(value);
    }

    /// <summary>
    /// Adds the wishes and rumours whose state changed in the wish log of the partner to the local wish log. What the
    /// local player has seen in their log stays, except that a wish that the partner accepted shows as new.
    /// </summary>
    private void OnWishChange(ClientPlayerData player, CoopSaveUpdate update) {
        var playerData = PlayerData.instance;
        if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker) || playerData == null) {
            return;
        }

        try {
            var changed = 0;
            var accepted = 0;
            var completed = 0;
            for (var i = 0; i < update.WishNames.Count && i < update.WishValues.Count; i++) {
                var name = update.WishNames[i];
                var value = update.WishValues[i];
                if (!IsNewerChange(_wishSequences, update, GetWishChangeKey(name, value))) {
                    continue;
                }

                if ((value & RumourEntry) != 0) {
                    var rumour = playerData.QuestRumourData.GetData(name);
                    if (PackRumour(rumour) != value) {
                        rumour.IsAccepted = (value & WishAccepted) != 0;
                        if (rumour.IsAccepted) {
                            rumour.HasBeenSeen = false;
                        }

                        playerData.QuestRumourData.SetData(name, rumour);
                        changed++;
                    }

                    _knownRumours[name] = PackRumour(rumour);
                    continue;
                }

                var wish = playerData.QuestCompletionData.GetData(name);
                var next = UnpackCompletion(value, wish.HasBeenSeen);
                if (_differentWishNames.Contains(name)) {
                    // The check left this wish completed in only one of the saves, so only whether it is accepted is
                    // shared, and the completion of one save isn't copied
                    next = wish;
                    next.IsAccepted = (value & WishAccepted) != 0;
                }

                // The known state is the local one, which may keep what the partner's doesn't share
                if (PackCompletion(next) == PackCompletion(wish)) {
                    _knownWishes[name] = PackCompletion(wish);
                    continue;
                }

                _knownWishes[name] = PackCompletion(next);
                if (next.IsAccepted && !wish.IsAccepted) {
                    next.HasBeenSeen = false;
                    _partnerAcceptedWishes.Add(name);
                    if (!next.IsCompleted) {
                        accepted++;
                    }
                }

                // Key dialogue of the partner that completed it makes the local player pay and get the reward
                if (next.IsCompleted && !wish.IsCompleted) {
                    _partnerCompletedWishes.Add(name);
                    completed++;
                }

                playerData.QuestCompletionData.SetData(name, next);
                changed++;
            }

            if (changed == 0) {
                return;
            }

            QuestManager.IncrementVersion();
            Logger.Info($"Added {changed} changes of the wish log from {player.Username}");

            if (accepted > 0 || completed > 0) {
                var what = accepted > 0 && completed > 0
                    ? $"accepted {CountWishes(accepted)} and completed {CountWishes(completed)}"
                    : accepted > 0
                        ? $"accepted {CountWishes(accepted)}"
                        : $"completed {CountWishes(completed)}";
                Chat($"{player.Username} {what}. Your wish log has the same now.");
            }
        } catch (Exception e) {
            LogWishError(e);
        }
    }

    /// <summary>
    /// The wishes and rumours of the local wish log that aren't in their first state, packed, for a check.
    /// </summary>
    private static List<(string Name, int Value)> GetWishEntries() {
        var entries = new List<(string Name, int Value)>();
        var playerData = PlayerData.instance;
        if (playerData == null) {
            return entries;
        }

        foreach (var pair in playerData.QuestCompletionData.Enumerate()) {
            var value = PackCompletion(pair.Value);
            if (value != 0) {
                entries.Add((pair.Key, value));
            }
        }

        foreach (var pair in playerData.QuestRumourData.Enumerate()) {
            var value = PackRumour(pair.Value);
            if (value != RumourEntry) {
                entries.Add((pair.Key, value));
            }
        }

        return entries;
    }

    /// <summary>
    /// Adds the wishes and rumours that the save of the partner accepted and the local save didn't to the local wish
    /// log, for a check, and finds the wishes that only one of the saves completed, which stay as they are. Both games
    /// compare what both saves sent, so they find the same wishes, and both wish logs agree afterwards apart from those.
    /// Wishes and rumours that changed live during the check are newer than both saves and stay as they are.
    /// </summary>
    /// <returns>How many wishes and rumours were added.</returns>
    private int AddWishes(IEnumerable<(string Name, int Value)> partnerEntries) {
        var playerData = PlayerData.instance;
        var partnerWishes = new Dictionary<string, int>(StringComparer.Ordinal);
        var added = 0;
        foreach (var (name, value) in partnerEntries) {
            if ((value & RumourEntry) == 0) {
                partnerWishes[name] = value;
                continue;
            }

            var key = GetWishChangeKey(name, value);
            var rumour = playerData.QuestRumourData.GetData(name);
            if ((value & WishAccepted) != 0 && !rumour.IsAccepted && !_wishSequences.ContainsKey(key)) {
                rumour.IsAccepted = true;
                rumour.HasBeenSeen = false;
                playerData.QuestRumourData.SetData(name, rumour);
                _mergedWishKeys.Add(key);
                added++;
            }
        }

        var localWishes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, value) in _sentWishEntries) {
            if ((value & RumourEntry) == 0) {
                localWishes[name] = value;
            }
        }

        var names = new HashSet<string>(partnerWishes.Keys, StringComparer.Ordinal);
        names.UnionWith(localWishes.Keys);

        _differentWishNames.Clear();
        foreach (var name in names) {
            if (_wishSequences.ContainsKey(name)) {
                continue;
            }

            var partnerValue = partnerWishes.TryGetValue(name, out var value) ? value : 0;
            var localValue = localWishes.TryGetValue(name, out var sent) ? sent : 0;
            var partnerDone = (partnerValue & (WishCompleted | WishEverCompleted)) != 0;
            var localDone = (localValue & (WishCompleted | WishEverCompleted)) != 0;
            if (partnerDone != localDone) {
                _differentWishNames.Add(name);
                continue;
            }

            var wish = playerData.QuestCompletionData.GetData(name);
            if (!localDone && (partnerValue & WishAccepted) != 0 && !wish.IsAccepted && !wish.IsCompleted) {
                wish.IsAccepted = true;
                wish.HasBeenSeen = false;
                playerData.QuestCompletionData.SetData(name, wish);
                _mergedWishKeys.Add(name);
                added++;
            }
        }

        _differentWishes = _differentWishNames.Count;
        if (added > 0) {
            QuestManager.IncrementVersion();
        }

        return added;
    }

    /// <summary>
    /// Packs the state of a wish that both players share: whether it is accepted, completed and was ever completed, and
    /// how often it was completed. Whether the player has seen it stays out.
    /// </summary>
    private static int PackCompletion(QuestCompletionData.Completion completion) {
        return (completion.IsAccepted ? WishAccepted : 0) |
               (completion.IsCompleted ? WishCompleted : 0) |
               (completion.WasEverCompleted ? WishEverCompleted : 0) |
               (Mathf.Clamp(completion.CompletedCount, 0, MaxWishCount) << WishCountShift);
    }

    /// <summary>
    /// Unpacks the state of a wish, with whether the local player has seen it.
    /// </summary>
    private static QuestCompletionData.Completion UnpackCompletion(int value, bool hasBeenSeen) {
        return new QuestCompletionData.Completion {
            HasBeenSeen = hasBeenSeen,
            IsAccepted = (value & WishAccepted) != 0,
            IsCompleted = (value & WishCompleted) != 0,
            WasEverCompleted = (value & WishEverCompleted) != 0,
            CompletedCount = (value >> WishCountShift) & MaxWishCount
        };
    }

    /// <summary>
    /// Packs the state of a rumour that both players share: whether it is accepted.
    /// </summary>
    private static int PackRumour(QuestRumourData.Data data) => RumourEntry | (data.IsAccepted ? WishAccepted : 0);

    /// <summary>
    /// "a wish" or the number of wishes, for messages.
    /// </summary>
    private static string CountWishes(int count) => count == 1 ? "a wish" : $"{count} wishes";

    /// <summary>
    /// Logs the first error of syncing the wish log.
    /// </summary>
    private void LogWishError(Exception e) {
        if (!_wishSyncFailed) {
            _wishSyncFailed = true;
            Logger.Error($"Could not sync the wish log of the two-player save:\n{e}");
        }
    }
}
