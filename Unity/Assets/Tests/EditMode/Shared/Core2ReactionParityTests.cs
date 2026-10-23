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
    /// WP-10 (D-250): the player's reaction stack. Everything recorded by Tools/oracle-core2-reactions.mjs from the shipped web is played again on
    /// the C# port through the one door the web has (<see cref="CoreDispatch"/>): the fights (4 classes: a bot that plays attack, Smash, Sweep, Defend
    /// and Counter cards, ends the turn and answers every reaction offer from its own LCG, half Play the first option, half Back) and the exchanges
    /// (a Defend and a Counter offered against each maneuver, a move of several hits, the switch, no Stamina, no card, a locked hand, a Poise on the
    /// edge, two attackers, a lethal hit). After every step the whole combat must equal the web's as a delta (the paused state included: the offer,
    /// the enemy phase's cursor, the held queue, the hand cleanup, the stack), the event types in order, the payload of every reaction and Counter
    /// event, the offer the step paused on (id, group, owner, source and every option with its price) and every RNG counter. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2ReactionParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "reactions");

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
                    yield return new TestCaseData(file, i).SetName("Core2ReactionFight_" + Path.GetFileNameWithoutExtension(file).Replace("fights-", string.Empty) + "_" + i);
            }
        }

        public static IEnumerable<TestCaseData> Exchanges()
        {
            if (!File.Exists(Path.Combine(Dir, "exchanges.json"))) yield break;
            var rows = (JArray)Read("exchanges.json")["exchanges"];
            for (var i = 0; i < rows.Count; i++) yield return new TestCaseData(i).SetName("Core2ReactionExchange_" + rows[i].Value<string>("kind") + "_" + i);
        }

        public static IEnumerable<TestCaseData> AutoUnits()
        {
            if (!File.Exists(Path.Combine(Dir, "units.json"))) yield break;
            var count = ((JArray)Read("units.json")["units"]).Count;
            for (var i = 0; i < count; i++) yield return new TestCaseData(i).SetName("Core2AutoReaction_" + i);
        }

        /// <summary>D12: auto-combat's reaction chooser picks what the web's chooser picks, from the same state and options.</summary>
        [TestCaseSource(nameof(AutoUnits))]
        public void TheAutoReactionChoiceMatchesTheWeb(int index)
        {
            var unit = (JObject)((JArray)Read("units.json")["units"])[index];
            var state = (JObject)unit["state"];
            var options = ((JArray)unit["options"]).OfType<JObject>().ToList();
            var label = "autoReaction[" + index + "]";
            var intent = state["seats"]?["player"]?["intent"] as JObject;
            Assert.That(AutoReactionPolicy.PerceivedManeuver(intent), Is.EqualTo(unit["perceived"].Type == JTokenType.Null ? null : unit.Value<string>("perceived")), label + " perceived");
            var chosen = AutoReactionPolicy.Choose(state, unit.Value<string>("seat"), options);
            Assert.That(chosen?.Value<string>("id"), Is.EqualTo(unit["chosen"].Type == JTokenType.Null ? null : unit.Value<string>("chosen")), label + " chosen");
            Core2DamageParityTests.Same(label + " answer", unit["answer"], AutoReactionPolicy.Answer(new JObject { ["id"] = "offer" }, chosen));
        }

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Read("index.json");
            Assert.That(FightFiles().Count(), Is.EqualTo(4), "one file per class");
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Core2DamageParityTests.Data.Run.ProfileHash), "the oracle ran the profile that is loaded");
            var seen = (JObject)index["seen"];
            Assert.That(seen.Value<int>("offers"), Is.GreaterThan(100), "offers were recorded");
            Assert.That(seen.Value<int>("played"), Is.GreaterThan(0));
            Assert.That(seen.Value<int>("back"), Is.GreaterThan(0));
            Assert.That(seen.Value<int>("unavailable"), Is.GreaterThan(0));
        }

        [TestCaseSource(nameof(Fights))]
        public void TheFightPlaysAsTheWebPlaysIt(string file, int index) => Replay((JObject)((JArray)Read(file)["fights"])[index], file + "[" + index + "]");

        [TestCaseSource(nameof(Exchanges))]
        public void TheExchangePlaysAsTheWebPlaysIt(int index)
        {
            var exchange = (JObject)((JArray)Read("exchanges.json")["exchanges"])[index];
            Replay(exchange, exchange.Value<string>("kind") + "[" + index + "] " + exchange.Value<string>("label"));
        }

        private static readonly HashSet<string> PayloadTypes = new HashSet<string>
        {
            "combatReactionOffered", "combatReactionChosen", "combatReactionStacked", "combatReactionUnavailable", "combatReactionFizzled", "combatEnemyReactionStacked",
            "combatCounterArmed", "combatCounterConsumed", "combatCounterTriggered", "combatGuardBroken", "combatRangeReturnUnavailable",
            "combatEngagementMoved", "combatRangeActionUnavailable", "characterMoved",
        };

        private static readonly string[] Decoration = { "abilityBefore", "abilityEntities", "cardTargetsAllEnemies", "intentPreview", "actionPreview" };

        private static JToken Sorted(JToken token)
        {
            if (token is JObject o) return new JObject(o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Sorted(p.Value))));
            if (token is JArray a) return new JArray(a.Select(Sorted));
            return token.DeepClone();
        }

        /// <summary>The events of a step whose payload the port builds too: the reactions' and the Counter's, and the Block a Counter adds.</summary>
        private static JArray Payloads(CombatState c, int mark) => new JArray(c.EventLog.Skip(mark)
            .Where(e => PayloadTypes.Contains(e.Value<string>("type")) || (e.Value<string>("type") == "blockGained" && e.Value<string>("source") == "counter"))
            .Select(e =>
            {
                // The web leaves these four undefined where the port holds null (see CoreMatchups.EventCarrier).
                var copy = (JObject)Sorted(e);
                foreach (var key in new[] { "cardId", "cardInstanceId", "cardType", "abilityKind" }) if (copy[key]?.Type == JTokenType.Null && e.Value<string>("type") != "combatReactionChosen") copy.Remove(key);
                foreach (var key in Decoration) copy.Remove(key);
                return copy;
            }));

        /// <summary>The offer the fight is paused on, as the oracle records it (offerOf): null when it is not paused on one.</summary>
        internal static JToken Offer(CombatState c)
        {
            var pending = c.Fields["pendingReaction"] as JObject;
            if (pending == null) return JValue.CreateNull();
            return Sorted(new JObject
            {
                ["id"] = pending["id"].DeepClone(), ["groupKey"] = pending["groupKey"].DeepClone(), ["ownerId"] = pending["ownerId"].DeepClone(), ["sourceId"] = pending["sourceId"].DeepClone(),
                ["options"] = new JArray(((JArray)pending["options"]).OfType<JObject>().Select(o =>
                {
                    var row = new JObject
                    {
                        ["id"] = o["id"].DeepClone(), ["cardId"] = o["cardId"].DeepClone(), ["name"] = o["name"].DeepClone(), ["staminaCost"] = o["staminaCost"].DeepClone(), ["manaCost"] = o["manaCost"].DeepClone(),
                        ["choiceName"] = o["choiceName"].DeepClone(), ["play"] = o["play"].DeepClone(),
                    };
                    if (o["incoming"] != null) row["incoming"] = o["incoming"].DeepClone();
                    return row;
                })),
            });
        }

        /// <summary>The fight a record names: the run (a class whose passives hold a hook that strikes keeps its Defends and loses its Counters), the cards laid over it, the patch merged over the fighters.</summary>
        internal static CombatState Open(JObject spec)
        {
            var data = Core2DamageParityTests.Data;
            var seed = (uint)spec["seed"].Value<long>();
            var run = CoreRunRules.Create(data.Run, seed, spec.Value<string>("classId"), CoreRunOptions.FromJson(null, Core2DamageParityTests.Unlocked));
            if (spec["deckRemove"] is JArray remove)
            {
                var ids = remove.Select(t => t.Value<string>()).ToList();
                foreach (var card in ((JArray)run["deck"]).OfType<JObject>().Where(card => ids.Contains(card.Value<string>("cardId"))).ToList()) card.Remove();
            }
            foreach (var add in (spec["deckAdd"] as JArray ?? new JArray()).OfType<JObject>())
                ((JArray)run["deck"]).Add(new JObject { ["instanceId"] = add["instanceId"].DeepClone(), ["cardId"] = add["cardId"].DeepClone(), ["upgraded"] = false });
            var c = CoreRunCombat.Create(data, new Rng(seed), run, spec.Value<string>("encounterId"));
            if (spec["hand"] is JArray hand)
            {
                c.Piles.Hand.Clear();
                var i = 0;
                foreach (var cardId in hand.Select(t => t.Value<string>()))
                    c.Piles.Hand.Add(new JObject { ["instanceId"] = "mx" + (++i), ["cardId"] = cardId, ["upgraded"] = false });
            }
            if (spec["fields"] is JObject fields) Core2DamageParityTests.Merge(c.Fields, fields);
            if (spec["patch"]?["player"] is JObject player) Core2DamageParityTests.Merge(c.Player, player);
            if (spec["patch"]?["enemies"] is JObject enemies)
                foreach (var p in enemies.Properties().Where(p => int.Parse(p.Name) < c.Enemies.Count)) Core2DamageParityTests.Merge(c.Enemies[int.Parse(p.Name)], (JObject)p.Value);
            return c;
        }

        /// <summary>Plays one recorded fight or exchange on the port and holds it to the web after every step.</summary>
        internal static void Replay(JObject record, string label, Action<CombatState, JObject, string> before = null)
        {
            var c = Open(record);
            JToken want = record["start"]["state"].DeepClone();
            Core2CombatStartParityTests.Same(label + " start", want, Core2CombatStartParityTests.Project(c));
            Assert.That(Core2CombatStartParityTests.Events(c, 0), Is.EqualTo(record["start"]["events"].Select(t => t.Value<string>()).ToList()), label + " start events");
            var n = 0;
            foreach (var step in ((JArray)record["steps"]).OfType<JObject>())
            {
                var intent = (JObject)step["intent"];
                var at = $"{label} step {n++} {intent.Value<string>("type")}";
                var mark = c.EventLog.Count;
                before?.Invoke(c, step, at);
                foreach (var id in (step["staggered"] as JArray ?? new JArray()).Select(t => t.Value<string>()))
                    c.Enemies.First(e => e.Value<string>("id") == id)["skipNextTurn"] = true;
                if (step["error"] != null)
                {
                    Assert.Catch<InvalidOperationException>(() => CoreDispatch.Dispatch(c, intent), at + " the web refused this intent: " + step["error"]);
                    continue;
                }
                CoreDispatch.Dispatch(c, intent);
                want = Core2CombatStartParityTests.Apply(want, step["delta"]);
                Assert.That(string.Join(" ", Core2CombatStartParityTests.Events(c, mark)), Is.EqualTo(string.Join(" ", step["events"].Select(t => t.Value<string>()))), at + " events");
                Core2CombatStartParityTests.Same(at, want, Core2CombatStartParityTests.Project(c));
                Core2DamageParityTests.Same(at + " payloads", step["payloads"], Payloads(c, mark));
                Core2DamageParityTests.Same(at + " offer", step["offer"], Offer(c));
            }
        }
    }
}
