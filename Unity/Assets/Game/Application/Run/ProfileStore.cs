using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Ashen.App.Saves;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using LK = Ashen.Generated.LoopKeys;

namespace Ashen.App.Run
{
    /// <summary>
    /// The durable profile document (shipped saves.loadMeta/saveMeta; rules/saves.json profileSlot): the found armaments,
    /// the seen records, the run history, progress and unlocks the run loop writes, and the player's stored settings. It is
    /// read once per run session and written through the SaveService like a run slot. A profile that exists but cannot be
    /// read (the W-01 recovery case) is used empty and never overwritten here. Engine-free.
    /// </summary>
    public sealed class ProfileStore
    {
        private readonly SaveService _saves;

        private ProfileStore(SaveService saves, JObject doc, bool writable, string state = null, string archiveId = null, string reason = null)
        {
            _saves = saves;
            Doc = doc;
            Writable = writable;
            State = state ?? (writable ? ArchiveKeys.StateOk : ArchiveKeys.StateCorrupt);
            ArchiveId = archiveId;
            Reason = reason;
        }

        /// <summary>The drawer entry the unreadable profile was set aside as (web profileStatus().archiveId); null when nothing was set aside (a newer profile is left where it is).</summary>
        public string ArchiveId { get; }

        /// <summary>The engine's own words for why the profile did not read (web profileStatus().reason), for Details for support; null when it read.</summary>
        public string Reason { get; }

        /// <summary>What the load left (web profileStatus().state): ok, empty, migrated, recovered, corrupt or newer.</summary>
        public string State { get; }

        /// <summary>The profile document; the run loop rewrites it in place (found, seen, results, progress, unlocks).</summary>
        public JObject Doc { get; private set; }

        /// <summary>False when a stored profile could not be read: it is left for the recovery flow, never replaced.</summary>
        public bool Writable { get; }

        /// <summary>The player's stored settings (profile.settings), or an empty object.</summary>
        public JObject Settings => Doc.Obj(LK.Settings) ?? new JObject();

        public static ProfileStore Load(SaveService saves, bool setAside = true)
        {
            var slot = saves.Rules.ProfileSlot;
            if (!saves.Exists(slot)) return new ProfileStore(saves, new JObject(), true, ArchiveKeys.StateEmpty);
            LoadResult loaded;
            try { loaded = saves.Load(slot); }
            catch (Exception e) { return Unreadable(saves, e.Message, setAside); }
            if (loaded.Status == LoadStatus.RefusedNewer) return new ProfileStore(saves, new JObject(), false, ArchiveKeys.StateNewer, null, loaded.Warnings.LastOrDefault());
            if (loaded.Status == LoadStatus.Corrupt && loaded.Older) return Older(saves, loaded.Warnings.FirstOrDefault(), setAside);
            if (loaded.Status == LoadStatus.Corrupt) return Unreadable(saves, loaded.Warnings.FirstOrDefault(), setAside);
            if (loaded.Status != LoadStatus.Loaded || !(loaded.Payload is JObject doc)) return new ProfileStore(saves, new JObject(), loaded.Status == LoadStatus.Empty, loaded.Status == LoadStatus.Empty ? ArchiveKeys.StateEmpty : ArchiveKeys.StateCorrupt);
            return new ProfileStore(saves, AsDoubles(doc), true, loaded.Recovered ? ArchiveKeys.StateRecovered : loaded.Migrated ? ArchiveKeys.StateMigrated : ArchiveKeys.StateOk);
        }

        /// <summary>A profile that cannot be read is set aside in the drawer (the bytes stay where they are: they are the evidence) and used empty. The Profile screen only looks, so it does not count another sighting.</summary>
        private static ProfileStore Unreadable(SaveService saves, string why, bool setAside)
        {
            var reason = string.Format(CultureInfo.InvariantCulture, ArchiveKeys.ReasonUnreadable, why ?? ArchiveKeys.Corrupt);
            var id = setAside ? saves.ArchiveProfile(reason) : saves.Drawer.FindProfile(saves.ReadRaw(saves.Rules.ProfileSlot));
            return new ProfileStore(saves, new JObject(), false, ArchiveKeys.StateCorrupt, id, reason);
        }

        /// <summary>A profile from an older version no migration reads: set aside with the engine's own reason (archiveMeta(primary.json, primary.error), no wrapper), used empty, and named older.</summary>
        private static ProfileStore Older(SaveService saves, string why, bool setAside)
        {
            var reason = why ?? ArchiveKeys.Corrupt;
            var id = setAside ? saves.ArchiveProfile(reason) : saves.Drawer.FindProfile(saves.ReadRaw(saves.Rules.ProfileSlot));
            return new ProfileStore(saves, new JObject(), false, ArchiveKeys.StateOlder, id, reason);
        }

        /// <summary>Writes the profile as a new save generation (nothing when it is read-only).</summary>
        public void Save(string contentHash)
        {
            if (!Writable) return;
            _saves.Checkpoint(_saves.Rules.ProfileSlot, Doc.DeepClone(), contentHash, Hash(Doc));
        }

        /// <summary>A copy to restore when a save fails after the profile was written in memory.</summary>
        public JObject Snapshot() => (JObject)Doc.DeepClone();

        public void Restore(JObject snapshot) => Doc = snapshot ?? new JObject();

        private static string Hash(JObject doc)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(ContentSet.Canonical(doc))).Select(b => b.ToString(ContentLayout.HexByteFormat)));
        }

        private static JObject AsDoubles(JObject doc)
        {
            var copy = (JObject)doc.DeepClone();
            foreach (var v in copy.Descendants().OfType<JValue>().ToList())
                if (v.Value is decimal) v.Value = Js.D(v);
            return copy;
        }
    }
}
