using System;
using Ashen.App.Run;
using Ashen.App.Settings;
using Ashen.App.Ui;
using Ashen.Domain.Combat;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using PK = Ashen.Generated.PrologueKeys;
using PV = Ashen.Generated.PrologueValues;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>What the opening opens with: the climb it leads into and the scenes to play.</summary>
    public sealed class PrologueArgs
    {
        public IClimb Session;
        public IPrologue Opening;
    }

    /// <summary>
    /// W-05 the opening (US-3.1..3.3; web src/ui/screens/prologue.js, D-1004): one painting of ui/prologue.json at a time with the caption plate under it (the scene's name, who speaks, the line
    /// with the typewriter reveal) and the bar of n / N, Pause, Settings, Skip opening and Continue (Set forth on the last). The scenes move on by themselves after their hold when the player's
    /// "Advance scenes automatically" is on (Pause holds them); a press on the painting finishes the reveal, then moves on. When the last scene is done, or the opening is skipped, the climb
    /// stands at its map. The run is not touched by the opening: a Core 2 opening lives in memory (<see cref="PrologueFlow"/>).
    /// </summary>
    public sealed class PrologueScreen : ScreenView
    {
        private IClimb _session;
        private IPrologue _opening;
        private PrologueViewState _view;
        private string _line = string.Empty;
        private double _shown;
        private double _perSecond;
        private bool _paused;
        private bool _inSettings;
        private IVisualElementScheduledItem _reveal;
        private IVisualElementScheduledItem _advance;

        /// <summary>The scene on screen (tests and captures read it).</summary>
        public PrologueViewState View => _view;

        public bool Paused => _paused;

        private SettingsSession Settings => _session?.PlayerSettings ?? new SettingsSession(Ui.RunContent, ProfileStore.Load(Ui.Saves), core2: true);

        protected override void OnBind(object args)
        {
            var a = (PrologueArgs)args;
            _session = a.Session;
            _opening = a.Opening;
            Root.Q<LocButton>(UiNames.PrologueContinue).clicked += Next;
            Root.Q<LocButton>(UiNames.PrologueSkip).clicked += Skip;
            Root.Q<LocButton>(UiNames.ProloguePause).clicked += TogglePause;
            Root.Q<LocButton>(UiNames.PrologueSettings).clicked += OpenSettings;
            Root.Q(UiNames.PrologueStage).RegisterCallback<PointerDownEvent>(_ => Press());
            Show();
            Context.Instance.LastFocused = Root.Q(UiNames.PrologueContinue);
        }

        private void Show()
        {
            _view = _opening.View(Ui.Data);
            if (_view == null)
            {
                Finish();
                return;
            }
            var presentation = _view.Presentation;
            var narrow = Nav.Layout != null && Nav.Layout.Narrow;
            var art = Root.Q(UiNames.PrologueArt);
            art.RemoveFromClassList(FrontClasses.PrologueArtIn);
            Ui.ApplyBackground(art, narrow ? _view.ArtNarrow : _view.ArtWide);
            // The phone's bar reads 1 / 5, Continue, then Pause, Settings and Skip (flow-07-prologue-390x844.jpg): the one button moves behind the progress.
            var advance = Root.Q<LocButton>(UiNames.PrologueContinue);
            if (narrow) advance.PlaceInFront(Root.Q(UiNames.PrologueProgress));
            else advance.BringToFront();
            // Still artwork (the web's presentation.reduceMotion, or the accessibility Reduced motion): no fade in.
            if (Settings.On(SettingIds.OpeningStill) || Settings.On(SettingIds.ReducedMotion)) art.AddToClassList(FrontClasses.PrologueArtIn);
            else art.schedule.Execute(() => art.AddToClassList(FrontClasses.PrologueArtIn));
            var item = Root.Q(UiNames.PrologueItem);
            Ui.ApplyBackground(item, _view.Item);
            UiDom.Show(item, _view.Item != null);
            SetText(UiNames.PrologueTitle, _view.Title);
            SetText(UiNames.PrologueSpeaker, _view.Speaker);
            SetText(UiNames.PrologueProgress, _view.Progress);
            Root.Q<LocButton>(UiNames.PrologueContinue).SetResolved(_view.Continue);
            UiDom.Show(Root.Q(UiNames.ProloguePause), presentation.Is(PK.ShowPause));
            UiDom.Show(Root.Q(UiNames.PrologueSkip), presentation.Is(PK.ShowSkip));
            SetPauseLabel();
            _line = _view.Line ?? string.Empty;
            _perSecond = Js.Or0(presentation[PK.RevealCharsPerSecond]) * Speed;
            var typewriter = presentation.Str(PK.Reveal) == PV.RevealTypewriter && _perSecond > 0 && !Settings.On(SettingIds.ReducedMotion);
            _shown = typewriter ? 0 : _line.Length;
            ShowLine();
            Resume();
        }

        private void SetText(string name, string text)
        {
            var label = Root.Q<LocLabel>(name);
            label?.SetResolved(text ?? string.Empty);
            UiDom.Show(label, !string.IsNullOrEmpty(text));
        }

        private void ShowLine() =>
            Root.Q<LocLabel>(UiNames.PrologueLine).SetResolved(_line.Substring(0, Math.Min(_line.Length, (int)Math.Ceiling(_shown))));

        private bool Revealed => _shown >= _line.Length;

        /// <summary>Starts (or restarts) the reveal and, once it is done, the hold before the scene moves on by itself.</summary>
        private void Resume()
        {
            StopTimers();
            if (_paused || _inSettings || _view == null) return;
            if (!Revealed)
            {
                var tick = Ui.Data.Tokens.Duration(TokenKeys.PrologueTick);
                _reveal = Root.schedule.Execute(() =>
                {
                    _shown += _perSecond * tick / UiMath.MillisPerSecond;
                    ShowLine();
                    if (!Revealed) return;
                    _reveal?.Pause();
                    Resume();
                }).Every(tick);
                return;
            }
            if (Settings.On(SettingIds.OpeningAutoAdvance) && _view.HoldMs > 0) _advance = Root.schedule.Execute(Next).StartingIn((long)(_view.HoldMs / Speed));
        }

        /// <summary>Playback speed (the web's presentation.speed): the reveal goes faster and the hold between scenes shorter.</summary>
        private double Speed => Math.Max(Settings.Number(SettingIds.OpeningSpeed), double.Epsilon);

        private void StopTimers()
        {
            _reveal?.Pause();
            _advance?.Pause();
            _reveal = null;
            _advance = null;
        }

        /// <summary>A press on the painting: finishes the reveal, then moves on (presentation.advanceOnClick).</summary>
        private void Press()
        {
            if (_view == null || !_view.Presentation.Is(PK.AdvanceOnClick)) return;
            if (!Revealed)
            {
                _shown = _line.Length;
                ShowLine();
                Resume();
                return;
            }
            Next();
        }

        /// <summary>Continue: the next scene, or the climb ("Set forth").</summary>
        public void Next()
        {
            StopTimers();
            _opening.Advance();
            Show();
        }

        /// <summary>Skip opening: the climb stands at its map at once.</summary>
        public void Skip()
        {
            StopTimers();
            _opening.Skip();
            Finish();
        }

        private void TogglePause()
        {
            _paused = !_paused;
            SetPauseLabel();
            Resume();
        }

        private void SetPauseLabel() =>
            Root.Q<LocButton>(UiNames.ProloguePause).SetResolved(Ui.Data.Strings.Get(_paused ? MetaStringKeys.PrologueResume : MetaStringKeys.ProloguePause));

        private void OpenSettings()
        {
            if (!Ui.Data.Screens.IsBuilt(ScreenIds.Settings)) return;
            _inSettings = true;
            StopTimers();
            Nav.Go(ScreenIds.Settings);
        }

        public override void OnReturned()
        {
            _inSettings = false;
            Resume();
        }

        public override bool HandleBack()
        {
            TogglePause();
            return true;
        }

        private void Finish()
        {
            StopTimers();
            Settings.MarkPrologueSeen();
            if (_session != null) RunFlow.ShowClimb(Context, _session);
        }

        public override void Unbind() => StopTimers();
    }
}
