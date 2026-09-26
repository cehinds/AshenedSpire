using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.Content
{
    /// <summary>StreamingAssets/Content/manifest.json: the only list of content files (docs/design/08 §2).</summary>
    public sealed class ContentManifest
    {
        public sealed class Entry
        {
            public string Path;
            public string Sha256;
            public bool Generated;
        }

        private ContentManifest() { }

        public int SchemaVersion { get; private set; }
        public string ContentVersion { get; private set; }
        public string ContentHash { get; private set; }
        public IReadOnlyDictionary<string, int> Counts { get; private set; }
        public IReadOnlyList<Entry> Files { get; private set; }

        public static ContentManifest Load(IContentSource source)
        {
            var root = (JObject)JsonContent.Parse(source.ReadText(ContentLayout.Manifest));
            var files = ((JObject)root[ManifestKeys.Files]).Properties()
                .Select(p => new Entry
                {
                    Path = p.Name,
                    Sha256 = (string)p.Value[ManifestKeys.Sha256],
                    Generated = (bool?)p.Value[ManifestKeys.Generated] ?? false,
                })
                .OrderBy(e => e.Path, System.StringComparer.Ordinal)
                .ToList();
            var counts = new Dictionary<string, int>(System.StringComparer.Ordinal);
            if (root[ManifestKeys.Counts] is JObject c)
                foreach (var p in c.Properties()) counts[p.Name] = (int)p.Value;
            return new ContentManifest
            {
                SchemaVersion = (int)root[ManifestKeys.SchemaVersion],
                ContentVersion = (string)root[ManifestKeys.ContentVersion],
                ContentHash = (string)root[ManifestKeys.ContentHash],
                Counts = counts,
                Files = files,
            };
        }

        /// <summary>Schema path for a data file: "catalog/cards.json" → "schemas/catalog.cards.schema.json".</summary>
        public static string SchemaFor(string file)
        {
            var name = file.Substring(0, file.Length - ContentLayout.JsonSuffix.Length)
                .Replace(ContentLayout.PathSeparator, ContentLayout.SchemaNameSeparator);
            return ContentLayout.SchemaFolder + name + ContentLayout.SchemaSuffix;
        }

        public static bool IsSchema(string file) => file.StartsWith(ContentLayout.SchemaFolder, System.StringComparison.Ordinal);
        public static bool IsPreset(string file) => file.StartsWith(RuleKeys.PresetFolder, System.StringComparison.Ordinal);
    }
}
