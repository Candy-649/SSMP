using UnityEngine;

namespace SSMP.Game.Client.Entity;

/// <summary>
/// Where the copy of a juggled entity was when the local player struck it and its FSM played the strike at once, and
/// what that FSM rolled on the way (see <see cref="Entity.PlayBounceHere"/>). The scene host puts the entity there and
/// takes the strike with the same dice, so that it flies off the way the player who struck it already sees it fly.
/// </summary>
internal readonly struct BounceStart {
    public BounceStart(string state, Vector2 position, int[] dice, byte anticipation) {
        State = state;
        Position = position;
        Dice = dice;
        Anticipation = anticipation;
    }

    /// <summary>
    /// The state the strike took the FSM to.
    /// </summary>
    public string State { get; }

    /// <summary>
    /// Where the copy was.
    /// </summary>
    public Vector2 Position { get; }

    /// <summary>
    /// The dice of the FSM as it played the strike (see <see cref="Save.SharedDice"/>).
    /// </summary>
    public int[] Dice { get; }

    /// <summary>
    /// The number that the game of the player who struck it waits on the scene host to have taken the strike under
    /// (see <see cref="Entity.BeginAnticipation"/>).
    /// </summary>
    public byte Anticipation { get; }
}
