using Ashen.Content;
using Ashen.Generated;
using Newtonsoft.Json.Linq;

namespace Ashen.App.Ui
{
    /// <summary>Everything the UI reads from content, parsed once (docs/design/08 §9–§10). Engine-free, so it is shared with the tests.</summary>
    public sealed class UiData
    {
        public ScreenRegistry Screens;
        public MenuSet Menus;
        public LayoutRules Layout;
        public UiTokens Tokens;
        public ComponentDefaults Components;
        public ConfirmPolicies Policies;
        public StringTable Strings;
        public JObject About;

        public static UiData Load(IContentSource source)
        {
            JObject Read(string path) => (JObject)JsonContent.Parse(source.ReadText(path));
            return new UiData
            {
                Screens = ScreenRegistry.From(Read(ContentFiles.UiScreens)),
                Menus = MenuSet.From(Read(ContentFiles.UiMenus)),
                Layout = LayoutRules.From(Read(ContentFiles.UiLayout)),
                Tokens = new UiTokens(Read(ContentFiles.UiTokens)),
                Components = ComponentDefaults.From(Read(ContentFiles.UiComponents)),
                Policies = ConfirmPolicies.From(Read(ContentFiles.UiConfirmationPolicies)),
                Strings = new StringTable(Read(ContentFiles.StringsEn), Read(ContentFiles.StringsAppEn)),
                About = Read(ContentFiles.About),
            };
        }

        /// <summary>The menu context for the title (US-1.3): valid slots from the save service, built screens from the registry.</summary>
        public MenuContext MenuContext(bool hasValidSlot) => new MenuContext { HasValidSlot = hasValidSlot, IsBuilt = Screens.IsBuilt };
    }
}
