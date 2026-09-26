using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.Content
{
    /// <summary>
    /// Validates the whole content set before play (US-0.4; docs/design/08 §12 ContentValid):
    /// manifest integrity, schema per data file, cross-references, effect-op vocabulary, required string keys,
    /// data-driven row rules (rules/validation.json), and every preset applied onto its base file.
    /// </summary>
    public sealed class ContentValidator
    {
        private readonly IContentSource _source;
        private readonly Dictionary<string, JToken> _cache = new Dictionary<string, JToken>(StringComparer.Ordinal);
        private readonly Dictionary<string, ISet<string>> _keys = new Dictionary<string, ISet<string>>(StringComparer.Ordinal);
        private ValidationReport _report;

        public ContentValidator(IContentSource source) => _source = source;

        public ValidationReport Validate()
        {
            _report = new ValidationReport();
            var manifest = ContentManifest.Load(_source);
            foreach (var entry in manifest.Files) CheckFile(entry);
            var dataFiles = manifest.Files.Select(e => e.Path).Where(IsData).ToList();
            var schemaValidator = new SchemaValidator(_report, KeysOf);
            foreach (var file in dataFiles)
            {
                var data = Load(file);
                var schemaPath = ContentManifest.SchemaFor(file);
                if (data == null) continue;
                if (!_source.Exists(schemaPath)) { Report(file, string.Empty, ValidationRules.Schema, ValidationMessages.NoSchema, schemaPath); continue; }
                schemaValidator.Validate(file, data, Load(schemaPath) as JObject);
                CheckOps(file, string.Empty, data);
            }
            CheckStrings();
            CheckRowRules();
            CheckPresets(manifest, schemaValidator);
            _report.FilesChecked = manifest.Files.Count;
            return _report;
        }

        private static bool IsData(string file) =>
            !ContentManifest.IsSchema(file) && !ContentManifest.IsPreset(file) && file != RuleKeys.StringsFile;

        private void CheckFile(ContentManifest.Entry entry)
        {
            if (!_source.Exists(entry.Path)) { Report(entry.Path, string.Empty, ValidationRules.MissingFile, ValidationMessages.MissingFile, entry.Path); return; }
            var text = _source.ReadText(entry.Path);
            var hash = Sha256(text);
            if (!string.Equals(hash, entry.Sha256, StringComparison.Ordinal)) Report(entry.Path, string.Empty, ValidationRules.Hash, ValidationMessages.Hash, entry.Sha256, hash);
        }

        private JToken Load(string file)
        {
            if (_cache.TryGetValue(file, out var cached)) return cached;
            JToken token = null;
            try { token = JsonContent.Parse(_source.ReadText(file)); }
            catch (Exception e) { Report(file, string.Empty, ValidationRules.Parse, ValidationMessages.Parse, e.Message); }
            _cache[file] = token;
            return token;
        }

        private ISet<string> KeysOf(string table)
        {
            if (_keys.TryGetValue(table, out var set)) return set;
            set = new HashSet<string>(StringComparer.Ordinal);
            if (_source.Exists(table) && Load(table) is JObject obj) foreach (var p in obj.Properties()) set.Add(p.Name);
            return _keys[table] = set;
        }

        private ISet<string> _containers;

        /// <summary>Effect containers come from rules/effectOps.json ("containers"): only objects inside those arrays are effects.</summary>
        private ISet<string> Containers()
        {
            if (_containers != null) return _containers;
            _containers = new HashSet<string>(StringComparer.Ordinal);
            if (Load(RuleKeys.EffectOpsFile) is JObject ops && ops[RuleKeys.Containers] is JArray list)
                foreach (var c in list) _containers.Add((string)c);
            return _containers;
        }

        private void CheckOps(string file, string path, JToken token, bool inContainer = false)
        {
            if (token is JObject obj)
            {
                if (inContainer && obj[RuleKeys.Op] is JValue op && op.Type == JTokenType.String && EffectOpNames.Parse(op.Value<string>()) == EffectOp.None)
                    Report(file, path, ValidationRules.UnknownOp, ValidationMessages.UnknownOp, op.Value<string>());
                foreach (var p in obj.Properties())
                    CheckOps(file, path.Length == 0 ? p.Name : path + ContentLayout.SchemaNameSeparator + p.Name, p.Value,
                        p.Value.Type == JTokenType.Array && Containers().Contains(p.Name));
            }
            else if (token is JArray arr)
                for (var i = 0; i < arr.Count; i++) CheckOps(file, path + ContentLayout.SchemaNameSeparator + i.ToString(CultureInfo.InvariantCulture), arr[i], inContainer);
        }

        private void CheckStrings()
        {
            if (!(Load(RuleKeys.StringsFile) is JObject strings) || !(Load(RuleKeys.StringKeysFile) is JObject spec)) return;
            foreach (var table in spec.Properties())
            {
                var prefix = (string)table.Value[RuleKeys.Prefix];
                var required = (table.Value[RuleKeys.Required] as JArray)?.Select(t => (string)t).ToList() ?? new List<string>();
                foreach (var key in KeysOf(table.Name).OrderBy(k => k, StringComparer.Ordinal))
                    foreach (var name in required)
                    {
                        var stringKey = prefix + ContentLayout.SchemaNameSeparator + key + ContentLayout.SchemaNameSeparator + name;
                        if (strings[stringKey] == null) Report(RuleKeys.StringsFile, stringKey, ValidationRules.String, ValidationMessages.String, stringKey);
                    }
            }
        }

        private void CheckRowRules()
        {
            if (!(Load(RuleKeys.ValidationFile) is JObject rules) || !(rules[RuleKeys.RowRules] is JArray rowRules)) return;
            foreach (var rule in rowRules.OfType<JObject>())
            {
                var id = (string)rule[RuleKeys.RuleId];
                var table = (string)rule[RuleKeys.Table];
                if (!(Load(table) is JObject rows)) continue;
                foreach (var row in rows.Properties())
                {
                    if (!(row.Value is JObject r) || !Holds(r, rule[RuleKeys.When] as JObject)) continue;
                    foreach (var clause in (rule[RuleKeys.Require] as JArray ?? new JArray()).OfType<JObject>())
                        if (!Holds(r, clause))
                        {
                            var field = (string)clause[RuleKeys.Field];
                            Report(table, row.Name + ContentLayout.SchemaNameSeparator + field, ValidationRules.Cost, ValidationMessages.Cost, id, field, JsonContent.Describe(r[field]));
                        }
                }
            }
        }

        /// <summary>A clause holds when the field satisfies its comparison, or equals one of orValues. Missing numeric fields count as 0.</summary>
        internal static bool Holds(JObject row, JObject clause)
        {
            if (clause == null) return true;
            var value = row[(string)clause[RuleKeys.Field]];
            if (clause[RuleKeys.OrValues] is JArray alts && value != null && alts.Any(a => JToken.DeepEquals(a, value))) return true;
            if (clause[RuleKeys.Eq] != null) return value != null && JToken.DeepEquals(value, clause[RuleKeys.Eq]);
            var n = JsonContent.IsNumber(value) ? JsonContent.ToDecimal(value) : (value == null || value.Type == JTokenType.Null ? 0m : (decimal?)null);
            if (n == null) return false;
            if (clause[RuleKeys.Gt] != null && !(n > JsonContent.ToDecimal(clause[RuleKeys.Gt]))) return false;
            if (clause[RuleKeys.Gte] != null && !(n >= JsonContent.ToDecimal(clause[RuleKeys.Gte]))) return false;
            if (clause[RuleKeys.Lt] != null && !(n < JsonContent.ToDecimal(clause[RuleKeys.Lt]))) return false;
            if (clause[RuleKeys.Lte] != null && !(n <= JsonContent.ToDecimal(clause[RuleKeys.Lte]))) return false;
            return true;
        }

        private void CheckPresets(ContentManifest manifest, SchemaValidator schemaValidator)
        {
            foreach (var preset in manifest.Files.Select(e => e.Path).Where(ContentManifest.IsPreset))
            {
                if (!(Load(preset) is JObject p) || !(p[RuleKeys.PresetFiles] is JObject files)) continue;
                foreach (var patch in files.Properties())
                {
                    if (!_source.Exists(patch.Name)) { Report(preset, patch.Name, ValidationRules.MissingFile, ValidationMessages.PresetFile, patch.Name); continue; }
                    var merged = MergePatch.Apply(Load(patch.Name), patch.Value);
                    var schemaPath = ContentManifest.SchemaFor(patch.Name);
                    if (_source.Exists(schemaPath)) schemaValidator.Validate(preset + ContentLayout.KeyJoiner + patch.Name, merged, Load(schemaPath) as JObject);
                }
            }
        }

        private static string Sha256(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString(Ashen.Generated.ContentLayout.HexByteFormat, CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private void Report(string file, string path, string rule, string template, params object[] args)
            => _report.Add(file, path, rule, string.Format(CultureInfo.InvariantCulture, template, args));
    }
}
