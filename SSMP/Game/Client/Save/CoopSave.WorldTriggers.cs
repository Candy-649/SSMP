using System;
using System.Collections;
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
/// path waits for whoever passes underneath, and the game only ever asks its own hero: the partner walks under it
/// without it noticing, so one player sees it come down and the other keeps seeing it hang, and afterwards they are
/// standing on different ground.
///
/// This is not the same thing as <see cref="CoopSave.ReplayLoadedCollapses"/>, which carries the floors that give way:
/// those are saved with the world, so the save of the partner has them either way and the replay is only about how it
/// looks. A boulder is saved nowhere. It hangs there again the next time the room loads, so there is nothing to put in
/// the save and nothing to send to a partner in another room - only the fall itself, while both are there to see it.
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// The start of the name of the boulders that hang over a path. Everything they do is in one FSM shared by all of
    /// them, so the name is what tells them from the rest of the world.
    /// </summary>
    private const string BoulderNamePrefix = "Bone_Boulder";

    /// <summary>
    /// Part of the name of the boulders that something else drops rather than the ground doing it. They are made when
    /// their trap goes off, so the game of the partner has no copy of them to bring down.
    /// </summary>
    private const string BoulderSummonedPart = "Summon";

    /// <summary>
    /// The start of the name of the object that holds the parts of a boss fight. The boulders under it belong to the
    /// fight, which drops them itself, so they are left alone.
    /// </summary>
    private const string BossSceneNamePrefix = "Boss Scene";

    /// <summary>
    /// The FSM that runs a boulder.
    /// </summary>
    private const string BoulderFsmName = "Control";

    /// <summary>
    /// The event that brings a boulder down, which the game itself sends when its trigger sees the hero. Sending it by
    /// hand plays the whole fall: the shake, the dust, the camera and the rubble it leaves.
    /// </summary>
    private const string BoulderDropEventName = "DROP";

    /// <summary>
    /// The state a boulder goes into when it starts to fall.
    /// </summary>
    private const string BoulderDropStateName = "Drop Antic";

    /// <summary>
    /// The states a boulder comes into <see cref="BoulderDropStateName"/> from when a player set it off here. Anything
    /// else means it was already on its way down, which must not be sent again.
    /// </summary>
    private static readonly string[] BoulderDropFromStateNames = ["Idle", "Shake", "Drop Skip"];

    /// <summary>
    /// The states a boulder can still be brought down from. One that is already falling, has landed or was switched off
    /// is in none of them, which is what keeps it from falling twice when both players walk under it at once.
    /// </summary>
    private static readonly string[] BoulderWaitingStateNames = ["Idle", "Shake", "Idle Inert"];

    /// <summary>
    /// The state a boulder is in while the room it is in is still starting up, which takes no events at all.
    /// </summary>
    private const string BoulderStartingStateName = "Init";

    /// <summary>
    /// How long the fall of a boulder is tried for, in seconds, while its copy here is still starting up.
    /// </summary>
    private const float WorldTriggerRetryTime = 2f;

    /// <summary>
    /// Whether a boulder is being brought down because the partner brought theirs down, in which case its fall is not
    /// sent back to them.
    ///
    /// It covers the fall that starts the moment the event is sent, which is the ordinary one. A boulder that was
    /// made inert first goes through a state in between and only starts falling on the frame after, by which time
    /// this is false again and one needless fall is sent back. That one is harmless - the boulder it describes is
    /// already falling over there, so nothing is done with it - but anything that lets a fallen boulder wait again
    /// would turn it into the two games setting each other off for ever.
    /// </summary>
    private bool _replayingWorldTrigger;

    /// <summary>
    /// Sends the fall of a boulder that a player set off here to the partner. Called from the hook on changes of FSM
    /// state that <see cref="RegisterInteractionHooks"/> puts in place, because one hook on it is enough.
    /// </summary>
    /// <param name="fsm">The FSM that is changing state.</param>
    /// <param name="toState">The state it is changing into.</param>
    private void OnWorldTriggerSwitch(Fsm fsm, FsmState toState) {
        if (_replayingWorldTrigger ||
            _checkedWith is not { } partnerId ||
            toState.Name != BoulderDropStateName ||
            fsm.Name != BoulderFsmName ||
            Array.IndexOf(BoulderDropFromStateNames, fsm.ActiveStateName) < 0 ||
            fsm.GameObject is not { } gameObject ||
            !IsWorldBoulder(gameObject)) {
            return;
        }

        Send(new CoopSaveUpdate {
            TargetId = partnerId,
            Kind = CoopSaveUpdateKind.WorldTrigger,
            Scene = gameObject.scene.name,
            ObjectPath = ScenePath.Get(gameObject.transform),
            FsmName = BoulderFsmName,
            StateName = BoulderDropStateName
        });

        Logger.Info($"Sent the fall of '{gameObject.name}' to the partner");
    }

    /// <summary>
    /// The partner walked under a boulder, so the copy here comes down as well.
    /// </summary>
    /// <param name="player">The player the update came from.</param>
    /// <param name="update">The update, which names the boulder by its path in its scene.</param>
    private void OnWorldTrigger(ClientPlayerData player, CoopSaveUpdate update) {
        if (GetCurrentMarker() is not { } marker || !IsPartner(player, marker) || _checkedWith != player.Id) {
            return;
        }

        MonoBehaviourUtil.Instance.StartCoroutine(
            BringBoulderDown(player.Username, update.Scene, update.ObjectPath, update.FsmName)
        );
    }

    /// <summary>
    /// Brings a boulder down, waiting for it if its room has only just loaded here.
    /// </summary>
    /// <param name="username">The name of the partner, for the log.</param>
    /// <param name="scene">The scene the boulder is in.</param>
    /// <param name="path">The path of the boulder in that scene.</param>
    /// <param name="fsmName">The name of the FSM that runs it.</param>
    private IEnumerator BringBoulderDown(string username, string scene, string path, string fsmName) {
        var until = Time.unscaledTime + WorldTriggerRetryTime;

        while (true) {
            // Nothing is kept for a partner who is somewhere else: a boulder hangs there again the next time its
            // room loads, so a fall that nobody here can see is a fall that never needed to happen here
            var target = ScenePath.Find(path, scene);
            if (target == null || !target.activeInHierarchy) {
                yield break;
            }

            var starting = false;
            foreach (var fsm in target.GetComponents<PlayMakerFSM>()) {
                if (fsm == null || fsm.FsmName != fsmName) {
                    continue;
                }

                if (IsWaitingBoulder(fsm)) {
                    ReplayBoulderDrop(fsm, target.name, username);

                    yield break;
                }

                starting |= fsm.ActiveStateName == BoulderStartingStateName;
            }

            // Both players walking into the same room at once is how this is met: the one who got there first walks
            // under the boulder while the room of the other is still starting up, and a boulder that is still
            // starting up takes no events at all. Giving up on it is not a small loss - a boulder is saved nowhere,
            // so a fall dropped here never happens here, and the two of them walk out onto different ground.
            if (!starting || Time.unscaledTime > until) {
                yield break;
            }

            yield return null;
        }
    }

    /// <summary>
    /// Sends a waiting boulder the event that brings it down, without letting that fall go back to the partner.
    /// </summary>
    /// <param name="fsm">The FSM that runs the boulder.</param>
    /// <param name="name">The name of the boulder, for the log.</param>
    /// <param name="username">The name of the partner, for the log.</param>
    private void ReplayBoulderDrop(PlayMakerFSM fsm, string name, string username) {
        _replayingWorldTrigger = true;
        try {
            fsm.SendEvent(BoulderDropEventName);
            Logger.Info($"Brought down '{name}' the way {username} did");
        } catch (Exception e) {
            Logger.Warn($"Could not bring down '{name}' the way the partner did: {e.Message}");
        } finally {
            _replayingWorldTrigger = false;
        }
    }

    /// <summary>
    /// Whether an object is a boulder that hangs over a path of the world, rather than one a trap makes or one a boss
    /// fight drops. Both of those are brought down by something that the two games already agree about.
    /// </summary>
    private static bool IsWorldBoulder(GameObject gameObject) {
        if (!gameObject.name.StartsWith(BoulderNamePrefix, StringComparison.Ordinal) ||
            gameObject.name.Contains(BoulderSummonedPart)) {
            return false;
        }

        for (var parent = gameObject.transform.parent; parent != null; parent = parent.parent) {
            if (parent.name.StartsWith(BossSceneNamePrefix, StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether an FSM belongs to a boulder that is still hanging and waiting to be walked under.
    /// </summary>
    private static bool IsWaitingBoulder(PlayMakerFSM fsm) {
        return Array.IndexOf(BoulderWaitingStateNames, fsm.ActiveStateName) >= 0 &&
               fsm.GetStateOrNull(BoulderDropStateName) != null;
    }
}
