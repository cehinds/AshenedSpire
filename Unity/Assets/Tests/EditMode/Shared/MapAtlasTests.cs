using System.Collections.Generic;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Content;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// VP-10 (D-900, D-901): the painted atlas under the act map and the fog that reveals it, held without the editor. The world a seed paints is the web's FNV hash of "{seed}:world", a light is
    /// the web's radial gradient (full to 56%, then fading), the painting covers the board as the web's `xMidYMid slice` does, and the lit set is mapknowledge.js litNodes. Suite: Presentation.
    /// </summary>
    [TestFixture, Category("Presentation")]
    public class MapAtlasTests
    {
        private static UiData _ui;
        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);
        private static AtlasDefaults Atlas => Ui.Components.Climb.Atlas;

        // ------------------------------------------------------------------ the world

        [TestCase("ASHEN1", "the-fivefold-frontier-world")]
        [TestCase("ABCD-EFGH", "the-fivefold-frontier-world")]
        [TestCase("", "the-shattered-gulf-world")]
        [TestCase("Zz9", "the-fractured-realm-world")]
        [TestCase("héllo", "the-fractured-realm-world")]
        public void ASeedPaintsTheWorldTheWebsHashChooses(string seed, string world)
        {
            // Expected values are the web's worldMapForRun: Math.imul(value ^ unit, 16777619) >>> 0 over "{seed}:world", modulo the three maps.
            Assert.That(MapAtlas.World(Atlas, seed), Is.EqualTo(world));
        }

        [Test]
        public void EveryTileOfEveryWorldIsInTheArtRegistry()
        {
            var art = Ui.Art;
            foreach (var world in Atlas.Worlds)
                for (var y = 0; y < Atlas.TileRows; y++)
                    for (var x = 0; x < Atlas.TileCols; x++)
                        Assert.That(MapAtlas.TileId(art, Atlas, world, x, y), Is.Not.Null, world + " tile " + x + "x" + y);
        }

        // ------------------------------------------------------------------ the lights

        [Test]
        public void ALightIsFullInsideItsCoreFadesLinearlyAndEndsAtItsRadius()
        {
            var lights = new[] { new AtlasLight(100, 100) };
            var r = Atlas.RevealRadius;
            var core = r * Atlas.RevealCore;
            Assert.That(MapAtlas.Reveal(Atlas, lights, 100, 100), Is.EqualTo(1d));
            Assert.That(MapAtlas.Reveal(Atlas, lights, 100 + core, 100), Is.EqualTo(1d).Within(1e-9));
            Assert.That(MapAtlas.Reveal(Atlas, lights, 100 + (core + r) / 2, 100), Is.EqualTo(0.5d).Within(1e-9), "halfway through the fade");
            Assert.That(MapAtlas.Reveal(Atlas, lights, 100 + r, 100), Is.EqualTo(0d).Within(1e-9));
            Assert.That(MapAtlas.Reveal(Atlas, lights, 100 + r * 2, 100), Is.EqualTo(0d));
            Assert.That(MapAtlas.Reveal(Atlas, new AtlasLight[0], 100, 100), Is.EqualTo(0d), "no light, no painting");
        }

        [Test]
        public void TwoLightsAddAsTheWebsMaskCirclesDo()
        {
            var r = Atlas.RevealRadius;
            var core = r * Atlas.RevealCore;
            var middle = (core + r) / 2;
            var lights = new[] { new AtlasLight(0, 0), new AtlasLight(middle * 2, 0) };
            // Each light shows half there; the two together show 1 - 0.5 * 0.5.
            Assert.That(MapAtlas.Reveal(Atlas, lights, middle, 0), Is.EqualTo(0.75d).Within(1e-9));
        }

        // ------------------------------------------------------------------ the painting over the board

        [Test]
        public void ThePaintingCoversTheBoardAndIsCutToIt()
        {
            // A 683 x 1024 painting in 2 x 2 tiles (512 + 171 wide, 512 + 512 tall) over a board 574 wide and 1700 tall: the height binds (scale 1700/1024), the width is cropped.
            var patches = MapAtlas.Patches(Atlas, 574, 1700, new double[] { 512, 171 }, new double[] { 512, 512 }, new AtlasLight[0], true);
            Assert.That(patches.Select(p => p.TileX + "x" + p.TileY), Is.EquivalentTo(new[] { "0x0", "0x1", "1x0", "1x1" }));
            var all = patches.SelectMany(p => p.Vertices).ToList();
            Assert.That(all.Min(v => v.X), Is.EqualTo(0f).Within(1e-3), "the board is covered from its left edge");
            Assert.That(all.Max(v => v.X), Is.EqualTo(574f).Within(1e-3));
            Assert.That(all.Min(v => v.Y), Is.EqualTo(0f).Within(1e-3));
            Assert.That(all.Max(v => v.Y), Is.EqualTo(1700f).Within(1e-3));
            Assert.That(all.All(v => v.Alpha == 1f), Is.True, "revealed everywhere");
            Assert.That(all.All(v => v.U >= -1e-4f && v.U <= 1f + 1e-4f && v.V >= -1e-4f && v.V <= 1f + 1e-4f), Is.True, "every vertex reads inside its tile");
            foreach (var patch in patches)
            {
                Assert.That(patch.Indices.Count % 3, Is.EqualTo(0));
                Assert.That(patch.Indices.All(i => i >= 0 && i < patch.Vertices.Count), Is.True);
            }
        }

        [Test]
        public void AFoggedBoardRevealsOnlyAroundItsLights()
        {
            var lights = new[] { new AtlasLight(287, 850) };
            var patches = MapAtlas.Patches(Atlas, 574, 1700, new double[] { 512, 171 }, new double[] { 512, 512 }, lights, false);
            var all = patches.SelectMany(p => p.Vertices).ToList();
            Assert.That(all.Any(v => v.Alpha == 1f), Is.True);
            Assert.That(all.Any(v => v.Alpha == 0f), Is.True);
            var far = all.Where(v => (v.X - 287) * (v.X - 287) + (v.Y - 850) * (v.Y - 850) > Atlas.RevealRadius * Atlas.RevealRadius);
            Assert.That(far.All(v => v.Alpha == 0f), Is.True, "nothing shows beyond a light's radius");
        }

        [Test]
        public void ATileTextureReadsTopDownAsAnImageIsStored()
        {
            // The top of the painting is the top of tile row 0; a texture's v runs upward, so the top edge of a tile reads v = 1 and its bottom edge v = 0.
            var patches = MapAtlas.Patches(Atlas, 100, 100, new double[] { 100 }, new double[] { 100 }, new AtlasLight[0], true);
            var top = patches[0].Vertices.OrderBy(v => v.Y).First();
            var bottom = patches[0].Vertices.OrderByDescending(v => v.Y).First();
            Assert.That(top.V, Is.EqualTo(1f).Within(1e-4));
            Assert.That(bottom.V, Is.EqualTo(0f).Within(1e-4));
        }

        // ------------------------------------------------------------------ the fog's light

        private static JObject Graph() => JObject.Parse(@"{
            'startIds': ['a1', 'a2'], 'bossId': 'boss',
            'nodes': {
                'a1': { 'type': 'monster', 'floor': 1, 'col': 0, 'next': ['b1', 'b2'] },
                'a2': { 'type': 'monster', 'floor': 1, 'col': 1, 'next': ['b2'] },
                'b1': { 'type': 'monster', 'floor': 2, 'col': 0, 'next': ['c1'] },
                'b2': { 'type': 'shrine', 'floor': 2, 'col': 1, 'next': ['c1', 'c2'] },
                'c1': { 'type': 'monster', 'floor': 3, 'col': 0, 'next': ['d1'] },
                'c2': { 'type': 'monster', 'floor': 3, 'col': 1, 'next': ['d1'] },
                'd1': { 'type': 'shrine', 'floor': 4, 'col': 0, 'next': ['boss'] },
                'boss': { 'type': 'boss', 'floor': 5, 'col': 0, 'next': [] }
            } }".Replace('\'', '"'));

        [Test]
        public void AtTheEntranceTheDoorsAndTheEndAreLit()
        {
            Assert.That(MapFog.Lit(Graph(), new string[0], null), Is.EquivalentTo(new[] { "a1", "a2", "boss" }));
        }

        [Test]
        public void StandingOnANodeLightsItAndTheSplitInFrontOfIt()
        {
            Assert.That(MapFog.Lit(Graph(), new[] { "a1" }, "a1"), Is.EquivalentTo(new[] { "a1", "a2", "b1", "b2", "boss" }));
        }

        [Test]
        public void TheTrailStaysLitAndAShrineStoodOnLightsTheNextOneAhead()
        {
            var lit = MapFog.Lit(Graph(), new[] { "a1", "b2" }, "b2");
            Assert.That(lit, Is.EquivalentTo(new[] { "a1", "a2", "b1", "b2", "c1", "c2", "d1", "boss" }), "the cone behind the frontier and the shrine two steps on");
            var later = MapFog.Lit(Graph(), new[] { "a1", "b2", "c2" }, "c2");
            Assert.That(later, Is.SupersetOf(lit), "fog never closes behind the run");
        }

        [Test]
        public void TheNearestShrineAheadIsTheSmallerIdAtATie()
        {
            var graph = JObject.Parse(@"{ 'nodes': {
                'x': { 'type': 'monster', 'next': ['s2', 's1'] },
                's1': { 'type': 'shrine', 'next': [] }, 's2': { 'type': 'shrine', 'next': [] } } }".Replace('\'', '"'));
            Assert.That(MapFog.NearestShrine((JObject)graph["nodes"], new[] { "x" }), Is.EqualTo("s1"));
            Assert.That(MapFog.NearestShrine((JObject)graph["nodes"], new[] { "s1" }), Is.Null, "a source is never its own answer");
        }
    }
}
