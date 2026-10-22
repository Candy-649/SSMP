using System.Collections;
using SSMP.Game.Client.Entity.Action;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component manages the EnemySpawner behaviour of the entity.
internal class EnemySpawnerComponent : EntityComponent {
    /// <summary>
    /// The <see cref="EnemySpawner"/> unity component of the entity.
    /// </summary>
    private readonly HostClientPair<EnemySpawner> _spawner;

    public EnemySpawnerComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        HostClientPair<EnemySpawner> spawner
    ) : base(netClient, entityId, gameObject) {
        _spawner = spawner;
        spawner.Client.enabled = false;

        spawner.Host.OnEnemySpawned += OnEnemySpawned;
    }

    /// <summary>
    /// Hook for when the enemy object is spawned from the spawner so we can call the spawn event.
    /// </summary>
    /// <param name="obj">The spawned game object.</param>
    private void OnEnemySpawned(GameObject obj) {
        EntityFsmActions.CallEntitySpawnEvent(
            new EntitySpawnDetails {
                Type = EntitySpawnType.EnemySpawnerComponent,
                GameObject = obj
            }
        );
    }

    /// <inheritdoc />
    protected override void InitializeHost() {
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        iTween.MoveBy(
            _spawner.Client.gameObject, new Hashtable {
                {
                    "amount",
                    _spawner.Client.moveBy
                }, {
                    "time",
                    _spawner.Client.easeTime
                }, {
                    "easetype",
                    _spawner.Client.easeType
                }, {
                    "space",
                    Space.World
                }
            }
        );
    }

    /// <inheritdoc />
    public override void Destroy() {
        if (_spawner.Host != null) {
            _spawner.Host.OnEnemySpawned -= OnEnemySpawned;
        }
    }
}
