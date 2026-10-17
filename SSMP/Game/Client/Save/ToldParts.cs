using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Parts of a room that a creature tells to go, under the same name in every game. A boss tells the spikes of its room
/// to come up, a spear to fall, a barb that it threw to fly; the part then rolls by itself where it comes up, how long
/// it waits and how it turns, and the spear looks at the player to fall on. Only the scene host runs the creature, but
/// the other game is told what it did and tells its own part the same, so the part ran in both games, each with its own
/// dice and each at its own player: the spikes came up in other places for the two players, and a player was hit by a
/// spear that the other saw fall somewhere else (FsmScan --hazards, USER 10-13: 先清查，再做对比).
///
/// Like the things that creatures throw (see ThrownThings), what the creature tells goes with a name: the game that
/// runs the creature makes one up as the creature tells a part of the room that rolls or looks at a player, gives it to
/// the part before the part takes the event, and sends it with the creature's action; the other game gives it to its
/// own part before it tells it. With the name both games share
/// - the dice: everything the part rolls from then on comes from its name (see SharedDice.RollByName);
/// - the player it goes after: the one the creature went after as it told the part, whom the part looks at wherever it
///   looks at a player, as it already did in the game that runs the creature (GamePatcher.NoteWhatAnEnemySetsOff).
/// What a named part tells in turn rolls under a name made from the part's own, the same in every game, so a part that
/// sets off others - a holder that tells its spikes to come up - passes the name on.
///
/// A part that didn't take the event in the game that runs the creature, still busy with the last time it was told,
/// isn't told here either; one that took it there but is still busy here goes back to where the other was as it took it
/// (as a part of a controller does, see CoopSave.ShownParts). For an event sent after a delay that isn't known as the
/// creature tells it, so such a part is only named. What a creature broadcasts to the room names the parts that roll
/// among those that take it, each by its place in the room. A machine that goes by the scene host's rounds is only
/// aimed: its rounds roll under names of their own, and the host's word brings one that is behind through the rest of
/// its round (see CoopSave.PacedMachines).
/// </summary>
internal static class ToldParts {
    /// <summary>
    /// What stands between a name and the path of a part under what was told.
    /// </summary>
    private const char PartStart = '|';

    /// <summary>
    /// How many parts that took an event are sent with it at most, to go back to where they were as they took it.
    /// </summary>
    private const int MaxTakers = 16;

    /// <summary>
    /// What the keys of this game's tells start with, so that the tells of two games that ran the same room never share
    /// a key.
    /// </summary>
    private static readonly string Prefix = Guid.NewGuid().ToString("N").Substring(0, 8);

    /// <summary>
    /// The number of the last tell of this game.
    /// </summary>
    private static int _lastNumber;

    /// <summary>
    /// What the actions of creatures that this game runs told in the frame they last ran in, to be sent with them.
    /// </summary>
    private static readonly ConditionalWeakTable<FsmStateAction, HostTell> HostTells = new();

    /// <summary>
    /// The hooks on the actions that tell or broadcast.
    /// </summary>
    private static readonly List<Hook> Hooks = [];

    /// <summary>
    /// Whether something went wrong here already and was said in the log, which is said once in each room.
    /// </summary>
    private static bool _failed;

    /// <summary>
    /// The parts told by the scene host's creature that were said in the log in this room, by instance ID.
    /// </summary>
    private static readonly HashSet<int> SaidParts = [];

    /// <summary>
    /// Whether a state machine of the room is a controller of a fight that the other game follows in every state, with
    /// the dice of each (see CoopSave.DirectedFights), which is left out: named, it would roll by the name and leave
    /// nothing to follow (set by CoopSave).
    /// </summary>
    public static Func<PlayMakerFSM, bool>? IsFollowedInEveryState { get; set; }

    /// <summary>
    /// Whether a state machine of the room goes by the rounds of the scene host's (see CoopSave.PacedMachines): each of
    /// its rounds rolls under a name of its own and waits for the host's word, which also brings one that is behind
    /// through the rest of its round. What a creature tells it is left to that: it is not named here, and not put back
    /// to take the event, which would cut its round short and leave what it had up as it was (set by CoopSave).
    /// </summary>
    public static Func<PlayMakerFSM, bool>? IsPaced { get; set; }

    /// <summary>
    /// What goes with an action of a creature that tells something.
    /// </summary>
    public enum Word : byte {
        /// <summary>
        /// Nothing: what it told rolls nothing and looks at no player, or it tells nothing of the room.
        /// </summary>
        None = 0,

        /// <summary>
        /// The name and the player of what it told.
        /// </summary>
        Named = 1,

        /// <summary>
        /// Nothing that it told took the event in the game that runs the creature: with where the parts of the room it
        /// went to were (see <see cref="CatchUp"/>).
        /// </summary>
        NotTaken = 2
    }

    /// <summary>
    /// Registers the hooks on the actions with which a creature tells something, for the game that runs it to name
    /// what it tells before it goes.
    /// </summary>
    public static void RegisterHooks() {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        AddHook(typeof(SendEventByName).GetMethod("OnEnter", flags, null, Type.EmptyTypes, null));
        AddHook(typeof(SendEventByNameV2).GetMethod("OnEnter", flags, null, Type.EmptyTypes, null));
        AddHook(typeof(SendEventByNameOnExit).GetMethod("OnExit", flags, null, Type.EmptyTypes, null));
        AddHook(typeof(SendEventToRegister).GetMethod("OnEnter", flags, null, Type.EmptyTypes, null));
        AddHook(typeof(SendEventToRegisterV2).GetMethod("OnEnter", flags, null, Type.EmptyTypes, null));
    }

    /// <summary>
    /// Hooks a method of an action that tells something.
    /// </summary>
    private static void AddHook(MethodInfo? method) {
        if (method == null) {
            Logger.Error("Could not find a method of an action that tells something; parts it tells are not named");
            return;
        }

        try {
            Hooks.Add(new Hook(method, new Action<Action<FsmStateAction>, FsmStateAction>(OnTell)));
        } catch (Exception e) {
            Logger.Error($"Could not hook {method.DeclaringType?.Name}.{method.Name}:\n{e}");
        }
    }

    /// <summary>
    /// Hook on an action that tells something: in the game that runs the creature it belongs to, names what it tells
    /// before the event goes.
    /// </summary>
    private static void OnTell(Action<FsmStateAction> orig, FsmStateAction self) {
        try {
            if (Entity.Entity.TryGetThrower(self, out var creature)) {
                NameWhatIsTold(self, creature);
            }
        } catch (Exception e) {
            SayFailed(e);
        }

        orig(self);
    }

    /// <summary>
    /// Names what an action of a creature that this game runs tells, and notes it for the action's data to the other
    /// game: the parts of the room that roll or look at a player among those it goes to, or among those registered for
    /// what it broadcasts.
    /// </summary>
    private static void NameWhatIsTold(FsmStateAction action, GameObject creature) {
        HostTells.Remove(action);
        var fsm = action.Fsm;
        if (fsm == null) {
            return;
        }

        switch (action) {
            case SendEventToRegister broadcast:
                NameBroadcast(action, creature, broadcast.eventName?.Value);
                return;
            case SendEventToRegisterV2 broadcast:
                NameBroadcast(action, creature, broadcast.EventName?.Value);
                return;
        }

        var (eventTarget, eventName, delay) = action switch {
            SendEventByName send => (send.eventTarget, send.sendEvent?.Value, send.delay?.Value ?? 0f),
            SendEventByNameV2 send => (send.eventTarget, send.sendEvent?.Value, send.delay?.Value ?? 0f),
            SendEventByNameOnExit send => (send.eventTarget, send.sendEvent?.Value, 0f),
            _ => (null, null, 0f)
        };
        if (eventTarget == null || eventName is not { Length: > 0 } ||
            FindTold(fsm, eventTarget) is not { } told || !NamesAny(told.Machines)) {
            return;
        }

        // Whether the parts take it is only known now for an event that goes at once. Nothing of all it goes to may
        // take it, of the room or not, for the other game to let it go by.
        List<Taker>? takers = null;
        if (delay < 0.001f) {
            var anyTakes = false;
            foreach (var machine in told.Everything) {
                anyTakes |= machine != null && machine.Fsm is { } anyFsm && Takes(anyFsm, eventName);
            }

            foreach (var machine in told.Machines) {
                if (machine.Fsm is not { } partFsm || !Takes(partFsm, eventName) || IsPacedMachine(machine) ||
                    !SharedDice.Rolls(machine) && !SharedDice.Aims(machine) || (takers ??= []).Count >= MaxTakers) {
                    continue;
                }

                takers.Add(new Taker(
                    PathUnder(machine.transform, told.Target), machine.FsmName, partFsm.ActiveState?.Name ?? ""
                ));
            }

            if (!anyTakes) {
                // Where each of them is, for a part of the other game that has yet to come where this one is
                List<Taker>? resting = null;
                foreach (var machine in told.Machines) {
                    if (machine.Fsm is not { } partFsm || (resting ??= []).Count >= MaxTakers) {
                        continue;
                    }

                    resting.Add(new Taker(
                        PathUnder(machine.transform, told.Target), machine.FsmName, partFsm.ActiveState?.Name ?? ""
                    ));
                }

                HostTells.Add(
                    action, new HostTell(Time.frameCount, Word.NotTaken, "", ThrownThings.NoPlayer, resting)
                );
                return;
            }
        }

        var key = $"{Prefix}.{++_lastNumber}";
        NameMachines(told, key);
        HostTells.Add(action, new HostTell(Time.frameCount, Word.Named, key, CodeOfTarget(creature), takers));
        SayOnce(told.Target, "rolls under a name that goes to the other game with what the creature told it");
    }

    /// <summary>
    /// Names the parts of the room that roll among those registered for what a creature that this game runs broadcasts.
    /// </summary>
    private static void NameBroadcast(FsmStateAction action, GameObject creature, string? eventName) {
        // An event about this game's player is not broadcast again in the other game, so nothing there is named
        if (eventName is not { Length: > 0 } || Entity.Action.EntityFsmActions.IsPlayerEvent(eventName) ||
            FindListeners(eventName) is not { Count: > 0 } listeners) {
            return;
        }

        var key = $"{Prefix}.{++_lastNumber}";
        NameListeners(listeners, key);
        HostTells.Add(action, new HostTell(Time.frameCount, Word.Named, key, CodeOfTarget(creature), null));
    }

    /// <summary>
    /// The number of the player whom a creature goes after, as a throw says it (see ThrownThings.CodeOf): the one its
    /// actions are pointed at, and otherwise this game's player.
    /// </summary>
    private static ushort CodeOfTarget(GameObject creature) {
        var hero = HeroController.SilentInstance;
        var target = GamePatcher.TargetOf(creature) ?? (hero != null ? hero.gameObject : null);
        return ThrownThings.CodeOf?.Invoke(target) ?? ThrownThings.NoPlayer;
    }

    /// <summary>
    /// Writes what an action of a creature that this game runs told, for <see cref="Read"/> in the other game.
    /// </summary>
    /// <param name="data">The data that goes with the action.</param>
    /// <param name="action">The action.</param>
    public static void Write(EntityNetworkData data, FsmStateAction action) {
        HostTells.TryGetValue(action, out var tell);
        HostTells.Remove(action);
        if (tell == null || tell.Frame != Time.frameCount) {
            data.Packet.Write((byte) Word.None);
            return;
        }

        data.Packet.Write((byte) tell.Word);
        if (tell.Word == Word.None) {
            return;
        }

        // A tell that nothing took goes with where the parts were instead of a name, which they never rolled under
        if (tell.Word == Word.Named) {
            data.Packet.Write(tell.Key);
            data.Packet.Write(tell.Target);
        }

        var takers = tell.Takers;
        data.Packet.Write((byte) (takers?.Count ?? 0));
        if (takers == null) {
            return;
        }

        foreach (var taker in takers) {
            data.Packet.Write(taker.Path);
            data.Packet.Write(taker.FsmName);
            data.Packet.Write(taker.StateName);
        }
    }

    /// <summary>
    /// Reads what <see cref="Write"/> wrote, or nothing for an action that is done to set a creature up rather than as
    /// the game that runs it did it.
    /// </summary>
    /// <param name="data">The data that came with the action, or null.</param>
    public static Told Read(EntityNetworkData? data) {
        if (data == null) {
            return default;
        }

        var word = (Word) data.Packet.ReadByte();
        if (word == Word.None) {
            return new Told(word, "", ThrownThings.NoPlayer, null, data.SenderId);
        }

        var key = "";
        var target = ThrownThings.NoPlayer;
        if (word == Word.Named) {
            key = data.Packet.ReadString();
            target = data.Packet.ReadUShort();
        }

        var count = data.Packet.ReadByte();
        List<Taker>? takers = null;
        for (var i = 0; i < count; i++) {
            (takers ??= []).Add(new Taker(
                data.Packet.ReadString(), data.Packet.ReadString(), data.Packet.ReadString()
            ));
        }

        return new Told(word, key, target, takers, data.SenderId);
    }

    /// <summary>
    /// Gets the parts here ready to be told what the scene host's creature told the host's, before the copy of the
    /// creature tells them: names them as the host named its own, has them go after the same player, and puts a part
    /// that is still busy here where the host's was as it took the event.
    /// </summary>
    /// <param name="fsm">The state machine of the copy that tells them.</param>
    /// <param name="eventTarget">Where the event goes.</param>
    /// <param name="eventName">The event.</param>
    /// <param name="told">What came with the creature's action.</param>
    /// <param name="atOnce">Whether the event goes now, rather than after a delay, when the parts are only
    /// named.</param>
    /// <returns>Whether the event is to go here: not when nothing took it in the host's game.</returns>
    public static bool BeforeReplayedTell(Fsm fsm, FsmEventTarget eventTarget, string eventName, Told told,
        bool atOnce) {
        if (told.Word == Word.None) {
            return true;
        }

        try {
            var here = FindTold(fsm, eventTarget);
            if (told.Word == Word.NotTaken) {
                // A tell that waits goes all the same, so only one that goes now catches parts up
                if (here != null && atOnce &&
                    (told.Takers == null || !CatchUp(fsm, here.Target, told.Takers, eventName))) {
                    SayOnce(here.Target, $"is not told '{eventName}', as it let that go by in the scene host's game");
                }

                return false;
            }

            if (here == null) {
                return true;
            }

            // Named after a part is put back, so that what the state it goes back to rolls again is not taken from the
            // name, which the host's part rolls from only after it took the event
            SayOnce(here.Target, "rolls under the name and goes after the player of the scene host's creature");
            AimAt(here, told);
            if (atOnce && told.Takers != null) {
                PutBackToTake(here.Target, told.Takers, eventName);
            }

            NameMachines(here, told.Key);
        } catch (Exception e) {
            SayFailed(e);
        }

        return told.Word != Word.NotTaken;
    }

    /// <summary>
    /// Says in the log what came of a part told by the scene host's creature, once for each part in the room.
    /// </summary>
    private static void SayOnce(Transform part, string what) {
        if (SaidParts.Add(part.GetInstanceID())) {
            Logger.Info($"The part '{ScenePath.Get(part)}' {what}");
        }
    }

    /// <summary>
    /// Forgets which parts were said in the log, as the room is left.
    /// </summary>
    public static void Forget() {
        SaidParts.Clear();
        _failed = false;
    }

    /// <summary>
    /// Names the parts here that roll among those registered for what the scene host's creature broadcast, as the host
    /// named its own, before the copy of the creature broadcasts it.
    /// </summary>
    public static void BeforeReplayedBroadcast(string eventName, Told told) {
        if (told.Word != Word.Named) {
            return;
        }

        try {
            if (FindListeners(eventName) is { Count: > 0 } listeners) {
                NameListeners(listeners, told.Key);
            }
        } catch (Exception e) {
            SayFailed(e);
        }
    }

    /// <summary>
    /// Names what an object of the room that rolls under a name tells, after it, in every game alike (see
    /// SharedDice.NextTellName): called as any state machine sends an event to others.
    /// </summary>
    /// <param name="sender">The state machine that sends the event.</param>
    /// <param name="eventTarget">Where it goes.</param>
    public static void PassOn(Fsm sender, FsmEventTarget? eventTarget) {
        if (eventTarget == null || sender.GameObject is not { } senderObject || senderObject == null ||
            !SharedDice.RollsByName(senderObject)) {
            return;
        }

        try {
            if (FindTold(sender, eventTarget) is not { } told) {
                return;
            }

            // What the sender tells itself rolls under its own name already
            told.Machines.RemoveAll(machine => machine.gameObject == senderObject);
            if (!NamesAny(told.Machines) || SharedDice.NextTellName(senderObject) is not { } name) {
                return;
            }

            NameMachines(told, name);
        } catch (Exception e) {
            SayFailed(e);
        }
    }

    /// <summary>
    /// The parts of the room that an event goes to: the object it goes to, and the state machines there, or there and
    /// under it, that take events from it. Those of creatures, and of anything that isn't a part of the room itself,
    /// are left out (see CoopHits.IsRoomObject). Null if it goes to no object of the room.
    /// </summary>
    private static ToldMachines? FindTold(Fsm sender, FsmEventTarget eventTarget) {
        GameObject? target;
        var children = false;
        string? fsmName = null;
        switch (eventTarget.target) {
            case FsmEventTarget.EventTarget.GameObject:
                target = sender.GetOwnerDefaultTarget(eventTarget.gameObject);
                children = eventTarget.sendToChildren?.Value == true;
                break;
            case FsmEventTarget.EventTarget.GameObjectFSM:
                target = sender.GetOwnerDefaultTarget(eventTarget.gameObject);
                fsmName = eventTarget.fsmName?.Value;
                break;
            case FsmEventTarget.EventTarget.FSMComponent when eventTarget.fsmComponent != null:
                target = eventTarget.fsmComponent.gameObject;
                fsmName = eventTarget.fsmComponent.FsmName;
                break;
            default:
                return null;
        }

        if (target == null || ThrownThings.IsThrown(target)) {
            return null;
        }

        var everything = new List<PlayMakerFSM>();
        var machines = new List<PlayMakerFSM>();
        foreach (var machine in children
                     ? target.GetComponentsInChildren<PlayMakerFSM>()
                     : target.GetComponents<PlayMakerFSM>()) {
            if (machine == null || fsmName != null && machine.FsmName != fsmName) {
                continue;
            }

            everything.Add(machine);
            if (CoopHits.IsRoomObject(machine) && IsFollowedInEveryState?.Invoke(machine) != true) {
                machines.Add(machine);
            }
        }

        return machines.Count == 0 ? null : new ToldMachines(target.transform, machines, everything);
    }

    /// <summary>
    /// Whether any of the state machines rolls by a name it is given here or looks at a player, so that a name goes
    /// with telling them.
    /// </summary>
    private static bool NamesAny(List<PlayMakerFSM> machines) {
        foreach (var machine in machines) {
            if (SharedDice.Rolls(machine) && !IsPacedMachine(machine) || SharedDice.Aims(machine)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a state machine of the room goes by the rounds of the scene host's (see <see cref="IsPaced"/>).
    /// </summary>
    private static bool IsPacedMachine(PlayMakerFSM machine) => IsPaced?.Invoke(machine) == true;

    /// <summary>
    /// Has the objects of the state machines that were told something roll under names made from a name and where each
    /// is under what was told, but for those that go by the scene host's rounds.
    /// </summary>
    private static void NameMachines(ToldMachines told, string name) {
        GameObject? last = null;
        foreach (var machine in told.Machines) {
            var gameObject = machine.gameObject;
            if (gameObject == last || !SharedDice.Rolls(machine) || IsPacedMachine(machine)) {
                continue;
            }

            last = gameObject;
            SharedDice.RollByName(gameObject, name + PartStart + PathUnder(machine.transform, told.Target));
        }
    }

    /// <summary>
    /// Has the parts here that look at a player go after the one that the scene host's creature went after, where they
    /// look at a player, as the host's parts do (see GamePatcher.SetOffFor).
    /// </summary>
    private static void AimAt(ToldMachines told, Told word) {
        if (ThrownThings.FigureOf?.Invoke(word.Target, word.SenderId) is not { } player || player == null) {
            return;
        }

        GameObject? last = null;
        foreach (var machine in told.Machines) {
            if (machine.gameObject == last || !SharedDice.Aims(machine)) {
                continue;
            }

            last = machine.gameObject;
            GamePatcher.SetOffFor(machine.gameObject, player);
        }
    }

    /// <summary>
    /// Puts each part here that took the event in the scene host's game, but would let it go by where it is here, into
    /// the state the host's was in as it took it.
    /// </summary>
    private static void PutBackToTake(Transform target, List<Taker> takers, string eventName) {
        foreach (var taker in takers) {
            var part = taker.Path.Length == 0 ? target : target.Find(taker.Path);
            if (part == null) {
                continue;
            }

            foreach (var machine in part.GetComponents<PlayMakerFSM>()) {
                if (machine == null || machine.FsmName != taker.FsmName || machine.Fsm is not { } fsm ||
                    Takes(fsm, eventName) || taker.StateName.Length == 0 || fsm.GetState(taker.StateName) == null ||
                    IsPacedMachine(machine)) {
                    continue;
                }

                Logger.Info(
                    $"The part '{part.name}' goes back to '{taker.StateName}' from '{fsm.ActiveState?.Name}' to " +
                    $"take '{eventName}' of the scene host's creature, as the host's did"
                );
                fsm.SetState(taker.StateName);
            }
        }
    }

    /// <summary>
    /// The most states a part is followed through, on from the state an event takes it to, to find where it comes to
    /// rest (see <see cref="CatchUp"/>).
    /// </summary>
    private const int MostStepsToRest = 10;

    /// <summary>
    /// The fields that hold events of each type of action, for <see cref="LeavesByItself"/>.
    /// </summary>
    private static readonly Dictionary<Type, FieldInfo[]> EventFields = new();

    /// <summary>
    /// Tells the parts here an event that nothing took in the scene host's game, each part on its own, where it has yet
    /// to come where the host's part is: where the event takes this part, and the states it goes on to by itself from
    /// there, lead through the state the host's part is in, or to it, and come to rest. That part took an event like it
    /// earlier, which never came here, and a gate the host's player saw close stayed open here for good: one closed
    /// already, and one still closing, as the gates that kept the host's player in the room while it waited for the
    /// others were as the fight began. A part that the host's game had busy with anything else, or at rest anywhere
    /// else, lets it go by as the host's did, and so does a machine that goes by the host's rounds, which its rounds
    /// bring along (see <see cref="IsPaced"/>). The event goes as one that the host's creature sent, so a gate it
    /// closes waits for this game's player to walk into the room (see BossRoomCoop.RunAsNetworkSender).
    /// </summary>
    /// <param name="sender">The state machine of the copy of the creature that tells the parts.</param>
    /// <param name="target">What the event went to.</param>
    /// <param name="resting">Where each part was in the scene host's game as it let the event go by.</param>
    /// <param name="eventName">The event.</param>
    /// <returns>Whether any part here was told.</returns>
    private static bool CatchUp(Fsm sender, Transform target, List<Taker> resting, string eventName) {
        var told = false;
        foreach (var host in resting) {
            var part = host.Path.Length == 0 ? target : target.Find(host.Path);
            if (part == null || host.StateName.Length == 0) {
                continue;
            }

            foreach (var machine in part.GetComponents<PlayMakerFSM>()) {
                if (machine == null || machine.FsmName != host.FsmName || machine.Fsm is not { } fsm ||
                    IsPacedMachine(machine) || !Takes(fsm, eventName) ||
                    !ComesToRestThrough(fsm, eventName, host.StateName) ||
                    FsmEvent.FindEvent(eventName) is not { } fsmEvent) {
                    continue;
                }

                Logger.Info(
                    $"The part '{ScenePath.Get(part)}' is told '{eventName}' from '{fsm.ActiveState?.Name}' all the " +
                    $"same, as the scene host's is in '{host.StateName}' already, on the way that this one takes it"
                );
                BossRoomCoop.RunAsNetworkSender(sender, () => fsm.Event(fsmEvent));
                told = true;
            }
        }

        return told;
    }

    /// <summary>
    /// Whether an event takes a state machine, from where it is, through only the states it goes on to by itself
    /// (FINISHED), to a state that waits to be told something, passing the given state on the way or coming to rest in
    /// it. A state waits to be told only where no way out of it goes on FINISHED or on an event that one of its own
    /// actions sends.
    /// </summary>
    /// <param name="fsm">The state machine.</param>
    /// <param name="eventName">The event.</param>
    /// <param name="stateName">The state.</param>
    private static bool ComesToRestThrough(Fsm fsm, string eventName, string stateName) {
        var passed = false;
        var next = WayOut(fsm.ActiveState?.Transitions, eventName) ?? WayOut(fsm.GlobalTransitions, eventName);
        for (var step = 0; next != null && step < MostStepsToRest; step++) {
            if (fsm.GetState(next) is not { } state) {
                return false;
            }

            passed |= next == stateName;
            if (WayOut(state.Transitions, FsmEvent.Finished.Name) is not { } onward) {
                return passed && !LeavesByItself(state);
            }

            next = onward;
        }

        return false;
    }

    /// <summary>
    /// Whether an action of a state holds an event that a way out of the state goes on, which it may send itself: a
    /// wait, a check or a trigger that leaves the state without being told.
    /// </summary>
    private static bool LeavesByItself(FsmState state) {
        if (state.Transitions is not { Length: > 0 } transitions || state.Actions is not { } actions) {
            return false;
        }

        foreach (var action in actions) {
            if (action == null || !action.Enabled) {
                continue;
            }

            foreach (var field in EventFieldsOf(action.GetType())) {
                switch (field.GetValue(action)) {
                    case FsmEvent fsmEvent when WayOut(transitions, fsmEvent.Name) != null:
                        return true;
                    case FsmEvent[] fsmEvents:
                        foreach (var each in fsmEvents) {
                            if (each != null && WayOut(transitions, each.Name) != null) {
                                return true;
                            }
                        }

                        break;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The fields of a type of action that hold an event or a list of events, found once for each type.
    /// </summary>
    private static FieldInfo[] EventFieldsOf(Type actionType) {
        if (EventFields.TryGetValue(actionType, out var fields)) {
            return fields;
        }

        var found = new List<FieldInfo>();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        for (var type = actionType; type != null && type != typeof(FsmStateAction); type = type.BaseType) {
            foreach (var field in type.GetFields(flags | BindingFlags.DeclaredOnly)) {
                if (field.FieldType == typeof(FsmEvent) || field.FieldType == typeof(FsmEvent[])) {
                    found.Add(field);
                }
            }
        }

        return EventFields[actionType] = found.ToArray();
    }

    /// <summary>
    /// The state that one of the given ways out goes to on the given event, or null for none.
    /// </summary>
    private static string? WayOut(FsmTransition[]? transitions, string eventName) {
        if (transitions == null) {
            return null;
        }

        foreach (var transition in transitions) {
            if (transition?.EventName == eventName) {
                return transition.ToState;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a state machine takes an event where it is: its state, or the machine as a whole, has a way out on it.
    /// </summary>
    private static bool Takes(Fsm fsm, string eventName) {
        return HasWayOut(fsm.ActiveState?.Transitions, eventName) || HasWayOut(fsm.GlobalTransitions, eventName);
    }

    /// <summary>
    /// Whether one of the given ways out goes on the given event.
    /// </summary>
    private static bool HasWayOut(FsmTransition[]? transitions, string eventName) {
        if (transitions == null) {
            return false;
        }

        foreach (var transition in transitions) {
            if (transition?.EventName == eventName) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The objects of the room registered for an event that roll, each once: the parts that a broadcast of it would
    /// name. Those of creatures and anything that isn't a part of the room itself are left out.
    /// </summary>
    private static List<GameObject>? FindListeners(string eventName) {
        if (EventRegistersField?.GetValue(null) is not Dictionary<int, List<EventRegister>> registers ||
            !registers.TryGetValue(EventRegister.GetEventHashCode(eventName), out var list)) {
            return null;
        }

        List<GameObject>? listeners = null;
        foreach (var register in list) {
            if (register == null || !CoopHits.IsRoomObject(register)) {
                continue;
            }

            var gameObject = register.gameObject;
            if (listeners?.Contains(gameObject) == true) {
                continue;
            }

            foreach (var machine in gameObject.GetComponents<PlayMakerFSM>()) {
                if (machine != null && SharedDice.Rolls(machine)) {
                    (listeners ??= []).Add(gameObject);
                    break;
                }
            }
        }

        return listeners;
    }

    /// <summary>
    /// Has objects of the room registered for a broadcast roll under names made from a name and their places in the
    /// room, the same in every game.
    /// </summary>
    private static void NameListeners(List<GameObject> listeners, string name) {
        foreach (var listener in listeners) {
            if (listener != null) {
                SharedDice.RollByName(listener, name + PartStart + ScenePath.Get(listener.transform));
            }
        }
    }

    /// <summary>
    /// The path of a part under what was told, by the names of the objects between them; empty for that itself.
    /// </summary>
    private static string PathUnder(Transform part, Transform target) {
        if (part == target) {
            return "";
        }

        var names = new List<string>();
        for (var current = part; current != null && current != target; current = current.parent) {
            names.Add(current.name);
        }

        names.Reverse();
        return string.Join("/", names);
    }

    /// <summary>
    /// Says in the log, once in each room, that naming what a creature told went wrong.
    /// </summary>
    private static void SayFailed(Exception e) {
        if (_failed) {
            return;
        }

        _failed = true;
        Logger.Error($"Could not name what a creature told:\n{e}");
    }

    /// <summary>
    /// The game's own lists of what is registered for each event, by the hash of the event's name.
    /// </summary>
    private static readonly FieldInfo? EventRegistersField =
        typeof(EventRegister).GetField("_eventRegister", BindingFlags.Static | BindingFlags.NonPublic);

    /// <summary>
    /// What an action of a creature told, as it came with the action: what it says, the name, the player, the parts
    /// that took the event in the scene host's game (where none took it: where the parts were), and who sent it.
    /// </summary>
    public readonly struct Told(Word word, string key, ushort target, List<Taker>? takers, ushort senderId) {
        public Word Word { get; } = word;
        public string Key { get; } = key;
        public ushort Target { get; } = target;
        public List<Taker>? Takers { get; } = takers;
        public ushort SenderId { get; } = senderId;
    }

    /// <summary>
    /// A part that took an event in the scene host's game, or one that let it go by where nothing took it: where it is
    /// under what was told, its state machine and the state that was in.
    /// </summary>
    public sealed class Taker(string path, string fsmName, string stateName) {
        public string Path { get; } = path;
        public string FsmName { get; } = fsmName;
        public string StateName { get; } = stateName;
    }

    /// <summary>
    /// What an action of a creature that this game runs told, in the frame it ran in.
    /// </summary>
    private sealed class HostTell(int frame, Word word, string key, ushort target, List<Taker>? takers) {
        public int Frame { get; } = frame;
        public Word Word { get; } = word;
        public string Key { get; } = key;
        public ushort Target { get; } = target;
        public List<Taker>? Takers { get; } = takers;
    }

    /// <summary>
    /// The object an event went to, the state machines of the room there that take events from it, and every state
    /// machine there that takes events from it, of the room or not.
    /// </summary>
    private sealed class ToldMachines(Transform target, List<PlayMakerFSM> machines, List<PlayMakerFSM> everything) {
        public Transform Target { get; } = target;
        public List<PlayMakerFSM> Machines { get; } = machines;
        public List<PlayMakerFSM> Everything { get; } = everything;
    }
}
