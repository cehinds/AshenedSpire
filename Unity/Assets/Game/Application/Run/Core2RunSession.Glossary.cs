using Ashen.App.Ui;

namespace Ashen.App.Run
{
    public sealed partial class Core2RunSession
    {
        private TermGlossary _glossary;

        /// <summary>The tooltip glossary of the run's profile: the framework terms and the profile's statuses and stances (web configureTooltipGlossary(registries)).</summary>
        public TermGlossary Glossary(UiData ui) =>
            _glossary ?? (_glossary = TermGlossary.Build(ui.Terms, CombatData.Statuses.All, CombatData.Stances.All, ui.Strings));
    }
}
