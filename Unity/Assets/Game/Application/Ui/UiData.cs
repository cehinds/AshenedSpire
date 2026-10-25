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

        /// <summary>The words and lists of a card's rules text (ui/cardText.json over strings/app.en.json cardtext.*): the card face generator (P-1).</summary>
        public Ashen.App.Combat.CardTextData CardText;

        /// <summary>ui/poses.json (P-1): the geometry of the player figure's pose frames and how long a pose is held.</summary>
        public PoseData Poses;

        /// <summary>ui/cardStage.json (P-3b): the player's combat figure, the web's card stage: its frames, actions and the arithmetic of their clips.</summary>
        public CardStageData CardStage;

        /// <summary>ui/enemyStage.json (P-4): the enemies' combat figure, the web's expansion stage: its frames, clips, stance frames and the rest and attack rules.</summary>
        public EnemyStageData EnemyStage;

        public static UiData Load(IContentSource source)
        {
            JObject Read(string path) => (JObject)JsonContent.Parse(source.ReadText(path));
            var strings = new StringTable(Read(ContentFiles.StringsEn), Read(ContentFiles.StringsCombatEn), Read(ContentFiles.StringsAppEn), Read(ContentFiles.StringsShopEn), Read(ContentFiles.StringsEventsEn), Read(ContentFiles.StringsNodesEn), Read(ContentFiles.StringsMetaEn));
            var art = new ArtLookup(ArtConfig.From(Read(ContentFiles.UiArt)), ArtRegistry.From(Read(ContentFiles.AssetsRegistry)));
            return new UiData
            {
                Screens = ScreenRegistry.From(Read(ContentFiles.UiScreens)),
                Menus = MenuSet.From(Read(ContentFiles.UiMenus)),
                Layout = LayoutRules.From(Read(ContentFiles.UiLayout)),
                Tokens = new UiTokens(Read(ContentFiles.UiTokens)),
                Components = ComponentDefaults.From(Read(ContentFiles.UiComponents)),
                Policies = ConfirmPolicies.From(Read(ContentFiles.UiConfirmationPolicies)),
                Strings = strings,
                CardText = new Ashen.App.Combat.CardTextData(strings, Read(ContentFiles.UiCardText)),
                Poses = new PoseData(Read(ContentFiles.UiPoses)),
                CardStage = new CardStageData(Read(ContentFiles.UiCardStage)),
                EnemyStage = new EnemyStageData(Read(ContentFiles.UiEnemyStage)),
                About = Read(ContentFiles.About),
                Nodes = Ashen.App.Nodes.NodeRules.From(Read(ContentFiles.UiNodes)),
                Meta = Read(ContentFiles.UiMeta),
                Art = art,
                CardFace = new CardFaceLookup(CardFaceConfig.From(Read(ContentFiles.UiCardface)), art),
                Feedback = FeedbackDefs.From(Read(ContentFiles.UiFeedback)),
            };
        }

        /// <summary>The menu context for the title (US-1.3): valid slots from the save service, built screens from the registry.</summary>
        public MenuContext MenuContext(bool hasValidSlot, bool inCombat = false) => new MenuContext { HasValidSlot = hasValidSlot, InCombat = inCombat, IsBuilt = Screens.IsBuilt };
    }
}
