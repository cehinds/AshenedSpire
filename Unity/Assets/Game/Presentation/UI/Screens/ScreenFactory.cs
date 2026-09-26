using Ashen.Generated;

namespace Ashen.Presentation.UI.Screens
{
    /// <summary>Screen id (ui/screens.json) → view. Code decides how a screen behaves; data decides which screens exist and how they connect.</summary>
    public static class ScreenFactory
    {
        public static IScreenView Create(string id)
        {
            switch (id)
            {
                case ScreenIds.Boot: return new BootScreen();
                case ScreenIds.ContentError: return new ContentErrorScreen();
                case ScreenIds.ProfileRecovery: return new ProfileRecoveryScreen();
                case ScreenIds.Title: return new TitleScreen();
                case ScreenIds.Slots: return new SlotsScreen();
                case ScreenIds.Confirm: return new ConfirmScreen();
                case ScreenIds.KitGallery: return new KitGalleryScreen();
                case ScreenIds.Creation: return new CreationScreen();
                case ScreenIds.Combat: return new CombatScreen();
                case ScreenIds.Pause: return new PauseScreen();
                default: return new PlaceholderScreen();
            }
        }

        private sealed class PlaceholderScreen : ScreenView
        {
            protected override void OnBind(object args) { }
        }
    }
}
