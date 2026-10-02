using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.App.Run;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using W = Ashen.Generated.WebConfigKeys;
using WR = Ashen.Generated.WebConfigReasons;

namespace Ashen.App.Settings
{
    /// <summary>What an import did: the keys taken, the keys reported (with why), and the content patch the game keys make.</summary>
    public sealed class WebImportResult
    {
        /// <summary>False when the file is not a web config (no overrides object): nothing was taken.</summary>
        public bool Recognised;

        /// <summary>The game keys taken, in web form (stored as the profile's advanced config; exported back as they came).</summary>
        public readonly JObject GameConfig = new JObject();

        /// <summary>The player settings taken (settings.&lt;key&gt; that name a W-18 control and fit it).</summary>
        public readonly JObject PlayerSettings = new JObject();

        /// <summary>Every key not taken, with its reason (WebConfigReasons) — reported, never dropped silently.</summary>
        public readonly List<KeyValuePair<string, string>> Reported = new List<KeyValuePair<string, string>>();

        /// <summary>The id-keyed patch document the game keys make ({ files: { path: mergePatch } }).</summary>
        public JObject Patch = new JObject();

        public int Taken => GameConfig.Count + PlayerSettings.Count;
    }

    /// <summary>
    /// Web-format config import and export (US-15.4; D-162). A web file is { schemaVersion, overrides: { key: value } }
    /// with keys gameConfig.&lt;bundle path&gt; and settings.&lt;player setting&gt;. Game keys map onto this build's content
    /// through settings/webKeyMap.json (generated from the transform config; renames first, the longest root wins) into
    /// a merge patch that is validated against the schemas with the active preset; a key that has no content path, or
    /// whose value breaks a schema, is reported. Player settings are validated by their W-18 control. Export writes the
    /// stored game keys and the player's settings back in the same web form, so owner files round-trip. Engine-free.
    /// </summary>
    public static class WebConfig
    {
        private sealed class Root
        {
            public string Web;
            public string File;
            public string Path;
        }

        /// <summary>settings/webKeyMap.json, parsed.</summary>
        private sealed class KeyMap
        {
            public string GamePrefix;
            public string SettingsPrefix;
            public string MirrorPrefix;
            public List<(Regex From, string To)> Renames;
            public List<Root> Roots;

            public static KeyMap From(IContentSource source)
            {
                var map = (JObject)JsonContent.Parse(source.ReadText(ContentFiles.SettingsWebKeyMap));
                return new KeyMap
                {
                    GamePrefix = (string)map[W.GameConfigPrefix],
                    SettingsPrefix = (string)map[W.SettingsPrefix],
                    MirrorPrefix = (string)map[W.MirrorPrefix],
                    Renames = Js.Items(map[W.Renames]).OfType<JObject>().Select(r => (new Regex((string)r[W.From], RegexOptions.CultureInvariant), (string)r[W.To])).ToList(),
                    Roots = Js.Items(map[W.Roots]).OfType<JObject>().Select(r => new Root { Web = (string)r[W.Web], File = (string)r[W.File], Path = (string)r[W.Path] }).ToList(),
                };
            }

            /// <summary>A game key's content file and path (renames first, the longest root wins), or null.</summary>
            public (string File, string[] Segments)? Locate(string key)
            {
                var renamed = key;
                foreach (var (from, to) in Renames)
                    if (from.IsMatch(renamed)) { renamed = from.Replace(renamed, to); break; }
                if (!renamed.StartsWith(GamePrefix, StringComparison.Ordinal)) return null;
                var bare = renamed.Substring(GamePrefix.Length);
                var root = Roots.FirstOrDefault(r => bare.StartsWith(r.Web + W.Separator, StringComparison.Ordinal));
                if (root == null) return null;
                var rest = bare.Substring(root.Web.Length + W.Separator.Length);
                return (root.File, (root.Path == null ? rest : root.Path + W.Separator + rest).Split(new[] { W.Separator }, StringSplitOptions.None));
            }
        }

        public static WebImportResult Import(JObject web, IContentSource source, string presetId, SettingsDefs defs)
        {
            var result = new WebImportResult();
            if (!(web?[W.Overrides] is JObject overrides)) return result;
            result.Recognised = true;
            var map = KeyMap.From(source);
            var game = new List<JProperty>();
            foreach (var p in overrides.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var key = p.Name;
                if (key.StartsWith(map.MirrorPrefix, StringComparison.Ordinal)) result.Reported.Add(Pair(key, WR.Mirror));
                else if (key.StartsWith(map.SettingsPrefix, StringComparison.Ordinal))
                {
                    var name = key.Substring(map.SettingsPrefix.Length);
                    var def = defs.Find(name);
                    if (def == null) result.Reported.Add(Pair(key, WR.UnknownSetting));
                    else if (def.Refusal(p.Value) != null) result.Reported.Add(Pair(key, WR.SettingDoesNotFit));
                    else result.PlayerSettings[name] = p.Value.DeepClone();
                }
                else if (key.StartsWith(map.GamePrefix, StringComparison.Ordinal)) game.Add(p);
                else result.Reported.Add(Pair(key, WR.NotAKey));
            }

            var baseContent = new ConfigLayers(source).Build(new LayerSelection { PresetId = presetId }).Content;
            var built = Build(game, map, baseContent);
            foreach (var p in game)
            {
                if (built.Reasons.TryGetValue(p.Name, out var reason)) result.Reported.Add(Pair(p.Name, reason));
                else result.GameConfig[p.Name] = p.Value.DeepClone();
            }
            result.Patch = built.Patch;

            // The schemas over the active preset: a file the patch breaks reports its keys, and the rest is rebuilt without them.
            if (result.GameConfig.Count > 0)
            {
                var report = new ValidationReport();
                new ConfigLayers(source).Build(new LayerSelection { PresetId = presetId, Patches = new[] { result.Patch } }, report);
                var broken = new HashSet<string>(report.Issues.Select(i => i.File), StringComparer.Ordinal);
                if (broken.Count > 0)
                {
                    foreach (var key in result.GameConfig.Properties().Select(x => x.Name).ToList())
                    {
                        var at = map.Locate(key);
                        if (at == null || !broken.Contains(at.Value.File)) continue;
                        result.GameConfig.Remove(key);
                        result.Reported.Add(Pair(key, WR.Invalid));
                    }
                    result.Patch = Build(result.GameConfig.Properties().ToList(), map, baseContent).Patch;
                }
            }
            return result;
        }

        /// <summary>
        /// The game keys applied to a working copy of each file they touch (array elements by index, strict: the value must
        /// exist and be the same kind), then each file's merge patch is its difference from the base (an edited array is
        /// replaced whole, as RFC 7386 does).
        /// </summary>
        private static (JObject Patch, Dictionary<string, string> Reasons) Build(IEnumerable<JProperty> keys, KeyMap map, ContentSet baseContent)
        {
            var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
            var working = new Dictionary<string, JToken>(StringComparer.Ordinal);
            foreach (var p in keys)
            {
                var at = map.Locate(p.Name);
                if (at == null || at.Value.Segments.Any(string.IsNullOrEmpty)) { reasons[p.Name] = WR.NoRoot; continue; }
                var (file, segments) = at.Value;
                if (!working.TryGetValue(file, out var doc))
                {
                    doc = baseContent.Get(file);
                    if (doc == null) { reasons[p.Name] = WR.NoRoot; continue; }
                    working[file] = doc;
                }
                var parent = At(doc, segments.Take(segments.Length - 1));
                var current = parent == null ? null : Child(parent, segments[segments.Length - 1]);
                if (current == null) { reasons[p.Name] = WR.UnknownPath; continue; }
                if (!SameKind(current, p.Value)) { reasons[p.Name] = WR.WrongType; continue; }
                current.Replace(p.Value.DeepClone());
            }
            var files = new JObject();
            foreach (var kv in working.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var diff = Diff(baseContent.Get(kv.Key), kv.Value);
                if (diff != null) files[kv.Key] = diff;
            }
            return (new JObject { [W.Files] = files }, reasons);
        }

        /// <summary>The content patch stored web game keys make over a preset (a new run's content).</summary>
        public static JObject PatchFor(JObject gameConfig, IContentSource source, string presetId = null)
        {
            var baseContent = new ConfigLayers(source).Build(new LayerSelection { PresetId = presetId }).Content;
            return Build((gameConfig ?? new JObject()).Properties().ToList(), KeyMap.From(source), baseContent).Patch;
        }

        /// <summary>Stores an import on the profile: the game keys as the advanced config (new runs), the settings as the player's. Saved once.</summary>
        public static void Apply(ProfileStore profile, WebImportResult result, string contentHash)
        {
            if (!result.Recognised) return;
            profile.Doc[W.AdvancedConfig] = new JObject { [W.SchemaVersion] = 1, [W.Overrides] = result.GameConfig.DeepClone() };
            if (!(profile.Doc[SettingsKeys.Settings] is JObject stored)) profile.Doc[SettingsKeys.Settings] = stored = new JObject();
            foreach (var p in result.PlayerSettings.Properties()) stored[p.Name] = p.Value.DeepClone();
            profile.Save(contentHash);
        }

        /// <summary>The stored game keys (web form) of the profile's advanced config.</summary>
        public static JObject StoredGameConfig(JObject profile) => profile?.Obj(W.AdvancedConfig)?.Obj(W.Overrides) ?? new JObject();

        /// <summary>The run content for a new run: the preset with the profile's imported game keys over it.</summary>
        public static RunContent ContentFor(IContentSource source, JObject profile, string presetId = null)
        {
            var stored = StoredGameConfig(profile);
            return stored.Count == 0 ? RunContent.Load(source, presetId) : RunContent.Load(source, presetId, new[] { PatchFor(stored, source, presetId) });
        }

        /// <summary>Export: { schemaVersion, overrides } with the stored game keys and every stored player setting, keys sorted.</summary>
        public static JObject Export(JObject profile, string settingsPrefix)
        {
            var overrides = new SortedDictionary<string, JToken>(StringComparer.Ordinal);
            foreach (var p in StoredGameConfig(profile).Properties()) overrides[p.Name] = p.Value.DeepClone();
            foreach (var p in profile?.Obj(SettingsKeys.Settings)?.Properties() ?? Enumerable.Empty<JProperty>()) overrides[settingsPrefix + p.Name] = p.Value.DeepClone();
            var o = new JObject();
            foreach (var kv in overrides) o[kv.Key] = kv.Value;
            return new JObject { [W.SchemaVersion] = 1, [W.Overrides] = o };
        }

        public static string SettingsPrefix(IContentSource source) => KeyMap.From(source).SettingsPrefix;

        private static JToken At(JToken node, IEnumerable<string> segments)
        {
            foreach (var seg in segments)
            {
                node = Child(node, seg);
                if (node == null) return null;
            }
            return node;
        }

        /// <summary>An object's property, or an array's element by its index.</summary>
        private static JToken Child(JToken node, string segment)
        {
            if (node is JObject o) return o.TryGetValue(segment, out var v) ? v : null;
            if (node is JArray a && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var i)) return i >= 0 && i < a.Count ? a[i] : null;
            return null;
        }

        /// <summary>Numbers match numbers (integer or not), strings strings, booleans booleans, objects objects, arrays arrays; null replaces anything.</summary>
        private static bool SameKind(JToken current, JToken value)
        {
            bool Number(JToken t) => t.Type == JTokenType.Integer || t.Type == JTokenType.Float;
            if (value.Type == JTokenType.Null || current.Type == JTokenType.Null) return true;
            if (Number(current) || Number(value)) return Number(current) && Number(value);
            return current.Type == value.Type;
        }

        /// <summary>RFC 7386 diff: objects member by member, anything else replaced whole; null when equal.</summary>
        private static JToken Diff(JToken from, JToken to)
        {
            if (JToken.DeepEquals(from, to)) return null;
            if (!(from is JObject a) || !(to is JObject b)) return to.DeepClone();
            var patch = new JObject();
            foreach (var p in b.Properties())
            {
                var d = Diff(a[p.Name], p.Value);
                if (d != null) patch[p.Name] = d;
            }
            foreach (var p in a.Properties())
                if (b[p.Name] == null) patch[p.Name] = JValue.CreateNull();
            return patch;
        }

        private static KeyValuePair<string, string> Pair(string key, string reason) => new KeyValuePair<string, string>(key, reason);
    }
}
