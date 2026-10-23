using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Skin;

/// <summary>
/// The skins of Silksong Customizer on this computer, found where Customizer looks for them: every folder named
/// Customizer under the plugins of BepInEx, and every folder in Mods/Customizer of the game's data. Customizer dresses
/// the player's own character with the ones that hold an active.txt; this finds them by the same rules, and finds the
/// skin that another player wears among all of them.
/// </summary>
internal static class CustomizerPacks {
    /// <summary>
    /// The full name of the type of Customizer's plugin.
    /// </summary>
    private const string PluginTypeName = "Customizer.Customizer";

    /// <summary>
    /// How long a look through the folders is used again before looking again, in seconds: characters are dressed
    /// whenever they show, and the skins don't come and go while playing.
    /// </summary>
    private const float FoundFor = 10f;

    /// <summary>
    /// The fingerprints of the skins found so far, by folder: their files don't change while the game runs.
    /// </summary>
    private static readonly Dictionary<string, string> Fingerprints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The skins found by the last look through the folders, and when that was.
    /// </summary>
    private static List<SkinPack>? _found;
    private static float _foundAt;

    /// <summary>
    /// Whether Silksong Customizer is loaded, and so dresses the player's own character with the active skins.
    /// </summary>
    public static bool IsLoaded => SkinModPlugins.Find(info => info.Instance?.GetType().FullName == PluginTypeName);

    /// <summary>
    /// Every skin of Customizer on this computer, in the order in which Customizer goes through them.
    /// </summary>
    public static List<SkinPack> FindAll() {
        if (_found != null && Time.unscaledTime - _foundAt < FoundFor && Time.unscaledTime >= _foundAt) {
            return _found;
        }

        var packs = new List<SkinPack>();

        try {
            foreach (var dir in Directory.GetDirectories(Paths.PluginPath, "Customizer", SearchOption.AllDirectories)) {
                packs.Add(new SkinPack(Path.GetFileName(Path.GetDirectoryName(dir)) ?? dir, dir));
            }
        } catch (Exception e) {
            Logger.Warn($"Could not look for skins of Customizer in the plugins: {e.Message}");
        }

        var modDir = Path.Combine(Application.dataPath, "Mods", "Customizer");
        try {
            if (Directory.Exists(modDir)) {
                foreach (var dir in Directory.GetDirectories(modDir)) {
                    packs.Add(new SkinPack(Path.GetFileName(dir), dir));
                }
            }
        } catch (Exception e) {
            Logger.Warn($"Could not look for skins of Customizer in '{modDir}': {e.Message}");
        }

        _found = packs;
        _foundAt = Time.unscaledTime;
        return packs;
    }

    /// <summary>
    /// The skin on this computer that another game names: the one with the same files, or else the one in a folder of
    /// the same name, or null if there is none.
    /// </summary>
    public static SkinPack? Find(List<SkinPack> packs, SkinPackRef wanted) {
        // The one in a folder of the same name first, so that the files of every other skin are read only when it
        // differs
        foreach (var pack in packs) {
            if (string.Equals(pack.Name, wanted.Name, StringComparison.OrdinalIgnoreCase)
                && pack.Fingerprint == wanted.Fingerprint) {
                return pack;
            }
        }

        foreach (var pack in packs) {
            if (pack.Fingerprint == wanted.Fingerprint) {
                return pack;
            }
        }

        foreach (var pack in packs) {
            if (string.Equals(pack.Name, wanted.Name, StringComparison.OrdinalIgnoreCase)) {
                return pack;
            }
        }

        return null;
    }

    /// <summary>
    /// A fingerprint of the art in a skin: the path and size of each of its images, so that the same skin in a folder
    /// of another name has the same one.
    /// </summary>
    public static string FingerprintOf(string dir) {
        if (Fingerprints.TryGetValue(dir, out var known)) {
            return known;
        }

        var lines = new List<string>();
        try {
            foreach (var file in Directory.EnumerateFiles(dir, "*.png", SearchOption.AllDirectories)) {
                var relative = file.Substring(dir.Length).TrimStart('\\', '/').Replace('\\', '/').ToLowerInvariant();
                lines.Add($"{relative}|{new FileInfo(file).Length}");
            }
        } catch (Exception e) {
            Logger.Warn($"Could not read the files of the skin in '{dir}': {e.Message}");
        }

        lines.Sort(StringComparer.Ordinal);

        // FNV-1a over the lines, which stays the same from one run and one computer to the next
        var hash = 14695981039346656037UL;
        foreach (var line in lines) {
            foreach (var b in Encoding.UTF8.GetBytes(line + "\n")) {
                hash ^= b;
                hash *= 1099511628211UL;
            }
        }

        var fingerprint = hash.ToString("x16");
        Fingerprints[dir] = fingerprint;
        return fingerprint;
    }
}

/// <summary>
/// A skin of Silksong Customizer on this computer.
/// </summary>
internal sealed class SkinPack {
    /// <summary>
    /// The name that other games know it by: the folder of the plugin that holds its Customizer folder, or its own
    /// folder in Mods/Customizer.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The folder that holds its active.txt and a folder of art for each sheet of sprites that it dresses.
    /// </summary>
    public string Dir { get; }

    public SkinPack(string name, string dir) {
        Name = name;
        Dir = dir;
    }

    /// <summary>
    /// Whether Customizer dresses the game with it.
    /// </summary>
    public bool IsActive => File.Exists(Path.Combine(Dir, "active.txt"));

    /// <summary>
    /// The fingerprint of its art.
    /// </summary>
    public string Fingerprint => CustomizerPacks.FingerprintOf(Dir);

    /// <summary>
    /// How other games name it.
    /// </summary>
    public SkinPackRef Ref => new(Name, Fingerprint);

    /// <summary>
    /// Whether it has art for any of the given sheets of sprites, by their names as Customizer matches them.
    /// </summary>
    public bool Dresses(ICollection<string> sheets) {
        try {
            foreach (var dir in Directory.GetDirectories(Dir)) {
                if (sheets.Contains(Path.GetFileName(dir))) {
                    return true;
                }
            }
        } catch (Exception e) {
            Logger.Warn($"Could not read the folders of the skin '{Name}': {e.Message}");
        }

        return false;
    }

    /// <summary>
    /// The names of the sheets of sprites that it has art for: the names of its folders.
    /// </summary>
    public List<string> SheetNames() {
        var names = new List<string>();
        try {
            foreach (var dir in Directory.GetDirectories(Dir)) {
                names.Add(Path.GetFileName(dir));
            }
        } catch (Exception e) {
            Logger.Warn($"Could not read the folders of the skin '{Name}': {e.Message}");
        }

        return names;
    }

    /// <summary>
    /// The image of its art for an atlas of a sheet of sprites, as Customizer names it, or null if it has none.
    /// </summary>
    public string? ArtFile(string sheet, string texture) {
        var path = Path.Combine(Dir, sheet, texture + ".png");
        return File.Exists(path) ? path : null;
    }
}

/// <summary>
/// Player Color, which draws the cloak of the player's own character in a colour of their choice with a shader of its
/// own and a mask of the cloak for each atlas, kept in Mods/Mask of the game's data.
/// </summary>
internal static class PlayerColorMod {
    /// <summary>
    /// The ID of Player Color's plugin.
    /// </summary>
    private const string PluginGuid = "com.PDE26jjk.PlayerColor";

    /// <summary>
    /// The masks loaded so far, by sheet and atlas, with null for those that have none.
    /// </summary>
    private static readonly Dictionary<string, Texture2D?> Masks = new(StringComparer.Ordinal);

    /// <summary>
    /// Backing field for <see cref="NoMask"/>.
    /// </summary>
    private static Texture2D? _noMask;

    /// <summary>
    /// The field in which Player Color keeps the masks it loaded, once looked for, null if it has none.
    /// </summary>
    private static FieldInfo? _theirMasks;
    private static bool _theirMasksLooked;

    /// <summary>
    /// The folder of the masks.
    /// </summary>
    private static string MaskDir => Path.Combine(Application.dataPath, "Mods", "Mask");

    /// <summary>
    /// Player Color's plugin, or null if it isn't loaded.
    /// </summary>
    private static BaseUnityPlugin? Plugin =>
        Chainloader.PluginInfos.TryGetValue(PluginGuid, out var info) ? info.Instance : null;

    /// <summary>
    /// Whether Player Color is loaded, has its masks and its shader, and so can draw a cloak in a colour.
    /// </summary>
    public static bool CanDraw => Plugin != null && Directory.Exists(MaskDir) && Shader != null;

    /// <summary>
    /// The shader that Player Color draws with, or null if it hasn't loaded it.
    /// </summary>
    public static Shader? Shader {
        get {
            var plugin = Plugin;
            if (plugin == null) {
                return null;
            }

            var shader = plugin.GetType()
                .GetField("PlayerColorShader", BindingFlags.NonPublic | BindingFlags.Static)?
                .GetValue(null) as Shader;
            return shader != null ? shader : Shader.Find("Mod/PlayerColor");
        }
    }

    /// <summary>
    /// The colour that Player Color gives the player's own cloak, or null if it gives it none: not loaded, switched
    /// off, without its masks or with its settings not read yet.
    /// </summary>
    public static PlayerColorLook? LocalLook() {
        var plugin = Plugin;
        if (plugin == null || !Directory.Exists(MaskDir)) {
            return null;
        }

        try {
            var settings = plugin.GetType().Assembly.GetType("silksong_PlayerColor.Settings");
            if (settings == null) {
                return null;
            }

            var use = Entry<bool>(settings, "UseOverrideMaterial");
            var color = Entry<Color>(settings, "Color2");
            var saturation = Entry<float>(settings, "Saturation");
            var brightness = Entry<float>(settings, "Brightness");
            if (use != true || color == null || saturation == null || brightness == null) {
                return null;
            }

            return new PlayerColorLook(color.Value, saturation.Value, brightness.Value);
        } catch (Exception e) {
            Logger.Warn($"Could not read the settings of Player Color: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Makes Player Color look again at every object that it chose to leave alone. It chooses only the first time it
    /// sees each, by whether there is a mask for the atlas the object draws from then, and an object that wore a skin,
    /// whose art has no mask, may wear the game's own art now.
    /// </summary>
    public static void LookAgain() {
        var plugin = Plugin;
        if (plugin == null) {
            return;
        }

        try {
            var others = plugin.GetType().GetField("otherObjs", BindingFlags.NonPublic | BindingFlags.Static);
            if (others?.GetValue(null) is HashSet<GameObject> leftAlone) {
                leftAlone.Clear();
            }
        } catch (Exception e) {
            Logger.Warn($"Could not have Player Color look again at what it leaves alone: {e.Message}");
        }
    }

    /// <summary>
    /// Calls the given action whenever a setting of Player Color changes, and returns what stops that, or null if
    /// Player Color isn't loaded.
    /// </summary>
    public static Action? OnSettingsChanged(Action action) {
        var config = Plugin?.Config;
        if (config == null) {
            return null;
        }

        EventHandler<SettingChangedEventArgs> handler = (_, _) => action();
        config.SettingChanged += handler;
        return () => config.SettingChanged -= handler;
    }

    /// <summary>
    /// The mask of the cloak for an atlas of a sheet of sprites, or <see cref="NoMask"/> if it has none: the one that
    /// Player Color loaded for the player's own hero if it did, which it keeps for good, else one loaded here once.
    /// </summary>
    public static Texture MaskFor(string sheet, string texture) {
        var key = sheet + "/" + texture;
        var theirs = LoadedByPlayerColor(key);
        if (theirs != null) {
            // One loaded here before Player Color had its own is used no more. It is not destroyed: other characters
            // may draw with it until they are dressed again, and the game frees it once nothing does
            Masks.Remove(key);
            return theirs;
        }

        if (!Masks.TryGetValue(key, out var mask)) {
            mask = null;
            var path = Path.Combine(MaskDir, sheet, texture + ".png");
            try {
                if (File.Exists(path)) {
                    var loaded = new Texture2D(2, 2);
                    // Not kept readable: it is only drawn with, and a copy in memory as well would double its size
                    if (loaded.LoadImage(File.ReadAllBytes(path), true)) {
                        loaded.name = "SSMP Player Color mask " + key;
                        mask = loaded;
                    } else {
                        UnityEngine.Object.Destroy(loaded);
                    }
                }
            } catch (Exception e) {
                Logger.Warn($"Could not load the mask of Player Color for '{key}': {e.Message}");
            }

            Masks[key] = mask;
        }

        return mask != null ? mask : NoMask;
    }

    /// <summary>
    /// The mask that Player Color loaded for the given atlas, or null if it hasn't: it loads them all when the first
    /// room loads, unless told not to, and else each the first time the player's own hero draws from its atlas.
    /// </summary>
    private static Texture? LoadedByPlayerColor(string key) {
        var plugin = Plugin;
        if (plugin == null) {
            return null;
        }

        try {
            if (!_theirMasksLooked) {
                _theirMasksLooked = true;
                _theirMasks = plugin.GetType().GetField("atlas2Mask", BindingFlags.NonPublic | BindingFlags.Static);
            }

            return _theirMasks?.GetValue(null) is Dictionary<string, Texture> masks
                   && masks.TryGetValue(key, out var mask) && mask != null
                ? mask
                : null;
        } catch (Exception e) {
            Logger.Warn($"Could not read the masks that Player Color loaded: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// A mask that colours nothing, which Player Color draws the atlases without a mask of their own with.
    /// </summary>
    public static Texture2D NoMask {
        get {
            if (_noMask == null) {
                _noMask = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _noMask.SetPixel(0, 0, Color.black);
                _noMask.Apply();
            }

            return _noMask;
        }
    }

    /// <summary>
    /// The value of a setting of Player Color, or null if it has none yet.
    /// </summary>
    private static T? Entry<T>(Type settings, string name) where T : struct {
        var entry = settings.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        return entry is ConfigEntry<T> typed ? typed.Value : null;
    }
}

/// <summary>
/// The plugins of BepInEx that this game has loaded, as far as skins go.
/// </summary>
internal static class SkinModPlugins {
    /// <summary>
    /// Skin mods whose skins no other game can show yet, by a part of their name.
    /// </summary>
    private static readonly string[] UnshownNames = ["Patchwork", "Stitchwork"];

    /// <summary>
    /// Whether a loaded plugin matches.
    /// </summary>
    public static bool Find(Func<PluginInfo, bool> match) {
        try {
            return Chainloader.PluginInfos.Values.Any(match);
        } catch (Exception e) {
            Logger.Warn($"Could not go through the loaded plugins: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// The names of the loaded skin mods whose skins no other game can show yet.
    /// </summary>
    public static List<string> Unshown() {
        var names = new List<string>();
        try {
            foreach (var info in Chainloader.PluginInfos.Values) {
                var name = info.Metadata?.Name ?? "";
                if (UnshownNames.Any(part => name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)) {
                    names.Add(name);
                }
            }
        } catch (Exception e) {
            Logger.Warn($"Could not go through the loaded plugins: {e.Message}");
        }

        return names;
    }
}
