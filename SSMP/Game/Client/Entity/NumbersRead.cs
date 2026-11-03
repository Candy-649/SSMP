namespace SSMP.Game.Client.Entity;

/// <summary>
/// The float, vector2 and vector3 variables of an FSM, by index, that something in the other game reads while the FSM
/// stays in a state (see Entity.AddVariableChanges).
/// </summary>
internal sealed class NumbersRead {
    /// <summary>
    /// The float variables.
    /// </summary>
    public int[] Floats { get; init; } = [];

    /// <summary>
    /// The vector2 variables.
    /// </summary>
    public int[] Vector2s { get; init; } = [];

    /// <summary>
    /// The vector3 variables.
    /// </summary>
    public int[] Vector3s { get; init; } = [];
}
