using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using MonoMod.RuntimeDetour;
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
    /// What Nexus Mods puts after the name of a file it hands out, which the folder it unpacks into keeps: the number of
    /// the mod, the version, its dots as dashes, and the time of the upload, as in "StarsNSparkles Cohesive
    /// Colors-653-1-0-1766869339".
    /// </summary>
    private static readonly Regex NexusSuffix = new(@"-\d+(?:-[0-9A-Za-z]+)*-\d{9,}$", RegexOptions.CultureInvariant);

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
    /// the same name, also without what Nexus Mods puts after it, or null if there is none.
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

        // A skin downloaded from Nexus Mods unpacks into a folder named after the file with the mod, the version and the
        // time after it, which one player keeps and another cuts down to the name: the friend's skin was there, in a
        // folder of that name with what came after it, and its files differed by two images that the player had
        // renamed, so neither the files nor the name found it (USER 10-24: "在她那边我穿的就是斗篷换色了")
        var bare = BareName(wanted.Name);
        foreach (var pack in packs) {
            if (string.Equals(BareName(pack.Name), bare, StringComparison.OrdinalIgnoreCase)) {
                return pack;
            }
        }

        return null;
    }

    /// <summary>
    /// The name of a skin's folder without what Nexus Mods puts after the name of the file it came in.
    /// </summary>
    private static string BareName(string name) => NexusSuffix.Replace(name.Trim(), "");

    /// <summary>
    /// A fingerprint of the art in a skin: the path and size of each of its images, so that the same skin in a folder
    /// of another name has the same one. An image switched off by ".off" after its name counts as the image it was.
    /// </summary>
    public static string FingerprintOf(string dir) {
        if (Fingerprints.TryGetValue(dir, out var known)) {
            return known;
        }

        var lines = new List<string>();
        try {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) {
                // The same skin with one of its images switched off, as the image of the title screen was here, had
                // another fingerprint, so the game of a player who has it all on didn't know it (USER 10-24: "这次换我这边
                // 认不出来了")
                var name = file.EndsWith(".off", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
                if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                var relative = name.Substring(dir.Length).TrimStart('\\', '/').Replace('\\', '/').ToLowerInvariant();
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
    /// The full name of the type that holds Player Color's settings.
    /// </summary>
    private const string SettingsTypeName = "silksong_PlayerColor.Settings";

    /// <summary>
    /// The properties of Player Color's shader.
    /// </summary>
    private static readonly int MainTexId = UnityEngine.Shader.PropertyToID("_MainTex");
    private static readonly int MaskTexId = UnityEngine.Shader.PropertyToID("_MaskTex");
    private static readonly int FlashColorId = UnityEngine.Shader.PropertyToID("_FlashColor");
    private static readonly int FlashAmountId = UnityEngine.Shader.PropertyToID("_FlashAmount");
    private static readonly int MaskFromColorId = UnityEngine.Shader.PropertyToID("_MaskFromColor1");
    private static readonly int MaskColorId = UnityEngine.Shader.PropertyToID("_MaskColor1");
    private static readonly int BrightnessId = UnityEngine.Shader.PropertyToID("t1");
    private static readonly int SaturationId = UnityEngine.Shader.PropertyToID("t2");

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
    /// Whether Player Color is loaded.
    /// </summary>
    public static bool IsLoaded => Plugin != null;

    /// <summary>
    /// Whether the masks of Player Color are where it looks for them, which come apart from it.
    /// </summary>
    public static bool HasMasks => Directory.Exists(MaskDir);

    /// <summary>
    /// Whether Player Color is loaded, has its masks and its shader, and so can draw a cloak in a colour.
    /// </summary>
    public static bool CanDraw => Plugin != null && Directory.Exists(MaskDir) && Shader != null;

    /// <summary>
    /// Whether the colour of the cloak can be chosen here: Player Color can draw it and has made its settings, which
    /// it does once the game has its game manager.
    /// </summary>
    public static bool CanChoose =>
        CanDraw && Setting<bool>("UseOverrideMaterial") != null && Setting<Color>("Color2") != null;

    /// <summary>
    /// The colour that Player Color draws the player's own cloak in, or null while it leaves the cloak in its own.
    /// </summary>
    public static Color? ChosenColour() {
        return FindColourSettings() && _useSetting!.Value ? _colourSetting!.Value : null;
    }

    /// <summary>
    /// Whether Player Color has made its settings of the cloak's colour, which a colour for it is chosen in: not while
    /// it isn't installed, nor where it stopped loading before it made them.
    /// </summary>
    public static bool HasColourSettings => FindColourSettings();

    /// <summary>
    /// Player Color's settings of whether it draws the player's own cloak in a colour and of that colour, once found,
    /// and when to look for them again while they aren't, in unscaled time.
    /// </summary>
    private static ConfigEntry<bool>? _useSetting;
    private static ConfigEntry<Color>? _colourSetting;
    private static float _lookForSettingsAt;

    /// <summary>
    /// Raised as the colour that Player Color draws the player's own cloak in changes, or whether it draws it in one -
    /// on this mod's page of the skin, in Player Color's own window or from its file - once its settings were found
    /// (see <see cref="ChosenColour"/>), which they are before anything is drawn in that colour.
    /// </summary>
    public static event Action? ColourChanged;

    /// <summary>
    /// Finds Player Color's settings of the cloak's colour, which it makes once, and keeps them, as the outline of the
    /// player's own hero asks for the colour at every frame (see PlayerManager.OwnColour), and hears each change of
    /// them (see <see cref="ColourChanged"/>). Until they are made, they are looked for at most once a second.
    /// </summary>
    /// <returns>Whether both were found.</returns>
    private static bool FindColourSettings() {
        if (_useSetting != null && _colourSetting != null) {
            return true;
        }

        if (Time.unscaledTime < _lookForSettingsAt) {
            return false;
        }

        if (_useSetting == null && Setting<bool>("UseOverrideMaterial") is { } use) {
            _useSetting = use;
            use.SettingChanged += OnColourSettingChanged;
        }

        if (_colourSetting == null && Setting<Color>("Color2") is { } colour) {
            _colourSetting = colour;
            colour.SettingChanged += OnColourSettingChanged;
        }

        if (_useSetting != null && _colourSetting != null) {
            return true;
        }

        _lookForSettingsAt = Time.unscaledTime + 1f;
        return false;
    }

    /// <summary>
    /// Passes a change of Player Color's settings of the cloak's colour on, without letting anything that hears it stop
    /// the change itself, which runs in Player Color's setting.
    /// </summary>
    private static void OnColourSettingChanged(object sender, EventArgs e) {
        try {
            ColourChanged?.Invoke();
        } catch (Exception exception) {
            Logger.Warn($"Could not follow the colour of the cloak in Player Color: {exception}");
        }
    }

    /// <summary>
    /// The colour that Player Color keeps for the player's own cloak, whether or not it draws the cloak in it now, or
    /// null if it hasn't made its settings.
    /// </summary>
    public static Color? KeptColour() => Setting<Color>("Color2")?.Value;

    /// <summary>
    /// Whether a colour chosen while it moved waits to be written to Player Color's file, and whether Player Color wrote
    /// its file at every change of a setting before this held that back (see <see cref="ChooseMoving"/>).
    /// </summary>
    private static bool _unsaved;
    private static bool _savedOnSet;

    /// <summary>
    /// Has Player Color draw the player's own cloak in the given colour from now on, or in its own colour for null.
    /// They are Player Color's own settings, which it saves at once for the next start and which its own window shows.
    /// </summary>
    public static void Choose(Color? colour) {
        Settle();
        Set(colour);
    }

    /// <summary>
    /// Has Player Color draw the player's own cloak in a colour that is still moving, as along a strip of colours: it
    /// draws it at once, and the colour is written to its file once it settles (see <see cref="Settle"/>). Its file
    /// was written again at every step of the way, many times a second as a strip slid along.
    /// </summary>
    public static void ChooseMoving(Color colour) {
        var config = Plugin?.Config;
        if (config != null && !_unsaved) {
            _savedOnSet = config.SaveOnConfigSet;
            config.SaveOnConfigSet = false;
            _unsaved = true;
        }

        Set(colour);
    }

    /// <summary>
    /// Writes a colour chosen while it moved to Player Color's file, once it has settled, and has Player Color write its
    /// file at each change again, as it did.
    /// </summary>
    public static void Settle() {
        if (!_unsaved) {
            return;
        }

        _unsaved = false;
        var config = Plugin?.Config;
        if (config == null) {
            return;
        }

        try {
            config.SaveOnConfigSet = _savedOnSet;
            config.Save();
        } catch (Exception e) {
            Logger.Warn($"Could not save the colour of the cloak in Player Color: {e.Message}");
        }
    }

    /// <summary>
    /// Sets Player Color's settings for the given colour, or for the cloak's own colour for null.
    /// </summary>
    private static void Set(Color? colour) {
        var use = Setting<bool>("UseOverrideMaterial");
        var target = Setting<Color>("Color2");
        if (use == null || target == null) {
            return;
        }

        try {
            if (colour is { } picked) {
                target.Value = picked;
            }

            use.Value = colour != null;
        } catch (Exception e) {
            Logger.Warn($"Could not change the colour of the cloak in Player Color: {e.Message}");
        }
    }

    /// <summary>
    /// Sets what a material of Player Color's shader draws an atlas with: its art, the mask of its cloak, the flash of
    /// the material that the sprite draws with otherwise, and the colour.
    /// </summary>
    /// <param name="colored">The material.</param>
    /// <param name="art">The art of the atlas.</param>
    /// <param name="mask">The mask of the atlas, or <see cref="NoMask"/> for art that has none.</param>
    /// <param name="source">The material that the sprite draws with otherwise.</param>
    /// <param name="look">The colour.</param>
    public static void Paint(Material colored, Texture? art, Texture mask, Material source, PlayerColorLook look) {
        colored.SetTexture(MainTexId, art);
        colored.SetTexture(MaskTexId, mask);
        if (source.HasColor(FlashColorId)) {
            colored.SetColor(FlashColorId, source.GetColor(FlashColorId));
            colored.SetFloat(FlashAmountId, source.GetFloat(FlashAmountId));
        }

        colored.SetFloat(BrightnessId, look.Brightness);
        colored.SetFloat(SaturationId, look.Saturation);
        colored.SetColor(MaskFromColorId, SkinLook.PlayerColorOriginal);
        colored.SetColor(MaskColorId, look.Color);
    }

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
    /// The hook that has a flash of a sprite leave white behind once it is at nothing, while it is in place.
    /// </summary>
    private static Hook? _flashHook;

    /// <summary>
    /// Has every flash of a sprite leave white behind once it is at nothing, while Player Color is loaded. The game
    /// leaves the colour of the last flash in place when a flash ends, which its own shaders show nothing of at
    /// nothing, but Player Color's shader still tints the hero with it: after the orange flashes of hot coals the
    /// friend's hero stayed yellow, with a pale purple cloak, until the next white flash, which a hit of the needle
    /// gives as it brings silk (USER 10-24: "被岩块烫过一下之后脸就会一直黄，飞针一下又好了"). White is what the game
    /// itself leaves once a flash is cancelled.
    /// </summary>
    public static void HookFlashes() {
        if (_flashHook != null || Plugin == null) {
            return;
        }

        var method = typeof(SpriteFlash).GetMethod(
            "SetParams",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            [typeof(float), typeof(Color)],
            null
        );
        if (method == null) {
            Logger.Warn("Could not find how the game sets the flash of a sprite, so with Player Color a flash may leave " +
                        "its colour on the hero");
            return;
        }

        try {
            _flashHook = new Hook(
                method,
                (Action<Action<SpriteFlash, float, Color>, SpriteFlash, float, Color>) OnSetFlash
            );
        } catch (Exception e) {
            Logger.Warn($"Could not hook the flash of sprites, so with Player Color a flash may leave its colour on the " +
                        $"hero: {e.Message}");
        }
    }

    /// <summary>
    /// Takes away the hook of <see cref="HookFlashes"/>.
    /// </summary>
    public static void UnhookFlashes() {
        _flashHook?.Dispose();
        _flashHook = null;
    }

    /// <summary>
    /// The game sets how strong and in what colour a sprite flashes; a flash at nothing goes in white.
    /// </summary>
    private static void OnSetFlash(
        Action<SpriteFlash, float, Color> orig,
        SpriteFlash self,
        float amount,
        Color colour
    ) {
        orig(self, amount, amount > 0f ? colour : Color.white);
    }

    /// <summary>
    /// The colour that Player Color gives the player's own cloak, or null if it gives it none: not loaded, switched
    /// off, without its masks or with its settings not read yet.
    /// </summary>
    public static PlayerColorLook? LocalLook() {
        if (Plugin == null || !Directory.Exists(MaskDir)) {
            return null;
        }

        var use = Setting<bool>("UseOverrideMaterial");
        var color = Setting<Color>("Color2");
        var saturation = Setting<float>("Saturation");
        var brightness = Setting<float>("Brightness");
        if (use is not { Value: true } || color == null || saturation == null || brightness == null) {
            return null;
        }

        return new PlayerColorLook(color.Value, saturation.Value, brightness.Value);
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
    /// A setting of Player Color, or null if it isn't loaded or has made no settings yet.
    /// </summary>
    private static ConfigEntry<T>? Setting<T>(string name) {
        var plugin = Plugin;
        if (plugin == null) {
            return null;
        }

        try {
            return plugin.GetType().Assembly.GetType(SettingsTypeName)?
                .GetProperty(name, BindingFlags.Public | BindingFlags.Static)?
                .GetValue(null) as ConfigEntry<T>;
        } catch (Exception e) {
            Logger.Warn($"Could not read the setting '{name}' of Player Color: {e.Message}");
            return null;
        }
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
