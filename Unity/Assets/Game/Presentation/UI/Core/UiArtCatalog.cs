using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ashen.Presentation.UI
{
    /// <summary>
    /// The UI's slice of the asset registry (built by Ashen.EditorTools.Cli.BuildUiArt from ui/components.json
    /// art.include and assets/registry.json). Textures are referenced by GUID, so the gitignored PNGs resolve
    /// again once the asset pipeline regenerates them (D-018).
    /// </summary>
    public sealed class UiArtCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public string id;
            public Texture2D texture;
        }

        public List<Entry> entries = new List<Entry>();

        private Dictionary<string, Texture2D> _index;

        public Texture2D Get(string id)
        {
            if (_index == null)
            {
                _index = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
                foreach (var e in entries) if (e != null && e.id != null) _index[e.id] = e.texture;
            }
            return _index.TryGetValue(id, out var t) ? t : null;
        }
    }
}
