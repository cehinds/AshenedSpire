using System;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// P-3a (D-1450..D-1459): the tooltip glossary and the terms behind it against the shipped web. ui/terms.json is the web's content/framework/terms.json at the pin (Tools/web-terms.mjs): the eight terms the legacy copy lacked and
    /// the six it worded differently. Tools/oracle-core2-glossary.mjs reads keywordExplanations for every card's rules text and every piece's effect lines of the configured profile; the port finds the same words, once each,
    /// with the same explanations. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class TermGlossaryTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "glossary");

        private static UiData _ui;

        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        private string _dir;
        private Core2RunSession _session;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-glossary-" + Guid.NewGuid().ToString("N"));
            var saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
            _session = Core2RunSession.New(Core2HostParityTests.Content, saves, 1, 779u, "reaver", "Aldric", null);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private static JObject Term(string id) => Js.Items(Ui.Terms["terms"]).OfType<JObject>().First(t => t.Str("id") == id);

        [Test]
        public void TheTermsAreTheWebsAtThePin()
        {
            Assert.That(Js.Items(Ui.Terms["terms"]).Count(), Is.EqualTo(119), "the legacy copy had 111; `node Tools/web-terms.mjs --check` holds it verbatim");
            foreach (var id in new[] { "term.fire", "term.sacred", "term.dualWield", "term.targetAllAllies", "term.tooltip.fire", "term.tooltip.sacred", "term.tooltip.dualWield", "term.tooltip.targetAllAllies" })
                Assert.That(Term(id), Is.Not.Null, id);
            Assert.That(Term("term.action").Str("canonicalText"), Is.EqualTo("Stamina"), "reworded: an Action is Stamina");
            Assert.That(Term("term.energy").Str("canonicalText"), Is.EqualTo("Stamina"));
            Assert.That(Term("term.tooltip.retain").Str("canonicalText"), Is.EqualTo("Stays in your hand at the end of your turn."));
            Assert.That(Term("term.tooltip.stamina").Str("canonicalText"), Is.EqualTo("Costs Stamina to play. Refills at the start of every turn."));
        }

        [Test]
        public void TheOracleIsPresent()
        {
            var index = OracleDocuments.Read(Path.Combine(Dir, "index.json"));
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Core2HostParityTests.Content.Core2.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(index["tally"].Value<int>("withEntries"), Is.GreaterThan(700));
            Assert.That(index["tally"].Value<int>("distinctNames"), Is.GreaterThan(80));
        }

        [Test]
        public void EveryTextNamesTheWordsTheWebGlossesWithTheirExplanations()
        {
            var glossary = _session.Glossary(Ui);
            var texts = ((JArray)OracleDocuments.Read(Path.Combine(Dir, "texts.json"))["texts"]).OfType<JObject>().ToList();
            var checkedEntries = 0;
            foreach (var row in texts)
            {
                var want = new JArray(((JArray)row["entries"]).OfType<JObject>().Select(e => new JObject { ["name"] = e["name"], ["explanation"] = e["explanation"] }));
                var got = new JArray(glossary.Explain(row.Value<string>("text")).Select(e => new JObject { ["name"] = e.Name, ["explanation"] = e.Explanation }));
                var diff = OracleCompare.FirstDifference(want, got, "$");
                if (diff != null) Assert.Fail(row.Value<string>("source") + ": first difference at " + diff + "\ntext " + row.Value<string>("text") + "\nwant " + want.ToString(Newtonsoft.Json.Formatting.None) + "\ngot  " + got.ToString(Newtonsoft.Json.Formatting.None));
                checkedEntries += want.Count;
            }
            Assert.That(checkedEntries, Is.GreaterThan(1500));
        }

        [Test]
        public void AStatusTooltipHasItsNumbersFilledIn()
        {
            var def = JObject.Parse("{\"name\":\"Bleed\",\"tooltip\":\"At {proc.threshold}, burst for {proc.burstPercent}% (min {proc.burstMin}, max {proc.burstMax}), {tv.pct}% more, {onEnter.0.amount}, {hooks.0.do.1.amount}, {decay.duration} turns, {resists.percent}%.\"," +
                "\"proc\":{\"threshold\":7,\"burstPercent\":15,\"burstMin\":8,\"burstMax\":35},\"taggedVulnerability\":{\"mult\":1.25},\"onEnter\":[{\"amount\":3}],\"hooks\":[{\"do\":[{\"amount\":1},{\"amount\":2}]}],\"decay\":{\"duration\":4},\"resists\":{\"percent\":50}}");
            Assert.That(TermGlossary.StatusTooltip(def), Is.EqualTo("At 7, burst for 15% (min 8, max 35), 25% more, 3, 2, 4 turns, 50%."));
        }
    }
}
