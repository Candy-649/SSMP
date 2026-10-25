using System;
using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Rings that a player hangs on with the harpoon to pull something down, in a checked two-player save: a switch on a
/// chain that comes down while someone hangs on its ring and then opens a door or starts a fight (its "Pull Control"
/// FSM, told by the "Proxy" FSM of the chain), and the ring of a lock that drops and opens a gate once someone has hung
/// on it for a moment (HarpoonRingSlideLock). The ring tells what it hangs from only of its own game's hero taking hold
/// of it and letting go, so the partner hung on a ring that stayed up here, and what it opened stayed shut (USER 10-14,
/// "飞针到之后会下拉的拉环没有同步"). Now each game sends its own player taking hold and letting go, and the other games
/// hold the ring for them the same way, a moment later: what it hangs from is held while any player holds the ring
/// here, and let go once the last of them did, like a plate that several players stand on (see OnPlateTouch). What the
/// ring itself shows of a hold shows to the other players as a look (see HarpoonRingLook); the sounds of the grab are the
/// holder's alone. Rings that carry a player along a rail or up and down are not these.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The FSM of the chain of a pull switch that passes its ring being taken hold of and let go of on to the switch.
    /// </summary>
    private const string RingProxyFsmName = "Proxy";

    /// <summary>
    /// The FSM of a pull switch, which comes down while its ring is held.
    /// </summary>
    private const string PullControlFsmName = "Pull Control";

    /// <summary>
    /// The variable of <see cref="RingProxyFsmName"/> that holds the switch it tells.
    /// </summary>
    private const string RingProxyTargetName = "Event Target";

    /// <summary>
    /// The events, and the states of <see cref="RingProxyFsmName"/> that they lead to, of a ring taken hold of and let
    /// go of.
    /// </summary>
    private const string RingConnectName = "CONNECT", RingDisconnectName = "DISCONNECT";

    /// <summary>
    /// What an update of a ring says in its StateName for letting go of it; it says nothing for taking hold.
    /// </summary>
    private const string RingLetGoName = "Left";

    /// <summary>
    /// What the rings hang from that the local player holds here, as it heard them.
    /// </summary>
    private readonly HashSet<GameObject> _localRingHolds = new();

    /// <summary>
    /// The members who hold each ring here in their own games, by what it hangs from: it is held for them until the
    /// last of them let go (see <see cref="OnRingHold"/>).
    /// </summary>
    private readonly Dictionary<GameObject, HashSet<ushort>> _ringHolders = new();

    /// <summary>
    /// Whether a ring is being held or let go of here for a member, which is not sent back to them.
    /// </summary>
    private bool _replayingRingHold;

    /// <summary>
    /// Registers the hooks on the rings of locks being held and let go of. The chains of pull switches are followed in
    /// the hook on FSMs changing state (see <see cref="OnRingProxySwitch"/>).
    /// </summary>
    private void RegisterRingHooks() {
        foreach (var holding in (bool[]) [true, false]) {
            AddInteractionHook(
                typeof(HarpoonRingSlideLock).GetMethod(
                    holding ? "HeroOnRing" : "HeroOffRing", InstanceFlags, null, Type.EmptyTypes, null
                ),
                new Action<Action<HarpoonRingSlideLock>, HarpoonRingSlideLock>((orig, self) => {
                    if (_replayingRingHold || OnLocalRingHold(self.gameObject, holding)) {
                        orig(self);
                    }
                })
            );
        }
    }

    /// <summary>
    /// Follows the chain of a pull switch hearing the local player take hold of its ring or let go of it, from the
    /// hook on FSMs changing state.
    /// </summary>
    /// <returns>Whether the change of state is held: a letting go while a member still holds the ring here.</returns>
    private bool OnRingProxySwitch(Fsm fsm, FsmState toState) {
        if (_replayingRingHold || toState.Name is not (RingConnectName or RingDisconnectName) ||
            fsm.Name != RingProxyFsmName || !IsPullSwitchChain(fsm)) {
            return false;
        }

        // Only the local player lets go of a ring that they hold: the switch tells its chain the same once it is back
        // up after a pull, and as a room loads where it was pulled before, which is nobody's letting go
        var holding = toState.Name == RingConnectName;
        if (!holding && !_localRingHolds.Contains(fsm.GameObject)) {
            return false;
        }

        return !OnLocalRingHold(fsm.GameObject, holding);
    }

    /// <summary>
    /// Whether an FSM is the one of the chain of a pull switch that tells the switch of its ring.
    /// </summary>
    private static bool IsPullSwitchChain(Fsm fsm) {
        return fsm.Variables.FindFsmGameObject(RingProxyTargetName)?.Value is { } target && target != null &&
               target.GetComponents<PlayMakerFSM>().Any(other => other.FsmName == PullControlFsmName);
    }

    /// <summary>
    /// The local player took hold of a ring or let go of it, as what it hangs from heard it: sent to the members.
    /// </summary>
    /// <param name="holder">What the ring hangs from.</param>
    /// <param name="holding">Whether the player took hold of it.</param>
    /// <returns>Whether it goes on here: letting go does not while a member still holds the ring here.</returns>
    private bool OnLocalRingHold(GameObject holder, bool holding) {
        if (holding) {
            _localRingHolds.Add(holder);
        } else {
            _localRingHolds.Remove(holder);
        }

        if (_checkedMembers.Count > 0) {
            var path = ScenePath.Get(holder.transform);
            SendToMembers(new CoopSaveUpdate {
                Kind = CoopSaveUpdateKind.RingHold,
                Scene = holder.scene.name,
                ObjectPath = path,
                StateName = holding ? "" : RingLetGoName
            });
            Logger.Info($"Sent {(holding ? "taking hold of" : "letting go of")} the ring of '{path}' to the partner");
        }

        if (holding || !_ringHolders.TryGetValue(holder, out var holders) || holders.Count == 0) {
            return true;
        }

        Logger.Info($"The ring of '{holder.name}' stays held here for {holders.Count} member(s) who still hold it");
        return false;
    }

    /// <summary>
    /// A member took hold of a ring or let go of it in their game. What it hangs from here is held for them, and let go
    /// once nobody holds the ring here any more.
    /// </summary>
    /// <param name="player">The member.</param>
    /// <param name="update">The update, which names what the ring hangs from by its path in its scene.</param>
    private void OnRingHold(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsMember(player, marker) ||
            !_checkedMembers.Contains(player.Id) ||
            ScenePath.Find(update.ObjectPath, update.Scene) is not { activeInHierarchy: true } holder) {
            return;
        }

        if (!_ringHolders.TryGetValue(holder, out var holders)) {
            holders = _ringHolders[holder] = [];
        }

        var holding = update.StateName != RingLetGoName;
        var wasHeld = holders.Count > 0 || _localRingHolds.Contains(holder);
        if (!(holding ? holders.Add(player.Id) : holders.Remove(player.Id))) {
            return;
        }

        var isHeld = holders.Count > 0 || _localRingHolds.Contains(holder);
        if (isHeld != wasHeld) {
            HoldRing(holder, isHeld, player.Username);
        }
    }

    /// <summary>
    /// Takes hold of a ring here for members, or lets go of it, the way the ring tells what it hangs from.
    /// </summary>
    /// <param name="holder">What the ring hangs from.</param>
    /// <param name="holding">Whether to take hold of it.</param>
    /// <param name="who">Who took hold of it or let go last, for the log.</param>
    private void HoldRing(GameObject holder, bool holding, string who) {
        _replayingRingHold = true;
        try {
            if (holder.TryGetComponent<HarpoonRingSlideLock>(out var slideLock)) {
                if (holding) {
                    slideLock.HeroOnRing();
                } else {
                    slideLock.HeroOffRing();
                }
            } else if (holder.GetComponents<PlayMakerFSM>().FirstOrDefault(fsm => fsm.FsmName == RingProxyFsmName)
                       is { } proxy) {
                proxy.SendEvent(holding ? RingConnectName : RingDisconnectName);
            } else {
                return;
            }
        } catch (Exception e) {
            Logger.Warn($"Could not {(holding ? "hold" : "let go of")} the ring of '{holder.name}': {e.Message}");
            return;
        } finally {
            _replayingRingHold = false;
        }

        Logger.Info(
            holding
                ? $"{who} holds the ring of '{holder.name}', so it is held here too"
                : $"{who} let go of the ring of '{holder.name}' last, so it is let go here too"
        );
    }

    /// <summary>
    /// Lets go of the rings that a member held here, for a member who left the room or the save: one that nobody else
    /// holds here any more is let go.
    /// </summary>
    /// <param name="id">The ID of the member.</param>
    /// <param name="name">The name of the member, for the log.</param>
    private void ForgetRingHoldsOf(ushort id, string name) {
        foreach (var pair in _ringHolders.ToList()) {
            if (!pair.Value.Remove(id) || pair.Value.Count > 0 || pair.Key == null ||
                _localRingHolds.Contains(pair.Key)) {
                continue;
            }

            HoldRing(pair.Key, false, name);
        }
    }

    /// <summary>
    /// A player left the room: a ring that they held here is let go for them, as nothing comes of it from their game
    /// any more.
    /// </summary>
    /// <param name="player">The player.</param>
    internal void OnPlayerLeaveScene(ClientPlayerData player) {
        try {
            ForgetRingHoldsOf(player.Id, player.Username);
        } catch (Exception e) {
            Logger.Warn($"Could not let go of the rings that {player.Username} held: {e.Message}");
        }

        // Nobody stands on a trap plate of a room they left, should their getting off it never have come
        ForgetTrapPlatesOf(player.Id);
    }

    /// <summary>
    /// Lets go of every plate that members stand on here and every ring that they hold here, for a session that ends
    /// while the room stays: nothing comes of them from their games any more.
    /// </summary>
    private void LetGoOfMemberHolds() {
        try {
            var holders = _platesStoodOn.Values.SelectMany(ids => ids)
                .Concat(_trapPlatesStoodOn.Values.SelectMany(ids => ids))
                .Concat(_ringHolders.Values.SelectMany(ids => ids))
                .Distinct()
                .ToList();
            foreach (var id in holders) {
                ForgetPlatesOf(id);
                ForgetRingHoldsOf(id, _playerData.TryGetValue(id, out var player) ? player.Username : "?");
            }
        } catch (Exception e) {
            Logger.Warn($"Could not let go of what members held down in the room: {e.Message}");
        }
    }

    /// <summary>
    /// Forgets the rings held in the room, for a new room.
    /// </summary>
    private void ResetRingHolds() {
        _localRingHolds.Clear();
        _ringHolders.Clear();
    }
}
