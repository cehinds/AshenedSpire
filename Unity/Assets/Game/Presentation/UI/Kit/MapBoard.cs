using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>
    /// The act map's board (docs/design/04 W-06; web ui/components/mapboard.js, D-902): a parchment ground, the painted atlas of the act's world revealed around the nodes the fog lights,
    /// the walked route as a gold line over a dark outline, and one disc per drawn node (its kind's icon, or the "?" of an unknown place in a dashed ring; the walked ones faded, the one the
    /// run stands on filled, the reachable ones rimmed and haloed). Floors run up from the foot to the boss at the head; the lanes sit on a canvas centred in the board. Only reachable nodes
    /// take focus; any node can be clicked and the screen decides (select, enter, refuse). Node buttons are added in floor-then-column order, so keyboard and pad walk the reachable row in order.
    /// </summary>
    [UxmlElement]
    public partial class MapBoard : VisualElement
    {
        private readonly Dictionary<string, Button> _nodes = new Dictionary<string, Button>(StringComparer.Ordinal);
        private ActMapViewState _view;
        private string _selected;
        private float _height;
        private IReadOnlyList<AtlasPatch> _patches = new List<AtlasPatch>();
        private readonly List<Texture2D> _patchTextures = new List<Texture2D>();

        public MapBoard()
        {
            AddToClassList(nameof(MapBoard).ToLowerInvariant());
            generateVisualContent += Draw;
            RegisterCallback<GeometryChangedEvent>(_ => Place());
        }

        /// <summary>The geometry in reference px (the zoom already applied): the floor pitch, the lane pitch, the distance from the outer lanes to the canvas edge and the room above the boss row.</summary>
        public float RowHeight { get; set; }

        public float ColPitch { get; set; }
        public float PadX { get; set; }
        public float PadTop { get; set; }

        /// <summary>The zoom the geometry was scaled by (the atlas's lights scale with it).</summary>
        public float Zoom { get; set; } = 1f;

        public Color RouteColor { get; set; } = Color.yellow;
        public Color RouteOutlineColor { get; set; } = Color.black;
        public float EdgeWidth { get; set; } = 1f;

        /// <summary>The lit walk to the nearest shrine (the player's Shrine path glow): its colour and line width.</summary>
        public Color LaneColor { get; set; } = Color.white;
        public float LaneWidth { get; set; } = 1f;
        public float OutlineWidth { get; set; } = 1f;

        /// <summary>The painted atlas: its numbers, and where a tile's texture comes from (tile x, y; null when it is not imported).</summary>
        public AtlasDefaults Atlas { get; set; }

        public Func<int, int, Texture2D> TileTexture { get; set; }

        /// <summary>The icon texture of an art name, and the colour of a tint token (the screen owns the art catalog and the palette).</summary>
        public Func<string, Texture2D> IconTexture { get; set; }

        public Func<string, Color> TintColor { get; set; }

        /// <summary>A node was clicked or submitted (its id and its button).</summary>
        public event Action<string, VisualElement> NodePicked;

        public IReadOnlyDictionary<string, Button> Nodes => _nodes;

        public Button Node(string id) => id != null && _nodes.TryGetValue(id, out var b) ? b : null;

        /// <summary>The width of the canvas the lanes and the painting sit on.</summary>
        public float CanvasWidth => PadX + PadX + Math.Max(0, (_view?.Columns ?? 1) - 1) * ColPitch;

        private float CanvasLeft => float.IsNaN(contentRect.width) ? 0f : Math.Max(0f, (contentRect.width - CanvasWidth) * (float)UiMath.Half);

        public void Bind(ActMapViewState view)
        {
            _view = view;
            Clear();
            _nodes.Clear();
            _patches = new List<AtlasPatch>();
            _patchTextures.Clear();
            if (view == null) return;
            _height = RowHeight * (view.Floors + 1) + PadTop;
            style.height = _height;
            style.minWidth = CanvasWidth;
            foreach (var node in view.Nodes.Where(n => n.Drawn).OrderBy(n => n.Floor).ThenBy(n => n.Col)) AddNode(view, node);
            Select(_selected != null && _nodes.ContainsKey(_selected) ? _selected : null);
            BuildAtlas(view);
            Place();
        }

        private void AddNode(ActMapViewState view, MapNodeView node)
        {
            var button = new Button { name = node.Id, focusable = node.Reachable, tooltip = node.AccessibleName };
            button.AddToClassList(UiClasses.MapNode);
            button.AddToClassList(UiClasses.Touch);
            button.AddToClassList(UiClasses.MapNodeKindPrefix + node.Kind);
            button.EnableInClassList(UiClasses.MapNodeReachable, node.Reachable);
            button.EnableInClassList(UiClasses.MapNodeCurrent, node.Current);
            button.EnableInClassList(UiClasses.MapNodeTravelled, node.Travelled && !node.Current);
            button.EnableInClassList(UiClasses.MapNodeDimmed, node.Dimmed);
            button.EnableInClassList(UiClasses.MapNodeLane, node.Lane);
            button.EnableInClassList(UiClasses.MapNodeHidden, node.Hidden);
            // An unknown place under the fog wears the dashed ring (web map.css .k-placed).
            var dashed = view.Fog && node.Kind == MapKeys.Event;
            button.EnableInClassList(MapClasses.NodeFogged, dashed);
            if (node.Reachable)
            {
                var halo = new VisualElement { pickingMode = PickingMode.Ignore };
                halo.AddToClassList(MapClasses.NodeHalo);
                button.Add(halo);
            }
            if (dashed)
            {
                var dash = new VisualElement { pickingMode = PickingMode.Ignore };
                dash.AddToClassList(MapClasses.NodeDash);
                button.Add(dash);
            }
            var icon = string.IsNullOrEmpty(node.IconName) ? null : IconTexture?.Invoke(node.IconName);
            if (icon != null)
            {
                var face = new VisualElement { pickingMode = PickingMode.Ignore };
                face.AddToClassList(MapClasses.NodeIcon);
                face.style.backgroundImage = new StyleBackground(icon);
                if (node.TintToken != null && TintColor != null) face.style.unityBackgroundImageTintColor = TintColor(node.TintToken);
                button.Add(face);
            }
            else
            {
                var mark = new LocLabel(node.GlyphKey) { pickingMode = PickingMode.Ignore };
                mark.AddToClassList(MapClasses.NodeMark);
                button.Add(mark);
            }
            var centred = -(float)(UiMath.Half * UiMath.Percent);
            button.style.translate = new Translate(Length.Percent(centred), Length.Percent(centred));
            var id = node.Id;
            button.clicked += () => NodePicked?.Invoke(id, button);
            Add(button);
            _nodes[node.Id] = button;
        }

        /// <summary>Marks one node selected (the tray's node), or none.</summary>
        public void Select(string id)
        {
            _selected = id;
            foreach (var p in _nodes) p.Value.EnableInClassList(UiClasses.MapNodeSelected, p.Key == id);
        }

        /// <summary>The centre of a node in board coordinates (floor 1 one pitch above the foot).</summary>
        public Vector2 Centre(MapNodeView node) =>
            new Vector2(CanvasLeft + PadX + (float)node.Col * ColPitch, _height - node.Floor * RowHeight);

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

        // ------------------------------------------------------------------ the painted atlas

        /// <summary>Cuts the world's painting to the canvas and weights it by the fog's lights: one patch per tile that lies on the board.</summary>
        private void BuildAtlas(ActMapViewState view)
        {
            if (Atlas == null || TileTexture == null || view.World == null || Atlas.TileCols <= 0 || Atlas.TileRows <= 0) return;
            var tiles = new Texture2D[Atlas.TileCols, Atlas.TileRows];
            var widths = new List<double>();
            var heights = new List<double>();
            for (var y = 0; y < Atlas.TileRows; y++)
                for (var x = 0; x < Atlas.TileCols; x++)
                {
                    tiles[x, y] = TileTexture(x, y);
                    if (tiles[x, y] == null) return;
                }
            for (var x = 0; x < Atlas.TileCols; x++) widths.Add(tiles[x, 0].width);
            for (var y = 0; y < Atlas.TileRows; y++) heights.Add(tiles[0, y].height);
            var lights = view.Nodes.Where(n => n.Drawn).Select(n =>
            {
                var c = Centre(n);
                return new AtlasLight(c.x - CanvasLeft, c.y);
            }).ToList();
            _patches = MapAtlas.Patches(Atlas, CanvasWidth, _height, widths, heights, lights, !view.Fog, Zoom);
            foreach (var patch in _patches) _patchTextures.Add(tiles[patch.TileX, patch.TileY]);
        }

        private void Draw(MeshGenerationContext mgc)
        {
            if (_view == null || float.IsNaN(contentRect.width)) return;
            var left = CanvasLeft;
            for (var i = 0; i < _patches.Count; i++)
            {
                var patch = _patches[i];
                var mesh = mgc.Allocate(patch.Vertices.Count, patch.Indices.Count, _patchTextures[i]);
                foreach (var v in patch.Vertices)
                    mesh.SetNextVertex(new Vertex { position = new Vector3(left + v.X, v.Y, Vertex.nearZ), uv = new Vector2(v.U, v.V), tint = new Color(1f, 1f, 1f, v.Alpha) });
                foreach (var index in patch.Indices) mesh.SetNextIndex((ushort)index);
            }
            var painter = mgc.painter2D;
            var route = _view.Edges.Where(e => e.Travelled && e.Drawn).ToList();
            // The web draws every outline, then every route over them.
            Strokes(painter, route, RouteOutlineColor, OutlineWidth);
            Strokes(painter, route, RouteColor, EdgeWidth);
            // The shrine lane glows over the walked route where both its ends are drawn (mapboard.js laneEdge).
            Strokes(painter, _view.Edges.Where(e => e.Lane && e.Drawn).ToList(), LaneColor, LaneWidth);
        }

        private void Strokes(Painter2D painter, List<MapEdgeView> edges, Color color, float width)
        {
            painter.strokeColor = color;
            painter.lineWidth = width;
            foreach (var edge in edges)
            {
                var from = _view.Node(edge.From);
                var to = _view.Node(edge.To);
                if (from == null || to == null) continue;
                painter.BeginPath();
                painter.MoveTo(Centre(from));
                painter.LineTo(Centre(to));
                painter.Stroke();
            }
        }
    }
}
