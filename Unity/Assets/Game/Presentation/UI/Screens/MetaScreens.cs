using System.Collections.Generic;
using System.Linq;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>Helpers the meta screens share: a label, an icon and a focusable row.</summary>
    internal static class MetaDom
    {
        public static LocLabel Label(VisualElement parent, string text, params string[] classes)
        {
            var label = new LocLabel { pickingMode = PickingMode.Ignore };
            foreach (var cls in classes) label.AddToClassList(cls);
            label.SetResolved(text);
            parent?.Add(label);
            return label;
        }

        public static VisualElement Box(VisualElement parent, params string[] classes)
        {
            var box = new VisualElement();
            foreach (var cls in classes) box.AddToClassList(cls);
            parent?.Add(box);
            return box;
        }
    }

    /// <summary>
    /// W-16 the run history (web src/ui/screens/history.js; docs/qa/web-screens/run-history-*, D-1006): the painting of the land, RECORDS, "Run history", the line of standard runs, wins and the win rate, each
    /// class's record, then one row per run, newest first: the class, where it ended and how many fights, the result and Custom pills and the seed. Back returns; the Compendium, the Profile and About
    /// are one press away (the web reaches them from the title and from Settings). Reads the profile through <see cref="JournalView"/>.
    /// </summary>
    public sealed class JournalScreen : ScreenView
    {
        private JournalViewState _view;

        public JournalViewState View => _view;

        protected override void OnBind(object args)
        {
            var profile = ProfileStore.Load(Ui.Saves);
            _view = JournalView.Build(profile.Doc, Ui.RunContent, Ui.Data);
            var summary = Root.Q<LocLabel>(UiNames.JournalSummary);
            summary.SetResolved(_view.Summary ?? string.Empty);
            UiDom.Show(summary, _view.Summary != null);
            var records = Root.Q(UiNames.JournalRecords);
            foreach (var record in _view.ClassRecords)
            {
                var cell = MetaDom.Box(records, FrontClasses.JournalRecord);
                var label = MetaDom.Label(cell, record.Key, FrontClasses.JournalRecordLabel);
                label.AddToClassList(UiClasses.Upper);
                label.SetResolved(record.Key);
                MetaDom.Label(cell, record.Value, FrontClasses.JournalRecordValue);
            }
            UiDom.Show(records, _view.ClassRecords.Count > 0);
            var rows = Root.Q(UiNames.JournalRows);
            foreach (var run in _view.History) rows.Add(Row(run));
            UiDom.Show(Root.Q(UiNames.JournalEmpty), _view.History.Count == 0);
            UiDom.Show(Root.Q(UiNames.JournalRowsScroll), _view.History.Count > 0);
            Root.Q<LocButton>(UiNames.JournalBack).clicked += () => Nav.Pop();
            Open(UiNames.JournalCompendium, ScreenIds.Compendium);
            Open(UiNames.JournalProfile, ScreenIds.Profile);
            Open(UiNames.JournalAbout, ScreenIds.About);
            Context.Instance.LastFocused = Root.Q(UiNames.JournalBack);
        }

        /// <summary>
        /// The Compendium, Profile and About links are Unity's own: the web's run history has only Back (history.js: one ButtonRow with Back), reaches the Compendium and the Profile from no menu at the pin
        /// (main.js showCompendium / showProfile are wired to nothing a player can press) and About from Settings. The buttons stay in the view, hidden, with their screens (P-6 D-1647).
        /// </summary>
        private void Open(string name, string screen)
        {
            var button = Root.Q<LocButton>(name);
            UiDom.Show(button, false);
            button.clicked += () => Nav.Go(screen);
        }

        private VisualElement Row(JournalRunRow run)
        {
            var row = new VisualElement { focusable = true };
            row.AddToClassList(FrontClasses.JournalRow);
            MetaDom.Label(row, run.Title, FrontClasses.JournalRowClass);
            var tail = MetaDom.Box(row, FrontClasses.JournalRowTail);
            tail.pickingMode = PickingMode.Ignore;
            MetaDom.Label(tail, run.WhereLine, FrontClasses.JournalRowWhere);
            Pill(tail, run.ResultPill, run.Victory);
            if (run.Custom) Pill(tail, run.Pills.Count > 0 ? run.Pills[0] : Ui.Data.Strings.Get(StringKeys.HistoryCustom), false);
            var seed = MetaDom.Label(tail, run.Seed, FrontClasses.JournalSeed);
            seed.AddToClassList(UiClasses.Upper);
            seed.SetResolved(run.Seed);
            return row;
        }

        private static void Pill(VisualElement parent, string text, bool on)
        {
            var pill = MetaDom.Label(parent, text, UiClasses.Chip, FrontClasses.JournalPill);
            pill.AddToClassList(UiClasses.Upper);
            pill.EnableInClassList(UiClasses.ChipOn, on);
            pill.SetResolved(text);
        }
    }

    /// <summary>
    /// The Compendium (web src/ui/screens/compendium.js; docs/qa/web-screens/compendium-*, D-1006): the armaments by kind on the rail with how many are held, a grid of what the profile has found (a name and
    /// its rarity and hand) and what it has not (an unknown silhouette), and the inspector for the one chosen. Reads the profile and the catalog through <see cref="CompendiumView"/>.
    /// </summary>
    public sealed class CompendiumScreen : ScreenView
    {
        private CompendiumViewState _view;
        private Shell _shell;
        private Workspace _workspace;
        private string _tab;
        private string _selected;

        public CompendiumViewState View => _view;
        public string Tab => _tab;
        public string Selected => _selected;

        protected override void OnBind(object args)
        {
            _view = CompendiumView.Build(ProfileStore.Load(Ui.Saves).Doc, Ui.RunContent, Ui.Data);
            _shell = Root.Q<Shell>(UiNames.Shell);
            _shell.SetFooter(null, StringKeys.HistoryBack);
            _shell.Primary += () => Nav.Pop();
            _shell.Exit += () => Nav.Pop();
            _workspace = Root.Q<Workspace>(UiNames.Workspace);
            _workspace.Rules = Ui.Data.Layout.CategoryNav;
            _workspace.Picked += id => { _tab = id; _selected = null; Render(); };
            _tab = _view.Tabs.FirstOrDefault()?.Id;
            Render();
            Context.Instance.LastFocused = Root.Q(UiNames.CompGrid).Children().FirstOrDefault() ?? (VisualElement)_shell.PrimaryButton;
        }

        private CompendiumTab Current => _view.Tabs.FirstOrDefault(t => t.Id == _tab);

        private void Render()
        {
            Root.Q<LocLabel>(UiNames.CompEyebrow).SetResolved(_view.Eyebrow);
            _workspace.SetCategories(_view.Tabs.Select(t => new CategoryItem { Id = t.Id, LabelKey = string.Format(System.Globalization.CultureInfo.InvariantCulture, FrontFormats.CompendiumTab, t.Id), Count = t.Count }).ToList(), _tab);
            var grid = Root.Q(UiNames.CompGrid);
            grid.Clear();
            var tab = Current;
            if (tab == null) return;
            if (_selected == null) _selected = tab.Entries.FirstOrDefault()?.Id;
            foreach (var entry in tab.Entries)
            {
                var card = new LocButton();
                card.AddToClassList(FrontClasses.CompCard);
                card.EnableInClassList(FrontClasses.CompCardUnknown, !entry.Found);
                card.EnableInClassList(UiClasses.Selected, entry.Id == _selected);
                Icon(card, entry, FrontClasses.CompIcon);
                var texts = MetaDom.Box(card, FrontClasses.CompCardTexts);
                texts.pickingMode = PickingMode.Ignore;
                MetaDom.Label(texts, entry.Name, FrontClasses.CompCardName);
                var meta = MetaDom.Label(texts, entry.Meta, FrontClasses.CompCardMeta);
                meta.AddToClassList(UiClasses.Upper);
                meta.SetResolved(entry.Meta);
                var id = entry.Id;
                card.clicked += () => { _selected = id; Render(); };
                grid.Add(card);
            }
            Inspect(tab.Entries.FirstOrDefault(e => e.Id == _selected));
        }

        private void Icon(VisualElement parent, CompendiumEntry entry, string cls)
        {
            var icon = MetaDom.Box(parent, cls);
            icon.pickingMode = PickingMode.Ignore;
            BoardHud.SetImage(icon, Ui.Texture(entry.ArtId));
            icon.EnableInClassList(FrontClasses.CompIconUnknown, !entry.Found);
        }

        private void Inspect(CompendiumEntry entry)
        {
            var host = Root.Q(UiNames.CompInspector);
            host.Clear();
            if (entry == null) return;
            Icon(host, entry, FrontClasses.CompInspectorIcon);
            var box = MetaDom.Box(host, FrontClasses.CompInspectorBox);
            var meta = MetaDom.Label(box, entry.Meta, FrontClasses.CompInspectorMeta);
            meta.AddToClassList(UiClasses.Upper);
            meta.SetResolved(entry.Meta);
            MetaDom.Label(box, entry.Name, FrontClasses.CompInspectorName);
            MetaDom.Label(box, entry.Detail, FrontClasses.CompInspectorText);
            if (entry.Stats.Count == 0) return;
            var stats = MetaDom.Box(box, FrontClasses.CompInspectorStats);
            foreach (var stat in entry.Stats) MetaDom.Label(stats, Ui.Data.Strings.Format(StringKeys.CreationReviewStat, new StringArgs().Add(UiPlaceholders.Value, stat.Value).Add(UiPlaceholders.Label, stat.Key)), UiClasses.Chip);
        }
    }

    /// <summary>The About screen: the same text as Settings → Advanced → About (<see cref="AboutPanel"/>) in its own frame.</summary>
    public sealed class AboutScreen : ScreenView
    {
        protected override void OnBind(object args)
        {
            var shell = Root.Q<Shell>(UiNames.Shell);
            shell.SetFooter(null, StringKeys.HistoryBack);
            shell.Primary += () => Nav.Pop();
            shell.Exit += () => Nav.Pop();
            AboutPanel.Fill(Root.Q(UiNames.AboutBody), AboutView.Build(Ui.Data, Ui.BuildVersion), Ui.Data.Strings);
            Context.Instance.LastFocused = shell.PrimaryButton;
        }
    }
}
