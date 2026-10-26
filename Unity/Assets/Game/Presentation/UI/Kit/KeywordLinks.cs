using Ashen.App.Combat;
using Ashen.App.Ui;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// The glossary's words in a text, as hover tips (web tooltipGlossary.js decorateKeywords; P-7d, D-1773): each occurrence becomes a gold underlined <c>&lt;link&gt;</c> of the label's rich text and the label gets
    /// one tooltip that says the explanation of whichever word the pointer is over (<see cref="Tooltip.AttachKeywords"/>, the link tag events of D-1592). The tooltip layer, size and delay are the board's
    /// (<see cref="ChipTips"/>). A label with no glossary word is left as it is.
    /// </summary>
    public static class KeywordLinks
    {
        /// <summary>Decorates <paramref name="label"/>'s text. True when it had a word to decorate.</summary>
        public static bool Decorate(Label label, TermGlossary glossary, CardTextData words, string colour)
        {
            if (label == null || glossary == null || words == null || ChipTips.Overlay == null || string.IsNullOrEmpty(label.text)) return false;
            var segments = glossary.Segments(label.text);
            if (!KeywordMarkup.Any(segments)) return false;
            label.text = KeywordMarkup.Rich(words, segments, colour);
            label.pickingMode = PickingMode.Position;
            Tooltip.AttachKeywords(label, ChipTips.Overlay, ChipTips.Size, ChipTips.DelayMs, KeywordMarkup.Explanations(segments));
            return true;
        }
    }
}
