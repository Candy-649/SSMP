using System;
using System.Collections.Generic;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The heat lamps of the peaks (<see cref="WeaverHeatLamp"/>), which break after a few hits and are never saved: the
/// game brings a broken one back only when its room loads again. In a checked two-player save both games keep a lamp as
/// one, since the hits of either player count in both (see CoopHits), and the room loading again for one of the players
/// brings the lamps back for both: when the partner leaves the room, or comes into it, the lamps here come back too
/// (USER 10-26, "维持游戏本来的规则吧，就做同步，然后一个人出房间就能复原两个人的"). The lamp of a player who left and
/// came back was whole again in their game, while the one of the player who stayed was still broken.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The name of the burst that the break of a lamp takes out of its warmth to play.
    /// </summary>
    private const string HeatLampBurstName = "Burst Effect";

    /// <summary>
    /// Brings the heat lamps of the room back as the room has them when it loads, for a member who came into the room
    /// or left it: the room loads anew for them. Also before the check with them is done, as for a member who comes
    /// back into the save, whose game loaded the room before the check.
    /// </summary>
    /// <param name="player">The player who came in or left.</param>
    /// <param name="cameIn">Whether they came in rather than left, for the log.</param>
    private void RestoreHeatLamps(ClientPlayerData player, bool cameIn) {
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker)) {
            return;
        }

        List<string>? restored = null;
        foreach (var lamp in Object.FindObjectsByType<WeaverHeatLamp>(FindObjectsSortMode.None)) {
            var name = lamp != null ? lamp.name : "?";
            try {
                if (lamp == null || !lamp.TryGetComponent<Breakable>(out var breakable)) {
                    continue;
                }

                var broken = breakable.isBroken;
                if (!broken && breakable.hitsLeft >= breakable.hitsToBreak) {
                    continue;
                }

                var was = broken ? "broken" : $"{breakable.hitsLeft} of {breakable.hitsToBreak} hits left";
                breakable.hitsLeft = breakable.hitsToBreak;
                if (broken) {
                    RestoreBrokenHeatLamp(lamp, breakable);
                }

                (restored ??= []).Add($"'{ScenePath.Get(lamp.transform)}' ({was})");
            } catch (Exception e) {
                Logger.Warn($"Could not bring back the heat lamp '{name}': {e.Message}");
            }
        }

        if (restored != null) {
            Logger.Info(
                $"[Lamps] {player.Username} {(cameIn ? "came into" : "left")} the room, which loads anew for them, so " +
                $"its heat lamps are whole here too: {string.Join(", ", restored)}"
            );
        }
    }

    /// <summary>
    /// Puts a broken heat lamp back the way its room loads it: whole and put out, with its warmth looking for the player
    /// afresh, which lights it up again for a player standing in it.
    /// </summary>
    private static void RestoreBrokenHeatLamp(WeaverHeatLamp lamp, Breakable breakable) {
        // Off while the lamp is put out, as the warmth of a room that loads looks for the player only after that
        var trigger = lamp.appearTrigger;
        var triggerOn = trigger != null && trigger.enabled;
        if (triggerOn) {
            trigger!.enabled = false;
        }

        try {
            breakable.isBroken = false;
            breakable.SetStaticPartsActivation(false);

            // What the break flung out is hidden again, as the room has it, so that the next break flings it anew
            HideHeatLampParts(breakable.flingDrops);
            HideHeatLampParts(breakable.debrisParts);
            HideHeatLampParts(breakable.debrisPartsAlt);

            // And the burst that the break took out of the warmth to play goes back into it, off
            var burst = lamp.transform.Find(HeatLampBurstName);
            if (burst != null && lamp.warmthRegion != null) {
                burst.SetParent(lamp.warmthRegion.transform, true);
                burst.gameObject.SetActive(false);
            }

            // The snow on it, which melted the first time it lit up, is back until it lights up again
            if (lamp.snowFadeOffRoutine != null) {
                lamp.StopCoroutine(lamp.snowFadeOffRoutine);
                lamp.snowFadeOffRoutine = null;
            }

            if (lamp.snowFader != null) {
                lamp.snowFader.AlphaSelf = 1f;
            }

            lamp.Start();
        } finally {
            if (triggerOn) {
                trigger!.enabled = true;
            }
        }
    }

    /// <summary>
    /// Switches off the parts of a heat lamp that its break switched on.
    /// </summary>
    private static void HideHeatLampParts(IEnumerable<GameObject?>? parts) {
        if (parts == null) {
            return;
        }

        foreach (var part in parts) {
            if (part != null) {
                part.SetActive(false);
            }
        }
    }
}
