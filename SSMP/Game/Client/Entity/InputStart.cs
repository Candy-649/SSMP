using UnityEngine;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// What the game of a scene client sends with something its player did to the copy of an entity that the copy's own FSM
/// played there at once (see <see cref="Entity.PlayHere"/>): where it took the FSM, where the copy was and how its body
/// moved, and the dice that the FSM rolled on the way. The scene host puts its entity there and plays the same with the
/// same dice (see <see cref="Entity.TakeInput"/>), so that it goes the way the player already sees it go.
/// </summary>
internal readonly struct InputStart {
    public InputStart(string state, Vector2 position, BodyMotion? motion, int[] dice, byte anticipation) {
        State = state;
        Position = position;
        Motion = motion;
        Dice = dice;
        Anticipation = anticipation;
    }

    /// <summary>
    /// The state the input took the FSM to.
    /// </summary>
    public string State { get; }

    /// <summary>
    /// Where the copy was.
    /// </summary>
    public Vector2 Position { get; }

    /// <summary>
    /// How the body of the copy was moving by itself, or null for a copy that does not (see
    /// <see cref="Component.OwnMotionComponent"/>).
    /// </summary>
    public BodyMotion? Motion { get; }

    /// <summary>
    /// The dice of the FSM as it played the input (see <see cref="Save.SharedDice"/>).
    /// </summary>
    public int[] Dice { get; }

    /// <summary>
    /// The number that the game of the player who did it waits on the scene host to answer (see
    /// <see cref="Entity.BeginAnticipation"/>).
    /// </summary>
    public byte Anticipation { get; }

    /// <summary>
    /// How a body that moves by itself was moving: how fast, which way it was turned in degrees, and how fast it was
    /// turning in degrees a second.
    /// </summary>
    internal readonly record struct BodyMotion(Vector2 Velocity, float Angle, float Spin);
}
