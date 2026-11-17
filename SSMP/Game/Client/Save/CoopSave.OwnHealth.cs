using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The health of a creature that every game runs by itself, which each game takes down by the blows of every player,
/// its own player's at once and the others' as they come, so that it comes to none at another blow in each game. When
/// it has none goes by the scene host's game (USER 11-17 "我说白了血条听房主的", "房主说血条归零了"): the boss at the end of
/// the second act turns to the next part of its fight, or dies, at the next blow after its health came to none, and
/// the scene host's game tells the others when it came to none there. A game that follows it looks at the health of
/// its creature as some until it is told so, and then the next blow, of any player, turns it in the game that it comes
/// to first (USER 11-17 "本地先打到就本地先演", "房主那边如果自己还没打到，客机打到了就演，自己已经打到了那就已经演了",
/// "房主传过来同理"): the scene host's game turns it at the blow as it comes, or has turned it already, and the others
/// find the turn among the steps they took (see CoopSave.TakeOwnStep), and what comes after it, the same in every
/// game, until the next move that goes by the players, which is the scene host's again ("然后后面得泥石流，战吼，都是统一
/// 的，到下一招分叉路再听房主的"). Each game's health comes to none no later than the scene host's word comes: the blows of
/// the scene host's player are told before its own word, and those of its own player it took at once.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What an update of a creature that every game runs by itself is when it tells that its health came to none in
    /// the scene host's game (see <see cref="TellOwnEmptied"/>).
    /// </summary>
    private const ushort OwnEmptiedPart = 8;

    /// <summary>
    /// The field in which a look at health keeps the health that it looks at, which it reads its health from when it
    /// has one, and the field of the health that it compares.
    /// </summary>
    private static readonly FieldInfo? CompareHealthField = typeof(CompareHP).GetField(
        "healthManager", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
    );

    private static readonly FieldInfo? CompareHpField = typeof(CompareHP).GetField(
        "hp", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
    );

    /// <summary>
    /// The creatures whose health came to none in this game, the scene host's, which told the others so, until a look
    /// of theirs at it finds some again (see <see cref="TellOwnEmptied"/>).
    /// </summary>
    private readonly HashSet<HealthManager> _emptiedTold = [];

    /// <summary>
    /// The creatures whose health came to none in the scene host's game, as it told this game, which follows it, until
    /// a look of theirs at their health here finds none, and goes on as the game has it (see
    /// <see cref="OnOwnHealthCompare"/>).
    /// </summary>
    private readonly HashSet<HealthManager> _emptiedWords = [];

    /// <summary>
    /// The hook on the look of a creature at its health.
    /// </summary>
    private Hook? OwnHealthHook() {
        return CreateHook(
            typeof(CompareHP).GetMethod(
                "DoIntCompare", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            ),
            new Action<Action<CompareHP>, CompareHP>(OnOwnHealthCompare)
        );
    }

    /// <summary>
    /// Tells the members in the room that the health of a creature that every game runs by itself came to none here,
    /// in the scene host's game, once each time (see <see cref="TakeOwnEmptied"/>).
    /// </summary>
    private void TellOwnEmptied(HealthManager health) {
        if (IsSceneHost?.Invoke() != true || !_emptiedTold.Add(health)) {
            return;
        }

        var creature = health.gameObject;
        var place = OwnCreatures.PlaceOf(creature);
        Logger.Info(
            $"[Own creatures] '{place}' has no health left here, {health.hp}, as the members in the room are told"
        );
        SendToRoomMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.OwnCreature,
            Scene = creature.scene.name,
            ObjectPath = place,
            PartCount = OwnEmptiedPart
        });
    }

    /// <summary>
    /// Has a creature that every game runs by itself go on here as it has no health, after the scene host's game told
    /// that it came to none there (see <see cref="TellOwnEmptied"/>): the next look of it at its health that finds
    /// none here goes as the game has it.
    /// </summary>
    private void TakeOwnEmptied(ClientPlayerData player, HealthManager health) {
        _emptiedWords.Add(health);
        Logger.Info(
            $"[Own creatures] '{OwnCreatures.PlaceOf(health.gameObject)}' has no health left in {player.Username}'s " +
            $"game, and goes on here as it has none at its next look, with {health.hp} health here"
        );
    }

    /// <summary>
    /// Hook on the look of a creature at its health, which compares it with a number: one that every game runs by
    /// itself, in a game that follows the scene host's, has some health to its look until the scene host's game told
    /// that it came to none, and then goes on as the game has it at the next look that finds none here. In the scene
    /// host's game, health that it finds again lets the next time it comes to none be told.
    /// </summary>
    private void OnOwnHealthCompare(Action<CompareHP> orig, CompareHP self) {
        if (OwnCreatures.Count == 0 || CompareHealthField?.GetValue(self) is not HealthManager health ||
            health == null || !OwnCreatures.Has(health.gameObject)) {
            orig(self);
            return;
        }

        var creature = health.gameObject;
        if (!OwnCreatures.FollowsSceneHost(creature)) {
            if (health.hp > 0) {
                _emptiedTold.Remove(health);
            }

            orig(self);
            return;
        }

        if (health.hp > 0) {
            orig(self);
            return;
        }

        var place = OwnCreatures.PlaceOf(creature);
        if (_emptiedWords.Remove(health)) {
            Logger.Info(
                $"[Own creatures] '{place}' has no health here, {health.hp}, as the scene host's game told, and goes " +
                "on as the game has it"
            );
            orig(self);
            return;
        }

        // None here before the scene host's game told so: it looks at it as some, a blow from none, which it reads in
        // the look itself, as the look reads it from no health
        Logger.Info(
            $"[Own creatures] '{place}' has no health here, {health.hp}, which the scene host's game did not " +
            "tell yet, and has some to its look"
        );
        CompareHealthField.SetValue(self, null);
        CompareHpField!.SetValue(self, 1);
        try {
            orig(self);
        } finally {
            CompareHealthField.SetValue(self, health);
        }
    }
}
