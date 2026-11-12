namespace SSMP.Util;

/// <summary>
/// The 32-bit FNV-1a hash, which is the same in every game, unlike the hash of a string in .NET: for what both games
/// work out on their own and compare, or keep in their records, such as stable IDs, the dice of a roll and the checks
/// of the state of a room. Fed the same characters and numbers in the same order, it comes to the same number.
/// </summary>
internal static class StableHash {
    /// <summary>
    /// The hash of nothing yet, the start of every hash.
    /// </summary>
    public const uint Start = 2166136261u;

    /// <summary>
    /// The prime that each step multiplies by.
    /// </summary>
    private const uint Prime = 16777619u;

    /// <summary>
    /// The hash with a character added.
    /// </summary>
    public static uint Add(uint hash, char c) => unchecked((hash ^ c) * Prime);

    /// <summary>
    /// The hash with a number added as a whole, in one step.
    /// </summary>
    public static uint Add(uint hash, uint value) => unchecked((hash ^ value) * Prime);

    /// <summary>
    /// The hash with each character of a text added in order.
    /// </summary>
    public static uint Add(uint hash, string text) {
        foreach (var c in text) {
            hash = Add(hash, c);
        }

        return hash;
    }

    /// <summary>
    /// The hash of a text.
    /// </summary>
    public static uint Of(string text) => Add(Start, text);
}
