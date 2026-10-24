using System;
using System.Collections.Generic;
using SSMP.Game.Settings;
using SSMP.Hooks;
using SSMP.Ui;
using Unity.Collections;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;
using UnityEngine.ResourceManagement.AsyncOperations;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Game.Client.Skin;

/// <summary>
/// Which sprites of the sheets of Hornet's art are her slashes and the like rather than Hornet herself, so that they
/// can be drawn from another skin than the one she wears (see <see cref="SlashSkin"/>). The game draws both on the same
/// sheets, each picture a sprite of its own: the sheet of a crest holds Hornet in the moves of that crest and the
/// slashes of those moves. Hornet plays her own clips on her own object, and her slashes and the like are objects of
/// their own that play clips named as effects ("SlashEffect", "Wanderer RecoilStab Efct", "SlashEffect_Glow").
///
/// So a sprite counts as a slash when a clip named as an effect draws it, unless a clip of Hornet's own draws it as
/// well and its own name doesn't say it is an effect: a crest with no slash for a move has its effect clip show one of
/// Hornet's frames. A sprite that no clip of the hero draws counts when its own name says it is an effect. Read from
/// the game's own clips and sheets, which a skin draws its art over, sprite for sprite.
/// </summary>
internal static class SlashPictures {
    /// <summary>
    /// The slash sprites of each sheet of Hornet's art that has any, by the name of the sheet, once read.
    /// </summary>
    private static Dictionary<string, HashSet<int>>? _bySheet;

    /// <summary>
    /// The slash sprites on each atlas that holds any, with where each sits on it, by sheet and the name of the game's
    /// own texture of the atlas, as a skin names its image for it.
    /// </summary>
    private static Dictionary<(string Sheet, string Texture), List<SlashSprite>>? _onAtlas;

    /// <summary>
    /// The hero being loaded in the menu to read them from, and what waits for them.
    /// </summary>
    private static AsyncOperationHandle<GameObject> _loading;
    private static readonly List<Action> Waiting = [];

    /// <summary>
    /// Whether loading the hero in the menu failed, which is not tried again: they are read as a hero comes.
    /// </summary>
    private static bool _loadFailed;

    /// <summary>
    /// Whether they are read.
    /// </summary>
    public static bool IsRead => _bySheet != null;

    /// <summary>
    /// Reads them as soon as can be, and then calls the given action: at once while there is a hero, else once the
    /// game's hero is loaded, as the menu has none, and let go again (see <see cref="SittingPreview"/>), so that the
    /// art of a skin's slashes can be cut up while the player is still in the menu. Nothing is called if that failed.
    /// </summary>
    public static void ReadSoon(Action then) {
        if (Read() != null) {
            then();
            return;
        }

        if (_loadFailed) {
            return;
        }

        if (!Waiting.Contains(then)) {
            Waiting.Add(then);
        }

        if (_loading.IsValid()) {
            return;
        }

        try {
            _loading = Addressables.LoadAssetAsync<GameObject>(SittingPreview.HeroAddress);
        } catch (Exception e) {
            Logger.Warn($"Could not load the hero to find the slashes in her art: {e.Message}");
            _loadFailed = true;
            Waiting.Clear();
            return;
        }

        _loading.Completed += loaded => {
            try {
                var hero = loaded.Status == AsyncOperationStatus.Succeeded && loaded.Result != null
                    ? loaded.Result.GetComponent<HeroController>()
                    : null;
                if (hero != null) {
                    Read(hero);
                }
            } catch (Exception e) {
                Logger.Warn($"Could not find the slashes in the art of the hero loaded in the menu: {e.Message}");
            }

            Addressables.Release(loaded);
            _loading = default;

            if (_bySheet == null) {
                Logger.Warn("Could not find the slashes in the art of the hero loaded in the menu");
                _loadFailed = true;
                Waiting.Clear();
                return;
            }

            var waiting = new List<Action>(Waiting);
            Waiting.Clear();
            foreach (var action in waiting) {
                try {
                    action();
                } catch (Exception e) {
                    Logger.Error($"Could not go on once the slashes in Hornet's art were found: {e}");
                }
            }
        };
    }

    /// <summary>
    /// Has each sprite under an object that shows a slash of the given sheets take the material and the place on its
    /// atlas that the slash draws with now. A sprite takes them only as its frame changes, and a slash may show the
    /// same frame for long, or show it again the next time it comes.
    /// </summary>
    /// <param name="root">The object.</param>
    /// <param name="slashesOf">The slash sprites of each sheet, by the sheet that its sprites draw from.</param>
    public static void Redraw(GameObject root, Dictionary<tk2dSpriteCollectionData, HashSet<int>> slashesOf) {
        if (slashesOf.Count == 0) {
            return;
        }

        foreach (var sprite in root.GetComponentsInChildren<tk2dSprite>(true)) {
            var sheet = sprite.Collection != null ? sprite.Collection.inst : null;
            if (sheet != null && slashesOf.TryGetValue(sheet, out var slashes) && slashes.Contains(sprite.spriteId)) {
                sprite.ForceBuild();
            }
        }
    }

    /// <summary>
    /// The slash sprites of the sheet of Hornet's art with the given name, or null for a sheet with none, or while they
    /// can't be read, as there is no hero yet.
    /// </summary>
    public static HashSet<int>? Of(string sheet) {
        var bySheet = Read();
        return bySheet != null && bySheet.TryGetValue(sheet, out var sprites) ? sprites : null;
    }

    /// <summary>
    /// The atlases that hold slash sprites, or null while they can't be read, as there is no hero yet.
    /// </summary>
    public static ICollection<(string Sheet, string Texture)>? Atlases() {
        Read();
        return _onAtlas?.Keys;
    }

    /// <summary>
    /// The slash sprites on the given atlas, or null for none.
    /// </summary>
    public static List<SlashSprite>? OnAtlas(string sheet, string texture) {
        Read();
        return _onAtlas != null && _onAtlas.TryGetValue((sheet, texture), out var sprites) ? sprites : null;
    }

    /// <summary>
    /// The libraries of clips that the hero and everything on it play from: its own, those of its crests, which it
    /// plays from in place of its own, and those of the objects on it, its slashes, strikes, silk skills and the rest.
    /// </summary>
    public static List<tk2dSpriteAnimation> HeroLibraries(HeroController hero) {
        var libraries = new List<tk2dSpriteAnimation>();

        var animator = hero.GetComponent<tk2dSpriteAnimator>();
        if (animator != null && animator.Library != null) {
            libraries.Add(animator.Library);
        }

        foreach (var groups in new[] { hero.configs, hero.specialConfigs }) {
            if (groups == null) {
                continue;
            }

            foreach (var group in groups) {
                var library = group?.Config != null ? group.Config.heroAnimOverrideLib : null;
                if (library != null && !libraries.Contains(library)) {
                    libraries.Add(library);
                }
            }
        }

        foreach (var part in hero.GetComponentsInChildren<tk2dSpriteAnimator>(true)) {
            if (part.Library != null && !libraries.Contains(part.Library)) {
                libraries.Add(part.Library);
            }
        }

        return libraries;
    }

    /// <summary>
    /// The sheets of sprites that the clips of the given hero draw from.
    /// </summary>
    public static HashSet<tk2dSpriteCollectionData> HeroSheets(HeroController hero) {
        var sheets = new HashSet<tk2dSpriteCollectionData>();
        foreach (var library in HeroLibraries(hero)) {
            foreach (var clip in library.clips ?? []) {
                foreach (var frame in clip?.frames ?? []) {
                    if (frame?.spriteCollection != null) {
                        sheets.Add(frame.spriteCollection);
                    }
                }
            }
        }

        return sheets;
    }

    /// <summary>
    /// The sheet that the sprites of the given sheet are drawn from, as a sprite takes it, or null if it can't be set
    /// up: the game sets a sheet up the first time something draws from it.
    /// </summary>
    public static tk2dSpriteCollectionData? Drawn(tk2dSpriteCollectionData sheet) {
        try {
            return sheet.inst;
        } catch (Exception e) {
            Logger.Warn($"Could not set up the sheet of sprites '{sheet.name}': {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// The number of the material of a sheet that draws with the given material, which says what atlas a sprite that
    /// draws with it is on whether or not the sheet's numbers of the materials of its sprites were filled in; else the
    /// given number.
    /// </summary>
    public static int MaterialIndex(tk2dSpriteCollectionData sheet, Material? material, int materialId) {
        var materials = sheet.materials;
        if (materials != null && material != null) {
            for (var m = 0; m < materials.Length; m++) {
                if (materials[m] == material) {
                    return m;
                }
            }
        }

        return materialId;
    }

    /// <summary>
    /// Reads the slash sprites from the clips of the given hero, or of the local hero, the first time there is one: they
    /// are the game's own and don't change while it runs. Read before anything here moves a sprite to art of its own.
    /// </summary>
    private static Dictionary<string, HashSet<int>>? Read(HeroController? hero = null) {
        if (_bySheet != null) {
            return _bySheet;
        }

        if (hero == null) {
            hero = HeroController.SilentInstance;
        }

        if (hero == null) {
            return null;
        }

        // The sprites that clips named as effects draw, and those that Hornet's own clips draw, by sheet
        var asEffect = new Dictionary<tk2dSpriteCollectionData, HashSet<int>>();
        var asHer = new Dictionary<tk2dSpriteCollectionData, HashSet<int>>();
        foreach (var library in HeroLibraries(hero)) {
            foreach (var clip in library.clips ?? []) {
                if (clip?.frames == null) {
                    continue;
                }

                var drawnBy = NamedAsEffect(clip.name, true) ? asEffect : asHer;
                foreach (var frame in clip.frames) {
                    var sheet = frame?.spriteCollection;
                    if (sheet == null) {
                        continue;
                    }

                    if (!drawnBy.TryGetValue(sheet, out var sprites)) {
                        drawnBy[sheet] = sprites = [];
                    }

                    sprites.Add(frame!.spriteId);
                }
            }
        }

        var bySheet = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var onAtlas = new Dictionary<(string Sheet, string Texture), List<SlashSprite>>();
        var sheets = new HashSet<tk2dSpriteCollectionData>(asEffect.Keys);
        sheets.UnionWith(asHer.Keys);
        var count = 0;
        foreach (var sheet in sheets) {
            var drawn = Drawn(sheet);
            var definitions = drawn != null ? drawn.spriteDefinitions : null;
            if (drawn == null || definitions == null) {
                continue;
            }

            asEffect.TryGetValue(sheet, out var effects);
            asHer.TryGetValue(sheet, out var hers);
            HashSet<int>? slashes = null;
            for (var i = 0; i < definitions.Length; i++) {
                var definition = definitions[i];
                if (definition?.uvs == null || definition.uvs.Length == 0) {
                    continue;
                }

                var byEffect = effects != null && effects.Contains(i);
                var byHer = hers != null && hers.Contains(i);
                var named = NamedAsEffect(definition.name, false);
                if (!(byEffect ? !byHer || named : !byHer && named)) {
                    continue;
                }

                var index = MaterialIndex(drawn, definition.material, definition.materialId);
                var texture = SkinManager.OwnTexture(drawn, index);
                if (texture == null || texture.name.Length == 0) {
                    continue;
                }

                (slashes ??= []).Add(i);
                count++;

                var atlas = (sheet.name, texture.name);
                if (!onAtlas.TryGetValue(atlas, out var sprites)) {
                    onAtlas[atlas] = sprites = [];
                }

                sprites.Add(new SlashSprite(i, (Vector2[]) definition.uvs.Clone()));
            }

            if (slashes != null) {
                bySheet[sheet.name] = slashes;
            }
        }

        _onAtlas = onAtlas;
        _bySheet = bySheet;
        Logger.Info($"Found {count} slash sprites on {bySheet.Count} sheets of Hornet's art, on {onAtlas.Count} atlases");
        return bySheet;
    }

    /// <summary>
    /// Whether a name says that what it names is an effect: of a clip, "Effect", "Efct" or "Glow" in any case; of a
    /// sprite, "effect" or "glow".
    /// </summary>
    private static bool NamedAsEffect(string? name, bool clip) {
        if (string.IsNullOrEmpty(name)) {
            return false;
        }

        return name!.IndexOf("effect", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("glow", StringComparison.OrdinalIgnoreCase) >= 0 ||
               clip && name.IndexOf("efct", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

/// <summary>
/// A slash sprite on an atlas: its number in its sheet, and where it sits on the game's atlas.
/// </summary>
internal readonly record struct SlashSprite(int Id, Vector2[] Uvs);

/// <summary>
/// The skin that the player's own slashes are drawn from, chosen on the settings page apart from the skin that Hornet
/// wears: her body from one skin and her slashes from another, or from the game's own art (USER 10-24, of a skin that
/// redraws the slashes alone and full skins that redraw them in their own colours: "可以尝试一下"). It is put together
/// as the game runs and never written anywhere: each player draws it from their own copy of the skin.
///
/// Each slash sprite of the local hero's sheets draws with a material of its own: from the game's own atlas, or from
/// the chosen skin's slash sprites of that atlas cut out and put together (see <see cref="SlashAtlas"/>), at their new
/// place there. The rest of each sheet draws with the sheet's own materials, which Customizer dresses in the skin that
/// Hornet wears. A sprite takes the picture and the material of its frame each time its frame changes, and Player
/// Color, which reads the texture of the picture shown, finds no mask of the cloak for this art and leaves the slashes
/// in their colours.
/// </summary>
internal static class SlashSkin {
    /// <summary>
    /// The choice of the game's own slashes as the settings keep it: no folder can have this name.
    /// </summary>
    public const string GameChoice = "*";

    /// <summary>
    /// The settings that keep the choice.
    /// </summary>
    private static ModSettings? _settings;

    /// <summary>
    /// The material made for the slashes of each atlas of the local hero's sheets, by sheet and material.
    /// </summary>
    private static readonly Dictionary<(tk2dSpriteCollectionData Sheet, int Index), Material> Made = new();

    /// <summary>
    /// What each slash sprite of the local hero's sheets that draws from art put in here drew with before, to be put
    /// back, and to be copied for the characters of other players in its place.
    /// </summary>
    private static readonly Dictionary<tk2dSpriteDefinition, Kept> Before = new();

    /// <summary>
    /// The skin whose art the local hero's slashes draw with now, or null for none: kept while the art of the next one
    /// loads.
    /// </summary>
    private static SkinPack? _worn;

    /// <summary>
    /// Raised when the player chose other slashes.
    /// </summary>
    public static event Action? Changed;

    /// <summary>
    /// Whether this watches for heroes and the menu.
    /// </summary>
    private static bool _started;

    /// <summary>
    /// Gives this the settings that keep the choice, and has the slashes drawn as chosen from then on, whether or not
    /// the player plays with others: each hero that comes wears them, and in the menu, where the game is started, the
    /// art of the chosen skin's slashes is cut up, so that nothing is cut up as a save starts.
    /// </summary>
    public static void Start(ModSettings settings) {
        _settings ??= settings;
        if (_started) {
            return;
        }

        _started = true;
        EventHooks.UIManagerUIGoToMainMenu += DressLoaded;

        // Called at once while there is a hero
        CustomHooks.HeroControllerStartAction += DressLoaded;
        if (HeroController.SilentInstance == null) {
            DressLoaded();
        }
    }

    /// <summary>
    /// The choice as the settings keep it: empty for the slashes of the skin that Hornet wears, <see cref="GameChoice"/>
    /// for the game's own, or else the name of a skin's folder. Empty too while Customizer, whose skins they are, isn't
    /// loaded: Hornet wears no skin then, and the choice is neither shown nor can be changed.
    /// </summary>
    public static string Choice => OwnSkin.CanChoose ? _settings?.SlashSkin ?? "" : "";

    /// <summary>
    /// The skins that slashes can be drawn from: those of Hornet, which have art for her sheets.
    /// </summary>
    public static List<SkinPack> Choices() => OwnSkin.Choices();

    /// <summary>
    /// The skin chosen out of the given ones, or null for none chosen or none found.
    /// </summary>
    public static SkinPack? ChosenPack(List<SkinPack> packs) {
        var choice = Choice;
        if (choice.Length == 0 || choice == GameChoice) {
            return null;
        }

        foreach (var pack in packs) {
            if (string.Equals(pack.Name, choice, StringComparison.OrdinalIgnoreCase)) {
                return pack;
            }
        }

        return null;
    }

    /// <summary>
    /// Draws the player's slashes as chosen from now on, also after a restart: from the skin that Hornet wears for an
    /// empty choice, from the game's own art for <see cref="GameChoice"/>, or else from the skin of that folder.
    /// </summary>
    public static void Choose(string choice) {
        if (_settings == null || _settings.SlashSkin == choice) {
            return;
        }

        _settings.SlashSkin = choice;
        _settings.Save();
        Logger.Info(
            choice.Length == 0 ? "Chose to draw the slashes as the skin worn"
            : choice == GameChoice ? "Chose to draw the slashes in the game's own look"
            : $"Chose to draw the slashes from the skin '{choice}'"
        );

        DressLoaded();
        Changed?.Invoke();
    }

    /// <summary>
    /// What the other games are told the player's slashes are drawn from, or null for the skin that Hornet wears, as
    /// for a chosen skin that is no longer here.
    /// </summary>
    public static SlashLook? LocalLook() {
        if (Choice == GameChoice) {
            return new SlashLook(null);
        }

        var pack = ChosenPack(CustomizerPacks.FindAll());
        return pack != null ? new SlashLook(pack.Ref) : null;
    }

    /// <summary>
    /// What a sprite of the local hero's sheets is copied with for the character of another player: what it drew with
    /// before this put its slashes on art of their own, else what it draws with.
    /// </summary>
    public static (Material? Material, Material? Inst, Vector2[] Uvs) Original(tk2dSpriteDefinition definition) {
        return Before.TryGetValue(definition, out var kept)
            ? (kept.Material, kept.Inst, kept.Uvs)
            : (definition.material, definition.materialInst, definition.uvs);
    }

    /// <summary>
    /// Draws the slashes of the local hero's sheets as chosen, once the art of the chosen skin is in, and the rest of
    /// the time as before. Called as the choice changes, as its art comes in, as a hero comes, whose sheets may be new
    /// ones, and as the menu shows, where the art of the chosen skin's slashes is cut up while there is no hero. What
    /// fails is logged, as this runs among what the game and this mod do as a hero starts.
    /// </summary>
    public static void DressLoaded() {
        try {
            Dress();
        } catch (Exception e) {
            Logger.Error($"Could not draw the slashes as chosen: {e}");
        }
    }

    /// <inheritdoc cref="DressLoaded"/>
    private static void Dress() {
        ForgetGone();

        var hero = HeroController.SilentInstance;
        var game = Choice == GameChoice;
        var pack = game ? null : ChosenPack(CustomizerPacks.FindAll());

        // In the menu the slashes of the chosen skin are cut up from the art of the hero loaded there, so that nothing
        // is cut up as a save starts
        if (pack != null && !SlashPictures.IsRead) {
            if (hero == null) {
                SlashArt.KeepLocal(_worn, pack);
                SlashPictures.ReadSoon(DressLoaded);
                return;
            }
        }

        // The slashes keep what they wear until all the art of the chosen skin is in, which calls this again
        SlashPackArt? art = null;
        if (pack != null && SlashPictures.Atlases() != null) {
            art = SlashArt.Of(pack);
            if (!art.Load()) {
                SlashArt.KeepLocal(_worn, pack);
                return;
            }
        }

        var dressed = new HashSet<tk2dSpriteDefinition>();
        var used = new HashSet<Material>();
        var changed = false;
        if (hero != null && (game || art != null)) {
            foreach (var sheet in SlashPictures.HeroSheets(hero)) {
                var slashes = SlashPictures.Of(sheet.name);
                var drawn = slashes != null ? SlashPictures.Drawn(sheet) : null;
                if (drawn?.spriteDefinitions == null) {
                    continue;
                }

                var definitions = drawn.spriteDefinitions;
                foreach (var id in slashes!) {
                    if (id < 0 || id >= definitions.Length || definitions[id] == null) {
                        continue;
                    }

                    var definition = definitions[id];
                    var kept = Before.TryGetValue(definition, out var before)
                        ? before
                        : new Kept(drawn, definition.material, definition.materialInst, definition.uvs);
                    var index = SlashPictures.MaterialIndex(drawn, kept.Material, definition.materialId);
                    var own = SkinManager.OwnTexture(drawn, index);
                    if (kept.Material == null || own == null) {
                        continue;
                    }

                    Texture texture;
                    Vector2[] uvs;
                    if (game) {
                        texture = own;
                        uvs = kept.Uvs;
                    } else if (art!.Get(sheet.name, own.name) is { } atlas && atlas.Uvs.TryGetValue(id, out var moved)) {
                        texture = atlas.Texture;
                        uvs = moved;
                    } else {
                        continue;
                    }

                    if (!Made.TryGetValue((drawn, index), out var material) || material == null) {
                        material = new Material(kept.Inst != null ? kept.Inst : kept.Material) {
                            name = $"SSMP slashes {sheet.name}/{own.name}"
                        };
                        Made[(drawn, index)] = material;
                    }

                    changed |= material.mainTexture != texture || definition.materialInst != material ||
                               definition.uvs != uvs;
                    material.mainTexture = texture;
                    used.Add(material);

                    Before[definition] = kept;
                    definition.material = material;
                    definition.materialInst = material;
                    definition.uvs = uvs;
                    dressed.Add(definition);
                }
            }
        }

        // The slashes that no longer draw from art put in here draw as the rest of their sheet again
        foreach (var definition in new List<tk2dSpriteDefinition>(Before.Keys)) {
            if (dressed.Contains(definition)) {
                continue;
            }

            var kept = Before[definition];
            Before.Remove(definition);
            definition.material = kept.Material;
            definition.materialInst = kept.Inst;
            definition.uvs = kept.Uvs;
            changed = true;
        }

        // What of the hero shows a slash now takes the change at once
        if (hero != null && changed) {
            var slashesOf = new Dictionary<tk2dSpriteCollectionData, HashSet<int>>();
            foreach (var sheet in SlashPictures.HeroSheets(hero)) {
                if (SlashPictures.Of(sheet.name) is { } slashes && SlashPictures.Drawn(sheet) is { } drawn) {
                    slashesOf[drawn] = slashes;
                }
            }

            SlashPictures.Redraw(hero.gameObject, slashesOf);
        }

        foreach (var key in new List<(tk2dSpriteCollectionData Sheet, int Index)>(Made.Keys)) {
            if (!used.Contains(Made[key])) {
                Object.Destroy(Made[key]);
                Made.Remove(key);
            }
        }

        // The art of a skin chosen before is let go once nothing of the local hero draws with it
        _worn = art != null ? pack : null;
        SlashArt.KeepLocal(_worn, _worn);
    }

    /// <summary>
    /// Lets go of what was kept of sheets that the game has unloaded, as when the player went back to the menu: the
    /// next hero draws from new ones.
    /// </summary>
    private static void ForgetGone() {
        foreach (var definition in new List<tk2dSpriteDefinition>(Before.Keys)) {
            if (Before[definition].Sheet == null) {
                Before.Remove(definition);
            }
        }

        foreach (var key in new List<(tk2dSpriteCollectionData Sheet, int Index)>(Made.Keys)) {
            if (key.Sheet == null) {
                if (Made[key] != null) {
                    Object.Destroy(Made[key]);
                }

                Made.Remove(key);
            }
        }
    }

    /// <summary>
    /// What a slash sprite drew with before it was put on art of its own: its sheet, its materials and where it sat on
    /// its atlas.
    /// </summary>
    private readonly record struct Kept(
        tk2dSpriteCollectionData Sheet,
        Material? Material,
        Material? Inst,
        Vector2[] Uvs
    );
}

/// <summary>
/// The art that slashes are drawn from, of the skin the player chose for their own and of those the other players
/// chose, loaded once for all of them.
/// </summary>
internal static class SlashArt {
    /// <summary>
    /// The art of each skin, by its folder.
    /// </summary>
    private static readonly Dictionary<string, SlashPackArt> Arts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The folders of the skins of the player's own slashes: the one they are drawn from now and the one chosen, which
    /// may still be loading, or null for none.
    /// </summary>
    private static string? _localWorn;
    private static string? _localChosen;

    /// <summary>
    /// The folders of the skins of the other players' slashes.
    /// </summary>
    private static HashSet<string> _others = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised when all the art of a skin's slashes is in.
    /// </summary>
    public static event Action? Loaded;

    /// <summary>
    /// The art of the slashes of the given skin, loaded or loading.
    /// </summary>
    public static SlashPackArt Of(SkinPack pack) {
        if (!Arts.TryGetValue(pack.Dir, out var art)) {
            art = new SlashPackArt(pack, OnLoaded);
            Arts[pack.Dir] = art;
        }

        return art;
    }

    /// <summary>
    /// Keeps the art of the skin that the player's own slashes are drawn from and of the one chosen for them, either
    /// null for none, and lets go of what nobody draws with.
    /// </summary>
    public static void KeepLocal(SkinPack? worn, SkinPack? chosen) {
        _localWorn = worn?.Dir;
        _localChosen = chosen?.Dir;
        LetGoOfUnused();
    }

    /// <summary>
    /// Keeps the art of the skins of the other players' slashes, and lets go of what nobody draws with.
    /// </summary>
    /// <returns>The art let go of.</returns>
    public static List<SlashPackArt> KeepForOthers(IEnumerable<SkinPack> packs) {
        _others = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in packs) {
            _others.Add(pack.Dir);
        }

        return LetGoOfUnused();
    }

    /// <summary>
    /// Lets go of the art of the skins that no one's slashes are drawn from or wait for.
    /// </summary>
    /// <returns>The art let go of.</returns>
    private static List<SlashPackArt> LetGoOfUnused() {
        var gone = new List<SlashPackArt>();
        foreach (var dir in new List<string>(Arts.Keys)) {
            if (string.Equals(dir, _localWorn, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dir, _localChosen, StringComparison.OrdinalIgnoreCase) || _others.Contains(dir)) {
                continue;
            }

            var art = Arts[dir];
            Logger.Info($"Letting go of the slashes of the skin '{art.Pack.Name}'");
            art.Destroy();
            Arts.Remove(dir);
            gone.Add(art);
        }

        return gone;
    }

    /// <summary>
    /// Dresses the slashes again once all the art of a skin is in.
    /// </summary>
    private static void OnLoaded(SlashPackArt art) {
        if (!Arts.ContainsValue(art)) {
            return;
        }

        if (string.Equals(art.Pack.Dir, _localChosen, StringComparison.OrdinalIgnoreCase)) {
            SlashSkin.DressLoaded();
        }

        Loaded?.Invoke();
    }
}

/// <summary>
/// The slashes of one skin: for each atlas that holds slash sprites, the skin's slash sprites of it alone (see
/// <see cref="SlashAtlas"/>). The images load one at a time, each read off the main thread, so that only one whole
/// image is in memory at once.
/// </summary>
internal sealed class SlashPackArt {
    /// <summary>
    /// The skin.
    /// </summary>
    public readonly SkinPack Pack;

    /// <summary>
    /// Called once all the art asked for is in.
    /// </summary>
    private readonly Action<SlashPackArt> _loaded;

    /// <summary>
    /// The slashes of each atlas that are in, with null for an atlas that the skin has no image for, or whose image
    /// could not be read.
    /// </summary>
    private readonly Dictionary<(string Sheet, string Texture), SlashAtlas?> _atlases = new();

    /// <summary>
    /// The atlases whose images wait to be read, with the image of each.
    /// </summary>
    private readonly Queue<(string Sheet, string Texture, string File)> _waiting = new();

    /// <summary>
    /// The atlases asked for, read or waiting.
    /// </summary>
    private readonly HashSet<(string Sheet, string Texture)> _asked = [];

    /// <summary>
    /// The image being read, or null for none.
    /// </summary>
    private UnityWebRequest? _reading;

    /// <summary>
    /// Whether the art was let go, after which nothing that finishes loading is kept.
    /// </summary>
    private bool _destroyed;

    public SlashPackArt(SkinPack pack, Action<SlashPackArt> loaded) {
        Pack = pack;
        _loaded = loaded;
    }

    /// <summary>
    /// The slashes of the given atlas, or null for none or none yet.
    /// </summary>
    public SlashAtlas? Get(string sheet, string texture) =>
        _atlases.TryGetValue((sheet, texture), out var atlas) ? atlas : null;

    /// <summary>
    /// Starts reading what isn't read yet of the atlases that hold slash sprites, and says whether all of it is in.
    /// </summary>
    public bool Load() {
        var atlases = SlashPictures.Atlases();
        if (atlases == null || _destroyed) {
            return false;
        }

        foreach (var atlas in atlases) {
            if (!_asked.Add(atlas)) {
                continue;
            }

            var file = Pack.ArtFile(atlas.Sheet, atlas.Texture);
            if (file == null) {
                _atlases[atlas] = null;
            } else {
                _waiting.Enqueue((atlas.Sheet, atlas.Texture, file));
            }
        }

        ReadNext();
        return _waiting.Count == 0 && _reading == null;
    }

    /// <summary>
    /// Starts reading the next image that waits, unless one is being read. Unity reads it off the main thread; it is
    /// kept readable to be cut up, and is let go of once it is.
    /// </summary>
    private void ReadNext() {
        while (_reading == null && !_destroyed && _waiting.Count > 0) {
            var (sheet, texture, file) = _waiting.Dequeue();
            UnityWebRequest request;
            try {
                var parameters = DownloadedTextureParams.Default;
                parameters.mipmapChain = false;
                parameters.readable = true;
                request = UnityWebRequestTexture.GetTexture(new Uri(file).AbsoluteUri, parameters);
            } catch (Exception e) {
                Logger.Warn($"Could not start reading '{file}': {e.Message}");
                _atlases[(sheet, texture)] = null;
                continue;
            }

            _reading = request;
            request.SendWebRequest().completed += _ => OnRead(request, sheet, texture, file);
        }
    }

    /// <summary>
    /// Cuts the slashes out of an image that was read, and reads the next one, or says that all of it is in.
    /// </summary>
    private void OnRead(UnityWebRequest request, string sheet, string texture, string file) {
        if (_reading != request) {
            request.Dispose();
            return;
        }

        _reading = null;

        Texture2D? image = null;
        try {
            if (request.result == UnityWebRequest.Result.Success) {
                image = DownloadHandlerTexture.GetContent(request);
            } else {
                Logger.Warn($"Could not read '{file}': {request.error}");
            }
        } catch (Exception e) {
            Logger.Warn($"Could not read '{file}': {e.Message}");
        } finally {
            request.Dispose();
        }

        SlashAtlas? atlas = null;
        if (image != null) {
            try {
                atlas = SlashAtlas.Cut(image, sheet, texture, Pack.Name);
            } catch (Exception e) {
                Logger.Warn($"Could not cut the slashes out of '{file}': {e.Message}");
            }

            Object.Destroy(image);
        }

        if (_destroyed) {
            atlas?.Destroy();
            return;
        }

        _atlases[(sheet, texture)] = atlas;
        ReadNext();
        if (_reading == null && _waiting.Count == 0) {
            Logger.Info($"Loaded the slashes of the skin '{Pack.Name}'");
            _loaded(this);
        }
    }

    /// <summary>
    /// Lets go of the art: stops what is being read and frees the textures.
    /// </summary>
    public void Destroy() {
        _destroyed = true;
        _waiting.Clear();
        if (_reading != null) {
            var reading = _reading;
            _reading = null;
            reading.Abort();
        }

        foreach (var atlas in _atlases.Values) {
            atlas?.Destroy();
        }

        _atlases.Clear();
    }
}

/// <summary>
/// The slash sprites of one atlas of a skin, cut out of the skin's image and put together on a texture of their own,
/// with where each sits there. A skin's image loads at four bytes a pixel, so the four atlases of Hornet's own sheet
/// would take some 340 MB more for the slashes of a skin on top of the skin worn; the slashes alone take a fraction.
/// </summary>
internal sealed class SlashAtlas {
    /// <summary>
    /// The pixels kept around each sprite, which its edges are smoothed with as it is drawn, as on the skin's image.
    /// </summary>
    private const int Margin = 2;

    /// <summary>
    /// The texture.
    /// </summary>
    public readonly Texture2D Texture;

    /// <summary>
    /// Where each slash sprite sits on the texture, by its number in its sheet.
    /// </summary>
    public readonly Dictionary<int, Vector2[]> Uvs;

    private SlashAtlas(Texture2D texture, Dictionary<int, Vector2[]> uvs) {
        Texture = texture;
        Uvs = uvs;
    }

    /// <summary>
    /// Cuts the slash sprites of the given atlas out of a skin's image of it, which is kept readable, or null if it
    /// has none or they don't fit on a texture.
    /// </summary>
    public static SlashAtlas? Cut(Texture2D image, string sheet, string texture, string skin) {
        var sprites = SlashPictures.OnAtlas(sheet, texture);
        if (sprites == null || sprites.Count == 0) {
            return null;
        }

        var width = image.width;
        var height = image.height;

        // The part of the image under each sprite, with its margin, in pixels from the bottom left, as the texture
        // keeps its pixels and as the sprites' places are given
        var parts = new List<(SlashSprite Sprite, RectInt From)>();
        foreach (var sprite in sprites) {
            float left = 1f, bottom = 1f, right = 0f, top = 0f;
            foreach (var uv in sprite.Uvs) {
                left = Mathf.Min(left, uv.x);
                right = Mathf.Max(right, uv.x);
                bottom = Mathf.Min(bottom, uv.y);
                top = Mathf.Max(top, uv.y);
            }

            var x0 = Mathf.Clamp(Mathf.FloorToInt(left * width) - Margin, 0, width);
            var x1 = Mathf.Clamp(Mathf.CeilToInt(right * width) + Margin, 0, width);
            var y0 = Mathf.Clamp(Mathf.FloorToInt(bottom * height) - Margin, 0, height);
            var y1 = Mathf.Clamp(Mathf.CeilToInt(top * height) + Margin, 0, height);
            if (x1 > x0 && y1 > y0) {
                parts.Add((sprite, new RectInt(x0, y0, x1 - x0, y1 - y0)));
            }
        }

        if (parts.Count == 0) {
            return null;
        }

        // Laid out in rows as wide as the image, the tallest first
        parts.Sort((a, b) => b.From.height.CompareTo(a.From.height));
        var places = new Vector2Int[parts.Count];
        int x = 0, y = 0, rowHeight = 0;
        for (var i = 0; i < parts.Count; i++) {
            var from = parts[i].From;
            if (x + from.width > width) {
                y += rowHeight;
                x = 0;
                rowHeight = 0;
            }

            places[i] = new Vector2Int(x, y);
            x += from.width;
            rowHeight = Mathf.Max(rowHeight, from.height);
        }

        var cutHeight = y + rowHeight;
        if (cutHeight > SystemInfo.maxTextureSize) {
            Logger.Warn($"The slashes of '{sheet}/{texture}' of the skin '{skin}' don't fit on a texture");
            return null;
        }

        // The pixels, copied part by part, row by row, from what Unity read; a format of another size of pixel is
        // read out as colours first
        var bytesPerPixel = image.format switch {
            TextureFormat.RGBA32 or TextureFormat.ARGB32 or TextureFormat.BGRA32 => 4,
            TextureFormat.RGB24 => 3,
            _ => 0
        };
        var format = bytesPerPixel > 0 ? image.format : TextureFormat.RGBA32;
        var pixelSize = bytesPerPixel > 0 ? bytesPerPixel : 4;
        var cut = new byte[width * cutHeight * pixelSize];
        if (bytesPerPixel > 0) {
            var from = image.GetRawTextureData<byte>();
            for (var i = 0; i < parts.Count; i++) {
                var part = parts[i].From;
                for (var row = 0; row < part.height; row++) {
                    NativeArray<byte>.Copy(
                        from,
                        ((part.y + row) * width + part.x) * pixelSize,
                        cut,
                        ((places[i].y + row) * width + places[i].x) * pixelSize,
                        part.width * pixelSize
                    );
                }
            }
        } else {
            var colours = image.GetPixels32();
            for (var i = 0; i < parts.Count; i++) {
                var part = parts[i].From;
                for (var row = 0; row < part.height; row++) {
                    for (var column = 0; column < part.width; column++) {
                        var colour = colours[(part.y + row) * width + part.x + column];
                        var at = ((places[i].y + row) * width + places[i].x + column) * 4;
                        cut[at] = colour.r;
                        cut[at + 1] = colour.g;
                        cut[at + 2] = colour.b;
                        cut[at + 3] = colour.a;
                    }
                }
            }
        }

        // Without smaller copies of itself, which the CPU would make as it is cut, mid-game for another player's choice
        var result = new Texture2D(width, cutHeight, format, false) {
            name = $"{PackArt.NamePrefix}{skin} slashes {sheet}/{texture}",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = image.filterMode
        };
        result.SetPixelData(cut, 0);
        result.Apply(false, true);

        // Each sprite's place on the new texture: the same pixels, moved
        var uvs = new Dictionary<int, Vector2[]>();
        for (var i = 0; i < parts.Count; i++) {
            var (sprite, from) = parts[i];
            var dx = places[i].x - from.x;
            var dy = places[i].y - from.y;
            var moved = new Vector2[sprite.Uvs.Length];
            for (var v = 0; v < moved.Length; v++) {
                moved[v] = new Vector2(
                    (sprite.Uvs[v].x * width + dx) / width,
                    (sprite.Uvs[v].y * height + dy) / cutHeight
                );
            }

            uvs[sprite.Id] = moved;
        }

        return new SlashAtlas(result, uvs);
    }

    /// <summary>
    /// Frees the texture.
    /// </summary>
    public void Destroy() {
        if (Texture != null) {
            Object.Destroy(Texture);
        }
    }
}

/// <summary>
/// What a player's slashes are drawn from when not from the skins they wear: a skin of Customizer, or the game's own art
/// for none.
/// </summary>
internal sealed record SlashLook(SkinPackRef? Pack);
