using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    /// The type of the action that sends an event to everything in the game that listens for it.
    /// </summary>
    private const string SendEventToRegisterTypeName = "SendEventToRegister";

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

        // Floors that crumble under a player and come back a moment later are not here: each player has their own (see
        // PersonalPlaces). Breaking them together left the other player waiting for the floor, or falling into lava.

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
        new(["Abyss_Weaver_Hanging_Plat"], "Break Control", "ENTER", "Tendrils Up", ["Idle"], ["Idle"]),

        // A bench that gives way under whoever sits on it and drops them down with it. It is written into the save as
        // well; this is the floor going here as it goes over there. The copy here only falls: taking the sitter off
        // the bench, carrying them down, the camera on them and handing them back their controls are about them.
        new(["Fake Bench Collapser"], "Control", "FAKE BENCH SIT", "Antic", ["Idle"], ["Idle"], keepOffThePlayer: true),

        // Not a trap, but it goes the same way: a character cowering among creatures, who stops once none of them is
        // left. The room counts what is still in the group of those creatures every few seconds, and in the game of the
        // player who doesn't run the room the room's own creatures only sleep there while their copies fight and die,
        // so it never counted down to none, and the character never got up to talk to that player. There is no event
        // that takes it there from where it waits, so its state is set. It stays rescued until the room loads again,
        // so a partner who walks in later is told too, or they never have the conversation that follows.
        new(["Courier Scene"], "Save Courier", "", "Send Event", ["Count Enemies"], ["Idle", "Count Enemies"],
            lasting: true),

        // Crows perched about a room for show, which fly off when the crows there that fight wake up, and are gone
        // until the room loads again. While both players are in the room the waking plays in both games, so nothing is
        // sent at the moment itself. A partner who walked in later had them perched again with nothing left to wake
        // them, and walking up to them did nothing; now they are sent the ones that flew, which fly off as they come in.
        new(["FlyAway Crow"], "Control", "FLY AWAY", "Deactivate", [], ["Init", "Idle L", "Idle R"],
            lasting: true, endsSwitchedOff: true)
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
    /// What an update of a plate says in place of the state a trap went into when the player got off it again before it
    /// went down. An update of a plate that says nothing there is the player stepping onto it.
    /// </summary>
    private const string PlateLeftName = "Left";

    /// <summary>
    /// The members who stand on each plate here in their own games, for whom the plate stands on itself once, until the
    /// last of them got off (<see cref="OnWorldTrigger"/>). Two members on one plate, of whom the first stepped off,
    /// would otherwise let it up under the other.
    /// </summary>
    private readonly Dictionary<PressurePlateBase, HashSet<ushort>> _platesStoodOn = new();

    /// <summary>
    /// Takes a member who left the save off every plate they stood on, letting a plate up that nobody else stands on.
    /// </summary>
    private void ForgetPlatesOf(ushort id) {
        foreach (var pair in _platesStoodOn.ToList()) {
            if (!pair.Value.Remove(id) || pair.Value.Count > 0) {
                continue;
            }

            try {
                if (pair.Key != null && pair.Key.player == pair.Key.gameObject) {
                    pair.Key.OnTouchEnd(pair.Key.gameObject);
                }
            } catch (Exception e) {
                Logger.Warn($"Could not let the plate up that a member who left stood on: {e.Message}");
            }
        }
    }

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
    /// Makes the update that sends a trap that a player set off here to the partner, if the change of state is a trap
    /// going off. Called from the hook on changes of FSM state that <see cref="RegisterInteractionHooks"/> puts in
    /// place, because one hook on it is enough, before the change: the trap goes off in the change, and the update
    /// goes once it has, with the dice it rolled (see <see cref="SetOffWorldTrigger"/>).
    /// </summary>
    /// <param name="fsm">The FSM that is changing state.</param>
    /// <param name="toState">The state it is changing into.</param>
    /// <returns>The update, or null if no trap goes off.</returns>
    private CoopSaveUpdate? OnWorldTriggerSwitch(Fsm fsm, FsmState toState) {
        if (_replayingWorldTrigger ||
            !WorldTriggerGoneOffStateNames.Contains(toState.Name) ||
            _checkedMembers.Count == 0 ||
            fsm.GameObject is not { } gameObject ||
            FindWorldTriggerKind(gameObject, fsm.Name, toState.Name) is not { } kind ||
            Array.IndexOf(kind.FromStateNames, fsm.ActiveStateName) < 0) {
            return null;
        }

        return new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.WorldTrigger,
            Scene = gameObject.scene.name,
            ObjectPath = ScenePath.Get(gameObject.transform),
            FsmName = fsm.Name,
            StateName = toState.Name
        };
    }

    /// <summary>
    /// Lets a trap that a player set off here go off, writing down what it rolls, and sends it to the partner with
    /// those dice for their game to roll the same: a mine leaps out, turns and lands by them (see
    /// <see cref="SharedDice"/>). A trap that it sets off in turn is sent before it, having gone off first; the dice
    /// of each roll go by the action that rolled, so it does not matter which goes off first over there. It is sent
    /// even if going off here broke, as it was before the dice went with it.
    /// </summary>
    /// <param name="trap">The update of the trap, from <see cref="OnWorldTriggerSwitch"/>.</param>
    /// <param name="goOff">Lets it go off.</param>
    private void SetOffWorldTrigger(CoopSaveUpdate trap, Action goOff) {
        try {
            trap.Amounts = [.. SharedDice.Record(goOff)];
        } finally {
            SendToMembers(trap);
            Logger.Info($"Sent the trap '{trap.ObjectPath}' going off to {GetCheckedNames()}");
        }
    }

    /// <summary>
    /// Sends the player here stepping onto a plate, the moment the plate feels them, and stepping off it again before
    /// it went down. It is a trap like the others, only not an FSM: the game asks its own hero, so the plate stayed up
    /// for the player who did not stand on it. The partner's plate is stood on in turn by itself (see
    /// <see cref="OnWorldTrigger"/>) and goes through what this one does - the wait for its weight to stay on, going
    /// down, a door - a moment behind, and gives up where this one gave up. Every plate goes this way. A lift that a
    /// plate calls or unlocks is told only by the plate of the player who stood on it, and the other game takes it
    /// from the updates about the lift (see <see cref="OnLiftPlateActivate"/>).
    /// </summary>
    private void OnPlateTouch(
        Action<PressurePlateBase, GameObject> orig,
        PressurePlateBase self,
        GameObject toucher,
        bool leaving
    ) {
        // A plate stands on itself only for the partner. Getting off one that has gone down already changes nothing:
        // it has put its collider away by then
        var send = toucher != self.gameObject && (!leaving || self.player == toucher && self.col.enabled);
        orig(self, toucher);

        if (!send || _checkedMembers.Count == 0) {
            return;
        }

        var path = ScenePath.Get(self.transform);
        SendToMembers(new CoopSaveUpdate {
            Kind = CoopSaveUpdateKind.WorldTrigger,
            Scene = self.gameObject.scene.name,
            ObjectPath = path,
            StateName = leaving ? PlateLeftName : ""
        });
        Logger.Info(
            leaving ? $"Sent getting off the plate '{path}' to the partner" : $"Sent the plate '{path}' to the partner"
        );
    }

    /// <summary>
    /// Sends a partner who has just walked into the room the ones of <see cref="WorldTriggerKinds"/> that went off here
    /// and stay that way until the room loads again (<see cref="WorldTriggerKind.Lasting"/>). Otherwise what goes off
    /// is only sent at the moment it does, which a partner who came in later never hears: the character that the room
    /// had them rescue stayed cowering in their game, and the conversation that follows was lost to them.
    /// </summary>
    /// <param name="player">The player who walked in.</param>
    internal void OnPlayerEnterScene(ClientPlayerData player) {
        try {
            if (!_checkedMembers.Contains(player.Id) || GetCurrentMarker() is not { } marker ||
                !IsPartner(player, marker)) {
                return;
            }

            // Switched off ones too, since some of these switch themselves off as they end
            foreach (var fsm in UnityEngine.Object.FindObjectsByType<PlayMakerFSM>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None
                     )) {
                if (fsm == null || GetLastingGoneOffStateName(fsm) is not { } stateName) {
                    continue;
                }

                var path = ScenePath.Get(fsm.transform);
                Send(new CoopSaveUpdate {
                    TargetId = player.Id,
                    Kind = CoopSaveUpdateKind.WorldTrigger,
                    Scene = fsm.gameObject.scene.name,
                    ObjectPath = path,
                    FsmName = fsm.FsmName,
                    StateName = stateName
                });
                Logger.Info($"Sent '{path}', which went off here before {player.Username} came in, to them");
            }
        } catch (Exception e) {
            Logger.Warn($"Could not send what went off here to {player.Username}, who came in: {e.Message}");
        }
    }

    /// <summary>
    /// The state that one of <see cref="WorldTriggerKinds"/> that lasts went off into here, or null if the FSM is none
    /// of them or hasn't gone off. One that switches itself off as it ends forgets its state as it goes off (it starts
    /// over when switched on), so it is known by being off, having run, while what it is in is on.
    /// </summary>
    private static string? GetLastingGoneOffStateName(PlayMakerFSM fsm) {
        var gameObject = fsm.gameObject;
        if (gameObject.activeInHierarchy) {
            return fsm.ActiveStateName is { } stateName && WorldTriggerGoneOffStateNames.Contains(stateName) &&
                   FindWorldTriggerKind(gameObject, fsm.FsmName, stateName) is { Lasting: true }
                ? stateName
                : null;
        }

        // One that its room keeps off never started, and one whose parent is off went off with it, not by itself
        if (gameObject.activeSelf || fsm.Fsm is not { Started: true } ||
            gameObject.transform.parent is { } parent && !parent.gameObject.activeInHierarchy) {
            return null;
        }

        foreach (var kind in WorldTriggerKinds) {
            if (kind is { Lasting: true, EndsSwitchedOff: true } && kind.FsmName == fsm.FsmName &&
                FindWorldTriggerKind(gameObject, fsm.FsmName, kind.GoneOffStateName) == kind) {
                return kind.GoneOffStateName;
            }
        }

        return null;
    }

    /// <summary>
    /// The partner walked into a trap, so the copy here goes off as well.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update, which names the trap by its path in its scene.</param>
    private void OnWorldTrigger(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker) ||
            !_checkedMembers.Contains(player.Id)) {
            return;
        }

        // A plate, which names no FSM. It stands on itself for the partner and so does all that it does for a player on
        // it - waits for them to stay, goes down, opens the door, makes the call that three plates of a shrine count -
        // and gives up if they get off in time. Not one that the player here is on already, or that went down already.
        // Nothing gets off a plate that has gone down, so one that came up again may still stand on itself
        if (update.FsmName.Length == 0) {
            if (ScenePath.Find(update.ObjectPath, update.Scene) is not { activeInHierarchy: true } target ||
                target.GetComponent<PressurePlateBase>() is not { } plate) {
                return;
            }

            // The plate stands on itself for each member who stepped on it while it could feel them, and gets off
            // only once the last of them did. A member whose step it couldn't feel, like while the local player was
            // the one on it, is not counted: their game sends no getting off for a plate that never took them
            if (!_platesStoodOn.TryGetValue(plate, out var standers)) {
                standers = _platesStoodOn[plate] = [];
            }

            if (update.StateName == PlateLeftName) {
                if (standers.Remove(player.Id) && standers.Count == 0) {
                    plate.OnTouchEnd(target);
                }
            } else if (plate.CanDepress && plate.col.enabled && (plate.player == null || plate.player == target)) {
                standers.Add(player.Id);
                plate.OnTouchStart(target);
                Logger.Info($"{player.Username} is on the plate '{target.name}'");
            }

            return;
        }

        MonoBehaviourUtil.Instance.StartCoroutine(SpringWorldTrigger(
            player.Username, update.Scene, update.ObjectPath, update.FsmName, update.StateName, update.Amounts
        ));
    }

    /// <summary>
    /// Sets a trap off, waiting for it if its room has only just loaded here.
    /// </summary>
    /// <param name="username">The name of the partner, for the log.</param>
    /// <param name="scene">The scene the trap is in.</param>
    /// <param name="path">The path of the trap in that scene.</param>
    /// <param name="fsmName">The name of the FSM that runs it.</param>
    /// <param name="goneOffStateName">The state it went into over there, which says which trap this is.</param>
    /// <param name="dice">The dice of the partner's game as it went off there.</param>
    private IEnumerator SpringWorldTrigger(
        string username,
        string scene,
        string path,
        string fsmName,
        string goneOffStateName,
        List<int> dice
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
                    ReplayWorldTrigger(fsm, kind, target.name, username, dice);

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
    /// Sends a waiting trap the event that sets it off, without letting that go back to the partner, with the dice
    /// that the partner's game had as it went off there.
    /// </summary>
    /// <param name="fsm">The FSM that runs the trap.</param>
    /// <param name="kind">What kind of trap it is.</param>
    /// <param name="name">The name of the trap, for the log.</param>
    /// <param name="username">The name of the partner, for the log.</param>
    /// <param name="dice">The dice of the partner's game.</param>
    private void ReplayWorldTrigger(
        PlayMakerFSM fsm,
        WorldTriggerKind kind,
        string name,
        string username,
        List<int> dice
    ) {
        _replayingWorldTrigger = true;
        try {
            var keptOff = kind.KeepOffThePlayer ? KeepTrapOffThePlayer(fsm) : 0;
            SharedDice.Throw(dice, () => {
                if (kind.EventName.Length == 0) {
                    fsm.SetState(kind.GoneOffStateName);
                } else {
                    fsm.SendEvent(kind.EventName);
                }
            });
            Logger.Info(
                keptOff > 0
                    ? $"Set off '{name}' the way {username} did, without the {keptOff} of its actions about them"
                    : $"Set off '{name}' the way {username} did"
            );
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

        // The kind before the parents: some of the states these go into, such as "Antic", are ones that creatures
        // go into all the time, and none of those is one of these
        if (MatchWorldTriggerKind(name, fsmName, goneOffStateName) is not { } kind) {
            return null;
        }

        for (var parent = gameObject.transform.parent; parent != null; parent = parent.parent) {
            if (parent.name.StartsWith(BossSceneNamePrefix, StringComparison.Ordinal)) {
                return null;
            }
        }

        return kind;
    }

    /// <summary>
    /// The kind of trap that objects of a name are, going by the FSM and the state that says it went off.
    /// </summary>
    private static WorldTriggerKind? MatchWorldTriggerKind(string name, string fsmName, string goneOffStateName) {
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
    /// Switches off, in the copy of a trap here, whatever it does to the player who set it off. In their game that
    /// player is them; here the same actions would take hold of the local player, who set nothing off. The copy only
    /// ever goes off once, so they stay off: the next time the room loads, it is a new copy.
    /// </summary>
    /// <returns>How many actions were switched off.</returns>
    private static int KeepTrapOffThePlayer(PlayMakerFSM fsm) {
        var hero = HeroController.instance;
        var switchedOff = 0;
        foreach (var state in fsm.FsmStates) {
            foreach (var action in state.Actions) {
                if (action is not { Enabled: true } || !IsAboutThePlayer(action, fsm.Fsm, hero)) {
                    continue;
                }

                action.Enabled = false;
                switchedOff++;
            }
        }

        return switchedOff;
    }

    /// <summary>
    /// Whether an action of a trap does something to the local player: moves, parents, frees or animates them, has the
    /// camera follow them or plays a sound on them - or sends an event to everything in the game that listens, which
    /// for the bench that gives way takes whoever sits on a bench off it.
    ///
    /// The object an action names is worked out through the FSM it belongs to, not through the action: an action only
    /// learns its FSM when its state is first entered, and these are states the copy has never been in.
    /// </summary>
    private static bool IsAboutThePlayer(FsmStateAction action, Fsm fsm, HeroController? hero) {
        if (action.GetType().Name == SendEventToRegisterTypeName) {
            return true;
        }

        if (hero == null) {
            return false;
        }

        foreach (var field in action.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)) {
            var target = field.GetValue(action) switch {
                FsmOwnerDefault owner => fsm.GetOwnerDefaultTarget(owner),
                FsmGameObject gameObject => gameObject.Value,
                _ => null
            };

            if (target != null && target.transform.IsChildOf(hero.transform)) {
                return true;
            }
        }

        return false;
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
            string[] waitingStateNames,
            bool keepOffThePlayer = false,
            bool lasting = false,
            bool endsSwitchedOff = false
        ) {
            NamePrefixes = namePrefixes;
            FsmName = fsmName;
            EventName = eventName;
            GoneOffStateName = goneOffStateName;
            FromStateNames = fromStateNames;
            WaitingStateNames = waitingStateNames;
            KeepOffThePlayer = keepOffThePlayer;
            Lasting = lasting;
            EndsSwitchedOff = endsSwitchedOff;
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
        /// The event that sets it off, which the game itself sends when its own trigger sees the hero, or empty for one
        /// that no event takes from where it waits to where it goes off, whose state is set instead.
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

        /// <summary>
        /// Whether it does things to the player who set it off, which the copy here must not do to the local player.
        /// Those are switched off before it is set off here (<see cref="KeepTrapOffThePlayer"/>), so for these the
        /// event sent by hand plays everything of the trap except what happens to that player.
        /// </summary>
        public bool KeepOffThePlayer { get; }

        /// <summary>
        /// Whether it stays gone off until its room loads again, in <see cref="GoneOffStateName"/> or switched off
        /// (<see cref="EndsSwitchedOff"/>), so that a partner who walks in afterwards is sent it too
        /// (<see cref="OnPlayerEnterScene"/>).
        /// </summary>
        public bool Lasting { get; }

        /// <summary>
        /// Whether going off ends with it switching its own object off, which also makes its FSM forget its state, so
        /// one that lasts is found by being off rather than by its state.
        /// </summary>
        public bool EndsSwitchedOff { get; }
    }
}
