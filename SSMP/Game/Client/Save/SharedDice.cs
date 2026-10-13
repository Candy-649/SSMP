using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Random = UnityEngine.Random;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The dice of the game, carried with something that one player set off, so that the other game throws the same numbers
/// when it does the same thing. The game that set it off decides: a trap that its own player walked into, a bell that
/// its own player struck.
///
/// A mine that its player walks near leaps out, turns, flips and lands a little higher or lower, all by the dice, and
/// each game rolled its own: the mine that went off beside one player lay somewhere else for the other. What goes with
/// it is the state of <see cref="Random"/> as it was set off, and again as each action of a state machine of the room
/// that rolls started. Everything such an action rolls comes out the same from the same state, however many numbers
/// and of whatever kind. The start of each action is written down, and not only the start of the whole, because the
/// game rolls for other things in between that the two games do not do alike: a sound that was played a moment ago is
/// not played again, and the pitch it would have rolled is not rolled.
///
/// Each roll is written down under the action that made it: its object, state machine, state and place in the state.
/// The other game gives an action the first roll written down under the same action that no action took yet, wherever
/// that is, and lets an action with none roll its own. So a trap that another thing set off does not take the rolls of
/// that thing when it went off over there already by itself, a state machine that goes another way over there does not
/// shift every roll after, and the same actions started in another order there still take their own rolls: a part that
/// one game started in the middle of a state, as it was switched on again, and the other after it, as it was switched
/// on for the first time.
///
/// A shuffle is another thing: its dice don't go into what is done, but into a list that it shuffles as the list
/// stands, and each game came to its list by itself. The same dice put two lists that stood in other orders into other
/// orders, and what a controller picks from such a list in turn came out otherwise in each game for good once one game
/// had shuffled the list once more than the other (USER 10-13: 骰子先供给给一个列表，还要再客机那边后处理的就不行). So
/// what goes with a shuffle is not its dice but the order it came to, thing by thing, each known by where it is in the
/// scene or by its value. The other game puts the same things of its own list in that order and shuffles nothing (USER
/// 10-13: 为什么不直接同步列表的结果), and goes on from the dice as the shuffle left them, for what is rolled after it
/// in the same moment. A list that doesn't hold the same things over there is shuffled there by itself, with a line in
/// the log.
///
/// It covers only what happens at once, in the same call: what the thing rolls later, after a wait, each game rolls
/// again. This is no seed shared by the room. That was turned down, since each game rolls at its own frame rate and for
/// its own player, so the same seed soon gives different numbers. Here the dice are lent for the one thing and handed
/// back.
///
/// Only an object that is given a name to roll under rolls the same later too, as the things that creatures throw do
/// (<see cref="RollByName"/>): each roll of each of its actions comes from the name and how many times that action
/// rolled under it before, whatever the frame rate. A part that a controller of a fight sends off is given one by the
/// word that tells the other game, for as long as it goes.
/// </summary>
internal static class SharedDice {
    /// <summary>
    /// How many numbers the state of <see cref="Random"/> is.
    /// </summary>
    private const int StateSize = 4;

    /// <summary>
    /// How many numbers come before what each record of the dice holds: what it holds, for which action, and how many
    /// numbers.
    /// </summary>
    private const int RecordHeadSize = 3;

    /// <summary>
    /// What a record of the dice holds.
    /// </summary>
    private enum RecordKind {
        /// <summary>
        /// The state of <see cref="Random"/> as an action that rolls started.
        /// </summary>
        Roll = 0,

        /// <summary>
        /// The order that a shuffle put the part of its list in that it goes through: where that part starts in the
        /// list, the state of <see cref="Random"/> after the shuffle, then the number of each thing in it in turn (see
        /// <see cref="KeyOf"/>).
        /// </summary>
        Order = 1
    }

    /// <summary>
    /// The most numbers that go with one thing, which keeps what is sent and what is read within bounds.
    /// </summary>
    private const int MaxNumbers = 2048;

    /// <summary>
    /// The actions of state machines that roll as they start, by name. Each game hooks them to write the dice down, or
    /// to take them from what the partner set off.
    /// </summary>
    private static readonly string[] RollingActionNames = [
        "RandomFloat", "RandomFloatV2", "RandomFloatEither", "FloatAddRandom", "RandomInt", "RandomBool",
        "RandomBoolPercent", "RandomVector2", "RandomVector3", "Vector2RandomValue", "RandomlyFlipFloat",
        "RandomlyFlipScale", "RandomlyFlipYScale", "SetRandomRotation", "TranslateRandom", "WaitRandom", "RandomWait",
        "SendRandomEvent", "SendRandomEventV2", "SendRandomEventV3", "SendRandomEventV3ActiveBool",
        "SendRandomEventV4", "SendRandomEventFair", "GetRandomChild", "GetRandomObject", "SelectRandomGameObject",
        "SelectRandomGameObjectV2", "SelectRandomString", "SelectRandomInt", "SelectRandomFloat", "SelectRandomVector3",
        "SelectRandomColor", "ArrayGetRandom", "ArrayShuffle", "SpawnRandomObjects", "SpawnRandomObjectsV2",
        "SpawnRandomObjectsVelocity", "SpawnRandomObjectsRadial", "SpawnRandomObjectsRadialV2", "CreateObjectsRandom",
        "SpawnFromPool", "SpawnFromPoolV2", "CreatePoolObjects", "FlingObject", "FlingObjects", "FlingObjectsV2",
        "FlingObjectsFromGlobalPool", "FlingObjectsFromGlobalPoolV2", "FlingObjectsFromGlobalPoolV3",
        "FlingObjectsFromGlobalPoolVel", "AudioPlayerOneShot", "AudioPlayerOneShotSingle", "AudioPlayRandom",
        "AudioPlayRandomSingle", "PlayRandomSound", "PlayRandomAnimation", "Tk2dSpriteSetIdRandom"
    ];

    /// <summary>
    /// What is being written down, for each thing being set off here that goes to the partner. More than one at once
    /// when one sets off another, like a tink that sets off a trap.
    /// </summary>
    private static readonly List<List<int>> Recordings = [];

    /// <summary>
    /// The dice of the thing that the partner set off and this game is doing now, or null.
    /// </summary>
    private static IReadOnlyList<int>? _thrown;

    /// <summary>
    /// The records of <see cref="_thrown"/>: what each holds, for which action, and where in the dice and how many
    /// numbers.
    /// </summary>
    private static (RecordKind Kind, int Action, int Start, int Length)[]? _records;

    /// <summary>
    /// Which of <see cref="_records"/> an action here has taken, so that each is taken once.
    /// </summary>
    private static bool[]? _taken;

    /// <summary>
    /// The objects of the room that roll under a name rather than with the game's own dice, the same in every game,
    /// with how many times each of their actions rolled under it (see <see cref="RollByName"/>).
    /// </summary>
    private static readonly Dictionary<GameObject, (string Name, Dictionary<int, int> Times)> NamedRollers = new();

    /// <summary>
    /// The state machine of an entity that rolls with the dice of what is being set off, or null for none. Otherwise
    /// only the room's own state machines do: an entity rolls where the scene host runs it, except for something that
    /// a strike or touch of the other player's has it do (see <see cref="Entity.Entity.PlayHere"/>).
    /// </summary>
    private static HutongGames.PlayMaker.Fsm? _entityFsm;

    /// <summary>
    /// Gets the hooks on the actions that roll as they start, for the hit replays to put in place.
    /// </summary>
    /// <returns>Each method and what replaces it.</returns>
    public static IEnumerable<(MethodInfo Method, Delegate Detour)> GetHooks() {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var name in RollingActionNames) {
            var type = GamePatcher.GetFsmActionTypeByName(name);
            var onEnter = type?.GetMethod("OnEnter", flags);
            if (type == null || onEnter == null || onEnter.DeclaringType != type) {
                Logger.Debug($"No action {name} that rolls as it starts, so its dice are not shared");
                continue;
            }

            yield return (onEnter, new Action<Action<FsmStateAction>, FsmStateAction>(OnRollingActionEnter));
        }
    }

    /// <summary>
    /// Sets off something here that goes to the partner, writing the dice down as it goes.
    /// </summary>
    /// <param name="action">What is set off.</param>
    /// <param name="entityFsm">The state machine of an entity that rolls with these dice too, or null for none.</param>
    /// <returns>The dice, for <see cref="Throw"/> in the partner's game.</returns>
    public static int[] Record(Action action, HutongGames.PlayMaker.Fsm? entityFsm = null) {
        var recording = new List<int>(TakeState());
        var lastEntityFsm = _entityFsm;
        Recordings.Add(recording);
        _entityFsm = entityFsm ?? lastEntityFsm;
        try {
            action();
        } finally {
            Recordings.Remove(recording);
            _entityFsm = lastEntityFsm;
        }

        return recording.ToArray();
    }

    /// <summary>
    /// Does what the partner set off with the dice their game had as they set it off, and hands this game back its own.
    /// </summary>
    /// <param name="dice">The dice, or null or broken ones for none, which leaves the game to roll its own.</param>
    /// <param name="action">What the partner set off.</param>
    /// <param name="entityFsm">The state machine of an entity that rolls with these dice too, or null for none.</param>
    public static void Throw(IReadOnlyList<int>? dice, Action action, HutongGames.PlayMaker.Fsm? entityFsm = null) {
        if (dice == null || ReadRecords(dice) is not { } records) {
            action();
            return;
        }

        var own = Random.state;
        var (lastThrown, lastRecords, lastTaken, lastEntityFsm) = (_thrown, _records, _taken, _entityFsm);
        Random.state = ToState(dice, 0);
        (_thrown, _records, _taken, _entityFsm) = (
            dice, records, new bool[records.Length], entityFsm ?? lastEntityFsm
        );
        try {
            action();
        } finally {
            (_thrown, _records, _taken, _entityFsm) = (lastThrown, lastRecords, lastTaken, lastEntityFsm);
            Random.state = own;
        }
    }

    /// <summary>
    /// Reads the records of dice: after the state as the thing was set off, each record says what it holds, for which
    /// action and how many numbers, and then holds them.
    /// </summary>
    /// <returns>The records, or null for broken dice.</returns>
    private static (RecordKind Kind, int Action, int Start, int Length)[]? ReadRecords(IReadOnlyList<int> dice) {
        if (dice.Count < StateSize) {
            return null;
        }

        var records = new List<(RecordKind Kind, int Action, int Start, int Length)>();
        for (var at = StateSize; at < dice.Count;) {
            if (dice.Count - at < RecordHeadSize) {
                return null;
            }

            // Dice of another kind of build are left whole, rather than read in part
            var kind = (RecordKind) dice[at];
            var length = dice[at + 2];
            if (kind != RecordKind.Roll && kind != RecordKind.Order || length < 0 ||
                length > dice.Count - at - RecordHeadSize) {
                return null;
            }

            records.Add((kind, dice[at + 1], at + RecordHeadSize, length));
            at += RecordHeadSize + length;
        }

        return records.ToArray();
    }

    /// <summary>
    /// Takes the first record of the dice being thrown here that holds the given kind for the given action and that no
    /// action took yet.
    /// </summary>
    /// <returns>Where what it holds starts in the dice and how many numbers, or null for none.</returns>
    private static (int Start, int Length)? TakeRecord(RecordKind kind, int action) {
        if (_records is not { } records || _taken is not { } taken) {
            return null;
        }

        for (var index = 0; index < records.Length; index++) {
            var record = records[index];
            if (taken[index] || record.Kind != kind || record.Action != action) {
                continue;
            }

            taken[index] = true;
            return (record.Start, record.Length);
        }

        return null;
    }

    /// <summary>
    /// Has the state machines of an object of the room roll under a name from now on, rather than with the game's own
    /// dice: each roll of each action from the name and how many times that action rolled under it before, so that
    /// every game that gives the object the same name rolls the same, at whatever time and in whatever order its
    /// actions come, as the things that creatures throw do (see ThrownThings). A part that a controller of a fight
    /// sends off is given one by the word that tells the other game (see CoopSave.ShownParts), and keeps it until it is
    /// given another or the room is left.
    /// </summary>
    /// <param name="gameObject">The object.</param>
    /// <param name="name">The name, the same in every game.</param>
    public static void RollByName(GameObject gameObject, string name) {
        NamedRollers[gameObject] = (name, new Dictionary<int, int>());
    }

    /// <summary>
    /// Has every object that rolls under a name roll with the game's own dice again, as the room is left.
    /// </summary>
    public static void ForgetRollNames() {
        NamedRollers.Clear();
    }

    /// <summary>
    /// Adds dice to the end of what an update carries.
    /// </summary>
    /// <param name="data">What the update carries.</param>
    /// <param name="dice">The dice.</param>
    /// <returns>Both.</returns>
    public static byte[] Append(byte[] data, int[] dice) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(data);
        Write(writer, dice);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Writes dice.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="dice">The dice.</param>
    public static void Write(BinaryWriter writer, int[] dice) {
        writer.Write(dice.Length);
        foreach (var number in dice) {
            writer.Write(number);
        }
    }

    /// <summary>
    /// Reads dice that <see cref="Append"/> or <see cref="Write"/> wrote, if what is left to read holds any.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The dice, or null if nothing is left to read.</returns>
    /// <exception cref="EndOfStreamException">What is left is not dice.</exception>
    public static int[]? Read(BinaryReader reader) {
        var stream = reader.BaseStream;
        if (stream.Length - stream.Position == 0) {
            return null;
        }

        var count = reader.ReadInt32();
        if (count is < 0 or > MaxNumbers || stream.Length - stream.Position < (long) count * sizeof(int)) {
            throw new EndOfStreamException($"Dice of {count} numbers do not fit what is left");
        }

        var dice = new int[count];
        for (var i = 0; i < count; i++) {
            dice[i] = reader.ReadInt32();
        }

        return dice;
    }

    /// <summary>
    /// Hook for the start of an action that rolls. While something set off here is written down, the dice as it starts
    /// are written down; while something the partner set off is done here, the action starts from the dice their game
    /// had as the same action started there.
    ///
    /// Only the actions of objects of the room count. A hit also moves the player who struck and the enemies around,
    /// whose state machines may roll in the same moment, and none of that is done again in the other game: the player
    /// is that game's own, and the enemies roll where the scene host runs them.
    /// </summary>
    private static void OnRollingActionEnter(Action<FsmStateAction> orig, FsmStateAction self) {
        // A thing that a creature threw rolls by its name, the same in every game (ThrownThings), whatever set it off
        if (ThrownThings.Count > 0 && self.Fsm?.FsmComponent is { } thrownFsm &&
            ThrownThings.TryGetMark(thrownFsm.gameObject, out var mark)) {
            ThrownThings.Rolling(mark.NextRoll(Identify(self, thrownFsm)), () => orig(self));
            return;
        }

        // So does an object of the room that was given a name to roll under
        if (NamedRollers.Count > 0 && self.Fsm?.FsmComponent is { } namedFsm && namedFsm != null &&
            NamedRollers.TryGetValue(namedFsm.gameObject, out var named)) {
            var roller = Identify(self, namedFsm);
            named.Times.TryGetValue(roller, out var times);
            named.Times[roller] = times + 1;
            ThrownThings.Rolling(ThrownThings.Seed(named.Name, roller, times), () => orig(self));
            return;
        }

        if (Recordings.Count == 0 && _thrown == null || self.Fsm?.FsmComponent is not { } fsm ||
            self.Fsm != _entityFsm && !CoopHits.IsRoomObject(fsm)) {
            orig(self);
            return;
        }

        var action = Identify(self, fsm);

        // A shuffle takes the order the partner's came to, rather than its dice
        if (self is ArrayShuffle shuffle) {
            if (_thrown is { } thrownOrder && TakeOrder(shuffle, thrownOrder, action)) {
                shuffle.Finish();
            } else {
                orig(self);
            }

            WriteOrder(shuffle, action);
            return;
        }

        if (_thrown is { } thrown && TakeRecord(RecordKind.Roll, action) is { Length: StateSize } roll) {
            Random.state = ToState(thrown, roll.Start);
        }

        if (Recordings.Count > 0) {
            var state = TakeState();
            foreach (var recording in Recordings) {
                if (recording.Count + RecordHeadSize + StateSize <= MaxNumbers) {
                    recording.Add((int) RecordKind.Roll);
                    recording.Add(action);
                    recording.Add(StateSize);
                    recording.AddRange(state);
                }
            }
        }

        orig(self);
    }

    /// <summary>
    /// Puts the list of a shuffle here in the first order written down under the same action that no shuffle took yet
    /// and whose things the list here holds, and takes that order, with the state of <see cref="Random"/> that the
    /// partner's shuffle left. The name of an object is no path, so another list of an object with the same name may
    /// have written down an order under the same action, which is passed over.
    /// </summary>
    /// <returns>Whether the list was put in an order of the partner's.</returns>
    private static bool TakeOrder(ArrayShuffle shuffle, IReadOnlyList<int> dice, int action) {
        if (_records is not { } records || _taken is not { } taken) {
            return false;
        }

        var hadOrder = false;
        for (var index = 0; index < records.Length; index++) {
            var record = records[index];
            if (taken[index] || record.Kind != RecordKind.Order || record.Action != action) {
                continue;
            }

            hadOrder = true;
            if (PutInOrder(shuffle, dice, record.Start, record.Length)) {
                taken[index] = true;
                Random.state = ToState(dice, record.Start + 1);
                return true;
            }
        }

        Logger.Info(
            $"A shuffle of '{shuffle.Fsm?.GameObjectName}' shuffles by itself, since " + (hadOrder
                ? "its list holds other things than the partner's"
                : "no order of the partner's came for it")
        );
        return false;
    }

    /// <summary>
    /// Puts the part of the list of a shuffle here that the shuffle goes through in the order that the partner's came
    /// to, thing by thing, rather than shuffling it.
    /// </summary>
    /// <param name="shuffle">The shuffle.</param>
    /// <param name="dice">The dice that the partner's order is in.</param>
    /// <param name="start">Where the order starts in the dice: where the part starts in the list, the state after the
    /// shuffle, then the number of each thing in turn (see <see cref="KeyOf"/>).</param>
    /// <param name="length">How many numbers the order is.</param>
    /// <returns>Whether that part of the list here holds the same things as the partner's, and so was put in order.
    /// </returns>
    private static bool PutInOrder(ArrayShuffle shuffle, IReadOnlyList<int> dice, int start, int length) {
        if (shuffle.array is not { } array || array.Values is not { } values || length < 1 + StateSize) {
            return false;
        }

        var (first, count) = ShuffledPart(shuffle, values.Length);
        if (dice[start] != first || length - 1 - StateSize != count) {
            return false;
        }

        var keys = new int[count];
        for (var i = 0; i < count; i++) {
            keys[i] = KeyOf(values[first + i]);
        }

        var used = new bool[count];
        var ordered = (object[]) values.Clone();
        for (var place = 0; place < count; place++) {
            var key = dice[start + 1 + StateSize + place];
            var found = -1;
            for (var i = 0; i < count; i++) {
                if (!used[i] && keys[i] == key) {
                    found = i;
                    break;
                }
            }

            if (found < 0) {
                return false;
            }

            used[found] = true;
            ordered[first + place] = values[first + found];
        }

        array.Values = ordered;
        return true;
    }

    /// <summary>
    /// Writes down the order that a shuffle here put its list in, with the state of <see cref="Random"/> that it left,
    /// for each thing being set off here that goes to the partner: what the game rolls after the shuffle in the same
    /// moment, outside the actions that are written down, then comes out the same there.
    /// </summary>
    private static void WriteOrder(ArrayShuffle shuffle, int action) {
        if (Recordings.Count == 0 || shuffle.array?.Values is not { } values) {
            return;
        }

        var (first, count) = ShuffledPart(shuffle, values.Length);
        var order = new int[RecordHeadSize + 1 + StateSize + count];
        order[0] = (int) RecordKind.Order;
        order[1] = action;
        order[2] = 1 + StateSize + count;
        order[3] = first;
        TakeState().CopyTo(order, RecordHeadSize + 1);
        for (var i = 0; i < count; i++) {
            order[RecordHeadSize + 1 + StateSize + i] = KeyOf(values[first + i]);
        }

        foreach (var recording in Recordings) {
            if (recording.Count + order.Length <= MaxNumbers) {
                recording.AddRange(order);
            }
        }
    }

    /// <summary>
    /// The part of a list of the given length that a shuffle goes through, the way the game reckons it: where it starts
    /// and how many things.
    /// </summary>
    private static (int First, int Count) ShuffledPart(ArrayShuffle shuffle, int length) {
        if (length == 0) {
            return (0, 0);
        }

        var first = 0;
        var last = length - 1;
        if (shuffle.startIndex is { Value: > 0 } startIndex) {
            first = Mathf.Min(startIndex.Value, last);
        }

        if (shuffle.shufflingRange is { Value: > 0 } range) {
            last = Mathf.Min(length - 1, first + range.Value);
        }

        return (first, last - first + 1);
    }

    /// <summary>
    /// A number for a thing in a list that is the same for the same thing in every game: an object by its scene and its
    /// path there, a component by its object and its kind, anything else of the game by its kind and name, a value by
    /// its kind and the value.
    /// </summary>
    private static int KeyOf(object? thing) {
        var text = thing switch {
            null => "",
            GameObject gameObject => gameObject == null ? "" : PlaceOf(gameObject.transform),
            Component component => component == null
                ? ""
                : PlaceOf(component.transform) + "|" + component.GetType().FullName,
            UnityEngine.Object unityObject => unityObject == null
                ? ""
                : unityObject.GetType().FullName + "|" + unityObject.name,
            float number => "float|" + number.ToString("R", CultureInfo.InvariantCulture),
            double number => "double|" + number.ToString("R", CultureInfo.InvariantCulture),
            _ => thing.GetType().FullName + "|" + Convert.ToString(thing, CultureInfo.InvariantCulture)
        };

        // FNV-1a, since the hash of a string is not promised to be the same in another process
        var hash = 2166136261u;
        foreach (var character in text) {
            hash = unchecked((hash ^ character) * 16777619u);
        }

        return unchecked((int) hash);
    }

    /// <summary>
    /// Where an object is, for <see cref="KeyOf"/>: its scene and its path there; for one in no loaded scene, like a
    /// prefab, which has no path to find, only its name.
    /// </summary>
    private static string PlaceOf(Transform transform) {
        var scene = transform.gameObject.scene;
        if (!scene.IsValid() || !scene.isLoaded) {
            return "|" + transform.name;
        }

        try {
            return scene.name + "|" + ScenePath.Get(transform);
        } catch (Exception e) {
            Logger.Debug($"No path for '{transform.name}' to know it by in a shuffled list: {e.Message}");
            return scene.name + "|" + transform.name;
        }
    }

    /// <summary>
    /// A number for an action that is the same in both games: from the name of its object, of its state machine, of its
    /// state and its place in the state. The copy of an entity is named after the entity with "(Clone)" on the end,
    /// which is left out, as many times as it is there: the copy of something spawned has it twice.
    /// </summary>
    private static int Identify(FsmStateAction action, PlayMakerFSM fsm) {
        const string clone = "(Clone)";

        var state = action.State;
        var place = state == null ? -1 : Array.IndexOf(state.Actions, action);
        var name = fsm.gameObject.name;
        while (name.EndsWith(clone, StringComparison.Ordinal)) {
            name = name[..^clone.Length];
        }

        // FNV-1a, since the hash of a string is not promised to be the same in another process
        var hash = 2166136261u;
        foreach (var text in new[] { name, fsm.FsmName, state?.Name ?? "" }) {
            foreach (var character in text) {
                hash = unchecked((hash ^ character) * 16777619u);
            }

            hash = unchecked((hash ^ '/') * 16777619u);
        }

        return unchecked((int) ((hash ^ (uint) place) * 16777619u));
    }

    /// <summary>
    /// Takes the state of <see cref="Random"/> as numbers.
    /// </summary>
    private static int[] TakeState() {
        var state = Random.state;
        return MemoryMarshal.Cast<Random.State, int>(MemoryMarshal.CreateSpan(ref state, 1)).ToArray();
    }

    /// <summary>
    /// Makes a state of <see cref="Random"/> from the numbers at the given place.
    /// </summary>
    private static Random.State ToState(IReadOnlyList<int> numbers, int start) {
        var state = new int[StateSize];
        for (var i = 0; i < StateSize; i++) {
            state[i] = numbers[start + i];
        }

        return MemoryMarshal.Cast<int, Random.State>(state)[0];
    }
}
