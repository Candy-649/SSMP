using SSMP.Game.Client.Entity.Action;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// How the copy of a creature turns in the other game: by itself in a move that it plays by itself, and where the
/// creature's turn follows from a rule set once or from the way it goes, as the creature turns here, and otherwise by
/// the angles that this game sends (RotationComponent).
/// </summary>
internal partial class Entity {
    /// <summary>
    /// Whether the copy of the creature in the other game turns itself now: an FSM of the creature is in a move that
    /// the copy plays by itself (see <see cref="IsInAMoveTheCopyPlays"/>), or an action that turns the creature here,
    /// whose replay went there, still runs (see EntityFsmActions.TurnsTheCopysRoot). Nothing of the turn is sent
    /// meanwhile (see EntityFsmActions.CopyMakesTheTurn).
    /// </summary>
    private bool CopyTurnsItself() {
        for (var fsmIndex = 0; fsmIndex < _fsms.Host.Count; fsmIndex++) {
            var fsm = _fsms.Host[fsmIndex];
            if (IsInAMoveTheCopyPlays(fsm)) {
                return true;
            }

            foreach (var hooked in _fsmSnapshots[fsmIndex].Replayed) {
                if (StillRunsThere(fsm, hooked) && EntityFsmActions.TurnsTheCopysRoot(hooked.Action)) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Starts on the copy the turns that it makes by itself in a state of an FSM of the creature told with the state of
    /// the room this game walks into (see EntityFsmActions.StartTurnHeardLate).
    /// </summary>
    /// <param name="fsm">The FSM of the copy.</param>
    /// <param name="stateName">The state.</param>
    private static void StartTheTurnsOf(PlayMakerFSM fsm, string stateName) {
        // The FSM here can be a stand-in with fewer states than the scene host's (WaitForTheFsmToBeMade)
        if (fsm.Fsm.GetState(stateName) is not { } state) {
            return;
        }

        foreach (var action in state.Actions) {
            if (action.Enabled && EntityFsmActions.CopyMakesTheTurn(action)) {
                EntityFsmActions.StartTurnHeardLate(action);
            }
        }
    }
}
