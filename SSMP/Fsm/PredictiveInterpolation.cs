using SSMP.Networking;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Fsm;

/// <summary>
/// Moves a copy smoothly between the positions its sender sends: on from the last one at the speed they show it
/// going, until either the packets that keep coming show that it stopped or the connection has been quiet for too
/// long, with how far the next position puts it from there smoothed away rather than jumped, over a time that follows
/// the round trip.
/// </summary>
internal class PredictiveInterpolation : MonoBehaviour {
    #region Settings

    /// <summary>
    /// Squared distance threshold for valid prediction vs teleport.
    /// </summary>
    [Header("Prediction Limits")] [SerializeField]
    private float snapThresholdSq = 16.0f;

    /// <summary>
    /// Minimum squared speed to consider moving.
    /// </summary>
    [SerializeField] private float minPredictionSpeedSq = 0.001f;

    /// <summary>
    /// Maximum allowed speed for prediction to prevent explosions on lag spikes.
    /// </summary>
    [SerializeField] private float maxProjectedSpeed = 50.0f;

    [Header("Network Timing")]
    [Tooltip("The fixed tick rate of your server (e.g. 0.05 for 20 ticks/sec).")]
    [SerializeField]
    private float serverDeltaTime = 1.0f / 20.0f;

    [SerializeField] private float minServerDeltaTime = 1.0f / 128.0f; // ~7ms

    /// <summary>
    /// The longest <see cref="serverDeltaTime"/> may be measured to be, in seconds.
    ///
    /// It is what the tick was set to before anything measured it, so the measurement can only ever shorten it. That
    /// matters: too long a tick reads every speed as slower than it is, which lags, and too short a tick reads them
    /// as faster, which overshoots and then has to be pulled back. Lagging is the side to err on, so the guess stands
    /// as the ceiling.
    /// </summary>
    private const float MaxServerDeltaTime = 1.0f / 20.0f;

    /// <summary>
    /// How much of each gap between two positions goes into <see cref="serverDeltaTime"/>.
    ///
    /// Small, because one gap says almost nothing - a position is only sent when something moves, and the way it
    /// travels drops what it cannot deliver - while a hundred of them say it exactly.
    /// </summary>
    private const float ServerDeltaTimeLearnRate = 0.05f;

    [Header("Smoothing Weights")] [SerializeField, Range(0f, 1f)]
    private float velocityBlendFactor = 0.7f;

    [Header("RTT Adaptation")] [SerializeField]
    private bool enableRttAdaptation = true;

    [SerializeField] private float rttSmoothingSpeed = 3.0f; // How fast to transition between RTT tiers

    #endregion

    #region Going On By Itself

    /// <summary>
    /// How long a copy goes on at full speed after its last position while nothing says that it stopped, in seconds.
    ///
    /// Positions come about sixty times a second, and a connection that drops some goes quiet for a tenth of a second
    /// or a quarter. A copy used to stop after two or three that didn't come (30 to 60 ms), slide back toward its
    /// last position and jump on when the next one came, so that a creature went on in fits and starts through every
    /// stretch of loss (USER 11-01 "丢包的时候，那个怪一停一停的"). Most things go on the way they were going for that
    /// long, and so the copy does.
    /// </summary>
    private const float CoastFullSpeedTime = 0.25f;

    /// <summary>
    /// By when, after its last position, a copy that is still told nothing has slowed to a stop, in seconds. From
    /// <see cref="CoastFullSpeedTime"/> to this its speed falls off evenly, so it doesn't stop dead.
    /// </summary>
    private const float CoastStopTime = 0.4f;

    /// <summary>
    /// The farthest a copy goes on from its last position, in units. What turns out wrong of where it went is smoothed
    /// away when the next position comes, and much more than this would be a slide that anyone sees.
    /// </summary>
    private const float CoastMaxDistance = BigMoveDistance;

    /// <summary>
    /// How many packets in a row, at the least, have to come without a position of a moving copy for it to count as
    /// stopped. A thing is sent only while it moves, so packets that keep coming without it mean it stopped, while a
    /// quiet connection brings no packets at all. One or two often come without it while it still moves, since the
    /// ticks of its sender and of the server don't line up.
    /// </summary>
    private const int MinStopPackets = 3;

    /// <summary>
    /// The most packets an ordinary step between two positions of a moving copy is taken to span, for how many in a
    /// row are usual for it (<see cref="_typicalCover"/>). More is a quiet stretch, not a step.
    /// </summary>
    private const int MaxTypicalCover = 4;

    /// <summary>
    /// How much of each step goes into <see cref="_typicalCover"/>.
    /// </summary>
    private const float CoverLearnRate = 0.1f;

    /// <summary>
    /// After how long going on from a position a copy looks out for walls, in seconds. As far as this it goes on as
    /// it always did, which positions sixty times a second seldom let it get far in; past it, it goes on by itself.
    /// </summary>
    private const float WallCheckAfter = 0.05f;

    /// <summary>
    /// How far short of a wall a copy that goes on by itself stops, in units: where a copy is placed is near the
    /// middle of its body, which reaches out about this far.
    /// </summary>
    private const float WallLookAhead = 0.3f;

    /// <summary>
    /// The layer of the ground and the walls (8).
    /// </summary>
    private const int TerrainMask = 1 << 8;

    /// <summary>
    /// A wall closer than this, in units, is one the copy is inside of, like a creature that goes through the ground,
    /// and is no reason to stop.
    /// </summary>
    private const float InsideTerrain = 0.001f;

    #endregion

    #region RTT Tier Definitions

    // RTT thresholds (in milliseconds)
    private static readonly float[] RttThresholds = [20f, 50f, 100f, 180f];

    // Visual correction time per tier: LAN, Excellent, Good, Fair, Poor
    // Higher values = smoother but more latent visual corrections
    private static readonly float[] CorrectionTimes = [0.05f, 0.06f, 0.10f, 0.14f, 0.20f];

    #endregion

    #region State Variables

    // The last authoritative position received from the server (used for velocity calculation)
    private Vector3 _lastServerPosition;

    // Where the object is reckoned to be now: its last position, and as far on from there as it went by itself
    private Vector3 _logicalPosition;

    // How fast and which way its positions show it going, which it goes on at
    private Vector3 _velocity;

    /// <summary>
    /// How fast and which way the object is seen to move, worked out from the positions it was sent, and slowing with
    /// it as it slows to a stop by itself.
    /// </summary>
    public Vector3 Velocity => _velocity * SpeedAfter(_reckoned);

    /// <summary>
    /// How far the object is drawn from where the sender's positions have it, which the smoothing takes away.
    /// </summary>
    public Vector3 VisualOffset => _visualOffset;

    private float _timeSinceLastPacket;
    private float _lastUpdateTime;

    /// <summary>
    /// How long the object has gone on from its last position, in seconds.
    /// </summary>
    private float _reckoned;

    /// <summary>
    /// How far the object has gone on from its last position, in units.
    /// </summary>
    private float _reckonedDistance;

    /// <summary>
    /// What <see cref="LinkWatch.Heard"/> was when the last position came.
    /// </summary>
    private int _heardAtPosition;

    /// <summary>
    /// How many packets a step between two positions of the object usually spans, while it moves: one at sixty
    /// positions a second, two or three from a sender that draws thirty or twenty frames a second, which sends a
    /// position only every second or third packet. So many in a row without a position are no stop.
    /// </summary>
    private float _typicalCover = 1f;

    /// <summary>
    /// Whether the object went on by itself through a quiet stretch since its last position, for the log.
    /// </summary>
    private bool _coasted;

    // Visual Offsets
    private Vector3 _visualOffset;
    private Vector3 _visualOffsetVelocity;

    private Transform _cachedTransform = null!;
    private uint _lastServerSequenceId;
    private bool _predictionDisabled;
    private bool _isInitialized;
    private bool _hasNewServerData;

    // RTT-Adaptive values (smoothly interpolated)
    private float _adaptedCorrectionTime;
    private float _currentRtt;

    #endregion

    private void Awake() {
        // One-time: runs once when this object is first created for the pool
        EnsureTransformCached();
    }

    private void OnDisable() {
        // Container recycled to pool — discard old player's init state so
        // OnEnable takes the fresh-spawn path instead of snapping to stale data
        _isInitialized = false;
        _hasNewServerData = false;
        // Reset sequence tracking so a recycled container doesn't discard the
        // new player's initial packets just because their sequence IDs happen
        // to be lower than the previous player's last seen value.
        _lastServerSequenceId = 0;
    }

    private void OnEnable() {
        // Fresh spawn from pool — seed all state to match the position SpawnPlayer
        // set on the transform before activating, with clean defaults for prediction
        EnsureTransformCached();
        _lastServerPosition = _cachedTransform.position;
        _logicalPosition = _cachedTransform.position;
        _lastUpdateTime = Time.time;
        _velocity = Vector3.zero;
        _visualOffset = Vector3.zero;
        _visualOffsetVelocity = Vector3.zero;
        _typicalCover = 1f;
        TookPosition();

        // Initialize the adaptive value from the "Good" tier so the first few frames
        // after spawn match the tier system instead of arbitrary magic numbers.
        // AdaptToRTT will smoothly converge from here once real RTT data flows in.
        _adaptedCorrectionTime = CorrectionTimes[2];
        _currentRtt = RttThresholds[1];
    }

    /// <summary>
    /// Manually update interpolation. Call this from a centralized update loop.
    /// </summary>
    /// <param name="dt">The delta time for this frame.</param>
    public void ManualUpdate(float dt) {
        if (_predictionDisabled) return;

        // Skip integration until the first authoritative packet arrives. Without
        // this guard, a freshly-recycled container would run extrapolation
        // against its seed position before the server has confirmed anything.
        if (!_hasNewServerData) return;

        _timeSinceLastPacket += dt;
        TellWhatQuietStretchesDid();

        // 1. Going on from the last position, until the packets say that it stopped
        if (_velocity.x != 0f || _velocity.y != 0f || _velocity.z != 0f) {
            // The packets that came since its last position, or since the connection came back from going quiet if
            // that was later: a run of them without a position means it stopped, while a quiet connection brings none
            var returned = LinkWatch.HeardAtReturn;
            var heardSince = LinkWatch.Heard - Mathf.Max(_heardAtPosition, returned);
            if (heardSince >= StopPackets) {
                // Where its last position has it, unless the connection went quiet since: it went on by itself
                // meanwhile, maybe as the sender's object did before it stopped, which is in positions still to come
                Stop(returned <= _heardAtPosition);
            } else {
                GoOn(dt, heardSince);
            }
        }

        // 2. Smooth Visual Offsets - Padé Approximation for performance
        if (_visualOffset.x != 0f || _visualOffset.y != 0f || _visualOffset.z != 0f) {
            var omega = 2f / _adaptedCorrectionTime;
            var x = omega * dt;
            var exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);

            _visualOffset.x = (_visualOffset.x + _visualOffsetVelocity.x * dt) * exp;
            _visualOffset.y = (_visualOffset.y + _visualOffsetVelocity.y * dt) * exp;
            _visualOffset.z = (_visualOffset.z + _visualOffsetVelocity.z * dt) * exp;

            _visualOffsetVelocity.x = (_visualOffsetVelocity.x - omega * _visualOffset.x) * exp;
            _visualOffsetVelocity.y = (_visualOffsetVelocity.y - omega * _visualOffset.y) * exp;
            _visualOffsetVelocity.z = (_visualOffsetVelocity.z - omega * _visualOffset.z) * exp;
        }

        // 3. Apply Final State - direct position set
        var applied = new Vector3(
            _logicalPosition.x + _visualOffset.x,
            _logicalPosition.y + _visualOffset.y,
            _logicalPosition.z + _visualOffset.z
        );

        // As far as its own speed takes it in this frame is no jump, however long the frame was
        SayItMovedALongWay(
            "what it was predicted to be doing", _cachedTransform.position, applied, Velocity.magnitude * dt
        );

        _cachedTransform.position = applied;
    }

    /// <summary>
    /// How many packets in a row without a position of the object mean that it stopped.
    /// </summary>
    private int StopPackets => Mathf.Max(MinStopPackets, Mathf.RoundToInt(2f * _typicalCover) + 1);

    /// <summary>
    /// Takes the object on from where it is reckoned to be by one frame, the way its positions showed it going: at
    /// full speed at first, then slowing to a stop (<see cref="CoastFullSpeedTime"/>, <see cref="CoastStopTime"/>),
    /// no further than <see cref="CoastMaxDistance"/> from its last position, and not on into a wall.
    /// </summary>
    /// <param name="dt">The length of the frame.</param>
    /// <param name="heardSince">The packets that came since its last position, for the log.</param>
    private void GoOn(float dt, int heardSince) {
        var from = _reckoned;
        var to = from + dt;
        _reckoned = to;

        var step = _velocity * (GoneOnAfter(to) - GoneOnAfter(from));
        var length = step.magnitude;
        var stoppedBy = to >= CoastStopTime ? StopCause.Time : StopCause.None;

        // No further than the limit in all
        var left = CoastMaxDistance - _reckonedDistance;
        if (length >= left) {
            step = left > 0f && length > 0f ? step * (left / length) : Vector3.zero;
            length = Mathf.Max(left, 0f);
            stoppedBy = StopCause.Distance;
        }

        // And not into a wall, once it goes on by itself
        if (to > WallCheckAfter) {
            var flatLength = Mathf.Sqrt(step.x * step.x + step.y * step.y);
            if (flatLength > 0f) {
                var direction = new Vector2(step.x / flatLength, step.y / flatLength);
                if (Helper.Raycast2DHit(
                        new Vector2(_logicalPosition.x, _logicalPosition.y), direction, flatLength + WallLookAhead,
                        TerrainMask, out var hit
                    ) && hit.distance > InsideTerrain) {
                    var free = Mathf.Max(0f, hit.distance - WallLookAhead);
                    if (free < flatLength) {
                        var kept = free / flatLength;
                        step *= kept;
                        length *= kept;
                        stoppedBy = StopCause.Wall;
                    }
                }
            }
        }

        _logicalPosition += step;
        _reckonedDistance += length;

        // For the log: on further than positions every 17 ms would have let it go, while hardly any packet came
        if (!_coasted && to > CoastCountsAfter && heardSince <= 1) {
            _coasted = true;
            _quietCoasts++;
        }

        if (_coasted) {
            _quietLongestCoast = Mathf.Max(_quietLongestCoast, to);
            _quietFarthestCoast = Mathf.Max(_quietFarthestCoast, _reckonedDistance);
        }

        if (stoppedBy == StopCause.None) {
            return;
        }

        _velocity = Vector3.zero;
        if (!_coasted) {
            return;
        }

        switch (stoppedBy) {
            case StopCause.Time:
                _quietStoppedByTime++;
                break;
            case StopCause.Distance:
                _quietStoppedByDistance++;
                break;
            case StopCause.Wall:
                _quietStoppedByWall++;
                break;
        }
    }

    /// <summary>
    /// Stops the object, which the packets that keep coming without a position of it say it did.
    /// </summary>
    /// <param name="goBack">
    /// Whether it stopped where its last position has it, which the copy then slides back to from as far as it went
    /// on. Not so when the connection went quiet since that position: whatever the sender's object did meanwhile is in
    /// positions that are still to come, and the copy stays where it got to until they do.
    /// </param>
    private void Stop(bool goBack) {
        if (goBack) {
            _visualOffset += _logicalPosition - _lastServerPosition;
            _logicalPosition = _lastServerPosition;
        }

        _velocity = Vector3.zero;
    }

    /// <summary>
    /// How far, in seconds at full speed, an object has gone on after the given time from its last position: all of
    /// it at first, then less and less until it stands.
    /// </summary>
    /// <param name="time">The time since its last position, in seconds.</param>
    private static float GoneOnAfter(float time) {
        const float slowing = CoastStopTime - CoastFullSpeedTime;
        if (time <= CoastFullSpeedTime) {
            return time;
        }

        if (time >= CoastStopTime) {
            return CoastFullSpeedTime + slowing * 0.5f;
        }

        var slowed = time - CoastFullSpeedTime;
        return CoastFullSpeedTime + slowed - slowed * slowed / (2f * slowing);
    }

    /// <summary>
    /// How much of its speed an object that goes on by itself still has after the given time from its last position.
    /// </summary>
    /// <param name="time">The time since its last position, in seconds.</param>
    private static float SpeedAfter(float time) {
        if (time <= CoastFullSpeedTime) {
            return 1f;
        }

        return time >= CoastStopTime ? 0f : (CoastStopTime - time) / (CoastStopTime - CoastFullSpeedTime);
    }

    /// <summary>
    /// Starts the reckoning of how far the object goes on afresh, from a position that was just taken.
    /// </summary>
    private void TookPosition() {
        _heardAtPosition = LinkWatch.Heard;
        _reckoned = 0f;
        _reckonedDistance = 0f;
        _timeSinceLastPacket = 0f;
        _coasted = false;
    }

    /// <summary>
    /// Updates the entity's position using the latest prediction state.
    /// </summary>
    /// <param name="newPos">The new authoritative position.</param>
    /// <param name="snapshotsSinceLast">
    /// How many of the sender's ticks this position covers. One for an ordinary update; more when updates in between
    /// were deliberately put aside, so that the speed worked out from the distance is not worked out against the
    /// length of a single tick and come out several times too fast.
    /// </param>
    public void SetNewPosition(Vector3 newPos, int snapshotsSinceLast = 1) {
        SetNewState(newPos, snapshotsSinceLast);
    }

    /// <summary>
    /// Updates the prediction state with a new authoritative snapshot from the server.
    /// </summary>
    /// <param name="newPos">The new authoritative position.</param>
    /// <param name="snapshotsSinceLast">Number of ticks since the last update (for velocity calc).</param>
    /// <param name="sequenceId">Optional sequence ID to discard out-of-order packets.</param>
    /// <param name="isTeleport">If true, snaps immediately without smoothing.</param>
    public void SetNewState(
        Vector3 newPos,
        int snapshotsSinceLast = 1,
        uint sequenceId = 0,
        bool isTeleport = false
    ) {
        EnsureTransformCached();

        // 1. Sequence Safety (Allow 0 for non-sequenced updates)
        if (sequenceId != 0) {
            // Unsigned arithmetic handles wrap-around automatically
            if ((int) (sequenceId - _lastServerSequenceId) <= 0) return;
            _lastServerSequenceId = sequenceId;
        }

        _hasNewServerData = true;

        // 2. Initialization Safety
        if (!_isInitialized || _predictionDisabled) {
            _isInitialized = true;
            ForceSnap(newPos);
            return;
        }

        // 3. Time Safety
        var now = Time.time;
        float actualDeltaTime;

        // How often positions really arrive, measured rather than set. What it was set to was a guess at twenty a
        // second, and they come about sixty times a second - so every speed read off two of them came out near a
        // third of what it really was, and everything followed its own position from a little behind.
        //
        // Only gaps between two positions that came one after the other are measured, and only those that could be
        // one tick at all. A gap longer than the ceiling is something that had nothing to say in between, since
        // nothing sends a position while it stands still; a gap shorter than the floor is two that arrived together
        // after one of them was held up. Neither is a tick, and neither is counted.
        if (snapshotsSinceLast == 1 && _lastUpdateTime > 0f) {
            var gap = now - _lastUpdateTime;
            if (gap >= minServerDeltaTime && gap <= MaxServerDeltaTime) {
                serverDeltaTime += (gap - serverDeltaTime) * ServerDeltaTimeLearnRate;
            }
        }

        // A gap counted in packets is only a gap in time while one packet is one tick, which is the ordinary case but
        // not every case: a large message that has to be cut into pieces sends several packets where there would
        // usually be one, and the numbering counts them all. Left alone, a burst of those would be read as a long
        // silence and everything moving through it would be read as crawling. The clock cannot be argued with, so it
        // is the ceiling: however many packets went by, no more time passed than has passed.
        if (snapshotsSinceLast > 1 && _lastUpdateTime > 0f && serverDeltaTime > 0f) {
            var ticksOnTheClock = Mathf.CeilToInt((now - _lastUpdateTime) / serverDeltaTime);
            if (ticksOnTheClock >= 1 && snapshotsSinceLast > ticksOnTheClock) {
                snapshotsSinceLast = ticksOnTheClock;
            }
        }

        if (snapshotsSinceLast > 0) {
            actualDeltaTime = snapshotsSinceLast * serverDeltaTime;
        } else {
            // Fallback: calculate local time difference
            actualDeltaTime = now - _lastUpdateTime;
        }

        // Detect bunched packets (arriving too fast)
        var isPacketBunched = actualDeltaTime < minServerDeltaTime;

        // Clamp for safety in calculations
        if (actualDeltaTime < 0.0001f) actualDeltaTime = serverDeltaTime;

        _lastUpdateTime = now;

        // How many packets a step of it usually spans while it moves, for how many in a row without a position of it
        // mean that it stopped
        var movementSq = (newPos - _lastServerPosition).sqrMagnitude;
        var playerStopped = movementSq < minPredictionSpeedSq;
        if (!playerStopped && snapshotsSinceLast is >= 1 and <= MaxTypicalCover) {
            _typicalCover += (snapshotsSinceLast - _typicalCover) * CoverLearnRate;
        }

        // 4. Snap / Teleport Check
        // Compare against logical position to detect large prediction errors
        var distSq = (newPos - _logicalPosition).sqrMagnitude;
        var snaps = isTeleport || distSq > snapThresholdSq;
        NoteWhereItCameTo(newPos, snaps);
        if (snaps) {
            ForceSnap(newPos);
            return;
        }

        // 5. Visual Offset Calculation (Continuity Logic)
        // Maintain visual continuity: VisualPos = LogicalPos + Offset.
        // NewOffset = OldVisualPos - NewLogicalPos (which will be newPos)
        // This ensures the object doesn't visually jump when logical position updates.
        _visualOffset = _cachedTransform.position - newPos;

        // FIX: Dampen velocity slightly on new packet to reduce oscillation from SmoothDamp
        _visualOffsetVelocity *= 0.9f;

        // 6. Velocity Estimation
        // First, check if player has stopped (must be done even for bunched packets)
        if (playerStopped) {
            // Player has stopped - zero velocity immediately to prevent gliding
            _velocity = Vector3.zero;
        } else if (!isPacketBunched) {
            // Only update velocity from non-bunched packets (to avoid noise)
            var instantVel = (newPos - _lastServerPosition) / actualDeltaTime;

            // Clamp velocity to prevent physics explosions on lag spikes
            if (instantVel.sqrMagnitude > maxProjectedSpeed * maxProjectedSpeed) {
                instantVel = instantVel.normalized * maxProjectedSpeed;
            }

            // Blend toward new velocity for smooth prediction
            _velocity = Vector3.Lerp(_velocity, instantVel, velocityBlendFactor);
        }
        // If bunched and still moving, keep previous velocity estimate

        // 7. Commit State
        _lastServerPosition = newPos;
        // Snap logical position to authoritative state
        _logicalPosition = newPos;
        TookPosition();
    }

    /// <summary>
    /// Takes where the object is standing now as what it should go on looking like, so that the distance between
    /// that and where the scene host has it is smoothed away rather than jumped.
    ///
    /// For the end of a knockback that the local game made up: while one is running, this is not driving the object
    /// at all and its own idea of where the object is has gone on running without it. Picking that up again writes
    /// the object straight back there in one frame, which is the jump the knockback was held for in the first place.
    /// </summary>
    public void KeepVisualPosition() {
        EnsureTransformCached();

        _visualOffset = _cachedTransform.position - _logicalPosition;
        _visualOffsetVelocity = Vector3.zero;
    }

    /// <summary>
    /// Works out how fast an object goes from one more step of it, the way SetNewState reads a step between two
    /// positions of the sender: one too small to be moving at all stops it, one too long to be anything but a jump
    /// stops it as the snap there does, and any other blends into how fast it went. The ceiling that SetNewState puts
    /// on the count of ticks by the clock is left out: too many ticks only read the step as slower, which is the side
    /// to err on (see <see cref="MaxServerDeltaTime"/>).
    /// </summary>
    /// <param name="velocity">How fast it went before the step.</param>
    /// <param name="step">The step.</param>
    /// <param name="ticks">How many ticks of the sender the step took.</param>
    public Vector3 ReadVelocity(Vector3 velocity, Vector3 step, int ticks) {
        var stepSq = step.sqrMagnitude;
        if (stepSq < minPredictionSpeedSq || stepSq > snapThresholdSq) {
            return Vector3.zero;
        }

        var instant = step / (Mathf.Max(1, ticks) * serverDeltaTime);
        if (instant.sqrMagnitude > maxProjectedSpeed * maxProjectedSpeed) {
            instant = instant.normalized * maxProjectedSpeed;
        }

        return Vector3.Lerp(velocity, instant, velocityBlendFactor);
    }

    /// <summary>
    /// Takes a position of the sender as where the object is now, going on at the given velocity, and keeps where the
    /// object is standing as what it looks like, so that the distance between the two is smoothed away.
    ///
    /// For the end of the wait on a knockback that the local game played first (Entity.UpdatePosition). The copy went
    /// on moving by itself meanwhile, the way the sender's positions showed the creature going without the knockback,
    /// and that is the velocity it goes on at. Worked out the usual way instead, from the last position taken before
    /// the wait, it would have the whole of the knockback in it and slide the copy on past where it stops.
    /// </summary>
    /// <param name="position">The position.</param>
    /// <param name="velocity">How fast and which way the object goes on from it.</param>
    public void Rebase(Vector3 position, Vector3 velocity) {
        EnsureTransformCached();

        _hasNewServerData = true;

        // Further from where the copy stands than smoothing can carry it in time is a jump either way
        if (!_isInitialized || _predictionDisabled ||
            (position - _cachedTransform.position).sqrMagnitude > snapThresholdSq) {
            _isInitialized = true;
            ForceSnap(position);
            return;
        }

        if (velocity.sqrMagnitude > maxProjectedSpeed * maxProjectedSpeed) {
            velocity = velocity.normalized * maxProjectedSpeed;
        }

        _visualOffset = _cachedTransform.position - position;
        _visualOffsetVelocity = Vector3.zero;
        _velocity = velocity;

        _lastServerPosition = position;
        _logicalPosition = position;
        TookPosition();
        _lastUpdateTime = Time.time;
    }

    /// <summary>
    /// How far something may be moved in one go before it is worth writing down, in units.
    ///
    /// About the width of a player. Below that nobody can see a jump at all; above it, it is the thing being
    /// reported as an enemy appearing somewhere it is not.
    /// </summary>
    private const float BigMoveDistance = 1.5f;

    /// <summary>
    /// The shortest time between two lines about the same object, in seconds, so that one thing moving badly for a
    /// second cannot fill the log.
    /// </summary>
    private const float BigMoveLogInterval = 0.5f;

    /// <summary>
    /// When this object may next say that it was moved a long way.
    /// </summary>
    private float _nextBigMoveLogTime;

    /// <summary>
    /// Writes down that this object was moved further in one go than anyone could miss, and what did it.
    ///
    /// Here because nothing else can see it. An enemy that appears on the wrong side of a player for three frames and
    /// is back before anyone can look leaves no trace at all: its health is right, its state is right, and the only
    /// wrong thing about it was where it was standing, which nothing writes down. This does.
    /// </summary>
    /// <param name="what">What moved it.</param>
    /// <param name="from">Where it was.</param>
    /// <param name="to">Where it was put.</param>
    /// <param name="explained">How much of the way its own speed accounts for.</param>
    private void SayItMovedALongWay(string what, Vector3 from, Vector3 to, float explained = 0f) {
        var distance = (to - from).magnitude;
        if (distance - explained < BigMoveDistance || Time.unscaledTime < _nextBigMoveLogTime) {
            return;
        }

        _nextBigMoveLogTime = Time.unscaledTime + BigMoveLogInterval;

        Logger.Info(
            $"{name} was moved {distance:0.00} by {what}, from ({from.x:0.00}, {from.y:0.00}) to " +
            $"({to.x:0.00}, {to.y:0.00}); it was going ({_velocity.x:0.0}, {_velocity.y:0.0}) and the last position " +
            $"of it arrived {_timeSinceLastPacket * 1000f:0} ms ago"
        );
    }

    /// <summary>
    /// Puts the object where the sender's positions have it now, dropping what the smoothing still has to carry it
    /// through of a step that the sender's object took, for something that moves it on from there by itself: a move
    /// that the copy of a creature plays by itself starts where the creature started it. Only the rest of a step longer
    /// than anyone could miss (see <see cref="BigMoveDistance"/>): what is left of shorter ones is the smoothing's own,
    /// which moves were started from before, and dropped, it would make the object jump. How fast it goes, and how far
    /// it is reckoned to have gone on since the last position, are kept. Nothing is done before the sender has said
    /// anything.
    /// </summary>
    /// <returns>Whether it was moved.</returns>
    public bool DropWhatIsLeftOfAStep() {
        EnsureTransformCached();
        if (!_isInitialized || !_hasNewServerData || _predictionDisabled) {
            return false;
        }

        var position = _logicalPosition;
        if ((position - _cachedTransform.position).sqrMagnitude < BigMoveDistance * BigMoveDistance) {
            return false;
        }

        SayItMovedALongWay("dropping what the smoothing had left of a step", _cachedTransform.position, position);
        _visualOffset = Vector3.zero;
        _visualOffsetVelocity = Vector3.zero;
        _cachedTransform.position = position;
        return true;
    }

    /// <summary>
    /// Forces the position to the given position.
    /// </summary>
    /// <param name="position">Vector3 containing the position to snap to.</param>
    private void ForceSnap(Vector3 position) {
        EnsureTransformCached();

        SayItMovedALongWay("being put straight there", _cachedTransform.position, position);

        _lastServerPosition = position;
        _logicalPosition = position;

        _velocity = Vector3.zero;
        _visualOffset = Vector3.zero;
        _visualOffsetVelocity = Vector3.zero;

        TookPosition();
        _lastUpdateTime = Time.time;

        _cachedTransform.position = position;
    }

    /// <summary>
    /// Sets whether prediction should be enabled.
    /// </summary>
    /// <param name="shouldEnable">True if prediction should be enabled, otherwise false.</param>
    public void SetPredictionEnabled(bool shouldEnable) {
        EnsureTransformCached();
        _predictionDisabled = !shouldEnable;
        if (_predictionDisabled) {
            ForceSnap(_cachedTransform.position);
        }
    }

    /// <summary>
    /// Helper to prevent NullRef if called before Awake.
    /// </summary>
    private void EnsureTransformCached() {
        if (_cachedTransform == null) _cachedTransform = transform;
    }

    /// <summary>
    /// Adapts the time over which what a new position corrects is smoothed away to the measured RTT: longer for a
    /// longer round trip, whose positions come less evenly.
    /// Call this whenever you measure/update the client's RTT.
    /// </summary>
    /// <param name="rttMs">Round-trip time in milliseconds</param>
    public void AdaptToRTT(float rttMs) {
        if (!enableRttAdaptation) return;

        // Smooth the RTT transition to prevent jarring changes
        _currentRtt = Mathf.Lerp(_currentRtt, rttMs, Time.deltaTime * rttSmoothingSpeed);

        // Smoothly transition toward the value for the current RTT
        var targetCorrectionTime = InterpolateValueForRtt(_currentRtt, CorrectionTimes);
        _adaptedCorrectionTime = Mathf.Lerp(
            _adaptedCorrectionTime, targetCorrectionTime, Time.deltaTime * rttSmoothingSpeed
        );
    }

    /// <summary>
    /// Interpolates a value from the tier arrays based on RTT.
    /// </summary>
    private static float InterpolateValueForRtt(float rtt, float[] tierValues) {
        // Below minimum threshold - use LAN tier
        if (rtt <= RttThresholds[0]) {
            return tierValues[0];
        }

        // Find which tier range we're in and interpolate
        for (var i = 0; i < RttThresholds.Length; i++) {
            if (rtt <= RttThresholds[i]) {
                var prevThreshold = (i == 0) ? 0f : RttThresholds[i - 1];
                var t = Mathf.InverseLerp(prevThreshold, RttThresholds[i], rtt);
                return Mathf.Lerp(tierValues[i], tierValues[i + 1], t);
            }
        }

        // Above maximum threshold - use Poor tier
        return tierValues[^1];
    }

    #region What Quiet Stretches Did, For The Log

    /// <summary>
    /// Why an object that went on by itself stopped.
    /// </summary>
    private enum StopCause {
        None,
        Time,
        Distance,
        Wall
    }

    /// <summary>
    /// After how long going on from its last position an object counts as having gone on by itself through a quiet
    /// stretch, for the log, in seconds: past what positions every 17 ms leave to the smoothing.
    /// </summary>
    private const float CoastCountsAfter = 0.06f;

    /// <summary>
    /// How long after the connection came back from going quiet what the copies did is written down, in seconds: by
    /// then the positions that came after it have corrected them.
    /// </summary>
    private const double TellAfter = 0.5;

    private static int _quietCoasts;
    private static float _quietLongestCoast;
    private static float _quietFarthestCoast;
    private static int _quietStoppedByTime;
    private static int _quietStoppedByDistance;
    private static int _quietStoppedByWall;
    private static float _quietLargestCorrection;
    private static int _quietSnaps;

    /// <summary>
    /// The quiet stretches through which nothing went on by itself, since the last line.
    /// </summary>
    private static int _quietStretchesUntold;

    /// <summary>
    /// The <see cref="LinkWatch.Returns"/> that the log has been told about.
    /// </summary>
    private static int _toldReturns;

    /// <summary>
    /// The frame in which <see cref="TellWhatQuietStretchesDid"/> last looked, so it looks once a frame.
    /// </summary>
    private static int _toldFrame = -1;

    /// <summary>
    /// Notes how far from where it went on to by itself the object's next position put it.
    /// </summary>
    /// <param name="newPos">The position.</param>
    /// <param name="snapped">Whether the object is put straight there.</param>
    private void NoteWhereItCameTo(Vector3 newPos, bool snapped) {
        if (!_coasted) {
            return;
        }

        _coasted = false;
        _quietLargestCorrection = Mathf.Max(_quietLargestCorrection, (_cachedTransform.position - newPos).magnitude);
        if (snapped) {
            _quietSnaps++;
        }
    }

    /// <summary>
    /// Writes one line on the quiet stretches since the last line, once the connection has been back for a moment:
    /// how long they were, how many times a copy went on by itself through them, for how long and how far, what
    /// stopped it, and how far off that was by the positions that came after.
    /// </summary>
    private static void TellWhatQuietStretchesDid() {
        var frame = Time.frameCount;
        if (frame == _toldFrame) {
            return;
        }

        _toldFrame = frame;
        if (LinkWatch.Returns == _toldReturns || LinkWatch.SecondsSinceReturn < TellAfter) {
            return;
        }

        _toldReturns = LinkWatch.Returns;
        LinkWatch.TakeQuietStretches(out var stretches, out var longestMs);
        if (_quietCoasts == 0) {
            _quietStretchesUntold += stretches;
            return;
        }

        var before = _quietStretchesUntold > 0
            ? $"; before these, {_quietStretchesUntold} quiet stretch(es) through which nothing went on by itself"
            : "";
        Logger.Info(
            $"[Smoothing] {stretches} quiet stretch(es) in what comes from " +
            $"{(LinkWatch.Hosting ? "the other players" : "the server")}, the longest {longestMs} ms: a copy went " +
            $"on by itself {_quietCoasts} time(s), for up to {_quietLongestCoast:0.00} s and " +
            $"{_quietFarthestCoast:0.00} units; stopped by the time limit {_quietStoppedByTime}, by the distance " +
            $"limit {_quietStoppedByDistance}, by a wall {_quietStoppedByWall}; the largest correction after was " +
            $"{_quietLargestCorrection:0.00} units, {_quietSnaps} put straight there{before}"
        );

        _quietCoasts = 0;
        _quietLongestCoast = 0f;
        _quietFarthestCoast = 0f;
        _quietStoppedByTime = 0;
        _quietStoppedByDistance = 0;
        _quietStoppedByWall = 0;
        _quietLargestCorrection = 0f;
        _quietSnaps = 0;
        _quietStretchesUntold = 0;
    }

    #endregion
}
