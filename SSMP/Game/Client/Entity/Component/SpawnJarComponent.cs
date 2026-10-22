using System.Collections;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;
using Random = UnityEngine.Random;
#pragma warning disable CS0414 // Field is assigned but its value is never used

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component manages the SpawnJarControl behaviour of the entity.
internal class SpawnJarComponent : EntityComponent {
    /// <summary>
    /// The <see cref="SpawnJarControl"/> unity component of the entity.
    /// </summary>
    private readonly HostClientPair<SpawnJarControl> _spawnJar;

    /// <summary>
    /// Whether this is the first spawn for the jar.
    /// </summary>
    private bool _firstSpawn;

    public SpawnJarComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        HostClientPair<SpawnJarControl> spawnJar
    ) : base(netClient, entityId, gameObject) {
        _spawnJar = spawnJar;
        spawnJar.Client.enabled = false;
        
        _firstSpawn = true;
    }
    
    /// <inheritdoc />
    protected override void InitializeHost() {
        var data = new EntityNetworkData {
            Type = EntityComponentType.SpawnJar
        };
        SendData(data);
        
        Logger.Debug("Sending SpawnJarComponent data InitializeHost");

        _firstSpawn = false;
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        Logger.Debug("Received SpawnJarComponent data");
        MonoBehaviourUtil.Instance.StartCoroutine(Behaviour());
        IEnumerator Behaviour() {
            var jar = _spawnJar.Client;

            jar.transform.SetPositionZ(0.01f);
            
            jar.readyDust.Play();

            yield return new WaitForSeconds(0.5f);

            var body = jar.GetComponent<Rigidbody2D>();
            body.angularVelocity = Random.Range(0, 2) > 0 ? -300f : 300f;

            jar.readyDust.Stop();
            jar.dustTrail.Play();

            var sprite = jar.GetComponent<SpriteRenderer>();
            sprite.enabled = true;

            // The same while loop with a slightly higher threshold for breaking, because sometimes it doesn't reach
            // that position quite yet due to network instability
            while (jar.transform.position.y > jar.breakY + 0.1f) {
                yield return null;
            }
            
            GameCameras.instance.cameraShakeFSM.SendEvent("EnemyKillShake");

            var position = jar.transform.position;
            
            jar.dustTrail.Stop();
            jar.ptBreakS.Play();
            jar.ptBreakL.Play();
            jar.strikeNailR.Spawn(position);

            body.angularVelocity = 0.0f;

            sprite.enabled = false;
            
            jar.breakSound.SpawnAndPlayOneShot(jar.audioSourcePrefab, position);
        }
    }

    /// <inheritdoc />
    public override void Destroy() {
    }
}
