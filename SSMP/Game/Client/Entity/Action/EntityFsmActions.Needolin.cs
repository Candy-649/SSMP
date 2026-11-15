using System.Collections;
using System.Reflection;
using HutongGames.PlayMaker.Actions;
using SSMP.Networking.Packet.Data;
using SSMP.Util;

namespace SSMP.Game.Client.Entity.Action;

internal static partial class EntityFsmActions {
    #region EnemySingControl

    /// <summary>
    /// Reflected private field of <see cref="EnemySingControl"/> that stops it from counting down its sing duration.
    /// </summary>
    private static readonly FieldInfo? EnemySingControlSentEndEventField =
        typeof(EnemySingControl).GetField(
            "sentEndEvent",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
        );

    /// <summary>Builds network data from the FSM action.</summary>
    private static bool GetNetworkDataFromAction(EntityNetworkData data, EnemySingControl action) {
        return true;
    }

    /// <summary>Applies network data to the FSM action.</summary>
    private static void ApplyNetworkDataFromAction(EntityNetworkData? data, EnemySingControl action) {
        // Nothing sings when the entity is initialized; the host sends this action again once it starts singing
        if (data == null) {
            return;
        }

        // Run the action on the client copy for the sing audio, the thread effect and the needolin text. The host
        // decides when the song ends, so mark the end event as sent to keep the client copy from counting down.
        action.OnEnter();
        EnemySingControlSentEndEventField?.SetValue(action, true);

        var exited = false;

        void ExitAction() {
            if (exited) {
                return;
            }

            exited = true;
            action.OnExit();
        }

        new ActionInState {
            Fsm = action.Fsm,
            StateName = action.State.Name,
            Coroutine = MonoBehaviourUtil.Instance.StartCoroutine(FollowSingEffects(action, ExitAction)),
            ExitAction = ExitAction
        }.Register();
    }

    /// <summary>
    /// Keeps the thread effect of a replayed <see cref="EnemySingControl"/> on its enemy every frame, and ends the
    /// song locally if the enemy disappears without a state change.
    /// </summary>
    /// <param name="action">The replayed action.</param>
    /// <param name="exitAction">The callback that ends the song on the client copy.</param>
    private static IEnumerator FollowSingEffects(EnemySingControl action, System.Action exitAction) {
        while (true) {
            yield return null;

            var enemy = action.Fsm?.GetOwnerDefaultTarget(action.enemyGameObject);
            if (enemy == null || !enemy.activeInHierarchy) {
                exitAction();
                yield break;
            }

            action.OnUpdate();
        }
    }

    #endregion
}
