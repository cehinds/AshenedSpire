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

        /// <summary>An enum's named bundles: choosing an option sets these other settings with it (the web's combat pacing preset).</summary>
        public JObject Presets;

        /// <summary>The enum whose choice returns to its first option when this setting is edited by hand.</summary>
        public string ResetsPreset;

        /// <summary>A control of the 405 build only (no row in the Core 2 web build): kept for that code, not on the page.</summary>
        public bool LegacyOnly;

        /// <summary>False for a toggle whose switch reads the other way round (the web's muteAudio: the switch is on while the audio is not muted).</summary>
        public bool Positive = true;

        /// <summary>A button control's action (resetAll).</summary>
        public string Action;

        /// <summary>How the page draws the row when it is not a plain control (the Developer tools switch).</summary>
        public string Row;

        /// <summary>The state whose words fill the row's help (music: on, off, muted).</summary>
        public string HelpState;

        public bool IsButton => Type == SV.Button;
        public bool IsToggle => Type == SV.Toggle;
        public bool IsEnum => Type == SV.Enum;
        public bool IsSlider => Type == SV.Slider;
        public bool AppliesNextRun => Scope == SV.Run;

        /// <summary>Whether a value fits this control (a boolean; one of the options; a number in range on the step grid).</summary>
        public string Refusal(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null || IsButton) return S.SettingsRefusalType;
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
                    Presets = c[SK.Presets] as JObject,
                    ResetsPreset = c.Str(SK.ResetsPreset),
                    LegacyOnly = c.Is(SK.LegacyOnly),
                    Positive = c[SK.Positive] == null || c.Is(SK.Positive),
                    Action = c.Str(SK.Action),
                    Row = c.Str(SK.Row),
                    HelpState = c.Str(SK.HelpState),
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

        /// <summary>The settings/defaults.json category the control is filed under.</summary>
        public string Category;

        public string Label;
        public string Help;
        public JToken Value;
        public string ValueText;
        public readonly List<KeyValuePair<string, string>> Options = new List<KeyValuePair<string, string>>();
        public double Min;
        public double Max;
        public double Step;
        public bool IsDefault;

        /// <summary>The switch position of a toggle (the stored value, or its opposite for a control that reads the other way round).</summary>
        public bool Shown;

        public bool Positive = true;
        public string Action;
        public string Row;

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
    public sealed partial class SettingsSession
    {
        private readonly RunContent _content;
        private readonly ProfileStore _profile;

        /// <param name="core2">The Core 2 build's player settings: the web's own defaults rule (the 405 reference preset's player settings are not read).</param>
        public SettingsSession(RunContent content, ProfileStore profile, bool inRun = false, bool core2 = false)
        {
            _content = content;
            _profile = profile;
            InRun = inRun;
            Core2 = core2;
            Defs = SettingsDefs.From(content.Snapshot.Content);
        }

        public SettingsDefs Defs { get; }

        /// <summary>The Core 2 build's player settings: the stored value, else the control's default (the 405 preset's player settings belong to the 405 build).</summary>
        public bool Core2 { get; }

        /// <summary>A setting was stored or a category reset (the session that wrote it, and the key; null for a reset), so live consumers re-read it.</summary>
        public static event Action<SettingsSession, string> Changed;

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
            var preset = Core2 ? null : _content.Snapshot.PlayerSettings?[key];
            if (preset != null && def.Refusal(preset) == null) return preset.DeepClone();
            return def.Def?.DeepClone();
        }

        public bool On(string key) => Value(key) is JValue v && v.Type == JTokenType.Boolean && (bool)v;

        public string Text(string key) => Js.Str(Value(key));

        public double Number(string key) => Js.Or0(Value(key));

        /// <summary>The opening has been shown on this profile (the web's settings.prologueSeen: "First time per profile" plays it once).</summary>
        public bool PrologueSeen => Stored?[SettingIds.PrologueSeen] is JValue v && v.Type == JTokenType.Boolean && (bool)v;

        /// <summary>Remembers that the opening was shown (the web's persistSettingsChange({prologueSeen: true}) when it finishes, skipped or not).</summary>
        public void MarkPrologueSeen()
        {
            if (PrologueSeen) return;
            if (!(_profile.Doc[SK.Settings] is JObject stored)) _profile.Doc[SK.Settings] = stored = new JObject();
            stored[SettingIds.PrologueSeen] = true;
            _profile.Save(_content.ContentHash);
        }

        /// <summary>The first-open tip banner was dismissed with Got it.</summary>
        public bool TipDismissed => Stored?[SettingIds.SettingsTipDismissed] is JValue v && v.Type == JTokenType.Boolean && (bool)v;

        public void DismissTip()
        {
            if (TipDismissed) return;
            if (!(_profile.Doc[SK.Settings] is JObject stored)) _profile.Doc[SK.Settings] = stored = new JObject();
            stored[SettingIds.SettingsTipDismissed] = true;
            _profile.Save(_content.ContentHash);
        }

        /// <summary>A copy of every stored value (what a reset's Undo puts back).</summary>
        public JObject Snapshot() => Stored != null ? (JObject)Stored.DeepClone() : new JObject();

        /// <summary>Puts a snapshot back (the web's Undo bar after a reset).</summary>
        public void Restore(JObject snapshot)
        {
            _profile.Doc[SK.Settings] = snapshot?.DeepClone() ?? new JObject();
            _profile.Save(_content.ContentHash);
            Changed?.Invoke(this, null);
        }

        /// <summary>The music's volume, 0 to 1: the web's resolveMusicEnabled with muteAudio over it (music plays when it is on and the audio is not muted).</summary>
        public double MusicLevel => On(SettingIds.MusicEnabled) && !On(SettingIds.MuteAudio) ? Number(SettingIds.MusicVolume) / UiMath.Percent : 0;

        /// <summary>The sound effects' volume, 0 to 1 (muteAudio silences them; the music switch does not).</summary>
        public double SfxLevel => On(SettingIds.MuteAudio) ? 0 : Number(SettingIds.SfxVolume) / UiMath.Percent;

        /// <summary>Sets a value and saves the profile. Returns null, or the refusal's string key (nothing changes).</summary>
        public string Set(string key, JToken value)
        {
            var def = Defs.Find(key);
            if (def == null) return S.SettingsRefusalUnknown;
            var refusal = def.Refusal(value);
            if (refusal != null) return refusal;
            if (!(_profile.Doc[SK.Settings] is JObject stored)) _profile.Doc[SK.Settings] = stored = new JObject();
            Store(stored, Defs, def, value);
            _profile.Save(_content.ContentHash);
            Changed?.Invoke(this, key);
            return null;
        }

        /// <summary>
        /// Writes one validated value with its preset coupling: a preset writes its bundle with it; a hand edit of a bundled
        /// value returns the preset to its first option. The one rule for a settings edit and a web-config import.
        /// </summary>
        public static void Store(JObject stored, SettingsDefs defs, SettingDef def, JToken value)
        {
            stored[def.Key] = value.DeepClone();
            if (def.Presets != null && def.Presets[(string)value] is JObject bundle)
                foreach (var p in bundle.Properties()) stored[p.Name] = p.Value.DeepClone();
            else if (def.ResetsPreset != null && defs.Find(def.ResetsPreset) is SettingDef preset && preset.Options.Count > 0)
                stored[def.ResetsPreset] = preset.Options[0];
        }

        /// <summary>
        /// Stores imported (already validated) settings through <see cref="Store"/>, in import order. Where the import names
        /// both a preset and values it bundles, the explicit values win and the imported preset stays only if every
        /// imported bundled value matches its bundle; otherwise it returns to its first option. Independent of order.
        /// </summary>
        public static void StoreImported(JObject stored, SettingsDefs defs, JObject imported)
        {
            foreach (var p in imported.Properties())
                if (defs.Find(p.Name) is SettingDef def) Store(stored, defs, def, p.Value);
            foreach (var p in imported.Properties())
                if (defs.Find(p.Name) is SettingDef def && def.Presets == null) stored[p.Name] = p.Value.DeepClone();
            foreach (var p in imported.Properties())
            {
                if (!(defs.Find(p.Name) is SettingDef def) || def.Presets == null || def.Options.Count == 0) continue;
                var bundle = def.Presets[(string)p.Value] as JObject;
                var consistent = bundle == null || bundle.Properties().All(b => imported[b.Name] == null || JToken.DeepEquals(imported[b.Name], b.Value));
                stored[p.Name] = consistent ? p.Value.DeepClone() : (JToken)def.Options[0];
            }
        }

        /// <summary>[Reset category ⟲]: every stored value of the category is cleared (the preset's or the def show again).</summary>
        public void ResetCategory(string category)
        {
            if (!(Stored is JObject stored)) return;
            var changed = false;
            foreach (var def in Defs.Controls.Where(c => c.Category == category)) changed |= stored.Remove(def.Key);
            if (!changed) return;
            _profile.Save(_content.ContentHash);
            Changed?.Invoke(this, null);
        }

        public SettingsViewState View(UiData ui, string category = null)
        {
            var strings = ui.Strings;
            category ??= Defs.Categories.Select(c => c.Key).FirstOrDefault();
            var view = new SettingsViewState { Title = strings.Get(S.SettingsTitle), Category = category, Reset = strings.Get(S.SettingsReset) };
            foreach (var c in Defs.Categories) view.Rail.Add(new KeyValuePair<string, string>(c.Key, strings.Get(c.Value)));
            foreach (var def in Defs.Controls.Where(c => c.Category == category)) view.Controls.Add(ControlOf(ui, def));
            return view;
        }

        /// <summary>One control as the screen draws it: its words, its effective value, its options and whether it still has its default.</summary>
        public SettingControlView ControlOf(UiData ui, SettingDef def)
        {
            var strings = ui.Strings;
            var value = Value(def.Key);
            var control = new SettingControlView
            {
                Key = def.Key,
                Type = def.Type,
                Category = def.Category,
                Label = strings.Get(def.LabelKey),
                Help = HelpOf(strings, def),
                Value = value,
                Min = def.Min,
                Max = def.Max,
                Step = def.Step,
                IsDefault = def.IsButton || JToken.DeepEquals(value, def.Def),
                Positive = def.Positive,
                Action = def.Action,
                Row = def.Row,
                Shown = def.IsToggle && value is JValue sv && sv.Type == JTokenType.Boolean && (bool)sv == def.Positive,
                AppliesNextRun = InRun && def.AppliesNextRun ? strings.Get(S.SettingsAppliesNextRun) : null,
            };
            foreach (var option in def.Options) control.Options.Add(new KeyValuePair<string, string>(option, OptionLabel(strings, def, option)));
            control.ValueText = ValueText(strings, def, value);
            return control;
        }

        /// <summary>A row's help: its string, or for a stateful row the words of the state it is in (the web's musicEnabledCondition).</summary>
        private string HelpOf(StringTable strings, SettingDef def)
        {
            if (def.HelpState == SV.HelpStateMusic)
            {
                if (!On(SettingIds.MusicEnabled)) return strings.Get(StringKeys.SettingsMusicEnabledOff);
                if (On(SettingIds.MuteAudio)) return strings.Get(StringKeys.SettingsMusicEnabledMuted);
                return strings.Format(StringKeys.SettingsMusicEnabledHelp, new StringArgs().Add(P.Value, SettingsSession.NumberText(Number(SettingIds.MusicVolume))));
            }
            return def.HelpKey != null ? strings.Get(def.HelpKey) : null;
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
