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
    /// P-6 (D-1640..D-1644): the profile that would not read, as the web's notice screen has it (src/ui/screens/profileNotice.js): the three states (corrupt, older, newer) the load leaves, what the screen says about each, and the
    /// ways on (restore, save a copy, start a new profile) each leaving the old bytes recoverable. Also the keyboard trap the Profile door keeps, and the glyph the date's narrow no-break space needs. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class ProfileNoticeTests
    {
        private static UiData _ui;

        private static UiData Ui => _ui ??= UiData.Load(TestContent.Source);

        private string _dir;
        private DateTime _now;
        private SaveService _saves;

        private static SaveRules RulesAt(int version)
        {
            var json = TestContent.ContentJson(ContentFiles.RulesSaves);
            json[SaveKeys.CurrentVersion] = version;
            return SaveRules.From(json);
        }

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-notice-" + Guid.NewGuid().ToString("N"));
            _now = new DateTime(2026, 10, 24, 16, 38, 0, DateTimeKind.Utc);
            _saves = new SaveService(_dir, RulesAt(TestContent.ContentJson(ContentFiles.RulesSaves).Value<int>(SaveKeys.CurrentVersion)), () => _now);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private int Current => _saves.Rules.CurrentVersion;

        private void WriteProfileAt(int version, int runs, int wins)
        {
            var writer = new SaveService(_dir, RulesAt(version), () => _now);
            var profile = new JObject
            {
                [ArchiveKeys.Progress] = new JObject { [ArchiveKeys.Runs] = runs, [ArchiveKeys.Wins] = wins },
                [ArchiveKeys.Unlocked] = new JArray("a", "b", "c"),
            };
            writer.Checkpoint(writer.Rules.ProfileSlot, profile, "c", "s");
        }

        [Test]
        public void AProfileFromAnOlderVersionNoMigrationReadsIsNamedOlderAndSetAside()
        {
            WriteProfileAt(Current - 1, 12, 4);
            var loaded = ProfileStore.Load(_saves);
            Assert.That(loaded.State, Is.EqualTo(ArchiveKeys.StateOlder), "the web refuses BY NAME rather than guessing at a shape nobody wrote");
            Assert.That(loaded.Writable, Is.False);
            Assert.That(loaded.Reason, Does.Contain("is older than this build"));
            var entry = _saves.Drawer.Get(loaded.ArchiveId);
            Assert.That(entry, Is.Not.Null, "the bytes are in the drawer");
            Assert.That(ProfileArchiveView.HumanReason(entry.Reason), Is.EqualTo(StringKeys.ProfileArchReasonOlder), "the drawer words it From an older version");
            Assert.That(ProfileArchiveView.Build(_saves, ProfileStore.Load(_saves, false), Ui.Strings).StateLine, Is.EqualTo("Your profile is from an older version we cannot open. It has been set aside — it is listed below."));
        }

        [Test]
        public void ANewerProfileIsLeftWhereItIsAndNothingIsArchived()
        {
            WriteProfileAt(Current + 1, 5, 2);
            var before = _saves.ReadRaw(_saves.Rules.ProfileSlot);
            var loaded = ProfileStore.Load(_saves);
            Assert.That(loaded.State, Is.EqualTo(ArchiveKeys.StateNewer));
            Assert.That(loaded.ArchiveId, Is.Null);
            Assert.That(_saves.Drawer.List(), Is.Empty, "the bytes are fine, just from the future");
            Assert.That(_saves.ReadRaw(_saves.Rules.ProfileSlot), Is.EqualTo(before));
        }

        [Test]
        public void TheCorruptNoticeSaysWhatSurvivedAndWhereItIs()
        {
            WriteProfileAt(Current, 12, 4);
            foreach (var file in Directory.GetFiles(_dir, _saves.Rules.ProfileSlot + "*")) File.WriteAllText(file, "{\"torn");
            var view = ProfileNoticeView.Build(_saves, ProfileStore.Load(_saves), Ui.Strings, TimeZoneInfo.Utc);
            Assert.That(view.Newer, Is.False);
            Assert.That(view.DoorTitle, Is.EqualTo("Couldn’t be read"));
            Assert.That(view.Heading, Is.EqualTo("We couldn’t read your profile"));
            Assert.That(view.Lead.Single(), Is.EqualTo("So we’ve set it aside instead of deleting it — it’s still here."));
            Assert.That(view.CardEyebrow, Is.EqualTo("Your profile"));
            Assert.That(view.CardName, Is.EqualTo("Set aside"), "torn bytes: nothing is guessed");
            Assert.That(view.CardMuted, Is.True);
            Assert.That(view.CardMeta, Does.StartWith("Set aside October 24, 4:38 PM · ").And.EndWith("stored with this game’s data on this device."));
            Assert.That(new[] { view.RestoreLabel, view.ExportLabel, view.LeaveLabel, view.FreshLabel }, Is.EqualTo(new[] { "Try to restore", "Save a copy to a file", "Not now", "Start a new profile" }));
            Assert.That(view.Reason, Is.Not.Null.And.Not.Empty, "the raw engine text rides behind Details for support");
        }

        [Test]
        public void TheOlderNoticeReadsWhatTheSetAsideBytesStillSay()
        {
            WriteProfileAt(Current - 1, 12, 4);
            var view = ProfileNoticeView.Build(_saves, ProfileStore.Load(_saves), Ui.Strings, TimeZoneInfo.Utc);
            Assert.That(view.CardName, Is.EqualTo("12 runs · 4 wins · 3 unlocks"));
            Assert.That(view.CardLine, Is.EqualTo("Saved"));
            Assert.That(view.CardMuted, Is.False);
            Assert.That(view.Heading, Is.EqualTo("We couldn’t read your profile"), "the web words older as it words corrupt");
        }

        [Test]
        public void TheNewerNoticeMakesDoingNothingTheFirstWayOn()
        {
            WriteProfileAt(Current + 1, 5, 2);
            var view = ProfileNoticeView.Build(_saves, ProfileStore.Load(_saves), Ui.Strings, TimeZoneInfo.Utc);
            Assert.That(view.Newer, Is.True);
            Assert.That(view.DoorTitle, Is.EqualTo("A newer version"));
            Assert.That(view.Heading, Is.EqualTo("This profile is from a newer version"));
            Assert.That(view.Lead.Count, Is.EqualTo(2));
            Assert.That(view.LeadEmphasis, Is.True);
            Assert.That(view.Lead[1], Is.EqualTo("Update Ashen Spire to open it again."));
            Assert.That(view.CardName, Is.EqualTo("Untouched"));
            Assert.That(view.CardMeta, Is.EqualTo("Stored with this game’s data on this device."));
            Assert.That(view.LeaveLabel, Is.EqualTo("Keep it and close"));
            Assert.That(view.RestoreLabel, Is.Null, "there is nothing to restore: the bytes were never moved");
            Assert.That(view.FreshLabel, Is.EqualTo("Start a new profile anyway"));
        }

        [Test]
        public void TryToRestoreFailsPlainlyAndNeverConsumesTheCopy()
        {
            WriteProfileAt(Current, 3, 1);
            foreach (var file in Directory.GetFiles(_dir, _saves.Rules.ProfileSlot + "*")) File.WriteAllText(file, "{\"torn");
            var loaded = ProfileStore.Load(_saves);
            var result = _saves.RestoreProfile(loaded.ArchiveId);
            Assert.That(result.Ok, Is.False);
            Assert.That(_saves.Drawer.Get(loaded.ArchiveId), Is.Not.Null);
            Assert.That(Ui.Strings.Get(StringKeys.ProfileArchRestoreFailed), Does.StartWith("That didn’t work — those bytes still can’t be read."));
        }

        [Test]
        public void StartingANewProfileFromANewerOneKeepsItsBytesInTheDrawer()
        {
            WriteProfileAt(Current + 1, 5, 2);
            var before = _saves.ReadRaw(_saves.Rules.ProfileSlot);
            ProfileStore.Load(_saves);
            var id = _saves.StartNewProfile();
            Assert.That(_saves.Drawer.Get(id).Save, Is.EqualTo(before), "no path replaces the profile without its old bytes being recoverable");
            Assert.That(_saves.Exists(_saves.Rules.ProfileSlot), Is.False);
        }

        [Test]
        public void SavingACopyWorksInEveryStateFromTheArchiveOrTheLiveProfile()
        {
            WriteProfileAt(Current + 1, 5, 2);
            var loaded = ProfileStore.Load(_saves);
            var text = _saves.ExportProfile(loaded.State);
            var export = (JObject)JsonContent.Parse(text);
            Assert.That(export[ArchiveKeys.Game].Value<string>(), Is.EqualTo(ArchiveKeys.GameName));
            Assert.That(export[ArchiveKeys.State].Value<string>(), Is.EqualTo(ArchiveKeys.StateNewer));
            Assert.That(export[ArchiveKeys.Note].Value<string>(), Is.EqualTo(ArchiveKeys.ProfileNote));
            Assert.That(export[ArchiveKeys.ProfileKey].Value<string>(), Is.EqualTo(_saves.ReadRaw(_saves.Rules.ProfileSlot)), "exactly as it was stored");
            Assert.That(new SaveService(Path.Combine(_dir, "empty"), _saves.Rules).ExportProfile(ArchiveKeys.StateEmpty), Is.Null, "no profile, nothing to save");
        }

        [Test]
        public void TheProfileDoorKeepsTheKeyboardAndPadInsideItself()
        {
            // The web's Tab trap (profileArchive.js openProfileArchive focusStops): Tab on the last control goes to the first, Shift+Tab on the first to the last. Here one focus model walks the screen's own regions with wrap,
            // and the router swallows UI Toolkit's own navigation, so focus cannot reach another screen.
            Assert.That(Ashen.App.Ui.FocusOrder.Step(4, 3, 1), Is.EqualTo(0));
            Assert.That(Ashen.App.Ui.FocusOrder.Step(4, 0, -1), Is.EqualTo(3));
            var def = Ui.Screens.Get(ScreenIds.Profile);
            Assert.That(def.FocusOrder, Is.EqualTo(new[] { UiNames.WorkspaceNav, UiNames.ProfilePane, UiNames.FooterPrimary, UiNames.ShellExit }), "every control of the door is in the one order, and nothing outside it");
            Assert.That(def.IsModal, Is.False, "a screen of its own: the one before it is not reachable");
            Assert.That(Ui.Screens.Get(ScreenIds.ProfileRecovery).FocusOrder, Is.EqualTo(new[] { UiNames.RecoveryBody }));
        }

        [TestCase("Inter-Regular.ttf")]
        [TestCase("Inter-Italic.ttf")]
        [TestCase("Cinzel-Regular.ttf")]
        public void TheFontsCarryTheNarrowNoBreakSpaceTheBrowserPutsBeforeAmOrPm(string file)
        {
            var path = Path.Combine(TestContent.RepoRoot, "Unity", "Assets", "Game", "Presentation", "UI", "Resources", "UI", "Fonts", file);
            Assert.That(TrueTypeCoverage.Covers(File.ReadAllBytes(path), 0x202F), Is.True, file);
        }
    }

    /// <summary>The smallest reader of a TrueType font's character map (format 4, the Basic Multilingual Plane): does the font map a code point to a glyph.</summary>
    internal static class TrueTypeCoverage
    {
        private static ushort U16(byte[] b, int at) => (ushort)((b[at] << 8) | b[at + 1]);

        private static uint U32(byte[] b, int at) => (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);

        public static bool Covers(byte[] font, int codePoint)
        {
            var tables = U16(font, 4);
            var cmap = -1;
            for (var i = 0; i < tables; i++)
            {
                var at = 12 + 16 * i;
                if (System.Text.Encoding.ASCII.GetString(font, at, 4) == "cmap") cmap = (int)U32(font, at + 8);
            }
            if (cmap < 0) return false;
            var count = U16(font, cmap + 2);
            for (var i = 0; i < count; i++)
            {
                var sub = cmap + (int)U32(font, cmap + 4 + 8 * i + 4);
                if (U16(font, sub) != 4) continue;
                var segX2 = U16(font, sub + 6);
                var ends = sub + 14;
                var starts = ends + segX2 + 2;
                var deltas = starts + segX2;
                var offsets = deltas + segX2;
                for (var s = 0; s < segX2 / 2; s++)
                {
                    if (codePoint > U16(font, ends + 2 * s) || codePoint < U16(font, starts + 2 * s)) continue;
                    var offset = U16(font, offsets + 2 * s);
                    var glyph = offset == 0
                        ? (U16(font, deltas + 2 * s) + codePoint) & 0xFFFF
                        : U16(font, offsets + 2 * s + offset + 2 * (codePoint - U16(font, starts + 2 * s)));
                    if (glyph != 0) return true;
                }
            }
            return false;
        }
    }
}
