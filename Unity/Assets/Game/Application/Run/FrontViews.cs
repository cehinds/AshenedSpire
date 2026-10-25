using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Newtonsoft.Json.Linq;
using K = Ashen.Generated.CombatKeys;
using LK = Ashen.Generated.LoopKeys;
using RK = Ashen.Generated.RunKeys;

namespace Ashen.App.Run
{
    /// <summary>One armament of the Compendium: held (its name, numbers and blurb) or unknown (a silhouette and what it is called until found).</summary>
    public sealed class CompendiumEntry
    {
        public string Id;
        public bool Found;
        public string Name;
        public string ArtKey;

        /// <summary>The registry id of the armament's icon (equipment.icon.*), or null.</summary>
        public string ArtId;

        /// <summary>"common · either hand".</summary>
        public string Meta;

        public string Rarity;
        public string Kind;

        /// <summary>What the inspector says: the blurb of a held armament, the "not yet found" line of an unknown one.</summary>
        public string Detail;

        public readonly List<KeyValuePair<string, string>> Stats = new List<KeyValuePair<string, string>>();
    }

    /// <summary>A rail item of the Compendium: a kind of armament and how many of it are held.</summary>
    public sealed class CompendiumTab
    {
        public string Id;
        public string Label;
        public int Found;
        public int Total;
        public string Count;
        public readonly List<CompendiumEntry> Entries = new List<CompendiumEntry>();
    }

    /// <summary>The Compendium (web src/ui/screens/compendium.js): the armaments of the game by kind, the ones the profile has found shown and the rest as unknown. Engine-free.</summary>
    public sealed class CompendiumViewState
    {
        public string Eyebrow;
        public string Title;
        public int Found;
        public int Total;
        public readonly List<CompendiumTab> Tabs = new List<CompendiumTab>();
    }

    public static class CompendiumView
    {
        public static CompendiumViewState Build(JObject profile, RunContent content, UiData ui)
        {
            var strings = ui.Strings;
            var found = new HashSet<string>(Js.Items((profile ?? new JObject())[RK.DiscoveredArmaments]).Select(Js.Str).Where(s => !string.IsNullOrEmpty(s)), StringComparer.Ordinal);
            var rows = Js.Items(content.Core2.Combat.Combat.Equipment[K.Armaments]).OfType<JObject>().ToList();
            var view = new CompendiumViewState { Title = strings.Get(StringKeys.CompendiumTitle), Total = rows.Count };
            var kinds = Js.Items(ui.Meta.Obj(SettingsPageKeys.Compendium)?[SettingsPageKeys.Kinds]).Select(Js.Str).Where(k => k != null).ToList();
            foreach (var kind in kinds)
            {
                var tab = new CompendiumTab { Id = kind, Label = strings.Get(Key(FrontFormats.CompendiumTab, kind)) };
                foreach (var row in rows.Where(r => r.Str(K.Kind) == kind))
                {
                    var id = row.Str(K.Id);
                    var held = found.Contains(id);
                    var rarity = row.Str(ArmouryKeys.Rarity);
                    var entry = new CompendiumEntry
                    {
                        Id = id,
                        Found = held,
                        Kind = kind,
                        Rarity = rarity,
                        ArtKey = row.Str(ArmouryKeys.ArtKey),
                        ArtId = RewardsView.ArtFromKey(ui.Components.RewardArmamentArt, row.Str(ArmouryKeys.ArtKey) ?? id, null),
                        Name = held ? Named(strings, id) : strings.Get(Key(FrontFormats.CompendiumUnknown, kind)),
                        Meta = strings.Format(StringKeys.CompendiumMeta, new StringArgs()
                            .Add(UiPlaceholders.Rarity, strings.Get(Key(FrontFormats.CompendiumRarity, rarity ?? string.Empty)))
                            .Add(UiPlaceholders.Hand, strings.Get(Key(FrontFormats.CompendiumHand, row.Str(K.Hand) ?? string.Empty)))),
                        Detail = held ? row.Str(ArmouryKeys.Blurb) ?? string.Empty : strings.Get(StringKeys.CompendiumUnknownBody),
                    };
                    if (held)
                        foreach (var stat in ArmouryKeys.StatList.Split(UiFormats.ListJoin[0]))
                            if (Js.IsNum(row[stat])) entry.Stats.Add(new KeyValuePair<string, string>(strings.Get(string.Format(CultureInfo.InvariantCulture, ArmouryKeys.StatStringKey, stat)), ((int)Math.Floor(row.Num(stat))).ToString(CultureInfo.InvariantCulture)));
                    tab.Entries.Add(entry);
                }
                tab.Found = tab.Entries.Count(e => e.Found);
                tab.Total = tab.Entries.Count;
                tab.Count = strings.Format(StringKeys.CompendiumCount, new StringArgs().Add(UiPlaceholders.Found, tab.Found).Add(UiPlaceholders.Total, tab.Total));
                view.Tabs.Add(tab);
            }
            view.Found = view.Tabs.Sum(t => t.Found);
            view.Eyebrow = strings.Format(StringKeys.CompendiumEyebrow, new StringArgs().Add(UiPlaceholders.Found, view.Found).Add(UiPlaceholders.Total, view.Total));
            return view;
        }

        private static string Key(string format, string id) => string.Format(CultureInfo.InvariantCulture, format, id);

        private static string Named(StringTable strings, string id)
        {
            var key = string.Format(CultureInfo.InvariantCulture, MetaFormats.ArmamentName, id);
            return strings.Has(key) ? strings.Get(key) : id;
        }
    }

    /// <summary>One save slot as the Profile screen's Saves pane lists it.</summary>
    public sealed class ProfileSlotRow
    {
        public int Index;
        public string Label;
        public string Facts;
        public string Portrait;
        public bool Empty;
    }

    /// <summary>The Profile screen (web profileArchive.js, as far as this build keeps data): whether the profile reads, what it holds, and the save slots.</summary>
    public sealed class ProfileViewState
    {
        public string Eyebrow;
        public string Title;
        public bool Readable;
        public string Status;
        public string Body;
        public int RunCount;
        public int SlotCount;
        public readonly List<string> Tally = new List<string>();
        public readonly List<ProfileSlotRow> Slots = new List<ProfileSlotRow>();
    }

    public static class ProfileView
    {
        public static ProfileViewState Build(ProfileStore profile, SaveService saves, RunContent content, UiData ui)
        {
            var strings = ui.Strings;
            var journal = JournalView.Build(profile.Doc, content, ui);
            var view = new ProfileViewState
            {
                Eyebrow = strings.Get(StringKeys.ProfileEyebrow),
                Title = strings.Get(StringKeys.ProfileTitle),
                Readable = profile.Writable,
                RunCount = journal.History.Count,
            };
            view.Status = strings.Get(view.Readable ? StringKeys.ProfileFine : StringKeys.ProfileUnreadable);
            view.Body = strings.Get(view.Readable ? StringKeys.ProfileFineBody : StringKeys.RecoveryBody);
            view.Tally.AddRange(journal.Profile);
            foreach (var slot in SlotSummaries.Read(saves))
            {
                var row = new ProfileSlotRow
                {
                    Index = slot.Index,
                    Label = strings.Format(StringKeys.SlotsLabel, new StringArgs().Add(UiPlaceholders.Slot, slot.Index)),
                    Empty = slot.Status == SlotStatus.Empty,
                    Portrait = slot.Portrait,
                };
                switch (slot.Status)
                {
                    case SlotStatus.Empty: row.Facts = strings.Get(StringKeys.SlotsEmpty); break;
                    case SlotStatus.Newer: row.Facts = strings.Get(StringKeys.SlotsNewerRow); break;
                    case SlotStatus.Unreadable: row.Facts = strings.Get(StringKeys.SlotsCorrupt); break;
                    default:
                        row.Facts = strings.Format(StringKeys.SlotsFacts, new StringArgs()
                            .Add(UiPlaceholders.Name, slot.Name).Add(UiPlaceholders.Class, RunHudView.ClassName(ui, slot.ClassId)).Add(UiPlaceholders.Act, slot.Act).Add(UiPlaceholders.Floor, slot.Floor)
                            .Add(UiPlaceholders.Hp, slot.Hp).Add(UiPlaceholders.HpMax, slot.HpMax));
                        break;
                }
                view.Slots.Add(row);
            }
            view.SlotCount = view.Slots.Count(s => !s.Empty);
            return view;
        }
    }

    /// <summary>One section of the About text (web about.js: an eyebrow, a heading and a paragraph).</summary>
    public sealed class AboutSectionView
    {
        public string Eyebrow;
        public string Heading;
        public string Body;
    }

    /// <summary>The About text (web settings -> Advanced -> About, src/ui/screens/about.js over src/content/aiDisclosure.js): the lead in paragraphs, the sections, the one action and the build lines.</summary>
    public sealed class AboutViewState
    {
        public readonly List<string> Lead = new List<string>();
        public readonly List<AboutSectionView> Sections = new List<AboutSectionView>();
        public string SaveLabel;

        /// <summary>"Ashen Spire 0.7.1" and, under it, "installed build · acknowledgement last updated 2026-09-26".</summary>
        public string Build;
        public string Provenance;

        /// <summary>The acknowledgement as plain text, what Save this to a file writes (aiDisclosure.js disclosureAsText).</summary>
        public string FileText;
        public string FileName;
    }

    public static class AboutView
    {
        public static AboutViewState Build(UiData ui, string version)
        {
            var strings = ui.Strings;
            var page = ui.About.Obj(AboutKeys.Page);
            var view = new AboutViewState
            {
                SaveLabel = strings.Get(StringKeys.AboutSave),
                Build = strings.Format(StringKeys.AboutBuild, new StringArgs().Add(UiPlaceholders.Version, version ?? string.Empty)),
                Provenance = strings.Format(StringKeys.AboutProvenance, new StringArgs().Add(UiPlaceholders.Path, strings.Get(StringKeys.AboutRunPath)).Add(UiPlaceholders.Date, strings.Get(page.Str(AboutKeys.Updated)))),
                FileName = strings.Get(StringKeys.AboutFileName),
            };
            foreach (var key in page.Arr(AboutKeys.Lead)) view.Lead.Add(strings.Get((string)key));
            foreach (var row in page.Arr(AboutKeys.Sections).OfType<JObject>())
                view.Sections.Add(new AboutSectionView { Eyebrow = strings.Get(StringKeys.AboutTitle), Heading = strings.Get(row.Str(AboutKeys.HeadingKey)), Body = strings.Get(row.Str(AboutKeys.BodyKey)) });
            // disclosureAsText: the title with the version, the lead, then each section as its heading over its paragraph.
            var text = new List<string>
            {
                strings.Format(StringKeys.AboutFileTitle, new StringArgs().Add(UiPlaceholders.Version, version ?? string.Empty)),
                string.Empty,
                string.Join(ArmouryKeys.Space, view.Lead),
                string.Empty,
            };
            foreach (var section in view.Sections) text.Add(section.Heading + UiFormats.Newline + section.Body + UiFormats.Newline);
            view.FileText = string.Join(UiFormats.Newline, text);
            return view;
        }
    }
}
