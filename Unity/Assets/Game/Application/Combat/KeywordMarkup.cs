using System.Collections.Generic;
using System.Linq;
using System.Text;
using Ashen.App.Ui;
using S = Ashen.Generated.StringKeys;

namespace Ashen.App.Combat
{
    /// <summary>
    /// The glossary words of a text as UI Toolkit rich text (web tooltipGlossary.js decorate: <c>.tooltip-keyword</c>, styles/kit.css:3965 gold with a dotted underline): each is a link whose id is the word in lower
    /// case, so the label's link tag events can say which word the pointer is over (<c>Tooltip.AttachKeywords</c>), in the gold, underlined. Everything else is plain text with any angle bracket made safe. Engine-free.
    /// </summary>
    public static class KeywordMarkup
    {
        /// <summary>True when the text has a glossary word in it.</summary>
        public static bool Any(IReadOnlyList<GlossarySegment> segments) => segments.Any(s => s.Entry != null);

        /// <summary>The text as rich text; <paramref name="colour"/> is a #rrggbb value.</summary>
        public static string Rich(CardTextData t, IReadOnlyList<GlossarySegment> segments, string colour)
        {
            var output = new StringBuilder();
            foreach (var segment in segments)
            {
                var safe = segment.Text.Replace(t.Word(S.CardtextAngleOpen), t.Word(S.CardtextAngleOpenSafe));
                if (segment.Entry == null)
                {
                    output.Append(safe);
                    continue;
                }
                output.Append(t.Say(S.CardtextRichLinkOpen, segment.Key)).Append(t.Say(S.CardtextRichColourOpen, colour)).Append(t.Word(S.CardtextRichUnderlineOpen))
                    .Append(safe)
                    .Append(t.Word(S.CardtextRichUnderlineClose)).Append(t.Word(S.CardtextRichColourClose)).Append(t.Word(S.CardtextRichLinkClose));
            }
            return output.ToString();
        }

        /// <summary>The explanation of each word the segments carry, by link id (the word in lower case).</summary>
        public static IReadOnlyDictionary<string, string> Explanations(IReadOnlyList<GlossarySegment> segments)
        {
            var map = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (var segment in segments)
                if (segment.Entry != null && !map.ContainsKey(segment.Key)) map[segment.Key] = segment.Entry.Explanation;
            return map;
        }
    }
}
