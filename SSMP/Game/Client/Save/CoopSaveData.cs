using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The saves of the local player that are two-player saves, stored in the config folder of SSMP because the game drops
/// data it doesn't know from its own save files.
/// </summary>
internal class CoopSaveMarkers {
    /// <summary>
    /// The paired saves, by the folder of the game's save files and the slot of the save.
    /// </summary>
    [JsonProperty("slots")]
    public Dictionary<string, CoopSaveMarker> Slots { get; set; } = new();
}

/// <summary>
/// A save of the local player that is paired with a save of another player.
/// </summary>
internal class CoopSaveMarker {
    /// <summary>
    /// The save key of the other player.
    /// </summary>
    [JsonProperty("partnerKey")]
    public string PartnerKey { get; set; } = "";

    /// <summary>
    /// The username of the other player when the saves were last checked, for messages.
    /// </summary>
    [JsonProperty("partnerName")]
    public string PartnerName { get; set; } = "";

    /// <summary>
    /// When the saves were paired.
    /// </summary>
    [JsonProperty("pairedUtc")]
    public DateTime PairedUtc { get; set; }

    /// <summary>
    /// When both saves were last backed up and compared, or null if they never were.
    /// </summary>
    [JsonProperty("lastCheckUtc")]
    public DateTime? LastCheckUtc { get; set; }

    /// <summary>
    /// The play time of the local save when both players last played it together, or null if they never did. The next
    /// check compares how long each save was played since then.
    /// </summary>
    [JsonProperty("checkedPlayTime")]
    public float? CheckedPlayTime { get; set; }

    /// <summary>
    /// The scene of the boss fight that started in the save with both players, while the fight lasts, or null.
    /// </summary>
    [JsonProperty("bossScene")]
    public string? BossScene { get; set; }

    /// <summary>
    /// The door that the local player came into the scene of the boss fight through.
    /// </summary>
    [JsonProperty("bossGate")]
    public string? BossGate { get; set; }

    /// <summary>
    /// How many bosses the save had beaten when the boss fight started, to notice when it is won.
    /// </summary>
    [JsonProperty("bossDefeats")]
    public int BossDefeats { get; set; }
}

/// <summary>
/// The saved objects of the world that two-player saves share, made by tools/coop_world_items.py from the scenes of the
/// game.
/// </summary>
internal class CoopWorldItems {
    /// <summary>
    /// The IDs of saved booleans of the world, by the scene name in lower case.
    /// </summary>
    [JsonProperty("bools")]
    public Dictionary<string, List<string>> Bools { get; set; } = new();

    /// <summary>
    /// The booleans of the player data that saved booleans of the world set to true together with their own state, by
    /// the scene name in lower case and the ID of the saved boolean.
    /// </summary>
    [JsonProperty("playerData")]
    public Dictionary<string, Dictionary<string, List<string>>> PlayerData { get; set; } = new();
}

/// <summary>
/// The flags of the player data that two-player saves share as the story state of the world, made by
/// tools/coop_story_flags.py from the scenes and the code of the game.
/// </summary>
internal class CoopStoryFlags {
    /// <summary>
    /// The names of the boolean fields.
    /// </summary>
    [JsonProperty("Bools")]
    public List<string> Bools { get; set; } = [];

    /// <summary>
    /// The names of the integer fields.
    /// </summary>
    [JsonProperty("Ints")]
    public List<string> Ints { get; set; } = [];

    /// <summary>
    /// The names of the string fields, which don't sync yet.
    /// </summary>
    [JsonProperty("Strings")]
    public List<string> Strings { get; set; } = [];

    /// <summary>
    /// The names of the fields of other types, of which the enums sync as their numbers.
    /// </summary>
    [JsonProperty("Enums")]
    public List<string> Enums { get; set; } = [];
}

/// <summary>
/// The items that hold the story of the world, made by tools/coop_story_items.py from the data of the game. Picking
/// one up is not shared: every pickup in the world stays one copy per player, like all other pickups. The list only
/// says which items a character giving one in dialogue hands to both players, because the partner has no pickup of it
/// anywhere, and which of them a one-off story interaction takes from both players once it uses one up.
/// </summary>
internal class CoopStoryItems {
    /// <summary>
    /// The items of the story.
    /// </summary>
    [JsonProperty("items")]
    public List<CoopStoryItem> Items { get; set; } = [];
}

/// <summary>
/// One item that holds the story of the world.
/// </summary>
internal class CoopStoryItem {
    /// <summary>
    /// The name of the type that the item is, which tells the items of different kinds with the same name apart.
    /// </summary>
    [JsonProperty("type")]
    public string Type { get; set; } = "";

    /// <summary>
    /// The name of the item asset.
    /// </summary>
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// What the item is: a lock key, a key that only sets a flag, an item that a one-off story interaction uses up, a
    /// memento, a relic, or the fixed target of a wish.
    /// </summary>
    [JsonProperty("kind")]
    public string Kind { get; set; } = "";

    /// <summary>
    /// Whether a one-off story interaction that uses the item up takes it from both players, because the world shows
    /// the result to both of them. Keys that shops also sell stay with the player who used theirs.
    /// </summary>
    [JsonProperty("shareRemoval")]
    public bool ShareRemoval { get; set; }
}
