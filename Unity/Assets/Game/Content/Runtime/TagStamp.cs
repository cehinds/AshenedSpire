using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.Content
{
    /// <summary>
    /// Port of the shipped tag join: model/tags.js <c>tagIndex</c> and model/registries.js <c>stampTags</c>. Every
    /// collection a tagFamilies row names (by dotted <c>source</c>) comes back with its objects carrying their
    /// <c>tags</c> (aside domains dropped, property tags split to <c>propertyTags</c>, classification rows as
    /// <c>kindIds</c>); armaments and armour also get <c>entityTags</c>/<c>itemTypeTags</c>/<c>itemTypes</c>.
    /// </summary>
    internal static class TagStamp
    {
        private static readonly HashSet<string> EquipmentItemFamilies = new HashSet<string>(StringComparer.Ordinal)
        {
            TagKeys.ArmamentFamily, TagKeys.ArmourFamily,
        };

        private sealed class Index
        {
            public readonly Dictionary<(string Family, string Scope, string ObjectId), List<string>> Tags =
                new Dictionary<(string, string, string), List<string>>();
            public readonly Dictionary<(string Family, string Scope, string ObjectId), List<string>> Kinds =
                new Dictionary<(string, string, string), List<string>>();
        }

        private static void AddUnique(Dictionary<(string, string, string), List<string>> map, (string, string, string) key, string tag)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
            if (!list.Contains(tag, StringComparer.Ordinal)) list.Add(tag);
        }

        private static HashSet<string> TagIdsWhere(JObject bundle, Func<JToken, bool> predicate) =>
            new HashSet<string>(JsValues.Items(bundle[RegistryKeys.TagRegistry])
                .Where(t => t is JObject && predicate(t))
                .Select(t => JsValues.Text(t[RegistryKeys.Id])), StringComparer.Ordinal);

        /// <summary>
        /// Source path → the stamped copy of that collection, in tagFamilies order (a later family naming the same
        /// source replaces the earlier one, as the shipped Map does).
        /// </summary>
        public static List<KeyValuePair<string, JArray>> Stamp(JObject bundle)
        {
            // tagIndex: families by name (a repeated name keeps its first position and its last row).
            var familyOrder = new List<string>();
            var families = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var row in JsValues.Items(bundle[RegistryKeys.TagFamilies]).OfType<JObject>())
            {
                if (!JsValues.Truthy(row[TagKeys.Family])) continue;
                var name = JsValues.Text(row[TagKeys.Family]);
                if (!families.ContainsKey(name)) familyOrder.Add(name);
                families[name] = row;
            }
            var asideDomains = new HashSet<string>(JsValues.Items(bundle[RegistryKeys.TagDomains]).OfType<JObject>()
                .Where(d => d[TagKeys.Aside] != null && d[TagKeys.Aside].Type == JTokenType.Boolean && d[TagKeys.Aside].Value<bool>())
                .Select(d => JsValues.Text(d[RegistryKeys.Id])), StringComparer.Ordinal);
            var aside = TagIdsWhere(bundle, t => asideDomains.Contains(JsValues.Text(t[TagKeys.Domain])) && !JsValues.IsNullish(t[TagKeys.Domain]));
            var kindIds = TagIdsWhere(bundle, t => JsValues.Str(t[TagKeys.Domain]) == TagKeys.ClassificationDomain);
            var propertyIds = TagIdsWhere(bundle, t => JsValues.Str(t[TagKeys.Domain]) == TagKeys.PropertyDomain);

            var index = new Index();
            foreach (var row in JsValues.Items(bundle[RegistryKeys.Tagging]).OfType<JObject>())
            {
                var family = JsValues.Text(row[TagKeys.Family]);
                if (JsValues.IsNullish(row[TagKeys.Family]) || !families.ContainsKey(family)) continue;
                var key = (family, JsValues.Truthy(row[TagKeys.Scope]) ? JsValues.Text(row[TagKeys.Scope]) : string.Empty, JsValues.Text(row[TagKeys.ObjectId]));
                var tagId = JsValues.Text(row[TagKeys.TagId]);
                if (kindIds.Contains(tagId)) AddUnique(index.Kinds, key, tagId);
                if (aside.Contains(tagId)) continue;
                AddUnique(index.Tags, key, tagId);
            }

            var stamped = new List<KeyValuePair<string, JArray>>();
            foreach (var name in familyOrder)
            {
                var spec = families[name];
                var source = JsValues.Str(spec[TagKeys.Source]);
                if (string.IsNullOrEmpty(source)) continue;
                if (!(RegistryBundle.AtPath(bundle, source) is JArray node)) continue;
                var scopeField = JsValues.Truthy(spec[TagKeys.ScopeField]) ? JsValues.Text(spec[TagKeys.ScopeField]) : null;
                var rows = new JArray();
                foreach (var def in node) rows.Add(StampOne(def, name, scopeField, index, propertyIds));
                var existing = stamped.FindIndex(kv => kv.Key == source);
                var entry = new KeyValuePair<string, JArray>(source, rows);
                if (existing >= 0) stamped[existing] = entry;
                else stamped.Add(entry);
            }
            return stamped;
        }

        private static JToken StampOne(JToken defToken, string family, string scopeField, Index index, HashSet<string> propertyIds)
        {
            if (!JsValues.Truthy(defToken) || !(defToken is JObject def)) return defToken?.DeepClone();
            var scope = scopeField != null && JsValues.Truthy(def[scopeField]) ? JsValues.Text(def[scopeField]) : string.Empty;
            var key = (family, scope, JsValues.Text(def[RegistryKeys.Id]));
            var authored = index.Tags.TryGetValue(key, out var a) ? a : new List<string>();
            var entityTags = authored.Where(t => !propertyIds.Contains(t)).ToList();
            var propertyTags = authored.Where(t => propertyIds.Contains(t)).ToList();
            var kinds = index.Kinds.TryGetValue(key, out var k) ? k : new List<string>();

            var result = JsValues.Spread(def);
            if (propertyTags.Count > 0) result[TagKeys.PropertyTags] = new JArray(propertyTags);
            if (kinds.Count > 0) result[TagKeys.KindIds] = new JArray(kinds);
            if (!EquipmentItemFamilies.Contains(family))
            {
                result[TagKeys.Tags] = new JArray(entityTags);
                return result;
            }
            // Equipment splits its tags: the whole vocabulary, the item-type half the Armoury names the piece by,
            // and the gameplay/presentation half that stays `tags`.
            var itemTypeTags = entityTags.Where(t => ItemTypeLabel(t).Length > 0).ToList();
            result[TagKeys.EntityTags] = new JArray(entityTags);
            result[TagKeys.ItemTypeTags] = new JArray(itemTypeTags);
            result[TagKeys.ItemTypes] = new JArray(itemTypeTags.Select(t => new JObject
            {
                [TagKeys.ItemTypeTag] = t,
                [TagKeys.ItemTypeLabel] = ItemTypeLabel(t),
            }));
            result[TagKeys.Tags] = new JArray(entityTags.Where(t => ItemTypeLabel(t).Length == 0));
            return result;
        }

        /// <summary><c>item:magic-focus</c> → <c>Magic Focus</c>; empty for a tag that is not an item type (the shipped null and '' alike).</summary>
        public static string ItemTypeLabel(string tag)
        {
            if (tag == null || !tag.StartsWith(TagKeys.ItemTypePrefix, StringComparison.Ordinal)) return string.Empty;
            var words = tag.Substring(TagKeys.ItemTypePrefix.Length)
                .Split(new[] { TagKeys.ItemTypeWordSeparator }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Substring(0, 1).ToUpperInvariant() + w.Substring(1));
            return string.Join(TagKeys.ItemTypeLabelJoiner, words);
        }
    }
}
