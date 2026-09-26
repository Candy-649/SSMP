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
/// out its own fleas for its own player, the partner does not see them, and the score, the saving and how hard it gets
/// are all as when playing alone. Going for the same fleas there left one of the two players with nothing to land on.
///
/// The other two are played with the same fleas. The fleas are entities, so the scene host runs them and the other
/// game only shows where they are. A hit of the other game's player on a flea goes to the scene host like a hit on any
/// other copy (see CoopHits), so the flea answers it there, and what the fleas then say about the game - a point, a
/// flea tinked or dropped - is replayed in the other game's copy of that game (see EntityFsmActions). So the two
/// players score together: a point is a point for both, whoever hit the flea or got past it, and a dropped flea counts
/// against both. Each game still counts, shows and saves the score as when playing alone.
///
/// Since the scene host's game sends those fleas out, a round of such a game goes on only while the scene host plays
/// it: once they have played along and their round is over, the other player's round of the same game ends too.
/// Putting the fleas away at the end of the scene host's round waits until the other player's round is over as well.
///
/// Both games tell each other which game their player is in and the score of the round, so that each player can be
/// shown where the other stands. The score is sent as a total rather than as single points, so a lost or a repeated
/// message cannot make it drift.
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
    /// Gets the ID of the partner of the two-player save, or null when no save is paired.
    /// </summary>
    private readonly Func<ushort?> _getPartnerId;

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
    /// The partner that <see cref="_sentGame"/> and <see cref="_sentScore"/> were told to. A partner who comes back
    /// has forgotten them, so they are told again.
    /// </summary>
    private ushort? _sentTo;

    /// <summary>
    /// The number the last message about the local player's game went under. They are only sent on a change, and a
    /// lost one is sent again later, which can bring it in after a newer one: the number lets the partner tell. It
    /// starts from the clock, so that it still counts up when this game is started again.
    /// </summary>
    private ulong _sentSequence = (ulong) DateTime.UtcNow.Ticks;

    /// <summary>
    /// The number of the newest message about the partner's game so far, and whose it was.
    /// </summary>
    private (ushort Id, ulong Sequence)? _partnerSequence;

    /// <summary>
    /// Whether the scene host has been in the game that the local player is in during this round of it, which a round
    /// of a game played with the same fleas needs in order to have any.
    /// </summary>
    private bool _sceneHostPlayedAlong;

    /// <summary>
    /// How many fleas the partner has hit in the game being played.
    /// </summary>
    public int PartnerScore { get; private set; }

    /// <summary>
    /// The game the partner is in, by the name of its object, or null when they are in none.
    /// </summary>
    private string? _partnerGame;

    /// <summary>
    /// Whether putting the fleas away was held back because the partner was still playing, and so still has to
    /// happen once they are finished too.
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
    /// What the partner was last told about the local player having won every game, so it is only sent on a change.
    /// </summary>
    private bool? _sentOutroReady;

    /// <summary>
    /// The partner that <see cref="_sentOutroReady"/> was told to. A partner who comes back has forgotten it, so they
    /// are told again: said only on a change, it never changed again once every game was won, and the festival of the
    /// partner who came back waited for it for good.
    /// </summary>
    private ushort? _sentOutroReadyTo;

    /// <summary>
    /// Hook that keeps a character of the festival from taking both players on until both have won every game.
    /// </summary>
    private Hook? _outroReadyHook;

    public FleaGameCoop(
        NetClient netClient,
        Dictionary<ushort, ClientPlayerData> playerData,
        EntityManager entityManager,
        Func<ushort?> getPartnerId,
        Action<PlayMakerFSM, string, int[]> sendObjectEvent
    ) {
        _netClient = netClient;
        _playerData = playerData;
        _entityManager = entityManager;
        _getPartnerId = getPartnerId;
        _sendObjectEvent = sendObjectEvent;
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
        _sentOutroReadyTo = null;

        _sentGame = null;
        _sentScore = 0;
        _sentTo = null;
        _partnerGame = null;
        _partnerSequence = null;
        _sceneHostPlayedAlong = false;

        ForgetScores();
    }

    /// <summary>
    /// Takes which game the partner is in and their score in it. The score is their total rather than a single
    /// point, and a message that was overtaken on the way is left out, so one that arrives late or twice can only ever
    /// be ignored.
    /// </summary>
    /// <param name="player">The player who sent it.</param>
    /// <param name="update">The update.</param>
    public void OnPartnerScore(ClientPlayerData player, CoopSaveUpdate update) {
        if (_getPartnerId() != player.Id ||
            _partnerSequence is { } newest && newest.Id == player.Id && update.Sequence <= newest.Sequence) {
            return;
        }

        _partnerSequence = (player.Id, update.Sequence);
        _partnerGame = update.Part != 0 ? update.ObjectPath : null;
        PartnerScore = System.Math.Max(PartnerScore, (int) update.Key);
    }

    /// <summary>
    /// Puts the fleas away after all, once neither player is in a game any more. Held back while the partner was
    /// still playing, since they are watching these same fleas, and nothing else would ever do it afterwards.
    /// </summary>
    private void ReleaseHeldReset() {
        if (!_resetHeld || PartnerPlaysSharedGame() || GetLocalGame() != null) {
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
    /// starting does not take the end yet.
    /// </summary>
    private void EndRoundWithoutSceneHost() {
        if (GetLocalGame() is not { } index || PersonalPlaces.Contains(_masters[index].gameObject)) {
            _sceneHostPlayedAlong = false;
            return;
        }

        var game = _masterNames[index];
        if (_partnerGame == game) {
            _sceneHostPlayedAlong = true;
            return;
        }

        if (!_sceneHostPlayedAlong || _masters[index].Fsm?.ActiveState is not { } state ||
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
        if (eventName == ResetEventName && _getPartnerId() != null && _entityManager.IsSceneRoleDetermined &&
            _entityManager.IsSceneHost && PartnerPlaysSharedGame()) {
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
        if (fsm is not { Name: DirectorFsmName, GameObject: { } root } || _getPartnerId() == null ||
            !_entityManager.IsSceneRoleDetermined || PersonalPlaces.Contains(root)) {
            return null;
        }

        RefreshMasters();
        var game = Array.FindIndex(_masters, master => master != null && master.gameObject == root);
        return game >= 0 ? game : null;
    }

    /// <summary>
    /// Picks the player that the thrower of a game throws at next: one of the two at random while the partner plays
    /// this round too, and the local player otherwise. A partner whose round has ended stands about the room out of
    /// the game, and half of what was thrown went at them.
    /// </summary>
    /// <param name="game">The index of the game in <see cref="_masters"/>.</param>
    private GameObject PickThrowTarget(int game) {
        if (_partnerGame == _masterNames[game] && _getPartnerId() is { } partnerId &&
            _playerData.TryGetValue(partnerId, out var partner) && partner.IsInLocalScene &&
            partner.PlayerObject is var avatar && avatar != null && avatar.activeInHierarchy &&
            _throwerDice.Next(2) == 0) {
            return avatar;
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
    /// Begins the count shown for the partner again, and forgets putting the fleas away if that was held back, since
    /// it has just happened.
    /// </summary>
    private void ForgetScores() {
        PartnerScore = 0;
        _resetHeld = false;
    }

    /// <summary>
    /// Tells the partner which game the local player is in and their score in it, whenever either changes, including
    /// when they are no longer in a game: that is how the partner hears that the fleas may be put away.
    /// </summary>
    /// <param name="partnerId">The ID of the partner.</param>
    private void SendLocalGame(ushort partnerId) {
        if (!_netClient.IsConnected) {
            return;
        }

        var index = GetLocalGame();
        var game = index is { } playing ? _masterNames[playing] : null;
        var score = index is { } scored && _masterScores[scored] is { } variable ? variable.Value : 0;
        if (game == _sentGame && score == _sentScore && partnerId == _sentTo) {
            return;
        }

        _sentGame = game;
        _sentScore = score;
        _sentTo = partnerId;
        Send(
            CoopSaveUpdateKind.FleaGameScore, (ulong) System.Math.Max(score, 0), (ushort) (game != null ? 1 : 0),
            game, ++_sentSequence
        );
    }

    /// <summary>
    /// Sends something about the festival to the partner of the two-player save.
    /// </summary>
    /// <param name="kind">What is being told.</param>
    /// <param name="key">The running count it carries, if any.</param>
    /// <param name="part">The flag it carries, if any.</param>
    /// <param name="game">The game it is about, by the name of its object, if any.</param>
    /// <param name="sequence">The number it goes under, if any.</param>
    private void Send(CoopSaveUpdateKind kind, ulong key, ushort part, string? game = null, ulong sequence = 0) {
        if (_getPartnerId() is not { } partnerId || !_netClient.IsConnected) {
            return;
        }

        _netClient.UpdateManager.SetCoopSaveUpdate(
            new CoopSaveUpdate {
                TargetId = partnerId,
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
    /// Whether the partner is in a game that both players play with the same fleas, which this game sends out while
    /// it is the scene host, and in this room to play it.
    /// </summary>
    private bool PartnerPlaysSharedGame() {
        if (_partnerGame == null || _getPartnerId() is not { } partnerId ||
            !_playerData.TryGetValue(partnerId, out var partner) || !partner.IsInLocalScene) {
            return false;
        }

        RefreshMasters();

        var index = Array.IndexOf(_masterNames, _partnerGame);
        return index >= 0 && _masters[index] != null && !PersonalPlaces.Contains(_masters[index].gameObject);
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
    /// Takes whether a player has won every game of the festival, which counts once they are the partner.
    /// </summary>
    /// <param name="player">The player who sent it.</param>
    /// <param name="update">The update.</param>
    public void OnPartnerOutroReady(ClientPlayerData player, CoopSaveUpdate update) {
        _outroReadyOf[player.Id] = update.Part != 0;
    }

    /// <summary>
    /// Hook for whether the local player has won every game of the festival. What follows the festival is one
    /// thing that happens to both players at once, in a place they are both standing in, and it can only happen
    /// once, so it waits until both of them have earned it. Each still wins their games on their own.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The player data.</param>
    /// <returns>Whether the festival may move on.</returns>
    private bool OnFleaGamesOutroReady(Func<PlayerData, bool> orig, PlayerData self) {
        var ready = orig(self);
        if (!ready || _getPartnerId() is not { } partnerId) {
            return ready;
        }

        return _outroReadyOf.TryGetValue(partnerId, out var partnerReady) && partnerReady;
    }

    /// <summary>
    /// Tells the partner which game the local player is in, and when they have won every game of the festival or no
    /// longer have. The latter is read from the three games one by one rather than from the answer above, which this
    /// game's own hook has already changed. Also what waits on the partner's game: putting the fleas away for the
    /// scene host, and ending a round without the scene host for the other player.
    /// </summary>
    /// <param name="heroController">The hero controller that updated.</param>
    private void OnHeroControllerUpdate(HeroController heroController) {
        if (_getPartnerId() is not { } partnerId) {
            return;
        }

        SendLocalGame(partnerId);

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
        if (_sentOutroReady == ready && _sentOutroReadyTo == partnerId) {
            return;
        }

        _sentOutroReady = ready;
        _sentOutroReadyTo = partnerId;
        Send(CoopSaveUpdateKind.FleaGamesOutroReady, 0, (ushort) (ready ? 1 : 0));
    }

    /// <summary>
    /// Hook for the score board appearing, which gives the partner a row of their own on it. The board sorts the
    /// rows it finds by their score, so the row places itself among the others rather than being put somewhere.
    /// </summary>
    /// <param name="orig">The original method.</param>
    /// <param name="self">The score board.</param>
    private void OnScoreBoardEnable(Action<ScoreBoardUI> orig, ScoreBoardUI self) {
        orig(self);

        if (_getPartnerId() == null) {
            return;
        }

        try {
            if (AddPartnerRow(self)) {
                self.Refresh();
            }
        } catch (Exception e) {
            Logger.Warn($"Could not add the partner's row to the score board: {e.GetType()}, {e.Message}");
        }
    }

    /// <summary>
    /// Gives the partner a row on a score board, made from the row that shows the local player's own score so that
    /// it matches the others. Does nothing if it already has one.
    /// </summary>
    /// <param name="board">The score board.</param>
    /// <returns>Whether a row was added.</returns>
    private bool AddPartnerRow(ScoreBoardUI board) {
        if (board.GetComponentInChildren<FleaPartnerScoreBadge>(true) != null) {
            return false;
        }

        var hero = board.GetComponentInChildren<ScoreBoardUIBadgeHero>(true);
        if (hero == null) {
            return false;
        }

        var row = Object.Instantiate(hero.gameObject, hero.transform.parent);
        row.name = "SSMP Partner Score";

        // The copy is still the local player's row until its own badge is taken off it
        foreach (var copied in row.GetComponents<ScoreBoardUIBadgeBase>()) {
            Object.DestroyImmediate(copied);
        }

        var badge = row.AddComponent<FleaPartnerScoreBadge>();

        // A badge draws its number into a text of its own object, which a badge added at runtime has no reference
        // to, so it is taken from the row this one was copied from
        BadgeScoreTextField?.SetValue(badge, BadgeScoreTextField.GetValue(hero));
        badge.GetScore = () => PartnerScore;

        row.SetActive(true);
        return true;
    }
}

/// <summary>
/// A row of a score board of the festival that shows how many fleas the partner has hit. The board asks every row
/// it finds for its score and puts them in order, so this one ranks itself against the local player and the
/// characters already on the board without any of them being touched.
/// </summary>
internal class FleaPartnerScoreBadge : ScoreBoardUIBadgeBase {
    /// <summary>
    /// Gets how many fleas the partner has hit.
    /// </summary>
    public Func<int>? GetScore { get; set; }

    /// <inheritdoc />
    public override int Score => GetScore?.Invoke() ?? 0;

    /// <inheritdoc />
    public override bool IsVisible => GetScore != null;
}
