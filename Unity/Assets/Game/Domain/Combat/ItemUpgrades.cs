using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using V = Ashen.Generated.CombatValues;

namespace Ashen.Domain.Combat
{
    /// <summary>
    /// One parsed upgrade tag (shipped model/itemUpgrades.js parseItemUpgradeTag): what kind of change it is and what
    /// it names — the card role, effect op or cost resource, the attribute of a requirement, or the relic passive key.
    /// </summary>
    public sealed class UpgradeTag
    {
        public string Kind;
        public string Role;
        public string Op;
        public string Resource;
        public string AttributeId;
        public string PassiveKey;
    }

    /// <summary>
    /// The closed, data-owned upgrade vocabulary (shipped model/itemUpgrades.js): content selects exact item/tier rows
    /// (equipment.itemUpgradeChanges) and the code only interprets the registered tags. The vocabulary (the cost tag,
    /// the card roles, effects and resources, the tag grammar) is rules/combatEngine.json itemUpgrades.
    /// </summary>
    public static class ItemUpgrades
    {
        private static readonly Regex RequirementTag = new Regex(Ashen.Generated.CombatPatterns.UpgradeRequirementTag, RegexOptions.CultureInvariant);
        private static readonly Regex CardTag = new Regex(Ashen.Generated.CombatPatterns.UpgradeCardTag, RegexOptions.CultureInvariant);

        private static JObject Rules(CombatData data) => data.Engine.Obj(K.ItemUpgrades) ?? new JObject();

        private static List<string> List(CombatData data, string key) => Js.Items(Rules(data)[key]).Select(Js.Str).ToList();

        /// <summary>parseItemUpgradeTag(tag, attributeIds): the descriptor, or null for a tag outside the vocabulary.</summary>
        public static UpgradeTag Parse(CombatData data, string tag)
        {
            var rules = Rules(data);
            if (tag == null) return null;
            if (tag == rules.Str(K.CostTag)) return new UpgradeTag { Kind = V.UpgradeCostKind, Resource = V.SmithingStoneResource };
            if (tag == rules.Str(K.EquipmentPoiseTag)) return new UpgradeTag { Kind = V.EquipmentPoiseKind };
            var passive = rules.Obj(K.RelicPassiveTags)?[tag];
            if (passive != null) return new UpgradeTag { Kind = V.RelicPassiveKind, PassiveKey = Js.Str(passive) };
            var requirement = RequirementTag.Match(tag);
            if (requirement.Success && data.Attributes.Has(requirement.Groups[V.GroupAttribute].Value))
                return new UpgradeTag { Kind = V.RequirementKind, AttributeId = requirement.Groups[V.GroupAttribute].Value };
            var card = CardTag.Match(tag);
            if (!card.Success) return null;
            var role = card.Groups[V.GroupRole].Value;
            var part = card.Groups[V.GroupPart].Value;
            var name = card.Groups[V.GroupName].Value;
            if (!List(data, K.CardRoles).Contains(role)) return null;
            if (part == rules.Str(K.EffectPart) && List(data, K.CardEffects).Contains(name))
                return new UpgradeTag { Kind = V.CardEffectKind, Role = role, Op = name };
            if (part == rules.Str(K.CostPart) && List(data, K.CardResources).Contains(name))
                return new UpgradeTag { Kind = V.CardCostKind, Role = role, Resource = name };
            return null;
        }

        /// <summary>The card field a cost resource writes: the action resource is <c>cost</c>, any other <c>&lt;resource&gt;Cost</c>.</summary>
        public static string CostField(CombatData data, string resource) =>
            resource == Rules(data).Str(K.ActionResource) ? Rules(data).Str(K.ActionCostField) : resource + Rules(data).Str(K.ResourceCostSuffix);

        /// <summary>itemUpgradeRows(registries, itemRef, nextTier): the exact authored rows of one tier.</summary>
        public static List<JObject> Rows(CombatData data, string itemRef, double nextTier) =>
            Js.Items(data.Equipment[K.ItemUpgradeChanges]).OfType<JObject>()
                .Where(row => row.Str(K.ItemRef) == itemRef && Js.IsNum(row[K.NextTier]) && row.Num(K.NextTier) == nextTier).ToList();

        /// <summary>
        /// applyItemCardUpgradeRows(def, role, rows, attributeIds): one tier's card rows applied to a resolved face —
        /// an effect amount or a cost moved by the row's value (never below zero), and the name marked once.
        /// </summary>
        public static JObject ApplyCardUpgradeRows(CombatData data, JObject def, string role, IEnumerable<JObject> rows)
        {
            var result = Js.Spread(def);
            var effects = new JArray(Js.Items(def[K.Effects]).OfType<JObject>().Select(e => (JToken)Js.Spread(e)));
            result[K.Effects] = effects;
            var changed = false;
            foreach (var row in rows)
            {
                var descriptor = Parse(data, row.Str(K.Tag));
                if (descriptor == null || descriptor.Role != role) continue;
                if (descriptor.Kind == V.CardEffectKind)
                {
                    var matches = effects.OfType<JObject>().Where(e => e.Str(K.Op) == descriptor.Op).ToList();
                    if (matches.Count != 1)
                        throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UpgradeEffectNotSingle, row.Str(K.ItemRef), Js.D(row[K.NextTier]), row.Str(K.Tag), descriptor.Op, role, matches.Count));
                    var target = matches[0];
                    if (!Js.IsNum(target[K.Amount]))
                        throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UpgradeTargetNotNumeric, row.Str(K.ItemRef), Js.D(row[K.NextTier]), row.Str(K.Tag), K.Amount));
                    var next = target.Num(K.Amount) + row.Num(K.Value);
                    if (next < 0) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UpgradeBelowZero, row.Str(K.ItemRef), Js.D(row[K.NextTier]), row.Str(K.Tag), K.Amount));
                    target.Put(K.Amount, next);
                    changed = true;
                }
                else if (descriptor.Kind == V.CardCostKind)
                {
                    var field = CostField(data, descriptor.Resource);
                    var before = Js.Nullish(result[field]) ? Js.N(0) : result[field];
                    if (!Js.IsNum(before))
                        throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UpgradeTargetNotNumeric, row.Str(K.ItemRef), Js.D(row[K.NextTier]), row.Str(K.Tag), field));
                    var next = Js.D(before) + row.Num(K.Value);
                    if (next < 0) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UpgradeBelowZero, row.Str(K.ItemRef), Js.D(row[K.NextTier]), row.Str(K.Tag), field));
                    result.Put(field, next);
                    changed = true;
                }
            }
            if (changed)
            {
                var suffix = Rules(data).Str(K.NameSuffix);
                var name = Js.Truthy(result[K.Name]) ? JsString(result[K.Name]) : string.Empty;
                while (suffix.Length > 0 && name.EndsWith(suffix, StringComparison.Ordinal)) name = name.Substring(0, name.Length - suffix.Length);
                result[K.Name] = name + suffix;
            }
            return result;
        }

        /// <summary><c>String(v)</c> for a card name.</summary>
        private static string JsString(JToken v) => Js.IsStr(v) ? Js.Str(v) : v.ToString(Newtonsoft.Json.Formatting.None);
    }
}
