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
/// Story scenes that a player's talk sets off and that play once: a character that wakes when it is handed what it asks
/// for (USER 11-27 "改吧", after "按照现在的做法，它应该属于有一次性的剧情见面？"). Like the scenes that start as a player
/// walks into them (see BossRoomCoop.Story), such a scene was there for one player only: it played in the game of the
/// player who handed it over, and only their save marked it, while what the game makes of that mark later reached the
/// other saves too, which then said that the character still slept and had gone on all at once.
///
/// Every player hands over their own share, at their own prompt, and nobody's prompt is answered for them (USER 11-27
/// "不，你这个“替玩家选否”的思路，我实在有太多阴影了，如果这个东西只有一个，那就每个人都要交自己那份，那就所有人都要有",
/// then "A"). The character wakes once every player has handed theirs over and every player is next to it, in every
/// game at once. A player who hands theirs over before that hears what it waits for, and their talk goes on to where
/// the game ends it, with what they handed over kept in the marker of the save (CoopSave.TalkStoriesPaid), as the
/// scene may wait for a player who comes another time. Once it may wake, each game takes its player into the talk as
/// it does when they press to talk, and the character goes on from the step after the handing over; the player who
/// hands over the last share goes on in their own talk. A game that plays it tells the others in the room, which then
/// wait only for their own player to be next to it, as each game sees the figures of the others a moment late. Each
/// game marks the scene in its own save as it plays it, and the mark is a flag of the shared world that a check and a
/// join keep if either save has it.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// The event with which the player hands over their share of what the talk of a story scene takes, at its prompt.
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
            "Give Heart Prompt",
            "End Dialogue",
            "Chose Yes"
        )
    ];

    /// <summary>
    /// The FSM and state names of <see cref="TalkStories"/> in which their events are taken, which most events are told
    /// apart by before anything else.
    /// </summary>
    private static readonly HashSet<(string FsmName, string StateName)> TalkStoryStates = TalkStories
        .SelectMany(story => new[] { (story.FsmName, story.WaitState), (story.FsmName, story.HandState) })
        .ToHashSet();

    /// <summary>
    /// The message for a player who handed over their share of what a story scene takes, while it waits. Another player
    /// tells of their own share only to the players in their scene, so this game may not have heard of it yet.
    /// </summary>
    private static string TalkStoryPaidMessage => Lang.Pick(
        "Handed over. This starts once everyone has handed theirs over and is next to it.",
        "交好了。等每个人都交了自己那份、也都走到它旁边，才会开始。"
    );

    /// <summary>
    /// The message for the players of a story scene for which every player handed over their share, while not every
    /// player is next to its character.
    /// </summary>
    private static string TalkStoryGatherMessage => Lang.Pick(
        "Everyone has handed theirs over: this starts once everyone is next to it.",
        "大家都交好了，等所有人都走到它旁边就开始。"
    );

    /// <summary>
    /// The message for a player who hasn't handed over their share of what a story scene takes, as a teammate does.
    /// </summary>
    private static string TalkStoryTeammatePaidMessage => Lang.Pick(
        "A teammate handed over what it asks for: this starts once everyone has handed theirs over.",
        "队友交了它要的东西，每个人都交了才会开始。"
    );

    /// <summary>
    /// The message for a player who handed over their share of what a story scene takes and isn't next to its
    /// character, as it starts in the game of another player in the room.
    /// </summary>
    private static string TalkStoryComeMessage => Lang.Pick(
        "It has started for a teammate: walk up to it to watch it too.",
        "队友那边已经开始了，走到它旁边就能一起看。"
    );

    /// <summary>
    /// Whether the local player handed over their share for a story scene in the loaded co-op save, by its scene and
    /// path (CoopSave.IsTalkStoryPaid). Set by the game's client, as the two-player save is made after this.
    /// </summary>
    public Func<string, bool>? IsTalkStoryPaidInSave { get; set; }

    /// <summary>
    /// Notes in the loaded co-op save that the local player handed over their share for a story scene, by its scene
    /// and path (CoopSave.MarkTalkStoryPaid). Set by the game's client, as the two-player save is made after this.
    /// </summary>
    public Action<string>? MarkTalkStoryPaidInSave { get; set; }

    /// <summary>
    /// The story scenes for which the local player handed over their share since the save was loaded, by their scene
    /// and path: for a save that isn't a co-op save, which has no marker to keep it in.
    /// </summary>
    private readonly HashSet<string> _paidTalkStories = new(StringComparer.Ordinal);

    /// <summary>
    /// The story scenes that a talk sets off of the current scene, by their FSM, as the local player or another player
    /// had to do with them.
    /// </summary>
    private readonly Dictionary<Fsm, TalkStoryVisit> _talkStoryVisits = new();

    /// <summary>
    /// Takes an event of the talk of a story scene that a talk sets off: as the local player hands over their share,
    /// tells the other players in the room, and lets the talk go on into the scene if it may wake now, or goes on to
    /// where the game ends the talk; as the local player is taken into the talk once it may wake, or presses to talk
    /// then, goes on from the step after the handing over; and as a player who handed theirs over presses to talk while
    /// it waits, ends the talk and tells them what it waits for.
    /// </summary>
    /// <returns>Whether the event was taken here instead of by the FSM.</returns>
    private bool TryTakeTalkStoryEvent(Fsm fsm, FsmState state, string eventName) {
        if (!TalkStoryStates.Contains((fsm.Name, state.Name)) || FindTalkStory(fsm) is not { } story) {
            return false;
        }

        var visit = GetTalkStoryVisit(fsm, story);
        if (state.Name == story.WaitState) {
            if (eventName != TalkStoryTalkEventName) {
                return false;
            }

            // The game lets a player talk only from within its trigger, and moves them to where they talk from
            if (MayTalkStoryWake(visit, true)) {
                Logger.Info($"Playing the story scene '{GetPath(fsm)}' from '{story.SceneState}', as all are there");
                Send(BossRoomUpdateKind.TalkStoryPlayed, fsm, story.WaitState, story.SceneState, eventName);
                fsm.SetStateAsItRuns(story.SceneState);
                return true;
            }

            if (!IsLocalTalkStoryPaid(story)) {
                return false;
            }

            // A player who handed theirs over doesn't do so again: the talk, which takes no event, ends as the game
            // ends it then (PlayMakerNPC.OnStartDialogue), and they hear what the scene waits for
            Logger.Info($"The story scene '{GetPath(fsm)}' still waits, for which the local player handed theirs over");
            UiManager.InternalChatBox.AddMessage(HaveOthersPaid(visit) ? TalkStoryGatherMessage : TalkStoryPaidMessage);
            return true;
        }

        // A no is the player's own, which the game takes its own way
        if (eventName != TalkStoryHandsEventName) {
            return false;
        }

        _paidTalkStories.Add(story.Key);
        MarkTalkStoryPaidInSave?.Invoke(story.Key);
        Send(BossRoomUpdateKind.TalkStoryPaid, fsm, story.HandState, "", eventName);

        if (MayTalkStoryWake(visit, true)) {
            Logger.Info($"The local player handed over the last share for the story scene '{GetPath(fsm)}'");
            Send(BossRoomUpdateKind.TalkStoryPlayed, fsm, story.HandState, story.SceneState, eventName);
            return false;
        }

        var allPaid = HaveOthersPaid(visit);
        Logger.Info(
            $"The local player handed over their share for the story scene '{GetPath(fsm)}', which waits for " +
            (allPaid ? "every player to be next to it" : "the other players' shares")
        );
        visit.HeardAllPaid |= allPaid;
        UiManager.InternalChatBox.AddMessage(allPaid ? TalkStoryGatherMessage : TalkStoryPaidMessage);

        // The talk waits in the prompt's state for the frame, and goes on to its end from outside the FSM's own run, as
        // a state that it is still in the middle of is no place to switch it from (see UpdateTalkStories)
        visit.EndsPaidTalk = true;
        return true;
    }

    /// <summary>
    /// Whether the character of a story scene that a talk sets off may wake now for the local player, who handed over
    /// their share: every player handed over theirs and every player is next to it, or it started in the game of
    /// another player in the room and the local player is next to it. The local player counts as next to it while
    /// they talk to it.
    /// </summary>
    private bool MayTalkStoryWake(TalkStoryVisit visit, bool isLocalTalking) {
        if (!IsLocalTalkStoryPaid(visit.Story)) {
            return false;
        }

        if (!IsHoldActive()) {
            return true;
        }

        // A game sees the figures of the others a moment late, so one game may find them all there while another
        // just doesn't: it plays here as well, rather than wait for players who were there as it started
        if (visit.StartedElsewhere) {
            return isLocalTalking || IsLocalHeroInStoryTrigger(visit.Where);
        }

        if (!HaveOthersPaid(visit) || (!isLocalTalking && !IsLocalHeroInStoryTrigger(visit.Where))) {
            return false;
        }

        foreach (var playerData in _playerData.Values) {
            if (!playerData.IsInLocalScene || !IsFigureInStoryTriggers(visit.Where.Triggers, playerData)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether every other player handed over their share for a story scene that a talk sets off, as they said in the
    /// room. The partner of a two-player save hasn't while they aren't on the server.
    /// </summary>
    private bool HaveOthersPaid(TalkStoryVisit visit) {
        if (!IsHoldActive()) {
            return true;
        }

        if (_isPartnerMissing()) {
            return false;
        }

        foreach (var playerData in _playerData.Values) {
            if (!visit.Payers.Contains(playerData.Id)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the local player handed over their share for a story scene that a talk sets off, in this session or, in
    /// a co-op save, before.
    /// </summary>
    private bool IsLocalTalkStoryPaid(TalkStory story) {
        return _paidTalkStories.Contains(story.Key) || IsTalkStoryPaidInSave?.Invoke(story.Key) == true;
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
    /// Gets what the current scene knows of a story scene that a talk sets off, made as it is first needed.
    /// </summary>
    private TalkStoryVisit GetTalkStoryVisit(Fsm fsm, TalkStory story) {
        if (_talkStoryVisits.TryGetValue(fsm, out var visit)) {
            return visit;
        }

        var where = new HeldStory(story.WaitState, GetTalkStoryTriggers(fsm), null);
        if (fsm.GameObject != null) {
            where.Position = fsm.GameObject.transform.position;
        }

        visit = new TalkStoryVisit(fsm, story, where);
        _talkStoryVisits[fsm] = visit;
        return visit;
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
    /// Ends the talk in which the local player handed over their share of a story scene that waits, and takes the local
    /// player into the talk once it may wake and they could talk to it: as the game does when they press to talk, which
    /// the scene then goes on from (see <see cref="TryTakeTalkStoryEvent"/>).
    /// </summary>
    private void UpdateTalkStories() {
        if (_talkStoryVisits.Count == 0) {
            return;
        }

        foreach (var (fsm, visit) in _talkStoryVisits.ToList()) {
            if (fsm.GameObject == null) {
                _talkStoryVisits.Remove(fsm);
                continue;
            }

            var stateName = fsm.ActiveState?.Name;
            if (visit.EndsPaidTalk) {
                visit.EndsPaidTalk = false;
                if (stateName == visit.Story.HandState) {
                    fsm.SetStateAsItRuns(visit.Story.EndState);
                }

                continue;
            }

            // Not while the local player is in a talk, a menu or the air, nor while the game moves them to talk: it
            // takes their control for that. Pressed again once they are free, if that came to nothing
            if (stateName != visit.Story.WaitState || !MayTalkStoryWake(visit, false) || !IsLocalHeroFree() ||
                HeroController.instance is not { } heroController || !heroController.CanInteract() ||
                fsm.GameObject.GetComponentInChildren<PlayMakerNPC>() is not { } npc) {
                continue;
            }

            Logger.Info($"Taking the local player into the talk of the story scene '{GetPath(fsm)}'");
            npc.QueueInteraction();
        }
    }

    /// <summary>
    /// Takes the word of another player in the local scene that they handed over their share of what the talk of a
    /// story scene takes, now or before, and tells the local player once what the scene waits for: their own share, or
    /// once every player handed theirs over, every player next to it. If this game marked the scene already, its
    /// character isn't in the room.
    /// </summary>
    private void OnTalkStoryPaid(BossRoomUpdate update) {
        if (FindTalkStoryVisit(update) is not { } visit || !visit.Payers.Add(update.PlayerId)) {
            return;
        }

        Logger.Info($"Player {update.PlayerId} handed over their share for the story scene '{update.Path}'");
        if (!IsLocalTalkStoryPaid(visit.Story)) {
            if (!visit.HeardTeammatePaid) {
                visit.HeardTeammatePaid = true;
                UiManager.InternalChatBox.AddMessage(TalkStoryTeammatePaidMessage);
            }
        } else if (!visit.HeardAllPaid && HaveOthersPaid(visit) && !MayTalkStoryWake(visit, false)) {
            visit.HeardAllPaid = true;
            UiManager.InternalChatBox.AddMessage(TalkStoryGatherMessage);
        }
    }

    /// <summary>
    /// Takes the word of another player in the local scene that a story scene that a talk sets off started in their
    /// game, which it then does here too once the local player is next to it, if they handed over their share. A
    /// player who isn't next to it hears to walk up.
    /// </summary>
    private void OnTalkStoryPlayed(BossRoomUpdate update) {
        if (FindTalkStoryVisit(update) is not { } visit || visit.StartedElsewhere) {
            return;
        }

        visit.StartedElsewhere = true;
        Logger.Info($"The story scene '{update.Path}' started in the game of player {update.PlayerId}");
        if (IsLocalTalkStoryPaid(visit.Story) && visit.Fsm.ActiveState?.Name == visit.Story.WaitState &&
            !IsLocalHeroInStoryTrigger(visit.Where)) {
            UiManager.InternalChatBox.AddMessage(TalkStoryComeMessage);
        }
    }

    /// <summary>
    /// Forgets what a player who left the local scene said of the story scenes that a talk sets off: they say it again
    /// as they come back in, from the save they play then.
    /// </summary>
    public void OnPlayerLeaveScene(ushort id) {
        foreach (var visit in _talkStoryVisits.Values) {
            visit.Payers.Remove(id);
        }
    }

    /// <summary>
    /// Finds what the current scene knows of the story scene that a word of another player in the local scene is about.
    /// </summary>
    /// <returns>The story scene, or null if the local scene has none such, or no longer its character.</returns>
    private TalkStoryVisit? FindTalkStoryVisit(BossRoomUpdate update) {
        var sceneName = SceneManager.GetActiveScene().name;
        var story = TalkStories.FirstOrDefault(each =>
            each.Path == update.Path && each.FsmName == update.FsmName &&
            string.Equals(each.SceneName, sceneName, StringComparison.OrdinalIgnoreCase)
        );
        return story != null && FindFsm(update.Path, update.FsmName) is { GameObject: not null } fsm
            ? GetTalkStoryVisit(fsm, story)
            : null;
    }

    /// <summary>
    /// Tells the other players in the local scene, as one comes in, for which story scenes of the scene the local
    /// player handed over their share: also in another session, which those who were not there could not hear.
    /// </summary>
    private void TellTalkStoriesPaid() {
        var sceneName = SceneManager.GetActiveScene().name;
        foreach (var story in TalkStories) {
            if (string.Equals(story.SceneName, sceneName, StringComparison.OrdinalIgnoreCase) &&
                IsLocalTalkStoryPaid(story)) {
                Send(BossRoomUpdateKind.TalkStoryPaid, story.Path, story.FsmName, story.HandState, "",
                    TalkStoryHandsEventName);
            }
        }
    }

    /// <summary>
    /// Forgets the story scenes that a talk sets off of the previous scene.
    /// </summary>
    private void ClearTalkStories() {
        _talkStoryVisits.Clear();
    }

    /// <summary>
    /// A story scene that a player's talk sets off and that plays once.
    /// </summary>
    /// <param name="SceneName">The scene it is in, in any case.</param>
    /// <param name="Path">The path in the scene of the object of its FSM.</param>
    /// <param name="FsmName">The name of its FSM.</param>
    /// <param name="WaitState">The state in which its FSM waits to be talked to.</param>
    /// <param name="HandState">The state of the talk at whose prompt the player hands over what it takes.</param>
    /// <param name="EndState">The state in which the game ends the talk, back to waiting to be talked to.</param>
    /// <param name="SceneState">The state that the scene goes on from after the handing over.</param>
    private record TalkStory(
        string SceneName,
        string Path,
        string FsmName,
        string WaitState,
        string HandState,
        string EndState,
        string SceneState
    ) {
        /// <summary>
        /// What tells the scene apart in the marker of a save: its scene and path.
        /// </summary>
        public string Key => SceneName + "/" + Path;
    }

    /// <summary>
    /// What the current scene knows of a story scene that a talk sets off.
    /// </summary>
    private class TalkStoryVisit {
        /// <summary>
        /// The FSM that plays it.
        /// </summary>
        public readonly Fsm Fsm;

        /// <summary>
        /// The story scene.
        /// </summary>
        public readonly TalkStory Story;

        /// <summary>
        /// Where its character is talked to.
        /// </summary>
        public readonly HeldStory Where;

        /// <summary>
        /// The IDs of the other players in the scene who said that they handed over their share for it.
        /// </summary>
        public readonly HashSet<ushort> Payers = [];

        /// <summary>
        /// Whether the talk in which the local player handed over their share goes on to its end as the next frame
        /// comes, as the scene waits.
        /// </summary>
        public bool EndsPaidTalk;

        /// <summary>
        /// Whether it started in the game of another player in the scene.
        /// </summary>
        public bool StartedElsewhere;

        /// <summary>
        /// Whether the local player heard that every player handed theirs over while not every player is there.
        /// </summary>
        public bool HeardAllPaid;

        /// <summary>
        /// Whether the local player heard that a teammate handed theirs over, before they did.
        /// </summary>
        public bool HeardTeammatePaid;

        public TalkStoryVisit(Fsm fsm, TalkStory story, HeldStory where) {
            Fsm = fsm;
            Story = story;
            Where = where;
        }
    }
}
