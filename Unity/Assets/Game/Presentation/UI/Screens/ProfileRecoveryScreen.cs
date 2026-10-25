using System;
using System.Globalization;
using System.IO;
using Ashen.App.Run;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// W-01 the profile that would not read (web src/ui/screens/profileNotice.js mountProfileNotice; the words are Sunna's): the door a player lands on, in front of the title, when the profile is corrupt or older than this build
    /// can open (its bytes were set aside, not deleted), or newer (its bytes are fine, left exactly where they are). It says what happened, what survived and where it is, and offers the ways on: Try to restore (the set-aside
    /// copy is read again and, if it reads, becomes the profile), Save a copy to a file, Not now, and, last and behind its own question, Start a new profile. In the newer state the first way on is to do nothing (Keep it and close).
    /// The profile is never replaced without its old bytes being recoverable.
    /// </summary>
    public sealed class ProfileRecoveryScreen : ScreenView
    {
        private ProfileNoticeViewState _view;
        private LocLabel _result;

        public ProfileNoticeViewState View => _view;

        protected override void OnBind(object args)
        {
            var strings = Ui.Data.Strings;
            var profile = ProfileStore.Load(Ui.Saves);
            _view = ProfileNoticeView.Build(Ui.Saves, profile, strings);

            var shell = Root.Q<Shell>(UiNames.Shell);
            shell.TitleLabel.SetResolved(_view.DoorTitle);
            shell.EyebrowLabel.SetResolved(_view.Eyebrow);
            shell.EyebrowLabel.AddToClassList(UiClasses.Upper);
            UiDom.Show(shell.EyebrowLabel, true);
            shell.SetFooter(null, null);

            var body = Root.Q(UiNames.RecoveryBody);
            var heading = MetaDom.Label(body, _view.Heading, FrontClasses.ProfileStatus);
            heading.AddToClassList(ArchiveClasses.State);
            for (var i = 0; i < _view.Lead.Count; i++)
            {
                var lead = MetaDom.Label(body, _view.Lead[i], FrontClasses.ProfileBody);
                if (_view.LeadEmphasis && i == _view.Lead.Count - 1) lead.AddToClassList(ArchiveClasses.Emphasis);
            }

            var card = MetaDom.Box(body, FrontClasses.ProfileCard, ArchiveClasses.Entry);
            var eyebrow = MetaDom.Label(card, _view.CardEyebrow, FrontClasses.ProfileEyebrow, ArchiveClasses.EntryEyebrow);
            eyebrow.AddToClassList(UiClasses.Upper);
            MetaDom.Label(card, _view.CardName, ArchiveClasses.EntryName);
            if (_view.CardLine.Length > 0) MetaDom.Label(card, _view.CardLine, ArchiveClasses.EntryLine);
            MetaDom.Label(card, _view.CardMeta, ArchiveClasses.EntryLine);

            // The way forward stands alone on its own rung; the two quiet ways share the next; the one destructive way is last and never widest.
            var first = _view.Newer ? Button(MetaDom.Box(body, ArchiveClasses.Actions), _view.LeaveLabel) : Button(MetaDom.Box(body, ArchiveClasses.Actions), _view.RestoreLabel);
            var quiet = MetaDom.Box(body, ArchiveClasses.Actions);
            var export = Button(quiet, _view.ExportLabel);
            var leave = _view.Newer ? first : Button(quiet, _view.LeaveLabel);
            var fresh = Button(MetaDom.Box(body, ArchiveClasses.Actions), _view.FreshLabel);
            fresh.AddToClassList(UiClasses.ToneDestructive);

            _result = MetaDom.Label(body, string.Empty, ArchiveClasses.Result);
            UiDom.Show(_result, false);
            if (!string.IsNullOrEmpty(_view.Reason)) Support(body, _view.Reason, _view.SupportLabel);

            leave.clicked += () => Nav.Fire(ScreenTriggers.Done);
            if (!_view.Newer) first.clicked += Restore;
            export.clicked += Export;
            fresh.clicked += Ask;

            // Something is ALWAYS focused (Sunna D1): Try to restore takes the cursor where it exists, and in the newer state that is Keep it and close, which is the right primary there. The destructive button never takes focus.
            Context.Instance.LastFocused = first;
        }

        private static LocButton Button(VisualElement row, string text)
        {
            var button = new LocButton();
            button.AddToClassList(ArchiveClasses.Action);
            button.SetResolved(text);
            row.Add(button);
            return button;
        }

        /// <summary>Details for support: the raw engine text behind the plain words, folded (web fold).</summary>
        private static void Support(VisualElement body, string reason, string label)
        {
            var head = new LocButton();
            head.AddToClassList(ArchiveClasses.Support);
            head.SetResolved(label);
            body.Add(head);
            var text = MetaDom.Label(null, reason, ArchiveClasses.SupportBody);
            UiDom.Show(text, false);
            body.Add(text);
            head.clicked += () => UiDom.Show(text, text.resolvedStyle.display == DisplayStyle.None);
        }

        private void Say(string message)
        {
            _result.SetResolved(message);
            UiDom.Show(_result, message.Length > 0);
        }

        /// <summary>Try to restore: the set-aside copy is read again. If it reads it becomes the profile and the game goes on; if not, the copy stays exactly where it was and can still be saved to a file.</summary>
        private void Restore()
        {
            var result = Ui.Saves.RestoreProfile(_view.ArchiveId);
            if (!result.Ok)
            {
                Say(Ui.Data.Strings.Get(StringKeys.ProfileArchRestoreFailed));
                return;
            }
            Context.Host?.Applier?.Apply(new SettingsSession(Ui.RunContent, ProfileStore.Load(Ui.Saves, false), core2: true));
            Nav.Fire(ScreenTriggers.Done);
        }

        /// <summary>Export works in EVERY state: from the archive when there is one, from the live profile when there is not (the newer case, where it matters most).</summary>
        private void Export()
        {
            var strings = Ui.Data.Strings;
            var text = _view.ArchiveId != null ? Ui.Saves.Drawer.Export(_view.ArchiveId) : Ui.Saves.ExportProfile(_view.State);
            if (text == null) { Say(strings.Get(StringKeys.RecoveryNothingToSave)); return; }
            var name = string.Format(CultureInfo.InvariantCulture, ArchiveKeys.ExportProfileFile, _view.ArchiveId ?? _view.State);
            Say(strings.Get(FileExport.Save(name, text) ? StringKeys.ProfileArchSaved : StringKeys.ProfileArchSaveFailed));
        }

        /// <summary>The one irreversible act, behind a second one (Sunna D3): the door's focus starts on Not yet, the safe option, and the old profile is set aside first, never deleted.</summary>
        private void Ask() => ConfirmRequest.Open(Nav, new ConfirmRequest
        {
            ConfirmId = ConfirmIds.Recovery,
            Target = Ui.Data.Strings.Get(StringKeys.RecoveryEyebrow),
            OnConfirm = StartFresh,
        });

        private void StartFresh()
        {
            try
            {
                Ui.Saves.StartNewProfile();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Say(Ui.Data.Strings.Get(StringKeys.RecoveryFreshFailed));
                return;
            }
            Nav.Fire(ScreenTriggers.Done);
        }
    }
}
