using System.Collections.Generic;
using Ashen.Generated;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Kit
{
    /// <summary>One straight line of the board's lattice, in the grid element's own pixels.</summary>
    public struct GridLine
    {
        public Vector2 From;
        public Vector2 To;
    }

    /// <summary>
    /// The perspective grid of the combat board (P-4, D-1540..): the sheared lattice of cells the fighters stand on (the web's formation-grid, one uniform unfilled outline per cell:
    /// src/ui/components/battlefieldStage.js and styles/combat.css). UI Toolkit draws no skewed boxes, so the cells' edges are one mesh of thin quads in the grid's colour, redrawn when the formation changes.
    /// </summary>
    public sealed class BattlefieldGrid : VisualElement
    {
        private readonly List<GridLine> _lines = new List<GridLine>();
        private Color _color = Color.white;
        private float _width = 1f;

        public BattlefieldGrid()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = 0;
            style.top = 0;
            style.right = 0;
            style.bottom = 0;
            generateVisualContent += Draw;
        }

        public int LineCount => _lines.Count;

        /// <summary>Replaces the lattice: its lines, their colour and their width.</summary>
        public void SetLines(IEnumerable<GridLine> lines, Color color, float width)
        {
            _lines.Clear();
            _lines.AddRange(lines);
            _color = color;
            _width = width;
            MarkDirtyRepaint();
        }

        private void Draw(MeshGenerationContext mgc)
        {
            if (_lines.Count == 0) return;
            var mesh = mgc.Allocate(_lines.Count * GridMesh.Corners, _lines.Count * GridMesh.Indices);
            var half = _width * (float)UiMath.Half;
            ushort at = 0;
            foreach (var line in _lines)
            {
                var along = line.To - line.From;
                if (along.sqrMagnitude <= 0f) along = Vector2.right;
                var across = new Vector2(-along.y, along.x).normalized * half;
                var a = line.From - across;
                var b = line.From + across;
                var c = line.To + across;
                var d = line.To - across;
                foreach (var corner in new[] { a, b, c, d }) mesh.SetNextVertex(new Vertex { position = new Vector3(corner.x, corner.y, Vertex.nearZ), tint = _color });
                mesh.SetNextIndex(at);
                mesh.SetNextIndex((ushort)(at + 1));
                mesh.SetNextIndex((ushort)(at + GridMesh.Second));
                mesh.SetNextIndex(at);
                mesh.SetNextIndex((ushort)(at + GridMesh.Second));
                mesh.SetNextIndex((ushort)(at + GridMesh.Third));
                at += GridMesh.Corners;
            }
        }
    }
}
