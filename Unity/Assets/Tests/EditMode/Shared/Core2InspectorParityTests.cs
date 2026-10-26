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
    /// P-7b (D-1730..): the combatant door says what the web's says. Tools/oracle-core2-inspector.mjs records, from ui/screens/combat.js combatantSubject through
    /// ui/components/combatantInspector.js combatantDetailBody and combatantInspectorPreview, the head, the pool meters and every block (the sections, the active list, the bestiary,
    /// the prediction, the enemy deck, the revealed hand) of the player and of each living enemy of fights played by the cards bot, under setups laid on a copy of the fight (the moves
    /// unread, read, Block, stances, a prepared Evade and statuses on both sides, a revealed move and hand, a broken Poise). The view model
    /// (<see cref="Core2CombatantInspector"/>) is compared block for block, row for row, word for word. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2InspectorParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "inspector");

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
                    yield return new TestCaseData(file, i).SetName("Core2Inspector_" + Path.GetFileNameWithoutExtension(file).Replace("fights-", string.Empty) + "_" + i + "_" + fights[i].Value<string>("encounterId"));
            }
        }

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Read("index.json");
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Data.Run.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(index["files"].Count(), Is.EqualTo(4));
        }

        private static JObject Clone(JToken token) => (JObject)token.DeepClone();

        /// <summary>The setups the oracle laid on its copy of the fight (Tools/oracle-core2-inspector.mjs lay).</summary>
        private static void Lay(CombatState c, JObject setup)
        {
            var p = c.Player;
            foreach (var e in c.Enemies)
            {
                if (setup["reveal"] != null) e["proposalIntentRevealedUntilByOwner"] = new JObject { ["player"] = c.Turn + setup.Value<double>("reveal") };
                if (setup["burdenCards"] != null)
                {
                    var counts = (JObject)setup["burdenCounts"];
                    e["burdenPiles"] = new JObject
                    {
                        ["draw"] = new JArray(Enumerable.Range(0, counts.Value<int>("draw")).Select(i => new JObject { ["instanceId"] = "b" + i, ["cardId"] = "strike" })),
                        ["hand"] = new JArray(((JArray)setup["burdenCards"]).Select((id, i) => new JObject { ["instanceId"] = "h" + i, ["cardId"] = id })),
                        ["discard"] = new JArray(Enumerable.Range(0, counts.Value<int>("discard")).Select(i => new JObject { ["instanceId"] = "d" + i, ["cardId"] = "defend" })),
                        ["exhaust"] = new JArray(),
                    };
                }
                if (setup.Value<bool?>("read") == true) e["actorIntentRevealed"] = true;
                if (setup["enemyBlock"] != null) e["block"] = setup["enemyBlock"].DeepClone();
                if (setup["enemyStance"] != null) e["combatStance"] = setup["enemyStance"].DeepClone();
                if (setup["enemyEvade"] != null) e["combatEvade"] = setup["enemyEvade"].DeepClone();
                if (setup["enemyStatuses"] is JArray enemyStatuses) foreach (var s in enemyStatuses) ((JObject)e["statuses"])[(string)s[0]] = s[1].DeepClone();
            }
            if (setup["playerBlock"] != null) p["block"] = setup["playerBlock"].DeepClone();
            if (setup["playerStance"] != null) p["combatStance"] = setup["playerStance"].DeepClone();
            if (setup["playerEvade"] != null) p["combatEvade"] = setup["playerEvade"].DeepClone();
            if (setup["playerStatuses"] is JArray playerStatuses) foreach (var s in playerStatuses) ((JObject)p["statuses"])[(string)s[0]] = s[1].DeepClone();
        }

        /// <summary>RunParityTests.FirstDifference with the numbers compared to nine decimals (the meters' share is a float).</summary>
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

        private static InspectorContext Context(string playerName) => new InspectorContext { Strings = Ui.Strings, Text = Ui.CardText, PlayerName = playerName };

        [TestCaseSource(nameof(Fights))]
        public void TheDoorOfEachFighterSaysWhatTheWebSays(string file, int index)
        {
            var fight = (JObject)Read(file)["fights"][index];
            var seed = (uint)fight.Value<long>("seed");
            var run = CoreRunRules.Create(Data.Run, seed, fight.Value<string>("classId"), CoreRunOptions.FromJson(null, Unlocked));
            var session = Core2CombatSession.Start(Data, new Rng(seed), run, fight.Value<string>("encounterId"));
            var context = Context(fight.Value<string>("playerName"));
            var problems = new List<string>();
            var compared = 0;
            var turns = (JArray)fight["turns"];
            for (var t = 0; t < turns.Count; t++)
            {
                if (t > 0) foreach (var step in (JArray)turns[t]["intents"]) Assert.That(session.Execute((JObject)step).Accepted, Is.True, $"turn {t}");
                foreach (var scenario in ((JObject)turns[t]["scenarios"]).Properties())
                {
                    var state = session.State.Detach();
                    Lay(state, (JObject)scenario.Value["setup"]);
                    foreach (var row in ((JObject)scenario.Value["doors"]).Properties())
                    {
                        var at = $"turn {t} {scenario.Name} {row.Name}";
                        var entity = row.Name == "player" ? state.Player : state.Enemies.FirstOrDefault(e => e.Value<string>("id") == row.Name);
                        Assert.That(entity, Is.Not.Null, at);
                        var got = Core2CombatantInspector.Build(state, entity, context).ToJson();
                        var diff = Same(row.Value, got, "$");
                        if (diff != null) problems.Add($"{at}: differs at {diff}");
                        compared++;
                    }
                }
            }
            Assert.That(compared, Is.GreaterThan(0));
            if (problems.Count > 0) Assert.Fail(problems.Count + " differences (" + compared + " compared):\n" + string.Join("\n", problems.Take(8)));
        }
    }
}
