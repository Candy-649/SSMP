using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The saved things that break, when the partner broke one that still stands here: the walls, doors, vines and crusts
/// that give way after a number of strikes, and every breakable object of the game's own kind (<see cref="Breakable"/>).
/// Each game counts the hits of both players on its own (see <see cref="CoopHits"/>), and the two counts can come out
/// apart: a wall broke in one game and still stood in the other, where the player had to strike it again (USER 10-29,
/// "两边没有同时破墙，我都进去了，她还在外面打门"). The save took the break at once (see OnWorldChange), but the thing
/// showed it only once its room loaded again.
/// It is now put the way its room loads it when it is broken, which is the game's own way of showing a break that
/// happened before: at once when it stands still between hits, otherwise as soon as it does, so that a hit or a break of
/// its own that it is still playing is not cut off, and by its own look at its flag when it is still starting. When both
/// games break it together, as they do whenever the counts agree, it is broken here before the partner's word comes, and
/// nothing is done. The line written for it carries the hits that went between the games for it, which show the hit that
/// one game did not count.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The flag of a state machine that keeps whether its saved object is set (PersistentBoolItem.GetValueFromFSM).
    /// </summary>
    private const string SavedFlagName = "Activated";

    /// <summary>
    /// How long a break of the partner waits for the thing here to stand still between hits, in seconds. The walls and
    /// the vines are done with a hit or with their own break well within it, so one that is not standing still by then
    /// is stuck, and is shown broken all the same.
    /// </summary>
    private const float BreakWaitTime = 3f;

    /// <summary>
    /// How far back the hits and touches that went between the games for a thing that stood here are written down with
    /// it, in seconds: about as far back as they are kept (see CoopHits.GetTraffic).
    /// </summary>
    private const float BreakTrafficTime = 120f;

    /// <summary>
    /// The field of the event by which a saved object asks a component of its own for its value when it saves
    /// (PersistentItem.UpdateValue), which only a few kinds of objects use.
    /// </summary>
    private static readonly FieldInfo? SaveStateReaderField = typeof(PersistentItem<bool>).GetField(
        "OnGetSaveState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
    );

    /// <summary>
    /// The state machines of the things that the partner broke and that wait here to stand still between hits, with the
    /// state that looks at their flag as their room loads, the state that this takes them to when it is set, and when the
    /// break came.
    /// </summary>
    private readonly Dictionary<PlayMakerFSM, (FsmState Look, FsmState Broken, float Since)> _waitingBreaks = new();

    /// <summary>
    /// The state machines whose waits ended, kept to be taken out of <see cref="_waitingBreaks"/> after going through it.
    /// </summary>
    private readonly List<PlayMakerFSM> _endedBreakWaits = [];

    /// <summary>
    /// Whether a saved object keeps its value in a component of its own rather than in the flag of its state machine,
    /// like a lever, a gate or a creature: the game asks that component for the value when it saves
    /// (PersistentItem.UpdateValue), and the flag of a state machine of the object, if it has one, is not what is saved.
    /// Also true if that can't be told, so that nothing is done to such an object then.
    /// </summary>
    internal static bool KeepsValueInComponent(PersistentBoolItem item) {
        return SaveStateReaderField == null || SaveStateReaderField.GetValue(item) != null;
    }

    /// <summary>
    /// Shows the things that the partner broke as broken here as well: those of the given keys in the loaded rooms that
    /// still stand. Only for changes of the world that come while playing, and not for a whole save that is merged when
    /// the players pair up (see OnWorldChange).
    /// </summary>
    /// <param name="keys">The keys of the saved objects that the partner set.</param>
    /// <param name="collapsing">The state machines that the partner's change brought down with their own fall (see
    /// <see cref="ReplayLoadedCollapses"/>).</param>
    private void FollowPartnerBreaks(HashSet<string> keys, HashSet<PlayMakerFSM> collapsing) {
        if (keys.Count == 0) {
            return;
        }

        // Switched off ones are left out: a thing that broke here already may switch itself off whole
        foreach (var item in Object.FindObjectsByType<PersistentBoolItem>(FindObjectsSortMode.None)) {
            if (item == null || !item.gameObject.activeInHierarchy) {
                continue;
            }

            GetItemSceneAndId(item, out var scene, out var id);
            if (!keys.Contains(GetItemKey(scene, id))) {
                continue;
            }

            try {
                FollowPartnerBreak(item, collapsing);
            } catch (Exception e) {
                Logger.Warn($"Could not show '{id}' broken as the partner broke it: {e.Message}");
            }
        }

        ShowWaitingBreaks();
    }

    /// <summary>
    /// Shows one thing that the partner broke as broken here, if it is of a kind that breaks and still stands: one of the
    /// game's breakable objects at once, and one that a state machine runs once it stands still between hits.
    /// </summary>
    private void FollowPartnerBreak(PersistentBoolItem item, HashSet<PlayMakerFSM> collapsing) {
        if (item.TryGetComponent<Breakable>(out var breakable)) {
            if (breakable.isBroken) {
                return;
            }

            var left = $"{breakable.hitsLeft} of {breakable.hitsToBreak}";

            // What its saved value does to it when its room loads it broken (Breakable.Awake)
            breakable.isBroken = true;
            breakable.SetAlreadyBroken();
            Logger.Info(
                $"[Breaks] '{ScenePath.Get(item.transform)}' stood here with {left} hits left when the partner broke " +
                $"it: shown broken now{DescribeBreakTraffic(item.gameObject)}"
            );
            return;
        }

        // One that keeps its value elsewhere, like a creature or a lever, is left to what keeps that in step
        if (KeepsValueInComponent(item)) {
            return;
        }

        // One that has not started yet is left to take its saved value, which has the partner's break, as its saved
        // item starts (PersistentItem.Start), as both do when their object comes on; its actions are not loaded
        // before it starts, and loading them early breaks it
        var fsm = FSMUtility.FindFSMWithPersistentBool(item.GetComponents<PlayMakerFSM>());
        if (fsm == null || fsm.Fsm is not { Started: true } || collapsing.Contains(fsm) || _waitingBreaks.ContainsKey(fsm) ||
            fsm.FsmVariables.FindFsmBool(SavedFlagName) is not { Value: false } || IsMechanismReplayed(fsm.Fsm) ||
            FindBrokenState(fsm) is not { } found) {
            return;
        }

        _waitingBreaks[fsm] = (found.Look, found.Broken, Time.unscaledTime);
    }

    /// <summary>
    /// Whether a use of the partner that this game replays on a mechanism holds a state machine, now or waiting: the
    /// replay sees to it.
    /// </summary>
    private bool IsMechanismReplayed(HutongGames.PlayMaker.Fsm fsm) {
        return _replays.ContainsKey(fsm) || _pendingReplays.ContainsKey(fsm) || _capturedMechanisms.ContainsKey(fsm);
    }

    /// <summary>
    /// For a state machine of a thing which breaks after a number of hits, the state that looks at its saved flag as its
    /// room loads and the state that this takes it to when the flag is set, the one of being broken. Null for a state
    /// machine of another kind. Such a one listens for the player's hits, counts them, and looks at its flag in one place
    /// only. A lever, a platform that drops or a reminder that a strike sets off counts nothing: it is set by the one hit,
    /// and the replay of that hit sets it here.
    /// </summary>
    private static (FsmState Look, FsmState Broken)? FindBrokenState(PlayMakerFSM fsm) {
        if (!HitMachines.CountsHits(fsm)) {
            return null;
        }

        (FsmState Look, FsmState Broken)? found = null;
        foreach (var state in fsm.FsmStates) {
            foreach (var action in state.Actions) {
                if (!action.Enabled || action is not BoolTest { boolVariable.Name: SavedFlagName, isTrue: { } isTrue } ||
                    HitMachines.FindTarget(fsm, state, isTrue.Name) is not { } to) {
                    continue;
                }

                // Looked at in two places: not one of these
                if (found is { } other && (other.Look != state || other.Broken != to)) {
                    return null;
                }

                found = (state, to);
            }
        }

        return found;
    }

    /// <summary>
    /// Whether a state machine in a state is still on its way to the state where it looks at its saved flag, as its room
    /// loaded: whether it gets there by itself, once each state on the way is done, without first waiting for a hit.
    /// </summary>
    private static bool IsOnWayTo(PlayMakerFSM fsm, FsmState from, FsmState look) {
        return HitMachines.FindOnWay(fsm, from, state => state == look || HitMachines.ListensForHits(state)) == look;
    }

    /// <summary>
    /// Shows the breaks of the partner that wait here, each once its thing stands still between hits or once it waited
    /// long enough. One that broke by itself in the meantime, or that is gone or switched off, waits no more.
    /// </summary>
    private void ShowWaitingBreaks() {
        if (_waitingBreaks.Count == 0) {
            return;
        }

        var now = Time.unscaledTime;
        foreach (var (fsm, (look, broken, since)) in _waitingBreaks) {
            try {
                if (fsm == null || !fsm.isActiveAndEnabled || IsMechanismReplayed(fsm.Fsm) ||
                    fsm.FsmVariables.FindFsmBool(SavedFlagName) is not { Value: false } flag) {
                    _endedBreakWaits.Add(fsm!);
                    continue;
                }

                // On its way to its look at the flag, as it is while it starts, or stopped: set, the look takes it to
                // being broken, as in a room that loads it so
                var state = fsm.Fsm.ActiveState;
                if (state == null || state != look && IsOnWayTo(fsm, state, look)) {
                    flag.Value = true;
                    _endedBreakWaits.Add(fsm);
                    Logger.Info(
                        $"[Breaks] '{ScenePath.Get(fsm.transform)}' ({fsm.FsmName}) was starting in " +
                        $"'{state?.Name ?? "nothing"}' when the partner broke it: it looks at its flag in '{look.Name}' " +
                        "and shows broken"
                    );
                    continue;
                }

                var waited = now - since;
                if (!HitMachines.ListensForHits(state) && waited < BreakWaitTime) {
                    continue;
                }

                // What its saved value does to it when its room loads it broken: the flag that it looks at as it starts
                // (PersistentItem.Start), and the state that the look takes it to
                _endedBreakWaits.Add(fsm);
                var counts = string.Join(", ", fsm.FsmVariables.IntVariables.Select(v => $"{v.Name}={v.Value}"));
                flag.Value = true;
                fsm.SetState(broken.Name);
                Logger.Info(
                    $"[Breaks] '{ScenePath.Get(fsm.transform)}' ({fsm.FsmName}) stood here in '{state.Name}' with " +
                    $"{(counts.Length > 0 ? counts : "no counts")} when the partner broke it" +
                    $"{(waited >= BreakWaitTime ? $", and still did {waited:0.0} s later" : "")}: shown broken now" +
                    DescribeBreakTraffic(fsm.gameObject)
                );
            } catch (Exception e) {
                _endedBreakWaits.Add(fsm);
                Logger.Warn($"Could not show a thing broken as the partner broke it: {e.Message}");
            }
        }

        foreach (var fsm in _endedBreakWaits) {
            _waitingBreaks.Remove(fsm);
        }

        _endedBreakWaits.Clear();
    }

    /// <summary>
    /// The hits and touches that went between the games lately for a thing that stood here when the partner broke it,
    /// as lines to add to the line written for it. Each says what it did to the thing in the game that made it or
    /// replayed it, so the one that did not count here shows.
    /// </summary>
    private static string DescribeBreakTraffic(GameObject target) {
        var lines = CoopHits.GetTraffic(
            target.scene.name, ScenePath.Get(target.transform), Time.unscaledTime - BreakTrafficTime
        );
        return lines.Count == 0
            ? $"\n    no hits or touches went between the games for it in the last {BreakTrafficTime:0} s"
            : "\n    hits and touches for it:\n      " + string.Join("\n      ", lines);
    }
}
