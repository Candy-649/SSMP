using System;
using System.Text;
using UnityEngine.UI;

namespace SSMP.Ui.Util;

/// <summary>
/// Gives a text the places to break a line that Chinese has, for the text of Unity's own UI (Text). Unity breaks a line
/// only at a space, or else where a word runs past the edge, and a word that doesn't fit after the last space of a line
/// goes down to the next line whole. Chinese has no spaces, so a stretch of it that comes after a space, as after a name
/// written in English, was one word that went down whole, and left the line above it short (USER 10-25:
/// "文字排版好像有问题", on the note of the outlines, after "来自另一个模组 Player Color，作者").
///
/// Each place where Chinese may break a line gets a space of the smallest size, a pixel high, which Unity breaks a line
/// at and which takes next to no room: between two Chinese characters, and between a Chinese one and one of another
/// script, but not before the marks that never begin a line (，。、；：？！ and the closing brackets and quotes), nor
/// after those that never end one (the opening brackets and quotes). A word in English is kept whole, as it was.
/// </summary>
internal static class ChineseLineBreaks {
    /// <summary>
    /// The space of the smallest size, where Unity may break a line. Unity draws a size of 0 as the tag's own letters.
    /// </summary>
    private const string Break = "<size=1> </size>";

    /// <summary>
    /// The marks that never begin a line.
    /// </summary>
    private const string NeverFirst = "，。、；：？！）》」』】〉〕〗～％…—·’”,.;:?!)]}%";

    /// <summary>
    /// The marks that never end a line.
    /// </summary>
    private const string NeverLast = "（《「『【〈〔〖‘“([{";

    /// <summary>
    /// The tags of Unity's rich text, which are passed over as they are: nothing is put inside a tag, and the
    /// characters on either side of one are the ones that say whether a line may break there.
    /// </summary>
    private static readonly string[] TagNames = ["b", "i", "size", "color", "material", "quad"];

    /// <summary>
    /// The text that a Text of the game's own is given: marked, where it draws rich text, which the marks are, in a
    /// dynamic font, the only kind that Unity draws a size of rich text in (the others would draw each mark as a whole
    /// space). The game's fonts for Chinese are dynamic (see Resources.FontManager).
    /// </summary>
    /// <param name="component">The Text that shows it.</param>
    /// <param name="text">The text.</param>
    /// <returns>The text to give it.</returns>
    public static string MarkFor(Text component, string text) =>
        component.supportRichText && component.font != null && component.font.dynamic ? Mark(text) : text;

    /// <summary>
    /// The text with the places where Chinese may break a line marked, or the text itself where there are none, as in
    /// a text that has no Chinese. Marking a text that is marked already gives it back as it is, as the marks are
    /// spaces, which a line already breaks at.
    /// </summary>
    /// <param name="text">The text, which may hold tags of rich text.</param>
    /// <returns>The text to show.</returns>
    public static string Mark(string text) {
        if (string.IsNullOrEmpty(text)) {
            return text;
        }

        StringBuilder? marked = null;
        // How much of the text is in what is marked so far
        var copied = 0;
        // The last character that is drawn, or none at the start of a line
        var previous = '\0';

        for (var i = 0; i < text.Length; i++) {
            var character = text[i];
            if (character == '<') {
                var tagLength = TagLength(text, i);
                if (tagLength > 0) {
                    i += tagLength - 1;
                    continue;
                }
            }

            if (MayBreakBetween(previous, character)) {
                marked ??= new StringBuilder(text.Length * 4);
                marked.Append(text, copied, i - copied).Append(Break);
                copied = i;
            }

            previous = character == '\n' ? '\0' : character;
        }

        return marked == null ? text : marked.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>
    /// Whether Chinese may break a line between two characters that are drawn one after the other.
    /// </summary>
    private static bool MayBreakBetween(char before, char after) =>
        before != '\0' &&
        !char.IsWhiteSpace(before) && !char.IsWhiteSpace(after) &&
        (IsChinese(before) || IsChinese(after)) &&
        NeverFirst.IndexOf(after) < 0 &&
        NeverLast.IndexOf(before) < 0;

    /// <summary>
    /// Whether a character is one of the scripts and marks of Chinese and Japanese, which a line may break on either
    /// side of. Korean is left out, as it breaks its lines at spaces, as English does.
    /// </summary>
    private static bool IsChinese(char character) =>
        character is >= '⺀' and <= '⿟' // radicals
            or >= '　' and <= '〿' // marks: 、。「」
            or >= '぀' and <= 'ヿ' // kana
            or >= '㄀' and <= 'ㄯ' // bopomofo
            or >= 'ㆠ' and <= 'ㇿ' // more bopomofo, strokes, more kana
            or >= '㈀' and <= '㏿' // enclosed and squared
            or >= '㐀' and <= '䶿' // characters, extension A
            or >= '一' and <= '鿿' // characters
            or >= '豈' and <= '﫿' // compatibility characters
            or >= '︰' and <= '﹏' // compatibility forms
            or >= '＀' and <= '￯'; // full width: ，：（）

    /// <summary>
    /// How long the tag of rich text is that starts at a place in a text, or 0 where none starts there: a known name
    /// after '&lt;' or '&lt;/', then nothing, a value after '=' or the settings of a quad after a space, up to the
    /// next '&gt;' on the same line.
    /// </summary>
    private static int TagLength(string text, int start) {
        var nameStart = start + 1;
        if (nameStart < text.Length && text[nameStart] == '/') {
            nameStart++;
        }

        var nameEnd = nameStart;
        while (nameEnd < text.Length && char.IsLetter(text[nameEnd])) {
            nameEnd++;
        }

        if (nameEnd == nameStart || nameEnd >= text.Length || !IsTagName(text, nameStart, nameEnd - nameStart)) {
            return 0;
        }

        if (text[nameEnd] != '>' && text[nameEnd] != '=' && text[nameEnd] != ' ') {
            return 0;
        }

        for (var i = nameEnd; i < text.Length; i++) {
            switch (text[i]) {
                case '>':
                    return i - start + 1;
                case '<' or '\n':
                    return 0;
            }
        }

        return 0;
    }

    /// <summary>
    /// Whether the given part of a text is the name of a tag of rich text.
    /// </summary>
    private static bool IsTagName(string text, int start, int length) {
        foreach (var name in TagNames) {
            if (name.Length == length &&
                string.Compare(text, start, name, 0, length, StringComparison.OrdinalIgnoreCase) == 0) {
                return true;
            }
        }

        return false;
    }
}
