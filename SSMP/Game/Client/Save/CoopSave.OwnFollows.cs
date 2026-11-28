using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using SSMP.Game.Client.Entity;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Save;

// SSMP.Fsm hides the Fsm type of PlayMaker in this namespace
using Fsm = HutongGames.PlayMaker.Fsm;

/// <summary>
/// The way of a creature that every game runs by itself through a move in which it keeps after a player where they
/// are, at a distance from them or right at them (see GamePatcher.MovesAfterAPlayer), which goes by the scene host's
/// game, as the way of a copy did (USER 11-28 "我觉得这个怪就按照之前副本那样发位置给其他人，因为这种怪招式也瞄人，也要
/// 等房主说出什么招，往哪打"): that game tells the members in the room where its creature is and how fast it goes a few
/// times a second, and in every other game the actions that move the creature after the player go on as the game has
/// them, but move it by the motion that makes for where that game's is (see GamePatcher.SceneHostsMotion). Each game
/// moved it by where it has the player, which is late for the other player in each game: the creatures went apart
/// over the move, and each other game's was put where the scene host's was as the next move started, the spear
/// thrower of the shell wood 5.2 units at a blow (Luminary's log of 11-28, 16:26:18.900; USER "我朋友说壳木林会扔长矛的
/// 小怪打了以后会瞬移"). This is the one way that a creature is set where the scene host's is within a move (USER 11-18
/// "位置校准只在出招时做"), and a knockback here plays out first, as there (see <see cref="OwnFollowAfterKnock"/>).
/// </summary>
internal partial class CoopSave {
    /// <summary>
    /// What an update of a creature that every game runs by itself is when the scene host's game tells where it is in a
    /// move after a player (see <see cref="TellOwnFollows"/>): the state machine and state of the move, where it is
    /// and how fast it goes in <see cref="CoopSaveUpdate.Values"/>, x, y and the two speeds, and how many such words
    /// that game sent before it in <see cref="CoopSaveUpdate.Sequence"/>.
    /// </summary>
    private const ushort OwnFollowPart = 23;

    /// <summary>
    /// How long the scene host's game waits between its words of where its creatures are in moves after a player.
    /// </summary>
    private const float OwnFollowInterval = 0.15f;

    /// <summary>
    /// How long a word of where a creature is in a move after a player stands: after it, with no newer one, the
    /// creature's own actions move it by this game again.
    /// </summary>
    private const float OwnFollowLifetime = 0.6f;

    /// <summary>
    /// How far ahead of the word of where a creature was it goes on, at its speed then, at most.
    /// </summary>
    private const float OwnFollowLookAhead = 0.5f;

    /// <summary>
    /// How fast a creature makes up the way between where it is and where the scene host's is, as a share of it each
    /// second.
    /// </summary>
    private const float OwnFollowPull = 6f;

    /// <summary>
    /// The fastest that a creature makes up the way between where it is and where the scene host's is.
    /// </summary>
    private const float OwnFollowMaxPull = 20f;

    /// <summary>
    /// How long after its knockback here ends a creature still leaves its way to the scene host's word, besides the
    /// round trip.
    /// </summary>
    private const float OwnFollowAfterKnock = 0.1f;

    /// <summary>
    /// When the scene host's game next tells where its creatures in moves after a player are.
    /// </summary>
    private float _nextOwnFollowWord;

    /// <summary>
    /// How many words of where its creatures in moves after a player are this game sent.
    /// </summary>
    private ulong _ownFollowWords;

    /// <summary>
    /// A word of the scene host's game of where a creature, or a part of one, is in a move after a player.
    /// </summary>
    private sealed record OwnFollow(
        ushort From,
        ulong Number,
        string FsmName,
        string StateName,
        Vector2 Place,
        Vector2 Speed,
        float CameAt
    );

    /// <summary>
    /// The latest word of the scene host's game of where each creature, or part of one, is in a move after a player.
    /// </summary>
    private readonly Dictionary<GameObject, OwnFollow> _ownFollows = new();

    /// <summary>
    /// The creatures that move by the scene host's motion of them, with the state machine and the state that they do
    /// it in, when they started, and the farthest they were from where that game's is, for the log.
    /// </summary>
    private readonly Dictionary<GameObject, (string Fsm, string State, float Since, float Farthest)> _followingHost =
        new();

    /// <summary>
    /// Until when a creature knocked back here moves at its speed in the scene host's game alone: the word of where it
    /// is there tells of the knockback only a round trip after the blow (see <see cref="OwnFollowAfterKnock"/>).
    /// </summary>
    private readonly Dictionary<GameObject, float> _followHeldAfterKnock = new();

    /// <summary>
    /// Tells where its creatures in moves after a player are, if this game is the scene host's, and ends the following
    /// of the scene host's creatures whose words are old or of another state than the one they are in, if it follows
    /// that game.
    /// </summary>
    private void UpdateOwnFollows() {
        TellOwnFollows();
        EndOldFollows();
    }

    /// <summary>
    /// Tells the members in the room where the creatures that this game, the scene host's, runs are in moves after a
    /// player, and how fast they go, a few times a second (see <see cref="OwnFollowInterval"/>).
    /// </summary>
    private void TellOwnFollows() {
        var now = Time.unscaledTime;
        if (now < _nextOwnFollowWord || IsSceneHost?.Invoke() != true || !IsMemberInRoom()) {
            return;
        }

        _nextOwnFollowWord = now + OwnFollowInterval;
        foreach (var moving in OwnCreatures.AllWithMachines()) {
            if (moving == null || !moving.activeInHierarchy || !OwnCreatures.RunsHere(moving) ||
                !moving.TryGetComponent<Rigidbody2D>(out var body)) {
                continue;
            }

            foreach (var machine in moving.GetComponents<PlayMakerFSM>()) {
                if (OwnCreatures.RunsInEachGame(machine) || machine.Fsm.ActiveState is not { } state ||
                    !state.Actions.Any(action => action.Enabled && GamePatcher.MovesAfterAPlayer(action))) {
                    continue;
                }

                var at = moving.transform.position;
                var speed = body.linearVelocity;
                SendToRoomMembers(new CoopSaveUpdate {
                    Kind = CoopSaveUpdateKind.OwnCreature,
                    Scene = moving.scene.name,
                    ObjectPath = OwnCreatures.PlaceOf(moving),
                    PartCount = OwnFollowPart,
                    FsmName = machine.FsmName,
                    StateName = state.Name,
                    Values = [at.x, at.y, speed.x, speed.y],
                    Sequence = ++_ownFollowWords
                });
                break;
            }
        }
    }

    /// <summary>
    /// Takes the word of the scene host's game of where a creature, or a part of one, is in a move after a player
    /// there (see <see cref="TellOwnFollows"/>), unless a newer one of that game came already.
    /// </summary>
    private void TakeOwnFollow(ClientPlayerData player, GameObject moving, CoopSaveUpdate update) {
        if (update.Values.Count < 4 ||
            _ownFollows.TryGetValue(moving, out var had) && had.From == player.Id && had.Number >= update.Sequence) {
            return;
        }

        _ownFollows[moving] = new OwnFollow(
            player.Id, update.Sequence, update.FsmName, update.StateName,
            new Vector2(update.Values[0], update.Values[1]), new Vector2(update.Values[2], update.Values[3]),
            Time.unscaledTime
        );
    }

    /// <summary>
    /// The motion of the object of an action that moves after a player at every frame, a creature in the move that
    /// the scene host's game told it is in, which makes for where that game's is now, as far as its word says, by its
    /// speed: its speed there, and as much faster as makes up the way between them. None if that game does not move it
    /// now (see GamePatcher.SceneHostsMotion). One that a blow knocks back here goes at its speed there alone through
    /// its knockback, and for a round trip after, as the word of where it is there tells of the knockback only then:
    /// put where that one was, its knockback was undone (USER 11-18 "检查位置越频繁，就越是会因为正好客机在做反击或者正好
    /// 客机攻击到怪而重置部分位移").
    /// </summary>
    private Vector2? MotionBySceneHost(FsmStateAction mover) {
        var fsm = mover.Fsm;
        var moving = fsm.GameObject;
        if (!_ownFollows.TryGetValue(moving, out var word) || !GoesBy(word, fsm) ||
            !GamePatcher.MovesAfterAPlayer(mover)) {
            return null;
        }

        var now = Time.unscaledTime;
        var roundTrip = _netClient.UpdateManager.AverageRtt / 1000f;
        var there = word.Place + word.Speed * Mathf.Min(now - word.CameAt + roundTrip / 2f, OwnFollowLookAhead);
        var way = there - (Vector2) moving.transform.position;
        NoteFollowing(moving, word, way.magnitude);
        if (moving.TryGetComponent<Recoil>(out var recoil) && recoil.IsRecoiling) {
            _followHeldAfterKnock[moving] = now + roundTrip + OwnFollowAfterKnock;
        }

        return _followHeldAfterKnock.TryGetValue(moving, out var until) && now < until
            ? word.Speed
            : word.Speed + Vector2.ClampMagnitude(way * OwnFollowPull, OwnFollowMaxPull);
    }

    /// <summary>
    /// Whether a state machine of a creature here goes by a word of the scene host's game of where that game's is: the
    /// word is new and of the state that the machine is in, and the creature plays no move here by itself.
    /// </summary>
    private bool GoesBy(OwnFollow word, Fsm fsm) {
        return Time.unscaledTime - word.CameAt < OwnFollowLifetime && word.FsmName == fsm.Name &&
               word.StateName == fsm.ActiveStateName && OwnCreatures.FollowsSceneHost(fsm.GameObject) &&
               !PlaysOwnMove(fsm);
    }

    /// <summary>
    /// Notes that a creature here moves by the scene host's motion of it in the state of a word, how far from where
    /// that game's is, which logs it as it starts in that state.
    /// </summary>
    private void NoteFollowing(GameObject moving, OwnFollow word, float away) {
        if (!_followingHost.TryGetValue(moving, out var following) || following.Fsm != word.FsmName ||
            following.State != word.StateName) {
            EndFollowingHost(moving);
            following = (word.FsmName, word.StateName, Time.unscaledTime, away);
            Logger.Info(
                $"[Own creatures] '{OwnCreatures.PlaceOf(moving)}' goes by the scene host's game in its move " +
                $"'{word.StateName}' of '{word.FsmName}' after a player, {away:F1} away from where it is there"
            );
        }

        _followingHost[moving] = following with { Farthest = Mathf.Max(following.Farthest, away) };
    }

    /// <summary>
    /// Forgets the words of the scene host's game that are old, and ends the following of the creatures whose words
    /// they were, or that are in another state than that of their word.
    /// </summary>
    private void EndOldFollows() {
        if (_ownFollows.Count == 0) {
            return;
        }

        foreach (var (moving, word) in _ownFollows.ToList()) {
            if (moving == null || Time.unscaledTime - word.CameAt >= OwnFollowLifetime) {
                _ownFollows.Remove(moving!);
                EndFollowingHost(moving);
            } else if (PlayMakerFSM.FindFsmOnGameObject(moving, word.FsmName) is not { } machine ||
                       !GoesBy(word, machine.Fsm)) {
                EndFollowingHost(moving);
            }
        }
    }

    /// <summary>
    /// Ends the following of the scene host's creature by a creature here, which logs how long it went and how far it
    /// was from it at the most.
    /// </summary>
    private void EndFollowingHost(GameObject? moving) {
        if (moving is null || !_followingHost.Remove(moving, out var following)) {
            return;
        }

        _followHeldAfterKnock.Remove(moving);
        if (moving != null) {
            Logger.Info(
                $"[Own creatures] '{OwnCreatures.PlaceOf(moving)}' went by the scene host's game in " +
                $"'{following.State}' for {Time.unscaledTime - following.Since:F2} s, " +
                $"{following.Farthest:F1} away from it at the most"
            );
        }
    }

    /// <summary>
    /// Forgets the words of where creatures are in moves after a player, as the room is left.
    /// </summary>
    private void ForgetOwnFollows() {
        _ownFollows.Clear();
        _followingHost.Clear();
        _followHeldAfterKnock.Clear();
        _nextOwnFollowWord = 0f;
    }
}
