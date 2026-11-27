using System.Collections;
using System.Collections.Generic;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// The arenas whose every enemy is a creature that every game runs by itself (see OwnCreatures), whose battle every
/// game runs by itself too: it lets out its waves, counts its own dead, starts its next wave and wins as it does alone
/// (USER 11-25 "这个是不是理论上可以两边自己出怪，只要骰子是一样的就行", "做吧"). What a wave lets out is the same in
/// every game, the arena's code rolls no dice, and its next wave goes by how many of the last one died, which comes out
/// the same in every game, as a death is told to every game, also to one where the creature has not come out yet (see
/// CoopSave.OnOwnCreature). Whoever walks in starts the battle in their own game at once and tells the others, whose
/// games start it too, with their gates left open until they walk in (USER 11-25 "就本地先演"). A member who comes
/// into the room while the battle runs follows the scene host's waves, count and win for this battle, as they do in
/// an arena of copies, and their creatures are their own all the same. One who walks into an arena that the scene
/// host's game won already, or has no battle in, does not fight it, as in an arena of copies (see OnStartBattle): in
/// one whose win is not shared, like a boss arena's, they would have fought it alone.
/// </summary>
internal partial class ArenaCoop {
    /// <summary>
    /// The waves of the arenas of the room that called their enemies out here (see <see cref="WaitsForItsWave"/>).
    /// </summary>
    private static readonly HashSet<BattleWave> CalledOutWaves = [];

    /// <summary>
    /// Whether every enemy of the waves of an arena is a creature that every game runs by itself. Kept once found so,
    /// and asked again until then, as an arena asked before the room's creatures were taken has none of them yet. One
    /// found otherwise while its battle runs stays so for the stay in the room: a machine that one of its waves holds,
    /// like the one that calls an ally in at the second wave of a fight, does what it does and is gone, which turned
    /// the arena into one that every game runs by itself in the middle of its battle, a game that followed the scene
    /// host's battle going on by itself from there (11-27 research of the allies).
    /// </summary>
    private bool IsOwnArena(BattleScene battleScene) {
        var state = GetState(battleScene);
        if (state.Own) {
            return true;
        }

        if (state.Mixed || WavesField!.GetValue(battleScene) is not IList { Count: > 0 } waves) {
            return false;
        }

        var taken = false;
        var other = false;
        foreach (var each in waves) {
            if (each is not BattleWave wave || wave == null) {
                continue;
            }

            foreach (Transform enemy in wave.transform) {
                if (OwnCreatures.Owning(enemy.gameObject) != null) {
                    taken = true;
                } else {
                    other = true;
                }
            }
        }

        if (other) {
            state.Mixed = taken && (state.Started || state.HostUpdate?.Status == BattleSceneStatus.Running);
            return false;
        }

        state.Own = true;
        Logger.Info($"Arena '{state.Path}' has only creatures that every game runs by itself, and runs in each game");
        return true;
    }

    /// <summary>
    /// Whether the local player follows the battle of an arena in the scene host's game: one of copies, or one that
    /// every game runs by itself whose battle ran already as the local player came into it.
    /// </summary>
    private bool FollowsHostIn(BattleScene battleScene) {
        return IsFollower() && (GetState(battleScene).FollowsTheHost || !IsOwnArena(battleScene));
    }

    /// <summary>
    /// Whether the scene host's game is the one that sends how the battle of an arena goes on here: in an arena that
    /// every game runs by itself, the player who starts it says so, and after that the scene host's game alone sends
    /// its waves and count, for a member who follows it there.
    /// </summary>
    private bool SendsHowItGoes(BattleScene battleScene) {
        return !IsOwnArena(battleScene) || _entityManager.IsSceneHost;
    }

    /// <summary>
    /// Takes the word of another player's game that the battle of an arena that every game runs by itself started
    /// there, or how it goes on there, which the game of a player who is in its battle already leaves be, and so does
    /// the game of one who follows the scene host there, which takes it as the scene host's. A battle that has just
    /// started there starts here too, by itself; one at a wave already is followed in the scene host's game. Whether it
    /// was the word of such an arena.
    /// </summary>
    private bool TakeOwnArenaWord(BattleScene? battleScene, BattleSceneUpdate update) {
        if (battleScene == null || update.Status is not (BattleSceneStatus.Running or BattleSceneStatus.Won) ||
            !IsOwnArena(battleScene)) {
            return false;
        }

        var state = GetState(battleScene);
        if (state.FollowsTheHost) {
            return false;
        }

        if (update.Status != BattleSceneStatus.Running || state.Started || state.OwnStartPending ||
            IsCompleted(battleScene)) {
            return true;
        }

        // A battle that went on there before this game was told of it, as of a member who has just come in
        if (update.Wave >= 0) {
            Logger.Info(
                $"Arena '{state.Path}' is at wave {update.Wave} in another player's game, and is followed in the " +
                "scene host's game for this battle"
            );
            state.FollowsTheHost = true;
            return false;
        }

        state.OwnStartPending = true;
        StartOwnArena(battleScene, state);
        return true;
    }

    /// <summary>
    /// Starts the battle of an arena that every game runs by itself, which another player's game started, once the
    /// arena is out and has turned off the enemies of its waves after loading: here by itself, with the gates left
    /// open until the local player walks in.
    /// </summary>
    private void StartOwnArena(BattleScene battleScene, ArenaState state) {
        if (!state.OwnStartPending || !battleScene.gameObject.activeInHierarchy ||
            GetInt(LoopsUntilDeactivateField, battleScene) > 0) {
            return;
        }

        state.OwnStartPending = false;
        if (state.Started || IsCompleted(battleScene)) {
            return;
        }

        Logger.Info($"Arena '{state.Path}' started in another player's game, and starts here too, by itself");
        StartForOtherPlayer(battleScene);
    }

    /// <summary>
    /// Notes that a wave of an arena called its enemies out here, or that this game started a wave after it, which it
    /// skipped, as the scene host's game had it.
    /// </summary>
    private static void NoteCalledOut(BattleWave wave) {
        CalledOutWaves.Add(wave);
    }

    /// <summary>
    /// Whether a creature of the room stands in a wave of an arena that has not called it out here yet: its wave
    /// counts it as it calls it out, and one that died before that here, counted after, would leave its wave waiting
    /// for it for good (see CoopSave.OnOwnCreature).
    /// </summary>
    internal static bool WaitsForItsWave(GameObject creature) {
        return creature.transform.parent is { } parent && parent.TryGetComponent<BattleWave>(out var wave) &&
               !CalledOutWaves.Contains(wave);
    }
}
