using System;
using System.Collections.Generic;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.Content;
using Ashen.Domain.Combat;
using Ashen.Domain.Run;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>
    /// The screen a profile that would not read lands on (web src/ui/screens/profileNotice.js mountProfileNotice, the words Sunna's): the state it renders (corrupt and older, whose bytes could not be read, or newer, whose bytes are fine and
    /// from the future), the heading and lead, the card that says what survived and where it is, and the ways on. Every fact comes from the profile's load state and the drawer; nothing is re-derived. Engine-free.
    /// </summary>
    public sealed class ProfileNoticeViewState
    {
        /// <summary>The bytes are fine, from a newer build: the first way on is to do nothing.</summary>
        public bool Newer;

        public string Eyebrow;
        public string DoorTitle;
        public string Heading;
        public readonly List<string> Lead = new List<string>();

        /// <summary>The last lead line is the web's bold one (Update Ashen Spire to open it again).</summary>
        public bool LeadEmphasis;

        public string CardEyebrow;
        public string CardName;
        public string CardLine;
        public string CardMeta;

        /// <summary>The card is muted: nothing about the profile could be read out of the bytes.</summary>
        public bool CardMuted;

        public string LeaveLabel;
        public string RestoreLabel;
        public string ExportLabel;
        public string FreshLabel;
        public string SupportLabel;

        /// <summary>The raw engine text behind Details for support; null when there is none.</summary>
        public string Reason;

        /// <summary>The drawer entry the profile was set aside as; null for a newer profile (nothing is archived: the bytes stay where they are).</summary>
        public string ArchiveId;

        /// <summary>The state name the export file wears when there is no entry (web: ashen-spire-profile-&lt;state&gt;.json).</summary>
        public string State;
    }

    public static class ProfileNoticeView
    {
        public static ProfileNoticeViewState Build(SaveService saves, ProfileStore profile, StringTable strings, TimeZoneInfo zone = null)
        {
            var newer = profile.State == ArchiveKeys.StateNewer;
            var view = new ProfileNoticeViewState
            {
                Newer = newer,
                State = profile.State,
                ArchiveId = newer ? null : profile.ArchiveId,
                Reason = profile.Reason,
                Eyebrow = strings.Get(StringKeys.RecoveryEyebrow),
                DoorTitle = strings.Get(newer ? StringKeys.RecoveryTitleNewer : StringKeys.RecoveryTitleUnreadable),
                Heading = strings.Get(newer ? StringKeys.RecoveryHeadingNewer : StringKeys.RecoveryHeadingUnreadable),
                LeaveLabel = strings.Get(newer ? StringKeys.RecoveryKeep : StringKeys.RecoveryNotNow),
                RestoreLabel = newer ? null : strings.Get(StringKeys.RecoveryRestore),
                ExportLabel = strings.Get(StringKeys.ProfileArchExport),
                FreshLabel = strings.Get(newer ? StringKeys.RecoveryFreshAnyway : StringKeys.RecoveryFresh),
                SupportLabel = strings.Get(StringKeys.ProfileArchSupport),
                CardEyebrow = strings.Get(StringKeys.RecoveryCardEyebrow),
            };
            if (newer)
            {
                view.Lead.Add(strings.Get(StringKeys.RecoveryLeadNewer));
                view.Lead.Add(strings.Get(StringKeys.RecoveryLeadNewerUpdate));
                view.LeadEmphasis = true;
            }
            else view.Lead.Add(strings.Get(StringKeys.RecoveryLeadUnreadable));

            // What survived, in the player's terms: read from the archived bytes when they read at all. A partial read is still worth showing (Saga P4b).
            var entry = view.ArchiveId == null ? null : saves.Drawer.Get(view.ArchiveId);
            var kept = entry == null ? null : Kept(entry.Save);
            view.CardMuted = kept == null;
            view.CardName = kept != null
                ? strings.Format(StringKeys.RecoveryCardKept, new StringArgs().Add(ArchivePlaceholders.Runs, kept.Item1).Add(ArchivePlaceholders.Wins, kept.Item2).Add(ArchivePlaceholders.Unlocks, kept.Item3))
                : strings.Get(newer ? StringKeys.RecoveryCardUntouched : StringKeys.RecoveryCardSetAside);
            view.CardLine = kept != null ? strings.Get(StringKeys.RecoveryCardSaved) : string.Empty;

            // The screen must say WHERE (Sunna D6): the fact of preservation is the comfort, and it is thin if we cannot point at anything.
            if (newer) view.CardMeta = strings.Get(StringKeys.RecoveryWhereNewer);
            else
            {
                var parts = new List<string>();
                var when = entry == null ? null : ProfileArchiveView.HumanTime(entry.At, strings, zone);
                if (when != null) parts.Add(strings.Format(StringKeys.ProfileArchWhen, new StringArgs().Add(UiPlaceholders.When, when)));
                var size = entry == null ? null : ProfileArchiveView.HumanSize(entry.Bytes, strings);
                if (size != null) parts.Add(size);
                parts.Add(strings.Get(StringKeys.RecoveryWhereTail));
                view.CardMeta = string.Join(strings.Get(StringKeys.ProfileArchJoin), parts);
            }
            return view;
        }

        /// <summary>The runs, wins and unlocks the archived bytes still say (the web's progress and unlocked), or null when they do not read as a profile. The stored save is an envelope here: its payload is the profile.</summary>
        private static Tuple<string, string, int> Kept(string save)
        {
            try
            {
                var root = (JObject)JsonContent.Parse(save);
                var doc = root[ArchiveKeys.Payload] as JObject ?? root;
                var progress = doc[ArchiveKeys.Progress] as JObject;
                if (progress == null) return null;
                var unlocked = doc[ArchiveKeys.Unlocked] as JArray;
                return Tuple.Create(Count(progress[ArchiveKeys.Runs]), Count(progress[ArchiveKeys.Wins]), unlocked?.Count ?? 0);
            }
            catch (Exception e) when (e is Newtonsoft.Json.JsonException || e is InvalidCastException)
            {
                return null; // unreadable is the NORMAL case here; say nothing rather than guess
            }
        }

        private static string Count(JToken token) => token == null || token.Type == JTokenType.Null ? ArchiveKeys.NoCount : RunJs.NumStr(Js.D(token));
    }
}
