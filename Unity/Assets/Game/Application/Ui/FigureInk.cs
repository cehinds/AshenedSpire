using System;
using System.Collections.Generic;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>The visible columns of a combat figure's frame: the first and one past the last opaque column (alpha of at least 40) and the image's width.</summary>
    public struct FigureInkColumns
    {
        public double X0;
        public double X1;
        public double Width;

        /// <summary>The ink's share of the image's width.</summary>
        public double Share => Width > 0 ? (X1 - X0) / Width : 1;
    }

    /// <summary>
    /// ui/figureInk.json (P-4, D-1540..): what the web reads off each frame with imageBounds to size a figure's floor shadow (battlefieldStage.js syncFloorShadows). Engine-free.
    /// </summary>
    public sealed class FigureInk
    {
        private readonly Dictionary<string, FigureInkColumns> _frames = new Dictionary<string, FigureInkColumns>(StringComparer.Ordinal);

        public FigureInk(JObject json)
        {
            foreach (var row in (JObject)json[InkKeys.Frames])
                _frames[row.Key] = new FigureInkColumns { X0 = row.Value.Value<double>(InkKeys.X0), X1 = row.Value.Value<double>(InkKeys.X1), Width = row.Value.Value<double>(InkKeys.Width) };
        }

        public int Count => _frames.Count;

        /// <summary>The ink of a frame by its art id, or false when it was not measured.</summary>
        public bool TryGet(string artId, out FigureInkColumns ink)
        {
            if (artId != null && _frames.TryGetValue(artId, out ink)) return true;
            ink = default;
            return false;
        }
    }
}
