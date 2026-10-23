using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.Content
{
    /// <summary>
    /// The resolved Core 2 game profile (WP-2, D-168): <c>profile/core.json</c> (id, hash, foundation, modules, ...)
    /// plus its content keys (<c>profile/content/*.json</c>), as the web's <c>resolveGameProfile('default')</c> returns them.
    /// The hash is shipped verbatim and never recomputed. A content file the web holds as a list of rows (the ones
    /// rules/rowOrder.json orders) is read as that list in registry order; the others are read as shipped.
    /// </summary>
    public sealed class CoreProfile
    {
        private readonly JObject _core;
        private readonly Dictionary<string, JToken> _content;

        private CoreProfile(JObject core, Dictionary<string, JToken> content)
        {
            _core = core;
            _content = content;
        }

        public string Id => JsValues.Str(_core[Core2ProfileKeys.Id]);

        /// <summary>The profile's content hash, <c>sha256:</c> and 64 hex digits, exactly as the web resolved it.</summary>
        public string Hash => JsValues.Str(_core[Core2ProfileKeys.Hash]);

        public JObject Foundation => _core[Core2ProfileKeys.Foundation] is JObject f ? (JObject)f.DeepClone() : new JObject();

        /// <summary>Module id → its settings object (or <c>false</c> when the module is off).</summary>
        public JObject Modules => _core[Core2ProfileKeys.Modules] is JObject m ? (JObject)m.DeepClone() : new JObject();

        /// <summary>The whole <c>profile/core.json</c> document.</summary>
        public JObject Core => (JObject)_core.DeepClone();

        /// <summary>The content keys present, in ordinal order.</summary>
        public IEnumerable<string> ContentKeys => _content.Keys.OrderBy(k => k, StringComparer.Ordinal);

        public bool HasContent(string key) => key != null && _content.ContainsKey(key);

        /// <summary>One content key (<see cref="Core2ProfileContentKeys"/>) as a deep clone: rows in order, an id-keyed object, or a scalar.</summary>
        public JToken Content(string key) => key != null && _content.TryGetValue(key, out var t) ? t.DeepClone() : null;

        /// <summary>The rows of a list-shaped content key (characters, cards, enemyCards, ...), in registry order; empty when it is not a list.</summary>
        public JArray Rows(string key) => Content(key) as JArray ?? new JArray();

        /// <summary>The setting object of a module, or null when the module is off or absent.</summary>
        public JObject Module(string id) => _core[Core2ProfileKeys.Modules]?[id] is JObject m ? (JObject)m.DeepClone() : null;

        /// <summary>Whether a module is on (an object of settings; <c>false</c> or absent is off).</summary>
        public bool ModuleOn(string id) => Module(id) != null;

        /// <summary>Reads the profile from a content set; throws when <c>profile/core.json</c> is absent. Call <see cref="Validate"/> for the findings.</summary>
        public static CoreProfile Load(ContentSet content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (!(content.Get(ContentFiles.ProfileCore) is JObject core))
                throw new InvalidOperationException(Core2ProfileMessages.NoProfile);
            var rowOrder = content.Get(ContentFiles.RulesRowOrder) as JObject ?? new JObject();
            var values = new Dictionary<string, JToken>(StringComparer.Ordinal);
            foreach (var file in content.Paths.Where(p => p.StartsWith(Core2Layout.ContentFolder, StringComparison.Ordinal)
                                                         && p.EndsWith(ContentLayout.JsonSuffix, StringComparison.Ordinal)))
            {
                var key = file.Substring(Core2Layout.ContentFolder.Length, file.Length - Core2Layout.ContentFolder.Length - ContentLayout.JsonSuffix.Length);
                var doc = content.Get(file);
                values[key] = doc is JObject keyed && rowOrder[file] is JArray
                    ? new JArray(RegistryBundle.OrderedKeys(rowOrder, file, keyed).Select(k => keyed[k]))
                    : doc;
            }
            return new CoreProfile(core, values);
        }

        /// <summary>
        /// Findings in the ContentValidator's shape: a missing id, foundation, module or content key, and a hash that is not a
        /// sha256 digest. Every module id of <see cref="Core2ModuleIds"/> and every key of <see cref="Core2ProfileContentKeys"/> is required.
        /// </summary>
        public ValidationReport Validate()
        {
            var report = new ValidationReport { FilesChecked = _content.Count + 1 };
            if (string.IsNullOrEmpty(Id)) report.Add(ContentFiles.ProfileCore, Core2ProfileKeys.Id, Core2ProfileRules.BadId, Core2ProfileMessages.BadId);
            if (!(_core[Core2ProfileKeys.Foundation] is JObject))
                report.Add(ContentFiles.ProfileCore, Core2ProfileKeys.Foundation, Core2ProfileRules.MissingFoundation, Core2ProfileMessages.MissingFoundation);
            if (Hash == null || !Regex.IsMatch(Hash, Core2ProfileValues.HashPattern, RegexOptions.CultureInvariant))
                report.Add(ContentFiles.ProfileCore, Core2ProfileKeys.Hash, Core2ProfileRules.BadHash, string.Format(System.Globalization.CultureInfo.InvariantCulture, Core2ProfileMessages.BadHash, Hash));
            var modules = _core[Core2ProfileKeys.Modules] as JObject;
            foreach (var id in ConstantsOf(typeof(Core2ModuleIds)))
                if (modules == null || modules[id] == null)
                    report.Add(ContentFiles.ProfileCore, Core2ProfileKeys.Modules + ContentLayout.SchemaNameSeparator + id, Core2ProfileRules.MissingModule,
                        string.Format(System.Globalization.CultureInfo.InvariantCulture, Core2ProfileMessages.MissingModule, id));
            foreach (var key in ConstantsOf(typeof(Core2ProfileContentKeys)))
                if (!_content.ContainsKey(key))
                    report.Add(Core2Layout.ContentFolder + key + ContentLayout.JsonSuffix, string.Empty, Core2ProfileRules.MissingContent,
                        string.Format(System.Globalization.CultureInfo.InvariantCulture, Core2ProfileMessages.MissingContent, key));
            return report;
        }

        private static IEnumerable<string> ConstantsOf(Type type) =>
            type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue());
    }
}
