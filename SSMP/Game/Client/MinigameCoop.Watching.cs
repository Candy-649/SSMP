using System;
using System.Collections.Generic;
using HutongGames.PlayMaker.Actions;
using SSMP.Game.Client.Save;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Watching the round of another player, and being watched. A player whose round is over while other players in the
/// room still play one is asked once, in the game's own box, whether to watch (USER 2026-10-22: "直接画一个窗口是否观战，
/// 就用游戏素材的提交物品那个"). Watching holds their hero still and hides it, puts their camera where the camera of
/// the player who plays is, and shows them that round as its player sees it (see <see cref="MinigameMirror"/>).
///
/// With more players than two (USER 2026-10-22, "那如果有很多人的话，这个观战是观战哪个", then "改吧" to this): left
/// and right go from one player who still plays to the next, in the order of their IDs, and once the round of the
/// watched player is over, the next one who still plays is watched. Watching ends when nobody plays any more, or when
/// the player presses jump. The line that says whom they watch is at the bottom of the screen, as the user asked. The
/// game of a player who plays sends the picture only while somebody watches.
/// </summary>
internal partial class MinigameCoop {
    /// <summary>
    /// The source of the invulnerability of a player who is asked whether to watch, or watches, since they stand still
    /// with their hero out of their hands.
    /// </summary>
    private static readonly object WatchInvulnerability = new();

    /// <summary>
    /// How often the line that says who is watched is written again, in seconds, for the score in it.
    /// </summary>
    private const float WatchLineEvery = 0.25f;

    /// <summary>
    /// The members who watch the round that the local player plays, each with the game they said they watch.
    /// </summary>
    private readonly Dictionary<ushort, string> _watchers = new();

    /// <summary>
    /// The watchers who no longer watch the local player's round, kept to be filled again.
    /// </summary>
    private readonly List<ushort> _goneWatchers = [];

    /// <summary>
    /// The members in the room who play a round that can be watched here, in the order of their IDs, kept to be filled
    /// again (see <see cref="FindWatchable"/>).
    /// </summary>
    private readonly List<ushort> _watchable = [];

    /// <summary>
    /// Sends a piece of the picture of the local player's round, made once.
    /// </summary>
    private Action<byte[]>? _sendViewPiece;

    /// <summary>
    /// The member whose round the local player watches, or null while they watch none.
    /// </summary>
    private ushort? _watchedId;

    /// <summary>
    /// The game that the watched member plays, by its name.
    /// </summary>
    private string? _watchedGame;

    /// <summary>
    /// The name of the watched member, for the line and the log.
    /// </summary>
    private string _watchedName = "";

    /// <summary>
    /// Whether the jump button was up since watching began. The press that said yes in the box can be the jump button
    /// too, and must not stop watching in the same moment.
    /// </summary>
    private bool _jumpUpSinceWatching;

    /// <summary>
    /// When the line that says who is watched is written again, in unscaled time.
    /// </summary>
    private float _watchLineAt;

    /// <summary>
    /// What the line that says who is watched said last: whom, their score, where they are among those who play, and
    /// how many play. Null when it has to be written anew.
    /// </summary>
    private (ushort Id, int Score, int Index, int Playing)? _watchLineSaid;

    /// <summary>
    /// Whether the hero is held for watching, or for the box that asks about it.
    /// </summary>
    private bool _heroHeld;

    /// <summary>
    /// The count of the game's taking control of the hero as of when it was held here: when another took it since,
    /// giving it back is theirs to do.
    /// </summary>
    private int _heroControlVersion;

    /// <summary>
    /// Whether the camera was held still for watching.
    /// </summary>
    private bool _cameraHeld;

    /// <summary>
    /// The box that asks whether to watch, while it is open, or null.
    /// </summary>
    private object? _watchBox;

    /// <summary>
    /// What the box held as its yes once it opened, which tells whether it is still the box that asks.
    /// </summary>
    private object? _watchBoxYes;

    /// <summary>
    /// Whether the local player was asked whether to watch since their last round: they are asked once after each.
    /// </summary>
    private bool _askedSinceRound;

    /// <summary>
    /// Whether the local player played a round in this room since they came into it: only a player whose own round is
    /// over is asked whether to watch another's.
    /// </summary>
    private bool _playedHere;

    /// <summary>
    /// The handle of the room the local player was last seen in, or -1.
    /// </summary>
    private int _room = -1;

    /// <summary>
    /// Whether taking note of what a round throws out went wrong, which is only logged once.
    /// </summary>
    private bool _thrownFailed;

    /// <summary>
    /// Takes whether a member watches the round that the local player plays.
    /// </summary>
    /// <param name="player">The player who sent it.</param>
    /// <param name="update">The update.</param>
    public void OnMemberWatch(ClientPlayerData player, CoopSaveUpdate update) {
        if (!IsMember(player.Id)) {
            return;
        }

        if (update.Part != 0) {
            _watchers[player.Id] = update.ObjectPath;

            // A player who just began to watch sees the whole round at once rather than its pieces as they change
            _mirror.SendWholeSoon();
            Logger.Info($"{player.Username} watches the local player's round of '{update.ObjectPath}'");
        } else if (_watchers.Remove(player.Id)) {
            Logger.Info($"{player.Username} stopped watching the local player's round");
        }
    }

    /// <summary>
    /// Takes a piece of the picture of another player's round.
    /// </summary>
    private void OnViewPiece(GameObject playerObject, byte[] piece) {
        _mirror.Take(playerObject, piece);
    }

    /// <summary>
    /// Hook for something being thrown out from the pools of the game, which is shown with the round of the local
    /// player to those who watch it if the place of the round threw it, like what is thrown at the player.
    /// </summary>
    private void OnSpawnEnter(Action<SpawnObjectFromGlobalPool> orig, SpawnObjectFromGlobalPool self) {
        orig(self);

        if (_localGame == null || !_mirror.IsSending) {
            return;
        }

        try {
            if (self.storeObject?.Value is { } thing && thing != null && self.Fsm?.GameObject is { } owner &&
                owner != null && PersonalPlaces.Contains(owner)) {
                _mirror.AddThrown(thing);
            }
        } catch (Exception e) {
            if (!_thrownFailed) {
                _thrownFailed = true;
                Logger.Warn($"Could not take note of what a round threw out: {e.GetType()}, {e.Message}");
            }
        }
    }

    /// <summary>
    /// Sends the picture of the local player's round while members in the room watch it, and forgets the watchers who
    /// are gone or watch another round.
    /// </summary>
    private void UpdateWatchers(Game? local) {
        if (_watchers.Count > 0) {
            _goneWatchers.Clear();
            foreach (var pair in _watchers) {
                if (local == null || pair.Value != local.Name || !_playerData.TryGetValue(pair.Key, out var player) ||
                    !player.IsInLocalScene) {
                    _goneWatchers.Add(pair.Key);
                }
            }

            foreach (var id in _goneWatchers) {
                _watchers.Remove(id);
            }
        }

        if (local == null || _watchers.Count == 0) {
            if (_mirror.IsSending) {
                _mirror.StopSending();
            }

            return;
        }

        if (!PersonalPlaces.TryFindPlace(local.Fsm.gameObject, out var place)) {
            return;
        }

        var controller = GetCameraController();
        var camera = controller != null ? (Vector2) controller.transform.position : Vector2.zero;
        _mirror.Send(local.Fsm.gameObject.scene, place, camera, _sendViewPiece ??= SendViewPiece);
    }

    /// <summary>
    /// Sends a piece of the picture of the local player's round to the players in the room.
    /// </summary>
    private void SendViewPiece(byte[] piece) {
        if (_netClient.IsConnected) {
            _netClient.UpdateManager.UpdatePlayerAnimation(SSMP.Animation.AnimationClip.MinigameView, 0, piece);
        }
    }

    /// <summary>
    /// Keeps up watching another player's round, or else waits for the answer of the box that asks about it, or else
    /// asks about it when the time comes.
    /// </summary>
    private void UpdateWatching(HeroController hero, Game? local) {
        if (_watchedId is { } watched) {
            KeepWatching(hero, watched);
            return;
        }

        if (_watchBox != null) {
            CheckWatchBox();
            return;
        }

        OfferToWatch(hero, local);
    }

    /// <summary>
    /// Fills <see cref="_watchable"/> with the members in the room who play a round of a game that is in this room
    /// too, in the order of their IDs.
    /// </summary>
    private void FindWatchable() {
        _watchable.Clear();
        foreach (var pair in _memberGames) {
            if (pair.Value.Game is { } game && _playerData.TryGetValue(pair.Key, out var player) &&
                player.IsInLocalScene && player.PlayerObject != null && IsMember(pair.Key) && FindGame(game) != null) {
                _watchable.Add(pair.Key);
            }
        }

        _watchable.Sort();
    }

    /// <summary>
    /// Asks the local player once whether to watch the others' rounds, when their own round in this room is over,
    /// another member in the room still plays one, and they are free: on their feet, with their hero in their hands,
    /// and nothing else on screen. A no is not asked again until they have played another round.
    /// </summary>
    private void OfferToWatch(HeroController hero, Game? local) {
        if (local != null || !_playedHere || _askedSinceRound || !IsHeroFree(hero) || CoopSave.IsDialogueOrBoxBusy()) {
            return;
        }

        FindWatchable();
        if (_watchable.Count == 0) {
            return;
        }

        _askedSinceRound = true;
        AskToWatch(hero, _watchable[0], _watchable.Count);
    }

    /// <summary>
    /// Asks the local player in the game's own box whether to watch, with their hero held still.
    /// </summary>
    /// <param name="hero">The hero.</param>
    /// <param name="first">The member who is watched first, whom the box names.</param>
    /// <param name="playing">How many members play a round that can be watched.</param>
    private void AskToWatch(HeroController hero, ushort first, int playing) {
        HoldHero(hero);

        var shown = new object();
        _watchBox = shown;

        // Only the box that is still asking may answer: one that is only finishing its closing animation must not
        void Answer(bool yes) {
            if (!ReferenceEquals(_watchBox, shown)) {
                return;
            }

            _watchBox = null;
            _watchBoxYes = null;
            if (yes && StartWatching(first)) {
                return;
            }

            if (!yes) {
                Logger.Info("The local player chose not to watch");
            }

            LetHeroGo();
        }

        var name = _playerData.TryGetValue(first, out var player) ? player.Username : "";
        CoopSave.ForgetItemBoxChoice();
        DialogueYesNoBox.Open(
            () => Answer(true),
            () => Answer(false),
            true,
            playing > 1
                ? Lang.Pick(
                    $"{name} and {playing - 1} more are still playing. Watch?", $"{name} 等 {playing} 人还在玩。要观战吗？"
                )
                : Lang.Pick($"{name} is still playing. Watch?", $"{name} 还在玩。要观战吗？"),
            null
        );

        // The box sets its yes as it opens. Without one, the game had no box to open, or which box is on screen can't
        // be told, and nothing would ever answer it: the hero would stay held for good
        _watchBoxYes = CoopSave.GetItemBoxYes();
        if (_watchBoxYes == null) {
            _watchBox = null;
            LetHeroGo();
            Logger.Warn("Could not ask the local player whether to watch: the game's box did not open");
            return;
        }

        Logger.Info($"Asked the local player whether to watch the round of {name}, one of {playing} who play");
    }

    /// <summary>
    /// Gives up on the box that asks whether to watch if something else took its place, which never answers it, once
    /// nothing is on screen any more. The hero would otherwise stay held for good.
    /// </summary>
    private void CheckWatchBox() {
        if (_watchBoxYes == null || ReferenceEquals(CoopSave.GetItemBoxYes(), _watchBoxYes) ||
            CoopSave.IsDialogueOrBoxBusy()) {
            return;
        }

        Logger.Info("The box that asked whether to watch went away without an answer");
        _watchBox = null;
        _watchBoxYes = null;
        LetHeroGo();
    }

    /// <summary>
    /// Closes the box that asks whether to watch, if it is still open, for a game that leaves the session: its answer
    /// would do nothing any more.
    /// </summary>
    private void CloseWatchBox() {
        var stillAsking = _watchBox != null && _watchBoxYes != null &&
                          ReferenceEquals(CoopSave.GetItemBoxYes(), _watchBoxYes);
        _watchBox = null;
        _watchBoxYes = null;
        if (stillAsking) {
            DialogueYesNoBox.ForceClose();
        }
    }

    /// <summary>
    /// Starts watching the rounds of the members who play one: the member the box named, or the first one who still
    /// plays if that one is done already.
    /// </summary>
    /// <returns>Whether watching started.</returns>
    private bool StartWatching(ushort first) {
        FindWatchable();
        if (_watchable.Count == 0) {
            Logger.Info("The rounds that the local player chose to watch were over already");
            return false;
        }

        if (!Watch(_watchable.Contains(first) ? first : _watchable[0])) {
            return false;
        }

        _jumpUpSinceWatching = false;
        HoldCamera();
        return true;
    }

    /// <summary>
    /// Begins to take the picture of a member's round, and tells them.
    /// </summary>
    /// <returns>Whether this game has the place of their round.</returns>
    private bool Watch(ushort id) {
        if (!_playerData.TryGetValue(id, out var player) || player.PlayerObject is not { } character ||
            character == null || !_memberGames.TryGetValue(id, out var member) || member.Game is not { } game ||
            FindGame(game) is not { } own || !PersonalPlaces.TryFindPlace(own.Fsm.gameObject, out var place) ||
            !_mirror.StartViewing(character, own.Fsm.gameObject.scene, place)) {
            Logger.Warn($"Could not find the round of member {id} in this room to watch it");
            return false;
        }

        _watchedId = id;
        _watchedGame = game;
        _watchedName = player.Username;
        _watchLineAt = 0f;
        _watchLineSaid = null;
        Send(id, CoopSaveUpdateKind.MinigameWatch, 0, 1, game);
        Logger.Info($"Watching the round of '{game}' that {player.Username} plays");
        return true;
    }

    /// <summary>
    /// Keeps the picture, the camera and the line of the watched round up. Left and right go to another member who
    /// plays, and so does the end of the watched round or the watched member leaving. Watching ends once nobody
    /// plays, the local player's hero is taken by the game, or the local player presses jump.
    /// </summary>
    private void KeepWatching(HeroController hero, ushort id) {
        if (IsHeroTaken(hero)) {
            StopWatching("the game took the local player's hero");
            return;
        }

        if (_mirror.IsMismatched) {
            StopWatching("the watched place is not the same in both games");
            return;
        }

        FindWatchable();
        if (!_memberGames.TryGetValue(id, out var member) || member.Game != _watchedGame || !_watchable.Contains(id)) {
            if (_watchable.Count == 0) {
                StopWatching("nobody plays a round any more");
            } else {
                WatchAnother(GetNextWatchable(id, 1), $"the round of {_watchedName} is over");
            }

            return;
        }

        if (InputHandler.Instance is { } input && global::GameManager.instance is { isPaused: false }) {
            var actions = input.inputActions;
            if (!_jumpUpSinceWatching) {
                _jumpUpSinceWatching = !actions.Jump.IsPressed;
            } else if (actions.Jump.WasPressed) {
                StopWatching("the local player pressed jump");
                return;
            }

            var step = actions.Right.WasPressed ? 1 : actions.Left.WasPressed ? -1 : 0;
            if (step != 0 && _watchable.Count > 1) {
                WatchAnother(GetNextWatchable(id, step), "the local player switched");
                return;
            }
        }

        _mirror.UpdateView();
        if (_mirror.Camera is { } camera) {
            PlaceCamera(camera);
        }

        ShowWatchLine(member, id);
    }

    /// <summary>
    /// The member who plays next to one in the order of their IDs, going round, in a direction. One who no longer plays
    /// counts from where they were in that order. <see cref="_watchable"/> has to be filled, and not be empty.
    /// </summary>
    private ushort GetNextWatchable(ushort from, int step) {
        var count = _watchable.Count;
        var at = _watchable.IndexOf(from);
        if (at >= 0) {
            return _watchable[(at + step + count) % count];
        }

        var after = 0;
        while (after < count && _watchable[after] < from) {
            after++;
        }

        return _watchable[step > 0 ? after % count : (after - 1 + count) % count];
    }

    /// <summary>
    /// Watches the round of another member instead, with the hero and the camera still held.
    /// </summary>
    /// <param name="id">The member.</param>
    /// <param name="reason">Why, for the log.</param>
    private void WatchAnother(ushort id, string reason) {
        var before = _watchedName;
        EndView(tell: true);
        if (Watch(id)) {
            Logger.Info($"Watching {_watchedName} rather than {before}: {reason}");
            return;
        }

        LetCameraGo();
        LetHeroGo();
        _uiManager.MinigamePrompt.Hide();
        Logger.Info($"Stopped watching, since no other round could be watched: {reason}");
    }

    /// <summary>
    /// Stops taking the picture of the watched round, and tells its member if asked to.
    /// </summary>
    private void EndView(bool tell) {
        if (tell && _watchedId is { } id) {
            Send(id, CoopSaveUpdateKind.MinigameWatch, 0, 0, _watchedGame);
        }

        _watchedId = null;
        _watchedGame = null;
        _mirror.StopViewing();
    }

    /// <summary>
    /// Stops watching: tells the watched member, takes the picture away, and gives the camera and the hero back.
    /// </summary>
    /// <param name="reason">Why, for the log.</param>
    /// <param name="tell">Whether to tell the watched member, which a game that left the session cannot.</param>
    private void StopWatching(string reason, bool tell = true) {
        if (_watchedId == null) {
            return;
        }

        EndView(tell);
        LetCameraGo();
        LetHeroGo();
        _uiManager.MinigamePrompt.Hide();
        Logger.Info($"Stopped watching the round of {_watchedName}: {reason}");
    }

    /// <summary>
    /// Shows at the bottom of the screen who is watched, their score where the game counts one, and how to switch and
    /// to stop.
    /// </summary>
    private void ShowWatchLine(MemberGame member, ushort id) {
        var now = Time.unscaledTime;
        if (now < _watchLineAt) {
            return;
        }

        // Written again only when something in it changed, rather than made anew every time
        _watchLineAt = now + WatchLineEvery;
        var playing = _watchable.Count;
        var index = _watchable.IndexOf(id);
        var said = (id, member.Score, index, playing);
        if (_watchLineSaid == said) {
            return;
        }

        _watchLineSaid = said;
        var place = $"{index + 1}/{playing}";
        var scored = _watchedGame != null && FindGame(_watchedGame)?.Kind.ScoreVariableName != null;
        var english = (playing > 1 ? $"Watching {_watchedName} ({place})" : $"Watching {_watchedName}") +
                      (scored ? $": {member.Score}. " : ". ") +
                      (playing > 1 ? "Left or right to switch, jump to stop watching" : "Press jump to stop watching");
        var chinese = (playing > 1 ? $"正在观战 {_watchedName}（{place}）" : $"正在观战 {_watchedName}") +
                      (scored ? $"：{member.Score} 分。" : "。") +
                      (playing > 1 ? "按左右换人，按跳跃键停止观战" : "按跳跃键停止观战");
        _uiManager.MinigamePrompt.Show(Lang.Pick(english, chinese));
    }

    /// <summary>
    /// Whether the local player is free to be asked something: on their feet in the room, with their hero in their
    /// hands, and not paused.
    /// </summary>
    private static bool IsHeroFree(HeroController hero) {
        return hero.acceptingInput && !hero.controlReqlinquished && hero.cState.onGround && !IsHeroTaken(hero) &&
               global::GameManager.instance is { isPaused: false } gameManager && !gameManager.IsInSceneTransition;
    }

    /// <summary>
    /// Whether the game has the hero for itself: dead, down, hurt by the room or on the way to another room.
    /// </summary>
    private static bool IsHeroTaken(HeroController hero) {
        return hero.cState.dead || hero.cState.hazardDeath || hero.cState.hazardRespawning ||
               hero.cState.transitioning || PlayerTargetRegistry.IsPlayerDown(hero.gameObject);
    }

    /// <summary>
    /// Holds the hero still and out of harm's way, for the box and for watching.
    /// </summary>
    private void HoldHero(HeroController hero) {
        if (_heroHeld) {
            return;
        }

        _heroControlVersion = HeroController.ControlVersion + 1;
        hero.RelinquishControl();
        hero.AddInvulnerabilitySource(WatchInvulnerability);
        _heroHeld = true;
    }

    /// <summary>
    /// Gives the hero back, unless something else took it since it was held here.
    /// </summary>
    private void LetHeroGo() {
        if (!_heroHeld) {
            return;
        }

        _heroHeld = false;
        var hero = HeroController.instance;
        if (hero == null) {
            return;
        }

        hero.RemoveInvulnerabilitySource(WatchInvulnerability);

        // Anyone taking control counts up the version, so one that still matches says the hero is ours to give back
        if (HeroController.ControlVersion == _heroControlVersion && hero.controlReqlinquished && !IsHeroTaken(hero) &&
            PlayerData.instance?.atBench != true) {
            hero.RegainControl();
        }
    }

    /// <summary>
    /// The controller of the game's camera, or null if there is none.
    /// </summary>
    private static CameraController? GetCameraController() {
        var cameras = GameCameras.instance;
        return cameras != null ? cameras.cameraController : null;
    }

    /// <summary>
    /// Holds the camera still, so that it no longer follows the hero and only goes where it is put.
    /// </summary>
    private void HoldCamera() {
        var controller = GetCameraController();
        if (controller == null) {
            return;
        }

        if (controller.mode != CameraController.CameraMode.FROZEN) {
            controller.FreezeInPlace(true);
        }

        _cameraHeld = true;
    }

    /// <summary>
    /// Puts the camera where the camera of the player who plays is.
    /// </summary>
    private void PlaceCamera(Vector2 at) {
        var controller = GetCameraController();
        if (controller == null) {
            return;
        }

        // Something of the room may have let go of it
        if (controller.mode != CameraController.CameraMode.FROZEN) {
            controller.FreezeInPlace(true);
            _cameraHeld = true;
        }

        controller.SnapTo(at.x, at.y);
    }

    /// <summary>
    /// Lets the camera follow the hero again, the way the game lets go of it (the CameraStopFreeze action), which also
    /// locks it back to the area of the room the hero is in.
    /// </summary>
    private void LetCameraGo() {
        if (!_cameraHeld) {
            return;
        }

        _cameraHeld = false;
        var controller = GetCameraController();
        if (controller != null && controller.mode == CameraController.CameraMode.FROZEN) {
            controller.StopFreeze(true);
        }
    }
}
