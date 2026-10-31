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
            if (damageHero == null || damageHero.gameObject == null || RunsByItself(damageHero, GameObject.Host)) {
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
            var flags = (byte) 0;
            if (damageHero != null && damageHero.gameObject != null) {
                if (damageHero.gameObject.activeSelf) {
                    flags |= ActiveFlag;
                }

                if (damageHero.enabled) {
                    flags |= EnabledFlag;
                }

                if (RunsByItself(damageHero, GameObject.Host)) {
                    flags |= RunsByItselfFlag;
                }
            }

            data.Packet.Write((byte) (damageHero != null ? damageHero.damageDealt : 0));
            data.Packet.Write(flags);
        }

        SendData(data);
    }

    /// <summary>
    /// The flag of an entry that says its object is switched on.
    /// </summary>
    private const byte ActiveFlag = 1;

    /// <summary>
    /// The flag of an entry that says its damage is switched on.
    /// </summary>
    private const byte EnabledFlag = 2;

    /// <summary>
    /// The flag of an entry that says it is on a part that the creature let go of and that runs by itself (see
    /// <see cref="RunsByItself"/>).
    /// </summary>
    private const byte RunsByItselfFlag = 4;

    /// <summary>
    /// Whether a damage component is on a part that the creature let go of into the room and that runs a state machine
    /// of its own, which each game runs as the creature tells it (see LetGoParts): what it switches is its own business
    /// there, and it is not switched from here. The spires that a coral brute raises let go of it as they wake, and
    /// their hurting parts, switched on in the other game as the host's rose, were never switched off there when the
    /// brute died before its spires sank, and hurt for good where nothing was drawn (USER 10-31: "我朋友被看不到的
    /// 珊瑚打到了"). A part let go of that runs nothing of its own, like a thing a creature flings on a chain, is
    /// switched only by the creature, and goes on following from here.
    /// </summary>
    private static bool RunsByItself(DamageHero damageHero, GameObject? creature) {
        return LetGoParts.RunsByItself(damageHero.transform, creature);
    }

    /// <inheritdoc />
    protected override void InitializeHost() {
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        var length = data.Packet.ReadByte();
        for (var i = 0; i < length; i++) {
            var damageDealt = data.Packet.ReadByte();
            var flags = data.Packet.ReadByte();
            var active = (flags & ActiveFlag) != 0;
            var enabled = (flags & EnabledFlag) != 0;

            // A part that either game's creature let go of and that runs by itself goes by itself there
            if ((flags & RunsByItselfFlag) != 0) {
                continue;
            }

            if (i < _hostDamageHeroes.Length && _hostDamageHeroes[i] != null &&
                _hostDamageHeroes[i].gameObject != null &&
                !RunsByItself(_hostDamageHeroes[i], GameObject.Host)) {
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
                _clientDamageHeroes[i].gameObject != null &&
                !RunsByItself(_clientDamageHeroes[i], GameObject.Client)) {
                _clientDamageHeroes[i].damageDealt = damageDealt;
                _clientDamageHeroes[i].enabled = enabled;

                // The same goes for the copy, which is there when the other game says the creature is
                // (Entity.UpdateIsActive). What is sent here of the body is only whether it is switched on itself, not
                // whether it is in the room: one in a group of the room that the story had switched off is on by
                // itself and gone. Switched on from here, the copy of whoever walked in came on at full health where
                // the creature waits, while the game that ran the room had none (10-28 logs, every walk-in).
                if (_clientDamageHeroes[i].gameObject != GameObject.Client) {
                    _clientDamageHeroes[i].gameObject.SetActive(active);
                }
            }
        }
    }

    /// <inheritdoc />
    public override void Destroy() {
    }
}
