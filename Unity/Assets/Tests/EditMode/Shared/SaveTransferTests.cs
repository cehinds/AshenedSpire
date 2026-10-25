using System;
using System.IO;
using System.Linq;
using Ashen.App.Saves;
using Ashen.Content;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ashen.Tests
{
    /// <summary>
    /// P-6 (D-1648): Move your saves (web src/engine/saveTransfer.js, ui/components/offlinePlay.js): a backup holds the profile and every run slot as stored, an import is validated in a scratch folder before it touches anything, keeps a
    /// previous-save backup of what it replaces and refuses with the web's words. Suite: Parity.
    /// </summary>
    [TestFixture, Category("Parity")]
    public class SaveTransferTests
    {
        private string _dir;
        private SaveService _saves;
        private SaveTransfer _transfer;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ashen-transfer-test-" + Guid.NewGuid().ToString("N"));
            _saves = new SaveService(_dir, SaveRules.From(TestContent.ContentJson(ContentFiles.RulesSaves)));
            _transfer = new SaveTransfer(_saves);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private string Slot(int index) => _saves.Rules.RunSlotName(index);

        private void Write(string slot, int marker) => _saves.Checkpoint(slot, new JObject { ["marker"] = marker, ["progress"] = new JObject { ["runs"] = marker } }, "c", "s");

        private int Marker(string slot) => (int)_saves.Load(slot).Payload["marker"];

        [Test]
        public void ABackupHoldsTheProfileAndEverySlotAsStored()
        {
            Write(_saves.Rules.ProfileSlot, 7);
            Write(Slot(2), 22);
            var backup = (JObject)JsonContent.Parse(_transfer.CreateBackup());
            Assert.That(backup[TransferKeys.Format].Value<string>(), Is.EqualTo(TransferKeys.FormatName));
            var entries = (JObject)backup[TransferKeys.Entries];
            Assert.That(entries.Count, Is.EqualTo(1 + _saves.Rules.RunSlots));
            Assert.That(entries[_saves.Rules.ProfileSlot].Value<string>(), Is.EqualTo(_saves.ReadRaw(_saves.Rules.ProfileSlot)), "exactly as stored");
            Assert.That(entries[Slot(1)].Type, Is.EqualTo(JTokenType.Null), "an empty slot is null");
            Assert.That(entries[Slot(2)].Value<string>(), Is.EqualTo(_saves.ReadRaw(Slot(2))));
        }

        [Test]
        public void ImportingReplacesEverythingAndKeepsAPreviousSaveBackup()
        {
            Write(_saves.Rules.ProfileSlot, 1);
            Write(Slot(1), 11);
            var backup = _transfer.CreateBackup();
            Write(_saves.Rules.ProfileSlot, 2);
            Write(Slot(1), 12);
            Write(Slot(3), 33);
            var preview = _transfer.Inspect(backup);
            Assert.That(preview.Runs, Is.EqualTo(1));
            Assert.That(preview.HasProfile, Is.True);
            Assert.That(preview.Slots, Is.EqualTo(_saves.Rules.RunSlots));
            Assert.That(Marker(Slot(1)), Is.EqualTo(12), "inspecting touches nothing");

            var before = _transfer.CreateBackup();
            _transfer.Restore(backup);
            Assert.That(Marker(_saves.Rules.ProfileSlot), Is.EqualTo(1));
            Assert.That(Marker(Slot(1)), Is.EqualTo(11));
            Assert.That(_saves.Exists(Slot(3)), Is.False, "a slot the backup leaves empty is emptied");
            Assert.That(_transfer.Previous(), Is.EqualTo(before), "what the import replaced is kept first");
            Assert.That(_transfer.Inspect(_transfer.Previous()).Runs, Is.EqualTo(2), "and it imports in its turn");
        }

        [Test]
        public void ABackupThatCannotBeImportedIsRefusedInTheWebsWordsAndNothingChanges()
        {
            Write(_saves.Rules.ProfileSlot, 5);
            var live = _saves.ReadRaw(_saves.Rules.ProfileSlot);
            Assert.That(Refusal(() => _transfer.Inspect("not json")).Key, Is.EqualTo(StringKeys.TransferErrUnreadable));
            Assert.That(Refusal(() => _transfer.Inspect(new string('x', TransferNumbers.MaxBytes + 1))).Key, Is.EqualTo(StringKeys.TransferErrTooLarge));
            Assert.That(Refusal(() => _transfer.Inspect("{\"format\":\"other\"}")).Key, Is.EqualTo(StringKeys.TransferErrUnsupported));

            var good = (JObject)JsonContent.Parse(_transfer.CreateBackup());
            var entries = (JObject)good[TransferKeys.Entries];
            entries[Slot(2)] = "{\"torn";
            var slot = Refusal(() => _transfer.Restore(good.ToString()));
            Assert.That(slot.Key, Is.EqualTo(StringKeys.TransferErrSlot));
            Assert.That(slot.Args.TryGet(UiPlaceholders.Slot, out var number) && number == "2", Is.True);
            entries[Slot(2)] = JValue.CreateNull();
            entries[_saves.Rules.ProfileSlot] = "{\"torn";
            Assert.That(Refusal(() => _transfer.Restore(good.ToString())).Key, Is.EqualTo(StringKeys.TransferErrProfile));
            Assert.That(_saves.ReadRaw(_saves.Rules.ProfileSlot), Is.EqualTo(live), "a refused import writes nothing");
            Assert.That(_transfer.Previous(), Is.Null, "and keeps no backup");
        }

        private static TransferRefusal Refusal(TestDelegate action)
        {
            var thrown = Assert.Throws<TransferRefusal>(action);
            return thrown;
        }
    }
}
