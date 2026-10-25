using System;
using System.IO;
using System.Linq;
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
    /// P-3a (D-1480..D-1489): the profile archive against the shipped web. Tools/oracle-core2-archive.mjs drives engine/save.js's drawer through its public doors (an unreadable profile read again and again, a new profile, unreadable
    /// runs, a hundred and ninety days on, thirty profiles) over a clock it holds; the C# drawer replays what went in and says the same listing and notices after every step, and the same export. The words the Profile screen puts on an
    /// entry (humanTime, humanSize, describe, humanReason, the rail's counts) are the web's, run as they stand. The restore, the new profile and the unreadable run go through the real SaveService. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class ProfileArchiveTests
    {
        private static string Dir => Path.Combine(TestContent.OracleRoot, "core2", "archive");

        private static UiData _ui;

        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        private string _dir;
        private DateTime _now;
        private SaveService _saves;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-archive-" + Guid.NewGuid().ToString("N"));
            _now = new DateTime(2026, 10, 24, 16, 38, 0, DateTimeKind.Utc);
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)), () => _now);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private static DateTime AtMs(long ms) => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);

        private static JArray Listing(ArchiveDrawer drawer) => new JArray(drawer.List().Select(e => new JObject
        {
            ["at"] = e.At,
            ["bytes"] = e.Bytes,
            ["count"] = e.Count > 0 ? (JToken)e.Count : JValue.CreateNull(),
            ["id"] = e.Id,
            ["kind"] = e.Kind,
            ["lastSeenAt"] = e.LastSeenAt == null ? JValue.CreateNull() : (JToken)e.LastSeenAt,
            ["reason"] = e.Reason,
            ["slot"] = e.Slot.HasValue ? (JToken)e.Slot.Value : JValue.CreateNull(),
        }));

        private static JArray Notices(ArchiveDrawer drawer) => new JArray(drawer.Notices().Select(n => new JObject { ["at"] = n.At, ["id"] = n.Id, ["kind"] = n.Kind, ["was"] = n.Was }));

        [Test]
        public void TheOracleIsPresent()
        {
            var index = OracleDocuments.Read(Path.Combine(Dir, "index.json"));
            Assert.That(index.Value<string>("profileHash"), Is.EqualTo(Core2HostParityTests.Content.Core2.ProfileHash), "the oracle ran the profile that is loaded");
            Assert.That(index["tally"].Value<int>("steps"), Is.GreaterThan(40));
            Assert.That(index["tally"].Value<int>("finalNotices"), Is.GreaterThan(0), "the drawer moved a profile out and said so");
        }

        [Test]
        public void TheDrawerSaysWhatTheWebsDrawerSaysAfterEveryStep()
        {
            var drawer = _saves.Drawer;
            var oracle = OracleDocuments.Read(Path.Combine(Dir, "drawer.json"));
            var exports = ((JArray)oracle["exports"]).OfType<JObject>().ToList();
            var index = 0;
            foreach (var step in ((JArray)oracle["steps"]).OfType<JObject>())
            {
                _now = AtMs(step.Value<long>("now"));
                if (step.Value<string>("kind") == ArchiveKeys.KindMeta) drawer.ArchiveProfile(step.Value<string>("save"), step.Value<string>("reason"));
                else drawer.ArchiveRun(step.Value<string>("save"), step.Value<string>("reason"), step.Value<int>("slot"));
                var diff = RunParityTests.FirstDifference(step["listing"], Listing(drawer), "$.listing") ?? RunParityTests.FirstDifference(step["notices"], Notices(drawer), "$.notices");
                if (diff != null) Assert.Fail("step " + index + ": first difference at " + diff);
                foreach (var export in exports.Where(x => x.Value<int>("step") == index))
                {
                    _now = AtMs(export.Value<long>("now"));
                    Assert.That(drawer.Export(export.Value<string>("id")), Is.EqualTo(export.Value<string>("text")), "the export of " + export.Value<string>("id"));
                }
                index++;
            }
            Assert.That(index, Is.GreaterThan(40));
            Assert.That(Directory.GetFiles(_dir, "archive.profile-*").Length, Is.EqualTo(drawer.Notices().Count), "every profile moved out is a file of its own: nothing was deleted");
            Assert.That(drawer.List().Count(e => e.IsProfile), Is.EqualTo(24), "the profiles' own cap");
            Assert.That(drawer.List().Count(e => !e.IsProfile), Is.LessThanOrEqualTo(12), "the runs' cap");
        }

        [Test]
        public void TheWordsOnAnEntryAreTheWebs()
        {
            var strings = Ui.Strings;
            var words = OracleDocuments.Read(Path.Combine(Dir, "words.json"));
            foreach (var row in ((JArray)words["times"]).OfType<JObject>())
                Assert.That(ProfileArchiveView.HumanTime(row.Value<string>("iso"), strings, TimeZoneInfo.Utc), Is.EqualTo(row.Value<string>("text")), "time " + row.Value<string>("iso"));
            foreach (var row in ((JArray)words["sizes"]).OfType<JObject>())
                Assert.That(ProfileArchiveView.HumanSize(row.Value<int>("bytes"), strings), Is.EqualTo(row.Value<string>("text")), "size " + row.Value<int>("bytes"));
            foreach (var row in ((JArray)words["reasons"]).OfType<JObject>())
            {
                var key = ProfileArchiveView.HumanReason(row.Value<string>("raw"));
                Assert.That(key == null ? string.Empty : strings.Get(key), Is.EqualTo(row.Value<string>("text")), "reason " + row.Value<string>("raw"));
            }
            foreach (var row in ((JArray)words["describes"]).OfType<JObject>())
            {
                var entry = (JObject)row["entry"];
                var archive = new ArchiveEntry { Id = "x", Kind = entry.Value<string>("kind"), Slot = entry["slot"] == null ? (int?)null : entry.Value<int>("slot"), Save = row.Value<string>("save") };
                Assert.That(ProfileArchiveView.Describe(archive, strings), Is.EqualTo(row.Value<string>("text")), "describe " + row.ToString(Newtonsoft.Json.Formatting.None));
            }
        }

        [Test]
        public void TheRailCountsAndSelectsAsTheWebsWorkspaceModelDoes()
        {
            var words = OracleDocuments.Read(Path.Combine(Dir, "words.json"));
            var strings = Ui.Strings;
            foreach (var row in ((JArray)words["workspace"]).OfType<JObject>())
            {
                TearDown();
                SetUp();
                var n = 0;
                foreach (var kind in ((JArray)row["archives"]).Select(k => (string)k))
                {
                    _now = _now.AddMinutes(1);
                    n++;
                    if (kind == ArchiveKeys.KindMeta) _saves.Drawer.ArchiveProfile("{\"n\":" + n + "}", "r");
                    else _saves.Drawer.ArchiveRun("{\"n\":" + n + "}", "r", 1);
                }
                var view = ProfileArchiveView.Build(_saves, ProfileStore.Load(_saves, false), strings, row.Value<string>("current"));
                var want = (JObject)row["view"];
                Assert.That(view.Current, Is.EqualTo(want.Value<string>("current")), row.ToString(Newtonsoft.Json.Formatting.None));
                Assert.That(view.Empty, Is.EqualTo(want.Value<string>("empty")));
                Assert.That(view.Entries.Count, Is.EqualTo(want.Value<int>("entries")));
                var wantCategories = ((JArray)want["categories"]).OfType<JObject>().ToList();
                Assert.That(view.Categories.Select(c => c.Id + ":" + c.Count + ":" + c.Selected), Is.EqualTo(wantCategories.Select(c => c.Value<string>("id") + ":" + c.Value<int>("count") + ":" + c.Value<bool>("selected"))));
            }
        }

        // ---- the real doors

        private void WriteProfile(int runs, int wins) =>
            _saves.Checkpoint(_saves.Rules.ProfileSlot, new JObject { [ArchiveKeys.Progress] = new JObject { [ArchiveKeys.Runs] = runs, [ArchiveKeys.Wins] = wins } }, "c", "s");

        private void Damage(string slot)
        {
            foreach (var file in Directory.GetFiles(_dir, slot + "*")) File.WriteAllText(file, "{\"torn");
        }

        [Test]
        public void AnUnreadableProfileIsSetAsideOnceAndCountedWhenSeenAgain()
        {
            WriteProfile(3, 1);
            Damage(_saves.Rules.ProfileSlot);
            var first = ProfileStore.Load(_saves);
            Assert.That(first.Writable, Is.False);
            Assert.That(first.State, Is.EqualTo(ArchiveKeys.StateCorrupt));
            _now = _now.AddMinutes(5);
            ProfileStore.Load(_saves);
            ProfileStore.Load(_saves, false);
            var entries = _saves.Drawer.List();
            Assert.That(entries.Count, Is.EqualTo(1), "the same bytes are one entry");
            Assert.That(entries[0].Count, Is.EqualTo(2), "seen twice; the screen's own look is not a sighting");
            Assert.That(entries[0].LastSeenAt, Is.Not.Null);
            Assert.That(ProfileArchiveView.HumanReason(entries[0].Reason), Is.EqualTo(StringKeys.ProfileArchReasonUnreadable));
            Assert.That(_saves.ReadRaw(_saves.Rules.ProfileSlot), Is.EqualTo("{\"torn"), "the bytes stay where they are: they are the evidence");
        }

        [Test]
        public void AFailedRestoreNeverConsumesTheCopy()
        {
            WriteProfile(3, 1);
            Damage(_saves.Rules.ProfileSlot);
            ProfileStore.Load(_saves);
            var id = _saves.Drawer.List().Single().Id;
            var result = _saves.RestoreProfile(id);
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Reason, Does.Contain("still cannot be read"));
            Assert.That(_saves.Drawer.Get(id), Is.Not.Null, "the copy is exactly where it was");
            Assert.That(_saves.Exists(ArchiveKeys.ScratchSlot), Is.False, "the check left nothing behind");
            Assert.That(_saves.RestoreProfile("nope").Ok, Is.False);
            _saves.Drawer.ArchiveRun("{}", "r", 1);
            Assert.That(_saves.RestoreProfile(_saves.Drawer.List().First(e => !e.IsProfile).Id).Reason, Does.Contain("not a profile"));
        }

        [Test]
        public void StartingANewProfileKeepsTheOldOneAndRestoringItSetsTheNewOneAside()
        {
            WriteProfile(7, 2);
            var id = _saves.StartNewProfile();
            Assert.That(id, Is.Not.Null);
            Assert.That(_saves.Exists(_saves.Rules.ProfileSlot), Is.False, "the slot is cleared: the next write starts a fresh profile");
            var kept = _saves.Drawer.Get(id);
            Assert.That(kept.Reason, Is.EqualTo(ArchiveKeys.ReasonNewProfile));
            Assert.That(ProfileArchiveView.Describe(kept, Ui.Strings), Is.EqualTo("Profile · 7 runs, 2 wins"), "what the entry IS, read from the stored bytes");

            WriteProfile(1, 0);
            _now = _now.AddMinutes(1);
            var restored = _saves.RestoreProfile(id);
            Assert.That(restored.Ok, Is.True, restored.Reason);
            var live = ProfileStore.Load(_saves, false);
            Assert.That((int)live.Doc[ArchiveKeys.Progress][ArchiveKeys.Runs], Is.EqualTo(7), "the restored profile is the live one");
            Assert.That(live.State, Is.EqualTo(ArchiveKeys.StateOk));
            var outgoing = _saves.Drawer.Get(restored.ArchiveId);
            Assert.That(outgoing.Reason, Is.EqualTo(string.Format(ArchiveKeys.ReasonRestored, id)), "the profile that was in use is set aside in its place, not deleted");
            Assert.That(ProfileArchiveView.Describe(outgoing, Ui.Strings), Is.EqualTo("Profile · 1 runs, 0 wins"));
            Assert.That(_saves.Drawer.Get(id), Is.Not.Null, "restoring does not consume the copy it restored from");
        }

        [Test]
        public void AnUnreadableRunIsSetAsideAndItsSlotCleared()
        {
            var slot = _saves.Rules.RunSlotName(1);
            _saves.Checkpoint(slot, new JObject { ["x"] = 1 }, "c", "s");
            Damage(slot);
            var loaded = Core2RunSession.Load(Core2HostParityTests.Content, _saves, 1);
            Assert.That(loaded.Status, Is.EqualTo(Core2RunLoadStatus.Damaged));
            var entry = _saves.Drawer.List().Single();
            Assert.That(entry.Kind, Is.EqualTo(ArchiveKeys.KindRun));
            Assert.That(entry.Slot, Is.EqualTo(1));
            Assert.That(entry.Save, Is.EqualTo("{\"torn"));
            Assert.That(_saves.Exists(slot), Is.False, "as the web does, the run is set aside and the slot is clear");
            Assert.That(ProfileArchiveView.Describe(entry, Ui.Strings), Is.EqualTo("Run · slot 1"));
        }

        [Test]
        public void AnUnreadableIndexIsSetAsideBeforeAFreshOneStarts()
        {
            File.WriteAllText(Path.Combine(_dir, ArchiveKeys.IndexFile), "{not an index");
            Assert.That(_saves.Drawer.List(), Is.Empty);
            Assert.That(Directory.GetFiles(_dir, "archive.salvage-*").Length, Is.EqualTo(1), "the catalogue must not be the thing that loses the drawer");
            _saves.Drawer.ArchiveRun("{}", "r", 1);
            Assert.That(_saves.Drawer.List().Count, Is.EqualTo(1));
        }

        [Test]
        public void TheStateLineFollowsWhatTheLoadLeft()
        {
            var strings = Ui.Strings;
            Assert.That(ProfileArchiveView.Build(_saves, ProfileStore.Load(_saves, false), strings).StateLine, Is.EqualTo("No profile yet — one is created the moment you begin your first climb."));
            WriteProfile(1, 0);
            var ok = ProfileArchiveView.Build(_saves, ProfileStore.Load(_saves, false), strings);
            Assert.That(ok.StateLine, Is.EqualTo("Your profile is fine."));
            Assert.That(ok.Empty, Is.EqualTo(ProfileArchiveView.EmptyDrawer));
            Assert.That(ok.EmptyText, Does.StartWith("Nothing has been set aside."));
            Damage(_saves.Rules.ProfileSlot);
            var corrupt = ProfileArchiveView.Build(_saves, ProfileStore.Load(_saves), strings, null, TimeZoneInfo.Utc);
            Assert.That(corrupt.StateLine, Is.EqualTo("Your profile could not be read. It has been set aside — it is listed below."));
            Assert.That(corrupt.Entries.Single().Line, Is.EqualTo("Couldn’t be read."));
            Assert.That(corrupt.Entries.Single().Unreadable, Is.True);
            Assert.That(corrupt.Entries.Single().Eyebrow, Does.StartWith("Set aside October 24, 4:38 PM · "));
        }
    }
}
