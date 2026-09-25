using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using UnityEngine.SceneManagement;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The end of a lava death, told to the room of the partner as well.
///
/// When a player comes back out of the lava, their game tells the room, and some rooms put themselves back together for
/// them: every broken floor of a boss room comes back up at once, so that there is ground to come back to. The room of
/// the other game was never told. That player saw the partner come back standing on floor that had not come back in
/// their own game, and the floors of the two games stayed apart until each broke and came back on its own again. Seen
/// in play, where both players fell in many times in one fight.
///
/// Not in a room whose lava rises: the rising lava hears this too, and has a two-player rule of its own
/// (<see cref="LavaChase"/>).
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What the game tells the room when a player has come back out of the lava.
    /// </summary>
    private const string LavaDeathEndEvent = "LAVA DEATH END";

    /// <summary>
    /// The hook that notices the local player coming back out of the lava.
    /// </summary>
    private Hook? _lavaDeathEndHook;

    /// <summary>
    /// Whether the end of the partner's lava death is being passed on to the local room, which is not told back.
    /// </summary>
    private bool _passingOnPartnersLavaDeathEnd;

    /// <summary>
    /// Watches for the local player coming back out of the lava.
    /// </summary>
    private void RegisterLavaDeathHooks() {
        try {
            var method = typeof(EventRegister).GetMethod(
                nameof(EventRegister.SendEvent),
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                [typeof(string), typeof(GameObject)],
                null
            );

            if (method == null) {
                Logger.Error("Could not find how the room is told of a lava death, so its floors stay apart");
                return;
            }

            _lavaDeathEndHook = new Hook(
                method,
                (Action<Action<string, GameObject>, string, GameObject>) OnRoomToldOfLavaDeathEnd
            );
        } catch (Exception e) {
            Logger.Error($"Could not watch for the end of lava deaths:\n{e}");
        }
    }

    /// <summary>
    /// Tells the partner when the room of the local player hears that the local player came back out of the lava.
    /// </summary>
    private void OnRoomToldOfLavaDeathEnd(Action<string, GameObject> orig, string eventName, GameObject excludeTarget) {
        orig(eventName, excludeTarget);

        if (eventName != LavaDeathEndEvent || _passingOnPartnersLavaDeathEnd) {
            return;
        }

        try {
            if (_lavaChase != null || GetCheckedPartner() is not { IsInLocalScene: true } partner) {
                return;
            }

            Send(new CoopSaveUpdate {
                TargetId = partner.Id,
                Kind = CoopSaveUpdateKind.LavaDeathEnd,
                Scene = SceneManager.GetActiveScene().name
            });
            Logger.Info("The local player came back out of the lava, so the room of the partner hears it too");
        } catch (Exception e) {
            Logger.Warn($"Could not tell the partner about the end of a lava death: {e.Message}");
        }
    }

    /// <summary>
    /// The partner came back out of the lava in the room the local player is in, so this room hears it as well.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update, which names the room.</param>
    private void OnPartnerLavaDeathEnd(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCheckedPartner()?.Id != player.Id || _lavaChase != null ||
            update.Scene != SceneManager.GetActiveScene().name) {
            return;
        }

        Logger.Info($"{player.Username} came back out of the lava, so this room hears it as well");
        _passingOnPartnersLavaDeathEnd = true;
        try {
            EventRegister.SendEvent(LavaDeathEndEvent, null);
        } finally {
            _passingOnPartnersLavaDeathEnd = false;
        }
    }
}
