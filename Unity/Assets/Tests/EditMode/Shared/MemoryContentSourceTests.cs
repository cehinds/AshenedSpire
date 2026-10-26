using System;
using System.IO;
using System.Linq;
using Ashen.App.Ui;
using Ashen.Content;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>D-1251: the in-memory content source the WebGL player fills from StreamingAssets over HTTP.</summary>
    [TestFixture, Category("Content")]
    public class MemoryContentSourceTests
    {
        [Test]
        public void ReadsWhatWasAddedAndNormalisesLineEnds()
        {
            var source = new MemoryContentSource().Add("a/b.json", "{\r\n}\r\n");
            Assert.That(source.Exists("a/b.json"), Is.True);
            Assert.That(source.ReadText("a/b.json"), Is.EqualTo("{\n}\n"));
            Assert.That(source.Count, Is.EqualTo(1));
        }

        [Test]
        public void MissingFilesAreReportedLikeTheDirectorySource()
        {
            var source = new MemoryContentSource();
            Assert.That(source.Exists("nope.json"), Is.False);
            Assert.That(source.Exists(null), Is.False);
            Assert.Throws<FileNotFoundException>(() => source.ReadText("nope.json"));
        }

        [Test]
        public void RefusesPathsOutsideTheContentRoot()
        {
            var source = new MemoryContentSource();
            Assert.Throws<ArgumentException>(() => source.Add("../x.json", "{}"));
            Assert.Throws<ArgumentException>(() => source.Add("/x.json", "{}"));
            Assert.Throws<ArgumentException>(() => source.Add("", "{}"));
        }

        [Test]
        public void TheManifestIsTheFileListAndTheWholeShippedContentLoadsFromMemory()
        {
            var disk = TestContent.Source;
            var memory = new MemoryContentSource();
            memory.Add(Ashen.Generated.ContentLayout.Manifest, disk.ReadText(Ashen.Generated.ContentLayout.Manifest));
            foreach (var entry in ContentManifest.Load(memory).Files) memory.Add(entry.Path, disk.ReadText(entry.Path));

            Assert.That(memory.Paths.Count(), Is.GreaterThan(400));
            var report = new ContentValidator(memory).Validate();
            Assert.That(report.IsValid, Is.True, report.Summary(20));
            Assert.That(UiData.Load(memory).Screens.Initial, Is.EqualTo(UiData.Load(disk).Screens.Initial));
        }
    }
}
