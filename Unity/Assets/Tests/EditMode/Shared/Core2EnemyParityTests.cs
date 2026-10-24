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
    /// WP-11 (D-260): the enemies' AI, their reactions and what the player knows of them. Everything recorded by Tools/oracle-core2-enemy.mjs from the
    /// shipped web is played again on the C# port: every enemy of the profile on a fight of its own (the move table after the projection, the SP pool and
    /// the price of every move under every stance, the expanded move, its carrier, its intent box, previewEnemyAction, the reaction moves, and 200 rolls of
    /// the weighted pick per seed with the plan and the enemyAI counter after each), the reaction decision on every window, incoming amount and state, the
    /// knowledge ledger, the intent read of a fight without a knowledge state, the fights (the player predicts the intents it could not read; the enemies
    /// charge delayed moves and react to the cards and the reactions of the player) and the declared exchanges. After every step the whole combat must
    /// equal the web's as a delta, the event types in order, the payload of every reaction, Counter, move and knowledge event, the offer the step paused
    /// on and every RNG counter. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2EnemyParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "enemy");

        private static readonly Dictionary<string, JObject> Files = new Dictionary<string, JObject>();

        internal static JObject Read(string name)
        {
            lock (Files)
            {
                if (!Files.TryGetValue(name, out var doc)) Files[name] = doc = (JObject)Ashen.Content.JsonContent.Parse(File.ReadAllText(Path.Combine(Dir, name)));
                return doc;
            }
        }

        private static JArray Rows(string file, string key) => (JArray)Read(file)[key];

        // ------------------------------------------------------------------ opening a fight

        /// <summary>The fight a record names: the run (patched), the cards laid over it, a solo encounter of the fight's own, the knowledge ledger, the patch merged over the fighters.</summary>
        internal static CombatState Open(JObject spec) => Open(spec, out _);

        internal static CombatState Open(JObject spec, out JObject run)
        {
            var data = Core2DamageParityTests.Data;
            var seed = (uint)spec["seed"].Value<long>();
            run = CoreRunRules.Create(data.Run, seed, spec.Value<string>("classId"), CoreRunOptions.FromJson(null, Core2DamageParityTests.Unlocked));
            if (spec["runPatch"] is JObject runPatch) Core2DamageParityTests.Merge(run, runPatch);
            if (spec["knowledge"] is JObject knowledge)
            {
                run["enemyKnowledgeRules"] = data.EnemyKnowledgeRules.DeepClone();
                CoreKnowledgeLedger.OpenRun(run, knowledge.Value<string>("receiptId"), knowledge.Value<bool?>("bankable") != false);
            }
            if (spec["deckRemove"] is JArray remove)
            {
                var ids = remove.Select(t => t.Value<string>()).ToList();
                foreach (var card in ((JArray)run["deck"]).OfType<JObject>().Where(card => ids.Contains(card.Value<string>("cardId"))).ToList()) card.Remove();
            }
            foreach (var add in (spec["deckAdd"] as JArray ?? new JArray()).OfType<JObject>())
                ((JArray)run["deck"]).Add(new JObject { ["instanceId"] = add["instanceId"].DeepClone(), ["cardId"] = add["cardId"].DeepClone(), ["upgraded"] = false });
            var enemyIds = (spec["enemyIds"] as JArray)?.Select(t => t.Value<string>()).ToList();
            var c = CoreRunCombat.Create(data, new Rng(seed), run, enemyIds != null ? FirstEncounter(data) : spec.Value<string>("encounterId"), null, enemyIds);
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

        private static string FirstEncounter(Ashen.Domain.Combat.CoreCombatData data) => data.Run.Run.Encounters.Ids.First();

        // ------------------------------------------------------------------ the oracle

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Read("index.json");
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Core2DamageParityTests.Data.Run.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(Rows("enemies.json", "enemies").Count, Is.EqualTo(Core2DamageParityTests.Data.Combat.Enemies.Ids.Count()), "every enemy of the profile");
            Assert.That(index["enemies"].Value<int>("picks"), Is.EqualTo(33000));
        }

        // ------------------------------------------------------------------ every enemy, on a fight of its own

        public static IEnumerable<TestCaseData> EnemyCases()
        {
            if (!File.Exists(Path.Combine(Dir, "enemies.json"))) yield break;
            var rows = Rows("enemies.json", "enemies");
            for (var i = 0; i < rows.Count; i++) yield return new TestCaseData(i).SetName("Core2Enemy_" + rows[i].Value<string>("id"));
        }

        private static JObject Clone(JObject o) => (JObject)o.DeepClone();

        private static JObject WithStatus(JObject enemy, string status, int stacks)
        {
            var copy = Clone(enemy);
            var statuses = copy["statuses"] as JObject ?? new JObject();
            statuses[status] = new JObject { ["stacks"] = stacks };
            copy["statuses"] = statuses;
            return copy;
        }

        private static JObject WithStance(JObject enemy, string maneuver)
        {
            var copy = Clone(enemy);
            if (maneuver == "none") copy.Remove("combatStance");
            else copy["combatStance"] = new JObject { ["maneuver"] = maneuver };
            return copy;
        }

        [TestCaseSource(nameof(EnemyCases))]
        public void TheEnemyIsTheWebs(int index)
        {
            var rec = (JObject)Rows("enemies.json", "enemies")[index];
            var id = rec.Value<string>("id");
            var c = Open((JObject)rec["spec"]);
            var enemy = c.Enemies[0];
            var def = c.Data.Enemies.Get(id);
            Assert.That(CoreIntents.Pool(c, def), Is.EqualTo(rec.Value<double>("pool")), id + " pool");
            Assert.That(def.Properties().Select(p => p.Name).Contains("firstMove") ? def.Value<string>("firstMove") : null, Is.EqualTo(rec.Value<string>("firstMove")), id + " firstMove");
            Assert.That(def.Value<string>("rangeClosingMove"), Is.EqualTo(rec.Value<string>("rangeClosingMove")), id + " rangeClosingMove");
            Assert.That(def.Value<string>("startingRange"), Is.EqualTo(rec.Value<string>("startingRange")), id + " startingRange");
            var moveIds = def["moves"].Children<JProperty>().Select(p => p.Name).ToList();
            Core2DamageParityTests.Same(id + " move ids", rec["moveIds"], new JArray(moveIds));
            Core2DamageParityTests.Same(id + " unlockable", rec["unlockable"], new JArray(moveIds.Where(m => def["moves"][m].Value<bool?>("locked") == true)));
            var silenced = WithStatus(enemy, "silenced", 2);
            var grounded = WithStatus(enemy, "grounded", 1);
            foreach (var row in ((JArray)rec["moves"]).OfType<JObject>())
            {
                var moveId = row.Value<string>("id");
                var at = id + "." + moveId;
                var move = (JObject)def["moves"][moveId];
                Assert.That(CoreIntents.MoveSp(c, move, enemy, moveId), Is.EqualTo(row.Value<double>("sp")), at + " sp");
                Assert.That(CoreIntents.MoveSp(c, move, silenced, moveId), Is.EqualTo(row.Value<double>("spSilenced")), at + " sp silenced");
                foreach (var stance in ((JObject)row["stanceCosts"]).Properties())
                    Assert.That(CoreIntents.MoveSp(c, move, WithStance(enemy, stance.Name), moveId), Is.EqualTo(stance.Value.Value<double>()), at + " sp under " + stance.Name);
                Core2DamageParityTests.Same(at + " damage", row["damage"], CoreIntents.MoveDamage(enemy, move) ?? JValue.CreateNull());
                Core2DamageParityTests.Same(at + " expanded", row["expanded"], CoreIntents.Expanded(c, enemy, move, moveId));
                Core2DamageParityTests.Same(at + " carrier", row["carrier"], CoreIntents.Carrier(c, enemy, move, moveId));
                Core2DamageParityTests.Same(at + " carrier grounded", row["carrierGrounded"], CoreIntents.Carrier(c, grounded, move, moveId)["combatProfile"]);
                Core2DamageParityTests.Same(at + " intent", row["intent"], CoreIntents.BuildIntent(c, move, moveId, enemy));
                Core2DamageParityTests.Same(at + " preview", row["preview"], CoreEnemyPreview.Preview(c, enemy, moveId));
                Core2DamageParityTests.Same(at + " preview silenced", row["previewSilenced"], CoreEnemyPreview.Preview(c, silenced, moveId));
            }
            foreach (var window in ((JObject)rec["reactions"]).Properties())
                Core2DamageParityTests.Same(id + " reaction moves " + window.Name, window.Value, new JArray(CoreReactions.ReactionMovesOf(c, enemy, window.Name).Select(row => (JToken)row.Key)));
            var start = Core2CombatStartParityTests.Project(c);
            Core2DamageParityTests.Same(id + " first intent", rec["intent"], start["enemies"][0]["intent"]);
            Core2DamageParityTests.Same(id + " first plan", rec["plannedMoves"], start["enemies"][0]["plannedMoves"]);
            Core2DamageParityTests.Same(id + " knowledge", rec["enemyKnowledge"], start["enemyKnowledge"]);
        }

        public static IEnumerable<TestCaseData> PickCases()
        {
            if (!File.Exists(Path.Combine(Dir, "enemies.json"))) yield break;
            var rows = Rows("enemies.json", "enemies");
            for (var i = 0; i < rows.Count; i++)
                for (var n = 0; n < ((JArray)rows[i]["picks"]).Count; n++)
                    yield return new TestCaseData(i, n).SetName("Core2Picks_" + rows[i].Value<string>("id") + "_" + n);
        }

        /// <summary>200 rolls of the plan: the move each takes, the plan left, the history and the enemyAI counter.</summary>
        [TestCaseSource(nameof(PickCases))]
        public void TheWeightedPickIsTheWebs(int index, int n)
        {
            var rec = (JObject)Rows("enemies.json", "enemies")[index];
            var id = rec.Value<string>("id");
            var pick = (JObject)((JArray)rec["picks"])[n];
            var c = Open(new JObject { ["seed"] = pick["seed"], ["classId"] = pick["classId"], ["enemyIds"] = new JArray(id) });
            var enemy = c.Enemies[0];
            var def = c.Data.Enemies.Get(id);
            if (pick.Value<bool>("unlock"))
                enemy["unlockedMoves"] = new JArray(((JArray)rec["unlockable"]).Select(t => t.DeepClone()));
            Assert.That(c.Rng.AllCounters().TryGetValue("enemyAI", out var before) ? before : 0u, Is.EqualTo(pick.Value<uint>("startEnemyAI")), id + " enemyAI at the start");
            var rolls = (JArray)pick["rolls"];
            for (var i = 0; i < rolls.Count; i++)
            {
                var roll = (JObject)rolls[i];
                var taken = CoreIntents.TakePlannedMove(c, enemy, def, i == 0);
                Assert.That(taken, Is.EqualTo(roll.Value<string>("moveId")), $"{id} pick {n} roll {i} move");
                Core2DamageParityTests.Same($"{id} pick {n} roll {i} plan", roll["plan"], enemy["plannedMoves"]);
            }
            Assert.That(enemy.Arr("movesHistory").Count, Is.EqualTo(pick.Value<int>("history")), id + " history");
            Assert.That(c.Rng.AllCounters().TryGetValue("enemyAI", out var after) ? after : 0u, Is.EqualTo(pick.Value<uint>("enemyAI")), id + " enemyAI after");
        }

        // ------------------------------------------------------------------ units: the reaction decision

        public static IEnumerable<TestCaseData> ReactionUnitCases()
        {
            if (!File.Exists(Path.Combine(Dir, "units.json"))) yield break;
            var rows = Rows("units.json", "reactions");
            for (var i = 0; i < rows.Count; i++) yield return new TestCaseData(i).SetName("Core2EnemyReaction_" + rows[i].Value<string>("enemyId") + "_" + rows[i].Value<string>("variant"));
        }

        private static JToken RollsOf(CombatState c) => c.Fields[Ashen.Generated.Core2CombatKeys.EnemyReactionRolls]?.DeepClone() ?? JValue.CreateNull();

        private static uint Counter(CombatState c, string stream) => c.Rng.AllCounters().TryGetValue(stream, out var n) ? n : 0u;

        /// <summary>decideEnemyReaction on every window and incoming amount, in sequence on one fight: the move, the draws on the enemyReaction stream and the memo.</summary>
        [TestCaseSource(nameof(ReactionUnitCases))]
        public void TheReactionDecisionIsTheWebs(int index)
        {
            var unit = (JObject)Rows("units.json", "reactions")[index];
            var c = Open((JObject)unit["spec"]);
            var enemy = c.Enemies[0];
            Core2DamageParityTests.Merge(enemy, (JObject)unit["patch"]);
            var n = 0;
            foreach (var call in ((JArray)unit["calls"]).OfType<JObject>())
            {
                var at = $"{unit.Value<string>("enemyId")} {unit.Value<string>("variant")} call {n++} {call.Value<string>("window")} incoming {call.Value<int>("incoming")}";
                c.ReactionPlaying = call.Value<bool?>("playing") == true;
                var result = CoreReactions.DecideEnemyReaction(c, enemy, call.Value<string>("window"), call.Value<string>("groupKey"), call.Value<double>("windowIndex"), call.Value<double>("incoming"));
                c.ReactionPlaying = false;
                Assert.That(result, Is.EqualTo(call.Value<string>("result")), at + " move");
                Assert.That(Counter(c, "enemyReaction"), Is.EqualTo(call.Value<uint>("enemyReaction")), at + " enemyReaction draws");
                Core2DamageParityTests.Same(at + " memo", call["rolls"], RollsOf(c));
            }
            var reacts = (JObject)unit["fightReacts"];
            Assert.That(CoreReactions.FightReacts(c), Is.EqualTo(reacts.Value<bool>("any")), "fightReacts");
            Assert.That(CoreReactions.FightReacts(c, "playerAction"), Is.EqualTo(reacts.Value<bool>("playerAction")), "fightReacts playerAction");
            Assert.That(CoreReactions.FightReacts(c, "playerReaction"), Is.EqualTo(reacts.Value<bool>("playerReaction")), "fightReacts playerReaction");
        }

        // ------------------------------------------------------------------ units: the knowledge ledger

        public static IEnumerable<TestCaseData> LedgerUnitCases()
        {
            if (!File.Exists(Path.Combine(Dir, "units.json"))) yield break;
            var rows = Rows("units.json", "ledger");
            for (var i = 0; i < rows.Count; i++) yield return new TestCaseData(i).SetName("Core2KnowledgeLedger_" + i + "_" + rows[i].Value<string>("kind"));
        }

        private static JToken Outcome(Func<JToken> fn)
        {
            try { return new JObject { ["value"] = fn() ?? JValue.CreateNull() }; }
            catch (InvalidOperationException e) { return new JObject { ["error"] = e.Message }; }
        }

        private static JObject Snap(JObject run) => new JObject { ["state"] = run["enemyKnowledgeState"]?.DeepClone(), ["skills"] = run["skills"]?.DeepClone() };

        [TestCaseSource(nameof(LedgerUnitCases))]
        public void TheKnowledgeLedgerIsTheWebs(int index)
        {
            var unit = (JObject)Rows("units.json", "ledger")[index];
            var kind = unit.Value<string>("kind");
            var rules = Core2DamageParityTests.Data.EnemyKnowledgeRules;
            var label = "ledger[" + index + "] " + kind;
            switch (kind)
            {
                case "target":
                    Assert.That(CoreKnowledgeLedger.MasteryTarget(rules, unit.Value<string>("enemyId")), Is.EqualTo(unit.Value<double>("value")), label);
                    break;
                case "category":
                {
                    var intent = new JObject { ["kind"] = unit.Value<string>("intentKind") };
                    Assert.That(CoreKnowledge.Category(intent, (JObject)unit["profile"], unit.Value<bool>("charging")), Is.EqualTo(unit.Value<string>("value")), label + " " + intent["kind"] + " " + unit["profile"]);
                    break;
                }
                case "label":
                    Assert.That(CoreKnowledge.BroadLabel(unit.Value<string>("category")), Is.EqualTo(unit.Value<string>("value")), label);
                    break;
                case "merge":
                {
                    var incoming = (JObject)unit["incoming"];
                    var current = (JObject)unit["current"];
                    var outcome = Outcome(() => CoreKnowledgeLedger.Merge(incoming, current));
                    var want = new JObject();
                    if (unit["value"] != null) want["value"] = unit["value"];
                    if (unit["error"] != null) want["error"] = unit["error"];
                    Core2DamageParityTests.Same(label, want, outcome);
                    if (unit["points"] is JObject points)
                        foreach (var pair in current.Value<JObject>("enemies").Properties())
                        {
                            Assert.That(CoreKnowledgeLedger.Points((JObject)pair.Value), Is.EqualTo(points.Value<double>(pair.Name)), label + " points " + pair.Name);
                            Core2DamageParityTests.Same(label + " progress " + pair.Name, unit["progress"][pair.Name], CoreKnowledgeLedger.Progress((JObject)pair.Value, rules.Obj("bestiary").Num("encountersToMaster")));
                        }
                    break;
                }
                case "open":
                {
                    var run = new JObject { ["enemyKnowledgeRules"] = unit["rules"] is JObject r ? r.DeepClone() : rules.DeepClone() };
                    var outcome = Outcome(() => CoreKnowledgeLedger.OpenRun(run, unit.Value<string>("receiptId") ?? (unit["receiptId"] == null ? "x" : null), unit.Value<bool?>("bankable") ?? true));
                    var want = new JObject();
                    if (unit["value"] != null) want["value"] = unit["value"];
                    if (unit["error"] != null) want["error"] = unit["error"];
                    Core2DamageParityTests.Same(label, want, outcome);
                    break;
                }
                case "run":
                    ReplayRun(unit, rules, label);
                    break;
                default:
                    Assert.Fail("unknown ledger unit " + kind);
                    break;
            }
        }

        private static void ReplayRun(JObject unit, JObject rules, string label)
        {
            var run = new JObject { ["enemyKnowledgeRules"] = rules.DeepClone(), ["skills"] = new JObject() };
            var opened = CoreKnowledgeLedger.OpenRun(run, unit.Value<string>("receiptId"), unit.Value<bool>("bankable"));
            Assert.That(opened, Is.EqualTo(unit.Value<bool>("opened")), label + " opened");
            var n = 0;
            foreach (var entry in ((JArray)unit["log"]).OfType<JObject>())
            {
                var op = entry.Value<string>("op");
                var at = label + " step " + n++ + " " + op;
                var args = (JObject)entry["args"];
                Core2DamageParityTests.Same(at + " before", entry["before"], Snap(run));
                JToken outcome;
                switch (op)
                {
                    case "open":
                        outcome = Outcome(() => CoreKnowledgeLedger.OpenEncounter(run, args.Value<string>("nodeId"), args.Value<string>("encounterId"), args["enemyIds"].Select(t => t.Value<string>()).ToList(), (JObject)args["profile"]));
                        break;
                    case "bonus":
                        outcome = Outcome(() =>
                        {
                            var bonus = CoreKnowledgeLedger.AddCounterBonus(run["enemyKnowledgeState"].Value<JObject>("pending"), (JObject)args["encounter"], args.Value<string>("enemyId"), args.Value<double>("target"), (JObject)args["current"]);
                            run["enemyKnowledgeState"]["pending"] = bonus;
                            return bonus;
                        });
                        break;
                    case "reconcile":
                        outcome = Outcome(() => CoreKnowledgeLedger.ReconcilePerception(run, args.Value<string>("encounterId"), args.Value<double>("earnedXp")));
                        break;
                    case "bank":
                        outcome = Outcome(() => CoreKnowledgeLedger.Bank(new JObject { ["id"] = "meta" }, (JObject)args["receipts"]));
                        break;
                    case "acknowledge":
                        outcome = Outcome(() =>
                        {
                            CoreKnowledgeLedger.AcknowledgeBank(run, (JObject)args["captured"]);
                            return run["enemyKnowledgeState"]["pending"];
                        });
                        break;
                    default:
                        Assert.Fail("unknown ledger op " + op);
                        return;
                }
                var want = new JObject();
                if (entry["value"] != null) want["value"] = entry["value"];
                if (entry["error"] != null) want["error"] = entry["error"];
                Core2DamageParityTests.Same(at, want, outcome);
                Core2DamageParityTests.Same(at + " after", entry["after"], Snap(run));
            }
            Core2DamageParityTests.Same(label + " problems", unit["problems"], new JArray(CoreKnowledgeLedger.RunProblems(run).Select(p => (JToken)p)));
        }

        // ------------------------------------------------------------------ units: the intent read without a knowledge state

        public static IEnumerable<TestCaseData> ReadUnitCases()
        {
            if (!File.Exists(Path.Combine(Dir, "units.json"))) yield break;
            var rows = Rows("units.json", "reads");
            for (var i = 0; i < rows.Count; i++) yield return new TestCaseData(i).SetName("Core2IntentRead_" + rows[i]["spec"].Value<string>("encounterId"));
        }

        private static JObject Counters(CombatState c)
        {
            var o = new JObject();
            foreach (var pair in c.Rng.AllCounters())
                if (pair.Key != "combatRounds" || pair.Value != 0) o[pair.Key] = pair.Value;
            return o;
        }

        /// <summary>A profile whose intent module reads `exact` shows every intent and draws nothing on enemyIntentVisibility.</summary>
        [TestCaseSource(nameof(ReadUnitCases))]
        public void TheIntentReadWithoutKnowledgeIsTheWebs(int index)
        {
            var unit = (JObject)Rows("units.json", "reads")[index];
            var c = Open((JObject)unit["spec"]);
            Assert.That(c.Fields["enemyKnowledge"], Is.Null, "the fight has no knowledge state");
            var n = 0;
            foreach (var want in ((JArray)unit["rolled"]).OfType<JObject>())
            {
                CoreIntents.Roll(c, false);
                var got = new JObject
                {
                    ["enemies"] = new JArray(c.Enemies.Select(e => new JObject
                    {
                        ["id"] = e["id"], ["intent"] = e.Obj("intent")?["moveId"] ?? JValue.CreateNull(), ["kind"] = e.Obj("intent")?["kind"] ?? JValue.CreateNull(),
                        ["intentRevealed"] = e["intentRevealed"] ?? JValue.CreateNull(), ["plan"] = new JArray((e["plannedMoves"] as JArray ?? new JArray()).Select(m => m["moveId"])),
                    })),
                    ["rng"] = Counters(c),
                };
                Core2DamageParityTests.Same("read " + unit["spec"].Value<string>("encounterId") + " roll " + n++, want, got);
            }
        }

        // ------------------------------------------------------------------ units: the gear an enemy is pinned to

        public static IEnumerable<TestCaseData> MountCases()
        {
            if (!File.Exists(Path.Combine(Dir, "units.json"))) yield break;
            var rows = Rows("units.json", "mounts");
            for (var i = 0; i < rows.Count; i++) yield return new TestCaseData(i).SetName("Core2EnemyMount_" + rows[i].Value<string>("enemyId"));
        }

        private static JObject MountSummary(CombatState c, JObject enemy, string label)
        {
            var mounts = new JObject();
            if (c.PropertyMounts != null && c.PropertyMounts.TryGetValue(enemy.Value<string>("id"), out var owned))
                foreach (var pair in owned.Entries())
                    mounts[pair.Key] = new JObject
                    {
                        ["kind"] = pair.Value.Kind, ["id"] = pair.Value.Id, ["instanceId"] = pair.Value.InstanceId, ["scopeTags"] = new JArray(pair.Value.ScopeTags),
                        ["mechanicTags"] = pair.Value.MechanicTags.DeepClone(), ["mechanicBindings"] = pair.Value.MechanicBindings.DeepClone(), ["configuredEnemy"] = pair.Value.ConfiguredEnemy, ["rules"] = pair.Value.Rules.Count,
                    };
            return new JObject { ["label"] = label, ["mounts"] = mounts, ["attachments"] = new JArray(CoreFixCore.Attachments(c, enemy)) };
        }

        /// <summary>No gear of the profile attaches a mechanic, so a row of the fight's own profile is laid with one, synced, synced again and taken off.</summary>
        [TestCaseSource(nameof(MountCases))]
        public void ThePinnedGearMountsWhatItAttaches(int index)
        {
            var unit = (JObject)Rows("units.json", "mounts")[index];
            var c = Open((JObject)unit["spec"]);
            var enemy = c.Enemies[0];
            var gear = Ashen.Generated.Core2ProfileContentKeys.Gear;
            var row = ((JArray)c.CoreData.Content(gear)).OfType<JObject>().First(r => r.Value<string>("id") == unit.Value<string>("gearId"));
            var steps = ((JArray)unit["steps"]).OfType<JObject>().ToList();
            try
            {
                Core2DamageParityTests.Same("mount start", steps[0], MountSummary(c, enemy, "start"));
                row["mechanicTags"] = new JArray("defense:poiseProtection");
                CoreEnemies.SyncConfiguredProperties(c);
                Core2DamageParityTests.Same("mount mounted", steps[1], MountSummary(c, enemy, "mounted"));
                CoreEnemies.SyncConfiguredProperties(c);
                Core2DamageParityTests.Same("mount again", steps[2], MountSummary(c, enemy, "again"));
                row["mechanicTags"] = new JArray();
                CoreEnemies.SyncConfiguredProperties(c);
                Core2DamageParityTests.Same("mount removed", steps[3], MountSummary(c, enemy, "removed"));
            }
            finally { row.Remove("mechanicTags"); }
        }

        // ------------------------------------------------------------------ fights and exchanges

        public static IEnumerable<string> FightFiles() =>
            Directory.Exists(Dir) ? Directory.GetFiles(Dir, "fights-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal) : Enumerable.Empty<string>();

        public static IEnumerable<TestCaseData> Fights()
        {
            foreach (var file in FightFiles())
            {
                var fights = (JArray)Read(file)["fights"];
                for (var i = 0; i < fights.Count; i++)
                    yield return new TestCaseData(file, i).SetName("Core2EnemyFight_" + Path.GetFileNameWithoutExtension(file).Replace("fights-", string.Empty) + "_" + i);
            }
        }

        public static IEnumerable<TestCaseData> Exchanges()
        {
            if (!File.Exists(Path.Combine(Dir, "exchanges.json"))) yield break;
            var rows = Rows("exchanges.json", "exchanges");
            for (var i = 0; i < rows.Count; i++) yield return new TestCaseData(i).SetName("Core2EnemyExchange_" + rows[i].Value<string>("kind") + "_" + i);
        }

        [TestCaseSource(nameof(Fights))]
        public void TheFightPlaysAsTheWebPlaysIt(string file, int index) => Replay((JObject)((JArray)Read(file)["fights"])[index], file + "[" + index + "]");

        [TestCaseSource(nameof(Exchanges))]
        public void TheExchangePlaysAsTheWebPlaysIt(int index)
        {
            var exchange = (JObject)Rows("exchanges.json", "exchanges")[index];
            Replay(exchange, exchange.Value<string>("kind") + "[" + index + "] " + exchange.Value<string>("label"));
        }

        private static readonly HashSet<string> PayloadTypes = new HashSet<string>
        {
            "combatReactionOffered", "combatReactionChosen", "combatReactionStacked", "combatReactionUnavailable", "combatReactionFizzled", "combatEnemyReactionStacked",
            "combatCounterArmed", "combatCounterConsumed", "combatCounterTriggered", "combatGuardBroken", "combatRangeReturnUnavailable",
            "perceptionGained", "intentPredictionAccepted", "enemyMoveStarted", "combatHitStarted", "enemyActorTurnStarted",
        };

        private static readonly string[] Decoration = { "abilityBefore", "abilityEntities", "cardTargetsAllEnemies", "intentPreview", "actionPreview", "intent" };

        private static JToken Sorted(JToken token)
        {
            if (token is JObject o) return new JObject(o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Sorted(p.Value))));
            if (token is JArray a) return new JArray(a.Select(Sorted));
            return token.DeepClone();
        }

        /// <summary>The events of a step whose payload the port builds too: the reactions', the Counter's, the moves' and the knowledge events, and the Block a Counter adds.</summary>
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
        private static JToken Offer(CombatState c)
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
                    if (o["sourceId"] != null) row["sourceId"] = o["sourceId"].DeepClone();
                    return row;
                })),
            });
        }

        /// <summary>Plays one recorded fight or exchange on the port and holds it to the web after every step.</summary>
        internal static void Replay(JObject record, string label, Func<CombatState, CombatState> between = null)
        {
            var c = Open(record, out var run);
            JToken want = record["start"]["state"].DeepClone();
            Core2CombatStartParityTests.Same(label + " start", want, Core2CombatStartParityTests.Project(c));
            Assert.That(Core2CombatStartParityTests.Events(c, 0), Is.EqualTo(record["start"]["events"].Select(t => t.Value<string>()).ToList()), label + " start events");
            var n = 0;
            foreach (var step in ((JArray)record["steps"]).OfType<JObject>())
            {
                var intent = (JObject)step["intent"];
                var at = $"{label} step {n++} {intent.Value<string>("type")}";
                var mark = c.EventLog.Count;
                foreach (var id in (step["staggered"] as JArray ?? new JArray()).Select(t => t.Value<string>()))
                    c.Enemies.First(e => e.Value<string>("id") == id)["skipNextTurn"] = true;
                if (step["error"] != null)
                {
                    Assert.Throws<InvalidOperationException>(() => CoreDispatch.Dispatch(c, intent), at + " the web refused this intent: " + step["error"]);
                    continue;
                }
                CoreDispatch.Dispatch(c, intent);
                want = Core2CombatStartParityTests.Apply(want, step["delta"]);
                Assert.That(string.Join(" ", Core2CombatStartParityTests.Events(c, mark)), Is.EqualTo(string.Join(" ", step["events"].Select(t => t.Value<string>()))), at + " events");
                Core2CombatStartParityTests.Same(at, want, Core2CombatStartParityTests.Project(c));
                Core2DamageParityTests.Same(at + " payloads", step["payloads"], Payloads(c, mark));
                Core2DamageParityTests.Same(at + " offer", step["offer"], Offer(c));
                if (between != null) { c = between(c); if (ReferenceEquals(step, ((JArray)record["steps"]).Last())) Core2CombatStartParityTests.Same(at + " restored", want, Core2CombatStartParityTests.Project(c)); if (ReferenceEquals(step, ((JArray)record["steps"]).Last())) Core2DamageParityTests.Same(at + " restored offer", step["offer"], Offer(c)); }
            }
            if (record["ledger"] is JObject ledger)
            {
                CoreKnowledge.Reconcile(run, c);
                Core2DamageParityTests.Same(label + " ledger", ledger, new JObject { ["state"] = run["enemyKnowledgeState"].DeepClone(), ["perception"] = run["skills"]["perception"].DeepClone() });
            }
        }
    }
}
