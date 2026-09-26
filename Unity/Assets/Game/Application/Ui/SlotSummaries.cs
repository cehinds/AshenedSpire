using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.App.Saves;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    public enum SlotStatus
    {
        Empty,
        Ready,
        Newer,
        Unreadable,
    }

    /// <summary>What the title preview and a W-03 slot row show about one run slot (US-1.3, US-1.4).</summary>
    public sealed class SlotSummary
    {
        public int Index;
        public string SlotName;
        public SlotStatus Status;
        public string Name;
        public string ClassId;
        public string Portrait;
        public string Seed;
        public string SavedAt;
        public string Journey;
        public int Act;
        public int Floor;
        public int Hp;
        public int HpMax;
        public long PlaytimeSeconds;

        public bool IsReady => Status == SlotStatus.Ready;
    }

    /// <summary>
    /// Reads the run slots through the SaveService (PF-06). A slot is Ready only when a verified copy loads;
    /// a save from a newer build is kept and reported, never loaded; a slot with files but no valid copy is
    /// Unreadable. The summary comes from the save payload's 'summary' object.
    /// </summary>
    public static class SlotSummaries
    {
        public static List<SlotSummary> Read(SaveService saves)
        {
            var list = new List<SlotSummary>();
            for (var i = 1; i <= saves.Rules.RunSlots; i++) list.Add(ReadOne(saves, i));
            return list;
        }

        public static SlotSummary ReadOne(SaveService saves, int index)
        {
            var name = saves.Rules.RunSlotName(index);
            var summary = new SlotSummary { Index = index, SlotName = name, Status = SlotStatus.Empty };
            if (!saves.Exists(name)) return summary;
            LoadResult result;
            try { result = saves.Load(name); }
            catch (Exception) { summary.Status = SlotStatus.Unreadable; return summary; }
            switch (result.Status)
            {
                case LoadStatus.Empty: return summary;
                case LoadStatus.RefusedNewer: summary.Status = SlotStatus.Newer; return summary;
                case LoadStatus.Corrupt: summary.Status = SlotStatus.Unreadable; return summary;
            }
            summary.Status = SlotStatus.Ready;
            if (result.Payload?[SlotSummaryKeys.Summary] is JObject s)
            {
                summary.Name = (string)s[SlotSummaryKeys.Name];
                summary.ClassId = (string)s[SlotSummaryKeys.ClassId];
                summary.Portrait = (string)s[SlotSummaryKeys.Portrait];
                summary.Seed = (string)s[SlotSummaryKeys.Seed];
                summary.SavedAt = (string)s[SlotSummaryKeys.SavedAt];
                summary.Journey = (string)s[SlotSummaryKeys.Journey];
                summary.Act = (int?)s[SlotSummaryKeys.Act] ?? 0;
                summary.Floor = (int?)s[SlotSummaryKeys.Floor] ?? 0;
                summary.Hp = (int?)s[SlotSummaryKeys.Hp] ?? 0;
                summary.HpMax = (int?)s[SlotSummaryKeys.HpMax] ?? 0;
                summary.PlaytimeSeconds = (long?)s[SlotSummaryKeys.Playtime] ?? 0L;
            }
            return summary;
        }

        public static bool HasValidSlot(IReadOnlyList<SlotSummary> slots) => slots.Any(s => s.IsReady);

        /// <summary>The slot Continue resumes: the most recently saved Ready slot (ISO-8601 'savedAt', ordinal); ties go to the lower slot.</summary>
        public static SlotSummary ContinueTarget(IReadOnlyList<SlotSummary> slots) =>
            slots.Where(s => s.IsReady)
                 .OrderByDescending(s => s.SavedAt ?? string.Empty, StringComparer.Ordinal)
                 .ThenBy(s => s.Index)
                 .FirstOrDefault();
    }
}
