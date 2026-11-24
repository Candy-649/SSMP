using System;
using System.Linq;
using System.Reflection;
using MonoMod.Cil;
using SSMP.Animation.Effects;
using SSMP.Game.Client.Entity;
using SSMP.Util;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The hits that the game deals by itself a moment after a blow of an attack that has them, one after another, to what
/// the blow struck (HealthManager.DoLagHits, which the attack starts as its blow lands): the drill of one crest grinds
/// on in what it struck, a slash of another strikes again for 1, a tool's poison bites. They come through no attack,
/// so the blow went to the others and they did not: on a creature that every game runs by itself, they took health in
/// the game of the player who struck alone. The boss at the end of the second act still had 13 in the user's game as
/// it fell in the scene host's, which alone took the 5 that Luminary's drill ground on with after its blows, as the
/// user's game alone took the 1 after the user's slashes (the logs of 11-24, 19:39:37 to 19:39:45; USER "伤害居然不是
/// 可靠了吗"). Each one on such a creature goes to the others now as a blow of the player who
/// struck, as the blow that started them did, and plays there as it played here: a wound, a block, or damage without a
/// hit (see CoopHitUpdate.ExtraDamage).
/// </summary>
internal partial class CoopHits {
    /// <summary>
    /// Registers the hook on the routine of the hits that the game deals by itself after a blow.
    /// </summary>
    private void RegisterLagHitHooks() {
        AddILHook(
            typeof(HealthManager).GetNestedTypes(BindingFlags.NonPublic)
                .FirstOrDefault(type => type.Name.StartsWith("<LagHits>", StringComparison.Ordinal))
                ?.GetMethod("MoveNext", InstanceFlags),
            DealLagHitsAsBlows
        );
    }

    /// <summary>
    /// IL hook on the routine of the hits that the game deals by itself after a blow, which has each of them go through
    /// <see cref="OnLagHit"/> or <see cref="OnLagExtraDamage"/>.
    /// </summary>
    private void DealLagHitsAsBlows(ILContext il) {
        try {
            var c = new ILCursor(il);
            c.GotoNext(
                MoveType.Before, i => i.MatchCall(typeof(HealthManager), nameof(HealthManager.ApplyExtraDamage))
            );
            c.Remove();
            c.EmitDelegate<Action<HealthManager, HitInstance>>(OnLagExtraDamage);

            c.GotoNext(MoveType.Before, i => i.MatchCall(typeof(HealthManager), nameof(HealthManager.Hit)));
            c.Remove();
            c.EmitDelegate<Func<HealthManager, HitInstance, IHitResponder.HitResponse>>(OnLagHit);
        } catch (Exception e) {
            Logger.Error($"Could not change the routine of the hits that the game deals after a blow:\n{e}");
        }
    }

    /// <summary>
    /// A hit that the game deals by itself after a blow: one of the local player's on a creature that every game runs
    /// by itself goes to the members in the room as a blow of theirs (see <see cref="StrikeAgain"/>).
    /// </summary>
    private IHitResponder.HitResponse OnLagHit(HealthManager health, HitInstance hit) {
        return SendsLagHit(health, hit) ? StrikeAgain(health, hit, false, () => health.Hit(hit)) : health.Hit(hit);
    }

    /// <summary>
    /// Damage without a hit that the game deals by itself after a blow: the local player's on a creature that every
    /// game runs by itself goes to the members in the room as a blow of theirs (see <see cref="StrikeAgain"/>).
    /// </summary>
    private void OnLagExtraDamage(HealthManager health, HitInstance hit) {
        if (!SendsLagHit(health, hit)) {
            health.ApplyExtraDamage(hit);
            return;
        }

        StrikeAgain(health, hit, true, () => {
            health.ApplyExtraDamage(hit);
            return IHitResponder.Response.DamageEnemy;
        });
    }

    /// <summary>
    /// Whether a hit that the game deals by itself after a blow goes to the members in the room: one of the local
    /// player's on a creature that every game runs by itself, by what struck as their blow does (see MakeDamagerHit):
    /// their character, the needle that a throw of theirs left in the room, or a tool of theirs.
    /// </summary>
    private bool SendsLagHit(HealthManager health, HitInstance hit) {
        return HasMembers && OwnCreatures.Has(health.gameObject) &&
               (hit.IsHeroDamage || hit.Source != null && HeroHarpoon.IsLocalNeedlePart(hit.Source.transform) ||
                LocalToolComponent.IsLocalTool(hit.Source));
    }

    /// <summary>
    /// Deals a hit that the game deals by itself after a blow of the local player as a blow of theirs on an object of
    /// the room (see MakeDamagerHit), and sends it to the members in the room unless it did nothing there, whose games
    /// deal it as it was dealt here (see ReplayHit). The scene host's word that the health of the creature came to none
    /// names it when it was the one that emptied it (see CoopSave.TellOwnEmptied).
    /// </summary>
    /// <param name="health">The health of the creature.</param>
    /// <param name="hit">The hit.</param>
    /// <param name="extra">Whether it is damage without a hit (see CoopHitUpdate.ExtraDamage).</param>
    /// <param name="deal">Deals it as the game does.</param>
    private IHitResponder.HitResponse StrikeAgain(
        HealthManager health,
        HitInstance hit,
        bool extra,
        Func<IHitResponder.HitResponse> deal
    ) {
        using var strike = new LocalStrike(true);
        var update = CreateUpdate(health, hit);
        var response = default(IHitResponder.HitResponse);
        int[] dice = [];
        var hitName = update != null ? NextHitName() : null;
        var seeds = update != null ? new NamedSeeds() : null;
        var hero = HeroController.SilentInstance;
        var striker = hero != null ? hero.gameObject : null;
        var answered = AnswersAttack(
            () => dice = SharedDice.Record(() => response = HitRoomObject(deal, health, striker)), hitName, seeds
        ) || response.response != IHitResponder.Response.None;
        if (update == null || !answered || !_netClient.IsConnected) {
            return response;
        }

        update.Blocked = response.response == IHitResponder.Response.Invincible;
        update.FromSceneHost = update.Blocked && OwnCreatures.RunsHere(health.gameObject);
        update.ExtraDamage = extra;
        update.Hit = AppendRolls(update.Hit, dice, hitName ?? "", seeds);
        update.Strike = LocalStrikeNumber;
        SendToRoomMembers(update);
        NoteTraffic(
            update.Scene, update.Path,
            $"sent {(extra ? "damage without a hit" : "a hit")} that the game dealt by itself after a blow on " +
            $"{update.Responder} ({DescribeMoves(health)})"
        );
        return response;
    }
}
