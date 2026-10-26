using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Ui;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// P-4 (D-1540..): the silhouette glow of the player's card stage. Tools/oracle-core2-aura.mjs runs the web's auraFilter on a matrix of frames, rests, paid resources and active clips as the stage
    /// calls it and reads the filter back with the renderer's own parser; CombatAuraData picks the same glows (radius, blur, colour, strength) and the same brightness. resourceAura is compared over
    /// costs, self-payments and receipts. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class CombatAuraTests
    {
        private const double Tolerance = 1e-9;
        private static UiData _ui;

        private static UiData Ui => _ui ?? (_ui = UiData.Load(TestContent.Source));

        private static JObject Oracle => OracleDocuments.Read(Path.Combine(TestContent.OracleRoot, "core2", "aura", "aura.json"));

        private static double? Spent(JToken receipt, string key) => receipt[key] == null ? (double?)null : receipt.Value<double>(key);

        [Test]
        public void TheStageGlowIsTheWebsForEveryFrameRestResourceAndClip()
        {
            var cases = (JArray)Oracle["stage"];
            Assert.That(cases.Count, Is.GreaterThan(3000));
            foreach (JObject c in cases)
            {
                var action = c.Value<string>("action");
                var draw = new StageDraw
                {
                    Pose = c.Value<string>("pose"), AuraAction = action,
                    AuraResources = ((JArray)c["resources"]).Select(r => r.Value<string>()).ToList(),
                };
                var layers = Ui.CombatAura.For(draw, c.Value<string>("rest"), false);
                var expected = (JArray)c["glows"];
                var label = $"{c["pose"]} {action} {c["rest"]} [{string.Join(",", draw.AuraResources)}]: {c["filter"]}";
                Assert.That(layers.Glows.Count, Is.EqualTo(expected.Count), label);
                Assert.That(layers.Brightness, Is.EqualTo(c.Value<double>("brightness")).Within(Tolerance), label);
                for (var i = 0; i < expected.Count; i++)
                {
                    var g = layers.Glows[i];
                    var e = expected[i];
                    Assert.That(g.Radius, Is.EqualTo(e.Value<double>("radius")).Within(Tolerance), label);
                    Assert.That(g.Blur, Is.EqualTo(e.Value<double>("blur")).Within(Tolerance), label);
                    Assert.That(g.Alpha, Is.EqualTo(e.Value<double>("alpha")).Within(Tolerance), label);
                    Assert.That(new[] { g.R, g.G, g.B }, Is.EqualTo(e["rgb"].Select(v => v.Value<double>()).ToArray()), label);
                }
            }
        }

        [Test]
        public void TheLightQualityAndTheDownedFigureDrawNoGlow()
        {
            var spell = new StageDraw { Pose = "cast-load", AuraAction = "spell", AuraResources = new List<string> { "mana" } };
            Assert.That(Ui.CombatAura.For(spell, "idle", false).None, Is.False);
            Assert.That(Ui.CombatAura.For(spell, "idle", true).None, Is.True, "lite renders no filters (alternativeCardStage.js filterFor)");
            Assert.That(Ui.CombatAura.For(new StageDraw { Pose = "defeated", Down = true, AuraAction = "spell" }, "defeated", false).None, Is.True);
            Assert.That(Ui.CombatAura.For(null, "idle", false).None, Is.True);
        }

        [Test]
        public void WhatACardPaidColoursTheGlowAsTheWebReadsIt()
        {
            var cases = (JArray)Oracle["costs"];
            Assert.That(cases.Count, Is.GreaterThan(300));
            foreach (JObject c in cases)
            {
                var card = (JObject)c["card"];
                var receipt = c["receipt"];
                var cost = CardStagePlans.CostOf(card);
                var got = CombatAuraData.Resources(cost, Spent(receipt, "staminaSpent"), Spent(receipt, "manaSpent"), Spent(receipt, "hpSpent"));
                Assert.That(got, Is.EqualTo(((JArray)c["resources"]).Select(r => r.Value<string>()).ToList()), $"{card} {receipt}");
            }
        }
    }
}
