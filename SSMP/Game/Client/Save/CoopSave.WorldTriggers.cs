using System;
using System.Collections;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// Traps of the world that go off when a player walks into them, in a checked two-player save. A boulder hanging over a
/// path waits for whoever passes underneath, a floor gives way under whoever stands on it, a wire across a corridor
/// waits to be walked through - and the game only ever asks its own hero. The partner walks straight through all of it
/// without any of it noticing, so one player sees the trap go off and the other keeps seeing it wait, and afterwards
/// the two of them are standing on different ground.
///
/// It cannot be fixed where it goes wrong. The partner's body is on the layer everything else is on and carries no tag,
/// and every one of these traps asks for the player's own layer or tag by number - so none of them can see it. It was
/// on the player's layer once and was deliberately moved off, because on that layer it set off doors and prompts that
/// belong to whoever is playing. So the trap is not made to see them; what it did is carried over and done again here.
///
/// This is not the same thing as <see cref="CoopSave.ReplayLoadedCollapses"/>, which carries the floors that give way
/// as the save has them: those are written down, so the save of the partner has them either way and that replay is only
/// about how it looks when a room loads. Most of what is here is saved nowhere. It waits there again the next time the
/// room loads, so there is nothing to put in the save and nothing to send to a partner in another room - only the
/// moment itself, while both of them are there to see it.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// Part of the name of the boulders that something else drops rather than the ground doing it. They are made when
    /// their trap goes off, so the game of the partner has no copy of them to bring down.
    /// </summary>
    private const string BoulderSummonedPart = "Summon";

    /// <summary>
    /// The start of the name of the object that holds the parts of a boss fight. Anything under it belongs to the
    /// fight, which sets its own traps off on both sides already, so all of it is left alone.
    /// </summary>
    private const string BossSceneNamePrefix = "Boss Scene";

    /// <summary>
    /// The state a trap is in while the room it is in is still starting up, which takes no events at all.
    /// </summary>
    private const string WorldTriggerStartingStateName = "Init";

    /// <summary>
    /// How long a trap is tried for, in seconds, while its copy here is still starting up.
    /// </summary>
    private const float WorldTriggerRetryTime = 2f;

    /// <summary>
    /// The traps that a player walking into them sets off, each named by what its objects are called and by the one
    /// event that sets it going.
    ///
    /// Every one of these has the same shape, which is why one table covers them: a state it waits in, and a single
    /// event that takes it out of that state and plays the whole thing - the shake, the dust, the camera, whatever it
    /// drops and whatever it leaves behind. Sending that event by hand is the trap going off, not an imitation of it.
    ///
    /// Only the detector is listed, never what it sets going. A wire tells its spikes to fall and a plate tells its
    /// device to swing, through events of their own, and those follow from the one sent here without being asked.
    ///
    /// Being wrong in this table is cheap in one direction and not the other. A name or state that does not exist
    /// simply never matches, and that trap stays as unsynced as it is today. An event sent to a trap that is not
    /// waiting is what must not happen, and cannot: every one of these events only has a way out of the waiting
    /// states, and the states are checked before it is sent either way.
    /// </summary>
    private static readonly WorldTriggerKind[] WorldTriggerKinds = [
        // Boulders hanging over a path. Two of the waiting states are the same boulder being shaken loose and one
        // that was switched off, both of which can still come down.
        new(["Bone_Boulder"], "Control", "DROP", "Drop Antic", ["Idle", "Shake", "Drop Skip"],
            ["Idle", "Shake", "Idle Inert"]),

        // Floors that crumble under a player. The two kinds are told apart by the event: one is sent what its
        // collision sends, the other is sent the crumble directly, because its collision branch stops to read a
        // collision that did not happen here.
        new(["moss_crumble_plat", "lava_crumble_plat"], "Control", "COLLIDE", "Drop Antic", ["Idle"], ["Idle"]),
        new(["bone_plat_", "crumble_plat_peak_"], "bone_crumble_plat", "CRUMBLE", "Crumble Antic", ["Idle"], ["Idle"]),
        new(["bone_plat_", "crumble_plat_peak_"], "Control", "CRUMBLE", "Crumble Antic", ["Idle"], ["Idle"]),

        // Spikes that a step brings out of the floor, and spikes that a step brings down from above
        new(["Dust Trap Spike Plate"], "Control", "STEP", "Step", ["Idle"], ["Idle"]),
        new(["Dust Trap Spike Dropper"], "Control", "FALL", "Fall Antic", ["Idle"], ["Idle"]),

        // Wires across a path, which tell their own spikes and stones to come down once they are walked through.
        // The pilgrim one looks at the camera first, so replaying it for a partner who is off screen quietly does
        // nothing - which is the right answer anyway.
        new(["Swamp Trap Wire"], "Control", "TOUCH", "Touch", ["Idle"], ["Idle"]),
        new(["Pilgrim Trap Wire"], "Control", "TOUCH", "Cam Check", ["Idle"], ["Idle"]),

        // A plate that sets off whatever it is wired to, and a mine that throws itself
        new(["Hunter Trap Plate"], "Control", "STEP", "Cam Check", ["Idle"], ["Idle"]),
        new(["Trapper Barb Trap Landmine"], "Control", "ATTACK", "Throw Out", ["Dormant"], ["Dormant"]),

        // Things overhead that a player walking under brings down or wakes
        new(["Drop Bell"], "Control", "DROP", "Drop Antic", ["Idle"], ["Idle"]),
        new(["Zap Worm Lightning"], "Control", "ALERT", "Do Zaps", ["Idle"], ["Idle"]),
        new(["Lava_Waterfall Set"], "Control", "ALERT", "Lava 1", ["Wait For Range"], ["Wait For Range"]),

        // Floors and ledges that break away. These are written into the save as well, so a partner in another room
        // gets them when their own room loads; this is only about seeing it happen while both are standing there.
        new(["Collapser Small"], "Control", "BREAK", "Antic Type", ["Idle"], ["Idle"]),
        new(["Abyss_Weaver_Hanging_Plat"], "Break Control", "ENTER", "Tendrils Up", ["Idle"], ["Idle"])
    ];

    /// <summary>
    /// The states that a trap going off puts it into, taken from <see cref="WorldTriggerKinds"/>.
    ///
    /// Kept as a set because the hook this serves is called for every change of state of every state machine in the
    /// game. One lookup here turns almost all of them away before anything else is asked.
    /// </summary>
    private static readonly HashSet<string> WorldTriggerGoneOffStateNames =
        BuildWorldTriggerGoneOffStateNames();

    /// <summary>
    /// Whether a trap is being set off because the partner set theirs off, in which case it is not sent back to them.
    ///
    /// It covers the moment that starts as the event is sent, which is the ordinary one. A boulder that was made
    /// inert first goes through a state in between and only starts falling on the frame after, by which time this is
    /// false again and one needless fall is sent back. That one is harmless - the boulder it describes is already
    /// falling over there, so nothing is done with it - but anything that lets a sprung trap wait again would turn it
    /// into the two games setting each other off for ever.
    /// </summary>
    private bool _replayingWorldTrigger;

    /// <summary>
    /// Collects the states of <see cref="WorldTriggerKinds"/> that say a trap has gone off.
    /// </summary>
    private static HashSet<string> BuildWorldTriggerGoneOffStateNames() {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in WorldTriggerKinds) {
            names.Add(kind.GoneOffStateName);
        }

        return names;
    }

    /// <summary>
    /// Sends a trap that a player set off here to the partner. Called from the hook on changes of FSM state that
    /// <see cref="RegisterInteractionHooks"/> puts in place, because one hook on it is enough.
    /// </summary>
    /// <param name="fsm">The FSM that is changing state.</param>
    /// <param name="toState">The state it is changing into.</param>
    private void OnWorldTriggerSwitch(Fsm fsm, FsmState toState) {
        if (_replayingWorldTrigger ||
            !WorldTriggerGoneOffStateNames.Contains(toState.Name) ||
            _checkedWith is not { } partnerId ||
            fsm.GameObject is not { } gameObject ||
            FindWorldTriggerKind(gameObject, fsm.Name, toState.Name) is not { } kind ||
            Array.IndexOf(kind.FromStateNames, fsm.ActiveStateName) < 0) {
            return;
        }

        Send(new CoopSaveUpdate {
            TargetId = partnerId,
            Kind = CoopSaveUpdateKind.WorldTrigger,
            Scene = gameObject.scene.name,
            ObjectPath = ScenePath.Get(gameObject.transform),
            FsmName = fsm.Name,
            StateName = toState.Name
        });

        Logger.Info($"Sent the trap '{gameObject.name}' going off to the partner");
    }

    /// <summary>
    /// The partner walked into a trap, so the copy here goes off as well.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update, which names the trap by its path in its scene.</param>
    private void OnWorldTrigger(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker) || _checkedWith != player.Id) {
            return;
        }

        MonoBehaviourUtil.Instance.StartCoroutine(
            SpringWorldTrigger(player.Username, update.Scene, update.ObjectPath, update.FsmName, update.StateName)
        );
    }

    /// <summary>
    /// Sets a trap off, waiting for it if its room has only just loaded here.
    /// </summary>
    /// <param name="username">The name of the partner, for the log.</param>
    /// <param name="scene">The scene the trap is in.</param>
    /// <param name="path">The path of the trap in that scene.</param>
    /// <param name="fsmName">The name of the FSM that runs it.</param>
    /// <param name="goneOffStateName">The state it went into over there, which says which trap this is.</param>
    private IEnumerator SpringWorldTrigger(
        string username,
        string scene,
        string path,
        string fsmName,
        string goneOffStateName
    ) {
        var until = Time.unscaledTime + WorldTriggerRetryTime;

        while (true) {
            // Nothing is kept for a partner who is somewhere else: almost all of these wait there again the next
            // time their room loads, so one that nobody here can see is one that never needed to happen here
            var target = ScenePath.Find(path, scene);
            if (target == null || !target.activeInHierarchy) {
                yield break;
            }

            if (FindWorldTriggerKind(target, fsmName, goneOffStateName) is not { } kind) {
                yield break;
            }

            var starting = false;
            foreach (var fsm in target.GetComponents<PlayMakerFSM>()) {
                if (fsm == null || fsm.FsmName != fsmName) {
                    continue;
                }

                if (IsWaitingWorldTrigger(fsm, kind)) {
                    ReplayWorldTrigger(fsm, kind, target.name, username);

                    yield break;
                }

                starting |= fsm.ActiveStateName == WorldTriggerStartingStateName;
            }

            // Both players walking into the same room at once is how this is met: the one who got there first walks
            // into the trap while the room of the other is still starting up, and a trap that is still starting up
            // takes no events at all. Giving up on it is not a small loss - most of these are saved nowhere, so one
            // dropped here never happens here, and the two of them walk out onto different ground.
            if (!starting || Time.unscaledTime > until) {
                yield break;
            }

            yield return null;
        }
    }

    /// <summary>
    /// Sends a waiting trap the event that sets it off, without letting that go back to the partner.
    /// </summary>
    /// <param name="fsm">The FSM that runs the trap.</param>
    /// <param name="kind">What kind of trap it is.</param>
    /// <param name="name">The name of the trap, for the log.</param>
    /// <param name="username">The name of the partner, for the log.</param>
    private void ReplayWorldTrigger(PlayMakerFSM fsm, WorldTriggerKind kind, string name, string username) {
        _replayingWorldTrigger = true;
        try {
            fsm.SendEvent(kind.EventName);
            Logger.Info($"Set off '{name}' the way {username} did");
        } catch (Exception e) {
            Logger.Warn($"Could not set off '{name}' the way the partner did: {e.Message}");
        } finally {
            _replayingWorldTrigger = false;
        }
    }

    /// <summary>
    /// What kind of trap an object is, or null if it is none of them or is one that is driven by something the two
    /// games already agree about.
    /// </summary>
    /// <param name="gameObject">The object.</param>
    /// <param name="fsmName">The name of the FSM in question.</param>
    /// <param name="goneOffStateName">The state that says it has gone off.</param>
    private static WorldTriggerKind? FindWorldTriggerKind(
        GameObject gameObject,
        string fsmName,
        string goneOffStateName
    ) {
        // A boulder that a trap makes when it springs has no copy over there to bring down, and anything belonging
        // to a boss fight is set off by the fight in both games already
        var name = gameObject.name;
        if (name.Contains(BoulderSummonedPart)) {
            return null;
        }

        for (var parent = gameObject.transform.parent; parent != null; parent = parent.parent) {
            if (parent.name.StartsWith(BossSceneNamePrefix, StringComparison.Ordinal)) {
                return null;
            }
        }

        foreach (var kind in WorldTriggerKinds) {
            if (kind.FsmName != fsmName || kind.GoneOffStateName != goneOffStateName) {
                continue;
            }

            foreach (var prefix in kind.NamePrefixes) {
                if (name.StartsWith(prefix, StringComparison.Ordinal)) {
                    return kind;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an FSM belongs to a trap that is still set and waiting to be walked into. One that has already gone
    /// off, or has nothing left to do, is in none of its waiting states - which is what keeps a trap from going off
    /// twice when both players walk into it at once, and keeps one that resets from being sprung again by the echo
    /// of its own.
    /// </summary>
    private static bool IsWaitingWorldTrigger(PlayMakerFSM fsm, WorldTriggerKind kind) {
        return Array.IndexOf(kind.WaitingStateNames, fsm.ActiveStateName) >= 0 &&
               fsm.GetStateOrNull(kind.GoneOffStateName) != null;
    }

    /// <summary>
    /// One kind of trap that a player walking into it sets off.
    /// </summary>
    private sealed class WorldTriggerKind {
        public WorldTriggerKind(
            string[] namePrefixes,
            string fsmName,
            string eventName,
            string goneOffStateName,
            string[] fromStateNames,
            string[] waitingStateNames
        ) {
            NamePrefixes = namePrefixes;
            FsmName = fsmName;
            EventName = eventName;
            GoneOffStateName = goneOffStateName;
            FromStateNames = fromStateNames;
            WaitingStateNames = waitingStateNames;
        }

        /// <summary>
        /// What the objects of this kind are called, of which the name of one starts with any of them. Everything a
        /// kind does is in one state machine shared by all of them, so the name is what tells them from the rest of
        /// the world.
        /// </summary>
        public string[] NamePrefixes { get; }

        /// <summary>
        /// The state machine that runs it.
        /// </summary>
        public string FsmName { get; }

        /// <summary>
        /// The event that sets it off, which the game itself sends when its own trigger sees the hero.
        /// </summary>
        public string EventName { get; }

        /// <summary>
        /// The state it goes into as it springs, which is how it is noticed here and how the partner knows which of
        /// these it was.
        /// </summary>
        public string GoneOffStateName { get; }

        /// <summary>
        /// The states it comes into <see cref="GoneOffStateName"/> from when a player set it off here. Anything else
        /// means it was already going off, which must not be sent again.
        /// </summary>
        public string[] FromStateNames { get; }

        /// <summary>
        /// The states it can still be set off from.
        /// </summary>
        public string[] WaitingStateNames { get; }
    }
}
