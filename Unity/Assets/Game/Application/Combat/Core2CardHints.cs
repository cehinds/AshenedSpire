using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Combat
{
    /// <summary>What a card's definition says about its stance and counter, as the inspector words it (the web's cardTooltip reads it off the definition).</summary>
    public sealed class Core2CardRules
    {
        public JObject CounterCoverage;
        public string DefenseRange;
        public double DefenseRulesVersion = Core2RangeNumbers.DefenseRulesVersion;
        public bool DefensiveReaction;
    }

    /// <summary>One rule hint of the inspector: a bold lead and its sentence (the web's combat-rule-hint).</summary>
    public sealed class CardHint
    {
        public string Lead;
        public string Text;
    }

    /// <summary>One mark the inspector explains: the sprite (a sigil or a school mark), its word and what it means (the web's sigilExplanationHtml).</summary>
    public sealed class CardSigilRow
    {
        public string Id;
        public bool School;
        public string Label;
        public string Help;
    }

    /// <summary>
    /// VP-15 (D-1100..): what the card inspector explains beyond the card's text, from src/ui/components/card.js cardTooltip and combatSigilView.js sigilExplanationHtml: the action, mirror and school
    /// marks with their words, the counter's return help or coverage, the defence range, the stance group, a counter's return range and a reaction sweep's note. Core 2 runs a configured profile, so the legacy
    /// stance-rating paragraph (card.js:715-726) is not part of it. Engine-free: the screen draws the rows.
    /// </summary>
    public static class Core2CardHints
    {
        private static string Key(string format, string id) => string.Format(CultureInfo.InvariantCulture, format, id);

        private static string TagValue(IEnumerable<string> tags, string prefix)
        {
            var values = tags.Where(t => t.StartsWith(prefix, StringComparison.Ordinal)).Select(t => t.Substring(prefix.Length)).Distinct().ToList();
            return values.Count == 1 ? values[0] : null;
        }

        /// <summary>The marks the card wears, each with its word and help: the action, the basic it mirrors, and its school.</summary>
        public static List<CardSigilRow> Sigils(StringTable strings, CardFaceLookup lookup, Core2CardView card)
        {
            var rows = new List<CardSigilRow>();
            var plan = lookup.Plan(card.Face);
            var config = lookup.Config;
            void Add(string sprite, string wordId, bool school)
            {
                var helpKey = Key(school ? UiFormats.CardSchoolHelpKey : UiFormats.CardSigilHelpKey, wordId);
                if (!strings.Has(helpKey)) return;
                rows.Add(new CardSigilRow
                {
                    Id = sprite,
                    School = school,
                    Label = strings.Get(Key(school ? UiFormats.CardSchoolKey : UiFormats.CardSigilKey, wordId)),
                    Help = strings.Get(helpKey),
                });
            }
            Add(plan.Sigil, plan.Sigil, false);
            if (plan.MirrorSigil != null) Add(plan.MirrorSigil, plan.Word, false);
            var school = TagValue(card.Tags, lookup.TagPrefix(ArtKeys.School));
            if (school != null && config.SchoolGlyphs.Contains(config.SchoolMark(school))) Add(config.SchoolMark(school), config.SchoolMark(school), true);
            return rows;
        }

        /// <summary>The rule hints under the marks, in the web's order.</summary>
        public static List<CardHint> Hints(StringTable strings, CardFaceLookup lookup, Core2CardView card)
        {
            var hints = new List<CardHint>();
            var rules = card.Rules ?? new Core2CardRules();
            var plan = lookup.Plan(card.Face);
            var join = strings.Get(StringKeys.CardHintJoin);
            var maneuver = TagValue(card.Tags, lookup.TagPrefix(ArtKeys.Maneuver));
            var counter = maneuver == lookup.Config.Hints.Value<string>(CardFaceKeys.CounterManeuver);
            if (counter)
            {
                var coverage = rules.CounterCoverage;
                if (coverage != null)
                {
                    string Names(string key) => string.Join(join, Js.Items(coverage[key]).Select(Js.Str).Where(s => s != null).Select(id => CoverageName(strings, id)));
                    var parts = new[] { Names(CardFaceKeys.Camps), Names(CardFaceKeys.Ranges), Names(CardFaceKeys.Targeting), Names(CardFaceKeys.Effects) }.Where(p => p.Length > 0);
                    var schools = Js.Items(coverage[CardFaceKeys.Schools]).Select(Js.Str).Where(s => s != null).ToList();
                    var args = new StringArgs().Add(UiPlaceholders.Coverage, string.Join(strings.Get(StringKeys.CardHintPartJoin), parts))
                        .Add(UiPlaceholders.Help, strings.Get(StringKeys.CardHintCounterCoverageHelp));
                    hints.Add(schools.Count > 0
                        ? Split(strings.Format(StringKeys.CardHintCounterCoverageSchools, args.Add(UiPlaceholders.Schools, string.Join(join, schools))))
                        : Split(strings.Format(StringKeys.CardHintCounterCoverage, args)));
                }
                else hints.Add(new CardHint { Text = strings.Get(StringKeys.CardHintCounterCore) });
            }
            if (!string.IsNullOrEmpty(rules.DefenseRange))
            {
                var ranges = lookup.Config.Ranges;
                var covers = ranges.Take(Math.Max(0, ranges.ToList().IndexOf(rules.DefenseRange)) + 1).Select(r => strings.Get(Key(UiFormats.CardRangeKey, r)));
                hints.Add(Split(strings.Format(StringKeys.CardHintDefenseRange, new StringArgs()
                    .Add(UiPlaceholders.Range, strings.Get(Key(UiFormats.CardRangeKey, rules.DefenseRange)))
                    .Add(UiPlaceholders.Covers, string.Join(join, covers)))));
            }
            if (lookup.Config.StanceSigils.Contains(plan.Sigil))
            {
                var groups = new List<string>();
                var defensive = plan.Sigil == lookup.Config.Hints.Value<string>(CardFaceKeys.DefenseSigil) || plan.Sigil == lookup.Config.Hints.Value<string>(CardFaceKeys.EvadeSigil);
                var sweep = plan.Sigil == lookup.Config.Hints.Value<string>(CardFaceKeys.SweepSigil);
                var rank = card.Rank ?? 1d;
                var both = counter || (sweep && (rank >= lookup.Config.Hints.Value<double>(CardFaceKeys.SweepDefensiveRank) || rules.DefensiveReaction));
                if (defensive) groups.Add(strings.Get(StringKeys.CardHintStanceDefensive));
                else
                {
                    groups.Add(strings.Get(StringKeys.CardHintStanceOffense));
                    if (both) groups.Add(strings.Get(StringKeys.CardHintStanceDefensive));
                }
                hints.Add(Split(strings.Format(StringKeys.CardHintStanceGroup, new StringArgs().Add(UiPlaceholders.Groups, string.Join(join, groups)))));
                if (counter && rules.DefenseRulesVersion >= lookup.Config.Hints.Value<double>(CardFaceKeys.ReturnRulesVersion))
                {
                    var range = TagValue(card.Tags, lookup.Config.Json[CardFaceKeys.Prefixes].Value<string>(CardFaceKeys.Range)) ?? lookup.Config.Ranges.First();
                    hints.Add(Split(strings.Format(StringKeys.CardHintReturnRange, new StringArgs().Add(UiPlaceholders.Range, strings.Get(Key(UiFormats.CardRangeKey, range))))));
                }
                if (maneuver == lookup.Config.Hints.Value<string>(CardFaceKeys.SweepManeuver) && card.Tags.Contains(lookup.Config.Hints.Value<string>(CardFaceKeys.ReactionTag)))
                    hints.Add(new CardHint { Text = strings.Get(StringKeys.CardHintReactionSweep) });
            }
            return hints;
        }

        private static string CoverageName(StringTable strings, string id)
        {
            var key = Key(UiFormats.CardCoverageKey, id);
            if (strings.Has(key)) return strings.Get(key);
            var range = Key(UiFormats.CardRangeKey, id);
            return strings.Has(range) ? strings.Get(range) : id;
        }

        /// <summary>A sentence whose first words (up to the first colon) are the bold lead, as the web's hints write "Stance group: Offense."; a sentence without a colon has no lead.</summary>
        private static CardHint Split(string sentence)
        {
            var colon = sentence.IndexOf(UiFormats.LeadSeparator, StringComparison.Ordinal);
            return colon < 0 ? new CardHint { Text = sentence } : new CardHint { Lead = sentence.Substring(0, colon + 1), Text = sentence.Substring(colon + 1).TrimStart() };
        }
    }
}
