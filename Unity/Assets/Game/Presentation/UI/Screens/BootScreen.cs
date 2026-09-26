using System.Collections.Generic;
using System.Globalization;
using Ashen.App.Saves;
using Ashen.App.Ui;
using Ashen.Content;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>Fixed boot state for review captures.</summary>
    public sealed class BootArgs
    {
        public int Done;
        public int Total;
        public bool Validating;
    }

    public sealed class BootViewModel : ViewModel
    {
        private int _done;
        private int _total;
        private bool _validating;

        public int Done { get => _done; set => Set(ref _done, value); }
        public int Total { get => _total; set => Set(ref _total, value); }
        public bool Validating { get => _validating; set => Set(ref _validating, value); }
    }

    /// <summary>
    /// W-24 boot (US-1.1 entry, PF-02): the wordmark, a determinate bar over the content manifest count and
    /// {boot.loading}; no input. Reads every manifest file, validates the content, checks the profile, then fires
    /// ready (title), failed (contentError with the report) or recovery (profileRecovery).
    /// </summary>
    public sealed class BootScreen : ScreenView
    {
        private readonly BootViewModel _vm = new BootViewModel();
        private IReadOnlyList<ContentManifest.Entry> _files;
        private IVisualElementScheduledItem _tick;
        private float _started;

        protected override void OnBind(object args)
        {
            var bar = Root.Q<Meter>(UiNames.BootProgress);
            var status = Root.Q<LocLabel>(UiNames.BootStatus);
            var count = Root.Q<LocLabel>(UiNames.BootCount);
            _vm.Observe(() =>
            {
                bar?.Set(_vm.Done, _vm.Total);
                if (status != null) status.stringKey = _vm.Validating ? StringKeys.BootValidating : StringKeys.BootLoading;
                count?.SetResolved(Ui.Data.Strings.Format(StringKeys.BootProgress, new StringArgs().Add(UiPlaceholders.Done, _vm.Done).Add(UiPlaceholders.Total, _vm.Total)));
            });
            if (args is BootArgs fixedState)
            {
                _vm.Total = fixedState.Total;
                _vm.Done = fixedState.Done;
                _vm.Validating = fixedState.Validating;
                return;
            }
            _started = Time.realtimeSinceStartup;
            _files = ContentManifest.Load(Ui.Source).Files;
            _vm.Total = _files.Count;
            _tick = Root.schedule.Execute(Step).Every(0);
        }

        private void Step()
        {
            if (_vm.Done < _files.Count)
            {
                var end = Mathf.Min(_files.Count, _vm.Done + Ui.Data.Components.BootFilesPerFrame);
                for (var i = _vm.Done; i < end; i++) Ui.Source.ReadText(_files[i].Path);
                _vm.Done = end;
                return;
            }
            if (!_vm.Validating)
            {
                _vm.Validating = true;
                return;
            }
            _tick.Pause();
            var report = new ContentValidator(Ui.Source).Validate();
            var elapsedMs = (Time.realtimeSinceStartup - _started) * UiMath.MillisPerSecond;
            var waitMs = Mathf.Max(0f, Ui.Data.Tokens.Duration(TokenKeys.BootMinimum) - (float)elapsedMs);
            Debug.Log(string.Format(CultureInfo.InvariantCulture, UiMessages.Booted, report.FilesChecked, (int)elapsedMs));
            Root.schedule.Execute(() => Finish(report)).StartingIn((long)waitMs);
        }

        private void Finish(ValidationReport report)
        {
            if (!report.IsValid)
            {
                Debug.LogError(string.Format(CultureInfo.InvariantCulture, UiMessages.ContentInvalid, report.Issues.Count));
                Nav.Fire(ScreenTriggers.Failed, report);
                return;
            }
            var profile = Ui.Saves.Rules.ProfileSlot;
            if (Ui.Saves.Exists(profile) && Ui.Saves.Load(profile).Status == LoadStatus.Corrupt)
            {
                Nav.Fire(ScreenTriggers.Recovery);
                return;
            }
            Nav.Fire(ScreenTriggers.Ready);
        }

        public override void Unbind() => _tick?.Pause();
    }
}
