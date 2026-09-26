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

        public Color TokenColor(string key)
        {
            return ColorUtility.TryParseHtmlString(Data.Tokens.Color(key), out var c) ? c : Color.black;
        }
    }
}
