using System;
using System.Collections.Generic;
using System.Reflection;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The story state of a checked two-player save: the flags of the player data that tell where the story of the world
/// is, like where characters went and which ways the story opened, as tools/coop_story_flags.py sorts them. When one
/// of them changes in the game of one player, the save of the partner gets it at once, and the latest change wins. The
/// check of the saves takes the flags that differ from the save that was played longer since both players last played
/// together, which is the one that went on while the other player was away.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The embedded list of flags of the player data that hold the story state.
    /// </summary>
    private const string StoryFlagsFilePath = "SSMP.Resource.coop-story-flags.json";

    /// <summary>
    /// How often the story flags are compared with the known ones, in seconds.
    /// </summary>
    private const float StoryFlagInterval = 0.5f;

    /// <summary>
    /// How often, in seconds, the play time that both players played together is written to the pairing, so that a
    /// crash loses little of it.
    /// </summary>
    private const float CheckedPlayTimeSaveInterval = 60f;

    /// <summary>
    /// The fields of the player data that hold the story state, loaded when first needed.
    /// </summary>
    private static FieldInfo[]? _storyFields;

    /// <summary>
    /// The indices of the fields in <see cref="_storyFields"/> by name.
    /// </summary>
    private static readonly Dictionary<string, int> StoryFieldIndices = new(StringComparer.Ordinal);

    /// <summary>
    /// The values of the story fields as both games last knew them, or null before a check made both saves agree.
    /// </summary>
    private int[]? _knownStoryValues;

    /// <summary>
    /// When the story flags are compared next.
    /// </summary>
    private float _nextStoryFlagTime;

    /// <summary>
    /// The play time of the local save that the current check compares, or null before the check needs it.
    /// </summary>
    private float? _checkPlayTime;

    /// <summary>
    /// Whether the play time that the current check compares counts from when both players last played together,
    /// rather than from the start of the save.
    /// </summary>
    private bool _checkPlayTimeSinceTogether;

    /// <summary>
    /// The values of the story fields that both games agree on in the current check: the ones that the local save
    /// sent, with the ones that the check and the partner changed since. Null before the local save sent them.
    /// </summary>
    private int[]? _agreedStoryValues;

    /// <summary>
    /// When the play time that both players played together is written to the pairing next.
    /// </summary>
    private float _nextCheckedPlayTimeSave;

    /// <summary>
    /// How many story flags the current check found different in both saves.
    /// </summary>
    private int _differentStoryFlags;

    /// <summary>
    /// Whether the current check took the story flags that differ from the save of the partner.
    /// </summary>
    private bool _storyFlagsFromPartner;

    /// <summary>
    /// Whether syncing the story flags threw, which is only logged once.
    /// </summary>
    private bool _storyFlagsFailed;

    /// <summary>
    /// Forgets the story flags that both games know, for a new session.
    /// </summary>
    private void ResetStoryFlags() {
        _knownStoryValues = null;
        _nextStoryFlagTime = 0f;
    }

    /// <summary>
    /// Takes the story flags that the check made both saves agree on as the ones that both games know. A flag that the
    /// local player changed after the local save sent its story flags differs from them, so it goes to the partner.
    /// </summary>
    private void RememberStoryFlags() {
        var playerData = PlayerData.instance;
        if (playerData == null) {
            return;
        }

        var fields = GetStoryFields();
        _knownStoryValues = new int[fields.Length];
        for (var i = 0; i < fields.Length; i++) {
            _knownStoryValues[i] = _agreedStoryValues != null && i < _agreedStoryValues.Length
                ? _agreedStoryValues[i]
                : ReadStoryValue(fields[i], playerData);
        }
    }

    /// <summary>
    /// Remembers the play time of the local save while both players play it together, from which the next check counts
    /// how long each save was played.
    /// </summary>
    private void UpdateCheckedPlayTime(CoopSaveMarker marker) {
        if (PlayerData.instance == null) {
            return;
        }

        marker.CheckedPlayTime = PlayerData.instance.playTime;
        if (Time.unscaledTime >= _nextCheckedPlayTimeSave) {
            _nextCheckedPlayTimeSave = Time.unscaledTime + CheckedPlayTimeSaveInterval;
            SaveMarkers();
        }
    }

    /// <summary>
    /// Sends the story flags that changed in the local save to the partner.
    /// </summary>
    private void UpdateStoryFlags(ClientPlayerData partner) {
        var playerData = PlayerData.instance;
        if (playerData == null || _knownStoryValues == null || Time.unscaledTime < _nextStoryFlagTime) {
            return;
        }

        _nextStoryFlagTime = Time.unscaledTime + StoryFlagInterval;

        try {
            var fields = GetStoryFields();
            CoopSaveUpdate? update = null;
            for (var i = 0; i < fields.Length && i < _knownStoryValues.Length; i++) {
                var value = ReadStoryValue(fields[i], playerData);
                if (value == _knownStoryValues[i]) {
                    continue;
                }

                _knownStoryValues[i] = value;
                update ??= new CoopSaveUpdate { TargetId = partner.Id, Kind = CoopSaveUpdateKind.WorldChange };
                update.FlagNames.Add(fields[i].Name);
                update.FlagValues.Add(value);
            }

            if (update != null) {
                Send(update);
                Logger.Info($"Sent {update.FlagNames.Count} story flags to {partner.Username}");
            }
        } catch (Exception e) {
            if (!_storyFlagsFailed) {
                _storyFlagsFailed = true;
                Logger.Error($"Could not send the story flags of the two-player save:\n{e}");
            }
        }
    }

    /// <summary>
    /// Takes the story flags among the given names as known by both games, after the partner set them or while they go
    /// to the partner another way, so that the story flags don't send them.
    /// </summary>
    private void RememberStoryValues(List<string> names) {
        var playerData = PlayerData.instance;
        if ((_knownStoryValues == null && _agreedStoryValues == null) || playerData == null) {
            return;
        }

        var fields = GetStoryFields();
        foreach (var name in names) {
            if (!StoryFieldIndices.TryGetValue(name, out var index)) {
                continue;
            }

            var value = ReadStoryValue(fields[index], playerData);
            if (_knownStoryValues != null && index < _knownStoryValues.Length) {
                _knownStoryValues[index] = value;
            }

            if (_agreedStoryValues != null && index < _agreedStoryValues.Length) {
                _agreedStoryValues[index] = value;
            }
        }
    }

    /// <summary>
    /// The story flags of the local save with their values, for a check.
    /// </summary>
    private static List<(string Name, int Value)> GetStoryEntries() {
        var entries = new List<(string Name, int Value)>();
        var playerData = PlayerData.instance;
        if (playerData == null) {
            return entries;
        }

        foreach (var field in GetStoryFields()) {
            entries.Add((field.Name, ReadStoryValue(field, playerData)));
        }

        return entries;
    }

    /// <summary>
    /// How long the local save was played for the current check: since both players last played it together, or since
    /// its start if they never did. It stays the same for the whole check, so that both games compare the same times.
    /// </summary>
    private float GetCheckPlayTime() {
        if (_checkPlayTime is { } checkPlayTime) {
            return checkPlayTime;
        }

        var playTime = PlayerData.instance != null ? PlayerData.instance.playTime : 0f;
        var checkedPlayTime = GetMarker(_sessionSlot)?.CheckedPlayTime;
        _checkPlayTimeSinceTogether = checkedPlayTime != null;
        _checkPlayTime = checkedPlayTime is { } together ? Mathf.Max(0f, playTime - together) : playTime;
        return _checkPlayTime.Value;
    }

    /// <summary>
    /// Compares the story flags that the partner's save sent with the ones that the local save sent, for a check, so that
    /// both games compare the same values. The flags that differ are taken from the save that was played longer since
    /// both players last played together, or from the save with the larger key if both were played equally long, so
    /// both games choose the same save. A boolean that a saved object of the world sets stays set if either save has
    /// that object, since the check gives it to both saves. Flags that changed live during the check are newer than
    /// both saves and stay as they are.
    /// </summary>
    /// <returns>How many story flags changed in the local save.</returns>
    private int AddStoryFlags(ClientPlayerData partner, List<CoopSaveUpdate> parts) {
        var playerData = PlayerData.instance;
        var fields = GetStoryFields();
        var partnerTime = parts.Count > 0 ? parts[0].PlayTime : 0f;
        var localTime = GetCheckPlayTime();
        _storyFlagsFromPartner = partnerTime > localTime || (partnerTime.Equals(localTime) && PartnerKeyWins());
        var worldItemFlags = GetWorldItemFlags(parts);

        _differentStoryFlags = 0;
        var changes = new CoopSaveUpdate();
        foreach (var part in parts) {
            for (var i = 0; i < part.FlagNames.Count && i < part.FlagValues.Count; i++) {
                var name = part.FlagNames[i];
                if (!StoryFieldIndices.TryGetValue(name, out var index) || _flagSequences.ContainsKey(name)) {
                    continue;
                }

                var partnerValue = part.FlagValues[i];
                var hasSent = _agreedStoryValues != null && index < _agreedStoryValues.Length;
                var localValue = hasSent ? _agreedStoryValues![index] : ReadStoryValue(fields[index], playerData);
                if (localValue == partnerValue) {
                    continue;
                }

                _differentStoryFlags++;
                var value = _storyFlagsFromPartner ? partnerValue : localValue;
                if (fields[index].FieldType == typeof(bool) && worldItemFlags.Contains(name)) {
                    value = 1;
                }

                if (hasSent) {
                    _agreedStoryValues![index] = value;
                }

                // A flag that keeps the value of the local save keeps what the local player changed since, which goes to
                // the partner after the check
                if (value != localValue && ReadStoryValue(fields[index], playerData) != value) {
                    changes.FlagNames.Add(name);
                    changes.FlagValues.Add(value);
                }
            }
        }

        return changes.FlagNames.Count > 0 ? ApplyInteractionFlags(changes) : 0;
    }

    /// <summary>
    /// The booleans of the player data that the saved objects of the world in either save set, for a check, which gives
    /// those objects to both saves.
    /// </summary>
    private HashSet<string> GetWorldItemFlags(List<CoopSaveUpdate> parts) {
        var flags = new HashSet<string>(StringComparer.Ordinal);
        GetWorldBools();
        foreach (var (scene, id) in _sentWorldItems) {
            if (_worldPlayerData!.TryGetValue(GetItemKey(scene, id), out var names)) {
                flags.UnionWith(names);
            }
        }

        foreach (var part in parts) {
            for (var i = 0; i < part.ItemIds.Count && i < part.ItemScenes.Count; i++) {
                if (_worldPlayerData!.TryGetValue(GetItemKey(part.ItemScenes[i], part.ItemIds[i]), out var names)) {
                    flags.UnionWith(names);
                }
            }
        }

        return flags;
    }

    /// <summary>
    /// Gets the fields of the player data that hold the story state, loading their list the first time.
    /// </summary>
    private static FieldInfo[] GetStoryFields() {
        if (_storyFields != null) {
            return _storyFields;
        }

        var fields = new List<FieldInfo>();
        var storyFlags = FileUtil.LoadObjectFromEmbeddedJson<CoopStoryFlags>(StoryFlagsFilePath);
        if (storyFlags == null) {
            Logger.Warn("Could not load the story flags for two-player saves");
        } else {
            AddStoryFields(fields, storyFlags.Bools, type => type == typeof(bool));
            AddStoryFields(fields, storyFlags.Ints, type => type == typeof(int));
            AddStoryFields(fields, storyFlags.Enums, type => type.IsEnum);
        }

        _storyFields = fields.ToArray();
        StoryFieldIndices.Clear();
        for (var i = 0; i < _storyFields.Length; i++) {
            StoryFieldIndices[_storyFields[i].Name] = i;
        }

        return _storyFields;
    }

    /// <summary>
    /// Adds the fields of the player data with the given names and a fitting type to the story fields.
    /// </summary>
    private static void AddStoryFields(List<FieldInfo> fields, List<string> names, Func<Type, bool> isType) {
        foreach (var name in names) {
            var field = GetPlayerDataField(name);
            if (field == null || !isType(field.FieldType) || BossRoomCoop.IsHeroStateName(name)) {
                Logger.Warn($"The story flag '{name}' of two-player saves isn't a field of the player data that can sync");
                continue;
            }

            fields.Add(field);
        }
    }

    /// <summary>
    /// Reads a story field as a number: 1 or 0 for booleans, and the number of an enum value.
    /// </summary>
    private static int ReadStoryValue(FieldInfo field, PlayerData playerData) {
        return field.GetValue(playerData) switch {
            bool flag => flag ? 1 : 0,
            int number => number,
            Enum value => Convert.ToInt32(value),
            _ => 0
        };
    }
}
