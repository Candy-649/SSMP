using System.Collections.Generic;
using GlobalEnums;
using SSMP.Networking.Packet.Data;
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
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// The other players in the local scene who are in a film of the game, by ID.
    /// </summary>
    private readonly HashSet<ushort> _playersInCutscene = [];

    /// <summary>
    /// Whether the local player was in a film of the game as of the last frame, as the other players were told.
    /// </summary>
    private bool _localInCutscene;

    /// <summary>
    /// Tells the other players as the local player goes into a film of the game or comes out of one, and forgets the
    /// films of the players who left the local scene.
    /// </summary>
    private void TellCutscene() {
        if (_playersInCutscene.Count > 0) {
            _playersInCutscene.RemoveWhere(_isNotInLocalScene ??= IsNotInLocalScene);
        }

        var inCutscene = global::GameManager.instance is { GameState: GameState.CUTSCENE };
        if (inCutscene == _localInCutscene) {
            return;
        }

        _localInCutscene = inCutscene;
        Logger.Info(
            inCutscene
                ? "The local player is in a film of the game, which the other players are told"
                : "The local player is out of the film of the game, which the other players are told"
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
                Logger.Info($"Player {update.PlayerId} is in a film of the game, so they are not in any room here yet");
            }

            return;
        }

        if (_playersInCutscene.Remove(update.PlayerId)) {
            Logger.Info($"Player {update.PlayerId} is out of the film of the game");
        }
    }
}
