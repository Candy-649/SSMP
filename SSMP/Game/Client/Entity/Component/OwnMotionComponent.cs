using System.Collections.Generic;
using SSMP.Fsm;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component lets the copy of an entity move by itself, for a thing that is set moving and then goes the way its
/// own physics and FSM take it: a rock or a cage that drops, a boulder that speeds up as it falls, a bell thrown in an
/// arc. Everything random about such a thing is decided where its FSM decides how it moves - where it starts, which
/// way it is turned, how fast it goes and spins - and in between nothing but physics and its own actions move it. So
/// what the scene host sends is each of those moments: where the entity is, how it is turned, how its body moves and
/// what kind of body it is. The copy is put there and given the same body and movement, it runs the actions that move
/// the entity step by step itself (EntityFsmActions), and the local game's physics carries it on from there the way the
/// scene host's game carries the entity. Followed by positions instead, like other entities, a stall in the network
/// left it hanging in the air. While it moves by itself the scene host sends no positions for it at all, so a falling
/// rock costs a message where it sets off, one where it stops and one for each thing it bumps into on the way.
///
/// Only for a thing that nothing but its own FSM and physics moves, and whose colliders that touch anything solid touch
/// only the room itself, which is the same in both games.
internal class OwnMotionComponent : EntityComponent {
    /// <summary>
    /// The objects of the entities with this component, the room's own and the copies, by which the replays of the
    /// actions that move them know that the copy moves itself (see <see cref="Moves"/>).
    /// </summary>
    private static readonly HashSet<GameObject> Objects = new();

    /// <summary>
    /// The body of the room's own copy of the entity, which the scene host's game moves.
    /// </summary>
    private readonly Rigidbody2D? _hostBody;

    /// <summary>
    /// The body of the copy on show.
    /// </summary>
    private readonly Rigidbody2D? _clientBody;

    /// <summary>
    /// What tells this component of the room's own copy of the entity bumping into something.
    /// </summary>
    private readonly BumpListener? _bumpListener;

    /// <summary>
    /// Whether an FSM of the entity did something since the last look that may have changed how it moves.
    /// </summary>
    private bool _changed;

    /// <summary>
    /// Whether the copy on show was set moving by what the scene host sent, so that a takeover carries on with it.
    /// </summary>
    private bool _copyWasMoved;

    /// <summary>
    /// How the copy was moving when this game took the entity over, taken before the copy is switched off and given to
    /// the room's own object once that is on (<see cref="InitializeHost"/>).
    /// </summary>
    private (RigidbodyType2D BodyType, float GravityScale, Vector2 Velocity, float Spin, float Angle)? _takenOver;

    /// <summary>
    /// The position that was last sent.
    /// </summary>
    private Vector2 _lastPosition;

    /// <summary>
    /// The angle that was last sent, in degrees.
    /// </summary>
    private float _lastAngle;

    /// <summary>
    /// The velocity that was last sent.
    /// </summary>
    private Vector2 _lastVelocity;

    /// <summary>
    /// The spin that was last sent, in degrees a second.
    /// </summary>
    private float _lastSpin;

    /// <summary>
    /// The kind of body that was last sent.
    /// </summary>
    private RigidbodyType2D _lastBodyType;

    /// <summary>
    /// The gravity scale that was last sent.
    /// </summary>
    private float _lastGravityScale;

    public OwnMotionComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject
    ) : base(netClient, entityId, gameObject) {
        _hostBody = gameObject.Host.GetComponent<Rigidbody2D>();
        _clientBody = gameObject.Client.GetComponent<Rigidbody2D>();

        if (_hostBody != null) {
            _lastBodyType = _hostBody.bodyType;
            _lastGravityScale = _hostBody.gravityScale;

            // A bump changes how the entity moves where no action does: the game bounces some things off the floor
            // with a strength rolled for each bounce, and a copy a moment behind may meet a corner a little
            // differently. So how the entity goes on from each bump is sent as well.
            var host = gameObject.Host;
            _bumpListener = host.GetComponent<BumpListener>() ?? host.AddComponent<BumpListener>();
            _bumpListener.Bumped += MarkChanged;
        }

        Objects.RemoveWhere(obj => obj == null);
        Objects.Add(gameObject.Host);
        Objects.Add(gameObject.Client);
    }

    /// <summary>
    /// Whether the given object is one of an entity whose copy moves itself, so that the actions moving it step by step
    /// are run on the copy rather than left to positions.
    /// </summary>
    /// <param name="gameObject">The object.</param>
    public static bool Moves(GameObject gameObject) => Objects.Contains(gameObject);

    /// <summary>
    /// Says that something may have changed how the scene host's entity moves, which is looked at in the next update:
    /// an FSM of the entity did something, or the entity bumped into something. Every action that sets a velocity, a
    /// spin, a position or a kind of body is one that is replayed, and so comes through <see cref="Entity"/> here.
    /// </summary>
    public void MarkChanged() {
        _changed = true;
    }

    /// <summary>
    /// Whether the copy is moving by itself, which is when the interpolation of positions leaves it alone.
    /// </summary>
    public bool IsMoving => IsControlled && _clientBody != null && MovesByItself(_clientBody);

    /// <summary>
    /// Whether the scene host's entity is moving by itself, which is when its positions are not sent: the other game
    /// moves the copy the same way from how it set off.
    /// </summary>
    public bool IsHostMoving => !IsControlled && _hostBody != null && MovesByItself(_hostBody);

    /// <summary>
    /// Whether a body is moving by itself: going somewhere, turning, or falling. One that has come to rest on something
    /// is put to sleep by the physics, and then its place is sent like any other entity's, so that a player walking in
    /// later finds it where it lies rather than where it set off.
    /// </summary>
    private static bool MovesByItself(Rigidbody2D body) {
        return body.linearVelocity != Vector2.zero || body.angularVelocity != 0f ||
               body.bodyType == RigidbodyType2D.Dynamic && body.gravityScale != 0f && body.IsAwake();
    }

    /// <summary>
    /// Callback for sending how the entity moves each time that may have changed.
    /// </summary>
    /// <inheritdoc />
    public override void OnUpdate() {
        if (IsControlled || _hostBody == null || GameObject.Host == null) {
            return;
        }

        // A kind of body changed by anything says so itself. Any other change is one of the FSM's actions or a bump,
        // and a velocity or spin that changes while nothing is done - by gravity or drag - changes the same way on the
        // copy, which has the same body in the same room.
        var bodyType = _hostBody.bodyType;
        var gravityScale = _hostBody.gravityScale;
        if (!_changed && bodyType == _lastBodyType && gravityScale == _lastGravityScale) {
            return;
        }

        _changed = false;

        var transform = GameObject.Host.transform;
        Vector2 position = transform.position;
        var angle = transform.eulerAngles.z;
        var velocity = _hostBody.linearVelocity;
        var spin = _hostBody.angularVelocity;
        if (position == _lastPosition && angle == _lastAngle && velocity == _lastVelocity && spin == _lastSpin &&
            bodyType == _lastBodyType && gravityScale == _lastGravityScale) {
            return;
        }

        _lastPosition = position;
        _lastAngle = angle;
        _lastVelocity = velocity;
        _lastSpin = spin;
        _lastBodyType = bodyType;
        _lastGravityScale = gravityScale;

        var data = new EntityNetworkData {
            Type = EntityComponentType.OwnMotion
        };
        data.Packet.Write(position.x);
        data.Packet.Write(position.y);
        data.Packet.Write(angle);
        data.Packet.Write(velocity.x);
        data.Packet.Write(velocity.y);
        data.Packet.Write(spin);
        data.Packet.Write((byte) bodyType);
        data.Packet.Write(gravityScale);

        SendData(data);
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        var position = new Vector2(data.Packet.ReadFloat(), data.Packet.ReadFloat());
        var angle = data.Packet.ReadFloat();
        var velocity = new Vector2(data.Packet.ReadFloat(), data.Packet.ReadFloat());
        var spin = data.Packet.ReadFloat();
        var bodyType = (RigidbodyType2D) data.Packet.ReadByte();
        var gravityScale = data.Packet.ReadFloat();

        // What was kept for a player walking in says how the entity moved some time ago, not where it is now; the
        // positions that come with it put it there
        if (!IsControlled || alreadyInSceneUpdate || GameObject.Client == null) {
            return;
        }

        var transform = GameObject.Client.transform;
        transform.position = new Vector3(position.x, position.y, transform.position.z);

        var eulerAngles = transform.eulerAngles;
        transform.eulerAngles = new Vector3(eulerAngles.x, eulerAngles.y, angle);

        if (_clientBody == null) {
            return;
        }

        // The same kind of body with the same gravity, so that what the physics does to the entity between two of these
        // - its fall, its arc, the floor stopping it - the physics does to the copy too
        _clientBody.bodyType = bodyType;
        _clientBody.gravityScale = gravityScale;
        _clientBody.position = position;
        _clientBody.rotation = angle;
        _clientBody.linearVelocity = velocity;
        _clientBody.angularVelocity = spin;
        _copyWasMoved = true;

        // No positions come while the entity moves by itself, so the interpolation is told where the copy is now, to
        // carry on from there when it stops rather than from where the entity stood before it set off
        if (GameObject.Client.TryGetComponent<PredictiveInterpolation>(out var interpolation)) {
            interpolation.SetNewState(transform.position, isTeleport: true);
        }
    }

    /// <summary>
    /// Takes how the copy is moving, when this game takes the entity over from the other one and the copy is about to
    /// stop. The room's own object is given the copy's place and the states of its FSMs, and nothing more: it was left
    /// hanging where the copy was, since what set it moving had already been done, and nothing else moves it.
    /// </summary>
    public void TakeMotionFromCopy() {
        if (!_copyWasMoved || _clientBody == null || GameObject.Client == null) {
            return;
        }

        _takenOver = (
            _clientBody.bodyType, _clientBody.gravityScale, _clientBody.linearVelocity, _clientBody.angularVelocity,
            GameObject.Client.transform.eulerAngles.z
        );
    }

    /// <inheritdoc />
    protected override void InitializeHost() {
        _copyWasMoved = false;

        if (_takenOver is not { } motion || _hostBody == null || GameObject.Host == null) {
            return;
        }

        _takenOver = null;

        var transform = GameObject.Host.transform;
        var eulerAngles = transform.eulerAngles;
        transform.eulerAngles = new Vector3(eulerAngles.x, eulerAngles.y, motion.Angle);

        _hostBody.bodyType = motion.BodyType;
        _hostBody.gravityScale = motion.GravityScale;
        _hostBody.linearVelocity = motion.Velocity;
        _hostBody.angularVelocity = motion.Spin;

        // Told again, for anyone else in the room
        MarkChanged();
    }

    /// <inheritdoc />
    public override void Destroy() {
        Objects.Remove(GameObject.Host);
        Objects.Remove(GameObject.Client);

        if (_bumpListener != null) {
            _bumpListener.Bumped -= MarkChanged;
        }
    }
}
