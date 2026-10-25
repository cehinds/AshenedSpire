using System;
using System.Collections.Generic;
using System.Linq;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>One change of the in-game Changelog (web src/content/changelog.js entries): its date, what changed, the detail behind it and the build it landed in.</summary>
    public sealed class ChangelogEntry
    {
        public string Id;
        public string Group;
        public string Summary;
        public string Detail;
        public string Build;
    }

    /// <summary>The changes of one day, newest first (web about.js changelogNodes: a heading per group, one fold per entry).</summary>
    public sealed class ChangelogGroup
    {
        public string Title;
        public readonly List<ChangelogEntry> Entries = new List<ChangelogEntry>();
    }

    /// <summary>ui/changelog.json: CHANGELOG.md read for the game (Tools/changelog.mjs). Engine-free.</summary>
    public sealed class ChangelogData
    {
        public readonly List<ChangelogGroup> Groups = new List<ChangelogGroup>();

        public static ChangelogData From(JObject json)
        {
            var data = new ChangelogData();
            foreach (var entry in ((JArray)json[ChangelogKeys.Entries]).OfType<JObject>())
            {
                var group = entry.Value<string>(ChangelogKeys.Group);
                if (data.Groups.Count == 0 || data.Groups[data.Groups.Count - 1].Title != group) data.Groups.Add(new ChangelogGroup { Title = group });
                data.Groups[data.Groups.Count - 1].Entries.Add(new ChangelogEntry
                {
                    Id = entry.Value<string>(CardLayoutKeys.Id), Group = group, Summary = entry.Value<string>(ChangelogKeys.Summary),
                    Detail = entry.Value<string>(ChangelogKeys.Detail), Build = entry.Value<string>(ChangelogKeys.Build),
                });
            }
            return data;
        }
    }
}
