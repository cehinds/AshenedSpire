using System.IO;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>US-0.10: the game clearly states it was made with AI (owner ruling 2026-09-26; suite: Content).</summary>
    [TestFixture, Category("Content")]
    public class AiDisclosureTests
    {
        private static JObject About => TestContent.ContentJson(AboutKeys.File);
        private static JObject AppStrings => TestContent.ContentJson(AboutKeys.AppStrings);

        [Test]
        public void FullDisclosureStatesAiCreationAndNamesEveryModality()
        {
            var cfg = About[AboutKeys.AiDisclosure];
            var full = (string)AppStrings[(string)cfg[AboutKeys.FullKey]];
            Assert.That(full, Does.Contain("generative AI"));
            foreach (var m in cfg[AboutKeys.Modalities].Select(t => (string)t)) Assert.That(full.ToLowerInvariant(), Does.Contain(m), m);
            Assert.That(full, Does.Contain("no licensed third-party"));
        }

        [Test]
        public void DisclosureIsShownOnTheTitleAndOnFirstLaunch()
        {
            var cfg = About[AboutKeys.AiDisclosure];
            Assert.That((bool)cfg[AboutKeys.ShowOnTitle], Is.True);
            Assert.That((bool)cfg[AboutKeys.ShowOnFirstLaunch], Is.True);
            Assert.That((string)AppStrings[(string)cfg[AboutKeys.ShortKey]], Does.Contain("AI"));
            Assert.That(AppStrings[(string)cfg[AboutKeys.NoticeTitleKey]], Is.Not.Null);
            Assert.That(AppStrings[StringKeys.AboutAiDisclosure], Is.Not.Null, "code-facing StringKeys must stay in sync");
        }

        [Test]
        public void ReadmeAndBuildInfoCarryTheStatement()
        {
            var repo = TestContent.RepoRoot;
            Assert.That(File.ReadAllText(Path.Combine(repo, "README.md")), Does.Contain("created with generative AI"));
            var info = JObject.Parse(File.ReadAllText(Path.Combine(repo, "build-info.json")));
            Assert.That((string)info["aiDisclosure"], Does.Contain("generative AI"));
        }

        [Test]
        public void CreditsSourcesExist()
        {
            foreach (var p in About[AboutKeys.Provenance].Select(t => (string)t))
                Assert.That(File.Exists(Path.Combine(TestContent.RepoRoot, p)), Is.True, p);
        }
    }
}
