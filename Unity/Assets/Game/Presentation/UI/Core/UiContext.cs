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
        private readonly Dictionary<string, UiArtCatalog> _groupCatalogs = new Dictionary<string, UiArtCatalog>(StringComparer.Ordinal);

        /// <summary>The effective run content (default preset), built on first use (W-04 needs it; the title does not).</summary>
        public RunContent RunContent => _runContent ??= RunContent.Load(Source);

        /// <summary>The run being played (set by W-04 Begin or a resume).</summary>
        public Core2RunSession Core2Session;

        /// <summary>The run being played, or null.</summary>
        public IClimb Climb => Core2Session;

        /// <summary>Forgets the run (the title, the run end).</summary>
        public void ClearRun()
        {
            Core2Session = null;
        }

        /// <summary>Quits the game (Application.Quit in a player; tests replace it).</summary>
        public Action Quit = UnityEngine.Application.Quit;

        public static UiContext Create(IContentSource source, UiData data, string saveDirectory)
        {
            var rules = SaveRules.From((Newtonsoft.Json.Linq.JObject)JsonContent.Parse(source.ReadText(ContentFiles.RulesSaves)));
            var context = new UiContext
            {
                Data = data,
                Source = source,
                Saves = new SaveService(saveDirectory, rules),
                Art = Resources.Load<UiArtCatalog>(UiResources.ArtCatalog),
                BuildVersion = UnityEngine.Application.version,
                DevBuild = Debug.isDebugBuild,
            };
            // The card face reads its art and glyphs through the context (the kit's CardView is built without one).
            Kit.CardView.Source = context;
            return context;
        }

        public Texture2D Texture(string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return null;
            var t = Art != null ? Art.Get(assetId) : null;
            if (t == null) t = GroupCatalog(assetId)?.Get(assetId);
            if (t == null && _missingArt.Add(assetId)) Debug.LogWarning(string.Format(System.Globalization.CultureInfo.InvariantCulture, UiMessages.MissingArt, assetId));
            return t;
        }

        /// <summary>The lazily loaded catalog (ui/art.json groups: cards, icons, scenes) that holds an id, or null when the id is in the always-loaded catalog.</summary>
        private UiArtCatalog GroupCatalog(string assetId)
        {
            var group = Data?.Art?.Config.GroupOf(assetId);
            if (group == null) return null;
            if (!_groupCatalogs.TryGetValue(group.Id, out var catalog))
                _groupCatalogs[group.Id] = catalog = Resources.Load<UiArtCatalog>(group.Resource);
            return catalog;
        }

        /// <summary>The icon texture of a content icon name (white glyph: tint it with -unity-background-image-tint-color), or null.</summary>
        public Texture2D Icon(string name) => Texture(Data.Art.Icon(name));

        /// <summary>The art of a card by id, type and tag ids (ui/art.json card rule; falls back to the category placeholder).</summary>
        public Texture2D CardArt(string cardId, string type, IEnumerable<string> tags, string profileId = null) => Texture(Data.Art.Card(cardId, type, tags, profileId).AssetId);

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
