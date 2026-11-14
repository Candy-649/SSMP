using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Objects of a room that pick at random how they start as the room loads: the beast of a bellway station starts
/// awake, or one time in five asleep. Each game picked for itself, so on about one visit in three the players saw it
/// asleep in one game and awake in the other (USER 11-04 "我们坐电梯切换房间的时候有时候看到钟道兽的形态不一致"). The
/// couriers who hand out deliveries pick the same way, and asleep they cannot be spoken to until a player sings to them
/// or strikes them: one player could talk to them and the other had no prompt (USER 11-14 "我朋友对话了我就对话不了了",
/// "直接没有对话的键").
///
/// How the room has them is the scene host's now, the way the room's machines go by the host's (see
/// CoopSave.PacedMachines). Each game notes how its own started. The scene host tells the others in the room as its
/// own starts, and tells whoever walks in later how they all started. The other game starts its own the way the host's
/// did instead of picking, and where the word isn't there yet, waits for it where it picks, for up to
/// <see cref="StartPickWait"/>. Only the start: what a player does to such an object later, the way they wake the
/// beast, each game has as it comes.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How long an object here waits where it picks for the scene host's word of how it started, in seconds, before it
    /// goes the way it picked. The host tells it as the local player walks in, which is as the room here is there: the
    /// word comes a round trip after that, well before an object here picks, unless the two games loaded the room
    /// within a round trip of each other, and then it comes as the host's own object picks.
    /// </summary>
    private const float StartPickWait = 3f;

    /// <summary>
    /// The objects that pick how they start, each by its name, its state machine that picks, the state where it picks,
    /// the states that its pick goes into, and the starts that the other ways out of that state come to: they aren't a
    /// pick, but are how the room has the object for whoever walks in after.
    /// </summary>
    private static readonly StartPickKind[] StartPickKinds = [
        // The beast of a bellway station: awake or asleep, by a pick of four to one, except at the one station where it
        // always sleeps. It wakes for a player who came by teleport without a pick, and is awake once it has carried a
        // player in.
        new(
            "Bone Beast NPC", "Interaction", "Start State", ["Wake Up", "Start Asleep"],
            new Dictionary<string, string> { ["Wait Finished Entering"] = "Wake Up" }
        ),

        // The couriers who hand out deliveries: awake, or one time in five asleep, when nobody can speak to them until
        // a player sings to them or strikes them
        new(
            "Couriers Quest Giver", "Dialogue", "Start State?", ["Idle", "Start Asleep"],
            new Dictionary<string, string>()
        )
    ];

    /// <summary>
    /// The states where the objects of <see cref="StartPickKinds"/> pick, kept as a set because the hook this serves is
    /// called for every change of state of every state machine in the game.
    /// </summary>
    private static readonly HashSet<string> StartPickStateNames =
        new(StartPickKinds.Select(kind => kind.PickStateName), StringComparer.Ordinal);

    /// <summary>
    /// How each object of the room here that picks how it starts started in this game, by its key (see
    /// <see cref="StartPickKey"/>): as it picked, as the scene host's word had it, or as a way out of where it picks
    /// that isn't a pick has it. The scene host tells these to whoever walks in.
    /// </summary>
    private readonly Dictionary<string, (string Scene, string ObjectPath, string FsmName, string StateName)>
        _startPicksHere = new();

    /// <summary>
    /// How the scene host's objects of the room started, by key, as its words said.
    /// </summary>
    private readonly Dictionary<string, string> _toldStartPicks = new();

    /// <summary>
    /// The objects here that wait where they pick for the scene host's word: the key, the object by its place, the
    /// state where it picks, the state that its own pick went to, and since when it waits, in unscaled time.
    /// </summary>
    private readonly Dictionary<Fsm, (string Key, (string Scene, string ObjectPath, string FsmName) Place,
        FsmState From, FsmState Own, float Since)> _heldStartPicks = new();

    /// <summary>
    /// The members that the scene host saw in the room as it last looked, by ID, so that it tells one who walks in.
    /// </summary>
    private readonly HashSet<ushort> _startPickMembers = [];

    /// <summary>
    /// The object that is let out of where it picks, into the scene host's start or its own, past the hook.
    /// </summary>
    private Fsm? _releasingStartPick;

    /// <summary>
    /// What a change of state out of the state where an object picks how it starts does (see
    /// <see cref="StartPickKinds"/>). Its start is noted, and told to the others in the room by the scene host, or by a
    /// game with nobody else in the room. In another game, the object goes the way the scene host's started instead of
    /// the way it picked, or waits where it is for the host's word. A way out that isn't a pick, like the beast
    /// carrying the local player in, goes as it goes.
    /// </summary>
    /// <param name="fsm">The state machine that changes state.</param>
    /// <param name="toState">The state it changes into.</param>
    /// <returns>The state it goes into, which may be another one, or null when it waits where it is.</returns>
    private FsmState? TakeStartPick(Fsm fsm, FsmState toState) {
        if (_checkedMembers.Count == 0 || MachineSwitchToStateField == null || fsm == _releasingStartPick ||
            fsm.ActiveState is not { } from || !StartPickStateNames.Contains(from.Name) ||
            fsm.GameObject is not { } gameObject || FindStartPickKind(gameObject, fsm.Name) is not { } kind ||
            from.Name != kind.PickStateName || kind.StartOf(toState.Name) is not { } start) {
            return toState;
        }

        (string Scene, string ObjectPath, string FsmName) place =
            (gameObject.scene.name, ScenePath.Get(gameObject.transform), fsm.Name);
        var key = StartPickKey(place.Scene, place.ObjectPath, place.FsmName);
        if (Array.IndexOf(kind.PickedStateNames, toState.Name) < 0 || IsSceneHost?.Invoke() == true ||
            !IsMemberInRoom()) {
            var how = start == toState.Name ? "as it picked" : $"as it went to '{toState.Name}'";
            NoteStartPick(key, place, start, how);
            return toState;
        }

        if (_toldStartPicks.TryGetValue(key, out var told) && fsm.GetState(told) is { } toldState) {
            NoteStartPick(key, place, told, AsTheHostsDid(toState.Name, told));
            return toldState;
        }

        if (!_heldStartPicks.ContainsKey(fsm)) {
            _heldStartPicks[fsm] = (key, place, from, toState, Time.unscaledTime);
            Logger.Info(
                $"'{place.ObjectPath}' in '{place.Scene}' waits where it picks how it starts for the word of how the " +
                $"scene host's started, rather than start as it picked, '{toState.Name}'"
            );
        }

        return null;
    }

    /// <summary>
    /// Notes how an object here started, says so, and in the scene host's game tells the others in the room.
    /// </summary>
    private void NoteStartPick(
        string key,
        (string Scene, string ObjectPath, string FsmName) place,
        string stateName,
        string how
    ) {
        _startPicksHere[key] = (place.Scene, place.ObjectPath, place.FsmName, stateName);
        var host = IsSceneHost?.Invoke() == true;
        Logger.Info(
            $"'{place.ObjectPath}' in '{place.Scene}' starts in '{stateName}' here, {how}" +
            (host ? ", which the others in the room take" : "")
        );

        if (host) {
            SendToRoomMembers(StartPickWord(_startPicksHere[key]));
        }
    }

    /// <summary>
    /// In the scene host's game, tells a member who walked into the room how the objects that pick how they start
    /// started here. In the other game, lets an object that waits where it picks go the host's way once the word came,
    /// or its own way once this game runs the room, or after <see cref="StartPickWait"/>.
    /// </summary>
    private void UpdateStartPicks() {
        if (IsSceneHost?.Invoke() == true) {
            TellStartPicksToWalkIns();
        } else {
            _startPickMembers.Clear();
        }

        if (_heldStartPicks.Count == 0) {
            return;
        }

        foreach (var (fsm, held) in _heldStartPicks.ToList()) {
            // Gone, or sent on from where it picks by something else meanwhile
            if (fsm.GameObject == null || fsm.ActiveState != held.From) {
                _heldStartPicks.Remove(fsm);
                continue;
            }

            string start;
            string how;
            if (_toldStartPicks.TryGetValue(held.Key, out var told) && fsm.GetState(told) != null) {
                start = told;
                how = AsTheHostsDid(held.Own.Name, told);
            } else if (IsSceneHost?.Invoke() == true || !IsMemberInRoom()) {
                start = held.Own.Name;
                how = "as it picked, since nobody else runs the room now";
            } else if (Time.unscaledTime - held.Since >= StartPickWait) {
                start = held.Own.Name;
                how = $"as it picked, since no word of how the scene host's started came in {StartPickWait} s";
            } else {
                continue;
            }

            _heldStartPicks.Remove(fsm);
            NoteStartPick(held.Key, held.Place, start, how);
            _releasingStartPick = fsm;
            try {
                fsm.SetState(start);
            } finally {
                _releasingStartPick = null;
            }
        }
    }

    /// <summary>
    /// Tells a member who walked into the room of the scene host how the objects of the room that pick how they start
    /// started here, to everyone in the room. A member who left and comes back walks in anew.
    /// </summary>
    private void TellStartPicksToWalkIns() {
        var walkedIn = false;
        var inRoom = 0;
        foreach (var id in _checkedMembers) {
            if (_playerData.TryGetValue(id, out var member) && member.IsInLocalScene) {
                inRoom++;
                walkedIn |= _startPickMembers.Add(id);
            } else {
                _startPickMembers.Remove(id);
            }
        }

        // A player who is no longer one of them walks in anew if they come back
        if (_startPickMembers.Count != inRoom) {
            _startPickMembers.IntersectWith(_checkedMembers);
        }

        if (!walkedIn) {
            return;
        }

        foreach (var start in _startPicksHere.Values) {
            Logger.Info(
                $"Telling the players who walked in that '{start.ObjectPath}' in '{start.Scene}' started in " +
                $"'{start.StateName}' here"
            );
            SendToRoomMembers(StartPickWord(start));
        }
    }

    /// <summary>
    /// Takes the scene host's word of how an object of the room started, which an object here that picks how it starts
    /// takes as it picks, or as it waits where it picks.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update, which names the object by its path in its scene.</param>
    private void OnStartPick(ClientPlayerData player, CoopSaveUpdate update) {
        if (!_checkedMembers.Contains(player.Id) || !player.IsInLocalScene || IsSceneHost?.Invoke() != false) {
            return;
        }

        var key = StartPickKey(update.Scene, update.ObjectPath, update.FsmName);
        _toldStartPicks[key] = update.StateName;

        // One here that started another way already, before the word came, stays as it started
        var late = _startPicksHere.TryGetValue(key, out var start) && start.StateName != update.StateName
            ? $", where it started in '{start.StateName}' already, which stays"
            : "";
        Logger.Info(
            $"The scene host's '{update.ObjectPath}' in '{update.Scene}' started in '{update.StateName}'{late}"
        );
    }

    /// <summary>
    /// Forgets how the objects of the room started, for a new room.
    /// </summary>
    private void ResetStartPicks() {
        _startPicksHere.Clear();
        _toldStartPicks.Clear();
        _heldStartPicks.Clear();
        _startPickMembers.Clear();
        _releasingStartPick = null;
    }

    /// <summary>
    /// How an object here that starts the way the scene host's did started, for the log, by what it picked itself.
    /// </summary>
    private static string AsTheHostsDid(string picked, string told) {
        return picked == told
            ? "as the scene host's did, which it picked too"
            : $"as the scene host's did, rather than as it picked, '{picked}'";
    }

    /// <summary>
    /// The word of how an object of the room started.
    /// </summary>
    private static CoopSaveUpdate StartPickWord(
        (string Scene, string ObjectPath, string FsmName, string StateName) start
    ) {
        return new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.StartPick,
            Scene = start.Scene,
            ObjectPath = start.ObjectPath,
            FsmName = start.FsmName,
            StateName = start.StateName
        };
    }

    /// <summary>
    /// The key of an object that picks how it starts: its scene, its path in it and the state machine that picks,
    /// which are the same in every game.
    /// </summary>
    private static string StartPickKey(string scene, string objectPath, string fsmName) {
        return scene + "|" + objectPath + "|" + fsmName;
    }

    /// <summary>
    /// The kind of an object that picks how it starts, by its name and the name of the state machine, if it is one.
    /// </summary>
    private static StartPickKind? FindStartPickKind(GameObject gameObject, string fsmName) {
        foreach (var kind in StartPickKinds) {
            if (kind.FsmName == fsmName && kind.ObjectName == gameObject.name) {
                return kind;
            }
        }

        return null;
    }

    /// <summary>
    /// An object that picks how it starts (see <see cref="StartPickKinds"/>).
    /// </summary>
    /// <param name="objectName">The name of the object.</param>
    /// <param name="fsmName">The name of its state machine that picks.</param>
    /// <param name="pickStateName">The state where it picks.</param>
    /// <param name="pickedStateNames">The states that its pick goes into.</param>
    /// <param name="otherWays">The starts that the other ways out of where it picks come to, by the state each goes
    /// into.</param>
    private sealed class StartPickKind(
        string objectName,
        string fsmName,
        string pickStateName,
        string[] pickedStateNames,
        Dictionary<string, string> otherWays
    ) {
        public string ObjectName { get; } = objectName;

        public string FsmName { get; } = fsmName;

        public string PickStateName { get; } = pickStateName;

        public string[] PickedStateNames { get; } = pickedStateNames;

        /// <summary>
        /// The start that going from where the object picks into a state comes to, or null for a state that isn't one.
        /// </summary>
        public string? StartOf(string stateName) {
            return Array.IndexOf(PickedStateNames, stateName) >= 0 ? stateName
                : otherWays.TryGetValue(stateName, out var start) ? start
                : null;
        }
    }
}
