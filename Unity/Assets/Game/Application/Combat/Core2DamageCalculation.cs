using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using DK = Ashen.Generated.Core2DamageKeys;
using IK = Ashen.Generated.IntentKeys;
using K = Ashen.Generated.CombatKeys;

namespace Ashen.App.Combat
{
    /// <summary>
    /// The calculation behind a previewed damage number (P-1, D-1320..): the web's <c>damageReceiptHtml</c> (ui/components/damageReceipt.js) of the receipt the card's preview carries, drawn as
    /// a title and lines. Pointing at an underlined damage number on the hand shows it. The web's three topics (Hits, Sources, Defenses) are links that open further tips; here each is a line,
    /// since a tooltip cannot hold a second one. Engine-free.
    /// </summary>
    public static class Core2DamageCalculation
    {
        /// <summary>cardDamageReceipt(preview): the receipts of the damage rows joined into one (the first's header, every hit, the summed total), or null when no row carries one.</summary>
        public static JObject Of(JObject preview)
        {
            var receipts = Js.Items(preview?[K.Values]).OfType<JObject>().Where(row => row.Str(K.Op) == K.Damage && row[IK.DamageReceipt] is JObject).Select(row => row.Obj(IK.DamageReceipt)).ToList();
            if (receipts.Count == 0) return null;
            var joined = (JObject)receipts[0].DeepClone();
            joined[IK.Hits] = new JArray(receipts.SelectMany(r => Js.Items(r[IK.Hits])).Select(h => h.DeepClone()));
            joined[DK.TotalDamage] = Js.N(receipts.Sum(r => r.Or0(DK.TotalDamage)));
            return joined;
        }

        private static string Number(JToken value) => Js.IsFinite(value) ? CoreIntentProjection.Number(Js.D(value)) : null;

        private static StringArgs Args() => new StringArgs();

        private static string Plural(StringTable strings, int count) => strings.Get(count == 1 ? StringKeys.CombatReceiptHitOne : StringKeys.CombatReceiptHitMany);

        /// <summary>groupedHitDetails: a row repeated across the hits that read alike is said once, with the hits it holds for.</summary>
        private static string Grouped(StringTable strings, List<JObject> hits, Func<JObject, List<string>> describe)
        {
            var order = new List<string>();
            var groups = new Dictionary<string, List<List<int>>>(StringComparer.Ordinal);
            for (var index = 0; index < hits.Count; index++)
            {
                var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var text in describe(hits[index]))
                {
                    occurrences.TryGetValue(text, out var occurrence);
                    occurrences[text] = occurrence + 1;
                    if (!groups.ContainsKey(text)) { groups[text] = new List<List<int>>(); order.Add(text); }
                    var rows = groups[text];
                    while (rows.Count <= occurrence) rows.Add(null);
                    (rows[occurrence] ?? (rows[occurrence] = new List<int>())).Add(index + 1);
                }
            }
            var lines = new List<string>();
            foreach (var text in order)
                foreach (var indices in groups[text].Where(r => r != null))
                    lines.Add(strings.Format(StringKeys.CombatReceiptGroupLine, Args().Add(IK.Label, strings.Get(indices.Count == 1 ? StringKeys.CombatReceiptHitLabel : StringKeys.CombatReceiptHitsLabel))
                        .Add(IK.Indices, string.Join(", ", indices)).Add(IK.Text, text)));
            return string.Join("; ", lines);
        }

        private static string Unknown(StringTable strings) => strings.Get(StringKeys.CombatReceiptUnknownNumber);

        /// <summary>The title and lines of the calculation; null when the preview holds no hit.</summary>
        public static Core2CombatText.Tip Tip(StringTable strings, JObject preview, string targetName = null)
        {
            var receipt = Of(preview);
            var hits = Js.Items(receipt?[IK.Hits]).OfType<JObject>().ToList();
            if (hits.Count == 0) return null;
            var targetId = receipt.Str(DK.TargetId);
            var scope = !string.IsNullOrEmpty(targetId)
                ? strings.Format(StringKeys.CombatReceiptScopeTarget, Args().Add(IK.Target, string.IsNullOrEmpty(targetName) ? targetId : targetName))
                : strings.Get(StringKeys.CombatReceiptScopeBefore);
            var summary = strings.Format(StringKeys.CombatReceiptSummary, Args().Add(IK.Total, Number(receipt[DK.TotalDamage]) ?? Unknown(strings)).Add(IK.Count, hits.Count).Add(IK.Hits, Plural(strings, hits.Count)));
            var lines = new List<string> { summary, scope };

            var detail = Grouped(strings, hits, hit =>
            {
                var components = Js.IsFinite(hit[IK.Physical]) || Js.IsFinite(hit[IK.Magical])
                    ? strings.Format(StringKeys.CombatReceiptComponents, Args().Add(IK.Physical, Number(hit[IK.Physical]) ?? Unknown(strings)).Add(IK.Magical, Number(hit[IK.Magical]) ?? Unknown(strings))) : string.Empty;
                var rounding = !Js.Nullish(hit[IK.Rounding]) && Js.Str(hit[IK.Rounding]) != string.Empty
                    ? strings.Format(StringKeys.CombatReceiptRounding, Args().Add(IK.Value, Js.Str(hit[IK.Rounding]) ?? Number(hit[IK.Rounding]))) : string.Empty;
                return new List<string> { strings.Format(StringKeys.CombatReceiptHitLine, Args().Add(IK.Incoming, Number(hit[IK.Incoming]) ?? Unknown(strings)).Add(IK.Components, components)
                    .Add(IK.Hp, Number(hit[DK.HpLoss]) ?? Unknown(strings)).Add(IK.Rounding, rounding)) };
            });
            var sources = Grouped(strings, hits, hit =>
            {
                var rows = Js.Items(hit[IK.Sources]).OfType<JObject>().Select(source =>
                {
                    string operation;
                    if (source.Str(IK.Operation) == IK.OpMultiply || Js.IsFinite(source[IK.Multiplier]))
                        operation = strings.Format(StringKeys.CombatReceiptMultiply, Args().Add(IK.N, Number(Js.IsFinite(source[IK.Multiplier]) ? source[IK.Multiplier] : source[IK.Amount]) ?? Unknown(strings)));
                    else if (source.Str(IK.Operation) == IK.OpSet) operation = strings.Format(StringKeys.CombatReceiptSet, Args().Add(IK.N, Number(source[IK.Amount]) ?? Unknown(strings)));
                    else if (Js.IsFinite(source[IK.Amount]))
                        operation = strings.Format(source.Num(IK.Amount) >= 0 ? StringKeys.CombatReceiptPlus : StringKeys.CombatReceiptMinus, Args().Add(IK.N, Number(Js.N(Math.Abs(source.Num(IK.Amount))))));
                    else operation = Unknown(strings);
                    var label = Js.Truthy(source[IK.Label]) ? source.Str(IK.Label) : Js.Truthy(source[IK.Id]) ? source.Str(IK.Id) : strings.Get(StringKeys.CombatReceiptSourceDefault);
                    return strings.Format(StringKeys.CombatReceiptSource, Args().Add(IK.Label, label).Add(IK.Operation, operation));
                }).ToList();
                // Sources are an ordered operation sequence: only complete matching sequences are shared, so repeated bonuses never move across intervening multipliers.
                return rows.Count > 0 ? new List<string> { string.Join("; ", rows) } : new List<string>();
            });
            var defenses = Grouped(strings, hits, hit =>
            {
                var rows = new List<string>();
                foreach (var (key, name) in new[] { (IK.WardReduced, StringKeys.CombatReceiptWardName), (IK.PoiseReduced, null), (IK.Blocked, StringKeys.CombatReceiptBlockName) })
                {
                    if (!(Js.IsFinite(hit[key]) && hit.Num(key) > 0)) continue;
                    rows.Add(name != null
                        ? strings.Format(StringKeys.CombatReceiptReduced, Args().Add(IK.Label, strings.Get(name)).Add(IK.N, Number(hit[key])))
                        : strings.Format(StringKeys.CombatReceiptPoiseAbsorbed, Args().Add(IK.N, Number(hit[key]))));
                }
                if (hit.Or0(IK.PoiseDamage) > 0) rows.Add(strings.Format(StringKeys.CombatReceiptPoiseDamage, Args().Add(IK.N, Number(hit[IK.PoiseDamage]))));
                return rows;
            });
            void Topic(string labelKey, string text)
            {
                if (!string.IsNullOrEmpty(text)) lines.Add(strings.Format(StringKeys.CombatReceiptTopic, Args().Add(IK.Label, strings.Get(labelKey)).Add(IK.Text, text)));
            }
            Topic(StringKeys.CombatReceiptHitsLabel, detail);
            Topic(StringKeys.CombatReceiptSourcesLabel, sources);
            Topic(StringKeys.CombatReceiptDefensesLabel, defenses);
            return new Core2CombatText.Tip { Title = strings.Get(StringKeys.CombatReceiptTitle), Body = string.Join("\n", lines) };
        }
    }
}
