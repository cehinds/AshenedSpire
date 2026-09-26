using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.App.Combat;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Generated;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// W-07 view-model (component loop step 4): built from content-built combat data at every step of golden
    /// combats, every hand card has its content name and a text whose bound tokens are resolved to the preview's
    /// numbers, playability equals the legality service, and every intent names a label string that exists.
    /// </summary>
    [TestFixture]
    public class CombatViewModelTests
    {
        private static CombatData _data;
        private static JObject _combatStrings;

        private static string CombatDir => Path.Combine(TestContent.OracleRoot, "combat");

        private static JObject ReadJson(string path)
        {
            using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { FloatParseHandling = FloatParseHandling.Double, DateParseHandling = DateParseHandling.None })
                return JObject.Load(reader);
        }

        private static CombatData Data
        {
            get
            {
                if (_data != null) return _data;
                var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
                _combatStrings = (JObject)snapshot.Content.Get(ContentFiles.StringsCombatEn);
                var registries = RuntimeRegistries.Build(snapshot.Content, (JObject)snapshot.Content.Get(ContentFiles.StringsEn));
                return _data = registries.ToCombatData((JObject)snapshot.Content.Get(ContentFiles.RulesMechanics), (JObject)snapshot.Content.Get(ContentFiles.RulesCombatEngine));
            }
        }

        [TestCase(0), TestCase(7), TestCase(21), TestCase(42)]
        public void TheViewIsDisplayReadyAtEveryStep(int index)
        {
            var log = ReadJson(Path.Combine(CombatDir, $"combat-{index:000}.json"));
            var session = CombatSession.Start(Data, (uint)log["seed"].Value<long>(), (JObject)log["create"]);
            foreach (var step in ((JArray)log["steps"]).OfType<JObject>().Prepend(null))
            {
                if (step != null) session.Execute(CombatCommand.FromJson((JObject)step["command"]));
                if (session.IsOver) break;
                var view = CombatViewModel.Build(session.State);
                Assert.That(view.Hand.Count, Is.EqualTo(session.State.Piles.Hand.Count));
                Assert.That(view.Player.Body.Name, Is.Not.Empty);
                foreach (var card in view.Hand)
                {
                    Assert.That(card.Name, Is.Not.Null.And.Not.Empty, card.CardId);
                    var tokens = CombatPreview.PreviewCard(session.State, card.InstanceId).Obj("tokens");
                    foreach (var p in tokens.Properties())
                        Assert.That(card.Text, Does.Not.Contain("{" + p.Name + "}"), $"{card.CardId}: bound token {p.Name} left unresolved in \"{card.Text}\"");
                    var legal = CombatLegality.CanPlay(session.State, card.InstanceId, card.Targets.FirstOrDefault()) == null;
                    Assert.That(card.Playable, Is.EqualTo(legal), card.CardId);
                }
                foreach (var enemy in view.Enemies.Where(e => e.Alive))
                {
                    Assert.That(enemy.Name, Is.Not.Empty, enemy.DefId);
                    if (enemy.Intent != null) Assert.That(_combatStrings[enemy.Intent.LabelKey], Is.Not.Null, enemy.Intent.LabelKey);
                }
            }
        }

        [Test]
        public void TokensResolveAndUnknownTokensStay()
        {
            var text = CombatViewModel.ResolveText("Deal {damage} damage {hits} times. {mystery}", JObject.Parse("{\"damage\":7,\"hits\":2}"));
            Assert.That(text, Is.EqualTo("Deal 7 damage 2 times. {mystery}"));
        }
    }
}
