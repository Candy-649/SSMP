using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMProOld;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using SSMP.Util;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Ui.Resources;

/// <summary>
/// The font manager that stores fonts that are used in-game.
/// </summary>
internal static class FontManager {
    /// <summary>
    /// The font the game draws its English text with, and the one this mod drew everything with.
    /// </summary>
    private static Font _latinFont = null!;

    /// <summary>
    /// A font that can draw Chinese, once one has been looked for.
    /// </summary>
    private static Font? _chineseFont;

    /// <summary>
    /// Whether a font that can draw Chinese has been looked for, so that a game with none does not search again for
    /// every piece of text it shows.
    /// </summary>
    private static bool _searchedForChinese;

    /// <summary>
    /// The font used for UI.
    ///
    /// The font above cannot draw Chinese: it is Perpetua, which the game keeps in its English font bundle, while a
    /// game running in Chinese draws its own text from a separate one. Handing it Chinese gives empty boxes rather
    /// than an error, so the Chinese wording of this mod would be there and unreadable.
    ///
    /// Asked for each time rather than settled once, because the language can be changed in the game's own options
    /// while it runs, and because it is not worth depending on whether fonts are loaded before the language is known.
    /// </summary>
    public static Font UIFontRegular => Lang.IsChinese ? FindChineseFont() ?? _latinFont : _latinFont;

    /// <summary>
    /// The font used for usernames above player objects. The game's own, which cannot draw Chinese - a name it
    /// cannot draw is handed to one of <see cref="NameFallbackFonts"/> instead.
    /// </summary>
    public static TMP_FontAsset InGameNameFont = null!;

    /// <summary>
    /// The faces found for names the game's own font cannot draw, or null until they have been looked for.
    /// </summary>
    private static TMP_FontAsset[]? _nameFallbackFonts;

    /// <summary>
    /// The face the chat's messages are drawn in. <see cref="DrawInChatFont"/> draws a text in it.
    /// </summary>
    private static TMPro.TMP_FontAsset _chatFont = null!;

    /// <summary>
    /// The face the chat's input is drawn in.
    /// </summary>
    public static TMPro.TMP_FontAsset ChatInputFont = null!;

    /// <summary>
    /// The faces that already have the others hung off them, so that it is done once each.
    /// </summary>
    private static readonly HashSet<TMP_FontAsset> FacesLeaningOnOthers = [];

    /// <summary>
    /// Pieces of the name of a TextMeshPro font that say it can draw simplified Chinese, in order of preference.
    /// Which of these the game has loaded depends on the language it is running in, so a player whose game is in
    /// English may have none of them and falls through to a font built from whatever can draw Chinese at all.
    /// </summary>
    private static readonly string[] NameFallbackNameHints = [
        "chinese",
        "japanese",
        "korean",
        "cjk"
    ];

    /// <summary>
    /// Pieces of the name of a TextMeshPro font that rule it out. The traditional faces sit next to the simplified
    /// one under names that differ by these letters alone, and a title face is cut for headings rather than for a
    /// name hanging over someone's head.
    /// </summary>
    private static readonly string[] NameFallbackNameAvoid = [
        "trad",
        "_tc",
        "title"
    ];

    /// <summary>
    /// Characters this mod draws, used to ask a font whether it can draw them rather than guessing from its name.
    /// The game's fonts are in compressed bundles, so their names cannot be known ahead of time.
    ///
    /// This used to be the single character 的, and that was not enough. The game loads the fonts of several
    /// languages at once, and the first one that could draw 的 was a Japanese face, so the wording came out drawn
    /// from two faces at two sizes in the same line. These are taken from the wording this mod actually shows.
    ///
    /// **This test can confirm a font and must never be trusted to reject one.** A dynamic font answers for every
    /// character here, whether the face holds it or borrows it: RequestCharactersInTexture puts the borrowed glyph
    /// in the atlas and HasCharacter reports what the atlas holds. The Japanese face passed all of these on both
    /// players' machines. That is why the name check in FindChineseFont runs first and this only backs it up.
    /// </summary>
    private const string ChineseSample = "的匹配大厅直连身份房间创建浏览公开进入你们双人存档连接退出加入输入已经开好";

    /// <summary>
    /// The colour emoji font every Windows has, in its fonts folder.
    /// </summary>
    private const string EmojiFontFile = "seguiemj.ttf";

    /// <summary>
    /// The fonts of Windows that the chat falls back on, in this order, for whatever neither its own face nor the
    /// emoji font can draw: the face in each file, and what it is there for. Kaomoji are made of the letters of many
    /// scripts - ಠ_ಠ, ༼ つ ◕_◕ ༽つ, ᕦ(ò_óˇ)ᕤ, (๑•̀ㅂ•́)و✧ - and with only those two to draw
    /// from, the chat showed a square for each letter of them (USER 10-12, "聊天框打颜文字的时候会有方块"). A file
    /// that a Windows lacks is left out; one is asked only for what everything before it could not draw.
    /// </summary>
    private static readonly (string File, int Face)[] ScriptFonts = [
        // Latin, Greek and Cyrillic beyond the plain ones, Arabic, Hebrew, the phonetic letters and accents
        ("segoeui.ttf", 0),
        // Symbols, arrows, shapes, box drawing, Braille
        ("seguisym.ttf", 0),
        // The scripts of India: ಠ. One file on Windows 11, another on Windows 10
        ("Nirmala.ttc", 0),
        ("Nirmala.ttf", 0),
        // Thai, Lao and Khmer: ๑
        ("LeelawUI.ttf", 0),
        // Canadian syllabics and Cherokee: ᕦ ᕤ
        ("gadugi.ttf", 0),
        // Tibetan: ༼ ༽
        ("himalaya.ttf", 0),
        // Ethiopic, N'Ko, Vai
        ("ebrima.ttf", 0),
        // Yi: ꉂ
        ("msyi.ttf", 0),
        // Javanese: ꧁ ꧂
        ("javatext.ttf", 0),
        // Korean
        ("malgun.ttf", 0),
        // Chinese and Japanese that the game's own face does not have
        ("msyh.ttc", 0),
        // Myanmar, Mongolian, and scripts no longer written such as runes
        ("mmrtext.ttf", 0),
        ("monbaiti.ttf", 0),
        ("seguihis.ttf", 0),
        // Cambria Math, the second face in its file: the letters of mathematics, 𝓪
        ("cambria.ttc", 1)
    ];

    /// <summary>
    /// The fonts to ask Windows for if the game turns out to have none that can draw Chinese, in order of preference.
    /// </summary>
    private static readonly string[] ChineseOsFonts = [
        "Microsoft YaHei UI",
        "Microsoft YaHei",
        "SimHei",
        "SimSun",
        "NSimSun"
    ];

    /// <summary>
    /// Pieces of a font name that say it is the simplified Chinese face, in order of preference. The game loads the
    /// fonts of several languages at once and hands them over in no useful order, so the first one that passes a
    /// glyph test is not the right one: it was a Japanese face on both players' machines, while
    /// NotoSerifCJKsc-Regular sat further down that very same list.
    /// </summary>
    private static readonly string[] ChineseFontNameHints = [
        "cjksc",
        "hanssc",
        "hanserifsc",
        "sanssc",
        "serifsc"
    ];

    /// <summary>
    /// Pieces of a font name that rule it out however promising the rest of the name looks. The traditional,
    /// Japanese and Korean faces of the same families are loaded next to the simplified one and their names differ
    /// by these few letters alone.
    /// </summary>
    private static readonly string[] ChineseFontNameAvoid = [
        "cjktc",
        "cjkjp",
        "cjkkr",
        "seriftc",
        "sanstc",
        "tc-",
        "jp-",
        "kr-"
    ];

    /// <summary>
    /// Load the fonts by trying to find them in the game through Unity.
    /// </summary>
    public static void LoadFonts() {
        Logger.Info("Loading fonts...");

        foreach (var font in UnityEngine.Resources.FindObjectsOfTypeAll<Font>()) {
            // Logged because which fonts a game has loaded depends on the language it is set to, and they arrive in
            // compressed bundles that cannot be read from outside the game. A log says what was really there.
            Logger.Info($"Font: {font.name}");

            switch (font.name) {
                case "Perpetua":
                    _latinFont = font;
                    break;
            }
        }

        foreach (var textMeshProFont in UnityEngine.Resources.FindObjectsOfTypeAll<TMP_FontAsset>()) {
            Logger.Info($"TMP_FontAsset: {textMeshProFont.name}");

            switch (textMeshProFont.name) {
                case "TrajanPro-Bold SDF":
                    InGameNameFont = textMeshProFont;
                    break;
            }
        }

        if (_latinFont == null) {
            Logger.Error("UI font regular is missing!");
        }

        if (InGameNameFont == null) {
            Logger.Error("In-game name font is missing!");
        }

        BuildChatFonts();
    }

    /// <summary>
    /// The font to draw one name in: the game's own where it can draw the whole name, and otherwise whichever of
    /// the game's other faces can draw the most of it.
    ///
    /// Chosen against the name in hand rather than once and for all, because the game's faces are cut down to the
    /// characters the game itself shows and a player's name is not something it ever showed. A face that holds one
    /// name may hold none of the next. Where no single face holds all of a name, the rest are hung off the one that
    /// holds the most and TextMeshPro takes each character from whichever of them has it.
    /// </summary>
    /// <param name="name">The name as it will be drawn.</param>
    /// <returns>The font to draw it in.</returns>
    public static TMP_FontAsset PickInGameNameFont(string name) {
        if (string.IsNullOrEmpty(name) || CountMissing(InGameNameFont, name) == 0) {
            return InGameNameFont;
        }

        TMP_FontAsset? best = null;
        var fewestMissing = int.MaxValue;
        foreach (var candidate in NameFallbackFonts) {
            var missing = CountMissing(candidate, name);
            if (missing >= fewestMissing) {
                continue;
            }

            fewestMissing = missing;
            best = candidate;

            if (missing == 0) {
                break;
            }
        }

        if (best == null) {
            return InGameNameFont;
        }

        if (fewestMissing > 0) {
            LeanOnTheOtherFaces(best, name, fewestMissing);
        }

        return best;
    }

    /// <summary>
    /// How many characters of a name a font has no glyph for. Whitespace is not counted: nothing is drawn for it,
    /// and a face that has no space still draws the name around it.
    /// </summary>
    /// <param name="font">The font to ask.</param>
    /// <param name="name">The name to draw.</param>
    /// <returns>How many characters it lacks, or none when asking it threw.</returns>
    private static int CountMissing(TMP_FontAsset font, string name) {
        if (font == null) {
            return 0;
        }

        var missing = 0;
        try {
            foreach (var character in name) {
                if (char.IsWhiteSpace(character)) {
                    continue;
                }

                if (!font.HasCharacter(character)) {
                    missing++;
                }
            }
        } catch (Exception e) {
            // Treated as drawable, because a name in the wrong face is better than no name at all
            Logger.Warn($"Could not ask '{font.name}' whether it can draw a name: {e.Message}");

            return 0;
        }

        return missing;
    }

    /// <summary>
    /// Hangs every other face off one, so that TextMeshPro can take a character the first lacks from a later one.
    ///
    /// Done once per face and only when a name needs it. It adds to what a face can draw and takes nothing away, so
    /// the game's own text is either unchanged or a box less.
    /// </summary>
    /// <param name="face">The face to hang the others off.</param>
    /// <param name="name">The name that needed it, for the one line this writes.</param>
    /// <param name="missing">How many characters that face lacked.</param>
    private static void LeanOnTheOtherFaces(TMP_FontAsset face, string name, int missing) {
        if (!FacesLeaningOnOthers.Add(face)) {
            return;
        }

        try {
            face.fallbackFontAssets ??= [];
            foreach (var other in NameFallbackFonts) {
                if (other != null && other != face && !face.fallbackFontAssets.Contains(other)) {
                    face.fallbackFontAssets.Add(other);
                }
            }

            Logger.Info(
                $"No face the game has loaded holds all of '{name}' - '{face.name}' is short of {missing} of it, " +
                "so the others are hung off it to fill the rest in"
            );
        } catch (Exception e) {
            Logger.Warn($"Could not hang the other faces off '{face.name}': {e.Message}");
        }
    }

    /// <summary>
    /// The faces to draw names the game's own name font cannot, in the order they are preferred. Looked for once,
    /// because a game that has none will not grow one.
    /// </summary>
    private static TMP_FontAsset[] NameFallbackFonts {
        get {
            if (_nameFallbackFonts != null) {
                return _nameFallbackFonts;
            }

            var preferred = new List<TMP_FontAsset>();
            var rest = new List<TMP_FontAsset>();

            foreach (var hint in NameFallbackNameHints) {
                foreach (var asset in UnityEngine.Resources.FindObjectsOfTypeAll<TMP_FontAsset>()) {
                    if (asset == null || preferred.Contains(asset) || rest.Contains(asset)) {
                        continue;
                    }

                    var assetName = asset.name.ToLowerInvariant();
                    if (!assetName.Contains(hint)) {
                        continue;
                    }

                    var avoided = false;
                    foreach (var avoid in NameFallbackNameAvoid) {
                        if (assetName.Contains(avoid)) {
                            avoided = true;
                            break;
                        }
                    }

                    // The ones named against are still kept, at the back. They are the wrong face to draw a whole
                    // name in, but a character the right face lacks has to come from somewhere.
                    (avoided ? rest : preferred).Add(asset);
                }
            }

            preferred.AddRange(rest);
            _nameFallbackFonts = preferred.ToArray();

            if (_nameFallbackFonts.Length == 0) {
                // Nothing can be built to stand in: the text over a player's head is drawn by TextMeshPro, which
                // takes only its own kind of font, and the version of it the game carries cannot make one at run
                // time. So this machine draws these names as boxes, and this says so.
                Logger.Error(
                    "Found no face for names the game's own font cannot draw, so they stay as boxes on this machine"
                );
            } else {
                var names = new List<string>();
                foreach (var asset in _nameFallbackFonts) {
                    names.Add(asset.name);
                }

                Logger.Info($"Faces for names the game's name font cannot draw: {string.Join(", ", names)}");
            }

            return _nameFallbackFonts;
        }
    }

    /// <summary>
    /// Draws a text in the face the chat's messages use, with the black outline that keeps them readable over a
    /// bright room.
    /// </summary>
    /// <param name="text">The text to draw in it.</param>
    public static void DrawInChatFont(TMPro.TMP_Text text) {
        text.font = _chatFont;
        text.OnPreRenderText += FitOldOutlineShader;
    }

    /// <summary>
    /// Builds the chat's faces. They are TextMeshPro's - the one that comes with Unity, not the old copy the
    /// game draws its own text with - because the chat has to show emoji and a Unity text cannot: it draws nothing
    /// its face lacks, and the game has no face with emoji in. This TextMeshPro builds its faces while the game runs,
    /// so it can take them from the colour emoji font of Windows.
    ///
    /// Two faces out of the same font. The messages are drawn from how far each point is from a letter's edge, which
    /// is what lets them keep their black outline. The input is drawn as plain pixels at the size it is shown: an
    /// input field ends its text with a character that has no width, and the outline drew that one as a speck after
    /// the last letter.
    /// </summary>
    private static void BuildChatFonts() {
        // The game never uses this TextMeshPro, so the settings every one of its texts reads as it is created were
        // never built into it. A text made before they exist stops on a null halfway through setting itself up and
        // never draws anything, which is why this runs with the loading of the fonts, before any of the interface is
        // made. What is read unchecked is filled in: the tables of where a line may break and the lists of faces to
        // fall back on, empty, and the face a new text starts out in
        var settings = ScriptableObject.CreateInstance<TMPro.TMP_Settings>();

        void Set(string field, object value) => typeof(TMPro.TMP_Settings)
            .GetField(field, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(settings, value);

        Set("s_Instance", settings);
        Set("m_leadingCharacters", new TextAsset(""));
        Set("m_followingCharacters", new TextAsset(""));
        Set("m_fallbackFontAssets", new List<TMPro.TMP_FontAsset>());
        Set("m_EmojiFallbackTextAssets", new List<TMPro.TMP_Asset>());
        Set("m_enableKerning", true);

        // How many pixels tall the chat's text is on this screen, so that what is drawn as plain pixels is drawn at
        // its own size rather than stretched to it
        var pixelSize = Mathf.CeilToInt(UiManager.ChatFontSize * Screen.height / 1080f);
        var face = UIFontRegular;

        _chatFont = TMPro.TMP_FontAsset.CreateFontAsset(
            face, 48, 6, GlyphRenderMode.SDFAA, 1024, 1024, TMPro.AtlasPopulationMode.Dynamic, true
        );
        OutlineMessageFace(_chatFont.material);

        ChatInputFont = TMPro.TMP_FontAsset.CreateFontAsset(
            face, pixelSize, 4, GlyphRenderMode.SMOOTH, 1024, 1024, TMPro.AtlasPopulationMode.Dynamic, true
        );
        Set("m_defaultFontAsset", ChatInputFont);

        // What the face cannot draw is drawn from the fonts of Windows: the emoji first, then the other scripts. Each
        // of these makes its texture only once it has something to draw in it
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        List<TMPro.TMP_FontAsset> messageFallbacks = [], inputFallbacks = [];

        var emoji = TMPro.TMP_FontAsset.CreateFontAsset(
            Path.Combine(fonts, EmojiFontFile), 0, pixelSize, 4, GlyphRenderMode.COLOR, 1024, 1024
        );
        if (emoji == null) {
            Logger.Warn($"Could not load '{EmojiFontFile}', so the chat cannot show emoji");
        } else {
            messageFallbacks.Add(emoji);
            inputFallbacks.Add(emoji);
        }

        var missing = new List<string>();
        foreach (var (file, faceIndex) in ScriptFonts) {
            var path = Path.Combine(fonts, file);
            if (!File.Exists(path)) {
                missing.Add(file);
                continue;
            }

            // Drawn the way the face they stand in for is drawn: the messages from the distance to the edges of the
            // letters, with the same outline, and the input as plain pixels at its own size. A font that cannot be
            // read is left out, and the rest still come
            TMPro.TMP_FontAsset? message, input;
            try {
                message = TMPro.TMP_FontAsset.CreateFontAsset(
                    path, faceIndex, 48, 6, GlyphRenderMode.SDFAA, 512, 512
                );
                input = TMPro.TMP_FontAsset.CreateFontAsset(
                    path, faceIndex, pixelSize, 4, GlyphRenderMode.SMOOTH, 512, 512
                );
            } catch (Exception e) {
                Logger.Warn($"Could not load '{file}' for the chat: {e.Message}");
                continue;
            }

            if (message == null || input == null) {
                missing.Add(file);
                continue;
            }

            OutlineMessageFace(message.material);
            messageFallbacks.Add(message);
            inputFallbacks.Add(input);
        }

        if (missing.Count > 0) {
            Logger.Info(
                $"The chat falls back on {inputFallbacks.Count} fonts; not found: {string.Join(", ", missing)}"
            );
        }

        _chatFont.fallbackFontAssetTable = messageFallbacks;
        ChatInputFont.fallbackFontAssetTable = inputFallbacks;
    }

    /// <summary>
    /// Gives a face of the chat's messages its black outline, drawn by the game's own shader for faces made of the
    /// distance to the edges of the letters.
    /// </summary>
    /// <param name="material">The material of the face.</param>
    private static void OutlineMessageFace(Material material) {
        material.shader = Shader.Find("TextMeshPro/Distance Field");
        material.SetFloat(TMPro.ShaderUtilities.ID_OutlineWidth, 0.2f);
        material.SetColor(TMPro.ShaderUtilities.ID_OutlineColor, Color.black);
        // An outline takes as much from inside the letters as it adds around them, so the letters are widened by as
        // much again to keep their weight
        material.SetFloat(TMPro.ShaderUtilities.ID_FaceDilate, 0.2f);
        TMPro.ShaderUtilities.UpdateShaderRatios(material);
    }

    /// <summary>
    /// Fits a text in the chat's message face to the shader it is drawn with. The game carries the shaders of an
    /// older TextMeshPro, which reads how sharp a letter's edge is from the second set of texture coordinates, where
    /// this one writes where its face texture sits instead. The older reading is put back just before the text is
    /// handed on to be drawn; without it the letters came out as rows of spikes.
    /// </summary>
    /// <param name="textInfo">The text as it is about to be drawn.</param>
    private static void FitOldOutlineShader(TMPro.TMP_TextInfo textInfo) {
        foreach (var meshInfo in textInfo.meshInfo) {
            for (var i = 0; i < meshInfo.vertexCount; i++) {
                meshInfo.uvs2[i].y = meshInfo.uvs0[i].w;
            }
        }
    }

    /// <summary>
    /// A font that can draw Chinese: one the game has already loaded if there is one, and otherwise one asked of
    /// Windows, which always has at least one of these. Looked for once, because a game that has none will not grow
    /// one, and the fallback does not need the game to provide anything.
    /// </summary>
    /// <returns>A font that can draw Chinese, or null if neither the game nor Windows offered one.</returns>
    private static Font? FindChineseFont() {
        if (_searchedForChinese) {
            return _chineseFont;
        }

        _searchedForChinese = true;

        // By name first, and only then by asking a font what it can draw. Asking cannot tell these apart: the
        // game's Chinese fonts are dynamic, so RequestCharactersInTexture makes Unity fill whatever the face
        // itself lacks out of a fallback, and HasCharacter then answers yes for every character. That is the very
        // thing that looks wrong on screen - the borrowed glyphs come from another face at another size, which is
        // the "some characters are bigger than others" this is meant to stop. A Japanese face passed all of the
        // test characters on both players' machines, so the test below can confirm a font but never reject one.
        foreach (var hint in ChineseFontNameHints) {
            foreach (var font in UnityEngine.Resources.FindObjectsOfTypeAll<Font>()) {
                var name = font.name.ToLowerInvariant();
                if (!name.Contains(hint)) {
                    continue;
                }

                var avoided = false;
                foreach (var avoid in ChineseFontNameAvoid) {
                    if (name.Contains(avoid)) {
                        avoided = true;
                        break;
                    }
                }

                if (avoided) {
                    continue;
                }

                Logger.Info($"Drawing Chinese with the game's simplified Chinese font: {font.name}");
                _chineseFont = font;

                return _chineseFont;
            }
        }

        foreach (var font in UnityEngine.Resources.FindObjectsOfTypeAll<Font>()) {
            var missing = FirstCharacterMissing(font);
            if (missing != null) {
                // Logged because which font is picked here decides whether the wording can be read at all, and
                // nothing used to say why one was passed over or taken
                Logger.Info(
                    $"Not drawing Chinese with the game's font '{font.name}' (dynamic: {font.dynamic}): it has " +
                    $"no '{missing}'"
                );
                continue;
            }

            Logger.Info($"Drawing Chinese with the game's own font: {font.name} (dynamic: {font.dynamic})");
            _chineseFont = font;

            return _chineseFont;
        }

        foreach (var name in ChineseOsFonts) {
            var font = Font.CreateDynamicFontFromOSFont(name, 16);
            if (font == null || FirstCharacterMissing(font) != null) {
                continue;
            }

            Logger.Info($"Drawing Chinese with a font from Windows: {name}");
            _chineseFont = font;

            return _chineseFont;
        }

        Logger.Error("Found no font that can draw Chinese, so this mod's text stays in the game's own font");

        return null;
    }

    /// <summary>
    /// The first character of <see cref="ChineseSample"/> a font cannot draw, or null if it can draw all of them. A
    /// font that answers by throwing is treated as one that cannot draw the first, because asking is only worth
    /// doing while it cannot break showing the text at all.
    /// </summary>
    /// <param name="font">The font to ask.</param>
    /// <returns>The first character the font lacks, or null if it lacks none.</returns>
    private static char? FirstCharacterMissing(Font font) {
        try {
            // A dynamic font only builds the glyphs asked of it, so it has to be asked before it can answer.
            if (font.dynamic) {
                font.RequestCharactersInTexture(ChineseSample);
            }

            foreach (var character in ChineseSample) {
                if (!font.HasCharacter(character)) {
                    return character;
                }
            }

            return null;
        } catch {
            return ChineseSample[0];
        }
    }
}
