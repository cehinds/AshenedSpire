using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ashen.Generated;
using S = Ashen.Generated.StringKeys;

namespace Ashen.App.Combat
{
    /// <summary>The colours and marks the rich text of a card's rules draws its numbers with.</summary>
    public sealed class CardMarkupStyle
    {
        /// <summary>The colour (a #rrggbb value) of a number the preview raised and of one it lowered, as the web's .val.up and .val.down.</summary>
        public string Up;
        public string Down;

        /// <summary>The lines held back until the card is selected are written too.</summary>
        public bool ShowExtra;

        /// <summary>A damage number is a link (id <see cref="ReceiptLink"/>) with an underline: it opens the damage calculation (the web's [data-damage-receipt]).</summary>
        public bool Receipt;
    }

    /// <summary>
    /// The html of a card's rules (the web's <c>face-line</c> and <c>val</c> spans) as UI Toolkit rich text: one line per face-line, a number the preview moved off its
    /// printed value tinted and bold (up green, down red), a damage number of a card with a damage calculation underlined and linked. Anything else in a span is plain text.
    /// </summary>
    public static class CardFaceMarkup
    {
        /// <summary>The id of the link a damage number carries.</summary>
        public static string ReceiptLink(CardTextData t) => t.Word(S.CardtextReceiptLink);

        /// <summary>Plain text of the rules (for a tooltip or a label that draws no rich text): the lines joined by the separator, the markup removed.</summary>
        public static string Plain(CardTextData t, string html, bool showExtra = true)
        {
            var rich = ToRich(t, html, new CardMarkupStyle { ShowExtra = showExtra });
            return Regex.Replace(rich, t.Word(S.CardtextRichTagPattern), string.Empty);
        }

        public static string ToRich(CardTextData t, string html, CardMarkupStyle style)
        {
            if (string.IsNullOrEmpty(html)) return string.Empty;
            var tag = new Regex(t.Word(S.CardtextSpanPattern), RegexOptions.CultureInvariant);
            var output = new StringBuilder();
            // Each open span: what its close writes, and whether its text is skipped.
            var open = new Stack<KeyValuePair<string, bool>>();
            var skipping = 0;
            var last = 0;
            var lineOpen = false;
            var damageLine = false;
            foreach (Match m in tag.Matches(html))
            {
                if (skipping == 0) output.Append(Text(t, html.Substring(last, m.Index - last)));
                last = m.Index + m.Length;
                if (m.Groups[1].Value.Length > 0)
                {
                    if (open.Count == 0) continue;
                    var closed = open.Pop();
                    if (closed.Value) skipping--;
                    else if (skipping == 0) output.Append(closed.Key);
                    continue;
                }
                var classes = m.Groups[Ashen.Generated.P1Count.Pair].Value;
                var attributes = m.Groups[Ashen.Generated.P1Count.Triple].Value;
                var isLine = HasClass(t, classes, S.CardtextFaceLineClass);
                if (isLine)
                {
                    var held = !style.ShowExtra && attributes.Contains(t.Word(S.CardtextExtraMark));
                    if (held) { open.Push(new KeyValuePair<string, bool>(string.Empty, true)); skipping++; continue; }
                    if (lineOpen) output.Append(t.Word(S.CardtextLineBreak));
                    lineOpen = true;
                    damageLine = attributes.Contains(t.Word(S.CardtextDamageKindMark));
                    open.Push(new KeyValuePair<string, bool>(string.Empty, false));
                    continue;
                }
                var close = string.Empty;
                if (skipping == 0 && HasClass(t, classes, S.CardtextClassVal))
                {
                    var up = HasClass(t, classes, S.CardtextUpClass);
                    var down = HasClass(t, classes, S.CardtextDownClass);
                    var link = style.Receipt && damageLine;
                    var colour = up ? style.Up : down ? style.Down : null;
                    var prefix = string.Empty;
                    if (link) { prefix += t.Say(S.CardtextRichLinkOpen, ReceiptLink(t)) + t.Word(S.CardtextRichUnderlineOpen); close = t.Word(S.CardtextRichUnderlineClose) + t.Word(S.CardtextRichLinkClose); }
                    if (colour != null) { prefix += t.Say(S.CardtextRichColourOpen, colour) + t.Word(S.CardtextRichBoldOpen); close = t.Word(S.CardtextRichBoldClose) + t.Word(S.CardtextRichColourClose) + close; }
                    output.Append(prefix);
                }
                open.Push(new KeyValuePair<string, bool>(close, false));
            }
            if (skipping == 0) output.Append(Text(t, html.Substring(last)));
            return output.ToString();
        }

        private static bool HasClass(CardTextData t, string classes, string key)
        {
            var name = t.Word(key);
            foreach (var part in classes.Split(t.Word(S.CardtextSpace)[0]))
                if (part == name) return true;
            return false;
        }

        /// <summary>The text between tags, its entities decoded and any angle bracket made safe for rich text.</summary>
        private static string Text(CardTextData t, string raw) =>
            raw.Length == 0 ? raw : t.Unesc(raw).Replace(t.Word(S.CardtextAngleOpen), t.Word(S.CardtextAngleOpenSafe));
    }
}
