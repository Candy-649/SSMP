using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HutongGames.PlayMaker;
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
/// The other game gives an action the roll written down under the same action, wherever that is, and lets an action
/// with none roll its own. So a trap that another thing set off does not take the rolls of that thing when it went off
/// over there already by itself, and a state machine that goes another way over there does not shift every roll after.
///
/// It covers only what happens at once, in the same call: what the thing rolls later, after a wait, each game rolls
/// again. This is no seed shared by the room. That was turned down, since each game rolls at its own frame rate and for
/// its own player, so the same seed soon gives different numbers. Here the dice are lent for the one thing and handed
/// back.
/// </summary>
internal static class SharedDice {
    /// <summary>
    /// How many numbers the state of <see cref="Random"/> is.
    /// </summary>
    private const int StateSize = 4;

    /// <summary>
    /// How many numbers each roll of an action takes: which action, and the state as it started.
    /// </summary>
    private const int RollSize = StateSize + 1;

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
    /// Where the first roll starts in <see cref="_thrown"/> that no action here has taken, which is where the next
    /// action looks for its own.
    /// </summary>
    private static int _nextRoll;

    /// <summary>
    /// The state machine of an entity that rolls with the dice of what is being set off, or null for none. Otherwise
    /// only the room's own state machines do: an entity rolls where the scene host runs it, except for something that
    /// a strike of the other player's has it do (see <see cref="Entity.Entity.PlayBounceHere"/>).
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
        if (dice == null || dice.Count < StateSize || (dice.Count - StateSize) % RollSize != 0) {
            action();
            return;
        }

        var own = Random.state;
        var (lastThrown, lastNextRoll, lastEntityFsm) = (_thrown, _nextRoll, _entityFsm);
        Random.state = ToState(dice, 0);
        (_thrown, _nextRoll, _entityFsm) = (dice, StateSize, entityFsm ?? lastEntityFsm);
        try {
            action();
        } finally {
            (_thrown, _nextRoll, _entityFsm) = (lastThrown, lastNextRoll, lastEntityFsm);
            Random.state = own;
        }
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
        if (Recordings.Count == 0 && _thrown == null || self.Fsm?.FsmComponent is not { } fsm ||
            self.Fsm != _entityFsm && !CoopHits.IsRoomObject(fsm)) {
            orig(self);
            return;
        }

        var action = Identify(self, fsm);

        if (_thrown is { } thrown) {
            for (var roll = _nextRoll; roll + RollSize <= thrown.Count; roll += RollSize) {
                if (thrown[roll] != action) {
                    continue;
                }

                // The rolls in between were of actions that did not start here, which leaves them to nothing
                Random.state = ToState(thrown, roll + 1);
                _nextRoll = roll + RollSize;
                break;
            }
        }

        foreach (var recording in Recordings) {
            if (recording.Count + RollSize <= MaxNumbers) {
                recording.Add(action);
                recording.AddRange(TakeState());
            }
        }

        orig(self);
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
