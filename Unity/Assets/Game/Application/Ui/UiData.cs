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

        /// <summary>ui/controls.json (P-7a): the actions the Controls list rebinds, the pad buttons and the key names.</summary>
        public ControlsData Controls;
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

        /// <summary>ui/equipmentCard.json (P-3a): the poker-style equipment card's frame and the rows its regions are solved into.</summary>
        public EquipmentCardLayout EquipmentCard;

        /// <summary>ui/progression.json (P-3a): the Character Sheet's stat rows and what an attribute's card leads with.</summary>
        public ProgressionLayout Progression;

        /// <summary>ui/lore.json (P-3a): the lists and scales behind the Text &amp; lore settings.</summary>
        public LoreLayout Lore;

        /// <summary>ui/changelog.json (P-3a): CHANGELOG.md as Settings > Advanced > Changelog lists it.</summary>
        public ChangelogData Changelog;

        /// <summary>ui/terms.json (P-3a): the web's framework terms at the pin, the vocabulary the tooltip glossary explains.</summary>
        public JObject Terms;

        /// <summary>ui/poses.json (P-1): the geometry of the player figure's pose frames and how long a pose is held.</summary>
        public PoseData Poses;

        /// <summary>ui/cardStage.json (P-3b): the player's combat figure, the web's card stage: its frames, actions and the arithmetic of their clips.</summary>
        public CardStageData CardStage;

        /// <summary>ui/enemyStage.json (P-4): the enemies' combat figure, the web's expansion stage: its frames, clips, stance frames and the rest and attack rules.</summary>
        public EnemyStageData EnemyStage;

        /// <summary>ui/battlefield.json (P-4): the web's combat board layout: the perspective grid, the party's and the enemies' scale settings and the sprite fit's literals.</summary>
        public BattlefieldData Battlefield;

        /// <summary>ui/figureInk.json (P-4): the visible columns of each combat figure frame, which a floor shadow is as wide as.</summary>
        public FigureInk FigureInk;

        /// <summary>ui/combatAura.json (P-4): the colours and strengths of the player figure's silhouette glow.</summary>
        public CombatAuraData CombatAura;

        /// <summary>ui/outfitArt.json (P-5): the player's painted figure outside the combat board (the combatant door's portrait, the stand and conversation views) by class, armour and hands.</summary>
        public OutfitArtData OutfitArt;

        public static UiData Load(IContentSource source)
        {
            JObject Read(string path) => (JObject)JsonContent.Parse(source.ReadText(path));
            var strings = new StringTable(ProfileStrings(Read), Read(ContentFiles.StringsEn), Read(ContentFiles.StringsCombatEn), Read(ContentFiles.StringsAppEn), Read(ContentFiles.StringsShopEn), Read(ContentFiles.StringsEventsEn), Read(ContentFiles.StringsNodesEn), Read(ContentFiles.StringsMetaEn));
            var art = new ArtLookup(ArtConfig.From(Read(ContentFiles.UiArt)), ArtRegistry.From(Read(ContentFiles.AssetsRegistry)));
            return new UiData
            {
                Screens = ScreenRegistry.From(Read(ContentFiles.UiScreens)),
                Menus = MenuSet.From(Read(ContentFiles.UiMenus)),
                Controls = ControlsData.From(Read(ContentFiles.UiControls)),
                Layout = LayoutRules.From(Read(ContentFiles.UiLayout)),
                Tokens = new UiTokens(Read(ContentFiles.UiTokens)),
                Components = ComponentDefaults.From(Read(ContentFiles.UiComponents)),
                Policies = ConfirmPolicies.From(Read(ContentFiles.UiConfirmationPolicies)),
                Strings = strings,
                CardText = new Ashen.App.Combat.CardTextData(strings, Read(ContentFiles.UiCardText)),
                Poses = new PoseData(Read(ContentFiles.UiPoses)),
                CardStage = new CardStageData(Read(ContentFiles.UiCardStage)),
                EnemyStage = new EnemyStageData(Read(ContentFiles.UiEnemyStage)),
                Battlefield = new BattlefieldData(Read(ContentFiles.UiBattlefield)),
                FigureInk = new FigureInk(Read(ContentFiles.UiFigureInk)),
                CombatAura = new CombatAuraData(Read(ContentFiles.UiCombatAura)),
                OutfitArt = new OutfitArtData(Read(ContentFiles.UiOutfitArt)),
                EquipmentCard = EquipmentCardLayout.From(Read(ContentFiles.UiEquipmentCard)),
                Progression = ProgressionLayout.From(Read(ContentFiles.UiProgression)),
                Lore = LoreLayout.From(Read(ContentFiles.UiLore)),
                Changelog = ChangelogData.From(Read(ContentFiles.UiChangelog)),
                Terms = Read(ContentFiles.UiTerms),
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
