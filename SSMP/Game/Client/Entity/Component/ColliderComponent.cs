using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component carries whether the collider of the entity is on, which decides whether it can be touched: a creature
/// that hides switches it off in its FSM, and on again as it comes out, and the copy in the game that does not run the
/// creature is told so. Every creature has it, whether or not it has a collider as the room loads, as with its layer
/// (see LayerComponent): one drawn by tk2d gets the collider of the picture it shows, made the first time it shows a
/// picture that has one. A boss whose first picture had none went without it, and its copy stayed touchable where the
/// boss hid for the cross slash it makes around a player, which hurt the player of the game that did not run it
/// outside of the ring (USER 11-09 "三阶段转阶段会释放的那个技能…明明不在范围里却会被打到"). The first collider of
/// each object is the one carried, which is all that the creatures that hide this way have. What tk2d switches with
/// each picture is not sent: the copy's own pictures switch it the same way, in their own time (see IsPictureOf).
internal class ColliderComponent : EntityComponent {
    /// <summary>
    /// The colliders of the room's own creature and of its copy, once each has one.
    /// </summary>
    private Collider2D? _hostCollider, _clientCollider;

    /// <summary>
    /// Whether the collider was last on, as sent, or null before it was sent.
    /// </summary>
    private bool? _lastEnabled;

    /// <summary>
    /// Whether the collider is on as the game that runs the creature last told, or null before it told: put on a
    /// collider that the copy only gets later.
    /// </summary>
    private bool? _toldEnabled;

    public ColliderComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject
    ) : base(netClient, entityId, gameObject) {
    }

    /// <summary>
    /// The collider of an object, kept once it has one.
    /// </summary>
    private static Collider2D? Find(ref Collider2D? kept, GameObject? owner) {
        if (kept == null && owner != null) {
            kept = owner.GetComponent<Collider2D>();
        }

        return kept;
    }

    /// <summary>
    /// Sends whether the collider is on as it changes, in the game that runs the creature; in the other, has a
    /// collider that the copy gets only later, with the first picture that has one, start as told.
    /// </summary>
    /// <inheritdoc />
    public override void OnUpdate() {
        if (IsControlled) {
            if (_toldEnabled is { } told && _clientCollider == null &&
                Find(ref _clientCollider, GameObject.Client) is { } late) {
                late.enabled = told;
            }

            return;
        }

        if (Find(ref _hostCollider, GameObject.Host) is not { } collider) {
            return;
        }

        var enabled = collider.enabled;
        if (_lastEnabled == enabled) {
            return;
        }

        _lastEnabled = enabled;
        if (IsPictureOf(collider, enabled)) {
            return;
        }

        Logger.Debug($"Collider of {GameObject.Host!.name} enabled changed to: {enabled}");

        var data = new EntityNetworkData {
            Type = EntityComponentType.Collider
        };
        data.Packet.Write(enabled);

        SendData(data);
    }

    /// <summary>
    /// Whether the picture that a creature drawn by tk2d shows puts its collider so: tk2d switches the colliders of
    /// what it draws with each picture whose collider is of a kind (tk2dBaseSprite.UpdateCollider), the box on for a
    /// box and off for a mesh or none, and the others the other way. The copy's own pictures do the same as it shows
    /// them, which a copy that plays a move by itself does ahead of the game that runs the creature: told as well, the
    /// switch came late and put the copy to the picture before, touchable as it hid or not as it struck.
    /// </summary>
    private static bool IsPictureOf(Collider2D collider, bool enabled) {
        if (!collider.TryGetComponent<tk2dBaseSprite>(out var sprite) || sprite.CurrentSprite is not { } picture ||
            picture.physicsEngine != tk2dSpriteDefinition.PhysicsEngine.Physics2D) {
            return false;
        }

        var isBox = collider is BoxCollider2D;
        return picture.colliderType switch {
            tk2dSpriteDefinition.ColliderType.Box => enabled == isBox,
            tk2dSpriteDefinition.ColliderType.Mesh => enabled != isBox,
            tk2dSpriteDefinition.ColliderType.None => !enabled,
            _ => false
        };
    }

    /// <inheritdoc />
    protected override void InitializeHost() {
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        if (!IsControlled) {
            return;
        }

        var enabled = data.Packet.ReadBool();
        _toldEnabled = enabled;

        // The room's own copy as well, so that it is already so if this game takes the creature over
        if (Find(ref _hostCollider, GameObject.Host) is { } host) {
            host.enabled = enabled;
        }

        if (Find(ref _clientCollider, GameObject.Client) is { } copy) {
            copy.enabled = enabled;
        }
    }

    /// <inheritdoc />
    public override void Destroy() {
    }
}
