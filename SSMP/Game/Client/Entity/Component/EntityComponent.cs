using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;

namespace SSMP.Game.Client.Entity.Component;

/// <summary>
/// Class for a generalizable part of an entity that requires networking for a specific feature.
/// </summary>
internal abstract class EntityComponent {
    /// <summary>
    /// The net client for networking.
    /// </summary>
    private readonly NetClient _netClient;

    /// <summary>
    /// The ID of the entity.
    /// </summary>
    private readonly ushort _entityId;

    /// <summary>
    /// Host-client pair of the game objects of the entity.
    /// </summary>
    protected readonly HostClientPair<GameObject> GameObject;

    /// <summary>
    /// Whether the entity is controlled.
    /// </summary>
    public bool IsControlled { get; set; }

    protected EntityComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject
    ) {
        _netClient = netClient;
        _entityId = entityId;

        GameObject = gameObject;

        IsControlled = true;
    }

    /// <summary>
    /// Whether a part of the room's own object of the entity is under it still. While the other game runs the creature,
    /// that object sleeps out of sight, and the parts it holds follow what the other game says of them, for when this
    /// game takes the creature over. A part that it let go of into the room is not asleep with it (see LetGoParts):
    /// switched on from here, it showed and hurt where it was let go. The needle that a boss throws stood where the
    /// boss had started, in the game that did not run the fight, for as long as the scene host's needle was out, and
    /// the player saw it there before the copy's was thrown (USER 11-02, 在织针的目的地我先看到了织针).
    /// </summary>
    /// <param name="part">The part of the room's own object, or null where it has none.</param>
    protected bool IsHeldByTheRoomsOwn(UnityEngine.GameObject? part) {
        return part != null && part.transform.IsChildOf(GameObject.Host.transform);
    }

    /// <summary>
    /// Send the given <see cref="EntityNetworkData"/>.
    /// </summary>
    /// <param name="data">The data to send.</param>
    protected void SendData(EntityNetworkData data) {
        if (SendThrough != null) {
            SendThrough(data);
            return;
        }

        _netClient.UpdateManager.AddEntityData(_entityId, data);
    }

    /// <summary>
    /// What sends the data of this part in its place, if anything: the entity, which holds it back behind its replays
    /// while it holds any.
    /// </summary>
    public System.Action<EntityNetworkData>? SendThrough { get; set; }

    /// <summary>
    /// Initializes the entity component when the client user is the scene host.
    /// </summary>
    protected virtual void InitializeHost() {
    }

    /// <summary>
    /// Initializes the entity component when the client user is the scene host with an epoch.
    /// </summary>
    public virtual void InitializeHost(uint sceneHostEpoch) {
        InitializeHost();
    }

    /// <summary>
    /// Initializes the entity component when the client user is a scene client.
    /// </summary>
    private static void InitializeClient() {
    }

    /// <summary>
    /// Initializes the entity component when the client user is a scene client with an epoch.
    /// </summary>
    public virtual void InitializeClient(uint sceneHostEpoch) {
        InitializeClient();
    }

    /// <summary>
    /// Performs periodic update polling for host components.
    /// </summary>
    public virtual void OnUpdate() {
    }

    /// <summary>
    /// Update the entity component with the given data.
    /// </summary>
    /// <param name="data">The data to update with.</param>
    /// <param name="alreadyInSceneUpdate">Whether this data is from an already in scene packet.</param>
    public abstract void Update(EntityNetworkData data, bool alreadyInSceneUpdate);

    /// <summary>
    /// Destroy the entity component.
    /// </summary>
    public abstract void Destroy();
}

/// <summary>
/// Enum for data types.
/// </summary>
[JsonConverter(typeof(StringEnumConverter))]
internal enum EntityComponentType : ushort {
    Fsm = 0,
    Death,
    Invincibility,
    Rotation,
    Collider,
    DamageHero,
    MeshRenderer,
    Velocity,
    GravityScale,
    ZPosition,
    Climber,
    EnemySpawner,
    ChildrenActivation,
    SpawnJar,
    SpriteRenderer,
    ChallengePrompt,
    Music,
    DreamPlatform,
    HazardRespawn,
    Health,
    BodyType,
    Layer,
    OwnMotion,

    /// <summary>
    /// Not a component: the scene host's answer to something the player of a scene client did to the entity that their
    /// game played at once, sent in order with the rest (see <see cref="Entity.HearEcho"/>).
    /// </summary>
    Echo,

    /// <summary>
    /// The colours of the sprites that the FSMs on the creature colour, where they ended up, for a copy that did not
    /// follow them there and a player who walks in after the creature changed them (see <see cref="TintComponent"/>).
    /// </summary>
    Tint,

    /// <summary>
    /// Which of the parts that the creature's FSMs dress it in are switched on, for a player who walks in after the
    /// creature switched them and for the game that takes it over (see <see cref="PartsComponent"/>).
    /// </summary>
    Parts,

    /// <summary>
    /// What the third act does to the creature: whether it turns, whether it turned, its attack, and its strikes (see
    /// <see cref="BlackThreadComponent"/>).
    /// </summary>
    BlackThread,

    /// <summary>
    /// The health of a boss that its FSMs keep instead of a HealthManager, for its bar (see
    /// <see cref="FsmHealthComponent"/>).
    /// </summary>
    FsmHealth
}
