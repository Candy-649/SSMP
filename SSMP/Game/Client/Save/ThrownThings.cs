using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using SSMP.Game.Client.Entity.Component;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Random = UnityEngine.Random;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The things that creatures throw, under the same name in every game: a ball that bursts into caltrops, a javelin
/// that sticks in a wall, a bell that a player can knock away. Each game made its own of what a creature threw, from
/// the same throw, and after that each went its own way: it burst on that game's own player, scattered with that game's
/// own dice and was knocked away by that game's own player, and by the copies of the partner's swing as well. So the
/// caltrops lay in other places in the two games, and a player walked over them unharmed in the other's game and was
/// hurt by ones that the other could not see (USER 10-12).
///
/// A thing gets its name, its key, as it comes out of the pool, before it is switched on: the game that runs the
/// creature makes one up for what the creature throws and sends it with the throw (EntityFsmActions), and the other
/// game gives it to its own; what a creature throws on a clock while its state lasts is named in the order it is
/// thrown, after a number sent as the state begins (see <see cref="Series"/>); what a named thing throws in turn is
/// named after it in both games, by what it is and how many of those it threw before. With the name the two games
/// share
/// - the dice: everything that a named thing rolls - where it scatters what it bursts into, how long it lies, how high
///   it bounces - comes from its name rather than from the game's own dice (see SharedDice and CoopHits), so both games
///   roll the same without a word between them;
/// - the player it goes after: the one the creature that threw it went after, and from then on whoever touched it last;
/// - what the players do to it: a player who strikes it, touches it or is hurt by it plays that in their own game at
///   once and sends it under its name, with where the thing was and how it moved, and the other game puts its own
///   there and does the same to it (CoopHits). A copy of the partner's swing no longer touches it.
/// A thing thrown before a player came into the room has no twin in that player's game.
/// </summary>
internal static class ThrownThings {
    /// <summary>
    /// What a throw says of the player it goes after when that is the player of the game that threw it.
    /// </summary>
    public const ushort ThrowersPlayer = ushort.MaxValue;

    /// <summary>
    /// What a throw says of the player it goes after when it goes after no one.
    /// </summary>
    public const ushort NoPlayer = ushort.MaxValue - 1;

    /// <summary>
    /// What starts a path that names a thrown thing rather than an object of a scene (see
    /// <see cref="PathOf(GameObject)"/>).
    /// </summary>
    private const char PathStart = '#';

    /// <summary>
    /// What stands between the key of a thrown thing and the path of one of its parts under it.
    /// </summary>
    private const char PartStart = '|';

    /// <summary>
    /// What stands between the path of a part of a thrown thing and where the thing was (see <see cref="PathOf(string,
    /// ThrownMark, GameObject, ThrownPlace)"/>).
    /// </summary>
    private const char PlaceStart = '@';

    /// <summary>
    /// How many characters where a thrown thing was takes in a path: four numbers as eight hexadecimal digits each, the
    /// same in every game.
    /// </summary>
    private const int PlaceLength = 4 * 8;

    /// <summary>
    /// What the keys of this game's throws start with, so that the throws of two games that ran the same room never
    /// share a key.
    /// </summary>
    private static readonly string Prefix = Guid.NewGuid().ToString("N").Substring(0, 8);

    /// <summary>
    /// The number of the last throw of this game.
    /// </summary>
    private static int _lastNumber;

    /// <summary>
    /// The thrown things with a name, by their keys.
    /// </summary>
    private static readonly Dictionary<string, ThrownMark> ByKey = new();

    /// <summary>
    /// The names that the next things coming out of the pool are given, or null.
    /// </summary>
    private static Pending? _pending;

    /// <summary>
    /// What the actions of creatures that this game runs threw in the frame they last ran in, to be sent with them.
    /// </summary>
    private static readonly ConditionalWeakTable<FsmStateAction, HostThrow> HostThrows = new();

    /// <summary>
    /// What a throw says of the player a thing goes after: the player's own number, <see cref="ThrowersPlayer"/> or
    /// <see cref="NoPlayer"/> (set by CoopHits).
    /// </summary>
    public static Func<GameObject?, ushort>? CodeOf { get; set; }

    /// <summary>
    /// The figure of the player that a throw says a thing goes after, by what it says and who threw it, or null for no
    /// one (set by CoopHits).
    /// </summary>
    public static Func<ushort, ushort, GameObject?>? FigureOf { get; set; }

    /// <summary>
    /// How many thrown things have a name now.
    /// </summary>
    public static int Count => ByKey.Count;

    /// <summary>
    /// Whether names are waiting for the next things that come out of the pool.
    /// </summary>
    public static bool IsExpecting => _pending != null;

    /// <summary>
    /// Whether a prefab is something that is named when it is thrown: something that can be touched, which a sound or a
    /// puff of dust cannot, and not a creature, which entity sync looks after.
    /// </summary>
    public static bool CanBeThrown(GameObject prefab) {
        return prefab.GetComponentInChildren<Collider2D>(true) != null &&
               prefab.GetComponentInChildren<HealthManager>(true) == null;
    }

    /// <summary>
    /// The mark of the named thrown thing that an object is, or is a part of.
    /// </summary>
    /// <param name="gameObject">The object.</param>
    /// <param name="mark">The mark.</param>
    /// <returns>Whether the object is, or is a part of, a named thrown thing.</returns>
    public static bool TryGetMark(GameObject? gameObject, out ThrownMark mark) {
        mark = null!;
        if (ByKey.Count == 0 || gameObject == null) {
            return false;
        }

        var found = gameObject.GetComponentInParent<ThrownMark>(true);
        if (found == null || found.Key == null) {
            return false;
        }

        mark = found;
        return true;
    }

    /// <summary>
    /// Whether an object is, or is a part of, a named thrown thing.
    /// </summary>
    public static bool IsThrown(GameObject? gameObject) {
        return TryGetMark(gameObject, out _);
    }

    /// <summary>
    /// Whether a path names a thrown thing (see <see cref="PathOf(GameObject)"/>).
    /// </summary>
    public static bool IsPath(string path) {
        return path.Length > 0 && path[0] == PathStart;
    }

    /// <summary>
    /// The path that names an object of a thrown thing in every game, with where the thing is now (see
    /// <see cref="PathOf(string, ThrownMark, GameObject, ThrownPlace)"/>).
    /// </summary>
    /// <param name="gameObject">The object.</param>
    /// <returns>The path, or null if the object is not a part of a named thrown thing.</returns>
    public static string? PathOf(GameObject gameObject) {
        return TryGetMark(gameObject, out var mark) ? PathOf(mark.Key!, mark, gameObject, PlaceOf(mark)) : null;
    }

    /// <summary>
    /// The path that names an object of a thrown thing in every game: the key of the thing, the path of the object
    /// under it, and where the thing was and how it moved as the player did what the path goes with, which its twin is
    /// put at first (see <see cref="PutAt"/>). Taken apart again by <see cref="Find"/>.
    /// </summary>
    /// <param name="key">The key of the thing, as it was then.</param>
    /// <param name="mark">The mark of the thing.</param>
    /// <param name="part">The object of the thing.</param>
    /// <param name="place">Where the thing was then (see <see cref="PlaceOf"/>).</param>
    public static string PathOf(string key, ThrownMark mark, GameObject part, ThrownPlace place) {
        return $"{PathStart}{key}{PartStart}{PartsComponent.GetPath(mark.transform, part.transform)}{PlaceStart}" +
               $"{Hex(place.Position.x)}{Hex(place.Position.y)}{Hex(place.Velocity.x)}{Hex(place.Velocity.y)}";
    }

    /// <summary>
    /// Where a thrown thing is and how it moves now.
    /// </summary>
    public static ThrownPlace PlaceOf(ThrownMark mark) {
        var body = mark.GetComponent<Rigidbody2D>();
        return new ThrownPlace(mark.transform.position, body != null ? body.linearVelocity : Vector2.zero);
    }

    /// <summary>
    /// Finds the object of a thrown thing that a path names (see <see cref="PathOf(string, ThrownMark, GameObject,
    /// ThrownPlace)"/>).
    /// </summary>
    /// <returns>The object, or null if no thrown thing here has that name.</returns>
    public static GameObject? Find(string path) {
        var partStart = path.IndexOf(PartStart);
        if (!IsPath(path) || partStart < 0 || !ByKey.TryGetValue(path.Substring(1, partStart - 1), out var mark) ||
            mark == null) {
            return null;
        }

        var placeStart = path.LastIndexOf(PlaceStart);
        var partEnd = placeStart > partStart && path.Length - placeStart - 1 == PlaceLength ? placeStart : path.Length;
        var part = path.Substring(partStart + 1, partEnd - partStart - 1);
        if (part.Length == 0) {
            return mark.gameObject;
        }

        var found = mark.transform.Find(part);
        return found == null ? null : found.gameObject;
    }

    /// <summary>
    /// Puts a thrown thing where a path says it was in the game it came from, moving as it moved there (see
    /// <see cref="PathOf(string, ThrownMark, GameObject, ThrownPlace)"/>). A thing that something holds still, like a
    /// javelin stuck in a wall, keeps how it moves.
    /// </summary>
    /// <param name="mark">The mark of the thing.</param>
    /// <param name="path">The path.</param>
    public static void PutAt(ThrownMark mark, string path) {
        var placeStart = path.LastIndexOf(PlaceStart);
        if (placeStart < 0 || path.Length - placeStart - 1 != PlaceLength ||
            !TryReadHex(path, placeStart + 1, out var x) || !TryReadHex(path, placeStart + 9, out var y) ||
            !TryReadHex(path, placeStart + 17, out var velocityX) ||
            !TryReadHex(path, placeStart + 25, out var velocityY)) {
            return;
        }

        var transform = mark.transform;
        transform.position = new Vector3(x, y, transform.position.z);

        var body = mark.GetComponent<Rigidbody2D>();
        if (body == null) {
            return;
        }

        body.position = new Vector2(x, y);
        if (body.bodyType == RigidbodyType2D.Dynamic) {
            body.linearVelocity = new Vector2(velocityX, velocityY);
        }
    }

    /// <summary>
    /// Writes a number as the eight hexadecimal digits of its bits.
    /// </summary>
    private static string Hex(float number) {
        return BitConverter.SingleToInt32Bits(number).ToString("x8");
    }

    /// <summary>
    /// Reads a number that <see cref="Hex"/> wrote, from a place in a text.
    /// </summary>
    private static bool TryReadHex(string text, int start, out float number) {
        number = 0f;
        if (!int.TryParse(
                text.Substring(start, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bits
            )) {
            return false;
        }

        number = BitConverter.Int32BitsToSingle(bits);
        return !float.IsNaN(number) && !float.IsInfinity(number);
    }

    /// <summary>
    /// Has a thrown thing go after a player from now on: the player who struck it, touched it or was hurt by it.
    /// </summary>
    /// <param name="mark">The mark of the thing.</param>
    /// <param name="player">The player, or null for no one.</param>
    public static void TurnTo(ThrownMark mark, GameObject? player) {
        mark.Target = player;
        GamePatcher.AimThrownAt(mark.Owners, player);
    }

    /// <summary>
    /// Has the things that the next action of a creature that this game runs throws named, for the throw to be sent
    /// under those names (see <see cref="WriteThrows"/>).
    /// </summary>
    /// <param name="action">The action that throws.</param>
    /// <param name="prefab">What it throws.</param>
    /// <param name="target">The player the creature goes after.</param>
    /// <returns>What stops the naming once the throw is over.</returns>
    public static IDisposable ExpectHostThrow(FsmStateAction action, GameObject prefab, GameObject? target) {
        var frame = Time.frameCount;
        if (!HostThrows.TryGetValue(action, out var thrown) || thrown.Frame != frame) {
            HostThrows.Remove(action);
            thrown = new HostThrow(frame, CodeOf?.Invoke(target) ?? NoPlayer, prefab.name);
            HostThrows.Add(action, thrown);
        }

        return Begin(new Pending(prefab, target, () => {
            var number = ++_lastNumber;
            thrown.Numbers.Add(number);
            return $"{Prefix}.{number}";
        }, thrown.Marks));
    }

    /// <summary>
    /// Has what a named thrown thing throws in turn named after it, which both games do alike.
    /// </summary>
    /// <param name="parent">The mark of the thing that throws.</param>
    /// <param name="prefab">What it throws.</param>
    /// <returns>What stops the naming once the throw is over.</returns>
    public static IDisposable ExpectChild(ThrownMark parent, GameObject prefab) {
        return Begin(new Pending(prefab, parent.Target, () => parent.NextChildKey(prefab.name), null));
    }

    /// <summary>
    /// Has the things that a replayed throw of the creature makes here named as the game that runs it named them.
    /// </summary>
    /// <param name="prefab">What the creature throws.</param>
    /// <param name="throws">The names, as they were read with the throw (see <see cref="ReadThrows"/>).</param>
    /// <returns>What stops the naming once the throw is over.</returns>
    public static IDisposable Expect(GameObject prefab, Throws throws) {
        // A copy that throws something else than the creature did, from a variable it was never given, names nothing
        if (throws.Count == 0 || throws.PrefabName != prefab.name) {
            return Nothing.Instance;
        }

        var target = FigureOf?.Invoke(throws.Target, throws.Thrower);
        var next = 0;
        return Begin(new Pending(prefab, target, () => next < throws.Count
            ? $"{throws.Prefix}.{throws.First + next++}"
            : null, null));
    }

    /// <summary>
    /// Begins the names of what an action of a creature that this game runs throws on a clock in a run of its state,
    /// to be sent with the run (see <see cref="WriteSeries"/>).
    /// </summary>
    /// <param name="target">The player the creature goes after.</param>
    public static Series BeginSeries(GameObject? target) {
        return new Series(Prefix, ++_lastNumber, CodeOf?.Invoke(target) ?? NoPlayer, target);
    }

    /// <summary>
    /// Writes the names of a run for <see cref="ReadSeries"/> in the other game: whether there are any, and their
    /// prefix, the number of the run and the player they go after.
    /// </summary>
    /// <param name="data">The data that goes with the action.</param>
    /// <param name="series">The names, or null if the run throws nothing that is named.</param>
    public static void WriteSeries(EntityNetworkData data, Series? series) {
        data.Packet.Write(series != null);
        if (series == null) {
            return;
        }

        data.Packet.Write(series.Prefix);
        data.Packet.Write(series.Number);
        data.Packet.Write(series.TargetCode);
    }

    /// <summary>
    /// Reads what <see cref="WriteSeries"/> wrote.
    /// </summary>
    /// <param name="data">The data that came with the action.</param>
    /// <returns>The names, or null if the run throws nothing that is named.</returns>
    public static Series? ReadSeries(EntityNetworkData data) {
        if (!data.Packet.ReadBool()) {
            return null;
        }

        var prefix = data.Packet.ReadString();
        var number = data.Packet.ReadInt();
        var targetCode = data.Packet.ReadUShort();
        return new Series(prefix, number, targetCode, FigureOf?.Invoke(targetCode, data.SenderId));
    }

    /// <summary>
    /// Has the next thing of a prefab that comes out of the pool named with the next name of a run.
    /// </summary>
    /// <param name="series">The names of the run.</param>
    /// <param name="prefab">What the run throws.</param>
    /// <returns>What stops the naming once the thing is out.</returns>
    public static IDisposable ExpectSeries(Series series, GameObject prefab) {
        return Begin(new Pending(prefab, series.Target, series.NextKey, null));
    }

    /// <summary>
    /// What a replayed throw of a creature makes here: what its action names here, unless the creature threw something
    /// else. An action can throw what a variable holds, which another action of the creature fills just before - the
    /// variant of the creature's spit or shot picked for it - and which this game's copy of the creature, which does
    /// not run, never fills: the copy threw nothing then, or a thing it had thrown before. What the creature threw is
    /// then looked for by its name among what its state machine names.
    /// </summary>
    /// <param name="action">The replayed action.</param>
    /// <param name="named">What the action names here.</param>
    /// <param name="throws">The names that came with the throw (see <see cref="ReadThrows"/>).</param>
    /// <returns>What to throw, or null for nothing.</returns>
    public static GameObject? PrefabToThrow(FsmStateAction action, GameObject? named, Throws throws) {
        if (throws.PrefabName.Length == 0 || named != null && named.name == throws.PrefabName) {
            return named;
        }

        // Looked for once per state machine and name, since what it holds does not change, and again if what was found
        // has been destroyed since
        var known = FoundByName.GetOrCreateValue(action.Fsm);
        if (!known.TryGetValue(throws.PrefabName, out var found) || found is not null && found == null) {
            found = FindPrefab(action.Fsm, throws.PrefabName);
            known[throws.PrefabName] = found;

            var thrower = action.Fsm.GameObject;
            var creature = thrower == null ? "-" : thrower.name;
            if (found != null && SaidFoundByName.Add($"{creature}|{throws.PrefabName}")) {
                Logger.Info(
                    $"[Thrown] The copy of '{creature}' held {(named == null ? "nothing" : $"'{named.name}'")} " +
                    $"where it threw '{throws.PrefabName}', so that was found by its name"
                );
            }
        }

        // Nothing of that name: a prefab of another name stands in for it, but not a thing of the room or one thrown
        // before, which this game would make a clone of, as whatever it has become since
        return found != null ? found : named != null && !named.scene.IsValid() ? named : null;
    }

    /// <summary>
    /// What <see cref="PrefabToThrow"/> found by its name in each state machine, or null where it found nothing.
    /// </summary>
    private static readonly ConditionalWeakTable<HutongGames.PlayMaker.Fsm, Dictionary<string, GameObject?>>
        FoundByName = new();

    /// <summary>
    /// The creatures and what they threw that <see cref="PrefabToThrow"/> found by its name already, each said once.
    /// </summary>
    private static readonly HashSet<string> SaidFoundByName = [];

    /// <summary>
    /// The fields of each kind of action that can hold a prefab, found once per kind.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> PrefabFields = new();

    /// <summary>
    /// A prefab of the given name that a state machine holds, in one of its variables or in a field of one of its
    /// actions, or null if it holds none.
    /// </summary>
    /// <param name="fsm">The state machine.</param>
    /// <param name="name">The name of the prefab.</param>
    private static GameObject? FindPrefab(HutongGames.PlayMaker.Fsm fsm, string name) {
        foreach (var variable in fsm.Variables.GameObjectVariables) {
            if (IsPrefabNamed(variable.Value, name)) {
                return variable.Value;
            }
        }

        foreach (var state in fsm.States) {
            foreach (var action in state.Actions) {
                foreach (var field in FieldsHoldingPrefabs(action.GetType())) {
                    switch (field.GetValue(action)) {
                        case FsmGameObject one when IsPrefabNamed(one.Value, name):
                            return one.Value;
                        case GameObject gameObject when IsPrefabNamed(gameObject, name):
                            return gameObject;
                        case FsmGameObject[] many:
                            foreach (var item in many) {
                                if (item != null && IsPrefabNamed(item.Value, name)) {
                                    return item.Value;
                                }
                            }

                            break;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The fields of a kind of action that can hold a prefab.
    /// </summary>
    private static FieldInfo[] FieldsHoldingPrefabs(Type type) {
        if (!PrefabFields.TryGetValue(type, out var fields)) {
            fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(field => field.FieldType == typeof(FsmGameObject) ||
                                field.FieldType == typeof(FsmGameObject[]) ||
                                field.FieldType == typeof(GameObject))
                .ToArray();
            PrefabFields[type] = fields;
        }

        return fields;
    }

    /// <summary>
    /// Whether an object is a prefab of the given name, rather than something in a scene.
    /// </summary>
    private static bool IsPrefabNamed(GameObject? gameObject, string name) {
        return gameObject != null && !gameObject.scene.IsValid() && gameObject.name == name;
    }

    /// <summary>
    /// Writes what the action of a creature that this game runs threw, under which names and after which player, for
    /// <see cref="ReadThrows"/> in the other game: its prefix, the number of the first, how many, the player, and what
    /// was thrown.
    /// </summary>
    /// <param name="data">The data that goes with the action.</param>
    /// <param name="action">The action.</param>
    public static void WriteThrows(EntityNetworkData data, FsmStateAction action) {
        HostThrows.TryGetValue(action, out var thrown);
        HostThrows.Remove(action);

        // The names go as the first and how many, which only says them all when they follow one another
        var hasThrown = thrown != null && thrown.Frame == Time.frameCount && thrown.Numbers.Count > 0 &&
                        thrown.Numbers.Count <= byte.MaxValue &&
                        thrown.Numbers[thrown.Numbers.Count - 1] - thrown.Numbers[0] == thrown.Numbers.Count - 1;

        data.Packet.Write(Prefix);
        data.Packet.Write(hasThrown ? thrown!.Numbers[0] : 0);
        data.Packet.Write((byte) (hasThrown ? thrown!.Numbers.Count : 0));
        data.Packet.Write(hasThrown ? thrown!.Target : NoPlayer);
        data.Packet.Write(hasThrown ? thrown!.PrefabName : "");

        // What goes without its names keeps none here either: what it rolls and what is done to it would go by names
        // that the other game does not know
        if (thrown != null && !hasThrown) {
            ForgetAll(thrown);
        }
    }

    /// <summary>
    /// Reads what <see cref="WriteThrows"/> wrote.
    /// </summary>
    /// <param name="data">The data that came with the action.</param>
    public static Throws ReadThrows(EntityNetworkData data) {
        return new Throws(
            data.Packet.ReadString(), data.Packet.ReadInt(), data.Packet.ReadByte(), data.Packet.ReadUShort(),
            data.Packet.ReadString(), data.SenderId
        );
    }

    /// <summary>
    /// Takes the names off what the action of a creature that this game runs threw, when what it threw does not go to
    /// the other game after all: something that turned out to be a creature, which comes there as one.
    /// </summary>
    /// <param name="action">The action.</param>
    public static void DropHostThrows(FsmStateAction action) {
        if (!HostThrows.TryGetValue(action, out var thrown)) {
            return;
        }

        HostThrows.Remove(action);
        ForgetAll(thrown);
    }

    /// <summary>
    /// Takes the names off everything of a throw.
    /// </summary>
    private static void ForgetAll(HostThrow thrown) {
        foreach (var mark in thrown.Marks) {
            if (mark != null) {
                Forget(mark);
            }
        }
    }

    /// <summary>
    /// Gives a thing that is coming out of the pool the name waiting for it, before it is switched on and its state
    /// machines begin - and roll.
    /// </summary>
    /// <param name="gameObject">The thing.</param>
    public static void OnActivating(GameObject gameObject) {
        if (_pending is not { } pending || gameObject == null || !pending.Matches(gameObject) ||
            pending.NextKey() is not { } key) {
            return;
        }

        var mark = gameObject.GetComponent<ThrownMark>();
        if (mark == null) {
            mark = gameObject.AddComponent<ThrownMark>();
        } else if (mark.Key != null) {
            Forget(mark);
        }

        if (ByKey.TryGetValue(key, out var other) && other != null && other != mark) {
            Forget(other);
        }

        mark.Begin(key, pending.Target);
        ByKey[key] = mark;
        pending.Marks?.Add(mark);
        GamePatcher.AimThrownAt(mark.Owners, pending.Target);
    }

    /// <summary>
    /// Takes the name off a thing that goes back into the pool or is gone.
    /// </summary>
    /// <param name="mark">The mark of the thing.</param>
    public static void Forget(ThrownMark mark) {
        if (mark.Key is not { } key) {
            return;
        }

        if (ByKey.TryGetValue(key, out var named) && named == mark) {
            ByKey.Remove(key);
        }

        mark.End();
        if (mark != null) {
            GamePatcher.AimThrownAt(mark.Owners, null);
        }
    }

    /// <summary>
    /// Does something with the dice of a named thing, and hands the game back its own.
    /// </summary>
    /// <param name="seed">The dice of the thing for this roll.</param>
    /// <param name="action">What rolls.</param>
    public static void Rolling(int seed, Action action) {
        var own = Random.state;
        Random.InitState(seed);
        try {
            action();
        } finally {
            Random.state = own;
        }
    }

    /// <summary>
    /// The dice for a roll of a thrown thing: from its key, what rolls and how many times it rolled before (32-bit
    /// FNV-1a, which is the same in every game).
    /// </summary>
    public static int Seed(string key, int roller, int times) {
        unchecked {
            var hash = 2166136261u;
            foreach (var character in key) {
                hash = (hash ^ character) * 16777619u;
            }

            hash = (hash ^ (uint) roller) * 16777619u;
            hash = (hash ^ (uint) times) * 16777619u;
            return (int) hash;
        }
    }

    /// <summary>
    /// Has names wait for the next things that come out of the pool, until what is returned is disposed of.
    /// </summary>
    private static IDisposable Begin(Pending pending) {
        var last = _pending;
        _pending = pending;
        return new Ending(() => _pending = last);
    }

    /// <summary>
    /// Where a thrown thing was and how it moved.
    /// </summary>
    public readonly record struct ThrownPlace(Vector2 Position, Vector2 Velocity);

    /// <summary>
    /// The names of a throw that came with it from the game that runs the creature.
    /// </summary>
    /// <param name="Prefix">What the keys start with.</param>
    /// <param name="First">The number of the first.</param>
    /// <param name="Count">How many.</param>
    /// <param name="Target">Who they go after (see <see cref="CodeOf"/>).</param>
    /// <param name="PrefabName">The name of what was thrown.</param>
    /// <param name="Thrower">The player whose game threw them.</param>
    public readonly record struct Throws(
        string Prefix,
        int First,
        int Count,
        ushort Target,
        string PrefabName,
        ushort Thrower
    );

    /// <summary>
    /// The names of what an action of a creature throws on a clock in a run of its state: a spray of spit, a cloud of
    /// bubbles, bombs along a charge. The game that runs the creature takes a number for the run as the state begins
    /// and sends it with the run, and each game names the things in the order the action throws them, which the dice
    /// the two share for the run keep the same (EntityFsmActions).
    /// </summary>
    /// <param name="prefix">What the keys start with: those of the game that runs the creature.</param>
    /// <param name="number">The number of the run.</param>
    /// <param name="targetCode">Who the things go after, as a throw says it (see <see cref="CodeOf"/>).</param>
    /// <param name="target">The player here that the things go after, or null for no one.</param>
    public sealed class Series(string prefix, int number, ushort targetCode, GameObject? target) {
        public readonly string Prefix = prefix;
        public readonly int Number = number;
        public readonly ushort TargetCode = targetCode;
        public readonly GameObject? Target = target;

        /// <summary>
        /// How many things the run named so far.
        /// </summary>
        private int _count;

        /// <summary>
        /// The name of the next thing the run throws.
        /// </summary>
        public string NextKey() {
            return $"{Prefix}.{Number}.{_count++}";
        }
    }

    /// <summary>
    /// What an action of a creature that this game runs threw in one frame.
    /// </summary>
    private sealed class HostThrow(int frame, ushort target, string prefabName) {
        public readonly int Frame = frame;
        public readonly ushort Target = target;
        public readonly string PrefabName = prefabName;
        public readonly List<int> Numbers = [];
        public readonly List<ThrownMark> Marks = [];
    }

    /// <summary>
    /// Names that wait for the things of one prefab that come out of the pool next.
    /// </summary>
    private sealed class Pending(
        GameObject prefab,
        GameObject? target,
        Func<string?> nextKey,
        List<ThrownMark>? marks
    ) {
        private readonly string _prefabName = prefab.name;
        public readonly GameObject? Target = target;
        public readonly List<ThrownMark>? Marks = marks;

        /// <summary>
        /// Whether a thing coming out of the pool is one of the prefab, rather than something else that comes out of it
        /// meanwhile. The pool makes its things as copies of the prefab, named after it.
        /// </summary>
        public bool Matches(GameObject gameObject) {
            var name = gameObject.name;
            return name == _prefabName || name.StartsWith(_prefabName + "(Clone)", StringComparison.Ordinal);
        }

        /// <summary>
        /// The next name, or null if there are no more.
        /// </summary>
        public string? NextKey() {
            return nextKey();
        }
    }

    /// <summary>
    /// Does something once it is disposed of.
    /// </summary>
    private sealed class Ending(Action end) : IDisposable {
        private Action? _end = end;

        public void Dispose() {
            _end?.Invoke();
            _end = null;
        }
    }

    /// <summary>
    /// Does nothing once it is disposed of.
    /// </summary>
    private sealed class Nothing : IDisposable {
        public static readonly Nothing Instance = new();

        public void Dispose() {
        }
    }
}

/// <summary>
/// The name of a thrown thing (see <see cref="ThrownThings"/>), on the thing, with how many times it threw something in
/// turn and rolled, which the dice of its next throw and roll go by. A thing of the pool keeps its mark when it goes
/// back, and has its name taken off as it is switched off.
/// </summary>
internal sealed class ThrownMark : MonoBehaviour {
    /// <summary>
    /// What a bounce of the thing is counted under among its rolls.
    /// </summary>
    private const int BounceRoller = 0x0b0c0e;

    /// <summary>
    /// The key of the thing, or null while it has none.
    /// </summary>
    public string? Key { get; private set; }

    /// <summary>
    /// The player the thing goes after, or null for no one.
    /// </summary>
    public GameObject? Target { get; set; }

    /// <summary>
    /// How many of each kind of thing it threw in turn, by name.
    /// </summary>
    private readonly Dictionary<string, int> _thrown = new();

    /// <summary>
    /// How many times each of its actions rolled, by what the action is (see SharedDice).
    /// </summary>
    private readonly Dictionary<int, int> _rolls = new();

    /// <summary>
    /// The parts of the thing that are asked whom they go after, found once: a thing of the pool keeps its parts.
    /// </summary>
    private int[]? _owners;

    /// <summary>
    /// The parts of the thing that are asked whom they go after, by instance ID (see GamePatcher.AimThrownAt).
    /// </summary>
    public int[] Owners => _owners ??= GamePatcher.ThrownTargetOwners(gameObject);

    /// <summary>
    /// Gives the thing a name.
    /// </summary>
    public void Begin(string key, GameObject? target) {
        Key = key;
        Target = target;
        _thrown.Clear();
        _rolls.Clear();
    }

    /// <summary>
    /// Takes the name off the thing.
    /// </summary>
    public void End() {
        Key = null;
        Target = null;
    }

    /// <summary>
    /// The name of the next thing of a kind that this thing throws.
    /// </summary>
    public string NextChildKey(string prefabName) {
        _thrown.TryGetValue(prefabName, out var times);
        _thrown[prefabName] = times + 1;
        return $"{Key}/{prefabName}#{times}";
    }

    /// <summary>
    /// The dice for the next roll of something of the thing.
    /// </summary>
    /// <param name="roller">What rolls, the same in every game.</param>
    public int NextRoll(int roller) {
        _rolls.TryGetValue(roller, out var times);
        _rolls[roller] = times + 1;
        return ThrownThings.Seed(Key ?? "", roller, times);
    }

    /// <summary>
    /// The dice for the next bounce of the thing.
    /// </summary>
    public int NextBounce() {
        return NextRoll(BounceRoller);
    }

    private void OnDisable() {
        ThrownThings.Forget(this);
    }

    private void OnDestroy() {
        ThrownThings.Forget(this);
    }
}
