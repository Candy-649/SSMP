using System;
using MonoMod.RuntimeDetour;
using SSMP.Internals;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Being caught and taken to the prison, in a checked two-player save: it happens to both players. The user chose this
/// on 2026-09-24, so that the escape and all that comes after it is something the two of them go through together.
///
/// Only one of them can ever be caught. What catches a player is a creature, and creatures only act in the game of the
/// player who runs the room, where they look for that game's own player and never see the other one. So the game of
/// the player who was caught tells the other game, and that game does to its own player what the capture did: it takes
/// away what the prison takes away, and sends them in through the same door. The capture itself is one call of the
/// game's own, made both where it happens and here, which is why the hook sits on that call and not on the creature.
///
/// A player who can't be moved right away - dead, on a bench, being moved already, riding a lift, in the pause menu, or
/// held by something the game is doing to them - is taken as soon as they can be. A player who is in the prison
/// already is left where they are.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The room that a caught player is taken to.
    /// </summary>
    private const string PrisonSceneName = "Slab_03";

    /// <summary>
    /// The door that a caught player comes in through, which plays their waking up in the cell.
    /// </summary>
    private const string PrisonDoorName = "door_slabCaged";

    /// <summary>
    /// Hook on the call that takes away what the prison takes from a caught player.
    /// </summary>
    private Hook? _captureHook;

    /// <summary>
    /// Whether the capture is being made here for the partner, in which case it is not sent back to them.
    /// </summary>
    private bool _capturingForPartner;

    /// <summary>
    /// The name of the partner who was caught, while the local player still has to be taken to the prison after them.
    /// </summary>
    private string? _pendingCaptureBy;

    /// <summary>
    /// Watches for the local player being caught.
    /// </summary>
    private void RegisterPrisonCaptureHooks() {
        _captureHook = CreateHook(
            typeof(HeroSlabCapture).GetMethod(nameof(HeroSlabCapture.ApplyCaptured), Type.EmptyTypes),
            new Action<Action>(OnApplyCaptured)
        );
    }

    /// <summary>
    /// Tells the partner that the local player was caught, so that their game takes them to the prison as well.
    /// </summary>
    /// <param name="orig">The original method.</param>
    private void OnApplyCaptured(Action orig) {
        orig();

        if (_capturingForPartner || _checkedMembers.Count == 0) {
            return;
        }

        SendToMembers(new CoopSaveUpdate { Kind = CoopSaveUpdateKind.PrisonCapture });
        Logger.Info($"Was caught and taken to the prison, so {GetCheckedNames()} are taken there too");
    }

    /// <summary>
    /// The partner was caught, so the local player goes to the prison after them.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    private void OnPrisonCapture(ClientPlayerData player) {
        if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker) ||
            !_checkedMembers.Contains(player.Id) || IsInPrisonClothes()) {
            return;
        }

        _pendingCaptureBy = player.Username;
        Chat(Lang.Pick(
            $"{player.Username} was caught, and you are taken along.",
            $"{player.Username} 被抓走了，你也会被一起关进去。"
        ));
    }

    /// <summary>
    /// Takes the local player to the prison once they can be moved, after the partner was caught.
    /// </summary>
    private void UpdatePrisonCapture(HeroController hero) {
        if (_pendingCaptureBy is not { } username) {
            return;
        }

        try {
            // Caught in their own game meanwhile, which already did all of this
            if (IsInPrisonClothes()) {
                _pendingCaptureBy = null;
                return;
            }

            // Not out of shared dialogue either, which the move of a delivery takes over and this one doesn't
            var gameManager = global::GameManager.instance;
            if (gameManager == null || PlayerData.instance.atBench || hero.cState.dead || hero.cState.hazardDeath ||
                hero.cState.hazardRespawning || !CanMoveHeroNow(gameManager, hero) ||
                IsReadingSharedDialogue?.Invoke() == true) {
                return;
            }

            _pendingCaptureBy = null;
            _capturingForPartner = true;
            try {
                HeroSlabCapture.ApplyCaptured();
            } finally {
                _capturingForPartner = false;
            }

            var moved = BeginMove(hero, new global::GameManager.SceneLoadInfo {
                SceneName = PrisonSceneName,
                EntryGateName = PrisonDoorName,
                WaitForSceneTransitionCameraFade = true
            });
            Logger.Info(
                moved
                    ? $"Taken to the prison after {username}"
                    : $"The game didn't start the move to the prison after {username}"
            );
        } catch (Exception e) {
            _pendingCaptureBy = null;
            Logger.Error($"Could not take the local player to the prison after {username}:\n{e}");
        }
    }

    /// <summary>
    /// Forgets a capture that the local player was still to follow, for a new session.
    /// </summary>
    private void ResetPrisonCapture() {
        _pendingCaptureBy = null;
    }

    /// <summary>
    /// Whether the local player wears what the prison leaves them in, which they do from being caught until they take
    /// back what it took.
    /// </summary>
    private static bool IsInPrisonClothes() {
        return CrestTypeExt.FromInternal(PlayerData.instance.CurrentCrestID) == CrestType.Cloakless;
    }
}
