using System.Collections.Generic;
using GlobalEnums;
using SSMP.Game.Client.Save;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// Films of the game, which a room plays for each player in their own game, like the one some rooms play as a player
/// comes in. A player in one can do nothing yet, however near the boss their character stands, so they are not in the
/// room yet (see AreAllInRoom): the boss's intro waits for every player (USER 09-15). The boss of a room whose arrival
/// film starts its fight waited for nobody: the first player's film ended, the fight began, and the other player came
/// out of theirs a second later with the boss already at them (USER 11-07 "我朋友说上去刚开始不知道为什么动不了"). So each
/// game tells the others as its player goes into a film and comes out of it.
/// A player still arriving in a room is in one the same way, though the game plays none: the game's own way in, like
/// the walk out of a lift, keeps them from acting until it ends. Told as nothing, they counted as in the room from the
/// moment their character showed in the lift, and a boss whose fight was on went after them there before they could
/// move (USER 11-09 "后来我上去也是，人还在电梯里呢，已经被打了"). Nothing goes after a player in a film or still arriving,
/// as nothing could in their own game.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// The other players in the local scene who are in a film of the game, by ID.
    /// </summary>
    private readonly HashSet<ushort> _playersInCutscene = [];

    /// <summary>
    /// Whether the local player was in a film of the game, or still arriving, as of the last frame, as the other
    /// players were told.
    /// </summary>
    private bool _localInCutscene;

    /// <summary>
    /// The scene in which the local player was last free to act (see IsLocalHeroFree), by its handle: in a scene they
    /// came into, they are still arriving until they are. A room loaded again is another scene.
    /// </summary>
    private int _arrivedInScene;

    /// <summary>
    /// Tells the other players as the local player goes into a film of the game or comes out of one, or comes into a
    /// room and is free to act there, and forgets the films of the players who left the local scene.
    /// </summary>
    private void TellCutscene() {
        if (_playersInCutscene.Count > 0) {
            _playersInCutscene.RemoveWhere(_isNotInLocalScene ??= IsNotInLocalScene);
        }

        var scene = SceneManager.GetActiveScene().handle;
        if (_arrivedInScene != scene && IsLocalHeroFree()) {
            _arrivedInScene = scene;
        }

        var inFilm = global::GameManager.instance is { GameState: GameState.CUTSCENE };
        var inCutscene = inFilm || _arrivedInScene != scene;
        if (inCutscene == _localInCutscene) {
            return;
        }

        _localInCutscene = inCutscene;
        Logger.Info(
            inFilm ? "The local player is in a film of the game, which the other players are told"
            : inCutscene ? "The local player is still arriving in the room, which the other players are told as a film"
            : "The local player is out of the film of the game, or has arrived, which the other players are told"
        );
        Send(
            inCutscene ? BossRoomUpdateKind.CutsceneBegan : BossRoomUpdateKind.CutsceneEnded,
            "", "", "", "", ""
        );
    }

    /// <summary>
    /// Tells a player who came into the local scene that the local player is in a film of the game, if they are.
    /// </summary>
    private void TellCutsceneToNewPlayer() {
        if (_localInCutscene) {
            Send(BossRoomUpdateKind.CutsceneBegan, "", "", "", "", "");
        }
    }

    /// <summary>
    /// Remembers that another player went into a film of the game in the local scene, or came out of one wherever they
    /// are now.
    /// </summary>
    private void OnCutsceneReport(BossRoomUpdate update) {
        if (update.Kind == BossRoomUpdateKind.CutsceneBegan) {
            if (update.SceneName == SceneManager.GetActiveScene().name && _playersInCutscene.Add(update.PlayerId)) {
                Logger.Info(
                    $"Player {update.PlayerId} is in a film of the game or still arriving, so they are not in any " +
                    "room here yet and nothing goes after them"
                );
                if (BodyInLocalScene(update.PlayerId) is { } body) {
                    PlayerTargetRegistry.UnregisterRemotePlayer(body);
                    GamePatcher.ForgetPlayerAsTarget(body);
                }
            }

            return;
        }

        if (_playersInCutscene.Remove(update.PlayerId)) {
            Logger.Info($"Player {update.PlayerId} is out of the film of the game, or has arrived");

            // Not one lying in a cocoon, whom nothing goes after either (CoopSave.SetPartnerBodyHidden)
            if (BodyInLocalScene(update.PlayerId) is { } body && !CoopSave.IsBodyKeptHidden(update.PlayerId)) {
                PlayerTargetRegistry.RegisterRemotePlayer(body);
            }
        }
    }

    /// <summary>
    /// The character of a player in the local scene, or null for one who is not.
    /// </summary>
    private GameObject? BodyInLocalScene(ushort id) {
        return _playerData.TryGetValue(id, out var player) && player.IsInLocalScene ? player.PlayerObject : null;
    }
}
