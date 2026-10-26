using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>A word the glossary explains in a text, as the text spells it, and what a tooltip says of it.</summary>
    public sealed class GlossaryEntry
    {
        public string Name;
        public string Explanation;
    }

    /// <summary>A stretch of a text: plain words, or a glossary word as the text spells it (<see cref="Entry"/>) with what a tooltip says of it.</summary>
    public sealed class GlossarySegment
    {
        public string Text;

        /// <summary>The glossary's word this stretch is, or null for plain words.</summary>
        public GlossaryEntry Entry;

        /// <summary>The word in lower case: what the glossary keys it by, and the id of the link that carries it.</summary>
        public string Key => Entry == null ? null : Text.ToLowerInvariant();
    }

    /// <summary>
    /// The tooltip glossary (web src/ui/components/tooltipGlossary.js configureTooltipGlossary and keywordExplanations): every term the framework words (ui/terms.json: a `term.tooltip.X` row explains the name and plural of `term.X`),
    /// every status and stance with its tooltip's numbers filled in (ui/uiContent.js statusTooltipText), and Block and Poise; a text names the ones it contains, each once, in the order they first appear. The first words given
    /// to a name stand: a status of the same name replaces a term's, nothing else does. Engine-free.
    /// </summary>
    public sealed class TermGlossary
    {
        private readonly Dictionary<string, string> _terms;
        private readonly Regex _pattern;

        private TermGlossary(Dictionary<string, string> terms)
        {
            _terms = terms;
            var names = terms.Keys.OrderByDescending(n => n.Length).Select(Regex.Escape).ToList();
            _pattern = names.Count == 0 ? null : new Regex(GlossaryKeys.WordBoundary + string.Join(GlossaryKeys.Alternation, names) + GlossaryKeys.WordBoundaryEnd, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>The words in the glossary, lower case.</summary>
        public IReadOnlyCollection<string> Names => _terms.Keys;

        public static TermGlossary Build(JObject terms, IEnumerable<JObject> statuses, IEnumerable<JObject> stances, StringTable strings)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var order = new List<string>();
            void Add(string name, string explanation)
            {
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(explanation)) return;
                var key = name.ToLowerInvariant();
                if (map.ContainsKey(key)) return;
                map[key] = explanation;
                order.Add(key);
            }
            var rows = Js.Items(terms[GlossaryKeys.Terms]).OfType<JObject>().ToList();
            var vocabulary = rows.ToDictionary(r => r.Str(GlossaryKeys.Id), r => r, StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var id = row.Str(GlossaryKeys.Id);
                if (!id.StartsWith(GlossaryKeys.TooltipPrefix, StringComparison.Ordinal)) continue;
                if (!vocabulary.TryGetValue(GlossaryKeys.TermPrefix + id.Substring(GlossaryKeys.TooltipPrefix.Length), out var name)) continue;
                Add(name.Str(GlossaryKeys.CanonicalText), row.Str(GlossaryKeys.CanonicalText));
                Add(name.Str(GlossaryKeys.PluralText), row.Str(GlossaryKeys.CanonicalText));
            }
            foreach (var row in statuses.Concat(stances))
            {
                var name = row.Str(GlossaryKeys.Name);
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(row.Str(GlossaryKeys.Tooltip))) continue;
                var key = name.ToLowerInvariant();
                if (!map.ContainsKey(key)) order.Add(key);
                map[key] = StatusTooltip(row);
            }
            Add(strings.Get(StringKeys.GlossaryBlockName), strings.Get(StringKeys.GlossaryBlockText));
            Add(strings.Get(StringKeys.GlossaryPoiseName), strings.Get(StringKeys.GlossaryPoiseText));
            var ordered = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in order) ordered[key] = map[key];
            return new TermGlossary(ordered);
        }

        /// <summary>String(v) of the numbers and words a status's tooltip names.</summary>
        private static string Say(JToken v)
        {
            switch (v.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return RunJs.NumStr(Js.D(v));
                case JTokenType.Boolean:
                    return (bool)v ? GlossaryKeys.True : GlossaryKeys.False;
                case JTokenType.String:
                    return (string)v;
                case JTokenType.Array:
                    return string.Join(GlossaryKeys.ArrayJoin, ((JArray)v).Select(Say));
                default:
                    return GlossaryKeys.ObjectText;
            }
        }

        /// <summary>statusTooltipText(def): the tooltip with the numbers of the status's own mechanics put in where it names them.</summary>
        public static string StatusTooltip(JObject def)
        {
            var text = def.Str(GlossaryKeys.Tooltip) ?? string.Empty;
            void Sub(string token, JToken v)
            {
                if (v == null || v.Type == JTokenType.Null) return;
                text = text.Replace(token, Say(v));
            }
            if (def.Obj(GlossaryKeys.Proc) is JObject proc)
            {
                Sub(GlossaryKeys.TokenProcThreshold, proc[GlossaryKeys.Threshold]);
                Sub(GlossaryKeys.TokenBurstPercent, proc[GlossaryKeys.BurstPercent]);
                Sub(GlossaryKeys.TokenBurstMin, proc[GlossaryKeys.BurstMin]);
                Sub(GlossaryKeys.TokenBurstMax, proc[GlossaryKeys.BurstMax]);
                Sub(GlossaryKeys.TokenPoiseDamage, proc[GlossaryKeys.PoiseDamage]);
            }
            if (def.Obj(GlossaryKeys.Resists) is JObject resists) Sub(GlossaryKeys.TokenResists, resists[GlossaryKeys.Percent]);
            if (def.Obj(GlossaryKeys.TaggedVulnerability) is JObject tagged) Sub(GlossaryKeys.TokenTaggedVulnerability, new JValue(RunJs.Round((Js.D(tagged[GlossaryKeys.Mult]) - 1) * UiMath.Percent)));
            if (def.Obj(GlossaryKeys.Decay) is JObject decay) Sub(GlossaryKeys.TokenDecay, decay[GlossaryKeys.Duration]);
            var enter = Js.Items(def[GlossaryKeys.OnEnter]).ToList();
            for (var i = 0; i < enter.Count; i++)
                Sub(GlossaryKeys.TokenOnEnter.Replace(GlossaryKeys.Arg0, i.ToString(CultureInfo.InvariantCulture)), (enter[i] as JObject)?[GlossaryKeys.Amount]);
            var hooks = Js.Items(def[GlossaryKeys.Hooks]).ToList();
            for (var i = 0; i < hooks.Count; i++)
            {
                var effects = Js.Items((hooks[i] as JObject)?[GlossaryKeys.Do]).ToList();
                for (var j = 0; j < effects.Count; j++)
                    Sub(GlossaryKeys.TokenHook.Replace(GlossaryKeys.Arg0, i.ToString(CultureInfo.InvariantCulture)).Replace(GlossaryKeys.Arg1, j.ToString(CultureInfo.InvariantCulture)), (effects[j] as JObject)?[GlossaryKeys.Amount]);
            }
            return text;
        }

        /// <summary>
        /// decorate(root) of tooltipGlossary.js for one text: the text cut where the glossary's words are, every occurrence of each (the web wraps each match in a <c>tooltip-keyword</c> span carrying its explanation),
        /// the words between them plain. A text with none is one plain stretch.
        /// </summary>
        public IReadOnlyList<GlossarySegment> Segments(string text)
        {
            var list = new List<GlossarySegment>();
            if (string.IsNullOrEmpty(text)) return list;
            var at = 0;
            if (_pattern != null)
                foreach (Match match in _pattern.Matches(text))
                {
                    if (match.Index > at) list.Add(new GlossarySegment { Text = text.Substring(at, match.Index - at) });
                    list.Add(new GlossarySegment { Text = match.Value, Entry = new GlossaryEntry { Name = match.Value, Explanation = _terms[match.Value.ToLowerInvariant()] } });
                    at = match.Index + match.Length;
                }
            if (at < text.Length) list.Add(new GlossarySegment { Text = text.Substring(at) });
            return list;
        }

        /// <summary>keywordExplanations(registries, text): the words of the glossary the text contains, each once, in the order they first appear (spelled as they last appear).</summary>
        public IReadOnlyList<GlossaryEntry> Explain(string text)
        {
            var found = new List<GlossaryEntry>();
            if (_pattern == null || string.IsNullOrEmpty(text)) return found;
            var seen = new Dictionary<string, GlossaryEntry>(StringComparer.Ordinal);
            foreach (Match match in _pattern.Matches(text))
            {
                var key = match.Value.ToLowerInvariant();
                if (seen.TryGetValue(key, out var entry)) entry.Name = match.Value;
                else
                {
                    entry = new GlossaryEntry { Name = match.Value, Explanation = _terms[key] };
                    seen[key] = entry;
                    found.Add(entry);
                }
            }
            return found;
        }
    }
}
