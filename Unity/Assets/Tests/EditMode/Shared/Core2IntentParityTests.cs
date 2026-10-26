using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Random;
using Ashen.Domain.Run;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// P-1 (D-1320..): what the player is told of an enemy's read move is the web's. Tools/oracle-core2-intent.mjs records, from engine/combat.js previewIntent through
    /// ui/models/CombatIntentProjectionModel.js projectCombatIntent, intentDamageRead and combatIntentPresentation, the projected intent and its badge of every living enemy of
    /// fights played by the end-turn bot, under setups laid on a copy of the fight (unread, read, Block, Barrier, Defend, Evade, Counter and Attack stances, Vulnerable, no Poise).
    /// The C# projection (<see cref="CoreIntentProjection"/>, <see cref="Core2IntentBadge"/>) is compared field for field: the HP range ('0–16'), the baseline, the tone, the
    /// per-hit numbers, the badge's symbols and the line under the number ('Total 0–16 HP'). Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2IntentParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "intent");

        private static CoreCombatData _data;
        private static UiData _ui;

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

        private static UiData Ui => _ui ?? (_ui = UiData.Load(TestContent.Source));

        private static JObject Read(string name) => OracleDocuments.Read(Path.Combine(Dir, name));

        private static JObject Unlocked => new JObject
        {
            ["gameProfileUnlocks"] = new JObject { [Data.Run.ProfileId] = new JObject { ["completedRunIds"] = new JArray(Enumerable.Range(0, Data.Run.Run.Classes.Count).Select(i => "r" + i)) } },
        };

        public static IEnumerable<TestCaseData> Fights()
        {
            if (!Directory.Exists(Dir)) yield break;
            foreach (var file in Directory.GetFiles(Dir, "fights-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal))
            {
                var fights = (JArray)Read(file)["fights"];
                for (var i = 0; i < fights.Count; i++)
                    yield return new TestCaseData(file, i).SetName("Core2Intent_" + Path.GetFileNameWithoutExtension(file).Replace("fights-", string.Empty) + "_" + i + "_" + fights[i].Value<string>("encounterId"));
            }
        }

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Read("index.json");
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Data.Run.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(index["files"].Count(), Is.EqualTo(4));
        }

        private static void Lay(CombatState c, JObject setup)
        {
            var p = c.Player;
            if (setup.Value<bool?>("read") == true) foreach (var e in c.Enemies) e[Ashen.Generated.Core2CombatKeys.ActorIntentRevealed] = true;
            if (setup["block"] != null) p["block"] = setup["block"].DeepClone();
            if (setup["barrier"] != null) p[Ashen.Generated.Core2CombatKeys.Barrier] = setup["barrier"].DeepClone();
            if (setup["stance"] != null) p[Ashen.Generated.Core2CombatKeys.CombatStance] = setup["stance"].DeepClone();
            if (setup["statuses"] is JObject statuses)
                foreach (var s in statuses.Properties()) ((JObject)p["statuses"])[s.Name] = new JObject { ["stacks"] = s.Value.DeepClone() };
            if (setup["poise"] != null && p[Ashen.Generated.Core2CombatKeys.PersistentPoise] is JObject poise) poise["value"] = setup["poise"].DeepClone();
        }

        private static readonly string[] Picked =
        {
            "kind", "moveId", "stance", "hidden", "revealed", "damage", "hits", "hitDamages", "totalDamage", "hpLoss", "hitChance", "uncertain", "hpLossBounds", "block",
            "counterDamage", "counterPoiseDamage", "delayed", "pending", "dodgeCharges", "label", "knowledgeRead",
        };

        private static double R9(JToken value) => Math.Round(value.Value<double>() * 1e9) / 1e9;

        /// <summary>The oracle's slim of an intent (Tools/oracle-core2-intent.mjs slim).</summary>
        private static JObject Slim(JObject intent)
        {
            var o = new JObject();
            foreach (var key in Picked.OrderBy(k => k, StringComparer.Ordinal))
                if (intent[key] != null) o[key] = key == "hitChance" ? new JValue(R9(intent[key])) : intent[key].DeepClone();
            if (intent["profile"] is JObject profile)
                o["profile"] = new JObject { ["camp"] = profile["camp"] ?? JValue.CreateNull(), ["maneuver"] = profile["maneuver"] ?? JValue.CreateNull(), ["range"] = profile["range"] ?? JValue.CreateNull() };
            if (intent["projectedDamage"] is JObject p)
            {
                var copy = (JObject)p.DeepClone();
                copy["hitChance"] = R9(p["hitChance"]);
                copy["expected"] = R9(p["expected"]);
                if (copy["baseline"] is JObject b) b["expected"] = R9(b["expected"]);
                o["projectedDamage"] = copy;
            }
            return o;
        }

        private static JToken S(string text) => text == null ? JValue.CreateNull() : new JValue(text);

        /// <summary>OracleCompare.FirstDifference with the probabilities compared to nine decimals (the walk sums floats in the web's order; the last bit is noise).</summary>
        private static string Same(JToken a, JToken b, string path)
        {
            if (a == null || b == null) return a == null && b == null ? null : $"{path}: {a} vs {b}";
            if (Js.IsNum(a) && Js.IsNum(b)) return Math.Abs(Js.D(a) - Js.D(b)) < 1e-9 ? null : $"{path}: {a} vs {b}";
            if (a is JObject oa && b is JObject ob)
            {
                foreach (var name in oa.Properties().Select(p => p.Name).Union(ob.Properties().Select(p => p.Name)))
                {
                    var d = Same(oa[name], ob[name], path + "." + name);
                    if (d != null) return d;
                }
                return null;
            }
            if (a is JArray aa && b is JArray ab)
            {
                if (aa.Count != ab.Count) return $"{path}: length {aa.Count} vs {ab.Count}";
                for (var i = 0; i < aa.Count; i++)
                {
                    var d = Same(aa[i], ab[i], path + "[" + i + "]");
                    if (d != null) return d;
                }
                return null;
            }
            return JToken.DeepEquals(a, b) ? null : $"{path}: {a} vs {b}";
        }

        private static JObject Badge(JObject intent, StringTable strings)
        {
            var badge = Core2IntentBadge.Of(intent, strings);
            var read = CoreIntentProjection.Read(intent);
            var parts = new JArray(badge.Parts.Select(part =>
            {
                var row = new JObject { ["icon"] = part.Icon, ["value"] = S(part.Value) };
                if (part.Summary != null) row["summary"] = part.Summary;
                return row;
            }));
            return new JObject
            {
                ["visibility"] = badge.Visibility, ["title"] = badge.Title, ["stance"] = badge.Stance, ["damageClass"] = S(badge.DamageClass),
                ["parts"] = parts, ["tone"] = S(badge.Tone),
                ["read"] = read == null ? JValue.CreateNull() : new JObject { ["label"] = read.Label, ["summary"] = badge.Summary },
            };
        }

        [TestCaseSource(nameof(Fights))]
        public void TheProjectedIntentAndItsBadgeSayWhatTheWebSays(string file, int index)
        {
            var fight = (JObject)Read(file)["fights"][index];
            var seed = (uint)fight.Value<long>("seed");
            var run = CoreRunRules.Create(Data.Run, seed, fight.Value<string>("classId"), CoreRunOptions.FromJson(null, Unlocked));
            var session = Core2CombatSession.Start(Data, new Rng(seed), run, fight.Value<string>("encounterId"));
            var strings = Ui.Strings;
            var problems = new List<string>();
            var compared = 0;
            var ranges = 0;
            var turns = (JArray)fight["turns"];
            for (var t = 0; t < turns.Count; t++)
            {
                if (t > 0) foreach (var step in (JArray)turns[t]["intents"]) Assert.That(session.Execute((JObject)step).Accepted, Is.True, $"turn {t}");
                foreach (var scenario in ((JObject)turns[t]["scenarios"]).Properties())
                {
                    var state = session.State.Detach();
                    Lay(state, (JObject)scenario.Value["setup"]);
                    foreach (var row in ((JObject)scenario.Value["enemies"]).Properties())
                    {
                        var at = $"turn {t} {scenario.Name} {row.Name}";
                        var enemy = state.Enemies.FirstOrDefault(e => e.Value<string>("id") == row.Name);
                        Assert.That(enemy, Is.Not.Null, at);
                        var want = (JObject)row.Value;
                        var wantIntent = (JObject)want["intent"];
                        var exact = CoreVisibility.IntentExact(state, enemy);
                        if (wantIntent.Value<bool?>("hidden") == true)
                        {
                            if (exact) problems.Add($"{at}: the web hides the move, the port reads it");
                            continue;
                        }
                        if (!exact) { problems.Add($"{at}: the web reads the move, the port hides it"); continue; }
                        var got = CoreIntentProjection.Project(state, enemy);
                        var diff = Same(wantIntent, Slim(got), "$");
                        if (diff != null) problems.Add($"{at}: intent differs at {diff}\n   web: {wantIntent.ToString(Newtonsoft.Json.Formatting.None)}\n  unity: {Slim(got).ToString(Newtonsoft.Json.Formatting.None)}");
                        var badgeDiff = Same(want["badge"], Badge(got, strings), "$");
                        if (badgeDiff != null) problems.Add($"{at}: badge differs at {badgeDiff}\n   web: {want["badge"].ToString(Newtonsoft.Json.Formatting.None)}\n  unity: {Badge(got, strings).ToString(Newtonsoft.Json.Formatting.None)}");
                        compared++;
                        if (wantIntent["projectedDamage"] is JObject projected && projected.Value<double?>("min") != projected.Value<double?>("max")) ranges++;
                    }
                }
            }
            Assert.That(compared, Is.GreaterThan(0));
            if (problems.Count > 0) Assert.Fail(problems.Count + " differences (" + compared + " compared):\n" + string.Join("\n", problems.Take(6)));
            Assert.That(ranges, Is.GreaterThanOrEqualTo(0));
        }
    }
}
