using SSMP.Api.Client;
using SSMP.Internals;
using SSMP.Util;
using UnityEngine;

namespace SSMP.Game.Client;

/// <inheritdoc />
internal class ClientPlayerData : IClientPlayer {
    /// <inheritdoc />
    public required ushort Id { get; init; }

    /// <inheritdoc />
    public required string Username { get; init; }

    /// <summary>
    /// The key that identifies the player for two-player saves, or an empty string if the server didn't send it.
    /// </summary>
    public string SaveKey { get; init; } = "";

    /// <inheritdoc />
    public bool IsInLocalScene { get; set; }

    /// <inheritdoc />
    public GameObject? PlayerContainer { get; set; }

    /// <inheritdoc />
    public GameObject? PlayerObject { get; set; }

    /// <summary>
    /// Which positions of this player are newer than the one they are standing at. Kept for as long as they are
    /// known, across rooms: unlike an entity, a player is not built again on walking into one.
    /// </summary>
    public PositionSequence PositionSequence => _positionSequence ??= new PositionSequence($"player {Username}");

    /// <summary>
    /// Backing field for <see cref="PositionSequence"/>, made when it is first asked for so that it can be named
    /// after the player rather than before their name is known.
    /// </summary>
    private PositionSequence? _positionSequence;

    /// <inheritdoc />
    public Team Team { get; set; }

    /// <inheritdoc />
    public byte SkinId { get; set; }

    /// <inheritdoc />
    public CrestType CrestType { get; set; }
}
