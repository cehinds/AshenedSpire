using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.Domain.Combat;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-12 (D-270): range and engagement. Everything recorded by <c>Tools/oracle-core2-reactions.mjs --bot range</c> from the shipped web is played again
    /// on the C# port through <see cref="CoreDispatch"/>: fights in which the bot takes every route a card offers (advance and throw), plays the cards that
    /// move the engagement, steps across its side of the field, answers every reaction offer, and meets enemies that start at a distance, close it
    /// on their own Stamina or with their closing move, and strike from where they stand. After every step the whole combat must equal the web's.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2RangeParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "range");

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
                    yield return new TestCaseData(file, i).SetName("Core2RangeFight_" + Path.GetFileNameWithoutExtension(file).Replace("fights-", string.Empty) + "_" + i);
            }
        }

        public static IEnumerable<TestCaseData> Exchanges()
        {
            if (!File.Exists(Path.Combine(Dir, "exchanges.json"))) yield break;
            var rows = (JArray)Read("exchanges.json")["exchanges"];
            for (var i = 0; i < rows.Count; i++) yield return new TestCaseData(i).SetName("Core2RangeExchange_" + rows[i].Value<string>("kind") + "_" + i);
        }

        /// <summary>The route rows the oracle records (routeRows): one flat row per route with its price and the odds of every target.</summary>
        private static JToken Rows(JObject range) => range == null ? JValue.CreateNull() : new JArray(range.Arr("routes").OfType<JObject>().Select(r => new JObject
        {
            ["id"] = r["id"].DeepClone(), ["available"] = r["available"].DeepClone(), ["affordable"] = r["affordable"].DeepClone(), ["reason"] = r["reason"].DeepClone(), ["advanceSp"] = r["advanceSp"].DeepClone(),
            ["precisionDelta"] = r["precisionDelta"].DeepClone(), ["costs"] = r["costs"].DeepClone(),
            ["targets"] = new JArray(r.Arr("targets").OfType<JObject>().Select(t => new JObject
            {
                ["targetId"] = t["targetId"].DeepClone(), ["band"] = t["band"].DeepClone(), ["inRange"] = t["inRange"].DeepClone(), ["hitChance"] = t["hitChance"].DeepClone(),
                ["hitChances"] = t["hitChances"].DeepClone(), ["currentHitChance"] = t["currentHitChance"].DeepClone(), ["conditional"] = t["conditional"].DeepClone(),
            })),
        }));

        /// <summary>The range quote of every card of the hand against every target, before the step, as the web showed it.</summary>
        private static void CheckPreviews(CombatState c, JObject step, string at)
        {
            if (!(step["previews"] is JObject previews)) return;
            foreach (var card in previews.Properties())
            {
                var row = (JObject)card.Value;
                foreach (var target in ((JObject)row["card"]).Properties())
                {
                    if (target.Value is JObject failed && failed["error"] != null) continue;
                    var got = CoreRangePreview.Card(c, card.Name, target.Name == "undefined" ? null : target.Name);
                    Core2DamageParityTests.Same(at + " previewCard " + card.Name + " -> " + target.Name, target.Value, Rows(got));
                }
                foreach (var target in ((JObject)row["hit"]).Properties())
                {
                    if (target.Value is JObject failed && failed["error"] != null) continue;
                    Core2DamageParityTests.Same(at + " previewCardHit " + card.Name + " -> " + target.Name, target.Value, Rows(CoreRangePreview.Hit(c, card.Name, target.Name)));
                }
            }
        }

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Read("index.json");
            Assert.That(FightFiles().Count(), Is.EqualTo(4), "one file per class");
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Core2DamageParityTests.Data.Run.ProfileHash), "the oracle ran the profile that is loaded");
        }

        [TestCaseSource(nameof(Fights))]
        public void TheFightPlaysAsTheWebPlaysIt(string file, int index) => Core2ReactionParityTests.Replay((JObject)((JArray)Read(file)["fights"])[index], file + "[" + index + "]", CheckPreviews);

        [TestCaseSource(nameof(Exchanges))]
        public void TheExchangePlaysAsTheWebPlaysIt(int index)
        {
            var exchange = (JObject)((JArray)Read("exchanges.json")["exchanges"])[index];
            Core2ReactionParityTests.Replay(exchange, exchange.Value<string>("kind") + "[" + index + "] " + exchange.Value<string>("label"), CheckPreviews);
        }
    }
}
