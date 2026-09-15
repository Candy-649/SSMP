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
/// check of the saves takes the flags that differ from the save that was played for longer, which is the one that went
/// on while the other player was away.
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
    /// Takes the local story flags as the ones that both games know, once a check made both saves agree.
    /// </summary>
    private void RememberStoryFlags() {
        var playerData = PlayerData.instance;
        if (playerData == null) {
            return;
        }

        var fields = GetStoryFields();
        _knownStoryValues = new int[fields.Length];
        for (var i = 0; i < fields.Length; i++) {
            _knownStoryValues[i] = ReadStoryValue(fields[i], playerData);
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
    /// Takes the story flags among the given names as known by both games, after the partner set them, so they don't go
    /// back to the partner.
    /// </summary>
    private void RememberStoryValues(List<string> names) {
        var playerData = PlayerData.instance;
        if (_knownStoryValues == null || playerData == null) {
            return;
        }

        var fields = GetStoryFields();
        foreach (var name in names) {
            if (StoryFieldIndices.TryGetValue(name, out var index) && index < _knownStoryValues.Length) {
                _knownStoryValues[index] = ReadStoryValue(fields[index], playerData);
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
    /// The play time of the local save for the current check, which stays the same for the whole check, so both games
    /// compare the same play times.
    /// </summary>
    private float GetCheckPlayTime() {
        _checkPlayTime ??= PlayerData.instance != null ? PlayerData.instance.playTime : 0f;
        return _checkPlayTime.Value;
    }

    /// <summary>
    /// Compares the story flags of the partner's save with the local ones, for a check. The flags that differ are taken
    /// from the save that was played for longer, or from the save with the larger key if both were played equally long,
    /// so both games choose the same save.
    /// </summary>
    /// <returns>How many story flags changed in the local save.</returns>
    private int AddStoryFlags(ClientPlayerData partner, List<CoopSaveUpdate> parts) {
        var playerData = PlayerData.instance;
        var fields = GetStoryFields();
        var partnerTime = parts.Count > 0 ? parts[0].PlayTime : 0f;
        var localTime = GetCheckPlayTime();
        _storyFlagsFromPartner = partnerTime > localTime ||
                                 (partnerTime.Equals(localTime) && string.CompareOrdinal(partner.SaveKey, LocalKey) > 0);

        _differentStoryFlags = 0;
        var changes = new CoopSaveUpdate();
        foreach (var part in parts) {
            for (var i = 0; i < part.FlagNames.Count && i < part.FlagValues.Count; i++) {
                if (!StoryFieldIndices.TryGetValue(part.FlagNames[i], out var index) ||
                    ReadStoryValue(fields[index], playerData) == part.FlagValues[i]) {
                    continue;
                }

                _differentStoryFlags++;
                if (_storyFlagsFromPartner) {
                    changes.FlagNames.Add(part.FlagNames[i]);
                    changes.FlagValues.Add(part.FlagValues[i]);
                }
            }
        }

        return changes.FlagNames.Count > 0 ? ApplyInteractionFlags(changes) : 0;
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
