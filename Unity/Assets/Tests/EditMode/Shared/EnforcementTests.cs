using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Ashen.Content;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// US-0.8: the data-driven rule cannot erode (docs/design/08 §12, suite: Enforcement).
    /// Scans C# under Unity/Assets/Game (excluding Generated) with a small lexer: comments are skipped,
    /// string/char/number literals are collected with their file and line.
    /// </summary>
    [TestFixture, Category("Enforcement")]
    public class EnforcementTests
    {
        private static readonly string[] ScannedAssemblies = { "Domain", "Application", "Content", "Presentation", "Platform" };
        private static readonly string[] EngineFreeAssemblies = { "Generated", "Domain", "Application", "Content" };
        private static readonly HashSet<string> AllowedNumbers = new HashSet<string> { "0", "1", "0u", "1u", "0m", "1m", "0L", "1L" };

        /// <summary>Layer order (docs/design/08 §2): an assembly may reference only assemblies to its left.</summary>
        private static readonly string[] LayerOrder = { "Ashen.Generated", "Ashen.Domain", "Ashen.Content", "Ashen.Application", "Ashen.Platform", "Ashen.Presentation" };

        private static string GameRoot => Path.GetFullPath(Path.Combine(TestContent.Root, "..", "..", "Game"));

        private sealed class Literal
        {
            public string File;
            public int Line;
            public string Kind;
            public string Text;
            public override string ToString() => Path.GetFileName(File) + ":" + Line + " " + Kind + " " + Text;
        }

        private static IEnumerable<string> Sources(IEnumerable<string> assemblies) =>
            assemblies.SelectMany(a => Directory.Exists(Path.Combine(GameRoot, a))
                ? Directory.GetFiles(Path.Combine(GameRoot, a), "*.cs", SearchOption.AllDirectories)
                : Array.Empty<string>());

        /// <summary>Minimal C# lexer: returns string, char and numeric literals outside comments.</summary>
        private static List<Literal> Scan(string file)
        {
            var src = File.ReadAllText(file);
            var result = new List<Literal>();
            var line = 1;
            var i = 0;
            while (i < src.Length)
            {
                var c = src[i];
                if (c == '\n') { line++; i++; continue; }
                if (c == '/' && i + 1 < src.Length && src[i + 1] == '/') { while (i < src.Length && src[i] != '\n') i++; continue; }
                if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) { if (src[i] == '\n') line++; i++; }
                    i += 2;
                    continue;
                }
                var verbatim = c == '@' && i + 1 < src.Length && src[i + 1] == '"';
                var interpolated = c == '$' && i + 1 < src.Length && src[i + 1] == '"';
                if (c == '"' || verbatim || interpolated)
                {
                    var start = line;
                    var sb = new StringBuilder();
                    i += c == '"' ? 1 : 2;
                    while (i < src.Length)
                    {
                        if (!verbatim && src[i] == '\\') { sb.Append(src[i]).Append(src[i + 1]); i += 2; continue; }
                        if (src[i] == '"') { if (verbatim && i + 1 < src.Length && src[i + 1] == '"') { sb.Append('"'); i += 2; continue; } i++; break; }
                        if (src[i] == '\n') line++;
                        sb.Append(src[i]);
                        i++;
                    }
                    result.Add(new Literal { File = file, Line = start, Kind = "string", Text = sb.ToString() });
                    continue;
                }
                if (c == '\'')
                {
                    var end = src.IndexOf('\'', i + (src[i + 1] == '\\' ? 3 : 2));
                    result.Add(new Literal { File = file, Line = line, Kind = "char", Text = src.Substring(i, end - i + 1) });
                    i = end + 1;
                    continue;
                }
                if (char.IsDigit(c) && (i == 0 || !(char.IsLetterOrDigit(src[i - 1]) || src[i - 1] == '_')))
                {
                    var m = Regex.Match(src.Substring(i), @"^(0[xX][0-9A-Fa-f_]+|[0-9][0-9_]*(\.[0-9]+)?([eE][+-]?[0-9]+)?)[uUlLmMfFdD]*");
                    result.Add(new Literal { File = file, Line = line, Kind = "number", Text = m.Value });
                    i += Math.Max(1, m.Length);
                    continue;
                }
                if (char.IsLetter(c) || c == '_') { while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++; continue; }
                i++;
            }
            return result;
        }

        private static List<Literal> AllLiterals() => Sources(ScannedAssemblies).SelectMany(Scan).ToList();

        [Test]
        public void NoMagicStrings()
        {
            var offenders = AllLiterals().Where(l => l.Kind != "number").ToList();
            Assert.That(offenders, Is.Empty, "string/char literals belong in data or Generated/:\n" + string.Join("\n", offenders.Take(40)));
        }

        [Test]
        public void NoMagicNumbers()
        {
            var offenders = AllLiterals().Where(l => l.Kind == "number" && !AllowedNumbers.Contains(l.Text)).ToList();
            Assert.That(offenders, Is.Empty, "numeric literals other than 0/1 belong in data or Generated/:\n" + string.Join("\n", offenders.Take(40)));
        }

        [Test]
        public void NoContentIdsInCode()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var manifest = ContentManifest.Load(TestContent.Source);
            foreach (var f in manifest.Files.Where(f => f.Path.StartsWith("catalog/", StringComparison.Ordinal)))
                if (JsonContent.Parse(TestContent.Source.ReadText(f.Path)) is JObject o && !o.ContainsKey("rows"))
                    foreach (var p in o.Properties()) ids.Add(p.Name);
            var hits = AllLiterals().Where(l => l.Kind == "string" && ids.Contains(l.Text)).ToList();
            Assert.That(ids.Count, Is.GreaterThan(300));
            Assert.That(hits, Is.Empty, "content IDs must never appear in code:\n" + string.Join("\n", hits));
        }

        [Test]
        public void EngineFreeAssembliesDoNotReferenceUnity()
        {
            foreach (var a in EngineFreeAssemblies)
            {
                var asmdef = Directory.GetFiles(Path.Combine(GameRoot, a), "*.asmdef").Single();
                Assert.That((bool?)JObject.Parse(File.ReadAllText(asmdef))["noEngineReferences"], Is.True, asmdef);
            }
            var offenders = Sources(EngineFreeAssemblies).Where(f => Regex.IsMatch(File.ReadAllText(f), @"\busing\s+Unity(Engine|Editor)\b")).ToList();
            Assert.That(offenders, Is.Empty);
        }

        [Test]
        public void AssemblyReferencesPointDownTheLayersOnly()
        {
            foreach (var asmdef in Directory.GetFiles(GameRoot, "*.asmdef", SearchOption.AllDirectories))
            {
                var json = JObject.Parse(File.ReadAllText(asmdef));
                var name = (string)json["name"];
                var level = Array.IndexOf(LayerOrder, name);
                Assert.That(level, Is.GreaterThanOrEqualTo(0), "unknown game assembly " + name);
                foreach (var r in (json["references"] as JArray ?? new JArray()).Select(t => (string)t).Where(r => r.StartsWith("Ashen.", StringComparison.Ordinal)))
                    Assert.That(Array.IndexOf(LayerOrder, r), Is.InRange(0, level - 1), name + " must not reference " + r);
            }
        }

        [Test]
        public void GeneratedCodeIsMarkedAndNotHandEdited()
        {
            foreach (var f in Directory.GetFiles(Path.Combine(GameRoot, "Generated"), "*.cs"))
            {
                Assert.That(f, Does.EndWith(".g.cs"), "only codegen output may live in Generated/");
                Assert.That(File.ReadAllText(f), Does.StartWith("// <auto-generated>"), f);
            }
        }

        [Test]
        public void LexerFindsWhatItShould()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "ashen-lexer-" + Guid.NewGuid().ToString("N") + ".cs");
            File.WriteAllText(tmp, "// \"ignored\" 99\n/* 'x' 7 */ var a = \"hit\"; var b = @\"v\"\"q\"; var c = 'z'; var d = 42; var e = x1 + 0x1F; var f = 1;");
            try
            {
                var lits = Scan(tmp).Select(l => l.Kind + ":" + l.Text).ToList();
                Assert.That(lits, Is.EqualTo(new[] { "string:hit", "string:v\"q", "char:'z'", "number:42", "number:0x1F", "number:1" }));
            }
            finally { File.Delete(tmp); }
        }
    }
}
