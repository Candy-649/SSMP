using System;
using System.Collections.Generic;
using UnityEngine;

namespace SSMP.Animation.Effects.Tools;

/// <summary>
/// Follows a thing of a tool of the local player for the partner's copy of it: the number the copy goes by, and what
/// goes to the partner about it, always the thing itself first. The game keeps these things in pools, so the same
/// object is followed again, under a new number, each time it is thrown.
/// </summary>
internal class TrackedTool : MonoBehaviour {
    /// <summary>
    /// The number that the partner knows the thing by.
    /// </summary>
    public byte Id { get; private set; }

    /// <summary>
    /// The name of the prefab of the thing.
    /// </summary>
    public string PrefabName { get; private set; } = "";

    /// <summary>
    /// Whether the thing is followed, from being thrown until it is gone.
    /// </summary>
    public bool Live { get; private set; }

    /// <summary>
    /// The damagers of the thing, in the order the partner's copy has them too.
    /// </summary>
    public DamageEnemies[] Damagers { get; private set; } = [];

    /// <summary>
    /// Whether the partner has been told of the thing yet. Until then what happens to it waits, as the copy that it is
    /// about does not exist yet.
    /// </summary>
    private bool _announced;

    /// <summary>
    /// What happened to the thing before the partner was told of it.
    /// </summary>
    private readonly List<byte[]> _held = [];

    /// <summary>
    /// The state changes that go at the end of the frame, by the index of the state machine and the state, with the
    /// numbers that go with them, once the state they change to has done what it does as it starts.
    /// </summary>
    private readonly List<(byte Fsm, string State, float[] Floats)> _changes = [];

    /// <summary>
    /// Whether the state of the thing, which its own code moves, goes to the partner as soon as it has settled.
    /// </summary>
    private bool _motionPending;

    /// <summary>
    /// The seconds until the state of a thing whose state goes all the time goes again.
    /// </summary>
    private float _streamTimer;

    /// <summary>
    /// Called with each message about the thing once it may go to the partner.
    /// </summary>
    [NonSerialized]
    public Action<byte[]>? Send;

    /// <summary>
    /// Writes the message that tells the partner of the thing, which a thing whose state goes all the time sends
    /// again and again, so that it also makes the partner's copy when there is none yet.
    /// </summary>
    [NonSerialized]
    public Func<byte[]>? WriteSpawn;

    /// <summary>
    /// Called when the thing is gone, so that nothing is kept about it.
    /// </summary>
    [NonSerialized]
    public Action<TrackedTool>? Ended;

    /// <summary>
    /// Starts following the thing under a new number.
    /// </summary>
    public void Begin(byte id, string prefabName) {
        Id = id;
        PrefabName = prefabName;
        Live = true;
        Damagers = GetComponentsInChildren<DamageEnemies>(true);
        _announced = false;
        _motionPending = false;
        _streamTimer = ToolCopyRules.GetStreamInterval(prefabName);
        _held.Clear();
        _changes.Clear();
    }

    /// <summary>
    /// Tells the partner of the thing, and then of what happened to it since.
    /// </summary>
    public void Announce(byte[] spawn) {
        if (!Live || _announced) {
            return;
        }

        _announced = true;
        Send?.Invoke(spawn);
        foreach (var message in _held) {
            Send?.Invoke(message);
        }

        _held.Clear();
    }

    /// <summary>
    /// Sends a message about the thing, or keeps it until the partner has been told of the thing.
    /// </summary>
    public void Post(byte[] message) {
        if (!Live) {
            return;
        }

        if (_announced) {
            Send?.Invoke(message);
        } else {
            _held.Add(message);
        }
    }

    /// <summary>
    /// Sends a state change at the end of the frame, with the numbers of the state machine as they were when it
    /// changed.
    /// </summary>
    public void QueueStateChange(byte fsmIndex, string state, float[] floats) {
        if (Live) {
            _changes.Add((fsmIndex, state, floats));
        }
    }

    /// <summary>
    /// Sends the state of the thing, which its own code moves, as soon as it has settled after a change.
    /// </summary>
    public void QueueMotion() {
        if (Live) {
            _motionPending = true;
        }
    }

    private void LateUpdate() {
        SendStateChanges();
        SendMotion();
        Stream();
    }

    /// <summary>
    /// Sends the thing again if its state goes all the time and the time for it has come.
    /// </summary>
    private void Stream() {
        var interval = ToolCopyRules.GetStreamInterval(PrefabName);
        if (!_announced || interval <= 0f || WriteSpawn == null) {
            return;
        }

        _streamTimer -= Time.deltaTime;
        if (_streamTimer > 0f) {
            return;
        }

        _streamTimer = interval;
        Post(WriteSpawn());
    }

    /// <summary>
    /// Sends the state of the thing if a change is waiting and the thing has settled after it.
    /// </summary>
    private void SendMotion() {
        if (!_motionPending || ToolCopyRules.GetState(PrefabName) is not { } state || !state.IsSettled(gameObject)) {
            return;
        }

        _motionPending = false;
        Post(ToolMessages.WriteMotion(Id, ToolSnapshot.Of(gameObject), state.Write(gameObject)));
    }

    /// <summary>
    /// Sends the state changes of this frame, each with where the thing is and how it moves after it.
    /// </summary>
    private void SendStateChanges() {
        if (_changes.Count == 0) {
            return;
        }

        var snapshot = ToolSnapshot.Of(gameObject);
        foreach (var (fsmIndex, state, floats) in _changes) {
            Post(ToolMessages.WriteState(Id, fsmIndex, state, snapshot, floats));
        }

        _changes.Clear();
    }

    private void OnDisable() {
        End();
    }

    /// <summary>
    /// Stops following the thing, which is gone, or which the pool takes back to throw again while it still flies.
    /// </summary>
    public void End() {
        if (!Live) {
            return;
        }

        SendStateChanges();
        if (_announced) {
            Send?.Invoke(ToolMessages.WriteSimple(ToolMessageKind.End, Id));
        }

        Live = false;
        _held.Clear();
        Ended?.Invoke(this);
    }
}
