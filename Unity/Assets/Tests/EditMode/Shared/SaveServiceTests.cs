using System;
using System.IO;
using System.Linq;
using Ashen.App.Saves;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>US-0.6 save integrity (suite: Saves).</summary>
    [TestFixture, Category("Saves")]
    public class SaveServiceTests
    {
        private string _dir;
        private SaveRules _rules;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-saves-" + Guid.NewGuid().ToString("N"));
            _rules = SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private SaveService Service() => new SaveService(_dir, _rules);
        private static JObject State(int floor) => JObject.Parse("{\"floor\":" + floor + ",\"hp\":31,\"deck\":[\"strike\",\"defend\"]}");
        private string P(string pattern, string slot) => Path.Combine(_dir, string.Format(pattern, slot));

        [Test]
        public void SlotCountAndNamesComeFromData()
        {
            Assert.That(_rules.RunSlots, Is.EqualTo(3));
            Assert.That(_rules.RunSlotName(1), Is.EqualTo("slot1"));
        }

        [Test]
        public void CheckpointRoundTripsAndGenIncrements()
        {
            var s = Service();
            var slot = _rules.RunSlotName(1);
            Assert.That(s.Load(slot).Status, Is.EqualTo(LoadStatus.Empty));
            Assert.That(s.Checkpoint(slot, State(1), "c", "s"), Is.EqualTo(1));
            Assert.That(s.Checkpoint(slot, State(2), "c", "s"), Is.EqualTo(2));
            var r = s.Load(slot);
            Assert.That(r.Status, Is.EqualTo(LoadStatus.Loaded));
            Assert.That(r.Gen, Is.EqualTo(2));
            Assert.That(JToken.DeepEquals(r.Payload, State(2)), Is.True);
            Assert.That(r.Warnings, Is.Empty);
            Assert.That(File.Exists(P(SaveLayout.Mirror, slot)) && File.Exists(P(SaveLayout.Backup, slot)), Is.True);
        }

        [Test]
        public void CorruptPrimaryFallsBackToTheMirror()
        {
            var s = Service();
            var slot = _rules.RunSlotName(2);
            s.Checkpoint(slot, State(5), "c", "s");
            File.WriteAllText(P(SaveLayout.Primary, slot), "{ not json");
            var r = s.Load(slot);
            Assert.That(r.Status, Is.EqualTo(LoadStatus.Loaded));
            Assert.That((int)r.Payload["floor"], Is.EqualTo(5));
            Assert.That(r.Warnings.Single(), Does.Contain("mirror"));
        }

        [Test]
        public void TamperedContentFailsTheHashAndFallsBack()
        {
            var s = Service();
            var slot = _rules.RunSlotName(1);
            s.Checkpoint(slot, State(3), "c", "s");
            var primary = P(SaveLayout.Primary, slot);
            File.WriteAllText(primary, File.ReadAllText(primary).Replace("\"floor\":3", "\"floor\":9"));
            Assert.That((int)s.Load(slot).Payload["floor"], Is.EqualTo(3));
        }

        [Test]
        public void AllCopiesCorruptIsReportedAndFilesAreKept()
        {
            var s = Service();
            var slot = _rules.RunSlotName(3);
            s.Checkpoint(slot, State(1), "c", "s");
            s.Checkpoint(slot, State(2), "c", "s");
            foreach (var f in Directory.GetFiles(_dir, slot + ".*json*")) File.WriteAllText(f, "garbage");
            var r = s.Load(slot);
            Assert.That(r.Status, Is.EqualTo(LoadStatus.Corrupt));
            Assert.That(File.Exists(P(SaveLayout.Primary, slot)), Is.True, "a corrupt save must never be deleted");
        }

        [Test]
        public void NewerVersionIsRefusedAndLeftUntouched()
        {
            var slot = _rules.RunSlotName(1);
            var future = new SaveRules { RunSlots = 3, CurrentVersion = _rules.CurrentVersion + 1, ProfileSlot = "profile", ReplaceRetries = 1, ReplaceRetryDelayMs = 1 };
            new SaveService(_dir, future).Checkpoint(slot, State(7), "c", "s");
            var before = File.ReadAllText(P(SaveLayout.Primary, slot));
            var r = Service().Load(slot);
            Assert.That(r.Status, Is.EqualTo(LoadStatus.RefusedNewer));
            Assert.That(File.ReadAllText(P(SaveLayout.Primary, slot)), Is.EqualTo(before));
        }

        [Test]
        public void OlderVersionsMigrateStepByStep()
        {
            var slot = _rules.RunSlotName(1);
            var v1 = new SaveRules { RunSlots = 3, CurrentVersion = 1, ProfileSlot = "profile", ReplaceRetries = 1, ReplaceRetryDelayMs = 1 };
            new SaveService(_dir, v1).Checkpoint(slot, State(4), "c", "s");
            var v3 = new SaveRules { RunSlots = 3, CurrentVersion = 3, ProfileSlot = "profile", ReplaceRetries = 1, ReplaceRetryDelayMs = 1 };
            var svc = new SaveService(_dir, v3);
            svc.RegisterMigration(1, e => { e["payload"]["cinders"] = 0; return e; });
            svc.RegisterMigration(2, e => { e["payload"]["cinders"] = (int)e["payload"]["cinders"] + 20; return e; });
            var r = svc.Load(slot);
            Assert.That(r.Status, Is.EqualTo(LoadStatus.Loaded));
            Assert.That((int)r.Payload["cinders"], Is.EqualTo(20));
            Assert.That(r.Version, Is.EqualTo(1));
            Assert.That(r.Warnings.Count(w => w.Contains("migrated")), Is.EqualTo(2));
            Assert.That(r.Commands, Is.Empty, "a migrated save never replays old-schema commands (D-017)");
        }

        [Test]
        public void CommandLogReplaysInOrderAndStopsAtATornRecord()
        {
            var s = Service();
            var slot = _rules.RunSlotName(1);
            var gen = s.Checkpoint(slot, State(1), "c", "s");
            for (var i = 0; i < 3; i++) s.Append(slot, gen, i, JObject.Parse("{\"type\":\"playCard\",\"card\":" + i + "}"), "h" + i);
            File.AppendAllText(Path.Combine(_dir, slot + "." + gen + ".log"), "{\"gen\":1,\"seq\":3,\"crc32\":1,\"cmd\":{\"type\":\"x\"}}\n{\"torn");
            var r = s.Load(slot);
            Assert.That(r.Commands.Select(c => (int)c["cmd"]["card"]), Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(r.Warnings.Single(), Does.Contain("truncated at record 3"));
        }

        [Test]
        public void NewCheckpointStartsAFreshLogAndDropsOldGens()
        {
            var s = Service();
            var slot = _rules.RunSlotName(1);
            var g1 = s.Checkpoint(slot, State(1), "c", "s");
            s.Append(slot, g1, 0, JObject.Parse("{\"type\":\"endTurn\"}"), "h");
            var g2 = s.Checkpoint(slot, State(2), "c", "s");
            Assert.That(File.Exists(Path.Combine(_dir, slot + "." + g1 + ".log")), Is.False);
            Assert.That(s.Load(slot).Commands, Is.Empty);
            Assert.That(g2, Is.EqualTo(2));
        }

        [Test]
        public void SlotsAreIsolatedAndDeleteRemovesOnlyThatSlot()
        {
            var s = Service();
            s.Checkpoint(_rules.RunSlotName(1), State(1), "c", "s");
            s.Checkpoint(_rules.RunSlotName(2), State(2), "c", "s");
            s.Delete(_rules.RunSlotName(1));
            Assert.That(s.Exists(_rules.RunSlotName(1)), Is.False);
            Assert.That((int)s.Load(_rules.RunSlotName(2)).Payload["floor"], Is.EqualTo(2));
        }

        [Test]
        public void ProfileUsesTheSameVerifiedMirroredPath()
        {
            var s = Service();
            s.Checkpoint(_rules.ProfileSlot, JObject.Parse("{\"runs\":4,\"wins\":1}"), "c", "s");
            File.Delete(P(SaveLayout.Primary, _rules.ProfileSlot));
            Assert.That((int)s.Load(_rules.ProfileSlot).Payload["wins"], Is.EqualTo(1));
        }

        [TestCase("", 0u)]
        [TestCase("123456789", 0xCBF43926u)]
        public void Crc32MatchesTheIeeeCheckValue(string text, uint expected) => Assert.That(Crc32.Of(text), Is.EqualTo(expected));
    }
}
