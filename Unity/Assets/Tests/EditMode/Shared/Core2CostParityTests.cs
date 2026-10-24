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
    /// WP-5 (D-200): Core 2 stances and card costs. Everything recorded by Tools/oracle-core2-costs.mjs from the web's shipped modules is
    /// recomputed by the C# port: the unit grid of the stance cost functions (every profile card against every stance a fight can hold), the
    /// price derivation of model/actionCosts.js (211 profile cards and 138 enemy cards at ranks 0..3, the enemy pools), and fights: the cost
    /// of every card in the opening hand and in a hand of all 211 cards under fourteen state variants (stance, statuses, phase, resources, cost
    /// growth), the cost part of previewCard, the gates a play meets with the Stamina and Mana one short and exactly enough, and chains of
    /// plays (the front half of doPlayCard: pay, leave the hand, count, enter the stance with its Precision and Evade) with the whole hand
    /// priced again after every play. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2CostParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "costs");

        private static CoreCombatData _data;
        private static readonly Dictionary<string, JObject> Files = new Dictionary<string, JObject>();

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

        private static JObject Read(string name)
        {
            lock (Files)
            {
                if (!Files.TryGetValue(name, out var doc)) Files[name] = doc = (JObject)JsonContent.Parse(File.ReadAllText(Path.Combine(Dir, name)));
                return doc;
            }
        }

        private static JObject Unlocked => new JObject
        {
            ["gameProfileUnlocks"] = new JObject { [Data.Run.ProfileId] = new JObject { ["completedRunIds"] = new JArray(Enumerable.Range(0, Data.Run.Run.Classes.Count).Select(i => "r" + i)) } },
        };

        private static CombatState Open(long seedValue, string classId, string encounterId)
        {
            var seed = (uint)seedValue;
            var run = CoreRunRules.Create(Data.Run, seed, classId, CoreRunOptions.FromJson(null, Unlocked));
            return CoreRunCombat.Create(Data, new Rng(seed), run, encounterId);
        }

        public static IEnumerable<string> FightFiles() =>
            Directory.Exists(Dir) ? Directory.GetFiles(Dir, "fights-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal) : Enumerable.Empty<string>();

        public static IEnumerable<TestCaseData> FightCases()
        {
            foreach (var file in FightFiles())
            {
                var count = ((JArray)Read(file)["fights"]).Count;
                for (var i = 0; i < count; i++) yield return new TestCaseData(file, i).SetName("Core2Costs_" + Path.GetFileNameWithoutExtension(file) + "_" + i);
            }
        }

        public static IEnumerable<TestCaseData> GridCases()
        {
            if (!File.Exists(Path.Combine(Dir, "grid.json"))) yield break;
            var grid = Read("grid.json");
            foreach (var variant in ((JArray)grid["variants"]).Select(v => v.Value<string>("name")))
                foreach (var stance in ((JArray)grid["stances"]).Select(t => t.Value<string>()))
                    yield return new TestCaseData(variant + "/" + stance).SetName("Core2Grid_" + variant + "_" + stance);
        }

        private static void Same(string label, JToken want, JToken got)
        {
            var diff = RunParityTests.FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail($"{label}: first difference at {diff}");
        }

        // ------------------------------------------------------------------ the setups the oracle lays over a fight

        private static JObject CardInstance(int index, string cardId) => new JObject { ["instanceId"] = "x" + (index + 1), ["cardId"] = cardId, ["upgraded"] = false };

        private static List<string> ProfileCardIds() => Data.Content("cards").Select(card => card.Value<string>("id")).ToList();

        private static CombatState Apply(CombatState c, JObject setup)
        {
            var p = c.Player;
            if (setup.Value<string>("hand") == "all") c.Piles.Hand = ProfileCardIds().Select((id, i) => CardInstance(i, id)).ToList();
            if (setup.ContainsKey("stance"))
            {
                if (setup["stance"].Type == JTokenType.Null) p.Remove("combatStance"); else p["combatStance"] = setup["stance"].DeepClone();
            }
            if (setup["concurrent"] != null) p["concurrentStances"] = setup["concurrent"].DeepClone();
            if (setup["statuses"] is JObject statuses)
                foreach (var s in statuses.Properties()) ((JObject)p["statuses"])[s.Name] = new JObject { ["stacks"] = s.Value.DeepClone() };
            if (setup["phase"] != null) c.Phase = setup.Value<string>("phase");
            if (setup["stamina"] != null) p["stamina"] = setup["stamina"].DeepClone();
            if (setup["mana"] != null) p["mana"] = setup["mana"].DeepClone();
            if (setup["riders"] is JObject riders)
            {
                var abilityRiders = (JObject)p["abilityRiders"];
                foreach (var r in riders.Properties()) abilityRiders[r.Name] = r.Value.DeepClone();
            }
            return c;
        }

        private static JObject PlayerState(CombatState c)
        {
            var p = c.Player;
            var statuses = (JObject)p["statuses"];
            return new JObject
            {
                ["stamina"] = p["stamina"].DeepClone(), ["mana"] = p["mana"].DeepClone(),
                ["combatStance"] = p["combatStance"]?.DeepClone() ?? JValue.CreateNull(),
                ["concurrentStances"] = p["concurrentStances"]?.DeepClone() ?? JValue.CreateNull(),
                ["matchingStanceCardMultiplier"] = p["matchingStanceCardMultiplier"]?.DeepClone() ?? JValue.CreateNull(),
                ["precision"] = statuses["precision"]?["stacks"]?.DeepClone() ?? 0, ["evade"] = statuses["evade"]?["stacks"]?.DeepClone() ?? 0,
                ["counters"] = p["counters"].DeepClone(), ["handSize"] = c.Piles.Hand.Count,
            };
        }

        // ------------------------------------------------------------------ the cost reads

        /// <summary>costsOf: [cardPlayCosts energy, mana, stamina, resolvedCardPlayCosts energy, mana, stamina, X] or { error }, as the oracle records them.</summary>
        private static JToken CostsOf(CombatState c, string instanceId)
        {
            try
            {
                var instance = c.Piles.Hand.First(card => card.Value<string>("instanceId") == instanceId);
                var def = CoreCosts.Resolve(c, instance);
                var paid = CoreCosts.CardPlayCosts(c, instanceId);
                var resolved = CoreCosts.PlayCosts(c, def);
                return new JArray(paid["energy"], paid["mana"], paid["stamina"], resolved["energy"], resolved["mana"], resolved["stamina"], def.Value<string>("cost") == "X" ? 1 : 0);
            }
            catch (Exception e) when (e is InvalidOperationException) { return new JObject { ["error"] = e.Message }; }
        }

        private static void CheckCosts(string label, CombatState c, JObject cards)
        {
            foreach (var card in cards.Properties())
            {
                var want = (JObject)card.Value;
                Same($"{label} {card.Name} ({want.Value<string>("cardId")}) costs", want["costs"], CostsOf(c, card.Name));
            }
        }

        private static void CheckPreview(string label, CombatState c, string instanceId, JObject want)
        {
            if (want["error"] != null) return;
            var got = CoreCosts.PreviewCosts(c, instanceId);
            var advance = want.Value<double>("advanceSp");
            var expect = new JObject
            {
                ["cost"] = want.Value<double>("cost") - advance, ["costIsX"] = want["costIsX"], ["manaCost"] = want["manaCost"], ["staminaCost"] = want.Value<double>("staminaCost") - advance,
            };
            if (want["costShift"].Type != JTokenType.Null) expect["costShift"] = want["costShift"];
            if (want["entryPrecision"].Type != JTokenType.Null) expect["entryPrecision"] = want["entryPrecision"];
            Assert.That(want["displayCosts"].Type, Is.EqualTo(JTokenType.Null), label + " the Core 2 face never shows a funded price");
            Same(label + " preview", expect, got);
        }

        private static readonly string[] CostRefusals = { "Not enough Stamina or Mana", "Not enough Actions (Stamina) to play this card", "Not enough mana to play this card" };

        private static void CheckLegality(string label, CombatState c, string instanceId, JObject legality)
        {
            if (legality == null || legality.Type == JTokenType.Null) return;
            foreach (var probe in legality.Properties())
            {
                var outcome = (JObject)probe.Value;
                var paid = CoreCosts.CardPlayCosts(c, instanceId);
                var energy = paid.Value<double>("energy");
                var mana = paid.Value<double>("mana");
                c.Player["stamina"] = probe.Name == "shortStamina" ? energy - 1 : energy;
                c.Player["mana"] = probe.Name == "shortMana" ? mana - 1 : mana;
                var got = CoreCosts.PlayRefusal(c, instanceId);
                var why = $"{label} {instanceId} {probe.Name}";
                if (outcome.Value<bool>("ok")) Assert.That(got, Is.Null, why);
                else
                {
                    var error = outcome.Value<string>("error");
                    if (CostRefusals.Contains(error) || error == Ashen.Generated.Core2CostMessages.PlayerTurnOnly || error.StartsWith("Restricted by ", StringComparison.Ordinal) || error.EndsWith(" is unplayable", StringComparison.Ordinal))
                        Assert.That(got, Is.EqualTo(error), why);
                    else if (probe.Name == "exact") Assert.That(got, Is.Null, why + " (the web refused earlier: " + error + ")");
                    else Assert.That(got == null || CostRefusals.Contains(got), why + " (the web refused earlier: " + error + "; got " + got + ")");
                }
            }
        }

        private static void CheckScenario(string label, JObject fight, JObject scenario)
        {
            var c = Apply(Open(fight.Value<long>("seed"), fight.Value<string>("classId"), fight.Value<string>("encounterId")), (JObject)scenario["setup"]);
            Same(label + " before", scenario["before"], PlayerState(c));
            var cards = (JObject)scenario["cards"];
            CheckCosts(label, c, cards);
            foreach (var card in cards.Properties())
            {
                var row = (JObject)card.Value;
                if (row["preview"] != null) CheckPreview($"{label} {card.Name}", c, card.Name, (JObject)row["preview"]);
            }
            foreach (var card in cards.Properties())
            {
                var row = (JObject)card.Value;
                if (row["legality"] == null) continue;
                var state = Apply(Open(fight.Value<long>("seed"), fight.Value<string>("classId"), fight.Value<string>("encounterId")), (JObject)scenario["setup"]);
                CheckLegality(label, state, card.Name, row["legality"] as JObject);
            }
        }

        // ------------------------------------------------------------------ the tests

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Read("index.json");
            Assert.That(FightFiles().Count(), Is.EqualTo(((JArray)index["files"]).Count(f => f.Value<string>("kind") == "fights")));
            foreach (var classId in Data.Run.Run.Classes.Ids) Assert.That(FightFiles(), Does.Contain("fights-" + classId + ".json"));
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Data.Run.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(Read("prices.json")["profileCards"].Count(), Is.EqualTo(ProfileCardIds().Count));
        }

        [TestCaseSource(nameof(GridCases))]
        public void TheStanceCostGridMatchesTheWeb(string key)
        {
            var grid = Read("grid.json");
            var fight = (JObject)grid["fight"];
            var parts = key.Split('/');
            var variant = ((JArray)grid["variants"]).First(v => v.Value<string>("name") == parts[0]);
            var stance = ((JArray)grid["stances"]).Select(t => t.Value<string>()).First(t => t == parts[1]);
            var setup = GridSetup(parts[1], (JObject)variant["statuses"]);
            var c = Apply(Open(fight.Value<long>("seed"), fight.Value<string>("classId"), fight.Value<string>("encounterId")), setup);
            var enemyPhase = Apply(Open(fight.Value<long>("seed"), fight.Value<string>("classId"), fight.Value<string>("encounterId")), setup);
            enemyPhase.Phase = "enemy";
            var asEnemy = Open(fight.Value<long>("seed"), fight.Value<string>("classId"), fight.Value<string>("encounterId"));
            asEnemy.Phase = "enemy";
            var enemy = asEnemy.Enemies[0];
            if (setup["stance"] != null && setup["stance"].Type != JTokenType.Null) enemy["combatStance"] = setup["stance"].DeepClone(); else enemy.Remove("combatStance");
            if (setup["concurrent"] != null) enemy["concurrentStances"] = setup["concurrent"].DeepClone();
            var rows = (JObject)((JObject)grid["rows"])[key];
            Assert.That(stance, Is.Not.Null);
            var bases = ((JArray)grid["bases"]).Select(b => b.Value<double>()).ToList();
            foreach (var cardRow in rows.Properties())
            {
                var def = CoreCosts.Resolve(c, new JObject { ["instanceId"] = "g", ["cardId"] = cardRow.Name, ["upgraded"] = false });
                var got = new JObject
                {
                    ["same"] = CoreStances.SameStance(c, c.Player, def),
                    ["chain"] = CoreStances.SpellChain(c, c.Player, def),
                    ["effectiveness"] = CoreActions.StanceEffectiveness(c, c.Player, def),
                    ["entry"] = CoreStances.EntryBonuses(c, def),
                    ["sp"] = new JArray(bases.Select(b => CoreStances.StaminaCost(c, b, c.Player, def, 0))),
                    ["spEnemyPhase"] = new JArray(bases.Select(b => CoreStances.StaminaCost(enemyPhase, b, enemyPhase.Player, def, 0))),
                    ["mp"] = new JArray(bases.Select(b => CoreStances.ManaCost(c, b, c.Player, def))),
                    ["enemy"] = new JArray(bases.Select(b => new JArray(new[] { 0d, 1d }.Select(m => CoreStances.StaminaCost(asEnemy, b, enemy, def, m))))),
                    ["enemySame"] = CoreStances.SameStance(asEnemy, enemy, def),
                };
                Same($"{key} {cardRow.Name}", cardRow.Value, got);
            }
        }

        /// <summary>D-214: the player's character level (the fight's characterLevel; the player entity carries no level) counts in the stance entry's Precision.</summary>
        [TestCase("0"), TestCase("1"), TestCase("4"), TestCase("9")]
        public void ThePlayerLevelFeedsTheStanceEntryPrecision(string level)
        {
            var grid = Read("grid.json");
            var fight = (JObject)grid["fight"];
            var rows = (JObject)((JObject)grid["levels"])[level];
            Assert.That(rows.Count, Is.EqualTo(ProfileCardIds().Count));
            var shortAtLevel = 0;
            foreach (var row in rows.Properties())
            {
                var c = Open(fight.Value<long>("seed"), fight.Value<string>("classId"), fight.Value<string>("encounterId"));
                c.Fields["characterLevel"] = double.Parse(level, System.Globalization.CultureInfo.InvariantCulture);
                c.Player.Remove("level");
                var instance = new JObject { ["instanceId"] = "g", ["cardId"] = row.Name, ["upgraded"] = false };
                var def = CoreCosts.Resolve(c, instance);
                var carrier = CoreCardPlay.Carrier(c, instance, def);
                var entry = CoreStances.EntryBonuses(c, def);
                var probe = Open(fight.Value<long>("seed"), fight.Value<string>("classId"), fight.Value<string>("encounterId"));
                probe.Fields["characterLevel"] = c.Fields["characterLevel"];
                probe.Player.Remove("level");
                CoreStances.SetProfileStance(probe, probe.Player, CoreCardPlay.Carrier(probe, instance, CoreCosts.Resolve(probe, instance)));
                var got = new JObject
                {
                    ["entry"] = entry, ["grant"] = CoreStances.PrecisionEntryGrant(c, c.Player, entry.Value<double>("precision"), carrier),
                    ["mix"] = CoreStances.PrecisionStatMix(c, c.Player, carrier),
                    ["precision"] = probe.Player["statuses"]["precision"]?["stacks"]?.DeepClone() ?? 0, ["evade"] = probe.Player["statuses"]["evade"]?["stacks"]?.DeepClone() ?? 0,
                };
                Same($"level {level} {row.Name}", row.Value, got);
                if (got.Value<double>("precision") > 0) shortAtLevel++;
            }
            Assert.That(shortAtLevel, Is.GreaterThan(0), "the level grid exercises entry grants");
        }

        /// <summary>The setup the oracle's grid lays over the fight for a stance name (Tools/oracle-core2-costs.mjs STANCES).</summary>
        private static JObject GridSetup(string name, JObject variantStatuses)
        {
            var stances = Read("grid.json")["stanceSetups"] as JObject;
            var setup = new JObject();
            var entry = (JObject)stances[name];
            setup["stance"] = entry["stance"]?.DeepClone() ?? JValue.CreateNull();
            if (entry["concurrent"] != null && entry["concurrent"].Type != JTokenType.Null) setup["concurrent"] = entry["concurrent"].DeepClone();
            var statuses = (JObject)variantStatuses.DeepClone();
            if (entry["statuses"] is JObject own) foreach (var s in own.Properties()) statuses[s.Name] = s.Value.DeepClone();
            setup["statuses"] = statuses;
            return setup;
        }

        [Test]
        public void ThePricesOfTheProfileCardsMatchTheWeb()
        {
            var prices = (JObject)Read("prices.json")["profileCards"];
            var c = Open(Read(FightFiles().First())["fights"][0].Value<long>("seed"), Read(FightFiles().First())["classId"].Value<string>(), Read(FightFiles().First())["fights"][0].Value<string>("encounterId"));
            var cards = Data.Content("cards").ToDictionary(card => card.Value<string>("id"), card => (JObject)card);
            foreach (var entry in prices.Properties())
            {
                var card = cards[entry.Name];
                var row = (JObject)entry.Value;
                Same(entry.Name + " authored", row["authored"], Derive(c, card, 0, null));
                var forced = Forced(card);
                var ranks = (JArray)row["forced"];
                for (var rank = 0; rank < ranks.Count; rank++) Same($"{entry.Name} forced rank {rank}", ranks[rank], Derive(c, forced, rank, null));
                if (row["upgraded"].Type != JTokenType.Null)
                {
                    var authored = (JObject)card.DeepClone();
                    authored["cost"] = card.Value<double>("cost") + 1;
                    Same(entry.Name + " upgraded", row["upgraded"], Derive(c, forced, 0, authored));
                }
            }
        }

        [Test]
        public void ThePricesOfTheEnemyCardsMatchTheWeb()
        {
            var prices = (JObject)Read("prices.json")["enemyCards"];
            var first = Read(FightFiles().First());
            var c = Open(first["fights"][0].Value<long>("seed"), first.Value<string>("classId"), first["fights"][0].Value<string>("encounterId"));
            var cards = Data.Content("enemyCards").ToDictionary(card => card.Value<string>("id"), card => (JObject)card);
            Assert.That(prices.Count, Is.EqualTo(cards.Count));
            foreach (var entry in prices.Properties())
            {
                var card = cards[entry.Name];
                var row = (JObject)entry.Value;
                Same(entry.Name + " authored", row["authored"], Derive(c, card, 0, null));
                var ranks = (JArray)row["forced"];
                for (var rank = 0; rank < ranks.Count; rank++) Same($"{entry.Name} forced rank {rank}", ranks[rank], Derive(c, Forced(card), rank, null));
            }
        }

        [Test]
        public void TheEnemyPoolsMatchTheWeb()
        {
            var prices = (JObject)Read("prices.json")["enemies"];
            var first = Read(FightFiles().First());
            var c = Open(first["fights"][0].Value<long>("seed"), first.Value<string>("classId"), first["fights"][0].Value<string>("encounterId"));
            foreach (var entry in prices.Properties())
            {
                var def = c.Data.Enemies.Get(entry.Name);
                var blighted = Js.Spread(def);
                blighted["blighted"] = true;
                var row = (JObject)entry.Value;
                Assert.That(CoreIntents.Pool(c, def), Is.EqualTo(row.Value<double>("pool")), entry.Name + " pool");
                Assert.That(CoreIntents.Pool(c, blighted), Is.EqualTo(row.Value<double>("blighted")), entry.Name + " blighted pool");
                Same(entry.Name + " blighted attributes", row["attributes"], CoreIntents.AttributesOf(c, blighted));
            }
        }

        private static JObject Forced(JObject card)
        {
            var copy = (JObject)card.DeepClone();
            copy.Remove("costRule");
            return copy;
        }

        private static JObject Derive(CombatState c, JObject card, double rank, JObject authored)
        {
            var applied = CoreActionCosts.Apply(c, card, authored, rank);
            var unchanged = ReferenceEquals(applied, card);
            var funds = CoreActionCosts.OwnRestoreFunds(c, applied);
            return new JObject
            {
                ["budget"] = CoreActionCosts.Budget(c, card, rank), ["optOut"] = CoreActionCosts.IsCostOptOut(card), ["unchanged"] = unchanged,
                ["cost"] = applied["cost"]?.DeepClone() ?? JValue.CreateNull(), ["manaCost"] = applied["manaCost"]?.DeepClone() ?? JValue.CreateNull(),
                ["effects"] = unchanged ? JValue.CreateNull() : applied["effects"]?.DeepClone() ?? JValue.CreateNull(),
                ["textTemplate"] = unchanged ? JValue.CreateNull() : applied["textTemplate"]?.DeepClone() ?? JValue.CreateNull(),
                ["funds"] = new JObject { ["stamina"] = funds.Stamina, ["mana"] = funds.Mana },
            };
        }

        [TestCaseSource(nameof(FightCases))]
        public void TheCostsOfAFightMatchTheWeb(string file, int index)
        {
            var fight = (JObject)((JArray)Read(file)["fights"])[index];
            var label = $"{file}[{index}] {fight.Value<string>("encounterId")}";
            CheckScenario(label + " opening", fight, (JObject)fight["opening"]);
            foreach (var variant in ((JObject)fight["openingVariants"]).Properties()) CheckScenario($"{label} opening/{variant.Name}", fight, (JObject)variant.Value);
            foreach (var variant in ((JObject)fight["variants"]).Properties()) CheckScenario($"{label} all/{variant.Name}", fight, (JObject)variant.Value);
        }

        [TestCaseSource(nameof(FightCases))]
        public void ChainsOfPlaysEnterTheStancesTheWebEnters(string file, int index)
        {
            var fight = (JObject)((JArray)Read(file)["fights"])[index];
            var chains = (JArray)fight["chains"];
            for (var k = 0; k < chains.Count; k++)
            {
                var chain = (JObject)chains[k];
                var label = $"{file}[{index}] {fight.Value<string>("encounterId")} chain {k}";
                var c = Apply(Open(fight.Value<long>("seed"), fight.Value<string>("classId"), fight.Value<string>("encounterId")), (JObject)chain["start"]["setup"]);
                Same(label + " start", chain["start"]["before"], PlayerState(c));
                Same(label + " start hand", chain["start"]["hand"], HandCosts(c));
                var n = 0;
                foreach (var step in ((JArray)chain["steps"]).OfType<JObject>())
                {
                    var stepLabel = $"{label} play {n++} {step.Value<string>("instanceId")}";
                    var mark = c.EventLog.Count;
                    var entry = CoreCardPlay.Enter(c, step.Value<string>("instanceId"));
                    var play = (JObject)step["play"];
                    Same(stepLabel + " paid", play["paid"], entry.Paid);
                    Assert.That(entry.CardId, Is.EqualTo(play.Value<string>("cardId")), stepLabel);
                    Same(stepLabel + " carrier", play["carrier"], CarrierView(entry.Carrier));
                    var profile = (JObject)entry.Carrier["combatProfile"].DeepClone();
                    profile.Remove("damageType");
                    profile.Remove("damageComponents");
                    Same(stepLabel + " profile", play["carrierProfile"], profile);
                    Same(stepLabel + " events", play["events"], new JArray(c.EventLog.Skip(mark).Select(e => e.Value<string>("type") == "combatStanceChanged"
                        ? (JToken)new JObject { ["type"] = "combatStanceChanged", ["maneuver"] = e["maneuver"] ?? e["payload"]?["maneuver"] } : e["type"])));
                    Same(stepLabel + " after", step["after"], PlayerState(c));
                    Same(stepLabel + " hand", step["hand"], HandCosts(c));
                }
            }
        }

        private static readonly string[] CarrierKeys = { "type", "tags", "authoredTags", "abilityKind", "stanceTrigger", "mirrors", "evade", "range", "appliedStatuses", "appliedStatusEffects",
            "appliedBuildupStatuses", "fixCoreSameStance", "traits", "counterCoverage", "targeting", "projectile" };

        private static JObject CarrierView(JObject carrier)
        {
            var view = new JObject();
            foreach (var key in CarrierKeys) if (carrier[key] != null) view[key] = carrier[key].DeepClone();
            return view;
        }

        private static JObject HandCosts(CombatState c)
        {
            var hand = new JObject();
            foreach (var card in c.Piles.Hand) hand[card.Value<string>("instanceId")] = CostsOf(c, card.Value<string>("instanceId"));
            return hand;
        }
    }
}
