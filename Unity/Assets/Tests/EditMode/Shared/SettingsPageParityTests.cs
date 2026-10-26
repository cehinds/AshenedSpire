using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// P-3a (D-1450..D-1459): the Settings page against the shipped web. Tools/oracle-core2-settings.mjs reads the configured profile's page: the General groups and topics, the Advanced sections of a release build and with Developer
    /// tools on, and every row's key, words, default, options and whether it is a select. Every row the page draws that the web draws has the web's words, default and options and is a select or a segmented choice as the web's rule
    /// says (`dropdown` or more than three options); the groups are the web's; the Text &amp; lore, Changelog and Game rules groups are on the page. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class SettingsPageParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "settings");

        private static UiData _ui;

        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        private string _dir;
        private SettingsSession _settings;
        private IReadOnlyList<SettingsTabDef> _layout;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-settings-" + Guid.NewGuid().ToString("N"));
            var saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
            _settings = new SettingsSession(Core2HostParityTests.Content, ProfileStore.Load(saves), core2: true);
            _layout = SettingsLayout.From(Ui.Meta);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private static JObject Page => OracleDocuments.Read(Path.Combine(Dir, "page.json"));

        private SettingsPageView Open(string tab, string group = null, string topic = null, bool dev = false) =>
            _settings.Page(Ui, _layout, new SettingsPageQuery { Tab = tab, Group = group, Topic = topic, DevBuild = dev });

        private static IEnumerable<JObject> WebRows()
        {
            var page = Page;
            foreach (var cat in new[] { "General", "Accessibility" })
                foreach (var group in ((JArray)page["general"][cat]).OfType<JObject>())
                    foreach (var topic in ((JArray)group["topics"]).OfType<JObject>())
                        foreach (var row in ((JArray)topic["rows"]).OfType<JObject>()) yield return row;
            foreach (var group in ((JArray)page["advanced"]["developer"]).OfType<JObject>())
                foreach (var topic in ((JArray)group["topics"]).OfType<JObject>())
                    foreach (var row in ((JArray)topic["rows"]).OfType<JObject>()) yield return row;
        }

        [Test]
        public void TheOracleIsPresent()
        {
            var index = OracleDocuments.Read(Path.Combine(Dir, "index.json"));
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Core2HostParityTests.Content.Core2.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(WebRows().Count(), Is.GreaterThan(100));
        }

        [Test]
        public void TheGeneralGroupsAreTheWebs()
        {
            var want = ((JArray)Page["general"]["General"]).Select(g => g.Value<string>("id")).ToList();
            Assert.That(Open("general").Groups.Select(g => g.Label), Is.EqualTo(want));
            var rules = Open("general", "rules");
            Assert.That(rules.ShowRules, Is.True, "the Game rules group is its own panel");
            Assert.That(rules.Rows, Is.Empty);
            Assert.That(rules.Empty, Is.Null);
        }

        [Test]
        public void TheAdvancedSectionsOfAReleaseBuildAreTheWebs()
        {
            var release = ((JArray)Page["advanced"]["release"]).OfType<JObject>().Select(g => g.Value<string>("label")).ToList();
            Assert.That(Open("advanced").Groups.Select(g => g.Label), Is.EqualTo(release));
            var developer = ((JArray)Page["advanced"]["developer"]).OfType<JObject>().Select(g => g.Value<string>("label")).Where(l => release.Contains(l) || l == "Opening sequence").ToList();
            Assert.That(Open("advanced", dev: true).Groups.Select(g => g.Label), Is.EqualTo(developer), "with Developer tools on: the web's order, the sections this build has");
            Assert.That(Open("advanced", "changelog").ShowChangelog, Is.True);
            Assert.That(Open("advanced", "changelog").Empty, Is.Null);
        }

        [Test]
        public void EveryGroupSaysItsTipAsTheWebDoes()
        {
            var web = ((JArray)Page["advanced"]["release"]).OfType<JObject>().ToDictionary(g => g.Value<string>("label"), g => g.Value<string>("tip"));
            foreach (var label in new[] { "Deck", "Interface", "Text & lore" })
            {
                var group = Open("advanced").Groups.Single(g => g.Label == label);
                var page = Open("advanced", group.Id);
                Assert.That(page.GroupTip, Is.EqualTo(web[label]), label);
            }
            Assert.That(Open("advanced", dev: true).Groups.Count, Is.GreaterThan(Open("advanced").Groups.Count));
            Assert.That(Open("advanced", "opening", dev: true).GroupTip, Is.EqualTo(((JArray)Page["advanced"]["developer"]).OfType<JObject>().First(g => g.Value<string>("id") == "Opening").Value<string>("tip")));
        }

        [Test]
        public void TheTextAndLoreRowsAreTheWebsInTheWebsTopics()
        {
            var web = ((JArray)Page["advanced"]["release"]).OfType<JObject>().Single(g => g.Value<string>("id") == "Text");
            var topics = ((JArray)web["topics"]).OfType<JObject>().ToList();
            var first = Open("advanced", "text");
            Assert.That(first.Topics.Select(t => t.Label), Is.EqualTo(topics.Select(t => t.Value<string>("label"))));
            foreach (var topic in topics)
            {
                var shown = Open("advanced", "text", first.Topics.Single(t => t.Label == topic.Value<string>("label")).Id);
                Assert.That(shown.Rows.Select(r => r.Key), Is.EqualTo(((JArray)topic["rows"]).Select(r => r.Value<string>("key"))), topic.Value<string>("label"));
            }
        }

        [Test]
        public void EveryRowThePageSharesWithTheWebHasItsWordsDefaultAndOptions()
        {
            var web = WebRows().GroupBy(r => r.Value<string>("key")).ToDictionary(g => g.Key, g => g.First());
            var seen = 0;
            foreach (var control in _settings.Defs.Controls.Where(c => !c.LegacyOnly && web.ContainsKey(c.Key)))
            {
                var row = web[control.Key];
                seen++;
                var shown = _settings.ControlOf(Ui, control);
                Assert.That(shown.Label, Is.EqualTo(row.Value<string>("label")), control.Key + " label");
                if (control.IsEnum)
                {
                    var choices = row["choices"]?.Select(c => (string)c).ToList();
                    Assert.That(control.Options, Is.EqualTo(choices), control.Key + " options");
                    Assert.That(shown.AsSelect, Is.EqualTo(row.Value<bool>("dropdown") || choices.Count > 3), control.Key + ": a select above three options or when the web says dropdown, else segmented");
                }
                if (control.IsToggle) Assert.That((bool)control.Def, Is.EqualTo(row.Value<bool?>("def") ?? false), control.Key + " default");
            }
            Assert.That(seen, Is.GreaterThan(30), "the rows both pages have");
        }

        [Test]
        public void TheSegmentedRuleSplitsTheWebsRows()
        {
            Assert.That(_settings.ControlOf(Ui, _settings.Defs.Find(SettingIds.CombatPacingPreset)).AsSelect, Is.False, "two options and no dropdown flag");
            Assert.That(_settings.ControlOf(Ui, _settings.Defs.Find(SettingIds.OpeningPlayback)).AsSelect, Is.True, "three options but the web says dropdown");
            Assert.That(_settings.ControlOf(Ui, _settings.Defs.Find(LoreKeys.Face)).AsSelect, Is.True, "nine faces");
            Assert.That(_settings.ControlOf(Ui, _settings.Defs.Find(LoreKeys.Slant)).AsSelect, Is.False);
        }
    }
}
