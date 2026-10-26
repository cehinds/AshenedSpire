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
    /// P-1 (D-1320..): the words on a card's face are the web's. Tools/oracle-core2-face.mjs records, from the web's model/cardFaceText.js and ui/components/card.js, the lines of every
    /// profile card (and its upgraded variant) with no fight, and of every card of an opening hand and of a hand of all of them under stance variants, with the preview the hand prints from;
    /// the C# generator (CardFaceText, CardFaceRules) is compared with them line for line and with the rules html at the three levels (hand, face, inspect). Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2CardFaceParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "face");

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

        private static CombatState Open(long seedValue, string classId, string encounterId)
        {
            var seed = (uint)seedValue;
            var run = CoreRunRules.Create(Data.Run, seed, classId, CoreRunOptions.FromJson(null, Unlocked));
            return CoreRunCombat.Create(Data, new Rng(seed), run, encounterId);
        }

        private static string LinesText(IEnumerable<FaceLine> lines) =>
            lines == null ? "null" : string.Join(" | ", lines.Select(l => l.Kind + ": " + l.Html));

        private static string WantLines(JToken lines) =>
            lines == null || lines.Type == JTokenType.Null ? "null" : string.Join(" | ", lines.Select(l => l.Value<string>("kind") + ": " + l.Value<string>("html")));

        /// <summary>One card's recorded face against the generated one; the differences, empty when it matches.</summary>
        private static List<string> Compare(string label, JObject want, FaceRules got)
        {
            var problems = new List<string>();
            if (WantLines(want["lines"]) != LinesText(got.Lines)) problems.Add($"{label} lines\n   web: {WantLines(want["lines"])}\n  unity: {LinesText(got.Lines)}");
            foreach (var level in new[] { "hand", "face", "inspect", "reading" })
            {
                var gotHtml = level == "hand" ? got.Hand : level == "face" ? got.Face : level == "inspect" ? got.Inspect : got.Reading;
                if (want.Value<string>(level) != gotHtml) problems.Add($"{label} {level}\n   web: {want.Value<string>(level)}\n  unity: {gotHtml}");
            }
            return problems;
        }

        [Test]
        public void TheOracleIsPresent()
        {
            var index = Read("index.json");
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Data.Run.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(((JObject)Read("static.json")["cards"]).Count, Is.EqualTo(Data.Content("cards").Count()));
        }

        [Test]
        public void EveryCardFaceWithoutAFightSaysWhatTheWebSays()
        {
            var bare = FaceEnv.Bare(Data);
            var env = new FaceEnv(bare, Ui.CardText);
            var problems = new List<string>();
            var checkedFaces = 0;
            foreach (var card in ((JObject)Read("static.json")["cards"]).Properties())
                foreach (var variant in ((JObject)card.Value).Properties())
                {
                    var instance = new JObject { ["instanceId"] = "s:" + card.Name, ["cardId"] = card.Name, ["upgraded"] = variant.Name == "upgraded" };
                    var def = CoreCards.Project(bare, instance);
                    var got = CardFaceRules.Render(env, def, null);
                    problems.AddRange(Compare(card.Name + " " + variant.Name, (JObject)variant.Value, got));
                    checkedFaces++;
                }
            Assert.That(checkedFaces, Is.GreaterThan(400));
            if (problems.Count > 0) Assert.Fail(problems.Count + " differences:\n" + string.Join("\n", problems.Take(8)));
        }

        [Test]
        public void TheViewWordsEveryCardOfTheHandAndThePiles()
        {
            var fight = (JObject)Read("fights-herald.json")["fights"][0];
            var seed = (uint)fight.Value<long>("seed");
            var run = CoreRunRules.Create(Data.Run, seed, fight.Value<string>("classId"), CoreRunOptions.FromJson(null, Unlocked));
            var session = Ashen.App.Combat.Core2CombatSession.Start(Data, new Rng(seed), run, fight.Value<string>("encounterId"));
            var view = Core2CombatView.Build(session, text: Ui.CardText);
            Assert.That(view.Hand, Is.Not.Empty);
            foreach (var card in view.Hand)
            {
                Assert.That(card.Words, Is.Not.Null, card.Name);
                Assert.That(card.Words.Hand, Is.Not.Empty, card.Name);
                var direct = CardFaceRules.Render(new FaceEnv(session.State, Ui.CardText), card.FaceDef, card.FacePreview);
                Assert.That(card.Words.Hand, Is.EqualTo(direct.Hand), card.Name);
            }
            var pile = Core2CombatView.Pile(session, Ashen.Generated.OverlayValues.PileDraw, Ui.CardText);
            Assert.That(pile, Is.Not.Empty);
            Assert.That(pile.All(card => card.Words != null && card.Words.Face.Length > 0), Is.True);
        }

        // ------------------------------------------------------------------ the hand

        private static CombatState Apply(CombatState c, JObject setup)
        {
            var p = c.Player;
            if (setup.Value<string>("hand") == "all")
                c.Piles.Hand = Data.Content("cards").Select((card, i) => new JObject { ["instanceId"] = "x" + (i + 1), ["cardId"] = card.Value<string>("id"), ["upgraded"] = false }).ToList();
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
            return c;
        }

        public static IEnumerable<TestCaseData> HandCases()
        {
            if (!Directory.Exists(Dir)) yield break;
            foreach (var file in Directory.GetFiles(Dir, "fights-*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal))
            {
                var fights = (JArray)Read(file)["fights"];
                for (var i = 0; i < fights.Count; i++)
                    foreach (var scenario in ((JObject)fights[i]["scenarios"]).Properties())
                        yield return new TestCaseData(file, i, scenario.Name).SetName("Core2Face_" + Path.GetFileNameWithoutExtension(file) + "_" + i + "_" + scenario.Name);
            }
        }

        [TestCaseSource(nameof(HandCases))]
        public void TheHandSaysWhatTheWebSays(string file, int index, string scenarioName)
        {
            var fight = (JObject)Read(file)["fights"][index];
            var scenario = (JObject)fight["scenarios"][scenarioName];
            var c = Apply(Open(fight.Value<long>("seed"), fight.Value<string>("classId"), fight.Value<string>("encounterId")), (JObject)scenario["setup"]);
            var env = new FaceEnv(c, Ui.CardText);
            var problems = new List<string>();
            foreach (var card in ((JObject)scenario["cards"]).Properties())
            {
                var want = (JObject)card.Value;
                if (want["error"] != null) continue;
                var instance = c.Piles.Hand.First(h => h.Value<string>("instanceId") == card.Name);
                JObject preview;
                try { preview = Core2FacePreview.Build(c, instance); }
                catch (Exception e) when (e is InvalidOperationException || e is NotSupportedException) { problems.Add($"{card.Name} ({want.Value<string>("cardId")}) preview: {e.Message}"); continue; }
                var diff = OracleCompare.FirstDifference(want["preview"], Summary(preview), "$");
                if (diff != null) problems.Add($"{card.Name} ({want.Value<string>("cardId")}) preview differs at {diff}\n   web: {want["preview"].ToString(Newtonsoft.Json.Formatting.None)}\n  unity: {Summary(preview).ToString(Newtonsoft.Json.Formatting.None)}");
                var calculation = Calculation(preview);
                var calculationDiff = OracleCompare.FirstDifference(want["receipt"] ?? JValue.CreateNull(), calculation, "$");
                if (calculationDiff != null) problems.Add($"{card.Name} ({want.Value<string>("cardId")}) damage calculation differs at {calculationDiff}\n   web: {want["receipt"]}\n  unity: {calculation}");
                var got = CardFaceRules.Render(env, preview.Obj("resolvedDefinition"), preview);
                problems.AddRange(Compare($"{card.Name} ({want.Value<string>("cardId")})", want, got));
            }
            if (problems.Count > 0) Assert.Fail(problems.Count + " differences:\n" + string.Join("\n", problems.Take(8)));
        }

        /// <summary>The tooltip of an underlined damage number as the oracle records it (summary, scope, topics), from the port's.</summary>
        private static JToken Calculation(JObject preview)
        {
            var tip = Core2DamageCalculation.Tip(Ui.Strings, preview);
            if (tip == null) return JValue.CreateNull();
            var lines = tip.Body.Split('\n');
            var topics = new JArray();
            foreach (var line in lines.Skip(2))
            {
                var at = line.IndexOf(": ", StringComparison.Ordinal);
                topics.Add(new JArray(line.Substring(0, at), line.Substring(at + 2)));
            }
            return new JObject { ["scope"] = lines[1], ["summary"] = lines[0], ["topics"] = topics };
        }

        private static readonly string[] ValueFields = { "op", "value", "hits", "hitDamages", "totalDamage", "faceValue", "splitBase", "ratingBonus", "ratingPart", "physicalDamageType", "defenseSplit", "tags", "status", "token", "counterReturn" };

        /// <summary>The oracle's summary of a preview (Tools/oracle-core2-face.mjs summary): the fields the face reads.</summary>
        private static JObject Summary(JObject preview)
        {
            return new JObject
            {
                ["values"] = new JArray(Ashen.Domain.Combat.Js.Items(preview["values"]).OfType<JObject>().Select(row =>
                {
                    var copy = new JObject();
                    foreach (var field in ValueFields.OrderBy(f => f, StringComparer.Ordinal)) if (row[field] != null) copy[field] = row[field].DeepClone();
                    if (row["damageReceipt"] is JObject receipt) copy["damageReceipt"] = ReceiptOf(receipt);
                    return copy;
                })),
                ["tokens"] = Sorted(preview["tokens"]), ["damageSequences"] = preview["damageSequences"].DeepClone(), ["counterReturn"] = preview["counterReturn"]?.DeepClone() ?? JValue.CreateNull(),
                ["entryPrecision"] = preview["entryPrecision"]?.DeepClone() ?? JValue.CreateNull(), ["costShift"] = JValue.CreateNull(),
                ["chargedEffects"] = ((JArray)preview["chargedEffects"]).Count, ["expansion"] = preview["combatExpansionVersion"].DeepClone(),
            };
        }

        private static JToken Or(JToken token, JToken fallback) => token == null || token.Type == JTokenType.Null ? fallback : token.DeepClone();

        /// <summary>The oracle's receiptOf: the hits of a damage number's calculation with the sources that made them.</summary>
        private static JObject ReceiptOf(JObject receipt) => new JObject
        {
            ["hits"] = new JArray(Ashen.Domain.Combat.Js.Items(receipt["hits"]).OfType<JObject>().Select(hit => new JObject
            {
                ["barrierReduced"] = Or(hit["barrierReduced"], 0), ["blocked"] = Or(hit["blocked"], 0), ["hpLoss"] = hit["hpLoss"]?.DeepClone(), ["incoming"] = hit["incoming"]?.DeepClone(),
                ["magical"] = Or(hit["magical"], JValue.CreateNull()), ["physical"] = Or(hit["physical"], JValue.CreateNull()), ["poiseDamage"] = Or(hit["poiseDamage"], 0),
                ["poiseReduced"] = Or(hit["poiseReduced"], 0),
                ["sources"] = new JArray(Ashen.Domain.Combat.Js.Items(hit["sources"]).OfType<JObject>().Select(source => new JObject
                {
                    ["amount"] = Or(source["amount"], JValue.CreateNull()), ["id"] = source["id"]?.DeepClone(), ["label"] = Or(source["label"], JValue.CreateNull()),
                    ["multiplier"] = Or(source["multiplier"], JValue.CreateNull()), ["operation"] = Or(source["operation"], JValue.CreateNull()), ["stage"] = Or(source["stage"], JValue.CreateNull()),
                })),
                ["wardReduced"] = Or(hit["wardReduced"], 0),
            })),
            ["targetId"] = Or(receipt["targetId"], JValue.CreateNull()), ["totalDamage"] = receipt["totalDamage"]?.DeepClone(),
        };

        private static JToken Sorted(JToken token)
        {
            if (!(token is JObject o)) return token.DeepClone();
            var copy = new JObject();
            foreach (var p in o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)) copy[p.Name] = Sorted(p.Value);
            return copy;
        }
    }
}
