using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker.Actions;
using MonoMod.RuntimeDetour;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;
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
/// 的，到下一招分叉路再听房主的"). The word names the blow that emptied it there, which mostly comes to the others after the
/// word, and counts in their game as it plays that blow too (see CoopSave.TakeOwnEmptied): the word of the
/// user's blow that emptied the boss came to Luminary's game before the blow, which then played there as the next blow
/// after it, and the boss fell there 1.6 s before the user's next blow felled it in the user's game, its blades held in
/// the air all the while and put 30 units away as the scene host's step came (Luminary's log of 11-22, 20:50:12.180 to
/// 13.780; USER "为啥会差很多", "但是不应该差很多").
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What an update of a creature that every game runs by itself is when it tells that its health came to none in
    /// the scene host's game (see <see cref="TellOwnEmptied"/>), with the player whose blow emptied it there in
    /// <see cref="CoopSaveUpdate.Key"/>, as a throw names a player (see ThrownThings.CodeOf), and the number of the
    /// blow in their game in <see cref="CoopSaveUpdate.Sequence"/>, 0 for none.
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
    /// The creatures whose health came to none in the scene host's game, as it told this game, which follows it, with
    /// the health that each had here as the word came, or less as it counted, until a look of theirs at their health
    /// here finds none, and goes on as the game has it, or until they were given more health here than that, for the
    /// next part of their fight (see <see cref="OnOwnHealthCompare"/>).
    /// </summary>
    private readonly Dictionary<HealthManager, int> _emptiedWords = new();

    /// <summary>
    /// The creatures whose health came to none in the scene host's game at a blow of another player than this game's,
    /// as it told this game, which this game had not played yet as the word came, with that player and the number of
    /// the blow in their game, and the health that it had here as the word came: the word counts here as it plays that
    /// blow, and is let go of at a later one of theirs, which tells that that blow never played here, where the scene
    /// host's steps take the creature on (see <see cref="TookOthersBlow"/>). The blow can play here after the next
    /// part's health: kept behind a move that the creature plays through here, as are the scene host's steps after
    /// it, which go first as the move ends (review of 6be63b5).
    /// </summary>
    private readonly Dictionary<HealthManager, (ushort Striker, uint Strike, int HadThen)> _emptiedAtBlows = [];

    /// <summary>
    /// The newest blow or touch of each other player that this game played on the health of a creature that every game
    /// runs by itself, by its number in their game (see <see cref="TookOthersBlow"/>).
    /// </summary>
    private readonly Dictionary<(HealthManager Health, ushort Striker), uint> _othersBlowsTaken = [];

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
    /// in the scene host's game, once each time, and at which blow (see <see cref="TakeOwnEmptied"/>): of this game's
    /// player, whose word of the blow goes after this one, or of another, whose blow this game plays as it came.
    /// </summary>
    private void TellOwnEmptied(HealthManager health) {
        if (IsSceneHost?.Invoke() != true || !_emptiedTold.Add(health)) {
            return;
        }

        var creature = health.gameObject;
        var place = OwnCreatures.PlaceOf(creature);
        var (striker, strike) = CoopHits.IsMakingLocalStrike
            ? (ThrownThings.ThrowersPlayer, CoopHits.LocalStrikeNumber)
            : CoopHits.IsReplayingPartnersStrike
                ? (CoopHits.ReplayedStriker, CoopHits.ReplayedStrikeNumber)
                : (ThrownThings.NoPlayer, 0u);
        var blow = strike == 0 ? "" :
            striker == ThrownThings.ThrowersPlayer ? $" at blow {strike} of this game's player," :
            $" at blow {strike} of the player {striker},";
        Logger.Info(
            $"[Own creatures] '{place}' has no health left here, {health.hp},{blow} as the members in the room are told"
        );
        SendToRoomMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.OwnCreature,
            Scene = creature.scene.name,
            ObjectPath = place,
            PartCount = OwnEmptiedPart,
            Key = striker,
            Sequence = strike
        });
    }

    /// <summary>
    /// Has a creature that every game runs by itself go on here as it has no health, after the scene host's game told
    /// that it came to none there (see <see cref="TellOwnEmptied"/>): the next look of it at its health that finds
    /// none here goes as the game has it. Where a blow of another player than this game's emptied it there, the word
    /// counts as this game plays that blow too, which mostly comes after the word: the blow plays here as there, as one
    /// before its health came to none, and the next one as the one after. One that played here already, which a later
    /// blow of theirs here tells as well, as each player's blows come in their order, counts at once.
    /// </summary>
    private void TakeOwnEmptied(ClientPlayerData player, HealthManager health, CoopSaveUpdate update) {
        var place = OwnCreatures.PlaceOf(health.gameObject);
        if (update.Sequence > 0 && !IsLocalPlayersBlow(player, update)) {
            var striker = update.Key == ThrownThings.ThrowersPlayer ? player.Id : (ushort) update.Key;
            var strike = (uint) update.Sequence;
            if (!_othersBlowsTaken.TryGetValue((health, striker), out var taken) || taken < strike) {
                _emptiedAtBlows[health] = (striker, strike, health.hp);
                Logger.Info(
                    $"[Own creatures] '{place}' has no health left in {player.Username}'s game since blow " +
                    $"{strike} of the player {striker}, which this game has not played yet, with {health.hp} health " +
                    "here"
                );
                return;
            }
        }

        _emptiedAtBlows.Remove(health);
        _emptiedWords[health] = health.hp;
        Logger.Info(
            $"[Own creatures] '{place}' has no health left in {player.Username}'s game, and goes on here as it has " +
            $"none at its next look, with {health.hp} health here"
        );
    }

    /// <summary>
    /// Whether the health of a creature that every game runs by itself came to none in the scene host's game, as it
    /// told this game, and was given no more here since: its bar shows none then, as the scene host's does, whatever
    /// this game's own count of the blows left it (USER 11-24 "丝母假死在我这里血条没有到0", "死了就死了不行吗").
    /// </summary>
    private bool HasNoHealthAsTold(HealthManager health) {
        return _emptiedWords.TryGetValue(health, out var hadThen) && health.hp <= Mathf.Max(hadThen, 1);
    }

    /// <summary>
    /// A blow or touch of another player that this game played on an object of the room, with what it set off here
    /// (see CoopHits.ReplayedStrike): one on the health of a creature that every game runs by itself is kept, and the
    /// scene host's word that its health came to none there at it counts from now on (see
    /// <see cref="TakeOwnEmptied"/>). A word of one of theirs before it is let go of, as that blow never played here:
    /// counted at a later one, a held blow played after it as the next one (review of 1655e30). The scene host's game
    /// tells the others that it is through with it (see TellStrikeDone).
    /// </summary>
    /// <param name="struck">What the blow or touch reached.</param>
    /// <param name="striker">The player who struck.</param>
    /// <param name="strike">The number of the strike in the game of that player.</param>
    private void TookOthersBlow(GameObject struck, ushort striker, uint strike) {
        if (struck.TryGetComponent<HealthManager>(out var health) && OwnCreatures.Has(struck)) {
            _othersBlowsTaken[(health, striker)] = strike;
            if (_emptiedAtBlows.TryGetValue(health, out var at) && at.Striker == striker && at.Strike <= strike) {
                _emptiedAtBlows.Remove(health);
                var place = OwnCreatures.PlaceOf(struck);
                if (at.Strike == strike) {
                    _emptiedWords[health] = Mathf.Min(at.HadThen, health.hp);
                    Logger.Info(
                        $"[Own creatures] '{place}' took blow {strike} of the player {striker} here, and goes on as " +
                        $"it has no health at its next look, with {health.hp} health here"
                    );
                } else {
                    Logger.Info(
                        $"[Own creatures] '{place}' took blow {strike} of the player {striker} here, and never blow " +
                        $"{at.Strike}, at which its health came to none in the scene host's game: it goes on by the " +
                        "scene host's steps"
                    );
                }
            }
        }

        TellStrikeDone(struck, striker, strike);
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

        // The word is of the part of the fight that the health came to none in: health given here since, more than it
        // had as the word counted, is the next part's, which the scene host's steps took the creature on to while it
        // still had some here, and its look at none in that part goes by the scene host's word of that part (the boss
        // at the end of the second act fell so with 13 here, the logs of 11-24 19:39:45, and a later look at none
        // would have ended a part of its fight before the scene host's game did)
        var place = OwnCreatures.PlaceOf(creature);
        if (_emptiedWords.TryGetValue(health, out var hadThen) && health.hp > Mathf.Max(hadThen, 1)) {
            _emptiedWords.Remove(health);
            Logger.Info(
                $"[Own creatures] '{place}' was given health here, {health.hp}, since the scene host's game told " +
                $"that it had none, {hadThen} here then, and waits for the scene host's word of this part"
            );
        }

        if (health.hp > 0) {
            orig(self);
            return;
        }

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
