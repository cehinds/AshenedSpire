using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Combat;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// P-7d (D-1773, D-1774): the combatant door's hover tips. The glossary's words in the door's text (web tooltipGlossary.js decorateKeywords, ended onto combatantDetailBody) are links of the label's rich text with
    /// one explanation each, matched exactly as <see cref="TermGlossary.Explain"/> matches them (the regex the web's decorate walks every text node with); the preview meters' tips are held to the web row by row in
    /// <see cref="Core2InspectorParityTests"/> (<c>tip</c>). Engine-free.
    /// </summary>
    [TestFixture, Category("Presentation")]
    public class Core2KeywordTipsTests
    {
        private string _dir;
        private SaveService _saves;
        private static readonly DateTime Epoch = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        private static UiData _ui;

        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-core2-keywords-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private TermGlossary Glossary() => Core2RunSession.New(Core2HostParityTests.Content, _saves, 1, 12345, "reaver", "Aldric", () => Epoch).Glossary(Ui);

        private static IEnumerable<string> DoorTexts()
        {
            var texts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(Path.Combine(TestContent.OracleRoot, "core2", "inspector"), "fights-*.json"))
                foreach (var token in OracleDocuments.Read(file).DescendantsAndSelf().OfType<JValue>())
                    if (token.Type == JTokenType.String && token.Path.Contains("blocks")) texts.Add((string)token.Value);
            return texts;
        }

        [Test]
        public void EveryOccurrenceOfAGlossaryWordIsAStretchOfItsOwnAndTheTextIsWhole()
        {
            var glossary = Glossary();
            var names = glossary.Names.Where(n => n.All(char.IsLetter)).Take(3).ToList();
            Assert.That(names.Count, Is.EqualTo(3), "the glossary has words");
            var text = $"Gains {names[0]} and {names[1]}; {names[0].ToUpperInvariant()} twice, {names[2]}.";
            var segments = glossary.Segments(text);
            Assert.That(string.Concat(segments.Select(s => s.Text)), Is.EqualTo(text), "the stretches are the text, in order");
            var found = segments.Where(s => s.Entry != null).ToList();
            Assert.That(found.Select(s => s.Key), Is.EqualTo(new[] { names[0], names[1], names[0], names[2] }), "each occurrence, spelled as written, keyed in lower case");
            Assert.That(found.Select(s => s.Text), Does.Contain(names[0].ToUpperInvariant()), "the text keeps its spelling");
            Assert.That(found.All(s => !string.IsNullOrEmpty(s.Entry.Explanation)), Is.True);
        }

        [Test]
        public void ATextWithNoGlossaryWordIsOnePlainStretchAndAnEmptyOneIsNone()
        {
            var glossary = Glossary();
            var plain = glossary.Segments("Zzyzx qxv.");
            Assert.That(plain.Count, Is.EqualTo(1));
            Assert.That(plain[0].Entry, Is.Null);
            Assert.That(KeywordMarkup.Any(plain), Is.False);
            Assert.That(glossary.Segments(string.Empty), Is.Empty);
            Assert.That(glossary.Segments(null), Is.Empty);
        }

        [Test]
        public void TheMarkupLinksEachWordByItsKeyInGoldAndUnderlineAndMakesAngleBracketsSafe()
        {
            var glossary = Glossary();
            var word = glossary.Names.First(n => n.All(char.IsLetter));
            var segments = glossary.Segments("a < b " + word + " c");
            var rich = KeywordMarkup.Rich(Ui.CardText, segments, "#c9a227");
            Assert.That(rich, Does.Contain("<link=\"" + word + "\">"));
            Assert.That(rich, Does.Contain("<color=#c9a227>"));
            Assert.That(rich, Does.Contain("<u>" + word + "</u>"));
            Assert.That(rich, Does.Contain("</link>"));
            Assert.That(rich.Replace(Ui.CardText.Word(StringKeys.CardtextAngleOpenSafe), string.Empty), Does.Not.Contain("a < b"), "a literal angle bracket does not open a rich text tag");
            var explanations = KeywordMarkup.Explanations(segments);
            Assert.That(explanations.Keys, Is.EqualTo(new[] { word }));
            Assert.That(explanations[word], Is.Not.Empty);
        }

        [Test]
        public void EveryTextTheDoorSaysIsCutWholeAndFindsTheWordsExplainFinds()
        {
            var glossary = Glossary();
            var texts = DoorTexts().ToList();
            Assert.That(texts.Count, Is.GreaterThan(200), "the oracle's doors were read");
            var withWords = 0;
            foreach (var text in texts)
            {
                var segments = glossary.Segments(text);
                Assert.That(string.Concat(segments.Select(s => s.Text)), Is.EqualTo(text), text);
                var keys = segments.Where(s => s.Entry != null).Select(s => s.Key).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
                var explained = glossary.Explain(text).Select(e => e.Name.ToLowerInvariant()).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
                Assert.That(keys, Is.EqualTo(explained), text);
                if (keys.Count > 0) withWords++;
            }
            Assert.That(withWords, Is.GreaterThan(20), "the doors do carry glossary words (statuses, Block, Poise, Stagger)");
        }
    }
}
