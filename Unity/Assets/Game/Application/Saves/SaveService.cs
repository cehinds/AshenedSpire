using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Ashen.Content;
using Ashen.Generated;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Saves
{
    /// <summary>rules/saves.json.</summary>
    public sealed class SaveRules
    {
        public int RunSlots;
        public int CurrentVersion;
        public string ProfileSlot;
        public int ReplaceRetries;
        public int ReplaceRetryDelayMs;

        public static SaveRules From(JObject json) => new SaveRules
        {
            RunSlots = (int)json[SaveKeys.RunSlots],
            CurrentVersion = (int)json[SaveKeys.CurrentVersion],
            ProfileSlot = (string)json[SaveKeys.ProfileSlot],
            ReplaceRetries = (int)json[SaveKeys.ReplaceRetries],
            ReplaceRetryDelayMs = (int)json[SaveKeys.ReplaceRetryDelayMs],
        };

        public string RunSlotName(int index) => string.Format(CultureInfo.InvariantCulture, SaveLayout.RunSlot, index);
    }

    public enum LoadStatus
    {
        Empty,
        Loaded,
        RefusedNewer,
        Corrupt,
    }

    public sealed class LoadResult
    {
        public LoadStatus Status;
        public int Gen;
        public int Version;
        public string ContentHash;
        public string SnapshotHash;
        public JToken Payload;
        public readonly List<JObject> Commands = new List<JObject>();
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Crash-safe saves (US-0.6, PF-06). A checkpoint writes a gen-stamped, hash-verified envelope with
    /// write-tmp → flush → read-back verify → File.Replace (keeping .bak), then a mirror the same way, then starts
    /// the gen's command log and only then deletes older logs. Loading picks the highest valid gen among primary,
    /// mirror and backups, refuses newer versions without touching them, migrates older ones step by step, and
    /// replays log records of the same gen up to the first bad checksum.
    /// </summary>
    public sealed class SaveService
    {
        /// <summary>Migration from version v to v+1, keyed by v.</summary>
        public delegate JObject Migration(JObject envelope);

        private readonly string _dir;
        private readonly SaveRules _rules;
        private readonly Dictionary<int, Migration> _migrations = new Dictionary<int, Migration>();

        public SaveService(string directory, SaveRules rules)
        {
            _dir = directory;
            _rules = rules;
            Directory.CreateDirectory(_dir);
        }

        public SaveRules Rules => _rules;

        public void RegisterMigration(int fromVersion, Migration migration) => _migrations[fromVersion] = migration;

        public bool Exists(string slot) => Candidates(slot).Any(File.Exists);

        // ------------------------------------------------------------------ write

        public int Checkpoint(string slot, JToken payload, string contentHash, string snapshotHash)
        {
            var gen = HighestGen(slot) + 1;
            var envelope = new JObject
            {
                [SaveKeys.SaveVersion] = _rules.CurrentVersion,
                [SaveKeys.Gen] = gen,
                [SaveKeys.ContentHash] = contentHash,
                [SaveKeys.SnapshotHash] = snapshotHash,
                [SaveKeys.Payload] = payload?.DeepClone(),
            };
            envelope[SaveKeys.Hash] = HashOf(envelope);
            var text = envelope.ToString(Formatting.None);
            WriteAtomic(PathOf(SaveLayout.Temp, slot), PathOf(SaveLayout.Primary, slot), PathOf(SaveLayout.Backup, slot), text);
            WriteAtomic(PathOf(SaveLayout.MirrorTemp, slot), PathOf(SaveLayout.Mirror, slot), PathOf(SaveLayout.MirrorBackup, slot), text);
            using (File.Create(LogPath(slot, gen))) { }
            foreach (var old in Directory.GetFiles(_dir, Format(SaveLayout.LogSearch, slot)).Where(p => p != LogPath(slot, gen))) File.Delete(old);
            return gen;
        }

        public void Append(string slot, int gen, int seq, JObject command, string stateHashAfter)
        {
            var cmd = ContentSet.Canonical(command);
            var record = new JObject
            {
                [SaveKeys.Gen] = gen,
                [SaveKeys.Seq] = seq,
                [SaveKeys.Crc] = Crc32.Of(cmd),
                [SaveKeys.Cmd] = command.DeepClone(),
                [SaveKeys.StateHashAfter] = stateHashAfter,
            };
            using (var fs = new FileStream(LogPath(slot, gen), FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                var bytes = Encoding.UTF8.GetBytes(record.ToString(Formatting.None) + ContentLayout.Lf);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
        }

        public void Delete(string slot)
        {
            foreach (var p in Candidates(slot).Concat(Directory.GetFiles(_dir, Format(SaveLayout.LogSearch, slot)))) if (File.Exists(p)) File.Delete(p);
        }

        // ------------------------------------------------------------------ read

        public LoadResult Load(string slot)
        {
            var result = new LoadResult { Status = LoadStatus.Empty };
            var candidates = Candidates(slot).Where(File.Exists).ToList();
            if (candidates.Count == 0) return result;

            JObject best = null;
            string bestPath = null;
            foreach (var path in candidates)
            {
                var env = TryRead(path);
                if (env == null) continue;
                if (best == null || (int)env[SaveKeys.Gen] > (int)best[SaveKeys.Gen]) { best = env; bestPath = path; }
            }
            if (best == null)
            {
                result.Status = LoadStatus.Corrupt;
                result.Warnings.Add(Format(SaveMessages.NoValidCopy, slot));
                return result;
            }
            if (bestPath != PathOf(SaveLayout.Primary, slot)) result.Warnings.Add(Format(SaveMessages.FellBack, Path.GetFileName(bestPath), (int)best[SaveKeys.Gen]));

            var version = (int)best[SaveKeys.SaveVersion];
            result.Version = version;
            if (version > _rules.CurrentVersion)
            {
                result.Status = LoadStatus.RefusedNewer;
                result.Warnings.Add(Format(SaveMessages.Newer, version, _rules.CurrentVersion));
                return result;
            }
            var logReplayable = version == _rules.CurrentVersion;
            while (version < _rules.CurrentVersion)
            {
                if (!_migrations.TryGetValue(version, out var step))
                {
                    result.Status = LoadStatus.Corrupt;
                    result.Warnings.Add(Format(SaveMessages.NoValidCopy, slot));
                    return result;
                }
                best = step((JObject)best.DeepClone());
                version++;
                best[SaveKeys.SaveVersion] = version;
                result.Warnings.Add(Format(SaveMessages.Migrated, version - 1, version));
            }

            result.Status = LoadStatus.Loaded;
            result.Gen = (int)best[SaveKeys.Gen];
            result.ContentHash = (string)best[SaveKeys.ContentHash];
            result.SnapshotHash = (string)best[SaveKeys.SnapshotHash];
            result.Payload = best[SaveKeys.Payload];
            if (logReplayable) ReadLog(slot, result);
            return result;
        }

        private void ReadLog(string slot, LoadResult result)
        {
            var path = LogPath(slot, result.Gen);
            if (!File.Exists(path)) return;
            var lines = File.ReadAllText(path, Encoding.UTF8).Split(ContentLayout.Lf[0]);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                JObject record;
                try { record = (JObject)JsonContent.Parse(lines[i]); }
                catch (Exception) { result.Warnings.Add(Format(SaveMessages.TornLog, i)); return; }
                var cmd = record[SaveKeys.Cmd] as JObject;
                if (cmd == null || (int?)record[SaveKeys.Gen] != result.Gen || (uint?)record[SaveKeys.Crc] != Crc32.Of(ContentSet.Canonical(cmd)))
                {
                    result.Warnings.Add(Format(SaveMessages.TornLog, i));
                    return;
                }
                result.Commands.Add(record);
            }
        }

        private JObject TryRead(string path)
        {
            try
            {
                var env = (JObject)JsonContent.Parse(File.ReadAllText(path, Encoding.UTF8));
                var stored = (string)env[SaveKeys.Hash];
                return stored != null && stored == HashOf(env) ? env : null;
            }
            catch (Exception) { return null; }
        }

        private int HighestGen(string slot)
        {
            var gens = Candidates(slot).Where(File.Exists).Select(TryRead).Where(e => e != null).Select(e => (int)e[SaveKeys.Gen]).ToList();
            return gens.Count == 0 ? 0 : gens.Max();
        }

        // ------------------------------------------------------------------ helpers

        private void WriteAtomic(string tmp, string dst, string bak, string text)
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (TryRead(tmp) == null) throw new IOException(Format(SaveMessages.NoValidCopy, tmp));
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(dst)) File.Replace(tmp, dst, bak, true);
                    else File.Move(tmp, dst);
                    return;
                }
                catch (IOException) when (attempt < _rules.ReplaceRetries)
                {
                    Thread.Sleep(_rules.ReplaceRetryDelayMs * (attempt + 1));
                }
            }
        }

        private static string HashOf(JObject envelope)
        {
            var copy = (JObject)envelope.DeepClone();
            copy.Remove(SaveKeys.Hash);
            return new ContentSet(new Dictionary<string, JToken> { { string.Empty, copy } }).Hash;
        }

        private IEnumerable<string> Candidates(string slot) => new[]
        {
            PathOf(SaveLayout.Primary, slot), PathOf(SaveLayout.Mirror, slot), PathOf(SaveLayout.Backup, slot), PathOf(SaveLayout.MirrorBackup, slot),
        };

        private string PathOf(string pattern, string slot) => Path.Combine(_dir, Format(pattern, slot));
        private string LogPath(string slot, int gen) => Path.Combine(_dir, Format(SaveLayout.Log, slot, gen));
        private static string Format(string template, params object[] args) => string.Format(CultureInfo.InvariantCulture, template, args);
    }
}
