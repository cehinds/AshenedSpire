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
    /// WP-4 (D-180): Core 2 combat start, hand and resources. Every fight recorded from the web's createRunCombat for the configured
    /// profile (Tools/oracle-core2-start.mjs: 4 classes x every encounter) is built again by <see cref="CoreRunCombat.Create"/> and must
    /// equal the web's combat object field for field the instant it opens (turn 1: Stamina full, hand drawn, first intents rolled, the
    /// event types in order, every RNG counter), and again after each end-turn step (every enemy staggered, so the enemy phase runs in
    /// full without an action: the status decay, the round shuffle of the whole deck, the intents re-rolled, the next hand drawn). The
    /// hand scenarios drive the hand functions on prepared piles. Rule snapshots are compared once per class. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2CombatStartParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "start");

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

        public static IEnumerable<string> StartFiles() =>
            Directory.Exists(Dir) ? Directory.GetFiles(Dir, "start-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal) : Enumerable.Empty<string>();

        private static IEnumerable<TestCaseData> Fights(string prefix)
        {
            foreach (var file in StartFiles())
            {
                var count = ((JArray)Read(file)["fights"]).Count;
                for (var i = 0; i < count; i++) yield return new TestCaseData(file, i).SetName(prefix + Path.GetFileNameWithoutExtension(file) + "_" + i);
            }
        }

        public static IEnumerable<TestCaseData> OpenCases() => Fights("Core2Open_");

        public static IEnumerable<TestCaseData> LoopCases() => Fights("Core2Loop_");

        public static IEnumerable<TestCaseData> HandCases()
        {
            if (!File.Exists(Path.Combine(Dir, "hand.json"))) yield break;
            var count = ((JArray)Read("hand.json")["scenarios"]).Count;
            for (var i = 0; i < count; i++) yield return new TestCaseData(i).SetName("Core2Hand_" + i);
        }

        // ------------------------------------------------------------------ the fight

        private static readonly string[] Statics = { "fixCoreRules", "combatExpansionRules", "reactionRules", "combatMatchupRules", "combatIntentRules" };

        private static CombatState Open(JObject fight, out JObject run)
        {
            var seed = (uint)fight["seed"].Value<long>();
            run = CoreRunRules.Create(Data.Run, seed, fight.Value<string>("classId"), CoreRunOptions.FromJson(null, Unlocked));
            return CoreRunCombat.Create(Data, new Rng(seed), run, fight.Value<string>("encounterId"));
        }

        /// <summary>The combat as the oracle projects it (Tools/oracle-core2-start.mjs projectFull): every field the fight carries but its rule snapshots and registries.</summary>
        private static JObject Project(CombatState c)
        {
            var o = new JObject();
            foreach (var p in c.Fields.Properties())
                if (!Statics.Contains(p.Name)) o[p.Name] = p.Value.DeepClone();
            if (o["enemyKnowledge"] is JObject knowledge) { knowledge.Remove("rules"); }
            o["attributes"] = c.Attributes?.DeepClone() ?? JValue.CreateNull();
            o["coreTags"] = c.CoreTags.DeepClone();
            if (c.DrawPerTurn != null) o["drawPerTurn"] = c.DrawPerTurn.DeepClone();
            o["enemies"] = new JArray(c.Enemies.Select(e => e.DeepClone()));
            if (c.EquipmentAttackSlotCount != null) o["equipmentAttackSlotCount"] = c.EquipmentAttackSlotCount.DeepClone();
            o["equipmentChanged"] = c.EquipmentChanged;
            o["equipmentPoolDeficits"] = c.EquipmentPoolDeficits.DeepClone();
            o["handMax"] = c.HandMax;
            if (c.HandRules != null)
            {
                o["handRules"] = c.HandRules.DeepClone();
                o["pendingDiscardDraw"] = c.PendingDiscardDraw;
            }
            o["itemUpgradeLevels"] = c.ItemUpgradeLevels.DeepClone();
            o["phase"] = c.Phase;
            o["piles"] = new JObject
            {
                ["draw"] = new JArray(c.Piles.Draw.Select(x => x.DeepClone())),
                ["hand"] = new JArray(c.Piles.Hand.Select(x => x.DeepClone())),
                ["discard"] = new JArray(c.Piles.Discard.Select(x => x.DeepClone())),
                ["exhaust"] = new JArray(c.Piles.Exhaust.Select(x => x.DeepClone())),
            };
            // Actions is Stamina (bindTurnStamina): the web's entity reads energy and energyMax through the pool.
            var player = (JObject)c.Player.DeepClone();
            player["energy"] = player["stamina"];
            player["energyMax"] = player["maxStamina"];
            o["player"] = player;
            var mounts = new JObject();
            if (c.PropertyMounts != null)
                foreach (var owner in c.PropertyMounts.Entries())
                {
                    var sources = new JObject();
                    foreach (var source in owner.Value.Entries())
                    {
                        var m = source.Value;
                        var row = new JObject
                        {
                            ["kind"] = m.Kind, ["id"] = m.Id, ["instanceId"] = m.InstanceId,
                            ["scopeTags"] = new JArray(m.ScopeTags), ["ruleTags"] = new JArray(m.Rules.Select(r => r["tag"]?.DeepClone())),
                        };
                        sources[source.Key] = row;
                    }
                    mounts[owner.Key] = sources;
                }
            o["propertyMounts"] = mounts;
            o["queue"] = c.Queue.Count;
            o["removedAttackSlotIds"] = c.RemovedAttackSlotIds?.DeepClone() ?? new JArray();
            o["result"] = c.Result == null ? JValue.CreateNull() : (JToken)c.Result;
            var rng = new JObject();
            foreach (var pair in c.Rng.AllCounters())
                if (pair.Key != "combatRounds" || pair.Value != 0) rng[pair.Key] = pair.Value;
            o["rng"] = rng;
            o["skillXp"] = c.SkillXp.DeepClone();
            o["skills"] = c.Skills.DeepClone();
            o["swapCostRule"] = c.SwapCostRule?.DeepClone() ?? JValue.CreateNull();
            o["swapsLeft"] = c.SwapsLeft;
            var gates = new JObject();
            foreach (var gate in c.TriggerState.Entries().OrderBy(e => e.Key, StringComparer.Ordinal))
                gates[gate.Key] = new JObject { ["fires"] = gate.Value.Fires, ["turn"] = gate.Value.Turn, ["turnFires"] = gate.Value.TurnFires };
            o["triggerState"] = gates;
            o["turn"] = c.Turn;
            return o;
        }

        private static List<string> Events(CombatState c, int mark) => c.EventLog.Skip(mark).Select(e => e.Value<string>("type")).ToList();

        /// <summary>The oracle's delta encoding, applied: objects key by key, equal-length arrays by index, anything else replaced.</summary>
        private static JToken Apply(JToken previous, JToken delta)
        {
            if (!(delta is JObject d)) return delta.DeepClone();
            if (d.Property("$") != null) return d["$"].DeepClone();
            if (d.Property("$a") != null)
            {
                var array = (JArray)previous.DeepClone();
                foreach (var p in ((JObject)d["$a"]).Properties()) array[int.Parse(p.Name)] = Apply(array[int.Parse(p.Name)], p.Value);
                return array;
            }
            var result = (JObject)previous.DeepClone();
            foreach (var p in d.Properties())
            {
                if (p.Value is JObject marker && marker.Property("$del") != null) result.Remove(p.Name);
                else result[p.Name] = Apply(result[p.Name], p.Value);
            }
            return result;
        }

        private static void Same(string label, JToken want, JToken got)
        {
            var diff = RunParityTests.FirstDifference(want, got, "$");
            if (diff != null) Assert.Fail($"{label}: first difference at {diff}");
        }

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Read("index.json");
            Assert.That(StartFiles().Count(), Is.EqualTo(((JArray)index["files"]).Count));
            foreach (var classId in Data.Run.Run.Classes.Ids) Assert.That(StartFiles(), Does.Contain("start-" + classId + ".json"));
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Data.Run.ProfileHash), "the oracle ran the profile that is loaded");
        }

        [TestCaseSource(nameof(OpenCases))]
        public void TheFightOpensAsTheWebOpensIt(string file, int index)
        {
            var fight = (JObject)((JArray)Read(file)["fights"])[index];
            var c = Open(fight, out _);
            var label = $"{file}[{index}] {fight.Value<string>("encounterId")}";
            Same(label + " start", fight["start"]["state"], Project(c));
            Assert.That(Events(c, 0), Is.EqualTo(fight["start"]["events"].Select(t => t.Value<string>()).ToList()), label + " start events");
            if (index == 0)
            {
                var statics = (JObject)fight["statics"];
                foreach (var name in Statics) Same(label + " " + name, statics[name], c.Fields[name]);
                Same(label + " enemyKnowledgeRules", statics["enemyKnowledgeRules"], c.Fields.Obj("enemyKnowledge")["rules"]);
            }
        }

        [TestCaseSource(nameof(LoopCases))]
        public void TheEndTurnLoopMatchesTheWeb(string file, int index)
        {
            var fight = (JObject)((JArray)Read(file)["fights"])[index];
            var c = Open(fight, out _);
            JToken want = fight["start"]["state"].DeepClone();
            var n = 0;
            foreach (var step in ((JArray)fight["steps"]).OfType<JObject>())
            {
                var label = $"{file}[{index}] {fight.Value<string>("encounterId")} step {n++}";
                var mark = c.EventLog.Count;
                foreach (var enemy in c.Enemies.Where(e => e.Value<bool>("alive"))) enemy["skipNextTurn"] = true;
                Assert.That(c.Enemies.Count(e => e.Value<bool>("alive")), Is.EqualTo(step.Value<int>("staggered")), label + " staggered");
                CoreTurns.EndTurn(c, new string[0]);
                want = Apply(want, step["delta"]);
                Same(label, want, Project(c));
                Assert.That(Events(c, mark), Is.EqualTo(step["events"].Select(t => t.Value<string>()).ToList()), label + " events");
            }
        }

        // ------------------------------------------------------------------ the hand

        [TestCaseSource(nameof(HandCases))]
        public void TheHandFunctionsMatchTheWeb(int index)
        {
            var scenario = (JObject)((JArray)Read("hand.json")["scenarios"])[index];
            var c = Open(scenario, out _);
            var label = $"hand[{index}] {scenario.Value<string>("classId")} {scenario.Value<string>("encounterId")}";
            // The oracle's setup: some of the hand played, part of the draw pile left, a card exhausted, a turn number.
            var setup = (JObject)scenario["setup"];
            var pick = BotLcg((uint)scenario["seed"].Value<long>() ^ 0x51ed270bu);
            Assert.That(c.Piles.Hand.Count + c.Piles.Draw.Count + c.Piles.Discard.Count, Is.EqualTo(scenario.Value<int>("cards")), label + " cards");
            var toDiscard = pick(c.Piles.Hand.Count + 1);
            for (var i = 0; i < toDiscard; i++)
            {
                var at = pick(c.Piles.Hand.Count);
                c.Piles.Discard.Add(c.Piles.Hand[at]);
                c.Piles.Hand.RemoveAt(at);
            }
            var keep = pick(c.Piles.Draw.Count + 1);
            while (c.Piles.Draw.Count > keep)
            {
                c.Piles.Discard.Add(c.Piles.Draw[c.Piles.Draw.Count - 1]);
                c.Piles.Draw.RemoveAt(c.Piles.Draw.Count - 1);
            }
            var exhaust = pick(3) == 0 ? 1 : 0;
            if (exhaust == 1 && c.Piles.Discard.Count > 0)
            {
                var at = pick(c.Piles.Discard.Count);
                c.Piles.Exhaust.Add(c.Piles.Discard[at]);
                c.Piles.Discard.RemoveAt(at);
            }
            c.Turn = 1 + pick(4);
            Assert.That(new[] { toDiscard, keep, exhaust, (int)c.Turn }, Is.EqualTo(new[] { setup.Value<int>("toDiscard"), setup.Value<int>("drawKeep"), setup.Value<int>("exhaust"), setup.Value<int>("turn") }), label + " setup");
            JToken want = scenario["before"];
            Same(label + " before", want, Project(c));

            var mark = c.EventLog.Count;
            var draws = pick(8);
            CoreHand.Draw(c, draws);
            var draw = (JObject)scenario["draw"];
            Assert.That(draws, Is.EqualTo(draw.Value<int>("n")), label + " draws");
            want = Apply(want, draw["delta"]);
            Same(label + " draw", want, Project(c));
            Assert.That(Events(c, mark), Is.EqualTo(draw["events"].Select(t => t.Value<string>()).ToList()), label + " draw events");

            var fates = ((JArray)scenario["refresh"]["fates"]).OfType<JObject>().ToList();
            Assert.That(c.Piles.Hand.Select(x => x.Value<string>("instanceId")).ToList(), Is.EqualTo(fates.Select(f => f.Value<string>("id")).ToList()), label + " hand");
            var kept = new List<JObject>();
            var discarded = new List<JObject>();
            var exhausted = new List<JObject>();
            for (var i = 0; i < c.Piles.Hand.Count; i++)
            {
                var card = c.Piles.Hand[i];
                var fate = CoreHand.EndTurnFate(c, card);
                Assert.That(fate, Is.EqualTo(fates[i].Value<string>("fate")), label + " fate " + i);
                (fate == "keep" ? kept : fate == "exhaust" ? exhausted : discarded).Add(card);
            }
            c.Piles.Hand = kept;
            foreach (var card in exhausted) c.Piles.Exhaust.Add(card);
            mark = c.EventLog.Count;
            CoreHand.ReturnUnplayed(c, discarded);
            var refresh = (JObject)scenario["refresh"];
            want = Apply(want, refresh["delta"]);
            Same(label + " refresh", want, Project(c));
            Assert.That(Events(c, mark), Is.EqualTo(refresh["events"].Select(t => t.Value<string>()).ToList()), label + " refresh events");

            c.Turn = setup.Value<int>("turn") + 1;
            var plan = CoreHand.Plan(c);
            var count = CoreHand.TurnDrawCount(c);
            var next = (JObject)scenario["next"];
            Assert.That(count, Is.EqualTo(next.Value<double>("count")), label + " count");
            Assert.That(new[] { plan.Minimum, plan.Maximum, (double)plan.Cards.Count }, Is.EqualTo(new[] { next["plan"].Value<double>("minimum"), next["plan"].Value<double>("maximum"), next["plan"].Value<double>("cards") }), label + " plan");
            mark = c.EventLog.Count;
            CoreHand.Draw(c, count);
            want = Apply(want, next["delta"]);
            Same(label + " next", want, Project(c));
            Assert.That(Events(c, mark), Is.EqualTo(next["events"].Select(t => t.Value<string>()).ToList()), label + " next events");
        }

        /// <summary>The oracle's botLcg(seed): a small deterministic picker (Tools/oracle-core2.mjs).</summary>
        private static Func<int, int> BotLcg(uint seed)
        {
            var state = seed != 0 ? seed : 1u;
            return n =>
            {
                state = unchecked(state * 1664525u + 1013904223u);
                return n <= 1 ? 0 : (int)((state >> 8) % (uint)n);
            };
        }
    }
}
