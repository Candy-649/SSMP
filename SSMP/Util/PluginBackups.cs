using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Util;

/// <summary>
/// Keeps the copies of the plugin's files that UPDATE.bat leaves beside them to the newest few. Each update keeps the
/// files it replaces as "&lt;file&gt;.&lt;version&gt;.bak", so that going back is a rename, and it never took any
/// away: on 2026-10-20 the user's folder held 153 of them, 580 MB, back to 0.3.1, 76 of them the same BouncyCastle
/// ("不应该只备份最近十个吗？！"). The co-op saves keep their newest 10 copies, and so do these now: of each file, those of
/// the 10 newest versions. The game loads none of them, as BepInEx only loads files that end in .dll.
///
/// It is done here rather than in the updater because the updater replaces only the plugin's files and never itself,
/// so a change to it would not reach those who have it already, while the plugin is new with every update.
/// </summary>
internal static class PluginBackups {
    /// <summary>
    /// How many copies of each file are kept.
    /// </summary>
    private const int Keep = 10;

    /// <summary>
    /// The name of a copy that the updater leaves: the file's own name, the version it was, and ".bak".
    /// </summary>
    private static readonly Regex CopyName = new(
        @"^(?<file>.+\.dll)\.(?<version>[0-9]+\.[0-9]+\.[0-9]+)\.bak$",
        RegexOptions.IgnoreCase
    );

    /// <summary>
    /// Removes the copies in the plugin's folder beyond the newest <see cref="Keep"/> of each file, and nothing else.
    /// </summary>
    public static void Tidy() {
        try {
            var folder = Path.GetDirectoryName(typeof(PluginBackups).Assembly.Location);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) {
                return;
            }

            var copiesByFile = new Dictionary<string, List<(Version Version, string Path)>>(
                StringComparer.OrdinalIgnoreCase
            );
            foreach (var path in Directory.GetFiles(folder, "*.bak")) {
                var match = CopyName.Match(Path.GetFileName(path));
                if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var version)) {
                    continue;
                }

                var file = match.Groups["file"].Value;
                if (!copiesByFile.TryGetValue(file, out var copies)) {
                    copiesByFile[file] = copies = [];
                }

                copies.Add((version, path));
            }

            var removed = 0;
            long bytes = 0;
            foreach (var copies in copiesByFile.Values) {
                copies.Sort((a, b) => b.Version.CompareTo(a.Version));
                for (var i = Keep; i < copies.Count; i++) {
                    bytes += new FileInfo(copies[i].Path).Length;
                    File.Delete(copies[i].Path);
                    removed++;
                }
            }

            if (removed > 0) {
                Logger.Info(
                    $"Removed {removed} old copies of the plugin's files that updates left, " +
                    $"{bytes / (1024 * 1024)} MB, keeping those of the newest {Keep} versions of each"
                );
            }
        } catch (Exception e) {
            Logger.Warn($"Could not tidy the copies of the plugin's files that updates left: {e.Message}");
        }
    }
}
