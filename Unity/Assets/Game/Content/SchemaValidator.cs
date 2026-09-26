using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.Content
{
    /// <summary>
    /// Walks a JSON value against the content JSON-Schema subset (docs/design/08 §4): type (single or union),
    /// properties, required, additionalProperties, items, enum, x-keyed, x-ref. References are resolved by the
    /// caller-supplied key lookup so the walker stays independent of file loading.
    /// </summary>
    public sealed class SchemaValidator
    {
        private readonly Func<string, ISet<string>> _keysOf;
        private readonly ValidationReport _report;

        public SchemaValidator(ValidationReport report, Func<string, ISet<string>> keysOf)
        {
            _report = report;
            _keysOf = keysOf;
        }

        public void Validate(string file, JToken value, JObject schema) => Walk(file, string.Empty, value, schema);

        private void Walk(string file, string path, JToken value, JObject schema)
        {
            if (schema == null) return;
            if (!TypeMatches(value, schema[SchemaKeys.Type]))
            {
                Report(file, path, ValidationRules.Type, ValidationMessages.Type, TypeText(schema[SchemaKeys.Type]), ActualType(value));
                return;
            }
            if (value.Type == JTokenType.String && schema[SchemaKeys.Enum] is JArray allowed)
            {
                var s = value.Value<string>();
                if (!allowed.Any(a => a.Type == JTokenType.String && a.Value<string>() == s))
                    Report(file, path, ValidationRules.Enum, ValidationMessages.Enum, s, string.Join(ContentLayout.KeyJoiner, allowed.Select(a => a.ToString())));
            }
            if (schema[SchemaKeys.Ref] is JValue refTable && value.Type == JTokenType.String)
            {
                var id = value.Value<string>();
                var table = refTable.Value<string>();
                if (id.Length > 0 && !_keysOf(table).Contains(id))
                    Report(file, path, ValidationRules.Ref, ValidationMessages.Ref, id, table);
            }
            if (value is JObject obj) WalkObject(file, path, obj, schema);
            else if (value is JArray arr && schema[SchemaKeys.Items] is JObject items)
            {
                for (var i = 0; i < arr.Count; i++) Walk(file, path + ContentLayout.SchemaNameSeparator + i.ToString(CultureInfo.InvariantCulture), arr[i], items);
            }
        }

        private void WalkObject(string file, string path, JObject obj, JObject schema)
        {
            var props = schema[SchemaKeys.Properties] as JObject;
            var additional = schema[SchemaKeys.AdditionalProperties] as JObject;
            if (schema[SchemaKeys.Required] is JArray required)
                foreach (var r in required)
                {
                    var name = r.Value<string>();
                    if (obj[name] == null) Report(file, path, ValidationRules.Required, ValidationMessages.Required, name);
                }
            foreach (var p in obj.Properties())
            {
                var childPath = path.Length == 0 ? p.Name : path + ContentLayout.SchemaNameSeparator + p.Name;
                if (props != null && props[p.Name] is JObject propSchema) Walk(file, childPath, p.Value, propSchema);
                else if (additional != null) Walk(file, childPath, p.Value, additional);
                else if (props != null) Report(file, childPath, ValidationRules.Unknown, ValidationMessages.Unknown, p.Name);
            }
        }

        private static bool TypeMatches(JToken value, JToken type)
        {
            if (type == null) return true;
            if (type is JArray union) return union.Any(t => SingleTypeMatches(value, t.Value<string>()));
            return SingleTypeMatches(value, type.Value<string>());
        }

        private static bool SingleTypeMatches(JToken v, string type)
        {
            switch (type)
            {
                case SchemaTypes.Object: return v.Type == JTokenType.Object;
                case SchemaTypes.Array: return v.Type == JTokenType.Array;
                case SchemaTypes.String: return v.Type == JTokenType.String;
                case SchemaTypes.Integer: return v.Type == JTokenType.Integer || (v.Type == JTokenType.Float && decimal.Truncate(v.Value<decimal>()) == v.Value<decimal>());
                case SchemaTypes.Number: return v.Type == JTokenType.Integer || v.Type == JTokenType.Float;
                case SchemaTypes.Boolean: return v.Type == JTokenType.Boolean;
                case SchemaTypes.Null: return v.Type == JTokenType.Null;
                default: return false;
            }
        }

        private static string TypeText(JToken type) => type is JArray a ? string.Join(ContentLayout.KeyJoiner, a.Select(t => t.Value<string>())) : type?.Value<string>();

        private static string ActualType(JToken v)
        {
            switch (v.Type)
            {
                case JTokenType.Object: return SchemaTypes.Object;
                case JTokenType.Array: return SchemaTypes.Array;
                case JTokenType.String: return SchemaTypes.String;
                case JTokenType.Integer: return SchemaTypes.Integer;
                case JTokenType.Float: return SchemaTypes.Number;
                case JTokenType.Boolean: return SchemaTypes.Boolean;
                default: return SchemaTypes.Null;
            }
        }

        private void Report(string file, string path, string rule, string template, params object[] args)
            => _report.Add(file, path, rule, string.Format(CultureInfo.InvariantCulture, template, args));
    }
}
