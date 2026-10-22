using System.Collections.Generic;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using SSMP.Util;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

// ReSharper disable UnusedMember.Local
#pragma warning disable CS8601 // Possible null reference assignment.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider
// adding the 'required' modifier or declaring as nullable.

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component manages the hazard respawn changes within certain bossfights.
internal class HazardRespawnComponent : EntityComponent {
    /// <summary>
    /// The offset of the climb hazard respawn indices.
    /// </summary>
    private const int ClimbRespawnOffset = 1;

    /// <summary>
    /// The Control FSM of the host entity.
    /// </summary>
    private readonly PlayMakerFSM _hostControlFsm;

    /// <summary>
    /// The game object that holds the Ascend Respawn objects.
    /// </summary>
    private readonly GameObject _ascendRespawnsObject;

    /// <summary>
    /// List of hazard respawn trigger behaviours.
    /// </summary>
    private readonly List<HazardRespawnTrigger> _hazardRespawnTriggers;

    /// <summary>
    /// The last state of 'active' of the Ascend Respawn object.
    /// </summary>
    private bool _lastActiveAscendsRespawns;

    /// <summary>
    /// The index of the highest respawn that has been triggered locally or received from the server.
    /// </summary>
    private int _highestRespawn = -1;

    public HazardRespawnComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject
    ) : base(netClient, entityId, gameObject) {
        var host = gameObject.Host;
        _hostControlFsm = host.LocateMyFSM("Control");
        if (!_hostControlFsm) {
            Logger.Error("Could not find 'Control' FSM on host object");
            return;
        }

        _hostControlFsm.InsertMethod(
            "Climb Plats1", 7, () => {
                Logger.Debug("Climb Plats1 state reached, sending hazard respawn data");

                _highestRespawn = 0;

                var data = new EntityNetworkData {
                    Type = EntityComponentType.HazardRespawn
                };

                data.Packet.Write((byte) 0);
                data.Packet.Write(_lastActiveAscendsRespawns);

                SendData(data);
            }
        );

        _hazardRespawnTriggers = [];

        // Find the Ascend Respawns objects and add all HazardRespawnTrigger behaviours to the list
        var hostParent = host.transform.parent;
        if (hostParent) {
            _ascendRespawnsObject = hostParent.gameObject.FindGameObjectInChildren("Ascend Respawns");
            if (_ascendRespawnsObject) {
                var children = _ascendRespawnsObject.GetChildren();
                foreach (var child in children) {
                    var hazardRespawnTrigger = child.GetComponent<HazardRespawnTrigger>();
                    if (hazardRespawnTrigger) {
                        _hazardRespawnTriggers.Add(hazardRespawnTrigger);

                        Logger.Debug(
                            $"Added '{hazardRespawnTrigger.gameObject.name}' to list of hazard respawn triggers"
                        );
                    }
                }
            }
        }
    }

    /// <inheritdoc />
    public override void SendAgain() {
        if (_ascendRespawnsObject) {
            _lastActiveAscendsRespawns = !_ascendRespawnsObject.activeSelf;
        }
    }

    /// <summary>
    /// Update hook to check for changes in the active state of the Ascend Respawns object and network them.
    /// </summary>
    /// <inheritdoc />
    public override void OnUpdate() {
        if (IsControlled || !_ascendRespawnsObject) {
            return;
        }

        var active = _ascendRespawnsObject.activeSelf;
        if (active != _lastActiveAscendsRespawns) {
            _lastActiveAscendsRespawns = active;

            Logger.Debug($"Ascends Respawns object changed active: {active}, sending to server");

            var data = new EntityNetworkData {
                Type = EntityComponentType.HazardRespawn
            };

            data.Packet.Write((byte) _highestRespawn);
            data.Packet.Write(active);

            SendData(data);
        }
    }

    /// <inheritdoc />
    protected override void InitializeHost() {
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        var numRespawn = data.Packet.ReadByte();
        var ascendsRespawnsActive = data.Packet.ReadBool();

        if (IsControlled) {
            _ascendRespawnsObject.SetActive(ascendsRespawnsActive);
        }

        if (numRespawn <= _highestRespawn) {
            Logger.Debug("Num respawn received is less than or equal to already registered highest respawn");
            return;
        }

        if (numRespawn == 0) {
            var p2AHazardVar = _hostControlFsm.FsmVariables.GetFsmGameObject("P2A Hazard");
            if (p2AHazardVar == null) {
                Logger.Error("Could not find P2A Hazard variable in host FSM");
                return;
            }

            var p2AHazard = p2AHazardVar.Value;
            if (!p2AHazard) {
                Logger.Error("P2A Hazard variable value is null in host FSM");
                return;
            }

            Logger.Debug("Setting hazard respawn to plats hazard respawn");
            HeroController.instance.SetHazardRespawn(p2AHazard.transform.position, true);
        } else {
            if (numRespawn > _hazardRespawnTriggers.Count) {
                Logger.Error($"Received numRespawn = {numRespawn}, but there is no matching hazard respawn trigger");
                return;
            }

            // Loop over all earlier triggers and set them to inactive
            HazardRespawnTrigger hazardRespawnTrigger;
            for (var i = numRespawn; i > 0; i--) {
                hazardRespawnTrigger = _hazardRespawnTriggers[i - 1];
                hazardRespawnTrigger.inactive = true;
            }

            hazardRespawnTrigger = _hazardRespawnTriggers[numRespawn - 1];
            PlayerData.instance.SetHazardRespawn(hazardRespawnTrigger.respawnMarker);

            Logger.Debug($"Setting hazard respawn to climb phase respawn: {hazardRespawnTrigger.gameObject.name}");
        }

        _highestRespawn = numRespawn;
    }

    /// <inheritdoc />
    public override void Destroy() {
    }
}
