using SSMP.Networking.Packet.Data;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// The arenas of a memory, for a player who comes to one after the scene host won it. A memory saves no win: it starts
/// afresh each time a player comes into it, and what the end of a fight there opens - the gates of a chamber, the roof
/// over the last one - opens only in the game that plays that end. The scene host's game played it as it won. A player
/// who came into the memory after that, or started it over after giving up, found the arena won there and so not to be
/// fought, and nothing in their own game ever opened the way on, while the scene host waited for them at the boss further
/// up, whose fight starts only once both players are there. Such a player now has the end played in their own game as
/// they walk into the arena, without the fight, the way the arenas that save no win have theirs (see LiveEnds). An end
/// that a creature plays, like a boss that its arena tells the fight is over, belongs to the scene host, which runs it.
/// </summary>
internal partial class ArenaCoop {
    /// <summary>
    /// Whether an arena is in a memory: in a room of the memory map zone, which holds every memory and nothing else.
    /// GameManager.IsMemoryScene also counts another zone, of a real place whose arenas save their wins. Only the
    /// arenas of the room that is loaded are asked about.
    /// </summary>
    /// <param name="battleScene">The arena.</param>
    private static bool IsInMemory(BattleScene battleScene) {
        var gameManager = global::GameManager.instance;
        return gameManager != null && battleScene.gameObject.scene == SceneManager.GetActiveScene() &&
               gameManager.GetCurrentMapZoneEnum() == GlobalEnums.MapZone.MEMORY;
    }

    /// <summary>
    /// Plays the end of the fight of an arena of a memory for the local player, who walked into it after the scene host
    /// won it (see the summary of this file). The arena counts as won here from then on, so it is not fought again.
    /// </summary>
    /// <param name="battleScene">The arena.</param>
    /// <param name="state">Its co-op state.</param>
    /// <param name="walkedIn">Whether the local player is known to have walked in: the scene host answered them after
    /// they did, when they may stand anywhere, also just outside the trigger they went through.</param>
    private void TryPlayMemoryEnd(BattleScene battleScene, ArenaState state, bool walkedIn) {
        if (state.LiveEndPlayed || state.MemoryEndLeftToHost || IsCompleted(battleScene) ||
            state.HostUpdate?.Status != BattleSceneStatus.AlreadyWon || !IsInMemory(battleScene)) {
            return;
        }

        if (!walkedIn && !IsLocalHeroInArena(battleScene)) {
            Logger.Info(
                $"Arena '{state.Path}' of this memory was won by the scene host before the local player came, but it " +
                "was started while they were not in it, so its end is played when they walk in"
            );
            return;
        }

        if (EndSceneField?.GetValue(battleScene) is not PlayMakerFSM endScene || endScene == null) {
            state.LiveEndPlayed = true;
            CompletedField!.SetValue(battleScene, true);
            Logger.Info(
                $"Arena '{state.Path}' of this memory was won by the scene host before the local player came, and has " +
                "no end of its own to play here: it counts as won here as well"
            );
            return;
        }

        if (Entity.Entity.FindByPart(endScene.gameObject) != null) {
            state.MemoryEndLeftToHost = true;
            Logger.Info(
                $"Arena '{state.Path}' of this memory was won by the scene host before the local player came; its end " +
                $"goes to '{endScene.gameObject.name}', a creature that the scene host runs, so it is not played here"
            );
            return;
        }

        // The object that plays the end is switched off in this game for now, or not where it waits for the end: the
        // next time the local player walks in
        if (!endScene.isActiveAndEnabled || !TakesEvent(endScene, BattleEndEvent)) {
            Logger.Info(
                $"Arena '{state.Path}' of this memory was won by the scene host before the local player came, but its " +
                $"end '{endScene.gameObject.name}' is {(endScene.isActiveAndEnabled ? $"in '{endScene.ActiveStateName}'" : "switched off")} " +
                "here, so it is played the next time they walk in"
            );
            return;
        }

        state.LiveEndPlayed = true;
        CompletedField!.SetValue(battleScene, true);
        Logger.Info(
            $"Arena '{state.Path}' of this memory was won by the scene host before the local player came, playing its " +
            $"end here without the fight: '{endScene.gameObject.name}' ({endScene.FsmName}, in " +
            $"'{endScene.ActiveStateName}') is told '{BattleEndEvent}'"
        );
        endScene.SendEvent(BattleEndEvent);
    }

    /// <summary>
    /// Whether an FSM goes anywhere on an event from the state it is in, by a transition of that state or a global one.
    /// </summary>
    /// <param name="fsm">The FSM.</param>
    /// <param name="eventName">The name of the event.</param>
    private static bool TakesEvent(PlayMakerFSM fsm, string eventName) {
        var machine = fsm.Fsm;
        if (machine == null) {
            return false;
        }

        foreach (var transition in machine.GlobalTransitions ?? []) {
            if (transition.EventName == eventName) {
                return true;
            }
        }

        foreach (var transition in machine.ActiveState?.Transitions ?? []) {
            if (transition.EventName == eventName) {
                return true;
            }
        }

        return false;
    }
}
