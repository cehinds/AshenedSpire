using System.IO;
using System.Linq;
using Ashen.App.Settings;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// P-5 (D-1590): the Rendering quality row. resolvePerformanceMode and resolveCombatPacing (src/ui/performance.js) are the three-way choice and the pace Auto takes; the row is the web's
    /// (label, note, choices, default) in the place the web puts it, straight after Combat pacing. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class RenderingQualityTests
    {
        [TestCase("lite", false, true)]
        [TestCase("lite", true, true)]
        [TestCase("full", false, false)]
        [TestCase("full", true, false)]
        [TestCase("auto", false, false)]
        [TestCase("auto", true, true)]
        [TestCase(null, true, true)]
        [TestCase("garbage", false, false)]
        public void TheModeIsTheWebs(string mode, bool coarse, bool lite) => Assert.That(RenderingQuality.IsLite(mode, coarse), Is.EqualTo(lite));

        [TestCase("slow", false, "slow")]
        [TestCase("normal", true, "normal")]
        [TestCase("fast", false, "fast")]
        [TestCase("instant", true, "instant")]
        [TestCase("auto", true, "fast")]
        [TestCase("auto", false, "normal")]
        [TestCase(null, true, "fast")]
        public void AutoPacingFollowsTheQuality(string anim, bool lite, string want) => Assert.That(RenderingQuality.Pacing(anim, lite), Is.EqualTo(want));

        [Test]
        public void TheRowIsTheWebsAndSitsAfterCombatPacing()
        {
            var rows = ((JObject)OracleDocuments.Read(Path.Combine(TestContent.OracleRoot, "core2", "settings", "page.json"))["general"]).Properties()
                .SelectMany(p => (JArray)p.Value).OfType<JObject>().SelectMany(g => (JArray)g["topics"]).OfType<JObject>().SelectMany(t => (JArray)t["rows"]).OfType<JObject>().ToList();
            var web = rows.Single(r => r.Value<string>("key") == "performanceMode");
            var defs = SettingsDefs.From(Core2HostParityTests.Content.Snapshot.Content);
            var control = defs.Find("performanceMode");
            Assert.That(control, Is.Not.Null);
            Assert.That(control.Options, Is.EqualTo(web["choices"].Select(c => (string)c).ToList()));
            Assert.That(control.Def.ToString(), Is.EqualTo(web.Value<string>("def")));
            var keys = defs.Controls.Where(c => c.Category == control.Category).Select(c => c.Key).ToList();
            Assert.That(keys.IndexOf("performanceMode"), Is.EqualTo(keys.IndexOf("animSpeed") + 1), "straight after Combat pacing, as the web lists it");
        }
    }
}
