using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using CC = Ashen.Generated.Core2CombatKeys;
using CK = Ashen.Generated.Core2RunKeys;
using K = Ashen.Generated.CombatKeys;
using RK = Ashen.Generated.RunKeys;

namespace Ashen.App.Run
{
    /// <summary>One chip of the Character Sheet's stat grid (web progressionStats rows): the number, the label its tooltip opens with and the formula behind it.</summary>
    public sealed class ProgressionStatRow
    {
        public string Id;
        public string Label;
        public string FaceLabel;
        public double Value;
        public string Formula;
    }

    /// <summary>One attribute's card (web creationBrief attributeCardModels as the Progression hub shows it): the face with its leading benefit and the reveal with every stat the attribute feeds, scales and unlocks.</summary>
    public sealed class AttributeCardView
    {
        public string Id;
        public string Short;
        public string Title;
        public double Value;

        /// <summary>"Strength (STR)": the face's name.</summary>
        public string FaceLabel;

        public string MainSummary;
        public string Sense;
        public string Flavour;
        public string Tooltip;
        public readonly List<string> Lines = new List<string>();
    }

    /// <summary>The Character Sheet's data: the stat grid and the attribute cards (web ui/models/ProgressionInspectionModel.js progressionStats).</summary>
    public sealed class ProgressionStatsView
    {
        public readonly List<ProgressionStatRow> Rows = new List<ProgressionStatRow>();
        public readonly List<AttributeCardView> Attributes = new List<AttributeCardView>();
    }

    /// <summary>
    /// The Progression hub's stat inspection (web ui/models/ProgressionInspectionModel.js progressionStats over model/statProjection.js, model/handRules.js, model/combatRatings.js and model/creationBrief.js attributeCardModels): HP, SP, MP, AR, PR, DR,
    /// Hand size and Draw with the arithmetic behind each, and one card per attribute naming what a point of it buys. Read only: every number comes from the run's own derived-stat snapshot and the worn gear. Engine-free.
    /// </summary>
    public static class ProgressionStats
    {
        private sealed class Projected
        {
            public List<KeyValuePair<string, double>> Weights;
            public double PerIncrease = 1;
            public double Gain = 1;
            public double? Max;
            public double? Cap;
        }

        private sealed class Feed
        {
            public string Id;
            public string Label;
            public double? PerTier;
            public int Points = 1;
            public double? Cap;
        }

        private sealed class Scaling
        {
            public string Id;
            public double Weight;
            public double? Multiplier;
            public double? Cap;
            public string Label;
        }

        private sealed class HandReceipt
        {
            public StatReceipt Opening;
            public StatReceipt Turn;
            public StatReceipt Size;
            public double OpeningValue;
            public double TurnValue;
            public bool Fill;
        }

        private static string N(double v) => RunJs.NumStr(v);

        private static double? Finite(JToken v) => Js.IsFinite(v) ? Js.D(v) : (double?)null;

        /// <summary>progressionStats(registries, run, settings) for the Core 2 run, with its attributes optionally replaced (the allocation dialog previews the points it holds).</summary>
        public static ProgressionStatsView For(Core2RunSession session, UiData ui, JObject attributes = null)
        {
            var strings = ui.Strings;
            var layout = ui.Progression;
            var d = session.Core.Combat.Run;
            var run = session.Run;
            if (attributes != null) run[K.Attributes] = attributes;
            var held = run.Obj(K.Attributes);
            var classId = run.Str(RK.Class);
            var classDef = d.Run.Classes.Get(classId);
            var level = Js.IsInt(run.Obj(K.Level)?[K.Level]) ? run.Obj(K.Level).Num(K.Level) : 1;
            var table = run.Obj(K.DerivedStatRuleSnapshot).Obj(K.Rules);
            var presentation = d.Run.DerivedStatRules.Obj(ReceiptKeys.Presentation) ?? new JObject();
            var ordered = presentation.Properties().Where(p => p.Name != ReceiptKeys.EnergyRow).Select(p => new KeyValuePair<string, JObject>(p.Name, (JObject)p.Value))
                .OrderBy(p => p.Value.Num(RK.Order)).ToList();
            var attributeRows = CreationStats.OrderedAttributes(d.Run);
            string Short(JObject def) => strings.Get(string.Format(System.Globalization.CultureInfo.InvariantCulture, ArmouryKeys.AttributeShortKey, def.Str(K.Id)));
            var shorts = attributeRows.ToDictionary(a => a.Str(K.Id), Short);
            string ShortOf(string id) => shorts.TryGetValue(id, out var s) ? s : id;

            // statProjection(registries, run).derived: the pools, priced by the run's own snapshot.
            var derived = new Dictionary<string, ProgressionStatRow>();
            var projected = new Dictionary<string, Projected>();
            double hpBonus = 0;
            var loadout = run.Obj(K.Loadout);
            if (loadout != null) hpBonus = CoreRunRules.RunMods(d, loadout, classId, null).Pool(Loadout.PoolFields(d.Run)[0]);
            foreach (var entry in ordered)
            {
                var id = entry.Key;
                if (layout.RatingRows.Contains(id) || table.Obj(K.Rules)?.Obj(id) == null) continue;
                var receipt = CoreDerivedStats.Derive(d, table, id, held, classDef, level);
                var row = CoreDerivedStats.ResolvedRow(d, table, id, classId);
                var isHp = id == ArmouryKeys.HpRow;
                var equipment = isHp ? hpBonus : 0;
                var adjustment = isHp ? Js.Or0(run[RK.MaxHpAdjustment]) : 0;
                var value = isHp ? Math.Max(1, receipt.Value + equipment + adjustment) : receipt.Value;
                projected[id] = new Projected
                {
                    Weights = CoreDerivedStats.RuleWeights(d, row), PerIncrease = receipt.PointsPerIncrease, Gain = receipt.GainPerTier, Max = receipt.Max, Cap = Finite(row[RK.Cap]),
                };
                derived[id] = new ProgressionStatRow
                {
                    Id = id, Label = entry.Value.Str(K.Label), FaceLabel = entry.Value.Str(ReceiptKeys.FaceLabel) ?? entry.Value.Str(K.Label), Value = value,
                    Formula = DerivedFormula(strings, receipt, equipment, adjustment, value, ShortOf),
                };
            }

            // runHandRules(registries, run, settings): the run's own three count rows, and what a fight deals from them.
            var hand = CoreRunCombat.HandRules(session.Core.Combat, run);
            var receipts = HandReceipts(d, hand, held, classDef, level, layout);
            foreach (var id in layout.HandRows)
            {
                var row = hand.Obj(CC.Rows)?.Obj(id);
                if (row == null) continue;
                projected[id] = new Projected
                {
                    Weights = CoreDerivedStats.RuleWeights(d, row), PerIncrease = Js.IsFinite(row[CK.PointsPerIncrease]) ? row.Num(CK.PointsPerIncrease) : 1, Gain = 1, Max = Finite(row[CK.Max]),
                };
            }

            var view = new ProgressionStatsView();
            var ratings = CoreRunCombat.RatingReceipt(session.Core.Combat, run);
            var totals = ratings.Obj(CK.Totals);
            foreach (var def in layout.Rows)
            {
                var row = new ProgressionStatRow { Id = def.Id, FaceLabel = strings.Get(def.FaceKey) };
                switch (def.Source)
                {
                    case ProgressionKeys.SourceDerived:
                        row.Label = derived[def.Id].Label;
                        row.Value = derived[def.Id].Value;
                        row.Formula = derived[def.Id].Formula;
                        break;
                    case ProgressionKeys.SourceRating:
                        row.Label = row.FaceLabel;
                        row.Value = totals.Num(def.Id);
                        row.Formula = string.Join(strings.Get(StringKeys.ProgressionStatSourceJoin), Js.Items(ratings[CK.Sources]).OfType<JObject>().Select(source => strings.Format(StringKeys.ProgressionStatSource,
                            new StringArgs().Add(UiPlaceholders.Name, source.Str(K.Name)).Add(UiPlaceholders.Value, N(Js.Or0(source[def.Id]))))));
                        break;
                    case ProgressionKeys.SourceHandSize:
                        row.Label = strings.Get(def.LabelKey);
                        row.Value = receipts.Size.Value;
                        row.Formula = strings.Format(StringKeys.ProgressionStatMaxHeld, new StringArgs().Add(UiPlaceholders.Value, N(receipts.Size.Value)));
                        break;
                    default:
                        row.Label = strings.Get(def.LabelKey);
                        row.Value = receipts.TurnValue;
                        row.Formula = strings.Format(StringKeys.ProgressionHandTurn, new StringArgs().Add(UiPlaceholders.Formula, TurnFormula(strings, receipts, ShortOf)));
                        break;
                }
                view.Rows.Add(row);
            }

            var scalingRows = layout.RatingRows.Select(id => new KeyValuePair<string, JObject>(id, CoreDerivedStats.ResolvedRow(d, table, id, classId))).ToList();
            foreach (var def in attributeRows)
                view.Attributes.Add(Card(strings, layout, d, def, Short(def), held, ordered, projected, scalingRows));
            return view;
        }

        // ------------------------------------------------------------------ the pools' arithmetic

        /// <summary>statProjection's formula: base, then each attribute's floored term by its short label, the level, the gear and the permanent adjustment, held to the row's bounds.</summary>
        private static string DerivedFormula(StringTable strings, StatReceipt receipt, double equipment, double adjustment, double value, Func<string, string> shortOf)
        {
            var terms = receipt.PointsPerIncrease == 1 && receipt.GainPerTier == 1
                ? string.Join(strings.Get(StringKeys.ProgressionStatTermJoin), receipt.Terms.Select(t => strings.Format(StringKeys.ProgressionStatTerm,
                    new StringArgs().Add(UiPlaceholders.Value, N(t.Value)).Add(UiPlaceholders.Label, shortOf(t.Key)))))
                : strings.Format(StringKeys.ProgressionStatTierGain, new StringArgs().Add(ReceiptPlaceholders.Tier, N(receipt.Tier)).Add(StatPlaceholders.Gain, N(receipt.GainPerTier)));
            if (terms.Length == 0) terms = N(0);
            string Part(string key, double v) => strings.Format(key, new StringArgs().Add(UiPlaceholders.Value, N(v)));
            var held = receipt.Raw != value
                ? Part(value == receipt.Min ? StringKeys.ProgressionStatHeldMin : StringKeys.ProgressionStatHeldMax, value == receipt.Min ? receipt.Min.Value : value)
                : string.Empty;
            return strings.Format(StringKeys.ProgressionStatFormula, new StringArgs()
                .Add(UiPlaceholders.Base, N(receipt.Base)).Add(StatPlaceholders.Terms, terms)
                .Add(UiPlaceholders.Level, receipt.LevelBonus != 0 ? Part(StringKeys.ProgressionStatLevelTerm, receipt.LevelBonus) : string.Empty)
                .Add(StatPlaceholders.Gear, equipment != 0 ? Part(StringKeys.ProgressionStatGearTerm, equipment) : string.Empty)
                .Add(StatPlaceholders.Permanent, adjustment != 0 ? Part(adjustment > 0 ? StringKeys.ProgressionStatPermanentGain : StringKeys.ProgressionStatPermanentLoss, Math.Abs(adjustment)) : string.Empty)
                .Add(StatPlaceholders.Held, held).Add(UiPlaceholders.Value, N(value)));
        }

        /// <summary>handSizeReceipts(rules, attributes, level): the opening hand, the most a turn draws and the hand size, each over the run's own count rows (model/handRules.js).</summary>
        private static HandReceipt HandReceipts(CoreRunData d, JObject hand, JObject attributes, JObject classDef, double level, ProgressionLayout layout)
        {
            StatReceipt Count(string id) => CoreDerivedStats.RowValue(d, hand.Obj(CC.Rows).Obj(id), attributes, classDef, level, id);
            var size = Count(layout.HandRows.Last());
            var opening = Count(ProgressionKeys.OpeningHandRow);
            var turn = Count(ProgressionKeys.SourceDraw);
            var fill = hand.Str(K.DrawMode) == CombatValues.DrawFill;
            var room = Math.Max(0, size.Value);
            return new HandReceipt
            {
                Size = size, Opening = opening, Turn = turn, Fill = fill,
                OpeningValue = Math.Min(room, opening.Value),
                TurnValue = Math.Min(room, fill ? room : turn.Value),
            };
        }

        private static string HandTerms(StringTable strings, StatReceipt receipt, Func<string, string> shortOf)
        {
            var text = strings.Format(StringKeys.ProgressionHandBase, new StringArgs().Add(UiPlaceholders.Value, N(receipt.Base)));
            foreach (var term in receipt.Terms.Where(t => t.Value != 0))
                text += strings.Format(StringKeys.ProgressionHandTerm, new StringArgs().Add(UiPlaceholders.Value, N(term.Value)).Add(UiPlaceholders.Label, shortOf(term.Key)));
            if (receipt.LevelBonus != 0) text += strings.Format(StringKeys.ProgressionHandLevel, new StringArgs().Add(UiPlaceholders.Value, N(receipt.LevelBonus)));
            return text;
        }

        /// <summary>handResourceRows' turnFormula: the draw row's arithmetic, or the fill rule's.</summary>
        private static string TurnFormula(StringTable strings, HandReceipt hand, Func<string, string> shortOf)
        {
            if (hand.Fill) return strings.Format(StringKeys.ProgressionHandFill, new StringArgs().Add(UiPlaceholders.Value, N(hand.Size.Value)));
            var turn = hand.Turn;
            string Part(string key, double v) => strings.Format(key, new StringArgs().Add(UiPlaceholders.Value, N(v)));
            var limits = turn.Min.HasValue && turn.Raw < turn.Min.Value ? Part(StringKeys.ProgressionHandRaised, turn.Min.Value)
                : turn.Max.HasValue && turn.Raw > turn.Max.Value ? Part(StringKeys.ProgressionHandLimited, turn.Max.Value) : string.Empty;
            var capped = turn.Value > hand.Size.Value ? Part(StringKeys.ProgressionHandCapped, hand.Size.Value) : string.Empty;
            return strings.Format(StringKeys.ProgressionHandTurnFormula, new StringArgs()
                .Add(StatPlaceholders.Terms, HandTerms(strings, turn, shortOf)).Add(StatPlaceholders.Limits, limits).Add(StatPlaceholders.Capped, capped)
                .Add(UiPlaceholders.Value, N(hand.TurnValue)).Add(StatPlaceholders.Never, capped.Length > 0 ? string.Empty : Part(StringKeys.ProgressionHandNever, hand.Size.Value)));
        }

        // ------------------------------------------------------------------ the attribute cards

        /// <summary>attributeCycle(weight, perIncrease): the fewest points (up to 100) after which every floor of floor(floor(n x weight) / perIncrease) has landed exactly, or null.</summary>
        private static int? Cycle(double weight, double perIncrease)
        {
            for (var n = 1; n <= StatLimits.CycleLimit; n++)
            {
                var weighted = n * weight;
                if (Math.Abs(weighted - RunJs.Round(weighted)) > StatMath.CycleTolerance) continue;
                var increases = RunJs.Round(weighted) / perIncrease;
                if (Math.Abs(increases - RunJs.Round(increases)) <= StatMath.CycleTolerance && RunJs.Round(increases) > 0) return n;
            }
            return null;
        }

        private static string Cap(StringTable strings, string key, double? cap) => cap.HasValue ? strings.Format(key, new StringArgs().Add(UiPlaceholders.Max, N(cap.Value))) : string.Empty;

        /// <summary>foldedSummary(sense): the first sentence of the authored text.</summary>
        private static string FirstSentence(string sense)
        {
            var text = (sense ?? string.Empty).Trim();
            var match = Regex.Match(text, ProgressionKeys.FirstSentence);
            return match.Success ? match.Value.Trim() : text;
        }

        private static AttributeCardView Card(StringTable strings, ProgressionLayout layout, CoreRunData d, JObject def, string shortLabel, JObject attributes,
            List<KeyValuePair<string, JObject>> ordered, Dictionary<string, Projected> projected, List<KeyValuePair<string, JObject>> scalingRows)
        {
            var id = def.Str(K.Id);
            var title = def.Str(K.Label) ?? id;
            var sense = def.Str(ProgressionKeys.Sense) ?? string.Empty;

            // What this attribute feeds, as facts before prose: every derived stat whose run weights put a positive weight on it.
            var feeds = new List<Feed>();
            foreach (var entry in ordered)
            {
                var statId = entry.Key;
                if (layout.RatingRows.Contains(statId) || !projected.TryGetValue(statId, out var p)) continue;
                var weight = p.Weights.Where(w => w.Value > 0 && w.Key == id).Select(w => (double?)w.Value).FirstOrDefault();
                if (!weight.HasValue) continue;
                var label = entry.Value.Str(K.Label);
                var cap = p.Max ?? p.Cap;
                var cycle = Cycle(weight.Value, p.PerIncrease);
                if (cycle == null) { feeds.Add(new Feed { Id = statId, Label = label, Cap = cap }); continue; }
                feeds.Add(new Feed { Id = statId, Label = label, Points = cycle.Value, Cap = cap, PerTier = RunJs.Round(cycle.Value * weight.Value / p.PerIncrease * p.Gain * StatMath.RoundScale) / StatMath.RoundScale });
            }

            // The combat ratings the attribute scales, from the rows the run's fights use.
            var ratings = new List<Scaling>();
            foreach (var pair in scalingRows)
            {
                var rule = pair.Value;
                if (rule == null || !(Js.IsFinite(rule[id]) && Js.D(rule[id]) > 0)) continue;
                ratings.Add(new Scaling { Id = pair.Key, Weight = Js.D(rule[id]), Multiplier = Finite(rule[K.Multiplier]), Cap = Finite(rule[CK.Max]), Label = strings.Get(layout.RatingLabels[pair.Key]) });
            }
            string Plural(int points) => strings.Get(points == 1 ? StringKeys.ProgressionAttrUnitOne : StringKeys.ProgressionAttrUnitMany);

            var lines = new List<string>();
            foreach (var feed in feeds)
            {
                var cap = Cap(strings, StringKeys.ProgressionAttrCapAtMost, feed.Cap);
                lines.Add(feed.PerTier.HasValue
                    ? strings.Format(StringKeys.ProgressionAttrFeed, new StringArgs().Add(UiPlaceholders.Label, feed.Label).Add(UiPlaceholders.Amount, N(feed.PerTier.Value)).Add(UiPlaceholders.Points, feed.Points)
                        .Add(ReceiptPlaceholders.Unit, Plural(feed.Points)).Add(UiPlaceholders.Cap, cap))
                    : strings.Format(StringKeys.ProgressionAttrFeedScales, new StringArgs().Add(UiPlaceholders.Label, feed.Label).Add(ReceiptPlaceholders.Attribute, title).Add(UiPlaceholders.Cap, cap)));
            }
            foreach (var rating in ratings)
                lines.Add(strings.Format(StringKeys.ProgressionAttrScaling, new StringArgs().Add(UiPlaceholders.Label, rating.Label).Add(StatPlaceholders.Weight, N(rating.Weight)).Add(StatPlaceholders.Short, shortLabel)
                    .Add(StatPlaceholders.Multiplier, rating.Multiplier.HasValue && rating.Multiplier.Value != 1
                        ? strings.Format(StringKeys.ProgressionAttrScalingMultiplier, new StringArgs().Add(StatPlaceholders.Multiplier, N(rating.Multiplier.Value))) : string.Empty)
                    .Add(UiPlaceholders.Cap, Cap(strings, StringKeys.ProgressionAttrCapAtMost, rating.Cap))));
            foreach (var piece in Js.Items(d.Run.Equipment[K.Armaments]).OfType<JObject>())
            {
                var gate = piece.Obj(RK.Requirements)?.Obj(ProgressionKeys.RequirementAttributes)?[id];
                if (gate == null || gate.Type == JTokenType.Null) continue;
                lines.Add(strings.Format(StringKeys.ProgressionAttrUnlock, new StringArgs().Add(UiPlaceholders.Name, piece.Str(K.Name)).Add(UiPlaceholders.Amount, N(Js.D(gate)))));
            }

            // The face: what a point buys, one cadence per fact.
            string Cadence(int points) => points == 1 ? strings.Get(StringKeys.ProgressionAttrCadenceOne) : strings.Format(StringKeys.ProgressionAttrCadenceMany, new StringArgs().Add(UiPlaceholders.Points, points));
            var faceFacts = feeds.Where(f => f.PerTier.HasValue).Select(f => strings.Format(StringKeys.ProgressionAttrFaceFact, new StringArgs().Add(UiPlaceholders.Amount, N(f.PerTier.Value))
                .Add(UiPlaceholders.Label, f.Label).Add(StatPlaceholders.Cadence, Cadence(f.Points)).Add(UiPlaceholders.Cap, Cap(strings, StringKeys.ProgressionAttrCapMax, f.Cap)))).ToList();
            var scalingCaps = feeds.Where(f => !f.PerTier.HasValue && f.Cap.HasValue).Select(f => strings.Format(StringKeys.ProgressionAttrFaceScales,
                new StringArgs().Add(UiPlaceholders.Label, f.Label).Add(UiPlaceholders.Cap, Cap(strings, StringKeys.ProgressionAttrCapMax, f.Cap)))).ToList();
            var scalingFacts = (faceFacts.Count > 0 ? ratings.Where(r => r.Cap.HasValue) : ratings).Select(r => strings.Format(StringKeys.ProgressionAttrScalingSummary,
                new StringArgs().Add(UiPlaceholders.Label, r.Label).Add(StatPlaceholders.Weight, N(r.Weight)).Add(UiPlaceholders.Cap, Cap(strings, StringKeys.ProgressionAttrCapMax, r.Cap)))).ToList();
            var stated = faceFacts.Concat(scalingCaps).Concat(scalingFacts).ToList();
            var faceSummary = stated.Count > 0 ? string.Join(strings.Get(StringKeys.ProgressionAttrJoin), stated) : FirstSentence(sense);

            var mains = new List<string>();
            if (layout.MainBenefits.TryGetValue(id, out var benefits))
                foreach (var benefit in benefits)
                {
                    var feed = feeds.FirstOrDefault(f => f.Id == benefit.Stat);
                    var rating = ratings.FirstOrDefault(r => r.Id == benefit.Stat);
                    if (feed == null && rating == null) continue;
                    int? points = feed != null ? feed.Points : Cycle(rating.Weight, 1);
                    double? gain = feed != null ? feed.PerTier : points.HasValue ? RunJs.Round(points.Value * rating.Weight * StatMath.RoundScale) / StatMath.RoundScale : (double?)null;
                    var cap = feed != null ? feed.Cap : rating.Cap;
                    var simple = rating == null || !rating.Multiplier.HasValue || rating.Multiplier.Value == 1;
                    var cadence = points == 1 ? strings.Get(StringKeys.ProgressionAttrMainCadenceOne) : strings.Format(StringKeys.ProgressionAttrMainCadenceMany, new StringArgs().Add(UiPlaceholders.Points, points ?? 0));
                    var rate = simple && gain.HasValue && points.HasValue && points.Value != 0
                        ? strings.Format(StringKeys.ProgressionAttrRate, new StringArgs().Add(StatPlaceholders.Gain, N(gain.Value)).Add(StatPlaceholders.Cadence, cadence))
                        : strings.Get(StringKeys.ProgressionAttrRateScales);
                    mains.Add(strings.Format(StringKeys.ProgressionAttrMainLine, new StringArgs().Add(UiPlaceholders.Label, strings.Get(benefit.LabelKey)).Add(StatPlaceholders.Rate, rate)
                        .Add(UiPlaceholders.Cap, Cap(strings, StringKeys.ProgressionAttrCapMax, cap))));
                }
            var card = new AttributeCardView
            {
                Id = id, Short = shortLabel, Title = title, Value = attributes.Num(id), Sense = sense, Flavour = FirstSentence(sense),
                FaceLabel = strings.Format(StringKeys.ProgressionAttrTitle, new StringArgs().Add(UiPlaceholders.Title, title).Add(StatPlaceholders.Short, shortLabel)),
                MainSummary = mains.Count > 0 ? string.Join(strings.Get(StringKeys.ProgressionAttrJoin), mains) : faceSummary,
                Tooltip = strings.Format(StringKeys.ProgressionAttrTooltip, new StringArgs().Add(UiPlaceholders.Title, title)),
            };
            card.Lines.AddRange(lines.Count > 0 ? lines : new List<string> { strings.Get(StringKeys.ProgressionAttrNone) });
            return card;
        }
    }
}
