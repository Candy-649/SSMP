using System.Collections.Generic;
using System.Linq;

namespace SSMP.Game.Client;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Steps of the story that each game takes by itself and that end something for every save: they wait until every
/// player got to them, and are then taken by all together, as the talk before a fight waits (see TryHoldFightGate).
/// Taken in one game first, such a step ended it for the others before their players got there: the flea festival's
/// ending played for whoever talked to its host first, and the flag of its end, which reaches every save, took the
/// ending and its reward away from the others. Now the talk with the host that leads into the ending waits until every
/// player read to its end, as a wish does, and the end of the festival after the reward until every player took their
/// own (USER 11-17 "我觉得和主持人对话可以像祈愿一样等人，然后领苍白油后换地方可以改成每个人都拿了再换"). Whoever gets
/// there first can move about meanwhile, and is told that the others are waited for.
/// </summary>
internal partial class BossRoomCoop {
    /// <summary>
    /// The steps that wait, each by the state that it goes into: every way into the state waits.
    /// </summary>
    private static readonly SharedStep[] SharedSteps = [
        new(
            "aqueduct_05_festival", "Caravan_States/Flea Festival/Flea Game - Juggling/Flea Games Host NPC", "Control",
            "Outro Control"
        ),
        new("aqueduct_05_festival", "Caravan_States", "Conditions", "Festival Outro Finish")
    ];

    /// <summary>
    /// The names of the FSMs of <see cref="SharedSteps"/>, which most FSMs are told apart by before anything else.
    /// </summary>
    private static readonly HashSet<string> SharedStepFsmNames = SharedSteps.Select(step => step.FsmName).ToHashSet();

    /// <summary>
    /// The step of the story that an FSM takes that waits for every player (see <see cref="SharedSteps"/>), or null.
    /// </summary>
    private static SharedStep? SharedStepOf(Fsm fsm) {
        if (!SharedStepFsmNames.Contains(fsm.Name) || fsm.GameObject is not { } gameObject || gameObject == null) {
            return null;
        }

        var sceneName = gameObject.scene.name;
        foreach (var step in SharedSteps) {
            if (step.FsmName == fsm.Name && step.SceneName == sceneName && step.Path == GetPath(fsm)) {
                return step;
            }
        }

        return null;
    }

    /// <summary>
    /// A step of the story that waits for every player (see <see cref="SharedSteps"/>).
    /// </summary>
    /// <param name="SceneName">The scene that it is in.</param>
    /// <param name="Path">The path in the scene of the object of its FSM.</param>
    /// <param name="FsmName">The name of its FSM.</param>
    /// <param name="StateName">The state that the step goes into.</param>
    private record SharedStep(string SceneName, string Path, string FsmName, string StateName);
}
