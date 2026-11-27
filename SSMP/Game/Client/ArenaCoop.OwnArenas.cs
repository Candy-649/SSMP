using System.Collections;
using System.Collections.Generic;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// The arenas whose every enemy is a creature that every game runs by itself (see OwnCreatures), with nothing else in
/// their waves but machines of the battle that do the same in every game (see IsBattleMachine), whose battle every
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
    /// Whether every enemy of the waves of an arena is a creature that every game runs by itself, and all else in them
    /// a machine of the battle that does the same in every game (see <see cref="IsBattleMachine"/>). Kept once found
    /// so, and asked again until then, as an arena asked before the room's creatures were taken has none of them yet.
    /// One found otherwise while its battle runs stays so for the stay in the room: what keeps it so may do what it
    /// does and be gone, which would turn the arena into one that every game runs by itself in the middle of its
    /// battle, a game that followed the scene host's battle going on by itself from there (11-27 research of the
    /// allies: the machine that calls an ally in at the second wave of a fight did so, before such machines counted).
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
                } else if (!IsBattleMachine(state, enemy)) {
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
    /// Whether something that a wave of an arena holds besides its creatures is a machine of the battle that does the
    /// same in every game, which every game runs by itself with the rest of the battle: what ends a wave a while after
    /// it starts, sets up the floors of a wave, lets out the creatures that wait in it, flings out the arena's reward,
    /// calls an ally in or plays a scene in the back. They do it in every game in an arena of copies too, as things of
    /// the room. One that holds a creature that every game does not run by itself, or a copy, or that makes creatures,
    /// keeps the battle the scene host's (census of the 16 arenas whose waves held more than creatures, 11-27: all 16
    /// hold only such machines; the sacks and the corpse of the pilgrims' last stand make only a corpse and the silk
    /// that possesses it, which have no health, before they let out the creature that waits in their wave). What it
    /// holds is found once.
    /// </summary>
    private static bool IsBattleMachine(ArenaState state, Transform thing) {
        if (!state.Things.TryGetValue(thing, out var held)) {
            var makes = false;
            foreach (var machine in thing.GetComponentsInChildren<PlayMakerFSM>(true)) {
                foreach (var step in machine.FsmStates) {
                    foreach (var action in step.Actions) {
                        makes |= EntitySpawner.SpawnsCreature(action);
                    }
                }
            }

            held = (thing.GetComponentsInChildren<Transform>(true), thing.GetComponentsInChildren<HealthManager>(true),
                makes);
            state.Things[thing] = held;
        }

        if (held.Makes) {
            return false;
        }

        // What was destroyed since is gone from it: the reward that an item placer flings out is picked up and goes,
        // and its object, kept here, may no longer be asked for its own (review of 8982d9f)
        foreach (var creature in held.Creatures) {
            if (creature != null && OwnCreatures.Owning(creature.gameObject) == null) {
                return false;
            }
        }

        foreach (var part in held.Parts) {
            if (part != null && Entity.Entity.IsRoomObjectOfAnEntity(part.gameObject)) {
                return false;
            }
        }

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
