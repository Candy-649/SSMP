using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Game.Client.Skin;

/// <summary>
/// The skin of Silksong Customizer that the player's own Hornet wears, chosen on the settings page of the multiplayer
/// menu.
///
/// Customizer dresses the game with every skin that holds an active.txt, each atlas in the first of them that has art
/// for it, as each room loads, and an atlas once only: a skin changed by hand showed only after a restart, and two
/// skins switched on together showed the one that came first. A choice here leaves an active.txt in the chosen skin
/// alone of the skins of Hornet, so that Customizer dresses the game with it from then on, also after a restart, and
/// dresses what the game has loaded already at once, as Customizer would after a restart.
/// </summary>
internal static class OwnSkin {
    /// <summary>
    /// The sheet of sprites of Hornet herself: a skin with art for it is a skin of hers.
    /// </summary>
    private static readonly HashSet<string> HornetSheet = new(StringComparer.Ordinal) { "Knight" };

    /// <summary>
    /// The file that makes Customizer dress the game with a skin.
    /// </summary>
    private const string ActiveFile = "active.txt";

    /// <summary>
    /// The name that the file of a skin switched off here is kept under, to be put back when it is chosen again.
    /// </summary>
    private const string SwitchedOffFile = "active.txt.off";

    /// <summary>
    /// The art of each skin loaded here, by the folder of the skin.
    /// </summary>
    private static readonly Dictionary<string, PackArt> Art = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The art that the loaded sheets were dressed in at some time: the game may still draw with it, so it is never
    /// destroyed here, only let go of, and the game frees it once nothing draws with it.
    /// </summary>
    private static readonly HashSet<PackArt> Worn = [];

    /// <summary>
    /// The art of the skins that the loaded sheets wear now, in Customizer's order.
    /// </summary>
    private static List<PackArt> _wearing = [];

    /// <summary>
    /// What the loaded sheets are to wear once its art is in, or null when nothing waits.
    /// </summary>
    private static Dressing? _waiting;

    /// <summary>
    /// Raised when the chosen skin changed, and again when the game shows it.
    /// </summary>
    public static event Action? Changed;

    /// <summary>
    /// Whether a skin can be chosen: Customizer is there to dress the game with it.
    /// </summary>
    public static bool CanChoose => CustomizerPacks.IsLoaded;

    /// <summary>
    /// Whether the game waits for the art of a skin to show it.
    /// </summary>
    public static bool IsDressing => _waiting != null;

    /// <summary>
    /// The skins of Hornet on this computer, in Customizer's order.
    /// </summary>
    public static List<SkinPack> Choices() {
        var choices = new List<SkinPack>();
        foreach (var pack in CustomizerPacks.FindAll()) {
            if (pack.Dresses(HornetSheet)) {
                choices.Add(pack);
            }
        }

        return choices;
    }

    /// <summary>
    /// The skin that Hornet wears out of the given ones: the first that holds an active.txt, or null for the game's
    /// own look.
    /// </summary>
    public static SkinPack? Current(List<SkinPack> choices) {
        foreach (var pack in choices) {
            if (pack.IsActive) {
                return pack;
            }
        }

        return null;
    }

    /// <summary>
    /// How many of the given skins hold an active.txt.
    /// </summary>
    public static int SwitchedOn(List<SkinPack> choices) {
        var count = 0;
        foreach (var pack in choices) {
            if (pack.IsActive) {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Puts Hornet in the given skin, or in the game's own look for null: the chosen skin alone of hers keeps an
    /// active.txt, and what the game has loaded is dressed in it.
    /// </summary>
    public static void Choose(SkinPack? chosen) {
        foreach (var pack in Choices()) {
            var wear = chosen != null && string.Equals(pack.Dir, chosen.Dir, StringComparison.OrdinalIgnoreCase);
            var active = Path.Combine(pack.Dir, ActiveFile);
            var off = Path.Combine(pack.Dir, SwitchedOffFile);
            try {
                if (wear && !File.Exists(active)) {
                    if (File.Exists(off)) {
                        File.Move(off, active);
                    } else {
                        File.WriteAllText(active, "");
                    }
                } else if (!wear && File.Exists(active)) {
                    // One switched off here before gives way to the newer one, which was put back by hand since
                    if (File.Exists(off)) {
                        File.Delete(off);
                    }

                    File.Move(active, off);
                }
            } catch (Exception e) {
                Logger.Warn($"Could not switch the skin '{pack.Name}' {(wear ? "on" : "off")}: {e.Message}");
            }
        }

        Logger.Info($"Chose to wear {(chosen == null ? "the game's own look" : $"the skin '{chosen.Name}'")}");
        DressLoaded();
        Changed?.Invoke();
    }

    /// <summary>
    /// Dresses the sheets of sprites that the game has loaded as Customizer would after a restart, with the skins that
    /// hold an active.txt now: each atlas that some skin has art for in the art of the first of them that has it, or
    /// in the game's own. The art loads off the main thread, and the sheets change all at once when it is in.
    /// </summary>
    public static void DressLoaded() {
        if (!CustomizerPacks.IsLoaded) {
            return;
        }

        var packs = CustomizerPacks.FindAll();
        var sheetsOfSkins = new HashSet<string>(StringComparer.Ordinal);
        var active = new List<SkinPack>();
        foreach (var pack in packs) {
            sheetsOfSkins.UnionWith(pack.SheetNames());
            if (pack.IsActive) {
                active.Add(pack);
            }
        }

        // The copies that other players' characters draw from have names of their own, which no skin has a folder for
        var atlases = new List<LoadedAtlas>();
        foreach (var sheet in Resources.FindObjectsOfTypeAll<tk2dSpriteCollectionData>()) {
            if (sheet == null || sheet.materials == null || !sheetsOfSkins.Contains(sheet.name)) {
                continue;
            }

            for (var i = 0; i < sheet.materials.Length; i++) {
                var own = SkinManager.OwnTexture(sheet, i);
                if (own != null && own.name.Length > 0 && sheet.materials[i] != null) {
                    atlases.Add(new LoadedAtlas(sheet, i, own));
                }
            }
        }

        var arts = new List<PackArt>();
        foreach (var pack in active) {
            if (!Art.TryGetValue(pack.Dir, out var art)) {
                art = new PackArt(pack, _ => TryDress());
                Art[pack.Dir] = art;
            }

            arts.Add(art);
        }

        _waiting = new Dressing(atlases, arts);
        LetGoOfUnused();
        TryDress();
    }

    /// <summary>
    /// Lets go of the art loaded here once the player plays: Customizer dresses each room as it loads from then on,
    /// with art of its own, and the game frees what the sheets wore in the menu once nothing draws with it.
    /// </summary>
    public static void LetGo() {
        _waiting = null;
        _wearing = [];
        LetGoOfUnused();
    }

    /// <summary>
    /// Dresses the loaded sheets in what waits, if all its art is in.
    /// </summary>
    private static void TryDress() {
        var waiting = _waiting;
        if (waiting == null) {
            return;
        }

        var ready = true;
        foreach (var art in waiting.Arts) {
            ready &= art.Load(waiting.Wanted);
        }

        if (!ready) {
            return;
        }

        _waiting = null;
        foreach (var atlas in waiting.Atlases) {
            // A sheet that the game let go of while the art loaded is gone
            if (atlas.Sheet == null || atlas.Own == null) {
                continue;
            }

            Texture? art = null;
            foreach (var pack in waiting.Arts) {
                art = pack.Get(atlas.SheetName, atlas.TextureName);
                if (art != null) {
                    break;
                }
            }

            var texture = art != null ? art : atlas.Own;
            var materials = atlas.Sheet.materials;
            if (materials != null && atlas.Index < materials.Length && materials[atlas.Index] != null) {
                materials[atlas.Index].mainTexture = texture;
            }

            var instances = atlas.Sheet.materialInsts;
            if (instances != null && atlas.Index < instances.Length && instances[atlas.Index] != null) {
                instances[atlas.Index].mainTexture = texture;
            }
        }

        _wearing = waiting.Arts;
        Worn.UnionWith(_wearing);
        LetGoOfUnused();

        // An object that wore a skin, which Player Color has no mask for, may wear the game's own art now
        PlayerColorMod.LookAgain();
        Changed?.Invoke();
    }

    /// <summary>
    /// Lets go of the art of skins that the loaded sheets neither wear nor wait for: destroyed if it never dressed
    /// anything, else left for the game to free once nothing draws with it.
    /// </summary>
    private static void LetGoOfUnused() {
        foreach (var dir in new List<string>(Art.Keys)) {
            var art = Art[dir];
            if (_wearing.Contains(art) || _waiting != null && _waiting.Arts.Contains(art)) {
                continue;
            }

            Art.Remove(dir);
            if (!Worn.Remove(art)) {
                art.Destroy();
            }
        }
    }

    /// <summary>
    /// What the loaded sheets are to wear: their atlases, and the art of the skins switched on in Customizer's order.
    /// </summary>
    private sealed class Dressing {
        public readonly List<LoadedAtlas> Atlases;
        public readonly List<PackArt> Arts;

        /// <summary>
        /// The atlases by sheet and texture, as the art of a skin names them.
        /// </summary>
        public readonly List<(string Sheet, string Texture)> Wanted = [];

        public Dressing(List<LoadedAtlas> atlases, List<PackArt> arts) {
            Atlases = atlases;
            Arts = arts;
            foreach (var atlas in atlases) {
                Wanted.Add((atlas.SheetName, atlas.TextureName));
            }
        }
    }

    /// <summary>
    /// An atlas of a sheet of sprites that the game has loaded: a material of the sheet, with the game's own texture.
    /// </summary>
    private sealed class LoadedAtlas {
        public readonly tk2dSpriteCollectionData Sheet;
        public readonly int Index;
        public readonly Texture Own;
        public readonly string SheetName;
        public readonly string TextureName;

        public LoadedAtlas(tk2dSpriteCollectionData sheet, int index, Texture own) {
            Sheet = sheet;
            Index = index;
            Own = own;
            SheetName = sheet.name;
            TextureName = own.name;
        }
    }
}
