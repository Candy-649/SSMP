using System;
using TMProOld;
using UnityEngine;
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
    /// cannot draw is handed to <see cref="InGameNameFallbackFont"/> instead, one whole name at a time.
    /// </summary>
    public static TMP_FontAsset InGameNameFont = null!;

    /// <summary>
    /// Whether a font for names the game's own cannot draw has been looked for yet.
    /// </summary>
    private static bool _searchedForNameFallback;

    /// <summary>
    /// The font found for names the game's own cannot draw, or null if there is none.
    /// </summary>
    private static TMP_FontAsset? _nameFallbackFont;

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
    }

    /// <summary>
    /// The font to draw one name in: the game's own where it can draw the whole name, and otherwise one that can.
    ///
    /// Picked for the whole name rather than per character, because a name drawn out of two faces comes out at two
    /// sizes on one line - the very thing the search for a Chinese font below goes to lengths to avoid.
    /// </summary>
    /// <param name="name">The name as it will be drawn.</param>
    /// <returns>The font to draw it in.</returns>
    public static TMP_FontAsset PickInGameNameFont(string name) {
        if (string.IsNullOrEmpty(name) || CanDrawWholeName(InGameNameFont, name)) {
            return InGameNameFont;
        }

        return InGameNameFallbackFont ?? InGameNameFont;
    }

    /// <summary>
    /// A font for names the game's own cannot draw. Looked for once, because a game that has none will not grow one.
    /// </summary>
    private static TMP_FontAsset? InGameNameFallbackFont {
        get {
            if (_searchedForNameFallback) {
                return _nameFallbackFont;
            }

            _searchedForNameFallback = true;
            _nameFallbackFont = FindNameFallbackFont();

            return _nameFallbackFont;
        }
    }

    /// <summary>
    /// Whether a font holds every character of a name that is actually drawn.
    /// </summary>
    /// <param name="font">The font to ask.</param>
    /// <param name="name">The name to draw.</param>
    /// <returns><see langword="true"/> when it can draw all of them; otherwise <see langword="false"/>.</returns>
    private static bool CanDrawWholeName(TMP_FontAsset font, string name) {
        if (font == null) {
            return true;
        }

        try {
            foreach (var character in name) {
                // Nothing is drawn for a space, and a font that has no space still draws the name around it
                if (char.IsWhiteSpace(character)) {
                    continue;
                }

                if (!font.HasCharacter(character)) {
                    return false;
                }
            }
        } catch (Exception e) {
            // Treated as drawable, because a name in the wrong font is better than no name at all
            Logger.Warn($"Could not ask '{font.name}' whether it can draw a name: {e.Message}");

            return true;
        }

        return true;
    }

    /// <summary>
    /// Finds a font for names the game's own cannot draw.
    /// </summary>
    /// <returns>The font, or null if this machine has nothing that can draw them.</returns>
    private static TMP_FontAsset? FindNameFallbackFont() {
        // By name first, for the reason the search below gives: the game loads the faces of several languages at
        // once and hands them over in no useful order, so the first one that can draw a character is not the right
        // one.
        foreach (var hint in NameFallbackNameHints) {
            foreach (var asset in UnityEngine.Resources.FindObjectsOfTypeAll<TMP_FontAsset>()) {
                if (asset == null) {
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

                if (avoided) {
                    continue;
                }

                Logger.Info($"Drawing names the game's name font cannot with: {asset.name}");

                return asset;
            }
        }

        // Nothing can be built here to stand in. The mod's own wording falls back on a font asked of Windows, but
        // that is a plain font and the text over a player's head is drawn by TextMeshPro, which takes only its own
        // kind - and the version of it the game carries cannot make one at run time. So a machine whose game has
        // loaded no face for these characters draws them as boxes, and this says which machine that was.
        Logger.Error(
            "Found no font for names the game's own font cannot draw, so they stay as boxes on this machine"
        );

        return null;
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
