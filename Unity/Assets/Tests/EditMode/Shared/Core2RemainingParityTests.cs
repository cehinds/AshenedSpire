using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-C0 (D-290): the Core 2 features a default run reaches that the fight oracles used to leave out. Every fight recorded by
    /// Tools/oracle-core2-remaining.mjs (a bot that plays any intent a player can send: every card with every target, choice and route, the flasks of the
    /// charge pools and of the slots, the answers to every offer and choice; no filter, the enemies take every turn in full; 4 classes x every encounter of the
    /// profile, and the featured runs that hold what the start does not) is played again on the C# port from the same seed and must equal the web after every
    /// step: the whole combat as a delta, the event types in order and every RNG counter. The port refuses nothing the web accepts. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2RemainingParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "remaining");

        private static readonly Dictionary<string, JObject> Files = new Dictionary<string, JObject>();

        internal static JObject Read(string name)
        {
            lock (Files)
            {
                if (!Files.TryGetValue(name, out var doc)) Files[name] = doc = (JObject)Ashen.Content.JsonContent.Parse(File.ReadAllText(Path.Combine(Dir, name)));
                return doc;
            }
        }

        public static IEnumerable<string> FightFiles() =>
            Directory.Exists(Dir) ? Directory.GetFiles(Dir, "fights-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal) : Enumerable.Empty<string>();

        public static IEnumerable<TestCaseData> Fights()
        {
            foreach (var file in FightFiles())
            {
                var fights = (JArray)Read(file)["fights"];
                for (var i = 0; i < fights.Count; i++)
                    yield return new TestCaseData(file, i).SetName("Core2Remaining_" + Path.GetFileNameWithoutExtension(file).Replace("fights-", string.Empty) + "_" + (fights[i].Value<string>("feature") ?? "plain") + "_" + fights[i].Value<string>("encounterId") + "_" + i);
            }
        }

        /// <summary>The fight a record names: the run, handed what the spec names (the oracle's applySpec), then the encounter.</summary>
        internal static CombatState Open(JObject fight)
        {
            var seed = (uint)fight["seed"].Value<long>();
            var data = Core2DamageParityTests.Data;
            var run = CoreRunRules.Create(data.Run, seed, fight.Value<string>("classId"), CoreRunOptions.FromJson(null, Core2DamageParityTests.Unlocked));
            var spec = fight["spec"] as JObject ?? new JObject();
            if (spec["deck"] is JArray deck)
                run["deck"] = new JArray(deck.Select((t, n) => new JObject { ["instanceId"] = "rm" + (n + 1), ["cardId"] = t.Value<string>(), ["upgraded"] = false }));
            foreach (var set in (spec["sets"] as JObject ?? new JObject()).Properties())
                for (var n = 0; n < ((JArray)set.Value).Count; n++) run["loadout"]["sets"][set.Name][n] = set.Value[n].DeepClone();
            var i = 0;
            foreach (var cardId in (spec["deckAdd"] as JArray ?? new JArray()).Select(t => t.Value<string>()))
                ((JArray)run["deck"]).Add(new JObject { ["instanceId"] = "rm" + (++i), ["cardId"] = cardId, ["upgraded"] = false });
            foreach (var flaskId in (spec["flasks"] as JArray ?? new JArray()).Select(t => t.Value<string>()))
                ((JArray)run["flasks"]).Add(new JObject { ["flaskId"] = flaskId });
            foreach (var id in (spec["relics"] as JArray ?? new JArray()).Select(t => t.Value<string>()))
                if (!((JArray)run["relics"]).Any(t => t.Value<string>() == id)) ((JArray)run["relics"]).Add(id);
            if (spec["maxHp"] != null) { run["maxHp"] = spec["maxHp"].DeepClone(); run["hp"] = spec["maxHp"].DeepClone(); }
            return CoreRunCombat.Create(data, new Rng(seed), run, fight.Value<string>("encounterId"));
        }

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Read("index.json");
            Assert.That(FightFiles().Count(), Is.EqualTo(4), "one file per class");
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Core2DamageParityTests.Data.Run.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(index["tally"].Value<int>("fights"), Is.GreaterThan(100));
            Assert.That(((JObject)index["tally"]["rejections"]).Count, Is.EqualTo(0), "the oracle leaves nothing out: no step is rejected by a filter");
        }

        [TestCaseSource(nameof(Fights))]
        public void TheFightPlaysAsTheWebPlaysIt(string file, int index) =>
            Core2FightParityTests.Replay((JObject)((JArray)Read(file)["fights"])[index], file + "[" + index + "]", Open);
    }
}
