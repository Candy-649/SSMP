using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Keeps the creatures of a room that another game runs from hurting the local player for a moment once things come
/// from the server again after a gap (USER 11-05: back from a link that broke, "突然被怪闪来闪去打死了"). In the gap
/// the copies stood where they were last told of while the game that runs them went on; what comes then puts each
/// where it has got to at once, and the moves held up on the way play all together, onto a player who saw none of it
/// coming. The harm of the room - spikes, acid, steam - still hurts: it did not move meanwhile.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How long the creatures cannot hurt the local player once things come again after a gap, in seconds. On 11-04 the
    /// last of what had been held up on the way came 0.4 s after the first packet.
    /// </summary>
    public const float KeptOffAfterGap = 1.5f;

    /// <summary>
    /// Until when the creatures cannot hurt the local player (see <see cref="KeepCreaturesOff"/>), by the clock that
    /// runs whatever the speed of the game.
    /// </summary>
    private static float _creaturesKeptOffUntil;

    /// <summary>
    /// How long nothing had come from the server when the creatures were last kept off, in seconds, for the log.
    /// </summary>
    private static double _creaturesKeptOffAfter;

    /// <summary>
    /// Keeps the creatures from hurting the local player for <see cref="KeptOffAfterGap"/> seconds from now, as things
    /// come from the server again after a gap in a room that another game runs.
    /// </summary>
    /// <param name="gap">How long nothing came, in seconds.</param>
    public void KeepCreaturesOff(double gap) {
        _creaturesKeptOffUntil = Time.unscaledTime + KeptOffAfterGap;
        _creaturesKeptOffAfter = gap;
        Logger.Info(
            $"[Hurts] Nothing came from the server for {gap:F1}s, so the creatures of this room cannot hurt the " +
            $"player for the next {KeptOffAfterGap:F1}s"
        );
    }

    /// <summary>
    /// Whether a hurt of a creature is kept off the local player, as it is for a moment once things come from the
    /// server again after a gap (see <see cref="KeepCreaturesOff"/>). Each one kept off is said.
    /// </summary>
    /// <param name="go">What hurt the player.</param>
    /// <param name="damageAmount">How much it hurt.</param>
    /// <param name="hazardType">What kind of harm it is: a creature's is ENEMY, its thrown things and blasts too.</param>
    private static bool KeepsCreatureHurtOff(GameObject go, int damageAmount, GlobalEnums.HazardType hazardType) {
        if (hazardType != GlobalEnums.HazardType.ENEMY || Time.unscaledTime >= _creaturesKeptOffUntil) {
            return false;
        }

        Logger.Info(
            $"[Hurts] Kept a hurt off the player: {damageAmount} from '{(go == null ? "?" : go.name)}', " +
            $"{KeptOffAfterGap - (_creaturesKeptOffUntil - Time.unscaledTime):F2}s after things came from the server " +
            $"again after {_creaturesKeptOffAfter:F1}s without"
        );
        return true;
    }
}
