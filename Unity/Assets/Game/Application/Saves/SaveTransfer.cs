using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Generated;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Saves
{
    /// <summary>A save file this build will not import, with the words that say why (a string key and its values, so the screen speaks them).</summary>
    public sealed class TransferRefusal : Exception
    {
        public TransferRefusal(string key, StringArgs args = null)
            : base(key)
        {
            Key = key;
            Args = args ?? new StringArgs();
        }

        public string Key { get; }

        /// <summary>The values the message names (the slot, the reason).</summary>
        public StringArgs Args { get; }
    }

    /// <summary>What an inspected backup holds (web saveTransfer.js inspect: the slots with a climb in them, and whether a profile is there).</summary>
    public sealed class TransferPreview
    {
        public int Runs;
        public int Slots;
        public bool HasProfile;
    }

    /// <summary>
    /// Move your saves (web src/engine/saveTransfer.js createSaveTransfer, shown by ui/components/offlinePlay.js under Download &amp; saves): one file holds the profile and every run slot as they are stored. Validation never touches the live
    /// saves: each entry is read in a scratch folder first. An import keeps a previous-save backup of what it replaces before it writes anything, and puts everything back if a write fails.
    /// </summary>
    public sealed class SaveTransfer
    {
        private readonly SaveService _saves;

        public SaveTransfer(SaveService saves) => _saves = saves;

        public int SlotCount => _saves.Rules.RunSlots;

        private IEnumerable<string> Keys => new[] { _saves.Rules.ProfileSlot }.Concat(Enumerable.Range(1, SlotCount).Select(_saves.Rules.RunSlotName));

        private JObject Snapshot()
        {
            var entries = new JObject();
            foreach (var key in Keys)
            {
                var raw = _saves.ReadRaw(key);
                entries[key] = raw == null ? JValue.CreateNull() : (JToken)raw;
            }
            return new JObject
            {
                [TransferKeys.Format] = TransferKeys.FormatName,
                [TransferKeys.Version] = TransferNumbers.Version,
                [TransferKeys.Entries] = entries,
            };
        }

        /// <summary>createBackup(): the profile and every run slot exactly as stored.</summary>
        public string CreateBackup() => Snapshot().ToString(Formatting.Indented);

        private string PreviousPath => Path.Combine(_saves.Folder, TransferKeys.PreviousFile);

        /// <summary>previous(): the backup the last import kept of what it replaced, or null.</summary>
        public string Previous() => File.Exists(PreviousPath) ? File.ReadAllText(PreviousPath, Encoding.UTF8) : null;

        /// <summary>Read a backup without touching the live saves; throws <see cref="TransferRefusal"/> when it cannot be imported.</summary>
        public TransferPreview Inspect(string text) => Validate(text).Preview;

        private sealed class Checked
        {
            public Dictionary<string, string> Entries;
            public TransferPreview Preview;
        }

        private Checked Validate(string text)
        {
            if (text == null || Encoding.UTF8.GetByteCount(text) > TransferNumbers.MaxBytes) throw new TransferRefusal(StringKeys.TransferErrTooLarge);
            JObject data;
            try { data = JsonContent.Parse(text) as JObject; }
            catch (JsonException) { data = null; }
            if (data == null) throw new TransferRefusal(StringKeys.TransferErrUnreadable);
            var entries = data[TransferKeys.Entries] as JObject;
            var keys = Keys.ToList();
            if ((string)data[TransferKeys.Format] != TransferKeys.FormatName || (int?)data[TransferKeys.Version] != TransferNumbers.Version || entries == null || entries.Count != keys.Count
                || !keys.All(k => entries.TryGetValue(k, out var v) && (v.Type == JTokenType.Null || v.Type == JTokenType.String)))
                throw new TransferRefusal(StringKeys.TransferErrUnsupported);

            var scratchDir = Path.Combine(Path.GetTempPath(), string.Format(CultureInfo.InvariantCulture, TransferKeys.ScratchFolder, Guid.NewGuid().ToString(TransferKeys.GuidFormat)));
            try
            {
                var scratch = new SaveService(scratchDir, _saves.Rules);
                var result = new Checked { Entries = new Dictionary<string, string>(), Preview = new TransferPreview { Slots = SlotCount } };
                for (var i = 0; i < keys.Count; i++)
                {
                    var key = keys[i];
                    var raw = entries[key].Type == JTokenType.Null ? null : (string)entries[key];
                    result.Entries[key] = raw;
                    if (raw == null) continue;
                    var isProfile = key == _saves.Rules.ProfileSlot;
                    LoadResult loaded;
                    try
                    {
                        scratch.ReplaceRaw(key, raw);
                        loaded = scratch.Load(key);
                    }
                    catch (IOException)
                    {
                        // The bytes do not even read as a save: the write-back check refuses them (a torn file, text that is not an envelope).
                        loaded = new LoadResult { Status = LoadStatus.Corrupt };
                        loaded.Warnings.Add(string.Format(CultureInfo.InvariantCulture, SaveMessages.NoValidCopy, key));
                    }
                    if (loaded.Status != LoadStatus.Loaded)
                    {
                        var reason = loaded.Warnings.FirstOrDefault() ?? ArchiveKeys.Corrupt;
                        throw isProfile
                            ? new TransferRefusal(StringKeys.TransferErrProfile, new StringArgs().Add(UiPlaceholders.Reason, reason))
                            : new TransferRefusal(StringKeys.TransferErrSlot, new StringArgs().Add(UiPlaceholders.Slot, i).Add(UiPlaceholders.Reason, reason));
                    }
                    if (isProfile) result.Preview.HasProfile = true;
                    else result.Preview.Runs++;
                }
                return result;
            }
            finally
            {
                if (Directory.Exists(scratchDir)) Directory.Delete(scratchDir, true);
            }
        }

        /// <summary>
        /// restore(text): the backup's saves become the live ones. The previous-save backup is written first (and must read back); if any write fails everything is put back and the refusal says so.
        /// </summary>
        public void Restore(string text)
        {
            var checkedBackup = Validate(text);
            var before = Snapshot();
            try
            {
                File.WriteAllText(PreviousPath, before.ToString(Formatting.Indented), Encoding.UTF8);
                if (File.ReadAllText(PreviousPath, Encoding.UTF8) != before.ToString(Formatting.Indented)) throw new IOException(TransferKeys.PreviousFile);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new TransferRefusal(StringKeys.TransferErrWrite);
            }
            try
            {
                foreach (var pair in checkedBackup.Entries) _saves.ReplaceRaw(pair.Key, pair.Value);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                try
                {
                    foreach (var key in Keys) _saves.ReplaceRaw(key, before[TransferKeys.Entries][key].Type == JTokenType.Null ? null : (string)before[TransferKeys.Entries][key]);
                }
                catch (Exception back) when (back is IOException || back is UnauthorizedAccessException)
                {
                    throw new TransferRefusal(StringKeys.TransferErrStopped);
                }
                throw new TransferRefusal(StringKeys.TransferErrWrite);
            }
        }
    }
}
