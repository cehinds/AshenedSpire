using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>US-0.1 asset registry and US-0.2 provenance (suite: Content).</summary>
    [TestFixture, Category("Content")]
    public class AssetRegistryTests
    {
        private static JObject Registry => TestContent.ContentJson(ContentFiles.AssetsRegistry);
        private static string UnityProject => Path.GetFullPath(Path.Combine(TestContent.Root, "..", "..", ".."));

        [Test]
        public void EveryAssetHasAStableIdOriginAndHash()
        {
            var reg = Registry;
            Assert.That(reg.Count, Is.GreaterThan(5000));
            foreach (var p in reg.Properties())
            {
                Assert.That(p.Name, Does.Match(@"^[a-z]+(\.[A-Za-z0-9_-]+)+$"), p.Name);
                var origin = (string)p.Value["origin"];
                Assert.That(origin, Is.EqualTo("imported").Or.EqualTo("generated"), p.Name);
                Assert.That((string)p.Value["sha256"], Does.Match("^[0-9a-f]{64}$"), p.Name);
                Assert.That((string)p.Value["source"], Is.Not.Empty, p.Name);
            }
        }

        [Test]
        public void EveryImportedAssetHasItsCommittedUnityMeta()
        {
            var missing = Registry.Properties()
                .Where(p => p.Value["unityPath"].Type == JTokenType.String)
                .Select(p => (string)p.Value["unityPath"])
                .Where(u => !File.Exists(Path.Combine(UnityProject, u + ".meta")))
                .ToList();
            Assert.That(missing, Is.Empty, "run node Tools/scan-assets.mjs --import and open Unity once:\n" + string.Join("\n", missing.Take(20)));
        }

        [Test]
        public void NothingUnderArtIsUnregistered()
        {
            var registered = Registry.Properties().Where(p => p.Value["unityPath"].Type == JTokenType.String)
                .Select(p => ((string)p.Value["unityPath"]).Replace('/', Path.DirectorySeparatorChar)).ToList();
            var known = new System.Collections.Generic.HashSet<string>(registered.Select(r => Path.GetFullPath(Path.Combine(UnityProject, r)) + ".meta"), StringComparer.OrdinalIgnoreCase);
            var artRoot = Path.Combine(UnityProject, "Assets", "Art");
            if (!Directory.Exists(artRoot)) Assert.Inconclusive("no Art folder");
            var strays = Directory.GetFiles(artRoot, "*.meta", SearchOption.AllDirectories)
                .Where(m => Regex.IsMatch(m, @"\.(png|svg)\.meta$", RegexOptions.IgnoreCase))
                .Where(m => !known.Contains(Path.GetFullPath(m)))
                .ToList();
            Assert.That(strays, Is.Empty, "every shipped image must resolve through the registry:\n" + string.Join("\n", strays.Take(20)));
        }

        [Test]
        public void EnemyArtCoversEveryEnemyInTheCatalog()
        {
            var reg = Registry;
            var enemies = TestContent.ContentJson(ContentFiles.CatalogEnemies);
            foreach (var e in enemies.Properties())
            {
                Assert.That(reg["enemy." + e.Name + ".base"], Is.Not.Null, e.Name + " base art");
                Assert.That(reg.Properties().Count(p => p.Name.StartsWith("enemy." + e.Name + ".state.", StringComparison.Ordinal)), Is.EqualTo(7), e.Name + " state frames");
            }
        }

        [Test]
        public void ProvenanceIsCarriedAndGeneratedAssetsAreDeclared()
        {
            var repo = TestContent.RepoRoot;
            var imported = JObject.Parse(File.ReadAllText(Path.Combine(repo, "Provenance", "imported", "manifest.json")));
            var sources = imported["files"].Select(f => (string)f["source"]).ToList();
            Assert.That(sources, Does.Contain("CREDITS.md"));
            Assert.That(sources.Count(s => s.Contains("prompts")), Is.GreaterThanOrEqualTo(15));
            foreach (var s in sources) Assert.That(File.Exists(Path.Combine(repo, "Provenance", "imported", s)), Is.True, s);

            var generated = JObject.Parse(File.ReadAllText(Path.Combine(repo, "Provenance", "generated", "manifest.json")));
            var declared = generated["assets"].Select(a => (string)a["id"]).ToList();
            var genAssets = Registry.Properties().Where(p => (string)p.Value["origin"] == "generated").Select(p => p.Name).ToList();
            Assert.That(genAssets.Except(declared), Is.Empty, "every generated asset needs a provenance entry (tool, prompt, references)");
        }
    }
}
