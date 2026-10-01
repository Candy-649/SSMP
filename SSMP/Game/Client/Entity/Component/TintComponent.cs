using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component carries the colour of an entity's sprite to a player who walks into the room after the creature
/// changed it. While both players are in the room the copy runs the creature's colour changes itself (see
/// EntityFsmActions.Effects), but not the ones it made before the copy was set up: the copy starts with the colour the
/// room gives the creature. A crow roosting in front of the room is drawn there as a black silhouette and turns its
/// own colour when it flies at the player, so for whoever came in after it took off it attacked in black, and the room's
/// own crow went on in black when that player's game took it over (Entity.TakeTintFromCopy). The colour is sent once it
/// has stopped changing, so a colour that eases in goes as one value, where it ends up, and the server keeps the last
/// one for whoever walks in next.
internal class TintComponent : EntityComponent {
    /// <summary>
    /// How long the colour has to stay the same before it is sent, in seconds.
    /// </summary>
    private const float SettleTime = 0.25f;

    /// <summary>
    /// The host-client pair of the sprites of the entity.
    /// </summary>
    private readonly HostClientPair<tk2dBaseSprite> _sprite;

    /// <summary>
    /// The colour sent last, or null if none has been sent since this game started running the creature.
    /// </summary>
    private Color32? _lastSent;

    /// <summary>
    /// The colour the sprite had the last time it was looked at, and since when.
    /// </summary>
    private Color32 _lastSeen;

    /// <inheritdoc cref="_lastSeen" />
    private float _seenSince;

    public TintComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject
    ) : base(netClient, entityId, gameObject) {
        _sprite = new HostClientPair<tk2dBaseSprite> {
            Host = gameObject.Host.GetComponent<tk2dBaseSprite>(),
            Client = gameObject.Client.GetComponent<tk2dBaseSprite>()
        };
    }

    /// <summary>
    /// Callback method to check whether the colour of the sprite has changed and settled.
    /// </summary>
    /// <inheritdoc />
    public override void OnUpdate() {
        if (IsControlled || _sprite.Host == null) {
            return;
        }

        Color32 colour = _sprite.Host.color;
        if (!IsSame(colour, _lastSeen)) {
            _lastSeen = colour;
            _seenSince = Time.unscaledTime;
            return;
        }

        if (Time.unscaledTime - _seenSince < SettleTime || _lastSent is { } sent && IsSame(sent, colour)) {
            return;
        }

        _lastSent = colour;

        var data = new EntityNetworkData {
            Type = EntityComponentType.Tint
        };
        data.Packet.Write(colour.r);
        data.Packet.Write(colour.g);
        data.Packet.Write(colour.b);
        data.Packet.Write(colour.a);

        SendData(data);
    }

    /// <inheritdoc />
    protected override void InitializeHost() {
        // What the server keeps for later players is whatever was sent last, perhaps by the other game, so the colour
        // is sent again once this game runs the creature
        _lastSent = null;
        if (_sprite.Host != null) {
            _lastSeen = _sprite.Host.color;
        }

        _seenSince = Time.unscaledTime;
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        var colour = new Color32(data.Packet.ReadByte(), data.Packet.ReadByte(), data.Packet.ReadByte(),
            data.Packet.ReadByte());

        if (_sprite.Client != null) {
            _sprite.Client.color = colour;
        }
    }

    /// <inheritdoc />
    public override void Destroy() {
    }

    /// <summary>
    /// Whether two colours are the same.
    /// </summary>
    private static bool IsSame(Color32 one, Color32 other) {
        return one.r == other.r && one.g == other.g && one.b == other.b && one.a == other.a;
    }
}
