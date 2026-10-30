using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SSMP.Game.Client;

/// <summary>
/// Parts of rooms that each player has to themselves. Nothing in them is shared between the two games: what moves there
/// runs in each game on its own rather than as an entity that the scene host runs for both, a hit or touch of the local
/// player there stays in this game, and the copies of the partner's attacks leave it alone. The places are the games
/// that each player plays on their own, whose rounds hide the players from each other (see <see cref="MinigameCoop"/>).
///
/// What is in a place is taken down once per loaded room, before anything in it has moved, since the things of a place
/// can leave it while they are in use: the festival's fleas fly about without a parent and go back when they are done.
/// Whatever is under something that was taken down counts too, like what those fleas make for themselves. Every part of
/// a place is numbered in the order it was taken down, which is the same in every game, since every game loads the same
/// room the same way: that is how one game tells another which part of a place it means.
///
/// The things of <see cref="Things"/> and the pods and balloons (see CoopHits.PersonalTypes) are what was made each
/// player's own so that playing together is not too hard, and nothing of what the partner does to theirs shows here
/// (USER 10-30, "直接去掉样子同步，在任何地方都不要留样子同步但是各用各的了"). A switch may share them all again later
/// (USER 10-30, "我计划后面加个按键可以一键打开这些为了不要太难而改成各用各的物品的同步"). A round of a place shows to a
/// player who watches it instead of playing (see <see cref="MinigameMirror"/>).
/// </summary>
internal static class PersonalPlaces {
    /// <summary>
    /// The places, each as the path of the object that everything in it is under, from an object at the top of a room.
    /// </summary>
    private static readonly string[][] Places = [
        // The festival's game of landing on fleas from above. Both players went for the same fleas, and one of them was
        // soon left with nothing to land on
        ["Caravan_States", "Flea Festival", "Flea Game - Bouncing"],

        // The festival's games of keeping fleas up and of dodging them. Played with the same fleas, whose game the
        // scene host ran, until each player got a round of their own (2026-10-22, the user: "全都改成一起开始但是各玩
        // 各的")
        ["Caravan_States", "Flea Festival", "Flea Game - Juggling"],
        ["Caravan_States", "Flea Festival", "Flea Game - Dodging"],

        // The contest of throwing pins at small targets, which each player enters, pays for and wins on their own. A hit
        // of one player on a target was sent to the other game, where it scored for whoever was playing there
        ["Black Thread States", "Normal World", "Pin Gallery States", "Here"]
    ];

    /// <summary>
    /// The things that each player has to themselves wherever a room has them, or only in the rooms of
    /// <see cref="ThingRooms"/>, by how their names start, each with what is under it: the floors that crumble under a
    /// player, or under their attack, and come back a moment later, the flies that a player bounces off and kills, which
    /// come back a moment later too, the crystal creatures that burst as a player comes near and grow back, and the
    /// floating ice crystals that a player bounces off and shatters, which form again. Other small flies are shared like
    /// any creature. Both games used to break them together, so a floor that the partner stood on was gone under this
    /// player as well, who then had to wait for it to come back - or, where lava
    /// rises behind them, fell in - and a fly that the partner bounced off was gone for this player too, where lava
    /// rises behind them until one of them was put back (USER 10-23, "本来时间就很赶了还要等"). The partner's body sets
    /// none of them off here, nor does the partner coming near them (see GamePatcher.IsAboutTheLocalPlayer), so a
    /// floor stays whole here under a partner who is seen to fall through it. Floors that stay broken are still
    /// shared, since the save keeps them (see CoopSave.WorldTriggerKinds).
    /// </summary>
    private static readonly string[] Things = [
        "moss_crumble_plat",
        "lava_crumble_plat",
        "bone_plat_01_crumble",
        "bone_plat_02_crumble",
        "bone_plat_crumble",
        "crumble_plat_peak_",
        "memory_ground_plat",

        // The flies of the lava race (see ThingRooms), and the drifting flies of the snowy peaks, of 4 health, which
        // fly back in about 3 s after they die (USER 10-24, "雪山那里那种小飞虫也不要共享了"). The partner's killing
        // theirs shows nothing here (USER 10-25, the look of it shown on this game's own was taken out: "删掉吧")
        "Bone Goomba Bounce Fly",
        "Peaks Drifter",

        // The crystal creatures of the peaks, which burst into spikes as a player comes near or strikes them and grow
        // back a few seconds later (USER 10-25, "像黑色晶体一样的虫子也不能共享"). The partner's bursting theirs shows
        // nothing here
        "Crystal Drifter",

        // The ice crystals that float on the icy water of the peaks, which a player can't stand on but strikes from
        // above to bounce off, and which form again a few seconds after they shatter (USER 10-26, "那个冰水上的冰块共享，
        // 改成不要共享", "一种可以下劈但是无法站立的场景物体"). A strike of the partner shattered them here too
        "Float Crystal"
    ];

    /// <summary>
    /// What starts like one of <see cref="Things"/> but is shared all the same: the giant crystal creatures, which
    /// shoot their spikes again and again and are fought, rather than burst once as a player passes.
    /// </summary>
    private static readonly string[] NotThings = ["Crystal Drifter Giant"];

    /// <summary>
    /// The one room that some of <see cref="Things"/> are things in, by how their names start; anywhere else they are
    /// shared like any creature. The flies of the lava race, where lava rises behind the players, are each player's own
    /// there alone: the same flies in another room, where they come back on their own 2.5 s after they die and have
    /// no lava behind them, are shared, like all the other small flies but the 4-health flies of the snowy peaks
    /// (USER 10-25, "不是所有的都要不同步，有很多不属于4血小飞虫的小飞虫还是要同步的").
    /// </summary>
    private static readonly Dictionary<string, string> ThingRooms = new() {
        ["Bone Goomba Bounce Fly"] = "Abyss_09"
    };

    /// <summary>
    /// The loaded rooms that were looked through already, by their handle, each with the instance IDs of what was taken
    /// down in it.
    /// </summary>
    private static readonly Dictionary<int, List<int>> LookedThrough = [];

    /// <summary>
    /// What was in a place or a thing when its room was looked through, by instance ID.
    /// </summary>
    private static readonly HashSet<int> InPlace = [];

    /// <summary>
    /// What was in a thing when its room was looked through, by instance ID: the part of <see cref="InPlace"/> that
    /// came from <see cref="Things"/> rather than <see cref="Places"/>.
    /// </summary>
    private static readonly HashSet<int> InThing = [];

    /// <summary>
    /// The parts of the places in the loaded rooms, by the handle of the room, with one list for each of
    /// <see cref="Places"/>, empty for a place that the room does not have: the place itself first, then everything
    /// under it, in the order they were taken down.
    /// </summary>
    private static readonly Dictionary<int, List<GameObject>[]> PlaceParts = [];

    /// <summary>
    /// The objects of the room being looked through.
    /// </summary>
    private static readonly List<Transform> Parts = [];

    /// <summary>
    /// A thing that a player strikes (one of <see cref="Things"/> with health: a fly or a crystal creature), with where
    /// it was as its room was looked through, before anything in the room had moved.
    /// </summary>
    public readonly struct StruckThing {
        /// <summary>
        /// The thing.
        /// </summary>
        public readonly GameObject Thing;

        /// <summary>
        /// Where it was as its room was looked through.
        /// </summary>
        public readonly Vector3 Start;

        public StruckThing(GameObject thing, Vector3 start) {
            Thing = thing;
            Start = start;
        }
    }

    /// <summary>
    /// The things of the loaded rooms that a player strikes, by the handle of the room (see <see cref="StruckThing"/>),
    /// for HarpoonWatch to say where they were.
    /// </summary>
    private static readonly Dictionary<int, List<StruckThing>> StruckThings = [];

    static PersonalPlaces() {
        // A room is looked through as it loads, before its things have started. One that was loaded before anything
        // was asked is looked through when one of its things is first asked about. What was in a room is forgotten
        // when the room is gone
        SceneManager.sceneLoaded += (scene, _) => LookThrough(scene);
        SceneManager.sceneUnloaded += Forget;
    }

    /// <summary>
    /// Looks through the rooms that are loaded now, and from here on every room as it loads (see the class), rather
    /// than when one of its things is first asked about, by which time they may have moved.
    /// </summary>
    public static void LookThroughLoaded() {
        for (var index = 0; index < SceneManager.sceneCount; index++) {
            LookThrough(SceneManager.GetSceneAt(index));
        }
    }

    /// <summary>
    /// Whether an object is in a place that each player has to themselves.
    /// </summary>
    /// <param name="gameObject">The object.</param>
    public static bool Contains(GameObject gameObject) {
        LookThrough(gameObject.scene);
        if (InPlace.Count == 0) {
            return false;
        }

        for (var current = gameObject.transform; current != null; current = current.parent) {
            if (InPlace.Contains(current.gameObject.GetInstanceID())) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an object is in one of the things that each player has to themselves (see <see cref="Things"/>), the
    /// floors, flies and creatures of their own, rather than in a place of a game each plays on their own, whose keepers
    /// and the like may still hear both players.
    /// </summary>
    /// <param name="gameObject">The object.</param>
    public static bool IsThingPart(GameObject gameObject) {
        LookThrough(gameObject.scene);
        if (InThing.Count == 0) {
            return false;
        }

        for (var current = gameObject.transform; current != null; current = current.parent) {
            if (InThing.Contains(current.gameObject.GetInstanceID())) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a name is that of one of the things that each player has to themselves (see <see cref="Things"/>), or
    /// starts like one, in a room: what such a thing spawns is called after it.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="scene">The room of the object with the name.</param>
    public static bool IsThingName(string name, Scene scene) {
        return IsThing(name, scene.name);
    }

    /// <summary>
    /// Finds the place that an object is under, as it stands now: an object that left its place while in use is not
    /// found in it. Meant for the objects that run a place, which stay where they are.
    /// </summary>
    /// <param name="gameObject">The object.</param>
    /// <param name="place">The number of the place, which <see cref="GetParts"/> takes.</param>
    /// <returns>Whether the object is under a place.</returns>
    public static bool TryFindPlace(GameObject gameObject, out int place) {
        place = -1;
        var scene = gameObject.scene;
        LookThrough(scene);
        if (!PlaceParts.TryGetValue(scene.handle, out var places)) {
            return false;
        }

        for (var index = 0; index < places.Length; index++) {
            if (places[index].Count > 0 && places[index][0] is { } root && root != null &&
                gameObject.transform.IsChildOf(root.transform)) {
                place = index;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the parts of a place in a loaded room, by the number each part has in every game: the place itself first,
    /// then everything under it. A part that is gone since is null in it.
    /// </summary>
    /// <param name="scene">The room.</param>
    /// <param name="place">The number of the place (see <see cref="TryFindPlace"/>).</param>
    /// <returns>The parts, or null if the room has no such place.</returns>
    public static IReadOnlyList<GameObject>? GetParts(Scene scene, int place) {
        LookThrough(scene);
        return PlaceParts.TryGetValue(scene.handle, out var places) && place >= 0 && place < places.Length &&
               places[place].Count > 0
            ? places[place]
            : null;
    }

    /// <summary>
    /// Finds the things of the loaded rooms that a player strikes (see <see cref="StruckThing"/>) that are near a point
    /// now, or were as their room loaded: one that died is put far away until it comes back.
    /// </summary>
    /// <param name="at">The point.</param>
    /// <param name="distance">How near.</param>
    /// <param name="into">The list to fill, emptied first.</param>
    public static void FindStruckThingsNear(Vector2 at, float distance, List<StruckThing> into) {
        into.Clear();
        var most = distance * distance;
        foreach (var things in StruckThings.Values) {
            foreach (var thing in things) {
                if (thing.Thing == null) {
                    continue;
                }

                if (((Vector2) thing.Thing.transform.position - at).sqrMagnitude <= most ||
                    ((Vector2) thing.Start - at).sqrMagnitude <= most) {
                    into.Add(thing);
                }
            }
        }
    }

    /// <summary>
    /// Takes down what is in the places and things of a room, once per loaded room.
    /// </summary>
    private static void LookThrough(Scene scene) {
        if (!scene.IsValid() || !scene.isLoaded || LookedThrough.ContainsKey(scene.handle)) {
            return;
        }

        var takenDown = new List<int>();
        LookedThrough[scene.handle] = takenDown;

        // What is kept across rooms is in none of them
        var room = scene.name;
        if (room == "DontDestroyOnLoad") {
            return;
        }

        List<GameObject>[]? places = null;
        foreach (var top in scene.GetRootGameObjects()) {
            for (var index = 0; index < Places.Length; index++) {
                var path = Places[index];
                if (top.name != path[0]) {
                    continue;
                }

                var place = top.transform;
                for (var step = 1; step < path.Length && place != null; step++) {
                    place = place.Find(path[step]);
                }

                if (place == null) {
                    continue;
                }

                if (places == null) {
                    places = new List<GameObject>[Places.Length];
                    for (var other = 0; other < places.Length; other++) {
                        places[other] = [];
                    }
                }

                TakeDown(place, takenDown, places[index], false);
            }

            top.GetComponentsInChildren(true, Parts);
            foreach (var part in Parts) {
                if (!IsThing(part.name, room)) {
                    continue;
                }

                TakeDown(part, takenDown, null, true);
                if (part.TryGetComponent<HealthManager>(out _)) {
                    if (!StruckThings.TryGetValue(scene.handle, out var struck)) {
                        StruckThings[scene.handle] = struck = [];
                    }

                    struck.Add(new StruckThing(part.gameObject, part.position));
                }
            }
        }

        Parts.Clear();
        if (places != null) {
            PlaceParts[scene.handle] = places;
        }
    }

    /// <summary>
    /// Takes down an object and everything under it, numbering them as parts of a place when it is one.
    /// </summary>
    private static void TakeDown(Transform root, List<int> takenDown, List<GameObject>? placeParts, bool thing) {
        foreach (var part in root.GetComponentsInChildren<Transform>(true)) {
            var id = part.gameObject.GetInstanceID();
            if (InPlace.Add(id)) {
                takenDown.Add(id);
            }

            if (thing) {
                InThing.Add(id);
            }

            placeParts?.Add(part.gameObject);
        }
    }

    /// <summary>
    /// Whether an object of this name in this room is one of <see cref="Things"/>.
    /// </summary>
    private static bool IsThing(string name, string room) {
        foreach (var start in NotThings) {
            if (name.StartsWith(start, StringComparison.Ordinal)) {
                return false;
            }
        }

        foreach (var start in Things) {
            if (name.StartsWith(start, StringComparison.Ordinal)) {
                return !ThingRooms.TryGetValue(start, out var onlyRoom) || onlyRoom == room;
            }
        }

        return false;
    }

    /// <summary>
    /// Forgets what was taken down in a room that is gone.
    /// </summary>
    private static void Forget(Scene scene) {
        PlaceParts.Remove(scene.handle);
        StruckThings.Remove(scene.handle);
        if (!LookedThrough.TryGetValue(scene.handle, out var takenDown)) {
            return;
        }

        LookedThrough.Remove(scene.handle);
        foreach (var id in takenDown) {
            InPlace.Remove(id);
            InThing.Remove(id);
        }
    }
}
