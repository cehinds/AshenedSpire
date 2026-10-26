using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Ashen.Content;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// WP-2 (D-168): the C# Core 2 loader builds what the web builds for the profile 'default' (Tools/oracle-core2-registries.mjs →
    /// Unity/Assets/Tests/Oracle/core2/registries). Every id-keyed table equals the dump (ids in registry order, each row by
    /// canonical JSON and in key order), every data registry the C# side reads equals its object dump, the profile document and
    /// hash are verbatim, and every profile content key equals its file. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2RegistryParityTests
    {
        private static readonly string OracleDir = Path.Combine(TestContent.OracleRoot, "core2", "registries");
        private const string RowKeyFallback = "tag";
        private static Core2Registries _core2;

        /// <summary>Dumped registries the loader does not build: computed by web code from other registries or modules, not content.</summary>
        private static readonly HashSet<string> NotContent = new HashSet<string>(StringComparer.Ordinal)
        {
            "contentVersion", "eventHistoryRequirements", "eventSpeakers", "framework", "frameworkTerms", "questChains", "scripts", "statDomains", "tree",
        };

        private static Core2Registries Core2
        {
            get
            {
                if (_core2 != null) return _core2;
                var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
                return _core2 = Core2Registries.Load(snapshot.Content);
            }
        }

        private static JToken Dump(string relative) => JsonContent.Parse(File.ReadAllText(Path.Combine(OracleDir, relative)));

        private static IEnumerable<string> Names(string folder) =>
            Directory.GetFiles(Path.Combine(OracleDir, folder), "*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.Ordinal);

        private static IEnumerable<TestCaseData> TableCases() => Names("tables").Select(n => new TestCaseData(n).SetName("Core2Table_" + n));
        private static IEnumerable<TestCaseData> ObjectCases() => Names("objects").Where(n => !NotContent.Contains(n)).Select(n => new TestCaseData(n).SetName("Core2Object_" + n));
        private static IEnumerable<TestCaseData> ContentCases() => Names(Path.Combine("profile", "content")).Select(n => new TestCaseData(n).SetName("Core2Content_" + n));

        [TestCaseSource(nameof(TableCases))]
        public void TableMatchesTheWebRegistry(string table)
        {
            var oracle = (JObject)Dump(Path.Combine("tables", table + ".json"));
            var actual = Core2.Table(table);
            Assert.That(actual, Is.Not.Null, table);
            var rows = (JArray)oracle["rows"];
            // The property rules are keyed by the tag they give behaviour to, so the dump's ids are null for them.
            var expectedIds = oracle["ids"].Select((t, i) => (string)t ?? (string)rows[i][RowKeyFallback]).ToList();
            Assert.That(actual.Properties().Select(p => p.Name).ToList(), Is.EqualTo(expectedIds), table + " ids in registry order");
            Assert.That((int)oracle["count"], Is.EqualTo(expectedIds.Count));
            for (var i = 0; i < rows.Count; i++) AssertSame(table + "/" + expectedIds[i], actual[expectedIds[i]], rows[i]);
        }

        [TestCaseSource(nameof(ObjectCases))]
        public void DataRegistryMatchesTheWebRegistry(string name)
        {
            var actual = Core2.Object(name);
            Assert.That(actual, Is.Not.Null, name);
            AssertSame(name, actual, Dump(Path.Combine("objects", name + ".json")), name == RegistryKeys.Equipment);
        }

        [Test]
        public void EveryDumpedRegistryIsComparedOrNamedAsNotContent()
        {
            var tables = Names("tables").ToList();
            Assert.That(tables.Count, Is.EqualTo(19));
            var objects = Names("objects").ToList();
            Assert.That(NotContent.Except(objects), Is.Empty, "stale exclusion");
            foreach (var name in objects.Where(n => !NotContent.Contains(n))) Assert.That(Core2.Object(name), Is.Not.Null, name);
        }

        [Test]
        public void ProfileIsVerbatimWithTheShippedHash()
        {
            var index = (JObject)Dump("index.json");
            var profile = Core2.Profile;
            Assert.That(profile.Id, Is.EqualTo((string)index["profileId"]));
            Assert.That(profile.Hash, Is.EqualTo((string)index["profileHash"]));
            AssertSame("profile/core", profile.Core, Dump(Path.Combine("profile", "core.json")));
            Assert.That(profile.Validate().IsValid, Is.True);
        }

        [Test]
        public void ProfileContentKeysAreTheDumpedOnes()
        {
            Assert.That(Core2.Profile.ContentKeys.ToList(), Is.EqualTo(Names(Path.Combine("profile", "content")).ToList()));
        }

        [TestCaseSource(nameof(ContentCases))]
        public void ProfileContentMatchesTheWebProfile(string key) =>
            AssertSame("profile/content/" + key, Core2.Profile.Content(key), Dump(Path.Combine("profile", "content", key + ".json")));

        // ------------------------------------------------------------------ accessors and validation

        [Test]
        public void TypedAccessorsReadTheProfileContent()
        {
            Assert.That(Core2.Characters.Count, Is.EqualTo(37));
            Assert.That(Core2.ProfileCards.Count, Is.EqualTo(211));
            Assert.That(Core2.EnemyCards.Count, Is.EqualTo(138));
            Assert.That(Core2.EnemyRelics.Count, Is.EqualTo(6));
            Assert.That(Core2.Gear.Count, Is.EqualTo(47));
            Assert.That(Core2.Enemies.Count, Is.EqualTo(33));
            Assert.That(Core2.EnemyMoves.Count, Is.EqualTo(138));
            foreach (var token in new[] { Core2.Stats, Core2.Routes, Core2.Rests, Core2.Rewards, Core2.Shops, Core2.ClassUnlocks, Core2.CardPools,
                                          Core2.StarterDeck, Core2.StartingHands, Core2.ClassAttributes, Core2.ResourceVariants })
                Assert.That(token, Is.Not.Null);
            var first = Core2.Enemies.Properties().First().Value;
            var moves = Core2.MovesOf((string)first[RegistryKeys.Id]);
            Assert.That(moves.Count, Is.GreaterThan(0));
            Assert.That(moves.Properties().All(m => m.Value["tags"] is JArray), Is.True, "nested moves are the stamped flat rows");
        }

        [Test]
        public void ReadsReturnCopies()
        {
            Core2.Cards.RemoveAll();
            Core2.Profile.Core.RemoveAll();
            Assert.That(Core2.Cards.Count, Is.EqualTo(529));
            Assert.That(Core2.Profile.Id, Is.Not.Empty);
        }

        [Test]
        public void TheLegacyRegistriesAreUntouched()
        {
            var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
            var legacy = RuntimeRegistries.Build(snapshot.Content, (JObject)snapshot.Content.Get(ContentFiles.StringsEn));
            Assert.That(legacy.Table(RegistryKeys.Cards).Count, Is.EqualTo(TestContent.ContentJson(ContentFiles.CatalogCards).Count));
            Assert.That(legacy.Table(Core2BundleKeys.Resources), Is.Null);
        }

        private static ContentSet WithoutPaths(Func<string, bool> drop, Action<IDictionary<string, JToken>> edit = null)
        {
            var snapshot = new ConfigLayers(TestContent.Source).Build(new LayerSelection { PresetId = "shipped" });
            var files = snapshot.Content.Paths.Where(p => !drop(p)).ToDictionary(p => p, p => snapshot.Content.Get(p));
            edit?.Invoke(files);
            return new ContentSet(files);
        }

        [Test]
        public void MissingModulesAndContentKeysAreReportedLikeContentIssues()
        {
            var content = WithoutPaths(p => p == ContentFiles.ProfileContentRoutes, files =>
            {
                var core = (JObject)files[ContentFiles.ProfileCore];
                ((JObject)core[Core2ProfileKeys.Modules]).Remove(Core2ModuleIds.Rest);
                core[Core2ProfileKeys.Hash] = "sha256:short";
                core.Remove(Core2ProfileKeys.Foundation);
            });
            var report = CoreProfile.Load(content).Validate();
            Assert.That(report.IsValid, Is.False);
            Assert.That(report.ByRule(Core2ProfileRules.MissingModule).Select(i => i.Path), Is.EqualTo(new[] { Core2ProfileKeys.Modules + "." + Core2ModuleIds.Rest }));
            Assert.That(report.ByRule(Core2ProfileRules.MissingContent).Select(i => i.File), Is.EqualTo(new[] { ContentFiles.ProfileContentRoutes }));
            Assert.That(report.ByRule(Core2ProfileRules.BadHash).Count(), Is.EqualTo(1));
            Assert.That(report.ByRule(Core2ProfileRules.MissingFoundation).Count(), Is.EqualTo(1));
            Assert.That(report.Summary(10), Does.Contain(Core2ModuleIds.Rest).And.Contain(Core2ProfileContentKeys.Routes));
            var thrown = Assert.Throws<InvalidOperationException>(() => Core2Registries.Load(content));
            Assert.That(thrown.Message, Does.Contain(Core2ProfileRules.MissingContent));
        }

        [Test]
        public void AContentSetWithoutAProfileCannotLoad() =>
            Assert.Throws<InvalidOperationException>(() => CoreProfile.Load(WithoutPaths(p => p == ContentFiles.ProfileCore)));

        // ------------------------------------------------------------------ comparison

        /// <summary>Equal as canonical JSON (numbers as doubles) and, since key order is semantic after projection (D-040), in object key order.</summary>
        private static void AssertSame(string what, JToken actual, JToken expected, bool topLevelOrderFree = false)
        {
            var difference = FirstDifference(actual, expected, what, topLevelOrderFree);
            Assert.That(difference, Is.Null, difference);
        }

        private static string FirstDifference(JToken actual, JToken expected, string path, bool orderFree = false)
        {
            if (actual is JObject ao && expected is JObject eo)
            {
                var a = ao.Properties().Select(p => p.Name).ToList();
                var e = eo.Properties().Select(p => p.Name).ToList();
                foreach (var key in e.Union(a))
                {
                    if (ao[key] == null || eo[key] == null) return path + "." + key + ": " + (ao[key] == null ? "missing" : "unexpected");
                    var inner = FirstDifference(ao[key], eo[key], path + "." + key);
                    if (inner != null) return inner;
                }
                return orderFree || a.SequenceEqual(e, StringComparer.Ordinal) ? null : path + ": key order [" + string.Join(",", a.Take(12)) + "] vs [" + string.Join(",", e.Take(12)) + "]";
            }
            if (actual is JArray aa && expected is JArray ea)
            {
                for (var i = 0; i < Math.Min(aa.Count, ea.Count); i++)
                {
                    var inner = FirstDifference(aa[i], ea[i], path + "[" + i + "]");
                    if (inner != null) return inner;
                }
                return aa.Count == ea.Count ? null : path + ": length " + aa.Count + " vs " + ea.Count;
            }
            return Scalar(actual) == Scalar(expected) ? null : path + ": got " + Show(actual) + ", expected " + Show(expected);
        }

        private static string Scalar(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return "null";
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
                return "n" + double.Parse(Convert.ToString(((JValue)t).Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
            return t.Type + ":" + t.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string Show(JToken t)
        {
            var s = t == null ? "(missing)" : t.ToString(Newtonsoft.Json.Formatting.None);
            return s.Length > 200 ? s.Substring(0, 200) + "..." : s;
        }
    }
}
