using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>Named template arguments ({name} placeholders; docs/design/08 §9).</summary>
    public sealed class StringArgs
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        public StringArgs Add(string name, string value)
        {
            _values[name] = value ?? string.Empty;
            return this;
        }

        public StringArgs Add(string name, int value) => Add(name, value.ToString(CultureInfo.InvariantCulture));
        public StringArgs Add(string name, long value) => Add(name, value.ToString(CultureInfo.InvariantCulture));

        public bool TryGet(string name, out string value) => _values.TryGetValue(name, out value);
    }

    /// <summary>
    /// The display strings (strings/en.json, then strings/app.en.json, later tables winning). Templates fill
    /// {name} placeholders from StringArgs; an unknown placeholder is left as written so a missing argument is
    /// visible rather than silent.
    /// </summary>
    public sealed class StringTable
    {
        private readonly Dictionary<string, string> _strings = new Dictionary<string, string>(StringComparer.Ordinal);

        public StringTable(params JObject[] tables)
        {
            foreach (var table in tables)
                if (table != null)
                    foreach (var p in table.Properties())
                        if (p.Value.Type == JTokenType.String) _strings[p.Name] = (string)p.Value;
        }

        public int Count => _strings.Count;

        public bool Has(string key) => key != null && _strings.ContainsKey(key);

        /// <summary>The string for key, or the key itself when it is missing (visible in review, never blank).</summary>
        public string Get(string key) => key != null && _strings.TryGetValue(key, out var s) ? s : key ?? string.Empty;

        public string Format(string key, StringArgs args) => Fill(Get(key), args);

        public static string Fill(string template, StringArgs args)
        {
            if (string.IsNullOrEmpty(template) || args == null) return template ?? string.Empty;
            var open = UiFormats.PlaceholderOpen[0];
            var close = UiFormats.PlaceholderClose[0];
            var sb = new StringBuilder(template.Length);
            var i = 0;
            while (i < template.Length)
            {
                var c = template[i];
                if (c == open)
                {
                    var end = template.IndexOf(close, i + 1);
                    if (end > i && args.TryGet(template.Substring(i + 1, end - i - 1), out var value))
                    {
                        sb.Append(value);
                        i = end + 1;
                        continue;
                    }
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }
}
