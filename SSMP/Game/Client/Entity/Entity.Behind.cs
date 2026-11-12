using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using SSMP.Game.Client.Entity.Component;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// What the scene host sends of a creature while its copy plays on to its own end of a move that the scene host's
/// creature came out of first: taken in after the move, in the order it came and as far apart as it came, rather than
/// dropped for wherever the scene host's creature had got to by then (USER 11-03 "这类客机先演的动作就应该演完接着演压下来
/// 的动作然后接着演主机新放回来的动作，不要屏蔽了"). Its FSM went from the end of the move through what the creature waits in
/// and picks its next move with, and only its latest state is sent: the copy that heard it several states on was cut
/// short where it stood, and a boss that the scene host's player had seen go on from its parry into a rise stood at the
/// end of the copy's parry and was then put 16 units up in one go (log of 11-03). The copy goes on that much behind the
/// scene host's creature, and makes it up by playing all of it half as fast again - one move after another, where the
/// creature goes and the clips it plays - until it has caught up (see <see cref="MakeUpRate"/>). Made up only as the
/// copy stood still between two moves, it never was for a creature that goes from one move into the next (USER 11-12
/// "极端一点的怪，几乎没有发呆的时间，又不能加快出招的话，那岂不是永远追不上了"). Whatever the local player does to the creature
/// first takes in the rest at once (see <see cref="CatchUpWithWhatCameSince"/>), so that it goes from where the
/// creature is over there now, as the user chose (11-03). Its health and the music go as they come all the same, and
/// its death after all that the copy
/// kept (see <see cref="TakeInAsItComes"/>). So it goes too while the copy plays on to its end an input of the local
/// player that the scene host's creature did not take, rather than cut it short for what that creature did instead
/// (see <see cref="PlayOnPastTheRefusal"/>, USER 11-12).
/// </summary>
internal partial class Entity {
    /// <summary>
    /// What the scene host sent of this entity that the copy has not taken in yet, in the order it came, each with when
    /// it came: from the word that said that the scene host's FSM came out of a move that the copy plays by itself
    /// first, the way the move goes out (see <see cref="WayOutOfTheMove"/>), which waits for the copy to get through
    /// the move (see <see cref="_cameSinceWaits"/>), and everything after it, taken in <see cref="_behind"/> later than
    /// it came. Each is this entity's to give back once it is taken in (EntityManager.ReleaseUpdate).
    /// </summary>
    private readonly Queue<(float At, BaseEntityUpdate Update)> _cameSince = new();

    /// <summary>
    /// How much later than it came the copy takes in what the scene host sends of it, in seconds: as much later as the
    /// copy came to the end of a move after the word that the scene host's FSM had come out of it, less what it has
    /// made up since by playing faster (see <see cref="MakeUpRate"/>).
    /// </summary>
    private float _behind;

    /// <summary>
    /// Whether <see cref="_cameSince"/> waits for the copy to get through a move that it plays by itself, which the
    /// scene host's FSM came out of first (see <see cref="_moveFinishBy"/>): the word that said so is the first of it.
    /// Or through an input of the local player that the scene host did not take (see <see cref="_playOnUntil"/>).
    /// </summary>
    private bool _cameSinceWaits;

    /// <summary>
    /// How much of how far it was behind the copy has made up by playing faster, in seconds, said once it caught up.
    /// </summary>
    private float _madeUp;

    /// <summary>
    /// The clip that the copy plays faster to make up being behind (see <see cref="MakeUpRate"/>), given its own speed
    /// back once the copy has caught up (see <see cref="ClipAtItsOwnSpeed"/>); or null.
    /// </summary>
    private tk2dSpriteAnimationClip? _clipMadeUp;

    /// <summary>
    /// Whether the copy takes in at once all that it was behind with (see <see cref="CatchUpWithWhatCameSince"/>): a
    /// move that the scene host's FSM went into meanwhile is over there or under way, and the copy follows the scene
    /// host through what is left of it rather than play it from its start (see <see cref="StartsAMove"/>).
    /// </summary>
    private bool _catchingUp;

    /// <summary>
    /// How much faster than it came the copy takes in what it is behind with, and plays the clips it starts meanwhile
    /// (see <see cref="UpdateAnimation"/>), until it has caught up: half as fast again (USER 11-12 "1.5倍吧"). It was
    /// three times as fast while the copy stood still ("三倍速会不会太多了（", "这用力过猛了吧").
    /// </summary>
    private const float MakeUpRate = 1.5f;

    /// <summary>
    /// How long the copy keeps what the scene host sent at the most, in seconds: as long as a move that it played on
    /// with twice over. What it kept longer is taken in at once, with all the rest (see <see cref="KeepsForLater"/>).
    /// </summary>
    private const float MostBehind = 2f * MoveFinishTime;

    /// <summary>
    /// What the scene host sent of the creature's health and the music of the room in what the copy keeps, taken in now
    /// (see <see cref="TakeInAsItComes"/>).
    /// </summary>
    private readonly List<EntityNetworkData> _asItComes = [];

    /// <summary>
    /// The first bytes of what the scene host sent of an action of an FSM (see <see cref="StateOfTheAction"/>).
    /// </summary>
    private static readonly byte[] ActionHead = new byte[2];

    /// <summary>
    /// Whether the copy is behind with what the scene host sent of it (see <see cref="_cameSince"/>).
    /// </summary>
    private bool IsBehind => _cameSince.Count > 0 || _cameSinceWaits || _behind > 0f;

    /// <summary>
    /// Whether the copy makes up being behind now by playing faster (see <see cref="PlayOnWhatCameSince"/>): not while
    /// it waits for a move or an answer, nor while it moves by itself by the physics.
    /// </summary>
    private bool MakesUp => _behind > 0f && !_cameSinceWaits && !WaitsForEcho && !MovesByItself();

    /// <summary>
    /// How fast the copy whose object this is plays the creature, against how fast it went in the game that runs it:
    /// half as fast again while it makes up being behind (see <see cref="MakeUpRate"/>), else as fast. A throw on a
    /// clock goes at that pace, as its state ends that much sooner, and throws as many (EntityFsmActions.RunTimed);
    /// what the creature threw flies at its own speed, by each game's own physics (USER 11-12 "投掷物按照我们的规则受
    /// 物理，应该不被加速").
    /// </summary>
    /// <param name="copy">The object of the copy.</param>
    internal static float PaceOf(GameObject copy) =>
        EntitiesByCopy.TryGetValue(copy, out var entity) && entity.MakesUp ? MakeUpRate : 1f;

    /// <summary>
    /// Whether the copy keeps what the scene host sent of it for later rather than take it in now: it is behind with
    /// what came since a move, or this is the word that the scene host's FSM came out of a move that the copy plays by
    /// itself first, after which what comes waits for the copy to get through the move too, or the word that it went
    /// into a move while the copy waits for its answer to an input of the local player, after which what comes waits
    /// for that answer, which is heard as it comes all the same (see <see cref="AnswersTheInput"/>). What it keeps is
    /// its own until it is taken in, but for the health and the music in it (see <see cref="TakeInAsItComes"/>). A
    /// death is taken in now, after all that the copy kept, and so is anything once the first of what it kept came
    /// longer ago than it goes behind at the most (see <see cref="MostBehind"/>), which a copy that took in nothing of
    /// it since would be. Not asked of the state of the room this game walks into, which comes after all that the copy
    /// kept (EntityManager.HandleEntityUpdate).
    /// </summary>
    /// <param name="update">What the scene host sent.</param>
    public bool KeepsForLater(BaseEntityUpdate update) {
        var reliable = update as ReliableEntityUpdate;
        if (!IsBehind) {
            if (reliable == null || Dies(reliable)) {
                return false;
            }

            if (ComesOutOfTheMoveFirst(reliable) is { } way) {
                WaitOutTheMove(way);
            } else if (WaitsForEcho && GoesIntoAMove(reliable)) {
                // Kept with all that comes after it until the answer says what became of the input (below)
                _cameSinceWaits = true;
                Logger.Info(
                    $"The scene host's FSM of entity {Id} went into a move before it answered the input to " +
                    $"'{_runHereReached}', so what it sends from here on waits for its answer"
                );
            } else {
                return false;
            }
        } else if (reliable != null && Dies(reliable)) {
            CatchUpWithWhatCameSince("the scene host's creature died");
            return false;
        } else if (_cameSince.Count > 0 && Time.unscaledTime - _cameSince.Peek().At > MostBehind) {
            CatchUpWithWhatCameSince("what it kept came too long ago");
            return false;
        }

        // The answer to the input that the copy waits for is heard as it comes, though what came before it waits: the
        // scene host's creature went into a move first, from which it may still have taken the input. Taken, all that
        // waited goes in at once before the answer, as what the copy holds for an answer goes (HearEcho), and the move
        // is not played from its start; not taken, the copy plays the input on to its end first, and the rest waits
        // for that
        if (WaitsForEcho && reliable != null && AnswersTheInput(reliable) is { } taken) {
            if (taken) {
                CatchUpWithWhatCameSince("the scene host took the input after all");
                return false;
            }

            PlayOnPastTheRefusal("the scene host did not take it");
        }

        if (reliable != null) {
            TakeInAsItComes(reliable);
        }

        _cameSince.Enqueue((Time.unscaledTime, update));
        return true;
    }

    /// <summary>
    /// Takes in now what the scene host sent of the creature's health and of the music of the room in something that
    /// the copy keeps for later: the health is the scene host's to settle as it comes, which the blows of the partner,
    /// seen to land where their figure is now, bring down - a boss whose health waited went on with what it had been,
    /// and showed it late for as long as the copy was behind - and the music is the room's. They are taken out of it
    /// and given back.
    /// </summary>
    /// <param name="update">What the scene host sent.</param>
    private void TakeInAsItComes(ReliableEntityUpdate update) {
        if (!update.UpdateTypes.Contains(EntityUpdateType.Data)) {
            return;
        }

        var data = update.GenericData;
        for (var i = 0; i < data.Count;) {
            if (data[i].Type is EntityComponentType.Health or EntityComponentType.Music) {
                _asItComes.Add(data[i]);
                data.RemoveAt(i);
            } else {
                i++;
            }
        }

        if (_asItComes.Count == 0) {
            return;
        }

        UpdateData(_asItComes, false, update.ReceivedSequence);
        foreach (var taken in _asItComes) {
            ObjectPool<EntityNetworkData>.Return(taken);
        }

        _asItComes.Clear();
    }

    /// <summary>
    /// Whether what the scene host sent says that its creature died: the copy then dies at once, after all that it was
    /// behind with, which is what the creature did before it died. Kept for later, the boss that the partner had killed
    /// fought on here, its replays still able to hurt the local player.
    /// </summary>
    /// <param name="update">What the scene host sent.</param>
    private static bool Dies(ReliableEntityUpdate update) {
        if (!update.UpdateTypes.Contains(EntityUpdateType.Data)) {
            return false;
        }

        foreach (var data in update.GenericData) {
            if (data.Type == EntityComponentType.Death) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How a word of the scene host's says that its FSM came out of the move that the copy plays by itself first, the
    /// way the move goes out (see <see cref="WayOutOfTheMove"/>): the state of the move it went out from, and the state
    /// it is said to be in - by the state it says the FSM went to, or by what the FSM did in a state, which can come
    /// before what said it went there, that being sent again in a later packet. Null while the copy plays no such move
    /// or is through it already, and for a word of nothing of the kind.
    /// </summary>
    /// <param name="update">The word.</param>
    private (FsmState From, string To)? ComesOutOfTheMoveFirst(ReliableEntityUpdate update) {
        if (!_runHereMove || _moveAtItsEnd || _runHere is not { } copyFsm || copyFsm == null ||
            _runHereCombo is not { } move) {
            return null;
        }

        // Named by the copy's FSM, as in UpdateHostFsmData
        var states = copyFsm.FsmStates;
        if (update.UpdateTypes.Contains(EntityUpdateType.HostFsm) &&
            update.HostFsmData.TryGetValue(_runHereIndex, out var hostFsm) &&
            hostFsm.Types.Contains(EntityHostFsmData.Type.State) && hostFsm.CurrentState < states.Length &&
            states[hostFsm.CurrentState] is var said &&
            OutOfTheMoveFrom(copyFsm, move, said) is { } from) {
            return (from, said.Name);
        }

        if (!update.UpdateTypes.Contains(EntityUpdateType.Data)) {
            return null;
        }

        foreach (var data in update.GenericData) {
            if (data.Type == EntityComponentType.Fsm && StateOfTheAction(data, _runHereIndex) is { } state &&
                OutOfTheMoveFrom(copyFsm, move, state) is { } exit) {
                return (exit, state.Name);
            }
        }

        return null;
    }

    /// <summary>
    /// The state of a move that the copy plays by itself from which its FSM comes to the given state out of the move by
    /// itself (see <see cref="WayOutOfTheMove"/>), or null, also if that state is in the move: all but the first one,
    /// gone into again while the copy is past it, which a move never does within itself (see
    /// <see cref="StartsAMove"/>).
    /// </summary>
    /// <param name="copyFsm">The FSM of the copy.</param>
    /// <param name="move">The states of the move.</param>
    /// <param name="state">The state.</param>
    private FsmState? OutOfTheMoveFrom(PlayMakerFSM copyFsm, HashSet<FsmState> move, FsmState state) {
        var again = state == _runHereFrom && copyFsm.Fsm.ActiveState != state;
        return move.Contains(state) && !again ? null : WayOutOfTheMove(copyFsm, move, state, _moveHostAt);
    }

    /// <summary>
    /// Whether a word of the scene host's says that its FSM went into a move that the copy plays by itself (see
    /// <see cref="StartsAMove"/>): the FSM that runs here for an input of the local player, which the scene host's
    /// creature, at the move by the time the input came, may not take.
    /// </summary>
    /// <param name="update">The word.</param>
    private bool GoesIntoAMove(ReliableEntityUpdate update) {
        if (_runHere is not { } copyFsm || copyFsm == null || !update.UpdateTypes.Contains(EntityUpdateType.HostFsm) ||
            !update.HostFsmData.TryGetValue(_runHereIndex, out var hostFsm) ||
            !hostFsm.Types.Contains(EntityHostFsmData.Type.State)) {
            return false;
        }

        var states = copyFsm.FsmStates;
        return hostFsm.CurrentState < states.Length && StartsAMove(
            copyFsm, states[hostFsm.CurrentState].Name, _fsmSnapshots[_runHereIndex].CurrentState, out _, out _
        );
    }

    /// <summary>
    /// What a word of the scene host's answers to the input of the local player that the copy waits for (see
    /// <see cref="HearEcho"/>), read without taking it in: whether its FSM took it where the copy's went, or null if
    /// the word holds no answer to it.
    /// </summary>
    /// <param name="update">The word.</param>
    private bool? AnswersTheInput(ReliableEntityUpdate update) {
        if (!update.UpdateTypes.Contains(EntityUpdateType.Data)) {
            return null;
        }

        foreach (var data in update.GenericData) {
            if (data.Type != EntityComponentType.Echo) {
                continue;
            }

            var echo = new global::SSMP.Networking.Packet.Packet(data.Packet.ToArray());
            var anticipation = echo.ReadByte();
            var fsmIndex = echo.ReadByte();
            var stateIndex = echo.ReadByte();
            if (echo.ReadUShort() != RequestTag || (sbyte) (anticipation - _runHereAwaited) < 0) {
                continue;
            }

            var states = _runHere!.Fsm.States;
            return fsmIndex == _runHereIndex && stateIndex < states.Length &&
                   states[stateIndex].Name == _runHereReached;
        }

        return null;
    }

    /// <summary>
    /// The state of an FSM of the copy that what the scene host sent of an action is of, read the way
    /// <see cref="UpdateData"/> reads it without taking anything of it in, or null if it is not of the FSM at the given
    /// index.
    /// </summary>
    /// <param name="data">What the scene host sent.</param>
    /// <param name="fsmIndex">The index of the FSM.</param>
    private FsmState? StateOfTheAction(EntityNetworkData data, int fsmIndex) {
        var withIndex = _fsms.Client.Count > 1;
        if (data.Packet.Length < (withIndex ? 3 : 2)) {
            return null;
        }

        data.Packet.CopyTo(ActionHead, 0, 0, 2);
        if (withIndex && ActionHead[0] != fsmIndex) {
            return null;
        }

        var stateIndex = withIndex ? ActionHead[1] : ActionHead[0];
        var states = _fsms.Client[fsmIndex].FsmStates;
        return stateIndex < states.Length ? states[stateIndex] : null;
    }

    /// <summary>
    /// Has the copy play on to its own end of the move that it plays by itself, which the scene host's FSM came out of
    /// first, before it takes in the word that said so and what comes after it (see <see cref="PlayOnWhatCameSince"/>).
    /// </summary>
    /// <param name="way">The state of the move that the scene host's FSM went out from, and where it was said to be.
    /// </param>
    private void WaitOutTheMove((FsmState From, string To) way) {
        _cameSinceWaits = true;
        _moveFinishBy = Time.unscaledTime + MoveFinishTime;
        _moveHostAt = way.From;
        Logger.Info(
            $"The scene host's FSM of entity {Id} came out of the move from '{way.From.Name}' to '{way.To}' first, " +
            "so its copy plays on to its own end of it, and what the scene host sends from here on waits for that"
        );
    }

    /// <summary>
    /// Takes in what came since, each as it is due (see <see cref="_behind"/>): from the copy's end of the move that it
    /// waited for, or of the input that the scene host did not take, the first word at once, and the rest as far apart
    /// as it came - again behind a move that the copy plays by itself, heard of in that, which the scene host's FSM
    /// came out of first too. Made up a little at a time, taken in faster than it came (see <see cref="MakeUpRate"/>).
    /// </summary>
    private void PlayOnWhatCameSince() {
        // Nor while the copy waits for the scene host's answer to an input of the local player, which says what becomes
        // of what waits (KeepsForLater)
        if (!IsBehind || WaitsForEcho) {
            return;
        }

        var now = Time.unscaledTime;
        if (_cameSinceWaits) {
            // Still playing on
            if (_moveFinishBy != null || _playOnUntil != null) {
                return;
            }

            // An input that the scene host did not take can be played on with nothing come meanwhile
            _cameSinceWaits = false;
            if (_cameSince.Count == 0) {
                return;
            }

            _behind = now - _cameSince.Peek().At;
            Logger.Info(
                $"The copy of entity {Id} goes on with the {_cameSince.Count} word(s) of the scene host's that came " +
                $"since, {_behind:0.00} s later than they came"
            );
        } else if (_behind > 0f) {
            // Not while the copy moves by itself by the physics, whose arcs go at their own speed: taken in sooner, the
            // next launch came before the copy got there, and put it there in one go (OwnMotionComponent)
            if (MovesByItself()) {
                ClipAtItsOwnSpeed();
            } else {
                var madeUp = Mathf.Min(_behind, Time.unscaledDeltaTime * (MakeUpRate - 1f));
                _behind -= madeUp;
                _madeUp += madeUp;
            }
        }

        while (_cameSince.Count > 0 && _cameSince.Peek().At + _behind <= now) {
            if (_cameSince.Peek().Update is ReliableEntityUpdate reliable &&
                ComesOutOfTheMoveFirst(reliable) is { } way) {
                WaitOutTheMove(way);
                return;
            }

            TakeInLater(_cameSince.Dequeue().Update);
        }

        if (_cameSince.Count == 0 && _behind <= 0f) {
            Logger.Info(
                $"The copy of entity {Id} has caught up with the scene host, {_madeUp:0.00} s of it made up by " +
                "playing faster"
            );
            _madeUp = 0f;
            ClipAtItsOwnSpeed();
        }
    }

    /// <summary>
    /// Takes in at once all that the copy is behind with, in the order it came, for what goes from where the scene
    /// host's creature is now: what the local player does to it, which the scene host plays there (see
    /// <see cref="PlayHere"/>, CoopHits), this game taking over the room, and the creature's death. A move that the
    /// copy plays on with stops where it is, and so does an input that the scene host did not take, and a move that
    /// the scene host's FSM went into meanwhile is not played here from its start (see <see cref="_catchingUp"/>).
    /// </summary>
    /// <param name="why">Why, for the log.</param>
    public void CatchUpWithWhatCameSince(string why) {
        if (!IsBehind) {
            return;
        }

        Logger.Info(
            $"The copy of entity {Id} takes in at once the {_cameSince.Count} word(s) of the scene host's that it " +
            (_cameSinceWaits ? "was to play after what it plays on" : $"is {_behind:0.00} s behind with") +
            $", as {why}"
        );
        var waited = _cameSinceWaits;
        _cameSinceWaits = false;
        _behind = 0f;
        _madeUp = 0f;
        ClipAtItsOwnSpeed();
        if (waited && _runHereMove) {
            CatchUpWithSceneHost("what the scene host sent since is taken in at once");
        } else if (_playOnUntil != null) {
            Logger.Info($"The copy of entity {Id} stops playing on the input that the scene host did not take");
            FollowSceneHost();
        }

        _catchingUp = true;
        while (_cameSince.Count > 0) {
            TakeInLater(_cameSince.Dequeue().Update);
        }

        _catchingUp = false;
    }

    /// <summary>
    /// Gives back all that the copy is behind with, none of it taken in.
    /// </summary>
    /// <param name="why">Why, for the log.</param>
    private void ForgetWhatCameSince(string why) {
        if (!IsBehind) {
            return;
        }

        Logger.Info(
            $"The copy of entity {Id} leaves the {_cameSince.Count} word(s) of the scene host's that it had not " +
            $"taken in, as {why}"
        );
        while (_cameSince.Count > 0) {
            EntityManager.ReleaseUpdate(_cameSince.Dequeue().Update);
        }

        _cameSinceWaits = false;
        _behind = 0f;
        _madeUp = 0f;
        ClipAtItsOwnSpeed();
    }

    /// <summary>
    /// Gives the clip that the copy played faster to make up being behind its own speed back, if it still plays it
    /// (see <see cref="_clipMadeUp"/>).
    /// </summary>
    private void ClipAtItsOwnSpeed() {
        if (_clipMadeUp != null && _animator.Client is { } animator && animator != null &&
            animator.CurrentClip == _clipMadeUp) {
            animator.ClipFps = _clipMadeUp.fps;
        }

        _clipMadeUp = null;
    }

    /// <summary>
    /// Takes in something that the scene host sent a while ago as it would have been taken in as it came, and gives it
    /// back. One that fails is written down, and what comes after it is still taken in: this is taken in from what the
    /// local player does and from the frame, where a throw would stop more than this.
    /// </summary>
    /// <param name="update">What the scene host sent.</param>
    private void TakeInLater(BaseEntityUpdate update) {
        try {
            switch (update) {
                case EntityUpdate unreliable:
                    TakeIn(unreliable, false);
                    break;
                case ReliableEntityUpdate reliable:
                    TakeIn(reliable, false, false);
                    break;
            }
        } catch (Exception e) {
            Logger.Error($"Could not take in what the scene host sent of entity {Id} a while ago:\n{e}");
        } finally {
            EntityManager.ReleaseUpdate(update);
        }
    }

    /// <summary>
    /// Takes in the unreliable kind of what the scene host sends of the entity: where its creature is, how it is scaled
    /// and turned, and the clip it plays.
    /// </summary>
    /// <param name="update">What it sent.</param>
    /// <param name="alreadyInSceneUpdate">Whether it came with the state of the room.</param>
    public void TakeIn(EntityUpdate update, bool alreadyInSceneUpdate) {
        // The stamp travels beside the position it describes rather than on its own, so it is read out here and
        // handed over with it. An update that carries none says nothing either way, which is not the same as saying
        // the scene host has taken nothing in.
        var anticipation = update.UpdateTypes.Contains(EntityUpdateType.Anticipation)
            ? update.Anticipations
            : null;

        if (update.UpdateTypes.Contains(EntityUpdateType.Position))
            UpdatePosition(update.Position, update.ReceivedSequence, anticipation);

        if (update.UpdateTypes.Contains(EntityUpdateType.Scale))
            UpdateScale(update.Scale, update.ReceivedSequence);

        if (update.UpdateTypes.Contains(EntityUpdateType.Animation))
            UpdateAnimation(
                update.AnimationId,
                (tk2dSpriteAnimationClip.WrapMode) update.AnimationWrapMode,
                alreadyInSceneUpdate,
                update.ReceivedSequence
            );

        if (update.UpdateTypes.Contains(EntityUpdateType.Rotation))
            UpdateRotation(update.Rotation, update.ReceivedSequence);
    }

    /// <summary>
    /// Takes in the reliable kind of what is sent of the entity: the clip its creature started, whether it is on, what
    /// the scene host's FSMs hold, and the rest of what its parts send.
    /// </summary>
    /// <param name="update">What was sent.</param>
    /// <param name="alreadyInSceneUpdate">Whether it came with the state of the room.</param>
    /// <param name="runsTheRoom">Whether this game is the scene host, which takes in only what the rest send of it.
    /// </param>
    public void TakeIn(ReliableEntityUpdate update, bool alreadyInSceneUpdate, bool runsTheRoom) {
        // What one player did to something that belongs to each of them alone is not carried to the other on the way
        // into a room.
        //
        // The server keeps whether each thing in a room is still there and how much health it has, and hands all of
        // it to whoever walks in afterwards. For nearly everything that is what should happen: an enemy the other
        // player killed is dead, and walking in should not raise it. But a creature carrying something that belongs
        // to one player is not shared - this mod gives each of them their own copy of it - and handing over "that
        // one is gone" took it away from a player who had never had it. One of them went through the room first, and
        // the other could not get theirs at all: not by walking in, not by being made the one who runs the room,
        // which reads the state of the copy it was just told to switch off.
        //
        // Only what was stored is turned away. While both of them are standing there it still behaves as one
        // creature, so neither of them sees the other fighting nothing.
        var isPersonal = alreadyInSceneUpdate && Type == EntityType.Aknid;

        // Active state and host FSM are driven by the scene host; clients are consumers only.
        if (!runsTheRoom) {
            // The clip first, as when it came in the unreliable kind, which is taken in before this one: the entity
            // is switched on or off and told the rest of that moment after it started the clip
            if (update.UpdateTypes.Contains(EntityUpdateType.Animation))
                UpdateAnimation(
                    update.AnimationId,
                    (tk2dSpriteAnimationClip.WrapMode) update.AnimationWrapMode,
                    alreadyInSceneUpdate,
                    update.ReceivedSequence
                );

            if (update.UpdateTypes.Contains(EntityUpdateType.Active) && !isPersonal)
                UpdateIsActive(update.IsActive);

            if (update.UpdateTypes.Contains(EntityUpdateType.HostFsm))
                UpdateHostFsmData(update.HostFsmData, alreadyInSceneUpdate);
        }

        if (update.UpdateTypes.Contains(EntityUpdateType.Data) && !isPersonal)
            UpdateData(update.GenericData, alreadyInSceneUpdate, update.ReceivedSequence);

        // A move that the scene host's FSM went into, which the copy plays by itself, starts once all of this is in
        StartMoveHeard();
    }
}
