using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.Content;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// P-6 (D-1649): the words the Core 2 screens say are the web's where the web has words for them. Tools/oracle-core2-strings.mjs resolves, against the pinned web checkout, each reference of Tools/web-strings.config.json
    /// (a row of its string table content/source/uiStrings.csv, or a literal at a line of src/) to what the web says; this build's text for the same key must say the same (placeholders are one token, curly quotes are straight, case is the
    /// stylesheet's). Every string key the Core 2 flow's screens and views use is either one of those or is listed as having no web row of its own, so a new key has to be classified. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class Core2StringsParityTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "strings");

        private static JObject Config => (JObject)JsonContent.Parse(File.ReadAllText(Path.Combine(TestContent.RepoRoot, "Tools", "web-strings.config.json")));

        private static string Norm(string s) =>
            Regex.Replace(Regex.Replace(Regex.Replace(Regex.Replace(s ?? string.Empty, @"\$\{[^}]*\}", "{}"), @"\{[^}]*\}", "{}")
                .Replace('‘', '\'').Replace('’', '\'').Replace('“', '"').Replace('”', '"').Replace(' ', ' ').Replace(' ', ' '), @"\s+", " ").Trim().ToLowerInvariant(), "^$", string.Empty);

        private static Dictionary<string, string> UnityStrings()
        {
            var all = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in new[] { "en.json", "app.en.json", "meta.en.json", "combat.en.json", "nodes.en.json", "shop.en.json", "events.en.json" })
            {
                var path = Path.Combine(TestContent.RepoRoot, "Unity", "Assets", "StreamingAssets", "Content", "strings", file);
                if (!File.Exists(path)) continue;
                foreach (var p in ((JObject)JsonContent.Parse(File.ReadAllText(path))).Properties()) if (p.Value.Type == JTokenType.String) all[p.Name] = (string)p.Value;
            }
            return all;
        }

        [Test]
        public void TheOracleIsPresentAndPinned()
        {
            var oracle = OracleDocuments.Read(Path.Combine(Dir, "strings.json"));
            Assert.That(oracle.Value<string>("webSha"), Is.Not.Empty, "the oracle names the web checkout it ran on");
            Assert.That(oracle.Value<int>("keys"), Is.GreaterThan(400));
            Assert.That(oracle.Value<int>("keys"), Is.EqualTo(Config["refs"].Count()), "every reference was resolved");
        }

        [Test]
        public void EveryKeyThatHasAWebCounterpartSaysWhatTheWebSays()
        {
            var oracle = OracleDocuments.Read(Path.Combine(Dir, "strings.json"));
            var unity = UnityStrings();
            var wrong = new List<string>();
            foreach (var p in ((JObject)oracle["strings"]).Properties())
            {
                if (!unity.TryGetValue(p.Name, out var ours)) { wrong.Add(p.Name + ": this build has no such string"); continue; }
                var web = (string)p.Value["web"];
                if (Norm(ours) != Norm(web)) wrong.Add(p.Name + "\n    ours: " + ours + "\n    web:  " + web + "   (" + (string)p.Value["ref"] + ")");
            }
            Assert.That(wrong, Is.Empty, string.Join("\n", wrong.Take(30)));
        }

        [Test]
        public void EveryStringTheCore2FlowUsesIsMappedOrListedAsHavingNoWebRow()
        {
            var config = Config;
            var legacy = new HashSet<string>(config["legacyFiles"].Select(t => (string)t), StringComparer.Ordinal);
            var mapped = new HashSet<string>(((JObject)config["refs"]).Properties().Select(p => p.Name), StringComparer.Ordinal);
            var listed = new HashSet<string>(config["unchecked"].Select(t => (string)t), StringComparer.Ordinal);
            var gameRoot = Path.Combine(TestContent.RepoRoot, "Unity", "Assets", "Game");
            var constants = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(gameRoot, "Generated", "StringKeys.g.cs")), "public const string (\\w+) = \"([^\"]*)\";")) constants[m.Groups[1].Value] = m.Groups[2].Value;

            var used = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var layer in new[] { "Presentation", "Application" })
                foreach (var file in Directory.GetFiles(Path.Combine(gameRoot, layer), "*.cs", SearchOption.AllDirectories).Where(f => !legacy.Contains(Path.GetFileName(f))))
                    foreach (Match m in Regex.Matches(File.ReadAllText(file), @"StringKeys\.(\w+)"))
                        if (constants.TryGetValue(m.Groups[1].Value, out var key)) used.Add(key);

            var strings = UnityStrings();
            var unclassified = used.Where(k => strings.ContainsKey(k) && !mapped.Contains(k) && !listed.Contains(k)).ToList();
            Assert.That(unclassified, Is.Empty, "classify a new string: give it the web's text and a reference in Tools/web-strings.config.json, or list it as unchecked (no web row of its own):\n" + string.Join("\n", unclassified.Take(40)));
            Assert.That(used.Count, Is.GreaterThan(700), "the Core 2 flow's keys were read from the screens and views");
        }
    }
}
