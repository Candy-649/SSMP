using System;
using System.Collections.Generic;
using System.Linq;
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
/// Story scenes that a player's talk sets off and that play once: a character that wakes when a player hands it what it
/// asks for (USER 11-27 "改吧", after "按照现在的做法，它应该属于有一次性的剧情见面？"). Like the scenes that start as a
/// player walks into them (see BossRoomCoop.Story), such a scene was there for one player only: it played in the game
/// of the player who handed it over, and only their save marked it, while what the game makes of that mark later
/// reached the other saves too, which then said that the character still slept and had gone on all at once.
///
/// The talk asks for what it takes only once every player has reached the character: until then the game answers as it
/// does for a player who lacks it, the talk ends, and the player hears to wait. Once a player has handed it over, every
/// other player in the room watches the scene in their own game: the game takes them into the talk as it does when they
/// press to talk, once they stand next to the character and could, and the character goes on from the step after the
/// handing over, as it does for the player who handed it over. Only that player gives anything. Each game marks the
/// scene in its own save as it plays it, and the mark is a flag of the shared world, so the save of a player who was
/// not there has it too.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// The event with which the talk of a story scene that a talk sets off says that the player holds what it takes.
    /// </summary>
    private const string TalkStoryHoldsEventName = "YES";

    /// <summary>
    /// The event with which the talk of a story scene that a talk sets off says that the player lacks what it takes,
    /// which ends the talk.
    /// </summary>
    private const string TalkStoryLacksEventName = "NO";

    /// <summary>
    /// The event with which the player hands over what the talk of a story scene that a talk sets off takes, at its
    /// prompt.
    /// </summary>
    private const string TalkStoryHandsEventName = "YES";

    /// <summary>
    /// The event with which a character's talk starts, sent as the player presses to talk.
    /// </summary>
    private const string TalkStoryTalkEventName = "INTERACT";

    /// <summary>
    /// The story scenes that a player's talk sets off and that play once, found in the game's data: the character's
    /// room shows it only while the flag that the scene sets isn't set as the room loads.
    /// </summary>
    private static readonly TalkStory[] TalkStories = [
        new(
            "cog_10",
            "Song Knight Scene/Song Knight Sleeping",
            "Control",
            "Tube Opened",
            "Has Heart?",
            "Give Heart Prompt",
            "Chose Yes"
        )
    ];

    /// <summary>
    /// The FSM and state names of <see cref="TalkStories"/> in which their events are taken, which most events are told
    /// apart by before anything else.
    /// </summary>
    private static readonly HashSet<(string FsmName, string StateName)> TalkStoryStates = TalkStories
        .SelectMany(story => new[] {
            (story.FsmName, story.WaitState), (story.FsmName, story.AskState), (story.FsmName, story.HandState)
        })
        .ToHashSet();

    /// <summary>
    /// The message for a player whose talk held back a story scene, since the other players aren't next to its
    /// character yet.
    /// </summary>
    private string TalkStoryWaitingMessage => _playerData.Count <= 1
        ? Lang.Pick(
            "This waits until your teammate is next to it too: talk to it again once they are.",
            "要等队友也走到它旁边才行，到了以后再跟它说话。"
        )
        : Lang.Pick(
            "This waits until your teammates are next to it too: talk to it again once they are.",
            "要等队友们也走到它旁边才行，到齐以后再跟它说话。"
        );

    /// <summary>
    /// The message for a player whose talk held back a story scene, once every player has reached its character.
    /// </summary>
    private static string TalkStoryReadyMessage => Lang.Pick(
        "Everyone is next to it now: talk to it again.",
        "人都到齐了，再跟它说话就行。"
    );

    /// <summary>
    /// The message for a player who isn't next to the character of a story scene that another player set off in the
    /// room, which plays for them once they are.
    /// </summary>
    private static string TalkStoryComeMessage => Lang.Pick(
        "A teammate set off a scene here: walk up to where they talked to watch it too.",
        "队友在这里开始了一段剧情，走到他们刚才说话的地方就能一起看。"
    );

    /// <summary>
    /// The story scenes of the current scene whose talk the local player held back, by their FSM, until every player
    /// has reached their character.
    /// </summary>
    private readonly Dictionary<Fsm, HeldStory> _waitingTalkStories = new();

    /// <summary>
    /// The FSMs of <see cref="_waitingTalkStories"/> whose players heard that every player has reached their character.
    /// A player who reached it counts as there until its talk asks again, also once they stepped away.
    /// </summary>
    private readonly HashSet<Fsm> _readyTalkStories = [];

    /// <summary>
    /// The story scenes of the current scene that another player set off, by their FSM, which play here once the local
    /// player is next to their character and free to talk.
    /// </summary>
    private readonly Dictionary<Fsm, ToldTalkStory> _toldTalkStories = new();

    /// <summary>
    /// Takes an event of the talk of a story scene that a talk sets off: holds back the question of what it takes until
    /// every player has reached its character, tells the other players in the room as the local player hands it over,
    /// and goes on from the step after the handing over as the local player is taken into the talk of one that another
    /// player set off.
    /// </summary>
    /// <returns>Whether the event was taken here instead of by the FSM.</returns>
    private bool TryTakeTalkStoryEvent(Fsm fsm, FsmState state, string eventName) {
        if (!TalkStoryStates.Contains((fsm.Name, state.Name)) || FindTalkStory(fsm) is not { } story) {
            return false;
        }

        if (state.Name == story.WaitState) {
            if (eventName != TalkStoryTalkEventName || !_toldTalkStories.Remove(fsm)) {
                return false;
            }

            Logger.Info(
                $"Playing the story scene '{GetPath(fsm)}' from '{story.SceneState}', as another player set it off"
            );
            fsm.SetStateAsItRuns(story.SceneState);
            return true;
        }

        if (state.Name == story.HandState) {
            if (eventName == TalkStoryHandsEventName) {
                ForgetWaitingTalkStory(fsm);
                _toldTalkStories.Remove(fsm);
                Logger.Info($"The local player set off the story scene '{GetPath(fsm)}', which every player plays");
                Send(BossRoomUpdateKind.TalkStoryStarted, fsm, story.HandState, story.SceneState, eventName);
            }

            return false;
        }

        if (eventName != TalkStoryHoldsEventName) {
            return false;
        }

        if (!_waitingTalkStories.TryGetValue(fsm, out var held)) {
            held = new HeldStory(story.WaitState, GetTalkStoryTriggers(fsm), null);
        }

        if (HaveAllReachedStory(held)) {
            ForgetWaitingTalkStory(fsm);
            return false;
        }

        if (_waitingTalkStories.TryAdd(fsm, held)) {
            Logger.Info($"Holding back the story scene '{GetPath(fsm)}' until the other players reach it");
        }

        UiManager.InternalChatBox.AddMessage(TalkStoryWaitingMessage);
        TellTeammatesWaiting();

        // The game's own answer for a player who lacks what it takes, which ends the talk
        fsm.Event(TalkStoryLacksEventName);
        return true;
    }

    /// <summary>
    /// Finds the one of <see cref="TalkStories"/> that an FSM plays.
    /// </summary>
    /// <returns>The story scene, or null if the FSM plays none.</returns>
    private static TalkStory? FindTalkStory(Fsm fsm) {
        if (fsm.GameObject == null) {
            return null;
        }

        var sceneName = fsm.GameObject.scene.name;
        string? path = null;
        foreach (var story in TalkStories) {
            if (story.FsmName != fsm.Name ||
                !string.Equals(story.SceneName, sceneName, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            path ??= ScenePath.Get(fsm.GameObject.transform);
            if (path == story.Path) {
                return story;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the trigger colliders in which a player talks to the character of a story scene that a talk sets off.
    /// </summary>
    private static List<Collider2D> GetTalkStoryTriggers(Fsm fsm) {
        var triggers = new List<Collider2D>();
        if (fsm.GameObject == null) {
            return triggers;
        }

        foreach (var npc in fsm.GameObject.GetComponentsInChildren<PlayMakerNPC>(true)) {
            AddTriggerColliders(npc.gameObject, triggers);
        }

        return triggers;
    }

    /// <summary>
    /// Tells the player whose talk held back a story scene once every player has reached its character, and takes the
    /// local player into the talk of a story scene that another player set off once they are next to its character
    /// and could talk to it: as the game does when they press to talk, which the scene then goes on from (see
    /// <see cref="TryTakeTalkStoryEvent"/>).
    /// </summary>
    private void UpdateTalkStories() {
        if (_waitingTalkStories.Count > 0) {
            foreach (var (fsm, held) in _waitingTalkStories.ToList()) {
                if (fsm.GameObject == null) {
                    ForgetWaitingTalkStory(fsm);
                } else if (HaveAllReachedStory(held) && _readyTalkStories.Add(fsm)) {
                    Logger.Info(
                        $"Every player reached the story scene '{GetPath(fsm)}', which starts as it is talked to"
                    );
                    UiManager.InternalChatBox.AddMessage(TalkStoryReadyMessage);
                }
            }
        }

        if (_toldTalkStories.Count == 0) {
            return;
        }

        foreach (var (fsm, told) in _toldTalkStories.ToList()) {
            if (fsm.GameObject == null) {
                _toldTalkStories.Remove(fsm);
                continue;
            }

            // Not while the local player talks to it themselves, which ends as it does in their game
            if (told.Pressed || fsm.ActiveState?.Name != told.Story.WaitState || !IsLocalHeroFree() ||
                HeroController.instance is not { } heroController || !heroController.CanInteract()) {
                continue;
            }

            if (!IsLocalHeroInStoryTrigger(told.Where)) {
                if (!told.Noticed) {
                    told.Noticed = true;
                    UiManager.InternalChatBox.AddMessage(TalkStoryComeMessage);
                }

                continue;
            }

            var npc = fsm.GameObject.GetComponentInChildren<PlayMakerNPC>();
            if (npc == null) {
                continue;
            }

            told.Pressed = true;
            Logger.Info($"Taking the local player into the talk of the story scene '{GetPath(fsm)}'");
            npc.QueueInteraction();
        }
    }

    /// <summary>
    /// Takes the word of another player in the local scene that they handed over what the talk of a story scene takes.
    /// Its scene plays here too, once the local player is next to its character and free to talk; if this game marked
    /// it already, its character isn't in the room.
    /// </summary>
    private void OnTalkStoryStarted(BossRoomUpdate update) {
        var sceneName = SceneManager.GetActiveScene().name;
        var story = TalkStories.FirstOrDefault(each =>
            each.Path == update.Path && each.FsmName == update.FsmName &&
            string.Equals(each.SceneName, sceneName, StringComparison.OrdinalIgnoreCase)
        );
        if (story == null || FindFsm(update.Path, update.FsmName) is not { } fsm || fsm.GameObject == null) {
            return;
        }

        ForgetWaitingTalkStory(fsm);
        if (_toldTalkStories.ContainsKey(fsm)) {
            return;
        }

        Logger.Info(
            $"Player {update.PlayerId} set off the story scene '{update.Path}', which plays here once the local " +
            "player is next to it and free to talk"
        );
        _toldTalkStories[fsm] = new ToldTalkStory(
            story,
            new HeldStory(story.WaitState, GetTalkStoryTriggers(fsm), null) {
                Position = fsm.GameObject.transform.position
            }
        );
    }

    /// <summary>
    /// Forgets that the talk of a story scene waits for the other players.
    /// </summary>
    private void ForgetWaitingTalkStory(Fsm fsm) {
        _waitingTalkStories.Remove(fsm);
        _readyTalkStories.Remove(fsm);
    }

    /// <summary>
    /// Forgets the story scenes that a talk sets off of the previous scene.
    /// </summary>
    private void ClearTalkStories() {
        _waitingTalkStories.Clear();
        _readyTalkStories.Clear();
        _toldTalkStories.Clear();
    }

    /// <summary>
    /// A story scene that a player's talk sets off and that plays once.
    /// </summary>
    /// <param name="SceneName">The scene it is in, in any case.</param>
    /// <param name="Path">The path in the scene of the object of its FSM.</param>
    /// <param name="FsmName">The name of its FSM.</param>
    /// <param name="WaitState">The state in which its FSM waits to be talked to.</param>
    /// <param name="AskState">The state of the talk that asks whether the player holds what it takes.</param>
    /// <param name="HandState">The state of the talk at whose prompt the player hands it over.</param>
    /// <param name="SceneState">The state that the scene goes on from after the handing over.</param>
    private record TalkStory(
        string SceneName,
        string Path,
        string FsmName,
        string WaitState,
        string AskState,
        string HandState,
        string SceneState
    );

    /// <summary>
    /// A story scene that another player set off, which plays here once the local player is next to its character.
    /// </summary>
    private class ToldTalkStory {
        /// <summary>
        /// The story scene.
        /// </summary>
        public readonly TalkStory Story;

        /// <summary>
        /// Where its character is talked to.
        /// </summary>
        public readonly HeldStory Where;

        /// <summary>
        /// Whether the local player was taken into its talk, whose start the scene then goes on from.
        /// </summary>
        public bool Pressed;

        /// <summary>
        /// Whether the local player heard that they need to walk up to its character.
        /// </summary>
        public bool Noticed;

        public ToldTalkStory(TalkStory story, HeldStory where) {
            Story = story;
            Where = where;
        }
    }
}
