using System.Collections.Generic;
using System.Linq;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Save;

/// <summary>
/// Things of a room that go along a curve over and over by a clock of their own (a VectorCurveAnimator that loops),
/// like the spiked cogs that run back and forth along their rails in four rooms, 30 of them (census 10-11). Each game
/// started their clocks as its room loaded and stopped them for the moments that it held still, so the players saw a
/// cog at different points of its way: one ran through where the cog was for the other, and was hurt where the other
/// saw nothing. They keep to the clocks of the scene host's game now: the host tells every so often how far each of
/// them is along, and the other game brings its own there, as far as the host's will have got by the time the word
/// arrives. It speeds a clock up or holds it still until it is there, rather than putting it there at once, which could
/// drop a cog on the player; a game that held still since lets its clocks fall behind again, and the next word brings
/// them back.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// How often the scene host tells the clocks of its room while another player is in it, in seconds.
    /// </summary>
    private const float RoomClockTellInterval = 2f;

    /// <summary>
    /// The least that a clock here is brought to the scene host's by, in seconds: less is no more than the error of the
    /// guess of how long the word took to arrive.
    /// </summary>
    private const float RoomClockTolerance = 0.05f;

    /// <summary>
    /// When the scene host last told the clocks of its room.
    /// </summary>
    private float _roomClocksToldAt;

    /// <summary>
    /// How far each clock here still has to go to be where the scene host's is, in its own seconds: forward, or back
    /// for one that is ahead and waits.
    /// </summary>
    private readonly Dictionary<VectorCurveAnimator, float> _roomClockCatchUps = new();

    /// <summary>
    /// Brings the clocks here along to the scene host's, and in the scene host's game tells them every so often while
    /// another player is in the room.
    /// </summary>
    private void UpdateRoomClocks() {
        CatchUpRoomClocks();

        if (IsSceneHost?.Invoke() != true || !IsMemberInRoom() ||
            Time.unscaledTime - _roomClocksToldAt < RoomClockTellInterval) {
            return;
        }

        _roomClocksToldAt = Time.unscaledTime;
        var word = new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.RoomClocks,
            Values = [_netClient.UpdateManager.AverageRtt]
        };
        foreach (var (key, clock) in GetRoomClocks()) {
            word.Amounts.Add((int) key);
            word.Values.Add(clock.elapsed);
        }

        if (word.Amounts.Count > 0) {
            SendToRoomMembers(word);
        }
    }

    /// <summary>
    /// Takes the scene host's word of how far the clocks of its room are along: each clock here that is somewhere else
    /// is brought to where the host's will be by the time the word arrived, the shorter way round.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update: in <see cref="CoopSaveUpdate.Values"/> the time of the sender's game to the
    /// server and back in milliseconds, then how far each clock is along, and in <see cref="CoopSaveUpdate.Amounts"/>
    /// the clocks by their keys (see <see cref="GetRoomClocks"/>).</param>
    private void OnRoomClocks(ClientPlayerData player, CoopSaveUpdate update) {
        if (!_checkedMembers.Contains(player.Id) || IsSceneHost?.Invoke() != false || !player.IsInLocalScene ||
            update.Values.Count != update.Amounts.Count + 1) {
            return;
        }

        // How long the word took to arrive: half the time of each game to the server and back
        var age = (update.Values[0] + _netClient.UpdateManager.AverageRtt) / 2000f;
        var told = new Dictionary<uint, float>();
        for (var i = 0; i < update.Amounts.Count; i++) {
            told[(uint) update.Amounts[i]] = update.Values[i + 1];
        }

        foreach (var (key, clock) in GetRoomClocks()) {
            if (!told.TryGetValue(key, out var elapsed)) {
                continue;
            }

            var duration = clock.duration;
            var behind = Mathf.Repeat(
                elapsed + age * clock.speedMultiplier - clock.elapsed + duration / 2f, duration
            ) - duration / 2f;
            if (Mathf.Abs(behind) < RoomClockTolerance) {
                _roomClockCatchUps.Remove(clock);
            } else {
                _roomClockCatchUps[clock] = behind;
            }
        }
    }

    /// <summary>
    /// Brings each clock here that is somewhere else than the scene host's a step nearer: one that is behind goes up to
    /// twice as fast, and one that is ahead stands still, until it is there.
    /// </summary>
    private void CatchUpRoomClocks() {
        if (_roomClockCatchUps.Count == 0) {
            return;
        }

        foreach (var (clock, left) in _roomClockCatchUps.ToList()) {
            if (!IsGoingRound(clock)) {
                _roomClockCatchUps.Remove(clock);
                continue;
            }

            var most = (clock.isRealtime ? Time.unscaledDeltaTime : Time.deltaTime) * clock.speedMultiplier;
            var step = Mathf.Clamp(left, -most, most);
            clock.elapsed = Mathf.Repeat(clock.elapsed + step, clock.duration);
            if (Mathf.Abs(left - step) < 0.001f) {
                _roomClockCatchUps.Remove(clock);
            } else {
                _roomClockCatchUps[clock] = left - step;
            }
        }
    }

    /// <summary>
    /// Whether a clock is still there and going round.
    /// </summary>
    private static bool IsGoingRound(VectorCurveAnimator clock) {
        return clock != null && clock.isAnimating && clock.duration > 0f;
    }

    /// <summary>
    /// The clocks of the room here that go round: those of its objects that are switched on, loop and have started,
    /// each by a key that is the same in every game, the hash of its scene and its path in it. Two that share a key
    /// are left out, as nothing tells them apart.
    /// </summary>
    private static List<(uint Key, VectorCurveAnimator Clock)> GetRoomClocks() {
        var room = SceneUtil.GetCurrentSceneName();
        var clocks = new Dictionary<uint, VectorCurveAnimator?>();
        foreach (var clock in Object.FindObjectsByType<VectorCurveAnimator>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None
                 )) {
            if (!clock.loop || !clock.isAnimating || clock.delayLeft > 0f || clock.duration <= 0f ||
                !IsPartOfRoom(clock.gameObject.scene.name, room)) {
                continue;
            }

            var key = HashText(clock.gameObject.scene.name + ":" + ScenePath.Get(clock.transform));
            clocks[key] = clocks.ContainsKey(key) ? null : clock;
        }

        var found = new List<(uint, VectorCurveAnimator)>();
        foreach (var (key, clock) in clocks) {
            if (clock != null) {
                found.Add((key, clock));
            }
        }

        return found;
    }

    /// <summary>
    /// The FNV-1a hash of a text, which is the same in every game, unlike the hash of a string in .NET.
    /// </summary>
    private static uint HashText(string text) {
        var hash = 2166136261u;
        foreach (var character in text) {
            hash = (hash ^ character) * 16777619u;
        }

        return hash;
    }
}
