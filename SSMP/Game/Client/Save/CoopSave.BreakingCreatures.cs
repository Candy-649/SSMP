using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// The things that break and that the game counts as creatures it killed, though they have no health: the lanterns
/// that summon fireballs, whose break the game writes into the journal as a kill. They go as the creatures go (USER
/// 11-14 "火灯就按照小怪来"). A player who walks into a room that others are in sees each of them as it stands there
/// now, broken or with the hits it took, as they see a creature: a lantern that the partner broke stood whole in the
/// game of a player who had rested elsewhere and came back while the partner was still in the room, and took three
/// more hits there (the friend's game on 11-13 at 21:24:39, "Wisp Flame Lantern/lamp/Active|Breakable: here "on,
/// isBroken=false, hitsLeft=4", partner "on, isBroken=true, hitsLeft=0""). And its break goes into the save of every
/// member, as the death of a creature does (see CoopSave.CreatureDeaths), so that once everyone has left, the room
/// brings it back as the game does, at the next rest, for everyone alike.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// Whether a saved object is a thing that the game counts as a creature it killed though it has no health: a state
    /// machine on it writes the kill into the journal. Only machines that have started are looked at, as asking for the
    /// actions of one that has not breaks it; a thing has started by the time it breaks.
    /// </summary>
    private static bool CountsAsAKill(PersistentBoolItem item) {
        foreach (var fsm in item.GetComponents<PlayMakerFSM>()) {
            if (fsm.Fsm is not { Started: true }) {
                continue;
            }

            foreach (var state in fsm.FsmStates) {
                foreach (var action in state.Actions) {
                    if (action is global::RecordJournalKill or global::RecordJournalKillV2) {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Tells a member who just walked into the room how each thing here that breaks and counts as a creature stands,
    /// for each that does not stand as its room loads it: broken, or with hits taken.
    /// </summary>
    /// <param name="player">The member.</param>
    private void SendBreakingCreaturesTo(ClientPlayerData player) {
        foreach (var breakable in Object.FindObjectsByType<Breakable>(FindObjectsSortMode.None)) {
            if (breakable.persistent == null || !breakable.isBroken && breakable.hitsLeft >= breakable.hitsToBreak ||
                !CountsAsAKill(breakable.persistent)) {
                continue;
            }

            var path = ScenePath.Get(breakable.transform);
            Send(new CoopSaveUpdate {
                TargetId = player.Id,
                Kind = CoopSaveUpdateKind.BreakingCreature,
                Scene = breakable.gameObject.scene.name,
                ObjectPath = path,
                PartCount = (ushort) (breakable.isBroken ? 1 : 0),
                Amounts = [breakable.hitsLeft]
            });
            Logger.Info(
                $"[Breaks] {player.Username}, who came in, is told how '{path}' stands here: " +
                (breakable.isBroken ? "broken" : $"{breakable.hitsLeft} of {breakable.hitsToBreak} hits left")
            );
        }
    }

    /// <summary>
    /// Has a thing that breaks and counts as a creature stand as it does in the game of a member who was in the room
    /// as the local player came in: broken, the way its room loads it when it is, or with as few hits left as there.
    /// One that is broken here already, or has no more hits left, stays as it is, so that the most broken goes when
    /// more members are in the room.
    /// </summary>
    /// <param name="player">The member.</param>
    /// <param name="update">How the thing stands in their game.</param>
    private void OnBreakingCreature(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker) || !IsFromMemberInSave(player) ||
            update.Amounts.Count == 0) {
            return;
        }

        if (ScenePath.Find(update.ObjectPath, update.Scene) is not { } found ||
            !found.TryGetComponent<Breakable>(out var breakable)) {
            Logger.Info($"[Breaks] '{update.ObjectPath}' of the room of {player.Username} is not here");
            return;
        }

        var broken = update.PartCount != 0;
        var hitsLeft = update.Amounts[0];
        var there = broken ? "broken" : $"{hitsLeft} of {breakable.hitsToBreak} hits left";
        if (breakable.isBroken || !broken && hitsLeft >= breakable.hitsLeft) {
            Logger.Info(
                $"[Breaks] '{update.ObjectPath}' is {there} in the game of {player.Username}, and here " +
                (breakable.isBroken ? "broken already" : $"{breakable.hitsLeft} hits left already") + ": kept"
            );
            return;
        }

        var here = $"{breakable.hitsLeft} of {breakable.hitsToBreak} hits left";
        if (broken) {
            // What its saved value does to it when its room loads it broken (Breakable.Awake)
            breakable.isBroken = true;
            breakable.SetAlreadyBroken();
        } else {
            breakable.hitsLeft = hitsLeft;
        }

        Logger.Info(
            $"[Breaks] '{update.ObjectPath}' is {there} in the game of {player.Username}, who was in the room as the " +
            $"local player came in, and stood here with {here}: so here too"
        );
    }
}
