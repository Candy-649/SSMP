using System.IO;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The walk of a creature that every game runs by itself as it goes by a player, which its Walker does in the game's
/// code, outside its state machines (see GamePatcher.OwnWalks): the scene host's game sees the players, turns the
/// creature to face one that it sees behind it, and has it walk on while it sees one, and it tells the members in the
/// room as the creature comes to see one or no longer does, and as it turns to face one, with where its walk is, which
/// every other game takes as it comes. Before, it went with the next step of the creature alone, and each other game's
/// walked on by itself where the scene host's had turned, and was set where that one was at its next move.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What an update of a creature that every game runs by itself is when the scene host's game tells where its walk
    /// is there as it comes to see a player in its range or no longer does, or as it turns to face one (see
    /// <see cref="TellOwnWalk"/>): whether it sees one in <see cref="CoopSaveUpdate.Key"/>, 1 or 0, whether it turned
    /// in <see cref="CoopSaveUpdate.Sequence"/>, 1 or 0, and where its walk is in <see cref="CoopSaveUpdate.Bytes"/>
    /// (see GamePatcher.WriteWalk).
    /// </summary>
    private const ushort OwnWalkPart = 22;

    /// <summary>
    /// Tells the members in the room where the walk of a creature that every game runs by itself is in this game, the
    /// scene host's, as it comes to see a player in its range or no longer does, or as it turns to face one (see
    /// GamePatcher.TellsWalk). Whether there was anyone to tell.
    /// </summary>
    private bool TellOwnWalk(GameObject creature, bool sees, bool turned) {
        if (IsSceneHost?.Invoke() != true || !IsMemberInRoom()) {
            return false;
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream)) {
            GamePatcher.WriteWalk(writer, creature);
        }

        SendToRoomMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.OwnCreature,
            Scene = creature.scene.name,
            ObjectPath = OwnCreatures.PlaceOf(creature),
            PartCount = OwnWalkPart,
            Key = sees ? 1UL : 0UL,
            Sequence = turned ? 1UL : 0UL,
            Bytes = stream.ToArray()
        });
        return true;
    }

    /// <summary>
    /// Takes the word of the scene host's game of where the walk of a creature that every game runs by itself is there
    /// (see <see cref="TellOwnWalk"/>).
    /// </summary>
    private static void TakeOwnWalk(ClientPlayerData player, GameObject creature, CoopSaveUpdate update) {
        using var reader = new BinaryReader(new MemoryStream(update.Bytes));
        GamePatcher.TakeWalkWord(creature, update.Key == 1, update.Sequence == 1, reader, player.Username);
    }
}
