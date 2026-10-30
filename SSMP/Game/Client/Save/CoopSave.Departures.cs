using System;
using System.Collections.Generic;
using System.Linq;
using SSMP.Networking.Packet.Data;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Characters that leave for good once a player has had what tells them they will: a talk, or seeing them where they
/// are. The game lets them go when time passes (GameManager.TimePasses) and only then: their leaving is a flag of the
/// world, which every member shares, while what lets it happen is each player's own. Shared from whichever game let
/// time pass first, a character went from a member who had not had it yet, with whatever of theirs that member had not
/// bought or heard yet (USER 10-30: the craftsman left after the partner bought from it, "我现在少一个她的商品，买不了
/// 了", and "联机应该看所有人的状态而不是房主的状态").
///
/// Such a character now leaves only once every member has had it: until then the flag of the leaving stays off in
/// every game, and the first time that passes after the last of them had it lets them go for everyone. Each game sends
/// the others what its own player has had (<see cref="CoopSaveUpdateKind.DepartureReadiness"/>), and holds the flag off
/// as long as any player it knows of has not had it. A member whose game has not said yet holds nothing back. A leaving
/// that a save already had when the saves were checked is done and stays (<see cref="_departuresDone"/>): only one that
/// comes while the members play together is held back.
///
/// Only characters that are gone for good: one that moves on to another place keeps what they sell and say there, and
/// held back they would stand in both places at once. The list is every leaving in GameManager.TimePasses that a
/// player's own flag lets happen and that nothing else in the game sets.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The flags of the characters that leave for good, each with the flag of a player's own that lets them.
    /// </summary>
    private static readonly (string Left, string Ready)[] Departures = [
        ("ArchitectLeft", "ArchitectWillLeave"),
        ("BoneBottomShopKeepLeft", "BoneBottomShopKeepWillLeave"),
        ("HalfwayPatronLeftGone", "SeenHalfwayPatronLeft"),
        ("HalfwayPatronRightGone", "SeenHalfwayPatronRight")
    ];

    /// <summary>
    /// What each member's own player has had of what lets the characters leave, by the member, as their game last said.
    /// </summary>
    private readonly Dictionary<ushort, Dictionary<string, bool>> _memberDepartureReadiness = new();

    /// <summary>
    /// What the local player has had of it, as the local game last sent it, or null before it sent anything since the
    /// check.
    /// </summary>
    private Dictionary<string, bool>? _sentDepartureReadiness;

    /// <summary>
    /// The members that the local game sent it to since the check, so that one who came in later gets it too.
    /// </summary>
    private readonly HashSet<ushort> _departureReadinessSentTo = [];

    /// <summary>
    /// The characters that had already left when the saves were last checked, in any of them: their leaving is done
    /// and stays, however many members had not had what lets it. Players may have walked through what the leaving
    /// left behind and taken it (USER 10-30: "可是这个剧情机械师已经走了，我们都捡到它的遗物了，它还能回来吗"), and a
    /// character back from it would stand next to what of theirs the players hold. Only a leaving that comes while the
    /// members play together is held back.
    /// </summary>
    private readonly HashSet<string> _departuresDone = new(StringComparer.Ordinal);

    /// <summary>
    /// Forgets what the members had, for a new session, after which every game sends it again.
    /// </summary>
    private void ResetDepartures() {
        _memberDepartureReadiness.Clear();
        _sentDepartureReadiness = null;
        _departureReadinessSentTo.Clear();
        _departuresDone.Clear();
    }

    /// <summary>
    /// After a check: what the local player has had goes to every member again, and every character that has left by
    /// now in the save that the check agreed on stays gone (<see cref="_departuresDone"/>).
    /// </summary>
    private void OnDeparturesChecked() {
        _sentDepartureReadiness = null;
        _departureReadinessSentTo.Clear();

        var playerData = PlayerData.instance;
        if (playerData == null) {
            return;
        }

        foreach (var (left, _) in Departures) {
            if (playerData.GetBool(left) && _departuresDone.Add(left)) {
                Logger.Info($"[Departures] {left} was already on at the check: that leaving is done and stays");
            }
        }
    }

    /// <summary>
    /// Forgets what a member that left had: their game no longer says, so they hold nothing back until it does again.
    /// </summary>
    /// <param name="id">The ID of the member.</param>
    private void ForgetDepartureReadinessOf(ushort id) {
        _memberDepartureReadiness.Remove(id);
        _departureReadinessSentTo.Remove(id);
    }

    /// <summary>
    /// Sends the other members what the local player has had of what lets the characters leave: when it changed, and
    /// to a member who was not sent it yet.
    /// </summary>
    private void SendDepartureReadiness() {
        var playerData = PlayerData.instance;
        if (playerData == null) {
            return;
        }

        var readiness = new Dictionary<string, bool>();
        foreach (var (_, ready) in Departures) {
            readiness[ready] = playerData.GetBool(ready);
        }

        var changed = _sentDepartureReadiness == null ||
                      readiness.Any(pair => _sentDepartureReadiness[pair.Key] != pair.Value);
        if (!changed && _checkedMembers.All(_departureReadinessSentTo.Contains)) {
            return;
        }

        var update = new CoopSaveUpdate { Kind = CoopSaveUpdateKind.DepartureReadiness };
        foreach (var pair in readiness) {
            update.FlagNames.Add(pair.Key);
            update.FlagValues.Add(pair.Value ? 1 : 0);
        }

        SendToMembers(update);
        _sentDepartureReadiness = readiness;
        _departureReadinessSentTo.Clear();
        _departureReadinessSentTo.UnionWith(_checkedMembers);
        Logger.Info(
            $"[Departures] Sent what this player has had to {GetCheckedNames()}: " +
            string.Join(", ", readiness.Select(pair => $"{pair.Key}={pair.Value}"))
        );
    }

    /// <summary>
    /// Takes what another member's player has had of what lets the characters leave, and holds back a leaving that they
    /// have not had yet.
    /// </summary>
    /// <param name="player">The member.</param>
    /// <param name="update">The update, with the flags and their values.</param>
    private void OnDepartureReadiness(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker) || !IsFromMemberInSave(player)) {
            return;
        }

        var readiness = new Dictionary<string, bool>();
        for (var i = 0; i < update.FlagNames.Count && i < update.FlagValues.Count; i++) {
            readiness[update.FlagNames[i]] = update.FlagValues[i] != 0;
        }

        _memberDepartureReadiness[player.Id] = readiness;
        Logger.Info(
            $"[Departures] {player.Username} has had: " +
            string.Join(", ", readiness.Select(pair => $"{pair.Key}={pair.Value}"))
        );

        HoldDepartures($"{player.Username} said what they have had");
    }

    /// <summary>
    /// Holds off each character's leaving that a player has not had what lets it yet: the local player, or a member
    /// whose game said so. The flag goes back off in the local save, and from there to the other members as any change
    /// of the story does.
    /// </summary>
    /// <param name="when">What it is done after, for the log.</param>
    private void HoldDepartures(string when) {
        try {
            HoldDeparturesNow(when);
        } catch (Exception e) {
            // It runs inside the check, the game's passing of time and the taking of the partner's changes, none of
            // which it may stop
            if (!_departuresFailed) {
                _departuresFailed = true;
                Logger.Error($"[Departures] Could not hold back the leaving of characters:\n{e}");
            }
        }
    }

    /// <summary>
    /// Whether holding back the leaving of characters threw, which is only logged once.
    /// </summary>
    private bool _departuresFailed;

    /// <inheritdoc cref="HoldDepartures" />
    private void HoldDeparturesNow(string when) {
        // Only between checks of a session: before the first one, nothing says yet which leavings are done
        var playerData = PlayerData.instance;
        if (playerData == null || GetCurrentMarker() == null || _knownStoryValues == null) {
            return;
        }

        foreach (var (left, ready) in Departures) {
            if (_departuresDone.Contains(left) || !playerData.GetBool(left)) {
                continue;
            }

            var waiting = new List<string>();
            if (!playerData.GetBool(ready)) {
                waiting.Add("this player");
            }

            foreach (var id in _checkedMembers) {
                if (_memberDepartureReadiness.TryGetValue(id, out var readiness) &&
                    readiness.TryGetValue(ready, out var had) && !had) {
                    waiting.Add(_playerData.TryGetValue(id, out var member) ? member.Username : $"player {id}");
                }
            }

            if (waiting.Count == 0) {
                continue;
            }

            playerData.SetBool(left, false);
            Logger.Info(
                $"[Departures] {left} put back off after {when}: {JoinNames(waiting)} did not have {ready} yet, " +
                "and the character leaves only once everyone did"
            );
        }
    }
}
