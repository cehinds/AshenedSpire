using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-18 (D-400): Play in deck order. Tools/oracle-core2-deck.mjs opens Core 2 fights with <c>settings.playInDeckOrder</c> on
    /// (4 classes x 12 seeds): the opening draw pile is the deck as listed (Innate first) and takes no <c>shuffle</c> value, the order
    /// rides on the fight (<c>orderedDraw</c>), an empty draw pile takes the discard back in deck order, and the Core round refresh
    /// still shuffles. The bot plays cards, drains the draw pile and draws, so <see cref="Actions.ReturnDiscardInOrder"/> is reached; every
    /// step must equal the web's fight field for field (events, RNG counters, piles). Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2DeckOrderParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "deck");

        private static CoreCombatData _data;

        private static CoreCombatData Data
        {
            get
            {
                if (_data != null) return _data;
                var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
                var registries = Core2Registries.Load(snapshot.Content);
                return _data = registries.ToCoreCombatData(snapshot.Content, registries.ToCoreRunData(snapshot.Content));
            }
        }

        private static JObject Settings => new JObject { ["playInDeckOrder"] = true };

        public static IEnumerable<TestCaseData> Fights()
        {
            var file = Path.Combine(Dir, "ordered-fights.json");
            if (!File.Exists(file)) yield break;
            var fights = (JArray)((JObject)JsonContent.Parse(File.ReadAllText(file)))["fights"];
            for (var i = 0; i < fights.Count; i++)
                yield return new TestCaseData((JObject)fights[i]).SetName("Core2DeckOrderFight_" + i + "_" + fights[i].Value<string>("classId"));
        }

        private static JObject Unlocked => new JObject
        {
            ["gameProfileUnlocks"] = new JObject { [Data.Run.ProfileId] = new JObject { ["completedRunIds"] = new JArray(Enumerable.Range(0, Data.Run.Run.Classes.Count).Select(i => "r" + i)) } },
        };

        private static List<string> Ids(IEnumerable<JObject> cards) => cards.Select(c => c.Value<string>("instanceId")).ToList();

        [Test]
        public void TheOracleIsPresent() => Assert.That(Fights().Count(), Is.GreaterThanOrEqualTo(Data.Run.Run.Classes.Count * 3));

        [Test]
        public void TheDefaultIsTheDeckShuffled()
        {
            Assert.That(DeckRules.PlayInDeckOrder(Data.Run, null), Is.False);
            Assert.That(DeckRules.PlayInDeckOrder(Data.Run, new JObject { ["playInDeckOrder"] = true }), Is.True);
            Assert.That(DeckRules.PlayInDeckOrder(Data.Run, new JObject { ["playInDeckOrder"] = null }), Is.False, "a null setting reads the default");
        }

        [TestCaseSource(nameof(Fights))]
        public void TheFightMatchesTheWeb(JObject fight)
        {
            var seed = (uint)fight["seed"].Value<long>();
            var run = CoreRunRules.Create(Data.Run, seed, fight.Value<string>("classId"), CoreRunOptions.FromJson(null, Unlocked));
            var c = CoreRunCombat.Create(Data, new Rng(seed), run, fight.Value<string>("encounterId"), Settings);
            var label = fight.Value<string>("classId") + "/" + fight.Value<string>("encounterId") + "/" + seed;
            var start = (JObject)fight["start"];
            Core2CombatStartParityTests.Same(label + " start", start["state"], Core2CombatStartParityTests.Project(c));
            Assert.That(Core2CombatStartParityTests.Events(c, 0), Is.EqualTo(start["events"].Select(t => t.Value<string>()).ToList()), label + " start events");
            Assert.That(c.Fields["orderedDraw"]?["order"]?.Select(t => t.Value<string>()).ToList(), Is.EqualTo(start["order"].Select(t => t.Value<string>()).ToList()), label + " order");

            JToken want = start["state"].DeepClone();
            var n = 0;
            foreach (var step in ((JArray)fight["steps"]).OfType<JObject>())
            {
                var at = $"{label} step {n++} {step.Value<string>("op")}";
                var mark = c.EventLog.Count;
                switch (step.Value<string>("op"))
                {
                    case "play":
                        foreach (var pick in step["picks"].Select(t => t.Value<int>()))
                        {
                            var card = c.Piles.Hand[pick];
                            c.Piles.Hand.RemoveAt(pick);
                            c.Piles.Discard.Add(card);
                        }
                        break;
                    case "drain":
                        for (var k = 0; k < step.Value<int>("n"); k++)
                        {
                            c.Piles.Discard.Add(c.Piles.Draw[0]);
                            c.Piles.Draw.RemoveAt(0);
                        }
                        break;
                    case "draw":
                        Actions.DrawCards(c, step.Value<int>("n"));
                        break;
                    default:
                        foreach (var enemy in c.Enemies.Where(e => e.Value<bool>("alive"))) enemy["skipNextTurn"] = true;
                        Assert.That(c.Enemies.Count(e => e.Value<bool>("alive")), Is.EqualTo(step.Value<int>("staggered")), at + " staggered");
                        CoreTurns.EndTurn(c, new string[0]);
                        break;
                }
                if (step["delta"] == null) return;
                want = Core2CombatStartParityTests.Apply(want, step["delta"]);
                Core2CombatStartParityTests.Same(at, want, Core2CombatStartParityTests.Project(c));
                Assert.That(Core2CombatStartParityTests.Events(c, mark), Is.EqualTo(step["events"].Select(t => t.Value<string>()).ToList()), at + " events");
                Assert.That(Ids(c.Piles.Draw), Is.EqualTo(step["draw"].Select(t => t.Value<string>()).ToList()), at + " draw pile");
            }
        }
    }
}
