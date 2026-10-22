using System;
using System.Reflection;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Util;

/// <summary>
/// The version of the game that runs, and whether it is older than the version the mod is built against: an older game
/// can lack what the mod uses.
/// </summary>
internal static class GameVersion {
    /// <summary>
    /// The version of the game the mod is built against. A constant of the game, which the compiler copies into the mod
    /// as it was when the mod was built.
    /// </summary>
    public const string BuiltFor = global::Constants.GAME_VERSION;

    /// <summary>
    /// The version of the game that runs, read from the game's own type as it runs, or null if it can't be read.
    /// </summary>
    public static string? Running { get; } = ReadRunning();

    /// <summary>
    /// Whether the game that runs is older than the version the mod is built against; false when either can't be read.
    /// </summary>
    public static bool IsOlder => Running != null &&
                                  System.Version.TryParse(Running, out var running) &&
                                  System.Version.TryParse(BuiltFor, out var builtFor) &&
                                  running < builtFor;

    /// <summary>
    /// Whether a failure comes from the game lacking what the mod uses, because the game is older than the version the
    /// mod is built against.
    /// </summary>
    public static bool IsTooOldFor(Exception? e) {
        if (!IsOlder) {
            return false;
        }

        for (; e != null; e = e.InnerException) {
            if (e is MissingMemberException or TypeLoadException) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The version and build of the game that runs, and the version the mod is built against, for the log: two players'
    /// logs then tell whether they run the same game.
    /// </summary>
    public static string Describe() {
        string build;
        try {
            build = typeof(global::Constants).Assembly.ManifestModule.ModuleVersionId.ToString();
        } catch (Exception e) {
            build = $"unknown ({e.GetType().Name})";
        }

        return $"Game version {Running ?? "unknown"} (build {build}), the mod is built for {BuiltFor}" +
               (IsOlder ? ": the game is older than the mod needs" : "");
    }

    private static string? ReadRunning() {
        try {
            var field = typeof(global::Constants).GetField(
                "GAME_VERSION",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            );
            if (field == null) {
                return null;
            }

            return (field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null)) as string;
        } catch (Exception e) {
            Logger.Warn($"Could not read the version of the game:\n{e}");
            return null;
        }
    }
}
