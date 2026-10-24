using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.Content;
using Ashen.Domain.Combat;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-13 (D-280): the snapshot of a Core 2 fight. Two oracles hold it.
    /// Clone-and-continue: every recorded Core 2 fight (the damage, matchups, statuses, reactions, enemy and range sets) is played again with the fight
    /// replaced by its own snapshot after every step: serialized, written as JSON text, read back (with the random counters as the save keeps them) and
    /// restored, then compared with the web's record before the next step goes on. The snapshot of a fight paused on a reaction offer, with the enemy entries
    /// on the stack, the queued contacts and the plans, is in those fights.
    /// Shape: Tools/oracle-core2-snapshot.mjs records the web's <c>serializeCombatSnapshot</c> at sampled steps of those fights; the port's snapshot of the
    /// same step must equal it (the pinned profile body is the pin, the previews the UI reads are not carried).
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2SnapshotParityTests
    {
        private static string Root => Path.Combine(TestContent.OracleRoot, "core2");

        private static readonly string[] Sets = { "reactions", "range", "damage", "matchups", "statuses", "enemy", "remaining" };

        private static readonly Dictionary<string, JObject> Files = new Dictionary<string, JObject>();

        private static JObject Read(string set, string file)
        {
            lock (Files)
            {
                var key = set + "/" + file;
                if (!Files.TryGetValue(key, out var doc)) Files[key] = doc = (JObject)JsonContent.Parse(File.ReadAllText(Path.Combine(Root, set, file)));
                return doc;
            }
        }

        private static string Collection(JObject doc) => doc["fights"] != null ? "fights" : "exchanges";

        public static IEnumerable<TestCaseData> Records()
        {
            foreach (var set in Sets)
            {
                var dir = Path.Combine(Root, set);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.json").Select(Path.GetFileName).Where(f => f == "exchanges.json" || (f.StartsWith("fights-", StringComparison.Ordinal))).OrderBy(f => f, StringComparer.Ordinal))
                {
                    var doc = Read(set, file);
                    var rows = (JArray)doc[Collection(doc)];
                    for (var i = 0; i < rows.Count; i++) yield return new TestCaseData(set, file, i).SetName("Core2Snapshot_" + set + "_" + Path.GetFileNameWithoutExtension(file).Replace("fights-", string.Empty) + "_" + i);
                }
            }
        }

        /// <summary>
        /// The web's snapshot cannot tell an action counter that was never set from one that is zero (its restore writes both as 0), and neither can the
        /// port's, so while a fight is played from its snapshots a zero counter and an absent one are the same fight (<see cref="Core2CombatStartParityTests.Same"/>).
        /// </summary>
        [ThreadStatic]
        internal static bool ZeroCountersAreAbsent;

        internal static JToken WithoutZeroCounters(JToken token)
        {
            var copy = (JObject)token.DeepClone();
            foreach (var key in new[] { "expansionActionSerial", "pendingExpansionActions" })
                if (copy[key] != null && copy.Value<double>(key) == 0) copy.Remove(key);
            return copy;
        }

        internal static CombatState RoundTrip(CombatState c)
        {
            var text = CoreSnapshot.Serialize(c).ToString(Formatting.None);
            var counters = CoreSnapshot.RngToJson(c.Rng).ToString(Formatting.None);
            var rng = CoreSnapshot.RngFromJson(c.Rng.Seed, CoreSnapshot.Doubles((JObject)JsonContent.Parse(counters)));
            return CoreSnapshot.Restore(c.CoreData, rng, CoreSnapshot.Doubles((JObject)JsonContent.Parse(text)));
        }

        private static void Replay(string set, JObject record, string label, Func<CombatState, CombatState> between)
        {
            switch (set)
            {
                case "reactions":
                case "range":
                    Core2ReactionParityTests.Replay(record, label, null, between);
                    break;
                case "matchups":
                    Core2MatchupParityTests.Replay(record, label, between);
                    break;
                case "enemy":
                    Core2EnemyParityTests.Replay(record, label, between);
                    break;
                case "remaining":
                    Core2FightParityTests.Replay(record, label, Core2RemainingParityTests.Open, between);
                    break;
                default:
                    Core2FightParityTests.Replay(record, label, between: between);
                    break;
            }
        }

        /// <summary>Clone-and-continue: the fight is its own snapshot after every step and still equals the web's record.</summary>
        [TestCaseSource(nameof(Records))]
        public void TheFightContinuesFromItsSnapshotAsTheWebPlaysIt(string set, string file, int index)
        {
            var doc = Read(set, file);
            var record = (JObject)((JArray)doc[Collection(doc)])[index];
            ZeroCountersAreAbsent = true;
            try { Replay(set, record, set + "/" + file + "[" + index + "]", RoundTrip); }
            finally { ZeroCountersAreAbsent = false; }
        }

        // ------------------------------------------------------------------ the shape

        private static JObject Recorded => Read("snapshot", "snapshots.json");

        public static IEnumerable<TestCaseData> Shapes()
        {
            if (!File.Exists(Path.Combine(Root, "snapshot", "snapshots.json"))) yield break;
            var rows = (JArray)Recorded["snapshots"];
            for (var i = 0; i < rows.Count; i++)
            {
                var row = (JObject)rows[i];
                yield return new TestCaseData(i).SetName("Core2SnapshotShape_" + row.Value<string>("set") + "_" + Path.GetFileNameWithoutExtension(row.Value<string>("file")).Replace("fights-", string.Empty) + "_" + row.Value<int>("index") + "_step" + row.Value<int>("step"));
            }
        }

        private sealed class Captured : Exception { }

        private static JToken Sorted(JToken token)
        {
            if (token is JObject o) return new JObject(o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Sorted(p.Value))));
            if (token is JArray a) return new JArray(a.Select(Sorted));
            return token.DeepClone();
        }

        private static readonly string[] EventDecoration = { "abilityBefore", "abilityEntities", "cardTargetsAllEnemies", "intentPreview", "actionPreview", "intent" };

        private static readonly string[] UndefinedForNull = { "cardId", "cardInstanceId", "cardType", "abilityKind" };

        /// <summary>
        /// What the two sides hold differently without a rule reading it (D-282): the decoration of an event (the ability riders and the previews, the move's intent
        /// document), the four keys the web leaves undefined where the port holds null, and the zero riders a later hit of a charged card carries.
        /// </summary>
        private static void Alike(JObject snapshot)
        {
            foreach (var e in snapshot["eventLog"].OfType<JObject>()) Event(e);
            foreach (var action in (snapshot["abilityQueue"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var meta = (JObject)action["meta"];
                if (meta == null) continue;
                foreach (var key in new[] { "abilityChargeDamage", "abilityChargeBreak" }) if (meta[key] != null && meta.Value<double>(key) == 0) meta.Remove(key);
                if (meta["event"] is JObject ev) Event(ev);
                if (meta["expansionGroup"] is JObject group) { group.Remove("publicIntentPreview"); if (group["counterReturn"] is JObject cr) Event(cr); if (group["enemyMoveStart"] is JObject start) Event(start); }
            }
        }

        private static void Event(JObject e)
        {
            foreach (var key in EventDecoration) e.Remove(key);
            // A Counter's reply that breaks the Poise: the web does not name its source on the event, the port does.
            if (e.Value<string>("type") == "poiseBroken") { e.Remove("sourceId"); e.Remove("sourceKind"); }
            if (e.Value<string>("type") != "combatReactionChosen") foreach (var key in UndefinedForNull) if (e[key]?.Type == JTokenType.Null) e.Remove(key);
        }

        [Test]
        public void TheShapeOracleIsPresent()
        {
            var doc = Recorded;
            Assert.That(doc.Value<string>("profileHash"), Is.EqualTo(Core2DamageParityTests.Data.Run.ProfileHash), "the oracle ran the profile that is loaded");
            var rows = ((JArray)doc["snapshots"]).OfType<JObject>().ToList();
            Assert.That(rows.Count, Is.GreaterThan(20));
            Assert.That(rows.Count(r => r["snapshot"]["pendingReaction"] != null), Is.GreaterThan(0), "a snapshot paused on an offer");
            Assert.That(rows.Count(r => r["snapshot"]["reactionStack"]?["entries"]?.Any(e => e.Value<string>("side") == "enemy") == true), Is.GreaterThan(0), "a stack with an enemy entry and its plan");
            Assert.That(rows.Count(r => r["snapshot"]["abilityQueue"] != null), Is.GreaterThan(0), "queued work");
            Assert.That(rows.Count(r => ((JArray)r["snapshot"]["enemies"]).Any(e => e["plannedMoves"] != null)), Is.GreaterThan(0), "an enemy with a plan");
        }

        /// <summary>The snapshot the port writes at a sampled step is the one the web writes, key for key (the pin standing for the profile body).</summary>
        [TestCaseSource(nameof(Shapes))]
        public void TheSnapshotHasTheShapeOfTheWebs(int n)
        {
            var row = (JObject)((JArray)Recorded["snapshots"])[n];
            var set = row.Value<string>("set");
            var doc = Read(set, row.Value<string>("file"));
            var record = (JObject)((JArray)doc[row.Value<string>("collection")])[row.Value<int>("index")];
            var step = row.Value<int>("step");
            var live = ((JArray)record["steps"]).OfType<JObject>().Select((s, i) => (s, i)).Where(p => p.s["error"] == null).Select(p => p.i).ToList();
            var call = 0;
            JObject got = null;
            CombatState seen = null;
            try
            {
                Replay(set, record, set + "/" + row.Value<string>("file") + "[" + row.Value<int>("index") + "]", c =>
                {
                    if (live[call++] != step) return c;
                    got = CoreSnapshot.Serialize(c);
                    seen = c;
                    throw new Captured();
                });
            }
            catch (Captured) { }
            Assert.That(got, Is.Not.Null, "the replay reached the step");
            var want = (JObject)row["snapshot"].DeepClone();
            Assert.That(JToken.DeepEquals(got["gameProfile"], want["gameProfile"]), Is.True, "the pin is the web's profile id and hash");
            // Beyond the web's shape: the follow-up trace of a paused enemy turn (D-281).
            if (want["followTrace"] == null) got.Remove("followTrace");
            if (want["recordSkillXp"] == null) got.Remove("recordSkillXp");
            Alike(want);
            Alike(got);
            var diff = RunParityTests.FirstDifference(Sorted(want), Sorted(got), "$");
            if (diff != null)
            {
                var at = System.Text.RegularExpressions.Regex.Match(diff, @"^\$\.eventLog\[(\d+)\]");
                var context = at.Success ? " in " + got["eventLog"][int.Parse(at.Groups[1].Value)].ToString(Formatting.None) : string.Empty;
                Assert.Fail($"{row.Value<string>("set")}/{row.Value<string>("file")}[{row.Value<int>("index")}] step {step}: first difference at {diff}{context}");
            }
        }

        /// <summary>
        /// The web's own snapshot restores, and the fight it restores writes the same snapshot back: the queued contacts, the stack with its enemy entries and
        /// their plans, the plays that wait, read from the shape the web wrote them in (not only from the shape the port wrote).
        /// </summary>
        [TestCaseSource(nameof(Shapes))]
        public void TheWebsOwnSnapshotRestoresAndIsWrittenBackAsItWas(int n)
        {
            var row = (JObject)((JArray)Recorded["snapshots"])[n];
            var web = (JObject)row["snapshot"].DeepClone();
            var restored = CoreSnapshot.Restore(Core2DamageParityTests.Data, new Ashen.Domain.Random.Rng((uint)row["snapshot"]["combatKey"].ToString().GetHashCode()), CoreSnapshot.Doubles(web));
            var again = CoreSnapshot.Serialize(restored);
            again.Remove("followTrace");
            again.Remove("recordSkillXp");
            var want = (JObject)row["snapshot"].DeepClone();
            if (want["followTrace"] != null) again["followTrace"] = want["followTrace"].DeepClone();
            Alike(want);
            Alike(again);
            var diff = RunParityTests.FirstDifference(Sorted(want), Sorted(again), "$");
            if (diff != null) Assert.Fail($"{row.Value<string>("set")}/{row.Value<string>("file")}[{row.Value<int>("index")}] step {row.Value<int>("step")}: written back differently at {diff}");
        }
    }
}
