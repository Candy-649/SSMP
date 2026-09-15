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
}
