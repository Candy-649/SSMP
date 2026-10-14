using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HutongGames.PlayMaker;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// What the room's own objects make of a sign of a player: a character who turns round to a taunt and greets it, flees
/// from it or comes out for it, a resting guard that a taunt wakes, the post that a taunt with the ring calls someone
/// with, a basket that a player sprints into and knocks over, and what that sets off in turn, like a room of sleepers
/// that wakes. The objects of the room hear only the signs of their own game's player, so each of these happened in
/// the game of the player who gave the sign and nowhere else (USER 10-15, "第三条要做").
///
/// Now the state machines of the room that a sign of the local player moves in the same breath as it is given are
/// written down as they go, each from the state it waited in through every state it went to, and the others' games
/// take theirs the same way with the dice of the sign (see <see cref="WalkSignHere"/>). What one judged of the player
/// who gave it on the way - that they stood near enough, which side they stood on - is judged by that player's game,
/// as the player who throws the dice decides what they roll; what it goes on to do from where the sign left it, like
/// a greeting after a pause, runs in each game by itself, as everything of the room does. Turned round, a character
/// faces each game's own player after that pause. One that looked at the sign and went straight back to waiting did
/// nothing to walk. What belongs to a boss fight is left to the fight, which plays it in every game already, and an
/// arena to the scene host's game.
/// </summary>
internal partial class CoopHits {
    /// <summary>
    /// The events of the local player's own state machines that tell straight what the player bumps into, which the
    /// room takes like a sign: the sprint that runs into something (USER 10-15, "冲刺撞击").
    /// </summary>
    private static readonly HashSet<string> BumpSigns = ["SPRINT BONKED"];

    /// <summary>
    /// The state machines of the room, by the start of the name of their object and their own name, whose answer to a
    /// sign stays with each game: the beast of a station that a taunt calls to carry the player who called it, as the
    /// interact button does.
    /// </summary>
    private static readonly (string ObjectName, string FsmName)[] OwnSignAnswers = [("Bone Beast NPC", "Interaction")];

    /// <summary>
    /// The most state machines that one sign sends the way of, and the most states of each way: more says that
    /// something else went on in the same breath.
    /// </summary>
    private const int MaxSignWays = 32;

    /// <summary>
    /// What a sign of the local player moves while it is given, or null while none is (see <see cref="WalkOwnSign"/>).
    /// </summary>
    private SignRecording? _signRecording;

    /// <summary>
    /// The state machines of the room that go the way that a sign of the partner took theirs, while they do (see
    /// <see cref="WalkSignHere"/>).
    /// </summary>
    private readonly Dictionary<Fsm, SignWalk> _signWalks = new();

    /// <summary>
    /// Has the room hear a sign of the local player, and the others' games take the way it took each state machine of
    /// the room that it moved (see <see cref="WalkSignHere"/>): it is written down as the game tells it, with the dice
    /// the room rolls and what it judges of the local player meanwhile, and sent.
    /// </summary>
    /// <param name="sign">The sign.</param>
    /// <param name="tell">What tells the sign as the game does.</param>
    private void WalkOwnSign(string sign, Action tell) {
        // Told in the middle of another, it is part of that one
        if (_signRecording != null || !HasMembers || !IsAnyMemberInRoom()) {
            tell();
            return;
        }

        var recording = new SignRecording();
        _signRecording = recording;
        int[] dice;
        try {
            dice = SharedDice.Record(tell);
        } finally {
            _signRecording = null;
        }

        SendSignWalk(sign, recording, dice);
    }

    /// <summary>
    /// Writes down a state that a state machine is about to go to while a sign of the local player is given, if it is
    /// one of the room whose way the others' games take (see <see cref="TakesSignWalks"/>).
    /// </summary>
    /// <param name="fsm">The state machine.</param>
    /// <param name="toState">The state.</param>
    /// <returns>The way of the state machine so far, to take the state off again if it never went there (see
    /// <see cref="UnnoteSignStep"/>), or null if nothing was written down.</returns>
    private SignWay? NoteSignStep(Fsm fsm, FsmState? toState) {
        if (_signRecording is not { } recording || toState == null) {
            return null;
        }

        if (!recording.Ways.TryGetValue(fsm, out var way)) {
            way = fsm.ActiveState is { } from && TakesSignWalks(fsm) ? new SignWay(fsm, from) : null;
            recording.Ways[fsm] = way;
            if (way != null) {
                recording.InOrder.Add(way);
            }
        }

        way?.States.Add(toState);
        return way;
    }

    /// <summary>
    /// Takes a state off the way that a state machine took, if something kept it from going there after all.
    /// </summary>
    /// <param name="way">The way.</param>
    /// <param name="toState">The state it was about to go to.</param>
    private static void UnnoteSignStep(SignWay? way, FsmState toState) {
        // One it went on from in the same breath has the states after it written down after it
        if (way != null && way.States.Count > 0 && way.States[^1] == toState && way.Fsm.ActiveState != toState) {
            way.States.RemoveAt(way.States.Count - 1);
        }
    }

    /// <summary>
    /// Takes a step of a state machine that goes the way of a sign of the partner (see <see cref="WalkSignHere"/>), as
    /// it goes to the next state of that way.
    /// </summary>
    /// <param name="fsm">The state machine.</param>
    /// <param name="toState">The state it goes to.</param>
    private void StepSignWalk(Fsm fsm, FsmState? toState) {
        if (_signWalks.Count > 0 && _signWalks.TryGetValue(fsm, out var walk) && walk.At < walk.States.Length &&
            walk.States[walk.At] == toState) {
            walk.At++;
        }
    }

    /// <summary>
    /// Whether a state machine is one of the room whose way a sign of a player takes in every game: one of the room's
    /// own (see <see cref="IsRoomObject"/>), other than an arena, which follows the scene host's game its own way,
    /// what belongs to a boss fight, which the fight plays in every game, and what stays with each game (see
    /// <see cref="OwnSignAnswers"/>).
    /// </summary>
    /// <param name="fsm">The state machine.</param>
    private static bool TakesSignWalks(Fsm fsm) {
        if (fsm.FsmComponent is not { } machine || machine == null || !IsRoomObject(machine) ||
            machine.GetComponent<BattleScene>() != null || BossRoomCoop.FindBossRoot(machine.transform) != null) {
            return false;
        }

        foreach (var (objectName, fsmName) in OwnSignAnswers) {
            if (machine.FsmName == fsmName && machine.name.StartsWith(objectName, StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Sends the way that a sign of the local player took each state machine of the room that it moved to the members
    /// in the room, with the dice it rolled. One that looked at the sign and went straight back to where it waited
    /// turned it down, and has nothing to walk.
    /// </summary>
    /// <param name="sign">The sign.</param>
    /// <param name="recording">What the sign moved.</param>
    /// <param name="dice">The dice.</param>
    private void SendSignWalk(string sign, SignRecording recording, int[] dice) {
        var ways = new List<(SignWay Way, string Scene, string Path, byte Index)>();
        foreach (var way in recording.InOrder) {
            if (way.States.Count == 0 || way.States.Count > byte.MaxValue ||
                way.States.Count == 2 && way.States[1] == way.From ||
                way.Fsm.FsmComponent is not { } machine || machine == null ||
                !TryGetPlace(machine.gameObject, out var scene, out var path)) {
                continue;
            }

            var sameName = Array.FindAll(
                machine.GetComponents<PlayMakerFSM>(), other => other.FsmName == machine.FsmName
            );
            var index = Array.IndexOf(sameName, machine);
            if (index is < 0 or > byte.MaxValue) {
                continue;
            }

            ways.Add((way, scene, path, (byte) index));
            if (ways.Count == MaxSignWays) {
                break;
            }
        }

        if (ways.Count == 0) {
            return;
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte) ways.Count);
        foreach (var (way, scene, path, index) in ways) {
            writer.Write(scene);
            writer.Write(path);
            writer.Write(way.Fsm.Name);
            writer.Write(index);
            writer.Write(way.From.Name);
            writer.Write((byte) way.States.Count);
            foreach (var state in way.States) {
                writer.Write(state.Name);
            }
        }

        SharedDice.Write(writer, dice);
        writer.Flush();

        if (!SendToRoomMembers(new CoopHitUpdate {
                Kind = CoopHitKind.ObjectSignWalk,
                Scene = SceneManager.GetActiveScene().name,
                Responder = sign,
                Hit = stream.ToArray()
            })) {
            return;
        }

        var said = new StringBuilder();
        foreach (var (way, _, path, _) in ways) {
            var states = string.Join("', '", way.States.ConvertAll(state => state.Name));
            said.Append(said.Length == 0 ? "" : "; ")
                .Append($"'{path}' '{way.Fsm.Name}' from '{way.From.Name}' to '{states}'");
        }

        Logger.Info($"Sent the way the local player's '{sign}' took {ways.Count} of the room to the others: {said}");
    }

    /// <summary>
    /// Takes the way that a sign of the partner took state machines of the room in their game (see
    /// <see cref="WalkOwnSign"/>): each that is here and in the state theirs waited in goes through the same states,
    /// with the dice of their game. Where it judges something of a player on the way, it goes the way it went there,
    /// where the partner's game judged the partner: an event that would take it anywhere else is not heard, and one
    /// that takes it on is told it where nothing here does. What it is told by the others on the way, like a room of
    /// sleepers that a basket knocked over wakes, goes their way as well. One that waits elsewhere here goes its own
    /// way.
    /// </summary>
    /// <param name="update">The update.</param>
    private void WalkSignHere(CoopHitUpdate update) {
        var walks = new List<SignWalk>();
        int[] dice;
        try {
            using var reader = new BinaryReader(new MemoryStream(update.Hit));
            var count = reader.ReadByte();
            for (var i = 0; i < count; i++) {
                var scene = reader.ReadString();
                var path = reader.ReadString();
                var fsmName = reader.ReadString();
                var index = reader.ReadByte();
                var from = reader.ReadString();
                var states = new string[reader.ReadByte()];
                for (var j = 0; j < states.Length; j++) {
                    states[j] = reader.ReadString();
                }

                if (FindSignWalk(scene, path, fsmName, index, from, states, update.Responder) is { } walk &&
                    !walks.Exists(other => other.Fsm == walk.Fsm)) {
                    walks.Add(walk);
                }
            }

            dice = SharedDice.Read(reader) ?? [];
        } catch (IOException e) {
            Logger.Warn($"Could not read the way the partner's '{update.Responder}' took the room: {e.Message}");
            return;
        }

        if (walks.Count == 0) {
            return;
        }

        // Each takes the way of its own as the others are told things on theirs
        foreach (var walk in walks) {
            _signWalks[walk.Fsm] = walk;
        }

        try {
            SharedDice.Throw(dice, () => {
                foreach (var walk in walks) {
                    Walk(walk);
                }
            });
        } finally {
            foreach (var walk in walks) {
                _signWalks.Remove(walk.Fsm);
            }
        }

        foreach (var walk in walks) {
            var name = walk.Fsm.GameObjectName;
            var turned = walk.Turned > 0 ? $", past {walk.Turned} way(s) of its own" : "";
            Logger.Info(
                walk.At == walk.States.Length
                    ? $"'{name}' '{walk.Fsm.Name}' went the way the partner's '{update.Responder}' took it there, " +
                      $"to '{walk.States[^1].Name}'{turned}"
                    : $"'{name}' '{walk.Fsm.Name}' went the way the partner's '{update.Responder}' took it there " +
                      $"only as far as '{walk.Fsm.ActiveStateName}'{turned}"
            );
        }
    }

    /// <summary>
    /// Finds the state machine here that a way of the partner's sign names, if it is one whose way a sign takes in
    /// every game and waits in the state theirs waited in.
    /// </summary>
    /// <returns>Its walk, or null for none.</returns>
    private SignWalk? FindSignWalk(
        string scene,
        string path,
        string fsmName,
        byte index,
        string from,
        string[] stateNames,
        string sign
    ) {
        if (ScenePath.Find(path, scene) is not { } target) {
            return null;
        }

        var sameName = Array.FindAll(target.GetComponents<PlayMakerFSM>(), fsm => fsm.FsmName == fsmName);
        if (index >= sameName.Length || sameName[index] is not { isActiveAndEnabled: true } machine ||
            machine.Fsm is not { } fsm || _signWalks.ContainsKey(fsm) || !TakesSignWalks(fsm)) {
            return null;
        }

        if (fsm.ActiveStateName != from) {
            Logger.Info(
                $"'{path}' '{fsmName}' is in '{fsm.ActiveStateName}' here rather than in '{from}', where the " +
                $"partner's '{sign}' found it, so it goes its own way"
            );
            return null;
        }

        var states = new FsmState[stateNames.Length];
        for (var i = 0; i < states.Length; i++) {
            if (fsm.GetState(stateNames[i]) is not { } state) {
                return null;
            }

            states[i] = state;
        }

        return new SignWalk(fsm, states);
    }

    /// <summary>
    /// Takes a state machine the way of a sign of the partner from where it is on it: each state is gone to on an
    /// event that takes it there, which hears it at once, or put in where no event of it does.
    /// </summary>
    /// <param name="walk">The walk.</param>
    private static void Walk(SignWalk walk) {
        // One that something on the way switched off goes no further, as it went no further there
        var fsm = walk.Fsm;
        while (walk.At < walk.States.Length && fsm.ActiveState is { } from &&
               fsm.FsmComponent is { isActiveAndEnabled: true }) {
            var at = walk.At;
            var to = walk.States[at];
            try {
                if (TransitionTo(fsm, from, to) is { } transition) {
                    fsm.Event(transition.FsmEvent);
                }

                if (walk.At == at) {
                    fsm.SetState(to.Name);
                }
            } catch (Exception e) {
                Logger.Warn($"Could not take '{fsm.GameObjectName}' '{fsm.Name}' to '{to.Name}': {e.Message}");
                return;
            }

            if (walk.At == at) {
                return;
            }
        }
    }

    /// <summary>
    /// Hook for an event that a state machine hears, which one that goes the way of a sign of the partner hears only
    /// where it takes it to the next state of that way, or nowhere: anywhere else is where something of this game -
    /// where its own player stands - would take it, while the partner's game judged it of the partner who gave the
    /// sign.
    /// </summary>
    private void OnProcessEventInSignWalk(
        Action<Fsm, FsmEvent, FsmEventData> orig,
        Fsm self,
        FsmEvent fsmEvent,
        FsmEventData eventData
    ) {
        if (_signWalks.Count == 0 || !_signWalks.TryGetValue(self, out var walk) || self.IsSwitchingState ||
            self.ActiveState is not { } state || TakenOn(self, state, fsmEvent) is not { } transition ||
            walk.At < walk.States.Length && transition.ToFsmState == walk.States[walk.At]) {
            orig(self, fsmEvent, eventData);
            return;
        }

        walk.Turned++;
    }

    /// <summary>
    /// A transition of a state machine from a state to another that an event takes it by, the way the game looks for
    /// the transition of an event: the global ones first.
    /// </summary>
    /// <returns>The transition, or null if no event takes it there.</returns>
    private static FsmTransition? TransitionTo(Fsm fsm, FsmState from, FsmState to) {
        foreach (var transition in fsm.GlobalTransitions) {
            if (transition.ToFsmState == to && TakenOn(fsm, from, transition.FsmEvent) == transition) {
                return transition;
            }
        }

        foreach (var transition in from.Transitions) {
            if (transition.ToFsmState == to && TakenOn(fsm, from, transition.FsmEvent) == transition) {
                return transition;
            }
        }

        return null;
    }

    /// <summary>
    /// The transition that an event takes a state machine by from a state, the way the game looks for it: the global
    /// ones first.
    /// </summary>
    /// <returns>The transition, or null if the event leads nowhere from there.</returns>
    private static FsmTransition? TakenOn(Fsm fsm, FsmState state, FsmEvent? fsmEvent) {
        if (fsmEvent == null) {
            return null;
        }

        foreach (var transition in fsm.GlobalTransitions) {
            if (transition.FsmEvent == fsmEvent) {
                return transition;
            }
        }

        foreach (var transition in state.Transitions) {
            if (transition.FsmEvent == fsmEvent) {
                return transition;
            }
        }

        return null;
    }

    /// <summary>
    /// What a sign of the local player moved while it was given (see <see cref="WalkOwnSign"/>).
    /// </summary>
    private sealed class SignRecording {
        /// <summary>
        /// The way of each state machine that went anywhere, or null for one whose way is no one else's.
        /// </summary>
        public Dictionary<Fsm, SignWay?> Ways { get; } = new();

        /// <summary>
        /// The ways, in the order their state machines first went anywhere.
        /// </summary>
        public List<SignWay> InOrder { get; } = [];
    }

    /// <summary>
    /// The way a sign of the local player took a state machine of the room.
    /// </summary>
    /// <param name="fsm">The state machine.</param>
    /// <param name="from">The state it was in as the sign was given.</param>
    private sealed class SignWay(Fsm fsm, FsmState from) {
        /// <summary>
        /// The state machine.
        /// </summary>
        public Fsm Fsm { get; } = fsm;

        /// <summary>
        /// The state it was in as the sign was given.
        /// </summary>
        public FsmState From { get; } = from;

        /// <summary>
        /// The states it went to, in order.
        /// </summary>
        public List<FsmState> States { get; } = [];
    }

    /// <summary>
    /// A state machine of the room that goes the way that a sign of the partner took theirs (see
    /// <see cref="WalkSignHere"/>).
    /// </summary>
    /// <param name="fsm">The state machine.</param>
    /// <param name="states">The states it goes to, in order.</param>
    private sealed class SignWalk(Fsm fsm, FsmState[] states) {
        /// <summary>
        /// The state machine.
        /// </summary>
        public Fsm Fsm { get; } = fsm;

        /// <summary>
        /// The states it goes to, in order.
        /// </summary>
        public FsmState[] States { get; } = states;

        /// <summary>
        /// How many of them it went to so far.
        /// </summary>
        public int At { get; set; }

        /// <summary>
        /// How many events it did not hear, which would have taken it elsewhere.
        /// </summary>
        public int Turned { get; set; }
    }
}
