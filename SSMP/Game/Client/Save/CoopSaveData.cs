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
/// A save of the local player that is paired with the saves of other players: one other player for a two-player save,
/// more for a save that more players share.
/// </summary>
internal class CoopSaveMarker {
    /// <summary>
    /// The save key of the first other player. Pairings from before saves could have more than two players name their
    /// only other player here, and <see cref="Members"/> takes them over as it loads; it is still written for the first
    /// member, so that an older build reads a two-player save the way it always did.
    /// </summary>
    [JsonProperty("partnerKey")]
    public string PartnerKey { get; set; } = "";

    /// <summary>
    /// The username of the first other player, kept like <see cref="PartnerKey"/>.
    /// </summary>
    [JsonProperty("partnerName")]
    public string PartnerName { get; set; } = "";

    /// <summary>
    /// The other players that share the save, each with their save key and their username when the saves were last
    /// checked, for messages. Empty in a pairing from before saves could have more than two players, which
    /// <see cref="TakeOverPartner"/> fills from <see cref="PartnerKey"/>.
    /// </summary>
    [JsonProperty("members")]
    public List<CoopSaveMember> Members { get; set; } = [];

    /// <summary>
    /// Fills <see cref="Members"/> of a pairing from before saves could have more than two players from its only other
    /// player, and writes the first member back to <see cref="PartnerKey"/> and <see cref="PartnerName"/>.
    /// </summary>
    public void TakeOverPartner() {
        if (Members.Count == 0 && PartnerKey.Length > 0) {
            Members.Add(new CoopSaveMember { Key = PartnerKey, Name = PartnerName });
        }

        if (Members.Count > 0) {
            PartnerKey = Members[0].Key;
            PartnerName = Members[0].Name;
        }
    }

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

    /// <summary>
    /// Dialogue that accepted wishes, with what it gave and set, from the moment it ended until a check finds both saves
    /// with those wishes (see CoopSave.GiveBackAcceptTalks). It is kept here rather than in the game's save, which a game
    /// that closes before saving loses along with the accept itself.
    /// </summary>
    [JsonProperty("acceptTalks")]
    public List<CoopAcceptTalk> AcceptTalks { get; set; } = [];

    /// <summary>
    /// Arrivals that the partner had first and that the local player is still to see on their own arrival, by the flag
    /// of the player data that the game marks each with (see CoopSave.Arrivals). The flag came from the partner, so the
    /// save itself can't tell that the local player hasn't seen it.
    /// </summary>
    [JsonProperty("arrivalsToSee")]
    public List<string> ArrivalsToSee { get; set; } = [];

    /// <summary>
    /// Wishes that dialogue begins without asking, whose step the local player read to while the partner hadn't, and
    /// which the dialogue went on from without the wish (see CoopSave.WishRead). The wish is taken in both saves the
    /// moment the partner reads to the same step. It is kept here so that a game that closes in between doesn't forget
    /// that its player read it, which a dialogue that is heard only once could never tell it again.
    /// </summary>
    [JsonProperty("wishesRead")]
    public List<string> WishesRead { get; set; } = [];
}

/// <summary>
/// Another player that shares a save of the local player.
/// </summary>
internal class CoopSaveMember {
    /// <summary>
    /// The save key of the player, which their game gets from their authentication key.
    /// </summary>
    [JsonProperty("key")]
    public string Key { get; set; } = "";

    /// <summary>
    /// The username of the player when the saves were last checked, for messages, and for finding them when they play
    /// on another computer, which gives them another save key.
    /// </summary>
    [JsonProperty("name")]
    public string Name { get; set; } = "";
}

/// <summary>
/// Dialogue that accepted wishes and did nothing else, as it went to the partner or came from them
/// (CoopSaveUpdateKind.WishTurnIn): the wishes, and what it took from the player, gave them and set.
/// </summary>
internal class CoopAcceptTalk {
    /// <summary>
    /// The wishes and rumours that the dialogue accepted.
    /// </summary>
    [JsonProperty("wishNames")]
    public List<string> WishNames { get; set; } = [];

    /// <summary>
    /// The packed state of each of them right after the dialogue accepted it.
    /// </summary>
    [JsonProperty("wishValues")]
    public List<int> WishValues { get; set; } = [];

    /// <summary>
    /// What the dialogue took and gave, each with the index of the change it belongs to before it.
    /// </summary>
    [JsonProperty("itemIds")]
    public List<string> ItemIds { get; set; } = [];

    /// <summary>
    /// How many of each of <see cref="ItemIds"/>.
    /// </summary>
    [JsonProperty("amounts")]
    public List<int> Amounts { get; set; } = [];

    /// <summary>
    /// The flags of the player data that the dialogue set.
    /// </summary>
    [JsonProperty("flagNames")]
    public List<string> FlagNames { get; set; } = [];

    /// <summary>
    /// The values that the dialogue set them to.
    /// </summary>
    [JsonProperty("flagValues")]
    public List<int> FlagValues { get; set; } = [];

    /// <summary>
    /// The scene of the character, for the log.
    /// </summary>
    [JsonProperty("scene")]
    public string Scene { get; set; } = "";

    /// <summary>
    /// The path of the character in its scene, for the log.
    /// </summary>
    [JsonProperty("path")]
    public string Path { get; set; } = "";

    /// <summary>
    /// When the dialogue was kept.
    /// </summary>
    [JsonProperty("utc")]
    public DateTime Utc { get; set; }
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

    /// <summary>
    /// The names of the boolean fields among <see cref="Bools"/> that record something done for good, like the win of
    /// an arena, which a check keeps if either save has them.
    /// </summary>
    [JsonProperty("Records")]
    public List<string> Records { get; set; } = [];
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
