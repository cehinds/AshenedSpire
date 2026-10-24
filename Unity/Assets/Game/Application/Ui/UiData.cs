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

        /// <summary>ui/nodes.json: the node screens' presentation data (W-09, W-11, W-13).</summary>
        public Ashen.App.Nodes.NodeRules Nodes;

        /// <summary>ui/meta.json: the meta lane's presentation data (the act map's reveal modes).</summary>
        public JObject Meta;

        /// <summary>ui/art.json over the asset registry (VP-2): finds a card's art, an icon or a scene by name.</summary>
        public ArtLookup Art;

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
                Strings = new StringTable(Read(ContentFiles.StringsEn), Read(ContentFiles.StringsCombatEn), Read(ContentFiles.StringsAppEn), Read(ContentFiles.StringsShopEn), Read(ContentFiles.StringsEventsEn), Read(ContentFiles.StringsNodesEn), Read(ContentFiles.StringsMetaEn)),
                About = Read(ContentFiles.About),
                Nodes = Ashen.App.Nodes.NodeRules.From(Read(ContentFiles.UiNodes)),
                Meta = Read(ContentFiles.UiMeta),
                Art = new ArtLookup(ArtConfig.From(Read(ContentFiles.UiArt)), ArtRegistry.From(Read(ContentFiles.AssetsRegistry))),
            };
        }

        /// <summary>The menu context for the title (US-1.3): valid slots from the save service, built screens from the registry.</summary>
        public MenuContext MenuContext(bool hasValidSlot, bool inCombat = false) => new MenuContext { HasValidSlot = hasValidSlot, InCombat = inCombat, IsBuilt = Screens.IsBuilt };
    }
}
