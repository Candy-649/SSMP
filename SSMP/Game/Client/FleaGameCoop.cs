using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity;
using SSMP.Game.Client.Save;
using SSMP.Hooks;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client;

/// <summary>
/// Co-op rules for the games of the festival, where the fleas that fly about are hit or dodged for points.
///
/// One of the three games is played by each player on their own (see <see cref="PersonalPlaces"/>): each game sends
/// out its own fleas for its own player, the others do not see them, and the score, the saving and how hard it gets
/// are all as when playing alone. Going for the same fleas there left one of the two players with nothing to land on.
///
/// The other two are played with the same fleas. The fleas are entities, so the scene host runs them and the other
/// games only show where they are. A hit of another game's player on a flea goes to the scene host like a hit on any
/// other copy (see CoopHits), so the flea answers it there, and what the fleas then say about the game - a point, a
/// flea tinked or dropped - is replayed in the other games' copies of that game (see EntityFsmActions). So the
/// players score together: a point is a point for all, whoever hit the flea or got past it, and a dropped flea counts
/// against all. Each game still counts, shows and saves the score as when playing alone.
///
/// Since the scene host's game sends those fleas out, a round of such a game goes on only while the scene host plays
/// it: once they have played along and their round is over, the other players' rounds of the same game end too.
/// Putting the fleas away at the end of the scene host's round waits until every other player's round is over as well.
///
/// The games tell each other which game their player is in, the score of the round and whether they run the room, so
/// that each player can be shown where the others stand. The score is sent as a total rather than as single points, so
/// a lost or a repeated message cannot make it drift.
///
/// What else happens in the room during such a game is the scene host's game's to say too (see
/// <see cref="OnSendEventByNameEnter"/>).
/// </summary>
internal class FleaGameCoop {
    /// <summary>
    /// Binding flags for the private members of the game.
    /// </summary>
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Events that mean a game of the festival is starting over, so the counts of both players begin again.
    /// </summary>
    private static readonly string[] ResetEventNames = ["GAME BEGIN PLAY", "RESET FLEA GAMES"];

    /// <summary>
    /// Name of the state machine that runs one of the games, counts the score and saves it.
    /// </summary>
    private const string MasterFsmName = "flea_game_master_control";

    /// <summary>
    /// Name of the state machine of a game that runs its rounds: when the fleas come, and what else happens in the room
    /// around them.
    /// </summary>
    private const string DirectorFsmName = "Game Specific Control";

    /// <summary>
    /// Name of what one game sends in now and then to throw something at the player. It is an entity, so only the
    /// scene host's game runs it, and it only ever threw at the scene host's player.
    /// </summary>
    private const string ThrowerName = "Flea Hunter DodgeGame";

    /// <summary>
    /// The states of a game that mean nobody is playing it. Whether the local player is in a game is read from the
    /// game itself rather than from the events it sends, because one event name of a game is not told apart from
    /// another's in what can be read of them.
    /// </summary>
    private static readonly string[] IdleStateNames = ["Init", "Inactive"];

    /// <summary>
    /// Name of the variable in which a game counts the score of the round being played.
    /// </summary>
    private const string ScoreVariableName = "Score";

    /// <summary>
    /// Event that puts every flea away again at the end of a game.
    /// </summary>
    private const string ResetEventName = "RESET FLEA GAMES";

    /// <summary>
    /// Event that ends a round of a game, as a game sends it when the last flea it allows has been dropped.
    /// </summary>
    private const string GameEndEventName = "GAME END";

    /// <summary>
    /// The states of a game that take <see cref="GameEndEventName"/>: once it has begun, and while it is playing.
    /// </summary>
    private static readonly string[] EndableStateNames = ["Transitioning - Can End", "Playing"];

    /// <summary>
    /// Reflected field with the text a badge of the score board draws its number into. A badge added at runtime has
    /// none, so it is taken from the badge it was made from.
    /// </summary>
    private static readonly FieldInfo? BadgeScoreTextField =
        typeof(ScoreBoardUIBadgeBase).GetField("scoreText", InstanceFlags);

    /// <summary>
    /// The net client, for telling the partner which game the local player is in.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The data of the other players, which tells whether the partner is in the local player's room.
    /// </summary>
    private readonly Dictionary<ushort, ClientPlayerData> _playerData;

    /// <summary>
    /// The entity manager, which tells whether this game controls the entities of the scene.
    /// </summary>
    private readonly EntityManager _entityManager;

    /// <summary>
    /// Gets the IDs of the members of the two-player save that it was checked with, none when no save is paired.
    /// </summary>
    private readonly Func<IReadOnlyCollection<ushort>> _getMembers;

    /// <summary>
    /// Sends the partner an event that a state machine of the room was told here, with the dice this game had as it
    /// was told (see <see cref="CoopHits.SendObjectEvent"/>).
    /// </summary>
    private readonly Action<PlayMakerFSM, string, int[]> _sendObjectEvent;

    /// <summary>
    /// Which player the thrower throws at, rolled apart from the dice of the game itself.
    /// </summary>
    private readonly System.Random _throwerDice = new();

    /// <summary>
    /// Hook for what the directors of the games tell the room.
    /// </summary>
    private Hook? _sendEventByNameHook;

    /// <summary>
    /// Hook that notices a game of the festival starting over.
    /// </summary>
    private Hook? _eventRegisterSendEventHook;

    /// <summary>
    /// Hook that adds the partner's row when a score board appears.
    /// </summary>
    private Hook? _scoreBoardOnEnableHook;

    /// <summary>
    /// The state machines that run the games of the festival in the loaded rooms. They are looked up again after a
    /// room has loaded rather than whenever they are needed, since that looks through every state machine there is.
    /// </summary>
    private PlayMakerFSM[] _masters = [];

    /// <summary>
    /// The names of the objects of <see cref="_masters"/>, which is how the two games tell each other which game.
    /// </summary>
    private string[] _masterNames = [];

    /// <summary>
    /// The variables in which <see cref="_masters"/> count the score.
    /// </summary>
    private FsmInt?[] _masterScores = [];

    /// <summary>
    /// Whether a room has loaded since the games were last looked up.
    /// </summary>
    private bool _mastersStale = true;

    /// <summary>
    /// The game the partner was last told the local player is in, by the name of its object, or null for none.
    /// </summary>
    private string? _sentGame;

    /// <summary>
    /// The score the partner was last told the local player has in <see cref="_sentGame"/>.
    /// </summary>
    private int _sentScore;

    /// <summary>
    /// Whether the members were last told that this game runs the room.
    /// </summary>
    private bool _sentSceneHost;

    /// <summary>
    /// The members that <see cref="_sentGame"/>, <see cref="_sentScore"/> and <see cref="_sentSceneHost"/> were told
    /// to. A member who comes back has forgotten them, so they are told again.
    /// </summary>
    private readonly HashSet<ushort> _sentTo = [];

    /// <summary>
    /// The number the last message about the local player's game went under. They are only sent on a change, and a
    /// lost one is sent again later, which can bring it in after a newer one: the number lets the partner tell. It
    /// starts from the clock, so that it still counts up when this game is started again.
    /// </summary>
    private ulong _sentSequence = (ulong) DateTime.UtcNow.Ticks;

    /// <summary>
    /// What each member said of the game they are in, by their ID.
    /// </summary>
    private readonly Dictionary<ushort, MemberGame> _memberGames = new();

    /// <summary>
    /// Whether the scene host has been in the game that the local player is in during this round of it, which a round
    /// of a game played with the same fleas needs in order to have any.
    /// </summary>
    private bool _sceneHostPlayedAlong;

    /// <summary>
    /// Whether putting the fleas away was held back because a member was still playing, and so still has to happen
    /// once they are finished too.
    /// </summary>
    private bool _resetHeld;

    /// <summary>
    /// Which players have won every game of the festival, by their ID. Kept for whoever says it rather than only for
    /// the partner: the game that finishes the check first says it at once, often before the other game has finished
    /// its own half, and it is only said again on a change - so a word turned away for coming that early was never
    /// heard at all, and the festival of the other player waited for it for good.
    /// </summary>
    private readonly Dictionary<ushort, bool> _outroReadyOf = new();

    /// <summary>
    /// What the members were last told about the local player having won every game, so it is only sent on a change.
    /// </summary>
    private bool? _sentOutroReady;

    /// <summary>
    /// The members that <see cref="_sentOutroReady"/> was told to. A member who comes back has forgotten it, so they
    /// are told again: said only on a change, it never changed again once every game was won, and the festival of the
    /// partner who came back waited for it for good.
    /// </summary>
    private readonly HashSet<ushort> _sentOutroReadyTo = [];

    /// <summary>
    /// Hook that keeps a character of the festival from taking both players on until both have won every game.
    /// </summary>
    private Hook? _outroReadyHook;

    public FleaGameCoop(
        NetClient netClient,
        Dictionary<ushort, ClientPlayerData> playerData,
        EntityManager entityManager,
        Func<IReadOnlyCollection<ushort>> getMembers,
        Action<PlayMakerFSM, string, int[]> sendObjectEvent
    ) {
        _netClient = netClient;
        _playerData = playerData;
        _entityManager = entityManager;
        _getMembers = getMembers;
        _sendObjectEvent = sendObjectEvent;
    }

    /// <summary>
    /// Whether a player is a member that the save was checked with.
    /// </summary>
    private bool IsMember(ushort playerId) => _getMembers().Contains(playerId);

    /// <summary>
    /// What a member said of the game they are in, made the first time it is asked for.
    /// </summary>
    private MemberGame GetMemberGame(ushort playerId) {
        if (!_memberGames.TryGetValue(playerId, out var member)) {
            member = new MemberGame();
            _memberGames[playerId] = member;
        }

        return member;
    }

    /// <summary>
    /// The checked members in the local player's room, each with what they said of their game.
    /// </summary>
    private IEnumerable<(ClientPlayerData Player, MemberGame Game)> GetRoomMemberGames() {
        foreach (var id in _getMembers()) {
            if (_playerData.TryGetValue(id, out var player) && player.IsInLocalScene &&
                _memberGames.TryGetValue(id, out var game)) {
                yield return (player, game);
            }
        }
    }

    /// <summary>
    /// Registers the hooks of the flea games.
    /// </summary>
    public void RegisterHooks() {
        var sendMethod = typeof(EventRegister).GetMethod(
            "SendEvent",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            [typeof(string), typeof(GameObject)],
            null
        );
        if (sendMethod == null) {
            Logger.Error("EventRegister does not declare SendEvent; the flea game counts will not start over");
        } else {
            _eventRegisterSendEventHook = new Hook(sendMethod, OnEventRegisterSendEvent);
        }

        var sendByNameMethod = typeof(SendEventByName).GetMethod(nameof(SendEventByName.OnEnter), InstanceFlags);
        if (sendByNameMethod == null) {
            Logger.Error("SendEventByName does not declare OnEnter; each game will change the rooms of the festival");
        } else {
            _sendEventByNameHook = new Hook(sendByNameMethod, OnSendEventByNameEnter);
        }

        var boardMethod = typeof(ScoreBoardUI).GetMethod("OnEnable", InstanceFlags);
        if (boardMethod == null) {
            Logger.Error("ScoreBoardUI does not declare OnEnable; the partner's row was not added");
        } else {
            _scoreBoardOnEnableHook = new Hook(boardMethod, OnScoreBoardEnable);
        }

        var outroMethod = typeof(PlayerData).GetMethod("get_FleaGamesOutroReady", InstanceFlags);
        if (outroMethod == null) {
            Logger.Error("PlayerData does not declare FleaGamesOutroReady; the festival will end for one player");
        } else {
            _outroReadyHook = new Hook(outroMethod, OnFleaGamesOutroReady);
        }

        EventHooks.HeroControllerUpdate += OnHeroControllerUpdate;
        SceneManager.sceneLoaded += OnSceneLoaded;

        // Rooms loaded while unhooked were not noticed
        _mastersStale = true;
    }

    /// <summary>
    /// Deregisters the hooks of the flea games.
    /// </summary>
    public void DeregisterHooks() {
        _eventRegisterSendEventHook?.Dispose();
        _eventRegisterSendEventHook = null;

        _sendEventByNameHook?.Dispose();
        _sendEventByNameHook = null;

        // Putting the fleas away was held back for the partner, who is not watching any more
        if (_resetHeld && GetLocalGame() == null) {
            PutFleasAway();
        }

        _scoreBoardOnEnableHook?.Dispose();
        _scoreBoardOnEnableHook = null;

        _outroReadyHook?.Dispose();
        _outroReadyHook = null;

        EventHooks.HeroControllerUpdate -= OnHeroControllerUpdate;
        SceneManager.sceneLoaded -= OnSceneLoaded;

        _outroReadyOf.Clear();
        _sentOutroReady = null;
        _sentOutroReadyTo.Clear();

        _sentGame = null;
        _sentScore = 0;
        _sentSceneHost = false;
        _sentTo.Clear();
        _memberGames.Clear();
        _sceneHostPlayedAlong = false;

        ForgetScores();
    }

    /// <summary>
    /// Takes which game a member is in, their score in it and whether their game runs the room. The score is their
    /// total rather than a single point, and a message that was overtaken on the way is left out, so one that arrives
    /// late or twice can only ever be ignored.
    /// </summary>
    /// <param name="player">The player who sent it.</param>
    /// <param name="update">The update.</param>
    public void OnPartnerScore(ClientPlayerData player, CoopSaveUpdate update) {
        if (!IsMember(player.Id)) {
            return;
        }

        var member = GetMemberGame(player.Id);
        if (member.Sequence is { } newest && update.Sequence <= newest) {
            return;
        }

        member.Sequence = update.Sequence;
        member.Game = (update.Part & InGameFlag) != 0 ? update.ObjectPath : null;
        member.IsSceneHost = (update.Part & SceneHostFlag) != 0;
        member.Score = System.Math.Max(member.Score, (int) update.Key);
    }

    /// <summary>
    /// The flag of a message about a member's game that says they are in one.
    /// </summary>
    private const ushort InGameFlag = 1;

    /// <summary>
    /// The flag of a message about a member's game that says their game runs the room, which is the game that sends
    /// the fleas out.
    /// </summary>
    private const ushort SceneHostFlag = 2;

    /// <summary>
    /// Puts the fleas away after all, once no player is in a game any more. Held back while a member was still
    /// playing, since they are watching these same fleas, and nothing else would ever do it afterwards.
    /// </summary>
    private void ReleaseHeldReset() {
        if (!_resetHeld || AnyMemberPlaysSharedGame() || GetLocalGame() != null) {
            return;
        }

        PutFleasAway();
    }

    /// <summary>
    /// Puts away the fleas of every game of the festival, as a game does at the end of its round.
    /// </summary>
    private void PutFleasAway() {
        _resetHeld = false;

        try {
            EventRegister.SendEvent(ResetEventName, null);
        } catch (Exception e) {
            Logger.Warn($"Could not put the fleas away: {e.GetType()}, {e.Message}");
        }
    }

    /// <summary>
    /// Ends the local player's round of a game played with the same fleas once the scene host has played along and is
    /// no longer in that game. The scene host's game sends those fleas out, so nothing would come any more: the round
    /// ends the way the game ends it when the last flea it allows has been dropped, and what was scored is saved as it
    /// is then. Checked every frame rather than when the scene host's round ends, because a round that is only just
    /// starting does not take the end yet. Which member runs the room is what their own game says of it, which comes a
    /// moment after the room changed hands: while no member in the room says so, nothing ends. With more than two
    /// players, the one who ran the room may walk out mid-round and leave it to another who still plays.
    /// </summary>
    private void EndRoundWithoutSceneHost() {
        if (GetLocalGame() is not { } index || PersonalPlaces.Contains(_masters[index].gameObject)) {
            _sceneHostPlayedAlong = false;
            return;
        }

        var game = _masterNames[index];
        var sceneHostKnown = false;
        foreach (var (_, member) in GetRoomMemberGames()) {
            if (!member.IsSceneHost) {
                continue;
            }

            if (member.Game == game) {
                _sceneHostPlayedAlong = true;
                return;
            }

            sceneHostKnown = true;
        }

        if (!sceneHostKnown || !_sceneHostPlayedAlong || _masters[index].Fsm?.ActiveState is not { } state ||
            Array.IndexOf(EndableStateNames, state.Name) < 0) {
            return;
        }

        _sceneHostPlayedAlong = false;
        Logger.Info($"The scene host is no longer in '{game}', so the local player's round of it ends too");

        try {
            EventRegister.SendEvent(GameEndEventName, null);
        } catch (Exception e) {
            Logger.Warn($"Could not end the round of '{game}': {e.GetType()}, {e.Message}");
        }
    }

    /// <summary>
    /// Hook for <see cref="EventRegister.SendEvent(string, UnityEngine.GameObject)"/>, which notices a game of the
    /// festival starting over so that the counts of both players do not carry into it.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="eventName">The event being sent.</param>
    /// <param name="source">The object that sent it.</param>
    private void OnEventRegisterSendEvent(Action<string, GameObject> orig, string eventName, GameObject source) {
        // Putting the fleas away is what the game that runs them does when its own player is finished. A partner who
        // plays with those same fleas would have every flea reparented, stopped and hidden in the middle of their
        // round, so it waits until they are finished too. The fleas of a game that each player has to themselves are
        // put away along with the rest then.
        if (eventName == ResetEventName && _getMembers().Count > 0 && _entityManager.IsSceneRoleDetermined &&
            _entityManager.IsSceneHost && AnyMemberPlaysSharedGame()) {
            _resetHeld = true;
            return;
        }

        orig(eventName, source);

        if (eventName != null && Array.IndexOf(ResetEventNames, eventName) >= 0) {
            ForgetScores();
        }
    }

    /// <summary>
    /// Hook for <see cref="SendEventByName.OnEnter"/>, for what the director of a game played with the same fleas has
    /// the room do.
    ///
    /// Each game runs the director of such a game for itself, and it rolls on its own clock when the room around the
    /// fleas changes: which of two platforms sinks and how high the other comes back up, when someone joins in. The
    /// fleas are entities and follow the scene host's game whatever the other director says, but the room did what each
    /// director said, so the two players stood on platforms that were not the same. What such a director tells an
    /// object of the room is now the scene host's alone to say: its game tells it and sends the partner the event with
    /// the dice it had, and the other game's director tells the room nothing.
    ///
    /// What is sent in to throw something at the player is an entity, which follows the scene host's game already. It
    /// is given a player to throw at (see <see cref="PickThrowTarget"/>), rather than always the scene host's own.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The action.</param>
    private void OnSendEventByNameEnter(Action<SendEventByName> orig, SendEventByName self) {
        if (SharedGameOf(self.Fsm) is not { } game) {
            orig(self);
            return;
        }

        var target = self.eventTarget is { target: FsmEventTarget.EventTarget.GameObject } eventTarget
            ? self.Fsm.GetOwnerDefaultTarget(eventTarget.gameObject)
            : null;
        if (target != null && target.name == ThrowerName && _entityManager.IsSceneHost &&
            HeroController.instance != null) {
            GamePatcher.SetTargetOf(target, PickThrowTarget(game));
        }

        if (GetRoomFsm(self, target) is not { } told) {
            orig(self);
            return;
        }

        if (!_entityManager.IsSceneHost) {
            self.Finish();
            return;
        }

        var eventName = self.sendEvent.Value;
        var dice = SharedDice.Record(() => orig(self));
        _sendObjectEvent(told, eventName, dice);
    }

    /// <summary>
    /// Gets the game that a state machine directs, by its index in <see cref="_masters"/>, if it is the director of a
    /// game played with the same fleas in a two-player save whose games have settled which of them is the scene host.
    /// </summary>
    /// <param name="fsm">The state machine.</param>
    /// <returns>The index of the game, or null if the state machine is no such director.</returns>
    private int? SharedGameOf(HutongGames.PlayMaker.Fsm? fsm) {
        if (fsm is not { Name: DirectorFsmName, GameObject: { } root } || _getMembers().Count == 0 ||
            !_entityManager.IsSceneRoleDetermined || PersonalPlaces.Contains(root)) {
            return null;
        }

        RefreshMasters();
        var game = Array.FindIndex(_masters, master => master != null && master.gameObject == root);
        return game >= 0 ? game : null;
    }

    /// <summary>
    /// Picks the player that the thrower of a game throws at next: one of those who play this round at random, the
    /// local player among them. A member whose round has ended stands about the room out of the game, and a share of
    /// what was thrown went at them.
    /// </summary>
    /// <param name="game">The index of the game in <see cref="_masters"/>.</param>
    private GameObject PickThrowTarget(int game) {
        var playing = new List<GameObject>();
        foreach (var (player, member) in GetRoomMemberGames()) {
            if (member.Game == _masterNames[game] && player.PlayerObject is { } avatar && avatar != null &&
                avatar.activeInHierarchy) {
                playing.Add(avatar);
            }
        }

        if (playing.Count > 0) {
            var pick = _throwerDice.Next(playing.Count + 1);
            if (pick < playing.Count) {
                return playing[pick];
            }
        }

        return HeroController.instance.gameObject;
    }

    /// <summary>
    /// Gets the state machine of the room that the event of a director is for, or null if it is for anything else - an
    /// entity, which follows the scene host's game by itself, or a player - or is not told at once, or not to one
    /// state machine alone.
    /// </summary>
    /// <param name="action">The action that tells the event.</param>
    /// <param name="target">The object it tells it to.</param>
    private static PlayMakerFSM? GetRoomFsm(SendEventByName action, GameObject? target) {
        if (target == null || action.everyFrame || action.delay.Value > 0f) {
            return null;
        }

        var fsms = target.GetComponents<PlayMakerFSM>();
        return fsms.Length == 1 && CoopHits.IsRoomObject(fsms[0]) ? fsms[0] : null;
    }

    /// <summary>
    /// Begins the counts shown for the members again, and forgets putting the fleas away if that was held back, since
    /// it has just happened.
    /// </summary>
    private void ForgetScores() {
        foreach (var member in _memberGames.Values) {
            member.Score = 0;
        }

        _resetHeld = false;
    }

    /// <summary>
    /// Tells the members which game the local player is in, their score in it and whether this game runs the room,
    /// whenever any of that changes, including when they are no longer in a game: that is how the members hear that the
    /// fleas may be put away. A member who was not told yet, like one who came back, is told on their own.
    /// </summary>
    private void SendLocalGame() {
        if (!_netClient.IsConnected) {
            return;
        }

        var index = GetLocalGame();
        var game = index is { } playing ? _masterNames[playing] : null;
        var score = index is { } scored && _masterScores[scored] is { } variable ? variable.Value : 0;
        var sceneHost = _entityManager.IsSceneRoleDetermined && _entityManager.IsSceneHost;
        if (game != _sentGame || score != _sentScore || sceneHost != _sentSceneHost) {
            _sentGame = game;
            _sentScore = score;
            _sentSceneHost = sceneHost;
            _sentTo.Clear();
        }

        var members = _getMembers();
        ForgetGone(_sentTo, members);
        var part = (ushort) ((game != null ? InGameFlag : 0) | (sceneHost ? SceneHostFlag : 0));
        foreach (var memberId in members) {
            if (_sentTo.Add(memberId)) {
                Send(memberId, CoopSaveUpdateKind.FleaGameScore, (ulong) System.Math.Max(score, 0), part, game,
                    ++_sentSequence);
            }
        }
    }

    /// <summary>
    /// Forgets the members that were told something who are no longer members, so that one who comes back is told it
    /// again.
    /// </summary>
    private static void ForgetGone(HashSet<ushort> told, IReadOnlyCollection<ushort> members) {
        List<ushort>? gone = null;
        foreach (var id in told) {
            if (!members.Contains(id)) {
                (gone ??= []).Add(id);
            }
        }

        if (gone != null) {
            foreach (var id in gone) {
                told.Remove(id);
            }
        }
    }

    /// <summary>
    /// Sends something about the festival to a member of the two-player save.
    /// </summary>
    /// <param name="memberId">The ID of the member.</param>
    /// <param name="kind">What is being told.</param>
    /// <param name="key">The running count it carries, if any.</param>
    /// <param name="part">The flags it carries, if any.</param>
    /// <param name="game">The game it is about, by the name of its object, if any.</param>
    /// <param name="sequence">The number it goes under, if any.</param>
    private void Send(
        ushort memberId,
        CoopSaveUpdateKind kind,
        ulong key,
        ushort part,
        string? game = null,
        ulong sequence = 0
    ) {
        if (!_netClient.IsConnected) {
            return;
        }

        _netClient.UpdateManager.SetCoopSaveUpdate(
            new CoopSaveUpdate {
                TargetId = memberId,
                Kind = kind,
                Key = key,
                Part = part,
                ObjectPath = game ?? "",
                Sequence = sequence
            }
        );
    }

    /// <summary>
    /// Gets which game of the festival the local player is in, as an index into <see cref="_masters"/>, or null when
    /// they are in none. A game whose state machine has not started, as in a room where the festival is not on, is
    /// one that nobody plays.
    /// </summary>
    private int? GetLocalGame() {
        RefreshMasters();

        for (var index = 0; index < _masters.Length; index++) {
            var master = _masters[index];
            if (master != null && master.Fsm?.ActiveState is { } state &&
                Array.IndexOf(IdleStateNames, state.Name) < 0) {
                return index;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a member in this room is in a game that the players play with the same fleas, which this game sends out
    /// while it is the scene host.
    /// </summary>
    private bool AnyMemberPlaysSharedGame() {
        foreach (var (_, member) in GetRoomMemberGames()) {
            if (member.Game == null) {
                continue;
            }

            RefreshMasters();
            var index = Array.IndexOf(_masterNames, member.Game);
            if (index >= 0 && _masters[index] != null && !PersonalPlaces.Contains(_masters[index].gameObject)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Looks the games of the festival up again if a room has loaded since they were last looked up.
    /// </summary>
    private void RefreshMasters() {
        if (!_mastersStale) {
            return;
        }

        _mastersStale = false;
        _masters = FindFsms(MasterFsmName);
        _masterNames = _masters.Select(master => master.gameObject.name).ToArray();
        _masterScores = _masters.Select(master => master.FsmVariables.FindFsmInt(ScoreVariableName)).ToArray();
    }

    /// <summary>
    /// Callback for a room having loaded, after which the games of the festival are looked up again.
    /// </summary>
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
        _mastersStale = true;
    }

    /// <summary>
    /// Finds the state machines of a given name in the scene, including the ones on objects that are switched off.
    /// </summary>
    /// <param name="name">The name of the state machine.</param>
    /// <returns>The state machines found.</returns>
    private static PlayMakerFSM[] FindFsms(string name) {
        return Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                     .Where(fsm => fsm.Fsm != null && fsm.Fsm.Name == name)
                     .ToArray();
    }

    /// <summary>
    /// Takes whether a player has won every game of the festival, which counts once they are a member.
    /// </summary>
    /// <param name="player">The player who sent it.</param>
    /// <param name="update">The update.</param>
    public void OnPartnerOutroReady(ClientPlayerData player, CoopSaveUpdate update) {
        _outroReadyOf[player.Id] = update.Part != 0;
    }

    /// <summary>
    /// Hook for whether the local player has won every game of the festival. What follows the festival is one
    /// thing that happens to all the players at once, in a place they are all standing in, and it can only happen
    /// once, so it waits until every one of them has earned it. Each still wins their games on their own.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The player data.</param>
    /// <returns>Whether the festival may move on.</returns>
    private bool OnFleaGamesOutroReady(Func<PlayerData, bool> orig, PlayerData self) {
        var ready = orig(self);
        if (!ready) {
            return false;
        }

        foreach (var memberId in _getMembers()) {
            if (!_outroReadyOf.TryGetValue(memberId, out var memberReady) || !memberReady) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Tells the members which game the local player is in, and when they have won every game of the festival or no
    /// longer have. The latter is read from the three games one by one rather than from the answer above, which this
    /// game's own hook has already changed. Also what waits on the members' games: putting the fleas away for the
    /// scene host, and ending a round without the scene host for the other players.
    /// </summary>
    /// <param name="heroController">The hero controller that updated.</param>
    private void OnHeroControllerUpdate(HeroController heroController) {
        var members = _getMembers();
        if (members.Count == 0) {
            return;
        }

        SendLocalGame();

        if (_entityManager.IsSceneRoleDetermined) {
            if (_entityManager.IsSceneHost) {
                _sceneHostPlayedAlong = false;
                ReleaseHeldReset();
            } else {
                EndRoundWithoutSceneHost();
            }
        }

        var playerData = PlayerData.instance;
        if (playerData == null) {
            return;
        }

        var ready = playerData.FleaGamesIsJugglingChampion &&
                    playerData.FleaGamesIsBouncingChampion &&
                    playerData.FleaGamesIsDodgingChampion;
        if (_sentOutroReady != ready) {
            _sentOutroReady = ready;
            _sentOutroReadyTo.Clear();
        }

        ForgetGone(_sentOutroReadyTo, members);
        foreach (var memberId in members) {
            if (_sentOutroReadyTo.Add(memberId)) {
                Send(memberId, CoopSaveUpdateKind.FleaGamesOutroReady, 0, (ushort) (ready ? 1 : 0));
            }
        }
    }

    /// <summary>
    /// Hook for the score board appearing, which gives each member a row of their own on it. The board sorts the
    /// rows it finds by their score, so each row places itself among the others rather than being put somewhere.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The score board.</param>
    private void OnScoreBoardEnable(Action<ScoreBoardUI> orig, ScoreBoardUI self) {
        orig(self);

        var added = false;
        foreach (var memberId in _getMembers().OrderBy(id => id)) {
            try {
                added |= AddMemberRow(self, memberId);
            } catch (Exception e) {
                Logger.Warn($"Could not add a member's row to the score board: {e.GetType()}, {e.Message}");
            }
        }

        if (added) {
            self.Refresh();
        }
    }

    /// <summary>
    /// Gives a member a row on a score board, made from the row that shows the local player's own score so that it
    /// matches the others. Does nothing if they already have one. The row shows only while they are a member.
    /// </summary>
    /// <param name="board">The score board.</param>
    /// <param name="memberId">The ID of the member.</param>
    /// <returns>Whether a row was added.</returns>
    private bool AddMemberRow(ScoreBoardUI board, ushort memberId) {
        if (board.GetComponentsInChildren<FleaPartnerScoreBadge>(true).Any(badge => badge.MemberId == memberId)) {
            return false;
        }

        var hero = board.GetComponentInChildren<ScoreBoardUIBadgeHero>(true);
        if (hero == null) {
            return false;
        }

        var row = Object.Instantiate(hero.gameObject, hero.transform.parent);
        row.name = $"SSMP Partner Score {memberId}";

        // The copy is still the local player's row until its own badge is taken off it
        foreach (var copied in row.GetComponents<ScoreBoardUIBadgeBase>()) {
            Object.DestroyImmediate(copied);
        }

        var badge = row.AddComponent<FleaPartnerScoreBadge>();
        badge.MemberId = memberId;

        // A badge draws its number into a text of its own object, which a badge added at runtime has no reference
        // to, so it is taken from the row this one was copied from
        BadgeScoreTextField?.SetValue(badge, BadgeScoreTextField.GetValue(hero));
        badge.GetScore = () => _memberGames.TryGetValue(memberId, out var member) ? member.Score : 0;
        badge.IsShown = () => IsMember(memberId);

        row.SetActive(true);
        return true;
    }
}

/// <summary>
/// A row of a score board of the festival that shows how many fleas a member has hit. The board asks every row it finds
/// for its score and puts them in order, so this one ranks itself against the local player and the characters already
/// on the board without any of them being touched.
/// </summary>
internal class FleaPartnerScoreBadge : ScoreBoardUIBadgeBase {
    /// <summary>
    /// The ID of the member whose row it is.
    /// </summary>
    public ushort MemberId { get; set; }

    /// <summary>
    /// Gets how many fleas the member has hit.
    /// </summary>
    public Func<int>? GetScore { get; set; }

    /// <summary>
    /// Gets whether the row is shown, which is while its player is a member.
    /// </summary>
    public Func<bool>? IsShown { get; set; }

    /// <inheritdoc />
    public override int Score => GetScore?.Invoke() ?? 0;

    /// <inheritdoc />
    public override bool IsVisible => GetScore != null && (IsShown?.Invoke() ?? true);
}

/// <summary>
/// What a member of the two-player save said of the game of the festival they are in.
/// </summary>
internal sealed class MemberGame {
    /// <summary>
    /// The number of the newest message about it so far, or null before the first.
    /// </summary>
    public ulong? Sequence { get; set; }

    /// <summary>
    /// The game they are in, by the name of its object, or null when they are in none.
    /// </summary>
    public string? Game { get; set; }

    /// <summary>
    /// Whether their game runs the room.
    /// </summary>
    public bool IsSceneHost { get; set; }

    /// <summary>
    /// How many fleas they have hit in the game being played.
    /// </summary>
    public int Score { get; set; }
}
