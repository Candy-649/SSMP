using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The ends of the bosses that are fought in a memory, which play the template heart_death_outro. Such an end gives
/// nothing but what its state writes to the player data: that the boss is beaten, and what the rest of the world asks
/// about it. A player who dies in a memory is out of it, back in the room they came from, while the others may fight on;
/// the end then counted only for the players still in the memory, as the records of a boss went to the players in its
/// scene alone. The boss stayed unbeaten for the one who was out, who could not fight it either, since a boss fight
/// starts only once every player is in its room. What the state of such an end writes now reaches every other player,
/// wherever they are, and what the room they are in shows of it, like the entrance of the memory, is looked at again.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// The name of the template that the end of the boss of a memory plays.
    /// </summary>
    private const string HeartEndTemplateName = "heart_death_outro";

    /// <summary>
    /// The method of the game that switches an object off when its record of the player data is set.
    /// </summary>
    private static readonly MethodInfo? DeactivatorEvaluate =
        typeof(DeactivateIfPlayerdataTrue).GetMethod("ForceEvaluate", InstanceFlags, null, Type.EmptyTypes, null);

    /// <summary>
    /// The method of the game that switches objects on or off by a test of the player data.
    /// </summary>
    private static readonly MethodInfo? ActivatorEvaluate =
        typeof(TestGameObjectActivator).GetMethod("DoEvaluate", InstanceFlags, null, Type.EmptyTypes, null);

    /// <summary>
    /// The test of the player data of an object that switches objects on or off by it.
    /// </summary>
    private static readonly FieldInfo? ActivatorTest =
        typeof(TestGameObjectActivator).GetField("playerDataTest", InstanceFlags);

    /// <summary>
    /// The variable of the end of a memory that names the room it sends the player back to.
    /// </summary>
    private const string ReturnSceneVariableName = "Return Scene";

    /// <summary>
    /// Which templates of RunFSM actions are the end of the boss of a memory.
    /// </summary>
    private static readonly Dictionary<FsmTemplate, bool> HeartEndTemplates = new();

    /// <summary>
    /// The FSM that the action running a template runs.
    /// </summary>
    private static readonly FieldInfo? RunFsmField = typeof(RunFSMAction).GetField("runFsm", InstanceFlags);

    /// <summary>
    /// The records from the ends of other players' memories whose objects in the loaded rooms are still to be looked at
    /// again, once the local player is free to move.
    /// </summary>
    private readonly HashSet<string> _heartRecordsToShow = [];

    /// <summary>
    /// The FSMs of the ends of memories that run in this game, until they show their words, which go to the other
    /// players.
    /// </summary>
    private readonly HashSet<Fsm> _heartEndRuns = [];

    /// <summary>
    /// The memories whose end another player's game played while the local player was not in them, and which gave the
    /// local player a record they did not have: the words of such an end are shown to them too.
    /// </summary>
    private readonly HashSet<string> _missedHeartEnds = [];

    /// <summary>
    /// The words of the ends of memories that the local player missed, still to be shown once they are free to move.
    /// </summary>
    private readonly Queue<BossRoomUpdate> _heartEndTexts = new();

    /// <summary>
    /// Whether the local player rested on a bench since the words of a missed end came.
    /// </summary>
    private bool _restedSinceHeartEndText;

    /// <summary>
    /// Notes the end of the boss of a memory that this game plays, and tells the other players its words once it shows
    /// them, so that one who was not in the memory reads them too.
    /// </summary>
    private void NoteHeartEndState(Fsm fsm, FsmState state) {
        if (PlaysHeartEnd(state)) {
            var scene = SceneManager.GetActiveScene().name;
            Logger.Info($"'{GetPath(fsm)}' plays the end of the boss of the memory '{scene}' in this game");
            foreach (var action in state.Actions ?? []) {
                if (action is RunFSM run && RunFsmField?.GetValue(run) is Fsm runFsm) {
                    _heartEndRuns.Add(runFsm);
                }
            }

            // The local player sees this end themselves
            var wasMissed = _missedHeartEnds.Remove(scene);
            if (wasMissed || _heartEndTexts.Any(words => words.SceneName == scene)) {
                Logger.Info($"The local player sees the end of '{scene}' themselves, so its words aren't shown again");
                var others = _heartEndTexts.Where(words => words.SceneName != scene).ToList();
                _heartEndTexts.Clear();
                others.ForEach(_heartEndTexts.Enqueue);
            }

            return;
        }

        if (_heartEndRuns.Count == 0 || !_heartEndRuns.Contains(fsm)) {
            return;
        }

        foreach (var action in state.Actions ?? []) {
            if (action is not RunDialogueBase dialogue) {
                continue;
            }

            _heartEndRuns.Remove(fsm);
            if (!_isFullSynchronisation()) {
                return;
            }

            // An exception here would leave PlayMaker's stack of running FSMs out of step for the rest of the frame
            try {
                var update = CreateDialogueUpdate(fsm, state.Name, dialogue);
                update.Kind = BossRoomUpdateKind.HeartEndText;
                update.SceneName = SceneManager.GetActiveScene().name;
                update.EventName = fsm.Variables.FindFsmString(ReturnSceneVariableName)?.Value ?? "";
                Logger.Info(
                    "Telling every other player the words of the end of the boss of a memory " +
                    $"('{update.DialogueSheet}', '{update.DialogueKey}', back to '{update.EventName}')"
                );
                _netClient.UpdateManager.SetBossRoomUpdate(update);
            } catch (Exception e) {
                Logger.Warn($"Could not tell the other players the words of the end of a memory:\n{e}");
            }

            return;
        }
    }

    /// <summary>
    /// Keeps the words of the end of a memory that another player's game played, for a local player who missed it, to
    /// show them once they are free to move.
    /// </summary>
    private void OnHeartEndText(BossRoomUpdate update) {
        if (!_missedHeartEnds.Remove(update.SceneName)) {
            Logger.Info(
                $"Not showing the words of the end of the memory '{update.SceneName}': the local player was in it, or " +
                "had its records before"
            );
            return;
        }

        Logger.Info($"Keeping the words of the end of the memory '{update.SceneName}' to show to the local player");
        _heartEndTexts.Enqueue(update);
        ShowHeartEndTexts();
    }

    /// <summary>
    /// Shows the words of the ends of memories that the local player missed, the way dialogue that another player
    /// shares is shown. Reading takes control from them, which a creature's hit would take back, so the words wait for a
    /// calm place: the room the memory sends its players back to, where a player who died in it stands, or wherever
    /// the local player is free to move once they rested on a bench.
    /// </summary>
    private void ShowHeartEndTexts() {
        if (_heartEndTexts.Count == 0) {
            return;
        }

        if (!_restedSinceHeartEndText && PlayerData.instance is { atBench: true }) {
            Logger.Info("The local player rests on a bench, so the words of a missed memory's end show once they get up");
            _restedSinceHeartEndText = true;
        }

        // Only into an empty queue of shared dialogue, which a change of scene empties: words still waiting here are kept
        if (_shownDialogue != null || _dialogueQueue.Count > 0 || !IsLocalHeroFree()) {
            return;
        }

        var scene = SceneManager.GetActiveScene().name;
        var due = _heartEndTexts
            .Where(words => words.SceneName != scene && (_restedSinceHeartEndText || words.EventName == scene))
            .ToList();
        if (due.Count == 0) {
            return;
        }

        var kept = _heartEndTexts.Where(words => !due.Contains(words)).ToList();
        _heartEndTexts.Clear();
        kept.ForEach(_heartEndTexts.Enqueue);
        _restedSinceHeartEndText = false;

        foreach (var words in due) {
            Logger.Info($"Showing the words of the end of the memory '{words.SceneName}' to the local player");

            // As dialogue of no object of this room: reading it answers nobody. In a box of its own, as it shows over the
            // room rather than over the black screen of the end
            _dialogueQueue.Enqueue(new BossRoomUpdate {
                SceneName = scene,
                Kind = BossRoomUpdateKind.DialogueStarted,
                Path = "",
                FsmName = "",
                FromState = HeartEndTemplateName + "\n" + words.SceneName + "\n" + words.DialogueKey,
                DialogueSheet = words.DialogueSheet,
                DialogueKey = words.DialogueKey,
                DialogueText = words.DialogueText,
                HideDecorators = false,
                TextAlignment = -1,
                OffsetY = 0f
            });
        }

        ShowNextDialogue();
    }

    /// <summary>
    /// Forgets the ends of memories that the local player missed, on going back to the main menu: they belong to the
    /// save that was being played.
    /// </summary>
    private void ClearMissedHeartEnds() {
        _missedHeartEnds.Clear();
        _heartEndTexts.Clear();
        _restedSinceHeartEndText = false;
    }

    /// <summary>
    /// Shares a write of the end of the boss of a memory with every other player, wherever they are. A write whose value
    /// the state reads from the player's own data first, like whether they had beaten the boss after another memory of
    /// their own, goes along with each of the others as a write from the receiving player's own data.
    /// </summary>
    /// <returns>Whether the write was made by such an end, which is then not shared in any other way.</returns>
    private bool TryShareHeartEnd(string name, Fsm fsm) {
        if (fsm.ActiveState is not { } state || !PlaysHeartEnd(state)) {
            return false;
        }

        if (IsHeroStateName(name) || !_netClient.IsConnected || !_isFullSynchronisation() ||
            GetOwnDataWrites(state).Any(write => write.Written == name)) {
            return true;
        }

        Logger.Info($"Sharing '{name}' of the end of the boss of a memory with every other player, wherever they are");
        Send(BossRoomUpdateKind.HeartEndRecord, "", "", "", "", "", name);
        foreach (var (written, source) in GetOwnDataWrites(state)) {
            Send(BossRoomUpdateKind.HeartEndRecord, "", "", source, "", "", written);
        }

        return true;
    }

    /// <summary>
    /// Writes what the end of the boss of a memory wrote in the game of another player, wherever the local player is.
    /// A write that goes by the player's own data takes the local player's.
    /// </summary>
    private void OnHeartEndRecord(BossRoomUpdate update) {
        var playerData = PlayerData.instance;
        var name = update.VariableName;
        if (playerData == null || name.Length == 0 || IsHeroStateName(name)) {
            return;
        }

        var source = update.FromState;
        if (source.Length > 0 && !playerData.GetBool(source)) {
            return;
        }

        Logger.Info(
            $"Another player shared '{name}' of the end of the boss of a memory in '{update.SceneName}'" +
            (source.Length > 0 ? $", which goes by the local player's own '{source}'" : "")
        );

        // A player for whom its boss was not beaten yet misses its end, unless their own game plays it too, which takes
        // the mark away: one dying in the memory as it ends never sees it, though they are still in its scene
        var isInMemory = SceneManager.GetActiveScene().name == update.SceneName;
        if (source.Length == 0 && !playerData.GetBool(name) && _missedHeartEnds.Add(update.SceneName)) {
            Logger.Info($"The local player had not seen the end of '{update.SceneName}' before, so its words are kept");
        }

        _applyingRecord = true;
        try {
            playerData.SetBool(name, true);
        } finally {
            _applyingRecord = false;
        }

        // A player in the same memory plays its end in their own game
        if (!isInMemory) {
            _heartRecordsToShow.Add(name);
            ShowHeartRecords();
        }
    }

    /// <summary>
    /// Looks again at the objects of the loaded rooms that the game switches on or off by the records that came from the
    /// end of another player's memory, which it only does as their room loads: the entrance of the memory itself would
    /// stay open, into a memory whose boss is beaten, which has no way out then. That waits while the local player is
    /// not free to move, so that an entrance they are on the way through is not switched off under them.
    /// </summary>
    private void ShowHeartRecords() {
        if (_heartRecordsToShow.Count == 0 || !IsLocalHeroFree()) {
            return;
        }

        var names = _heartRecordsToShow.ToList();
        _heartRecordsToShow.Clear();

        foreach (var deactivator in Object.FindObjectsByType<DeactivateIfPlayerdataTrue>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None
                 )) {
            if (!names.Contains(deactivator.boolName)) {
                continue;
            }

            Logger.Info($"Looking again at '{ScenePath.Get(deactivator.transform)}', which goes by '{deactivator.boolName}'");
            try {
                DeactivatorEvaluate?.Invoke(deactivator, null);
            } catch (Exception e) {
                Logger.Warn($"Could not look again at '{deactivator.name}':\n{e}");
            }
        }

        foreach (var activator in Object.FindObjectsByType<TestGameObjectActivator>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None
                 )) {
            if (!activator.isActiveAndEnabled || ActivatorTest?.GetValue(activator) is not PlayerDataTest test ||
                !TestsAny(test, names)) {
                continue;
            }

            Logger.Info($"Looking again at '{ScenePath.Get(activator.transform)}', which tests a record of a memory's end");
            try {
                ActivatorEvaluate?.Invoke(activator, null);
            } catch (Exception e) {
                Logger.Warn($"Could not look again at '{activator.name}':\n{e}");
            }
        }
    }

    /// <summary>
    /// Forgets the records still to be shown, as the next room shows them by itself when it loads, and the ends of the
    /// room left behind. The words of missed ends are kept: they are shown wherever the player goes.
    /// </summary>
    private void ClearHeartRecords() {
        _heartRecordsToShow.Clear();
        _heartEndRuns.Clear();
    }

    /// <summary>
    /// Whether a test of the player data tests any of the given records.
    /// </summary>
    private static bool TestsAny(PlayerDataTest test, ICollection<string> names) {
        foreach (var group in test.TestGroups ?? []) {
            foreach (var condition in group.Tests ?? []) {
                if (names.Contains(condition.FieldName)) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a state plays the end of the boss of a memory.
    /// </summary>
    private static bool PlaysHeartEnd(FsmState state) {
        foreach (var action in state.Actions ?? []) {
            if (action is not RunFSM { fsmTemplateControl.fsmTemplate: { } template }) {
                continue;
            }

            // Asked on every change of state, and the name of an asset is a new string each time it is read
            if (!HeartEndTemplates.TryGetValue(template, out var isHeartEnd)) {
                HeartEndTemplates[template] = isHeartEnd = template.name == HeartEndTemplateName;
            }

            if (isHeartEnd) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The writes of a state whose value the state reads from the player's own data first, with the record each is read
    /// from.
    /// </summary>
    private static IEnumerable<(string Written, string Source)> GetOwnDataWrites(FsmState state) {
        foreach (var action in state.Actions ?? []) {
            if (action is not HutongGames.PlayMaker.Actions.SetPlayerDataBool {
                    boolName.Value: { Length: > 0 } written, value: { UseVariable: true, Name: { Length: > 0 } variable }
                }) {
                continue;
            }

            foreach (var read in state.Actions ?? []) {
                if (read is HutongGames.PlayMaker.Actions.GetPlayerDataBool {
                        boolName.Value: { Length: > 0 } source, storeValue.Name: { } stored
                    } && stored == variable) {
                    yield return (written, source);
                    break;
                }
            }
        }
    }
}
