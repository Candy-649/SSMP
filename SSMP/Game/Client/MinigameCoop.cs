using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Animation;
using SSMP.Animation.Effects;
using SSMP.Hooks;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Ui;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client;

/// <summary>
/// Co-op rules for the games that each player plays on their own: the three games of the festival, where the fleas
/// that fly about are hit or dodged for points, and the contest of throwing pins at small targets.
///
/// Each player plays them in their own game (see <see cref="PersonalPlaces"/>): every game runs its own round for its
/// own player, and the score, the saving and how hard it gets are all as when playing alone. Two of the festival's
/// games were played with the same fleas, which the scene host's game ran, until the user had every game played this
/// way (2026-10-22: "全都改成一起开始但是各玩各的，并且玩的时候看不到对方，先结束的人可以选择观战另一个人玩的").
/// The players start a round together, as each of them says yes to the character who runs the game
/// (CoopSave.FleaGames).
///
/// While the local player plays a round they see nobody else, nor anyone else's attacks, and a player who plays a round
/// is seen by nobody but those who watch them (see <see cref="UpdateHiding"/>): what they play with is in their own
/// game only, so anyone else would see them jump at nothing. A player whose round is over while other players in the
/// room still play one is asked once whether to watch, and is then shown the round of one of them as its player sees
/// it (see <see cref="MinigameMirror"/>), going on to another with left and right or when that round ends, until
/// nobody plays any more or they stop watching with the jump button.
///
/// The games tell each other which game their player plays a round of, and the score of the round, so that each player
/// knows whom to hide and whom they can watch, and the festival's score boards show where the others stand. The score
/// is sent as a total rather than as single points, so a lost or a repeated message cannot make it drift.
/// </summary>
internal partial class MinigameCoop {
    /// <summary>
    /// Binding flags for the private members of the game.
    /// </summary>
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// A kind of game that each player plays on their own: the state machine that runs a round of it, the states in
    /// which nobody plays it, the states in which a round that is decided ends, and the variable in which it counts the
    /// score of a round, if it has one.
    /// </summary>
    private sealed record GameKind(
        string FsmName,
        string[] IdleStateNames,
        string[] EndingStateNames,
        string? ScoreVariableName
    );

    /// <summary>
    /// The kinds of games that each player plays on their own. Whether the local player plays one is read from the
    /// state machine itself rather than from the events it sends, because one event name of a game is not told apart
    /// from another's in what can be read of them. A round counts as played from its start until it is decided: once it
    /// ends, its player is seen again, is no longer to be watched, and can watch another's.
    /// </summary>
    private static readonly GameKind[] GameKinds = [
        // The three games of the festival, each run by a state machine of this name on its object, which counts the
        // score of the round and saves the best one. A round that is lost takes the hero, fades out, puts the hero by
        // the character who runs the game, saves the best score and gives the hero back
        new(
            "flea_game_master_control",
            ["Init", "Inactive"],
            ["Take Control", "Manual End", "End Fadeout", "Transition From Game", "Save Max", "Pass Control"],
            "Score"
        ),

        // The contest of throwing pins, whose state machine waits in "Inert" between rounds. A round is decided when
        // its last target is done with, or the player missed too often or stepped out
        new(
            "Pin Challenge",
            ["Init", "Inert"],
            ["Level 1 End 2", "Level 2 End", "Fail Wait", "End Pin Challenge"],
            null
        )
    ];

    /// <summary>
    /// Reflected field with the text a badge of the score board draws its number into. A badge added at runtime has
    /// none, so it is taken from the badge it was made from.
    /// </summary>
    private static readonly FieldInfo? BadgeScoreTextField =
        typeof(ScoreBoardUIBadgeBase).GetField("scoreText", InstanceFlags);

    /// <summary>
    /// The flag of a message about a member's game that says they play a round of one.
    /// </summary>
    private const ushort InGameFlag = 1;

    /// <summary>
    /// The net client, for telling the members which game the local player is in.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The data of the other players, which tells whether a member is in the local player's room.
    /// </summary>
    private readonly Dictionary<ushort, ClientPlayerData> _playerData;

    /// <summary>
    /// Gets the IDs of the members of the two-player save that it was checked with, none when no save is paired.
    /// </summary>
    private readonly Func<IReadOnlyCollection<ushort>> _getMembers;

    /// <summary>
    /// The UI manager, for the line that says how to stop watching.
    /// </summary>
    private readonly UiManager _uiManager;

    /// <summary>
    /// The picture of the round that the local player plays, for those who watch it, and of the round that the local
    /// player watches.
    /// </summary>
    private readonly MinigameMirror _mirror = new();

    /// <summary>
    /// Hook that adds the members' rows when a score board appears.
    /// </summary>
    private Hook? _scoreBoardOnEnableHook;

    /// <summary>
    /// Hook that keeps a character of the festival from taking both players on until both have won every game.
    /// </summary>
    private Hook? _outroReadyHook;

    /// <summary>
    /// Hook that notices what the place of the local player's round throws out from the pools of the game.
    /// </summary>
    private Hook? _spawnHook;

    /// <summary>
    /// The games in the loaded rooms. They are looked up again after a room has loaded rather than whenever they are
    /// needed, since that looks through every state machine there is.
    /// </summary>
    private Game[] _games = [];

    /// <summary>
    /// Whether a room has loaded since the games were last looked up.
    /// </summary>
    private bool _gamesStale = true;

    /// <summary>
    /// The game whose round the local player plays, as of the last frame, or null for none.
    /// </summary>
    private Game? _localGame;

    /// <summary>
    /// The game the members were last told the local player is in, by its name, or null for none.
    /// </summary>
    private string? _sentGame;

    /// <summary>
    /// The score the members were last told the local player has in <see cref="_sentGame"/>.
    /// </summary>
    private int _sentScore;

    /// <summary>
    /// The members that <see cref="_sentGame"/> and <see cref="_sentScore"/> were told to. A member who comes back has
    /// forgotten them, so they are told again.
    /// </summary>
    private readonly HashSet<ushort> _sentTo = [];

    /// <summary>
    /// The number the last message about the local player's game went under. They are only sent on a change, and a
    /// lost one is sent again later, which can bring it in after a newer one: the number lets the members tell. It
    /// starts from the clock, so that it still counts up when this game is started again.
    /// </summary>
    private ulong _sentSequence = (ulong) DateTime.UtcNow.Ticks;

    /// <summary>
    /// What each member said of the game they are in, by their ID.
    /// </summary>
    private readonly Dictionary<ushort, MemberGame> _memberGames = new();

    /// <summary>
    /// Which players have won every game of the festival, by their ID. Kept for whoever says it rather than only for
    /// the members: the game that finishes the check first says it at once, often before the other game has finished
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

    public MinigameCoop(
        NetClient netClient,
        Dictionary<ushort, ClientPlayerData> playerData,
        Func<IReadOnlyCollection<ushort>> getMembers,
        UiManager uiManager
    ) {
        _netClient = netClient;
        _playerData = playerData;
        _getMembers = getMembers;
        _uiManager = uiManager;

        // The parts of a place are numbered as its room loads, before anything in it has moved, which every game has
        // to do alike: from here on each room is, whether or not a session is on when it loads
        PersonalPlaces.LookThroughLoaded();
    }

    /// <summary>
    /// A game that each player plays on their own, in a loaded room.
    /// </summary>
    private sealed class Game {
        /// <summary>
        /// The state machine that runs its rounds.
        /// </summary>
        public required PlayMakerFSM Fsm { get; init; }

        /// <summary>
        /// What kind of game it is.
        /// </summary>
        public required GameKind Kind { get; init; }

        /// <summary>
        /// The name of the object of its state machine, which is how the games tell each other which game.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// The variable in which it counts the score of a round, if it has one.
        /// </summary>
        public FsmInt? Score { get; init; }

        /// <summary>
        /// Whether a round of it is played and not yet decided. One whose state machine has not started, as in a room
        /// where the festival is not on, is played by nobody.
        /// </summary>
        public bool IsPlayed => Fsm != null && Fsm.Fsm?.ActiveState is { } state &&
                                Array.IndexOf(Kind.IdleStateNames, state.Name) < 0 &&
                                Array.IndexOf(Kind.EndingStateNames, state.Name) < 0;
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
    /// Registers the hooks of the games.
    /// </summary>
    public void RegisterHooks() {
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

        var spawnMethod = typeof(SpawnObjectFromGlobalPool).GetMethod(
            nameof(SpawnObjectFromGlobalPool.OnEnter), InstanceFlags
        );
        if (spawnMethod == null) {
            Logger.Error("SpawnObjectFromGlobalPool does not declare OnEnter; what a round throws out is not watched");
        } else {
            _spawnHook = new Hook(spawnMethod, OnSpawnEnter);
        }

        MinigameView.Received = OnViewPiece;
        EventHooks.HeroControllerUpdate += OnHeroControllerUpdate;
        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.sceneUnloaded += OnSceneUnloaded;

        // Rooms loaded while unhooked were not noticed
        _gamesStale = true;
    }

    /// <summary>
    /// Deregisters the hooks of the games, and gives back whatever the local player was held, hidden or shown for.
    /// </summary>
    public void DeregisterHooks() {
        StopWatching("this game left the session", tell: false);
        CloseWatchBox();
        LetHeroGo();
        _mirror.StopSending();
        _watchers.Clear();
        ShowEverything();

        _scoreBoardOnEnableHook?.Dispose();
        _scoreBoardOnEnableHook = null;

        _outroReadyHook?.Dispose();
        _outroReadyHook = null;

        _spawnHook?.Dispose();
        _spawnHook = null;

        MinigameView.Received = null;
        EventHooks.HeroControllerUpdate -= OnHeroControllerUpdate;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneUnloaded -= OnSceneUnloaded;

        _outroReadyOf.Clear();
        _sentOutroReady = null;
        _sentOutroReadyTo.Clear();

        _sentGame = null;
        _sentScore = 0;
        _sentTo.Clear();
        _memberGames.Clear();
        _askedSinceRound = false;
        _localGame = null;
        _playedHere = false;
        _room = -1;
    }

    /// <summary>
    /// Takes which game a member plays a round of, and their score in it. The score is their total rather than a single
    /// point, and a message that was overtaken on the way is left out, so one that arrives late or twice can only ever
    /// be ignored. A round that begins begins its score at nothing.
    /// </summary>
    /// <param name="player">The player who sent it.</param>
    /// <param name="update">The update.</param>
    public void OnMemberGame(ClientPlayerData player, CoopSaveUpdate update) {
        if (!IsMember(player.Id)) {
            return;
        }

        var member = GetMemberGame(player.Id);
        if (member.Sequence is { } newest && update.Sequence <= newest) {
            return;
        }

        member.Sequence = update.Sequence;
        var game = (update.Part & InGameFlag) != 0 ? update.ObjectPath : null;
        if (game != null && game != member.Game) {
            member.Rounds++;
            member.Score = 0;
        }

        member.Game = game;
        member.Score = System.Math.Max(member.Score, (int) update.Key);
    }

    /// <summary>
    /// Tells the members which game the local player plays a round of and their score in it, whenever either changes,
    /// including when they no longer play one. A member who was not told yet, like one who came back, is told on their
    /// own.
    /// </summary>
    private void SendLocalGame(Game? local) {
        if (!_netClient.IsConnected) {
            return;
        }

        var game = local?.Name;
        var score = local?.Score?.Value ?? 0;
        if (game != _sentGame || score != _sentScore) {
            _sentGame = game;
            _sentScore = score;
            _sentTo.Clear();
        }

        var members = _getMembers();
        ForgetGone(_sentTo, members);
        var part = game != null ? InGameFlag : (ushort) 0;
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
    /// Sends something about the games to a member of the two-player save.
    /// </summary>
    /// <param name="memberId">The ID of the member.</param>
    /// <param name="kind">What is being told.</param>
    /// <param name="key">The running count it carries, if any.</param>
    /// <param name="part">The flags it carries, if any.</param>
    /// <param name="game">The game it is about, by its name, if any.</param>
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
    /// Gets the game whose round the local player plays, or null when they play none.
    /// </summary>
    private Game? GetLocalGame() {
        RefreshGames();

        foreach (var game in _games) {
            if (game.IsPlayed) {
                return game;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a game of the loaded rooms by its name.
    /// </summary>
    private Game? FindGame(string name) {
        RefreshGames();

        foreach (var game in _games) {
            if (game.Name == name && game.Fsm != null) {
                return game;
            }
        }

        return null;
    }

    /// <summary>
    /// Looks the games up again if a room has loaded since they were last looked up.
    /// </summary>
    private void RefreshGames() {
        if (!_gamesStale) {
            return;
        }

        _gamesStale = false;
        var games = new List<Game>();
        var fsms = Object.FindObjectsByType<PlayMakerFSM>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var fsm in fsms) {
            if (fsm.Fsm == null) {
                continue;
            }

            foreach (var kind in GameKinds) {
                if (fsm.Fsm.Name != kind.FsmName) {
                    continue;
                }

                games.Add(new Game {
                    Fsm = fsm,
                    Kind = kind,
                    Name = fsm.gameObject.name,
                    Score = kind.ScoreVariableName is { } score ? fsm.FsmVariables.FindFsmInt(score) : null
                });
            }
        }

        _games = games.ToArray();
    }

    /// <summary>
    /// Callback for a room having loaded, after which the games are looked up again.
    /// </summary>
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
        _gamesStale = true;
    }

    /// <summary>
    /// Callback for a room having gone, which ends watching a round in it, so that the camera that was held still for
    /// it is let go: a player who quits to the menu while they watch would otherwise find it held in their next game.
    /// </summary>
    private void OnSceneUnloaded(Scene scene) {
        if (_watchedId != null && _mirror.IsViewingIn(scene)) {
            StopWatching("the room was left");
        }
    }

    /// <summary>
    /// Forgets what the local player played and was asked about in the room before, once they are in another room.
    /// </summary>
    private void NoteRoom() {
        var room = SceneManager.GetActiveScene().handle;
        if (room == _room) {
            return;
        }

        _room = room;
        _playedHere = false;
        _askedSinceRound = false;
    }

    /// <summary>
    /// Takes whether a player has won every game of the festival, which counts once they are a member.
    /// </summary>
    /// <param name="player">The player who sent it.</param>
    /// <param name="update">The update.</param>
    public void OnMemberOutroReady(ClientPlayerData player, CoopSaveUpdate update) {
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
    /// Everything about the games that is done every frame: telling the members which game the local player is in,
    /// sending the picture of their round to those who watch it, asking about and keeping up watching another player's
    /// round, hiding who should not be seen, and telling the members whether the local player has won every game of
    /// the festival.
    /// </summary>
    /// <param name="hero">The hero controller that updated.</param>
    private void OnHeroControllerUpdate(HeroController hero) {
        var members = _getMembers();
        if (members.Count == 0) {
            if (_watchedId != null || _localGame != null || _keptOff.Count > 0) {
                StopWatching("no other member is left", tell: false);
                _localGame = null;
                _mirror.StopSending();
                _watchers.Clear();
                ShowEverything();
            }

            return;
        }

        NoteRoom();
        var local = GetLocalGame();
        if (local != _localGame) {
            Logger.Info(local != null
                ? $"The local player began a round of '{local.Name}'"
                : $"The local player's round of '{_localGame?.Name}' is over");
            _localGame = local;
        }

        // After each round of their own, a player is asked once whether to watch the others
        if (local != null) {
            _playedHere = true;
            _askedSinceRound = false;
        }

        SendLocalGame(local);
        UpdateWatchers(local);
        UpdateWatching(hero, local);
        UpdateHiding(hero, local);
        SendOutroReady(members);
    }

    /// <summary>
    /// Tells the members when the local player has won every game of the festival or no longer has. Read from the three
    /// games one by one rather than from the answer above, which this game's own hook has already changed.
    /// </summary>
    private void SendOutroReady(IReadOnlyCollection<ushort> members) {
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
/// What a member of the two-player save said of the game they play a round of.
/// </summary>
internal sealed class MemberGame {
    /// <summary>
    /// The number of the newest message about it so far, or null before the first.
    /// </summary>
    public ulong? Sequence { get; set; }

    /// <summary>
    /// The game they play a round of, by its name, or null when they play none.
    /// </summary>
    public string? Game { get; set; }

    /// <summary>
    /// How many rounds they were seen to begin, which tells one round from the next.
    /// </summary>
    public int Rounds { get; set; }

    /// <summary>
    /// Their score in the round being played, or in the last one.
    /// </summary>
    public int Score { get; set; }
}
