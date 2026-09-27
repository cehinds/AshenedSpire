using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Run;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// The act map's board (docs/design/04 W-06): one opaque disc per node with its glyph, laid out by floor (the first
    /// floor at the foot, the boss at the head) and column, the edges drawn beneath (the travelled trail gold and dotted,
    /// the ways on from where the run stands lit), boss nodes labelled with their destination. Only reachable nodes take
    /// focus (they carry the rim and the › cue); any node can be clicked, and the screen decides (select, enter, refuse).
    /// Node buttons are added in floor-then-column order, so keyboard and pad walk the reachable row in order.
    /// </summary>
    [UxmlElement]
    public partial class MapBoard : VisualElement
    {
        private readonly Dictionary<string, Button> _nodes = new Dictionary<string, Button>(StringComparer.Ordinal);
        private ActMapViewState _view;
        private string _selected;

        public MapBoard()
        {
            AddToClassList(nameof(MapBoard).ToLowerInvariant());
            generateVisualContent += Draw;
            RegisterCallback<GeometryChangedEvent>(_ => Place());
        }

        /// <summary>The height of one floor in reference px (the zoom scales it).</summary>
        public float RowHeight { get; set; }

        public Color EdgeColor { get; set; } = Color.gray;
        public Color TrailColor { get; set; } = Color.yellow;
        public Color WayColor { get; set; } = Color.white;
        public float EdgeWidth { get; set; } = 1f;
        public float DotSpacing { get; set; } = 1f;

        /// <summary>A node was clicked or submitted (its id and its button).</summary>
        public event Action<string, VisualElement> NodePicked;

        public IReadOnlyDictionary<string, Button> Nodes => _nodes;

        public Button Node(string id) => id != null && _nodes.TryGetValue(id, out var b) ? b : null;

        public void Bind(ActMapViewState view)
        {
            _view = view;
            Clear();
            _nodes.Clear();
            if (view == null) return;
            style.height = RowHeight * (view.Floors + 1);
            foreach (var node in view.Nodes.OrderBy(n => n.Floor).ThenBy(n => n.Col))
            {
                var button = new Button { name = node.Id, focusable = node.Reachable, tooltip = node.AccessibleName };
                button.AddToClassList(UiClasses.MapNode);
                button.AddToClassList(UiClasses.Touch);
                button.AddToClassList(UiClasses.MapNodeKindPrefix + node.Kind);
                button.EnableInClassList(UiClasses.MapNodeReachable, node.Reachable);
                button.EnableInClassList(UiClasses.MapNodeCurrent, node.Current);
                button.EnableInClassList(UiClasses.MapNodeTravelled, node.Travelled && !node.Current);
                var glyph = new LocLabel(node.Current ? StringKeys.GlyphNodeCurrent : node.GlyphKey) { pickingMode = PickingMode.Ignore };
                glyph.AddToClassList(UiClasses.MapNodeGlyph);
                button.Add(glyph);
                if (node.Reachable)
                {
                    var cue = new LocLabel(StringKeys.MapReachableCue) { pickingMode = PickingMode.Ignore };
                    cue.AddToClassList(UiClasses.MapNodeCue);
                    button.Add(cue);
                }
                if (!string.IsNullOrEmpty(node.BossLabel))
                {
                    var label = new LocLabel { pickingMode = PickingMode.Ignore };
                    label.SetResolved(node.BossLabel);
                    label.AddToClassList(UiClasses.MapNodeLabel);
                    label.AddToClassList(UiClasses.ValueText);
                    button.Add(label);
                }
                var centred = -(float)(UiMath.Half * UiMath.Percent);
                button.style.translate = new Translate(Length.Percent(centred), Length.Percent(centred));
                var id = node.Id;
                button.clicked += () => NodePicked?.Invoke(id, button);
                Add(button);
                _nodes[node.Id] = button;
            }
            Select(_selected != null && _nodes.ContainsKey(_selected) ? _selected : null);
            Place();
        }

        /// <summary>Marks one node selected (the tray's node), or none.</summary>
        public void Select(string id)
        {
            _selected = id;
            foreach (var p in _nodes) p.Value.EnableInClassList(UiClasses.MapNodeSelected, p.Key == id);
        }

        /// <summary>The centre of a node in board coordinates (floor 1 at the foot).</summary>
        public Vector2 Centre(MapNodeView node)
        {
            var rect = contentRect;
            var columns = Math.Max(1, _view?.Columns ?? 1);
            var fx = columns > 1 ? (float)node.Col / (columns - 1) : (float)UiMath.Half;
            var x = rect.xMin + fx * rect.width;
            var y = rect.yMax - ((float)node.Floor - (float)UiMath.Half) * RowHeight;
            return new Vector2(x, y);
        }

        private void Place()
        {
            if (_view == null || float.IsNaN(contentRect.width)) return;
            foreach (var node in _view.Nodes)
            {
                if (!_nodes.TryGetValue(node.Id, out var button)) continue;
                var c = Centre(node);
                button.style.left = c.x;
                button.style.top = c.y;
            }
            MarkDirtyRepaint();
        }

        /// <summary>Scroll offset that brings a node to the middle of a viewport of the given height.</summary>
        public float OffsetFor(string id, float viewportHeight)
        {
            var node = _view?.Node(id);
            if (node == null) return 0f;
            return Math.Max(0f, Centre(node).y - viewportHeight * (float)UiMath.Half);
        }

        private void Draw(MeshGenerationContext mgc)
        {
            if (_view == null || float.IsNaN(contentRect.width)) return;
            var painter = mgc.painter2D;
            var reachable = new HashSet<string>(_view.Nodes.Where(n => n.Reachable).Select(n => n.Id), StringComparer.Ordinal);
            foreach (var edge in _view.Edges)
            {
                var from = _view.Node(edge.From);
                var to = _view.Node(edge.To);
                if (from == null || to == null) continue;
                var a = Centre(from);
                var b = Centre(to);
                if (edge.Travelled)
                {
                    Dots(painter, a, b, TrailColor);
                    continue;
                }
                var way = from.Current && reachable.Contains(edge.To);
                painter.strokeColor = way ? WayColor : EdgeColor;
                painter.lineWidth = EdgeWidth;
                painter.BeginPath();
                painter.MoveTo(a);
                painter.LineTo(b);
                painter.Stroke();
            }
        }

        /// <summary>The travelled trail: gold dots along the edge (04 §0 non-colour cue: dotted).</summary>
        private void Dots(Painter2D painter, Vector2 a, Vector2 b, Color color)
        {
            var length = Vector2.Distance(a, b);
            var spacing = Math.Max(1f, DotSpacing);
            var count = Math.Max(1, (int)(length / spacing));
            painter.fillColor = color;
            for (var i = 0; i <= count; i++)
            {
                var p = Vector2.Lerp(a, b, (float)i / count);
                painter.BeginPath();
                painter.Arc(p, EdgeWidth, 0f, (float)UiMath.FullTurnDegrees);
                painter.Fill();
            }
        }
    }
}
