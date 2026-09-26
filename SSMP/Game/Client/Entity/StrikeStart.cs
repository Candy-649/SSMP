using UnityEngine;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// How the copy of an entity stood and moved when the local player struck it and the strike was played on the copy at
/// once (see <see cref="Entity.PlayStrikeHere"/>). The scene host puts the entity there before it takes the strike, so
/// that the entity flies off from where the player struck it rather than from where it had fallen to by the time the
/// strike arrived.
/// </summary>
internal readonly struct StrikeStart {
    public StrikeStart(string fromState, Vector2 position, Vector2 velocity, float angle, float spin, byte number) {
        FromState = fromState;
        Position = position;
        Velocity = velocity;
        Angle = angle;
        Spin = spin;
        Number = number;
    }

    /// <summary>
    /// The state the FSM was in, which the strike only takes it through and back to.
    /// </summary>
    public string FromState { get; }

    /// <summary>
    /// Where the copy was.
    /// </summary>
    public Vector2 Position { get; }

    /// <summary>
    /// How fast the copy moved.
    /// </summary>
    public Vector2 Velocity { get; }

    /// <summary>
    /// How the copy was turned, in degrees.
    /// </summary>
    public float Angle { get; }

    /// <summary>
    /// How fast the copy turned, in degrees a second.
    /// </summary>
    public float Spin { get; }

    /// <summary>
    /// The number the strike goes under, which the scene host sends back with how the entity moves once it has taken
    /// the strike in (see <see cref="Component.OwnMotionComponent.BeginStrike"/>).
    /// </summary>
    public byte Number { get; }
}
