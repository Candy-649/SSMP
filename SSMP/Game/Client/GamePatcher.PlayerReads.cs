using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// A creature whose state machine reads of the player what is known of every player (see PlayerFacts): whether they
/// swim, and whether they carry the white flower, as the tendrils of the abyss do, which rise at a player who walks up
/// to them, or swims past further off, and keep down as one swims close by or carries the flower. The game that runs
/// it reads these of one player near it at a time, and asks its ranges and its checks of the needolin about that same
/// player (see <see cref="ReadPlayerOf"/>; USER 11-26 "为啥不能读两个人的，像处理弹琴一样，比如说有人靠近，就读那个人的，如
/// 果两个人都靠近，就只要有人有就显示那个状态，如果状态冲突就谁先触发显示谁的"). Before, it read them of the player of
/// that game, wherever they were, and its ranges of either player: a tendril rose at the other player as the player of
/// the game that ran it swam far off, and kept down as the other player walked up to it while that player swam.
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// The state of the player that GetHeroCState reads for whether they swim.
    /// </summary>
    private const string SwimmingState = "swimming";

    /// <summary>
    /// The field of the player's data that GetPlayerDataVariable reads for whether they carry the white flower.
    /// </summary>
    private const string WhiteFlowerField = "HasWhiteFlower";

    /// <summary>
    /// Which player each state machine that asks reads (see <see cref="ReadPlayerOf"/>).
    /// </summary>
    private static readonly ConditionalWeakTable<Fsm, PlayerRead> PlayerReads = new();

    /// <summary>
    /// Makes what a state machine reads of the players as it first asks.
    /// </summary>
    private static readonly ConditionalWeakTable<Fsm, PlayerRead>.CreateValueCallback NewPlayerRead =
        fsm => new PlayerRead(ReadsWhatEachPlayerIs(fsm));

    /// <summary>
    /// The players near the creature being looked at, by when each came near it (see <see cref="FindNearPlayers"/>).
    /// </summary>
    private static readonly List<GameObject> NearPlayers = [];

    /// <summary>
    /// The players there are while the creature being looked at is looked at.
    /// </summary>
    private static readonly List<GameObject> PlayersThere = [];

    /// <summary>
    /// The players who are no longer near the creature being looked at.
    /// </summary>
    private static readonly List<GameObject> PlayersGone = [];

    /// <summary>
    /// What the reads of a state would store for a player, by the name of the variable (see <see cref="SetsOff"/>).
    /// </summary>
    private static readonly Dictionary<string, bool> ReadValues = new();

    /// <summary>
    /// The hooks on the reads of whether the player swims, as the state starts and every frame.
    /// </summary>
    private Hook? _getHeroCStateEnterHook, _getHeroCStateUpdateHook;

    /// <summary>
    /// The hook on the read of a field of the player's data.
    /// </summary>
    private Hook? _getPlayerDataVariableEnterHook;

    /// <summary>
    /// Registers what reads of the player near a creature what the game reads of its own player.
    /// </summary>
    private void RegisterPlayerReadHooks() {
        _getHeroCStateEnterHook = TryCreateHook(
            typeof(GetHeroCState), "OnEnter", new Action<Action<GetHeroCState>, GetHeroCState>(OnGetHeroCState)
        );
        _getHeroCStateUpdateHook = TryCreateHook(
            typeof(GetHeroCState), "OnUpdate", new Action<Action<GetHeroCState>, GetHeroCState>(OnGetHeroCState)
        );
        _getPlayerDataVariableEnterHook = TryCreateHook(
            typeof(GetPlayerDataVariable), "OnEnter",
            new Action<Action<GetPlayerDataVariable>, GetPlayerDataVariable>(OnGetPlayerDataVariableEnter)
        );
    }

    /// <summary>
    /// Disposes what reads of the player near a creature what the game reads of its own player.
    /// </summary>
    private void DisposePlayerReadHooks() {
        _getHeroCStateEnterHook?.Dispose();
        _getHeroCStateUpdateHook?.Dispose();
        _getPlayerDataVariableEnterHook?.Dispose();
        _getHeroCStateEnterHook = _getHeroCStateUpdateHook = _getPlayerDataVariableEnterHook = null;
    }

    /// <summary>
    /// Whether a state machine reads of the player what is known of every player (see PlayerFacts).
    /// </summary>
    private static bool ReadsWhatEachPlayerIs(Fsm fsm) {
        foreach (var state in fsm.States) {
            foreach (var action in state.Actions) {
                if (action is GetHeroCState { VariableName.Value: SwimmingState } or
                    GetPlayerDataVariable { VariableName.Value: WhiteFlowerField }) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The player that a state machine that reads of the player what is known of every player reads now, in the game
    /// that runs it, or null for none, as nobody is near it, or for any other state machine. A creature that lies
    /// hidden reads the first player near it whose own reads set it off now, and else the one who came near it first:
    /// whoever has what calls it up calls it up. Once it shows itself, it keeps to the player it read as it did, while
    /// they are near, as the game keeps to one player: as each player went by what they were each frame, it rose at
    /// one, went down at the other and rose again, over and over. Near is in one of its ranges.
    /// </summary>
    private static GameObject? ReadPlayerOf(Fsm fsm) {
        var read = PlayerReads.GetValue(fsm, NewPlayerRead);
        var creature = fsm.GameObject;
        if (!read.ReadsEachPlayer || read.Frame == Time.frameCount || !Entity.Entity.IsEntityObject(creature)) {
            return read.Player;
        }

        read.Frame = Time.frameCount;
        FindNearPlayers(creature, read);
        GameObject? player = null;
        if (NearPlayers.Count == 0) {
            read.Holder = null;
        } else if (creature.GetComponent<Renderer>() is { enabled: true }) {
            if (read.Holder == null || !NearPlayers.Contains(read.Holder)) {
                read.Holder = read.Player != null && NearPlayers.Contains(read.Player) ? read.Player : NearPlayers[0];
            }

            player = read.Holder;
        } else {
            read.Holder = null;
            if (fsm.ActiveState is { } state) {
                foreach (var near in NearPlayers) {
                    if (SetsOff(fsm, state, near)) {
                        player = near;
                        break;
                    }
                }
            }

            player ??= NearPlayers[0];
        }

        if (player != read.Player) {
            Logger.Info(
                $"[Player reads] '{creature.name}' in '{fsm.ActiveStateName}' of '{fsm.Name}' reads " +
                (player != null ? DescribePlayer(player) : "nobody") + $" now, of {NearPlayers.Count} near it"
            );

            // What the state read of the player as it started it reads of this player from now on
            if (player != null && fsm.ActiveState is { } active) {
                ReadAgain(active, player);
            }
        }

        read.Player = player;
        return player;
    }

    /// <summary>
    /// Lists the players near a creature by when each came near it (see <see cref="NearPlayers"/>): in one of its
    /// ranges, as the ranges of a creature that reads the player are answered (see IsValidTargetForAlertRange).
    /// </summary>
    private static void FindNearPlayers(GameObject creature, PlayerRead read) {
        PlayersThere.Clear();
        PlayersThere.AddRange(PlayerTargetRegistry.GetTrackedPlayers());
        PlayersGone.Clear();
        foreach (var known in read.CameNear.Keys) {
            if (!PlayersThere.Contains(known)) {
                PlayersGone.Add(known);
            }
        }

        var now = Time.unscaledTime;
        read.Ranges ??= creature.GetComponentsInChildren<AlertRange>(true);
        foreach (var player in PlayersThere) {
            var near = false;
            foreach (var range in read.Ranges) {
                if (range.gameObject.activeInHierarchy && IsValidTargetForAlertRange(range, player)) {
                    near = true;
                    break;
                }
            }

            if (!near) {
                PlayersGone.Add(player);
            } else if (!read.CameNear.ContainsKey(player)) {
                read.CameNear[player] = now;
            }
        }

        foreach (var gone in PlayersGone) {
            read.CameNear.Remove(gone);
        }

        NearPlayers.Clear();
        foreach (var pair in read.CameNear) {
            var at = 0;
            while (at < NearPlayers.Count && read.CameNear[NearPlayers[at]] <= pair.Value) {
                at++;
            }

            NearPlayers.Insert(at, pair.Key);
        }
    }

    /// <summary>
    /// Whether the reads of a player in a state of a creature that lies hidden set it off: a check of a range that
    /// they are in, a check of their song that hears it anywhere, or a test of what the reads of the state store, as
    /// each of them stores it of that player, that sends an event that takes the state machine on. Not a range that
    /// they are out of, which sends the creature further down rather than calling it up.
    /// </summary>
    private static bool SetsOff(Fsm fsm, FsmState state, GameObject player) {
        ReadValues.Clear();
        foreach (var action in state.Actions) {
            if (!action.Enabled) {
                continue;
            }

            switch (action) {
                case GetHeroCState { VariableName.Value: SwimmingState } swims when !swims.StoreValue.IsNone:
                    ReadValues[swims.StoreValue.Name] = PlayerFacts.IsSwimming(player);
                    break;
                case GetPlayerDataVariable { VariableName.Value: WhiteFlowerField } flower
                    when !flower.StoreValue.IsNone:
                    ReadValues[flower.StoreValue.variableName] = PlayerFacts.HasWhiteFlower(player);
                    break;
                case CheckAlertRangeByName check:
                    var inRange = AlertRange.Find(fsm.GameObject, check.alertRangeName) is { } range &&
                                  IsValidTargetForAlertRange(range, player);
                    if (!check.storeResult.IsNone) {
                        ReadValues[check.storeResult.Name] = inRange;
                    }

                    if (inRange && TakesOn(fsm, state, check.sendEvent.Value)) {
                        return true;
                    }

                    break;
                case CheckHeroPerformanceRegion song when song.IgnoreNeedolinRange.Value &&
                                                          PlayerFacts.Performs(player):
                    if (TakesOn(fsm, state, song.ActiveInner?.Name) || TakesOn(fsm, state, song.ActiveOuter?.Name)) {
                        return true;
                    }

                    break;
            }
        }

        foreach (var action in state.Actions) {
            if (action is BoolTestMulti { Enabled: true } test && Holds(test) is { } holds &&
                TakesOn(fsm, state, (holds ? test.trueEvent : test.falseEvent)?.Name)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a test of a state holds, as the game tests it (BoolTestMulti.DoAllTrue), of the values that the reads of
    /// the state store of a player (see <see cref="ReadValues"/>), or null where the game tests nothing, its lists
    /// being empty or of different lengths.
    /// </summary>
    private static bool? Holds(BoolTestMulti test) {
        if (test.boolVariables.Length == 0 || test.boolVariables.Length != test.boolStates.Length) {
            return null;
        }

        for (var i = 0; i < test.boolVariables.Length; i++) {
            var variable = test.boolVariables[i];
            var value = ReadValues.TryGetValue(variable.Name, out var read) ? read : variable.Value;
            if (value != test.boolStates[i].Value) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether an event takes a state machine on from a state.
    /// </summary>
    private static bool TakesOn(Fsm fsm, FsmState state, string? eventName) {
        if (string.IsNullOrEmpty(eventName)) {
            return false;
        }

        foreach (var way in state.Transitions) {
            if (way.EventName == eventName) {
                return true;
            }
        }

        foreach (var way in fsm.GlobalTransitions) {
            if (way.EventName == eventName) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads again of a player what a state read as it started, of the player it read then.
    /// </summary>
    private static void ReadAgain(FsmState state, GameObject player) {
        foreach (var action in state.Actions) {
            switch (action) {
                case GetHeroCState { Enabled: true, VariableName.Value: SwimmingState } swims
                    when !swims.StoreValue.IsNone:
                    swims.StoreValue.Value = PlayerFacts.IsSwimming(player);
                    break;
                case GetPlayerDataVariable { Enabled: true, VariableName.Value: WhiteFlowerField } flower
                    when !flower.StoreValue.IsNone:
                    flower.StoreValue.SetValue(PlayerFacts.HasWhiteFlower(player));
                    break;
            }
        }
    }

    /// <summary>
    /// Whether a creature's state machine reads that the player it reads swims, where it reads it of the player near
    /// it (see <see cref="ReadPlayerOf"/>).
    /// </summary>
    private static void OnGetHeroCState(Action<GetHeroCState> orig, GetHeroCState self) {
        orig(self);
        if (self.VariableName.Value == SwimmingState && !self.StoreValue.IsNone &&
            ReadPlayerOf(self.Fsm) is { } player) {
            self.StoreValue.Value = PlayerFacts.IsSwimming(player);
        }
    }

    /// <summary>
    /// Whether a creature's state machine reads that the player it reads carries the white flower, where it reads it
    /// of the player near it (see <see cref="ReadPlayerOf"/>).
    /// </summary>
    private static void OnGetPlayerDataVariableEnter(Action<GetPlayerDataVariable> orig, GetPlayerDataVariable self) {
        orig(self);
        if (self.VariableName.Value == WhiteFlowerField && !self.StoreValue.IsNone &&
            ReadPlayerOf(self.Fsm) is { } player) {
            self.StoreValue.SetValue(PlayerFacts.HasWhiteFlower(player));
        }
    }

    /// <summary>
    /// Which player a state machine that reads of the player what is known of every player reads.
    /// </summary>
    private sealed class PlayerRead(bool readsEachPlayer) {
        /// <summary>
        /// Whether the state machine reads of the player what is known of every player (see
        /// <see cref="ReadsWhatEachPlayerIs"/>); none other reads anybody here.
        /// </summary>
        public readonly bool ReadsEachPlayer = readsEachPlayer;

        /// <summary>
        /// When each player near the creature came near it, by Time.unscaledTime.
        /// </summary>
        public readonly Dictionary<GameObject, float> CameNear = new();

        /// <summary>
        /// The ranges of the creature, in one of which a player is near it.
        /// </summary>
        public AlertRange[]? Ranges;

        /// <summary>
        /// The frame that <see cref="Player"/> was found for, by Time.frameCount.
        /// </summary>
        public int Frame = -1;

        /// <summary>
        /// The player it reads, or null for none.
        /// </summary>
        public GameObject? Player;

        /// <summary>
        /// The player it keeps to while it shows itself, or null while it lies hidden.
        /// </summary>
        public GameObject? Holder;
    }
}
