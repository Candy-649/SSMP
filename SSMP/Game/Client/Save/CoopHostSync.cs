using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SSMP.Game.Client.Save;

/// <summary>
/// What a co-op save takes from the host as a player joins, from the embedded coop-host-sync.json that
/// tools/coop_host_sync.py writes.
/// </summary>
internal sealed class CoopHostSyncData {
    /// <summary>
    /// The fields of the player data by how they merge: "host" takes the host's value, "or" either player's true, "max"
    /// the larger number and "bits" the flags of both. Every other field stays the joining player's.
    /// </summary>
    [JsonProperty("fields")]
    public Dictionary<string, List<string>> Fields { get; set; } = new();

    /// <summary>
    /// The saved booleans of the world by scene in lower case and ID: "h" for the host's state, "a" for set in either
    /// save. Every other saved object stays the joining player's.
    /// </summary>
    [JsonProperty("bools")]
    public Dictionary<string, Dictionary<string, string>> Bools { get; set; } = new();

    /// <summary>
    /// The saved numbers of the world, like <see cref="Bools"/>; "a" takes the larger number.
    /// </summary>
    [JsonProperty("ints")]
    public Dictionary<string, Dictionary<string, string>> Ints { get; set; } = new();

    /// <summary>
    /// The items that each pickup of the world gives, by scene in lower case and ID.
    /// </summary>
    [JsonProperty("sources")]
    public Dictionary<string, Dictionary<string, List<string>>> Sources { get; set; } = new();

    /// <summary>
    /// The items by their name.
    /// </summary>
    [JsonProperty("items")]
    public Dictionary<string, CoopHostSyncItem> Items { get; set; } = new();

    /// <summary>
    /// The wishes by name: "host" for the host's state, "own" for each player's. A wish that isn't listed follows the
    /// host.
    /// </summary>
    [JsonProperty("quests")]
    public Dictionary<string, string> Quests { get; set; } = new();

    /// <summary>
    /// What each wish gives once it is completed.
    /// </summary>
    [JsonProperty("rewards")]
    public Dictionary<string, List<CoopHostSyncReward>> Rewards { get; set; } = new();

    /// <summary>
    /// The locks that take a key, by the key.
    /// </summary>
    [JsonProperty("locks")]
    public Dictionary<string, List<CoopHostSyncLock>> Locks { get; set; } = new();

    /// <summary>
    /// Each upgraded crest with the version before it, whose slots it takes over.
    /// </summary>
    [JsonProperty("crestVersions")]
    public Dictionary<string, string> CrestVersions { get; set; } = new();
}

/// <summary>
/// An item: where it is kept and how it merges.
/// </summary>
internal sealed class CoopHostSyncItem {
    /// <summary>
    /// "col" for the collectables, "tool", "crest", "relic", or "pd" for an item that sets fields of the player data.
    /// </summary>
    [JsonProperty("in")]
    public string In { get; set; } = "";

    /// <summary>
    /// HOST, ONLY_ADD, PERSONAL, SESSION or UNSURE.
    /// </summary>
    [JsonProperty("class")]
    public string Class { get; set; } = "";

    /// <summary>
    /// What kind of item it is, for the log.
    /// </summary>
    [JsonProperty("kind")]
    public string Kind { get; set; } = "";

    /// <summary>
    /// Whether a player has more than one of it, so that each source gives one more. Every other item is unique.
    /// </summary>
    [JsonProperty("counted")]
    public bool Counted { get; set; }

    /// <summary>
    /// Whether it is a story item that the world uses up.
    /// </summary>
    [JsonProperty("story")]
    public bool Story { get; set; }

    /// <summary>
    /// Whether it is a mask shard.
    /// </summary>
    [JsonProperty("shards")]
    public bool Shards { get; set; }

    /// <summary>
    /// Whether it is a spool piece.
    /// </summary>
    [JsonProperty("spools")]
    public bool Spools { get; set; }

    /// <summary>
    /// The boolean of the player data that it sets.
    /// </summary>
    [JsonProperty("bool")]
    public string? Bool { get; set; }

    /// <summary>
    /// The number of the player data that it adds <see cref="N"/> to.
    /// </summary>
    [JsonProperty("add")]
    public string? Add { get; set; }

    /// <summary>
    /// The number of the player data that it raises to at least <see cref="N"/>.
    /// </summary>
    [JsonProperty("atLeast")]
    public string? AtLeast { get; set; }

    [JsonProperty("n")]
    public int N { get; set; } = 1;

    /// <summary>
    /// The wishes that it is for, when a boss drops it once.
    /// </summary>
    [JsonProperty("onceFor")]
    public List<string>? OnceFor { get; set; }
}

/// <summary>
/// The reward of a wish.
/// </summary>
internal sealed class CoopHostSyncReward {
    [JsonProperty("item")]
    public string Item { get; set; } = "";

    [JsonProperty("count")]
    public int Count { get; set; } = 1;

    /// <summary>
    /// How many it gives in the last act, if that is more than 0.
    /// </summary>
    [JsonProperty("act3")]
    public int Act3 { get; set; }
}

/// <summary>
/// A lock that takes a key: a boolean of the player data, or a saved boolean of the world.
/// </summary>
internal sealed class CoopHostSyncLock {
    [JsonProperty("bool")]
    public string? Bool { get; set; }

    [JsonProperty("scene")]
    public string? Scene { get; set; }

    [JsonProperty("id")]
    public string? Id { get; set; }
}

/// <summary>
/// What merging the host's save into a joining player's save changed, for the log and the player.
/// </summary>
internal sealed class CoopHostSyncReport {
    /// <summary>
    /// <see cref="Refusal"/> for a save in the mode in which a death ends it (Steel Soul) whose host's save is not.
    /// </summary>
    public const string SteelSoulWithNormalHost = "steel soul save, normal host";

    /// <summary>
    /// <see cref="Refusal"/> where the save or the host's is in that mode and a death already ended it.
    /// </summary>
    public const string SteelSoulEnded = "steel soul run ended";

    /// <summary>
    /// Why the saves can't be merged, or null.
    /// </summary>
    public string? Refusal { get; set; }

    public int HostFields { get; set; }

    public int AddedFields { get; set; }

    public int WorldObjects { get; set; }

    public int ClosedAgain { get; set; }

    public int Pickups { get; set; }

    public int Wishes { get; set; }

    public int Rewards { get; set; }

    public int Items { get; set; }

    public int Shards { get; set; }

    public int Spools { get; set; }

    public int Keys { get; set; }

    /// <summary>
    /// The beaten bosses of the joining player's save that the host's hasn't beaten, by their records. They are fought
    /// again in the merged save.
    /// </summary>
    public List<string> AheadBosses { get; } = [];

    /// <summary>
    /// Whether the joining player's save is in a later act than the host's.
    /// </summary>
    public bool AheadAct { get; set; }

    /// <summary>
    /// Flags of arrivals that the host had and the joining player hasn't, which they still see as they arrive.
    /// </summary>
    public List<string> ArrivalsToSee { get; } = [];

    /// <summary>
    /// What changed, for the log.
    /// </summary>
    public List<string> Lines { get; } = [];

    public override string ToString() {
        return $"{HostFields} fields from the host, {AddedFields} added, {WorldObjects} saved objects from the host " +
               $"({ClosedAgain} back to the host's), {Pickups} pickups the host took, {Wishes} wishes, {Rewards} " +
               $"wish rewards, {Items} items, {Shards} mask shards, {Spools} spool pieces, {Keys} keys";
    }
}

/// <summary>
/// Aligns a joining player's save with the host's save as they join a co-op save (the user's rules of 2026-10-11):
/// the host's world, story, beaten bosses and wishes; the important things the host found that the joining player
/// hasn't, without taking any of theirs; and everything else the joining player's own. It works on the JSON of the
/// game's SaveGameData ({"playerData": ..., "sceneData": ...}) and needs nothing of the game, so that it can be run on
/// saves outside the game too.
/// </summary>
internal static class CoopHostSync {
    /// <summary>
    /// The masks that a new save starts with, and the shards that make one more (HeroController.AddToMaxHealth counts
    /// shards as (maxHealthBase - 5) * 4 of 20).
    /// </summary>
    private const int BaseMasks = 5;

    private const int ShardsPerMask = 4;

    private const int MaxShards = 20;

    /// <summary>
    /// The silk that a new save starts with, and the pieces that make one more spool (HeroController.AddToMaxSilk
    /// counts pieces as (silkMax - 9) * 2 of 18).
    /// </summary>
    private const int BaseSilk = 9;

    private const int PiecesPerSpool = 2;

    private const int MaxPieces = 18;

    /// <summary>
    /// The version of the game that the merged save is marked with when the caller doesn't give the version of the game
    /// that runs: the version of the game this was written against (GameManager.PreparePlayerDataForSave).
    /// </summary>
    public const string GameVersion = "1.0.30000";

    public const int RevisionBreak = 28104;

    /// <summary>
    /// The crest of the curse, which the hero wears from the curse until it is cured, and the crest the cure gives.
    /// </summary>
    private const string CursedCrest = "Cursed";

    private const string CuredCrest = "Witch";

    /// <summary>
    /// The crest that every save starts with.
    /// </summary>
    private const string FirstCrest = "Hunter";

    /// <summary>
    /// The kind of the place where a save wakes that is a bench (PlayerData.respawnType): 0 is a marker of a room,
    /// which is where the game has a save wake at the start of the last act (GameManager.StartAct3).
    /// </summary>
    private const int BenchRespawn = 1;

    /// <summary>
    /// Flags of arrivals that each player sees for themselves (CoopSave.Arrivals): one the host had first is still
    /// the joining player's to see.
    /// </summary>
    private static readonly string[] ArrivalFlags = ["crashedIntoGreymoor"];

    /// <summary>
    /// Merges the host's save into a joining player's save.
    /// </summary>
    /// <param name="host">The host's save.</param>
    /// <param name="client">The joining player's save, or a new save for an empty slot. It isn't changed.</param>
    /// <param name="data">What merges how.</param>
    /// <param name="report">What changed, and why the saves can't be merged if they can't.</param>
    /// <param name="version">The version that the game which runs marks its saves with, so that loading the merged
    /// save runs no upgrade for older saves over the host's data again. The joining player's save is brought up to
    /// that version before it is merged.</param>
    /// <param name="revisionBreak">The revision that the game which runs marks its saves with.</param>
    /// <returns>The merged save, or null if the saves can't be merged.</returns>
    public static JObject? Merge(
        JObject host,
        JObject client,
        CoopHostSyncData data,
        out CoopHostSyncReport report,
        string? version = null,
        int? revisionBreak = null
    ) {
        report = new CoopHostSyncReport();
        var merge = new Merger(host, client, data, report);
        return merge.Run(version ?? GameVersion, revisionBreak ?? RevisionBreak);
    }

    /// <summary>
    /// The beaten bosses and the act of a joining player's save that are ahead of the host's, which merging undoes for
    /// them, without merging.
    /// </summary>
    public static CoopHostSyncReport CompareProgress(JObject host, JObject client) {
        var report = new CoopHostSyncReport();
        var hostData = host["playerData"] as JObject ?? new JObject();
        var clientData = client["playerData"] as JObject ?? new JObject();
        Merger.FindAhead(hostData, clientData, report);
        return report;
    }

    /// <summary>
    /// Whether a boolean of the player data records a beaten boss, as CoopSave.IsDefeatRecord decides.
    /// </summary>
    public static bool IsDefeatRecord(string name) {
        return name.StartsWith("defeated", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("Defeated", StringComparison.Ordinal);
    }

    private sealed class Merger {
        private readonly CoopHostSyncData _data;
        private readonly CoopHostSyncReport _report;

        /// <summary>
        /// The player data and scene data of the host, of the joining player as they were, and of the merged save.
        /// </summary>
        private readonly JObject _host;
        private readonly JObject _client;
        private readonly JObject _merged;
        private readonly JObject _hostScenes;
        private readonly JObject _clientScenes;
        private readonly JObject _mergedScenes;

        /// <summary>
        /// The mask shards and spool pieces that the joining player gets.
        /// </summary>
        private int _shards;
        private int _pieces;

        public Merger(JObject host, JObject client, CoopHostSyncData data, CoopHostSyncReport report) {
            _data = data;
            _report = report;
            _host = host["playerData"] as JObject ?? new JObject();
            _hostScenes = host["sceneData"] as JObject ?? new JObject();
            var merged = (JObject) client.DeepClone();
            _merged = merged["playerData"] as JObject ?? (JObject) (merged["playerData"] = new JObject());
            _mergedScenes = merged["sceneData"] as JObject ?? (JObject) (merged["sceneData"] = new JObject());
            _client = (JObject) _merged.DeepClone();
            _clientScenes = (JObject) _mergedScenes.DeepClone();
        }

        public JObject? Run(string version, int revisionBreak) {
            // PlayerData.permadeathMode: 0 normal, 1 a death ends the save (Steel Soul), 2 a death ended it. A normal
            // save may follow the world of a Steel Soul host and stays normal itself (the mode is never merged); a
            // Steel Soul save follows only a Steel Soul world, and a run that a death ended follows none
            var hostMode = Int(_host, "permadeathMode");
            var clientMode = Int(_client, "permadeathMode");
            if (hostMode == 2 || clientMode == 2) {
                _report.Refusal = CoopHostSyncReport.SteelSoulEnded;
                return null;
            }

            if (hostMode != clientMode && !(clientMode == 0 && hostMode == 1)) {
                _report.Refusal = CoopHostSyncReport.SteelSoulWithNormalHost;
                return null;
            }

            FindAhead(_host, _client, _report);

            MergeFields();
            LeaveBench();
            var takenByHost = MergeWorld();
            MergeQuests();
            MergeJournal();
            MergeCollectables();
            MergeTools();
            MergeCrests();
            GivePickups(takenByHost);
            GiveRewards();
            GiveOnceDrops();
            MergeKeys();
            UseUpStoryItems();
            MergeSpecials();
            SetCounts();
            FixCrest();
            NoteArrivals();

            _merged["version"] = version;
            _merged["RevisionBreak"] = revisionBreak;
            return (JObject) _merged.Parent!.Parent!;
        }

        #region Fields

        private void MergeFields() {
            foreach (var name in List("host")) {
                if (_host[name] is not { } value || JToken.DeepEquals(value, _merged[name])) {
                    continue;
                }

                _merged[name] = value.DeepClone();
                _report.HostFields++;
            }

            foreach (var name in List("or")) {
                if (Bool(_host, name) && !Bool(_merged, name)) {
                    _merged[name] = true;
                    _report.AddedFields++;
                }
            }

            foreach (var name in List("max")) {
                if (_host[name] != null && Int(_host, name) > Int(_merged, name)) {
                    _merged[name] = Int(_host, name);
                    _report.AddedFields++;
                }
            }

            foreach (var name in List("bits")) {
                if (_host[name] == null) {
                    continue;
                }

                var bits = ULong(_host[name]) | ULong(_merged[name]);
                if (bits != ULong(_merged[name])) {
                    _merged[name] = new JValue(bits);
                    _report.AddedFields++;
                }
            }
        }

        private IEnumerable<string> List(string merge) {
            return _data.Fields.TryGetValue(merge, out var names) ? names : [];
        }

        /// <summary>
        /// Gets the joining player up from the bench their save was written on, when they wake where the host's save
        /// wakes and that is no bench. The merge takes the place where the host's save wakes (respawnScene,
        /// respawnMarkerName, respawnType), and only getting up from a bench clears atBench, so a player who saved on a
        /// bench and woke where the host's save did at the start of the last act was taken by the game to sit for good:
        /// the needolin did nothing, and nothing that waits for a player free to move went ahead (USER 10-31, trying
        /// the dreams: "/1失败了", "而且弹琴键点不了").
        /// </summary>
        private void LeaveBench() {
            if (Int(_merged, "respawnType") == BenchRespawn || !Bool(_merged, "atBench")) {
                return;
            }

            _merged["atBench"] = false;
            _report.HostFields++;
        }

        /// <summary>
        /// The beaten bosses and the act that a joining player's save is ahead of the host's in.
        /// </summary>
        public static void FindAhead(JObject host, JObject client, CoopHostSyncReport report) {
            foreach (var property in client.Properties()) {
                if (property.Value.Type == JTokenType.Boolean && IsDefeatRecord(property.Name) &&
                    (bool) property.Value && !Bool(host, property.Name)) {
                    report.AheadBosses.Add(property.Name);
                }
            }

            report.AheadAct = Act(client) > Act(host);
        }

        private static int Act(JObject data) {
            return Bool(data, "blackThreadWorld") ? 3 : Bool(data, "act2Started") ? 2 : 1;
        }

        #endregion

        #region World

        /// <summary>
        /// Takes the host's state of the saved objects of the world that follow the host, sets the pickups that either
        /// player took, and keeps every other saved object of the joining player.
        /// </summary>
        /// <returns>The pickups that the host took and the joining player hadn't, by scene in lower case and ID.</returns>
        private List<(string Scene, string Id)> MergeWorld() {
            var taken = new List<(string Scene, string Id)>();
            MergeObjects("persistentBools", _data.Bools, true, taken);
            MergeObjects("persistentInts", _data.Ints, false, taken);
            return taken;
        }

        private void MergeObjects(
            string listName,
            Dictionary<string, Dictionary<string, string>> classes,
            bool bools,
            List<(string Scene, string Id)> taken
        ) {
            var hostEntries = Entries(_hostScenes, listName);
            var mergedList = SerializedList(_mergedScenes, listName);
            var mergedEntries = new Dictionary<(string, string), JObject>();
            foreach (var entry in mergedList.OfType<JObject>()) {
                mergedEntries[Key(entry)] = entry;
            }

            // The host's objects first, then the joining player's that the host has none of
            foreach (var pair in hostEntries) {
                var (scene, id) = pair.Key;
                var mergeClass = ClassOf(classes, scene, id);
                if (mergeClass == null) {
                    continue;
                }

                mergedEntries.TryGetValue(pair.Key, out var mine);
                if (mergeClass == "h") {
                    if (mine == null) {
                        mergedList.Add(pair.Value.DeepClone());
                        _report.WorldObjects++;
                    } else if (!JToken.DeepEquals(mine["Value"], pair.Value["Value"])) {
                        mine["Value"] = pair.Value["Value"]!.DeepClone();
                        mine["Mutator"] = pair.Value["Mutator"]?.DeepClone() ?? 0;
                        _report.WorldObjects++;
                    }

                    continue;
                }

                // Set in either save: the pickup is gone for both, and a number is the larger one
                if (bools) {
                    if (!Bool(pair.Value, "Value") || (mine != null && Bool(mine, "Value"))) {
                        continue;
                    }

                    if (mine == null) {
                        mergedList.Add(pair.Value.DeepClone());
                    } else {
                        mine["Value"] = true;
                        mine["Mutator"] = pair.Value["Mutator"]?.DeepClone() ?? 0;
                    }

                    taken.Add((scene, id));
                    _report.Pickups++;
                } else if (Int(pair.Value, "Value") > (mine == null ? int.MinValue : Int(mine, "Value"))) {
                    if (mine == null) {
                        mergedList.Add(pair.Value.DeepClone());
                    } else {
                        mine["Value"] = Int(pair.Value, "Value");
                    }

                    _report.WorldObjects++;
                }
            }

            // What only the joining player's save changed of the objects that follow the host goes back to how the
            // host's world has it, which is how a new save has it
            for (var i = mergedList.Count - 1; i >= 0; i--) {
                if (mergedList[i] is not JObject entry) {
                    continue;
                }

                var key = Key(entry);
                if (!hostEntries.ContainsKey(key) && ClassOf(classes, key.Item1, key.Item2) == "h") {
                    mergedList.RemoveAt(i);
                    _report.ClosedAgain++;
                }
            }
        }

        private static string? ClassOf(Dictionary<string, Dictionary<string, string>> classes, string scene, string id) {
            return classes.TryGetValue(scene, out var objects) && objects.TryGetValue(id, out var mergeClass)
                ? mergeClass
                : null;
        }

        /// <summary>
        /// The saved objects of a list of the scene data by scene in lower case and ID.
        /// </summary>
        private static Dictionary<(string, string), JObject> Entries(JObject scenes, string listName) {
            var entries = new Dictionary<(string, string), JObject>();
            if (scenes[listName]?["serializedList"] is JArray list) {
                foreach (var entry in list.OfType<JObject>()) {
                    entries[Key(entry)] = entry;
                }
            }

            return entries;
        }

        private static JArray SerializedList(JObject scenes, string listName) {
            if (scenes[listName] is not JObject collection) {
                scenes[listName] = collection = new JObject();
            }

            if (collection["serializedList"] is not JArray list) {
                collection["serializedList"] = list = new JArray();
            }

            return list;
        }

        private static (string, string) Key(JObject entry) {
            return (((string?) entry["SceneName"] ?? "").ToLowerInvariant(), (string?) entry["ID"] ?? "");
        }

        /// <summary>
        /// Whether a saved boolean of the world is set in a save.
        /// </summary>
        private static bool IsSet(JObject scenes, string scene, string id) {
            return Entries(scenes, "persistentBools").TryGetValue((scene, id), out var entry) && Bool(entry, "Value");
        }

        #endregion

        #region Wishes and the journal

        /// <summary>
        /// Takes the host's state of every wish but the deliveries, which are each player's.
        /// </summary>
        private void MergeQuests() {
            var hostList = NamedList(_host, "QuestCompletionData");
            var mergedList = NamedList(_merged, "QuestCompletionData", true)!;
            var hostEntries = ByName(hostList);
            var mergedEntries = ByName(mergedList);
            foreach (var pair in hostEntries) {
                if (IsOwnQuest(pair.Key)) {
                    continue;
                }

                if (!mergedEntries.TryGetValue(pair.Key, out var mine)) {
                    mergedList.Add(pair.Value.DeepClone());
                    _report.Wishes++;
                } else if (!JToken.DeepEquals(mine["Data"], pair.Value["Data"])) {
                    mine["Data"] = pair.Value["Data"]!.DeepClone();
                    _report.Wishes++;
                }
            }

            for (var i = mergedList.Count - 1; i >= 0; i--) {
                if (mergedList[i] is JObject entry && (string?) entry["Name"] is { } name && !IsOwnQuest(name) &&
                    !hostEntries.ContainsKey(name)) {
                    mergedList.RemoveAt(i);
                    _report.Wishes++;
                }
            }
        }

        private bool IsOwnQuest(string name) {
            return _data.Quests.TryGetValue(name, out var mergeClass) && mergeClass == "own";
        }

        /// <summary>
        /// Gives the joining player the reward of each wish that the host completed and they never did, the way they get
        /// the reward of a boss that the host beat (the user's rule of 2026-10-11, "补").
        /// </summary>
        private void GiveRewards() {
            var hostEntries = ByName(NamedList(_host, "QuestCompletionData"));
            var clientEntries = ByName(NamedList(_client, "QuestCompletionData"));
            var lastAct = Bool(_merged, "blackThreadWorld");
            foreach (var pair in _data.Rewards) {
                if (!hostEntries.TryGetValue(pair.Key, out var hostEntry) || !IsCompleted(hostEntry) ||
                    (clientEntries.TryGetValue(pair.Key, out var clientEntry) && IsCompleted(clientEntry))) {
                    continue;
                }

                foreach (var reward in pair.Value) {
                    var count = lastAct && reward.Act3 > 0 ? reward.Act3 : reward.Count;

                    // On top of what they have: a reward of rosaries or materials is more of them, like the wish
                    // gives it, whatever they had already
                    if (Give(reward.Item, count, true)) {
                        _report.Rewards++;
                        _report.Lines.Add($"reward of {pair.Key}: {count} {reward.Item}");
                    }
                }
            }
        }

        private static bool IsCompleted(JObject entry) {
            return Bool(entry["Data"], "IsCompleted") || Bool(entry["Data"], "WasEverCompleted");
        }

        /// <summary>
        /// The kills of each enemy: the larger count, and seen by either player.
        /// </summary>
        private void MergeJournal() {
            if (_host["EnemyJournalKillData"]?["list"] is not JArray hostList) {
                return;
            }

            if (_merged["EnemyJournalKillData"] is not JObject journal) {
                _merged["EnemyJournalKillData"] = journal = new JObject();
            }

            if (journal["list"] is not JArray mergedList) {
                journal["list"] = mergedList = new JArray();
            }

            var mergedEntries = ByName(mergedList);
            foreach (var entry in hostList.OfType<JObject>()) {
                if ((string?) entry["Name"] is not { } name) {
                    continue;
                }

                if (!mergedEntries.TryGetValue(name, out var mine)) {
                    mergedList.Add(entry.DeepClone());
                    continue;
                }

                if (mine["Record"] is not JObject record) {
                    mine["Record"] = entry["Record"]?.DeepClone();
                    continue;
                }

                record["Kills"] = System.Math.Max(Int(record, "Kills"), Int(entry["Record"], "Kills"));
                record["HasBeenSeen"] = Bool(record, "HasBeenSeen") || Bool(entry["Record"], "HasBeenSeen");
            }
        }

        #endregion

        #region Items

        /// <summary>
        /// The unique important items that the host has and the joining player hasn't. Items with more than one of them
        /// come by their sources (<see cref="GivePickups"/>), and story items that the host used up go in
        /// <see cref="UseUpStoryItems"/>.
        /// </summary>
        private void MergeCollectables() {
            foreach (var entry in ByName(NamedList(_host, "Collectables")).Values) {
                if ((string?) entry["Name"] is not { } name || !IsOnlyAdd(name, "col", out var item) || item.Counted ||
                    Int(entry["Data"], "Amount") <= 0) {
                    continue;
                }

                if (Give(name, 1)) {
                    _report.Items++;
                }
            }
        }

        /// <summary>
        /// The important tools that the host has and the joining player hasn't. Their pickups in the world aren't
        /// saved, and a shop that also sells one keeps no flag of its own, so the host's tool is given however they got
        /// it.
        /// </summary>
        private void MergeTools() {
            foreach (var entry in ByName(NamedList(_host, "Tools")).Values) {
                if ((string?) entry["Name"] is not { } name || !IsOnlyAdd(name, "tool", out _) ||
                    !Bool(entry["Data"], "IsUnlocked")) {
                    continue;
                }

                if (Give(name, 1)) {
                    _report.Items++;
                }
            }
        }

        /// <summary>
        /// The crests that the host has and the joining player hasn't.
        /// </summary>
        private void MergeCrests() {
            foreach (var entry in ByName(NamedList(_host, "ToolEquips")).Values) {
                if ((string?) entry["Name"] is not { } name || !IsOnlyAdd(name, "crest", out _) ||
                    !Bool(entry["Data"], "IsUnlocked")) {
                    continue;
                }

                if (Give(name, 1)) {
                    _report.Items++;
                }
            }
        }

        private bool IsOnlyAdd(string name, string kind, out CoopHostSyncItem item) {
            return _data.Items.TryGetValue(name, out item!) && item.In == kind && item.Class == "ONLY_ADD";
        }

        /// <summary>
        /// Gives the items of each pickup that the host took and the joining player hadn't. A pickup that gives one of
        /// several items gives those that the host has.
        /// </summary>
        private void GivePickups(List<(string Scene, string Id)> taken) {
            foreach (var (scene, id) in taken) {
                if (!_data.Sources.TryGetValue(scene, out var objects) || !objects.TryGetValue(id, out var items)) {
                    continue;
                }

                foreach (var name in items) {
                    if (items.Count > 1 && !HostHas(name)) {
                        continue;
                    }

                    if (IsUsedUp(name)) {
                        continue;
                    }

                    if (Give(name, 1)) {
                        _report.Lines.Add($"pickup {scene}/{id}: {name}");
                    }
                }
            }
        }

        /// <summary>
        /// The wish items that a boss drops once, which the host has and the joining player hasn't, while their wish is
        /// still open in the merged save. The boss is beaten in the host's world, so they couldn't get them any more.
        /// </summary>
        private void GiveOnceDrops() {
            var quests = ByName(NamedList(_merged, "QuestCompletionData"));
            foreach (var pair in _data.Items) {
                if (pair.Value.OnceFor == null || Amount(_merged, pair.Key) > 0 || Amount(_host, pair.Key) <= 0 ||
                    !pair.Value.OnceFor.Any(quest => quests.TryGetValue(quest, out var entry) && !IsCompleted(entry))) {
                    continue;
                }

                if (Give(pair.Key, Amount(_host, pair.Key))) {
                    _report.Lines.Add($"once dropped: {pair.Key}");
                }
            }
        }

        /// <summary>
        /// The keys that open locks: one less for each lock that the host opened and the joining player hadn't, and one
        /// back for each one they had opened that the host hasn't, which is closed in the merged save again.
        /// </summary>
        private void MergeKeys() {
            foreach (var pair in _data.Locks) {
                var change = 0;
                foreach (var keyLock in pair.Value) {
                    var wasOpen = IsOpen(keyLock, _client, _clientScenes);
                    var isOpen = IsOpen(keyLock, _merged, _mergedScenes);
                    change += wasOpen == isOpen ? 0 : wasOpen ? 1 : -1;
                }

                if (change == 0) {
                    continue;
                }

                var entry = CollectableEntry(pair.Key, true)!;
                var data = (JObject) entry["Data"]!;
                var amount = System.Math.Max(0, Int(data, "Amount") + change);
                _report.Keys += amount - Int(data, "Amount");
                data["Amount"] = amount;
                _report.Lines.Add($"{pair.Key}: {change:+#;-#;0} for the locks");
            }
        }

        private static bool IsOpen(CoopHostSyncLock keyLock, JObject playerData, JObject scenes) {
            return keyLock.Bool != null
                ? Bool(playerData, keyLock.Bool)
                : keyLock.Scene != null && keyLock.Id != null && IsSet(scenes, keyLock.Scene, keyLock.Id);
        }

        /// <summary>
        /// Takes away the story items that the host used up: what took them is used up in the merged save too.
        /// </summary>
        private void UseUpStoryItems() {
            foreach (var pair in _data.Items) {
                if (!pair.Value.Story || !IsUsedUp(pair.Key) || CollectableEntry(pair.Key, false) is not { } entry ||
                    Int(entry["Data"], "Amount") == 0) {
                    continue;
                }

                entry["Data"]!["Amount"] = 0;
                _report.Lines.Add($"used up by the host: {pair.Key}");
            }
        }

        /// <summary>
        /// Whether the host had a story item and used it up: its entry is there with none left.
        /// </summary>
        private bool IsUsedUp(string name) {
            return _data.Items.TryGetValue(name, out var item) && item.Story &&
                   ByName(NamedList(_host, "Collectables")).TryGetValue(name, out var entry) &&
                   Int(entry["Data"], "Amount") == 0;
        }

        /// <summary>
        /// Whether the host has an item.
        /// </summary>
        private bool HostHas(string name) {
            if (!_data.Items.TryGetValue(name, out var item)) {
                return false;
            }

            return item.In switch {
                "col" => Amount(_host, name) > 0,
                "tool" => IsUnlocked(_host, "Tools", name),
                "crest" => IsUnlocked(_host, "ToolEquips", name),
                "relic" => ByName(NamedList(_host, "Relics")).TryGetValue(name, out var relic) &&
                           Bool(relic["Data"], "IsCollected"),
                _ => true
            };
        }

        /// <summary>
        /// Gives the joining player an item: one more of an item with more than one of them, or the item if they don't
        /// have it.
        /// </summary>
        /// <param name="name">The item.</param>
        /// <param name="count">How many of it.</param>
        /// <param name="onTop">Whether a collectable that isn't an important one-of-a-kind item, like rosaries or a
        /// material, comes on top of what they have, as a reward gives it, rather than making up to the count.</param>
        /// <returns>Whether anything changed.</returns>
        private bool Give(string name, int count, bool onTop = false) {
            if (count <= 0 || !_data.Items.TryGetValue(name, out var item)) {
                return false;
            }

            switch (item.In) {
                case "col": {
                    var data = (JObject) CollectableEntry(name, true)!["Data"]!;
                    var amount = Int(data, "Amount");
                    var given = item.Counted || (onTop && item.Class != "ONLY_ADD")
                        ? amount + count
                        : System.Math.Max(amount, count);
                    if (given == amount) {
                        return false;
                    }

                    data["Amount"] = given;
                    if (item.Kind == "key") {
                        _report.Keys += given - amount;
                    }

                    return true;
                }
                case "tool":
                    return UnlockTool(name);
                case "crest":
                    return UnlockCrest(name);
                case "relic": {
                    var list = NamedList(_merged, "Relics", true)!;
                    var entries = ByName(list);
                    if (entries.TryGetValue(name, out var relic)) {
                        if (Bool(relic["Data"], "IsCollected")) {
                            return false;
                        }

                        relic["Data"]!["IsCollected"] = true;
                        return true;
                    }

                    list.Add(new JObject {
                        ["Name"] = name,
                        ["Data"] = new JObject {
                            ["IsCollected"] = true, ["IsDeposited"] = false, ["HasSeenInRelicBoard"] = false
                        }
                    });
                    return true;
                }
                case "pd":
                    if (item.Shards) {
                        _shards += count;
                        return true;
                    }

                    if (item.Spools) {
                        _pieces += count;
                        return true;
                    }

                    if (item.Bool != null) {
                        if (Bool(_merged, item.Bool)) {
                            return false;
                        }

                        _merged[item.Bool] = true;
                        return true;
                    }

                    if (item.Add != null) {
                        _merged[item.Add] = Int(_merged, item.Add) + count * item.N;
                        return true;
                    }

                    if (item.AtLeast != null && Int(_merged, item.AtLeast) < item.N) {
                        _merged[item.AtLeast] = item.N;
                        return true;
                    }

                    return false;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Unlocks a tool for the joining player, as the host has it, or as ToolItem.Unlock makes it.
        /// </summary>
        private bool UnlockTool(string name) {
            var list = NamedList(_merged, "Tools", true)!;
            var entries = ByName(list);
            if (entries.TryGetValue(name, out var mine)) {
                if (Bool(mine["Data"], "IsUnlocked")) {
                    return false;
                }

                mine["Data"]!["IsUnlocked"] = true;
                mine["Data"]!["IsHidden"] = false;
                return true;
            }

            var hostEntry = ByName(NamedList(_host, "Tools")).TryGetValue(name, out var theirs) ? theirs : null;
            list.Add(new JObject {
                ["Name"] = name,
                ["Data"] = new JObject {
                    ["IsUnlocked"] = true,
                    ["IsHidden"] = false,
                    ["HasBeenSeen"] = false,
                    ["HasBeenSelected"] = false,
                    ["AmountLeft"] = hostEntry == null ? 0 : Int(hostEntry["Data"], "AmountLeft")
                }
            });
            return true;
        }

        /// <summary>
        /// Unlocks a crest for the joining player as ToolCrest.Unlock does: with the slots of the version before it,
        /// which it is worn instead of, or with the slots it comes with.
        /// </summary>
        private bool UnlockCrest(string name) {
            var list = NamedList(_merged, "ToolEquips", true)!;
            var entries = ByName(list);
            if (entries.TryGetValue(name, out var mine) && Bool(mine["Data"], "IsUnlocked")) {
                return false;
            }

            JToken slots = new JArray();
            if (_data.CrestVersions.TryGetValue(name, out var previous)) {
                UnlockCrest(previous);
                if (ByName(list).TryGetValue(previous, out var before) && before["Data"]?["Slots"] is JArray beforeSlots) {
                    slots = beforeSlots.DeepClone();
                }

                if ((string?) _merged["CurrentCrestID"] == previous) {
                    _merged["CurrentCrestID"] = name;
                }
            }

            var data = new JObject { ["IsUnlocked"] = true, ["Slots"] = slots, ["DisplayNewIndicator"] = true };
            if (mine != null) {
                mine["Data"] = data;
            } else {
                list.Add(new JObject { ["Name"] = name, ["Data"] = data });
            }

            return true;
        }

        /// <summary>
        /// The entry of a collectable in the merged save, made with none of it if it isn't there and it should be.
        /// </summary>
        private JObject? CollectableEntry(string name, bool create) {
            var list = NamedList(_merged, "Collectables", true)!;
            if (ByName(list).TryGetValue(name, out var entry)) {
                return entry;
            }

            if (!create) {
                return null;
            }

            entry = new JObject {
                ["Name"] = name,
                ["Data"] = new JObject { ["Amount"] = 0, ["IsSeenMask"] = 0, ["AmountWhileHidden"] = 0 }
            };
            list.Add(entry);
            return entry;
        }

        private static int Amount(JObject playerData, string name) {
            return ByName(NamedList(playerData, "Collectables")).TryGetValue(name, out var entry)
                ? Int(entry["Data"], "Amount")
                : 0;
        }

        private static bool IsUnlocked(JObject playerData, string listName, string name) {
            return ByName(NamedList(playerData, listName)).TryGetValue(name, out var entry) &&
                   Bool(entry["Data"], "IsUnlocked");
        }

        #endregion

        #region Counts and specials

        /// <summary>
        /// Puts the mask shards and spool pieces that the joining player got into their masks and spools.
        /// </summary>
        private void SetCounts() {
            if (_shards > 0) {
                var masks = Int(_merged, "maxHealthBase");
                var before = (masks - BaseMasks) * ShardsPerMask + Int(_merged, "heartPieces");
                var after = System.Math.Min(MaxShards, before + _shards);
                var newMasks = BaseMasks + after / ShardsPerMask;
                _merged["maxHealthBase"] = newMasks;
                _merged["heartPieces"] = after % ShardsPerMask;
                _merged["maxHealth"] = Int(_merged, "maxHealth") + newMasks - masks;
                _report.Shards = after - before;
            }

            if (_pieces > 0) {
                var silk = Int(_merged, "silkMax");
                var before = (silk - BaseSilk) * PiecesPerSpool + Int(_merged, "silkSpoolParts");
                var after = System.Math.Min(MaxPieces, before + _pieces);
                _merged["silkMax"] = BaseSilk + after / PiecesPerSpool;
                _merged["silkSpoolParts"] = after % PiecesPerSpool;
                _report.Spools = after - before;
            }
        }

        /// <summary>
        /// The fields that merge their own way.
        /// </summary>
        private void MergeSpecials() {
            // The colour of the home is the host's once they painted it
            if (Int(_host, "BelltownHouseColour") != 0) {
                _merged["BelltownHouseColour"] = _host["BelltownHouseColour"]!.DeepClone();
            }

            // Wish items that a boss drops once, picked up in either save
            foreach (var name in new[] { "gotPickledRoachEgg", "broodMotherEyeCollected" }) {
                if (Bool(_host, name)) {
                    _merged[name] = true;
                }
            }

            // The spots of a wish that either player found
            if (_host["SteelQuestSpots"] is JArray hostSpots && _merged["SteelQuestSpots"] is JArray spots) {
                foreach (var spot in spots.OfType<JObject>()) {
                    var scene = (string?) spot["SceneName"];
                    if (hostSpots.OfType<JObject>().Any(other =>
                            (string?) other["SceneName"] == scene && Bool(other, "IsSeen"))) {
                        spot["IsSeen"] = true;
                    }
                }
            }

            // The extra slots for tools that the host has, with nothing in them
            if (NamedList(_host, "ExtraToolEquips") is { } hostSlots) {
                var mergedSlots = NamedList(_merged, "ExtraToolEquips", true)!;
                var names = ByName(mergedSlots);
                foreach (var entry in hostSlots.OfType<JObject>()) {
                    if ((string?) entry["Name"] is not { } name || names.ContainsKey(name)) {
                        continue;
                    }

                    var copy = (JObject) entry.DeepClone();
                    foreach (var value in copy.Descendants().OfType<JProperty>().ToList()) {
                        if (value.Name == "EquippedTool") {
                            value.Value = "";
                        }
                    }

                    mergedSlots.Add(copy);
                }
            }
        }

        /// <summary>
        /// Puts the hero on the crest that the story has them on: the crest of the curse while the host's world has them
        /// cursed, and off it once it hasn't, onto the crest the cure gives if it was cured.
        /// </summary>
        private void FixCrest() {
            var cursed = Bool(_merged, "gainedCurse") && !Bool(_merged, "BelltownDoctorCuredCurse");
            var crest = (string?) _merged["CurrentCrestID"] ?? "";
            if (cursed && crest != CursedCrest) {
                if (ByName(NamedList(_host, "ToolEquips")).TryGetValue(CursedCrest, out var hostCrest) &&
                    hostCrest["Data"] is JObject hostCrestData) {
                    // The host's crest with the tools that the joining player has, and none in place of the others
                    var data = (JObject) hostCrestData.DeepClone();
                    foreach (var tool in data.Descendants().OfType<JProperty>().Where(p => p.Name == "EquippedTool")
                                 .ToList()) {
                        if ((string?) tool.Value is { Length: > 0 } toolName &&
                            !IsUnlocked(_merged, "Tools", toolName)) {
                            tool.Value = "";
                        }
                    }

                    var list = NamedList(_merged, "ToolEquips", true)!;
                    if (ByName(list).TryGetValue(CursedCrest, out var mine)) {
                        mine["Data"] = data;
                    } else {
                        list.Add(new JObject { ["Name"] = CursedCrest, ["Data"] = data });
                    }
                }

                _merged["PreviousCrestID"] = crest;
                _merged["CurrentCrestID"] = CursedCrest;
                _report.Lines.Add($"crest {crest} -> {CursedCrest}");
            } else if (!cursed && crest == CursedCrest) {
                var previous = (string?) _merged["PreviousCrestID"] ?? "";
                var next = Bool(_merged, "BelltownDoctorCuredCurse") && IsUnlocked(_merged, "ToolEquips", CuredCrest)
                    ? CuredCrest
                    : previous.Length > 0 && previous != CursedCrest && IsUnlocked(_merged, "ToolEquips", previous)
                        ? previous
                        : FirstCrest;
                _merged["CurrentCrestID"] = next;
                _report.Lines.Add($"crest {CursedCrest} -> {next}");
            }
        }

        /// <summary>
        /// Notes the arrivals that the host had and the joining player hasn't, which they still see.
        /// </summary>
        private void NoteArrivals() {
            foreach (var flag in ArrivalFlags) {
                if (Bool(_merged, flag) && !Bool(_client, flag)) {
                    _report.ArrivalsToSee.Add(flag);
                }
            }
        }

        #endregion
    }

    #region JSON

    /// <summary>
    /// The list of a named list of the player data ({"savedData": [...]}), or null if there is none and it isn't made.
    /// </summary>
    private static JArray? NamedList(JObject playerData, string name, bool create = false) {
        if (playerData[name] is not JObject collection) {
            if (!create) {
                return null;
            }

            playerData[name] = collection = new JObject();
        }

        if (collection["savedData"] is not JArray list) {
            if (!create) {
                return null;
            }

            collection["savedData"] = list = new JArray();
        }

        return list;
    }

    /// <summary>
    /// The entries of a list by their names; the first of two with the same name.
    /// </summary>
    private static Dictionary<string, JObject> ByName(JArray? list) {
        var entries = new Dictionary<string, JObject>(StringComparer.Ordinal);
        if (list == null) {
            return entries;
        }

        foreach (var entry in list.OfType<JObject>()) {
            if ((string?) entry["Name"] is { } name && !entries.ContainsKey(name)) {
                entries[name] = entry;
            }
        }

        return entries;
    }

    private static bool Bool(JToken? parent, string name) {
        return parent?[name] is JValue { Type: JTokenType.Boolean } value && (bool) value;
    }

    private static int Int(JToken? parent, string name) {
        return parent?[name] is JValue value && value.Type is JTokenType.Integer or JTokenType.Float
            ? Convert.ToInt32(value.Value)
            : 0;
    }

    private static ulong ULong(JToken? token) {
        return token is JValue value
            ? value.Value switch {
                BigInteger big => (ulong) big,
                long number => unchecked((ulong) number),
                ulong number => number,
                int number => unchecked((ulong) number),
                _ => 0UL
            }
            : 0UL;
    }

    #endregion
}
