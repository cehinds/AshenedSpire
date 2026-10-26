using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>One entry card of the Profile screen (web profileArchive.js entryCard): when it was set aside and its size, what it is, why, and the raw reason behind Details for support.</summary>
    public sealed class ArchiveEntryView
    {
        public string Id;
        public string Eyebrow;
        public string Name;
        public string Line;
        public string Reason;
        public bool CanRestore;

        /// <summary>The copy was set aside because it could not be read, so the restore question says restoring it may not work.</summary>
        public bool Unreadable;
    }

    /// <summary>One rail category of the Profile screen: Profiles or Runs, with how many are set aside.</summary>
    public sealed class ArchiveCategoryView
    {
        public string Id;
        public string LabelKey;
        public string Label;
        public int Count;
        public bool Selected;
    }

    /// <summary>The drawer's notice (web profile-salvaged): the oldest profiles were moved out to their own files, with the dates.</summary>
    public sealed class ArchiveNoticeView
    {
        public string Title;
        public string Body;
        public readonly List<string> Moves = new List<string>();
        public string Tail;
    }

    /// <summary>
    /// The Profile screen's content (web src/ui/screens/profileArchive.js renderProfileSection over models/ProfileWorkspaceModel.js): the state line for the profile in use, the drawer policy, the drawer's notice, the rail of
    /// Profiles and Runs, and a card per set-aside entry, newest first. Every fact comes from the drawer and the profile's load state; nothing is re-derived. Engine-free.
    /// </summary>
    public sealed class ProfileArchiveViewState
    {
        public string StateLine;
        public string Policy;
        public ArchiveNoticeView Notice;
        public readonly List<ArchiveCategoryView> Categories = new List<ArchiveCategoryView>();
        public string Current;

        /// <summary>The selected category's entries, newest first.</summary>
        public readonly List<ArchiveEntryView> Entries = new List<ArchiveEntryView>();

        /// <summary>"drawer" when nothing at all is set aside, "category" when only the selected category is empty, else null.</summary>
        public string Empty;

        public string EmptyText;
    }

    public static class ProfileArchiveView
    {
        public const string EmptyDrawer = ArchiveKeys.EmptyDrawer;
        public const string EmptyCategory = ArchiveKeys.EmptyCategory;

        /// <summary>The rail's categories, in order (PROFILE_CATEGORIES).</summary>
        public static readonly string[] Categories = { ArchiveKeys.KindMeta, ArchiveKeys.KindRun };

        public static ProfileArchiveViewState Build(SaveService saves, ProfileStore profile, StringTable strings, string current = null, TimeZoneInfo zone = null)
        {
            var archives = saves.Drawer.List().Reverse().ToList();
            var view = new ProfileArchiveViewState
            {
                StateLine = strings.Get(StateKey(profile.State)),
                Policy = strings.Get(StringKeys.ProfileArchPolicy),
            };

            var counts = Categories.ToDictionary(c => c, c => archives.Count(a => CategoryOf(a) == c));
            var active = Array.IndexOf(Categories, current) >= 0 ? current : Categories.FirstOrDefault(c => counts[c] > 0) ?? Categories[0];
            view.Current = active;
            foreach (var id in Categories)
                {
                var key = id == ArchiveKeys.KindMeta ? StringKeys.ProfileCategoryMeta : StringKeys.ProfileCategoryRun;
                view.Categories.Add(new ArchiveCategoryView { Id = id, LabelKey = key, Label = strings.Get(key), Count = counts[id], Selected = id == active });
            }
            foreach (var entry in archives.Where(a => CategoryOf(a) == active)) view.Entries.Add(Entry(entry, strings, zone));
            view.Empty = archives.Count == 0 ? EmptyDrawer : view.Entries.Count == 0 ? EmptyCategory : null;
            if (view.Empty == EmptyDrawer) view.EmptyText = strings.Get(StringKeys.ProfileArchEmptyDrawer);
            else if (view.Empty == EmptyCategory) view.EmptyText = strings.Get(active == ArchiveKeys.KindMeta ? StringKeys.ProfileEmptyMeta : StringKeys.ProfileEmptyRun);
            view.Notice = Notice(saves.Drawer.Notices().Where(n => n.Kind == ArchiveKeys.NoticeSalvaged).ToList(), strings, zone);
            return view;
        }

        /// <summary>profileCategoryOf: anything that is not a profile is a run.</summary>
        public static string CategoryOf(ArchiveEntry entry) => entry != null && entry.IsProfile ? ArchiveKeys.KindMeta : ArchiveKeys.KindRun;

        private static string StateKey(string state)
        {
            switch (state)
            {
                case ArchiveKeys.StateEmpty: return StringKeys.ProfileArchStateEmpty;
                case ArchiveKeys.StateMigrated: return StringKeys.ProfileArchStateMigrated;
                case ArchiveKeys.StateRecovered: return StringKeys.ProfileArchStateRecovered;
                case ArchiveKeys.StateCorrupt: return StringKeys.ProfileArchStateCorrupt;
                case ArchiveKeys.StateOlder: return StringKeys.ProfileArchStateOlder;
                case ArchiveKeys.StateNewer: return StringKeys.ProfileArchStateNewer;
                default: return StringKeys.ProfileArchStateOk;
            }
        }

        private static ArchiveEntryView Entry(ArchiveEntry a, StringTable strings, TimeZoneInfo zone)
        {
            var when = HumanTime(a.At, strings, zone);
            var size = HumanSize(a.Bytes, strings);
            var parts = new List<string>();
            if (when != null) parts.Add(strings.Format(StringKeys.ProfileArchWhen, new StringArgs().Add(UiPlaceholders.When, when)));
            if (size != null) parts.Add(size);
            var eyebrow = string.Join(strings.Get(StringKeys.ProfileArchJoin), parts);
            if (a.Count > 1 && a.LastSeenAt != null)
                eyebrow += strings.Format(StringKeys.ProfileArchAgain, new StringArgs().Add(UiPlaceholders.Count, a.Count).Add(UiPlaceholders.When, HumanTime(a.LastSeenAt, strings, zone) ?? string.Empty));
            var reason = HumanReason(a.Reason);
            return new ArchiveEntryView
            {
                Id = a.Id,
                Eyebrow = eyebrow,
                Name = Describe(a, strings),
                Line = reason == null ? string.Empty : strings.Get(reason),
                Reason = a.Reason,
                CanRestore = a.IsProfile,
                Unreadable = reason == StringKeys.ProfileArchReasonUnreadable,
            };
        }

        /// <summary>humanTime(iso): "October 24, 4:38 PM" in the player's own time, or null when there is no time or it does not read.</summary>
        public static string HumanTime(string iso, StringTable strings, TimeZoneInfo zone = null)
        {
            if (string.IsNullOrEmpty(iso)) return null;
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)) return null;
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone ?? TimeZoneInfo.Local);
            return strings.Format(StringKeys.ProfileArchDate, new StringArgs()
                .Add(UiPlaceholders.Date, local.ToString(ArchiveKeys.DateFormat, CultureInfo.InvariantCulture))
                .Add(UiPlaceholders.Time, local.ToString(ArchiveKeys.TimeFormat, CultureInfo.InvariantCulture)));
        }

        /// <summary>humanSize(bytes): "812 bytes" or "4 KB", or null for nothing.</summary>
        public static string HumanSize(int bytes, StringTable strings)
        {
            if (bytes == 0) return null;
            return bytes < ArchiveNumbers.BytesPerKb
                ? strings.Format(StringKeys.ProfileArchBytes, new StringArgs().Add(UiPlaceholders.Count, bytes))
                : strings.Format(StringKeys.ProfileArchKb, new StringArgs().Add(UiPlaceholders.Count, (int)RunJs.Round(bytes / (double)ArchiveNumbers.BytesPerKb)));
        }

        /// <summary>describe(entry): what an entry IS in the player's words: a run (and its slot), or a profile (and, when it reads, its runs and wins).</summary>
        public static string Describe(ArchiveEntry entry, StringTable strings)
        {
            if (!entry.IsProfile)
                return entry.Slot.HasValue && entry.Slot.Value != 0
                    ? strings.Format(StringKeys.ProfileArchDescribeRunSlot, new StringArgs().Add(UiPlaceholders.Slot, entry.Slot.Value))
                    : strings.Get(StringKeys.ProfileArchDescribeRun);
            try
            {
                var root = (JObject)JsonContent.Parse(entry.Save);
                var progress = (root[ArchiveKeys.Progress] ?? (root[ArchiveKeys.Payload] as JObject)?[ArchiveKeys.Progress]) as JObject;
                if (progress != null && (IsSet(progress[ArchiveKeys.Runs]) || IsSet(progress[ArchiveKeys.Wins])))
                {
                    var runs = IsSet(progress[ArchiveKeys.Runs]) ? RunJs.NumStr(Js.D(progress[ArchiveKeys.Runs])) : RunJs.NumStr(0);
                    return IsSet(progress[ArchiveKeys.Wins])
                        ? strings.Format(StringKeys.ProfileArchDescribeRunsWins, new StringArgs().Add(ArchivePlaceholders.Runs, runs).Add(ArchivePlaceholders.Wins, RunJs.NumStr(Js.D(progress[ArchiveKeys.Wins]))))
                        : strings.Format(StringKeys.ProfileArchDescribeRuns, new StringArgs().Add(ArchivePlaceholders.Runs, runs));
                }
            }
            catch (Exception e) when (e is Newtonsoft.Json.JsonException || e is InvalidCastException)
            {
                // Unreadable is the normal case; fall through to the plain noun.
            }
            return strings.Get(StringKeys.ProfileArchDescribeProfile);
        }

        private static bool IsSet(JToken token) => token != null && token.Type != JTokenType.Null;

        /// <summary>humanReason(raw): the string key that says in words what happened, or null for no reason. The raw engine text lives behind Details for support.</summary>
        public static string HumanReason(string raw)
        {
            var r = raw ?? string.Empty;
            if (Regex.IsMatch(r, ArchiveKeys.PatternUnreadable, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return StringKeys.ProfileArchReasonUnreadable;
            if (Regex.IsMatch(r, ArchiveKeys.PatternOlder, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return StringKeys.ProfileArchReasonOlder;
            if (Regex.IsMatch(r, ArchiveKeys.PatternNewProfile, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return StringKeys.ProfileArchReasonNewProfile;
            if (Regex.IsMatch(r, ArchiveKeys.PatternDangling, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return StringKeys.ProfileArchReasonDangling;
            if (Regex.IsMatch(r, ArchiveKeys.PatternRestored, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return StringKeys.ProfileArchReasonRestored;
            return r.Length > 0 ? StringKeys.ProfileArchReasonOther : null;
        }

        private static ArchiveNoticeView Notice(IReadOnlyList<ArchiveNotice> salvaged, StringTable strings, TimeZoneInfo zone)
        {
            if (salvaged.Count == 0) return null;
            var many = salvaged.Count > 1;
            var view = new ArchiveNoticeView
            {
                Title = strings.Get(StringKeys.ProfileArchNoticeTitle),
                Body = strings.Get(many ? StringKeys.ProfileArchNoticeMany : StringKeys.ProfileArchNoticeOne),
                Tail = strings.Get(many ? StringKeys.ProfileArchNoticeTailMany : StringKeys.ProfileArchNoticeTailOne),
            };
            foreach (var n in salvaged)
            {
                var was = HumanTime(n.Was, strings, zone);
                var at = HumanTime(n.At, strings, zone);
                var args = new StringArgs().Add(ArchivePlaceholders.Was, was ?? string.Empty).Add(ArchivePlaceholders.At, at ?? string.Empty);
                view.Moves.Add(strings.Format(was != null ? (at != null ? StringKeys.ProfileArchNoticeMovedBoth : StringKeys.ProfileArchNoticeMovedWas) : (at != null ? StringKeys.ProfileArchNoticeMovedAt : StringKeys.ProfileArchNoticeMoved), args));
            }
            return view;
        }
    }
}
