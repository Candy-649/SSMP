using System;

namespace SSMP.Util;

/// <summary>
/// A helper class to cache properties of enum types to avoid repeating allocations and reflection overhead.
/// </summary>
/// <typeparam name="T">The enum type.</typeparam>
public static class EnumCache<T> where T : struct, Enum {
    /// <summary>
    /// The number of values defined in the enum.
    /// </summary>
    public static readonly int Count = Enum.GetNames(typeof(T)).Length;
}

/// <summary>
/// The values of an enum type, asked for once, in the order that <see cref="Enum.GetValues"/> gives them. Without a
/// struct constraint, so that methods constrained only to <see cref="Enum"/> can use it. The array is shared: it is
/// only read.
/// </summary>
/// <typeparam name="T">The enum type.</typeparam>
internal static class EnumValues<T> where T : Enum {
    /// <summary>
    /// The values of the enum.
    /// </summary>
    public static readonly T[] Values = (T[]) Enum.GetValues(typeof(T));
}
