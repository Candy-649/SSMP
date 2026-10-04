using System.Collections.Generic;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client;

/// <summary>
/// The one way into an arena that locks itself behind a player as they come in. The arena in Under_10 is entered
/// through a hatch that a lever opens, and a trigger just inside of it, 'Battle Active/Trapdoor Closer', sends BG CLOSE
/// to the hatch whenever the player walks in. That forces the levers down, so that hitting them does nothing until the
/// battle is won and BG OPEN frees them. It is the only such trigger and the only hatch that locks its levers.
///
/// A player who walked in and back out before the hatch shut was locked out of the arena in their own game, while the
/// other player went in and started the fight without them, and could die in there with nobody able to come for them.
/// Like every other way into an arena, the hatch now stays locked only for a player who is in: a player outside of it
/// gets its levers back, and walking in locks it behind them again.
/// </summary>
internal partial class ArenaCoop {
    /// <summary>
    /// The scene of the arena whose hatch locks itself.
    /// </summary>
    private const string LockingHatchScene = "Under_10";

    /// <summary>
    /// The path of that hatch below its arena.
    /// </summary>
    private const string LockingHatchPath = "Gates/Pipe_Vent_Hatch_BG";

    /// <summary>
    /// The detector of that hatch on its side away from the arena.
    /// </summary>
    private const string LockingHatchOutsidePath = "Hero Detector Top";

    /// <summary>
    /// The FSM of that hatch that locks and frees its levers.
    /// </summary>
    private const string LockingHatchFsm = "BG Control";

    /// <summary>
    /// The state of that FSM with the levers locked, in which it waits for the end of the battle.
    /// </summary>
    private const string LockingHatchLockedState = "Wait for open";

    /// <summary>
    /// The hatches that lock themselves, by the arena they lead into, or null for an arena without one. Looked up once
    /// for each arena of the scene.
    /// </summary>
    private readonly Dictionary<BattleScene, LockingHatch?> _lockingHatches = new();

    /// <summary>
    /// Gives the levers of the hatch into an arena back to the local player while they are outside of it, if the hatch
    /// locked itself behind them before.
    /// </summary>
    private void UnlockHatchForPlayerOutside(BattleScene battleScene) {
        if (!_lockingHatches.TryGetValue(battleScene, out var hatch)) {
            hatch = FindLockingHatch(battleScene);
            _lockingHatches[battleScene] = hatch;
        }

        if (hatch == null || hatch.Fsm == null || hatch.Outside == null ||
            hatch.Fsm.ActiveStateName != LockingHatchLockedState || !hatch.Outside.IsInside) {
            return;
        }

        Logger.Info(
            $"The local player is outside the hatch '{hatch.Fsm.name}', which locked itself behind them before, so its " +
            "levers work for them again"
        );
        hatch.Fsm.SendEvent(OpenGatesEvent);
    }

    /// <summary>
    /// Finds the hatch into an arena that locks itself, if it has one.
    /// </summary>
    private static LockingHatch? FindLockingHatch(BattleScene battleScene) {
        if (battleScene.gameObject.scene.name != LockingHatchScene ||
            battleScene.transform.Find(LockingHatchPath) is not { } hatch) {
            return null;
        }

        var fsm = hatch.gameObject.LocateMyFSM(LockingHatchFsm);
        var outside = hatch.Find(LockingHatchOutsidePath)?.GetComponent<TrackTriggerObjects>();
        if (fsm == null || outside == null) {
            Logger.Warn($"Could not find what locks the hatch '{hatch.name}' of arena '{battleScene.name}'");
            return null;
        }

        return new LockingHatch(fsm, outside);
    }

    /// <summary>
    /// A hatch into an arena that locks itself behind a player who comes in.
    /// </summary>
    private class LockingHatch(PlayMakerFSM fsm, TrackTriggerObjects outside) {
        /// <summary>
        /// The FSM of the hatch that locks and frees its levers.
        /// </summary>
        public readonly PlayMakerFSM Fsm = fsm;

        /// <summary>
        /// The detector of the hatch on its side away from the arena.
        /// </summary>
        public readonly TrackTriggerObjects Outside = outside;
    }
}
