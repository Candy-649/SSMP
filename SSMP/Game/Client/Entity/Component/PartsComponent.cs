using System.Collections.Generic;
using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component carries which of some parts of an entity are switched on, by their path under it (see
/// <see cref="EntityRegistryEntry.SharedParts"/>), from the game that runs the creature to the other one. While both
/// players are in the room the copy switches its parts as the scene host's creature does (EntityFsmActions), but not
/// the ones that were switched before the copy was set up, and the room's own creature, asleep while the other game
/// runs it, keeps whatever its own first steps switched on before it was put to sleep.
///
/// A tall reed of the citadel rests wrapped in a cobweb, one of two that its first steps pick, and its first wake takes
/// the cobweb off, once: whether it woke before is kept in its FSM, and a later wake leaves the cobwebs as they are. A
/// game that took the room over woke the room's own reed with the cobweb it had put on by itself, and with the word of
/// the other game that it had woken before, so nothing took it off, and the reed got up and fought in its cobweb for
/// both players (USER 10-08).
///
/// The scene host sends which parts are on whenever that changes, and once as it starts running the creature. The
/// server keeps the last word for whoever walks in next. The other game puts it on the copy and on the room's own
/// creature, so that the game that takes the creature over finds its parts the way the other game left them.
internal class PartsComponent : EntityComponent {
    /// <summary>
    /// The parts of the room's own creature, in the order of the registry, each null if it isn't there.
    /// </summary>
    private readonly GameObject?[] _hostParts;

    /// <summary>
    /// The parts of the copy, in the order of the registry, each null if it isn't there.
    /// </summary>
    private readonly GameObject?[] _clientParts;

    /// <summary>
    /// Whether the room's own creature has any of the parts at all; a thing that only shares the name of the creature
    /// has none and sends nothing.
    /// </summary>
    private readonly bool _hasParts;

    /// <summary>
    /// Which parts were on when they were sent last, a bit for each, or null if nothing has been sent since this game
    /// started running the creature.
    /// </summary>
    private int? _lastSent;

    public PartsComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        IReadOnlyList<string> paths
    ) : base(netClient, entityId, gameObject) {
        _hostParts = new GameObject?[paths.Count];
        _clientParts = new GameObject?[paths.Count];
        for (var i = 0; i < paths.Count; i++) {
            _hostParts[i] = FindPart(gameObject.Host, paths[i]);
            _clientParts[i] = FindPart(gameObject.Client, paths[i]);
            _hasParts |= _hostParts[i] != null;
        }
    }

    /// <summary>
    /// Callback method to check whether a part has been switched on or off.
    /// </summary>
    /// <inheritdoc />
    public override void OnUpdate() {
        if (IsControlled || !_hasParts) {
            return;
        }

        var parts = 0;
        for (var i = 0; i < _hostParts.Length; i++) {
            if (_hostParts[i] is { } part && part != null && part.activeSelf) {
                parts |= 1 << i;
            }
        }

        if (_lastSent == parts) {
            return;
        }

        _lastSent = parts;

        var data = new EntityNetworkData {
            Type = EntityComponentType.Parts
        };
        data.Packet.Write((byte) _hostParts.Length);
        for (var i = 0; i < _hostParts.Length; i++) {
            data.Packet.Write((parts & (1 << i)) != 0);
        }

        SendData(data);
    }

    /// <inheritdoc />
    protected override void InitializeHost() {
        // What the server keeps for later players is whatever was sent last, perhaps by the other game, so the parts
        // are sent again once this game runs the creature
        _lastSent = null;
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        if (!IsControlled) {
            return;
        }

        var count = data.Packet.ReadByte();
        for (var i = 0; i < count; i++) {
            var isOn = data.Packet.ReadBool();
            if (i >= _hostParts.Length) {
                continue;
            }

            SetOn(_clientParts[i], isOn);
            SetOn(_hostParts[i], isOn);
        }
    }

    /// <inheritdoc />
    public override void Destroy() {
    }

    /// <summary>
    /// Finds a part of a creature by its path under it.
    /// </summary>
    private static GameObject? FindPart(GameObject? root, string path) {
        if (root == null) {
            return null;
        }

        var part = root.transform.Find(path);
        return part == null ? null : part.gameObject;
    }

    /// <summary>
    /// Switches a part on or off, if it is there.
    /// </summary>
    private static void SetOn(GameObject? part, bool isOn) {
        if (part != null && part.activeSelf != isOn) {
            part.SetActive(isOn);
        }
    }
}
