using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace SSMP.Game.Client;

/// <summary>
/// What a creature reads of the player it goes after through the player's own HeroController, where that player is the
/// figure of a player of another game, which has none: whether they stand on the ground, which the game's own check of
/// the player's box finds of the figure's box (see <see cref="IsOnGround"/>). The charger of the docks, which climbs
/// onto the platform above it as the player it goes after stands there, read it of nothing as it went after the figure
/// of another player who stood above it, and stopped there every frame: in the game that ran it, and, as it runs in
/// every game, in every other.
/// </summary>
internal partial class GamePatcher {
    /// <summary>
    /// The HeroController that the charger's clamber check took of the player it goes after as it started, none for a
    /// figure (see <see cref="OnDockClamberCheckUpdate"/>).
    /// </summary>
    private static readonly FieldInfo? DockClamberHeroField =
        typeof(DockClamberCheck).GetField("hc", InstanceNonPublicFlags);

    /// <summary>
    /// The game's check of whether something stands on the ground, for each figure of a player of another game that a
    /// creature asked it of (see <see cref="IsOnGround"/>).
    /// </summary>
    private static readonly ConditionalWeakTable<GameObject, TouchGroundResult> FigureGround = new();

    /// <summary>
    /// The hook on the clamber check of the charger of the docks.
    /// </summary>
    private Hook? _dockClamberCheckUpdateHook;

    /// <summary>
    /// Registers what reads a figure of a player of another game as the game reads its own player.
    /// </summary>
    private void RegisterFigureReadHooks() {
        _dockClamberCheckUpdateHook = TryCreateHook(
            typeof(DockClamberCheck), "OnUpdate",
            new Action<Action<DockClamberCheck>, DockClamberCheck>(OnDockClamberCheckUpdate)
        );
    }

    /// <summary>
    /// Disposes what reads a figure of a player of another game as the game reads its own player.
    /// </summary>
    private void DisposeFigureReadHooks() {
        _dockClamberCheckUpdateHook?.Dispose();
        _dockClamberCheckUpdateHook = null;
    }

    /// <summary>
    /// Whether a player stands on the ground: this game's player as the game has it, and the figure of a player of
    /// another game by the game's own check of the player's box (TouchGroundResult), made on the figure's box, which
    /// has the size and the offset of the player's (see PlayerManager).
    /// </summary>
    internal static bool IsOnGround(GameObject player) {
        if (player.GetComponent<HeroController>() is { } hero) {
            return hero.cState.onGround;
        }

        if (player.GetComponent<Collider2D>() is not { } box) {
            return false;
        }

        var ground = FigureGround.GetValue(player, _ => new TouchGroundResult());
        ground.Update(box, false);
        return ground.IsTouchingGround;
    }

    /// <summary>
    /// The clamber check of the charger of the docks, for the figure of a player of another game, which it found no
    /// HeroController on as it started: the game's check (DockClamberCheck.OnUpdate), with whether the player stands on
    /// the ground found by <see cref="IsOnGround"/>. It climbs as the player it goes after stands on the ground above
    /// it and the rays from its two sides find the same platform over it.
    /// </summary>
    private static void OnDockClamberCheckUpdate(Action<DockClamberCheck> orig, DockClamberCheck self) {
        if (DockClamberHeroField?.GetValue(self) != null) {
            orig(self);
            return;
        }

        if (self.noClamber.Value || self.hero.Value is not { } player || self.clamberRayL.Value is not { } left ||
            self.clamberRayR.Value is not { } right ||
            player.transform.position.y <= self.Owner.transform.position.y || !IsOnGround(player)) {
            return;
        }

        var terrain = LayerMask.GetMask("Terrain");
        var leftHit = Helper.Raycast2D(left.transform.position, Vector2.up, 10f, terrain);
        var rightHit = Helper.Raycast2D(right.transform.position, Vector2.up, 10f, terrain);
        if (leftHit.collider != null && rightHit.collider != null && leftHit.point.y == rightHit.point.y &&
            leftHit.collider.gameObject.CompareTag("Platform") && rightHit.collider.gameObject.CompareTag("Platform")) {
            FSMUtility.SendEventToGameObject(self.Owner, self.clamberEvent, false);
        }
    }
}
