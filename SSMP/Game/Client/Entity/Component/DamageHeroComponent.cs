using System.Linq;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component manages the damage that an entity deals to the player: how much each part deals, whether the part is
/// there, and whether its damage is switched on. A state machine switches the damage of a part on and off by itself
/// (SetDamageHero sets DamageHero.enabled, and a switched-off one deals none, HeroBox.TakeDamageFromDamager), and a
/// copy runs none of it: a rolling ball that sleeps harmless and turns harmful as it wakes hurt only in the game that
/// ran it, where it took the scene host's player down, and its copy in the partner's game stayed harmless (the logs
/// of USER 10-17, "[Attacks]": it came up in one game only).
internal class DamageHeroComponent : EntityComponent {
    /// <summary>
    /// Damage components on the host object, in hierarchy order.
    /// </summary>
    private readonly DamageHero[] _hostDamageHeroes;

    /// <summary>
    /// Damage components on the client clone, in hierarchy order.
    /// </summary>
    private readonly DamageHero[] _clientDamageHeroes;

    /// <summary>
    /// Last replicated damage values, in hierarchy order.
    /// </summary>
    private readonly int[] _lastDamageDealt;

    /// <summary>
    /// Last replicated active states, in hierarchy order.
    /// </summary>
    private readonly bool[] _lastActive;

    /// <summary>
    /// Last replicated states of the damage being switched on, in hierarchy order.
    /// </summary>
    private readonly bool[] _lastEnabled;

    public DamageHeroComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        DamageHero[]? hostDamageHeroes,
        DamageHero[]? clientDamageHeroes
    ) : base(netClient, entityId, gameObject) {
        _hostDamageHeroes = hostDamageHeroes ?? [];
        _clientDamageHeroes = clientDamageHeroes ?? [];
        _lastDamageDealt = _hostDamageHeroes.Select(damageHero => damageHero != null ? damageHero.damageDealt : 0)
                                            .ToArray();
        _lastActive = _hostDamageHeroes.Select(damageHero =>
            damageHero != null && damageHero.gameObject != null && damageHero.gameObject.activeSelf
        ).ToArray();
        _lastEnabled = _hostDamageHeroes.Select(damageHero => damageHero != null && damageHero.enabled).ToArray();
    }

    /// <inheritdoc />
    public override void SendAgain() {
        for (var i = 0; i < _lastDamageDealt.Length; i++) {
            _lastDamageDealt[i] = int.MinValue;
        }
    }

    /// <summary>
    /// Callback method to check for damage hero updates.
    /// </summary>
    /// <inheritdoc />
    public override void OnUpdate() {
        if (IsControlled) {
            return;
        }

        if (GameObject.Host == null) {
            return;
        }

        var changed = false;
        for (var i = 0; i < _hostDamageHeroes.Length; i++) {
            var damageHero = _hostDamageHeroes[i];
            if (damageHero == null || damageHero.gameObject == null) {
                continue;
            }

            var damageDealt = damageHero.damageDealt;
            var active = damageHero.gameObject.activeSelf;
            var enabled = damageHero.enabled;
            if (damageDealt == _lastDamageDealt[i] && active == _lastActive[i] && enabled == _lastEnabled[i]) {
                continue;
            }

            _lastDamageDealt[i] = damageDealt;
            _lastActive[i] = active;
            _lastEnabled[i] = enabled;
            changed = true;
        }

        if (!changed) {
            return;
        }

        var data = new EntityNetworkData {
            Type = EntityComponentType.DamageHero
        };
        data.Packet.Write((byte) _hostDamageHeroes.Length);
        foreach (var damageHero in _hostDamageHeroes) {
            data.Packet.Write((byte) (damageHero != null ? damageHero.damageDealt : 0));
            data.Packet.Write(damageHero != null && damageHero.gameObject != null && damageHero.gameObject.activeSelf);
            data.Packet.Write(damageHero != null && damageHero.enabled);
        }

        SendData(data);
    }

    /// <inheritdoc />
    protected override void InitializeHost() {
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        var length = data.Packet.ReadByte();
        for (var i = 0; i < length; i++) {
            var damageDealt = data.Packet.ReadByte();
            var active = data.Packet.ReadBool();
            var enabled = data.Packet.ReadBool();
            if (i < _hostDamageHeroes.Length && _hostDamageHeroes[i] != null &&
                _hostDamageHeroes[i].gameObject != null) {
                _hostDamageHeroes[i].damageDealt = damageDealt;
                _hostDamageHeroes[i].enabled = enabled;

                // A creature that deals damage with its own body is its own first entry here, and while the other
                // game runs it, the room's own copy of it is asleep. Switching that on woke the whole creature at the
                // place it started from until the entity put it back to sleep, which is the creature flashing there
                // every time any of its damage changed. Whether it is awake is the entity's business; the parts below
                // it can follow along while it sleeps.
                if (_hostDamageHeroes[i].gameObject != GameObject.Host) {
                    _hostDamageHeroes[i].gameObject.SetActive(active);
                }
            }

            if (i < _clientDamageHeroes.Length && _clientDamageHeroes[i] != null &&
                _clientDamageHeroes[i].gameObject != null) {
                _clientDamageHeroes[i].damageDealt = damageDealt;
                _clientDamageHeroes[i].enabled = enabled;
                _clientDamageHeroes[i].gameObject.SetActive(active);
            }
        }
    }

    /// <inheritdoc />
    public override void Destroy() {
    }
}
