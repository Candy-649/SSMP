using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Controllers of a fight whose parts the other game shows the way they go up in the scene host's game, in a checked
/// two-player save. In the fight on the stage, two controllers of the room do what the creature only sets going, and
/// their own clocks set them going again too: one sets off flares over the stage that burst in rings that hurt, picking
/// from its list which go up, how high and after what wait, and each flare as it goes up rolls how far to one side it
/// bursts and how large; the other bursts the floor open in a row of its pick, each burst after a wait of its own. The
/// controllers are no creatures, so each game ran its own, and the rings and the bursts came at other places and times
/// for each player (USER 10-13).
///
/// Only the scene host's game rolls (USER 10-13: 要么就房主投骰子，客机只同步骰子骰了啥). Its controllers go on as the
/// game has them, and as each part that they switch on goes up - a flare, a burst of the floor - it tells the other
/// game which part it is and where it stands, and for a part that starts a state machine of its own as it goes up,
/// what it came to as it started: how large it is, where its pieces are and what its flags say, like whether a flare
/// hurts. A part that the controller moves while it is up is told too. The other game's controllers find their parts
/// and do nothing more - each is held from its first step on, as the states of a controller of a fight are - and its
/// parts go up as the host's words come. Nothing is rolled or worked out again there (USER 10-13: 烟花的位置不能直接
/// 拿到吗); from there each part runs by itself, the way the host's does: a flare waits, bursts and goes out, a burst
/// plays and goes out. What a part does to a player, it does in that player's game, as before.
///
/// The words keep the time between them in the host's game, so words that come together after the network stalled go
/// up one after another as they did there, rather than all at once (USER 10-13: 网络波动一下). A word after a pause
/// longer than any wait within a round goes up as it comes, so a stall leaves nothing behind after the round. A part
/// that is still up here from before goes up afresh, as the host's did. A part that the host's controller switches off
/// early goes out here at its own time.
///
/// The parts are the objects under the controller that its variables hold, since those are what its actions switch on
/// and move. A part that was up already when the partner came in is not told, nor one that went up before this game
/// ran the room.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The longest time between two words of the parts of one controller, in seconds, that is kept between them here:
    /// longer than any wait between the parts of one round, shorter than the pause between rounds.
    /// </summary>
    private const float ShownPartSpacingLimit = 1.5f;

    /// <summary>
    /// How far a part that is up has to move, squared, in units, to be told.
    /// </summary>
    private const float ShownPartMoveSqr = 0.0001f;

    /// <summary>
    /// The controllers here whose parts the other game shows, with what is known of their parts.
    /// </summary>
    private readonly Dictionary<Fsm, ShownController> _shownControllers = new();

    /// <summary>
    /// The controllers of <see cref="_shownControllers"/>, gathered to be gone through each frame, since showing a part
    /// can note another controller.
    /// </summary>
    private readonly List<Fsm> _shownControllerList = [];

    /// <summary>
    /// Notes a controller whose parts the other game shows, the first time it is seen.
    /// </summary>
    private ShownController NoteShownController(Fsm fsm) {
        if (!_shownControllers.TryGetValue(fsm, out var shown)) {
            shown = new ShownController();
            _shownControllers[fsm] = shown;
        }

        return shown;
    }

    /// <summary>
    /// Tells the partner the parts of the controllers here that went up or moved since the last frame, while this game
    /// runs the room and the partner is in it; and otherwise puts up the parts of the scene host's words that are due.
    /// </summary>
    private void UpdateShownParts() {
        if (_shownControllers.Count == 0) {
            return;
        }

        var isHost = IsSceneHost?.Invoke() == true;
        var runsTheRoom = _checkedMembers.Count == 0 || isHost || !IsMemberInRoom();
        var tells = _checkedMembers.Count > 0 && isHost && IsMemberInRoom();

        _shownControllerList.Clear();
        _shownControllerList.AddRange(_shownControllers.Keys);
        foreach (var fsm in _shownControllerList) {
            if (!_shownControllers.TryGetValue(fsm, out var shown)) {
                continue;
            }

            if (fsm.GameObject is not { } controller || controller == null) {
                _shownControllers.Remove(fsm);
                continue;
            }

            if (runsTheRoom) {
                // Words of a scene host that this game took over from are no longer the room's
                shown.Due.Clear();
                WatchParts(fsm, controller.transform, shown, tells);
            } else {
                // What this game knew of its own parts is not what the host's are
                shown.Parts.Clear();
                shown.LastTold = -1f;
                ShowDueParts(fsm, shown);
            }
        }
    }

    /// <summary>
    /// Finds the parts of a controller here that went up or moved since the last frame and tells the partner, if it
    /// tells. A part seen for the first time is only noted.
    /// </summary>
    private void WatchParts(Fsm fsm, Transform controller, ShownController shown, bool tells) {
        foreach (var variable in fsm.Variables.GameObjectVariables) {
            if (variable?.Value is not { } gameObject || gameObject == null) {
                continue;
            }

            var part = gameObject.transform;
            if (part == controller || !part.IsChildOf(controller)) {
                continue;
            }

            var isUp = gameObject.activeInHierarchy;
            if (tells && isUp && shown.Parts.TryGetValue(part, out var was)) {
                if (!was.IsUp) {
                    TellPart(fsm, controller, shown, part, true);
                } else if ((part.position - was.Place).sqrMagnitude > ShownPartMoveSqr) {
                    TellPart(fsm, controller, shown, part, false);
                }
            }

            shown.Parts[part] = (isUp, part.position);
        }
    }

    /// <summary>
    /// Tells the partner that a part of a controller went up or moved here. A part that went up and has a state machine
    /// that hasn't started yet starts now, as it would before it first updates, so that what it comes to as it starts
    /// goes with the word.
    /// </summary>
    private void TellPart(Fsm fsm, Transform controller, ShownController shown, Transform part, bool wentUp) {
        try {
            // A creature among the parts goes where its own sync puts it
            if (EntityProcessor.IsRegistered(part.gameObject)) {
                return;
            }

            if (wentUp) {
                StartMachinesUnder(part);
            }

            var now = Time.time;
            var place = part.position;
            var word = new CoopSaveUpdate {
                TargetId = CoopTargets.Everyone,
                Kind = CoopSaveUpdateKind.MachinePart,
                Scene = controller.gameObject.scene.name,
                ObjectPath = ScenePath.Get(controller),
                FsmName = fsm.Name,
                StateName = PathUnder(part, controller),
                Part = (ushort) (wentUp ? 1 : 0),
                Sequence = ++_machineRoundCount,
                Values = [shown.LastTold < 0f ? 0f : now - shown.LastTold, place.x, place.y]
            };
            shown.LastTold = now;

            if (wentUp && part.GetComponent<PlayMakerFSM>() != null) {
                var size = part.localScale;
                word.Values.Add(size.x);
                word.Values.Add(size.y);
                foreach (Transform piece in part) {
                    var piecePlace = piece.localPosition;
                    var pieceSize = piece.localScale;
                    word.Values.Add(piecePlace.x);
                    word.Values.Add(piecePlace.y);
                    word.Values.Add(pieceSize.x);
                    word.Values.Add(pieceSize.y);
                }

                foreach (var machine in part.GetComponents<PlayMakerFSM>()) {
                    foreach (var flag in machine.FsmVariables.BoolVariables) {
                        word.Names.Add(machine.FsmName + "\n" + flag.Name);
                        word.Amounts.Add(flag.Value ? 1 : 0);
                    }
                }
            }

            Send(word);
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Takes the scene host's word that a part of a controller went up or moved: it goes up here when its time comes,
    /// the time between it and the word before kept as it was in the host's game, unless that was a pause.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update.</param>
    private void OnMachinePart(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker) ||
            !_checkedMembers.Contains(player.Id) || IsSceneHost?.Invoke() == true || update.Values.Count < 3) {
            return;
        }

        if (ScenePath.Find(update.ObjectPath, update.Scene) is not { activeInHierarchy: true } target ||
            FindMachineKind(target, update.FsmName) is not { ShowsParts: true } ||
            FindMachineFsm(target, update.FsmName) is not { } fsm) {
            return;
        }

        if (_takenMachineRounds.TryGetValue((fsm, player.Id), out var taken) && update.Sequence <= taken) {
            return;
        }

        _takenMachineRounds[(fsm, player.Id)] = update.Sequence;

        var shown = NoteShownController(fsm);
        var now = Time.time;
        var gap = update.Values[0];
        var due = gap is >= 0f and <= ShownPartSpacingLimit ? Mathf.Max(now, shown.LastShown + gap) : now;
        shown.LastShown = due;
        shown.Due.Add((due, update));
    }

    /// <summary>
    /// Puts up the parts of the scene host's words for a controller here whose time has come, in the order they came.
    /// </summary>
    private void ShowDueParts(Fsm fsm, ShownController shown) {
        var now = Time.time;
        while (shown.Due.Count > 0 && shown.Due[0].Due <= now) {
            var word = shown.Due[0].Word;
            shown.Due.RemoveAt(0);
            try {
                ShowPart(fsm, word);
            } catch (Exception e) {
                LogInteractionError(e);
            }
        }
    }

    /// <summary>
    /// Puts a part of a controller here where the scene host's word says the host's is, and for one that went up there,
    /// puts it up here: with the host's flags, afresh if it is still up from before, started at once, and then as large
    /// and with its pieces where the host's came to as it started.
    /// </summary>
    private void ShowPart(Fsm fsm, CoopSaveUpdate word) {
        if (fsm.GameObject is not { } controller || controller == null ||
            controller.transform.Find(word.StateName) is not { } part ||
            EntityProcessor.IsRegistered(part.gameObject)) {
            return;
        }

        var values = word.Values;
        for (var i = 0; i < word.Names.Count && i < word.Amounts.Count; i++) {
            var name = word.Names[i];
            var split = name.IndexOf('\n');
            if (split < 0) {
                continue;
            }

            var machineName = name[..split];
            var flagName = name[(split + 1)..];
            foreach (var machine in part.GetComponents<PlayMakerFSM>()) {
                if (machine.FsmName == machineName && machine.FsmVariables.FindFsmBool(flagName) is { } flag) {
                    flag.Value = word.Amounts[i] != 0;
                }
            }
        }

        part.position = new Vector3(values[1], values[2], part.position.z);
        if (word.Part != 1) {
            return;
        }

        if (part.gameObject.activeSelf) {
            part.gameObject.SetActive(false);
        }

        part.gameObject.SetActive(true);
        StartMachinesUnder(part);

        if (values.Count < 5) {
            return;
        }

        part.localScale = new Vector3(values[3], values[4], part.localScale.z);
        var index = 5;
        foreach (Transform piece in part) {
            if (index + 4 > values.Count) {
                break;
            }

            piece.localPosition = new Vector3(values[index], values[index + 1], piece.localPosition.z);
            piece.localScale = new Vector3(values[index + 2], values[index + 3], piece.localScale.z);
            index += 4;
        }
    }

    /// <summary>
    /// The path of a part under its controller, by the names of the objects between them.
    /// </summary>
    private static string PathUnder(Transform part, Transform controller) {
        var names = new List<string>();
        for (var current = part; current != null && current != controller; current = current.parent) {
            names.Add(current.name);
        }

        names.Reverse();
        return string.Join("/", names);
    }

    /// <summary>
    /// Forgets the controllers of the room that was left.
    /// </summary>
    private void ResetShownParts() {
        _shownControllers.Clear();
        _shownControllerList.Clear();
    }

    /// <summary>
    /// What is known of a controller whose parts the other game shows.
    /// </summary>
    private sealed class ShownController {
        /// <summary>
        /// In the game that runs the room, each part as it was last frame: whether it was up and where.
        /// </summary>
        public readonly Dictionary<Transform, (bool IsUp, Vector3 Place)> Parts = new();

        /// <summary>
        /// The scene host's words that wait here for their time, in the order they came, with their times.
        /// </summary>
        public readonly List<(float Due, CoopSaveUpdate Word)> Due = [];

        /// <summary>
        /// When this game last told a part of it, in game time, or -1 for not yet.
        /// </summary>
        public float LastTold = -1f;

        /// <summary>
        /// When the last of the host's words here was due, in game time.
        /// </summary>
        public float LastShown = float.NegativeInfinity;
    }
}
