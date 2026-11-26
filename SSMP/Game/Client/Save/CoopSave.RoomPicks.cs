using System;
using System.Collections.Generic;
using System.Linq;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The groups of the room that let some of their children out at random (ActivateRandomChild), where they hold
/// creatures that every game runs by itself: the burrowing worms, of which a group lets out one at a time and picks
/// again each time one has dug back into the ground, and the conch drillers of a room, which are laid out by one of
/// two patterns as the room loads. Each game picked by itself, and ran the creatures that it had let out: the worm
/// that came out at the scene host's player was not out in another game, where the scene host's steps of it went
/// nowhere, and the worm that game had let out waited underground there for steps of the scene host's that never came
/// (d355194 ran the worms in every game). The copies of such a group were never under it, and came out as the scene
/// host's game had them (see EntityManager.OnPickRandomChild).
///
/// So the scene host's game picks, as the game does, and tells the members in the room which of the group are out after
/// each pick of it, and a member who comes in which are out of every group, before what the creatures here do (see
/// <see cref="SendRoomPicksTo"/>). A game that follows the scene host's picks nothing once it knows that it does, and
/// has the group out as the scene host's game told, as the word comes and as the group picks again here. A game that
/// does not know yet whether it runs the room, as it loads the room, picks as the game does, and is told as its player
/// comes in (USER 11-26 "做吧", on the scene host's pick told rather than dice shared: the pick is made as each game
/// loads the room, before any word of the scene host's comes). With three or four players, each member is told so.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// Which of the children of each group of the room that holds creatures that every game runs by itself are out in
    /// the scene host's game, as it told this one last, by the scene and the place of the group: the name of each
    /// child, and whether it is out.
    /// </summary>
    private readonly Dictionary<(string Scene, string Place), List<(string Name, bool On)>> _roomPicksTold = new();

    /// <summary>
    /// The members who came into the room before this game knew whether it runs it, who are told what a member who
    /// comes in is told of the creatures that every game runs by itself once it knows that it does (see
    /// <see cref="UpdateEarlyComers"/>).
    /// </summary>
    private readonly List<ClientPlayerData> _earlyComers = [];

    /// <summary>
    /// Takes the pick of a group of the room that lets some of its children out at random, if it holds a creature that
    /// every game runs by itself (see EntityManager.PicksOwnGroup): the scene host's game picks as the game does and
    /// tells the members in the room which are out, a game that follows it picks nothing and has out what the scene
    /// host's game told it last, and a game that does not know yet which it is picks as the game does. Whether it took
    /// the pick.
    /// </summary>
    private bool PickOwnGroup(Action<ActivateRandomChild> orig, ActivateRandomChild group) {
        if (!HoldsOwnCreature(group.transform)) {
            return false;
        }

        var place = ScenePath.Get(group.transform);
        switch (OwnCreatures.SceneRole?.Invoke()) {
            case true:
                orig(group);
                TellRoomPick(group, place, null);
                break;
            case false when _roomPicksTold.TryGetValue((group.gameObject.scene.name, place), out var told):
                Logger.Info(
                    $"[Room picks] '{place}' would pick here, and has out what the scene host's game picked last: " +
                    DescribeOut(told)
                );
                SetOut(group.transform, told);
                break;
            case false:
                Logger.Info(
                    $"[Room picks] '{place}' would pick here, and picks nothing: the scene host's game picks and " +
                    "tells this one"
                );
                break;
            default:
                orig(group);
                Logger.Info(
                    $"[Room picks] '{place}' picked here before it is known who runs the room, and has out " +
                    DescribeOut(OutOf(group.transform))
                );
                break;
        }

        return true;
    }

    /// <summary>
    /// Whether a creature that every game runs by itself is under an object of the room.
    /// </summary>
    private static bool HoldsOwnCreature(Transform group) {
        if (OwnCreatures.Count == 0) {
            return false;
        }

        foreach (var pair in OwnCreatures.All) {
            if (pair.Key != null && pair.Key.transform.IsChildOf(group)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tells a member who came into the room what is told of the creatures here that every game runs by itself, if
    /// this game is the scene host's: which of the groups of the room are out, and then where the creatures are and
    /// what they do (see CoopSave.OwnCreatures). A member who comes in before this game knows whether it runs the room
    /// is told so once it knows that it does (see <see cref="UpdateEarlyComers"/>).
    /// </summary>
    private void SendOwnRoomTo(ClientPlayerData player) {
        if (OwnCreatures.SceneRole?.Invoke() == null) {
            _earlyComers.Add(player);
            return;
        }

        SendRoomPicksTo(player);
        SendOwnCreaturesTo(player);
    }

    /// <summary>
    /// Tells the members who came into the room before this game knew whether it runs the room what is told of the
    /// creatures here that every game runs by itself, once it knows that it does (see <see cref="SendOwnRoomTo"/>):
    /// as this game is told who is in the room as it comes in, before it is told that it runs it, the members in the
    /// room went untold, and one whose game had out another pattern of the drillers of a room kept it all through the
    /// visit, as the group never picks again there (review of 6db4406). They were not told either where the creatures
    /// are, which the creatures' next steps mend, nor of a creature dead here, which no step mends.
    /// </summary>
    private void UpdateEarlyComers() {
        if (_earlyComers.Count == 0 || OwnCreatures.SceneRole?.Invoke() is not { } runs) {
            return;
        }

        foreach (var player in _earlyComers) {
            if (!runs || !player.IsInLocalScene) {
                continue;
            }

            Logger.Info(
                $"[Own creatures] {player.Username} came in before this game knew that it runs the room, and is told " +
                "now what a member who comes in is told"
            );
            SendRoomPicksTo(player);
            SendOwnCreaturesTo(player);
        }

        _earlyComers.Clear();
    }

    /// <summary>
    /// Tells a member who came into the room which of the children of each group here that holds creatures that every
    /// game runs by itself are out, if this game is the scene host's (see <see cref="TellRoomPick"/>): their game,
    /// which picked as it loaded the room, has them out so too.
    /// </summary>
    private void SendRoomPicksTo(ClientPlayerData player) {
        if (OwnCreatures.Count == 0 || !_checkedMembers.Contains(player.Id) || IsSceneHost?.Invoke() != true ||
            IsLeavingOnDebugTrip?.Invoke() == true) {
            return;
        }

        foreach (var group in Object.FindObjectsByType<ActivateRandomChild>(FindObjectsSortMode.None)) {
            if (HoldsOwnCreature(group.transform)) {
                TellRoomPick(group, ScenePath.Get(group.transform), player);
            }
        }
    }

    /// <summary>
    /// Tells which of the children of a group of the room are out here: to a member who came in, or to the members in
    /// the room, after the scene host's game picked.
    /// </summary>
    private void TellRoomPick(ActivateRandomChild group, string place, ClientPlayerData? to) {
        var outs = OutOf(group.transform);
        var update = new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.RoomPick,
            Scene = group.gameObject.scene.name,
            ObjectPath = place,
            Names = outs.Select(child => child.Name).ToList(),
            Amounts = outs.Select(child => child.On ? 1 : 0).ToList()
        };
        Logger.Info(
            $"[Room picks] '{place}' has out here {DescribeOut(outs)}, as " +
            (to != null
                ? $"{to.Username}, who came in, is told"
                : "the scene host's game picked, which the members in the room are told")
        );
        if (to == null) {
            SendToRoomMembers(update);
            return;
        }

        update.TargetId = to.Id;
        Send(update);
    }

    /// <summary>
    /// Takes the word of the scene host's game of which of the children of a group of the room are out there (see
    /// <see cref="TellRoomPick"/>): this game has them out so too, and keeps it for the picks of the group here.
    /// </summary>
    private void OnRoomPick(ClientPlayerData player, CoopSaveUpdate update) {
        var place = update.ObjectPath;
        var told = update.Names.Zip(update.Amounts, (name, on) => (name, on != 0)).ToList();
        if (OwnCreatures.SceneRole?.Invoke() == true) {
            Logger.Info(
                $"[Room picks] '{place}' has out in {player.Username}'s game {DescribeOut(told)}, which this game, " +
                "the scene host's, picks by itself"
            );
            return;
        }

        _roomPicksTold[(update.Scene, place)] = told;
        if (ScenePath.Find(place, update.Scene) is not { } group) {
            Logger.Info(
                $"[Room picks] '{place}' has out in {player.Username}'s game {DescribeOut(told)}, and is not here"
            );
            return;
        }

        Logger.Info(
            $"[Room picks] '{place}' has out in {player.Username}'s game {DescribeOut(told)}, and here " +
            $"{DescribeOut(OutOf(group.transform))}, which goes as there"
        );
        SetOut(group.transform, told);
    }

    /// <summary>
    /// The name of each child of a group of the room, and whether it is out.
    /// </summary>
    private static List<(string Name, bool On)> OutOf(Transform group) {
        var outs = new List<(string Name, bool On)>();
        foreach (Transform child in group) {
            outs.Add((child.name, child.gameObject.activeSelf));
        }

        return outs;
    }

    /// <summary>
    /// Has the children of a group of the room out as told, by their names: a child that the word does not name stays
    /// as it is.
    /// </summary>
    private static void SetOut(Transform group, List<(string Name, bool On)> told) {
        foreach (Transform child in group) {
            foreach (var (name, on) in told) {
                if (child.name == name && child.gameObject.activeSelf != on) {
                    child.gameObject.SetActive(on);
                }
            }
        }
    }

    /// <summary>
    /// The children of a group of the room that are out, for the log.
    /// </summary>
    private static string DescribeOut(List<(string Name, bool On)> outs) {
        var on = outs.Where(child => child.On).Select(child => $"'{child.Name}'").ToList();
        return on.Count > 0 ? string.Join(", ", on) : "none of them";
    }
}
