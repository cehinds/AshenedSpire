using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using P = Ashen.Generated.MetaPlaceholders;
using S = Ashen.Generated.MetaStringKeys;
using SK = Ashen.Generated.SettingsKeys;
using SV = Ashen.Generated.SettingsValues;

namespace Ashen.App.Settings
{
    /// <summary>One control of settings/defaults.json, with its content references resolved against the run content.</summary>
    public sealed class SettingDef
    {
        public string Key;
        public string Category;
        public string Type;
        public string Scope;
        public IReadOnlyList<string> Options = Array.Empty<string>();
        public double Min;
        public double Max;
        public double Step;
        public JToken Def;
        public string LabelKey;
        public string HelpKey;
        public string OptionKey;
        public string UnitKey;

        public bool IsToggle => Type == SV.Toggle;
        public bool IsEnum => Type == SV.Enum;
        public bool IsSlider => Type == SV.Slider;
        public bool AppliesNextRun => Scope == SV.Run;

        /// <summary>Whether a value fits this control (a boolean; one of the options; a number in range on the step grid).</summary>
        public string Refusal(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return S.SettingsRefusalType;
            if (IsToggle) return value.Type == JTokenType.Boolean ? null : S.SettingsRefusalType;
            if (IsEnum)
            {
                if (value.Type != JTokenType.String) return S.SettingsRefusalType;
                return Options.Contains((string)value, StringComparer.Ordinal) ? null : S.SettingsRefusalOption;
            }
            if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float) return S.SettingsRefusalType;
            var n = Js.D(value);
            if (double.IsNaN(n) || n < Min || n > Max) return S.SettingsRefusalRange;
            if (Step > 0)
            {
                var steps = (n - Min) / Step;
                if (steps != Math.Floor(steps) && n != Max) return S.SettingsRefusalRange;
            }
            return null;
        }
    }

    /// <summary>settings/defaults.json, parsed: the rail categories in order and the controls.</summary>
    public sealed class SettingsDefs
    {
        public readonly List<KeyValuePair<string, string>> Categories = new List<KeyValuePair<string, string>>();
        public readonly List<SettingDef> Controls = new List<SettingDef>();

        public SettingDef Find(string key) => Controls.FirstOrDefault(c => c.Key == key);

        public static SettingsDefs From(ContentSet content)
        {
            var doc = (JObject)content.Get(ContentFiles.SettingsDefaults) ?? new JObject();
            var defs = new SettingsDefs();
            foreach (var c in Js.Items(doc[SK.Categories]).OfType<JObject>())
                defs.Categories.Add(new KeyValuePair<string, string>(c.Str(SK.Id), c.Str(SK.LabelKey)));
            foreach (var c in Js.Items(doc[SK.Controls]).OfType<JObject>())
            {
                defs.Controls.Add(new SettingDef
                {
                    Key = c.Str(SK.Key),
                    Category = c.Str(SK.Category),
                    Type = c.Str(SK.Type),
                    Scope = c.Str(SK.Scope),
                    Options = OptionsOf(content, Resolve(content, c[SK.Options])),
                    Min = Js.Or0(Resolve(content, c[SK.Min])),
                    Max = Js.Or0(Resolve(content, c[SK.Max])),
                    Step = Js.Or0(c[SK.Step]),
                    Def = Resolve(content, c[SK.Def])?.DeepClone(),
                    LabelKey = c.Str(SK.LabelKey),
                    HelpKey = c.Str(SK.HelpKey),
                    OptionKey = c.Str(SK.OptionKey),
                    UnitKey = c.Str(SK.UnitKey),
                });
            }
            return defs;
        }

        /// <summary>A value, or a '&lt;file&gt;#&lt;dotted.path&gt;' reference into the content (a missing file or path is null).</summary>
        internal static JToken Resolve(ContentSet content, JToken token)
        {
            if (token == null || token.Type != JTokenType.String) return token;
            var text = (string)token;
            var at = text.IndexOf(SV.RefSeparator, StringComparison.Ordinal);
            if (at < 0) return token;
            var file = text.Substring(0, at);
            var path = text.Substring(at + SV.RefSeparator.Length);
            return content.Contains(file) ? content.Select(file, path) : null;
        }

        private static IReadOnlyList<string> OptionsOf(ContentSet content, JToken options)
        {
            if (options is JArray list) return list.Select(Js.Str).Where(s => s != null).ToList();
            if (options is JObject map) return map.Properties().Select(p => p.Name).ToList();
            return Array.Empty<string>();
        }
    }

    /// <summary>One W-18 control as the screen draws it.</summary>
    public sealed class SettingControlView
    {
        public string Key;
        public string Type;
        public string Label;
        public string Help;
        public JToken Value;
        public string ValueText;
        public readonly List<KeyValuePair<string, string>> Options = new List<KeyValuePair<string, string>>();
        public double Min;
        public double Max;
        public double Step;
        public bool IsDefault;

        /// <summary>The control is run-scoped and the screen was opened in a run: it shows settings.appliesNextRun.</summary>
        public string AppliesNextRun;
    }

    /// <summary>W-18 <c>screen:settings</c>: the rail and the open category's generated controls.</summary>
    public sealed class SettingsViewState
    {
        public string Title;
        public string Category;
        public readonly List<KeyValuePair<string, string>> Rail = new List<KeyValuePair<string, string>>();
        public readonly List<SettingControlView> Controls = new List<SettingControlView>();
        public string Reset;
    }

    /// <summary>
    /// W-18 over the profile (US-15.1): a value is the profile's <c>settings.&lt;key&gt;</c>, else the preset's
    /// playerSettings, else the control's def; a stored value that no longer fits the control is ignored. Each change is
    /// validated against its control and saved to the profile at once. Engine-free.
    /// </summary>
    public sealed class SettingsSession
    {
        private readonly RunContent _content;
        private readonly ProfileStore _profile;

        public SettingsSession(RunContent content, ProfileStore profile, bool inRun = false)
        {
            _content = content;
            _profile = profile;
            InRun = inRun;
            Defs = SettingsDefs.From(content.Snapshot.Content);
        }

        public SettingsDefs Defs { get; }

        /// <summary>Opened from the pause menu: run-scoped controls are marked as applying to the next run.</summary>
        public bool InRun { get; }

        private JObject Stored => _profile.Doc[SK.Settings] as JObject;

        /// <summary>The effective value of a setting, or null for an unknown key.</summary>
        public JToken Value(string key)
        {
            var def = Defs.Find(key);
            if (def == null) return null;
            var stored = Stored?[key];
            if (stored != null && def.Refusal(stored) == null) return stored.DeepClone();
            var preset = _content.Snapshot.PlayerSettings?[key];
            if (preset != null && def.Refusal(preset) == null) return preset.DeepClone();
            return def.Def?.DeepClone();
        }

        public bool On(string key) => Value(key) is JValue v && v.Type == JTokenType.Boolean && (bool)v;

        public string Text(string key) => Js.Str(Value(key));

        public double Number(string key) => Js.Or0(Value(key));

        /// <summary>Sets a value and saves the profile. Returns null, or the refusal's string key (nothing changes).</summary>
        public string Set(string key, JToken value)
        {
            var def = Defs.Find(key);
            if (def == null) return S.SettingsRefusalUnknown;
            var refusal = def.Refusal(value);
            if (refusal != null) return refusal;
            if (!(_profile.Doc[SK.Settings] is JObject stored)) _profile.Doc[SK.Settings] = stored = new JObject();
            stored[key] = value.DeepClone();
            _profile.Save(_content.ContentHash);
            return null;
        }

        /// <summary>[Reset category ⟲]: every stored value of the category is cleared (the preset's or the def show again).</summary>
        public void ResetCategory(string category)
        {
            if (!(Stored is JObject stored)) return;
            var changed = false;
            foreach (var def in Defs.Controls.Where(c => c.Category == category)) changed |= stored.Remove(def.Key);
            if (changed) _profile.Save(_content.ContentHash);
        }

        public SettingsViewState View(UiData ui, string category = null)
        {
            var strings = ui.Strings;
            category ??= Defs.Categories.Select(c => c.Key).FirstOrDefault();
            var view = new SettingsViewState { Title = strings.Get(S.SettingsTitle), Category = category, Reset = strings.Get(S.SettingsReset) };
            foreach (var c in Defs.Categories) view.Rail.Add(new KeyValuePair<string, string>(c.Key, strings.Get(c.Value)));
            foreach (var def in Defs.Controls.Where(c => c.Category == category))
            {
                var value = Value(def.Key);
                var control = new SettingControlView
                {
                    Key = def.Key,
                    Type = def.Type,
                    Label = strings.Get(def.LabelKey),
                    Help = def.HelpKey != null ? strings.Get(def.HelpKey) : null,
                    Value = value,
                    Min = def.Min,
                    Max = def.Max,
                    Step = def.Step,
                    IsDefault = JToken.DeepEquals(value, def.Def),
                    AppliesNextRun = InRun && def.AppliesNextRun ? strings.Get(S.SettingsAppliesNextRun) : null,
                };
                foreach (var option in def.Options) control.Options.Add(new KeyValuePair<string, string>(option, OptionLabel(strings, def, option)));
                control.ValueText = ValueText(strings, def, value);
                view.Controls.Add(control);
            }
            return view;
        }

        private static string OptionLabel(StringTable strings, SettingDef def, string option)
        {
            if (def.OptionKey == null) return option;
            var key = string.Format(CultureInfo.InvariantCulture, def.OptionKey, option);
            return strings.Has(key) ? strings.Get(key) : option;
        }

        private static string ValueText(StringTable strings, SettingDef def, JToken value)
        {
            if (def.IsToggle) return strings.Get(value is JValue v && v.Type == JTokenType.Boolean && (bool)v ? S.SettingsOn : S.SettingsOff);
            if (def.IsEnum) return OptionLabel(strings, def, Js.Str(value));
            var number = Js.Or0(value).ToString(CultureInfo.InvariantCulture);
            return def.UnitKey != null ? strings.Format(def.UnitKey, new StringArgs().Add(P.Value, number)) : number;
        }

        /// <summary>The refusal's text, with the range filled for a slider.</summary>
        public string RefusalText(UiData ui, string key, string refusal)
        {
            var def = Defs.Find(key);
            var args = new StringArgs();
            if (def != null)
                args.Add(P.Min, def.Min.ToString(CultureInfo.InvariantCulture)).Add(P.Max, def.Max.ToString(CultureInfo.InvariantCulture));
            return ui.Strings.Format(refusal, args);
        }
    }
}
