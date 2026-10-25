using System.Collections.Generic;
using System.Reflection;
using SSMP.Networking.Packet.Data;

namespace SSMP.Util;

/// <summary>
/// The version of this mod, as npm publishes it, and how two games tell each other theirs as one joins the other.
///
/// Two versions of the mod don't understand each other, and only a Steam lobby said so before (through its data,
/// see ConnectInterface.OnLobbyJoined): a player who joined with a room code or an address joined a host of another
/// version as if nothing were wrong, and was stuck there (USER 10-25: a host of a version from before 0.4.130 went into
/// their save while the player who joined waited in a room for them to be ready). The joining game now sends its
/// version among the addons it has, which every version of the mod reads and checks, and an older host answers with
/// the addons that it has, which don't name a version: either way, both games know.
/// </summary>
internal static class ModVersion {
    /// <summary>
    /// The name of the package on npm, as players install it.
    /// </summary>
    public const string PackageName = "silksong-coop-mod";

    /// <summary>
    /// The version, as npm publishes it: major, minor and patch, as in 0.4.133.
    /// </summary>
    public static readonly string Short = ReadShort();

    /// <summary>
    /// The name that the version goes by among the addons of a joining game. Within the 20 characters that every
    /// version of the mod allows the name of an addon, as Short is within its 10 for a version: an older host that
    /// read more would fail to read the request at all, and the player would only see the connection time out.
    /// </summary>
    private const string AddonName = "SSMP version";

    /// <summary>
    /// The version as an addon, which the joining game sends among its addons, and which a host sends among its own
    /// where it turns a player away for their addons.
    /// </summary>
    public static AddonData AsAddon() => new(AddonName, Short);

    /// <summary>
    /// Takes the version out of a list of addons that came from the other game, so that the list holds only its
    /// addons, and gives it.
    /// </summary>
    /// <param name="addons">The addons that came, or null for none.</param>
    /// <returns>The version of the mod that the other game runs, or null where it didn't say, as one older than
    /// 0.4.133 doesn't.</returns>
    public static string? TakeFrom(List<AddonData>? addons) {
        if (addons == null) {
            return null;
        }

        string? version = null;
        for (var i = addons.Count - 1; i >= 0; i--) {
            if (addons[i].Identifier != AddonName) {
                continue;
            }

            version ??= addons[i].Version;
            addons.RemoveAt(i);
        }

        return version;
    }

    /// <summary>
    /// What a host tells a player whose version differs, in both languages, as the host's game doesn't know which one
    /// the player reads. A game that sent no version runs one from before this check, so older than the host's.
    /// </summary>
    /// <param name="joining">The version that the joining game runs, or null where it didn't say.</param>
    public static string RejectionForJoining(string? joining) => joining == null
        ? $"模组版本不同：房主是 {Short}，你的版本更旧。两边都更新到最新版再连。\n" +
          $"Different mod versions: the host runs {Short}, yours is older. Both update to the newest one, then join."
        : $"模组版本不同：房主是 {Short}，你是 {joining}。两边都更新到最新版再连。\n" +
          $"Different mod versions: the host runs {Short}, you run {joining}. Both update to the newest one, then join.";

    /// <summary>
    /// What a joining player is told where the host named no version, as one from before this check doesn't.
    /// </summary>
    public static string HostOlder() => Lang.Pick(
        $"The host's mod is an older version than yours ({Short}). Both update to the newest one, then join.",
        $"房主的模组版本比你的旧（你是 {Short}）。两边都更新到最新版再连。"
    );

    /// <summary>
    /// The version from the version of the assembly, which the build gives the version in the project file.
    /// </summary>
    private static string ReadShort() {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
