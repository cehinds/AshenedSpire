using Ashen.Generated;
using UnityEngine.UIElements;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>
    /// The Exit door (web src/ui/quitGame.js mountQuitScreen): THE EMBER GUTTERS over "Closing Ashen Spire…" and Return to title. The game closes a moment later; where the host refuses (a browser tab, the editor) the line says
    /// the window was kept open and the way back to the title stays. There is no confirmation before it, as the web has none: title.js sends Exit straight here.
    /// </summary>
    public sealed class FarewellScreen : ScreenView
    {
        private IVisualElementScheduledItem _close;
        private IVisualElementScheduledItem _kept;

        protected override void OnBind(object args)
        {
            var status = Root.Q<LocLabel>(UiNames.FarewellStatus);
            status.stringKey = StringKeys.FarewellClosing;
            var back = Root.Q<LocButton>(UiNames.FarewellBack);
            back.clicked += () =>
            {
                _close?.Pause();
                _kept?.Pause();
                TitleScreen.ResetSession();
                Nav.Go(ScreenIds.Title, null, true);
            };
            var tokens = Ui.Data.Tokens;
            _close = Root.schedule.Execute(() =>
            {
                Ui.Quit();
                _kept = Root.schedule.Execute(() => status.stringKey = StringKeys.FarewellKept).StartingIn(tokens.Duration(TokenKeys.FarewellKept));
            }).StartingIn(tokens.Duration(TokenKeys.FarewellClose));
            Context.Instance.LastFocused = back;
        }

        public override void Unbind()
        {
            _close?.Pause();
            _kept?.Pause();
        }
    }
}
