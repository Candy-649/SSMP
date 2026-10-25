using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using SSMP.Hooks;
using SSMP.Util;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Skin;

/// <summary>
/// CustomizerT2D, the companion of Customizer that dresses the textures that Customizer doesn't: the atlases that Unity
/// packs sprites into, among them the pages that the hero's charged slashes are drawn from. As the game starts it reads
/// the images of the Texture2D folder of each skin in the game's folder of skins that holds an active.txt, or of every
/// skin there when none does, the last skin by name winning each texture; then, as each texture shows, it loads the
/// image of the same name and size into it, so that the game's own art of that texture is gone until the game quits.
///
/// This hooks in before that: it keeps the game's own art of the pages that the hero's attacks draw from as they are
/// about to be dressed, so that a player whose charged slash is drawn in the game's own art shows it on every screen
/// (see <see cref="AtlasSlashes"/>); and it takes no skin's images where none is switched on but some were switched
/// off, as choosing the game's own look on the settings page does: CustomizerT2D took the images of every skin then
/// (USER 10-25: the friend's charged slash showed the stars of a skin that nobody wore).
/// </summary>
internal static class CustomizerT2D {
    /// <summary>
    /// The plugin's GUID, which SSMP names as a soft dependency, so that it loads first and this hooks into it before
    /// it starts.
    /// </summary>
    public const string PluginGuid = "com.yourname.customizer.t2donly";

    /// <summary>
    /// The full name of the type of the plugin.
    /// </summary>
    private const string PluginTypeName = "CustomizerT2DOnly.CustomizerT2DOnly";

    /// <summary>
    /// The atlases whose pages hold the pictures of the hero's attacks (see <see cref="AtlasSlashPictures"/>): the
    /// charged slashes of most crests and two more attacks on "Hornet", the beast's charged slash on "Beast_Slash", the
    /// shaman's blades on "Tools", the architect's charged slash on "Core". Named here for the pages that CustomizerT2D
    /// dresses before there was a hero to find the pictures on; after that, a page counts by its pictures.
    /// </summary>
    public static readonly HashSet<string> AttackAtlases = new(StringComparer.OrdinalIgnoreCase) {
        "Hornet", "Beast_Slash", "Tools", "Core"
    };

    /// <summary>
    /// The fields of the plugin that say what it will dress: the names of its images, its images with their sizes, and
    /// the textures it dressed and those it won't.
    /// </summary>
    private static FieldInfo? _keysField;
    private static FieldInfo? _mapField;
    private static FieldInfo? _replacedField;
    private static FieldInfo? _notOursField;

    /// <summary>
    /// The textures, by instance, that the plugin was found to dress or to have dressed, whose game's own art was kept
    /// then if it was wanted; and those that it was found not to dress, until it reads its images again. It tries to
    /// dress each texture that shows, again and again.
    /// </summary>
    private static readonly HashSet<int> Decided = [];
    private static readonly HashSet<int> NotDressed = [];

    /// <summary>
    /// The textures that the plugin dressed, by name.
    /// </summary>
    private static readonly HashSet<string> Dressed = new(StringComparer.Ordinal);

    /// <summary>
    /// The copies of the game's own art of the pages of the attack atlases that the plugin was about to dress, by the
    /// name of the page, until the pictures of the attacks are cut out of them.
    /// </summary>
    private static readonly Dictionary<string, Texture2D> Kept = new(StringComparer.Ordinal);

    /// <summary>
    /// The skins that the plugin took images from, in its order, once it chose them.
    /// </summary>
    private static List<string>? _skins;

    /// <summary>
    /// The image that the plugin took for each page, by the name it matches pages by, or null for none.
    /// </summary>
    private static readonly Dictionary<string, string?> ImageOfPage = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether something here failed, which is logged once.
    /// </summary>
    private static bool _failed;

    /// <summary>
    /// Whether the plugin is loaded and this hooked into it.
    /// </summary>
    public static bool IsLoaded { get; private set; }

    /// <summary>
    /// Raised when the game's own art of a page was kept.
    /// </summary>
    public static event Action? PageKept;

    /// <summary>
    /// Hooks into the plugin, if it is loaded: from SSMP's Awake, before it starts, as it chooses its skins then.
    /// </summary>
    public static void Hook() {
        if (IsLoaded) {
            return;
        }

        var type = AccessTools.TypeByName(PluginTypeName);
        if (type == null) {
            return;
        }

        try {
            _keysField = AccessTools.Field(type, "_keys");
            _mapField = AccessTools.Field(type, "_map");
            _replacedField = AccessTools.Field(type, "_replacedIds");
            _notOursField = AccessTools.Field(type, "_notOurTex");

            var harmony = new Harmony("ssmp.customizert2d");
            var swap = AccessTools.Method(type, "TrySwap", [typeof(Texture2D)]);
            if (swap != null && swap.ReturnType == typeof(bool) && _keysField != null && _replacedField != null &&
                _notOursField != null) {
                harmony.Patch(
                    swap,
                    prefix: new HarmonyMethod(typeof(CustomizerT2D), nameof(BeforeSwap)),
                    postfix: new HarmonyMethod(typeof(CustomizerT2D), nameof(AfterSwap))
                );
            } else {
                Logger.Warn("Could not find how CustomizerT2D dresses a texture: a charged slash drawn in the game's " +
                            "own art may show its skin");
            }

            var skins = AccessTools.Method(type, "GetTexturePackDirsToScan", [typeof(string)]);
            if (skins != null && skins.ReturnType == typeof(List<string>)) {
                harmony.Patch(skins, postfix: new HarmonyMethod(typeof(CustomizerT2D), nameof(AfterSkinsChosen)));
            } else {
                Logger.Warn("Could not find how CustomizerT2D chooses its skins");
            }

            IsLoaded = true;
        } catch (Exception e) {
            Logger.Warn($"Could not hook into CustomizerT2D: {e.Message}");
        }
    }

    /// <summary>
    /// Whether the plugin dresses the texture of the given name, so that its own art is no longer the game's.
    /// </summary>
    public static bool HasDressed(string texture) => Dressed.Contains(texture);

    /// <summary>
    /// The names of the pages whose game's own art is kept.
    /// </summary>
    public static List<string> KeptPages() => [..Kept.Keys];

    /// <summary>
    /// Takes the kept copy of the game's own art of a page, or null if none is kept: it is the caller's to destroy.
    /// </summary>
    public static Texture2D? TakeKept(string page) {
        if (!Kept.TryGetValue(page, out var kept)) {
            return null;
        }

        Kept.Remove(page);
        return kept;
    }

    /// <summary>
    /// The image that the plugin dressed a page with: the image of the same name of the last skin it took images
    /// from that has one, or null if it dressed it with none.
    /// </summary>
    public static string? ImageOf(string page) {
        if (!Dressed.Contains(page) || _skins == null) {
            return null;
        }

        var name = Normalize(page);
        if (ImageOfPage.TryGetValue(name, out var found)) {
            return found;
        }

        found = null;
        foreach (var skin in _skins) {
            if (SkinImages.Of(skin).TryGetValue(name, out var image)) {
                found = image;
            }
        }

        ImageOfPage[name] = found;
        return found;
    }

    /// <summary>
    /// The name that the plugin matches textures and images by: a page of an atlas without the mark of its packing at
    /// its end, so that an image made for another packing of a page still dresses it.
    /// </summary>
    public static string Normalize(string name) {
        if (string.IsNullOrEmpty(name)) {
            return name;
        }

        name = name.Replace("|", "_");
        if (!name.StartsWith("sactx-", StringComparison.OrdinalIgnoreCase)) {
            return name;
        }

        var dash = name.LastIndexOf('-');
        var markLength = name.Length - dash - 1;
        if (dash <= 0 || markLength is < 6 or > 10) {
            return name;
        }

        for (var i = dash + 1; i < name.Length; i++) {
            if (!Uri.IsHexDigit(name[i])) {
                return name;
            }
        }

        return name.Substring(0, dash);
    }

    /// <summary>
    /// The atlas that a page belongs to by the name that the plugin matches it by: "Hornet" for
    /// "sactx-0-4096x4096-BC7-Hornet".
    /// </summary>
    public static string AtlasOf(string normalized) {
        var parts = normalized.Split(['-'], 5);
        return parts.Length == 5 ? parts[4] : "";
    }

    /// <summary>
    /// As the plugin is about to try to dress a texture, which it does where it has an image of the same name and
    /// size: the game's own art of a page of the attacks is copied first, unless its pictures were cut out of it
    /// already. The copy is kept once the plugin did dress it (see <see cref="AfterSwap"/>).
    /// </summary>
    private static void BeforeSwap(object __instance, Texture2D? __0, out Texture2D? __state) {
        __state = null;
        try {
            if (__0 == null) {
                return;
            }

            var id = __0.GetInstanceID();
            if (Decided.Contains(id) || NotDressed.Contains(id)) {
                return;
            }

            // Nothing is dressed before the plugin has read its images
            if (_keysField!.GetValue(__instance) is not HashSet<string> keys ||
                _replacedField!.GetValue(__instance) is not HashSet<int> replaced ||
                _notOursField!.GetValue(__instance) is not HashSet<int> notOurs) {
                return;
            }

            // Dressed before: its art is no longer the game's own
            if (replaced.Contains(id)) {
                Decided.Add(id);
                return;
            }

            var name = __0.name;
            var normalized = Normalize(name);
            if (notOurs.Contains(id) || !keys.Contains(normalized) || !SameSize(__instance, normalized, __0)) {
                NotDressed.Add(id);
                return;
            }

            Decided.Add(id);
            var attackPage = AtlasSlashPictures.IsRead
                ? AtlasSlashPictures.Of(name) is { Pictures.Count: > 0 }
                : AttackAtlases.Contains(AtlasOf(normalized));
            if (!attackPage || Kept.ContainsKey(name) || AtlasSlashes.HasGameArt(name)) {
                return;
            }

            if ((SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) == 0 ||
                (SystemInfo.copyTextureSupport & CopyTextureSupport.RTToTexture) == 0) {
                Fail("this computer can't copy textures on the graphics card");
                return;
            }

            // Only the full size, on the graphics card alone
            var copy = new Texture2D(__0.width, __0.height, __0.graphicsFormat, 1,
                TextureCreationFlags.DontInitializePixels) {
                name = $"SSMP game's own {name}",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            copy.Apply(false, true);
            Graphics.CopyTexture(__0, 0, 0, copy, 0, 0);
            __state = copy;
        } catch (Exception e) {
            Fail(e.Message);
        }
    }

    /// <summary>
    /// As the plugin tried to dress a texture: whether it did is what counts, and the copy of the game's own art made
    /// before is kept only then.
    /// </summary>
    private static void AfterSwap(bool __result, Texture2D? __0, Texture2D? __state) {
        try {
            if (__result && __0 != null) {
                Dressed.Add(__0.name);
            }

            if (__state == null) {
                return;
            }

            if (__result && __0 != null && !Kept.ContainsKey(__0.name)) {
                Kept[__0.name] = __state;
                Logger.Info($"Kept the game's own art of '{__0.name}' as CustomizerT2D dressed it");
                PageKept?.Invoke();
            } else {
                Object.Destroy(__state);
            }
        } catch (Exception e) {
            Fail(e.Message);
        }
    }

    /// <summary>
    /// Whether the plugin's image of the given name has the size of the texture, which it dresses only then.
    /// </summary>
    private static bool SameSize(object plugin, string normalized, Texture2D texture) {
        if (_mapField?.GetValue(plugin) is not IDictionary map || !map.Contains(normalized)) {
            return true;
        }

        return map[normalized] is not ValueTuple<byte[], int, int> image ||
               image.Item2 == texture.width && image.Item3 == texture.height;
    }

    /// <summary>
    /// As the plugin has chosen the skins to take images from: where no skin holds an active.txt it chose every one,
    /// and none is taken where some were switched off.
    /// </summary>
    private static void AfterSkinsChosen(ref List<string>? __result) {
        try {
            if (__result == null) {
                return;
            }

            // Some skin was switched off and none is on, as for the game's own look: no skin's images then
            if (!__result.Exists(skin => File.Exists(Path.Combine(skin, "active.txt"))) &&
                __result.Exists(skin => File.Exists(Path.Combine(skin, "active.txt.off")))) {
                Logger.Info($"No skin is switched on and some were switched off: CustomizerT2D takes the images of " +
                            $"none of the {__result.Count} skins, rather than of every one");
                __result = [];
            }

            // The images are read again: a texture it didn't dress before may be dressed now
            _skins = [..__result];
            ImageOfPage.Clear();
            NotDressed.Clear();
        } catch (Exception e) {
            Fail(e.Message);
        }
    }

    /// <summary>
    /// Logs the first thing that failed here.
    /// </summary>
    private static void Fail(string message) {
        if (_failed) {
            return;
        }

        _failed = true;
        Logger.Warn("Could not keep the game's own art of the attacks from CustomizerT2D, so a charged slash drawn " +
                    $"in the game's own art may show its skin: {message}");
    }
}

/// <summary>
/// The images of the Texture2D folders of skins, which CustomizerT2D dresses the game's atlases with, by the name that
/// it matches them by, and their sizes.
/// </summary>
internal static class SkinImages {
    /// <summary>
    /// The images of each skin's folder, by the folder.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> Images =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The width and height of each image, from its header, or zero for one that couldn't be read.
    /// </summary>
    private static readonly Dictionary<string, Vector2Int> Sizes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The images of a skin's Texture2D folder, by the name that CustomizerT2D matches them by, as it reads them: the
    /// last of several of the same name winning. Looked up once, as the skins don't change while the game runs.
    /// </summary>
    /// <param name="skin">The folder of the skin.</param>
    public static Dictionary<string, string> Of(string skin) {
        if (Images.TryGetValue(skin, out var images)) {
            return images;
        }

        images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try {
            var folder = Path.Combine(skin, "Texture2D");
            if (Directory.Exists(folder)) {
                foreach (var file in Directory.GetFiles(folder, "*.png")) {
                    if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) {
                        images[CustomizerT2D.Normalize(Path.GetFileNameWithoutExtension(file))] = file;
                    }
                }
            }
        } catch (Exception e) {
            Logger.Warn($"Could not read the Texture2D folder of '{skin}': {e.Message}");
        }

        Images[skin] = images;
        return images;
    }

    /// <summary>
    /// The size of an image, read from its header.
    /// </summary>
    public static Vector2Int SizeOf(string file) {
        if (Sizes.TryGetValue(file, out var size)) {
            return size;
        }

        size = Vector2Int.zero;
        try {
            using var stream = File.OpenRead(file);
            var header = new byte[24];
            if (stream.Read(header, 0, header.Length) == header.Length && header[12] == 'I' && header[13] == 'H' &&
                header[14] == 'D' && header[15] == 'R') {
                size = new Vector2Int(BigEndian(header, 16), BigEndian(header, 20));
            }
        } catch (Exception e) {
            Logger.Warn($"Could not read the size of '{file}': {e.Message}");
        }

        Sizes[file] = size;
        return size;

        static int BigEndian(byte[] bytes, int at) =>
            bytes[at] << 24 | bytes[at + 1] << 16 | bytes[at + 2] << 8 | bytes[at + 3];
    }
}

/// <summary>
/// The pictures that the hero's attacks draw with renderers of sprites, from pages of atlases that Unity packs rather
/// than from the sheets that Customizer dresses: the charged slashes of the hunter, reaper, witch, shaman, beast and
/// architect crests, the witch's follow-up to her down slash and the shaman's blades. Only CustomizerT2D dresses those,
/// in each game as that game's skins go, and another player's attack is a copy of the local hero's (see HeroCopies), so
/// each player saw everyone's charged slashes in their own game's art (USER 10-25: "我这边看我朋友那边她蓄力斩是带star n的
/// 刀光的，我朋友那边看自己是不带的").
///
/// Read once from a hero, the one loaded in the menu or the one playing: the sprites that the renderers under its
/// attacks show and those that their clips show, sampled frame by frame on copies. For each, its shape and where it
/// sits on its page, so that it can be cut out of the game's art of the page, or a skin's image of it, and drawn just
/// the same (see <see cref="AtlasPageArt"/>).
/// </summary>
internal static class AtlasSlashPictures {
    /// <summary>
    /// The pixels kept around each picture, which its edges are smoothed with as it is drawn.
    /// </summary>
    private const int Margin = 2;

    /// <summary>
    /// How wide the texture is at most that the pictures of a page are cut out onto.
    /// </summary>
    private const int CutWidth = 4096;

    /// <summary>
    /// The pictures of each page, by the name of the page, once read.
    /// </summary>
    private static Dictionary<string, Page>? _pages;

    /// <summary>
    /// Whether they are read.
    /// </summary>
    public static bool IsRead => _pages != null;

    /// <summary>
    /// The names of the pages that hold them.
    /// </summary>
    public static ICollection<string> Pages => _pages != null ? _pages.Keys : [];

    /// <summary>
    /// The pictures of the given page, or null for none.
    /// </summary>
    public static Page? Of(string page) => _pages != null && _pages.TryGetValue(page, out var found) ? found : null;

    /// <summary>
    /// The picture that a sprite of the game's own on a page shows: the one of its name, if it is the same part of the
    /// page; or null for none.
    /// </summary>
    public static AtlasPicture? Shown(Sprite sprite, Texture texture) {
        if (Of(texture.name) is not { } page || !page.Pictures.TryGetValue(sprite.name, out var named)) {
            return null;
        }

        return AtlasPicture.Of(sprite, texture, Margin) is { } seen && named.SamePlace(seen) ? named : null;
    }

    /// <summary>
    /// How many reads may come up short, as some pages weren't loaded yet, before what was found counts as all.
    /// </summary>
    private const int ShortReads = 5;

    /// <summary>
    /// How many reads came up short so far.
    /// </summary>
    private static int _shortReads;

    /// <summary>
    /// Reads them from a hero, until they are read. They are the game's own and don't change while it runs. A read
    /// that finds sprites whose page isn't loaded yet, as of an atlas that the game loads late, is tried again later.
    /// </summary>
    public static void Read(HeroController hero) {
        if (_pages != null) {
            return;
        }

        var attacks = hero.transform.Find("Attacks");
        if (attacks == null) {
            Logger.Warn("Could not find the attacks of the hero, whose charged slashes are drawn from atlases");
            _pages = new Dictionary<string, Page>(StringComparer.Ordinal);
            return;
        }

        var sprites = new HashSet<Sprite>();
        foreach (var renderer in attacks.GetComponentsInChildren<SpriteRenderer>(true)) {
            if (renderer.sprite != null) {
                sprites.Add(renderer.sprite);
            }
        }

        var shownAtRest = sprites.Count;
        try {
            SampleClips(attacks, sprites);
        } catch (Exception e) {
            Logger.Warn($"Could not go through the clips of the hero's attacks: {e.Message}");
        }

        // Where the clips gave nothing, every sprite of the kinds shown at rest on their pages: the name without the
        // number at its end
        var sampled = sprites.Count - shownAtRest;
        if (sampled == 0 && sprites.Count > 0) {
            AddKinds(sprites);
        }

        var pages = new Dictionary<string, Page>(StringComparer.Ordinal);
        var twofold = new List<(Page Page, string Name)>();
        int unloaded = 0, turned = 0;
        foreach (var sprite in sprites) {
            var texture = sprite.texture;
            if (texture == null) {
                unloaded++;
                continue;
            }

            var picture = AtlasPicture.Of(sprite, texture, Margin);
            if (picture == null) {
                turned++;
                continue;
            }

            if (!pages.TryGetValue(texture.name, out var page)) {
                pages[texture.name] = page = new Page(texture.name, texture.width, texture.height, texture.isDataSRGB);
            }

            // Two sprites of one name in two places on a page: neither is drawn here, as which is shown can't be told
            if (page.Pictures.TryGetValue(picture.Name, out var named)) {
                if (!named.SamePlace(picture)) {
                    twofold.Add((page, picture.Name));
                }

                continue;
            }

            page.Pictures[picture.Name] = picture;
        }

        if (unloaded > 0 && ++_shortReads < ShortReads) {
            Logger.Info($"{unloaded} sprites of the hero's attacks have no page loaded yet: they are read again later");
            return;
        }

        var count = 0;
        var sizes = new List<string>();
        foreach (var (page, name) in twofold) {
            page.Pictures.Remove(name);
        }

        foreach (var page in pages.Values) {
            page.LayOut(CutWidth);
            count += page.Pictures.Count;
            sizes.Add($"{page.Name} {page.Pictures.Count} in {page.CutSize.x}x{page.CutSize.y}");
        }

        _pages = pages;
        Logger.Info($"Found {count} pictures of the hero's attacks, {sampled} sprites from their clips" +
                    (turned > 0 ? $", and {turned} that can't be cut out, which stay as the game shows them" : "") +
                    (unloaded > 0 ? $", and {unloaded} whose page never loaded" : "") +
                    (twofold.Count > 0 ? $", and {twofold.Count} names on two places of a page" : "") +
                    $": {string.Join(", ", sizes)}");
    }

    /// <summary>
    /// Adds every loaded sprite of the kinds of the given ones on their pages.
    /// </summary>
    private static void AddKinds(HashSet<Sprite> sprites) {
        var kinds = new Dictionary<Texture, HashSet<string>>();
        foreach (var sprite in sprites) {
            var texture = sprite.texture;
            if (texture == null) {
                continue;
            }

            if (!kinds.TryGetValue(texture, out var onPage)) {
                kinds[texture] = onPage = new HashSet<string>(StringComparer.Ordinal);
            }

            onPage.Add(KindOf(sprite.name));
        }

        foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>()) {
            var texture = sprite != null ? sprite.texture : null;
            if (texture != null && kinds.TryGetValue(texture, out var onPage) &&
                onPage.Contains(KindOf(sprite!.name))) {
                sprites.Add(sprite);
            }
        }

        static string KindOf(string name) => name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
    }

    /// <summary>
    /// Adds the sprites that the clips of the animators under the attacks show, sampled at each of their frames on a
    /// copy of the animator's object under a switched off holder, so that nothing on it wakes.
    /// </summary>
    private static void SampleClips(Transform attacks, HashSet<Sprite> sprites) {
        var holder = new GameObject("SSMP attack clips");
        holder.SetActive(false);
        try {
            foreach (var animator in attacks.GetComponentsInChildren<Animator>(true)) {
                var clips = animator.runtimeAnimatorController != null
                    ? animator.runtimeAnimatorController.animationClips
                    : null;
                if (clips == null || clips.Length == 0) {
                    continue;
                }

                var copy = Object.Instantiate(animator.gameObject, holder.transform, false);
                var renderers = copy.GetComponentsInChildren<SpriteRenderer>(true);
                foreach (var clip in clips) {
                    if (clip == null) {
                        continue;
                    }

                    // In the middle of each frame, as a sprite set at a frame's time may come a hair after it, and at
                    // both ends
                    var step = 1f / (clip.frameRate > 0f ? clip.frameRate : 30f);
                    var frames = Mathf.Min(Mathf.CeilToInt(clip.length / step), 600);
                    Sample(0f);
                    for (var frame = 0; frame < frames; frame++) {
                        Sample(Mathf.Min((frame + 0.5f) * step, clip.length));
                    }

                    Sample(clip.length);

                    void Sample(float time) {
                        clip.SampleAnimation(copy, time);
                        foreach (var renderer in renderers) {
                            if (renderer.sprite != null) {
                                sprites.Add(renderer.sprite);
                            }
                        }
                    }
                }

                Object.Destroy(copy);
            }
        } finally {
            Object.Destroy(holder);
        }
    }

    /// <summary>
    /// A page of an atlas: the size of the game's texture of it, and its pictures with where each goes on the texture
    /// that they are cut out onto.
    /// </summary>
    internal sealed class Page {
        /// <summary>
        /// The name of the game's texture of the page.
        /// </summary>
        public readonly string Name;

        /// <summary>
        /// The size of the game's texture of the page, which a skin's image of it has, or CustomizerT2D doesn't take
        /// it.
        /// </summary>
        public readonly Vector2Int GameSize;

        /// <summary>
        /// Whether its colours are kept as sRGB, as an image of it is read and its pictures are cut out.
        /// </summary>
        public readonly bool Srgb;

        /// <summary>
        /// Its pictures, by name.
        /// </summary>
        public readonly Dictionary<string, AtlasPicture> Pictures = new(StringComparer.Ordinal);

        /// <summary>
        /// The size of the texture that its pictures are cut out onto.
        /// </summary>
        public Vector2Int CutSize { get; private set; }

        public Page(string name, int width, int height, bool srgb) {
            Name = name;
            GameSize = new Vector2Int(width, height);
            Srgb = srgb;
        }

        /// <summary>
        /// Places each picture on the texture that they are cut out onto: in rows of the given width at most, the
        /// tallest first.
        /// </summary>
        public void LayOut(int maxWidth) {
            var pictures = new List<AtlasPicture>(Pictures.Values);
            pictures.Sort((a, b) => b.Size.y.CompareTo(a.Size.y));

            int x = 0, y = 0, rowHeight = 0, width = 0;
            foreach (var picture in pictures) {
                if (picture.Size.x > maxWidth) {
                    Pictures.Remove(picture.Name);
                    continue;
                }

                if (x + picture.Size.x > maxWidth) {
                    y += rowHeight;
                    x = 0;
                    rowHeight = 0;
                }

                picture.Place = new Vector2Int(x, y);
                x += picture.Size.x;
                width = Mathf.Max(width, x);
                rowHeight = Mathf.Max(rowHeight, picture.Size.y);
            }

            var height = y + rowHeight;
            if (height > SystemInfo.maxTextureSize) {
                Logger.Warn($"The pictures of the attacks on '{Name}' don't fit on a texture");
                Pictures.Clear();
                width = height = 0;
            }

            CutSize = new Vector2Int(width, height);
        }
    }
}

/// <summary>
/// A picture of the hero's attacks on a page of an atlas: its shape, as the game draws it, and the part of the page
/// around it, which is cut out of an art of the page and drawn with the same shape. The page holds it as it is or
/// mirrored, never turned on its side.
/// </summary>
internal sealed class AtlasPicture {
    /// <summary>
    /// The name of the sprite.
    /// </summary>
    public readonly string Name;

    /// <summary>
    /// The name of the page.
    /// </summary>
    public readonly string Page;

    /// <summary>
    /// The corners of its shape, in pixels of the part cut out from its bottom left, and the triangles between them.
    /// </summary>
    public readonly Vector2[] Corners;
    public readonly ushort[] Triangles;

    /// <summary>
    /// Its centre within the part cut out, from 0 to 1 across it.
    /// </summary>
    public readonly Vector2 Pivot;

    /// <summary>
    /// Its pixels in a unit of the game's world.
    /// </summary>
    public readonly float PixelsPerUnit;

    /// <summary>
    /// The size of the part cut out.
    /// </summary>
    public readonly Vector2Int Size;

    /// <summary>
    /// Where the part cut out sits on the page, as a blit takes it: the place on the page, from 0 to 1 across it, of a
    /// point of the part, from 0 to 1 across it, is that point times the scale plus the offset.
    /// </summary>
    public readonly Vector2 Scale;
    public readonly Vector2 Offset;

    /// <summary>
    /// Where the part cut out goes on the texture that the page's pictures are cut out onto.
    /// </summary>
    public Vector2Int Place;

    /// <summary>
    /// The game's own sprite of it, once seen showing, which a renderer shows again where a player's attacks are drawn
    /// as the game draws them.
    /// </summary>
    public Sprite? Game;

    private AtlasPicture(
        string name,
        string page,
        Vector2[] corners,
        ushort[] triangles,
        Vector2 pivot,
        float pixelsPerUnit,
        Vector2Int size,
        Vector2 scale,
        Vector2 offset
    ) {
        Name = name;
        Page = page;
        Corners = corners;
        Triangles = triangles;
        Pivot = pivot;
        PixelsPerUnit = pixelsPerUnit;
        Size = size;
        Scale = scale;
        Offset = offset;
    }

    /// <summary>
    /// The picture that a sprite on a page shows, with the given margin around its shape, or null for one whose shape
    /// can't be read or that the page holds turned on its side.
    /// </summary>
    public static AtlasPicture? Of(Sprite sprite, Texture page, int margin) {
        var vertices = sprite.vertices;
        var uvs = sprite.uv;
        var triangles = sprite.triangles;
        var unit = sprite.pixelsPerUnit;
        if (vertices == null || uvs == null || triangles == null || vertices.Length < 3 ||
            uvs.Length != vertices.Length || triangles.Length < 3 || unit <= 0f) {
            return null;
        }

        // The corners in the picture's own pixels, from the bottom left of its rectangle
        var pivot = sprite.pivot;
        var points = new Vector2[vertices.Length];
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        for (var i = 0; i < vertices.Length; i++) {
            points[i] = vertices[i] * unit + pivot;
            min = Vector2.Min(min, points[i]);
            max = Vector2.Max(max, points[i]);
        }

        // Where those sit on the page, u = A·p + c, from the triangle that spans the most
        int first = -1, second = -1, third = -1;
        var widest = 1e-3f;
        for (var t = 0; t + 2 < triangles.Length; t += 3) {
            int i = triangles[t], j = triangles[t + 1], k = triangles[t + 2];
            if (i >= points.Length || j >= points.Length || k >= points.Length) {
                return null;
            }

            var p1 = points[j] - points[i];
            var p2 = points[k] - points[i];
            var area = Mathf.Abs(p1.x * p2.y - p2.x * p1.y);
            if (area > widest) {
                widest = area;
                (first, second, third) = (i, j, k);
            }
        }

        if (first < 0) {
            return null;
        }

        var e1 = points[second] - points[first];
        var e2 = points[third] - points[first];
        var determinant = e1.x * e2.y - e2.x * e1.y;
        var u1 = uvs[second] - uvs[first];
        var u2 = uvs[third] - uvs[first];
        var a00 = (u1.x * e2.y - u2.x * e1.y) / determinant;
        var a01 = (u2.x * e1.x - u1.x * e2.x) / determinant;
        var a10 = (u1.y * e2.y - u2.y * e1.y) / determinant;
        var a11 = (u2.y * e1.x - u1.y * e2.x) / determinant;

        var from = new Vector2Int(Mathf.FloorToInt(min.x) - margin, Mathf.FloorToInt(min.y) - margin);
        var size = new Vector2Int(
            Mathf.CeilToInt(max.x) + margin - from.x,
            Mathf.CeilToInt(max.y) + margin - from.y
        );

        // Turned on its side, which a blit can't take: across the whole picture, less than a tenth of a pixel of the
        // page off its rows and columns
        if (Mathf.Abs(a01 * page.width) * size.y > 0.1f || Mathf.Abs(a10 * page.height) * size.x > 0.1f ||
            Mathf.Abs(a00 * page.width) < 0.5f || Mathf.Abs(a11 * page.height) < 0.5f) {
            return null;
        }

        // Every corner where the page has it, to a tenth of a pixel
        var c = uvs[first] - new Vector2(a00 * points[first].x, a11 * points[first].y);
        for (var i = 0; i < points.Length; i++) {
            if (Mathf.Abs((a00 * points[i].x + c.x - uvs[i].x) * page.width) > 0.1f ||
                Mathf.Abs((a11 * points[i].y + c.y - uvs[i].y) * page.height) > 0.1f) {
                return null;
            }
        }

        var corners = new Vector2[points.Length];
        for (var i = 0; i < points.Length; i++) {
            corners[i] = points[i] - from;
        }

        return new AtlasPicture(
            sprite.name,
            page.name,
            corners,
            (ushort[]) triangles.Clone(),
            new Vector2((pivot.x - from.x) / size.x, (pivot.y - from.y) / size.y),
            unit,
            size,
            new Vector2(a00 * size.x, a11 * size.y),
            new Vector2(a00 * from.x + c.x, a11 * from.y + c.y)
        );
    }

    /// <summary>
    /// Whether another picture is the same part of the page, as one sprite in two bundles is.
    /// </summary>
    public bool SamePlace(AtlasPicture other) {
        return Size == other.Size && Mathf.Abs(Scale.x - other.Scale.x) < 1e-6f &&
               Mathf.Abs(Scale.y - other.Scale.y) < 1e-6f && Mathf.Abs(Offset.x - other.Offset.x) < 1e-6f &&
               Mathf.Abs(Offset.y - other.Offset.y) < 1e-6f;
    }
}

/// <summary>
/// The pictures of the hero's attacks on one page, cut out of one art of it, the game's own or a skin's image of it,
/// onto a texture of their own on the graphics card, with a sprite for each that draws it as the game's sprite does.
/// A skin's images are read one at a time, off the main thread.
/// </summary>
internal sealed class AtlasPageArt {
    /// <summary>
    /// The arts waiting for their image to be read, and the one being read.
    /// </summary>
    private static readonly Queue<AtlasPageArt> Waiting = new();
    private static AtlasPageArt? _readingArt;

    /// <summary>
    /// The name of the game's texture of the page.
    /// </summary>
    public readonly string Page;

    /// <summary>
    /// What the art is of, as it is logged.
    /// </summary>
    private readonly string _of;

    /// <summary>
    /// The skin's image, or null for the game's own art.
    /// </summary>
    private readonly string? _file;

    /// <summary>
    /// The sprites, by the name of the picture, once the art is in: none if it couldn't be read.
    /// </summary>
    public Dictionary<string, Sprite>? Sprites { get; private set; }

    /// <summary>
    /// The texture the pictures are cut out onto.
    /// </summary>
    private Texture2D? _texture;

    /// <summary>
    /// The image being read, or null for none.
    /// </summary>
    private UnityWebRequest? _request;

    /// <summary>
    /// Whether it was let go, after which nothing that finishes reading is kept.
    /// </summary>
    private bool _destroyed;

    private AtlasPageArt(string page, string of, string? file) {
        Page = page;
        _of = of;
        _file = file;
    }

    /// <summary>
    /// The pictures cut out of the game's own art of a page, from a copy of it that was kept before CustomizerT2D
    /// dressed it, or null if they couldn't be.
    /// </summary>
    public static AtlasPageArt? OfGame(string page, Texture2D kept) {
        var art = new AtlasPageArt(page, "the game's own art", null);
        if (!art.Cut(kept)) {
            art.Destroy();
            return null;
        }

        return art;
    }

    /// <summary>
    /// The pictures cut out of a skin's image of a page, read in the background: the sprites are there once it is in.
    /// </summary>
    public static AtlasPageArt OfSkin(string page, string skin, string file) {
        var art = new AtlasPageArt(page, $"the image of the skin '{skin}'", file);
        Waiting.Enqueue(art);
        ReadNext();
        return art;
    }

    /// <summary>
    /// Starts reading the next image waiting, unless one is being read.
    /// </summary>
    private static void ReadNext() {
        while (_readingArt == null && Waiting.Count > 0) {
            var art = Waiting.Dequeue();
            if (!art._destroyed && art.StartReading()) {
                _readingArt = art;
            }
        }
    }

    /// <summary>
    /// Starts reading the skin's image, and says whether it is being read.
    /// </summary>
    private bool StartReading() {
        try {
            var parameters = DownloadedTextureParams.Default;
            parameters.mipmapChain = false;
            parameters.readable = false;
            parameters.linearColorSpace = !(AtlasSlashPictures.Of(Page)?.Srgb ?? true);
            var request = UnityWebRequestTexture.GetTexture(new Uri(_file!).AbsoluteUri, parameters);
            _request = request;
            request.SendWebRequest().completed += _ => OnRead(request);
            return true;
        } catch (Exception e) {
            Logger.Warn($"Could not start reading '{_file}': {e.Message}");
            Sprites = new Dictionary<string, Sprite>();
            return false;
        }
    }

    /// <summary>
    /// Cuts the pictures out of the skin's image as it is read, and reads the next one.
    /// </summary>
    private void OnRead(UnityWebRequest request) {
        if (_readingArt == this) {
            _readingArt = null;
        }

        Texture2D? image = null;
        try {
            if (_request == request && request.result == UnityWebRequest.Result.Success) {
                image = DownloadHandlerTexture.GetContent(request);
            } else if (_request == request && !_destroyed) {
                Logger.Warn($"Could not read '{_file}': {request.error}");
            }
        } catch (Exception e) {
            Logger.Warn($"Could not read '{_file}': {e.Message}");
        } finally {
            request.Dispose();
        }

        if (_request == request) {
            _request = null;
        }

        try {
            if (image != null && !_destroyed) {
                image.filterMode = FilterMode.Point;
                image.wrapMode = TextureWrapMode.Clamp;
                Cut(image);
            }
        } finally {
            if (image != null) {
                Object.Destroy(image);
            }
        }

        // An image that couldn't be read draws as the game shows the page
        Sprites ??= new Dictionary<string, Sprite>();
        ReadNext();
    }

    /// <summary>
    /// Cuts the page's pictures out of an art of it, as big as the game's texture of the page, onto a texture of their
    /// own on the graphics card, and makes their sprites. Says whether it did.
    /// </summary>
    private bool Cut(Texture source) {
        var page = AtlasSlashPictures.Of(Page);
        if (page == null || page.Pictures.Count == 0 || page.CutSize.x == 0 || page.CutSize.y == 0) {
            Sprites = new Dictionary<string, Sprite>();
            return false;
        }

        var active = RenderTexture.active;
        var sprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        try {
            // The colours kept as they are, sRGB or not
            var format = page.Srgb && QualitySettings.activeColorSpace == ColorSpace.Linear
                ? GraphicsFormat.R8G8B8A8_SRGB
                : GraphicsFormat.R8G8B8A8_UNorm;
            var texture = new Texture2D(
                page.CutSize.x,
                page.CutSize.y,
                format,
                TextureCreationFlags.DontInitializePixels
            ) {
                name = $"{PackArt.NamePrefix}attacks from {_of} of {Page}",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.Apply(false, true);
            _texture = texture;

            // Each picture is blitted to the bottom left of a texture as big as the biggest, and copied from there
            var most = Vector2Int.zero;
            foreach (var picture in page.Pictures.Values) {
                most = Vector2Int.Max(most, picture.Size);
            }

            var part = RenderTexture.GetTemporary(new RenderTextureDescriptor(most.x, most.y, format, 0));
            try {
                foreach (var picture in page.Pictures.Values) {
                    var scale = new Vector2(
                        picture.Scale.x * most.x / picture.Size.x,
                        picture.Scale.y * most.y / picture.Size.y
                    );
                    Graphics.Blit(source, part, scale, picture.Offset);
                    Graphics.CopyTexture(
                        part, 0, 0, 0, 0, picture.Size.x, picture.Size.y,
                        texture, 0, 0, picture.Place.x, picture.Place.y
                    );
                }
            } finally {
                RenderTexture.ReleaseTemporary(part);
            }

            foreach (var picture in page.Pictures.Values) {
                var sprite = Sprite.Create(
                    texture,
                    new Rect(picture.Place.x, picture.Place.y, picture.Size.x, picture.Size.y),
                    picture.Pivot,
                    picture.PixelsPerUnit,
                    0,
                    SpriteMeshType.FullRect,
                    Vector4.zero,
                    false
                );
                sprites[picture.Name] = sprite;
                sprite.OverrideGeometry(picture.Corners, picture.Triangles);
                sprite.name = picture.Name;
            }

            Sprites = sprites;
            Logger.Info($"Cut {sprites.Count} pictures of the attacks out of {_of} of '{Page}'");
            return true;
        } catch (Exception e) {
            Logger.Warn($"Could not cut the pictures of the attacks out of {_of} of '{Page}': {e.Message}");
            foreach (var sprite in sprites.Values) {
                Object.Destroy(sprite);
            }

            Sprites = new Dictionary<string, Sprite>();
            if (_texture != null) {
                Object.Destroy(_texture);
                _texture = null;
            }

            return false;
        } finally {
            // A blit leaves its target active
            RenderTexture.active = active;
        }
    }

    /// <summary>
    /// Lets go of the art: stops reading its image and frees its sprites and texture.
    /// </summary>
    public void Destroy() {
        _destroyed = true;
        if (_request != null) {
            var request = _request;
            _request = null;
            request.Abort();
        }

        if (Sprites != null) {
            foreach (var sprite in Sprites.Values) {
                if (sprite != null) {
                    Object.Destroy(sprite);
                }
            }
        }

        if (_texture != null) {
            Object.Destroy(_texture);
            _texture = null;
        }
    }
}

/// <summary>
/// Draws the pictures of the hero's attacks that come from atlases (see <see cref="AtlasSlashPictures"/>) as each
/// player chose their slashes, on every screen: the local hero's as the local player chose, and each copy that shows an
/// attack of another player's character (see HeroCopies) as that player chose, from the look their game sent. As the
/// slashes of the sheets are (see SlashSkin): from the skin chosen for the slashes, from the game's own art, or from
/// the skins worn; a page that the skin has no image of, or none of the page's size, which CustomizerT2D wouldn't
/// take, is drawn in the game's own art. Each renderer that shows such a picture is given the sprite of that picture
/// in that art after its clip has set the game's own, at every frame; while that art is being read, it shows what the
/// game shows.
/// </summary>
internal static class AtlasSlashes {
    /// <summary>
    /// What the player's own hero wears, as the text that tells the other games (see SkinManager).
    /// </summary>
    public static Func<string>? LocalLookText;

    /// <summary>
    /// What a character of another player wears, as the text that their game sent, or an empty text for the game's own
    /// look, as where the server lets nobody wear skins (see SkinManager).
    /// </summary>
    public static Func<GameObject, string>? LookOfCharacter;

    /// <summary>
    /// What the other players wear, each as the text that their game sent (see SkinManager).
    /// </summary>
    public static Func<List<string>>? LooksOfOthers;

    /// <summary>
    /// The renderers that may show a picture of the attacks: the local hero's, or those of a copy that shows an attack
    /// of a character of another player.
    /// </summary>
    private static readonly List<Shown> Renderers = [];

    /// <summary>
    /// The picture that each sprite seen on those renderers shows, or null for a sprite that isn't one.
    /// </summary>
    private static readonly Dictionary<Sprite, AtlasPicture?> PictureOf = new();

    /// <summary>
    /// The art cut out of the game's own art of each page that CustomizerT2D dressed, by page, kept until the game
    /// quits, as the game's own art is gone; and out of skins' images, by image.
    /// </summary>
    private static readonly Dictionary<string, AtlasPageArt> GameArts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, AtlasPageArt> SkinArts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The art that each page of the local hero's attacks, and of each character's, is drawn with, by page, or null
    /// for what the game shows: worked out the first time it is needed after something changed.
    /// </summary>
    private static Dictionary<string, AtlasPageArt?>? _local;
    private static readonly Dictionary<GameObject, Dictionary<string, AtlasPageArt?>> OfCharacter = new();

    /// <summary>
    /// The local hero whose renderers were taken in, and the renderers found on an object, kept from one to the next.
    /// </summary>
    private static HeroController? _hero;
    private static readonly List<SpriteRenderer> Found = [];

    /// <summary>
    /// The pictures of the attacks seen on a page of the attacks that weren't found as the hero was read, logged once.
    /// </summary>
    private static readonly HashSet<string> Unknown = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether this was started, and whether to work out the arts again at the next frame, as the game's own art of a
    /// page was kept.
    /// </summary>
    private static bool _started;
    private static bool _refreshSoon;

    /// <summary>
    /// How long after a read that came up short the pictures are read again from the hero, in seconds, and when.
    /// </summary>
    private const float ReadAgainAfter = 2f;
    private static float _readAgainAt;

    /// <summary>
    /// Starts drawing the pictures as chosen: at every frame, and again as anything chosen changes. Called once
    /// MonoBehaviourUtil is there.
    /// </summary>
    public static void Start() {
        if (_started || MonoBehaviourUtil.Instance == null) {
            return;
        }

        _started = true;
        MonoBehaviourUtil.Instance.OnLateUpdateEvent += OnLateUpdate;
        SlashSkin.Changed += Refresh;
        OwnSkin.Changed += Refresh;
        CustomizerT2D.PageKept += () => _refreshSoon = true;

        // Called at once while there is a hero
        CustomHooks.HeroControllerStartAction += OnHeroStart;
    }

    /// <summary>
    /// Whether the game's own art of the given page was cut out already, so that it needn't be kept again.
    /// </summary>
    public static bool HasGameArt(string page) => GameArts.ContainsKey(page);

    /// <summary>
    /// Whether a skin has an image of a page of the attacks that would be drawn: so that it counts among the skins
    /// that its player wears even where it dresses none of the hero's sheets.
    /// </summary>
    public static bool DrawsAttacks(SkinPack skin) {
        var images = SkinImages.Of(skin.Dir);
        if (images.Count == 0) {
            return false;
        }

        if (AtlasSlashPictures.IsRead) {
            foreach (var page in AtlasSlashPictures.Pages) {
                if (ImageOf(skin, page) != null) {
                    return true;
                }
            }

            return false;
        }

        foreach (var name in images.Keys) {
            if (CustomizerT2D.AttackAtlases.Contains(CustomizerT2D.AtlasOf(name))) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Takes in the renderers of a copy, still switched off, that shows an attack of the given character of another
    /// player (see HeroCopies).
    /// </summary>
    public static void Track(GameObject copy, GameObject character) {
        copy.GetComponentsInChildren(true, Found);
        foreach (var renderer in Found) {
            Renderers.Add(new Shown(renderer, character));
        }

        Found.Clear();
    }

    /// <summary>
    /// Takes in the renderers of the attacks of a local hero that starts, and reads the pictures from it if they
    /// weren't read.
    /// </summary>
    private static void OnHeroStart() {
        var hero = HeroController.SilentInstance;
        if (hero == null || hero == _hero) {
            return;
        }

        _hero = hero;
        var attacks = hero.transform.Find("Attacks");
        if (attacks != null) {
            attacks.GetComponentsInChildren(true, Found);
            foreach (var renderer in Found) {
                Renderers.Add(new Shown(renderer, null));
            }

            Found.Clear();
        }

        ReadPictures(hero);
    }

    /// <summary>
    /// Reads the pictures from a hero if they aren't read, and works out the arts once they are.
    /// </summary>
    private static void ReadPictures(HeroController hero) {
        if (AtlasSlashPictures.IsRead) {
            Refresh();
            return;
        }

        try {
            AtlasSlashPictures.Read(hero);
        } catch (Exception e) {
            Logger.Warn($"Could not find the pictures of the hero's attacks on atlases: {e.Message}");
        }

        _readAgainAt = Time.unscaledTime + ReadAgainAfter;
        if (AtlasSlashPictures.IsRead) {
            Refresh();
        }
    }

    /// <summary>
    /// Works out again what each player's pictures are drawn with: cuts the game's own art of the pages that were
    /// kept, starts reading the images wanted now, and lets go of those that nobody's are drawn with. Called as the
    /// local player chooses, as a hero comes, as the looks of other players come or change, and as the game's own art
    /// of a page is kept.
    /// </summary>
    public static void Refresh() {
        try {
            _local = null;
            OfCharacter.Clear();
            if (!AtlasSlashPictures.IsRead) {
                return;
            }

            // The game's own art of a page with pictures is cut out once, and no copy is kept longer
            foreach (var page in CustomizerT2D.KeptPages()) {
                var kept = CustomizerT2D.TakeKept(page);
                if (kept == null) {
                    continue;
                }

                if (!GameArts.ContainsKey(page) && AtlasSlashPictures.Of(page) != null &&
                    AtlasPageArt.OfGame(page, kept) is { } art) {
                    GameArts[page] = art;
                }

                Object.Destroy(kept);
            }

            var looks = new List<OwnerLook> { LocalLook() };
            if (LooksOfOthers?.Invoke() is { } others) {
                foreach (var text in others) {
                    looks.Add(OwnerLook.Of(text));
                }
            }

            var wanted = new HashSet<AtlasPageArt>();
            foreach (var look in looks) {
                foreach (var page in AtlasSlashPictures.Pages) {
                    if (ArtFor(look, page) is { } art) {
                        wanted.Add(art);
                    }
                }
            }

            foreach (var image in new List<string>(SkinArts.Keys)) {
                var art = SkinArts[image];
                if (!wanted.Contains(art)) {
                    Forget(art);
                    SkinArts.Remove(image);
                }
            }
        } catch (Exception e) {
            Logger.Error($"Could not work out what the attacks of each player are drawn with: {e}");
        }
    }

    /// <summary>
    /// Lets go of an art and of what is known of its sprites: a renderer that shows one of them shows the game's own
    /// sprite of its picture again.
    /// </summary>
    private static void Forget(AtlasPageArt art) {
        if (art.Sprites != null) {
            foreach (var shown in Renderers) {
                var renderer = shown.Renderer;
                if (renderer != null && renderer.sprite is { } sprite &&
                    PictureOf.TryGetValue(sprite, out var picture) && picture != null &&
                    art.Sprites.TryGetValue(picture.Name, out var made) && made == sprite) {
                    renderer.sprite = picture.Game;
                }
            }

            foreach (var sprite in art.Sprites.Values) {
                PictureOf.Remove(sprite);
            }
        }

        art.Destroy();
    }

    /// <summary>
    /// What the local hero's attacks are drawn from: as the other games are told.
    /// </summary>
    private static OwnerLook LocalLook() => OwnerLook.Of(LocalLookText?.Invoke() ?? "");

    /// <summary>
    /// The art that a page of the attacks of the given look is drawn with, started if it isn't there, or null for what
    /// the game shows: its own art, or the image that CustomizerT2D dressed the page with, as wanted.
    /// </summary>
    private static AtlasPageArt? ArtFor(OwnerLook look, string page) {
        var image = look.Game ? null
            : look.Slashes != null ? ImageOf(look.Slashes, page)
            : LastImageOf(look.Worn, page);

        if (image != null) {
            if (string.Equals(image, CustomizerT2D.ImageOf(page), StringComparison.OrdinalIgnoreCase)) {
                return null;
            }

            if (!SkinArts.TryGetValue(image, out var art)) {
                var skin = look.Slashes ?? look.Worn.Find(worn => ImageOf(worn, page) == image);
                art = AtlasPageArt.OfSkin(page, skin?.Name ?? image, image);
                SkinArts[image] = art;
            }

            return art;
        }

        // The game's own art, which the page shows unless CustomizerT2D dressed it
        return CustomizerT2D.HasDressed(page) && GameArts.TryGetValue(page, out var own) ? own : null;
    }

    /// <summary>
    /// The last of the given skins that has an image of the page, as CustomizerT2D takes the image of the last skin it
    /// reads that has one.
    /// </summary>
    private static string? LastImageOf(List<SkinPack> skins, string page) {
        for (var i = skins.Count - 1; i >= 0; i--) {
            if (ImageOf(skins[i], page) is { } image) {
                return image;
            }
        }

        return null;
    }

    /// <summary>
    /// A skin's image of a page, as CustomizerT2D would dress the page with it: of the same name, and of the size of
    /// the game's texture of the page; or null if it has none.
    /// </summary>
    private static string? ImageOf(SkinPack skin, string page) {
        if (!SkinImages.Of(skin.Dir).TryGetValue(CustomizerT2D.Normalize(page), out var image)) {
            return null;
        }

        return AtlasSlashPictures.Of(page) is not { } pictures || SkinImages.SizeOf(image) == pictures.GameSize
            ? image
            : null;
    }

    /// <summary>
    /// Gives each renderer that shows a picture of the attacks the sprite of that picture in its player's art, after
    /// the clips have set the game's own for this frame.
    /// </summary>
    private static void OnLateUpdate() {
        if (_refreshSoon) {
            _refreshSoon = false;
            Refresh();
        }

        if (!AtlasSlashPictures.IsRead) {
            // Read again a little later where some pages weren't loaded yet
            if (_hero != null && Time.unscaledTime >= _readAgainAt) {
                ReadPictures(_hero);
            }

            return;
        }

        for (var i = Renderers.Count - 1; i >= 0; i--) {
            var shown = Renderers[i];
            var renderer = shown.Renderer;
            if (renderer == null || shown.IsCopy && shown.Character == null) {
                Renderers.RemoveAt(i);
                continue;
            }

            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) {
                continue;
            }

            var sprite = renderer.sprite;
            if (sprite == null || PictureShownBy(sprite) is not { } picture) {
                continue;
            }

            var arts = shown.IsCopy ? CharacterArts(shown.Character!) : LocalArts();
            Sprite? wanted = null;
            if (arts.TryGetValue(picture.Page, out var art) && art?.Sprites != null) {
                art.Sprites.TryGetValue(picture.Name, out wanted);
            }

            wanted ??= picture.Game;
            if (wanted != null && wanted != sprite) {
                renderer.sprite = wanted;
            }
        }
    }

    /// <summary>
    /// The picture that a sprite shows, looked up the first time it is seen: a sprite of the game's own, or one made
    /// here.
    /// </summary>
    private static AtlasPicture? PictureShownBy(Sprite sprite) {
        if (PictureOf.TryGetValue(sprite, out var picture)) {
            return picture;
        }

        picture = null;
        var texture = sprite.texture;
        if (texture != null && AtlasSlashPictures.Of(texture.name) != null) {
            picture = AtlasSlashPictures.Shown(sprite, texture);
            if (picture != null) {
                picture.Game = sprite;
            } else if (Unknown.Add(sprite.name) && Unknown.Count <= 20) {
                Logger.Info($"The attacks show '{sprite.name}' on '{texture.name}', which wasn't found as the hero " +
                            "was read: it shows as the game shows it");
            }
        } else if (texture != null && texture.name.StartsWith(PackArt.NamePrefix, StringComparison.Ordinal)) {
            picture = MadeFor(sprite);
        }

        PictureOf[sprite] = picture;
        return picture;
    }

    /// <summary>
    /// The picture that a sprite made here shows, or null for none.
    /// </summary>
    private static AtlasPicture? MadeFor(Sprite sprite) {
        foreach (var art in SkinArts.Values) {
            if (Find(art) is { } picture) {
                return picture;
            }
        }

        foreach (var art in GameArts.Values) {
            if (Find(art) is { } picture) {
                return picture;
            }
        }

        return null;

        AtlasPicture? Find(AtlasPageArt art) {
            if (art.Sprites == null || !art.Sprites.TryGetValue(sprite.name, out var made) || made != sprite) {
                return null;
            }

            return AtlasSlashPictures.Of(art.Page) is { } page && page.Pictures.TryGetValue(sprite.name, out var found)
                ? found
                : null;
        }
    }

    /// <summary>
    /// The art of each page of the local hero's attacks.
    /// </summary>
    private static Dictionary<string, AtlasPageArt?> LocalArts() => _local ??= ArtsOf(LocalLook());

    /// <summary>
    /// The art of each page of the attacks of a character of another player.
    /// </summary>
    private static Dictionary<string, AtlasPageArt?> CharacterArts(GameObject character) {
        if (!OfCharacter.TryGetValue(character, out var arts)) {
            arts = ArtsOf(OwnerLook.Of(LookOfCharacter?.Invoke(character) ?? ""));
            OfCharacter[character] = arts;
        }

        return arts;
    }

    /// <summary>
    /// The art of each page for a look.
    /// </summary>
    private static Dictionary<string, AtlasPageArt?> ArtsOf(OwnerLook look) {
        var arts = new Dictionary<string, AtlasPageArt?>(StringComparer.Ordinal);
        foreach (var page in AtlasSlashPictures.Pages) {
            arts[page] = ArtFor(look, page);
        }

        return arts;
    }

    /// <summary>
    /// A renderer that may show a picture of the attacks: the local hero's, or one of a copy that shows an attack of a
    /// character of another player.
    /// </summary>
    private readonly record struct Shown(SpriteRenderer Renderer, GameObject? Character) {
        /// <summary>
        /// Whether it is one of a copy that shows an attack of a character: the character may be gone.
        /// </summary>
        public bool IsCopy { get; } = Character is not null;
    }

    /// <summary>
    /// What a player's attacks are drawn from: the skin chosen for the slashes, the game's own art, or the skins worn,
    /// in their order.
    /// </summary>
    private sealed record OwnerLook(SkinPack? Slashes, bool Game, List<SkinPack> Worn) {
        /// <summary>
        /// The look that a player's game sent, as the skins of this computer show it: a skin that isn't here counts as
        /// not worn, and slashes chosen from one that isn't here are drawn as the rest, as their sheets are.
        /// </summary>
        public static OwnerLook Of(string text) {
            var look = SkinLook.Parse(text);
            if (look.Slash is { Pack: null }) {
                return new OwnerLook(null, true, []);
            }

            var packs = look.Slash != null || look.Packs.Count > 0 ? CustomizerPacks.FindAll() : [];
            if (look.Slash?.Pack is { } chosen && CustomizerPacks.Find(packs, chosen) is { } slashes) {
                return new OwnerLook(slashes, false, []);
            }

            var worn = new List<SkinPack>();
            foreach (var wanted in look.Packs) {
                if (CustomizerPacks.Find(packs, wanted) is { } pack) {
                    worn.Add(pack);
                }
            }

            return new OwnerLook(null, false, worn);
        }
    }
}
