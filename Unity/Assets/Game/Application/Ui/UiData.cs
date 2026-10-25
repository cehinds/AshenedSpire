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

        /// <summary>ui/cardface.json over the art lookup (VP-3): what a card's face draws besides its text.</summary>
        public CardFaceLookup CardFace;

        /// <summary>ui/feedback.json (VP-5): the numbers the combat feedback plays with.</summary>
        public FeedbackDefs Feedback;

        public static UiData Load(IContentSource source)
        {
            JObject Read(string path) => (JObject)JsonContent.Parse(source.ReadText(path));
            var art = new ArtLookup(ArtConfig.From(Read(ContentFiles.UiArt)), ArtRegistry.From(Read(ContentFiles.AssetsRegistry)));
            return new UiData
            {
                Screens = ScreenRegistry.From(Read(ContentFiles.UiScreens)),
                Menus = MenuSet.From(Read(ContentFiles.UiMenus)),
                Layout = LayoutRules.From(Read(ContentFiles.UiLayout)),
                Tokens = new UiTokens(Read(ContentFiles.UiTokens)),
                Components = ComponentDefaults.From(Read(ContentFiles.UiComponents)),
                Policies = ConfirmPolicies.From(Read(ContentFiles.UiConfirmationPolicies)),
                Strings = new StringTable(ProfileStrings(Read), Read(ContentFiles.StringsEn), Read(ContentFiles.StringsCombatEn), Read(ContentFiles.StringsAppEn), Read(ContentFiles.StringsShopEn), Read(ContentFiles.StringsEventsEn), Read(ContentFiles.StringsNodesEn), Read(ContentFiles.StringsMetaEn)),
                About = Read(ContentFiles.About),
                Nodes = Ashen.App.Nodes.NodeRules.From(Read(ContentFiles.UiNodes)),
                Meta = Read(ContentFiles.UiMeta),
                Art = art,
                CardFace = new CardFaceLookup(CardFaceConfig.From(Read(ContentFiles.UiCardface)), art),
                Feedback = FeedbackDefs.From(Read(ContentFiles.UiFeedback)),
            };
        }

        /// <summary>
        /// The display text of the Core 2 profile's rows as string keys (<c>&lt;prefix&gt;.&lt;row key&gt;.&lt;name&gt;</c>): rules/stringKeys.json names, per profile/bundle
        /// table, the prefix and which row field becomes which string (class names, attribute labels, unlock hints, ...). The rows keep their text inline (WP-21), so
        /// the string files layered over this table can still override a name.
        /// </summary>
        private static JObject ProfileStrings(System.Func<string, JObject> read)
        {
            var strings = new JObject();
            foreach (var table in read(RuleKeys.StringKeysFile).Properties())
            {
                var prefix = (string)table.Value[RuleKeys.Prefix];
                if (!(table.Value[RegistryKeys.StringKeyFields] is JObject fields) || !(read(table.Name) is JObject rows)) continue;
                foreach (var row in rows.Properties())
                    foreach (var field in fields.Properties())
                        if (row.Value[field.Name] is JValue text && text.Type == JTokenType.String)
                            strings[string.Join(ContentLayout.SchemaNameSeparator, prefix, row.Name, (string)field.Value)] = text.DeepClone();
            }
            return strings;
        }

        /// <summary>The menu context for the title (US-1.3): valid slots from the save service, built screens from the registry.</summary>
        public MenuContext MenuContext(bool hasValidSlot, bool inCombat = false) => new MenuContext { HasValidSlot = hasValidSlot, InCombat = inCombat, IsBuilt = Screens.IsBuilt };
    }
}
