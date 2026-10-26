using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Domain.Rewards;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using LK = Ashen.Generated.LoopKeys;
using LV = Ashen.Generated.LoopValues;
using RK = Ashen.Generated.RunKeys;
using V = Ashen.Generated.CombatValues;
using WK = Ashen.Generated.RewardsKeys;
using WV = Ashen.Generated.RewardsValues;

namespace Ashen.App.Run
{
    /// <summary>One cost pip of an offered card: its kind (ui/components.json card.costKinds) and number.</summary>
    public sealed class RewardCost
    {
        public string Kind;
        public string Text;
    }

    /// <summary>One pick of a card offer or draft: a card (CardView data) or a class-tree node (a tile).</summary>
    public sealed class RewardPickView
    {
        public string Id;
        public bool IsNode;
        public string Name;
        public string Text;
        public string TypeLine;
        public string Rarity;
        public bool Upgraded;
        public List<RewardCost> Costs = new List<RewardCost>();

        /// <summary>What the card's face is drawn from (its art, type band and tag rail); null for a node tile.</summary>
        public CardFaceQuery Face;

        /// <summary>The rules as the web writes them (P-1), for the face and for the inspector, and whether the model has more lines than the face shows; null where the card text was not given.</summary>
        public string RulesHtml;
        public string InspectHtml;
        public bool RulesOverflow;
    }

    /// <summary>
    /// The reward faces' words and art (shipped reward.js rowBody): a card pick, the sentence of a relic's or flask's effect
    /// from its template, and the art id of an offered item. The spoils door, the shop and the Armoury draw with them (the 405
    /// reward door these served is gone, WP-21). Engine-free.
    /// </summary>
    public static class RewardsView
    {
        /// <summary>A template-with-{art} registry id from an artKey (a flask's "flask-crimson" loses its prefix first), or null.</summary>
        internal static string ArtFromKey(string template, string artKey, string prefix)
        {
            if (string.IsNullOrEmpty(template) || string.IsNullOrEmpty(artKey)) return null;
            if (!string.IsNullOrEmpty(prefix) && artKey.StartsWith(prefix, StringComparison.Ordinal)) artKey = artKey.Substring(prefix.Length);
            return StringTable.Fill(template, new StringArgs().Add(UiPlaceholders.Art, artKey));
        }

        // ------------------------------------------------------------------ cards and nodes

        internal static string CardName(CombatData data, string cardId) => data.Cards.Has(cardId) ? data.Cards.Get(cardId).Str(K.Name) : cardId;

        /// <summary>A card as its CardView reads out of a fight: name, kind, rarity, authored costs and its text with the authored numbers.</summary>
        public static RewardPickView CardPick(CombatData data, StringTable strings, IReadOnlyList<string> costKinds, string cardId, bool upgraded)
        {
            if (!data.Cards.Has(cardId)) return new RewardPickView { Id = cardId, Name = cardId, Text = string.Empty };
            var def = Cards.Resolve(data, Js.Obj(K.CardId, cardId, K.Upgraded, upgraded));
            var pick = new RewardPickView
            {
                Id = cardId,
                Name = def.Str(K.Name),
                TypeLine = CombatText.CardKind(strings, Cards.Kind(data, def)),
                Rarity = def.Str(RK.Rarity),
                Face = CardFaceQuery.From(def),
                Upgraded = upgraded,
                Text = Sentence(data, def.Str(K.TextTemplate), EffectTokens(data, def[K.Effects])),
            };
            // Pips in ui/components.json card.costKinds order (actions, stamina, mana): Actions always, the pools when the card costs them.
            var amounts = new[] { def.Num(K.Cost), def.Num(K.StaminaCost), def.Num(K.ManaCost) };
            for (var i = 0; i < costKinds.Count && i < amounts.Length; i++)
                if (i == 0 || amounts[i] > 0) pick.Costs.Add(new RewardCost { Kind = costKinds[i], Text = CombatText.Number(amounts[i]) });
            return pick;
        }

        // ------------------------------------------------------------------ sentences (card.js staticTokens, validate.js relicTokens)

        /// <summary>A text template with its tokens filled from the numbers; an unresolved token keeps its braces (shipped honest degrade).</summary>
        internal static string Sentence(CombatData data, string template, JObject tokens) =>
            CombatViewModel.ResolveText(template ?? string.Empty, tokens);

        /// <summary>computeTokenBindings over an effect list: each tokenizable literal number under its token.</summary>
        internal static JObject EffectTokens(CombatData data, JToken effects)
        {
            var tokens = new JObject();
            var list = Js.Items(effects).ToList();
            foreach (var binding in CombatPreview.TokenBindings(data, effects))
            {
                var parts = binding.Key.Split(new[] { V.KeySeparator }, StringSplitOptions.None);
                if (parts.Length <= 1 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index >= list.Count) continue;
                var value = (list[index] as JObject)?[parts[1]];
                if (Js.IsNum(value)) tokens[binding.Value] = value.DeepClone();
            }
            return tokens;
        }

        /// <summary>A rule's or relic's trigger ops flattened (triggers[].do, then effects, then do).</summary>
        internal static JArray TriggerOps(JObject def)
        {
            var ops = new JArray();
            foreach (var t in Js.Items(def?[K.Triggers]).OfType<JObject>()) foreach (var op in Js.Items(t[K.Do])) ops.Add(op.DeepClone());
            foreach (var op in Js.Items(def?[K.Effects])) ops.Add(op.DeepClone());
            foreach (var op in Js.Items(def?[K.Do])) ops.Add(op.DeepClone());
            return ops;
        }

        /// <summary>
        /// relicTokens(def, rules): the relic's own ops bound by position, its passive modifiers by the
        /// ui/components.json rewards.modifierTokens rule, and each carried property rule's ops (a rule's numbers are
        /// bound by op here; the shipped build binds them by variable name, which agrees wherever the variable is named
        /// after its op).
        /// </summary>
        internal static JObject RelicTokens(CombatData data, JObject def, ComponentDefaults components)
        {
            var tokens = EffectTokens(data, TriggerOps(def));
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in Js.Items(def.Obj(K.Passives)?[K.Modifiers]).OfType<JObject>())
            {
                if (!(components.RewardModifierTokens?[row.Str(RunFlowKeys.Tag) ?? string.Empty] is JObject rule)) continue;
                var baseName = row.Str(rule.Str(RunFlowKeys.From)) + rule.Str(RunFlowKeys.Suffix);
                counts[baseName] = counts.TryGetValue(baseName, out var n) ? n + 1 : 1;
                var token = counts[baseName] == 1 ? baseName : baseName + V.TokenRepeatSeparator + counts[baseName].ToString(CultureInfo.InvariantCulture);
                var value = row[rule.Str(RunFlowKeys.Value)];
                if (Js.IsNum(value)) tokens[token] = value.DeepClone();
            }
            foreach (var tag in Js.Items(def[K.PropertyTags]).Select(Js.Str).Where(t => t != null && data.PropertyRules.Has(t)))
                foreach (var p in EffectTokens(data, TriggerOps(data.PropertyRules.Get(tag))).Properties())
                    if (tokens[p.Name] == null) tokens[p.Name] = p.Value.DeepClone();
            return tokens;
        }
    }
}
