using System;
using System.Collections.Generic;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI
{
    /// <summary>Services a screen can reach: parsed UI data, the content source, saves, art, and platform hooks.</summary>
    public sealed class UiContext
    {
        public UiData Data;
        public IContentSource Source;
        public SaveService Saves;
        public UiArtCatalog Art;
        public string BuildVersion;
        public bool DevBuild;

        private RunContent _runContent;
        private readonly HashSet<string> _missingArt = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The effective run content (default preset), built on first use (W-04 needs it; the title does not).</summary>
        public RunContent RunContent => _runContent ??= RunContent.Load(Source);

        /// <summary>The run being played (set by W-04 Begin or a resume; the combat screen reads it).</summary>
        public RunSession Session;

        /// <summary>Quits the game (Application.Quit in a player; tests replace it).</summary>
        public Action Quit = UnityEngine.Application.Quit;

        public static UiContext Create(IContentSource source, UiData data, string saveDirectory)
        {
            var rules = SaveRules.From((Newtonsoft.Json.Linq.JObject)JsonContent.Parse(source.ReadText(ContentFiles.RulesSaves)));
            return new UiContext
            {
                Data = data,
                Source = source,
                Saves = new SaveService(saveDirectory, rules),
                Art = Resources.Load<UiArtCatalog>(UiResources.ArtCatalog),
                BuildVersion = UnityEngine.Application.version,
                DevBuild = Debug.isDebugBuild,
            };
        }

        public Texture2D Texture(string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return null;
            var t = Art != null ? Art.Get(assetId) : null;
            if (t == null && _missingArt.Add(assetId)) Debug.LogWarning(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiMessages.MissingArt, assetId));
            return t;
        }

        /// <summary>Sets an element's background from a registry id; leaves the grey box when the art is missing.</summary>
        public void ApplyBackground(VisualElement element, string assetId)
        {
            var t = Texture(assetId);
            element.style.backgroundImage = t != null ? new StyleBackground(t) : new StyleBackground(StyleKeyword.None);
        }

        /// <summary>A colour token as a Color: hex through ColorUtility, else the rgb()/rgba() form USS also reads (channels 0–255, alpha 0–1).</summary>
        public Color TokenColor(string key)
        {
            var value = Data.Tokens.Color(key);
            if (ColorUtility.TryParseHtmlString(value, out var c)) return c;
            var n = Numbers(value);
            var channels = (int)UiMath.RgbChannels;
            if (n.Count < channels) return Color.black;
            var byteMax = (float)UiMath.ByteMax;
            return new Color(n[0] / byteMax, n[1] / byteMax, n[channels - 1] / byteMax, n.Count > channels ? n[channels] : 1f);
        }

        /// <summary>The decimal numbers in a string, in order (the channels of an rgb()/rgba() colour).</summary>
        private static List<float> Numbers(string value)
        {
            var list = new List<float>();
            if (string.IsNullOrEmpty(value)) return list;
            var start = -1;
            for (var i = 0; i <= value.Length; i++)
            {
                var part = i < value.Length && (char.IsDigit(value[i]) || value[i] == UiFormats.DecimalPoint[0]);
                if (part && start < 0) start = i;
                if (!part && start >= 0)
                {
                    if (float.TryParse(value.Substring(start, i - start), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f)) list.Add(f);
                    start = -1;
                }
            }
            return list;
        }
    }
}
