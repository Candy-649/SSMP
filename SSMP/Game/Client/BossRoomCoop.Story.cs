using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using SSMP.Networking.Packet.Data;
using SSMP.Ui;
using SSMP.Util;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Story scenes that play once wait for every player, the way a boss does. Each game plays such a scene by itself, as
/// its player walks into it, and marks it as seen in a way that reaches the other game: a flag both games share, a
/// wish, or a flag written in the scene's dialogue. Played first in one game, the scene was gone from the other before
/// its player got there, and the two players went through the same part of the story seeing different things.
///
/// The scene now holds back its start until every player has reached it: until their figure has been in the scene's
/// trigger, which lies in the same place in every game. By then the other game, whose room decided as it loaded to show
/// the scene, has set off its own, so nothing that this game's scene marks can take it away. Waiting only for the
/// other player to come into the room let them leave by another way before they got to the scene, and lose it. The
/// player who waits can go where they like, but can't watch the scene alone, since it would then be gone for the other
/// player. Only scenes that start as their player walks into them are held: one that plays as a player comes through a
/// door, or that a room leaves out as it loads, has no start that could wait, and plays for each player on their own
/// arrival (CoopSave.Arrivals). So does a scene whose trigger the player only reaches while the scene that began at the
/// door still steers them.
///
/// A scene that the game sets in several rooms, and plays in whichever its player reaches first, counts as reached by a
/// player who waits at it in any of them. Each game tells the others, wherever they are, when its player waits at such a
/// scene, when it starts for them and when they leave it without it starting. Two players who reach it in different
/// rooms then watch it at the same time, each in their own room, where before each waited for the other to come to
/// theirs.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// The event with which the held story scenes start, sent by the trigger that the player walks into.
    /// </summary>
    private const string StoryStartEventName = "ENTER";

    /// <summary>
    /// The scene of the main menu, through which the local player closes their save.
    /// </summary>
    private const string MenuSceneName = "Menu_Title";

    /// <summary>
    /// How far, in units, the local player may have moved from where they set off a held story scene for it to start
    /// there once it may, when its triggers can't be found to tell whether they are still in one.
    /// </summary>
    private const float StoryRestartDistance = 3f;

    /// <summary>
    /// How long, in seconds, a story scene is held before the players hear that it waits. A player who comes into a
    /// room is in it for the other players only once the server answers, and a scene by the door is held until then.
    /// </summary>
    private const float StoryNoticeDelay = 1.5f;

    /// <summary>
    /// How often, in seconds, a player who waits at a story scene that the game sets in several rooms says so again.
    /// </summary>
    private const float StoryWaitReportInterval = 1f;

    /// <summary>
    /// How long, in seconds, a player counts as waiting at a story scene that the game sets in several rooms after they
    /// last said so: a player whose game stopped saying it without the word that they left reaching the others counts
    /// no longer than this.
    /// </summary>
    private const float StoryWaitReportLifetime = 3f;

    /// <summary>
    /// The story scenes that start as the player walks into them and that the other game then skips, found in the
    /// game's data: each room shows its scene only while the scene's flag, quest or wish isn't set as the room loads.
    /// Left out: a way out of a room whose scene plays as the player comes into the next one, where holding the way
    /// out would only shut it while the scene in the next room is still decided as that room loads; and the talk
    /// before a fight in a boss arena, which already waits for every player there (ArenaCoop.BossStarts). A scene that
    /// the game sets in several rooms has an entry in each, with the same path, FSM and state.
    /// </summary>
    private static readonly StoryScene[] StoryScenes = [
        new("song_05", "Black Thread States/Normal World/Lace Scene/Lace NPC Citadel Meet", "Control", "Dormant"),
        new("song_20", "Black Thread States/Normal World/Lace Scene/Lace NPC Citadel Meet", "Control", "Dormant"),
        new("abyss_05", "Cutscene Options/Lace Abyss Ghost Cutscene", "Lace Cutscene", "Wait for trigger"),
        new("tut_04", "States/Intro Scene/Snail Shamans Set", "Dialogue", "Wait For Enter"),
        new("room_forge", "_NPCs/Forge Daughter", "Dialogue", "Act 3 Wait"),
        new("song_25", "Black Thread States/Black Thread World/Weakness Scene Act3 Final", "Control", "Dormant"),
        new(
            "aqueduct_05_festival",
            "Caravan_States/Flea_Festival_Intro/Caravan Troupe Leader Festival Intro",
            "Dialogue",
            "Wait For Enter"
        )
    ];

    /// <summary>
    /// The FSM and state names of <see cref="StoryScenes"/>, which most events are told apart by before anything else.
    /// </summary>
    private static readonly HashSet<(string FsmName, string StateName)> StoryStartStates =
        StoryScenes.Select(scene => (scene.FsmName, scene.StateName)).ToHashSet();

    /// <summary>
    /// The story scenes that the game sets in several rooms, and plays in whichever its player reaches first: those of
    /// <see cref="StoryScenes"/> with an entry in more than one scene.
    /// </summary>
    private static readonly HashSet<StoryKey> SharedStories = StoryScenes
        .GroupBy(scene => scene.Key)
        .Where(group => group.Count() > 1)
        .Select(group => group.Key)
        .ToHashSet();

    /// <summary>
    /// The names of the fields of the actions that notice the player in which they keep their trigger.
    /// </summary>
    private static readonly string[] TriggerFieldNames = ["gameObject", "trigger", "Target", "target"];

    /// <summary>
    /// The field of <see cref="SendFSMEventOnEntry"/> that holds the FSM it sends its event to.
    /// </summary>
    private static readonly FieldInfo? EntrySenderFsmField = typeof(SendFSMEventOnEntry).GetField("fsm", InstanceFlags);

    /// <summary>
    /// The message for a player whose story scene waits for the other players.
    /// </summary>
    private static string StoryWaitingMessage => Lang.Pick(
        "This scene waits until your teammate gets here too.",
        "这段剧情要等队友也走到这里才开始。"
    );

    /// <summary>
    /// The message for a player whose story scene may start, but who walked away from where they set it off.
    /// </summary>
    private static string StoryReadyMessage => Lang.Pick(
        "Walk back to where the scene began to start it.",
        "回到刚才的地方，剧情就会开始。"
    );

    /// <summary>
    /// The story scenes of the current scene that wait, by their FSM.
    /// </summary>
    private readonly Dictionary<Fsm, HeldStory> _heldStories = new();

    /// <summary>
    /// The story scene whose start is being sent again now that it may start, which is let through.
    /// </summary>
    private Fsm? _startingStory;

    /// <summary>
    /// The other players who said that they wait at a story scene that the game sets in several rooms, with the scene
    /// and when they last said so, in unscaled seconds.
    /// </summary>
    private readonly Dictionary<ushort, (StoryKey Story, float HeardAt)> _storyWaits = new();

    /// <summary>
    /// The names of the other players for whom a story scene that the game sets in several rooms started, with the
    /// scene. They count as having reached it until the local player closes their save, since they can't set it off
    /// again: also after either player lost the connection and came back, the other under another ID.
    /// </summary>
    private readonly HashSet<(string Username, StoryKey Story)> _storyStarts = [];

    /// <summary>
    /// Holds back the start of a story scene until every player has reached it.
    /// </summary>
    /// <returns>Whether the event is held back.</returns>
    private bool TryHoldStoryStart(Fsm fsm, FsmState state, string eventName) {
        if (eventName != StoryStartEventName || fsm == _startingStory || FindStoryScene(fsm, state) is not { } scene) {
            return false;
        }

        if (!_heldStories.TryGetValue(fsm, out var held)) {
            var shared = SharedStories.Contains(scene.Key) ? scene.Key : null;
            held = new HeldStory(state.Name, GetStoryTriggers(fsm, state.Name), shared) {
                NextNoticeTime = Time.unscaledTime + StoryNoticeDelay
            };
        }

        if (HaveAllReachedStory(held)) {
            _heldStories.Remove(fsm);
            TellStoryStarted(held);
            return false;
        }

        if (_heldStories.TryAdd(fsm, held)) {
            Logger.Info($"Holding back the story scene '{GetPath(fsm)}' until the other players reach it");
            TellWaitingAtStory(held);
        }

        var heroController = HeroController.instance;
        if (heroController != null) {
            held.Position = heroController.transform.position;
        }

        return true;
    }

    /// <summary>
    /// Finds the one of <see cref="StoryScenes"/> for which an FSM waits in a state for the player to walk into it.
    /// </summary>
    /// <returns>The story scene, or null if the FSM doesn't wait for one in that state.</returns>
    private static StoryScene? FindStoryScene(Fsm fsm, FsmState state) {
        if (!StoryStartStates.Contains((fsm.Name, state.Name)) || fsm.GameObject == null) {
            return null;
        }

        var sceneName = fsm.GameObject.scene.name;
        string? path = null;
        foreach (var scene in StoryScenes) {
            if (scene.FsmName != fsm.Name || scene.StateName != state.Name ||
                !string.Equals(scene.SceneName, sceneName, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            path ??= ScenePath.Get(fsm.GameObject.transform);
            if (path == scene.Path) {
                return scene;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether every other player has reached a story scene: is in the local scene and has been in one of the scene's
    /// triggers since it was held, or is now. Without triggers to look at, being in the scene is enough. For a scene
    /// that the game sets in several rooms, a player who waits at it in any of them, or for whom it started, has reached
    /// it too. The partner of a two-player save counts as not there while they aren't on the server. True when nobody
    /// else plays.
    /// </summary>
    private bool HaveAllReachedStory(HeldStory held) {
        if (!IsHoldActive()) {
            return true;
        }

        if (_isPartnerMissing()) {
            return false;
        }

        foreach (var playerData in _playerData.Values) {
            if (held.Shared is { } story && HasReachedSharedStory(playerData, story)) {
                continue;
            }

            if (!playerData.IsInLocalScene) {
                return false;
            }

            if (held.Triggers.Count == 0 || held.Arrived.Contains(playerData.Id)) {
                continue;
            }

            var container = playerData.PlayerContainer;
            if (container == null || !IsInStoryTriggers(held.Triggers, container.transform.position)) {
                return false;
            }

            held.Arrived.Add(playerData.Id);
        }

        return true;
    }

    /// <summary>
    /// Starts the story scenes that waited once every player has reached them, and tells the players about those that
    /// still wait.
    /// </summary>
    private void ReleaseHeldStories() {
        if (_heldStories.Count == 0) {
            return;
        }

        foreach (var pair in _heldStories.ToList()) {
            var fsm = pair.Key;
            var held = pair.Value;
            if (fsm.GameObject == null || fsm.ActiveState?.Name != held.StateName) {
                _heldStories.Remove(fsm);
                TellLeftStory(held);
                continue;
            }

            // Also while it waits only for the local player to be free to start it
            if (Time.unscaledTime >= held.NextReportTime) {
                TellWaitingAtStory(held);
            }

            if (!HaveAllReachedStory(held)) {
                if (Time.unscaledTime >= held.NextNoticeTime) {
                    held.NextNoticeTime = Time.unscaledTime + NoticeInterval;
                    UiManager.InternalChatBox.AddMessage(StoryWaitingMessage);
                    Send(BossRoomUpdateKind.Waiting, "", "", "", "", "");
                }

                continue;
            }

            TryStartHeldStory(fsm, held);
        }
    }

    /// <summary>
    /// Starts a story scene that may start now, once the local player is free to be taken into it, if they are still
    /// where they set it off. Otherwise it starts the next time they walk into it, as it does in the game.
    /// </summary>
    private void TryStartHeldStory(Fsm fsm, HeldStory held) {
        if (!IsLocalHeroInStoryTrigger(held)) {
            _heldStories.Remove(fsm);
            TellLeftStory(held);
            Logger.Info(
                $"The story scene '{GetPath(fsm)}' starts when the local player walks into it again, since every " +
                "player reached it"
            );
            UiManager.InternalChatBox.AddMessage(StoryReadyMessage);
            return;
        }

        // The game only sets a scene off as the player walks in, never in the middle of a menu, a talk or a rest
        if (!IsLocalHeroFree()) {
            return;
        }

        _heldStories.Remove(fsm);
        Logger.Info($"Starting the story scene '{GetPath(fsm)}', since every player reached it");
        TellStoryStarted(held);
        _startingStory = fsm;
        try {
            fsm.Event(StoryStartEventName);
        } finally {
            _startingStory = null;
        }
    }

    /// <summary>
    /// Whether the local player could walk into a story scene now: they move by themselves, no menu, map or talk is
    /// open, and they aren't resting, down, dead or changing scenes.
    /// </summary>
    private static bool IsLocalHeroFree() {
        var heroController = HeroController.instance;
        return heroController != null && heroController.acceptingInput && !heroController.controlReqlinquished &&
               !heroController.cState.dead && !PlayerTargetRegistry.IsPlayerDown(heroController.gameObject) &&
               PlayerData.instance is { disablePause: false, atBench: false } && IsDialogueRunning() != true &&
               global::GameManager.instance is not { IsInSceneTransition: true };
    }

    /// <summary>
    /// Whether the local player is in a trigger that starts a held story scene. When none could be found, whether
    /// they are still close to where they set it off.
    /// </summary>
    private static bool IsLocalHeroInStoryTrigger(HeldStory held) {
        var heroController = HeroController.instance;
        if (heroController == null) {
            return false;
        }

        var heroCollider = heroController.col2d;
        if (held.Triggers.Count == 0 || heroCollider == null || !heroCollider.enabled) {
            return Vector2.Distance(heroController.transform.position, held.Position) <= StoryRestartDistance;
        }

        foreach (var trigger in held.Triggers) {
            if (trigger != null && trigger.enabled && trigger.gameObject.activeInHierarchy &&
                trigger.Distance(heroCollider).isOverlapped) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a point is in one of the triggers of a story scene.
    /// </summary>
    private static bool IsInStoryTriggers(List<Collider2D> triggers, Vector2 point) {
        foreach (var trigger in triggers) {
            if (trigger != null && ContainsPoint(trigger, point)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the trigger colliders that start a story scene: those of the actions in its waiting state that notice the
    /// player, and those of the objects below the scene's own that send it an event as something walks into them.
    /// </summary>
    private static List<Collider2D> GetStoryTriggers(Fsm fsm, string stateName) {
        var triggers = new List<Collider2D>();

        foreach (var action in fsm.GetState(stateName)?.Actions ?? []) {
            var typeName = action.GetType().Name;
            if (!DetectionActionPrefixes.Any(prefix => typeName.StartsWith(prefix, StringComparison.Ordinal))) {
                continue;
            }

            foreach (var fieldName in TriggerFieldNames) {
                AddTriggerColliders(GetObjectOf(fsm, GetActionField(action, fieldName)), triggers);
            }
        }

        if (fsm.Owner is PlayMakerFSM owner && EntrySenderFsmField != null) {
            foreach (var sender in owner.GetComponentsInChildren<SendFSMEventOnEntry>(true)) {
                if (EntrySenderFsmField.GetValue(sender) as PlayMakerFSM == owner) {
                    AddTriggerColliders(sender.gameObject, triggers);
                }
            }
        }

        return triggers;
    }

    /// <summary>
    /// Gets the object that a field of an action names, whichever way it holds it.
    /// </summary>
    private static GameObject? GetObjectOf(Fsm fsm, object? value) {
        return value switch {
            FsmOwnerDefault owner => fsm.GetOwnerDefaultTarget(owner),
            FsmGameObject variable => variable.Value,
            FsmObject { Value: Component component } => component.gameObject,
            FsmObject { Value: GameObject gameObject } => gameObject,
            Component component => component.gameObject,
            GameObject gameObject => gameObject,
            _ => null
        };
    }

    /// <summary>
    /// Adds the trigger colliders of an object to a list, unless they are in it already.
    /// </summary>
    private static void AddTriggerColliders(GameObject? gameObject, List<Collider2D> triggers) {
        if (gameObject == null) {
            return;
        }

        foreach (var collider in gameObject.GetComponents<Collider2D>()) {
            if (collider.isTrigger && !triggers.Contains(collider)) {
                triggers.Add(collider);
            }
        }
    }

    /// <summary>
    /// Whether another player waits at a story scene that the game sets in several rooms, or it started for them.
    /// </summary>
    private bool HasReachedSharedStory(ClientPlayerData playerData, StoryKey story) {
        if (_storyStarts.Contains((playerData.Username, story))) {
            return true;
        }

        return _storyWaits.TryGetValue(playerData.Id, out var wait) && wait.Story == story &&
               Time.unscaledTime - wait.HeardAt <= StoryWaitReportLifetime;
    }

    /// <summary>
    /// Tells the other players, wherever they are, that the local player waits at a held story scene, if the game sets
    /// it in several rooms.
    /// </summary>
    private void TellWaitingAtStory(HeldStory held) {
        if (held.Shared is not { } story) {
            return;
        }

        held.NextReportTime = Time.unscaledTime + StoryWaitReportInterval;
        Send(BossRoomUpdateKind.WaitingAtStory, story.Path, story.FsmName, story.StateName, "", "");
    }

    /// <summary>
    /// Tells the other players, wherever they are, that a story scene started for the local player, if the game sets
    /// it in several rooms.
    /// </summary>
    private void TellStoryStarted(HeldStory held) {
        if (held.Shared is { } story) {
            Send(BossRoomUpdateKind.StartedStory, story.Path, story.FsmName, story.StateName, "", "");
        }
    }

    /// <summary>
    /// Tells the other players, wherever they are, that the local player no longer waits at a held story scene that
    /// didn't start, if the game sets it in several rooms.
    /// </summary>
    private void TellLeftStory(HeldStory held) {
        if (held.Shared is { } story) {
            Send(BossRoomUpdateKind.LeftStory, story.Path, story.FsmName, story.StateName, "", "");
        }
    }

    /// <summary>
    /// Takes the word of another player, wherever the local player is, that they wait at a story scene that the game
    /// sets in several rooms, that it started for them, or that they left it without it starting.
    /// </summary>
    private void OnStoryReport(BossRoomUpdate update) {
        var story = new StoryKey(update.Path, update.FsmName, update.FromState);
        if (!SharedStories.Contains(story)) {
            return;
        }

        var playerId = update.PlayerId;
        switch (update.Kind) {
            case BossRoomUpdateKind.WaitingAtStory:
                if (!_storyWaits.TryGetValue(playerId, out var wait) || wait.Story != story) {
                    Logger.Info($"Player {playerId} waits at the story scene '{story.Path}' in '{update.SceneName}'");
                }

                _storyWaits[playerId] = (story, Time.unscaledTime);
                break;
            case BossRoomUpdateKind.StartedStory:
                Logger.Info($"The story scene '{story.Path}' started for player {playerId} in '{update.SceneName}'");
                _storyWaits.Remove(playerId);
                if (_playerData.TryGetValue(playerId, out var playerData)) {
                    _storyStarts.Add((playerData.Username, story));
                }

                break;
            case BossRoomUpdateKind.LeftStory:
                if (_storyWaits.TryGetValue(playerId, out var left) && left.Story == story) {
                    Logger.Info($"Player {playerId} no longer waits at the story scene '{story.Path}'");
                    _storyWaits.Remove(playerId);
                }

                break;
        }
    }

    /// <summary>
    /// Forgets the story scenes of the previous scene. Those that waited for the other players no longer do.
    /// </summary>
    private void ClearStories() {
        foreach (var held in _heldStories.Values) {
            TellLeftStory(held);
        }

        _heldStories.Clear();
    }

    /// <summary>
    /// Forgets what the other players said about the story scenes that the game sets in several rooms once the local
    /// player closes their save, whether connected or not.
    /// </summary>
    private void ClearStoryReportsOnMenu(Scene oldScene, Scene newScene) {
        if (newScene.name != MenuSceneName) {
            return;
        }

        _storyWaits.Clear();
        _storyStarts.Clear();
    }

    /// <summary>
    /// A story scene that starts as the player walks into it.
    /// </summary>
    /// <param name="SceneName">The scene it is in, in any case.</param>
    /// <param name="Path">The path in the scene of the object of its FSM.</param>
    /// <param name="FsmName">The name of its FSM.</param>
    /// <param name="StateName">The state in which its FSM waits for the player.</param>
    private record StoryScene(string SceneName, string Path, string FsmName, string StateName) {
        /// <summary>
        /// What tells the scene apart in every room that the game sets it in.
        /// </summary>
        public StoryKey Key => new(Path, FsmName, StateName);
    }

    /// <summary>
    /// What tells a story scene apart in every room that the game sets it in.
    /// </summary>
    /// <param name="Path">The path in the scene of the object of its FSM.</param>
    /// <param name="FsmName">The name of its FSM.</param>
    /// <param name="StateName">The state in which its FSM waits for the player.</param>
    private record StoryKey(string Path, string FsmName, string StateName);

    /// <summary>
    /// The start of a story scene that waits.
    /// </summary>
    private class HeldStory {
        /// <summary>
        /// The state of the FSM that waits for the event.
        /// </summary>
        public readonly string StateName;

        /// <summary>
        /// The triggers that start the scene.
        /// </summary>
        public readonly List<Collider2D> Triggers;

        /// <summary>
        /// The IDs of the other players whose figure has been in one of the triggers since the scene was held.
        /// </summary>
        public readonly HashSet<ushort> Arrived = [];

        /// <summary>
        /// The scene, if the game sets it in several rooms, so that a player who waits at it in another counts too.
        /// </summary>
        public readonly StoryKey? Shared;

        /// <summary>
        /// Where the local player was when they last set the scene off.
        /// </summary>
        public Vector2 Position;

        /// <summary>
        /// When the players hear next that the local player waits, in unscaled seconds.
        /// </summary>
        public float NextNoticeTime;

        /// <summary>
        /// When the other games hear next that the local player waits at the scene, if the game sets it in several
        /// rooms, in unscaled seconds.
        /// </summary>
        public float NextReportTime;

        public HeldStory(string stateName, List<Collider2D> triggers, StoryKey? shared) {
            StateName = stateName;
            Triggers = triggers;
            Shared = shared;
        }
    }
}
