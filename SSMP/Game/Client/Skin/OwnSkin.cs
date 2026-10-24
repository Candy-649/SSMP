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
    /// The name the game gives the sheet of sprites of Hornet herself.
    /// </summary>
    internal const string HornetSheet = "Knight";

    /// <summary>
    /// How the game's names of the sheets of the moves of her crests begin.
    /// </summary>
    private const string CrestMoveSheetPrefix = "Hornet CrestWeapon";

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
    /// The game's own texture of each atlas that was dressed here, by sheet and material, which its material no longer
    /// shows.
    /// </summary>
    private static readonly Dictionary<(tk2dSpriteCollectionData Sheet, int Index), Texture> OwnOf = new();

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
            if (pack.SheetNames().Exists(IsHornetSheet)) {
                choices.Add(pack);
            }
        }

        return choices;
    }

    /// <summary>
    /// Whether a sheet of sprites is one that Hornet herself is drawn from: her own, or one of the moves of her crests,
    /// which the game draws whole, Hornet and the slash together, on sheets of their own. These are what the figure of
    /// another player counts as its body (SheetRole.Body). A skin with art for any of them is a skin of hers and is
    /// worn alone of those: Customizer takes each sheet from the first skin that has it. A skin that redraws the slashes
    /// of some crests alone was left out of the choice when only her own sheet counted, and switched on beside another
    /// skin it put Hornet back in the game's own look in those moves (USER 10-24: "它会在出刀光的时候把角色换回初始大黄蜂").
    /// </summary>
    /// <param name="sheet">The name of the sheet, as Customizer names its folder.</param>
    private static bool IsHornetSheet(string sheet) {
        return sheet == HornetSheet || sheet.StartsWith(CrestMoveSheetPrefix, StringComparison.Ordinal);
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

        foreach (var key in new List<(tk2dSpriteCollectionData Sheet, int Index)>(OwnOf.Keys)) {
            if (key.Sheet == null) {
                OwnOf.Remove(key);
            }
        }

        // The copies that other players' characters draw from have names of their own, which no skin has a folder for
        var atlases = new List<LoadedAtlas>();
        foreach (var sheet in Resources.FindObjectsOfTypeAll<tk2dSpriteCollectionData>()) {
            if (sheet == null || sheet.materials == null || !sheetsOfSkins.Contains(sheet.name)) {
                continue;
            }

            for (var i = 0; i < sheet.materials.Length; i++) {
                var own = GameTexture(sheet, i);
                if (own != null) {
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
        // A skin chosen as the save started, whose art had not loaded yet: the sheets wear the game's own art again,
        // which Customizer dresses in that skin as the next room loads, rather than keep the skin chosen before
        if (_waiting != null) {
            foreach (var atlas in _waiting.Atlases) {
                Put(atlas, atlas.Own);
            }

            _waiting = null;
        }

        // What the sheets wear is theirs from now on: destroyed if nothing draws with it, else freed by the game once
        // nothing does
        if (_wearing.Count > 0) {
            var drawn = TexturesDrawnWith();
            foreach (var art in _wearing) {
                Art.Remove(art.Pack.Dir);
                if (!art.HoldsAny(drawn)) {
                    art.Destroy();
                }
            }

            _wearing = [];
        }

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
            Texture? art = null;
            foreach (var pack in waiting.Arts) {
                art = pack.Get(atlas.SheetName, atlas.TextureName);
                if (art != null) {
                    break;
                }
            }

            Put(atlas, art != null ? art : atlas.Own);
        }

        // The art worn before is destroyed once no loaded sheet draws with it, as each one was dressed again; one that
        // still does, as a skin's folder went, keeps it until the game frees it
        var stopped = _wearing.FindAll(art => !waiting.Arts.Contains(art));
        if (stopped.Count > 0) {
            var drawn = TexturesDrawnWith();
            foreach (var art in stopped) {
                Art.Remove(art.Pack.Dir);
                if (!art.HoldsAny(drawn)) {
                    art.Destroy();
                }
            }
        }

        _wearing = waiting.Arts;
        LetGoOfUnused();

        // An object that wore a skin, which Player Color has no mask for, may wear the game's own art now
        PlayerColorMod.LookAgain();
        Changed?.Invoke();
    }

    /// <summary>
    /// Destroys the art of skins that the loaded sheets neither wear nor wait for, which never dressed anything: art
    /// that did is let go of where it stops being worn.
    /// </summary>
    private static void LetGoOfUnused() {
        foreach (var dir in new List<string>(Art.Keys)) {
            var art = Art[dir];
            if (_wearing.Contains(art) || _waiting != null && _waiting.Arts.Contains(art)) {
                continue;
            }

            Art.Remove(dir);
            art.Destroy();
        }
    }

    /// <summary>
    /// Has the given atlas of a loaded sheet draw with the given texture, if the game has not let go of either.
    /// </summary>
    private static void Put(LoadedAtlas atlas, Texture? texture) {
        if (atlas.Sheet == null || texture == null) {
            return;
        }

        var materials = atlas.Sheet.materials;
        if (materials != null && atlas.Index < materials.Length && materials[atlas.Index] != null) {
            materials[atlas.Index].mainTexture = texture;
        }

        var instances = atlas.Sheet.materialInsts;
        if (instances != null && atlas.Index < instances.Length && instances[atlas.Index] != null) {
            instances[atlas.Index].mainTexture = texture;
        }
    }

    /// <summary>
    /// The game's own texture of an atlas of a loaded sheet, or null if it can't be told: the one found the first time
    /// the atlas was dressed here; else what its material draws with, unless a skin replaced that, as Customizer's art
    /// has no name and the art loaded here is named after its skin; else the one that the sheet keeps of the atlas.
    /// </summary>
    private static Texture? GameTexture(tk2dSpriteCollectionData sheet, int index) {
        if (OwnOf.TryGetValue((sheet, index), out var known) && known != null) {
            return known;
        }

        var material = sheet.materials[index];
        if (material == null) {
            return null;
        }

        var own = IsGameTexture(material.mainTexture) ? material.mainTexture : SkinManager.OwnTexture(sheet, index);
        if (own == null || !IsGameTexture(own)) {
            return null;
        }

        OwnOf[(sheet, index)] = own;
        return own;
    }

    /// <summary>
    /// Whether a texture is one of the game's own: one that a skin put in has no name or is named after the skin.
    /// </summary>
    internal static bool IsGameTexture(Texture? texture) =>
        texture != null && texture.name.Length > 0 &&
        !texture.name.StartsWith(PackArt.NamePrefix, StringComparison.Ordinal);

    /// <summary>
    /// The textures that the loaded sheets of sprites draw with.
    /// </summary>
    private static HashSet<Texture> TexturesDrawnWith() {
        var drawn = new HashSet<Texture>();
        foreach (var sheet in Resources.FindObjectsOfTypeAll<tk2dSpriteCollectionData>()) {
            if (sheet != null) {
                AddAll(sheet.materials);
                AddAll(sheet.materialInsts);
            }
        }

        return drawn;

        void AddAll(Material[]? materials) {
            if (materials == null) {
                return;
            }

            foreach (var material in materials) {
                if (material != null && material.mainTexture != null) {
                    drawn.Add(material.mainTexture);
                }
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
