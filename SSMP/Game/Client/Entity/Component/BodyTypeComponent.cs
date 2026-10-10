using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component carries the kind of body the entity has: moved by physics, or only by what it does itself. A creature
/// can change it in its FSM - one that lies in wait may be built kinematic and made dynamic when it wakes - and the
/// room's own copy in the game that does not run the creature never goes through that change. When that game takes the
/// creature over, the room's copy has to be the kind the creature was left as, and nothing else there knows it: the copy
/// that was on show in the meantime is always kinematic, since it is only ever put where it is told.
internal class BodyTypeComponent : EntityComponent {
    /// <summary>
    /// The body of the room's own copy of the entity.
    /// </summary>
    private readonly Rigidbody2D _hostBody;

    /// <summary>
    /// The kind of body that was last sent, or null if none was sent yet.
    /// </summary>
    private RigidbodyType2D? _lastType;

    public BodyTypeComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        Rigidbody2D hostBody
    ) : base(netClient, entityId, gameObject) {
        _hostBody = hostBody;
    }

    /// <inheritdoc />
    public override void SendAgain() {
        _lastType = null;
    }

    /// <summary>
    /// Callback for checking the kind of body each update.
    /// </summary>
    /// <inheritdoc />
    public override void OnUpdate() {
        if (IsControlled || _hostBody == null) {
            return;
        }

        var type = _hostBody.bodyType;
        if (_lastType == type) {
            return;
        }

        _lastType = type;

        var data = new EntityNetworkData {
            Type = EntityComponentType.BodyType
        };
        data.Packet.Write((byte) type);

        SendData(data);
    }

    /// <inheritdoc />
    protected override void InitializeHost() {
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        // Only the room's own copy is given it; the copy on show stays kinematic
        if (!IsControlled || _hostBody == null) {
            return;
        }

        _hostBody.bodyType = (RigidbodyType2D) data.Packet.ReadByte();
    }

    /// <inheritdoc />
    public override void Destroy() {
    }
}
