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
                case ScreenIds.Prologue: return new PrologueScreen();
                case ScreenIds.Settings: return new SettingsScreen();
                case ScreenIds.Journal: return new JournalScreen();
                case ScreenIds.Compendium: return new CompendiumScreen();
                case ScreenIds.Profile: return new ProfileScreen();
                case ScreenIds.About: return new AboutScreen();
                case ScreenIds.Combat: return new CombatScreen();
                case ScreenIds.Core2Combat: return new Core2CombatScreen();
                case ScreenIds.Core2Rewards: return new Core2RewardsScreen();
                case ScreenIds.Core2Rest: return new Core2RestScreen();
                case ScreenIds.Core2Shop: return new Core2ShopScreen();
                case ScreenIds.Core2Event: return new Core2EventScreen();
                case ScreenIds.Core2RunEnd: return new Core2RunEndScreen();
                case ScreenIds.Pause: return new PauseScreen();
                case ScreenIds.Armoury: return new ArmouryScreen();
                case ScreenIds.Rewards: return new RewardsScreen();
                case ScreenIds.ActMap: return new ActMapScreen();
                case ScreenIds.Rest: return new RestScreen();
                case ScreenIds.RunEnd: return new RunEndScreen();
                case ScreenIds.Merchant: return new MerchantScreen();
                case ScreenIds.Event:
                case ScreenIds.Dialogue: return new EventScreen();
                case ScreenIds.LegacyDungeon: return new DungeonScreen();
                default: return new PlaceholderScreen();
            }
        }

        private sealed class PlaceholderScreen : ScreenView
        {
            protected override void OnBind(object args) { }
        }
    }
}
