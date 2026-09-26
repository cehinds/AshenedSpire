using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using M = Ashen.Generated.CombatMessages;
using V = Ashen.Generated.CombatValues;
using CombatMath = Ashen.Generated.CombatMath;

namespace Ashen.Domain.Combat
{
    /// <summary>A namespaced item ref split into its parts (shipped model/itemUpgrades.js itemRefIdentity).</summary>
    public sealed class ItemIdentity
    {
        public string ItemRef;
        public string ItemKind;
        public string ItemId;
        public string ClassId;
    }

    /// <summary>One upgrade tag read against the closed vocabulary (shipped parseItemUpgradeTag).</summary>
    public sealed class UpgradeTag
    {
        public string Kind;
        public string Resource;
        public string Field;
        public string PassiveKey;
        public string AttributeId;
        public string Role;
        public string Op;
    }

    /// <summary>
    /// The closed, data-owned upgrade vocabulary (shipped model/itemUpgrades.js): item refs, the exact item/tier rows of
    /// equipment.itemUpgradeChanges, the tag grammar (rules/combatEngine.json itemUpgrades) and the card face a Smithing
    /// tier gives an armament's basic card (applyItemCardUpgradeRows, which resolveCard applies per tier).
    /// </summary>
    public static class ItemUpgrades
    {
        private static readonly int Two = (int)CombatMath.Two;
        private static readonly int Three = Two + 1;
        private static readonly int Four = Two * Two;

        private static JObject Vocabulary(CombatData d) => d.Engine.Obj(K.ItemUpgrades) ?? new JObject();

        private static List<string> List(CombatData d, string key) => Js.Items(Vocabulary(d)[key]).Select(Js.Str).ToList();

        /// <summary>The Smithing Stone cost tag every authored tier carries (UPGRADE_COST_TAG).</summary>
        public static string CostTag(CombatData d) => Vocabulary(d).Str(K.CostTag);

        private static string Num(double n) => n.ToString(CultureInfo.InvariantCulture);

        /// <summary>itemRefIdentity(itemRef): armament/&lt;id&gt;, armor/&lt;class&gt;/&lt;id&gt; or relic/&lt;id&gt;; null otherwise.</summary>
        public static ItemIdentity Identity(string itemRef)
        {
            var parts = itemRef != null ? itemRef.Split(V.ItemRefSeparator[0]) : new string[0];
            if (parts.Length == 0) return null;
            if (parts[0] == V.ArmamentRefPrefix && parts.Length == Two && parts[1].Length > 0)
                return new ItemIdentity { ItemRef = itemRef, ItemKind = V.ArmamentRefPrefix, ItemId = parts[1] };
            if (parts[0] == V.ArmorRefPrefix && parts.Length == Three && parts[1].Length > 0 && parts[Two].Length > 0)
                return new ItemIdentity { ItemRef = itemRef, ItemKind = V.Armor, ItemId = parts[Two], ClassId = parts[1] };
            if (parts[0] == V.RelicRefPrefix && parts.Length == Two && parts[1].Length > 0)
                return new ItemIdentity { ItemRef = itemRef, ItemKind = V.RelicKind, ItemId = parts[1] };
            return null;
        }

        /// <summary>parseItemUpgradeTag(tag, attributeIds): the descriptor a registered tag names, or null.</summary>
        public static UpgradeTag ParseTag(CombatData d, string tag)
        {
            var vocab = Vocabulary(d);
            if (tag == CostTag(d)) return new UpgradeTag { Kind = V.UpgradeCostKind, Resource = V.SmithingStoneResource };
            if (tag == vocab.Str(K.EquipmentPoiseTag)) return new UpgradeTag { Kind = V.EquipmentPoiseKind, Field = K.PoiseThreshold };
            var passive = vocab.Obj(K.RelicPassiveTags);
            if (tag != null && passive != null && passive[tag] != null) return new UpgradeTag { Kind = V.RelicPassiveKind, PassiveKey = passive.Str(tag) };
            var parts = tag != null ? tag.Split(V.TagSeparator[0]) : new string[0];
            if (parts.Length == Two && parts[0] == V.RequirementKind && d.Attributes.Has(parts[1]))
                return new UpgradeTag { Kind = V.RequirementKind, AttributeId = parts[1] };
            if (parts.Length != Four || parts[0] != V.UpgradeCardPart || !List(d, K.CardRoles).Contains(parts[1])) return null;
            var third = parts[Two];
            var fourth = parts[Three];
            if (third == V.UpgradeEffectPart && List(d, K.CardEffects).Contains(fourth)) return new UpgradeTag { Kind = V.CardEffectKind, Role = parts[1], Op = fourth };
            if (third == V.UpgradeCostPart && List(d, K.CardResources).Contains(fourth)) return new UpgradeTag { Kind = V.CardCostKind, Role = parts[1], Resource = fourth };
            return null;
        }

        /// <summary>itemUpgradeTagMatchesKind(descriptor, itemKind).</summary>
        public static bool TagMatchesKind(UpgradeTag t, string itemKind)
        {
            if (t == null) return false;
            if (t.Kind == V.UpgradeCostKind) return itemKind == V.ArmamentRefPrefix || itemKind == V.Armor || itemKind == V.RelicKind;
            if (itemKind == V.ArmamentRefPrefix) return t.Kind == V.RequirementKind || t.Kind == V.CardEffectKind || t.Kind == V.CardCostKind;
            if (itemKind == V.Armor) return t.Kind == V.EquipmentPoiseKind;
            if (itemKind == V.RelicKind) return t.Kind == V.RelicPassiveKind;
            return false;
        }

        /// <summary>itemUpgradeRows(registries, itemRef, nextTier): the authored rows of one exact tier.</summary>
        public static List<JObject> Rows(CombatData d, string itemRef, double nextTier) =>
            Js.Items(d.Equipment[K.ItemUpgradeChanges]).OfType<JObject>().Where(r => r.Str(K.ItemRef) == itemRef && Js.IsNum(r[K.NextTier]) && r.Num(K.NextTier) == nextTier).ToList();

        /// <summary>itemUpgradeTiers(registries, itemRef): the distinct tiers an item authors, ascending.</summary>
        public static List<double> Tiers(CombatData d, string itemRef)
        {
            var tiers = new List<double>();
            foreach (var r in Js.Items(d.Equipment[K.ItemUpgradeChanges]).OfType<JObject>())
                if (r.Str(K.ItemRef) == itemRef && !tiers.Contains(r.Num(K.NextTier))) tiers.Add(r.Num(K.NextTier));
            tiers.Sort();
            return tiers;
        }

        /// <summary>
        /// applyItemCardUpgradeRows(def, role, rows): one tier's card changes for a role — an effect's amount or a cost
        /// field moved by the row's value — and a '+' on the name when anything changed. Throws by name on a row the
        /// face cannot take.
        /// </summary>
        public static JObject ApplyCardRows(CombatData d, JObject def, string role, IEnumerable<JObject> rows)
        {
            var result = Js.Spread(def);
            result[K.Effects] = new JArray(Js.Items(def[K.Effects]).Select(e => e is JObject o ? (JToken)Js.Spread(o) : e.DeepClone()));
            var changed = false;
            foreach (var row in rows)
            {
                var t = ParseTag(d, row.Str(K.Tag));
                if (t == null || t.Role != role) continue;
                var at = Num(row.Num(K.NextTier));
                if (t.Kind == V.CardEffectKind)
                {
                    var matches = Js.Items(result[K.Effects]).OfType<JObject>().Where(e => e.Str(K.Op) == t.Op).ToList();
                    if (matches.Count != 1)
                        throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UpgradeEffectCount, row.Str(K.ItemRef), at, row.Str(K.Tag), t.Op, role, matches.Count));
                    var target = matches[0];
                    if (!Js.IsNum(target[K.Amount])) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UpgradeNonNumericAmount, row.Str(K.ItemRef), at, row.Str(K.Tag)));
                    var next = target.Num(K.Amount) + row.Num(K.Value);
                    if (next < 0) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UpgradeBelowZero, row.Str(K.ItemRef), at, row.Str(K.Tag), K.Amount));
                    target.Put(K.Amount, next);
                    changed = true;
                }
                else if (t.Kind == V.CardCostKind)
                {
                    var field = t.Resource == V.ActionResource ? K.Cost : t.Resource + V.CostFieldSuffix;
                    var before = Js.Nullish(result[field]) ? Js.N(0) : result[field];
                    if (!Js.IsNum(before)) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UpgradeNonNumericField, row.Str(K.ItemRef), at, row.Str(K.Tag), field));
                    var next = Js.D(before) + row.Num(K.Value);
                    if (next < 0) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, M.UpgradeBelowZero, row.Str(K.ItemRef), at, row.Str(K.Tag), field));
                    result.Put(field, next);
                    changed = true;
                }
            }
            if (changed) result.Put(K.Name, (Js.Truthy(result[K.Name]) ? JsString(result[K.Name]) : string.Empty).TrimEnd(V.UpgradeSuffix.ToCharArray()) + V.UpgradeSuffix);
            return result;
        }

        /// <summary><c>String(v)</c> for a truthy name (a string in every shipped row).</summary>
        private static string JsString(JToken v) => Js.IsStr(v) ? Js.Str(v) : v.ToString();
    }
}
