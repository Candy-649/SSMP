using System;
using System.Collections.Generic;
using System.IO;
using SSMP.Util;
using UnityEngine;
using UnityEngine.Networking;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Skin;

/// <summary>
/// Dresses the characters of other players in the skins they wear. Each game says what its player wears (a
/// <see cref="SkinLook"/>), and every other game dresses that player's character in it from its own copy of the same
/// skin mods: the art of a skin of Silksong Customizer, and the cloak colour of Player Color drawn with Player Color's
/// own shader and masks. The character of each player draws from sheets of sprites of its own, copied from the
/// player's own hero, so it wears its own skin whatever the player's own hero wears.
/// </summary>
internal class SkinManager {
    /// <summary>
    /// The properties of Player Color's shader.
    /// </summary>
    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    private static readonly int MaskTexId = Shader.PropertyToID("_MaskTex");
    private static readonly int FlashColorId = Shader.PropertyToID("_FlashColor");
    private static readonly int FlashAmountId = Shader.PropertyToID("_FlashAmount");
    private static readonly int MaskFromColorId = Shader.PropertyToID("_MaskFromColor1");
    private static readonly int MaskColorId = Shader.PropertyToID("_MaskColor1");
    private static readonly int BrightnessId = Shader.PropertyToID("t1");
    private static readonly int SaturationId = Shader.PropertyToID("t2");

    /// <summary>
    /// Whether the server lets players wear skins.
    /// </summary>
    private readonly Func<bool> _skinsAllowed;

    /// <summary>
    /// The characters of other players, by the object that draws each.
    /// </summary>
    private readonly Dictionary<GameObject, Figure> _figures = new();

    /// <summary>
    /// The atlases that the characters draw from, by sheet and texture as Customizer names them: the same for every
    /// character, as they are all copied from the same hero.
    /// </summary>
    private readonly HashSet<(string Sheet, string Texture)> _atlases = [];

    /// <summary>
    /// The art loaded from each skin that someone wears, by the folder of the skin.
    /// </summary>
    private readonly Dictionary<string, PackArt> _art = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What the other players wear, whose art is kept loaded.
    /// </summary>
    private List<string> _wanted = [];

    /// <summary>
    /// The names of the sheets of sprites of the player's own hero, once known.
    /// </summary>
    private HashSet<string>? _heroSheets;

    /// <summary>
    /// What stops watching the settings of Player Color, or null while nothing watches them.
    /// </summary>
    private Action? _stopWatchingPlayerColor;

    /// <summary>
    /// Raised when what the player's own hero wears may have changed: a setting of Player Color changed, or the player
    /// chose another skin in the menu.
    /// </summary>
    public event Action? LocalLookChanged;

    public SkinManager(Func<bool> skinsAllowed) {
        _skinsAllowed = skinsAllowed;
    }

    /// <summary>
    /// Starts watching the settings of Player Color, which the player can change while playing.
    /// </summary>
    public void RegisterHooks() {
        _stopWatchingPlayerColor ??= PlayerColorMod.OnSettingsChanged(() => LocalLookChanged?.Invoke());
        OwnSkin.Changed -= OnOwnSkinChanged;
        OwnSkin.Changed += OnOwnSkinChanged;
    }

    /// <summary>
    /// Stops watching the settings of Player Color.
    /// </summary>
    public void DeregisterHooks() {
        _stopWatchingPlayerColor?.Invoke();
        _stopWatchingPlayerColor = null;
        OwnSkin.Changed -= OnOwnSkinChanged;
    }

    /// <summary>
    /// Callback for the player choosing a skin in the menu.
    /// </summary>
    private void OnOwnSkinChanged() {
        LocalLookChanged?.Invoke();
    }

    /// <summary>
    /// What the player's own hero wears, as the text that tells the other games. The skins of Customizer count only
    /// once the hero is there, as only those with art for the hero's sheets change how the player looks.
    /// </summary>
    public string LocalLook() {
        var look = new SkinLook();

        var sheets = HeroSheets();
        if (sheets != null && CustomizerPacks.IsLoaded) {
            foreach (var pack in CustomizerPacks.FindAll()) {
                if (pack.IsActive && pack.Dresses(sheets)) {
                    look.Packs.Add(pack.Ref);
                }
            }
        }

        look.PlayerColor = PlayerColorMod.LocalLook();
        look.Unshown.AddRange(SkinModPlugins.Unshown());

        return look.ToString();
    }

    /// <summary>
    /// The names of the sheets of sprites that the player's own hero draws from, or null while there is no hero.
    /// </summary>
    private HashSet<string>? HeroSheets() {
        if (_heroSheets != null) {
            return _heroSheets;
        }

        var hero = HeroController.SilentInstance;
        if (hero == null) {
            return null;
        }

        var animator = hero.GetComponent<tk2dSpriteAnimator>();
        var library = animator != null ? animator.Library : null;
        if (library == null || library.clips == null) {
            return null;
        }

        var sheets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var clip in library.clips) {
            if (clip?.frames == null) {
                continue;
            }

            foreach (var frame in clip.frames) {
                var sheet = frame?.spriteCollection;
                if (sheet != null) {
                    sheets.Add(sheet.name);
                }
            }
        }

        return _heroSheets = sheets;
    }

    /// <summary>
    /// What the game of this player can't show of what another player wears, each as a line for the chat, so that the
    /// player knows the other player looks different on their own screen.
    /// </summary>
    public List<string> Problems(string username, string lookText) {
        var problems = new List<string>();
        var look = SkinLook.Parse(lookText);
        if (look.IsEmpty) {
            return problems;
        }

        if (!_skinsAllowed()) {
            problems.Add(Lang.Pick(
                $"Skins are off on this server, so you see {username} in the game's own look.",
                $"房主关了皮肤，你看到的 {username} 是游戏原本的样子。"
            ));
            return problems;
        }

        if (look.Packs.Count > 0) {
            var packs = CustomizerPacks.FindAll();
            foreach (var wanted in look.Packs) {
                if (CustomizerPacks.Find(packs, wanted) == null) {
                    problems.Add(Lang.Pick(
                        $"{username} wears the skin \"{wanted.Name}\", which isn't found on this computer, so " +
                        $"you see {username} without it.",
                        $"{username} 穿着皮肤「{wanted.Name}」，你这边没找到这个皮肤，所以你看到的 {username} 没穿它。"
                    ));
                }
            }
        }

        if (look.PlayerColor != null && !PlayerColorMod.CanDraw) {
            problems.Add(Lang.Pick(
                $"{username} colours their cloak with Player Color, which isn't fully installed here, so you see " +
                $"{username}'s cloak in its own colour.",
                $"{username} 用 Player Color 换了斗篷颜色，你这边没装好 Player Color，所以你看到的是原来的颜色。"
            ));
        }

        foreach (var mod in look.Unshown) {
            // Said of the mod being there, which may dress nothing at all
            problems.Add(Lang.Pick(
                $"{username} has {mod}: skins worn through it can't be shown in co-op yet.",
                $"{username} 装了 {mod}，用它换的皮肤联机里还显示不出来。"
            ));
        }

        return problems;
    }

    /// <summary>
    /// Takes in the character of another player as it is made: the copies of the hero's sheets of sprites that it
    /// draws from, by the originals they were copied from.
    /// </summary>
    public void RegisterFigure(
        GameObject figureObject,
        Dictionary<tk2dSpriteCollectionData, tk2dSpriteCollectionData> copies
    ) {
        var figure = new Figure(figureObject);
        _figures[figureObject] = figure;
        AddSlots(figure, copies);
    }

    /// <summary>
    /// Takes in more sheets of sprites copied for the character of another player, as it first plays from a library of
    /// a crest that draws from them, and dresses it again so that they wear what it wears too.
    /// </summary>
    public void AddSheets(
        GameObject figureObject,
        Dictionary<tk2dSpriteCollectionData, tk2dSpriteCollectionData> copies
    ) {
        if (_figures.TryGetValue(figureObject, out var figure) && AddSlots(figure, copies)) {
            Draw(figure);
        }
    }

    /// <summary>
    /// Adds an atlas to the character for each material of each of the given copied sheets that it doesn't have yet,
    /// and starts loading the art of what is worn for atlases that no character had before.
    /// </summary>
    /// <returns>Whether any sheet was new to the character.</returns>
    private bool AddSlots(Figure figure, Dictionary<tk2dSpriteCollectionData, tk2dSpriteCollectionData> copies) {
        var atlasesBefore = _atlases.Count;
        var added = false;

        foreach (var pair in copies) {
            var copy = pair.Key;
            var original = pair.Value;
            if (copy.materials == null || !figure.Sheets.Add(copy)) {
                continue;
            }

            added = true;
            var sheet = new SheetCopy(copy);
            for (var i = 0; i < copy.materials.Length; i++) {
                var own = OwnTexture(original, i);
                var slot = new Slot(sheet, i, original.name, own);

                AddMaterial(slot, copy.materials[i]);
                if (copy.materialInsts != null && i < copy.materialInsts.Length) {
                    AddMaterial(slot, copy.materialInsts[i]);
                }

                if (sheet.DefinitionsByMaterial.TryGetValue(i, out var definitions)) {
                    foreach (var index in definitions) {
                        AddMaterial(slot, sheet.Material[index]);
                        AddMaterial(slot, sheet.MaterialInst[index]);
                    }
                }

                if (slot.Materials.Count == 0) {
                    continue;
                }

                figure.Slots.Add(slot);
                if (slot.TextureName.Length > 0) {
                    _atlases.Add((slot.SheetName, slot.TextureName));
                }
            }
        }

        // The first character made says which atlases there are: art can be loaded now for what the other players
        // wear. The others are copies of the same hero, with the same atlases, until one plays from a library of a
        // crest that draws from sheets of its own
        if (_atlases.Count > atlasesBefore) {
            LoadWantedArt();
        }

        return added;

        static void AddMaterial(Slot slot, Material? material) {
            if (material != null && !slot.Materials.Contains(material)) {
                slot.Materials.Add(material);
            }
        }
    }

    /// <summary>
    /// The texture of the game's own art that an original sheet of sprites draws its material with: the texture that
    /// Customizer replaces, whose name names the image of a skin, kept by the sheet even after Customizer has replaced
    /// it in the material of the player's own hero.
    /// </summary>
    internal static Texture? OwnTexture(tk2dSpriteCollectionData original, int index) {
        var textures = original.textures;
        var ids = original.materialPngTextureId;
        var textureIndex = ids != null && index < ids.Length ? ids[index] : index;
        if (textures != null && textureIndex >= 0 && textureIndex < textures.Length && textures[textureIndex] != null) {
            return textures[textureIndex];
        }

        var material = original.materials != null && index < original.materials.Length ? original.materials[index] : null;
        return material != null ? material.mainTexture : null;
    }

    /// <summary>
    /// Dresses the given character in what its player wears, or in the game's own look for an empty text.
    /// </summary>
    public void Dress(GameObject? figureObject, string look) {
        if (figureObject == null || !_figures.TryGetValue(figureObject, out var figure)) {
            return;
        }

        figure.Look = look;
        Draw(figure);
    }

    /// <summary>
    /// Puts the game's own look back on the given character, as it goes back to the pool.
    /// </summary>
    public void Undress(GameObject? figureObject) {
        Dress(figureObject, "");
    }

    /// <summary>
    /// Says what the other players wear, so that the art of their skins is loaded before their characters show, and
    /// the art of skins that nobody wears any more is let go.
    /// </summary>
    public void Want(IEnumerable<string> looks) {
        _wanted = [];
        foreach (var look in looks) {
            if (!string.IsNullOrEmpty(look)) {
                _wanted.Add(look);
            }
        }

        LoadWantedArt();
    }

    /// <summary>
    /// Dresses every character again, after the server changed whether players can wear skins.
    /// </summary>
    public void RedrawAll() {
        foreach (var figure in _figures.Values) {
            Draw(figure);
        }

        LoadWantedArt();
    }

    /// <summary>
    /// Loads the art of the skins that the other players wear, and that their characters wear, and lets go of the
    /// rest.
    /// </summary>
    private void LoadWantedArt() {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_skinsAllowed() && _atlases.Count > 0) {
            var looks = new List<string>(_wanted);
            foreach (var figure in _figures.Values) {
                if (figure.Look.Length > 0) {
                    looks.Add(figure.Look);
                }
            }

            var packs = looks.Count > 0 ? CustomizerPacks.FindAll() : [];
            foreach (var text in looks) {
                foreach (var wanted in SkinLook.Parse(text).Packs) {
                    var pack = CustomizerPacks.Find(packs, wanted);
                    if (pack == null || !keep.Add(pack.Dir)) {
                        continue;
                    }

                    ArtOf(pack).Load(_atlases);
                }
            }
        }

        var gone = new List<PackArt>();
        foreach (var art in _art.Values) {
            if (!keep.Contains(art.Pack.Dir)) {
                gone.Add(art);
            }
        }

        if (gone.Count == 0) {
            return;
        }

        foreach (var art in gone) {
            Logger.Info($"Letting go of the art of the skin '{art.Pack.Name}', which nobody wears any more");
            art.Destroy();
            _art.Remove(art.Pack.Dir);
        }

        // A character that still drew with art that is gone, as its skin is no longer there, draws its own again
        foreach (var figure in _figures.Values) {
            if (figure.Wearing.Exists(art => gone.Contains(art))) {
                Draw(figure);
            }
        }
    }

    /// <summary>
    /// The art of the given skin, loaded or loading.
    /// </summary>
    private PackArt ArtOf(SkinPack pack) {
        if (!_art.TryGetValue(pack.Dir, out var art)) {
            art = new PackArt(pack, OnArtLoaded);
            _art[pack.Dir] = art;
        }

        return art;
    }

    /// <summary>
    /// Dresses the characters again once all the art of a skin has loaded.
    /// </summary>
    private void OnArtLoaded(PackArt art) {
        foreach (var figure in _figures.Values) {
            if (figure.Look.Length > 0) {
                Draw(figure);
            }
        }
    }

    /// <summary>
    /// Gives the materials of the given character the art and the shader of what it wears.
    /// </summary>
    private void Draw(Figure figure) {
        var look = _skinsAllowed() ? SkinLook.Parse(figure.Look) : new SkinLook();

        // The art of its skins in their order. Until all of it has loaded, the character keeps the game's own art, so
        // that it never shows half of a skin
        var arts = new List<PackArt>();
        if (look.Packs.Count > 0) {
            var ready = true;
            var packs = CustomizerPacks.FindAll();
            foreach (var wanted in look.Packs) {
                var pack = CustomizerPacks.Find(packs, wanted);
                if (pack == null) {
                    continue;
                }

                var art = ArtOf(pack);
                ready &= art.Load(_atlases);
                arts.Add(art);
            }

            // A character that wears these skins already keeps them while the art of atlases new to it loads, as when
            // it first plays from a library of a crest; its new atlases show the game's own art until then
            if (!ready && !SameArts(figure.Wearing, arts)) {
                arts.Clear();
            }
        }

        figure.Wearing = arts;

        var colorShader = look.PlayerColor != null ? PlayerColorMod.Shader : null;

        foreach (var slot in figure.Slots) {
            // An atlas takes the art of the first skin that has it, as Customizer gives it
            Texture? art = null;
            if (slot.TextureName.Length > 0) {
                foreach (var pack in arts) {
                    art = pack.Get(slot.SheetName, slot.TextureName);
                    if (art != null) {
                        break;
                    }
                }
            }

            var texture = art != null ? art : slot.Own;
            foreach (var material in slot.Materials) {
                if (material != null) {
                    material.mainTexture = texture;
                }
            }

            Material? colored = null;
            if (look.PlayerColor is { } color && colorShader != null) {
                colored = ColoredMaterial(slot, colorShader);
                colored.SetTexture(MainTexId, texture);
                // Player Color colours the cloak by the mask of the game's own atlas, which it finds by the name of
                // the texture: the art of a skin, whose texture has no name, has no mask
                colored.SetTexture(
                    MaskTexId,
                    art == null && slot.TextureName.Length > 0
                        ? PlayerColorMod.MaskFor(slot.SheetName, slot.TextureName)
                        : PlayerColorMod.NoMask
                );
                var source = slot.Materials[0];
                if (source.HasColor(FlashColorId)) {
                    colored.SetColor(FlashColorId, source.GetColor(FlashColorId));
                    colored.SetFloat(FlashAmountId, source.GetFloat(FlashAmountId));
                }

                colored.SetFloat(BrightnessId, color.Brightness);
                colored.SetFloat(SaturationId, color.Saturation);
                colored.SetColor(MaskFromColorId, SkinLook.PlayerColorOriginal);
                colored.SetColor(MaskColorId, color.Color);
            }

            slot.Sheet.Use(slot.Index, colored);
        }

        // The sprite takes the material of its frame only when the frame changes; this one shows the change at once
        try {
            var sprite = figure.Object != null ? figure.Object.GetComponent<tk2dSprite>() : null;
            if (sprite != null && sprite.Collection != null) {
                sprite.ForceBuild();
            }
        } catch (Exception e) {
            Logger.Warn($"Could not redraw a character after dressing it: {e.Message}");
        }
    }

    /// <summary>
    /// Whether two lists of art are the same skins in the same order.
    /// </summary>
    private static bool SameArts(List<PackArt> wearing, List<PackArt> arts) {
        if (wearing.Count != arts.Count) {
            return false;
        }

        for (var i = 0; i < arts.Count; i++) {
            if (wearing[i] != arts[i]) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The material that draws the given atlas with Player Color's shader, made the first time it is needed.
    /// </summary>
    private static Material ColoredMaterial(Slot slot, Shader shader) {
        if (slot.Colored == null || slot.Colored.shader != shader) {
            if (slot.Colored != null) {
                Object.Destroy(slot.Colored);
            }

            slot.Colored = new Material(shader) {
                name = $"SSMP Player Color {slot.SheetName}/{slot.TextureName}"
            };
        }

        return slot.Colored;
    }

    /// <summary>
    /// The character of another player.
    /// </summary>
    private sealed class Figure {
        /// <summary>
        /// The object that draws it.
        /// </summary>
        public readonly GameObject Object;

        /// <summary>
        /// Its atlases.
        /// </summary>
        public readonly List<Slot> Slots = [];

        /// <summary>
        /// The copied sheets of sprites that its atlases are of.
        /// </summary>
        public readonly HashSet<tk2dSpriteCollectionData> Sheets = [];

        /// <summary>
        /// What its player wears, as the text it travelled as.
        /// </summary>
        public string Look = "";

        /// <summary>
        /// The art of the skins it draws with now.
        /// </summary>
        public List<PackArt> Wearing = [];

        public Figure(GameObject figureObject) {
            Object = figureObject;
        }
    }

    /// <summary>
    /// A sheet of sprites copied for a character, with the materials that each of its sprites drew with as it was
    /// copied, which the sprites take back when the character no longer wears Player Color.
    /// </summary>
    private sealed class SheetCopy {
        /// <summary>
        /// The copy.
        /// </summary>
        public readonly tk2dSpriteCollectionData Copy;

        /// <summary>
        /// The material of each sprite as it was copied.
        /// </summary>
        public readonly Material?[] Material;

        /// <summary>
        /// The material that each sprite draws with, as it was copied.
        /// </summary>
        public readonly Material?[] MaterialInst;

        /// <summary>
        /// The sprites by the material they draw with.
        /// </summary>
        public readonly Dictionary<int, List<int>> DefinitionsByMaterial = new();

        public SheetCopy(tk2dSpriteCollectionData copy) {
            Copy = copy;

            // Each sprite goes with the material that it draws with, which says what atlas it is on whether or not the
            // sheet's numbers of the materials of its sprites were filled in
            var indexOf = new Dictionary<Material, int>();
            var materials = copy.materials ?? [];
            for (var m = 0; m < materials.Length; m++) {
                if (materials[m] != null && !indexOf.ContainsKey(materials[m])) {
                    indexOf[materials[m]] = m;
                }
            }

            var definitions = copy.spriteDefinitions ?? [];
            Material = new Material?[definitions.Length];
            MaterialInst = new Material?[definitions.Length];
            for (var i = 0; i < definitions.Length; i++) {
                var definition = definitions[i];
                if (definition == null) {
                    continue;
                }

                Material[i] = definition.material;
                MaterialInst[i] = definition.materialInst;
                var materialIndex = definition.material != null && indexOf.TryGetValue(definition.material, out var at)
                    ? at
                    : definition.materialId;
                if (!DefinitionsByMaterial.TryGetValue(materialIndex, out var list)) {
                    DefinitionsByMaterial[materialIndex] = list = [];
                }

                list.Add(i);
            }
        }

        /// <summary>
        /// Makes the sprites of the given material draw with the given one, or with their own for null.
        /// </summary>
        public void Use(int materialIndex, Material? material) {
            if (!DefinitionsByMaterial.TryGetValue(materialIndex, out var list)) {
                return;
            }

            var definitions = Copy.spriteDefinitions;
            foreach (var i in list) {
                var definition = definitions[i];
                definition.material = material != null ? material : Material[i];
                definition.materialInst = material != null ? material : MaterialInst[i];
            }
        }
    }

    /// <summary>
    /// One atlas of a copied sheet of sprites: a material index of the sheet.
    /// </summary>
    private sealed class Slot {
        /// <summary>
        /// The copied sheet.
        /// </summary>
        public readonly SheetCopy Sheet;

        /// <summary>
        /// The index of the material in the sheet.
        /// </summary>
        public readonly int Index;

        /// <summary>
        /// The name of the original sheet, which a skin names its folder of art for it after.
        /// </summary>
        public readonly string SheetName;

        /// <summary>
        /// The game's own texture of the atlas.
        /// </summary>
        public readonly Texture? Own;

        /// <summary>
        /// The name of the game's own texture, which a skin names its image for the atlas after.
        /// </summary>
        public readonly string TextureName;

        /// <summary>
        /// The copied materials that draw the atlas.
        /// </summary>
        public readonly List<Material> Materials = [];

        /// <summary>
        /// The material that draws it with Player Color's shader, once one was needed.
        /// </summary>
        public Material? Colored;

        public Slot(SheetCopy sheet, int index, string sheetName, Texture? own) {
            Sheet = sheet;
            Index = index;
            SheetName = sheetName;
            Own = own;
            TextureName = own != null ? own.name : "";
        }
    }
}
