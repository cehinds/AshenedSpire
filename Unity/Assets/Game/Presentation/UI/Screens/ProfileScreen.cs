using System.Globalization;
using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The Profile (web src/ui/screens/profileArchive.js, W1g; docs/qa/web-screens/profile-*, D-1006, D-1480): PLAYER DATA, "Profile", a rail of the two kinds the drawer holds (Profiles and Runs, with how many are set aside) and the pane:
    /// the profile in use (the state line, the drawer's policy, a notice when the drawer had to move a profile out) beside the selected kind's entries, newest first. Each entry says when it was set aside and how big it is, what it is and
    /// why, and offers Save a copy to a file, Restore this profile (profiles only, behind its own question) and Details for support. Nothing is deleted and a failed restore never consumes the copy. The classic build's tally and save
    /// slots (<see cref="ProfileView"/>) are not on this screen: the web's Profile has neither.
    /// </summary>
    public sealed class ProfileScreen : ScreenView
    {
        private ProfileArchiveViewState _view;
        private Shell _shell;
        private Workspace _workspace;
        private string _category;
        private string _message = string.Empty;

        public ProfileArchiveViewState View => _view;

        protected override void OnBind(object args)
        {
            _shell = Root.Q<Shell>(UiNames.Shell);
            _shell.SetFooter(null, StringKeys.ProfileBack);
            _shell.Primary += () => Nav.Pop();
            _shell.Exit += () => Nav.Pop();
            _workspace = Root.Q<Workspace>(UiNames.Workspace);
            _workspace.Rules = Ui.Data.Layout.CategoryNav;
            _workspace.Picked += id => { _category = id; Render(); };
            Render();
            Context.Instance.LastFocused = _shell.ExitButton ?? _shell.PrimaryButton;
        }

        private void Render()
        {
            var strings = Ui.Data.Strings;
            _view = ProfileArchiveView.Build(Ui.Saves, ProfileStore.Load(Ui.Saves, false), strings, _category);
            _category = _view.Current;
            var items = new CategoryItem[_view.Categories.Count];
            for (var i = 0; i < items.Length; i++)
                items[i] = new CategoryItem { Id = _view.Categories[i].Id, LabelKey = _view.Categories[i].LabelKey, Count = _view.Categories[i].Count.ToString(CultureInfo.InvariantCulture) };
            _workspace.SetCategories(items, _view.Current);

            var host = Root.Q(UiNames.ProfilePane);
            host.Clear();
            var identity = MetaDom.Box(host, ArchiveClasses.Identity);
            var eyebrow = MetaDom.Label(identity, strings.Get(StringKeys.ProfileYour), FrontClasses.ProfileEyebrow, ArchiveClasses.Eyebrow);
            eyebrow.AddToClassList(UiClasses.Upper);
            MetaDom.Label(identity, _view.StateLine, FrontClasses.ProfileStatus, ArchiveClasses.State);
            MetaDom.Label(identity, _view.Policy, FrontClasses.ProfileBody, ArchiveClasses.Policy);
            if (_view.Notice != null) Notice(identity, _view.Notice);

            var records = MetaDom.Box(host, ArchiveClasses.Records);
            if (_view.Empty != null) MetaDom.Label(records, _view.EmptyText, ArchiveClasses.Empty);
            else foreach (var entry in _view.Entries) records.Add(Entry(entry));
            var result = MetaDom.Label(records, _message, ArchiveClasses.Result);
            UiDom.Show(result, _message.Length > 0);
        }

        private static void Notice(VisualElement parent, ArchiveNoticeView notice)
        {
            var box = MetaDom.Box(parent, FrontClasses.ProfileCard, ArchiveClasses.Notice);
            var title = MetaDom.Label(box, notice.Title, FrontClasses.ProfileEyebrow, ArchiveClasses.NoticeTitle);
            title.AddToClassList(UiClasses.Upper);
            MetaDom.Label(box, notice.Body, FrontClasses.ProfileBody);
            foreach (var move in notice.Moves) MetaDom.Label(box, move, ArchiveClasses.NoticeLine);
            MetaDom.Label(box, notice.Tail, ArchiveClasses.NoticeLine);
        }

        private VisualElement Entry(ArchiveEntryView entry)
        {
            var strings = Ui.Data.Strings;
            var card = MetaDom.Box(null, FrontClasses.ProfileCard, ArchiveClasses.Entry);
            var eyebrow = MetaDom.Label(card, entry.Eyebrow, FrontClasses.ProfileEyebrow, ArchiveClasses.EntryEyebrow);
            eyebrow.AddToClassList(UiClasses.Upper);
            MetaDom.Label(card, entry.Name, ArchiveClasses.EntryName);
            MetaDom.Label(card, entry.Line, ArchiveClasses.EntryLine);

            var actions = MetaDom.Box(card, ArchiveClasses.Actions);
            var export = Action(actions, StringKeys.ProfileArchExport);
            export.clicked += () => Export(entry.Id);
            if (entry.CanRestore)
            {
                var restore = Action(actions, StringKeys.ProfileArchRestore);
                restore.clicked += () => Ask(card, entry);
            }
            if (!string.IsNullOrEmpty(entry.Reason)) Support(card, entry.Reason);
            return card;
        }

        private LocButton Action(VisualElement parent, string key)
        {
            var button = new LocButton(key);
            button.AddToClassList(ArchiveClasses.Action);
            parent.Add(button);
            return button;
        }

        /// <summary>Details for support: the raw engine text behind the plain reason, folded.</summary>
        private void Support(VisualElement card, string reason)
        {
            var head = new LocButton(StringKeys.ProfileArchSupport);
            head.AddToClassList(ArchiveClasses.Support);
            card.Add(head);
            var body = MetaDom.Label(null, reason, ArchiveClasses.SupportBody);
            UiDom.Show(body, false);
            card.Add(body);
            head.clicked += () => UiDom.Show(body, body.resolvedStyle.display == DisplayStyle.None);
        }

        private void Export(string id)
        {
            var strings = Ui.Data.Strings;
            var text = Ui.Saves.Drawer.Export(id);
            if (text == null) { Say(strings.Get(StringKeys.ProfileArchGone)); return; }
            var saved = FileExport.Save(string.Format(CultureInfo.InvariantCulture, ArchiveKeys.ExportFile, id), text);
            Say(strings.Get(saved ? StringKeys.ProfileArchSaved : StringKeys.ProfileArchSaveFailed));
        }

        /// <summary>The risk is stated BEFORE the click does anything, in the entry's own question: the profile in use is set aside in its place, and a copy that could not be read may not restore.</summary>
        private void Ask(VisualElement card, ArchiveEntryView entry)
        {
            if (card.Q(className: ArchiveClasses.Confirm) != null) return;
            var strings = Ui.Data.Strings;
            var box = MetaDom.Box(card, FrontClasses.ProfileCard, ArchiveClasses.Confirm);
            var title = MetaDom.Label(box, strings.Get(StringKeys.ProfileArchConfirmTitle), FrontClasses.ProfileEyebrow);
            title.AddToClassList(UiClasses.Upper);
            MetaDom.Label(box, strings.Get(StringKeys.ProfileArchConfirmBody), FrontClasses.ProfileBody);
            if (entry.Unreadable) MetaDom.Label(box, strings.Get(StringKeys.ProfileArchConfirmUnreadable), FrontClasses.ProfileBody);
            var actions = MetaDom.Box(box, ArchiveClasses.Actions);
            var cancel = Action(actions, StringKeys.ProfileArchConfirmCancel);
            var go = Action(actions, StringKeys.ProfileArchConfirmGo);
            cancel.clicked += () => box.RemoveFromHierarchy();
            go.clicked += () => Restore(box, entry.Id);
            cancel.Focus();
        }

        private void Restore(VisualElement box, string id)
        {
            var strings = Ui.Data.Strings;
            var result = Ui.Saves.RestoreProfile(id);
            if (result.Ok)
            {
                // Re-render FIRST, then speak into the node that survives it: the entry that was acted on is gone from the list.
                _message = strings.Get(StringKeys.ProfileArchRestored);
                Render();
                Context.Host?.Applier?.Apply(new Ashen.App.Settings.SettingsSession(Ui.RunContent, ProfileStore.Load(Ui.Saves, false), core2: true));
                return;
            }
            // A failed restore NEVER consumes the copy, and says so.
            box.RemoveFromHierarchy();
            Say(strings.Get(StringKeys.ProfileArchRestoreFailed));
        }

        private void Say(string message)
        {
            _message = message;
            var result = Root.Q<LocLabel>(className: ArchiveClasses.Result);
            if (result == null) return;
            result.SetResolved(message);
            UiDom.Show(result, message.Length > 0);
        }
    }
}
