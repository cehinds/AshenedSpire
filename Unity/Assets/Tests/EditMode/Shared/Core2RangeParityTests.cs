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


        internal static JObject Read(string name) => OracleDocuments.Read(Path.Combine(Dir, name));

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
        internal static void CheckPreviews(CombatState c, JObject step, string at)
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


        // ------------------------------------------------------------------ the units (Tools/oracle-core2-range.mjs)

        public static IEnumerable<TestCaseData> Units()
        {
            if (!File.Exists(Path.Combine(Dir, "quotes.json"))) yield break;
            var rows = (JArray)Read("quotes.json")["cases"];
            for (var i = 0; i < rows.Count; i++) yield return new TestCaseData(i).SetName("Core2RangeUnit_" + rows[i].Value<string>("kind") + "_" + i);
        }

        private static CombatState OpenUnit(JObject spec)
        {
            var c = Core2ReactionParityTests.Open(spec);
            var engagements = c.Fields.Obj("rangeState").Obj("engagements");
            var bands = (spec["bands"] as JArray) ?? new JArray();
            for (var i = 0; i < bands.Count && i < c.Enemies.Count; i++)
                engagements[CoreRange.PairKey("enemy:" + c.Enemies[i].Value<string>("id"), "player:player")] = bands[i].DeepClone();
            return c;
        }

        private static JObject Entity(CombatState c, string reference) => reference == "player" ? c.Player : c.Enemies[int.Parse(reference.Substring("enemy:".Length))];

        private static List<CombatAction> ActionsOf(CombatState c, JObject built) => ((JArray)built["rows"]).OfType<JObject>().Select(row => new CombatAction
        {
            Effect = (JObject)row["effect"].DeepClone(), Source = c.Player, Owner = c.Player, Target = Entity(c, row.Value<string>("target")), Card = (JObject)built["carrier"].DeepClone(), Meta = ActionMeta.Empty(),
        }).ToList();

        /// <summary>What the quote left on the actions: the target batches, the site batches and the source each hit is dealt with.</summary>
        private static JToken View(CombatState c, List<CombatAction> actions) => new JObject
        {
            ["actions"] = new JArray(actions.Select(a => new JObject
            {
                ["batches"] = a.Meta?.RangeTargetBatches == null ? JValue.CreateNull() : new JArray(a.Meta.RangeTargetBatches.Select(batch => new JArray(batch.Select(id => new JObject { ["id"] = id })))),
                ["sites"] = (JToken)a.Meta?.RangeSiteBatches ?? JValue.CreateNull(),
                ["resolved"] = a.Card?["resolvedRangeSource"]?.DeepClone() ?? JValue.CreateNull(),
            })),
            ["staminaAfter"] = c.Player["stamina"].DeepClone(), ["engagements"] = c.Fields.Obj("rangeState").Obj("engagements").DeepClone(),
        };

        private static void SameView(string at, JObject want, CombatState c, List<CombatAction> actions)
        {
            var got = (JObject)View(c, actions);
            foreach (var key in new[] { "actions", "staminaAfter", "engagements" })
                Core2DamageParityTests.Same(at + " " + key, want["actions"] != null && key == "actions" ? want["actions"] : want[key], got[key]);
            foreach (var p in ((JObject)want["rng"]).Properties())
                Assert.That(c.Rng.AllCounters().TryGetValue(p.Name, out var value) ? (long)value : 0L, Is.EqualTo(p.Value.Value<long>()), at + " rng " + p.Name);
        }

        [TestCaseSource(nameof(Units))]
        public void TheUnitMatchesTheWeb(int index)
        {
            var unit = (JObject)((JArray)Read("quotes.json")["cases"])[index];
            var at = unit.Value<string>("kind") + "[" + index + "]";
            var spec = (JObject)unit["spec"];
            switch (unit.Value<string>("kind"))
            {
                case "quote":
                {
                    var c = OpenUnit(spec);
                    var actions = ActionsOf(c, (JObject)unit["built"]);
                    var route = unit.Value<string>("route");
                    var preview = unit.Value<bool>("preview");
                    if (unit["error"] != null) { Assert.Catch<Exception>(() => CoreRange.Quote(c, c.Player, actions, route, preview), at + " the web refused this quote: " + unit["error"]); break; }
                    var plan = CoreRange.Quote(c, c.Player, actions, route, preview);
                    var lineage = plan.Obj("lineage");
                    plan.Remove("lineage");
                    Core2DamageParityTests.Same(at + " plan", unit["plan"], plan);
                    Core2DamageParityTests.Same(at + " lineage", unit["lineage"], lineage ?? (JToken)JValue.CreateNull());
                    SameView(at, unit, c, actions);
                    break;
                }
                case "accept":
                {
                    var c = OpenUnit(spec);
                    c.Player["stamina"] = unit["stamina"].DeepClone();
                    var actions = ActionsOf(c, (JObject)unit["built"]);
                    var mark = c.EventLog.Count;
                    if (unit["error"] != null) { Assert.Catch<Exception>(() => AcceptUnit(c, actions, unit), at + " the web refused this acceptance: " + unit["error"]); break; }
                    if (unit["skipped"] != null) break;
                    var carrier = AcceptUnit(c, actions, unit);
                    Core2DamageParityTests.Same(at + " plan", unit["plan"], carrier.Obj("rangePlan"));
                    Core2DamageParityTests.Same(at + " lineage", unit["lineage"], carrier["rangeLineagePlan"]?.DeepClone() ?? JValue.CreateNull());
                    SameView(at, unit, c, actions);
                    Core2DamageParityTests.Same(at + " events", unit["events"], new JArray(c.EventLog.Skip(mark).Select(e => new JObject { ["type"] = e["type"], ["amount"] = e["amount"]?.DeepClone() ?? JValue.CreateNull() })));
                    break;
                }
                case "move":
                {
                    var c = OpenUnit(spec);
                    if (spec["fields"]?["reactionSourceId"] != null) c.Fields["reactionSourceId"] = spec["fields"]["reactionSourceId"].DeepClone();
                    if (unit["fields"]?["reactionSourceId"] != null) c.Fields["reactionSourceId"] = unit["fields"]["reactionSourceId"].DeepClone();
                    var effect = (JObject)unit["effect"].DeepClone();
                    var target = unit.Value<string>("target");
                    var action = new CombatAction { Effect = effect, Source = c.Player, Owner = c.Player, Target = target == "self" ? c.Player : target == "pendingAttacker" ? null : c.Enemies[0], Card = new JObject(), Meta = ActionMeta.Empty() };
                    var mark = c.EventLog.Count;
                    if (unit["error"] != null) { Assert.Catch<Exception>(() => CoreRange.MoveEngagement(c, action), at + " the web refused this move: " + unit["error"]); break; }
                    CoreRange.MoveEngagement(c, action);
                    Core2DamageParityTests.Same(at + " engagements", unit["engagements"], c.Fields.Obj("rangeState").Obj("engagements"));
                    if (unit["events"] != null)
                        Core2DamageParityTests.Same(at + " events", unit["events"], new JArray(c.EventLog.Skip(mark).Select(e => new JObject { ["type"] = e["type"], ["sourceId"] = e["sourceId"], ["targetId"] = e["targetId"], ["bands"] = e["bands"] })));
                    break;
                }
                case "formation":
                {
                    var c = OpenUnit(spec);
                    Core2DamageParityTests.Same(at + " plan", unit["plan"], CoreFormation.Plan(c, unit.Value<string>("cell"), (JObject)unit["settings"]));
                    break;
                }
                default:
                    Assert.Fail("unknown unit " + at);
                    break;
            }
        }

        private static JObject AcceptUnit(CombatState c, List<CombatAction> actions, JObject unit)
        {
            var carrier = actions[0].Card;
            carrier["rangeRoute"] = unit["route"].DeepClone();
            carrier["rangeBaseCosts"] = new JObject { ["energy"] = 0, ["stamina"] = 0, ["mana"] = 0 };
            CoreRange.Accept(c, c.Player, actions, carrier, "root:1");
            return carrier;
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
