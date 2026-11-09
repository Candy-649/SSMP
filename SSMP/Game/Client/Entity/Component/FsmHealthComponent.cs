using System.Collections.Generic;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Entity.Component;

/// <summary>
/// The health of a boss that its FSMs keep instead of a HealthManager, for its bar (Ui.EnemyHealthBars): the one whose
/// fight is first against the burning parts of its body, each with health of its own, and then against its core, which
/// opens once they are all broken (census of every boss the user named, 11-10; USER 11-10 "你的血条要能覆盖这些boss",
/// "一阶段是打几个着火的地方，二阶段才是boss本体好像"). The game that runs the boss reads it from those FSMs, which count
/// the blows of every player there (HitCounters), and tells the other games, whose copies count none: they show only
/// what they are told (USER 11-09 "客机也不应该自己摆弄血条").
/// </summary>
internal class FsmHealthComponent : EntityComponent {
    /// <summary>
    /// The FSM of each burning part, with its health in <see cref="HealthVariable"/>.
    /// </summary>
    private const string PartFsmName = "wisp_brazier_arm";

    /// <summary>
    /// The FSM of the core, on the boss's own object, with its health in <see cref="HealthVariable"/>: it waits in
    /// <see cref="ClosedState"/> until the core opens, and ends in <see cref="BrokenState"/>, which can come before its
    /// health is gone, after a number of blows.
    /// </summary>
    private const string CoreFsmName = "Take Damage";

    /// <summary>
    /// The variable of the health of a burning part and of the core.
    /// </summary>
    private const string HealthVariable = "HP";

    /// <summary>
    /// The state of the core's FSM while the core is closed.
    /// </summary>
    private const string ClosedState = "Init";

    /// <summary>
    /// The state of the core's FSM in which the core breaks, and the boss with it.
    /// </summary>
    private const string BrokenState = "Break";

    /// <summary>
    /// The components of the bosses of the room, for the bars.
    /// </summary>
    private static readonly List<FsmHealthComponent> Components = [];

    /// <summary>
    /// The FSMs of the burning parts of the room's own object of the boss.
    /// </summary>
    private readonly List<PlayMakerFSM> _parts = [];

    /// <summary>
    /// The FSM of the core of the room's own object of the boss.
    /// </summary>
    private readonly PlayMakerFSM? _core;

    /// <summary>
    /// Whether this game told the health yet, and what it told last.
    /// </summary>
    private bool _told;

    /// <summary>
    /// The health and whether the core is open, as this game told them last.
    /// </summary>
    private int _toldHealth;

    /// <inheritdoc cref="_toldHealth"/>
    private bool _toldCoreOpen;

    public FsmHealthComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject
    ) : base(netClient, entityId, gameObject) {
        foreach (var fsm in gameObject.Host.GetComponentsInChildren<PlayMakerFSM>(true)) {
            if (fsm.FsmName == PartFsmName) {
                _parts.Add(fsm);
                PartsFull += fsm.FsmVariables.GetFsmInt(HealthVariable).Value;
            } else if (fsm.FsmName == CoreFsmName && fsm.gameObject == gameObject.Host) {
                _core = fsm;
            }
        }

        Components.Add(this);
    }

    /// <summary>
    /// The components of the bosses of the room whose FSMs keep their health.
    /// </summary>
    public static IReadOnlyList<FsmHealthComponent> All => Components;

    /// <summary>
    /// The room's own object of the boss.
    /// </summary>
    public GameObject Creature => GameObject.Host;

    /// <summary>
    /// Whether the boss is in sight in this game: the object of it that this game shows is there.
    /// </summary>
    public bool IsShown {
        get {
            var shown = IsControlled ? GameObject.Client : GameObject.Host;
            return shown != null && shown.activeInHierarchy;
        }
    }

    /// <summary>
    /// The health that the burning parts have together at the start, as the room was made: the full health of the
    /// first part of the fight.
    /// </summary>
    public int PartsFull { get; }

    /// <summary>
    /// The health: of the burning parts together, or of the core once it is open; -1 before the game that runs the
    /// boss told any.
    /// </summary>
    public int Health { get; private set; } = -1;

    /// <summary>
    /// Whether the core is open, the second part of the fight.
    /// </summary>
    public bool CoreOpen { get; private set; }

    /// <summary>
    /// Whether the boss is beaten: its core open and broken.
    /// </summary>
    public bool IsBeaten => CoreOpen && Health == 0;

    /// <summary>
    /// Whether a blow landed on the boss in its fight, as far as its health says.
    /// </summary>
    public bool WasHit => CoreOpen || Health >= 0 && Health < PartsFull;

    /// <inheritdoc />
    public override void OnUpdate() {
        if (IsControlled) {
            return;
        }

        var coreState = _core != null ? _core.ActiveStateName : null;
        var coreOpen = !string.IsNullOrEmpty(coreState) && coreState != ClosedState;
        var health = 0;
        if (!coreOpen) {
            foreach (var part in _parts) {
                health += Mathf.Max(part.FsmVariables.GetFsmInt(HealthVariable).Value, 0);
            }
        } else if (coreState != BrokenState) {
            health = Mathf.Max(_core!.FsmVariables.GetFsmInt(HealthVariable).Value, 0);
        }

        Health = health;
        CoreOpen = coreOpen;
        if (_told && health == _toldHealth && coreOpen == _toldCoreOpen) {
            return;
        }

        _told = true;
        _toldHealth = health;
        _toldCoreOpen = coreOpen;
        Logger.Info(
            $"[Health] '{GameObject.Host.name}' has {health} health in its " +
            (coreOpen ? "core" : $"burning parts, of {PartsFull}") + ", as its FSMs keep it"
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
        data.Packet.Write(Health);
        data.Packet.Write(CoreOpen);
        return data;
    }

    /// <inheritdoc />
    public override void SendAgain() {
        _told = false;
    }

    /// <inheritdoc />
    public override void AddToRoomSnapshot(List<EntityNetworkData> data) {
        if (!IsControlled && _told) {
            data.Add(Write());
        }
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        Health = data.Packet.ReadInt();
        CoreOpen = data.Packet.ReadBool();
    }

    /// <inheritdoc />
    public override void Destroy() {
        Components.Remove(this);
    }
}
