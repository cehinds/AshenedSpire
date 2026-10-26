using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>One lazily loaded art catalog (ui/art.json groups): the ids it takes and the Resources path of its UiArtCatalog.</summary>
    public sealed class ArtGroup
    {
        public string Id;
        public string Resource;
        public IReadOnlyList<string> Include = Array.Empty<string>();

        /// <summary>The same include rule as ui/components.json art.include (a plain entry is a prefix, one '*' matches between its halves).</summary>
        public bool Includes(string assetId) => assetId != null && Include.Any(entry => ComponentDefaults.ArtMatches(entry, assetId));
    }

    /// <summary>The art ids a card resolved to and how: its own painting, an equipment-profile painting, the library painting for its maneuver, or the category placeholder.</summary>
    public enum CardArtKind
    {
        None = 0,
        Painting,
        Profile,
        Library,
        Placeholder,
    }

    public struct CardArtResult
    {
        public string AssetId;
        public CardArtKind Kind;
    }

    /// <summary>
    /// ui/art.json (VP-2): the art groups, the lookup templates, the icon aliases and the Core 2 card art rule. Engine-free and
    /// shared with the tests; Presentation turns the ids it returns into textures through the UI art catalogs.
    /// </summary>
    public sealed class ArtConfig
    {
        public IReadOnlyList<ArtGroup> Groups = Array.Empty<ArtGroup>();
        public IReadOnlyDictionary<string, string> Lookup = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, string> IconAlias = new Dictionary<string, string>();
        public JObject Card = new JObject();

        public static ArtConfig From(JObject json)
        {
            return new ArtConfig
            {
                Groups = (json[ArtKeys.Groups] as JArray ?? new JArray()).OfType<JObject>().Select(g => new ArtGroup
                {
                    Id = (string)g[ArtKeys.Id],
                    Resource = (string)g[ArtKeys.Resource],
                    Include = ScreenRegistry.Strings(g[ArtKeys.Include]),
                }).ToList(),
                Lookup = Strings(json[ArtKeys.Lookup]),
                IconAlias = Strings(json[ArtKeys.IconAlias]),
                Card = json[ArtKeys.Card] as JObject ?? new JObject(),
            };
        }

        /// <summary>The group whose catalog holds an id, or null when it is in the always-loaded catalog (or in none).</summary>
        public ArtGroup GroupOf(string assetId) => Groups.FirstOrDefault(g => g.Includes(assetId));

        internal static Dictionary<string, string> Strings(JToken token) =>
            (token as JObject ?? new JObject()).Properties().ToDictionary(p => p.Name, p => (string)p.Value, StringComparer.Ordinal);

        internal static Dictionary<string, int> Ints(JToken token) =>
            (token as JObject ?? new JObject()).Properties().ToDictionary(p => p.Name, p => (int)p.Value, StringComparer.Ordinal);
    }

    /// <summary>The ids in assets/registry.json that are imported into Unity (they have a unityPath): what an art lookup may return.</summary>
    public sealed class ArtRegistry
    {
        private readonly HashSet<string> _ids;

        public ArtRegistry(IEnumerable<string> ids) => _ids = new HashSet<string>(ids, StringComparer.Ordinal);

        public static ArtRegistry From(JObject registry) =>
            new ArtRegistry(registry.Properties().Where(p => p.Value[ArtRegistryKeys.UnityPath]?.Type == JTokenType.String).Select(p => p.Name));

        public bool Has(string assetId) => assetId != null && _ids.Contains(assetId);

        public IEnumerable<string> Ids => _ids;
    }

    /// <summary>
    /// Finds art by name: a card's painting by card id and tags, an icon by name, an event or scene by id (ui/art.json). Every
    /// method returns a registry id that is imported, or null; the caller turns it into a texture and has its own fallback.
    /// </summary>
    public sealed class ArtLookup
    {
        private readonly ArtConfig _config;
        private readonly ArtRegistry _registry;

        public ArtLookup(ArtConfig config, ArtRegistry registry)
        {
            _config = config;
            _registry = registry;
        }

        public ArtConfig Config => _config;

        public bool Has(string assetId) => _registry.Has(assetId);

        /// <summary>The registry id of a named kind of art (ArtKinds), filled from the template, or null when it is not imported.</summary>
        public string Find(string kind, StringArgs args)
        {
            if (!_config.Lookup.TryGetValue(kind, out var template)) return null;
            var id = StringTable.Fill(template, args);
            return _registry.Has(id) ? id : null;
        }

        /// <summary>The icon of a content icon name (white glyph; USS tints it): its own art, else its alias, else null.</summary>
        public string Icon(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var own = Find(ArtKinds.Icon, new StringArgs().Add(ArtPlaceholders.Name, name));
            if (own != null) return own;
            return _config.IconAlias.TryGetValue(name, out var alias) ? Find(ArtKinds.Icon, new StringArgs().Add(ArtPlaceholders.Name, alias)) : null;
        }

        public string Kit(string name) => Find(ArtKinds.Kit, new StringArgs().Add(ArtPlaceholders.Name, name));
        public string Frame(string name) => Find(ArtKinds.Frame, new StringArgs().Add(ArtPlaceholders.Name, name));
        public string Event(string id) => Find(ArtKinds.Event, new StringArgs().Add(ArtPlaceholders.Id, id));
        public string Scene(string name) => Find(ArtKinds.Scene, new StringArgs().Add(ArtPlaceholders.Name, name));
        public string Portrait(string id) => Find(ArtKinds.Portrait, new StringArgs().Add(ArtPlaceholders.Id, id));

        /// <summary>
        /// The art of a card (the web's simpleCardArt port, ui/art.json card): its own painting by id, then the equipment profile's, then
        /// the library painting for its camp, maneuver and variant, else the stated placeholder of its category. `type` is the card type
        /// (attack, skill, power); `tags` are the card's tag ids.
        /// </summary>
        public CardArtResult Card(string cardId, string type, IEnumerable<string> tags, string profileId = null)
        {
            var rules = _config.Card;
            var own = Existing(rules.Value<string>(ArtKeys.Painting), ArtPlaceholders.Id, cardId);
            if (own != null) return new CardArtResult { AssetId = own, Kind = CardArtKind.Painting };
            var profile = Existing(rules.Value<string>(ArtKeys.Profile), ArtPlaceholders.Id, profileId);
            if (profile != null) return new CardArtResult { AssetId = profile, Kind = CardArtKind.Profile };

            var tagList = (tags ?? Array.Empty<string>()).ToList();
            var prefixes = ArtConfig.Strings(rules[ArtKeys.Tags]);
            string TagValue(string key)
            {
                var values = tagList.Where(t => t.StartsWith(prefixes[key], StringComparison.Ordinal)).Select(t => t.Substring(prefixes[key].Length)).Distinct().ToList();
                return values.Count == 1 ? values[0] : null;
            }

            var categories = ArtConfig.Strings(rules[ArtKeys.CampCategory]);
            var camp = TagValue(ArtKeys.Camp);
            var category = camp != null && categories.TryGetValue(camp, out var c) ? c : null;
            var maneuver = Maneuver(rules, TagValue(ArtKeys.Maneuver), type, tagList.Any(t => t.StartsWith(prefixes[ArtKeys.Maneuver], StringComparison.Ordinal)));
            var library = (rules[ArtKeys.LibraryManeuvers] as JArray ?? new JArray()).Select(t => (string)t).ToList();
            if (category != null && maneuver != null && library.Contains(maneuver))
            {
                var variant = Variant(rules, category, cardId, profileId, TagValue(ArtKeys.Element) ?? TagValue(ArtKeys.School));
                var id = StringTable.Fill(rules.Value<string>(ArtKeys.Library), new StringArgs()
                    .Add(ArtPlaceholders.Category, category).Add(ArtPlaceholders.Maneuver, maneuver)
                    .Add(ArtPlaceholders.Variant, variant.ToString(rules.Value<string>(ArtKeys.VariantFormat), CultureInfo.InvariantCulture)));
                if (_registry.Has(id)) return new CardArtResult { AssetId = id, Kind = CardArtKind.Library };
            }
            var placeholders = ArtConfig.Strings(rules[ArtKeys.Placeholder]);
            var holder = category != null && placeholders.TryGetValue(category, out var h) ? h : placeholders.GetValueOrDefault(rules.Value<string>(ArtKeys.DefaultCategory));
            return _registry.Has(holder) ? new CardArtResult { AssetId = holder, Kind = CardArtKind.Placeholder } : default;
        }

        private string Existing(string template, string placeholder, string value)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(template)) return null;
            var id = StringTable.Fill(template, new StringArgs().Add(placeholder, value));
            return _registry.Has(id) ? id : null;
        }

        /// <summary>The library maneuver of a card: its maneuver tag (casting and ranged read as attack on an attack card), an attack with no tag is an attack, a casting skill or power has none (it takes the placeholder).</summary>
        internal static string Maneuver(JObject rules, string tagged, string type, bool hasManeuverTag)
        {
            var attack = rules.Value<string>(ArtKeys.AttackType);
            if (tagged == null) return !hasManeuverTag && type == attack ? attack : null;
            var aliases = ArtConfig.Strings(rules[ArtKeys.ManeuverAlias]);
            if (!aliases.TryGetValue(tagged, out var alias)) return tagged;
            return type == attack ? alias : rules.Value<string>(ArtKeys.SkillManeuver);
        }

        /// <summary>The library variant 1..variantCount: the element's variant for a magical card, a hash of the card id (and profile) for a physical one.</summary>
        private static int Variant(JObject rules, string category, string cardId, string profileId, string elementTag)
        {
            var count = rules.Value<int>(ArtKeys.VariantCount);
            if (category == rules.Value<string>(ArtKeys.MagicalCategory))
            {
                var variants = ArtConfig.Ints(rules[ArtKeys.ElementVariant]);
                var aliases = ArtConfig.Strings(rules[ArtKeys.ElementAlias]);
                var element = elementTag != null && aliases.TryGetValue(elementTag, out var a) ? a : elementTag;
                if (element == null || !variants.ContainsKey(element)) element = rules.Value<string>(ArtKeys.DefaultElement);
                return variants[element];
            }
            var hash = (JObject)rules[ArtKeys.Hash];
            var h = unchecked((uint)hash.Value<long>(ArtKeys.Offset));
            var prime = unchecked((uint)hash.Value<long>(ArtKeys.Prime));
            var key = (cardId ?? string.Empty) + hash.Value<string>(ArtKeys.Separator) + (profileId ?? string.Empty);
            unchecked { foreach (var ch in key) h = (h ^ (uint)ch) * prime; }
            return (int)(h % (uint)count) + 1;
        }
    }
}
