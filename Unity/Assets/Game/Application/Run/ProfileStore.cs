using System;
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

        private ProfileStore(SaveService saves, JObject doc, bool writable)
        {
            _saves = saves;
            Doc = doc;
            Writable = writable;
        }

        /// <summary>The profile document; the run loop rewrites it in place (found, seen, results, progress, unlocks).</summary>
        public JObject Doc { get; private set; }

        /// <summary>False when a stored profile could not be read: it is left for the recovery flow, never replaced.</summary>
        public bool Writable { get; }

        /// <summary>The player's stored settings (profile.settings), or an empty object.</summary>
        public JObject Settings => Doc.Obj(LK.Settings) ?? new JObject();

        public static ProfileStore Load(SaveService saves)
        {
            var slot = saves.Rules.ProfileSlot;
            if (!saves.Exists(slot)) return new ProfileStore(saves, new JObject(), true);
            LoadResult loaded;
            try { loaded = saves.Load(slot); }
            catch (Exception) { return new ProfileStore(saves, new JObject(), false); }
            if (loaded.Status != LoadStatus.Loaded || !(loaded.Payload is JObject doc)) return new ProfileStore(saves, new JObject(), loaded.Status == LoadStatus.Empty);
            return new ProfileStore(saves, AsDoubles(doc), true);
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
