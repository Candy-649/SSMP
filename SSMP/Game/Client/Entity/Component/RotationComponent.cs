using System;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component manages the rotation of the entity.
/// <remarks>
/// The first angle of a turn goes as data of the entity, the way that is sent again until it arrives, in its place
/// among the replays of the same frame, which may go by it: most turns are a single one. While the creature goes on
/// turning frame after frame, each angle after it rides with its position, the way that is not sent again, and the
/// other game takes the newest (settled with the user on 11-03, who would not have every such message go:
/// "我建议你好好想想而不是突然就极端的和我说所有每帧同步都不需要保留"); the angle it ends up at goes once it stops, as
/// data again, which the server keeps for the players who walk in later. Both ways go in one order of the packets they
/// were first sent in, on the server and in the other game: an angle sent again after a newer one went is not taken
/// there (Entity.UpdateData). While the copy turns itself as the creature turns here - in a move that it plays by
/// itself or from a replay of what turns it (Entity.CopyTurnsItself), or as its body spins by itself
/// (OwnMotionComponent) - nothing of the turn goes until it stops: sent every frame, each angle came a moment late and
/// turned the copy back.
/// </remarks>
internal class RotationComponent : EntityComponent {
    /// <summary>
    /// The net client, which sends the angles of a turn as it goes on.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The ID of the entity.
    /// </summary>
    private readonly ushort _entityId;

    /// <summary>
    /// The body of the room's own copy of the entity, if it has one.
    /// </summary>
    private readonly Rigidbody2D? _hostBody;

    /// <summary>
    /// The angle of the entity the last time it was looked at.
    /// </summary>
    private float _lastRotation;

    /// <summary>
    /// Whether the entity is in the middle of a turn: it turned in the frame before, or the copy turns itself.
    /// </summary>
    private bool _turning;

    /// <summary>
    /// The angle that last went as data of the entity, as long as nothing of the turn went the other way since and the
    /// copy did not turn itself: the angle a turn ends up at goes as data unless it is this one.
    /// </summary>
    private float _sentAsData;

    /// <summary>
    /// Whether the copy in the other game turns itself now, which the entity says (see Entity.CopyTurnsItself).
    /// </summary>
    public Func<bool> TurnsOnTheCopy { get; set; } = null!;

    public RotationComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject
    ) : base(netClient, entityId, gameObject) {
        _netClient = netClient;
        _entityId = entityId;
        _hostBody = gameObject.Host.GetComponent<Rigidbody2D>();
    }

    /// <summary>
    /// Callback method to check for rotation updates.
    /// </summary>
    /// <inheritdoc />
    public override void OnUpdate() {
        if (IsControlled) {
            return;
        }

        if (GameObject.Host == null) {
            return;
        }

        var rotation = GameObject.Host.transform.eulerAngles.z;
        if (rotation == _lastRotation && !_turning) {
            return;
        }

        if (_hostBody != null && _hostBody.angularVelocity != 0f && OwnMotionComponent.Moves(GameObject.Host) ||
            TurnsOnTheCopy()) {
            _lastRotation = rotation;
            _sentAsData = float.NaN;
            _turning = true;
            return;
        }

        if (rotation != _lastRotation) {
            _lastRotation = rotation;
            if (_turning) {
                _sentAsData = float.NaN;
                _netClient.UpdateManager.UpdateEntityRotation(_entityId, rotation);
            } else {
                _turning = true;
                SendAsData(rotation);
            }

            return;
        }

        if (_turning) {
            _turning = false;
            if (rotation != _sentAsData) {
                SendAsData(rotation);
            }
        }
    }

    /// <summary>
    /// Sends an angle of the entity as its data (see the remarks of the class).
    /// </summary>
    /// <param name="rotation">The angle.</param>
    private void SendAsData(float rotation) {
        _sentAsData = rotation;
        var data = new EntityNetworkData {
            Type = EntityComponentType.Rotation
        };
        data.Packet.Write(rotation);

        SendData(data);
    }

    /// <inheritdoc />
    protected override void InitializeHost() {
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        var rotation = data.Packet.ReadFloat();

        Turn(GameObject.Host, rotation);
        Turn(GameObject.Client, rotation);
    }

    /// <summary>
    /// Turns an object of the entity about its depth to the given angle.
    /// </summary>
    /// <param name="gameObject">The object, or null where it is gone.</param>
    /// <param name="rotation">The angle.</param>
    internal static void Turn(GameObject? gameObject, float rotation) {
        if (gameObject == null) {
            return;
        }

        var transform = gameObject.transform;
        var eulerAngles = transform.eulerAngles;
        transform.eulerAngles = new Vector3(
            eulerAngles.x,
            eulerAngles.y,
            rotation
        );
    }

    /// <inheritdoc />
    public override void Destroy() {
    }
}
