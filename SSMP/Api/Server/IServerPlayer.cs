using SSMP.Game;
using SSMP.Internals;
using SSMP.Math;

namespace SSMP.Api.Server;

/// <summary>
/// A class containing all the relevant data managed by the server about a player.
/// </summary>
public interface IServerPlayer {
    /// <summary>
    /// The ID of the player.
    /// </summary>
    ushort Id { get; }
    
    /// <summary>
    /// The Unique client identifier of the player.
    /// Interchangeable between IPEndPoint and SteamID.
    /// </summary>
    string UniqueClientIdentifier { get; }
    
    /// <summary>
    /// The authentication key of the player.
    /// </summary>
    string AuthKey { get; }

    /// <summary>
    /// Whether this player is authorized, meaning that they have high-level permission.
    /// </summary>
    bool IsAuthorized { get; }

    /// <summary>
    /// The username of the player.
    /// </summary>
    string Username { get; }

    /// <summary>
    /// The name of the scene the player is currently in.
    /// </summary>
    string CurrentScene { get; }

    /// <summary>
    /// The last known position of the player.
    /// </summary>
    Vector2? Position { get; }

    /// <summary>
    /// Whether the player has an active map icon.
    /// </summary>
    bool HasMapIcon { get; }

    /// <summary>
    /// The last known map position of the player.
    /// </summary>
    Vector2? MapPosition { get; }

    /// <summary>
    /// The scale of the player as a bool indicating whether they should be flipped.
    /// </summary>
    bool Scale { get; }

    /// <summary>
    /// The ID of the last animation of the player.
    /// </summary>
    ushort AnimationId { get; }

    /// <summary>
    /// The current team of the player.
    /// </summary>
    Team Team { get; }

    /// <summary>
    /// What the player wears through skin mods, as text with a line for each part, empty for the game's own look.
    /// </summary>
    string Skin { get; }
    
    /// <summary>
    /// The type of crest the player is using.
    /// </summary>
    CrestType CrestType { get; }
}
