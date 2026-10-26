using System;
using System.IO;
using Ashen.App.Run;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Platform;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// Download &amp; saves (web src/ui/components/offlinePlay.js, the Settings header's button): Move your saves. Export saves hands over one file holding the profile and every run slot, Import saves reads such a file (a file the
    /// player picks in a browser; the exports folder elsewhere), asks before it replaces anything and keeps a previous-save backup of what it replaced, which Export previous-save backup hands over. The web's game download and
    /// offline-copy sections are the hosted build's own and are not here (D-1648).
    /// </summary>
    public sealed class TransferScreen : ScreenView
    {
        private SaveTransfer _transfer;
        private LocLabel _status;
        private LocButton _import;
        private IVisualElementScheduledItem _pick;

        protected override void OnBind(object args)
        {
            _transfer = new SaveTransfer(Ui.Saves);
            var strings = Ui.Data.Strings;
            var shell = Root.Q<Shell>(UiNames.Shell);
            shell.SetFooter(null, StringKeys.TransferDone);
            shell.Primary += () => Nav.Pop();
            shell.Exit += () => Nav.Pop();
            Root.Q<LocLabel>(UiNames.TransferNote).SetResolved(strings.Format(StringKeys.TransferNote, new StringArgs().Add(UiPlaceholders.Count, _transfer.SlotCount)));
            _status = Root.Q<LocLabel>(UiNames.TransferStatus);
            _import = Root.Q<LocButton>(UiNames.TransferImport);
            Root.Q<LocButton>(UiNames.TransferExport).clicked += Export;
            _import.clicked += Import;
            var recovery = Root.Q<LocButton>(UiNames.TransferRecovery);
            UiDom.Show(recovery, _transfer.Previous() != null);
            recovery.clicked += () => Hand(_transfer.Previous(), TransferKeys.PreviousBackupFile, null);
            Context.Instance.LastFocused = shell.PrimaryButton;
        }

        private void Say(string text)
        {
            _status.SetResolved(text);
            UiDom.Show(_status, text.Length > 0);
        }

        private void Refuse(TransferRefusal refusal) => Say(Ui.Data.Strings.Format(refusal.Key, refusal.Args));

        private void Export() => Hand(_transfer.CreateBackup(), TransferKeys.BackupFile, StringKeys.TransferExported);

        /// <summary>Hand a file to the player; the web says nothing after the previous-save backup, and Save backup downloaded after the other.</summary>
        private void Hand(string text, string name, string said)
        {
            if (text == null) return;
            if (!FileExport.Save(name, text)) { Say(Ui.Data.Strings.Get(StringKeys.TransferErrSaveFailed)); return; }
            if (said != null) Say(Ui.Data.Strings.Get(said));
        }

        /// <summary>The web checks that no climb is in hand (assertImportAllowed) and then opens the file chooser; the file is read and inspected before anything is asked.</summary>
        private void Import()
        {
            if (Ui.Climb != null) { Say(Ui.Data.Strings.Get(StringKeys.TransferErrTitle)); return; }
            if (WebFiles.Available)
            {
                WebFiles.Pick(WebGlNames.JsonAccept);
                _pick?.Pause();
                _pick = Root.schedule.Execute(PollPick).Every(Ui.Data.Tokens.Duration(TokenKeys.FilePoll));
                return;
            }
            var path = Path.Combine(Application.persistentDataPath, UiResources.ExportsFolder, TransferKeys.BackupFile);
            if (!File.Exists(path))
            {
                Say(Ui.Data.Strings.Format(StringKeys.TransferErrNoFile, new StringArgs().Add(UiPlaceholders.Path, Path.GetDirectoryName(path))));
                return;
            }
            Offer(File.ReadAllText(path));
        }

        private void PollPick()
        {
            var state = WebFiles.Poll(out var text);
            if (state == WebFiles.PickState.Waiting) return;
            _pick?.Pause();
            if (state == WebFiles.PickState.Ready) Offer(text);
        }

        /// <summary>Read the backup, say what it holds and ask (web: openConfirmationModal "Replace this browser’s saves?"); the live saves are not touched until the answer.</summary>
        private void Offer(string text)
        {
            var strings = Ui.Data.Strings;
            TransferPreview preview;
            try { preview = _transfer.Inspect(text); }
            catch (TransferRefusal refusal) { Refuse(refusal); return; }
            var message = strings.Format(preview.Runs == 1 ? StringKeys.TransferConfirmRunsOne : StringKeys.TransferConfirmRunsMany, new StringArgs().Add(UiPlaceholders.Count, preview.Runs))
                + strings.Get(preview.HasProfile ? StringKeys.TransferConfirmWithProfile : StringKeys.TransferConfirmNoProfile);
            ConfirmRequest.Open(Nav, new ConfirmRequest
            {
                ConfirmId = ConfirmIds.ImportSaves,
                Args = new StringArgs().Add(UiPlaceholders.Count, preview.Slots),
                Target = message,
                OnConfirm = () => Restore(text),
            });
        }

        private void Restore(string text)
        {
            try { _transfer.Restore(text); }
            catch (TransferRefusal refusal) { Refuse(refusal); return; }
            Say(Ui.Data.Strings.Get(StringKeys.TransferImported));
            // The web reloads the game with the imported saves; here the settings come from the new profile and the game returns to the title.
            Context.Host?.Applier?.Apply(new Ashen.App.Settings.SettingsSession(Ui.RunContent, ProfileStore.Load(Ui.Saves, false), core2: true));
            TitleScreen.ResetSession();
            Nav.Go(ScreenIds.Title, null, true);
        }

        public override void Unbind() => _pick?.Pause();
    }
}
