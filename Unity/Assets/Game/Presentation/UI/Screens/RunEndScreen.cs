using Ashen.App.Run;
using Ashen.App.Ui;
using Ashen.Generated;
using Ashen.Presentation.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// W-15 run end (AF-11; US-4.7, US-4.9, US-13.1): victory ("EMBER RESTORED" over the lit citadel) or death ("YOU
    /// PERISHED" over the fight's region, dimmed); who and where; the stat strip (seed, class, act and floor, the killer or
    /// the felled keeper, the time, cinders, fights won); the final deck; what this run unlocked (finishRun's receipt). The
    /// run is already recorded in the profile and its slot is clear (permadeath). [Run history] waits for the journal
    /// (W-16, planned); [Return to title] ends it.
    /// </summary>
    public sealed class RunEndScreen : ScreenView
    {
        private RunSession _session;
        private Shell _shell;

        public RunSession Session => _session;
        public RunEndViewState View { get; private set; }
        public LocButton TitleButton => _shell?.PrimaryButton;

        protected override void OnBind(object args)
        {
            _session = (args as RunScreenArgs)?.Session ?? Ui.Session;
            if (_session == null || !_session.RunOver)
            {
                Debug.LogError(UiMessages.NoRun);
                return;
            }
            var strings = Ui.Data.Strings;
            var view = View = RunEndView.Build(_session, Ui.Data);
            _shell = Root.Q<Shell>(UiNames.Shell);
            _shell.SetFooter(StringKeys.RunEndHistory, StringKeys.RunEndToTitle);
            _shell.PrimaryButton?.AddToClassList(UiClasses.ButtonReady);
            _shell.Primary += ToTitle;
            var history = _shell.BackButton;
            _shell.Back += () =>
            {
                if (Ui.Data.Screens.IsBuilt(ScreenIds.Journal)) Nav.Go(ScreenIds.Journal);
                else RunFlow.Refuse(Context, history, StringKeys.RunEndHistoryLater);
            };
            _shell.TitleLabel?.SetResolved(view.Title);
            Root.EnableInClassList(UiClasses.RunEndVictory, view.Victory);
            Root.EnableInClassList(UiClasses.RunEndDefeat, !view.Victory);
            Root.Q<LocLabel>(UiNames.RunEndDetail)?.SetResolved(view.Detail);
            Root.Q<LocLabel>(UiNames.RunEndWhere)?.SetResolved(view.Where);
            Root.Q<LocLabel>(UiNames.RunEndDeckTitle)?.SetResolved(view.DeckTitle);
            Fill(Root.Q(UiNames.RunEndStats), view.Stats, UiClasses.RunEndStat);
            Fill(Root.Q(UiNames.RunEndDeck), view.Deck, UiClasses.RunEndCard);
            var unlocks = Root.Q(UiNames.RunEndUnlocks);
            if (view.Unlocks.Count > 0) Fill(unlocks, view.Unlocks, UiClasses.RunEndUnlock);
            else Fill(unlocks, new[] { strings.Get(StringKeys.RunEndUnlocksNone) }, UiClasses.RunEndUnlock);
            var figure = Root.Q(UiNames.RunEndFigure);
            if (figure != null) Ui.ApplyBackground(figure, view.Portrait);
            var background = Root.Q(UiNames.RunEndBackground);
            if (background != null)
            {
                var components = Ui.Data.Components;
                var id = view.Victory ? components.Climb.VictoryBackground
                    : StringTable.Fill(components.CombatBackground, new StringArgs().Add(UiPlaceholders.Region, view.Region));
                Ui.ApplyBackground(background, Ui.Art != null && Ui.Art.Get(id) != null ? id : components.CombatFallbackBackground);
            }
            Context.Instance.LastFocused = _shell.PrimaryButton;
        }

        private static void Fill(VisualElement list, System.Collections.Generic.IEnumerable<string> lines, string className)
        {
            if (list == null) return;
            list.Clear();
            foreach (var text in lines)
            {
                var label = new LocLabel();
                label.SetResolved(text);
                label.AddToClassList(className);
                label.AddToClassList(UiClasses.ValueText);
                list.Add(label);
            }
        }

        /// <summary>Return to title: the run is over and its slot already clear.</summary>
        public void ToTitle()
        {
            Ui.Session = null;
            Nav.Go(ScreenIds.Title, null, true);
        }

        public override bool HandleBack() => true;
    }
}
