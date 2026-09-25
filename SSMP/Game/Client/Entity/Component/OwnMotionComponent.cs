using SSMP.Fsm;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component lets the copy of an entity move by itself, for a thing that is set moving once and then keeps going
/// the same way until it stops: a rock that drops, a stalactite that falls. Everything random about such a thing is
/// decided at the moment it sets off - where it starts, which way it is turned, how fast it goes and spins - and
/// nothing after that changes it. So what the scene host sends is that moment: where the entity is, how it is turned
/// and how its body moves, each time its body starts or stops moving or changes how. The copy is put there and given
/// the same movement, and the local game's physics carries it on from there the way the scene host's game carries the
/// entity. Followed by positions instead, like other entities, a stall in the network left it hanging in the air.
/// While it moves by itself the scene host sends no positions for it at all, so a falling rock costs two small
/// messages rather than one every frame.
///
/// Only for a body that nothing but its own FSM moves: kinematic, or with no gravity and only trigger colliders. The
/// copy's body is kinematic, so it takes no gravity and nothing pushes it.
internal class OwnMotionComponent : EntityComponent {
    /// <summary>
    /// The body of the room's own copy of the entity, which the scene host's game moves.
    /// </summary>
    private readonly Rigidbody2D? _hostBody;

    /// <summary>
    /// The body of the copy on show.
    /// </summary>
    private readonly Rigidbody2D? _clientBody;

    /// <summary>
    /// The velocity that was last sent.
    /// </summary>
    private Vector2 _lastVelocity;

    /// <summary>
    /// The spin that was last sent, in degrees a second.
    /// </summary>
    private float _lastSpin;

    public OwnMotionComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject
    ) : base(netClient, entityId, gameObject) {
        _hostBody = gameObject.Host.GetComponent<Rigidbody2D>();
        _clientBody = gameObject.Client.GetComponent<Rigidbody2D>();
    }

    /// <summary>
    /// Whether the copy is moving by itself, which is when the interpolation of positions leaves it alone.
    /// </summary>
    public bool IsMoving => IsControlled && _clientBody != null &&
                            (_clientBody.linearVelocity != Vector2.zero || _clientBody.angularVelocity != 0f);

    /// <summary>
    /// Whether the scene host's entity is moving by itself, which is when its positions are not sent: the other game
    /// moves the copy the same way from how it set off.
    /// </summary>
    public bool IsHostMoving => !IsControlled && _hostBody != null &&
                                (_hostBody.linearVelocity != Vector2.zero || _hostBody.angularVelocity != 0f);

    /// <summary>
    /// Callback for checking the movement of the body each update.
    /// </summary>
    /// <inheritdoc />
    public override void OnUpdate() {
        if (IsControlled || _hostBody == null || GameObject.Host == null) {
            return;
        }

        var velocity = _hostBody.linearVelocity;
        var spin = _hostBody.angularVelocity;
        if (velocity == _lastVelocity && spin == _lastSpin) {
            return;
        }

        _lastVelocity = velocity;
        _lastSpin = spin;

        var transform = GameObject.Host.transform;
        var position = transform.position;

        var data = new EntityNetworkData {
            Type = EntityComponentType.OwnMotion
        };
        data.Packet.Write(position.x);
        data.Packet.Write(position.y);
        data.Packet.Write(transform.eulerAngles.z);
        data.Packet.Write(velocity.x);
        data.Packet.Write(velocity.y);
        data.Packet.Write(spin);

        SendData(data);
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        var position = new Vector2(data.Packet.ReadFloat(), data.Packet.ReadFloat());
        var angle = data.Packet.ReadFloat();
        var velocity = new Vector2(data.Packet.ReadFloat(), data.Packet.ReadFloat());
        var spin = data.Packet.ReadFloat();

        // What was kept for a player walking in says how the entity set off some time ago, not where it is now; the
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

        _clientBody.position = position;
        _clientBody.rotation = angle;
        _clientBody.linearVelocity = velocity;
        _clientBody.angularVelocity = spin;

        // No positions come while the entity moves by itself, so the interpolation is told where the copy is now, to
        // carry on from there when it stops rather than from where the entity stood before it set off
        if (GameObject.Client.TryGetComponent<PredictiveInterpolation>(out var interpolation)) {
            interpolation.SetNewState(transform.position, isTeleport: true);
        }
    }

    /// <inheritdoc />
    public override void Destroy() {
    }
}
