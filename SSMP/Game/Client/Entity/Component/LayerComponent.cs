using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component carries the layer of the entity, which decides what can hit it. A great many creatures go untouchable
/// and back by changing it in their FSM - to 2 or 14 while hidden or passing through, to 11 again when they can be
/// fought - and the copy on show in the game that does not run the creature kept the layer it was made with: one that
/// came out could not be hit there, and one that hid could. It goes as a state rather than by doing the FSM's action
/// again, because a player who walks in later has the copy start over from its first states, which is where most of
/// them hide.
internal class LayerComponent : EntityComponent {
    /// <summary>
    /// The layer that was last sent, or -1 if none was sent yet.
    /// </summary>
    private int _lastLayer = -1;

    public LayerComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject
    ) : base(netClient, entityId, gameObject) {
    }

    /// <summary>
    /// Callback for checking the layer each update.
    /// </summary>
    /// <inheritdoc />
    public override void OnUpdate() {
        if (IsControlled || GameObject.Host == null) {
            return;
        }

        var layer = GameObject.Host.layer;
        if (_lastLayer == layer) {
            return;
        }

        _lastLayer = layer;

        var data = new EntityNetworkData {
            Type = EntityComponentType.Layer
        };
        data.Packet.Write((byte) layer);

        SendData(data);
    }

    /// <inheritdoc />
    protected override void InitializeHost() {
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        if (!IsControlled) {
            return;
        }

        var layer = data.Packet.ReadByte();

        // The room's own copy as well, so that it is already on the right layer if this game takes the creature over
        if (GameObject.Host != null) {
            GameObject.Host.layer = layer;
        }

        if (GameObject.Client != null) {
            GameObject.Client.layer = layer;
        }
    }

    /// <inheritdoc />
    public override void Destroy() {
    }
}
