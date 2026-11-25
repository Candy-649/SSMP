using System.Collections.Generic;
using System.Linq;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The summons of the creatures that every game runs by itself: the creatures of a kind that every game runs by itself
/// that one of them makes as it runs, which every game makes by itself as its own creature does, under the name that
/// the action that made it gives it, the same in every game (see OwnCreatures.MakesSummon), and runs as its own,
/// rolling under that name and taking the steps that go by where the players are from the scene host's game (USER
/// 11-25 "各生各的，不看人的部分投骰子，看人的部分听房主"). Before, the scene host's game made them alone and every other
/// showed a copy of them (see RoomCreatures).
///
/// A game a moment behind the scene host's makes a summon a moment later: the words of the scene host's game of it
/// that come before wait for it to be made here and to start, and are taken then, as they came (see
/// <see cref="KeepsSummonWord"/>). A member who comes in after a summon was made is told by the scene host's game what
/// made it, and makes it as that action does, before they are told what any creature does (see
/// <see cref="TakeOwnMade"/>); one that their own creature makes as it starts, which the scene host's made as it
/// started too, is the same one, and so is one that the same action of it makes on its way into a step of the scene
/// host's (see OwnCreatures.MakesSummon). One that is gone here is taken away there, and one that lies dead here, of a
/// death that it comes back from, dies there too (see SendOwnCreaturesTo). With three or four players, every game
/// makes each summon, and every game that comes in later is told of it.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What an update of a creature that every game runs by itself is when the scene host's game tells a member who has
    /// come into the room what made a summon of one there (see <see cref="TellOwnMade"/>): the place of its maker, the
    /// state machine and the state of the action that made it in <see cref="CoopSaveUpdate.Names"/>, and the number of
    /// the action in the state in <see cref="CoopSaveUpdate.Key"/>.
    /// </summary>
    private const ushort OwnMadePart = 18;

    /// <summary>
    /// The words of the scene host's game of the summons that are not made here yet, or have not started, by their
    /// places, as they came (see <see cref="KeepsSummonWord"/>).
    /// </summary>
    private readonly Dictionary<string, List<(ClientPlayerData Player, CoopSaveUpdate Word)>> _summonWords = new();

    /// <summary>
    /// Keeps a word of the scene host's game of a summon, or of a part of one, that is not made here yet, as its maker
    /// here is a moment behind, or whose state machine has not started yet, which would start over what the word set
    /// it to, or that comes after others that wait, until it is made and started here (see
    /// <see cref="UpdateSummonWords"/>). The word of what made one, for the local player who came in after it was
    /// made, is taken at once (see <see cref="TakeOwnMade"/>). Whether it takes the word.
    /// </summary>
    private bool KeepsSummonWord(ClientPlayerData player, CoopSaveUpdate update) {
        if (update.PartCount == OwnMadePart) {
            TakeOwnMade(update);
            return true;
        }

        var place = update.ObjectPath;
        if (!_summonWords.ContainsKey(place) && OwnCreatures.Find(update.Scene, place) is { } found &&
            HasStarted(found, update)) {
            return false;
        }

        if (!_summonWords.TryGetValue(place, out var words)) {
            words = [];
            _summonWords[place] = words;
            Logger.Info(
                $"[Own creatures] {player.Username}'s game told of '{place}', a summon that is not made or not " +
                "started here yet, and the word waits for it"
            );
        }

        words.Add((player, update));
        return true;
    }

    /// <summary>
    /// Whether the state machine of a summon, or of a part of one, that a word is of has started, or the first of the
    /// object, for a word of no state machine.
    /// </summary>
    private static bool HasStarted(GameObject thing, CoopSaveUpdate update) {
        var machine = update.FsmName.Length > 0
            ? PlayMakerFSM.FindFsmOnGameObject(thing, update.FsmName)
            : thing.GetComponent<PlayMakerFSM>();
        return machine == null || machine.Fsm.ActiveState != null;
    }

    /// <summary>
    /// Takes the words of the scene host's game that waited for summons, as they came, of each that is made and has
    /// started here (see <see cref="KeepsSummonWord"/>).
    /// </summary>
    private void UpdateSummonWords() {
        if (_summonWords.Count == 0) {
            return;
        }

        foreach (var place in _summonWords.Keys.ToList()) {
            var words = _summonWords[place];
            var first = words[0].Word;
            if (OwnCreatures.Find(first.Scene, place) is not { } found || !HasStarted(found, first)) {
                continue;
            }

            _summonWords.Remove(place);
            Logger.Info(
                $"[Own creatures] '{place}' is made and started here, and takes {words.Count} word(s) that waited"
            );
            foreach (var (player, word) in words) {
                OnOwnCreature(player, word);
            }
        }
    }

    /// <summary>
    /// Tells a member who has come into the room what made a summon here (see <see cref="TakeOwnMade"/>), before what
    /// it does.
    /// </summary>
    private void TellOwnMade(ClientPlayerData player, string scene, string place, OwnCreatures.SummonMaker maker) {
        Logger.Info(
            $"[Own creatures] {player.Username} is told that '{maker.Place}' made '{place}' here in " +
            $"'{maker.State}' of '{maker.Fsm}'"
        );
        Send(new CoopSaveUpdate {
            TargetId = player.Id,
            Kind = CoopSaveUpdateKind.OwnCreature,
            Scene = scene,
            ObjectPath = place,
            PartCount = OwnMadePart,
            Names = [maker.Place, maker.Fsm, maker.State],
            Key = (ulong) maker.Action
        });
    }

    /// <summary>
    /// Makes a summon that the scene host's game made before the local player came in, as the action that made it there
    /// makes it (see OwnCreatures.MakeSummonAsTold), whose state comes next. One that is here already, as its maker
    /// here made it as it started, as it did there, is that one.
    /// </summary>
    private static void TakeOwnMade(CoopSaveUpdate update) {
        var place = update.ObjectPath;
        var maker = new OwnCreatures.SummonMaker(update.Names[0], update.Names[1], update.Names[2], (int) update.Key);
        var told = $"'{maker.Place}' made '{place}' in '{maker.State}' of '{maker.Fsm}' in the scene host's game";
        if (OwnCreatures.Find(update.Scene, place) != null) {
            Logger.Info($"[Own creatures] {told}, which is here already");
            return;
        }

        Logger.Info(
            OwnCreatures.MakeSummonAsTold(place, maker) != null
                ? $"[Own creatures] {told}, and makes it here too"
                : $"[Own creatures] {told}, whose maker or action is not here"
        );
    }
}
