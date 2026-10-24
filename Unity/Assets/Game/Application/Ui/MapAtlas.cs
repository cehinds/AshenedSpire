using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>ui/components.json climb.atlas: the worlds the painted atlas under the act map is drawn from, its tile grid and how a light reveals it (D-900).</summary>
    public sealed class AtlasDefaults
    {
        public IReadOnlyList<string> Worlds = Array.Empty<string>();
        public string WorldSalt = string.Empty;
        public uint HashOffset;
        public uint HashPrime;

        /// <summary>The detail level of the tiles used (mapdetail.{map}.l{level}.t{x}x{y}).</summary>
        public int Level;

        public int TileCols;
        public int TileRows;

        /// <summary>The radius of a node's light in reference px, and the share of it that is full strength (the rest fades out).</summary>
        public double RevealRadius;
        public double RevealCore;

        /// <summary>The distance between the vertices of the reveal mesh, in reference px.</summary>
        public double GridStep;

        public static AtlasDefaults From(JObject json)
        {
            if (json == null) return new AtlasDefaults();
            var hash = json[AtlasKeys.Hash] as JObject;
            return new AtlasDefaults
            {
                Worlds = ScreenRegistry.Strings(json[AtlasKeys.Worlds]),
                WorldSalt = (string)json[AtlasKeys.WorldSalt] ?? string.Empty,
                HashOffset = unchecked((uint)((long?)hash?[AtlasKeys.Offset] ?? 0L)),
                HashPrime = unchecked((uint)((long?)hash?[AtlasKeys.Prime] ?? 0L)),
                Level = (int?)json[AtlasKeys.Level] ?? 0,
                TileCols = (int?)json[AtlasKeys.TileCols] ?? 0,
                TileRows = (int?)json[AtlasKeys.TileRows] ?? 0,
                RevealRadius = (double?)json[AtlasKeys.RevealRadius] ?? 0d,
                RevealCore = (double?)json[AtlasKeys.RevealCore] ?? 0d,
                GridStep = (double?)json[AtlasKeys.GridStep] ?? 0d,
            };
        }
    }

    /// <summary>A node's light on the atlas: where it stands on the board, in reference px.</summary>
    public struct AtlasLight
    {
        public AtlasLight(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X;
        public double Y;
    }

    /// <summary>One vertex of the reveal mesh: where it sits on the board, which texel of its tile it reads (v up, as a texture) and how much of the painting shows through it.</summary>
    public struct AtlasVertex
    {
        public float X;
        public float Y;
        public float U;
        public float V;
        public float Alpha;
    }

    /// <summary>The part of one atlas tile that lies on the board, as a grid of vertices and the triangles between them.</summary>
    public sealed class AtlasPatch
    {
        public int TileX;
        public int TileY;
        public readonly List<AtlasVertex> Vertices = new List<AtlasVertex>();
        public readonly List<int> Indices = new List<int>();
    }

    /// <summary>
    /// The painted atlas of the act map (web mapTerrainHtml, mapDetail.js; D-900): one world per run, chosen by the seed, painted over the board as it covers it
    /// (the web's `xMidYMid slice`), and revealed where the fog's lights are. A light is the web's radial gradient (white up to the core share of its radius, then
    /// fading to nothing); the lights add as the web's mask circles do. Engine-free: the screen turns the patches into a mesh.
    /// </summary>
    public static class MapAtlas
    {
        /// <summary>The 32-bit FNV-1a hash of a text over its UTF-16 units (the web's decorative hash: Math.imul(value ^ unit, prime) >>> 0).</summary>
        public static uint Hash(AtlasDefaults d, string text)
        {
            var h = d.HashOffset;
            unchecked
            {
                foreach (var unit in text ?? string.Empty) h = (h ^ unit) * d.HashPrime;
            }
            return h;
        }

        /// <summary>The world a seed paints (worldMapForRun: one coherent world per seed, whatever the act), or null when there is none.</summary>
        public static string World(AtlasDefaults d, string seedText)
        {
            if (d.Worlds.Count == 0) return null;
            return d.Worlds[(int)(Hash(d, (seedText ?? string.Empty) + d.WorldSalt) % (uint)d.Worlds.Count)];
        }

        /// <summary>The registry id of one tile of a world (art lookup mapDetail), or null when it is not imported.</summary>
        public static string TileId(ArtLookup art, AtlasDefaults d, string world, int x, int y) =>
            art?.Find(ArtKinds.MapDetail, new StringArgs()
                .Add(ArtPlaceholders.Map, world).Add(ArtPlaceholders.Level, d.Level).Add(ArtPlaceholders.X, x).Add(ArtPlaceholders.Y, y));

        /// <summary>How much of the painting the lights show at a point: 1 minus what no light covers, a light being full inside the core and fading linearly to nothing at its radius.</summary>
        public static double Reveal(AtlasDefaults d, IReadOnlyList<AtlasLight> lights, double x, double y, double scale = 1d)
        {
            var uncovered = 1d;
            var radius = d.RevealRadius * scale;
            var core = radius * d.RevealCore;
            var fade = radius - core;
            foreach (var light in lights)
            {
                var distance = Math.Sqrt((x - light.X) * (x - light.X) + (y - light.Y) * (y - light.Y));
                var strength = distance <= core ? 1d : distance >= radius || fade <= 0d ? 0d : (radius - distance) / fade;
                uncovered *= 1d - strength;
            }
            return 1d - uncovered;
        }

        /// <summary>
        /// The patches that paint a world over a board of <paramref name="canvasW"/> x <paramref name="canvasH"/> reference px: the painting (the tiles side by side, the widths of its
        /// columns and the heights of its rows in texels) is scaled to cover the board and centred, each tile is cut to the board and gridded, and each vertex carries the reveal
        /// of the lights there (everywhere when <paramref name="revealAll"/>). Tile (x, y) counts from the painting's top left.
        /// </summary>
        public static IReadOnlyList<AtlasPatch> Patches(AtlasDefaults d, double canvasW, double canvasH, IReadOnlyList<double> columnWidths, IReadOnlyList<double> rowHeights, IReadOnlyList<AtlasLight> lights, bool revealAll, double lightScale = 1d)
        {
            var patches = new List<AtlasPatch>();
            var paintingW = columnWidths.Sum();
            var paintingH = rowHeights.Sum();
            if (paintingW <= 0d || paintingH <= 0d || canvasW <= 0d || canvasH <= 0d || d.GridStep <= 0d) return patches;
            var scale = Math.Max(canvasW / paintingW, canvasH / paintingH);
            var originX = (canvasW - paintingW * scale) * UiMath.Half;
            var originY = (canvasH - paintingH * scale) * UiMath.Half;
            var tileTop = originY;
            for (var y = 0; y < rowHeights.Count; y++)
            {
                var tileLeft = originX;
                var tileH = rowHeights[y] * scale;
                for (var x = 0; x < columnWidths.Count; x++)
                {
                    var tileW = columnWidths[x] * scale;
                    var patch = Patch(d, x, y, tileLeft, tileTop, tileW, tileH, canvasW, canvasH, lights, revealAll, lightScale);
                    if (patch != null) patches.Add(patch);
                    tileLeft += tileW;
                }
                tileTop += tileH;
            }
            return patches;
        }

        private static AtlasPatch Patch(AtlasDefaults d, int tileX, int tileY, double left, double top, double width, double height, double canvasW, double canvasH, IReadOnlyList<AtlasLight> lights, bool revealAll, double lightScale)
        {
            var x0 = Math.Max(0d, left);
            var y0 = Math.Max(0d, top);
            var x1 = Math.Min(canvasW, left + width);
            var y1 = Math.Min(canvasH, top + height);
            if (x1 <= x0 || y1 <= y0) return null;
            var nx = Math.Max(1, (int)Math.Ceiling((x1 - x0) / d.GridStep));
            var ny = Math.Max(1, (int)Math.Ceiling((y1 - y0) / d.GridStep));
            var patch = new AtlasPatch { TileX = tileX, TileY = tileY };
            for (var j = 0; j <= ny; j++)
            {
                var y = y0 + (y1 - y0) * j / ny;
                for (var i = 0; i <= nx; i++)
                {
                    var x = x0 + (x1 - x0) * i / nx;
                    patch.Vertices.Add(new AtlasVertex
                    {
                        X = (float)x,
                        Y = (float)y,
                        U = (float)((x - left) / width),
                        V = (float)(1d - (y - top) / height),
                        Alpha = (float)(revealAll ? 1d : Reveal(d, lights, x, y, lightScale)),
                    });
                }
            }
            var stride = nx + 1;
            for (var j = 0; j < ny; j++)
            {
                for (var i = 0; i < nx; i++)
                {
                    var a = j * stride + i;
                    var b = a + 1;
                    var c = a + stride;
                    var e = c + 1;
                    // Clockwise on the screen (y grows downward): top left, top right, bottom left; then top right, bottom right, bottom left.
                    patch.Indices.AddRange(new[] { a, b, c, b, e, c });
                }
            }
            return patch;
        }
    }
}
