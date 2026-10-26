using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Ashen.Content;
using Ashen.Generated;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Saves
{
    /// <summary>One thing set aside in the drawer: a profile (kind meta) or a run (kind run), the bytes as they were stored, and why and when (web engine/save.js listArchives, getArchive).</summary>
    public sealed class ArchiveEntry
    {
        public string Id;
        public string Kind;
        public int? Slot;
        public string Reason;
        public string At;
        public int Count;
        public string LastSeenAt;
        public string Save;

        /// <summary>The size shown on the entry: the length of the stored text, as the web counts it (bytes: save.length).</summary>
        public int Bytes => Save?.Length ?? 0;

        public bool IsProfile => Kind == ArchiveKeys.KindMeta;

        internal JObject ToJson()
        {
            var json = new JObject
            {
                [ArchiveKeys.Id] = Id,
                [ArchiveKeys.Kind] = Kind,
                [ArchiveKeys.Slot] = Slot.HasValue ? (JToken)Slot.Value : JValue.CreateNull(),
                [ArchiveKeys.Reason] = Reason,
                [ArchiveKeys.At] = At,
            };
            if (Count > 0) json[ArchiveKeys.Count] = Count;
            json[ArchiveKeys.Save] = Save;
            if (LastSeenAt != null) json[ArchiveKeys.LastSeenAt] = LastSeenAt;
            return json;
        }

        internal static ArchiveEntry From(JObject json) => new ArchiveEntry
        {
            Id = (string)json[ArchiveKeys.Id],
            Kind = (string)json[ArchiveKeys.Kind],
            Slot = json[ArchiveKeys.Slot] is JValue v && v.Type == JTokenType.Integer ? (int?)(int)v : null,
            Reason = (string)json[ArchiveKeys.Reason],
            At = (string)json[ArchiveKeys.At],
            Count = (int?)json[ArchiveKeys.Count] ?? 0,
            LastSeenAt = (string)json[ArchiveKeys.LastSeenAt],
            Save = (string)json[ArchiveKeys.Save],
        };
    }

    /// <summary>Something the drawer had to do to itself, said out loud on the Profile screen: the oldest profile was moved out to its own file (web drawerNotices, kind profile-salvaged).</summary>
    public sealed class ArchiveNotice
    {
        public string At;
        public string Kind;
        public string Id;
        public string Key;
        public string Was;
    }

    /// <summary>
    /// The archive drawer (web engine/save.js readArchiveIndex, writeArchiveEntry, archiveMeta, archive, listArchives, getArchive, exportArchive, drawerNotices): the copies of what could not be read or was replaced, KEYED and APPENDED, never
    /// overwritten. Runs age out after about six months and only the newest 12 are kept; a profile is never aged out and never evicted by a run. If profiles alone fill the drawer (24) the oldest is MOVED to its own file and a notice is
    /// recorded, never deleted. An unreadable index is set aside under its own name before a fresh one is started. One file (archive.json) beside the saves, in the shape the web keeps in storage. Engine-free.
    /// </summary>
    public sealed class ArchiveDrawer
    {
        private readonly string _dir;
        private readonly Func<DateTime> _clock;
        private int _seq;

        public ArchiveDrawer(string directory, Func<DateTime> clock = null)
        {
            _dir = directory;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        private string IndexPath => Path.Combine(_dir, ArchiveKeys.IndexFile);

        private DateTime Now => _clock().ToUniversalTime();

        private static string Iso(DateTime utc) => utc.ToString(ArchiveKeys.Iso, CultureInfo.InvariantCulture);

        /// <summary>Date.parse of a stored time; a missing one is the oldest there is (only an entry from before times were kept lacks one).</summary>
        internal static DateTime Parse(string iso) =>
            iso != null && DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : DateTime.MinValue;

        // ------------------------------------------------------------------ the index

        private sealed class Index
        {
            public List<ArchiveEntry> Entries = new List<ArchiveEntry>();
            public List<ArchiveNotice> Notices = new List<ArchiveNotice>();
        }

        private Index Read()
        {
            var index = new Index();
            if (!File.Exists(IndexPath)) return index;
            string raw;
            try { raw = File.ReadAllText(IndexPath, Encoding.UTF8); }
            catch (IOException) { return index; }
            if (raw.Length == 0) return index;
            JObject parsed;
            try { parsed = (JObject)JsonContent.Parse(raw); }
            catch (Exception e) when (e is JsonException || e is InvalidCastException)
            {
                // The drawer's own catalogue must not be the thing that loses the drawer: the raw bytes go aside under their own name first.
                try { File.WriteAllText(Path.Combine(_dir, string.Format(CultureInfo.InvariantCulture, ArchiveKeys.SalvageFile, EpochMs(Now))), raw, Encoding.UTF8); }
                catch (IOException) { /* the fresh index below still lets the game run */ }
                return index;
            }
            if (parsed[ArchiveKeys.Entries] is JArray entries)
                foreach (var e in entries.OfType<JObject>()) index.Entries.Add(ArchiveEntry.From(e));
            if (parsed[ArchiveKeys.Notices] is JArray notices)
                foreach (var n in notices.OfType<JObject>())
                    index.Notices.Add(new ArchiveNotice
                    {
                        At = (string)n[ArchiveKeys.At], Kind = (string)n[ArchiveKeys.Kind], Id = (string)n[ArchiveKeys.Id], Key = (string)n[ArchiveKeys.Key], Was = (string)n[ArchiveKeys.Was],
                    });
            return index;
        }

        private void Write(Index index)
        {
            var json = new JObject
            {
                [ArchiveKeys.Version] = 1,
                [ArchiveKeys.Entries] = new JArray(index.Entries.Select(e => e.ToJson())),
            };
            if (index.Notices.Count > 0)
                json[ArchiveKeys.Notices] = new JArray(index.Notices.Select(n => new JObject
                {
                    [ArchiveKeys.At] = n.At, [ArchiveKeys.Kind] = n.Kind, [ArchiveKeys.Id] = n.Id, [ArchiveKeys.Key] = n.Key, [ArchiveKeys.Was] = n.Was,
                }));
            Directory.CreateDirectory(_dir);
            var tmp = IndexPath + ArchiveKeys.TempSuffix;
            File.WriteAllText(tmp, json.ToString(Formatting.None), Encoding.UTF8);
            File.Copy(tmp, IndexPath, true);
            File.Delete(tmp);
        }

        private static long EpochMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

        private string MakeId(string kind, int? slot)
        {
            _seq++;
            var ms = EpochMs(Now);
            return kind == ArchiveKeys.KindRun
                ? string.Format(CultureInfo.InvariantCulture, ArchiveKeys.IdRun, slot, ms, _seq)
                : string.Format(CultureInfo.InvariantCulture, ArchiveKeys.IdMeta, ms, _seq);
        }

        // ------------------------------------------------------------------ writing

        /// <summary>writeArchiveEntry: append, then prune runs only (aged out, newest 12) and move the oldest profiles out when profiles alone pass 24.</summary>
        private string WriteEntry(ArchiveEntry entry)
        {
            var index = Read();
            index.Entries.Add(entry);
            var cutoff = Now.AddDays(-ArchiveNumbers.MaxAgeDays);
            var runs = index.Entries.Where(e => !e.IsProfile).ToList();
            var profiles = index.Entries.Where(e => e.IsProfile).ToList();

            var keptRuns = runs.Where(e => string.IsNullOrEmpty(e.At) || Parse(e.At) >= cutoff).ToList();
            if (keptRuns.Count > ArchiveNumbers.RunLimit) keptRuns.RemoveRange(0, keptRuns.Count - ArchiveNumbers.RunLimit);

            var keptProfiles = profiles;
            if (profiles.Count > ArchiveNumbers.ProfileLimit)
            {
                var evicted = profiles.Take(profiles.Count - ArchiveNumbers.ProfileLimit).ToList();
                keptProfiles = profiles.Skip(evicted.Count).ToList();
                foreach (var e in evicted)
                {
                    var key = string.Format(CultureInfo.InvariantCulture, ArchiveKeys.ProfileSalvageFile, e.Id);
                    try
                    {
                        File.WriteAllText(Path.Combine(_dir, key), e.Save ?? string.Empty, Encoding.UTF8);
                        index.Notices.Add(new ArchiveNotice { At = Iso(Now), Kind = ArchiveKeys.NoticeSalvaged, Id = e.Id, Key = key, Was = e.At });
                    }
                    catch (IOException)
                    {
                        // Refused the salvage write: keep the profile in the drawer rather than dropping it.
                        keptProfiles.Insert(0, e);
                    }
                }
            }

            index.Entries = keptRuns.Concat(keptProfiles).OrderBy(e => Parse(e.At)).ToList();
            Write(index);
            return entry.Id;
        }

        /// <summary>archiveMeta(json, reason): set a profile's bytes aside (the primary is never removed). The same bytes seen again keep ONE entry, count it and note when it was last seen.</summary>
        public string ArchiveProfile(string json, string reason)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var index = Read();
            var existing = index.Entries.FirstOrDefault(e => e.IsProfile && e.Save == json);
            if (existing != null)
            {
                existing.Count = (existing.Count > 0 ? existing.Count : 1) + 1;
                existing.LastSeenAt = Iso(Now);
                Write(index);
                return existing.Id;
            }
            return WriteEntry(new ArchiveEntry { Id = MakeId(ArchiveKeys.KindMeta, null), Kind = ArchiveKeys.KindMeta, Slot = null, Reason = reason, At = Iso(Now), Count = 1, Save = json });
        }

        /// <summary>The id of the profile entry that already holds exactly these bytes, without counting another sighting (a screen that only looks); null when there is none.</summary>
        public string FindProfile(string json) => string.IsNullOrEmpty(json) ? null : Read().Entries.FirstOrDefault(e => e.IsProfile && e.Save == json)?.Id;

        /// <summary>archive(json, reason, slot): set a run's bytes aside.</summary>
        public string ArchiveRun(string json, string reason, int slot) =>
            WriteEntry(new ArchiveEntry { Id = MakeId(ArchiveKeys.KindRun, slot), Kind = ArchiveKeys.KindRun, Slot = slot, Reason = reason, At = Iso(Now), Save = json });

        // ------------------------------------------------------------------ the handle on the drawer

        /// <summary>listArchives(): the entries, oldest first (the web's "newest last"; the screen reverses it).</summary>
        public IReadOnlyList<ArchiveEntry> List() => Read().Entries;

        /// <summary>drawerNotices(): what the drawer had to do to itself; empty is the normal state.</summary>
        public IReadOnlyList<ArchiveNotice> Notices() => Read().Notices;

        /// <summary>getArchive(id): the entry with its bytes, or null.</summary>
        public ArchiveEntry Get(string id) => Read().Entries.FirstOrDefault(e => e.Id == id);

        /// <summary>Now as the drawer writes a time (the export's exportedAt).</summary>
        public string Stamp() => Iso(Now);

        /// <summary>exportArchive(id): the text a player saves to a file (generated from the archive, never kept separately), or null.</summary>
        public string Export(string id)
        {
            var entry = Get(id);
            if (entry == null) return null;
            return new JObject
            {
                [ArchiveKeys.ExportedAt] = Iso(Now),
                [ArchiveKeys.Game] = ArchiveKeys.GameName,
                [ArchiveKeys.Archive] = entry.ToJson(),
            }.ToString(Formatting.Indented);
        }
    }
}
