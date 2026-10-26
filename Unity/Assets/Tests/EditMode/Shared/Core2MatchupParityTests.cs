using System;
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
    /// WP-8 (D-230): Counter, Smash and Sweep. Everything recorded by Tools/oracle-core2-matchups.mjs from the shipped web is played again on the
    /// C# port. The fights (4 classes: a bot that plays attack, Smash, Sweep and Counter cards and ends the turn, the enemies' Counters included)
    /// and the exchanges (a card of a maneuver against an armed Counter, an enemy attack against a Counter the player armed, a Poise break that
    /// interrupts a Counter, two attackers and one Counter, a Smash against a spell stance) must equal the web after every step: the whole
    /// combat as a delta, the event types in order, the payload of every Counter event and every RNG counter. The units cover the pure pieces:
    /// the Counter arithmetic, the coverage filter, the prepared Counter and its receipt, the stance pressure matrix, the Counter's Accuracy
    /// check, the Guard break and the round-start decay. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2MatchupParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "matchups");


        internal static JObject Read(string name) => OracleDocuments.Read(Path.Combine(Dir, name));

        private static CoreCombatData Data => Core2DamageParityTests.Data;

        public static IEnumerable<string> FightFiles() =>
            Directory.Exists(Dir) ? Directory.GetFiles(Dir, "fights-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal) : Enumerable.Empty<string>();

        public static IEnumerable<TestCaseData> Fights()
        {
            foreach (var file in FightFiles())
            {
                var fights = (JArray)Read(file)["fights"];
                for (var i = 0; i < fights.Count; i++)
                    yield return new TestCaseData(file, i).SetName("Core2MatchupFight_" + Path.GetFileNameWithoutExtension(file).Replace("fights-", string.Empty) + "_" + i);
            }
        }

        public static IEnumerable<TestCaseData> Exchanges()
        {
            if (!File.Exists(Path.Combine(Dir, "exchanges.json"))) yield break;
            var rows = (JArray)Read("exchanges.json")["exchanges"];
            for (var i = 0; i < rows.Count; i++) yield return new TestCaseData(i).SetName("Core2MatchupExchange_" + rows[i].Value<string>("kind") + "_" + i);
        }

        // ------------------------------------------------------------------ replaying a record

        /// <summary>The fight a record names: the run, the cards laid over it (the deck and the hand), the patch merged over the fighters.</summary>
        internal static CombatState Open(JObject spec)
        {
            var seed = (uint)spec["seed"].Value<long>();
            var run = CoreRunRules.Create(Data.Run, seed, spec.Value<string>("classId"), CoreRunOptions.FromJson(null, Core2DamageParityTests.Unlocked));
            foreach (var add in (spec["deckAdd"] as JArray ?? new JArray()).OfType<JObject>())
                ((JArray)run["deck"]).Add(new JObject { ["instanceId"] = add["instanceId"].DeepClone(), ["cardId"] = add["cardId"].DeepClone(), ["upgraded"] = false });
            var c = CoreRunCombat.Create(Data, new Rng(seed), run, spec.Value<string>("encounterId"));
            if (spec["hand"] is JArray hand)
            {
                c.Piles.Hand.Clear();
                var i = 0;
                foreach (var cardId in hand.Select(t => t.Value<string>()))
                    c.Piles.Hand.Add(new JObject { ["instanceId"] = "mx" + (++i), ["cardId"] = cardId, ["upgraded"] = false });
            }
            if (spec["patch"]?["player"] is JObject player) Core2DamageParityTests.Merge(c.Player, player);
            if (spec["patch"]?["enemies"] is JObject enemies)
                foreach (var p in enemies.Properties().Where(p => int.Parse(p.Name) < c.Enemies.Count)) Core2DamageParityTests.Merge(c.Enemies[int.Parse(p.Name)], (JObject)p.Value);
            return c;
        }

        private static readonly HashSet<string> PayloadTypes = new HashSet<string> { "combatCounterArmed", "combatCounterConsumed", "combatCounterTriggered", "combatGuardBroken", "combatRangeReturnUnavailable" };

        /// <summary>The events of a step whose payload the port builds too: the Counter's, and the Block it adds.</summary>
        private static JArray Payloads(CombatState c, int mark) => new JArray(c.EventLog.Skip(mark)
            .Where(e => PayloadTypes.Contains(e.Value<string>("type")) || (e.Value<string>("type") == "blockGained" && e.Value<string>("source") == "counter"))
            .Select(e =>
            {
                // The web leaves these four undefined where the port holds null (see CoreMatchups.EventCarrier).
                var copy = (JObject)Sorted(e);
                foreach (var key in new[] { "cardId", "cardInstanceId", "cardType", "abilityKind" }) if (copy[key]?.Type == JTokenType.Null) copy.Remove(key);
                foreach (var key in Decoration) copy.Remove(key);
                return copy;
            }));

        private static JToken Sorted(JToken token)
        {
            if (token is JObject o) return new JObject(o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Sorted(p.Value))));
            if (token is JArray a) return new JArray(a.Select(Sorted));
            return token.DeepClone();
        }

        internal static void Replay(JObject record, string label, Func<CombatState, CombatState> between = null)
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
                switch (intent.Value<string>("type"))
                {
                    case "endTurn":
                        foreach (var id in (step["staggered"] as JArray ?? new JArray()).Select(t => t.Value<string>()))
                            c.Enemies.First(e => e.Value<string>("id") == id)["skipNextTurn"] = true;
                        CoreDispatch.Dispatch(c, intent);
                        break;
                    case "setReactions":
                    case "playCard":
                    case "chooseReaction":
                    case "chooseCards":
                    case "chooseDiscard":
                        CoreDispatch.Dispatch(c, intent);
                        break;
                    default:
                        Assert.Fail(at + " an intent the port does not play");
                        break;
                }
                want = Core2CombatStartParityTests.Apply(want, step["delta"]);
                Assert.That(string.Join(" ", Core2CombatStartParityTests.Events(c, mark)), Is.EqualTo(string.Join(" ", step["events"].Select(t => t.Value<string>()))), at + " events");
                Core2CombatStartParityTests.Same(at, want, Core2CombatStartParityTests.Project(c));
                Core2DamageParityTests.Same(at + " counter events", step["payloads"], Payloads(c, mark));
                if (between != null) { c = between(c); if (ReferenceEquals(step, ((JArray)record["steps"]).Last())) Core2CombatStartParityTests.Same(at + " restored", want, Core2CombatStartParityTests.Project(c)); }
            }
        }

        // ------------------------------------------------------------------ units

        public static IEnumerable<TestCaseData> Units()
        {
            if (!File.Exists(Path.Combine(Dir, "units.json"))) yield break;
            var rows = (JArray)Read("units.json")["units"];
            for (var i = 0; i < rows.Count; i++) yield return new TestCaseData(i).SetName("Core2MatchupUnit_" + rows[i].Value<string>("kind") + "_" + i);
        }

        private static readonly string[] Decoration = { "abilityBefore", "abilityEntities", "cardTargetsAllEnemies", "intentPreview", "actionPreview" };

        /// <summary>The events since <paramref name="mark"/>, without the decoration the port does not build.</summary>
        private static JArray EventRows(CombatState c, int mark) => new JArray(c.EventLog.Skip(mark).Select(e =>
        {
            var copy = (JObject)e.DeepClone();
            foreach (var key in Decoration) copy.Remove(key);
            return Sorted(copy);
        }));

        private static void Same(string label, JToken want, JToken got) => Core2DamageParityTests.Same(label, Sorted(want), Sorted(got));

        private static List<JObject> Rows(JToken token) => (token as JArray ?? new JArray()).OfType<JObject>().ToList();

        private static string NullableString(JToken token) => token == null || token.Type == JTokenType.Null ? null : token.Value<string>();

        [TestCaseSource(nameof(Units))]
        public void TheUnitMatchesTheWeb(int index)
        {
            var unit = (JObject)((JArray)Read("units.json")["units"])[index];
            var kind = unit.Value<string>("kind");
            var b = Core2DamageParityTests.Build((JObject)unit["spec"]);
            var c = b.C;
            var label = kind + "[" + index + "]";
            switch (kind)
            {
                case "counterReturn":
                    foreach (var row in Rows(unit["rows"]))
                        Same(label + " " + row["incoming"] + " " + row["advantaged"], row["result"], CoreCounterReturn.Of(c, row.Value<double>("incoming"), row.Value<bool>("advantaged")).ToJson());
                    foreach (var row in Rows(unit["beats"]))
                        Assert.That(CoreCounterReturn.Beats(c, NullableString(row["maneuver"])), Is.EqualTo(row.Value<bool>("result")), label + " beats " + row["maneuver"]);
                    break;
                case "receiptReturn":
                {
                    var receipt = (JObject)unit["receipt"];
                    Same(label + " return", unit["forReceipt"], CoreCounterReturn.ForReceipt(c, receipt).ToJson());
                    var bonuses = ((JArray)unit["bonus"]).Select(t => t.Value<double>()).ToList();
                    for (var i = 0; i < bonuses.Count; i++)
                        Assert.That(CoreCounterReturn.BlockBonus(c, receipt, i, unit["incoming"]), Is.EqualTo(bonuses[i]), label + " bonus " + i);
                    break;
                }
                case "blockGrant":
                    Assert.That(CoreCounterReturn.BlockGrant(c, b.Target, unit.Value<double>("amount")), Is.EqualTo(unit.Value<double>("result")), label);
                    break;
                case "spentPaid":
                {
                    var carrier = unit["manual"].Type == JTokenType.Null ? new JObject() : new JObject { ["manualCounterSerial"] = unit["manual"].DeepClone() };
                    Assert.That(CoreCounterReturn.Spent(b.Target), Is.EqualTo(unit.Value<bool>("spent")), label + " spent");
                    Assert.That(CoreCounterReturn.PaidContact(c, carrier, new JObject { ["serial"] = unit["serial"].DeepClone() }), Is.EqualTo(unit.Value<bool>("paid")), label + " paid");
                    Assert.That(CoreCounterReturn.PaidContact(c, carrier, null), Is.EqualTo(unit.Value<bool>("paidWithoutCounter")), label + " paid without a Counter");
                    break;
                }
                case "coverage":
                {
                    var coverage = unit["coverage"] as JObject;
                    var profile = unit["profile"] as JObject;
                    var effect = unit.Value<string>("effect");
                    Assert.That(CoreMatchups.CounterCoverageMatches(coverage, profile, effect), Is.EqualTo(unit.Value<bool>("result")), label);
                    Assert.That(CoreMatchups.CounterCoverageMatches(null, profile, effect), Is.EqualTo(unit.Value<bool>("noCoverage")), label + " none");
                    Assert.That(CoreMatchups.CounterCoverageMatches(coverage, null, effect), Is.EqualTo(unit.Value<bool>("noProfile")), label + " no profile");
                    break;
                }
                case "counterCard":
                {
                    var def = (JObject)unit["def"];
                    var carrier = (JObject)unit["carrier"];
                    foreach (var row in Rows(unit["covers"]))
                    {
                        var profile = (JObject)row["profile"];
                        Assert.That(CoreMatchups.CounterCoversAttack(c, def, profile, "damage"), Is.EqualTo(row.Value<bool>("damage")), label + " covers damage " + profile);
                        Assert.That(CoreMatchups.CounterCoversAttack(c, def, profile, "status"), Is.EqualTo(row.Value<bool>("status")), label + " covers status " + profile);
                    }
                    Assert.That(CoreMatchups.IsSpellCounter(carrier), Is.EqualTo(unit.Value<bool>("spellCounter")), label + " spell counter");
                    Assert.That(CoreMatchups.ArmsAsCoreCounter(c, carrier), Is.EqualTo(unit.Value<bool>("arms")), label + " arms");
                    Assert.That(CoreMatchups.ArmsAsCoreCounter(c, def), Is.EqualTo(unit.Value<bool>("armsDef")), label + " arms (def)");
                    Assert.That(CoreStances.ConfiguredStance(c, carrier), Is.EqualTo(unit.Value<string>("stance")), label + " stance");
                    break;
                }
                case "prepare":
                {
                    if (unit["error"].Type != JTokenType.Null) break;
                    var def = (JObject)unit["def"];
                    var instance = new JObject { ["instanceId"] = "mxx", ["cardId"] = def["id"].DeepClone(), ["upgraded"] = false };
                    var carrier = CoreCardPlay.Carrier(c, instance, def);
                    b.Target.Obj("counters")["cardsPlayedThisCombat"] = 1;
                    var enemy = unit["targetId"].Type == JTokenType.Null ? null : c.Enemies[0];
                    var effects = Rows(def["effects"]);
                    if (effects.Any(e => e["if"] != null)) break;
                    CoreCounterReturn.PrepareCounter(c, b.Target, enemy, carrier, effects, new ActionMeta());
                    var counter = (JObject)b.Target["combatCounter"].DeepClone();
                    counter.Remove("carrier");
                    Same(label + " counter " + unit["cardId"], unit["counter"], counter);
                    break;
                }
                case "preview":
                {
                    var contacts = Rows(unit["contacts"]);
                    var receipt = CoreTactical.Preview(c, b.Source, b.Target, (JObject)unit["carrier"], contacts);
                    Same(label + " receipt " + unit["cardId"] + " " + unit["variation"], unit["receipt"], receipt);
                    break;
                }
                case "stancePressure":
                {
                    var carrier = (JObject)unit["carrier"];
                    var hit = unit["hit"] as JObject;
                    Assert.That(CoreTactical.StancePressureMultiplier(c, b.Source, b.Target, carrier, hit), Is.EqualTo(unit.Value<double>("result")), label + " result");
                    Assert.That(CoreTactical.StancePressureMultiplier(c, b.Source, b.Target, carrier, new JObject { ["evadeAttempted"] = true }), Is.EqualTo(unit.Value<double>("explicit")), label + " explicit");
                    Assert.That(CoreTactical.StancePressureMultiplier(c, b.Source, b.Target, carrier), Is.EqualTo(unit.Value<double>("derived")), label + " derived");
                    Assert.That(CoreTactical.MatchupPoiseMultiplier(c, b.Source, b.Target, carrier), Is.EqualTo(unit.Value<double>("poise")), label + " poise");
                    break;
                }
                case "counterAttack":
                    CounterAttack(unit, label);
                    break;
                case "authorize":
                {
                    foreach (var engagement in c.Fields.Obj("rangeState").Obj("engagements").Properties().ToList()) engagement.Value = unit["band"].DeepClone();
                    if (unit["error"].Type != JTokenType.Null) break;
                    var mark = c.EventLog.Count;
                    var result = CoreCounterReturn.AuthorizeRange(c, b.Target, b.Source, (JObject)unit["counterCarrier"].DeepClone(), (JObject)unit["incoming"].DeepClone());
                    Same(label + " carrier", unit["result"], (JToken)result ?? JValue.CreateNull());
                    Same(label + " events", unit["events"], EventRows(c, mark));
                    break;
                }
                case "paidAuthorize":
                {
                    foreach (var engagement in c.Fields.Obj("rangeState").Obj("engagements").Properties().ToList()) engagement.Value = unit["band"].DeepClone();
                    var incoming = (JObject)unit["incoming"].DeepClone();
                    var plan = (JObject)incoming["rangePlan"];
                    var contact = ((JArray)plan["targets"]).OfType<JObject>().First(row => row.Value<string>("contactId") == incoming.Value<string>("rangeContactId"));
                    c.ReactionPlaying = true;
                    c.RangeContext = new JObject { ["sourceKey"] = plan["sourceKey"].DeepClone(), ["targets"] = new JArray(contact.DeepClone()) };
                    c.RangeIncoming = new JObject { ["rootId"] = plan["rootId"].DeepClone(), ["contactId"] = contact["contactId"].DeepClone(), ["sourceKey"] = plan["sourceKey"].DeepClone(), ["targetKey"] = CoreRange.ActorKey(c, b.Target) };
                    JObject paid = null;
                    string paidError = null;
                    try { paid = CoreCounterReturn.PaidRange(c, b.Target, (JObject)unit["counterCarrier"].DeepClone(), new JObject { ["energy"] = 1, ["stamina"] = 1, ["mana"] = 0 }, unit.Value<string>("route")); }
                    catch (PlayRefusalException e) { paidError = e.Message; }
                    c.ReactionPlaying = false;
                    c.RangeContext = null;
                    c.RangeIncoming = null;
                    Assert.That(paidError, Is.EqualTo(unit["paidError"].Type == JTokenType.Null ? null : unit.Value<string>("paidError")), label + " paid error");
                    Same(label + " paid", unit["paid"], (JToken)paid ?? JValue.CreateNull());
                    if (paid == null) break;
                    var mark = c.EventLog.Count;
                    var carrier = (JObject)unit["counterCarrier"].DeepClone();
                    carrier["rangeCounterReservation"] = paid["reservation"].DeepClone();
                    var result = CoreCounterReturn.AuthorizeRange(c, b.Target, b.Source, carrier, incoming);
                    Same(label + " carrier", unit["result"], (JToken)result ?? JValue.CreateNull());
                    Same(label + " events", unit["events"], EventRows(c, mark));
                    break;
                }
                case "guardBreak":
                {
                    var receipt = (JObject)unit["receipt"];
                    var carrier = (JObject)unit["carrier"];
                    Assert.That(CoreMatchups.IsSmashGuardBreak(receipt, b.Target), Is.EqualTo(unit.Value<bool>("verdict")), label + " verdict");
                    Assert.That(CoreMatchups.IsSmashGuardBreak(null, b.Target), Is.EqualTo(unit.Value<bool>("none")), label + " none");
                    c.Queue.Clear();
                    var mark = c.EventLog.Count;
                    CoreMatchups.CompleteGuardBreak(c, b.Source, b.Target, carrier, receipt);
                    Same(label + " events", unit["events"], EventRows(c, mark));
                    Same(label + " queue", unit["queue"], new JArray(c.Queue.Select(a => new JObject
                    {
                        ["effect"] = a.Effect.DeepClone(), ["source"] = a.Source?["id"]?.DeepClone(), ["target"] = a.Target?["id"]?.DeepClone(), ["combatReaction"] = a.Card?["combatReaction"]?.DeepClone(),
                    })));
                    break;
                }
                case "decay":
                {
                    var mark = c.EventLog.Count;
                    CoreMatchups.DecayStanceRoundStart(c);
                    Same(label + " player", unit["player"], Core2DamageParityTests.Looks(c.Player));
                    Same(label + " enemies", unit["enemies"], new JArray(c.Enemies.Select(Core2DamageParityTests.Looks)));
                    Same(label + " events", unit["events"], EventRows(c, mark));
                    break;
                }
                case "wardDamage":
                {
                    var mark = c.EventLog.Count;
                    c.Queue.Clear();
                    var action = new CombatAction { Effect = new JObject { ["op"] = "wardDamage", ["target"] = "enemy", ["amount"] = unit["amount"].DeepClone() }, Source = b.Source, Owner = b.Source, Target = b.Target, Card = (JObject)unit["carrier"], Meta = new ActionMeta() };
                    CoreActions.Execute(c, action);
                    Assert.That(unit["error"].Type, Is.EqualTo(JTokenType.Null), label + " the web refused");
                    Same(label + " target", unit["target"], Core2DamageParityTests.Looks(b.Target));
                    Same(label + " events", unit["events"], EventRows(c, mark));
                    Assert.That(c.Queue.Count, Is.EqualTo(unit.Value<int>("queue")), label + " queue");
                    break;
                }
                default:
                    Assert.Fail("a unit of kind " + kind);
                    break;
            }
        }

        /// <summary>counterAttackPreview and resolveCounterAttack: the preview, then the reply's one Accuracy check on each script of dice.</summary>
        private static void CounterAttack(JObject unit, string label)
        {
            var spec = (JObject)unit["spec"];
            var carrier = (JObject)spec["carrier"];
            void Bands(CombatState c) { foreach (var engagement in c.Fields.Obj("rangeState").Obj("engagements").Properties().ToList()) engagement.Value = unit["band"].DeepClone(); }
            var b = Core2DamageParityTests.Build(spec);
            Bands(b.C);
            var preview = CoreCounterReturn.AttackPreview(b.C, b.Source, b.Target, (JObject)carrier.DeepClone());
            Same(label + " preview", unit["preview"], (JToken)preview ?? JValue.CreateNull());
            foreach (var script in Rows(unit["scripts"]))
            {
                if (script["error"].Type != JTokenType.Null) continue;
                var fresh = Core2DamageParityTests.Build(spec);
                Bands(fresh.C);
                var dice = ((JArray)script["dice"]).Select(t => t.Value<int>()).ToList();
                var drawn = 0;
                Func<int, int, int> draw = (low, high) => dice[drawn++];
                var mark = fresh.C.EventLog.Count;
                var result = CoreCounterReturn.ResolveAttack(fresh.C, fresh.Source, fresh.Target, (JObject)carrier.DeepClone(), draw);
                Assert.That(drawn, Is.EqualTo(script.Value<int>("used")), label + " draws");
                Same(label + " result", script["result"], (JToken)result ?? JValue.CreateNull());
                Same(label + " target", script["targetAfter"], Core2DamageParityTests.Looks(fresh.Target));
                Same(label + " events", script["events"], EventRows(fresh.C, mark));
            }
        }

        // ------------------------------------------------------------------ the oracle

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Read("index.json");
            Assert.That(FightFiles().Count(), Is.GreaterThanOrEqualTo(4));
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Data.Run.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(index["exchanges"].Value<int>("count"), Is.GreaterThan(0));
            foreach (var seen in new[] { "counterArmed", "counterConsumed", "counterTriggered" })
                Assert.That(index["seen"].Value<int>(seen), Is.GreaterThan(0), "the recorded fights reach " + seen);
        }

        [TestCaseSource(nameof(Fights))]
        public void TheFightPlaysAsTheWebPlaysIt(string file, int index)
        {
            var fight = (JObject)((JArray)Read(file)["fights"])[index];
            Replay(fight, $"{file}[{index}] {fight.Value<string>("encounterId")}");
        }

        [TestCaseSource(nameof(Exchanges))]
        public void TheExchangePlaysAsTheWebPlaysIt(int index)
        {
            var exchange = (JObject)((JArray)Read("exchanges.json")["exchanges"])[index];
            Replay(exchange, $"exchange[{index}] {exchange.Value<string>("kind")} {exchange.Value<string>("label")}");
        }
    }
}
