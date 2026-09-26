using System;
using System.Linq;
using Ashen.App.Audio;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>US-16.2 procedural SFX core (suite: Presentation — engine-free half).</summary>
    [TestFixture, Category("Presentation")]
    public class SynthRendererTests
    {
        private static SynthParams Params => SynthParams.From(TestContent.ContentJson(SynthKeys.SynthFile));
        private static JObject Recipes => (JObject)TestContent.ContentJson(SynthKeys.SfxFile)[SynthKeys.Recipes];
        private static SynthRenderer Renderer() => new SynthRenderer(Params, Recipes);

        [TestCase("hit", "hit")]
        [TestCase("procBurst_frost", "procBurst_frost")]
        [TestCase("procBurst_venom", "procBurst")]
        [TestCase("noSuchSound", "default")]
        [TestCase(null, "default")]
        public void ResolutionIsExactThenFamilyThenDefault(string id, string expected) => Assert.That(Renderer().Resolve(id), Is.EqualTo(expected));

        [Test]
        public void EveryRecipeRendersAudibleFiniteBoundedSamples()
        {
            var r = Renderer();
            foreach (var id in Recipes.Properties().Select(p => p.Name))
            {
                var pcm = r.Render(id);
                Assert.That(pcm.Length, Is.GreaterThan(0), id);
                Assert.That(pcm.All(s => !float.IsNaN(s) && !float.IsInfinity(s)), Is.True, id);
                Assert.That(pcm.Max(Math.Abs), Is.GreaterThan(0.001f).And.LessThanOrEqualTo(1.5f), id + " peak");
            }
        }

        [Test]
        public void LengthCoversTheLongestLayer()
        {
            var layers = (JArray)Recipes["hit"];
            var expected = layers.Max(l => ((double?)l["t0"] ?? 0) + (double)l["dur"]);
            Assert.That(Renderer().Render("hit").Length, Is.EqualTo((int)Math.Ceiling(expected * Params.SampleRate)));
        }

        [Test]
        public void RenderingIsDeterministic()
        {
            Assert.That(Renderer().Render("procBurst"), Is.EqualTo(Renderer().Render("procBurst")));
        }

        [Test]
        public void MissingIdIsNeverSilent()
        {
            Assert.That(Renderer().Render("definitelyMissing").Max(Math.Abs), Is.GreaterThan(0.001f));
        }

        [Test]
        public void ContextsMapEveryBedOrSilenceAndStingersResolve()
        {
            var contexts = TestContent.ContentJson(SynthKeys.ContextsFile);
            var beds = (JObject)TestContent.ContentJson(SynthKeys.MusicFile)[SynthKeys.Beds];
            foreach (var c in ((JObject)contexts[SynthKeys.Contexts]).Properties())
            {
                var bed = (string)c.Value[SynthKeys.Bed];
                Assert.That(bed == Params.SilenceWord || beds[bed] != null, Is.True, c.Name + " → " + bed);
            }
            foreach (var s in ((JObject)contexts[SynthKeys.Stingers]).Properties())
                Assert.That(Renderer().Resolve((string)s.Value["recipe"]), Is.EqualTo((string)s.Value["recipe"]), s.Name);
        }
    }
}
