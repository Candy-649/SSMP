using TeamCherry.Localization;

namespace SSMP.Util;

/// <summary>
/// Picks the wording for the language the game itself is set to, so that a player reading the game in Chinese reads
/// this mod in Chinese as well, and everyone else still reads it in English.
///
/// Both wordings sit next to each other at the place they are used rather than in a table of keys somewhere else.
/// With this many lines, a table would mostly be an opportunity for the two halves to drift apart, and reading a
/// prompt in a diff would mean looking up a key to find out what it actually says.
/// </summary>
public static class Lang {
    /// <summary>
    /// Whether the game is being read in Chinese. Asked each time rather than worked out once, because the language
    /// can be changed in the game's own options while it runs.
    ///
    /// The members are matched rather than the name they print, which is what the game itself compares: the name
    /// costs a new string every time it is asked for, and one of these is asked for every frame a prompt is on
    /// screen. There is more than one Chinese, and leaving any of them out would quietly hand that player English.
    /// </summary>
    public static bool IsChinese => Language.CurrentLanguage() is
        LanguageCode.ZH or LanguageCode.ZH_TW or LanguageCode.ZH_HK or LanguageCode.ZH_CN or LanguageCode.ZH_SG;

    /// <summary>
    /// The wording to show for a line that this mod writes.
    /// </summary>
    /// <param name="english">The English wording.</param>
    /// <param name="chinese">The Chinese wording.</param>
    /// <returns>Whichever of the two suits the language the game is set to.</returns>
    public static string Pick(string english, string chinese) => IsChinese ? chinese : english;
}
