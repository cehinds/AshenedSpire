using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Ashen.EditorTools
{
    /// <summary>
    /// Applies data-driven import presets (Editor/Config/importRules.json) to every texture under Assets/Art before
    /// Unity imports it: Sprite, alpha as transparency, no mipmaps, bilinear, per-domain max size and compression.
    /// </summary>
    public sealed class AshenTextureImporter : AssetPostprocessor
    {
        private const string RulesPath = "Assets/Editor/Config/importRules.json";
        private static JArray _rules;

        private static JArray Rules => _rules ?? (_rules = (JArray)JObject.Parse(File.ReadAllText(RulesPath))["rules"]);

        private void OnPreprocessTexture()
        {
            var path = assetPath.Replace(Path.DirectorySeparatorChar, '/');
            var rule = Rules.OfType<JObject>().FirstOrDefault(r => path.StartsWith((string)r["prefix"], StringComparison.Ordinal));
            if (rule == null) return;
            var ti = (TextureImporter)assetImporter;
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.alphaIsTransparency = true;
            ti.mipmapEnabled = false;
            ti.filterMode = FilterMode.Bilinear;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.spritePixelsPerUnit = (float)rule["pixelsPerUnit"];
            ti.maxTextureSize = (int)rule["maxSize"];
            ti.textureCompression = (string)rule["compression"] == "highQuality" ? TextureImporterCompression.CompressedHQ : TextureImporterCompression.Compressed;
        }
    }
}
