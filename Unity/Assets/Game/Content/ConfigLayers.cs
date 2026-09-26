using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.Content
{
    /// <summary>What to layer on top of the shipped content for one run (docs/design/08 §8).</summary>
    public sealed class LayerSelection
    {
        /// <summary>Preset id (settings/presets/*.json); null selects the preset marked "default".</summary>
        public string PresetId;

        /// <summary>Ordered id-keyed patch documents ({ "files": { path: mergePatch } }): ascension, custom-run modifiers, advanced settings.</summary>
        public IReadOnlyList<JObject> Patches = Array.Empty<JObject>();

        /// <summary>Player settings (UI/behaviour keys), applied over the preset's playerSettings.</summary>
        public JObject PlayerSettings;

        /// <summary>Optional modding overrides (persistentDataPath/Overrides); read only when ModdingEnabled.</summary>
        public IContentSource Overrides;
        public bool ModdingEnabled;
    }

    /// <summary>The frozen result of layering: effective content + player settings + provenance, carried in the save.</summary>
    public sealed class RunSnapshot
    {
        internal RunSnapshot(ContentSet content, string presetId, JObject playerSettings, IReadOnlyList<string> overridesApplied)
        {
            Content = content;
            PresetId = presetId;
            PlayerSettings = (JObject)playerSettings.DeepClone();
            OverridesApplied = overridesApplied;
        }

        public ContentSet Content { get; }
        public string PresetId { get; }
        public JObject PlayerSettings { get; }
        public IReadOnlyList<string> OverridesApplied { get; }
        public string Hash => Content.Hash;
    }

    /// <summary>
    /// Builds the effective configuration: base content → preset → ordered patches → modding overrides, each an
    /// RFC 7386 merge-patch over id-keyed documents, then validates every changed file against its schema.
    /// </summary>
    public sealed class ConfigLayers
    {
        private readonly IContentSource _source;
        private readonly ContentManifest _manifest;

        public ConfigLayers(IContentSource source)
        {
            _source = source;
            _manifest = ContentManifest.Load(source);
        }

        public IReadOnlyList<string> PresetIds => PresetFiles().Select(p => (string)Load(p)[RuleKeys.PresetId]).ToList();

        public RunSnapshot Build(LayerSelection selection, ValidationReport report = null)
        {
            selection = selection ?? new LayerSelection();
            var files = new Dictionary<string, JToken>(StringComparer.Ordinal);
            foreach (var entry in _manifest.Files)
                if (!ContentManifest.IsSchema(entry.Path)) files[entry.Path] = JsonContent.Parse(_source.ReadText(entry.Path));

            var preset = ResolvePreset(selection.PresetId);
            var changed = new HashSet<string>(StringComparer.Ordinal);
            ApplyDocument(files, preset, changed);
            foreach (var patch in selection.Patches) ApplyDocument(files, patch, changed);

            var overridesApplied = new List<string>();
            if (selection.ModdingEnabled && selection.Overrides != null) ApplyOverrides(files, selection.Overrides, changed, overridesApplied);

            var settings = (preset[RuleKeys.PlayerSettings] as JObject)?.DeepClone() as JObject ?? new JObject();
            if (selection.PlayerSettings != null) settings = (JObject)MergePatch.Apply(settings, selection.PlayerSettings);

            if (report != null) ValidateChanged(files, changed, report);
            return new RunSnapshot(new ContentSet(files), (string)preset[RuleKeys.PresetId], settings, overridesApplied);
        }

        private IEnumerable<string> PresetFiles() => _manifest.Files.Select(e => e.Path).Where(ContentManifest.IsPreset);

        private JObject Load(string path) => (JObject)JsonContent.Parse(_source.ReadText(path));

        private JObject ResolvePreset(string id)
        {
            foreach (var path in PresetFiles())
            {
                var doc = Load(path);
                if (id == null ? (bool?)doc[RuleKeys.PresetDefault] == true : (string)doc[RuleKeys.PresetId] == id) return doc;
            }
            throw new ArgumentException(id == null ? ConfigMessages.NoDefaultPreset : Format(ConfigMessages.UnknownPreset, id));
        }

        private static void ApplyDocument(Dictionary<string, JToken> files, JObject doc, ISet<string> changed)
        {
            if (!(doc?[RuleKeys.PresetFiles] is JObject patches)) return;
            foreach (var p in patches.Properties())
            {
                if (!files.ContainsKey(p.Name)) throw new ArgumentException(Format(ConfigMessages.UnknownFile, p.Name));
                files[p.Name] = MergePatch.Apply(files[p.Name], p.Value);
                changed.Add(p.Name);
            }
        }

        /// <summary>Modding layer (docs/design/08 §2): only manifest paths, size-capped, depth-capped; everything else is refused.</summary>
        private void ApplyOverrides(Dictionary<string, JToken> files, IContentSource overrides, ISet<string> changed, List<string> applied)
        {
            foreach (var entry in _manifest.Files)
            {
                if (ContentManifest.IsSchema(entry.Path) || !overrides.Exists(entry.Path)) continue;
                var text = overrides.ReadText(entry.Path);
                var bytes = Encoding.UTF8.GetByteCount(text);
                if (bytes > ConfigLimits.MaxOverrideBytes) throw new ArgumentException(Format(ConfigMessages.OverrideSize, entry.Path, bytes, ConfigLimits.MaxOverrideBytes));
                var patch = JsonContent.Parse(text);
                if (Depth(patch) > ConfigLimits.MaxJsonDepth) throw new ArgumentException(Format(ConfigMessages.OverridePath, entry.Path));
                files[entry.Path] = MergePatch.Apply(files[entry.Path], patch);
                changed.Add(entry.Path);
                applied.Add(entry.Path);
            }
        }

        private void ValidateChanged(Dictionary<string, JToken> files, IEnumerable<string> changed, ValidationReport report)
        {
            var validator = new SchemaValidator(report, table => files.TryGetValue(table, out var t) && t is JObject o
                ? new HashSet<string>(o.Properties().Select(p => p.Name), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal));
            foreach (var path in changed.OrderBy(p => p, StringComparer.Ordinal))
            {
                var schemaPath = ContentManifest.SchemaFor(path);
                if (_source.Exists(schemaPath)) validator.Validate(path, files[path], Load(schemaPath));
            }
        }

        private static int Depth(JToken t) => t is JContainer c && c.HasValues ? 1 + c.Children().Max(Depth) : 1;

        private static string Format(string template, params object[] args) => string.Format(CultureInfo.InvariantCulture, template, args);
    }
}
