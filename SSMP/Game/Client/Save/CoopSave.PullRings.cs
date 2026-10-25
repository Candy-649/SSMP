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
///
/// A pole that bends while its ring is held, and throws whoever holds it off once it bent as far as it goes, bent only in
/// the holder's game. Its bend is one for all who hold it (USER 10-25: "杆子弯了的话，这是一种进度，挂上去之后肯定要掉下去是一起
/// 掉下去呀"): it is held like the others, and the hook that sets it bending in one game sets it bending the same way in
/// the others, so a player who takes hold of it later hangs on the bend that it has, it springs back only once the last
/// of them let go, and as it bent as far as it goes each game throws its own player off, if they are on it, also one who
/// was still on the way to its ring. A member who walks in is sent the holds, and where two hooks set it bending from
/// either side at the same moment, it bends the scene host's way in both. A rope that a ring hangs from swung only for
/// the pushes of the local hero's hold: their needle hooking the ring, taking hold of it and letting go of it. Those now
/// push it in every game, the way they pushed it, so that it swings for both ("同理绳子，为啥不是一起晃"); a strike on its
/// ring pushes it in every game already, as the strike of another player is played on it there.
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
    /// What an update of a ring says in its StateName for letting go of it; it says nothing for taking hold, but for
    /// hooking the ring of a pole that the hook set bending in the sender's game, the way it bends
    /// (<see cref="PoleConnectLeftName"/> or <see cref="PoleConnectRightName"/>).
    /// </summary>
    private const string RingLetGoName = "Left";

    /// <summary>
    /// The FSM of a ring that the harpoon hooks, and of a pole that bends while its ring is held.
    /// </summary>
    private const string RingControlFsmName = "Control";

    /// <summary>
    /// The states of a ring that the local player's needle hooks it in, from the left and from the right, the one that
    /// a dash catches it in without hooking it, which tells its pole nothing, and the one it goes to once the player let
    /// go of it, which tells what it hangs from that it was let go of.
    /// </summary>
    private const string RingHookedLeftState = "Connect L",
        RingHookedRightState = "Connect R",
        RingCaughtState = "Quick Grab",
        RingLetGoState = "Cooldown";

    /// <summary>
    /// The variable of a ring that is true while the local hero is on it, from its hook or catch to its letting go.
    /// </summary>
    private const string RingConnectedName = "Is Connected";

    /// <summary>
    /// The events that a ring tells the pole it is on as the needle hooks it from the left and from the right, which bend
    /// it the one way and the other.
    /// </summary>
    private const string PoleConnectLeftName = "CONNECT L", PoleConnectRightName = "CONNECT R";

    /// <summary>
    /// The states of a pole: the pause before it bends, bending, bent as far as it goes, throwing the hero off, springing
    /// back, and at rest.
    /// </summary>
    private const string PolePauseState = "Connect Pause",
        PoleBendState = "Rotate",
        PoleStrainState = "Max Strain",
        PoleThrowState = "Hit Max",
        PoleReboundState = "Rebound",
        PoleRestState = "Idle";

    /// <summary>
    /// The variables of a pole: how fast it bends, which way it speeds up to, and what turns as it bends.
    /// </summary>
    private const string PoleSpeedName = "Rotate Speed", PoleAccelName = "Rotate Accel", PolePivotName = "Pivot";

    /// <summary>
    /// The FSM of the hero's harpoon, and the event that a pole that bent as far as it goes tells the hero, which throws
    /// the hero off the ring they hang on, but only once they hang on it: while the needle pulls them to it, or as they
    /// catch it, the hero doesn't hear it.
    /// </summary>
    private const string HeroHarpoonFsmName = "Harpoon Dash", ThrowOffName = "FORCE DISCONNECT";

    /// <summary>
    /// A pole that bent as far as it goes while the local hero was still on the way to its ring, which throws them off
    /// as soon as they hang on it (see <see cref="UpdatePendingPoleThrow"/>).
    /// </summary>
    private GameObject? _pendingPoleThrow;

    /// <summary>
    /// The harpoon FSM of the local hero, found when first needed.
    /// </summary>
    private PlayMakerFSM? _heroHarpoonFsm;

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
    /// Registers the hooks on the rings of locks being held and let go of, and on ropes being pushed. The chains of pull
    /// switches and the poles are followed in the hook on FSMs changing state (see <see cref="OnRingProxySwitch"/> and
    /// <see cref="OnRingPoleSwitch"/>).
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

        // The pushes on a rope of the needle hooking its ring, taking hold of it and letting go of it, which only the
        // local hero's own hold of the ring makes. A strike on the ring pushes it in each game already, as the strike
        // of another player is played on it there
        foreach (var (name, impulse) in RopeHoldPushes) {
            AddInteractionHook(
                typeof(HarpoonRingChain).GetMethod(name, InstanceFlags, null, Type.EmptyTypes, null),
                new Action<Action<HarpoonRingChain>, HarpoonRingChain>(
                    (orig, self) => OnRopePush(orig, self, impulse(self))
                )
            );
        }
    }

    /// <summary>
    /// The methods of a rope that push it as the local hero takes hold of its ring, with the push of each, which the
    /// rope turns away from the hero.
    /// </summary>
    private static readonly (string, Func<HarpoonRingChain, Vector2>)[] RopeHoldPushes = [
        ("HeroNeedleConnect", chain => chain.needleConnectImpulse),
        ("HeroOnRing", chain => chain.heroAttachImpulse),
        ("HeroOffRing", chain => chain.heroLeaveImpulse)
    ];

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
    /// Follows the poles that bend while their ring is held, from the hook on FSMs changing state: the local player's
    /// needle hooking the ring of a pole or a dash catching it, and letting go of it, as what it hangs from heard them,
    /// and a pole that would spring back as the local player let go of it while a member still holds it here.
    /// </summary>
    /// <returns>Whether the change of state is held: the pole goes on bending for the member.</returns>
    private bool OnRingPoleSwitch(Fsm fsm, FsmState toState) {
        if (_replayingRingHold || fsm.Name != RingControlFsmName) {
            return false;
        }

        switch (toState.Name) {
            case RingHookedLeftState or RingHookedRightState when PoleOfRing(fsm) is { } pole:
                // A hook sets the pole bending only where it is at rest or springing back, which the other games are
                // told the way it bends; one that comes while it bends hangs on the bend that it has, there too
                var side = toState.Name == RingHookedLeftState ? PoleConnectLeftName : PoleConnectRightName;
                OnLocalRingHold(pole, true, PoleControlOf(pole) is { } poleFsm && TakesEvent(poleFsm, side) ? side : "");
                return false;
            case RingCaughtState when PoleOfRing(fsm) is { } pole:
                OnLocalRingHold(pole, true);
                return false;
            case RingLetGoState when _localRingHolds.Count > 0 && PoleOfRing(fsm) is { } pole &&
                                     _localRingHolds.Contains(pole):
                // The ring itself rests whoever else still holds it; the pole is held as it hears the letting go
                OnLocalRingHold(pole, false);
                return false;
            case PoleReboundState when fsm.ActiveStateName is PoleBendState or PoleStrainState:
            case PoleRestState when fsm.ActiveStateName == PolePauseState:
                if (!IsPole(fsm) || !_ringHolders.TryGetValue(fsm.GameObject, out var holders) || holders.Count == 0) {
                    return false;
                }

                Logger.Info($"The pole '{fsm.GameObject.name}' stays bent here for {holders.Count} member(s) who hold it");
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// A pole that bent as far as it goes throws the hero off by telling the hero itself, whatever ring the hero holds.
    /// Here it throws the local player off only if they are on its ring: one that bent here for members alone springs
    /// back without a word to the local hero. A local hero still on the way to its ring, who doesn't hear it yet, is
    /// thrown off as soon as they hang on it (see <see cref="UpdatePendingPoleThrow"/>).
    /// </summary>
    /// <returns>The state to switch to.</returns>
    private FsmState RedirectPoleThrow(Fsm fsm, FsmState toState) {
        if (toState.Name != PoleThrowState || fsm.Name != RingControlFsmName || !IsPole(fsm)) {
            return toState;
        }

        var pole = fsm.GameObject;
        if (!IsLocalHeroOnPole(pole)) {
            if (fsm.GetState(PoleReboundState) is not { } rebound) {
                return toState;
            }

            Logger.Info($"The pole '{pole.name}' bent as far as it goes for members alone, so it springs back");
            return rebound;
        }

        if (HeroHarpoonFsm() is { } harpoon && !TakesEvent(harpoon, ThrowOffName)) {
            _pendingPoleThrow = pole;
            Logger.Info($"The pole '{pole.name}' bent as far as it goes before the hero hung on it, who falls off on it");
        }

        return toState;
    }

    /// <summary>
    /// Throws the local hero off the ring of a pole that bent as far as it goes while they were still on the way to it,
    /// once they hang on it and hear it, as the pole would have: they hooked it after the others, who hang on the same
    /// bend and were thrown off with it. Forgotten once they let go of it.
    /// </summary>
    private void UpdatePendingPoleThrow() {
        if (_pendingPoleThrow == null) {
            return;
        }

        if (!IsLocalHeroOnPole(_pendingPoleThrow)) {
            _pendingPoleThrow = null;
            return;
        }

        if (HeroHarpoonFsm() is not { } harpoon || !TakesEvent(harpoon, ThrowOffName)) {
            return;
        }

        Logger.Info($"The hero hangs on the pole '{_pendingPoleThrow.name}', which bent as far as it goes, so falls off");
        _pendingPoleThrow = null;
        harpoon.SendEvent(ThrowOffName);
    }

    /// <summary>
    /// Whether the local hero is on a ring of a pole, from the moment their needle hooked it or a dash caught it until
    /// they let go of it.
    /// </summary>
    private bool IsLocalHeroOnPole(GameObject pole) {
        if (_localRingHolds.Contains(pole)) {
            return true;
        }

        // What the rings say themselves, for a hold that began before it was followed
        foreach (var fsm in pole.GetComponentsInChildren<PlayMakerFSM>()) {
            if (fsm.FsmName == RingControlFsmName && fsm.FsmVariables.FindFsmBool(RingConnectedName) is { Value: true }) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The harpoon FSM of the local hero, or null for none.
    /// </summary>
    private PlayMakerFSM? HeroHarpoonFsm() {
        var hero = HeroController.instance;
        if (hero == null) {
            return null;
        }

        if (_heroHarpoonFsm == null || _heroHarpoonFsm.gameObject != hero.gameObject) {
            _heroHarpoonFsm = hero.GetComponents<PlayMakerFSM>().FirstOrDefault(
                fsm => fsm.FsmName == HeroHarpoonFsmName
            );
        }

        return _heroHarpoonFsm;
    }

    /// <summary>
    /// Whether an FSM changes state on an event in the state that it is in now.
    /// </summary>
    private static bool TakesEvent(PlayMakerFSM fsm, string eventName) {
        if (fsm.Fsm?.ActiveState is not { } state) {
            return false;
        }

        foreach (var transition in state.Transitions) {
            if (transition.EventName == eventName) {
                return true;
            }
        }

        foreach (var transition in fsm.Fsm.GlobalTransitions) {
            if (transition.EventName == eventName) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The pole that the ring of an FSM is on, which bends while the ring is held, or null for a ring on none.
    /// </summary>
    private static GameObject? PoleOfRing(Fsm ringFsm) {
        return ringFsm.GameObject != null && ringFsm.GameObject.GetComponentInParent<HarpoonRingPoleConfigure>() is
            { } pole
            ? pole.gameObject
            : null;
    }

    /// <summary>
    /// Whether an FSM is the one of a pole that bends while its ring is held.
    /// </summary>
    private static bool IsPole(Fsm fsm) {
        return fsm.GameObject != null && fsm.GameObject.TryGetComponent<HarpoonRingPoleConfigure>(out _);
    }

    /// <summary>
    /// The FSM that bends a pole, for what a ring hangs from that is a pole, or null for anything else.
    /// </summary>
    private static PlayMakerFSM? PoleControlOf(GameObject holder) {
        return holder.TryGetComponent<HarpoonRingPoleConfigure>(out _)
            ? holder.GetComponents<PlayMakerFSM>().FirstOrDefault(fsm => fsm.FsmName == RingControlFsmName)
            : null;
    }

    /// <summary>
    /// A member hooked the ring of a pole, caught it or let go of it in their game, which is followed here as it is
    /// held for them: a hook that set the pole bending there sets it bending here too, the same way, where it is at
    /// rest or springing back. One that comes while the pole bends here doesn't start it again, but where both
    /// players' hooks set it bending at the same moment from either side, the scene client's game turns its bend the
    /// way of the scene host's while it has hardly begun, so that it bends one way in both. Once the last of the players
    /// who hold it let go, it springs back.
    /// </summary>
    private void OnPoleHold(
        ClientPlayerData player,
        GameObject holder,
        PlayMakerFSM pole,
        bool holding,
        string side,
        HashSet<ushort> holders
    ) {
        if (!holding) {
            if (holders.Count == 0 && !_localRingHolds.Contains(holder)) {
                HoldRing(holder, false, player.Username);
            }

            return;
        }

        if (side is not (PoleConnectLeftName or PoleConnectRightName)) {
            return;
        }

        if (TakesEvent(pole, side)) {
            HoldRing(holder, true, player.Username, side);
            return;
        }

        if (pole.ActiveStateName is not (PolePauseState or PoleBendState) || IsSceneClient?.Invoke() != true ||
            CountMembersInRoom() != 1 || !TurnPoleBend(pole, side)) {
            return;
        }

        Logger.Info($"{player.Username} hooked the pole '{holder.name}' as it began to bend, so it bends their way");
    }

    /// <summary>
    /// The members who are in the local room.
    /// </summary>
    private int CountMembersInRoom() {
        var count = 0;
        foreach (var id in _checkedMembers) {
            if (_playerData.TryGetValue(id, out var player) && player.IsInLocalScene) {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Turns the bend of a pole that has hardly begun the other way, as if it had been hooked from the other side: what
    /// it speeds up to, how fast it bends and where it has got to so far, mirrored.
    /// </summary>
    /// <returns>Whether it bent the other way before.</returns>
    private static bool TurnPoleBend(PlayMakerFSM pole, string side) {
        var variables = pole.FsmVariables;
        if (variables.FindFsmFloat(PoleAccelName) is not { } accel || variables.FindFsmFloat(PoleSpeedName) is not
                { } speed || accel.Value == 0f || accel.Value > 0f == (side == PoleConnectLeftName)) {
            return false;
        }

        accel.Value = -accel.Value;
        speed.Value = -speed.Value;

        // Only once it bends: in the pause before, it is still where the hook found it
        if (pole.ActiveStateName == PoleBendState && variables.FindFsmGameObject(PolePivotName)?.Value is { } pivot &&
            pivot != null) {
            var angles = pivot.transform.localEulerAngles;
            pivot.transform.localEulerAngles = new Vector3(angles.x, angles.y, -angles.z);
        }

        return true;
    }

    /// <summary>
    /// Sends a member who walked into the room what the local player holds here, which was only sent as they took hold:
    /// a pole that the local player hangs on bends in the member's game too, the way it bends here.
    /// </summary>
    /// <param name="player">The member who walked in.</param>
    private void SendRingHoldsTo(ClientPlayerData player) {
        foreach (var holder in _localRingHolds) {
            if (holder == null) {
                continue;
            }

            var side = "";
            if (PoleControlOf(holder) is { } pole &&
                pole.ActiveStateName is PolePauseState or PoleBendState or PoleStrainState &&
                pole.FsmVariables.FindFsmFloat(PoleAccelName) is { } accel && accel.Value != 0f) {
                side = accel.Value > 0f ? PoleConnectLeftName : PoleConnectRightName;
            }

            var path = ScenePath.Get(holder.transform);
            Send(new CoopSaveUpdate {
                TargetId = player.Id,
                Kind = CoopSaveUpdateKind.RingHold,
                Scene = holder.scene.name,
                ObjectPath = path,
                StateName = side
            });
            Logger.Info($"Sent the ring of '{path}', which the local player holds, to {player.Username}, who came in");
        }
    }

    /// <summary>
    /// The local hero hooked the ring of a rope, took hold of it or let go of it, which pushes the rope: it is pushed
    /// here, and the push is sent to the members the way it pushed, which the game turns away from the side of its own
    /// hero.
    /// </summary>
    private void OnRopePush(Action<HarpoonRingChain> orig, HarpoonRingChain self, Vector2 impulse) {
        var body = self.ringBody;
        var hero = HeroController.instance;
        var push = impulse;
        if (body != null && hero != null && hero.transform.position.x > body.position.x) {
            push.x *= -1f;
        }

        orig(self);
        if (_checkedMembers.Count == 0 || body == null) {
            return;
        }

        try {
            SendToMembers(new CoopSaveUpdate {
                Kind = CoopSaveUpdateKind.RopePush,
                Scene = self.gameObject.scene.name,
                ObjectPath = ScenePath.Get(self.transform),
                Values = [push.x, push.y]
            });
        } catch (Exception e) {
            Logger.Warn($"Could not send a push on the rope '{self.name}': {e.Message}");
        }
    }

    /// <summary>
    /// A member pushed a rope that a ring hangs from in their game: it is pushed the same way here.
    /// </summary>
    /// <param name="player">The member.</param>
    /// <param name="update">The update, which names the rope by its path in its scene and holds the push.</param>
    private void OnRopePushed(ClientPlayerData player, CoopSaveUpdate update) {
        if (update.Values.Count < 2 || GetCurrentMarker() is not { } marker || !IsMember(player, marker) ||
            !_checkedMembers.Contains(player.Id) ||
            ScenePath.Find(update.ObjectPath, update.Scene) is not { activeInHierarchy: true } rope ||
            !rope.TryGetComponent<HarpoonRingChain>(out var chain) || chain.ringBody == null) {
            return;
        }

        chain.ringBody.AddForce(new Vector2(update.Values[0], update.Values[1]), ForceMode2D.Impulse);
        Logger.Info($"{player.Username} pushed the rope '{rope.name}', so it is pushed here too");
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
    /// <param name="side">For a hook of the ring of a pole that set the pole bending, the way it bends.</param>
    /// <returns>Whether it goes on here: letting go does not while a member still holds the ring here.</returns>
    private bool OnLocalRingHold(GameObject holder, bool holding, string side = "") {
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
                StateName = holding ? side : RingLetGoName
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

        // A pole bends from a hook, not from being held (see OnPoleHold)
        if (PoleControlOf(holder) is { } pole) {
            OnPoleHold(player, holder, pole, holding, update.StateName, holders);
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
    /// <param name="side">For taking hold of the ring of a pole, the side that the needle hooked it from, which is the
    /// way it bends.</param>
    private void HoldRing(GameObject holder, bool holding, string who, string side = "") {
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
            } else if (PoleControlOf(holder) is { } pole) {
                pole.SendEvent(
                    holding
                        ? side == PoleConnectRightName ? PoleConnectRightName : PoleConnectLeftName
                        : RingDisconnectName
                );
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
        _pendingPoleThrow = null;
    }
}
