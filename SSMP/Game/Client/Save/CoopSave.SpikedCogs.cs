using System;
using System.Collections.Generic;
using MonoMod.RuntimeDetour;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The spiked cogs that catch a player who touches them (CogMultiHitter: 290 in the game's rooms, 30 of them the cogs
/// that run back and forth along their rails). A cog holds the player for a moment, a cog on a rail stopping where it
/// is, then hurts them, and lets go once the game has put them back at the last safe place. A player whose last health
/// it took went down instead of being put back, so their cog stood where it was for the whole wait and after it (USER
/// 10-17, "死在那个轮子上了，然后在死亡视角那个轮子就会一直卡在那里"), and once pulled back up they stood in the cogs where
/// they fell ("复活掉在齿轮地上不会回到安全区域…而是直接站在齿轮里了"). A cog also caught them again where they lay: being
/// caught holds a player in the game's wounded pose for up to four seconds, which the hurt that follows ends, and with
/// that hurt kept off a player lying down (OnTakeDamage), the pose ran out, and its end showed their body standing on
/// the cog ("而且角色也没有正确消失"). Cogs now go the way of the grinders of the machines (see CoopSave.PacedMachines,
/// USER 10-06): a cog that holds the local player as they go down lets go of them as it would once they were put back,
/// they stand up at the last safe place, and no cog catches a player lying down.
///
/// A cog on a rail stopped only in the game of the player it caught, and ran on in the others, away from the figure of
/// that player, which stood caught by nothing. Like a grinder, which stops in both games whoever it catches (USER
/// 10-06, "两个人不管谁死都让两边都停"), it now stops in every game for as long as it holds a player in theirs.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The hook on something touching a cog.
    /// </summary>
    private Hook? _cogTouchHook;

    /// <summary>
    /// The cogs that caught the local player and haven't let go of them yet.
    /// </summary>
    private readonly HashSet<CogMultiHitter> _caughtCogs = [];

    /// <summary>
    /// How many times a cog touched the player since they last went down, so that only the first of each wait is
    /// written down.
    /// </summary>
    private static int _cogTouchesOfLyingPlayer;

    /// <summary>
    /// The cogs on rails that stopped as they caught the local player, which the members in the room were told of and
    /// are told of again once they run on.
    /// </summary>
    private readonly HashSet<CogMultiHitter> _cogsStoppedForLocal = [];

    /// <summary>
    /// The cogs on rails that hold members in their games, with the members they hold, which stand still here too.
    /// </summary>
    private readonly Dictionary<CogMultiHitter, HashSet<ushort>> _cogsStoppedForMembers = new();

    /// <summary>
    /// Registers the hook on something touching a cog.
    /// </summary>
    private void RegisterSpikedCogHooks() {
        _cogTouchHook = CreateHook(
            typeof(CogMultiHitter).GetMethod("OnTriggerEnter2D", InstanceFlags, null, [typeof(Collider2D)], null),
            new Action<Action<CogMultiHitter, Collider2D>, CogMultiHitter, Collider2D>(OnCogTouched)
        );
    }

    /// <summary>
    /// Keeps a cog from catching the local player while they lie down, and notes a cog that caught them. A cog catches
    /// nothing but the local player, so while they lie down, a cog is left alone whatever touched it.
    /// </summary>
    private void OnCogTouched(Action<CogMultiHitter, Collider2D> orig, CogMultiHitter self, Collider2D collision) {
        var hero = HeroController.instance;
        if (hero != null && PlayerTargetRegistry.IsPlayerDown(hero.gameObject)) {
            // Going down switches the hit box of the player off, and a cog only feels it switched on, so this also says
            // that something switched it back on
            if (collision != null && collision.GetComponentInParent<HeroController>() == hero &&
                _cogTouchesOfLyingPlayer++ == 0) {
                Logger.Info($"The cog '{PathOf(self.transform)}' touched the player lying down and did not catch them");
            }

            return;
        }

        orig(self, collision);

        if (hero != null && self.damagingHc == hero) {
            _caughtCogs.Add(self);

            // A cog on a rail stopped where it caught them, and stops in the games of the members as well
            if (self.cogAnimator != null && self.cogAnimator.FreezePosition && _cogsStoppedForLocal.Add(self)) {
                TellMembersAboutCog(self, true);
            }
        }
    }

    /// <summary>
    /// Has the cogs that hold the local player let go of them once they are down, the way a cog lets go once the game
    /// has put the player back at the last safe place (CogMultiHitter.OnHeroHazardRespawn): a cog on a rail goes on,
    /// and the player stands up at that place rather than where the cog caught them.
    /// </summary>
    private void UpdateCaughtCogs() {
        UpdateStoppedCogs();
        if (_caughtCogs.Count == 0) {
            return;
        }

        // Gone, or let go of the player by the game: they were put back, or the cog was switched off or another one
        // caught them
        var hero = HeroController.instance;
        _caughtCogs.RemoveWhere(cog => cog == null || hero == null || cog.damagingHc != hero);
        if (_caughtCogs.Count == 0 || hero == null || !PlayerTargetRegistry.IsPlayerDown(hero.gameObject)) {
            return;
        }

        if (_rescue is { ByGrinder: false } rescue) {
            rescue.ByGrinder = true;
        }

        foreach (var cog in _caughtCogs) {
            Logger.Info($"The cog '{PathOf(cog.transform)}' held the player as they went down, and lets go of them");
            cog.OnHeroHazardRespawn();
        }

        _caughtCogs.Clear();
    }

    /// <summary>
    /// Tells the members that a cog that stopped as it caught the local player runs on, once it does here, and keeps
    /// the cogs that hold members in their games standing still here until the last of them is let go. A member who
    /// left the room is let go here.
    /// </summary>
    private void UpdateStoppedCogs() {
        if (_cogsStoppedForLocal.Count > 0) {
            List<CogMultiHitter>? runOn = null;
            foreach (var cog in _cogsStoppedForLocal) {
                if (cog == null || cog.cogAnimator == null || !cog.cogAnimator.FreezePosition) {
                    (runOn ??= []).Add(cog!);
                }
            }

            if (runOn != null) {
                foreach (var cog in runOn) {
                    _cogsStoppedForLocal.Remove(cog);
                    if (cog != null) {
                        TellMembersAboutCog(cog, false);
                    }
                }
            }
        }

        if (_cogsStoppedForMembers.Count == 0) {
            return;
        }

        List<CogMultiHitter>? goneCogs = null;
        foreach (var pair in _cogsStoppedForMembers) {
            var cog = pair.Key;
            pair.Value.RemoveWhere(id => !_playerData.TryGetValue(id, out var member) || !member.IsInLocalScene);
            if (cog == null || cog.cogAnimator == null || pair.Value.Count == 0) {
                (goneCogs ??= []).Add(cog!);
                continue;
            }

            // The local player's own catch letting go of it here must not set it going while it holds a member
            cog.cogAnimator.FreezePosition = true;
        }

        if (goneCogs == null) {
            return;
        }

        foreach (var cog in goneCogs) {
            _cogsStoppedForMembers.Remove(cog);
            RunOnUnlessHolding(cog);
        }
    }

    /// <summary>
    /// Lets a cog that stood still for members run on again, unless it holds the local player here.
    /// </summary>
    private void RunOnUnlessHolding(CogMultiHitter? cog) {
        if (cog == null || cog.cogAnimator == null) {
            return;
        }

        var hero = HeroController.instance;
        if (hero != null && cog.damagingHc == hero || _cogsStoppedForLocal.Contains(cog)) {
            return;
        }

        cog.cogAnimator.FreezePosition = false;
    }

    /// <summary>
    /// Tells the members in the room that a cog on a rail caught the local player and stopped, or that it runs on.
    /// </summary>
    private void TellMembersAboutCog(CogMultiHitter cog, bool holds) {
        if (!IsMemberInRoom()) {
            return;
        }

        // The cog already stopped or ran on, so what throws here must not reach the game
        try {
            SendToMembers(new CoopSaveUpdate {
                Kind = CoopSaveUpdateKind.CogHold,
                Scene = cog.gameObject.scene.name,
                ObjectPath = ScenePath.Get(cog.transform),
                Part = (ushort) (holds ? 1 : 0)
            });
        } catch (Exception e) {
            LogInteractionError(e);
        }
    }

    /// <summary>
    /// Takes a member's word that a cog on a rail caught them, or let them go: the same cog here stands still where it
    /// is for as long as it holds any member, and runs on once the last of them is let go, unless it holds the local
    /// player here.
    /// </summary>
    private void OnCogHold(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker) ||
            !_checkedMembers.Contains(player.Id) ||
            ScenePath.Find(update.ObjectPath, update.Scene) is not { activeInHierarchy: true } target ||
            target.GetComponent<CogMultiHitter>() is not { } cog || cog.cogAnimator == null) {
            return;
        }

        if (update.Part != 0) {
            if (!_cogsStoppedForMembers.TryGetValue(cog, out var members)) {
                _cogsStoppedForMembers[cog] = members = [];
            }

            if (members.Add(player.Id)) {
                Logger.Info($"The cog '{update.ObjectPath}' stops here too, since it caught {player.Username}");
            }

            cog.cogAnimator.FreezePosition = true;
            return;
        }

        if (!_cogsStoppedForMembers.TryGetValue(cog, out var held) || !held.Remove(player.Id)) {
            return;
        }

        Logger.Info($"The cog '{update.ObjectPath}' runs on here too, since it let {player.Username} go");
        if (held.Count == 0) {
            _cogsStoppedForMembers.Remove(cog);
            RunOnUnlessHolding(cog);
        }
    }
}
