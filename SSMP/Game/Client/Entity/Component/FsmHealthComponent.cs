using System.Collections.Generic;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity.Component;

/// <summary>
/// The health of the copy of a boss that its FSMs keep instead of a HealthManager, for its bar (see FsmHealth): the
/// game that runs the boss reads it from those FSMs and tells the other games, which show only what they are told
/// (USER 11-09 "客机也不应该自己摆弄血条").
/// </summary>
internal class FsmHealthComponent : EntityComponent {
    /// <summary>
    /// The components of the bosses of the room, for the bars.
    /// </summary>
    private static readonly List<FsmHealthComponent> Components = [];

    /// <summary>
    /// Whether this game told the health yet.
    /// </summary>
    private bool _told;

    public FsmHealthComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject
    ) : base(netClient, entityId, gameObject) {
        Health = new FsmHealth(gameObject.Host);
        Components.Add(this);
    }

    /// <summary>
    /// The components of the bosses of the room whose FSMs keep their health.
    /// </summary>
    public static IReadOnlyList<FsmHealthComponent> All => Components;

    /// <summary>
    /// The health, as this game reads it from the FSMs of the room's own object of the boss, or as it was told.
    /// </summary>
    public FsmHealth Health { get; }

    /// <summary>
    /// Whether the boss is in sight in this game: the object of it that this game shows is there.
    /// </summary>
    public bool IsShown {
        get {
            var shown = IsControlled ? GameObject.Client : GameObject.Host;
            return Health.Health >= 0 && shown != null && shown.activeInHierarchy;
        }
    }

    /// <inheritdoc />
    public override void OnUpdate() {
        if (IsControlled || !Health.Read() && _told) {
            return;
        }

        _told = true;
        Logger.Info(
            $"[Health] '{GameObject.Host.name}' has {Health.Health} left in its " +
            (Health.CoreOpen ? "core" : $"burning parts, of {Health.PartsFull}") +
            ", by the health and blows its FSMs keep"
        );
        SendData(Write());
    }

    /// <summary>
    /// The health and whether the core is open, as data to tell.
    /// </summary>
    private EntityNetworkData Write() {
        var data = new EntityNetworkData {
            Type = EntityComponentType.FsmHealth
        };
        data.Packet.Write(Health.Health);
        data.Packet.Write(Health.CoreOpen);
        return data;
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        Health.Health = data.Packet.ReadInt();
        Health.CoreOpen = data.Packet.ReadBool();
    }

    /// <inheritdoc />
    public override void Destroy() {
        Components.Remove(this);
    }
}
