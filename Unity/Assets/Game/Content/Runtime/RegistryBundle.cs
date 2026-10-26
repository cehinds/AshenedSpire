using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.Content
{
    /// <summary>
    /// Reassembles the Core 2 content bundle (WP-2) from the id-keyed files under profile/bundle/ (the bundle the web projects through
    /// applyGameProfileToContent): arrays in projection order, the input the shipped createRegistries() receives. Order comes from
    /// rules/rowOrder.json (canonical JSON sorts keys); rows a layer added that the order does not list follow in ordinal key order.
    /// Display text is already inline in those rows.
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
            new Source(RegistryKeys.Balance, ContentFiles.ProfileBundleBalance, Shape.Document),
            new Source(RegistryKeys.Equipment, ContentFiles.ProfileBundleEquipmentMeta, Shape.Document),
            new Source(RegistryKeys.Cards, ContentFiles.ProfileBundleCards, Shape.Keyed),
            new Source(RegistryKeys.Relics, ContentFiles.ProfileBundleRelics, Shape.Keyed),
            new Source(RegistryKeys.Statuses, ContentFiles.ProfileBundleStatuses, Shape.Keyed),
            new Source(RegistryKeys.Stances, ContentFiles.ProfileBundleStances, Shape.Keyed),
            new Source(RegistryKeys.Keywords, ContentFiles.ProfileBundleKeywords, Shape.Keyed),
            new Source(RegistryKeys.Enemies, ContentFiles.ProfileBundleEnemies, Shape.Keyed),
            new Source(RegistryKeys.Encounters, ContentFiles.ProfileBundleEncounters, Shape.Keyed),
            new Source(RegistryKeys.Flasks, ContentFiles.ProfileBundleFlasks, Shape.Keyed),
            new Source(RegistryKeys.Classes, ContentFiles.ProfileBundleClasses, Shape.Keyed),
            new Source(RegistryKeys.Events, ContentFiles.ProfileBundleEvents, Shape.Keyed),
            new Source(EventKeys.Speakers, ContentFiles.ProfileBundleSpeakers, Shape.Keyed),
            new Source(RegistryKeys.Attributes, ContentFiles.ProfileBundleAttributes, Shape.Keyed),
            new Source(RegistryKeys.ClassTree, ContentFiles.ProfileBundleClassTree, Shape.Keyed),
            new Source(MapKeys.Seats, ContentFiles.ProfileBundleSeats, Shape.Keyed),
            new Source(RunKeys.CreationModes, ContentFiles.ProfileBundleCreationModes, Shape.Keyed),
            new Source(RunKeys.CharacterCreation, ContentFiles.ProfileBundleCharacterCreation, Shape.Document),
            new Source(RunKeys.AttributeRules, ContentFiles.ProfileBundleAttributeRules, Shape.Document),
            new Source(RunKeys.DerivedStatRules, ContentFiles.ProfileBundleDerivedStatRules, Shape.Document),
            new Source(RewardsKeys.Nodes, ContentFiles.ProfileBundleNodes, Shape.Keyed),
            new Source(RegistryKeys.PropertyRules, ContentFiles.ProfileBundlePropertyRules, Shape.List),
            new Source(RegistryKeys.TagDomains, ContentFiles.ProfileBundleTagDomains, Shape.Keyed),
            new Source(RegistryKeys.TagRegistry, ContentFiles.ProfileBundleTags, Shape.Keyed),
            new Source(RegistryKeys.TagFamilies, ContentFiles.ProfileBundleTagFamilies, Shape.Keyed),
            new Source(RegistryKeys.Tagging, ContentFiles.ProfileBundleTagging, Shape.Keyed),
            new Source(At(RegistryKeys.Equipment, RegistryKeys.Armaments), ContentFiles.ProfileBundleArmaments, Shape.Keyed),
            new Source(At(RegistryKeys.Equipment, RegistryKeys.Armour), ContentFiles.ProfileBundleArmour, Shape.Keyed),
            new Source(At(RegistryKeys.Equipment, RegistryKeys.StartingKits), ContentFiles.ProfileBundleStartingKits, Shape.Keyed),
            new Source(At(RegistryKeys.Equipment, RegistryKeys.BasicCardProfiles), ContentFiles.ProfileBundleBasicCardProfiles, Shape.Keyed),
            new Source(At(RegistryKeys.Equipment, RegistryKeys.ItemUpgradeChanges), ContentFiles.ProfileBundleItemUpgradeChanges, Shape.List),
            new Source(At(RegistryKeys.Equipment, RegistryKeys.CardExposure), ContentFiles.ProfileBundleCardExposure, Shape.List),
            new Source(At(RegistryKeys.Equipment, RegistryKeys.CardTagging), ContentFiles.ProfileBundleCardTagging, Shape.List),
            new Source(At(RegistryKeys.Equipment, RegistryKeys.EquipmentRequirements), ContentFiles.ProfileBundleEquipmentRequirements, Shape.List),
            new Source(At(RegistryKeys.Equipment, RegistryKeys.Slots), ContentFiles.ProfileBundleEquipmentSlots, Shape.Document),
        };

        private readonly ContentSet _content;
        private readonly JObject _rowOrder;

        private RegistryBundle(ContentSet content)
        {
            _content = content;
            _rowOrder = content.Get(ContentFiles.RulesRowOrder) as JObject ?? new JObject();
        }

        /// <summary>The bundle object: { cards: [...], equipment: { armaments: [...], ... }, balance: {...}, tagging: [...], ... }.</summary>
        public static JObject Load(ContentSet content)
        {
            var loader = new RegistryBundle(content);
            var bundle = new JObject();
            var read = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in Sources)
            {
                if (!content.Contains(source.File)) continue;
                read.Add(source.File);
                SetPath(bundle, source.Path, loader.Read(source));
            }
            loader.ReadRemaining(bundle, read);
            return bundle;
        }

        /// <summary>
        /// The rest of profile/bundle/ (enemyMoves, resources, sigils, shops, nodeEffects, ...) as top-level bundle keys named
        /// after the file: an id-keyed file listed in rules/rowOrder.json becomes its rows in that order, a <c>{ rows }</c> list
        /// its rows, anything else the document itself.
        /// </summary>
        private void ReadRemaining(JObject bundle, HashSet<string> read)
        {
            foreach (var file in _content.Paths.Where(p => p.StartsWith(Core2Layout.BundleFolder, StringComparison.Ordinal) && !read.Contains(p)))
            {
                var key = file.Substring(Core2Layout.BundleFolder.Length);
                if (!key.EndsWith(ContentLayout.JsonSuffix, StringComparison.Ordinal)) continue;
                key = key.Substring(0, key.Length - ContentLayout.JsonSuffix.Length);
                var doc = _content.Get(file);
                if (doc is JObject obj && _rowOrder[file] is JArray)
                    bundle[key] = Keyed(file, obj);
                else if (doc is JObject list && list.Count == 1 && list[ContentLayout.ListRows] is JArray rows)
                    bundle[key] = rows;
                else bundle[key] = doc;
            }
        }

        private JToken Read(Source source)
        {
            var file = source.File;
            var doc = _content.Get(file);
            switch (source.Shape)
            {
                case Shape.List:
                    return (doc as JObject)?[ContentLayout.ListRows] as JArray ?? new JArray();
                case Shape.Keyed:
                    return Keyed(file, doc as JObject ?? new JObject());
                default:
                    return doc;
            }
        }

        private JArray Keyed(string file, JObject doc) => new JArray(OrderedKeys(file, doc).Select(key => doc[key]));

        private List<string> OrderedKeys(string file, JObject doc) => OrderedKeys(_rowOrder, file, doc);

        /// <summary>Row keys of an id-keyed file in authoring order (rules/rowOrder.json), then unlisted keys in ordinal order.</summary>
        internal static List<string> OrderedKeys(JObject rowOrder, string file, JObject doc)
        {
            var present = new HashSet<string>(doc.Properties().Select(p => p.Name), StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var keys = new List<string>();
            foreach (var key in (rowOrder[file] as JArray ?? new JArray()).Select(t => (string)t))
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
