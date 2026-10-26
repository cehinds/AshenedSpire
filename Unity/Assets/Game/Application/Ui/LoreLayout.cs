using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>How one lore text is set once the player's choices are resolved (web models/LoreTypeModel.js resolveLoreType read through styles/kit.css :root[data-lore-*]).</summary>
    public sealed class LoreType
    {
        /// <summary>The face the line wears (a shipped one: the Typeface choice, or the face a missing one is drawn in).</summary>
        public string Face;

        /// <summary>The identity line in card inspection: its italic or upright and its size in design px.</summary>
        public bool IdentityItalic;
        public double IdentityPx;

        /// <summary>The lore window's text: its italic or upright, size in design px and the line height as a multiple of the font's natural line.</summary>
        public bool WindowItalic;
        public double WindowPx;
        public double WindowIdentityPx;
        public double LeadingOfNatural;

        /// <summary>Letter spacing in em (the same for both).</summary>
        public double TrackingEm;
    }

    /// <summary>The three parts card lore is written in (web LoreTypeModel.loreParts, LORE.md section 7): the identity line, the history and a closing line set apart.</summary>
    public sealed class LoreParts
    {
        public string Identity = string.Empty;
        public readonly List<string> Body = new List<string>();
        public string Closing = string.Empty;

        /// <summary>The line has more behind it: a window to open.</summary>
        public bool HasMore => Body.Count > 0 || Closing.Length > 0;

        public static LoreParts Split(string text)
        {
            var parts = Regex.Split(text ?? string.Empty, LoreKeys.ParagraphBreak).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            var result = new LoreParts();
            if (parts.Count == 0) return result;
            result.Identity = parts[0];
            var rest = parts.Skip(1).ToList();
            if (rest.Count > 1)
            {
                result.Closing = rest[rest.Count - 1];
                rest.RemoveAt(rest.Count - 1);
            }
            result.Body.AddRange(rest);
            return result;
        }
    }

    /// <summary>
    /// ui/lore.json: the lists and numbers behind the Text &amp; lore rows. <see cref="Resolve"/> turns the player's stored choices into what the stylesheet draws: a value that is not on a list falls back to the default,
    /// a face that ships no italic is always upright, and a face the game does not ship is drawn in its substitute. Engine-free.
    /// </summary>
    public sealed class LoreLayout
    {
        public IReadOnlyList<string> Faces = Array.Empty<string>();
        public IReadOnlyDictionary<string, string> Substitutes = new Dictionary<string, string>();
        public IReadOnlyList<string> NoItalic = Array.Empty<string>();
        public IReadOnlyList<string> Slants = Array.Empty<string>();
        public IReadOnlyDictionary<string, KeyValuePair<double, double>> Sizes = new Dictionary<string, KeyValuePair<double, double>>();
        public IReadOnlyDictionary<string, double> Leading = new Dictionary<string, double>();
        public IReadOnlyDictionary<string, double> Tracking = new Dictionary<string, double>();
        public double IdentityBasePx;
        public double IdentityLeading;
        public double WindowIdentityPlusPx;
        public double NaturalLine;
        public string DefaultFace;
        public string DefaultSize;
        public string DefaultLeading;
        public string DefaultTracking;
        public string DefaultSlant;
        public string DefaultIdentitySlant;

        public static LoreLayout From(JObject json)
        {
            var defaults = (JObject)json[LoreKeys.Defaults];
            return new LoreLayout
            {
                Faces = ((JArray)json[LoreKeys.Faces]).Select(v => (string)v).ToList(),
                Substitutes = ((JObject)json[LoreKeys.Substitutes]).Properties().ToDictionary(p => p.Name, p => (string)p.Value),
                NoItalic = ((JArray)json[LoreKeys.NoItalic]).Select(v => (string)v).ToList(),
                Slants = ((JArray)json[LoreKeys.Slants]).Select(v => (string)v).ToList(),
                Sizes = ((JObject)json[LoreKeys.Sizes]).Properties().ToDictionary(p => p.Name, p => new KeyValuePair<double, double>(p.Value.Value<double>(LoreKeys.IdentityScale), p.Value.Value<double>(LoreKeys.WindowPx))),
                Leading = ((JObject)json[LoreKeys.LeadingRows]).Properties().ToDictionary(p => p.Name, p => p.Value.Value<double>()),
                Tracking = ((JObject)json[LoreKeys.TrackingRows]).Properties().ToDictionary(p => p.Name, p => p.Value.Value<double>()),
                IdentityBasePx = json.Value<double>(LoreKeys.IdentityBasePx),
                IdentityLeading = json.Value<double>(LoreKeys.IdentityLeading),
                WindowIdentityPlusPx = json.Value<double>(LoreKeys.WindowIdentityPlusPx),
                NaturalLine = json.Value<double>(LoreKeys.NaturalLine),
                DefaultFace = defaults.Value<string>(LoreKeys.DefaultFace),
                DefaultSize = defaults.Value<string>(LoreKeys.DefaultSize),
                DefaultLeading = defaults.Value<string>(LoreKeys.DefaultLeading),
                DefaultTracking = defaults.Value<string>(LoreKeys.DefaultTracking),
                DefaultSlant = defaults.Value<string>(LoreKeys.DefaultSlant),
                DefaultIdentitySlant = defaults.Value<string>(LoreKeys.DefaultIdentitySlant),
            };
        }

        private static string Pick(IEnumerable<string> list, string value, string fallback) => value != null && list.Contains(value) ? value : fallback;

        /// <summary>resolveLoreType(settings): the stored choices (null for one that is unset) as the line is set.</summary>
        public LoreType Resolve(string face, string size, string leading, string tracking, string slant, string identitySlant)
        {
            var chosen = Pick(Faces, face, DefaultFace);
            var shipped = Substitutes.TryGetValue(chosen, out var substitute) ? substitute : chosen;
            var upright = NoItalic.Contains(chosen);
            var sizeKey = Pick(Sizes.Keys, size, DefaultSize);
            return new LoreType
            {
                Face = shipped,
                IdentityItalic = !upright && Pick(Slants, identitySlant, DefaultIdentitySlant) == Slants[0],
                WindowItalic = !upright && Pick(Slants, slant, DefaultSlant) == Slants[0],
                IdentityPx = Sizes[sizeKey].Key * IdentityBasePx,
                WindowPx = Sizes[sizeKey].Value,
                WindowIdentityPx = Sizes[sizeKey].Value + WindowIdentityPlusPx,
                LeadingOfNatural = Leading[Pick(Leading.Keys, leading, DefaultLeading)] / NaturalLine,
                TrackingEm = Tracking[Pick(Tracking.Keys, tracking, DefaultTracking)],
            };
        }
    }
}
