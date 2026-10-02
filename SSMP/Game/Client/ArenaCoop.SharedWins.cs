using System;
using System.Linq;
using System.Reflection;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client;

/// <summary>
/// What an arena that one player won leaves for the other player, beyond the saved win that both saves share
/// (tools/coop_world_items.py and tools/coop_story_flags.py list those).
/// <list type="bullet">
/// <item>Arenas whose win the game doesn't save at all, where everything happens in the live end of the fight, like a
/// character who is saved and talks, or an item that turns up. The game of a player who wins one leaves a mark of its
/// own in the save, which the partner's save gets like a saved object of the world. Whoever comes in later finds the
/// end of the fight played without the fight.</item>
/// <item>An item that a wave of the fight brings out, which a room that loads as won never shows. It is put out
/// again, and it hides itself if the player took it already.</item>
/// <item>A rescued character who thanks the player on the spot before moving on. A partner who wasn't there hears it
/// the first time they come into the room afterwards.</item>
/// </list>
/// </summary>
internal partial class ArenaCoop {
    /// <summary>
    /// The start of the ID of the mark that a won arena without a saved win leaves in the save, followed by the name of
    /// the arena. tools/coop_world_items.py lists the same IDs as saved objects of the world.
    /// </summary>
    internal const string WonMarkPrefix = "SSMP Won ";

    /// <summary>
    /// The ID of the mark, kept in the local save only, that a rescued character still owes the local player their
    /// thanks on the spot.
    /// </summary>
    private const string ThanksOwedId = "SSMP Thanks Owed";

    /// <summary>
    /// The event that the end of a battle sends to the end scene of its arena.
    /// </summary>
    private const string BattleEndEvent = "BATTLE END";

    /// <summary>
    /// The event that breaks a wall chunk.
    /// </summary>
    private const string BreakEvent = "BREAK";

    /// <summary>
    /// Reflected field with the FSM that an arena tells that its battle was won.
    /// </summary>
    private static readonly FieldInfo? EndSceneField = typeof(BattleScene).GetField("endScene", InstanceFlags);

    /// <summary>
    /// The arenas whose win the game doesn't save, and how the end of their fight is played for a player who comes in
    /// after someone won it.
    /// </summary>
    private static readonly LiveEnd[] LiveEnds = [
        // An ambush guards an item: a wall breaks and the item turns up. What starts the ambush is switched off once it
        // opened the way out, since it would start the fight again
        new("ant_08", ["Init"], "BattleStart Inspect Region", "Control", "Idle", ["Wall Chunk B", "Wall Chunk C"]),
        // A character who hides during the fight comes out
        new("arborium_11", ["Cowering"]),
        new("song_07", ["Barrel Idle", "Hiding"]),
        // A character who asks for help talks first, and the fight is over the moment it would start. Their end has a
        // global transition, so the end is sent once, from the state that starts the fight only, to the FSM of the
        // talk among the character's FSMs
        new("ward_09", ["Start Battle"], targetFsm: "Conversation Control"),
        // A small creature is freed
        new("shadow_28", ["Wriggle"])
    ];

    /// <summary>
    /// Items that a wave of an arena's fight brings out, which a room that loads as won puts out again: the scene, the
    /// path of the object that places the item under the arena, the item under it, and the arena object whose place it
    /// takes, which has ground under it.
    /// </summary>
    private static readonly (string Scene, string Placer, string Item, string Spot)[] WaveItems = [
        ("cog_07", "Wave 2 - Item/Item Placer", "Collectable Item Pickup", "Wave 1/Song Automaton Ball L")
    ];

    /// <summary>
    /// A rescued character who thanks the player on the spot after an arena, and then moves on, so a room that loads as
    /// won never shows it. The scene, the saved flag of the rescue, the flag of the local player without which the
    /// rescue never shows in their game, the FSM of the room that picks what it shows and the state it ends in when
    /// the rescue was done, and the scene of the rescue under that FSM's object.
    /// </summary>
    private static readonly (string Scene, string Flag, string SeenFlag, string Control, string ControlFsm,
        string DoneState, string RescueScene) RescueThanks = (
            "shellgrave", "savedPlinney", "sawPlinneyLeft", "Event Control", "Control", "None", "Plinney Rescue Scene"
        );

    /// <summary>
    /// The states in which the rescued character waits for the end of the fight.
    /// </summary>
    private static readonly string[] RescueWaitingStates = ["Scared", "Scared L", "Scared R"];

    /// <summary>
    /// Whether the rescued character's thanks are owed in the active scene, which the update looks after.
    /// </summary>
    private bool _thanksOwedHere;

    /// <summary>
    /// Whether the scene of the rescue was switched on for the thanks in the active scene.
    /// </summary>
    private bool _thanksSceneShown;

    /// <summary>
    /// Registers the hooks for what a won arena leaves for the other player.
    /// </summary>
    private void RegisterSharedWinHooks() {
        AddHook(typeof(BattleScene), "Start", new Action<Action<BattleScene>, BattleScene>(OnArenaStart));
        AddHook(typeof(BattleScene), "BattleCompleted", new Action<Action<BattleScene>, BattleScene>(OnBattleCompleted));
        MonoBehaviourUtil.Instance.OnUpdateEvent += UpdateRescueThanks;
    }

    /// <summary>
    /// Deregisters what <see cref="RegisterSharedWinHooks"/> registered besides the hooks.
    /// </summary>
    private void DeregisterSharedWinHooks() {
        MonoBehaviourUtil.Instance.OnUpdateEvent -= UpdateRescueThanks;
        _thanksOwedHere = false;
    }

    /// <summary>
    /// Plays the end of the fight of an arena without a saved win for a player who comes in after someone won it.
    /// </summary>
    private void OnArenaStart(Action<BattleScene> orig, BattleScene self) {
        orig(self);

        try {
            if (_isSharedSave() && FindLiveEnd(self) != null && HasWonMark(self)) {
                BeginLiveEnd(self, GetState(self), "it was won before");
            }
        } catch (Exception e) {
            Logger.Error($"Could not check whether arena '{self.name}' was won before:\n{e}");
        }
    }

    /// <summary>
    /// Puts out again the item that a wave of an arena that loads as won would have brought out.
    /// </summary>
    private void OnBattleCompleted(Action<BattleScene> orig, BattleScene self) {
        orig(self);

        try {
            PutOutWaveItem(self);
        } catch (Exception e) {
            Logger.Error($"Could not put out the item of arena '{self.name}':\n{e}");
        }
    }

    /// <summary>
    /// Finds how the end of the fight of an arena without a saved win is played, or null for other arenas.
    /// </summary>
    private static LiveEnd? FindLiveEnd(BattleScene battleScene) {
        var sceneName = battleScene.gameObject.scene.name;
        foreach (var liveEnd in LiveEnds) {
            if (string.Equals(liveEnd.SceneName, sceneName, StringComparison.OrdinalIgnoreCase)) {
                return liveEnd;
            }
        }

        return null;
    }

    /// <summary>
    /// The ID of the mark that a won arena without a saved win leaves in the save.
    /// </summary>
    private static string GetWonMarkId(BattleScene battleScene) => WonMarkPrefix + battleScene.name;

    /// <summary>
    /// Whether the save has the mark that an arena without a saved win was won.
    /// </summary>
    private bool HasWonMark(BattleScene battleScene) {
        var sceneData = SceneData.instance;
        return sceneData != null &&
               sceneData.PersistentBools.TryGetValue(
                   battleScene.gameObject.scene.name, GetWonMarkId(battleScene), out var mark
               ) && mark.Value;
    }

    /// <summary>
    /// Leaves the mark in the save that an arena without a saved win was won, which the partner's save gets too.
    /// </summary>
    private void LeaveWonMark(BattleScene battleScene, ArenaState state) {
        var sceneData = SceneData.instance;
        if (sceneData == null || FindLiveEnd(battleScene) == null || HasWonMark(battleScene)) {
            return;
        }

        Logger.Info($"Arena '{state.Path}' doesn't save its win, leaving a mark of it in the save");
        sceneData.PersistentBools.SetValue(new PersistentItemData<bool> {
            ID = GetWonMarkId(battleScene),
            SceneName = battleScene.gameObject.scene.name,
            Value = true,
            IsSemiPersistent = false
        });
    }

    /// <summary>
    /// Starts playing the end of the fight of an arena without a saved win for the local player, who didn't fight it:
    /// the arena counts as won here, so nothing starts the fight again, and the end goes to the arena's end scene once
    /// it waits for it.
    /// </summary>
    private void BeginLiveEnd(BattleScene battleScene, ArenaState state, string reason) {
        if (state.LiveEndOwed || state.LiveEndPlayed) {
            return;
        }

        Logger.Info($"Arena '{state.Path}' doesn't save its win and {reason}, playing its end without the fight");
        state.LiveEndOwed = true;
        CompletedField!.SetValue(battleScene, true);
        LeaveWonMark(battleScene, state);
    }

    /// <summary>
    /// Plays the next step of the end of the fight of an arena without a saved win, once its objects are ready for it.
    /// </summary>
    private void UpdateLiveEnd(BattleScene battleScene, ArenaState state) {
        if (!state.LiveEndOwed || FindLiveEnd(battleScene) is not { } liveEnd) {
            return;
        }

        // What starts the fight is switched off once it is done getting the room ready
        if (liveEnd.StarterName != null && !state.LiveEndStarterOff) {
            var starter = battleScene.transform.Find(liveEnd.StarterName);
            if (starter != null) {
                var fsm = starter.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == liveEnd.StarterFsm);
                if (fsm != null && fsm.ActiveStateName != liveEnd.StarterReadyState) {
                    return;
                }

                starter.gameObject.SetActive(false);
            }

            state.LiveEndStarterOff = true;
            foreach (var name in liveEnd.BreakNames) {
                var chunk = battleScene.transform.Find(name);
                if (chunk == null) {
                    continue;
                }

                foreach (var fsm in chunk.GetComponents<PlayMakerFSM>()) {
                    fsm.SendEvent(BreakEvent);
                }
            }
        }

        if (EndSceneField?.GetValue(battleScene) is not PlayMakerFSM endScene || endScene == null) {
            return;
        }

        if (liveEnd.TargetFsm != null) {
            endScene = endScene.GetComponents<PlayMakerFSM>().FirstOrDefault(fsm => fsm.FsmName == liveEnd.TargetFsm);
        }

        if (endScene == null || !endScene.isActiveAndEnabled ||
            !liveEnd.WaitingStates.Contains(endScene.ActiveStateName)) {
            return;
        }

        state.LiveEndOwed = false;
        state.LiveEndPlayed = true;
        Logger.Info($"Playing the end of arena '{state.Path}' for '{endScene.gameObject.name}'");
        endScene.SendEvent(BattleEndEvent);
    }

    /// <summary>
    /// Puts out again the item that a wave of an arena's fight brings out, when the arena shows as won and its waves are
    /// switched off with the item still in them. The item hides itself if the local player took it already.
    /// </summary>
    private static void PutOutWaveItem(BattleScene battleScene) {
        var sceneName = battleScene.gameObject.scene.name;
        foreach (var (scene, placerPath, itemName, spotPath) in WaveItems) {
            if (!string.Equals(scene, sceneName, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var placer = battleScene.transform.Find(placerPath);
            var item = placer == null ? null : placer.Find(itemName);
            var spot = battleScene.transform.Find(spotPath);
            if (placer == null || item == null || spot == null) {
                Logger.Warn($"Could not find the item of arena '{battleScene.name}' to put out again");
                continue;
            }

            Logger.Info($"Arena '{battleScene.name}' shows as won, putting its item out again where it can be taken");
            // Out of the wave that is switched off, as the fight itself does with it
            placer.SetParent(null, true);
            placer.position = spot.position;
            placer.gameObject.SetActive(true);
            item.gameObject.SetActive(true);
        }
    }

    /// <summary>
    /// Takes note of a flag that the partner's game set. A rescue that the partner did while the local player was
    /// somewhere else still owes the local player the rescued character's thanks on the spot.
    /// </summary>
    /// <param name="name">The name of the flag.</param>
    public void OnPartnerFlagSet(string name) {
        if (name != RescueThanks.Flag) {
            return;
        }

        if (string.Equals(SceneManager.GetActiveScene().name, RescueThanks.Scene, StringComparison.OrdinalIgnoreCase)) {
            // The local player is in the room, where the rescue plays for them as well
            return;
        }

        var sceneData = SceneData.instance;
        if (sceneData == null) {
            return;
        }

        Logger.Info("The partner did a rescue while the local player was somewhere else, who is owed the thanks");
        sceneData.PersistentBools.SetValue(new PersistentItemData<bool> {
            ID = ThanksOwedId,
            SceneName = RescueThanks.Scene,
            Value = true,
            IsSemiPersistent = false
        });
    }

    /// <summary>
    /// Notes whether the active scene owes the local player a rescued character's thanks, after the scene changed.
    /// </summary>
    private void OnRescueSceneChanged(string sceneName) {
        _thanksSceneShown = false;
        _thanksOwedHere = string.Equals(sceneName, RescueThanks.Scene, StringComparison.OrdinalIgnoreCase) &&
                          SceneData.instance is { } sceneData &&
                          sceneData.PersistentBools.TryGetValue(RescueThanks.Scene, ThanksOwedId, out var mark) &&
                          mark.Value;
    }

    /// <summary>
    /// Brings the rescued character back for the thanks they owe the local player, once the room picked what it shows,
    /// and sends them the end of the fight once they are there.
    /// </summary>
    private void UpdateRescueThanks() {
        if (!_thanksOwedHere) {
            return;
        }

        try {
            var playerData = PlayerData.instance;
            if (playerData == null) {
                return;
            }

            if (!playerData.GetBool(RescueThanks.Flag) || !playerData.GetBool(RescueThanks.SeenFlag)) {
                // The rescue never shows in the local player's game, so there are no thanks to give
                ForgetOwedThanks("the rescue never shows in this game");
                return;
            }

            var control = GameObject.Find(RescueThanks.Control);
            if (control == null) {
                return;
            }

            if (!_thanksSceneShown) {
                var controlFsm = control.GetComponents<PlayMakerFSM>()
                    .FirstOrDefault(fsm => fsm.FsmName == RescueThanks.ControlFsm);
                if (controlFsm == null) {
                    return;
                }

                // Still picking what the room shows
                if (controlFsm.Fsm.ActiveState?.Transitions is { Length: > 0 }) {
                    return;
                }

                if (controlFsm.ActiveStateName != RescueThanks.DoneState) {
                    // The room shows something else this time, like before the rescue could happen; maybe next time
                    _thanksOwedHere = false;
                    return;
                }

                var rescueScene = control.transform.Find(RescueThanks.RescueScene);
                if (rescueScene == null) {
                    ForgetOwedThanks("the rescue is not in the room");
                    return;
                }

                Logger.Info("Bringing back the rescued character for the thanks owed to the local player");
                _thanksSceneShown = true;
                rescueScene.gameObject.SetActive(true);
                return;
            }

            var battleScene = control.GetComponentInChildren<BattleScene>();
            if (battleScene == null || EndSceneField?.GetValue(battleScene) is not PlayMakerFSM character ||
                character == null || !character.isActiveAndEnabled ||
                !RescueWaitingStates.Contains(character.ActiveStateName)) {
                return;
            }

            character.SendEvent(BattleEndEvent);
            ForgetOwedThanks("the rescued character is giving them");
        } catch (Exception e) {
            ForgetOwedThanks($"bringing the character back failed:\n{e}");
        }
    }

    /// <summary>
    /// Forgets that a rescued character owes the local player their thanks.
    /// </summary>
    private void ForgetOwedThanks(string reason) {
        _thanksOwedHere = false;
        Logger.Info($"The thanks owed to the local player are done: {reason}");
        SceneData.instance?.PersistentBools.SetValue(new PersistentItemData<bool> {
            ID = ThanksOwedId,
            SceneName = RescueThanks.Scene,
            Value = false,
            IsSemiPersistent = false
        });
    }

    /// <summary>
    /// How the end of the fight of an arena without a saved win is played for a player who didn't fight it.
    /// </summary>
    private class LiveEnd {
        public LiveEnd(
            string sceneName,
            string[] waitingStates,
            string? starterName = null,
            string? starterFsm = null,
            string? starterReadyState = null,
            string[]? breakNames = null,
            string? targetFsm = null
        ) {
            SceneName = sceneName;
            WaitingStates = waitingStates;
            StarterName = starterName;
            StarterFsm = starterFsm;
            StarterReadyState = starterReadyState;
            BreakNames = breakNames ?? [];
            TargetFsm = targetFsm;
        }

        /// <summary>
        /// The scene of the arena.
        /// </summary>
        public string SceneName { get; }

        /// <summary>
        /// The states of the arena's end scene in which it waits for the end of the fight.
        /// </summary>
        public string[] WaitingStates { get; }

        /// <summary>
        /// The object under the arena that starts its fight and must be switched off, or null.
        /// </summary>
        public string? StarterName { get; }

        /// <summary>
        /// The FSM of that object.
        /// </summary>
        public string? StarterFsm { get; }

        /// <summary>
        /// The state that FSM is in once it got the room ready, after which it is switched off.
        /// </summary>
        public string? StarterReadyState { get; }

        /// <summary>
        /// Objects under the arena that the start of the fight breaks.
        /// </summary>
        public string[] BreakNames { get; }

        /// <summary>
        /// The FSM on the object of the arena's end scene that waits for the end, when it is not the end scene itself,
        /// or null.
        /// </summary>
        public string? TargetFsm { get; }
    }
}
