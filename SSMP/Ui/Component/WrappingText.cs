using SSMP.Ui.Util;
using UnityEngine.UI;

namespace SSMP.Ui.Component;

/// <summary>
/// A text of this mod's own UI that breaks its lines of Chinese where Chinese breaks them (see
/// <see cref="ChineseLineBreaks"/>), whatever gives it its text. What it is given is marked once and kept, so that
/// giving it the same text again changes nothing, as with Unity's own Text: some texts are given theirs at every frame.
/// What it reads back is the marked text, which Unity measures and draws.
/// </summary>
internal class WrappingText : Text {
    /// <summary>
    /// The text it was last given, and that text marked.
    /// </summary>
    private string? _given;
    private string? _marked;

    /// <inheritdoc />
    public override string text {
        get => base.text;
        set {
            if (value != _given) {
                _given = value;
                _marked = supportRichText ? ChineseLineBreaks.Mark(value) : value;
            }

            base.text = _marked;
        }
    }
}
