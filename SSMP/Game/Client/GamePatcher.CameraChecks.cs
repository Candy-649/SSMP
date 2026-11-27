using System;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace SSMP.Game.Client;

/// <summary>
/// Whether a creature that this game runs for every game is on screen. Some creatures do what they do only while the
/// player can see them (CheckOutOfCamera): mechanical ones sleep until they come on screen and fall asleep again once
/// they are well off it, others stop moving, fall silent or vanish while off it. The game asks the camera of its own
/// player, so the creatures that the scene host's game runs woke and slept by the scene host's screen alone: one near
/// the partner but off the scene host's screen slept on while the partner walked up to it and struck it. That is one
/// of the ways a creature ignored the partner (USER 10-17, "我走到它的攻击范围了都不动，打它也不动", "这样的情况不止这
/// 个虫子会有"). For such a creature, the screen of any player in the room counts, each partner's screen worked out
/// from where their figure is (see <see cref="EstimateCameraPosition"/>); what is on none of them is left to the
/// game's own check. So goes how far such a creature is from the camera (GetDistance): the lily pads of the clover
/// woods drift, and feel a player land on them, only while the camera is near, and those of the scene host's game,
/// whose steps every game takes, stood still as the partner landed on one far from the scene host.
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// How far from the middle of the screen across and up something still counts as on it, before the margin of the
    /// check, as the game measures it (CheckOutOfCamera.DoCheck).
    /// </summary>
    private const float CameraCheckHalfWidth = 15f;

    /// <inheritdoc cref="CameraCheckHalfWidth"/>
    private const float CameraCheckHalfHeight = 9f;

    /// <summary>
    /// The hook on the check of whether something is on screen.
    /// </summary>
    private Hook? _checkOutOfCameraHook;

    /// <summary>
    /// The hook on how far something is from something else.
    /// </summary>
    private Hook? _getDistanceHook;

    /// <summary>
    /// Registers the hook on the check of whether something is on screen.
    /// </summary>
    private void RegisterCameraCheckHooks() {
        _checkOutOfCameraHook = TryCreateHook(
            typeof(CheckOutOfCamera), "DoCheck", OnCheckOutOfCameraDoCheck, InstanceNonPublicFlags | InstancePublicFlags
        );
        _getDistanceHook = TryCreateHook(
            typeof(GetDistance), "DoGetDistance", OnGetDistance, InstanceNonPublicFlags | InstancePublicFlags
        );
    }

    /// <summary>
    /// Disposes the hook on the check of whether something is on screen.
    /// </summary>
    private void DisposeCameraCheckHooks() {
        _checkOutOfCameraHook?.Dispose();
        _checkOutOfCameraHook = null;
        _getDistanceHook?.Dispose();
        _getDistanceHook = null;
    }

    /// <summary>
    /// Has a creature that this game runs for every game count as on screen while it is on the screen of a partner in
    /// the room, doing what the game does for one on its own screen; otherwise the game checks its own screen.
    /// </summary>
    /// <param name="orig">The game's own check.</param>
    /// <param name="self">The action.</param>
    private static void OnCheckOutOfCameraDoCheck(Action<CheckOutOfCamera> orig, CheckOutOfCamera self) {
        if (!IsOnAPartnersScreen(self)) {
            orig(self);
            return;
        }

        if (self.insideEvent != null) {
            self.Fsm.Event(self.insideEvent);
        }

        self.outsideBool?.Value = false;
        self.insideBool?.Value = true;
    }

    /// <summary>
    /// Has how far a creature that this game runs for every game is from the camera be how far it is from the nearest
    /// screen of a player in the room, each partner's worked out from where their figure is, at the depth of the
    /// camera, which the distance counts.
    /// </summary>
    /// <param name="orig">The game's own look at the distance.</param>
    /// <param name="self">The action.</param>
    private static void OnGetDistance(Action<GetDistance> orig, GetDistance self) {
        orig(self);
        _mainCameraVariable ??= FsmVariables.GlobalVariables.FindFsmGameObject("MainCamera");
        var camera = self.target.Value;
        if (camera == null || camera != _mainCameraVariable?.Value || self.storeResult == null ||
            self.Fsm?.FsmComponent is not { } fsm || !Entity.Entity.RunsHere(fsm) ||
            self.Fsm.GetOwnerDefaultTarget(self.gameObject) is not { } measured || measured == null) {
            return;
        }

        var hero = HeroController.instance;
        var position = measured.transform.position;
        var nearest = self.storeResult.Value;
        foreach (var player in PlayerTargetRegistry.GetTrackedPlayers()) {
            if (player == null || hero != null && player == hero.gameObject) {
                continue;
            }

            var screen = EstimateCameraPosition(player.transform.position, camera.transform.position.z);
            nearest = Mathf.Min(nearest, Vector3.Distance(position, screen));
        }

        self.storeResult.Value = nearest;
    }

    /// <summary>
    /// Whether what a check of a creature that this game runs for every game asks about is on the screen of another
    /// player in the room, standing and not between two rooms (PlayerTargetRegistry), with the margin of the check.
    /// </summary>
    private static bool IsOnAPartnersScreen(CheckOutOfCamera check) {
        if (check.Fsm?.FsmComponent is not { } fsm || !Entity.Entity.RunsHere(fsm) ||
            check.Fsm.GetOwnerDefaultTarget(check.gameObject) is not { } target || target == null) {
            return false;
        }

        var hero = HeroController.instance;
        var margin = check.margin?.Value ?? 0f;
        var position = target.transform.position;
        foreach (var player in PlayerTargetRegistry.GetTrackedPlayers()) {
            if (player == null || hero != null && player == hero.gameObject) {
                continue;
            }

            var screen = EstimateCameraPosition(player.transform.position, 0f);
            if (Mathf.Abs(position.x - screen.x) <= CameraCheckHalfWidth + margin &&
                Mathf.Abs(position.y - screen.y) <= CameraCheckHalfHeight + margin) {
                return true;
            }
        }

        return false;
    }
}
