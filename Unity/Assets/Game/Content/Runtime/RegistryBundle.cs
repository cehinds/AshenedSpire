using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.Content
{
    /// <summary>
    /// Reassembles the shipped content bundle shape (arrays in authoring order, display text attached) from the
    /// id-keyed content files of a <see cref="ContentSet"/>: the input the shipped createRegistries() receives.
    /// Authoring order comes from rules/rowOrder.json (canonical JSON sorts keys); rows a layer added that the order
    /// does not list follow in ordinal key order. Display text is re-attached from strings/en.json using the
    /// prefix and field→name map of rules/stringKeys.json.
    /// </summary>
    internal sealed class RegistryBundle
    {
        private enum Shape { Keyed, List, Document }

        private sealed class Source
        {
            public Source(string path, string file, Shape shape) { Path = path; File = file; Shape = shape; }
            public string Path { get; }
            public string File { get; }
            public Shape Shape { get; }
        }

        private static string At(params string[] parts) => string.Join(RegistryKeys.PathSeparator, parts);

        /// <summary>Bundle path → content file. Documents come first so the collections nested in them land on top.</summary>
        private static readonly Source[] Sources =
        {
            new Source(RegistryKeys.Balance, ContentFiles.BalanceBalance, Shape.Document),
            new Source(RegistryKeys.Equipment, ContentFiles.CatalogEquipmentMeta, Shape.Document),
            new Source(RegistryKeys.Cards, ContentFiles.CatalogCards, Shape.Keyed),
            new Source(RegistryKeys.Relics, ContentFiles.CatalogRelics, Shape.Keyed),
            new Source(RegistryKeys.Statuses, ContentFiles.CatalogStatuses, Shape.Keyed),
            new Source(RegistryKeys.Stances, ContentFiles.CatalogStances, Shape.Keyed),
            new Source(RegistryKeys.Keywords, ContentFiles.CatalogKeywords, Shape.Keyed),
            new Source(RegistryKeys.Enemies, ContentFiles.CatalogEnemies, Shape.Keyed),
            new Source(RegistryKeys.Encounters, ContentFiles.CatalogEncounters, Shape.Keyed),
            new Source(RegistryKeys.Flasks, ContentFiles.CatalogFlasks, Shape.Keyed),
            new Source(RegistryKeys.Classes, ContentFiles.CatalogClasses, Shape.Keyed),
            new Source(RegistryKeys.Events, ContentFiles.CatalogEvents, Shape.Keyed),
            new Source(EventKeys.Speakers, ContentFiles.CatalogSpeakers, Shape.Keyed),
            new Source(RegistryKeys.Attributes, ContentFiles.CatalogAttributes, Shape.Keyed),
            new Source(RegistryKeys.ClassTree, ContentFiles.CatalogClassTree, Shape.Keyed),
            new Source(MapKeys.Seats, ContentFiles.CatalogSeats, Shape.Keyed),
            new Source(RunKeys.CreationModes, ContentFiles.CatalogCreationModes, Shape.Keyed),
            new Source(RunKeys.CharacterCreation, ContentFiles.CatalogCharacterCreation, Shape.Document),
            new Source(RunKeys.AttributeRules, ContentFiles.RulesAttributeRules, Shape.Document),
            new Source(RunKeys.DerivedStatRules, ContentFiles.RulesDerivedStatRules, Shape.Document),
            new Source(RewardsKeys.Nodes, ContentFiles.TagsNodes, Shape.Keyed),
            new Source(RegistryKeys.PropertyRules, ContentFiles.TagsPropertyRules, Shape.List),
            new Source(RegistryKeys.TagDomains, ContentFiles.TagsTagDomains, Shape.Keyed),
            new Source(RegistryKeys.TagRegistry, ContentFiles.TagsTags, Shape.Keyed),
            new Source(RegistryKeys.TagFamilies, ContentFiles.TagsTagFamilies, Shape.Keyed),
            new Source(RegistryKeys.Tagging, ContentFiles.TagsTagging, Shape.Keyed),
            new Source(At(RegistryKeys.Equipment, RegistryKeys.Armaments), ContentFiles.CatalogArmaments, Shape.Keyed),
            new Source(At(RegistryKeys.Equipment, RegistryKeys.Armour), ContentFiles.CatalogArmour, Shape.Keyed),
            new Source(At(RegistryKeys.Equipment, RegistryKeys.StartingKits), ContentFiles.CatalogStartingKits, Shape.Keyed),
            new Source(At(RegistryKeys.Equipment, RegistryKeys.BasicCardProfiles), ContentFiles.CatalogBasicCardProfiles, Shape.Keyed),
            new Source(At(RegistryKeys.Equipment, RegistryKeys.ItemUpgradeChanges), ContentFiles.CatalogItemUpgradeChanges, Shape.List),
            new Source(At(RegistryKeys.Equipment, RegistryKeys.CardExposure), ContentFiles.CatalogCardExposure, Shape.List),
            new Source(At(RegistryKeys.Equipment, RegistryKeys.CardTagging), ContentFiles.CatalogCardTagging, Shape.List),
            new Source(At(RegistryKeys.Equipment, RegistryKeys.EquipmentRequirements), ContentFiles.CatalogEquipmentRequirements, Shape.List),
            new Source(At(RegistryKeys.Equipment, RegistryKeys.Slots), ContentFiles.CatalogEquipmentSlots, Shape.Document),
        };

        private readonly ContentSet _content;
        private readonly JObject _strings;
        private readonly JObject _rowOrder;
        private readonly JObject _stringKeys;
        private readonly JObject _textAnchors;

        private RegistryBundle(ContentSet content, JObject strings)
        {
            _content = content;
            _strings = strings ?? new JObject();
            _rowOrder = content.Get(ContentFiles.RulesRowOrder) as JObject ?? new JObject();
            _stringKeys = content.Get(ContentFiles.RulesStringKeys) as JObject ?? new JObject();
            _textAnchors = content.Get(ContentFiles.RulesTextAnchors) as JObject ?? new JObject();
        }

        /// <summary>The bundle object: { cards: [...], equipment: { armaments: [...], ... }, balance: {...}, tagging: [...], ... }.</summary>
        public static JObject Load(ContentSet content, JObject strings)
        {
            var loader = new RegistryBundle(content, strings);
            var bundle = new JObject();
            foreach (var source in Sources)
            {
                if (!content.Contains(source.File)) continue;
                SetPath(bundle, source.Path, loader.Read(source));
            }
            return bundle;
        }

        private JToken Read(Source source)
        {
            var doc = _content.Get(source.File);
            switch (source.Shape)
            {
                case Shape.List:
                    return (doc as JObject)?[ContentLayout.ListRows] as JArray ?? new JArray();
                case Shape.Keyed:
                    return Keyed(source.File, doc as JObject ?? new JObject());
                default:
                    return doc;
            }
        }

        private JArray Keyed(string file, JObject doc)
        {
            var order = OrderedKeys(file, doc);
            var spec = _stringKeys[file] as JObject;
            var prefix = (string)spec?[RuleKeys.Prefix];
            var fields = spec?[RegistryKeys.StringKeyFields] as JObject;
            var anchorsByRow = _textAnchors[file] as JObject;
            var rows = new JArray();
            foreach (var key in order)
            {
                var row = doc[key];
                if (row is JObject obj && fields != null && prefix != null)
                {
                    var texts = new List<KeyValuePair<string, JToken>>();
                    foreach (var field in fields.Properties())
                    {
                        var text = _strings[string.Join(ContentLayout.SchemaNameSeparator, prefix, key, (string)field.Value)];
                        if (text != null && text.Type == JTokenType.String) texts.Add(new KeyValuePair<string, JToken>(field.Name, text.DeepClone()));
                    }
                    row = texts.Count == 0 ? obj : Attach(obj, texts, anchorsByRow?[key] as JObject);
                }
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>
        /// Re-attaches display text where it was authored (D-069): each field goes after the key rules/textAnchors.json
        /// recorded for it ('' = first; a field may follow another text field). Text with no anchor, or whose anchor the
        /// row no longer has, follows the row's own keys.
        /// </summary>
        private static JObject Attach(JObject row, List<KeyValuePair<string, JToken>> texts, JObject anchors)
        {
            var pending = texts.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var result = new JObject();
            void Place(string anchor)
            {
                foreach (var field in texts.Select(kv => kv.Key).Where(f => pending.ContainsKey(f) && anchors?[f] is JValue v && (string)v == anchor).ToList())
                {
                    result[field] = pending[field];
                    pending.Remove(field);
                    Place(field);
                }
            }
            Place(string.Empty);
            foreach (var p in row.Properties())
            {
                result[p.Name] = p.Value;
                Place(p.Name);
            }
            foreach (var kv in texts.Where(kv => pending.ContainsKey(kv.Key))) result[kv.Key] = kv.Value;
            return result;
        }

        private List<string> OrderedKeys(string file, JObject doc)
        {
            var present = new HashSet<string>(doc.Properties().Select(p => p.Name), StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var keys = new List<string>();
            foreach (var key in (_rowOrder[file] as JArray ?? new JArray()).Select(t => (string)t))
                if (key != null && present.Contains(key) && seen.Add(key)) keys.Add(key);
            keys.AddRange(present.Where(k => !seen.Contains(k)).OrderBy(k => k, StringComparer.Ordinal));
            return keys;
        }

        /// <summary>Writes <paramref name="value"/> at a dotted path, creating (or replacing non-object) parents.</summary>
        private static void SetPath(JObject root, string path, JToken value)
        {
            var parts = path.Split(RegistryKeys.PathSeparator[0]);
            var node = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (!(node[parts[i]] is JObject child))
                {
                    child = new JObject();
                    node[parts[i]] = child;
                }
                node = child;
            }
            node[parts[parts.Length - 1]] = value;
        }

        /// <summary>The value at a dotted path, walking objects only (the shipped <c>atPath</c>), or null.</summary>
        public static JToken AtPath(JToken root, string path)
        {
            var node = root;
            foreach (var part in path.Split(RegistryKeys.PathSeparator[0]))
            {
                if (!(node is JObject o)) return null;
                node = o[part];
            }
            return node;
        }
    }
}
