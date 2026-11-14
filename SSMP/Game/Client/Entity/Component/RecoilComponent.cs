using SSMP.Networking.Client;
using SSMP.Networking.Packet.Data;
using UnityEngine;

namespace SSMP.Game.Client.Entity.Component;

/// <inheritdoc />
/// This component carries how blows knock the creature back: the ways they do not (Recoil.IsUpBlocked and the three
/// others), whether a blow holds it still where it is instead (FreezeInPlace), and how fast it goes (RecoilSpeedBase).
/// The copy is knocked back by the local player's own blows at once (CoopHits.OnRecoilByDirection), so it has to be
/// knocked back the way the creature is. The creature's FSM sets them (SetRecoilBlocked, SetRecoilSpeed), and so does
/// the game's crawler as it starts to crawl (Crawler.StartCrawling). Like the layer (LayerComponent), they go as a
/// state rather than by doing the FSM's action again: a player who walks in later has the copy start over from its
/// first states, and what the creature did in the room's first frames, before it was settled which game runs it,
/// went to nobody. The creature that hides in coral is knocked back no way at all from then on, and the copy of the
/// player who walked in was knocked up by each of their upward blows, and slid back (USER 11-14).
internal class RecoilComponent : EntityComponent {
    /// <summary>
    /// The creature's own knockback and its copy's.
    /// </summary>
    private readonly HostClientPair<Recoil> _recoil;

    /// <summary>
    /// The ways and the hold that were last sent (see <see cref="WaysOf"/>).
    /// </summary>
    private byte _lastWays;

    /// <summary>
    /// The speed that was last sent.
    /// </summary>
    private float _lastSpeed;

    public RecoilComponent(
        NetClient netClient,
        ushort entityId,
        HostClientPair<GameObject> gameObject,
        HostClientPair<Recoil> recoil
    ) : base(netClient, entityId, gameObject) {
        _recoil = recoil;

        // The copy is made from the creature as the room loads, as it is then
        _lastWays = WaysOf(recoil.Host);
        _lastSpeed = recoil.Host.RecoilSpeedBase;
    }

    /// <summary>
    /// Callback for checking the knockback each update.
    /// </summary>
    /// <inheritdoc />
    public override void OnUpdate() {
        if (IsControlled || _recoil.Host == null) {
            return;
        }

        var ways = WaysOf(_recoil.Host);
        var speed = _recoil.Host.RecoilSpeedBase;
        if (ways == _lastWays && speed.Equals(_lastSpeed)) {
            return;
        }

        _lastWays = ways;
        _lastSpeed = speed;

        var data = new EntityNetworkData {
            Type = EntityComponentType.Recoil
        };
        data.Packet.Write(ways);
        data.Packet.Write(speed);

        SendData(data);
    }

    /// <inheritdoc />
    public override void Update(EntityNetworkData data, bool alreadyInSceneUpdate) {
        if (!IsControlled) {
            return;
        }

        var ways = data.Packet.ReadByte();
        var speed = data.Packet.ReadFloat();

        // The room's own creature as well, so that it is knocked back the right ways if this game takes it over
        if (_recoil.Host != null) {
            Put(_recoil.Host, ways, speed);
        }

        if (_recoil.Client != null) {
            Put(_recoil.Client, ways, speed);
        }
    }

    /// <inheritdoc />
    public override void Destroy() {
    }

    /// <summary>
    /// The ways that blows do not knock the creature back, as bits: up 1, down 2, left 4 and right 8, and 16 if a blow
    /// holds it still where it is.
    /// </summary>
    private static byte WaysOf(Recoil recoil) {
        return (byte) ((recoil.IsUpBlocked ? 1 : 0) | (recoil.IsDownBlocked ? 2 : 0) | (recoil.IsLeftBlocked ? 4 : 0) |
                       (recoil.IsRightBlocked ? 8 : 0) | (recoil.FreezeInPlace ? 16 : 0));
    }

    /// <summary>
    /// Gives a knockback the ways (see <see cref="WaysOf"/>) and the speed of the creature's.
    /// </summary>
    private static void Put(Recoil recoil, byte ways, float speed) {
        recoil.IsUpBlocked = (ways & 1) != 0;
        recoil.IsDownBlocked = (ways & 2) != 0;
        recoil.IsLeftBlocked = (ways & 4) != 0;
        recoil.IsRightBlocked = (ways & 8) != 0;
        recoil.FreezeInPlace = (ways & 16) != 0;
        recoil.SetRecoilSpeed(speed);
    }
}
