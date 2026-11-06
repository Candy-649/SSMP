using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SSMP.Animation.Effects;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The maggots of another player, shown on their character.
///
/// A maggoted hero has maggots dripping off them and a tint that comes and goes, which the game makes anew on the hero
/// as they become maggoted (HeroController.SetIsMaggoted) rather than switching on a part of the hero, so the other
/// players never saw it: one could not tell whether the other had to go and wash it off (USER 11-06, "我看不到我朋友有没有
/// 中蛆，就是中蛆状态身上会有掉下来蛆的特效"). Each game tells the members in its room as its own hero becomes maggoted or
/// stops being so, and a member who comes into the room while it is, and the character of that player drips and is
/// tinted the same way. What the screen of the maggoted player shows and the sound it hears stay theirs.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The name of the copy of the dripping maggots on the character of another player.
    /// </summary>
    private const string PartnerMaggotsName = "Partner Maggots";

    /// <summary>
    /// How long the copy of the dripping maggots stays after its player stopped being maggoted, in seconds, so that
    /// the drops that fell already land.
    /// </summary>
    private const float PartnerMaggotsSettle = 2f;

    /// <summary>
    /// The tint that comes and goes on the characters of maggoted players, by the instance ID of the character.
    /// </summary>
    private readonly Dictionary<int, SpriteFlash.FlashHandle> _maggotFlashes = new();

    /// <summary>
    /// The hook that sees the local hero become maggoted and stop being so.
    /// </summary>
    private Hook? _maggotHook;

    /// <summary>
    /// Watches for the local hero becoming maggoted and stopping being so.
    /// </summary>
    private void RegisterMaggotHooks() {
        _maggotHook = CreateHook(
            typeof(HeroController).GetMethod(
                nameof(HeroController.SetIsMaggoted),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            ),
            (Action<Action<HeroController, bool>, HeroController, bool>) OnSetIsMaggoted
        );
    }

    /// <summary>
    /// Tells the members in the room when the local hero becomes maggoted or stops being so.
    /// </summary>
    private void OnSetIsMaggoted(Action<HeroController, bool> orig, HeroController self, bool value) {
        var was = self.cState.isMaggoted;
        orig(self, value);

        if (was == self.cState.isMaggoted) {
            return;
        }

        Logger.Info(
            $"[Maggots] The local player {(value ? "became" : "is no longer")} maggoted, which the members in the room " +
            "are told"
        );
        SendToRoomMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.Maggoted,
            PartCount = (ushort) (value ? 1 : 0)
        });
    }

    /// <summary>
    /// Tells a member who has come into the room, or into whose room the local player has come, that the local hero is
    /// maggoted, if so. Their game makes the character of the local player anew, without any.
    /// </summary>
    /// <param name="player">The member.</param>
    private void SendMaggotsTo(ClientPlayerData player) {
        if (!_checkedMembers.Contains(player.Id) || HeroController.instance is not { } hero ||
            !hero.cState.isMaggoted) {
            return;
        }

        Logger.Info($"[Maggots] {player.Username} is told that the local player is maggoted, as they meet in the room");
        Send(new CoopSaveUpdate {
            TargetId = player.Id,
            Kind = CoopSaveUpdateKind.Maggoted,
            PartCount = 1
        });
    }

    /// <summary>
    /// Shows on the character of another player that they are maggoted in their game, or that they no longer are.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="update">The update, whose <see cref="CoopSaveUpdate.PartCount"/> says which.</param>
    private void OnMaggoted(ClientPlayerData player, CoopSaveUpdate update) {
        var maggoted = update.PartCount != 0;
        var character = player.PlayerObject;
        if (!player.IsInLocalScene || character == null) {
            Logger.Info(
                $"[Maggots] {player.Username} {(maggoted ? "became" : "is no longer")} maggoted, with no character of " +
                "theirs in this room to show it on"
            );
            return;
        }

        if (!maggoted) {
            Logger.Info($"[Maggots] {player.Username} is no longer maggoted, which their character stops showing");
            HideMaggots(character);
            return;
        }

        var hero = HeroController.instance;
        if (hero == null) {
            return;
        }

        var dripping = character.FindGameObjectInChildren(PartnerMaggotsName) != null;
        if (!dripping) {
            var maggots = EffectUtils.SpawnGlobalPoolObject(hero.maggotEffectPrefab.gameObject, character.transform, 0f, true);
            if (maggots != null) {
                maggots.name = PartnerMaggotsName;
                maggots.GetComponent<PlayParticleEffects>().PlayParticleSystems();
            }
        }

        var id = character.GetInstanceID();
        var tinted = character.TryGetComponent<SpriteFlash>(out var flash) &&
                     _maggotFlashes.TryGetValue(id, out var handle) && flash.IsFlashing(true, handle);
        if (!tinted && flash != null) {
            _maggotFlashes[id] = flash.FlashingMaggot();
        }

        Logger.Info(
            $"[Maggots] {player.Username} became maggoted, which their character shows" +
            (dripping && tinted ? " already" : "")
        );
    }

    /// <summary>
    /// A member left the room: their character stops showing their maggots, which they tell the room again when they
    /// come back.
    /// </summary>
    /// <param name="player">The member.</param>
    private void HideMaggotsOf(ClientPlayerData player) {
        if (player.PlayerObject is { } character) {
            HideMaggots(character);
        }
    }

    /// <summary>
    /// Stops the dripping maggots and the tint of maggots on the character of another player.
    /// </summary>
    /// <param name="character">The character.</param>
    private void HideMaggots(GameObject character) {
        if (_maggotFlashes.Remove(character.GetInstanceID(), out var handle) &&
            character.TryGetComponent<SpriteFlash>(out var flash)) {
            flash.CancelRepeatingFlash(handle);
        }

        if (character.FindGameObjectInChildren(PartnerMaggotsName) is not { } maggots) {
            return;
        }

        maggots.name = PartnerMaggotsName + " (ending)";
        maggots.GetComponent<PlayParticleEffects>().StopParticleSystems();
        Object.Destroy(maggots, PartnerMaggotsSettle);
    }
}
